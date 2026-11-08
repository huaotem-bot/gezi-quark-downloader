using GeZi.DownloadEngine.Framework.Extensions.Helpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GeZi.DownloadEngine.Planning;
using GeZi.DownloadEngine.Resume;
using GeZi.DownloadEngine.Storage;
using GeZi.DownloadEngine.Validation;
using GeZi.DownloadEngine.Transport;
using GeZi.DownloadEngine.Media;

namespace GeZi.DownloadEngine.Framework;

public class DownloadService : AbstractDownloadService
{
    public DownloadService(DownloadConfiguration options) : base(options) { }
    public DownloadService() : base(null) { }

    /// <summary>
    /// 暂停：除了让 worker 停止从网络读，还要把共写模式的**写盘队列**一起刹住。
    /// <para>
    /// 🚨 2026-10-28 补：原来只调基类的 Pause()，worker 停了但
    /// <c>ConcurrentStream</c> 的 Watcher 线程毫不知情，会继续把队列里积压的
    /// 数据（最多一个 16 MB 写合并窗口）全部写进磁盘。
    /// 用户看到的就是「点了暂停，磁盘还在写、文件还在变大」，体感上等于没暂停。
    /// </para>
    /// <para>
    /// 顺序写模式没有独立写线程（worker 自己同步写），不受影响。
    /// </para>
    /// </summary>
    public override void Pause()
    {
        base.Pause();
        try
        {
            var shared = Package?.Storage as SharedFileStorage;
            shared?.PauseWrites();
        }
        catch
        {
            // 存储层已释放时调不到，忽略：暂停语义仍由基类的 PauseToken 保证。
        }
    }

    /// <summary>恢复：worker 与写盘队列一起恢复。</summary>
    public override void Resume()
    {
        try
        {
            var shared = Package?.Storage as SharedFileStorage;
            shared?.ResumeWrites();
        }
        catch
        {
        }

        base.Resume();
    }

    /// <summary>
    /// 顺序写模式下默认优先下载的「头部窗口」长度（字节）。
    /// <para>
    /// 2026-11-05：用于消除队头阻塞 + 实现边下边看。取 16 MB：
    /// 播放器一般只需要开头几 MB 就能起播，16 MB 既够用又不会把
    /// 后面所有连接都饿死（每轮监控都会重新按 _flushed 推进窗口）。
    /// </para>
    /// </summary>
    private const long SequentialHeadPriorityBytes = 16L * 1024 * 1024;

    // ---- 阶段3：续传 + 内容校验 ----
    private string _resumeKey;
    private string _validatorValue = "";

    /// <summary>
    /// 2026-11-04：源文件指纹（本地算的，不依赖服务端响应头）。
    /// <para>
    /// 格式：<c>总长|网盘类型|源文件Fid</c>。写入 .geziresume 的 <c>sourcekey</c> 行，
    /// 续传加载时与本次任务比对 —— 不一致说明「同一目标路径换成了另一个源文件」，
    /// 必须整份作废重下，否则会拼出「长度对、内容坏」的成品。
    /// </para>
    /// <para>
    /// 空串 = 调用方没提供（老代码路径 / 自检），此时不做这项校验，退回按 TotalBytes 判。
    /// </para>
    /// </summary>
    private string _sourceKeyValue = "";

    // 🚨 2026-11-08：md5 不可信的「源文件指纹」缓存（进程级，跨任务共享）。
    //   夸克 getDownloadUrl 返回的 md5 字段与实际内容不符，但内容本身是好的；
    //   这种「元数据问题」对同一个源文件（SourceKey 相同）每次都一样。
    //   第一次复核确认「抽样逐字节一致」后记下指纹，后续同一源文件直接跳过抽样，
    //   不再每个文件白跑十几秒网络复核。key = SourceKey（空时退回 ExpectedMd5）。
    //   进程重启即失效（可接受：重启后第一次只跑 4 段，几秒就回来）。
    private static readonly ConcurrentDictionary<string, byte> Md5Untrusted =
        new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

    private CancellationTokenSource _resumeSaveCts;
    private Task _resumeSaveTask;

    // ---- 慢连接抢占切出来的分片 ----
    // 抢占会「先改原分片 End，再把后半段作为新 Chunk 入队」，但 Package.Chunks 是规划阶段
    // 定下的原始数组，新片不会进数组。若保存续传时只看 Package.Chunks，原片 End 已被改小、
    // 后半段却缺失 → 分片表出现空洞 → ResumeState.IsUsable() 判不连续 → 整份续传被丢弃
    // （实测「取消后完全没有 .geziresume」就是这个原因）。所以这里单独登记，保存时合并。
    private readonly ConcurrentDictionary<string, Chunk> _splitChunks =
        new ConcurrentDictionary<string, Chunk>(StringComparer.Ordinal);

    /// <summary>
    /// 分片表读写的互斥锁（2026-11-01，续传可靠性方案 A）。
    /// <para>
    /// 🚨🚨 为什么必须有它：<c>TrySplitChunk</c> / <c>TrySplitChunkTail</c> 的动作是两步 ——
    /// ① <c>chunk.End = mid</c>（把原片改短）；② <c>_splitChunks[tail.Id] = tail</c>（登记新片）。
    /// 这两步**不是原子的**。若保存快照恰好落在两步之间，快照里就是
    /// 「原片变短了 + 新片不在」→ 分片表出现一个空洞 →
    /// <c>ResumeState.IsUsable()</c> 判不连续 → **整份续传被丢弃 → 从零重下**。
    /// </para>
    /// <para>
    /// 实测证据（用户日志 2026-11-01）：6 次空洞全部**恰好 524288 字节 = 512 KiB**，
    /// 起点全部落在 X.5 MiB，时间点全在下载开始后 1~6 秒 ——
    /// 512KiB 正是 <c>PreemptMinBytes</c> 的默认值，说明就是切分瞬间被快照撞上了。
    /// </para>
    /// <para>
    /// 锁内只做「改 End + 登记新片」这一组极短的操作，不含任何 I/O，
    /// 因此不会影响下载吞吐（实测每任务切分几十次，每次微秒级）。
    /// </para>
    /// </summary>
    private readonly object _chunkTableLock = new object();

    /// <summary>
    /// 保存快照期间**暂停切分**（2026-11-01，续传可靠性方案 E）。
    /// <para>
    /// 锁（方案 A）已经保证了「改 End + 登记新片」的原子性，但保存本身要遍历全表、
    /// 调 <c>FlushAsync</c>、裁剪 Position，耗时可能几十毫秒。
    /// 这段时间里切分线程会一直阻塞在锁上 —— 与其阻塞，不如让它们**直接跳过这一轮**：
    /// 切分本来就是「下次再切也行」的优化，跳过一轮对吞吐没有可感知影响，
    /// 却能让保存更快完成，进一步降低撞进中间态的概率。
    /// </para>
    /// </summary>
    private volatile bool _chunkSplitPaused;

    /// <summary>
    /// 末段补片的串行闸门。
    /// <para>
    /// 队列空时会有**多个 worker 同时**调 <c>TryRefillTail</c>，若不串行，
    /// 两个线程可能拿到同一个 victim 各切一刀 —— 第二次切分基于已被改小的 End，
    /// 区间计算就会错乱。锁内只做 List/Dictionary 遍历与一次入队，不含 I/O。
    /// </para>
    /// </summary>
    private readonly object _tailRefillGate = new object();

    /// <summary>
    /// 「整体限速」判定器（状态机）。
    /// <para>
    /// 抽成独立类型不是为了好看，是为了**能自检**：<see cref="PreemptRound"/> 需要真实分片、
    /// 真实连接、真实计时器才能跑，而判定规则本身是纯逻辑（样本数 / 慢数 / 是否收尾 / 时间）。
    /// 抽出来之后自检可以覆盖「粘滞窗口」「收尾期豁免」「样本不足不判定」这些边界，
    /// 不必起真实下载。
    /// </para>
    /// </summary>
    private readonly OverallSlowGate _overallSlow = new OverallSlowGate();

    /// <summary>
    /// 当前允许的**在飞连接上限**（0 = 尚未接管，等于不限）。
    /// <para>
    /// 🚨 2026-11-08 第二次修正：原来这里是「连接数爬山」的目标值，实测零收益（日志只有 2 条、
    /// 两次都直接等于用户设的上限），已整段关闭。现在改由
    /// <see cref="UpdateEndgameCap"/> 接管：可领片数不够时把上限压下来，
    /// 避免几千个 worker 空转抢 <c>_tailRefillGate</c> 锁拖慢真正在跑的那几十路。
    /// </para>
    /// <para>
    /// 🚨 为什么是「限制在飞数」而不是「改 worker 数」：
    /// worker 池是一次性 <c>Task.Run</c> 开满的（见 <c>workerCount</c>），
    /// 运行中无法增减。所以降连接只能让多余 worker 在取片前排队等待 ——
    /// 效果等价（真正在跑的连接数就是 <see cref="_liveWorkers"/>），
    /// 而且完全不打扰已经在跑的那些连接（不重连、不重排分片、续传零影响）。
    /// </para>
    /// </summary>
    private int _climbCap;

    /// <summary>
    /// 收尾期**主动降并发**（2026-11-08 新增，取代已关闭的连接数爬山）。
    ///
    /// <para>
    /// 🚨 为什么必须降：分片表是下载开始前一次性切完的**静态片**，片数固定。于是
    /// <c>在飞连接数 = min(目标并发, 可领片数)</c>，
    /// <c>可领片数 ≈ 总片数 × (1 − 进度)</c>。
    /// 442MB / 128KB 片 = 约 3380 片，设 2048 路时：
    /// 进度超过 1 − 2048×128KB/442MB ≈ 39% 之后片就开始不够分；
    /// 到 99% 只剩 4.4MB = 34 片，其余 2000 多个 worker 全部空转。
    /// </para>
    ///
    /// <para>
    /// 空转不是免费的：每个空转 worker 每轮都要抢 <c>_tailRefillGate</c> 锁、
    /// 遍历 active 找最大片 —— 几千个 worker 每秒数万次无效加锁，
    /// 真正在下载的那几十路反而抢不到 CPU（用户实测「卡 28 秒，暂停再恢复立刻跑完」）。
    /// </para>
    ///
    /// <para>
    /// 所以这里按「当前可领片数」把 <see cref="_climbCap"/> 压下来。它只限制**在飞上限**，
    /// 不重连、不重排分片、不动续传 —— 已经在跑的连接完全不受影响。
    /// 额外留 <see cref="RefillProbeHeadroom"/> 条余量，保证仍有空闲 worker
    /// 能走到 <see cref="TryRefillTail"/> 去切大片（否则降并发会把补片一起锁死）。
    /// </para>
    /// </summary>
    private void UpdateEndgameCap(ChunkQueue queue, ConcurrentDictionary<string, Chunk> active)
    {
        int target = Options.ParallelCount;
        if (target < 1) target = 1;
        if (target > ChunkSplitter.MaxRealConcurrency) target = ChunkSplitter.MaxRealConcurrency;

        // 可领片数 = 正在跑的 + 队列里排着的。这就是「现在最多能喂饱几路」。
        long afford = (long)active.Count + queue.Count;
        long cap = afford + RefillProbeHeadroom;
        if (cap > target) cap = target;
        if (cap < EndgameMinCap) cap = EndgameMinCap;

        int newCap = cap > int.MaxValue ? int.MaxValue : (int)cap;
        if (newCap == _climbCap)
            return;

        int old = _climbCap;
        _climbCap = newCap;

        // 只在「真的降到了目标以下」时记一条：开局 cap 等于目标并发是正常态，不必刷日志。
        // 收尾期只降不涨，所以一条就够，不会刷屏。
        if (newCap < target && (old == 0 || newCap < old))
        {
            _logger?.LogInfo(string.Format(
                "收尾期降并发：可领片数 {0}（在跑 {1} + 排队 {2}），目标 {3} 路，已把在飞上限压到 {4} 路以免空转抢锁。",
                afford, active.Count, queue.Count, target, newCap));
        }
    }

    /// <summary>
    /// 收尾期降并发的下限：再怎么收尾也不压到 64 路以下。
    /// 🚨 2026-11-08 实测：原来 4 路会把最后 1~2% 压成「只剩个位数片、
    /// 每个片靠 1 条连接下、这条连接被限速后反复重连磨 40 秒」（日志 12:59~13:00 段）。
    /// 抬到 64 保留「少空转抢锁」的好处，但绝不把最后一段逼成孤军奋战——
    /// 64 路足以让 TryRefillTail 把大片尾巴切碎后多路并行冲完。
    /// </summary>
    private const int EndgameMinCap = 64;

    /// <summary>
    /// 降并发时给「补片探针」留的余量：始终允许少量空闲 worker 越过上限去调
    /// <see cref="TryRefillTail"/>，否则降并发会把「切大片救收尾」这条路一起锁死。
    /// </summary>
    private const int RefillProbeHeadroom = 8;

    /// <summary>
    /// 把字节/秒格式化成「12.3 KB/s」这种人类可读形式。
    /// <para>只用于日志，不进界面 —— 界面速度格式化走 <c>Util.FormatSize</c> 那一套。</para>
    /// </summary>
    private static string FmtBps(double bps)
    {
        if (bps < 0) bps = 0;
        if (bps >= 1024 * 1024)
            return (bps / (1024.0 * 1024.0)).ToString("0.0") + " MB";
        if (bps >= 1024)
            return (bps / 1024.0).ToString("0.0") + " KB";
        return bps.ToString("0") + " B";
    }

