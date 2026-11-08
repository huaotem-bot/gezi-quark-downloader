using System;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 连接数爬山控制器（带迟滞）。
    ///
    /// 背景（2026-11-08 用户实测日志）：
    ///   同一台机器、同一个 443MB 文件，4096 路 24.20 MB/s、2048 路 20.16 MB/s、
    ///   512 路 5.96 MB/s —— 说明在「服务端还肯给」的区间里，加连接是**线性有效**的；
    ///   但一旦某家网盘当天把单 IP 配额压到底（例如中位数只剩 5.4 KB/s），
    ///   继续堆 4096 路不但不涨，反而因为 NAT 会话表 / 路由器队列溢出丢包而变慢。
    ///
    /// 所以不能永远用「用户设多少就开多少」，也不能盲目减半（减到 512 可能直接掉 4 倍）。
    /// 正确做法是**爬山**：小步加、看总量涨不涨、不涨就退回拐点并记住。
    ///
    /// 三条硬约束（用户 2026-11-08 明确指定）：
    ///   ① 每 <see cref="AdjustIntervalMs"/>（30 秒）最多调一次，绝不允许来回抖；
    ///   ② 只有「加完 N 路总量反而降 15% 以上」才判定加错了、要回退；
    ///   ③ 记住拐点（<see cref="Ceiling"/>），后续不再越过它，避免反复试探同一堵墙。
    ///
    /// 纯计算、无 I/O、无计时器 —— 时间由调用方以 <c>nowMs</c> 传入，所以能被自检逐条断言。
    /// </summary>
    public sealed class ConnectionClimber
    {
        /// <summary>两次调整的最小间隔（毫秒）。用户指定 30 秒。</summary>
        public const int AdjustIntervalMs = 30000;

        /// <summary>每次上调的比例（百分比）。25% 是「一次加得够看出来、又不至于一下跳太多」的折中。</summary>
        public const int StepUpPercent = 25;

        /// <summary>判定「加错了」的容许下降比例（百分比）。用户指定 15%。</summary>
        public const int RevertDropPercent = 15;

        /// <summary>总速度连续这么多毫秒为 0 视为「彻底停滞」，允许立刻降一档（不受 30 秒限制）。</summary>
        public const int StallResetMs = 20000;

        /// <summary>爬坡的最小连接数。低于这个数字就没有「加连接」的意义了。</summary>
        public const int MinTarget = 16;

        /// <summary>
        /// 拐点记忆的有效期（毫秒）。超过这个时间没人提起，就忘掉重新试探。
        ///
        /// 🚨 为什么必须有过期：CDN 抖一下就会让某 30 秒的总速度掉 15% 以上，
        ///    如果一次抖动就永久把 <see cref="Ceiling"/> 钉死在低位，
        ///    当天剩下的时间都爬不回去了 —— 那是最坏的自伤。
        ///    5 分钟是「够长到不会反复试探同一堵墙、够短到能跟上服务端策略变化」的折中。
        /// </summary>
        public const int CeilingForgetMs = 300000;

        private long _lastAdjustMs = -1;
        private long _lastTotalBps;
        private int _lastTarget;
        private int _ceiling = int.MaxValue;
        private long _ceilingSetMs = -1;
        private long _stallSinceMs = -1;

        /// <summary>当前建议的目标并发数。0 表示「还没开始爬，用调用方给的初值」。</summary>
        public int Target { get; private set; }

        /// <summary>已探明的拐点（曾在这里加过连接却变慢）。<see cref="int.MaxValue"/> 表示尚未探明。</summary>
        public int Ceiling { get { return _ceiling; } }

        /// <summary>上次调整时的总速度（字节/秒），用于下次比较。</summary>
        public long LastTotalBps { get { return _lastTotalBps; } }

        /// <summary>重置到未爬坡状态（换任务 / 用户手动改档位时调用）。</summary>
        /// <param name="startTarget">起始目标并发；&lt;= 0 表示等到第一次 Evaluate 再定。</param>
        public void Reset(int startTarget)
        {
            _lastAdjustMs = -1;
            _lastTotalBps = 0;
            _lastTarget = startTarget > 0 ? startTarget : 0;
            _ceiling = int.MaxValue;
            _ceilingSetMs = -1;
            _stallSinceMs = -1;
            Target = startTarget > 0 ? startTarget : 0;
        }

        /// <summary>彻底清空（含拐点记忆），用于用户改档位 / 换任务。</summary>
        public void ResetAll(int startTarget)
        {
            Reset(startTarget);
        }

        /// <summary>
        /// 喂一次观测，返回**新的目标并发数**（&lt;= 0 表示这次不调整，调用方沿用原值）。
        /// </summary>
        /// <param name="nowMs">当前时间（毫秒，可用 Environment.TickCount）。</param>
        /// <param name="totalBps">当前总速度（字节/秒）。</param>
        /// <param name="hardCeiling">硬上限（用户档位天花板），任何情况下不超过它。</param>
        /// <param name="inFlight">当前在飞连接数（用于判断「加了到底有没有真的开出来」）。</param>
        public int Evaluate(long nowMs, long totalBps, int hardCeiling, int inFlight)
        {
            if (hardCeiling < MinTarget)
                hardCeiling = MinTarget;

            if (_stallSinceMs < 0)
            {
                // 拐点过期：见 CeilingForgetMs 的注释，避免 CDN 抖一下就把当天钉死在低位。
                if (_ceiling != int.MaxValue && _ceilingSetMs >= 0
                    && nowMs - _ceilingSetMs >= CeilingForgetMs)
                {
                    _ceiling = int.MaxValue;
                    _ceilingSetMs = -1;
                }
            }

            // 第一次进来：从调用方给的初值起步，本轮回一个初值、不判涨跌。
            if (_lastAdjustMs < 0)
            {
                if (Target <= 0)
                    Target = hardCeiling;
                if (Target > hardCeiling)
                    Target = hardCeiling;
                _lastAdjustMs = nowMs;
                _lastTotalBps = totalBps;
                return Target;
            }

            // ── 停滞检测：总速度连续 20 秒为 0，说明当前这堆连接已经全废，
            //    立刻降一档重开（不受 30 秒限制）。这是「必须暂停再恢复才动」的对症解。
            if (totalBps <= 0)
            {
                if (_stallSinceMs < 0)
                {
                    _stallSinceMs = nowMs;
                }
                else if (nowMs - _stallSinceMs >= StallResetMs && inFlight > 0)
                {
                    _stallSinceMs = nowMs;
                    _lastAdjustMs = nowMs;
                    _lastTotalBps = 0;
                    int half = Target / 2;
                    if (half < MinTarget)
                        half = MinTarget;
                    if (half > hardCeiling)
                        half = hardCeiling;
                    Target = half;
                    return Target;
                }
                return 0;
            }
            _stallSinceMs = -1;

            // ── 30 秒节流：没到点就不动，绝不允许来回抖。
            if (nowMs - _lastAdjustMs < AdjustIntervalMs)
                return 0;

            long prevBps = _lastTotalBps;
            _lastAdjustMs = nowMs;
            _lastTotalBps = totalBps;

            // 上一轮没有基准（刚起步或刚停滞过），这一轮只记录、不判定。
            if (prevBps <= 0)
                return 0;

            // 总速度明显下降（超过容许比例）⇒ 判定「上一轮的加连接是错的」。
            //   用乘法避免浮点：(prev - now) / prev > 15%  <=>  now * 100 < prev * 85
            if (totalBps * 100 < prevBps * (100 - RevertDropPercent))
            {
                // 记住拐点：这次的目标就是上限，以后不再越过。
                if (_lastTarget > 0 && _lastTarget < _ceiling)
                {
                    _ceiling = _lastTarget;
                    _ceilingSetMs = nowMs;
                }

                int back = _lastTarget;
                if (back <= 0)
                    back = Target;
                if (back < MinTarget)
                    back = MinTarget;
                if (back > _ceiling)
                    back = _ceiling;
                if (back > hardCeiling)
                    back = hardCeiling;
                Target = back;
                return Target;
            }

            // 总速度没降（含小幅波动、含上涨）⇒ 说明还能吃，继续小步加。
            //   但如果「加完了在飞数根本没涨」，说明瓶颈不在我们这边（片不够分/服务端不接），
            //   再加也没用，保持不动。
            if (inFlight > 0 && inFlight < MinTarget)
                return 0;

            long next = (long)Target * (100 + StepUpPercent) / 100;
            if (next > hardCeiling)
                next = hardCeiling;
            if (next > _ceiling)
                next = _ceiling;
            if (next <= Target)
                return 0;

            _lastTarget = Target;
            Target = (int)next;
            return Target;
        }
    }
}
