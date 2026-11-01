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
// 职责：主题与配色切换

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
        /// 主题切换 = 「双快照 + 圆形揭示」。
        /// 旧实现（已废弃）：动画期间只放大一个**纯色圆**，底层 UI 一直是旧主题 ——
        /// 用户看到的只是"一块颜色在放大"，而不是新界面被揭开。
        /// 新实现（Material You 的做法）：
        ///   ① 切换前先抓一张**旧 UI 快照**；
        ///   ② 立刻换肤 + UpdateLayout，再抓一张**新 UI 快照**；
        ///   ③ 覆盖层叠两张图：底 = 旧快照，上 = 新快照（EllipseGeometry 裁剪）；
        ///   ④ 动画只改裁剪圆半径，从点击点向外扩展 ——
        ///      每一帧露出来的都是**已经完成切换的新 UI**。
        /// ⚠️ ①②③ 全在同一个 UI 线程消息里同步执行（中间没有 await），
        ///    WPF 不会渲染中间态，用户看不到"直接跳变"。
        /// ⚠️ 换肤后的收尾（存配置 / 日志 / 提示文案）放在动画 Completed 里，
        ///    避免磁盘 I/O 卡住动画启动。
        /// </summary>
        private void PlayThemeReveal(FrameworkElement origin, AppTheme want, Action afterApply)
        {
            var rootEl = RootBorder as FrameworkElement;
            if (rootEl == null || origin == null) { ThemeManager.SetTheme(want); afterApply(); return; }

            double w = rootEl.ActualWidth, h = rootEl.ActualHeight;
            if (w < 1 || h < 1) { ThemeManager.SetTheme(want); afterApply(); return; }

            Point center;
            try
            {
                center = origin.TranslatePoint(
                    new Point(origin.ActualWidth / 2, origin.ActualHeight / 2), rootEl);
            }
            catch
            {
                center = new Point(w / 2, h / 2);
            }

            double maxR = Math.Sqrt(
                Math.Pow(Math.Max(center.X, w - center.X), 2) +
                Math.Pow(Math.Max(center.Y, h - center.Y), 2));

            // ① 旧快照垫底（圆外露出来的就是它）。
            //    ⚠️ 2026-10-18 用户实测不接受模糊 → 必须全分辨率（0.6 倍铺满全窗必糊）。
            ThemeOldSnapshot.Source = CaptureRoot(rootEl);
            ThemeOldSnapshot.Visibility = Visibility.Visible;

            // ② 换肤 + 提示文案 + 强制布局
            ThemeManager.SetTheme(want);
            UpdateThemeHint();
            rootEl.UpdateLayout();

            // ③ 真实界面加圆形 Clip（半径 0 → 圆外立刻露出旧图）
            // ⚠️ 用 Clip 而不是"覆盖层 + 双图"：真实 UI 一直在渲染，
            //    分段控件的滑块动画可以继续滑，不会被快照冻在旧位置。
            var clip = new EllipseGeometry(center, 0, 0);
            rootEl.Clip = clip;

            // ④ 动画：只放大裁剪圆
            int my = ++_themeRevealSeq;
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var dur = TimeSpan.FromMilliseconds(420);
            var rx = new DoubleAnimation(0, maxR, dur) { EasingFunction = ease };
            var ry = new DoubleAnimation(0, maxR, dur) { EasingFunction = ease };
            rx.Completed += (s, e) =>
            {
                if (my != _themeRevealSeq) return;
                try { afterApply(); } catch { }
                rootEl.Clip = null;
                ThemeOldSnapshot.Visibility = Visibility.Collapsed;
                ThemeOldSnapshot.Source = null;
            };
            clip.BeginAnimation(EllipseGeometry.RadiusXProperty, rx);
            clip.BeginAnimation(EllipseGeometry.RadiusYProperty, ry);
        }


        /// <summary>
        /// 设置页「个性化」三选一切换。走独立处理器而不是 <see cref="OnSettingChanged"/>，
        /// 因为换主题**不需要**重建调度器 / 重读所有设置项 —— 那会顺带触发一串
        /// 无关的副作用（比如重建下载调度器会打断在途任务）。
        /// </summary>
        private void OnThemeChanged(object sender, RoutedEventArgs e)
        {
            // 加载配置回填控件时也会触发 Checked，必须挡掉（否则会把 System 又写回成具体值）
            if (_applyingSettings) return;

            AppTheme want;
            if (ThemeDarkRadio.IsChecked == true) want = AppTheme.Dark;
            else if (ThemeSystemRadio.IsChecked == true) want = AppTheme.System;
            else want = AppTheme.Light;

            try
            {
                // 换肤后的收尾（动画结束时才做：存盘 / 日志 / 提示文案）
                Action persist = () =>
                {
                    _settings.Theme = want;
                    SettingsStore.Save(_settings);
                    UpdateThemeHint();
                    // 「当前状态」四行与四张网盘卡的颜色都是构建时从资源里取的，
                    // 换肤后必须重建一次，否则会停留在旧主题的配色上。
                    try { RefreshPanStateList(); } catch { }
                    try { RefreshPanCards(); } catch { }
                    Log(ThemeLogText(want));
                };

                // 目标明暗（选「跟随系统」时按当前系统判断）
                bool targetDark = want == AppTheme.Dark
                    || (want == AppTheme.System && ThemeManager.IsSystemDark());

                // 明暗实际没变（例如 浅色 ↔ 跟随系统，而系统本来就是浅色）→ 直接切，不播动画
                if (targetDark == ThemeManager.IsDarkEffective)
                {
                    ThemeManager.SetTheme(want);
                    persist();
                    return;
                }

                // 2026-10-18 用户又反馈「切换时整个界面特别模糊」→ 旧快照改回全分辨率
                // （0.6 倍快照铺满全窗必然糊）。现在唯一的前置耗时是抓旧图（全分辨率 ≈ 100ms）；
                // 抓完立刻换肤 + 上圆形 Clip，滑块与扩散**同时进行** ——
                // Clip 方案下真实 UI 一直在渲染，滑块不会被冻住。
                PlayThemeReveal(sender as FrameworkElement, want, persist);
            }
            catch (Exception ex)
            {
                Log("切换主题失败：" + ex.Message);
            }
        }


        /// <summary>把配置里的主题回填到单选按钮（加载设置时调用）。</summary>
        private void SyncThemeRadios()
        {
            try
            {
                switch (_settings.Theme)
                {
                    case AppTheme.Dark: ThemeDarkRadio.IsChecked = true; break;
                    case AppTheme.Light: ThemeLightRadio.IsChecked = true; break;
                    default: ThemeSystemRadio.IsChecked = true; break;
                }
                UpdateThemeHint();
            }
            catch { }
        }


        /// <summary>
        /// 说明行的文案随选择变化：
        /// 选「跟随系统」时要讲清"系统是深色/浅色"以及"改系统会自动跟"，
        /// 否则用户会疑惑"我明明选了跟随系统，现在到底是哪个"。
        /// </summary>
        private void UpdateThemeHint()
        {
            try
            {
                if (ThemeHintText == null) return;

                if (ThemeSystemRadio.IsChecked == true)
                {
                    // 去掉括号里的「（当前系统为浅色）」——
                    // 选了「跟随系统」后当前是什么色，界面本身就是答案，这句是噪音。
                    ThemeHintText.Text = "跟随系统时，Windows 切换深色/浅色，本程序会自动跟着变。";
                }
                else if (ThemeDarkRadio.IsChecked == true)
                {
                    ThemeHintText.Text = "始终使用深色界面，不受系统设置影响。";
                }
                else
                {
                    ThemeHintText.Text = "始终使用浅色界面，不受系统设置影响。";
                }
            }
            catch { }
        }


        private static string ThemeLogText(AppTheme t)
        {
            if (t == AppTheme.Dark) return "外观已切换为深色";
            if (t == AppTheme.Light) return "外观已切换为浅色";
            return "外观已设为跟随系统（当前" + (ThemeManager.IsSystemDark() ? "深色" : "浅色") + "）";
        }


        /// <summary>
        /// 主题切换后重画托盘图标（2026-10-04 新增）。
        /// 订阅在 <see cref="InitTray"/> 里（此时 _tray 才存在），退出时在
        /// <see cref="ExitApp"/> 前面的 Dispose 路径里已经 Dispatcher 掉了 —— 但为了
        /// 不留悬挂引用，OnExit 之前若窗口已关，事件仍持有 this；这里用弱式保护：
        /// 回调里先看 `_exiting`，退出中直接忽略。
        /// </summary>
        private void OnThemeChangedRedrawTray(bool isDark)
        {
            try
            {
                if (_exiting) return;
                // 保险：跟随系统模式下 ThemeManager 已切回 UI 线程，这里再兜一层。
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(() => OnThemeChangedRedrawTray(isDark)));
                    return;
                }
                _tray?.RefreshIcon();
            }
            catch { }
        }
    }
}
