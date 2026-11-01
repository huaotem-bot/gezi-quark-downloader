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
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;
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
    /// 托盘图标（常驻通知区）+ 主题化的右键菜单。
    /// WPF 本身没有托盘 API；`Shell_NotifyIcon` 自己 P/Invoke 要处理
    /// 消息窗口、图标句柄、任务栏重建（TaskbarCreated）等一堆细节。
    /// 本项目 <c>UseWindowsForms=true</c> **本来就开着**（「浏览目录」用
    /// `FolderBrowserDialog`），所以 NotifyIcon 是零新增依赖的最优解。
    /// ContextMenuStrip 是 Win32 经典外观（灰底、方角、系统字体），
    /// 跟本项目自绘的圆角卡片 UI 完全两个世界。所以这里用 **WPF 的 ContextMenu**
    /// （复用 Styles.xaml 里那套隐式 ContextMenu/MenuItem 样式）。
    /// ⚠️ 托盘图标**必须 Dispose**：不释放的话进程退出后图标会留在托盘上，
    ///    鼠标划过去才消失（最经典的托盘 bug）。
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private readonly WinForms.NotifyIcon _ni;
        private readonly WinForms.Timer _clickTimer;   // 区分单击 / 双击
        private Window _menuHost;
        private ContextMenu _menu;
        private MenuItem _miMini;
        private MenuItem _miPause;
        private bool _disposed;

        /// <summary>左键**单击**：显示 / 隐藏小窗（用户要求）。</summary>
        public event Action SingleClickRequested;

        /// <summary>左键**双击**：显示主窗口（用户要求）。</summary>
        public event Action DoubleClickRequested;

        /// <summary>菜单「显示/隐藏小窗」。</summary>
        public event Action ToggleMiniRequested;

        /// <summary>菜单「暂停全部 / 恢复全部」。</summary>
        public event Action PauseAllRequested;

        /// <summary>菜单「退出」—— 真正退出程序。</summary>
        public event Action ExitRequested;

        public TrayIcon(string tooltip)
        {
            _ni = new WinForms.NotifyIcon
            {
                Icon = BuildTrayIcon(),
                Text = Truncate(tooltip, 63),   // NOTIFYICONDATA.szTip 上限 64 字符（含结尾 0）
                Visible = true,
            };
            _ni.MouseClick += OnMouseClick;
            _ni.DoubleClick += OnDoubleClick;

            // 单击与双击的分流：WinForms 的 NotifyIcon 在双击前**必定先报一次单击**，
            // 所以单击动作要延迟一个"双击间隔"再执行；期间若来了双击就取消它。
            // 用系统配的双击时间（默认 500ms），用户改了设置也能跟上。
            _clickTimer = new WinForms.Timer
            {
                Interval = Math.Max(200, WinForms.SystemInformation.DoubleClickTime),
            };
            _clickTimer.Tick += (s, e) =>
            {
                _clickTimer.Stop();
                Raise(SingleClickRequested);
            };
        }

        // ============================================================
        // 图标：用主色画的「下载箭头」（用户指定，取代原来的 app.ico）
        // ============================================================

        /// <summary>
        /// 托盘图标 = 主题主色 + `IconDownload` 几何（向下箭头 + 底线）。
        /// 那是"衔橄榄枝的鸽子"，缩到 16px 后几乎看不清，
        /// 外面还带一大圈白底方块，在深色任务栏上很突兀（用户反馈「不好看」）。
        /// 直接把矢量图标渲染成位图更清晰，也能自动跟随 DPI（16/20/24/32px）。
        /// </summary>
        internal static System.Drawing.Icon BuildTrayIcon()
        {
            try
            {
                int size = WinForms.SystemInformation.SmallIconSize.Width;
                if (size <= 0) size = 16;
                var glyph = RenderGlyph(size);
                if (glyph == null) return LoadAppIcon();
                return BitmapToIcon(glyph);
            }
            catch
            {
                return LoadAppIcon();
            }
        }

        /// <summary>
        /// 主题切换后重画托盘图标（2026-10-04 新增）。
        /// <see cref="BuildTrayIcon"/> 用的是 `PrimaryBrush`，
        /// 而 DrawIcon 的结果是**一次性渲染的位图**，不会随资源字典变化自动更新。
        /// 深色模式下主色从 #4F6BED 变成 #5B74F0，不重画的话任务栏上还是旧颜色。
        /// （浅色→深色时差别很小，但既然做就做对。）
        /// </summary>
        public void RefreshIcon()
        {
            try
            {
                if (_ni == null) return;
                var old = _ni.Icon;
                _ni.Icon = BuildTrayIcon();
                // ⚠️ 旧 Icon 是 GDI 对象，不释放会泄漏（每次切主题漏一个）
                if (old != null && !ReferenceEquals(old, _ni.Icon)) old.Dispose();
            }
            catch { }
        }

        /// <summary>
        /// 把「下载箭头」渲染成指定位图尺寸（透明底）。
        /// 托盘图标和气泡通知共用它 —— 这样两处**永远是同一个图标**。
        /// </summary>
        internal static BitmapSource RenderGlyph(int size)
        {
            try
            {
                var geo = Application.Current?.TryFindResource("IconDownload") as Geometry;
                var brush = Application.Current?.TryFindResource("PrimaryBrush") as Brush
                            ?? Brushes.RoyalBlue;
                if (geo == null || size <= 0) return null;

                // 按几何的**实际包围盒**缩放并居中，而不是按 24×24 viewBox ——
                // 否则图形本身在 viewBox 里的留白会让图标显得又小又偏。
                var b = geo.Bounds;
                double target = size * 0.82;                       // 留 9% 边距给描边
                double scale = target / Math.Max(b.Width, b.Height);
                var g = geo.Clone();
                var tg = new TransformGroup();
                tg.Children.Add(new ScaleTransform(scale, scale));
                tg.Children.Add(new TranslateTransform(
                    (size - b.Width * scale) / 2 - b.X * scale,
                    (size - b.Height * scale) / 2 - b.Y * scale));
                g.Transform = tg;

                var pen = new Pen(brush, Math.Max(1.5, size * 0.115))
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                };

                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawGeometry(null, pen, g);

                var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            catch { return null; }
        }

        /// <summary>把 WPF 位图转成 GDI+ 的 Icon（注意 DestroyIcon，否则 GDI 句柄泄漏）。</summary>
        private static System.Drawing.Icon BitmapToIcon(BitmapSource src)
        {
            var ms = new MemoryStream();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            enc.Save(ms);
            ms.Position = 0;

            using (var bmp = new System.Drawing.Bitmap(ms))
            {
                IntPtr h = bmp.GetHicon();
                try
                {
                    // Clone 之后 Icon 拥有自己的数据，才能安全 DestroyIcon
                    using (var tmp = System.Drawing.Icon.FromHandle(h))
                        return (System.Drawing.Icon)tmp.Clone();
                }
                finally { DestroyIcon(h); }
            }
        }

        /// <summary>兜底：还是用 exe 资源里的 app.ico。取不到就退回系统默认图标。</summary>
        private static System.Drawing.Icon LoadAppIcon()
        {
            try
            {
                var res = Application.GetResourceStream(new Uri("Resources/app.ico", UriKind.Relative));
                if (res != null)
                {
                    using (var s = res.Stream)
                        return new System.Drawing.Icon(s, WinForms.SystemInformation.SmallIconSize);
                }
            }
            catch { }
            try { return System.Drawing.SystemIcons.Application; } catch { }
            return null;
        }

        // ============================================================
        // 鼠标：单击 = 小窗，双击 = 主窗口（用户指定）
        // ============================================================

        private void OnMouseClick(object sender, WinForms.MouseEventArgs e)
        {
            if (e.Button == WinForms.MouseButtons.Left)
            {
                _clickTimer.Stop();
                _clickTimer.Start();   // 等一个双击间隔，没有第二击才算"单击"
                return;
            }
            if (e.Button == WinForms.MouseButtons.Right)
                ShowMenu();
        }

        private void OnDoubleClick(object sender, EventArgs e)
        {
            _clickTimer.Stop();        // 取消挂起的"单击"
            Raise(DoubleClickRequested);
        }

        // ============================================================
        // 右键菜单
        // ============================================================

        /// <summary>
        /// 在鼠标位置弹出 WPF 右键菜单。
        /// ⚠️ 定位方式试过三种，只有最后一种对（都实测过菜单窗口的真实 rect）：
        ///   ① `AbsolutePoint` + `HorizontalOffset=光标X` → 菜单跑到屏幕**右下**（实测偏了 300+px）。
        ///      原因：WPF 会把它按 `PlacementTarget`（一个在屏幕外的 1×1 宿主窗口）去算，
        ///      再被"夹"回屏幕，结果完全不是光标位置。
        ///   ② `MousePoint` → 也不行。WPF 的 MousePoint 读的是**它自己的鼠标跟踪**，
        ///      而托盘菜单弹出时鼠标从没进过宿主窗口，读到的位置是陈旧的。
        ///   ③ ✅ **把宿主窗口挪到光标处 + `RelativePoint`(0,0)** —— 不依赖任何跟踪，
        ///      菜单就出现在宿主（= 光标）左上角。实测偏差 0~2px。
        /// ⚠️ 打开前必须把宿主设为**前台**，否则点菜单外面菜单不会自动收起
        ///    （托盘菜单不像普通右键菜单那样有鼠标捕获）。
        /// </summary>
        private void ShowMenu()
        {
            try
            {
                if (_menuHost == null)
                {
                    _menuHost = new Window
                    {
                        WindowStyle = WindowStyle.None,
                        ShowInTaskbar = false,
                        AllowsTransparency = true,
                        Background = Brushes.Transparent,
                        Width = 1,
                        Height = 1,
                        Left = -32000,     // 平时放屏幕外，用户看不到
                        Top = -32000,
                        ShowActivated = false,
                        ResizeMode = ResizeMode.NoResize,
                    };
                    _menuHost.Show();
                }

                if (_menu == null)
                {
                    _menu = BuildMenu();
                    // 菜单收起后把宿主挪回屏幕外：它虽然是 1×1 透明窗口，但停在
                    // 光标处会**吃掉那一个像素的点击**（点桌面图标时可能点到它）。
                    _menu.Closed += (s2, e2) =>
                    {
                        try { _menuHost.Left = -32000; _menuHost.Top = -32000; } catch { }
                    };
                }

                // WinForms 的 Cursor.Position 是**物理像素**，而 WPF 的 Left/Top 是 DIP，
                // 高 DPI 下必须换算，否则菜单会随缩放比例越跑越偏。
                var pt = WinForms.Cursor.Position;
                var dpi = VisualTreeHelper.GetDpi(_menuHost);
                _menuHost.Left = pt.X / (dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX);
                _menuHost.Top = pt.Y / (dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY);

                _menu.PlacementTarget = _menuHost;
                _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint;
                _menu.HorizontalOffset = 0;
                _menu.VerticalOffset = 0;

                var h = new WindowInteropHelper(_menuHost).Handle;
                if (h != IntPtr.Zero) NativeMethods.SetForegroundWindow(h);

                _menu.IsOpen = true;
            }
            catch { }
        }

        private ContextMenu BuildMenu()
        {
            var cm = new ContextMenu();

            // 托盘菜单左右空隙太大。
            // 根因：复用了带 **22px 图标列** 的隐式 MenuItem 样式（那是给任务行/文件列表
            // 右键菜单对齐图标用的），而托盘这四项**都没有图标** —— 图标列 + Margin 变成
            // 左侧一大块空白，Header 列又是 `*` 填满，右侧再留一截。
            // 统一套 TrayMenuItem（无图标列、Padding 14,7，见 Styles.xaml）。
            var itemStyle = Application.Current != null
                ? Application.Current.TryFindResource("TrayMenuItem") as Style
                : null;

            var miShow = new MenuItem { Header = "显示主窗口", Style = itemStyle };
            miShow.Click += (s, e) => Raise(DoubleClickRequested);
            cm.Items.Add(miShow);

            _miMini = new MenuItem { Header = "显示小窗", IsCheckable = true, Style = itemStyle };
            _miMini.Click += (s, e) => Raise(ToggleMiniRequested);
            cm.Items.Add(_miMini);

            _miPause = new MenuItem { Header = "暂停全部", Style = itemStyle };
            _miPause.Click += (s, e) => Raise(PauseAllRequested);
            cm.Items.Add(_miPause);

            // 🚨 分隔条**必须显式套上应用里的 Separator 样式**，不能指望隐式样式。
            // WPF 的 ContextMenu 对**菜单内**的分隔条用的是它自己内部那套
            // 菜单分隔条样式（挂在 `MenuItem.SeparatorStyleKey` 上），**会盖掉**
            // 应用里 `<Style TargetType="Separator">` 的隐式样式。
            // 实测取证（tmp-links 的 traymenu-probe，真弹菜单后读实例）：
            //   不显式设置 → `Separator.Background` = **#FFD7D7D7**（系统浅灰）、
            //                `Margin` = 0,2,0,2（也不是我们的 6,4）→ 深色模式下是**一条白横线**；
            //   显式设置后 → `Background` = #FF333844（BorderBrushSoft 深色）✅。
            // ⚠️ 试过在 `Application.Resources` 里给 `MenuItem.SeparatorStyleKey` 覆盖样式，
            //    **无效**（还是 #FFD7D7D7）—— 只有按到实例上才赢。
            var sep = new Separator();
            try
            {
                var sepStyle = Application.Current != null
                    ? Application.Current.TryFindResource(typeof(Separator)) as Style
                    : null;
                if (sepStyle != null) sep.Style = sepStyle;
            }
            catch { }
            cm.Items.Add(sep);

            var miExit = new MenuItem { Header = "退出", Style = itemStyle };
            miExit.Click += (s, e) => Raise(ExitRequested);
            cm.Items.Add(miExit);

            return cm;
        }

        /// <summary>同步「小窗」菜单项的勾选态与文案。</summary>
        public void SetMiniVisible(bool visible)
        {
            if (_miMini == null) return;
            _miMini.IsChecked = visible;
            _miMini.Header = visible ? "隐藏小窗" : "显示小窗";
        }

        /// <summary>同步「暂停全部 / 恢复全部」的文案。</summary>
        public void SetAllPaused(bool allPaused)
        {
            if (_miPause == null) return;
            _miPause.Header = allPaused ? "恢复全部" : "暂停全部";
        }

        /// <summary>
        /// 弹一条通知。
        /// ⚠️ **不再用 `_ni.ShowBalloonTip`**：Win10/11 会把它转成系统 toast，
        /// 而 toast 的图标取自进程的 AppUserModelID（= exe 的 app.ico），
        /// **不是**托盘图标 —— 用户看到的就是"一个黑疙瘩"（app.ico 是浅底 + 深色
        /// 鸽子线条，缩到 16px 就糊成一团）。改成自绘 toast，图标用**同一个**
        /// 下载箭头（<see cref="RenderGlyph"/>），配色也跟应用一致。
        /// </summary>
        public void ShowBalloon(string title, string text)
        {
            try { NotifyToast.Show(title, text, RenderGlyph(32)); } catch { }
        }

        private static void Raise(Action a)
        {
            if (a != null) { try { a(); } catch { } }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // ⚠️ 顺序：先 Visible=false 再 Dispose，否则某些 Windows 版本上
            //    托盘会留下一个"死图标"直到鼠标划过才消失。
            try { _clickTimer?.Stop(); _clickTimer?.Dispose(); } catch { }
            try { _ni.Visible = false; } catch { }
            try { _ni.Dispose(); } catch { }
            try { _menuHost?.Close(); } catch { }
            try { _menuHost = null; } catch { }
        }
    }
}
