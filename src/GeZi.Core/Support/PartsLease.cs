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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using GeZi.Core.Api;
using GeZi.Core.Models;
using GeZi.Core.Support;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 分片目录租约。让清扫器区分“正在用/暂停中”与“真孤儿”。
    /// <para>
    /// 只看文件 mtime 有一个致命问题：暂停的任务不再写分片，mtime 很快变旧，
    /// 清扫器就会把用户还想续传的断点删掉。租约文件在任务开始时写入，
    /// 运行期间由进度上报线程持续刷新心跳；清扫器看到未过期的租约就整目录跳过。
    /// </para>
    /// <para>
    /// 租约不是安全边界，只是本地状态提示；写不进去时下载仍然继续，
    /// 清扫器只是退回到按 mtime 判断（可能误删），所以这里不抛异常。
    /// </para>
    /// </summary>
    public static class PartsLease
    {
        public const string FileName = ".lease";

        /// <summary>租约保护期。暂停超过这个天数的任务才允许被自动回收。</summary>
        public const int ProtectDays = 30;

        private const string FormatVersion = "1";

        /// <summary>租约状态。用于诊断与后续“完成待提交”等策略。</summary>
        public enum State
        {
            /// <summary>正在下载/暂停中（都算“用户在意的任务”）。</summary>
            Active = 0,
            /// <summary>下载已完成，等待合并/校验/清理。</summary>
            Finalizing = 1,
            /// <summary>已完成且用户标记保留断点。</summary>
            Pinned = 2,
        }

        /// <summary>
        /// 写入/刷新租约（心跳）。任务开始与运行期间定期调用。
        /// <paramref name="state"/> 只影响诊断语义，不影响保护期。
        /// </summary>
        public static void Touch(string partsDir, State state = State.Active)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(partsDir)) return;
                Directory.CreateDirectory(partsDir);
                DateTime now = DateTime.UtcNow;
                var sb = new StringBuilder();
                sb.Append("v=").Append(FormatVersion).Append('\n');
                sb.Append("pid=").Append(CurrentPid()).Append('\n');
                sb.Append("state=").Append(state.ToString()).Append('\n');
                sb.Append("heartbeatUtc=").Append(now.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
                sb.Append("expiresUtc=").Append(now.AddDays(ProtectDays).ToString("O", CultureInfo.InvariantCulture)).Append('\n');
                File.WriteAllText(Path.Combine(partsDir, FileName), sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
                // 租约是“尽量保护”，不是下载的前置条件；写不进去不阻塞任务。
            }
        }

        /// <summary>
        /// 目录是否仍在租约保护期内。读不到/格式不对时保守返回 true，
        /// 宁可少清一个目录，也不要误删用户断点。
        /// </summary>
        public static bool IsProtected(string partsDir, DateTime utcNow)
        {
            try
            {
                string path = Path.Combine(partsDir, FileName);
                if (!File.Exists(path)) return false;

                string[] lines = File.ReadAllLines(path);
                if (lines.Length == 0) return true;

                DateTime? expires = null;
                int pid = 0;
                bool hasPid = false;
                bool looksLikeV1 = false;

                foreach (string raw in lines)
                {
                    string line = (raw ?? "").Trim();
                    if (line.Length == 0) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        // 旧格式：第一行就是一个 ISO 过期时间。
                        if (!looksLikeV1 && expires == null)
                        {
                            DateTime legacy;
                            if (DateTime.TryParse(line, CultureInfo.InvariantCulture,
                                    DateTimeStyles.RoundtripKind, out legacy))
                            {
                                expires = legacy;
                                looksLikeV1 = true;
                            }
                        }
                        continue;
                    }

                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (key.Equals("expiresUtc", StringComparison.OrdinalIgnoreCase))
                    {
                        DateTime t;
                        if (DateTime.TryParse(val, CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind, out t))
                            expires = t;
                    }
                    else if (key.Equals("pid", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid))
                            hasPid = true;
                    }
                }

                // 本进程自己持有的租约永远有效（即使心跳暂时没刷新）。
                if (hasPid && pid == CurrentPid())
                    return true;

                // 持有者进程还活着 → 保护；进程没了也还没过期 → 仍保护（等宽限期）。
                if (hasPid && pid > 0 && IsProcessAlive(pid))
                    return true;

                if (expires.HasValue)
                    return expires.Value > utcNow;

                return true;   // 解析不出过期时间：保守保护
            }
            catch
            {
                return true;
            }
        }

        /// <summary>读取租约状态；解析失败返回 null。供诊断/日志使用。</summary>
        public static State? ReadState(string partsDir)
        {
            try
            {
                string path = Path.Combine(partsDir, FileName);
                if (!File.Exists(path)) return null;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = (raw ?? "").Trim();
                    const string prefix = "state=";
                    if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    State s;
                    if (Enum.TryParse(line.Substring(prefix.Length).Trim(), true, out s))
                        return s;
                }
            }
            catch { }
            return null;
        }

        /// <summary>任务成功收尾、分片目录即将删除时调用。</summary>
        public static void Release(string partsDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(partsDir)) return;
                File.Delete(Path.Combine(partsDir, FileName));
            }
            catch { }
        }

        private static int CurrentPid()
        {
            try { return Process.GetCurrentProcess().Id; }
            catch { return 0; }
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                    return !p.HasExited;
            }
            catch
            {
                // 进程不存在 / 无权限查询 → 当作不存活，交给过期时间兜底。
                return false;
            }
        }
    }
}