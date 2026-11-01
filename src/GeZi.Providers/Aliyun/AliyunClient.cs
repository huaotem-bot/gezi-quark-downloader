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
using GeZi.Core.Models;
using GeZi.Core.Support;
using GeZi.Core.Api;

namespace GeZi.Providers.Aliyun
{
    /// <summary>
    /// 阿里云盘客户端（含免转存取直链）。
    /// <para>
    /// <b>鉴权模型与夸克/UC 完全不同：</b>
    /// 阿里用 OAuth 的 <c>refresh_token</c>，先用它换 <c>access_token</c>，
    /// 再拿分享的 <c>share_token</c>；请求业务接口时带 <c>x-share-token</c> 头。
    /// </para>
    /// <para>
    /// <b>免转存链路：</b>
    /// ① <c>get_share_token</c>（share_id + 提取码）→ share_token；
    /// ② <c>list_by_anonymous</c> 列目录；
    /// ③ <c>get_share_link_download_url</c> 直接换下载地址 —— 全程不落自己网盘。
    /// </para>
    /// <para>
    /// <b>已知限制：</b>阿里的部分接口会校验 <c>x-signature</c> / <c>x-canary</c> 等风控头，
    /// 分享下载额度也受账号等级与分享者设置限制。若服务端开始强制签名，
    /// 这里会明确抛错而不是静默失败 —— 宁可见到"该网盘暂不可用"，也不要拿到坏链接。
    /// </para>
    /// </summary>
    public sealed class AliyunClient : IPanProvider
    {
        private readonly HttpClient _http;
        private readonly HttpClientHandler _handler;
        private readonly string _apiBase;
        private readonly string _authBase;

        private string _refreshToken;
        private string _accessToken;
        private string _driveId;

        /// <summary>
        /// 资源盘 id —— 用户网页版「全部文件」真正的所在盘。
        /// <para>
        /// 阿里改版后 <c>default_drive_id</c> 是备份盘（通常为空），
        /// 资源盘要靠 <c>/v2/user/driveMigrate</c> 补取，见
        /// <see cref="AliyunConstants.DriveMigratePath"/>。
        /// </para>
        /// </summary>
        private string _resourceDriveId;

        /// <summary>资源盘相关的非致命提示（取不到时记一笔，界面可提示）。</summary>
        private string _driveNote = "";
        private string _accountName = "";
        /// <summary>账号头像 URL（<c>/v2/user/get</c> 的 <c>avatar</c>），取不到为空串。</summary>
        private string _avatarUrl = "";
        private DateTime _accessTokenExpiresAt = DateTime.MinValue;

        /// <summary>
        /// 换 access_token 的互斥闸门。
        /// <para>
        /// 🚨 必须互斥：阿里的 refresh_token 是**单次有效、用一次就轮换**的
        /// （下面拿到 <c>rotated</c> 就会覆盖 <c>_refreshToken</c>）。
        /// 而 <see cref="EnsureAccessTokenAsync"/> 的入口检查是「令牌还没过期就直接返回」——
        /// 一旦过期，多个并发请求会**同时**判定为过期、各自拿**同一个**旧 refresh_token 去换，
        /// 服务端只认第一个，其余全部 <c>invalid_grant</c>；
        /// 更糟的是失败的那个会把 <c>_refreshToken</c> 覆盖成旧值，把刚轮换到的新值冲掉，
        /// 于是整个进程从此「登录失效」，只能重新登录。
        /// </para>
        /// <para>
        /// 所以这里用闸门 + **双重检查**：进闸门后先重新看一次令牌，别人已经换好了就直接用。
        /// </para>
        /// </summary>
        private readonly System.Threading.SemaphoreSlim _tokenGate =
            new System.Threading.SemaphoreSlim(1, 1);

        /// <summary>
        /// 网页直接给出的短期 access_token。
        /// <para>
        /// 阿里新版网页的 sessionStorage 里只有 access_token、没有 refresh_token，
        /// 拿不到续期凭据时只能先把这个短期令牌用起来（约 2 小时），
        /// 过期后提示用户重新登录。有 refresh_token 时该字段为空、走正常续期。
        /// </para>
        /// </summary>
        private readonly string _webAccessToken;

        /// <summary>最近一次校验失败的原因，供界面显示（成功时为空串）。</summary>
        private string _lastError = "";

        /// <summary>
        /// 最近一次「转存到自有网盘」失败的真实原因。
        /// <para>
        /// 🚨 这个字段存在的唯一目的就是**不再吞异常**。原先 <c>SaveShareFileAsync</c>
        /// 用裸 <c>catch { return (null, folderId); }</c> 把建目录 / copy 的
        /// HTTP 400/403 全部吃掉，界面上只剩一句误导性的
        /// 「没有取得直链（分享者可能关闭直链或游客额度限制）」，
        /// 导致「阿里转存到底错在哪」查了很久查不出来。
        /// </para>
        /// </summary>
        private string _lastSaveError = "";

        /// <summary>最近一次转存失败的原因（空 = 没有失败记录）。</summary>
        public string LastSaveError
        {
            get { return _lastSaveError ?? ""; }
        }

        // ---------------- 设备会话（secp256k1 签名）状态 ----------------
        //
        // 为什么需要：阿里新版对列目录/容量等接口强制校验设备签名。
        // 没有它，access_token 再对也只能拿到昵称头像，列目录一律
        // DeviceSessionSignatureInvalid —— 这正是「能登录、我的网盘却空白」的根因。

        /// <summary>设备 ID（32 位十六进制）。首次使用时生成，之后必须持久化复用。</summary>
        private string _deviceId = "";

        /// <summary>设备私钥（64 位十六进制 secp256k1）。与 <see cref="_deviceId"/> 成对持久化。</summary>
        private string _devicePrivateKey = "";

        /// <summary>设备会话签名（130 hex），建会话成功后复用，不必每请求重签。</summary>
        private string _deviceSignature = "";

        /// <summary>设备会话到期时间（服务端约 25 分钟）。</summary>
        private DateTime _deviceSessionExpiresAt = DateTime.MinValue;

        /// <summary>当前账号 user_id —— 设备签名摘要里要用，来自 /v2/user/get。</summary>
        private string _userId = "";

        private readonly Dictionary<string, string> _shareTokens =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private bool _disposed;

        /// <param name="refreshToken">OAuth refresh_token。</param>
        /// <param name="apiBase">
        /// 业务 API 根地址（默认真实端点）。指向本地 mock 服务器即可在无账号情况下
        /// 验证请求构造与响应解析。
        /// </param>
        /// <param name="authBase">OAuth 根地址（默认真实端点，同上可覆盖）。</param>
        /// <param name="accessToken">
        /// 网页登录直接给出的短期 access_token。仅在**没有** refresh_token 时使用：
        /// 阿里新版网页的 sessionStorage 里就只有它，没有续期凭据。
        /// </param>
        public AliyunClient(string refreshToken = null, string apiBase = null,
                            string authBase = null, string accessToken = null,
                            string deviceId = null, string devicePrivateKey = null)
        {
            _refreshToken = refreshToken ?? "";
            _webAccessToken = accessToken ?? "";
            _deviceId = deviceId ?? "";
            _devicePrivateKey = devicePrivateKey ?? "";
            if (!string.IsNullOrEmpty(_webAccessToken))
            {
                _accessToken = _webAccessToken;
                // 网页的 access_token 官方有效期约 2 小时；这里保守按 90 分钟，
                // 宁可提前失效去提示重新登录，也不要拿着快过期的令牌硬跑。
                _accessTokenExpiresAt = DateTime.UtcNow.AddMinutes(90);
            }
            _apiBase = string.IsNullOrEmpty(apiBase) ? AliyunConstants.ApiBase : apiBase.TrimEnd('/');
            _authBase = string.IsNullOrEmpty(authBase) ? AliyunConstants.AuthBase : authBase.TrimEnd('/');

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
            get { return PanKind.Aliyun; }
        }

