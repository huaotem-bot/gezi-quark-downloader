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

namespace GeZi.Core.Support
{
    /// <summary>
    /// 出站连接的网络开关（进程级，静态）。
    /// 程序默认**强制直连** —— <see cref="QuarkSession"/> 与
    /// <c>SegmentedDownloader</c> 构造 <see cref="System.Net.Http.HttpClientHandler"/> 时
    /// 显式写了 <c>Proxy = null, UseProxy = false</c>。这么做的理由见各处的注释
    /// （本机代理软件常把夸克接口劫持到 127.0.0.1；代理会改变 TLS ClientHello 指纹，
    /// 而 pan.quark.cn 的 WAF 对指纹敏感）。
    /// 但**强制直连**对一部分用户是致命的：公司网络 / 只能经代理出口的环境里，
    /// 直连根本出不去，程序完全不可用。所以这里提供一个**显式开关**，
    /// 由设置页控制（<c>AppSettings.UseSystemProxy</c>）。
    /// ⚠️ 默认 **false = 直连**，与旧版本行为完全一致（零回归）。
    /// ⚠️ 开关只影响**此后新建**的 HttpClientHandler；已发起的请求不受影响，
    ///    所以改完设置后要新建任务 / 重新登录才会走新路径。
    /// </summary>
    public static class NetworkOptions
    {
        private static volatile bool _useSystemProxy;

        /// <summary>
        /// 是否跟随 Windows 系统代理（WinINET 设置，含 PAC 脚本）。
        /// 由 UI 层在启动与设置变更时写入。
        /// </summary>
        public static bool UseSystemProxy
        {
            get { return _useSystemProxy; }
            set { _useSystemProxy = value; }
        }

        /// <summary>
        /// 显式直连用的代理对象：<see cref="IWebProxy.IsBypassed"/> 恒 <c>true</c>，等价于"不走代理"。
        ///
        /// <para>
        /// 为什么不直接给 <see cref="System.Net.HttpWebRequest.Proxy"/> 赋 <c>null</c>：
        /// 该属性的文档明确写着赋 <c>null</c> 会抛 <see cref="ArgumentNullException"/>；
        /// 官方推荐的直连写法是 <c>GlobalProxySelection.GetEmptyWebProxy()</c>，
        /// 但那个 API 已被标记过时（会产生编译警告，项目要求 0 警告）。
        /// 这里用一个等价的最小实现。
        /// </para>
        /// </summary>
        private sealed class DirectProxy : IWebProxy
        {
            public static readonly DirectProxy Instance = new DirectProxy();

            public ICredentials Credentials { get; set; }

            public Uri GetProxy(Uri destination)
            {
                return destination;
            }

            public bool IsBypassed(Uri host)
            {
                return true;
            }
        }

        /// <summary>
        /// 按当前开关解析出应使用的代理对象（直连时返回 <see cref="DirectProxy"/>）。
        /// </summary>
        private static IWebProxy ResolveProxy()
        {
            if (!_useSystemProxy)
                return DirectProxy.Instance;

            try
            {
                var proxy = WebRequest.GetSystemWebProxy();
                if (proxy == null)
                    return DirectProxy.Instance;

                // 系统代理可能要身份验证（企业环境常见）。DefaultCredentials 会在
                // 需要时带上当前 Windows 凭据，不需要时也不会有副作用。
                try { proxy.Credentials = CredentialCache.DefaultCredentials; }
                catch { /* 某些代理实现不允许设置，忽略即可 */ }

                return proxy;
            }
            catch
            {
                // 取系统代理失败 → 退回直连，绝不让设置项把程序变成不可用。
                return DirectProxy.Instance;
            }
        }

        /// <summary>
        /// 按当前开关给 <see cref="System.Net.HttpWebRequest"/> 配置代理。
        /// 语义与 <see cref="ApplyTo(System.Net.Http.HttpClientHandler)"/> 完全一致。
        ///
        /// <para>
        /// 🚨 为什么必须调：<c>HttpWebRequest</c> 在 <c>Proxy</c> 属性未设置时，
        /// 会**继承 Windows「Internet 选项」里的系统代理**（微软官方文档原文）。
        /// 本机的代理软件常把 CDN 请求劫持/限速，还会改变 TLS ClientHello 指纹
        /// 触发夸克/UC 的 WAF —— 表现就是"域名能解析但下不动"。
        /// </para>
        /// </summary>
        public static void ApplyTo(HttpWebRequest request)
        {
            if (request == null)
                return;

            try
            {
                request.Proxy = ResolveProxy();
            }
            catch
            {
                // 绝不让代理配置把下载变成不可用。
                try { request.Proxy = DirectProxy.Instance; } catch { }
            }
        }

        /// <summary>
        /// 按当前开关给 handler 配置代理。**所有**构造 HttpClientHandler 的地方都应调它，
        /// 保证 API 层与下载层行为一致（否则会出现"能解析但下不动"这种半通状态）。
        /// </summary>
        public static void ApplyTo(System.Net.Http.HttpClientHandler handler)
        {
            if (handler == null)
                return;

            if (!_useSystemProxy)
            {
                // 保持既有行为：不读系统代理。
                handler.Proxy = null;
                handler.UseProxy = false;
                return;
            }

            try
            {
                var proxy = WebRequest.GetSystemWebProxy();
                if (proxy == null)
                {
                    handler.Proxy = null;
                    handler.UseProxy = false;
                    return;
                }

                // 系统代理可能要身份验证（企业环境常见）。DefaultCredentials 会在
                // 需要时带上当前 Windows 凭据，不需要时也不会有副作用。
                try { proxy.Credentials = CredentialCache.DefaultCredentials; }
                catch { /* 某些代理实现不允许设置，忽略即可 */ }

                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
            catch
            {
                // 取系统代理失败 → 退回直连，绝不让设置项把程序变成不可用。
                handler.Proxy = null;
                handler.UseProxy = false;
            }
        }
    }
}
