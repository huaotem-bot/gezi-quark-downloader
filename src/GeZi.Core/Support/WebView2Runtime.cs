// GeZi —— 多网盘下载器 (Multi-cloud downloader)
// Copyright (C) 2026  Yi Yuan
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
// along with this program.  If not, see <https://www.gnu.org/licenses/agpl-3.0>.

using System;
using System.Globalization;
using Microsoft.Win32;

namespace GeZi.Core.Support
{
    /// <summary>
    /// WebView2 运行时（Evergreen）检测。
    /// <para>
    /// <b>为什么需要它：</b>内嵌登录依赖 WebView2 运行时。Windows 11 系统预装，
    /// Windows 10 绝大多数已有（Edge 会顺带安装），但确实有少数机器没有
    /// （企业/教育环境脱离 Windows Update、LTSC、精简系统等）。
    /// </para>
    /// <para>
    /// <b>设计原则：缺失绝不阻塞程序。</b>
    /// 检测失败只意味着"内嵌登录"这个便捷入口不可用，
    /// 手动粘贴 Cookie 的通道始终可用 —— 所以这里只返回事实，不抛异常、不弹窗。
    /// </para>
    /// <para>
    /// 检测方式采用微软官方文档推荐的两处注册表 <c>pv</c> 值
    /// （HKLM 用于 per-machine 安装，HKCU 用于 per-user 安装）；
    /// 任一存在且版本 &gt; 0.0.0.0 即视为已安装。
    /// </para>
    /// </summary>
    public static class WebView2Runtime
    {
        /// <summary>WebView2 Runtime 的固定产品 GUID（微软文档给定，不要改）。</summary>
        private const string ClientGuid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

        /// <summary>官方在线安装器（Bootstrapper，约 2 MB）。</summary>
        public const string BootstrapperUrl =
            "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        /// <summary>面向普通用户的下载页（安装器失效时给这个）。</summary>
        public const string ConsumerDownloadUrl =
            "https://developer.microsoft.com/microsoft-edge/webview2/consumer/";

        /// <summary>
        /// 是否已安装可用的 WebView2 运行时。
        /// </summary>
        public static bool IsInstalled
        {
            get
            {
                string v = GetVersion();
                return !string.IsNullOrEmpty(v) && v != "0.0.0.0";
            }
        }

        /// <summary>
        /// 取运行时版本号；未安装返回空串。
        /// 解析失败也返回空串（宁可当作没装，也不让检测本身抛异常）。
        /// </summary>
        public static string GetVersion()
        {
            // 64 位系统上 per-machine 安装写在 WOW6432Node 下
            string[] paths =
            {
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + ClientGuid,
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + ClientGuid,
            };

            RegistryHive[] hives = { RegistryHive.LocalMachine, RegistryHive.CurrentUser };

            foreach (var hive in hives)
            {
                foreach (var sub in paths)
                {
                    string v = TryReadPv(hive, sub);
                    if (!string.IsNullOrEmpty(v) && v != "0.0.0.0")
                        return v;
                }
            }
            return "";
        }

