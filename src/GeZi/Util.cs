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
        public static string FormatSize(long n)
        {
            // 🚨 原来除以 1024 却标 "KB/MB/GB"（那是 KiB/MiB/GiB）。
            // 后果：同一个文件「夸克分享页写 9.54 GB、本程序写 8.89 GB」，用户以为少下了。
            // 改成 **1024 进制**（2026-10-24 又改回来：实测夸克容量接口 total=10737418240 正好 10x1024^3，夸克自己就是 1024 进制） —— 单位名副其实，也顺带让速度显示符合网络速率的惯例（1 MB/s = 10^6 B/s）。
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
