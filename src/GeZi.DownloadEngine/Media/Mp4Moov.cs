// 鸽子下载器 — 多网盘下载器
// Copyright (C) 2026 鸽子下载器贡献者
//
// 本程序是自由软件：你可以依据 GNU Affero 通用公共许可证（AGPL-3.0）
// 的条款再分发和/或修改它，许可证由自由软件基金会发布，版本 3 或你选择的任何更新版本。
//
// 本程序基于「有用」的期望分发，但不提供任何担保，甚至不包含适销性或
// 特定用途适用性的默示担保。详见 GNU Affero 通用公共许可证。
//
// 你应该已随本程序收到一份 GNU Affero 通用公共许可证副本；
// 如果没有，请见 <https://www.gnu.org/licenses/>。

using System;
using System.Collections.Generic;

namespace GeZi.DownloadEngine.Media
{
    /// <summary>MP4 里 <c>moov</c>（索引表）的位置信息。</summary>
    public sealed class Mp4MoovInfo
    {
        /// <summary>moov 在文件内的绝对偏移。</summary>
        public long Offset;

        /// <summary>moov 整个 box 的字节数（含 8 字节 box 头）。</summary>
        public long Size;

        /// <summary>是否位于文件尾部（即 <c>mdat</c> 之后）。faststart 的文件为 false。</summary>
        public bool IsAtTail;

        public override string ToString()
        {
            return "moov@" + Offset + " size=" + Size + (IsAtTail ? " (tail)" : " (head)");
        }
    }

    /// <summary>
    /// 极简 MP4/ISO-BMFF 结构解析：只为回答一个问题 ——
    /// <b>索引表 <c>moov</c> 到底在文件开头还是结尾？</b>
    ///
    /// <para>
    /// 为什么要问：没有做 faststart 的文件（ffmpeg 默认就是这么输出的），
    /// 结构是 <c>ftyp → free → mdat(全部视频数据) → moov(索引)</c>。
    /// 播放器必须先读到 <c>moov</c> 才知道每个采样在哪、怎么解码，
    /// 所以「只下了前几 MB」的半截文件一律报「无法播放」。
    /// 实测样本 82.mp4（83,395,509 字节）：moov 在偏移 83,260,432，仅 135,077 字节。
    /// </para>
    ///
    /// <para>
    /// 因此只要在下载开始时把这 132 KB 单独抓下来写到正确偏移，
    /// 剩下的视频数据仍按顺序下，用户下到几 MB 就能开始看了。
    /// </para>
    ///
    /// <para>
    /// 本类只做纯字节解析，不碰网络、不碰磁盘，便于单元测试。
    /// 调用方负责按需把头部/尾部字节读进来。
    /// </para>
    /// </summary>
    public static class Mp4Moov
    {
        /// <summary>读头部时建议的字节数。正常文件前几个 box 的头部都在这个范围内。</summary>
        public const int HeadProbeBytes = 64 * 1024;

        /// <summary>
        /// 从 mdat 结尾往后读的字节数。
        /// <para>
        /// 🚨 2026-10-24 从 4 MB 降到 128 KB：这个窗口是**单连接串行**读的，且发生在
        /// 主下载开始之前。实测夸克分享直链单连接只有 ~51 KB/s，读 4 MB 要 **80 秒**，
        /// 那 80 秒里一个分片都没开始、文件长度 0、日志空 —— 用户看到的就是「点了下载没反应」。
        /// </para>
        /// <para>
        /// 为什么 128 KB 就够：moov 紧贴 mdat 结尾时，tail[0] 就是它的 box 头
        /// （4 字节 size + 4 字节 type），8 字节即可确定 size；窗口只需覆盖
        /// 「mdat 与 moov 之间可能夹的 free / skip / wide 等小 box」。
        /// 若 moov 比窗口大，再按已知偏移精确补读一次（见 DownloadService.FetchMoovCoreAsync）。
        /// </para>
        /// </summary>
        public const int TailProbeBytes = 128 * 1024;

        /// <summary>
        /// 判断这个文件是否值得做「moov 优先」。
        /// 只认 <c>.mp4</c> / <c>.m4v</c> / <c>.mov</c>；其它扩展名一律不处理
        /// （避免对 zip/rar 之类也去请求尾部，白费一次 RTT）。
        /// </summary>
        public static bool LooksLikeMp4(string fileNameOrUrl)
        {
            if (string.IsNullOrEmpty(fileNameOrUrl)) return false;
            string s = fileNameOrUrl;
            int q = s.IndexOf('?');
            if (q >= 0) s = s.Substring(0, q);
            int dot = s.LastIndexOf('.');
            if (dot < 0 || dot == s.Length - 1) return false;
            string ext = s.Substring(dot + 1).ToLowerInvariant();
            return ext == "mp4" || ext == "m4v" || ext == "mov";
        }

