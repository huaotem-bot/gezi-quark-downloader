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
                            // 🚨🚨 2026-11-02 修「重启后续传只有 4 条线程」的**真正根因**。
                            //   原来这里写死 Size = t.Total，而 t.Total 来自**进度回调** ——
                            //   任务还没开始下（或刚入队就重启）时它是 0，于是把取链时
                            //   PendingInfo.Size 里本来就正确的真实大小**覆盖成 0** 落盘。
                            //   重启后 ThreadAllocator 对 size<=0 只给 minPerFile=4，
                            //   水位填充又明确跳过 size<=0 的项 → 永远钉死在 4 路。
                            //   正解：进度回调给过真值就用它（下载中可能比取链更准），
                            //   否则回落到 PendingInfo.Size（取链时拿到的真实大小）。
                            Size = t.Total > 0 ? t.Total : (pi != null ? pi.Size : 0),
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
            // 2026-11-01 大改：窗口从 600×480 放大到 660×560。
            // 原因：原来列表区只有 ~300px，6 条就要滚动；而底部四个文字按钮挤在一行，
            // 视觉上完全没有主次。放大后列表能舒服放 8~9 行，按钮也有了呼吸空间。
            DialogChrome.StyleWindow(dlg, 660, 560);

            var root = DialogChrome.DialogRoot();
            // 四行：说明 / 统计条 / 列表(*) / 按钮区
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 标题已画在自绘标题栏里（2026-10-26），内容区不再重复。
            // 2026-11-01 重做：原来是一整块灰底 Tip（两行粗字），占掉大量纵向空间。
            //   现在收成一行小字 —— 信息不减，但视觉重量降下来（现代大厂做法）。
            var head = DialogChrome.Hint(string.Format(
                "上次有 {0} 个任务未完成，其中 {1} 个可以继续下载。勾选后点「继续下载」会重新获取直链，接着已下载的部分继续（不会重下）。",
                all.Count, resumable.Count), 0, 14);
            head.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            // 统计条：把「共几个 / 可续传几个 / 已勾选几个」集中成一行小字。
            // 原来这些数字散落在说明文字里，用户勾选时看不出"我到底选了几个"。
            var statText = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 10),
                VerticalAlignment = VerticalAlignment.Center,
            };
            statText.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
            var statRow = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            var selAllTop = DialogChrome.SecondaryButton("全选可续传");
            selAllTop.Padding = new Thickness(10, 4, 10, 4);
            selAllTop.FontSize = 11;
            DockPanel.SetDock(selAllTop, Dock.Right);
            statRow.Children.Add(selAllTop);
            statRow.Children.Add(statText);
            Grid.SetRow(statRow, 1);
            root.Children.Add(statRow);

            // 「续传界面的文件前面有个对勾太误导人了，
            //  让人以为已经自动勾选了」。
            //  原来是在 Display 字符串前面拼了一个 "✓"（表示"可续传"），可它长得
            //  就像"已勾选"；再加上 ListBox 的选中蓝底，整行看起来就是"已经选好了"，
            //  于是用户以为可以直接点「继续下载」，结果按钮是灰的（一个都没真勾上）。
            //  → 换成**真正的复选框**：默认全部不勾，勾选态与按钮可用性直接联动。
            var wrappers = new List<PendingRow>();
            foreach (var p in all) wrappers.Add(new PendingRow(p));

            // 「全选」已在统计条右侧（selAllTop），这里不再重复造一个。
            var selAll = selAllTop;
            var resumeBtn = DialogChrome.PrimaryButton("继续下载");
            var discardBtn = DialogChrome.SecondaryButton("丢弃这些任务");
            var closeBtn = DialogChrome.SecondaryButton("稍后再说");
            // 主按钮加宽、加重 —— 用户来这个窗口的唯一目的就是「继续下载」，
            // 它必须是视觉上最突出的那个（原来四个按钮等宽等重，主次全无）。
            resumeBtn.MinWidth = 120;
            resumeBtn.FontWeight = FontWeights.SemiBold;
            discardBtn.Foreground = TryFindResource("DangerBrush") as Brush;

            // 用 ListBox + 复选（ItemsSource 为包装对象，便于双向绑定 IsChecked）
            var box = new ListBox
            {
                SelectionMode = SelectionMode.Single,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4),
            };
            var plainStyle = TryFindResource("PlainListBox") as Style;
            if (plainStyle != null) box.Style = plainStyle;
            // 行容器换成卡片样式（圆角 + 悬停 + 选中主色淡底），
            // 不再用 WPF 默认的系统蓝矩形选中框。
            var rowStyle = TryFindResource("PendingListItem") as Style;
            if (rowStyle != null) box.ItemContainerStyle = rowStyle;
            box.Padding = new Thickness(2);
            box.Background = Brushes.Transparent;
            // ⚠️ **必须**给 ItemTemplate：ListBox 的项没有模板时会调 ToString()，
            //    而 PendingRow 没重写它 → 用户看到一列
            //    「GeZi.MainWindow+PendingRow」（用户反馈：「续传的时候为什么都
            //    显示同样的东西，不应该显示需要续传的文件名吗」）。
            box.ItemTemplate = BuildPendingRowTemplate();
            foreach (var w in wrappers) box.Items.Add(w);

            // 2026-11-02 用户反馈「双击不能选中文件」：
            //   原来没有任何点击/双击绑定，勾选只能精确点中那个 18px 的小方框。
            //   而用户的心理模型是「列表 = 整行可点」，于是出现
            //   「点了行、有蓝底、以为选中了、其实没勾」—— 截图里 78.dzip 正是现场。
            //   现在整行点击即翻转勾选（IDM / Motrix / 迅雷的通行做法）。
            //   必须判 e.OriginalSource：点中复选框本身时不要重复翻转（会翻两次等于没翻）。
            box.PreviewMouseLeftButtonDown += (s2, e2) =>
            {
                try
                {
                    if (FindAncestorCheckBox(e2.OriginalSource as DependencyObject) != null)
                        return;

                    var row = ResolvePendingRow(box, e2.OriginalSource as DependencyObject);
                    if (row == null || !row.CanResume) return;

                    row.IsChecked = !row.IsChecked;
                    e2.Handled = true;
                }
                catch { }
            };

            var boxCard = DialogChrome.Card(box);
            boxCard.Padding = new Thickness(0);
            Grid.SetRow(boxCard, 2);
            root.Children.Add(boxCard);

            // 🚨 2026-11-01：「全选可续传」已经在统计条右侧（selAllTop），
            //   **绝不能**再塞进按钮行 —— 同一个 WPF 元素不能有两个父容器，
            //   否则抛「指定的元素已经是另一个元素的逻辑子元素」→ 整个弹窗建不出来，
            //   用户看到的就是「续传框根本不弹」。
            //   底部只放三个：稍后再说 / 丢弃 / 继续下载（主操作在最右）。
            var btns = DialogChrome.ButtonRow(closeBtn, discardBtn, resumeBtn);
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
                int picked = wrappers.Count(w => w.IsChecked && w.CanResume);
                resumeBtn.IsEnabled = picked > 0;
                // 按钮文案带上数量，用户一眼知道"我这一下会续几个"
                resumeBtn.Content = picked > 0 ? "继续下载 (" + picked + ")" : "继续下载";
                // 统计条：共 / 可续传 / 已勾选
                statText.Text = string.Format("共 {0} 个任务 · 可续传 {1} 个 · 已勾选 {2} 个",
                    wrappers.Count, wrappers.Count(w => w.CanResume), picked);
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
            if (PendingEntryBtn == null)
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
                // 红点角标一起收起 —— 没任务时不该还挂着一个红点。
                if (PendingBadge != null) PendingBadge.Visibility = Visibility.Collapsed;
                return;
            }

            // 2026-11-01：文案固定「未完成」，数量进右上角红点角标（更醒目、更紧凑）。
            // PendingEntryText 保留（XAML 里已改成固定文字），这里不再拼数字。
            PendingEntryBtn.Visibility = Visibility.Visible;

            if (PendingBadge != null)
            {
                PendingBadge.Visibility = Visibility.Visible;
                if (PendingBadgeText != null)
                    PendingBadgeText.Text = count > 99 ? "99+" : count.ToString();
            }
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
            // 2026-11-01 重做续传界面（用户：「非常丑，请彻底重做」）。
            // 原来整行只有一个 CheckBox，Content 绑 Display（文件名+大小拼成一根字符串），
            // 结果是：原生方框 + 一根同样大小的灰字，没有层级、没有进度、没有状态。
            // 现在拆成三段：自绘圆角复选框 | 文件名(主) + 大小(次) | 状态标记。

            var root = new FrameworkElementFactory(typeof(Grid));
            root.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1));

            // 三列：复选框(24) | 文本(*) | 状态(Auto)
            var c0 = new FrameworkElementFactory(typeof(ColumnDefinition));
            c0.SetValue(ColumnDefinition.WidthProperty, new GridLength(24));
            var c1 = new FrameworkElementFactory(typeof(ColumnDefinition));
            c1.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            var c2 = new FrameworkElementFactory(typeof(ColumnDefinition));
            c2.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            var cols = new FrameworkElementFactory(typeof(Grid));
            // Grid.ColumnDefinitions 不能直接塞 FrameworkElementFactory，用附加属性挂不上，
            // 所以这里走「先建 Grid，再在 ApplyTemplate 后补列」的替代方案：
            // 直接用一个横向 StackPanel 更简单可靠。
            root = new FrameworkElementFactory(typeof(StackPanel));
            root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            root.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1));

            // ① 自绘复选框（圆角方框 + 主色勾），替换原生方框
            var cb = new FrameworkElementFactory(typeof(CheckBox));
            var st = TryFindResource("PendingCheckBox") as Style;
            if (st != null) cb.SetValue(FrameworkElement.StyleProperty, st);
            else
            {
                var fallback = TryFindResource("AppCheckBox") as Style;
                if (fallback != null) cb.SetValue(FrameworkElement.StyleProperty, fallback);
            }
            cb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            cb.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
            cb.SetBinding(UIElement.IsEnabledProperty,
                new Binding("CanResume") { Mode = BindingMode.OneWay });
            cb.SetBinding(ToggleButton.IsCheckedProperty,
                new Binding("IsChecked")
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                });
            root.AppendChild(cb);

            // ② 文本区：文件名(主) + 大小(次) 上下两行
            var textCol = new FrameworkElementFactory(typeof(StackPanel));
            textCol.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);
            textCol.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            var nameTb = new FrameworkElementFactory(typeof(TextBlock));
            nameTb.SetValue(TextBlock.FontSizeProperty, 13.0);
            nameTb.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
            nameTb.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            nameTb.SetBinding(TextBlock.TextProperty, new Binding("FileName"));
            nameTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            textCol.AppendChild(nameTb);

            var subTb = new FrameworkElementFactory(typeof(TextBlock));
            subTb.SetValue(TextBlock.FontSizeProperty, 11.0);
            subTb.SetValue(TextBlock.MarginProperty, new Thickness(0, 3, 0, 0));
            subTb.SetBinding(TextBlock.TextProperty, new Binding("SubText"));
            subTb.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
            textCol.AppendChild(subTb);

            root.AppendChild(textCol);

            // ③ 右侧状态标记（不可续传时显示「需重新解析」）
            var badge = new FrameworkElementFactory(typeof(TextBlock));
            badge.SetValue(TextBlock.FontSizeProperty, 11.0);
            badge.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            badge.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 0, 0));
            badge.SetBinding(TextBlock.TextProperty, new Binding("BadgeText"));
            badge.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            root.AppendChild(badge);

            return new DataTemplate { VisualTree = root };
        }


        /// <summary>
        /// 批量续传：为每个任务重新取链并入队下载。
        /// <para>取链按 <see cref="PendingTask.FromShare"/> 分流，复用与首次下载相同的接口。
        /// 任一任务取链失败不影响其他任务（逐条 try/catch 并记日志）。</para>
        /// </summary>
        /// <summary>
        /// 等「启动时那次自动登录」跑完再弹续传框。
        /// <para>
        /// 🚨🚨 2026-11-01 修「重启后续传不动、账号一直显示未登录」：
        /// 续传框是模态的（<c>ShowDialog</c>），一弹出就接管消息泵；而自动登录的
        /// 续体排在普通优先级上，模态框不关就一直排队 → <c>_client</c> 始终是 null。
        /// 于是界面显示「未登录」、续传拿不到登录态、点「继续下载」毫无反应。
        /// 用户关掉软件重开时不再弹续传框，登录得以跑完，所以「重开一次反而显示已登录」。
        /// </para>
        /// <para>
        /// 现在两者改成**先后关系**：先等登录（无论成败），再弹框。
        /// 登录本身有超时兜底；万一异常卡住，这里也最多等
        /// <see cref="StartupLoginWaitMs"/> 就放行，绝不让续传框永远不弹。
        /// </para>
        /// </summary>
        private async Task WaitStartupLoginBeforePendingAsync()
        {
            var t = _startupLoginTask;
            if (t == null || t.IsCompleted)
                return;

            LogDetail("正在等登录态就绪，随后再询问未完成的任务…");
            try
            {
                // Task.WhenAny 而不是 await t：给一个硬上限，避免登录卡死时永远不弹框。
                // 用 ConfigureAwait(true)（默认）保证回到 UI 线程 —— 下面要弹模态框。
                var done = await Task.WhenAny(t, Task.Delay(StartupLoginWaitMs))
                                     .ConfigureAwait(true);
                if (ReferenceEquals(done, t))
                {
                    // 让登录里的异常不往上冒（登录自己已经记过日志了）。
                    try { await t.ConfigureAwait(true); } catch { }
                }
                else
                {
                    LogDetail("登录态校验超过 " + (StartupLoginWaitMs / 1000)
                        + " 秒仍未完成，先询问未完成的任务（稍后可在任务页重试续传）。");
                }
            }
            catch { }
        }


        /// <summary>弹续传框前最多等登录多久（毫秒）。</summary>
        private const int StartupLoginWaitMs = 20000;


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
            if (t != null && !t.IsCompleted)
            {
                // 🚨 2026-11-01：原来这里是 WhenAny(t, Delay(15000)) —— 15 秒一到
                //   就**当作登录失败**往下走。可登录慢的时候（夸克要走 WAF 兜底、
                //   阿里/迅雷要换令牌）经常超过 15 秒，于是续传用半成品状态开跑，
                //   用户看到的就是「一直在等、就是不动」。现在等到真正结束为止
                //   （登录自身有超时兜底，不会无限挂）。
                try { await t.ConfigureAwait(true); } catch { }
            }
            if (_client != null)
            {
                LogDetail("自动登录已完成，开始续传。");
                return _client;
            }

            // 兜底前先看一眼四家实例缓存：里面可能是 keepalive 过的**活**实例，
            // 比配置快照新得多（__puus 约 3 小时轮换，快照早晚过期 → 403）。
            var cachedLive = TryGetLiveQuarkClient(_activeKind);
            if (cachedLive != null)
            {
                LogDetail("自动登录尚未完成，已复用缓存里的活客户端开始续传。");
                return cachedLive;
            }

            // 兜底：保存的 Cookie 足够取直链了（账号校验只影响昵称/头像的显示）。
            // ⚠️ 只在**当前账号是夸克系**时才用夸克 Cookie 建客户端；
            //    登录着阿里/迅雷时 _settings.Cookie 可能是空的或别的账号的，
            //    硬拿它建夸克客户端只会制造一个必然失败的请求。
            if (!string.IsNullOrEmpty(_settings.Cookie)
                && (_activeKind == PanKind.Quark || _activeKind == PanKind.Uc))
            {
                try
                {
                    LogDetail("自动登录尚未完成，已直接用保存的登录态开始续传。");
                    return new QuarkClient(_settings.Cookie, _activeKind);
                }
                catch (Exception ex)
                {
                    LogDetail("用保存的登录态续传失败: " + ex.Message);
                }
            }
            return null;
        }


        /// <summary>
        /// 用**设置里保存的**夸克/UC 登录态现建一个客户端（续传兜底用）。
        /// <para>
        /// 为什么需要它：续传的那条任务属于「哪个网盘」由记录里的 <c>PanKindValue</c>
        /// 决定，而当前登录的账号可能是另一家。原来的代码只看全局 <c>_client</c>，
        /// 一旦当前登录的是阿里/迅雷，夸克分享的续传就会被判成"没登录"直接跳过 ——
        /// 用户看到的正是「明明账号都在，就是不动」。
        /// </para>
        /// <para>返回 null 表示设置里也没有可用的登录态（那就真的需要用户去登录）。</para>
        /// </summary>
        /// <summary>
        /// 按**指定网盘**建一个夸克系客户端（续传用）。
        /// <para>
        /// 与 <see cref="TryBuildQuarkClientFromSettings"/> 的区别：那个优先当前账号，
        /// 这个**只认传入的网盘** —— 续传任务属于哪家就必须用哪家的 Cookie，
        /// 拿错客户端请求会 401/403（症状和"取链失败"一模一样，极难分辨）。
        /// </para>
        /// </summary>
        private QuarkClient TryBuildQuarkClientFor(PanKind kind)
        {
            // 🚨🚨 2026-11-02 修「UC 续传必 403」的核心：
            //   优先返回**活客户端**（全局 _client / 四家实例缓存），
            //   它们的 Cookie 是 keepalive 自动轮换过的；
            //   只有当哪都找不到时才退而从配置快照新建一个。
            //
            //   ⚠️ 调用方拿到的可能是**借来的**活实例，绝不能 Dispose
            //      （见 IsBorrowedQuarkClient）。
            var live = TryGetLiveQuarkClient(kind);
            if (live != null) return live;

            try
            {
                if (kind == PanKind.Uc && !string.IsNullOrEmpty(_settings.UcCookie))
                    return new QuarkClient(_settings.UcCookie, PanKind.Uc);
                if (kind == PanKind.Quark && !string.IsNullOrEmpty(_settings.Cookie))
                    return new QuarkClient(_settings.Cookie, PanKind.Quark);
            }
            catch (Exception ex)
            {
                LogDetail("按网盘建客户端失败(" + kind + "): " + ex.Message);
            }
            return null;
        }


        /// <summary>
        /// 取「这一家网盘的**活**夸克系客户端」—— 全局的或四家实例缓存里的。
        /// <para>
        /// 🚨 为什么必须要它：手动下载能成、续传却 403，根因就是**用了哪个客户端**。
        /// 手动下载走全局 <c>_client</c>（跑过 KeepAlive、<c>__puus</c> 被自动轮换，令牌始终新鲜）；
        /// 续传原来从 <c>_settings.UcCookie</c> 现建 —— 那是**配置文件里的快照**，
        /// 而 UC/夸克的 <c>__puus</c> 约 3 小时轮换一次，快照早晚过期 → 服务端签的直链无效 → CDN 403。
        /// </para>
        /// <para>
        /// ⚠️ 返回的是**借来的**实例，调用方**绝不能 Dispose**（见 <see cref="IsBorrowedQuarkClient"/>）。
        /// </para>
        /// </summary>
        private QuarkClient TryGetLiveQuarkClient(PanKind kind)
        {
            if (kind != PanKind.Quark && kind != PanKind.Uc) return null;
            try
            {
                if (_client != null && _client.Kind == kind) return _client;
                if (_shareClient != null && _shareClient.Kind == kind) return _shareClient;
                QuarkClient cached;
                if (_quarkCache.TryGetValue(kind, out cached) && cached != null)
                    return cached;
            }
            catch (Exception ex)
            {
                LogDetail("取活客户端失败(" + kind + "): " + ex.Message);
            }
            return null;
        }


        /// <summary>
        /// 这个夸克系客户端是不是「借来的」（全局/缓存里的活实例）。
        /// <para>借来的**不许 Dispose** —— 那会把整个会话弄死，用户得重新登录。</para>
        /// </summary>
        private bool IsBorrowedQuarkClient(QuarkClient c)
        {
            if (c == null) return false;
            if (ReferenceEquals(c, _client)) return true;
            if (ReferenceEquals(c, _shareClient)) return true;
            foreach (var kv in _quarkCache)
                if (ReferenceEquals(kv.Value, c)) return true;
            return false;
        }


        private QuarkClient TryBuildQuarkClientFromSettings()
        {
            try
            {
                // 优先当前账号那家（夸克/UC 各存各的 Cookie，不能混用）。
                if (_activeKind == PanKind.Uc && !string.IsNullOrEmpty(_settings.UcCookie))
                    return new QuarkClient(_settings.UcCookie, PanKind.Uc);
                if (!string.IsNullOrEmpty(_settings.Cookie))
                    return new QuarkClient(_settings.Cookie, PanKind.Quark);
                if (!string.IsNullOrEmpty(_settings.UcCookie))
                    return new QuarkClient(_settings.UcCookie, PanKind.Uc);
            }
            catch (Exception ex)
            {
                LogDetail("用保存的登录态建客户端失败: " + ex.Message);
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
            // 🚨🚨 2026-11-02 修「阿里云盘续传 2 个任务永远失败」：
            //   这些记录创建于 2026-11-02 之前的版本，当时解析阿里/迅雷分享
            //   **没有把 share_id 写进待续传记录**（只存在内存里），
            //   于是重启后必然取链失败，日志是一句没头没尾的「续传失败」。
            //   分享 ID 与任务一一绑定，**不可能凭空猜出来**（猜错会去请求别人的分享，
            //   反而把「缺 ID」变成「ID 错误」，更难排查）。
            //   ⇒ 单独记账，最后用一句**用户能看懂的话**告诉他下一步做什么。
            var needReparse = new List<string>();
            // 续传也必须走同一套“按大小分总预算”的分配，不能每个任务直接开满
            // _settings.Threads（3 个 256 路就是 768 条连接，绕过 512 总预算）。
            // 这只是**预估**：按"全部待续传任务"分配，但取链阶段会有一部分失败被跳过
            // （阿里/迅雷缺分享 ID、分享失效等），那些失败的条目不该继续占预算。
            // 取链结束后会用**实际成功的那批**重新分配一次（见下方 jobs.Count 分支）。
            var alloc = AllocateThreads(tasks.Select(p => p.Size).ToList());
            int allocIdx = 0;

            // 2026-11-02：取链时会拿到真实大小（DownloadLink.Size），记在这里，
            // 取链结束后用它**重新分配线程** —— 否则 Size=0 的记录永远只有 4 路。
            var knownSizes = new Dictionary<PendingTask, long>();
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

                        if (p.PanKindValue != 0 && p.FromShare)
                        {
                            // 非夸克网盘的**分享**任务：用 provider 的分享取链接口。
                            //
                            // 🚨🚨 2026-11-02 修「阿里/迅雷续传永远失败：分享 ID 为空」：
                            //   2026-11-02 之前创建的记录里 PwdId 根本没被写过
                            //   （ParseOtherPanAsync 当时只设了 _panShareRef），
                            //   于是这里传空串进 provider，阿里直接抛
                            //   「阿里云盘分享 ID 为空」→ 这条永远续传不了。
                            //   现在加**兜底恢复**：PwdId 为空时，从分享历史里按文件名反查，
                            //   还找不到就用最近一次解析过的分享 ID（同一次下载的几条任务
                            //   通常来自同一个分享）。
                            if (string.IsNullOrEmpty(p.PwdId))
                            {
                                string recovered = TryRecoverShareIdForPending(p);
                                if (!string.IsNullOrEmpty(recovered))
                                {
                                    p.PwdId = recovered;
                                    LogDetail("[" + (p.Name ?? "?") + "] 记录里缺分享 ID，已从分享历史恢复。");
                                }
                            }
                            if (string.IsNullOrEmpty(p.PwdId))
                            {
                                // 缺分享 ID → 这条路**注定取不到链**，直接记「需要重新解析」，
                                // 不去发那个必然失败的请求（省一次网络往返，也让日志更准）。
                                LogDetail("[" + (p.Name ?? "?") + "] 这条记录是旧版本建的，缺分享信息，需要重新解析分享才能续传。");
                                needReparse.Add(p.Name ?? "?");
                                continue;
                            }
                            url = await RefreshOtherPanLinkForPendingAsync(p, linkCts.Token)
                                .ConfigureAwait(false);
                        }
                        else if (p.PanKindValue != 0)
                        {
                            // 🚨🚨 2026-11-01 修复「续传总提示无法获取链接」（用户实测日志）：
                            //   阿里 / 迅雷 / UC 的**自有网盘**任务原来被错误地送进了
                            //   RefreshOtherPanLinkForPendingAsync —— 那是**分享专用**的实现，
                            //   内部走 provider.GetShareDownloadLinksAsync(shareRef, ...)，
                            //   需要一个 share_id。而自有网盘任务根本没有 share_id
                            //   （记录里 PwdId/Stoken 全是 null）→ 必然失败：
                            //     · UC  ：HTTP 404 code 41006「分享不存在」
                            //     · 迅雷：HTTP 400 error_code 3「share_id can not be empty」
                            //   用户看到的就是「总是提示无法获取链接」，而且**主动取链却正常**
                            //   —— 因为主动下载走的是 OwnLinksAsync（自有网盘接口），
                            //   只有续传这条错路走到了分享接口上。
                            //   正解：自有网盘用 RefreshOwnLinkForPendingAsync（GetOwnDownloadLinksAsync）。
                            url = await RefreshOwnLinkForPendingAsync(
                                (PanKind)p.PanKindValue, p.Fid, p.Token, linkCts.Token)
                                .ConfigureAwait(false);
                        }
                        else if (p.FromShare)
                        {
                            // 分享续传必须要夸克登录态（换 stoken / 取直链都靠它）。
                            // 全局客户端可能是 null（登录失败、或当前登录的是阿里/迅雷），
                            // 这时用**保存的夸克 Cookie** 现建一个局部客户端再试 ——
                            // 拿不到才放弃，且放弃时明确告知原因（不再静默跳过）。
                            if (client == null)
                                client = TryBuildQuarkClientFromSettings();
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
                            // 夸克系（夸克 / UC）的**自有网盘**任务。
                            // 2026-11-01：优先用「网盘类型与这条任务一致」的客户端 ——
                            //   client 可能是当前登录的另一家（比如登录 UC 去续夸克任务），
                            //   拿错客户端请求会 401/403，症状同样是"取链失败"。
                            if (client == null || client.Kind != kind)
                            {
                                var byKind = TryBuildQuarkClientFor(kind);
                                if (byKind != null)
                                {
                                    // ⚠️ 只在「client 是我们自己建的临时实例」时才 Dispose，
                                    //    **绝不能** Dispose 全局 _client / _shareClient / 四家缓存 ——
                                    //    那会让整个会话失效，用户得重新登录。
                                    //    TryBuildQuarkClientFor 现在会**优先返回借来的活实例**，
                                    //    所以判据必须是 IsBorrowedQuarkClient 而不是逐个 ReferenceEquals
                                    //    （缓存里的实例原来漏判了）。
                                    var oldClient = client;
                                    client = byKind;
                                    if (oldClient != null && !IsBorrowedQuarkClient(oldClient))
                                    {
                                        try { oldClient.Dispose(); } catch { }
                                    }
                                }
                            }
                            if (client == null)
                            {
                                Log("[" + p.Name + "] 需要 " + PanUrlParser.DisplayNameOf(kind)
                                    + " 登录态才能续传，已跳过。");
                                skipped.Add(p.Name ?? "?");
                                continue;
                            }
                            var links = await client.GetDownloadAsync(new List<string> { p.Fid });
                            var hit = links?.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl));
                            url = hit?.DownloadUrl;

                            // 🚨🚨 2026-11-02 修「Size=0 的任务永远只有 4 条线程」：
                            //   根因：PendingTask.Size 是**下载时**从进度回调拿的，
                            //   而「刚点下载没写盘就重启」的任务 Size 是 0；
                            //   ThreadAllocator 对 size<=0 只能给 minPerFile=4，
                            //   水位填充又明确跳过 size<=0 的项 → 永远钉死在 4 路。
                            //   但取链结果 DownloadLink.Size 里**本来就带着真实大小**，
                            //   以前只取了 Url 就把它丢了。现在顺手补回来：
                            //   ① 更新 p.Size（落盘后下次启动就准了）；
                            //   ② 让下面按真实大小分配线程，而不是 4。
                            if (hit != null && hit.Size > 0)
                            {
                                if (p.Size <= 0)
                                {
                                    p.Size = hit.Size;
                                    LogDetail("[" + (p.Name ?? "?") + "] 取链时拿到真实大小 "
                                        + Util.FormatSize(hit.Size) + "，已补进记录。");
                                }
                                // 记到局部字典，供后面「按真实大小重新分配」使用
                                knownSizes[p] = hit.Size;
                            }
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
                // 🚨 2026-11-02：阿里/迅雷「缺分享信息」这一类**不是网络问题**，
                //   是记录本身缺数据（旧版本建的），用户能自己解决 ——
                //   必须在界面上说清楚「怎么办」，而不是只留一句「续传失败」。
                if (needReparse.Count > 0)
                {
                    Log(string.Format("有 {0} 个阿里/迅雷任务缺分享信息，需要重新解析：{1}",
                        needReparse.Count, string.Join("、", needReparse.ToArray())));
                    int rn = needReparse.Count;
                    // 用**非模态**通知条而不是模态弹窗：
                    // 用户可能一次恢复十几个任务，为一个「旧记录缺数据」的问题
                    // 弹窗打断整批续传不划算；通知条照样说清了原因和下一步。
                    RunOnUi(() => NotifyToast.Show("需要重新解析分享",
                        string.Format("{0} 个旧任务缺分享信息，回「下载」页重新解析该链接即可接着下（不会重下）", rn),
                        null));
                }

                if (jobs.Count == 0)
                {
                    Log("没有任务成功取到直链。");
                    // 有跳过项时说明记录还在，用户可以从「未完成 N」入口再试一次。
                    if (skipped.Count > 0)
                    {
                        // 模态框必须在主线程弹（上面有 ConfigureAwait(false)）。
                        int n = skipped.Count;
                        RunOnUi(() => AppDialog.Show(this,
                            string.Format("这 {0} 个任务这次都没能取到直链。\n\n"
                                + "记录和分片都保留着，登录后点任务页的「未完成」入口可以再试一次，"
                                + "不会从头重下。", n),
                            "没能开始续传",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning));
                    }
                    return;
                }

                // 2026-11-02 修「夸克续传只有 4 条线程」：
                //   根因是 alloc 在**取链之前**就按全部任务算好了。用户这次续传 4 个任务，
                //   其中 3 个阿里分享因缺分享 ID 失败被跳过，只剩 1 个夸克任务；
                //   但它的 Threads 仍是按"4 个任务里最小的那个"分到的 4 路
                //   （日志：已按文件大小分配连接数：256、256、4、4）。
                //   一个 45MB 文件只开 4 条连接，速度被单连接带宽卡死。
                //   正解：取链结束后，用**实际成功的那批**重新分配一次。
                // 🚨 2026-11-02：只要**任何一个**任务的大小是取链时才补到的，就要重分配。
                //   原来只在「成功数 != 总数」时重分配（为了修「3 个失败导致剩下的只拿 4 路」），
                //   但 Size=0 的任务即使成功取链，也会因为「大小未知」被钉在 4 路 ——
                //   必须用 knownSizes 里的真实大小重算一次。
                if (jobs.Count > 0 && (jobs.Count != tasks.Count || knownSizes.Count > 0))
                {
                    // 用「取链拿到的真实大小」优先，没有就退回记录里的大小。
                    var realSizes = new List<long>(jobs.Count);
                    foreach (var j in jobs)
                    {
                        long sz = 0;
                        if (j.Pending != null)
                        {
                            sz = j.Pending.Size;
                            // 若这条任务在 knownSizes 里有更准的值，用它
                            foreach (var kv in knownSizes)
                            {
                                if (kv.Key.Fid == j.Pending.Fid && kv.Value > 0)
                                {
                                    sz = kv.Value;
                                    break;
                                }
                            }
                        }
                        realSizes.Add(sz);
                    }
                    var realAlloc = AllocateThreads(realSizes);
                    for (int i = 0; i < jobs.Count; i++)
                    {
                        if (i < realAlloc.Length && realAlloc[i] > 0)
                            jobs[i].Threads = realAlloc[i];
                    }
                    LogDetail(string.Format(
                        "按实际成功续传的 {0} 个任务重新分配连接数：{1}（原预估值是按 {2} 个算的，含已失败的）",
                        jobs.Count,
                        string.Join(" / ", realAlloc.Select(x => x.ToString()).ToArray()),
                        tasks.Count));
                }

                LogAllocation(alloc);
                // 一次性加：续传可能一次恢复几十上百条，逐条加会让分组视图反复重排
                var resumedRows = new List<TaskItem>(jobs.Count);
                foreach (var job in jobs)
                    resumedRows.Add(new TaskItem(job, job.Name));

                // 🚨 2026-11-01：这三步**全是 UI 操作**（改 _tasks 集合、刷日志框、
                //   启动任务并把状态推给界面）。上面有大量 ConfigureAwait(false)，
                //   到这里很可能已经在线程池线程上 —— 直接调会抛
                //   InvalidOperationException（"另一个线程拥有该对象"），
                //   而且是**静默**抛在 async void / 未观察 Task 里，用户只看到
                //   「任务加进来了但一直排队、没速度」。必须整体切回主线程。
                RunOnUi(() =>
                {
                    AddTaskRows(resumedRows);
                    Log("已续传 " + jobs.Count + " 个任务。");
                    RunJobs(jobs);
                });
            }
            finally
            {
                // 🚨🚨 2026-11-01 修复「续传后必须刷新界面才看到任务 / 等很久没速度 / 部分任务续传失败」。
                //   根因（startup-error.log 实锤，21:10 / 21:25 / 22:00 三次）：
                //     InvalidOperationException: 调用线程无法访问此对象，因为另一个线程拥有该对象。
                //       at Dispatcher.VerifyAccess()
                //       at MainWindow.SetBusy(Boolean busy)
                //       at MainWindow.<ResumePendingBatchAsync>...  ← 就是这里！
                //   上面大量 ConfigureAwait(false) 让这个 finally 跑在**线程池线程**上，
                //   而 SetBusy 直接改按钮 IsEnabled（UI 对象）→ 抛异常。
                //   异常从 `_ = ResumePendingBatchAsync(chosen)` 逸出成为 UnobservedTaskException，
                //   用户看到的就是：
                //     · 任务列表里已经有行，但状态停在「排队中」、速度 0（RunJobs 没跑到）；
                //     · 必须切页/刷新才看到（列表是后来才画的）；
                //     · 「部分任务续传失败」（有的批次在取链中途就断了）。
                //   ⇒ 所有 UI 操作必须显式回主线程。
                RunOnUi(() => SetBusy(false));
            }
        }


        /// <summary>
        /// 把一段 UI 操作切回主线程执行（已在主线程则直接执行）。
        /// <para>
        /// 续传流程里有大量 <c>ConfigureAwait(false)</c>（为了不阻塞 UI），
        /// 于是 await 之后可能落在**线程池线程**上 —— 那里碰任何 WPF 控件都会抛
        /// <c>InvalidOperationException</c>。凡是操作控件的语句都要包一层。
        /// </para>
        /// </summary>
        /// <summary>
        /// 从点击源往上找最近的 CheckBox（判断这次点击是不是点在复选框上）。
        /// <para>整行点击翻转勾选时必须先排除，否则点复选框会翻两次（等于没翻）。</para>
        /// </summary>
        private static System.Windows.Controls.CheckBox FindAncestorCheckBox(DependencyObject node)
        {
            while (node != null)
            {
                var cb = node as System.Windows.Controls.CheckBox;
                if (cb != null) return cb;
                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
            }
            return null;
        }


        /// <summary>从点击源解析出它所在的那一行（PendingRow）。</summary>
        private static PendingRow ResolvePendingRow(ListBox box, DependencyObject src)
        {
            if (box == null || src == null) return null;

            // ① 先看点击源自己的 DataContext（最常见的路径：点到 TextBlock / Border）
            var fe = src as FrameworkElement;
            if (fe != null && fe.DataContext is PendingRow)
                return fe.DataContext as PendingRow;

            // ② 再往上找 ListBoxItem 容器
            try
            {
                var item = ItemsControl.ContainerFromElement(box, src) as ListBoxItem;
                if (item != null) return item.DataContext as PendingRow;
            }
            catch { }

            // ③ 最后沿可视树向上逐层试 DataContext
            var node = src;
            while (node != null)
            {
                var el = node as FrameworkElement;
                if (el != null && el.DataContext is PendingRow)
                    return el.DataContext as PendingRow;
                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
            }
            return null;
        }


        private void RunOnUi(Action action)
        {
            if (action == null) return;
            try
            {
                if (Dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    Dispatcher.Invoke(action);
                }
            }
            catch
            {
                // UI 更新失败不能反过来打断续传主流程
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
                //
                // 🚨🚨 2026-11-02 修「UC 续传 HTTP 403」：
                //   判据原来是 `p.PanKind != 0`，把 **UC（PanKind=1）也卷了进来**，
                //   于是 UC 任务被送去 RefreshOwnLinkForPendingAsync →
                //   PanProviderRegistry.Create(Uc) → UcDriveClient。
                //   但 UC 在数据层**与夸克同源**：正常下载走的是
                //   OwnLinksAsync → _client.GetDownloadAsync（QuarkClient 链路），
                //   _panProvider 对 UC **恒为 null**。
                //   拿 UcDriveClient 取到的直链与 QuarkClient 那套不是一回事，
                //   请求 CDN 必然 403（日志：直链HTTP 403 签名过期/防盗链，重试 2 次后失败）。
                //   ⇒ 只有**阿里/迅雷**才走 provider；UC 必须和夸克一样走 QuarkClient。
                //   ⚠️ 这条在 AGENTS.md 里写过（「UC 不是其他网盘，判据必须用 UsesProvider」），
                //      我当时没查记忆，又踩了一遍。
                if (!p.FromShare && (p.PanKind == 2 || p.PanKind == 3))
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

                // 🚨 2026-11-02：夸克/UC 的自有网盘任务，必须用**网盘类型匹配**的客户端取链。
                //   OwnLinksAsync 内部用的是全局 _client —— 续传时它可能是「当前登录的那家」，
                //   与这条任务的 PanKind 不一定一致（例如登录夸克、续传 UC 任务）。
                //   拿错 Cookie 请求 CDN 会 401/403，症状和「直链过期」一模一样，极难分辨。
                // 🚨🚨 2026-11-02 修「手动下载能成、续传 403」的真正根因：
                //
                //   手动下载走 OwnLinksAsync → **_client.GetDownloadAsync** —— 那是**全局活客户端**：
                //   它启动过 KeepAlive、`__puus` 被 RefreshPuusAsync 自动轮换过，Cookie 始终是最新的。
                //
                //   而续传原来走 TryBuildQuarkClientFor → `new QuarkClient(_settings.UcCookie, ...)`，
                //   用的是**配置文件里的 Cookie 快照**。UC/夸克的 `__puus` 约 3 小时轮换一次，
                //   快照早晚会过期 → 服务端签发的直链无效 → CDN 直接 403。
                //   日志里「直链HTTP 403 签名过期/防盗链」重试 2 次后失败，就是这个。
                //
                //   ⇒ 续传必须复用**同一个活客户端**，和手动下载完全一致。
                //   只有当全局客户端的网盘类型对不上时（例如登录夸克去续 UC 任务），
                //   才退而新建一个——但那种情况下我们至少要先确认没有更好的选择。
                if (p.PanKind == 0 || p.PanKind == 1)
                {
                    var kind2 = (PanKind)p.PanKind;

                    // ① 优先复用全局客户端（令牌是活的，和手动下载同一条路）
                    var live = _client;
                    if (live != null && live.Kind == kind2)
                    {
                        try
                        {
                            var one = await live.GetDownloadAsync(new List<string> { p.Fid })
                                .ConfigureAwait(false);
                            var u = one?.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl))?.DownloadUrl;
                            if (!string.IsNullOrEmpty(u))
                                return u;
                        }
                        catch (Exception ex)
                        {
                            LogDetail("[续传] 用当前客户端取链失败(" + PanUrlParser.DisplayNameOf(kind2) + "): " + ex.Message);
                        }
                    }

                    // ② 全局客户端网盘对不上（或取链失败）→ 退而新建一个**临时**客户端。
                    //    ⚠️ 这是下策：临时客户端没有 keepalive，Cookie 是快照，可能已过期。
                    //    只在确实没有别的选择时用。
                    var byKind = TryBuildQuarkClientFor(kind2);
                    if (byKind != null)
                    {
                        try
                        {
                            var one = await byKind.GetDownloadAsync(new List<string> { p.Fid })
                                .ConfigureAwait(false);
                            return one?.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl))?.DownloadUrl;
                        }
                        finally
                        {
                            try { byKind.Dispose(); } catch { }
                        }
                    }
                }

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
        /// <summary>
        /// 为一条「缺分享 ID」的续传记录找回它所属的分享 ID。
        /// <para>
        /// 🚨 为什么需要它：2026-11-02 之前的版本在解析阿里/迅雷分享时
        /// **没有把 share_id 写进待续传记录**（只存在内存的 _panShareRef 里），
        /// 于是那些记录重启后必然报「分享 ID 为空」。
        /// 分享 ID 其实在「解析历史」里有完整保存，可以从那里反查。
        /// </para>
        /// <para>
        /// 恢复顺序：
        /// ① 按文件名在分享历史里找**同名的文件**（最准）；
        /// ② 找不到就用最近一次解析过的分享 ID ——
        ///    同一次下载的多条任务通常来自同一个分享，这个兜底命中率很高。
        /// ③ 都没有才返回空（那就真的只能让用户重新解析）。
        /// </para>
        /// </summary>
        private string TryRecoverShareIdForPending(PendingTask p)
        {
            // 🚨🚨 2026-11-02 撤销上一轮的错误实现。
            //   上一轮我在这里「取分享历史里最近的一条」当作恢复值 ——
            //   那是**完全错误的猜测**：用户历史里最新的是「walli 4k」这类无关分享，
            //   把它的 share_id 塞给阿里任务后，服务端直接回
            //   `NotFound.ShareLink: The resource share_link cannot be found`，
            //   反而把「缺 ID」变成了「ID 错误」，更难排查。
            //
            //   正确原则：**分享 ID 与任务是一一绑定的，不可能凭空猜出来**。
            //   猜错的代价（去请求一个别人的分享）比直接失败更糟。
            //   ⇒ 宁可如实报告「这条记录缺分享信息，需要重新解析」，也不要瞎猜。
            return null;
        }


        private Task<string> RefreshOtherPanLinkForPendingAsync(PendingTask p, CancellationToken ct)
        {
            return RefreshOtherPanLinkForPendingAsync((PanKind)p.PanKindValue, p.PwdId, p.Passcode,
                p.StartFid, p.Fid, p.Token, ct);
        }
    }
}
