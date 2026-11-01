// GeZi —— 多网盘下载器 (Multi-cloud downloader)
// Copyright (C) 2026  Yi Yuan
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/agpl-3.0>.

using System;
using System.Collections.Generic;
using System.Text;
using GeZi.Core.Api;
using GeZi.Core.Models;
using GeZi.Core.Support;

namespace GeZi.Providers.Qr
{
    /// <summary>
    /// 精简 QR 码编码器（纯 C#，零依赖）。
    /// 只实现登录场景所需的能力：byte 模式、自动选版、纠错等级 M、掩码自动择优。
    /// 输出布尔矩阵（true = 深色模块），由 UI 层自行绘制。
    ///
    /// 之所以自己写而不引 NuGet：单为一张登录二维码引入第三方库，
    /// 不值得为此增加打包体积与供应链面。
    /// </summary>
    public static class QrEncoder
    {
        /// <summary>编码结果为模块矩阵，true 表示深色。</summary>
        public static bool[,] Encode(string text)
        {
            if (string.IsNullOrEmpty(text))
                throw new ArgumentException("二维码内容不能为空", nameof(text));

            byte[] data = Encoding.UTF8.GetBytes(text);
            int version = ChooseVersion(data.Length);
            var (codewords, ecCount, blockGroups) = BuildCodewords(data, version);
            return BuildMatrix(codewords, version, ecCount, blockGroups);
        }

        /// <summary>
        /// 把内容编码为可直接显示的 24 位 BGR 位图数据（自下而上、每行按 4 字节对齐）。
        /// 返回的数据可直接喂给 System.Drawing.Bitmap，避免 UI 层再写一遍绘制代码。
        /// </summary>
        /// <param name="text">要编码的内容</param>
        /// <param name="scale">每个模块的像素边长（>=1）</param>
        /// <param name="quietZone">四周静默区的模块数，标准要求 >= 4</param>
        /// <param name="width">输出位图宽度（像素）</param>
        /// <param name="height">输出位图高度（像素）</param>
        /// <param name="stride">输出每行字节数（含对齐填充）</param>
        public static byte[] EncodeToBgr(string text, int scale, int quietZone,
            out int width, out int height, out int stride)
        {
            if (scale < 1) scale = 1;
            if (quietZone < 0) quietZone = 0;

            bool[,] m = Encode(text);
            int modules = m.GetLength(0);
            int side = modules + quietZone * 2;

            width = side * scale;
            height = side * scale;
            stride = ((width * 3 + 3) / 4) * 4;

            byte[] buf = new byte[stride * height];
            // 白色底
            for (int i = 0; i < buf.Length; i++) buf[i] = 0xFF;

            for (int mr = 0; mr < modules; mr++)
            {
                for (int mc = 0; mc < modules; mc++)
                {
                    if (!m[mr, mc]) continue;   // 浅色模块保持白色

                    int px0 = (mc + quietZone) * scale;
                    int py0 = (mr + quietZone) * scale;
                    for (int dy = 0; dy < scale; dy++)
                    {
                        // GDI+ 的位图数据是自下而上存储
                        int y = height - 1 - (py0 + dy);
                        int rowBase = y * stride + px0 * 3;
                        for (int dx = 0; dx < scale; dx++)
                        {
                            int off = rowBase + dx * 3;
                            buf[off] = 0x00;      // B
                            buf[off + 1] = 0x00;  // G
                            buf[off + 2] = 0x00;  // R
                        }
                    }
                }
            }
            return buf;
        }

        // 各版本（1..40）在纠错等级 M 下的数据码字数与每块容量
        // 为控制代码量，只列到版本 20（byte 模式下可容纳约 850 字节，远超登录 URL 需要）
        private static readonly int[] DataCodewordsM =
        {
            0,
            16, 28, 44, 64, 86, 108, 124, 154, 182, 216,
            254, 290, 334, 365, 415, 453, 507, 563, 627, 669
        };

        // 纠错等级 M 下每块的纠错码字数（与 EC 块数配合）
        private static readonly int[] EcCodewordsPerBlockM =
        {
            0,
            10, 16, 26, 18, 24, 16, 18, 22, 22, 26,
            30, 22, 22, 24, 24, 28, 28, 26, 26, 26
        };

