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
using GeZi.Services;
using GeZi.Providers;
using GeZi.Providers.Quark;
using GeZi.Providers.Uc;
using GeZi.Providers.Aliyun;
using GeZi.Providers.Xunlei;
using GeZi.Providers.Shared;
using GeZi.Providers.Qr;

namespace GeZi
{
    /// <summary>
    /// 「更新详情」窗口（2026-10-18 新一代）。
    /// 展示"发现新版本"的关键信息（版本号 / 发布时间 / GitHub 更新说明），
    /// 「前往下载」把用户送到 Release 页（浏览器下载）。本窗口**不碰任何网络**，
    /// 保持职责单一。
    /// 旧实现把更新包托管在夸克网盘分享里，找包、转存、下载、
    /// 到期清理整套绑在夸克接口上；新版只读 GitHub Releases，安装包交给浏览器，
    /// 没有中转文件、没有覆盖安装、也没有"自我更新"的误报面。
    /// </summary>
    public partial class UpdatePromptWindow : Window
    {
        private readonly GitHubRelease _release;

        /// <summary>
        /// 用户在详情窗里点了「忽略此版本」。
        /// 由调用方（关于页）负责把它落到配置里 —— 本窗口不碰持久化。
        /// </summary>
        public bool IgnoredByUser { get; private set; }

        public UpdatePromptWindow(GitHubRelease release)
        {
            if (release == null) throw new ArgumentNullException("release");
            _release = release;

            InitializeComponent();

            NewVersionText.Text = "发现新版本 v" + _release.Version;
            CurrentVersionText.Text = "当前版本 v" + (UpdateService.CurrentVersion != null
                ? UpdateService.CurrentVersion.ToString() : "?");

            // 徽章：有发布时间就显示日期，否则显示安装包大小，都没有显示版本标签
            if (_release.PublishedAt.HasValue)
                SizeText.Text = _release.PublishedAt.Value.ToLocalTime().ToString("yyyy-MM-dd") + " 发布";
            else if (_release.AssetSize > 0)
                // 🚨 2026-11-04：统一走 Util.FormatSize（1024 进制），不再手写 /1048576.0。
                SizeText.Text = Util.FormatSize(_release.AssetSize);
            else
                SizeText.Text = "v" + _release.TagName;

            BodyText.Text = string.IsNullOrWhiteSpace(_release.Body)
                ? "（这个版本没有写更新说明，可打开 Release 页查看。）"
                : _release.Body.Trim();

            Loaded += (s, e) =>
            {
                DialogChrome.ApplyRoundedCorners(this);
            };
        }

        /// <summary>「前往下载」：打开 Release 详情页（浏览器下载安装包）。</summary>
        private void OnDownloadClick(object sender, RoutedEventArgs e)
        {
            var url = _release.HtmlUrl;
            if (string.IsNullOrEmpty(url))
                url = UpdateService.ReleasesPageUrl;
            try
            {
                if (!ShellLaunch.OpenUrl(url))
                    AppDialog.Show(this, "打开浏览器失败，请手动访问：\n\n" + url, "更新");
            }
            catch (Exception)
            {
                // 两种失败（ShellLaunch 返回 false / 抛异常）给同一句可操作的话：
                // 直接把网址摆出来让用户手动复制，不再贴异常原文。
                AppDialog.Show(this, "打开浏览器失败，请手动访问：\n\n" + url, "更新");
            }
            try { Close(); } catch { }
        }

        /// <summary>
        /// 「忽略此版本」：记下标记，由调用方落盘（之后不再提示这个版本）。
        /// 与「暂不」的区别：「暂不」只是这次不看，下次还会提醒。
        /// </summary>
        private void OnIgnoreClick(object sender, RoutedEventArgs e)
        {
            IgnoredByUser = true;
            try { Close(); } catch { }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            try { Close(); } catch { }
        }
    }
}
