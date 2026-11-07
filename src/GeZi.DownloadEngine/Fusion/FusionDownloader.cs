// GeZi 鸽子下载器 — 多网盘下载器
// Copyright (C) 2026  GeZi contributors
//
// 本程序是自由软件：你可以再分发和/或修改它，依据 GNU Affero 通用公共许可证
// 第 3 版（或更新版本）的条款，由自由软件基金会发布。
// 本程序基于「希望它有用」的目的分发，但不提供任何担保。
// 详见 <https://www.gnu.org/licenses/>。

using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GeZi.DownloadEngine.Framework;
using GeZi.DownloadEngine.Planning;
using GeZi.DownloadEngine.Resume;
using GeZi.DownloadEngine.Storage;
using GeZi.DownloadEngine.Transport;
using GeZi.DownloadEngine.Validation;

namespace GeZi.DownloadEngine
{
    /// <summary>
    /// 融合引擎入口 —— 用 bezzad 的调度框架 + 我们自己的四个零件跑一个下载任务。
    ///
    /// <para>
    /// 为什么要有这个类：主窗口原本直接实例化自研引擎，调度与写盘揉在一个类里。
    /// 融合引擎（<see cref="Framework.DownloadService"/>）复用 bezzad 的框架层，
    /// 但把分片规划、传输通道、落盘策略、内容校验全部换成我们自己的实现：
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="AdaptiveChunkPlanner"/> —— 我们的 4096 片 + 头部变长分片算法</item>
    /// <item><see cref="OpenSslChunkTransport"/> —— UC 的 CDN 按 TLS 指纹拦 SChannel，必须走 OpenSSL 桥</item>
    /// <item><see cref="SequentialFileStorage"/> / <see cref="SharedFileStorage"/> —— 可切换落盘</item>
    /// <item><see cref="IfRangeValidator"/> —— If-Range + Content-Range + 哈希三级校验</item>
    /// </list>
    /// </summary>
    public static class FusionDownloader
    {
        /// <summary>未指定线程数时的默认并发。迅雷 CDN 单 IP 超过 16 并发会大量 503（实测）。</summary>
        public const int DefaultThreads = 16;

