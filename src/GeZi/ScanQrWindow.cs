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
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using GeZi.Core.Support;
using GeZi.Providers;
using GeZi.Providers.Quark;
using GeZi.Providers.Uc;
using GeZi.Providers.Aliyun;
using GeZi.Providers.Xunlei;
using GeZi.Providers.Shared;
using GeZi.Providers.Qr;
using GeZi.Services;
using WpfImage = System.Windows.Controls.Image;   // 避免与 System.Drawing.Image 撞名

namespace GeZi
{
    /// <summary>
    /// 「识别二维码」对话框（2026-10-04 新增）。
    /// 夸克分享有时只给一张二维码图片、不给文本链接（用户原话：
    /// 「有些时候夸克的分享链接不是一点点形式而是二维码」）。原来只能手动
    /// 把图存下来、再找第三方工具扫，很断。这里做进软件里。
    /// 按"顺手程度"排：
    ///   1. Ctrl+V 直接粘贴剪贴板里的截图 ← 默认，最快（截图后不用存盘）
    ///   2. 「选择图片…」从磁盘挑
    ///   3. 直接把图片文件拖到窗口上
    /// 三条路统一走 <see cref="QrDecoder"/> 解码，成功后把文本回传给主窗。
    /// </summary>
    internal sealed class ScanQrWindow : Window
    {
        private readonly Action<string> _onDecoded;   // 解出的文本（保证非空）
        private readonly Action<string> _log;

        private byte[] _pendingBytes;                  // 待解码的原始字节
        private string _pendingPath;                   // 或文件路径

        // ⚠️ 这里的 Image 是 WPF 的 System.Windows.Controls.Image（下方 using 了别名 WpfImage），
        //    不能直接写 Image —— 会跟 System.Drawing.Image 撞名（CS0104）。
        private WpfImage _imgPreviewBox;
        private StackPanel _emptyPanel;   // 空态（图标+提示），出图后隐藏
        private TextBlock _statusText;
        private Button _decodeBtn;

        /// <summary>
        /// 打开扫码窗口。<paramref name="onDecoded"/> 在解码成功后被调用（文本非空）。
        /// </summary>
        public ScanQrWindow(Action<string> onDecoded, Action<string> log)
        {
            _onDecoded = onDecoded;
            _log = log;

            Title = "识别二维码";
            ResizeMode = ResizeMode.NoResize;
            // 520×560 → 440×440：
            //   原来预览卡是 Star 行拉伸占满整个窗口（白框巨大），
            //   现在预览卡固定 220 高、居中，窗口跟着收小。
            DialogChrome.StyleWindow(this, 440, 440);
            // 圆角与标题栏深浅在 OnSourceInitialized 里设 —— 构造函数里窗口句柄还是
            // IntPtr.Zero，那里调是静默无效的（曾经因为这一行以为"圆角已经加过了"）。
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            DialogChrome.ApplyRoundedCorners(this);
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            // 开窗即尝试读取剪贴板：绝大多数场景就是"刚截完图过来"，直接命中
            TryPasteFromClipboard(silentIfEmpty: true);
        }

        // ---------------- 界面 ----------------

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            BuildUi();
        }