    protected override async Task<Stream> StartDownload()
    {
        try
        {
            await _singleInstanceSemaphore.WaitAsync();
            _splitChunks.Clear();
            // 抢占尾巴分片的字节计数与阶段说明都必须在每次新下载开始时归零，
            // 否则上一轮残留的尾巴字节会让进度条一开始就超过 0%。
            Package.ExtraReceivedBytes = 0;
            Package.Phase = null;
            // 准备阶段全程给可见状态：这几步都是网络往返，没有进度事件，
            // 界面会停在上一帧 —— 用户以为卡死了。400ms 后显示，快的话不闪。
            SchedulePhase("正在获取文件信息…");
            Package.TotalFileSize = await _requestInstances.First().GetFileSize().ConfigureAwait(false);
            Package.IsSupportDownloadInRange = await _requestInstances.First().IsSupportDownloadInRange().ConfigureAwait(false);
            // 探测资源标识（强 ETag 优先，回退 Last-Modified）。
            // 它有两个用途：① 每个分片请求带 If-Range，资源一变服务端就回 200 让我们停手；
            // ② 判断上次的续传状态是不是同一份文件。
            _validatorValue = await ProbeValidatorAsync(_globalCancellationTokenSource.Token).ConfigureAwait(false);
            // 2026-11-04：源文件指纹由调用方给（本地算的，不依赖服务端响应头）。
            // 见 ResumeState.SourceKey 的注释 —— 它兜住 Validator 探测不到时的空档。
            _sourceKeyValue = Options.SourceKey ?? "";
            CancelScheduledPhase();
            Package.Phase = null;

            // 续传：命中就用保存的分片表**覆盖** Planner 的结果。
            // 必须用原表，否则 Position 与区间对不上，写出来是错位的坏文件。
            _resumeKey = Package.InMemoryStream ? null : Package.FileName;
            ResumeState resume = await LoadResumeAsync(Package.TotalFileSize, _validatorValue, _globalCancellationTokenSource.Token).ConfigureAwait(false);

            // ⚠️ 落盘层必须在知道「有没有续传」之后创建：
            // 顺序落盘默认用 FileMode.Create，会清空目标文件；续传时那等于把已下好的数据删掉，
            // 而分片表还指着旧偏移，最终产出一个「长度对、内容坏」的文件。
            //
            // 续传时还要把「已可信的连续前缀」告诉落盘层：ApplyResume 会按安全余量把某些已完成
            // 区域退回去重下，那段在文件里是旧数据；若落盘层把文件长度当成已刷盘，重下的数据会被
            // 「已写过就丢弃」整段吃掉，worker 仍把 Position 推到片尾 → 报完成但文件没变长。
            long safeOffset = resume != null ? SafeResumeOffset(resume) : -1;
            Package.BuildStorage(Options.ReserveStorageSpaceBeforeStartingDownload, Options.MaximumMemoryBufferBytes,
            _logger, Options.StorageMode, Options.SequentialWindowBytes, resume != null, safeOffset,
            Options.BatchMaxCount, Options.BatchMaxBytes);
            ValidateBeforeChunking();

            // 「moov 优先」：MP4 的索引表常在文件末尾，先把它单独抓下来写到正确偏移，
            // 用户下到几 MB 就能开始看。只在全新下载时做（续传时索引要么已在文件里，
            // 要么等自然下完即可），任何失败都静默降级，绝不影响主流程。
            //
            // 🚨 位置必须在主下载**之前**（串行），但内部必须**并行分块**。这两个条件缺一不可：
            //   · 放到主下载之后并行跑 → 主下载的 64~256 条连接已把 CDN 单 IP 带宽吃满，
            //     moov 那 16 路只能抢到残渣（实测 45 秒才写进 26%），永远抓不完。
            //   · 内部只用 1 条连接串行读 → 夸克分享直链单连接仅 ~51 KB/s，
            //     5.5 MB 的 moov 要 113 秒，期间一个分片都没开始、文件长度 0、日志空，
            //     用户看到的就是「点了下载一直没反应」。
            // 现在：主下载开始前、连接池空闲时，16 路并行把 5.5 MB 拉满带宽 ≈ 2 秒。
            if (resume == null)
            {
                SchedulePhase("正在准备边下边看…");
                await TryFetchMoovFirstAsync(_globalCancellationTokenSource.Token).ConfigureAwait(false);
                CancelScheduledPhase();
                Package.Phase = null;
            }

            if (resume != null)
            {
                ApplyResume(Package, resume);
                _logger?.LogInfo($"续传命中：{resume.Chunks.Length} 片，已有 {resume.ReceivedBytes}/{resume.TotalBytes} 字节");
            }
            else
            {
                _chunkHub.SetFileChunks(Package);
            }

            StartResumeSaver();

            // firing the start event after creating chunks
            OnDownloadStarted(new DownloadStartedEventArgs(Package.FileName, Package.TotalFileSize));

            if (Options.ParallelDownload)
            {
                await ParallelDownload(_pauseTokenSource.Token).ConfigureAwait(false);
            }
            else
            {
                await SerialDownload(_pauseTokenSource.Token).ConfigureAwait(false);
            }

            // ⚠️ 取消必须在这里显式转成异常，不能指望 ParallelDownload 抛 OCE。
            // bezzad 的 ChunkDownloader 把「已取消」当成单个分片失败：打一条 critical 日志、
            // 记到 chunk.FailoverCount，然后正常返回 —— 于是 WhenAll 正常完成，代码会继续
            // 走到下面的「内容校验 + ClearResumeAsync」，把用户的续传状态当成下载成功删掉
            // （实测现象：取消后既没有 .geziresume，又报「未校验」）。
            _globalCancellationTokenSource.Token.ThrowIfCancellationRequested();

            // 收尾：停保存器 → 最终保存 → 校验内容 → 清续传
            await StopResumeSaverAsync().ConfigureAwait(false);
            await SaveResumeSnapshotAsync(CancellationToken.None).ConfigureAwait(false);

            // 🚨 校验之前必须先把落盘层的队列排空（2026-10-24 修的真 bug）。
            // 共写模式（SharedFileStorage → ConcurrentStream）把 packet 交给后台 Watcher 线程异步写，
            // 「所有分片都下完」只代表数据进了队列，不代表数据到了磁盘。
            // 不刷就校验 = 拿半截文件算哈希 → 间歇性报「长度对、内容错」，
            // 实测每次丢的都是最后一批里约一个 16KB packet，sha 失败率约 2/3。
            // 顺序写模式不受影响，因为它的 WriteAsync 内部就同步刷盘了。
            // 刷盘阶段：共写模式要把后台写队列排空，几百 MB 可能要几秒。
            // 这期间没有网络流量，必须显式告诉界面「在干本地活」，否则看起来像卡死。
            RefreshExtraReceivedBytes();
            SchedulePhase("正在写入磁盘…");
            var flushClock = System.Diagnostics.Stopwatch.StartNew();
            await (Package?.Storage?.FlushAsync(force: true) ?? Task.FromResult(0)).ConfigureAwait(false);
            flushClock.Stop();
            CancelScheduledPhase();
            _logger?.LogInfo("收尾：刷盘耗时 " + flushClock.ElapsedMilliseconds + " ms");

            // 校验阶段：SHA-256 几百 MB 要数秒，同样没有网络流量。
            RefreshExtraReceivedBytes();
            SchedulePhase("正在校验文件完整性…");
            var verifyClock = System.Diagnostics.Stopwatch.StartNew();
            VerifyOutcome verify = await VerifyContentAsync().ConfigureAwait(false);
            verifyClock.Stop();
            CancelScheduledPhase();
            _logger?.LogInfo("收尾：内容校验耗时 " + verifyClock.ElapsedMilliseconds + " ms");
            Package.Phase = null;

            // 哈希不符 != 文件坏了（2026-10-24 全量比对坐实）。
            // 夸克 getDownloadUrl 返回的 md5 字段与实际内容不符：本地与服务器逐字节一致，
            // 但两者都不等于接口声称的 md5。直接判「文件可能已损坏」会把好文件误杀。
            // 先抽样逐字节复核：全一致 = 元数据不可信；有不一致 = 真损坏。
            if (verify.HashMismatch)
            {
                // 🚨 2026-11-08：同一源文件「哈希不符」的复核结论做进程级缓存。
                //   夸克 md5 元数据不可信是**确定性**的（同一 SourceKey 每次都一样），
                //   第一次抽样确认后，后续同一源文件直接套用结论，跳过网络复核。
                string md5CacheKey = string.IsNullOrEmpty(_sourceKeyValue)
                    ? (Options.ExpectedMd5 ?? "")
                    : _sourceKeyValue;
                bool cachedUntrusted = !string.IsNullOrEmpty(md5CacheKey)
                    && Md5Untrusted.ContainsKey(md5CacheKey);

                int check = CrossCheckMatched;
                if (cachedUntrusted)
                {
                    // 已确认过「元数据不可信、内容可用」，不再抽样。
                    _logger?.LogInfo("哈希不符（同一源文件已确认元数据不可信），跳过抽样复核，按文件可用处理。");
                }
                else
                {
                    // 复核也是网络活，可能几秒；给个可见阶段，别让界面看起来像死掉。
                    SchedulePhase("正在复核文件内容…");
                    check = await CrossCheckAsync(_globalCancellationTokenSource.Token).ConfigureAwait(false);
                    CancelScheduledPhase();
                    Package.Phase = null;
                }

                if (check == CrossCheckDamaged)
                {
                    await ClearResumeAsync().ConfigureAwait(false);
                    throw new InvalidDataException(verify.Message);
                }
                // 🚨 2026-11-07：抽样 16 段全未取到时，补一条本地长度确认日志。
                //   纯日志、零行为：不改放行结论（红线：不在内容校验里用 Fail 拦长度），
                //   只让「长度对内容坏」这类共写固有风险在现场可被直接读出来。
                if (check == CrossCheckUnverified)
                {
                    try
                    {
                        long realLen = !string.IsNullOrWhiteSpace(Package.FileName)
                            && System.IO.File.Exists(Package.FileName)
                            ? new System.IO.FileInfo(Package.FileName).Length : -1;
                        _logger?.LogInfo(string.Format(
                            "抽样复核未取到远端样本，本地长度为 {0} 字节，应有 {1} 字节；"
                            + "长度一致仅说明已刷盘，内容仍待用户打开确认。",
                            realLen, Package.TotalFileSize));
                    }
                    catch { }
                }
                else if (check == CrossCheckMatched)
                {
                    // 抽样逐字节一致 → 元数据不可信成立，记缓存供后续同源文件复用。
                    if (!string.IsNullOrEmpty(md5CacheKey))
                        Md5Untrusted.TryAdd(md5CacheKey, 1);
                }

                verify = VerifyOutcome.Warn(check == CrossCheckMatched
                    ? "内容已按服务器返回完整下载（抽样逐字节复核一致）；"
                      + "网盘接口给的哈希与实际内容不符，属元数据问题，文件可用"
                    : "已按服务器声明的长度下完（抽样复核未能取到远端样本，无法逐字节比对）；"
                      + "网盘接口给的哈希与实际内容不符，按元数据问题处理，文件可用");
            }

            if (!verify.Ok)
            {
                // 内容不对：续传状态已无意义，必须清掉，否则下次接着往坏文件里续
                await ClearResumeAsync().ConfigureAwait(false);
                throw new InvalidDataException(verify.Message);
            }

            await ClearResumeAsync().ConfigureAwait(false);
            // 补一帧 Done = Total 的最终进度：完成帧不带 Progress，
            // 界面否则会停在最后一帧的 99.9%。此时校验已通过，Total 是真实值。
            RaiseCompletedProgress();
            _logger?.LogInfo("内容校验：" + verify.Message);

            await SendDownloadCompletionSignal(DownloadStatus.Completed).ConfigureAwait(false);
        }
        catch (OperationCanceledException exp) // or TaskCanceledException
        {
            // 用户取消：保留续传状态（很小），下次重新添加同一任务可从断点续。
            // 想彻底清干净由上层调 ResumeStore.ClearAsync 决定。
            await StopResumeSaverAsync().ConfigureAwait(false);

            // 🚨🚨 2026-11-07 修「下载途中右键删除并移除，.geziresume 又回来」：
            //   取消分支原本**无条件写一次最终快照**，与删除线程删文件形成竞态，
            //   谁后跑谁赢 —— 实测删完又冒出「已暂停」、续传命中 177 片。
            //   用户明确「删除并移除」时不再写快照，反而主动清一次。
            //   默认（普通暂停/取消）该回调为 null 或返回 false，行为逐字不变。
            bool deleteRemove = false;
            try
            {
                var probe = Options.IsDeleteRemoveRequested;
                if (probe != null) deleteRemove = probe();
            }
            catch { deleteRemove = false; }

            if (deleteRemove)
            {
                await ClearResumeAsync().ConfigureAwait(false);
                _logger?.LogInfo("删除并移除：取消流程不再写续传快照，已清除续传记录。");
            }
            else
            {
                // 必须补一次最终快照：周期保存器默认 3 秒一轮，短任务被取消时可能一次都没跑过，
                // 只停保存器而不落盘 = 取消后完全没有续传信息（实测 24MB / 0.3 秒的下载就命中）。
                await SaveResumeSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await SendDownloadCompletionSignal(DownloadStatus.Stopped, exp).ConfigureAwait(false);
        }
        catch (Exception exp)
        {
            await StopResumeSaverAsync().ConfigureAwait(false);

            // 资源变更 / 区间不符 / 内容校验失败 → 分片表与目标文件都已不可信，清续传。
            // 其它失败（网络抖动、CDN 503 超限）→ 保留续传，下次接着下。
            if (exp is FatalChunkException || exp is InvalidDataException)
            {
                await ClearResumeAsync().ConfigureAwait(false);
            }
            else
            {
                await SaveResumeSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await SendDownloadCompletionSignal(DownloadStatus.Failed, exp).ConfigureAwait(false);
        }
        finally
        {
            await StopResumeSaverAsync().ConfigureAwait(false);
            _singleInstanceSemaphore.Release();
            await Task.Yield();
        }

        return Package.Storage?.OpenRead();
    }

    private async Task SendDownloadCompletionSignal(DownloadStatus state, Exception error = null)
    {
        var isCancelled = state == DownloadStatus.Stopped;
        Package.IsSaveComplete = state == DownloadStatus.Completed;
        Status = state;
        // 任务终态（完成/取消/失败）必须强制刷盘，不能因为处于暂停而跳过
        await (Package?.Storage?.FlushAsync(force: true) ?? Task.FromResult(0)).ConfigureAwait(false);
        await (_logger?.FlushAsync() ?? Task.FromResult(0)).ConfigureAwait(false);
        OnDownloadFileCompleted(new AsyncCompletedEventArgs(error, isCancelled, Package));
    }

    /// <summary>
    /// 发一次 <c>Range: bytes=0-0</c> 探测，抓取资源标识（强 ETag / Last-Modified）。
    /// 失败一律返回空串 —— 探测不到就不做 If-Range，不能让探测本身挡住下载。
    /// </summary>
    private async Task<string> ProbeValidatorAsync(CancellationToken cancel)
    {
        IContentValidator validator = Options.Validator;
        if (validator == null)
        {
            return "";
        }

        Request first = _requestInstances.FirstOrDefault();
        if (first == null || first.Address == null)
        {
            return "";
        }

        // 🚨 先吃「已经抓过的响应头」：GetFileSize / IsSupportDownloadInRange 刚发过一次
        // Range: bytes=0-0，ETag 与 Last-Modified 都已经在 Request 的缓存里。
        // 再发一次一模一样的请求纯属浪费 —— 每次往返都要重新做 TCP+TLS 握手
        // （OpenSSL 桥 200~600ms，夸克 CDN 慢时 1~2 秒），
        // 这正是用户说的「点了下载要等几秒才开始」的主因之一。
        string cached = first.GetCachedValidator();
        if (string.IsNullOrEmpty(cached) == false)
        {
            _logger?.LogDebug("资源标识直接取自响应头缓存：" + cached);
            return cached;
        }

        try
        {
            var request = new ChunkRequest
            {
                Url = first.Address.AbsoluteUri,
                Start = 0,
                End = 0,
                TimeoutMs = Options.RequestConfiguration != null ? Options.RequestConfiguration.Timeout : 30000,
            ConnectionLimit = Options.ParallelCount,
            };

            RequestConfiguration cfg = first.Configuration;
            if (cfg != null)
            {
                request.UserAgent = cfg.UserAgent;
                request.Referer = cfg.Referer;
                if (cfg.Headers != null)
                {
                    request.Cookie = cfg.Headers["Cookie"];
                    request.Origin = cfg.Headers["Origin"];
                }
            }

            using (ChunkResponse response = await Options.Transport.OpenAsync(request, cancel).ConfigureAwait(false))
            {
                return validator.Capture(response);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("探测资源标识失败（将不使用 If-Range）：" + ex.Message);
            return "";
        }
    }

    /// <summary>
    /// 「moov 优先」：把 MP4 的索引表（moov）单独抓下来写到文件的正确偏移。
    ///
    /// <para>
    /// 为什么需要：没做 faststart 的 MP4（ffmpeg 默认输出）结构是
    /// <c>ftyp → free → mdat(全部视频数据) → moov(索引)</c>，索引紧贴文件尾。
    /// 播放器必须先读 moov 才知道怎么解码，所以「只下了前几 MB」的半截文件一律报
    /// 「无法播放」。实测样本 82.mp4：83,395,509 字节，moov 在偏移 83,260,432，仅 132 KB。
    /// 把这一小块提前抓下来，用户下到几 MB 就能开始看。
    /// </para>
    ///
    /// <para>
    /// <b>全程尽力而为</b>：任何一步失败都只记一条日志然后返回，绝不影响主下载流程。
    /// 多发的最坏情况是 2 个 Range 请求（头部 64 KB + 尾部 4 MB）。
    /// </para>
    /// </summary>
    private async Task TryFetchMoovFirstAsync(CancellationToken cancel)
    {
        if (!Options.EnableMoovFirst) { _logger?.LogDebug("moov 优先：EnableMoovFirst=false，跳过"); return; }
        if (Package.TotalFileSize <= 0) { _logger?.LogDebug("moov 优先：总长为 0，跳过"); return; }
        if (!Mp4Moov.LooksLikeMp4(Package.FileName)) { _logger?.LogDebug("moov 优先：不是 MP4（" + Package.FileName + "），跳过"); return; }

        // 🚨🚨 2026-11-07：顺序写默认**不做** moov 优先。
        //   moov 优先必须 EnsureLength(总长) 预分配才能把索引写到文件尾，
        //   那会让顺序写的文件第一秒就是 45MB 全零 —— 长度不再等于真实进度，
        //   播放器按索引读到中间全是零，只能播开头一小段（用户实测）。
        //   「连续增长」与「边下边看」在顺序写下互斥，这里选前者（诚实降级为下完才能播）。
        //   共写（StorageMode.Shared）不受影响，照旧走 moov 优先（用户实测可用）。
        //   MoovFirstOnSequential 默认 false；置 true 才回到改造前行为。
        if (Options.StorageMode == StorageMode.Sequential && !Options.MoovFirstOnSequential)
        {
            _logger?.LogInfo("顺序写：跳过 moov 优先以保持文件连续增长（视频下完即可播放）；"
                + "如需边下边看可改用共写模式。");
            return;
        }

        IChunkTransport transport = Options.Transport;
        Request first = _requestInstances.FirstOrDefault();
        if (transport == null || first == null || first.Address == null)
        {
            _logger?.LogDebug("moov 优先：取流通道或请求模板不可用，跳过");
            return;
        }
        _logger?.LogDebug("moov 优先：开始探测（总长 " + Package.TotalFileSize + "，文件 " + Package.FileName + "）");

        // 🚨 必须有总预算兜底。实测夸克分享直链单连接只有 ~51 KB/s，而 moov 可以有
        // 5 MB 以上（573 MB 样本实测 5,795,878 字节）；内部已改成 16 块并行，
        // 正常几秒就能抓完，这个预算只是防止极端情况无限拖下去。
        // 注意：.NET Framework 的 ConnectStream.ReadAsync 不响应 CancellationToken，
        // 所以预算只能中断建连，中断不了正在传输的读 —— 真正保证「不卡」的是并行分块。
        const int MoovBudgetMs = 8000;
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            budget.CancelAfter(MoovBudgetMs);
            try
            {
                await FetchMoovCoreAsync(transport, first, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancel.IsCancellationRequested) throw;   // 用户取消，照常往上抛
                _logger?.LogInfo("moov 优先：超过 " + (MoovBudgetMs / 1000)
                    + " 秒预算，放弃（不影响下载，只是暂时不能边下边看）");
            }
            catch (Exception ex)
            {
                // 尽力而为：索引没抓到不影响正常下载，只是用户得多等一会儿才能播
                _logger?.LogWarning("moov 优先失败（不影响下载）：" + ex.Message);
            }
        }
    }

    /// <summary>「moov 优先」的主体：探测 mdat 结尾 → 定位 moov → 抓取 → 写入正确偏移。</summary>
    private async Task FetchMoovCoreAsync(IChunkTransport transport, Request first, CancellationToken cancel)
    {
        long total = Package.TotalFileSize;

        // ① 读头部 64 KB，找 mdat 的结束位置（= moov 的起点）
        long mdatEnd;
        bool hasTailMoov;
        using (ChunkResponse head = await OpenRangeAsync(transport, first, 0,
                   Math.Min(Mp4Moov.HeadProbeBytes - 1, total - 1), cancel).ConfigureAwait(false))
        {
            byte[] headBuf = await ReadAllAsync(head.Stream, Mp4Moov.HeadProbeBytes, cancel).ConfigureAwait(false);
            hasTailMoov = Mp4Moov.TryFindMdatEnd(headBuf, headBuf.Length, total, out mdatEnd);
        }

        if (!hasTailMoov)
        {
            // 要么已经 faststart（moov 在头部），要么结构不认识 —— 都不用管
            _logger?.LogDebug("moov 优先：头部没找到 mdat 结尾（可能已 faststart 或结构不认识），跳过");
            return;
        }

        // ② 只读 16 字节拿到 moov 的 box 头（size + type），不读整个 moov
        //    实测 573 MB 样本的 moov 有 5.5 MB，先整段读再判断会白等 100 秒以上。
        Mp4MoovInfo info;
        using (ChunkResponse box = await OpenRangeAsync(transport, first, mdatEnd,
                   Math.Min(mdatEnd + 15, total - 1), cancel).ConfigureAwait(false))
        {
            byte[] boxBuf = await ReadAllAsync(box.Stream, 16, cancel).ConfigureAwait(false);
            if (!Mp4Moov.TryFindMoovInTail(boxBuf, boxBuf.Length, mdatEnd, total, out info))
            {
                _logger?.LogInfo("moov 优先：mdat 之后不是 moov（偏移 " + mdatEnd + "），跳过");
                return;
            }
        }

        if (info == null || info.Size < 8 || info.Offset < 0 || info.Offset + info.Size > total)
        {
            _logger?.LogInfo("moov 优先：moov 区间不合理，跳过");
            return;
        }

        // ③ 并行分块抓取：实测夸克分享直链的 ~51 KB/s 是**每连接**上限，不是总带宽上限
        //    （16 路实测合计 816 KB/s ≈ 16×51，与理论完全吻合；单路串行读 5.5 MB 要 113 秒）。
        //    并行度取 64：此时主下载尚未开始、连接池空闲，64 路能把 5.5 MB 在 ~2 秒内拉完。
        //    每块读完立即写到最终偏移，内存峰值 = 单块大小。
        //    并发写安全：写的是文件里同一段字节，与分片下载内容完全一致，
        //    ConcurrentStream 的写路径有 _streamLock 串行化，重复写同一份数据无害。
        Package.Storage.EnsureLength(total);

        const int MoovParallel = 64;
        const int MinPieceBytes = 128 * 1024;
        long moovStart = info.Offset;
        long moovSize = info.Size;

        int pieces = MoovParallel;
        while (pieces > 1 && moovSize / pieces < MinPieceBytes) pieces--;
        long pieceBytes = (moovSize + pieces - 1) / pieces;

        var tasks = new Task[pieces];
        var written = new int[pieces];
        for (int i = 0; i < pieces; i++)
        {
            int idx = i;
            long pStart = moovStart + (long)idx * pieceBytes;
            if (pStart >= moovStart + moovSize) { tasks[idx] = Task.FromResult(0); continue; }
            long pEnd = Math.Min(pStart + pieceBytes, moovStart + moovSize) - 1;
            int want = (int)(pEnd - pStart + 1);
            tasks[idx] = Task.Run(async () =>
            {
                try
                {
                    using (ChunkResponse piece = await OpenRangeAsync(transport, first, pStart, pEnd, cancel).ConfigureAwait(false))
                    {
                        byte[] buf = await ReadAllAsync(piece.Stream, want, cancel).ConfigureAwait(false);
                        if (buf != null && buf.Length == want)
                        {
                            Package.Storage.WriteBypass(pStart, buf, buf.Length);
                            written[idx] = 1;
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.LogWarning("moov 优先：第 " + idx + " 块读取失败（不影响下载）：" + ex.Message);
                }
            }, cancel);
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("moov 优先：并行抓取异常（不影响下载）：" + ex.Message);
        }

        int okCount = 0;
        for (int i = 0; i < written.Length; i++) okCount += written[i];

        await Package.Storage.FlushAsync(force: true).ConfigureAwait(false);

        if (okCount == pieces)
        {
            _logger?.LogInfo("moov 优先：已提前写入索引 " + moovSize + " 字节 @ " + moovStart
                + "（" + pieces + " 块并行，下到几 MB 即可播放）");
        }
        else
        {
            _logger?.LogWarning("moov 优先：只抓到 " + okCount + "/" + pieces
                + " 块，索引不完整（不影响下载，稍后自然下完即可播放）");
        }
    }

    /// <summary>按区间发一次 Range 请求（moov 优先专用）。</summary>
    private async Task<ChunkResponse> OpenRangeAsync(IChunkTransport transport, Request template,
        long start, long end, CancellationToken cancel)
    {
        var request = new ChunkRequest
        {
            Url = template.Address.AbsoluteUri,
            Start = start,
            End = end,
            TimeoutMs = Options.RequestConfiguration != null ? Options.RequestConfiguration.Timeout : 30000,
            ConnectionLimit = Options.ParallelCount,
        };

        RequestConfiguration cfg = template.Configuration;
        if (cfg != null)
        {
            request.UserAgent = cfg.UserAgent;
            request.Referer = cfg.Referer;
            if (cfg.Headers != null)
            {
                request.Cookie = cfg.Headers["Cookie"];
                request.Origin = cfg.Headers["Origin"];
            }
        }
        ChunkResponse response = await transport.OpenAsync(request, cancel).ConfigureAwait(false);

        // 必须校验区间（2026-10-24 修的静默损坏隐患）。
        // 主下载路径有 ChunkDownloader 的 GuardChunk 守着，moov 优先这条路原来**完全没有校验**：
        // 若服务器对某个 Range 请求回 200（整个文件），ReadAllAsync 会把文件开头的数据
        // 写到 moov 的真实偏移上 -> 文件长度对、内容错、哈希不符，而且抽样很容易漏掉文件尾部。
        IContentValidator validator = Options.Validator;
        if (validator != null && response != null)
        {
            ChunkGuardResult guard = validator.GuardChunk(request, response);
            if (guard != ChunkGuardResult.Ok)
            {
                try { response.Dispose(); } catch { }
                throw new FatalChunkException("moov 优先：区间响应不符（" + guard
                    + "，请求 " + start + "-" + end + "）");
            }
        }
        return response;
    }

    /// <summary>
    /// 哈希不符时的二次确认：抽样与服务器逐字节比对。
    /// <para>
    /// 实测（2026-10-24）：夸克 <c>getDownloadUrl</c> 返回的 <c>md5</c> 与实际内容不符 ——
    /// 432,229,242 字节的文件，本地哈希 == 服务器实际内容哈希（745515ec...），
    /// 但都 != 接口声称的 md5（1c774105...）。所以「哈希不符」必须先排除「元数据不可信」，
    /// 否则会把完好的文件误判成损坏。抽 16 段 x 1 MB：全部逐字节一致 -> 元数据问题；
    /// 任何一段不一致 -> 真损坏。
    /// </para>
    /// </summary>
    /// <summary>抽样复核：发现确实不一致的字节（真损坏）。</summary>
    private const int CrossCheckDamaged = 0;

    /// <summary>抽样复核：取到了样本且逐字节一致（元数据不可信，文件可用）。</summary>
    private const int CrossCheckMatched = 1;

    /// <summary>抽样复核：一段样本都没取到，无法比对（不能据此判损坏）。</summary>
    private const int CrossCheckUnverified = 2;

    private async Task<int> CrossCheckAsync(CancellationToken cancel)
    {
        // 🚨 2026-11-08：段数从 16 降到 4。
        //   16 段 × 256KB = 4MB，但夸克直链单连接 ~51KB/s，16 段并发也要 20 秒才勉强出结果，
        //   实测「一致 0 段，不一致 0 段，未取到 16 段」—— 纯白跑，还让界面挂「复核中」17 秒。
        //   4 段横跨文件头/中/尾，采样点更分散、代表性不降反升，且 4 段并发约 5 秒就能回来。
        //   真损坏时任意一段不一致即被抓到（损坏不是「正好避开 4 个采样点」能躲掉的）。
        //   配合 md5 不可信缓存（见 Md5UntrustedCache），绝大多数夸克文件连这 4 段都不用跑。
        const int Samples = 4;

        // 🚨 样本必须小。夸克分享直链单连接实测 ~51 KB/s：
        //   1 MB 一段要 20 秒，16 段并发也正好撞上预算边缘 —— 实测 16 段全部超时，
        //   日志出现「一致 0 段，不一致 0 段，未取到 16 段」。
        //   256 KB 一段约 5 秒，1 MB 总量足以判定「是元数据问题还是真损坏」。
        const int SampleBytes = 256 * 1024;

        const int TotalBudgetMs = 12000;

        long total = Package.TotalFileSize;
        string path = Package.FileName;
        if (total <= 0 || string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return CrossCheckUnverified;
        }

        IChunkTransport transport = Options.Transport;
        Request template = _requestInstances != null ? _requestInstances.FirstOrDefault() : null;
        if (transport == null || template == null || template.Address == null)
        {
            return CrossCheckUnverified;
        }

        long span = Math.Max(1L, total / Samples);

        // 0=没跑 1=一致 2=不一致 3=没取到
        int[] results = new int[Samples];
        string[] reasons = new string[Samples];

        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            budget.CancelAfter(TotalBudgetMs);
            var tasks = new Task[Samples];
            for (int i = 0; i < Samples; i++)
            {
                int idx = i;
                long start = (long)idx * span;
                if (start >= total)
                {
                    results[idx] = 3;
                    tasks[idx] = Task.FromResult(0);
                    continue;
                }
                long end = Math.Min(total - 1, start + SampleBytes - 1);
                tasks[idx] = Task.Run(() => CompareSampleAsync(
                    transport, template, path, start, end, results, reasons, idx, budget.Token));
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
                // 单段异常已在 CompareSampleAsync 内部吞掉并记 3，这里兜底
            }
        }

        int same = 0, diff = 0, missed = 0;
        string firstReason = null;
        for (int i = 0; i < Samples; i++)
        {
            if (results[i] == 1) same++;
            else if (results[i] == 2) diff++;
            else
            {
                missed++;
                if (firstReason == null && !string.IsNullOrEmpty(reasons[i]))
                {
                    firstReason = reasons[i];
                }
            }
        }

        _logger?.LogInfo("哈希复核：抽样 " + Samples + " 段 x " + (SampleBytes / 1024) + " KB，一致 "
            + same + " 段，不一致 " + diff + " 段，未取到 " + missed + " 段"
            + (string.IsNullOrEmpty(firstReason) ? "" : "（首个失败原因：" + firstReason + "）"));

        if (diff > 0)
        {
            return CrossCheckDamaged;
        }
        if (same > 0)
        {
            return CrossCheckMatched;
        }
        // 一段都没取到：直链可能已被服务端限流/作废，无法比对。
        // 绝不能当成「复核一致」去告诉用户，也绝不能当成「损坏」把好文件杀掉。
        return CrossCheckUnverified;
    }

    /// <summary>
    /// 抽样复核的一段：拉取远端同区间，与本地同偏移逐字节比对。
    /// 结果写进 <paramref name="results"/>：1=一致 2=不一致 3=没取到。
    /// </summary>
    private async Task CompareSampleAsync(IChunkTransport transport, Request template, string path,
        long start, long end, int[] results, string[] reasons, int idx, CancellationToken cancel)
    {
        int want = (int)(end - start + 1);
        if (want <= 0)
        {
            results[idx] = 3;
            return;
        }

        ChunkResponse resp = null;
        try
        {
            resp = await OpenRangeAsync(transport, template, start, end, cancel).ConfigureAwait(false);
            if (resp == null)
            {
                reasons[idx] = "没有响应";
                results[idx] = 3;
                return;
            }
            if (resp.StatusCode != 200 && resp.StatusCode != 206)
            {
                reasons[idx] = "HTTP " + resp.StatusCode;
                results[idx] = 3;
                return;
            }

            byte[] remote = await ReadAllAsync(resp.Stream, want, cancel).ConfigureAwait(false);
            if (remote == null || remote.Length != want)
            {
                reasons[idx] = "只读到 " + (remote == null ? 0 : remote.Length) + "/" + want + " 字节";
                results[idx] = 3;
                return;
            }

            byte[] local = new byte[want];
            int read = 0;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(start, SeekOrigin.Begin);
                while (read < want)
                {
                    int n = fs.Read(local, read, want - read);
                    if (n <= 0) break;
                    read += n;
                }
            }
            if (read != want)
            {
                reasons[idx] = "本地只读到 " + read + "/" + want + " 字节";
                results[idx] = 3;
                return;
            }

            for (int k = 0; k < want; k++)
            {
                if (local[k] != remote[k])
                {
                    results[idx] = 2;
                    return;
                }
            }
            results[idx] = 1;
        }
        catch (OperationCanceledException)
        {
            reasons[idx] = "超时或被取消";
            results[idx] = 3;
        }
        catch (Exception ex)
        {
            reasons[idx] = ex.Message;
            results[idx] = 3;
        }
        finally
        {
            if (resp != null) { try { resp.Dispose(); } catch { } }
        }
    }

    /// <summary>把流读满（或读到 EOF），返回实际读到的字节。</summary>
    private static async Task<byte[]> ReadAllAsync(Stream stream, int maxBytes, CancellationToken cancel)
    {
        if (stream == null) return null;
        int cap = maxBytes > 0 ? maxBytes : 1024 * 1024;
        var buffer = new byte[cap];
        int total = 0;
        while (total < cap)
        {
            int n = await stream.ReadAsync(buffer, total, cap - total, cancel).ConfigureAwait(false);
            if (n <= 0) break;
            total += n;
        }
        if (total == buffer.Length) return buffer;
        var trimmed = new byte[total];
        Buffer.BlockCopy(buffer, 0, trimmed, 0, total);
        return trimmed;
    }

    /// <summary>
    /// 读续传状态。任何不一致（总长变了、资源标识变了、分片表不连续）都当没有续传并清掉旧状态。
    /// </summary>
    private async Task<ResumeState> LoadResumeAsync(long totalBytes, string validatorValue, CancellationToken cancel)    {
        IResumeStore store = Options.ResumeStore;
        if (store == null || string.IsNullOrEmpty(_resumeKey) || totalBytes <= 0)
        {
            return null;
        }

        ResumeState state;
        try
        {
            state = await store.LoadAsync(_resumeKey, cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }

        if (state == null)
        {
            return null;
        }

        // 🚨 2026-11-01（方案 C）：不再要求「严格连续」。
        //    只要分片表**完整覆盖**整个文件（首片从 0 开始、末片覆盖到末尾），
        //    中间的洞就补成「待下分片」继续用 —— 这样用户「想怎么续传就怎么续传」，
        //    不会再因为一处 512KB 的空洞丢掉整份进度。
        //    洞里的字节由补出来的分片负责下载，所以不会产出「长度对、内容坏」的文件。
        if (!state.IsUsable())
        {
            if (state.CoversAll())
            {
                int filled = state.FillGapsWithPendingChunks();
                if (filled > 0)
                {
                    _logger?.LogDebug(string.Format(
                        "续传表有 {0} 处空洞，已补成待下分片继续续传（不会从零重下）", filled));
                }
            }
            else
            {
                // 首尾都没盖住 → 状态真的坏了，整份作废
                return null;
            }
        }

        if (!state.IsUsable() && !state.CoversAll())
        {
            return null;
        }

        if (state.TotalBytes != totalBytes)
        {
            _logger?.LogWarning(string.Format(
                "续传状态的长度（{0}）与本次探测（{1}）不一致 → 这不是同一个文件，丢弃旧续传状态",
                state.TotalBytes, totalBytes));
            await ClearResumeAsync().ConfigureAwait(false);
            return null;
        }

        // 🚨🚨 2026-11-04 修复「同名不同文件串进度」（用户实测 UC 续传 403）：
        //   原来的判据要求 **两边都非空** 才比对：
        //     if (!string.IsNullOrEmpty(validatorValue) && !string.IsNullOrEmpty(state.Validator) && ...)
        //   于是只要本次探测**没拿到** validator（CDN 不回 ETag / 走 OpenSSL 桥时响应头缺失 /
        //   探测超时返回空串），校验就被整个跳过 —— 旧状态被**无条件采信**。
        //   真实事故：用户先下了夸克的「示例视频」（15,340,287 字节），
        //   之后从 UC 网盘下**同名**的另一个文件（186,678,707 字节），
        //   UniquePath 把它落成「示例视频 (1)」，而 .geziresume 是按路径定位的 ——
        //   引擎读到了旧状态，长度虽然对不上会被上面那条挡住，
        //   但只要长度恰好相同（同名不同内容），就会拿旧进度续新文件 → 拼出坏文件。
        //   ⇒ 现在的规则（更严格，宁可重下也不出错）：
        //     · 本次探测**有** validator → 必须与记录**完全一致**，否则作废；
        //     · 本次探测**没有** validator，但记录**有** → 无法证明是同一个文件 → 作废；
        //     · 两边都没有 → 只能靠 TotalBytes 判（上面已判过），放行。
        bool hasNow = !string.IsNullOrEmpty(validatorValue);
        bool hasOld = !string.IsNullOrEmpty(state.Validator);
        if (hasNow && hasOld
            && !string.Equals(state.Validator, validatorValue, StringComparison.Ordinal))
        {
            _logger?.LogWarning("资源标识已变化（" + state.Validator + " → " + validatorValue
                + "），丢弃旧续传状态");
            await ClearResumeAsync().ConfigureAwait(false);
            return null;
        }
        if (!hasNow && hasOld)
        {
            // 本次拿不到资源标识 = 无法证明"还是那个文件"。
            // 旧版这里是静默放行，结果同名不同文件会串进度。
            _logger?.LogWarning("本次未取到资源标识，无法确认与旧续传状态是否同一文件，丢弃旧状态重下");
            await ClearResumeAsync().ConfigureAwait(false);
            return null;
        }

        // 🚨🚨 2026-11-04 第二道保险：**源文件指纹**（本地算的，必然存在）。
        //   上面那两道靠的是服务端 ETag，而 ETag 可能探测不到（CDN 不回 / 超时 / OpenSSL 桥）。
        //   指纹是 UI 层用「总长 + 网盘序号 + 源文件 Fid」拼的，与网络无关，永远有值。
        //   真实事故：夸克的「示例视频」(15,340,287) 与 UC 的同名文件 (186,678,707)
        //   下到同一目录，UniquePath 把后者落成「示例视频 (1)」，
        //   而 .geziresume 按路径定位 → 长度若恰好相同就会串进度。
        //   空串表示旧版本写的状态（没这个字段）或调用方没提供 → 跳过，向后兼容。
        if (!string.IsNullOrEmpty(_sourceKeyValue)
            && !string.IsNullOrEmpty(state.SourceKey)
            && !string.Equals(state.SourceKey, _sourceKeyValue, StringComparison.Ordinal))
        {
            _logger?.LogWarning(string.Format(
                "源文件指纹不一致（旧 {0} / 新 {1}）→ 这不是同一个源文件，丢弃旧续传状态重下",
                state.SourceKey, _sourceKeyValue));
            await ClearResumeAsync().ConfigureAwait(false);
            return null;
        }

        return state;
    }

    /// <summary>
    /// 用续传状态覆盖分片表。
    ///
    /// 🚨 每片的 <c>Position</c> 要**减去一段安全余量**再向下对齐：进程被强杀的那一瞬间，
    /// 最后一个在飞的写可能「撕裂」（长度涨上去了但尾部是垃圾）。按记录值直接信任，
    /// 合并后就是「长度对、内容坏」，而完成校验只比长度是看不出来的。
    /// 丢掉最多 64KB/片，代价极小，换来的是不会产出坏文件。
    /// </summary>
    /// <summary>
    /// 算「已可信的连续前缀」：从偏移 0 开始，沿续传表往后走，直到第一个还没下完的分片为止。
    ///
    /// 为什么不用文件长度：ApplyResume 会把非完整分片的 Position 退回 64 KB 再对齐 4 KB，
    /// 也就是说「文件里已有的字节」不等于「可以信任的字节」—— 那段要重下覆盖。
    /// 落盘层只有知道真正的可信前缀，才不会把重下的数据当重复数据丢掉。
    /// </summary>
    private static long SafeResumeOffset(ResumeState state)
    {
        if (state == null || state.Chunks == null || state.Chunks.Length == 0)
        {
            return -1;
        }

        long expect = 0;
        foreach (ResumeChunk c in state.Chunks)
        {
            if (c == null)
            {
                return -1;
            }

            if (c.Start != expect)
            {
                // 🚨 2026-11-01（方案 C）：表中间有洞时，**不能**返回 -1。
                //    -1 的含义是「不敢相信任何前缀，让落盘层按文件长度处理」——
                //    而文件长度里可能包含洞里那段没下过的字节（预分配/空洞），
                //    那正是「长度对、内容坏」的来源。
                //    正解：返回**洞之前那段连续可信前缀**的长度（expect）。
                //    落盘层从 expect 往后重新刷，洞里的字节由补出来的分片重新下载。
                return expect;
            }

            if (c.Position < c.Length)
            {
                return c.Start;
            }

            expect = c.End + 1;
        }

        // 全部分片都已完成：整个文件都可信（收尾的长度校验会兜底）
        return expect;
    }

    private void ApplyResume(DownloadPackage package, ResumeState state)
    {
        const long SafetyMargin = 64L * 1024;
        const long Align = 4L * 1024;

        var chunks = new Chunk[state.Chunks.Length];
        bool firstIncomplete = true;
        for (int i = 0; i < state.Chunks.Length; i++)
        {
            ResumeChunk rc = state.Chunks[i];
            long length = rc.End - rc.Start + 1;

            long position = rc.Position;
            if (position < 0)
            {
                position = 0;
            }

            if (position >= length)
            {
                position = length;
            }
            else if (firstIncomplete)
            {
                // ⚠️ 第一个未完成片必须整片重下（Position 归 0），不能只退 64 KB。
                // 落盘层拿到的「可信前缀」是这一片的 Start（见 SafeResumeOffset），
                // 它会等一个恰好落在 Start 上的数据块才开始刷盘。若这里留了非零 Position，
                // Start..Start+Position 这段永远没人写，_flushed 就再也推不动 ——
                // 现象是「worker 全部下完、报下载完成，但文件长度一动不动」。
                position = 0;
                firstIncomplete = false;
            }
            else if (position > 0)
            {
                position -= SafetyMargin;
                if (position < 0)
                {
                    position = 0;
                }

                position -= position % Align;
            }

            Chunk chunk = StaticChunkPlanner.NewChunk(i.ToString("D4"), rc.Start, rc.End, Options);
            chunk.Position = position;
            chunks[i] = chunk;
        }

        package.Chunks = chunks;
        package.Validate();
    }

    private void StartResumeSaver()
    {
        if (string.IsNullOrEmpty(_resumeKey))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _resumeSaveCts = cts;
        CancellationToken token = cts.Token;

        _resumeSaveTask = Task.Run(
            async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(Options.ResumeSaveIntervalMs, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                        return;
                    }

                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    await SaveResumeSnapshotAsync(token).ConfigureAwait(false);
                }
            });
    }

    /// <summary>
    /// 收尾阶段的「延迟显示」计时器（2026-10-25）。
    ///
    /// <para>
    /// 用户的诉求是「大厂根本没有『正在写入磁盘』这一步，或者快到看不见」。
    /// 正常情况刷盘只要几十毫秒，那么短的时间不该在界面上闪一下文字。
    /// 所以这里不立刻上报，而是等 PhaseShowDelayMs 毫秒后
    /// 如果这一步还没结束，才真正显示出来 —— 快就完全看不见，真卡住才看得见。
    /// </para>
    /// </summary>
    private CancellationTokenSource _phaseDelayCts;

    private const int PhaseShowDelayMs = 400;

    private void SchedulePhase(string phase)
    {
        CancelScheduledPhase();
        var cts = new CancellationTokenSource();
        _phaseDelayCts = cts;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PhaseShowDelayMs, cts.Token).ConfigureAwait(false);

                // 🚨 竞态防护（2026-10-25 修的真 bug）。
                // CancelScheduledPhase() 会把 _phaseDelayCts 置空，但线程池调度有延迟：
                // 可能出现「主流程已经走到 Package.Phase = null、甚至已经开始下一步了，
                // 这个延迟任务才刚醒过来」的情况。此时若无条件 RaisePhaseProgress，
                // 就会把已经清掉的 Phase 重新写回去，而后面再没有任何代码清它 ——
                // 界面就永久停在「正在校验文件完整性…」，用户看到的是「永远不显示已完成」。
                // 用引用相等判定：只有自己仍是当前计时器时才允许上报。
                if (!ReferenceEquals(_phaseDelayCts, cts))
                {
                    return;
                }
                RaisePhaseProgress(phase);
            }
            catch (OperationCanceledException)
            {
                // 阶段在 400ms 内结束 —— 正常情况，什么都不显示
            }
            catch
            {
                // 上报失败不能影响下载主流程
            }
        });
    }

    private void CancelScheduledPhase()
    {
        var cts = _phaseDelayCts;
        _phaseDelayCts = null;
        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
        }
    }

    private async Task StopResumeSaverAsync()
    {
        CancellationTokenSource cts = _resumeSaveCts;
        _resumeSaveCts = null;
        Task task = _resumeSaveTask;
        _resumeSaveTask = null;

        if (cts != null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // 已释放
            }
        }

        if (task != null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // 保存器退出时的异常不影响下载结果
            }
        }

        if (cts != null)
        {
            try
            {
                cts.Dispose();
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>
    /// 把当前分片进度落盘。分片表必须连续覆盖整个文件才保存 —— 动态抢占会把原片切小、
    /// 新片插进中间，所以要先按 Start 排序再校验；不连续就宁可不存，也不能存一份坏表。
    /// </summary>
    private async Task SaveResumeSnapshotAsync(CancellationToken cancel)
    {
        // 🚨 2026-11-07：用户「删除并移除」后，周期保存器 / 收尾补写都不许再落盘，
        //    否则与删除线程抢同一个 .geziresume，文件被反复写回来。
        try
        {
            var probe = Options.IsDeleteRemoveRequested;
            if (probe != null && probe()) return;
        }
        catch { /* 回调异常不影响正常下载路径 */ }

        if (string.IsNullOrEmpty(_resumeKey))
        {
            return;
        }

        Chunk[] chunks = Package?.Chunks;
        if (chunks == null || chunks.Length == 0 || Package.TotalFileSize <= 0)
        {
            return;
        }

        // 必须把抢占切出来的新片一起算进去：原片的 End 已被改小，后半段只存在于 _splitChunks，
        // 漏了它就会在分片表里留下空洞，IsUsable() 判不连续后整份续传被丢弃。
        // 🚨 2026-11-01（方案 E）：保存期间暂停切分。
        //    锁已经保证原子性，但保存要遍历全表 + FlushAsync + 裁剪，可能几十毫秒；
        //    这段时间让切分线程直接跳过，比让它们阻塞在锁上更快、更安全。
        //    ⚠️ 必须在 try/finally 里复位，否则保存中途抛异常会让切分永久停摆。
        _chunkSplitPaused = true;
        try
        {

        // 🚨 2026-11-01（方案 A）：读分片表必须与「切分」互斥。
        //    否则可能读到「原片已改短、新片还没登记」的中间态 —— 那正好是一个空洞，
        //    会让整份续传被 IsUsable() 判废（实测每次都恰好缺 512KiB）。
        Chunk[] sorted;
        lock (_chunkTableLock)
        {
            var all = new List<Chunk>(chunks.Length + _splitChunks.Count);
            all.AddRange(chunks.Where(c => c != null));
            foreach (var extra in _splitChunks.Values)
            {
                if (extra != null)
                {
                    all.Add(extra);
                }
            }

            sorted = all.OrderBy(c => c.Start).ToArray();
        }
        if (sorted.Length == 0)
        {
            return;
        }

        var state = new ResumeState
        {
            TotalBytes = Package.TotalFileSize,
            Validator = _validatorValue,
            // 2026-11-04：源文件指纹（本地算的，不依赖服务端响应头）。
            // 见 ResumeState.SourceKey 的注释：用于识别「同一路径换了另一个源文件」。
            SourceKey = _sourceKeyValue,
            SavedAtUtc = DateTime.UtcNow,
            Chunks = new ResumeChunk[sorted.Length],
        };

        // 顺序落盘时 Chunk.Position 只是「已接收字节数」，而文件里真正落地的只有开头连续的一段
        // （窗口里还在排队的块尚未写出）。若直接按 Position 记录续传点，下次就会从「文件其实还是空」
        // 的位置往后接，产出一个长度对、内容坏的中间文件。所以要用落盘层报告的连续刷盘偏移裁剪。
        //
        // 先把窗口里「能连续写出的部分」尽量刷掉：FlushAsync 只刷起点正好接上的块，遇到空洞立即返回，
        // 不会死等。不刷的话取消时续传点会被裁到接近 0，等于白下。
        IChunkStorage storage = Package?.Storage;
        if (storage != null)
        {
            try
            {
                await storage.FlushAsync().ConfigureAwait(false);
            }
            catch
            {
                // 刷盘失败不影响「记录一个保守的续传点」
            }
        }

        long rangeLow = Options.RangeLow;
        long flushed = -1;
        if (storage != null && string.Equals(storage.Name, "sequential", StringComparison.Ordinal))
        {
            flushed = storage.Length;
        }

        for (int i = 0; i < sorted.Length; i++)
        {
            Chunk c = sorted[i];
            long length = c.End - c.Start + 1;
            long position = Interlocked.Read(ref c.PositionRaw);

            if (position < 0)
            {
                position = 0;
            }

            if (flushed >= 0)
            {
                // 文件偏移 = Start + Position - RangeLow，只有落在连续刷盘范围内的部分才算数
                long allowed = flushed + rangeLow - c.Start;
                if (allowed < 0)
                {
                    allowed = 0;
                }

                if (position > allowed)
                {
                    position = allowed;
                }
            }

            if (position > length)
            {
                position = length;
            }

            state.Chunks[i] = new ResumeChunk { Start = c.Start, End = c.End, Position = position };
        }

        // 🚨 2026-11-01（方案 B）：先尝试**吃掉小空洞**再判可用性。
        //    原来是一处空洞就让整份作废（实测那空洞恰好只有 512KiB，
        //    却让 178MB 的续传从零重下）。补洞的代价只是重下那几百 KB。
        //    只有 gap 超过 16MB（说明状态真坏了）才仍然整份作废 —— 那是正确的。
        if (!state.IsUsable())
        {
            int filled = state.NormalizeGaps();
            if (filled > 0)
            {
                _logger?.LogDebug(
                    string.Format("续传状态有 {0} 处小空洞，已并入相邻分片（续传时重下这几段，不影响其余进度）",
                        filled));
            }
        }

        if (!state.IsUsable())
        {
            // 🚨 2026-11-01（方案 C）：补洞后仍不连续时，只要**完整覆盖**就照样存 ——
            //    加载侧会把它补成待下分片继续用（见 LoadResumeAsync）。
            //    真正要丢弃的只有「首尾都没盖住」这种彻底坏掉的状态。
            if (state.CoversAll())
            {
                _logger?.LogDebug("续传表带空洞但完整覆盖，已保存（加载时会补成待下分片）");
            }
            else
            {
                // 首尾没盖住 → 存下去也没用，只会让下次白读一次。
                // 这条日志很关键：没有它，「取消后没有续传文件」看起来就像保存根本没被调用。
                long expect = 0;
                string firstGap = "";
                foreach (var rc in state.Chunks)
                {
                    if (rc.Start != expect)
                    {
                        firstGap = string.Format("首处不连续：期望 Start={0}，实际 {1}（End={2}）", expect, rc.Start, rc.End);
                        break;
                    }

                    expect = rc.End + 1;
                }

                if (firstGap.Length == 0)
                {
                    firstGap = string.Format("末片 End 与总长不符：覆盖到 {0}，总长 {1}", expect, state.TotalBytes);
                }

                _logger?.LogWarning("续传状态不可用，已丢弃：" + firstGap);
                return;
            }
        }

        try
        {
            await Options.ResumeStore.SaveAsync(_resumeKey, state, cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 收尾阶段传 CancellationToken.None，正常不会走到这里
        }
        catch
        {
            // 状态存不下不影响下载本身
        }
        }
        finally
        {
            // 无论成功/失败/异常，都必须恢复切分，否则引擎会永久不再切分。
            _chunkSplitPaused = false;
        }
    }

    private async Task ClearResumeAsync()
    {
        if (string.IsNullOrEmpty(_resumeKey))
        {
            return;
        }

        try
        {
            await Options.ResumeStore.ClearAsync(_resumeKey, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 清不掉只是留个残留文件，不影响结果
        }
    }

    private async Task<VerifyOutcome> VerifyContentAsync()
    {
        IContentValidator validator = Options.Validator;
        if (validator == null || Package.InMemoryStream || string.IsNullOrEmpty(Package.FileName))
        {
            return VerifyOutcome.Skip("未校验（内存流或没有目标路径）");
        }

        // 🚨🚨 2026-11-06：「假完成」的诊断。
        //
        // 实测日志（45.45MB 文件）：进度显示 100%、哈希复核「本地只读到 0/262144 字节」，
        // 却因为「按服务器声明的长度下完 + 抽样拿不到远端样本」被判「文件可用」。
        // 根因是 ReceivedBytesSize / VerifyChunkCoverage 都只看 Chunk.Position
        // 这个**内存计数**，顺序写窗口被队头阻塞时它可以是满的、磁盘文件仍是 0 字节。
        //
        // ⚠️ 这里**只记日志、不改判定**：真正的拦截在 FusionDownloader 完成后的
        //    LinkLengthMismatchException 长度硬校验（它会触发重新取链自愈）。
        //    若在这里直接返回 Fail，会变成 InvalidDataException → 不触发自愈 →
        //    用户看到「下载失败」而不是自动重来，体验更差。
        // 进度显示已由 DownloadPackage.EffectiveReceivedBytes 改用真实刷盘长度，
        // 不会再出现「界面 20MB / 磁盘 0KB」。
        try
        {
            if (Package.TotalFileSize > 0 && System.IO.File.Exists(Package.FileName))
            {
                long realLen = new System.IO.FileInfo(Package.FileName).Length;
                if (realLen != Package.TotalFileSize)
                {
                    _logger?.LogWarning(string.Format(
                        "文件实际长度与总长不符（期望 {0} 字节，实际 {1} 字节），数据尚未全部落盘，"
                        + "完成后会由外层长度校验拦下并重新取链。",
                        Package.TotalFileSize, realLen));
                }
            }
        }
        catch { }

        var expect = new HashExpectation
        {
            Sha256 = Options.ExpectedSha256,
            Sha1 = Options.ExpectedSha1,
            Md5 = Options.ExpectedMd5,
        };

        // 🚨 2026-10-28 补的兜底（原实现这里直接返回 Skip，等于「没哈希就什么都不查」）。
        //
        // 背景：共写模式会**预分配成满尺寸**（`FusionDownloader` 硬编码
        // ReserveStorageSpaceBeforeStartingDownload = true → ConcurrentStream 里 SetLength(总长)）。
        // 于是目标文件长度**一开始就等于总大小**，「文件长度对不对」这条判据彻底失效。
        // 而四个网盘的直链大多不返回 md5/sha，HashExpectation 全空 →
        // validator 返回 Skip → 下载残缺也照样报「下载完成」。
        //
        // 所以这里用**分片表**当判据：所有分片都写满、且拼起来正好覆盖 [0, 总长)，
        // 才算真的下完。分片表的 Position 是内存里的真实计数，与预分配无关，骗不了。
        if (expect.Sha256 == null && expect.Sha1 == null && expect.Md5 == null)
        {
            VerifyOutcome coverage = VerifyChunkCoverage();
            if (!coverage.Ok)
            {
                _logger?.LogWarning("分片覆盖校验未通过：" + coverage.Message);
                return coverage;
            }
            _logger?.LogInfo("分片覆盖校验通过：" + coverage.Message);
            // 覆盖校验通过后仍然交给 validator 跑一遍：
            // 它没有哈希时只会返回 Skip，但保留了「将来网盘开始返回哈希」的升级路径。
        }

        try
        {
            return await validator.VerifyAsync(Package.FileName, expect, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return VerifyOutcome.Fail("", "内容校验执行失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 无哈希时的完整性兜底：分片表必须「片片写满 + 首尾相接 + 总长吻合」。
    /// <para>
    /// 为什么必须把 <see cref="_splitChunks"/> 也算上：抢占会把一个慢分片的剩余区间
    /// 从中间切开，原分片的 <c>End</c> 被改小、后半段作为**新的 Chunk 对象**只存在于
    /// <see cref="_splitChunks"/> 里，<c>Package.Chunks</c> 数组看不见它。
    /// 只遍历数组会漏掉所有尾巴分片，把「还在下」误判成「已完成」。
    /// </para>
    /// </summary>
    private VerifyOutcome VerifyChunkCoverage()
    {
        Chunk[] chunks = Package?.Chunks;
        if (chunks == null || chunks.Length == 0)
            return VerifyOutcome.Skip("未校验（没有分片表）");

        var all = new List<Chunk>(chunks.Length + _splitChunks.Count);
        all.AddRange(chunks);
        foreach (var extra in _splitChunks.Values)
        {
            if (extra != null) all.Add(extra);
        }

        // 先逐片看有没有没写满的 —— 这条能直接指出「哪一片还差多少字节」，
        // 比只报「覆盖不连续」更容易定位。
        foreach (Chunk c in all)
        {
            if (!c.IsDownloadCompleted())
            {
                long left = c.Length - Interlocked.Read(ref c.PositionRaw);
                return VerifyOutcome.Fail("",
                    string.Format("文件没有下载完整：分片 [{0}, {1}] 还差 {2} 字节。请重新下载或续传。",
                        c.Start, c.End, left));
            }
        }

        // 再验「首尾相接 + 总长吻合」：分片全写满但互相重叠/有空洞时，拼出来的文件是错的。
        Chunk[] sorted = all.ToArray();
        Array.Sort(sorted, (a, b) => a.Start.CompareTo(b.Start));

        long cursor = Options.RangeLow;
        foreach (Chunk c in sorted)
        {
            if (c.Start != cursor)
            {
                return VerifyOutcome.Fail("",
                    string.Format("文件内容不连续：期望从偏移 {0} 开始，实际分片从 {1} 开始。请重新下载。",
                        cursor, c.Start));
            }
            cursor = c.End + 1;
        }

        long expectEnd = Options.RangeLow + Package.TotalFileSize;
        if (cursor != expectEnd)
        {
            return VerifyOutcome.Fail("",
                string.Format("文件长度不符：分片只覆盖到 {0}，应为 {1}。请重新下载。", cursor, expectEnd));
        }

        return VerifyOutcome.Skip(string.Format(
            "未校验哈希（网盘接口没给），已按 {0} 个分片核对覆盖范围完整（共 {1} 字节）",
            sorted.Length, Package.TotalFileSize));
    }

    private void ValidateBeforeChunking()
    {
        CheckSingleChunkDownload();
        CheckSupportDownloadInRange();
        SetRangedSizes();
        CheckSizes();
    }

    private void SetRangedSizes()
    {
        if (Options.RangeDownload)
        {
            if (!Package.IsSupportDownloadInRange)
            {
                throw new NotSupportedException("The server of your desired address does not support download in a specific range");
            }

            if (Options.RangeHigh < Options.RangeLow)
            {
                // 高低给反了 → 交换回来。
                // 原实现写的是 `RangeLow = RangeHigh - 1`：用户想下 [100, 50]（51 字节），
                // 结果被改成 [49, 50]（2 字节），静默只下 2 个字节还报成功。
                long swap = Options.RangeLow;
                Options.RangeLow = Options.RangeHigh;
                Options.RangeHigh = swap;
            }

            if (Options.RangeLow < 0)
            {
                Options.RangeLow = 0;
            }

            if (Options.RangeHigh < 0)
            {
                Options.RangeHigh = Options.RangeLow;
            }

            if (Package.TotalFileSize > 0)
            {
                Options.RangeHigh = Math.Min(Package.TotalFileSize, Options.RangeHigh);
            }

            Package.TotalFileSize = Options.RangeHigh - Options.RangeLow + 1;
        }
        else
        {
            Options.RangeHigh = Options.RangeLow = 0; // reset range options
        }
    }

    private void CheckSizes()
    {
        if (Options.CheckDiskSizeBeforeDownload && !Package.InMemoryStream)
        {
            FileHelper.ThrowIfNotEnoughSpace(Package.TotalFileSize, Package.FileName);
        }
    }

    private void CheckSingleChunkDownload()
    {
        // 只把「未知/负数大小」归一成 0。
        // 原来是 `<= 1`，把 1 字节的文件也当成 0 字节 —— 那种文件一个字节都不会下，
        // 最后落一个空文件还报成功。
        if (Package.TotalFileSize < 0)
            Package.TotalFileSize = 0;

        if (Package.TotalFileSize <= Options.MinimumSizeOfChunking)
            SetSingleChunkDownload();
    }

    private void CheckSupportDownloadInRange()
    {
        if (Package.IsSupportDownloadInRange == false)
            SetSingleChunkDownload();
    }

    private void SetSingleChunkDownload()
    {
        Options.ChunkCount = 1;
        Options.ParallelCount = 1;
        _parallelSemaphore = new SemaphoreSlim(1, 1);
    }

    private async Task ParallelDownload(PauseToken pauseToken)
    {
        // 未启用抢占、也没开播放优先级时走 bezzad 原生路径（chunk = task，SemaphoreSlim 限并发），
        // 行为完全不变。
        //
        // ⚠️ 播放优先级必须走队列路径：原生路径是「每个 chunk 一个 Task」，
        // 分片在开始前就全部派发出去了，没有队列可重排，SetPriorityWindow 会静默失效。
        //
        // 🚨 2026-11-01 修正：原来还有一条「片数 < 4 就走原生路径」的短路，
        //    它会把**小文件/片数少的任务**整个排除在队列路径之外 ——
        //    而末段补片（TryRefillTail）只在队列路径里存在，
        //    于是这些任务恰恰是"末段只剩 2~3 片、永远用不满线程"的那批，永远救不到。
        //    片数少不该是绕过队列的理由：队列路径对片少同样正确（worker 抢队列而已），
        //    只是没有抢占/补片可做，代价仅是多几个空转 worker（每 50ms 醒一次）。
        //    ⇒ 去掉片数下限，只保留"功能未开启"这一条真正的短路条件。
        if ((!Options.EnableSlowChunkPreemption && !Options.EnablePlaybackPriority)
            || Package.Chunks == null || Package.Chunks.Length == 0)
        {
            var tasks0 = GetChunksTasks(pauseToken);
            var result0 = Task.WhenAll(tasks0);
            await result0.ConfigureAwait(false);

            if (result0.IsFaulted)
            {
                throw result0.Exception;
            }
            return;
        }

        await ParallelDownloadWithPreemption(pauseToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 队列 + worker 池 + 慢连接抢占。
    ///
    /// 与原生模型的区别：原生是「每个 chunk 一个 Task，SemaphoreSlim 限并发」，
    /// 分片边界在开始前就固定死了。这里改成「chunk 进队列、worker 循环领取」，
    /// 于是运行期可以把某个慢分片的剩余区间切一半塞回队列，让空闲 worker 抢走。
    ///
    /// 切分的安全性：
    ///  - <see cref="Chunk.End"/> 在 x64 上是对齐的 8 字节字段，读写原子；
    ///  - 原分片把 End 改小后，<c>ReadStream</c> 的循环条件 <c>CanWrite</c>
    ///    （Start + Position &lt; End）自然为 false 并退出，
    ///    <c>EmptyLength</c> 也会把多读的字节夹成 0 丢弃；
    ///  - 新分片的区间是 [切点, 原 End]，与原分片已写的部分**不重叠**，
    ///    写盘位置 <c>Start + Position</c> 各自独立，不会互相覆盖。
    /// </summary>
    private async Task ParallelDownloadWithPreemption(PauseToken pauseToken)
    {
        var queue = new ChunkQueue();
        foreach (var chunk in Package.Chunks)
            queue.Enqueue(chunk);

        // 播放优先级窗口可能在下单前就已经设好（用户点了「边下边看」再开始下载），
        // 这里先按当前窗口重排一次，让窗口内的分片一开始就排在高优先级队列。
        ApplyPriorityWindowToQueue(queue);

        // 活跃分片（正在下载的）：抢占监控要看它们的进度
        var active = new ConcurrentDictionary<string, Chunk>();

        // 🚨 2026-11-01（用户反馈「末段严重掉速、只能用极少线程」的**直接根因之一**）：
        //
        // 原来这里把 worker 数夹到 `Package.Chunks.Length` —— 本意是"片数比连接少时，
        // 多开 worker 也是空转"。但配合**末段补片**就变成了死锁式塌缩：
        //   · 剩余片数 = 3、workerCount 也被夹成 3 → 最多 3 条连接；
        //   · 补片能把 3 片切成 16 片，但 worker 只有 3 个，多出来的片没人领；
        //   · 于是补片白做，速度永远 = 3 × 单连接速度。
        // 迅雷尤其明显（并发封顶 16，本来就少，再被夹到个位数就彻底爬不动）。
        //
        // 正解：worker 数按 **Options.ParallelCount** 开满，只受 MaxRealConcurrency 限制；
        // 片数不够时 worker 会自然阻塞在 queue.TryTake 上（50ms 轮询），
        // 一旦补片切出新片就立刻能领到活 —— 这正是 refs/YunX「线程按需创建」的同一思路。
        // 空转成本极低：每个空闲 worker 每 50ms 醒一次，几百个也只是几微秒。
        int workerCount = Options.ParallelCount;
        if (workerCount < 1) workerCount = 1;
        if (workerCount > ChunkSplitter.MaxRealConcurrency)
            workerCount = ChunkSplitter.MaxRealConcurrency;

        var cancel = _globalCancellationTokenSource.Token;
        var stopMonitor = new CancellationTokenSource();
        Exception firstError = null;
        var errorLock = new object();

        // ---- 抢占监控 ----
        var monitor = Task.Run(async () =>
        {
            var lastPos = new Dictionary<string, long>();
            var lastTick = new Dictionary<string, int>();

            while (!stopMonitor.IsCancellationRequested && !cancel.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(Options.PreemptIntervalMs, stopMonitor.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                try
                {
                    // 抢占切出来的新分片先进普通队列，这里按当前播放窗口重新提升一次。
                    // 播放窗口是运行期可变的（用户可能拖动进度），所以每轮都看一次，
                    // 成本只有一次 List 扫描，不含 I/O。
                    ApplyPriorityWindowToQueue(queue);

                    // ---- 2026-11-07：动态慢阈值改由 PreemptRound 用**中位数**更新 ----
                    // 这里原来按「总速度 ÷ 在飞连接数」算平均值，有两个致命缺陷：
                    //   ① 平均值会被少数快连接拉高。用户日志实测「170/254 路低于 12KB/s，
                    //      但平均单连接 33.0 KB/s」—— 平均值 33 时判慢线 16.5，
                    //      那 170 路全部落在判慢线以下，于是全部重连；
                    //   ② `_liveWorkers` 是「领过活的 worker 数」，不是「正在跑连接的数」，
                    //      空闲 worker 也会被算进分母，平均值被稀释得更低。
                    // 中位数没有这两个毛病，且已经由 PreemptRound 里的逐片采样算好了。

                    PreemptRound(active, queue, lastPos, lastTick, cancel);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger?.LogDebug($"慢片抢占本轮失败：{ex.Message}");
                }
            }
        }, stopMonitor.Token);

        // ---- 慢启动闸门（2026-11-05，对齐 refs/gopeed 的 slowStartController）----
        //
        // 🚨🚨 为什么需要：worker 池是一次性 Task.Run 开满的，于是下载开始那一瞬间
        //   会同时发生 256 次 DNS 解析 + 256 次 TCP 握手 + 256 次 TLS 握手
        //   + 256 个 HTTP Range 请求。任何一个环节抖动（DNS 超时、CDN 判异常流量回 503），
        //   都会让这一大批连接**同时**失败、同时退避、同时重连 —— 吞吐周期性塌陷。
        //   分批放行（1 → 2 → 4 → 16 → 256）能把「同时失败」变成「局部失败」，
        //   而且 4 步就爬满，用户感知不到启动变慢。
        SlowStartGate slowGate = Options.EnableSlowStart ? new SlowStartGate(workerCount) : null;
        // 已真正领到过活的 worker 数（慢启动推进判据，见下方驱动器）。
        int slowStartStarted = 0;

        // 🚨 小文件豁免：片数本来就少（≤ 8 片）时，慢启动只会让「本该 8 路并行」
        // 退化成「1 路慢慢爬」—— 爬坡的收益（防握手风暴）远小于损失（串行下载）。
        // 判据用「片数 ≤ 8」而不是「文件大小」：分片规划器对小文件本来就会切得很少，
        // 片数才是决定「能开几路并行」的那个量。
        if (slowGate != null && Package.Chunks.Length <= 8)
        {
            slowGate.OpenAll();
        }
        var slowStartClock = slowGate != null ? System.Diagnostics.Stopwatch.StartNew() : null;
        Task slowStartDriver = null;
        if (slowGate != null)
        {
            slowStartDriver = Task.Run(async () =>
            {
                try
                {
                    while (!slowGate.Done && !stopMonitor.IsCancellationRequested
                           && !cancel.IsCancellationRequested)
                    {
                        await Task.Delay(150, stopMonitor.Token).ConfigureAwait(false);

                        // 放行下一批的判据：**当前已放行的车道都已经被 worker 占满**
                        // （_liveWorkers ≥ 已放行数），说明这一批连接已经真的在传数据了。
                        // 只看「有没有开出来」不看「快不快」——
                        // 慢启动的职责是防止握手风暴，不是判断链路质量。
                        // 🚨 必须先判「全部下完」：否则 worker 会在闸门前干等，
                        //    Task.WhenAll 永远不返回 → stopMonitor 不被取消 → 闸门永远不放行，
                        //    形成死锁（片数少于并发数时必然踩到）。
                        if (AllChunksDone())
                        {
                            slowGate.OpenAll();
                            break;
                        }

                        // 判据用「已经真正领到过活的 worker 数」而不是「此刻在飞数」：
                        //   在飞数会随着单个 worker 下载完一片而回到 0，
                        //   若只放在飞数，1 路放行时 worker 下完一片就再也推不动闸门，
                        //   只能干等 10 秒硬超时 —— 等于把慢启动变成 10 秒卡顿。
                        int started = Volatile.Read(ref slowStartStarted);
                        bool healthy = started >= slowGate.Allowed;
                        // 硬超时兜底：CDN 卡住不返回数据时不能无限等，否则下载被慢启动自己卡死。
                        bool timedOut = slowStartClock.ElapsedMilliseconds >= Options.SlowStartMaxWaitMs;
                        if (healthy || timedOut)
                            slowGate.Advance();
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _logger?.LogDebug("慢启动推进失败: " + ex.Message); }
                finally
                {
                    // 无论怎么退出，都必须放开全部车道 ——
                    // 否则一旦推进任务异常，剩下的 worker 会永远卡在闸门前。
                    slowGate.OpenAll();
                }
            });
        }

        // ---- worker 池 ----
        // 抢占路径不走信号量，活跃数只能自己数（见 LiveActiveChunks 的注释）。
        Volatile.Write(ref _liveWorkers, 0);
        var workers = new Task[workerCount];
        for (int w = 0; w < workerCount; w++)
        {
            int workerIndex = w;   // 闭包捕获：不能直接捕获循环变量
            workers[w] = Task.Run(async () =>
            {
                // 慢启动：轮到自己之前先等一等（每 30ms 看一次闸门，成本可忽略）。
                // ⚠️ 这个等待必须**在 while 之外**：worker 只等一次，
                //    一旦放行就一直跑到下载结束，不会被反复挡回去。
                while (slowGate != null && !slowGate.IsAllowed(workerIndex))
                {
                    if (cancel.IsCancellationRequested)
                        return;
                    try { await Task.Delay(30, cancel).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }

                // 🚨🚨 2026-11-07 新增：放行后**错峰建连**（对齐 refs/YunX 的
                //   `min(i, 8) × 25ms`，见 `DownloadManager.kt` 的 STAGGER_CAP / STAGGER_MS）。
                //
                // 问题：慢启动闸门是「一批一批放行」——1 → 2 → 4 → 16 → 256。
                //   最后一跳一次放行 240 个 worker，它们**同时**发起 TCP + TLS 握手。
                //   代价有两块：
                //     · TLS 握手是 CPU 密集的（RSA/ECDHE），240 个同时做会把 CPU 打满，
                //       而这段时间没有一个字节在下载；
                //     · 绝大部分家用宽带/家宽路由的 NAT 表与 PPS 都吃不下瞬时 240 个新建，
                //       表现为「前面速度慢、要等好几秒才爬起来」。
                //
                // 做法：只对前 8 个 worker 拉开 25ms 间隔（第 9 个起跟第 8 个一起走），
                //   总延迟最多 175ms —— 相对于「爬满 512 路要 3~5 秒」可以忽略，
                //   但足以把瞬时握手的山峰削平。
                //   ⚠️ 上限 8 是照抄 YunX 的实测值：超过 8 之后削峰收益递减，
                //      再拉长只会拖慢起步。
                {
                    const int staggerCap = 8;
                    const int staggerMs = 25;
                    int slots = workerIndex < staggerCap ? workerIndex : staggerCap - 1;
                    if (slots > 0)
                    {
                        if (cancel.IsCancellationRequested)
                            return;
                        try { await Task.Delay(slots * staggerMs, cancel).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                    }
                }

                bool tookFirstChunk = false;
                // 连续空转轮数（2026-11-08）：用于空闲 worker 指数退避，见下面的 Task.Delay 分支。
                int idleRounds = 0;

                while (!cancel.IsCancellationRequested)
                {
                    // 🚨 2026-11-08：连接数爬坡的「刹车」。
                    //   爬山控制器（ConnectionClimber）只允许 ≤ _climbCap 条连接同时在飞，
                    //   这里让超额的 worker 在取片前等一等 —— 效果等于降低并发，
                    //   但**不打断任何已经在跑的连接**（不重连、不重排分片、续传零影响）。
                    //
                    // ⚠️ 安全阀：只有在「真的已有 _climbCap 条在飞」时才等。
                    //   否则万一 _climbCap 很小而实际连接数上不去（比如全断了），
                    //   所有 worker 都在这儿排队 ⇒ 死锁。用 active.Count 而不是 _liveWorkers
                    //   也是同一个目的：active 才是「真正拿着连接」的那批。
                    // 🚨 2026-11-08 第二次修正：降并发改按 **worker 序号** 放行。
                    //
                    // 之前那版用 `active.Count >= capNow` 做判据，数学上是死代码：
                    //   cap = 可领片数 + 余量 = active + queue + 8 恒大于 active，
                    //   判据永远不成立 ⇒ 一个 worker 都拦不住。
                    //
                    // 现在：只有序号前 _climbCap 的 worker 参与抢片，其余直接睡。
                    //   · 收尾期可领片数只剩几十时 _climbCap 就是几十，
                    //     几千个高序号 worker 全部睡死，不再每秒数万次抢 _tailRefillGate 锁；
                    //   · 低序号 worker 里总有空闲的，照样能走到 TryRefillTail 补片，
                    //     不会把「切大片救收尾」这条路锁死。
                    //   · 不重连、不重排分片、不动续传 —— 已在跑的连接完全不受影响。
                    int capNow = _climbCap;
                    if (capNow > 0 && workerIndex >= capNow)
                    {
                        try { await Task.Delay(200, cancel).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                        continue;
                    }

                    Chunk chunk;
                    if (!queue.TryTake(out chunk))
                    {
                        // 队列空：如果所有分片都下完了就收工，否则等一等（可能有抢占切出来的新片）
                        if (AllChunksDone())
                            break;

                        // 🚨 2026-11-01 新增「末段补片」（对齐 refs/YunX 的尾部收缩，见 TryRefillTail）。
                        //    原来这里只会傻等 50ms 再试 —— 而末段的特征是「队列空、但还剩几个大片」，
                        //    于是大量 worker 空转、速度塌到「剩余片数 × 单连接速度」。
                        //    现在让**第一个发现空闲的 worker** 主动把剩余大片切碎填回队列，
                        //    其余 worker 下一轮就能领到活。
                        if (TryRefillTail(queue, active))
                        {
                            idleRounds = 0;
                            continue;
                        }

                        // 🚨🚨 2026-11-08 新增：空闲 worker **指数退避**。
                        //
                        // 用户实测（443MB / 4096 路）：收尾只剩 1% 时队列里最多几十片，
                        //   其余 4000+ 个 worker 全部空转。原来它们每 50ms 醒一次，
                        //   每次都要抢 `_tailRefillGate` 锁、遍历 active 找最大片 ——
                        //   一秒钟 8 万次无效加锁 + 8 万次字典遍历，纯烧 CPU，
                        //   反而让那几十条真正在下载的连接抢不到 CPU 时间片。
                        //   日志里「3.39 MB/s → 845 KB/s → 414 KB/s」的下坡路，
                        //   有一部分就是这么来的（不是网盘给的更少）。
                        //
                        // 做法：连续空转的轮数越多，睡得越久（50→100→200→400，封顶 500ms）。
                        //   一旦领到活就清零。3000+ 空转 worker 的唤醒次数从 8 万次/秒
                        //   降到 6 千次/秒，两个数量级的下降。
                        //   ⚠️ 封顶 500ms 而不是更长：真有人切出新片时，最坏多等半秒，
                        //      相对「收尾阶段本身就要几秒」可接受。
                        if (idleRounds < 6)
                            idleRounds++;
                        int idleDelayMs = 50 << Math.Min(idleRounds, 3);   // 50/100/200/400
                        if (idleDelayMs > 500)
                            idleDelayMs = 500;

                        try { await Task.Delay(idleDelayMs, cancel).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                        continue;
                    }

                    // 慢启动判据：这个 worker 第一次领到活 = 它已经真的跑起来了
                    //（见 slowStartDriver 的注释）。只记第一次，不按领活次数累加。
                    if (!tookFirstChunk)
                    {
                        tookFirstChunk = true;
                        Interlocked.Increment(ref slowStartStarted);
                    }
                    idleRounds = 0;
                    active[chunk.Id] = chunk;
                    Interlocked.Increment(ref _liveWorkers);
                    try
                    {
                        var request = _requestInstances[0];
                        await DownloadChunkNoSemaphore(chunk, request, pauseToken, _globalCancellationTokenSource)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        lock (errorLock)
                        {
                            if (firstError == null) firstError = ex;
                        }
                        _globalCancellationTokenSource.Cancel(false);
                        break;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _liveWorkers);
                        Chunk ignored;
                        active.TryRemove(chunk.Id, out ignored);
                    }
                }
            }, cancel);
        }

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            stopMonitor.Cancel();
            try { await monitor.ConfigureAwait(false); } catch { }
            if (slowStartDriver != null)
            {
                // 先放开全部车道再等它退出：驱动器退出时也会 OpenAll，
                // 这里提前放开可以避免「驱动器正卡在 Task.Delay 里、worker 还在等闸门」的窗口。
                slowGate.OpenAll();
                try { await slowStartDriver.ConfigureAwait(false); } catch { }
            }
            stopMonitor.Dispose();
        }

        if (firstError != null)
            throw firstError;
    }

    // ================= 播放优先级（边下边看） =================

    /// <summary>
    /// 播放窗口起点（字节偏移）。-1 表示未设置，队列不做任何重排。
    /// 用 Interlocked 读写：UI 线程随时可能设置，worker 线程随时可能读取。
    /// </summary>
    private long _priorityStart = -1;

    /// <summary>播放窗口长度（字节）。0 表示未设置。</summary>
    private long _priorityLength;

    /// <summary>
    /// 设置「播放优先级窗口」：告诉下载器从现在开始优先下 [start, start+length) 这段数据。
    ///
    /// 为什么需要它：MP4 的 moov 由 <see cref="TryFetchMoovFirstAsync"/> 单独处理，
    /// 但 mdat 里的视频帧仍按分片顺序下。用户拖动播放进度后，播放器要读的位置
    /// 可能还在队列很后面，于是播几秒就卡住。把窗口内的分片提到高优先级队列，
    /// 就能让「马上要读的数据」先到。
    ///
    /// 线程安全：任意线程可调用；设置后下一次 worker 取任务 / 监控轮询即生效。
    /// 传 <paramref name="length"/> = 0 表示取消优先级（恢复纯 FIFO）。
    /// </summary>
    public void SetPriorityWindow(long start, long length)
    {
        if (start < 0 || length <= 0)
        {
            Interlocked.Exchange(ref _priorityStart, -1L);
            Interlocked.Exchange(ref _priorityLength, 0L);
            return;
        }
        Interlocked.Exchange(ref _priorityLength, length);
        // 先写长度再写起点：读取方先看到起点非 -1 时，长度一定已经是新的。
        Interlocked.Exchange(ref _priorityStart, start);
        _logger?.LogDebug($"已设置播放优先区间：[{start}, {start + length})");
    }

    /// <summary>当前播放窗口；未设置时返回 false。</summary>
    public bool TryGetPriorityWindow(out long start, out long end)
    {
        long s0 = Interlocked.Read(ref _priorityStart);
        long l0 = Interlocked.Read(ref _priorityLength);
        if (s0 < 0 || l0 <= 0)
        {
            start = 0;
            end = -1;
            return false;
        }
        start = s0;
        end = s0 + l0 - 1;
        return true;
    }

    /// <summary>
    /// 按当前播放窗口重排队列。窗口未设置时什么都不做。
    ///
    /// ⚠️ 顺序写模式下必须把窗口起点夹到「已连续刷盘位置」之后：
    /// <see cref="Storage.SequentialFileStorage"/> 只在连续前缀上推进，
    /// 如果优先下载了 40% 位置而 0~40% 还有空洞，这些数据只能堆在 32MB 内存窗口里，
    /// 窗口一满就背压，反而把整条流水线堵死。
    /// 所以顺序写时窗口起点 = max(播放位置, 已刷盘偏移)；
    /// 随机写（Shared）没有连续前缀概念，窗口起点原样使用。
    /// </summary>
    private void ApplyPriorityWindowToQueue(ChunkQueue queue)
    {
        bool sequential = Package?.Storage != null
            && Options.StorageMode == Storage.StorageMode.Sequential;

        long start, end;
        bool hasWindow = TryGetPriorityWindow(out start, out end);

        if (sequential)
        {
            // 🚨🚨 2026-11-05：顺序写「边下边看」+ 消除队头卡住的关键改动。
            //
            // 顺序写只在「连续前缀」上推进 _flushed：第 0 片那块数据没到，
            // 后面 200 条快连接下的数据全堆在内存窗口里，窗口一满就全员背压，
            // 表现为「下了几兆突然卡着不动」。这是队头阻塞（head-of-line blocking），
            // 不是磁盘慢，也不是网络慢。
            //
            // 解法：不管用户有没有点「边下边看」，都默认把**紧接已刷盘偏移的头部区间**
            // 提到队列最前，让 _flushed 持续推进。这样：
            //   ① 窗口不再被后面的乱序数据塞满 → 卡住消失；
            //   ② 文件开头几 MB 很快落盘，配合已有的 moov 优先（EnsureLength +
            //      WriteBypass 把索引写到文件尾）→ 顺序写也能边下边看；
            //   ③ 用户显式拖了播放进度时，仍优先尊重用户窗口（取两者较靠前的起点）。
            //
            // ⚠️ 这段**只在 Sequential 分支**执行，共写（Shared）走下面的原逻辑，
            //    一个字节都不改，避免影响已经实测达标的主路径。
            long flushed = Package.Storage.Length;
            long headEnd = Math.Min(Package.TotalFileSize,
                flushed + SequentialHeadPriorityBytes) - 1;

            if (hasWindow)
            {
                // 用户在播放器里拖了进度：以用户窗口为准，但起点不能退到
                // 已刷盘之前（那部分已经写完了，提了也没用）。
                if (start < flushed) start = flushed;
                if (start > end) return;
            }
            else
            {
                start = flushed;
                end = headEnd;
            }

            if (end < start) return;
            int moved = queue.Reorder(start, end);
            if (moved > 0)
                _logger?.LogDebug($"顺序写头部优先 [{start}, {end}]：提前 {moved} 片");
            return;
        }

        if (!hasWindow)
            return;

        if (Package != null && Package.TotalFileSize > 0 && start >= Package.TotalFileSize)
        {
            // 随机写 + 预分配：文件长度已经是总长，调用方按「当前文件长度」算出来的
            // 起点会落在文件末尾之外，窗口里一个待下载分片都没有。
            // 这时退化为从头开始 —— 播放器一般从头播，保证开头连续可读才有意义。
            start = 0;
        }

        int movedShared = queue.Reorder(start, end);
        if (movedShared > 0)
            _logger?.LogDebug($"播放优先区间 [{start}, {end}]：提前 {movedShared} 片");
    }

    /// <summary>是否所有分片都已下完。</summary>
    private bool AllChunksDone()
    {
        foreach (var c in Package.Chunks)
        {
            if (!c.IsDownloadCompleted())
                return false;
        }

        // 🚨🚨 2026-11-07 修「前快后慢 / 卡在 99% 却永远等下去」的一个分支：
        //   抢占与末段补片会把一个分片从中间切开 —— 原片 End 被改小、后半段作为新片
        //   登记进 _splitChunks。此时原片的 IsDownloadCompleted() 立刻变 true，
        //   若只看 Package.Chunks，就会在尾巴片还没下完时判定「全部完成」，
        //   让所有 worker 退出，尾巴片留在 _splitChunks 里没人管。
        //   （数据最终会被 VerifyChunkCoverage 拦下报失败 —— 等于白下。）
        //   ⇒ 必须把 _splitChunks 一起算上。
        foreach (var kv in _splitChunks)
        {
            Chunk extra = kv.Value;
            if (extra != null && !extra.IsDownloadCompleted())
                return false;
        }

        return true;
    }

    /// <summary>
    /// 一轮抢占：找出进度明显落后的分片，把它剩余区间从中间切开，
    /// 后半段作为新分片塞回队列。
    ///
    /// <para>
    /// 🚨🚨 2026-11-05 新增「整体限速判定」（老引擎 <c>SegmentedDownloader</c> 有，融合引擎迁移时丢了）：
    /// 如果**大多数连接同时低于阈值**，那不是个别慢节点，而是服务端按 IP 的整体配额低。
    /// 此时换连接、切片、重连全都无用 —— 每条新连接照样被压到同一个速度，
    /// 反而把带宽全烧在 TCP 握手和 HTTP 请求头上，吞吐还会周期性塌陷。
    /// </para>
    ///
    /// <para>
    /// 实测依据（用户 2026-11-05 日志）：19 分钟里 151 次慢重连，
    /// 实测速度全部聚集在 10221~12269 B/s（分片 24=10239、52=10234、78=10226），
    /// 而并发 256 × 10KB/s ≈ 2.5 MB/s —— 正好是阿里/UC 的单 IP 配额。
    /// 这种「全员整齐地慢」正是整体限速的典型特征，不是单连接问题。
    /// </para>
    /// </summary>
    private void PreemptRound(ConcurrentDictionary<string, Chunk> active,
        ChunkQueue queue,
        Dictionary<string, long> lastPos, Dictionary<string, int> lastTick,
        CancellationToken cancel)
    {
        if (active.Count == 0)
            return;

        // 🚨🚨 2026-11-08 第二次修正：**收尾期主动降并发放在最前面**（取代已关闭的连接数爬山）。
        //
        // 为什么必须在最前面：下面有若干个早退（样本不足 2、中位数算不出等），
        // 而「可领片数不够」这件事与速度无关 —— 放到中段就会在收尾期被早退跳过，
        // 恰恰是它最该干活的时刻。用户实测日志（GeZi.log）证明：
        //   全日志只有 2 条「连接数爬坡」，两次都直接等于用户设的上限，
        //   收尾期因 inFlight < 64 完全跳过 ⇒ 爬山零收益。
        // 现在改成纯「片数够不够分」的算术，每轮（2 秒）必算，与速度样本无关。
        UpdateEndgameCap(queue, active);

        int now = Environment.TickCount;

        // 1) 采样：算出每个活跃分片本轮的速度（字节/秒）
        var speeds = new List<KeyValuePair<string, double>>();
        foreach (var kv in active)
        {
            Chunk c = kv.Value;
            long pos = Interlocked.Read(ref c.PositionRaw);

            long prevPos;
            int prevTick;
            if (lastPos.TryGetValue(kv.Key, out prevPos) && lastTick.TryGetValue(kv.Key, out prevTick))
            {
                int dt = now - prevTick;
                if (dt > 200)
                {
                    double bps = (pos - prevPos) * 1000.0 / dt;
                    if (bps < 0) bps = 0;
                    speeds.Add(new KeyValuePair<string, double>(kv.Key, bps));
                }
            }
            lastPos[kv.Key] = pos;
            lastTick[kv.Key] = now;
        }

        // 🚨🚨 2026-11-07 修「前面很快、最后特别慢」的最后一个分支：
        //   原来这里是无条件 `if (speeds.Count < 2) return;`，于是收尾期
        //   （只剩 1 路在磨最后几 MB）**整轮抢占直接返回**，谁都不会被换掉。
        //   而收尾恰恰是最需要抢占的时刻：那一路的速度就是用户此刻看到的总速度。
        //
        //   现在只在「非收尾期」要求至少 2 个样本（1 个样本算不出中位数、
        //   也无法判断谁掉队）；收尾期允许 1 个样本，改用**绝对下限**判定 ——
        //   只剩一路时没有同伴可比，只能问「它是不是真的慢」。
        bool endgameNow = active.Count <= Options.PreemptEndgameInflight;
        if (speeds.Count < 2 && !(endgameNow && speeds.Count == 1))
            return;

        // 2) 中位数（每连接）—— **不是平均值**。
        //
        // 🚨🚨 2026-11-07 改（用户日志坐实）：原来用平均值，被少数快连接拉高以后
        //   判慢线跟着抬高，把大批正常连接一起判成慢。实测日志原文：
        //     「170/254 路都低于阈值 12.0 KB/s（本轮实测平均单连接 33.0 KB/s）」
        //   —— 平均值 33 KB/s 时判慢线 = 33×0.5 = 16.5 KB/s，那 170 路（大多 12~14 KB/s）
        //   全部落在线下 → 全部重连 → 握手烧掉带宽 → 下一轮平均值更低 → 更糟。
        //   中位数不受少数极快/极慢连接影响，代表「典型同伴」的速度，
        //   这才是判断「某条连接是否掉队」该用的参照系。
        //
        // ⚠️ 中位数只在样本 ≥ 2 时才有意义（已在上面早退），且这里**不做任何平滑** ——
        //   平滑会让阈值滞后于真实变化，而采样窗口本身就是 2 秒一轮，已经足够稳。
        // 计算逻辑已抽到 GeZi.Core.Support.SlowWatchdog.Median（有自检）。
        var speedValues = new List<double>(speeds.Count);
        foreach (var s in speeds) speedValues.Add(s.Value);
        double median = GeZi.Core.Support.SlowWatchdog.Median(speedValues);
        if (median <= 0)
            return;

        // 把中位数写进动态阈值，供 ChunkDownloader 的看门狗使用。
        // 语义：判慢线 = 中位数 × DynamicSlowRatio（默认 0.5）。
        // 全员 13.5 KB/s → 6.75 KB/s，谁都不算慢；个别掉到 4 KB/s 才被抓。
        Options.DynamicSlowThresholdBps = GeZi.Core.Support.SlowWatchdog.DynamicFloorFromMedian(
            median, Options.DynamicSlowRatio);

        // 3) 慢的判定阈值 = max(绝对下限, 中位数 × 比例)。
        //
        // 🚨 绝对下限 PreemptMinBps(12KB/s) 不是可选项：
        //    收尾阶段只剩一两路时，中位数就是那一两路自己，
        //    「低于自己 × 0.34」永远成立不了，谁都不会被判慢 —— 阈值会自己塌掉。
        //    加了下限后，收尾那几路只要真的慢就会被抓出来换掉。
        //
        // ⚠️ 2026-11-07：这里仍然是「选哪几路去抢」的判据，不是「看门狗判慢」的判据。
        //    看门狗用的是 Options.DynamicSlowThresholdBps（= 中位数 × 0.5，无绝对下限），
        //    两者故意分开：抢占要保守（宁可少抢，抢错会打断正常连接），
        //    看门狗要宽松（全员慢时绝不能全部重连）。
        double threshold = Math.Max((double)Options.PreemptMinBps, median * Options.PreemptSpeedRatio);

        // 4) 整体限速判定（规则见 OverallSlowGate，收尾期自动豁免）
        int slowCount = 0;
        foreach (var s in speeds)
        {
            if (s.Value < threshold) slowCount++;
        }

        bool newlyLogged;
        bool skip = _overallSlow.Evaluate(
            speeds.Count, slowCount, active.Count,
            Options.PreemptEndgameInflight, Options.OverallSlowMinSamples,
            Options.OverallSlowHoldMs,
            DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond,
            out newlyLogged);

        if (newlyLogged)
        {
            // 🚨 2026-11-07：把「目标并发 / 当前在飞」一起打出来（纯日志、零行为）。
            //   用户问「为什么吃不满线程」时，这条能直接区分两种情况：
            //     · 在飞数 ≈ 目标并发，但全员慢 → 服务端按 IP 的整体配额低（正常）；
            //     · 在飞数远小于目标并发 → 客户端侧没开出来，需另查慢启动 / 闸门。
            int targetConn = Options.ParallelCount;
            if (targetConn < 1) targetConn = 1;
            _logger?.LogInfo(string.Format(
                "整体限速中：{0}/{1} 路都低于阈值 {2}/s（本轮实测单连接中位数 {3}/s）；"
                + "目标并发 {4} 路，当前在飞 {5} 路。这属于服务端整体配额低、"
                + "而非个别慢节点，已暂停抢占以免无谓重连。",
                slowCount, speeds.Count, FmtBps(threshold), FmtBps(median),
                targetConn, active.Count));
        }

        if (skip)
        {
            // ⚠️ 抑制期间也要同步标记：ChunkDownloader 会据此**丢掉绝对下限**，
            //    只按「相对同伴」判慢 —— 否则 16KB/s 的绝对线会把全员判慢，
            //    整体限速判定白做（实测日志里 151 次重连全是这么来的）。
            Options.OverallSlowActive = _overallSlow.Active;
            return;
        }

        if (_overallSlow.ClearLogged())
        {
            _logger?.LogInfo("整体限速已缓解，恢复慢连接抢占。");
        }
        Options.OverallSlowActive = false;

        // 3) 挑最慢的分片：**先请求换连接，换不动才切片**
        //
        // 🚨🚨 2026-11-05 改（对齐 refs/YunX 的 preemptSlowChunks）：
        //   原来这里只有「切片」一种手段 —— 把慢片的剩余区间切一半给别的 worker。
        //   但切片的本质是**把同一条慢连接的工作分给更多连接**，
        //   如果慢的根因是「这条 TCP 连接落在慢节点上」，切片治不了本；
        //   而 YunX 的做法是**直接换一条新连接** —— 换连接本身就可能落到快节点。
        //
        //   两者都保留，但顺序反过来：
        //     ① 先请求换连接（成本：一次 TCP+TLS 握手，200~600ms）；
        //     ② 该分片已经换过太多次（SpeedReconnectCount 达上限）→ 才切片。
        //   这样「个别连接掉队」能真正换到快节点，
        //   而「这条 Range 区间本身被限速」时才退化成分片摊薄。
        speeds.Sort((a, b) => a.Value.CompareTo(b.Value));
        int done = 0;
        foreach (var s in speeds)
        {
            if (done >= Options.PreemptMaxPerRound)
                break;
            if (s.Value > threshold)
                break;   // 已按速度升序，后面的都不慢

            Chunk c;
            if (!active.TryGetValue(s.Key, out c))
                continue;

            // ① 优先换连接：只要这个分片还有换连接额度，就交给它自己重连。
            //    worker 在 ReadStream 的每轮循环开头会读到这个标志并主动断流。
            //    注意用 >= 而不是 >：达到上限后不再请求，避免无意义的重连风暴。
            if (c.SpeedReconnectCount < Options.MaxSpeedReconnects)
            {
                c.RequestExternalReconnect();
                done++;
                continue;
            }

            // ② 换连接额度用完 → 退化为切片（把剩余区间摊给别的 worker）。
            //    用保守下限（512KB），避免正常下载期把片切太碎。
            if (!TrySplitChunk(c, queue, cancel, Options.PreemptMinBytes))
                continue;
            done++;
        }
    }

    /// <summary>
    /// 汇总分片进度时，把「抢占切出来的尾巴分片」也计入已下载字节。
    /// </summary>
    /// <remarks>
    /// <see cref="TrySplitChunk"/> 会把原分片的 <c>End</c> 改小、另建一个 tail 分片塞进
    /// <see cref="_splitChunks"/>，而 <c>Package.Chunks</c> 数组里仍是那个被缩短的原对象。
    /// 基类按 <c>Package.Chunks</c> 求和，于是所有尾巴分片的进度**永久漏算** ——
    /// 实测 79.mp4 下完后进度条停在 96.8%（差 2 MB，正好是被切走的部分）。
    /// </remarks>
    protected override void OnChunkDownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
    {
        RefreshExtraReceivedBytes();
        base.OnChunkDownloadProgressChanged(sender, e);
    }

    /// <summary>把 <see cref="_splitChunks"/> 里各尾巴分片的已写字节汇总进 <see cref="DownloadPackage.ExtraReceivedBytes"/>。</summary>
    private void RefreshExtraReceivedBytes()
    {
        if (_splitChunks.Count == 0)
        {
            return;
        }
        long extra = 0;
        foreach (var kv in _splitChunks)
        {
            extra += Interlocked.Read(ref kv.Value.PositionRaw);
        }
        Package.ExtraReceivedBytes = extra;
    }

    /// <summary>
    /// 末段补片：队列已空、但仍有大片没下完时，主动把**最大的剩余分片**再切一刀，
    /// 让空闲 worker 有活可干。返回 true 表示确实切出了新分片。
    ///
    /// <para>
    /// 🚨 为什么必须有它（2026-11-01 实测根因）：
    /// 已有的 <see cref="PreemptRound"/> 只在「某片速度 &lt; 全局平均 × 0.34」时才切分，
    /// 而末段的特征是「**片数少、但速度都正常**」—— 剩下那几片跑得并不慢，
    /// 只是 worker 大量闲置，于是抢占一次都不触发，速度直接塌到
    /// 「剩余片数 × 单连接速度」。再叠加 PreemptMinBytes=512KB（剩余不足 1MB 干脆不切）、
    /// 每轮最多 3 片、2 秒一轮，收尾根本救不回来。
    /// </para>
    /// <para>
    /// 参考 refs/YunX 的 <c>ElasticAllocator</c>（尾部收缩）：
    /// 「剩余字节 ≤ workers × 块大小时，把块压到 remaining / workers，
    /// 让全部连接并行冲完最后一段，彻底消除拖尾塌缩」。
    /// 这里是同一个思路，但**不改原有分片边界**，只做二分切分 ——
    /// 改动面最小，且复用了已经验证过的 <see cref="TrySplitChunk"/> 安全语义
    /// （改小原片 End + 新片入队，写盘区间不重叠）。
    /// </para>
    /// <para>
    /// ⚠️ 并发安全：多个 worker 会同时发现队列空并调到这里，
    /// 所以用 <c>_tailRefillGate</c> 让它们串行，避免同一片被两个线程各切一刀。
    /// </para>
    /// </summary>
    /// <summary>
    /// 2026-11-05 新增：按**实测速度**算末段补片的目标块大小（对齐 refs/YunX 的 ElasticAllocator）。
    ///
    /// <para>
    /// 公式：<c>块 ≈ 单连接 5 秒能下完的量</c>，夹在 [TailRefillMinBytes, 4MB]。
    /// </para>
    ///
    /// <para>
    /// 为什么是 5 秒：YunX 的 <c>TARGET_SECONDS = 5</c> 来自实测 —— 块太小则 Range 请求数爆炸
    /// （每次都要重新建连 + 握手，200~600ms），块太大则「最后一点数据」全压在少数几块上，
    /// 空闲 worker 只能干等。5 秒是「一块刚好能被一条连接连续下完、又不至于拖住收尾」的平衡点。
    /// </para>
    ///
    /// <para>
    /// 返回 0 表示「速度还测不出来」，调用方应保持原值 —— 绝不能把块算成 0 或极小值。
    /// </para>
    /// </summary>
    private long AdaptiveTailSliceBytes()
    {
        try
        {
            // 用「动态慢阈值 / 比例」反推平均单连接速度：
            // DynamicSlowThresholdBps = avgPerConn × DynamicSlowRatio，所以 avgPerConn = 阈值 / 比例。
            // 这样不用再单独维护一份速度采样，避免两处口径漂移。
            double ratio = Options.DynamicSlowRatio;
            long floor = Options.DynamicSlowThresholdBps;
            if (ratio <= 0 || floor <= 0)
                return 0;

            double avgPerConn = floor / ratio;
            if (avgPerConn <= 0)
                return 0;

            // 🚨 2026-11-06：把算式抽到 Core 的纯计算类，算式与常量逐字不变，
            //    目的只是让它能被自检直接断言边界（可验证化），行为零变化。
            return GeZi.Core.Support.DownloadSizing.AdaptiveSliceBytes(avgPerConn, Options.TailRefillMinBytes);
        }
        catch
        {
            return 0;
        }
    }


    private bool TryRefillTail(ChunkQueue queue, ConcurrentDictionary<string, Chunk> active)
    {
        // 队列里已经有活（别的 worker 刚塞进去的），不必再切。
        if (queue.Count > 0)
            return true;

        lock (_tailRefillGate)
        {
            // 二次检查：等锁期间别的 worker 可能已经补过片了。
            if (queue.Count > 0)
                return true;

            // 找「剩余量最大」的活跃分片。只切最大的那一刀，避免一次切太碎。
            Chunk victim = null;
            long bestRemain = 0;
            foreach (var kv in active)
            {
                Chunk c = kv.Value;
                if (c == null)
                    continue;
                long pos = Interlocked.Read(ref c.PositionRaw);
                long remain = c.End - (c.Start + pos) + 1;
                if (remain > bestRemain)
                {
                    bestRemain = remain;
                    victim = c;
                }
            }

            if (victim == null)
                return false;

            // 剩余太小就别切了：切片本身要发一次新的 Range 请求（重新建连 + 握手），
            // 剩余不足 2×TailRefillMinBytes 时切出来的两半都太小，纯亏。
            if (bestRemain < Options.TailRefillMinBytes * 2)
                return false;

            // ⚠️ 只在「确实有空闲 worker」时才补 —— 否则正常并发下载时每轮都切，
            //    会把分片切得极碎，每片都要重新握手，反而更慢（这是 YunX 注释里
            //    「块太小会产生海量 Range 请求 → 拖慢快连接」的同一个坑）。
            int live = Volatile.Read(ref _liveWorkers);
            // 🚨 这里**不能**再拿 Package.Chunks.Length 夹 target（2026-11-01 修）。
            //    原始片数恰恰是"末段片少"的那个数 —— 用它当上限，缺口永远算成 0：
            //    例如 9.5GB/16 并发起始有 128 片，末段只剩 3 片在跑，
            //    若 target 被夹成 3，则 live=3 >= target=3 → 直接 return false，一片都不补。
            //    这正是"末段只用极少线程"的核心成因之一。
            //    上限只受 MaxRealConcurrency 约束（与 workerCount 的算法保持一致）。
            int target = Options.ParallelCount;
            if (target < 1) target = 1;
            if (target > ChunkSplitter.MaxRealConcurrency)
                target = ChunkSplitter.MaxRealConcurrency;
            if (live >= target)
                return false;

            // ★ 关键：**按缺口数切**，而不是每次只切一刀。
            //
            // 一刀一刀切的话，每个 worker 发现队列空都要走一轮「加锁 → 找最大片 → 切一刀」，
            // 而 worker 之间是并发抢的 —— 16 路迅雷要切 15 刀才能填满，每刀都要重新握手，
            // 收尾那几秒全耗在建立连接上。实测这类"逐个补"在末段几乎补不上。
            //
            // 这里一次性把缺口（target - live）补满：从最大的片子开始切，
            // 每切一刀就多一个可领的分片，直到队列里的片数够所有空闲 worker 领。
            // 迅雷并发封顶 16，最多切 15 刀就满；夸克 256 并发时缺口虽大，
            // 但此时剩余数据通常已经不足（下面 minBytes 判断会自然收敛）。
            int deficit = target - live;
            if (deficit <= 0)
                return false;

            // ★★ 核心算法：把**所有活跃分片的剩余总量**按缺口数均分，而不是对单片二分。
            //
            // 为什么不能用二分（2026-11-01 实测踩到）：
            //   末段往往只剩 2~3 个大片在跑（例如 768KB、448KB）。二分一次 → 768 变成
            //   384+384，再切就要 384 < 2×256KB 门槛，切不动了 → 循环 break，
            //   总共只多出 1 个可领分片，14 个空闲 worker 依然没活干。
            //   诊断实测：539 次补片调用，每次都只切出 1 刀，末段连接数始终是 2~4。
            //
            // 正确做法（对齐 refs/YunX 的 ElasticAllocator「尾部收缩」）：
            //   算出剩余总量 remainTotal，均分成 deficit 份，每份 = remainTotal / deficit；
            //   只要每份 ≥ TailRefillMinBytes，就从最大的片子开始，按「份」切出来。
            //   这样 2 个大片也能被摊成 16 份，让全部 worker 并行冲完最后一段。
            //
            // 关键：**先算总量再决定切不切**，而不是"切一刀看还剩多少"。
            long remainTotal = 0;
            foreach (var kv in active)
            {
                Chunk c = kv.Value;
                if (c == null)
                    continue;
                long pos2 = Interlocked.Read(ref c.PositionRaw);
                remainTotal += c.End - (c.Start + pos2) + 1;
            }

            if (remainTotal <= 0)
                return false;

            // ★ 每份大小必须按「**单个最大片**的剩余量」来算，不能按 remainTotal 算。
            //
            // 2026-11-01 实测踩到的坑（诊断 D3/D5 坐实）：
            //   末段只剩 2~3 个片在跑，比如 768KB + 448KB，总量 1.2MB、缺口 6 份。
            //   按总量均分 → slice = 1.2MB / 6 = 200KB（看似合理）；
            //   但如果按 remainTotal/deficit 得到 slice=832KB，而单个最大片只有 768KB，
            //   条件 biggestRemain(768KB) < slice(832KB) + min(256KB) 恒成立 → 一刀都切不出去。
            //   实测就是「539 次调用只切出 1 片」，14 个 worker 全程空转。
            //
            // 正解：以**最大片的剩余量**为基准，把它摊成若干份，每份 ≥ TailRefillMinBytes。
            //   这样 768KB 的片能被摊成 3 份 256KB，立刻多出 2 个可领分片。
            long maxRemain = 0;
            foreach (var kv in active)
            {
                Chunk c = kv.Value;
                if (c == null)
                    continue;
                long pos2 = Interlocked.Read(ref c.PositionRaw);
                long rem = c.End - (c.Start + pos2) + 1;
                if (rem > maxRemain)
                    maxRemain = rem;
            }

            if (maxRemain <= 0)
                return false;

            // 这个最大片最多能被切成几份（每份不低于下限）。
            long maxSlices = maxRemain / Options.TailRefillMinBytes;
            if (maxSlices < 2)
                return false;   // 连两刀都切不出来，补片没意义

            // 取「缺口数」与「能切的份数」的较小者，再据此算每份大小。
            int slicesWanted = deficit;
            if (slicesWanted > maxSlices)
                slicesWanted = (int)maxSlices;
            if (slicesWanted < 2)
                slicesWanted = 2;

            long sliceBytes = maxRemain / slicesWanted;
            if (sliceBytes < Options.TailRefillMinBytes)
                sliceBytes = Options.TailRefillMinBytes;

            // 🚨🚨 2026-11-05 新增：**速度自适应块大小**（对齐 refs/YunX 的 ElasticAllocator）。
            //
            // YunX 的做法（ElasticAllocator.baseBlockSize）：
            //     块 ≈ 单 worker 下载 TARGET_SECONDS(5秒) 的量，夹在 [256KB, 4MB]
            // 它这么设计是因为：
            //   · 快连接 → 用大块，省掉每次 Range 请求的建连/握手开销；
            //   · 慢连接 → 用小块，让「最后一点数据」能摊到所有连接上并行冲完。
            //
            // 我们原来是纯静态的：sliceBytes = maxRemain / slicesWanted，
            // 只跟「剩余量 / 缺口数」有关，**完全不看速度**。
            // 后果：链路很慢时（单连接 10KB/s），切出来的 256KB 块要磨 26 秒；
            //       而链路很快时，256KB 又太小 —— 每切一块都要重新建连，纯亏。
            //
            // 现在按实测速度校准：块 = 单连接 5 秒能下完的量。
            //   · 阈值仍由 TailRefillMinBytes 兜底（不低于 256KB，避免碎片化）；
            //   · 上限 4MB（对齐 YunX 的 MAX_ELASTIC_BLOCK，过大块会降低动态性）。
            // ⚠️ 速度取不到（0）时保持原值 —— 不能让「还没测出速度」把块算成 0。
            long adaptive = AdaptiveTailSliceBytes();
            if (adaptive > 0)
            {
                // 自适应值只做**上限收窄**，不放宽：
                // 放宽会让「剩余量很少」时切出超过剩余量的块（切不动）。
                if (adaptive < sliceBytes)
                    sliceBytes = Math.Max(adaptive, Options.TailRefillMinBytes);
            }

            // 按「份」切：每次从最大的片子里切出 sliceBytes 一段塞回队列。
            // 与二分不同，这里切出的尾巴大小固定 = sliceBytes，
            // 所以 2 个 768KB 的片可以被切成 6 份 256KB，真正填满空闲 worker。
            int produced = 0;
            int budget = deficit < target ? deficit : target;
            while (produced < budget)
            {
                Chunk biggest = null;
                long biggestRemain = 0;
                foreach (var kv in active)
                {
                    Chunk c = kv.Value;
                    if (c == null)
                        continue;
                    long pos2 = Interlocked.Read(ref c.PositionRaw);
                    long rem = c.End - (c.Start + pos2) + 1;
                    if (rem > biggestRemain)
                    {
                        biggestRemain = rem;
                        biggest = c;
                    }
                }

                if (biggest == null)
                    break;

                // 这个片剩下的不够再切一份了 → 停（它已经是最大的了，其它更切不动）。
                if (biggestRemain < sliceBytes + Options.TailRefillMinBytes)
                    break;

                // 从尾部切出 sliceBytes 一段（不是切一半）。
                if (!TrySplitChunkTail(biggest, queue, sliceBytes))
                    break;

                produced++;
            }

            return produced > 0;
        }
    }

    /// <summary>
    /// 从分片**尾部**切出指定大小的一段，作为新分片入队（末段补片专用）。
    ///
    /// <para>
    /// 与 <see cref="TrySplitChunk"/> 的区别：那个是**二分**（各占一半），
    /// 这个是**按固定份切尾巴** —— 末段需要把剩余数据摊成 N 份给 N 个 worker，
    /// 二分一次只能多出 1 个可领分片，填不满。
    /// </para>
    /// <para>
    /// 安全语义与二分完全一致：先改小原片的 End（让它尽快收尾），再入队尾巴，
    /// 两者写盘区间不重叠（原片 [Start, 新End]，尾巴 [新End+1, 旧End]）。
    /// </para>
    /// </summary>
    /// <param name="chunk">要切的原分片。</param>
    /// <param name="queue">新分片入队处。</param>
    /// <param name="tailBytes">要切出的尾巴大小（字节）。</param>
    private bool TrySplitChunkTail(Chunk chunk, ChunkQueue queue, long tailBytes)
    {
        // 保存快照期间不切分（方案 E）：切分是「下次再切也行」的优化，跳过一轮无感知。
        if (_chunkSplitPaused) return false;

        long pos = Interlocked.Read(ref chunk.PositionRaw);
        long curStart = chunk.Start + pos;
        long curEnd = chunk.End;

        long remain = curEnd - curStart + 1;
        // 切完尾巴后，原片剩下的部分也得够看（至少还能跑一小段）。
        if (remain <= tailBytes)
            return false;

        long tailStart = curEnd - tailBytes + 1;
        long newEnd = tailStart - 1;

        // 先改原片 End，再入队 —— 顺序与 TrySplitChunk 一致（理由见那里的注释）。
        // 同 TrySplitChunk：改 End 与登记新片必须原子（方案 A）。
        lock (_chunkTableLock)
        {
            chunk.End = newEnd;

            var tail = new Chunk(tailStart, curEnd)
            {
                Id = chunk.Id + "t_" + Guid.NewGuid().ToString("N").Substring(0, 6),
                MaxTryAgainOnFailover = chunk.MaxTryAgainOnFailover,
                Timeout = chunk.Timeout
            };

            queue.Enqueue(tail);
            _splitChunks[tail.Id] = tail;
            _logger?.LogDebug($"末段补片：分片 {chunk.Id} 切出尾部 {tail.Id}（{tailBytes} 字节，{tailStart}-{curEnd}）");
        }
        return true;
    }

    /// <summary>
    /// 把一个分片的剩余区间切成两半，后半段作为新分片入队。
    /// 返回 false 表示剩余太小或切分失败。
    /// </summary>
    /// <param name="minBytes">
    /// 切分下限（切完每半至少要这么大）。
    /// 慢片抢占传 <see cref="DownloadConfiguration.PreemptMinBytes"/>（512KB，保守），
    /// 末段补片传 <see cref="DownloadConfiguration.TailRefillMinBytes"/>（256KB，可以更细）。
    /// 两者语义不同，**不能共用同一个值** —— 共用会让末段切不动（512KB 下限在收尾时太大），
    /// 或让正常并发期切得过碎（256KB 会让每片都重新握手）。
    /// </param>
    private bool TrySplitChunk(Chunk chunk, ChunkQueue queue, CancellationToken cancel, long minBytes)
    {
        // 保存快照期间不切分（方案 E）。
        if (_chunkSplitPaused) return false;

        long pos = Interlocked.Read(ref chunk.PositionRaw);
        long curStart = chunk.Start + pos;
        long curEnd = chunk.End;

        long remain = curEnd - curStart + 1;
        if (remain < minBytes * 2)
            return false;   // 切完两半都太小，没意义

        long mid = curStart + remain / 2 - 1;

        // 先改原分片的 End（让它尽快收尾），再把后半段入队。
        // 顺序很重要：若先入队再改 End，后半段可能被 worker 立刻领走并开始下载，
        // 而原分片仍以为 End 是旧值，两边就会下同一段（虽然写盘位置不同不会损坏，
        // 但白白浪费带宽）。
        // 2026-11-01（方案 A）：「改 End + 登记新片」必须原子，否则保存快照会撞进中间态。
        // 锁内不含任何 I/O，微秒级，对吞吐无影响。
        lock (_chunkTableLock)
        {
            chunk.End = mid;

            var tail = new Chunk(mid + 1, curEnd)
            {
                Id = chunk.Id + "_" + Guid.NewGuid().ToString("N").Substring(0, 6),
                MaxTryAgainOnFailover = chunk.MaxTryAgainOnFailover,
                Timeout = chunk.Timeout
            };

            queue.Enqueue(tail);
            _splitChunks[tail.Id] = tail;
            _logger?.LogDebug($"抢占分片 {chunk.Id}：在 {mid} 处切开，尾部 {tail.Id}（{curEnd - mid} 字节）");
        }
        return true;
    }

    /// <summary>
    /// 与 <see cref="DownloadChunk"/> 相同，但**不占** <c>_parallelSemaphore</c> ——
    /// 队列模式下并发数由 worker 数量天然限制，再套信号量会死锁
    /// （worker 都在等信号量，队列里却没人在消费）。
    /// </summary>
    private async Task<Chunk> DownloadChunkNoSemaphore(Chunk chunk, Request request, PauseToken pause,
        CancellationTokenSource cancellationTokenSource)
    {
        ChunkDownloader chunkDownloader = new ChunkDownloader(chunk, Options, Package.Storage, _logger, Options.Transport);
        chunkDownloader.DownloadProgressChanged += OnChunkDownloadProgressChanged;

        cancellationTokenSource.Token.ThrowIfCancellationRequested();
        return await chunkDownloader.Download(request, pause, cancellationTokenSource.Token).ConfigureAwait(false);
    }

    private async Task SerialDownload(PauseToken pauseToken)
    {
        var tasks = GetChunksTasks(pauseToken);
        foreach (var task in tasks)
            await task.ConfigureAwait(false);
    }

    private IEnumerable<Task> GetChunksTasks(PauseToken pauseToken)
    {
        for (int i = 0; i < Package.Chunks.Length; i++)
        {
            var request = _requestInstances[i % _requestInstances.Count];
            yield return DownloadChunk(Package.Chunks[i], request, pauseToken, _globalCancellationTokenSource);
        }
    }

    private async Task<Chunk> DownloadChunk(Chunk chunk, Request request, PauseToken pause, CancellationTokenSource cancellationTokenSource)
    {
        ChunkDownloader chunkDownloader = new ChunkDownloader(chunk, Options, Package.Storage, _logger, Options.Transport);
        chunkDownloader.DownloadProgressChanged += OnChunkDownloadProgressChanged;
        await _parallelSemaphore.WaitAsync(cancellationTokenSource.Token).ConfigureAwait(false);
        try
        {
            cancellationTokenSource.Token.ThrowIfCancellationRequested();
            return await chunkDownloader.Download(request, pause, cancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            cancellationTokenSource.Token.ThrowIfCancellationRequested();
            cancellationTokenSource.Cancel(false);
            throw;
        }
        finally
        {
            _parallelSemaphore.Release();
        }
    }
}