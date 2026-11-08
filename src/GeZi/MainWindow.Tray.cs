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
// 职责：托盘、迷你窗、窗口边框与关闭

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

        // ---------------- 窗口关闭 / 托盘驻留 ----------------
        // 2026-10-02 曾整套删掉"关闭到托盘"：那时点 × 就隐藏，但**没有可发现的
        // 退出入口**，用户以为关了其实还在跑，再点 exe 又因为找不到可见窗口而"什么都不弹"。
        // 用户要求加回后台功能 + 小窗，并明确了行为：
        //   「第一次时会询问用户，然后可以选择最小化托盘或者是直接关闭程序，
        //     并且在这个弹窗的右下角可以勾选此后不再提示，在设置中可以修改」。
        // 所以现在的语义是：`AppSettings.OnCloseButton` 决定 ——
        //   Ask（默认，只问一次）→ 弹询问框，勾了「不再提示」就把选择写进设置；
        //   MinimizeToTray → 隐藏到托盘（下载继续）；
        //   Exit → 真退出。
        // ⚠️ 无论哪种模式，**托盘右键菜单里必须有「退出」**，这是唯一不会被误解的出口。

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            if (_exiting)
                return;   // 已经在走退出流程，放行

            var action = _settings.OnCloseButton;
            if (action == CloseAction.Ask)
            {
                bool dontAsk;
                var r = AppDialog.ShowWithDontAsk(this,
                    "最小化后仍在后台下载，彻底退出请右键托盘图标选「退出」。\n" +
                    "直接退出会中断下载，已下部分保留，下次可续传。",
                    "关闭窗口",
                    "最小化到托盘", "直接退出",
                    MessageBoxImage.Question, out dontAsk);

                if (r == MessageBoxResult.None)
                {
                    // 点了弹窗右上角 X / 按了 Esc：既没选「最小化」也没选「退出」，
                    // 视为反悔 —— 什么都不做，窗口保持打开。
                    e.Cancel = true;
                    return;
                }

                action = (r == MessageBoxResult.OK)
                    ? CloseAction.MinimizeToTray : CloseAction.Exit;

                if (dontAsk)
                {
                    // 勾了「此后不再提示」→ 记住这次的选择（设置页里还能改回来）。
                    // ⚠️ 只改这一个字段 + 落盘，**不要**走 ApplySettings()：
                    //    那会重读所有控件、重建调度器、还打一行"设置已生效"，纯属副作用。
                    _settings.OnCloseButton = action;
                    try { SettingsStore.Save(_settings); } catch { }
                    try { LoadSettingsToUi(); } catch { }
                    Log("已记住「关闭按钮」行为：" +
                        (action == CloseAction.MinimizeToTray ? "最小化到托盘" : "直接退出程序") +
                        "（可在设置页修改）");
                }
            }

            if (action == CloseAction.MinimizeToTray)
            {
                e.Cancel = true;      // 不真关，只隐藏
                HideToTray();
                return;
            }

            // 直接退出：若还有任务在跑，先问一句（避免误关）
            if (_batchRunning)
            {
                var r = AppDialog.Show(this,
                    "还有下载任务正在运行，确定要退出吗？\n（退出会中断本次下载，已下部分会保留，下次可续传。）",
                    "退出确认", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (r != MessageBoxResult.OK)
                {
                    e.Cancel = true;
                    return;
                }
            }
            _exiting = true;
            // 不放行也不行：这里只做清理，真正退出统一走 ExitApp()，
            // 否则"最后关掉的那个窗口"会决定进程去留（App 已设 OnExplicitShutdown）。
            e.Cancel = true;
            ExitApp();
        }


        /// <summary>隐藏到托盘（窗口 Hide，进程与下载继续）。</summary>
        private void HideToTray()
        {
            try
            {
                Hide();
                // 隐藏时把日志刷新降频：没人在看，200ms 一次的布局重排纯属浪费。
                // 队列仍在攒（不丢日志），恢复显示时会一次性刷出来。
                if (_logFlushTimer != null)
                    _logFlushTimer.Interval = TimeSpan.FromMilliseconds(LogFlushMsHidden);
                if (_tray != null && !_trayHintShown)
                {
                    _trayHintShown = true;
                    _tray.ShowBalloon("鸽子下载仍在后台运行",
                        "下载会继续。双击托盘图标可重新打开窗口，右键可选「退出」。");
                }
                Log("已最小化到托盘，下载继续。右键托盘图标可选「退出」。");
            }
            catch { }
        }


        /// <summary>把主窗从托盘/最小化状态拉起来。也被"再点一次 exe"的跨进程消息调用。</summary>
        private void RestoreFromTray()
        {
            try
            {
                if (_logFlushTimer != null)
                    _logFlushTimer.Interval = TimeSpan.FromMilliseconds(LogFlushMs);
                if (!IsVisible) Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                // 有些情况下 Activate() 仍抢不到前台（前台锁定），再用 Win32 兜一刀
                var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (h != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(h, NativeMethods.SW_RESTORE);
                    NativeMethods.SetForegroundWindow(h);
                }
            }
            catch { }
        }


        /// <summary>真正退出程序：清托盘 → 关小窗 → 关主窗 → Shutdown。</summary>
        private void ExitApp()
        {
            _exiting = true;
            // 🚨 ThemeManager.ThemeChanged 是**静态**事件，订阅者会被它一直抓着。
            // 不解绑的话：静态事件根 → 这个 MainWindow 的委托 → 整个可视树，
            // 窗口关了也回收不掉（关窗后仍能收到主题通知，然后往一个已关闭的窗口上 BeginInvoke）。
            try { ThemeManager.ThemeChanged -= OnThemeChangedRedrawTray; } catch { }
            try { _tray?.Dispose(); _tray = null; } catch { }
            try { CloseMiniWindow(); } catch { }
            try { Close(); } catch { }
            try { Application.Current.Shutdown(); } catch { }
        }


        /// <summary>创建托盘图标（在 Loaded 里调，此时窗口句柄已存在）。</summary>
        private void InitTray()
        {
            try
            {
                // 提示文案要把"单击/双击"讲清楚 —— 这两种行为不写出来没人猜得到。
                _tray = new TrayIcon("鸽子下载 · 单击开关小窗，双击打开主窗口，右键更多");
                // 用户指定：**单击 = 小窗，双击 = 主窗口**。
                // （单击动作在 TrayIcon 里延迟一个双击间隔执行，否则双击会先触发单击。）
                _tray.SingleClickRequested += ToggleMiniWindow;
                _tray.DoubleClickRequested += RestoreFromTray;
                _tray.ExitRequested += ExitApp;
                _tray.ToggleMiniRequested += ToggleMiniWindow;
                _tray.PauseAllRequested += () => OnPauseAllOrResume(null, null);
                _tray.SetMiniVisible(false);
                _tray.SetAllPaused(false);
                // 主题切换后托盘图标要重画（图标是一次性渲染的位图，不会自动跟资源字典变）。
                // ⚠️ ThemeChanged 在 ThemeManager 里是**同步**触发的（用户在设置页点单选钮 = UI 线程），
                //    但**跟随系统**模式下是 SystemEvents 线程触发的 → ThemeManager 已经切回 UI 线程，
                //    这里再保一道 Dispatcher，别赌调用方。
                ThemeManager.ThemeChanged -= OnThemeChangedRedrawTray;
                ThemeManager.ThemeChanged += OnThemeChangedRedrawTray;
            }
            catch (Exception ex)
            {
                LogDetail("托盘图标创建失败（不影响下载）：" + ex.Message);
            }
        }


        /// <summary>显示 / 隐藏小窗（托盘菜单用）。</summary>
        private void ToggleMiniWindow()
        {
            if (_mini != null && _mini.IsVisible) CloseMiniWindow();
            else ShowMiniWindow();
        }


        private void ShowMiniWindow()
        {
            try
            {
                if (_mini == null)
                {
                    _mini = new MiniWindow(_tasks);
                    _mini.OpenMainRequested += RestoreFromTray;
                    _mini.HideRequested += CloseMiniWindow;
                    _mini.PositionChanged += (l, t) =>
                    {
                        // 拖动过程中会疯狂触发，这里只记值；真正落盘在关闭时做一次。
                        _miniLeft = l; _miniTop = t;
                    };
                    _mini.Closed += (s, e) =>
                    {
                        _settings.MiniLeft = _miniLeft;
                        _settings.MiniTop = _miniTop;
                        _settings.MiniPosSet = true;
                        ApplySettings();
                        _mini = null;
                        _tray?.SetMiniVisible(false);
                    };
                }

                if (_settings.MiniPosSet)
                {
                    _mini.WindowStartupLocation = WindowStartupLocation.Manual;
                    _mini.Left = _settings.MiniLeft;
                    _mini.Top = _settings.MiniTop;
                }
                else
                {
                    // 首次打开：贴到工作区右下角，不挡中间
                    var wa = SystemParameters.WorkArea;
                    _mini.WindowStartupLocation = WindowStartupLocation.Manual;
                    _mini.Left = wa.Right - 360;
                    _mini.Top = wa.Bottom - 260;
                }
                _miniLeft = _mini.Left;
                _miniTop = _mini.Top;

                _mini.Show();
                _mini.Activate();
                _tray?.SetMiniVisible(true);
            }
            catch (Exception ex)
            {
                Log("小窗打开失败：" + ex.Message);
            }
        }


        private void CloseMiniWindow()
        {
            try
            {
                if (_mini != null)
                {
                    _mini.Shutdown();
                    _mini.Close();
                }
            }
            catch { }
            _mini = null;
            _tray?.SetMiniVisible(false);
        }


        // 用 WindowChrome 去掉了系统边框，所以拖拽/双击最大化要自己接。
        // 注意：窗口按钮本身标了 WindowChrome.IsHitTestVisibleInChrome=True，
        // 它们的 Click 不会被下面的 MouseLeftButtonDown 吞掉。

        private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                // 双击标题栏 = 最大化 / 还原（与系统行为一致）
                ToggleMaximize();
                return;
            }
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        }


        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            // 最大化后窗口会贴上屏幕边缘并盖住我们画的 1px 描边，
            // 用 Padding 让内容不被屏幕边缘裁掉。
            RootPadding();
        }


        /// <summary>最大化时给根 Grid 留出边距，否则自定义描边会被屏幕边缘吃掉。</summary>
        private void RootPadding()
        {
            try
            {
                if (Content is Border bd && bd.Child is Grid g)
                    g.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            }
            catch { }
        }


        private void OnMinimizeClick(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }


        private void OnMaximizeClick(object sender, RoutedEventArgs e)
        {
            ToggleMaximize();
        }


        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }


        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            try
            {
                // 同步最大化/还原图标
                if (MaxIcon != null)
                    MaxIcon.Data = WindowState == WindowState.Maximized
                        ? (Geometry)FindResource("IconRestore")
                        : (Geometry)FindResource("IconMaximize");
            }
            catch { }
            RootPadding();
        }
    }
}
