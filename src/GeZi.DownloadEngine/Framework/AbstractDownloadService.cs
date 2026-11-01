using GeZi.DownloadEngine.Framework.Extensions.Helpers;
using GeZi.DownloadEngine.Framework.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.DownloadEngine.Framework;

public abstract class AbstractDownloadService : IDownloadService, IDisposable
{
    protected ILogger _logger;
    protected SemaphoreSlim _parallelSemaphore;

    /// <summary>
    /// 当前真正在跑的 worker 数。
    /// <para>为什么需要它：bezzad 原本用
    /// <c>ParallelCount - _parallelSemaphore.CurrentCount</c> 算活跃数，
    /// 但启用抢占的路径（<c>ParallelDownloadWithPreemption</c>）走的是自己的
    /// worker 池 + <c>DownloadChunkNoSemaphore</c>，<b>根本不经过那个信号量</b> ——
    /// 于是活跃数恒等于 0，UI 的「连接车道」永远是全灰。</para>
    /// </summary>
    protected int _liveWorkers;
    protected readonly SemaphoreSlim _singleInstanceSemaphore = new SemaphoreSlim(1, 1);
    protected CancellationTokenSource _globalCancellationTokenSource;
    protected TaskCompletionSource<AsyncCompletedEventArgs> _taskCompletion;
    protected readonly PauseTokenSource _pauseTokenSource;
    protected ChunkHub _chunkHub;
    protected List<Request> _requestInstances;
    protected readonly Bandwidth _bandwidth;
    protected DownloadConfiguration Options { get; set; }

    public bool IsBusy => Status == DownloadStatus.Running;
    public bool IsCancelled => _globalCancellationTokenSource?.IsCancellationRequested == true;
    public bool IsPaused => _pauseTokenSource.IsPaused;
    public DownloadPackage Package { get; set; }
    public DownloadStatus Status
    {
        get => Package?.Status ?? DownloadStatus.None;
        set => Package.Status = value;
    }
    public event EventHandler<AsyncCompletedEventArgs> DownloadFileCompleted;
    public event EventHandler<DownloadProgressChangedEventArgs> DownloadProgressChanged;
    public event EventHandler<DownloadProgressChangedEventArgs> ChunkDownloadProgressChanged;
    public event EventHandler<DownloadStartedEventArgs> DownloadStarted;

    public AbstractDownloadService(DownloadConfiguration options)
    {
        _pauseTokenSource = new PauseTokenSource();
        _bandwidth = new Bandwidth();
        Options = options ?? new DownloadConfiguration();
        Package = new DownloadPackage();

        // ⚠️ 迁入改造（GeZi）：这里原本会无条件改写 ServicePointManager 的全局静态设置，
        // 对我们有害：
        //   1. ServerCertificateValidationCallback 会放行「过期证书」与「自签根证书」，
        //      而且是**进程级**的 —— 一旦装上，所有 HTTPS 连接（含各网盘 API）一起降级。
        //   2. DefaultConnectionLimit=1000 / MaxServicePointIdleTime=10000 会覆盖
        //      GeZi 的 NetworkConfig.Apply()（512 / 120000）。
        //   3. SecurityProtocol 只到 TLS 1.2，会覆盖我们需要的 TLS 1.3。
        // 因此改为显式开关，默认不动全局状态，由产品侧 NetworkConfig 统一管理。
        if (Options.ApplyGlobalServicePointSettings)
        {
            ApplyGlobalServicePointSettings();
        }
    }

    public Task<Stream> DownloadFileTaskAsync(DownloadPackage package, CancellationToken cancellationToken = default)
    {
        return DownloadFileTaskAsync(package, package.Urls, cancellationToken);
    }

    public Task<Stream> DownloadFileTaskAsync(DownloadPackage package, string address, CancellationToken cancellationToken = default)
    {
        return DownloadFileTaskAsync(package, new[] { address }, cancellationToken);
    }

