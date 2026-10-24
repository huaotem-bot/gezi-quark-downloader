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

namespace GeZi.Core.Support
{
    /// <summary>夸克网页端公开接口的常量，全部来自原 Python 核心里已验证的取值。</summary>
    public static class QuarkConstants
    {
        public const string ApiBase = "https://drive-pc.quark.cn";
        public const string AccountUrl = "https://pan.quark.cn/account/info";

        /// <summary>
        /// 会话刷新接口：去掉 <c>__puus</c> 请求它，服务端会在 <c>Set-Cookie</c>
        /// 里重新下发新鲜的 <c>__puus</c>。与 UC 的
        /// <see cref="UcConstants.ConfigUrl"/> 同款机制（见 YunX
        /// <c>QuarkApi.refreshPuus</c>）。
        /// </summary>
        public const string ConfigUrl = "https://drive-pc.quark.cn/1/clouddrive/config?pr=ucpro&fr=pc";
        public const string Referer = "https://pan.quark.cn/";
        public const string Origin = "https://pan.quark.cn";

        /// <summary>
        /// API 调用用的 UA：伪装成夸克网盘 PC 客户端（Electron）。
        /// </summary>
        public const string ApiUa =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) quark-cloud-drive/3.14.2 Chrome/112.0.5615.165 " +
            "Electron/24.1.3.8 Safari/537.36 Channel/pckk_other_ch";

        /// <summary>
        /// 下载直链用的 UA。
        /// **必须与 ApiUa 保持同族（客户端伪装）**，不要改回普通浏览器 UA。
        /// 依据：同类开源项目 QuarkDownloader（油猴脚本）在下发直链给
        /// aria2 / cURL / Motrix 时，一律用「夸克客户端 UA」而非浏览器 UA，
        /// 并以同样方式附带 Cookie —— 见其 generateAria2Commands /
        /// generateCurlCommands / sendFilesToMotrixRpc。
        /// 原因是夸克 CDN 会对「取链时声明的客户端身份」与「实际下载时的身份」
        /// 做一致性校验：取链用客户端 UA、下载却换成浏览器 UA，等于自证非客户端，
        /// 更容易被判定为盗链而降速或 412。统一为客户端 UA 才是完整闭环。
        /// 版本号比 QuarkDownloader 的 2.5.20 更新，且与 ApiUa 的 3.14.2 同代，
        /// 避免「取链一个版本、下载另一个版本」的破绽。
        /// </summary>
        public const string DlUa =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) quark-cloud-drive/3.14.2 Chrome/112.0.5615.165 " +
            "Electron/24.1.3.8 Safari/537.36 Channel/pckk_other_ch";

        public const string DefaultFolder = "GeZi";
        public const string LoginCookieKey = "__puus";

        public const string QrLoginGetToken = "https://uop.quark.cn/cas/ajax/getTokenForQrcodeLogin";
        public const string QrLoginGetTicket = "https://uop.quark.cn/cas/ajax/getServiceTicketByQrcodeToken";
        public const string QrLoginScanBase = "https://su.quark.cn/4_eMHBJ";

        /// <summary>
        /// 用 service_ticket 激活登录态的**备用入口**（位于可达的 uop 域）。
        /// `pan.quark.cn/account/info?st=` 是官方主入口，但该域的前置 ALB
        /// 会 RST 掉"过小的 ClientHello"，而 .NET Framework 的 SChannel 不支持 TLS 1.3、
        /// ClientHello 天然偏小 → 在 .NET 里该域**完全不可达**（SChannel / WinINet 均实测被拒）。
        /// 实测发现 `uop.quark.cn/cas/login?st=<TICKET>&lw=scan` 同样接受 service_ticket，
        /// 并下发一整套 `.quark.cn` 域的登录 Cookie（`_UP_*` 系列）；而 `uop.quark.cn`
        /// 在 .NET 里**完全可达**。因此把它作为首选入口，`pan.quark.cn` 仅作补充尝试。
        /// </summary>
        public const string CasLoginUrl = "https://uop.quark.cn/cas/login";

        // ---- 扫码轮询状态码（2026-10-02 实测确认）----
        // 夸克 uop 接口用 status 区分阶段，而不是只靠有没有 service_ticket。
        // 早前 C# 版只判断 ticket 是否非空，导致"等待扫码 / 已扫码待确认 / 已过期"
        // 三种状态全被吞掉，界面一直卡着不动 —— 这才是"扫码好像有问题"的根因。
        /// <summary>未扫码：'Query result is empty' —— 二维码还没被扫。</summary>
        public const long QrStatusWaitingScan = 50004001;
        /// <summary>token 无效或已过期：'Token Not Found' —— 需刷新二维码。</summary>
        public const long QrStatusTokenNotFound = 50004002;
        /// <summary>token 已失效/被替换：'Token Expired' 类 —— 需刷新二维码。</summary>
        public const long QrStatusTokenExpired = 50004003;

        public const int DownloadThreads = 64;

        /// <summary>单次读取字节数（下载缓冲区大小）。</summary>
        public const int DownloadChunk = 256 * 1024;
    }
}
