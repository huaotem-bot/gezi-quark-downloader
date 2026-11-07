// GeZi - 鸽子下载器
// Copyright (C) 2026 Yi Yuan
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
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.DownloadEngine.Resume;

/// <summary>
/// 续传分片表里的一行：区间 [Start, End] 与已写入的字节数 Position。
/// </summary>
public sealed class ResumeChunk
{
    public long Start { get; set; }

    public long End { get; set; }

    public long Position { get; set; }

    /// <summary>本片长度（含首尾）。</summary>
    public long Length => End >= Start ? End - Start + 1 : 0;

    public bool IsComplete => Length > 0 && Position >= Length;

    public override string ToString()
    {
        return Start + "-" + End + "@" + Position;
    }
}

/// <summary>
/// 一次下载的续传状态。
/// </summary>
public sealed class ResumeState
{
    /// <summary>格式版本。解析到不认识的版本一律当没有续传，避免读错旧格式。</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>文件总长度。与本次探测结果不一致时整份续传作废。</summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// 探测阶段捕获的资源标识（强 ETag 或 Last-Modified）。
    /// 与本次探测不一致说明文件在暂停期间被改过，续传会拼出坏文件，必须作废。
    /// </summary>
    public string Validator { get; set; } = "";

    /// <summary>
    /// 2026-11-04 新增：这条续传状态**属于哪个源文件**的指纹。
    /// <para>
    /// 事故背景：`.geziresume` 是按**目标路径**定位的（PartsPaths.ResolvePaths 只看 dest）。
    /// 用户先下夸克的「示例视频」（15,340,287 字节），之后从 UC 网盘下**同名**的
    /// 另一个文件（186,678,707 字节），UniquePath 把它落成「示例视频 (1)」——
    /// 只要长度碰巧相同，引擎就会拿旧进度续新文件，拼出「长度对、内容坏」的成品。
    /// </para>
    /// <para>
    /// 光靠 Validator 不够：它是**服务端**给的，CDN 不回 ETag、
    /// 探测超时、走 OpenSSL 桥时响应头缺失，都会让它变成空串 —— 那时旧状态就会被无条件采信。
    /// </para>
    /// <para>
    /// 这个字段是**本地**算的、必然存在的指纹：文件总长 + 网盘类型 + 源文件 Fid。
    /// 三者任一不同 → 判定「不是同一个源文件」→ 整份续传作废、从零重下。
    /// 空串表示旧版本写的状态（没有这个字段），此时退回按 TotalBytes 判。
    /// </para>
    /// </summary>
    public string SourceKey { get; set; } = "";

