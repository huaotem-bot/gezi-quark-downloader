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
using System.Runtime.InteropServices;
using GeZi.Providers;
using GeZi.Providers.Quark;
using GeZi.Providers.Uc;
using GeZi.Providers.Aliyun;
using GeZi.Providers.Xunlei;
using GeZi.Providers.Shared;
using GeZi.Providers.Qr;

using GeZi.Services;
namespace GeZi
{
    public static class Util
    {
        /// <summary>
        /// **全程序唯一**的字节数格式化（2026-11-04 用户拍板统一）。
        /// <para>
        /// 🚨🚨 统一为 **1024 进制**（KiB/MiB/GiB 的数值，但沿用 KB/MB/GB 的写法）。
        /// 理由：
        ///   ① Windows 资源管理器用 1024 —— 用户拿它对照，必须一致；
        ///   ② 夸克 / UC / 阿里 / 迅雷 四家网页版都是 1024
        ///      （实测夸克容量接口 total=10737418240 正好 = 10 × 1024³）；
        ///   ③ 程序内所有地方（我的网盘、分享列表、任务页、日志、历史、小窗）
        ///      必须用同一套，否则同一个文件在不同页面显示不同大小。
        /// </para>
        /// <para>
        /// ⚠️ **不要再出现第二份实现**。历史上 <c>ShareFileItem.cs</c> 自己写了一份
        /// 1000 进制的私有 FormatSize，导致「分享列表 15.34 MB / 任务页 14.63 MB」
        /// 同一个文件差 4.9%，用户反复报「大小对不上」。
        /// 现在那边已改为直接调用本方法。
        /// </para>
        /// </summary>
        public static string FormatSize(long n)
        {
            if (n < 0) n = 0;
            double v = n;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (v >= 1024 && i < units.Length - 1)
            {
                v /= 1024.0;
                i++;
            }
            return i == 0 ? n + " B" : v.ToString("F2") + " " + units[i];
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out string ppszPath);

        /// <summary>取 Windows「下载」文件夹的真实路径（可能被重定向到别的盘/OneDrive），与原版 _downloads_dir 一致。</summary>
        public static string GetDownloadsFolder()
        {
            try
            {
                // FOLDERID_Downloads
                var fid = new Guid("374DE290-123F-4565-9164-39C4925E467B");
                int hres = SHGetKnownFolderPath(ref fid, 0, IntPtr.Zero, out string path);
                if (hres == 0 && !string.IsNullOrEmpty(path) && Directory.Exists(path))
                    return path;
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        /// <summary>尝试创建目录并写入一个测试文件，判断该目录是否真的可写。</summary>
        public static bool IsWritable(string dir)
        {
            if (string.IsNullOrEmpty(dir))
                return false;
            try
            {
                Directory.CreateDirectory(dir);
                string test = Path.Combine(dir, ".w_" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(test, "1");
                File.Delete(test);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>应用本地数据目录（%LOCALAPPDATA%，系统保证可写、不受「受控文件夹访问」保护）。</summary>
        public static string GetLocalAppDataDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "鸽子下载");
        }
    }
}
