using GeZi.DownloadEngine.Framework.Extensions.Logging;
using GeZi.DownloadEngine.Storage;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.DownloadEngine.Framework;

public class DownloadPackage : IDisposable
{
    public bool IsSaving { get; set; }
    public bool IsSaveComplete { get; set; }
    public double SaveProgress { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.None;
    public string[] Urls { get; set; }
    public long TotalFileSize { get; set; }
    public string FileName { get; set; }
    public Chunk[] Chunks { get; set; }
    /// <summary>
    /// 抢占切出来的「尾巴分片」已下载的字节数。
    ///
    /// **为什么必须单独记**：<see cref="DownloadService.TrySplitChunk"/> 把原分片的
    /// <c>End</c> 改小、把后半段做成**新的 Chunk 对象**放进 <c>_splitChunks</c> 字典，
    /// 而 <see cref="Chunks"/> 数组里仍然是原来那个（已被缩短的）对象。
    /// 于是 <c>Sum(Chunks.Position)</c> 会**永久漏掉**所有尾巴分片的进度 ——
    /// 实测后果：79.mp4 下完后进度条停在 96.8%（60.96 / 62.96 MB），
    /// 差的 2 MB 正是被抢占切走的部分。
    /// </summary>
    public long ExtraReceivedBytes { get; set; }

    public long ReceivedBytesSize => (Chunks?.Sum(chunk => chunk.Position) ?? 0) + ExtraReceivedBytes;

    /// <summary>
    /// 当前阶段说明（收尾/校验）。null = 正常下载阶段。见 <see cref="DownloadProgressChangedEventArgs.Phase"/>。
    /// </summary>
    public string Phase { get; set; }
    public bool IsSupportDownloadInRange { get; set; } = true;
    public bool InMemoryStream => string.IsNullOrWhiteSpace(FileName);
    /// <summary>
    /// 落盘策略实例。具体实现由 <see cref="BuildStorage"/> 按
    /// <see cref="DownloadConfiguration.StorageMode"/> 选择。
    /// </summary>
    public IChunkStorage Storage { get; set; }

    public void Clear()
    {
        if (Chunks != null)
        {
            foreach (Chunk chunk in Chunks)
                chunk.Clear();
        }
        Chunks = null;
    }

    /// <summary>
    /// 收尾刷盘 —— **必须 force**：即使处于暂停也要把已收下的数据真正落盘，
    /// 否则「暂停 → 取消下载」会丢掉最后一个窗口的数据。
    /// </summary>
    public async Task FlushAsync()
    {
        if (Storage?.CanWrite == true)
            await Storage.FlushAsync(force: true).ConfigureAwait(false);
    }

    [Obsolete("This method has been deprecated. Please use FlushAsync instead.")]
    public void Flush()
    {
        if (Storage?.CanWrite == true)
            Storage?.FlushAsync(force: true).Wait();
    }

    public void Validate()
    {
        foreach (var chunk in Chunks)
        {
            if (chunk.IsValidPosition() == false)
            {
                var realLength = Storage?.Length ?? 0;
                if (realLength <= chunk.Position)
                {
                    chunk.Clear();
                }
            }

            if (!IsSupportDownloadInRange)
                chunk.Clear();
        }
    }

    /// <summary>
    /// 按落盘策略创建存储。
    /// </summary>
    /// <param name="reserveFileSize">仅共写模式使用：是否预分配成满尺寸。
    /// 顺序模式永远不预分配 —— 目标是「从 0 连续增长」，预分配会在尾部留下零填充的假长度，
    /// 让「看文件大小判断进度」和「边下边看」同时失效。</param>
    /// <param name="maxMemoryBufferBytes">仅共写模式使用：内存缓冲上限。</param>
    /// <param name="logger">日志。</param>
    /// <param name="mode">落盘策略；默认顺序。</param>
    /// <param name="sequentialWindowBytes">顺序模式的重排窗口；0 = 默认 32 MB。</param>
    /// <param name="preserveExisting">续传命中时为 true：保留目标文件已有内容。</param>
    /// <param name="preserveFromOffset">续传时「已可信的连续前缀」长度（第一个未完成分片的起点）。
    /// 传 -1 表示退回用文件长度。为什么不能直接用文件长度：续传表可能要求重下某些已完成区域
    /// （ApplyResume 的 64 KB 安全余量），文件里那段是旧数据，当成已刷盘会把重下的数据整段丢弃。</param>
    public void BuildStorage(bool reserveFileSize, long maxMemoryBufferBytes = 0, ILogger logger = null,
        StorageMode mode = StorageMode.Shared, long sequentialWindowBytes = 0,
        bool preserveExisting = false, long preserveFromOffset = -1,
        int batchMaxCount = 0, long batchMaxBytes = 0)
    {
        bool memory = string.IsNullOrWhiteSpace(FileName);

        if (mode == StorageMode.Shared)
        {
            Storage = memory
                ? new SharedFileStorage(maxMemoryBufferBytes, logger)
                : new SharedFileStorage(FileName, reserveFileSize ? TotalFileSize : 0, maxMemoryBufferBytes, logger,
                    batchMaxCount, batchMaxBytes);
        }
        else
        {
            // 顺序模式：不预分配，目标文件从 0 连续增长。
            // preserveExisting 只在「续传命中」时为 true —— 那时必须保留已有内容。
            Storage = new SequentialFileStorage(
                FileName, TotalFileSize, sequentialWindowBytes, preserveExisting, preserveFromOffset);
        }
    }

    public void Dispose()
    {
        Clear();
        Storage?.Dispose();
    }
}