    public ResumeChunk[] Chunks { get; set; } = new ResumeChunk[0];

    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 保存前**吃掉分片表里的空洞**（2026-11-01，续传可靠性方案 B）。
    /// <para>
    /// 🚨 为什么需要：<see cref="IsUsable"/> 要求分片表**严格连续**，一处空洞就让整份作废。
    /// 而实测（用户日志）空洞几乎全是**恰好 512KiB**、起点落在 X.5 MiB ——
    /// 那是动态抢占切分时「原片已改短、新片还没登记」的瞬间被快照撞上造成的。
    /// 让 178MB 的续传因为一个 512KB 的空洞整份作废，等于「一页有错字就整本书重印」。
    /// </para>
    /// <para>
    /// 做法：把每个 gap **并入前一片的 End**，并把该片 <c>Position</c> 回退到 gap 起点 ——
    /// 那一段会在续传时重新下载，代价最多几百 KB，而不是整个文件。
    /// </para>
    /// <para>
    /// ⚠️ 只在 gap ≤ <paramref name="maxGapBytes"/> 时才补。太大的 gap（比如中间缺了 50MB）
    /// 说明状态真的坏了，继续补只会白下几十 MB，不如整份作废更诚实。
    /// </para>
    /// </summary>
    /// <param name="maxGapBytes">可接受的最大补洞尺寸；0 表示用默认 16MB。</param>
    /// <returns>补掉的空洞数量；0 表示本来就连续或没有可补的。</returns>
    public int NormalizeGaps(long maxGapBytes = 0)
    {
        if (maxGapBytes <= 0) maxGapBytes = DefaultMaxGapBytes;
        if (Chunks == null || Chunks.Length < 2) return 0;

        var sorted = Chunks.Where(c => c != null).OrderBy(c => c.Start).ToArray();
        if (sorted.Length < 2) return 0;

        var merged = new List<ResumeChunk>(sorted.Length);
        int filled = 0;
        ResumeChunk prev = null;
        foreach (var c in sorted)
        {
            if (prev == null) { prev = CloneChunk(c); merged.Add(prev); continue; }

            long expect = prev.End + 1;
            if (c.Start <= expect)
            {
                // 重叠或相邻：直接并进前一片（保留较大的 Position 相对值）
                if (c.End > prev.End)
                {
                    // 扩展 prev 到 c.End，Position 取「到 expect 为止的已完成量 + c 的已完成量」
                    long prevDone = prev.Position;
                    long cDone = c.Position;
                    prev.End = c.End;
                    long newPos = prevDone + cDone;
                    long newLen = prev.Length;
                    prev.Position = newPos > newLen ? newLen : newPos;
                }
                continue;
            }

            long gap = c.Start - expect;
            if (gap <= maxGapBytes)
            {
                // 吃掉这个 gap：把 prev 延伸到 **c.Start-1**（只跨过洞，不吞并 c 本身）。
                //
                // 🚨 2026-11-01 自检抓到过一个 bug：原来这里写的是 prev.End = c.End，
                //    等于把洞**和它后面的整片**一起吞进 prev —— 但 c 自己的已完成数据
                //    并没有搬进 prev，Position 只按 prev 原长度算，
                //    结果 c 那一段被当成"已完成"却从没下过 → 长度对、内容坏。
                //    正确做法：prev 只跨过洞，c 原样保留在后面；下一轮 expect 会接上 c。
                long prevLenBefore = prev.Length;
                prev.End = c.Start - 1;
                prev.Position = prevLenBefore;   // 只保留 gap 之前已完成的部分
                filled++;

                // c 本身接着放进去（它没有被吞并）
                prev = CloneChunk(c);
                merged.Add(prev);
                continue;
            }
            else
            {
                // gap 太大：不补，原样保留（后面 IsUsable 会判废，这是正确的）
                prev = CloneChunk(c);
                merged.Add(prev);
            }
        }

        if (filled > 0)
            Chunks = merged.ToArray();
        return filled;
    }

    /// <summary>补洞的默认上限：16MB。超过说明状态真坏了，整份作废更诚实。</summary>
    public const long DefaultMaxGapBytes = 16L * 1024 * 1024;

    /// <summary>
    /// 分片表是否**完整覆盖**整个文件（允许中间有洞，只要洞的两侧都有片）。
    /// <para>
    /// 🚨 2026-11-01（方案 C）：这是「想怎么续传就怎么续传」的关键。
    /// <see cref="IsUsable"/> 要求严格连续 —— 一处 512KB 空洞就让 178MB 作废。
    /// 但其实只要**每个字节都归属于某个分片**，恢复时把「未完成部分」重新下载就够了，
    /// 根本不需要连续。
    /// </para>
    /// <para>
    /// 与 <see cref="IsUsable"/> 的区别：
    /// · IsUsable：Start 必须严格等于上一片 End+1（无洞）；
    /// · CoversAll：只要求从 0 开始、到 TotalBytes-1 结束，中间允许洞，但**洞必须被填补**
    ///   —— 调用方（ApplyResume）会把洞补成待下分片，所以这里只校验首尾。
    /// </para>
    /// </summary>
    public bool CoversAll()
    {
        if (Version != CurrentVersion || TotalBytes <= 0) return false;
        if (Chunks == null || Chunks.Length == 0) return false;

        long maxEnd = -1;
        foreach (var c in Chunks)
        {
            if (c == null || c.Length <= 0) return false;
            if (c.Start < 0) return false;
            if (c.Position < 0 || c.Position > c.Length) return false;
            if (c.End > maxEnd) maxEnd = c.End;
        }

        // 首片必须从 0 开始（否则文件开头没人下），末片必须覆盖到 TotalBytes-1
        long minStart = long.MaxValue;
        foreach (var c in Chunks)
            if (c.Start < minStart) minStart = c.Start;

        return minStart == 0 && maxEnd == TotalBytes - 1;
    }

