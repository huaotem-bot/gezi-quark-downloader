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
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Api;
using GeZi.Core.Models;
using GeZi.Core.Support;

namespace GeZi.DownloadEngine
{
    /// <summary>一个下载任务：URL + 目标路径 + 独立取消/暂停令牌。</summary>
    public class DownloadJob
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string Dest { get; set; } = "";
        public string Cookie { get; set; } = "";

        /// <summary>
        /// 下载请求的 Referer。空 = 沿用下载器的默认值（夸克）。
        /// <para>
        /// <b>阿里云盘必须显式给</b>：它的 OSS 直链在签名时把 Referer 也算进去了
        /// （URL 里带 <c>x-oss-additional-headers=referer</c>），取值必须是
        /// <c>https://www.alipan.com/</c>。带夸克 Referer 会被判
        /// <c>SignatureDoesNotMatch</c>（403），不带则 <c>AuthorizationArgumentError</c>（400）。
        /// </para>
        /// </summary>
        public string Referer { get; set; }

        /// <summary>
        /// 下载请求的 User-Agent。空 = 沿用下载器的默认值（夸克客户端 UA）。
        /// <para>
        /// <b>UC 必须显式给</b>：UC 的 CDN 与夸克是两套防盗链，
        /// 拿夸克的 UA 去请求 UC 直链会被判 403。见 <c>UcConstants.DlUa</c>。
        /// </para>
        /// </summary>
        public string UserAgent { get; set; }

        /// <summary>
        /// 下载请求的 Origin。空 = 沿用下载器的默认值（<c>https://pan.quark.cn</c>）。
        /// <para>
        /// 与 <see cref="Referer"/> 必须成对匹配，否则头集合不自洽
        /// （真实浏览器发请求时这两个同源，混搭本身就是异常特征）。
        /// </para>
        /// </summary>
        public string Origin { get; set; }

        /// <summary>
        /// 下载数据面是否走 OpenSSL 桥。
        /// <para>
        /// <b>UC 必须为 true</b>：UC 的下载 CDN <c>dl-pc-sz-cf.pds.uc.cn</c> 按 TLS
        /// ClientHello 指纹过滤，Windows 自带的 SChannel 一律回
        /// <c>403 RequestDeniedByCallback / require login [auth not found]</c>；
        /// 同一个直链、同一份 Cookie 换到 OpenSSL 通道立刻回 <c>206</c>（实测）。
        /// 夸克 / 阿里 / 迅雷的 CDN 不查这个指纹，保持 false 走 HttpClient 即可。
        /// </para>
        /// </summary>
        public bool UseOpenSsl { get; set; }

        /// <summary>0 表示用默认线程数。</summary>
        public int Threads { get; set; } = 0;

        /// <summary>内容哈希（取链接口有就带；没有则完成时标记“未校验”）。</summary>
        public string Md5 { get; set; }
        public string Sha1 { get; set; }
        public string Sha256 { get; set; }

        /// <summary>
        /// 分片/续传元数据的根目录覆盖。null/空 = 与目标文件同级（默认）。
        /// 见 PartsPaths 的说明。
        /// </summary>
        public string PartsRoot { get; set; }

        /// <summary>
        /// 重新获取本文件直链的回调。夸克直链带时效签名，长任务下到一半会过期；
        /// 提供它之后，下载器遇到 401/403/412 能自动换新链续传，而不是整个任务失败。
        /// 由 UI 层注入 —— 只有它知道这个文件对应的 fid / token / 分享上下文。
        /// </summary>
        public Func<CancellationToken, Task<string>> LinkRefresher { get; set; }

        /// <summary>
        /// 为 true 时，失败文案**不再承诺"可续传"**。
        /// 免转存的续传取链依赖分享 stoken，而续传**不会重新换 stoken**（只在解析分享时取一次），
        /// stoken 过期就永远取不到新链 → 提示必须改成"需重新解析分享"。
        /// 由 UI 层按 <c>Pending.FromShare</c> 设置（FromShare == true ⟺ 免转存）。
        /// </summary>
        public bool ResumeCannotRefresh { get; set; }

        /// <summary>
        /// 这个网盘能承受的最大并发连接数（0 = 不限，按全局预算均分）。
        ///
        /// <para>
        /// 🚨 各网盘的服务端限流策略完全不同，不能一个数字通吃：
        /// </para>
        /// <list type="bullet">
        /// <item>迅雷：单 IP 并发上限约 <b>16</b>。实测 137 个分片同时请求时
        /// CDN 大面积回 <c>503 Service Unavailable</c>，退避重试把速度压到 1 MB/s；
        /// 同一账号 16 并发反而能到 12.98 MB/s —— <b>并发越高越慢</b>。</item>
        /// <item>阿里云盘：单 IP 有约 2.5~2.7 MB/s 硬限速，64 并发已吃到天花板
        /// （128 并发只多 11%），再堆连接只会触发限流。</item>
        /// <item>夸克 / UC：没有这个限制，256 并发能跑到 20+ MB/s。</item>
        /// </list>
        /// </summary>
        public int MaxParallelCount { get; set; }