        /// <summary>
        /// 跑一个下载任务。由 <see cref="Scheduling.DownloadScheduler"/> 调用。
        /// </summary>
        /// <param name="job">任务描述（URL / 目标路径 / Cookie / UA / Referer / Origin / 哈希）。</param>
        /// <param name="threads">并发数；0 表示用 <see cref="DefaultThreads"/>。</param>
        /// <param name="progress">进度回调。</param>
        /// <param name="cancel">取消令牌。</param>
        /// <param name="pause">暂停令牌。</param>
        /// <param name="storageMode">落盘策略。</param>
        /// <param name="sequentialWindowBytes">顺序落盘的重排窗口；0 = 默认 32 MB。</param>
        /// <param name="expectedMd5">期望 MD5（取链接口有就给）。</param>
        /// <param name="expectedSha1">期望 SHA-1。</param>
        /// <param name="expectedSha256">期望 SHA-256。</param>
        /// <param name="log">诊断日志（可为 null）。</param>
        /// <param name="onServiceReady">
        /// 服务实例创建后回调一次，调用方可借它调用 <see cref="DownloadService.SetPriorityWindow"/>
        /// 实现「边下边看」的播放优先级。回调在下载开始前、同一线程上同步执行。
        /// </param>
        /// <param name="diagnostics">
        /// 结构化诊断快照（约每 2 秒一次），驱动任务行的「连接车道」。
        /// <para>这里没有独立诊断线程主动上报；
        /// 融合引擎没有，所以这里借 bezzad 的进度事件节流上报 ——
        /// <c>ActiveChunks</c>（= <c>ParallelCount - 信号量剩余</c>）就是活跃连接数，
        /// 正好是车道要的那个值。不传则完全不上报，行为与之前一致。</para>
        /// </param>
        public static async Task<DownloadResult> DownloadAsync(
            DownloadJob job,
            int threads,
            IProgress<DownloadProgress> progress,
            CancellationToken cancel,
            PauseToken pause,
            StorageMode storageMode = StorageMode.Shared,
            long sequentialWindowBytes = 0,
            string expectedMd5 = null,
            string expectedSha1 = null,
            string expectedSha256 = null,
            Action<string> log = null,
            Action<DownloadService> onServiceReady = null,
            Action<DiagnosticsSnapshot> diagnostics = null)
        {
            if (job == null)
            {
                return new DownloadResult { Ok = false, Message = "任务为空" };
            }

            if (string.IsNullOrWhiteSpace(job.Url))
            {
                return new DownloadResult { Ok = false, Message = "下载地址为空" };
            }

            if (string.IsNullOrWhiteSpace(job.Dest))
            {
                return new DownloadResult { Ok = false, Message = "目标路径为空" };
            }

            // 🚨 下载前连通性预检（DNS + TCP，带退避重试）。
            // 原因见 PreflightProbe 的注释：融合引擎一次性开满 256 条连接，
            // 全部同时做同步 DNS；DNS 抖动一下就会在 1 秒内全灭，
            // 并把英文原始异常抛到界面上。预检把它变成「多等几百毫秒」。
            string preflight = await PreflightProbe
                .CheckAsync(job.Url, cancel, log)
                .ConfigureAwait(false);
            if (preflight != null)
            {
                if (log != null)
                {
                    log("下载前预检失败：" + preflight);
                }
                return new DownloadResult { Ok = false, Message = preflight };
            }

            int eff = threads > 0 ? threads : DefaultThreads;
            if (eff > ChunkSplitter.MaxRealConcurrency)
            {
                eff = ChunkSplitter.MaxRealConcurrency;
            }
            if (eff < 1)
            {
                eff = 1;
            }

            // 🚨 直链自愈（2026-10-28 新增）——
            // 四网盘给的下载直链都是带签名的临时地址，夸克/UC 的 TTL 只有 300 秒；
            // 9.5 GB 这种大文件按 26 MB/s 也要 6 分钟以上，必然在下载中途过期。
            // 老引擎一直有这条自愈路径（Legacy/SegmentedDownloader.Download.cs:97-123），
            // 融合引擎从来没有 ⇒ 表现就是「下到一半突然失败：直链已失效或没有权限，请重新取链」。
            //
            // 修法：识别签名类错误（401/403/412）→ 调 job.LinkRefresher 重新取链 → 用新链重试。
            // 进度不会丢：FileResumeStore 的快照按**目标文件路径**存（FileResumeStore.cs:49-52），
            // 与 URL 无关；重试时框架会自动 LoadResumeAsync 接续（DownloadService.cs:796）。
            // 上限按文件大小算（与老引擎同一套 ComputeMaxLinkRefreshes，防死循环）。
            int maxRefreshes = MaxLinkRefreshes;
            bool maxRefreshesSized = false;
            int refreshes = 0;
            int quickFailStreak = 0;
            string currentUrl = job.Url;
            long expectedTotal = 0;
            Exception finalFailure = null;
            bool wasCancelled = false;
            var sw = Stopwatch.StartNew();

            while (true)
            {
                Exception failure = null;
                bool cancelled = false;

                // 本次尝试活了多久 —— 用来区分「TTL 到期」和「一刷新就挂」。
                var attemptSw = Stopwatch.StartNew();

                DownloadConfiguration options = BuildOptions(
                    job, eff, storageMode, sequentialWindowBytes, expectedMd5, expectedSha1, expectedSha256);

                var service = new DownloadService(options);
                if (onServiceReady != null)
                {
                    // 先把实例交出去，UI 才能在整个下载期间随时调整播放优先级窗口。
                    try { onServiceReady(service); }
                    catch { /* 回调异常不能影响下载本身 */ }
                }
                if (log != null)
                {
                    // 把框架内部的分级日志接到调用方的回调：续传命中/丢弃、内容校验结果、
                    // 抢占切分这些「静默分支」全靠它才看得出为什么。
                    service.AddLogger(new ActionLogger(log));
                }

                service.DownloadFileCompleted += (sender, e) =>
                {
                    if (e == null)
                    {
                        return;
                    }
                    if (e.Error != null)
                    {
                        failure = e.Error;
                    }
                    if (e.Cancelled)
                    {
                        cancelled = true;
                    }
                };

            if (progress != null || diagnostics != null)
            {
                // 诊断节流：进度事件每个分片完成都会触发（256 并发时非常密），
                // 而车道只需要 2 秒粒度 —— 不节流会把 UI 线程打满。
                var diagClock = Stopwatch.StartNew();
                int workerCount = options.ParallelCount;
                // 上一次上报的时刻（毫秒）。初值取一个很大的负数，
                // 保证**第一个进度事件就立即上报** —— 小文件几十毫秒就下完，
                // 如果第一次也要等满 2 秒，快照数永远是 0，车道永远空着。
                long lastDiagMs = long.MinValue / 2;
                service.DownloadProgressChanged += (sender, e) =>
                {
                    if (e == null)
                    {
                        return;
                    }
                    if (progress != null)
                    {
                        try
                        {
                            progress.Report(new DownloadProgress
                            {
                                Done = e.ReceivedBytesSize,
                                Total = e.TotalBytesToReceive,
                                Speed = e.BytesPerSecondSpeed,
                                // 🚨 2026-11-07：界面显示用 5 秒平滑值，调度仍用 Speed。
                                DisplaySpeed = e.DisplaySpeed > 0 ? e.DisplaySpeed : e.BytesPerSecondSpeed,
                                // 收尾/校验阶段没有网络流量，速度必然是 0；
                                // 带上 Phase 让界面显示「正在校验文件完整性…」而不是卡在「下载中」。
                                Phase = e.Phase,
                            });
                        }
                        catch
                        {
                            // 进度回调抛异常不能影响下载本身。
                        }
                    }
                    if (diagnostics != null)
                    {
                        long nowMs = diagClock.ElapsedMilliseconds;
                        if (nowMs - lastDiagMs < 2000)
                        {
                            return;
                        }
                        lastDiagMs = nowMs;
                        try
                        {
                            diagnostics(new DiagnosticsSnapshot
                            {
                                Name = Path.GetFileName(job.Dest),
                                Dest = job.Dest,
                                Speed = e.BytesPerSecondSpeed,
                                ActiveConnections = e.ActiveChunks,
                                WorkerCount = workerCount,
                                DoneBytes = e.ReceivedBytesSize,
                                TotalBytes = e.TotalBytesToReceive,
                                MergeMode = false,
                                Timestamp = DateTime.Now,
                            });
                        }
                        catch
                        {
                            // 诊断上报失败绝不能影响下载。
                        }
                    }
                };
            }

            // 取消 → bezzad 的 CancelAsync。注册完记得 Dispose，否则 DownloadService 会被令牌长期持有。
            CancellationTokenRegistration cancelReg = default(CancellationTokenRegistration);
            if (cancel.CanBeCanceled)
            {
                cancelReg = cancel.Register(() =>
                {
                    try
                    {
                        service.CancelAsync();
                    }
                    catch
                    {
                        // 已经结束的情况下 CancelAsync 可能抛，忽略。
                    }
                });
            }

            // 暂停 → 轮询我们的 PauseToken 并映射到 bezzad 的 Pause/Resume。
            // 为什么用轮询而不是事件：PauseToken 没有变更事件，而 100ms 的轮询开销可以忽略。
            var pauseWatchCts = new CancellationTokenSource();
            Task pauseWatch = WatchPauseAsync(service, pause, pauseWatchCts.Token);

                var package = new DownloadPackage { FileName = job.Dest };
                try
                {
                    await service.DownloadFileTaskAsync(package, currentUrl, cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                catch (Exception ex)
                {
                    if (failure == null)
                    {
                        failure = ex;
                    }
                }
                finally
                {
                    try
                    {
                        pauseWatchCts.Cancel();
                    }
                    catch
                    {
                        // ignore
                    }
                    try
                    {
                        await pauseWatch.ConfigureAwait(false);
                    }
                    catch
                    {
                        // 监控任务被取消是正常路径
                    }
                    cancelReg.Dispose();
                    try
                    {
                        service.Dispose();
                    }
                    catch
                    {
                        // ignore
                    }
                    pauseWatchCts.Dispose();
                }

                if (cancel.IsCancellationRequested || cancelled)
                {
                    wasCancelled = true;
                    break;
                }

                if (failure == null)
                {
                    // 🚨 「200 + 错误页」：过期直链不一定回 4xx，有些 CDN 直接回 200 加一个
                    //    几十字节的错误页，框架会当它正常下完，只有长度能看出来。
                    //    这种情况必须也走重新取链，否则用户看到「完成」但文件打不开。
                    long actualLen = TryFileLength(job.Dest);
                    // 🚨 2026-11-06（S0 观测）：收尾时把「应有长度 / 磁盘实际长度」一起记下来。
                    //    纯日志、零行为变化；用途是让「假完成 / 进度与磁盘背离」在现场可被直接读出来，
                    //    而不是等到用户打开文件才发现。判定逻辑一行未动。
                    if (log != null && package.TotalFileSize > 0)
                    {
                        log(string.Format(
                            "收尾长度核对：应有 {0} 字节，实际 {1} 字节{2}",
                            package.TotalFileSize, actualLen,
                            actualLen == package.TotalFileSize ? "（一致）" : "（不一致，走长度硬校验）"));
                    }
                    if (package.TotalFileSize > 0 && actualLen >= 0 && actualLen != package.TotalFileSize)
                    {
                        failure = new LinkLengthMismatchException(string.Format(
                            "下载完成但长度不符（期望 {0} 字节，实际 {1} 字节），疑似直链已过期返回了错误页。",
                            package.TotalFileSize, actualLen));
                    }
                    else
                    {
                        // 本次尝试成功，记住总长（后面做长度硬校验）并跳出重试循环。
                        expectedTotal = package.TotalFileSize;
                        break;
                    }
                }

                // ── 直链自愈判定 ──────────────────────────────────────────
                // 只有「签名/授权过期」这一类错误重新取链才有意义；
                // DNS 失败、磁盘写失败、校验不符这些重取一万次也没用。
                // 首次失败时才知道真实总长（DownloadJob 里没有大小字段），
                // 先按保守假设算一次上限，之后每次失败再按实测速度重算。
                if (!maxRefreshesSized)
                {
                    maxRefreshesSized = true;
                    maxRefreshes = ComputeMaxLinkRefreshes(package.TotalFileSize, 0);
                }
                else
                {
                    // 🚨 按实测速度重算上限（2026-10-30）。固定按 2 MB/s 估会让低速大文件
                    //    在中途耗尽刷新次数，表现成「下到一半就说直链失效」。
                    double elapsedSecs = sw.Elapsed.TotalSeconds;
                    long doneBytes = TryFileLength(job.Dest);
                    if (elapsedSecs > 5.0 && doneBytes > 0)
                    {
                        maxRefreshes = ComputeMaxLinkRefreshes(
                            package.TotalFileSize, doneBytes / elapsedSecs);
                    }
                }

                // 刷新后几秒内又挂 = 不是 TTL 到期，别再浪费刷新次数。
                if (attemptSw.Elapsed.TotalSeconds < QuickFailWindowSeconds)
                {
                    quickFailStreak++;
                }
                else
                {
                    quickFailStreak = 0;
                }

                LinkRefreshReason reason = ClassifyLinkError(failure);
                if (reason != LinkRefreshReason.None
                    && job.LinkRefresher != null
                    && quickFailStreak < MaxQuickFailStreak
                    && refreshes < maxRefreshes)
                {
                    refreshes++;
                    if (log != null)
                    {
                        log(string.Format(
                            "直链{0}，正在重新取链（第 {1}/{2} 次）…",
                            DescribeRefreshReason(reason), refreshes, maxRefreshes));
                    }

                    string fresh = null;
                    try
                    {
                        fresh = await job.LinkRefresher(cancel).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        wasCancelled = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (log != null)
                        {
                            log("重新取链失败：" + ex.Message);
                        }
                    }

                    if (!string.IsNullOrEmpty(fresh))
                    {
                        currentUrl = fresh;
                        // 不清空 package：进度在 .geziresume 里，下一次尝试会自动接续。
                        continue;
                    }

                    if (log != null)
                    {
                        log("重新取链没有拿到新地址，放弃自愈。");
                    }
                }

                finalFailure = failure;
                break;
            }

            sw.Stop();

            if (wasCancelled)
            {
                return new DownloadResult { Ok = false, Message = "已取消" };
            }

            if (finalFailure != null)
            {
                log?.Invoke("融合引擎失败：" + finalFailure.Message);
                return new DownloadResult { Ok = false, Message = DescribeFailure(finalFailure) };
            }

            // 收尾硬校验：内容哈希由 IfRangeValidator 在框架内部做过（没给哈希时会 Skip），
            // 但「长度是否等于源大小」必须在这里再确认一次 ——
            // 历史教训（verify1017）：分片长度涨上去了、目标文件长度却对不上，只比内容不比长度看不出来。
            long expect = expectedTotal;
            long actual = -1;
            try
            {
                var fi = new FileInfo(job.Dest);
                if (fi.Exists)
                {
                    actual = fi.Length;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("融合引擎长度校验读文件失败：" + ex.Message);
            }

            if (expect > 0 && actual != expect)
            {
                log?.Invoke(string.Format("融合引擎长度不符：期望 {0}，实际 {1}", expect, actual));
                return new DownloadResult
                {
                    Ok = false,
                    Message = string.Format(
                        "下载完成但文件长度不符（期望 {0} 字节，实际 {1} 字节），已保留续传信息，请重试。",
                        expect, actual < 0 ? 0 : actual),
                };
            }

            double secs = sw.Elapsed.TotalSeconds;
            // 🚨 2026-11-01（用户要求）：这里**不能**再带「完成」二字。
            //    这条 Message 会一路传到 DownloadScheduler.Report(…, JobState.Completed, res.Message)
            //    → JobUpdate.Error → TaskItem.Error，而 TaskItem.DetailText 的完成分支是
            //    StatusText + " · " + Error，而 StatusText 已经是「完成」
            //    ⇒ 界面上就成了「完成 · 完成，平均 1.66 MB/s」，同一个词出现两次。
            //    现在只返回**附加信息**（平均速度），拼接交给 DetailText 负责，
            //    最终显示成「完成 · 平均 1.66 MB/s」。
            string speedText = secs > 0.1 && actual > 0
                ? string.Format("平均 {0:0.00} MB/s", actual / 1048576.0 / secs)
                : "";
            return new DownloadResult { Ok = true, Message = speedText };
        }

        /// <summary>把 <see cref="DownloadJob"/> 翻译成 bezzad 的配置对象。</summary>
        private static DownloadConfiguration BuildOptions(
            DownloadJob job,
            int eff,
            StorageMode storageMode,
            long sequentialWindowBytes,
            string expectedMd5,
            string expectedSha1,
            string expectedSha256)
        {
            var request = new RequestConfiguration();
            if (!string.IsNullOrEmpty(job.UserAgent))
            {
                request.UserAgent = job.UserAgent;
            }
            if (!string.IsNullOrEmpty(job.Referer))
            {
                request.Referer = job.Referer;
            }
            // Cookie 与 Origin 走 Headers：ChunkDownloader.BuildChunkRequest 从这里读，
            // 而 CookieContainer 还要按域匹配（夸克 / UC / 阿里 / 迅雷四个域各不相同，容易漏）。
            if (!string.IsNullOrEmpty(job.Cookie))
            {
                request.Headers["Cookie"] = job.Cookie;
            }
            if (!string.IsNullOrEmpty(job.Origin))
            {
                request.Headers["Origin"] = job.Origin;
            }
            // Accept 是 WebHeaderCollection 的受限头，只能走强类型属性，
            // 直接 Headers["Accept"] = ... 会抛「必须使用适当的属性或方法修改"Accept"标头」。
            request.Accept = "*/*";

            // 🚨 2026-11-05：把该网盘的客户端额外头透传到数据面。
            // 原来下载层只有 UA / Referer / Origin / Cookie 四个头，取链层却带完整
            // 浏览器头 —— 身份自相矛盾。这些头经 ChunkDownloader.BuildChunkRequest
            // 的 ExtraHeaders 原样发出，无需改动下载引擎。
            if (job.ExtraHeaders != null)
            {
                foreach (var kv in job.ExtraHeaders)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    if (string.Equals(kv.Key, "Cookie", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(kv.Key, "User-Agent", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(kv.Key, "Referer", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(kv.Key, "Origin", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(kv.Key, "Accept", StringComparison.OrdinalIgnoreCase)) continue;
                    request.Headers[kv.Key] = kv.Value;
                }
            }
            request.Timeout = 15000;

            // 🚨🚨 2026-11-05：顺序写的重排窗口按并发数自适应。
            //
            // 默认 32 MB 在 256/512 路并发下太小：每条连接一个 64 KB 缓冲，
            // 256 路两轮就能把窗口塞满；一旦头部某片慢，全员背压 →
            // 用户看到「下了几兆突然卡着不动」。窗口放大到至少「并发 × 512 KB」
            // （上限 128 MB），给慢片留出足够的等待余量。
            //
            // ⚠️ 只在**顺序写**且调用方没显式指定（0）时生效；
            //    共写完全不走 SequentialWindowBytes，不受影响。
            // ⚠️ 这是内存上限而非预分配：顺序写本来就永不预分配文件长度，
            //    放大窗口不会占用磁盘，只在乱序数据堆积时多用内存。
            long effWindow = sequentialWindowBytes;
            if (storageMode == StorageMode.Sequential && effWindow <= 0)
            {
                effWindow = (long)eff * 512L * 1024L;
                if (effWindow < 32L * 1024 * 1024) effWindow = 32L * 1024 * 1024;
                if (effWindow > 128L * 1024 * 1024) effWindow = 128L * 1024 * 1024;
            }

            // 🚨 机械盘必须放大写合并批次（2026-10-24 补的缺口）。
            // 批次上限 64 时，512 条连接每批只能凑到 64 个来自不同位置的孤立包，
            // 排序后仍是 64 段 —— 每 4 MB 就要 64 次寻道（HDD 约 10 ms/次），
            // 理论上限只有约 6 MB/s，而且并发越高越糟。
            // 放大到 512 个 / 64 MB 后，同一条连接连续读出的多个 64 KB 能在批内
            // 拼成 1 MB 级长段，寻道次数降一个数量级。
            // 固态盘没有寻道代价，保持默认 64 / 16 MB 即可（省内存）。
            bool hdd = DiskMedia.DetectForPath(job.Dest) == DiskMediaKind.Hdd;

            return new DownloadConfiguration
            {
                ParallelDownload = true,
                ChunkCount = eff,
                ParallelCount = eff,
                // 64 KB 读缓冲：实测 64 KB / 256 KB / 1 MB 三者速度无差别，取中间值减少小包。
                BufferBlockSize = 64 * 1024,
                // 必须显式设：bezzad 默认 1000ms，慢一点的连接会被判超时反复重试。
                Timeout = 15000,
                RequestConfiguration = request,
                Planner = AdaptiveChunkPlanner.Instance,
                // UC 的 CDN 按 TLS ClientHello 指纹拦 SChannel，必须走 OpenSSL 桥；
                // 其余网盘的 CDN 不查指纹，走原生 HttpWebRequest 更快。
                Transport = job.UseOpenSsl
                    ? (IChunkTransport)OpenSslChunkTransport.Instance
                    : HttpChunkTransport.Instance,
                StorageMode = storageMode,
                SequentialWindowBytes = effWindow,
                // 🚨 必须预分配（2026-10-24 修的真 bug，直接决定「边下边看」能不能用）。
                // 不预分配时目标文件长度 = 已写入的最大偏移，于是：
                //   · 播放器打开时看到的是一截不完整文件，读不到末尾的 moov 索引；
                //   · 用户下到 30% 点播放，播放器仍报「无法播放此文件」。
                // 预分配后文件从第一秒起就是完整尺寸，moov 优先写进去就能立刻播。
                // 代价：磁盘上立刻占用完整大小（大厂下载器全都这么做）。
                ReserveStorageSpaceBeforeStartingDownload = true,
                EnableSlowChunkPreemption = true,
                // 播放优先级（边下边看）必须走队列路径，见 EnablePlaybackPriority 的注释。
                EnablePlaybackPriority = true,
                // ★ 末段补片粒度（2026-11-01 新增，解决"最后阶段严重掉速、只用极少线程"）。
                //   与 PreemptMinBytes（512KB，慢片抢占用）刻意分开：
                //   收尾时剩下的大片要能切得比 512KB 更细，才能把空闲 worker 填满。
                //   依据 refs/YunX 的 ElasticAllocator：尾部把块压到 remaining/workers，
                //   下限 MIN_TAIL_BLOCK 取 64KB；这里保守取 256KB
                //   （每片仍要一次 Range 请求，太碎会拖慢快连接）。
                TailRefillMinBytes = 256L * 1024,
                // 慢连接看门狗：低于 16 KB/s 持续 10 秒就主动断开重连（换条新 TCP 连接）。
                // 实测（8MB/8 线程，首连接 8 KB/s、其余 60 KB/s）：
                //   关闭 → 126.6 秒；开启 → 22.5 秒，快 5.6 倍。
                // 全部连接都正常时（60 KB/s 均速）开关前后 17.5s vs 17.4s，无误伤。
                // 设 LowestSpeedLimit = 0 即完全关闭。
                //
                // 🚨🚨 2026-11-05：这个绝对下限**按网盘分档**，不再写死 16KB/s。
                //   理由见 DownloadJob.SlowFloorBps 的注释 —— 阿里/UC 是「单 IP 总配额」
                //   限流，256 路摊下来每路只有 10 KB/s，用 16KB/s 会把全部连接判成慢，
                //   151 次重连全打在同一件事上（用户实测日志）。
                //   job.SlowFloorBps = 0（自检/老路径）时退回引擎默认 16 KB/s。
                LowestSpeedLimit = job.SlowFloorBps > 0 ? job.SlowFloorBps : 16L * 1024,
                LowestSpeedLimitSeconds = 10,
                MaxSpeedReconnects = 3,
                // 整体限速判定（2026-11-05 恢复老引擎的机制）：
                // 大多数连接同时低于阈值 → 是服务端按 IP 的整体配额低，抢占无用。
                OverallSlowMinSamples = 4,
                OverallSlowHoldMs = 30_000,
                PreemptEndgameInflight = 3,
                PreemptMinBps = 12L * 1024,
                ResumeStore = FileResumeStore.Instance,
                Validator = IfRangeValidator.Instance,
                // 2026-11-04：把「源文件指纹」传进下载服务，写入 .geziresume 的 sourcekey 行。
                // 续传加载时与本次任务比对 —— 不一致说明同一路径换了另一个源文件，
                // 必须整份作废重下（否则拼出「长度对、内容坏」的成品）。
                // 空串 = 调用方没提供 → 不做这项校验，退回按 TotalBytes 判。
                SourceKey = job.SourceKey ?? "",
                ExpectedMd5 = expectedMd5 ?? job.Md5,
                ExpectedSha1 = expectedSha1 ?? job.Sha1,
                ExpectedSha256 = expectedSha256 ?? job.Sha256,
                // 机械盘：512 个包 / 64 MB 一批（见上面 hdd 的说明）；固态盘用默认 0 = 64 / 16 MB。
                // 🚨 2026-11-07：取值改从 Core 的纯计算常量读，数值逐字不变，
                //    目的只是让这批参数能被自检钉死（防未来改坏共写实测达标的主路径）。
                BatchMaxCount = hdd ? GeZi.Core.Support.DownloadSizing.HddBatchMaxCount : 0,
                BatchMaxBytes = hdd ? GeZi.Core.Support.DownloadSizing.HddBatchMaxBytes : 0,
                // 🚨 2026-11-07：注入「删除并移除」意图查询，取消分支据此不再写续传快照。
                //    默认 false，普通暂停/取消行为逐字不变。
                IsDeleteRemoveRequested = () => job.DeleteAndRemoveRequested,
                // 🚨 2026-11-07：每个**任务**一个容忍闸门，绝不跨任务共享 ——
                //    共享会让 A 任务的偶发 200 累加到 B 任务上，把好好的任务作废。
                //    语义见 RangeDowngradeGate：窗口内 ≤3 次只重试，超过才判致命。
                RangeDowngrade = new GeZi.Core.Support.RangeDowngradeGate(),
            };
        }

        /// <summary>把 bezzad 的 PauseToken 状态映射到服务的 Pause()/Resume()。</summary>
        private static async Task WatchPauseAsync(
            DownloadService service, PauseToken pause, CancellationToken stop)
        {
            if (pause == null)
            {
                return;
            }

            bool applied = false;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    bool wantPause = pause.IsPaused;
                    if (wantPause != applied)
                    {
                        try
                        {
                            if (wantPause)
                            {
                                service.Pause();
                            }
                            else
                            {
                                service.Resume();
                            }
                            applied = wantPause;
                        }
                        catch
                        {
                            // 服务已结束时调用 Pause/Resume 可能抛，忽略。
                        }
                    }
                    // 🚨 2026-10-28：轮询间隔 100ms → 25ms。
                    // 这是「点了暂停没反应」三段延迟里的第一段：
                    //   ① 这里发现「该暂停了」最多等一个轮询周期；
                    //   ② service.Pause() 后 worker 要等当前 64KB 读完才会走到
                    //      ChunkDownloader.ReadStream 顶部的 WaitWhilePausedAsync；
                    //   ③ 写队列里已缓冲的数据还会继续落盘（ConcurrentStream 没有暂停概念）。
                    // 25ms 让第一段基本不可感知（原来 100ms 叠上后两段就是用户说的「慢半拍」）。
                    // 开销：一个 25ms 定时循环，几乎为 0；Pause 只在状态变化时才真正调用。
                    await Task.Delay(25, stop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
        }

        /// <summary>直链失效原因分类。只有「签名/授权类」才值得重新取链。</summary>
        private enum LinkRefreshReason
        {
            None = 0,
            /// <summary>401 未授权。</summary>
            Unauthorized,
            /// <summary>403 签名过期 / 防盗链。</summary>
            Forbidden,
            /// <summary>410 已失效：服务端判定该链作废（过期链常见返回）。</summary>
            Gone,
            /// <summary>412 前置条件失败（夸克常用它表示签名校验不过）。</summary>
            PreconditionFailed,
            /// <summary>HTTP 200 但内容不对：服务端返回了错误页，表现为长度与源大小不符。</summary>
            LengthMismatch,
        }

        /// <summary>
        /// 「HTTP 200 但内容不是文件」。
        /// <para>
        /// 过期直链最常见的表现并不是 403 —— 有些 CDN 直接回 200 + 一个几十字节的错误页，
        /// 框架会把它当成正常下完，只有「目标文件长度 ≠ 源大小」能看出来。
        /// 单独建类型是为了让 <see cref="ClassifyLinkError"/> 能识别它并触发重新取链，
        /// 否则用户看到的是「下载完成」但文件根本打不开。
        /// </para>
        /// </summary>
        private sealed class LinkLengthMismatchException : Exception
        {
            public LinkLengthMismatchException(string message) : base(message) { }
        }

        /// <summary>
        /// 从异常里判出「是不是签名过期」。
        /// <para>
        /// 🚨 优先看 <see cref="System.Net.WebException.Response"/> 的状态码（最可靠），
        /// 拿不到再退回解析消息里的 <c>HTTP 4xx</c>。
        /// 绝不能只做字符串包含判断 —— 那会把「文件不存在(404)」也当成可自愈。
        /// </para>
        /// </summary>
        private static LinkRefreshReason ClassifyLinkError(Exception ex)
        {
            if (ex == null)
            {
                return LinkRefreshReason.None;
            }

            // 「200 + 错误页」是我们自己造出来的伪异常，没有状态码可看，直接认它。
            if (ex is LinkLengthMismatchException)
            {
                return LinkRefreshReason.LengthMismatch;
            }

            int code = 0;
            var we = ex as System.Net.WebException;
            if (we != null && we.Response != null)
            {
                var resp = we.Response as System.Net.HttpWebResponse;
                if (resp != null)
                {
                    code = (int)resp.StatusCode;
                }
            }

            if (code == 0)
            {
                // 框架内部有时把状态码拼进消息（例如「HTTP 403 ...」），退回解析。
                var m = Regex.Match(ex.Message ?? "", @"HTTP\s+(\d{3})");
                if (m.Success)
                {
                    int.TryParse(m.Groups[1].Value, out code);
                }
            }

            switch (code)
            {
                case 401: return LinkRefreshReason.Unauthorized;
                case 403: return LinkRefreshReason.Forbidden;
                case 410: return LinkRefreshReason.Gone;
                case 412: return LinkRefreshReason.PreconditionFailed;
                default: return LinkRefreshReason.None;
            }
        }

        private static string DescribeRefreshReason(LinkRefreshReason reason)
        {
            switch (reason)
            {
                case LinkRefreshReason.Unauthorized: return "HTTP 401 未授权";
                case LinkRefreshReason.Forbidden: return "HTTP 403 签名过期/防盗链";
                case LinkRefreshReason.Gone: return "HTTP 410 直链已失效";
                case LinkRefreshReason.PreconditionFailed: return "HTTP 412 前置条件失败";
                case LinkRefreshReason.LengthMismatch: return "返回内容不是文件（疑似错误页）";
                default: return "非签名类错误";
            }
        }

        /// <summary>重新取链次数的默认值（小文件）。</summary>
        private const int MaxLinkRefreshes = 5;

        /// <summary>
        /// 重新取链次数的硬上限（防死循环）。
        /// <para>
        /// 🚨 2026-10-30 从 60 提到 400：上限必须能覆盖「低速大文件」的最坏情况。
        /// 实测 200 KB/s 下 9.5 GB 需要约 156 次刷新，原来的 60 次必然中途失败。
        /// 真正的死循环由 <see cref="MaxQuickFailStreak"/> 兜住，不靠这个数字。
        /// </para>
        /// </summary>
        private const int MaxLinkRefreshesCap = 400;

        /// <summary>直链 TTL（秒）。取自 <see cref="GeZi.Core.Support.LinkLifetime.ServerSeconds"/>，全局唯一来源。</summary>
        private const int LinkTtlSeconds = GeZi.Core.Support.LinkLifetime.ServerSeconds;

        /// <summary>拿不到实测速度时的保守假设（字节/秒）。</summary>
        private const double AssumedBytesPerSecond = 2.0 * 1024 * 1024;

        /// <summary>
        /// 连续多少次「刷新后很快又失败」就放弃。
        /// <para>
        /// 刷新后几秒内就再次失败，说明问题不在 TTL 到期（比如文件被删、权限不足、CDN 屏蔽），
        /// 这时再刷新多少次都没用，必须尽快失败把原因告诉用户。
        /// </para>
        /// </summary>
        private const int MaxQuickFailStreak = 3;

        /// <summary>低于这个时长就失败 = 「快速失败」，不算 TTL 到期。</summary>
        private const double QuickFailWindowSeconds = 30.0;

        /// <summary>
        /// 按文件大小与实测速度估算需要多少次重取链。
        /// <para>
        /// 🚨 2026-10-30 改为按**实测速度**算。原实现固定按 2 MB/s 估「一次能撑 600 MB」，
        /// 与「直链 TTL 约 300 秒」对不上：2 MB/s × 300 s 只有 600 MB 是巧合，
        /// 一旦实测只有 200 KB/s，一次刷新只能撑 60 MB，9.5 GB 要 156 次，
        /// 而按固定 600 MB 算出来只要 17 次 → 下到一半必然报「直链失效」。
        /// </para>
        /// <para>
        /// <paramref name="bytesPerSecond"/> 传 0 表示还没有实测数据，用保守假设值。
        /// </para>
        /// </summary>
        private static int ComputeMaxLinkRefreshes(long totalBytes, double bytesPerSecond)
        {
            if (totalBytes <= 0)
            {
                return MaxLinkRefreshes;
            }

            // 每次刷新实际能撑的时长按 TTL 的 75% 算，留 25% 余量避免贴着过期边界失败。
            double speed = bytesPerSecond > 0 ? bytesPerSecond : AssumedBytesPerSecond;
            double bytesPerRefresh = speed * (LinkTtlSeconds * 0.75);
            if (bytesPerRefresh < 1.0)
            {
                bytesPerRefresh = 1.0;
            }

            long need = (long)(totalBytes / bytesPerRefresh) + 1;
            if (need < MaxLinkRefreshes)
            {
                need = MaxLinkRefreshes;
            }
            if (need > MaxLinkRefreshesCap)
            {
                need = MaxLinkRefreshesCap;
            }
            return (int)need;
        }

        /// <summary>读目标文件当前长度；读不到返回 -1。</summary>
        private static long TryFileLength(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists ? fi.Length : -1;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>把异常翻译成用户看得懂的一句话（避免把英文堆栈丢到界面上）。</summary>
        private static string DescribeFailure(Exception ex)
        {
            var we = ex as System.Net.WebException;
            if (we != null)
            {
                if (we.Response != null)
                {
                    var resp = we.Response as System.Net.HttpWebResponse;
                    if (resp != null)
                    {
                        int code = (int)resp.StatusCode;
                        if (code == 401 || code == 403)
                        {
                            return string.Format("HTTP {0}：直链已失效或没有权限，请重新取链。", code);
                        }
                        if (code == 404 || code == 410)
                        {
                            return string.Format("HTTP {0}：文件在服务器上已不存在。", code);
                        }
                        return string.Format("HTTP {0}：{1}", code, resp.StatusDescription);
                    }
                }

                // 🚨 没有 HTTP 响应 = 根本没连上（DNS / TCP / TLS / 超时）。
                // 这条分支以前是漏的：直接落到最下面的 return ex.Message，
                // 于是界面上出现「未能解析此远程名称: 'dl-pc-sz-cf.pds.uc.cn'」这种英文原文。
                // 注意 WebException 继承的是 InvalidOperationException（不是 IOException），
                // 所以它不会掉进下面的「写盘失败」分支 —— 必须在 WebException 分支内处理干净。
                switch (we.Status)
                {
                    case System.Net.WebExceptionStatus.NameResolutionFailure:
                    case System.Net.WebExceptionStatus.ProxyNameResolutionFailure:
                        return "域名解析失败，请检查网络或代理设置。";
                    case System.Net.WebExceptionStatus.ConnectFailure:
                        return "无法连接下载服务器，请检查网络或代理设置。";
                    case System.Net.WebExceptionStatus.Timeout:
                        return "连接下载服务器超时，请稍后重试。";
                    case System.Net.WebExceptionStatus.TrustFailure:
                        return "HTTPS 证书校验失败，网络可能被劫持。";
                    case System.Net.WebExceptionStatus.SecureChannelFailure:
                        return "TLS 握手失败，请检查系统时间或代理设置。";
                }

                if (we.InnerException != null && !string.IsNullOrEmpty(we.InnerException.Message))
                {
                    return "下载失败：" + we.InnerException.Message;
                }
                return "下载失败：" + (string.IsNullOrEmpty(we.Message) ? "网络错误" : we.Message);
            }

            if (ex is InvalidDataException)
            {
                return "内容校验失败：" + ex.Message;
            }

            if (ex is IOException)
            {
                return "写盘失败：" + ex.Message;
            }

            string msg = ex.Message;
            if (string.IsNullOrEmpty(msg))
            {
                return "下载失败（无错误信息）";
            }
            return msg;
        }
    }
}
