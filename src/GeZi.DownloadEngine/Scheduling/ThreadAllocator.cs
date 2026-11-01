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
using System.Collections.Generic;
using GeZi.Core.Api;
using GeZi.Core.Models;
using GeZi.Core.Support;

namespace GeZi.DownloadEngine
{
    /// <summary>
    /// 把「总连接预算」按**文件大小比例**分给一批下载任务。
    /// Python Flet 版有「智能调度」：`实际线程数 = min(下载线程数, 总连接预算 / 本批任务数)`
    /// —— 多个文件**平分**总预算。C# 版当初删掉「全局连接预算」设置项时，
    /// 连"多文件共享总预算"这件事一起没了，变成每个任务都开满自己的线程数
    /// （单文件设 512、同时跑 3 个 = 1536 条连接，而夸克是按账号限总连接数的）。
    /// 「不要平均分配了，改成比例分配，按文件大小，文件越大分配越多，
    /// 文件越小分配越少，但有最低下限，不然别让小文件根本下不了了」。
    /// 所以：<c>threads_i = clamp(预算 × size_i / Σsize, 下限, 用户设的单文件并发数)</c>
    /// 抽成独立静态类是为了**可测试** —— 纯数学，不碰 UI 也不碰网络，
    /// 验证宿主可以直接跑一组用例（见 tmp-links）。
    /// </summary>
    public static class ThreadAllocator
    {
        /// <summary>
        /// 总连接预算。夸克按账号限制总连接数，Python 版实测 512 为最优总量。
        /// </summary>
        public const int DefaultBudget = 512;

        /// <summary>单个文件至少给几条连接 —— 保证小文件也下得动，不会被大文件挤成 0。</summary>
        public const int MinPerFile = 4;

