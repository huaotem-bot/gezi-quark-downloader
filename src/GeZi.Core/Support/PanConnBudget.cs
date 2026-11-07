// GeZi —— 多网盘下载器 (Multi-cloud downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 按网盘划分的**连接预算**（纯计算，无 I/O、无静态可变状态）。
    ///
    /// <para>
    /// 🚨🚨 为什么必须按网盘分开（2026-11-07 修「多网盘同时下载时互相侵占」）：
    /// 原来全程序只有一个静态 <c>SemaphoreSlim(512)</c>，夸克、UC、阿里、迅雷**共用**。
    /// 但四家的服务端配额是**各自独立**结算的：
    /// </para>
    /// <list type="bullet">
    /// <item>夸克：按**账号**限总连接，512 条能跑 20+ MB/s（实测）。</item>
    /// <item>UC：按**单 IP 总带宽** 7~8 MB/s，超过 256 条每条摊薄到 13 KB/s。</item>
    /// <item>阿里：按**单 IP** 2.5~2.7 MB/s，64 条已到顶。</item>
    /// <item>迅雷：按**单 IP** 封 16 条，再多 CDN 大面积 503。</item>
    /// </list>
    /// <para>
    /// 也就是说「夸克开 512 + 迅雷开 16」在服务端是两笔账，互不影响。
    /// 共用一个 512 只会让它们互相抢额度：
    /// 迅雷先占住 16 条后，夸克最多只能拿 496 条，还要再被
    /// 「单任务不超过平均份额 2 倍」夹一道 ⇒ <b>夸克永远拿不到 512</b>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 上面四条是**正常态**的实测值，也是安全默认。
    /// 🚨 2026-11-07 用户要求「四家全部放开，最高 3000」做实测，
    /// 所以 <see cref="Quark"/> / <see cref="Uc"/> / <see cref="Aliyun"/> / <see cref="Xunlei"/>
    /// 现在**统一是 3000**（= <c>ChunkSplitter.MaxRealConcurrency</c>），
    /// 各家服务端真实限额交给运行时自己去撞，日志里「在飞 vs 目标」就是撞出来的结果。
    /// 这是**测试期取值**，不是新的实测结论；要恢复安全默认就把四个常量改回
    /// 512 / 256 / 64 / 16（原始依据见 AGENTS.md 第四节、记忆/59-整体限速与慢启动、
    /// 记忆/4-融合引擎与落盘.md:617-625）。
    /// </para>
    /// </summary>
    public static class PanConnBudget
    {
        /// <summary>
        /// 夸克：测试期全放开到 3000（与 <c>ChunkSplitter.MaxRealConcurrency</c>、
        /// <c>ThreadAllocator.DefaultBudget</c> 同步）。正常态实测值是 512。
        /// </summary>
        public const int Quark = 3000;

        /// <summary>UC：测试期全放开到 3000。正常态实测值是 256（单 IP 7~8 MB/s）。</summary>
        public const int Uc = 3000;

        /// <summary>阿里云盘：测试期全放开到 3000。正常态实测值是 64（单 IP 2.5~2.7 MB/s）。</summary>
        public const int Aliyun = 3000;

        /// <summary>迅雷云盘：测试期全放开到 3000。正常态实测值是 16（单 IP 封 16 条，再多 503）。</summary>
        public const int Xunlei = 3000;

        /// <summary>支持的网盘数量（PanKind: Quark=0 / Uc=1 / Aliyun=2 / Xunlei=3）。</summary>
        public const int PanCount = 4;

        /// <summary>
        /// 每个任务开始时**必须阻塞拿到**的连接数下限（保证任务能真正跑起来）。
        /// 与 <c>DownloadScheduler</c> 里的 <c>MinPerFileThreads</c> 保持同一个数。
        /// </summary>
        public const int MinBlockingFloor = 16;

        /// <summary>
        /// 把任意整数归一成合法的 PanKind 下标。未知值一律当夸克（默认网盘）。
        /// </summary>
        public static int NormalizePanKind(int panKind)
        {
            return (panKind < 0 || panKind >= PanCount) ? 0 : panKind;
        }

        /// <summary>
        /// 这个网盘可用的**总连接预算**。未知网盘按夸克算。
        /// </summary>
        public static int BudgetFor(int panKind)
        {
            switch (NormalizePanKind(panKind))
            {
                case 1: return Uc;
                case 2: return Aliyun;
                case 3: return Xunlei;
                default: return Quark;
            }
        }

        /// <summary>
        /// 单任务软上限：不超过「本网盘预算 ÷ 本网盘同时在跑的任务数 × 2」，
        /// 且不超过本网盘预算本身。
        ///
        /// <para>
        /// 乘 2 的由来：允许单个任务高于平均份额（防大文件被摊平），
        /// 但不能高到把同网盘其它任务饿死。真正的硬上限是每网盘信号量本身。
        /// </para>
        /// </summary>
        public static int SoftCap(int panBudget, int panRunning)
        {
            if (panBudget <= 0) return 1;
            int running = panRunning < 1 ? 1 : panRunning;
            long share = panBudget / running;
            if (share < 1) share = 1;
            long soft = share * 2;
            if (soft > panBudget) soft = panBudget;
            if (soft < 1) soft = 1;
            return (int)soft;
        }

        /// <summary>
        /// 单任务开始时**阻塞等待**的连接条数。
        ///
        /// <para>
        /// 平时恒为 <see cref="MinBlockingFloor"/>（16），与改造前逐字等价；
        /// 只有「本网盘同时在跑的任务数 × 16 超过本网盘预算」时才会降到
        /// <c>预算 ÷ 任务数</c>。
        /// </para>
        ///
        /// <para>
        /// 🚨 为什么必须降：迅雷预算只有 16 条。两个迅雷任务同时开始时，
        /// 若双方都坚持阻塞拿 16 条，第二个会**一直等第一个整体下完**才启动，
        /// 界面表现就是「排队中 / 0 B/s」的假死。降到 8+8 才是正确行为 ——
        /// 服务端对该 IP 的总配额本来就只有 16，两个任务本来就该各分一半。
        /// </para>
        ///
        /// <para>
        /// 其余情况（夸克 512、UC 256、阿里 64 配 3 个任务）算出来都 ≥ 16，
        /// 结果仍是 16 ⇒ 对既有实测行为**零影响**。
        /// </para>
        /// </summary>
        public static int BlockingMinimum(int panBudget, int panRunning)
        {
            if (panBudget <= 0) return 1;
            int running = panRunning < 1 ? 1 : panRunning;
            int share = panBudget / running;
            if (share < 1) share = 1;
            return share < MinBlockingFloor ? share : MinBlockingFloor;
        }
    }
}
