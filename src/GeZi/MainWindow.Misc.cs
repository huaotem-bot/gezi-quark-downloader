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
// 职责：其它零散成员

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

        /// <summary>当前账号的容量（总 / 已用）。取不到时返回 (0,0)，界面回退"未知"。</summary>
        private async Task<(long total, long used)> OwnCapacityAsync()
        {
            if (OwnIsQuarkLike)
                return await _client.GetCapacityAsync().ConfigureAwait(true);

            if (_ownProvider == null)
                return (0, 0);
            var space = await _ownProvider.GetSpaceAsync().ConfigureAwait(true);
            PersistProviderToken();      // 取容量会换 access_token，可能轮换 refresh_token
            return space == null ? (0, 0) : (space.Total, space.Used);
        }


        /// <summary>
        /// 主窗消息钩子，处理两条消息：
        ///   · <see cref="App.WmActivateMain"/>：另一个实例请我把窗口拉起来；
        ///   · <see cref="NativeMethods.WM_CLIPBOARDUPDATE"/>：剪贴板内容变了
        ///     （只在设置里开着「剪贴板自动解析」时才注册，见 <see cref="ApplyClipboardListener"/>）。
        /// 为什么让**收到消息的旧进程**自己激活，而不是让新进程去 SetForegroundWindow：
        /// Windows 的前台锁定不允许后台进程抢焦点，新进程调用往往只让任务栏闪一下。
        /// 旧进程是被"用户操作"触发的，激活才有效。
        /// </summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == App.WmActivateMain)
            {
                RestoreFromTray();
                handled = true;
            }
            else if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
            {
                // ⚠️ 不置 handled：这是系统的广播性质通知，本程序只是订阅者之一，
                //    没有理由把它吃掉（也避免将来其它组件想接同一条消息时被拦）。
                OnClipboardChanged();
            }
            return IntPtr.Zero;
        }


        protected override void OnClosed(EventArgs e)
        {
            try { _logFlushTimer?.Stop(); } catch { }
            try { _sleepGuardTimer?.Stop(); } catch { }
            // 释放保活。其实进程退出时线程级状态会自动释放，这里显式写更清楚，
            // 也避免"关窗后进程还活着"的路径（如托盘常驻）把保活留着。
            try { SleepGuard.Release(); } catch { }
            try { _client?.StopKeepAlive(); } catch { }
            // 多网盘客户端：不释放会留下未关闭的 HttpClient 与连接池
            try { _panProvider?.Dispose(); _panProvider = null; } catch { }
            // ⚠️ 托盘图标必须释放：不释放的话进程退出后图标会留在通知区，
            //    鼠标划过去才消失（最经典的托盘 bug）。
            try { _tray?.Dispose(); _tray = null; } catch { }
            try { _mini?.Shutdown(); } catch { }
            // 摘掉剪贴板监听：进程退出时系统也会回收这条注册，但显式摘掉更干净，
            // 且与 SourceInitialized 里的注册成对出现（窗口在托盘常驻期间一直持有它）。
            try
            {
                if (_clipboardHookHwnd != IntPtr.Zero)
                {
                    NativeMethods.RemoveClipboardFormatListener(_clipboardHookHwnd);
                    _clipboardHookHwnd = IntPtr.Zero;
                }
            }
            catch { }
            base.OnClosed(e);
        }


        // ---------------- 解析历史面板（2026-10-09） ----------------
        // 数据在 HistoryStore（GeZi.state.json 里的 Shares），这里只管画与开关。
        // 布局照选定稿：整窗压暗 + **居中**卡片，列表按 今天 / 昨天 / 更早 分组，
        // 一行 = 文件夹图标 + 名称 + 「时间 · 分享 ID · 提取码」，悬停整行高亮。

        /// <summary>
        /// 窗口级按键。**目前只处理 Esc**（关闭解析历史面板）。
        /// 🚨 第三方分支那一整套快捷键（1/2/3 切页、h 开历史、a/d/x/g 等）
        /// 我们**不采纳**，所以这里刻意不做任何单键分发 —— 只保留
        /// 「面板开着时 Esc 关闭」这一条**必要**行为：面板自己的文案就写着
        /// 「按 Esc、或点面板外的空白处关闭」，不实现就是骗用户。
        /// </summary>
        private void OnMainKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            if (!HistoryOpen) return;
            HideHistoryPanel();
            e.Handled = true;
        }


        /// <summary>
        /// 文件列表的左键点击策略（在 PreviewMouseLeftButtonDown / 隧道阶段统一裁决）：
        ///   · **Shift+点击 = 离散多选**（只切换点中的这一项），**不做连选**；
        ///   · **Ctrl+点击 = 只选中这一个**；
        ///   · 单击 = WPF 默认行为（选中）；**进目录请双击**（见 <see cref="OnFileListDoubleClick"/>）。
        /// WPF 的 `SelectionMode="Extended"` 里：
        ///   · Ctrl+点击  = 切换单项（离散多选）
        ///   · **Shift+点击 = 连选**：把"锚点项"到当前项**整段**都选上
        /// 用户要的是「可以使用 shift 多选，但**不是连选**」——
        /// 也就是 Shift 只作用于**点中的那一个**，不拉区间。
        /// 所以在隧道阶段把它拦下来自己处理，并 `e.Handled = true` 阻止 WPF 的连选。
        /// ⚠️ 不拦的话会怎样：选中第 1 个、Shift+点第 5 个 → 1~5 全被选上（连选）。
        /// </summary>
        private void OnListPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            var item = FindListBoxItem(e.OriginalSource as DependencyObject);
            if (item == null)
                return;   // 点在空白处：交给默认处理（清空选择）

            // ① Shift+点击 = 离散多选（只切换点中的这一项）
            if ((mods & ModifierKeys.Shift) != 0)
            {
                e.Handled = true;                    // 关键：阻止 WPF 的"连选"
                item.IsSelected = !item.IsSelected;
                try { item.Focus(); } catch { }
                return;
            }

            // ② Ctrl+点击 = **按用户要求关掉**（原来它是"切换单项"）。
            //    现在让它表现为"只选中这一个"，且**不进目录** ——
            //    这是单击变"进入"之后，选中文件夹的主要入口之一。
            if ((mods & ModifierKeys.Control) != 0)
            {
                e.Handled = true;
                var lb = sender as ListBox;
                if (lb != null) lb.SelectedItems.Clear();
                item.IsSelected = true;
                try { item.Focus(); } catch { }
                return;
            }

            // ③ 不按修饰键、点是文件 = WPF 默认行为（选中）。
            // 🚨 原版（第三方分支 v2.4）这里是「单击文件夹 = 直接进入」，
            //    我们**不要**这个行为 —— 用户明确要求保留「双击进入」，单击只做选中。
            //    所以这里直接落到 ④，不做任何拦截。
            // ④ 单击 / 其它情况：完全走 WPF 默认行为（选中）
        }


        /// <summary>从鼠标命中的可视元素往上找 ListBoxItem。</summary>
        private static ListBoxItem FindListBoxItem(DependencyObject d)
        {
            while (d != null)
            {
                if (d is ListBoxItem it) return it;
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            return null;
        }


        /// <summary>工具栏按钮的「图标 + 文字」内容（与 XAML 里手写的结构一致）。</summary>
        private UIElement BuildToolIconText(string iconKey, string text, double strokeThickness)
        {
            var geo = TryFindResource(iconKey) as System.Windows.Media.Geometry;
            var path = new ShapePath
            {
                Data = geo,
                Stretch = Stretch.Uniform,
                Width = 12,
                Height = 12,
                VerticalAlignment = VerticalAlignment.Center,
                StrokeThickness = strokeThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
            var brush = TryFindResource("TextSecondaryBrush") as Brush;
            if (brush != null) path.Stroke = brush;

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(path);
            sp.Children.Add(new TextBlock
            {
                Text = text,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            return sp;
        }


        private void OnClearDone(object sender, RoutedEventArgs e)
        {
            for (int i = _tasks.Count - 1; i >= 0; i--)
            {
                var s = _tasks[i].State;
                // 含 Cancelling：用户点了取消就说明不想要它了，
                // 不该逼他等下载器慢慢退出之后才能清掉。
                if (s == JobState.Completed || s == JobState.Failed
                    || s == JobState.Cancelled || s == JobState.Cancelling)
                    RemoveTaskRowAt(i);
            }
            SyncPending();
            UpdatePauseAllButton(force: true);
        }


        /// <summary>
        /// 从右键菜单的动作里取出**要操作的全部任务**。
        /// 原来只取 `SelectedItem`（一个）—— 于是"Shift 离散多选之后
        /// 右键暂停/删除"只会作用于其中一个（用户反馈）。现在把选中的全取出来；
        /// 没选中任何行时回退到菜单项自己的 Tag（行内按钮那条路径）。
        /// </summary>
        private List<TaskItem> TasksFromMenu(object sender)
        {
            var list = TaskList.SelectedItems.OfType<TaskItem>().ToList();
            if (list.Count > 0) return list;
            var one = (sender as System.Windows.Controls.MenuItem)?.Tag as TaskItem;
            return one != null ? new List<TaskItem> { one } : new List<TaskItem>();
        }


        /// <summary>从右键菜单的动作里取出目标任务：优先用选中的，回退按钮 Tag。</summary>
        private TaskItem TaskFromMenu(object sender)
        {
            return (TaskList.SelectedItem as TaskItem)
                   ?? ((sender as System.Windows.Controls.MenuItem)?.Tag as TaskItem);
        }


        // ⚠️ 2026-11-01（用户要求）：RemoveTaskRecord(TaskItem) 已删除。
        //    它的唯一调用者是右键菜单的「仅移除记录」，该入口已按用户要求整项移除。
        //    「删除并移除」（OnTaskDeleteLocal）走的是 CancelJob + DeleteRound 那条完整路径，
        //    不经过这里，功能不受影响。


        /// <summary>删除本地文件的一轮尝试（删分片路径 + 目标文件 + 占用兜底）。</summary>
        private void DeleteRound(string dest, out int deleted, out int delayed, List<string> failed)
        {
            deleted = 0;
            delayed = 0;

            // 分片目录 + 续传元数据的清理。
            // 这里原来写死 `dest + ".qparts"` / `dest + ".qmeta"`，
            // **完全没有考虑「默认分片位置」设置** —— 一旦用户在设置页把分片根目录
            // 指到别处（或事后改过），分片实际落在 `{root}\{slug}_{fnv8}\parts`，
            // 而删除逻辑还在删目标旁边的 `.qparts`，于是"分片根本没删掉"。
            // 现在改为复用下载器**同一份**路径推导（PartsPaths.ResolvePaths），
            // 并把「当前 + 历史用过的」全部根目录都覆盖到 —— 用户反复改过设置时，
            // 散落在多个旧根目录下的残片也能一次清干净。
            // 融合引擎的续传记录（.geziresume）不在 EnumeratePartialPaths 的覆盖范围内，
            // 那个方法只认老引擎的 .qparts / .qmeta。所以「删除并移除文件」之后
            // 目标文件没了、.geziresume 还留在旁边（用户实测：删完还剩一个 3 KB 的文件）。
            // 这里单独清一次。
            try
            {
                string resumeFile = dest + ".geziresume";
                if (File.Exists(resumeFile))
                {
                    File.Delete(resumeFile);
                    deleted++;
                }
            }
            catch
            {
                // 删不掉就交给下面的失败清单去报，不要在这里中断整轮清理
            }

            foreach (string p in PartsPaths.EnumeratePartialPaths(dest, AllPartsRoots()))
            {
                try
                {
                    if (Directory.Exists(p))
                    {
                        Directory.Delete(p, true);
                        deleted++;
                    }
                    else if (File.Exists(p))
                    {
                        File.Delete(p);
                        deleted++;
                    }
                }
                catch (Exception ex) { failed.Add(p + " — " + ex.Message); }
            }

            // 目标文件本身
            try
            {
                if (File.Exists(dest))
                {
                    File.Delete(dest);
                    deleted++;
                }
            }
            catch (IOException)
            {
                // 被占用（播放器/预览）：登记重启后删除，比直接失败体验好得多。
                if (NativeMethods.MoveFileEx(dest, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT))
                    delayed++;
                else
                    failed.Add(dest + " — 被其他程序占用，且登记重启删除失败（可能需要管理员权限）");
            }
            catch (Exception ex) { failed.Add(dest + " — " + ex.Message); }
        }


        /// <summary>
        /// 重新解析分享：申请新 stoken → 从 startFid 起沿 shareRelDir 逐级进目录 →
        /// 在最终目录里**按 fid** 找到目标文件的**新** share_fid_token。
        /// 按 fid 匹配而不是按名字：fid 在分享内不变，比文件名可靠。
        /// </summary>
        /// <returns>null = 失败；否则 Item1 = 新 stoken，Item2 = 新 share_fid_token。
        /// 用 Tuple 而不是 out 参数：async 方法不允许 ref/out。</returns>
        private async Task<Tuple<string, string>> TryReResolveShareTokenAsync(
            QuarkClient client, string pwdId, string passcode, string startFid,
            string shareRelDir, string fid, CancellationToken ct)
        {
            try
            {
                // ① 重新申请分享凭证（分享取消/改密码都会在这一步失败）
                string stoken = await client.GetStokenAsync(pwdId, passcode ?? "").ConfigureAwait(false);
                if (string.IsNullOrEmpty(stoken))
                    return null;

                // ② 从进入目录开始，沿完整相对路径逐级下钻。
                //    名字匹配做两手准备：_pathList 里存的是目录原名，而 RelDir 系列
                //    字段是 SanitizeFileName 规范化过的，原名带非法字符时两者不同。
                string pdirFid = string.IsNullOrEmpty(startFid) ? "0" : startFid;
                string rel = (shareRelDir ?? "").Replace('/', '\\').Trim('\\');
                if (rel.Length > 0)
                {
                    foreach (string seg in rel.Split('\\'))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (seg.Length == 0)
                            continue;
                        var dir = await FindShareEntryAsync(client, pwdId, stoken, pdirFid,
                            e => e.IsDir
                                 && (e.Name == seg || PathUtil.SanitizeFileName(e.Name) == seg),
                            ct).ConfigureAwait(false);
                        if (dir == null)
                            return null;   // 目录被改名/移动/删除 → 按路径找不到了
                        pdirFid = dir.Fid;
                    }
                }

                // ③ 最终目录里按 fid 定位文件，拿它的新 share_fid_token
                var file = await FindShareEntryAsync(client, pwdId, stoken, pdirFid,
                    e => !e.IsDir && e.Fid == fid, ct).ConfigureAwait(false);
                if (file == null || string.IsNullOrEmpty(file.Token))
                    return null;

                return Tuple.Create(stoken, file.Token);
            }
            catch (Exception ex)
            {
                LogDetail("自动重新解析分享失败: " + ex.Message);
                return null;
            }
        }


        /// <summary>
        /// 「重新检测」—— 探测播放器。
        /// </summary>
        /// <remarks>
        /// ⚠️ 必须异步：<see cref="PlayerLocator.Find"/> 除了读注册表、查几个固定路径，
        /// 还会**遍历整个 PATH** 逐个 <c>File.Exists</c>。PATH 里只要有一个网络路径，
        /// 这一步就会卡好几秒 —— 原来是同步调用，等于把整个窗口冻住（点哪都没反应）。
        /// 现在把探测丢到后台线程，按钮就地显示「检测中」+ 转圈。
        /// </remarks>
        private async void OnRecheckPlayer(object sender, RoutedEventArgs e)
        {
            long t0 = BusyClock();
            if (!BeginBusy(RecheckPlayerBtn, "检测中")) return;
            try
            {
                PlayerLocator.Player p;
                try
                {
                    p = await Task.Run(() => PlayerLocator.Find());
                }
                catch (Exception ex)
                {
                    PlayerStatusText.Text = "检测播放器失败: " + ex.Message;
                    return;
                }
                ApplyPlayerStatus(p);
            }
            finally
            {
                await EndBusyAsync(RecheckPlayerBtn, t0);
            }
        }


        private void OnGetVlc(object sender, RoutedEventArgs e)
        {
            const string url = "https://www.videolan.org/vlc/";
            // 这个按钮只负责打开官方页面，不会偷偷下载或安装播放器。
            // ShellLaunch 与 Python webbrowser.open 对齐，激活已有默认浏览器实例。
            // 都失败就**弹窗**给用户，并把网址原文显示出来供复制 —— Log 里
            // 可能被大量日志淹没，弹窗不会。
            if (!ShellLaunch.OpenUrl(url))
            {
                Log("打不开浏览器，请手动访问：" + url);
                AppDialog.Show(this,
                    "无法启动浏览器。\n\n请手动复制下方地址到浏览器打开：\n\n" + url,
                    "需要手动打开",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
        }


        static MainWindow()
        {
            // 防止"改了核心常量却忘了改 UI 档位"这类不一致再次发生。
            // 这种 bug 很隐蔽：界面看起来正常，实际选的值被悄悄夹掉，
            // 用户会觉得程序在骗他。宁可启动就炸，也不要静默失配。
            int last = ThreadPresets[ThreadPresets.Length - 1];
            if (last != ConcurrencyCap)
            {
                throw new InvalidOperationException(
                    "并发档位与核心层上限不一致：档位最高 " + last +
                    "，核心上限 " + ConcurrencyCap +
                    "。请同步修改 ThreadPresets 与 XAML 的 ComboBoxItem。");
            }
        }


        private static int PresetToIndex(int threads)
        {
            int best = 1, bestDiff = int.MaxValue;
            for (int i = 0; i < ThreadPresets.Length; i++)
            {
                int diff = Math.Abs(ThreadPresets[i] - threads);
                if (diff < bestDiff) { bestDiff = diff; best = i; }
            }
            return best;
        }


        /// <summary>
        /// 应用设置变更。
        /// 曾经的实现是无条件 Dispose + new。但保存设置（改并发/连接预算）时
        /// 下载往往正在跑：Dispose 会把内部 SemaphoreSlim 一起释放，
        /// 而此刻 RunOneAsync 正持有它 —— 接下来的 sem.Release() 就抛
        /// ObjectDisposedException("已释放该信号量")，任务被中断且提示莫名其妙。
        /// 更隐蔽的后果：RunJobs 读的是字段 _scheduler，旧对象被 Dispose 后
        /// 新对象上位，于是"正在跑的那批"用旧限流、"新点的"用新限流 ——
        /// 两个 scheduler 并存，ConcurrentTasks 限流直接失效。
        /// 现在的策略：
        ///   - 空闲时：正常重建（新并发数立即生效）
        ///   - 下载中：只更新 SettingsStore，并提示"并发数将在下次下载生效"。
        ///            绝不 Dispose 正在用的对象。
        /// </summary>
        private void RecreateScheduler()
        {
            // 连接上限现在由下载引擎 / QuarkSession 各自显式设定，
            // 不再暴露"全局连接预算"给用户（原来的设置项实际不起作用）。
            // NetworkConfig.Apply 仍在调度器构造里以固定值调用一次，兜底全局 DefaultConnectionLimit。
            if (_batchRunning)
            {
                // 正在下载：绝不触碰现有 scheduler，避免上面描述的两种事故。
                Log("并发数已保存，将在下一批下载生效（当前批次正在运行，不打断）。");
                return;
            }

            // 空闲：安全地重建
            try { _scheduler?.Dispose(); } catch { }
            _scheduler = new DownloadScheduler(_settings.ConcurrentTasks);
            _scheduler.Log = LogFromWorker;
            _scheduler.Diagnostics = OnDiagnostics;
            ApplyFusionSettingsToScheduler();
        }


        /// <summary>
        /// 把「落盘方式」等融合引擎相关设置推给调度器。
        /// 老引擎不看这两项（它固定「独立分片 + 边合并」），所以只在融合引擎分支里生效。
        /// </summary>
        private void ApplyFusionSettingsToScheduler()
        {
            if (_scheduler == null) return;
            // 2026-10-24 起落盘策略统一为「共写 + 16 MB 写合并」（大厂形态）：
            // 预分配 + 多连接各写最终偏移 + 批量排序刷盘 → 没有合并阶段，
            // 固态盘和机械盘走同一套，不再让用户选、也不再按磁盘类型分叉。
            // Settings.StorageMode 保留只为兼容老配置文件，不再影响行为。
            _scheduler.FusionStorageMode = GeZi.DownloadEngine.Storage.StorageMode.Shared;
            // 0 = 用 SequentialFileStorage 的默认窗口（32 MB）。
            // 顺序写已不是默认路径，这个值只在有人手动改 FusionStorageMode 时才生效。
            _scheduler.FusionSequentialWindowBytes = 0;
            // 机械盘连接上限：用户可以自己调（设置页「机械盘连接上限」）。
            // 调度器只在 DiskMedia 判定为 HDD 时才用它，固态盘不受影响。
            _scheduler.HddMaxConcurrency = _settings.HddMaxConcurrency;
        }


        /// <summary>把轮询结果翻译成给用户看的过期/失败说明（未识别的码原样展示，不吞信息）。</summary>
        private static string DescribeExpired(QrPollResult r)
        {
            if (r.Status == -1)
                return "扫码超时，二维码可能已失效。";
            if (!string.IsNullOrEmpty(r.Message))
                return "二维码已失效（" + r.Message + "）。";
            return "二维码已失效。";
        }


        /// <summary>
        /// 取**真实**的账号昵称与头像（登录时、手动刷新时共用这一份）。
        /// `ValidateAsync` 里的昵称**永远**是兜底的"已登录用户" ——
        /// account/info 挂在 `pan.quark.cn`，.NET 的 SChannel 必然被 WAF RST（见上面长注释）。
        /// 真名和头像只能借 Python 桥（OpenSSL 3.5.7 能过）。
        /// ⚠️ **踩过的坑**：手动刷新那一版只调了 `ValidateAsync`、没走 Python 桥，
        /// 于是把好名字覆盖成了"已登录用户"（用户实测：「点了右上角刷新之后我的名字没了」）。
        /// 所以刷新与登录**必须走同一个方法**。
        /// </summary>
        private async Task<(bool valid, string nick, string avatar)> ResolveAccountInfoAsync(
            QuarkClient client, string cookie,
            bool? knownValid = null, string knownNick = null, bool forceRefresh = false)
        {
            // 外层（ApplyLoginAsync）刚刚已经调过一次 ValidateAsync，这里再调一次等于
            // 白多一次网络往返（实测每次 0.5~1.5 秒，夸克 account/info 还要走 WAF 兜底）。
            bool valid;
            string nick;
            if (knownValid.HasValue)
            {
                valid = knownValid.Value;
                nick = knownNick;
            }
            else
            {
                var r = await client.ValidateAsync();
                valid = r.Item1;
                nick = r.Item2;
            }
            if (!valid) return (false, null, null);

            // 启动自动登录时，「同一个账号 + 上次已拿到真名/头像」直接吃缓存，
            // 不再启动 Python 桥进程（那是打开软件 5~6 秒的主要来源：进程启动 + TLS 握手）。
            // 手动刷新（forceRefresh=true）必须真去拿，否则用户改了昵称/头像看不到变化。
            //
            // ⚠️ 判据必须按**账号 uid** 比，不能按整串 Cookie：
            //    __puus 约 3 小时刷新一次，刷新后整串都变 → 缓存永远对不上 →
            //    每次切到夸克都要起一次 Python 桥（5~6 秒），就是「切夸克特别慢」。
            //    client.AccountSig 是夸克返回的稳定 uid，同一账号永远相同。
            string sig = client.AccountSig ?? "";
            bool sameAccount = !string.IsNullOrEmpty(sig)
                ? string.Equals(_settings.WhoAccountKey ?? "", sig, StringComparison.Ordinal)
                : (!string.IsNullOrEmpty(_settings.Cookie)
                   && string.Equals(_settings.Cookie, cookie, StringComparison.Ordinal));
            if (!forceRefresh
                && sameAccount
                && !string.IsNullOrEmpty(_settings.AvatarUrl)
                && !string.IsNullOrEmpty(_settings.Who)
                && _settings.Who != FallbackNick)
            {
                client.SetAvatarUrl(_settings.AvatarUrl);
                LogDetail("账号昵称/头像命中本地缓存，跳过账号信息请求");
                return (true, _settings.Who, _settings.AvatarUrl);
            }

            // 昵称是兜底值、或还没有头像 → 借 Python 桥补真实信息
            // ⚠️ 只有夸克需要这条：account/info 挂在 pan.quark.cn 上，.NET 连不上
            // （WAF 按 TLS 指纹 RST）。UC 的 account/info 在 drive.uc.cn，.NET 直接可达，
            // ValidateAsync 已经拿到真名了 —— 再走 Python 桥只会白起一个进程。
            if (client.Kind != PanKind.Uc
                && (nick == FallbackNick || string.IsNullOrEmpty(client.AvatarUrl)))
            {
                try
                {
                    var info = await Task.Run(() => PanAccountBridge.FetchAccountInfo(cookie));
                    if (info.HasValue && info.Value.Ok)
                    {
                        if (!string.IsNullOrEmpty(info.Value.Nickname))
                            nick = info.Value.Nickname;
                        if (!string.IsNullOrEmpty(info.Value.Avatar))
                            client.SetAvatarUrl(info.Value.Avatar);
                    }
                    else if (info.HasValue && !string.IsNullOrEmpty(info.Value.Error))
                    {
                        LogDetail("取账号昵称/头像失败：" + info.Value.Error);
                    }
                }
                catch (Exception ex)
                {
                    // 拿不到就用兜底值，绝不让它影响登录
                    LogDetail("取账号昵称/头像出错：" + ex.Message);
                }
            }
            // 记下"这份昵称/头像属于哪个账号"，供下次按 uid 命中缓存（见上面的判据）。
            // 只写内存即可：ApplyLoginAsync 紧接着会 SettingsStore.Save。
            if (!string.IsNullOrEmpty(sig))
                _settings.WhoAccountKey = sig;
            return (true, nick, client.AvatarUrl);
        }


        /// <summary>点账号卡直接去「登录」页 —— 侧栏底部那块地方小，放不了按钮。</summary>
        /// <summary>侧栏品牌区那个「?」按钮：打开「关于」窗口（软件介绍 + 赞赏码）。</summary>
        private void OnAboutClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var w = new AboutWindow { Owner = this };
                w.ShowDialog();
            }
            catch (Exception ex)
            {
                LogDetail("打开「关于」窗口失败：" + ex.Message);
            }
        }
    }
}
