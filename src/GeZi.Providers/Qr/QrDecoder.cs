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
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using ZXing;
using ZXing.Common;
using GeZi.Core.Api;
using GeZi.Core.Models;
using GeZi.Core.Support;

namespace GeZi.Providers.Qr
{
    /// <summary>
    /// 二维码/条码解码器（基于 ZXing.Net，Apache-2.0，本地固化引用见 lib/ZXing/）。
    /// <see cref="QrEncoder"/> 那种编码器只要把数据排进矩阵，
    /// 而解码反着来：要先在杂乱图像里找出定位图形、做透视校正、按模块采样，
    /// 最后跑 Reed-Solomon 纠错 —— 现实中的截图/照片还带缩放、JPEG 噪点、背景干扰。
    /// 这部分用成熟库远比自写可靠。
    /// 现实图片千奇百怪，单次尝试失败率偏高，所以这里做**多轮渐进尝试**：
    ///   1) 原图直解（清晰截图命中率最高，也最快）
    ///   2) 放大（小图/被缩小的图，模块太小会丢）
    ///   3) 灰度 + 提高对比度（低对比、带色调的截图）
    ///   4) 灰度 + 二值化（噪点、渐变背景）
    ///   5) 反转色（深浅色主题下的"反色二维码"）
    ///   6) 中央裁剪重试（二维码只占图片一小块时，裁掉无关区域减少干扰）
    /// 任一命中即返回，避免无谓开销。
    /// </summary>
    public static class QrDecoder
    {
        /// <summary>解码结果。</summary>
        public sealed class Result
        {
            /// <summary>是否成功解出内容。</summary>
            public bool Ok;

            /// <summary>解出的文本（含非链接内容，交给上层判断）。</summary>
            public string Text;

            /// <summary>识别到的码制（如 QR_CODE、CODE_128）。</summary>
            public string Format;

            /// <summary>失败原因（成功时为空）。</summary>
            public string Error;

            /// <summary>命中的尝试轮次名（排查用）。</summary>
            public string Attempt;
        }

        /// <summary>允许的码制。默认收窄到场景相关的两类，避免把菜谱、产品条码等误识成结果。</summary>
        private static readonly List<BarcodeFormat> Formats = new List<BarcodeFormat>
        {
            BarcodeFormat.QR_CODE,
            BarcodeFormat.CODE_128,   // 少数分享会以条码图给出
        };

        /// <summary>尝试的放大倍数（原图之外的额外轮次）。</summary>
        private const int UpscaleFactor = 2;

        /// <summary>图片像素总量上限（超过则先等比缩小，避免大图爆内存/超时）。</summary>
        private const long MaxPixels = 32L * 1024 * 1024;   // 约 3200 万像素

        /// <summary>按最大边限制的尺寸（超大图缩到这个量级再解）。</summary>
        private const int MaxSide = 4000;

        /// <summary>
        /// 从图片文件解码。支持 png / jpg / bmp / gif 等 GDI+ 能读的格式。
        /// </summary>
        public static Result DecodeFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return Fail("路径为空");
            if (!File.Exists(path))
                return Fail("文件不存在：" + path);

            Bitmap bmp = null;
            try
            {
                // 不用 Image.FromFile —— 那样会一直占着文件句柄，
                // 从流加载可以让本方法返回后立刻释放（用户可能马上删/移这张图）。
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bmp = new Bitmap(fs);
                }
            }
            catch (Exception ex)
            {
                // 位图已经建出来了（只是后续步骤失败）→ 必须自己释放，否则整张图的内存挂着直到 GC。
                if (bmp != null)
                {
                    try { bmp.Dispose(); } catch { }
                    bmp = null;
                }
                return Fail("图片读取失败：" + ex.Message);
            }

