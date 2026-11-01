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

using System.Threading;
using System.Threading.Tasks;
using GeZi.DownloadEngine.Transport;

namespace GeZi.DownloadEngine.Validation;

/// <summary>
/// 分片响应的守卫结论。只有 <see cref="Ok"/> 才允许把字节写进目标文件。
/// </summary>
public enum ChunkGuardResult
{
    /// <summary>正常，可以继续写入。</summary>
    Ok = 0,

    /// <summary>
    /// 服务端忽略了 Range 直接回 200。说明资源在下载期间变了，
    /// 继续写会拼出「旧片 + 新片」混杂的坏文件，必须整任务作废。
    /// </summary>
    ResourceChanged = 1,

    /// <summary>
    /// Content-Range 起点与请求不符。服务端给的区间不是我们要的那段，
    /// 写进去会错位（历史上这是静默损坏的主要来源之一）。
    /// </summary>
    StartMismatch = 2,
}

/// <summary>
/// 期望的内容哈希。三者按 SHA-256 → SHA-1 → MD5 的优先级只取一个用，
/// 与自研引擎 <c>SegmentedDownloader.VerifyContentAsync</c> 的优先级保持一致。
/// </summary>
public sealed class HashExpectation
{
    public string Sha256 { get; set; }

    public string Sha1 { get; set; }

    public string Md5 { get; set; }

    public bool IsEmpty =>
        string.IsNullOrEmpty(Sha256) &&
        string.IsNullOrEmpty(Sha1) &&
        string.IsNullOrEmpty(Md5);
}

/// <summary>
/// 内容校验结论。<see cref="Skipped"/> 表示网盘接口没给哈希，属于「未校验」而非「通过」，
/// 上层必须把这两种情况分开显示，不能混为一谈。
/// </summary>
public sealed class VerifyOutcome
{
    public bool Ok { get; set; }

    public bool Skipped { get; set; }

    public string Algorithm { get; set; } = "";

    public string Message { get; set; } = "";

    /// <summary>
    /// 哈希与实际内容不一致。
    /// <para>
    /// 这<b>不等于</b>「文件损坏」：部分网盘（实测夸克 <c>getDownloadUrl</c>）返回的哈希字段
    /// 与实际下发内容不符，而下载到的内容本身完整正确。上层必须先做二次确认再判失败，
    /// 否则会把完好的文件误杀（2026-10-24 用户实测踩到）。
    /// </para>
    /// </summary>
    public bool HashMismatch { get; set; }

    public static VerifyOutcome Pass(string algorithm)
    {
        return new VerifyOutcome { Ok = true, Algorithm = algorithm, Message = algorithm + " 校验通过" };
    }

    public static VerifyOutcome Fail(string algorithm, string message)
    {
        return new VerifyOutcome { Ok = false, Algorithm = algorithm, Message = message };
    }

    public static VerifyOutcome Skip(string message)
    {
        return new VerifyOutcome { Ok = true, Skipped = true, Message = message };
    }

    /// <summary>
    /// 哈希不符（<see cref="HashMismatch"/> = true），但本身<b>不判失败</b> ——
    /// 由上层抽样复核后再决定是「元数据不可信」还是「真损坏」。
    /// </summary>
    public static VerifyOutcome Warn(string message)
    {
        return new VerifyOutcome { Ok = true, HashMismatch = true, Message = message };
    }
}

/// <summary>
/// 不可重试的分片错误：分片本身没坏、连接也没断，是「资源变了」或「服务端给的区间不对」。
/// 重试只会得到同样的结果，必须让整个任务失败并作废已下载的部分。
/// </summary>
public sealed class FatalChunkException : System.Exception
{
    public FatalChunkException(string message) : base(message) { }

    public FatalChunkException(string message, System.Exception inner) : base(message, inner) { }
}

/// <summary>
/// 内容校验器：负责「防止文件损坏」的整条链路 —— 下载前捕获资源标识、
/// 每个分片请求带上 If-Range、校验分片响应区间、下载完成后比对哈希。
/// </summary>
public interface IContentValidator
{
    string Name { get; }

    /// <summary>
    /// 从一次探测响应里捕获资源标识（强 ETag 优先，回退 Last-Modified）。
    /// 返回空串表示服务端没给可用的校验器，此时后续不做 If-Range 也不做变更判定。
    /// </summary>
    string Capture(ChunkResponse response);

    /// <summary>给分片请求带上 If-Range，让服务端在资源变更时拒绝提供区间。</summary>
    void Guard(ChunkRequest request, string validator);

    /// <summary>校验分片响应是否可以安全写入。</summary>
    ChunkGuardResult GuardChunk(ChunkRequest request, ChunkResponse response);

    /// <summary>下载完成后比对文件内容哈希。</summary>
    Task<VerifyOutcome> VerifyAsync(string path, HashExpectation expect, CancellationToken cancel);
}
