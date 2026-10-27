// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GeZi.Core.Support
{
    /// <summary>从任意 URL（多来自二维码）还原出夸克分享链接。</summary>
    public static class ShareLinkResolver
    {
        /// <summary>手机优先——二维码本来给手机扫。桌面兜底：有些站只把链接藏在页面里。</summary>
        private const string UaAndroid =
            "Mozilla/5.0 (Linux; Android 13; SM-G991B) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/126.0 Mobile Safari/537.36";

        private const string UaIPhone =
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 " +
            "(KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";

        private const string UaWeChat =
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 " +
            "(KHTML, like Gecko) Mobile/15E148 MicroMessenger/8.0.49(0x18003128) NetType/WIFI Language/zh_CN";

        private const string UaDesktop =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/126.0 Safari/537.36";

        private static readonly KeyValuePair<string, string>[] UaPool =
        {
            new KeyValuePair<string, string>("Android", UaAndroid),
            new KeyValuePair<string, string>("iPhone", UaIPhone),
            new KeyValuePair<string, string>("微信", UaWeChat),
            new KeyValuePair<string, string>("桌面", UaDesktop),
        };

        private const int MaxHops = 5;
        private const int TimeoutSeconds = 6;
        private const int TotalBudgetMs = 25000;
        private const int MaxBodyChars = 2 * 1024 * 1024;

        /// <summary>scheme 可选，可带 <c>?pwd=…</c>。</summary>
        private static readonly Regex QuarkShare = new Regex(
            @"(?:https?:)?//pan\.quark\.cn/s/([A-Za-z0-9_-]+)([^\s""'<>\\]*)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MetaRefresh = new Regex(
            @"<meta[^>]+http-equiv\s*=\s*[""']?\s*refresh\s*[""']?[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MetaUrl = new Regex(
            @"url\s*=\s*[""']?\s*([^""'>\s;]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] DeadKeywords =
            { "链接已失效", "已失效", "已过期", "不存在", "已删除", "已被取消", "已取消分享" };

        private static readonly string[] BlockKeywords =
            { "请用手机", "手机扫描", "请在手机", "请在移动端", "移动端打开", "请使用手机" };

        /// <summary>还原结果。</summary>
        public sealed class Result
        {
            /// <summary>是否拿到夸克分享链接。</summary>
            public bool Ok;

            /// <summary>拿到的分享链接（可带 <c>?pwd=</c>）。</summary>
            public string Url;

            /// <summary>命中方式：重定向 / 页面提取 / 本身就是。</summary>
            public string How;

            /// <summary>命中时使用的 UA 名（排查用）。</summary>
            public string UaLabel;

            /// <summary>跟了几跳。</summary>
            public int Hops;

            /// <summary>失败原因（成功时为空）。</summary>
            public string Error;

            public override string ToString()
            {
                return Ok ? (Url + "  [" + How + (UaLabel == null ? "" : "/" + UaLabel) + "，"
                             + Hops + " 跳]")
                          : ("失败：" + Error);
            }
        }

        /// <summary>是否 http/https 绝对地址。</summary>
        public static bool IsHttpUrl(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            Uri u;
            return Uri.TryCreate(s.Trim(), UriKind.Absolute, out u)
                   && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
        }

        /// <summary>是否已经是夸克分享链接。</summary>
        public static bool IsQuarkShare(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return QuarkShare.IsMatch(s);
        }

        /// <summary>还原出夸克分享链接。已经是夸克链接则直接返回，不发请求。</summary>
        public static async Task<Result> ResolveAsync(string url)
        {
            if (!IsHttpUrl(url))
                return new Result { Error = "不是 http/https 链接" };

            string start = url.Trim();

            if (IsQuarkShare(start))
                return new Result { Ok = true, Url = start, How = "本身就是" };

            var sw = Stopwatch.StartNew();
            var notes = new List<string>();
            string lastHead = null;

            foreach (var ua in UaPool)
            {
                if (sw.ElapsedMilliseconds > TotalBudgetMs)
                {
                    notes.Add("超时预算用尽");
                    break;
                }

                var r = await TryWithUaAsync(start, ua.Key, ua.Value, notes).ConfigureAwait(false);
                if (r.Ok) return r;

                if (!string.IsNullOrEmpty(r.Error))
                    lastHead = r.Error;
            }

            return new Result { Error = DescribeDeadEnd(lastHead, notes) };
        }

        /// <summary>同步包装（内部全部 ConfigureAwait(false)，不会死锁）。</summary>
        public static Result Resolve(string url)
        {
            return ResolveAsync(url).GetAwaiter().GetResult();
        }

        /// <summary>单个 UA 跑完整条链：跟跳转，每跳都试着从页面正文里抠链接。</summary>
        private static async Task<Result> TryWithUaAsync(
            string startUrl, string uaLabel, string ua, List<string> notes)
        {
            Uri cur;
            if (!Uri.TryCreate(startUrl, UriKind.Absolute, out cur))
                return new Result { Error = "地址无法解析" };

            int hops = 0;
            string head = null;

            try
            {
                using (var handler = new HttpClientHandler())
                {
                    // 手动跟跳：拿到夸克链接就收工，也免得被带到无关落地页
                    handler.AllowAutoRedirect = false;
                    handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                    NetworkOptions.ApplyTo(handler);

                    using (var client = new HttpClient(handler))
                    {
                        client.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);
                        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);
                        client.DefaultRequestHeaders.TryAddWithoutValidation(
                            "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                        client.DefaultRequestHeaders.TryAddWithoutValidation(
                            "Accept-Language", "zh-CN,zh;q=0.9");

                        for (hops = 0; hops < MaxHops; hops++)
                        {
                            string curStr = cur.ToString();

                            var direct = ExtractQuark(curStr);
                            if (direct != null)
                                return new Result { Ok = true, Url = direct, How = "重定向", UaLabel = uaLabel, Hops = hops };

                            HttpResponseMessage resp = null;
                            try
                            {
                                resp = await client.GetAsync(cur).ConfigureAwait(false);
                                int code = (int)resp.StatusCode;

                                if (code >= 300 && code < 400)
                                {
                                    string loc = resp.Headers.Location == null
                                        ? null : resp.Headers.Location.ToString();
                                    if (!string.IsNullOrWhiteSpace(loc))
                                    {
                                        loc = loc.Trim();
                                        var hit = ExtractQuark(loc);
                                        if (hit == null)
                                        {
                                            Uri absLoc;
                                            if (Uri.TryCreate(loc, UriKind.Absolute, out absLoc)
                                                || Uri.TryCreate(cur, loc, out absLoc))
                                            {
                                                hit = ExtractQuark(absLoc.ToString());
                                                if (hit != null) loc = absLoc.ToString();
                                            }
                                        }
                                        if (hit != null)
                                            return new Result { Ok = true, Url = hit, How = "重定向", UaLabel = uaLabel, Hops = hops + 1 };

                                        Uri nu;
                                        if (!Uri.TryCreate(loc, UriKind.Absolute, out nu)
                                            && !Uri.TryCreate(cur, loc, out nu))
                                            break;
                                        if (nu.Scheme != Uri.UriSchemeHttp && nu.Scheme != Uri.UriSchemeHttps)
                                            break;
                                        if (string.Equals(nu.ToString(), curStr, StringComparison.OrdinalIgnoreCase))
                                            break;

                                        cur = nu;
                                        continue;
                                    }
                                }

                                // 扫正文
                                if (resp.Content != null)
                                {
                                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                    if (!string.IsNullOrEmpty(body))
                                    {
                                        if (body.Length > MaxBodyChars)
                                            body = body.Substring(0, MaxBodyChars);
                                        head = body;

                                        // 还原实体，否则 ?pwd=a&b 会断在 & 处
                                        string unescaped = body
                                            .Replace("&amp;", "&")
                                            .Replace("&#38;", "&")
                                            .Replace("&quot;", "\"")
                                            .Replace("&#x2F;", "/")
                                            .Replace("&#47;", "/");

                                        var hit = ExtractQuark(unescaped);
                                        if (hit != null)
                                            return new Result { Ok = true, Url = hit, How = "页面提取", UaLabel = uaLabel, Hops = hops };

                                        var m = MetaRefresh.Match(unescaped);
                                        if (m.Success)
                                        {
                                            var mu = MetaUrl.Match(m.Value);
                                            if (mu.Success)
                                            {
                                                string u = mu.Groups[1].Value.Trim();
                                                if (u.Length > 0
                                                    && !u.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    Uri nu;
                                                    if (Uri.TryCreate(u, UriKind.Absolute, out nu)
                                                        || Uri.TryCreate(cur, u, out nu))
                                                    {
                                                        if ((nu.Scheme == Uri.UriSchemeHttp || nu.Scheme == Uri.UriSchemeHttps)
                                                            && !string.Equals(nu.ToString(), curStr, StringComparison.OrdinalIgnoreCase))
                                                        {
                                                            cur = nu;
                                                            continue;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }

                                // 换下一个 UA
                                break;
                            }
                            catch (Exception ex)
                            {
                                notes.Add(uaLabel + "：" + ex.GetType().Name);
                                break;
                            }
                            finally
                            {
                                if (resp != null) resp.Dispose();
                            }
                        }
                    }
                }

                return new Result { Hops = hops, Error = head };
            }
            catch (Exception ex)
            {
                notes.Add(uaLabel + "：" + ex.GetType().Name);
                return new Result { Hops = hops, Error = head };
            }
        }

        /// <summary>从文本里取第一条夸克分享链接；没有返回 null。</summary>
        private static string ExtractQuark(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var m = QuarkShare.Match(text);
            if (!m.Success) return null;

            string url = m.Value;
            if (url.StartsWith("//", StringComparison.Ordinal))
                url = "https:" + url;

            return url;
        }

        /// <summary>失败时尽量说清原因，别只丢一句"没找到"。</summary>
        private static string DescribeDeadEnd(string head, List<string> notes)
        {
            if (!string.IsNullOrEmpty(head))
            {
                foreach (var k in DeadKeywords)
                {
                    if (head.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                        return "页面提示「" + k + "」——这类跳转地址通常是一次性的，" +
                               "被手机扫过或浏览器打开过就作废了，回原页面重新取一次再来";
                }
                foreach (var k in BlockKeywords)
                {
                    if (head.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                        return "站点对桌面端隐藏了链接（写着「" + k + "」），换了 4 种 UA 也没拿到";
                }
            }

            if (notes != null && notes.Count > 0)
                return "没找到夸克分享链接（" + string.Join("；", notes.ToArray()) + "）";

            return "没找到夸克分享链接（页面里既没有链接也没有跳转）";
        }
    }
}
