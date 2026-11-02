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
            if (string.IsNullOrEmpty(dest) || string.IsNullOrEmpty(fid))
                return false;

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
                    return true;

                return !string.IsNullOrEmpty(token)
                    && !string.IsNullOrEmpty(pwdId)
                    && !string.IsNullOrEmpty(stoken);
            }

            // 我的网盘：有 fid + dest 就够（直链可以重取）
            return true;
        }
    }
}
