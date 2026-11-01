// GeZi 鸽子下载器
// Copyright (C) 2026 GeZi contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// 默认取流实现：走 Windows 自带的 SChannel（HttpWebRequest）。
// 这是绝大多数直链（夸克、阿里、迅雷）的正确选择，行为与原 bezzad 一致。

using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Support;

namespace GeZi.DownloadEngine.Transport
{
    /// <summary>
    /// 基于 <see cref="HttpWebRequest"/> 的取流实现（SChannel）。
    /// </summary>
    public sealed class HttpChunkTransport : IChunkTransport
    {
        /// <summary>共享单例（无状态）。</summary>
        public static readonly HttpChunkTransport Instance = new HttpChunkTransport();

        /// <inheritdoc />
        public string Name
        {
            get { return "http"; }
        }

        /// <inheritdoc />
        public async Task<ChunkResponse> OpenAsync(ChunkRequest request, CancellationToken cancel)
        {
            if (request == null) throw new ArgumentNullException("request");

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(request.Url);
            req.Method = "GET";
            req.AllowAutoRedirect = true;
            req.AutomaticDecompression = DecompressionMethods.None;
            // 🚨 必须 KeepAlive：一个 128 片 / 16 并发的任务里，每条 worker 要连续领 8 片。
            // KeepAlive=false 时每片都要重新 TCP + TLS 握手（到迅雷 CDN 约 1 秒），
            // 实测 83MB 文件 16 线程：KeepAlive=false → 4.95 MB/s，
            // 自研引擎（HttpClient 连接池复用）→ 9.47 MB/s，差 1.9 倍全在握手。
            // 连接池上限由 NetworkConfig.Apply() 的 DefaultConnectionLimit 统一管理。
            req.KeepAlive = true;
            // 🚨 连接池上限必须跟随调用方的真实并发数（历史上这里硬编码 64，
            // 导致 256 线程任务实际只有 64 条 TCP 连接在飞，速度被卡死在 ~3.2 MB/s）。
            // 只增不减：不会把 NetworkConfig 设好的更大预算降下来。
            int wantLimit = request.ConnectionLimit > 0 ? request.ConnectionLimit : 64;
            if (req.ServicePoint.ConnectionLimit < wantLimit)
            {
                req.ServicePoint.ConnectionLimit = wantLimit;
            }
            req.ServicePoint.Expect100Continue = false;
            req.Timeout = request.TimeoutMs;
            req.ReadWriteTimeout = request.TimeoutMs;

            // 🚨 代理策略必须与 API 层、老引擎完全一致：默认强制直连。
            // HttpWebRequest 在 Proxy 属性未设置时会**继承 Windows「Internet 选项」的系统代理**
            // （微软官方文档原文）。本机代理软件常把 CDN 请求劫持/限速，还会改变 TLS
            // ClientHello 指纹触发夸克/UC 的 WAF —— 表现就是"域名能解析但下不动"。
            // 老引擎（Legacy/SegmentedDownloader.cs）一直显式直连，融合引擎的 transport
            // 漏了这一步，属于回归。
            NetworkOptions.ApplyTo(req);

            if (!string.IsNullOrEmpty(request.UserAgent)) req.UserAgent = request.UserAgent;
            if (!string.IsNullOrEmpty(request.Referer)) req.Referer = request.Referer;
            if (!string.IsNullOrEmpty(request.Cookie)) req.Headers["Cookie"] = request.Cookie;
            if (!string.IsNullOrEmpty(request.Origin)) req.Headers["Origin"] = request.Origin;
            if (!string.IsNullOrEmpty(request.IfRange)) req.Headers["If-Range"] = request.IfRange;

            if (request.ExtraHeaders != null)
            {
                foreach (var kv in request.ExtraHeaders)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    if (string.Equals(kv.Key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(kv.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(kv.Key, "Range", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(kv.Key, "If-Range", StringComparison.OrdinalIgnoreCase)) continue;
                    req.Headers[kv.Key] = kv.Value;
                }
            }

            if (request.End >= request.Start)
                req.AddRange(request.Start, request.End);
            else if (request.Start > 0)
                req.AddRange(request.Start);

            using (cancel.Register(() => { try { req.Abort(); } catch { } }))
            {
                WebResponse resp = await req.GetResponseAsync().ConfigureAwait(false);
                var http = resp as HttpWebResponse;
                var result = new ChunkResponse();
                result.Stream = resp.GetResponseStream();
                result.StatusCode = http != null ? (int)http.StatusCode : 200;

                if (http != null)
                {
                    string range = http.Headers["Content-Range"];
                    if (!string.IsNullOrEmpty(range))
                    {
                        // bytes 0-1023/186678707
                        int slash = range.IndexOf('/');
                        int dash = range.IndexOf('-');
                        if (dash > 0)
                        {
                            string startTxt = range.Substring(0, dash).Trim();
                            int sp = startTxt.IndexOf(' ');
                            if (sp >= 0) startTxt = startTxt.Substring(sp + 1).Trim();
                            long v;
                            if (long.TryParse(startTxt, out v)) result.ContentRangeStart = v;
                        }
                        if (slash > 0 && slash + 1 < range.Length)
                        {
                            long v;
                            if (long.TryParse(range.Substring(slash + 1).Trim(), out v))
                                result.ContentRangeTotal = v;
                        }
                    }
                    result.ETag = http.Headers["ETag"];
                    result.LastModified = http.Headers["Last-Modified"];
                }

                return result;
            }
        }
    }
}
