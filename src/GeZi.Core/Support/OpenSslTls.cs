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
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 用 **OpenSSL** 发 HTTPS 请求的最小传输层（2026-10-11 新增，用来取代内置 CPython）。
    /// <c>pan.quark.cn</c> 的前置 ALB 按 **TLS ClientHello 指纹**过滤。实测（2026-10-11，均强制直连）：
    /// <code>
    ///   SChannel（.NET HttpClient / Windows 自带 curl）        → ❌ 连接被重置
    ///   OpenSSL 3.0.18（系统 Python 3.14 的 ssl）              → ❌ 连接被强制关闭
    ///   BouncyCastle 2.7（纯托管 TLS，含 TLS1.2/1.3 两种组合）  → ❌ 同上
    ///   OpenSSL 3.5.7（内置 CPython 自带的那套）                → ✅ HTTP 200
    /// </code>
    /// ⇒ **只有 OpenSSL 3.5.7 能过**，所以既不能换托管栈、也不能换版本，
    ///   只能**直接调用项目里已经固化的那份 OpenSSL 3.5.7**
    ///   （<c>lib/OpenSSL/libssl-3-x64.dll</c> + <c>libcrypto-3-x64.dll</c>）。
    /// 同一件事以前是"起一个 CPython 子进程跑 .py"，
    /// 代价是包体积 +11MB、并且带着"解释器 + 脚本 + 子进程"这组特征（会被杀软当成
    /// PyInstaller 式载荷，实测命中 BitDefender 系的 <c>Gen:Variant.Yogi</c>）。
    /// 现在改成 P/Invoke，**指纹完全一致，但不再需要解释器**。
    /// 本类刻意做得很小：只发 GET、只读到一个响应就结束
    /// （固定 <c>Connection: close</c>）。它<b>不是</b>通用 HTTP 客户端 ——
    /// 业务接口一律走 <c>QuarkClient</c> / <c>HttpClient</c>。
    /// <para>
    /// 安全：从 Windows 证书存储导出 Root/CA，启用 <c>SSL_VERIFY_PEER</c> +
    /// <c>SSL_set1_host</c>，证书链/主机名任一失败即终止；没有“忽略证书错误”开关。
    /// </para>
    /// ⚠️ <b>平台</b>：DLL 是 x64 的，进程必须是 64 位（AnyCPU 在 64 位系统上即为 x64）。
    ///    32 位系统上 <see cref="IsAvailable"/> 会是 false，调用方需自行降级
    ///    （登录页的「导入并登录」可以手动粘 Cookie）。
    /// </summary>
    public static class OpenSslTls
    {
        private const string SslDll = "libssl-3-x64.dll";
        private const string CryptoDll = "libcrypto-3-x64.dll";

        private static readonly object ProbeLock = new object();
        private static bool _probed;
        private static bool _available;
        private static string _version = "";
        private static string _probeError = "";

        private static readonly object CaLock = new object();
        private static string _caBundlePath;

        /// <summary>本机能否用（DLL 是否加载成功）。首次调用会真正试加载一次。</summary>
        public static bool IsAvailable
        {
            get { EnsureProbed(); return _available; }
        }

        /// <summary>OpenSSL 运行时版本串（诊断用，例如 <c>OpenSSL 3.5.7 9 Jun 2026</c>）。</summary>
        public static string VersionString
        {
            get { EnsureProbed(); return _version; }
        }

        /// <summary>探测失败的原因（诊断用）。</summary>
        public static string ProbeError
        {
            get { EnsureProbed(); return _probeError; }
        }

        /// <summary>
        /// 从 Windows 证书存储导出 Root/CA 到 PEM，供 OpenSSL 做链校验。
        /// 不随包携带 CA bundle：系统根证书会更新，硬编码的 bundle 迟早过期。
        /// </summary>
        private static string EnsureCaBundle()
        {
            lock (CaLock)
            {
                if (!string.IsNullOrEmpty(_caBundlePath) && File.Exists(_caBundlePath))
                    return _caBundlePath;

                // 测试/高级用户可显式指定（仍会做完整证书校验，不是“跳过校验”开关）。
                string overridePath = Environment.GetEnvironmentVariable("GEZI_CA_BUNDLE");
                if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
                {
                    _caBundlePath = overridePath;
                    return overridePath;
                }

                var sb = new StringBuilder();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AppendStore(sb, seen, StoreLocation.LocalMachine, StoreName.Root);
                AppendStore(sb, seen, StoreLocation.CurrentUser, StoreName.Root);
                AppendStore(sb, seen, StoreLocation.LocalMachine, StoreName.CertificateAuthority);
                AppendStore(sb, seen, StoreLocation.CurrentUser, StoreName.CertificateAuthority);

                if (sb.Length == 0)
                    throw new IOException("无法从 Windows 证书存储导出 CA 证书");

                // 优先 LocalAppData（可写、跟随用户）；便携版/受限环境回退到 exe 目录或 temp。
                // 任何一个目录都写不进去时才失败 —— 绝不降级成“无校验”。
                string[] dirs =
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GeZi"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GeZi"),
                    Path.Combine(Path.GetTempPath(), "GeZi"),
                };

                Exception last = null;
                foreach (string dir in dirs)
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        string path = Path.Combine(dir, "cacert.pem");
                        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                        try
                        {
                            if (File.Exists(path))
                                File.Replace(tmp, path, null, true);
                            else
                                File.Move(tmp, path);
                        }
                        catch (IOException)
                        {
                            // 证书文件不是状态数据，复制失败也比“无校验”好。
                            File.Copy(tmp, path, true);
                            try { File.Delete(tmp); } catch { }
                        }

                        _caBundlePath = path;
                        return path;
                    }
                    catch (Exception ex)
                    {
                        if (!(ex is IOException) && !(ex is UnauthorizedAccessException))
                            throw;
                        last = ex;
                    }
                }

                throw new IOException("无法写入 CA bundle（已尝试 LocalAppData / exe 目录 / temp）", last);
            }
        }

        private static void AppendStore(StringBuilder sb, HashSet<string> seen,
            StoreLocation location, StoreName name)
        {
            try
            {
                using (var store = new X509Store(name, location))
                {
                    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                    foreach (X509Certificate2 cert in store.Certificates)
                    {
                        try
                        {
                            string thumb = cert.Thumbprint;
                            if (!string.IsNullOrEmpty(thumb) && !seen.Add(thumb)) continue;

                            string b64 = Convert.ToBase64String(cert.RawData);
                            sb.AppendLine("-----BEGIN CERTIFICATE-----");
                            for (int i = 0; i < b64.Length; i += 64)
                                sb.AppendLine(b64.Substring(i, Math.Min(64, b64.Length - i)));
                            sb.AppendLine("-----END CERTIFICATE-----");
                        }
                        catch
                        {
                            // 个别证书不可导出，跳过；不能因此放弃整份信任根。
                        }
                    }
                }
            }
            catch
            {
                // 某些机器没有 CA 存储，或策略禁止读取；由调用方判断是否还有可用根。
            }
        }

        private static void EnsureProbed()
        {
            lock (ProbeLock)
            {
                if (_probed) return;
                _probed = true;
                try
                {
                    Native.OPENSSL_init_ssl(0, IntPtr.Zero);
                    IntPtr p = Native.OpenSSL_version(0);
                    _version = p == IntPtr.Zero ? "(未知)" : Marshal.PtrToStringAnsi(p);
                    _available = Native.TLS_client_method() != IntPtr.Zero;
                }
                catch (Exception ex)
                {
                    _available = false;
                    _probeError = ex.GetType().Name + ": " + ex.Message;
                }
            }
        }

        // ---------------- 响应 ----------------

        /// <summary>
        /// 单次响应体最多读多少字节（16 MB）。
        /// <para>
        /// 🚨 2026-11-01：原来调用处硬编码 65536（64KB）。UC 的
        /// <c>/1/clouddrive/share/sharepage/detail</c> 在目录文件较多时响应超过 64KB
        /// （实测 64970 字节处被切断）→ 最后一段字符串没闭合 →
        /// 上层 JavaScriptSerializer 抛「ArgumentException: 传入了未终止的字符串」，
        /// 表现为「HTTP 200 + code:0 却报加载目录失败」。
        /// 目录类接口的响应再大也就几百 KB，16MB 是绝对安全的上限。
        /// </para>
        /// </summary>
        private const int MaxResponseBytes = 16 * 1024 * 1024;

        /// <summary>一次请求的结果。</summary>
        public sealed class Response
        {
            /// <summary>HTTP 状态码（-1 = 没拿到响应）。</summary>
            public int StatusCode = -1;

            /// <summary>完整响应头原文（含多行 <c>Set-Cookie</c>）。</summary>
            public string RawHeaders = "";

            /// <summary>响应体（已按 Content-Length / chunked 处理）。</summary>
            public string Body = "";

            /// <summary>从响应头里解析出的所有 Set-Cookie（**只取名字=值，不含属性**）。</summary>
            public readonly List<KeyValuePair<string, string>> Cookies =
                new List<KeyValuePair<string, string>>();

            /// <summary>
            /// 响应体是否**可能被截断**（读满了 <see cref="MaxResponseBytes"/> 仍未结束）。
            /// <para>
            /// 2026-11-01 新增：原来 <c>ReadAll</c> 硬编码 64KB 上限，UC 的目录响应
            /// 超过 64KB 就会被切掉最后一个字符串 → 上层 JSON 解析抛「未终止的字符串」，
            /// 而 HTTP 状态是 200，极难定位。现在上限放大，并保留这个标记供排查。
            /// </para>
            /// </summary>
            public bool Truncated;

            public bool Ok { get { return StatusCode >= 200 && StatusCode < 400; } }
        }

        // ---------------- 请求 ----------------

        /// <summary>
        /// 发一个 HTTPS GET。
        /// </summary>
        /// <param name="host">主机名（同时用作 SNI 与 Host 头），如 <c>pan.quark.cn</c>。</param>
        /// <param name="pathAndQuery">以 <c>/</c> 开头的路径 + 查询串。</param>
        /// <param name="headers">额外请求头（可含 Cookie / User-Agent / Referer…）。</param>
        /// <param name="timeoutMs">连接与读写的超时（毫秒）。</param>
        /// <exception cref="IOException">握手失败、连接被重置等。</exception>
        public static Response Get(string host, string pathAndQuery,
            IDictionary<string, string> headers = null, int timeoutMs = 20000)
        {
            return Send(host, "GET", pathAndQuery, headers, null, timeoutMs);
        }

        /// <summary>
        /// 发一个 HTTPS POST（<c>application/json</c>）。
        /// </summary>
        /// <remarks>
        /// UC 的 <c>pc-api.uc.cn</c> 与 <c>pan.quark.cn</c> 同源同策：前置 ALB 按 TLS
        /// ClientHello 指纹过滤，.NET 的 SChannel 握手一律被 RST。凡是写操作（删除、
        /// 重命名、移动）都走这里，否则「能读不能改」会变成一半功能可用。
        /// </remarks>
        public static Response Post(string host, string pathAndQuery,
            IDictionary<string, string> headers = null, string jsonBody = null,
            int timeoutMs = 20000)
        {
            return Send(host, "POST", pathAndQuery, headers, jsonBody ?? "", timeoutMs);
        }

        /// <summary>GET / POST 共用的一条 TLS 连接。</summary>
        public static Response Send(string host, string method, string pathAndQuery,
            IDictionary<string, string> headers, string body, int timeoutMs)
        {
            if (string.IsNullOrEmpty(host)) throw new ArgumentNullException("host");
            if (string.IsNullOrEmpty(method)) method = "GET";
            if (string.IsNullOrEmpty(pathAndQuery)) pathAndQuery = "/";
            if (!pathAndQuery.StartsWith("/")) pathAndQuery = "/" + pathAndQuery;

            EnsureProbed();
            if (!_available)
                throw new IOException("OpenSSL 不可用（" + _probeError + "）");

            IntPtr ctx = IntPtr.Zero, ssl = IntPtr.Zero;
            TcpClient tcp = null;
            try
            {
                OpenTls(host, timeoutMs, out ctx, out ssl, out tcp);

                byte[] bodyBytes = string.IsNullOrEmpty(body)
                    ? new byte[0]
                    : Encoding.UTF8.GetBytes(body);
                byte[] head = Encoding.UTF8.GetBytes(
                    BuildRequest(host, method, pathAndQuery, headers, bodyBytes.Length));
                byte[] req = new byte[head.Length + bodyBytes.Length];
                Buffer.BlockCopy(head, 0, req, 0, head.Length);
                Buffer.BlockCopy(bodyBytes, 0, req, head.Length, bodyBytes.Length);
                if (Native.SSL_write(ssl, req, req.Length) <= 0)
                    throw new IOException("SSL_write 失败: " + Native.ErrText());

                // 🚨🚨 2026-11-01 修复（用户报「UC 网盘无法正常获取文件目录」的真根因）：
                //    这里原来是硬编码 **65536（64KB）** 上限。
                //    UC 的 /sharepage/detail 响应在文件多时会超过 64KB
                //    （实测 64970 字节处被切断），于是 JSON 最后一个字符串没有闭合，
                //    上层 JavaScriptSerializer 抛「ArgumentException: 传入了未终止的字符串」，
                //    而 HTTP 状态是 200、code 是 0 —— 表现就是"服务端明明成功，界面却报加载失败"。
                //    ⇒ 上限提到 16MB（目录响应再大也够），并显式检测是否真被截断。
                byte[] raw = ReadAll(ssl, MaxResponseBytes);
                var parsed = ParseResponse(raw);
                // 若读满上限仍未结束，说明响应可能不完整 —— 让上层能看见，而不是静默解析失败。
                if (raw.Length >= MaxResponseBytes)
                    parsed.Truncated = true;
                return parsed;
            }
            finally
            {
                if (ssl != IntPtr.Zero) Native.SSL_free(ssl);
                if (ctx != IntPtr.Zero) Native.SSL_CTX_free(ctx);
                if (tcp != null) tcp.Close();
            }
        }

        /// <summary>
        /// 建立一条完成握手与证书校验的 TLS 连接。失败时自行释放半成品。
        /// </summary>
        private static void OpenTls(string host, int timeoutMs,
            out IntPtr ctx, out IntPtr ssl, out TcpClient tcp)
        {
            ctx = IntPtr.Zero;
            ssl = IntPtr.Zero;
            tcp = null;
            try
            {
                ctx = Native.SSL_CTX_new(Native.TLS_client_method());
                if (ctx == IntPtr.Zero)
                    throw new IOException("SSL_CTX_new 失败: " + Native.ErrText());

                // 必须校验证书链与主机名：这条通道承载 ticket / Cookie，
                // 不能再用“业务层 JSON 看起来像夸克”替代传输层信任。
                Native.SSL_CTX_set_verify(ctx, Native.SSL_VERIFY_PEER, IntPtr.Zero);
                string caBundle = EnsureCaBundle();
                if (Native.SSL_CTX_load_verify_locations(ctx, caBundle, null) != 1)
                {
                    // 显式 bundle 失败时再试一次 OpenSSL 的默认路径；
                    // 两者都失败就终止，绝不降级成 SSL_VERIFY_NONE。
                    if (Native.SSL_CTX_set_default_verify_paths(ctx) != 1)
                        throw new IOException("无法装载 CA 信任根: " + Native.ErrText());
                }

                tcp = new TcpClient();
                tcp.ReceiveTimeout = timeoutMs;
                tcp.SendTimeout = timeoutMs;
                tcp.Connect(host, 443);

                ssl = Native.SSL_new(ctx);
                if (ssl == IntPtr.Zero)
                    throw new IOException("SSL_new 失败: " + Native.ErrText());

                IntPtr bio = Native.BIO_new_socket(tcp.Client.Handle, Native.BIO_NOCLOSE);
                Native.SSL_set_bio(ssl, bio, bio);

                // SNI（SSL_set_tlsext_host_name 是宏，这里展开成 SSL_ctrl）
                IntPtr namePtr = Marshal.StringToHGlobalAnsi(host);
                try
                {
                    Native.SSL_ctrl(ssl, Native.SSL_CTRL_SET_TLSEXT_HOSTNAME,
                        Native.TLSEXT_NAMETYPE_host_name, namePtr);
                }
                finally { Marshal.FreeHGlobal(namePtr); }

                // 主机名校验：只验链不验主机名，等于允许“任意受信任证书”冒充 pan.quark.cn。
                if (Native.SSL_set1_host(ssl, host) != 1)
                    throw new IOException("SSL_set1_host 失败: " + Native.ErrText());

                // ⚠️ 刻意**不设 ALPN** —— 与内置 CPython 的 ssl 默认行为一致（实测能过）。
                int rc = Native.SSL_connect(ssl);
                if (rc != 1)
                {
                    int err = Native.SSL_get_error(ssl, rc);
                    throw new IOException("TLS 握手失败 (SSL_get_error=" + err + "): " + Native.ErrText());
                }

                long verify = Native.SSL_get_verify_result(ssl);
                if (verify != Native.X509_V_OK)
                    throw new IOException("TLS 证书校验失败 (" + verify + "): " + Native.VerifyErrorText(verify));
            }
            catch
            {
                if (ssl != IntPtr.Zero) Native.SSL_free(ssl);
                if (ctx != IntPtr.Zero) Native.SSL_CTX_free(ctx);
                if (tcp != null) tcp.Close();
                ctx = IntPtr.Zero;
                ssl = IntPtr.Zero;
                tcp = null;
                throw;
            }
        }

        // ---------------- 流式下载 ----------------

        /// <summary>流式下载一次的结果。</summary>
        public sealed class StreamResult
        {
            /// <summary>HTTP 状态码（-1 = 没拿到响应）。</summary>
            public int StatusCode = -1;

            /// <summary>响应头原文。</summary>
            public string RawHeaders = "";

            /// <summary>响应头里 <c>Content-Length</c> 的值（没有则为 -1）。</summary>
            public long ContentLength = -1;

            /// <summary>响应头里 <c>Content-Range</c> 的起点（没有则为 -1）。</summary>
            public long ContentRangeStart = -1;

            /// <summary>响应头里 <c>Content-Range</c> 的资源总长（没有则为 -1）。</summary>
            public long ContentRangeTotal = -1;

            /// <summary>实际写入目标流的字节数。</summary>
            public long Written;

            /// <summary>读到的字节超过了 <c>maxBytes</c>（说明服务端没按 Range 截断）。</summary>
            public bool Truncated;
        }

        /// <summary>
        /// 走 OpenSSL 通道下载一段字节，边读边写入 <paramref name="output"/>，不整包进内存。
        /// </summary>
        /// <remarks>
        /// UC 的下载 CDN <c>dl-pc-sz-cf.pds.uc.cn</c> 与 <c>pc-api.uc.cn</c> 同策：按 TLS
        /// ClientHello 指纹过滤，Windows 自带的 SChannel 一律回
        /// <c>403 RequestDeniedByCallback / require login [auth not found]</c>。
        /// 实测同一个直链、同一份 Cookie 换到本通道立刻回 <c>206</c>，因此 UC 的分片
        /// 下载必须走这里，不能走 <see cref="System.Net.Http.HttpClient"/>。
        /// </remarks>
        /// <param name="host">主机名（同时用作 SNI 与 Host 头）。</param>
        /// <param name="pathAndQuery">以 <c>/</c> 开头的路径 + 查询串。</param>
        /// <param name="headers">额外请求头（必须含 Range 才叫分片）。</param>
        /// <param name="maxBytes">本次最多读取多少字节（即分片长度）。</param>
        /// <param name="output">写入目标（通常是已定位到分片起点的文件流）。</param>
        /// <param name="timeoutMs">连接超时。</param>
        /// <param name="readTimeoutMs">单次读超时；&lt;=0 时沿用 <paramref name="timeoutMs"/>。</param>
        public static StreamResult DownloadTo(string host, string pathAndQuery,
            IDictionary<string, string> headers, long maxBytes, Stream output,
            int timeoutMs, int readTimeoutMs = 0)
        {
            if (string.IsNullOrEmpty(host)) throw new ArgumentNullException("host");
            if (output == null) throw new ArgumentNullException("output");
            if (maxBytes < 0) maxBytes = 0;
            if (string.IsNullOrEmpty(pathAndQuery)) pathAndQuery = "/";
            if (!pathAndQuery.StartsWith("/")) pathAndQuery = "/" + pathAndQuery;

            EnsureProbed();
            if (!_available)
                throw new IOException("OpenSSL 不可用（" + _probeError + "）");

            IntPtr ctx = IntPtr.Zero, ssl = IntPtr.Zero;
            TcpClient tcp = null;
            try
            {
                OpenTls(host, timeoutMs, out ctx, out ssl, out tcp);
                if (readTimeoutMs > 0) tcp.ReceiveTimeout = readTimeoutMs;

                byte[] head = Encoding.UTF8.GetBytes(BuildRequest(host, "GET", pathAndQuery, headers, 0));
                if (Native.SSL_write(ssl, head, head.Length) <= 0)
                    throw new IOException("SSL_write 失败: " + Native.ErrText());

                var result = new StreamResult();
                byte[] buf = new byte[65536];

                // ---- 1. 先读响应头（直到 \r\n\r\n），头部上限 64 KiB ----
                var headerBuf = new MemoryStream();
                int headerEnd = -1;
                byte[] sep = new byte[] { 13, 10, 13, 10 };
                while (headerEnd < 0)
                {
                    int n = Native.SSL_read(ssl, buf, buf.Length);
                    if (n <= 0)
                    {
                        int err = Native.SSL_get_error(ssl, n);
                        if (headerBuf.Length == 0)
                            throw new IOException("读响应头失败 (SSL_get_error=" + err + "): " + Native.ErrText());
                        break;
                    }
                    headerBuf.Write(buf, 0, n);
                    if (headerBuf.Length > 65536)
                        throw new IOException("响应头超过 64 KiB，疑似不是 HTTP 响应");
                    headerEnd = IndexOf(headerBuf.ToArray(), sep);
                }

                byte[] all = headerBuf.ToArray();
                int bodyStart = headerEnd >= 0 ? headerEnd + 4 : all.Length;
                byte[] headBytes = Sub(all, 0, headerEnd >= 0 ? headerEnd : all.Length);
                result.RawHeaders = Encoding.UTF8.GetString(headBytes);
                ParseStatusAndRange(result, result.RawHeaders);

                // ---- 2. 把已经读进来的那部分响应体先写掉 ----
                int already = all.Length - bodyStart;
                if (already > 0)
                {
                    int take = (int)Math.Min(already, maxBytes);
                    output.Write(all, bodyStart, take);
                    result.Written += take;
                    if (take < already) result.Truncated = true;
                }

                // ---- 3. 继续读到 EOF 或读满 maxBytes ----
                while (!result.Truncated && result.Written < maxBytes)
                {
                    int n = Native.SSL_read(ssl, buf, buf.Length);
                    if (n <= 0)
                    {
                        int err = Native.SSL_get_error(ssl, n);
                        // 6 = SSL_ERROR_ZERO_RETURN（对端正常关闭）
                        if (err != 6 && result.Written == 0)
                            throw new IOException("读响应体失败 (SSL_get_error=" + err + "): " + Native.ErrText());
                        break;
                    }
                    int take = (int)Math.Min(n, maxBytes - result.Written);
                    output.Write(buf, 0, take);
                    result.Written += take;
                    if (take < n) result.Truncated = true;
                }

                return result;
            }
            finally
            {
                if (ssl != IntPtr.Zero) Native.SSL_free(ssl);
                if (ctx != IntPtr.Zero) Native.SSL_CTX_free(ctx);
                if (tcp != null) tcp.Close();
            }
        }

        // ---------------- 分片流式通道（供下载引擎的 IChunkTransport 使用） ----------------

        /// <summary>
        /// 一个已经握手、发完请求、读完响应头的只读流；每次 <see cref="Read"/> 直接从 SSL 连接读，
        /// 不把整个分片先攒进内存。给 bezzad 那类"按 Range 拿 Stream 再自己 Read"的引擎用。
        /// </summary>
        /// <remarks>
        /// 与 <see cref="DownloadTo"/> 的区别：那个是"一次调用读完一段写进目标流"，
        /// 这个是"交出一个 Stream，由调用方决定读多少、什么时候停"，因此支持引擎侧
        /// 的暂停、取消、超时和慢连接抢占。
        /// </remarks>
        public sealed class OpenChunkStream : Stream
        {
            private IntPtr _ssl;
            private IntPtr _ctx;
            private TcpClient _tcp;
            private readonly byte[] _readBuf = new byte[65536];
            private byte[] _pending;
            private int _pendingPos;
            private int _pendingLen;
            private long _remaining;
            private volatile bool _disposed;
            private int _disposeOnce;

            // 🚨 2026-10-25 修「下 UC 大文件突然闪退」：
            //    原来是 Dispose 里 _tcp.Close() 之后**立刻** SSL_free，
            //    而此刻另一个线程可能正卡在 SSL_read 里面还没返回。
            //    socket 关闭只是让 SSL_read 尽快出错，**不保证读线程已经离开
            //    libssl 内部代码**；SSL_free 一执行，读线程再碰那块内存就是
            //    use-after-free → 原生访问违规（0xC0000005）→ 托管层 catch 不到
            //    → 进程直接消失（事件日志里栈顶就是 SSL_read）。
            //    片数越多、抢占越频繁（UC 大文件必然如此）命中率越高。
            //
            // 修法：引用计数。读线程进 SSL_read 前 _readers++，出来后 --；
            //    Dispose 只置「待释放」标记，最后一个退出的读线程负责真正 free。
            //    这样 SSL_free 永远不可能和 SSL_read 并发，也不用任何锁阻塞取消。
            private int _readers;
            private int _freePending;
            private int _freeOnce;

            /// <summary>HTTP 状态码。</summary>
            public int StatusCode { get; private set; }

            /// <summary>响应头原文。</summary>
            public string RawHeaders { get; private set; }

            /// <summary><c>Content-Range</c> 起点（没有则 -1）。</summary>
            public long ContentRangeStart { get; private set; }

            /// <summary><c>Content-Range</c> 里的资源总长（没有则 -1）。</summary>
            public long ContentRangeTotal { get; private set; }

            /// <summary>响应头里的 <c>ETag</c>（没有则 null）。</summary>
            public string ETag { get; private set; }

            /// <summary>响应头里的 <c>Last-Modified</c>（没有则 null）。</summary>
            public string LastModified { get; private set; }

            internal OpenChunkStream(IntPtr ctx, IntPtr ssl, TcpClient tcp,
                byte[] pending, int pendingLen, int statusCode, string rawHeaders,
                long rangeStart, long rangeTotal, string etag, string lastModified,
                long expected)
            {
                _ctx = ctx;
                _ssl = ssl;
                _tcp = tcp;
                _pending = pending;
                _pendingPos = 0;
                _pendingLen = pendingLen;
                StatusCode = statusCode;
                RawHeaders = rawHeaders;
                ContentRangeStart = rangeStart;
                ContentRangeTotal = rangeTotal;
                ETag = etag;
                LastModified = lastModified;
                _remaining = expected > 0 ? expected : -1;
            }

            public override bool CanRead { get { return !_disposed; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }

            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException("buffer");
                if (offset < 0 || count < 0 || offset + count > buffer.Length)
                    throw new ArgumentOutOfRangeException("count");
                if (_disposed) throw new ObjectDisposedException("OpenChunkStream");
                if (count == 0) return 0;
                if (_remaining == 0) return 0;

                // 1. 先把响应头之后顺手读到的那点响应体吐出去
                if (_pendingLen > _pendingPos)
                {
                    int n = Math.Min(count, _pendingLen - _pendingPos);
                    Buffer.BlockCopy(_pending, _pendingPos, buffer, offset, n);
                    _pendingPos += n;
                    if (_remaining > 0) _remaining -= n;
                    return n;
                }
                _pending = null;

                // 2. 直接从 SSL 连接读（SSL_read 只能写进缓冲起点，所以先读进内部缓冲）
                int want = count;
                if (want > _readBuf.Length) want = _readBuf.Length;
                if (_remaining > 0 && want > _remaining) want = (int)_remaining;

                // 🚨 登记「我正在用原生句柄」，Dispose 看到 _readers > 0 就不会 free。
                //    自增**必须**在检查 _disposed 之前：否则 Dispose 可能正好在
                //    「检查通过」与「自增」之间读到 0 而释放句柄 → 这里拿到悬空指针。
                Interlocked.Increment(ref _readers);
                try
                {
                    if (_disposed || _ssl == IntPtr.Zero)
                        throw new ObjectDisposedException("OpenChunkStream");

                    int rc = Native.SSL_read(_ssl, _readBuf, want);
                    if (rc > 0)
                    {
                        Buffer.BlockCopy(_readBuf, 0, buffer, offset, rc);
                        if (_remaining > 0) _remaining -= rc;
                        return rc;
                    }

                    int err = Native.SSL_get_error(_ssl, rc);
                    // 连接被 Close() 强行打断：bezzad 靠 ObjectDisposedException 识别"读超时"
                    if (_disposed) throw new ObjectDisposedException("OpenChunkStream");
                    // 6 = SSL_ERROR_ZERO_RETURN，对端正常关闭，等价 EOF
                    if (err == 6) return 0;
                    // 5 = SSL_ERROR_SYSCALL：socket 被关闭或读超时
                    if (err == 5) throw new IOException("OpenSSL 连接中断（SSL_ERROR_SYSCALL）");
                    throw new IOException("SSL_read 失败 (SSL_get_error=" + err + "): " + Native.ErrText());
                }
                finally
                {
                    // 我是最后一个退出的读线程、且 Dispose 已经在等 → 由我来 free。
                    if (Interlocked.Decrement(ref _readers) == 0
                        && Volatile.Read(ref _freePending) != 0)
                        FreeNative();
                }
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

            /// <summary>
            /// 同步读的异步包装。
            ///
            /// **为什么必须重写**：<c>SSL_read</c> 本身就是阻塞调用，而 .NET Framework 4.8 里
            /// <see cref="Stream.ReadAsync(byte[],int,int,CancellationToken)"/> 的默认实现会走
            /// <c>BeginRead/EndRead</c> → 基类用 <c>Task.Run</c> 把同步 <c>Read</c> 丢到线程池。
            /// 一个 186MB 的文件按 8~16KB 一次读，就是 **2 万多次线程池调度**，
            /// 每次还伴随 bezzad 那边新建 <c>CancellationTokenSource</c> 与注册回调 ——
            /// 实测把每连接吞吐从 168 KB/s 压到 28 KB/s（2.69 MB/s → 0.90 MB/s）。
            ///
            /// 这里直接在当前线程同步读、返回已完成的 Task。调用链
            /// （<c>OpenSslChunkTransport</c> 已用 <c>Task.Run</c> 隔离）本来就在线程池线程上，
            /// 因此不会阻塞 UI；效果等价于自研引擎「每个 worker 一条线程同步读」的模型。
            /// 取消语义不变：连接被 <see cref="Dispose"/> 打断时 <see cref="Read"/> 抛
            /// <c>ObjectDisposedException</c>，bezzad 据此识别读超时并重试。
            /// </summary>
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
                CancellationToken cancellationToken)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    var tcs = new TaskCompletionSource<int>();
                    tcs.SetCanceled();
                    return tcs.Task;
                }

                try
                {
                    return Task.FromResult(Read(buffer, offset, count));
                }
                catch (Exception ex)
                {
                    var tcs = new TaskCompletionSource<int>();
                    tcs.SetException(ex);
                    return tcs.Task;
                }
            }

            /// <summary>
            /// 关闭连接。**必须幂等** —— 这条流会被多层包装各释放一次：
            /// <c>ThrottledStream.Dispose</c> → <c>_baseStream.Close()</c>，
            /// 外层 <c>ChunkResponse.Dispose</c> → <c>Stream.Dispose()</c>，
            /// 还有 bezzad 的取消注册 <c>cancelToken.Register(stream.Close)</c>。
            /// 2026-10-24 实测踩到：原实现直接 <c>SSL_free(_ssl)</c> 且不置零，
            /// 第二次进入就是 **double free** —— 进程以 0xC0000374（堆损坏）
            /// 或 0xC0000005（访问冲突）直接崩，托管层 catch 不到，表现为
            /// 「下到 10% 突然消失」且没有任何异常文本。
            /// 片数越多（adaptive 256 片）释放路径越频繁，越容易命中。
            /// 所以这里用 Interlocked 抢一个一次性标记，谁先到谁真正释放，
            /// 并把句柄置零，后续调用直接返回。
            /// </summary>
            protected override void Dispose(bool disposing)
            {
                if (Interlocked.Exchange(ref _disposeOnce, 1) != 0)
                    return;   // 已经释放过，绝不能再 SSL_free 一次

                _disposed = true;

                // 先断 socket：把卡在 SSL_read 里的线程尽快踢出来。
                try { if (_tcp != null) { _tcp.Close(); _tcp = null; } } catch { }

                // 再登记「待释放」。此刻如果还有读线程在 SSL_read 里，
                // 由最后一个退出的那个负责 free（见 Read 的 finally）——
                // 绝不在这里直接 SSL_free，那正是闪退的根因。
                Interlocked.Exchange(ref _freePending, 1);
                if (Volatile.Read(ref _readers) == 0)
                    FreeNative();

                base.Dispose(disposing);
            }


            /// <summary>
            /// 真正释放原生句柄。**幂等**：Read 的 finally 与 Dispose 都可能调到这里，
            /// 靠 <c>_freeOnce</c> 保证只生效一次。
            /// ⚠️ 必须先 <c>SSL_free</c> 再 <c>SSL_CTX_free</c> —— 顺序反了同样是悬空指针。
            /// </summary>
            private void FreeNative()
            {
                if (Interlocked.Exchange(ref _freeOnce, 1) != 0)
                    return;
                try
                {
                    if (_ssl != IntPtr.Zero) { Native.SSL_free(_ssl); _ssl = IntPtr.Zero; }
                }
                catch { }
                try
                {
                    if (_ctx != IntPtr.Zero) { Native.SSL_CTX_free(_ctx); _ctx = IntPtr.Zero; }
                }
                catch { }
            }
        }

        /// <summary>
        /// 走 OpenSSL 打开一个 Range 请求，返回可逐块读取的流（响应头已解析完毕）。
        /// </summary>
        /// <param name="host">主机名（同时用作 SNI 与 Host 头）。</param>
        /// <param name="pathAndQuery">以 <c>/</c> 开头的路径 + 查询串。</param>
        /// <param name="headers">额外请求头（Range 必填）。</param>
        /// <param name="timeoutMs">连接超时。</param>
        /// <param name="readTimeoutMs">单次读超时；&lt;=0 时沿用 <paramref name="timeoutMs"/>。</param>
        /// <param name="expectedBytes">期望字节数；&gt;0 时读到这么多即 EOF，&lt;=0 时读到对端关闭。</param>
        public static OpenChunkStream OpenChunk(string host, string pathAndQuery,
            IDictionary<string, string> headers, int timeoutMs, int readTimeoutMs = 0,
            long expectedBytes = 0)
        {
            if (string.IsNullOrEmpty(host)) throw new ArgumentNullException("host");
            if (string.IsNullOrEmpty(pathAndQuery)) pathAndQuery = "/";
            if (!pathAndQuery.StartsWith("/")) pathAndQuery = "/" + pathAndQuery;

            EnsureProbed();
            if (!_available)
                throw new IOException("OpenSSL 不可用（" + _probeError + "）");

            IntPtr ctx = IntPtr.Zero, ssl = IntPtr.Zero;
            TcpClient tcp = null;
            bool handoff = false;
            try
            {
                OpenTls(host, timeoutMs, out ctx, out ssl, out tcp);
                if (readTimeoutMs > 0) tcp.ReceiveTimeout = readTimeoutMs;

                byte[] head = Encoding.UTF8.GetBytes(BuildRequest(host, "GET", pathAndQuery, headers, 0));
                if (Native.SSL_write(ssl, head, head.Length) <= 0)
                    throw new IOException("SSL_write 失败: " + Native.ErrText());

                // 读响应头（到 \r\n\r\n 为止，上限 64 KiB）
                var headerBuf = new MemoryStream();
                byte[] buf = new byte[65536];
                int headerEnd = -1;
                byte[] sep = new byte[] { 13, 10, 13, 10 };
                while (headerEnd < 0)
                {
                    int n = Native.SSL_read(ssl, buf, buf.Length);
                    if (n <= 0)
                    {
                        int e = Native.SSL_get_error(ssl, n);
                        if (headerBuf.Length == 0)
                            throw new IOException("读响应头失败 (SSL_get_error=" + e + "): " + Native.ErrText());
                        break;
                    }
                    headerBuf.Write(buf, 0, n);
                    if (headerBuf.Length > 65536)
                        throw new IOException("响应头超过 64 KiB，疑似不是 HTTP 响应");
                    headerEnd = IndexOf(headerBuf.ToArray(), sep);
                }

                byte[] all = headerBuf.ToArray();
                int bodyStart = headerEnd >= 0 ? headerEnd + 4 : all.Length;
                byte[] headBytes = Sub(all, 0, headerEnd >= 0 ? headerEnd : all.Length);
                string rawHeaders = Encoding.UTF8.GetString(headBytes);

                var probe = new StreamResult();
                ParseStatusAndRange(probe, rawHeaders);

                int already = all.Length - bodyStart;
                byte[] pending = new byte[already];
                if (already > 0) Buffer.BlockCopy(all, bodyStart, pending, 0, already);

                string etag = null, lastMod = null;
                foreach (string ln in rawHeaders.Split(new[] { "\r\n" }, StringSplitOptions.None))
                {
                    int colon = ln.IndexOf(':');
                    if (colon <= 0) continue;
                    string name = ln.Substring(0, colon).Trim();
                    string val = ln.Substring(colon + 1).Trim();
                    if (string.Equals(name, "ETag", StringComparison.OrdinalIgnoreCase)) etag = val;
                    else if (string.Equals(name, "Last-Modified", StringComparison.OrdinalIgnoreCase)) lastMod = val;
                }

                var stream = new OpenChunkStream(ctx, ssl, tcp, pending, already,
                    probe.StatusCode, rawHeaders, probe.ContentRangeStart,
                    probe.ContentRangeTotal, etag, lastMod, expectedBytes);
                handoff = true;
                return stream;
            }
            finally
            {
                if (!handoff)
                {
                    if (ssl != IntPtr.Zero) Native.SSL_free(ssl);
                    if (ctx != IntPtr.Zero) Native.SSL_CTX_free(ctx);
                    if (tcp != null) tcp.Close();
                }
            }
        }

        private static void ParseStatusAndRange(StreamResult r, string rawHeaders)
        {
            if (string.IsNullOrEmpty(rawHeaders)) return;
            string[] lines = rawHeaders.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length > 0)
            {
                string first = lines[0];
                int sp = first.IndexOf(' ');
                if (sp > 0)
                {
                    int sp2 = first.IndexOf(' ', sp + 1);
                    string code = sp2 > 0 ? first.Substring(sp + 1, sp2 - sp - 1) : first.Substring(sp + 1);
                    int c;
                    if (int.TryParse(code.Trim(), out c)) r.StatusCode = c;
                }
            }
            for (int i = 1; i < lines.Length; i++)
            {
                string ln = lines[i];
                int colon = ln.IndexOf(':');
                if (colon <= 0) continue;
                string name = ln.Substring(0, colon).Trim();
                string value = ln.Substring(colon + 1).Trim();
                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    long v;
                    if (long.TryParse(value, out v)) r.ContentLength = v;
                }
                else if (string.Equals(name, "Content-Range", StringComparison.OrdinalIgnoreCase))
                {
                    // bytes 0-1023/186678707
                    int slash = value.IndexOf('/');
                    int dash = value.IndexOf('-');
                    if (dash > 0)
                    {
                        string startTxt = value.Substring(0, dash).Trim();
                        int sp = startTxt.IndexOf(' ');
                        if (sp >= 0) startTxt = startTxt.Substring(sp + 1).Trim();
                        long v;
                        if (long.TryParse(startTxt, out v)) r.ContentRangeStart = v;
                    }
                    if (slash > 0 && slash + 1 < value.Length)
                    {
                        string totalTxt = value.Substring(slash + 1).Trim();
                        long v;
                        if (long.TryParse(totalTxt, out v)) r.ContentRangeTotal = v;
                    }
                }
            }
        }

        private static string BuildRequest(string host, string method, string pathAndQuery,
            IDictionary<string, string> headers, int contentLength)
        {
            var sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(pathAndQuery).Append(" HTTP/1.1\r\n");
            sb.Append("Host: ").Append(host).Append("\r\n");
            sb.Append("Accept: */*\r\n");
            sb.Append("Connection: close\r\n");
            if (contentLength > 0)
            {
                sb.Append("Content-Length: ").Append(contentLength).Append("\r\n");
                // 调用方（QuarkClient）已在 headers 里带了 Content-Type 就沿用；
                // 没带时按 UC/夸克 API 的实际约定补 JSON。
                bool hasType = false;
                if (headers != null)
                {
                    foreach (var kv in headers)
                    {
                        if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                        {
                            hasType = true;
                            break;
                        }
                    }
                }
                if (!hasType)
                    sb.Append("Content-Type: application/json;charset=UTF-8\r\n");
            }
            if (headers != null)
            {
                foreach (var kv in headers)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    if (string.Equals(kv.Key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(kv.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    sb.Append(kv.Key).Append(": ").Append(kv.Value ?? "").Append("\r\n");
                }
            }
            sb.Append("\r\n");
            return sb.ToString();
        }

        private static byte[] ReadAll(IntPtr ssl, int maxBytes)
        {
            using (var ms = new MemoryStream())
            {
                byte[] buf = new byte[8192];
                while (ms.Length < maxBytes)
                {
                    int n = Native.SSL_read(ssl, buf, buf.Length);
                    if (n <= 0)
                    {
                        // n == 0 / 负值都表示结束或出错。已经读到东西就当成正常结束
                        // （对端 Connection: close 时本就是这个表现）。
                        if (ms.Length == 0)
                        {
                            int err = Native.SSL_get_error(ssl, n);
                            if (err != 6 /*SSL_ERROR_ZERO_RETURN*/)
                                throw new IOException("读响应失败 (SSL_get_error=" + err + "): " + Native.ErrText());
                        }
                        break;
                    }
                    ms.Write(buf, 0, n);
                }
                return ms.ToArray();
            }
        }

        /// <summary>把原始字节拆成「状态行 / 响应头 / 响应体」。</summary>
        private static Response ParseResponse(byte[] raw)
        {
            var r = new Response();
            if (raw == null || raw.Length == 0) return r;

            // 头部按字节找 \r\n\r\n，避免把响应体里的二进制当文本处理
            int sep = IndexOf(raw, new byte[] { 13, 10, 13, 10 });
            byte[] headBytes = sep > 0 ? Sub(raw, 0, sep) : raw;
            byte[] bodyBytes = sep > 0 ? Sub(raw, sep + 4, raw.Length - sep - 4) : new byte[0];

            r.RawHeaders = Encoding.UTF8.GetString(headBytes);

            string[] lines = r.RawHeaders.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length > 0)
            {
                // HTTP/1.1 200 OK
                string first = lines[0];
                int sp = first.IndexOf(' ');
                if (sp > 0)
                {
                    int sp2 = first.IndexOf(' ', sp + 1);
                    string code = sp2 > 0 ? first.Substring(sp + 1, sp2 - sp - 1) : first.Substring(sp + 1);
                    int c;
                    if (int.TryParse(code.Trim(), out c)) r.StatusCode = c;
                }
            }

            bool chunked = false;
            for (int i = 1; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (ln.Length == 0) continue;
                int colon = ln.IndexOf(':');
                if (colon <= 0) continue;
                string name = ln.Substring(0, colon).Trim();
                string value = ln.Substring(colon + 1).Trim();

                if (string.Equals(name, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    // 只取 "名字=值"，属性（Path/Domain/…）不要
                    int semi = value.IndexOf(';');
                    string pair = semi >= 0 ? value.Substring(0, semi) : value;
                    int eq = pair.IndexOf('=');
                    if (eq > 0)
                    {
                        r.Cookies.Add(new KeyValuePair<string, string>(
                            pair.Substring(0, eq).Trim(), pair.Substring(eq + 1).Trim()));
                    }
                }
                else if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                         && value.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    chunked = true;
                }
            }

            r.Body = chunked
                ? DecodeChunked(bodyBytes)
                : Encoding.UTF8.GetString(bodyBytes);
            return r;
        }

        private static string DecodeChunked(byte[] b)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < b.Length)
            {
                int eol = IndexOf(b, new byte[] { 13, 10 }, i);
                if (eol < 0) break;
                string sizeLine = Encoding.ASCII.GetString(b, i, eol - i).Trim();
                int semi = sizeLine.IndexOf(';');
                if (semi >= 0) sizeLine = sizeLine.Substring(0, semi);
                int size;
                if (!int.TryParse(sizeLine, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out size) || size <= 0)
                    break;
                int start = eol + 2;
                if (start + size > b.Length) size = b.Length - start;
                sb.Append(Encoding.UTF8.GetString(b, start, size));
                i = start + size + 2;   // 跳过块尾的 CRLF
            }
            return sb.ToString();
        }

        private static int IndexOf(byte[] hay, byte[] needle, int from = 0)
        {
            for (int i = from; i <= hay.Length - needle.Length; i++)
            {
                bool hit = true;
                for (int j = 0; j < needle.Length; j++)
                    if (hay[i + j] != needle[j]) { hit = false; break; }
                if (hit) return i;
            }
            return -1;
        }

        private static byte[] Sub(byte[] src, int start, int len)
        {
            var d = new byte[len];
            Buffer.BlockCopy(src, start, d, 0, len);
            return d;
        }

        // ---------------- P/Invoke ----------------

        private static class Native
        {
            public const int SSL_VERIFY_PEER = 1;
            public const int X509_V_OK = 0;
            public const int SSL_CTRL_SET_TLSEXT_HOSTNAME = 55;
            public const int TLSEXT_NAMETYPE_host_name = 0;
            public const int BIO_NOCLOSE = 0;

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern long OPENSSL_init_ssl(long opts, IntPtr settings);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr TLS_client_method();

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr SSL_CTX_new(IntPtr method);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern void SSL_CTX_free(IntPtr ctx);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern void SSL_CTX_set_verify(IntPtr ctx, int mode, IntPtr callback);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            public static extern int SSL_CTX_load_verify_locations(IntPtr ctx, string caFile, string caPath);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern int SSL_CTX_set_default_verify_paths(IntPtr ctx);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr SSL_new(IntPtr ctx);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern void SSL_free(IntPtr ssl);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern void SSL_set_bio(IntPtr ssl, IntPtr rbio, IntPtr wbio);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern long SSL_ctrl(IntPtr ssl, int cmd, long larg, IntPtr parg);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern int SSL_connect(IntPtr ssl);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            public static extern int SSL_set1_host(IntPtr ssl, string host);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern long SSL_get_verify_result(IntPtr ssl);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr X509_verify_cert_error_string(long verifyResult);

            public static string VerifyErrorText(long verifyResult)
            {
                IntPtr p = X509_verify_cert_error_string(verifyResult);
                return p == IntPtr.Zero ? "(未知证书错误)" : Marshal.PtrToStringAnsi(p);
            }

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern int SSL_write(IntPtr ssl, byte[] buf, int len);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern int SSL_read(IntPtr ssl, byte[] buf, int len);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern int SSL_get_error(IntPtr ssl, int ret);

            [DllImport(CryptoDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr BIO_new_socket(IntPtr sock, int closeFlag);

            [DllImport(CryptoDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr OpenSSL_version(int type);

            [DllImport(CryptoDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern ulong ERR_get_error();

            [DllImport(CryptoDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern void ERR_error_string_n(ulong e, byte[] buf, int len);

            public static string ErrText()
            {
                ulong e = ERR_get_error();
                if (e == 0) return "(无 OpenSSL 错误)";
                byte[] buf = new byte[256];
                ERR_error_string_n(e, buf, buf.Length);
                int n = Array.IndexOf(buf, (byte)0);
                return Encoding.ASCII.GetString(buf, 0, n < 0 ? buf.Length : n);
            }
        }
    }
}
