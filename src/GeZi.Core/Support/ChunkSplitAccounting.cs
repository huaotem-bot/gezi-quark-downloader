using System;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 分片切分时的「Position 记账对账」纯函数（无 I/O、无状态、可自检）。
    ///
    /// <para>
    /// 🚨🚨 2026-11-08 修「进度 100% 却还在下载、必须重启续传才完成」的真根因。
    /// 日志铁证（GeZi.log 15:08~15:10）：
    ///   「收尾期降并发：可领片数 22」→「总进度 100.0%，总速度 166.12 KB/s」，
    ///   而 166 KB/s ≈ 22 片 × 8 KB/s —— 进度已显示 100%，仍有 22 片在真下。
    /// </para>
    ///
    /// <para>
    /// 根因是**进度账与完成判据不同源**：
    ///   · 进度 = Sum(所有分片 Position)，一加就到 100%；
    ///   · 完成判据 = 每个分片 Position ≥ Length（逐片看，缺一片都不算完）。
    /// 分片切分（抢占 / 末段补片）在并发写时存在竞态 ——
    ///   <c>TrySplitChunkTail</c>/<c>TrySplitChunk</c> 先在**锁外**读 Position，
    ///   再**加锁**改小 End。中间这个间隙，正在写该片的 worker 又推进了 Position，
    ///   于是「原片 Position 越过新 End（多算几字节）」+「切出去的尾片 Position 从 0 开始（少算同样多）」。
    ///   一加一减总量碰巧 = 总长 → 显示 100%；但逐片看尾片没下满 → AllChunksDone() 恒 false
    ///   → 任务卡在「下载中」不进入完成流程。
    /// </para>
    ///
    /// <para>
    /// 修复：切分时把「原片已写但越过新 End」的字节**转记到尾片**，
    /// 让进度与完成判据永远同源。本函数是这段对账的纯计算部分。
    /// </para>
    /// </summary>
    public static class ChunkSplitAccounting
    {
        /// <summary>
        /// 切分一个分片时，把「原片已写但越过新 End」的字节转记到尾片。
        /// </summary>
        /// <param name="start">原片的 Start（区间起点，闭）。</param>
        /// <param name="position">锁内重新读到的原片已写字节数（Position）。</param>
        /// <param name="newEnd">切分后原片的新 End（闭）。</param>
        /// <param name="newPosition">原片应夹回的真实 Position（不会超过新 Length）。</param>
        /// <param name="carriedBytes">应转记到尾片的「超额已写」字节数（这些字节实际写在了尾片区间）。</param>
        public static void Reconcile(long start, long position, long newEnd,
            out long newPosition, out long carriedBytes)
        {
            long newLength = newEnd - start + 1;
            if (newLength < 0)
                newLength = 0;

            if (position > newLength)
            {
                newPosition = newLength;
                carriedBytes = position - newLength;
            }
            else
            {
                newPosition = position < 0 ? 0 : position;
                carriedBytes = 0;
            }
        }
    }
}
