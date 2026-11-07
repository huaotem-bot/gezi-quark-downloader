// GeZi —— 多网盘下载器 (Multi-cloud downloader)
// Copyright (C) 2026  Yi Yuan
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/agpl-3.0>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Support;
using GeZi.Core.Api;
using GeZi.Core.Models;
using GeZi.DownloadEngine.Framework;
using GeZi.DownloadEngine.Storage;

namespace GeZi.DownloadEngine
{
    public enum JobState
    {
        Queued,
        Downloading,
        Paused,
        Completed,
        Failed,
        Cancelled,

        /// <summary>
        /// 「已请求取消，但下载器还没真正退出」的中间态。
        ///
        /// 为什么需要它：<see cref="DownloadJob.CancelJob"/> 只是发一个取消信号，
        /// 而 worker 可能正卡在 ReadAsync 里等数据，要等那次读返回才检查取消标志 ——
        /// 所以状态**不能立即**变成 Cancelled。
        /// 在这个窗口期里，任务看起来还是"下载中"，用户点「清空已完成」清不掉它，
        /// 会以为按钮坏了。
        ///
        /// 由 **UI 层**在调用 CancelJob 之后立即置位；下载器真正退出后，
        /// Core 会回一个 Cancelled 把它覆盖掉。Core 自身不会产生这个状态。
        /// </summary>
        Cancelling,
    }

