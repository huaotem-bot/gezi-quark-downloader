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
    /// <summary>
    /// 目录/文件条目，经过 _norm 归一化后的形态。
    /// <para>
    /// 名字里的 "Quark" 是历史遗留：它最初只服务夸克，但后来成了
    /// <see cref="GeZi.Core.Api.IPanProvider.ListOwnDirAsync"/> 的返回类型，
    /// 是全应用统一的条目模型（界面与下载引擎都只认它）。
    /// 因此它必须留在 GeZi.Core，不能被挪进任何具体网盘项目。
    /// </para>
    /// </summary>
    public class QuarkFileEntry
    {
        public string Fid { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public bool IsDir { get; set; }
        /// <summary>分享场景下的 share_fid_token，取直链时需要。</summary>
        public string Token { get; set; }
    }
}
