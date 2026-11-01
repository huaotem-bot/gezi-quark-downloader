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

using GeZi.DownloadEngine.Framework;

namespace GeZi.DownloadEngine.Planning
{
    /// <summary>
    /// bezzad 原生分片：静态等分。
    ///
    /// 行为与拆分前 <c>ChunkHub.Validate + SetFileChunks</c> **完全一致**（这是默认实现，
    /// 保持向后兼容，让 bezzad 的既有测试与调用方不受影响）：
    ///  - 片数 = <c>config.ChunkCount</c>，但不超过文件字节数，且至少 1；
    ///  - 每片大小 = 总大小 / 片数（整除）；
    ///  - 余数全部加到**最后一片**的 End 上；
    ///  - 起点从 <c>config.RangeLow</c> 开始。
    ///
    /// 缺陷（已实测）：片少时没有工作窃取余量，一条慢连接会拖死整个任务。
    /// 网盘下载请用 <see cref="AdaptiveChunkPlanner"/>。
    /// </summary>
    public sealed class StaticChunkPlanner : IChunkPlanner
    {
        /// <summary>无状态单例（规划是纯函数，实例可全局共享）。</summary>
        public static readonly StaticChunkPlanner Instance = new StaticChunkPlanner();

        public string Name => "static";

        public bool SupportsDynamicResplit => false;

        public Chunk[] Plan(long totalFileSize, DownloadConfiguration config)
        {
            if (config == null)
                config = new DownloadConfiguration();

            int chunkCount = config.ChunkCount;
            long startOffset = config.RangeLow;

            if (startOffset < 0)
                startOffset = 0;

            if (totalFileSize < chunkCount)
                chunkCount = (int)totalFileSize;

            if (chunkCount < 1)
                chunkCount = 1;

            long chunkSize = totalFileSize / chunkCount;

            var chunks = new Chunk[chunkCount];
            for (int i = 0; i < chunkCount; i++)
            {
                long startPosition = startOffset + (i * chunkSize);
                long endPosition = startPosition + chunkSize - 1;
                chunks[i] = NewChunk(i.ToString(), startPosition, endPosition, config);
            }

            // 余数全塞给最后一片（与 bezzad 原生一致）。
            chunks[chunks.Length - 1].End += totalFileSize % chunkCount;

            return chunks;
        }

        /// <summary>
        /// 造一个分片，并把配置里的重试次数与超时灌进去。
        /// （与 bezzad 原 <c>ChunkHub.GetChunk</c> 一致。）
        /// </summary>
        internal static Chunk NewChunk(string id, long start, long end, DownloadConfiguration config)
        {
            var chunk = new Chunk(start, end)
            {
                Id = id,
                MaxTryAgainOnFailover = config.MaxTryAgainOnFailover,
                Timeout = config.Timeout
            };
            return chunk;
        }
    }
}
