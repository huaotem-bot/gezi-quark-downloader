using System;
using System.Threading;   // 2026-11-05：外部换连接标志用 Interlocked

namespace GeZi.DownloadEngine.Framework;

/// <summary>
/// Chunk data structure
/// </summary>
public class Chunk
{
    /// <summary>
    /// Chunk unique identity name
    /// </summary>
    public string Id { get; set; }
    
    /// <summary>
    /// Start offset of the chunk in the file bytes
    /// </summary>
    public long Start { get; set; }

    /// <summary>
    /// End offset of the chunk in the file bytes
    /// </summary>
    public long End { get; set; }

    /// <summary>
    /// Current write offset of the chunk
    /// </summary>
    public long Position
    {
        get { return PositionRaw; }
        set { PositionRaw = value; }
    }

    /// <summary>
    /// <see cref="Position"/> 的底层字段，供 <see cref="Interlocked"/> 原子访问。
    ///
    /// **为什么单独暴露**：慢连接抢占要在**另一个线程**上读当前进度来判断某个分片
    /// 是不是落后了。直接读 <c>Position</c> 属性在 x64 上虽然也不会撕裂（对齐 8 字节），
    /// 但用 <see cref="Interlocked.Read(ref long)"/> 能给出明确的内存屏障语义，
    /// 避免监控线程读到 CPU 缓存里的陈旧值、把快分片误判成慢分片而白白切碎。
    /// 本字段与 <see cref="Position"/> 属性**指向同一块内存**（属性直接读写它），
    /// 两边永远不会漂移；保留属性是为了不改动 bezzad 既有调用点。
    /// </summary>
    public long PositionRaw;

    /// <summary>
    /// How many times to try again after the error
    /// </summary>
    public int MaxTryAgainOnFailover { get; set; }

    /// <summary>
    /// How many milliseconds to wait for a response from the server?
    /// </summary>
    public int Timeout { get; set; }

    /// <summary>
    /// How many times has downloading the chunk failed?
    /// </summary>
    public int FailoverCount { get; private set; }

    /// <summary>
    /// 该分片因为「连接太慢」而主动断开重连过几次。
    ///
    /// 与 <see cref="FailoverCount"/> 分开计数，因为两者语义不同：
    /// FailoverCount 统计的是**出错**（超时/503/连接中断），
    /// SpeedReconnectCount 统计的是**没出错但慢得离谱**。
    /// 慢重连必须有独立上限（默认 3 次），否则一条被 CDN 限速的连接
    /// 会无限重连，把带宽全浪费在 TCP 握手和 HTTP 请求头上。
    /// 达到上限后就不再重连，退化为让抢占机制把剩余区间切给别人。
    /// </summary>
    public int SpeedReconnectCount { get; set; }

    /// <summary>
    /// 2026-11-05 新增：**外部请求换连接**的标志（由 <c>PreemptRound</c> 置位）。
    ///
    /// <para>
    /// 与 <see cref="SpeedReconnectCount"/> 的区别：
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="SpeedReconnectCount"/> 是**分片自己**在 ReadStream 里判断「我太慢了」，
    ///       属于**自省式**判定；</item>
    /// <item>本标志是**旁观者**（PreemptRound 的监控线程）比较「这条连接 vs 同伴」后，
    ///       从外部要求它换连接，属于**相对式**判定。</item>
    /// </list>
    ///
    /// <para>
    /// 🚨 为什么两者都要（对齐 refs/YunX 的 <c>InflightChunk.preempt</c>）：
    /// 自省式只能抓「绝对慢」（低于固定阈值），而网盘场景常见的是**相对慢** ——
    /// 一条连接 60KB/s 在 16 路里不算慢，但在 256 路里就是掉队；
    /// 反过来，全部连接都只有 10KB/s 时，谁都不该被判慢（那是整体限速，换连接也没用）。
    /// 只有「同伴快、它慢」才值得换 —— 这正是旁观者视角才能看到的。
    /// </para>
    ///
    /// <para>
    /// 用 <see cref="Interlocked"/> 读写：监控线程写，worker 线程读。
    /// </para>
    /// </summary>
    private int _externalReconnectRequested;

