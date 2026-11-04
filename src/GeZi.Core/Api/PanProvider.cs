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
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Models;

namespace GeZi.Core.Api
{
    /// <summary>支持解析与下载的网盘种类。</summary>
    public enum PanKind
    {
        /// <summary>夸克网盘（pan.quark.cn / drive.quark.cn）。</summary>
        Quark = 0,
        /// <summary>UC 网盘（drive.uc.cn）。与夸克同一套 ucpro 后端，但域名与 Cookie 独立。</summary>
        Uc = 1,
        /// <summary>阿里云盘（aliyundrive.com / alipan.com）。</summary>
        Aliyun = 2,
        /// <summary>迅雷云盘（pan.xunlei.com）。</summary>
        Xunlei = 3,
    }

    /// <summary>
    /// 从分享链接/文案里解析出来的引用。
    /// 各网盘的分享 ID 形态不同，但后续流程一致：拿 ID（+提取码）→ 换 token → 列目录 → 取直链。
    /// </summary>
    public sealed class PanShareRef
    {
        public PanKind Kind { get; set; }

        /// <summary>
        /// 分享 ID。夸克/UC 是 <c>/s/</c> 后面那串；
        /// 阿里是 <c>/s/</c> 后面的 share_id；迅雷是 <c>/s/</c> 后面的 share_id。
        /// </summary>
        public string ShareId { get; set; } = "";

        /// <summary>提取码/访问码。没有密码的分享为空串。</summary>
        public string Passcode { get; set; } = "";

        /// <summary>链接里带的子目录 fid（夸克/UC 的 <c>/s/xxx/fid</c> 形态）。</summary>
        public string StartFid { get; set; } = "0";

        /// <summary>原始输入（诊断与日志用，可能含提取码，展示前要脱敏）。</summary>
        public string RawUrl { get; set; } = "";

        public bool HasPasscode
        {
            get { return !string.IsNullOrEmpty(Passcode); }
        }

        public override string ToString()
        {
            return Kind + ":" + ShareId;
        }
    }

    /// <summary>
    /// 统一后的文件条目。各网盘字段名不同（fid/file_id/id、file_name/name），
    /// 在这里归一化，上层只认这一种。
    /// </summary>
    public sealed class PanFileEntry
    {
        public string Fid { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public bool IsDir { get; set; }

        /// <summary>
        /// 分享场景下取直链所需的附加令牌
        /// （夸克/UC 的 share_fid_token、阿里的 file_id 本身、迅雷的 file_id）。
        /// </summary>
        public string Token { get; set; }

        /// <summary>服务端给的内容哈希（有就带上，用于下载完成后校验）。</summary>
        public string Md5 { get; set; }
        public string Sha1 { get; set; }
        public string Sha256 { get; set; }

        public PanFileEntry Clone()
        {
            return (PanFileEntry)MemberwiseClone();
        }
    }

    /// <summary>
    /// 各网盘的能力差异。UI 据此决定显示/隐藏哪些按钮，
    /// 避免"按钮能点但点了必然失败"。
    /// </summary>
    public sealed class PanCapabilities
    {
        /// <summary>是否支持免转存（不保存到自己网盘直接取直链）。</summary>
        public bool TransferFree { get; set; }

        /// <summary>是否需要图形验证码（极验滑块等）才能取链。</summary>
        public bool NeedsCaptcha { get; set; }

        /// <summary>是否需要额外的请求签名头（x-signature / x-canary 之类）。</summary>
        public bool NeedsSignature { get; set; }

        /// <summary>分享是否支持提取码。</summary>
        public bool SupportsPasscode { get; set; } = true;

        /// <summary>是否支持浏览分享内的子目录。</summary>
        public bool SupportsSubFolder { get; set; } = true;

        /// <summary>是否需要登录态才能取直链（三个网盘都是 true）。</summary>
        public bool RequiresLogin { get; set; } = true;

        /// <summary>给用户看的一句话说明（含已知限制）。</summary>
        public string Note { get; set; } = "";
    }

    /// <summary>
    /// 自有网盘的容量信息（账号卡片上那条进度条的数据来源）。
    /// </summary>
    public sealed class PanSpaceInfo
    {
        /// <summary>总容量（字节）。0 表示这家没返回，界面按"未知"处理。</summary>
        public long Total { get; set; }

        /// <summary>已用容量（字节）。</summary>
        public long Used { get; set; }
    }

    /// <summary>
    /// 网盘能力的统一契约。
    /// <para>
    /// 设计原则：**只抽"取直链"这一条链路**，不碰下载引擎。
    /// 所有网盘的最终产物都是 <see cref="DownloadLink"/>，
    /// 交给现有的 <c>SegmentedDownloader</c> 分片下载 —— 这样新增网盘
    /// 不会影响已经调好的 256 并发引擎。
    /// </para>
    /// </summary>
    public interface IPanProvider : IDisposable
    {
        /// <summary>这个实现对应的网盘。</summary>
        PanKind Kind { get; }

        /// <summary>展示名（"夸克网盘"/"UC网盘"/…）。</summary>
        string DisplayName { get; }

        /// <summary>能力声明。</summary>
        PanCapabilities Capabilities { get; }

        /// <summary>是否已具备可用登录态。没有就应提示用户先登录。</summary>
        bool IsLoggedIn { get; }

        /// <summary>当前账号标识（昵称或 uid），未登录返回空串。</summary>
        string AccountName { get; }

