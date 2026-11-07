// GeZi —— 多网盘下载器 (Multi-cloud downloader)
// Copyright (C) 2026  Yi Yuan
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

namespace GeZi.Core.Support
{
    /// <summary>
    /// 运行日志脱敏 —— 把凭证类字符串从日志里抹掉再落盘。
    ///
    /// <para>
    /// 背景：`GeZi.log` 是用户会主动发给别人的东西（报错求助、提 issue）。
    /// 但它里面会混进完整的下载直链，而直链的查询串带签名：
    /// 阿里 OSS 是 `Signature=...`、夸克/UC 是 `sign=...&st=...`、迅雷是 `auth_key=...`。
    /// 拿到这些串的人可以在有效期内直接下别人的文件。
    /// </para>
    ///
    /// <para>
    /// 🚨 硬约束（都是踩过的坑，改之前先读）：
    /// <list type="bullet">
    /// <item>**必须是纯函数** —— 无 I/O、无静态可变状态。自检要能直接断言边界。</item>
    /// <item>**绝不能吞掉整条日志** —— 只替换敏感片段，其余原样保留。
    ///   「脱敏后日志什么信息都没有」比不脱敏更糟。</item>
    /// <item>**不能误伤正常文本** —— 例如 `sign` 必须是 `sign=` 才算签名参数，
    ///   否则「设计 / signature / 签收」这类词会被打码；同理 `token` 也不能裸匹配。</item>
    /// <item>**保留前 4 位**（可辨识是不是同一个令牌），其余打码成 `***`。</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class LogRedactor
    {
        /// <summary>打码后的占位符。</summary>
        public const string Mask = "***";

        /// <summary>短于这个长度的值不打码（本来就不是凭证，打码反而丢信息）。</summary>
        private const int MinSecretLength = 8;

        // ---- 查询串里的签名类参数：name=value（名字后面必须紧跟 = ） ----
        //
        // ⚠️ 用 @"(?<name>...)=..." 而不是裸词匹配：
        //    "signature"、"design"、"signed" 里都含 sign，裸匹配会把它们打成 ***。
        //    这里要求「前面不是字母数字」+「参数名后紧跟 = 」才算。
        private static readonly Regex QuerySecret = new Regex(
            @"(?<![A-Za-z0-9])" +
            @"(?<name>Signature|sign|st|auth_key|authkey|token|access_token|refresh_token|" +
            @"pan_token|__puus|__pus|ckey|expires_key|e)[=:](?<value>[^&\s""'<>]{4,})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ---- 请求头 / Cookie 串：Key: value 或 Key=value ----
        private static readonly Regex HeaderSecret = new Regex(
            @"(?<![A-Za-z0-9])(?<name>Authorization|Cookie|Set-Cookie|Bearer)\s*[:=]\s*(?<value>[^\r\n，。]{8,})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ---- 明显是令牌的独立串（很长且像 base64/jwt） ----
        //
        // jwt 三段式 eyJ...；或 32 位以上十六进制/字母数字混合。
        // ⚠️ 只打码「不像正常句子」的串：要求整串无空格且长度够。
        private static readonly Regex JwtSecret = new Regex(
            @"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{4,}",
            RegexOptions.Compiled);

        /// <summary>
        /// 脱敏一条日志。null / 空串原样返回。
        /// <para>幂等：对已经脱敏过的文本再调一次，结果不变。</para>
        /// </summary>
        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string s = text;

            s = QuerySecret.Replace(s, m =>
            {
                string v = m.Groups["value"].Value;
                return m.Groups["name"].Value + "=" + MaskValue(v);
            });

            s = HeaderSecret.Replace(s, m =>
            {
                return m.Groups["name"].Value + "=" + Mask;
            });

            s = JwtSecret.Replace(s, Mask);

            return s;
        }

        /// <summary>
        /// 保留前 4 位便于分辨是同一个令牌，其余打码。
        /// 太短的值（&lt; 8 位）不是凭证，整个打码更安全。
        /// </summary>
        private static string MaskValue(string v)
        {
            if (string.IsNullOrEmpty(v)) return v;

            // 已经打过码的**一律原样返回**，保证幂等。
            // ⚠️ 为什么不能只看 StartsWith("***")：打好码之后形如 `abcd***`，
            //    第二次进来它会先被 MaskValue 当成新值（长度 7 < 8）再打一次 → 前 4 位丢了。
            if (v.IndexOf(Mask, StringComparison.Ordinal) >= 0) return v;

            if (v.Length < MinSecretLength) return Mask;
            return v.Substring(0, 4) + Mask;
        }
    }
}
