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
using System.IO;
using System.Text.RegularExpressions;

namespace GeZi.Core.Support
{
    /// <summary>文件/路径工具，等价于核心里的 sanitize_filename / unique_path / base_name。</summary>
    public static class PathUtil
    {
        private static readonly Regex InvalidChars = new Regex(@"[\\/:*?""<>|]", RegexOptions.Compiled);

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
            string dir = Path.GetDirectoryName(path) ?? "";
            string root = Path.Combine(dir, Path.GetFileNameWithoutExtension(path));
            string ext = Path.GetExtension(path);
            int i = 1;
            while (File.Exists(root + " (" + i + ")" + ext) || Directory.Exists(root + " (" + i + ")" + ext))
                i++;
            return root + " (" + i + ")" + ext;
        }

        /// <summary>是否为 Windows 保留设备名（可带扩展名）。</summary>
        public static bool IsReservedName(string name)
        {
            return !string.IsNullOrEmpty(name) && ReservedNames.IsMatch(name);
        }

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

            if (full.Length > MaxPathChars)
            {
                error = "路径过长（超过 " + MaxPathChars + " 字符），请换一个更短的下载目录。";
                return false;
            }
            if (full.Length >= LegacyMaxPathChars)
            {
                warning = "路径长度已超过 " + (LegacyMaxPathChars - 1) +
                          " 字符；若系统未启用长路径支持（LongPathsEnabled），下载可能在写文件时失败。";
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