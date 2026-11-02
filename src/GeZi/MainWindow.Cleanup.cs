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
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GeZi.Core.Api;
using GeZi.Providers.Shared;

namespace GeZi
{
    /// <summary>
    /// 「为取直链而转存」的临时文件清理。
    /// <para>
    /// 阿里 / 迅雷分享**必须转存**才能拿到直链（不像夸克 / UC 可以免转存），
    /// 于是每下一个文件都会在用户自己网盘里留一份副本。这份副本：
    /// </para>
    /// <list type="bullet">
    /// <item>下载**过程中绝不能删** —— CDN 回源时文件没了会 503 洪泛，速度直接崩；</item>
    /// <item>下载**结束后必须删** —— 否则用户网盘里全是垃圾（用户已经明确抱怨过）。</item>
    /// </list>
    /// <para>
    /// 🚨 所以清理点只有一个：任务进入终态（完成 / 失败 / 取消）。
    /// 崩溃或强退来不及清理的，靠启动时扫 <c>GeZi.cleanup.txt</c> 兜底。
    /// </para>
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>待清理记录，形如 <c>"2|fileId"</c>（前段是 PanKind 的整数值）。</summary>
        private readonly HashSet<string> _storedJournal =
            new HashSet<string>(StringComparer.Ordinal);

        private bool _storedJournalLoaded;
        private bool _storedSweepStarted;

        /// <summary>
        /// 目标路径 → 下载条目。任务终态时靠它找回 <see cref="DownloadItem.StoredIds"/>。
        /// <para>
        /// 为什么不用 dest 直接存 id 列表：直链过期重取链时 <c>RefreshLinkAsync</c> 会往
        /// **同一个 DownloadItem 实例**里追加新 id，共享实例才能自动累积。
        /// </para>
        /// </summary>
        private readonly Dictionary<string, DownloadItem> _itemByDest =
            new Dictionary<string, DownloadItem>(StringComparer.OrdinalIgnoreCase);

        /// <summary>登记「这个目标路径对应哪个下载条目」，供终态清理反查。</summary>
        private void TrackItemForDest(string dest, DownloadItem item)
        {
            if (string.IsNullOrEmpty(dest) || item == null) return;
            _itemByDest[dest] = item;
        }

        private static string StoredJournalPath
        {
            get
            {
                string dir = null;
                try { dir = Path.GetDirectoryName(typeof(MainWindow).Assembly.Location); }
                catch { }
                if (string.IsNullOrEmpty(dir)) dir = AppDomain.CurrentDomain.BaseDirectory;
                return Path.Combine(dir ?? ".", "GeZi.cleanup.txt");
            }
        }

        private static string StoredRecord(PanKind kind, string id)
        {
            return ((int)kind).ToString() + "|" + id;
        }

        private void LoadStoredJournal()
        {
            if (_storedJournalLoaded) return;
            _storedJournalLoaded = true;
            try
            {
                if (!File.Exists(StoredJournalPath)) return;
                foreach (string line in File.ReadAllLines(StoredJournalPath, Encoding.UTF8))
                {
                    string s = (line ?? "").Trim();
                    if (s.Length > 2 && s.IndexOf('|') > 0)
                        _storedJournal.Add(s);
                }
            }
            catch { }
        }

