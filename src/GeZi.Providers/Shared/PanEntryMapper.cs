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
using GeZi.Core.Models;
using GeZi.Core.Api;
using GeZi.Core.Support;

namespace GeZi.Providers.Shared
{
    /// <summary>
    /// 多网盘条目与界面既有条目类型之间的转换。
    /// <para>
    /// 界面（<c>MainWindow</c>）与整个下载引擎都是围绕 <see cref="QuarkFileEntry"/>
    /// 写的。为了不让新增网盘去改动那套已经跑通的代码，这里做一次**纯字段搬运**：
    /// 非夸克网盘取回的 <see cref="PanFileEntry"/> 转成 <see cref="QuarkFileEntry"/>
    /// 再交给界面。
    /// </para>
    /// <para>
    /// 单独抽出来的理由：这是"多网盘"与"现有 UI"之间唯一的接缝。
    /// 字段映射错一个（比如 <c>IsDir</c> 反了、<c>Token</c> 丢了），
    /// 表现就是「文件夹点不进去」或「取链说令牌无效」—— 很难从现象反推。
    /// 放在 Core 里就能被自测覆盖。
    /// </para>
    /// </summary>
    public static class PanEntryMapper
    {
        /// <summary>多网盘条目 → 界面/引擎使用的条目类型。</summary>
        public static QuarkFileEntry ToQuarkEntry(PanFileEntry src)
        {
            if (src == null)
                return new QuarkFileEntry { Fid = "", Name = "", Size = 0, IsDir = false, Token = "" };

            return new QuarkFileEntry
            {
                Fid = src.Fid ?? "",
                Name = src.Name ?? "",
                Size = src.Size,
                IsDir = src.IsDir,
                Token = src.Token ?? "",
            };
        }

        /// <summary>批量转换；<paramref name="src"/> 为 null 时返回空列表而不是 null。</summary>
        public static List<QuarkFileEntry> ToQuarkEntries(IEnumerable<PanFileEntry> src)
        {
            var result = new List<QuarkFileEntry>();
            if (src == null)
                return result;

            foreach (var e in src)
                result.Add(ToQuarkEntry(e));
            return result;
        }

        /// <summary>界面条目 → 多网盘条目（取直链时把 fid/name/token 带回去）。</summary>
        public static PanFileEntry ToPanEntry(QuarkFileEntry src)
        {
            if (src == null)
                return new PanFileEntry { Fid = "", Name = "", Size = 0, IsDir = false, Token = "" };

            return new PanFileEntry
            {
                Fid = src.Fid ?? "",
                Name = src.Name ?? "",
                Size = src.Size,
                IsDir = src.IsDir,
                Token = src.Token ?? "",
            };
        }

        /// <summary>批量转换；null 返回空列表。</summary>
        public static List<PanFileEntry> ToPanEntries(IEnumerable<QuarkFileEntry> src)
        {
            var result = new List<PanFileEntry>();
            if (src == null)
                return result;

            foreach (var e in src)
                result.Add(ToPanEntry(e));
            return result;
        }
    }
}