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

using System.Text;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 迅雷云盘端点与指纹。
    /// <para>
    /// <b>这是三个网盘里最难的一个</b>，原因不是接口复杂，而是风控：
    /// 取链接口要求 <c>x-captcha-token</c> —— 那是极验滑块验证码的产物，
    /// 必须由真人拖动滑块生成。因此本实现只做**半自动**：
    /// 用户在浏览器里过掉验证码后，把 token 粘进来，程序带着它请求。
    /// </para>
    /// <para>
    /// 我们**不实现**任何自动过验证码的方案。那既不可靠，也是在帮用户绕过
    /// 服务方的安全机制 —— 这类代码不会出现在本项目里。
    /// </para>
    /// </summary>
    public static class XunleiConstants
    {
        /// <summary>业务 API 主机。</summary>
        public const string ApiBase = "https://api-pan.xunlei.com";

        public const string Referer = "https://pan.xunlei.com/";
        public const string Origin = "https://pan.xunlei.com";

        /// <summary>网页版 UA。</summary>
        public const string ApiUa =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        public const string DlUa = ApiUa;

        /// <summary>
        /// 客户端版本号，作为 <c>X-Client-Version</c> 请求头发给 pan 接口。
        /// <para>
        /// 取值来自 refs/YunX 的 <c>XunleiApi.panRequest</c>（那里固定写 8.31.0.9726），
        /// 与 <see cref="AppClientId"/> 那套 App 客户端版本一致。
        /// 迅雷会拿它和 client_id 交叉校验，缺失时部分接口直接报参数缺失。
        /// </para>
        /// </summary>
        public const string ClientVersion = "8.31.0.9726";

        /// <summary>验证码 token 请求头（极验滑块产物，必须由用户提供）。</summary>
        public const string CaptchaTokenHeader = "x-captcha-token";

        /// <summary>登录态请求头（迅雷网页版用 Authorization: Bearer &lt;pan_token&gt;）。</summary>
        public const string AuthorizationHeader = "Authorization";

        /// <summary>列目录默认页大小。</summary>
        public const int PageSize = 100;

        /// <summary>
        /// 设备标识参数名。
        /// <para>
        /// <b>实测确认必需：</b>不带它请求 <c>/drive/v1/share</c>，服务端返回
        /// <c>{"error":"invalid_argument","detail":"device_id is empty"}</c>。
        /// 带上之后错误推进到 <c>captcha_invalid</c>（验证码无效）——
        /// 说明参数链路上 <c>device_id</c> 在 <c>captcha_token</c> 之前校验。
        /// </para>
        /// </summary>
        public const string DeviceIdParam = "device_id";

        /// <summary>
        /// 客户端 ID（迅雷 API 常与 device_id 一起出现）。
        /// </summary>
        public const string ClientIdParam = "client_id";

        /// <summary>
        /// 网页版（pan.xunlei.com）当前使用的 client_id。
        /// <para>
        /// 从站点自己的打包产物里取得：<c>_nuxt/dist/client/utils-initial.*.js</c>
        /// 里有 <c>{"pan.xunlei.com":{clientId:"Xqp0kJBXWhwaTpB6",…}}</c> 的
        /// 按 host 分支表（<c>pan.xlpan.com</c> 用 <c>XseEZG9_7KHMCmS1</c>，
        /// 那是另一个域名，不要混用）。
        /// </para>
        /// <para>
        /// 早先用的是 <c>Xp6vsxz_7IYVw2BB</c>，它在现网产物里已经**完全不存在**
        /// 了。令牌本身能读到、但带上过时的 client_id 去调
        /// <c>/drive/v1/about</c> 会被服务端判为无效，表现就是登录窗口一直卡在
        /// "pan_token 无效或已过期"。client_id 必须与签发令牌的那个客户端一致。
        /// </para>
        /// </summary>
        public const string ClientId = "Xqp0kJBXWhwaTpB6";

        // ---------------- 账号登录（手机号 + 短信 / 密码） ----------------
        // 上面那个 ClientId 是**网页版**的，只能配网页版签发的 pan_token。
        // 账号登录走的是安卓客户端协议，client_id / secret / 版本号必须换成下面这套，
        // 两套混用会被判无效（与上面的注释同一个坑）。

        /// <summary>迅雷账号服务主机（登录、短信、令牌刷新都在这里）。</summary>
        public const string AuthBase = "https://xluser-ssl.xunlei.com";

        /// <summary>安卓客户端 client_id（账号登录专用）。</summary>
        public const string AppClientId = "Xp6vsxz_7IYVw2BB";

        /// <summary>安卓客户端 client_secret（账号登录专用）。</summary>
        public const string AppClientSecret = "Xp6vsy4tN9toTVdMSpomVdXpRmES";

        /// <summary>安卓客户端版本号，参与 captcha_sign 计算，不能随意改。</summary>
        public const string AppVersion = "8.31.0.9726";

        /// <summary>安卓客户端包名，参与设备签名与 captcha_sign 计算。</summary>
        public const string AppPackageName = "com.xunlei.downloadprovider";

        /// <summary>设备签名里固定的盐（与包名一起参与 sha1）。</summary>
        public const string DeviceSignSalt = "4034a062aaa22f906fca4fefe9fb3a3021";

        /// <summary>账号登录请求的 UA（与 sdkVersion 一起构成客户端指纹）。</summary>
        public const string AppUa =
            "ANDROID-com.xunlei.downloadprovider/8.31.0.9726 netWorkType/5G appid/40 " +
            "deviceName/Xiaomi_M2004j7ac deviceModel/M2004J7AC OSVersion/12 " +
            "protocolVersion/301 platformVersion/10 sdkVersion/512000 " +
            "Oauth2Client/0.9 (Linux 4_14_186-perf-gddfs8vbb238b) (JAVA 0)";

        /// <summary>短信验证码登录用的 sdkVersion。</summary>
        public const string SmsSdkVersion = "231500";

        /// <summary>密码登录用的 sdkVersion。</summary>
        public const string PasswordSdkVersion = "513006";

        // ---------------- 网页令牌刷新（扫码 / 网页登录那套） ----------------
        // 网页登录签发的 refresh_token 只能用网页 client_id 刷新，且**不带 client_secret**。
        // 请求形态也与账号登录不同：JSON body + 桌面 UA + Origin/Referer + client hints。
        // 对照：refs/YunX/app/src/main/kotlin/com/yunx/app/data/network/XunleiWebCredential.kt

        /// <summary>网页版 client_id（与上面的 <see cref="ClientId"/> 同值，语义上单列出来）。</summary>
        public const string WebClientId = ClientId;

        /// <summary>网页令牌刷新用的桌面 UA（必须是桌面浏览器，不能是安卓客户端）。</summary>
        public const string DesktopUa =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        /// <summary>
        /// 与 <see cref="DesktopUa"/> 配套的 client hints。
        /// <para>
        /// 只改 UA 而不同步这三个头，服务端会按移动端判定，
        /// 网页令牌刷新会被直接拒绝（表现为「扫码登录完没多久就过期」）。
        /// </para>
        /// </summary>
        public const string DesktopSecChUa =
            "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"";

        /// <summary>桌面浏览器固定为 <c>?0</c>（不是移动端）。</summary>
        public const string DesktopSecChUaMobile = "?0";

        /// <summary>桌面平台标识。</summary>
        public const string DesktopSecChUaPlatform = "\"Windows\"";

        /// <summary>
        /// 把字符串转义成可以安全嵌进 JSON 字面量的形式。
        /// <para>
        /// 只为拼 refresh_token 这一个字段服务，所以只处理会破坏 JSON 的字符；
        /// 令牌本身是 base64url / JWT 字符集，正常路径下不会有需要转义的字符。
        /// </para>
        /// </summary>
        public static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            var sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// captcha_sign 的 10 个盐，**顺序不能变** —— 它是逐轮链式 md5，
        /// 换序结果完全不同。来自安卓客户端的登录流程产物。
        /// </summary>
        public static readonly string[] CaptchaSalts =
        {
            "9uJNVj/wLmdwKrJaVj/omlQ",
            "Oz64Lp0GigmChHMf/6TNfxx7O9PyopcczMsnf",
            "Eb+L7Ce+Ej48u",
            "jKY0",
            "ASr0zCl6v8W4aidjPK5KHd1Lq3t+vBFf41dqv5+fnOd",
            "wQlozdg6r1qxh0eRmt3QgNXOvSZO6q/GXK",
            "gmirk+ciAvIgA/cxUUCema47jr/YToixTT+Q6O",
            "5IiCoM9B1/788ntB",
            "P07JH0h6qoM6TSUAK2aL9T5s2QBVeY9JWvalf",
            "+oK0AN",
        };
    }
}