        // 纠错等级 M 下的分块数（组 1 块数）
        private static readonly int[] BlockCountM =
        {
            0,
            1, 1, 1, 2, 2, 4, 4, 4, 5, 5,
            5, 8, 9, 9, 10, 10, 11, 13, 14, 16
        };

        private static int ChooseVersion(int byteLen)
        {
            // byte 模式：4 bit 模式指示 + 字符计数 + 数据 + 4 bit 结束符
            for (int v = 1; v < DataCodewordsM.Length; v++)
            {
                int capacityBits = DataCodewordsM[v] * 8;
                int countBits = v <= 9 ? 8 : 16;
                int needBits = 4 + countBits + byteLen * 8;
                if (needBits <= capacityBits)
                    return v;
            }
            throw new ArgumentException("内容过长，超出本编码器支持范围（版本 20）");
        }

        private sealed class BlockGroup
        {
            public int BlockCount;
            public int DataPerBlock;
            public int EcPerBlock;
        }

        private static (byte[] codewords, int ecCount, List<BlockGroup> groups) BuildCodewords(byte[] data, int version)
        {
            int totalData = DataCodewordsM[version];
            int blocks = BlockCountM[version];
            int ecPerBlock = EcCodewordsPerBlockM[version];

            // ---- 组装比特流 ----
            var bits = new List<bool>();
            AddBits(bits, 0b0100, 4);                    // byte 模式
            AddBits(bits, data.Length, version <= 9 ? 8 : 16);
            foreach (var b in data)
                AddBits(bits, b, 8);

            int capacity = totalData * 8;
            // 结束符最多 4 bit
            int term = Math.Min(4, capacity - bits.Count);
            for (int i = 0; i < term; i++)
                bits.Add(false);
            // 补齐到字节
            while (bits.Count % 8 != 0)
                bits.Add(false);

            // 填充字节 0xEC / 0x11 交替
            var dataCw = new List<byte>();
            for (int i = 0; i < bits.Count; i += 8)
            {
                int b = 0;
                for (int j = 0; j < 8; j++)
                    if (bits[i + j]) b |= 1 << (7 - j);
                dataCw.Add((byte)b);
            }
            bool pad = true;
            while (dataCw.Count < totalData)
            {
                dataCw.Add(pad ? (byte)0xEC : (byte)0x11);
                pad = !pad;
            }

            // ---- 分块 ----
            var groups = new List<BlockGroup>();
            int shortLen = totalData / blocks;
            int numLong = totalData % blocks;
            int numShort = blocks - numLong;
            if (numShort > 0)
                groups.Add(new BlockGroup { BlockCount = numShort, DataPerBlock = shortLen, EcPerBlock = ecPerBlock });
            if (numLong > 0)
                groups.Add(new BlockGroup { BlockCount = numLong, DataPerBlock = shortLen + 1, EcPerBlock = ecPerBlock });

            // ---- 逐块计算 RS 纠错 ----
            var dataBlocks = new List<byte[]>();
            var ecBlocks = new List<byte[]>();
            int pos = 0;
            foreach (var g in groups)
            {
                for (int i = 0; i < g.BlockCount; i++)
                {
                    var db = new byte[g.DataPerBlock];
                    Array.Copy(dataCw.ToArray(), pos, db, 0, g.DataPerBlock);
                    pos += g.DataPerBlock;
                    dataBlocks.Add(db);
                    ecBlocks.Add(CalcReedSolomon(db, g.EcPerBlock));
                }
            }

            // ---- 交织：先数据后纠错 ----
            var result = new List<byte>();
            int maxData = 0;
            foreach (var b in dataBlocks)
                maxData = Math.Max(maxData, b.Length);
            for (int i = 0; i < maxData; i++)
                foreach (var b in dataBlocks)
                    if (i < b.Length)
                        result.Add(b[i]);

            int maxEc = 0;
            foreach (var b in ecBlocks)
                maxEc = Math.Max(maxEc, b.Length);
            for (int i = 0; i < maxEc; i++)
                foreach (var b in ecBlocks)
                    if (i < b.Length)
                        result.Add(b[i]);

            return (result.ToArray(), ecPerBlock, groups);
        }

