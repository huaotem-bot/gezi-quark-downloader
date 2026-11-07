using System;

namespace GeZi.DownloadEngine.Framework;

/// <summary>
/// 「分片请求了区间，服务端却回了 200 整文件」专用的异常（2026-11-07 新增）。
///
/// <para>
/// 🚨🚨 为什么需要它（对照 refs/YunX 的 <c>ChunkResult.RANGE_IGNORED</c>）：
/// 原来只要 <c>IfRangeValidator.GuardChunk</c> 判出 <c>ResourceChanged</c>，
/// 就直接抛 <see cref="FatalChunkException"/> → <c>DownloadService</c> 走
/// <c>ClearResumeAsync()</c> → **整个任务失败、续传文件一并删除**。
/// 于是一个下到 90% 的大文件，只因为某个 CDN 节点偶发忽略了一次 Range，
/// 前面几十分钟全部作废。这是「大文件下载出错」里最伤人的一条。
/// </para>
///
/// <para>
/// ⇒ 现在由 <c>RangeDowngradeGate</c> 先计数：
/// </para>
/// <list type="bullet">
/// <item>窗口内**未超容忍**（默认 3 次）→ 抛本异常 → 由 <c>Download()</c> 接住，
///       走普通重试路径继续拉这一片，**不判定任务失败、不清续传**；</item>
/// <item>窗口内**超过容忍** → 仍抛 <see cref="FatalChunkException"/>，
///       认定「服务端真的不支持 Range 了」，保持原来的失败与清续传行为不变。</item>
/// </list>
///
/// <para>
/// ⚠️ 它与 <see cref="SlowReconnectException"/> 的区别：那个是「换一条更快的连接」，
/// 零退避；这个是「服务端这次没按规矩回」，应当**稍等再试**（真降级时立刻重连只会再撞一次）。
/// </para>
/// </summary>
internal sealed class RangeIgnoredException : Exception
{
    /// <summary>本窗口内已累计的次数（含本次），仅供日志与诊断。</summary>
    public int Occurrence { get; }

    public RangeIgnoredException(string message, int occurrence) : base(message)
    {
        Occurrence = occurrence;
    }
}
