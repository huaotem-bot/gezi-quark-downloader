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

using System.Collections.Generic;
using GeZi.Core.Api;

namespace GeZi
{
    /// <summary>取直链后的下载条目（名字/大小/直链/相对目录），与分享原 fid 解耦。</summary>
    internal class DownloadItem
    {
        /// <summary>
        /// 为取这条直链而转存到自己网盘的文件 id（阿里/迅雷才会填，夸克/UC 免转存恒为空）。
        /// <para>
        /// 🚨 是**累计**列表而不是单值：下载中途直链过期会重新取链，每次都再转存一份，
        /// 全部都要在任务终态时清理，漏一个就在用户网盘里留一份垃圾。
        /// </para>
        /// </summary>
        public readonly List<string> StoredIds = new List<string>();

        /// <summary>上面这些转存 id 属于哪家网盘 —— 清理时必须用对客户端。</summary>
        public PanKind StoredKind;

        /// <summary>记一次转存（自动去重）。直链过期重取链时会被反复调用。</summary>
        public void AddStored(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (!StoredIds.Contains(id)) StoredIds.Add(id);
        }

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
