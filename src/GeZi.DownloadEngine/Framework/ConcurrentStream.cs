using GeZi.DownloadEngine.Framework.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.DownloadEngine.Framework;

public class ConcurrentStream : TaskStateManagement, IDisposable
{
    private ConcurrentPacketBuffer<Packet> _inputBuffer;
    private volatile bool _disposed;
    private Stream _stream;
    private string _path;
    private CancellationTokenSource _watcherCancelSource;

    /// <summary>
    /// 保护 <see cref="_stream"/> 的「定位 + 写」与「Flush」互斥。
    /// FileStream 明确不是线程安全的，而 Watcher 线程写盘、续传保存线程刷盘是并发的。
    /// </summary>
    private readonly object _streamLock = new object();

    public string Path
    {
        get => _path;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _path = value;
                _stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            }
        }
    }
    public byte[] Data
    {
        get
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(_stream));

            if (_stream is MemoryStream mem)
                return mem.ToArray();

            return null;
        }
        set
        {
            if (value != null)
            {
                // Don't pass straight value to MemoryStream,
                // because causes stream to be an immutable array
                _stream = new MemoryStream();
                _stream.Write(value, 0, value.Length);
            }
        }
    }
    public bool CanRead => _stream?.CanRead == true;
    public bool CanSeek => _stream?.CanSeek == true;
    public bool CanWrite => _stream?.CanWrite == true;
    public long Length => _stream?.Length ?? 0;
    public long Position
    {
        get => _stream?.Position ?? 0;
        set => _stream.Position = value;
    }
    public long MaxMemoryBufferBytes
    {
        get => _inputBuffer.BufferSize;
        set => _inputBuffer.BufferSize = value;
    }

    // parameterless constructor for deserialization
    public ConcurrentStream() : this(0, null) { }

    public ConcurrentStream(ILogger logger) : this(0, logger) { }

    public ConcurrentStream(long maxMemoryBufferBytes = 0, ILogger logger = null) : base(logger)
    {
        _stream = new MemoryStream();
        Initial(maxMemoryBufferBytes);
    }

    public ConcurrentStream(Stream stream, long maxMemoryBufferBytes = 0, ILogger logger = null) : base(logger)
    {
        _stream = stream;
        Initial(maxMemoryBufferBytes);
    }

    public ConcurrentStream(string filename, long initSize, long maxMemoryBufferBytes = 0, ILogger logger = null) : base(logger)
    {
        _path = filename;
        // 🚨 大缓冲 + RandomAccess 是必须的（2026-10-24 实测）。
        // 原实现用默认 4KB 缓冲、无 FileOptions：多个 worker 并发乱序写同一文件时，
        // 每一次 Seek 都会让 4KB 缓冲失效并触发一次真实磁盘/页缓存往返，
        // 于是吞吐随并发数**反向崩塌**：
        //   16 线程 → 5.0 MB/s，32 线程 → 1.73 MB/s（自研引擎同期稳定 10 MB/s）。
        // 1MB 缓冲让相邻分片的写合并成一次大写入；RandomAccess 提示系统这是随机偏移
        // 写、不要做顺序预读，减少无用 I/O。
        // 🚨 FileShare.Delete 不能省（项目红线）：否则下载中途取消时，
        // 目标文件被这个句柄占着，Delete 必失败 → 只能退化成重启后删除。
        _stream = new FileStream(filename, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.Read | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);

        if (initSize > 0)
            SetLength(initSize);

        Initial(maxMemoryBufferBytes);
    }

    private void Initial(long maxMemoryBufferBytes, ILogger logger = null)
    {
        _inputBuffer = new ConcurrentPacketBuffer<Packet>(maxMemoryBufferBytes, logger);
        _watcherCancelSource = new CancellationTokenSource();

        Task<Task> task = Task.Factory.StartNew(
            function: Watcher,
            cancellationToken: _watcherCancelSource.Token,
            creationOptions: TaskCreationOptions.LongRunning,
            scheduler: TaskScheduler.Default);

        task.Unwrap();
    }

    /// <summary>
    /// 打开一个**独立的只读流**，用于边下边看与内容校验。
    ///
    /// <para>
    /// 🚨 2026-10-24 修的真 bug：原实现是
    /// <c>Seek(0, SeekOrigin.Begin); return _stream;</c> —— 它返回的是写入端那**同一个**
    /// <c>_stream</c>，还顺手把它的 Position 归零。
    /// 而 Watcher 线程正用「<c>Seek(pos, Begin)</c> + <c>_stream.WriteAsync</c>」写盘，
    /// 两个线程共用同一个 Position：读者一把 Position 改成 0，
    /// Watcher 接下来那次写就会**落在文件开头**而不是目标偏移，
    /// 表现为「文件长度对、内容错一块」，且随机复现（取决于读写交错时机）。
    /// 边下边看时播放器就是这么读的，所以生产里一定会踩到。
    /// 实测：共写模式 3 次里坏 2 次，每次约一个 16KB packet。
    /// </para>
    /// </summary>
    public Stream OpenRead()
    {
        if (_stream is MemoryStream mem)
        {
            // 内存模式：给一份快照，读端不能去动 mem.Position
            return new MemoryStream(mem.ToArray(), false);
        }

        if (string.IsNullOrEmpty(_path))
        {
            // 外部传入的 Stream（无路径）：只能退回原行为，但至少不再改 Position
            return _stream;
        }

        // 文件模式：另开一个只读句柄，与写入句柄彻底隔离
        return new FileStream(_path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var stream = OpenRead();
        try
        {
            return stream.Read(buffer, offset, count);
        }
        finally
        {
            // 只关闭我们自己新开的句柄；_stream 归写入端所有，绝不能在这里 Dispose
            if (!ReferenceEquals(stream, _stream))
                stream.Dispose();
        }
    }

    public async Task WriteAsync(long position, byte[] bytes, int length, bool fireAndForget = true)
    {
        if (bytes.Length < length)
            throw new ArgumentOutOfRangeException(nameof(length));

        if (IsFaulted && Exception is not null)
            throw Exception;

        bool added = await _inputBuffer.TryAdd(new Packet(position, bytes, length));
        if (!added)
        {
            // 以前这里直接忽略返回值：包没进缓冲区也没人知道，
            // 最后表现成「进度 100%、长度对、内容缺一截」，而且 errors 里干干净净。
            throw new IOException(
                "写入缓冲区失败：数据包被丢弃（缓冲已关闭或已停止接收）。继续下载会造成文件内容缺失，已中止。");
        }

        if (fireAndForget == false)
        {
            // to ensure that the written packet is actually stored on the stream
            await FlushAsync().ConfigureAwait(false);
        }
    }

    private async Task Watcher()
    {
        // 批量消费缓冲区：一次最多攒 BatchMaxCount 个 packet / BatchMaxBytes 字节，
        // 然后按偏移排序、把相邻的合并成一次大写入。
        var batch = new List<Packet>(_batchMaxCount);
        try
        {
            StartState();
            while (!_watcherCancelSource.IsCancellationRequested)
            {
                // 🚨 2026-10-28 新增：暂停时**不要把已缓冲的数据继续落盘**。
                // 原来 Pause() 只让 worker 停止从网络读，而 Watcher 线程毫不知情，
                // 会继续把队列里最多 16 MB 的 packet 全部写进磁盘 ——
                // 用户看到的就是「点了暂停，磁盘灯还在狂闪、文件还在变大」。
                // 放在取包**之前**等，这样暂停时手里不攥着任何一批数据。
                await WaitWhileWritePausedAsync().ConfigureAwait(false);

                bool got = await _inputBuffer
                    .WaitTryTakeBatchAsync(_watcherCancelSource.Token, _batchMaxCount, _batchMaxBytes, batch)
                    .ConfigureAwait(false);

                if (!got || batch.Count == 0)
                    continue;

                try
                {
                    // 🚨🚨 2026-11-03 修「暂停后文件还在变大」（用户实测确认无效）：
                    //   原来闸门只在**取包之前**等（上面那句），
                    //   而「已经取出 batch、正在写」的窗口内暂停是**拦不住**的 ——
                    //   这一批（最多 16 MB / 64 个 packet）照样全部写进磁盘。
                    //   16 MB 在 26 MB/s 下约 0.6 秒，用户看到的就是「点了暂停文件还在涨」。
                    //   ⇒ 写之前**再等一次闸门**：暂停时把已取出的这批留在内存里，
                    //      恢复后接着写，不丢包也不重复写。
                    await WaitWhileWritePausedAsync().ConfigureAwait(false);
                    await WriteBatchOnFile(batch).ConfigureAwait(false);
                }
                finally
                {
                    // 🚨 必须在写完**之后**才恢复 Flush 阻塞，
                    // 否则 FlushAsync 会在数据落盘前返回（见 NotifyBatchConsumed 注释）
                    _inputBuffer.NotifyBatchConsumed();
                    batch.Clear();
                }
            }
        }
        catch (Exception ex) when (ex is TaskCanceledException || ex is OperationCanceledException)
        {
            // 收尾时 Watcher 被取消是**正常流程**（下载已结束，队列该退了）。
            // 原来这里打 LogError，日志里出现红字「已取消该操作」，
            // 用户以为下载出错 —— 实际上那一条恰恰代表一切正常。
            Logger?.LogDebug("ConcurrentStream: 写线程已退出（" + ex.GetType().Name + "）");
            CancelState();
        }
        catch (Exception ex)
        {
            SetException(ex);
            _watcherCancelSource.Cancel(false);
        }
        finally
        {
            // 🚨 2026-10-25 修的真 bug：Watcher 一旦退出（异常或取消），
            // 就没有任何线程会再调 NotifyBatchConsumed() → ResumeAddingIfEmpty()。
            // 而 _flushBlocker / _addingBlocker 只在「队列恰好空」时才会被恢复，
            // 于是生产端永久卡在 TryAdd、FlushAsync 永久卡在 WaitToComplete，
            // 界面表现为「100% + 只剩 1 个连接 + 永远停在正在写入磁盘」。
            // 退出前无条件放行：异常场景本来就该抛错，不该顺带把进程拖成假死。
            _inputBuffer.ForceResume();
            await Task.Yield();
        }
    }

    /// <summary>一批最多攒多少个 packet（默认值）。</summary>
    private const int DefaultBatchMaxCount = 64;

    /// <summary>
    /// 写盘闸门：<c>IsSet</c> = 允许写（默认），<c>Reset</c> = 暂停写。
    /// <para>
    /// 暂停语义：Watcher 在**取包之前**等这个闸门，所以暂停时队列里积压的数据原封不动留着，
    /// 恢复后接着写，不会丢包、也不会重复写。
    /// </para>
    /// </summary>
    private readonly ManualResetEventSlim _writeGate = new ManualResetEventSlim(true);

    /// <summary>暂停写盘（worker 仍在跑时由 <c>DownloadService.Pause()</c> 调）。</summary>
    public void PauseWrites()
    {
        try { _writeGate.Reset(); } catch { }
    }

    /// <summary>恢复写盘。</summary>
    public void ResumeWrites()
    {
        try { _writeGate.Set(); } catch { }
    }

    /// <summary>当前是否处于「暂停写盘」状态。</summary>
    public bool WritesPaused => !_writeGate.IsSet;

    /// <summary>
    /// 等待写盘闸门打开。
    /// <para>
    /// 🚨 必须能被取消/强制放行：否则用户「暂停后又取消下载」时，
    /// Watcher 会永远卡在这里，收尾的 <see cref="FlushAsync"/> 也就永远不返回 ——
    /// 那正是历史上出现过的「停在正在写入磁盘、只能杀进程」。
    /// </para>
    /// </summary>
    private async Task WaitWhileWritePausedAsync()
    {
        while (!_writeGate.IsSet)
        {
            // 用取消令牌 + 超时双重保险：任一触发都放行，绝不无限阻塞。
            if (_watcherCancelSource.IsCancellationRequested)
                return;

            try
            {
                await Task.Delay(20, _watcherCancelSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 一批最多攒多少字节（默认值）。
    /// 16 MB —— 与 aria2 的 <c>--disk-cache</c> 默认值、libtorrent 的
    /// <c>coalesce_writes</c> 窗口齐平。攒得越多，排序后越容易拼成更长的连续段，
    /// 机械盘上每批的磁头寻道次数就越少（4 MB 时约 8~16 段，16 MB 时能压到 2~4 段）。
    /// 代价是每任务最多多占 16 MB 内存（并发 3 任务 = 48 MB，可接受）。
    /// </summary>
    private const long DefaultBatchMaxBytes = 16L * 1024 * 1024;

    /// <summary>
    /// 一批最多攒多少个 packet。
    ///
    /// <para>
    /// 🚨 2026-10-24 补：原来这是 <c>private const</c> 写死 64，对机械盘是硬伤。
    /// 512 条连接时，一批 64 个 packet 各来自不同连接、位置完全分散，
    /// 排序后仍是 64 个孤立段 —— 每 4 MB 就要 64 次寻道（HDD 约 10 ms/次），
    /// 理论上限只有约 6 MB/s，而且<b>并发越高越糟</b>。
    /// 机械盘必须把批次放大，让「同一条连接连续读出的多个 64KB」在批内拼成长段。
    /// </para>
    /// </summary>
    private int _batchMaxCount = DefaultBatchMaxCount;

    /// <summary>一批最多攒多少字节。见 <see cref="_batchMaxCount"/> 的说明。</summary>
    private long _batchMaxBytes = DefaultBatchMaxBytes;

    /// <summary>一批最多攒多少个 packet。0 或负数表示用默认 64。</summary>
    public int BatchMaxCount
    {
        get => _batchMaxCount;
        set => _batchMaxCount = value > 0 ? value : DefaultBatchMaxCount;
    }

    /// <summary>一批最多攒多少字节。0 或负数表示用默认 16 MB。</summary>
    public long BatchMaxBytes
    {
        get => _batchMaxBytes;
        set => _batchMaxBytes = value > 0 ? value : DefaultBatchMaxBytes;
    }

    /// <summary>
    /// 把一批 packet 按偏移排序后合并写入。
    ///
    /// <para>
    /// <b>为什么必须合并</b>：这是「大厂式」落盘的核心（libtorrent 的
    /// <c>coalesce_writes</c>、aria2 的 <c>--disk-cache</c> 做的是同一件事）。
    /// 原来的实现每收到 64KB 就 Seek 一次、写一次 —— 16 条连接交错到达时，
    /// 磁头在文件的 16 个位置之间反复跳，机械盘上光寻道就能吃掉一半带宽。
    /// </para>
    ///
    /// <para>
    /// 合并后：同一分片连续读出的若干 64KB 会拼成 1~2MB 的一次写入，
    /// 寻道次数降低一到两个数量级。注意它<b>不减少</b>随机性（16 条连接
    /// 仍在 16 个不同位置），只是把「每 64KB 跳一次」变成「每 2MB 跳一次」。
    /// </para>
    /// </summary>
    private async Task WriteBatchOnFile(List<Packet> batch)
    {
        if (batch.Count == 1)
        {
            // 低负载时（队列里只有一个）走原路径，零额外开销
            await WritePacketOnFile(batch[0]).ConfigureAwait(false);
            return;
        }

        batch.Sort(ComparePacketByPosition);

        int i = 0;
        while (i < batch.Count)
        {
            Packet head = batch[i];
            long segStart = head.Position;
            long segEnd = head.EndOffset;

            // 把所有与当前段首尾相接或重叠的后续 packet 并入同一段
            int j = i + 1;
            while (j < batch.Count && batch[j].Position <= segEnd)
            {
                if (batch[j].EndOffset > segEnd)
                    segEnd = batch[j].EndOffset;
                j++;
            }

            if (j == i + 1)
            {
                // 孤立的一块，不必复制
                await WriteSegmentAsync(segStart, head.Data, 0, head.Length).ConfigureAwait(false);
            }
            else
            {
                // 把 j-i 块拼成一段连续内存，一次写出去
                int segLen = (int)(segEnd - segStart);
                var merged = new byte[segLen];
                for (int k = i; k < j; k++)
                {
                    Packet p = batch[k];
                    int offsetInSegment = (int)(p.Position - segStart);
                    Buffer.BlockCopy(p.Data, 0, merged, offsetInSegment, p.Length);
                }

                Logger?.LogDebug(
                    $"ConcurrentStream: 合并写入 {j - i} 块 / {segLen} 字节 @ {segStart}");
                await WriteSegmentAsync(segStart, merged, 0, segLen).ConfigureAwait(false);
            }

            for (int k = i; k < j; k++)
                batch[k].Dispose();

            i = j;
        }
    }

    private static int ComparePacketByPosition(Packet a, Packet b)
    {
        int cmp = a.Position.CompareTo(b.Position);
        if (cmp != 0) return cmp;
        return a.EndOffset.CompareTo(b.EndOffset);
    }

    private async Task WriteSegmentAsync(long position, byte[] data, int offset, int length)
    {
        // 🚨 _stream（FileStream）不是线程安全的，必须把「定位 + 写」和 FlushAsync 串行化。
        // 2026-10-24 实测：续传保存器每 3 秒调一次 Storage.FlushAsync()，
        // 而 Watcher 线程同时在写同一个 FileStream 的 1MB 缓冲区 ——
        // 并发 Flush/Write 会把缓冲区内容写错位置，表现为「长度对、内容错一块」，
        // 且只在共写模式出现（顺序写模式所有 I/O 都在同一把 _gate 锁里）。
        // 锁内不能有 await（Monitor 不能跨 await），所以这里改成同步写：
        // Watcher 本来就是独占的 LongRunning 线程，同步写反而更快。
        lock (_streamLock)
        {
            _stream.Seek(position, SeekOrigin.Begin);
            _stream.Write(data, offset, length);
        }

        return;
    }

    public long Seek(long offset, SeekOrigin origin)
    {
        if (offset != Position && CanSeek)
        {
            _stream.Seek(offset, origin);
        }

        return Position;
    }

    public void SetLength(long value)
    {
        lock (_streamLock)
        {
            _stream.SetLength(value);
        }
    }

    private async Task WritePacketOnFile(Packet packet)
    {
        await WriteSegmentAsync(packet.Position, packet.Data, 0, packet.Length).ConfigureAwait(false);
        packet.Dispose();
    }

    /// <summary>刷盘等待上限（毫秒）。超过就强制放行并告警，绝不无限等。</summary>
    private const int FlushTimeoutMs = 30000;

    /// <summary>
    /// 刷盘。
    /// <para>
    /// 🚨🚨 2026-11-03 重大修正：原来这个方法**开头无条件 <c>ResumeWrites()</c>** ——
    /// 而它被续传保存器**每 3 秒**调一次。后果是：
    /// 用户点暂停 → 3 秒内这个 FlushAsync 就把闸门**强行打开** →
    /// Watcher 继续写盘 → 文件继续变大。**暂停在 3 秒内必然失效。**
    /// 这才是「暂停确实无效」的真正根因。
    /// </para>
    /// <para>
    /// 现在区分两种语义：
    /// <list type="bullet">
    /// <item><c>force: false</c>（默认，周期刷盘）—— 暂停时**直接跳过**，不碰闸门。
    ///       数据留在内存/OS 缓冲里，恢复后由下一次 Flush 落盘，不会丢。</item>
    /// <item><c>force: true</c>（收尾/取消/完成）—— 先放行闸门再排空，
    ///       保证「暂停 → 取消下载」时 Watcher 不会卡死、最后一次刷盘不漏。</item>
    /// </list>
    /// </para>
    /// </summary>
    public async Task FlushAsync(bool force = false)
    {
        // 周期刷盘遇到暂停：什么都不做，直接返回。
        // ⚠️ 绝不能在这里 ResumeWrites() —— 那等于把用户的暂停操作作废。
        if (!force && WritesPaused)
            return;

        // 收尾刷盘必须先放行写盘闸门。
        // 否则「用户暂停 → 取消下载」时 Watcher 卡在 WaitWhileWritePausedAsync，
        // 队列永远不排空，下面的 WaitToComplete 只能等满 30 秒超时。
        if (force)
            ResumeWrites();

        // 🚨 2026-10-25：原来这里是无超时的 await _inputBuffer.WaitToComplete()。
        // 只要 Watcher 因为任何原因退出（写盘抛 IOException、文件被删、磁盘错误），
        // _flushBlocker 就永久停在 Paused，这个 await 也就永久不返回 ——
        // 用户看到的是「100% + 停在正在写入磁盘」，只能杀进程。
        // 正常情况这一步只要几十毫秒（16MB 写合并窗口），超时只在真出问题时触发。
        Task wait = _inputBuffer.WaitToComplete();
        Task done = await Task.WhenAny(wait, Task.Delay(FlushTimeoutMs)).ConfigureAwait(false);
        if (done != wait)
        {
            Logger?.LogWarning("ConcurrentStream: 刷盘等待超过 " + FlushTimeoutMs
                + " ms，已强制放行（可能有未落盘的包，内容校验会兜住）");
            _inputBuffer.ForceResume();
            try { await wait.ConfigureAwait(false); } catch { }
        }
        else
        {
            await wait.ConfigureAwait(false);
        }

        // 必须与 Watcher 的写串行化（见 _streamLock 注释）
        lock (_streamLock)
        {
            if (_stream?.CanWrite == true)
            {
                _stream.Flush();
            }
        }

        // 🚨 这里原来有一句 GC.Collect()，2026-10-25 删除。
        //
        // 它是 bezzad 上游的遗留：上游只在下载**收尾**时调一次 FlushAsync，
        // 顺手做一次 GC 回收内存流的缓冲，成本可以忽略。
        // 但 GeZi 的续传保存器**每 3 秒**调一次 FlushAsync（为了让续传点贴近真实落盘位置），
        // 于是这句变成了「每 3 秒一次完整 GC」——
        // 完整 GC 会挂起进程内所有线程（含 256 个下载 worker），
        // 而且堆越大停顿越久：实测反馈的「越下越慢」正与此吻合。
        // FlushAsync 的职责是「把数据刷到磁盘」，回收托管内存不是它该干的事，
        // 交给运行时按需做即可。
    }

    public void Dispose()
    {
        if (_disposed == false)
        {
            _disposed = true;

            // 🚨 先排空队列，再取消 Watcher（2026-10-24 修的真 bug）。
            // 原实现直接 Cancel：队列里还没被 Watcher 取走的 packet 会被永久丢弃，
            // 而且 _stream 紧接着被 Dispose —— 表现为「文件长度对、内容缺一块」，
            // 且是间歇性的（取决于取消瞬间队列里有没有货）。给 30 秒上限防死等。
            try
            {
                _inputBuffer.WaitToComplete().Wait(TimeSpan.FromSeconds(30));
            }
            catch
            {
                // 排空失败也要继续释放，不能因为一次异常泄漏句柄
            }

            _watcherCancelSource.Cancel(); // request the cancellation

            lock (_streamLock)
            {
                try { _stream.Flush(); } catch { }
                _stream.Dispose();
            }

            _inputBuffer.Dispose();
        }
    }
}
