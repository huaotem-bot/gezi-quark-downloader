using GeZi.DownloadEngine.Framework.Extensions.Logging;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.DownloadEngine.Framework;

/// <summary>
/// Represents a thread-safe, ordered collection of objects.
/// With thread-safe multi-thread adding and single-thread consuming methodology (N producers - 1 consumer)
/// </summary>
/// <typeparam name="T">Specifies the type of elements in the ConcurrentDictionary.</typeparam>
/// <remarks>
[DebuggerTypeProxy(typeof(IReadOnlyCollection<>))]
[DebuggerDisplay("Count = {Count}")]
internal class ConcurrentPacketBuffer<T> : IReadOnlyCollection<T>, IDisposable where T : class, ISizeableObject
{
    private volatile bool _disposed = false;
    private long _bufferSize = long.MaxValue;
    protected readonly ILogger _logger;
    protected readonly SemaphoreSlim _queueConsumeLocker = new SemaphoreSlim(0);
    protected readonly PauseTokenSource _addingBlocker = new PauseTokenSource();
    protected readonly PauseTokenSource _flushBlocker = new PauseTokenSource();
    protected readonly ConcurrentQueue<T> _queue;

    public long BufferSize
    {
        get => _bufferSize;
        set
        {
            _bufferSize = (value <= 0) ? long.MaxValue : value;
        }
    }

    public ConcurrentPacketBuffer(long size, ILogger logger = null) : this(logger)
    {
        BufferSize = size;
    }

    public ConcurrentPacketBuffer(ILogger logger = null)
    {
        _queue = new ConcurrentQueue<T>();
        _logger = logger;
    }

