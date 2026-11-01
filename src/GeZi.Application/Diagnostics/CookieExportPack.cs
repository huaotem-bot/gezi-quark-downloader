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
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace GeZi.Services
{
    /// <summary>
    /// 把多个网盘、多个账号的凭证打包成一个 zip。
    /// <para>
    /// 结构（用户 2026-10-26 指定）：
    /// <code>
    /// GeZi-Cookies-20261026-221500.zip
    /// ├── quark/   夸克昵称.txt
    /// ├── uc/      UC昵称.txt
    /// ├── aliyun/  阿里昵称.txt
    /// └── xunlei/  迅雷昵称.txt
    /// </code>
    /// 四个文件夹固定存在（该家一个账号都没勾也建空目录），
    /// 这样用户拿到包一眼就知道"四家都在这里"，不会以为漏导了。
    /// </para>
    /// </summary>
    public static class CookieExportPack
    {
        /// <summary>一个待导出的账号。</summary>
        public sealed class Item
        {
            /// <summary>网盘英文名，也是 zip 里的文件夹名（quark / uc / aliyun / xunlei）。</summary>
            public string Folder { get; set; } = "";

            /// <summary>账号展示名，用作文件名。空则回退成 <c>account-N</c>。</summary>
            public string AccountName { get; set; } = "";

            /// <summary>凭证明文（Cookie 串 / 令牌串）。</summary>
            public string Secret { get; set; } = "";

            /// <summary>
            /// 附加凭证（可空）。目前只用于阿里云盘：<c>DeviceId</c> 与
            /// <c>DevicePrivateKey</c> 是设备签名的一部分，**缺了它们导出的阿里凭证换台机器用不了**
            /// —— 每次请求都要用私钥签 secp256k1 摘要，没有私钥就只能重新走一遍登录。
            /// 序列化成每行 <c>key=value</c>，附在 Secret 之后，用空行分隔。
            /// </summary>
            public System.Collections.Generic.IDictionary<string, string> Extra { get; set; }
        }

        /// <summary>四个固定文件夹名，顺序即 zip 里的顺序。</summary>
        public static readonly string[] Folders = { "quark", "uc", "aliyun", "xunlei" };

        /// <summary>
        /// 写出 zip，返回最终路径。失败抛异常，由调用方转成提示。
        /// </summary>
        public static string Create(string zipPath, IList<Item> items)
        {
            if (string.IsNullOrWhiteSpace(zipPath))
                throw new ArgumentException("导出路径为空。", "zipPath");
            if (items == null)
                throw new ArgumentNullException("items");

            // 先写临时文件再改名：中途失败不会留下一个半截 zip 让用户以为导出成功了。
            string tmp = zipPath + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    // ① 先把四个文件夹建出来（ZipArchive 里"空目录"靠一个以 / 结尾的空条目表达）。
                    foreach (string folder in Folders)
                    {
                        string probe = folder + "/";
                        bool has = false;
                        foreach (var it in items)
                        {
                            if (it != null && string.Equals(it.Folder, folder, StringComparison.OrdinalIgnoreCase))
                            {
                                has = true;
                                break;
                            }
                        }
                        if (!has)
                            zip.CreateEntry(probe, CompressionLevel.NoCompression);
                    }

                    // ② 再写账号文件。同网盘内重名时追加 -2、-3…，绝不覆盖。
                    var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    int seq = 0;
                    foreach (var it in items)
                    {
                        if (it == null) continue;
                        string folder = NormalizeFolder(it.Folder);
                        if (folder.Length == 0) continue;
                        seq++;

                        string baseName = SanitizeFileName(it.AccountName);
                        if (baseName.Length == 0)
                            baseName = "account-" + seq.ToString(System.Globalization.CultureInfo.InvariantCulture);

                        string key = folder + "/" + baseName;
                        int n;
                        if (used.TryGetValue(key, out n))
                        {
                            n++;
                            used[key] = n;
                            baseName = baseName + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        }
                        else
                        {
                            used[key] = 1;
                        }

                        var entry = zip.CreateEntry(folder + "/" + baseName + ".txt",
                            CompressionLevel.Optimal);
                        using (var s = entry.Open())
                        using (var w = new StreamWriter(s, new UTF8Encoding(false)))
                        {
                            w.Write(it.Secret ?? "");

                            // 附加凭证（阿里设备 id / 私钥）另起一段写，保持「第一行就是 Cookie 串」，
                            // 老的手工复制习惯不受影响。
                            if (it.Extra != null && it.Extra.Count > 0)
                            {
                                w.Write("\n\n");
                                foreach (var kv in it.Extra)
                                {
                                    if (string.IsNullOrEmpty(kv.Key)) continue;
                                    if (string.IsNullOrEmpty(kv.Value)) continue;
                                    w.Write(kv.Key);
                                    w.Write("=");
                                    w.Write(kv.Value);
                                    w.Write("\n");
                                }
                            }
                        }
                    }
                }

                if (File.Exists(zipPath))
                    File.Delete(zipPath);
                File.Move(tmp, zipPath);
                return zipPath;
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        /// <summary>把网盘标识归一到四个固定文件夹名；认不出返回空串（该条会被跳过）。</summary>
        private static string NormalizeFolder(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw.Trim().ToLowerInvariant();
            switch (s)
            {
                case "quark": return "quark";
                case "uc": return "uc";
                case "aliyun":
                case "ali": return "aliyun";
                case "xunlei":
                case "xl": return "xunlei";
                default: return "";
            }
        }

        /// <summary>
        /// 把账号名变成合法文件名：去掉 Windows 不允许的字符与控制字符，
        /// 去掉结尾的点和空格（Windows 会静默截断），并限长避免路径超限。
        /// </summary>
        private static string SanitizeFileName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (c < 32) continue;
                if (c == '\\' || c == '/' || c == ':' || c == '*' || c == '?'
                    || c == '"' || c == '<' || c == '>' || c == '|')
                    continue;
                sb.Append(c);
            }
            string s = sb.ToString().Trim().TrimEnd('.');
            if (s.Length > 60) s = s.Substring(0, 60).Trim();
            return s;
        }
    }
}
