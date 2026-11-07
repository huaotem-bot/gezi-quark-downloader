using System;

namespace GeZi.DownloadEngine.Framework;

/// <summary>
/// 「连接太慢，主动换一条」专用的异常（2026-11-05 新增）。
///
/// <para>
/// 🚨🚨 为什么需要它（用户实测日志：
/// <c>Task Canceled on download chunk 24 with retry :: ReadAsync of the chunk 24
/// stream was too slow (10239 B/s) and has been reconnected</c>）：
/// </para>
///
/// <para>
/// 原来慢连接重连**复用了 TaskCanceledException**，于是被 <c>Download()</c> 里
/// 那个「读超时也重试」的 catch 分支接住，一路走 <see cref="ChunkDownloader.ContinueWithDelay"/>：
/// </para>
/// <list type="number">
/// <item>**干等指数退避** 150 → 300 → 600 → 1200 → 2400 → 5000 ms。
///       而慢检测窗口本身就有 10 秒，一次「换连接」的实际成本是 10 秒 + 退避；</item>
/// <item>**消耗 <c>Chunk.FailoverCount</c>** —— 那是「真出错」的重试预算。
///       3 次慢重连就把出错重试额度吃掉 3 次，之后遇到 503 可能已经没有机会；</item>
/// <item>**永久放大 <c>Chunk.Timeout</c>**（每次 +10ms，从不回退），
///       读超时窗口越来越宽，慢连接反而更难被超时机制抓住。</item>
/// </list>
///
/// <para>
/// 对照 refs/YunX 的做法（<c>ChunkDownloader.kt</c> 的 <c>PreemptedException</c>）：
/// 抢占属于「换连接」而不是「失败」，**零退避立即重连**，且不消耗重试次数。
/// 实测日志里那 61 路空转 37 秒等最后 500KB 的场景，靠的就是这个语义。
/// </para>
///
/// <para>
/// ⇒ 现在慢重连抛这个专用异常，由 <c>Download()</c> 单独 catch：
/// 直接重连、不退避、不动 FailoverCount、不动 Timeout。
/// 真实的 IO 异常（503 / 连接重置 / 读超时）仍走原来的退避路径，行为完全不变。
/// </para>
/// </summary>
internal sealed class SlowReconnectException : Exception
{
    /// <summary>触发本次重连时的实测速度（字节/秒），仅供日志与诊断。</summary>
    public double MeasuredBps { get; }

    public SlowReconnectException(string message, double measuredBps) : base(message)
    {
        MeasuredBps = measuredBps;
    }
}
