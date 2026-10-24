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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Support;

namespace GeZi.Core.Api
{
    /// <summary>
    /// 迅雷账号登录结果。
    /// </summary>
    public sealed class XunleiLoginResult
    {
        /// <summary>业务访问令牌（<c>api-pan.xunlei.com</c> 的 Authorization）。</summary>
        public string AccessToken = "";

        /// <summary>刷新令牌 —— 有它就能自动续期，不用天天重新登录。</summary>
        public string RefreshToken = "";

        /// <summary>昵称（登录响应里带的，可能为空）。</summary>
        public string NickName = "";

        /// <summary>账号 id。</summary>
        public string UserId = "";

        public bool Ok
        {
            get { return !string.IsNullOrEmpty(AccessToken); }
        }
    }

    /// <summary>
    /// 迅雷账号登录（手机号 + 短信验证码 / 账号密码）。
    /// <para>
    /// <b>为什么要有这条路：</b>网页版抓 Cookie 只能拿到 <c>pan_token</c>，
    /// 它两小时就过期且**没有续期凭据** —— 用户每天都要重新登录。
    /// 账号登录能拿到 <c>refresh_token</c>，可以长期续期。
    /// </para>
    /// <para>
    /// 协议来自安卓客户端，对照实现：
    /// <c>refs/wenxi/lib/data/providers/xunlei_login.dart</c>。
    /// 流程是两段式的：先拿 <c>sessionID</c>（账号密码/短信验证），
    /// 再用它换业务令牌 —— 中间必须过一次 <c>captcha/init</c>，
    /// 否则 <c>auth/signin/token</c> 会以验证码无效拒绝。
    /// </para>
    /// <para>
    /// <b>本类不实现任何自动过验证码的逻辑。</b>如果服务端要求滑块/安全验证，
    /// 会如实抛出 <see cref="XunleiVerificationRequiredException"/>，由界面引导用户
    /// 在官方页面完成验证。
    /// </para>
    /// </summary>
    public sealed class XunleiLoginService : IDisposable
    {
        private readonly HttpClient _http;
        private readonly HttpClientHandler _handler;
        private readonly string _authBase;

        private string _deviceId;
        private string _creditKey = "";
        private string _smsToken = "";
        private bool _disposed;

        /// <summary>最近一次失败原因（成功时为空串）。</summary>
        public string LastError { get; private set; }

        /// <summary>设备标识（32 位 hex）。持久化后下次复用，避免被当成新设备触发风控。</summary>
        public string DeviceId
        {
            get { return _deviceId; }
        }

