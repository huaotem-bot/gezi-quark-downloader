// GeZi —— 多网盘下载器 (Multi-cloud downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 四家网盘的「客户端身份档案」——取链与下载必须共用同一份。
    ///
    /// <para>
    /// 🚨🚨 为什么需要它：服务端判定「你是不是官方客户端」看的是一整组特征
    /// （UA / Channel / 版本号 / 查询参数 sys、ve / Sec-Ch-Ua / Sec-Fetch-* / Origin / Referer），
    /// 不是只看 UA 字符串。原来这些值散在 QuarkSession（取链层）、
    /// FusionDownloader.BuildOptions（下载层）、各家 Constants 里各写一份 ——
    /// 结果就是**取链时是完整浏览器客户端、下载时只剩 UA/Referer/Origin/Cookie 四个头**，
    /// 身份自相矛盾。UC 对非会员账号的视频下发「请使用 UC 浏览器播放」引导流，
    /// 与这个缺口高度相关。
    /// </para>
    ///
    /// <para>
    /// 取值交叉对照了 refs/LinkSwift（config 里的 ua.main / secCh / getLink 查询串）
    /// 与 refs/YunX/Agent.md（GUEST_UA + Sec-Ch-Ua + Referer/Origin）两个独立项目。
    /// </para>
    ///
    /// <para>
    /// 放在 Core 是为了让 GeZi.SelfTest 能直接断言「取链头与下载头一致」，
    /// 且 DownloadEngine 不需要反向引用 Providers。
    /// </para>
    /// </summary>
    public static class PanClientProfile
    {
        /// <summary>一条身份档案。</summary>
        public sealed class Profile
        {
            /// <summary>网盘。</summary>
            public Api.PanKind Kind { get; set; }

            /// <summary>API 调用 UA（取链、列目录）。</summary>
            public string ApiUserAgent { get; set; }

            /// <summary>下载直链 UA。必须与 ApiUserAgent 同族同版本。</summary>
            public string DownloadUserAgent { get; set; }

            /// <summary>Referer。</summary>
            public string Referer { get; set; }

            /// <summary>Origin。</summary>
            public string Origin { get; set; }

            /// <summary>取链 URL 的查询参数（pr / fr / sys / ve 等）。</summary>
            public IDictionary<string, string> Query { get; set; }

            /// <summary>API 与下载都要带的公共头。</summary>
            public IDictionary<string, string> CommonHeaders { get; set; }

            /// <summary>明确禁止下发的头（例如迅雷加 Sec-Ch-Ua 会被判移动端）。</summary>
            public ISet<string> ForbiddenHeaders { get; set; }

            /// <summary>是否走 OpenSSL 桥（TLS ClientHello 指纹）。</summary>
            public bool UseOpenSsl { get; set; }
        }

        private static readonly Dictionary<Api.PanKind, Profile> _map = Build();

        /// <summary>按网盘取档案；未知值回落到夸克。</summary>
        public static Profile For(Api.PanKind kind)
        {
            Profile p;
            return _map.TryGetValue(kind, out p) ? p : _map[Api.PanKind.Quark];
        }

        /// <summary>按整数取档案（续传记录里存的是 int）。</summary>
        public static Profile For(int kind)
        {
            return For((Api.PanKind)kind);
        }

        private static IDictionary<string, string> Dict(params string[] kv)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < kv.Length; i += 2)
                d[kv[i]] = kv[i + 1];
            return d;
        }

        private static ISet<string> Set(params string[] names)
        {
            return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<Api.PanKind, Profile> Build()
        {
            var map = new Dictionary<Api.PanKind, Profile>();

            // ---------------- 夸克 ----------------
            // 对齐 refs/LinkSwift/config/quark.json + （改）网盘直链下载助手.user.js:414/416
            const string quarkUa =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) quark-cloud-drive/3.14.2 Chrome/112.0.5615.165 " +
                "Electron/24.1.3.8 Safari/537.36 Channel/pckk_other_ch";
            map[Api.PanKind.Quark] = new Profile
            {
                Kind = Api.PanKind.Quark,
                ApiUserAgent = quarkUa,
                DownloadUserAgent = quarkUa,
                Referer = "https://pan.quark.cn/",
                Origin = "https://pan.quark.cn",
                Query = Dict("pr", "ucpro", "fr", "pc", "sys", "win32", "ve", "3.14.2"),
                CommonHeaders = Common("empty", "cors", "same-site", null),
                ForbiddenHeaders = Set(),
                UseOpenSsl = false,
            };

            // ---------------- UC ----------------
            // 对齐 refs/LinkSwift/user.js:431~434 与 refs/YunX/Agent.md:440
            // ⚠️ Channel 必须是 ucpan_other_ch（原来误用夸克的 pckk_other_ch）；
            //    Sec-Ch-Ua 必须与 UA 里的 Chromium/100 对齐。
            const string ucUa =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) uc-cloud-drive/2.5.20 Chrome/100.0.4896.160 " +
                "Electron/18.3.5.4-b478491100 Safari/537.36 Channel/ucpan_other_ch";
            const string ucSecChUa =
                "\"Not=A?Brand\";v=\"99\", \"Chromium\";v=\"100\", \"Google Chrome\";v=\"100\"";
            map[Api.PanKind.Uc] = new Profile
            {
                Kind = Api.PanKind.Uc,
                ApiUserAgent = ucUa,
                DownloadUserAgent = ucUa,
                Referer = "https://drive.uc.cn/",
                Origin = "https://drive.uc.cn",
                Query = Dict("pr", "UCBrowser", "fr", "pc", "sys", "win32", "ve", "1.8.8"),
                CommonHeaders = Common("empty", "cors", "same-site", ucSecChUa),
                ForbiddenHeaders = Set(),
                UseOpenSsl = true,
            };

            // ---------------- 阿里云盘 ----------------
            // 身份是「网页版 adrive」，不是客户端。OSS 直链签名把 Referer 算进去了，
            // 必须是 https://www.alipan.com/（不能改回 aliyundrive.com）。
            const string aliUa =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
            map[Api.PanKind.Aliyun] = new Profile
            {
                Kind = Api.PanKind.Aliyun,
                ApiUserAgent = aliUa,
                DownloadUserAgent = aliUa,
                Referer = "https://www.alipan.com/",
                Origin = "https://www.alipan.com",
                Query = Dict(),
                CommonHeaders = Common("empty", "cors", "same-site", null),
                ForbiddenHeaders = Set(),
                UseOpenSsl = false,
            };

            // ---------------- 迅雷云盘 ----------------
            // 🚨 禁止 Sec-Ch-Ua：XunleiLogin.cs:372 实测「加了会被判成移动端，
            //    进而拒绝网页令牌的刷新」。这是反向约束，别顺手补上。
            const string xlUa =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
            map[Api.PanKind.Xunlei] = new Profile
            {
                Kind = Api.PanKind.Xunlei,
                ApiUserAgent = xlUa,
                DownloadUserAgent = xlUa,
                Referer = "https://pan.xunlei.com/",
                Origin = "https://pan.xunlei.com",
                Query = Dict(),
                CommonHeaders = Common("empty", "cors", "same-site", null),
                ForbiddenHeaders = Set("Sec-Ch-Ua", "Sec-Ch-Ua-Mobile", "Sec-Ch-Ua-Platform"),
                UseOpenSsl = false,
            };

            return map;
        }

        private static IDictionary<string, string> Common(
            string dest, string mode, string site, string secChUa)
        {
            var d = Dict(
                "Accept", "application/json, text/plain, */*",
                "Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8",
                "Cache-Control", "no-cache",
                "Pragma", "no-cache",
                "Sec-Fetch-Dest", dest,
                "Sec-Fetch-Mode", mode,
                "Sec-Fetch-Site", site);
            if (!string.IsNullOrEmpty(secChUa))
                d["Sec-Ch-Ua"] = secChUa;
            return d;
        }

        /// <summary>
        /// 把档案里的公共头合并进目标字典，并剔除该网盘的禁止项。
        /// 取链层与下载层都调它，保证两边完全一致。
        /// </summary>
        public static void ApplyHeaders(Api.PanKind kind, IDictionary<string, string> target)
        {
            if (target == null) return;
            Profile p = For(kind);
            foreach (var kv in p.CommonHeaders)
            {
                if (p.ForbiddenHeaders.Contains(kv.Key)) continue;
                // 不覆盖调用方显式设过的值（例如导航型 Accept、Connection: close）。
                if (target.ContainsKey(kv.Key)) continue;
                target[kv.Key] = kv.Value;
            }
        }

        /// <summary>取链 URL 的查询参数片段（不含前导 ?，已按 pr/fr/sys/ve 排序）。</summary>
        public static string QueryString(Api.PanKind kind)
        {
            Profile p = For(kind);
            var parts = new List<string>();
            foreach (var kv in p.Query)
                parts.Add(Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value));
            return string.Join("&", parts.ToArray());
        }
    }
}
