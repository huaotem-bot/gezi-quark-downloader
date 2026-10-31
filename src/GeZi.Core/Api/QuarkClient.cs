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
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using GeZi.Core.Models;
using GeZi.Core.Support;

namespace GeZi.Core.Api
{
    /// <summary>
    /// 夸克网页端公开接口客户端，等价于原 Python 核心里 QuarkClient 类的移植。
    /// Cookie 手动维护（与 Python 一样整串下发、按 Set-Cookie 增量合并），
    /// 不依赖 CookieContainer 的域名匹配，避免跨域 Cookie 丢失。
    /// JSON 用框架自带的 JavaScriptSerializer（零外部依赖，可离线编译）。
    /// </summary>
    public class QuarkClient : IDisposable
    {
        private readonly QuarkSession _session;
        private string _accountSig;

        private static readonly Random Rng = new Random();

        public QuarkClient(string cookie)
        {
            _session = new QuarkSession(cookie);
        }

        // ---------------- Cookie ----------------

        /// <summary>实时汇总的 Cookie 串（含服务端 Set-Cookie 更新后的最新值）。</summary>
        public string CookieStr => _session.BuildCookieString();

        // ---------------- 底层请求 ----------------

        private Timer _keepAliveTimer;
        private readonly object _keepAliveLock = new object();

        /// <summary>
        /// 对外请求入口：在底层请求之上增加了 401/412 鉴权重试与重新校验。
        /// 遇到鉴权类状态码时，先做一次快速登录态校验；若 Cookie 仍有效则退避重试，
        /// 避免偶发风控/抖动导致的假性失效被误判为登录过期。
        /// </summary>
        private async Task<object> RequestAsync(HttpMethod method, string url,
            IDictionary<string, string> query = null, object body = null, int timeoutSeconds = 30)
        {
            int authRetry = 0;
            const int maxAuthRetry = 1;
            while (true)
            {
                try
                {
                    return await RequestCoreAsync(method, url, query, body, timeoutSeconds).ConfigureAwait(false);
                }
                catch (QuarkApiException ex) when (
                    (ex.HttpStatus == 401 || ex.HttpStatus == 412) && authRetry < maxAuthRetry)
                {
                    authRetry++;
                    // 鉴权退避：2–4 秒，避免紧接重试撞上风控窗口
                    await Task.Delay(2000 + Rng.Next(0, 2000)).ConfigureAwait(false);

                    bool stillValid = await QuickValidateAsync().ConfigureAwait(false);
                    if (!stillValid)
                    {
                        throw new QuarkApiException(
                            ex.HttpStatus == 401
                                ? "登录态已失效(401)，请重新登录"
                                : "请求被拦截(412)，登录态可能已失效，请重新登录",
                            ex.Code, ex.Status) { HttpStatus = ex.HttpStatus };
                    }
                    // Cookie 仍有效，重试原请求
                    continue;
                }
            }
        }