        private void SaveStoredJournal()
        {
            try
            {
                if (_storedJournal.Count == 0)
                {
                    if (File.Exists(StoredJournalPath)) File.Delete(StoredJournalPath);
                    return;
                }
                var sb = new StringBuilder();
                foreach (string rec in _storedJournal) sb.AppendLine(rec);
                File.WriteAllText(StoredJournalPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>
        /// 把一次转存记进 item，并落盘到日志（防崩溃漏清理）。
        /// 取链成功时调用；直链过期重取链时会再调一次。
        /// </summary>
        private void NoteStored(DownloadItem item, PanKind kind, string storedId)
        {
            if (item == null || string.IsNullOrEmpty(storedId)) return;
            item.StoredKind = kind;
            item.AddStored(storedId);

            LoadStoredJournal();
            if (_storedJournal.Add(StoredRecord(kind, storedId)))
                SaveStoredJournal();
        }

        /// <summary>按网盘分组取一个可用的客户端（优先当前实例 / 缓存实例，最后临时新建）。</summary>
        private IPanProvider ResolveProviderForCleanup(PanKind kind)
        {
            if (_ownProvider != null && _activeKind == kind) return _ownProvider;

            IPanProvider cached;
            if (_providerCache != null && _providerCache.TryGetValue(kind, out cached) && cached != null)
                return cached;

            try { return CreateProviderFor(kind); }
            catch { return null; }
        }

        /// <summary>
        /// 任务终态清理入口：按目标路径找到对应条目并清理它的临时转存。
        /// 用后即摘，避免字典常驻。
        /// </summary>
        private async Task CleanupStoredForDestAsync(string dest)
        {
            if (string.IsNullOrEmpty(dest)) return;
            DownloadItem item;
            if (!_itemByDest.TryGetValue(dest, out item)) return;
            _itemByDest.Remove(dest);
            await CleanupStoredForItemAsync(item).ConfigureAwait(true);
        }

        /// <summary>
        /// 任务终态清理：删掉这个任务为取链而转存的所有副本。
        /// 失败只记日志 —— 清理是收尾工作，绝不能因此把已完成的任务标成失败。
        /// </summary>
        private async Task CleanupStoredForItemAsync(DownloadItem item)
        {
            if (item == null || item.StoredIds.Count == 0) return;

            // 🚨🚨 2026-11-02 修「设置里的『下载完成后自动清理转存文件』对阿里/迅雷无效」。
            //
            //   根因：转存清理有**两条独立路径** ——
            //     ① CleanupTransferredAsync（夸克/UC 那条，看 _settings.AutoClean）；
            //     ② 本方法（阿里/迅雷那条，走 provider.CleanupStoredAsync）——
            //        **完全没检查 AutoClean**，无条件删。
            //   用户关掉这个开关后，阿里/迅雷的转存副本照样被删，设置形同虚设。
            //
            //   ⚠️ 但要注意：**关掉开关不等于不清理内存记账**。
            //     上面那两行 Clear / 落盘必须照常执行 —— 否则 item.StoredIds 会一直挂着，
            //     下次任务终态又会被重复处理，日志里刷一堆无意义的记录。
            //     所以判据放在**取完 ids、清完记账之后**，只跳过真正的网络删除。
            var ids = item.StoredIds.ToList();
            item.StoredIds.Clear();

            LoadStoredJournal();
            foreach (string id in ids) _storedJournal.Remove(StoredRecord(item.StoredKind, id));
            SaveStoredJournal();

            // 记账已清完（见方法开头），这里才决定要不要真的去网盘删。
            if (_settings == null || !_settings.AutoClean)
            {
                LogDetail("已保留网盘转存文件 " + ids.Count + " 个（"
                    + PanUrlParser.DisplayNameOf(item.StoredKind) + "，可在设置里关闭自动清理）");
                return;
            }

            IPanProvider provider = ResolveProviderForCleanup(item.StoredKind);
            if (provider == null)
            {
                LogDetail("临时转存清理跳过（" + PanUrlParser.DisplayNameOf(item.StoredKind) + " 无可用客户端）");
                return;
            }

            try
            {
                await provider.CleanupStoredAsync(ids).ConfigureAwait(true);
                LogDetail("已清理临时转存 " + ids.Count + " 项（" + PanUrlParser.DisplayNameOf(item.StoredKind) + "）");
            }
            catch (Exception ex)
            {
                LogDetail("临时转存清理失败（" + PanUrlParser.DisplayNameOf(item.StoredKind) + "）: " + ex.Message);
            }
        }

        /// <summary>
        /// 启动兜底：上一次运行崩溃 / 被强杀时来不及清理的转存，在登录态就绪后扫一遍删掉。
        /// 只跑一次；必须在**至少一家网盘登录成功后**才调，否则客户端没凭证删不掉。
        /// </summary>
        private async Task SweepStaleStoredAsync()
        {
            if (_storedSweepStarted) return;
            if (!IsLoggedInAny) return;

            LoadStoredJournal();
            if (_storedJournal.Count == 0) return;

            _storedSweepStarted = true;

            // 先按 kind 分组，避免为每家网盘反复建客户端。
            var byKind = new Dictionary<PanKind, List<string>>();
            foreach (string rec in _storedJournal.ToList())
            {
                int sep = rec.IndexOf('|');
                if (sep <= 0) continue;
                int k;
                if (!int.TryParse(rec.Substring(0, sep), out k)) continue;
                PanKind kind = (PanKind)k;
                List<string> list;
                if (!byKind.TryGetValue(kind, out list))
                {
                    list = new List<string>();
                    byKind[kind] = list;
                }
                list.Add(rec.Substring(sep + 1));
            }

            foreach (var kv in byKind)
            {
                IPanProvider provider = ResolveProviderForCleanup(kv.Key);
                if (provider == null) continue;   // 这家还没登录，留着下次再清
                try
                {
                    await provider.CleanupStoredAsync(kv.Value).ConfigureAwait(true);
                    foreach (string id in kv.Value)
                        _storedJournal.Remove(StoredRecord(kv.Key, id));
                    LogDetail("启动清理遗留临时转存 " + kv.Value.Count + " 项（"
                        + PanUrlParser.DisplayNameOf(kv.Key) + "）");
                }
                catch (Exception ex)
                {
                    LogDetail("启动清理临时转存失败（" + PanUrlParser.DisplayNameOf(kv.Key) + "）: " + ex.Message);
                }
            }

            SaveStoredJournal();
        }
    }
}
