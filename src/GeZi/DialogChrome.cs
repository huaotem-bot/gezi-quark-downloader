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

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using GeZi.Providers;
using GeZi.Providers.Quark;
using GeZi.Providers.Uc;
using GeZi.Providers.Aliyun;
using GeZi.Providers.Xunlei;
using GeZi.Providers.Shared;
using GeZi.Providers.Qr;

using GeZi.Services;
namespace GeZi
{
    /// <summary>
    /// 手写弹窗（诊断面板 / 下载历史 / 直链 / 账号管理 / 启动待处理）的统一样式脚手架。
    /// 这些窗口原本是用代码 `new Window { ... }` + `new Button { ... }`
    /// 拼出来的，控件全走系统默认外观：方角、灰色系统字体、无内边距 —— 放在
    /// 自绘主界面旁边像是另一个软件。此处把"套主题"这件事收拢成几个工厂方法，
    /// 各弹窗只管摆布局，外观由这里统一给。
    /// </summary>
    internal static class DialogChrome
    {
        // ---------------- 无边框窗口的 Win11 系统圆角 ----------------
        // WPF 自己没法给顶层窗口加圆角（除非开 AllowsTransparency，那会关掉
        // ClearType，文字发糊 —— 小窗就踩过这个坑）。Win11 的 DWM 提供原生圆角，
        // 一次调用即可，且由系统裁剪内容、不留锯齿。Win10 上调用会失败，静默忽略。

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE</summary>
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