    /// <summary>
    /// 把分片表**补齐成无洞的连续表**（方案 C）。
    /// <para>
    /// 对每一处 gap，插入一个 <c>Position = 0</c> 的分片 —— 它会被当作「待下载」处理，
    /// 从而保证文件里每个字节都有人负责。这是「允许任意续传」的正确性基础：
    /// 少了这一步，洞里的字节永远没人下，最终产出长度对、内容坏的文件。
    /// </para>
    /// </summary>
    /// <returns>插入的补洞分片数量。</returns>
    public int FillGapsWithPendingChunks()
    {
        if (Chunks == null || Chunks.Length == 0) return 0;

        var sorted = Chunks.Where(c => c != null).OrderBy(c => c.Start).ToArray();
        var result = new List<ResumeChunk>(sorted.Length + 8);
        int inserted = 0;
        long expect = 0;

        foreach (var c in sorted)
        {
            if (c.Start > expect)
            {
                // 洞：插一个待下分片（Position=0 = 一个字节都没下）
                result.Add(new ResumeChunk { Start = expect, End = c.Start - 1, Position = 0 });
                inserted++;
            }
            result.Add(CloneChunk(c));
            long next = c.End + 1;
            if (next > expect) expect = next;
        }

        if (inserted > 0)
            Chunks = result.ToArray();
        return inserted;
    }

    private static ResumeChunk CloneChunk(ResumeChunk c)
    {
        return new ResumeChunk { Start = c.Start, End = c.End, Position = c.Position };
    }

    /// <summary>已写入字节总数。</summary>
    public long ReceivedBytes
    {
        get
        {
            long sum = 0;
            if (Chunks != null)
            {
                foreach (ResumeChunk c in Chunks)
                {
                    if (c != null && c.Position > 0)
                    {
                        sum += c.Position;
                    }
                }
            }

            return sum;
        }
    }

    /// <summary>
    /// 校验分片表是否可用：必须非空、按 Start 升序、首尾相连无空洞无重叠、
    /// 且总长度等于 <see cref="TotalBytes"/>。任何一条不满足都当没有续传。
    /// </summary>
    public bool IsUsable()
    {
        if (Version != CurrentVersion || TotalBytes <= 0)
        {
            return false;
        }

        if (Chunks == null || Chunks.Length == 0)
        {
            return false;
        }

        long expected = 0;
        foreach (ResumeChunk c in Chunks)
        {
            if (c == null || c.Length <= 0)
            {
                return false;
            }

            if (c.Start != expected)
            {
                return false;
            }

            // Position 越界说明记录被写坏（例如进程在写状态文件时被杀），整份作废
            if (c.Position < 0 || c.Position > c.Length)
            {
                return false;
            }

            expected = c.End + 1;
        }

        return expected == TotalBytes;
    }
}

/// <summary>
/// 续传状态存取。
///
/// 与自研引擎（<c>SegmentedDownloader</c> 的 <c>.qparts</c> + <c>.qmeta</c>）的差别：
/// 自研引擎每片一个独立文件、物理上无写竞争，所以状态里只需要「每片长度」；
/// bezzad 是单文件随机写（<c>ConcurrentStream</c>），数据本身留在目标文件里，
/// 所以这里只需要保存「分片表 + 每片已写字节数」，恢复时按 Position 续写即可。
/// 因此本接口存的是**进度表**，不是分片数据。
/// </summary>
public interface IResumeStore
{
    string Name { get; }

    /// <summary>读取续传状态。没有或不可用时返回 null，绝不抛异常打断下载。</summary>
    Task<ResumeState> LoadAsync(string key, CancellationToken cancel);

    /// <summary>写入续传状态。</summary>
    Task SaveAsync(string key, ResumeState state, CancellationToken cancel);

    /// <summary>删除续传状态（下载成功或用户取消时调用）。</summary>
    Task ClearAsync(string key, CancellationToken cancel);
}
