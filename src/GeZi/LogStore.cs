using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GeZi
{
    /// <summary>
    /// 运行日志的**落盘**部分。
    ///
    /// <para>
    /// 2026-10-26 用户反馈：「日志与下载记录持久化，退出重进不丢」。
    /// 下载记录与解析历史本来就存在 <c>GeZi.state.json</c> 里（见 <see cref="HistoryStore"/>），
    /// 唯独「运行日志」是纯内存的 —— 关掉软件就全没了，出问题想回看只能趁软件还开着。
    /// </para>
    ///
    /// <para><b>为什么不塞进 state.json</b></para>
    /// <list type="bullet">
    /// <item>state.json 是**整体读写 + DPAPI 加解密**，每次保存都要重写全文件；
    ///       日志是高频追加（下载时每秒几十行），塞进去会让每次保存都变慢。</item>
    /// <item>日志是纯文本、无敏感字段，不需要加密；混进加密文件反而更容易被误删。</item>
    /// </list>
    ///
    /// <para><b>文件位置</b>：与 <c>GeZi.config.xml</c> 同目录的 <c>GeZi.log</c>。</para>
    ///
    /// <para><b>大小控制</b>：超过 <see cref="MaxBytes"/> 就整体截断，只留后半段
    /// （重命名 + 重写，不用滚动多份文件 —— 用户要的是"上次关掉前的日志还在"，
    /// 不需要历史归档）。</para>
    ///
    /// <para><b>线程安全</b>：<see cref="Append"/> 由下载线程直接调用，内部加锁。</para>
    /// </summary>
    internal static class LogStore
    {
        /// <summary>日志文件上限 4MB（约 3~4 万行），够翻很久，又不会无限长。</summary>
        private const long MaxBytes = 4L * 1024 * 1024;

        /// <summary>启动时最多恢复的行数 —— 与界面上限一致，多了也是白读。</summary>
        public const int MaxRestoreLines = 2000;

        private static readonly object Gate = new object();

        private static string _path;
        private static bool _pathResolved;

        /// <summary>日志文件路径：优先与 config 同目录，取不到就退回 LocalAppData。</summary>
        public static string FilePath
        {
            get
            {
                if (_pathResolved) return _path;
                lock (Gate)
                {
                    if (_pathResolved) return _path;
                    try
                    {
                        string dir = Path.GetDirectoryName(SettingsStore.FilePath);
                        _path = Path.Combine(dir, "GeZi.log");
                    }
                    catch
                    {
                        try { _path = Path.Combine(Util.GetLocalAppDataDir(), "GeZi.log"); }
                        catch { _path = null; }
                    }
                    _pathResolved = true;
                    return _path;
                }
            }
        }

        /// <summary>
        /// 追加一行（不含换行，内部补）。失败**静默忽略** —— 写日志不能影响下载。
        /// </summary>
        public static void Append(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            string path = FilePath;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                lock (Gate)
                {
                    RollIfTooBig(path);
                    // FileShare.ReadWrite：诊断包导出/用户用记事本打开时也能继续写
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write,
                                                   FileShare.ReadWrite))
                    using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                    {
                        w.Write(line);
                        w.Write(Environment.NewLine);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 日志保留时长（小时）—— 用户 2026-11-07 拍板：<b>只保存 24 小时</b>。
        /// <para>
        /// 最终行为一句话：<b>按天切分，磁盘上最多同时存在两份，单份封顶
        /// <see cref="MaxBytes"/>，重启最多回填 <see cref="MaxRestoreLines"/> 行，
        /// 且任何一份超过 24 小时就删</b>。
        /// </para>
        /// <para>
        /// 🚨 判据是**滚动 24 小时**（<c>DateTime.Now.AddHours(-KeepHours)</c>），
        /// 不是「天」也不是「0 点」—— 两者差别很大：
        /// <list type="bullet">
        /// <item>按日历天算，今天 00:05 写的日志到今晚 23:59 仍算「今天」，
        ///       实际能活到将近 48 小时；</item>
        /// <item>按 24 小时算，它在明天 00:05 之后一定被删，精确符合用户要的
        ///       「只能保存 24 小时」。</item>
        /// </list>
        /// </para>
        /// <para>
        /// ⚠️ 历史坑（改回前务必读）：这里曾经是 <c>KeepDays = 1</c> 配
        /// <c>Today.AddDays(-(KeepDays + 1))</c>，即截断线落在**前天零点**，
        /// 于是磁盘上实际堆着「今天 + 昨天 + 前天」三份 —— 与「最多两份」不符的
        /// off-by-one，存在过好几轮。现在换成小时制 + <see cref="KeepFiles"/> 份数配额，
        /// 两条判据互相兜底，不会再靠 `+1` 这种心算。
        /// </para>
        /// </summary>
        private const int KeepHours = 24;

        /// <summary>
        /// 磁盘上最多保留的日志文件份数（当前正在写的 + 归档）。
        /// <para>
        /// 为什么要有这个「硬配额」：<see cref="KeepHours"/> 管的是「老没老」，
        /// 但同一天里完全可能堆出多份（重装、手工拷进旧日志、系统时间被改、
        /// 或者跨零点归档时同名合并失败）—— 光看时间戳删不掉它们。
        /// 配额按 <c>LastWriteTime</c> 倒序只留最新 <see cref="KeepFiles"/> 份，
        /// 当前正在写的那份永远豁免。
        /// </para>
        /// </summary>
        private const int KeepFiles = 2;

        /// <summary>
        /// 🚨 2026-11-07 落实「日志只保留一天」：跨零点归档当前文件。
        /// <para>
        /// 原来的 <see cref="PurgeOldLogs"/> 只删「别的」 GeZi*.log，且**明确跳过当前
        /// 正在写的 GeZi.log** —— 而所有日志都往这一个文件里追加，文件名永远不变，
        /// 于是它永远不会超期、永远不会被删：实测磁盘上 1.88MB / 13010+ 行，
        /// 用户看到的「只保存一天」其实从没生效过。
        /// </para>
        /// <para>
        /// 正解：**按天切文件**。启动时若发现 GeZi.log 最后写入不是今天，
        /// 就把它改名成 GeZi-yyyyMMdd.log 归档，之后 Append 会自动新建今天的 GeZi.log。
        /// 归档文件由 PurgeOldLogs 按 <see cref="KeepHours"/> 滚动窗口 + <see cref="KeepFiles"/> 份数配额清理。
        /// 这样当前文件永远只有当天日志，恢复时 LoadTail 也不会把几千行旧内容灌回来。
        /// </para>
        /// </summary>
        public static void ArchiveIfCrossedDay()
        {
            try
            {
                string cur = FilePath;
                if (string.IsNullOrEmpty(cur) || !File.Exists(cur)) return;
                lock (Gate)
                {
                    var fi = new FileInfo(cur);
                    if (!fi.Exists || fi.Length == 0) return;
                    if (fi.LastWriteTime.Date >= DateTime.Today) return;   // 今天写的，不动
                    string stamp = fi.LastWriteTime.ToString("yyyyMMdd");
                    string dir = Path.GetDirectoryName(cur);
                    string archive = Path.Combine(dir, "GeZi-" + stamp + ".log");
                    try
                    {
                        if (File.Exists(archive))
                        {
                            // 同名归档已存在（例如同一天里重装过）：内容并进去，不覆盖丢日志
                            File.AppendAllText(archive, Environment.NewLine + File.ReadAllText(cur));
                            File.Delete(cur);
                        }
                        else
                        {
                            File.Move(cur, archive);
                        }
                    }
                    catch
                    {
                        // 文件被记事本/诊断包占用 —— 静默跳过，下次启动再归档。
                        // 绝不因归档失败影响启动，更不删用户正在看的文件。
                    }
                }
            }
            catch { }
        }


        /// <summary>
        /// 清理过期日志 —— 两条判据**同时**生效，先删超龄的，再按份数配额兜底。
        /// <para>
        /// 调用点：启动时一次 + 主窗口定时器每 6 小时一次（跨零点后能自动清）。
        /// ⚠️ 启动时必须**先** <see cref="ArchiveIfCrossedDay"/> 再调本方法，
        ///    否则旧内容还压在 GeZi.log 里、而本方法又跳过它，等于白跑。
        /// </para>
        /// <para>
        /// 判据一（时间）：<c>LastWriteTime &lt; Now - KeepHours</c> ⇒ 删。
        /// 判据二（份数）：按 <c>LastWriteTime</c> 倒序，排在 <see cref="KeepFiles"/> 之后的全删。
        /// </para>
        /// <para>
        /// 边界条件（逐条处理）：
        /// <list type="bullet">
        /// <item><b>当前正在写的那个文件</b> —— 名字相同就**永远**跳过，
        ///       连份数配额也不能删它（否则正在写的日志会当场消失）。</item>
        /// <item><b>文件被占用</b> —— catch 静默忽略，下次再删（用户可能正用记事本打开）。</item>
        /// <item><b>系统时间被改</b> —— 用 LastWriteTime 判断，不解析文件名；
        ///       时间被改到未来也只会「留着」，不会误删。</item>
        /// <item><b>目录不存在</b> —— Directory.Exists 先挡。</item>
        /// <item><b>误删别的文件</b> —— 只匹配 GeZi*.log，且只在日志目录内。</item>
        /// </list>
        /// </para>
        /// </summary>
        public static void PurgeOldLogs()
        {
            // 🚨 2026-11-07：先按天归档当前 GeZi.log（否则它永远跳过、永远不超期），
            //    再按时间 + 份数删旧归档。放在最前面，两个调用点（启动 / 6h 定时器）
            //    都自动获得跨零点切分能力，不用各自再改。
            ArchiveIfCrossedDay();
            try
            {
                string cur = FilePath;
                if (string.IsNullOrEmpty(cur)) return;
                string dir = Path.GetDirectoryName(cur);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

                // 判据一：滚动窗口（不是日历天，见 KeepHours 说明）
                DateTime cutoff = DateTime.Now.AddHours(-KeepHours);
                string[] files;
                try { files = Directory.GetFiles(dir, "GeZi*.log"); }
                catch { return; }

                int removed = 0;
                var survivors = new List<FileInfo>();

                // 第一轮：删超龄的，同时收集活下来的（配额要用）
                foreach (string p in files)
                {
                    try
                    {
                        // 绝不删当前正在写的那个
                        if (string.Equals(p, cur, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var fi = new FileInfo(p);
                        if (!fi.Exists) continue;
                        if (fi.LastWriteTime < cutoff)
                        {
                            fi.Delete();
                            removed++;
                            continue;
                        }
                        survivors.Add(fi);
                    }
                    catch
                    {
                        // 被占用 / 权限不足 —— 静默跳过，下次再试
                    }
                }

                // 判据二：时间判据兜不住的（同一天堆多份、重装、时间被改），
                //        按份数配额掐掉最旧的，给当前文件留出一个名额。
                try
                {
                    int keepArchived = KeepFiles - 1;   // 当前 GeZi.log 占 1 份
                    if (keepArchived < 0) keepArchived = 0;
                    if (survivors.Count > keepArchived)
                    {
                        survivors.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
                        for (int i = keepArchived; i < survivors.Count; i++)
                        {
                            try
                            {
                                survivors[i].Delete();
                                removed++;
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                if (removed > 0)
                {
                    try
                    {
                        Append("[日志] 已清理 " + removed + " 个过期日志文件（保留最近 "
                               + KeepHours + " 小时、最多 " + KeepFiles + " 份）");
                    }
                    catch { }
                }
            }
            catch { }
        }


        /// <summary>超过上限就把文件读后半段重写（近似"截掉最早的一半"）。</summary>
        private static void RollIfTooBig(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length <= MaxBytes) return;
                long keep = MaxBytes / 2;
                long start = fi.Length - keep;
                byte[] buf;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Seek(start, SeekOrigin.Begin);
                    buf = new byte[keep];
                    int read = 0, n;
                    while (read < buf.Length && (n = fs.Read(buf, read, buf.Length - read)) > 0)
                        read += n;
                    if (read < buf.Length) Array.Resize(ref buf, read);
                }
                // 从下一个换行处开始，避免第一行是半截
                int skip = 0;
                while (skip < buf.Length && buf[skip] != (byte)'\n') skip++;
                if (skip < buf.Length) skip++;
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                {
                    fs.Write(buf, skip, buf.Length - skip);
                }
            }
            catch { }
        }

        /// <summary>
        /// 读文件尾部最多 <paramref name="maxLines"/> 行（启动时灌回界面用）。
        /// 从文件末尾往前按块读，不整个读进内存。
        /// </summary>
        public static List<string> LoadTail(int maxLines)
        {
            var lines = new List<string>();
            string path = FilePath;
            if (string.IsNullOrEmpty(path)) return lines;
            try
            {
                if (!File.Exists(path)) return lines;
                if (maxLines <= 0) maxLines = MaxRestoreLines;

                // 最多只倒读 2MB，避免文件很大时启动变慢
                const int maxScan = 2 * 1024 * 1024;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long len = fs.Length;
                    long start = Math.Max(0, len - maxScan);
                    fs.Seek(start, SeekOrigin.Begin);
                    byte[] buf = new byte[len - start];
                    int read = 0, n;
                    while (read < buf.Length && (n = fs.Read(buf, read, buf.Length - read)) > 0)
                        read += n;

                    string text = Encoding.UTF8.GetString(buf, 0, read);
                    // 若从中间切进来，第一行是半截 → 丢掉
                    if (start > 0)
                    {
                        int nl = text.IndexOf('\n');
                        text = nl >= 0 ? text.Substring(nl + 1) : string.Empty;
                    }
                    var all = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    int from = Math.Max(0, all.Length - maxLines);
                    for (int i = from; i < all.Length; i++)
                    {
                        if (all[i].Length > 0) lines.Add(all[i]);
                    }
                }
            }
            catch { }
            return lines;
        }

        /// <summary>清空日志文件（界面上「清空日志」用；目前没有入口，留给后续）。</summary>
        public static void Clear()
        {
            string path = FilePath;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                lock (Gate)
                {
                    if (File.Exists(path)) File.Delete(path);
                }
            }
            catch { }
        }
    }
}
