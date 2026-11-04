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
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Models;
using GeZi.Core.Support;
using GeZi.Core.Api;
using GeZi.Providers.Quark;

namespace GeZi.Providers.Uc
{
    /// <summary>
    /// UC 网盘客户端（含免转存取直链）。
    /// <para>
    /// <b>协议来源：</b>UC 与夸克同属阿里的 ucpro 后端，接口路径与参数完全一致
    /// （产品代号 UC 用 <c>pr=UCBrowser</c>，夸克用 <c>pr=ucpro</c>）。本实现照着已验证可用的
    /// <see cref="QuarkClient"/> 的分享链路写成，只替换域名、UA 与 Cookie 域。
    /// </para>
    /// <para>
    /// <b>免转存：</b><c>POST /1/clouddrive/file/download</c> 带上
    /// <c>pwd_id</c> + <c>stoken</c> + <c>fids</c> + <c>fids_token</c> 直接返回直链，
    /// 不需要先把文件保存到自己网盘。这是 UC 相对其它网盘最大的优势。
    /// </para>
    /// <para>
    /// <b>登录态：</b>只需要 Cookie <c>__puus</c>（与夸克同名，但域是 <c>.uc.cn</c>、值不同，
    /// 两者不能混用）。
    /// </para>
    /// </summary>
    public sealed class UcDriveClient : IPanProvider
    {
        private readonly QuarkSession _session;
        private readonly string _apiBase;
        private string _accountName = "";
        /// <summary>账号头像 URL（account/info 的 <c>avatarUri</c>），没设头像时为空串。</summary>
        private string _avatarUrl = "";
        private bool _disposed;

        /// <param name="cookie">登录 Cookie（需含 __puus）。</param>
        /// <param name="apiBase">
        /// API 根地址。默认用真实端点；传别的值可以让客户端指向本地 mock 服务器，
        /// 从而在**不需要账号**的情况下验证请求构造（路径、CSRF 头、JSON 字段）是否正确。
        /// </param>
        public UcDriveClient(string cookie = null, string apiBase = null)
        {
            _apiBase = string.IsNullOrEmpty(apiBase) ? UcConstants.ApiBase : apiBase.TrimEnd('/');
            _session = new QuarkSession(cookie, System.Net.DecompressionMethods.None, PanKind.Uc);
        }

        public PanKind Kind
        {
            get { return PanKind.Uc; }
        }

        public string DisplayName
        {
            get { return "UC网盘"; }
        }

        public PanCapabilities Capabilities
        {
            get
            {
                return new PanCapabilities
                {
                    TransferFree = true,
                    NeedsCaptcha = false,
                    NeedsSignature = false,
                    SupportsPasscode = true,
                    SupportsSubFolder = true,
                    RequiresLogin = true,
                    Note = "与夸克同一套后端；免转存取链只需登录 Cookie（__puus）。",
                };
            }
        }

        public bool IsLoggedIn
        {
            get { return _session != null && !string.IsNullOrEmpty(_session.BuildCookieString()); }
        }

        public string AccountName
        {
            get { return _accountName ?? ""; }
        }

        /// <summary>
        /// 账号头像 URL（account/info 的 <c>avatarUri</c>）。
        /// 该账号没上传过头像时服务端返回空串 → 界面回退"昵称首字"头像。
        /// </summary>
        public string AvatarUrl
        {
            get { return _avatarUrl ?? ""; }
        }

        /// <summary>当前会话的 Cookie 串（用于持久化）。</summary>
        public string CookieStr
        {
            get { return _session.BuildCookieString(); }
        }

        // ---------------- 分享 ----------------

        /// <summary>用分享 ID（+提取码）换 stoken。</summary>
        public async Task<string> GetStokenAsync(string shareId, string passcode,
            CancellationToken cancel = default)
        {
            var res = await RequestAsync(HttpMethod.Post,
                _apiBase + "/1/clouddrive/share/sharepage/token",
                new Dictionary<string, string> { { "pr", UcConstants.Pr }, { "fr", "pc" } },
                new { pwd_id = shareId, passcode = passcode ?? "" },
                cancel).ConfigureAwait(false);

            Check(res, "获取分享凭证");
            return PanJson.AsString(PanJson.GetPath(res, "data", "stoken")) ?? "";
        }

