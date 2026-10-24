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
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using GeZi.Core.Support;

namespace GeZi
{
    /// <summary>
    /// 一个「本机保存的账号」档案。
    ///
    /// 移植自 Python Flet 版的账号档案机制：登录成功后把 cookie / 昵称 / 最近使用时间
    /// 各存一份到 accounts/ 目录，之后就能在列表里一键切换，不用重新扫码。
    /// </summary>
    public class AccountProfile
    {
        /// <summary>账号稳定标识（夸克接口返回的 uid/account_id）。同一账号固定，用于去重与文件名。</summary>
        public string Key { get; set; } = "";

        /// <summary>显示昵称。取不到时回退到账号标识。</summary>
        public string Nick { get; set; } = "";

        /// <summary>登录 Cookie（明文，落盘前由 Store 用 DPAPI 加密）。</summary>
        public string Cookie { get; set; } = "";

        /// <summary>最近使用时间（UTC）。用于按「最近使用」倒序排列。</summary>
        public DateTime LastUsedUtc { get; set; }

        [XmlIgnore]
        public string DisplayName => string.IsNullOrEmpty(Nick) ? Key : Nick;
    }

    /// <summary>
    /// 多账号档案的读写。设计上刻意「失败不抛」——账号档案属于锦上添花的功能，
    /// 任何一个文件读坏了都不应该影响主流程（登录、下载）。
    ///
    /// 存储布局（与 Flet 版保持一致，方便两边互认）：
    /// <code>
    ///   {数据目录}/accounts/{key}.xml      每个账号一份档案
    ///   {数据目录}/GeZi.config.xml         默认指针 = 当前账号（SettingsStore 负责）
    /// </code>
    /// 档案放子目录避免把数据目录糊满一堆文件。
    /// </summary>
    public static class AccountStore
    {
        private static readonly XmlSerializer Ser = new XmlSerializer(typeof(AccountProfile));
        private static readonly object Locker = new object();

        /// <summary>账号档案目录。与 SettingsStore 同根，保证便携/回退策略一致。</summary>
        public static string AccountsDir
        {
            get
            {
                string root = Path.GetDirectoryName(SettingsStore.FilePath) ?? AppDomain.CurrentDomain.BaseDirectory;
                return Path.Combine(root, "accounts");
            }
        }

        /// <summary>把账号标识转成安全的文件名片段（去掉非法字符并限长），对应 Flet 的 _account_safe。</summary>
        public static string SafeKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                return "account";
            var sb = new StringBuilder(key.Length);
            foreach (char c in key)
            {
                if (c < 0x20 || c == '\\' || c == '/' || c == ':' || c == '*' ||
                    c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
                    continue;
                sb.Append(c);
            }
            var s = sb.ToString().Trim().Trim('.', ' ');
            if (s.Length > 40)
                s = s.Substring(0, 40);
            return s.Length == 0 ? "account" : s;
        }

        private static string ProfilePath(string key)
            => Path.Combine(AccountsDir, SafeKey(key) + ".xml");

        /// <summary>
        /// 保存/更新一个账号档案。close 时把同一标识的旧档案覆盖（不递增），
        /// 不同账号标识不同 → 各自独立，同名也不冲突。
        /// </summary>
        public static void Save(string key, string nick, string cookie)
        {
            if (string.IsNullOrEmpty(key))
                return;
            lock (Locker)
            {
                try
                {
                    Directory.CreateDirectory(AccountsDir);
                    var p = LoadRaw(ProfilePath(key)) ?? new AccountProfile { Key = key };
                    p.Key = key;
                    if (!string.IsNullOrEmpty(nick))
                        p.Nick = nick;
                    p.Cookie = SecretProtector.Protect(cookie ?? "");
                    p.LastUsedUtc = DateTime.UtcNow;

                    string profilePath = ProfilePath(key);
                    string tmp = profilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        Ser.Serialize(fs, p);
                        fs.Flush(true);
                    }
                    if (File.Exists(profilePath))
                        File.Replace(tmp, profilePath, null);
                    else
                        File.Move(tmp, profilePath);
                }
                catch
                {
                    // 档案写失败不影响本次登录
                }
            }
        }

        /// <summary>读取全部账号档案，按「最近使用」倒序（最近用的在前）。</summary>
        public static List<AccountProfile> LoadAll()
        {
            var list = new List<AccountProfile>();
            lock (Locker)
            {
                string dir = AccountsDir;
                if (!Directory.Exists(dir))
                    return list;
                try
                {
                    foreach (var f in Directory.GetFiles(dir, "*.xml"))
                    {
                        var p = LoadRaw(f);
                        if (p == null)
                            continue;
                        p.Cookie = SecretProtector.Unprotect(p.Cookie);
                        if (string.IsNullOrEmpty(p.Cookie) && string.IsNullOrEmpty(p.Key))
                            continue;
                        list.Add(p);
                    }
                }
                catch
                {
                    // 目录读不到就返回已收集的
                }
            }
            list.Sort((a, b) => b.LastUsedUtc.CompareTo(a.LastUsedUtc));
            return list;
        }

        /// <summary>按标识取一个档案（含已解密的 cookie）。找不到返回 null。</summary>
        public static AccountProfile Get(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;
            lock (Locker)
            {
                var p = LoadRaw(ProfilePath(key));
                if (p == null)
                    return null;
                p.Cookie = SecretProtector.Unprotect(p.Cookie);
                return p;
            }
        }

        /// <summary>删除某个账号档案。手动操作专用 —— 程序不会自己删档案。</summary>
        public static bool Remove(string key)
        {
            if (string.IsNullOrEmpty(key))
                return false;
            lock (Locker)
            {
                try
                {
                    var p = ProfilePath(key);
                    if (File.Exists(p))
                    {
                        File.Delete(p);
                        return true;
                    }
                }
                catch { }
                return false;
            }
        }

        private static AccountProfile LoadRaw(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;
                using (var fs = File.OpenRead(path))
                    return Ser.Deserialize(fs) as AccountProfile;
            }
            catch
            {
                return null;
            }
        }
    }
}
