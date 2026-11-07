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
using GeZi.Core.Api;
using GeZi.Core.Models;
using GeZi.Core.Support;
using GeZi.Providers.Uc;

namespace GeZi.Providers.Quark
{
    /// <summary>
    /// 出站请求的会话层：统一请求头指纹、Cookie 载体、节流节奏和连接复用。
    ///
    /// 设计目标不是"伪装成官方客户端"，而是**让请求头完整且前后一致**——
    /// 一个真实的浏览器/网页端请求本来就带这一整套头，缺项或不一致才是异常特征。
    /// 同时统一节流与退避，避免因瞬时高频重试被服务端按可疑流量处理。
    ///
    /// 关键点：
    /// - 所有请求共用同一份浏览器标准头（含 Sec-Fetch-*、Accept-Language 等）
    /// - 请求间加入随机化最小间隔，避免固定频率的机器节奏
    /// - Cookie 始终从 CookieContainer 实时读取，保证 Set-Cookie 更新立即生效
    /// - HttpClient 复用，连接保活，减少 TCP/TLS 握手带来的额外特征
    /// </summary>
    public sealed class QuarkSession : IDisposable
    {
        private readonly HttpClient _http;
        private readonly HttpClientHandler _handler;
        private readonly CookieContainer _jar;
        private readonly SemaphoreSlim _gate;

        // 令牌桶：容量 BurstCapacity，按 MinIntervalMs 的速率补充。
        // 桶满时前 BurstCapacity 个请求可立即放行（批量取链不必每个都干等），
        // 之后仍严格维持「平均每 MinIntervalMs 一个请求」的节奏。
        private double _tokens;
        private DateTime _lastRefillAt = DateTime.MinValue;
        private readonly object _throttleLock = new object();
        private readonly Random _throttleRng = new Random(Guid.NewGuid().GetHashCode());

        /// <summary>两次请求之间的最小间隔（毫秒）。过低会被视为机器流量，过高影响体验。</summary>
        public int MinIntervalMs { get; set; } = 260;

        /// <summary>
        /// 突发容量：桶满时最多允许多少个请求连发而不等待。
        /// 批量取链（一次几十上百个文件）靠它省掉开头那几十次无谓等待；
        /// 不能设太大，否则等于放弃节流。
        /// </summary>
        public int BurstCapacity { get; set; } = 8;

        public HttpClient Http => _http;

        /// <summary>
        /// 这个会话服务的网盘。夸克与 UC 共用同一套 ucpro 协议，
        /// 差别只在域名、UA 与 Cookie 域 —— 用 Kind 一次性切换，
        /// 避免复制两份几乎相同的 HTTP 底座（Cookie 解析、节流、退避重试都只有一份）。
        /// </summary>
        public PanKind Kind { get; private set; }

        public QuarkSession(string cookie = null, DecompressionMethods decompression = DecompressionMethods.None)
            : this(cookie, decompression, PanKind.Quark)
        {
        }

        public QuarkSession(string cookie, DecompressionMethods decompression, PanKind kind)
        {
            Kind = kind;
            _jar = new CookieContainer { Capacity = 500, PerDomainCapacity = 200 };
            if (!string.IsNullOrEmpty(cookie))
                SeedCookies(cookie);

            _handler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = _jar,
                AllowAutoRedirect = true,
                AutomaticDecompression = decompression,
                // 复用连接：保持 TCP/TLS 会话，减少重复握手
                MaxConnectionsPerServer = 128,
            };
            // 代理策略统一交给 NetworkOptions（默认**直连**，理由：
            //   1) 与 Python Flet 版行为一致（urllib 不读系统代理）；
            //   2) 本机代理软件常把请求劫持到 127.0.0.1，导致夸克接口失败；
            //   3) 代理会改变 TLS ClientHello，而 pan.quark.cn 的 WAF
            //      对指纹敏感，走代理可能直接被拒）。
            // 只有用户在设置里显式打开「跟随系统代理」时才读 WinINET 设置 ——
            // 旧版这里写的 "可设 UseSystemProxy = true" 是**没有实现的幽灵开关**，
            // 现已做成真设置项（见 NetworkOptions 与 AppSettings.UseSystemProxy）。
            NetworkOptions.ApplyTo(_handler);
            _http = new HttpClient(_handler) { Timeout = Timeout.InfiniteTimeSpan };
            _gate = new SemaphoreSlim(1, 1);
        }

        // ---------------- Cookie ----------------

        // 夸克与 UC 的 Cookie 域互不重叠：夸克登录态在 .quark.cn，
        // UC 登录态在 .uc.cn（两者用同一个 Cookie 名 __puus，但值不同、不能混用）。
        private static readonly string[] QuarkCookieDomains =
        {
            "https://pan.quark.cn", "https://drive-pc.quark.cn", "https://drive-m.quark.cn",
            "https://drive.quark.cn", "https://drive-h.quark.cn", "https://uop.quark.cn",
            "https://su.quark.cn", "https://quark.cn",
        };

