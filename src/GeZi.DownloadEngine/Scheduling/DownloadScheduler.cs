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
        /// 合计也不会超过各自的网盘预算。
        /// 刻意做成静态：目标是账号/IP 级总连接数，不是某个调度器实例的局部预算。
        ///
        /// <para>
        /// 🚨🚨 2026-11-07 改造：原来是**一个** <c>SemaphoreSlim(512)</c>，四家网盘共用。
        /// 但四家的服务端配额是各自独立结算的（夸克按账号、UC/阿里/迅雷按单 IP），
        /// 共用一份预算只会让它们互相侵占 —— 典型症状：
        /// 「夸克设 512 + 迅雷设 16 同时下载」时迅雷先占住额度，
        /// 夸克再被「不超过平均份额 2 倍」夹一道，**永远拿不到 512**。
        /// 现在拆成四个独立信号量，各拿各的上限，互不影响。
        /// </para>
        /// <para>
        /// 各家的数字与 UI 层 <c>MainWindow.MaxParallelFor</c> 逐字一致
        /// （见 <see cref="GeZi.Core.Support.PanConnBudget"/>）：夸克 512 / UC 256 / 阿里 64 / 迅雷 16。
        /// </para>
        /// </summary>
        private static readonly SemaphoreSlim[] PanConnLease = new SemaphoreSlim[]
        {
            new SemaphoreSlim(GeZi.Core.Support.PanConnBudget.Quark, GeZi.Core.Support.PanConnBudget.Quark),
            new SemaphoreSlim(GeZi.Core.Support.PanConnBudget.Uc, GeZi.Core.Support.PanConnBudget.Uc),
            new SemaphoreSlim(GeZi.Core.Support.PanConnBudget.Aliyun, GeZi.Core.Support.PanConnBudget.Aliyun),
            new SemaphoreSlim(GeZi.Core.Support.PanConnBudget.Xunlei, GeZi.Core.Support.PanConnBudget.Xunlei),
        };

        /// <summary>
        /// 每个网盘当前**正在跑**的任务数（用于算单任务平均份额与阻塞下限）。
        /// 只统计真正进了 <see cref="RunOneAsync"/> 主体、已经拿到并发槽的任务。
        /// </summary>
        private static readonly int[] PanRunning = new int[GeZi.Core.Support.PanConnBudget.PanCount];

        /// <summary>
        /// 每个**磁盘**当前正在跑的任务数（用于机械盘并发按盘聚合）。
        ///
        /// <para>
        /// 🚨🚨 2026-11-07 新增：原来机械盘限制是**逐任务**判定的
        /// （<c>DiskMedia.DetectForPath(job.Dest) == Hdd</c> 就把自己要的并发夹到 128），
        /// 谁也不知道别人也在同一块盘上。于是 3 个任务写同一块 HDD = 3×128 = 384 路
        /// 随机写同一颗磁头，比单任务 512 路还糟（三个任务各有各的写合并窗口，
        /// 磁头要在三套偏移之间来回跳）。机械盘是寻道物理限制，加连接只会更慢。
        /// </para>
        /// <para>
        /// 现在按盘符聚合：同一块盘上 N 个任务时，单任务软上限 =
        /// min(盘配额 ÷ N × 2, 盘配额)。N=1 时结果与改造前**逐字等价**。
        /// </para>
        /// <para>
        /// 键用 <see cref="GeZi.Core.Support.DiskConnBudget.DriveKey"/>（大写盘符 / UNC 共享名）。
        /// 用 <see cref="StringComparer.OrdinalIgnoreCase"/> 避免大小写重复计数。
        /// </para>
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> DiskRunning =
            new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>登记一个任务进入某块盘（返回进入后的在跑数）。</summary>
        private static int EnterDisk(string path)
        {
            string key = GeZi.Core.Support.DiskConnBudget.DriveKey(path);
            return DiskRunning.AddOrUpdate(key, 1, (k, v) => v + 1);
        }

        /// <summary>撤销登记（<c>&lt; 0</c> 时归零，绝不出现负数导致份额算错）。</summary>
        private static void LeaveDisk(string path)
        {
            string key = GeZi.Core.Support.DiskConnBudget.DriveKey(path);
            try
            {
                DiskRunning.AddOrUpdate(key, 0, (k, v) => v > 1 ? v - 1 : 0);
            }
            catch
            {
                // 计数器异常绝不拖垮下载
            }
        }

        /// <summary>该盘上当前在跑的任务数（&lt; 1 时返回 1，避免除零）。</summary>
        private static int RunningOnDisk(string path)
        {
            string key = GeZi.Core.Support.DiskConnBudget.DriveKey(path);
            int n;
            if (DiskRunning.TryGetValue(key, out n) && n > 0) return n;
            return 1;
        }

        /// <summary>取该网盘的租约信号量（越界按夸克）。</summary>
        private static SemaphoreSlim LeaseOf(int panKind)
        {
            return PanConnLease[GeZi.Core.Support.PanConnBudget.NormalizePanKind(panKind)];
        }

        /// <summary>登记一个任务进入本网盘（返回进入后的在跑数，供算份额）。</summary>
        private static int EnterPan(int panKind)
        {
            int idx = GeZi.Core.Support.PanConnBudget.NormalizePanKind(panKind);
            return System.Threading.Interlocked.Increment(ref PanRunning[idx]);
        }

        /// <summary>登记一个任务离开本网盘。</summary>
        private static void LeavePan(int panKind)
        {
            int idx = GeZi.Core.Support.PanConnBudget.NormalizePanKind(panKind);
            int v = System.Threading.Interlocked.Decrement(ref PanRunning[idx]);
            if (v < 0) System.Threading.Interlocked.Exchange(ref PanRunning[idx], 0);
        }

        /// <summary>本网盘当前在跑几个任务（至少 1）。</summary>
        private static int RunningInPan(int panKind)
        {
            int idx = GeZi.Core.Support.PanConnBudget.NormalizePanKind(panKind);
            int v = Volatile.Read(ref PanRunning[idx]);
            return v < 1 ? 1 : v;
        }

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

        public DownloadScheduler(int maxConcurrent = 3, int connBudget = ThreadAllocator.DefaultBudget)
        {
            _maxConcurrent = Math.Max(1, maxConcurrent);
            _sem = new SemaphoreSlim(_maxConcurrent);
            NetworkConfig.Apply(connBudget);
        }

        /// <summary>全局连接预算上限。仅供诊断/日志。</summary>
        public int ConnectionBudget => ThreadAllocator.DefaultBudget;

        /// <summary>
        /// 当前已被在飞任务占用的连接额度（跨实例、四网盘合计）。仅供诊断/日志。
        /// <para>2026-11-07：由「单个全局预算」改为四网盘分别统计后求和。</para>
        /// </summary>
        public int LeasedConnections
        {
            get
            {
                int sum = 0;
                for (int i = 0; i < PanConnLease.Length; i++)
                {
                    int cap = GeZi.Core.Support.PanConnBudget.BudgetFor(i);
                    sum += cap - PanConnLease[i].CurrentCount;
                }
                return sum;
            }
        }

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
            // 进/出本网盘的「在跑任务数」必须成对：进入时在并发槽之后立刻登记，
            // 离开时无论如何都要减回去，否则该网盘的在跑数只增不减，
            // 后续任务的份额会被越摊越小（假死在跑数）。
            // ⚠️ 用 try/finally 而不是在 if 分支里登记：路径互斥失败时也必须减。
            bool panEntered = false;
            // 磁盘「在跑任务数」的成对登记标记（与 panEntered 同理，
            // 只在自己登记成功后才能在 finally 里减，否则会把别人的计数减掉）。
            bool diskEntered = false;
            try
            {
                if (!_activeDests.TryAdd(destKey, 0))
                {
                    Report(progress, job, JobState.Failed, "已跳过：同一文件正在下载中");
                    return;
                }
                owned = true;

                // 登记本网盘「在跑任务数」：必须在自己算份额**之前**登记，
                // 这样 share 里算的就是真实同网盘并发数（含自己）。
                EnterPan(job.PanKind);
                panEntered = true;

                // 登记本盘「在跑任务数」：同理，必须在自己算磁盘软上限**之前**登记。
                // 这样 3 个任务同时下到同一块 HDD 时，第三个进来看到的是 3，
                // 而不是 1（否则三个都按「本盘只有我一个」算，各拿满 128 路）。
                EnterDisk(job.Dest);
                diskEntered = true;

                // ---- 本网盘连接预算租约 ----
                // 申请“期望值”个额度；额度不足就在这里等前一个任务释放，
                // 而不是让三个任务各开 256 路把账号连接数顶穿。
                int want = job.Threads > 0 ? job.Threads : QuarkConstants.DownloadThreads;
                // 夹到「本网盘」的物理天花板（夸克 512 / UC 256 / 阿里 64 / 迅雷 16）。
                // 原来夹的是全局 512，对夸克等价，但会让「迅雷要 100」这种非法值
                // 一路走到下面的 MaxParallelCount 才被夹——日志里就会出现假的申请值。
                int panCap = GeZi.Core.Support.PanConnBudget.BudgetFor(job.PanKind);
                if (want > panCap) want = panCap;
                if (want > ThreadAllocator.DefaultBudget) want = ThreadAllocator.DefaultBudget;

                // 🚨 机械盘限制并发（2026-10-24 补的缺口，2026-11-07 改为**按盘聚合**）。
                // HDD 的随机写代价是寻道的物理限制：512 条连接各写各的位置，
                // 写合并再强也压不住磁头来回跳。实测推断（本机无 HDD，按寻道 10 ms 估算）：
                //   512 并发 → 每 4 MB 约 64 次寻道 → 上限约 6 MB/s；
                //    32 并发 → 每 32 MB 约 32 次寻道 → 上限约 60 MB/s。
                // 也就是说机械盘上「并发越低越快」，与固态盘完全相反。
                //
                // 🚨🚨 2026-11-07：原来是**逐任务**判定，3 个任务写同一块盘 = 3×128 = 384 路，
                //   比单任务 512 路还糟（三个任务各有各的 16MB 写合并窗口，磁头在三套偏移间跳）。
                //   现在按盘符聚合：单任务软上限 = min(盘配额 ÷ 本盘任务数 × 2, 盘配额)。
                //   本盘只有 1 个任务时算出 = 盘配额，**与改造前逐字等价**。
                if (DiskMedia.DetectForPath(job.Dest) == DiskMediaKind.Hdd)
                {
                    int diskCap = GeZi.Core.Support.DiskConnBudget.DiskCapFor(true, HddMaxConcurrency);
                    int diskRunning = RunningOnDisk(job.Dest);
                    int diskSoft = GeZi.Core.Support.DiskConnBudget.SoftCap(diskCap, diskRunning);
                    int before = want;
                    want = GeZi.Core.Support.DiskConnBudget.Apply(want, diskSoft);
                    if (want < before)
                    {
                        Log("[落盘] 目标在机械盘，本盘同时 " + diskRunning + " 个任务共享 "
                            + diskCap + " 条连接，本任务并发从 " + before + " 降到 " + want
                            + "（HDD 随机写受寻道限制，并发越低越快）");
                    }
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
                //
                // 🚨🚨 2026-11-07 改造：预算由「全局 512 按批均分」改为**按网盘独立结算**。
                //   原因：四家网盘的服务端配额各算各的（夸克按账号、UC/阿里/迅雷按单 IP），
                //   共用一份 512 只会互相侵占 —— 夸克设 512 + 迅雷设 16 同跑时，
                //   迅雷先占额度、夸克再被 softCap 夹一道，夸克永远拿不到 512。
                //   现在：budget = 本网盘预算（512/256/64/16），
                //         running = **本网盘**同时在跑的任务数（不再是全批任务数）。
                int panBudget = GeZi.Core.Support.PanConnBudget.BudgetFor(job.PanKind);
                int running = RunningInPan(job.PanKind);

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
                // 本网盘信号量的固定租约，所以不会超配额。
                int softCap = GeZi.Core.Support.PanConnBudget.SoftCap(panBudget, running);
                if (want > softCap)
                {
                    Log("[并发] 本网盘 " + running + " 个任务共享 " + panBudget + " 条连接，本任务 "
                        + want + " -> " + softCap);
                    want = softCap;
                }

                // 🚨 2026-11-07：把「申请值 / 实际拿到值」记下来（纯日志、零行为）。
                //    用户问「吃不满线程」时，这条能直接区分：
                //      · leased ≈ want → 客户端开满了，慢是服务端配额；
                //      · leased ≪ want → 客户端被预算/分母卡住，需查这里。
                // 保底若干条阻塞拿（保证任务能开始）；超出部分只拿空闲额度，绝不空等。
                // 否则又会出现「第 3 个任务永远轮不到」的情况。
                //
                // 🚨 blockingFloor 由「本网盘预算 ÷ 本网盘在跑任务数」夹取：
                //   迅雷预算只有 16，两个迅雷任务同时开始时若双方都阻塞要 16 条，
                //   第二个会一直等第一个整体下完（界面表现为「排队中 / 0 B/s」假死）。
                //   其余网盘（512/256/64 配 ≤6 个任务）算出来都 ≥16，与改造前逐字等价。
                int blockingFloor = GeZi.Core.Support.PanConnBudget.BlockingMinimum(panBudget, running);
                SemaphoreSlim lease = LeaseOf(job.PanKind);
                int wantedLease = want;
                for (int i = 0; i < want; i++)
                {
                    if (i < blockingFloor)
                    {
                        await lease.WaitAsync(globalCancel).ConfigureAwait(false);
                        leased++;
                        continue;
                    }
                    if (!await lease.WaitAsync(0, globalCancel).ConfigureAwait(false))
                        break;
                    leased++;
                }

                if (Log != null)
                    Log("[并发] 连接租约：申请 " + wantedLease + "，实得 " + leased
                        + "（本网盘预算 " + panBudget + "，本网盘在跑 " + running
                        + " 个任务，同批计划 " + plannedConcurrency + " 个任务）");

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
                    LeaseOf(job.PanKind).Release(leased);
                // 退出本网盘的「在跑任务数」。必须在算份额的信号量归还之后再减，
                // 否则新任务可能在旧任务还没归还额度时就按「少一个任务」算份额，
                // 拿到过大的 want 却抢不到额度（无害，但日志会对不上）。
                if (panEntered)
                    LeavePan(job.PanKind);
                // 退出本盘的「在跑任务数」。同样放在额度归还之后。
                if (diskEntered)
                    LeaveDisk(job.Dest);
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