    public IEnumerator<T> GetEnumerator()
    {
        return _queue.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public int Count => _queue.Count;
    public bool IsAddingCompleted => _addingBlocker.IsPaused;
    public bool IsEmpty => _queue.Count == 0;

    public T[] ToArray()
    {
        return _queue.ToArray();
    }

    public async Task<bool> TryAdd(T item)
    {
        try
        {
            await _addingBlocker.WaitWhilePausedAsync().ConfigureAwait(false);
            _flushBlocker.Pause();
            _queue.Enqueue(item);
            _queueConsumeLocker.Release();
            StopAddingIfLimitationExceeded(item.Length);
            return true;
        }
        catch (OperationCanceledException)
        {
            // 取消必须上抛：以前这里连取消一起吞成 false，调用方又不看返回值，
            // 结果「暂停/取消瞬间正在写的那一包」静默消失，文件长度对、内容缺一段。
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task WaitTryTakeAsync(CancellationToken cancellation, Func<T, Task> callbackTask)
    {
        try
        {
            await _queueConsumeLocker.WaitAsync(cancellation).ConfigureAwait(false);
            if (_queue.TryDequeue(out var item) && item != null)
            {
                await callbackTask(item).ConfigureAwait(false);
            }
        }
        finally
        {
            ResumeAddingIfEmpty();
        }
    }

    /// <summary>
    /// 一次取走**一批**元素（大厂式写合并的取数端）。
    ///
    /// <para>
    /// 原实现一次只取一个，消费端就得为每 64KB 做一次 Seek + 一次写 ——
    /// 16 条连接交错到达时，磁盘磁头在 16 个位置之间反复跳，
    /// 机械盘寻道直接吃掉一半带宽。批量取让消费端能先攒一批、
    /// 按偏移排序、再把相邻的合并成一次大写入。
    /// </para>
    ///
    /// <para>
    /// <b>信号量配对</b>：生产者每入队一次就 Release 一次。批量取走 N 个只消耗
    /// 1 个计数，会留下 N-1 个多余计数。这里在取完后调用
    /// <see cref="DrainExtraSignalsIfEmpty"/> 把它们消耗掉，
    /// 否则消费端会在队列已空时被虚假唤醒、空转烧 CPU。
    /// </para>
    /// </summary>
    /// <param name="cancellation">取消令牌</param>
    /// <param name="maxCount">本次最多取多少个</param>
    /// <param name="maxBytes">本次最多取多少字节（超过就不再取，留给下一批）</param>
    /// <param name="output">取出的元素写进这里（调用方负责 Clear）</param>
    /// <returns>取到至少一个元素返回 true</returns>
    public async Task<bool> WaitTryTakeBatchAsync(CancellationToken cancellation, int maxCount, long maxBytes, List<T> output)
    {
        if (maxCount < 1) maxCount = 1;
        if (maxBytes < 1) maxBytes = 1;

        output.Clear();

        // 先试着直接取：队列里可能已经有货，不必白等一次信号量
        if (!TryTakeBatchInternal(maxCount, maxBytes, output))
        {
            await _queueConsumeLocker.WaitAsync(cancellation).ConfigureAwait(false);
            if (!TryTakeBatchInternal(maxCount, maxBytes, output))
            {
                // 信号量被唤醒但队列是空的（批量取遗留的计数），
                // 把这个空计数还回去，免得下次又空转
                DrainExtraSignalsIfEmpty();
                return false;
            }
        }

        DrainExtraSignalsIfEmpty();
        return true;
    }

    /// <summary>
    /// 调用方**写完**一批之后必须调这个，用于恢复「刷新阻塞」和「写入背压」。
    ///
    /// <para>
    /// 🚨 <b>必须放在写入之后，不能放在取包之后</b>（2026-10-24 踩过的真 bug）：
    /// 这里会把 <c>_flushBlocker</c> 恢复，而 <see cref="WaitToComplete"/>（即
    /// <c>ConcurrentStream.FlushAsync</c> 等的就是它）一旦被恢复就立即返回。
    /// 若在取包后立刻恢复，<c>FlushAsync</c> 会在数据还没落盘时就返回，
    /// 调用方读到的仍是预分配出来的零填充文件 —— 表现是「长度对、内容全 0」。
    /// </para>
    /// </summary>
    public void NotifyBatchConsumed()
    {
        ResumeAddingIfEmpty();
    }

    private bool TryTakeBatchInternal(int maxCount, long maxBytes, List<T> output)
    {
        if (!_queue.TryDequeue(out var first) || first == null)
            return false;

        output.Add(first);
        long bytes = first.Length;

        while (output.Count < maxCount && bytes < maxBytes)
        {
            if (!_queue.TryDequeue(out var item) || item == null)
                break;
            output.Add(item);
            bytes += item.Length;
        }

        return true;
    }

    /// <summary>
    /// 队列已空时，把批量取遗留的多余信号量计数逐个消耗掉。
    /// 用 <c>Wait(0)</c> 非阻塞尝试，绝不会卡住。
    /// </summary>
    private void DrainExtraSignalsIfEmpty()
    {
        while (_queue.IsEmpty)
        {
            if (!_queueConsumeLocker.Wait(0))
                return;
        }
    }

    private void StopAddingIfLimitationExceeded(long packetSize)
    {
        if (BufferSize < packetSize * Count)
        {
            _logger?.LogDebug($"写缓冲已达 {packetSize * Count} 字节，暂停入队等待内存释放");
            StopAdding();
        }
    }

    private void ResumeAddingIfEmpty()
    {
        if (IsEmpty)
        {
            _flushBlocker.Resume();
            ResumeAdding();
        }
    }

    /// <summary>
    /// 强制放行两个闸门（2026-10-25）。
    ///
    /// <para>
    /// <c>_flushBlocker</c> 只在「队列恰好空」时被 <see cref="ResumeAddingIfEmpty"/> 恢复，
    /// <c>_addingBlocker</c> 同理。一旦消费端（ConcurrentStream.Watcher）异常退出，
    /// 就再没有人会去恢复它们 —— 生产端永久卡在 <c>TryAdd</c>，
    /// <c>FlushAsync</c> 永久卡在 <c>WaitToComplete</c>，
    /// 界面表现为「100% + 1/256 连接 + 永远停在正在写入磁盘」。
    /// </para>
    ///
    /// <para>
    /// 这个方法给消费端的退出路径和刷盘超时兜底使用：宁可少写几个包（异常场景本来就会报错），
    /// 也不能让整个下载永久假死。
    /// </para>
    /// </summary>
    public void ForceResume()
    {
        _flushBlocker.Resume();
        _addingBlocker.Resume();
    }

    public async Task WaitToComplete()
    {
        await _flushBlocker.WaitWhilePausedAsync().ConfigureAwait(false);
    }

    public void StopAdding()
    {
        _logger?.LogDebug("写缓冲已满，暂停接收新数据");
        _addingBlocker.Pause();
    }

    public void ResumeAdding()
    {
        _logger?.LogDebug("写缓冲已腾出空间，恢复接收新数据");
        _addingBlocker.Resume();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopAdding();
        _queueConsumeLocker.Dispose();
        _addingBlocker.Resume();
    }
}
