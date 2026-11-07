using System;
using System.Collections.Generic;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 按**物理磁盘**聚合连接预算的纯计算部分（无 I/O、无状态、可自检）。
    ///
    /// <para>
    /// 🚨🚨 2026-11-07 新增的原因：原来 <c>DownloadScheduler</c> 里那句
    /// <c>if (DiskMedia.DetectForPath(job.Dest) == DiskMediaKind.Hdd &amp;&amp; want &gt; HddMaxConcurrency)</c>
    /// 是**逐任务**判定的 —— 每个任务各自看到「目标在机械盘」就把自己夹到 128 路，
    /// 谁也不知道别人也在同一块盘上。
    /// </para>
    /// <para>
    /// 于是「同时下载 3 个文件到同一块机械盘」= 3 × 128 = 384 路随机写同一颗磁头，
    /// 比单任务 512 路还糟：单任务至少写合并窗口是同一份（按偏移排序后批量刷盘），
    /// 而三个任务各有各的窗口、各按各的偏移排序，磁头要在三套偏移之间来回跳。
    /// 机械盘的随机写是**寻道物理限制**（约 10 ms/次），不是带宽限制，
    /// 加连接只会更慢。
    /// </para>
    ///
    /// <para>
    /// 所以配额必须**按盘符聚合**：同一块盘上同时在跑 N 个任务时，
    /// 每个任务的软上限 = min(盘配额 ÷ N × 2, 盘配额)。
    /// 取 2 倍平均份额的理由与网盘层一致：允许单个大文件多拿，
    /// 但又不至于把整块盘占光（否则小文件会饿死）。
    /// </para>
    /// </summary>
    public static class DiskConnBudget
    {
        /// <summary>机械盘默认并发上限（与 <c>DownloadScheduler.HddMaxConcurrency</c> 默认值一致）。</summary>
        public const int DefaultHddCap = 128;

        /// <summary>盘符识别失败时的聚合键（所有识别不出的路径共用一个桶，宁可保守）。</summary>
        public const string UnknownDrive = "?";

        /// <summary>
        /// 从目标路径取出**磁盘聚合键**。
        ///
        /// <para>
        /// 机械盘的寻道代价是按**物理设备**计的不是按挂载点计的，
        /// 但本程序拿不到物理设备号（要多引 Win32 API），
        /// 所以退一步按盘符聚合 —— 对「C/D/E 各一块盘」的常见情况完全正确，
        /// 对「一块盘分多个区」的情况偏保守（各区分开算，总并发可能偏高）。
        /// 偏保守的方向是对的：宁可少开一点，不要把自己拖死。
        /// </para>
        /// </summary>
        /// <param name="path">目标文件路径（可以为目录）。</param>
        /// <returns>大写盘符（如 <c>"C:"</c>）；UNC 路径返回 <c>"\\server\share"</c> 形式；失败返回 <see cref="UnknownDrive"/>。</returns>
        public static string DriveKey(string path)
        {
            if (string.IsNullOrEmpty(path))
                return UnknownDrive;

            try
            {
                string root = System.IO.Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root))
                    return UnknownDrive;

                // UNC：\\server\share\... → 取 \\server\share 作为聚合键
                if (root.StartsWith("\\\\", StringComparison.Ordinal))
                {
                    string[] parts = root.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                        return "\\\\" + parts[0] + "\\" + parts[1];
                    return root.TrimEnd('\\');
                }

                // 本地盘：C:\ → "C:"
                string key = root.TrimEnd('\\', '/');
                if (key.Length == 0)
                    return UnknownDrive;
                return key.ToUpperInvariant();
            }
            catch
            {
                return UnknownDrive;
            }
        }

        /// <summary>
        /// 同一块盘上 N 个任务时，单个任务的软上限。
        ///
        /// <para>
        /// 🚨 机械盘上「并发越低越快」，所以这里**只会往下夹、不会往上抬**：
        /// 返回 min(share × 2, diskCap)，其中 share = diskCap ÷ tasks（至少 1）。
        /// </para>
        /// <list type="bullet">
        /// <item>1 个任务、盘配额 128 → 128（与逐任务判定时完全一致，行为等价）</item>
        /// <item>2 个任务、盘配额 128 → 128（各 64，×2 后仍 128）</item>
        /// <item>3 个任务、盘配额 128 → 85</item>
        /// <item>6 个任务、盘配额 128 → 42</item>
        /// </list>
        /// <para>注意：三个任务同时拿 85 = 255 路仍略高于 128 —— 这是「软上限」的固有代价，
        /// 换成硬租约需要动态建信号量、且会让后来的任务永远轮不到（历史上已经踩过这个坑）。
        /// 真正的保护是 2 倍系数把峰值压在盘配额的 2 倍以内，而不是无上限。</para>
        /// </summary>
        /// <param name="diskCap">该盘的总配额（HDD 时 = 用户设的机械盘连接上限；≤0 表示不限制）。</param>
        /// <param name="tasksOnDisk">该盘上同时在跑的任务数（≤0 按 1 处理）。</param>
        /// <returns>单任务软上限；<paramref name="diskCap"/> ≤ 0 时返回 0 表示不限制。</returns>
        public static int SoftCap(int diskCap, int tasksOnDisk)
        {
            if (diskCap <= 0)
                return 0;

            int n = tasksOnDisk < 1 ? 1 : tasksOnDisk;
            int share = diskCap / n;
            if (share < 1) share = 1;

            long soft = (long)share * 2;
            if (soft > diskCap) soft = diskCap;
            return (int)soft;
        }

        /// <summary>
        /// 应用磁盘软上限。<paramref name="softCap"/> ≤ 0 表示不限制，原值返回。
        /// </summary>
        /// <param name="want">当前想要开的并发数。</param>
        /// <param name="softCap">磁盘层算出的软上限（0 = 不限）。</param>
        /// <returns>夹取后的并发数。</returns>
        public static int Apply(int want, int softCap)
        {
            if (want < 1) return want;
            if (softCap <= 0) return want;
            return want > softCap ? softCap : want;
        }

        /// <summary>
        /// 从「任务目标路径 + 是否机械盘」算出该任务所属的磁盘配额。
        /// 非机械盘返回 0（不限制）——固态盘随机写没有寻道代价，多任务反而更快。
        /// </summary>
        /// <param name="isHdd">目标盘是否为机械盘。</param>
        /// <param name="hddCap">用户设置的机械盘连接上限（≤0 时用 <see cref="DefaultHddCap"/>）。</param>
        public static int DiskCapFor(bool isHdd, int hddCap)
        {
            if (!isHdd) return 0;
            return hddCap > 0 ? hddCap : DefaultHddCap;
        }

        /// <summary>
        /// 把「路径 → 任务数」的统计表按盘符累加。纯函数版，便于自检。
        ///
        /// <para>
        /// 入参是「每个任务的 (盘符键)」，返回每个盘符上跑了几个任务。
        /// 键比较用 <see cref="StringComparer.OrdinalIgnoreCase"/>（盘符大小写不敏感）。
        /// </para>
        /// </summary>
        public static Dictionary<string, int> CountByDrive(IEnumerable<string> driveKeys)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (driveKeys == null)
                return result;

            foreach (string raw in driveKeys)
            {
                string key = string.IsNullOrEmpty(raw) ? UnknownDrive : raw;
                int n;
                result.TryGetValue(key, out n);
                result[key] = n + 1;
            }
            return result;
        }
    }
}