        private static string TryReadPv(RegistryHive hive, string subKey)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
                using (var key = baseKey.OpenSubKey(subKey, writable: false))
                {
                    if (key == null)
                        return null;
                    var val = key.GetValue("pv") as string;
                    return val == null ? null : val.Trim();
                }
            }
            catch
            {
                // 注册表被策略限制、权限不足等：当作读不到
                return null;
            }
        }

        /// <summary>
        /// 给用户看的一句话状态说明（设置页直接展示）。
        /// </summary>
        public static string DescribeStatus()
        {
            string v = GetVersion();
            if (string.IsNullOrEmpty(v) || v == "0.0.0.0")
            {
                return "未检测到 WebView2 运行时 —— 内嵌登录不可用，"
                     + "但仍可手动粘贴 Cookie（下方输入框）。";
            }
            return "WebView2 运行时已就绪（" + v + "），可使用内嵌登录。";
        }

        /// <summary>
        /// 版本字符串是否可解析为有效版本（自测用；也用于判断异常返回值）。
        /// </summary>
        public static bool IsValidVersionString(string v)
        {
            if (string.IsNullOrWhiteSpace(v))
                return false;
            Version parsed;
            return Version.TryParse(v.Trim(), out parsed);
        }

        /// <summary>把版本串规范化成可比较的 Version；失败返回 null。</summary>
        public static Version ParseVersion(string v)
        {
            if (string.IsNullOrWhiteSpace(v))
                return null;
            Version parsed;
            if (!Version.TryParse(v.Trim(), out parsed))
                return null;
            return parsed;
        }

        /// <summary>格式化版本用于展示（解析失败时原样返回）。</summary>
        public static string FormatVersion(string v)
        {
            var parsed = ParseVersion(v);
            if (parsed == null)
                return string.IsNullOrWhiteSpace(v) ? "(未知)" : v.Trim();
            return parsed.ToString();
        }

        /// <summary>
        /// 生成一份"临时、隔离"的 WebView2 用户数据目录路径。
        /// <para>
        /// 用独立目录而不是默认位置，原因：不让网盘登录态写进用户自己的浏览器配置，
        /// 也避免与本机 Edge 的会话互相干扰（同类项目 wenxi 在 Windows 上同样用隔离配置）。
        /// </para>
        /// <para>
        /// <b>三级回退（实测必需）：</b>
        /// 优先 <c>%LOCALAPPDATA%\GeZi\webview2</c>；该路径被 ACL/安全软件拒绝时
        /// （实测会抛 UnauthorizedAccessException）退到 <c>%TEMP%</c>；
        /// 再不行退到程序目录下的 <c>webview2-data</c>。全失败才返回 null。
        /// </para>
        /// <para>
        /// <b>为什么要能固定：</b>登录窗口如果每次都换新目录，用户每打开一次就得重登一次，
        /// 而且阿里这类"登录态只活在 localStorage"的站点，窗口一关会话就彻底没了。
        /// 传 <c>profileKey</c> 让同一家网盘共用一份配置，登录一次即可复用。
        /// </para>
        /// </summary>
        /// <param name="profileKey">
        /// 固定 profile 名（如 "aliyun"）。给定时使用 <c>profile-{key}</c> 这个**固定目录**，
        /// 登录态可跨窗口复用；不给则每次生成随机目录（自检用）。
        /// </param>
        public static string CreateIsolatedDataFolder(string profileKey = null)
        {
            string suffix;
            if (string.IsNullOrWhiteSpace(profileKey))
            {
                suffix = "profile-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            }
            else
            {
                // 只保留字母数字：profileKey 来自枚举名，这里再收一次，
                // 防止有人传进来带路径分隔符把目录建到别处。
                var sb = new System.Text.StringBuilder();
                foreach (char c in profileKey)
                {
                    if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                        sb.Append(char.ToLowerInvariant(c));
                }
                suffix = "profile-" + (sb.Length == 0 ? "default" : sb.ToString());
            }

            var candidates = new System.Collections.Generic.List<string>();

            try
            {
                candidates.Add(System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GeZi", "webview2", suffix));
            }
            catch { }

            try
            {
                candidates.Add(System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "GeZi-webview2", suffix));
            }
            catch { }

            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrEmpty(baseDir))
                    candidates.Add(System.IO.Path.Combine(baseDir, "webview2-data", suffix));
            }
            catch { }

            foreach (var dir in candidates)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(dir);
                    // 建得出来不代表写得进去（有些 ACL 只给"创建"不给"写入"），实测一次
                    string probe = System.IO.Path.Combine(dir, ".probe");
                    System.IO.File.WriteAllText(probe, "ok");
                    System.IO.File.Delete(probe);
                    return dir;
                }
                catch
                {
                    // 换下一个候选
                }
            }
            return null;      // 全失败：调用方回退到 WebView2 默认目录
        }

        /// <summary>清理隔离数据目录（登录窗口关闭后调用，避免残留会话文件）。</summary>
        public static void TryCleanupFolder(string dir)
        {
            if (string.IsNullOrEmpty(dir))
                return;
            try
            {
                if (System.IO.Directory.Exists(dir))
                    System.IO.Directory.Delete(dir, true);
            }
            catch { }
        }
    }
}