// GeZi 鸽子下载器 — 多网盘下载器
// Copyright (C) 2026  GeZi contributors
//
// 本程序是自由软件：你可以再分发和/或修改它，依据 GNU Affero 通用公共许可证
// 第 3 版（或更新版本）的条款，由自由软件基金会发布。
// 本程序基于「希望它有用」的目的分发，但不提供任何担保。
// 详见 <https://www.gnu.org/licenses/>。

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GeZi.DownloadEngine.Storage;

/// <summary>物理磁盘介质类型。</summary>
public enum DiskMediaKind
{
    /// <summary>探测失败或无法判断。</summary>
    Unknown = 0,

    /// <summary>机械硬盘（寻道有代价）。</summary>
    Hdd = 1,

    /// <summary>固态硬盘（无寻道代价）。</summary>
    Ssd = 2,
}

/// <summary>
/// 探测目标磁盘是机械盘还是固态盘，用来给下载任务自动挑选落盘策略。
///
/// <para><b>为什么需要这个</b>：随机写（多连接各写最终位置）在固态盘上是免费的速度，
/// 在机械盘上却是灾难——256 条连接各写一个偏移 = 每秒上千次磁头寻道。
/// 顺序写对两种盘都友好，但有 32 MB 重排窗口的内存代价。所以让程序自己判断，
/// 用户不需要懂这些。</para>
///
/// <para><b>实现选择</b>：只用 <c>IOCTL_STORAGE_QUERY_PROPERTY</c> +
/// <c>StorageDeviceSeekPenaltyProperty</c>，零额外依赖。
/// ⚠️ 打开卷设备必须用 <c>FILE_READ_ATTRIBUTES(0x80)</c>，
/// 用 <c>GENERIC_READ</c> 会拿到 err=5 拒绝访问（实测）。</para>
///
/// <para>探测失败一律返回 <see cref="DiskMediaKind.Unknown"/>，
/// 调用方应保守地退回顺序写。</para>
/// </summary>
public static class DiskMedia
{
    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;

    // 打开卷 / 物理盘只需读取属性权限，不要用 GENERIC_READ（会 err=5）。
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        ref StoragePropertyQuery lpInBuffer, int nInBufferSize,
        ref DeviceSeekPenaltyDescriptor lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>按盘符缓存，避免每次下载都查一遍设备。</summary>
    private static readonly ConcurrentDictionary<char, DiskMediaKind> Cache =
        new ConcurrentDictionary<char, DiskMediaKind>();

    /// <summary>每个盘符的探测结论是否已经打过日志（避免每个任务刷一遍）。</summary>
    private static readonly ConcurrentDictionary<char, bool> LoggedDrives =
        new ConcurrentDictionary<char, bool>();

    /// <summary>
    /// 探测指定文件路径所在的磁盘介质。
    /// 路径不存在也能探测（只看盘符）。UNC / 无盘符路径返回 <see cref="DiskMediaKind.Unknown"/>。
    /// </summary>
    public static DiskMediaKind DetectForPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return DiskMediaKind.Unknown;

        char drive;
        try
        {
            string root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
                return DiskMediaKind.Unknown;   // UNC \\server\share 之类，判断不了
            drive = char.ToUpperInvariant(root[0]);
            if (drive < 'A' || drive > 'Z') return DiskMediaKind.Unknown;
        }
        catch (ArgumentException)
        {
            return DiskMediaKind.Unknown;
        }

        return Cache.GetOrAdd(drive, ProbeDrive);
    }

    /// <summary>探测指定盘符。</summary>
    public static DiskMediaKind DetectForDrive(char drive)
    {
        char d = char.ToUpperInvariant(drive);
        if (d < 'A' || d > 'Z') return DiskMediaKind.Unknown;
        return Cache.GetOrAdd(d, ProbeDrive);
    }

    /// <summary>
    /// 给出该路径推荐的落盘策略。
    /// <para>🚨 2026-11-07：恒定返回 <see cref="StorageMode.Shared"/>。
    /// 机械盘友好不由「换落盘模型」实现，而由共写侧的大批量写合并
    /// （hdd 判定 → 512 包 / 64 MB 一批）+ 降低并发共同完成，这本来就是共写已有的能力。</para>
    /// </summary>
    public static StorageMode RecommendedModeForPath(string path)
    {
        return StorageMode.Shared;
    }

    /// <summary>清空缓存（切换目标盘或用户手动改设置时用）。</summary>
    public static void ClearCache()
    {
        Cache.Clear();
        LoggedDrives.Clear();
    }

    /// <summary>把介质类型翻译成给用户看的说明。</summary>
    public static string Describe(DiskMediaKind kind)
    {
        switch (kind)
        {
            case DiskMediaKind.Ssd: return "固态盘（无寻道代价）";
            case DiskMediaKind.Hdd: return "机械盘（有寻道代价）";
            default: return "未知介质（按机械盘处理，最保守）";
        }
    }

    /// <summary>
    /// 取路径所在盘符，并判断这个盘是不是第一次被探测（调用方据此决定要不要打日志，
    /// 避免同一块盘上每个任务都刷一条同样的提示）。
    /// </summary>
    public static bool TryConsumeFirstProbeForPath(string path, out char drive)
    {
        drive = '\0';
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            string root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':') return false;
            drive = char.ToUpperInvariant(root[0]);
        }
        catch (ArgumentException)
        {
            return false;
        }
        return LoggedDrives.TryAdd(drive, true);
    }

    private static DiskMediaKind ProbeDrive(char drive)
    {
        // 卷设备名：\\.\C:
        string volumePath = "\\\\.\\" + drive + ":";
        DiskMediaKind kind = QuerySeekPenalty(volumePath);
        if (kind != DiskMediaKind.Unknown) return kind;

        // 卷查不到就试物理盘（部分 U 盘 / 虚拟盘只认物理盘路径）。
        // 注意：盘符到物理盘的精确映射要走 WMI，这里只是「随便试一个能查通的」，
        // 因为绝大多数机器只有一块盘，误判风险可接受；查不到就算了。
        return DiskMediaKind.Unknown;
    }

    private static DiskMediaKind QuerySeekPenalty(string devicePath)
    {
        SafeFileHandle handle = CreateFileW(
            devicePath, FileReadAttributes, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return DiskMediaKind.Unknown;
        }

        try
        {
            var query = new StoragePropertyQuery
            {
                PropertyId = StorageDeviceSeekPenaltyProperty,
                QueryType = PropertyStandardQuery,
            };

            var result = new DeviceSeekPenaltyDescriptor();
            bool ok = DeviceIoControl(
                handle, IoctlStorageQueryProperty,
                ref query, Marshal.SizeOf(typeof(StoragePropertyQuery)),
                ref result, Marshal.SizeOf(typeof(DeviceSeekPenaltyDescriptor)),
                out _, IntPtr.Zero);

            if (!ok) return DiskMediaKind.Unknown;

            // IncursSeekPenalty = true 表示「访问不同位置有额外代价」= 机械盘。
            // 固态盘（含 NVMe、部分 RAID 虚拟盘）返回 false。
            return result.IncursSeekPenalty ? DiskMediaKind.Hdd : DiskMediaKind.Ssd;
        }
        catch (Exception)
        {
            return DiskMediaKind.Unknown;
        }
        finally
        {
            handle.Dispose();
        }
    }
}
