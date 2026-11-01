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
// 职责：未完成任务（续传）询问与恢复

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
        /// 把当前"未完成"的任务（排队/下载/暂停）写进持久化记录，
        /// 已完成的从记录里移除。供下次启动提示"有 N 个未完成任务"。
        /// 节流：只有"未完成任务的名字 + 状态 + 落盘路径"组合变了才真正写盘 ——
        /// OnJobUpdate 每 500ms 就来一次，无条件写盘等于持续小额 IO。
        /// </summary>
        /// <remarks>
        /// 🚨 2026-10-28：这里**必须合并，不能整体覆盖**。
        /// <para>
        /// 原来只遍历 <c>_tasks</c> 就 <c>SetPending(list)</c>，而 <c>_tasks</c> 里**没有**
        /// 那些「启动时弹续传框、用户点了稍后再说」的任务 —— 它们只存在于磁盘记录里。
        /// 于是只要列表发生任何一次增删（<c>_tasks.CollectionChanged</c> 直接挂在这里），
        /// 整份记录就被只含当前可见任务的列表覆盖掉：未完成任务入口消失、
        /// 分片全部变成永远没人回收的孤儿。
        /// </para>
        /// <para>
        /// 现在的语义是「**以 _tasks 为准做增量**」：
        /// ① <c>_tasks</c> 里非终态的任务 → 写入/更新；
        /// ② <c>_tasks</c> 里已经到终态的任务 → 不再写入（等于从记录里删掉）；
        /// ③ 记录里存在、但 <c>_tasks</c> 里**根本没有对应行**的 → 原样保留。
        /// </para>
        /// <para>
        /// ③ 是这次修复的关键：没有对应行不代表任务没了，只代表它这一轮没被载入。
        /// 用 <c>Dest</c> 做键（下载路径由 <c>UniquePath</c> 保证唯一），
        /// 不用 Name —— 同名不同路径的任务是合法的。
        /// </para>
        /// <para>
        /// ⚠️ <c>Cancelling</c> 必须算「非终态」：用户点取消后引擎正在
        /// <c>ClearPartial</c> 清分片，此时强杀进程 → 记录被丢掉、分片却没清完 →
        /// 那几 GB 分片和半截目标文件从此没有任何入口能看到、也永远不会被回收。
        /// </para>
        /// </remarks>
        private void SyncPending()
        {
            try
            {
                // 🚨 先算「状态分布」轻量指纹，没变就直接返回。
                // 进度帧（每秒上百次）绝不可能改变 pending 内容，但下面那段会
                // 遍历全部任务拼字符串 + 调 HistoryStore.GetPending()（读盘 + 反序列化 +
                // DPAPI 解密）。2000 个任务时那就是每秒上百次全量读盘 → 界面彻底卡死。
                // 这个循环只读 State 枚举，2000 个任务也就几微秒。
                int nQueued = 0, nDown = 0, nPaused = 0, nCancel = 0;
                foreach (var t in _tasks)
                {
                    switch (t.State)
                    {
                        case JobState.Queued: nQueued++; break;
                        case JobState.Downloading: nDown++; break;
                        case JobState.Paused: nPaused++; break;
                        case JobState.Cancelling: nCancel++; break;
                    }
                }
                // 任务总数也要进去：删任务/加任务可能不改变上面四个计数
                // 🚨 Queued 与 Downloading 合并计数：pending 记录里两者的**内容完全相同**
                //    （都写 Name|State|Dest，State 变化只影响这一个字段），
                //    但 30 路并发每秒会有几十个任务 Queued→Downloading，
                //    分开计数就等于每秒触发几十次全量读写。合并后这条迁移不再触发。
                int light = _tasks.Count * 100000 + (nQueued + nDown) * 1000 + nPaused * 10 + nCancel;
                if (light == _pendingLightSig)
                    return;
                _pendingLightSig = light;

                // 🚨 顺次取链期间：每块 30 个入队都会让 _tasks.Count 变一次 →
                // 上面那个指纹失效 → 走到下面「读整个 state.json + 8000 次 DPAPI 解密
                // + 加密写盘」。2000 个文件 = 67 块 = 67 次全量往返，UI 线程直接冻死。
                // 这里改成「每跳过 10 次才真落一次」，取链期间的开销降到 1/10。
                if (_pendingSuppress > 0 && ++_pendingSkipped < _pendingSkipLimit)
                    return;
                _pendingSkipped = 0;

                var sb = new StringBuilder();
                var list = new List<PendingTask>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in _tasks)
                {
                    // 更新包下载不持久化：跨重启续传没有意义，且避免 Pending 信息残缺
                    if (t.Job.SkipPersist)
                        continue;
                    if (t.State == JobState.Queued || t.State == JobState.Downloading
                        || t.State == JobState.Paused || t.State == JobState.Cancelling)
                    {
                        // 签名里带 Dest：原来只有 Name|State，导致同一任务换了落盘路径
                        // （用户改下载目录后重新入队）时签名不变、新路径永远不落盘，
                        // 重启后 Size/Stoken 全是旧的。
                        sb.Append(t.Name).Append('|').Append(t.State)
                          .Append('|').Append(t.Job.Dest).Append(';');
                        var pi = t.Job.Pending;
                        list.Add(new PendingTask
                        {
                            Name = t.Name,
                            Dest = t.Job.Dest,
                            Url = t.Job.Url,
                            Fid = pi?.Fid,
                            Token = pi?.Token,
                            FromShare = pi?.FromShare ?? false,
                            Size = t.Total,
                            StreamPlay = true,      // 共写模式已删除，恒可边下边播
                            StreamPlayMode = true,
                            Status = t.StatusText,
                            // ---- 续传所需（重启后重取链）----
                            PwdId = pi?.PwdId,
                            Stoken = pi?.Stoken,
                            Passcode = pi?.Passcode,
                            RelDir = pi?.RelDir,
                            // 自动重解析分享的上下文（stoken 过期自愈用）
                            StartFid = pi?.StartFid,
                            ShareRelDir = pi?.ShareRelDir,
                            Md5 = pi?.Md5,
                            Sha1 = pi?.Sha1,
                            Sha256 = pi?.Sha256,
                            // 来源网盘：0=夸克。续传时据此选客户端，不能丢。
                            PanKindValue = pi?.PanKind ?? 0,
                        });
                        if (!string.IsNullOrEmpty(t.Job.Dest))
                            seen.Add(t.Job.Dest);
                    }
                }

                // ③ 保留磁盘上有、但这一轮没进 _tasks 的记录。
                List<PendingTask> existing = null;
                try { existing = HistoryStore.GetPending(); }
                catch { }
                if (existing != null)
                {
                    foreach (var old in existing)
                    {
                        if (old == null) continue;
                        if (string.IsNullOrEmpty(old.Dest)) continue;
                        if (seen.Contains(old.Dest)) continue;
                        // 同一份记录里也不能出现两条相同 Dest（历史脏数据兜底）
                        if (!seen.Add(old.Dest)) continue;
                        list.Add(old);
                    }
                }

                string sig = sb.ToString();
                // 记录条数也要进签名：③ 那部分变化不会体现在 sb 里，
                // 不带上就会「保留了新记录但签名没变 → 不写盘」。
                if (existing != null && existing.Count != list.Count)
                    sig += "#" + list.Count;
                if (sig == _pendingSig)
                    return;
                _pendingSig = sig;
                HistoryStore.SetPending(list);
                RefreshPendingEntry();
            }
            catch { }
        }


        // ---------------- 任务管理 ----------------

        /// <summary>
        /// 工具栏那个合并后的「暂停全部 / 恢复全部」按钮 —— 按当前任务状态决定做什么。
        /// </summary>
        /// <remarks>
        /// 用户反馈「暂停全部和恢复全部可以弄成一个按钮」。原来两个独立按钮
        /// 在任何时刻都只有一个是"有意义"的（没在下载时点「暂停全部」什么也不会发生），
        /// 合并成一个会自己换文案/图标的按钮更省地方也更清楚。
        /// </remarks>
        private void OnPauseAllOrResume(object sender, RoutedEventArgs e)
        {
            bool anyRunning = _tasks.Any(t => t.State == JobState.Downloading || t.State == JobState.Queued);
            if (anyRunning)
            {
                foreach (var t in _tasks)
                {
                    if (t.State == JobState.Downloading || t.State == JobState.Queued)
                    {
                        t.Job.PauseJob();
                        // 与单个暂停同理：先置标志再改状态，否则进度上报会把 Paused 覆盖掉
                        t.PauseRequested = true;
                        t.State = JobState.Paused;
                    }
                }
            }
            else
            {
                foreach (var t in _tasks)
                {
                    if (t.State == JobState.Paused)
                    {
                        t.PauseRequested = false;
                        t.Job.ResumeJob();
                        t.State = JobState.Downloading;
                    }
                }
            }
            UpdatePauseAllButton(force: true);
        }


        private void OnTaskResume(object sender, RoutedEventArgs e)
        {
            foreach (var t in TasksFromMenu(sender)) ResumeTask(t);
        }


        /// <summary>恢复单个任务（右键菜单与行内按钮共用）。</summary>
        private void ResumeTask(TaskItem t)
        {
            if (t == null) return;
            if (t.State == JobState.Paused)
            {
                t.PauseRequested = false;   // 先清标志，否则会继续忽略核心的 Downloading
                t.Job.ResumeJob();
                t.State = JobState.Downloading;
                Log("已恢复: " + t.Name);
            }
            else
            {
                Log("该任务当前状态不可恢复（" + t.StatusText + "）");
            }
        }


        private void ShowPendingOnStartup()
        {
            List<PendingTask> all;
            try { all = HistoryStore.GetPending(); }
            catch { return; }
            if (all == null || all.Count == 0)
                return;

            foreach (var p in all)
                LogDetail("  - " + p.Name + "  [" + (p.Status ?? "") + "]");

            var resumable = all.Where(p => p != null && p.CanResume).ToList();
            var notResumable = all.Where(p => p == null || !p.CanResume).ToList();

            var dlg = new Window
            {
                Owner = this,
                Title = "未完成的任务",
                ResizeMode = ResizeMode.CanResize,
            };
            DialogChrome.StyleWindow(dlg, 600, 480);

            var root = DialogChrome.DialogRoot();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 标题已画在自绘标题栏里（2026-10-26），内容区不再重复。
            var head = DialogChrome.Tip(string.Format(
                "上次有 {0} 个任务未完成，其中 {1} 个可以继续下载。\n"
                + "勾选后点「继续下载」会重新获取直链，接着已下载的部分继续（不会重下）。",
                all.Count, resumable.Count), 8, 12);
            Grid.SetRow(head, 1);
            root.Children.Add(head);

            // 「续传界面的文件前面有个对勾太误导人了，
            //  让人以为已经自动勾选了」。
            //  原来是在 Display 字符串前面拼了一个 "✓"（表示"可续传"），可它长得
            //  就像"已勾选"；再加上 ListBox 的选中蓝底，整行看起来就是"已经选好了"，
            //  于是用户以为可以直接点「继续下载」，结果按钮是灰的（一个都没真勾上）。
            //  → 换成**真正的复选框**：默认全部不勾，勾选态与按钮可用性直接联动。
            var wrappers = new List<PendingRow>();
            foreach (var p in all) wrappers.Add(new PendingRow(p));

            var selAll = DialogChrome.SecondaryButton("全选可续传");
            var resumeBtn = DialogChrome.SuccessButton("继续下载");
            var discardBtn = DialogChrome.DangerButton("丢弃这些任务");
            var closeBtn = DialogChrome.SecondaryButton("稍后再说");

            // 用 ListBox + 复选（ItemsSource 为包装对象，便于双向绑定 IsChecked）
            var box = new ListBox
            {
                SelectionMode = SelectionMode.Single,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4),
            };
            var plainStyle = TryFindResource("PlainListBox") as Style;
            if (plainStyle != null) box.Style = plainStyle;
            // ⚠️ **必须**给 ItemTemplate：ListBox 的项没有模板时会调 ToString()，
            //    而 PendingRow 没重写它 → 用户看到一列
            //    「GeZi.MainWindow+PendingRow」（用户反馈：「续传的时候为什么都
            //    显示同样的东西，不应该显示需要续传的文件名吗」）。
            box.ItemTemplate = BuildPendingRowTemplate();
            foreach (var w in wrappers) box.Items.Add(w);

            var boxCard = DialogChrome.Card(box);
            boxCard.Padding = new Thickness(0);
            Grid.SetRow(boxCard, 2);
            root.Children.Add(boxCard);

            var btns = DialogChrome.ButtonRow(selAll, resumeBtn, discardBtn, closeBtn);
            btns.Margin = new Thickness(0, 16, 0, 0);
            Grid.SetRow(btns, 3);
            root.Children.Add(btns);

            dlg.Content = root;

            // 「没勾任何文件时『继续下载』是灰的，用不了」。
            // 这个禁用态本身是对的（一个都没勾时点它没有任何意义），
            // 真正的问题是上面那个假对勾让人以为已经勾好了 —— 现在换成真复选框，
            // 并让按钮状态**实时跟着勾选走**：勾一个立刻变亮，全取消立刻变灰。
            Action syncResumeBtn = () =>
            {
                resumeBtn.IsEnabled = wrappers.Any(w => w.IsChecked && w.CanResume);
            };
            foreach (var w in wrappers)
            {
                w.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(PendingRow.IsChecked)) syncResumeBtn();
                };
            }
            syncResumeBtn();

            bool doResume = false;
            bool doDiscard = false;

            selAll.Click += (s, e) =>
            {
                foreach (var w in wrappers)
                    if (w.CanResume) w.IsChecked = true;
            };
            resumeBtn.Click += (s, e) => { doResume = true; dlg.DialogResult = true; };
            discardBtn.Click += (s, e) => { doDiscard = true; dlg.DialogResult = true; };
            closeBtn.Click += (s, e) => { dlg.DialogResult = false; };

            // 默认**一个都不勾**（原来是"全部设 false"，效果一样，但写在这里容易被
            // 误读成"有默认勾选"）。勾选完全交给用户，按钮状态跟着走。
            syncResumeBtn();

            dlg.ShowDialog();

            // 弹窗关掉后立刻刷新「未完成」入口：用户选了续传/丢弃，计数要跟着变；
            // 点了「稍后再说」则入口继续留着，随时能再打开（见 OnOpenPendingEntry）。
            RefreshPendingEntry();

            if (doDiscard)
            {
                // 丢弃 = 清 pending + 清该任务的全部本地残留
                //（分片目录 / 续传元数据 / **未下完的目标文件**；网盘上的文件保留）。
                // 「丢弃这些任务的时候不会删除分片和对应的其他文件」。
                //  这里补两处：
                //   ① 原来只删分片目录与 .qmeta，**没删目标文件** —— 而共写模式的目标文件
                //      是预分配的满尺寸文件（可能是几百 MB 的空洞文件），留着最占地方；
                //   ② 原来 `TryDeletePath` 静默吞异常、`n++` 无条件自增，
                //      于是"删失败"和"删成功"在日志里长得一模一样 —— 用户报的正是
                //      "删没删不知道"。现在分开统计成功/失败，并把失败情况写进日志。
                int n = 0, failed = 0;
                long freed = 0;
                var roots = AllPartsRoots();
                foreach (var p in all)
                {
                    if (p == null) continue;
                    bool anyFail = false;
                    try
                    {
                        // 🚨 2026-10-28：先看有没有分片残留，再决定要不要动目标文件。
                        //   原来的写法是「删完分片 → 无条件 File.Delete(p.Dest)」，
                        //   而 p.Dest 未必是残件：目标文件可能早就下完了、只是这条记录
                        //   一直没被清（记录残留 / 上次强杀在收尾前）。
                        //   那条路径会把**用户已经下好的成品直接删掉，且不进回收站**。
                        //   现在：只有存在分片残留（.qparts 目录 / .qmeta / .geziresume 之类）
                        //   才认定 dest 是半成品、可以删；没有任何残留就只清记录，文件留着。
                        bool hasParts = false;
                        var partials = new List<string>();
                        foreach (string path in PartsPaths.EnumeratePartialPaths(p.Dest, roots))
                        {
                            if (!Directory.Exists(path) && !File.Exists(path)) continue;
                            hasParts = true;
                            partials.Add(path);
                        }

                        foreach (string path in partials)
                        {
                            freed += PathSizeSafe(path);
                            TryDeletePath(path);
                            if (Directory.Exists(path) || File.Exists(path)) anyFail = true;
                        }

                        // 目标文件本身（仅当确认为半成品时才删）
                        if (!string.IsNullOrEmpty(p.Dest) && File.Exists(p.Dest))
                        {
                            if (hasParts)
                            {
                                freed += PathSizeSafe(p.Dest);
                                try { File.Delete(p.Dest); } catch { }
                                if (File.Exists(p.Dest)) anyFail = true;
                            }
                            else
                            {
                                LogDetail("[" + (p.Name ?? "?") + "] 没有分片残留，目标文件可能是已下完的成品，保留不动："
                                    + p.Dest);
                            }
                        }
                    }
                    catch { anyFail = true; }

                    if (anyFail) failed++;
                    else n++;
                }
                HistoryStore.ClearPending();
                _pendingSig = null;
                _pendingLightSig = -1;   // 同时作废轻量指纹，否则下次 SyncPending 会误判「没变化」
                Log(failed == 0
                    ? string.Format("已丢弃 {0} 个未完成任务，本地残留已清理（释放 {1}），网盘文件保留。",
                        n, Util.FormatSize(freed))
                    : string.Format("已丢弃 {0} 个未完成任务（释放 {1}）；其中 {2} 个有文件正被占用未能删除，"
                        + "下次启动的自动清扫会兜底回收。网盘文件保留。",
                        n + failed, Util.FormatSize(freed), failed));
                return;
            }

            if (!doResume)
            {
                Log("未完成的任务已保留记录，稍后可从下载页重新下载（分片在，会自然续传）。");
                return;
            }

            var chosen = wrappers.Where(w => w.IsChecked && w.CanResume)
                                 .Select(w => w.Task).ToList();
            if (chosen.Count == 0)
            {
                Log("没有勾选任何可续传的任务。");
                return;
            }

            if (chosen.Count > ConfirmThreshold)
            {
                var ans = AppDialog.Show(this,
                    string.Format("将续传 {0} 个任务，会一次性发出相应数量的取链请求。\n\n确认继续吗？",
                        chosen.Count),
                    "确认续传", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (ans != MessageBoxResult.Yes)
                {
                    Log("已取消批量续传。");
                    return;
                }
            }

            Log(string.Format("开始续传 {0} 个任务…", chosen.Count));
            Tabs.SelectedIndex = 1;
            _ = ResumePendingBatchAsync(chosen);
        }


        /// <summary>
        /// 刷新任务页右上角的「未完成 N」入口。
        /// <para>
        /// 用户反馈：「我点了『稍后再说』之后，怎么再续传？」
        /// 原来这个弹窗**只在启动时弹一次**，关掉就没有任何入口，
        /// 除非重启软件否则再也看不到那些没下完的任务。现在常驻一个入口，
        /// 有未完成任务时才显示，点它重新打开续传界面。
        /// </para>
        /// </summary>
        private void RefreshPendingEntry()
        {
            if (PendingEntryBtn == null || PendingEntryText == null)
                return;
            // SyncPending 也可能从后台线程进来，UI 更新统一回主线程。
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshPendingEntry));
                return;
            }

            int count = 0;
            try
            {
                var all = HistoryStore.GetPending();
                if (all != null) count = all.Count;
            }
            catch { count = 0; }

            if (count <= 0)
            {
                PendingEntryBtn.Visibility = Visibility.Collapsed;
                return;
            }

            PendingEntryText.Text = "未完成 " + count;
            PendingEntryBtn.Visibility = Visibility.Visible;
        }

        /// <summary>点「未完成 N」重新打开续传界面（与启动时那个弹窗是同一个）。</summary>
        private void OnOpenPendingEntry(object sender, RoutedEventArgs e)
        {
            ShowPendingOnStartup();
        }


        /// <summary>
        /// 续传列表的行模板：**真正的复选框** + 文件名（+ 大小 / 不可续传的说明）。
        /// <para></para>
        /// <para>
        /// 原来用 <c>DisplayMemberPath = "Display"</c>，只能在文字里拼一个 "✓" 冒充勾选态，
        /// 用户明确反馈「文件前面有个对勾太误导人了，让人以为已经自动勾选了」。
        /// 换成 ItemTemplate 才能放真的 CheckBox，并把勾选态双向绑回 PendingRow.IsChecked
        /// （「继续下载」按钮的可用性就是靠它驱动的）。
        /// </para>
        /// </summary>
        private DataTemplate BuildPendingRowTemplate()
        {
            var cb = new FrameworkElementFactory(typeof(CheckBox));
            var st = TryFindResource("AppCheckBox") as Style;
            if (st != null) cb.SetValue(FrameworkElement.StyleProperty, st);
            cb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            cb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            // 不可续传的行：复选框置灰（文案里已说明原因）
            cb.SetBinding(UIElement.IsEnabledProperty,
                new Binding("CanResume") { Mode = BindingMode.OneWay });
            cb.SetBinding(ToggleButton.IsCheckedProperty,
                new Binding("IsChecked")
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                });
            cb.SetBinding(ContentControl.ContentProperty, new Binding("Display"));

            return new DataTemplate { VisualTree = cb };
        }


        /// <summary>
        /// 批量续传：为每个任务重新取链并入队下载。
        /// <para>取链按 <see cref="PendingTask.FromShare"/> 分流，复用与首次下载相同的接口。
        /// 任一任务取链失败不影响其他任务（逐条 try/catch 并记日志）。</para>
        /// </summary>
        /// <summary>
        /// 续传前确保拿到一个可用的客户端。
        /// <para>启动时的自动登录是异步的，而续传框弹得很早
        /// （Idle 优先级），用户可能先点了「继续下载」。此时全局客户端还没赋值，
        /// 旧代码会直接放弃续传 —— 用户只觉得"点了没反应"。</para>
        /// <para>顺序：① 已有客户端直接用；② 等启动时那次自动登录（最多 15 秒）；
        /// ③ 还不行就用保存的 Cookie 现建一个（取直链只需要 Cookie，
        /// 昵称/头像的校验可以后补）；④ 都没有才返回 null。</para>
        /// </summary>
        private async Task<QuarkClient> EnsureClientForResumeAsync()
        {
            // 分享用的是独立客户端（_shareClient）：当前账号不是夸克时 _client 可能是 null
            // 或者是 UC 的客户端，拿它去刷夸克分享的 stoken 会失败。
            if (_shareClient != null) return _shareClient;
            if (_client != null) return _client;

            LogDetail("续传需要登录态，正在等待自动登录完成…");
            var t = _startupLoginTask;
            if (t != null)
            {
                try { await Task.WhenAny(t, Task.Delay(15000)).ConfigureAwait(true); }
                catch { }
            }
            if (_client != null)
            {
                LogDetail("自动登录已完成，开始续传。");
                return _client;
            }

            // 兜底：保存的 Cookie 足够取直链了（账号校验只影响昵称/头像的显示）
            if (!string.IsNullOrEmpty(_settings.Cookie))
            {
                try
                {
                    LogDetail("自动登录尚未完成，已直接用保存的登录态开始续传。");
                    return new QuarkClient(_settings.Cookie);
                }
                catch (Exception ex)
                {
                    LogDetail("用保存的登录态续传失败: " + ex.Message);
                }
            }
            return null;
        }


        private async Task ResumePendingBatchAsync(List<PendingTask> tasks)
        {
            // 「弹出续传窗口时我点继续下载，但左下角的账号卡
            //  还没显示登录，这时会怎么样？」
            // 查证：续传框是在 Idle 优先级弹的，而自动登录（ValidateAsync + 走 Python 桥
            // 取昵称/头像）是**异步**的，很可能还没跑完 —— 此时客户端字段仍是 null，
            // 原来的代码会**直接 return**，只在任务页留一行日志，
            // 用户看到的就是"点了没反应"，而且完全不知道为什么。
            // → 现在：① 先等一会儿自动登录；② 还不行就用保存的 Cookie 现建一个
            //   **局部**客户端（取直链只需要 Cookie，账号昵称/头像的校验可以后补）；
            //   ③ 确实没有登录态才明确弹框告知（而不是静默放弃）。
            // 注意用的是**局部** client：自动登录完成时会换掉全局客户端，
            // 若直接用字段，换的那一刻可能在途请求会被 Dispose 掉。
            var client = await EnsureClientForResumeAsync();
            // ⚠️ 判据是「任意一家登录了」而不是「夸克客户端拿到了」：
            //    阿里/迅雷账号下 _client 恒为 null（走 _ownProvider），
            //    用 client == null 判会让这两家的续传永远进不来。
            if (client == null && !IsLoggedInAny)
            {
                Log("续传需要先登录。请登录后重新下载（分片在，会自然续传）。");
                AppDialog.Show(this,
                    "续传需要重新获取直链，得先登录。\n\n"
                    + "分片都还在，登录后再点一次「继续下载」即可，不会重下。",
                    "需要先登录",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            SetBusy(true);
            var jobs = new List<DownloadJob>();
            // 🚨 2026-10-28：取链失败/超时/未登录这几种情况原来都是裸 `continue`，
            //   只在日志里留一行就悄悄跳过。后果是用户点了「继续下载」，
            //   界面上少了几条，却完全不知道少了什么、为什么少。
            //   更要命的是旧版 SyncPending 会把"不在 _tasks 里的"记录整体清空，
            //   这几条被跳过 = 记录被删 = 未完成任务永久消失（分片还留在盘上变孤儿）。
            //   现在：跳过一律记账，最后统一汇总告知；记录本身保留（见 SyncPending 的合并逻辑）。
            var skipped = new List<string>();
            // 续传也必须走同一套“按大小分总预算”的分配，不能每个任务直接开满
            // _settings.Threads（3 个 256 路就是 768 条连接，绕过 512 总预算）。
            var alloc = AllocateThreads(tasks.Select(p => p.Size).ToList());
            int allocIdx = 0;
            try
            {
                foreach (var p in tasks)
                {
                    int myAlloc = allocIdx < alloc.Length ? alloc[allocIdx] : _settings.Threads;
                    allocIdx++;
                    try
                    {
                        // 🚨 2026-10-26：取链**必须有超时**。
                        //   原来这里传的是 default(CancellationToken)，而 QuarkSession 的 HttpClient
                        //   是 Timeout.InfiniteTimeSpan —— 网络半死时这个 await 永远不返回，
                        //   SetBusy(true) 就一直挂着：解析/下载/刷新 6 个按钮全变灰、零提示，
                        //   用户看到的就是「点了继续下载什么也没发生」。
                        //   45 秒对正常取链绰绰有余（实测 <2s），超时只当这一条失败、继续下一条。
                        using (var linkCts = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                        {
                        string url = null;
                        // 这条续传任务来自哪家网盘（记录里存的，不是当前登录账号）。
                        PanKind kind = (PanKind)p.PanKindValue;

                        if (p.PanKindValue != 0)
                        {
                            // 非夸克网盘：用对应 provider 重取链（凭证从当前设置重建）。
                            url = await RefreshOtherPanLinkForPendingAsync(p, linkCts.Token)
                                .ConfigureAwait(false);
                        }
                        else if (p.FromShare)
                        {
                            if (client == null)
                            {
                                Log("[" + p.Name + "] 这条是夸克分享，需要夸克登录态才能续传，已跳过。");
                                skipped.Add(p.Name ?? "?");
                                continue;
                            }
                            // 分享模式：先用记录里的 pwd_id/stoken/token 重取（GetShareDownloadAsync
                            // 的参数是显式传入的，不依赖客户端的"当前分享"状态）。
                            // stoken 一般几小时~几天就过期，过期后这一路必失败。
                            // 交给 GetShareLinkWithReResolveAsync：旧凭证失败就自动重解析分享
                            // （换新 stoken + 新 share_fid_token）再试 —— 用户点一次续传即自愈。
                            url = await GetShareLinkWithReResolveAsync(
                                client, p.Fid, p.Token, p.PwdId, p.Stoken, p.Passcode,
                                p.StartFid, p.ShareRelDir,
                                (freshStoken, freshToken) =>
                                {
                                    // 只在真正发生重解析时才打（旧 stoken 还有效时不会进这里）
                                    Log("[" + p.Name + "] 分享凭证已过期，已自动重新解析分享。");
                                    // 写回记录：下面构造 job.Pending 时 Stoken = p.Stoken 用的就是新值
                                    p.Stoken = freshStoken;
                                    p.Token = freshToken;
                                }, linkCts.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            if (client == null)
                            {
                                Log("[" + p.Name + "] 需要夸克登录态才能续传，已跳过。");
                                skipped.Add(p.Name ?? "?");
                                continue;
                            }
                            var links = await client.GetDownloadAsync(new List<string> { p.Fid });
                            url = links?.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl))?.DownloadUrl;
                        }

                        if (string.IsNullOrEmpty(url))
                        {
                            Log("[" + p.Name + "] 续传取链失败（可能分享已失效或需要重新解析）。");
                            skipped.Add(p.Name ?? "?");
                            continue;
                        }

                        string dest = p.Dest;
                        try { Directory.CreateDirectory(Path.GetDirectoryName(dest)); } catch { }

                        var job = new DownloadJob
                        {
                            Name = p.Name ?? Path.GetFileName(dest),
                            Url = url,
                            Dest = dest,
                            // 🚨 续传也必须按「这条任务来自哪家网盘」带全套下载头。
                            //    原来这里一个头都没设 → 下载器全部回落到夸克默认值：
                            //    阿里 OSS 400（签名绑 Referer）、迅雷 403、UC 403（Referer/UA 都要 UC 的）
                            //    + UC 还必须走 OpenSSL 桥（否则 TLS 指纹被拒）。
                            //    症状和「新建任务下不了」一模一样，用户重启后续传才发现。
                            Cookie = PendingDownloadCookie(kind, client),
                            Referer = DownloadRefererFor(kind),
                            UserAgent = DownloadUserAgentFor(kind),
                            Origin = DownloadOriginFor(kind),
                            UseOpenSsl = DownloadUseOpenSslFor(kind),
                            MaxParallelCount = MaxParallelFor((PanKind)p.PanKindValue),
                            Threads = myAlloc,
                            Md5 = p.Md5, Sha1 = p.Sha1, Sha256 = p.Sha256,
                            PartsRoot = string.IsNullOrWhiteSpace(_settings.PartsRoot) ? null : _settings.PartsRoot,
                            Pending = new PendingInfo
                            {
                                PwdId = p.PwdId, Stoken = p.Stoken, Passcode = p.Passcode,
                                Fid = p.Fid, Token = p.Token, FromShare = p.FromShare,
                                RelDir = p.RelDir ?? "", Dest = dest, Size = p.Size,
                                Md5 = p.Md5, Sha1 = p.Sha1, Sha256 = p.Sha256,
                                // 自动重解析分享的上下文（stoken 过期自愈用）。
                                // 上面取链若触发过重解析，p.Stoken/p.Token 已是新值。
                                StartFid = p.StartFid, ShareRelDir = p.ShareRelDir,
                                // 保留来源网盘：否则续传一次就退化成"夸克任务"
                                PanKind = p.PanKindValue,
                            },
                        };
                        var captured = job;
                        job.LinkRefresher = ct => RefreshLinkAsyncForPending(captured, ct);
                        // 免转存任务续传取不到新链（stoken 过期）→ 失败文案要说"需重新解析分享"
                        // 非夸克网盘有专门的 refresher（见 RefreshOtherPanLinkForPendingAsync），
                        // 只要设置里有凭证就能自愈，所以不算"无法刷新"。
                        job.ResumeCannotRefresh = p.FromShare && p.PanKindValue == 0;
                        jobs.Add(job);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Log("[" + (p.Name ?? "?") + "] 续传取链超时（45 秒无响应），已跳过这条。"
                            + "可稍后重试，分片都在，不会重下。");
                        skipped.Add(p.Name ?? "?");
                    }
                    catch (Exception ex)
                    {
                        Log("[" + (p.Name ?? "?") + "] 续传失败: " + ex.Message);
                        skipped.Add(p.Name ?? "?");
                    }
                }

                // 统一汇总：跳过几条、分别叫什么，一条日志说清，别让用户猜。
                if (skipped.Count > 0)
                {
                    Log(string.Format("有 {0} 个任务这次没能开始续传：{1}",
                        skipped.Count, string.Join("、", skipped.ToArray())));
                }

                if (jobs.Count == 0)
                {
                    Log("没有任务成功取到直链。");
                    // 有跳过项时说明记录还在，用户可以从「未完成 N」入口再试一次。
                    if (skipped.Count > 0)
                    {
                        AppDialog.Show(this,
                            string.Format("这 {0} 个任务这次都没能取到直链。\n\n"
                                + "记录和分片都保留着，登录后点任务页右上角的「未完成」入口可以再试一次，"
                                + "不会从头重下。", skipped.Count),
                            "没能开始续传",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                    }
                    return;
                }

                LogAllocation(alloc);
                // 一次性加：续传可能一次恢复几十上百条，逐条加会让分组视图反复重排
                var resumedRows = new List<TaskItem>(jobs.Count);
                foreach (var job in jobs)
                    resumedRows.Add(new TaskItem(job, job.Name));
                AddTaskRows(resumedRows);
                Log("已续传 " + jobs.Count + " 个任务。");
                RunJobs(jobs);
            }
            finally
            {
                SetBusy(false);
            }
        }


        /// <summary>给"续传任务"用的重新取链回调（基于 Job 上的 Pending 上下文）。</summary>
        private async Task<string> RefreshLinkAsyncForPending(DownloadJob job, CancellationToken ct)
        {
            var p = job.Pending;
            if (p == null || string.IsNullOrEmpty(p.Fid))
                return null;
            try
            {
                // 阿里/迅雷的「我的网盘」任务：不走夸克那套（那是分享专用），
                // 也不走 provider 的分享取链 —— 用它的自有网盘取链接口。
                if (!p.FromShare && p.PanKind != 0)
                {
                    return await RefreshOwnLinkForPendingAsync(
                        (PanKind)p.PanKind, p.Fid, p.Token, ct).ConfigureAwait(false);
                }

                if (p.FromShare)
                {
                    // 非夸克网盘：用对应 provider 重新取链（凭证在设置里，任务只记 PanKind）。
                    // 不走夸克那套 GetShareLinkWithReResolveAsync —— 那会拿夸克客户端
                    // 去请求别的网盘，必然失败。
                    if (p.PanKind != 0)
                    {
                        return await RefreshOtherPanLinkForPendingAsync(p, ct).ConfigureAwait(false);
                    }

                    // 与续传入队同理：旧 stoken 过期就自动重解析分享换新凭证。
                    // 写回 p.Stoken/p.Token —— job.Pending 是引用：本次换链立即用上新值，
                    // 之后落盘的 PendingTask（保存时从 pi 序列化）也是新值。
                    return await GetShareLinkWithReResolveAsync(
                        ShareClient, p.Fid, p.Token, p.PwdId, p.Stoken, p.Passcode,
                        p.StartFid, p.ShareRelDir,
                        (freshStoken, freshToken) =>
                        {
                            p.Stoken = freshStoken;
                            p.Token = freshToken;
                        }, ct).ConfigureAwait(false);
                }

                var ownFiles = new List<PanFileEntry>
                {
                    new PanFileEntry { Fid = p.Fid, Token = p.Token ?? "" },
                };
                var links = (await OwnLinksAsync(ownFiles).ConfigureAwait(false)).ToList();
                return links?.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl))?.DownloadUrl;
            }
            catch { return null; }
        }


        /// <summary>
        /// 续传时给非夸克网盘的任务重新取直链。
        /// <para>
        /// 凭证不落在任务里（那是敏感数据，且会随用户更新凭证而过期），
        /// 任务只记 <see cref="PendingTask.PanKindValue"/>，这里按当前设置重建客户端。
        /// </para>
        /// </summary>
        private async Task<string> RefreshOtherPanLinkForPendingAsync(PanKind kind, string shareId,
            string passcode, string startFid, string fid, string token, CancellationToken ct)
        {
            IPanProvider provider = null;
            try
            {
                provider = CreateProviderFor(kind);
                if (!provider.IsLoggedIn)
                {
                    LogDetail(PanUrlParser.DisplayNameOf(kind)
                        + " 未配置凭证，无法为续传任务重新取链。请到设置里补上。");
                    return null;
                }

                var shareRef = new PanShareRef
                {
                    Kind = kind,
                    ShareId = shareId ?? "",
                    Passcode = passcode ?? "",
                    StartFid = string.IsNullOrEmpty(startFid) ? "0" : startFid,
                };

                var one = new List<PanFileEntry>
                {
                    new PanFileEntry { Fid = fid, Token = token ?? "" },
                };

                var links = await provider.GetShareDownloadLinksAsync(shareRef, one, ct)
                    .ConfigureAwait(false);

                var hit = links == null
                    ? null
                    : links.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl));
                return hit == null ? null : hit.DownloadUrl;
            }
            catch (Exception ex)
            {
                LogDetail("续传取链失败（" + PanUrlParser.DisplayNameOf(kind) + "）: " + ex.Message);
                return null;
            }
            finally
            {
                if (provider != null)
                {
                    try { provider.Dispose(); } catch { }
                }
            }
        }


        /// <summary>
        /// 续传任务该带的下载 Cookie（按「这条任务来自哪家网盘」，不是当前登录账号）。
        /// <para>
        /// 夸克：用当前夸克客户端的 Cookie；UC：用设置里的 UC Cookie（与夸克是**不同域、不同值**，
        /// 原来这里对非夸克一律清空，UC 续传就少了登录态）；阿里/迅雷：空
        /// （直链是 OSS 预签名，带了反而可能被判非法请求）。
        /// </para>
        /// </summary>
        private string PendingDownloadCookie(PanKind kind, QuarkClient quarkClient)
        {
            if (kind == PanKind.Quark)
                return quarkClient != null ? quarkClient.CookieStr : "";
            if (kind == PanKind.Uc)
                return _settings == null ? "" : (_settings.UcCookie ?? "");
            return "";
        }


        /// <summary>
        /// 续传时给阿里/迅雷的**自有网盘**任务重新取直链。
        /// <para>
        /// 与分享那条路的区别：不需要 shareId / stoken，直接用文件自己的 fid
        /// 调 <see cref="IPanProvider.GetOwnDownloadLinksAsync"/>。凭证同样从设置里
        /// 现取现建客户端，任务里不落敏感数据。
        /// </para>
        /// </summary>
        private async Task<string> RefreshOwnLinkForPendingAsync(PanKind kind, string fid,
            string token, CancellationToken ct)
        {
            IPanProvider provider = null;
            try
            {
                provider = CreateProviderFor(kind);
                if (!provider.IsLoggedIn)
                {
                    LogDetail(PanUrlParser.DisplayNameOf(kind)
                        + " 未配置凭证，无法为续传任务重新取链。请到设置里补上。");
                    return null;
                }

                var one = new List<PanFileEntry>
                {
                    new PanFileEntry { Fid = fid, Token = token ?? "" },
                };

                var links = await provider.GetOwnDownloadLinksAsync(one, ct).ConfigureAwait(false);
                var hit = links == null
                    ? null
                    : links.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl));
                return hit == null ? null : hit.DownloadUrl;
            }
            catch (Exception ex)
            {
                LogDetail("续传取链失败（" + PanUrlParser.DisplayNameOf(kind) + "）: " + ex.Message);
                return null;
            }
            finally
            {
                if (provider != null)
                {
                    try { provider.Dispose(); } catch { }
                }
            }
        }


        /// <summary>PendingInfo 版本（下载中换链用）。</summary>
        private Task<string> RefreshOtherPanLinkForPendingAsync(PendingInfo p, CancellationToken ct)
        {
            return RefreshOtherPanLinkForPendingAsync((PanKind)p.PanKind, p.PwdId, p.Passcode,
                p.StartFid, p.Fid, p.Token, ct);
        }


        /// <summary>PendingTask 版本（重启后续传用）。</summary>
        private Task<string> RefreshOtherPanLinkForPendingAsync(PendingTask p, CancellationToken ct)
        {
            return RefreshOtherPanLinkForPendingAsync((PanKind)p.PanKindValue, p.PwdId, p.Passcode,
                p.StartFid, p.Fid, p.Token, ct);
        }
    }
}
