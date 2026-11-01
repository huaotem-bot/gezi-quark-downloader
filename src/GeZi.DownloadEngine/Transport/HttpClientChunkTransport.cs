// GeZi 鸽子下载器
// Copyright (C) 2026 GeZi contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// HttpClient 版取流实现（SChannel，连接池复用）。
//
// 与 HttpChunkTransport（HttpWebRequest）的区别：HttpClient 内部维护连接池，
// 多个分片请求复用同一批 TCP/TLS 连接，省掉每片重新握手的开销。
// 自研引擎（SegmentedDownloader）一直用 HttpClient，实测 83MB 文件 16 线程
// 能跑到 9.47 MB/s，而 HttpWebRequest 版只有 4.95 MB/s —— 差 1.9 倍。
// 所以网盘下载默认应走这个实现。

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Support;

namespace GeZi.DownloadEngine.Transport
{
    /// <summary>
    /// 基于 <see cref="HttpClient"/> 的取流实现：连接池复用，比 HttpWebRequest 快一倍。
    /// </summary>
    public sealed class HttpClientChunkTransport : IChunkTransport
    {
        /// <summary>共享单例。内部 HttpClient 全局复用（连接池也跟着复用）。</summary>
        public static readonly HttpClientChunkTransport Instance = new HttpClientChunkTransport();

        private readonly HttpClient _client;

        private HttpClientChunkTransport()
        {
            var handler = new HttpClientHandler();
            handler.AllowAutoRedirect = true;
            handler.AutomaticDecompression = DecompressionMethods.None;
            handler.UseCookies = false;   // Cookie 由我们自己按分片塞头，不用 CookieContainer
            handler.MaxConnectionsPerServer = 512;

            // 🚨 代理策略必须与 API 层、老引擎完全一致：默认强制直连。
            // HttpClientHandler 不调这一步时会继承系统代理，本机代理软件常把 CDN 请求
            // 劫持/限速并改变 TLS ClientHello 指纹 → 触发夸克/UC 的 WAF，
            // 表现就是"域名能解析但下不动"。老引擎（Legacy/SegmentedDownloader.cs:134）
            // 一直有这一步，融合引擎的 transport 漏了，属于回归。
            NetworkOptions.ApplyTo(handler);

            _client = new HttpClient(handler);
            // 超时由每个请求的 CancellationToken 控制，HttpClient 自身不设短超时
            _client.Timeout = TimeSpan.FromMilliseconds(Timeout.Infinite);
        }

        /// <inheritdoc />
        public string Name
        {
            get { return "httpclient"; }
        }

        /// <inheritdoc />
        public async Task<ChunkResponse> OpenAsync(ChunkRequest request, CancellationToken cancel)
        {
            if (request == null) throw new ArgumentNullException("request");

            var msg = new HttpRequestMessage(HttpMethod.Get, request.Url);

            if (!string.IsNullOrEmpty(request.UserAgent))
                msg.Headers.TryAddWithoutValidation("User-Agent", request.UserAgent);
            if (!string.IsNullOrEmpty(request.Referer))
                msg.Headers.TryAddWithoutValidation("Referer", request.Referer);
            if (!string.IsNullOrEmpty(request.Origin))
                msg.Headers.TryAddWithoutValidation("Origin", request.Origin);
            if (!string.IsNullOrEmpty(request.Cookie))
                msg.Headers.TryAddWithoutValidation("Cookie", request.Cookie);
            if (!string.IsNullOrEmpty(request.IfRange))
                msg.Headers.TryAddWithoutValidation("If-Range", request.IfRange);

            if (request.ExtraHeaders != null)
            {
                foreach (var kv in request.ExtraHeaders)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    if (string.Equals(kv.Key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(kv.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(kv.Key, "Range", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(kv.Key, "If-Range", StringComparison.OrdinalIgnoreCase)) continue;
                    msg.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }
            }

            if (request.End >= request.Start)
                msg.Headers.TryAddWithoutValidation("Range", "bytes=" + request.Start + "-" + request.End);
            else if (request.Start > 0)
                msg.Headers.TryAddWithoutValidation("Range", "bytes=" + request.Start + "-");

            // ResponseHeadersRead：拿到响应头就返回，响应体按需读（不预缓冲整个分片）
            HttpResponseMessage resp = await _client
                .SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cancel)
                .ConfigureAwait(false);

            // 请求头在拿到响应时已经全部发出，请求对象到此就可以释放。
            // 以前这里不释放、只靠 GC 收尾 —— 256 个连接的高频分片请求会攒一堆
            // 待回收的 HttpRequestMessage，表现为下载中后期 GC 抖动。
            // （响应流仍然由下面的 OwnedStream 负责连同 resp 一起释放，不受影响。）
            msg.Dispose();

            var result = new ChunkResponse();
            result.StatusCode = (int)resp.StatusCode;

            try
            {
                result.Stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            }
            catch
            {
                resp.Dispose();
                throw;
            }

            // 响应头解析：Content-Range / ETag / Last-Modified
            if (resp.Content.Headers.ContentRange != null)
            {
                var cr = resp.Content.Headers.ContentRange;
                if (cr.From.HasValue) result.ContentRangeStart = cr.From.Value;
                if (cr.Length.HasValue) result.ContentRangeTotal = cr.Length.Value;
            }

            if (resp.Headers.ETag != null && !resp.Headers.ETag.IsWeak)
                result.ETag = resp.Headers.ETag.Tag;
            if (resp.Content.Headers.LastModified.HasValue)
                result.LastModified = resp.Content.Headers.LastModified.Value
                    .ToUniversalTime().ToString("R");

            // 响应体流读完/释放时把 HttpResponseMessage 一并释放，
            // 否则连接不会归还连接池（每次泄漏一条连接，跑一会儿就卡死）。
            result.Stream = new OwnedStream(result.Stream, resp);

            return result;
        }

        /// <summary>
        /// 把「响应体流」和「HttpResponseMessage」绑在一起：流被 Dispose 时
        /// 连带释放响应消息，确保底层连接回到连接池。
        /// </summary>
        private sealed class OwnedStream : Stream
        {
            private readonly Stream _inner;
            private readonly IDisposable _owner;
            private int _disposed;

            public OwnedStream(Stream inner, IDisposable owner)
            {
                _inner = inner;
                _owner = owner;
            }

            public override bool CanRead { get { return _inner.CanRead; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override int Read(byte[] buffer, int offset, int count)
                => _inner.Read(buffer, offset, count);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
                CancellationToken cancellationToken)
                => _inner.ReadAsync(buffer, offset, count, cancellationToken);

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

            protected override void Dispose(bool disposing)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                try { if (disposing) _inner.Dispose(); } catch { }
                try { _owner.Dispose(); } catch { }
                base.Dispose(disposing);
            }
        }
    }
}
