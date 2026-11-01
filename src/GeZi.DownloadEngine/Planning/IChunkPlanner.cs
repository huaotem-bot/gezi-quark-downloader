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
    /// 分片规划器：决定一个文件被切成哪些区间。
    ///
    /// **为什么要有这层抽象**：bezzad 原生的分片是「静态等分」——片数 = ChunkCount，
    /// 每片大小 = 总大小 / 片数，余数全塞给最后一片。这套算法在「并发 8」的时代够用，
    /// 但在我们实测过的场景（夸克/UC 的 CDN 按连接限速、总有几条连接明显比别人慢）
    /// 会撞上「尾部塌缩」：片少时每条连接只领 1~2 片，一条慢连接攥着一大块慢慢啃，
    /// 其它连接早早空闲却无片可抢，整个任务的完成时间被最慢那条连接拖死。
    ///
    /// 我们自研引擎（<c>SegmentedDownloader.Split</c>）早就解决过这个问题：
    /// 片数 = max(按大小算的基础值, 有效并发 × 8)，夹取到 [1, 4096]。
    /// 每连接 8 片的窃取深度，让快的连接干完立刻抢下一片、慢的只拖自己。
    /// 这套算法实测与 Python 版持平（1.01x），是当前唯一验证过能跑满带宽的分片策略。
    ///
    /// 所以这里把「怎么切」抽成可插拔的零件：
    ///  - <see cref="StaticChunkPlanner"/>：bezzad 原生行为，保持向后兼容（默认）；
    ///  - <see cref="AdaptiveChunkPlanner"/>：我们的算法，给网盘下载用。
    ///
    /// ⚠️ 实现必须是**纯函数**：同样的 (totalFileSize, config) 必须永远产出同样的区间。
    /// 将来做续传时要靠它重算出与元数据一致的片表，一旦掺入时钟/随机数，
    /// 续传就会认不出旧分片、进度全丢。
    /// </summary>
    public interface IChunkPlanner
    {
        /// <summary>规划器名字，仅用于日志与诊断。</summary>
        string Name { get; }

        /// <summary>
        /// 是否支持运行期把一个大分片再切小（工作窃取）。
        /// 目前两种实现都返回 false —— 我们靠「初始就切足够多的片」达到窃取效果，
        /// 而不是运行期改分片边界。将来若要做真正的动态窃取，这里是开关。
        /// </summary>
        bool SupportsDynamicResplit { get; }

        /// <summary>
        /// 规划分片区间。
        /// </summary>
        /// <param name="totalFileSize">文件总字节数（已扣除 RangeLow 之后的有效长度）。</param>
        /// <param name="config">下载配置，实现方只读取自己需要的字段（ChunkCount / RangeLow 等）。</param>
        /// <returns>按起点升序、互不重叠、首尾相接覆盖整个文件的分片数组。</returns>
        Chunk[] Plan(long totalFileSize, DownloadConfiguration config);
    }
}
