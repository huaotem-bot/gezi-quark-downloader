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

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GeZi.Core.Api;
using GeZi.Providers.Shared;
using GeZi.Services;

namespace GeZi
{
    /// <summary>
    /// 「导出 Cookie」弹窗：四家网盘 + 各自下面的所有账号，可勾选后打成一个 zip。
    /// <para>
    /// 用户 2026-10-26 的要求（原话）：「默认选中四个网盘，每个网盘右侧有一个展开的按钮，
    /// 展开以后能看见这家网盘下面的所有账号」，导出成「一个压缩包，里边有四个文件夹
    /// 分别是四个网盘的英文名，然后里边有对应的导出的账号」。
    /// </para>
    /// <para>
    /// 为什么单独一个文件：这段 UI 有 ~200 行、且只被一个入口调用，
    /// 塞进 MainWindow.Account.cs（已 2191 行）只会让它更难读。
    /// </para>
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>一家网盘在弹窗里的勾选状态。</summary>
        private sealed class ExportPanGroup
        {
            public PanKind Kind;
            public string DisplayName;
            public bool Checked = true;
            public readonly List<AccountProfile> Accounts = new List<AccountProfile>();
            /// <summary>每个账号是否勾选，键是 AccountProfile.Key。</summary>
            public readonly Dictionary<string, bool> AccountChecked = new Dictionary<string, bool>();
            public CheckBox HeadBox;
            public Button ExpandBtn;
            public StackPanel Body;
            public bool Expanded;
        }