    /// <summary>任务状态/进度更新事件。</summary>
    public class JobUpdate
    {
        public DownloadJob Job { get; set; }
        public JobState State { get; set; }
        public DownloadProgress Progress { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// 批量下载调度器：限制并发任务数，每个任务内部再用融合引擎分片下载。
    /// 进度通过 IProgress&lt;JobUpdate&gt; 上报（Progress&lt;T&gt; 自动切回 UI 线程）。
    /// </summary>
    public class DownloadScheduler : IDisposable
    {
        private readonly SemaphoreSlim _sem;

        /// <summary>同时下载任务数上限（构造时固定）。用于把连接预算按任务数摊开。</summary>
        private readonly int _maxConcurrent;

        /// <summary>
        /// 全局连接预算租约。UI 的「按文件大小分配」只是每个任务的**期望值**，
        /// 这里才是**硬上限**：多个批次（续传批次 + 新下载批次）、甚至多个
        /// <see cref="DownloadScheduler"/> 实例同时存在时，所有在飞任务的连接额度
        /// 合计也不会超过 <see cref="ThreadAllocator.DefaultBudget"/>。
        /// 刻意做成静态：目标是账号级总连接数，不是某个调度器实例的局部预算。
        /// </summary>
        private static readonly SemaphoreSlim GlobalConnLease =
            new SemaphoreSlim(ThreadAllocator.DefaultBudget, ThreadAllocator.DefaultBudget);

        /// <summary>诊断日志出口（转给融合引擎）。</summary>
        public Action<string> Log { get; set; }

        /// <summary>
        /// 结构化诊断出口。由 UI 层的诊断面板挂上，转发自融合引擎的进度事件。
        /// </summary>
        public Action<DiagnosticsSnapshot> Diagnostics { get; set; }

        /// <summary>
        /// 融合引擎的落盘策略。
        /// <para><c>null</c> = <b>自动</b>（默认）：按目标磁盘介质挑 ——
        /// 固态盘用随机写（无合并阶段，写完即完），机械盘用顺序写（全程顺序 I/O）。</para>
        /// <para>设成具体值 = 用户在设置页手动覆盖，强制使用该策略。</para>
        /// </summary>
        public GeZi.DownloadEngine.Storage.StorageMode? FusionStorageMode { get; set; }

        /// <summary>融合引擎顺序落盘的重排窗口；0 = 默认 32 MB。</summary>
        public long FusionSequentialWindowBytes { get; set; }

        /// <summary>
        /// 目标在机械盘时的并发上限。
        ///
        /// <para>
        /// HDD 上「并发越低越快」，与固态盘完全相反：写合并只能把「每 64 KB 跳一次」
        /// 变成「每 2 MB 跳一次」，跳的次数仍与并发数同阶。512 条连接时每 4 MB
        /// 要 64 次寻道（约 10 ms/次），上限约 6 MB/s；降到 32 条后每 32 MB
        /// 才 32 次寻道，上限约 60 MB/s。
        /// </para>
        /// <para>
        /// 默认 <b>128</b>：介于「磁头被打散」与「吃不满宽带」之间。
        /// 设置页把它做成了 32 / 64 / 96 / 128 / 192 / 256 六档
        /// （<c>HddThreadPresets</c>，默认第 4 档 128），用户可按自己的盘调。
        /// 越往 32 走寻道越少、上限越高但单连接带宽吃不满；
        /// 越往 256 走越接近固态盘行为，机械盘上会明显掉速。
        /// </para>
        /// </summary>
        public int HddMaxConcurrency { get; set; } = 128;

        // ==================== 同路径互斥 ====================
        //
        // 移植自 Python 版 core 的 `_dwlock` / `_active_dests`（core L2807 / L4843）。
        //
        // 为什么必须有：共写模式下，两个下载器会各自 seek 到不同偏移写**同一个文件**。
        // 典型触发场景是"删完重下"——旧任务还没真正退出，新任务已经起来了，
        // 两组 worker 交叉写同一文件 → 进度错乱、文件损坏。
        // **这不是体验问题，是数据安全问题。**
        //
        // 语义与 Python 版一致：冲突时**跳过**（不排队等待），把原因回给 UI。
        // key 用绝对路径；Windows 下路径大小写不敏感，故用 OrdinalIgnoreCase。
        private static readonly ConcurrentDictionary<string, byte> _activeDests =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        /// <summary>把目标路径规范成互斥用的 key（取绝对路径，失败则退回原串）。</summary>
        private static string DestKey(string dest)
        {
            try { return Path.GetFullPath(dest); }
            catch { return dest; }
        }

        public DownloadScheduler(int maxConcurrent = 3, int connBudget = 512)
        {
            _maxConcurrent = Math.Max(1, maxConcurrent);
            _sem = new SemaphoreSlim(_maxConcurrent);
            NetworkConfig.Apply(connBudget);
        }

        /// <summary>全局连接预算上限。仅供诊断/日志。</summary>
        public int ConnectionBudget => ThreadAllocator.DefaultBudget;

        /// <summary>当前已被在飞任务占用的连接额度（跨实例）。仅供诊断/日志。</summary>
        public int LeasedConnections => ThreadAllocator.DefaultBudget - GlobalConnLease.CurrentCount;

        public async Task RunAllAsync(IReadOnlyList<DownloadJob> jobs, IProgress<JobUpdate> progress, CancellationToken globalCancel)
        {
            // 批次规模一次算清：份额由「本批任务数」决定，而不是各任务自己去读
            // sem.CurrentCount —— 三个任务几乎同时进来时，先到的那个读到的
            // CurrentCount 还是初始值，会把 running 低估成 1，于是三个任务各算
            // 256/256/170，合计 682 > 512，第 3 个任务照样要等别人释放。
            // 用批次大小做分母，份额确定且合计不超过预算。
            // 🚨🚨 2026-11-07 修「单文件下载吃不满线程（长期钉在 ~170 路）」：
            //   planned 原来是「本批入队文件数」，但取链是顺次入队的，一个批次里
            //   可能登记了 3 个任务、实际只跑 1 个。分母按 3 算 → share=512/3≈170、
            //   softCap=340，再叠加「超出 16 条只拿空闲额度」的循环，
            //   单任务实际只能拿到 ~170 条租约（日志实测在飞 163~177）。
            //   用户填 256 甚至 512 都吃不满 —— 这不是服务端限速，是这里的分母。
            //   修法：**单文件批次按 1 算分母**，让 share=512、softCap 不再压它；
            //   多任务（≥2）保持原有均分逻辑不变（已实测的预算分配不回退）。
            int planned = GeZi.Core.Support.DownloadSizing.PlannedConcurrency(jobs.Count, _maxConcurrent);
            var tasks = jobs.Select(job => RunOneAsync(job, _sem, planned, progress, globalCancel)).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public void Dispose() => _sem.Dispose();

        /// <summary>
        /// 决定这次任务用哪种落盘策略。
        /// <para><b>2026-10-24 起统一为一种：共写 + 写合并窗口</b>（大厂形态）。
        /// 预分配整个文件、多连接各写最终偏移、16 MB 批量按偏移排序后刷盘，
        /// 因此<b>没有合并阶段</b>；配合 moov 优先即可边下边看。</para>
        /// <para>机械盘的寻道代价由 <c>ConcurrentStream</c> 的 16 MB 写合并窗口
        /// 压到每批 2~4 段，不再需要按磁盘类型退回顺序写。
        /// <see cref="SequentialFileStorage"/> 实现保留作兜底，但不再自动选用。</para>
        /// <para>磁盘类型仍然探测，但只用于日志（让用户知道探测到了什么）。</para>
        /// </summary>
        private StorageMode ResolveStorageMode(DownloadJob job)
        {
            if (FusionStorageMode.HasValue) return FusionStorageMode.Value;

            DiskMediaKind kind = DiskMedia.DetectForPath(job.Dest);
            char drive;
            if (DiskMedia.TryConsumeFirstProbeForPath(job.Dest, out drive))
            {
                Log?.Invoke("[落盘] " + drive + ": 判定为 " + DiskMedia.Describe(kind)
                    + "，统一走 共写（预分配 + 16 MB 写合并，无合并阶段）");
            }
            return StorageMode.Shared;
        }

        /// <summary>
        /// 正在下载的融合引擎服务实例，按目标路径索引。
        ///
        /// 用途：UI 点「边下边看」时要知道**哪个** DownloadService 在写这个文件，
        /// 才能调它的 SetPriorityWindow 把播放位置附近的分片提前。
        /// 表里只有融合引擎的实例。
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DownloadService> _activeServices =
            new System.Collections.Concurrent.ConcurrentDictionary<string, DownloadService>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 还没开始下载就点了「边下边看」的文件：记下播放位置，等服务创建后补设窗口。
        /// key = 目标路径，value = 播放偏移。
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _playbackHints =
            new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 设置某个文件的播放优先级窗口（边下边看）。
        ///
        /// 返回 false 表示该文件当前没有在下载（或走的是老引擎），调用方忽略即可。
        /// 线程安全，可从 UI 线程直接调用。
        /// </summary>
        public bool SetPlaybackPriority(string destPath, long offset, long length)
        {
            if (string.IsNullOrEmpty(destPath))
                return false;

            DownloadService svc;
            if (!_activeServices.TryGetValue(destPath, out svc) || svc == null)
            {
                // 还没开始下载：先记下位置，等服务实例一创建就补设。
                _playbackHints[destPath] = offset;
                return false;
            }

            svc.SetPriorityWindow(offset, length);
            return true;
        }

        /// <summary>取消某个文件的播放优先级（恢复纯 FIFO）。</summary>
        public void ClearPlaybackPriority(string destPath)
        {
            DownloadService svc;
            if (!string.IsNullOrEmpty(destPath) && _activeServices.TryGetValue(destPath, out svc) && svc != null)
                svc.SetPriorityWindow(-1, 0);
        }

        private async Task RunOneAsync(DownloadJob job, SemaphoreSlim sem, int plannedConcurrency, IProgress<JobUpdate> progress, CancellationToken globalCancel)
        {
            await sem.WaitAsync(globalCancel).ConfigureAwait(false);

            // 同路径互斥：抢不到就跳过（不排队），避免两个下载器同时写同一个文件。
            //
            // ⚠️ 必须用 owned 标记：只有**自己登记成功**才允许在 finally 里移除。
            // 若无条件移除，TryAdd 失败的那一方会把**别人**持有的锁删掉，
            // 互斥等于失效 —— 那比不做还危险（会给出"已保护"的假象）。
            string destKey = DestKey(job.Dest);
            bool owned = false;
            int leased = 0;
            try
            {
                if (!_activeDests.TryAdd(destKey, 0))
                {
                    Report(progress, job, JobState.Failed, "已跳过：同一文件正在下载中");
                    return;
                }
                owned = true;

                // ---- 全局连接预算租约 ----
                // 申请“期望值”个额度；额度不足就在这里等前一个任务释放，
                // 而不是让三个任务各开 256 路把账号连接数顶穿。
                int want = job.Threads > 0 ? job.Threads : QuarkConstants.DownloadThreads;
                if (want > ThreadAllocator.DefaultBudget) want = ThreadAllocator.DefaultBudget;

                // 🚨 机械盘限制并发（2026-10-24 补的缺口）。
                // HDD 的随机写代价是寻道的物理限制：512 条连接各写各的位置，
                // 写合并再强也压不住磁头来回跳。实测推断（本机无 HDD，按寻道 10 ms 估算）：
                //   512 并发 → 每 4 MB 约 64 次寻道 → 上限约 6 MB/s；
                //    32 并发 → 每 32 MB 约 32 次寻道 → 上限约 60 MB/s。
                // 也就是说机械盘上「并发越低越快」，与固态盘完全相反。
                // 32 是折中值：既能吃满家用宽带，又不会把磁头打散。
                if (DiskMedia.DetectForPath(job.Dest) == DiskMediaKind.Hdd && want > HddMaxConcurrency)
                {
                    Log("[落盘] 目标在机械盘，并发从 " + want + " 降到 " + HddMaxConcurrency
                        + "（HDD 随机写受寻道限制，并发越低越快）");
                    want = HddMaxConcurrency;
                }

                // 🚨 按网盘封顶（2026-10-25 补的真 bug）。
                // 迅雷单 IP 并发上限约 16：137 个分片全开时 CDN 大面积 503，
                // 退避重试把速度压到 1 MB/s；16 并发实测能到 12.98 MB/s。
                // 阿里云盘单 IP 约 2.5 MB/s 硬限速，64 并发已够。
                // 夸克/UC 不限（MaxParallelCount = 0），256 并发能跑满带宽。
                if (job.MaxParallelCount > 0 && want > job.MaxParallelCount)
                {
                    Log("[并发] 该网盘上限 " + job.MaxParallelCount + " 条连接，本任务 "
                        + want + " -> " + job.MaxParallelCount
                        + "（服务端按单 IP 限流，并发过高会被 503 拒绝、反而更慢）");
                    want = job.MaxParallelCount;
                }

                // 🚨 连接预算均分（2026-10-24 补）。
                // 原来每个任务都按 job.Threads（默认 256）申请租约，而总预算只有 512：
                // 3 个任务 = 768 > 512 -> 第 3 个任务会一直阻塞在 WaitAsync 上，
                // 界面上就是「排队中 / 0 B/s」，看起来像卡死。
                // 这里按「当前在跑的任务数」把预算摊开，让所有任务都能同时推进。
                // 16 是保底（再少就真的慢了）。
                //
                // 🚨 这里原来还有一个 `const int RecommendedMaxThreads = 256`，
                // 会把「设置页填 512」永远压回 256 —— 用户看到任务显示 256/256 连接，
                // 以为自己的设置根本没生效（实测就是这么报的）。
                // 预算均分本身已经保证总量不超 DefaultBudget：
                //   1 个任务 → 512、2 个任务 → 256、3 个任务 → 170
                // 所以不需要再额外夹一刀；用户填多少，单任务就该拿多少（受总预算约束）。
                const int MinPerFileThreads = 16;
                int budget = ThreadAllocator.DefaultBudget;
                int running = plannedConcurrency;
                if (running < 1) running = 1;
                int share = budget / running;
                if (share < MinPerFileThreads) share = MinPerFileThreads;

                // 🚨 2026-10-25 修正：这里原来把 want **硬压到 share**（= budget / 同时任务数），
                // 会把 ThreadAllocator 按「大小比例 + waves 放大」算好的结果拍平，造成预算闲置。
                // 实测（tmp-alloc-probe.py 复现算法）：
                //   20GB + 5GB + 100MB 三个同时跑（planned=3, share=170）
                //     Allocate → 256 / 249 / 6 = 511（正好用满预算、比例也对）
                //     硬压后   → 170 / 170 / 6 = 346  ← **闲置 166 条预算（32%）**
                //   也就是说大文件少拿 33% 连接，速度直接受影响；
                //   而 ThreadAllocator 里那段「水位填充」（:133-168）费力解决的
                //   「160MB 小文件只有 4 路」问题，也会被这里重新打回去。
                //
                // 现在改成**软上限**：允许单任务超过平均份额，最多到 2× 平均份额
                // （防止一个超大文件把整批吃光）；真正的硬上限仍然是
                // GlobalConnLease 的 512 条静态租约，所以不会超配额。
                // 等大文件场景（7×3.2GB, planned=3）实测总量 510→512，基本无变化，
                // 所以这个放宽不会让「等大文件」变差。
                int softCap = share * 2;
                if (softCap > budget) softCap = budget;
                if (want > softCap)
                {
                    Log("[并发] " + running + " 个任务共享 " + budget + " 条连接，本任务 " + want + " -> " + softCap);
                    want = softCap;
                }

                // 🚨 2026-11-07：把「申请值 / 实际拿到值」记下来（纯日志、零行为）。
                //    用户问「吃不满线程」时，这条能直接区分：
                //      · leased ≈ want → 客户端开满了，慢是服务端配额；
                //      · leased ≪ want → 客户端被预算/分母卡住，需查这里。
                // 保底 16 条阻塞拿（保证任务能开始）；超出部分只拿空闲额度，绝不空等。
                // 否则又会出现「第 3 个任务永远轮不到」的情况。
                int wantedLease = want;
                for (int i = 0; i < want; i++)
                {
                    if (i < MinPerFileThreads)
                    {
                        await GlobalConnLease.WaitAsync(globalCancel).ConfigureAwait(false);
                        leased++;
                        continue;
                    }
                    if (!await GlobalConnLease.WaitAsync(0, globalCancel).ConfigureAwait(false))
                        break;
                    leased++;
                }

                if (Log != null)
                    Log("[并发] 连接租约：申请 " + wantedLease + "，实得 " + leased
                        + "（总预算 " + ThreadAllocator.DefaultBudget + "，同批计划 " + plannedConcurrency + " 个任务）");

                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(globalCancel, job.Cancel.Token))
                {
                    Report(progress, job, JobState.Downloading);

                    var perFile = new JobProgressAdapter(job, progress);
                    DownloadResult res;

                    // 融合引擎：bezzad 调度框架 + 我们的分片/传输/落盘/校验零件。
                    // 它自己管分片并发，所以不走 using 块。
                    StorageMode resolvedMode = ResolveStorageMode(job);
                    res = await FusionDownloader.DownloadAsync(
                        job, leased, perFile, linked.Token, job.Pause,
                        resolvedMode, FusionSequentialWindowBytes,
                        job.Md5, job.Sha1, job.Sha256, Log,
                        svc =>
                        {
                            // 登记到表里：UI 才能在下载过程中随时调整播放优先级。
                            _activeServices[destKey] = svc;
                            // 下单前就设过窗口的（用户先点播放再开始下载），这里补一次。
                            long ps, pl;
                            if (_playbackHints.TryGetValue(destKey, out ps))
                            {
                                _playbackHints.TryRemove(destKey, out pl);
                                svc.SetPriorityWindow(ps, 64L * 1024 * 1024);
                            }
                        },
                        Diagnostics).ConfigureAwait(false);

                    if (res.Ok)
                        // 把校验结果带到 UI：完成不等于“已验证”，用户要能一眼看到
                        // “内容校验通过”还是“未校验（接口没给哈希）”。
                        Report(progress, job, JobState.Completed, res.Message);
                    else if (linked.IsCancellationRequested)
                        Report(progress, job, JobState.Cancelled, res.Message);
                    else
                        Report(progress, job, JobState.Failed, res.Message);
                }
            }
            finally
            {
                // 先还连接额度，再放并发槽：下一个任务一进来就能拿到刚释放的额度。
                if (leased > 0)
                    GlobalConnLease.Release(leased);
                // 下载结束（含成功/失败/取消/异常）必然走到这里，
                // 立即释放目标路径，供"删除后重下"继续。
                if (owned)
                {
                    byte _;
                    _activeDests.TryRemove(destKey, out _);
                }
                // 服务实例必须摘掉，否则续传/重下时 UI 会拿到已完成的旧实例
                {
                    DownloadService dead;
                    _activeServices.TryRemove(destKey, out dead);
                }
                sem.Release();
            }
        }

        private static void Report(IProgress<JobUpdate> progress, DownloadJob job, JobState state, string error = "")
        {
            progress?.Report(new JobUpdate { Job = job, State = state, Error = error });
        }

        private sealed class JobProgressAdapter : IProgress<DownloadProgress>
        {
            private readonly DownloadJob _job;
            private readonly IProgress<JobUpdate> _outer;

            public JobProgressAdapter(DownloadJob job, IProgress<JobUpdate> outer)
            {
                _job = job;
                _outer = outer;
            }

            public void Report(DownloadProgress value)
            {
                _outer?.Report(new JobUpdate { Job = _job, State = JobState.Downloading, Progress = value });
            }
        }
    }
}
