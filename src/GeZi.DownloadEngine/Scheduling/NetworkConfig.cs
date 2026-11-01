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
using System.Net;
using System.Threading;

namespace GeZi.DownloadEngine
{
    /// <summary>.NET Framework 网络参数调优：默认值对下载器很不友好，必须显式调整。</summary>
    public static class NetworkConfig
    {
        /// <summary>
        /// 线程池"最小工作线程数"的下限。
        /// 自从文件流改用 <c>FileOptions.Asynchronous</c>（写盘走 overlapped IO、不占线程），
        /// worker 已不需要独占线程，所以**不再按连接数预创建线程**。
        /// 保留这个下限是为了应对仍然同步的 DNS 解析（每次新建连接一次）。
        /// </summary>
        private const int ThreadPoolMinFloor = 64;

        public static void Apply(int connBudget = 64)
        {
            try
            {
                // 全局连接预算。注意这不只是"够不够用"的问题：
                // .NET Framework 里它同时是 ServicePoint 的 DefaultConnectionLimit，
                // 任何没显式设置 MaxConnectionsPerServer 的 HttpClient 都会受它约束。
                if (connBudget < 64) connBudget = 64;
                if (connBudget > 8192) connBudget = 8192;
                ServicePointManager.DefaultConnectionLimit = connBudget;
                // 每次新建连接都刷新 DNS：夸克 CDN 有多个 IP，这样才能命中不同节点提速
                ServicePointManager.DnsRefreshTimeout = 0;
                // 省掉 Expect: 100-continue 的一次往返
                ServicePointManager.Expect100Continue = false;
                // 关闭 Nagle 算法，降低小请求（API 调用）延迟
                ServicePointManager.UseNagleAlgorithm = false;
                // 空闲连接 2 分钟后再回收，利于复用
                ServicePointManager.MaxServicePointIdleTime = 120000;

                // ---- 线程池预热 ----
                // 这条对高并发下载至关重要。.NET 线程池的"最小工作线程数"默认等于
                // CPU 核数（本机实测 16），之后每注入一个新线程要约 1~2 秒。
                // 512 个 worker 同时要线程时，要等好几分钟才能全部就位 ——
                // 表现是"点开始后界面几乎不动，过一会儿才慢慢提速"，
                // 用户多半会以为又卡死了。
                // 关键：这里要按"单文件最大并发"来算，而不是按"全局连接预算"。
                // 两者含义不同：前者是单个任务能开多少条连接，后者是同时下载多个
                // 任务时的总连接上限。线程池要能立刻喂饱前者，所以取两者较大值更稳。
                // 直接引用 ChunkSplitter.MaxRealConcurrency（单文件并发上限），
                // 保证"所有档位都能立刻拿到线程"这件事由同一个常量驱动，
                // 不会因为改了别处而失配。
                // 原来这里设的是 max(connBudget, 512)，理由是
                // "512 个 worker 要立刻拿到线程"。**但那个前提已经不成立**：
                // 文件流现在用 FileOptions.Asynchronous 打开，写盘走 overlapped IO，
                // **不再占用线程**；worker 全程 await（HTTP / 写盘都是异步），
                // 只在 IO 完成回调时短暂借用线程池。
                // 继续预创建 512 个线程只会带来：上下文切换开销、
                // GC 的 stop-the-world 要暂停 512 个线程、每个线程的栈占用
                // —— 实测表现就是"CPU 占用非常高"。
                // 仍保留一个适中下限：.NET Framework 的 DNS 解析是同步的，
                // 错峰建连时会有短暂的线程需求（每次新建连接一次解析）。
                int min = Math.Max(ThreadPoolMinFloor, Environment.ProcessorCount * 8);
                if (min > 32767) min = 32767;
                int ioMin;
                ThreadPool.GetMinThreads(out _, out ioMin);
                ThreadPool.SetMinThreads(min, Math.Max(ioMin, min));
            }
            catch { }
        }
    }
}