        /// <summary>
        /// 列出分享目录（免转存第一步）。
        /// 自动翻页直到取完；单页失败不会把已取到的结果丢掉。
        /// </summary>
        public async Task<IReadOnlyList<PanFileEntry>> ListShareAsync(
            PanShareRef shareRef, string parentFid, CancellationToken cancel = default)
        {
            if (shareRef == null)
                throw new QuarkApiException("分享引用为空。");

            string stoken = await GetStokenAsync(shareRef.ShareId, shareRef.Passcode, cancel)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(stoken))
                throw new QuarkApiException("分享链接无效或提取码错误（未取到分享凭证）。");

            var merged = new List<PanFileEntry>();
            for (int page = 1; page <= 200; page++)
            {
                var query = new Dictionary<string, string>
                {
                    { "pr", UcConstants.Pr }, { "fr", "pc" },
                    { "pwd_id", shareRef.ShareId }, { "stoken", stoken },
                    { "pdir_fid", string.IsNullOrEmpty(parentFid) ? "0" : parentFid },
                    { "force", "0" }, { "_page", page.ToString() },
                    { "_size", UcConstants.PageSize.ToString() },
                    { "_fetch_banner", "0" }, { "_fetch_share", "0" }, { "_fetch_total", "1" },
                    { "_sort", "file_type:asc,updated_at:desc" },
                    { "ver", "2" }, { "fetch_share_full_path", "0" },
                };

                // 实测：UC 的分享列表是 **POST** 到 /share/sharepage/v2/detail。
                // 用 GET 打 v2/detail 会返回 405 Method Not Allowed；
                // 用旧路径 /detail 返回 400（能认但列表语义不同）。
                // 依据 YunX 的 UCConstants.SHARE_DETAIL_URL。
                var body = new Dictionary<string, object>
                {
                    { "pwd_id", shareRef.ShareId },
                    { "stoken", stoken },
                    { "pdir_fid", string.IsNullOrEmpty(parentFid) ? "0" : parentFid },
                    { "force", "0" },
                    { "_page", page },
                    { "_size", UcConstants.PageSize },
                    { "_fetch_banner", "0" },
                    { "_fetch_share", "0" },
                    { "_fetch_total", "1" },
                    { "_sort", "file_type:asc,updated_at:desc" },
                    { "ver", "2" },
                    { "fetch_share_full_path", "0" },
                };

                var res = await RequestAsync(HttpMethod.Post,
                    _apiBase + "/1/clouddrive/share/sharepage/v2/detail", query, body, cancel)
                    .ConfigureAwait(false);
                Check(res, "读取分享列表");

                var list = PanJson.AsArray(PanJson.GetPath(res, "data", "list"));
                int added = 0;
                if (list != null)
                {
                    foreach (var it in list)
                    {
                        merged.Add(NormalizeEntry(it, share: true));
                        added++;
                    }
                }

                long total = PanJson.AsLong(PanJson.GetPath(res, "metadata", "_total"));
                if (added == 0 || (total > 0 && merged.Count >= total))
                    break;
            }

            return merged;
        }

        // ---------------- 免转存取直链 ----------------

        /// <summary>
        /// 免转存取直链：不保存到自己网盘，直接拿下载地址。
        /// <para>
        /// 多组查询参数依次尝试 —— 服务端不同入口对 <c>pr/fr/entry</c> 的校验有差异，
        /// 与夸克那边一致（夸克也是三组依次试）。任一组拿到可用直链即返回。
        /// </para>
        /// </summary>
        public async Task<IReadOnlyList<DownloadLink>> GetShareDownloadLinksAsync(
            PanShareRef shareRef, IList<PanFileEntry> files, CancellationToken cancel = default)
        {
            if (shareRef == null)
                throw new QuarkApiException("分享引用为空。");
            if (files == null || files.Count == 0)
                return new List<DownloadLink>();

            string stoken = await GetStokenAsync(shareRef.ShareId, shareRef.Passcode, cancel)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(stoken))
                throw new QuarkApiException("分享链接无效或提取码错误（未取到分享凭证）。");

