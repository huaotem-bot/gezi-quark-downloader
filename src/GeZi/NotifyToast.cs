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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GeZi
{
    /// <summary>
    /// 应用内自绘的「气泡通知」（取代 <c>NotifyIcon.ShowBalloonTip</c>）。
    /// 用户反馈「这个提示的图标为什么是个黑疙瘩」——
    /// Win10/11 会把 `ShowBalloonTip` 转成系统 toast，而 toast 的图标取自
    /// **进程的 AppUserModelID**（也就是 exe 的图标 app.ico），**不是**
    /// NotifyIcon 的 hIcon。app.ico 是"浅底 + 深色鸽子线条"，缩到 16~20px
    /// 就成了一团黑。用户要的是**托盘那个图标**（蓝色下载箭头）。
    /// 而 toast 图标没法用 WinForms 的 NotifyIcon 控制（要 NIIF_USER + hBalloonIcon
    /// 手写 Shell_NotifyIcon，且 Win10 未必认）。所以干脆自绘一个：
    /// 图标、配色、字体全跟应用一致，还能顺手做得比系统 toast 好看。
    /// 行为：右下角（任务栏之上）弹出，6 秒后自动消失；鼠标移上去暂停计时，
    /// 点一下就关。同一时刻只留一个。
    /// </summary>
    internal sealed class NotifyToast : Window
    {
        private const int AutoCloseMs = 6000;

        private static NotifyToast _current;

        private readonly DispatcherTimer _timer;
        private readonly TextBlock _title;
        private readonly TextBlock _body;
        private readonly Image _icon;

        private NotifyToast()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Background = Brushes.Transparent;
            FontFamily = (FontFamily)(TryFindResource("AppFontFamily")
                                      ?? new FontFamily("Microsoft YaHei UI, Segoe UI"));
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            // 跟小窗同一套：WindowChrome（**不是**分层窗口，ClearType 正常）
            System.Windows.Shell.WindowChrome.SetWindowChrome(this,
                new System.Windows.Shell.WindowChrome
                {
                    CaptionHeight = 0,
                    ResizeBorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(0),
                    GlassFrameThickness = new Thickness(0),
                    UseAeroCaptionButtons = false,
                });

            var card = new Border
            {
                BorderThickness = new Thickness(1),
            };
            // 用 SetResourceReference（= 代码版的 DynamicResource）：切主题时会**自己**变色。
            // 原来写的 TryFindResource(...) ?? fallback 是**快照**语义 —— 弹窗存活期间
            // 用户手动切主题它不会变（虽然 toast 只有 3 秒，但既然统一走资源就统一到底）。
            card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");

            var grid = new Grid { Width = 320, Margin = new Thickness(14, 12, 14, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _icon = new Image
            {
                Width = 26,
                Height = 26,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 12, 0),
                Stretch = Stretch.Uniform,
            };
            RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);
            Grid.SetColumn(_icon, 0);
            grid.Children.Add(_icon);

            var texts = new StackPanel();
            _title = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            _body = new TextBlock
            {
                FontSize = 12,
                LineHeight = 18,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            _body.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            texts.Children.Add(_title);
            texts.Children.Add(_body);
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);

            card.Child = grid;
            Content = card;

            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(AutoCloseMs),
                DispatcherPriority.Background, (s, e) => Close(), Dispatcher);

            MouseEnter += (s, e) => _timer.Stop();
            MouseLeave += (s, e) => { _timer.Stop(); _timer.Start(); };
            MouseLeftButtonUp += (s, e) => Close();
            Loaded += (s, e) => DialogChrome.ApplyRoundedCorners(this);
            Closed += (s, e) =>
            {
                try { _timer.Stop(); } catch { }
                if (ReferenceEquals(_current, this)) _current = null;
            };
        }

        /// <summary>弹一条通知（同一时刻只保留一条，新的顶掉旧的）。</summary>
        internal static void Show(string title, string text, BitmapSource icon)
        {
            try
            {
                if (_current != null)
                {
                    try { _current.Close(); } catch { }
                    _current = null;
                }

                var t = new NotifyToast();
                t._title.Text = title ?? "";
                t._body.Text = text ?? "";
                if (icon != null) t._icon.Source = icon;
                else t._icon.Visibility = Visibility.Collapsed;

                _current = t;
                t.Show();
                t.Place();
                t._timer.Start();
            }
            catch { }
        }

        /// <summary>
        /// 摆到工作区右下角（`WorkArea` 已经扣掉任务栏）。
        /// ⚠️ `SizeToContent` 的窗口在 `Show()` 之前 ActualWidth/Height 还是 0，
        ///    所以要等布局跑完再摆位置，否则会贴到 (0,0)。
        /// </summary>
        private void Place()
        {
            UpdateLayout();
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 12;
            Top = wa.Bottom - ActualHeight - 12;
        }
    }
}