        /// <summary>
        /// DWMWA_USE_IMMERSIVE_DARK_MODE —— 让 **Windows 自己画的原生标题栏** 变深色。
        /// 这些弹窗（下载诊断 / 识别二维码 / 获取直链 / 未完成任务 …）用的是
        /// **系统标题栏**（`WindowStyle` 保持默认，不像主窗那样自绘）。系统标题栏的颜色
        /// 不受 WPF 资源字典控制 —— 它由 DWM 按"应用是否声明深色"来画，默认是**浅色**。
        /// 所以深色模式下：内容区变暗了，唯独顶上那条标题栏**还是白的**（用户截图反馈）。
        /// 本调用告诉 DWM 把这个窗口按深色画。（Win10 1809+ 有效，无效时静默忽略。）
        /// ⚠️ 属性号在旧版 Win10 上是 **19**，2004+ 才是 **20** → 两个都试。
        /// ⚠️ **必须在窗口句柄存在之后**（`Loaded` 及以后）调用。
        /// </summary>
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        /// <summary>
        /// 按当前主题设置窗口的**原生标题栏**深浅。
        /// 返回是否成功（失败不抛，调用方可忽略）。
        /// </summary>
        public static bool ApplyDarkTitleBar(Window w, bool dark)
        {
            try
            {
                if (w == null) return false;
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return false;

                int v = dark ? 1 : 0;
                int hr = DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
                if (hr != 0)
                    hr = DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref v, sizeof(int));

                // 有些系统改完要"重画"标题栏才生效 → 轻微改一下窗口大小再改回来
                // （这是社区通用做法；不做的话个别版本上要等窗口被激活才刷新）
                try
                {
                    NativeMethods.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
                }
                catch { }

                return hr == 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// 给窗口加系统级圆角。**必须在 Loaded（或之后）调用** —— 那时窗口句柄才存在；
        /// 写在构造函数里拿到的是 `IntPtr.Zero`，函数会静默返回（曾经因此排查了很久）。
        /// <para>
        /// 2026-10-28 补：顺带把**原生标题栏**的深浅也按当前主题设一下。
        /// 此前 <see cref="ApplyDarkTitleBar"/> 全项目零调用 —— 是个死方法，
        /// 结果深色模式下所有弹窗的标题栏都是白的（对话框正文是黑的，上下两块颜色对不上）。
        /// 挂在这里是因为所有弹窗都已经走这个入口，不用再去改八个窗口。
        /// </para>
        /// </summary>
        public static void ApplyRoundedCorners(Window w)
        {
            try
            {
                if (w == null) return;
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;

                // DWMWCP_ROUNDSMALL = 3（"微微圆角"，Win11 22H2+ 才有）
                // DWMWCP_ROUND      = 2（标准圆角，更早的 Win11 也支持）
                int pref = 3;
                int hr = DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
                if (hr != 0)
                {
                    pref = 2;
                    DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
                }
            }
            catch { }

            // 标题栏深浅。取 IsDarkEffective 而不是 Current：Current 是用户偏好
            // （可能是「跟随系统」），IsDarkEffective 才是这一刻真实生效的那个。
            try { ApplyDarkTitleBar(w, ThemeManager.IsDarkEffective); } catch { }
        }

        /// <summary>
        /// 按 key 取主题资源，取不到时用 fallback。
        /// ⚠️ 是 <c>internal</c> 而不是 private：<see cref="QrLoginWindow"/> 用代码搭界面，
        ///    也需要同一套取资源逻辑（两边各写一份必然会漂移）。
        /// </summary>
        internal static T Res<T>(string key, T fallback) where T : class
        {
            try
            {
                var v = System.Windows.Application.Current?.TryFindResource(key);
                return v as T ?? fallback;
            }
            catch { return fallback; }
        }

        // ---------------- 自绘标题栏 ----------------
        // 这些弹窗原来用的是**系统标题栏**：左边一个应用图标 + 一行窗口标题文字，
        // 右边三个窗口按钮。用户反馈：
        //   「这些弹窗有些都是因为……上方有这个东西导致看起来很丑，我想让它不显示，
        //     当然右边的那些按键还是要保留」
        // → 改成 WindowStyle=None + WindowChrome，标题栏里**只留右侧窗口按钮**，
        //   不再有图标、不再有标题文字（标题由窗口内容里的 PageTitle 承担，
        //   原来系统标题栏和页面大标题写两遍，本身就是重复）。
        // ⚠️ 走 WindowChrome 而不是 AllowsTransparency：分层窗口会关掉 ClearType，文字发糊。
        // ⚠️ 与 AboutWindow.xaml 同一套已验证做法（1px 描边 Border + CaptionHeight + IsHitTestVisibleInChrome）。

        /// <summary>自绘标题栏高度（只有右侧窗口按钮，没有图标和标题文字）。</summary>
        private const double CaptionHeight = 40;

        /// <summary>已经装过自绘标题栏的窗口（防止重复包装）。</summary>
        private static readonly System.Collections.Generic.HashSet<Window> CaptionInstalled =
            new System.Collections.Generic.HashSet<Window>();


        /// <summary>给窗口套上主题：背景色、字体、尺寸习惯。</summary>
        public static void StyleWindow(Window w, double width, double height)
        {
            // ⚠️ 高度要多给 CaptionHeight：自绘标题栏在**客户区内部**
            //    （原来的系统标题栏在客户区之外，不占 WPF 的 Height）。
            //    不补这一截，内容区会比以前矮 40，底部那排按钮会被挤掉。
            w.Width = width;
            w.Height = height + CaptionHeight;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.ShowInTaskbar = false;
            w.WindowStyle = WindowStyle.None;
            w.FontFamily = Res<FontFamily>("AppFontFamily", new FontFamily("Microsoft YaHei UI, Segoe UI"));
            w.Background = Res<Brush>("AppBgBrush", Brushes.White);
            w.SnapsToDevicePixels = true;
            w.UseLayoutRounding = true;

            // 无边框之后靠 WindowChrome 把系统行为找回来：
            // 拖动 / 双击最大化 / 贴边分屏 / 边缘缩放 / 标题栏右键系统菜单。
            // ⚠️ ResizeMode 必须在这里就读到（各弹窗都在构造函数初始化器里设好，早于本方法）。
            bool resizable = w.ResizeMode == ResizeMode.CanResize || w.ResizeMode == ResizeMode.CanResizeWithGrip;
            System.Windows.Shell.WindowChrome.SetWindowChrome(w, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = CaptionHeight,
                ResizeBorderThickness = resizable ? new Thickness(6) : new Thickness(0),
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false,
            });

            // ⚠️ 圆角要在 Loaded 之后（那时窗口句柄才存在）。
            // ⚠️ 内容是在本方法**之后**才赋值的，所以标题栏在 Loaded 里装 ——
            //    那时内容已经在树上，且窗口还没画出来，不会闪一下。
            w.Loaded += (s, e) =>
            {
                ApplyRoundedCorners(w);
                InstallCaption(w);
            };
        }


        /// <summary>
        /// 把窗口内容包进「自绘标题栏 + 原内容」两行网格。
        /// 标题栏是**透明的**：除了右侧窗口按钮，上面什么都不画。
        /// </summary>
        private static void InstallCaption(Window w)
        {
            try
            {
                if (w == null || CaptionInstalled.Contains(w)) return;
                var content = w.Content as UIElement;
                if (content == null) return;
                CaptionInstalled.Add(w);
                w.Closed += (s, e) => CaptionInstalled.Remove(w);

                var host = new Grid();
                host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CaptionHeight) });
                host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                System.Windows.Shapes.Path maxPath;
                host.Children.Add(BuildCaption(w, out maxPath));   // 第 0 行

