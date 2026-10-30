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
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.Core.Download
{
    /// <summary>一个下载任务：URL + 目标路径 + 独立取消/暂停令牌。</summary>
    public class DownloadJob
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string Dest { get; set; } = "";
        public string Cookie { get; set; } = "";
        /// <summary>0 表示用默认线程数。</summary>
        public int Threads { get; set; } = 0;

        /// <summary>
        /// 落盘方式：独立分片+边合并（可边下边播）或共写单一文件（下完即结束、无合并环节）。
        /// 由 UI 层从设置页填入；核心层据此选择 worker 的写入路径。
        /// </summary>
        public WriteMode WriteMode { get; set; } = WriteMode.SegmentedFiles;

        /// <summary>
        /// 分片/续传元数据的根目录覆盖。null/空 = 与目标文件同级（默认）。
        /// 见 SegmentedDownloader 的 _partsRoot 说明。
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

        /// <summary>是否具备重取链所需的全部信息。</summary>
        public bool CanResume
        {
            get
            {
                if (string.IsNullOrEmpty(Dest) || string.IsNullOrEmpty(Fid)) return false;
                if (!FromShare) return true;                       // 我的网盘：只需 fid
                return !string.IsNullOrEmpty(Token)
                    && !string.IsNullOrEmpty(PwdId)
                    && !string.IsNullOrEmpty(Stoken);
            }
        }
    }
}
