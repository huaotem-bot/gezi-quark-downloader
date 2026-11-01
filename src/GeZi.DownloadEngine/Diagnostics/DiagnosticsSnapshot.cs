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

// ---- 诊断快照类型（2026-10-29 从 Legacy 自研引擎迁出） ----
// 自研引擎删除后，这个类型仍被 DownloadScheduler / FusionDownloader / UI 诊断面板共用，
// 所以单独成文件，不再挂在任何引擎类上。

using System;

namespace GeZi.DownloadEngine
{
    /// <summary>
    /// 一次下载的**诊断快照** —— 供 UI 的诊断面板消费（结构化数据，不需要解析日志文本）。
    /// <para>这些指标合起来回答"速度上不去到底卡在哪"：</para>
    /// <list type="bullet">
    /// <item>活跃连接数接近上限但速度低 → 服务端限速（客户端无解）；</item>
    /// <item>活跃连接数明显偏低 → 客户端侧没把连接发出去（可优化）；</item>
    /// <item>对端 IP 只有 1 个 → DNS 没分散，慢连接抢占重连也还是同节点；</item>
    /// <item>已抢占次数偏高 → 存在长尾慢连接，抢占机制正在起作用。</item>
    /// </list>
    /// </summary>
    public class DiagnosticsSnapshot
    {
        /// <summary>任务名（文件名）。</summary>
        public string Name { get; set; }
        /// <summary>
        /// 目标文件**完整路径**。
        /// <para>为什么必须单独带一个：UI 侧要拿它把快照**精确**对到某个任务行上。
        /// 用 <see cref="Name"/> 对是错的 —— 那个名字经过 <c>PathUtil.SanitizeFileName</c>
        /// 与 <c>PathUtil.UniquePath</c> 加工，和网盘上的原始文件名（= <c>TaskItem.Name</c>）
        /// 经常不相等：同名文件已存在时会加 <c> (1)</c> 后缀（实测：用户 32 条历史里有 2 条如此），
        /// 含 <c>\ / : * ? " &lt; &gt; |</c> 的会被替换成 <c>_</c>。对不上 ⇒ 车道永远不亮。</para>
        /// <para>完整路径在"同时下载的任务"之间天然唯一（<c>DownloadScheduler</c> 的同路径互斥
        /// 也是拿它当 key），所以做匹配键最合适。</para>
        /// </summary>
        public string Dest { get; set; }
        /// <summary>即时速度（字节/秒，EWMA 平滑值）。</summary>
        public double Speed { get; set; }
        /// <summary>当前活跃连接数。</summary>
        public int ActiveConnections { get; set; }
        /// <summary>本次任务实际启用的并发上限。</summary>
        public int WorkerCount { get; set; }
        /// <summary>已完成分片数。</summary>
        public int PartsDone { get; set; }
        /// <summary>分片总数。</summary>
        public int PartsTotal { get; set; }
        /// <summary>累计已写的字节数。</summary>
        public long DoneBytes { get; set; }
        /// <summary>文件总大小（0 = 未知）。</summary>
        public long TotalBytes { get; set; }
        /// <summary>累计抢占次数（慢连接被掐掉的次数）。</summary>
        public int PreemptedCount { get; set; }
        /// <summary>是否处于「边下边播」的合并模式。</summary>
        public bool MergeMode { get; set; }
        /// <summary>合并模式下已按序合并的字节数。</summary>
        public long MergedBytes { get; set; }
        /// <summary>对端 IP 分布描述（"1.2.3.4×120, 5.6.7.8×3"，空串 = 无样本）。</summary>
        public string PeerIps { get; set; }
        /// <summary>是否已判定为停滞。</summary>
        public bool Stalled { get; set; }
        /// <summary>快照时间。</summary>
        public DateTime Timestamp { get; set; }
    }
}
