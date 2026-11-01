using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GeZi.DownloadEngine;

/// <summary>
/// 目标文件周边「附属文件」（分片目录、续传元数据、待删除标记、续传快照）的
/// 路径推导与清理。
///
/// <para>
/// **只有这一份推导**：下载器与界面层（「删除本地文件」「丢弃未完成任务」）
/// 必须共用它，否则两处各写一遍，改了 partsRoot 就会漂移 —— 那正是"分片删不掉"的成因。
/// </para>
/// <para>
/// 本类原先挂在 <c>SegmentedDownloader</c>（Legacy 自研引擎）的 partial 文件上。
/// 自研引擎已整体移除，但这些推导与 UI 的契约不变，故原样搬到独立静态类，
/// 行为、路径格式、后缀字符串**一个字都没改** —— 旧版本留下的分片目录仍能被认出并清理。
/// </para>
/// </summary>
public static class PartsPaths
{
    /// <summary>待删除标记后缀。取消/失败时目标文件先改名为它，再尝试删除。</summary>
    public const string PendingDeleteSuffix = ".gezi-deleting";

    /// <summary>默认分片目录：与目标文件同级，便于整体清理。</summary>
    private static string DefaultPartsDir(string dest) => dest + ".qparts";

    /// <summary>
    /// 解析某个目标文件对应的「分片目录」与「续传元数据」路径。
    /// 默认（无 partsRoot）：`{dest}.qparts` / `{dest}.qmeta` —— 与目标同级。
    /// 指定 partsRoot：`{root}\{slug}_{fnv8}` 下统一放 `parts/` 与 `meta.json`，
    ///   避免下载目录被一堆 `.qparts` 污染，也支持分片跨盘存放。
    /// hash 用目标**绝对路径**算（FNV-1a），保证同一文件落到同一目录，
    /// 从而续传能认出旧分片；不同目录下的同名文件也不会互相串。
    /// </summary>
    /// <param name="dest">目标文件完整路径。</param>
    /// <param name="partsRoot">分片根目录；null/空白表示与目标同级。**必须与下载时传入的值一致**。</param>
    public static void ResolvePaths(string dest, string partsRoot, out string partsDir, out string metaPath)
    {
        if (string.IsNullOrWhiteSpace(partsRoot))
        {
            partsDir = DefaultPartsDir(dest);
            metaPath = dest + ".qmeta";
            return;
        }

        string full;
        try { full = Path.GetFullPath(dest); }
        catch { full = dest; }

        string slug = SafeName(Path.GetFileName(dest));
        string key = slug + "_" + Fnv1a(full).ToString("x8");
        string baseDir = Path.Combine(partsRoot.Trim(), key);
        partsDir = Path.Combine(baseDir, "parts");
        metaPath = Path.Combine(baseDir, "meta.json");
    }

    /// <summary>
    /// 计算"删除本地文件"时需要一并清理的分片目录与续传元数据路径。
    /// 这是给界面层用的<b>只读推导</b>：除了当前 <paramref name="partsRoot"/> 对应的位置，
    /// 还会带上历史遗留的默认位置（`{dest}.qparts` / `{dest}.qmeta`）。
    /// 原因：用户可能**中途改过**分片根目录 —— 之前下载留下的分片在旧位置，
    /// 只按当前位置删就会漏掉，表现为"分片根本没删掉"。
    /// 返回的集合已按路径去重，可直接逐个尝试删除（不存在则跳过）。
    /// </summary>
    public static IEnumerable<string> EnumeratePartialPaths(string dest, string partsRoot)
        => EnumeratePartialPaths(dest, new[] { partsRoot });

    /// <summary>
    /// 同上，但支持传入**多个**分片根目录（当前生效 + 历史用过的）。
    /// 用户多次改过设置时，残留分片可能散落在多个旧根目录下，必须逐个覆盖。
    /// </summary>
    public static IEnumerable<string> EnumeratePartialPaths(string dest, IEnumerable<string> partsRoots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) 每个（当前 + 历史的）根目录对应的位置
        var roots = new List<string>();
        if (partsRoots != null)
            foreach (var r in partsRoots)
                if (!string.IsNullOrWhiteSpace(r)) roots.Add(r.Trim());
        if (roots.Count == 0) roots.Add(null);   // 无自定义根 → 只有默认位置

