// GeZi 鸽子下载器 — 多网盘下载器
// Copyright (C) 2026  GeZi contributors
//
// 本程序是自由软件：你可以再分发和/或修改它，依据 GNU Affero 通用公共许可证
// 第 3 版（或更新版本）的条款，由自由软件基金会发布。
// 本程序基于「希望它有用」的目的分发，但不提供任何担保。
// 详见 <https://www.gnu.org/licenses/>。

using GeZi.DownloadEngine.Framework;
using GeZi.DownloadEngine.Framework.Extensions.Logging;
using System;
using System.IO;
using System.Threading.Tasks;

namespace GeZi.DownloadEngine.Storage;

/// <summary>
/// 共写落盘 —— 直接复用 bezzad 原生的 <see cref="ConcurrentStream"/>，行为与上游完全一致。
///
/// 适用于：固态硬盘、临时文件、不在意边下边看、追求最低内存占用的场景。
/// 机械硬盘上请改用 <see cref="SequentialFileStorage"/>（原因见 <see cref="IChunkStorage"/> 文档）。
/// </summary>
public sealed class SharedFileStorage : IChunkStorage
{
    private readonly ConcurrentStream _inner;
    private readonly object _gate = new object();
    private bool _disposed;

    /// <summary>内存流模式（<paramref name="filename"/> 为空时）。</summary>
    public SharedFileStorage(long maxMemoryBufferBytes = 0, ILogger logger = null)
    {
        _inner = new ConcurrentStream(maxMemoryBufferBytes, logger);
    }

    /// <summary>文件模式。</summary>
    /// <param name="filename">目标文件完整路径。</param>
    /// <param name="initSize">预分配尺寸（0 表示不预分配）。</param>
    /// <param name="batchMaxCount">写合并一批最多攒多少个 packet；0 = 默认 64。机械盘要放大。</param>
    /// <param name="batchMaxBytes">写合并一批最多攒多少字节；0 = 默认 16 MB。机械盘要放大。</param>
    public SharedFileStorage(string filename, long initSize, long maxMemoryBufferBytes = 0, ILogger logger = null,
        int batchMaxCount = 0, long batchMaxBytes = 0)
    {
        _inner = new ConcurrentStream(filename, initSize, maxMemoryBufferBytes, logger);
        if (batchMaxCount > 0) _inner.BatchMaxCount = batchMaxCount;
        if (batchMaxBytes > 0) _inner.BatchMaxBytes = batchMaxBytes;
    }

    public string Name => "shared";

    public bool CanWrite => !_disposed && _inner.CanWrite;

    public long Length => _inner.Length;

    public Task WriteAsync(long position, byte[] bytes, int length, bool fireAndForget = true)
        => _inner.WriteAsync(position, bytes, length, fireAndForget);

    public Task FlushAsync() => _inner.FlushAsync();

    /// <summary>
    /// 暂停/恢复写盘队列。共写模式下 worker 停了，Watcher 线程仍会继续把
    /// 队列里积压的数据（最多一个写合并窗口）写进磁盘 —— 用户看到的就是
    /// 「暂停了磁盘还在写」。这里把 Watcher 也一起刹住。
    /// 顺序写模式没有独立写线程，不受影响（worker 一停就没有写入）。
    /// </summary>
    public void PauseWrites() => _inner.PauseWrites();

    /// <summary>恢复写盘队列。</summary>
    public void ResumeWrites() => _inner.ResumeWrites();

    public Stream OpenRead() => _inner.OpenRead();

    /// <summary>
    /// 把文件撑到完整尺寸（预分配）。
    ///
    /// 2026-10-24 修正：原来这里是空实现，理由是「随机写能写到任意偏移」——
    /// 但那只解决「写得进去」，解决不了「播放器愿意打开」：
    /// 文件长度等于已写入的最大偏移时，播放器看到的是一截不完整文件，
    /// 读不到末尾 moov，边下边看必然失败。预分配后长度从第一秒就是全量。
    /// </summary>
    public void EnsureLength(long totalBytes)
    {
        if (totalBytes <= 0) return;
        try
        {
            lock (_gate)
            {
                if (_inner.Length >= totalBytes) return;
                _inner.SetLength(totalBytes);
            }
        }
        catch { /* 预分配失败不影响下载本身 */ }
    }

    /// <summary>
    /// 共写模式下「旁路写」就是普通写 —— 本来就是按偏移随机写，没有连续前缀的概念。
    /// </summary>
    public void WriteBypass(long position, byte[] bytes, int length)
    {
        if (length <= 0 || bytes == null) return;
        // 同步等待：调用方（DownloadService 的 moov 优先）在启动阶段顺序调用，
        // 这里写成同步只是为了接口简单；内部仍走 ConcurrentStream 的队列。
        try { _inner.WriteAsync(position, bytes, length, false).GetAwaiter().GetResult(); }
        catch { /* moov 优先是「尽力而为」，失败不影响主流程 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inner.Dispose();
    }
}
