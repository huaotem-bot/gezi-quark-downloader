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
using System.Text;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 从"用户手上能拿到的东西"里解析出可用的 Cookie 串。
    /// 用户反馈：「为什么没有导入 cookie 文件进行登录的功能」——
    /// 原来只能把 Cookie 文本粘进输入框，但浏览器扩展导出的是**文件**，
    /// 手动打开复制再粘贴很别扭。这里负责把文件内容变成能用的 Cookie 串。
    /// 支持的来源（按识别顺序）：
    ///   1) JSON 数组：<c>[{"name":"a","value":"1","domain":".quark.cn"}, ...]</c>
    ///      —— Cookie-Editor / EditThisCookie 等扩展的导出格式
    ///   2) JSON 对象：<c>{"a":"1","b":"2"}</c>（name → value 映射）
    ///   3) JSON 对象带单个 cookie 字段：<c>{"cookie":"a=1; b=2"}</c>
    ///   4) 原始 Cookie 串：<c>a=1; b=2</c>（允许带 "Cookie: " 前缀、允许跨行）
    /// 输出统一成 <c>a=1; b=2</c> —— 就是 <c>Headers["Cookie"]</c> 要的样子。
    /// </summary>
    public static class CookieText
    {
        /// <summary>把文件/粘贴板内容解析成 Cookie 串。无法解析时返回空串。</summary>
        public static string Parse(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "";

            string s = raw.Trim().TrimStart('\uFEFF').Trim();
            if (s.Length == 0)
                return "";

            // JSON 形态：交给 JSON 分支
            if (s[0] == '[' || s[0] == '{')
            {
                string fromJson = TryParseJson(s);
                if (!string.IsNullOrEmpty(fromJson))
                    return fromJson;
                // JSON 解析失败就往下当纯文本再试一次（容错）
            }

            return NormalizeRaw(s);
        }

        /// <summary>把原始 Cookie 串规整成 "k=v; k2=v2"。</summary>
        private static string NormalizeRaw(string s)
        {
            // 去掉可能的 "Cookie: " / "cookie:" 前缀
            string t = s.Trim();
            if (t.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
                t = t.Substring("Cookie:".Length);

            // 跨行的直接拼成一行 —— 按 ';' 切时换行会残留在键值里
            t = t.Replace("\r", " ").Replace("\n", " ");

            return Join(ParsePairs(t));
        }

        /// <summary>
        /// 解析 "a=1; b=2" 形式的键值对。
        /// 自带实现（不依赖 <c>QuarkSession</c>），这样本类零外部依赖、可单独测试。
        /// </summary>
        private static Dictionary<string, string> ParsePairs(string s)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(s))
                return d;
            foreach (var part in s.Split(';'))
            {
                var p = part.Trim();
                if (p.Length == 0) continue;
                int eq = p.IndexOf('=');
                if (eq <= 0) continue;                 // 没有 '=' 或 '=' 在最前 → 不是有效键值对
                var k = p.Substring(0, eq).Trim();
                var v = p.Substring(eq + 1).Trim();
                if (k.Length > 0) d[k] = v;
            }
            return d;
        }

        private static string TryParseJson(string json)
        {
            object parsed;
            try
            {
                int i = 0;
                parsed = ParseValue(json, ref i);
            }
            catch
            {
                return "";
            }

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // ① 数组：每个元素形如 {name, value, domain}
            var arr = parsed as List<object>;
            if (arr != null)
            {
                foreach (var it in arr)
                {
                    var o = it as Dictionary<string, object>;
                    if (o == null) continue;
                    string name = Str(o, "name", "Name", "key", "Key");
                    string value = Str(o, "value", "Value");
                    if (name.Length == 0 || value.Length == 0) continue;

                    // ⚠️ 必须按域过滤：用户可能导出**整个浏览器**的 Cookie，
                    //    几百条无关 Cookie 塞进请求头会撑爆 Header 长度限制（服务端 400）。
                    string domain = Str(o, "domain", "Domain");
                    if (domain.Length > 0 &&
                        domain.IndexOf("quark", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    dict[name] = value;
                }
                return Join(dict);
            }

            // ② 对象
            var obj = parsed as Dictionary<string, object>;
            if (obj != null)
            {
                // ②a 单个 cookie 对象：{name, value}
                string n1 = Str(obj, "name", "Name");
                string v1 = Str(obj, "value", "Value");
                if (n1.Length > 0 && v1.Length > 0)
                {
                    dict[n1] = v1;
                    return Join(dict);
                }

                // ②b 里面直接放了整串 cookie：{cookie: "a=1; b=2"}
                foreach (var key in new[] { "cookie", "cookieString", "cookie_string", "raw" })
                {
                    if (obj.TryGetValue(key, out var cv))
                    {
                        string cs = cv as string;
                        if (!string.IsNullOrEmpty(cs))
                            return NormalizeRaw(cs);
                    }
                }

                // ②c 退化成 name → value 映射
                foreach (var kv in obj)
                {
                    string v = kv.Value as string;
                    if (v == null) continue;
                    if (kv.Key.Length == 0) continue;
                    dict[kv.Key] = v;
                }
                return Join(dict);
            }

            return "";
        }

        // ────────────────────────────────────────────────────────────
        // 极简 JSON 解析（只覆盖 cookie 导出会用到的子集：对象 / 数组 / 字符串 / 字面量）
        // 为什么不用 JavaScriptSerializer：它需要 System.Web.Extensions，
        // 而给 SDK 风格的 WPF 工程加这个引用会让 XAML 编译器（MarkupCompilePass）报
        // "Could not find assembly 'System.Web'"，构建直接挂掉。
        // 为几十行解析逻辑引入这种构建耦合不值得，所以自带一个零依赖的。
        // ────────────────────────────────────────────────────────────

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            // 数字 / true / false / null：cookie 场景用不到具体值，原样取 token 即可
            int start = i;
            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']'
                   && !char.IsWhiteSpace(s[i]))
                i++;
            return s.Substring(start, i - start);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            i++;                                  // 跳过 '{'
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == '}') { i++; break; }
                if (s[i] == ',') { i++; continue; }
                if (s[i] != '"') { i++; continue; }   // 容错：跳过意外字符

                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                object val = ParseValue(s, ref i);
                d[key] = val;
            }
            return d;
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++;                                  // 跳过 '['
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == ']') { i++; break; }
                if (s[i] == ',') { i++; continue; }
                list.Add(ParseValue(s, ref i));
            }
            return list;
        }

        private static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;                                  // 跳过起始引号
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 <= s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i, 4),
                                    System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out code))
                                sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;   // \" \\ \/ 等
                }
            }
            return sb.ToString();
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static string Str(Dictionary<string, object> o, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (o.TryGetValue(k, out var v))
                {
                    var s = v as string;
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            return "";
        }

        private static string Join(Dictionary<string, string> dict)
        {
            if (dict == null || dict.Count == 0)
                return "";
            var sb = new StringBuilder();
            foreach (var kv in dict)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }
    }
}
