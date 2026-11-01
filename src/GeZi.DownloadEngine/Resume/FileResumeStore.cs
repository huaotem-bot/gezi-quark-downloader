// GeZi - 鸽子下载器
// Copyright (C) 2026 Yi Yuan
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
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.DownloadEngine.Resume;

/// <summary>
/// 把续传状态存成目标文件旁边的一个文本文件（<c>&lt;key&gt;.geziresume</c>）。
///
/// 为什么用手写文本格式而不是 JSON：
/// net48 下可用的 JSON 序列化（<c>JavaScriptSerializer</c> / <c>DataContractJsonSerializer</c>）
/// 要么要额外程序集引用、要么要给每个类打 <c>[DataContract]</c>，而这里的结构极简单
/// （一个总长 + 一个标识 + 一串三元组）。手写格式零依赖、解析快、出问题时能直接肉眼查，
/// 也避免了「反序列化异常导致续传整份失效」这种连锁故障。
///
/// 写入用「先写临时文件再原子替换」：进程在写状态时被杀不会留下半截文件。
/// </summary>
public sealed class FileResumeStore : IResumeStore
{
    /// <summary>无状态单例（行为只取决于传入的 key 路径）。</summary>
    public static readonly FileResumeStore Instance = new FileResumeStore();

    private const string Magic = "GZRESUME";
    private const string Extension = ".geziresume";

    public string Name => "file";

    /// <summary>由目标文件路径推导状态文件路径。</summary>
    public static string StatePathOf(string key)
    {
        return key + Extension;
    }

    public Task<ResumeState> LoadAsync(string key, CancellationToken cancel)
    {
        return Task.Run(() => LoadCore(key, cancel), cancel);
    }

    public Task SaveAsync(string key, ResumeState state, CancellationToken cancel)
    {
        return Task.Run(() => SaveCore(key, state, cancel), cancel);
    }

    public Task ClearAsync(string key, CancellationToken cancel)
    {
        return Task.Run(
            () =>
            {
                cancel.ThrowIfCancellationRequested();
                TryDelete(StatePathOf(key));
            },
            cancel);
    }

    private static ResumeState LoadCore(string key, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();

        string path = StatePathOf(key);
        if (!File.Exists(path))
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path, Encoding.UTF8);
        }
        catch
        {
            // 读不出来就当没有续传，绝不让状态文件的问题挡住下载本身
            return null;
        }

        if (lines.Length < 3)
        {
            return null;
        }

        string[] head = lines[0].Trim().Split(' ');
        if (head.Length < 2 || head[0] != Magic)
        {
            return null;
        }

        int version;
        if (!int.TryParse(head[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out version) ||
            version != ResumeState.CurrentVersion)
        {
            return null;
        }

        var state = new ResumeState { Version = version };
        var chunks = new List<ResumeChunk>(Math.Max(16, lines.Length - 3));

        for (int i = 1; i < lines.Length; i++)
        {
            cancel.ThrowIfCancellationRequested();

            string line = lines[i];
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            int sp = line.IndexOf(' ');
            if (sp <= 0)
            {
                return null;
            }

            string tag = line.Substring(0, sp);
            string rest = line.Substring(sp + 1).Trim();

            switch (tag)
            {
                case "total":
                    if (!long.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out long total))
                    {
                        return null;
                    }

                    state.TotalBytes = total;
                    break;

                case "validator":
                    state.Validator = Decode(rest);
                    break;

                case "saved":
                    if (long.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
                    {
                        try
                        {
                            state.SavedAtUtc = new DateTime(ticks, DateTimeKind.Utc);
                        }
                        catch
                        {
                            state.SavedAtUtc = DateTime.UtcNow;
                        }
                    }

                    break;

                case "chunk":
                    ResumeChunk chunk = ParseChunk(rest);
                    if (chunk == null)
                    {
                        return null;
                    }

                    chunks.Add(chunk);
                    break;
            }
        }

        state.Chunks = chunks.ToArray();
        return state.IsUsable() ? state : null;
    }

    private static ResumeChunk ParseChunk(string text)
    {
        string[] parts = text.Split(' ');
        if (parts.Length < 3)
        {
            return null;
        }

        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long start) ||
            !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long end) ||
            !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long position))
        {
            return null;
        }

        return new ResumeChunk { Start = start, End = end, Position = position };
    }

    private static void SaveCore(string key, ResumeState state, CancellationToken cancel)
    {
        if (state == null || state.Chunks == null || state.Chunks.Length == 0)
        {
            return;
        }

        cancel.ThrowIfCancellationRequested();

        var sb = new StringBuilder(64 + state.Chunks.Length * 40);
        sb.Append(Magic).Append(' ').Append(state.Version).Append('\n');
        sb.Append("total ").Append(state.TotalBytes.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("validator ").Append(Encode(state.Validator)).Append('\n');
        sb.Append("saved ").Append(state.SavedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');

        foreach (ResumeChunk c in state.Chunks)
        {
            if (c == null)
            {
                continue;
            }

            sb.Append("chunk ")
              .Append(c.Start.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(c.End.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(c.Position.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        string path = StatePathOf(key);
        // tmp 名带 GUID：原来固定 ".tmp" 后缀，两个下载线程同时保存同一个 key 时
        // 会互相踩对方的临时文件（一个正在写、另一个 TryDelete 掉再 Move）。
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            // 先落盘再替换：WriteAllText 不带 Flush(true)，
            // 断电时可能出现「文件名换好了、内容是空的」。
            byte[] bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }

            // 🚨 真原子替换。原来的写法是「TryDelete(path) 然后 File.Move(tmp, path)」，
            // 那两步之间被杀 / 断电 = 新旧两份都没了（旧已删、新还没改名），
            // 续传快照凭空消失，用户重开软件就只能从头下。
            // File.Replace 在 NTFS 上是单次事务性改名，要么旧要么新，不会出现空档。
            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        catch
        {
            TryDelete(tmp);
        }
    }

    /// <summary>
    /// 校验器可能是带引号、带空格的 ETag（例如 <c>"abc 123"</c>），
    /// 用 base64 编码避免解析歧义。
    /// </summary>
    private static string Encode(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static string Decode(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch
        {
            return "";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 状态文件删不掉不影响下载结果，忽略
        }
    }
}
