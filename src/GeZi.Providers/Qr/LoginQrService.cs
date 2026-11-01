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
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using GeZi.Core.Support;
using GeZi.Core.Api;
using GeZi.Core.Models;
using GeZi.Providers.Shared;

namespace GeZi.Providers.Qr
{
    /// <summary>
    /// 扫码登录轮询阶段。移植自 Python Flet 版的做法：
    /// 每轮把"当前处于哪一步"上报给界面，用户能看到扫码/确认的实时反馈，
    /// 而不是干等 300 秒然后突然成功或失败。
    /// </summary>
    public enum QrLoginStage
    {
        /// <summary>等待用户扫码（接口返回 50004001 "Query result is empty"）。</summary>
        WaitingScan,
        /// <summary>已扫码，等待手机上点确认（接口开始返回非空结果但尚无 ticket）。</summary>
        ScannedWaitConfirm,
        /// <summary>二维码已失效，本轮会立刻结束，不再空转到超时。</summary>
        Expired,
        /// <summary>拿到 service_ticket，可以换取登录态了。</summary>
        GotTicket,
    }

    /// <summary>一次轮询的结果：阶段 + 原始 status + 原始 message（未识别时可原样展示，不吞信息）。</summary>
    public struct QrPollResult
    {
        public QrLoginStage Stage;
        public long Status;
        public string Message;
        public string Ticket;
    }

    /// <summary>夸克扫码登录：轮询 uop 接口换取登录 Cookie，不呼出内置浏览器。</summary>
    public class LoginQrService : IDisposable
    {
        private readonly HttpClient _http;

