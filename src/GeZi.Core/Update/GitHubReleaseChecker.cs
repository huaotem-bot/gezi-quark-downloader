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
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.Core.Update
{
    /// <summary>
    /// 查 GitHub Releases 的「最新发布」（2026-10-18 新一代检查更新）。
    /// 项目已开源（github.com/huaotem-bot/gezi-quark-downloader），
    /// Release 天然带 tag_name（版本号）、body（更新说明）、html_url（下载页），
    /// 不依赖夸克登录态、不写用户网盘、无中转清理 —— 旧那套网盘更新源整套退役。
    /// GitHub API 对没有 User-Agent 的请求直接 403。
    /// 启动检查要快；国内访问 api.github.com 偶发不通，
    /// 失败时启动检查静默跳过（手动检查才如实报错），10 秒足够区分"慢"和"不通"。
    /// </summary>
    public sealed class GitHubReleaseChecker
    {
        /// <summary>仓库归属（与 README 的开源地址一致）。</summary>
        public const string RepoOwner = "huaotem-bot";

        /// <summary>仓库名（与 README 的开源地址一致）。</summary>
        public const string RepoName = "gezi-quark-downloader";

        /// <summary>最新发布接口（GitHub 固定语义：永远指向 latest release）。</summary>
        public const string LatestApiUrl =
            "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";

        /// <summary>仓库 Releases 列表页（API 失败时给用户的手动入口）。</summary>
        public const string ReleasesPageUrl =
            "https://github.com/" + RepoOwner + "/" + RepoName + "/releases";

        /// <summary>GitHub API 要求的 User-Agent。</summary>
        private const string UserAgent = "GeZi-Updater (https://github.com/" + RepoOwner + "/" + RepoName + ")";

        private readonly TimeSpan _timeout;

        public GitHubReleaseChecker(TimeSpan? timeout = null)
        {
            _timeout = timeout ?? TimeSpan.FromSeconds(10);
        }

        /// <summary>查最新发布。取不到 / 解析失败 / 版本号不认识时返回 null。</summary>
        public async Task<GitHubRelease> FindLatestAsync(CancellationToken ct = default(CancellationToken))
        {
            string json = await GetStringAsync(LatestApiUrl, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json))
                return null;
            return Parse(json);
        }

        /// <summary>
        /// GET 一个 URL 并返回响应体；HTTP 非 2xx / 超时 / 网络错误一律返回 null
        /// （由上层决定"静默跳过"还是"如实报错"）。
        /// </summary>
        private async Task<string> GetStringAsync(string url, CancellationToken ct)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = UserAgent;
            request.Accept = "application/vnd.github+json";
            request.Timeout = (int)_timeout.TotalMilliseconds;
            request.ReadWriteTimeout = (int)_timeout.TotalMilliseconds;
            // 与下载器同一套纪律：本机开着代理会拖慢/卡死直连
            request.Proxy = null;

            try
            {
                using (ct.Register(() => { try { request.Abort(); } catch { } }))
                using (var resp = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                using (var stream = resp.GetResponseStream())
                {
                    if (stream == null) return null;
                    using (var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8))
                        return await reader.ReadToEndAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>把 GitHub 的 release JSON 解析成 <see cref="GitHubRelease"/>。</summary>
        internal static GitHubRelease Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var ser = new DataContractJsonSerializer(typeof(ReleaseDto));
                using (var ms = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                {
                    var dto = ser.ReadObject(ms) as ReleaseDto;
                    if (dto == null) return null;

                    var rel = new GitHubRelease
                    {
                        TagName = dto.tag_name ?? "",
                        Title = dto.name ?? "",
                        Body = dto.body ?? "",
                        HtmlUrl = dto.html_url ?? "",
                    };

                    UpdateVersion v;
                    if (!UpdateVersion.TryParse(dto.tag_name, out v) || v == null)
                        return null;   // tag 不符合 v主.次.补 → 当作没有（不误报）
                    rel.Version = v;

                    DateTime pub;
                    if (!string.IsNullOrEmpty(dto.published_at)
                        && DateTime.TryParse(dto.published_at,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out pub))
                        rel.PublishedAt = pub;

                    // assets 里挑 win64 安装包（发版附件名固定 GeZi-vX.Y.Z-win64.zip）
                    foreach (var a in NonNull(dto.assets))
                    {
                        if (a != null && !string.IsNullOrEmpty(a.name)
                            && a.name.StartsWith("GeZi-v", StringComparison.OrdinalIgnoreCase)
                            && a.name.EndsWith("-win64.zip", StringComparison.OrdinalIgnoreCase))
                        {
                            rel.AssetSize = a.size;
                            break;
                        }
                    }
                    return rel;
                }
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<AssetDto> NonNull(List<AssetDto> list)
        {
            return list ?? new List<AssetDto>();
        }

        /// <summary>GitHub release JSON 的最小字段集（snake_case → DataMember Name）。</summary>
        [DataContract]
        internal sealed class ReleaseDto
        {
            [DataMember(Name = "tag_name")] public string tag_name = null;
            [DataMember(Name = "name")] public string name = null;
            [DataMember(Name = "body")] public string body = null;
            [DataMember(Name = "html_url")] public string html_url = null;
            [DataMember(Name = "published_at")] public string published_at = null;
            [DataMember(Name = "assets")] public List<AssetDto> assets = null;
        }

        [DataContract]
        internal sealed class AssetDto
        {
            [DataMember(Name = "name")] public string name = null;
            [DataMember(Name = "size")] public long size = 0;
        }
    }
}
