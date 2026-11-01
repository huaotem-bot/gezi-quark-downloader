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
using GeZi.Core.Api;
using GeZi.Core.Models;
using GeZi.Core.Support;

namespace GeZi.Services
{
    /// <summary>
    /// 直链导出：把已取到的直链生成可直接粘贴到外部下载器的文本。
    /// 参考同类开源项目 QuarkDownloader（油猴脚本）的导出实现：
    /// 它在下发直链时**一定同时给出匹配的 User-Agent 与 Cookie**，
    /// 而不是只丢一个裸链接 —— 裸链接粘到 aria2/IDM 里必然 403，
    /// 因为夸克直链有防盗链，缺 UA/Cookie 会被直接拒。
    /// 本类输出的格式都遵循这一原则：
    ///   1) aria2c 命令（-x/-s 取自同族项目的经验值）
    ///   2) curl 命令（-C - 断点续传）
    ///   3) 纯链接列表（给 IDM 等能自定义请求头的场景）
    ///   4) 带文件名的直链列表（文件名 + Tab + 链接，方便肉眼对照）
    ///   5) **Motrix 图形界面用**（分段列好各字段 + 标注「粘到哪个框」）——
    ///      Motrix 的「新建任务 → 高级」里有 User-Agent / Referer / Cookie 三个独立输入框；
    ///      夸克直链有防盗链，**Cookie 不填就是 412**。
    ///      直接给一行长命令的话用户得自己抠，所以这里拆成一段一段。
    ///      ⚠️ 别把「纯直链」当成 Motrix 能用的东西 —— 它只有 URL，没有 Cookie。
    /// </summary>
    public static class LinkExporter
    {
        /// <summary>导出格式。</summary>
        public enum Format
        {
            /// <summary>aria2c 命令行（每行一条）。</summary>
            Aria2,
            /// <summary>curl 命令行（每行一条）。</summary>
            Curl,
            /// <summary>
            /// **Motrix 图形界面用**：把各字段**分段列好**，每段前面标注「粘到哪个框」。
            /// Motrix 的「新建任务」里有个可展开的「高级」区，
            /// 里面有 **User-Agent / Referer / Cookie** 三个独立输入框
            /// （实测截图确认，不是所有版本都有）。
            /// 夸克直链有防盗链 —— **Cookie 不填就是 412**。
            /// 但 Cookie 混在 curl/aria2c 那种一行长命令里，用户得自己抠，
            /// 所以这里直接拆成一段一段，逐项复制粘贴即可。
            /// 💡 实测结论：**夸克只校验 Cookie**（+ Referer 建议带上）。
            ///    UA 换成 aria2 默认 / 浏览器 / 空值都是 HTTP 206，**User-Agent 可留空**。
            /// </summary>
            MotrixGui,
            /// <summary>纯直链，每行一条（不含 UA/Cookie）。</summary>
            PlainUrls,
            /// <summary>
            /// 带文件名的直链列表：每行「文件名 + 制表符 + 直链」。
            /// 纯 URL 一行一条看不出哪条对应哪个文件，加上文件名后方便对照。
            /// 枚举名保留 Labeled（历史命名），界面显示名是「纯直链」——
            /// 用户明确要求「把名字改回原本的名字纯直链」，别再显示成「标注版」。
            /// </summary>
            Labeled,
        }

        /// <summary>
        /// 单条导出项。
        /// <paramref name="OutputPath"/> 是相对目录的相对路径（含子目录），
        /// 用于让外部下载器保持目录结构 —— 对应 QuarkDownloader 的 fullPath 字段。
        /// </summary>
        public class Item
        {
            public string Url { get; set; }
            public string OutputPath { get; set; }
        }

        /// <summary>
        /// curl 与 aria2 的并发参数。取 16 而非更高的值，理由有二：
        ///   1) 同类项目的经验值 —— QuarkDownloader 导出的是 `-x 16 -s 16`，
        ///      云析上限 32、aria2 默认 `-s 5`，16 是各家折中后的共识区间；
        ///   2) 夸克 CDN 按连接限速且对异常并发有反制，16 路已能吃满多数家宽，
        ///      再往上更容易触发限流（GeZi 自身 512 档位的实测也印证了这点）。
        /// </summary>
        private const int ExportConcurrency = 16;

