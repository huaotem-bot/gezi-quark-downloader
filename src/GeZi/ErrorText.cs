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
using System.Net;
using System.Net.Sockets;

namespace GeZi
{
    /// <summary>
    /// 把技术异常翻译成用户能看懂的中文。
    ///
    /// 为什么需要这一层（2026-10-26 用户要求）：
    /// 原来很多弹窗直接把 <c>ex.Message</c> 贴出去，用户看到的是
    /// 「远程服务器返回错误: (400) 错误的请求。」「已取消一个任务。」
    /// 「无法加载 DLL 'WebView2Loader.dll'。」—— 既不知道发生了什么，也不知道该做什么。
    /// 这里按异常类型/内层 Win32 码归因，只输出「发生了什么 + 下一步怎么办」。
    ///
    /// ⚠️ 绝不吞掉原始信息：调用方仍应把 <c>ex.Message</c> 写进日志（<c>Log</c>/<c>LogDetail</c>），
    /// 本类只负责**界面**那一句。这样排查时日志里仍有原始错误。
    /// </summary>
    internal static class ErrorText
    {
        /// <summary>
        /// 通用异常归因：返回一句中文说明（不含异常原文）。
        /// </summary>
        public static string Describe(Exception ex)
        {
            if (ex == null) return "未知错误";

            // 🚨 .NET Framework 里 TaskCanceledException 不只代表"用户取消"，
            //    HttpClient 超时也抛它 —— 用户看到「已取消一个任务。」完全没法行动。
            if (ex is OperationCanceledException || ex is TimeoutException)
                return "等待响应超时，可能是网络慢或被拦截。请稍后重试";

            var we = ex as WebException ?? FindInner<WebException>(ex);
            if (we != null)
            {
                var resp = we.Response as HttpWebResponse;
                if (resp != null)
                {
                    int code = (int)resp.StatusCode;
                    string http = DescribeHttp(code);
                    if (http != null) return http;
                }
                switch (we.Status)
                {
                    case WebExceptionStatus.NameResolutionFailure:
                    case WebExceptionStatus.ProxyNameResolutionFailure:
                        return "域名解析失败，请检查网络或代理设置";
                    case WebExceptionStatus.ConnectFailure:
                        return "连不上服务器，请检查网络或代理设置";
                    case WebExceptionStatus.Timeout:
                        return "连接超时，请检查网络或代理设置";
                    case WebExceptionStatus.TrustFailure:
                        return "服务器证书校验失败，请检查系统时间或代理是否劫持了连接";
                    case WebExceptionStatus.SecureChannelFailure:
                        return "加密连接握手失败，请检查网络环境";
                }
                return "网络请求失败，请稍后重试";
            }

            var se = ex as SocketException ?? FindInner<SocketException>(ex);
            if (se != null)
            {
                switch (se.SocketErrorCode)
                {
                    case SocketError.HostNotFound:
                        return "域名解析失败，请检查网络或代理设置";
                    case SocketError.ConnectionRefused:
                        return "服务器拒绝连接，请稍后重试";
                    case SocketError.TimedOut:
                        return "连接超时，请检查网络或代理设置";
                    case SocketError.NetworkUnreachable:
                    case SocketError.HostUnreachable:
                        return "网络不通，请检查网络或代理设置";
                }
                return "网络连接失败，请稍后重试";
            }

            var ioe = ex as System.IO.IOException ?? FindInner<System.IO.IOException>(ex);
            if (ioe != null)
                return DescribeIo(ioe);

            var ua = ex as UnauthorizedAccessException ?? FindInner<UnauthorizedAccessException>(ex);
            if (ua != null)
                return "没有权限操作这个文件或目录，请换一个位置或检查文件属性";

            var dll = ex as DllNotFoundException ?? FindInner<DllNotFoundException>(ex);
            if (dll != null)
                return "缺少运行库文件，请重新解压完整安装包后再试";

            var io = ex as InvalidOperationException ?? FindInner<InvalidOperationException>(ex);
            if (io != null && ex.GetType().Name == "InvalidOperationException")
                return "当前状态不允许这个操作，请重试";

            return "操作失败，详细信息已记入日志";
        }

        /// <summary>
        /// HTTP 状态码 → 中文原因 + 该做什么。返回 null 表示没有更具体的说法。
        /// </summary>
        public static string DescribeHttp(int code)
        {
            switch (code)
            {
                case 400:
                    return "服务器拒绝了这次请求（请求参数不被接受）。"
                         + "常见于登录态失效或直链已过期，请重新登录后重试";
                case 401:
                    return "登录已失效，请重新登录";
                case 403:
                    return "服务器拒绝了访问（没有权限或直链已过期），请重新登录后重试";
                case 404:
                    return "文件在服务器上找不到了（可能已被删除或移动）";
                case 405:
                case 406:
                    return "服务器不接受这种请求方式，可能是直链已失效";
                case 408:
                    return "服务器响应超时，请稍后重试";
                case 412:
                    return "直链签名已过期，正在尝试自动换新链";
                case 413:
                    return "文件太大，服务器拒绝了这次请求";
                case 429:
                    return "请求太频繁被服务器限流，请稍等一会儿再试";
                case 500:
                case 502:
                case 503:
                case 504:
                    return "服务器暂时不可用（服务端故障或限流），请稍后重试";
                default:
                    return null;
            }
        }

