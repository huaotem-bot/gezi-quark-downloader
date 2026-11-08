// GeZi —— 多网盘下载器 (Multi-cloud downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/agpl-3.0>.

// ---- 本文件是 MainWindow 的 partial 之一（代码规范化拆分，2026-10-24） ----
// 成员按功能归组，全部成员声明在同一 partial 类里，作用域与拆分前完全一致。
// 职责：任务列表：条目、状态、进度、筛选排序

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;   // ToggleButton（续传列表的真复选框）
using System.Windows.Data;                  // Binding（同上）
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;   // RenderTargetBitmap（主题切换的双快照）
using System.Windows.Threading;
using GeZi.Core.Api;
using GeZi.DownloadEngine;
using GeZi.Core.Models;
using GeZi.Core.Support;
using GeZi.Services;
using System.Text.RegularExpressions;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using SelectionMode = System.Windows.Controls.SelectionMode;
using MessageBox = System.Windows.MessageBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Run = System.Windows.Documents.Run;
using TextTrimming = System.Windows.TextTrimming;
using DiagClock = System.Diagnostics.Stopwatch;
using GeZi.Providers;
using GeZi.Providers.Quark;
using GeZi.Providers.Uc;
using GeZi.Providers.Aliyun;
using GeZi.Providers.Xunlei;
using GeZi.Providers.Shared;
using GeZi.Providers.Qr;
using ShapePath = System.Windows.Shapes.Path;   // 避免与 System.IO.Path 撞名

namespace GeZi
{
    public partial class MainWindow : Window
    {

        /// <summary>
        /// 把所有在跑的任务**汇总成一行**写进日志。
        /// 取代原来核心层那种「每个任务每 5 秒一行」的刷屏日志 ——
        /// 普通用户只关心"一共多快、还剩多少"，逐任务的连接数/分片数/命中 IP
        /// 是排查用的，需要时把日志切到「详细」档即可，诊断面板本来也一直有。
        /// 去重：内容与上一条完全相同就不打（空闲/卡住时不会一直刷）。
        /// </summary>
        private void LogAggregateProgress()
        {
            try
            {
                var active = _tasks.Where(t => t.State == JobState.Downloading
                                            || t.State == JobState.Queued
                                            || t.State == JobState.Paused
                                            || t.State == JobState.Cancelling).ToList();
                if (active.Count == 0)
                {
                    _lastProgressLog = null;   // 这一批结束了，下次重新开始
                    return;
                }

                // 🚨 2026-11-07：日志里的总速度也用平滑值，避免 0↔10MB/s 跳动误导判断。
                double speed = active.Sum(t => t.DisplaySpeed);
                long done = active.Sum(t => t.Done);
                long total = active.Sum(t => t.Total);
                int running = active.Count(t => t.State == JobState.Downloading);

                // 🚨 2026-11-08：日志里的总进度用**向下截断**，不是四舍五入。
                //    F1 会把 99.95% 显示成 100.0%，误导用户以为「下完了却还在跑」。
                //    与任务行的 TaskItem.PercentText（Math.Floor(_percent*10)/10）保持同一口径：
                //    先放大 10 倍向下取整再除回来，只保留「真实跨过的」十分位。
                string pct = total > 0
                    ? (Math.Floor(done * 1000.0 / total) / 10.0).ToString("F1") + "%"
                    : "—";
                string line = string.Format("下载中：{0} 个任务（{1} 个在跑），总速度 {2}/s，总进度 {3}",
                    active.Count, running,
                    Util.FormatSize((long)speed), pct);

                if (line == _lastProgressLog) return;   // 没变化就不重复打
                _lastProgressLog = line;
                // 每秒进度对日常视图是噪音（任务行已有进度条 + 速度），算专业级
                LogDetail(line);
            }
            catch { }
        }


        /// <summary>文件列表筛选框变化：只按名称包含关键字过滤，不改动底层集合。</summary>
        private void OnFileFilterChanged(object sender, TextChangedEventArgs e)
        {
            _fileFilter = (FileFilterBox.Text ?? "").Trim();
            try { _filesView?.Refresh(); } catch { }
        }


        /// <summary>目录在前、文件在后，同类按名称排序 —— 与 Python Flet 版一致。</summary>
        private static List<ShareFileItem> SortDirEntries(IList<QuarkFileEntry> items)
        {
            return items
                .OrderBy(it => it.IsDir ? 0 : 1)
                .ThenBy(it => it.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(it => new ShareFileItem
                {
                    Fid = it.Fid, Name = it.Name, Size = it.Size,
                    IsDir = it.IsDir, Token = it.Token,
                })
                .ToList();
        }


        /// <summary>
        /// 「顺次取链 + 顺次下载」的批次上下文。
        /// 每取到一块就立刻启动一个下载块，块之间并行；
        /// 只有「所有块都已启动」且「所有块都已跑完」时才算整批结束 ——
        /// 收尾要清理网盘中转文件，提前清会让后面的块取不到重取链所需的源文件。
        /// </summary>
        private sealed class ChainRun
        {
            private int _chunksLeft;     // 还没启动的块数
            private int _chunksActive;   // 已启动但还没跑完的块数
            private readonly object _sync = new object();

            public readonly List<DownloadJob> AllJobs = new List<DownloadJob>();
            public readonly int TotalFiles;

            public ChainRun(int totalChunks, int totalFiles)
            {
                _chunksLeft = totalChunks;
                TotalFiles = totalFiles;
            }

            /// <summary>某一块已经启动（开始下载）。</summary>
            public void ChunkStarted()
            {
                lock (_sync)
                {
                    if (_chunksLeft > 0) _chunksLeft--;
                    _chunksActive++;
                }
            }

            /// <summary>这一块没有可取的文件（跳过），只减少待启动计数。</summary>
            public void SkipChunk()
            {
                lock (_sync) { if (_chunksLeft > 0) _chunksLeft--; }
            }

            /// <summary>返回 true 表示所有块都已彻底结束。</summary>
            public bool NotifyChunkDone()
            {
                lock (_sync)
                {
                    if (_chunksActive > 0) _chunksActive--;
                    return _chunksLeft == 0 && _chunksActive == 0;
                }
            }

            /// <summary>取链提前结束（取消/异常）时把没启动的块作废；返回 true 表示可以立刻收尾。</summary>
            public bool AbortRemaining()
            {
                lock (_sync)
                {
                    _chunksLeft = 0;
                    return _chunksActive == 0;
                }
            }
        }


        private async void RunJobs(List<DownloadJob> jobs)
        {
            await RunJobsCoreAsync(jobs, null).ConfigureAwait(true);
        }


        /// <summary>
        /// 下载一批任务的核心。
        /// <paramref name="chain"/> 为 null = 一次性整批（老路径，跑完即收尾）；
        /// 非 null = 「顺次取链 + 顺次下载」里的一个块，收尾要等所有块都跑完。
        /// </summary>
        private async Task RunJobsCoreAsync(List<DownloadJob> jobs, ChainRun chain)
        {
            var progress = new Progress<JobUpdate>(OnJobUpdate);
            // 把 scheduler 抓到局部变量再 await。
            // 直接写 await _scheduler.RunAllAsync(...) 的话，await 期间若 _scheduler
            // 被替换（见 RecreateScheduler），语义就变成"用可能已变化的字段"，
            // 且和已经开始的这批不是同一个对象 —— 限流账目会错乱。
            // 抓成局部引用后，本批任务从头到尾只用同一个 scheduler，清晰且安全。
            var scheduler = _scheduler;
            System.Threading.Interlocked.Increment(ref _batchRunningCount);
            // 新任务已入队 → 合并按钮立刻切成「暂停全部」（不用等第一次进度上报）
            UpdatePauseAllButton(force: true);
            // 立刻开启保活，别等 30 秒的定时器 —— 用户可能点完就去睡觉了
            RefreshSleepGuard();
            try
            {
                await scheduler.RunAllAsync(jobs, progress, CancellationToken.None);
            }
            catch (ObjectDisposedException ex)
            {
                // 兜底：即便将来又有别的路径 Dispose 了 scheduler，也不该让用户
                // 看到"已释放该信号量"这种无信息量的英文异常名。
                LogDetail("下载被中断：下载器被重新初始化（请重新开始该任务，已下载部分会续传）。" + ex.Message);
            }
            catch (Exception ex)
            {
                Log("下载异常: " + ex.Message);
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref _batchRunningCount);
            }
            LogDetail("本块下载结束");

            // 顺次模式下，只有「所有块都已启动且都已跑完」才收尾
            if (chain == null)
                await FinishBatchAsync(jobs).ConfigureAwait(true);
            else if (chain.NotifyChunkDone())
                await FinishBatchAsync(chain.AllJobs).ConfigureAwait(true);
        }


        /// <summary>整批收尾：汇总日志 + 清理网盘中转文件。</summary>
        private async Task FinishBatchAsync(List<DownloadJob> jobs)
        {
            // 整批结束时在日志里汇总一次（原托盘气泡通知已随"后台功能"一起移除）。
            try
            {
                // ⚠️ 不在任务列表里的（更新包）**不参与**这批汇总 —— 它有自己的收尾提示；
                //    若把它算进来，会被当成"未完成"，打出一句假的「下载失败」。
                int ok = 0, fail = 0;
                foreach (var j in jobs)
                {
                    var t = FindTaskRow(j);
                    if (t == null) continue;
                    if (t.State == JobState.Completed) ok++;
                    else fail++;
                }
                if (ok + fail == 0)
                {
                    // 整批都不在任务列表里 → 不打汇总
                }
                else if (ok > 0 && fail == 0)
                    Log(string.Format("全部完成：{0} 个文件已下载完成。", ok));
                else if (ok > 0)
                    Log(string.Format("下载结束：{0} 个完成，{1} 个未完成。", ok, fail));
                else if (fail > 0)
                    Log(string.Format("下载失败：{0} 个文件未能完成，请查看任务列表。", fail));
            }
            catch { }

            await CleanupTransferredAsync().ConfigureAwait(true);
        }


        /// <summary>
        /// 往任务列表加一行，并同步 Job→行 的反查索引。
        /// ⚠️ 所有 <c>_tasks.Add(new TaskItem(...))</c> 都必须走这里，
        ///    否则索引缺项会让进度回调找不到那一行（进度永远不更新）。
        /// </summary>
        private TaskItem AddTaskRow(TaskItem item)
        {
            _tasks.Add(item);
            if (item != null && item.Job != null)
                _taskByJob[item.Job] = item;
            return item;
        }