        /// <summary>
        /// 生成导出文本。
        /// </summary>
        /// <param name="items">待导出的条目（URL + 相对输出路径）。</param>
        /// <param name="ua">下载用 User-Agent，必须是取链时同族的客户端 UA。</param>
        /// <param name="cookie">完整 Cookie 串。</param>
        /// <param name="referer">Referer，通常为 https://pan.quark.cn/。</param>
        /// <param name="format">输出格式。</param>
        /// <param name="absoluteSaveDir">
        /// **仅 Motrix 格式使用**：保存目录的绝对路径（如 `C:\Users\me\Downloads`）。
        /// 🚨 为什么必须要它：Motrix 是**独立进程**，它把 `--save-dir "."` 解析成
        /// **它自己的工作目录**（实测落在 `D:\motrix\`，即 Motrix 安装目录！），
        /// 而不是用户执行命令时所在的目录 —— 文件会跑到用户完全想不到的地方。
        /// curl / aria2c 没这个问题：它们是用户在当前 shell 里直接跑的，
        /// 相对路径天然相对用户的 cwd。
        /// 为空时退回 `"."`（保持旧行为，不静默出错）。
        /// </param>
        public static string Build(IList<Item> items, string ua, string cookie,
            string referer, Format format, string absoluteSaveDir = null)
        {
            var sb = new StringBuilder();
            if (items == null || items.Count == 0)
                return sb.ToString();

            foreach (var it in items)
            {
                if (it == null || string.IsNullOrEmpty(it.Url))
                    continue;

                // 统一转成 POSIX 风格：外部下载器多是命令行工具，跑在 bash/WSL 下，
                // 反斜杠会被当成转义符而不是分隔符。mkdir 与 -o 必须用同一套分隔符，
                // 否则会「在 A 目录建好、往 B\C 写文件」，白白建出错目录。
                string outPath = string.IsNullOrEmpty(it.OutputPath)
                    ? ""
                    : it.OutputPath.Replace('\\', '/');

                switch (format)
                {
                    case Format.PlainUrls:
                        sb.AppendLine(it.Url);
                        break;

                    case Format.Labeled:
                        // 带文件名的纯直链：文件名（含相对目录）+ 制表符 + 直链。
                        // 制表符对齐方便肉眼对照；用原始 OutputPath（含子目录）更贴近用户看到的路径。
                        sb.Append(string.IsNullOrEmpty(it.OutputPath) ? "(未知文件)" : it.OutputPath)
                          .Append('\t').Append(it.Url).AppendLine();
                        break;

                    case Format.Aria2:
                        // 目录保持：aria2 不会自动建目录，需先 mkdir。
                        AppendMkdir(sb, outPath);
                        sb.Append("aria2c -c -x ").Append(ExportConcurrency)
                          .Append(" -s ").Append(ExportConcurrency)
                          .Append(" \"").Append(it.Url).Append('"');
                        if (!string.IsNullOrEmpty(outPath))
                            sb.Append(" -o \"").Append(outPath).Append('"');
                        if (!string.IsNullOrEmpty(ua))
                            sb.Append(" -U \"").Append(ua).Append('"');
                        // aria2 的自定义 header 需要逐条给出；Cookie 与 Referer
                        // 都是防盗链校验项，缺任一都可能 403。
                        sb.Append(" --header=\"Cookie: ").Append(cookie ?? "").Append('"');
                        if (!string.IsNullOrEmpty(referer))
                            sb.Append(" --header=\"Referer: ").Append(referer).Append('"');
                        sb.AppendLine();
                        sb.AppendLine();
                        break;

                    case Format.Curl:
                        AppendMkdir(sb, outPath);
                        sb.Append("curl -L -C - \"").Append(it.Url).Append('"');
                        if (!string.IsNullOrEmpty(outPath))
                            sb.Append(" -o \"").Append(outPath).Append('"');
                        if (!string.IsNullOrEmpty(ua))
                            sb.Append(" -A \"").Append(ua).Append('"');
                        if (!string.IsNullOrEmpty(cookie))
                            sb.Append(" -b \"").Append(cookie).Append('"');
                        if (!string.IsNullOrEmpty(referer))
                            sb.Append(" -e \"").Append(referer).Append('"');
                        sb.AppendLine();
                        sb.AppendLine();
                        break;

                    case Format.MotrixGui:
                        // Motrix 图形界面：每个值单独一段，段首标注「粘到哪个框」。
                        // 夸克直链有防盗链 —— **Cookie 不填就是 412**；UA 实测可留空。
                        // ⚠️ 段与段之间留空行：用户要逐段选中复制，粘在一起的段落很难选准。
                        if (sb.Length > 0)
                        {
                            sb.AppendLine("──────────────────────────────");
                            sb.AppendLine();
                        }
                        sb.AppendLine("【文件名】→ 粘到「高级 → 文件名」（留空则自动识别）");
                        sb.AppendLine(System.IO.Path.GetFileName(outPath));
                        sb.AppendLine();
                        sb.AppendLine("【链接】→ 粘到「新建任务」最上面的输入框");
                        sb.AppendLine(it.Url);
                        sb.AppendLine();
                        sb.AppendLine("【Referer】→ 粘到「高级 → Referer」");
                        sb.AppendLine(referer ?? "");
                        sb.AppendLine();
                        sb.AppendLine("【Cookie】→ 粘到「高级 → Cookie」   ★必填，不填会 412");
                        sb.AppendLine(cookie ?? "");
                        sb.AppendLine();
                        sb.AppendLine("【User-Agent】→ 粘到「高级 → User-Agent」（可留空，实测不影响）");
                        sb.AppendLine(ua ?? "");
                        sb.AppendLine();
                        break;
                }
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 若输出路径含子目录，前置一条 mkdir -p，保证外部下载器能写入。
        /// 入参必须是已转好 POSIX 分隔符的路径（调用方统一处理）。
        /// </summary>
        private static void AppendMkdir(StringBuilder sb, string posix)
        {
            if (string.IsNullOrEmpty(posix))
                return;
            int slash = posix.LastIndexOf('/');
            if (slash <= 0)
                return;
            string dir = posix.Substring(0, slash);
            sb.Append("mkdir -p \"").Append(dir).Append("\" && ");
        }
    }
}
