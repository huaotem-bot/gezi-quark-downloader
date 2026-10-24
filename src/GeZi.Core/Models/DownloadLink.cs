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

namespace GeZi.Core.Models
{
    /// <summary>取直链接口返回的单条记录（drive 与分享下载共用）。</summary>
    public class DownloadLink
    {
        public string Fid { get; set; }
        public string DownloadUrl { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }

        /// <summary>服务端返回的内容哈希；没有就为 null（完成时标记“未校验”）。</summary>
        public string Md5 { get; set; }
        public string Sha1 { get; set; }
        public string Sha256 { get; set; }
    }
}
