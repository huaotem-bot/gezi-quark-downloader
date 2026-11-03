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

namespace GeZi.Providers.Aliyun
{
    /// <summary>
    /// 阿里云盘（aliyundrive.com / alipan.com）端点与指纹。
    /// <para>
    /// 与夸克/UC 的差别很大：阿里用 <b>OAuth refresh_token</b> 而不是 Cookie，
    /// 分享走 <c>share_token</c>，请求要带 <c>x-share-token</c> 头。
    /// </para>
    /// <para>
    /// <b>免转存：</b><c>POST /v2/file/get_share_link_download_url</c> 带
    /// <c>x-share-token</c> + <c>share_id</c> + <c>file_id</c> 直接返回下载地址，
    /// 不需要先保存到自己网盘。
    /// </para>
    /// </summary>
    public static class AliyunConstants
    {
        /// <summary>OAuth 换 token 的主机（与业务 API 不同域）。</summary>
        public const string AuthBase = "https://auth.aliyundrive.com";

        /// <summary>
        /// 新版网页用的 OAuth 端点主机。
        /// <para>
        /// 从站点自己的配置对象里取得：<c>openEndpoint: window.Global.open_endpoint
        /// || "https://open.aliyundrive.com"</c>，换 token 走
        /// <c>POST /oauth/access_token</c>，body 为
        /// <c>{client_id, grant_type:"refresh_token", refresh_token}</c>，
        /// 返回标准 OAuth 结构（<c>access_token</c>/<c>refresh_token</c>/<c>expires_in</c>）。
        /// </para>
        /// <para>
        /// 旧端点 <c>auth.aliyundrive.com/v2/account/token</c> 仍在用，但新版页面已经
        /// 走这条，所以优先试它、失败再回退旧端点。
        /// </para>
        /// </summary>
        public const string OpenAuthBase = "https://open.aliyundrive.com";

        /// <summary>新版网页换 token 时带的固定 client_id（网页版 adrive）。</summary>
        public const string WebClientId = "abe7fc1e4df241678bf7d422e0d930da";

        /// <summary>业务 API 主机。</summary>
        public const string ApiBase = "https://api.aliyundrive.com";

        public const string Referer = "https://www.aliyundrive.com/";
        public const string Origin = "https://www.aliyundrive.com";

        /// <summary>
        /// 下载直链（OSS）必须使用的 Referer。
        /// <para>
        /// <b>不能复用 <see cref="Referer"/></b>：阿里把 Referer 也算进了 OSS 直链签名
        /// （URL 的 <c>x-oss-additional-headers=referer</c>），签发时用的就是新版网页域名
        /// <c>https://www.alipan.com/</c>。实测：
        /// 带 <c>alipan.com</c> → 206 Partial Content；
        /// 带 <c>aliyundrive.com</c> / 夸克 Referer / 不带 → 403 SignatureDoesNotMatch 或 400 AuthorizationArgumentError。
        /// </para>
        /// </summary>
        public const string DownloadReferer = "https://www.alipan.com/";

        /// <summary>网页版 UA。阿里对 UA 一致性要求没有夸克那么苛刻，但保持同族仍更稳。</summary>
        public const string ApiUa =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        public const string DlUa = ApiUa;

        /// <summary>列目录的默认页大小（阿里上限 100）。</summary>
        public const int PageSize = 100;

        /// <summary>分享 token 请求头名。</summary>
        public const string ShareTokenHeader = "x-share-token";

        /// <summary>取直链时的有效期（秒）。默认 4 小时。</summary>
        public const int DownloadUrlExpireSec = 14400;

        // ---------------- 设备会话（secp256k1 签名） ----------------

        /// <summary>
        /// 创建设备会话的路径（挂在业务 API 主机下）。
        /// <para>
        /// 阿里新版对 <c>/adrive/v3/file/list</c>、容量查询等接口强制校验设备签名，
        /// 缺签名一律返回 <c>DeviceSessionSignatureInvalid</c>。
        /// 协议见 <see cref="Api.AliyunSignature"/> 与 refs/wenxi/lib/data/providers/aliyun.dart。
        /// </para>
        /// </summary>
        public const string DeviceSessionPath = "/users/v1/users/device/create_session";

        /// <summary>列自有网盘的接口路径（注意是 <c>adrive/v3</c>，不是 <c>v2</c>）。</summary>
        public const string OwnFileListPath = "/adrive/v3/file/list";

        /// <summary>查容量用量的接口路径（返回 <c>personal_space_info</c>）。</summary>
        public const string SpaceInfoPath = "/v2/databox/get_personal_info";

