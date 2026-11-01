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

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using GeZi.Core.Support;
using GeZi.Core.Api;
using GeZi.Core.Models;

namespace GeZi.Providers.Shared
{
    /// <summary>
    /// 专打 **<c>pan.quark.cn</c>** 的那几件事（2026-10-11 新增，取代原来的 Python 桥）。
    /// `pan.quark.cn` 的前置 ALB 按 **TLS ClientHello 指纹**过滤，
    /// .NET 的 SChannel 会被直接 RST —— 而：
    ///   · **扫码登录的"换登录态"**（<c>account/info?st=&lt;ticket&gt;</c>）只挂在这个域上，
    ///     `__pus` / `__puus` 只有它能下发（实测：可达域 uop 在真票据下也只给 `_UP_*`，那是游客态）；
    ///   · **账号昵称 / 头像** 也只在这个域上。
    /// 所以这两个功能必须走一条"能过那道墙"的通道。
    /// <see cref="OpenSslTls"/> —— 直接 P/Invoke 项目固化的 **OpenSSL 3.5.7**
    /// （<c>lib/OpenSSL/</c>）。实测只有它能过（SChannel ❌ / OpenSSL 3.0.18 ❌ / BouncyCastle ❌）。
    /// 老实现是"起一个内置 CPython 子进程跑 quark_cookie_helper.py"。
    /// 代价：包体积 +11MB，而且带着「解释器 + 脚本 + 子进程」这组特征 —— 实测被杀软
    /// （BitDefender 系）报成 `Gen:Variant.Yogi`。改成 P/Invoke 后 **TLS 指纹完全一致**，
    /// 但不再需要解释器/脚本/子进程。
    /// ⚠️ 本类**只负责这两件事**；业务接口一律走 <see cref="QuarkClient"/>（HttpClient/SChannel）。
    /// </summary>
    public static class PanAccountBridge
    {
        private const string Referer = "https://pan.quark.cn/";

        /// <summary>与老 helper 脚本一致的"驱动接口"UA（补全 Cookie 时用）。</summary>
        private const string DriveUa =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "Chrome/124.0 Safari/537.36";

        /// <summary>补全 Cookie 用的三个接口（顺序与老 helper 一致，别随意改）。</summary>
        private static readonly string[] EnrichUrls =
        {
            "https://pan.quark.cn/list",
            "https://drive-pc.quark.cn/1/clouddrive/file/sort"
                + "?pr=ucpro&fr=pc&uc_param_str=&pdir_fid=0&_page=1&_size=50"
                + "&_fetch_total=1&_sort=file_type:asc,updated_at:desc",
            "https://drive.quark.cn/1/clouddrive/member"
                + "?pr=ucpro&fr=pc&uc_param_str=&fetch_subscribe=true",
        };

        /// <summary>通道诊断信息（DLL 版本 / 失败原因），用于日志。</summary>
        public static string Diagnostics
        {
            get
            {
                return OpenSslTls.IsAvailable
                    ? "OpenSSL " + OpenSslTls.VersionString
                    : "不可用：" + OpenSslTls.ProbeError;
            }
        }

        /// <summary>一次换 Cookie 的结果。</summary>
        public struct ExchangeOutcome
        {
            /// <summary>为 true 表示拿到了登录态（`__pus` / `__puus`）。</summary>
            public bool Ok;
            /// <summary>完整 Cookie 串（失败时可能是部分 Cookie）。</summary>
            public string Cookie;
            /// <summary>账号昵称（成功时通常有）。</summary>
            public string Nickname;
            /// <summary>失败原因（面向用户，简短）。</summary>
            public string Error;
            /// <summary>诊断明细（面向排查）。</summary>
            public string Detail;
        }

        /// <summary>账号信息（昵称 + 头像）的查询结果。</summary>
        public struct AccountInfo
        {
            public bool Ok;
            /// <summary>昵称。实测来自 <c>data.nickname</c>。</summary>
            public string Nickname;
            /// <summary>头像地址。实测来自 <c>data.avatarUri</c>（形如 http://image.quark.cn/…）。</summary>
            public string Avatar;
            public string Error;
        }

        // ---------------- 账号昵称 / 头像 ----------------

        /// <summary>
        /// 取账号昵称 + 头像。通道不可用时返回 <c>null</c>（调用方保持原值即可）。
        /// </summary>
        public static AccountInfo? FetchAccountInfo(string cookie)
        {
            if (string.IsNullOrWhiteSpace(cookie)) return null;
            if (!OpenSslTls.IsAvailable) return null;

            try
            {
                var hdrs = BaseHeaders();
                hdrs["Cookie"] = cookie.Trim();

                var resp = OpenSslTls.Get("pan.quark.cn", "/account/info?fr=pc&platform=pc", hdrs);
                if (resp.StatusCode != 200)
                {
                    return new AccountInfo
                    {
                        Ok = false,
                        Error = "HTTP " + resp.StatusCode,
                    };
                }

                if (IsSuccessFalse(resp.Body))
                {
                    return new AccountInfo
                    {
                        Ok = false,
                        Error = "account/info 返回失败: " + (JsonString(resp.Body, "msg")
                            ?? JsonString(resp.Body, "code") ?? "?"),
                    };
                }

                return new AccountInfo
                {
                    Ok = true,
                    Nickname = (JsonString(resp.Body, "nickname") ?? "").Trim(),
                    Avatar = PickAvatar(resp.Body),
                };
            }
            catch (Exception ex)
            {
                return new AccountInfo
                {
                    Ok = false,
                    Error = ex.GetType().Name + ": " + ex.Message,
                };
            }
        }

