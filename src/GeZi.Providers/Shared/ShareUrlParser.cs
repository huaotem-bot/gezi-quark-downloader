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

using System.Text.RegularExpressions;
using GeZi.Core.Api;
using GeZi.Core.Models;
using GeZi.Core.Support;

namespace GeZi.Providers.Shared
{
    public sealed class ShareUrlInfo
    {
        public string PwdId { get; set; }
        public string Passcode { get; set; } = "";
        public string StartFid { get; set; } = "0";

        /// <summary>
        /// 当整段输入是**夸克口令**（形如 <c>/~469d3M9zTu~:/</c>）而不是链接时，
        /// 这里存口令里的那串码。夸克没有公开的「口令 → 分享链接」接口，
        /// 所以只能识别出来并给出可操作的提示，不能真的解析（见 IsKouling）。
        /// </summary>
        public string Kouling { get; set; } = "";

        /// <summary>是纯口令（没有可用链接）—— 调用方应提示用户改用分享链接。</summary>
        public bool IsKouling
        {
            get { return !string.IsNullOrEmpty(Kouling) && string.IsNullOrEmpty(PwdId); }
        }
    }

    /// <summary>
    /// 解析分享链接 / 分享文案，等价于核心里的 parse_share_url，并额外支持：
    ///   ① 直接粘贴**整段分享文案**（「链接：… 提取码：abcd」）—— 提取码也能认出来；
    ///   ② 识别**夸克口令**（<c>/~xxxx~:/</c>）并标记出来，便于上层给出准确提示。
    /// </summary>
    public static class ShareUrlParser
    {
        // /s/xxxxx —— 分享 ID
        private static readonly Regex ShareId =
            new Regex(@"/s/([A-Za-z0-9_-]+)", RegexOptions.Compiled);

        // ?pwd=xxxx / &pwd=xxxx
        private static readonly Regex Passcode =
            new Regex(@"pwd=([A-Za-z0-9]+)", RegexOptions.Compiled);

        // 中文文案里的提取码：提取码：abcd / 提取码 abcd / 密码:abcd / 访问码：abcd / pwd=abcd
        // ⚠️ 前面加负向后顾 `(?<![/A-Za-z0-9])`：分享 ID 本身可能就叫 pwd1234
        //    （https://pan.quark.cn/s/pwd1234），不加的话会把 "pwd1234" 当提取码解析成 "1234"。
        private static readonly Regex PasscodeCn = new Regex(
            @"(?<![/A-Za-z0-9])(?:提取码|提取碼|密码|密碼|访问码|訪問碼|口令|pwd|PWD|Pwd)\s*[=:：]?\s*([A-Za-z0-9]{1,12})",
            RegexOptions.Compiled);

        // 夸克 PC 客户端口令： /~469d3M9zTu~:/
        private static readonly Regex Kouling =
            new Regex(@"/~([A-Za-z0-9]{4,32})~:?/?", RegexOptions.Compiled);

        private static readonly Regex Fid32 =
            new Regex(@"/([A-Za-z0-9]{32})(?:-[^/?#]*)?", RegexOptions.Compiled);

        public static ShareUrlInfo Parse(string url)
        {
            var info = new ShareUrlInfo();
            if (string.IsNullOrEmpty(url))
                return info;

            var m = ShareId.Match(url);
            if (m.Success)
                info.PwdId = m.Groups[1].Value;

            m = Passcode.Match(url);
            if (m.Success)
                info.Passcode = m.Groups[1].Value;

            if (string.IsNullOrEmpty(info.Passcode))
            {
                // 整段分享文案里的「提取码：abcd」
                m = PasscodeCn.Match(url);
                if (m.Success)
                    info.Passcode = m.Groups[1].Value;
            }

            m = Kouling.Match(url);
            if (m.Success)
                info.Kouling = m.Groups[1].Value;

            foreach (Match fm in Fid32.Matches(url))
            {
                string fid = fm.Groups[1].Value;
                if (fid != info.PwdId) // 排除恰好 32 位的分享 ID
                    info.StartFid = fid;
            }
            return info;
        }
    }
}