                w.Content = null;                                  // 先摘下来，否则会报「元素已有父级」
                Grid.SetRow(content, 1);
                host.Children.Add(content);

                var shell = new Border
                {
                    BorderThickness = new Thickness(1),
                    Child = host,
                    Tag = maxPath,
                };
                shell.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
                shell.SetResourceReference(Border.BackgroundProperty, "AppBgBrush");
                w.Content = shell;

                // 最大化后窗口会顶到屏幕边缘、把这条 1px 描边吃掉 → 留 7px
                // （与主窗 RootPadding() 的处理一致）。
                w.StateChanged += (s, e) =>
                {
                    try
                    {
                        shell.Margin = w.WindowState == WindowState.Maximized
                            ? new Thickness(7) : new Thickness(0);
                        var p = shell.Tag as System.Windows.Shapes.Path;
                        if (p != null)
                            p.Data = Res<Geometry>(w.WindowState == WindowState.Maximized
                                ? "IconRestore" : "IconMaximize", null);
                    }
                    catch { }
                };
            }
            catch { }
        }


        /// <summary>
        /// 标题栏本体：透明背景，右侧一排窗口按钮。
        /// 可缩放窗口给「最大化/还原 + 关闭」，固定尺寸窗口只给「关闭」。
        /// ⚠️ 不给「最小化」：这些弹窗 `ShowInTaskbar = false`，最小化后会从任务栏和
        ///    Alt+Tab 里一起消失，用户没有任何办法把它叫回来 —— 那是死路，不是功能。
        /// </summary>
        private static UIElement BuildCaption(Window w, out System.Windows.Shapes.Path maxPath)
        {
            maxPath = null;

            var bar = new Grid { Height = CaptionHeight, Background = Brushes.Transparent };

            // ---- 左侧：窗口标题 ----
            // 用户反馈（2026-10-26）「现在顶上看着特别空特别难看」：
            // 去掉系统标题栏之后，这 40px 里只剩下右上角两个按钮，
            // 左边整条是空的 —— 窗口顶上像缺了一块。
            // 现在把标题画在这里（对齐「解析历史」面板的做法：标题就在顶部条里），
            // 内容区不再重复一个大标题。
            var titleText = new TextBlock
            {
                Text = w.Title ?? "",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(titleText, false);
            bar.Children.Add(titleText);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            };
            // 标题栏本身是「拖动区」，按钮必须显式声明才点得动。
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(buttons, true);

            bool resizable = w.ResizeMode == ResizeMode.CanResize || w.ResizeMode == ResizeMode.CanResizeWithGrip;
            if (resizable)
            {
                var max = CaptionIconButton("IconMaximize", 11, "最大化或还原", "最大化 / 还原", "CaptionButton");
                maxPath = max.Tag as System.Windows.Shapes.Path;
                max.Click += (s, e) =>
                {
                    try
                    {
                        w.WindowState = w.WindowState == WindowState.Maximized
                            ? WindowState.Normal : WindowState.Maximized;
                    }
                    catch { }
                };
                buttons.Children.Add(max);
            }

            var close = CaptionIconButton("IconClose", 12, "关闭窗口", "关闭", "CaptionCloseButton");
            close.Click += (s, e) => { try { w.Close(); } catch { } };
            buttons.Children.Add(close);

            bar.Children.Add(buttons);
            return bar;
        }


        /// <summary>标题栏里那种「只有一个描边图标」的按钮。</summary>
        private static Button CaptionIconButton(string iconKey, double size,
            string automationName, string tooltip, string styleKey)
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = Res<Geometry>(iconKey, null),
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextSecondaryBrush");

            var b = new Button
            {
                Content = path,
                Tag = path,
                ToolTip = tooltip,
                // 34×30 的热区偏小（比 Windows 标准标题栏按钮窄一圈），
                // 鼠标稍微偏一点就点不中，深色下尤其难瞄。放大到 38×32。
                Width = 38,
                Height = 32,
            };
            var s = Res<Style>(styleKey, null);
            if (s != null) b.Style = s;
            System.Windows.Automation.AutomationProperties.SetName(b, automationName);
            return b;
        }

        /// <summary>
        /// 内容区大标题。
        /// <para>
        /// 🚨 2026-10-26 起：`StyleWindow` 的窗口**标题已经画在自绘标题栏里**，
        /// 内容区再调本方法就会出现两遍标题。八个弹窗的 `PageTitle` 调用已全部删除。
        /// 只有**不是** `StyleWindow` 窗口的地方（例如内嵌面板）才该用它。
        /// </para>
        /// </summary>
        public static TextBlock PageTitle(string text)
        {
            var t = new TextBlock
            {
                Text = text,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2),
            };
            var b = Res<Brush>("TextPrimaryBrush", null);
            if (b != null) t.Foreground = b;
            return t;
        }

        /// <summary>
        /// 弹窗根容器：四周统一 20 的内边距。
        /// 原来各窗口各写一套（22,20,22,18 / 20,18,20,18 / 22,18,22,16），
        /// 窗口之间来回切时标题会左右跳 2px、上下跳 2px —— 单看每个都还行，
        /// 连着开就有"没对齐"的感觉。
        /// </summary>
        public static Grid DialogRoot()
        {
            return new Grid { Margin = new Thickness(20) };
        }

        /// <summary>次级说明文字（12px，行高 19，二级灰）。</summary>
        public static TextBlock Hint(string text, double top = 0, double bottom = 0)
        {
            var t = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                LineHeight = 19,
                Margin = new Thickness(0, top, 0, bottom),
            };
            // 原来是三级灰（最淡那一档）。12px 的三级灰在浅底上偏"虚"，
            // 弹窗里的说明是用户唯一能读到操作含义的地方，提到二级灰。
            var b = Res<Brush>("TextSecondaryBrush", null);
            if (b != null) t.Foreground = b;
            return t;
        }

        /// <summary>
        /// 说明提示条：把一段说明收进浅底圆角块。
        /// 裸灰字直接压在标题下面时，标题和正文糊成一片 —— 这是"弹窗丑"的主要来源。
        /// 收进浅底块后，标题 → 提示条 → 内容 三段层级分明，也和下面的白卡片区分开了。
        /// </summary>
        public static Border Tip(string text, double top = 0, double bottom = 0)
        {
            return new Border
            {
                Child = Hint(text),
                Background = Res<Brush>("SurfaceAltBrush", Brushes.WhiteSmoke),
                BorderBrush = Res<Brush>("BorderBrushSoft", Brushes.LightGray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, top, 0, bottom),
            };
        }

        /// <summary>卡片内的小标题（13px 半粗，二级灰）。</summary>
        public static TextBlock SectionLabel(string text)
        {
            var t = new TextBlock
            {
                Text = text,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8),
            };
            var b = Res<Brush>("TextSecondaryBrush", null);
            if (b != null) t.Foreground = b;
            return t;
        }

        /// <summary>主色实心按钮。</summary>
        /// <summary>
        /// 主动作按钮。
        /// 「除主界面外的小窗口按钮全部改成背景透明」。
        /// 主界面的按钮走 XAML 里的 PrimaryButton 样式（不受影响）；
        /// 这些对话框一律用 GhostButton（透明底 + 悬停反馈）。
        /// </summary>
        public static Button PrimaryButton(string text, RoutedEventHandler onClick = null)
        {
            var b = new Button { Content = text };
            var s = Res<Style>("GhostButton", null);
            if (s != null) b.Style = s;
            if (onClick != null) b.Click += onClick;
            return b;
        }

        /// <summary>
        /// 正向确认按钮（绿色文字）。
        /// 2026-10-26 用户要求：「续传界面下方『继续下载』改为绿色」——
        /// 对话框按钮统一透明底（见 <see cref="PrimaryButton"/> 的注释），
        /// 所以这里只换文字色，形状与其余按钮保持一致，不破坏"小窗口全透明"的约定。
        /// </summary>
        public static Button SuccessButton(string text, RoutedEventHandler onClick = null)
        {
            var b = new Button { Content = text };
            var s = Res<Style>("GhostButton", null);
            if (s != null) b.Style = s;
            var fg = Res<System.Windows.Media.Brush>("SuccessBrush", null);
            if (fg != null) b.Foreground = fg;
            if (onClick != null) b.Click += onClick;
            return b;
        }

        /// <summary>白底描边按钮（次要操作）。小窗口统一透明化 → GhostButton。</summary>
        public static Button SecondaryButton(string text, RoutedEventHandler onClick = null)
        {
            var b = new Button { Content = text };
            var s = Res<Style>("GhostButton", null);
            if (s != null) b.Style = s;
            if (onClick != null) b.Click += onClick;
            return b;
        }

        /// <summary>
        /// 危险操作按钮（删除类）。背景透明化（用户要求全部透明），
        /// 但保留**红色文字**警示 —— 「丢弃这些任务」是破坏性操作，不能失去语义。
        /// </summary>
        public static Button DangerButton(string text, RoutedEventHandler onClick = null)
        {
            var b = new Button { Content = text };
            var s = Res<Style>("GhostButton", null);
            if (s != null) b.Style = s;
            var fg = Res<System.Windows.Media.Brush>("DangerBrush", null);
            if (fg != null) b.Foreground = fg;
            if (onClick != null) b.Click += onClick;
            return b;
        }

        /// <summary>圆角白卡片容器（带淡阴影）。</summary>
        public static Border Card(UIElement child)
        {
            var bd = new Border { Child = child };
            var s = Res<Style>("Card", null);
            if (s != null)
            {
                bd.Style = s;
            }
            else
            {
                bd.Background = Brushes.White;
                bd.CornerRadius = new CornerRadius(12);
                bd.Padding = new Thickness(18);
            }
            return bd;
        }

        /// <summary>套主题的列表（用于直链 / 历史 / 账号列表）。</summary>
        public static ListBox StyledList()
        {
            var lb = new ListBox();
            var s = Res<Style>("PlainListBox", null);
            if (s != null) lb.Style = s;
            lb.BorderThickness = new Thickness(0);
            // 去掉 WPF 默认 ListBoxItem 的「系统蓝」：
            //   选中 = 蓝底白字、键盘焦点 = 1px 浅蓝矩形描边（会套在圆角行卡片外面，很难看）。
            //   行内容自己就是完整卡片，容器只需原样呈现内容。见 Styles.xaml 的 PlainListRow。
            //   ⚠️ 个别列表若需要「悬停/选中」反馈，调用方在 StyledList() 之后
            //      再显式覆盖 ItemContainerStyle 即可（例如文件选择列表用的是 DialogListItem）。
            var item = Res<Style>("PlainListRow", null);
            if (item != null) lb.ItemContainerStyle = item;
            return lb;
        }

        /// <summary>文件管理器式页签（单选）。给「获取直链」的直链种类切换用。</summary>
        public static ToggleButton FileTab(string text, object tag = null)
        {
            var tb = new ToggleButton { Content = text, Tag = tag };
            var s = Res<Style>("FileTabItem", null);
            if (s != null) tb.Style = s;
            return tb;
        }

        /// <summary>页签条底部那条 1px 分隔线（未选中页签坐在它上面）。</summary>
        public static Border TabStrip()
        {
            var bd = new Border { Height = 1 };
            var s = Res<Style>("TabStripBorder", null);
            if (s != null) bd.Style = s;
            else bd.Background = Res<Brush>("BorderBrushSoft", null);
            return bd;
        }

        /// <summary>页签条 + 内容区的外框（上两角圆、下两角方，形成连体页签感）。</summary>
        public static Border TabHost(UIElement child)
        {
            var bd = new Border { Child = child };
            var s = Res<Style>("TabHostCard", null);
            if (s != null) bd.Style = s;
            else
            {
                bd.Background = Res<Brush>("SurfaceBrush", Brushes.White);
                bd.CornerRadius = new CornerRadius(0, 0, 10, 10);
                bd.BorderThickness = new Thickness(1, 0, 1, 1);
                bd.BorderBrush = Res<Brush>("BorderBrushSoft", null);
            }
            return bd;
        }

        /// <summary>横向等宽间距的按钮条（右对齐）。</summary>
        public static StackPanel ButtonRow(params UIElement[] buttons)
        {
            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            for (int i = 0; i < buttons.Length; i++)
            {
                var fe = (FrameworkElement)buttons[i];
                if (i > 0) fe.Margin = new Thickness(8, 0, 0, 0);
                // 统一按钮尺寸：原来宽度完全跟着字数走（「关闭」36px、「丢弃这些任务」90px），
                // 一排里参差不齐，看着像随手拼的。给 88×32 的下限：
                // 短文案撑满 88，长文案自然变宽；高度 32 让点击区不至于太薄。
                if (double.IsNaN(fe.Width) && fe.MinWidth == 0) fe.MinWidth = 88;
                if (double.IsNaN(fe.Height)) fe.Height = 32;
                sp.Children.Add(fe);
            }
            return sp;
        }

    }
}