    public virtual async Task<Stream> DownloadFileTaskAsync(DownloadPackage package, string[] urls, CancellationToken cancellationToken = default)
    {
        Package = package;
        await InitialDownloader(cancellationToken, urls);
        return await StartDownload().ConfigureAwait(false);
    }

    public Task<Stream> DownloadFileTaskAsync(string address, CancellationToken cancellationToken = default)
    {
        return DownloadFileTaskAsync(new[] { address }, cancellationToken);
    }

    public virtual async Task<Stream> DownloadFileTaskAsync(string[] urls, CancellationToken cancellationToken = default)
    {
        await InitialDownloader(cancellationToken, urls);
        return await StartDownload().ConfigureAwait(false);
    }

    public Task DownloadFileTaskAsync(string address, string fileName, CancellationToken cancellationToken = default)
    {
        return DownloadFileTaskAsync(new[] { address }, fileName, cancellationToken);
    }

    public virtual async Task DownloadFileTaskAsync(string[] urls, string fileName, CancellationToken cancellationToken = default)
    {
        await InitialDownloader(cancellationToken, urls);
        await StartDownload(fileName).ConfigureAwait(false);
    }

    public Task DownloadFileTaskAsync(string address, DirectoryInfo folder, CancellationToken cancellationToken = default)
    {
        return DownloadFileTaskAsync(new[] { address }, folder, cancellationToken);
    }
    public virtual async Task DownloadFileTaskAsync(string[] urls, DirectoryInfo folder, CancellationToken cancellationToken = default)
    {
        await InitialDownloader(cancellationToken, urls);
        var name = await _requestInstances.First().GetFileName().ConfigureAwait(false);
        var filename = Path.Combine(folder.FullName, name);
        await StartDownload(filename).ConfigureAwait(false);
    }

    public virtual void CancelAsync()
    {
        _globalCancellationTokenSource?.Cancel(true);
        Resume();
        Status = DownloadStatus.Stopped;
    }

    public virtual async Task CancelTaskAsync()
    {
        CancelAsync();
        if (_taskCompletion != null)
            await _taskCompletion.Task.ConfigureAwait(false);
    }

    public virtual void Resume()
    {
        Status = DownloadStatus.Running;
        _pauseTokenSource.Resume();
    }

    public virtual void Pause()
    {
        _pauseTokenSource.Pause();
        Status = DownloadStatus.Paused;
    }

    public virtual async Task Clear()
    {
        try
        {
            if (IsBusy || IsPaused)
                await CancelTaskAsync().ConfigureAwait(false);

            await _singleInstanceSemaphore?.WaitAsync();

            _parallelSemaphore?.Dispose();
            _globalCancellationTokenSource?.Dispose();
            _bandwidth.Reset();
            _requestInstances = null;

            if (_taskCompletion != null)
            {
                if (_taskCompletion.Task.IsCompleted == false)
                    _taskCompletion.TrySetCanceled();

                _taskCompletion = null;
            }
            // Note: don't clear package from `DownloadService.Dispose()`.
            // Because maybe it will be used at another time.
        }
        finally
        {
            _singleInstanceSemaphore?.Release();
        }
    }

    protected async Task InitialDownloader(CancellationToken cancellationToken, params string[] addresses)
    {
        await Clear();
        Status = DownloadStatus.Created;
        _globalCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _taskCompletion = new TaskCompletionSource<AsyncCompletedEventArgs>();
        _requestInstances = addresses.Select(url => new Request(url, Options.RequestConfiguration)).ToList();
        Package.Urls = _requestInstances.Select(req => req.Address.OriginalString).ToArray();
        _chunkHub = new ChunkHub(Options);
        _parallelSemaphore = new SemaphoreSlim(Options.ParallelCount, Options.ParallelCount);
    }

    protected async Task StartDownload(string fileName)
    {
        Package.FileName = fileName;
        Directory.CreateDirectory(Path.GetDirectoryName(fileName)); // ensure the folder is exist

        if (File.Exists(fileName))
        {
            File.Delete(fileName);
        }

        await StartDownload().ConfigureAwait(false);
    }

