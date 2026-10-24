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
using System.IO;
using Microsoft.Win32;

namespace GeZi
{
    /// <summary>
    /// 本机播放器探测（**只管 VLC**）。
    /// 为什么需要它：边下边播依赖**外部播放器**打开一个"还没下完"的文件。
    /// 而能否播得起来，除了格式之外还取决于播放器的**文件共享模式** ——
    /// VLC 用 <c>FileShare.ReadWrite</c> 打开，与我们写盘互不干扰；
    /// 部分播放器只要只读共享，会让我们后续写盘失败。
    /// 🚨 **不探测 mpv / PotPlayer**。
    ///   原因：全项目只对 VLC 做过参数适配（`ShellLaunch` 里给 VLC 加
    ///   `--started-from-file`，与资源管理器右键行为对齐；其它播放器不认这个参数），
    ///   用其它播放器可能出现行为不一致的 bug → 索性**只认 VLC**，
    ///   不给用户"还能用 mpv"的预期。
    ///   （历史实现曾探测 VLC → mpv → PotPlayer，已按下述只留 VLC。）
    /// 探测顺序：注册表 App Paths → 常见安装目录 → PATH。
    /// 全部失败返回 null，由调用方回退到"系统默认程序"。
    /// </summary>
    internal static class PlayerLocator
    {
        internal sealed class Player
        {
            public string Name;      // 显示名
            public string ExePath;   // 可执行文件完整路径
            public bool Recommended; // 保留字段（现在恒为 true，只探测 VLC）
        }

        /// <summary>探测 VLC。没找到返回 null。</summary>
        public static Player Find()
        {
            return FindVlc();
        }

        /// <summary>VLC —— 唯一支持的播放器：格式全，且以 FileShare.ReadWrite 打开文件。</summary>
        public static Player FindVlc()
        {
            string p = Probe("vlc.exe", "VideoLAN\\VLC\\vlc.exe",
                             "VideoLAN\\VLC\\vlc.exe");
            return p == null ? null : new Player { Name = "VLC", ExePath = p, Recommended = true };
        }

        /// <summary>
        /// 三路探测：注册表 App Paths → 常见安装目录（含 32 位重定向）→ PATH。
        /// </summary>
        private static string Probe(string exeName, string relX64, string relX86)
        {
            // ① 注册表 App Paths —— 安装程序正规登记的位置，最可靠
            foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName,
                                         @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" + exeName })
            {
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(root))
                    {
                        if (k == null) continue;
                        var v = k.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(v) && File.Exists(v)) return v;
                    }
                }
                catch { }
            }

            // ② 常见安装目录
            foreach (var baseDir in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     })
            {
                if (string.IsNullOrEmpty(baseDir)) continue;
                foreach (var rel in new[] { relX64, relX86 })
                {
                    if (string.IsNullOrEmpty(rel)) continue;
                    try
                    {
                        string p = Path.Combine(baseDir, rel);
                        if (File.Exists(p)) return p;
                    }
                    catch { }
                }
            }

            // ③ PATH —— 便携版 / 解压版常这么装
            try
            {
                string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathVar.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        string p = Path.Combine(dir.Trim(), exeName);
                        if (File.Exists(p)) return p;
                    }
                    catch { }
                }
            }
            catch { }

            return null;
        }
    }
}
