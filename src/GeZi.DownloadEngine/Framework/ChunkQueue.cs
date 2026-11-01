// GeZi 鸽子下载器 — 多网盘下载器
// Copyright (C) 2026 huaotem-bot
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// 本文件是下载引擎的分片队列。
// 设计目标：让「正在播放的那一段」优先下载（边下边看），
// 同时保持原有 FIFO 顺序（分片按偏移顺序入队，对机械盘天然友好）。

using System;
using System.Collections.Generic;

namespace GeZi.DownloadEngine.Framework
{
    /// <summary>
    /// 两级分片队列：高优先级（播放窗口内）+ 普通（其余）。
    ///
    /// 可见性说明：这里刻意用 <c>public</c> 而不是 <c>internal</c> ——
    /// 自检项目（GeZi.SelfTest）要直接验证重排逻辑，
    /// 而重排错了的表现是「播放位置的数据迟迟不下」，属于难复现的调度问题，
    /// 必须在自检里锁死，不能只靠端到端测速。
    ///
    /// 为什么不用两个 <c>ConcurrentQueue</c>：优先窗口是**运行期可变的**
    /// （用户随时可能拖动播放进度），需要能对已在普通队列里、尚未开始下载的分片
    /// 做「提升」操作。ConcurrentQueue 不支持按条件移除，所以这里用 List + 锁。
    ///
    /// 竞争程度很低：入队发生在开始时和抢占切分时（每 2 秒最多几次），
    /// 出队发生在 worker 领任务时（每个分片一次），锁内只做 List 操作，不含 I/O。
    /// </summary>
    public sealed class ChunkQueue
    {
        private readonly List<Chunk> _normal = new List<Chunk>();
        private readonly List<Chunk> _priority = new List<Chunk>();
        private readonly object _gate = new object();

        /// <summary>队列里还剩多少分片（两级合计）。</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                    return _normal.Count + _priority.Count;
            }
        }

        /// <summary>进普通队列（FIFO）。</summary>
        public void Enqueue(Chunk chunk)
        {
            if (chunk == null)
                return;
            lock (_gate)
                _normal.Add(chunk);
        }

        /// <summary>进高优先级队列。</summary>
        public void EnqueuePriority(Chunk chunk)
        {
            if (chunk == null)
                return;
            lock (_gate)
                _priority.Add(chunk);
        }

        /// <summary>
        /// 取一个待下载分片：先取高优先级队列，空了再取普通队列。
        /// 高优先级队列内部仍保持入队顺序（播放窗口内也从头往后下，符合播放器读顺序）。
        /// </summary>
        public bool TryTake(out Chunk chunk)
        {
            lock (_gate)
            {
                if (_priority.Count > 0)
                {
                    chunk = _priority[0];
                    _priority.RemoveAt(0);
                    return true;
                }
                if (_normal.Count > 0)
                {
                    chunk = _normal[0];
                    _normal.RemoveAt(0);
                    return true;
                }
            }
            chunk = null;
            return false;
        }

        /// <summary>
        /// 把落在 [start, end] 区间内、**尚未开始下载**的分片从普通队列提升到高优先级队列。
        ///
        /// 只提升 Position == 0 的分片：已经在下的分片如果被移到优先队列，
        /// 等于把「下一轮才轮到」的任务提前，没有意义（它已经占着一条连接），
        /// 反而会让 Reorder 反复搬运同一批对象。
        ///
        /// 返回被提升的分片数量（仅用于日志/诊断）。
        /// </summary>
        public int Reorder(long start, long end)
        {
            if (start < 0 || end < start)
                return 0;

            lock (_gate)
            {
                if (_normal.Count == 0)
                    return 0;

                int moved = 0;
                // 逆序删除，避免下标漂移
                for (int i = _normal.Count - 1; i >= 0; i--)
                {
                    Chunk c = _normal[i];
                    if (c == null)
                    {
                        _normal.RemoveAt(i);
                        continue;
                    }

                    // 与播放窗口无交集的分片跳过
                    if (c.End < start || c.Start > end)
                        continue;

                    // 已经开始下载的不搬（它已有连接）
                    if (c.Position > 0)
                        continue;

                    _normal.RemoveAt(i);
                    _priority.Add(c);
                    moved++;
                }

                if (moved > 1)
                {
                    // 优先队列内按起点排序，保证播放器读顺序 = 下载顺序
                    _priority.Sort((a, b) => a.Start.CompareTo(b.Start));
                }
                return moved;
            }
        }

        /// <summary>
        /// 清空（取消/重新开始时用）。返回清掉的分片数。
        /// </summary>
        public int Clear()
        {
            lock (_gate)
            {
                int n = _normal.Count + _priority.Count;
                _normal.Clear();
                _priority.Clear();
                return n;
            }
        }
    }
}
