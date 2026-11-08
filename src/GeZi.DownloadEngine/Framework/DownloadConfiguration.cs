using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GeZi.DownloadEngine.Planning;
using GeZi.DownloadEngine.Storage;
using GeZi.DownloadEngine.Resume;
using GeZi.DownloadEngine.Validation;
using GeZi.DownloadEngine.Transport;

namespace GeZi.DownloadEngine.Framework;

public class DownloadConfiguration : ICloneable, INotifyPropertyChanged
{
    private int _bufferBlockSize;
    private int _chunkCount;
    private long _maximumBytesPerSecond;
    private int _maximumTryAgainOnFailover;
    private long _maximumMemoryBufferBytes;
    private bool _checkDiskSizeBeforeDownload;
    private bool _parallelDownload;
    private int _parallelCount;
    private int _timeout;
    private bool _rangeDownload;
    private long _rangeLow;
    private long _rangeHigh;
    private bool _clearPackageOnCompletionWithFailure;
    private long _minimumSizeOfChunking;
    private bool _reserveStorageSpaceBeforeStartingDownload;

    // 不挂空委托：OnPropertyChanged 里已经用 `PropertyChanged?.Invoke(...)` 判空，
    // 再挂一个 `delegate { }` 只会让每次无订阅的赋值都白调一次空方法。
    public event PropertyChangedEventHandler PropertyChanged;

    public DownloadConfiguration()
    {
        RequestConfiguration = new RequestConfiguration(); // default requests configuration
        _maximumTryAgainOnFailover = int.MaxValue; // the maximum number of times to fail.
        _parallelDownload = false; // download parts of file as parallel or not
        _parallelCount = 0; // number of parallel downloads
        _chunkCount = 1; // file parts to download
        _timeout = 1000; // timeout (millisecond) per stream block reader
        _bufferBlockSize = 1024; // usually, hosts support max to 8000 bytes
        _maximumBytesPerSecond = ThrottledStream.Infinite; // No-limitation in download speed
        _checkDiskSizeBeforeDownload = true; // check disk size for temp and file path
        _rangeDownload = false; // enable ranged download
        _rangeLow = 0; // starting byte offset
        _rangeHigh = 0; // ending byte offset
        _clearPackageOnCompletionWithFailure = false; // Clear package and downloaded data when download completed with failure
        _minimumSizeOfChunking = 512; // minimum size of chunking to download a file in multiple parts
        _reserveStorageSpaceBeforeStartingDownload = false; // Before starting the download, reserve the storage space of the file as file size.
    }

