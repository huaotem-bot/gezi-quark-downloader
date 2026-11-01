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
using GeZi.Core.Api;
using GeZi.Core.Models;
using GeZi.Core.Support;

namespace GeZi.Services
{
    /// <summary>
    /// GitHub Releases 里的一个发布版本（2026-10-18「新一代检查更新」）。
    /// 旧实现把更新包托管在夸克网盘分享里，找包、转存、下载、
    /// 到期清理一整套都绑在夸克接口上（分享一失效整个更新流程就挂）。新版直接查
    /// GitHub Releases API：不依赖登录态、不写用户网盘、没有中转清理。
    /// 下载也不再由程序代劳 ——「前往下载」直接打开 Release 页，用户用浏览器下，
    /// 省掉整套安装/覆盖/重启逻辑，也不会再被系统当成"自我更新行为"。
    /// </summary>
    public sealed class GitHubRelease
    {
        /// <summary>从 tag_name（如 <c>v2.2.0</c>）解析出的三段版本号。</summary>
        public UpdateVersion Version { get; set; }

        /// <summary>原始 tag（如 <c>v2.2.0</c>），展示用。</summary>
        public string TagName { get; set; }

        /// <summary>Release 标题（GitHub 的 name 字段，如「鸽子下载 v2.2.0 —— …」）。</summary>
        public string Title { get; set; }

        /// <summary>更新说明（Markdown 原文）。可能为空。</summary>
        public string Body { get; set; }

        /// <summary>Release 详情页地址（「前往下载」就打开它）。</summary>
        public string HtmlUrl { get; set; }

        /// <summary>安装包字节数（从 assets 里挑 <c>GeZi-*-win64.zip</c>）；取不到为 0。</summary>
        public long AssetSize { get; set; }

        /// <summary>发布时间（UTC）；取不到为 null。</summary>
        public DateTime? PublishedAt { get; set; }

        /// <summary>是否可用（至少要有版本号和详情页地址）。</summary>
        public bool IsValid
        {
            get { return Version != null && !string.IsNullOrEmpty(HtmlUrl); }
        }
    }
}