        /// <summary>
        /// 把分享文件转存到自己网盘。
        /// <para>
        /// 🚨 这是<b>批量网关内部的相对路径</b>，没有 <c>/v2</c> 前缀，必须配
        /// <see cref="BatchPath"/> 一起用。直连 <c>/v2/file/copy</c> 新版已不可靠。
        /// 对照 refs/wenxi/lib/data/providers/aliyun.dart 的 <c>_save()</c>。
        /// </para>
        /// </summary>
        public const string FileCopyPath = "/file/copy";

        /// <summary>
        /// 在自己网盘建目录（转存用的临时目录），type=folder。
        /// <para>
        /// 🚨 必须是 <c>adrive/v2/file/createWithFolders</c>。原先写的 <c>/v2/file/create</c>
        /// 是「建文件」接口（要 pre_hash / proof_code），传 <c>type:"folder"</c> 必然失败，
        /// 表现就是「阿里转存永远建不出临时目录 → 转存失败」。
        /// 对照 refs/wenxi（唯一有阿里转存的开源实现，其 test/aliyun_test.dart:387
        /// 明确断言了这个路径）。
        /// </para>
        /// </summary>
        public const string FileCreatePath = "/adrive/v2/file/createWithFolders";

        /// <summary>
        /// 转存用的**中转文件夹名**（网盘根目录下）。
        /// <para>
        /// 🚨 2026-11-03 统一：必须与夸克/UC（<c>QuarkConstants.DefaultFolder</c>）、
        /// 迅雷（<c>XunleiConstants.TempFolderName</c>）保持**同一个名字**，
        /// 否则用户在「我的网盘」里会看到三套不同名字的文件夹，根本分不清哪个是本软件的。
        /// </para>
        /// </summary>
        public const string TransitFolderName = "GeZi";

        /// <summary>
        /// 阿里批量操作网关。新版把转存（<see cref="FileCopyPath"/>）、改名等收在这里，
        /// 请求体形如 <c>{resource:"file", requests:[{id,method,url,headers,body}]}</c>，
        /// 响应 <c>{responses:[{id,status,body}]}</c>。
        /// </summary>
        public const string BatchPath = "/adrive/v4/batch";

        /// <summary>异步任务查询（批量操作可能返回 async_task_id，要轮询到 succeed）。</summary>
        public const string AsyncTaskGetPath = "/v2/async_task/get";

        /// <summary>取自己网盘内文件的直链。</summary>
        public const string OwnDownloadUrlPath = "/v2/file/get_download_url";

        /// <summary>把文件丢进回收站（清理转存临时文件用；不是物理删除，用户还能找回）。</summary>
        public const string RecyclebinTrashPath = "/v2/recyclebin/trash";

        /// <summary>
        /// 用户域接口基址（<c>/v2/user/driveMigrate</c> 挂在这里，不是业务 API 主机）。
        /// </summary>
        public const string UserBase = "https://user.aliyundrive.com";

        /// <summary>
        /// 补取「资源盘 / 备份盘」id 的接口路径。
        /// <para>
        /// 阿里改版后一个账号有多个盘：<c>/v2/user/get</c> 返回的
        /// <c>default_drive_id</c> 指向<b>备份盘</b>，而用户在网页版「全部文件」里
        /// 看到的文件其实在<b>资源盘</b>（<c>resource_drive_id</c>）。
        /// </para>
        /// <para>
        /// 官方前端 <c>fetchUser</c> 的兜底逻辑（从 5.5MB 主 bundle 逆向得到）：
        /// <c>/v2/user/get</c> 若没给 <c>resource_drive_id</c>，就
        /// <c>POST /user/driveMigrate</c>（hostType:"user", version:"v2"）
        /// 拿 <c>{backup_drive_id, resource_drive_id}</c>。
        /// 不调这一步，列目录永远只能看到空的备份盘 —— 这正是「登录成功但没有文件」的根因。
        /// </para>
        /// </summary>
        public const string DriveMigratePath = "/v2/user/driveMigrate";

        /// <summary>业务请求的 X-Canary 头（阿里按它判定「网页版客户端」）。</summary>
        public const string Canary = "client=web,app=adrive,version=v6.8.12";

        /// <summary>设备会话里登记的设备名（服务端「已登录设备」列表会显示它）。</summary>
        public const string DeviceName = "GeZi";

        /// <summary>设备会话里登记的设备型号。</summary>
        public const string DeviceModel = "Windows网页版";

        /// <summary>设备会话有效期（分钟）。服务端约 25 分钟，提前 1 分钟续签。</summary>
        public const int DeviceSessionMinutes = 25;

        /// <summary>列自有网盘的单页条数（adrive/v3 上限 200）。</summary>
        public const int OwnPageSize = 200;
    }
}