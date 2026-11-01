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

    public ResumeChunk[] Chunks { get; set; } = new ResumeChunk[0];

    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;

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
