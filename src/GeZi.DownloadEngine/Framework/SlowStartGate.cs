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
using System.Threading;

namespace GeZi.DownloadEngine.Framework
{
    /// <summary>
    /// 慢启动闸门：**分批**放行 worker，而不是一次性把 256/512 条连接全捅出去。
    ///
    /// <para>
    /// 🚨🚨 为什么需要（2026-11-05，对齐 refs/gopeed 的 <c>slowStartController</c>）：
    /// 融合引擎的 worker 池是一次性 <c>Task.Run</c> 开满的，于是下载开始的那一瞬间
    /// 会同时发生：① 256 次 DNS 解析；② 256 次 TCP 握手；③ 256 次 TLS 握手；
    /// ④ 256 个 HTTP Range 请求。任何一个环节抖动（DNS 超时、CDN 判异常流量回 503），
    /// 都会让这一大批连接**同时**失败并同时退避，然后同时重连 —— 吞吐周期性地塌陷。
    /// </para>
    ///
    /// <para>
    /// gopeed 的做法是 1 → 2 → 4 → 16 → 256 的**平方增长**，每一批全部拿到响应才放下一批。
    /// 平方（而不是翻倍）是关键：翻倍要 8 步才到 256，平方只要 4 步，
    /// 爬坡延迟从数秒降到 1 秒出头，同时仍然保留了「先确认少量连接可用再放量」的保护。
    /// </para>
    ///
    /// <para>
    /// 抽成独立类型是为了能自检 —— 批次序列是纯算术，不需要真实下载就能验证边界
    /// （max=1 / max=16 / max=512 / 非 2 的幂 / 溢出保护）。
    /// </para>
    /// </summary>
    public class SlowStartGate
    {
        private readonly int _maxWorkers;
        private int _allowed;

        /// <summary>创建一个闸门。<paramref name="maxWorkers"/> 小于 1 时按 1 处理。</summary>
        public SlowStartGate(int maxWorkers)
        {
            _maxWorkers = maxWorkers < 1 ? 1 : maxWorkers;
            // 初始只放行 1 路：先确认「一条连接能通、能拿到数据」，再谈放量。
            _allowed = 1;
        }

        /// <summary>目标并发上限。</summary>
        public int MaxWorkers { get { return _maxWorkers; } }

        /// <summary>当前已放行的 worker 数。</summary>
        public int Allowed { get { return Volatile.Read(ref _allowed); } }

        /// <summary>是否已经放到上限。</summary>
        public bool Done { get { return Allowed >= _maxWorkers; } }

        /// <summary>
        /// 算出「再放一批」之后应该是多少路。纯计算，不改状态。
        ///
        /// <para>规则（与 refs/gopeed 的 <c>commitBatch</c> 一致）：</para>
        /// <list type="bullet">
        /// <item>1 → 2（1 的平方还是 1，必须特判，否则永远长不大）</item>
        /// <item>否则 → 当前值的平方，封顶到 <see cref="MaxWorkers"/></item>
        /// </list>
        /// </summary>
        public static int NextAllowed(int current, int maxWorkers)
        {
            int max = maxWorkers < 1 ? 1 : maxWorkers;
            if (current < 1) return 1;
            if (current >= max) return max;
            if (current == 1) return Math.Min(2, max);

            // 先判溢出再相乘：current² 在 max 很小时也可能超过 int.MaxValue
            //（虽然实际并发封顶 512，这里仍然写防御性代码）。
            if (current > max / current) return max;
            int next = current * current;
            return next > max ? max : next;
        }

        /// <summary>
        /// 推进到下一批。返回推进后的放行数；已经到上限时原样返回。
        /// </summary>
        public int Advance()
        {
            int cur = Volatile.Read(ref _allowed);
            int next = NextAllowed(cur, _maxWorkers);
            if (next > cur)
                Interlocked.Exchange(ref _allowed, next);
            return next;
        }

        /// <summary>直接放到满（下载失败重试 / 用户暂停后恢复时用，跳过爬坡）。</summary>
        public void OpenAll()
        {
            Interlocked.Exchange(ref _allowed, _maxWorkers);
        }

        /// <summary>
        /// 判断某个 worker（序号从 0 开始）是否已经被放行。
        /// </summary>
        public bool IsAllowed(int workerIndex)
        {
            return workerIndex < Allowed;
        }
    }
}
