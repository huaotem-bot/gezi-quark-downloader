// GeZi —— 多网盘下载器 (Multi-cloud downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/agpl-3.0>.

using System;
using System.Collections.Generic;

namespace GeZi.DownloadEngine.Planning
{
    /// <summary>
    /// 自研分片算法（从 <c>Legacy/SegmentedDownloader.Split.cs</c> 原样搬运，
    /// 逻辑一字未改，只是把「返回 (Start,End) 元组列表」抽成纯静态类，
    /// 让 Legacy 引擎与 bezzad 适配层共用同一份定义 —— 避免两处各写一遍后漂移）。
    ///
    /// **设计目标**：让不同大小的文件都能跑出它带宽上限的速度，而且读数要稳。
    ///
    /// 历史教训（两次，方向相反）：
    ///  1. 早先单分片下限硬编码 4MB，30MB 的文件只能切 7 片，
    ///     用户把线程数调到 512 也只用上 7 个连接 —— 设置形同虚设。
    ///  2. 后来改成「片大小 = 总大小 / 线程数」，用户填 512 就切 512 片。
    ///     结果虽然快，但**速度剧烈波动**：分片太碎，每个连接要换好几次片，
    ///     而每换一次都要重新建连（TCP+TLS 握手期间那条槽位零吞吐），
    ///     于是速度在「满速」和「换片空窗」之间来回锯齿。
    ///
    /// **上面那套「片数尽量少」的思路是错的。**
    /// 教训来自 Python 版对比实测：同样的网络、账号、文件，
    /// Python 版能跑 20~100+ MB/s，C# 版只有 3~5 MB/s。
    /// 根因就是分片策略 —— Python 版「片数 = 线程 × 8，上限 4096」，
    /// C# 版却是「片数 = 并发 × 1.5」，片大而少。
    ///
    /// 为什么「片大而少」会致命：
    ///   夸克/UC 的 CDN 会按连接限速，总有几条连接明显比别人慢。
    ///   片少时，每条连接领到 1~2 片就没了，一条慢连接攥着一大块
    ///   数据慢慢啃，其它连接早早空闲却无片可抢 —— 这就是「尾部塌缩」，
    ///   整个任务的完成时间被最慢的那条连接拖死，总速度也就塌到几条
    ///   慢连接之和。
    /// 片多时（每线程 8 片），快的连接干完立刻抢下一片，慢的只拖自己，
    /// 负载自动均衡，总速度 ≈ 所有连接的带宽之和。
    ///
    /// 现在的算法（对齐 Python 版）：
    ///   片数 = max(按大小算的基础值, 有效并发 × 8)，夹取到 [1, 4096]
    /// 下限 512KB；单片上限按文件大小分档（1MB / 2MB / 8MB / 16MB）。
    /// </summary>
    public static class ChunkSplitter
    {
        /// <summary>
        /// 实际能开出的最大并发连接数。
        /// ⚠️ 这里是单文件并发上限的**唯一权威定义**（自研引擎已删除）：
        /// 用户把线程数填得再大，引擎也开不出更多连接，切片时就不该按更大的数去切。
        /// </summary>
        public const int MaxRealConcurrency = 512;

