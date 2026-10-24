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
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GeZi
{
    /// <summary>
    /// 「关于」窗口：软件介绍 + 赞赏码（可点击放大）。
    /// 用户要求「和 Python Flet 版那个差不多」——Flet 版有个 `_show_about()`
    /// 带品牌行、B 站链接、应用简介、功能特性、赞赏支持。这里按当前 C# 版的实际情况
    /// **重写了介绍内容**（Flet 版那套功能描述已经对不上了）。
    /// ⚠️ 赞赏码用的是 Flet 版里那份 `APP_REWARD_LG_B64`（360×360）解出来的
    /// `Resources/reward-qr.png` —— 同一张码，没有重画。
    /// </summary>
    public partial class AboutWindow : Window
    {
        /// <summary>作者 B 站主页（沿用 Flet 版那个短链）。</summary>
        private const string BiliUrl = "https://b23.tv/gIoEfQM";

        /// <summary>
        /// 项目开源地址（GitHub）。
        /// 这类工具的用户最怕"来路不明的 exe"（见过太多捆绑木马的），
        /// 源码公开是他们判断可信度的第一依据 —— 比任何"本软件无毒"的声明都有用。
        /// </summary>
        private const string RepoUrl = "https://github.com/huaotem-bot/gezi-quark-downloader";

        /// <summary>功能特性（面向普通用户，一条一句话）。</summary>
        /// <remarks>
        /// 用户提醒「怎么没在简介里写免转存功能，这不是很大的一个特点吗」——
        /// 确实漏了。补上后顺带把「多账号」也加了（也是别人常问的）。
        /// 写法要求：**说清"对你有什么好处"**，别只写功能名。
        /// </remarks>
        /// <remarks>
        /// 改成 **两列小卡**（标题 + 一句说明），
        /// 所以这里从"一行长文字"拆成两项。写法仍是"说清对你有什么好处"。
        /// </remarks>
        private static readonly string[,] Features =
        {
            { "解析分享", "链接、提取码、整段分享文案都能直接粘" },
            { "免转存下载", "不往自己网盘写中转文件，省空间、不留垃圾" },
            { "高速下载", "单文件最多 512 个连接，支持断点续传" },
            { "扫码登录", "浏览「我的网盘」，支持转存与删除" },
            { "边下边播", "目标文件从 0 连续增长，下载中即可播放" },
            { "导出直链", "纯直链、curl、aria2c 三种格式" },
            { "多账号", "可保存多个账号，随时一键切换" },
            { "托盘常驻", "关掉窗口也在后台继续下，带悬浮小窗" },
        };

        public AboutWindow()
        {
            InitializeComponent();

            // 版本号从程序集读，别再手写一份（写死了迟早对不上）
            try
            {
                var ver = Assembly.GetExecutingAssembly().GetName().Version;
                VersionPillText.Text = "v" + (ver != null ? ver.ToString(3) : "2.3.1");
            }
            catch { }

            BuildFeatureList();
            RefreshUpdateDot();

            Loaded += (s, e) =>
            {
                DialogChrome.ApplyRoundedCorners(this);
                // 再刷一次：启动检查是异步的，可能刚好在构造之后才完成。
                RefreshUpdateDot();
            };
        }

        // ---------------- 检查更新（2026-10-11 新增） ----------------

        private bool _checkingUpdate;

        /// <summary>按 <see cref="UpdateService.HasUpdate"/> 决定右上角红点显示与否。</summary>
        private void RefreshUpdateDot()
        {
            try
            {
                UpdateDot.Visibility = UpdateService.HasUpdate
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            catch { }
        }

        /// <summary>
        /// 点「检查更新」：立刻查一次（**不节流**），然后把结果明确告诉用户。
        /// 需求要求启动时**不打断用户** ——
        /// 启动只做后台检查 → 红点 + 气泡，用户不点就当没发生过。
        /// 这里是用户**主动点了**，所以有结果就该说清楚（失败也要给原因和手动地址），
        /// 与"静默"的启动检查是两回事。
        /// </summary>
        private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
        {
            if (_checkingUpdate) return;
            _checkingUpdate = true;
            CheckUpdateBtn.IsEnabled = false;
            try
            {
                var m = await UpdateService.CheckNowAsync();

                // Settings 由主窗口 Attach；正常路径一定有，防御一下免得测试宿主崩
                if (UpdateService.Settings != null)
                    UpdateService.Apply(UpdateService.Settings, m, null);
                RefreshUpdateDot();

                // ① 有新版本、但之前被忽略了 → 先问要不要恢复提醒
                //    （不能装作没有，也不能说"再点一次就好"—— 那样点了还是被忽略）
                if (m != null && UpdateService.CurrentVersion != null
                    && m.Version.CompareTo(UpdateService.CurrentVersion) > 0
                    && UpdateService.IsIgnored(m.Version))
                {
                    var resume = AppDialog.Show(this,
                        "你之前忽略了 v" + m.Version + " 的更新提醒。\n\n是否恢复提醒并查看更新？",
                        "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (resume != MessageBoxResult.Yes) return;

                    if (UpdateService.Settings != null)
                    {
                        UpdateService.ClearIgnore(UpdateService.Settings);
                        SettingsStore.Save(UpdateService.Settings);
                    }
                    RefreshUpdateDot();
                }

                // ② 有更新（且未被忽略）→ **直接**展示详情，不再多问一层「是否查看更新详情？」
                //    「忽略此版本」的入口已挪进详情窗左下角 —— 原来它挂在那个确认框的「否」上，
                //    用户只想"不展开详情"却会**永久屏蔽该版本**，手滑代价太大。
                if (UpdateService.HasUpdate && m != null)
                {
                    OpenUpdatePrompt(m);
                    RefreshUpdateDot();
                }
                else if (UpdateService.Latest != null
                         && UpdateService.Latest.Version.CompareTo(UpdateService.CurrentVersion) > 0)
                {
                    // 有新版本但被用户忽略了 —— 不能谎称"已是最新"，要说清楚是被忽略的
                    AppDialog.Show(this,
                        "你已忽略 v" + UpdateService.Latest.Version + " 的更新提醒。\n"
                        + "当前版本仍是 v" + UpdateService.CurrentVersion + "。\n\n"
                        + "想恢复提醒：再点一次「检查更新」即可。",
                        "检查更新");
                }
                else
                {
                    AppDialog.Show(this,
                        "当前已是最新版本（v" + UpdateService.CurrentVersion + "）。",
                        "检查更新");
                }
            }
            catch (Exception ex)
            {
                AppDialog.Show(this,
                    "检查更新失败：" + ex.Message
                    + "\n\n你也可以手动打开 Releases 页查看：\n" + UpdateService.ReleasesPageUrl,
                    "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _checkingUpdate = false;
                CheckUpdateBtn.IsEnabled = true;
            }
        }

        /// <summary>
        /// 把 <see cref="Features"/> 填成两列小卡（<c>FeatureList</c> 是 UniformGrid，Columns=2）。
        /// 每格 = 标题（半粗）+ 一句说明（小一号、淡色）。
        /// </summary>
        /// <summary>
        /// 打开「更新详情」窗口。
        /// 抽出来是因为有两个入口：「检查更新」按钮，以及启动气泡跳过来的场景。
        /// </summary>
        private void OpenUpdatePrompt(GeZi.Core.Update.GitHubRelease m)
        {
            if (m == null) return;

            var prompt = new UpdatePromptWindow(m)
            {
                Owner = this,
            };
            prompt.ShowDialog();

            // 用户在详情窗里**明确**点了「忽略此版本」才落盘
            if (prompt.IgnoredByUser && UpdateService.Settings != null)
            {
                UpdateService.IgnoreCurrent(UpdateService.Settings);
                SettingsStore.Save(UpdateService.Settings);
            }
        }

        private void BuildFeatureList()
        {
            var title = (Brush)FindResource("TextPrimaryBrush");
            var desc = (Brush)FindResource("TextTertiaryBrush");
            var cardBg = (Brush)FindResource("SurfaceAltBrush");
            var cardBorder = (Brush)FindResource("BorderBrushSoft");

            int rows = Features.GetLength(0);
            for (int i = 0; i < rows; i++)
            {
                bool leftCol = (i % 2) == 0;

                var stack = new StackPanel();
                stack.Children.Add(new TextBlock
                {
                    Text = Features[i, 0],
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = title,
                });
                stack.Children.Add(new TextBlock
                {
                    Text = Features[i, 1],
                    FontSize = 10.5,
                    LineHeight = 15,
                    Margin = new Thickness(0, 3, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = desc,
                });

                var cell = new Border
                {
                    Child = stack,
                    Background = cardBg,
                    BorderBrush = cardBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(11, 9, 11, 9),
                    Margin = new Thickness(leftCol ? 0 : 4, i < 2 ? 0 : 8, leftCol ? 4 : 0, 0),
                    SnapsToDevicePixels = true,
                };

                FeatureList.Children.Add(cell);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            try { Close(); } catch { }
        }

        private void OnBiliClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!ShellLaunch.OpenUrl(BiliUrl))
                    AppDialog.Show(this, "打开浏览器失败，请手动访问：\n\n" + BiliUrl, "打开链接");
            }
            catch { }
        }

        /// <summary>点「开源地址」→ 用默认浏览器打开 GitHub 仓库。</summary>
        private void OnRepoClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!ShellLaunch.OpenUrl(RepoUrl))
                    AppDialog.Show(this, "打开浏览器失败，请手动访问：\n\n" + RepoUrl, "打开链接");
            }
            catch { }
        }

        /// <summary>点赞赏码 → 弹一个放大的窗口（和 Flet 版的「赞赏支持」弹窗同一个意思）。</summary>
        private void OnQrClick(object sender, RoutedEventArgs e)
        {
            try { ShowReward(); } catch { }
        }

        private void ShowReward()
        {
            var win = new Window
            {
                Owner = this,
                Title = "赞赏支持",
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (Brush)FindResource("SurfaceBrush"),
                FontFamily = (FontFamily)FindResource("AppFontFamily"),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
            };
            TextOptions.SetTextFormattingMode(win, TextFormattingMode.Display);

            // 跟小窗/关于窗一样：WindowChrome（非分层窗口，文字才清晰）+ DWM 圆角
            System.Windows.Shell.WindowChrome.SetWindowChrome(win,
                new System.Windows.Shell.WindowChrome
                {
                    CaptionHeight = 40,
                    ResizeBorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(0),
                    GlassFrameThickness = new Thickness(0),
                    UseAeroCaptionButtons = false,
                });

            var img = new Image
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/Resources/reward-qr.png")),
                Width = 340,
                Height = 340,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var close = new Button
            {
                Content = "关闭",
                MinWidth = 96,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 14, 0, 0),
                Style = FindResource("PrimaryButton") as Style,
            };
            close.Click += (s2, e2) => { try { win.Close(); } catch { } };

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = "七十鸽 的赞赏码",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
            });
            panel.Children.Add(new TextBlock
            {
                Text = "用微信 / 支付宝扫码即可赞赏支持",
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)FindResource("TextTertiaryBrush"),
            });
            panel.Children.Add(img);
            panel.Children.Add(close);

            win.Content = new Border
            {
                BorderBrush = (Brush)FindResource("BorderBrushSoft"),
                BorderThickness = new Thickness(1),
                Child = panel,
            };

            win.Loaded += (s2, e2) => DialogChrome.ApplyRoundedCorners(win);
            win.ShowDialog();
        }
    }
}