        public LoginQrService()
        {
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.None,
            };
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        /// <summary>获取二维码 token 和扫码 URL（把 qrUrl 展示给用户或打开浏览器即可）。</summary>
        public async Task<(bool ok, string token, string qrUrl, string error)> GetQrCodeAsync()
        {
            try
            {
                string url = QuarkConstants.QrLoginGetToken + "?client_id=532&v=1.2&request_id=" + Guid.NewGuid().ToString("N");
                var res = await GetJsonAsync(url).ConfigureAwait(false);
                var members = AsDict(Get(AsDict(Get(AsDict(res), "data")), "members"));
                string token = AsString(Get(members, "token")) ?? "";
                if (AsLong(Get(AsDict(res), "status")) != 2000000 || string.IsNullOrEmpty(token))
                {
                    string msg = AsString(Get(AsDict(res), "message")) ?? "无 token";
                    return (false, "", "", "获取扫码登录二维码失败: " + msg);
                }
                string qrUrl = QuarkConstants.QrLoginScanBase
                    + "?token=" + Uri.EscapeDataString(token)
                    + "&client_id=532&ssb=weblogin&uc_param_str="
                    + "&uc_biz_str=S:custom|OPT:SAREA@0|OPT:IMMERSIVE@1|OPT:BACK_BTN_STYLE@0";
                return (true, token, qrUrl, "");
            }
            catch (Exception ex)
            {
                return (false, "", "", "扫码登录接口连接失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 带状态上报的轮询：每轮解析 status/message 并通过 <paramref name="onState"/> 回调，
        /// 让界面能显示"等待扫码 → 已扫码待确认 → 成功 / 已过期"的实时进度。
        /// 返回最终结果：GotTicket 表示拿到 ticket；Expired 表示二维码失效（**立即返回**，
        /// 不再空转到超时）；其余情况表示超时。
        /// 对未知 status 采取"不吞信息"策略：原样带回 status 与 message，由界面展示，
        /// 而不是假装还在等待 —— 免得下次接口又加新状态码时用户又看到"卡住"。
        /// </summary>
        public async Task<QrPollResult> PollForTicketWithStatusAsync(
            string token, int timeoutSeconds = 180,
            IProgress<QrPollResult> onState = null,
            CancellationToken cancel = default)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            long lastReported = long.MinValue;
            while (DateTime.UtcNow < deadline)
            {
                cancel.ThrowIfCancellationRequested();
                // 与官方网页版一致：2 秒一轮（官网 setInterval(..., 2e3)）。
                // 原来用 1200ms，比官网密 40%，除了多打请求没有任何收益。
                await Task.Delay(2000, cancel).ConfigureAwait(false);

                long status = -1;
                string message = null;
                string ticket = null;
                try
                {
                    // ⚠️ 必须带 client_id —— 2026-10-24 实测三轮 100% 复现：
                    //   只传 token               → {"status":50004002,"message":"Token Not Found"}
                    //   token + client_id=532&v=1.2 → {"status":50004001,"message":"Query result is empty"}（正常等待扫码）
                    // 原因：取码接口会下发 _UP_28A_52_=532 这类客户端标识 Cookie，而本类用
                    // UseCookies=false，轮询时若不显式带 client_id，服务端就认为这个 token
                    // 不属于任何客户端 → 直接判「不存在」，界面表现成「二维码刚出来就失效」。
                    // 曾经把 client_id 删掉过（注释说「官方并不需要」），那是错的，别再删。
                    string url = QuarkConstants.QrLoginGetTicket + "?token="
                        + Uri.EscapeDataString(token) + "&client_id=532&v=1.2";
                    var res = await GetJsonAsync(url).ConfigureAwait(false);
                    status = AsLong(Get(AsDict(res), "status"));
                    message = AsString(Get(AsDict(res), "message"));
                    var members = AsDict(Get(AsDict(Get(AsDict(res), "data")), "members"));
                    ticket = AsString(Get(members, "service_ticket")) ?? "";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // 单次轮询失败继续，不打扰界面
                    continue;
                }

                // 1) 拿到 ticket → 扫码+确认都完成了
                if (!string.IsNullOrEmpty(ticket))
                {
                    var okRes = new QrPollResult
                    {
                        Stage = QrLoginStage.GotTicket, Status = status,
                        Message = message, Ticket = ticket,
                    };
                    Report(onState, okRes);
                    return okRes;
                }

                // 2) 明确失效 → 立即返回，让界面提示刷新
                if (status == QuarkConstants.QrStatusTokenNotFound
                    || status == QuarkConstants.QrStatusTokenExpired)
                {
                    var expRes = new QrPollResult
                    {
                        Stage = QrLoginStage.Expired, Status = status, Message = message,
                    };
                    Report(onState, expRes);
                    return expRes;
                }

                // 3) 未扫码 → 等待；4) 其它 status → 视作"已扫码待确认"（夸克会先返回一个非空 data
                //    但还没 ticket 的中间态），并把原始码带给界面，未识别也不隐瞒。
                var stage = status == QuarkConstants.QrStatusWaitingScan
                    ? QrLoginStage.WaitingScan
                    : QrLoginStage.ScannedWaitConfirm;

                // 同一状态不重复上报，避免界面闪烁刷新
                if (status != lastReported)
                {
                    lastReported = status;
                    Report(onState, new QrPollResult { Stage = stage, Status = status, Message = message });
                }
            }
            return new QrPollResult { Stage = QrLoginStage.Expired, Status = -1, Message = "超时" };
        }

        private static void Report(IProgress<QrPollResult> onState, QrPollResult r)
        {
            if (onState == null) return;
            try { onState.Report(r); } catch { }
        }

        /// <summary>
        /// 用 service_ticket 换取登录 Cookie（<c>__pus</c> / <c>__puus</c>）。
        /// <para></para>
        /// <list type="number">
        /// <item><b>首选：Python 桥</b>。登录态只能由 <c>pan.quark.cn/account/info?st=</c> 下发，
        ///       而该域把 .NET 的 SChannel 全部 RST 掉（按 ClientHello 指纹过滤）。
        ///       本机若存在"较新 OpenSSL"的 Python，就借它跑这一步 —— 这是唯一能真正拿到
        ///       <c>__pus</c> 的正路。详见 <see cref="PanAccountBridge"/>。</item>
        /// <item><b>回退：纯 .NET</b>。没有可用 Python 时，退到 uop 域尝试（只能拿到
        ///       <c>_UP_*</c> 系列，通常无法完成登录），并如实说明原因。</item>
        /// </list>
        /// </summary>
        public async Task<(bool ok, string cookie, string error)> ExchangeTicketAsync(string ticket)
        {
            // ---- 路径 1：Python 桥（正路） ----
            var viaPython = await ExchangeViaPythonAsync(ticket).ConfigureAwait(false);
            if (viaPython.HasValue)
            {
                var r = viaPython.Value;
                if (r.Ok) return (true, r.Cookie, "");
                // 桥可用但换失败 → 带上原因继续走回退，让错误信息更完整
                _pythonFailure = r.Error + (string.IsNullOrEmpty(r.Detail) ? "" : "（" + r.Detail + "）");
            }
            else
            {
                _pythonFailure = "本机未找到可用的 Python（需要自带 OpenSSL 3.2+ 的解释器）";
            }

            // ---- 路径 2：纯 .NET 回退 ----
            try
            {
                var jar = new CookieContainer { Capacity = 500, PerDomainCapacity = 200 };
                using (var handler = new HttpClientHandler
                {
                    CookieContainer = jar,
                    UseCookies = true,
                    AllowAutoRedirect = true,
                    AutomaticDecompression = DecompressionMethods.None,
                })
                using (var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
                {
                    // 收集每一步的失败原因，最终拼进错误信息 —— 不再笼统报"发送请求时出错"。
                    var problems = new List<string>();
                    string stQ = Uri.EscapeDataString(ticket);

                    // 1a) 用 service_ticket 在 uop 域激活登录态。
                    // `uop.quark.cn` 在 .NET 里可达，且实测同样接受 service_ticket 并下发
                    // 一整套 `.quark.cn` 域的登录 Cookie（`_UP_*` 系列）。
                    // 把它放第一位，是因为下面 1b 的 pan.quark.cn 在 .NET 栈**必然失败**。
                    try
                    {
                        await GetRawAsync(http, QuarkConstants.CasLoginUrl + "?st=" + stQ + "&lw=scan",
                            new Dictionary<string, string>
                            {
                                { "Origin", "https://pan.quark.cn" },
                                { "Accept", "text/html,application/xhtml+xml,*/*;q=0.8" },
                            }, 20).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        problems.Add("uop.quark.cn/cas/login 失败: " + RootMessage(ex));
                    }

                    // 1b) 官方主入口。.NET 下会因 TLS 指纹被 RST，失败**不算错**。
                    try
                    {
                        await GetRawAsync(http, QuarkConstants.AccountUrl + "?st=" + stQ + "&lw=scan",
                            new Dictionary<string, string>
                            {
                                { "Origin", "https://pan.quark.cn" },
                                { "Accept", "text/html,application/xhtml+xml,*/*;q=0.8" },
                            }, 20).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // 只记录，不返回 —— 关键 Cookie 可能已由 1a 那步拿到，
                        // 且后续 enrich 仍有机会补全。
                        problems.Add("pan.quark.cn 激活登录态失败（该域在本机 .NET 栈不可达，属已知限制）: " + RootMessage(ex));
                    }

                    // 2) 补全网盘/下载接口所需 Cookie（__puus 等）
                    string[] enrich =
                    {
                        "https://pan.quark.cn/list",
                        "https://drive-pc.quark.cn/1/clouddrive/file/sort?pr=ucpro&fr=pc&uc_param_str=&pdir_fid=0&_page=1&_size=50&_fetch_total=1&_sort=file_type:asc,updated_at:desc",
                        "https://drive.quark.cn/1/clouddrive/member?pr=ucpro&fr=pc&uc_param_str=&fetch_subscribe=true",
                    };
                    foreach (var u in enrich)
                    {
                        try
                        {
                            await GetRawAsync(http, u, new Dictionary<string, string>
                            {
                                { "User-Agent", QuarkConstants.DlUa },
                                { "Origin", "https://pan.quark.cn" },
                            }, 20).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            problems.Add(new Uri(u).Host + " 失败: " + RootMessage(ex));
                        }
                    }

                    // 3) 汇总所有 *.quark.cn 域的 Cookie
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string[] domains =
                    {
                        "https://pan.quark.cn", "https://drive-pc.quark.cn", "https://drive.quark.cn",
                        "https://uop.quark.cn", "https://su.quark.cn",
                    };
                    foreach (var d in domains)
                    {
                        try
                        {
                            foreach (Cookie c in jar.GetCookies(new Uri(d)))
                                dict[c.Name] = c.Value;
                        }
                        catch { }
                    }
                    string cookie = JoinCookies(dict);

                    // 4) 判定是否拿到了关键登录 Cookie（`__puus`）。
                    // 这里**只认 `__puus`**，不把 `_UP_*` 系列当成功证据 ——
                    // 实测：即使 st 完全无效，`uop.quark.cn/cas/login` 也会照常下发
                    // `_UP_F7E_8D_` / `_UP_A4A_11_` / `_UP_D_` 等 Cookie，且 `_UP_D_` 的值
                    // 在真假票据下并不稳定（假票据有时也回 `pc`）。**没有可靠的"看起来像成功"
                    // 的信号**，所以不能靠猜 —— 判据必须保守，否则会把失败伪装成成功。
                    // 真正的"是否登录成功"由调用方的 ApplyLoginAsync → ValidateAsync 判定：
                    // 它会拿这份 Cookie 去调可达的 `drive-pc.quark.cn` 业务接口，
                    // 用真实返回码说话（那才是唯一能自证的判据）。
                    if (dict.ContainsKey(QuarkConstants.LoginCookieKey))
                        return (true, cookie, "");

                    // 纯 .NET 回退也失败。把"为什么正路没走通"讲清楚 —— 用户最需要知道的是
                    // "缺什么、怎么办"，而不是一句笼统的"未取回关键登录态"。
                    string detail = problems.Count > 0 ? ("；明细: " + string.Join(" | ", problems)) : "";
                    return (false, cookie, BuildExchangeFailureMessage(detail));
                }
            }
            catch (Exception ex)
            {
                return (false, "", BuildExchangeFailureMessage("；异常: " + RootMessage(ex)));
            }
        }

        // 上一次 Python 桥的失败原因（无 Python 时也会填）。用于拼装最终错误文案。
        private string _pythonFailure;

        /// <summary>调用 Python 桥换 Cookie。返回 null 表示"本机没有可用 Python，此路不通"。</summary>
        private static Task<PanAccountBridge.ExchangeOutcome?> ExchangeViaPythonAsync(string ticket)
        {
            // 探测 + 进程调用都是阻塞式的，放到线程池跑，避免卡住 UI。
            return Task.Run<PanAccountBridge.ExchangeOutcome?>(() =>
            {
                try
                {
                    return PanAccountBridge.Exchange(ticket);
                }
                catch (Exception ex)
                {
                    return new PanAccountBridge.ExchangeOutcome
                    {
                        Ok = false,
                        Error = "Python 桥调用异常: " + ex.Message,
                    };
                }
            });
        }

        /// <summary>拼装"两条路都失败"时的用户可读错误信息。</summary>
        private string BuildExchangeFailureMessage(string netDetail)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("扫码登录未能取回登录态");
            if (!string.IsNullOrEmpty(_pythonFailure))
                sb.Append("；Python 通道: ").Append(_pythonFailure);
            sb.Append("；.NET 通道: 未取回 ")
              .Append(QuarkConstants.LoginCookieKey).Append(netDetail);
            return sb.ToString();
        }

        /// <summary>取最内层异常的消息 —— 外层往往只是"发送请求时出错"，真正原因在里面。</summary>
        private static string RootMessage(Exception ex)
        {
            var e = ex;
            int guard = 0;
            while (e.InnerException != null && guard++ < 8)
                e = e.InnerException;
            return e.GetType().Name + ": " + e.Message;
        }

        // ---------------- 工具 ----------------

        private async Task<object> GetJsonAsync(string url)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.TryAddWithoutValidation("User-Agent", QuarkConstants.ApiUa);
                req.Headers.TryAddWithoutValidation("Referer", QuarkConstants.Referer);
                req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                using (var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false))
                {
                    string raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return Deserialize(raw);
                }
            }
        }