        /// <summary>
        /// 从<b>文件头部</b>的字节里解析 box 链，找出 <c>mdat</c> 的结束位置。
        /// <para></para>
        /// <para>
        /// 返回 false 表示「不用管」：要么头部就有 <c>moov</c>（已经是 faststart，
        /// 下到一点就能播），要么结构不认识（例如分片 MP4 的 <c>moof</c>、
        /// 或者 <c>mdat</c> 的 size 字段是 0 表示"一直到文件尾"）。
        /// </para>
        /// </summary>
        /// <param name="head">文件开头若干字节（至少 32 字节，建议 <see cref="HeadProbeBytes"/>）。</param>
        /// <param name="headLength">head 里有效字节数。</param>
        /// <param name="totalSize">文件总长度（用于判断 mdat 是否吃到尾）。</param>
        /// <param name="mdatEnd">输出：mdat box 结束处的绝对偏移。</param>
        public static bool TryFindMdatEnd(byte[] head, int headLength, long totalSize, out long mdatEnd)
        {
            mdatEnd = -1;
            if (head == null || headLength < 8 || totalSize <= 0) return false;

            long pos = 0;
            // 只看头部能覆盖的范围；box 链一般不超过 4 个
            for (int guard = 0; guard < 16; guard++)
            {
                if (pos + 8 > headLength) return false;
                long size = ReadU32(head, (int)pos);
                string type = ReadType(head, (int)pos + 4);
                long headerLen = 8;

                if (size == 1)
                {
                    // 64 位 largesize
                    if (pos + 16 > headLength) return false;
                    size = ReadU64(head, (int)pos + 8);
                    headerLen = 16;
                }
                else if (size == 0)
                {
                    // size==0 表示"延伸到文件末尾"，只有 mdat 会这么写
                    if (type == "mdat") return false;
                    return false;
                }

                if (size < headerLen) return false;          // 明显损坏
                long boxEnd = pos + size;
                if (boxEnd > totalSize) return false;        // 超出文件，别乱猜

                if (type == "moov")
                {
                    // 头部就有 moov —— 已经是 faststart，不需要任何特殊处理
                    return false;
                }

                if (type == "mdat")
                {
                    // mdat 之后还有内容（moov/free 等）才值得做
                    if (boxEnd < totalSize) { mdatEnd = boxEnd; return true; }
                    return false;
                }

                // 跳过未知 box（ftyp / free / wide / uuid / moof …）
                pos = boxEnd;
                if (pos >= totalSize) return false;
            }
            return false;
        }

        /// <summary>
        /// 在<b>文件尾部</b>读到的字节里找 <c>moov</c>。
        /// </summary>
        /// <param name="tail">从某个绝对偏移开始读到文件尾的字节。</param>
        /// <param name="tailLength">有效字节数。</param>
        /// <param name="tailStartOffset">tail[0] 对应的文件绝对偏移。</param>
        /// <param name="totalSize">文件总长度。</param>
        /// <param name="info">输出 moov 位置。</param>
        public static bool TryFindMoovInTail(byte[] tail, int tailLength, long tailStartOffset,
                                             long totalSize, out Mp4MoovInfo info)
        {
            info = null;
            if (tail == null || tailLength < 8) return false;

            // 先按 box 链正常解析（快、准）
            long pos = 0;
            for (int guard = 0; guard < 64; guard++)
            {
                if (pos + 8 > tailLength) break;
                long size = ReadU32(tail, (int)pos);
                string type = ReadType(tail, (int)pos + 4);
                long headerLen = 8;

                if (size == 1)
                {
                    if (pos + 16 > tailLength) break;
                    size = ReadU64(tail, (int)pos + 8);
                    headerLen = 16;
                }
                else if (size == 0)
                {
                    break;   // 吃到尾，无法继续
                }

                if (size < headerLen) break;
                long boxEnd = pos + size;

                if (type == "moov")
                {
                    long absOffset = tailStartOffset + pos;
                    // 完整性：必须真的落在文件内，且长度对得上
                    if (absOffset >= 0 && absOffset + size <= totalSize)
                    {
                        info = new Mp4MoovInfo { Offset = absOffset, Size = size, IsAtTail = true };
                        return true;
                    }
                    return false;
                }

                if (boxEnd > tailLength) break;   // 这个 box 跨越了 tail 边界，解析不下去
                pos = boxEnd;
            }

            // 兜底：逐字节找 "moov" 四个字节（只在 box 链解析失败时用）。
            // 命中后必须能读出合理的 size 才算数，否则视为误判（视频数据里也可能出现这四个字节）。
            for (int i = 0; i + 8 <= tailLength; i++)
            {
                if (tail[i + 4] != (byte)'m' || tail[i + 5] != (byte)'o'
                    || tail[i + 6] != (byte)'o' || tail[i + 7] != (byte)'v')
                    continue;

                long size = ReadU32(tail, i);
                if (size < 8) continue;
                long absOffset = tailStartOffset + i;
                if (absOffset < 0 || absOffset + size > totalSize) continue;

                info = new Mp4MoovInfo { Offset = absOffset, Size = size, IsAtTail = true };
                return true;
            }
            return false;
        }

        private static long ReadU32(byte[] b, int i)
        {
            return ((long)b[i] << 24) | ((long)b[i + 1] << 16) | ((long)b[i + 2] << 8) | b[i + 3];
        }

        private static long ReadU64(byte[] b, int i)
        {
            long hi = ReadU32(b, i);
            long lo = ReadU32(b, i + 4);
            if (hi > 0x7FFFFFFFL) return long.MaxValue;   // 大得离谱，当作不可用
            return (hi << 32) | lo;
        }

        private static string ReadType(byte[] b, int i)
        {
            return new string(new[] { (char)b[i], (char)b[i + 1], (char)b[i + 2], (char)b[i + 3] });
        }
    }
}
