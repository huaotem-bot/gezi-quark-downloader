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
using System.Collections.Generic;
using System.IO;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 分片残留清扫器：删除各分片目录里「超过 N 天未被触碰」的孤儿分片。
    /// <para></para>
    /// <para>
    /// 用户中途取消、换分片目录、或程序被强杀时，分片文件会留在磁盘上。
    /// 没有清扫机制的话，这些残留**只增不减** —— 用户会明显感到"磁盘越用越少"，
    /// 却找不到是谁占的（分片目录默认还是隐藏的）。Python Flet 版靠启动时的一次
    /// 后台清扫解决这个问题，C# 版此前没有对应实现。
    /// </para>
    /// <para></para>
    /// <para>
    /// 正在下载的分片，其 mtime 会被持续刷新，所以**永远不会**被误删；
    /// 只有"早已放弃"的分片（超过阈值没被碰过）才会被回收。这正是我们要的语义。
    /// </para>
    /// <para></para>
    /// <para>
    /// 中断/暂停超过 <see cref="DefaultMaxAgeDays"/> 天的下载，其分片会在下次启动时
    /// 被清掉，该任务将**无法续传**（需从 0 重下）。这是"自动回收磁盘"与
    /// "无限期保留断点"之间必须做的取舍；阈值取得足够大就不会误伤正常使用。
    /// </para>
    /// <para></para>
    /// <para>
    /// 清扫在**独立后台线程**执行，且每删一个文件都容忍
    /// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>
    /// （文件正被下载器占用、权限不足等）—— 删不掉就跳过，绝不因此影响下载。
    /// </para>
    /// </summary>
    public static class PartsSweeper
    {
        /// <summary>
        /// 默认「多久没被触碰就算孤儿」的天数。与 Python Flet 版保持一致（1 天）。
        /// </summary>
        public const int DefaultMaxAgeDays = 1;

        /// <summary>清扫结果，用于写日志。</summary>
        public struct SweepResult
        {
            /// <summary>扫描到的分片文件总数（含未过期的）。</summary>
            public int Scanned;
            /// <summary>实际删除的文件数。</summary>
            public int Removed;
            /// <summary>因被占用/权限不足而跳过的文件数。</summary>
            public int Skipped;
            /// <summary>释放的字节数。</summary>
            public long FreedBytes;
            /// <summary>被扫描的分片根目录数。</summary>
            public int RootsScanned;

            public bool AnythingDone => Removed > 0;
        }

        /// <summary>
        /// 清扫给定的全部分片根目录（含历史目录）。
        /// <para>
        /// <paramref name="partsRoots"/> 应传入"当前生效 + 历史上用过的"全部根目录
        /// （界面层用 <c>AllPartsRoots()</c> 得到）；只传当前的那个会漏掉旧目录里的残留。
        /// 传 <c>null</c> 或空集合时，额外清扫一遍**默认位置**（目标文件同级的
        /// <c>*.qparts</c> 目录）—— 那是用户从没设过自定义根目录时的实际落盘点。
        /// </para>
        /// </summary>
        /// <param name="partsRoots">分片根目录集合；可为 null。</param>
        /// <param name="maxAgeDays">超过多少天没被触碰即视为孤儿；&lt;=0 时用默认值。</param>
        /// <param name="shouldStop">
        /// 可选的取消回调：返回 true 表示外部要求中止（如程序正在退出）。
        /// 每处理一个目录检查一次，便于快速退出。
        /// </param>
        public static SweepResult Sweep(IEnumerable<string> partsRoots, int maxAgeDays,
            Func<bool> shouldStop = null)
        {
            if (maxAgeDays <= 0) maxAgeDays = DefaultMaxAgeDays;

            var result = new SweepResult();
            DateTime cutoff = DateTime.Now.AddDays(-maxAgeDays);

            foreach (string dir in EnumerateTargetDirs(partsRoots))
            {
                if (shouldStop != null && shouldStop())
                    break;

                result.RootsScanned++;
                SweepOneDir(dir, cutoff, ref result);
            }

            return result;
        }

        /// <summary>
        /// 清扫默认落盘位置：下载目录下的 <c>*.qparts</c>（以及对应的 <c>*.qmeta</c>）。
        /// 这些目录不在用户配置的 partsRoots 里，旧版清扫器永远看不到。
        /// </summary>
        public static SweepResult SweepDefault(IEnumerable<string> downloadRoots, int maxAgeDays,
            Func<bool> shouldStop = null)
        {
            if (maxAgeDays <= 0) maxAgeDays = DefaultMaxAgeDays;
            var result = new SweepResult();
            DateTime cutoff = DateTime.Now.AddDays(-maxAgeDays);

            if (downloadRoots == null) return result;
            foreach (string raw in downloadRoots)
            {
                string root = (raw ?? "").Trim();
                if (root.Length == 0 || !SafeIsDir(root)) continue;
                result.RootsScanned++;

                string[] qpartsDirs;
                try
                {
                    qpartsDirs = Directory.GetDirectories(root, "*.qparts", SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }

                foreach (string dir in qpartsDirs)
                {
                    if (shouldStop != null && shouldStop()) return result;
                    result.RootsScanned++;

                    // 有租约 → 整个目录（含旁边的 .qmeta）都保留。
                    if (PartsLease.IsProtected(dir, DateTime.UtcNow))
                    {
                        result.Skipped++;
                        continue;
                    }

                    SweepOneDir(dir, cutoff, ref result);

                    string dest = dir.Substring(0, dir.Length - ".qparts".Length);
                    string meta = dest + ".qmeta";
                    try
                    {
                        if (File.Exists(meta) && File.GetLastWriteTime(meta) < cutoff)
                        {
                            long size = 0;
                            try { size = new FileInfo(meta).Length; } catch { }
                            File.Delete(meta);
                            result.Scanned++;
                            result.Removed++;
                            result.FreedBytes += size;
                        }

                        string metaBak = meta + ".bak";
                        if (File.Exists(metaBak) && File.GetLastWriteTime(metaBak) < cutoff)
                        {
                            long size = 0;
                            try { size = new FileInfo(metaBak).Length; } catch { }
                            File.Delete(metaBak);
                            result.Scanned++;
                            result.Removed++;
                            result.FreedBytes += size;
                        }
                    }
                    catch (IOException) { result.Skipped++; }
                    catch (UnauthorizedAccessException) { result.Skipped++; }
                }
            }
            return result;
        }

        /// <summary>
        /// 枚举需要清扫的目录 —— 先给每个根目录本身，再给它下面一层子目录
        /// （分片根目录下的结构是 <c>{root}\{slug}_{hash}\parts\</c>，真正的分片在更深处）。
        /// 同时兜底扫一遍"目标文件同级"的默认分片目录无法枚举（那需要目标路径），
        /// 所以这里只处理用户显式配置过的根目录。
        /// </summary>
        private static IEnumerable<string> EnumerateTargetDirs(IEnumerable<string> partsRoots)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (partsRoots != null)
            {
                foreach (string raw in partsRoots)
                {
                    string root = (raw ?? "").Trim();
                    if (root.Length == 0) continue;

                    // 根目录本身（分片可能直接堆在这里）
                    if (seen.Add(root) && SafeIsDir(root))
                        yield return root;

                    // 一层子目录：{root}\{slug}_{hash}\parts 里的 parts，
                    // 以及 {root}\{slug}_{hash} 本身
                    foreach (string sub in SafeEnumerateDirs(root))
                    {
                        if (seen.Add(sub))
                            yield return sub;

                        string parts = Path.Combine(sub, "parts");
                        if (seen.Add(parts) && SafeIsDir(parts))
                            yield return parts;
                    }
                }
            }
        }

        /// <summary>清扫单个目录里的过期分片文件。</summary>
        private static void SweepOneDir(string dir, DateTime cutoff, ref SweepResult result)
        {
            // 有未过期租约 → 整个目录跳过。暂停中的任务不写分片，mtime 会很快变旧；
            // 只看 mtime 会把“用户还想续传”的断点删掉。
            if (PartsLease.IsProtected(dir, DateTime.UtcNow))
            {
                result.Skipped++;
                return;
            }

            string[] names;
            try { names = Directory.GetFiles(dir); }
            catch { return; }   // 不存在/无权限 → 跳过

            foreach (string full in names)
            {
                if (!IsPartFile(full)) continue;
                result.Scanned++;

                try
                {
                    var fi = new FileInfo(full);
                    // mtime 比 cutoff 新 → 还在用（或刚用过），保留
                    if (fi.LastWriteTime >= cutoff) continue;

                    long size = 0;
                    try { size = fi.Length; } catch { }

                    // FileOptions.DeleteOnClose 的思路在这里不适用：我们需要"删不掉就跳过"。
                    try
                    {
                        File.Delete(full);
                        result.Removed++;
                        result.FreedBytes += size;
                    }
                    catch (IOException) { result.Skipped++; }
                    catch (UnauthorizedAccessException) { result.Skipped++; }
                }
                catch (IOException) { result.Skipped++; }
                catch (UnauthorizedAccessException) { result.Skipped++; }
            }
        }

        /// <summary>
        /// 是不是分片文件。与 Python 版判据一致（<c>.partmeta</c> 或文件名含 <c>.part</c>），
        /// 另加 C# 版自己的两种：<c>.qmeta</c>（续传元数据）与 <c>meta.json</c>（自定义根目录下）。
        /// </summary>
        private static bool IsPartFile(string fullPath)
        {
            string name = Path.GetFileName(fullPath);
            if (name.Length == 0) return false;

            if (name.EndsWith(".partmeta", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith(".qmeta", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Equals("meta.json", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.IndexOf(".part", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool SafeIsDir(string path)
        {
            try { return Directory.Exists(path); }
            catch { return false; }
        }

        private static IEnumerable<string> SafeEnumerateDirs(string root)
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch { yield break; }
            foreach (string d in dirs) yield return d;
        }
    }
}
