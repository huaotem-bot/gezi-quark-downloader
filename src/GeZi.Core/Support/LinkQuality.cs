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

namespace GeZi.Core.Support
{
    /// <summary>
    /// 直链质量评估。
    /// 同一个文件从不同官方入口取回的直链，CDN 节点、签名有效期、限速策略都可能不同，
    /// 质量差的表现为：下载中途容易 403/断流、单连接速度低。
    /// 这里用一个纯本地的启发式打分来排序候选项：
    /// 分数越高越优先使用。判据全部来自 URL 本身，不发额外请求、不触碰任何风控边界。
    /// </summary>
    public static class LinkQuality
    {
        /// <summary>对一条直链打分。返回负值表示"不可用"。</summary>
        public static int Score(string url)
        {
            if (string.IsNullOrEmpty(url) || url.Length <= 20)
                return -1;

            int score = 0;

            // 1) 必须是 https：http 直链在多数网络下会被劫持或降速
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                score += 100;

            // 2) 签名参数完整度：官方直链通常带 auth_key / 时间戳等，缺了就容易过期
            if (url.IndexOf("auth_key=", StringComparison.OrdinalIgnoreCase) >= 0) score += 30;
            if (url.IndexOf("Expires=", StringComparison.OrdinalIgnoreCase) >= 0) score += 15;
            if (url.IndexOf("Signature=", StringComparison.OrdinalIgnoreCase) >= 0) score += 15;

            // 3) 域名倾向：直连 CDN 域名通常比走跳转域名少一跳
            if (Contains(url, ".quark.cn")) score += 10;
            if (Contains(url, ".uc.cn")) score += 6;
            if (Contains(url, "cdn")) score += 5;

            // 4) 明显是占位/错误页的，直接判负
            if (Contains(url, "error") || Contains(url, "denied") || Contains(url, "forbidden"))
                score -= 200;

            // 5) 极短 URL 多半是跳转壳而非真实直链
            if (url.Length < 60) score -= 20;

            return score;
        }

        private static bool Contains(string haystack, string needle)
        {
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
