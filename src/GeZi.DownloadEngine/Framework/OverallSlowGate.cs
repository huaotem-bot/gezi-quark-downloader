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

namespace GeZi.DownloadEngine.Framework
{
    /// <summary>
    /// 「整体限速」判定器：判断当前这轮「大部分连接同时变慢」到底是
    /// **服务端按 IP 的整体配额低**，还是**个别连接掉队**。
    ///
    /// <para>
    /// 🚨🚨 为什么必须有它（2026-11-05 恢复老引擎机制）：
    /// 融合引擎迁移时把这个判定丢了，只剩「某片低于平均值 × 0.34 就换连接 / 切片」。
    /// 但当服务端按 IP 整体限速时，**每条连接都同样慢** ——
    /// 按相对阈值看谁都不掉队（大家一起慢，平均值也低），
    /// 按绝对下限看又全部超线 → 于是 256 条连接集体被判慢、集体重连、
    /// 回来还是同一个速度，带宽全烧在 TCP 握手和 HTTP 请求头上。
    /// </para>
    ///
    /// <para>
    /// 用户实测日志（2026-11-05）：19 分钟里 151 次慢重连，
    /// 实测速度全部聚集在 10221~12269 B/s（分片 24=10239、52=10234、78=10226），
    /// 而 256 × 10KB/s ≈ 2.5 MB/s —— 正好是阿里/UC 的单 IP 配额。
    /// 这种「全员整齐地慢」就是整体限速的典型特征。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 这个判定**只用于抑制慢连接抢占**，绝不能外泄给别的机制。
    /// 老引擎里曾用它去拦「假死抢救」，结果造成自我锁定：
    /// 整体限速 → 不抢救 → 速度归零 → 更确信整体限速。
    /// </para>
    ///
    /// <para>
    /// 抽成独立类型是为了**能自检** —— <c>PreemptRound</c> 要真实分片和连接才能跑，
    /// 而判定规则本身是纯逻辑（样本数 / 慢数 / 是否收尾 / 时间），可以脱离下载验证。
    /// </para>
    /// </summary>
    public class OverallSlowGate
    {
        /// <summary>抑制窗口的截止时刻（UTC 毫秒）。0 = 从未判定过。</summary>
        private long _holdUntilMs;

        /// <summary>「整体限速中」提示是否已经打过（只打一次，避免每 2 秒刷屏）。</summary>
        private bool _logged;

        /// <summary>当前是否处于整体限速状态（含粘滞窗口）。</summary>
        public bool Active { get; private set; }

        /// <summary>是否已经打过「整体限速中」的日志。</summary>
        public bool Logged { get { return _logged; } }

        /// <summary>抑制窗口的截止时刻（UTC 毫秒），0 表示从未判定过。</summary>
        public long HoldUntilMs { get { return _holdUntilMs; } }

        /// <summary>
        /// 判定一轮。
        /// </summary>
        /// <param name="sampled">本轮有效样本数（能算出速度的连接数）。</param>
        /// <param name="slowCount">其中低于阈值的条数。</param>
        /// <param name="inflight">当前在飞连接数（用来判收尾期）。</param>
        /// <param name="endgameInflight">收尾期门槛：在飞 ≤ 本值即视为收尾。</param>
        /// <param name="minSamples">判定所需的最少样本数。</param>
        /// <param name="holdMs">判定成立后的粘滞时长（毫秒）。</param>
        /// <param name="nowMs">当前 UTC 毫秒。</param>
        /// <param name="newlyLogged">
        /// 输出：本次调用**刚刚**打出「整体限速中」提示（调用方据此决定是否写日志）。
        /// </param>
        /// <returns>
        /// <c>true</c> = 本轮应当**跳过抢占**（整体限速中，抢占无意义且有害）；
        /// <c>false</c> = 正常抢占。
        /// </returns>
        public bool Evaluate(int sampled, int slowCount, int inflight, int endgameInflight,
            int minSamples, int holdMs, long nowMs, out bool newlyLogged)
        {
            newlyLogged = false;

            // 收尾期（在飞连接很少）：**不做**整体限速判定，永远允许抢占。
            //   ① 样本太少（1~3 路），统计上不可靠 —— 1/1 路慢就会被判成「全都慢」；
            //   ② 收尾恰恰最需要抢占 —— 只剩几路在磨，那几路的速度就是用户看到的总速度，
            //      换节点远比继续干等便宜。
            //   这两个机制曾互相打架（实测：进度 19/20、在飞 1 路时被误判整体限速而放弃抢占，
            //   最后一片只能干等）。
            bool endgame = inflight <= endgameInflight;
            if (endgame)
            {
                // 收尾期不受抑制窗口约束，直接放行抢占。
                // ⚠️ 这里**不能**清 _logged：留着它，调用方的 ClearLogged() 才能
                //    打出「整体限速已缓解」那条日志。清了就静默恢复，用户看不到。
                Active = false;
                return false;
            }

            // 样本不足（多在启动爬坡期）：**既不判定也不抢占**。
            // 刚建好的连接还没跑起来，瞬时速度天然接近 0 ——
            // 老引擎实测踩过：启动 6 秒就报「335/339 路都低于阈值」，
            // 那不是真实速度，是连接还没开始传数据。
            if (sampled < minSamples)
                return true;

            // 抑制窗口（粘滞）：判定一旦成立就稳定压住 holdMs，期间只观察不动手。
            // 慢连接是抖动的 —— 这一轮 8/10 慢、下一轮 3/10 慢，每轮独立判定就会在
            // 「判定整体限速 → 恢复抢占 → 又判定整体限速」之间反复横跳。
            bool systemic = nowMs < _holdUntilMs;

            // 过半连接同时低于阈值 → 整体限速。
            // 用「慢数 × 2 ≥ 样本数」而不是「慢数 > 样本数/2」：
            // 后者在样本为奇数时会漏掉恰好一半的情况（5 路里 3 路慢应当算整体限速）。
            if (slowCount * 2 >= sampled)
            {
                systemic = true;
                _holdUntilMs = nowMs + holdMs;
                if (!_logged)
                {
                    _logged = true;
                    newlyLogged = true;
                }
            }

            Active = systemic;
            return systemic;
        }

        /// <summary>
        /// 标记「整体限速已缓解」（调用方据此写日志）。返回是否真的从「限速中」变成「已缓解」。
        /// </summary>
        public bool ClearLogged()
        {
            if (!_logged) return false;
            _logged = false;
            return true;
        }

        /// <summary>重置为初始状态（续传重来 / 自检用）。</summary>
        public void Reset()
        {
            _holdUntilMs = 0;
            _logged = false;
            Active = false;
        }
    }
}
