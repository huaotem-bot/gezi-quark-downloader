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
        }

        private static T Res<T>(string key, T fallback) where T : class
        {
            try
            {
                var v = System.Windows.Application.Current?.TryFindResource(key);
                return v as T ?? fallback;
            }
            catch { return fallback; }
        }

        /// <summary>给窗口套上主题：背景色、字体、尺寸习惯。</summary>
        public static void StyleWindow(Window w, double width, double height)
        {
            w.Width = width;
            w.Height = height;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.ShowInTaskbar = false;
            w.FontFamily = Res<FontFamily>("AppFontFamily", new FontFamily("Microsoft YaHei UI, Segoe UI"));
            w.Background = Res<Brush>("AppBgBrush", Brushes.White);
            w.SnapsToDevicePixels = true;
            w.UseLayoutRounding = true;

            // 🚨 原生标题栏的深浅**不归 WPF 管**（由 DWM 按窗口自己声明画），
            //    不设置的话深色模式下内容区是暗的、顶上那条标题栏还是白的。
            //    ⚠️ 句柄要在 Loaded 之后才存在；♻️ 还要在**用户切主题时**跟着变
            //    （窗口是长寿命的，用户可能开着诊断窗口去切主题）。
            w.Loaded += (s, e) =>
            {
                ApplyDarkTitleBar(w, ThemeManager.IsDarkEffective);
                Action<bool> h = dark => ApplyDarkTitleBar(w, dark);
                ThemeManager.ThemeChanged += h;
                // 窗口关了要解绑，否则事件会一直持有这个窗口（内存泄漏 + 对已关窗口调 DWM）
                w.Closed += (s2, e2) => ThemeManager.ThemeChanged -= h;
            };
        }

        /// <summary>页面大标题。</summary>
        public static TextBlock PageTitle(string text)
        {
            var t = new TextBlock
            {
                Text = text,
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            };
            var b = Res<Brush>("TextPrimaryBrush", null);
            if (b != null) t.Foreground = b;
            return t;
        }

        /// <summary>次级说明文字。</summary>
        public static TextBlock Hint(string text, double top = 0, double bottom = 0)
        {
            var t = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, top, 0, bottom),
            };
            var b = Res<Brush>("TextTertiaryBrush", null);
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
                if (i > 0) ((FrameworkElement)buttons[i]).Margin = new Thickness(8, 0, 0, 0);
                sp.Children.Add(buttons[i]);
            }
            return sp;
        }

    }
}