        /// <summary>纯底层请求，不处理鉴权重试。</summary>
        private async Task<object> RequestCoreAsync(HttpMethod method, string url,
            IDictionary<string, string> query = null, object body = null, int timeoutSeconds = 30)
        {
            if (query != null && query.Count > 0)
            {
                var sb = new StringBuilder(url);
                sb.Append(url.IndexOf('?') >= 0 ? '&' : '?');
                bool first = true;
                foreach (var kv in query)
                {
                    if (!first)
                        sb.Append('&');
                    sb.Append(Uri.EscapeDataString(kv.Key)).Append('=')
                      .Append(Uri.EscapeDataString(kv.Value ?? ""));
                    first = false;
                }
                url = sb.ToString();
            }

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                try
                {
                    // 会话层负责：补全标准浏览器请求头、节流、对 5xx/408/429 指数退避重试
                    using (var resp = await _session.SendWithRetryAsync(() =>
                    {
                        var req = new HttpRequestMessage(method, url);
                        QuarkSession.ApplyHeaders(req, url, api: true);
                        if (body != null)
                        {
                            var json = Serialize(body);
                            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                        }
                        return req;
                    }, 3, cts.Token).ConfigureAwait(false))
                    {
                        string raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        try
                        {
                            return Deserialize(raw);
                        }
                        catch (Exception)
                        {
                            string hint = AuthHint((int)resp.StatusCode);
                            throw new QuarkApiException(
                                hint ?? ("HTTP " + (int)resp.StatusCode + ": " + resp.ReasonPhrase),
                                null, null, (int)resp.StatusCode);
                        }
                    }
                }
                catch (QuarkApiException)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw new QuarkApiException("请求超时(" + timeoutSeconds + "s): " + url);
                }
                catch (Exception ex)
                {
                    throw new QuarkApiException("网络请求失败: " + ex.Message);
                }
            }
        }

        /// <summary>把鉴权类状态码翻译成用户看得懂的提示，避免笼统报"请求失败"。</summary>
        private static string AuthHint(int code)
        {
            switch (code)
            {
                case 401:
                    return "登录态已失效(401)，请重新登录";
                case 403:
                    return "访问被拒绝(403)，可能是登录态失效或该操作需要更高权限";
                case 412:
                    return "请求被拦截(412)，请稍后重试；若持续出现请重新登录";
                case 429:
                    return "请求过于频繁(429)，已自动退避，请稍后再试";
                default:
                    return null;
            }
        }

        /// <summary>
        /// 下载取链失败时的文案加工：把限流类业务码翻译成用户能看懂、能行动的提示。
        /// 23018 在 file/download 上是「大文件下载限制」（2026-10-31 实测：限流窗口内
        /// 拒发直链、窗口重开后同请求可通过），不是转存路径那个「空间不足」含义。
        /// </summary>
        private static string DescribeLinkError(QuarkApiException ex)
        {
            if (AsLong(ex.Code) == QuarkErrorCode.DownloadFileSizeLimit)
                return "触发夸克大文件下载限制(23018)：当前账号进入服务端限流窗口，"
                     + "等待一段时间通常会自动解除，可稍后重试，或转存到自己网盘后再下载";
            return null;
        }

        private static object Check(object res, string what = "接口")
        {
            var d = AsDict(res);
            if (d != null)
            {
                if (AsLong(Get(d, "status")) == 200 && AsLong(Get(d, "code")) == 0)
                    return res;
                string msg = AsString(Get(d, "message"));
                if (string.IsNullOrEmpty(msg))
                    msg = Serialize(res);
                if (msg.Length > 200)
                    msg = msg.Substring(0, 200);
                object code = Get(d, "code");
                object status = Get(d, "status");
                if (msg.Contains("user NOT real name"))
                    throw new QuarkApiException("账号未实名无法分享，请前往夸克官方进行实名", code, status);
                if (IsCapacityMessage(msg) || IsCapacityCode(code))
                    throw new QuarkApiException("网盘容量不足，无法完成转存（请清理网盘空间或减少选择文件）", code, status);
                throw new QuarkApiException("[" + what + "] " + msg, code, status);
            }
            throw new QuarkApiException("[" + what + "] 接口返回异常");
        }

        /// <summary>
        /// 判断一条接口消息是否为「网盘容量不足」。
        /// 对齐 Python 版 `_is_capacity_error`（匹配「容量」/「capacity」），
        /// 并额外补上夸克实际会返回的英文表述 —— 只匹配中文会漏掉一批。
        /// </summary>
        public static bool IsCapacityMessage(string msg)
        {
            if (string.IsNullOrEmpty(msg))
                return false;
            string m = msg.ToLowerInvariant();
            return msg.Contains("容量")
                || m.Contains("capacity")
                || m.Contains("storage is full")
                || m.Contains("not enough space")
                || m.Contains("space limit");
        }

        /// <summary>
        /// 夸克转存相关的容量不足业务码。取值为常见已知码 + 兜底文本判定，
        /// 宁可漏判（退化为普通报错）也不误判（把网络错误当容量问题去降级）。
        /// </summary>
        private static bool IsCapacityCode(object code)
        {
            long c = AsLong(code);
            // 一律引用常量类，别在这里写裸数字（见 QuarkErrorCode 的说明）：
            //   23017 / 23018 = 转存接口**同步响应**里的空间不足族；
            //   32003         = 转存任务**异步轮询结果**里的容量不足码。
            return c == QuarkErrorCode.ShareSaveCapacityInsufficient
                || c == QuarkErrorCode.ShareSaveCapacityInsufficientAlt
                || c == QuarkErrorCode.CapacityInsufficient;
        }

        // ---------------- 账号 ----------------

        /// <summary>
        /// 账号头像 URL。**探测**出来的（见 <see cref="PickAvatarUrl"/>），取不到为 null。
        /// </summary>
        public string AvatarUrl { get; private set; }

        /// <summary>
        /// 外部（Python 桥）拿到头像地址后回填。
        /// </summary>
        /// <remarks>
        /// `account/info` 挂在 `pan.quark.cn`，**.NET 连不上**（WAF 按
        /// ClientHello 指纹过滤），所以 <see cref="ValidateAsync"/> 永远走不到解析分支，
        /// 头像只能由 Python 桥取回后从外部塞进来。
        /// </remarks>
        public void SetAvatarUrl(string url)
        {
            if (!string.IsNullOrEmpty(url)) AvatarUrl = url;
        }

        /// <summary>
        /// 从 account/info 的返回里挑头像地址。
        /// </summary>
        /// <remarks>
        /// ⚠️ 夸克没有公开这个字段名 —— 实测（用真实账号请求 `account/info`）
        /// 字段叫 **`avatarUri`**，形如 `http://image.quark.cn/o/uop/...`。
        /// 这里仍保留多候选 + 兜底扫描，因为字段名可能随端/版本变化。
        /// 全都拿不到就返回 null —— 界面回退到"昵称首字"头像。
        /// </remarks>
        private static string PickAvatarUrl(Dictionary<string, object> data)
        {
            if (data == null) return null;

            string[] keys =
            {
                // 实测字段名（放第一位，命中就不用再扫）
                "avatarUri", "avatar_uri",
                "avatar", "avatar_url", "avatarUrl", "avatarurl",
                "headimg", "head_img", "headimgurl", "head_url", "headimg_url",
                "pic", "pic_url", "picurl", "icon", "icon_url",
                "user_avatar", "user_icon", "portrait", "photo", "photo_url",
                "figureurl", "figure_url",
            };
            foreach (var k in keys)
            {
                string v = AsString(Get(data, k));
                if (IsHttpUrl(v)) return v;
            }

            // 兜底：字段名可能不在候选表里 —— 扫一遍所有字符串值，
            // 挑"是 http 链接、且路径里带 avatar/head/pic"的那个。
            foreach (var kv in data)
            {
                string v = kv.Value as string;
                if (!IsHttpUrl(v)) continue;
                if (v.IndexOf("avatar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    v.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    v.IndexOf("portrait", StringComparison.OrdinalIgnoreCase) >= 0)
                    return v;
            }
            return null;
        }

        private static bool IsHttpUrl(string s)
        {
            return !string.IsNullOrEmpty(s) &&
                   (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        public async Task<(bool ok, string nick)> ValidateAsync()
        {
            try
            {
                var res = await RequestAsync(HttpMethod.Get, QuarkConstants.AccountUrl,
                    new Dictionary<string, string> { { "fr", "pc" }, { "platform", "pc" } });
                var data = AsDict(Get(AsDict(res), "data"));
                if (data != null)
                {
                    string nick = AsString(Get(data, "nickname")) ?? "已登录用户";
                    string sig = AsStringOrNumber(Get(data, "id"))
                        ?? AsStringOrNumber(Get(data, "account_id"))
                        ?? AsStringOrNumber(Get(data, "uid"))
                        ?? AsStringOrNumber(Get(data, "user_id"))
                        ?? nick;
                    if (!string.IsNullOrEmpty(sig))
                        _accountSig = sig;
                    // 头像：能探测到就记下来，拿不到保持 null（界面回退首字头像）
                    var av = PickAvatarUrl(data);
                    if (!string.IsNullOrEmpty(av)) AvatarUrl = av;
                    return (true, nick);
                }
            }
            catch (QuarkApiException) { }
            catch (Exception) { }

            try
            {
                await ListDirAsync("0", 1).ConfigureAwait(false);
                return (true, "已登录用户");
            }
            catch (QuarkApiException ex)
            {
                return (false, ex.Message);
            }
        }

        public string AccountSig => _accountSig ?? "";

        // ---------------- 容量 ----------------

        public async Task<(long total, long used)> GetCapacityAsync()
        {
            try
            {
                var res = await RequestAsync(HttpMethod.Get, QuarkConstants.ApiBase + "/1/clouddrive/member",
                    new Dictionary<string, string> { { "pr", "ucpro" }, { "fr", "pc" } });
                Check(res, "查询网盘容量");
                var data = AsDict(Get(AsDict(res), "data"));
                long total = Pick(data, "total_capacity", "secret_total_capacity", "total");
                long used = Pick(data, "use_capacity", "secret_use_capacity", "used_capacity", "used");
                if (total > 0)
                    return (total, used);
            }
            catch (Exception) { }
            return (0, 0);
        }

        private static long Pick(Dictionary<string, object> data, params string[] keys)
        {
            if (data == null)
                return 0;
            foreach (var k in keys)
            {
                var n = AsLong(Get(data, k));
                if (n > 0)
                    return n;
            }
            return 0;
        }

        // ---------------- 分享 ----------------

        public async Task<string> GetStokenAsync(string pwdId, string passcode = "")
        {
            var res = await RequestAsync(HttpMethod.Post, QuarkConstants.ApiBase + "/1/clouddrive/share/sharepage/token",
                new Dictionary<string, string> { { "pr", "ucpro" }, { "fr", "pc" } },
                new { pwd_id = pwdId, passcode = passcode });
            Check(res, "获取分享凭证");
            return AsString(Get(AsDict(Get(AsDict(res), "data")), "stoken")) ?? "";
        }

        public async Task<List<QuarkFileEntry>> ShareDetailAsync(string pwdId, string stoken, string pdirFid)
        {
            var merged = new List<QuarkFileEntry>();
            int page = 1;
            while (page <= 200)
            {
                var query = new Dictionary<string, string>
                {
                    { "pr", "ucpro" }, { "fr", "pc" },
                    { "pwd_id", pwdId }, { "stoken", stoken }, { "pdir_fid", pdirFid },
                    { "force", "0" }, { "_page", page.ToString() }, { "_size", "50" },
                    { "_fetch_banner", "0" }, { "_fetch_share", "0" }, { "_fetch_total", "1" },
                    { "_sort", "file_type:asc,updated_at:desc" },
                    { "ver", "2" }, { "fetch_share_full_path", "0" },
                };
                var res = await RequestAsync(HttpMethod.Get, QuarkConstants.ApiBase + "/1/clouddrive/share/sharepage/detail", query);
                Check(res, "读取分享列表");
                var list = AsArray(Get(AsDict(Get(AsDict(res), "data")), "list"));
                int added = 0;
                if (list != null)
                {
                    foreach (var it in list)
                    {
                        merged.Add(Normalize(it, share: true));
                        added++;
                    }
                }
                long total = AsLong(Get(AsDict(Get(AsDict(res), "metadata")), "_total"));
                if (added == 0 || (total > 0 && merged.Count >= total))
                    break;
                page++;
            }
            return merged;
        }

        /// <summary>目录的**一页**：条目 + 接口那边还有没有下一页。</summary>
        public sealed class DirPage
        {
            public readonly List<QuarkFileEntry> Items = new List<QuarkFileEntry>();
            public bool HasMore;
        }

        /// <summary>一次向接口要多少条。与原循环里写死的 50 一致。</summary>
        private const int DirPageSize = 50;

        /// <summary>
        /// 取分享目录的**一页**（page 从 1 开始）。
        /// <see cref="ShareDetailAsync"/> 是"从第 1 页循环到第 200 页、
        /// 把整个目录攒完再返回"—— 一个几千项的分享点进去要等十几秒（用户实测反馈
        /// 「一开始点击加载会很卡顿」）。这个只取一页，由上层按需一页页地取。
        /// </summary>
        public async Task<DirPage> ShareDetailPageAsync(string pwdId, string stoken,
            string pdirFid, int page)
        {
            var query = new Dictionary<string, string>
            {
                { "pr", "ucpro" }, { "fr", "pc" },
                { "pwd_id", pwdId }, { "stoken", stoken }, { "pdir_fid", pdirFid },
                { "force", "0" }, { "_page", page.ToString() }, { "_size", DirPageSize.ToString() },
                { "_fetch_banner", "0" }, { "_fetch_share", "0" }, { "_fetch_total", "1" },
                { "_sort", "file_type:asc,updated_at:desc" },
                { "ver", "2" }, { "fetch_share_full_path", "0" },
            };
            var res = await RequestAsync(HttpMethod.Get,
                QuarkConstants.ApiBase + "/1/clouddrive/share/sharepage/detail", query);
            Check(res, "读取分享列表");
            var one = new DirPage();
            var list = AsArray(Get(AsDict(Get(AsDict(res), "data")), "list"));
            if (list != null)
            {
                foreach (var it in list)
                    one.Items.Add(Normalize(it, share: true));
            }
            one.HasMore = PageHasMore(AsLong(Get(AsDict(Get(AsDict(res), "metadata")), "_total")),
                page, one.Items.Count);
            return one;
        }

        /// <summary>取自己网盘目录的一页（对应 <see cref="ListDirAsync"/> 的第 page 页）。</summary>
        public async Task<DirPage> ListDirPageAsync(string pdirFid, int page)
        {
            var query = new Dictionary<string, string>
            {
                { "pr", "ucpro" }, { "fr", "pc" }, { "uc_param_str", "" },
                { "pdir_fid", pdirFid }, { "_page", page.ToString() }, { "_size", DirPageSize.ToString() },
                { "_fetch_total", "1" }, { "_fetch_sub_dirs", "0" },
                { "_sort", "file_type:asc,updated_at:desc" },
                { "_fetch_full_path", "0" }, { "fetch_all_file", "1" },
                { "fetch_risk_file_name", "1" },
            };
            var res = await RequestAsync(HttpMethod.Get,
                QuarkConstants.ApiBase + "/1/clouddrive/file/sort", query);
            Check(res, "读取网盘列表");
            var one = new DirPage();
            var list = AsArray(Get(AsDict(Get(AsDict(res), "data")), "list"));
            if (list != null)
            {
                foreach (var it in list)
                    one.Items.Add(Normalize(it, share: false));
            }
            one.HasMore = PageHasMore(AsLong(Get(AsDict(Get(AsDict(res), "metadata")), "_total")),
                page, one.Items.Count);
            return one;
        }

        /// <summary>
        /// 这一页之后还有没有：空页肯定没有；接口给了 _total 就按它判
        /// （已取条数 + 本页条数 是否达到总数）；没给 _total 时退化成"整页取满就当还有"。
        /// </summary>
        private static bool PageHasMore(long total, int page, int added)
        {
            if (added <= 0) return false;
            if (total > 0) return (long)(page - 1) * DirPageSize + added < total;
            return added >= DirPageSize;
        }

        public async Task<string> SaveShareAsync(IList<string> fidList, IList<string> tokenList,
            string toPdirFid, string pwdId, string stoken)
        {
            var query = new Dictionary<string, string>
            {
                { "pr", "ucpro" }, { "fr", "pc" }, { "uc_param_str", "" }, { "app", "clouddrive" },
                { "__dt", Rng.Next(60000, 300001).ToString() },
                { "__t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() },
            };
            var body = new
            {
                fid_list = fidList,
                fid_token_list = tokenList,
                to_pdir_fid = toPdirFid,
                pwd_id = pwdId,
                stoken = stoken,
                pdir_fid = "0",
                scene = "link",
            };
            var res = await RequestAsync(HttpMethod.Post, QuarkConstants.ApiBase + "/1/clouddrive/share/sharepage/save", query, body);
            Check(res, "转存");
            string taskId = AsString(Get(AsDict(Get(AsDict(res), "data")), "task_id"));
            if (string.IsNullOrEmpty(taskId))
                throw new QuarkApiException("转存接口未返回任务ID");
            return taskId;
        }

        /// <param name="ct">
        /// 可选取消令牌。转存确认最长要轮询 300 秒，用户点了「终止入队」
        /// 应当能立刻停下，而不是干等完 —— 这个 token 由上层（MainWindow 的入队流程）传入。
        /// </param>
        public async Task<object> QueryTaskAsync(string taskId, int timeoutSeconds = 300,
            CancellationToken ct = default(CancellationToken))
        {
            int retry = 0;
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var query = new Dictionary<string, string>
                {
                    { "pr", "ucpro" }, { "fr", "pc" }, { "uc_param_str", "" },
                    { "task_id", taskId }, { "retry_index", retry.ToString() },
                    { "__dt", Rng.Next(60000, 300001).ToString() },
                    { "__t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() },
                };
                var res = await RequestAsync(HttpMethod.Get, QuarkConstants.ApiBase + "/1/clouddrive/task", query);
                Check(res, "查询转存任务");
                var data = AsDict(Get(AsDict(res), "data"));
                long status = AsLong(Get(data, "status"));

                // 成功：转存完成（status == 2）
                if (status == QuarkErrorCode.TaskStatusSuccess)
                    return data;

                // 终态失败检测。
                // 转存任务可能因「网盘容量不足」(业务码 32003) 等原因以失败收场，
                // 不能傻等满 timeoutSeconds 才报"转存任务超时"—— 那样用户要干等很久。
                // 一旦识别到失败终态就立即抛出，上层（SaveAndGetItemsAsync / 更新流程）
                // 据此降级为免转存或如实提示。
                // 判据（保守，绝不误判正常轮询）：
                //   · failed_code 非零 = 任务明确失败；
                //   · status 为负 = 失败（0=排队 1=进行中 2=成功，负数即失败）。
                long failedCode = AsLong(Get(data, "failed_code"));
                if (failedCode != 0 || status < 0)
                {
                    if (failedCode == QuarkErrorCode.CapacityInsufficient
                        || IsCapacityCode(failedCode)
                        || IsCapacityMessage(AsString(Get(data, "message"))))
                    {
                        throw new QuarkApiException(
                            "网盘容量不足，无法完成转存（请清理网盘空间或减少选择文件）",
                            failedCode, status);
                    }
                    throw new QuarkApiException(
                        string.Format("转存任务失败（code={0}, status={1}）", failedCode, status),
                        failedCode, status);
                }

                retry++;
                await Task.Delay(TimeSpan.FromMilliseconds(600), ct).ConfigureAwait(false);
            }
            throw new QuarkApiException("转存任务超时");
        }

        /// <summary>转存一批分享文件到网盘并等待完成，返回转存后得到的新 fid 列表（顺序与输入一致）。</summary>
        public async Task<List<string>> SaveShareAndGetFidsAsync(IList<string> fidList, IList<string> tokenList,
            string toPdirFid, string pwdId, string stoken, CancellationToken ct = default(CancellationToken))
        {
            string taskId = await SaveShareAsync(fidList, tokenList, toPdirFid, pwdId, stoken).ConfigureAwait(false);
            var data = await QueryTaskAsync(taskId, ct: ct).ConfigureAwait(false);
            var fids = new List<string>();
            var saveAs = AsDict(Get(AsDict(data), "save_as"));
            var arr = AsArray(Get(saveAs, "save_as_top_fids"));
            if (arr != null)
            {
                foreach (var f in arr)
                {
                    if (f is string s && !string.IsNullOrEmpty(s))
                        fids.Add(s);
                }
            }
            return fids;
        }

        // ---------------- 我的网盘 ----------------

        public async Task<List<QuarkFileEntry>> ListDirAsync(string pdirFid, int maxPages = 40)
        {
            var merged = new List<QuarkFileEntry>();
            int page = 1;
            while (page <= maxPages)
            {
                var query = new Dictionary<string, string>
                {
                    { "pr", "ucpro" }, { "fr", "pc" }, { "uc_param_str", "" },
                    { "pdir_fid", pdirFid }, { "_page", page.ToString() }, { "_size", "50" },
                    { "_fetch_total", "1" }, { "_fetch_sub_dirs", "0" },
                    { "_sort", "file_type:asc,updated_at:desc" },
                    { "_fetch_full_path", "0" }, { "fetch_all_file", "1" },
                    { "fetch_risk_file_name", "1" },
                };
                var res = await RequestAsync(HttpMethod.Get, QuarkConstants.ApiBase + "/1/clouddrive/file/sort", query);
                Check(res, "读取网盘列表");
                var list = AsArray(Get(AsDict(Get(AsDict(res), "data")), "list"));
                int added = 0;
                if (list != null)
                {
                    foreach (var it in list)
                    {
                        merged.Add(Normalize(it, share: false));
                        added++;
                    }
                }
                long total = AsLong(Get(AsDict(Get(AsDict(res), "metadata")), "_total"));
                if (added == 0 || (total > 0 && merged.Count >= total))
                    break;
                page++;
            }
            return merged;
        }

        public async Task<string> PathFidAsync(string path)
        {
            var res = await RequestAsync(HttpMethod.Post, QuarkConstants.ApiBase + "/1/clouddrive/file/info/path_list",
                new Dictionary<string, string> { { "pr", "ucpro" }, { "fr", "pc" } },
                new { file_path = new[] { path }, @namespace = "0" });
            Check(res, "查询目录");
            var arr = AsArray(Get(AsDict(res), "data"));
            if (arr != null)
            {
                foreach (var it in arr)
                {
                    var d = AsDict(it);
                    if (d != null && AsString(Get(d, "file_path")) == path)
                        return AsString(Get(d, "fid"));
                }
            }
            return null;
        }

        public async Task<string> MkdirAsync(string path)
        {
            var res = await RequestAsync(HttpMethod.Post, QuarkConstants.ApiBase + "/1/clouddrive/file",
                new Dictionary<string, string> { { "pr", "ucpro" }, { "fr", "pc" }, { "uc_param_str", "" } },
                new { pdir_fid = "0", file_name = "", dir_path = path, dir_init_lock = false });
            Check(res, "创建文件夹");
            string fid = AsString(Get(AsDict(Get(AsDict(res), "data")), "fid"));
            if (string.IsNullOrEmpty(fid))
                throw new QuarkApiException("创建文件夹失败: 未返回目录ID");
            return fid;
        }

        /// <summary>
        /// 取我的网盘直链。
        /// 策略：把所有官方入口都问一遍（而不是"第一个能用就用"），
        /// 再把候选用 <see cref="LinkQuality"/> 打分，挑质量最好的一组返回。
        /// 多入口的意义：不同入口返回的直链对应不同 CDN 节点与签名，
        /// 择优能明显减少下载中途 403/断流。
        /// 只在"全部入口都失败"时才抛异常，单个入口失败不影响整体。
        /// </summary>
        public async Task<List<DownloadLink>> GetDownloadAsync(IList<string> fids)
        {
            var attempts = new[]
            {
                // 入口 1：PC 客户端入口（常规最优）
                new Dictionary<string, string> { { "pr", "ucpro" }, { "fr", "pc" }, { "uc_param_str", "" } },
                // 入口 2：网页端入口（CDN 选择策略可能不同）
                new Dictionary<string, string> { { "pr", "ucpro" }, { "fr", "pc" }, { "uc_param_str", "" }, { "entry", "ft" } },
                // 入口 3：移动端入口（部分网络下反而更稳）
                new Dictionary<string, string> { { "pr", "ucpro" }, { "fr", "android" }, { "uc_param_str", "" } },
            };

            object lastRes = null;
            string lastErr = null;
            List<DownloadLink> best = null;

            foreach (var q in attempts)
            {
                try
                {
                    var res = await RequestAsync(HttpMethod.Post,
                        QuarkConstants.ApiBase + "/1/clouddrive/file/download", q,
                        new { fids = fids }).ConfigureAwait(false);
                    Check(res, "获取直链");
                    var list = ParseDownloadData(Get(AsDict(res), "data"));
                    if (IsUsable(list))
                    {
                        lastRes = res;   // 留作兜底
                        if (best == null || TotalScore(list) > TotalScore(best))
                            best = list;
                    }
                    else
                    {
                        lastRes = res;
                    }
                }
                catch (QuarkApiException ex)
                {
                    lastErr = DescribeLinkError(ex) ?? ex.Message;
                }
            }

            if (best != null)
                return best;
            if (lastRes != null)
                return ParseDownloadData(Get(AsDict(lastRes), "data"));
            throw new QuarkApiException(lastErr ?? "获取直链失败");
        }

        public async Task<List<DownloadLink>> GetShareDownloadAsync(IList<string> fids, IList<string> tokens,
            string pwdId, string stoken)
        {
            var attempts = new[]
            {
                new Dictionary<string, string> { { "entry", "ft" }, { "fr", "pc" }, { "pr", "ucpro" } },
                new Dictionary<string, string> { { "pr", "ucpro" }, { "fr", "pc" }, { "uc_param_str", "" } },
                new Dictionary<string, string> { { "pr", "ucpro" }, { "fr", "android" }, { "uc_param_str", "" } },
            };

            object lastRes = null;
            string lastErr = null;
            List<DownloadLink> best = null;

            foreach (var q in attempts)
            {
                try
                {
                    var res = await RequestAsync(HttpMethod.Post,
                        QuarkConstants.ApiBase + "/1/clouddrive/file/download", q,
                        new { fids = fids, fids_token = tokens, pwd_id = pwdId, stoken = stoken }).ConfigureAwait(false);
                    Check(res, "获取分享直链");
                    var list = ParseDownloadData(Get(AsDict(res), "data"));
                    if (IsUsable(list))
                    {
                        lastRes = res;
                        if (best == null || TotalScore(list) > TotalScore(best))
                            best = list;
                    }
                    else
                    {
                        lastRes = res;
                    }
                }
                catch (QuarkApiException ex)
                {
                    lastErr = DescribeLinkError(ex) ?? ex.Message;
                }
            }

            if (best != null)
                return best;
            if (lastRes != null)
                return ParseDownloadData(Get(AsDict(lastRes), "data"));
            throw new QuarkApiException(lastErr ?? "获取分享直链失败");
        }

        /// <summary>一组直链的总质量分（各条之和），用于在多个入口结果间择优。</summary>
        private static long TotalScore(List<DownloadLink> list)
        {
            long sum = 0;
            if (list == null)
                return sum;
            foreach (var l in list)
            {
                int s = LinkQuality.Score(l?.DownloadUrl);
                sum += s < 0 ? 0 : s;
            }
            return sum;
        }

        /// <summary>判断一批直链结果是否"可用"：至少有一条带非空 URL 且长度合理。</summary>
        private static bool IsUsable(List<DownloadLink> list)
        {
            if (list == null || list.Count == 0)
                return false;
            foreach (var l in list)
            {
                if (!string.IsNullOrEmpty(l.DownloadUrl) && l.DownloadUrl.Length > 20)
                    return true;
            }
            return false;
        }

        private static List<DownloadLink> ParseDownloadData(object data)
        {
            var result = new List<DownloadLink>();
            var arr = AsArray(data);
            if (arr != null)
            {
                foreach (var it in arr)
                    result.Add(NormalizeDownload(it));
            }
            else
            {
                var d = AsDict(data);
                if (d != null)
                {
                    var list = AsArray(Get(d, "list"));
                    if (list != null)
                    {
                        foreach (var it in list)
                            result.Add(NormalizeDownload(it));
                    }
                    else
                    {
                        var files = AsArray(Get(d, "files"));
                        if (files != null)
                            foreach (var it in files)
                                result.Add(NormalizeDownload(it));
                        else
                            result.Add(NormalizeDownload(d));
                    }
                }
            }
            return result;
        }

        // ---------------- 删除 / 回收站 / 分享 ----------------

        public async Task DeleteAsync(IList<string> fids)
        {
            var res = await RequestAsync(HttpMethod.Post, QuarkConstants.ApiBase + "/1/clouddrive/file/delete",
                new Dictionary<string, string> { { "pr", "ucpro" }, { "fr", "pc" }, { "uc_param_str", "" } },
                new { action_type = 2, filelist = fids, exclude_fids = new string[0] });
            Check(res, "删除文件");
        }

        public async Task PurgeRecycleAsync(IList<string> fids)
        {
            var fidset = new HashSet<string>(fids);
            var records = new List<string>();
            for (int page = 1; page <= 20; page++)
            {
                var res = await RequestAsync(HttpMethod.Get, QuarkConstants.ApiBase + "/1/clouddrive/file/recycle/list",
                    new Dictionary<string, string>
                    {
                        { "_page", page.ToString() }, { "_size", "50" },
                        { "pr", "ucpro" }, { "fr", "pc" }, { "uc_param_str", "" },
                    });
                Check(res, "读取回收站");
                var lst = AsArray(Get(AsDict(Get(AsDict(res), "data")), "list"));
                int cnt = 0;
                if (lst != null)
                {
                    foreach (var it in lst)
                    {
                        cnt++;
                        var d = AsDict(it);
                        string fid = d != null ? (AsString(Get(d, "fid")) ?? "") : "";
                        if (fidset.Contains(fid))
                        {
                            string rid = d != null ? AsString(Get(d, "record_id")) : null;
                            if (!string.IsNullOrEmpty(rid))
                                records.Add(rid);
                        }
                    }
                }
                if (cnt < 50)
                    break;
            }
            if (records.Count > 0)
            {
                var res = await RequestAsync(HttpMethod.Post, QuarkConstants.ApiBase + "/1/clouddrive/file/recycle/remove",
                    new Dictionary<string, string> { { "uc_param_str", "" }, { "fr", "pc" }, { "pr", "ucpro" } },
                    new { select_mode = 2, record_list = records });
                Check(res, "清理回收站");
            }
        }

        // ---------------- 归一化与 JSON 工具 ----------------

        private static QuarkFileEntry Normalize(object itObj, bool share)
        {
            var it = AsDict(itObj) ?? new Dictionary<string, object>();
            return new QuarkFileEntry
            {
                Fid = AsString(Get(it, "fid")) ?? "",
                Name = AsString(Get(it, "file_name")) ?? "",
                Size = AsLong(Get(it, "size")),
                IsDir = AsBool(Get(it, "dir")),
                Token = share ? (AsString(Get(it, "share_fid_token")) ?? "") : "",
            };
        }

        private static DownloadLink NormalizeDownload(object itObj)
        {
            var it = AsDict(itObj);
            if (it == null)
                return new DownloadLink();
            return new DownloadLink
            {
                Fid = AsString(Get(it, "fid")) ?? "",
                DownloadUrl = AsString(Get(it, "download_url")) ?? "",
                Name = AsString(Get(it, "file_name")) ?? "",
                Size = AsLong(Get(it, "size")),
            };
        }

        private static object Deserialize(string json)
            => new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json);

        private static string Serialize(object o)
            => new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(o);

        private static Dictionary<string, object> AsDict(object o)
            => o as Dictionary<string, object>;

        private static object[] AsArray(object o)
            => o as object[];

        private static object Get(Dictionary<string, object> d, string key)
        {
            if (d != null && d.TryGetValue(key, out var v))
                return v;
            return null;
        }

        private static string AsString(object o)
            => o as string;

        private static string AsStringOrNumber(object o)
        {
            if (o == null)
                return null;
            if (o is string s)
                return s;
            if (o is long || o is int || o is double || o is decimal || o is float)
                return Convert.ToString(o, CultureInfo.InvariantCulture);
            return null;
        }

        private static long AsLong(object o)
        {
            if (o == null)
                return 0;
            if (o is long)
                return (long)o;
            if (o is int)
                return (int)o;
            if (o is double)
                return (long)(double)o;
            if (o is decimal)
                return (long)(decimal)o;
            if (o is float)
                return (long)(float)o;
            if (o is string s)
            {
                long v;
                return long.TryParse(s, out v) ? v : 0;
            }
            return 0;
        }

        private static bool AsBool(object o)
        {
            if (o is bool b)
                return b;
            if (o is string s)
            {
                long v;
                if (long.TryParse(s, out v))
                    return v != 0;
                return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
            }
            return AsLong(o) != 0;
        }

        // ---------------- 会话保活 ----------------

        /// <summary>
        /// 登录态保活：低频访问一个轻量接口，让服务端保持会话活跃，
        /// 避免长时间闲置后 Cookie 被回收导致下次操作直接失败。
        /// 由调用方按较长周期（如 5-10 分钟）触发，不要高频调用。
        /// </summary>
        public async Task<bool> KeepAliveAsync()
        {
            try
            {
                await ListDirAsync("0", 1).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>快速校验 __puus 是否仍然有效（轻量、短超时）。</summary>
        private async Task<bool> QuickValidateAsync()
        {
            try
            {
                await RequestCoreAsync(HttpMethod.Get, QuarkConstants.AccountUrl,
                    new Dictionary<string, string> { { "fr", "pc" }, { "platform", "pc" } },
                    null, 10).ConfigureAwait(false);
                return true;
            }
            catch (QuarkApiException ex) when (ex.HttpStatus == 401 || ex.HttpStatus == 412)
            {
                return false;
            }
            catch
            {
                // 网络波动时再用 ListDir 做一次交叉验证，不轻易误判失效
                try
                {
                    await RequestCoreAsync(HttpMethod.Get,
                        QuarkConstants.ApiBase + "/1/clouddrive/file/sort",
                        new Dictionary<string, string>
                        {
                            { "pr", "ucpro" }, { "fr", "pc" },
                            { "pdir_fid", "0" }, { "_page", "1" }, { "_size", "1" },
                        }, null, 10).ConfigureAwait(false);
                    return true;
                }
                catch (QuarkApiException ex2) when (ex2.HttpStatus == 401 || ex2.HttpStatus == 412)
                {
                    return false;
                }
                catch
                {
                    // 两次都因网络失败，保守视为仍有效，避免网络抖动误杀
                    return true;
                }
            }
        }

        /// <summary>启动后台保活心跳。周期带随机抖动，避免固定节奏被识别。</summary>
        public void StartKeepAlive(int intervalMinutes = 5)
        {
            lock (_keepAliveLock)
            {
                _keepAliveFails = 0;      // 重新开始保活 → 失败计数归零
                StopKeepAliveLocked();
                // 首次触发：interval 的 50%–100% 随机（避免所有实例同时心跳）
                int dueMs = (int)(intervalMinutes * 60 * 1000 * (0.5 + 0.5 * Rng.NextDouble()));
                int periodMs = intervalMinutes * 60 * 1000;
                _keepAliveTimer = new Timer(_ => { var __ = KeepAliveTickAsync(); },
                    null, dueMs, periodMs);
            }
        }

        /// <summary>停止后台保活心跳。</summary>
        public void StopKeepAlive()
        {
            lock (_keepAliveLock)
            {
                _keepAliveFails = 0;      // 重新开始保活 → 失败计数归零
                StopKeepAliveLocked();
            }
        }

        private void StopKeepAliveLocked()
        {
            _keepAliveTimer?.Dispose();
            _keepAliveTimer = null;
        }

        /// <summary>
        /// 保活心跳**连续失败**到阈值时触发一次 —— 登录态很可能已经失效。
        /// 用户问「账号状态多久刷新一次」时发现：原来这里
        /// 失败**只静默记录**，于是登录真的过期了，界面还显示着昵称和绿色容量条，
        /// 用户完全不知道，直到某个操作撞到 401。现在会通知 UI 明确提示。
        /// ⚠️ 在**后台线程**上触发，订阅方必须自己切回 UI 线程。
        /// ⚠️ 只触发一次（随后停掉心跳），避免每 5 分钟骚扰一次。
        /// </summary>
        public event Action LoginExpired;

        /// <summary>连续失败几次才判定登录失效。单次失败可能只是网络抖动。</summary>
        private const int KeepAliveFailThreshold = 2;

        private int _keepAliveFails;

        private async Task KeepAliveTickAsync()
        {
            try
            {
                bool ok = await KeepAliveAsync().ConfigureAwait(false);
                if (ok)
                {
                    _keepAliveFails = 0;
                    return;
                }

                // ⚠️ 单次失败不算数：网络抖一下也会失败。连续 2 次才判定失效
                //    （与 ValidateAsync 里"两次都因网络失败保守视为仍有效"同一个思路）。
                _keepAliveFails++;
                if (_keepAliveFails >= KeepAliveFailThreshold)
                {
                    StopKeepAlive();      // 已经失效了就别再打了
                    try { LoginExpired?.Invoke(); } catch { }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            StopKeepAlive();
            _session?.Dispose();
        }
    }
}