        public string DisplayName
        {
            get { return "阿里云盘"; }
        }

        public PanCapabilities Capabilities
        {
            get
            {
                return new PanCapabilities
                {
                    TransferFree = true,
                    NeedsCaptcha = false,
                    // 阿里对部分接口有 x-signature 风控；分享下载链路目前不需要，
                    // 但服务端随时可能加严 —— 如实标注，别让用户以为永远稳。
                    NeedsSignature = false,
                    SupportsPasscode = true,
                    SupportsSubFolder = true,
                    RequiresLogin = true,
                    Note = "用 refresh_token 鉴权；免转存走 get_share_link_download_url。" +
                           "受账号等级与分享者设置限制，部分分享可能取不到直链。",
                };
            }
        }

        public bool IsLoggedIn
        {
            // 网页登录只给 access_token（无 refresh_token），这也算已登录：
            // 判据只看 refresh_token 会让「网页登录成功」被当成未登录，
            // 表现为解析分享直接失败。
            get
            {
                return !string.IsNullOrEmpty(_refreshToken)
                    || !string.IsNullOrEmpty(_webAccessToken);
            }
        }

        public string AccountName
        {
            get { return _accountName ?? ""; }
        }

        /// <summary>
        /// 账号头像 URL。<c>/v2/user/get</c> 的 <c>avatar</c> 字段（形如
        /// <c>https://cdn.aliyundrive.net/…</c>），该账号没设头像时为空串。
        /// </summary>
        public string AvatarUrl
        {
            get { return _avatarUrl ?? ""; }
        }

        /// <summary>当前 refresh_token（用于持久化；调用方负责加密）。</summary>
        /// <summary>当前短期 access_token（供界面/持久化读取，可能为空）。</summary>
        public string AccessToken
        {
            get { return _accessToken ?? ""; }
        }

        /// <summary>是否只持有短期令牌（没有 refresh_token，无法自动续期）。</summary>
        public bool IsShortLivedOnly
        {
            get { return string.IsNullOrEmpty(_refreshToken) && !string.IsNullOrEmpty(_webAccessToken); }
        }

        /// <summary>最近一次校验失败的原因（成功时为空串）。</summary>
        public string LastError
        {
            get { return _lastError ?? ""; }
        }

        public string RefreshToken
        {
            get { return _refreshToken ?? ""; }
        }

        /// <summary>
        /// 设备 ID（32 hex）。首次使用时自动生成，调用方应持久化后回传，
        /// 否则每次启动都会被服务端当成新设备（官网「已登录设备」列表会堆积）。
        /// </summary>
        public string DeviceId
        {
            get { return _deviceId ?? ""; }
        }

        /// <summary>设备私钥（64 hex）。与 <see cref="DeviceId"/> 成对持久化（调用方负责加密）。</summary>
        public string DevicePrivateKey
        {
            get { return _devicePrivateKey ?? ""; }
        }

        /// <summary>
        /// 最近一次登录校验时设备会话的建立结果。
        /// <para>
        /// 空串 = 设备会话正常（列目录/查容量可用）；
        /// 非空 = 建不起来，内容即原因 —— 此时账号仍算登录成功
        /// （分享下载不需要设备签名），但「我的网盘」会受限，界面应把它显示出来。
        /// </para>
        /// </summary>
        public string DeviceSessionNote { get; private set; } = "";

        /// <summary>当前账号 user_id（设备签名摘要需要；校验成功后才有值）。</summary>
        public string UserId
        {
            get { return _userId ?? ""; }
        }

        /// <summary>当前 drive_id（列自有网盘必需；校验成功后才有值）。</summary>
        public string DriveId
        {
            get { return _driveId ?? ""; }
        }

        /// <summary>资源盘 id（用户「全部文件」所在盘）；空表示未取到。</summary>
        public string ResourceDriveId
        {
            get { return _resourceDriveId ?? ""; }
        }

        /// <summary>资源盘相关提示（空 = 正常）。</summary>
        public string DriveNote
        {
            get { return _driveNote ?? ""; }
            set { _driveNote = value ?? ""; }
        }

        /// <summary>
        /// 列目录 / 取直链应当使用的 drive_id：<b>资源盘优先</b>，取不到才回退备份盘。
        /// <para>
        /// 必须用同一个盘，否则「列出来的文件」和「取直链的文件」分属不同盘会全部失败。
        /// </para>
        /// </summary>
        private string OwnDriveId
        {
            get
            {
                if (!string.IsNullOrEmpty(_resourceDriveId))
                    return _resourceDriveId;
                return _driveId ?? "";
            }
        }

        /// <summary>
        /// 取 <see cref="OwnDriveId"/>，但**空值时直接抛中文异常**。
        /// <para>
        /// 🚨 以前是把空串塞进请求体的 <c>drive_id</c>，服务端回
        /// <c>400 InvalidRequestJSONFormat</c>——报错完全不提是盘 id 没取到，
        /// 看着像「接口挂了」，实际只是这次客户端没走过登录校验。
        /// 这里在本地就拦下来，说清是哪个盘 id 缺了、该怎么办。
        /// </para>
        /// <para>
        /// 需要「先判断再决定是否补校验」的地方（比如转存前补一次
        /// <see cref="ValidateAsync"/>）继续用 <see cref="OwnDriveId"/>，别用这个。
        /// </para>
        /// </summary>
        private string RequireOwnDriveId()
        {
            string id = OwnDriveId;
            if (string.IsNullOrEmpty(id))
                throw new QuarkApiException(
                    "阿里云盘没有取到盘 id（资源盘和备份盘都为空），无法继续。"
                    + "请点「验证登录态」；仍失败请重新登录。");
            return id;
        }

        // ---------------- 鉴权 ----------------

        /// <summary>
        /// 用 refresh_token 换 access_token。结果缓存到过期为止。
        /// 阿里会轮换 refresh_token —— 拿到新的必须回写，否则下次登录态失效。
        /// <para>
        /// 新版网页换 token 走 <c>open.aliyundrive.com/oauth/access_token</c>
        /// （标准 OAuth，要带固定 <c>client_id</c>），旧端点
        /// <c>auth.aliyundrive.com/v2/account/token</c> 仍可用。两个都试，
        /// 优先新版：官方页面已经只走新版，旧端点随时可能下线。
        /// </para>
        /// </summary>
        public async Task EnsureAccessTokenAsync(CancellationToken cancel = default)
        {
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _accessTokenExpiresAt)
                return;
            if (string.IsNullOrEmpty(_refreshToken))
            {
                // 只有短期 access_token 且已过期：没有续期凭据，只能请用户重登。
                if (!string.IsNullOrEmpty(_webAccessToken))
                    throw new QuarkApiException(
                        "阿里云盘登录已过期：网页只提供了短期令牌（无 refresh_token），请重新登录。");
                throw new QuarkApiException("阿里云盘未登录：缺少 refresh_token。");
            }

            // 并发换 token 会互相踩（refresh_token 单次有效），必须串行化。
            await _tokenGate.WaitAsync(cancel).ConfigureAwait(false);
            bool entered = true;

            try
            {
                // 双重检查：等闸门期间可能已经有别的调用换好了。
                if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _accessTokenExpiresAt)
                    return;

                object res = null;
                string lastError = null;

