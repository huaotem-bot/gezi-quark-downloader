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
    /// <summary>末段补片的最小切分粒度（默认 256KB，对齐 refs/YunX 的 MIN_ELASTIC_BLOCK）。</summary>
    private long _tailRefillMinBytes = 256L * 1024;
    private int _preemptMaxPerRound = 3;
    private double _preemptSpeedRatio = 0.34;
    private long _lowestSpeedLimit = 16L * 1024;
    private int _lowestSpeedLimitSeconds = 10;
    private int _maxSpeedReconnects = 3;
    private IResumeStore _resumeStore;
    private IContentValidator _validator;
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