        // ---------------- 用 service_ticket 换登录态 ----------------

        /// <summary>
        /// 用 service_ticket 换取登录 Cookie。通道不可用时返回 <c>null</c>
        /// （调用方据此回退到纯 .NET 实现，虽然那条路拿不到登录态）。
        ///   ① `pan.quark.cn/account/info?st=&lt;ticket&gt;&lw=scan` —— 下发 `__pus` 等；
        ///   ② 依次请求 <see cref="EnrichUrls"/> 补全 `__puus` 等；
        ///   ③ 汇总所有 `*.quark.cn` 域的 Cookie，检查关键登录态。
        /// </summary>
        public static ExchangeOutcome? Exchange(string ticket)
        {
            if (string.IsNullOrWhiteSpace(ticket))
                return new ExchangeOutcome { Ok = false, Error = "service_ticket 为空" };

            if (!OpenSslTls.IsAvailable)
                return null;   // 明确告知调用方：这条路走不了

            var problems = new List<string>();
            var jar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string nickname = "";

            // ---- ① 主入口：用票据激活登录态（同时能拿到昵称）----
            try
            {
                var hdrs = BaseHeaders();
                hdrs["Origin"] = Referer.TrimEnd('/');
                hdrs["Accept"] = "text/html,application/xhtml+xml,*/*;q=0.8";
                string path = "/account/info?st=" + Uri.EscapeDataString(ticket) + "&lw=scan";

                var resp = OpenStuck("pan.quark.cn", path, hdrs, jar);
                if (resp != null)
                {
                    if (resp.StatusCode != 200)
                        problems.Add("account/info HTTP " + resp.StatusCode);
                    else if (IsSuccessFalse(resp.Body))
                        problems.Add("account/info 返回失败: " + (JsonString(resp.Body, "msg")
                            ?? JsonString(resp.Body, "code") ?? "?"));
                    else
                        nickname = (JsonString(resp.Body, "nickname") ?? "").Trim();
                }
            }
            catch (Exception ex)
            {
                problems.Add("account/info 连接失败: " + ex.GetType().Name + ": " + ex.Message);
            }

            // ---- ② 补全：网盘/下载接口会带上 __puus 等 ----
            foreach (var u in EnrichUrls)
            {
                try
                {
                    var uri = new Uri(u);
                    var hdrs = BaseHeaders();
                    hdrs["User-Agent"] = DriveUa;
                    hdrs["Origin"] = Referer.TrimEnd('/');
                    OpenStuck(uri.Host, uri.PathAndQuery, hdrs, jar);
                }
                catch (Exception ex)
                {
                    problems.Add(new Uri(u).Host + " 失败: " + ex.GetType().Name);
                }
            }

            // ---- ③ 汇总 ----
            var sb = new StringBuilder();
            foreach (var kv in jar)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            string cookie = sb.ToString();

            bool hasLogin = jar.ContainsKey("__pus") || jar.ContainsKey("__puus");
            if (hasLogin)
                return new ExchangeOutcome { Ok = true, Cookie = cookie, Nickname = nickname };

            var names = new List<string>(jar.Keys);
            names.Sort();
            return new ExchangeOutcome
            {
                Ok = false,
                Cookie = cookie,
                Error = "未取回关键登录态(__pus/__puus)",
                Detail = (problems.Count > 0 ? string.Join(" | ", problems) + " | " : "")
                         + "拿到 " + names.Count + " 个 Cookie：" + string.Join(",", names),
            };
        }

        /// <summary>发一次请求，并把响应里的 Set-Cookie 合并进 <paramref name="jar"/>。</summary>
        private static OpenSslTls.Response OpenStuck(string host, string pathAndQuery,
            IDictionary<string, string> headers, IDictionary<string, string> jar)
        {
            // 带上已经攒到的 Cookie（与老 helper 的 CookieJar 行为一致）
            if (jar.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var kv in jar)
                {
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(kv.Key).Append('=').Append(kv.Value);
                }
                headers["Cookie"] = sb.ToString();
            }

            var resp = OpenSslTls.Get(host, pathAndQuery, headers);
            foreach (var c in resp.Cookies)
                jar[c.Key] = c.Value;
            return resp;
        }

        // ---------------- 小工具 ----------------

        private static Dictionary<string, string> BaseHeaders()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "User-Agent", QuarkConstants.DlUa },
                { "Referer", Referer },
                { "Accept", "application/json, text/plain, */*" },
                { "Accept-Language", "zh-CN,zh;q=0.9" },
            };
        }

        private static bool IsSuccessFalse(string body)
        {
            return !string.IsNullOrEmpty(body)
                && Regex.IsMatch(body, "\"success\"\\s*:\\s*false", RegexOptions.IgnoreCase);
        }

        /// <summary>从 JSON 文本里取一个字符串字段（够用即可，不引入 JSON 库）。</summary>
        private static string JsonString(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return null;
            var m = Regex.Match(body, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>头像字段实测叫 <c>avatarUri</c>，顺带兼容几个常见别名。</summary>
        private static string PickAvatar(string body)
        {
            foreach (var k in new[] { "avatarUri", "avatar", "avatarUrl", "avatar_url" })
            {
                string v = JsonString(body, k);
                if (!string.IsNullOrEmpty(v) && v.Trim().Length > 0)
                    return v.Trim();
            }
            return "";
        }
    }
}