        /// <param name="deviceId">
        /// 已保存的设备标识；传空则新建一个。**务必持久化并复用** ——
        /// 每次登录都换设备会让服务端判定异常。
        /// </param>
        /// <param name="authBase">登录服务根地址（测试时可指向本地 mock）。</param>
        public XunleiLoginService(string deviceId = null, string authBase = null)
        {
            _deviceId = XunleiProtocol.IsIdentifier(deviceId)
                ? deviceId
                : XunleiProtocol.NewIdentifier();
            _authBase = string.IsNullOrEmpty(authBase)
                ? XunleiConstants.AuthBase
                : authBase.TrimEnd('/');
            LastError = "";

            _handler = new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                MaxConnectionsPerServer = 8,
            };
            NetworkOptions.ApplyTo(_handler);
            _http = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(30) };
        }

        /// <summary>
        /// 手机号格式检查：中国大陆 11 位（1 开头），允许带 <c>+86</c> / <c>86</c> 前缀。
        /// <para>
        /// 为什么卡这么死：短信接口有频率限制，把明显不是手机号的字符串放过去，
        /// 会白白消耗一次配额，而且服务端回的错误码和"号码格式错"完全无关，
        /// 用户根本不知道是自己少打了几位。宁可在本地先拦住。
        /// </para>
        /// </summary>
        public static bool IsMobile(string value)
        {
            string digits = NormalizeMobile(value);
            if (digits.Length != 11 || digits[0] != '1')
                return false;

            foreach (var c in digits)
            {
                if (c < '0' || c > '9')
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 去掉空格与 <c>+86</c> / <c>86</c> 国家码，返回纯数字串。
        /// 服务端要的是裸号码，带国家码会被判成无效号码。
        /// </summary>
        public static string NormalizeMobile(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            var sb = new System.Text.StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (c >= '0' && c <= '9')
                    sb.Append(c);
            }

            string digits = sb.ToString();
            if (digits.Length == 13 && digits.StartsWith("86", StringComparison.Ordinal))
                digits = digits.Substring(2);
            return digits;
        }

        /// <summary>
        /// 第一步（短信）：给手机号发验证码。
        /// 成功返回 true，并把 <c>creditkey</c>/<c>token</c> 暂存在实例里，
        /// 供 <see cref="VerifySmsAsync"/> 使用。
        /// </summary>
        public async Task<bool> SendSmsAsync(string mobile, CancellationToken cancel = default)
        {
            LastError = "";
            _creditKey = "";
            _smsToken = "";

            if (!IsMobile(mobile))
            {
                LastError = "请输入正确的手机号（11 位，1 开头）。";
                return false;
            }

            // 服务端要裸号码；用户填了 +86 前缀的话在这里剥掉。
            string mobileDigits = NormalizeMobile(mobile);

            var body = XunleiProtocol.BaseBody(_deviceId, XunleiConstants.SmsSdkVersion);
            body["mobile"] = mobileDigits;
            body["register"] = "0";

            object res = await PostAsync("/xluser.core.login/v3/sendsms", body,
                "android-ok-http-client/xl-acc-sdk/version-5.0.12.512000", null, cancel)
                .ConfigureAwait(false);

            string errorCode = PanJson.AsString(PanJson.Get(res, "errorCode")) ?? "";
            string error = PanJson.AsString(PanJson.Get(res, "error")) ?? "";
            string creditKey = PanJson.AsString(PanJson.Get(res, "creditkey")) ?? "";

            if (!(errorCode.Length == 0 || errorCode == "0") || !(error.Length == 0 || error == "success"))
            {
                LastError = Describe(res, "短信发送失败，请稍后重试。");
                return false;
            }
            if (creditKey.Length == 0)
            {
                LastError = Describe(res, "短信发送失败（服务端未返回 creditkey）。");
                return false;
            }

            _creditKey = creditKey;
            _smsToken = PanJson.AsString(PanJson.Get(res, "token")) ?? "";
            // 记下手机号 —— 下一步 smslogin 必须带同一个，否则服务端判"验证已失效"。
            _pendingMobile = mobileDigits;
            return true;
        }

        /// <summary>第一步（短信）：用验证码完成账号验证并换取业务令牌。</summary>
        public async Task<XunleiLoginResult> VerifySmsAsync(string code, CancellationToken cancel = default)
        {
            LastError = "";

            string smsCode = (code ?? "").Trim();
            if (smsCode.Length < 4 || smsCode.Length > 8)
            {
                LastError = "请输入正确的短信验证码。";
                return null;
            }
            if (_creditKey.Length == 0)
            {
                LastError = "验证已失效，请重新发送验证码。";
                return null;
            }

            var body = XunleiProtocol.BaseBody(_deviceId, XunleiConstants.SmsSdkVersion, _creditKey);
            body["mobile"] = _pendingMobile ?? "";
            body["smsCode"] = smsCode;
            body["token"] = _smsToken;
            body["register"] = "0";

            object res = await PostAsync("/xluser.core.login/v3/smslogin", body,
                "android-ok-http-client/xl-acc-sdk/version-5.0.12.512000", null, cancel)
                .ConfigureAwait(false);

            return await FinishAsync(res, _pendingMobile ?? "", cancel).ConfigureAwait(false);
        }

        /// <summary>待验证的手机号（sendSms 与 smslogin 必须一致）。</summary>
        private string _pendingMobile = "";

        /// <summary>
        /// 账号密码登录（一步到位）。
        /// <para>部分账号会被要求安全验证，此时返回 null 并设置 <see cref="LastError"/>。</para>
        /// </summary>
        public async Task<XunleiLoginResult> PasswordAsync(string userName, string password,
            CancellationToken cancel = default)
        {
            LastError = "";

            string account = (userName ?? "").Trim();
            if (account.Length == 0 || string.IsNullOrEmpty(password))
            {
                LastError = "请输入账号和密码。";
                return null;
            }

            var body = XunleiProtocol.BaseBody(_deviceId, XunleiConstants.PasswordSdkVersion);
            body["userName"] = account;
            body["passWord"] = password;
            body["verifyKey"] = "";
            body["verifyCode"] = "";
            body["isMd5Pwd"] = "0";

            object res = await PostAsync("/xluser.core.login/v3/login", body,
                "android-ok-http-client/xl-acc-sdk/version-5.0.13.513006", null, cancel)
                .ConfigureAwait(false);

            return await FinishAsync(res, account, cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// 用 refresh_token 换新的 access_token。返回 null 表示刷新失败（需重新登录）。
        /// </summary>
        public async Task<XunleiLoginResult> RefreshAsync(string refreshToken,
            CancellationToken cancel = default)
        {
            LastError = "";
            if (string.IsNullOrEmpty(refreshToken))
            {
                LastError = "缺少 refresh_token，请重新登录。";
                return null;
            }

            using (var req = new HttpRequestMessage(HttpMethod.Post,
                _authBase + "/v1/auth/token"))
            {
                req.Headers.TryAddWithoutValidation("User-Agent", XunleiConstants.AppUa);
                req.Headers.TryAddWithoutValidation("Accept", "application/json;charset=UTF-8");
                req.Headers.TryAddWithoutValidation("X-Client-Id", XunleiConstants.AppClientId);
                req.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);
                req.Headers.TryAddWithoutValidation("X-Client-Version", XunleiConstants.AppVersion);
                req.Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client_id", XunleiConstants.AppClientId),
                    new KeyValuePair<string, string>("client_secret", XunleiConstants.AppClientSecret),
                    new KeyValuePair<string, string>("grant_type", "refresh_token"),
                    new KeyValuePair<string, string>("refresh_token", refreshToken),
                });

                using (var resp = await _http.SendAsync(req, cancel).ConfigureAwait(false))
                {
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        LastError = "刷新迅雷令牌失败（HTTP " + (int)resp.StatusCode + "）："
                                    + Truncate(text, 160);
                        return null;
                    }

                    var obj = PanJson.Deserialize(text);
                    if (obj == null)
                    {
                        LastError = "刷新迅雷令牌失败：响应无法解析。";
                        return null;
                    }

                    var result = new XunleiLoginResult
                    {
                        AccessToken = PanJson.AsString(PanJson.Get(obj, "access_token"))
                                      ?? PanJson.AsString(PanJson.Get(obj, "accessToken")) ?? "",
                        RefreshToken = PanJson.AsString(PanJson.Get(obj, "refresh_token"))
                                       ?? PanJson.AsString(PanJson.Get(obj, "refreshToken")) ?? "",
                    };
                    if (!result.Ok)
                    {
                        LastError = Describe(obj, "刷新迅雷令牌失败。");
                        return null;
                    }
                    if (result.RefreshToken.Length == 0)
                        result.RefreshToken = refreshToken;
                    return result;
                }
            }
        }

        /// <summary>
        /// 用**网页登录**的 refresh_token 换新令牌。
        /// <para>
        /// 网页 token 与 App token 不是同一套 OAuth 客户端，请求形态完全不同：
        /// 网页这条是 JSON body + 桌面 UA + Origin/Referer，而且
        /// <b>必须不带 client_secret</b>；带成 App 那套会被直接拒绝，
        /// 表现就是「扫码登录完没多久就提示过期」。
        /// </para>
        /// <para>
        /// 对照实现：<c>refs/YunX/app/src/main/kotlin/com/yunx/app/data/network/XunleiApi.kt</c>
        /// 的 <c>refreshToken</c>（<c>web</c> 分支）。
        /// </para>
        /// </summary>
        public async Task<XunleiLoginResult> RefreshWebAsync(string refreshToken,
            string deviceId, CancellationToken cancel = default)
        {
            LastError = "";
            if (string.IsNullOrEmpty(refreshToken))
            {
                LastError = "缺少网页登录的 refresh_token，请重新扫码登录。";
                return null;
            }

            string dev = string.IsNullOrEmpty(deviceId) ? _deviceId : deviceId;

            using (var req = new HttpRequestMessage(HttpMethod.Post,
                _authBase + "/v1/auth/token"))
            {
                // 桌面 UA 必须与这套 client hints 一致：只改 UA 不同步
                // Sec-CH-UA 会被判成移动端，进而拒绝网页令牌的刷新。
                req.Headers.TryAddWithoutValidation("User-Agent", XunleiConstants.DesktopUa);
                req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                req.Headers.TryAddWithoutValidation("Origin", "https://pan.xunlei.com");
                req.Headers.TryAddWithoutValidation("Referer", "https://pan.xunlei.com/");
                req.Headers.TryAddWithoutValidation("X-Client-Id", XunleiConstants.WebClientId);
                if (!string.IsNullOrEmpty(dev))
                    req.Headers.TryAddWithoutValidation("X-Device-Id", dev);
                req.Headers.TryAddWithoutValidation("Sec-Ch-Ua", XunleiConstants.DesktopSecChUa);
                req.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Mobile", XunleiConstants.DesktopSecChUaMobile);
                req.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Platform", XunleiConstants.DesktopSecChUaPlatform);

                string body = "{\"grant_type\":\"refresh_token\",\"client_id\":\""
                              + XunleiConstants.WebClientId + "\",\"refresh_token\":\""
                              + XunleiConstants.EscapeJson(refreshToken) + "\"}";
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using (var resp = await _http.SendAsync(req, cancel).ConfigureAwait(false))
                {
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        LastError = "刷新迅雷网页令牌失败（HTTP " + (int)resp.StatusCode + "）："
                                    + Truncate(text, 160);
                        return null;
                    }

                    var obj = PanJson.Deserialize(text);
                    if (obj == null)
                    {
                        LastError = "刷新迅雷网页令牌失败：响应无法解析。";
                        return null;
                    }

                    var result = new XunleiLoginResult
                    {
                        AccessToken = PanJson.AsString(PanJson.Get(obj, "access_token"))
                                      ?? PanJson.AsString(PanJson.Get(obj, "accessToken")) ?? "",
                        RefreshToken = PanJson.AsString(PanJson.Get(obj, "refresh_token"))
                                       ?? PanJson.AsString(PanJson.Get(obj, "refreshToken")) ?? "",
                    };
                    if (!result.Ok)
                    {
                        LastError = Describe(obj, "刷新迅雷网页令牌失败。");
                        return null;
                    }
                    if (result.RefreshToken.Length == 0)
                        result.RefreshToken = refreshToken;
                    return result;
                }
            }
        }

        /// <summary>
        /// 第二段：把登录响应里的 <c>sessionID</c> 换成业务令牌。
        /// <para>
        /// 顺序不能颠倒：<c>captcha/init</c> 必须先拿到 <c>captcha_token</c>，
        /// 再作为 <c>X-Captcha-Token</c> 请求头带给 <c>auth/signin/token</c>。
        /// </para>
        /// </summary>
        private async Task<XunleiLoginResult> FinishAsync(object login, string userName,
            CancellationToken cancel)
        {
            string errorCode = PanJson.AsString(PanJson.Get(login, "errorCode")) ?? "";
            string error = PanJson.AsString(PanJson.Get(login, "error")) ?? "";
            string sessionId = PanJson.AsString(PanJson.Get(login, "sessionID")) ?? "";
            string verifyType = PanJson.AsString(PanJson.Get(login, "verifyType")) ?? "";

            if (!(errorCode == "0" || errorCode.Length == 0 || error == "success") || sessionId.Length == 0)
            {
                if (error == "review_panel" || errorCode == "1007" || verifyType.Length > 0)
                {
                    LastError = "迅雷要求安全验证：请在官方页面完成验证后再试。"
                                + Describe(login, "");
                    return null;
                }
                LastError = Describe(login, "登录失败，请检查账号、密码或验证码。");
                return null;
            }

            string userId = PanJson.AsString(PanJson.Get(login, "userID")) ?? "";
            string nickName = PanJson.AsString(PanJson.Get(login, "nickName")) ?? "";

            // ① 验证码初始化
            string timestamp = XunleiProtocol.NowTimestamp();
            var captchaBody = new Dictionary<string, object>
            {
                { "action", "POST:/auth/signin/token" },
                { "captcha_token", "" },
                { "client_id", XunleiConstants.AppClientId },
                { "device_id", _deviceId },
                { "meta", new Dictionary<string, object>
                    {
                        { "username", userName ?? "" },
                        { "client_version", XunleiConstants.AppVersion },
                        { "package_name", XunleiConstants.AppPackageName },
                        { "timestamp", timestamp },
                        { "captcha_sign", XunleiProtocol.CaptchaSign(_deviceId, timestamp) },
                        { "user_id", userId },
                    }
                },
                { "redirect_uri", "xlaccsdk01://xunlei.com/callback?state=harbor" },
            };

            object captchaRes = await PostAsync("/v1/shield/captcha/init", captchaBody,
                XunleiConstants.AppUa, null, cancel).ConfigureAwait(false);
            string captchaToken = PanJson.AsString(PanJson.Get(captchaRes, "captcha_token")) ?? "";
            if (captchaToken.Length == 0)
            {
                LastError = Describe(captchaRes, "安全验证初始化失败，请重试。");
                return null;
            }

            // ② 用 sessionID + captcha_token 换业务令牌
            var tokenBody = new Dictionary<string, object>
            {
                { "client_id", XunleiConstants.AppClientId },
                { "client_secret", XunleiConstants.AppClientSecret },
                { "provider", "access_end_point_token" },
                { "signin_token", sessionId },
            };

            object tokenRes = await PostAsync("/v1/auth/signin/token", tokenBody,
                XunleiConstants.AppUa, captchaToken, cancel).ConfigureAwait(false);

            string access = PanJson.AsString(PanJson.Get(tokenRes, "access_token"))
                            ?? PanJson.AsString(PanJson.Get(tokenRes, "accessToken")) ?? "";
            string refresh = PanJson.AsString(PanJson.Get(tokenRes, "refresh_token"))
                             ?? PanJson.AsString(PanJson.Get(tokenRes, "refreshToken")) ?? "";

            if (access.Length == 0)
            {
                LastError = Describe(tokenRes, "登录凭证获取失败，请重新登录。");
                return null;
            }

            return new XunleiLoginResult
            {
                AccessToken = access,
                RefreshToken = refresh,
                NickName = nickName,
                UserId = userId,
            };
        }

        // ---------------- HTTP ----------------

        private async Task<object> PostAsync(string path, Dictionary<string, object> body,
            string agent, string captchaToken, CancellationToken cancel)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, _authBase + path))
            {
                req.Headers.TryAddWithoutValidation("User-Agent", agent);
                req.Headers.TryAddWithoutValidation("Accept", "application/json;charset=UTF-8");
                req.Headers.TryAddWithoutValidation("X-Client-Id", XunleiConstants.AppClientId);
                req.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);
                req.Headers.TryAddWithoutValidation("X-Client-Version", XunleiConstants.AppVersion);
                if (!string.IsNullOrEmpty(captchaToken))
                    req.Headers.TryAddWithoutValidation("X-Captcha-Token", captchaToken);

                req.Content = new StringContent(PanJson.Serialize(body),
                    System.Text.Encoding.UTF8, "application/json");

                using (var resp = await _http.SendAsync(req, cancel).ConfigureAwait(false))
                {
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    int status = (int)resp.StatusCode;
                    if (status == 408 || status == 429 || status >= 500)
                        throw new QuarkApiException("迅雷登录服务暂不可用，请稍后重试（HTTP "
                                                    + status + "）。");

                    var obj = PanJson.Deserialize(text);
                    if (obj == null)
                    {
                        if (!resp.IsSuccessStatusCode)
                            throw new QuarkApiException("迅雷登录请求失败（HTTP " + status + "）："
                                                        + Truncate(text, 160));
                        throw new QuarkApiException("迅雷登录响应无法解析：" + Truncate(text, 160));
                    }
                    return obj;
                }
            }
        }

        /// <summary>把服务端的错误字段拼成一句人话。</summary>
        private static string Describe(object res, string fallback)
        {
            string msg = PanJson.AsString(PanJson.Get(res, "errorDesc"))
                         ?? PanJson.AsString(PanJson.Get(res, "error_description"))
                         ?? PanJson.AsString(PanJson.Get(res, "message")) ?? "";
            if (msg.Length > 0)
                return msg;
            return fallback ?? "";
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _http.Dispose(); } catch { }
            try { _handler.Dispose(); } catch { }
        }
    }

    /// <summary>迅雷要求安全验证（滑块/人工审核）时抛出。</summary>
    public sealed class XunleiVerificationRequiredException : Exception
    {
        public XunleiVerificationRequiredException(string message) : base(message) { }
    }
}