        /// <summary>
        /// 批量往任务列表加行。
        /// <para>
        /// 🚨 为什么不能用 <c>ICollectionView.DeferRefresh()</c> 包住一批 Add（2026-11-01 事故）：
        /// <c>DeferRefresh</c> 的语义是「暂停刷新，期间**禁止**修改数据源」，只适合改
        /// Filter / Sort / Group 描述。在 defer 期间插元素，<c>ListCollectionView</c> 会在
        /// <c>ProcessCollectionChangedWithAdjustedIndex → get_CurrentPosition</c> 里调
        /// <c>VerifyRefreshNotDeferred()</c> 抛 <c>InvalidOperationException</c>
        /// （「延迟 Refresh 时无法更改或检查 CollectionView 的内容或 Current 位置」），
        /// 冒到全局就变成「程序遇到一个内部错误：当前状态不允许这个操作，请重试」。
        /// 多任务下载时 <c>QueueInChunksAsync</c> 反复批量入队，所以必现。
        /// </para>
        /// <para>
        /// 🚨 也**不能**改成「一次通知带多个 NewItems」：实测 WPF 的
        /// <c>ListCollectionView</c> 明确拒绝范围操作，会抛
        /// <c>NotSupportedException</c>（「不支持范围操作」），
        /// 而且是在元素**已经进集合之后**才抛 —— 兜底重插会变成重复行。
        /// </para>
        /// <para>
        /// 正解：仍然逐条 <c>Add</c>（视图语义完全正常），但用 <c>_suppressTaskSync</c>
        /// 把集合变更的三项副作用（空态 / 分类计数 / 待办落盘）挂起，
        /// 整批插完再统一跑一次 —— 既省掉 N 次 O(n) 重算，又不碰任何 WPF 范围语义。
        /// </para>
        /// </summary>
        private void AddTaskRows(IList<TaskItem> items)
        {
            if (items == null || items.Count == 0) return;

            // 只有一条就没必要挂起副作用
            if (items.Count == 1)
            {
                AddTaskRow(items[0]);
                return;
            }

            bool prev = _suppressTaskSync;
            _suppressTaskSync = true;
            try
            {
                // AddTaskRow 内部同步维护 Job→行 反查索引，不能绕过它。
                for (int i = 0; i < items.Count; i++)
                    AddTaskRow(items[i]);
            }
            finally
            {
                _suppressTaskSync = prev;
            }

            // 外层如果已经在批量里（prev==true），由最外层负责统一同步。
            if (!prev)
                SyncTaskSideEffects();
        }


        /// <summary>
        /// 任务集合变更后的三项副作用：空态插画、分类 Tab 计数、未完成任务落盘。
        /// 由 <see cref="AddTaskRows"/> 在批量插入末尾统一调用一次。
        /// </summary>
        private void SyncTaskSideEffects()
        {
            try
            {
                UpdateEmptyStates();
                UpdateTaskFilterCounts();
                SyncPending();
            }
            catch { }
        }


        /// <summary>按 Job 找任务行：先查字典（O(1)），查不到才退回线性兜底。</summary>
        private TaskItem FindTaskRow(DownloadJob job)
        {
            if (job == null) return null;
            TaskItem hit;
            if (_taskByJob.TryGetValue(job, out hit))
                return hit;
            // 兜底：万一有路径没走 AddTaskRow（例如老代码或异常分支），
            // 线性找一次并把索引补上，绝不因为索引缺项而丢掉进度。
            var item = _tasks.FirstOrDefault(t => t.Job == job);
            if (item != null)
                _taskByJob[job] = item;
            return item;
        }


        /// <summary>移除一行任务，并同步索引。</summary>
        private void RemoveTaskRow(TaskItem item)
        {
            if (item == null) return;
            _tasks.Remove(item);
            if (item.Job != null)
                _taskByJob.Remove(item.Job);
        }


        /// <summary>按下标移除一行任务，并同步索引。</summary>
        private void RemoveTaskRowAt(int index)
        {
            if (index < 0 || index >= _tasks.Count) return;
            var item = _tasks[index];
            _tasks.RemoveAt(index);
            if (item != null && item.Job != null)
                _taskByJob.Remove(item.Job);
        }