        private static readonly string[] UcCookieDomains =
        {
            // 网页前端
            "https://drive.uc.cn", "https://fast.uc.cn", "https://pc.uc.cn",
            "https://uc.cn", "https://www.uc.cn",
            // API 主机（实测：UC 的业务接口在 pc-api.uc.cn 上）
            "https://pc-api.uc.cn",
        };

        /// <summary>当前网盘对应的 Cookie 域集合。</summary>
        private string[] CookieDomains
        {
            get { return Kind == PanKind.Uc ? UcCookieDomains : QuarkCookieDomains; }
        }

        /// <summary>写入 Cookie 时用的域后缀。</summary>
        private string CookieDomainSuffix
        {
            get { return Kind == PanKind.Uc ? ".uc.cn" : ".quark.cn"; }
        }

        public static Dictionary<string, string> ParseCookies(string s)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(s))
                return d;
            foreach (var part in s.Split(';'))
            {
                var p = part.Trim();
                if (p.Length == 0)
                    continue;
                int eq = p.IndexOf('=');
                if (eq <= 0)
                    continue;
                var k = p.Substring(0, eq).Trim();
                var v = p.Substring(eq + 1).Trim();
                if (k.Length > 0)
                    d[k] = v;
            }
            return d;
        }

        public void SeedCookies(string cookie)
        {
            foreach (var kv in ParseCookies(cookie))
            {
                try
                {
                    _jar.Add(new Cookie(kv.Key, kv.Value, "/", CookieDomainSuffix));
                }
                catch
                {
                    // 单个 Cookie 格式异常不影响整体
                }
            }
        }

        /// <summary>
        /// 读取指定名字的 Cookie 值（在当前网盘的 Cookie 域里找）。
        /// UC 的 <c>ctoken</c>（CSRF token）需要这样取出来放进请求头。
        /// </summary>
        public string GetCookieValue(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            foreach (var d in CookieDomains)
            {
                CookieCollection cookies;
                try
                {
                    cookies = _jar.GetCookies(new Uri(d));
                }
                catch
                {
                    continue;
                }
                foreach (Cookie c in cookies)
                {
                    if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                        return c.Value;
                }
            }
            return null;
        }

        /// <summary>
        /// 删除指定名字的 Cookie（在所有相关域里）。
        /// <para>
        /// 用途：刷新 <c>__puus</c> 时必须先把它摘掉再请求 <c>/config</c>，
        /// 否则服务端看到旧的 <c>__puus</c> 不会重新下发 —— 这是 AList
        /// <c>refreshPuus</c> / YunX <c>withoutPuus</c> 的核心手法。
        /// </para>
        /// </summary>
        public void RemoveCookie(string name)
        {
            if (string.IsNullOrEmpty(name))
                return;
            foreach (var d in CookieDomains)
            {
                CookieCollection cookies;
                try
                {
                    cookies = _jar.GetCookies(new Uri(d));
                }
                catch
                {
                    continue;
                }
                foreach (Cookie c in cookies)
                {
                    if (!string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        // 用一个已过期的同名 Cookie 覆盖，CookieContainer 会把它清掉
                        _jar.Add(new Cookie(c.Name, "", c.Path ?? "/", CookieDomainSuffix)
                        {
                            Expires = DateTime.Now.AddDays(-1),
                        });
                    }
                    catch
                    {
                        // 单个 Cookie 异常不影响整体
                    }
                }
            }
        }

        /// <summary>
        /// 覆盖写入单个 Cookie（同名同域直接替换，没有则新增）。
        /// 用于把刷新拿到的 <c>__puus</c>/<c>__pus</c> 合并回会话。
        /// </summary>
        public void SetCookie(string name, string value)
        {
            if (string.IsNullOrEmpty(name))
                return;
            try
            {
                _jar.Add(new Cookie(name, value ?? "", "/", CookieDomainSuffix));
            }
            catch
            {
                // 值里含非法字符时忽略，不影响已有会话
            }
        }

        /// <summary>按域汇总 Cookie，后者覆盖前者，得到可下发给接口的整串。</summary>
        public string BuildCookieString()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in CookieDomains)
            {
                CookieCollection cookies;
                try
                {
                    cookies = _jar.GetCookies(new Uri(d));
                }
                catch
                {
                    continue;
                }
                foreach (Cookie c in cookies)
                    dict[c.Name] = c.Value;
            }
            var sb = new StringBuilder();
            foreach (var kv in dict)
            {
                if (sb.Length > 0)
                    sb.Append("; ");
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }

        // ---------------- 请求头指纹 ----------------

        /// <summary>
        /// 构造一套完整、自洽的浏览器标准请求头。
        /// 「完整」是重点：真实浏览器不会只带 UA 和 Referer，
        /// 缺项本身就是可疑特征。这里把常规头补齐并保持前后一致。
        /// </summary>
        public static void ApplyHeaders(HttpRequestMessage req, string url, bool api = true)
        {
            var ua = api ? QuarkConstants.ApiUa : QuarkConstants.DlUa;
            Set(req, "User-Agent", ua);
            Set(req, "Accept", api
                ? "application/json, text/plain, */*"
                : "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            Set(req, "Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            Set(req, "Referer", QuarkConstants.Referer);
            Set(req, "Origin", "https://pan.quark.cn");
            Set(req, "Cache-Control", "no-cache");
            Set(req, "Pragma", "no-cache");
            Set(req, "DNT", "1");
            Set(req, "Connection", "keep-alive");

            // Sec-Fetch-* 是现代浏览器必发的一组头，缺失是明显的非浏览器特征
            bool sameOrigin = url != null && url.IndexOf("pan.quark.cn", StringComparison.OrdinalIgnoreCase) >= 0;
            Set(req, "Sec-Fetch-Dest", api ? "empty" : "document");
            Set(req, "Sec-Fetch-Mode", api ? "cors" : "navigate");
            Set(req, "Sec-Fetch-Site", sameOrigin ? "same-origin" : "same-site");
        }

        /// <summary>
        /// 按 <see cref="Kind"/> 注入对应网盘的请求头。
        ///
        /// <para>
        /// 🚨 2026-11-05：改成从 <see cref="PanClientProfile"/> 统一取值 ——
        /// 下载数据面（FusionDownloader）也从同一份档案取头，
        /// 这样「取链时的客户端身份」与「实际下载时的身份」逐字一致。
        /// 原来这里额外写了 Sec-Fetch-* / Accept-Language，下载层却一个都没有。
        /// </para>
        /// </summary>
        public void ApplyHeadersForKind(HttpRequestMessage req, string url, bool api = true)
        {
            var p = PanClientProfile.For(Kind);
            bool webDomain = IsUcWebDomain(url);
            Set(req, "User-Agent", UcUa(url, api, p));
            Set(req, "Accept", api
                ? "application/json, text/plain, */*"
                : "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            Set(req, "Referer", p.Referer);
            Set(req, "Origin", p.Origin);
            Set(req, "DNT", "1");
            Set(req, "Connection", "keep-alive");

            foreach (var kv in p.CommonHeaders)
            {
                if (p.ForbiddenHeaders.Contains(kv.Key)) continue;
                // 网页域账号接口用普通 Chrome UA，不能同时带 UC 客户端的 Sec-Ch-Ua：
                // 两者对不上反而更像伪造。这条接口按 UA 分身份（见 WebUa 的注释）。
                if (webDomain && kv.Key.Equals("Sec-Ch-Ua", StringComparison.OrdinalIgnoreCase)) continue;
                Set(req, kv.Key, kv.Value);
            }
        }

        /// <summary>UC 的网页域（drive.uc.cn）账号接口，走的是另一套网页身份。</summary>
        private static bool IsUcWebDomain(string url)
        {
            return url != null && url.IndexOf("drive.uc.cn", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// UC 的两个域认不同的 UA：<c>pc-api.uc.cn</c> 只认客户端 UA，
        /// <c>drive.uc.cn</c> 的 <c>account/info</c> 只认网页 UA（见 <see cref="UcConstants.WebUa"/>）。
        /// 其余网盘一律用档案里的 UA，保证取链与下载同版本。
        /// </summary>
        private string UcUa(string url, bool api, PanClientProfile.Profile p)
        {
            if (Kind != PanKind.Uc)
                return api ? p.ApiUserAgent : p.DownloadUserAgent;
            if (!api)
                return p.DownloadUserAgent;
            if (url != null && url.IndexOf("drive.uc.cn", StringComparison.OrdinalIgnoreCase) >= 0)
                return UcConstants.WebUa;   // 网页域的 account/info 按 UA 分身份，单独处理
            return p.ApiUserAgent;
        }

        /// <summary>
        /// 按 <see cref="Kind"/> 生成字典形式的完整请求头，供自建 OpenSSL 通道使用。
        /// </summary>
        /// <remarks>
        /// 必须与 <see cref="ApplyHeadersForKind"/> 取同一套值：UC 的 <c>pc-api.uc.cn</c>
        /// 与 <c>pan.quark.cn</c> 一样按 TLS ClientHello 指纹过滤，只能走 OpenSSL 通道；
        /// 两条通道的头不一致，就等于给 WAF 送了两个不同的指纹。
        /// </remarks>
        public Dictionary<string, string> BuildHeadersForKind(string url, bool api = true)
        {
            var p = PanClientProfile.For(Kind);
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "User-Agent", UcUa(url, api, p) },
                { "Accept", api
                    ? "application/json, text/plain, */*"
                    : "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8" },
                { "Referer", p.Referer },
                { "Origin", p.Origin },
                { "DNT", "1" },
                // 每条请求各开一条 TLS 连接，必须显式 close，否则对端会等 Content-Length。
                { "Connection", "close" },
                { "Cookie", BuildCookieString() },
            };
            PanClientProfile.ApplyHeaders(Kind, d);
            // 网页域账号接口用普通 Chrome UA，不能同时带 UC 客户端的 Sec-Ch-Ua。
            if (IsUcWebDomain(url)) d.Remove("Sec-Ch-Ua");
            return d;
        }

        private static void Set(HttpRequestMessage req, string name, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            req.Headers.TryAddWithoutValidation(name, value);
        }

        // ---------------- 节流 ----------------

        /// <summary>
        /// 请求前的节流等待：令牌桶。
        /// 桶里有令牌就立即放行（允许 BurstCapacity 个连发），
        /// 桶空了才按「平均每 MinIntervalMs 一个请求」排队，并带随机抖动避免固定节奏。
        /// </summary>
        public async Task ThrottleAsync(CancellationToken cancel = default)
        {
            int wait;
            lock (_throttleLock)
            {
                var now = DateTime.UtcNow;
                int interval = MinIntervalMs > 0 ? MinIntervalMs : 1;

                if (_lastRefillAt == DateTime.MinValue)
                {
                    // 首次调用：桶直接给满，避免启动时先白等一个间隔
                    _tokens = BurstCapacity;
                    _lastRefillAt = now;
                }
                else
                {
                    var elapsed = (now - _lastRefillAt).TotalMilliseconds;
                    if (elapsed > 0)
                    {
                        // 每秒补充 1000/interval 个令牌
                        _tokens = Math.Min(BurstCapacity, _tokens + elapsed / interval);
                        _lastRefillAt = now;
                    }
                }

                // 允许透支为负：负数代表「已预约但还没轮到」，绝对值就是排队位次
                _tokens -= 1.0;
                if (_tokens >= 0)
                {
                    wait = 0;
                }
                else
                {
                    // 1 个令牌 = interval 毫秒；再乘抖动，避免固定节奏
                    var jitter = 0.85 + 0.3 * _throttleRng.NextDouble();
                    wait = (int)Math.Ceiling(-_tokens * interval * jitter);
                }
            }
            if (wait > 0)
                await Task.Delay(wait, cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// 带指数退避的请求执行。遇到可重试状态码（5xx / 408 / 429）时退避重试，
        /// 退避时间带随机抖动，避免所有分片同一时刻重试形成尖峰。
        /// </summary>
        public async Task<HttpResponseMessage> SendWithRetryAsync(
            Func<HttpRequestMessage> requestFactory, int maxAttempts = 3,
            CancellationToken cancel = default, Action<int> onRetry = null)
        {
            Exception last = null;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancel.ThrowIfCancellationRequested();
                await ThrottleAsync(cancel).ConfigureAwait(false);

                HttpRequestMessage req = null;
                HttpResponseMessage resp = null;
                try
                {
                    req = requestFactory();
                    resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel)
                                    .ConfigureAwait(false);

                    if (IsRetryableStatus((int)resp.StatusCode) && attempt < maxAttempts - 1)
                    {
                        onRetry?.Invoke((int)resp.StatusCode);
                        resp.Dispose();
                        req.Dispose();
                        await BackoffAsync(attempt, cancel).ConfigureAwait(false);
                        continue;
                    }
                    return resp;
                }
                catch (OperationCanceledException)
                {
                    resp?.Dispose();
                    req?.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    resp?.Dispose();
                    req?.Dispose();
                    if (attempt < maxAttempts - 1)
                    {
                        await BackoffAsync(attempt, cancel).ConfigureAwait(false);
                        continue;
                    }
                }
            }
            throw new HttpRequestException("请求失败(已重试 " + maxAttempts + " 次): " + (last?.Message ?? "未知错误"), last);
        }

        private static bool IsRetryableStatus(int code)
            => code == 408 || code == 429 || (code >= 500 && code < 600);

        /// <summary>指数退避 + 随机抖动：0.6s, 1.4s, 3.0s ...（带 ±40% 抖动）</summary>
        private static async Task BackoffAsync(int attempt, CancellationToken cancel)
        {
            double baseMs = 600 * Math.Pow(2.2, attempt);
            double jitter = 0.6 + 0.8 * new Random(Guid.NewGuid().GetHashCode()).NextDouble();
            int ms = (int)Math.Min(baseMs * jitter, 15000);
            await Task.Delay(ms, cancel).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _http?.Dispose();
            _handler?.Dispose();
            _gate?.Dispose();
        }
    }
}