        /// <summary>入口：点「导出 Cookie」。</summary>
        private void OnExportCookies(object sender, RoutedEventArgs e)
        {
            try
            {
                ShowExportCookieDialog();
            }
            catch (Exception ex)
            {
                Log("打开导出 Cookie 窗口失败：" + ex.Message);
                AppDialog.Show(this, "打开导出窗口失败：" + ErrorText.Describe(ex) + "。",
                    "导出 Cookie", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }


        private void ShowExportCookieDialog()
        {
            var win = new Window
            {
                Owner = this,
                Title = "导出 Cookie",
                ResizeMode = ResizeMode.CanResize,
            };
            DialogChrome.StyleWindow(win, 560, 580);

            var root = DialogChrome.DialogRoot();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                 // 说明
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 列表
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                 // 按钮

            // ---------- 说明 ----------
            var tip = DialogChrome.Tip(
                "勾选要导出的账号，点「导出」打成一个 zip。\n"
                + "zip 里按网盘分四个文件夹：quark / uc / aliyun / xunlei。\n"
                + "⚠️ 这些文件等同于账号登录凭证，请勿分享给他人。", 0, 12);
            Grid.SetRow(tip, 0);
            root.Children.Add(tip);

            // ---------- 四家 + 账号 ----------
            var groups = new List<ExportPanGroup>();
            var listPanel = new StackPanel { Orientation = Orientation.Vertical };

            foreach (var kind in new[] { PanKind.Quark, PanKind.Uc, PanKind.Aliyun, PanKind.Xunlei })
            {
                var g = new ExportPanGroup
                {
                    Kind = kind,
                    DisplayName = PanUrlParser.DisplayNameOf(kind),
                };
                try { g.Accounts.AddRange(AccountStore.LoadAll(kind)); }
                catch (Exception ex) { Log("读取" + g.DisplayName + "账号失败：" + ex.Message); }
                foreach (var a in g.Accounts)
                    g.AccountChecked[a.Key] = true;

                // 一家都建不出来（没登录过、也没存过账号）时仍然显示这一行，
                // 只是「展开」是灰的 —— 用户能看出"这家确实没东西"。
                BuildExportGroupRow(g, listPanel);
                groups.Add(g);
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = listPanel,
                Padding = new Thickness(0, 0, 4, 0),
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            // ---------- 底部按钮 ----------
            var exportBtn = DialogChrome.PrimaryButton("导出");
            exportBtn.MinWidth = 96;
            exportBtn.Click += (s, ev) =>
            {
                try { DoExportCookies(win, groups); }
                catch (Exception ex)
                {
                    Log("导出 Cookie 失败：" + ex.Message);
                    AppDialog.Show(win, "导出失败：" + ErrorText.Describe(ex) + "。",
                        "导出 Cookie", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            var cancelBtn = DialogChrome.SecondaryButton("取消");
            cancelBtn.MinWidth = 96;
            cancelBtn.Click += (s, ev) => win.Close();

            var row = DialogChrome.ButtonRow(exportBtn, cancelBtn);
            row.HorizontalAlignment = HorizontalAlignment.Right;
            row.Margin = new Thickness(0, 12, 0, 0);
            Grid.SetRow(row, 2);
            root.Children.Add(row);

            win.Content = root;
            win.ShowDialog();
        }


        /// <summary>构建一家网盘的行：勾选框 + 名称 + 账号数 + 右侧「展开」。</summary>
        private void BuildExportGroupRow(ExportPanGroup g, Panel host)
        {
            // 🚨 2026-10-26 用户反馈（附截图）：「这个界面特别古老的画风」。
            //   原来的问题：① 复选框是 WPF 默认的方形小框，一眼就是十年前的控件；
            //   ② 卡片圆角 8、内边距 12,9，四条边贴得紧，没有呼吸感；
            //   ③ 网盘名和「（2 个账号）」同一字号同一颜色，层级完全平。
            //   改法：复选框统一套 AppCheckBox（18px 圆角 6 自绘框 + 对勾），
            //   卡片圆角提到 12、内边距 14,11，账号数改成小胶囊徽章，
            //   展开按钮加一个方向箭头（收起时朝下、展开时朝上），一眼能看出可点。
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 11, 12, 11),
                Margin = new Thickness(0, 0, 0, 10),
            };
            card.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");

            var outer = new StackPanel { Orientation = Orientation.Vertical };

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var box = new CheckBox
            {
                IsChecked = g.Checked,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var cbStyle = TryFindResource("AppCheckBox") as Style;
            if (cbStyle != null) box.Style = cbStyle;
            // 复选框 + 「夸克网盘（3 个账号）」拼成一行，点文字也能勾。
            var headText = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var nameText = new TextBlock
            {
                Text = g.DisplayName,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            headText.Children.Add(nameText);

            // 账号数做成小胶囊：比裸文字更容易扫读，也不会和网盘名抢层级。
            var countPill = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            countPill.SetResourceReference(Border.BackgroundProperty, "SurfaceSunkenBrush");
            var countText = new TextBlock
            {
                Text = g.Accounts.Count + " 个账号",
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            countText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            countPill.Child = countText;
            headText.Children.Add(countPill);

            box.Content = headText;
            g.HeadBox = box;
            Grid.SetColumn(box, 0);
            head.Children.Add(box);

            // 展开按钮：文字 + 方向箭头（收起=▾ 展开=▴）
            var expandBtn = new Button
            {
                Style = TryFindResource("GhostButton") as Style,
                FontSize = 11,
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = g.Accounts.Count > 0,
            };
            var expandContent = new StackPanel { Orientation = Orientation.Horizontal };
            var expandLabel = new TextBlock { Text = "展开", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            expandLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var chevron = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M1,1 L5,5 L9,1"),
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 10, Height = 6,
                Margin = new Thickness(5, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Stretch = Stretch.Uniform,
            };
            chevron.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextTertiaryBrush");
            expandContent.Children.Add(expandLabel);
            expandContent.Children.Add(chevron);
            expandBtn.Content = expandContent;
            if (g.Accounts.Count == 0)
                expandBtn.ToolTip = "这家网盘还没有保存过账号";
            Grid.SetColumn(expandBtn, 1);
            head.Children.Add(expandBtn);
            g.ExpandBtn = expandBtn;

            outer.Children.Add(head);

            // 账号区（默认收起）
            var body = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(22, 8, 0, 2),
                Visibility = Visibility.Collapsed,
            };
            g.Body = body;
            foreach (var a in g.Accounts)
            {
                var ab = new CheckBox
                {
                    Content = string.IsNullOrEmpty(a.Nick) ? a.Key : a.Nick,
                    IsChecked = true,
                    FontSize = 12.5,
                    Margin = new Thickness(0, 4, 0, 4),
                };
                if (cbStyle != null) ab.Style = cbStyle;
                ab.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
                if (!string.IsNullOrEmpty(a.Remark))
                    ab.ToolTip = a.Remark;
                var key = a.Key;
                ab.Checked += (s, ev) => g.AccountChecked[key] = true;
                ab.Unchecked += (s, ev) => g.AccountChecked[key] = false;
                body.Children.Add(ab);
            }
            outer.Children.Add(body);

            // 展开 / 收起
            expandBtn.Click += (s, ev) =>
            {
                g.Expanded = !g.Expanded;
                body.Visibility = g.Expanded ? Visibility.Visible : Visibility.Collapsed;
                expandLabel.Text = g.Expanded ? "收起" : "展开";
                // 收起时箭头朝下（还有东西可展开），展开时朝上
                chevron.Data = Geometry.Parse(g.Expanded ? "M1,5 L5,1 L9,5" : "M1,1 L5,5 L9,1");
            };

            // 整家勾选框：勾/不勾同时同步到下面每个账号。
            box.Checked += (s, ev) =>
            {
                g.Checked = true;
                foreach (var a in g.Accounts) g.AccountChecked[a.Key] = true;
                SyncAccountBoxes(body, true);
            };
            box.Unchecked += (s, ev) =>
            {
                g.Checked = false;
                foreach (var a in g.Accounts) g.AccountChecked[a.Key] = false;
                SyncAccountBoxes(body, false);
            };

            card.Child = outer;
            host.Children.Add(card);
        }


        private static void SyncAccountBoxes(Panel body, bool value)
        {
            if (body == null) return;
            foreach (var child in body.Children)
            {
                var cb = child as CheckBox;
                if (cb != null) cb.IsChecked = value;
            }
        }


        /// <summary>真正干活：收集勾选 → 弹保存对话框 → 写 zip。</summary>
        private void DoExportCookies(Window owner, List<ExportPanGroup> groups)
        {
            var items = new List<CookieExportPack.Item>();
            int skippedNoSecret = 0;
            foreach (var g in groups)
            {
                if (!g.Checked) continue;
                string folder = FolderNameOf(g.Kind);
                if (folder.Length == 0) continue;
                foreach (var a in g.Accounts)
                {
                    bool on;
                    if (!g.AccountChecked.TryGetValue(a.Key, out on) || !on) continue;
                    if (string.IsNullOrWhiteSpace(a.Cookie))
                    {
                        // 原来这里是静默跳过：用户勾了 5 个，导出完只说「已导出 3 个」，
                        // 剩下 2 个为什么没导出一句解释都没有（多半是 DPAPI 解不开的坏档案）。
                        skippedNoSecret++;
                        continue;
                    }

                    var item = new CookieExportPack.Item
                    {
                        Folder = folder,
                        AccountName = string.IsNullOrEmpty(a.Nick) ? a.Key : a.Nick,
                        Secret = a.Cookie,
                    };

                    // 阿里云盘的凭证必须带上设备标识与私钥，否则换台机器导入后
                    // 每个请求都签不出 X-Signature，等于导了个废文件。
                    //
                    // ⚠️ 设备凭证是**全局单份**（存在 Settings 里，只有最近一次登录那套），
                    // 不在账号档案里，所以只能配给这家网盘当前主用的那个账号；
                    // 其余阿里账号只能导出令牌，导入后需要重新走一次登录换新设备。
                    if (g.Kind == PanKind.Aliyun && a.IsPrimary)
                    {
                        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
                        if (!string.IsNullOrEmpty(_settings.AliyunDeviceId))
                            extra["DeviceId"] = _settings.AliyunDeviceId;
                        if (!string.IsNullOrEmpty(_settings.AliyunDevicePrivateKey))
                            extra["DevicePrivateKey"] = _settings.AliyunDevicePrivateKey;
                        if (extra.Count > 0) item.Extra = extra;
                    }

                    items.Add(item);
                }
            }

            if (items.Count == 0)
            {
                AppDialog.Show(owner, "没有勾选任何有凭证的账号。",
                    "导出 Cookie", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出 Cookie",
                Filter = "ZIP 压缩包|*.zip",
                FileName = "GeZi-Cookies-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip",
                AddExtension = true,
                OverwritePrompt = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            };
            if (dlg.ShowDialog(owner) != true)
                return;

            // 🚨 原来这一句没有 try：写 zip 失败（磁盘满 / 目标被占用 / 路径过长）
            // 会直接把异常抛穿整个点击处理，用户只看到通用的「内部错误」弹窗，
            // 而且完全不知道导出没成功。
            string path;
            try
            {
                path = CookieExportPack.Create(dlg.FileName, items);
            }
            catch (Exception ex)
            {
                Log("导出 Cookie 失败：" + ex.Message);
                AppDialog.Show(owner,
                    "导出失败：" + ex.Message + "\n\n目标位置：" + dlg.FileName,
                    "导出 Cookie", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Log("已导出 " + items.Count + " 个账号的凭证：" + path);
            AppDialog.Show(owner,
                "已导出 " + items.Count + " 个账号到：\n\n" + path +
                (skippedNoSecret > 0
                    ? "\n\n另有 " + skippedNoSecret + " 个勾选的账号没有可用的凭证，已跳过"
                      + "（通常是换过电脑后旧档案解不开，重新登录一次即可）。"
                    : "") +
                "\n\n注意：压缩包内是账号登录凭证，请勿分享给他人。",
                "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
            owner.Close();
        }


        /// <summary>PanKind → zip 里的文件夹名（与 CookieExportPack.Folders 一致）。</summary>
        private static string FolderNameOf(PanKind kind)
        {
            switch (kind)
            {
                case PanKind.Quark: return "quark";
                case PanKind.Uc: return "uc";
                case PanKind.Aliyun: return "aliyun";
                case PanKind.Xunlei: return "xunlei";
                default: return "";
            }
        }
    }
}