        foreach (var root in roots)
        {
            ResolvePaths(dest, root, out string partsDir, out string metaPath);
            if (seen.Add(partsDir)) yield return partsDir;
            if (seen.Add(metaPath)) yield return metaPath;
            if (seen.Add(metaPath + ".bak")) yield return metaPath + ".bak";

            // 指定了 partsRoot 时，hash 目录（分片目录的父目录）也是下载器建的，
            // 删完 parts/meta.json 后它就成了空壳，一并回收，避免堆积。
            if (!string.IsNullOrWhiteSpace(root))
            {
                string baseDir = Path.GetDirectoryName(partsDir);
                if (!string.IsNullOrEmpty(baseDir) && seen.Add(baseDir))
                    yield return baseDir;
            }
        }

        // 2) 历史默认位置：即便现在改用了 partsRoot，旧分片也可能留在这里
        string legacyParts = DefaultPartsDir(dest);
        string legacyMeta = dest + ".qmeta";
        if (seen.Add(legacyParts)) yield return legacyParts;
        if (seen.Add(legacyMeta)) yield return legacyMeta;
        if (seen.Add(legacyMeta + ".bak")) yield return legacyMeta + ".bak";

        // 3) 上次删除失败留下的“待删除”文件也属于残留，一起回收
        if (seen.Add(dest + PendingDeleteSuffix)) yield return dest + PendingDeleteSuffix;
        if (seen.Add(legacyMeta + PendingDeleteSuffix)) yield return legacyMeta + PendingDeleteSuffix;

        // 4) 🚨 2026-10-28 补：融合引擎的续传快照也属于「这个文件的残留」。
        // 原来这里没有它，于是「取消下载」和「丢弃未完成任务」两条路径都不删
        // {dest}.geziresume —— 留下一个 3KB 状态文件，而且危害不止是垃圾：
        // 用户重下同一个文件时，这份旧快照会被命中，但目标文件已经被删了，
        // 顺序落盘层打开一个空文件、_flushed=0，没有任何 chunk 从 0 写
        // ⇒ 写窗口无限等待，表现为「一启动就卡住」。
        if (seen.Add(dest + ".geziresume")) yield return dest + ".geziresume";
        if (seen.Add(dest + ".geziresume" + PendingDeleteSuffix)) yield return dest + ".geziresume" + PendingDeleteSuffix;
    }

    /// <summary>
    /// 清理上次运行留下的 <c>*.gezi-deleting</c> 残留。下载中删除失败的文件会留成这个后缀，
    /// 下次启动时它们已不可能被占用，直接删掉即可。返回删除的文件数。
    /// <para>
    /// 🚨 2026-10-28 修：原来 <c>try</c> 只包住了 <c>EnumerateFiles</c> 的**赋值**，
    /// 而它是惰性枚举 —— 真正的目录遍历发生在 <c>foreach</c> 里，那一步在 try 之外。
    /// 于是根目录下只要有一个不可读的子目录（权限不足 / 目录被删 / junction 断裂），
    /// 异常就会从 foreach 抛出，穿透到 <c>StartPartsSweep</c> 的整体 catch，
    /// 结果是**这一整轮所有 <c>*.gezi-deleting</c> 一个都不清理**，越积越多。
    /// 修法：先把枚举结果物化进 List（在 try 内完成遍历），再遍历 List 删除。
    /// </para>
    /// </summary>
    public static int SweepPendingDeletes(IEnumerable<string> roots)
    {
        int removed = 0;
        if (roots == null)
            return 0;
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            // 在 try 内完成真实遍历（物化），单个不可读子目录只损失它自己。
            var files = new List<string>();
            try
            {
                foreach (string f in Directory.EnumerateFiles(root, "*" + PendingDeleteSuffix,
                             SearchOption.AllDirectories))
                {
                    files.Add(f);
                }
            }
            catch
            {
                // 遍历中途抛（某个子目录不可读）：已经收集到的照常处理，不整轮放弃。
            }

            foreach (string f in files)
            {
                try { File.Delete(f); removed++; }
                catch { }
            }
        }
        return removed;
    }

    /// <summary>把文件名里对路径非法的字符替换掉（分片根目录下要建同名子目录）。</summary>
    private static string SafeName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "file";
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        string s = sb.ToString();
        // 目录名过长会占满 MAX_PATH 预算，截断到 40 字符足够区分。
        return s.Length > 40 ? s.Substring(0, 40) : s;
    }

    /// <summary>FNV-1a 32 位哈希。用于把绝对路径压成一个稳定的短 key（非加密用途）。</summary>
    private static uint Fnv1a(string s)
    {
        uint h = 2166136261;
        foreach (char c in s)
        {
            h ^= c;
            h *= 16777619;
        }
        return h;
    }
}
