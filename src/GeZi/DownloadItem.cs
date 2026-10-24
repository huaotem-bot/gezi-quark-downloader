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

namespace GeZi
{
    /// <summary>取直链后的下载条目（名字/大小/直链/相对目录），与分享原 fid 解耦。</summary>
    internal class DownloadItem
    {
        public string Name;
        public long Size;
        public string Url;
        public string RelDir;

        /// <summary>取链接口返回的内容哈希；为空表示服务端没给（完成时显示“未校验”）。</summary>
        public string Md5;
        public string Sha1;
        public string Sha256;

        /// <summary>文件在网盘/分享里的 fid。直链过期时需要它去重新取链。</summary>
        public string Fid;

        /// <summary>分享内下载用的 share_fid_token（免转存路径需要）。</summary>
        public string Token;

        /// <summary>true = 走「分享免转存」取链（需要 pwd_id + stoken + token），false = 走我的网盘取链。</summary>
        public bool FromShare;

        /// <summary>分享提取码。重解析分享（换新 stoken）时要带上。</summary>
        public string Passcode;

        /// <summary>分享进入目录 fid（普通分享为 "0"）。重解析分享的起点。</summary>
        public string StartFid;

        /// <summary>
        /// 文件在分享内的完整相对目录（相对 <see cref="StartFid"/>）。
        /// 仅供重解析分享时逐级定位文件；本地路径仍看 <see cref="RelDir"/>。
        /// </summary>
        public string ShareRelDir;
    }
}
