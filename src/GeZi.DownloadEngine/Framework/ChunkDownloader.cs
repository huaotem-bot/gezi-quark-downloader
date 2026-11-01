using GeZi.DownloadEngine.Framework.Extensions.Helpers;
using GeZi.DownloadEngine.Framework.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using GeZi.DownloadEngine.Validation;
using GeZi.DownloadEngine.Transport;
using GeZi.DownloadEngine.Storage;

namespace GeZi.DownloadEngine.Framework;

internal class ChunkDownloader
{
    private readonly ILogger _logger;
    private readonly DownloadConfiguration _configuration;
    private readonly int _timeoutIncrement = 10;
    private ThrottledStream _sourceStream;
    private IChunkStorage _storage;
    internal Chunk Chunk { get; set; }

    public event EventHandler<DownloadProgressChangedEventArgs> DownloadProgressChanged;

    private readonly IChunkTransport _transport;
    private readonly IContentValidator _validator;
    private readonly string _validatorValue;

    public ChunkDownloader(Chunk chunk, DownloadConfiguration config, IChunkStorage storage,
        ILogger logger = null, IChunkTransport transport = null,
        IContentValidator validator = null, string validatorValue = null)
    {
        Chunk = chunk;
        _configuration = config;
        _storage = storage;
        _logger = logger;
        _transport = transport ?? config?.Transport ?? HttpChunkTransport.Instance;
        _validator = validator ?? config?.Validator ?? IfRangeValidator.Instance;
        _validatorValue = validatorValue ?? "";
        _configuration.PropertyChanged += ConfigurationPropertyChanged;
    }