        private static void AddBits(List<bool> bits, int value, int len)
        {
            for (int i = len - 1; i >= 0; i--)
                bits.Add(((value >> i) & 1) != 0);
        }

        // ---- GF(256) 上的 Reed-Solomon ----
        private static readonly byte[] GfExp = new byte[512];
        private static readonly byte[] GfLog = new byte[256];

        static QrEncoder()
        {
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                GfExp[i] = (byte)x;
                GfLog[x] = (byte)i;
                x <<= 1;
                if (x >= 256) x ^= 0x11D;
            }
            for (int i = 255; i < 512; i++)
                GfExp[i] = GfExp[i - 255];
        }

        private static byte GfMul(byte a, byte b)
        {
            if (a == 0 || b == 0) return 0;
            return GfExp[GfLog[a] + GfLog[b]];
        }

        private static byte[] CalcReedSolomon(byte[] data, int ecCount)
        {
            var gen = GeneratorPoly(ecCount);
            var res = new byte[data.Length + ecCount];
            Array.Copy(data, res, data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                byte coef = res[i];
                if (coef == 0) continue;
                for (int j = 1; j < gen.Length; j++)
                    res[i + j] ^= GfMul(gen[j], coef);
            }
            var ec = new byte[ecCount];
            Array.Copy(res, data.Length, ec, 0, ecCount);
            return ec;
        }

        private static byte[] GeneratorPoly(int degree)
        {
            var poly = new byte[] { 1 };
            for (int i = 0; i < degree; i++)
            {
                var next = new byte[poly.Length + 1];
                for (int j = 0; j < poly.Length; j++)
                {
                    next[j] ^= poly[j];
                    next[j + 1] ^= GfMul(poly[j], GfExp[i]);
                }
                poly = next;
            }
            return poly;
        }

        // ---- 矩阵构建 ----

