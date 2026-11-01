using System;

namespace GeZi.DownloadEngine.Framework;

/// <summary>
///     Provides any information about progress, like progress percentage, speed,
///     total received bytes and received bytes array to live streaming, for the DownloadService.DownloadProgressChanged event of a
///     DownloadService. 
/// </summary>
public class DownloadProgressChangedEventArgs : EventArgs
{
    public DownloadProgressChangedEventArgs(string id)
    {
        ProgressId = id ?? "Main";
    }

    /// <summary>
    ///     Progress unique identity
    /// </summary>
    public string ProgressId { get; }

    /// <summary>
    ///     Gets the asynchronous task progress percentage.
    /// </summary>
    /// <returns>A percentage value indicating the asynchronous task progress.</returns>
    public double ProgressPercentage => TotalBytesToReceive == 0 ? 0 : ((double)ReceivedBytesSize * 100) / TotalBytesToReceive;

    /// <summary>
    ///     Gets the number of received bytes.
    /// </summary>
    /// <returns>An System.Int64 value that indicates the number of received bytes.</returns>
    public long ReceivedBytesSize { get; internal set; }

    /// <summary>
    ///     Gets the total number of bytes in a System.Net.WebClient data download operation.
    /// </summary>
    /// <returns>An System.Int64 value that indicates the number of bytes that will be received.</returns>
    public long TotalBytesToReceive { get; internal set; }

    /// <summary>
    ///     How many bytes downloaded per second
    /// </summary>
    public double BytesPerSecondSpeed { get; internal set; }

    /// <summary>
    ///     Average download speed
    /// </summary>
    public double AverageBytesPerSecondSpeed { get; internal set; }

    /// <summary>
    ///     How many bytes progressed per this time
    /// </summary>
    public long ProgressedByteSize { get; internal set; }

    /// <summary>
    ///     Gets the received bytes.
    /// </summary>
    /// <returns>A byte array that indicates the received bytes.</returns>
    public byte[] ReceivedBytes { get; internal set; }
    
    /// <summary>
    ///     Gets the number of chunks being downloaded currently.
    /// </summary>
    public int ActiveChunks { get; internal set; }

    /// <summary>
    ///     当前阶段说明（收尾/校验等），null 表示正常下载阶段。
    ///
    /// **为什么需要**：分片全部下完之后，还要做 <c>FlushAsync</c> 与内容校验
    /// （几百 MB 的 SHA-256 要数秒）。这段时间**没有任何网络流量**，
    /// 但状态仍是 Downloading、速度停在最后一帧、连接数停在旧快照 ——
    /// 用户看到的就是「100% 了还在跑、还显示速度」，以为卡死。
    /// 带上 Phase 后 UI 可以直接显示「正在校验文件完整性…」。
    /// </summary>
    public string Phase { get; internal set; }
}