        private void OnJobUpdate(JobUpdate u)
        {
            var item = FindTaskRow(u.Job);
            if (item == null)
                return;

            // ⚠️ 用户已按暂停、但下载器还在收尾时，**忽略核心报来的 Downloading**。
            // 核心的 JobProgressAdapter 永远报 Downloading（它只是转发进度，不关心暂停），
            // 不拦的话刚设好的 Paused 会被下一帧覆盖 → 图标先变三角又变回竖杠。
            // 进度数据本身照常更新（这样能看到速度渐降到 0，文案随之从「暂停中…」变「已暂停」）。
            bool keepPaused = item.PauseRequested && u.State == JobState.Downloading;
            if (!keepPaused)
            {
                item.State = u.State;
                // 走到终态/被恢复时清掉暂停标志，避免它一直挂着影响后续状态显示
                if (u.State != JobState.Paused)
                    item.PauseRequested = false;
            }
            // 🚨 界面显示翻译后的人话，日志保留引擎原始错误（2026-10-26 用户要求）。
            // 引擎报的是「HTTP 400: Bad Request」「HTTP 403」这类技术串，
            // 直接摆在任务卡上用户既看不懂也不知道该做什么。
            string rawError = u.Error ?? "";
            item.Error = rawError.Length == 0 ? "" : ErrorText.DescribeDownloadError(rawError);
            if ((u.State == JobState.Failed || u.State == JobState.Cancelled) && rawError.Length > 0)
            {
                Log("[" + item.Name + "] " + (u.State == JobState.Failed ? "失败" : "取消") + ": " + rawError
                    + (item.Error != rawError ? "（" + item.Error + "）" : ""));
            }
            if (u.Progress != null)
            {
                // 🚨🚨 2026-11-03 修「暂停后进度条和数字还在变」（用户实测确认无效）：
                //   原来这里**无条件**更新 Percent/Done/Total，只冻结了 Speed。
                //   而引擎是闸门式暂停：worker 要等当前 64KB 读完才停，
                //   写队列里已缓冲的数据还会继续落盘并上报进度 ——
                //   于是用户看到「点了暂停，进度条还在走、数字还在涨」。
                //   ⇒ 暂停期间**一个字段都不更新**，界面完全冻结。
                //   真实进度不会丢：恢复时下一帧自然追上。
                if (!keepPaused)
                {
                    item.Percent = u.Progress.Percent;
                    item.Speed = u.Progress.Speed;
                    // 🚨 2026-11-07：界面显示用 5 秒平滑速度；调度水位仍读 item.Speed。
                    item.DisplaySpeed = u.Progress.DisplaySpeed;
                    item.Done = u.Progress.Done;
                    item.Total = u.Progress.Total;
                }

                // 🚨 2026-11-02：Total 首次拿到正数时，**强制**把 Pending 落一次盘。
                //   背景：DownloadJob 本身没有大小字段，Size 只能从进度回调拿；
                //   而 SyncPending 的状态指纹把 Queued/Downloading 合并计数（防每秒几十次全量写盘），
                //   所以「入队时写了 Size=0」之后**再也不会被更新**。
                //   后果：用户「刚点下载、还没写盘就重启」→ 磁盘上永远是 Size=0 →
                //   续传界面显示「大小未知」（虽然现在已不再禁止续传，但显示仍不准）。
                //   这里在 Total 从 0 变正数的那一刻把指纹置为 -1，让下一次 SyncPending
                //   必定落盘 —— 只多写一次，开销可忽略。
                // 暂停期间不更新 Total（见上面的冻结说明），但**指纹失效**照常做 ——
                // 那是落盘记账，与界面显示无关。
                bool totalBecameKnown = item.Total <= 0 && u.Progress.Total > 0;
                if (!keepPaused)
                    item.Total = u.Progress.Total;
                if (totalBecameKnown)
                    _pendingLightSig = -1;
                // 阶段说明（如「正在合并分片」）。非下载态一律清空，
                // 免得完成/失败后还挂着「正在合并分片」这种残留文案。
                item.Phase = u.State == JobState.Downloading ? u.Progress.Phase : null;
            }

            // 🚨 完成帧（JobState.Completed）不带 Progress（见 DownloadScheduler.Report），
            // 界面只能停在最后一次 Downloading 帧的百分比上 —— 实测 99.9%。
            // 走到这里说明内容校验已通过（失败会抛 InvalidDataException），
            // 文件字节数必然等于总长，所以钉到 100% 是真实值，不是粉饰。
            if (u.State == JobState.Completed)
            {
                item.Percent = 100.0;
                if (item.Total > 0) item.Done = item.Total;
            }

            // ---- 历史记录：终态各记一条（只记一次）----
            if (!item.HistoryRecorded
                && (u.State == JobState.Completed || u.State == JobState.Failed || u.State == JobState.Cancelled))
            {
                item.HistoryRecorded = true;
                string status = u.State == JobState.Completed ? "完成"
                    : u.State == JobState.Failed ? "失败" : "已取消";
                long sizeBytes = item.Total > 0 ? item.Total : item.Done;
                HistoryStore.AddHistory(item.Name, Util.FormatSize(sizeBytes), item.Job.Dest, status);

                // 阿里 / 迅雷分享取链前必须转存一份到自己网盘，下载结束要删掉，
                // 否则用户网盘里会堆满垃圾。**只能等终态删**：下载中途删会让 CDN 回源 503。
                string destForCleanup = item.Job.Dest;
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    await CleanupStoredForDestAsync(destForCleanup);
                }));
            }

            // ---- 未完成任务记录：仅在状态集合发生变化时同步（避免每帧进度都写盘）----
            SyncPending();

            // 合并按钮的文案/图标要跟着任务状态走（有在跑的 → 暂停全部；否则 → 恢复全部）
            UpdatePauseAllButton();

            // 取链水位（2026-10-31）：每来一帧进度就顺手判一次「队列是不是快空了」，
            // 快空了就唤醒正在等的取链循环去补货。这里只做几次整数比较，
            // 不碰 UI、不碰磁盘，开销可以忽略。
            PumpWatermark();

        }


        // ---------------- 任务列表右键菜单（单任务操作） ----------------
        // 移植自 Python Flet 版的 _pause_single / _resume_single / _cancel_single /
        // _task_del_local / _file_menu_popup（右键菜单）。
        // 为什么必须有：此前 C# 只有「全部暂停/恢复/取消」，一个跑得慢的任务
        // 会拖住整批，用户没法单独处理它 —— 这是下载工具的基本操作。

        /// <summary>
        /// 右键按下时把光标下的任务选中，这样菜单动作能对准用户真正点的那一行。
        /// 若该处没有任务（点到空白），保持原选中不变。
        /// </summary>
        private void OnTaskListRightClick(object sender, MouseButtonEventArgs e)
        {
            var lbi = FindListBoxItem(e.OriginalSource as DependencyObject);
            if (lbi == null) return;

            // ⚠️ 原来无条件 `TaskList.SelectedItem = ...` ——
            //    这句会**把整个选择替换成这一个**，于是"Shift 离散多选之后再右键"
            //    多选立刻被清掉，菜单里的暂停/删除只作用到一个文件
            //    （用户反馈：「根本无法完成多选的情况下右键使用功能」）。
            //    现在：点到**已选中**的行 → 保持整份多选不动；点到**没选中**的行 →
            //    才把它单独选上（否则右键一个没选中的行却对别的行生效，很反直觉）。
            if (lbi.IsSelected) return;
            TaskList.SelectedItems.Clear();
            lbi.IsSelected = true;
        }

        /// <summary>
        /// 右键菜单弹出前，先确认光标真的落在一个任务行上；点在列表空白处
        /// 就 <c>e.Handled = true</c> 拦住，菜单不弹（用户反馈「右键空白位置也出现右键选项」）。
        /// </summary>
        private void OnTaskListContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            // 菜单弹哪取决于右击点在哪个 ListBoxItem 上；FindListBoxItem 返回 null 即空白处。
            var lbi = FindListBoxItem(e.OriginalSource as DependencyObject);
            if (lbi == null)
                e.Handled = true;
        }


        // ⚠️ 双击任务行**不再**打开文件（用户 m13541 要求）：
        //    原来 TaskList 绑了 MouseDoubleClick="OnTaskListDoubleClick"，
        //    双击行内任意位置都会调 OpenTaskFileCore → 直接拉起 VLC，
        //    误触率极高（想多选、想点进度条都会中招）。
        //    现在唯一入口是行内「播放」按钮（OnOpenTaskFile，仅 CanPlay=True 时可见）
        //    与右键菜单「播放文件」。


        /// <summary>
        /// 右键菜单打开时按选中任务的**实际状态**决定显示哪些项、文案是什么。
        ///
        /// <para>用户 m13541 反馈：菜单里「暂停」「恢复」两条永远同时在，无论任务是
        /// 在跑还是已暂停，总有一条是死按钮；「取消」对已完成的任务毫无意义；
        /// 「仅移除记录」和「取消」在做同一件事。所以这里按状态裁剪：</para>
        /// <list type="bullet">
        /// <item>有进行中/暂停中的任务 → 显示「暂停」或「恢复」（合并成一条，文案切换）与「取消」；</item>
        /// <item>全部是终态（完成/失败/已取消）→ 隐藏「暂停/恢复」「取消」，显示「仅移除记录」；</item>
        /// <item>「播放文件」始终跟着 <see cref="TaskItem.CanPlay"/> 走（与行内「播放」按钮同一判据）。</item>
        /// </list>
        /// </summary>
        private void OnTaskContextMenuOpened(object sender, RoutedEventArgs e)
        {
            var list = TasksFromMenu(sender);
            if (list.Count == 0) return;

            // 进行中 = 会真正占着 worker 的状态；暂停也算（它随时能恢复）。
            var live = list.Where(x => x.State == JobState.Downloading
                                    || x.State == JobState.Queued
                                    || x.State == JobState.Paused
                                    || x.State == JobState.Cancelling).ToList();
            bool anyLive = live.Count > 0;

            // 「暂停」还是「恢复」：全部已暂停 → 恢复；只要还有一个在跑 → 暂停。
            bool allPaused = live.Count > 0 && live.All(x => x.State == JobState.Paused);
            TaskPauseResumeItem.Header = allPaused ? "恢复" : "暂停";
            TaskPauseResumeIcon.Data = allPaused
                ? (Geometry)FindResource("IconPlay")
                : (Geometry)FindResource("IconPause");

            TaskPauseResumeItem.Visibility = anyLive ? Visibility.Visible : Visibility.Collapsed;
            TaskCancelItem.Visibility = anyLive ? Visibility.Visible : Visibility.Collapsed;

            // 播放：只要选中里有一个能播就显示（多选时点一下打开第一个能播的）。
            TaskPlayItem.Visibility = list.Any(x => x.CanPlay)
                ? Visibility.Visible : Visibility.Collapsed;

            // 分隔条跟着上下的可见性走，避免出现「连续两条横线」或「末尾一条横线」。
            // 2026-11-01：「仅移除记录」及其专属分隔线 TaskMenuSep2 已删除，
            // 现在只剩 TaskMenuSep1 一条，用来隔开「终止类操作」与「打开类操作」。
            TaskMenuSep1.Visibility = (TaskPlayItem.Visibility == Visibility.Visible
                                       || TaskCancelItem.Visibility == Visibility.Visible)
                ? Visibility.Visible : Visibility.Collapsed;
        }


        /// <summary>
        /// 「暂停 / 恢复」合并后的入口：选中里还有在跑的就暂停，全暂停了就恢复。
        /// 与原来 <see cref="OnTaskPause"/> / <see cref="OnTaskResume"/> 的分发逻辑一致。
        /// </summary>
        private void OnTaskPauseResume(object sender, RoutedEventArgs e)
        {
            var list = TasksFromMenu(sender);
            if (list.Count == 0) return;
            bool allPaused = list.All(x => x.State == JobState.Paused);
            foreach (var t in list)
            {
                if (allPaused) ResumeTask(t);
                else if (t.State != JobState.Completed && t.State != JobState.Failed
                         && t.State != JobState.Cancelled) PauseTask(t);
            }
        }


        private void OnTaskPause(object sender, RoutedEventArgs e)
        {
            foreach (var t in TasksFromMenu(sender)) PauseTask(t);
        }


        /// <summary>
        /// 任务行上那个**图标式播放/暂停切换按钮**（图标会随状态互换）。
        /// 它控制的是**这个下载任务**的暂停/恢复 —— 与右键菜单的「暂停/恢复」同一套逻辑。
        /// ⚠️ 不是"控制播放器"：GeZi 是把文件交给 VLC 等**外部播放器**打开的，控制不了它们。
        /// </summary>
        private void OnTaskTogglePause(object sender, RoutedEventArgs e)
        {
            var t = (sender as System.Windows.Controls.Button)?.Tag as TaskItem;
            if (t == null) return;
            if (t.State == JobState.Paused) ResumeTask(t);
            else PauseTask(t);
        }


        private void OnTaskCancel(object sender, RoutedEventArgs e)
        {
            foreach (var t in TasksFromMenu(sender)) CancelTask(t);
        }


        // ⚠️ 2026-11-01（用户要求）：「仅移除记录」入口已从右键菜单删除，方法一并删除。
        //    它与「删除并移除」在用户眼里是同一个意思（都是"不要这个任务了"），
        //    两个都留着只会让人多犹豫一次。想保留本地文件的，就不该动任务行。
        //    ⇒ RemoveTaskRecord(TaskItem) 的唯一调用者就是这里，也一并删除。


        /// <summary>
        /// 「删除本地文件」：清掉目标文件 + 分片目录 + .qmeta。
        /// 与 Python 版 `_task_del_local` 对齐。关键细节：
        ///  - 先取消后台任务，否则 worker 还在写，删了又会重新创建；
        ///  - 文件被外部程序（播放器、资源管理器预览）占用时，用 MoveFileEx
        ///    登记为**重启后自动删除**，而不是直接报一个用户看不懂的错误。
        /// </summary>
        /// <summary>
        /// 「删除本地文件」= **不要这个任务了**：先取消下载，再删掉本地文件与分片。
        /// 「在任务中一个任务正在下载中当我选择删除本地文件，
        /// 就相当于我不要这个任务了，所以会进行取消下载并完成删除本地文件。
        /// 日志上只会显示已成功删除本地文件XXX」。
        /// ⚠️ 取消条件里**必须包含 Paused**：暂停中的任务 Job 仍挂在调度器上
        /// （worker 阻塞在 PauseToken 闸门），只把任务从列表移走而不取消，
        /// `RunAllAsync` 就永不返回 → `_batchRunning` 一直 true
        /// → 退出时永远提示"还有任务在运行"、下次启动又被当成可续传任务。
        /// （同样的坑在「仅移除记录」上已经踩过一次，见那里的注释。）
        /// ⚠️ 支持 Shift 离散多选后一次删多个。
        /// </summary>
        private void OnTaskDeleteLocal(object sender, RoutedEventArgs e)
        {
            var list = TasksFromMenu(sender)
                .Where(x => x?.Job != null && !string.IsNullOrEmpty(x.Job.Dest))
                .ToList();
            if (list.Count == 0) return;

            string body = list.Count == 1
                ? list[0].Job.Dest
                : string.Join("\n", list.Take(8).Select(x => "· " + x.Name))
                  + (list.Count > 8 ? "\n…" : "");
            var ans = AppDialog.Show(this,
                string.Format("确定删除{0}本地文件吗？\n\n{1}\n\n" +
                              "（正在下载的会先取消；分片与续传元数据一并清理，不可撤销）",
                    list.Count == 1 ? "以下" : list.Count + " 个任务的", body),
                "删除本地文件", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (ans != MessageBoxResult.Yes)
                return;

            foreach (var t in list)
            {
                // 只要还在"活着"的状态就先取消（含暂停——见上面的注释）
                if (t.State == JobState.Downloading || t.State == JobState.Queued
                    || t.State == JobState.Paused || t.State == JobState.Cancelling)
                {
                    // 🚨 必须先置位再取消：取消信号会立刻触发下载器的收尾分支，
                    //    晚了就赶不上「不写续传快照」的判定（2026-11-07 实测竞态）。
                    t.Job.DeleteAndRemoveRequested = true;
                    try { t.Job.CancelJob(); } catch { }
                    t.State = JobState.Cancelling;
                }

                string dest = t.Job.Dest;
                string name = t.Name;
                // 🚨 2026-11-07：先标记为「已删除」，必须在 SyncPending 之前。
                //   否则第 ③ 段会把磁盘上那条旧记录捡回来，徽章与 .geziresume 都会复活。
                MarkPendingDeleted(dest);
                RemoveTaskRow(t);
                // 后台删除：文件可能较大，且被占用时要等待句柄释放，不能卡 UI 线程。
                // 删除结束后再回 UI 线程同步一次 pending —— 取消流程可能在这段时间内
                // 按旧状态又写了一次记录，最后一刀把它清掉。
                _ = Task.Run(() =>
                {
                    DeleteLocalFiles(dest, name);
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try { SyncPending(); RefreshPendingEntry(); } catch { }
                        }));
                    }
                    catch { }
                });
            }

            // 🚨 2026-11-06：删完必须**立刻**同步一次 pending 记录。
            //   RemoveTaskRow 只改内存集合，不写盘；原来依赖 OnJobUpdate 的
            //   状态变化帧去触发 SyncPending，但行已被移除 → FindTaskRow 返回 null
            //   → 那一帧直接 return，GeZi.state.json 里这条记录永远留着，
            //   重启后续传框里又冒出一条「已删除的任务」（用户实测）。
            //   这里显式同步一次，把这条从磁盘记录里摘掉。
            SyncPending();
            RefreshPendingEntry();
        }


        /// <summary>分类过滤谓词（供 ICollectionView 调用）。</summary>
        private bool TaskFilterPredicate(object o)
        {
            var t = o as TaskItem;
            if (t == null) return false;
            switch (_taskFilter)
            {
                case "active":
                    return t.State == JobState.Queued || t.State == JobState.Downloading
                        || t.State == JobState.Paused;
                case "failed":
                    return t.State == JobState.Failed || t.State == JobState.Cancelled;
                case "done":
                    return t.State == JobState.Completed;
                default:
                    return true;
            }
        }


        /// <summary>
        /// 分类 Tab 切换。只改过滤器 + Refresh，
        /// 不重建集合 —— 保证滚动位置、选中项、绑定都不受影响。
        /// </summary>
        private void OnTaskFilterChanged(object sender, RoutedEventArgs e)
        {
            try
            {
                if (TaskFilterActive == null) return;   // 初始化期间控件尚未就绪
                if (TaskFilterAll.IsChecked == true) _taskFilter = "all";
                else if (TaskFilterActive.IsChecked == true) _taskFilter = "active";
                else if (TaskFilterFailed.IsChecked == true) _taskFilter = "failed";
                else _taskFilter = "done";
                if (_taskView != null) _taskView.Refresh();
                UpdateTaskFilterCounts();
            }
            catch { }
        }


        /// <summary>更新分类 Tab 右侧的计数（"共 N 条"），让用户不用切过去就知道有几条。</summary>
        private void UpdateTaskFilterCounts()
        {
            try
            {
                if (TaskFilterCountText == null) return;
                int n = 0;
                foreach (var t in _tasks)
                {
                    if (t == null) continue;
                    bool hit;
                    switch (_taskFilter)
                    {
                        case "active":
                            hit = t.State == JobState.Queued || t.State == JobState.Downloading
                               || t.State == JobState.Paused; break;
                        case "failed":
                            hit = t.State == JobState.Failed || t.State == JobState.Cancelled; break;
                        case "done":
                            hit = t.State == JobState.Completed; break;
                        default:
                            hit = true; break;
                    }
                    if (hit) n++;
                }
                TaskFilterCountText.Text = n > 0 ? "共 " + n + " 条" : "";
            }
            catch { }
        }


        /// <summary>「当前状态」卡里正在使用那家网盘的昵称与容量（只有当前那行显示容量）。</summary>
        private string _loginNick = "";
        private long _loginUsed;
        private long _loginTotal;


        /// <summary>
        /// 同步「登录」页里的网盘状态列表（与侧栏账号卡同源，避免两处状态不一致）。
        /// </summary>
        /// <param name="kind">
        /// 这个昵称/容量**属于哪家网盘**。null = 沿用 <see cref="_activeKind"/>。
        /// <para>
        /// 🚨🚨 2026-11-02 修复「夸克账号里显示阿里昵称、重启才恢复」：
        /// 原来这个方法内部用 <c>_panNick[_activeKind] = nick</c> ——
        /// 键是**当前网盘**（随时会被切换改掉），值是**传进来的昵称**（可能来自上一家）。
        /// 两者不同步时，阿里的昵称就被写进了夸克的槽位。
        /// 而 <c>_panNick</c> 是纯内存字典，重启即清空并从账号档案重填 ——
        /// 这正好解释用户观察到的「只有重启才能恢复」。
        /// </para>
        /// <para>
        /// 现在**所有调用点必须显式传网盘**，不再依赖可变的全局状态。
        /// </para>
        /// </param>
        private void SyncLoginPageState(string nick, long used, long total, PanKind? kind = null)
        {
            if (PanStateList == null)
                return;
            // 登录页四张网盘卡的状态跟着账号状态走（已登录 / 凭证已保存 / 未登录）。
            // ⚠️ 必须挂在这里而不是只挂 LoadSettingsToUi —— 启动时 LoadSettingsToUi 跑在
            //    QuarkClient 创建之前，那时 _client 还是 null，卡上会永远停在「凭证已保存」。
            RefreshPanCards();
            // 「我的网盘」上方的四家切换条也要跟着账号状态走
            RefreshDrivePanTabs();
            // 四家各自实测一次（只跑一轮；结果缓存，登录成功后作废重验）。
            if (!_panLiveDone && !_panLiveValidating)
                ValidatePanSessionsAsync();

            // 上次运行崩溃/强杀时来不及删的临时转存，在登录态就绪后补删一次。
            // 内部自带「只跑一次 + 至少一家已登录」双重闸门，重复调用无副作用。
            if (IsLoggedInAny)
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    await SweepStaleStoredAsync();
                }));
            _loginNick = nick ?? "";
            // 🚨 昵称必须同时记到「这家网盘」自己的槽位里。
            //    原来只有 _loginNick 一个全局槽，四行都读它 ——
            //    用户实测：第一行写「夸克网盘」、副标题却是「迅雷用户」，
            //    因为 _loginNick 被最近一次登录的那家（迅雷）覆盖了。
            if (!string.IsNullOrEmpty(nick) && IsLoggedInAny)
            {
                // 显式指定网盘；没传才回落到 _activeKind（兼容老调用点）。
                PanKind slot = kind.HasValue ? kind.Value : _activeKind;
                _panNick[slot] = nick;
            }
            _loginUsed = used;
            _loginTotal = total;
            RefreshPanStateList();
        }


        /// <summary>
        /// 重建「当前状态」卡：四家网盘各占一行，各自显示自己的昵称与凭证状态。
        /// <para>
        /// 用户要求「一下子能登录所有网盘的账号，解析链接时无论什么网盘都能解析、
        /// 不需要提前切换账号」—— 所以这里必须四家同时可见，
        /// 而不是原来那种只显示当前账号一行的写法。
        /// </para>
        /// </summary>
        /// <summary>
        /// 切换「当前状态」卡右上角的「正在验证登录态…」提示（旋转圈 + 文案）。
        /// <para>
        /// 启动时四家网盘要逐个实测登录态，这期间行数据是过时的 —— 用户会以为掉线了。
        /// 所以验证期间明确显示在转，验完自动收起。
        /// </para>
        /// </summary>
        private void SetPanStateBusy(bool busy)
        {
            if (PanStateBusyHint == null || PanStateBusySpinner == null)
                return;

            try
            {
                if (!busy)
                {
                    PanStateBusyHint.Visibility = Visibility.Collapsed;
                    if (PanStateBusySpin != null)
                        PanStateBusySpin.BeginAnimation(RotateTransform.AngleProperty, null);
                    return;
                }

                PanStateBusyHint.Visibility = Visibility.Visible;
                if (PanStateBusySpin == null)
                    return;

                // 每帧 +30°，1 秒一圈，无限循环。
                var spin = new DoubleAnimation
                {
                    From = 0,
                    To = 360,
                    Duration = new Duration(TimeSpan.FromSeconds(1)),
                    RepeatBehavior = RepeatBehavior.Forever,
                };
                PanStateBusySpin.BeginAnimation(RotateTransform.AngleProperty, spin);
            }
            catch { }
        }

        /// <summary>「当前状态」卡上次重建时刻（用于切换/刷新合并，避免频繁读盘重建）。</summary>
        private DateTime _panStateLastRebuildUtc = DateTime.MinValue;

        private void RefreshPanStateList(bool force = false)
        {
            if (PanStateList == null)
                return;
            // 🚨 2026-11-07：切换功能页时 SyncLoginPageState / 续期回调 / 主题刷新
            //   会在几百毫秒内连调好几次本方法；每次都逐家读账号档案 XML + DPAPI 解密
            //   + 重建 4 行控件 = 主线程同步 IO，用户感知「一切换就卡顿」。
            //   300ms 内的重复调用直接合并掉；force=true 用于登录态确定的关键帧。
            DateTime nowUtc = DateTime.UtcNow;
            if (!force && (nowUtc - _panStateLastRebuildUtc) < TimeSpan.FromMilliseconds(300))
                return;
            _panStateLastRebuildUtc = nowUtc;
            try
            {
                PanStateList.Children.Clear();

                // 每家网盘取最近使用的那份档案（AccountStore.LoadAll 已按最近使用排序）
                var byPan = new Dictionary<PanKind, AccountProfile>();
                try
                {
                    // 逐家读各自子目录：LoadAll(kind) 只打开那一家的文件，
                    // 绝不会把别家账号算进来。
                    foreach (var k in new[] { PanKind.Quark, PanKind.Uc, PanKind.Aliyun, PanKind.Xunlei })
                    {
                        var one = AccountStore.LoadAll(k);
                        if (one.Count > 0)
                            byPan[k] = one[0];
                    }
                }
                catch { }

                var kinds = new[] { PanKind.Quark, PanKind.Uc, PanKind.Aliyun, PanKind.Xunlei };
                for (int i = 0; i < kinds.Length; i++)
                {
                    AccountProfile acc;
                    byPan.TryGetValue(kinds[i], out acc);
                    PanStateList.Children.Add(BuildPanStateRow(kinds[i], acc, i == kinds.Length - 1));
                }
            }
            catch { }
        }


        /// <summary>
        /// 重建「我的网盘」上方的四家网盘切换条。
        /// <para>
        /// 用户反馈（2026-10-25）：「现在我的网盘只能显示夸克的，我希望能选择这四个网盘」。
        /// 四家的「能不能点」是运行态算出来的（登录态 / 凭证 / 实测结果），
        /// 所以按钮在代码里生成，状态一变就整体重建。
        /// </para>
        /// <para>
        /// 🎨 2026-10-26 重做：原来是四个**等宽胶囊**（一个深色实心 + 三个白底描边），
        /// 用户说「这四个按钮太丑了，有 AI 味」。那种"一排同尺寸圆角描边块"正是
        /// 模板化生成的典型观感。现在改成**无边框文字页签**：
        /// 平时只有灰字，选中的那家变深色加粗并在下方压一条 2px 主色横条。
        /// 这也是本界面其他位置（面包屑、工具条）已经在用的语言。
        /// </para>
        /// </summary>
        private void RefreshDrivePanTabs()
        {
            if (DrivePanTabs == null)
                return;
            try
            {
                DrivePanTabs.Children.Clear();
                // 页签专用样式：hover 只提亮字色、不铺底色
                // （GhostButton 的近白底会盖住下方 2px 的 DrivePanThumb 滑块）
                var tabStyle = TryFindResource("DrivePanTabButton") as Style;
                var kinds = new[] { PanKind.Quark, PanKind.Uc, PanKind.Aliyun, PanKind.Xunlei };
                for (int i = 0; i < kinds.Length; i++)
                {
                    var kind = kinds[i];
                    bool active = IsLoggedInAny && _activeKind == kind;
                    // 统一判据（见 PanUsable 的说明）
                    bool hasCred = PanUsable(kind);

                    var stack = new StackPanel { Orientation = Orientation.Vertical };
                    var label = new TextBlock
                    {
                        Text = PanUrlParser.DisplayNameOf(kind),
                        FontSize = 12.5,
                        FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    };
                    label.SetResourceReference(TextBlock.ForegroundProperty,
                        active ? "TextPrimaryBrush" : "TextSecondaryBrush");
                    stack.Children.Add(label);

                    // 选中态的 2px 横条**不再由每个按钮自绘**（2026-10-26）——
                    // 原来这里是每按钮一条 Border，切换时是「旧的一条消失 + 新的一条出现」，
                    // 视觉上就是瞬移，用户反馈「为什么这个滑动动画没有做」。
                    // 现在整条切换条共用一个 DrivePanThumb 滑块（XAML 里），
                    // 这里只留一个等高透明占位，保证四家文字基线一致。
                    var underline = new Border
                    {
                        Height = 2,
                        CornerRadius = new CornerRadius(1),
                        Margin = new Thickness(0, 5, 0, 0),
                        Background = Brushes.Transparent,
                    };
                    stack.Children.Add(underline);

                    var btn = new Button
                    {
                        Content = stack,
                        Tag = kind.ToString(),
                        Style = tabStyle,
                        Padding = new Thickness(13, 6, 13, 3),
                        Margin = new Thickness(0, 0, 2, 0),
                    };
                    if (!hasCred && !active)
                    {
                        // 还没登录的那家做淡一点（仍可点 —— 点了会引导去「登录」页）
                        btn.Opacity = 0.45;
                        btn.ToolTip = "还没登录这家网盘，点了会引导去登录";
                    }
                    // hover 只提亮字色：label 的 Foreground 是局部值，样式触发器盖不住，
                    // 所以在这里挂；绝不铺底色（否则盖住下方滑块）。
                    btn.MouseEnter += (s2, e2) =>
                        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    btn.MouseLeave += (s2, e2) => label.SetResourceReference(TextBlock.ForegroundProperty,
                        active ? "TextPrimaryBrush" : "TextSecondaryBrush");
                    btn.Click += OnDrivePanTabClick;
                    DrivePanTabs.Children.Add(btn);
                }

                // 按钮刚加进视觉树，这一刻 Width/ActualWidth 还是 0，必须等布局跑完
                // 再量位置。用一次性的 LayoutUpdated 拿到真实坐标后立刻退订。
                EventHandler placeThumb = null;
                placeThumb = (s, ev) =>
                {
                    DrivePanTabs.LayoutUpdated -= placeThumb;
                    // 切换网盘触发的重建走滑动动画；其它情况（面板首次显示、
                    // 登录态变化）直接落位，免得看到滑块从最左边滑过来。
                    PlaceDrivePanThumb(_panThumbAnimate);
                    _panThumbAnimate = false;
                };
                DrivePanTabs.LayoutUpdated += placeThumb;
            }
            catch { }
        }


        /// <summary>
        /// 把「我的网盘」切换条底下的滑块挪到当前选中那一家下面。
        /// <paramref name="animate"/> 为 true 时走 160ms 缓动，切换网盘时能看到滑块滑过去；
        /// 首次显示（面板刚打开 / 刚重建）走 false，直接落到目标位，避免从左边滑进来的怪动作。
        /// </summary>
        private void PlaceDrivePanThumb(bool animate)
        {
            if (DrivePanThumb == null || DrivePanTabs == null) return;
            Button target = null;
            foreach (var child in DrivePanTabs.Children)
            {
                var b = child as Button;
                if (b == null) continue;
                PanKind k;
                if (!Enum.TryParse(b.Tag as string, true, out k)) continue;
                if (IsLoggedInAny && _activeKind == k) { target = b; break; }
            }
            if (target == null || target.ActualWidth <= 0)
            {
                DrivePanThumb.Width = 0;
                return;
            }
            // 量出选中按钮相对切换条左上角的 X。
            // 🚨 2026-10-26 用户二次反馈：滑块必须**覆盖整个按钮**，相邻按钮的滑块首尾相接，
            //   切换时才是一条连续的线。原来这里取 ActualWidth - 26 再 +13 居中，
            //   四个网盘之间就露出 26px 的缝，看起来「不是一个整体，中间有断掉的一截」。
            double x;
            try { x = target.TranslatePoint(new Point(0, 0), DrivePanTabsRoot).X; }
            catch { return; }
            double w = target.ActualWidth;

            DrivePanThumb.Width = w;
            if (!animate)
            {
                DrivePanThumbShift.BeginAnimation(TranslateTransform.XProperty, null);
                DrivePanThumbShift.X = x;
                return;
            }
            var anim = new DoubleAnimation
            {
                To = x,
                Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            DrivePanThumbShift.BeginAnimation(TranslateTransform.XProperty, anim);
        }


        /// <summary>
        /// 点了「我的网盘」上方的某一家网盘：把当前账号切到那一家。
        /// <para>
        /// 为什么是"切账号"而不是"同时并排显示四家的文件"：
        /// 目录里的 fid 是**账号内的编号**，四家的 fid 空间完全不同；
        /// 下载、删除、转存这些动作也都要知道"这条文件属于哪个客户端"。
        /// 所以一家一家切是最不容易出错的做法 —— 每家的登录态都还在，
        /// 切回来不用重新登录。
        /// </para>
        /// </summary>
        private async void OnDrivePanTabClick(object sender, RoutedEventArgs e)
        {
            var fe = sender as FrameworkElement;
            PanKind kind;
            if (fe == null || !Enum.TryParse(fe.Tag as string, true, out kind))
                return;

            if (IsLoggedInAny && _activeKind == kind)
            {
                // 已经在这一家：当成"回到根目录 / 重新载入"
                if (!ShareMode)
                {
                    ResetNav();
                    await LoadDirAsync("0");
                }
                return;
            }

            // 🚨 2026-10-25 修「疯狂切换时提示某某网盘未登录」：
            //    每次点击先占一个序号，最后点的那次说了算；先点的那次回来发现
            //    序号已经变了，就静默作废（既不弹窗也不刷新 UI）。
            int seq = ++_panSwitchSeq;

            // 这次重建要让滑块滑过去（见 PlaceDrivePanThumb）。
            _panThumbAnimate = true;

            var btn = sender as Button;
            if (btn != null)
            {
                // ⚠️ 不改 Content：网盘页签的 Content 是一个「文字 + 选中横条」的
                //    StackPanel（2026-10-26 改版），直接赋字符串会把结构冲掉，
                //    切换过程中那一条会突然变成一行文字。只压透明度即可。
                btn.IsEnabled = false;
                btn.Opacity = 0.5;
            }

            // 串行化：同一时刻只允许一个切换在跑，否则两个切换会互相改 _activeKind。
            try
            {
                await _panSwitchGate.WaitAsync();
            }
            catch (Exception ex)
            {
                LogDetail("切换网盘排队失败：" + ex.Message);
                RefreshDrivePanTabs();
                return;
            }

            bool ok = false;
            try
            {
                // 排队期间用户又点了别家 → 这次请求已经过期，直接放弃。
                if (seq != _panSwitchSeq)
                    return;

                ok = await SwitchToPanAsync(kind);
            }
            catch (Exception ex)
            {
                Log("切换网盘失败：" + ex.Message);
            }
            finally
            {
                _panSwitchGate.Release();
                // ⚠️ 先把按钮/标签恢复再弹窗：AppDialog.Show 是模态的，
                //    放在 try 里会让「切换中…」和禁用态一直挂着，直到用户关掉弹窗
                //    （实测截图里那三个字就卡在标签上）。
                // 只有仍是最后一次点击时才重建（重建会把后面那次的禁用态抹掉）。
                if (seq == _panSwitchSeq)
                    RefreshDrivePanTabs();
            }

            // 被后面那一次点击取代：结果作废，绝不弹「还没登录」。
            if (seq != _panSwitchSeq)
                return;

            if (ok)
            {
                // 记住「最后浏览的是这一家」：下次启动直接打开它。
                // 🚨 只写 LastBrowsePanKind，**绝不碰** ActivePanKind / Who / AvatarUrl /
                //    CurrentAccountKey —— 那四项是「登录身份」，只由登录动作写入
                //    （2026-10-25 用户拍板的方案A，见 ActivateCachedPanAsync 里的注释）。
                RememberBrowsePan(kind);

                // 🚨 2026-10-25 修「有些网盘不显示容量」：容量原来只在启动验证阶段查一次，
                //    那次网络抖一下（迅雷还会静默吞异常）就永远空白，直到重启。
                //    切到这家时发现没有容量就补查一次。
                if (PanTotalOf(kind) <= 0)
                {
                    await FillPanCapacityAsync(kind);
                    if (seq == _panSwitchSeq)
                        RefreshPanStateList();
                }
                return;
            }

            AppDialog.Show(this,
                "还没有登录 " + PanUrlParser.DisplayNameOf(kind) + " 账号。\n\n"
                + "请到「登录」页点该网盘的卡片登录，登录成功后这里就能直接切过来。",
                "还没登录", MessageBoxButton.OK, MessageBoxImage.Information);
        }


        /// <summary>
        /// 把当前账号切到指定网盘（优先用该网盘最近保存的账号，其次用配置里的凭证）。
        /// </summary>
        /// <returns>切换后是否确实是这家网盘在用。</returns>
        private async Task<bool> SwitchToPanAsync(PanKind kind)
        {
            if (IsLoggedInAny && _activeKind == kind)
                return true;

            // ⓪ 这家网盘**已经登录过**（实例还在缓存里）→ 直接复用，不发任何校验请求。
            //    🚨 2026-10-25 用户反馈「我在我的网盘中切换网盘，在我的账号中又会重新登录，
            //    这也太浪费性能了」：原来这里不看缓存，每次都走下面的完整登录
            //    （new client → ValidateAsync → ResolveAccountInfoAsync，夸克还要起 Python 桥，
            //    实测 5~6 秒）。账号没变、凭证没变，没有任何理由重新登录一遍。
            if (await ActivateCachedPanAsync(kind))
                return IsLoggedInAny && _activeKind == kind;

            // ① 优先用这家网盘的**主账号**档案
            //    （GetPrimary 找不到主账号标记时会回退「最近使用」的那一个，
            //      与原来的「取 LoadAll 第一条」行为一致，升级后不会突然切错账号）
            AccountProfile acc = null;
            try
            {
                var primary = AccountStore.GetPrimary((int)kind);
                if (primary != null && !string.IsNullOrWhiteSpace(primary.Cookie))
                    acc = primary;
            }
            catch { }

            if (acc != null)
            {
                if (kind == PanKind.Aliyun || kind == PanKind.Xunlei)
                    await ApplyProviderLoginAsync(kind, acc.Cookie);
                else
                    await ApplyLoginAsync(acc.Cookie, kind);
                if (IsLoggedInAny && _activeKind == kind)
                    return true;
            }

            // ② 没有档案 → 用配置里保存的凭证兜底
            //    ⚠️ 阿里云盘有两种令牌：refresh_token（长效）与 access_token（约 2 小时）。
            //    新版阿里把 refresh_token 收到服务端 session 里，配置里往往只剩 access_token。
            //    只认 refresh_token 会误判「还没登录」（实测踩到：明明四卡都绿，
            //    点「阿里云盘」却弹「还没有登录」）。所以两个都要试，并用前缀告诉下游是哪一种。
            string cred;
            switch (kind)
            {
                case PanKind.Uc:
                    cred = _settings.UcCookie;
                    break;
                case PanKind.Aliyun:
                    cred = !string.IsNullOrWhiteSpace(_settings.AliyunRefreshToken)
                        ? "rt:" + _settings.AliyunRefreshToken
                        : (!string.IsNullOrWhiteSpace(_settings.AliyunAccessToken)
                            ? "at:" + _settings.AliyunAccessToken
                            : "");
                    break;
                case PanKind.Xunlei:
                    cred = _settings.XunleiPanToken;
                    break;
                default:
                    cred = _settings.Cookie;
                    break;
            }
            if (string.IsNullOrWhiteSpace(cred))
                return false;

            if (kind == PanKind.Aliyun || kind == PanKind.Xunlei)
                await ApplyProviderLoginAsync(kind, cred);
            else
                await ApplyLoginAsync(cred, kind);
            return IsLoggedInAny && _activeKind == kind;
        }


        /// <summary>
        /// 「当前状态」里的一行：网盘色点 + 名称 + 状态胶囊 + 副标题 + 「切换」。
        /// <para>
        /// 用户反馈两点（2026-10-25）：
        /// ① 「这里 ui 太丑了」—— 原来是裸 Grid + 上下分割线，四行挤在一起没有边界感，
        ///    现在每行是一块独立的圆角底卡，行间留 8px，一眼能数清几家网盘。
        /// ② 「为什么有个切换还只有一个」—— 原来只有「有账号档案且不是当前」才画按钮，
        ///    所以最常见的情况下只有一行有按钮。现在**每行都给「切换」**，
        ///    点开就是这一家网盘的账号列表（可切到同网盘的另一个账号）。
        /// </para>
        /// <para>
        /// 🎨 2026-11-01 再改（用户 m13541 第 4 项「去除 AI 味、更简约现代」）：
        /// ① 左侧 30×30 彩色方块 + 白色缩写 → 8px 品牌色圆点；
        /// ② 每行灰底圆角块 → 无底色 + 一条极淡分隔线（末行不画）；
        /// ③ 「切换」描边胶囊 → 无边框文字按钮。
        /// 三处合起来去掉的就是「一排同尺寸彩色块 + 一排同尺寸描边按钮」这种模板观感。
        /// </para>
        /// </summary>
        /// <param name="isLast">是否最后一行；非末行留 8px 行距。</param>
        private Border BuildPanStateRow(PanKind kind, AccountProfile acc, bool isLast)
        {
            bool active = IsLoggedInAny && _activeKind == kind;
            // 实测可用也算「已登录」—— 这就是用户要的"四家同时登录、每家一个账号"。
            bool live = active || PanIsLive(kind);
            bool expired = active && _loginExpired;
            // 🚨 昵称必须**按网盘各自取**，不能用全局的 _loginNick。
            //    用户实测的串位 bug：第一行是「夸克网盘」，副标题却是「迅雷用户」——
            //    因为 _loginNick 只有一个槽，被最近一次登录/刷新容量的那家覆盖。
            //    取值优先级：这家网盘实测到的昵称 → 这家网盘的账号档案 → 兜底全局昵称（仅当前行）。
            string nick = PanLiveNick(kind);
            if (string.IsNullOrEmpty(nick) && acc != null)
                nick = acc.DisplayName;
            if (string.IsNullOrEmpty(nick) && active)
                nick = _loginNick;
            // ⚠️ 不能只看账号档案：阿里/迅雷的凭证存在配置里（AliyunRefreshToken /
            //    XunleiPanToken），不一定有对应的账号档案 —— 只看档案会出现
            //    「上面的卡片写凭证已保存、下面这行写未登录」的自相矛盾。
            bool hasCred = acc != null || HasPanCredential(kind);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 左侧品牌色标记：8px 圆点。
            //
            // 🎨 2026-11-01 用户反馈「界面要有 AI 味，帮我简约现代一点」（m13541 第 4 项）。
            //    原来这里是 30×30 的**圆角色块 + 白色缩写字母**（夸 / UC / 阿 / 迅），
            //    四行并排就是四个彩色方块 —— 这正是模板生成最常见的「图标占位」观感：
            //    既不是真 logo，又要占一块实心面积，还让整行显得笨重。
            //    改成纯色圆点后，品牌色还在（四家仍一眼可分），但重量降到最低，
            //    视线自然落到右侧的网盘名与账号名上。
            var dot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = PanTintBrush(kind),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 12, 0),
            };
            Grid.SetColumn(dot, 0);
            grid.Children.Add(dot);

            // 第一行：网盘名 + 状态胶囊
            var line1 = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            line1.Children.Add(new TextBlock
            {
                Text = PanUrlParser.DisplayNameOf(kind),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)(TryFindResource("TextPrimaryBrush")
                    ?? new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C))),
                VerticalAlignment = VerticalAlignment.Center,
            });

            string pillText;
            string pillColorKey;
            if (expired)
            {
                pillText = "登录已过期";
                pillColorKey = "DangerBrush";
            }
            else if (live)
            {
                pillText = "已登录";
                pillColorKey = "SuccessBrush";
            }
            else if (hasCred)
            {
                pillText = "凭证已保存";
                pillColorKey = "CapacityMidBrush";
            }
            else
            {
                pillText = "未登录";
                pillColorKey = "TextTertiaryBrush";
            }
            var pillFg = (Brush)(TryFindResource(pillColorKey)
                ?? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));
            line1.Children.Add(new Border
            {
                Background = WithAlpha(pillFg, 0x1F),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 1, 6, 2),
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = pillText,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = pillFg,
                },
            });

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(line1);

            string sub;
            if (expired)
            {
                sub = "请到上方卡片重新登录";
            }
            else if (live)
            {
                // ⚠️ 这里**不要**再拼「已登录，可直接解析」：左边那个绿色胶囊已经写着「已登录」，
                //    拼出来的效果是「可直接解析  ·  已登录，可直接解析」（阿里没有昵称时尤其难看），
                //    而「可直接解析」本身对用户也没有信息量 —— 能列目录就能解析。
                //    这一行只放**这家网盘的具体身份**，没有身份就留一句中性的「已登录」。
                sub = nick ?? "";

                // 容量：**四家都显示**（2026-10-25 用户要求）。
                //
                // 原来这里写的是 `if (active && _loginTotal > 0)` —— 只有当前激活那一家
                // 才拼容量，其余三家明明已经实测通过、也有 GetSpaceAsync/GetCapacityAsync，
                // 却从来没查过，所以永远不显示。用户直接问过「为什么不是所有网盘都显示容量」。
                //
                // 现在优先用这一家自己的容量（验证阶段顺手查回来的），
                // 当前激活那一家在没有实测缓存时回落到 _loginUsed/_loginTotal。
                long capUsed = PanUsedOf(kind);
                long capTotal = PanTotalOf(kind);
                if (capTotal <= 0 && active && _loginTotal > 0)
                {
                    capUsed = _loginUsed;
                    capTotal = _loginTotal;
                }
                if (capTotal > 0)
                    sub += (sub.Length > 0 ? "  ·  " : "")
                         + "已用 " + Util.FormatSize(capUsed)
                         + " / 共 " + Util.FormatSize(capTotal);
                if (sub.Length == 0 && active)
                    sub = "当前使用中";
                // 非当前、又没有昵称（阿里拿不到昵称）→ 留空，不画副标题：
                // 胶囊已经写了「已登录」，再写一遍是噪音。
            }
            else if (hasCred)
            {
                sub = string.IsNullOrEmpty(nick) ? "可直接使用" : nick;
            }
            else
            {
                // 四行结构恒定，右侧「切换」按钮现在永远都在，文案与之一致。
                sub = "还没有登录，点上方卡片登录或右侧「切换」";
            }
            if (!string.IsNullOrEmpty(sub))
            {
                texts.Children.Add(new TextBlock
                {
                    Text = sub,
                    FontSize = 11,
                    Margin = new Thickness(0, 3, 0, 0),
                    Foreground = (Brush)(TryFindResource("TextSecondaryBrush")
                        ?? new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66))),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);

            // 🚨 2026-11-07：四行**永远**画「切换」按钮。
            //   原来 `if (hasCred || live)` 会让「全未登录」时四行右侧全空 ——
            //   加上副标题只剩一句灰字，整块看起来像消失了（用户反馈「很丑」）。
            //   现在四行结构恒定：网盘名 + 状态胶囊 + 副标题 + 切换，任何时候都饱满。
            //   没有凭证时点「切换」会打开该网盘的账号列表（空列表里有引导，不困惑）。
            {
                var switchBtn = new Button
                {
                    Content = "切换",
                    Padding = new Thickness(12, 3, 12, 3),
                    MinWidth = 54,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0),
                    Tag = kind.ToString(),
                };
                // 🎨 2026-11-01（用户 m13541 第 4 项「去 AI 味」）：
                //    原来这里是描边胶囊按钮。四行并排四个一模一样的描边胶囊，
                //    加上左边四个彩色方块，整块就是「一排同尺寸圆角块」的模板观感。
                //    改成无边框文字按钮（GhostButton）—— 平时只有灰字，hover 才提亮，
                //    和本界面其他次级操作（导出 Cookie、打开目录）保持同一语言。
                var ghostStyle = TryFindResource("GhostButton") as Style;
                if (ghostStyle != null)
                    switchBtn.Style = ghostStyle;
                switchBtn.Click += OnOpenAccounts;
                Grid.SetColumn(switchBtn, 2);
                // 2026-11-02 修「切换按钮选中框右侧被遮挡」：
                //   GhostButton 模板里的焦点环是 Margin=-3（向外扩张 3px），
                //   而这一列是最右列，外层卡片 Padding / ClipToBounds 把超出的 3px 裁掉了
                //   → 胶囊右边缺一截。这里给按钮留 4px 呼吸位，焦点环就完整了。
                //   ⚠️ 不能删 FocusRing —— 那是键盘无障碍的焦点指示。
                switchBtn.Margin = new Thickness(0, 0, 4, 0);
                grid.Children.Add(switchBtn);
            }

            return new Border
            {
                // 🎨 2026-11-01（用户 m13541 第 4 项「去 AI 味、更简约现代」）：
                //    原来是「每行一块 SurfaceAlt 灰底 + 10px 圆角」—— 四行就是四个
                //    一模一样的灰圆角块堆在一起，配合左边的彩色方块，正是模板化观感。
                //    现在改成**无底色 + 一条极淡分隔线**（末行不画线），
                //    和卡片标题区、账号列表同一套「线而非块」的语言。
                Background = Brushes.Transparent,
                BorderBrush = (Brush)(TryFindResource("BorderBrushSoft")
                    ?? new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xF0))),
                BorderThickness = new Thickness(0, 0, 0, isLast ? 0 : 1),
                Padding = new Thickness(2, 9, 2, 9),
                Child = grid,
            };
        }


        /// <summary>把资源画刷换成同色的半透明版本（状态胶囊底色用），主题切换后自动跟随。</summary>
        private static SolidColorBrush WithAlpha(Brush src, byte alpha)
        {
            var scb = src as SolidColorBrush;
            Color c = scb != null ? scb.Color : Color.FromRgb(0x88, 0x88, 0x88);
            return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        }


        /// <summary>
        /// 这家网盘在本机有没有可用的凭证（与 RefreshPanCards 的判据保持一致）。
        /// 阿里/迅雷的凭证存在配置里而不是账号档案里，必须单独查。
        /// </summary>
        private bool HasPanCredential(PanKind kind)
        {
            switch (kind)
            {
                case PanKind.Uc:
                    return !string.IsNullOrWhiteSpace(_settings.UcCookie);
                case PanKind.Aliyun:
                    return !string.IsNullOrWhiteSpace(_settings.AliyunRefreshToken)
                        || !string.IsNullOrWhiteSpace(_settings.AliyunAccessToken);
                case PanKind.Xunlei:
                    return !string.IsNullOrWhiteSpace(_settings.XunleiPanToken);
                default:
                    return !string.IsNullOrWhiteSpace(_settings.Cookie);
            }
        }


        /// <summary>四家网盘「各自都能用」的实测结果（只记成功，见 ValidatePanSessionsAsync）。</summary>
        private readonly Dictionary<PanKind, bool> _panLive = new Dictionary<PanKind, bool>();

        /// <summary>实测通过时拿到的昵称（四家各一份）。</summary>
        private readonly Dictionary<PanKind, string> _panNick = new Dictionary<PanKind, string>();

        /// <summary>
        /// 四家各自网盘的容量（已用 / 总量）。
        /// <para>
        /// 🚨 为什么不复用 <c>_loginUsed</c> / <c>_loginTotal</c>：那两个是**全局单值**，
        /// 只属于当前激活的那一家账号。用户问过「为什么不是所有网盘都显示容量」——
        /// 因为原来 <see cref="BuildPanStateRow"/> 里写的是 <c>if (active &amp;&amp; _loginTotal &gt; 0)</c>，
        /// 非当前的那三家从来没查过容量，自然没得显示。这里给每家存一份。
        /// </para>
        /// </summary>
        private readonly Dictionary<PanKind, long> _panUsed = new Dictionary<PanKind, long>();

        private readonly Dictionary<PanKind, long> _panTotal = new Dictionary<PanKind, long>();

        private bool _panLiveValidating;

        private bool _panLiveDone;


        /// <summary>
        /// 这家网盘是否已实测可用（当前账号天然算可用）。
        /// <para>
        /// 🚨 2026-11-03 修「明明已登录却显示未登录」：
        /// 原来判据散在三处（<c>PanIsLive</c> / <c>HasPanCredential</c> /
        /// <c>IsLoggedInAny</c>），各写各的，于是出现「有凭证但标记被清 → 显示未登录」。
        /// 现在统一由 <see cref="PanUsable"/> 提供单一结论，所有显示处都调它。
        /// </para>
        /// </summary>
        private bool PanIsLive(PanKind kind)
        {
            if (IsLoggedInAny && _activeKind == kind)
                return true;
            bool ok;
            return _panLive.TryGetValue(kind, out ok) && ok;
        }


        /// <summary>
        /// 这家网盘「能不能用」的**唯一判据**（登录页四卡、网盘切换页签都调它）。
        /// <para>
        /// 🚨 2026-11-03 修「已登录却显示未登录」：
        /// 原来各处自己拼「有凭证 || 实测通过」，而 <c>InvalidatePanLive</c> 会作废
        /// 实测标记 —— 于是凭证明明在、只是标记被清了一下，界面就退回「未登录」。
        /// 现在统一成：**有凭证就算能用**（凭证是否过期由校验去判断，
        /// 不该让显示层替它下结论）。
        /// </para>
        /// </summary>
        private bool PanUsable(PanKind kind)
        {
            return HasPanCredential(kind) || PanIsLive(kind);
        }


        /// <summary>实测通过时拿到的昵称；没有就返回空串。</summary>
        private string PanLiveNick(PanKind kind)
        {
            string n;
            if (_panNick.TryGetValue(kind, out n) && !string.IsNullOrEmpty(n))
            {
                // 🚨 2026-11-02：**以磁盘为准**纠正内存脏数据。
                //   昵称串台的根因是 _panNick 被写进了错误的槽位（见 SyncLoginPageState），
                //   而 _panNick 是纯内存字典 —— 重启就清空，所以用户观察到「重启后恢复」。
                //   但如果不重启，脏数据会一直显示。这里每次取值时与账号档案比对：
                //   档案（磁盘）是持久事实，内存只是缓存，不一致就以磁盘为准并就地修正。
                try
                {
                    var primary = AccountStore.GetPrimary((int)kind);
                    string truth = primary != null ? primary.DisplayName : null;
                    if (!string.IsNullOrEmpty(truth) && !string.Equals(truth, n, StringComparison.Ordinal))
                    {
                        LogDetail(PanUrlParser.DisplayNameOf(kind) + " 昵称与账号档案不一致（缓存 "
                            + n + " / 档案 " + truth + "），已按档案纠正。");
                        _panNick[kind] = truth;
                        return truth;
                    }
                }
                catch { }
                return n;
            }
            return "";
        }


        /// <summary>这家网盘已用字节数；未知返回 -1。</summary>
        private long PanUsedOf(PanKind kind)
        {
            long v;
            return _panUsed.TryGetValue(kind, out v) ? v : -1L;
        }


        /// <summary>这家网盘总字节数；未知返回 -1。</summary>
        private long PanTotalOf(PanKind kind)
        {
            long v;
            return _panTotal.TryGetValue(kind, out v) ? v : -1L;
        }


        /// <summary>
        /// 顺手把这一家网盘的容量查回来，填进 <see cref="_panUsed"/> / <see cref="_panTotal"/>。
        /// <para>
        /// 只读**已经验证通过的缓存实例**（<see cref="_quarkCache"/> / <see cref="_providerCache"/>），
        /// 不新建客户端 —— 四家验证阶段本来就已经把可用实例预热进去了。
        /// </para>
        /// <para>
        /// 任何异常都吞掉：容量是**装饰信息**，查不到就只是这一行不显示容量，
        /// 绝不能因为它把「已登录」的结论也带崩。
        /// </para>
        /// </summary>
        private async Task FillPanCapacityAsync(PanKind kind)
        {
            try
            {
                long used;
                long total;

                if (kind == PanKind.Quark || kind == PanKind.Uc)
                {
                    QuarkClient c;
                    if (!_quarkCache.TryGetValue(kind, out c) || c == null)
                        return;
                    var cap = await c.GetCapacityAsync().ConfigureAwait(true);
                    total = cap.total;
                    used = cap.used;
                }
                else
                {
                    IPanProvider p;
                    if (!_providerCache.TryGetValue(kind, out p) || p == null)
                        return;
                    var sp = await p.GetSpaceAsync().ConfigureAwait(true);
                    if (sp == null)
                        return;
                    total = sp.Total;
                    used = sp.Used;
                }

                if (total > 0)
                {
                    _panUsed[kind] = used;
                    _panTotal[kind] = total;
                }
                else
                {
                    // 🚨 原来这里什么都不说：迅雷 GetSpaceAsync 内部 catch 后返回
                    //    Total=0，界面就一直空白，日志里一个字都没有，根本查不出来。
                    //    现在至少留一行，下次再有人问「为什么不显示容量」有据可查。
                    LogDetail(PanUrlParser.DisplayNameOf(kind)
                        + " 容量查询返回 0（接口没给 limit / 凭证不含容量），这一行暂时不显示容量。");
                }
            }
            catch (OperationCanceledException)
            {
                // 切换/退出导致的取消：不是错误，什么都不做。
            }
            catch (Exception ex)
            {
                // 容量查不到不影响登录结论，但必须留痕（原来这里是空 catch）。
                LogDetail(PanUrlParser.DisplayNameOf(kind) + " 容量查询失败：" + ex.Message);
            }
        }


        /// <summary>
        /// 启动预热（夸克 / UC）：把刚验证通过的客户端实例直接放进切换缓存。
        /// <para>
        /// 🚨 为什么必须在这里做：<see cref="SwitchToPanAsync"/> 缓存未命中时会退回完整登录，
        /// 夸克还要走 Python 桥取昵称（实测 5~6 秒）。启动时四家本来就各验一次，
        /// 顺手把验证通过的实例留下来，之后任何一家第一次切换都是零网络。
        /// 用户原话：「为什么每次在我的网盘切换网盘，在我的账号中又会重新登录，这也太浪费性能了」。
        /// </para>
        /// <para>
        /// ⚠️ 当前激活那一家不缓存：它的实例已经挂在 <c>_client</c> 上，再存一份会让
        /// <see cref="DropCachedPan"/> 的 ReferenceEquals 保护失效（同一实例两处引用 → 泄漏）。
        /// </para>
        /// </summary>
        private void PrewarmQuarkClient(PanKind kind, QuarkClient client)
        {
            if (client == null)
                return;
            if (IsLoggedInAny && _activeKind == kind)
            {
                // 这家已经在用了，手里这份只是临时验证实例，直接扔掉。
                try { client.Dispose(); } catch { }
                return;
            }
            QuarkClient old;
            if (_quarkCache.TryGetValue(kind, out old) && old != null && !ReferenceEquals(old, client))
            {
                try { old.Dispose(); } catch { }
            }
            _quarkCache[kind] = client;
        }


        /// <summary>启动预热（阿里 / 迅雷）：理由与 <see cref="PrewarmQuarkClient"/> 完全相同。</summary>
        private void PrewarmProvider(PanKind kind, IPanProvider provider)
        {
            if (provider == null)
                return;
            if (IsLoggedInAny && _activeKind == kind)
            {
                try { provider.Dispose(); } catch { }
                return;
            }
            IPanProvider old;
            if (_providerCache.TryGetValue(kind, out old) && old != null && !ReferenceEquals(old, provider))
            {
                try { old.Dispose(); } catch { }
            }
            _providerCache[kind] = provider;
        }


        /// <summary>
        /// 逐家用**它自己保存的凭证**真发一次校验请求，把「能用」记下来。
        /// <para>
        /// 用户要求四家网盘同时保持登录（每家一个账号），解析链接时不必提前切账号。
        /// 解析侧本来就是这个行为（各家用自己保存的凭证现建客户端），但界面上必须能看出来
        /// 每家到底还能不能用 —— 所以这里真验一次，而不是只看"配置里有没有字符串"。
        /// </para>
        /// <para>
        /// ⚠️ 只记成功、不记失败：校验失败可能是网络抖动 / 代理 / 服务端限流，
        /// 记成"失效"会误导用户去重新登录。失败就退回「凭证已保存」这个保守说法。
        /// </para>
        /// </summary>
        private async void ValidatePanSessionsAsync()
        {
            await ValidatePanSessionsAsync(null).ConfigureAwait(true);
        }

        /// <summary>
        /// 只验指定的一家（或全部，<paramref name="onlyKind"/> 为 <c>null</c> 时）。
        /// <para>
        /// 🚨 2026-10-25：原来切换账号时会四家全部重验一遍 —— 夸克那一家要走
        /// OpenSSL 桥 + WAF 兜底，一次 1.5~3 秒，四家串起来就是「切换一次卡好几秒」，
        /// 用户原话「一切换账号都会把所有账号重新获取一次」。切换只影响一家，
        /// 其余三家的实测结果没有任何理由作废。
        /// </para>
        /// </summary>
        private async Task ValidatePanSessionsAsync(PanKind? onlyKind)
        {
            // ⚠️ 启动早期 SyncLoginPageState 可能在 LoadSettingsToUi 之前就被调到，
            //    那时 _settings 还是 null —— 这里必须挡一下（实测过 NRE 崩溃）。
            if (_settings == null)
                return;
            if (_panLiveValidating)
                return;
            _panLiveValidating = true;
            // 启动预热：让「当前状态」卡右上角转起来，明确告诉用户"还在验证，别急"。
            SetPanStateBusy(true);
            try
            {
                var kinds = onlyKind.HasValue
                    ? new[] { onlyKind.Value }
                    : new[] { PanKind.Quark, PanKind.Uc, PanKind.Aliyun, PanKind.Xunlei };
                foreach (var kind in kinds)
                {
                    try
                    {
                        if (!HasPanCredential(kind))
                            continue;
                        bool ok = false;
                        string nick = "";
                        if (kind == PanKind.Quark || kind == PanKind.Uc)
                        {
                            string cookie = kind == PanKind.Quark ? _settings.Cookie : _settings.UcCookie;
                            // 🚨 刻意不用 using：验证通过的实例要留下来做「启动预热」，
                            //    之后切到这家就是零网络激活（见 PrewarmQuarkClient）。
                            var c = new QuarkClient(cookie ?? "", kind);
                            try
                            {
                                var r = await c.ValidateAsync().ConfigureAwait(true);
                                ok = r.Item1;
                                nick = r.Item2;
                            }
                            catch
                            {
                                try { c.Dispose(); } catch { }
                                throw;
                            }
                            if (ok)
                                PrewarmQuarkClient(kind, c);
                            else
                                try { c.Dispose(); } catch { }
                        }
                        else
                        {
                            // 同样不用 using —— 理由见上。
                            var p = CreateProviderFor(kind);
                            if (p == null || !p.IsLoggedIn)
                            {
                                // ⚠️ AliyunClient.IsLoggedIn 只判 refresh_token（见其 :187），
                                //    而阿里新版网页只给两小时就过期的 access_token —— 这种
                                //    情况下这里为 false 但凭证其实是"需要换一张新的"，
                                //    不是"没登录"。交给自动续期去重开内嵌登录抓新令牌。
                                if (p != null)
                                    try { p.Dispose(); } catch { }
                                if (kind == PanKind.Aliyun)
                                    TryAutoRenewAliyunCredential("缺少可续期的令牌");
                                continue;
                            }
                            try
                            {
                                var r = await p.ValidateAsync().ConfigureAwait(true);
                                ok = r.Item1;
                                nick = r.Item2;
                            }
                            catch
                            {
                                try { p.Dispose(); } catch { }
                                throw;
                            }
                            if (ok)
                                PrewarmProvider(kind, p);
                            else
                                try { p.Dispose(); } catch { }
                        }
                        if (ok)
                        {
                            _panLive[kind] = true;
                            // 实测通过 ⇒ 登录态是好的，撤掉「已过期」红字（见 ShowLoginExpired）。
                            if (kind == _activeKind)
                                _loginExpired = false;
                            _panNick[kind] = nick ?? "";
                            // 拿接口返回的真实昵称回写档案 —— 修掉早期「昵称只有一个全局
                            // 槽位」留下的脏数据（实测 UC 档案里存着夸克账号的昵称）。
                            //
                            // 🚨🚨 夸克**不能**走这条路：account/info 挂在 pan.quark.cn，
                            //    .NET 的 SChannel 必然被 WAF RST，所以 ValidateAsync 给的是
                            //    兜底的「已登录用户」（见 MainWindow.Misc.cs:592 那段注释）。
                            //    拿它回写会把好名字覆盖掉 —— 用户实测过「刷新之后我的名字没了」，
                            //    2026-10-25 我又踩了一次（雨过晴 → 已登录用户）。
                            //    夸克真名只能靠 ResolveAccountInfoAsync 走 Python 桥，
                            //    那条路在 ApplyLoginAsync 里已经 Save 过，不需要这里补。
                            try
                            {
                                if (kind != PanKind.Quark
                                    && !string.IsNullOrEmpty(nick)
                                    && !string.Equals(nick, FallbackNick, StringComparison.Ordinal))
                                {
                                    string plain = kind == PanKind.Uc ? _settings.UcCookie
                                                 : kind == PanKind.Aliyun
                                                    ? (!string.IsNullOrEmpty(_settings.AliyunRefreshToken)
                                                        ? _settings.AliyunRefreshToken : _settings.AliyunAccessToken)
                                                    : _settings.XunleiPanToken;
                                    AccountStore.UpdateNickByCookie((int)kind, plain, nick);
                                }
                            }
                            catch { }
                            // 顺手把这家的容量也查回来 —— 用户要的是四行都显示容量，
                            // 而不是只有当前激活那一家有。
                            await FillPanCapacityAsync(kind).ConfigureAwait(true);
                        }
                        else
                        {
                            // 🚨 2026-10-28：验证失败必须**撤掉在线标记**。
                            //    原来这里只写日志，_panLive[kind] 保持上次的 true，
                            //    于是凭证真的过期了，页签上还一直显示「已登录」——
                            //    用户点进去才发现什么都列不出来。
                            _panLive[kind] = false;
                            LogDetail(PanUrlParser.DisplayNameOf(kind)
                                + " 登录态实测未通过（凭证可能已过期，解析时会用保存的凭证现建客户端）。");

                            // 阿里令牌约两小时就过期，而且没有 refresh 接口可换 —— 唯一可靠的
                            // 续期手段是重开一次内嵌登录（profile 里的会话还活着，页面自己就登好了）。
                            // 用户要求做成全自动：察觉失效就续期，不需要点任何按钮。
                            if (kind == PanKind.Aliyun)
                                TryAutoRenewAliyunCredential("校验未通过");
                        }
                    }
                    catch (Exception ex)
                    {
                        // 出错同样不能算「在线」：抛异常说明这次根本没验证成功。
                        _panLive[kind] = false;
                        LogDetail(PanUrlParser.DisplayNameOf(kind)
                            + " 登录态实测出错：" + ex.Message);
                    }
                }
            }
            finally
            {
                _panLiveValidating = false;
                _panLiveDone = true;
                // 验证结束，收起「正在验证登录态…」。
                SetPanStateBusy(false);
            }
            RefreshPanCards();
            // 四卡实测结束 = 登录态已确定，这一帧必须重建，不能被节流吞掉。
            RefreshPanStateList(force: true);
        }


        /// <summary>
        /// 凭证变了（重新登录 / 粘贴新值）→ 之前那次实测结果作废，下次重新验。
        /// <para>
        /// <paramref name="onlyKind"/> 有值时只作废那一家 —— 切一个账号不该把
        /// 另外三家的「已登录」实测结论一起扔掉（否则会连带触发四家重验）。
        /// </para>
        /// </summary>
        private void InvalidatePanLive(PanKind? onlyKind = null)
        {
            if (onlyKind.HasValue)
            {
                _panLive.Remove(onlyKind.Value);
                _panNick.Remove(onlyKind.Value);
                // 容量也跟着作废：换了账号，旧账号的容量不能留在界面上。
                _panUsed.Remove(onlyKind.Value);
                _panTotal.Remove(onlyKind.Value);
                // 只作废一家时**不能**把 _panLiveDone 置 false：那等于告诉调用方
                // 「四家都还没验过」，会立刻触发一次全量重验。
                return;
            }
            _panLive.Clear();
            _panNick.Clear();
            _panUsed.Clear();
            _panTotal.Clear();
            _panLiveDone = false;
        }


        /// <summary>四家网盘的品牌色（只用于状态列表左侧小色块）。</summary>
        private Brush PanTintBrush(PanKind kind)
        {
            Color c;
            switch (kind)
            {
                case PanKind.Uc: c = Color.FromRgb(0xF0, 0xA0, 0x20); break;
                case PanKind.Aliyun: c = Color.FromRgb(0xFF, 0x7A, 0x45); break;
                case PanKind.Xunlei: c = Color.FromRgb(0x2B, 0x7F, 0xFF); break;
                default: c = Color.FromRgb(0x4F, 0x6B, 0xED); break;
            }
            return new SolidColorBrush(c);
        }


        /// <summary>色块里的短名（26px 宽塞不下「阿里云盘」）。</summary>
        private string PanShortName(PanKind kind)
        {
            switch (kind)
            {
                case PanKind.Uc: return "UC";
                case PanKind.Aliyun: return "阿";
                case PanKind.Xunlei: return "迅";
                default: return "夸";
            }
        }


        /// <summary>
        /// 同步两个列表的「空状态插画」可见性。
        /// 纯皮肤逻辑：集合没内容（或筛选后全被过滤掉）时显示提示，否则隐藏。
        /// 现在还会参考"是否正在加载"：
        /// 加载中显示"正在加载…"，加载完且为空才显示"还没有内容"。
        /// 不加这个判断的话，加载的几百毫秒里会闪出"还没有内容"，误导用户。
        /// </summary>
        private void UpdateEmptyStates()
        {
            try
            {
                if (FileEmptyState != null)
                {
                    // 用筛选视图的计数：筛选后一条不剩时也应该显示空状态；
                    // 但筛选是本地过滤，跟"正在加载"无关 —— 筛选出 0 条时
                    // 仍应显示空状态，不该显示"正在加载"。
                    // 没有筛选词时 _files.Count 就是可见条数，**不必整表枚举**视图 ——
                    // 几千项的目录里，这个枚举是卡顿的主因之一。
                    int n = string.IsNullOrEmpty(_fileFilter)
                        ? _files.Count
                        : (_filesView != null ? _filesView.Cast<object>().Count() : _files.Count);
                    bool loading = _loadingDir && _files.Count == 0 && string.IsNullOrEmpty(_fileFilter);

                    if (FileLoadingState != null)
                        FileLoadingState.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;

                    FileEmptyState.Visibility = (!loading && n == 0)
                        ? Visibility.Visible : Visibility.Collapsed;

                    // 空状态的文案按模式区分：分享模式该说"粘贴链接"，
                    // 网盘模式该说"这个目录是空的"。一句通用文案两边都不贴切。
                    if (FileEmptyText != null)
                        FileEmptyText.Text = ShareMode ? "还没有内容" : "这个目录是空的";
                }
                if (TaskEmptyState != null)
                    TaskEmptyState.Visibility = _tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }


        /// <summary>
        /// 合并版 <see cref="UpdateEmptyStates"/>：置个标记，攒到本轮 UI 空闲时才算一次。
        /// 往列表里铺目录时集合会连着变很多次，而 UpdateEmptyStates
        /// 要数一遍可见条数（有筛选词时是整表枚举）—— 一次铺几千条就是 O(n²)，
        /// 界面直接卡死（几千项目录的用户实测反馈）。合并之后一批变化只算一次。
        /// </summary>
        private void RequestUpdateEmptyStates()
        {
            if (_emptyStatePending) return;
            _emptyStatePending = true;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _emptyStatePending = false;
                    UpdateEmptyStates();
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { _emptyStatePending = false; }
        }
    }
}
