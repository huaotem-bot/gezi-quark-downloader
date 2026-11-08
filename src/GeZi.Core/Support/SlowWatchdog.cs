using System;
using System.Collections.Generic;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 慢连接看门狗的**纯计算**部分（无 I/O、无状态、可自检）。
    ///
    /// <para>
    /// 🚨🚨 2026-11-07 重做的原因（用户实测日志坐实）：旧判据是「绝对下限」，
    /// 按网盘给 8~16 KB/s，只要连接速度低于它就把**所有**连接判慢、全部断开重连。
    /// 一旦服务端把整条 IP 的供给压到 13.5 KB/s/连接（用户 2026-11-07 20:07 日志实测），
    /// 就出现自伤循环：
    /// </para>
    /// <list type="number">
    /// <item>13.5 KB/s &lt; 夸克绝对下限 16 KB/s → 全员判慢；</item>
    /// <item>但「整体限速」要求全员低于 12 KB/s 才触发 → 13.5 又刚好不够低；</item>
    /// <item>于是 OverallSlowActive 恒为 false、绝对下限全程生效；</item>
    /// <item>每一轮上千次断开重连 → TCP+TLS 握手 200~600ms 占掉 3~5% 时间 → 平均值更低。</item>
    /// </list>
    /// <para>
    /// 用户日志里 747 次「连接被强制关闭或取消，无法访问已释放的对象」
    /// （SslStream 399 + OpenChunkStream 348）就是这条循环留下的。
    /// </para>
    ///
    /// <para>
    /// 新判据：**只跟同伴比**。判慢线 = 同时在飞连接速度的**中位数** × 比例（默认 0.5），
    /// 再与一条 4 KB/s 的「死亡底线」取较大值。
    /// </para>
    /// <list type="bullet">
    /// <item>全员 13.5 KB/s → 中位数 13.5 → 判慢线 6.75 KB/s → 全员合格，零重连 ✓</item>
    /// <item>9 条 4 KB/s + 160 条 14 KB/s → 中位数 14 → 判慢线 7 → 只抓那 9 条 ✓</item>
    /// <item>真死掉的（0.5 KB/s）无论中位数多少都低于 4 KB/s 死亡底线 → 抓 ✓</item>
    /// </list>
    /// <para>
    /// 🚨 **为什么必须用中位数而不是平均值**：平均值会被少数快连接拉高。
    /// 用户日志原文「170/254 路都低于阈值 12.0 KB/s（本轮实测平均单连接 33.0 KB/s）」
    /// —— 平均值 33 KB/s 时判慢线 16.5 KB/s，那 170 路（大多 12~14 KB/s）全部落在线下。
    /// 中位数不受极值影响，代表「典型同伴」的速度，才是判断「某条连接是否掉队」的正确参照系。
    /// </para>
    /// </summary>
    public static class SlowWatchdog
    {
        /// <summary>
        /// 死亡底线：低于这条线的连接，不管同伴多慢都值得换一条。
        ///
        /// <para>
        /// 4 KB/s 的由来：正常 CDN 单连接最低档也有 40~80 KB/s（见 AGENTS.md 第四节实测），
        /// 掉到 4 KB/s 只能是这条 TCP 落了坏节点或已被 RST 半边。
        /// </para>
        /// </summary>
        public const long DeathFloorBps = 4L * 1024;

        /// <summary>中位数默认采样下限：少于 2 个样本时中位数没有统计意义。</summary>
        public const int MinMedianSamples = 2;

        /// <summary>
        /// 求中位数。空集合返回 0；单个样本返回该样本。
        ///
        /// <para>
        /// 不做任何平滑：采样窗口本身就是 2 秒一轮，已经足够稳；
        /// 平滑会让阈值滞后于真实变化。
        /// </para>
        /// </summary>
        /// <param name="values">各连接本轮速度（字节/秒），负数按 0 计入。</param>
        /// <returns>中位数；样本为空返回 0。</returns>
        public static double Median(IList<double> values)
        {
            if (values == null || values.Count == 0)
                return 0;

            var sorted = new List<double>(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                double v = values[i];
                sorted.Add(v > 0 ? v : 0);
            }
            sorted.Sort();

            int n = sorted.Count;
            if ((n & 1) == 1)
                return sorted[n / 2];
            return (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
        }

        /// <summary>
        /// 由中位数算出供看门狗使用的动态判慢线（字节/秒）。
        /// </summary>
        /// <param name="medianBps">中位数速度；≤0 表示本轮样本不足、返回 0（调用方应保留上一轮的值）。</param>
        /// <param name="ratio">判慢比例（默认 0.5）；≤0 视为关闭相对判据、返回 0。</param>
        /// <returns>动态判慢线；0 表示「本轮不更新」。</returns>
        public static long DynamicFloorFromMedian(double medianBps, double ratio)
        {
            if (medianBps <= 0 || ratio <= 0)
                return 0;
            return (long)(medianBps * ratio);
        }

        /// <summary>
        /// 本轮实际生效的判慢线。
        ///
        /// <para>
        /// 🚨 **要害**：动态线有效时，**绝不把按网盘分档的绝对下限 max 进来** ——
        /// 只要还把 16 KB/s 的绝对线取 max，全员 13.5 KB/s 就还是会被全判慢，白改一场。
        /// 绝对下限只在「还没测出中位数」时兜底（刚开始、样本不足）。
        /// </para>
        /// </summary>
        /// <param name="dynamicFloor">动态判慢线（中位数 × 比例）；0 表示本轮没有。</param>
        /// <param name="absoluteFloor">按网盘分档的绝对下限（兜底用）。</param>
        /// <param name="deathFloor">死亡底线，默认 <see cref="DeathFloorBps"/>。</param>
        /// <returns>生效的判慢线，恒 ≥ deathFloor（deathFloor ≤0 时按 <see cref="DeathFloorBps"/>）。</returns>
        public static long EffectiveFloor(long dynamicFloor, long absoluteFloor, long deathFloor)
        {
            long death = deathFloor > 0 ? deathFloor : DeathFloorBps;
            long chosen = dynamicFloor > 0 ? dynamicFloor : absoluteFloor;
            return chosen > death ? chosen : death;
        }

        /// <summary>重连成本的保守估计（毫秒）：TCP + TLS 握手 + 请求头，实测 200~600ms。</summary>
        public const long ReconnectCostMs = 1000;

        /// <summary>
        /// 收尾期的重连成本门槛（毫秒）。2026-11-08 新增：收尾只剩最后几片时，
        /// 每条慢连接反复「断开重连」是负收益（换连接也大概率还是慢、还白丢握手时间），
        /// 所以把「值得换连接」的门槛从 1 秒抬高到 5 秒 —— 只有「按当前速度还要下 &gt; 5 秒」
        /// 才换，几十 KB 的小尾巴就让它硬下完。
        /// </summary>
        public const long EndgameReconnectCostMs = 5000;

        /// <summary>
        /// 判慢之后，这条连接**到底值不值得**断开重连。
        ///
        /// <para>
        /// 🚨🚨 2026-11-08 修「收尾卡死、必须暂停再恢复才动」（用户实测坐实）：
        /// 旧判据写在 <c>ChunkDownloader</c> 里，是
        /// <c>bps &gt; 0 &amp;&amp; (EmptyLength / bps) &gt; 2.0</c>，两个致命缺陷：
        /// </para>
        /// <list type="number">
        /// <item><description>
        /// **窗口内读到 0 字节时 bps == 0**，第一项直接 false ⇒ 一条彻底挂死的连接
        /// 永远不会被判慢、永远不重连，只能干等 <c>Chunk.Timeout</c>。
        /// 用户日志「11:18:16 3.39 MB/s → 11:18:36 414 KB/s → 卡 28 秒无任何进展，
        /// 手动暂停再恢复立刻跑完」正是它：最后的尾巴片挂在一条死连接上，看门狗闭着眼。
        /// </description></item>
        /// <item><description>
        /// 固定 2 秒基准偏高。收尾每片只剩几十 KB，按 6 KB/s 算剩余时间远小于 2 秒
        /// ⇒ 一律判「不值得重连」，于是收尾全是慢连接在啃，速度越掉越低。
        /// 重连成本实测 200~600ms，所以剩余 1 秒以上就该换。
        /// </description></item>
        /// </list>
        /// <para>
        /// 新判据：
        /// · 已经下完（剩余 ≤ 0）→ 不重连；
        /// · 窗口内 0 字节 → 只要还有剩余就重连（死连接不能等超时）；
        /// · 否则「按当前速度还要下 &gt; <see cref="ReconnectCostMs"/>」才重连。
        /// </para>
        /// </summary>
        /// <param name="bps">本窗口实测速度（字节/秒）。</param>
        /// <param name="remainingBytes">该分片剩余字节数。</param>
        /// <param name="costMs">重连成本估计（毫秒）；≤0 时用 <see cref="ReconnectCostMs"/>。</param>
        public static bool ShouldReconnect(double bps, long remainingBytes, long costMs)
        {
            if (remainingBytes <= 0)
                return false;                       // 已经下完，没什么可换的

            long cost = costMs > 0 ? costMs : ReconnectCostMs;
            if (bps <= 0)
                return true;                        // 窗口内 0 字节 = 彻底挂死，必须换

            double remainMs = remainingBytes * 1000.0 / bps;
            return remainMs > cost;
        }

        /// <summary><see cref="ShouldReconnect(double,long,long)"/> 的默认成本重载。</summary>
        public static bool ShouldReconnect(double bps, long remainingBytes)
        {
            return ShouldReconnect(bps, remainingBytes, ReconnectCostMs);
        }
    }
}
