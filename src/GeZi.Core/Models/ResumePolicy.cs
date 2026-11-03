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

namespace GeZi.Core.Models
{
    /// <summary>
    /// 一条「未完成任务」记录能否续传的**纯判据**。
    /// <para>
    /// 🚨 为什么放在 Core 而不是留在 WPF 的 PendingTask 里：
    /// 自检工程（tests/GeZi.SelfTest）**不能引用 WPF 项目**（会拖进整个 UI 栈），
    /// 而这条判据恰恰是最需要用例守住的 —— 它历史上被改错过一次，
    /// 导致「刚点下载没写盘就重启」的记录被禁止续传，用户以为续传功能坏了。
    /// 抽成无依赖的纯函数后，两边共用同一份逻辑，用例也能直接覆盖。
    /// </para>
    /// </summary>
    public static class ResumePolicy
    {
        /// <summary>
        /// 判断能否续传。
        /// </summary>
        /// <param name="dest">本地目标路径。为空 = 不知道往哪写，不可续传。</param>
        /// <param name="fid">网盘文件 id。为空 = 不知道取哪个文件，不可续传。</param>
        /// <param name="fromShare">是否来自分享（分享必须持有 stoken/token 才能重取直链）。</param>
        /// <param name="token">分享的 share_fid_token。</param>
        /// <param name="pwdId">分享的 pwd_id。</param>
        /// <param name="stoken">分享的 stoken。</param>
        /// <remarks>
        /// <para>
        /// ⚠️ **文件大小 <c>size</c> 刻意不参与判断**（2026-11-02 明确）：
        /// 2026-11-01 曾加过「size &lt;= 0 就不可续传」，两个理由后来都被证伪：
        /// </para>
        /// <list type="number">
        /// <item><c>ThreadAllocator</c> 对未知大小会**退回平分**（n==1 时直接给满），
        /// 不会把任务饿死 —— 这是它注释里写明的行为；</item>
        /// <item>续传时的总长度来自引擎自己 <c>GetFileSize()</c> 探测，
        /// 与这条记录里存的 size **毫无关系**。</item>
        /// </list>
        /// <para>
        /// 而「刚点下载、一个字节都没写盘就重启」的记录，size 必然是 0 ——
        /// 禁选等于把最常见的场景挡在门外。
        /// </para>
        /// </remarks>
        public static bool CanResume(string dest, string fid, bool fromShare,
            string token, string pwdId, string stoken, int panKind = 0)
        {
            return Check(dest, fid, fromShare, token, pwdId, stoken, panKind, null).Can;
        }

        /// <summary>
        /// 续传可行性检查的**完整结果**：能不能 + 为什么 + 用户该怎么办。
        /// <para>
        /// 🚨 2026-11-03 新增。原来 <see cref="CanResume"/> 只返回 bool，
        /// 界面上只能显示一句笼统的「需重新解析」，用户根本不知道到底缺什么、
        /// 该做什么。现在把原因和下一步动作一起返回，界面直接展示。
        /// </para>
        /// </summary>
        public sealed class ResumeCheckResult
        {
            /// <summary>能不能续传。</summary>
            public bool Can;

            /// <summary>不能续传时的**原因**（人话，直接显示给用户）。空 = 能续传。</summary>
            public string Reason;

            /// <summary>用户该做什么（人话）。空 = 无需操作。</summary>
            public string NextAction;

            /// <summary>简短徽章文字（界面右上角小标签），空 = 不显示。</summary>
            public string Badge;
        }

        /// <summary>
        /// 检查能否续传，并给出原因与下一步动作。
        /// </summary>
        /// <param name="shareUrl">
        /// 原始分享链接。有它就能重新解析分享拿全套凭证 —— 这是「百分百能续传」的兜底。
        /// 旧记录没有这个字段，传 null 即可。
        /// </param>
        public static ResumeCheckResult Check(string dest, string fid, bool fromShare,
            string token, string pwdId, string stoken, int panKind = 0,
            string shareUrl = null)
        {
            if (string.IsNullOrEmpty(dest))
            {
                return new ResumeCheckResult
                {
                    Can = false,
                    Reason = "这条记录没存下载路径，不知道文件该写到哪。",
                    NextAction = "回到「下载」页重新下载这个文件。",
                    Badge = "缺下载路径",
                };
            }

            if (string.IsNullOrEmpty(fid))
            {
                return new ResumeCheckResult
                {
                    Can = false,
                    Reason = "这条记录没存文件 ID，没法向网盘要直链。",
                    NextAction = "回到「下载」页重新解析并下载这个文件。",
                    Badge = "缺文件信息",
                };
            }

            if (fromShare)
            {
                // 🚨🚨 2026-11-02 修复「阿里/迅雷分享任务永远不可续传」：
                //   FromShare 这个布尔承载了两种完全不同的语义 ——
                //     · 夸克 / UC（0/1）：重取直链必须带 pwd_id + stoken + share_fid_token，
                //       三者缺一不可（stoken 与 token 是**绑定的一组**，见 AGENTS.md 铁律）；
                //     · 阿里 / 迅雷（2/3）：取链由 IPanProvider 内部完成
                //       （阿里 file/copy 转存、迅雷转存到临时目录再取直链），
                //       **根本不需要 stoken** —— 这两家的记录里 PwdId/Stoken 天然是空的。
                //   原来用同一套判据要求三者齐全，于是阿里/迅雷的分享任务
                //   被永久标成「需重新解析」+ 复选框置灰，用户看到的就是
                //   「明明能下，为什么这条不能续传」。
                //   ⇒ 阿里/迅雷只要有 fid + dest 就可续传（直链由 provider 重取）。
                if (panKind == 2 || panKind == 3)
                {
                    // 阿里/迅雷：provider 内部自己转存，有 fid 就能重取链。
                    // ⚠️ 但有个真实失败场景：记录是 2026-11-02 之前建的，
                    //    那时**没写 PwdId**，而阿里 provider 的 GetShareTokenAsync
                    //    需要 share_id —— 不过它还有 TryOwnDriveDownloadUrlAsync 兜底
                    //    （用 f.Fid 直接取自有网盘直链），所以有 fid 仍可一试。
                    return new ResumeCheckResult { Can = true };
                }

                // 夸克 / UC 分享：必须有 pwd_id + stoken + share_fid_token 三件套。
                // ⚠️ 但有**原始分享链接**时就能自愈：重新解析分享即可拿到全新三件套，
                //    所以「三件套缺失」只在**链接也丢了**时才真正不可续传。
                bool hasCred = !string.IsNullOrEmpty(token)
                    && !string.IsNullOrEmpty(pwdId)
                    && !string.IsNullOrEmpty(stoken);
                if (hasCred)
                    return new ResumeCheckResult { Can = true };

                if (!string.IsNullOrEmpty(shareUrl))
                {
                    // 凭证过期但链接还在 → 续传时会自动重解析，用户无感。
                    return new ResumeCheckResult { Can = true };
                }

                return new ResumeCheckResult
                {
                    Can = false,
                    Reason = "这条记录缺分享信息（分享 ID / 凭证），也没存原始分享链接，没法重新取直链。",
                    NextAction = "回到「下载」页把这个分享重新解析一次，再点下载 —— 分片都还在，会接着下，不会重下。",
                    Badge = "需重新解析",
                };
            }

            // 我的网盘：有 fid + dest 就够（直链可以重取）
            return new ResumeCheckResult { Can = true };
        }
    }
}
