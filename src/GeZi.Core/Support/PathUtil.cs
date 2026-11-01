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
using System.IO;
using System.Text.RegularExpressions;

namespace GeZi.Core.Support
{
    /// <summary>文件/路径工具，等价于核心里的 sanitize_filename / unique_path / base_name。</summary>
    public static class PathUtil
    {
        // 除 Windows 明令禁止的 9 个字符外，还要挡掉 0x00~0x1F 控制字符：
        // 网盘里真有人把 \t \n 放进文件名，落盘时 FileStream 会抛 ArgumentException，
        // 而这里不挡的话，那个异常会在下载很深的地方才冒出来。
        private static readonly Regex InvalidChars = new Regex(@"[\\/:*?""<>|\x00-\x1F]", RegexOptions.Compiled);

        /// <summary>
        /// Windows 保留设备名。这些名字（含带扩展名的 CON.txt）不能作为文件/目录名，
        /// 网盘里恰好有这种文件名时，落盘会在很多系统调用上直接失败。
        /// </summary>
        private static readonly Regex ReservedNames = new Regex(
            @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>单个目录/文件名上限（NTFS/绝大多数文件系统）。</summary>
        public const int MaxSegmentChars = 255;

        /// <summary>完整路径上限（Windows 的扩展长度上限）。</summary>
        public const int MaxPathChars = 32767;

        /// <summary>未启用长路径支持时，传统 MAX_PATH 的界限。</summary>
        public const int LegacyMaxPathChars = 260;

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "quark_file";
            string s = InvalidChars.Replace(name, "_").Trim().TrimEnd(' ', '.');
            if (string.IsNullOrEmpty(s))
                return "quark_file";
            // 保留设备名：CON、NUL、COM1… 直接落盘会失败，统一加前缀规避。
            if (ReservedNames.IsMatch(s))
                s = "_" + s;
            if (s.Length > MaxSegmentChars)
                s = s.Substring(0, MaxSegmentChars).TrimEnd(' ', '.');
            return string.IsNullOrEmpty(s) ? "quark_file" : s;
        }

        public static string UniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return path;
            string root, ext;
            SplitNameExt(path, out root, out ext);
            int i = 1;
            while (File.Exists(root + " (" + i + ")" + ext) || Directory.Exists(root + " (" + i + ")" + ext))
                i++;
            return root + " (" + i + ")" + ext;
        }

        /// <summary>
        /// 常见复合扩展名。必须整段当后缀，否则 <c>a.tar.gz</c> 会被
        /// <see cref="Path.GetExtension"/> 拆成 root=<c>a.tar</c> + ext=<c>.gz</c>，
        /// 重名后变成 <c>a.tar (1).gz</c> —— 名字看起来像被改坏了。
        /// </summary>
        private static readonly string[] MultiExtensions =
        {
            ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tar.lz", ".tar.lzma",
        };

        /// <summary>把路径拆成「去掉后缀的主体」和「后缀」，复合扩展名整体保留。</summary>
        private static void SplitNameExt(string path, out string root, out string ext)
        {
            string dir = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileName(path);

            foreach (var multi in MultiExtensions)
            {
                if (name.Length > multi.Length &&
                    name.EndsWith(multi, StringComparison.OrdinalIgnoreCase))
                {
                    root = Path.Combine(dir, name.Substring(0, name.Length - multi.Length));
                    ext = name.Substring(name.Length - multi.Length);
                    return;
                }
            }

            root = Path.Combine(dir, Path.GetFileNameWithoutExtension(path));
            ext = Path.GetExtension(path);
        }

