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
using System.Globalization;
using System.Text.RegularExpressions;

namespace GeZi.Core.Update
{
    /// <summary>
    /// 三段式版本号（主.次.补），用于更新检查。
    /// <see cref="Version"/> 会带第 4 段
    /// （<c>2.2.0.0</c>），而需求只按三段比；而且它比较的是 Revision，容易和发布号混淆。
    /// 这里收窄成三段，语义明确、也便于单测。
    /// </summary>
    public sealed class UpdateVersion : IComparable<UpdateVersion>, IEquatable<UpdateVersion>
    {
        public int Major { get; private set; }
        public int Minor { get; private set; }
        public int Patch { get; private set; }

        public UpdateVersion(int major, int minor, int patch)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
        }

        /// <summary>
        /// 解析版本字符串，如 <c>2.2.0</c> / <c>v2.2.0</c> / <c>2.2.0.0</c>。
        /// </summary>
        /// <remarks>
        /// · 允许可选的 <c>v</c>/<c>V</c> 前缀；
        /// · 接受 1~4 段，<b>只用前三段</b>，第 4 段（AssemblyRevision）忽略 ——
        ///   <see cref="Version.ToString()"/> 默认会输出 4 段，这里要能直接吃；
        /// · 缺的段按 0 补（<c>"2"</c> → 2.0.0）。
        /// </remarks>
        public static bool TryParse(string text, out UpdateVersion result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string s = text.Trim();
            if (s[0] == 'v' || s[0] == 'V') s = s.Substring(1);
            if (s.Length == 0) return false;

            string[] parts = s.Split('.');
            if (parts.Length < 1 || parts.Length > 4) return false;

            int[] nums = new int[3];
            for (int i = 0; i < 3; i++)
            {
                if (i >= parts.Length) { nums[i] = 0; continue; }
                int v;
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out v))
                    return false;
                nums[i] = v;
            }
            // 第 4 段若存在也要能解析（否则是格式错误，不是"忽略"）
            if (parts.Length == 4)
            {
                int dummy;
                if (!int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out dummy))
                    return false;
            }

            result = new UpdateVersion(nums[0], nums[1], nums[2]);
            return true;
        }

        /// <summary>把 <see cref="Version"/>（程序集版本）转成三段式。</summary>
        public static UpdateVersion FromVersion(Version v)
        {
            if (v == null) return null;
            return new UpdateVersion(
                v.Major >= 0 ? v.Major : 0,
                v.Minor >= 0 ? v.Minor : 0,
                v.Build >= 0 ? v.Build : 0);
        }

        /// <summary>主 &gt; 次 &gt; 补，逐段比较数值大小。</summary>
        public int CompareTo(UpdateVersion other)
        {
            if (other == null) return 1;
            if (Major != other.Major) return Major > other.Major ? 1 : -1;
            if (Minor != other.Minor) return Minor > other.Minor ? 1 : -1;
            if (Patch != other.Patch) return Patch > other.Patch ? 1 : -1;
            return 0;
        }

        public bool Equals(UpdateVersion other)
        {
            if (other == null) return false;
            return Major == other.Major && Minor == other.Minor && Patch == other.Patch;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as UpdateVersion);
        }

        public override int GetHashCode()
        {
            return (Major * 1000003) ^ (Minor * 10007) ^ Patch;
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}", Major, Minor, Patch);
        }
    }
}