    protected abstract Task<Stream> StartDownload();

    protected void OnDownloadStarted(DownloadStartedEventArgs e)
    {
        Status = DownloadStatus.Running;
        Package.IsSaving = true;
        DownloadStarted?.Invoke(this, e);
    }

    protected void OnDownloadFileCompleted(AsyncCompletedEventArgs e)
    {
        Package.IsSaving = false;

        if (e.Cancelled)
        {
            Status = DownloadStatus.Stopped;
        }
        else if (e.Error != null)
        {
            if (Options.ClearPackageOnCompletionWithFailure)
            {
                Package.Storage?.Dispose();
                Package.Storage = null;
                Package.Clear();
                if (Package.InMemoryStream == false)
                    File.Delete(Package.FileName);
            }
        }
        else // completed
        {
            Package.Clear();
        }

        if (Package.InMemoryStream == false)
        {
            Package.Storage?.Dispose();
            Package.Storage = null;
        }

        _taskCompletion.TrySetResult(e);
        DownloadFileCompleted?.Invoke(this, e);
    }

    /// <summary>
    /// 取当前活跃分片数：优先用抢占路径维护的真实计数，回退到信号量差值。
    /// </summary>
    protected int LiveActiveChunks()
    {
        int live = Volatile.Read(ref _liveWorkers);
        if (live > 0)
        {
            return live;
        }
        SemaphoreSlim sem = _parallelSemaphore;
        if (sem == null)
        {
            return 0;
        }
        int busy = Options.ParallelCount - sem.CurrentCount;
        return busy > 0 ? busy : 0;
    }

    /// <summary>
    /// 汇总一次分片进度并对外广播。
    /// </summary>
    /// <remarks>
    /// 改成 <c>virtual</c> 是为了让 <see cref="DownloadService"/> 能把「抢占切出的尾巴分片」
    /// 的字节数补进 <see cref="DownloadPackage.ExtraReceivedBytes"/> ——
    /// 那些分片不在 <c>Package.Chunks</c> 数组里，基类按数组求和必然漏算。
    /// </remarks>
    protected virtual void OnChunkDownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
    {
        // 🚨 已取消就不再广播进度：取消后 worker 还在收尾（关连接、落盘、记账），
        // 这段时间冒出来的进度事件会把界面上「已取消」的任务又刷成「下载中 xx%」，
        // 用户看到进度还在跳会以为取消没生效。取消后的状态由界面自己定格。
        if (IsCancelled)
            return;

        // 只有「服务端没给总长」时才允许用已下载量顶替总长（未知长度的流式下载）。
        // 原来无条件改写，一旦 Done 因重复下载/重连重下而虚高，总长就被抬高，
        // 进度条永远到不了 100% —— 这是「100% 假象」的另一条来源。
        if (Package.TotalFileSize <= 0 && e.ReceivedBytesSize > 0)
            Package.TotalFileSize = e.ReceivedBytesSize;

        _bandwidth.CalculateSpeed(e.ProgressedByteSize);
        var totalProgressArg = new DownloadProgressChangedEventArgs(nameof(DownloadService)) {
            TotalBytesToReceive = Package.TotalFileSize,
            ReceivedBytesSize = Package.ReceivedBytesSize,
            BytesPerSecondSpeed = _bandwidth.Speed,
            AverageBytesPerSecondSpeed = _bandwidth.AverageSpeed,
            ProgressedByteSize = e.ProgressedByteSize,
            ReceivedBytes = e.ReceivedBytes,
            ActiveChunks = LiveActiveChunks(),
            Phase = Package.Phase,
        };
        Package.SaveProgress = totalProgressArg.ProgressPercentage;
        e.ActiveChunks = totalProgressArg.ActiveChunks;
        ChunkDownloadProgressChanged?.Invoke(this, e);
        DownloadProgressChanged?.Invoke(this, totalProgressArg);
    }