        private void BuildUi()
        {
            var root = new Grid { Margin = new Thickness(22, 20, 22, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 说明
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 预览
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 状态
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮

            // 标题已画在自绘标题栏里（2026-10-26），内容区不再重复。

            // 原本这里有一段三行的操作说明文字
            //（"把夸克分享二维码图片识别成链接。支持三种方式：…"），已**删除**。
            // 理由：三种方式在界面上本就一目了然（拖放区有提示、下方有「选择图片…」按钮），
            // 再堆三行文字是"加噪音"。Row 1 保留为空 Auto 行（高度 0）。

            // 预览区：一个虚线框感的卡片，里面放拖放提示或已选图片
            // 空态时中间显示二维码图标（IconQr 是描边几何，只能 Stroke）：
            var qrIcon = new System.Windows.Shapes.Path
            {
                Data = TryFindResource("IconQr") as System.Windows.Media.Geometry,
                Stretch = System.Windows.Media.Stretch.Uniform,
                Width = 44,
                Height = 44,
                StrokeThickness = 1.5,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            {
                var b = TryFindResource("TextTertiaryBrush") as System.Windows.Media.Brush;
                if (b != null) qrIcon.Stroke = b;
            }

            var dropHint = new TextBlock
            {
                Text = "把图片拖到这里\n（或 Ctrl+V 粘贴 / 点下方「选择图片…」）",
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                FontSize = 12.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0),
            };
            {
                var b = TryFindResource("TextTertiaryBrush") as System.Windows.Media.Brush;
                if (b != null) dropHint.Foreground = b;
            }

            _emptyPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _emptyPanel.Children.Add(qrIcon);
            _emptyPanel.Children.Add(dropHint);

            var preview = new WpfImage
            {
                Stretch = System.Windows.Media.Stretch.Uniform,
                Margin = new Thickness(6),
            };
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(
                preview, System.Windows.Media.BitmapScalingMode.HighQuality);

            var previewHost = new Grid();
            previewHost.Children.Add(_emptyPanel);
            previewHost.Children.Add(preview);
            _imgPreviewBox = preview;

            var previewCard = DialogChrome.Card(previewHost);
            // 固定高度 + 居中，不再吃 Star 行撑满窗口（原来 560 高的窗口大半是这块白）
            previewCard.Height = 220;
            previewCard.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(previewCard, 2);
            root.Children.Add(previewCard);

            _statusText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(2, 10, 2, 0),
                Visibility = Visibility.Collapsed,
            };
            Grid.SetRow(_statusText, 3);
            root.Children.Add(_statusText);

            var pickBtn = DialogChrome.SecondaryButton("选择图片…", (s, a) => PickFile());
            _decodeBtn = DialogChrome.PrimaryButton("识别", (s, a) => DecodeNow());
            _decodeBtn.IsEnabled = false;
            var cancelBtn = DialogChrome.SecondaryButton("取消", (s, a) => Close());

            var btns = DialogChrome.ButtonRow(pickBtn, _decodeBtn, cancelBtn);
            btns.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetRow(btns, 4);
            root.Children.Add(btns);

            Content = root;

            // ---- 交互：Ctrl+V 粘贴、拖放 ----
            AllowDrop = true;
            PreviewKeyDown += (s, a) =>
            {
                if (a.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    TryPasteFromClipboard(silentIfEmpty: false);
                    a.Handled = true;
                }
                else if (a.Key == Key.Escape)
                {
                    Close();
                }
                else if (a.Key == Key.Enter && _decodeBtn.IsEnabled)
                {
                    DecodeNow();
                    a.Handled = true;
                }
            };

            Drop += OnDrop;
            DragOver += (s, a) =>
            {
                a.Effects = a.Data.GetDataPresent(DataFormats.FileDrop)
                    ? DragDropEffects.Copy : DragDropEffects.None;
                a.Handled = true;
            };
        }

        // ---------------- 取图 ----------------

        private void PickFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择二维码图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(this) == true)
                LoadFromFile(dlg.FileName);
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;