            var fids = new List<string>();
            var tokens = new List<string>();
            foreach (var f in files)
            {
                fids.Add(f.Fid);
                tokens.Add(f.Token ?? "");
            }

            var attempts = new[]
            {
                new Dictionary<string, string> { { "entry", "ft" }, { "fr", "pc" }, { "pr", UcConstants.Pr } },
                new Dictionary<string, string> { { "pr", UcConstants.Pr }, { "fr", "pc" }, { "uc_param_str", "" } },
                new Dictionary<string, string> { { "pr", UcConstants.Pr }, { "fr", "android" }, { "uc_param_str", "" } },
            };

            List<DownloadLink> best = null;
            string lastErr = null;
            object lastRes = null;

            foreach (var q in attempts)
            {
                try
                {
                    var res = await RequestAsync(HttpMethod.Post,
                        _apiBase + "/1/clouddrive/file/download", q,
                        new { fids = fids, fids_token = tokens, pwd_id = shareRef.ShareId, stoken = stoken },
                        cancel).ConfigureAwait(false);

                    Check(res, "获取分享直链");
                    var parsed = ParseDownloadData(PanJson.Get(res, "data"));
                    if (IsUsable(parsed))
                    {
                        if (best == null || TotalScore(parsed) > TotalScore(best))
                            best = parsed;
                    }
                    lastRes = res;
                }
                catch (QuarkApiException ex)
                {
                    lastErr = ex.Message;
                }
            }

