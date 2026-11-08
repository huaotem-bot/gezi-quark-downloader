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

namespace GeZi.Core.Support
{
    /// <summary>
    /// 速度档位（设置页「网络与下载」卡片标题右侧的三档滑块）。
    ///
    /// <para>
    /// 🚨 2026-11-08 用户要求（原话）：「默认是标准，往右又可以选输出，再选就是超载…
    /// 标准模式就是让线程只能选择到最高 512，输出模式可以最高到 2048，
    /// 超载模式最高可选在线程至 4096」。
    /// 同日实测后经用户拍板定为：**标准 512 / 输出 1024 / 超载 3000**
    /// （日志证据：3000 路峰值 24.20 MB/s 是实测拐点，4096 路的 23.09 MB/s 反而更低，连接再多是负收益）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 语义是「**天花板**」而不是「预设值」：
    /// 选标准档时，四家网盘的下拉里 1024 / 2048 这些档位不出现（或者出现也选不出效果），
    /// 已配置的超额值会被夹到 512 —— 这样「界面能选、代码却夹不到」的假承诺不会出现。
    /// 引擎的物理上限仍是 <c>ChunkSplitter.MaxRealConcurrency</c>(4096)，两者取 min 生效。
    /// </para>
    ///
    /// <para>
    /// 抽成 Core 纯函数是为了能自检：三档的取值、夹取、越界归一都是纯算术，
    /// 不需要 WPF 也不需要真实下载就能穷举验证。
    /// </para>
    /// </summary>
    public static class SpeedTier
    {
        /// <summary>标准档：滑块白色，单文件最高 512 条连接（等于正常态夸克实测拐点）。</summary>
        public const int Standard = 0;

        /// <summary>输出档：滑块金色，单文件最高 1024 条连接。</summary>
        public const int Boost = 1;

        /// <summary>超载档：滑块红色，单文件最高 3000 条连接。</summary>
        public const int Overload = 2;

        /// <summary>档位数量（滑块的分段数）。</summary>
        public const int TierCount = 3;

        /// <summary>标准档的并发天花板（512）。</summary>
        public const int StandardCeiling = 512;

        /// <summary>输出档的并发天花板（1024）。</summary>
        public const int BoostCeiling = 1024;

        /// <summary>
        /// 超载档的并发天花板（3000）。
        /// <para>
        /// 🚨 2026-11-08 用户拍板定为 3000：日志实测 3000 路峰值 24.20 MB/s，
        /// 是四档里唯一**正向收益**的拐点（512→5.96 / 2048→20.16 / 3000→24.20 / 4096→23.09 MB/s），
        /// 4096 条连接对夸克已经是**负收益**。所以天花板取实测拐点 3000，不放 4096。
        /// 引擎物理上限 <c>ChunkSplitter.MaxRealConcurrency</c> 仍是 4096，
        /// 这里只是「界面可选到的天花板」，两者取 min 生效。
        /// </para>
        /// </summary>
        public const int OverloadCeiling = 3000;

        /// <summary>
        /// 把任意整数归一到合法档位。负数 / 0 以下 → 标准档，超过 2 → 超载档。
        /// 老配置里没有这个字段时反序列化成 0，正好等于「标准」，**升级行为不变**。
        /// </summary>
        public static int Normalize(int tier)
        {
            if (tier < Standard) return Standard;
            if (tier > Overload) return Overload;
            return tier;
        }

        /// <summary>某一档允许的最大并发连接数。</summary>
        public static int Ceiling(int tier)
        {
            switch (Normalize(tier))
            {
                case Boost: return BoostCeiling;
                case Overload: return OverloadCeiling;
                default: return StandardCeiling;
            }
        }

        /// <summary>档位中文名（界面滑块的三段文案与提示都用它，避免两处各写一份）。</summary>
        public static string Name(int tier)
        {
            switch (Normalize(tier))
            {
                case Boost: return "输出";
                case Overload: return "超载";
                default: return "标准";
            }
        }

        /// <summary>
        /// 一句话说明（显示在滑块下方）。文案里带具体上限，用户不用翻文档。
        /// </summary>
        public static string Hint(int tier)
        {
            switch (Normalize(tier))
            {
                case Boost:
                    return "输出：单文件最多 1024 条连接。连接多、并发高，适合大文件赶时间。";
                case Overload:
                    return "超载：单文件最多 3000 条连接。极限压榨带宽，对路由器和网盘风控压力最大。";
                default:
                    return "标准：单文件最多 512 条连接。最稳，也最不容易触发网盘限流。";
            }
        }

        /// <summary>
        /// 把期望连接数夹到当前档位的天花板。空值（≤0）原样返回，交给上层走「回落基准」逻辑。
        /// </summary>
        public static int Clamp(int threads, int tier)
        {
            if (threads <= 0) return threads;
            int ceiling = Ceiling(tier);
            return threads > ceiling ? ceiling : threads;
        }

        /// <summary>
        /// 某个档位下「界面上应该出现哪些并发档位」。用于三档滑块与四网盘下拉联动：
        /// 标准档只留 ≤512 的档位，输出档留到 1024，超载档留到 3000。
        /// </summary>
        public static int[] VisiblePresets(int[] allPresets, int tier)
        {
            if (allPresets == null || allPresets.Length == 0) return new int[0];
            int ceiling = Ceiling(tier);
            int n = 0;
            for (int i = 0; i < allPresets.Length; i++)
            {
                if (allPresets[i] <= ceiling) n++;
            }
            // 一个都不满足时至少保留最小那档，避免下拉变空。
            if (n == 0)
            {
                int smallest = allPresets[0];
                for (int i = 1; i < allPresets.Length; i++)
                {
                    if (allPresets[i] < smallest) smallest = allPresets[i];
                }
                return new[] { smallest };
            }
            var result = new int[n];
            int k = 0;
            for (int i = 0; i < allPresets.Length; i++)
            {
                if (allPresets[i] <= ceiling) result[k++] = allPresets[i];
            }
            return result;
        }
    }
}
