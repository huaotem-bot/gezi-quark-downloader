// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Support;

namespace GeZi.Core.Download
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
    /// 批量下载调度器：限制并发任务数，每个任务内部再用 SegmentedDownloader 分片下载。
    /// 进度通过 IProgress&lt;JobUpdate&gt; 上报（Progress&lt;T&gt; 自动切回 UI 线程）。
    /// </summary>
    public class DownloadScheduler : IDisposable
    {
        private readonly SemaphoreSlim _sem;

        /// <summary>诊断日志出口（转给每个 SegmentedDownloader）。</summary>
        public Action<string> Log { get; set; }

        /// <summary>
        /// 结构化诊断出口。由 UI 层的诊断面板挂上，转发自各下载器的
        /// <see cref="SegmentedDownloader.Diagnostics"/>。见其注释。
        /// </summary>
        public Action<DiagnosticsSnapshot> Diagnostics { get; set; }

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
            _sem = new SemaphoreSlim(Math.Max(1, maxConcurrent));
            NetworkConfig.Apply(connBudget);
        }

        public async Task RunAllAsync(IReadOnlyList<DownloadJob> jobs, IProgress<JobUpdate> progress, CancellationToken globalCancel)
        {
            var tasks = jobs.Select(job => RunOneAsync(job, _sem, progress, globalCancel)).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public void Dispose() => _sem.Dispose();

        private async Task RunOneAsync(DownloadJob job, SemaphoreSlim sem, IProgress<JobUpdate> progress, CancellationToken globalCancel)
        {
            await sem.WaitAsync(globalCancel).ConfigureAwait(false);

            // 同路径互斥：抢不到就跳过（不排队），避免两个下载器同时写同一个文件。
            //
            // ⚠️ 必须用 owned 标记：只有**自己登记成功**才允许在 finally 里移除。
            // 若无条件移除，TryAdd 失败的那一方会把**别人**持有的锁删掉，
            // 互斥等于失效 —— 那比不做还危险（会给出"已保护"的假象）。
            string destKey = DestKey(job.Dest);
            bool owned = false;
            try
            {
                if (!_activeDests.TryAdd(destKey, 0))
                {
                    Report(progress, job, JobState.Failed, "已跳过：同一文件正在下载中");
                    return;
                }
                owned = true;

                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(globalCancel, job.Cancel.Token))
                {
                    Report(progress, job, JobState.Downloading);
                    // 分片根目录只对「独立分片」模式生效；单一文件（共写）的续传
                    // 记账跟随目标文件（dest + ".qmeta"），不进 PartsRoot。
                    using (var dl = new SegmentedDownloader(threads: job.Threads, cookie: job.Cookie,
                               partsRoot: job.WriteMode == WriteMode.SegmentedFiles ? job.PartsRoot : null,
                               writeMode: job.WriteMode))
                    {
                        dl.Log = Log;
                        dl.Diagnostics = Diagnostics;
                        dl.LinkRefresher = job.LinkRefresher;
                        // 免转存任务：失败文案不能说"可续传"（stoken 过期就取不到新链）
                        dl.ResumeCannotRefresh = job.ResumeCannotRefresh;
                        var perFile = new JobProgressAdapter(job, progress);
                        var res = await dl.DownloadAsync(job.Url, job.Dest, perFile, linked.Token, job.Pause).ConfigureAwait(false);

                        if (res.Ok)
                            Report(progress, job, JobState.Completed);
                        else if (linked.IsCancellationRequested)
                            Report(progress, job, JobState.Cancelled, res.Message);
                        else
                            Report(progress, job, JobState.Failed, res.Message);
                    }
                }
            }
            finally
            {
                // 下载结束（含成功/失败/取消/异常）必然走到这里，
                // 立即释放目标路径，供"删除后重下"继续。
                if (owned)
                {
                    byte _;
                    _activeDests.TryRemove(destKey, out _);
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
