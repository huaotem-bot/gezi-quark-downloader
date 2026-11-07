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
    /// 下载调度的纯计算规则（放在 Core 是为了让 GeZi.SelfTest 能直接断言边界，且不反向依赖 Providers）。
    ///
    /// <para>
    /// 🚨 本次抽取是**行为等价重构**：块大小算法此前已在 DownloadService.AdaptiveTailSliceBytes 里实现
    /// （对齐 YunX 的 TARGET_SECONDS=5 / MAX_ELASTIC_BLOCK=4MB）。本类只是把同一段算式变成可单测的纯函数，
    /// 供未来接线使用；本轮**没有**改变任何调用点的取值，也没有新增任何默认开启的能力。
    /// </para>
    ///
    /// <para>
    /// 硬约束：所有方法必须是纯函数（无 I/O、无静态可变状态、无副作用），
    /// 且「速度测不出来」时一律返回 0，调用方必须保持原值 —— 绝不把块算成 0 或极小值。
    /// </para>
    /// </summary>
    public static class DownloadSizing
    {
        /// <summary>速度自适应块的秒数支点（对齐对照来源的 TARGET_SECONDS）。</summary>
        public const double TargetSeconds = 5.0;

        /// <summary>自适应块的绝对上限（对齐对照来源的 MAX_ELASTIC_BLOCK）。</summary>
        public const long MaxSliceBytes = 4L * 1024 * 1024;

        /// <summary>缺口的默认等价上界（与现有固定并发上限一致，见 ThreadAllocator.DefaultBudget）。</summary>
        public const int DefaultInflightCap = 512;

        /// <summary>共写写合并：机械盘单批最多多少个包（现状值，逐字等价）。</summary>
        public const int HddBatchMaxCount = 512;

        /// <summary>共写写合并：机械盘单批最多多少字节（现状值，逐字等价）。</summary>
        public const long HddBatchMaxBytes = 64L * 1024 * 1024;

        /// <summary>
        /// <summary>
        /// 🚨 2026-11-07 修「单文件吃不满线程」：算连接预算的批次分母。
        /// 单文件批次按 1 算（不该被分母压到 ~170）；多任务保持原均分逻辑。
        /// 抽成纯函数便于自检钉死，避免以后又被改回去。
        /// </summary>
        public static int PlannedConcurrency(int jobCount, int maxConcurrent)
        {
            int planned = jobCount < maxConcurrent ? jobCount : maxConcurrent;
            if (planned < 1) planned = 1;
            if (jobCount == 1) planned = 1;
            return planned;
        }

        /// <summary>
        /// 🚨 2026-11-07：把配置里的「落盘方式」旧值归一到共写(1)。
        /// 用户明确「不要顺序写了」；老 GeZi.config.xml 里可能存 0 / -1 / Sequential / Auto，
        /// 一律归一，绝不抛异常（这个函数存在的意义就是不让一份老配置毁掉其他字段）。
        /// 抽到 Core 是为了让自检能直接覆盖，不依赖 WPF 主程序。
        /// </summary>
        public static int NormalizeStorageMode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 1;
            string v = raw.Trim();
            int n;
            if (int.TryParse(v, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out n))
                return n <= 0 ? 1 : n;
            // 所有老枚举名 / Auto / 未知值一律共写。
            return 1;
        }

        /// <summary>
        /// 🚨 2026-11-07：混合网盘批次里，**某一桶**能用的连接预算。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 「继续下载」一批里可能同时有夸克、UC、阿里、迅雷的记录，
        /// 必须按网盘分桶各算一次分配（见 <c>MainWindow.AllocateThreadsByPan</c>）。
        /// </para>
        /// <para>
        /// ⚠️ 关键在于 **不能每个桶都给满 512**：<c>ThreadAllocator.Allocate</c> 在
        /// <c>n == 1</c> 时会直接返回 <c>perFileCap</c>（源码注释「单文件：保持原行为」），
        /// 四个桶各 1 个文件就是 夸克 512 + UC 256 + 阿里 64 + 迅雷 16 = 848 路，
        /// 直接绕过 512 总预算。
        /// </para>
        /// <para>
        /// ⇒ 按桶内任务数占比分摊。单桶（= 单网盘批，整批都在一家）时占比为 1、
        /// 得满额，**与改造前逐字等价**。
        /// </para>
        /// <para>
        /// ⚠️ 这里**不做** <c>⌈n/concurrency⌉</c> 的批次放大 ——
        /// <c>Allocate</c> 内部已经按同一口径放大过 <c>effBudget</c>，外部再乘就是双重放大。
        /// </para>
        /// </remarks>
        /// <param name="totalBudget">总连接预算（= <c>ThreadAllocator.DefaultBudget</c> 512）。</param>
        /// <param name="bucketCount">这一桶里有多少个任务。</param>
        /// <param name="totalCount">整批任务总数（所有桶之和）。</param>
        /// <param name="minPerFile">每文件下限，保证小批不会被算成 0。</param>
        /// <returns>该桶可用的预算，恒 ≥ <paramref name="minPerFile"/>。</returns>
        public static int PanBucketBudget(int totalBudget, int bucketCount, int totalCount, int minPerFile)
        {
            if (totalBudget <= 0) return minPerFile > 0 ? minPerFile : 1;
            if (totalCount <= 0 || bucketCount <= 0) return minPerFile > 0 ? minPerFile : 1;
            if (bucketCount > totalCount) bucketCount = totalCount;

            long b = (long)totalBudget * bucketCount / totalCount;
            int r = b > int.MaxValue ? int.MaxValue : (int)b;
            if (r < minPerFile) r = minPerFile;
            if (r > totalBudget) r = totalBudget;
            return r;
        }

        /// <summary>
        /// 速度自适应末段补片粒度：块 ≈ 单连接 5 秒能下完的量，夹在 [minBytes, MaxSliceBytes]。
        /// </summary>
        /// <param name="avgPerConnBps">平均单连接速度（字节/秒）；≤0 表示还没测出来。</param>
        /// <param name="minBytes">下限（调用方传 TailRefillMinBytes，默认 256KB）。</param>
        /// <returns>算出的块大小；0 表示「速度测不出来，调用方保持原值」。</returns>
        public static long AdaptiveSliceBytes(double avgPerConnBps, long minBytes)
        {
            if (avgPerConnBps <= 0 || minBytes <= 0)
                return 0;

            double want = avgPerConnBps * TargetSeconds;
            if (want < minBytes) want = minBytes;
            if (want > MaxSliceBytes) want = MaxSliceBytes;
            return (long)want;
        }

        /// <summary>
        /// 在飞分片配额推导（对照来源按其可用内存推导、封顶 512 的思路）。
        ///
        /// <para>
        /// 🚨 本轮**仅提供纯函数**，默认产出固定值 512，与现状逐字等价；
        ///    是否按可用内存接线属于待确认项，本轮不接。
        /// </para>
        /// </summary>
        /// <param name="availableBytes">可用内存字节数；≤0 表示拿不到，退回默认等价。</param>
        /// <param name="bytesPerInflight">单个在飞分片的内存预算（读缓冲 + 重排窗口摊销）。</param>
        /// <returns>推导出的在飞上限；恒不小于 1，且不大于 DefaultInflightCap。</returns>
        public static int InflightCap(long availableBytes, long bytesPerInflight)
        {
            // 默认等价：拿不到内存信息或预算非法时，一律返回现有固定上限。
            // 这样未接线的调用方拿到的值与改造前完全相同。
            if (availableBytes <= 0 || bytesPerInflight <= 0)
                return DefaultInflightCap;

            long byBudget = availableBytes / bytesPerInflight;
            if (byBudget < 1) byBudget = 1;
            if (byBudget > DefaultInflightCap) byBudget = DefaultInflightCap;
            return (int)byBudget;
        }

        /// <summary>尾部收缩：剩余量不足 workers×块时，把块压到 remaining/workers（下限 minBytes，上限 base）。</summary>
        public static long TailShrink(long remaining, int workers, long baseSlice, long minBytes)
        {
            if (remaining <= 0 || baseSlice <= 0 || minBytes <= 0) return 0;
            int w = workers < 1 ? 1 : workers;
            long limit = 0;
            try { limit = (long)w * baseSlice; } catch { return 0; }
            long result;
            if (remaining <= limit)
            {
                result = remaining / w;
                if (result < minBytes) result = minBytes;
                if (result > baseSlice) result = baseSlice;
            }
            else
            {
                result = baseSlice;
            }
            return result;
        }
    }
}