        private static async Task GetRawAsync(HttpClient http, string url, Dictionary<string, string> headers, int timeoutSeconds)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (!headers.ContainsKey("User-Agent")) headers["User-Agent"] = QuarkConstants.ApiUa;
                if (!headers.ContainsKey("Referer")) headers["Referer"] = QuarkConstants.Referer;
                if (!headers.ContainsKey("Accept")) headers["Accept"] = "application/json, text/plain, */*";
                if (!headers.ContainsKey("Accept-Language")) headers["Accept-Language"] = "zh-CN,zh;q=0.9";
                foreach (var kv in headers)
                    req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                using (var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false))
                {
                    await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
        }

        private static object Deserialize(string json)
            => new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json);

        private static Dictionary<string, object> AsDict(object o) => o as Dictionary<string, object>;

        private static object Get(Dictionary<string, object> d, string key)
        {
            if (d != null && d.TryGetValue(key, out var v))
                return v;
            return null;
        }

        private static string AsString(object o) => o as string;

        private static long AsLong(object o)
        {
            if (o is long) return (long)o;
            if (o is int) return (int)o;
            if (o is double) return (long)(double)o;
            if (o is decimal) return (long)(decimal)o;
            if (o is string s) { long v; return long.TryParse(s, out v) ? v : 0; }
            return 0;
        }

        private static string JoinCookies(Dictionary<string, string> d)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in d)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }

        public void Dispose() => _http.Dispose();
    }
}