            using (bmp)
            {
                return Decode(bmp);
            }
        }

        /// <summary>
        /// 从内存字节解码（贴剪贴板图片用；剪贴板拿到的是 DIB 字节，没有路径）。
        /// </summary>
        public static Result DecodeBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return Fail("图片数据为空");

            try
            {
                using (var ms = new MemoryStream(bytes, writable: false))
                using (var bmp = new Bitmap(ms))
                {
                    return Decode(bmp);
                }
            }
            catch (Exception ex)
            {
                return Fail("图片解析失败：" + ex.Message);
            }
        }

        /// <summary>从已加载的 Bitmap 解码。</summary>
        public static Result Decode(Bitmap source)
        {
            if (source == null || source.Width <= 0 || source.Height <= 0)
                return Fail("图片无效");

            try
            {
                using (var work = Normalize(source))
                {
                    // ---- 第 1 轮：原图 ----
                    var r = TryDecode(work, "原图");
                    if (r != null) return r;

                    // ---- 第 2 轮：放大 ----
                    using (var up = Resize(work, work.Width * UpscaleFactor, work.Height * UpscaleFactor))
                    {
                        r = TryDecode(up, "放大 " + UpscaleFactor + "×");
                        if (r != null) return r;
                    }

                    // ---- 第 3 轮：灰度增强对比 ----
                    using (var gray = ToGray(work, contrast: true))
                    {
                        r = TryDecode(gray, "灰度增强");
                        if (r != null) return r;

                        // ---- 第 4 轮：灰度 + 二值化（Otus 阈值） ----
                        using (var bw = Binarize(gray))
                        {
                            r = TryDecode(bw, "二值化");
                            if (r != null) return r;
                        }
                    }

                    // ---- 第 5 轮：反转色（深色主题截图里的"反色二维码"） ----
                    using (var inv = Invert(work))
                    {
                        r = TryDecode(inv, "反色");
                        if (r != null) return r;
                    }

                    // ---- 第 6 轮：中央裁剪（码只占画面一小块） ----
                    foreach (int keepPct in new[] { 80, 60, 40 })
                    {
                        using (var crop = CenterCrop(work, keepPct))
                        {
                            if (crop == null) continue;
                            r = TryDecode(crop, "中央裁剪 " + keepPct + "%");
                            if (r != null) return r;
                        }
                    }
                }

                return Fail("没识别到二维码。试试换一张更清晰、二维码更完整的图。");
            }
            catch (Exception ex)
            {
                return Fail("解码出错：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        // ---------------- 内部实现 ----------------

        /// <summary>单次解码尝试；失败返回 null（不抛异常，避免噪声图打断整条链）。</summary>
        private static Result TryDecode(Bitmap bmp, string attempt)
        {
            try
            {
                var reader = new BarcodeReader
                {
                    // 只认需要的码制：范围越窄，误识别概率越低、速度越快
                    Options = new DecodingOptions
                    {
                        PossibleFormats = Formats,
                        // 二维码在照片里常有倾斜；开这两项让定位图形更易被找到
                        TryHarder = true,
                        TryInverted = false,   // 反色我们自己单独做一轮（更可控）
                    },
                    AutoRotate = true,
                };

                var res = reader.Decode(bmp);
                if (res == null || string.IsNullOrEmpty(res.Text)) return null;

                return new Result
                {
                    Ok = true,
                    Text = res.Text,
                    Format = res.BarcodeFormat.ToString(),
                    Attempt = attempt,
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 归一化：超大图等比缩小（避免内存与耗时失控），并把像素格式统一成 32bppArgb，
        /// 让后续所有变换都在同一种格式上跑（某些格式的 LockBits 会抛异常）。
        /// </summary>
        private static Bitmap Normalize(Bitmap src)
        {
            int w = src.Width, h = src.Height;

            // 图像太大 → 等比缩到 MaxSide 以内且总像素不超 MaxPixels
            double scale = 1.0;
            if (w > MaxSide || h > MaxSide)
                scale = Math.Min((double)MaxSide / w, (double)MaxSide / h);
            if ((long)(w * scale) * (long)(h * scale) > MaxPixels)
                scale = Math.Sqrt((double)MaxPixels / ((long)w * h));

            Bitmap bmp = scale < 1.0
                ? Resize(src, Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)))
                : new Bitmap(src);

            // 统一格式
            if (bmp.PixelFormat != PixelFormat.Format32bppArgb)
            {
                var converted = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(converted))
                {
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                }
                bmp.Dispose();
                bmp = converted;
            }
            return bmp;
        }

        private static Bitmap Resize(Bitmap src, int w, int h)
        {
            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                // 放大时用 NearestNeighbor 对 QR 更友好（保持模块边界锐利），
                // 但 ZXing 自带采样容错，这里统一用高质量插值，避免锯齿反而干扰定位。
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return dst;
        }

        /// <summary>转灰度；contrast=true 时把对比度拉开（用逐像素 lut 映射，快且无副作用）。</summary>
        private static Bitmap ToGray(Bitmap src, bool contrast)
        {
            var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                // 用 ColorMatrix 做灰度 + 对比度，比逐像素快得多
                var cm = new ColorMatrix(new[]
                {
                    new[] { .299f, .299f, .299f, 0f, 0f },
                    new[] { .587f, .587f, .587f, 0f, 0f },
                    new[] { .114f, .114f, .114f, 0f, 0f },
                    new[] { 0f,    0f,    0f,    1f, 0f },
                    new[] { 0f,    0f,    0f,    0f, 1f },
                });

                if (contrast)
                {
                    // 围绕 0.5 提升对比度：1.6 倍是实测对扫描件/截图较稳的值
                    const float c = 1.6f, t = (1f - c) / 2f;
                    var boost = new ColorMatrix(new[]
                    {
                        new[] { c, 0f, 0f, 0f, 0f },
                        new[] { 0f, c, 0f, 0f, 0f },
                        new[] { 0f, 0f, c, 0f, 0f },
                        new[] { 0f, 0f, 0f, 1f, 0f },
                        new[] { t, t, t, 0f, 1f },
                    });
                    cm = Multiply(boost, cm);
                }

                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(cm);
                    g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height),
                        0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
            }
            return dst;
        }

        /// <summary>矩阵相乘（先应用 a 再应用 b 的合成；GDI+ 的约定：结果 = a × b）。</summary>
        private static ColorMatrix Multiply(ColorMatrix a, ColorMatrix b)
        {
            var r = new float[5][];
            for (int i = 0; i < 5; i++) r[i] = new float[5];
            for (int i = 0; i < 5; i++)
                for (int j = 0; j < 5; j++)
                {
                    float s = 0f;
                    for (int k = 0; k < 5; k++) s += a[i, k] * b[k, j];
                    r[i][j] = s;
                }
            return new ColorMatrix(r);
        }

        /// <summary>Otsu 大津法自动阈值二值化（比固定阈值稳，能适应不同亮度）。</summary>
        private static Bitmap Binarize(Bitmap gray)
        {
            int w = gray.Width, h = gray.Height;
            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);

            var rect = new Rectangle(0, 0, w, h);
            var sd = gray.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var dd = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            try
            {
                int stride = sd.Stride;
                byte[] sb = new byte[stride * h];
                System.Runtime.InteropServices.Marshal.Copy(sd.Scan0, sb, 0, sb.Length);

                // 灰度直方图（取 B 通道即可，前面已转灰度，三通道相等）
                var hist = new int[256];
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++) hist[sb[row + x * 4]]++;
                }

                int threshold = OtsuThreshold(hist, w * h);

                byte[] db = new byte[stride * h];
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        byte v = sb[row + x * 4] >= threshold ? (byte)255 : (byte)0;
                        int o = row + x * 4;
                        db[o] = v; db[o + 1] = v; db[o + 2] = v; db[o + 3] = 255;
                    }
                }
                System.Runtime.InteropServices.Marshal.Copy(db, 0, dd.Scan0, db.Length);
            }
            finally
            {
                gray.UnlockBits(sd);
                dst.UnlockBits(dd);
            }
            return dst;
        }

        private static int OtsuThreshold(int[] hist, int total)
        {
            double sum = 0;
            for (int i = 0; i < 256; i++) sum += (double)i * hist[i];

            double sumB = 0, maxVar = -1;
            int wB = 0, best = 127;
            for (int t = 0; t < 256; t++)
            {
                wB += hist[t];
                if (wB == 0) continue;
                int wF = total - wB;
                if (wF == 0) break;

                sumB += (double)t * hist[t];
                double mB = sumB / wB;
                double mF = (sum - sumB) / wF;
                double between = (double)wB * wF * (mB - mF) * (mB - mF);
                if (between > maxVar) { maxVar = between; best = t; }
            }
            return best;
        }

        /// <summary>RGB 反转（处理"反色二维码"）。</summary>
        private static Bitmap Invert(Bitmap src)
        {
            var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                var cm = new ColorMatrix(new[]
                {
                    new[] { -1f,  0f,  0f, 0f, 0f },
                    new[] {  0f, -1f,  0f, 0f, 0f },
                    new[] {  0f,  0f, -1f, 0f, 0f },
                    new[] {  0f,  0f,  0f, 1f, 0f },
                    new[] {  1f,  1f,  1f, 0f, 1f },
                });
                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(cm);
                    g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height),
                        0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
            }
            return dst;
        }

        /// <summary>按百分比取中央区域（<paramref name="keepPct"/> = 保留边长百分比）。</summary>
        private static Bitmap CenterCrop(Bitmap src, int keepPct)
        {
            int cw = src.Width * keepPct / 100;
            int ch = src.Height * keepPct / 100;
            if (cw < 32 || ch < 32) return null;             // 太小没意义
            if (cw >= src.Width && ch >= src.Height) return null;

            int x = (src.Width - cw) / 2;
            int y = (src.Height - ch) / 2;
            var dst = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.DrawImage(src, new Rectangle(0, 0, cw, ch),
                    new Rectangle(x, y, cw, ch), GraphicsUnit.Pixel);
            }
            return dst;
        }

        private static Result Fail(string msg)
        {
            return new Result { Ok = false, Error = msg };
        }
    }
}
