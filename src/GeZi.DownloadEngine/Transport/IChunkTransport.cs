// GeZi 鸽子下载器
// Copyright (C) 2026 GeZi contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// 本文件是五层架构里下载引擎的「取流零件」抽象。
// 设计目标：把「怎么拿到一段 Range 响应流」从分片调度里剥离出来，
// 使同一套调度逻辑既能走 Windows 自带的 SChannel（HttpWebRequest），
// 也能走 OpenSSL 3.5.7（UC 的 CDN 会按 TLS ClientHello 指纹拒绝 SChannel）。

using System;

namespace GeZi.DownloadEngine.Transport
{
    /// <summary>
    /// 一次分片取流请求。刻意不依赖 bezzad 的 <see cref="Request"/> 类型，
    /// 这样 transport 实现可以独立测试、也能被自研引擎复用。
    /// </summary>
    public sealed class ChunkRequest
    {
        /// <summary>完整 URL（含查询串）。</summary>
        public string Url;

        /// <summary>本次要读的起始偏移（绝对字节位置）。</summary>
        public long Start;

        /// <summary>本次要读的结束偏移（含）；&lt;0 表示到文件末尾。</summary>
        public long End = -1;

        /// <summary><c>Referer</c> 头（没有则 null）。</summary>
        public string Referer;

        /// <summary><c>User-Agent</c> 头（没有则 null）。</summary>
        public string UserAgent;

        /// <summary><c>Origin</c> 头（没有则 null）。</summary>
        public string Origin;

        /// <summary><c>Cookie</c> 头原文（没有则 null）。</summary>
        public string Cookie;

        /// <summary><c>If-Range</c> 头（强 ETag 或 Last-Modified；没有则 null）。</summary>
        public string IfRange;

        /// <summary>连接/读超时（毫秒）。</summary>
        public int TimeoutMs = 30000;

        /// <summary>
        /// 该 host 期望的并发连接上限；0 表示不改（保持 transport 默认）。
        /// <para>
        /// 🚨 必须由调用方按实际并发数传下来。历史 bug：HttpChunkTransport 曾把它硬编码成 64，
        /// 于是 UI 显示「256 连接」而真实 TCP 只有 64 条，速度被卡在 64×单连接带宽
        /// （实测 3.2 MB/s，而老引擎同条件 11.7 MB/s）。
        /// </para>
        /// </summary>
        public int ConnectionLimit;

        /// <summary>额外请求头（可为 null）。</summary>
        public System.Collections.Generic.IDictionary<string, string> ExtraHeaders;

        /// <summary>主机名。</summary>
        public string Host
        {
            get { return new Uri(Url).Host; }
        }

        /// <summary>路径 + 查询串。</summary>
        public string PathAndQuery
        {
            get
            {
                Uri u = new Uri(Url);
                string pq = u.PathAndQuery;
                return string.IsNullOrEmpty(pq) ? "/" : pq;
            }
        }

        /// <summary>本次请求的字节数；未知返回 0。</summary>
        public long ExpectedBytes
        {
            get { return End >= Start ? End - Start + 1 : 0; }
        }
    }

    /// <summary>
    /// 一次分片取流的结果。<see cref="Stream"/> 由调用方负责释放。
    /// </summary>
    public sealed class ChunkResponse : IDisposable
    {
        /// <summary>响应体流（只读）。</summary>
        public System.IO.Stream Stream;

        /// <summary>HTTP 状态码。</summary>
        public int StatusCode;

        /// <summary><c>Content-Range</c> 起点（没有则 -1）。</summary>
        public long ContentRangeStart = -1;

        /// <summary><c>Content-Range</c> 里的资源总长（没有则 -1）。</summary>
        public long ContentRangeTotal = -1;

        /// <summary>响应头里的 <c>ETag</c>（没有则 null）。</summary>
        public string ETag;

        /// <summary>响应头里的 <c>Last-Modified</c>（没有则 null）。</summary>
        public string LastModified;

        /// <summary>释放响应流。</summary>
        public void Dispose()
        {
            if (Stream != null)
            {
                try { Stream.Dispose(); }
                catch { }
                Stream = null;
            }
        }
    }

    /// <summary>
    /// 取流零件：按 Range 打开一个响应流。
    /// </summary>
    public interface IChunkTransport
    {
        /// <summary>传输通道的简短名字（写日志用）。</summary>
        string Name { get; }

        /// <summary>按 <paramref name="request"/> 打开响应流。</summary>
        System.Threading.Tasks.Task<ChunkResponse> OpenAsync(ChunkRequest request,
            System.Threading.CancellationToken cancel);
    }
}