        /// <summary>
        /// 按大小比例分配线程数，返回与入参等长的数组。
        /// </summary>
        /// <param name="sizes">每个任务的文件字节数（未知填 0 或负数）。</param>
        /// <param name="perFileCap">用户设的「单文件并发连接数」，同时是每项的上限。</param>
        /// <param name="budget">总预算。</param>
        /// <param name="minPerFile">每项下限。</param>
        /// <param name="concurrency">
        /// 同时最多跑几个任务（= 设置页的「同时下载任务数」）。
        /// **这个参数很关键**：预算约束的是"同时并发"的量级，而不是"整批"的账面总和。
        /// </param>
        /// <remarks>
        /// · **只有一个文件** → 直接给 <paramref name="perFileCap"/>（行为与改动前一致）。
        /// · **多个文件等大** → 退化成"平分"，与 Python 版行为一致。
        /// · **大小全未知** → 也退回平分，不会把所有任务饿到只剩下限。
        /// · 结果永远落在 [<paramref name="minPerFile"/>, <paramref name="perFileCap"/>]。
        /// 调度器用信号量限制**同时只跑
        /// <paramref name="concurrency"/> 个**任务，所以 N 个文件其实是分
        /// ⌈N/concurrency⌉ 批跑完的。如果预算只按 512 分给全部 N 个，
        /// 文件一多（比如 200 个）每份就被压到下限 4 路 —— 而同时其实只有 3 个在跑，
        /// 白白浪费了额度。所以这里把预算乘上"批数"：
        /// <c>有效预算 = budget × ⌈N / concurrency⌉</c>，
        /// 这样**同时**在跑的那几个加起来正好落在 budget 附近，而文件之间的
        /// **大小比例关系完全不变**（放大系数是统一的）。
        /// </remarks>
        public static int[] Allocate(IList<long> sizes, int perFileCap,
            int budget = DefaultBudget, int minPerFile = MinPerFile, int concurrency = 1)
        {
            int cap = Math.Max(1, perFileCap);
            if (minPerFile < 1) minPerFile = 1;
            if (minPerFile > cap) minPerFile = cap;

            int n = sizes == null ? 0 : sizes.Count;
            var result = new int[n];
            if (n == 0) return result;

            if (n == 1)
            {
                result[0] = cap;             // 单文件：保持原行为
                return result;
            }

            // 有效预算：按"要分几批才跑完"放大（见 remarks）
            int k = Math.Max(1, concurrency);
            int waves = (n + k - 1) / k;
            long effBudget = (long)budget * Math.Max(1, waves);

            long total = 0;
            for (int i = 0; i < n; i++)
                if (sizes[i] > 0) total += sizes[i];

            if (total <= 0)
            {
                // 大小未知 → 退回"平分"（Python 版行为），避免所有任务只吃下限
                long even = effBudget / n;
                if (even < minPerFile) even = minPerFile;
                if (even > cap) even = cap;
                for (int i = 0; i < n; i++) result[i] = (int)even;
                return result;
            }

            for (int i = 0; i < n; i++)
            {
                double share = sizes[i] > 0 ? effBudget * ((double)sizes[i] / total) : 0;
                int t = (int)Math.Round(share);
                if (t < minPerFile) t = minPerFile;   // 下限：小文件也要下得动
                if (t > cap) t = cap;                 // 上限：不超过用户设的单文件档位
                result[i] = t;
            }

            // 四舍五入会让总和略微越界（例如 512/3 → 171×3 = 513）。
            // 把超出部分从"当前最大"的项上扣回来，保证总量不超有效预算。
            // ⚠️ 扣的时候不能把任何一项压到下限以下 —— **下限优先于预算**：
            //    宁可总量略微超预算，也不能让小文件下不动（这正是用户强调的那句
            //    「不然别让小文件根本下不了了」）。
            long sum = 0;
            for (int i = 0; i < n; i++) sum += result[i];
            int guard = 0;
            while (sum > effBudget && guard++ < 100000)
            {
                int best = -1;
                for (int i = 0; i < n; i++)
                    if (result[i] > minPerFile && (best < 0 || result[i] > result[best]))
                        best = i;
                if (best < 0) break;   // 所有项都到下限了，无法再扣
                result[best]--;
                sum--;
            }

            // ---- 水位填充：把「因 perFileCap 封顶而省下的预算」继续分给未封顶的文件 ----
            // 为什么需要：按比例分配时，一旦某个文件被 perFileCap 封顶，
            // 它那一份就用不完了，而这部分预算会**白白闲置**。
            // 实测（用户日志复现，tmp-links 用例 alloc_waterfill）：
            //   30.78GB + 160.09MB、cap=256、conc=3
            //   → 大文件 256（撞 cap）、小文件 4（只吃下限）→ **只用掉 260/512，浪费 252**
            // 而夸克 CDN 是**按连接**限速的（docs/CDN并发阈值-实测.md：64→4.00、256→13.94 MB/s，
            // 拐点 256），闲置预算 = 白扔的带宽 ⇒ 160MB 小文件只有 4 路，要磨十几分钟。
            // ⚠️ 必须**保持大小比例**：slack 只在「未封顶的文件」之间按 size 比例分摊。
            //    绝不能平均加 —— 那会退化成"平均分配"，直接违背用户当初的要求
            //    「不要平均分配，按文件大小，大的多分小的少分，但有最低下限」。
            // ⚠️ 大小未知（size<=0）的文件不参与：它们已在上面走过"平分"分支，
            //    再灌预算只会把已知大小的份额挤掉。
            for (int round = 0; round < 64; round++)
            {
                long used = 0;
                for (int i = 0; i < n; i++) used += result[i];
                long slack = effBudget - used;
                if (slack <= 0) break;

                long activeSize = 0;
                for (int i = 0; i < n; i++)
                    if (sizes[i] > 0 && result[i] < cap) activeSize += sizes[i];
                if (activeSize <= 0) break;     // 全部封顶 / 全是未知大小 → 无料可分

                bool progressed = false;
                for (int i = 0; i < n; i++)
                {
                    if (sizes[i] <= 0 || result[i] >= cap) continue;
                    long add = slack * sizes[i] / activeSize;   // 按比例
                    if (add <= 0) continue;
                    long room = cap - result[i];
                    if (add > room) add = room;                 // 不许越过单文件上限
                    result[i] += (int)add;
                    progressed = true;
                }
                if (progressed) continue;

                // 比例分摊后 floor 全为 0（slack 已不够让每个 active 项各 +1）
                // → 按大小从大到小逐个 +1，把余数分完即止。
                // （不引 Linq：这是个纯数学类，保持零依赖；O(n²) 但只在余数分发现场跑一次。）
                for (int pass = 0; pass < n && slack > 0; pass++)
                {
                    int best = -1;
                    for (int i = 0; i < n; i++)
                    {
                        if (sizes[i] <= 0 || result[i] >= cap) continue;
                        if (best < 0 || sizes[i] > sizes[best]) best = i;
                    }
                    if (best < 0) break;
                    result[best]++;
                    slack--;
                }
                break;
            }

            return result;
        }
    }
}