        private static bool[,] BuildMatrix(byte[] codewords, int version, int ecCount, List<BlockGroup> groups,
            int forceMask = -1)
        {
            int size = version * 4 + 17;
            var matrix = new bool[size, size];
            var reserved = new bool[size, size];

            PlaceFinders(matrix, reserved, size);
            PlaceTiming(matrix, reserved, size);
            PlaceAlignment(matrix, reserved, version, size);
            PlaceFormatReserve(reserved, size);
            if (version >= 7)
                PlaceVersionReserve(reserved, version, size);

            // 数据填充：从右下角开始、每次取两列、上下折返（之字形）。
            // 注意 M 列的处理：整个第 6 列是垂直时序图形，遇到时要整列跳过，
            // 且折返方向仍按"两列一组"推进（这就是列 6 处 col-- 的作用）。
            int bitIdx = 0;
            int totalBits = codewords.Length * 8;
            bool upward = true;
            for (int col = size - 1; col > 0; col -= 2)
            {
                // 第 6 列整列是垂直时序图形，不参与数据填充。
                // 标准做法是把"当前列"整体跳到 5，随后照常配对 (5,4)，
                // 而不是把本组改写成 (7,5)——后者会让第 5 列被填两次、bit 流错位。
                if (col == 6)
                    col = 5;

                int right = col;
                int left = col - 1;

                for (int i = 0; i < size; i++)
                {
                    int row = upward ? (size - 1 - i) : i;
                    // 每组的右列在前，左列在后
                    foreach (int c in new[] { right, left })
                    {
                        if (c < 0 || c >= size || reserved[row, c])
                            continue;
                        bool dark = false;
                        if (bitIdx < totalBits)
                        {
                            int bytePos = bitIdx >> 3;
                            int bitInByte = 7 - (bitIdx & 7);   // 高位在前
                            dark = ((codewords[bytePos] >> bitInByte) & 1) != 0;
                        }
                        matrix[row, c] = dark;
                        bitIdx++;
                    }
                }
                upward = !upward;
            }

            // 掩码择优：分别用 8 种掩码评分，取罚分最低的。
            // forceMask >= 0 时固定使用该掩码（仅调试用，用于与参考实现逐格比对）。
            int bestMask = 0;
            int bestScore = int.MaxValue;
            bool[,] best = null;
            for (int m = 0; m < 8; m++)
            {
                if (forceMask >= 0 && m != forceMask)
                    continue;
                var cand = (bool[,])matrix.Clone();
                ApplyMask(cand, reserved, size, m);
                PlaceFormat(cand, size, m);
                if (version >= 7)
                    PlaceVersion(cand, version, size);
                int score = Score(cand, size);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestMask = m;
                    best = cand;
                }
            }
            return best;
        }

        /// <summary>
        /// 放置 3 个定位图形（左上、右上、左下）。
        /// 注意：**没有右下角**——把两个坐标做笛卡尔积会凭空多出一个右下定位图形，
        /// 从而吃掉一大片数据区（这是最容易犯的错）。这里显式列出三个坐标。
        /// 每个图形本体 7×7，外加一圈分隔带，故偏移取 -1..7。
        /// </summary>
        private static void PlaceFinders(bool[,] m, bool[,] r, int size)
        {
            var corners = new[]
            {
                (0, 0),              // 左上
                (0, size - 7),       // 右上
                (size - 7, 0),       // 左下
            };

            foreach (var (pr, pc) in corners)
            {
                for (int i = -1; i <= 7; i++)
                    for (int j = -1; j <= 7; j++)
                    {
                        int rr = pr + i, cc = pc + j;
                        if (rr < 0 || rr >= size || cc < 0 || cc >= size)
                            continue;
                        // 本体 7×7：外框深色、内圈白色、中心 3×3 深色；
                        // 偏移为 -1 或 7 时属于分隔带（保持浅色，但仍算功能区）
                        bool dark = (i >= 0 && i <= 6 && (j == 0 || j == 6))
                                 || (j >= 0 && j <= 6 && (i == 0 || i == 6))
                                 || (i >= 2 && i <= 4 && j >= 2 && j <= 4);
                        if (i < 0 || i > 6 || j < 0 || j > 6)
                            dark = false;   // 分隔带恒为浅色
                        m[rr, cc] = dark;
                        r[rr, cc] = true;
                    }
            }
        }

        /// <summary>
        /// 时序图形（Timing Pattern）：第 6 行与第 6 列上黑白交替的定位参考线。
        /// 范围是 8..size-9（两端与定位图形相接处已被定位图形占用）。
        /// 同时把 (6,8)/(8,6) 这类与格式信息交叉的点也标记为已占用。
        /// </summary>
        private static void PlaceTiming(bool[,] m, bool[,] r, int size)
        {
            // 时序图形：第 6 行与第 6 列上，介于上下/左右定位图形之间的区间。
            // 定位图形占 0..6 与 size-7..size-1，故时序区间为 8 .. size-9（闭区间）。
            for (int i = 8; i <= size - 9; i++)
            {
                bool dark = (i % 2) == 0;
                if (!r[6, i]) { m[6, i] = dark; r[6, i] = true; }
                if (!r[i, 6]) { m[i, 6] = dark; r[i, 6] = true; }
            }
            // 与格式信息交叉的两点：(6,8) 与 (8,6)，取值固定不参与数据填充
            m[6, 8] = true; r[6, 8] = true;
            m[8, 6] = true; r[8, 6] = true;
        }

        private static void PlaceAlignment(bool[,] m, bool[,] r, int version, int size)
        {
            if (version == 1) return;
            var coords = AlignmentCoords(version, size);
            foreach (int gr in coords)
                foreach (int gc in coords)
                {
                    // 与三个定位图形（含分隔带）重叠的对齐图形要整块跳过。
                    // 判据必须是"图形矩形是否与定位区相交"，而不是只看中心点是否被占用——
                    // 只用中心点判断会在部分版本上误判，进而多占/少占数据区。
                    if (OverlapsFinder(gr, gc, size))
                        continue;

                    for (int i = -2; i <= 2; i++)
                        for (int j = -2; j <= 2; j++)
                        {
                            int rr = gr + i, cc = gc + j;
                            if (rr < 0 || rr >= size || cc < 0 || cc >= size) continue;
                            bool dark = Math.Max(Math.Abs(i), Math.Abs(j)) != 1;
                            m[rr, cc] = dark;
                            r[rr, cc] = true;
                        }
                }
        }

        /// <summary>
        /// 判断某个对齐图形中心 (gr, gc) 是否与三个定位图形之一重叠。
        /// 注意：定位图形只有左上、右上、左下三个，**右下角没有**，
        /// 所以判据必须是"两个坐标同时落在各自的定位区间内"，
        /// 而不能简单地认为"靠近任意边界就算重叠"——那样会把右下的对齐图形误删。
        /// 定位图形连同分隔带占 0..7 与 size-8..size-1。
        /// </summary>
        private static bool OverlapsFinder(int gr, int gc, int size)
        {
            // 三个定位图形只有左上、右上、左下（右下没有）。
            // 对齐图形本体 5×5，中心 (gr,gc)，覆盖 [gr-2,gr+2] × [gc-2,gc+2]；
            // 定位图形连同分隔带覆盖 [0,7] 或 [size-8,size-1]。
            const int half = 2;
            int r0 = gr - half, r1 = gr + half;
            int c0 = gc - half, c1 = gc + half;

            bool RowHitsTop = r0 <= 7;
            bool RowHitsBottom = r1 >= size - 8;
            bool ColHitsLeft = c0 <= 7;
            bool ColHitsRight = c1 >= size - 8;

            return (RowHitsTop && ColHitsLeft)
                || (RowHitsTop && ColHitsRight)
                || (RowHitsBottom && ColHitsLeft);
        }

        /// <summary>
        /// 各版本的对齐图形中心坐标（ISO/IEC 18004 附表）。
        /// 这张表是规范给定的，不要用公式去推导——推导很容易在部分版本上出错，
        /// 而一旦对齐图形坐标错位，就会吃掉/多占数据区，导致整个码无法解码。
        /// 索引 = version - 1。
        /// </summary>
        private static readonly int[][] AlignmentTable =
        {
            new int[0],                         // 1  （版本 1 无对齐图形）
            new[] { 6, 18 },                    // 2
            new[] { 6, 22 },                    // 3
            new[] { 6, 26 },                    // 4
            new[] { 6, 30 },                    // 5
            new[] { 6, 34 },                    // 6
            new[] { 6, 22, 38 },                // 7
            new[] { 6, 24, 42 },                // 8
            new[] { 6, 26, 46 },                // 9
            new[] { 6, 28, 50 },                // 10
            new[] { 6, 30, 54 },                // 11
            new[] { 6, 32, 58 },                // 12
            new[] { 6, 34, 62 },                // 13
            new[] { 6, 26, 46, 66 },            // 14
            new[] { 6, 26, 48, 70 },            // 15
            new[] { 6, 26, 50, 74 },            // 16
            new[] { 6, 30, 54, 78 },            // 17
            new[] { 6, 30, 56, 82 },            // 18
            new[] { 6, 30, 58, 86 },            // 19
            new[] { 6, 34, 62, 90 },            // 20
        };

        private static int[] AlignmentCoords(int version, int size)
        {
            if (version < 1 || version > AlignmentTable.Length)
                return new int[0];
            return AlignmentTable[version - 1];
        }

        /// <summary>
        /// 标记格式信息占用的格子，使其不被数据填充覆盖。
        /// 注意：第 8 行/第 8 列上穿插着时序图形（第 6 行/第 6 列），
        /// 格式信息在这些位置的落点是"跳开"的，不能简单地把整行整列都划进来。
        /// 这里按标准列出每个格式位的确切坐标。
        /// </summary>
        private static void PlaceFormatReserve(bool[,] r, int size)
        {
            // 副本 A：左上
            for (int c = 0; c <= 5; c++) r[8, c] = true;   // (8,0)..(8,5)
            r[8, 7] = true;                                 // (8,7)  跳过 (8,6) 时序
            r[8, 8] = true;                                 // (8,8)
            r[7, 8] = true;                                 // (7,8)
            for (int row = 0; row <= 5; row++) r[row, 8] = true;  // (0,8)..(5,8)

            // 副本 B：右上 + 左下
            for (int c = size - 8; c <= size - 1; c++) r[8, c] = true;
            for (int row = size - 7; row <= size - 1; row++) r[row, 8] = true;

            // 固定深色模块
            r[size - 8, 8] = true;
        }

        private static void PlaceVersionReserve(bool[,] r, int version, int size)
        {
            for (int i = 0; i < 6; i++)
                for (int j = size - 11; j < size - 8; j++)
                {
                    r[i, j] = true;
                    r[j, i] = true;
                }
        }

        private static void ApplyMask(bool[,] m, bool[,] r, int size, int mask)
        {
            for (int i = 0; i < size; i++)
                for (int j = 0; j < size; j++)
                {
                    if (r[i, j]) continue;
                    bool inv = mask switch
                    {
                        0 => (i + j) % 2 == 0,
                        1 => i % 2 == 0,
                        2 => j % 3 == 0,
                        3 => (i + j) % 3 == 0,
                        4 => (i / 2 + j / 3) % 2 == 0,
                        5 => (i * j) % 2 + (i * j) % 3 == 0,
                        6 => ((i * j) % 2 + (i * j) % 3) % 2 == 0,
                        7 => ((i + j) % 2 + (i * j) % 3) % 2 == 0,
                        _ => false,
                    };
                    if (inv)
                        m[i, j] = !m[i, j];
                }
        }

        /// <summary>
        /// 写入格式信息（15 位）。按 ISO/IEC 18004 定义：
        /// bit 0 是最低位，bit 14 是最高位。
        ///
        /// 两个副本的落位规则（这是最容易写错的地方）：
        /// - 副本 A（左上，需绕开定位图形）：
        ///     bits 0..5  -> (8, 0..5)
        ///     bit  6     -> (8, 7)
        ///     bit  7     -> (8, 8)
        ///     bit  8     -> (7, 8)
        ///     bits 9..14 -> (5..0, 8)   即 bit i 落在行 (14-i)
        /// - 副本 B：
        ///     bits 0..6  -> (size-1 .. size-7, 8)
        ///     bits 7..14 -> (8, size-8 .. size-1)
        /// </summary>
        private static void PlaceFormat(bool[,] m, int size, int mask)
        {
            int data = (0b00 << 3) | mask;      // 纠错等级 M = 00
            int bch = data << 10;
            const int gen = 0x537;
            for (int i = 4; i >= 0; i--)
                if (((bch >> (i + 10)) & 1) != 0)
                    bch ^= gen << i;
            int fmt = ((data << 10) | bch) ^ 0x5412;

            for (int i = 0; i < 15; i++)
            {
                // 位置 i=0 对应格式信息的最高位 b14（ISO/IEC 18004 图 25 的排布约定），
                // 因此要高位在前地取位，而不是从最低位开始。
                bool bit = ((fmt >> (14 - i)) & 1) != 0;

                // ---- 副本 A ----
                if (i <= 5) m[8, i] = bit;
                else if (i == 6) m[8, 7] = bit;
                else if (i == 7) m[8, 8] = bit;
                else if (i == 8) m[7, 8] = bit;
                else m[14 - i, 8] = bit;          // i=9..14 -> 行 5..0

                // ---- 副本 B ----
                if (i <= 6) m[size - 1 - i, 8] = bit;
                else m[8, size - 15 + i] = bit;   // i=7..14 -> 列 size-8..size-1
            }

            // 固定深色模块（格式信息旁边那个恒为深色的点）
            m[size - 8, 8] = true;
        }

        private static void PlaceVersion(bool[,] m, int version, int size)
        {
            int bch = version << 12;
            int gen = 0x1F25;
            for (int i = 5; i >= 0; i--)
                if (((bch >> (i + 12)) & 1) != 0)
                    bch ^= gen << i;
            int ver = (version << 12) | bch;

            for (int i = 0; i < 18; i++)
            {
                bool bit = ((ver >> i) & 1) != 0;
                int r = i / 3, c = i % 3;
                m[r, size - 11 + c] = bit;
                m[size - 11 + c, r] = bit;
            }
        }

        /// <summary>
        /// 掩码罚分（ISO/IEC 18004 §8.8.2 的四条规则）。
        /// 完整实现四条规则很重要：只算前两条时，评分会偏离标准，
        /// 挑出的"最优"掩码往往不是真正的低罚分掩码，部分解码器（如 OpenCV）
        /// 对非最优掩码的鲁棒性明显更差，会导致扫不出来。
        /// </summary>
        private static int Score(bool[,] m, int size)
        {
            int score = 0;

            // ---- 规则 1：同色连续 >= 5 个，每次计 3 + (长度 - 5) ----
            for (int i = 0; i < size; i++)
            {
                int runH = 1, runV = 1;
                for (int j = 1; j < size; j++)
                {
                    if (m[i, j] == m[i, j - 1]) { runH++; if (runH == 5) score += 3; else if (runH > 5) score++; }
                    else runH = 1;
                    if (m[j, i] == m[j - 1, i]) { runV++; if (runV == 5) score += 3; else if (runV > 5) score++; }
                    else runV = 1;
                }
            }

            // ---- 规则 2：2x2 同色块，每个计 3 ----
            for (int i = 0; i < size - 1; i++)
                for (int j = 0; j < size - 1; j++)
                {
                    bool c = m[i, j];
                    if (c == m[i, j + 1] && c == m[i + 1, j] && c == m[i + 1, j + 1])
                        score += 3;
                }

            // ---- 规则 3：出现 1:1:3:1:1 的定位图形相似图案（两侧带 4 个浅色），每处计 40 ----
            // 水平与垂直方向都要扫。
            for (int i = 0; i < size; i++)
            {
                for (int j = 0; j < size; j++)
                {
                    // 水平：dark light dark dark dark light dark
                    if (j + 6 < size && IsFinderLike(m, i, j, 0, 1))
                        score += 40;
                    // 垂直
                    if (i + 6 < size && IsFinderLike(m, i, j, 1, 0))
                        score += 40;
                }
            }

            // ---- 规则 4：深色模块占比偏离 50% 的惩罚 ----
            int dark = 0;
            for (int i = 0; i < size; i++)
                for (int j = 0; j < size; j++)
                    if (m[i, j]) dark++;
            int total = size * size;
            // 每偏离 5% 计 10 分（取向下取整的百分比差值 / 5）
            int percent = dark * 100 / total;
            int k = Math.Abs(percent - 50) / 5;
            score += k * 10;

            return score;
        }

        /// <summary>
        /// 判断从 (r,c) 出发、沿 (dr,dc) 方向是否为 1:1:3:1:1 图案，
        /// 且其前后至少一侧有 4 个连续浅色模块（标准要求的"静默区"上下文）。
        /// </summary>
        private static bool IsFinderLike(bool[,] m, int r, int c, int dr, int dc)
        {
            // 核心 7 格图案：深 浅 深 深 深 浅 深
            bool[] pat = { true, false, true, true, true, false, true };
            for (int k = 0; k < 7; k++)
            {
                int rr = r + dr * k, cc = c + dc * k;
                if (m[rr, cc] != pat[k]) return false;
            }

            // 前侧 4 格必须都是浅色
            bool beforeLight = true;
            for (int k = 1; k <= 4; k++)
            {
                int rr = r - dr * k, cc = c - dc * k;
                if (rr < 0 || cc < 0 || rr >= m.GetLength(0) || cc >= m.GetLength(1)) continue;
                if (m[rr, cc]) { beforeLight = false; break; }
            }

            // 后侧 4 格必须都是浅色
            bool afterLight = true;
            for (int k = 7; k <= 10; k++)
            {
                int rr = r + dr * k, cc = c + dc * k;
                if (rr < 0 || cc < 0 || rr >= m.GetLength(0) || cc >= m.GetLength(1)) { afterLight = false; break; }
                if (m[rr, cc]) { afterLight = false; break; }
            }

            // 一侧的 4 格静默区即可（越界视为不满足该侧）
            return beforeLight || afterLight;
        }
    }
}
