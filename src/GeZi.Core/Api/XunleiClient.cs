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
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Models;
using GeZi.Core.Support;

namespace GeZi.Core.Api
{
    /// <summary>
    /// 迅雷云盘客户端（免转存取直链）。
    /// <para>
    /// <b>关于 <c>x-captcha-token</c>：</b>迅雷 pan 接口在部分情况下要求它。
    /// 早前本项目以为「该 token 只能由真人拖极验滑块产生」，因此把它做成半自动
    /// （用户手动粘贴）。<b>该结论已被实测推翻</b>：<c>POST /v1/shield/captcha/init</c>
    /// 用 alist 那套 10 层 md5 签名即可在无任何用户交互的情况下拿到有效 token
    /// （实测 HTTP 200、744 字符、<c>expires_in</c> 300 秒）。现在由
    /// <see cref="InitCaptchaAsync"/> 自动申请，失败时才回落到用户手动粘贴的值。
    /// </para>
    /// <para>
    /// 签名算法见 <see cref="XunleiProtocol.CaptchaSign"/>，协议对照
    /// <c>refs/YunX</c> 的 <c>XunleiApi.initPanCaptcha</c>。
    /// </para>
    /// <para>
    /// <b>免转存：</b>带上 share 凭证与 captcha token 调
    /// <c>GET /drive/v1/files/{file_id}</c>，响应里的 <c>web_content_link</c>
    /// 或 <c>medias[].link.url</c> 就是直链（通常落在 <c>lixian.xunlei.com</c> 镜像域）。
    /// 不需要先把文件转存到自己网盘。
    /// </para>
    /// <para>
    /// <b>诚实说明：</b>迅雷的风控是三家里最严、变动最频繁的。
    /// 本实现按公开协议写成，但**未经过真实账号验证**（缺少凭证）。
    /// 一旦服务端改了签名或验证码策略，这里会明确报错，而不是返回坏链接。
    /// </para>
    /// </summary>
    public sealed class XunleiClient : IPanProvider
    {
        private readonly HttpClient _http;
        private readonly HttpClientHandler _handler;
        private readonly string _apiBase;
        /// <summary>captcha 盾主机（默认官方地址；自检时指向 mock 服务器）。</summary>
        private readonly string _authBase;

        /// <summary>
        /// 网页路线取验证码的钩子（由 UI 层注入）。
        /// <para>
        /// 迅雷的 captcha 盾按 <c>client_id</c> 分体系：网页签发的 <c>pan_token</c>
        /// 必须配网页签发的 captcha。App 体系申请的会被判
        /// <c>captcha_invalid / detail="client_id not match"</c>。
        /// 网页签名是一层层混淆函数，纯算法复现不划算，所以交给 WebView2
        /// 让网页自己生成（refs/YunX 的做法）。
        /// </para>
        /// <para>返回 <c>(captcha, deviceId)</c>；<c>deviceId</c> 非空时替换当前设备标识。</para>
        /// </summary>
        public Func<Task<(string captcha, string deviceId)>> CaptchaRefresher;

        private readonly string _panToken;
        private string _captchaToken;
        private string _accountName = "";
        /// <summary>账号头像 URL（<c>/drive/v1/about</c> 里的头像字段），取不到为空串。</summary>
        private string _avatarUrl = "";
        /// <summary>最近一次校验失败的原因，供界面显示（成功时为空串）。</summary>
        private string _lastError = "";
        private string _deviceId;
        /// <summary>
        /// 请求里用的 <c>client_id</c>，从 <c>pan_token</c> 的 JWT <c>aud</c> 解析。
        /// <para>
        /// <b>实测：<c>client_id</c> 必须等于签发该 token 的客户端，硬编码会 400。</b>
        /// 网页登录可能用不同 clientId 签发 token，本机就同时存在
        /// <c>aud=Xqp0kJBXWhwaTpB6</c>（scope 含 sso）与 <c>aud=XW5SkOhLDjnOZP7J</c>
        /// （scope 含 offline）两种；拿前者去配后者的 token，服务端一律回
        /// <c>{"error":"captcha_invalid","error_code":9,...,"detail":"client_id not match"}</c>。
        /// </para>
        /// </summary>
        private string _clientId = "";
        /// <summary>
        /// 从 access_token(JWT) 解析出的用户 ID，<c>captcha/init</c> 的 meta 需要它。
        /// <para>服务端对空 user_id 会发「降级 captcha token」，POST 类接口会拒绝，
        /// 所以这里必须尽量带上真实值。</para>
        /// </summary>
        private string _userId = "";
        private bool _disposed;

