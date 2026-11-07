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
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using GeZi.Core.Support;
using GeZi.Providers;
using GeZi.Providers.Quark;
using GeZi.Providers.Uc;
using GeZi.Providers.Aliyun;
using GeZi.Providers.Xunlei;
using GeZi.Providers.Shared;
using GeZi.Providers.Qr;

using GeZi.Services;
namespace GeZi
{
    /// <summary>一条下载历史记录。字段与 Python 版 records 对齐。</summary>
    [DataContract]
    public class HistoryRecord
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        [DataMember(Order = 2)] public string Size { get; set; }     // 显示用文本（"1.23 GB"），与 Python 一致
        [DataMember(Order = 3)] public string Path { get; set; }     // 本地完整路径，供"打开/定位"
        [DataMember(Order = 4)] public string Status { get; set; }   // 完成 / 失败 / 已取消 ...
        [DataMember(Order = 5)] public string Time { get; set; }     // "yyyy-MM-dd HH:mm:ss"
    }

    /// <summary>
    /// 一条「解析过的分享」记录（按 <c>h</c> 唤出的历史面板用）。
    /// 那条是**下载**历史（记的是本地文件路径与大小），
    /// 这条记的是**分享**（pwd_id / 提取码），两者字段没有交集，混在一起两边都要判空。
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

        /// <summary>解析时刻。"yyyy-MM-dd HH:mm:ss"，与下载历史同一个格式。</summary>
        [DataMember(Order = 6)] public string Time { get; set; }

        /// <summary>
        /// 用户手动「置顶」的标记：置顶的始终排在最前，**不受后续解析的影响**。
        /// 那样只是把它挪到最前面一次，而且显示的时间
        /// 会变成谎话（明明是上周解析的，却写着"今天"）。
        /// </summary>
        [DataMember(Order = 7)] public bool Pinned { get; set; }

        /// <summary>
        /// 这条分享属于哪家网盘（<c>GeZi.Core.Api.PanKind</c> 的整数值，默认 0 = 夸克）。
        /// 历史面板要用它决定「失效校验」走哪套接口 —— 拿夸克客户端去探阿里分享
        /// 会一律抛异常，把好链接标成「已失效」。旧文件没有这个字段时反序列化为 0（夸克），
        /// 与加入这个字段之前的实际行为一致。
        /// </summary>
        [DataMember(Order = 8)] public int PanKind { get; set; }

        /// <summary>
        /// 上次校验出来的**有效性**结论，落盘保存：<c>0</c> = 还不知道，<c>1</c> = 有效，<c>2</c> = 已失效。
        /// <para>
        /// 🚨 2026-10-26 用户反馈「为什么只显示一次哪个链接失效了，后面再打开就不显示了」。
        /// 根因：原来结论只存在内存里（<c>_historyProbedAt</c> 只记时刻、不记结论），
        /// 面板一重建就是一批全新的行，标签默认 Collapsed；而 10 分钟缓存又让校验被跳过，
        /// 于是「第一次探测标红 → 关掉再开 → 什么都不显示」。
        /// 结论落盘之后，重开面板（甚至重启软件）都能立刻看到上次的判定。
        /// </para>
        /// 旧 state.json 没有这两个字段 → 反序列化成 0 / null，等价于「还没探过」，行为不变。
        /// </summary>
        [DataMember(Order = 9)] public int Validity { get; set; }

        /// <summary>上面那次结论的时刻（"yyyy-MM-dd HH:mm:ss"），用来做跨会话的缓存有效期。</summary>
        [DataMember(Order = 10)] public string ValidityAt { get; set; }
    }

    /// <summary>
    /// 下载历史 + 未完成任务记录 + 解析历史的持久化。
    /// 移植自 Python Flet 版的 `records`（历史，上限 200）与 `flet_pending`（未完成任务）。
    /// 两者原先都存在 core 的 state 里（同一个 state.json），这里合并成一个文件，
    /// 放在与配置同目录（`SettingsStore.FilePath` 的同级 `GeZi.state.json`），
    /// 保证便携（跟 exe 走）与可写回退（%LOCALAPPDATA%）行为一致。
    /// JSON 用 `DataContractJsonSerializer`（.NET Framework 自带，零 NuGet）。
    /// ⚠️ 不能用 `JavaScriptSerializer`：它所在的 System.Web.Extensions 依赖 System.Web，
    /// 而 **WPF 项目引用 System.Web 会导致 XAML 编译器找不到程序集**（实测 MC1000）。
    /// </summary>
    internal static class HistoryStore
    {
        private const int MaxRecords = 200;   // 与 Python Flet 版一致

        /// <summary>
        /// 解析历史的上限。比下载历史小得多：它是"最近打开过什么"，翻久了没意义，
        /// 而且每条都要在面板里画出来。
        /// </summary>
        private const int MaxShareHistory = 50;

        /// <summary>
        /// 整个 state.json 的读改写互斥锁。
        /// <para>
        /// 🚨 2026-10-28 补：这是项目里**唯一**没有锁的持久化点
        /// （<c>SettingsStore.IoLock</c>、<c>AccountStore.Locker</c>、<c>LogStore.Gate</c> 都有）。
        /// </para>
        /// <para>
        /// 竞态双方是真实存在的：
        /// ① <c>MainWindow.Pending.SyncPending</c> 在 UI 线程调 <c>SetPending</c>；
        /// ② <c>MainWindow.History</c> 里分享有效性校验跑在
        ///   <c>await Task.WhenAll(...).ConfigureAwait(false)</c> 之后的**线程池线程**上，
        ///   调 <c>SetShareValidity</c>。
        /// </para>
        /// <para>
        /// 每个方法都是「Load 整份 → 改一块 → Save 整份」，两份整表没有锁就会
        /// 后写者用**陈旧快照**覆盖前者 —— 表现就是「刚保存的未完成任务凭空消失」。
        /// </para>
        /// <para>
        /// 只锁**改**操作，不锁纯读（<c>GetHistory</c>/<c>GetPending</c> 等）：
        /// 读路径每次 Load 都从磁盘新建一个 State 对象，不存在共享可变对象；
        /// 而 Save 用 GUID 临时文件 + File.Replace 原子替换，读到的要么是旧版要么是新版。
        /// </para>
        /// </summary>
        private static readonly object Gate = new object();

        /// <summary>
        /// 可选的日志出口（由 <c>MainWindow</c> 启动时挂上）。
        /// 历史记录读坏属于「用户该知道」的事，但这个类本身不依赖任何 UI，
        /// 所以用可空回调，没挂就当哑巴。
        /// </summary>
        public static Action<string> LogSink { get; set; }

        [DataContract]
        private sealed class State
        {
            [DataMember(Order = 1)] public List<HistoryRecord> Records { get; set; }
            [DataMember(Order = 2)] public List<PendingTask> Pending { get; set; }
            /// <summary>解析历史。⚠️ 老 state.json 里没有这个字段 → 反序列化为 null，Load 里补空表。</summary>
            [DataMember(Order = 3)] public List<ShareHistoryEntry> Shares { get; set; }
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
                            if (s.Records == null) s.Records = new List<HistoryRecord>();
                            if (s.Pending == null) s.Pending = new List<PendingTask>();
                            if (s.Shares == null) s.Shares = new List<ShareHistoryEntry>();
                            // 状态文件里存的 Url/Token/Stoken/Passcode 是 DPAPI 密文；
                            // 内存中恢复成明文，UI 与续传逻辑无需感知加密细节。
                            DecryptSecrets(s);
                            return s;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 🚨 原来这里是裸 catch {}：文件读坏了照样返回空 State，
                // 而下一次任何写入（AddHistory / SetPending / AddShareHistory 全是
                // Load→改→Save）都会把这份空状态写回去，**原始记录被永久覆盖**，
                // 未完成任务的分片从此变成没人认领的孤儿。
                //
                // 所以读失败时先把坏文件改名留档，再降级成空状态。
                QuarantineBrokenState(ex);
            }
            return new State
            {
                Records = new List<HistoryRecord>(),
                Pending = new List<PendingTask>(),
                Shares = new List<ShareHistoryEntry>(),
            };
        }

        /// <summary>
        /// 把读不动的 <c>GeZi.state.json</c> 改名留档（<c>.broken-yyyyMMddHHmmss</c>）。
        /// 绝不覆盖已有的留档，失败就静默放弃 —— 这里本身已经在异常路径上，
        /// 再抛一次只会把问题放大成「连界面都打不开」。
        /// </summary>
        private static void QuarantineBrokenState(Exception cause)
        {
            try
            {
                string path = StatePath;
                if (!File.Exists(path)) return;

                string stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                string target = path + ".broken-" + stamp;
                int n = 1;
                while (File.Exists(target))
                {
                    target = path + ".broken-" + stamp + "-" + n;
                    n++;
                }
                File.Move(path, target);

                // 留档成功 → 之后写的都是新文件，不会覆盖用户原有记录。
                // 原始异常文本不写进日志（可能含路径），只记一句人话。
                LogSink?.Invoke("历史记录文件读不动（" + cause.GetType().Name +
                                "），已留档为 " + Path.GetFileName(target) + "，本次以空记录启动。");
            }
            catch
            {
                // 留档失败（文件被占用/无权限）：仍然降级成空状态，
                // 至少保证程序能启动；用户原文件没被删。
            }
        }

        private static void Save(State s)
        {
            string tmp = null;
            Action restore = null;
            try
            {
                string path = StatePath;
                // 唯一临时文件 + Flush(true) + 一次 Replace：
                // 旧实现“删原文件再 Move”，删除与移动之间断电会同时丢掉旧记录和新记录。
                tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                // 落盘前把敏感字段临时换成密文；finally 里立刻恢复内存明文。
                restore = EncryptSecrets(s);
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var ser = new DataContractJsonSerializer(typeof(State));
                    ser.WriteObject(fs, s);
                    fs.Flush(true);
                }
                if (File.Exists(path))
                    File.Replace(tmp, path, null);
                else
                    File.Move(tmp, path);
            }
            catch
            {
                try { if (!string.IsNullOrEmpty(tmp)) File.Delete(tmp); } catch { }
            }
            finally
            {
                if (restore != null)
                {
                    try { restore(); } catch { }
                }
            }
        }

        // ---------------- 敏感字段加解密 ----------------
        //
        // 状态文件里 Url/Token/Stoken/Passcode 是可以直接换链的凭证，等价于密码；
        // 用与 Cookie 相同的 DPAPI（CurrentUser）加密。内存中保持明文，
        // 只在序列化那一刻临时替换，序列化结束立即还原。

        /// <summary>把密文字段解回明文。旧版明文（无 enc: 前缀）原样保留。</summary>
        private static void DecryptSecrets(State s)
        {
            if (s == null) return;
            if (s.Pending != null)
            {
                foreach (var p in s.Pending)
                {
                    if (p == null) continue;
                    p.Url = SecretProtector.UnprotectField(p.Url);
                    p.Token = SecretProtector.UnprotectField(p.Token);
                    p.Stoken = SecretProtector.UnprotectField(p.Stoken);
                    p.Passcode = SecretProtector.UnprotectField(p.Passcode);
                }
            }
            if (s.Shares != null)
            {
                foreach (var h in s.Shares)
                {
                    if (h == null) continue;
                    h.Passcode = SecretProtector.UnprotectField(h.Passcode);
                }
            }
        }

        /// <summary>
        /// 把敏感字段临时加密，返回“还原内存明文”的动作。
        /// 加密失败时字段置空 —— fail-closed：宁可不保存这条记录，也不落明文。
        /// </summary>
        private static Action EncryptSecrets(State s)
        {
            var restore = new List<Action>();
            if (s == null)
                return () => { };

            if (s.Pending != null)
            {
                foreach (var p in s.Pending)
                {
                    if (p == null) continue;
                    var item = p;
                    string url = item.Url, token = item.Token, stoken = item.Stoken, passcode = item.Passcode;
                    restore.Add(() =>
                    {
                        item.Url = url;
                        item.Token = token;
                        item.Stoken = stoken;
                        item.Passcode = passcode;
                    });
                    item.Url = SecureField(url);
                    item.Token = SecureField(token);
                    item.Stoken = SecureField(stoken);
                    item.Passcode = SecureField(passcode);
                }
            }

            if (s.Shares != null)
            {
                foreach (var h in s.Shares)
                {
                    if (h == null) continue;
                    var item = h;
                    string passcode = item.Passcode;
                    restore.Add(() => { item.Passcode = passcode; });
                    item.Passcode = SecureField(passcode);
                }
            }

            return () =>
            {
                foreach (var r in restore)
                {
                    try { r(); } catch { }
                }
            };
        }

        private static string SecureField(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return plain;
            try { return SecretProtector.ProtectField(plain); }
            catch { return ""; }   // DPAPI 不可用 → 不写明文
        }

        // ---------------- 历史记录 ----------------

        public static List<HistoryRecord> GetHistory()
        {
            var s = Load();
            var list = new List<HistoryRecord>(s.Records);
            list.Reverse();   // 新的在前
            return list;
        }

        /// <summary>追加一条历史。超出上限时丢最旧的。</summary>
        public static void AddHistory(string name, string size, string path, string status)
        {
            lock (Gate)
            {
                var s = Load();
                s.Records.Add(new HistoryRecord
                {
                    Name = name,
                    Size = size,
                    Path = path,
                    Status = status,
                    Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                });
                if (s.Records.Count > MaxRecords)
                    s.Records.RemoveRange(0, s.Records.Count - MaxRecords);
                Save(s);
            }
        }

        public static void ClearHistory()
        {
            lock (Gate)
            {
                var s = Load();
                s.Records.Clear();
                Save(s);
            }
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
            string name, bool isFolder, int panKind = 0)
        {
            if (string.IsNullOrEmpty(pwdId)) return;
            try
            {
                lock (Gate)
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
                    PanKind = panKind,
                    Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                });
                if (s.Shares.Count > MaxShareHistory)
                    s.Shares.RemoveRange(0, s.Shares.Count - MaxShareHistory);
                Save(s);
                }
            }
            catch { }
        }

        /// <summary>置顶 / 取消置顶某条解析历史。</summary>
        public static void SetSharePinned(string pwdId, bool pinned)
        {
            if (string.IsNullOrEmpty(pwdId)) return;
            try
            {
                lock (Gate)
                {
                    var s = Load();
                    foreach (var e in s.Shares)
                    {
                        if (string.Equals(e.PwdId, pwdId, StringComparison.Ordinal))
                            e.Pinned = pinned;
                    }
                    Save(s);
                }
            }
            catch { }
        }

        /// <summary>
        /// 把一批「分享是否有效」的校验结论落盘（0 未知 / 1 有效 / 2 失效）。
        /// <para>
        /// 一次探测可能同时得出好几条结论，这里**只写一次盘** —— 逐条 Save 会让
        /// state.json 在一个面板打开期间被反复重写（每条一次全量序列化）。
        /// </para>
        /// 找不到对应 pwd_id 的（面板期间被删了）直接忽略。
        /// </summary>
        public static void SetShareValidity(IDictionary<string, int> results)
        {
            if (results == null || results.Count == 0) return;
            try
            {
                lock (Gate)
                {
                var s = Load();
                string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                bool changed = false;
                foreach (var e in s.Shares)
                {
                    int v;
                    if (e == null || e.PwdId == null) continue;
                    if (!results.TryGetValue(e.PwdId, out v)) continue;
                    if (e.Validity == v && !string.IsNullOrEmpty(e.ValidityAt)) continue;
                    e.Validity = v;
                    e.ValidityAt = now;
                    changed = true;
                }
                if (changed) Save(s);
                }
            }
            catch { }
        }

        /// <summary>删掉某条解析历史（按 pwd_id）。</summary>
        public static void RemoveShareHistory(string pwdId)
        {
            if (string.IsNullOrEmpty(pwdId)) return;
            try
            {
                lock (Gate)
                {
                    var s = Load();
                    for (int i = s.Shares.Count - 1; i >= 0; i--)
                    {
                        if (string.Equals(s.Shares[i].PwdId, pwdId, StringComparison.Ordinal))
                            s.Shares.RemoveAt(i);
                    }
                    Save(s);
                }
            }
            catch { }
        }
        public static void ClearShareHistory()
        {
            lock (Gate)
            {
                var s = Load();
                s.Shares.Clear();
                Save(s);
            }
        }

        // ---------------- 未完成任务 ----------------

        public static List<PendingTask> GetPending()
        {
            return Load().Pending;
        }

        /// <summary>整体覆盖未完成任务列表（由 UI 在任务状态变化时调用）。</summary>
        public static void SetPending(List<PendingTask> pending)
        {
            lock (Gate)
            {
                var s = Load();
                s.Pending = pending ?? new List<PendingTask>();
                Save(s);
            }
        }

        public static void ClearPending()
        {
            lock (Gate)
            {
                var s = Load();
                s.Pending.Clear();
                Save(s);
            }
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
    /// <summary>
    /// 一条未完成任务的**来源类型** —— 决定续传时该怎么重新取直链。
    /// <para>
    /// 🚨 2026-11-03 新增：原来只有 FromShare 一个 bool，却要表达三种语义，
    /// 结果阿里/迅雷分享被当成「需要分享凭证」，续传必然失败（用户实测）。
    /// 拆成枚举后，每个分支的取链方式一目了然。
    /// </para>
    /// </summary>
    public enum PendingSource
    {
        /// <summary>我的网盘里的自有文件：按 Fid 走 OwnDownloadLinks，不需要任何分享凭证。</summary>
        OwnDrive = 0,

        /// <summary>
        /// 分享免转存（不写入网盘，直接从分享取链）。
        /// <para>夸克/UC 需要 pwd_id + stoken + share_fid_token 三件套。</para>
        /// </summary>
        ShareNoSave = 1,

        /// <summary>
        /// 分享已转存到自己网盘（夸克/UC 的「转存→取直链」）。
        /// <para>文件在网盘里，取链用 SavedFid（没有则退回 Fid），不需要 stoken。</para>
        /// </summary>
        ShareSaved = 2,

        /// <summary>
        /// 阿里/迅雷分享：provider 内部自己完成「转存→取直链」。
        /// <para>
        /// 🚨 这一类的记录里 PwdId/Stoken **天然为空**，
        /// 所以判据**绝不能**要求它们 —— 这是 2026-11-02 那批续传失败的根因。
        /// </para>
        /// </summary>
        ShareProvider = 3,
    }

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

        /// <summary>内容哈希；重启后续传仍用同一组期望值校验。</summary>
        [DataMember(Order = 17)] public string Md5 { get; set; }
        [DataMember(Order = 18)] public string Sha1 { get; set; }
        [DataMember(Order = 19)] public string Sha256 { get; set; }

        /// <summary>写入模式是否为"边下边播"（独立分片+边合并）。</summary>
        [DataMember(Order = 14)] public bool StreamPlayMode { get; set; }

        /// <summary>
        /// 该任务来自哪个网盘。旧记录没有这个字段 → 反序列化得到 0 = 夸克，
        /// 正好是历史数据的真实来源，所以天然向后兼容。
        /// <para>续传时据此决定用哪个客户端重取直链，避免拿夸克客户端去请求 UC/阿里/迅雷。</para>
        /// </summary>
        [DataMember(Order = 20)] public int PanKindValue { get; set; }

        // ==================== 2026-11-03：续传信息补全（Order > 20） ====================
        // 🚨 为什么必须加这几项：原来只有 FromShare 一个 bool，却要表达**三种**语义 ——
        //    ① 夸克/UC 免转存分享（要 pwd_id + stoken + share_fid_token）；
        //    ② 夸克/UC 转存后取链（按新 fid 取，不需要 stoken）；
        //    ③ 阿里/迅雷分享（provider 内部自己转存，**不需要 share_id**）。
        //    一个 bool 分不清，续传就会判错分支 → 必然失败。
        //    而且**原始分享链接从来没存过**，PwdId 一丢就永远找不回来。
        // ⚠️ 全部 Order > 20 且旧文件缺这些字段时反序列化为默认值，向后兼容。
        // ⚠️ FromShare 必须**继续正确赋值**：它同时是「旧代码读新记录」的兼容桥梁。

        /// <summary>
        /// 这条任务的**来源类型**（见 <see cref="PendingSource"/>）。
        /// <para>
        /// 旧记录没有这个字段 → 反序列化得 0（OwnDrive）。
        /// 所以读取时**必须**走 <see cref="ResolveSource"/>，它会结合 FromShare/PanKindValue 推导，
        /// 不能直接用这个字段。
        /// </para>
        /// </summary>
        [DataMember(Order = 21)] public int Source { get; set; }

        /// <summary>
        /// 原始分享链接（完整 URL，含提取码若有）。
        /// <para>
        /// 🚨 这是「百分百能续传」的关键兜底：PwdId / stoken 都会过期或丢失，
        /// 但只要还有**原始链接**，就能重新解析分享、重新拿全套凭证。
        /// 旧记录里没有 → 空串，此时只能靠 PwdId。
        /// </para>
        /// </summary>
        [DataMember(Order = 22)] public string ShareUrl { get; set; }

        /// <summary>
        /// 转存后的文件 fid（分享已转存到自己网盘时才有值）。
        /// <para>
        /// 夸克/UC 走「转存→取直链」时，真正的文件在用户网盘里，
        /// 取链要用**这个** fid 而不是分享里的原 fid。
        /// </para>
        /// </summary>
        [DataMember(Order = 23)] public string SavedFid { get; set; }

        /// <summary>
        /// 上一次续传失败的真实原因（人话，直接显示在界面上）。
        /// <para>
        /// 原来失败原因只在日志里闪一下，用户重开软件后什么都看不到，
        /// 只知道「这条不能续传」却不知道为什么。落盘后界面能直接告诉他。
        /// </para>
        /// </summary>
        [DataMember(Order = 24)] public string ResumeHint { get; set; }

        /// <summary>
        /// 任务创建时刻（本地时间 ticks，UTC 基准的 DateTime.Ticks + 本地偏移已折算）。
        ///
        /// <para>
        /// 2026-11-05 新增：续传界面按「今天 / 昨天 / 更早」分组用。
        /// 记的是**任务入队时刻**，不是「最后一次保存快照的时刻」——
        /// 后者在 .geziresume 的 <c>saved</c> 行里，一个下了三天的任务它永远显示今天。
        /// </para>
        /// <para>
        /// 用 <c>long</c> 而不是 <c>DateTime</c>：DataContractJsonSerializer 对
        /// DateTime 的序列化格式在不同运行时下不稳定，ticks 是纯数字最可靠。
        /// 旧记录缺这个字段 → 反序列化得 0，界面归到「更早」，不会误判成今天。
        /// </para>
        /// </summary>
        [DataMember(Order = 25)] public long CreatedAtTicks { get; set; }

        /// <summary>按本地日期返回分组标签。判据只在 Core 的 PendingDateGroup 里有一份。</summary>
        public string DateGroupLabel()
        {
            return GeZi.Core.Support.PendingDateGroup.Label(CreatedAtTicks);
        }

        /// <summary>分组排序键。判据只在 Core 的 PendingDateGroup 里有一份。</summary>
        public int DateGroupOrder()
        {
            return GeZi.Core.Support.PendingDateGroup.Order(CreatedAtTicks);
        }

        /// <summary>
        /// 解析出**真正**的来源类型（兼容旧记录）。
        /// <para>
        /// 旧记录 <see cref="Source"/> 恒为 0，必须从 FromShare + PanKindValue 推导：
        /// <list type="bullet">
        /// <item>FromShare=false → 我的网盘（自有文件）；</item>
        /// <item>FromShare=true + 阿里/迅雷 → provider 自己转存；</item>
        /// <item>FromShare=true + 夸克/UC + 有 SavedFid → 已转存；</item>
        /// <item>FromShare=true + 夸克/UC + 无 SavedFid → 免转存。</item>
        /// </list>
        /// </para>
        /// </summary>
        public PendingSource ResolveSource()
        {
            // 新记录直接采信（>0 说明是明确写过的）
            if (Source > 0)
                return (PendingSource)Source;

            if (!FromShare)
                return PendingSource.OwnDrive;

            if (PanKindValue == 2 || PanKindValue == 3)
                return PendingSource.ShareProvider;

            return string.IsNullOrEmpty(SavedFid)
                ? PendingSource.ShareNoSave
                : PendingSource.ShareSaved;
        }

        /// <summary>
        /// 是否具备"可自动重取链"的完整信息。
        /// 免转存（游客直链）任务缺 stoken/token，重启后无法重取 → 不可续传。
        /// </summary>
        public bool CanResume
        {
            get
            {
                // 判据已抽到 GeZi.Core 的 ResumePolicy（纯函数，自检可直接覆盖）。
                // 🚨 2026-11-02 撤销 2026-11-01 的 Size<=0 禁选 —— 那两个理由都是错的：
                //   ① 「AllocateThreads 按大小分配，Size=0 分不到合理值」：
                //      错。ThreadAllocator 对未知大小会退回平分，n==1 时直接给满。
                //   ② 「续传时 TotalBytes 与 0 对不上」：
                //      错。那个 totalBytes 来自引擎自己 GetFileSize() 探测，与本字段无关。
                //   ⇒ 禁选纯属自伤：用户「刚点下载、还没写盘就重启」时 Size 必然是 0，
                //      于是被挡住、标「需重新解析」，用户以为续传坏了，实际是被我拦了。
                // 必须把 PanKindValue 一起传进去：阿里/迅雷的分享任务不需要 stoken
                //（取链在 provider 内部完成），用统一判据会把它们误判成不可续传。
                return Check().Can;
            }
        }

        /// <summary>
        /// 续传可行性**完整检查**（能不能 + 为什么 + 怎么办）。
        /// <para>界面直接用它拿到「原因」和「下一步」，不用再自己拼文案。</para>
        /// </summary>
        public GeZi.Core.Models.ResumePolicy.ResumeCheckResult Check()
        {
            return GeZi.Core.Models.ResumePolicy.Check(
                Dest, Fid, FromShare, Token, PwdId, Stoken, PanKindValue, ShareUrl);
        }
    }
}