                // 1) 新版 OAuth 端点（官方页面当前使用）
                try
                {
                    var oauthBody = new Dictionary<string, object>
                    {
                        { "client_id", AliyunConstants.WebClientId },
                        { "grant_type", "refresh_token" },
                        { "refresh_token", _refreshToken },
                    };
                    res = await PostJsonAsync(AliyunConstants.OpenAuthBase + "/oauth/access_token",
                        oauthBody, null, cancel, allowTokenRefresh: false).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(PanJson.AsString(PanJson.Get(res, "access_token"))))
                        res = null;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    res = null;
                }

                // 2) 旧端点回退
                if (res == null)
                {
                    try
                    {
                        var body = new Dictionary<string, object>
                        {
                            { "refresh_token", _refreshToken },
                            { "grant_type", "refresh_token" },
                        };
                        res = await PostJsonAsync(_authBase + "/v2/account/token",
                            body, null, cancel, allowTokenRefresh: false).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(PanJson.AsString(PanJson.Get(res, "access_token"))))
                            res = null;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        res = null;
                    }
                }

                string newAccess = res == null ? null : PanJson.AsString(PanJson.Get(res, "access_token"));
                if (string.IsNullOrEmpty(newAccess))
                {
                    string msg = res == null
                        ? (lastError ?? "换 token 请求失败")
                        : (PanJson.AsString(PanJson.Get(res, "message"))
                           ?? PanJson.AsString(PanJson.Get(res, "error_description")) ?? "未知错误");
                    throw new QuarkApiException("阿里云盘刷新登录态失败：" + msg
                        + "（refresh_token 可能已过期，需重新登录）。");
                }

                _accessToken = newAccess;

                // 服务端可能下发新的 refresh_token，必须接住
                string rotated = PanJson.AsString(PanJson.Get(res, "refresh_token"));
                if (!string.IsNullOrEmpty(rotated))
                    _refreshToken = rotated;

                string driveId = PanJson.AsString(PanJson.Get(res, "default_drive_id"));
                if (!string.IsNullOrEmpty(driveId))
                    _driveId = driveId;

                long expiresIn = PanJson.AsLong(PanJson.Get(res, "expires_in"));
                // 提前 5 分钟过期，避免边界上拿到刚失效的 token
                _accessTokenExpiresAt = expiresIn > 300
                    ? DateTime.UtcNow.AddSeconds(expiresIn - 300)
                    : DateTime.UtcNow.AddSeconds(60);
            }
            finally
            {
                if (entered)
                {
                    try { _tokenGate.Release(); } catch { }
                }
            }
        }

        /// <summary>校验登录态并取昵称。</summary>
        public async Task<(bool ok, string nick)> ValidateAsync(CancellationToken cancel = default)
        {
            try
            {
                await EnsureAccessTokenAsync(cancel).ConfigureAwait(false);
                _driveNote = "";
                var res = await PostJsonAsync(_apiBase + "/v2/user/get",
                    new Dictionary<string, object>(), _accessToken, cancel).ConfigureAwait(false);

                string nick = PanJson.AsString(PanJson.Get(res, "nick_name"))
                              ?? PanJson.AsString(PanJson.Get(res, "name")) ?? "";
                _accountName = nick;
                // 头像：实测字段叫 avatar（绝对 URL）；顺带兼容几个别名，
                // 万一官方改名也不用再改一次。取不到就留空 → 界面回退首字头像。
                string av = PanJson.AsString(PanJson.Get(res, "avatar"))
                            ?? PanJson.AsString(PanJson.Get(res, "avatar_url"))
                            ?? PanJson.AsString(PanJson.Get(res, "avatarUrl"))
                            ?? PanJson.AsString(PanJson.Get(res, "icon"));
                _avatarUrl = av ?? "";

                // 设备签名要用 user_id，列自有网盘要用 default_drive_id ——
                // 两者都藏在这个响应里，漏取会让后续所有签名/列目录请求失败。
                string uid = PanJson.AsString(PanJson.Get(res, "user_id")) ?? "";
                if (!string.IsNullOrEmpty(uid))
                    _userId = uid;

                string did = PanJson.AsString(PanJson.Get(res, "default_drive_id")) ?? "";
                if (!string.IsNullOrEmpty(did))
                    _driveId = did;

                // 阿里改版后 default_drive_id 指向「备份盘」，用户「全部文件」在「资源盘」。
                // 照官方 fetchUser 的兜底：user/get 没给 resource_drive_id 就调 driveMigrate 补拿。
                // 少了这一步，「我的网盘」永远只能看到空的备份盘（=「登录成功但没有文件」）。
                string rid = PanJson.AsString(PanJson.Get(res, "resource_drive_id")) ?? "";
                if (string.IsNullOrEmpty(rid))
                {
                    try
                    {
                        var mig = await PostJsonAsync(
                            AliyunConstants.UserBase + AliyunConstants.DriveMigratePath,
                            new Dictionary<string, object>(), _accessToken, cancel).ConfigureAwait(false);
                        rid = PanJson.AsString(PanJson.Get(mig, "resource_drive_id")) ?? "";
                        string bak = PanJson.AsString(PanJson.Get(mig, "backup_drive_id")) ?? "";
                        if (!string.IsNullOrEmpty(bak))
                            _driveId = bak;
                    }
                    catch (Exception ex)
                    {
                        _driveNote = "资源盘 id 未取到：" + (ex.Message ?? "");
                    }
                }
                if (!string.IsNullOrEmpty(rid))
                    _resourceDriveId = rid;

                // 顺手把设备会话建起来（best-effort）。
                // 登录校验本身不需要它（/v2/user/get 不校验设备签名），
                // 但「我的网盘」列目录与查容量都要 —— 在这里先试一次，
                // 成功就省掉进网盘时的等待，失败也只记一笔不阻断登录
                // （分享下载不需要设备签名，不该因为签名失败就登不进来）。
                DeviceSessionNote = "";
                try
                {
                    await EnsureDeviceSessionAsync(cancel).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 资源盘提示更关键，别被设备会话的错误盖掉
                    if (string.IsNullOrEmpty(_driveNote))
                        DeviceSessionNote = ex.Message ?? "";
                }

                _lastError = "";
                return (true, nick);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                // 调用方主动取消（切账号/关窗口）—— 不是登录态问题，原样抛给上层。
                throw;
            }
            catch (OperationCanceledException)
            {
                // 不是我们取消的 → HttpClient 超时（.NET Framework 里超时也抛
                // TaskCanceledException，消息是中文的"已取消一个任务。"）。
                // 这是网络问题，不是令牌失效。
                _lastError = "校验超时（网络慢或连不上阿里云盘接口），请稍后重试";
                return (false, "");
            }
            catch (Exception ex)
            {
                // 不再静默：失败原因留给界面显示（此前 catch {} 吞掉一切，
                // 用户点「验证登录态」只会看到界面毫无反应）。
                _lastError = ex.Message ?? "";
                return (false, "");
            }
        }

        // ---------------- 设备会话 ----------------

        /// <summary>
        /// 确保设备会话有效（必要时 <c>create_session</c> 重签）。
        /// <para>
        /// 阿里新版对 <c>/adrive/v3/file/list</c>、容量查询等接口校验设备签名：
        /// 请求头要带 <c>X-Device-Id</c> + <c>X-Signature</c>（+ <c>X-Canary</c>），
        /// 签名覆盖 <c>sha256(appId:deviceId:userId:nonce)</c>。
        /// 会话约 25 分钟有效，提前 1 分钟重签。
        /// </para>
        /// <para>
        /// 首次调用会生成并保存 <see cref="DeviceId"/> / <see cref="DevicePrivateKey"/>；
        /// 调用方应把它们持久化后回传给构造函数，否则每次启动都算新设备。
        /// </para>
        /// </summary>
        private async Task EnsureDeviceSessionAsync(CancellationToken cancel)
        {
            if (!string.IsNullOrEmpty(_deviceSignature)
                && DateTime.UtcNow < _deviceSessionExpiresAt.AddSeconds(-60))
                return;

            await EnsureAccessTokenAsync(cancel).ConfigureAwait(false);

            if (string.IsNullOrEmpty(_devicePrivateKey))
                _devicePrivateKey = AliyunSignature.NewPrivateKeyHex();
            if (string.IsNullOrEmpty(_deviceId))
                _deviceId = NewHexId();

            // user_id 是签名摘要的一部分，必须先拿到
            if (string.IsNullOrEmpty(_userId))
            {
                var me = await PostJsonAsync(_apiBase + "/v2/user/get",
                    new Dictionary<string, object>(), _accessToken, cancel).ConfigureAwait(false);
                string uid = PanJson.AsString(PanJson.Get(me, "user_id")) ?? "";
                if (string.IsNullOrEmpty(uid))
                    throw new QuarkApiException(
                        "阿里云盘未返回账号标识（user_id），无法建立设备会话，请重新登录。");
                _userId = uid;

                string did = PanJson.AsString(PanJson.Get(me, "default_drive_id")) ?? "";
                if (!string.IsNullOrEmpty(did))
                    _driveId = did;
            }

            AliyunSignatureResult signed;
            try
            {
                signed = AliyunSignature.Sign(_devicePrivateKey, _deviceId, _userId, 0);
            }
            catch (Exception ex)
            {
                throw new QuarkApiException("阿里云盘设备签名失败：" + ex.Message);
            }

            var body = new Dictionary<string, object>
            {
                { "deviceName", AliyunConstants.DeviceName },
                { "modelName", AliyunConstants.DeviceModel },
                { "pubKey", signed.PublicKey },
            };

            // create_session 本身也要带签名头（先用刚算出来的这一份）
            var res = await PostJsonAsync(_apiBase + AliyunConstants.DeviceSessionPath,
                body, _accessToken, cancel, null, _deviceId, signed.Signature).ConfigureAwait(false);

            if (!PanJson.AsBool(PanJson.Get(res, "result")))
            {
                string msg = PanJson.AsString(PanJson.Get(res, "message")) ?? "";
                throw new QuarkApiException("阿里云盘设备会话创建失败"
                    + (string.IsNullOrEmpty(msg) ? "（服务端未返回 result=true）" : "：" + msg)
                    + "。请到官网「登录设备」里清理后重新登录。");
            }

            _deviceSignature = signed.Signature;
            _deviceSessionExpiresAt = DateTime.UtcNow.AddMinutes(AliyunConstants.DeviceSessionMinutes);
        }

        /// <summary>生成 32 位十六进制随机 ID（设备 ID / 请求 ID）。</summary>
        private static string NewHexId()
        {
            var buf = new byte[16];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(buf);
            var sb = new StringBuilder(32);
            for (int i = 0; i < buf.Length; i++)
                sb.Append(buf[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>取分享的 share_token（带提取码）。结果按 share_id 缓存。</summary>
        public async Task<string> GetShareTokenAsync(string shareId, string passcode,
            CancellationToken cancel = default)
        {
            if (string.IsNullOrEmpty(shareId))
                throw new QuarkApiException("阿里云盘分享 ID 为空。");

            string cached;
            if (_shareTokens.TryGetValue(shareId, out cached) && !string.IsNullOrEmpty(cached))
                return cached;

            var body = new Dictionary<string, object>
            {
                { "share_id", shareId },
                { "share_pwd", passcode ?? "" },
            };

            var res = await PostJsonAsync(_apiBase + "/v2/share_link/get_share_token",
                body, null, cancel).ConfigureAwait(false);

            string token = PanJson.AsString(PanJson.Get(res, "share_token"));
            if (string.IsNullOrEmpty(token))
            {
                string msg = PanJson.AsString(PanJson.Get(res, "message")) ?? "未返回 share_token";
                string code = PanJson.AsString(PanJson.Get(res, "code")) ?? "";
                string hint = null;
                if (code.IndexOf("NotFound.ShareLink", StringComparison.OrdinalIgnoreCase) >= 0)
                    hint = "分享链接已失效、被取消，或 share_id 抄错了。";
                else if (msg.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0
                         || msg.IndexOf("密码", StringComparison.Ordinal) >= 0)
                    hint = "提取码不正确。";

                throw new QuarkApiException("阿里云盘获取分享凭证失败：" + msg
                    + (string.IsNullOrEmpty(hint) ? "" : "（" + hint + "）"));
            }

            _shareTokens[shareId] = token;
            return token;
        }

        // ---------------- 分享列目录 ----------------

        /// <summary>
        /// 把上层传来的父目录 ID 归一成阿里云盘认的值。
        /// <para>
        /// 阿里分享的根目录是字面量 <c>"root"</c>（见 refs/wenxi aliyun.dart:535
        /// <c>rootId: 'root'</c>）。但夸克/UC 系习惯用 <c>"0"</c> 表示根，
        /// 上层若原样透传 <c>"0"</c>，服务端会当成一个不存在的 file_id。
        /// 所以 "" / "0" / "root" 一律归一成 "root"。
        /// </para>
        /// </summary>
        private static string NormalizeShareParent(string parentFid)
        {
            if (string.IsNullOrEmpty(parentFid))
                return "root";
            if (parentFid == "0")
                return "root";
            return parentFid;
        }


        /// <summary>列出分享目录（免转存第一步）。自动翻页。</summary>
        public async Task<IReadOnlyList<PanFileEntry>> ListShareAsync(
            PanShareRef shareRef, string parentFid, CancellationToken cancel = default)
        {
            if (shareRef == null)
                throw new QuarkApiException("分享引用为空。");

            string shareToken = await GetShareTokenAsync(shareRef.ShareId, shareRef.Passcode, cancel)
                .ConfigureAwait(false);

            var merged = new List<PanFileEntry>();
            string marker = "";

            // ⚠️ 阿里分享的根目录 ID 是 "root"，**不是** "0"。
            //    上层（MainWindow）对夸克/UC 习惯用 "0"，若原样透传进来这里必须兜底，
            //    否则 parent_file_id="0" 会让第一层目录直接报错（"文件不存在"）。
            string parent = NormalizeShareParent(parentFid);

            for (int page = 0; page < 100; page++)
            {
                var body = new Dictionary<string, object>
                {
                    { "share_id", shareRef.ShareId },
                    { "parent_file_id", parent },
                    { "limit", AliyunConstants.PageSize },
                    { "marker", marker ?? "" },
                    { "all", false },
                    { "fields", "*" },
                    { "url_expire_sec", 14400 },
                    { "order_by", "name" },
                    { "order_direction", "ASC" },
                    { "image_thumbnail_process", "image/resize,w_160/format,jpeg" },
                    { "image_url_process", "image/resize,w_1920/format,jpeg" },
                    { "video_thumbnail_process", "video/snapshot,t_1000,f_jpg,ar_auto,w_300" },
                };

                var res = await PostJsonAsync(
                    // 分享列目录的专用路径是 /adrive/v2/file/list_by_share。
                    // 依据：refs/wenxi/lib/data/providers/aliyun.dart:694-698
                    //   shareId.isNotEmpty ? '/adrive/v2/file/list_by_share' : '/adrive/v3/file/list'
                    // ⚠️ 不要退回 /adrive/v2/file/list —— 那是「个人网盘」接口，
                    //    需要 drive_id 而不是 share_id，用分享令牌打它会拿不到 items。
                    _apiBase + "/adrive/v2/file/list_by_share",
                    body, null, cancel, shareToken).ConfigureAwait(false);

                var items = PanJson.AsArray(PanJson.Get(res, "items"));
                if (items == null || items.Length == 0)
                    break;

                foreach (var it in items)
                    merged.Add(NormalizeEntry(it));

                marker = PanJson.AsString(PanJson.Get(res, "next_marker")) ?? "";
                if (string.IsNullOrEmpty(marker))
                    break;
            }

            return merged;
        }

        // ---------------- 免转存取直链 ----------------

        /// <summary>
        /// 免转存取直链：<c>get_share_link_download_url</c>，不保存到自己网盘。
        /// </summary>
        public async Task<IReadOnlyList<DownloadLink>> GetShareDownloadLinksAsync(
            PanShareRef shareRef, IList<PanFileEntry> files, CancellationToken cancel = default)
        {
            if (shareRef == null)
                throw new QuarkApiException("分享引用为空。");
            if (files == null || files.Count == 0)
                return new List<DownloadLink>();

            string shareToken = await GetShareTokenAsync(shareRef.ShareId, shareRef.Passcode, cancel)
                .ConfigureAwait(false);

            var result = new List<DownloadLink>();
            string lastErr = null;

            foreach (var f in files)
            {
                try
                {
                    // 🚨 顺序（2026-10-25 用户实测后调整）：**先转存，再免转存**。
                    //
                    //    旧顺序是「先试 get_share_link_download_url（免转存），失败才转存」，
                    //    结果就是用户说的「阿里也出问题了，不能转存下载」——
                    //    那个免转存端点现在极不稳定（用假 share_token 都只回 HTTP 410），
                    //    偶尔回一个 url 但实际下不动，于是**永远走不到转存分支**。
                    //
                    //    现在的策略与夸克/UC 对齐：默认真转存到自己网盘再取直链（稳定），
                    //    只有转存失败（容量不足 / 无写权限 / 接口异常）才退回免转存。
                    string storedFolder = null;
                    string url = null;
                    // 免转存分支的原始响应，后面拼错误文案 / 兜底字段还要用（作用域要覆盖整个 try）。
                    object res = null;

                    var saved = await SaveShareFileAsync(shareRef, f, shareToken, cancel)
                        .ConfigureAwait(false);
                    url = saved.url;
                    storedFolder = saved.folderId;

                    if (string.IsNullOrEmpty(url))
                    {
                        // 兜底一：免转存直链（分享方允许时可用，但不写网盘）。
                        var body = new Dictionary<string, object>
                        {
                            { "share_id", shareRef.ShareId },
                            { "file_id", f.Fid },
                            { "expire_sec", AliyunConstants.DownloadUrlExpireSec },
                        };

                        // 410/404 时 PostJsonAsync 会抛，这里接住，交给下面的兜底路径。
                        // 说明：实测用**假的** share_token 请求该端点会得到 HTTP 410（空体），
                        // 而同主机对 /adrive/v2/file/list 返回的是规范的 401 ShareLinkTokenInvalid。
                        // 因此 410 更可能是"令牌无效/被风控"的表现，而不是端点下线 ——
                        // LinkSwift（仍在维护）用的正是这个路径，与这里一致。
                        try
                        {
                            res = await PostJsonAsync(
                                _apiBase + "/v2/file/get_share_link_download_url",
                                body, null, cancel, shareToken).ConfigureAwait(false);
                        }
                        catch (QuarkApiException)
                        {
                            res = null;
                        }

                        url = res == null
                            ? null
                            : (PanJson.AsString(PanJson.Get(res, "url"))
                               ?? PanJson.AsString(PanJson.Get(res, "download_url")));
                    }

                    if (string.IsNullOrEmpty(url))
                    {
                        // 最后一档：文件可能本来就在自己网盘里（用户手动转过）。
                        url = await TryOwnDriveDownloadUrlAsync(f.Fid, cancel).ConfigureAwait(false);
                    }

                    if (string.IsNullOrEmpty(url))
                    {
                        // 转存成功但最终没拿到直链 → 没有任何东西要下载，
                        // 临时目录必须**立刻**清掉，否则就永久留在用户网盘里了。
                        if (!string.IsNullOrEmpty(storedFolder))
                        {
                            try
                            {
                                await CleanupStoredAsync(new List<string> { storedFolder }, cancel)
                                    .ConfigureAwait(false);
                            }
                            catch { }
                        }

                        string msg = PanJson.AsString(PanJson.Get(res, "message")) ?? "未返回下载地址";
                        // 🚨 把转存阶段的真实原因拼进去。原先这里只写「未返回下载地址」，
                        //    而真正的原因（建目录 400 / copy 403）被 SaveShareFileAsync 吞掉了，
                        //    界面上只剩一句误导性的「分享者可能关闭直链或游客额度限制」。
                        if (!string.IsNullOrEmpty(_lastSaveError))
                            msg = _lastSaveError + "；免转存也失败：" + msg;
                        lastErr = string.Format("「{0}」取直链失败：{1}", f.Name, msg);
                        continue;
                    }

                    result.Add(new DownloadLink
                    {
                        Fid = f.Fid,
                        DownloadUrl = url,
                        Name = string.IsNullOrEmpty(f.Name)
                            ? (PanJson.AsString(PanJson.Get(res, "name")) ?? f.Fid)
                            : f.Name,
                        Size = f.Size > 0 ? f.Size : PanJson.AsLong(PanJson.Get(res, "size")),
                        Md5 = f.Md5 ?? PanJson.AsString(PanJson.Get(res, "content_hash")),
                        Sha1 = f.Sha1,
                        Sha256 = f.Sha256,
                        // 走了转存兜底才有值；下载结束由上层调 CleanupStoredAsync 删这个目录。
                        StoredId = storedFolder,
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

        /// <summary>
        /// 兜底：用 access_token 调 <c>get_download_url</c>（需要已登录、且文件在自己网盘里）。
        /// 失败返回 null，不抛异常 —— 它只是"主路径不通时的备选"。
        /// </summary>
        private async Task<string> TryOwnDriveDownloadUrlAsync(string fileId, CancellationToken cancel)
        {
            if (string.IsNullOrEmpty(_refreshToken))
                return null;
            try
            {
                await EnsureAccessTokenAsync(cancel).ConfigureAwait(false);
                var body = new Dictionary<string, object>
                {
                    { "drive_id", RequireOwnDriveId() },
                    { "file_id", fileId },
                    { "expire_sec", AliyunConstants.DownloadUrlExpireSec },
                };
                var res = await PostJsonAsync(_apiBase + "/v2/file/get_download_url",
                    body, _accessToken, cancel).ConfigureAwait(false);
                return PanJson.AsString(PanJson.Get(res, "url"));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 把分享里的一个文件**真正转存**到自己网盘，再取它在自己网盘里的直链。
        /// <para>
        /// 流程（依据 refs/wenxi/lib/data/providers/aliyun.dart 的 <c>_shareSource</c>）：
        /// 建临时目录 → <c>/v2/file/copy</c> 转存 → 轮询目录直到文件可见 → 取自有盘直链。
        /// </para>
        /// <para>
        /// 🚨 阿里转存是**异步**的：copy 立刻返回，但文件要过几秒才出现在目录里。
        /// 不等它出现就去取直链一定拿不到，所以必须轮询。
        /// </para>
        /// <para>
        /// 🚨 返回的 <c>folderId</c> 必须随 <see cref="DownloadLink.StoredId"/> 上抛，
        /// 等**下载结束**再删：提前删会让 CDN 回源失败（503 洪泛 + 速度崩）。
        /// </para>
        /// </summary>
        /// <returns>失败返回 <c>(null, null)</c> 或 <c>(null, folderId)</c>，不抛异常 —— 它只是兜底路径。</returns>
        private async Task<(string url, string folderId)> SaveShareFileAsync(
            PanShareRef shareRef, PanFileEntry file, string shareToken, CancellationToken cancel)
        {
            string folderId = null;
            _lastSaveError = "";
            try
            {
                await EnsureAccessTokenAsync(cancel).ConfigureAwait(false);

                // 🚨🚨 盘 id 只在 ValidateAsync() 里取（/v2/user/get + driveMigrate），
                //    而解析分享时建的客户端**从没走过登录校验** —— 于是 OwnDriveId 是空的，
                //    转存直接失败并报「阿里没有取到 drive_id」，用户看到就像"没登录"。
                //    这里补一次校验把盘 id 取到（失败不抛，下面的判断会给出真实原因）。
                if (string.IsNullOrEmpty(OwnDriveId))
                {
                    try { await ValidateAsync(cancel).ConfigureAwait(false); }
                    catch { }
                }

                if (string.IsNullOrEmpty(OwnDriveId))
                {
                    _lastSaveError = "阿里没有取到 drive_id（资源盘/备份盘都为空），无法转存。";
                    return (null, null);
                }

                // ① 建本次下载专用的临时目录（名字带 guid，避免并发下载互相覆盖）
                string folderName = "GeZi临时转存_" + Guid.NewGuid().ToString("N").Substring(0, 16);
                var mkBody = new Dictionary<string, object>
                {
                    { "drive_id", RequireOwnDriveId() },
                    { "parent_file_id", "root" },
                    { "name", folderName },
                    { "type", "folder" },
                    { "check_name_mode", "refuse" },
                };
                var mkRes = await PostJsonAsync(_apiBase + AliyunConstants.FileCreatePath, mkBody,
                    _accessToken, cancel, null, _deviceId, _deviceSignature).ConfigureAwait(false);
                folderId = PanJson.AsString(PanJson.Get(mkRes, "file_id"));
                if (string.IsNullOrEmpty(folderId))
                {
                    // 🚨 不能静默返回 —— 建不出目录就没有任何东西可下，原因必须留痕。
                    _lastSaveError = "阿里建临时目录失败：" + DescribeError(mkRes);
                    return (null, null);
                }

                // ② 转存：把分享里的文件复制进临时目录。
                // 🚨 必须走批量网关 /adrive/v4/batch（内层 url = "/file/copy"）。
                //    直连 /v2/file/copy 新版已不可靠，见 AliyunConstants.FileCopyPath 的说明。
                var cpBody = new Dictionary<string, object>
                {
                    { "share_id", shareRef.ShareId },
                    { "file_id", file.Fid },
                    { "to_drive_id", RequireOwnDriveId() },
                    { "to_parent_file_id", folderId },
                    { "auto_rename", true },
                };
                var cpRes = await PostBatchAsync(AliyunConstants.FileCopyPath, cpBody, shareToken, cancel)
                    .ConfigureAwait(false);
                string savedId = PanJson.AsString(PanJson.Get(cpRes, "file_id"));

                // ③ 轮询：等文件真的出现在临时目录里（最多约 15 秒）
                string finalId = null;
                for (int attempt = 0; attempt < 15 && string.IsNullOrEmpty(finalId); attempt++)
                {
                    if (attempt > 0)
                        await Task.Delay(1000, cancel).ConfigureAwait(false);

                    var entries = await ListOwnDirAsync(folderId, cancel).ConfigureAwait(false);
                    foreach (var e in entries)
                    {
                        if (e.IsDir) continue;
                        if (!string.IsNullOrEmpty(savedId))
                        {
                            if (string.Equals(e.Fid, savedId, StringComparison.Ordinal))
                            {
                                finalId = e.Fid;
                                break;
                            }
                        }
                        else if (string.Equals(e.Name, file.Name, StringComparison.Ordinal))
                        {
                            finalId = e.Fid;
                            break;
                        }
                    }
                }

                // 兜底：copy 没回 file_id 且名字对不上时，目录里唯一的文件就是它。
                if (string.IsNullOrEmpty(finalId))
                {
                    var entries = await ListOwnDirAsync(folderId, cancel).ConfigureAwait(false);
                    foreach (var e in entries)
                    {
                        if (e.IsDir) continue;
                        finalId = e.Fid;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(finalId))
                    return (null, folderId);   // 目录已建出来，交给上层按终态清理

                // ④ 取自有盘直链
                var dlBody = new Dictionary<string, object>
                {
                    { "drive_id", RequireOwnDriveId() },
                    { "file_id", finalId },
                    { "expire_sec", AliyunConstants.DownloadUrlExpireSec },
                };
                var dlRes = await PostJsonAsync(_apiBase + AliyunConstants.OwnDownloadUrlPath, dlBody,
                    _accessToken, cancel, null, _deviceId, _deviceSignature).ConfigureAwait(false);
                string url = PanJson.AsString(PanJson.Get(dlRes, "url"));
                if (string.IsNullOrEmpty(url))
                    _lastSaveError = "阿里转存成功但取自有盘直链失败：" + DescribeError(dlRes);
                return (url, folderId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 目录可能已经建出来了 —— 仍然把 id 返回去，让上层决定何时清理。
                // 🚨 但异常文本**必须留痕**，否则界面只会显示一句误导性的
                //    「没有取得直链（分享者可能关闭直链或游客额度限制）」。
                _lastSaveError = "阿里转存异常：" + ex.Message;
                return (null, folderId);
            }
        }


        /// <summary>
        /// 走阿里批量网关 <c>/adrive/v4/batch</c> 执行一次文件操作，返回内层响应的 body。
        /// <para>
        /// 请求体：<c>{resource:"file", requests:[{id,method,url,headers,body}]}</c>；
        /// 响应体：<c>{responses:[{id,status,body}]}</c>。
        /// 内层 <c>status</c> 不是 2xx 就抛 <see cref="QuarkApiException"/>（带服务端 message）。
        /// 返回前会自动等掉 <c>async_task_id</c>（阿里部分操作是异步的）。
        /// </para>
        /// <para>协议对照 refs/wenxi/lib/data/providers/aliyun.dart 的 <c>_batch()</c> / <c>_waitTask()</c>。</para>
        /// </summary>
        private async Task<object> PostBatchAsync(string innerPath, object innerBody,
            string shareToken, CancellationToken cancel)
        {
            var request = new Dictionary<string, object>
            {
                { "id", "0" },
                { "method", "POST" },
                { "url", innerPath },
                { "headers", new Dictionary<string, object> { { "Content-Type", "application/json" } } },
                { "body", innerBody },
            };
            var batchBody = new Dictionary<string, object>
            {
                { "resource", "file" },
                { "requests", new List<object> { request } },
            };

            var batchRes = await PostJsonAsync(_apiBase + AliyunConstants.BatchPath, batchBody,
                _accessToken, cancel, shareToken, _deviceId, _deviceSignature).ConfigureAwait(false);

            var replies = PanJson.AsArray(PanJson.Get(batchRes, "responses"));
            if (replies == null || replies.Length == 0)
                throw new QuarkApiException("阿里批量接口没有返回 responses：" + DescribeError(batchRes));

            var reply = replies[0];
            int status = PanJson.AsInt(PanJson.Get(reply, "status"));
            object body = PanJson.Get(reply, "body");
            if (status < 200 || status >= 300)
            {
                throw new QuarkApiException(string.Format("阿里批量操作失败（HTTP {0}）：{1}",
                    status, DescribeError(body)));
            }

            await WaitAsyncTaskAsync(body, cancel).ConfigureAwait(false);
            return body;
        }


        /// <summary>
        /// 若响应体带 <c>async_task_id</c>，轮询 <c>/v2/async_task/get</c> 直到 succeed。
        /// 没有该字段说明是同步完成，直接返回。
        /// </summary>
        private async Task WaitAsyncTaskAsync(object body, CancellationToken cancel)
        {
            string taskId = PanJson.AsString(PanJson.Get(body, "async_task_id"));
            if (string.IsNullOrEmpty(taskId))
                return;

            for (int attempt = 0; attempt < 80; attempt++)
            {
                cancel.ThrowIfCancellationRequested();
                if (attempt > 0)
                    await Task.Delay(500, cancel).ConfigureAwait(false);

                var res = await PostJsonAsync(_apiBase + AliyunConstants.AsyncTaskGetPath,
                    new Dictionary<string, object> { { "async_task_id", taskId } },
                    _accessToken, cancel, null, _deviceId, _deviceSignature).ConfigureAwait(false);

                string state = (PanJson.AsString(PanJson.Get(res, "state")) ?? "").ToLowerInvariant();
                if (state == "succeed" || state == "success")
                    return;
                if (state == "failed" || state == "fail" || state == "cancelled")
                {
                    throw new QuarkApiException("阿里异步任务失败："
                        + (PanJson.AsString(PanJson.Get(res, "message")) ?? state));
                }
            }
            throw new QuarkApiException("阿里转存任务长时间未完成（异步任务超时）。");
        }


        /// <summary>把服务端错误响应压成一句人话（message / code / 原文兜底）。</summary>
        private static string DescribeError(object res)
        {
            if (res == null) return "无响应";
            string msg = PanJson.AsString(PanJson.Get(res, "message"));
            string code = PanJson.AsString(PanJson.Get(res, "code"));
            if (!string.IsNullOrEmpty(msg) && !string.IsNullOrEmpty(code))
                return msg + "（" + code + "）";
            if (!string.IsNullOrEmpty(msg)) return msg;
            if (!string.IsNullOrEmpty(code)) return code;
            try
            {
                string raw = PanJson.Serialize(res);
                if (raw != null && raw.Length > 300) raw = raw.Substring(0, 300);
                return raw ?? "无响应";
            }
            catch { return "无响应"; }
        }

        /// <summary>取自己网盘内文件的直链。</summary>
        public async Task<IReadOnlyList<DownloadLink>> GetOwnDownloadLinksAsync(
            IList<PanFileEntry> files, CancellationToken cancel = default)
        {
            if (files == null || files.Count == 0)
                return new List<DownloadLink>();

            await EnsureAccessTokenAsync(cancel).ConfigureAwait(false);

            var result = new List<DownloadLink>();
            foreach (var f in files)
            {
                var body = new Dictionary<string, object>
                {
                    { "drive_id", RequireOwnDriveId() },
                    { "file_id", f.Fid },
                    { "expire_sec", AliyunConstants.DownloadUrlExpireSec },
                };

                var res = await PostJsonAsync(_apiBase + "/v2/file/get_download_url",
                    body, _accessToken, cancel).ConfigureAwait(false);

                string url = PanJson.AsString(PanJson.Get(res, "url"));
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
        /// 清理「为取直链而转存」的临时文件/目录（下载任务进入终态后由上层调用）。
        /// <para>
        /// 走 <c>/v2/recyclebin/trash</c>（进回收站，不是物理删除，用户还能找回）。
        /// 失败只记进 <see cref="LastError"/>，**不抛**：下载已经结束了，
        /// 删不掉只是留个残留，不该因此把任务标成失败。
        /// </para>
        /// </summary>
        public async Task CleanupStoredAsync(IList<string> storedIds, CancellationToken cancel = default)
        {
            if (storedIds == null || storedIds.Count == 0)
                return;

            try
            {
                await EnsureAccessTokenAsync(cancel).ConfigureAwait(false);

                foreach (string id in storedIds)
                {
                    // 根目录绝不能删。
                    if (string.IsNullOrEmpty(id) || id == "root")
                        continue;

                    var body = new Dictionary<string, object>
                    {
                        { "drive_id", RequireOwnDriveId() },
                        { "file_id", id },
                    };
                    await PostJsonAsync(_apiBase + AliyunConstants.RecyclebinTrashPath, body,
                        _accessToken, cancel, null, _deviceId, _deviceSignature).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _lastError = "清理转存临时文件失败：" + ex.Message;
            }
        }

        // ---------------- 自有网盘浏览 ----------------

        /// <summary>
        /// 列出自己网盘某个目录下的条目（<paramref name="parentId"/> 空/"0" = 根目录）。
        /// <para>
        /// 用 <c>/adrive/v3/file/list</c>（**必须带设备签名**）：一次最多 200 条，
        /// 返回 <c>next_marker</c> 时继续翻页。这里翻完所有页再返回，
        /// 与其它网盘客户端的行为一致 —— 界面按批揭示，上层不用处理分页游标。
        /// </para>
        /// </summary>
        public async Task<IReadOnlyList<QuarkFileEntry>> ListOwnDirAsync(
            string parentId, CancellationToken cancel = default)
        {
            await EnsureDeviceSessionAsync(cancel).ConfigureAwait(false);

            if (string.IsNullOrEmpty(OwnDriveId))
                throw new QuarkApiException(
                    "阿里云盘未取到 drive_id，无法列目录。请点「验证登录态」后重试；仍失败请重新登录。");

            var result = new List<QuarkFileEntry>();
            string marker = "";
            // 上限 200 页 × 200 条，防止服务端异常时死循环（正常目录远达不到）。
            for (int page = 0; page < 200; page++)
            {
                var body = new Dictionary<string, object>
                {
                    { "drive_id", RequireOwnDriveId() },
                    { "parent_file_id", string.IsNullOrEmpty(parentId) || parentId == "0" ? "root" : parentId },
                    { "limit", AliyunConstants.OwnPageSize },
                    { "all", false },
                    { "fields", "*" },
                    { "url_expire_sec", AliyunConstants.DownloadUrlExpireSec },
                    { "order_by", "name" },
                    { "order_direction", "ASC" },
                };
                if (!string.IsNullOrEmpty(marker))
                    body["marker"] = marker;

                var res = await PostJsonAsync(_apiBase + AliyunConstants.OwnFileListPath, body,
                    _accessToken, cancel, null, _deviceId, _deviceSignature).ConfigureAwait(false);

                var items = PanJson.Get(res, "items") as IList<object>;
                if (items != null)
                {
                    foreach (var it in items)
                    {
                        string type = PanJson.AsString(PanJson.Get(it, "type")) ?? "";
                        result.Add(new QuarkFileEntry
                        {
                            Fid = PanJson.AsString(PanJson.Get(it, "file_id")) ?? "",
                            Name = PanJson.AsString(PanJson.Get(it, "name")) ?? "",
                            Size = PanJson.AsLong(PanJson.Get(it, "size")),
                            IsDir = string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase),
                            // 自有文件不需要 share_fid_token；留空让下载器走 GetOwnDownloadLinksAsync
                            Token = "",
                        });
                    }
                }

                marker = PanJson.AsString(PanJson.Get(res, "next_marker")) ?? "";
                if (string.IsNullOrEmpty(marker))
                    break;
            }
            return result;
        }

        /// <summary>
        /// 查容量用量：<c>/v2/databox/get_personal_info</c>（**必须带设备签名**）。
        /// 返回 <c>{ "personal_space_info": { "used_size", "total_size" } }</c>。
        /// <para>
        /// 注意：旧实现用的是 <c>/v1.0/user/getDriveInfo</c>，该接口在当前线上产物里
        /// 已完全不存在（在官方 5.5MB bundle 里 0 次命中），所以以前永远显示「容量未知」。
        /// </para>
        /// </summary>
        public async Task<PanSpaceInfo> GetSpaceAsync(CancellationToken cancel = default)
        {
            try
            {
                await EnsureDeviceSessionAsync(cancel).ConfigureAwait(false);

                var res = await PostJsonAsync(_apiBase + AliyunConstants.SpaceInfoPath,
                    new Dictionary<string, object>(), _accessToken, cancel,
                    null, _deviceId, _deviceSignature).ConfigureAwait(false);

                var space = PanJson.Get(res, "personal_space_info") ?? res;
                var info = new PanSpaceInfo
                {
                    Used = PanJson.AsLong(PanJson.Get(space, "used_size")),
                    Total = PanJson.AsLong(PanJson.Get(space, "total_size")),
                };
                if (info.Total > 0)
                    _lastError = "";
                else
                    _lastError = "阿里云盘容量接口未返回数据（可能需重新登录）";
                return info;
            }
            catch (Exception ex)
            {
                // 容量拿不到不该让账号卡片变成错误页 —— 返回 0 让界面显示"未知"，
                // 但把原因记进 _lastError，便于用户点「验证登录态」时看到线索。
                _lastError = "容量获取失败：" + (ex.Message ?? "");
                return new PanSpaceInfo();
            }
        }

        // ---------------- HTTP ----------------

        /// <summary>
        /// POST JSON 到阿里接口，失败抛 <see cref="QuarkApiException"/>。
        /// <para>
        /// 对 <b>429 / BlockException（TooManyRequests）</b> 与 5xx 做有限重试：
        /// 阿里对高频调用会直接返回 <c>{"code":"BlockException","message":"rule:TooManyRequests"}</c>，
        /// 实测连续翻几个目录就会命中。这里退避重试（1s→2s→4s），
        /// 仍失败才把原因抛给界面 —— 否则用户会看到「列目录失败」却不知道是限流。
        /// </para>
        /// </summary>
        private async Task<object> PostJsonAsync(string url, object body, string accessToken,
            CancellationToken cancel, string shareToken = null,
            string deviceId = null, string signature = null,
            bool allowTokenRefresh = true)
        {
            const int maxAttempts = 4;
            string payload = PanJson.Serialize(body);
            // 401 后最多换一次令牌再重试，避免反复换导致死循环（refresh_token 单次有效，换多了更糟）。
            bool refreshedOnce = false;

            for (int attempt = 1; ; attempt++)
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                    req.Headers.TryAddWithoutValidation("User-Agent", AliyunConstants.ApiUa);
                    req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                    req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
                    req.Headers.TryAddWithoutValidation("Referer", AliyunConstants.Referer);
                    req.Headers.TryAddWithoutValidation("Origin", AliyunConstants.Origin);
                    req.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
                    req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
                    req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
                    req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");

                    if (!string.IsNullOrEmpty(accessToken))
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                    if (!string.IsNullOrEmpty(shareToken))
                        req.Headers.TryAddWithoutValidation(AliyunConstants.ShareTokenHeader, shareToken);

                    // 设备签名头：阿里新版按它校验「网页版客户端 + 已登记设备」。
                    // 缺了它，列目录/容量接口会直接回 DeviceSessionSignatureInvalid。
                    if (!string.IsNullOrEmpty(deviceId))
                    {
                        req.Headers.TryAddWithoutValidation("X-Device-Id", deviceId);
                        req.Headers.TryAddWithoutValidation("X-Canary", AliyunConstants.Canary);
                        req.Headers.TryAddWithoutValidation("X-Request-Id", NewHexId());
                    if (!string.IsNullOrEmpty(signature))
                        req.Headers.TryAddWithoutValidation("X-Signature", signature);
                }

                    using (var resp = await _http.SendAsync(req, cancel).ConfigureAwait(false))
                    {
                        string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                        if (!resp.IsSuccessStatusCode)
                        {
                            // 把服务端的 code/message 抽出来，别把整段 JSON 糊到用户脸上
                            var err = PanJson.Deserialize(text);
                            string emsg = PanJson.AsString(PanJson.Get(err, "message"));
                            string ecode = PanJson.AsString(PanJson.Get(err, "code")) ?? "";

                            // 🚨 401（以及明确说令牌有问题的 403）：服务端已经判本地令牌失效。
                            // 本地 _accessTokenExpiresAt 是**纯本地推算**的，服务端提前作废时
                            // 我们最长会拿着死令牌硬撞到推算的过期时间（近 2 小时），期间所有操作全失败。
                            // 这里作废本地令牌、立刻换一次再重试；只换一次。
                            bool tokenRejected = (int)resp.StatusCode == 401
                                || ((int)resp.StatusCode == 403
                                    && ecode.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0);
                            if (tokenRejected && !refreshedOnce && allowTokenRefresh
                                && !string.IsNullOrEmpty(accessToken))
                            {
                                refreshedOnce = true;
                                _accessToken = null;
                                _accessTokenExpiresAt = DateTime.MinValue;
                                try
                                {
                                    await EnsureAccessTokenAsync(cancel).ConfigureAwait(false);
                                    accessToken = _accessToken;
                                    continue;
                                }
                                catch (QuarkApiException)
                                {
                                    // 换不到就按下面的原始错误报出去，别再重试
                                }
                            }

                            // 限流 / 服务端临时故障 → 退避重试，不要立刻判死刑
                            bool retriable = (int)resp.StatusCode == 429
                                             || (int)resp.StatusCode >= 500
                                             || ecode.IndexOf("BlockException", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (retriable && attempt < maxAttempts)
                            {
                                await Task.Delay(1000 * (1 << (attempt - 1)), cancel).ConfigureAwait(false);
                                continue;
                            }

                            if (retriable)
                            {
                                throw new QuarkApiException(string.Format(
                                    "阿里云盘限流（{0}）：{1}。稍等一会儿再试，或减少同时进行的操作。",
                                    ecode, string.IsNullOrEmpty(emsg) ? "请求过于频繁" : emsg));
                            }

                            if (!string.IsNullOrEmpty(emsg))
                            {
                                throw new QuarkApiException(string.Format(
                                    "阿里云盘返回错误（{0}）：{1}", ecode, emsg));
                            }
                            throw new QuarkApiException(string.Format(
                                "阿里云盘接口返回 HTTP {0}：{1}",
                                (int)resp.StatusCode, Truncate(text, 200)));
                        }

                        var obj = PanJson.Deserialize(text);
                        if (obj == null)
                            throw new QuarkApiException("阿里云盘接口返回无法解析：" + Truncate(text, 200));
                        return obj;
                    }
                }
            }
        }

        private static PanFileEntry NormalizeEntry(object it)
        {
            var d = PanJson.AsDict(it);
            if (d == null)
                return new PanFileEntry { Fid = "", Name = "", Size = 0 };

            string type = PanJson.AsString(PanJson.Get(d, "type")) ?? "";
            return new PanFileEntry
            {
                Fid = PanJson.AsString(PanJson.Get(d, "file_id")) ?? "",
                Name = PanJson.AsString(PanJson.Get(d, "name")) ?? "",
                Size = PanJson.AsLong(PanJson.Get(d, "size")),
                IsDir = string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase),
                Token = PanJson.AsString(PanJson.Get(d, "file_id")) ?? "",
                // 阿里的 content_hash 通常是 sha1，这里按 sha1 存；下载完成后按它校验
                Sha1 = PanJson.AsString(PanJson.Get(d, "content_hash")),
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