    private void ConfigurationPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        _logger?.LogDebug($"Changed configuration {e.PropertyName} property");
        if (e.PropertyName == nameof(_configuration.MaximumBytesPerSecond) &&
            _sourceStream?.CanRead == true)
        {
            _sourceStream.BandwidthLimit = _configuration.MaximumSpeedPerChunk;
        }
    }

    public async Task<Chunk> Download(Request downloadRequest, PauseToken pause, CancellationToken cancelToken)
    {
        try
        {
            _logger?.LogDebug($"Starting download the chunk {Chunk.Id}");
            await DownloadChunk(downloadRequest, pause, cancelToken).ConfigureAwait(false);
            return Chunk;
        }
        catch (FatalChunkException)
        {
            // 资源变更 / 区间不符 —— 重试无意义，直接上抛让任务失败并作废
            _logger?.LogCritical($"Fatal chunk error on chunk {Chunk.Id}: resource changed or range mismatch");
            throw;
        }
        catch (TaskCanceledException error) // when stream reader timeout occurred 
        {
            // 取消和「读超时」抛的是同一种异常，必须分开：
            // 以前不判 cancelToken，用户点暂停/取消时这一片会被当成超时去重试
            // （还要吃 150ms~5s 的指数退避 + 重新握手），表现就是「点了取消还在跑」。
            if (cancelToken.IsCancellationRequested) throw;
            _logger?.LogError(error, $"Task Canceled on download chunk {Chunk.Id} with retry");
            return await ContinueWithDelay(downloadRequest, pause, cancelToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException error) // when stream reader cancel/timeout occurred 
        {
            // 同上：取消时 cancelToken 注册的 stream.Close 也会抛这个。
            if (cancelToken.IsCancellationRequested) throw;
            _logger?.LogError(error, $"Disposed object error on download chunk {Chunk.Id} with retry");
            return await ContinueWithDelay(downloadRequest, pause, cancelToken).ConfigureAwait(false);
        }
        catch (Exception error) when (Chunk.CanTryAgainOnFailover() && error.IsMomentumError())
        {
            _logger?.LogError(error, $"Error on download chunk {Chunk.Id} with retry");
            return await ContinueWithDelay(downloadRequest, pause, cancelToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Can't handle this exception
            _logger?.LogCritical(error, $"Fatal error on download chunk {Chunk.Id}");
            throw;
        }
        finally
        {
            _logger?.LogDebug($"Exit from download method of the chunk {Chunk.Id}");
            await Task.Yield();
        }
    }

    private async Task<Chunk> ContinueWithDelay(Request request, PauseToken pause, CancellationToken cancelToken)
    {
        _logger?.LogDebug($"ContinueWithDelay of the chunk {Chunk.Id}");
        await request.ThrowIfIsNotSupportDownloadInRange().ConfigureAwait(false);

        // 🚨 重试间隔必须是指数退避，不能直接等 Chunk.Timeout（2026-10-24 实测）。
        // 原实现 Task.Delay(Chunk.Timeout)，而 Chunk.Timeout 初始就是 15 秒 ——
        // 一旦 CDN 限流（迅雷 32 并发时大面积返回 HTTP 503），每个失败分片都要
        // 干等 15 秒才重试，于是并发越高越慢，吞吐**反向崩塌**：
        //   16 线程 5.0 MB/s → 32 线程 1.74 MB/s（自研引擎同期稳定 9.7~10.4 MB/s）。
        // 改成 150ms 起、每次翻倍、封顶 5 秒后，同样的 503 只是短暂让路而不是卡死。
        int attempt = Chunk.FailoverCount;
        if (attempt < 0) attempt = 0;
        if (attempt > 10) attempt = 10;

        long delay = _configuration.RetryBaseDelayMs;
        for (int i = 0; i < attempt; i++)
        {
            delay *= 2;
            if (delay >= _configuration.RetryMaxDelayMs)
            {
                delay = _configuration.RetryMaxDelayMs;
                break;
            }
        }
        if (delay > _configuration.RetryMaxDelayMs)
            delay = _configuration.RetryMaxDelayMs;

        // 退避期间仍然响应取消，避免卡在等待里退不出去
        await Task.Delay((int)delay, cancelToken).ConfigureAwait(false);

        // 读超时继续放大，减少「正常但慢」的连接被误判为超时
        Chunk.Timeout += _timeoutIncrement;
        // re-request and continue downloading
        return await Download(request, pause, cancelToken).ConfigureAwait(false);
    }

    private async Task DownloadChunk(Request downloadRequest, PauseToken pauseToken, CancellationToken cancelToken)
    {
        cancelToken.ThrowIfCancellationRequested();
        _logger?.LogDebug($"开始下载分片 {Chunk.Id}");
        if (Chunk.IsDownloadCompleted() == false)
        {
            // 取流走 IChunkTransport：默认 SChannel（HttpWebRequest），
            // UC 换成 OpenSSL —— 它的 CDN 会按 TLS ClientHello 指纹拒绝 SChannel。
            ChunkRequest chunkRequest = BuildChunkRequest(downloadRequest);
            using (ChunkResponse response = await _transport.OpenAsync(chunkRequest, cancelToken).ConfigureAwait(false))
            {
                if (response.StatusCode == 200 || response.StatusCode == 206 ||
                    response.StatusCode == 201 || response.StatusCode == 202 ||
                    response.StatusCode == 205)
                {
                    ChunkGuardResult guard = _validator.GuardChunk(chunkRequest, response);
                    if (guard == ChunkGuardResult.ResourceChanged)
                    {
                        throw new FatalChunkException(
                            $"资源在下载期间发生变化（分片 {Chunk.Id} 请求区间，服务端回了 200 而非 206）。" +
                            "为避免产出损坏文件，本次下载作废。");
                    }

                    if (guard == ChunkGuardResult.StartMismatch)
                    {
                        throw new FatalChunkException(
                            $"服务端返回的区间起点不符（分片 {Chunk.Id} 请求 {chunkRequest.Start}，" +
                            $"服务端 {response.ContentRangeStart}）。为避免写错位，本次下载作废。");
                    }

                    _logger?.LogDebug($"分片 {Chunk.Id} 已连接：HTTP {response.StatusCode}，通道 {_transport.Name}");
                    if (response.Stream != null)
                    {
                        using (_sourceStream = new ThrottledStream(response.Stream, _configuration.MaximumSpeedPerChunk))
                        {
                            await ReadStream(_sourceStream, pauseToken, cancelToken).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    throw new WebException($"Download response status of the chunk {Chunk.Id} was {response.StatusCode}");
                }
            }
        }
    }

    /// <summary>
    /// 把 bezzad 的 Request + 当前分片状态翻译成一次取流请求。
    /// Range 语义与原来的 SetRequestRange 保持一致。
    /// </summary>
    private ChunkRequest BuildChunkRequest(Request downloadRequest)
    {
        var chunkRequest = new ChunkRequest();
        chunkRequest.Url = downloadRequest.Address.AbsoluteUri;

        var startOffset = Chunk.Start + Chunk.Position;
        chunkRequest.Start = startOffset;

        // 与原 SetRequestRange 一致：只有「多片 / 已读了一部分 / 显式指定区间」才限制上界
        bool limitRange = Chunk.End > 0 &&
            (_configuration.ChunkCount > 1 || Chunk.Position > 0 || _configuration.RangeDownload);
        if (limitRange && startOffset < Chunk.End)
            chunkRequest.End = Chunk.End;
        else
            chunkRequest.End = -1;

        chunkRequest.TimeoutMs = _configuration.RequestConfiguration != null
            ? _configuration.RequestConfiguration.Timeout
            : 30000;

        RequestConfiguration cfg = downloadRequest.Configuration;
        if (cfg != null)
        {
            chunkRequest.UserAgent = cfg.UserAgent;
            chunkRequest.Referer = cfg.Referer;

            if (cfg.Headers != null)
            {
                chunkRequest.Cookie = cfg.Headers["Cookie"];
                chunkRequest.Origin = cfg.Headers["Origin"];
                chunkRequest.IfRange = cfg.Headers["If-Range"];

                var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string key in cfg.Headers.AllKeys)
                {
                    if (string.IsNullOrEmpty(key)) continue;
                    if (string.Equals(key, "Cookie", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(key, "Origin", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(key, "If-Range", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(key, "Range", StringComparison.OrdinalIgnoreCase)) continue;
                    extra[key] = cfg.Headers[key];
                }
                if (extra.Count > 0) chunkRequest.ExtraHeaders = extra;
            }
        }

        // 探测阶段捕获的强 ETag / Last-Modified 在这里补上 If-Range；
        // 调用方显式指定过 Headers["If-Range"] 时 Guard 不会覆盖。
        _validator.Guard(chunkRequest, _validatorValue);

        return chunkRequest;
    }

    internal async Task ReadStream(Stream stream, PauseToken pauseToken, CancellationToken cancelToken)
    {
        int readSize = 1;
        CancellationToken? innerToken = null;

        // 慢连接看门狗（aria2 --lowest-speed-limit 的等价物）。
        // 已有的 Chunk.Timeout 只抓「完全卡住」，抓不到「一直很慢但没停」的连接 ——
        // 一条 20 KB/s 的连接读满 64 KB 只要 3.2 秒，永远碰不到 15 秒读超时。
        long lowestSpeed = _configuration.LowestSpeedLimit;
        int lowestSpeedSeconds = _configuration.LowestSpeedLimitSeconds;
        bool speedWatchEnabled = lowestSpeed > 0 && _configuration.MaxSpeedReconnects > 0;
        long speedWindowStartTicks = DateTime.UtcNow.Ticks;
        long speedWindowBytes = 0;

        try
        {
            // close stream on cancellation because, it's not work on .Net Framework
            using var _ = cancelToken.Register(stream.Close);
            while (readSize > 0 && Chunk.CanWrite)
            {
                cancelToken.ThrowIfCancellationRequested();
                await pauseToken.WaitWhilePausedAsync().ConfigureAwait(false);
                byte[] buffer = new byte[_configuration.BufferBlockSize];
                using var innerCts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
                innerToken = innerCts.Token;
                innerCts.CancelAfter(Chunk.Timeout);
                using (innerToken.Value.Register(stream.Close))
                {
                    // if innerToken timeout occurs, close the stream just during the reading stream
                    readSize = await stream.ReadAsync(buffer, 0, buffer.Length, innerToken.Value).ConfigureAwait(false);
                }

                readSize = (int)Math.Min(Chunk.EmptyLength, readSize);
                if (readSize > 0)
                {
                    await _storage.WriteAsync(Chunk.Start + Chunk.Position - _configuration.RangeLow, buffer, readSize).ConfigureAwait(false);
                    Chunk.Position += readSize;

                    OnDownloadProgressChanged(new DownloadProgressChangedEventArgs(Chunk.Id) {
                        TotalBytesToReceive = Chunk.Length,
                        ReceivedBytesSize = Chunk.Position,
                        ProgressedByteSize = readSize,
                        ReceivedBytes = buffer.Take(readSize).ToArray()
                    });

                    if (speedWatchEnabled)
                    {
                        speedWindowBytes += readSize;
                        long nowTicks = DateTime.UtcNow.Ticks;
                        long elapsedMs = (nowTicks - speedWindowStartTicks) / TimeSpan.TicksPerMillisecond;
                        if (elapsedMs >= lowestSpeedSeconds * 1000L)
                        {
                            double bps = speedWindowBytes * 1000.0 / elapsedMs;
                            // 剩余量太少就不值得重连：重连要付 TCP+TLS 握手和
                            // 重发请求头的代价（实测 200~600ms）。
                            // 🚨 判据必须按「时间」而不是「字节数」：
                            // 固定 2MB 门槛会把 1MB 的小分片全部排除掉（8MB 文件切 8 片
                            // 就是 1MB/片），自适应分片器在网盘场景下每片常只有几百 KB 到几 MB。
                            // 正确逻辑：按当前速度还要下 2 秒以上，重连才划算。
                            bool worthReconnect = bps > 0 && (Chunk.EmptyLength / bps) > 2.0;
                            if (bps < lowestSpeed && worthReconnect &&
                                Chunk.SpeedReconnectCount < _configuration.MaxSpeedReconnects &&
                                SpeedReconnectGovernor.TryAcquire())
                            {
                                Chunk.SpeedReconnectCount++;
                                _logger?.LogWarning(
                                    $"分片 {Chunk.Id} 速度 {bps:F0} B/s 低于下限 {lowestSpeed} B/s，" +
                                    $"主动断开重连（第 {Chunk.SpeedReconnectCount}/{_configuration.MaxSpeedReconnects} 次），" +
                                    $"已下 {Chunk.Position}/{Chunk.Length}，剩余 {Chunk.EmptyLength}");
                                // 关流后抛 TaskCanceledException，命中 Download 里既有的
                                // catch (TaskCanceledException) → ContinueWithDelay 分支，
                                // 从而复用指数退避、FailoverCount 和 Range 续传逻辑。
                                stream.Close();
                                throw new TaskCanceledException(
                                    $"ReadAsync of the chunk {Chunk.Id} stream was too slow ({bps:F0} B/s) and has been reconnected");
                            }

                            // 无论判不判慢，都开新窗口继续观察
                            speedWindowStartTicks = nowTicks;
                            speedWindowBytes = 0;
                        }
                    }
                }
            }
        }
        catch (ObjectDisposedException exp) // When closing stream manually, ObjectDisposedException will be thrown
        {
            _logger?.LogError(exp, $"ReadAsync of the chunk {Chunk.Id} stream was canceled or closed forcibly from server");
            cancelToken.ThrowIfCancellationRequested();
            if (innerToken?.IsCancellationRequested == true)
            {
                _logger?.LogError(exp, $"ReadAsync of the chunk {Chunk.Id} stream has been timed out");
                throw new TaskCanceledException($"ReadAsync of the chunk {Chunk.Id} stream has been timed out", exp);
            }

            throw; // throw origin stack trace of exception 
        }

        _logger?.LogDebug($"ReadStream of the chunk {Chunk.Id} completed successfully");
    }

    private void OnDownloadProgressChanged(DownloadProgressChangedEventArgs e)
    {
        DownloadProgressChanged?.Invoke(this, e);
    }
}
