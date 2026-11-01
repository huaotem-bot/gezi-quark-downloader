// GeZi 鸽子下载器
// Copyright (C) 2026 GeZi contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// OpenSSL 取流实现。
// 为什么需要它：UC 的下载 CDN dl-pc-sz-cf.pds.uc.cn 与 pc-api.uc.cn 同策，
// 按 TLS ClientHello 指纹过滤，Windows 自带的 SChannel 一律回
// 403 RequestDeniedByCallback / require login [auth not found]。
// 实测同一个直链、同一份 Cookie 换到 OpenSSL 3.5.7 通道立刻回 206。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Support;

namespace GeZi.DownloadEngine.Transport
{
    /// <summary>
    /// 基于 OpenSSL 3.5.7 原生库的取流实现（UC 专用）。
    /// </summary>
    public sealed class OpenSslChunkTransport : IChunkTransport
    {
        /// <summary>共享单例（无状态）。</summary>
        public static readonly OpenSslChunkTransport Instance = new OpenSslChunkTransport();

        /// <inheritdoc />
        public string Name
        {
            get { return "openssl"; }
        }

        /// <summary>当前环境能否使用（原生库是否可加载）。</summary>
        public bool IsAvailable
        {
            get { return OpenSslTls.IsAvailable; }
        }

        /// <inheritdoc />
        public Task<ChunkResponse> OpenAsync(ChunkRequest request, CancellationToken cancel)
        {
            if (request == null) throw new ArgumentNullException("request");
            cancel.ThrowIfCancellationRequested();

            // OpenSslTls 是同步阻塞 API（SSL_read 阻塞），放到线程池执行，
            // 避免在调用方的 Task 线程上阻塞事件循环。
            return Task.Run(() =>
            {
                cancel.ThrowIfCancellationRequested();

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (request.Start > 0 || request.End >= request.Start)
                {
                    headers["Range"] = request.End >= request.Start
                        ? string.Format("bytes={0}-{1}", request.Start, request.End)
                        : string.Format("bytes={0}-", request.Start);
                }
                if (!string.IsNullOrEmpty(request.Referer)) headers["Referer"] = request.Referer;
                if (!string.IsNullOrEmpty(request.UserAgent)) headers["User-Agent"] = request.UserAgent;
                if (!string.IsNullOrEmpty(request.Origin)) headers["Origin"] = request.Origin;
                if (!string.IsNullOrEmpty(request.Cookie)) headers["Cookie"] = request.Cookie;
                if (!string.IsNullOrEmpty(request.IfRange)) headers["If-Range"] = request.IfRange;

                if (request.ExtraHeaders != null)
                {
                    foreach (var kv in request.ExtraHeaders)
                    {
                        if (string.IsNullOrEmpty(kv.Key)) continue;
                        if (string.Equals(kv.Key, "Range", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(kv.Key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(kv.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                        headers[kv.Key] = kv.Value;
                    }
                }

                OpenSslTls.OpenChunkStream stream = OpenSslTls.OpenChunk(
                    request.Host, request.PathAndQuery, headers,
                    request.TimeoutMs, request.TimeoutMs, request.ExpectedBytes);

                var result = new ChunkResponse();
                result.Stream = stream;
                result.StatusCode = stream.StatusCode;
                result.ContentRangeStart = stream.ContentRangeStart;
                result.ContentRangeTotal = stream.ContentRangeTotal;
                result.ETag = stream.ETag;
                result.LastModified = stream.LastModified;
                return result;
            }, cancel);
        }
    }
}
