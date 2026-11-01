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
using GeZi.DownloadEngine.Framework;

namespace GeZi.DownloadEngine.Planning
{
    /// <summary>
    /// 自适应分片：用我们自研引擎（<see cref="ChunkSplitter"/>）的算法切 bezzad 的分片。
    ///
    /// **为什么网盘下载要用它**（实测数据）：
    /// 同一个 UC 文件（186,678,707 字节）、同样的 32 并发、同样的 OpenSSL 通道，
    ///  - <see cref="StaticChunkPlanner"/>（32 片等分）→ **1.00 MB/s**
    ///  - 自研引擎（4096 片、每连接 8 片窃取深度）→ **2.69 MB/s**
    /// 差 2.7 倍。根因不是通道、不是并发数，而是**分片粒度**：
    /// 片少时一条慢连接攥着一大块啃，其它连接干完就闲，总速度塌到几条慢连接之和。
    ///
    /// 片多时 bezzad 的调度模型（chunk = task，SemaphoreSlim 排队）天然具备
    /// 工作窃取效果：快的 worker 释放槽位后立刻领下一片，慢的只拖自己。
    /// 所以这里**不需要**运行期改分片边界（<see cref="SupportsDynamicResplit"/> 为 false），
    /// 靠「初始就切足够多的片」达到均衡 —— 这与 gopeed 的 helpOtherConnection
    /// 是两条不同的路，我们的路实测能跑满带宽，且不动 bezzad 的调度循环。
    ///
    /// ⚠️ <c>config.ChunkCount</c> 在这里只作为「并发数」使用（传给 Splitter 当 threads），
    /// 实际片数由算法决定。这与 <see cref="StaticChunkPlanner"/> 语义不同，是刻意的。
    /// </summary>
    public sealed class AdaptiveChunkPlanner : IChunkPlanner
    {
        /// <summary>无状态单例（规划是纯函数，实例可全局共享）。</summary>
        public static readonly AdaptiveChunkPlanner Instance = new AdaptiveChunkPlanner();

        public string Name => "adaptive";

        public bool SupportsDynamicResplit => false;

        public Chunk[] Plan(long totalFileSize, DownloadConfiguration config)
        {
            if (config == null)
                config = new DownloadConfiguration();

            long startOffset = config.RangeLow;
            if (startOffset < 0)
                startOffset = 0;

            // 并发数：bezzad 里 ChunkCount 是「切几片」，我们借用它表达「几个连接」。
            // 真正想要的并发由调用方通过 ChunkCount/ParallelCount 传入。
            int threads = config.ChunkCount > 0 ? config.ChunkCount : 1;

            List<(long Start, long End)> ranges = ChunkSplitter.Split(totalFileSize, threads);

            if (ranges.Count == 0)
            {
                // 空文件 / 零长度：给一片占位，避免调用方拿到空数组后除零或空引用。
                return new[] { StaticChunkPlanner.NewChunk("0", startOffset, startOffset, config) };
            }

            var chunks = new Chunk[ranges.Count];
            for (int i = 0; i < ranges.Count; i++)
            {
                long start = startOffset + ranges[i].Start;
                long end = startOffset + ranges[i].End;
                chunks[i] = StaticChunkPlanner.NewChunk(i.ToString(), start, end, config);
            }

            return chunks;
        }
    }
}
