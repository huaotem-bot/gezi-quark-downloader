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

namespace GeZi.Core.Support
{
    /// <summary>
    /// 直链寿命的唯一来源。
    /// <para>
    /// 🚨 2026-10-30 建立：以前「缓存多久」和「自愈时一次刷新能撑多久」是两处独立硬编码
    /// （`LinkCache` 600 秒 / 融合引擎 300 秒），而且缓存比服务端有效期还长 ——
    /// 等于有相当概率把一条快过期的链喂给下载器，只能靠自愈兜底。
    /// 这里统一成一份，两边都从它取值，改一处即可全局生效。
    /// </para>
    /// </summary>
    public static class LinkLifetime
    {
        /// <summary>
        /// 服务端直链有效期（秒）。夸克 / UC 实测约 300 秒（5 分钟）。
        /// </summary>
        public const int ServerSeconds = 300;

        /// <summary>
        /// 自愈估算时，一次刷新实际「可用」的比例。
        /// 取 0.75 留 25% 余量，避免贴着过期边界才去刷新。
        /// </summary>
        public const double UsableRatio = 0.75;

        /// <summary>
        /// 本地缓存可用时长（秒）。必须**小于** <see cref="ServerSeconds"/>：
        /// 缓存命中时不会再去校验签名，超过服务端有效期就等于直接发死链。
        /// </summary>
        public const int CacheSeconds = 240;
    }
}
