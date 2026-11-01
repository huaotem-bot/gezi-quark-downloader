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
using System.Text.RegularExpressions;
using GeZi.Core.Api;
using GeZi.Core.Models;
using GeZi.Core.Support;

namespace GeZi.Providers.Shared
{
    /// <summary>
    /// 多网盘分享链接识别器。
    /// <para>
    /// 职责只有一件事：**从一段粘贴进来的文本里判断是哪个网盘、分享 ID 是什么、提取码是什么**。
    /// 不做网络请求，因此可以完整自测（不需要任何账号）。
    /// </para>
    /// <para>
    /// 支持四种输入形态：
    /// ① 纯链接 <c>https://pan.quark.cn/s/abcdef</c>；
    /// ② 带查询参数的链接 <c>...?pwd=1234</c>；
    /// ③ 整段分享文案（"链接：… 提取码：abcd"）；
    /// ④ 带子目录 fid 的链接 <c>.../s/abcdef/0123456789abcdef0123456789abcdef</c>。
    /// </para>
    /// </summary>
    public static class PanUrlParser
    {
        // ---- 各网盘的域名特征 ----
        // 用 host 判断而不是整串包含，避免 "quark.cn.evil.com" 这类伪装被误判。
        private static readonly Regex QuarkHost =
            new Regex(@"^(?:[\w-]+\.)*quark\.cn$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex UcHost =
            new Regex(@"^(?:[\w-]+\.)*uc\.cn$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AliyunHost =
            new Regex(@"^(?:[\w-]+\.)*(?:aliyundrive\.com|alipan\.com)$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex XunleiHost =
            new Regex(@"^(?:[\w-]+\.)*xunlei\.com$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ---- 分享 ID ----
        // 夸克/UC/阿里/迅雷都是 /s/{id}；id 允许字母数字下划线与短横。
        private static readonly Regex ShareId =
            new Regex(@"/s/([A-Za-z0-9_-]{4,64})", RegexOptions.Compiled);

        // 子目录 fid：/s/{id}/{fid}，fid 通常是 32 位十六进制（夸克/UC）
        private static readonly Regex StartFid =
            new Regex(@"/s/[A-Za-z0-9_-]{4,64}/([A-Za-z0-9]{16,64})", RegexOptions.Compiled);

        // ---- 提取码 ----
        // 链接里的 ?pwd=xxxx
        private static readonly Regex PasscodeQuery =
            new Regex(@"[?&](?:pwd|password|passcode|code)=([A-Za-z0-9]{1,16})",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 中文文案里的提取码。
        // ⚠️ 负向后顾不能省：分享 ID 本身可能叫 pwd1234，
        //    不加的话会把 "pwd1234" 里的 "1234" 当提取码（Quark 版踩过这个坑）。
        private static readonly Regex PasscodeCn = new Regex(
            @"(?<![/A-Za-z0-9])(?:提取码|提取碼|密码|密碼|访问码|訪問碼|分享码|口令)\s*[=:：]?\s*([A-Za-z0-9]{1,16})",
            RegexOptions.Compiled);

        /// <summary>
        /// 解析一段文本。识别不出网盘或分享 ID 时返回 null（调用方给"无法识别"提示）。
        /// </summary>
        public static PanShareRef Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string url = ExtractFirstUrl(text);
            if (string.IsNullOrEmpty(url))
                return null;

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                return null;

            PanKind? kind = DetectKind(uri.Host);
            if (kind == null)
                return null;

            var m = ShareId.Match(uri.AbsoluteUri);
            if (!m.Success)
                return null;

            var result = new PanShareRef
            {
                Kind = kind.Value,
                ShareId = m.Groups[1].Value,
                RawUrl = text,
            };

            // 子目录 fid（只有夸克/UC 用得上；其它网盘忽略）
            var fm = StartFid.Match(uri.AbsoluteUri);
            if (fm.Success)
                result.StartFid = fm.Groups[1].Value;

            // 提取码：先看查询参数，再看整段文案
            var pm = PasscodeQuery.Match(uri.Query);
            if (pm.Success)
            {
                result.Passcode = pm.Groups[1].Value;
            }
            else
            {
                var cn = PasscodeCn.Match(text);
                if (cn.Success)
                    result.Passcode = cn.Groups[1].Value;
            }

            return result;
        }

        /// <summary>按域名判断网盘类型；不认识返回 null。</summary>
        public static PanKind? DetectKind(string host)
        {
            if (string.IsNullOrEmpty(host))
                return null;

            if (QuarkHost.IsMatch(host)) return PanKind.Quark;
            if (UcHost.IsMatch(host)) return PanKind.Uc;
            if (AliyunHost.IsMatch(host)) return PanKind.Aliyun;
            if (XunleiHost.IsMatch(host)) return PanKind.Xunlei;
            return null;
        }

        /// <summary>
        /// 从任意文本里抠出第一个 http(s) 链接。
        /// 分享文案常是"【标题】链接：https://… 提取码：abcd"这种混排。
        /// </summary>
        private static string ExtractFirstUrl(string text)
        {
            var m = Regex.Match(text, @"https?://[^\s""'<>，。；、）】]+", RegexOptions.IgnoreCase);
            if (m.Success)
                return m.Value.TrimEnd('.', ',', ';', ')', ']', '}');

            // 没有协议头时，按域名特征补一个（用户常直接粘 "pan.quark.cn/s/xxx"）
            var bare = Regex.Match(text,
                @"(?:[\w-]+\.)*(?:quark\.cn|uc\.cn|aliyundrive\.com|alipan\.com|xunlei\.com)/[^\s""'<>，。；、）】]+",
                RegexOptions.IgnoreCase);
            if (bare.Success)
                return "https://" + bare.Value.TrimEnd('.', ',', ';', ')', ']', '}');

            return null;
        }

        /// <summary>网盘展示名。</summary>
        public static string DisplayNameOf(PanKind kind)
        {
            switch (kind)
            {
                case PanKind.Quark: return "夸克网盘";
                case PanKind.Uc: return "UC网盘";
                case PanKind.Aliyun: return "阿里云盘";
                case PanKind.Xunlei: return "迅雷云盘";
                default: return kind.ToString();
            }
        }
    }
}