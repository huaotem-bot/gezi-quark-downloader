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

namespace GeZi.Providers.Uc
{
    /// <summary>
    /// UC 网盘的端点与指纹。
    /// <para>
    /// <b>关键事实：UC 与夸克是同一套后端。</b>
    /// 两边都是阿里的 ucpro 产品线，接口路径、参数名、返回结构完全一致，
    /// 连取链接口查询参数里的产品代号都写着 <c>pr=ucpro</c>（uc = UC）。
    /// 差别只有三处：域名、UA 里的客户端名、Cookie 所在域。
    /// </para>
    /// <para>
    /// 因此 UC 的实现是「复用夸克的协议，换掉这三处」，
    /// 而不是重新逆向一套接口 —— 这也是为什么 UC 是三个网盘里最快能落地的。
    /// </para>
    /// </summary>
    public static class UcConstants
    {
        /// <summary>
        /// API 主机。
        /// <para>
        /// ⚠️ <b>实测关键结论：UC 的 API 与网页前端是两台不同的主机。</b>
        /// 网页在 <c>drive.uc.cn</c>，但 <c>/1/clouddrive/...</c> 这些接口在
        /// <c>pc-api.uc.cn</c> 上。请求 <c>drive.uc.cn/1/clouddrive/...</c>
        /// 会命中 SPA 的 fallback 路由、返回 <b>index.html</b>（HTTP 200 + 一堆 HTML），
        /// 而不是 JSON —— 表现为"接口返回了无法解析的内容"，极难定位。
        /// </para>
        /// <para>
        /// 实测证据（<c>share_id</c> 用假值）：
        /// <c>pc-api.uc.cn</c> → <c>{"code":41006,"message":"分享不存在"}</c>（结构被接受）；
        /// <c>drive.uc.cn</c> → HTML 首页。
        /// </para>
        /// </summary>
        public const string ApiBase = "https://pc-api.uc.cn";

        /// <summary>网页前端主机（登录、取 ctoken 用）。</summary>
        public const string WebBase = "https://drive.uc.cn";

        public const string Referer = "https://drive.uc.cn/";
        public const string Origin = "https://drive.uc.cn";

        /// <summary>
        /// 产品代号查询参数值。
        /// <para>
        /// 夸克用 <c>pr=ucpro</c>，UC 用 <c>pr=UCBrowser</c>（依据 YunX 的
        /// <c>UCConstants</c> 抓包值）。实测两者在 UC 上都能返回业务错误，
        /// 但跟随官方抓包值更稳。
        /// </para>
        /// </summary>
        public const string Pr = "UCBrowser";

        /// <summary>登录态验证接口（未登录时 data 为空对象）。</summary>
        public const string AccountInfoUrl = "https://drive.uc.cn/account/info";

        /// <summary>
        /// 会话刷新接口：<b>去掉 <c>__puus</c> 后请求它，服务端会在 <c>Set-Cookie</c>
        /// 里重新下发一个新鲜的 <c>__puus</c>。</b>
        /// <para>
        /// 依据 YunX <c>UCApi.refreshSession</c>（<c>UCConstants.CONFIG_URL</c>）与
        /// AList 的 <c>refreshPuus</c>。<c>__pus</c> 是长效凭证，<c>__puus</c>
        /// 只有约 3 小时寿命 —— 不带 <c>__puus</c> 去问一次，就等于让服务端
        /// 重新签一个。这是「登录成功后过几小时就失效」的正解，
        /// 而不是让用户反复重新登录。
        /// </para>
        /// </summary>
        public const string ConfigUrl = "https://pc-api.uc.cn/1/clouddrive/config?pr=UCBrowser&fr=pc";

        /// <summary>
        /// WebView 登录页。UC 没有像夸克那样的扫码 token 接口，
        /// 官方客户端也是让用户在网页里登录后取 Cookie。
        /// </summary>
        public const string LoginUrl = "https://drive.uc.cn/";

        /// <summary>
        /// 关键 Cookie 字段：UC 与夸克共用这两个名字，
        /// 两个都齐才算拿到完整登录态（依据 YunX 的 isValidCookie）。
        /// </summary>
        public const string CookiePus = "__pus";
        public const string CookiePuus = "__puus";

        /// <summary>__puus 约 3 小时有效，提前到 90 分钟刷新。</summary>
        public const int PuusRefreshIntervalMs = 90 * 60 * 1000;

        /// <summary>
        /// API 调用 UA：伪装成 UC 网盘 PC 客户端。
        /// 与夸克同理 —— 取链与下载必须用同一身份，否则 CDN 会判定为盗链。
        /// </summary>
        /// <summary>
        /// ⚠️ Channel 必须是 <c>ucpan_other_ch</c>（UC 自己的渠道），
        /// 原来写成夸克的 <c>pckk_other_ch</c> 会自证「不是 UC 客户端」。
        /// 取值与 <see cref="GeZi.Core.Support.PanClientProfile"/> 的 UC 档案一致。
        /// </summary>
        public static string ApiUa
        {
            get { return GeZi.Core.Support.PanClientProfile.For(GeZi.Core.Api.PanKind.Uc).ApiUserAgent; }
        }

        /// <summary>下载直链 UA，必须与 <see cref="ApiUa"/> 同族。</summary>
        public static string DlUa
        {
            get { return GeZi.Core.Support.PanClientProfile.For(GeZi.Core.Api.PanKind.Uc).DownloadUserAgent; }
        }

        /// <summary>
        /// UC 客户端的 Sec-CH-UA 值。refs/LinkSwift 与 refs/YunX 两家都给 UC 下发这个头；
        /// 缺它是「非浏览器/非客户端」的明显特征。值必须与 UA 里的 Chromium/100 对齐。
        /// </summary>
        public static string SecChUa
        {
            get
            {
                return GeZi.Core.Support.PanClientProfile.For(GeZi.Core.Api.PanKind.Uc)
                    .CommonHeaders["Sec-Ch-Ua"];
            }
        }

        /// <summary>取链 URL 用的客户端版本号（sys=win32&amp;ve=1.8.8）。</summary>
        public const string ClientVersion = "1.8.8";

        /// <summary>
        /// 网页端 UA：只有 <c>drive.uc.cn/account/info</c> 认它。
        /// </summary>
        /// <remarks>
        /// 实测：同一个 <c>account/info</c>，用 <see cref="ApiUa"/> 请求稳定返回
        /// <c>{"success":false,"code":"AUTH_ERROR"}</c>，换成普通 Chrome UA 立刻
        /// 返回 <c>{"success":true,"data":{"nickname":…}}</c>。这条接口是网页域接口，
        /// 按 UA 分身份，与 API 会话的 <c>pc-api.uc.cn</c> 不是一套。
        /// </remarks>
        public const string WebUa =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        /// <summary>登录 Cookie 名。UC 与夸克同名但值不同、域不同。</summary>
        public const string LoginCookieKey = "__puus";

        /// <summary>
        /// CSRF token 的 Cookie 名（访问首页时下发）。
        /// 实测：UC 业务接口要求把它放进 <see cref="CsrfTokenHeader"/> 请求头，
        /// 否则返回 403 <c>missing csrf token</c>。
        /// </summary>
        public const string CsrfCookieName = "ctoken";

        /// <summary>CSRF token 请求头名（实测有效；<c>csrf-token</c> 无效）。</summary>
        public const string CsrfTokenHeader = "x-csrf-token";

        /// <summary>单次请求的默认页大小（与夸克保持一致，服务端接受 50）。</summary>
        public const int PageSize = 50;
    }
}