        /// <summary>
        /// 续传上下文（可选）。由 UI 层在创建任务时填好，用于把该任务写进
        /// 「未完成任务」持久化记录，使程序重启后能**不依赖界面上下文**地重取直链续传。
        /// 为什么放在 Job 上而不是让 UI 自己拼：UI 在写 pending 时只有 <c>TaskItem</c>，
        /// 而这些字段（fid/token/分享上下文/相对目录）在下载链路里就只存在于创建那一刻，
        /// 挂在 Job 上最不容易漏传。
        /// </summary>
        public PendingInfo Pending { get; set; }

        /// <summary>
        /// 为 true 时，这个任务**不会**被写进「未完成任务」持久化记录
        /// （重启后不会提示续传）。更新包的下载用它 —— 跨重启续传更新包没有意义
        /// （要重新查版本、重新取链），且避免 Pending 信息残缺导致续传界面出错。
        /// </summary>
        public bool SkipPersist { get; set; }

        public CancellationTokenSource Cancel { get; } = new CancellationTokenSource();
        public PauseToken Pause { get; } = new PauseToken();

        public void CancelJob() { try { Cancel.Cancel(); } catch { } }
        public void PauseJob() => Pause.Pause();
        public void ResumeJob() => Pause.Resume();
    }

    /// <summary>
    /// 一个下载任务的「续传上下文」—— 让任务能在程序重启后被重新取链并续下。
    /// <para>
    /// 除 <see cref="Dest"/> 外的字段都用于**重新申请直链**（旧链带时效签名，必已失效）。
    /// 分享模式下缺 <see cref="Token"/> / <see cref="PwdId"/> / <see cref="Stoken"/>
    /// 就无法重取（例如免转存/游客直链任务），此时 <see cref="CanResume"/> 为 false。
    /// </para>
    /// </summary>
    public class PendingInfo
    {
        /// <summary>分享的 pwd_id（分享模式才有）。</summary>
        public string PwdId { get; set; }
        /// <summary>分享的 stoken（分享模式才有）。</summary>
        public string Stoken { get; set; }
        /// <summary>提取码；无码为空。</summary>
        public string Passcode { get; set; }
        /// <summary>网盘文件 fid（所有模式都有）。</summary>
        public string Fid { get; set; }
        /// <summary>分享内文件的 share fid token（分享模式才有）。</summary>
        public string Token { get; set; }
        /// <summary>是否来自分享（否则为"我的网盘"）。</summary>
        public bool FromShare { get; set; }

        /// <summary>
        /// 来源网盘（0 = 夸克）。续传时据此选客户端：
        /// 拿夸克客户端去请求 UC/阿里/迅雷必然失败，所以这个字段必须随任务持久化。
        /// </summary>
        public int PanKind { get; set; }
        /// <summary>相对目录（含尾部分隔符时也算合法），用于重建目标路径。</summary>
        public string RelDir { get; set; }
        /// <summary>分享进入目录 fid（带 fid 的分享链接才有意义，普通分享是 "0"）。
        /// 自动重解析分享时的起点。</summary>
        public string StartFid { get; set; }
        /// <summary>
        /// 文件在分享内的完整相对目录（相对 <see cref="StartFid"/>）。
        /// 仅供重解析分享定位文件；本地路径仍看 <see cref="RelDir"/>。
        /// </summary>
        public string ShareRelDir { get; set; }
        /// <summary>目标文件完整路径。</summary>
        public string Dest { get; set; }
        /// <summary>文件总大小（0 = 未知）。</summary>
        public long Size { get; set; }

        /// <summary>内容哈希；跨重启续传后仍用同一组期望值校验。</summary>
        public string Md5 { get; set; }
        public string Sha1 { get; set; }
        public string Sha256 { get; set; }

        /// <summary>
        /// 原始分享链接（完整 URL）。
        /// <para>
        /// 🚨 2026-11-03：续传的「百分百兜底」—— PwdId / stoken 都会过期或丢失，
        /// 但只要还有原始链接，就能重新解析分享拿全套新凭证。
        /// </para>
        /// </summary>
        public string ShareUrl { get; set; }

        /// <summary>转存后的文件 fid（「转存→取直链」时取链要用这个）。</summary>
        public string SavedFid { get; set; }

        /// <summary>
        /// 是否具备重取链所需的全部信息。
        /// <para>
        /// 🚨 2026-11-03：改成**委托给 ResumePolicy**，不再自己写一份。
        /// 原来这里和 <c>HistoryStore.PendingTask.CanResume</c> 各有一份判据，
        /// 而这份**没算阿里/迅雷**（它们的分享任务天然没有 PwdId/Stoken）→
        /// 同一个任务在引擎侧判「不可续传」、在界面侧判「可续传」，自相矛盾。
        /// 判据只许有一份实现。
        /// </para>
        /// </summary>
        public bool CanResume
        {
            get
            {
                return GeZi.Core.Models.ResumePolicy.CanResume(
                    Dest, Fid, FromShare, Token, PwdId, Stoken, PanKind);
            }
        }
    }
}
