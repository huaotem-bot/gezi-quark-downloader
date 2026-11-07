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

using System.Globalization;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 源文件指纹：识别「同一个目标路径换成了另一个源文件」。
    /// <para>
    /// 🚨🚨 为什么需要（2026-11-04 用户实测事故）：
    /// 续传状态 <c>.geziresume</c> 是**按目标路径**定位的（见 PartsPaths.ResolvePaths），
    /// 而 <see cref="PathUtil.UniquePath"/> 会把同名文件落成「名称 (1)」。
    /// 于是「先下夸克的 示例视频（15,340,287 字节），再下 UC 的同名文件（186,678,707 字节）」时，
    /// 新任务会命中旧路径上的续传状态 —— 只要长度碰巧相同，
    /// 引擎就会拿旧进度续新文件，拼出「长度对、内容坏」的成品。
    /// </para>
    /// <para>
    /// 🚨 为什么不靠 ETag：那是**服务端**给的，CDN 不回、探测超时、走 OpenSSL 桥时都会缺失。
    /// 旧版在缺失时**静默跳过校验**，等于旧状态被无条件采信。
    /// 这里用**本地必然有值**的三元组，与网络无关。
    /// </para>
    /// <para>
    /// 放在 Core 是为了让自检项目能直接覆盖（自检不引用 WPF 主项目）。
    /// </para>
    /// </summary>
    public static class SourceKey
    {
        /// <summary>
        /// 拼指纹：<c>总长|网盘序号|源文件Fid</c>。
        /// </summary>
        /// <param name="size">源文件字节数；<c>&lt;=0</c> 表示未知。</param>
        /// <param name="panKind">源文件所在网盘（PanKind 的整数值：夸克0 / UC1 / 阿里2 / 迅雷3）。</param>
        /// <param name="fid">源文件在网盘上的 fid，可为空。</param>
        public static string Build(long size, int panKind, string fid)
        {
            // 大小未知时写 "?" 而不是 "0"：
            // 否则「先建记录时 Size=0、取链后拿到真实大小」会被误判成「换了文件」，
            // 白白丢掉本来正常的续传进度。
            string sizePart = size > 0
                ? size.ToString(CultureInfo.InvariantCulture)
                : "?";
            return sizePart + "|"
                + panKind.ToString(CultureInfo.InvariantCulture) + "|"
                + (fid ?? "");
        }
    }
}
