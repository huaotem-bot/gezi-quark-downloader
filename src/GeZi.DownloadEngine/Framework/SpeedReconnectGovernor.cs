// GeZi - 夸克/UC/阿里/迅雷 网盘下载器
// Copyright (C) 2025 GeZi Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Threading;

namespace GeZi.DownloadEngine.Framework;

/// <summary>
/// 全局「慢连接重连」节流器 —— 防止重连风暴。
///
/// <para>
/// 慢连接检测是**每个分片各自独立**判断的：16 条连接里只要有几条同时变慢，
/// 就会同时决定重连。但重连本身有代价（关连接、TCP 握手、TLS 握手、
/// 重发 HTTP 请求头），如果 16 条一起重连，这几秒内的吞吐会直接掉到 0，
/// 而且新连接还要重新慢启动 —— 比不重连更糟。
/// </para>
///
/// <para>
/// 所以这里给「因太慢而重连」这个动作加一个全局预算：任意滚动
/// <see cref="WindowMs"/> 毫秒内最多放行 <see cref="MaxPerWindow"/> 次。
/// 被拒绝的分片不重连，继续慢慢啃（或者等抢占机制把它的剩余区间切走），
/// 下一轮检测时再试。这样重连是**错峰**发生的，不会集体抖动。
/// </para>
///
/// <para>
/// 注意：这里只限制「因慢而重连」。出错重连（超时、503、连接被重置）
/// 不受此限 —— 那种情况本来就没法继续，必须立刻重试。
/// </para>
/// </summary>
internal static class SpeedReconnectGovernor
{
    /// <summary>滚动窗口长度（毫秒）。</summary>
    public const int WindowMs = 10000;

    /// <summary>一个窗口内最多允许几次「因慢而重连」。</summary>
    public const int MaxPerWindow = 4;

    private static long _windowStartTicks;
    private static int _countInWindow;

    /// <summary>
    /// 申请一次「因慢而重连」的许可。
    /// </summary>
    /// <returns>拿到许可返回 true；被节流返回 false（调用方应放弃本次重连）。</returns>
    public static bool TryAcquire()
    {
        long now = DateTime.UtcNow.Ticks;

        // 用 CAS 循环决定「谁负责开新窗口」。
        // 原来的写法是「读到过期 → 无条件 Exchange 起点 + 清零计数」：
        // 两个线程同时判定过期时，后到的那个会把先到者刚 Increment 的计数
        // 一起清零，等于白送一次许可；高并发下这个窗口形同虚设。
        // 现在只有 CAS 成功的那个线程才清零，其余线程重新读一遍再走正常路径。
        while (true)
        {
            long start = Interlocked.Read(ref _windowStartTicks);
            long elapsedMs = start == 0 ? long.MaxValue : (now - start) / TimeSpan.TicksPerMillisecond;

            if (elapsedMs < WindowMs)
            {
                int used = Interlocked.Increment(ref _countInWindow);
                return used <= MaxPerWindow;
            }

            if (Interlocked.CompareExchange(ref _windowStartTicks, now, start) == start)
            {
                Interlocked.Exchange(ref _countInWindow, 0);
                int used = Interlocked.Increment(ref _countInWindow);
                return used <= MaxPerWindow;
            }
            // 别人抢先重置了窗口，重读状态再来一轮。
        }
    }

    /// <summary>测试用：把窗口和计数清零。</summary>
    public static void Reset()
    {
        Interlocked.Exchange(ref _windowStartTicks, 0);
        Interlocked.Exchange(ref _countInWindow, 0);
    }
}
