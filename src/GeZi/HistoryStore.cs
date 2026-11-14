// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace GeZi
{
    /// <summary>
    /// 一条「解析过的分享」记录（按 <c>h</c> 唤出的历史面板用）。
    /// 记的是**分享**（pwd_id / 提取码），不是本地下载记录 ——
    /// 下载历史已随「下载历史」弹窗一起删除（2026-10-31，失败/已完成筛选页即历史）。
    /// </summary>
    [DataContract]
    public class ShareHistoryEntry
    {
        [DataMember(Order = 1)] public string PwdId { get; set; }

        /// <summary>提取码；无码时为空串。</summary>
        [DataMember(Order = 2)] public string Passcode { get; set; }

        /// <summary>解析时进入的目录（带 fid 的分享链接才有意义，普通分享是 "0"）。</summary>
        [DataMember(Order = 3)] public string StartFid { get; set; }

        /// <summary>
        /// 面板里显示的名字：解析后根目录里的**第一个文件夹名**。
        /// 没有文件夹（分享根目录只有散文件）时退化成「（根目录）· N 个文件」。
        /// </summary>
        [DataMember(Order = 4)] public string Name { get; set; }

        /// <summary>上面那个名字是不是文件夹 —— 决定面板里用文件夹图标还是文件图标。</summary>
        [DataMember(Order = 5)] public bool IsFolder { get; set; }

        /// <summary>解析时刻。"yyyy-MM-dd HH:mm:ss"。</summary>
        [DataMember(Order = 6)] public string Time { get; set; }

        /// <summary>
        /// 用户手动「置顶」的标记：置顶的始终排在最前，**不受后续解析的影响**。
        /// 那样只是把它挪到最前面一次，而且显示的时间
        /// 会变成谎话（明明是上周解析的，却写着"今天"）。
        /// </summary>
        [DataMember(Order = 7)] public bool Pinned { get; set; }
    }

    /// <summary>
    /// 未完成任务记录 + 解析历史的持久化。
    /// 移植自 Python Flet 版的 `flet_pending`（未完成任务），
    /// 存在与配置同目录（`SettingsStore.FilePath` 的同级 `GeZi.state.json`），
    /// 保证便携（跟 exe 走）与可写回退（%LOCALAPPDATA%）行为一致。
    /// JSON 用 `DataContractJsonSerializer`（.NET Framework 自带，零 NuGet）。
    /// ⚠️ 不能用 `JavaScriptSerializer`：它所在的 System.Web.Extensions 依赖 System.Web，
    /// 而 **WPF 项目引用 System.Web 会导致 XAML 编译器找不到程序集**（实测 MC1000）。
    /// </summary>
    internal static class HistoryStore
    {
        /// <summary>
        /// 解析历史的上限。它是"最近打开过什么"，翻久了没意义，
        /// 而且每条都要在面板里画出来。
        /// </summary>
        private const int MaxShareHistory = 50;

        [DataContract]
        private sealed class State
        {
            [DataMember(Order = 2)] public List<PendingTask> Pending { get; set; }
            /// <summary>解析历史。⚠️ 老 state.json 里没有这个字段 → 反序列化为 null，Load 里补空表。</summary>
            [DataMember(Order = 3)] public List<ShareHistoryEntry> Shares { get; set; }
            // 下载历史 Records（原 Order=1）已删：老 state.json 里残留的该字段
            // 反序列化时被忽略，下次 Save 起自然消失。
        }

        private static string StatePath
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(SettingsStore.FilePath);
                    return Path.Combine(dir, "GeZi.state.json");
                }
                catch
                {
                    return Path.Combine(Util.GetLocalAppDataDir(), "GeZi.state.json");
                }
            }
        }

        private static State Load()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    using (var fs = File.OpenRead(StatePath))
                    {
                        var ser = new DataContractJsonSerializer(typeof(State));
                        var s = ser.ReadObject(fs) as State;
                        if (s != null)
                        {
                            if (s.Pending == null) s.Pending = new List<PendingTask>();
                            if (s.Shares == null) s.Shares = new List<ShareHistoryEntry>();
                            return s;
                        }
                    }
                }
            }
            catch { }
            return new State
            {
                Pending = new List<PendingTask>(),
                Shares = new List<ShareHistoryEntry>(),
            };
        }

        private static void Save(State s)
        {
            try
            {
                string path = StatePath;
                string tmp = path + ".tmp";
                // 原子写：先写临时文件再替换，避免写一半留下半截 JSON 导致下次读崩。
                using (var fs = File.Create(tmp))
                {
                    var ser = new DataContractJsonSerializer(typeof(State));
                    ser.WriteObject(fs, s);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch { }
        }

        // ---------------- 解析历史（按 h 唤出的面板用） ----------------

        /// <summary>解析历史：**置顶的在最前**，其余按时间倒序（新的在前）。</summary>
        public static List<ShareHistoryEntry> GetShareHistory()
        {
            var s = Load();
            var list = new List<ShareHistoryEntry>(s.Shares);
            list.Reverse();   // 存储是"旧的在前"，给 UI 时翻成"新的在前"
            if (list.TrueForAll(x => !x.Pinned))
                return list;

            // 置顶的提到最前；两段内部都保持"新的在前"
            var result = list.FindAll(x => x.Pinned);
            result.AddRange(list.FindAll(x => !x.Pinned));
            return result;
        }

        /// <summary>
        /// 记一条解析历史。
        /// pwd_id 相同就把旧的删掉再插到**末尾**（= 最新）——
        /// 再解析一次同一条链接，只是让它回到列表最前面并更新名字/提取码/时间，
        /// 而不是堆出好几条一模一样的记录。
        /// ⚠️ 旧那条若被置顶过，要**把置顶状态带过来** —— 置顶是用户的显式选择，
        ///    不能因为他又点了一次这条链接就悄悄取消。
        /// </summary>
        public static void AddShareHistory(string pwdId, string passcode, string startFid,
            string name, bool isFolder)
        {
            if (string.IsNullOrEmpty(pwdId)) return;
            try
            {
                var s = Load();
                bool wasPinned = false;
                for (int i = s.Shares.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(s.Shares[i].PwdId, pwdId, StringComparison.Ordinal))
                    {
                        wasPinned = wasPinned || s.Shares[i].Pinned;
                        s.Shares.RemoveAt(i);
                    }
                }
                s.Shares.Add(new ShareHistoryEntry
                {
                    PwdId = pwdId,
                    Passcode = passcode ?? "",
                    StartFid = string.IsNullOrEmpty(startFid) ? "0" : startFid,
                    Name = name ?? "",
                    IsFolder = isFolder,
                    Pinned = wasPinned,
                    Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                });
                if (s.Shares.Count > MaxShareHistory)
                    s.Shares.RemoveRange(0, s.Shares.Count - MaxShareHistory);
                Save(s);
            }
            catch { }
        }

        /// <summary>置顶 / 取消置顶某条解析历史。</summary>
        public static void SetSharePinned(string pwdId, bool pinned)
        {
            if (string.IsNullOrEmpty(pwdId)) return;
            try
            {
                var s = Load();
                foreach (var e in s.Shares)
                {
                    if (string.Equals(e.PwdId, pwdId, StringComparison.Ordinal))
                        e.Pinned = pinned;
                }
                Save(s);
            }
            catch { }
        }

        /// <summary>删掉某条解析历史（按 pwd_id）。</summary>
        public static void RemoveShareHistory(string pwdId)
        {
            if (string.IsNullOrEmpty(pwdId)) return;
            try
            {
                var s = Load();
                for (int i = s.Shares.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(s.Shares[i].PwdId, pwdId, StringComparison.Ordinal))
                        s.Shares.RemoveAt(i);
                }
                Save(s);
            }
            catch { }
        }

        public static void ClearShareHistory()
        {
            var s = Load();
            s.Shares.Clear();
            Save(s);
        }

        // ---------------- 未完成任务 ----------------

        public static List<PendingTask> GetPending()
        {
            return Load().Pending;
        }

        /// <summary>整体覆盖未完成任务列表（由 UI 在任务状态变化时调用）。</summary>
        public static void SetPending(List<PendingTask> pending)
        {
            var s = Load();
            s.Pending = pending ?? new List<PendingTask>();
            Save(s);
        }

        public static void ClearPending()
        {
            var s = Load();
            s.Pending.Clear();
            Save(s);
        }
    }

    /// <summary>
    /// 未完成任务的跨重启记录。
    /// 与 Python 的 flet_pending 对齐。字段经过一次扩充：早期版本只记 9 个字段，
    /// 不足以**重新取链**（续传必须重新申请直链，因为直链带时效签名）。
    /// 现在补齐了 <see cref="FromShare"/> / <see cref="Fid"/> / <see cref="Token"/> /
    /// <see cref="PwdId"/> / <see cref="Stoken"/> / <see cref="Passcode"/>，
    /// 使启动时可以**不依赖原界面上下文**地重走"取链 → 续传"。
    /// 新增字段一律用 <c>Order &gt; 9</c>，且旧文件没有这些字段时反序列化为默认值
    /// （DataContractJsonSerializer 允许缺字段），保证向后兼容。
    /// </summary>
    [DataContract]
    public class PendingTask
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        [DataMember(Order = 2)] public string Dest { get; set; }
        [DataMember(Order = 3)] public string Url { get; set; }
        [DataMember(Order = 4)] public string Fid { get; set; }
        [DataMember(Order = 5)] public string Token { get; set; }
        [DataMember(Order = 6)] public bool FromShare { get; set; }
        [DataMember(Order = 7)] public long Size { get; set; }
        [DataMember(Order = 8)] public bool StreamPlay { get; set; }
        [DataMember(Order = 9)] public string Status { get; set; }

        // ---- 以下为续传所需（Order ≥ 10），旧记录里缺省 = 默认值 ----

        /// <summary>分享链接的 pwd_id —— 分享模式下重取链要用。</summary>
        [DataMember(Order = 10)] public string PwdId { get; set; }

        /// <summary>分享的 stoken（取链凭证）—— 分享模式下重取链要用。</summary>
        [DataMember(Order = 11)] public string Stoken { get; set; }

        /// <summary>分享提取码；无码时为空串。</summary>
        [DataMember(Order = 12)] public string Passcode { get; set; }

        /// <summary>该任务的相对目录（用于重建目标路径，保持目录结构）。</summary>
        [DataMember(Order = 13)] public string RelDir { get; set; }

        /// <summary>
        /// 分享链接的进入目录 fid（带 fid 的分享链接才有意义，普通分享是 "0"）。
        /// 自动重解析分享时的**起点**：从它开始逐级展开找目标文件。
        /// </summary>
        [DataMember(Order = 15)] public string StartFid { get; set; }

        /// <summary>
        /// 文件在分享内的**完整相对目录**（相对 <see cref="StartFid"/>，不是相对浏览目录）。
        /// 仅供重解析分享时逐级定位文件用。
        /// ⚠️ 与 <see cref="RelDir"/> 的分工：RelDir 相对"下载时浏览的目录"，决定本地
        /// Dest 路径（保持既有语义不变）；ShareRelDir 相对"分享进入点"，只服务重解析。
        /// </summary>
        [DataMember(Order = 16)] public string ShareRelDir { get; set; }

        /// <summary>写入模式是否为"边下边播"（独立分片+边合并）。</summary>
        [DataMember(Order = 14)] public bool StreamPlayMode { get; set; }

        /// <summary>
        /// 是否具备"可自动重取链"的完整信息。
        /// 免转存（游客直链）任务缺 stoken/token，重启后无法重取 → 不可续传。
        /// </summary>
        public bool CanResume
        {
            get
            {
                if (string.IsNullOrEmpty(Dest) || string.IsNullOrEmpty(Fid)) return false;
                if (FromShare) return !string.IsNullOrEmpty(Token) && !string.IsNullOrEmpty(PwdId)
                                  && !string.IsNullOrEmpty(Stoken);
                return true;   // 我的网盘：只需 fid
            }
        }
    }
}