        /// <param name="panToken">网页版登录态（Authorization: Bearer &lt;panToken&gt;）。</param>
        /// <param name="captchaToken">极验滑块验证通过后得到的 x-captcha-token，可为空。</param>
        /// <param name="apiBase">
        /// API 根地址（默认真实端点）。指向本地 mock 服务器即可在无账号情况下
        /// 验证请求构造与响应解析。
        /// </param>
        /// <param name="authBase">
        /// captcha 盾主机（默认官方地址）。自检时指向 mock 服务器，
        /// 用来验证「遇到 captcha_required 自动换 token 重试」这条链路。
        /// </param>
        public XunleiClient(string panToken = null, string captchaToken = null, string apiBase = null,
            string deviceId = null, string authBase = null)
        {
            _panToken = panToken ?? "";
            _captchaToken = captchaToken ?? "";
            _apiBase = string.IsNullOrEmpty(apiBase) ? XunleiConstants.ApiBase : apiBase.TrimEnd('/');
            _authBase = string.IsNullOrEmpty(authBase) ? XunleiConstants.AuthBase : authBase.TrimEnd('/');
            // 实测必需：不带 device_id 服务端直接报 "device_id is empty"。
            // 用稳定随机值（同一进程内复用），避免每次请求换设备触发风控。
            _deviceId = string.IsNullOrEmpty(deviceId)
                ? Guid.NewGuid().ToString("N")
                : deviceId;

            _handler = new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                MaxConnectionsPerServer = 32,
            };
            NetworkOptions.ApplyTo(_handler);
            _http = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(60) };
        }

        public PanKind Kind
        {
            get { return PanKind.Xunlei; }
        }

        public string DisplayName
        {
            get { return "迅雷云盘"; }
        }

        public PanCapabilities Capabilities
        {
            get
            {
                return new PanCapabilities
                {
                    TransferFree = true,
                    // captcha_token 现在由客户端自动申请；用户手动粘贴只是兜底
                    NeedsCaptcha = true,
                    NeedsSignature = false,
                    SupportsPasscode = true,
                    SupportsSubFolder = true,
                    RequiresLogin = true,
                    Note = "验证码 token 由客户端自动申请（captcha/init），无需用户操作；" +
                           "风控变动较频繁，失败时可在设置里手动粘贴兜底。",
                };
            }
        }

        public bool IsLoggedIn
        {
            get { return !string.IsNullOrEmpty(_panToken); }
        }

        public string AccountName
        {
            get { return _accountName ?? ""; }
        }

        /// <summary>
        /// 账号头像 URL。迅雷把头像放在 <c>/drive/v1/about</c> 的头像字段里
        /// （实测可能是 <c>icon</c> / <c>avatar</c> / <c>portrait</c> 之一，各家版本不一致），
        /// 取不到就是空串 → 界面回退昵称首字。
        /// </summary>
        public string AvatarUrl
        {
            get { return _avatarUrl ?? ""; }
        }

        /// <summary>设置/更新验证码 token（用户在浏览器过完滑块后粘贴）。</summary>
        public void SetCaptchaToken(string token)
        {
            _captchaToken = token ?? "";
        }

        public bool HasCaptchaToken
        {
            get { return !string.IsNullOrEmpty(_captchaToken); }
        }

        /// <summary>当前 pan_token（持久化用；调用方负责加密）。</summary>
        public string PanToken
        {
            get { return _panToken ?? ""; }
        }

        // ---------------- 分享列目录 ----------------

        /// <summary>
        /// 列出分享目录。迅雷的分享接口同时返回分享信息与文件列表。
        /// </summary>
        public async Task<IReadOnlyList<PanFileEntry>> ListShareAsync(
            PanShareRef shareRef, string parentFid, CancellationToken cancel = default)
        {
            if (shareRef == null)
                throw new QuarkApiException("分享引用为空。");

            var query = new Dictionary<string, string>
            {
                { "share_id", shareRef.ShareId },
                { "pass_code", shareRef.Passcode ?? "" },
                { "limit", XunleiConstants.PageSize.ToString() },
                { "parent_id", string.IsNullOrEmpty(parentFid) ? "" : parentFid },
                // 实测必需：缺 device_id 服务端报 "device_id is empty"
                { XunleiConstants.DeviceIdParam, _deviceId },
                { XunleiConstants.ClientIdParam, XunleiConstants.ClientId },
            };

            var res = await GetJsonAsync(_apiBase + "/drive/v1/share",
                query, cancel).ConfigureAwait(false);

            var files = PanJson.AsArray(PanJson.Get(res, "files"))
                        ?? PanJson.AsArray(PanJson.Get(res, "list"));
            if (files == null)
            {
                // 分享密码错误或链接失效时，接口会直接返回错误信息
                string msg = PanJson.AsString(PanJson.Get(res, "error_description"))
                             ?? PanJson.AsString(PanJson.Get(res, "message"))
                             ?? PanJson.AsString(PanJson.Get(res, "error")) ?? "未返回文件列表";
                throw new QuarkApiException("迅雷云盘读取分享失败：" + msg
                    + "（分享可能失效，或提取码错误）。");
            }

            var result = new List<PanFileEntry>();
            foreach (var it in files)
                result.Add(NormalizeEntry(it));
            return result;
        }

        // ---------------- 免转存取直链 ----------------

        /// <summary>
        /// 免转存取直链。需要 captcha token —— 没有就明确报错并提示用户怎么拿。
        /// </summary>
        public async Task<IReadOnlyList<DownloadLink>> GetShareDownloadLinksAsync(
            PanShareRef shareRef, IList<PanFileEntry> files, CancellationToken cancel = default)
        {
            if (files == null || files.Count == 0)
                return new List<DownloadLink>();

            // 不在这里拦「没有 captcha_token」——GetJsonAsync 遇到 captcha_required
            // 会自动申请。只有自动申请也失败时才会抛出，并带上服务端原始错误。


            var result = new List<DownloadLink>();
            string lastErr = null;

            foreach (var f in files)
            {
                try
                {
                    var res = await FetchFileDetailAsync(f.Fid, cancel).ConfigureAwait(false);

                    string url = ExtractDownloadUrl(res);
                    if (string.IsNullOrEmpty(url))
                    {
                        lastErr = string.Format("「{0}」未返回直链", f.Name);
                        continue;
                    }

                    result.Add(new DownloadLink
                    {
                        Fid = f.Fid,
                        DownloadUrl = url,
                        Name = f.Name,
                        Size = f.Size > 0 ? f.Size : PanJson.AsLong(PanJson.Get(res, "size")),
                        Md5 = f.Md5 ?? PanJson.AsString(PanJson.Get(res, "hash")),
                        Sha1 = f.Sha1,
                        Sha256 = f.Sha256,
                    });
                }
                catch (Exception ex)
                {
                    lastErr = string.Format("「{0}」取直链失败：{1}", f.Name, ex.Message);
                }
            }

            if (result.Count == 0 && !string.IsNullOrEmpty(lastErr))
                throw new QuarkApiException(lastErr);

            return result;
        }

        /// <summary>取自己网盘内文件的直链。</summary>
        public async Task<IReadOnlyList<DownloadLink>> GetOwnDownloadLinksAsync(
            IList<PanFileEntry> files, CancellationToken cancel = default)
        {
            if (files == null || files.Count == 0)
                return new List<DownloadLink>();

            var result = new List<DownloadLink>();
            foreach (var f in files)
            {
                var res = await FetchFileDetailAsync(f.Fid, cancel).ConfigureAwait(false);

                string url = ExtractDownloadUrl(res);
                if (string.IsNullOrEmpty(url))
                    continue;

                result.Add(new DownloadLink
                {
                    Fid = f.Fid,
                    DownloadUrl = url,
                    Name = f.Name,
                    Size = f.Size,
                    Md5 = f.Md5,
                    Sha1 = f.Sha1,
                    Sha256 = f.Sha256,
                });
            }
            return result;
        }

        /// <summary>
        /// 从文件详情里挖出直链。迅雷把地址放在两个可能的位置：
        /// ① <c>web_content_link</c>（网页直链，最常见）；
        /// ② <c>medias[].link.url</c>（多码率视频，取第一条）。
        /// </summary>
        private static string ExtractDownloadUrl(object res)
        {
            string direct = PanJson.AsString(PanJson.Get(res, "web_content_link"));
            if (!string.IsNullOrEmpty(direct))
                return direct;

            // 带 _magic/usage/with 参数取详情时，服务端会把「按用途分类」的链接放进 links，
            // 原始文件那条是 links["application/octet-stream"].url
            // （对照 refs/wenxi/lib/data/providers/xunlei.dart:697-701，那里也是先读它）。
            var links = PanJson.Get(res, "links");
            if (links != null)
            {
                string octet = PanJson.AsString(
                    PanJson.GetPath(links, "application/octet-stream", "url"));
                if (!string.IsNullOrEmpty(octet))
                    return octet;
            }

            var medias = PanJson.AsArray(PanJson.Get(res, "medias"));
            if (medias != null)
            {
                foreach (var m in medias)
                {
                    string u = PanJson.AsString(PanJson.GetPath(m, "link", "url"));
                    if (!string.IsNullOrEmpty(u))
                        return u;
                }
            }

            return PanJson.AsString(PanJson.Get(res, "download_url"));
        }

        // ---------------- 自有网盘浏览 ----------------

        /// <summary>
        /// 校验登录态并取昵称。
        /// <para>
        /// 用 <c>/drive/v1/about</c>：带合法 pan_token 时返回账号与空间信息，
        /// 失效时返回错误对象。这里**只看能否取到账号字段**，不额外要求 captcha
        /// —— 验证码只在取直链时才需要，不该拦住"能不能看到自己的网盘"。
        /// </para>
        /// </summary>
        /// <summary>最近一次校验失败的原因（成功时为空串）。</summary>
        public string LastError
        {
            get { return _lastError ?? ""; }
        }

        public async Task<(bool ok, string nick)> ValidateAsync(CancellationToken cancel = default)
        {
            if (string.IsNullOrEmpty(_panToken))
            {
                _lastError = "缺少 pan_token（网页里没读到，或粘贴为空）";
                return (false, "");
            }
            try
            {
                var res = await GetJsonAsync(_apiBase + "/drive/v1/about",
                    FileQuery(), cancel).ConfigureAwait(false);

                string name = PanJson.AsString(PanJson.Get(res, "name"))
                              ?? PanJson.AsString(PanJson.Get(res, "nick_name"))
                              ?? PanJson.AsString(PanJson.Get(res, "user_name")) ?? "";
                string err = PanJson.AsString(PanJson.Get(res, "error_description"))
                             ?? PanJson.AsString(PanJson.Get(res, "error"));
                if (!string.IsNullOrEmpty(err) && string.IsNullOrEmpty(name))
                {
                    _lastError = err;
                    return (false, "");
                }

                _accountName = name;
                // 头像：about 里可能直接给，也可能嵌在 user 对象里，两处都试一遍。
                var user = PanJson.Get(res, "user");
                string av = PanJson.AsString(PanJson.Get(res, "icon"))
                            ?? PanJson.AsString(PanJson.Get(res, "avatar"))
                            ?? PanJson.AsString(PanJson.Get(res, "avatar_url"))
                            ?? PanJson.AsString(PanJson.Get(res, "portrait"))
                            ?? PanJson.AsString(PanJson.Get(user, "icon"))
                            ?? PanJson.AsString(PanJson.Get(user, "avatar"))
                            ?? PanJson.AsString(PanJson.Get(user, "avatar_url"))
                            ?? PanJson.AsString(PanJson.Get(user, "portrait"));
                _avatarUrl = av ?? "";
                _lastError = "";
                return (true, string.IsNullOrEmpty(name) ? "迅雷用户" : name);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message ?? "";
                return (false, "");
            }
        }

        /// <summary>
        /// 列出自己网盘某个目录下的条目（<paramref name="parentId"/> 空 = 根目录）。
        /// 迅雷的根目录用空字符串表示（不是 "0"）。
        /// </summary>
        public async Task<IReadOnlyList<QuarkFileEntry>> ListOwnDirAsync(
            string parentId, CancellationToken cancel = default)
        {
            if (string.IsNullOrEmpty(_panToken))
                throw new QuarkApiException("迅雷云盘未登录：缺少 pan_token。");

            var result = new List<QuarkFileEntry>();
            long offset = 0;
            // 上限 200 页，防止服务端异常时死循环。
            for (int page = 0; page < 200; page++)
            {
                var query = FileQuery();
                // 根目录传空串；"0" 也按根处理（与夸克/UC 的约定对齐）
                query["parent_id"] = (string.IsNullOrEmpty(parentId) || parentId == "0")
                    ? "" : parentId;
                query["limit"] = XunleiConstants.PageSize.ToString();
                query["offset"] = offset.ToString();
                query["with_audit"] = "true";

                var res = await GetJsonAsync(_apiBase + "/drive/v1/files",
                    query, cancel).ConfigureAwait(false);

                var files = PanJson.AsArray(PanJson.Get(res, "files"))
                            ?? PanJson.AsArray(PanJson.Get(res, "list"));
                if (files == null)
                {
                    // 第一页就失败 → 说明真的取不到（未登录/风控），要报错而不是静默空列表
                    if (page == 0)
                    {
                        string msg = PanJson.AsString(PanJson.Get(res, "error_description"))
                                     ?? PanJson.AsString(PanJson.Get(res, "message"))
                                     ?? "未返回文件列表";
                        throw new QuarkApiException("迅雷云盘列目录失败：" + msg);
                    }
                    break;
                }

                foreach (var it in files)
                {
                    var e = NormalizeEntry(it);
                    result.Add(new QuarkFileEntry
                    {
                        Fid = e.Fid,
                        Name = e.Name,
                        Size = e.Size,
                        IsDir = e.IsDir,
                        Token = "",
                    });
                }

                if (files.Length < XunleiConstants.PageSize)
                    break;
                offset += files.Length;
            }
            return result;
        }

        /// <summary>
        /// 查容量用量。
        /// <para>
        /// 实测 <c>/drive/v1/about</c> 的真实返回（2026-10-24）：
        /// <code>
        /// {"kind":"drive#about",
        ///  "quota":{"kind":"drive#quota","limit":"10737418240","usage":"0",
        ///           "usage_in_trash":"0","is_unlimited":false,"upgrade_type":"user"},
        ///  "expires_at":"","quotas":{}}
        /// </code>
        /// 容量在 <c>quota.limit</c> / <c>quota.usage</c>，而且是**字符串**。
        /// 早先这里读 <c>space</c> / <c>space_used</c>（两个字段根本不存在），
        /// 取到的永远是 0，界面因此一直停在「容量获取中…」。
        /// </para>
        /// <para>
        /// 这里同时保留旧字段与 <c>quotas</c> 兜底：服务端换字段名时不至于又白屏。
        /// </para>
        /// </summary>
        public async Task<PanSpaceInfo> GetSpaceAsync(CancellationToken cancel = default)
        {
            try
            {
                if (string.IsNullOrEmpty(_panToken))
                    return new PanSpaceInfo();

                var res = await GetJsonAsync(_apiBase + "/drive/v1/about",
                    FileQuery(), cancel).ConfigureAwait(false);

                long total = PanJson.AsLong(PanJson.GetPath(res, "quota", "limit"));
                long used = PanJson.AsLong(PanJson.GetPath(res, "quota", "usage"));

                // 兜底 1：极老的返回把容量平铺在顶层
                if (total <= 0)
                    total = PanJson.AsLong(PanJson.Get(res, "space"));
                if (used <= 0)
                    used = PanJson.AsLong(PanJson.Get(res, "space_used"));

                // 兜底 2：多盘账号把各盘额度放在 quotas 字典里，取第一个有 limit 的
                if (total <= 0)
                {
                    var quotas = PanJson.Get(res, "quotas") as Dictionary<string, object>;
                    if (quotas != null)
                    {
                        foreach (var kv in quotas)
                        {
                            long ql = PanJson.AsLong(PanJson.GetPath(kv.Value, "limit"));
                            if (ql <= 0)
                                continue;
                            total += ql;
                            used += PanJson.AsLong(PanJson.GetPath(kv.Value, "usage"));
                        }
                    }
                }

                return new PanSpaceInfo { Used = used, Total = total };
            }
            catch
            {
                // 容量拿不到不该让账号卡片变成错误页
                return new PanSpaceInfo();
            }
        }

        // ---------------- HTTP ----------------

        /// <summary>取链/文件详情请求的公共查询参数（实测必需 device_id）。</summary>
        private Dictionary<string, string> FileQuery()
        {
            return new Dictionary<string, string>
            {
                { XunleiConstants.DeviceIdParam, _deviceId },
                { XunleiConstants.ClientIdParam, ResolveClientId() },
            };
        }

        /// <summary>
        /// 取直链（<c>GET /drive/v1/files/{file_id}</c>）专用的查询参数。
        /// <para>
        /// <b>这几个参数直接决定下载速度，实测差 10~13 倍</b>（同一文件、同一
        /// <c>fid</c>/<c>tid</c>/<c>mid</c>，A/B 交错各 3 轮，排除抖动）：
        /// </para>
        /// <list type="table">
        /// <item><description>只带 <c>device_id</c>+<c>client_id</c>：1 连接 0.02、8 连接 0.10、32 连接 0.10 MB/s（加并发完全不涨，被硬限速）</description></item>
        /// <item><description>再补上下面这四个：1 连接 0.14、8 连接 1.07、32 连接 1.31 MB/s（加并发会涨）</description></item>
        /// </list>
        /// <para>
        /// 差别不在文件也不在并发，而在服务端据此返回的 CDN 主机不同
        /// （<c>vod1284-aliyun06-vip-lixian</c> vs <c>vod1363-…</c>）以及 <c>pt</c> 取值。
        /// 参数取自 <c>refs/wenxi/lib/data/providers/xunlei.dart:684-701</c>。
        /// </para>
        /// </summary>
        private Dictionary<string, string> FileDetailQuery()
        {
            var q = FileQuery();
            q["_magic"] = "2021";
            q["usage"] = "PLAY";
            q["thumbnail_size"] = "SIZE_LARGE";
            q["with"] = "[\"hdr10\",\"subtitle_files\",\"task\",\"public_share_tag\"]";
            return q;
        }

        /// <summary>
        /// 取单个文件的详情（内含直链）。
        /// <para>
        /// 先按「加速参数」取；服务端对某些非媒体文件（如 <c>.dzip</c>）可能因为
        /// <c>usage=PLAY</c> 不返回可用链接，此时退回普通参数再取一次，
        /// 保证「加速失败也不至于取不到链接」。
        /// </para>
        /// </summary>
        private async Task<object> FetchFileDetailAsync(string fid, CancellationToken cancel)
        {
            string url = _apiBase + "/drive/v1/files/" + Uri.EscapeDataString(fid);

            object res = await GetJsonAsync(url, FileDetailQuery(), cancel).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(ExtractDownloadUrl(res)))
                return res;

            // usage=PLAY 没给出链接 → 退回不带加速参数的老写法
            return await GetJsonAsync(url, FileQuery(), cancel).ConfigureAwait(false);
        }

        private async Task<object> GetJsonAsync(string url, Dictionary<string, string> query,
            CancellationToken cancel)
        {
            string full = BuildUrl(url, query);

            // 第一次按当前 captcha_token 发；遇到 captcha_required / captcha_invalid
            // 就自动申请新 token 再发一次（对齐 refs/YunX 的 panCallInternal）。
            SendOutcome first = await SendOnceAsync(full, cancel).ConfigureAwait(false);
            if (!first.NeedsCaptcha)
                return first.Payload;

            // 首选网页路线：只有网页签发的 captcha 才认网页来的 pan_token。
            // 网页顺手给出它自己的 deviceid（wdi10.<64hex>），必须一起换，
            // 否则服务端判 "device not match"。
            string action = "GET:" + UrlPathOf(url);
            string fresh = null;

            // 🚨 captcha 盾按 client_id 分体系：网页 token 只认网页 captcha，
            // App（手机号账号登录）token 只认 App captcha。所以先走与 token 同体系的那条，
            // 失败再试另一条 —— 顺序反了就是「登录成功但列不了目录」。
            bool appToken = IsAppToken();

            if (appToken)
                fresh = await InitCaptchaAsync(action, cancel).ConfigureAwait(false);

            if (string.IsNullOrEmpty(fresh) && CaptchaRefresher != null)
            {
                try
                {
                    var got = await CaptchaRefresher().ConfigureAwait(false);
                    if (got.captcha != null && got.captcha.Length > 0)
                    {
                        fresh = got.captcha;
                        if (!string.IsNullOrEmpty(got.deviceId))
                        {
                            _deviceId = got.deviceId;
                            full = BuildUrl(url, query);
                        }
                    }
                }
                catch { }
            }

            // 另一条体系兜底（网页 token 落到这里说明网页 captcha 取不到，死马当活马医）
            if (string.IsNullOrEmpty(fresh) && !appToken)
                fresh = await InitCaptchaAsync(action, cancel).ConfigureAwait(false);

            if (string.IsNullOrEmpty(fresh))
                throw new QuarkApiException("迅雷云盘要求验证码，但自动申请 captcha_token 失败："
                    + Truncate(first.ErrorText, 200));

            _captchaToken = fresh;
            SendOutcome second = await SendOnceAsync(full, cancel).ConfigureAwait(false);
            if (!second.NeedsCaptcha)
                return second.Payload;

            throw new QuarkApiException("迅雷云盘验证码刷新后仍失败："
                + Truncate(second.ErrorText, 200));
        }

        /// <summary>一次 GET 的结果：成功则带 <see cref="Payload"/>，否则带错误信息。</summary>
        private sealed class SendOutcome
        {
            public object Payload;
            public bool NeedsCaptcha;
            public string ErrorText = "";
        }

        /// <summary>
        /// 发一次 GET 并判定结果。<c>captcha_required</c>/<c>captcha_invalid</c> 不抛异常，
        /// 而是打上 <see cref="SendOutcome.NeedsCaptcha"/> 让上层换 token 重试。
        /// </summary>
        private async Task<SendOutcome> SendOnceAsync(string full, CancellationToken cancel)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, full))
            {
                req.Headers.TryAddWithoutValidation("User-Agent", XunleiConstants.ApiUa);
                req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
                req.Headers.TryAddWithoutValidation("Referer", XunleiConstants.Referer);
                req.Headers.TryAddWithoutValidation("Origin", XunleiConstants.Origin);
                req.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
                // 设备标识同时放进请求头：captcha/init 与 pan 接口都按它认设备，
                // 必须与签发 token 的那台一致（网页登录时取自 deviceid Cookie）。
                // 注意：实测「加不加 X-Device-Id 都是 401」，它**不是** captcha_required
                // 的原因；真正的原因是缺 captcha_token，由 GetJsonAsync 自动补。
                if (!string.IsNullOrEmpty(_deviceId))
                    req.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);
                req.Headers.TryAddWithoutValidation("X-Client-Version", XunleiConstants.ClientVersion);

                if (!string.IsNullOrEmpty(_panToken))
                    req.Headers.TryAddWithoutValidation(
                        XunleiConstants.AuthorizationHeader, "Bearer " + _panToken);
                if (!string.IsNullOrEmpty(_captchaToken))
                    req.Headers.TryAddWithoutValidation(
                        XunleiConstants.CaptchaTokenHeader, _captchaToken);

                using (var resp = await _http.SendAsync(req, cancel).ConfigureAwait(false))
                {
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var outcome = new SendOutcome { ErrorText = text ?? "" };

                    var obj = PanJson.Deserialize(text);
                    string err = obj == null ? "" :
                        (PanJson.AsString(PanJson.Get(obj, "error")) ?? "");
                    if (err == "captcha_required" || err == "captcha_invalid")
                    {
                        outcome.NeedsCaptcha = true;
                        return outcome;
                    }

                    if (!resp.IsSuccessStatusCode)
                    {
                        throw new QuarkApiException(string.Format(
                            "迅雷云盘接口返回 HTTP {0}：{1}",
                            (int)resp.StatusCode, Truncate(text, 200)));
                    }

                    if (obj == null)
                        throw new QuarkApiException("迅雷云盘接口返回无法解析：" + Truncate(text, 200));

                    outcome.Payload = obj;
                    return outcome;
                }
            }
        }

        /// <summary>
        /// 申请一个新的 <c>captcha_token</c>。
        /// <para>
        /// 协议来自 refs/YunX 的 <c>initPanCaptcha</c>（其签名算法又与 alist 一致）：
        /// <c>meta.captcha_sign</c> = <see cref="XunleiProtocol.CaptchaSign"/>，
        /// <c>meta.user_id</c> 必须是真实用户（空值只发降级 token）。
        /// </para>
        /// <para>注意用 App 端 client_id —— 网页 client_id 不参与 captcha 盾。</para>
        /// </summary>
        private async Task<string> InitCaptchaAsync(string action, CancellationToken cancel)
        {
            try
            {
                string ts = XunleiProtocol.NowTimestamp();
                var body = new Dictionary<string, object>
                {
                    { "client_id", XunleiConstants.AppClientId },
                    { "action", action },
                    { "device_id", _deviceId },
                    { "redirect_uri", "xlaccsdk01://xunlei.com/callback?state=harbor" },
                    { "meta", new Dictionary<string, object>
                        {
                            { "client_version", XunleiConstants.AppVersion },
                            { "package_name", XunleiConstants.AppPackageName },
                            { "timestamp", ts },
                            { "captcha_sign", XunleiProtocol.CaptchaSign(_deviceId, ts) },
                            { "user_id", ResolveUserId() },
                        }
                    },
                    { "captcha_token", _captchaToken ?? "" },
                };

                using (var req = new HttpRequestMessage(HttpMethod.Post,
                    _authBase + "/v1/shield/captcha/init"))
                {
                    req.Headers.TryAddWithoutValidation("User-Agent", XunleiConstants.AppUa);
                    req.Headers.TryAddWithoutValidation("Accept", "application/json;charset=UTF-8");
                    req.Headers.TryAddWithoutValidation("X-Client-Id", XunleiConstants.AppClientId);
                    req.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);
                    req.Headers.TryAddWithoutValidation("X-Client-Version", XunleiConstants.AppVersion);
                    req.Content = new StringContent(PanJson.Serialize(body),
                        System.Text.Encoding.UTF8, "application/json");

                    using (var resp = await _http.SendAsync(req, cancel).ConfigureAwait(false))
                    {
                        string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var obj = PanJson.Deserialize(text);
                        return PanJson.AsString(PanJson.Get(obj, "captcha_token")) ?? "";
                    }
                }
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 从 access_token 的 JWT payload 里取 <c>sub</c>/<c>user_id</c>。
        /// <para>网页 token 是标准 JWT；解析失败返回空串（不抛异常，宁可降级也不阻断）。</para>
        /// </summary>
        private string ResolveUserId()
        {
            if (!string.IsNullOrEmpty(_userId))
                return _userId;
            try
            {
                var obj = DecodeJwtPayload(_panToken);
                _userId = PanJson.AsString(PanJson.Get(obj, "sub"))
                          ?? PanJson.AsString(PanJson.Get(obj, "user_id")) ?? "";
                return _userId;
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 当前 <c>pan_token</c> 是否来自 App（手机号账号登录）体系。
        /// <para>
        /// 用它决定 captcha 走哪套盾：App token 必须配 App 的 <c>captcha/init</c>
        /// （<see cref="InitCaptchaAsync"/>），网页 token 必须配网页自己写的 captcha。
        /// </para>
        /// </summary>
        private bool IsAppToken()
        {
            return string.Equals(ResolveClientId(), XunleiConstants.AppClientId,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// 从 access_token 的 JWT payload 里取 <c>aud</c> 当 <c>client_id</c>。
        /// <para>
        /// <b>为什么不能硬编码：</b>迅雷会校验 <c>client_id</c> 与 token 的 <c>aud</c> 是否一致，
        /// 不一致就回 <c>captcha_invalid / detail="client_id not match"</c>。
        /// 网页登录在不同入口会签发不同 aud 的 token（本机实测见过
        /// <c>Xqp0kJBXWhwaTpB6</c> 与 <c>XW5SkOhLDjnOZP7J</c> 两种），
        /// 所以必须用 token 自己声明的那个。
        /// </para>
        /// <para>
        /// 解析失败或 <c>aud</c> 为空时退回 <see cref="XunleiConstants.ClientId"/>（网页版默认值），
        /// 保证不会因为解析问题把请求打瘫。
        /// </para>
        /// </summary>
        private string ResolveClientId()
        {
            if (!string.IsNullOrEmpty(_clientId))
                return _clientId;
            try
            {
                var obj = DecodeJwtPayload(_panToken);
                string aud = PanJson.AsString(PanJson.Get(obj, "aud"));
                if (string.IsNullOrEmpty(aud))
                {
                    // aud 偶尔是数组，取第一个
                    var arr = PanJson.AsArray(PanJson.Get(obj, "aud"));
                    if (arr != null && arr.Length > 0)
                        aud = PanJson.AsString(arr[0]);
                }
                _clientId = string.IsNullOrEmpty(aud) ? XunleiConstants.ClientId : aud;
                return _clientId;
            }
            catch
            {
                _clientId = XunleiConstants.ClientId;
                return _clientId;
            }
        }

        /// <summary>解出 JWT 的 payload 对象；不是 JWT 或解析失败返回 <c>null</c>。</summary>
        private static object DecodeJwtPayload(string token)
        {
            string t = token ?? "";
            int d1 = t.IndexOf('.');
            if (d1 < 0) return null;
            int d2 = t.IndexOf('.', d1 + 1);
            if (d2 < 0) return null;

            string payload = t.Substring(d1 + 1, d2 - d1 - 1)
                              .Replace('-', '+').Replace('_', '/');
            int pad = payload.Length % 4;
            if (pad == 2) payload += "==";
            else if (pad == 3) payload += "=";

            string json = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(payload));
            return PanJson.Deserialize(json);
        }

        /// <summary>把完整 URL 还原成 <c>/drive/v1/about</c> 这种路径（captcha 的 action 要用）。</summary>
        private static string UrlPathOf(string url)
        {
            try
            {
                int i = url.IndexOf("://", StringComparison.Ordinal);
                if (i >= 0)
                {
                    int slash = url.IndexOf('/', i + 3);
                    if (slash < 0) return "/";
                    url = url.Substring(slash);
                }
                int q = url.IndexOf('?');
                if (q >= 0) url = url.Substring(0, q);
                return url;
            }
            catch
            {
                return "/";
            }
        }

        private static string BuildUrl(string baseUrl, Dictionary<string, string> query)
        {
            if (query == null || query.Count == 0)
                return baseUrl;

            var sb = new System.Text.StringBuilder(baseUrl);
            char sep = baseUrl.IndexOf('?') >= 0 ? '&' : '?';
            foreach (var kv in query)
            {
                if (string.IsNullOrEmpty(kv.Value))
                    continue;
                sb.Append(sep).Append(Uri.EscapeDataString(kv.Key))
                  .Append('=').Append(Uri.EscapeDataString(kv.Value));
                sep = '&';
            }
            return sb.ToString();
        }

        private static PanFileEntry NormalizeEntry(object it)
        {
            var d = PanJson.AsDict(it);
            if (d == null)
                return new PanFileEntry { Fid = "", Name = "", Size = 0 };

            string kind = PanJson.AsString(PanJson.Get(d, "kind")) ?? "";
            return new PanFileEntry
            {
                Fid = PanJson.AsString(PanJson.Get(d, "id"))
                      ?? PanJson.AsString(PanJson.Get(d, "file_id")) ?? "",
                Name = PanJson.AsString(PanJson.Get(d, "name")) ?? "",
                Size = PanJson.AsLong(PanJson.Get(d, "size")),
                IsDir = string.Equals(kind, "drive#folder", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(kind, "folder", StringComparison.OrdinalIgnoreCase),
                Token = PanJson.AsString(PanJson.Get(d, "id")) ?? "",
                Md5 = PanJson.AsString(PanJson.Get(d, "hash")),
            };
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
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
}