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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 诊断包：把「用户遇到问题时要提供的信息」一次性打成 zip，
    /// 免去让用户自己找日志、报环境、截图设置页。
    ///
    /// <para><b>明确不包含</b>：账号 Cookie、登录票据（ticket）、下载直链与分享口令。
    /// 这些要么在调用方就已经脱敏，要么根本不采集 —— 诊断包是给外人看的，
    /// 不能把"能不能登我账号"的东西一起发出去。</para>
    /// </summary>
    public static class DiagnosticsPack
    {
        /// <summary>
        /// 生成诊断包，返回实际写出的 zip 路径。
        /// </summary>
        /// <param name="outZipPath">目标 zip 路径（目录不存在会自动创建）。</param>
        /// <param name="runtimeLog">运行日志文本（UI 的日志框内容即可）。</param>
        /// <param name="extraInfo">额外的键值信息（设置摘要等），调用方负责脱敏。</param>
        public static string Create(string outZipPath, string appName, string appVersion,
            string runtimeLog = null, IEnumerable<KeyValuePair<string, string>> extraInfo = null)
        {
            if (string.IsNullOrWhiteSpace(outZipPath))
                throw new ArgumentException("诊断包输出路径为空。", nameof(outZipPath));

            string full = Path.GetFullPath(outZipPath);
            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // 先写临时文件再改名：中断也不会留下一个"看起来正常但打不开"的 zip。
            string tmp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    WriteText(zip, "info.txt", BuildInfo(appName, appVersion, extraInfo));
                    WriteText(zip, "runtime.log", string.IsNullOrEmpty(runtimeLog) ? "(无运行日志)" : runtimeLog);
                    WriteText(zip, "README.txt",
                        "GeZi 诊断包" + Environment.NewLine +
                        "本包包含：环境信息、设置摘要、运行日志。" + Environment.NewLine +
                        "本包不包含：账号 Cookie、登录票据、下载直链、分享口令。" + Environment.NewLine);
                }

                if (File.Exists(full))
                    File.Delete(full);
                File.Move(tmp, full);
                return full;
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        private static void WriteText(ZipArchive zip, string entryName, string content)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var s = entry.Open())
            using (var w = new StreamWriter(s, new UTF8Encoding(false)))
            {
                w.Write(content ?? "");
            }
        }

        private static string BuildInfo(string appName, string appVersion,
            IEnumerable<KeyValuePair<string, string>> extraInfo)
        {
            var sb = new StringBuilder();
            sb.Append("应用: ").Append(appName ?? "GeZi").Append('\n');
            sb.Append("版本: ").Append(appVersion ?? "unknown").Append('\n');
            sb.Append("生成时间: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
              .Append(" (本地) / ")
              .Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
              .Append(" (UTC)\n");
            sb.Append('\n');

            sb.Append("--- 运行环境 ---\n");
            sb.Append("OS: ").Append(Environment.OSVersion).Append('\n');
            sb.Append("64 位系统: ").Append(Environment.Is64BitOperatingSystem).Append('\n');
            sb.Append("64 位进程: ").Append(Environment.Is64BitProcess).Append('\n');
            sb.Append("CLR: ").Append(Environment.Version).Append('\n');
            sb.Append("CPU 逻辑核心: ").Append(Environment.ProcessorCount).Append('\n');
            sb.Append("长路径(Early-longPaths): ").Append(IsLongPathEnabled() ? "已启用" : "未启用").Append('\n');
            sb.Append("当前目录: ").Append(SafeCurrentDirectory()).Append('\n');

            try
            {
                using (var p = Process.GetCurrentProcess())
                {
                    sb.Append("工作集: ").Append(p.WorkingSet64 / (1024 * 1024)).Append(" MB\n");
                    sb.Append("私有内存: ").Append(p.PrivateMemorySize64 / (1024 * 1024)).Append(" MB\n");
                    sb.Append("线程数: ").Append(p.Threads.Count).Append('\n');
                }
            }
            catch { }
            sb.Append("GC 已用托管内存: ").Append(GC.GetTotalMemory(false) / (1024 * 1024)).Append(" MB\n");

            if (extraInfo != null)
            {
                sb.Append('\n').Append("--- 设置摘要（已脱敏） ---\n");
                var list = new List<KeyValuePair<string, string>>(extraInfo);
                list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                foreach (var kv in list)
                    sb.Append(kv.Key).Append(": ").Append(kv.Value ?? "").Append('\n');
            }

            return sb.ToString();
        }

        private static string SafeCurrentDirectory()
        {
            try { return Environment.CurrentDirectory; }
            catch { return "(不可用)"; }
        }

        private static bool IsLongPathEnabled()
        {
            try
            {
                object v = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem",
                    "LongPathsEnabled", 0);
                return v is int && (int)v != 0;
            }
            catch
            {
                return false;
            }
        }
    }
}