        /// <summary>
        /// 下载引擎报回来的错误串 → 中文说明。
        /// 引擎给的是 <c>"HTTP 400"</c> / <c>"HTTP 400: Bad Request"</c> 这类技术串，
        /// 直接显示用户看不懂；这里解析出状态码再翻译，识别不了就原样返回。
        /// </summary>
        public static string DescribeDownloadError(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "下载失败，原因未知";

            // 引擎的格式是 "HTTP <code>" 或 "HTTP <code>: <reason>"
            int code = 0;
            if (raw.StartsWith("HTTP ", StringComparison.OrdinalIgnoreCase))
            {
                int i = 5;
                while (i < raw.Length && raw[i] >= '0' && raw[i] <= '9') i++;
                int.TryParse(raw.Substring(5, i - 5), out code);
            }

            if (code > 0)
            {
                string friendly = DescribeHttp(code);
                if (!string.IsNullOrEmpty(friendly))
                    return friendly + "（HTTP " + code + "）";
            }

            // 🚨 不是 HTTP 错误时也要兜一层：引擎可能把 .NET 的英文原始异常串带上来
            // （典型：「未能解析此远程名称: 'dl-pc-sz-cf.pds.uc.cn'」）。
            // 界面上不应该出现这种英文原文。
            string friendlyNet = DescribeNetworkRaw(raw);
            if (!string.IsNullOrEmpty(friendlyNet))
                return friendlyNet;

            // 可能是"磁盘空间不足""哈希不符"这类引擎自己写好的中文，
            // 那种已经是人话了，原样用。
            return raw;
        }

        /// <summary>
        /// 把 .NET 网络层的英文原始异常串翻译成中文。识别不了返回 <c>null</c>。
        /// </summary>
        private static string DescribeNetworkRaw(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            // 代理本身解析失败要排在普通 DNS 前面（两者文案不同）。
            if (Contains(raw, "ProxyNameResolutionFailure"))
                return "代理服务器无法解析，请检查代理设置";

            // 域名解析
            if (Contains(raw, "未能解析此远程名称")
                || Contains(raw, "remote name could not be resolved")
                || Contains(raw, "NameResolutionFailure")
                || Contains(raw, "HostNotFound")
                || Contains(raw, "不知道这样的主机"))
            {
                return "域名解析失败，请检查网络或代理设置";
            }

            // 连不上 / 被拒
            if (Contains(raw, "无法连接到远程服务器")
                || Contains(raw, "Unable to connect to the remote server")
                || Contains(raw, "积极拒绝")
                || Contains(raw, "actively refused")
                || Contains(raw, "ConnectFailure"))
            {
                return "无法连接下载服务器，请检查网络或代理设置";
            }

            // 超时
            if (Contains(raw, "操作超时")
                || Contains(raw, "operation has timed out")
                || Contains(raw, "请求超时")
                || Contains(raw, "没有正确答复")
                || Contains(raw, "did not properly respond"))
            {
                return "连接下载服务器超时，请稍后重试";
            }

            // TLS / 证书
            if (Contains(raw, "基础连接已经关闭")
                || Contains(raw, "underlying connection was closed")
                || Contains(raw, "SecureChannelFailure")
                || Contains(raw, "TrustFailure"))
            {
                return "与下载服务器的加密连接被中断，请检查网络或代理设置";
            }

            // 被中止 / 取消
            if (Contains(raw, "请求被中止")
                || Contains(raw, "request was aborted"))
            {
                return "下载已中断";
            }

            return null;
        }

        private static bool Contains(string raw, string needle)
        {
            return raw.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string DescribeIo(System.IO.IOException ioe)
        {
            // 磁盘满 / 路径非法 / 文件被占用 是下载器最常见三种 I/O 失败
            string m = ioe.Message ?? "";
            if (m.IndexOf("磁盘空间不足", StringComparison.Ordinal) >= 0
                || m.IndexOf("disk space", StringComparison.OrdinalIgnoreCase) >= 0)
                return "磁盘空间不足，请清理后再试";
            if (m.IndexOf("正由另一进程使用", StringComparison.Ordinal) >= 0
                || m.IndexOf("being used by another process", StringComparison.OrdinalIgnoreCase) >= 0)
                return "文件正被其他程序占用，请关闭占用它的程序后重试";
            if (m.IndexOf("文件名", StringComparison.Ordinal) >= 0
                || m.IndexOf("路径", StringComparison.Ordinal) >= 0)
                return "文件路径不合法或太长，请换一个更短的下载目录";
            return "读写文件失败，请检查下载目录是否可写、磁盘是否已满";
        }

        /// <summary>沿 InnerException 链找指定类型（异常的真正原因常被包一层）。</summary>
        private static T FindInner<T>(Exception ex) where T : Exception
        {
            var cur = ex;
            int guard = 0;
            while (cur != null && guard++ < 8)
            {
                var t = cur as T;
                if (t != null) return t;
                cur = cur.InnerException;
            }
            return null;
        }
    }
}