            // 只认第一张能读出来的图片（拖多张时按顺序试）
            foreach (var f in files)
            {
                if (LoadFromFile(f)) return;
            }
            SetStatus("拖进来的文件读不出图片（支持的格式：PNG / JPG / BMP / GIF / WebP）", error: true);
        }

        private void TryPasteFromClipboard(bool silentIfEmpty)
        {
            try
            {
                if (!Clipboard.ContainsImage())
                {
                    if (!silentIfEmpty) SetStatus("剪贴板里没有图片。请先截图或复制一张二维码图片。", error: true);
                    return;
                }

                var src = Clipboard.GetImage();
                if (src == null)
                {
                    if (!silentIfEmpty) SetStatus("剪贴板里的图片读不出来。", error: true);
                    return;
                }

                // 转成 PNG 字节交给解码器（走 DecodeBytes 这条统一入口）
                byte[] png;
                using (var ms = new MemoryStream())
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(src));
                    enc.Save(ms);
                    png = ms.ToArray();
                }

                SetPending(png, null);
                SetStatus("已读取剪贴板图片（" + src.PixelWidth + "×" + src.PixelHeight + "），点「识别」或按回车。", error: false);
            }
            catch (Exception ex)
            {
                if (!silentIfEmpty) SetStatus("读剪贴板失败：" + ex.Message, error: true);
            }
        }

        private bool LoadFromFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using (var ms = new MemoryStream(bytes))
                {
                    // ⚠️ 完整限定 System.Drawing.Bitmap：本文件已用别名绑走 WPF 的 Image，
                    //    不 using System.Drawing（否则 Image 又会撞名）。
                    var bmp = new System.Drawing.Bitmap(ms);   // 先探一下能不能解码
                    int w = bmp.Width, h = bmp.Height;
                    bmp.Dispose();
                    SetPending(bytes, path);
                    SetStatus("已选择：" + Path.GetFileName(path) + "（" + w + "×" + h + "），点「识别」或按回车。", error: false);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private void SetPending(byte[] bytes, string path)
        {
            _pendingBytes = bytes;
            _pendingPath = path;
            _decodeBtn.IsEnabled = true;
            ShowPreview(bytes, path);
        }

        private void ShowPreview(byte[] bytes, string path)
        {
            try
            {
                BitmapImage bi;
                if (bytes != null)
                {
                    bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = new MemoryStream(bytes);
                    bi.EndInit();
                    bi.Freeze();
                }
                else
                {
                    bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.UriSource = new Uri(path);
                    bi.EndInit();
                    bi.Freeze();
                }
                _imgPreviewBox.Source = bi;
                // 图出来了 → 收起空态提示（图标+文字），只留图片
                if (_emptyPanel != null) _emptyPanel.Visibility = Visibility.Collapsed;
            }
            catch
            {
                // 预览失败不影响解码；图没出来 → 空态提示保持可见
                if (_emptyPanel != null) _emptyPanel.Visibility = Visibility.Visible;
            }
        }

        // ---------------- 解码 ----------------

        private void DecodeNow()
        {
            if (_pendingBytes == null)
            {
                SetStatus("还没有选图片。", error: true);
                return;
            }

            _decodeBtn.IsEnabled = false;
            SetStatus("正在识别…", error: false);

            byte[] bytes = _pendingBytes;
            string path = _pendingPath;

            // 解码要跑多轮图像变换（可能上百毫秒），放到线程池，别卡住窗口
            Task.Run(() =>
            {
                QrDecoder.Result r = path != null
                    ? QrDecoder.DecodeFile(path)
                    : QrDecoder.DecodeBytes(bytes);
                return r;
            }).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    SetStatus("识别出错：" + (t.Exception?.GetBaseException().Message ?? "未知错误"), error: true);
                    _decodeBtn.IsEnabled = true;
                    return;
                }

                var r = t.Result;
                if (r == null)
                {
                    SetStatus("识别失败。", error: true);
                    _decodeBtn.IsEnabled = true;
                    return;
                }

                if (!r.Ok)
                {
                    SetStatus(r.Error ?? "没识别到二维码。", error: true);
                    _decodeBtn.IsEnabled = true;
                    return;
                }

                // 成功
                _log?.Invoke("二维码识别成功（" + r.Format + "，" + r.Attempt + "）：" + r.Text);
                var cb = _onDecoded;
                Close();
                // 先关窗再回调，避免回调里弹的对话框被本窗口挡在后面
                cb?.Invoke(r.Text);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void SetStatus(string text, bool error)
        {
            _statusText.Text = text;
            _statusText.Visibility = Visibility.Visible;
            // SetResourceReference：跟着主题走（深色下 DangerBrush 是亮红 #EF4444，
            // 硬编码 Firebrick 在深底上几乎看不见）。
            _statusText.SetResourceReference(
                System.Windows.Controls.TextBlock.ForegroundProperty,
                error ? "DangerBrush" : "TextSecondaryBrush");
        }
    }
}
