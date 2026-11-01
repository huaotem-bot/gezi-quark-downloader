// GeZi —— 多网盘下载器 (Multi-cloud downloader)
// Copyright (C) 2026  Yi Yuan
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/agpl-3.0>.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShapePath = System.Windows.Shapes.Path;

using GeZi.Services;
namespace GeZi
{
    /// <summary>
    /// 夸克扫码登录窗口。
    ///
    /// <para>
    /// <b>为什么从登录页搬出来：</b>登录页原来把二维码、状态、按钮全铺在左列，
    /// 光二维码就占 264×264，把「多网盘账号」四卡挤到下面，整页一大半是二维码框。
    /// 用户要求改成「点夸克卡的扫码登录 → 弹窗出二维码」。
    /// </para>
    /// <para>
    /// <b>这个类只做壳：</b>取码、轮询、换票、落地登录全部仍在
    /// <c>MainWindow.QrLogin.cs</c> 的 <c>OnQrLogin</c> 里 —— 那套逻辑要访问
    /// <c>_settings</c> / <c>_client</c> / <c>ApplyLoginAsync</c> 等主窗口成员，
    /// 整体搬进窗口会把主窗口的登录流程拆成两半。所以这里只暴露控件，
    /// 由主窗口直接操作（与 <see cref="WebLoginWindow"/> 的分工相反，但改动面最小、风险最低）。
    /// </para>
    /// </summary>
    internal sealed class QrLoginWindow : Window
    {
        /// <summary>二维码图像。未取到码时所属的 <see cref="QrImageBox"/> 是隐藏的。</summary>
        public Image QrImage { get; }

        /// <summary>装二维码的白底框（二维码必须白底黑块，深色主题下也不能跟着变）。</summary>
        public Border QrImageBox { get; }

        /// <summary>未取码时的占位框（灰底 + 二维码图标 + 提示文字）。</summary>
        public Border QrPlaceholderBox { get; }

        /// <summary>状态条容器，随扫码进度显隐。</summary>
        public Border QrStatusBox { get; }

        /// <summary>状态文字（等待扫码 / 已扫码待确认 / 成功 / 过期）。</summary>
        public TextBlock QrStatusText { get; }

        /// <summary>状态图标，图标形状由主窗口按语义挑。</summary>
        public ShapePath QrStatusIcon { get; }

        /// <summary>
        /// 唯一的操作按钮「刷新二维码」（取码与重取都是它）。
        /// <para>
        /// 原来这里是「获取二维码」+「刷新」两个按钮并排靠左 —— 用户反馈
        /// 「获取二维码和刷新没有居中太丑了，而且似乎只要保留一个刷新二维码功能就行了」。
        /// 取码与重取本来就是同一个动作（都走 <c>OnQrLogin</c>），拆成两个按钮
        /// 只是让人多看一眼"我该点哪个"，合并成一个反而清楚。
        /// </para>
        /// </summary>
        public Button RefreshBtn { get; }

        /// <summary>
        /// 兼容旧调用点的别名 —— 与 <see cref="RefreshBtn"/> 是**同一个按钮对象**。
        /// ⚠️ 同一个按钮，所以**不能**再给两者各挂一次 Click（会取两次码）。
        /// </summary>
        public Button GetBtn { get { return RefreshBtn; } }

        public QrLoginWindow()
        {
            Title = "扫码登录 · 夸克网盘";
            ResizeMode = ResizeMode.NoResize;
            // 高度用 UIA 实测反推，不是拍脑袋估的（本机 125% DPI，实测值都是物理像素）：
            //   标题 346-373 / 副标题 378-397 / 二维码框 414-744 / 按钮 763-795
            //   / 状态文字 822-841 / 状态框内边距 + 根边距 ≈ 至 876
            //   窗口顶 286 → 需要 (876-286)/1.25 ≈ 472 逻辑像素。
            // ⚠️ 写小了状态条会被切掉（456 时实测底部露出半截）；
            //    固定高度而不是 SizeToContent：状态条是 Collapsed/Visible 切换的，
            //    自适应高度会让窗口在「等待扫码 → 已扫码」时上下跳动。
            // 2026-10-26：弹窗统一后标题 17→18、说明行高 16→19，比上面实测那版多约 4px，
            //   474 → 486 留出余量（这段高度是死的，少给就切字）。
            //   同日再减 26：标题已移到自绘标题栏，内容区不再有那一行。
            DialogChrome.StyleWindow(this, 404, 460);

            var root = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };

            // ---- 说明（标题已画在自绘标题栏里，2026-10-26）----
            root.Children.Add(DialogChrome.Hint("用手机「夸克 App」扫描二维码，并在手机上确认。", 0, 14));

            // ---- 二维码区（固定 264 高，避免取码前后窗口跳动）----
            var qrGrid = new Grid { Height = 264, Margin = new Thickness(0, 0, 0, 14) };

            // ⚠️ Background=White 故意硬编码：二维码需要「深色模块 + 白色静默区」才扫得出来，
            //    深色主题下若跟着变深，扫码会直接失败（不是审美问题）。
            QrImageBox = new Border
            {
                Background = Brushes.White,
                BorderThickness = new Thickness(1),
                BorderBrush = DialogChrome.Res<Brush>("BorderBrushStrong", Brushes.Gray),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8),
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 264,
                Height = 264,
            };
            QrImage = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(QrImage, BitmapScalingMode.NearestNeighbor);
            QrImageBox.Child = QrImage;
            qrGrid.Children.Add(QrImageBox);

            var placeholderInner = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            placeholderInner.Children.Add(new ShapePath
            {
                Data = DialogChrome.Res<Geometry>("IconQr", null),
                Stretch = Stretch.Uniform,
                Width = 42,
                Height = 42,
                HorizontalAlignment = HorizontalAlignment.Center,
                Stroke = DialogChrome.Res<Brush>("BorderBrushStrong", Brushes.Gray),
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
            placeholderInner.Children.Add(new TextBlock
            {
                Text = "点下方按钮获取二维码",
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = DialogChrome.Res<Brush>("TextTertiaryBrush", Brushes.Gray),
                FontSize = 11,
            });
            QrPlaceholderBox = new Border
            {
                Background = DialogChrome.Res<Brush>("SurfaceSunkenBrush", Brushes.WhiteSmoke),
                BorderThickness = new Thickness(1),
                BorderBrush = DialogChrome.Res<Brush>("BorderBrushSoft", Brushes.LightGray),
                CornerRadius = new CornerRadius(10),
                Child = placeholderInner,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 264,
                Height = 264,
            };
            qrGrid.Children.Add(QrPlaceholderBox);
            root.Children.Add(qrGrid);

            // ---- 按钮行（居中，只有一个按钮） ----
            RefreshBtn = DialogChrome.PrimaryButton("刷新二维码");
            RefreshBtn.HorizontalAlignment = HorizontalAlignment.Center;
            RefreshBtn.MinWidth = 132;
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            btnRow.Children.Add(RefreshBtn);
            root.Children.Add(btnRow);

            // ---- 状态条 ----
            QrStatusIcon = new ShapePath
            {
                Data = DialogChrome.Res<Geometry>("IconClock", null),
                Stretch = Stretch.Uniform,
                Width = 15,
                Height = 15,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Stroke = DialogChrome.Res<Brush>("TextSecondaryBrush", Brushes.Gray),
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
            QrStatusText = new TextBlock
            {
                Text = "",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = DialogChrome.Res<Brush>("TextSecondaryBrush", Brushes.Gray),
            };
            var statusInner = new StackPanel { Orientation = Orientation.Horizontal };
            statusInner.Children.Add(QrStatusIcon);
            statusInner.Children.Add(QrStatusText);

            QrStatusBox = new Border
            {
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(10),
                Visibility = Visibility.Collapsed,
                Background = DialogChrome.Res<Brush>("SurfaceSunkenBrush", Brushes.WhiteSmoke),
                Child = statusInner,
            };
            root.Children.Add(QrStatusBox);

            Content = root;
        }
    }
}