    /// <summary>
    /// 主动广播一次「阶段」进度（收尾、校验等无网络流量的阶段）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="OnChunkDownloadProgressChanged"/> 的区别：**不碰带宽统计**。
    /// 那个方法会用 <c>ProgressedByteSize</c> 重算速度，传 0 进去会把速度算成 0，
    /// 还会污染平均速度；这里直接构造参数，速度显式给 0、连接数给 0，
    /// 让界面明确呈现「没有网络流量了，在做本地工作」。
    /// </remarks>
    protected void RaisePhaseProgress(string phase)
    {
        Package.Phase = phase;
        var arg = new DownloadProgressChangedEventArgs(nameof(DownloadService))
        {
            TotalBytesToReceive = Package.TotalFileSize,
            ReceivedBytesSize = Package.ReceivedBytesSize,
            BytesPerSecondSpeed = 0,
            AverageBytesPerSecondSpeed = _bandwidth.AverageSpeed,
            ProgressedByteSize = 0,
            ReceivedBytes = null,
            ActiveChunks = 0,
            Phase = phase,
        };
        try
        {
            DownloadProgressChanged?.Invoke(this, arg);
        }
        catch
        {
            // 观察者异常不能影响下载主流程。
        }
    }

    /// <summary>
    /// 广播一帧「已完成」的最终进度：<c>ReceivedBytesSize</c> 直接取文件总长。
    /// </summary>
    /// <remarks>
    /// 🚨 为什么需要它（2026-10-25 实测反馈「有些没到 100% 就显示已完成」）：
    /// 任务完成时界面百分比停在 <b>99.9%</b>，原因有两层 ——
    /// <list type="number">
    /// <item>完成帧 <c>JobState.Completed</c> 不带 Progress（见 <c>DownloadScheduler.Report</c>），
    /// 界面只能保留最后一次 Downloading 帧的百分比；</item>
    /// <item>最后一帧本身差约一个 64KB 包：最后一个分片的收尾进度事件
    /// 与完成信号几乎同时发生，UI 侧不一定赶上。</item>
    /// </list>
    /// 走到这里说明内容校验已经通过（不通过会抛 <c>InvalidDataException</c>），
    /// 文件字节数必然等于总长，所以把 Done 钉成 Total 是<b>真实值</b>，不是粉饰。
    /// </remarks>
    protected void RaiseCompletedProgress()
    {
        Package.Phase = null;
        var arg = new DownloadProgressChangedEventArgs(nameof(DownloadService))
        {
            TotalBytesToReceive = Package.TotalFileSize,
            ReceivedBytesSize = Package.TotalFileSize,
            BytesPerSecondSpeed = 0,
            AverageBytesPerSecondSpeed = _bandwidth.AverageSpeed,
            ProgressedByteSize = 0,
            ReceivedBytes = null,
            ActiveChunks = 0,
            Phase = null,
        };
        Package.SaveProgress = 100.0;
        try
        {
            DownloadProgressChanged?.Invoke(this, arg);
        }
        catch
        {
            // 观察者异常不能影响下载主流程。
        }
    }

    public void AddLogger(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 显式套用 bezzad 原有的全局网络设置。
    /// </summary>
    /// <remarks>
    /// 只有独立使用本引擎、且没有自己的 <c>NetworkConfig</c> 时才应该调用。
    /// GeZi 主程序**不要**打开：<c>DownloadConfiguration.ApplyGlobalServicePointSettings</c>
    /// 保持默认 false，全局参数由 <c>GeZi.DownloadEngine.NetworkConfig.Apply()</c> 负责。
    /// 特别注意这里不安装「放行过期/自签证书」的回调。
    /// </remarks>
    public static void ApplyGlobalServicePointSettings()
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
        ServicePointManager.Expect100Continue = false;
        ServicePointManager.DefaultConnectionLimit = 1000;
        ServicePointManager.MaxServicePointIdleTime = 10000;
    }

    public virtual void Dispose()
    {
        Clear().Wait();
    }
}