        /// <summary>
        /// 账号头像 URL。拿不到（或该账号根本没设头像）返回空串，
        /// 界面回退成"昵称首字"的彩色圆形头像 —— 不留空白圆圈。
        /// </summary>
        string AvatarUrl { get; }

        /// <summary>
        /// 用分享引用换出根目录条目。
        /// </summary>
        /// <exception cref="QuarkApiException">网盘返回错误（密码错、链接失效等）。</exception>
        Task<IReadOnlyList<PanFileEntry>> ListShareAsync(
            PanShareRef shareRef, string parentFid, CancellationToken cancel = default);

        /// <summary>
        /// 免转存取直链：不保存到自己网盘，直接拿下载地址。
        /// </summary>
        Task<IReadOnlyList<DownloadLink>> GetShareDownloadLinksAsync(
            PanShareRef shareRef, IList<PanFileEntry> files, CancellationToken cancel = default);

        /// <summary>
        /// 取自己网盘内文件的直链（已转存/自有文件）。
        /// </summary>
        Task<IReadOnlyList<DownloadLink>> GetOwnDownloadLinksAsync(
            IList<PanFileEntry> files, CancellationToken cancel = default);

        /// <summary>
        /// 清理「为了取直链而转存」到自己网盘的临时文件/目录。
        /// <para>
        /// 🚨 为什么必须由上层在**下载结束后**调用，而不是取完链就删：
        /// 直链是 CDN 回源模式，源文件一删，正在下的分片全部回源失败
        /// → 服务端 503 洪泛 + 速度掉到 1 MB/s 以下（实测过）。
        /// </para>
        /// <para>
        /// 免转存取链的实现（没有转存动作）直接返回已完成任务即可。
        /// </para>
        /// </summary>
        /// <param name="storedIds">取自 <see cref="DownloadLink.StoredId"/> 的 id 列表。</param>
        Task CleanupStoredAsync(IList<string> storedIds, CancellationToken cancel = default);

        /// <summary>
        /// 把文件**从用户网盘删除**（进回收站）。
        /// <para>
        /// 🚨🚨 2026-11-04 新增。此前界面层写死了「阿里/迅雷暂不支持删除」，
        /// 那是**错的** —— 两家的删除能力其实早就实现了（见下），只是没有公开入口：
        /// <list type="bullet">
        /// <item>阿里：<c>POST /v2/recyclebin/trash</c>（{drive_id, file_id}，单个删，循环调用）
        ///       —— 与 refs/wenxi 的 <c>aliyun.dart:1205 delete()</c> 完全一致。</item>
        /// <item>迅雷：<c>POST /drive/v1/files:batchTrash</c>（{ids, space}）
        ///       或 <c>files:batchDelete</c> —— 与 refs/YunX 的
        ///       <c>XunleiApi.kt:776 deleteFiles()</c> / <c>:625 batchDelete()</c> 一致。</item>
        /// </list>
        /// </para>
        /// <para>
        /// ⚠️ 与 <see cref="CleanupStoredAsync"/> 的区别：那个是「删自己转存的临时副本」，
        /// 失败只记日志；这个是**用户主动删自己的文件**，失败必须抛异常让界面提示。
        /// </para>
        /// </summary>
        Task DeleteFilesAsync(IList<string> fileIds, CancellationToken cancel = default);

        // ---------------- 自有网盘浏览 ----------------
        // 下面三个是"登录后能看到自己的东西"所需：校验登录态、列目录、查容量。
        // 没有它们，用户登录完只会看到一句"登录成功"，却看不到网盘里有什么
        // —— 这正是被反馈过的问题（"登录了但什么都不显示"）。
        // 各家协议不同，所以放在接口里由各客户端自己实现。

        /// <summary>
        /// 最近一次 <see cref="ValidateAsync"/> 失败的原因（成功时为空串）。
        /// <para>
        /// 存在的理由很具体：<see cref="ValidateAsync"/> 只返回 ok/昵称，
        /// 用户点「验证登录态」失败时界面只能写一句"无效，请重新登录"，
        /// 而真正的原因（令牌过期、参数不对、网络不通、服务端拒绝）全被
        /// <c>catch</c> 吞掉了 —— 用户看不出到底哪儿不对，也无法自助解决。
        /// 把这个原因暴露出来，界面才能给出可操作的一句话。
        /// </para>
        /// </summary>
        string LastError { get; }

        /// <summary>
        /// 校验登录态是否仍然有效，并取回昵称（拿不到真名时返回兜底值）。
        /// </summary>
        Task<(bool ok, string nick)> ValidateAsync(CancellationToken cancel = default);

        /// <summary>
        /// 列出自己网盘里某个目录下的条目。<paramref name="parentId"/> 传空或 "0" 表示根目录。
        /// </summary>
        /// <remarks>
        /// 返回的 <see cref="Models.QuarkFileEntry"/> 是全应用统一的条目模型
        /// （Fid/Name/Size/IsDir/Token），界面和下载引擎都只认它，
        /// 所以各网盘只要把响应映射成这个模型即可，不需要各写一套列表 UI。
        /// </remarks>
        Task<IReadOnlyList<Models.QuarkFileEntry>> ListOwnDirAsync(
            string parentId, CancellationToken cancel = default);

        /// <summary>查自己网盘的容量用量（账号卡片上的剩余空间）。</summary>
        Task<PanSpaceInfo> GetSpaceAsync(CancellationToken cancel = default);
    }
}