        /// <summary>
        /// 把文件切成若干区间，供多 worker 以「工作窃取」方式领取。
        /// </summary>
        /// <param name="total">文件总字节数。</param>
        /// <param name="threads">用户请求的并发数（会被 <see cref="MaxRealConcurrency"/> 夹取）。</param>
        /// <returns>按起点升序、首尾相接、完整覆盖 [0, total-1] 的区间列表。</returns>
        public static List<(long Start, long End)> Split(long total, int threads)
        {
            if (total <= 0)
                return new List<(long, long)>();

            // 🚨🚨 2026-11-07 重构：从「先算片数、再反推片大小」改成「先定片大小、再算片数」。
            //
            // 旧结构的病根是**双向夹取**，两个方向都会出错：
            //   ① 小文件被 `capByMinChunk = total / 512KB` 钉死片数
            //      → 88MB 只能切 176 片，512 条连接里 336 条永远闲着（用户报「吃不满线程」）；
            //   ② 大文件被 `if (n > maxSegments) n = maxSegments` 反向夹回
            //      → 100GB 变成 4096 片 × 25.6MB，上面刚夹过的 `maxSegBytes = 16MB` 被**彻底绕过**
            //      （用户报「大文件哪怕开了最高线程分片也太大了」）。
            // 两者同源，改一处不改另一处必然原地打转。
            //
            // 新结构只有三步，没有反向夹取：
            //   seg = clamp(total / (并发 × 8), 最小片, 最大片)
            //   n   = ceil(total / seg)          ← 片数由覆盖关系算出，不再被任何上限压低
            // 片数上限退化为「元数据护栏」（只防病态输入，不参与调优）：
            //   4096 那个值当年是调优手段，现在改成 65536 只做护栏 ——
            //   100GB + 512 线程算出来是 6400 片，离护栏还很远。
            //
            // 为什么是「并发 × 8」：工作窃取深度 8。慢连接被抢占后要立刻有片可领，
            // 深度不足会退化成「每连接一片、干完就闲」（2026-10-11 照抄云析封顶 512 踩过，
            // 速度崩到 27KB/s）。这是本项目唯一被反复验证过的比例，不要动。
            int eff = threads > 0 ? Math.Min(threads, MaxRealConcurrency) : 1;
            if (eff < 1) eff = 1;

            // 最小片长（三档）。**不能为了「片数够分」就把小文件的片切到 128KB** ——
            // 小文件切碎的唯一后果是「一堆 TLS 握手换几十 KB」，净亏。
            //   <8MB   → 512KB（5MB 文件 = 10 片，8MB = 16 片）
            //   8~64MB → 256KB（64MB = 256 片）
            //   ≥64MB  → 128KB（88MB = 704 片，能真正吃满 512 条连接）
            // ⚠️ 128KB 是硬底线，不能再往下：单片 64KB 在 13KB/s 的连接上只要 4.7 秒，
            //    而一次 TLS 握手 0.3~0.6 秒 —— 握手开销要吃掉 6%~13%，净亏。
            long minSegBytes;
            if (total < 8L * 1024 * 1024) minSegBytes = 512L * 1024;                 // 512KB
            else if (total < 64L * 1024 * 1024) minSegBytes = 256L * 1024;           // 256KB
            else minSegBytes = 128L * 1024;                                          // 128KB

            // 最大片长：文件越大，单片可以越大（总时长久，单片大一点也不显著拖长尾）。
            // 但必须有上限 —— 否则 100GB 会出 25.6MB 的巨片，一条慢连接就能拖死收尾。
            long maxSegBytes;
            if (total < 32L * 1024 * 1024) maxSegBytes = 1L * 1024 * 1024;            // <32MB → 1MB
            else if (total < 256L * 1024 * 1024) maxSegBytes = 2L * 1024 * 1024;      // <256MB → 2MB
            else if (total < 2L * 1024 * 1024 * 1024) maxSegBytes = 8L * 1024 * 1024; // <2GB → 8MB
            else maxSegBytes = 16L * 1024 * 1024;                                      // ≥2GB → 16MB

            // 元数据护栏（不是调优参数）：只防病态输入把 .qmeta 撑爆。
            // 4096 那个老值是当年「先算片数」结构的调优结果，在「先定片长」结构里已无意义。
            const long MaxPartsGuard = 65536;

            long seg = total / ((long)eff * 8);
            if (seg < 1) seg = 1;
            if (seg < minSegBytes) seg = minSegBytes;
            if (seg > maxSegBytes) seg = maxSegBytes;
            if (seg < 1) seg = 1;

            // 片数由「片长覆盖整个文件」自然得出 —— 这里**不做任何反向夹取**。
            long n = (total + seg - 1) / seg;
            if (n < 1) n = 1;
            if (n > MaxPartsGuard) n = MaxPartsGuard;
            if (n > int.MaxValue) n = int.MaxValue;

            // 重算一次对齐后的片大小（n 被护栏夹过时才会变化，正常情况逐字节等于 seg）
            long finalSeg = (total + n - 1) / n;
            if (finalSeg < 1) finalSeg = 1;

            var parts = new List<(long Start, long End)>((int)Math.Min(n, int.MaxValue));
            for (long i = 0; i < n; i++)
            {
                long s = i * finalSeg;
                if (s >= total) break;
                long e = Math.Min(s + finalSeg - 1, total - 1);
                parts.Add((s, e));
            }

            // ==================== 头部变长分片（2026-10-13）====================
            // 真实 CDN 单连接只有 ~40KB/s，而合并必须严格按片序 —— 于是目标文件的
            // 增长速率被「头部那一片的单连接速率」卡住。30GB 按 16MB/片时，
            // 头 16MB 要约 **6.7 分钟**才凑齐，起播体验极差。
            // 把文件**头部**单独切成 1MB 的小片，让多条连接并行下头部；
            // 中后部维持原有的「静态大片 + 尾部动态窃取」（那是实测过的吞吐最优解，
            // 全局切小会崩吞吐，踩过）。
            // ⚠️ **必须是纯函数**：RunSegmentedAsync 每次都会重算 parts 并与 .qmeta 比对
            //    （PartsEqual），一旦不一致就重建 plan、W 全部归零 → **进度全丢**。
            //    所以这里只允许用 total/threads 推导常量，绝不能用随机数或时钟。
            // ⚠️ **不能突破元数据护栏**（<see cref="MaxPartsGuard"/>，防 100GB+ 失控）。
            // 头部阈值 2026-10-24 从 1GB 降到 8MB、占比从 1/16 提到 1/4、上限 64→256 片。
            // 原因（实测）：顺序落盘只认「从 0 连续的已刷盘前缀」。24MB 文件 + 16 线程时，
            // 若第一条连接被限速到 20KB/s，磁盘文件会一路保持 0 字节直到它下完（22 秒），
            // 边下边看彻底失效。头部切小片让几十条连接并行填头，慢连接最多只拖住 1MB。
            const long HeadZoneMinTotal = 8L * 1024 * 1024;        // ≥8MB 就启用
            const long HeadZoneBytes = 64L * 1024 * 1024;          // 头部区域最大 64MB
            const long HeadSegBytes = 1L * 1024 * 1024;            // 头部分片 1MB
            const int HeadMaxParts = 256;                          // 头部最多 256 片

            if (total >= HeadZoneMinTotal)
            {
                long headZone = Math.Min(HeadZoneBytes, total / 4);
                long headParts = headZone / HeadSegBytes;
                if (headParts > HeadMaxParts) headParts = HeadMaxParts;

                // 至少 2 片才有并行意义；剩余部分还要够切（否则不如不分）
                long headEnd = headParts * HeadSegBytes - 1;
                long remainTotal = total - (headEnd + 1);
                if (headParts >= 2 && remainTotal >= minSegBytes)
                {
                    // 中后部**必须沿用上面已经验证过能覆盖到 total 的 finalSeg**，
                    // 绝不能按 nMid 重新算 midSeg = remainTotal / nMid ——
                    // 🚨 2026-10-14 实测踩坑（用户 4.97GB 文件，并发 512 档）：
                    // n = 4096、finalSeg = 1301609（能完整覆盖 total）。
                    // 变长分支里 headParts(64) + nMid(4096) = 4160 > maxSegments(4096)
                    // → nMid 被夹成 4032 → 若按 4032 重算 midSeg 会更大、被夹回 finalSeg，
                    // 但 4032 × 1301609 + 67108864 = 5315594752 **够不到 total**，
                    // 而循环靠 if (ms >= total) break 提前退出 → **尾部 15.4MB 被静默丢掉**。
                    // 症状：所有分片 100% 下完、meta 记录完整、但目标文件就是差一截，
                    // 且 .qparts 已被清空 → 无法续传补下，只能整文件重下。
                    // ✅ 正解：片大小直接用 finalSeg（它对整个 total 是精确的），
                    //    片数按「实际能切出多少片」自然得出，末尾余量并入最后一片。
                    long midSeg = finalSeg;

                    var v = new List<(long Start, long End)>((int)(headParts + n));
                    long s = 0;
                    for (long i = 0; i < headParts; i++)
                    {
                        v.Add((s, s + HeadSegBytes - 1));
                        s += HeadSegBytes;
                    }
                    // 中后部：从 headEnd+1 起按 midSeg 连续切，最后一片吃到 total-1。
                    // 用 while 而不是 for(nMid) —— 片数由覆盖关系自然决定，
                    // 不会再出现「算好的片数切不完整个区间」的缺口。
                    while (s < total)
                    {
                        long e = Math.Min(s + midSeg - 1, total - 1);
                        v.Add((s, e));
                        s = e + 1;
                    }
                    return v;
                }
            }

            return parts;
        }
    }
}
