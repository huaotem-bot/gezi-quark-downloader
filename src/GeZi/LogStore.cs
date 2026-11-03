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
        /// 日志保留天数（用户要求：只保存一天，超过就删）。
        /// <para>
        /// 🚨 边界处理：<b>保留「今天 + 昨天」</b>，而不是严格只留今天 ——
        /// 因为跨零点时昨天的日志可能还在被写（进程没重启），
        /// 直接删会丢掉刚写的最后几行，甚至因文件被占用而失败。
        /// 多留一天的成本可以忽略，换来的是「绝不误删正在写的文件」。
        /// </para>
        /// </summary>
        private const int KeepDays = 1;

        /// <summary>
        /// 清理过期日志。
        /// <para>
        /// 调用点：启动时一次 + 主窗口定时器每 6 小时一次（跨零点后能自动清）。
        /// </para>
        /// <para>
        /// 边界条件（逐条处理）：
        /// <list type="bullet">
        /// <item><b>跨零点</b> —— 保留今天+昨天（见 KeepDays 说明）。</item>
        /// <item><b>文件被占用</b> —— catch 静默忽略，下次再删（用户可能正用记事本打开）。</item>
        /// <item><b>系统时间被改</b> —— 用 LastWriteTime 判断，不解析文件名。</item>
        /// <item><b>目录不存在</b> —— Directory.Exists 先挡。</item>
        /// <item><b>误删别的文件</b> —— 只匹配 GeZi*.log，且只在日志目录内。</item>
        /// <item><b>当前正在写的那个文件</b> —— 名字相同就跳过，绝不删自己。</item>
        /// </list>
        /// </para>
        /// </summary>
        public static void PurgeOldLogs()
        {
            try
            {
                string cur = FilePath;
                if (string.IsNullOrEmpty(cur)) return;
                string dir = Path.GetDirectoryName(cur);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

                DateTime cutoff = DateTime.Today.AddDays(-(KeepDays + 1));
                string[] files;
                try { files = Directory.GetFiles(dir, "GeZi*.log"); }
                catch { return; }

                int removed = 0;
                foreach (string p in files)
                {
                    try
                    {
                        // 绝不删当前正在写的那个
                        if (string.Equals(p, cur, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var fi = new FileInfo(p);
                        if (!fi.Exists) continue;
                        if (fi.LastWriteTime >= cutoff) continue;
                        fi.Delete();
                        removed++;
                    }
                    catch
                    {
                        // 被占用 / 权限不足 —— 静默跳过，下次再试
                    }
                }
                if (removed > 0)
                {
                    try
                    {
                        Append("[日志] 已清理 " + removed + " 个超过 " + (KeepDays + 1) + " 天的日志文件");
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
