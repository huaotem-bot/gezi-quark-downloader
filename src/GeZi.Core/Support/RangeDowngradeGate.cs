using System;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 「服务端忽略 Range 回了 200」的容忍闸门。
    ///
    /// <para>
    /// 🚨🚨 2026-11-07 新增的原因：原来只要有一个分片拿到 200 而不是 206，
    /// 立刻抛 <c>FatalChunkException</c> → 整个任务失败 **并且清掉续传文件**
    /// （<c>DownloadService</c> 里 <c>exp is FatalChunkException → ClearResumeAsync()</c>）。
    /// 一个几百 MB 的文件下到 90%，只因为某个 CDN 节点偶发忽略了一次 Range，
    /// 前面几十分钟全部作废 —— 这是「大文件下载出错」里最伤人的一条。
    /// </para>
    ///
    /// <para>
    /// 对照 <c>refs/YunX</c> 的做法（<c>DownloadManager.RANGE_IGNORED_TOLERANCE = 3</c>）：
    /// 前 3 次只把这一片当失败重试，第 4 次才认定「服务端真的不支持 Range」并回退单流。
    /// 本类实现同样的语义，但**多加一个时间窗口**：
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>
    /// 没有窗口的话，一个下了三小时的文件，中途偶发三次 200（每次隔半小时）
    /// 也会累加成「超容忍」被作废。这不是我们想要的 —— 偶发就该一直容忍下去。
    /// </item>
    /// <item>
    /// 有窗口（默认 60 秒）才能区分两种情况：
    /// <b>偶发</b>（几十分钟一次，永不触发）与
    /// <b>真降级</b>（同一时刻几十条连接全收到 200，几秒内就攒够 3 次）。
    /// </item>
    /// </list>
    ///
    /// <para>
    /// 线程安全：内部用锁；多个分片同时收到 200 时按真实发生次数累加。
    /// </para>
    /// </summary>
    public sealed class RangeDowngradeGate
    {
        /// <summary>默认容忍次数（与 refs/YunX 的 RANGE_IGNORED_TOLERANCE 一致）。</summary>
        public const int DefaultTolerance = 3;

        /// <summary>
        /// 默认统计窗口（毫秒）。60 秒的依据：慢启动爬满 512 路约需 3~5 秒，
        /// 真降级时一个窗口内必然攒够容忍数；而偶发场景的间隔是分钟级。
        /// </summary>
        public const long DefaultWindowMs = 60_000;

        private readonly object _gate = new object();
        private int _count;
        private long _firstMs;

        /// <summary>本窗口内已发生的次数。</summary>
        public int Count
        {
            get { lock (_gate) { return _count; } }
        }

        /// <summary>重置（每次开始新下载时调，避免上次的残留影响这次）。</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _count = 0;
                _firstMs = 0;
            }
        }

        /// <summary>
        /// 记录一次「服务端回 200」。
        /// </summary>
        /// <param name="nowMs">当前时间（毫秒，调用方用 Environment.TickCount）。</param>
        /// <param name="tolerance">容忍次数，≤0 时用 <see cref="DefaultTolerance"/>。</param>
        /// <param name="windowMs">统计窗口，≤0 时用 <see cref="DefaultWindowMs"/>。</param>
        /// <returns>
        /// <c>true</c> = 窗口内已超过容忍次数，应当按「服务端真的不支持 Range」处理；
        /// <c>false</c> = 还在容忍范围内，调用方应把这一片当普通失败重试。
        /// </returns>
        public bool Observe(long nowMs, int tolerance, long windowMs)
        {
            int tol = tolerance > 0 ? tolerance : DefaultTolerance;
            long win = windowMs > 0 ? windowMs : DefaultWindowMs;

            lock (_gate)
            {
                // 首次发生，或已经超出窗口 → 重新起算。
                // ⚠️ 窗口过期必须**重置计数**而不是只换起点：
                //    否则一次持续一小时的低频偶发（每分钟一次）也会累加到 60，
                //    等于没有窗口。
                if (_count == 0 || nowMs - _firstMs > win || nowMs < _firstMs)
                {
                    _count = 1;
                    _firstMs = nowMs;
                }
                else
                {
                    _count++;
                }

                return _count > tol;
            }
        }
    }
}