        /// <summary>
        /// 带「本批已占用」集合的 <see cref="UniquePath(string)"/>。
        ///
        /// <para>
        /// 光看磁盘是不够的：一次选中两个同名文件（同一分享里两个目录各有一个 <c>video.mp4</c>，
        /// 或者用户连点两次「下载」）时，两个 dest 都还没落盘，
        /// <see cref="UniquePath(string)"/> 两次都返回同一个路径。
        /// 之后下载调度器发现 dest 重复，会把第二个任务直接丢掉并记「已跳过：同一文件正在下载中」
        /// —— 用户少下一个文件，界面上只有一行容易被忽略的日志。
        /// </para>
        /// <para>
        /// 命中集合时按同样的 <c>名称 (N).扩展名</c> 规则继续往后找，并把结果登记进集合。
        /// 集合用 <see cref="StringComparer.OrdinalIgnoreCase"/>，因为 Windows 路径不区分大小写。
        /// </para>
        /// </summary>
        public static string UniquePath(string path, ISet<string> taken)
        {
            if (taken == null)
                return UniquePath(path);

            string candidate = path;
            if (!File.Exists(candidate) && !Directory.Exists(candidate) && taken.Add(candidate))
                return candidate;

            string root, ext;
            SplitNameExt(path, out root, out ext);
            int i = 1;
            while (true)
            {
                candidate = root + " (" + i + ")" + ext;
                if (!File.Exists(candidate) && !Directory.Exists(candidate) && taken.Add(candidate))
                    return candidate;
                i++;
            }
        }

        /// <summary>是否为 Windows 保留设备名（可带扩展名）。</summary>
        public static bool IsReservedName(string name)
        {
            return !string.IsNullOrEmpty(name) && ReservedNames.IsMatch(name);
        }

        /// <summary>
        /// 引擎在目标文件旁边追加的最长后缀。
        ///
        /// <para>
        /// 分片目录是 <c>{dest}.qparts</c>，里面每个分片叫 <c>p000000</c>（7 字符），
        /// 所以真正会被创建的最长路径是 <c>{dest}.qparts\p000000</c> = dest + 15 字符；
        /// 续传记账 <c>{dest}.qmeta</c> 是 +6、<c>{dest}.geziresume</c> 是 +12。
        /// 只量 dest 本身会漏掉这 15 字符 —— 246~259 字符的 dest 一路检查全绿，
        /// 真正跑到 <c>Directory.CreateDirectory</c> 才抛 PathTooLong。
        /// </para>
        /// </summary>
        public const int EngineSuffixChars = 15;

        /// <summary>
        /// 下载前本地预检：路径长度、非法字符、保留名、目录可创建、目录可写。
        /// 不碰网络，也不检查磁盘剩余空间（那由下载器按实际还需字节数判断）。
        /// <paramref name="warning"/> 非空表示“能下，但用户该知道的风险”，不阻止下载。
        /// </summary>
        public static bool TryPreflight(string dest, out string error, out string warning)
        {
            error = null;
            warning = null;

            if (string.IsNullOrWhiteSpace(dest))
            {
                error = "目标路径为空。";
                return false;
            }

            string full;
            try
            {
                full = Path.GetFullPath(dest);
            }
            catch (Exception ex)
            {
                error = "路径无效：" + ex.Message;
                return false;
            }

            // 按「最长实际路径」判断，而不是 dest 本身。
            int effectiveLen = full.Length + EngineSuffixChars;

            if (effectiveLen > MaxPathChars)
            {
                error = "路径过长（超过 " + MaxPathChars + " 字符），请换一个更短的下载目录。";
                return false;
            }
            if (effectiveLen >= LegacyMaxPathChars)
            {
                warning = "路径长度已超过 " + (LegacyMaxPathChars - 1) +
                          " 字符（含分片文件）；若系统未启用长路径支持（LongPathsEnabled），下载可能在写文件时失败。";
            }

            string dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir))
            {
                error = "无法确定目标目录。";
                return false;
            }

            // 逐段检查长度与保留名（跳过 "C:" 这种驱动器段）
            string[] segs = full.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (string seg in segs)
            {
                if (seg.Length == 2 && seg[1] == ':')
                    continue;
                if (seg.Length > MaxSegmentChars)
                {
                    error = "路径中有过长的目录/文件名（超过 " + MaxSegmentChars + " 字符）。";
                    return false;
                }
                if (ReservedNames.IsMatch(seg))
                {
                    error = "Windows 保留名不能作为文件或目录名：" + seg;
                    return false;
                }
            }

            try
            {
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                error = "无法创建目标目录：" + ex.Message;
                return false;
            }

            // 目录可写预检：只建一个空探测文件再删，代价极小，但能提前拦住只读/权限问题。
            string probe = Path.Combine(dir, ".gezi_write_probe_" + Guid.NewGuid().ToString("N"));
            try
            {
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                error = "目标目录不可写：" + ex.Message;
                return false;
            }

            return true;
        }
    }
}