            if (best != null)
                return best;
            if (lastRes != null)
                return ParseDownloadData(PanJson.Get(lastRes, "data"));
            throw new QuarkApiException(lastErr ?? "获取 UC 分享直链失败。");
        }

        /// <summary>取自己网盘内文件的直链（已转存/自有文件，不需要 stoken）。</summary>
        public async Task<IReadOnlyList<DownloadLink>> GetOwnDownloadLinksAsync(
            IList<PanFileEntry> files, CancellationToken cancel = default)
        {
            if (files == null || files.Count == 0)
                return new List<DownloadLink>();

            var fids = new List<string>();
            foreach (var f in files)
                fids.Add(f.Fid);

            var res = await RequestAsync(HttpMethod.Post,
                _apiBase + "/1/clouddrive/file/download",
                new Dictionary<string, string> { { "pr", UcConstants.Pr }, { "fr", "pc" }, { "uc_param_str", "" } },
                new { fids = fids }, cancel).ConfigureAwait(false);

            Check(res, "获取直链");
            return ParseDownloadData(PanJson.Get(res, "data"));
        }

        // ---------------- CSRF ----------------

        private string _csrfToken;

        /// <summary>
        /// 取 CSRF token（可选）。
        /// <para>
        /// <b>实测结论：<c>pc-api.uc.cn</c> 上的业务接口不需要 CSRF。</b>
        /// 不带 <c>x-csrf-token</c> 请求 <c>sharepage/token</c>，服务端正常返回
        /// <c>{"code":41006,"message":"分享不存在"}</c>（业务错误，说明请求被接受）。
        /// </para>
        /// <para>
        /// 要求 CSRF 的是<b>网页主机</b> <c>drive.uc.cn</c>（返回
        /// <c>403 {"message":"missing csrf token"}</c>）—— 那是前端自己的路由，
        /// 不是我们要调的接口。所以这里**不再主动访问首页**（省一次请求），
        /// 只从用户粘贴的 Cookie 里捡一个 <c>ctoken</c>（有就带上，没有就算了）。
        /// </para>
        /// </summary>
        private Task<string> EnsureCsrfAsync(CancellationToken cancel)
        {
            if (!string.IsNullOrEmpty(_csrfToken))
                return Task.FromResult(_csrfToken);

            // 只从现有 Cookie 里取，不发请求
            _csrfToken = _session.GetCookieValue(UcConstants.CsrfCookieName) ?? "";
            if (string.IsNullOrEmpty(_csrfToken))
                _csrfToken = _session.GetCookieValue("csrf") ?? "";

            return Task.FromResult(_csrfToken);
        }

        /// <summary>
        /// 校验一段 Cookie 是否是有效的 UC 登录态，成功返回昵称。
        /// <para>
        /// <b>判据以「本地字段完整性」为主，网络校验只作辅助。</b>
        /// </para>
        /// <para>
        /// 早前这里要求 <c>account/info</c> 返回的 <c>data</c> 必须非空，结果
        /// <b>已登录的用户也被判成未登录</b>（实测：网页里明明显示了「UC网盘用户」，
        /// 程序却提示"data 为空"）。原因是 <c>account/info</c> 的返回结构并不稳定，
        /// 不同接口版本/不同账号状态下 <c>data</c> 可能为空对象，但它并不代表未登录。
        /// </para>
        /// <para>
        /// 参考同类项目 wenxi 的做法（<c>auth.dart</c>）：UC 与夸克一样，
        /// 判据就是 <b>Cookie 里同时有非空的 <c>__pus</c> 与 <c>__puus</c></b>，
        /// 不做任何网络请求。Cookie 到底能不能用，最终会在**取链时**暴露，
        /// 用登录窗口去严格拦截只会把好凭证误判成坏的。
        /// </para>
        /// </summary>
        public async Task<(bool ok, string nick, string error)> ValidateCookieAsync(
            string cookie, CancellationToken cancel = default)
        {
            if (string.IsNullOrWhiteSpace(cookie))
                return (false, "", "Cookie 为空。");

            // ① 本地判据（决定成败）：__pus 与 __puus 都要有且非空
            if (!LooksLikeCompleteUcCookie(cookie))
            {
                return (false, "",
                    "Cookie 不完整：需要同时包含非空的 " + UcConstants.CookiePus
                    + " 与 " + UcConstants.CookiePuus + "。");
            }

            // ② 网络校验（只用来**补充昵称**，不再用它否决凭证）
            //    任何异常都当作"拿不到昵称"，不影响登录判定。
            string nick = "";
            try
            {
                var acct = await TryFetchAccountAsync(cookie, cancel).ConfigureAwait(false);
                nick = acct.nick;
                _avatarUrl = acct.avatar ?? "";
            }
            catch
            {
                // 忽略：昵称拿不到不等于登录失败
            }

            if (string.IsNullOrEmpty(nick))
                nick = "UC网盘用户";

            _accountName = nick;
            return (true, nick, "");
        }

        // ---------------- 自有网盘浏览 ----------------
        // UC 的自有网盘实际上由 QuarkClient(Kind=Uc) 承担（同一套 ucpro 接口）。
        // 这里补上接口要求的三个成员，是为了让 UcDriveClient 作为 IPanProvider
        // 仍然自洽 —— 免得以后有人拿它去列自有目录时撞上 NotImplementedException。

        /// <summary>
        /// 校验登录态并取昵称（复用 <see cref="ValidateCookieAsync"/> 的判据）。
        /// </summary>
        public async Task<(bool ok, string nick)> ValidateAsync(CancellationToken cancel = default)
        {
            var (ok, nick, error) = await ValidateCookieAsync(CookieStr, cancel).ConfigureAwait(false);
            _lastError = ok ? "" : (error ?? "");
            return (ok, nick);
        }

        /// <summary>最近一次校验失败的原因（成功时为空串）。</summary>
        public string LastError
        {
            get { return _lastError ?? ""; }
        }

        private string _lastError = "";

        /// <summary>
        /// 列自己网盘目录。委托给 <c>QuarkClient(Kind=Uc)</c> —— UC 与夸克同协议，
        /// 没必要在这里重写一遍分页与错误映射。
        /// </summary>
        public async Task<IReadOnlyList<QuarkFileEntry>> ListOwnDirAsync(
            string parentId, CancellationToken cancel = default)
        {
            using (var q = new QuarkClient(CookieStr, PanKind.Uc))
            {
                return await q.ListDirAsync(string.IsNullOrEmpty(parentId) ? "0" : parentId)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>查容量用量（同样委托给 <c>QuarkClient(Kind=Uc)</c>）。</summary>
        public async Task<PanSpaceInfo> GetSpaceAsync(CancellationToken cancel = default)
        {
            try
            {
                using (var q = new QuarkClient(CookieStr, PanKind.Uc))
                {
                    var (total, used) = await q.GetCapacityAsync().ConfigureAwait(false);
                    return new PanSpaceInfo { Total = total, Used = used };
                }
            }
            catch
            {
                return new PanSpaceInfo();
            }
        }


        /// <summary>
        /// 取账号昵称 + 头像。同一个 account/info 请求里把 <c>avatarUri</c> 也带出来，
        /// 免得为了头像再发一次网络请求。
        /// 任何一步失败都返回空串（拿不到不等于登录失败）。
        /// </summary>
        private static async Task<(string nick, string avatar)> TryFetchAccountAsync(
            string cookie, CancellationToken cancel)
        {
            using (var session = new QuarkSession(cookie,
                System.Net.DecompressionMethods.None, PanKind.Uc))
            {
                // account/info 在 drive.uc.cn 上：既被 TLS 指纹拦（.NET 握手被 RST），
                // 又只认网页 UA（客户端 UA 稳定返回 AUTH_ERROR）。两个条件缺一不可。
                var hdrs = session.BuildHeadersForKind(UcConstants.AccountInfoUrl, api: true);
                var uri = new Uri(UcConstants.AccountInfoUrl);
                OpenSslTls.Response raw;
                try
                {
                    raw = await Task.Run(() => OpenSslTls.Send(
                        uri.Host, "GET", uri.PathAndQuery, hdrs, null, 15000))
                        .ConfigureAwait(false);
                }
                catch
                {
                    return ("", "");
                }

                if (raw.StatusCode < 200 || raw.StatusCode >= 300)
                    return ("", "");

                string text = raw.Body ?? "";
                if (LooksLikeHtml(text))
                    return ("", "");

                var obj = PanJson.Deserialize(text);
                var dict = PanJson.AsDict(PanJson.Get(obj, "data"));
                if (dict == null || dict.Count == 0)
                    return ("", "");

                string nick = PanJson.AsString(PanJson.Get(dict, "nickname"))
                    ?? PanJson.AsString(PanJson.Get(dict, "nick_name"))
                    ?? PanJson.AsString(PanJson.Get(dict, "name"))
                    ?? "";
                // 实测字段叫 avatarUri；该账号没设头像时是**空串**（不是缺字段），
                // 所以这里取到的空串要原样返回，交给界面回退首字头像。
                string avatar = PanJson.AsString(PanJson.Get(dict, "avatarUri"))
                    ?? PanJson.AsString(PanJson.Get(dict, "avatar_uri"))
                    ?? PanJson.AsString(PanJson.Get(dict, "avatar"))
                    ?? PanJson.AsString(PanJson.Get(dict, "avatar_url"))
                    ?? "";
                return (nick, avatar);
            }
        }

        /// <summary>
        /// 廉价预检：Cookie 里是否同时有 __pus 与 __puus。
        /// 不发网络请求，用于挡掉"登录过程中的中间态"。
        /// </summary>
        public static bool LooksLikeCompleteUcCookie(string cookie)
        {
            if (string.IsNullOrEmpty(cookie))
                return false;

            bool hasPus = false, hasPuus = false;
            foreach (var part in cookie.Split(';'))
            {
                string p = part.Trim();
                int eq = p.IndexOf('=');
                if (eq <= 0)
                    continue;
                string name = p.Substring(0, eq).Trim();
                // 值必须非空：登录过程中会先出现 "__puus=" 这种空值中间态，
                // 只判"名字存在"会把中间态当成登录成功。
                string value = eq + 1 < p.Length ? p.Substring(eq + 1).Trim() : "";
                if (value.Length == 0)
                    continue;

                if (string.Equals(name, UcConstants.CookiePus, StringComparison.OrdinalIgnoreCase))
                    hasPus = true;
                else if (string.Equals(name, UcConstants.CookiePuus, StringComparison.OrdinalIgnoreCase))
                    hasPuus = true;
            }
            return hasPus && hasPuus;
        }

        // ---------------- HTTP ----------------

        private async Task<object> RequestAsync(HttpMethod method, string url,
            Dictionary<string, string> query, object body, CancellationToken cancel)
        {
            string full = BuildUrl(url, query);

            // UC 的所有业务接口都要带 x-csrf-token（实测：缺了就是 403 missing csrf token）
            string csrf = await EnsureCsrfAsync(cancel).ConfigureAwait(false);

            // UC 的前置 ALB 按 TLS ClientHello 指纹过滤：同一份有效 Cookie，
            // OpenSSL 能拿到 200，.NET 的 SChannel 握手一律被 RST（表现为"发送请求时出错"）。
            // 所以 uc.cn 上的请求全部走自建 OpenSSL 通道。
            if (full != null && full.IndexOf("uc.cn", StringComparison.OrdinalIgnoreCase) >= 0)
                return await RequestViaOpenSslAsync(method, full, csrf, body).ConfigureAwait(false);

            using (var resp = await _session.SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(method, full);
                _session.ApplyHeadersForKind(req, full, api: true);
                if (!string.IsNullOrEmpty(csrf))
                    req.Headers.TryAddWithoutValidation(UcConstants.CsrfTokenHeader, csrf);
                if (body != null)
                {
                    string json = PanJson.Serialize(body);
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }
                return req;
            }, 3, cancel).ConfigureAwait(false))
            {
                string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    throw new QuarkApiException(BuildApiError("UC", resp, text));
                }

                var obj = PanJson.Deserialize(text);
                if (obj == null)
                {
                    // 服务端在登录态无效时会返回 HTML（登录页），而不是 JSON。
                    // 这种情况必须明确告诉用户"是登录态问题"，否则会被误判成接口坏了。
                    if (LooksLikeHtml(text))
                    {
                        throw new QuarkApiException(
                            "UC 网盘登录态无效或已过期（服务端返回了登录页而非数据）。" +
                            "请重新登录 drive.uc.cn 后更新 Cookie（需包含 __puus）。");
                    }
                    throw new QuarkApiException("UC 接口返回了无法解析的内容：" + Truncate(text, 200));
                }
                return obj;
            }
        }

        /// <summary>
        /// 走自建 OpenSSL 通道发一个 UC 接口请求。
        /// </summary>
        /// <remarks>
        /// <c>pc-api.uc.cn</c> 与 <c>pan.quark.cn</c> 同一套前置 ALB，只放行 OpenSSL 的
        /// ClientHello 指纹。这里把 Set-Cookie 合并回会话（只合并非空值，UC 鉴权失败时
        /// 会下发一串空值 Cookie，收下就等于把用户的有效凭证清空）。
        /// </remarks>
        private async Task<object> RequestViaOpenSslAsync(HttpMethod method, string full,
            string csrf, object body)
        {
            Uri uri = new Uri(full);
            var hdrs = _session.BuildHeadersForKind(full, api: true);
            if (!string.IsNullOrEmpty(csrf))
                hdrs[UcConstants.CsrfTokenHeader] = csrf;
            string json = body != null ? PanJson.Serialize(body) : null;
            if (json != null)
                hdrs["Content-Type"] = "application/json;charset=UTF-8";

            OpenSslTls.Response raw;
            try
            {
                // OpenSslTls 是同步阻塞实现，放到线程池上跑，别卡 UI 线程
                raw = await Task.Run(() => OpenSslTls.Send(
                    uri.Host, method.Method, uri.PathAndQuery, hdrs, json, 30000))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new QuarkApiException("网络请求失败: " + ex.Message);
            }

            foreach (var c in raw.Cookies)
            {
                if (string.IsNullOrEmpty(c.Key) || string.IsNullOrEmpty(c.Value))
                    continue;
                _session.SetCookie(c.Key, c.Value);
            }

            string text = raw.Body ?? "";
            if (raw.StatusCode < 200 || raw.StatusCode >= 300)
                throw new QuarkApiException(
                    "UC 接口返回 HTTP " + raw.StatusCode + "：" + Truncate(text, 200));

            var obj = PanJson.Deserialize(text);
            if (obj == null)
            {
                if (LooksLikeHtml(text))
                    throw new QuarkApiException(
                        "UC 网盘登录态无效或已过期（服务端返回了登录页而非数据）。" +
                        "请重新登录 drive.uc.cn 后更新 Cookie（需包含 __puus）。");
                throw new QuarkApiException("UC 接口返回了无法解析的内容：" + Truncate(text, 200));
            }
            return obj;
        }

        private static string BuildUrl(string baseUrl, Dictionary<string, string> query)
        {
            if (query == null || query.Count == 0)
                return baseUrl;

            var sb = new StringBuilder(baseUrl);
            char sep = baseUrl.IndexOf('?') >= 0 ? '&' : '?';
            foreach (var kv in query)
            {
                sb.Append(sep).Append(Uri.EscapeDataString(kv.Key))
                  .Append('=').Append(Uri.EscapeDataString(kv.Value ?? ""));
                sep = '&';
            }
            return sb.ToString();
        }

        /// <summary>校验返回码。UC 与夸克一样用 <c>code</c> 表达业务状态。</summary>
        private static object Check(object res, string what)
        {
            var d = PanJson.AsDict(res);
            if (d == null)
                throw new QuarkApiException(what + "失败：返回内容不是对象。");

            int code = PanJson.AsInt(PanJson.Get(d, "code"));
            // 部分接口把状态放在 status 里
            if (code == 0)
                code = PanJson.AsInt(PanJson.Get(d, "status"));

            if (code == 0 || code == 200)
                return res;

            string msg = PanJson.AsString(PanJson.Get(d, "message"))
                         ?? PanJson.AsString(PanJson.Get(d, "msg"))
                         ?? "";
            throw new QuarkApiException(string.Format("{0}失败（code={1}）：{2}", what, code, msg));
        }

        private static PanFileEntry NormalizeEntry(object it, bool share)
        {
            var d = PanJson.AsDict(it);
            if (d == null)
                return new PanFileEntry { Fid = "", Name = "", Size = 0, IsDir = false, Token = "" };

            return new PanFileEntry
            {
                Fid = PanJson.AsString(PanJson.Get(d, "fid"))
                      ?? PanJson.AsString(PanJson.Get(d, "file_id")) ?? "",
                Name = PanJson.AsString(PanJson.Get(d, "file_name"))
                       ?? PanJson.AsString(PanJson.Get(d, "name")) ?? "",
                Size = PanJson.AsLong(PanJson.Get(d, "size")),
                IsDir = PanJson.AsBool(PanJson.Get(d, "dir"))
                        || PanJson.AsBool(PanJson.Get(d, "is_dir"))
                        || PanJson.AsLong(PanJson.Get(d, "file_type")) == 0,
                Token = share
                    ? (PanJson.AsString(PanJson.Get(d, "share_fid_token"))
                       ?? PanJson.AsString(PanJson.Get(d, "fid_token")) ?? "")
                    : "",
                Md5 = PanJson.AsString(PanJson.Get(d, "md5")),
                Sha1 = PanJson.AsString(PanJson.Get(d, "sha1")),
                Sha256 = PanJson.AsString(PanJson.Get(d, "sha256")),
            };
        }

        private static List<DownloadLink> ParseDownloadData(object data)
        {
            var result = new List<DownloadLink>();
            var arr = PanJson.AsArray(data);
            if (arr != null)
            {
                foreach (var it in arr)
                    result.Add(NormalizeDownload(it));
                return result;
            }

            var d = PanJson.AsDict(data);
            if (d == null)
                return result;

            var list = PanJson.AsArray(PanJson.Get(d, "list"));
            if (list != null)
            {
                foreach (var it in list)
                    result.Add(NormalizeDownload(it));
                return result;
            }

            var files = PanJson.AsArray(PanJson.Get(d, "files"));
            if (files != null)
            {
                foreach (var it in files)
                    result.Add(NormalizeDownload(it));
                return result;
            }

            result.Add(NormalizeDownload(d));
            return result;
        }

        private static DownloadLink NormalizeDownload(object it)
        {
            var d = PanJson.AsDict(it);
            if (d == null)
                return new DownloadLink { Fid = "", DownloadUrl = "", Name = "", Size = 0 };

            return new DownloadLink
            {
                Fid = PanJson.AsString(PanJson.Get(d, "fid"))
                      ?? PanJson.AsString(PanJson.Get(d, "file_id")) ?? "",
                DownloadUrl = PanJson.AsString(PanJson.Get(d, "download_url"))
                              ?? PanJson.AsString(PanJson.Get(d, "url")) ?? "",
                Name = PanJson.AsString(PanJson.Get(d, "file_name"))
                       ?? PanJson.AsString(PanJson.Get(d, "name")) ?? "",
                Size = PanJson.AsLong(PanJson.Get(d, "size")),
                Md5 = PanJson.AsString(PanJson.Get(d, "md5")),
                Sha1 = PanJson.AsString(PanJson.Get(d, "sha1")),
                Sha256 = PanJson.AsString(PanJson.Get(d, "sha256")),
            };
        }

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

        private static long TotalScore(List<DownloadLink> list)
        {
            long sum = 0;
            if (list == null)
                return sum;
            foreach (var l in list)
            {
                int s = LinkQuality.Score(l != null ? l.DownloadUrl : null);
                sum += s < 0 ? 0 : s;
            }
            return sum;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>判断响应是不是 HTML（UC 登录态失效时会返回登录页）。</summary>
        private static bool LooksLikeHtml(string s)
        {
            if (string.IsNullOrEmpty(s))
                return false;
            string head = s.TrimStart();
            return head.StartsWith("<", StringComparison.Ordinal)
                || head.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                || head.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 把 UC 的错误响应整理成一句人话。
        /// UC 的错误体带一大坨 <c>metadata</c>（埋点信息），原样抛给用户毫无意义；
        /// 这里只取 <c>code</c> + <c>message</c>，并对常见码给出可操作提示。
        /// </summary>
        private static string BuildApiError(string who, HttpResponseMessage resp, string text)
        {
            int status = (int)resp.StatusCode;
            var obj = PanJson.Deserialize(text);
            string msg = PanJson.AsString(PanJson.Get(obj, "message"));
            int code = PanJson.AsInt(PanJson.Get(obj, "code"));
            if (code == 0)
                code = PanJson.AsInt(PanJson.Get(obj, "status"));

            if (LooksLikeHtml(text))
            {
                return string.Format(
                    "{0} 网盘登录态无效或已过期（服务端返回了网页而非数据）。" +
                    "请重新登录后更新 Cookie（需包含 __puus）。", who);
            }

            if (!string.IsNullOrEmpty(msg))
            {
                // 常见业务码 → 可操作提示
                string hint = null;
                if (code == 41006 || msg.IndexOf("分享不存在", StringComparison.Ordinal) >= 0)
                    hint = "分享链接已失效、被取消，或分享 ID 抄错了。";
                else if (code == 14001)
                    hint = "分享凭证无效或分享已失效，请重新复制分享链接。";
                else if (status == 401 || status == 403)
                    hint = "登录态无效或权限不足，请重新登录后更新 Cookie。";

                return string.Format("{0} 网盘返回错误（code={1}）：{2}{3}",
                    who, code, msg,
                    string.IsNullOrEmpty(hint) ? "" : "（" + hint + "）");
            }

            return string.Format("{0} 接口返回 HTTP {1}：{2}", who, status, Truncate(text, 200));
        }

        /// <summary>
        /// UC 分享**免转存**即可取链，不会在用户网盘里留下任何副本 —— 所以这里无事可做。
        /// 保留空实现是为了满足接口，让上层清理逻辑可以对四家网盘一视同仁地调用。
        /// </summary>
        public Task CleanupStoredAsync(IList<string> storedIds, CancellationToken cancel = default(CancellationToken))
        {
            return Task.FromResult(0);
        }

        /// <summary>
        /// 删除用户网盘里的文件。UC 与夸克同源，复用同一套 recycle/remove 接口。
        /// <para>⚠️ 主窗口的 UC 路径走的是 QuarkClient（不走这里），
        /// 这个方法只在「按 PanKind 创建 provider」的场景被调到。</para>
        /// </summary>
        public async Task DeleteFilesAsync(IList<string> fileIds, CancellationToken cancel = default(CancellationToken))
        {
            if (fileIds == null || fileIds.Count == 0)
                return;
            // 与 ListOwnDirAsync 同一个做法：临时建一个 QuarkClient(Kind=Uc) 干活，用完即弃。
            using (var q = new QuarkClient(CookieStr, PanKind.Uc))
            {
                await q.DeleteAsync(fileIds).ConfigureAwait(false);
                try { await q.PurgeRecycleAsync(fileIds).ConfigureAwait(false); } catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _session.Dispose(); } catch { }
        }
    }
}