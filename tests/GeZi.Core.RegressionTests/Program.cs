using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Download;

internal static class Program
{
    private const int PartSize = 256 * 1024;

    private static int Main()
    {
        int failures = 0;
        foreach (var test in new Func<Task>[]
        {
            UncommittedTailMustNotBeMerged,
            ResumeMustDiscardUncommittedMergedTail,
            RetryMustOverwriteUncommittedTail,
            PauseMustPreserveCommittedProgress,
            ReconciliationFailureMustPreserveResumeState,
            FreshSegmentedDownloadMustMatch
        })
        {
            try
            {
                test().GetAwaiter().GetResult();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine("FAIL " + test.Method.Name + ": " + ex.Message);
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static async Task UncommittedTailMustNotBeMerged()
    {
        using (var fixture = new DownloadFixture())
        {
            int reports = 0;
            long observed = -1;
            using (var server = new RangeServer(fixture.Data, async (start, end) =>
            {
                if (start != 1)
                    return;
                // Model net48 BeginWriteCore's SetLength-before-WriteFile window.
                Extend(fixture.Part(0), PartSize);
                int baseline = Volatile.Read(ref reports);
                await WaitUntil(() => Volatile.Read(ref reports) >= baseline + 3, fixture.Cancel);
                observed = new FileInfo(fixture.Dest).Length;
            }))
            using (var downloader = new SegmentedDownloader(threads: 2))
            {
                var result = await downloader.DownloadAsync(server.Url, fixture.Dest,
                    new CallbackProgress(_ => Interlocked.Increment(ref reports)), fixture.Cancel);
                Assert(result.Ok, result.Message);
                Console.WriteLine("  in-flight target length=" + observed + "; result.Ok=" + result.Ok);
                AssertBytes(fixture.Data, File.ReadAllBytes(fixture.Dest));
                Assert(observed <= 1, "Merger read beyond successfully written bytes: " + observed);
            }
        }
    }

    private static async Task ResumeMustDiscardUncommittedMergedTail()
    {
        using (var fixture = new DownloadFixture())
        {
            Extend(fixture.Part(0), PartSize / 2);
            File.WriteAllBytes(fixture.Dest, new[] { fixture.Data[0] });
            Extend(fixture.Dest, PartSize / 2);
            using (var server = new RangeServer(fixture.Data))
            using (var downloader = new SegmentedDownloader(threads: 2))
            {
                var result = await downloader.DownloadAsync(server.Url, fixture.Dest, cancel: fixture.Cancel);
                Assert(result.Ok, result.Message);
                Console.WriteLine("  resume result.Ok=" + result.Ok);
                AssertBytes(fixture.Data, File.ReadAllBytes(fixture.Dest));
            }
        }
    }

    private static async Task RetryMustOverwriteUncommittedTail()
    {
        using (var fixture = new DownloadFixture())
        {
            int tailRequests = 0;
            long retryStart = -1;
            using (var server = new RangeServer(fixture.Data, async (start, end) =>
            {
                if (start == 1)
                {
                    await WaitUntil(() => TailComplete(fixture), fixture.Cancel);
                }
                else if (start >= PartSize && Interlocked.Increment(ref tailRequests) == 2)
                {
                    retryStart = start;
                    // Model the uncommitted extension left by an interrupted disk write.
                    Extend(fixture.Part(1), new FileInfo(fixture.Part(1)).Length + 32 * 1024);
                }
            }, truncate: (start, end) => start == PartSize + 1))
            using (var downloader = new SegmentedDownloader(threads: 2))
            {
                var result = await downloader.DownloadAsync(server.Url, fixture.Dest, cancel: fixture.Cancel);
                Assert(result.Ok, result.Message);
                Assert(tailRequests == 2 && retryStart == PartSize + 1 + 64 * 1024,
                    "Expected an HTTP retry at the committed offset, got " + retryStart);
                Console.WriteLine("  retry offset=" + retryStart + "; result.Ok=" + result.Ok);
                AssertBytes(fixture.Data, File.ReadAllBytes(fixture.Dest));
            }
        }
    }

    private static async Task PauseMustPreserveCommittedProgress()
    {
        using (var fixture = new DownloadFixture())
        {
            var pause = new PauseToken();
            int paused = 0, reports = 0;
            long done = 0;
            using (var server = new RangeServer(fixture.Data, (start, end) =>
            {
                if (start == 1)
                {
                    pause.Pause();
                    Volatile.Write(ref paused, 1);
                }
                return Task.CompletedTask;
            }))
            using (var downloader = new SegmentedDownloader(threads: 2))
            {
                Task<DownloadResult> download = downloader.DownloadAsync(server.Url, fixture.Dest,
                    new CallbackProgress(p =>
                    {
                        Interlocked.Exchange(ref done, p.Done);
                        Interlocked.Increment(ref reports);
                    }), fixture.Cancel, pause);
                try
                {
                    await WaitUntil(() => Volatile.Read(ref paused) != 0
                        && Volatile.Read(ref reports) >= 3, fixture.Cancel);
                    Assert(Interlocked.Read(ref done) == 2, "Progress advanced while paused.");
                    Assert(new FileInfo(fixture.Part(0)).Length == 1, "Worker wrote while paused.");
                }
                finally
                {
                    pause.Resume();
                }
                var result = await download;
                Assert(result.Ok, result.Message);
                AssertBytes(fixture.Data, File.ReadAllBytes(fixture.Dest));
            }
        }
    }

    private static async Task ReconciliationFailureMustPreserveResumeState()
    {
        using (var fixture = new DownloadFixture())
        using (var server = new RangeServer(fixture.Data))
        using (var downloader = new SegmentedDownloader(threads: 2))
        {
            Extend(fixture.Part(0), PartSize / 2);
            File.WriteAllBytes(fixture.Dest, new[] { fixture.Data[0] });
            Extend(fixture.Dest, PartSize / 2);
            byte[] original = File.ReadAllBytes(fixture.Dest);
            using (var locked = new FileStream(fixture.Dest, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = await downloader.DownloadAsync(server.Url, fixture.Dest, cancel: fixture.Cancel);
                Assert(!failed.Ok && failed.Message.Contains("未提交尾部"),
                    "Expected an explicit reconciliation failure: " + failed.Message);
                Assert(File.Exists(fixture.Dest + ".qmeta"), "Failure discarded resume metadata.");
                AssertBytes(original, File.ReadAllBytes(fixture.Dest));
            }
            var resumed = await downloader.DownloadAsync(server.Url, fixture.Dest, cancel: fixture.Cancel);
            Assert(resumed.Ok, resumed.Message);
            AssertBytes(fixture.Data, File.ReadAllBytes(fixture.Dest));
        }
    }

    private static async Task FreshSegmentedDownloadMustMatch()
    {
        using (var fixture = new DownloadFixture())
        {
            File.Delete(fixture.Dest + ".qmeta");
            File.Delete(fixture.Part(0));
            File.Delete(fixture.Part(1));
            byte[] data = Enumerable.Range(0, 24 * 1024 * 1024).Select(i => (byte)(1 + i % 251)).ToArray();
            using (var server = new RangeServer(data))
            using (var downloader = new SegmentedDownloader(threads: 4))
            {
                var result = await downloader.DownloadAsync(server.Url, fixture.Dest, cancel: fixture.Cancel);
                Assert(result.Ok, result.Message);
                AssertBytes(data, File.ReadAllBytes(fixture.Dest));
                Assert(!File.Exists(fixture.Dest + ".qmeta"), "Successful download retained metadata.");
                Assert(!Directory.Exists(fixture.Dest + ".qparts"), "Successful download retained parts.");
            }
        }
    }

    private static bool TailComplete(DownloadFixture fixture)
    {
        string[] lines = File.ReadAllLines(fixture.Dest + ".qmeta");
        return lines.Length >= 4 && lines[3].EndsWith("," + PartSize, StringComparison.Ordinal);
    }

    private static void Extend(string path, long length)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete))
            stream.SetLength(length);
    }

    private static async Task WaitUntil(Func<bool> predicate, CancellationToken cancel)
    {
        while (!predicate())
            await Task.Delay(20, cancel);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertBytes(byte[] expected, byte[] actual)
    {
        Assert(expected.Length == actual.Length,
            "Length mismatch: expected " + expected.Length + ", actual " + actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
                throw new InvalidOperationException("Byte mismatch at " + i + ": expected "
                    + expected[i] + ", actual " + actual[i]);
        }
    }

    private sealed class CallbackProgress : IProgress<DownloadProgress>
    {
        private readonly Action<DownloadProgress> _report;
        public CallbackProgress(Action<DownloadProgress> report) { _report = report; }
        public void Report(DownloadProgress value) => _report(value);
    }

    private sealed class DownloadFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "GeZi-regression-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource _deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        public readonly byte[] Data = Enumerable.Range(0, PartSize * 2).Select(i => (byte)(1 + i % 251)).ToArray();
        public string Dest => Path.Combine(_root, "synthetic.bin");
        public CancellationToken Cancel => _deadline.Token;
        public string Part(int index) => Path.Combine(Dest + ".qparts", "p" + index.ToString("D6", CultureInfo.InvariantCulture));

        public DownloadFixture()
        {
            Directory.CreateDirectory(Dest + ".qparts");
            File.WriteAllBytes(Part(0), new[] { Data[0] });
            File.WriteAllBytes(Part(1), new[] { Data[PartSize] });
            File.WriteAllText(Dest + ".qmeta", string.Format(CultureInfo.InvariantCulture,
                "{0}\n2\n0,{1},1\n{2},{3},1\n", Data.Length, PartSize - 1, PartSize, Data.Length - 1));
        }

        public void Dispose()
        {
            _deadline.Cancel();
            _deadline.Dispose();
            Directory.Delete(_root, true);
        }
    }

    private sealed class RangeServer : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly ConcurrentQueue<Exception> _errors = new ConcurrentQueue<Exception>();
        private readonly ConcurrentBag<Task> _requests = new ConcurrentBag<Task>();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly Task _accept;
        public string Url { get; }

        public RangeServer(byte[] data, Func<long, long, Task> beforeResponse = null,
            Func<long, long, bool> truncate = null)
        {
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/synthetic";
            _accept = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        TcpClient client = await _listener.AcceptTcpClientAsync();
                        _requests.Add(Serve(client, data, beforeResponse, truncate));
                    }
                }
                catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
                catch (SocketException) when (_stop.IsCancellationRequested) { }
            });
        }

        private async Task Serve(TcpClient client, byte[] data, Func<long, long, Task> beforeResponse,
            Func<long, long, bool> truncate)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true))
                {
                    string request = await reader.ReadLineAsync();
                    long start = 0, end = data.Length - 1;
                    bool ranged = false;
                    string line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    {
                        if (!line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                            continue;
                        string[] range = line.Substring("Range: bytes=".Length).Split('-');
                        start = long.Parse(range[0], CultureInfo.InvariantCulture);
                        if (range[1].Length > 0)
                            end = long.Parse(range[1], CultureInfo.InvariantCulture);
                        ranged = true;
                    }
                    bool head = request.StartsWith("HEAD ", StringComparison.Ordinal);
                    bool probe = head || (start == 0 && end == 0);
                    if (!probe && beforeResponse != null)
                        await beforeResponse(start, end);
                    string headers = "HTTP/1.1 " + (ranged ? "206 Partial Content" : "200 OK")
                        + "\r\nAccept-Ranges: bytes\r\nContent-Length: " + (end - start + 1)
                        + (ranged ? "\r\nContent-Range: bytes " + start + "-" + end + "/" + data.Length : "")
                        + "\r\nConnection: close\r\n\r\n";
                    byte[] encoded = Encoding.ASCII.GetBytes(headers);
                    await stream.WriteAsync(encoded, 0, encoded.Length);
                    if (!head)
                    {
                        int count = !probe && truncate != null && truncate(start, end)
                            ? 64 * 1024 : (int)(end - start + 1);
                        await stream.WriteAsync(data, (int)start, count);
                    }
                }
            }
            catch (Exception ex)
            {
                _errors.Enqueue(ex);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _accept.GetAwaiter().GetResult();
            Task.WhenAll(_requests.ToArray()).GetAwaiter().GetResult();
            _stop.Dispose();
            if (!_errors.IsEmpty)
                throw new AggregateException(_errors);
        }
    }
}
