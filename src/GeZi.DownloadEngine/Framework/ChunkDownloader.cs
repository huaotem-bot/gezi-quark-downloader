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
        _logger?.LogDebug($"配置项 {e.PropertyName} 已变更");
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
            _logger?.LogDebug($"开始下载分片 {Chunk.Id}");
            await DownloadChunk(downloadRequest, pause, cancelToken).ConfigureAwait(false);
            return Chunk;
        }
        catch (FatalChunkException)
        {
            // 资源变更 / 区间不符 —— 重试无意义，直接上抛让任务失败并作废
            _logger?.LogCritical($"分片 {Chunk.Id} 致命错误：源文件已变化或区间不符");
            throw;
        }
        catch (RangeIgnoredException ignored)
        {
            // 🚨🚨 2026-11-07 新增：「服务端这次没按规矩回 206，但还在容忍范围内」。
            //   与 SlowReconnectException 的区别：那个是「换一条更快的连接」，零退避；
            //   这个是「服务端这次没按规矩回」，应当**稍等再试** —— 真降级时
            //   立刻重连只会再撞一次，退避能让服务端缓过来。
            //   这一步走普通重试路径（吃 FailoverCount、并放大 Timeout），
            //   因为它确实是「一次失败的请求」，不是主动放弃。
            //   ⚠️ 关键差别在任务层：**不再抛致命错误、不再清续传**，
            //   只有窗口内超过容忍次数才会由 DownloadChunk 抛 FatalChunkException。
            _logger?.LogWarning(
                $"分片 {Chunk.Id} 服务端忽略了 Range（第 {ignored.Occurrence} 次，仍在容忍范围内），"
                + "本次按普通失败重试，不判定下载失败");
            return await ContinueWithDelay(downloadRequest, pause, cancelToken).ConfigureAwait(false);
        }
        catch (SlowReconnectException slow)
        {
            // 🚨🚨 2026-11-05 新增：慢连接换连接 —— **零退避、零代价**。
            //
            // 与下面几个 catch 的关键区别（照抄 refs/YunX 的 PreemptedException 语义）：
            //   · **不走 ContinueWithDelay** → 不等 150~5000ms 的指数退避；
            //   · **不动 Chunk.FailoverCount** → 不占用「真出错」的重试预算；
            //   · **不动 Chunk.Timeout** → 读超时窗口保持原样。
            //
            // 为什么可以零退避：这不是失败，是**主动放弃一条慢连接**。
            //   已写字节全部保留在 Chunk.Position 里，下一轮 DownloadChunk 会以
            //   `Start + Position` 作 Range 起点继续拉，一个字节都不重下。
            //   而退避原本是给「服务器限流/503」用的 —— 那种情况立刻重连只会再撞一次；
            //   慢连接恰恰相反：**换一条新连接本身就可能落到快节点**，等待纯属浪费。
            //
            // ⚠️ 上限仍由 SpeedReconnectCount < MaxSpeedReconnects 在抛出点控制（默认 3），
            //    不会无限重连。达到上限后这条连接就老老实实慢慢啃，
            //    剩余区间交给 PreemptRound / TryRefillTail 切给别的 worker。
            _logger?.LogWarning(
                $"分片 {Chunk.Id} 慢连接换连接（零退避），已下 {Chunk.Position}/{Chunk.Length}，"
                + $"剩余 {Chunk.EmptyLength}，实测 {slow.MeasuredBps:F0} B/s");
            return await Download(downloadRequest, pause, cancelToken).ConfigureAwait(false);
        }
        // 🚨🚨 2026-11-07 修「主动暂停/删除却刷几十条 [critical] 分片 N 发生致命错误」：
        //   取消时 HTTP 层抛的可能是 WebException / IOException，不是 TaskCanceledException，
        //   会穿过上面两个具体的 catch 落到最下面的兜底 catch，被打成「致命错误」并 throw。
        //   175 条连接同时取消 = 175 条 critical 日志，纯噪音且刷屏卡顿。
        //   正解：**任何**异常只要取消已请求，就静默重抛（不打日志、不重试、不计失败）。
        //   放在所有 catch 之前，且用异常过滤器（when）不改变原有异常类型分发。
        catch (Exception) when (cancelToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException error) // when stream reader timeout occurred 
        {
            // 取消和「读超时」抛的是同一种异常，必须分开：
            // 以前不判 cancelToken，用户点暂停/取消时这一片会被当成超时去重试
            // （还要吃 150ms~5s 的指数退避 + 重新握手），表现就是「点了取消还在跑」。
            if (cancelToken.IsCancellationRequested) throw;
            _logger?.LogError(error, $"分片 {Chunk.Id} 读取超时，准备重试");
            return await ContinueWithDelay(downloadRequest, pause, cancelToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException error) // when stream reader cancel/timeout occurred 
        {
            // 同上：取消时 cancelToken 注册的 stream.Close 也会抛这个。
            if (cancelToken.IsCancellationRequested) throw;
            _logger?.LogError(error, $"分片 {Chunk.Id} 连接被关闭，准备重试");
            return await ContinueWithDelay(downloadRequest, pause, cancelToken).ConfigureAwait(false);
        }
        catch (Exception error) when (Chunk.CanTryAgainOnFailover() && error.IsMomentumError())
        {
            _logger?.LogError(error, $"分片 {Chunk.Id} 下载出错，准备重试");
            return await ContinueWithDelay(downloadRequest, pause, cancelToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Can't handle this exception
            _logger?.LogCritical(error, $"分片 {Chunk.Id} 发生致命错误");
            throw;
        }
        finally
        {
            _logger?.LogDebug($"分片 {Chunk.Id} 下载方法退出");
            await Task.Yield();
        }
    }

    private async Task<Chunk> ContinueWithDelay(Request request, PauseToken pause, CancellationToken cancelToken)
    {
        _logger?.LogDebug($"分片 {Chunk.Id} 进入退避重试");
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
                        // 🚨🚨 2026-11-07 修「大文件下到 90% 因为一次偶发 200 全部作废」：
                        //   原实现无条件抛 FatalChunkException → DownloadService 走
                        //   ClearResumeAsync()，任务失败 + 续传文件一并删除。
                        //   现在先问容忍闸门（对齐 refs/YunX 的 RANGE_IGNORED_TOLERANCE = 3）：
                        //     · 窗口内未超容忍 → 抛 RangeIgnoredException，由 Download() 接住当普通失败重试；
                        //     · 窗口内超过容忍 → 才认定「服务端真的不支持 Range」，保持原致命行为不变。
                        //   ⚠️ 闸门为 null（老调用方没注入）时**完全退回原行为**，默认等价。
                        GeZi.Core.Support.RangeDowngradeGate gate = _configuration.RangeDowngrade;
                        if (gate != null)
                        {
                            bool exceeded = gate.Observe(
                                Environment.TickCount,
                                _configuration.RangeDowngradeTolerance,
                                _configuration.RangeDowngradeWindowMs);
                            if (!exceeded)
                            {
                                throw new RangeIgnoredException(
                                    $"分片 {Chunk.Id} 请求区间但服务端回了 200（第 {gate.Count} 次，在容忍范围内）",
                                    gate.Count);
                            }
                        }

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
        // 🚨🚨 2026-11-05 改：慢阈值 = max(绝对下限, 动态相对阈值)。
        //
        // 原来只用绝对值 LowestSpeedLimit(16KB/s)。用户把并发开到 256/512 时，
        // 带宽摊到每条连接必然低于 16KB/s（实测 512 路 ≈ 10KB/s），
        // 于是**几乎所有连接同时被判慢** → 重连风暴 → 吞吐周期性塌陷。
        // 用户日志里那条 `10239 B/s` 正好落在阈值下方，就是这个原因。
        //
        // 现在叠加「本任务平均单连接速度 × 0.5」（对齐 refs/YunX 的 avgPerConn/2）：
        //   · 并发高、单连接普遍慢时，阈值跟着降 → 不再全员误判；
        //   · 个别连接真的掉队时（远低于同伴），仍然会被抓住换掉；
        //   · 绝对下限 LowestSpeedLimit 永远兜底，防「收尾只剩一两路、平均值被自己拉低」。
        //
        // ⚠️ 每轮循环重新读取（而不是开头读一次）：动态阈值由监控任务周期更新，
        //    读一次就固定下来的话，这条分片永远用着建连那一刻的旧阈值。
        int lowestSpeedSeconds = _configuration.LowestSpeedLimitSeconds;
        bool speedWatchEnabled = _configuration.MaxSpeedReconnects > 0;
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

                // 🚨🚨 2026-11-05 新增：检查「外部请求换连接」。
                //
                // PreemptRound 的监控线程会比较「这条连接 vs 同伴的速度」，
                // 发现它掉队就置位这个标志。这里每轮读取检查一次 ——
                // 命中就主动断流、抛 SlowReconnectException，走零退避换连接。
                //
                // ⚠️ 为什么放在**每轮循环开头**而不是只在慢检测窗口里：
                //   慢检测窗口默认 10 秒才评估一次，而外部标志可能随时被置位。
                //   每轮检查的开销只是一次 Interlocked.Exchange，可忽略。
                //
                // ⚠️ 上限同样受 MaxSpeedReconnects 约束：达到上限就不再响应外部请求，
                //   否则一条被 CDN 稳定限速的连接会无限重连，把带宽全花在握手。
                // ⚠️ 先把标志「取出来」再判断额度：
                //   TakeExternalReconnectRequest() 是**读并清除**语义，
                //   如果写成 `Take() && 额度够`，额度不足时标志已被清掉，
                //   这次请求就白丢了（下次还得等监控再置一次）。
                //   取出来再判，额度不足时也不残留脏标志。
                if (Chunk.TakeExternalReconnectRequest())
                {
                    if (Chunk.SpeedReconnectCount < _configuration.MaxSpeedReconnects)
                    {
                        Chunk.SpeedReconnectCount++;
                        _logger?.LogWarning(
                            $"分片 {Chunk.Id} 被判定为掉队连接，主动换连接（第 {Chunk.SpeedReconnectCount}/"
                            + $"{_configuration.MaxSpeedReconnects} 次），已下 {Chunk.Position}/{Chunk.Length}");
                        stream.Close();
                        throw new SlowReconnectException(
                            $"chunk {Chunk.Id} preempted by external watchdog (peer-relative slow)",
                            0);
                    }
                    // 额度用尽：忽略这次请求，让这条连接继续慢慢啃。
                    // 剩余区间会由 PreemptRound 的**切片**分支摊给别的 worker。
                }
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

                    // 🚨 2026-11-08：分片可能在写期间被抢占/末段补片切分（End 缩小）。
                    // 切分线程会把原片 Position 夹回新 Length，但若本 worker 在切分**之后**才
                    // 完成 `Position += readSize`，又会让 Position 越过新 Length（竞态）。
                    // 这里补一道防御性夹取：Position 永不超过 Length，保证「进度账」与
                    // 「完成判据」不漂移（真正写进尾片区间的字节由尾片重新下，无害）。
                    long clampLen = Chunk.Length;
                    if (Chunk.Position > clampLen)
                        Chunk.Position = clampLen;

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
                            // 每轮重算有效阈值：**相对判据优先**，绝对只有一个「彻底死了」的底线。
                            //
                            // 🚨🚨 2026-11-07 重做（用户实测日志坐实）：旧逻辑会自伤。
                            //   连接速度 13.5 KB/s < 绝对下限 16 KB/s → 全部判慢；
                            //   而「整体限速」要求全员低于 12 KB/s 才触发 —— 13.5 又刚好不够低，
                            //   所以 OverallSlowActive 一直是 false、绝对下限 16 KB/s 全程生效，
                            //   → 每一轮上千次断开重连 → 握手 200~600ms 占 3~5% → 平均更低。
                            //   用户日志里 747 次「连接被强制关闭」就是它。
                            //
                            // 新判据：**只跟同伴比**。
                            //   动态阈值 = 同时在飞连接速度的**中位数** × DynamicSlowRatio（默认 0.5）。
                            //   · 全员 13.5 KB/s → 阈值 6.75 KB/s → 全员合格，零重连 ✓
                            //   · 9 条 4 KB/s + 160 条 14 KB/s → 中位数 14 → 阈值 7 → 只抓那 9 条 ✓
                            //   · 真死掉的（0.5 KB/s）无论中位数多少都低于 4 KB/s 死亡底线 → 抓 ✓
                            //   · 收尾只剩一两路时中位数就是它自己，会把自己判慢 ——
                            //     所以中位数在样本 < 2 时**不更新**，保留上一轮的值。
                            long dynamicFloor = _configuration.DynamicSlowThresholdBps;
                            long absoluteFloor = _configuration.LowestSpeedLimit;
                            // 判慢线计算已抽成纯函数（GeZi.Core.Support.SlowWatchdog），
                            // 那边有完整的自检与「为什么用中位数、为什么不 max 绝对线」的长注释。
                            // 这里只负责把本分片的实测速度喂进去。
                            long lowestSpeed = GeZi.Core.Support.SlowWatchdog.EffectiveFloor(
                                dynamicFloor, absoluteFloor, GeZi.Core.Support.SlowWatchdog.DeathFloorBps);
                            // 剩余量太少就不值得重连：重连要付 TCP+TLS 握手和
                            // 重发请求头的代价（实测 200~600ms）。
                            // 🚨 判据必须按「时间」而不是「字节数」：
                            // 固定 2MB 门槛会把 1MB 的小分片全部排除掉（8MB 文件切 8 片
                            // 就是 1MB/片），自适应分片器在网盘场景下每片常只有几百 KB 到几 MB。
                            //
                            // 🚨🚨 2026-11-08 修「收尾卡死、必须暂停再恢复才动」：
                            //   旧判据 `bps > 0 && (EmptyLength / bps) > 2.0` 有两个致命缺陷：
                            //   ① 窗口内读到 0 字节时 bps == 0 → 第一项直接 false →
                            //      一条彻底挂死的连接永远不会被判慢、永远不重连，只能干等超时。
                            //      用户日志「11:18:16 3.39 MB/s → 11:18:36 414 KB/s →
                            //      卡 28 秒无进展，手动暂停再恢复立刻跑完」就是它。
                            //   ② 固定 2 秒基准偏高：收尾每片只剩几十 KB，按 6 KB/s 算
                            //      剩余时间远小于 2 秒 → 一律判「不值得重连」，收尾全是慢连接在啃。
                            //      重连成本实测 200~600ms，剩余 1 秒以上就该换。
                            //   判据已抽到 GeZi.Core.Support.SlowWatchdog.ShouldReconnect（有自检）：
                            //   · 已下完 → 不重连；· 零字节 → 只要还有剩余就重连；
                            //   · 否则按当前速度还要下 > 1 秒才重连。
                            bool worthReconnect = GeZi.Core.Support.SlowWatchdog.ShouldReconnect(
                                bps, Chunk.EmptyLength,
                                _configuration.EndgameNow
                                    ? GeZi.Core.Support.SlowWatchdog.EndgameReconnectCostMs
                                    : GeZi.Core.Support.SlowWatchdog.ReconnectCostMs);
                            if (bps < lowestSpeed && worthReconnect &&
                                Chunk.SpeedReconnectCount < _configuration.MaxSpeedReconnects &&
                                SpeedReconnectGovernor.TryAcquire())
                            {
                                Chunk.SpeedReconnectCount++;
                                _logger?.LogWarning(
                                    $"分片 {Chunk.Id} 速度 {bps:F0} B/s 低于下限 {lowestSpeed} B/s，" +
                                    $"主动断开重连（第 {Chunk.SpeedReconnectCount}/{_configuration.MaxSpeedReconnects} 次），" +
                                    $"已下 {Chunk.Position}/{Chunk.Length}，剩余 {Chunk.EmptyLength}");
                                // 🚨🚨 2026-11-05 改：抛**专用异常**，不再抛 TaskCanceledException。
                                //   原来复用它 → 被 Download() 里「读超时也重试」的分支接住 →
                                //   走 ContinueWithDelay → 干等指数退避 + 吃掉 FailoverCount
                                //   + 永久放大 Chunk.Timeout。三者叠加让「换连接」变得比不换还慢。
                                //   现在由 Download() 单独 catch：立即重连、零退避、不消耗任何预算。
                                //   已写字节全部保留（Chunk.Position 不动），重连从断点续传。
                                stream.Close();
                                throw new SlowReconnectException(
                                    $"分片 {Chunk.Id} 读取速度过慢（{bps:F0} B/s），已重新连接",
                                    bps);
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
            _logger?.LogError(exp, $"分片 {Chunk.Id} 的连接被服务端关闭或强制取消");
            cancelToken.ThrowIfCancellationRequested();
            if (innerToken?.IsCancellationRequested == true)
            {
                _logger?.LogError(exp, $"分片 {Chunk.Id} 读取超时");
                throw new TaskCanceledException($"分片 {Chunk.Id} 读取超时", exp);
            }

            throw; // throw origin stack trace of exception 
        }

        _logger?.LogDebug($"分片 {Chunk.Id} 数据流读取完成");
    }

    private void OnDownloadProgressChanged(DownloadProgressChangedEventArgs e)
    {
        DownloadProgressChanged?.Invoke(this, e);
    }
}