    /// <summary>外部请求换连接（PreemptRound 调用）。</summary>
    public void RequestExternalReconnect()
    {
        Interlocked.Exchange(ref _externalReconnectRequested, 1);
    }

    /// <summary>读取并**清除**「外部请求换连接」标志（worker 读取时用，读完即复位）。</summary>
    public bool TakeExternalReconnectRequest()
    {
        return Interlocked.Exchange(ref _externalReconnectRequested, 0) != 0;
    }

    /// <summary>
    /// Length of current chunk. 
    /// When the chunk length is zero, the file is open to receive new bytes
    /// until no more bytes are received from the server.
    /// </summary>
    public long Length => End - Start + 1;

    /// <summary>
    /// Unused length of current chunk.
    /// When the chunk length is zero, the file is open to receive new bytes
    /// until no more bytes are received from the server.
    /// </summary>
    public long EmptyLength => Length > 0 ? Length - Position : long.MaxValue;

    /// <summary>
    /// Can write more data on this chunk according to the chunk situations?
    /// <para>
    /// 🚨 2026-10-28 修 off-by-one：<see cref="Length"/> 是<b>闭区间</b>
    /// （<c>End - Start + 1</c>），所以判断「还能不能写」必须用 <c>End + 1</c>。
    /// 原来写的是 <c>Start + Position &lt; End</c>，等于每片**少算最后一个字节**：
    /// 分片 <c>Start=0, End=0</c>（长度 1）时 <c>0+0 &lt; 0</c> 为假，一个字节都写不进去。
    /// </para>
    /// <para>
    /// 触发条件不是「总有」，而是「分片长度 ≡ 1 (mod 64KB)」时必然出现：
    /// <c>ChunkDownloader</c> 一次只读 64 KB，最后一片正好只剩 65537 字节时，
    /// 第 65536 字节处 <c>Position=65536</c>、<c>End=Start+65536</c>，
    /// 判定 <c>&lt;</c> 失败 → 少 1 字节却标记完成 → 续传表也记完成 → <b>重试永远补不上</b>。
    /// </para>
    /// </summary>
    public bool CanWrite => Length > 0 ? Start + Position < End + 1 : true;


    public Chunk()
    {
        Timeout = 1000;
        Id = Guid.NewGuid().ToString("N");
    }

    public Chunk(long start, long end) : this()
    {
        Start = start;
        End = end;
    }

    public bool CanTryAgainOnFailover()
    {
        return FailoverCount++ < MaxTryAgainOnFailover;
    }

    public void Clear()
    {
        Position = 0;
        FailoverCount = 0;
    }

    /// <summary>
    /// 这一片是否真的把数据写满了。
    /// <para>
    /// 🚨 2026-10-28 修 off-by-one：与 <see cref="CanWrite"/> 同一处错误。
    /// <see cref="Length"/> 是闭区间（<c>End - Start + 1</c>），
    /// 「已写到片尾」的判据必须是 <c>Start + Position &gt; End</c>（即 <c>&gt;= End + 1</c>）。
    /// </para>
    /// <para>
    /// 原来写的是 <c>Start + Position &gt;= End</c>，后果比 <c>CanWrite</c> 更恶劣：
    /// <c>Start=0, End=0, Position=0</c>（零字节）时 <c>0 &gt;= 0</c> 成立 → <b>零字节就判完成</b>；
    /// 其余分片则普遍少算最后 1 字节，且续传表照抄这个结论 →
    /// 一旦漏字节就<b>永久</b>补不上（重下也按「已完成」跳过）。
    /// </para>
    /// </summary>
    public bool IsDownloadCompleted()
    {
        var isNoneEmptyFile = Length > 0;
        var isChunkedFilledWithBytes = Start + Position > End;

        return isNoneEmptyFile && isChunkedFilledWithBytes;
    }

    public bool IsValidPosition()
    {
        return Length == 0 || (Position >= 0 && Position <= Length);
    }
}