    /// <summary>
    /// Create the OnPropertyChanged method to raise the event
    /// The calling member's name will be used as the parameter.
    /// </summary>
    /// <param name="name">changed property name</param>
    protected void OnPropertyChanged([CallerMemberName] string name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private IChunkTransport _transport;
    private bool _applyGlobalServicePointSettings;

    /// <summary>
    /// 是否在构造时套用 bezzad 原有的全局 <c>ServicePointManager</c> 设置。
    /// 默认 <c>false</c>：GeZi 主程序由 <c>NetworkConfig.Apply()</c> 统一管理全局参数，
    /// 不能被这里的 1000 连接 / 10 秒空闲 / TLS 1.2 覆盖。
    /// </summary>
    public bool ApplyGlobalServicePointSettings
    {
        get { return _applyGlobalServicePointSettings; }
        set
        {
            _applyGlobalServicePointSettings = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 取流通道。默认走 Windows 自带的 SChannel（<see cref="HttpChunkTransport"/>）；
    /// UC 必须换成 <see cref="OpenSslChunkTransport"/>，否则 CDN 会按 TLS 指纹回 403。
    /// </summary>
    public IChunkTransport Transport
    {
        get { return _transport ?? HttpChunkTransport.Instance; }
        set
        {
            _transport = value;
            OnPropertyChanged();
        }
    }

    private IChunkPlanner _planner;
    private bool _enableSlowChunkPreemption;
    private int _retryBaseDelayMs = 150;
    private int _retryMaxDelayMs = 5000;
    private int _preemptIntervalMs = 2000;
    private long _preemptMinBytes = 512L * 1024;
    /// <summary>
    /// 末段补片的最小切分粒度（默认 64KB，对齐 refs/YunX 的 MIN_ELASTIC_BLOCK）。
    /// <para>
    /// 🚨🚨 2026-11-08 从 256KB 降到 64KB（**这是修回归，不是调参**）。
    /// <see cref="DownloadService.TryRefillTail"/> 的判据是
    /// <c>bestRemain &lt; TailRefillMinBytes * 2 → return false</c>、
    /// <c>maxSlices = maxRemain / TailRefillMinBytes</c>、<c>maxSlices &lt; 2 → return false</c>。
    /// 而 2026-11-07 的分片公式改造把常规片长从 512KB 降到 128KB 以后，
    /// 128 / 256 = 0 ⇒ **收尾期一刀都切不出来，直接 return false**，
    /// 末段退化成「剩余片数 × 单连接速度」。
    /// 实测症状：`24.20 MB/s / 88.9%` → 十秒后 `1.83 MB/s / 95.9%`（掉 13 倍）。
    /// 64KB 保证哪怕只剩一个 128KB 的片也能切成两刀。
    /// </para>
    /// </summary>
    private long _tailRefillMinBytes = 64L * 1024;
    private int _preemptMaxPerRound = 3;
    private double _preemptSpeedRatio = 0.34;
    private long _lowestSpeedLimit = 16L * 1024;
    private int _lowestSpeedLimitSeconds = 10;
    private int _maxSpeedReconnects = 3;
    /// <summary>2026-11-05：动态慢阈值（由 DownloadService 监控任务实时更新），见 <see cref="DynamicSlowThresholdBps"/>。</summary>
    private long _dynamicSlowThresholdBps;
    /// <summary>2026-11-05：相对慢阈值比例，默认 0.5（对齐 refs/YunX 的 avgPerConn/2）。</summary>
    private double _dynamicSlowRatio = 0.5;
    /// <summary>2026-11-05：整体限速判定的最少样本数，默认 4。</summary>
    private int _overallSlowMinSamples = 4;
    /// <summary>2026-11-05：整体限速抑制窗口的粘滞时长，默认 30 秒。</summary>
    private int _overallSlowHoldMs = 30_000;
    /// <summary>2026-11-05：整体限速进行中的实时标记（由 DownloadService 监控任务维护）。</summary>
    private volatile bool _overallSlowActive;
    /// <summary>2026-11-08：收尾期标志（在飞连接数已很少），由 DownloadService 每轮 PreemptRound 维护，供 ChunkDownloader 放缓慢重连。</summary>
    private volatile bool _endgameNow;
    /// <summary>2026-11-05：慢分片判定的绝对下限，默认 12 KB/s。</summary>
    private long _preemptMinBps = 12L * 1024;
    /// <summary>2026-11-05：收尾期在飞连接数门槛，默认 3。</summary>
    private int _preemptEndgameInflight = 3;
    /// <summary>2026-11-07：「服务端忽略 Range 回 200」的容忍闸门（每个任务一个实例，可为 null）。</summary>
    private GeZi.Core.Support.RangeDowngradeGate _rangeDowngrade;
    /// <summary>2026-11-07：忽略 Range 的容忍次数，默认 3（对齐 refs/YunX）。</summary>
    private int _rangeDowngradeTolerance = GeZi.Core.Support.RangeDowngradeGate.DefaultTolerance;
    /// <summary>2026-11-07：忽略 Range 的统计窗口，默认 60 秒。</summary>
    private long _rangeDowngradeWindowMs = GeZi.Core.Support.RangeDowngradeGate.DefaultWindowMs;
    /// <summary>2026-11-05：慢启动开关，默认开启。</summary>
    private bool _enableSlowStart = true;
    /// <summary>2026-11-05：慢启动硬超时，默认 10 秒。</summary>
    private int _slowStartMaxWaitMs = 10_000;
    private IResumeStore _resumeStore;
    private IContentValidator _validator;
    /// <summary>2026-11-04：源文件指纹，见 <see cref="SourceKey"/>。</summary>
    private string _sourceKey;
    private int _resumeSaveIntervalMs = 3000;
    private string _expectedSha256;
    private string _expectedSha1;
    private string _expectedMd5;
    private StorageMode _storageMode = StorageMode.Shared;
    private long _sequentialWindowBytes;
    private bool _enableMoovFirst = true;
    private int _batchMaxCount;
    private long _batchMaxBytes;

    /// <summary>
    /// 写合并一批最多攒多少个 packet；0 = 默认 64。
    ///
    /// <para>
    /// 🚨 2026-10-24：机械盘必须放大这个值。批次上限 64 时，512 条连接
    /// 每批只能凑到 64 个来自不同位置的孤立包，排序后仍是 64 段 ——
    /// 每 4 MB 就要 64 次寻道（HDD 约 10 ms/次），上限约 6 MB/s。
    /// 放大到 512 后，同一连接连续读出的多个 64 KB 能在批内拼成 1 MB 级长段，
    /// 寻道次数降一个数量级。
    /// </para>
    /// </summary>
    public int BatchMaxCount
    {
        get { return _batchMaxCount; }
        set { _batchMaxCount = value; }
    }

    /// <summary>写合并一批最多攒多少字节；0 = 默认 16 MB。机械盘建议 64 MB。</summary>
    public long BatchMaxBytes
    {
        get { return _batchMaxBytes; }
        set { _batchMaxBytes = value; }
    }

    /// <summary>
    /// 是否启用「慢连接抢占」（动态再切分）。
    ///
    /// 默认 <c>false</c>，保持 bezzad 原行为。网盘下载应打开 —— 这是自研引擎
    /// 与 bezzad 唯一的真实性能差距来源：
    /// 迅雷 83MB 文件 16 线程，自研引擎（有抢占）**9.47 MB/s**，
    /// bezzad（无抢占，即使换成 HttpClient 连接池）**4.95 MB/s**。
    ///
    /// 原理：CDN 按连接限速，总有几条连接明显比别的慢。片数固定时，慢连接
    /// 攥着整片慢慢啃，其它连接干完就闲 —— 「尾部塌缩」。抢占机制定期检测
    /// 进度明显落后的分片，把它剩余区间从中间切一半，后半段交回队列让
    /// 空闲的 worker 领走，从而把慢连接的影响摊薄。
    /// </summary>
    /// <summary>
    /// 是否启用「播放优先级」（边下边看）。
    ///
    /// 为什么单独一个开关：播放优先级依赖队列式调度（worker 从队列领任务），
    /// 而 <see cref="EnableSlowChunkPreemption"/> 关闭时走的是 bezzad 原生路径
    /// （每个 chunk 一个 Task + 信号量），**没有队列可重排**，优先级完全不生效。
    /// 所以想要边下边看，这个开关必须打开。
    /// </summary>
    private bool _enablePlaybackPriority;

    public bool EnablePlaybackPriority
    {
        get { return _enablePlaybackPriority; }
        set
        {
            _enablePlaybackPriority = value;
            OnPropertyChanged();
        }
    }

    public bool EnableSlowChunkPreemption
    {
        get { return _enableSlowChunkPreemption; }
        set
        {
            _enableSlowChunkPreemption = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 分片失败后的首次重试间隔（毫秒），之后按 2 倍指数退避。默认 150。
    ///
    /// **不要改回「直接等 Chunk.Timeout」**：那会让每次 503 都干等 15 秒，
    /// 并发越高越慢（实测 32 线程吞吐崩到 1.74 MB/s）。
    /// 150ms 是实测最优：迅雷 83MB 文件 16 线程，
    /// 150ms → 12.98 MB/s（三轮均值），300ms → 12.10，15 秒 → 5.0。
    /// </summary>
    public int RetryBaseDelayMs
    {
        get { return _retryBaseDelayMs; }
        set
        {
            _retryBaseDelayMs = value < 10 ? 10 : value;
            OnPropertyChanged();
        }
    }

    /// <summary>重试退避上限（毫秒）。默认 5000。</summary>
    public int RetryMaxDelayMs
    {
        get { return _retryMaxDelayMs; }
        set
        {
            _retryMaxDelayMs = value < 50 ? 50 : value;
            OnPropertyChanged();
        }
    }

    /// <summary>抢占检测间隔（毫秒）。默认 2000。</summary>
    public int PreemptIntervalMs    {
        get { return _preemptIntervalMs; }
        set
        {
            _preemptIntervalMs = value < 200 ? 200 : value;
            OnPropertyChanged();
        }
    }

    /// <summary>可被抢占的分片至少要剩这么多字节（太小的片切了没意义）。默认 512KB。</summary>
    public long PreemptMinBytes
    {
        get { return _preemptMinBytes; }
        set
        {
            _preemptMinBytes = value < 64 * 1024 ? 64 * 1024 : value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 末段补片的**最小切分粒度**（字节）。默认 256KB。
    ///
    /// <para>
    /// 与 <see cref="PreemptMinBytes"/> 的区别：那个管的是「慢片抢占」（某片跑得慢才切），
    /// 这个管的是「末段补片」（队列空了、还有大片没下完，主动切给空闲 worker）。
    /// 末段可以切得比抢占更细 —— 收尾时反正只剩几片，多开几条连接就是多赚，
    /// 但也不能碎到每片一个 Range 请求，所以下限取 256KB
    /// （对齐 refs/YunX 的 MIN_ELASTIC_BLOCK = 256KB）。
    /// </para>
    /// </summary>
    public long TailRefillMinBytes
    {
        get { return _tailRefillMinBytes; }
        set
        {
            _tailRefillMinBytes = value < 64 * 1024 ? 64 * 1024 : value;
            OnPropertyChanged();
        }
    }

    /// <summary>每轮最多抢占几个分片（防一次性切太碎）。默认 3。</summary>
    public int PreemptMaxPerRound
    {
        get { return _preemptMaxPerRound; }
        set
        {
            _preemptMaxPerRound = value < 1 ? 1 : value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 慢的判定阈值：某分片本轮速度低于「全局平均每连接速度 × 该比例」即视为慢。
    /// 默认 0.34（约三分之一）。
    /// </summary>
    public double PreemptSpeedRatio
    {
        get { return _preemptSpeedRatio; }
        set
        {
            _preemptSpeedRatio = value <= 0 ? 0.34 : value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 「慢连接」的绝对速度下限（字节/秒）。某条连接持续低于这个速度就主动断开重连。
    /// 默认 16384（16 KB/s）；设为 0 或负数表示关闭。
    ///
    /// **为什么需要它**：已有的超时重连只抓「卡住不动」的连接 ——
    /// 一条 20 KB/s 的连接读满 64 KB 只要 3.2 秒，永远碰不到 15 秒的读超时，
    /// 于是它就这么慢慢啃完整片，把整条下载的尾巴拖长。这正是 aria2 的
    /// <c>--lowest-speed-limit</c> 要解决的问题：不等超时，低于阈值就直接换连接。
    ///
    /// **阈值必须保守**：CDN 慢连接多为单连接问题（换个 TCP 连接就好），
    /// 但也可能整条链路都慢（如阿里对单 IP 限速 2.5 MB/s 摊到 16 路 ≈ 156 KB/s）。
    /// 16 KB/s 只杀真正的「半死」连接，不会误杀正常偏慢的连接。
    /// </summary>
    public long LowestSpeedLimit
    {
        get { return _lowestSpeedLimit; }
        set
        {
            _lowestSpeedLimit = value < 0 ? 0 : value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 判定「慢」需要连续观察多少秒。默认 10。
    ///
    /// 观察窗口太短会被「TCP 慢启动 + 首包延迟」误判：一条健康的连接
    /// 刚建立时前 1~2 秒速度可能确实很低，3 秒窗口会让它刚热身就被砍掉，
    /// 结果所有连接都在慢启动阶段反复重连，永远跑不起来。
    /// </summary>
    public int LowestSpeedLimitSeconds
    {
        get { return _lowestSpeedLimitSeconds; }
        set
        {
            _lowestSpeedLimitSeconds = value < 3 ? 3 : value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 同一分片最多因「太慢」重连几次。默认 3。
    ///
    /// 必须有上限：某些 CDN 会对同一 Range 稳定地只给低带宽，
    /// 无限重连等于把带宽全花在 TCP 握手和 HTTP 请求头上。
    /// 达到上限后该分片不再重连，交给慢连接抢占机制把剩余区间切给别人。
    /// </summary>
    public int MaxSpeedReconnects
    {
        get { return _maxSpeedReconnects; }
        set
        {
            _maxSpeedReconnects = value < 0 ? 0 : value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 2026-11-05 新增：**动态慢阈值**（字节/秒）。0 = 未设置，退回只用 <see cref="LowestSpeedLimit"/>。
    ///
    /// <para>
    /// 🚨🚨 为什么需要它（用户实测日志：分片 24 只有 10239 B/s 被判慢并重连）：
    /// <see cref="LowestSpeedLimit"/> 是**绝对值** 16384 B/s。当用户把并发开到 256/512 时，
    /// 带宽被摊到每条连接上必然远低于 16KB/s —— 于是**几乎所有连接同时被判慢**，
    /// 触发重连风暴，吞吐周期性塌陷。实测 512 路时单连接约 10KB/s，正好落在阈值下方。
    /// </para>
    ///
    /// <para>
    /// 对照 refs/YunX 的 <c>preemptSlowChunks</c>：它用的是**相对阈值** ——
    /// <c>floor = max(PREEMPT_MIN_BPS, 本任务平均单连接速度 / 2)</c>。
    /// 用相对值是为了适配不同 CDN 的限速档次：夸克单连接几十 KB/s、迅雷更低，
    /// 固定阈值必然有一边不合适；而绝对下限兜住「收尾只剩一两路、平均值被自己拉低」的退化情况。
    /// </para>
    ///
    /// <para>
    /// 本属性由 <c>DownloadService</c> 的监控任务按「实时平均单连接速度 × 比例」周期性更新；
    /// 真正生效的阈值 = <c>max(LowestSpeedLimit, 本属性)</c> —— **绝对下限永远兜底**。
    /// </para>
    /// </summary>
    public long DynamicSlowThresholdBps
    {
        get { return _dynamicSlowThresholdBps; }
        set
        {
            long v = value < 0 ? 0 : value;
            if (v == _dynamicSlowThresholdBps) return;   // 避免每轮都触发 PropertyChanged
            _dynamicSlowThresholdBps = v;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 相对慢阈值的比例：某条连接速度低于「本任务平均单连接速度 × 本值」即视为慢。
    /// 默认 0.5（对齐 refs/YunX 的 <c>avgPerConn / 2</c>）。
    /// <para>0 或负数表示关闭相对判定，只按 <see cref="LowestSpeedLimit"/> 绝对值判。</para>
    /// </summary>
    public double DynamicSlowRatio
    {
        get { return _dynamicSlowRatio; }
        set
        {
            _dynamicSlowRatio = value <= 0 ? 0 : value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 慢分片判定的**绝对下限**（字节/秒）。默认 12288（12 KB/s）。
    ///
    /// <para>
    /// 判定阈值 = <c>max(本值, 平均单连接速度 × PreemptSpeedRatio)</c>。
    /// 没有这个下限时，收尾阶段只剩一两路、平均值被自己拉低，
    /// 阈值会跟着塌到几百 B/s，谁都判不出慢（对齐 refs/YunX 的 PREEMPT_MIN_BPS）。
    /// </para>
    /// </summary>
    public long PreemptMinBps
    {
        get { return _preemptMinBps; }
        set
        {
            _preemptMinBps = value < 0 ? 0 : value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 「收尾期」的在飞连接数门槛。默认 3。
    ///
    /// <para>
    /// 在飞连接数 ≤ 本值时：
    /// ① 样本太少，整体限速判定在统计上不可靠（1/1 路慢就判成「全都慢」）；
    /// ② 收尾恰恰最需要抢占 —— 只剩几路在磨，那几路的速度就是用户看到的总速度，
    ///    换节点远比干等便宜。
    /// 所以收尾期**跳过整体限速判定**，永远允许抢占。
    /// </para>
    /// </summary>
    public int PreemptEndgameInflight
    {
        get { return _preemptEndgameInflight; }
        set
        {
            _preemptEndgameInflight = value < 1 ? 1 : value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 是否启用「慢启动」（分批放行 worker）。默认 <c>true</c>。
    ///
    /// <para>
    /// 关闭后所有 worker 一次性开跑 —— 这是融合引擎原来的行为。
    /// 只有在排查「启动瞬间大面积失败」时才需要关掉做对照。
    /// </para>
    /// </summary>
    public bool EnableSlowStart
    {
        get { return _enableSlowStart; }
        set
        {
            _enableSlowStart = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 慢启动的**硬超时**（毫秒）。默认 10000。
    ///
    /// <para>
    /// 慢启动正常情况下 1~2 秒就能爬满（1→2→4→16→256 只需 4 步）。
    /// 但如果 CDN 卡住不返回数据，「等一批就放下一批」会永远等下去 ——
    /// 超时后强制放开全部并发，保证下载不会被慢启动自己卡死。
    /// </para>
    /// </summary>
    public int SlowStartMaxWaitMs
    {
        get { return _slowStartMaxWaitMs; }
        set
        {
            _slowStartMaxWaitMs = value < 500 ? 500 : value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 「整体限速」判定的最少有效样本数。默认 4。
    ///
    /// <para>
    /// 少于这个数就只做抢占、不做整体限速判定 —— 样本太少时统计不可靠：
    /// 启动爬坡期只有两三路刚建连，瞬时速度天然接近 0，
    /// 一算就是「100% 的连接都慢」，把正常启动误判成整体限速。
    /// </para>
    /// </summary>
    public int OverallSlowMinSamples
    {
        get { return _overallSlowMinSamples; }
        set
        {
            _overallSlowMinSamples = value < 2 ? 2 : value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 判定「整体限速」后，暂停慢连接抢占的**粘滞时长**（毫秒）。默认 30000。
    ///
    /// <para>
    /// 🚨🚨 为什么必须是粘滞而不是每轮重算：慢连接是**抖动**的 ——
    /// 这一轮 8/10 路慢、下一轮 3/10 路慢，每轮独立判定就会在
    /// 「判定整体限速 → 恢复抢占 → 又判定整体限速」之间反复横跳，
    /// 每次都掐掉一批连接重连，白白消耗握手和带宽。
    /// 30 秒窗口保证判定一旦成立就稳定压住 30 秒，期间只观察不动手。
    /// </para>
    /// </summary>
    public int OverallSlowHoldMs
    {
        get { return _overallSlowHoldMs; }
        set
        {
            _overallSlowHoldMs = value < 1000 ? 1000 : value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 「整体限速」是否正在进行中（由 <c>DownloadService</c> 的监控任务维护，只读）。
    ///
    /// <para>
    /// 语义：**大多数连接同时低于阈值**，说明是服务端按 IP 的整体配额低，
    /// 而不是个别慢节点 —— 此时换连接、切片、重连全都无用，只会白烧握手。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 这个标记**只用于抑制慢连接抢占**，绝不能外泄给别的机制。
    /// 老引擎（<c>SegmentedDownloader</c>）里曾用它去拦「假死抢救」，
    /// 结果造成自我锁定：整体限速 → 不抢救 → 速度归零 → 更确信整体限速。
    /// </para>
    /// </summary>
    public bool OverallSlowActive
    {
        get { return _overallSlowActive; }
        set
        {
            if (value == _overallSlowActive) return;
            _overallSlowActive = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 收尾期标志（2026-11-08 新增）。由 <c>DownloadService.PreemptRound</c> 每轮维护：
    /// 在飞连接数 ≤ <see cref="PreemptEndgameInflight"/> 即为收尾期。
    ///
    /// <para>
    /// 用途：收尾期只剩最后几片时，每条慢连接都被反复「断开重连」是纯负收益 ——
    /// 换连接也大概率还是慢（整条 IP 的供给没变），反而每次丢 TCP+TLS 握手 200~600ms
    /// 且可能丢已下字节。此时放慢重连节奏，让它硬下完。
    /// </para>
    /// </summary>
    public bool EndgameNow
    {
        get { return _endgameNow; }
        set
        {
            if (value == _endgameNow) return;
            _endgameNow = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 「服务端忽略 Range 回 200」的容忍闸门（2026-11-07 新增）。
    ///
    /// <para>
    /// 🚨🚨 原行为：任一请求了区间的分片拿到 200 而非 206 → 抛
    /// <c>FatalChunkException</c> → 整个任务失败**并清掉续传文件**。
    /// 一个下到 90% 的大文件，因为某个 CDN 节点偶发忽略一次 Range 就全部作废。
    /// </para>
    ///
    /// <para>
    /// 对照 refs/YunX 的 <c>RANGE_IGNORED_TOLERANCE = 3</c>：前 3 次只当普通失败重试，
    /// 第 4 次才认定「服务端真的不支持 Range」。本属性提供同一个计数器，
    /// 由 <c>ChunkDownloader</c> 在 <c>GuardChunk</c> 判出 ResourceChanged 时调用。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 每个下载任务自己 new 一个（由 <c>FusionDownloader.BuildOptions</c> 或
    /// <c>DownloadService</c> 在开始时创建），**不要跨任务共享** ——
    /// 共享会让「A 任务的偶发」累加到「B 任务」上，把好好的任务作废。
    /// 传 null 时 <c>ChunkDownloader</c> 退回老行为（直接致命错误），保证默认等价。
    /// </para>
    /// </summary>
    public GeZi.Core.Support.RangeDowngradeGate RangeDowngrade
    {
        get { return _rangeDowngrade; }
        set
        {
            if (ReferenceEquals(value, _rangeDowngrade)) return;
            _rangeDowngrade = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 「服务端忽略 Range」的容忍次数（默认 3，对齐 refs/YunX 的 RANGE_IGNORED_TOLERANCE）。
    /// 只在不传 <see cref="RangeDowngrade"/> 时无意义；≤0 时按默认值处理。
    /// </summary>
    public int RangeDowngradeTolerance
    {
        get { return _rangeDowngradeTolerance; }
        set
        {
            _rangeDowngradeTolerance = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 「服务端忽略 Range」的统计窗口（毫秒，默认 60 秒）。
    /// 没有窗口的话，一个下了三小时的文件中途偶发三次 200 也会被累加成超容忍。
    /// </summary>
    public long RangeDowngradeWindowMs
    {
        get { return _rangeDowngradeWindowMs; }
        set
        {
            _rangeDowngradeWindowMs = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 分片规划器。默认 <see cref="StaticChunkPlanner"/>（bezzad 原生静态等分，向后兼容）。
    /// 网盘下载应设为 <see cref="AdaptiveChunkPlanner"/> —— 它给出「每连接 8 片」的
    /// 窃取深度，实测比静态等分快 2.7 倍（1.00 → 2.69 MB/s）。
    /// </summary>
    public IChunkPlanner Planner
    {
        get { return _planner ?? StaticChunkPlanner.Instance; }
        set
        {
            _planner = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 续传状态存取器。默认 <see cref="NullResumeStore"/>（不落任何盘）。
    /// 网盘下载应设为 <see cref="FileResumeStore.Instance"/>，把分片进度存到
    /// 目标文件旁边的 <c>.geziresume</c>，进程被杀或用户暂停后可从断点续传。
    ///
    /// ⚠️ 续传命中时会**用保存的分片表覆盖 Planner 的结果** —— 分片表必须与
    /// 上次一致，否则 Position 与区间对不上会写出错位的坏文件。
    /// </summary>
    public IResumeStore ResumeStore
    {
        get { return _resumeStore ?? NullResumeStore.Instance; }
        set
        {
            _resumeStore = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 内容校验器。默认 <see cref="IfRangeValidator.Instance"/>（If-Range + Content-Range + 哈希）。
    /// </summary>
    public IContentValidator Validator
    {
        get { return _validator ?? IfRangeValidator.Instance; }
        set
        {
            _validator = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 2026-11-04：源文件指纹（<c>总长|网盘序号|源文件Fid</c>），写入 .geziresume。
    /// <para>
    /// 用于识别「同一目标路径换成了另一个源文件」。
    /// 为什么不靠 ETag：那是服务端给的，CDN 不回、探测超时、走 OpenSSL 桥时都会缺失，
    /// 缺失时旧版会**静默跳过校验**，于是同名不同文件的续传状态被无条件采信。
    /// </para>
    /// <para>空串 = 不做这项校验（老调用方 / 自检），退回只按 TotalBytes 判。</para>
    /// </summary>
    public string SourceKey
    {
        get { return _sourceKey ?? ""; }
        set
        {
            _sourceKey = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 落盘策略。<b>2026-10-24 起固定为 <see cref="StorageMode.Shared"/></b>（预分配 + 共写 + 16 MB 写合并 = 没有合并阶段）。
    ///
    /// 两套落盘哲学的决定性差别（2026-10-24 实测 + 参考项目考证）：
    /// <list type="bullet">
    /// <item><b>Sequential</b>：乱序数据进内存窗口重排，再严格顺序刷盘，目标文件从 0 连续增长。
    ///   机械盘不需要寻道；任意时刻已落盘部分是有效前缀，<b>可以边下边看</b>；
    ///   磁盘占用 1×、I/O 量 1×。代价是内存窗口（见 <see cref="SequentialWindowBytes"/>）。</item>
    /// <item><b>Shared</b>：bezzad 原生共写，多连接随机写同一文件。
    ///   内存占用最低，但机械盘磁头疯狂寻道（用户实测「2.0 后版本磁盘占用 100%」）、
    ///   文件中间有空洞<b>不能边下边看</b>、预分配+随机写会产出「长度对内容坏」
    ///   （gopeed 同样踩过，只能 <c>Truncate(0)</c> 整份重下）。</item>
    /// </list>
    /// </summary>
    public StorageMode StorageMode
    {
        get { return _storageMode; }
        set
        {
            _storageMode = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 顺序落盘的重排窗口大小（字节）。默认 0 = 用 <see cref="SequentialFileStorage.DefaultWindowBytes"/>（32 MB）。
    /// 窗口越大越能吸收连接速度差异、越少背压，但内存占用越高。
    /// 3 个任务并发时实际占用约为 3× 该值。
    /// </summary>
    public long SequentialWindowBytes
    {
        get { return _sequentialWindowBytes; }
        set
        {
            _sequentialWindowBytes = value < 0 ? 0 : value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 是否启用「moov 优先」（默认 true）。仅对 MP4/M4V/MOV 生效。
    ///
    /// <para>
    /// 背景：没有做 faststart 的 MP4（ffmpeg 默认输出）结构是
    /// <c>ftyp → free → mdat(全部视频数据) → moov(索引)</c>，
    /// 索引表紧贴文件尾。播放器必须先读 moov 才能解码，所以「只下了前几 MB」
    /// 的半截文件一律报「无法播放」—— 这与落盘策略无关，顺序写、随机写都一样。
    /// </para>
    ///
    /// <para>
    /// 开启后，下载开始时会多发 2 个 Range 请求（读头部 64 KB + 从 mdat 末尾读 4 MB）
    /// 定位 moov，然后把这通常只有几百 KB 的索引<b>单独抓下来直接写到文件正确偏移</b>，
    /// 之后视频数据仍按原策略下载。这样用户下到几 MB 就能开始看了。
    /// </para>
    ///
    /// <para>
    /// 只在<b>全新下载</b>时执行（续传时索引要么已在文件里，要么等它自然下完即可），
    /// 失败一律静默降级，绝不影响主下载流程。
    /// </para>
    /// </summary>
    public bool EnableMoovFirst
    {
        get { return _enableMoovFirst; }
        set
        {
            _enableMoovFirst = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 🚨 2026-11-07：由上层注入的「这个任务是不是被用户删除并移除了」查询。
    /// 默认 null = 普通取消/暂停，行为逐字不变。取消分支读到 true 时不再写续传快照，
    /// 并主动清一次，根治「删除线程删 .geziresume / 取消流程又写回来」的竞态。
    /// </summary>
    public Func<bool> IsDeleteRemoveRequested { get; set; }

    /// <summary>
    /// 🚨🚨 2026-11-07 修「顺序写文件第一秒就是 45MB、只能看前面一小段」：
    /// 顺序写的最大价值是「文件从 0 连续增长、长度＝真实进度」，
    /// 但 moov 优先会调 EnsureLength(总长) 把文件预分配成满尺寸（全零 + 尾部索引），
    /// 于是长度恒等于总长、播放器按索引读到中间全是零 → 只能播开头一小段。
    /// 二者在顺序写下互斥。
    ///
    /// <para>默认 <b>false</b>：顺序写跳过 moov 优先，文件真·连续增长；
    /// 代价是「下完才能播」（诚实降级，不再两头骗）。共写不受此项影响，照旧做 moov 优先。</para>
    /// <para>置 true = 用户明确接受「顺序写也要预分配」的取舍，此时行为回到改造前。</para>
    /// </summary>
    public bool MoovFirstOnSequential { get; set; }

    /// <summary>
    /// 续传状态的保存间隔（毫秒）。默认 3000。
    /// 太频繁会拖慢下载（每次要写文件），太稀疏则强杀时丢的进度多。
    /// </summary>
    public int ResumeSaveIntervalMs
    {
        get { return _resumeSaveIntervalMs; }
        set
        {
            _resumeSaveIntervalMs = value < 500 ? 500 : value;
            OnPropertyChanged();
        }
    }

    /// <summary>期望的 SHA-256（小写十六进制）。非空时下载完成后优先用它校验。</summary>
    public string ExpectedSha256
    {
        get { return _expectedSha256; }
        set
        {
            _expectedSha256 = value;
            OnPropertyChanged();
        }
    }

    /// <summary>期望的 SHA-1（小写十六进制）。</summary>
    public string ExpectedSha1
    {
        get { return _expectedSha1; }
        set
        {
            _expectedSha1 = value;
            OnPropertyChanged();
        }
    }

    /// <summary>期望的 MD5（小写十六进制）。</summary>
    public string ExpectedMd5
    {
        get { return _expectedMd5; }
        set
        {
            _expectedMd5 = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Stream buffer size which is used for size of blocks
    /// </summary>
    public int BufferBlockSize
    {
        get
        {
            // 限速时把块缩小，是为了让节流判断更及时（大块读一次就超额度了）。
            // 但下限不能没有：限速 8 连接共 64KB/s 时每连接 8KB/s，
            // 原来的写法直接把缓冲区缩到 8KB —— 每次只读 8KB，
            // 读取次数和 HTTP 帧开销占比一起飙升，限速反而更慢。
            // 8KB 已经是「够小能及时节流」的下限，再小只会亏吞吐。
            const long MinBlock = 8L * 1024;
            long cap = MaximumSpeedPerChunk;
            if (cap < MinBlock) cap = MinBlock;
            if (cap > _bufferBlockSize) cap = _bufferBlockSize;
            return (int)cap;
        }
        set
        {
            _bufferBlockSize = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Check disk available size for download file before starting the download.
    /// </summary>
    public bool CheckDiskSizeBeforeDownload
    {
        get => _checkDiskSizeBeforeDownload;
        set
        {
            _checkDiskSizeBeforeDownload = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// File chunking parts count
    /// </summary>
    public int ChunkCount
    {
        get => _chunkCount;
        set
        {
            _chunkCount = Math.Max(1, value);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The maximum bytes per second that can be transferred through the base stream.
    /// </summary>
    public long MaximumBytesPerSecond
    {
        get => _maximumBytesPerSecond;
        set
        {
            _maximumBytesPerSecond = value <= 0 ? long.MaxValue : value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The maximum bytes per second that can be transferred through the base stream at each chunk downloader.
    /// This Property is ReadOnly.
    /// </summary>
    public long MaximumSpeedPerChunk => ParallelDownload
        ? MaximumBytesPerSecond / Math.Min(ChunkCount, ParallelCount)
        : MaximumBytesPerSecond;

    /// <summary>
    /// How many time try again to download on failed
    /// </summary>
    public int MaxTryAgainOnFailover
    {
        get => _maximumTryAgainOnFailover;
        set
        {
            _maximumTryAgainOnFailover = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Download file chunks as Parallel or Serial?
    /// </summary>
    public bool ParallelDownload
    {
        get => _parallelDownload;
        set
        {
            _parallelDownload = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Count of chunks to download in parallel.
    /// If ParallelCount is &lt;=0, then ParallelCount is equal to ChunkCount.
    /// </summary>
    public int ParallelCount
    {
        get => _parallelCount <= 0 ? ChunkCount : _parallelCount;
        set
        {
            _parallelCount = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Download a range of byte
    /// </summary>
    public bool RangeDownload
    {
        get => _rangeDownload;
        set
        {
            _rangeDownload = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The starting byte offset for ranged download
    /// </summary>
    public long RangeLow
    {
        get => _rangeLow;
        set
        {
            _rangeLow = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The ending byte offset for ranged download
    /// </summary>
    public long RangeHigh
    {
        get => _rangeHigh;
        set
        {
            _rangeHigh = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Custom body of your requests
    /// </summary>
    public RequestConfiguration RequestConfiguration { get; set; }

    /// <summary>
    /// Download timeout per stream file blocks
    /// </summary>
    public int Timeout
    {
        get => _timeout;
        set
        {
            _timeout = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Clear package and downloaded data when download completed with failure
    /// </summary>
    public bool ClearPackageOnCompletionWithFailure
    {
        get => _clearPackageOnCompletionWithFailure;
        set
        {
            _clearPackageOnCompletionWithFailure = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Minimum size of chunking and multiple part downloading
    /// </summary>
    public long MinimumSizeOfChunking
    {
        get => _minimumSizeOfChunking;
        set
        {
            _minimumSizeOfChunking = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Before starting the download, reserve the storage space of the file as file size.
    /// Default value is false.
    /// </summary>
    public bool ReserveStorageSpaceBeforeStartingDownload
    {
        get => _reserveStorageSpaceBeforeStartingDownload;
        set
        {
            _reserveStorageSpaceBeforeStartingDownload = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets the maximum amount of memory, in bytes, that the Downloader library is allowed
    /// to allocate for buffering downloaded content. Once this limit is reached, the library will
    /// stop downloading and start writing the buffered data to a file stream before continuing.
    /// The default value for is 0, which indicates unlimited buffering.
    /// </summary>
    /// <example>
    /// The following example sets the maximum memory buffer to 50 MB, causing the library to release
    /// the memory buffer after each 50 MB of downloaded content:
    /// <code>
    /// MaximumMemoryBufferBytes = 1024 * 1024 * 50
    /// </code>
    /// </example>
    public long MaximumMemoryBufferBytes
    {
        get => _maximumMemoryBufferBytes;
        set
        {
            _maximumMemoryBufferBytes = value;
            OnPropertyChanged();
        }
    }

    public object Clone()
    {
        return MemberwiseClone();
    }
}