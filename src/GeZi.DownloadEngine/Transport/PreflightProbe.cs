// GeZi 鸽子下载器
// Copyright (C) 2026 GeZi contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// 下载前的连通性预检（DNS 解析 + TCP 握手，带退避重试）。
//
// 🚨 为什么需要它（2026-10-25 实测事故）：
// 融合引擎一次性开满 256 条连接，全部同时做**同步** DNS 解析
// （NetworkConfig 里 DnsRefreshTimeout=0，且 .NET Framework 的 DNS 是同步的）。
// 只要那一刻 DNS 抖动一下，256 条连接会在 1 秒内全部报
// 「未能解析此远程名称: 'xxx'」—— 任务直接判死，用户只看到一行英文原始异常，
// 既没有重试也没有解释。而域名本身并没有下线
// （实测 dl-pc-sz-cf.pds.uc.cn 可正常解析到 3 个 IP）。
//
// 预检把「一次性 256 个并发解析」换成「先解析一次，失败退避重试」：
// 抖动期间多等几百毫秒就能过去，而不是把整个任务判死。

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.DownloadEngine.Transport
{
    /// <summary>
    /// 下载前的连通性预检：先确认域名能解析、端口能握手，再开并发。
    /// </summary>
    public static class PreflightProbe
    {
        /// <summary>最多尝试次数（含首次）。</summary>
        private const int MaxAttempts = 3;

        /// <summary>单次 TCP 握手超时（毫秒）。</summary>
        private const int TcpTimeoutMs = 5000;

        /// <summary>各次重试前的等待（毫秒）。DNS 抖动通常几百毫秒就恢复。</summary>
        private static readonly int[] BackoffMs = { 400, 1200, 3000 };

        /// <summary>
        /// 预检目标地址是否可达。
        /// </summary>
        /// <param name="url">下载直链。</param>
        /// <param name="cancel">取消令牌。</param>
        /// <param name="log">诊断日志（可为 null）。</param>
        /// <returns>成功返回 <c>null</c>；失败返回一句中文原因（可直接显示给用户）。</returns>
        public static async Task<string> CheckAsync(
            string url,
            CancellationToken cancel,
            Action<string> log)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                // URL 都解析不了就别在这里拦，交给后面报更具体的错。
                return null;
            }

            bool https = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            if (!https && !string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            {
                // 非 http(s) 不走这条路。
                return null;
            }

            string host = uri.DnsSafeHost;
            if (string.IsNullOrWhiteSpace(host))
            {
                return null;
            }

            int port = uri.Port > 0 ? uri.Port : (https ? 443 : 80);

            IPAddress literal;
            bool isLiteral = IPAddress.TryParse(host, out literal);

            string lastError = null;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                cancel.ThrowIfCancellationRequested();

                string error = await TryOnceAsync(host, port, isLiteral, cancel).ConfigureAwait(false);
                if (error == null)
                {
                    if (attempt > 1 && log != null)
                    {
                        log(string.Format("网络预检第 {0} 次通过（{1}）", attempt, host));
                    }
                    return null;
                }

                lastError = error;
                if (log != null)
                {
                    log(string.Format(
                        "网络预检第 {0}/{1} 次失败（{2}）：{3}", attempt, MaxAttempts, host, error));
                }

                if (attempt < MaxAttempts)
                {
                    int wait = BackoffMs[Math.Min(attempt - 1, BackoffMs.Length - 1)];
                    await Task.Delay(wait, cancel).ConfigureAwait(false);
                }
            }

            return string.Format(
                "无法连接下载服务器（{0}）：{1}。请检查网络或代理设置后重试。",
                host, lastError ?? "未知原因");
        }

        /// <summary>做一次「解析 + 握手」。成功返回 <c>null</c>，失败返回原因。</summary>
        private static async Task<string> TryOnceAsync(
            string host,
            int port,
            bool isLiteral,
            CancellationToken cancel)
        {
            if (!isLiteral)
            {
                try
                {
                    IPAddress[] addrs = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
                    if (addrs == null || addrs.Length == 0)
                    {
                        return "域名解析失败（没有返回任何地址）";
                    }
                }
                catch (Exception ex)
                {
                    return "域名解析失败：" + ShortMessage(ex);
                }
            }

            TcpClient tcp = null;
            try
            {
                tcp = new TcpClient();
                Task connect = tcp.ConnectAsync(host, port);
                Task timeout = Task.Delay(TcpTimeoutMs, cancel);
                Task done = await Task.WhenAny(connect, timeout).ConfigureAwait(false);
                if (done != connect)
                {
                    // 超时分支：如果其实是用户取消，就让取消异常正常抛出去。
                    cancel.ThrowIfCancellationRequested();
                    return "连接超时（" + TcpTimeoutMs + " 毫秒内未握手成功）";
                }

                // 让连接失败的真实异常冒出来（WhenAny 不会抛）。
                await connect.ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return "连接失败：" + ShortMessage(ex);
            }
            finally
            {
                if (tcp != null)
                {
                    try { tcp.Close(); }
                    catch { /* 关连接失败无关紧要 */ }
                }
            }
        }

        /// <summary>把异常压成一句人话（去掉 AggregateException 包装）。</summary>
        private static string ShortMessage(Exception ex)
        {
            Exception e = ex;

            var agg = e as AggregateException;
            if (agg != null && agg.InnerExceptions.Count > 0)
            {
                e = agg.InnerExceptions[0];
            }

            var web = e as WebException;
            if (web != null && web.Status == WebExceptionStatus.NameResolutionFailure)
            {
                return "域名解析失败";
            }

            var sock = e as SocketException;
            if (sock != null && sock.SocketErrorCode == SocketError.HostNotFound)
            {
                return "域名解析失败";
            }

            string msg = e.Message;
            if (string.IsNullOrWhiteSpace(msg) && e.InnerException != null)
            {
                msg = e.InnerException.Message;
            }

            return string.IsNullOrWhiteSpace(msg) ? e.GetType().Name : msg;
        }
    }
}
