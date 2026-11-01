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
using GeZi.Core.Api;
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

        /// <summary>
        /// 这个档案属于哪个网盘（<c>PanKind</c> 的整数值：Quark=0 / Uc=1 / Aliyun=2 / Xunlei=3）。
        ///
        /// <para>
        /// 老档案没有这个节点，反序列化后就是 0（= Quark）—— 加这个字段之前的版本
        /// 只存过夸克/UC 账号，默认成 Quark 正好符合当时的实际情况，不会误判。
        /// </para>
        /// </summary>
        public int PanKind { get; set; }

        /// <summary>
        /// 是否是**这家网盘**当前在用的账号（同一家只有一个为 true）。
        /// <para>
        /// 老档案没有这个节点 → 反序列化后是 false；启动时由
        /// <see cref="AccountStore.EnsurePrimaryFlags"/> 给每家补一个，
        /// 所以界面上不会出现「有账号但没有主账号」的中间态。
        /// </para>
        /// </summary>
        public bool IsPrimary { get; set; }

        /// <summary>用户自己起的备注名（留空则显示昵称）。同一家网盘两个账号昵称相同时靠它区分。</summary>
        public string Remark { get; set; } = "";

        [XmlIgnore]
        public string DisplayName => string.IsNullOrEmpty(Nick) ? Key : Nick;

        /// <summary>
        /// 档案里有加密凭据，但本机解不开（换了电脑 / 换了 Windows 用户 / 系统重装）。
        /// <para>
        /// 🚨 DPAPI 是**跟用户账户 + 机器绑定**的：换机后密文还在，但
        /// <c>SecretProtector.Unprotect</c> 只会返回空串、不抛异常。
        /// 老代码只判「Cookie 和 Key 都空才跳过」，于是这类档案照样列在账号列表里，
        /// 用户点进去才发现每个操作都失败，却看不出是「凭据废了」还是「网盘抽风」。
        /// 这个标记让界面能直接写「凭据已失效，请重新登录」。
        /// </para>
        /// </summary>
        [XmlIgnore]
        public bool CredentialUnreadable { get; set; }

        /// <summary>界面上实际显示的名字：有备注用备注，否则用昵称。</summary>
        [XmlIgnore]
        public string Caption => string.IsNullOrEmpty(Remark) ? DisplayName : Remark;
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

        /// <summary>
        /// 档案文件路径（**带网盘前缀**）。
        /// <para>
        /// 2026-10-25 改：原来是裸 <c>{key}.xml</c>，结果是不同网盘的账号挤在同一个
        /// 命名空间里 —— 实测踩过：<c>阿里云盘.xml</c> 与 <c>dove.xml</c> 都存着
        /// <c>PanKind=0</c>（夸克），在夸克账号列表里显示成两个来路不明的账号，
        /// 而且切换时可能把阿里的 Cookie 交给夸克客户端。
        /// </para>
        /// <para>
        /// 现在文件名带上 <c>PanKind</c> 整数前缀，四家的档案互不干扰；
        /// 旧的无前缀文件由 <see cref="MigrateLegacyFiles"/> 在启动时自动迁移。
        /// </para>
        /// </summary>
        /// <summary>
        /// 网盘子目录名。
        /// <para>
        /// 🚨 用户 2026-10-25 指定：<c>accounts/</c> 下按网盘建子目录，名字用
        /// <c>quark</c> / <c>uc</c> / <c>aliyun</c> / <c>xunlei</c>。
        /// </para>
        /// <para>
        /// 🚨🚨 <b>目录名就是归属的唯一权威</b>。账号文件放在哪家目录里就属于哪家，
        /// 读取时一律用目录覆盖档案内部的 <c>PanKind</c> 字段 —— 这样即使字段写错、
        /// 老档案迁移出错、昵称撞车（同一个昵称在夸克和 UC 各有一个账号），也
        /// <b>物理上不可能</b>把 A 家的 Cookie 列进 B 家的账号列表。
        /// </para>
        /// </summary>
        public static string KindDir(int panKind)
        {
            switch (panKind)
            {
                case 1: return "uc";
                case 2: return "aliyun";
                case 3: return "xunlei";
                default: return "quark";
            }
        }

        /// <summary>某家网盘的账号目录。</summary>
        public static string KindDirPath(int panKind)
            => Path.Combine(AccountsDir, KindDir(panKind));

        private static string ProfilePath(int panKind, string key)
            => Path.Combine(KindDirPath(panKind), SafeKey(key) + ".xml");

        /// <summary>旧版（根目录 + 数字前缀 <c>{kind}-{key}.xml</c>）的档案路径，只用于迁移。</summary>
        private static string LegacyPrefixedPath(int panKind, string key)
            => Path.Combine(AccountsDir, panKind + "-" + SafeKey(key) + ".xml");

        /// <summary>旧版（根目录 + 无前缀 <c>{key}.xml</c>）的档案路径，只用于读取与迁移。</summary>
        private static string LegacyProfilePath(string key)
            => Path.Combine(AccountsDir, SafeKey(key) + ".xml");

        /// <summary>判断一个文件名是不是旧版无前缀档案（新版是 <c>{数字}-{key}.xml</c>）。</summary>
        private static bool IsLegacyFileName(string fileName)
        {
            string bare = Path.GetFileNameWithoutExtension(fileName);
            int dash = bare.IndexOf('-');
            if (dash <= 0)
                return true;
            for (int i = 0; i < dash; i++)
                if (bare[i] < '0' || bare[i] > '9')
                    return true;
            return false;
        }

        /// <summary>
        /// 保存/更新一个账号档案。close 时把同一标识的旧档案覆盖（不递增），
        /// 不同账号标识不同 → 各自独立，同名也不冲突。
        /// </summary>
        public static void Save(string key, string nick, string cookie)
        {
            Save(key, nick, cookie, 0);
        }

        /// <summary>
        /// 保存/更新一个账号档案，并记下它属于哪个网盘。
        ///
        /// <para>
        /// <paramref name="panKind"/> 用 <c>PanKind</c> 的整数值。必须存 ——
        /// 否则「切换账号」时无从知道这个 Cookie 该交给哪家的客户端
        /// （夸克 Cookie 交给迅雷客户端 = 必然登录失败）。
        /// </para>
        /// </summary>
        public static void Save(string key, string nick, string cookie, int panKind)
        {
            if (string.IsNullOrEmpty(key))
                return;
            lock (Locker)
            {
                try
                {
                    Directory.CreateDirectory(AccountsDir);

                    // 🚨 同一账号不要重复建档：先按「稳定指纹」找有没有已存在的档案，
                    //    有就沿用它的 Key 覆盖写。否则每次登录（昵称/AccountSig 拿不到时
                    //    会回退成别的字符串）都会新建一份，账号列表里就会冒出好几个
                    //    「名字不同的同一个自己」—— 实测踩过三条。
                    string fp = StableCookieFingerprint(cookie);
                    if (!string.IsNullOrEmpty(fp))
                    {
                        try
                        {
                            // 🚨 只在**本家目录**里查重：跨网盘比对毫无意义，
                            //    夸克和 UC 的 Cookie 结构一模一样（都是 __pus/__puus），
                            //    跨目录比对会把两家账号误判成同一个。
                            string kindDir = KindDirPath(panKind);
                            if (Directory.Exists(kindDir))
                            {
                                foreach (var f in Directory.GetFiles(kindDir, "*.xml"))
                                {
                                    var old = LoadRaw(f);
                                    if (old == null || string.IsNullOrEmpty(old.Key))
                                        continue;
                                    string oldPlain = SecretProtector.Unprotect(old.Cookie);
                                    if (StableCookieFingerprint(oldPlain) != fp)
                                        continue;
                                    key = old.Key;      // 沿用旧档案的 Key，覆盖同一份文件
                                    break;
                                }
                            }
                        }
                        catch { }
                    }

                    Directory.CreateDirectory(KindDirPath(panKind));
                    string profilePath = ProfilePath(panKind, key);
                    // 🚨 2026-10-28：同名不同号 —— 必须在这里挡住，否则直接销毁一个账号。
                    //   夸克/UC 的 Key 来自 client.AccountSig（每号唯一），但阿里/迅雷的
                    //   Key 来自**昵称**，而迅雷网页登录拿不到真实昵称，恒为「迅雷用户」。
                    //   于是第二个迅雷账号算出来的 Key 与第一个完全相同 → 写到同一个
                    //   accounts/xunlei/迅雷用户.xml → 前一个账号被物理覆盖，用户列表里
                    //   只剩一个。这里在「该路径已存在、但指纹不同」时给 Key 加稳定短后缀。
                    try
                    {
                        if (!string.IsNullOrEmpty(fp) && File.Exists(profilePath))
                        {
                            var exist = LoadRaw(profilePath);
                            if (exist != null)
                            {
                                string existFp = StableCookieFingerprint(
                                    SecretProtector.Unprotect(exist.Cookie));
                                if (!string.IsNullOrEmpty(existFp) && existFp != fp)
                                {
                                    string suffix = ShortFingerprint(fp);
                                    string candidate = key + "-" + suffix;
                                    int n = 2;
                                    while (File.Exists(ProfilePath(panKind, candidate)))
                                    {
                                        var c = LoadRaw(ProfilePath(panKind, candidate));
                                        // 撞到的是同一个账号（同一份凭证）→ 就用它，不要新建
                                        if (c != null && StableCookieFingerprint(
                                                SecretProtector.Unprotect(c.Cookie)) == fp)
                                            break;
                                        candidate = key + "-" + suffix + "-" + n;
                                        n++;
                                    }
                                    key = candidate;
                                    profilePath = ProfilePath(panKind, key);
                                }
                            }
                        }
                    }
                    catch { }
                    var p = LoadRaw(profilePath) ?? new AccountProfile { Key = key };
                    p.Key = key;
                    if (!string.IsNullOrEmpty(nick))
                        p.Nick = nick;
                    p.Cookie = SecretProtector.Protect(cookie ?? "");
                    p.PanKind = panKind;
                    p.LastUsedUtc = DateTime.UtcNow;

                    // 主账号标记：老档案沿用原值；新档案在「本家还没有主账号」时自动补位。
                    // 这样第一次登录某家网盘就自然有主账号，不会出现空窗。
                    if (!p.IsPrimary && !HasPrimaryFlag(panKind))
                        p.IsPrimary = true;

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

                    // 迁移：同一个 key 的旧版档案（根目录下带前缀 / 无前缀）已经没用了，删掉避免重复。
                    try
                    {
                        foreach (var legacy in new[] { LegacyPrefixedPath(panKind, key), LegacyProfilePath(key) })
                        {
                            if (!File.Exists(legacy))
                                continue;
                            var old = LoadRaw(legacy);
                            if (old == null)
                                File.Delete(legacy);
                            else if (old.PanKind == panKind ||
                                     string.Equals(Path.GetFileName(legacy), panKind + "-" + SafeKey(key) + ".xml",
                                                   StringComparison.OrdinalIgnoreCase))
                                File.Delete(legacy);
                        }
                    }
                    catch { }
                }
                catch
                {
                    // 档案写失败不影响本次登录
                }
            }
        }

        /// <summary>
        /// 从 Cookie 里取「账号稳定指纹」。
        /// <para>
        /// 🚨 2026-10-25 实测：同一个夸克账号被存了三份档案（dove / 雨过晴 / 阿里云盘），
        /// 三份 Cookie 的整串哈希都不一样 —— 因为 <c>__puus</c> 是约 3 小时轮换一次的短效令牌，
        /// 每次保存都不相同。但 <c>__pus</c> / <c>__uid</c> / <c>ctoken</c> 是**长效且稳定**的。
        /// </para>
        /// <para>
        /// 所以判断「是不是同一个账号」只能看稳定字段，不能看整串 Cookie，
        /// 否则同一账号每保存一次就多一份档案，账号列表里就会冒出好几个名字不同的自己。
        /// </para>
        /// </summary>
        public static string StableCookieFingerprint(string cookie)
        {
            if (string.IsNullOrEmpty(cookie))
                return "";
            string pus = null, uid = null, ctoken = null;
            foreach (var seg in cookie.Split(';'))
            {
                var s = seg.Trim();
                int eq = s.IndexOf('=');
                if (eq <= 0)
                    continue;
                string k = s.Substring(0, eq).Trim();
                string v = s.Substring(eq + 1).Trim();
                if (k == "__pus") pus = v;
                else if (k == "__uid") uid = v;
                else if (k == "ctoken") ctoken = v;
            }
            if (!string.IsNullOrEmpty(pus))
                return "pus:" + pus;
            if (!string.IsNullOrEmpty(uid))
                return "uid:" + uid;
            if (!string.IsNullOrEmpty(ctoken))
                return "ctk:" + ctoken;
            // 阿里/迅雷这类不是 Cookie 的凭证，整串本身就是身份，直接用它。
            return "raw:" + cookie;
        }

        /// <summary>
        /// 把稳定指纹压成 8 位十六进制短码，用于「同名不同号」时区分档案文件名。
        /// <para>
        /// 用 FNV-1a 而不是 SHA：这里只要**稳定且够散**，不承担任何安全职责
        /// （指纹本身已经是明文凭证的派生值，短码只用来避免文件名相撞）。
        /// </para>
        /// </summary>
        private static string ShortFingerprint(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint))
                return "0";
            unchecked
            {
                uint h = 2166136261u;
                foreach (char ch in fingerprint)
                {
                    h ^= ch;
                    h *= 16777619u;
                }
                return h.ToString("x8");
            }
        }

        /// <summary>
        /// 用「接口返回的真实昵称」修正档案里存的昵称。
        /// <para>
        /// 🚨 2026-10-25 实测发现的历史脏数据：<c>accounts/uc/101051704151.xml</c> 里
        /// <c>Nick</c> 写的是「雨过晴」（那是夸克账号的昵称），而 UC 接口实测返回的是
        /// <c>dove</c>；夸克那份 <c>百云.xml</c> 反而被写成了 <c>dove</c>。原因是早期
        /// 「当前登录昵称」只有一个全局槽位，四家轮流登录时互相覆盖，脏值又被 Save 固化。
        /// </para>
        /// <para>
        /// 目录改造只保证「A 家的 Cookie 不会列进 B 家」，管不了昵称写错。所以每次
        /// 登录态实测通过（<c>ValidatePanSessionsAsync</c>）都拿真实昵称回写一次，
        /// 脏数据会自愈，用户不用手动点「切换」。
        /// </para>
        /// </summary>
        /// <param name="panKind">网盘（只看这一家的子目录，绝不跨家比对）。</param>
        /// <param name="cookie">刚刚验证通过的那份凭证。</param>
        /// <param name="nick">接口返回的真实昵称；空值不覆盖（接口偶尔不给昵称）。</param>
        public static void UpdateNickByCookie(int panKind, string cookie, string nick)
        {
            if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(nick))
                return;
            string fp = StableCookieFingerprint(cookie);
            if (string.IsNullOrEmpty(fp))
                return;
            lock (Locker)
            {
                try
                {
                    string kindDir = KindDirPath(panKind);
                    if (!Directory.Exists(kindDir))
                        return;
                    foreach (var f in Directory.GetFiles(kindDir, "*.xml"))
                    {
                        var p = LoadRaw(f);
                        if (p == null || string.IsNullOrEmpty(p.Key))
                            continue;
                        string plain = SecretProtector.Unprotect(p.Cookie);
                        if (StableCookieFingerprint(plain) != fp)
                            continue;
                        if (string.Equals(p.Nick, nick, StringComparison.Ordinal))
                            return;   // 已经是对的，不重写文件
                        p.Nick = nick;
                        p.PanKind = panKind;
                        string tmp = f + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            Ser.Serialize(fs, p);
                            fs.Flush(true);
                        }
                        File.Replace(tmp, f, null);
                        return;
                    }
                }
                catch
                {
                    // 昵称修正失败不影响登录
                }
            }
        }

        /// <summary>读取全部账号档案（四家全读），按「最近使用」倒序（最近用的在前）。</summary>
        public static List<AccountProfile> LoadAll()
        {
            return LoadAll(null);
        }

        /// <summary>
        /// 读取账号档案，按「最近使用」倒序。
        /// <para>
        /// 🚨🚨 <paramref name="only"/> 有值时<b>只读那一家网盘的子目录</b>
        /// （<c>accounts/quark/</c>、<c>accounts/uc/</c> …），这是「账号绝不串」的
        /// 根本保证：另一家的文件连打开都不会打开，谈不上误判。
        /// </para>
        /// <para>
        /// 读到的每一条都用<b>目录名覆盖</b> <see cref="AccountProfile.PanKind"/>，
        /// 档案内部那个字段只作参考 —— 老档案缺这个节点时默认是 0（夸克），
        /// 若拿它当真就会把 UC / 阿里的账号显示进夸克列表。
        /// </para>
        /// </summary>
        public static List<AccountProfile> LoadAll(PanKind? only)
        {
            var list = new List<AccountProfile>();
            lock (Locker)
            {
                try
                {
                    // 先把根目录下遗留的旧版档案搬进各自子目录，再统一按目录读。
                    MigrateLegacyFiles();

                    var kinds = new List<int>();
                    if (only.HasValue)
                        kinds.Add((int)only.Value);
                    else
                    {
                        kinds.Add((int)PanKind.Quark);
                        kinds.Add((int)PanKind.Uc);
                        kinds.Add((int)PanKind.Aliyun);
                        kinds.Add((int)PanKind.Xunlei);
                    }

                    foreach (int k in kinds)
                    {
                        string dir = KindDirPath(k);
                        if (!Directory.Exists(dir))
                            continue;
                        foreach (var f in Directory.GetFiles(dir, "*.xml"))
                        {
                            var p = LoadRaw(f);
                            if (p == null)
                                continue;
                            // 密文存在但解不开 = DPAPI 换了机器/用户。先记下来，别当成「本来就没凭据」。
                            bool hadBlob = !string.IsNullOrEmpty(p.Cookie);
                            p.Cookie = SecretProtector.Unprotect(p.Cookie);
                            if (hadBlob && string.IsNullOrEmpty(p.Cookie))
                                p.CredentialUnreadable = true;
                            if (string.IsNullOrEmpty(p.Cookie) && string.IsNullOrEmpty(p.Key))
                                continue;
                            p.PanKind = k;   // 目录即权威
                            list.Add(p);
                        }
                    }
                }
                catch
                {
                    // 目录读不到就返回已收集的
                }
            }
            // 去重：同一个 (网盘, key) 只留最近使用的那份（迁移过程中可能出现两份）。
            // 再按**账号稳定指纹**去重：同一账号被存成多个不同 key 的档案是历史遗留
            // （实测「dove / 雨过晴 / 阿里云盘」三条其实是同一个夸克账号，
            // 只有 __puus 不同 —— 那是约 3 小时轮换一次的短效令牌）。
            var dedup = new List<AccountProfile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenCred = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in list.OrderByDescending(x => x.LastUsedUtc))
            {
                string id = p.PanKind + "\u0001" + (p.Key ?? "");
                if (!seen.Add(id))
                    continue;
                string credFp = p.PanKind + "\u0001" + StableCookieFingerprint(p.Cookie);
                if (!string.IsNullOrEmpty(p.Cookie) && !seenCred.Add(credFp))
                    continue;
                dedup.Add(p);
            }
            dedup.Sort((a, b) => b.LastUsedUtc.CompareTo(a.LastUsedUtc));
            return dedup;
        }

        /// <summary>
        /// 把根目录下遗留的旧版档案搬进各自网盘子目录。
        /// <para>
        /// 两种旧形态：
        /// <list type="bullet">
        /// <item><c>{kind}-{key}.xml</c> —— 数字前缀就是当时的归属，直接采信；</item>
        /// <item><c>{key}.xml</c> —— 没有前缀，只能读档案里的 <c>PanKind</c>（老档案缺省 0）。</item>
        /// </list>
        /// 只在目标不存在时改名；失败一律忽略（档案是锦上添花，下次启动再试）。
        /// 调用时持有 <see cref="Locker"/>。
        /// </para>
        /// </summary>
        private static void MigrateLegacyFiles()
        {
            string root = AccountsDir;
            if (!Directory.Exists(root))
                return;
            string[] files;
            try { files = Directory.GetFiles(root, "*.xml"); }
            catch { return; }

            foreach (var f in files)
            {
                var p = LoadRaw(f);
                if (p == null)
                    continue;

                string bare = Path.GetFileNameWithoutExtension(f);
                int kind = p.PanKind;
                string key = p.Key ?? "";

                if (!IsLegacyFileName(f))
                {
                    // {kind}-{key}.xml：前缀就是权威归属
                    int dash = bare.IndexOf('-');
                    int parsed;
                    if (int.TryParse(bare.Substring(0, dash), out parsed) &&
                        parsed >= (int)PanKind.Quark && parsed <= (int)PanKind.Xunlei)
                        kind = parsed;
                    if (string.IsNullOrEmpty(key))
                        key = bare.Substring(dash + 1);
                }
                else if (string.IsNullOrEmpty(key))
                {
                    key = bare;
                }

                if (string.IsNullOrEmpty(key))
                    continue;

                try
                {
                    string target = ProfilePath(kind, key);
                    if (string.Equals(target, f, StringComparison.OrdinalIgnoreCase))
                        continue;
                    Directory.CreateDirectory(KindDirPath(kind));
                    if (File.Exists(target))
                        File.Delete(f);            // 新档案已存在 → 旧的是重复的，直接清掉
                    else
                        File.Move(f, target);      // 旧档案搬进子目录
                }
                catch
                {
                    // 迁移失败不影响使用，下次启动再试
                }
            }
        }

        /// <summary>按（网盘, 标识）取一个档案（含已解密的 cookie）。找不到返回 null。</summary>
        public static AccountProfile Get(int panKind, string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;
            lock (Locker)
            {
                var p = LoadRaw(ProfilePath(panKind, key));
                if (p == null)
                    return null;
                p.Cookie = SecretProtector.Unprotect(p.Cookie);
                return p;
            }
        }

        /// <summary>删除某个账号档案。手动操作专用 —— 程序不会自己删档案。</summary>
        public static bool Remove(int panKind, string key)
        {
            if (string.IsNullOrEmpty(key))
                return false;
            lock (Locker)
            {
                bool any = false;
                bool wasPrimary = false;
                try
                {
                    var p = ProfilePath(panKind, key);
                    if (File.Exists(p))
                    {
                        // 删之前先看它是不是主账号：删掉主账号必须立刻补位，
                        // 否则会留下「还有账号、但一条都不是主账号」的不可预期状态
                        // （云析 AccountDirectory.kt:86 同款做法）。
                        var old = LoadRaw(p);
                        wasPrimary = old != null && old.IsPrimary;
                        File.Delete(p);
                        any = true;
                    }
                    // 顺带清掉根目录下同名的旧版档案，避免下次启动又被迁移回来。
                    foreach (var legacy in new[] { LegacyPrefixedPath(panKind, key), LegacyProfilePath(key) })
                    {
                        if (!File.Exists(legacy))
                            continue;
                        var old = LoadRaw(legacy);
                        if (old == null || old.PanKind == panKind)
                        {
                            File.Delete(legacy);
                            any = true;
                        }
                    }
                }
                catch { }
                if (any && wasPrimary)
                    EnsurePrimaryForKind(panKind);
                return any;
            }
        }


        /// <summary>把某个档案设为本家网盘的**主账号**（同家其余档案一律取消标记）。</summary>
        public static void SetPrimary(int panKind, string key)
        {
            if (string.IsNullOrEmpty(key))
                return;
            lock (Locker)
            {
                try
                {
                    string kindDir = KindDirPath(panKind);
                    if (!Directory.Exists(kindDir))
                        return;
                    foreach (var f in Directory.GetFiles(kindDir, "*.xml"))
                    {
                        var o = LoadRaw(f);
                        if (o == null || string.IsNullOrEmpty(o.Key))
                            continue;
                        bool want = string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase);
                        if (o.IsPrimary == want)
                            continue;
                        o.IsPrimary = want;
                        WriteRaw(f, o);
                    }
                }
                catch { }
            }
        }


        /// <summary>
        /// 取本家网盘的**主账号**。老档案全都没标记时回退「最近使用」的那一个，
        /// 保证调用方永远拿到一个可用的账号（不会因为升级后没标记而变成"没有账号"）。
        /// </summary>
        public static AccountProfile GetPrimary(int panKind)
        {
            List<AccountProfile> all;
            try { all = LoadAll((PanKind)panKind); }
            catch { return null; }
            if (all == null || all.Count == 0)
                return null;
            foreach (var p in all)
            {
                if (p.IsPrimary)
                    return p;
            }
            return all[0];
        }


        /// <summary>给某个账号设置备注名（传空串 = 恢复显示昵称）。</summary>
        public static void SetRemark(int panKind, string key, string remark)
        {
            if (string.IsNullOrEmpty(key))
                return;
            lock (Locker)
            {
                try
                {
                    string path = ProfilePath(panKind, key);
                    var p = LoadRaw(path);
                    if (p == null)
                        return;
                    p.Remark = (remark ?? "").Trim();
                    WriteRaw(path, p);
                }
                catch { }
            }
        }


        /// <summary>
        /// 启动时补一次主账号标记：每家网盘只要有账号却没有主账号，
        /// 就把「最近使用」的那一个补成主账号。老配置文件升级上来必须有这一步，
        /// 否则界面会出现「有账号但没一个是在用的」。
        /// </summary>
        public static void EnsurePrimaryFlags()
        {
            foreach (PanKind k in new[] { PanKind.Quark, PanKind.Uc, PanKind.Aliyun, PanKind.Xunlei })
            {
                try { EnsurePrimaryForKind((int)k); }
                catch { }
            }
        }


        /// <summary>本家网盘是否已经有主账号（读盘扫描，调用时持有 <see cref="Locker"/>）。</summary>
        private static bool HasPrimaryFlag(int panKind)
        {
            try
            {
                string kindDir = KindDirPath(panKind);
                if (!Directory.Exists(kindDir))
                    return false;
                foreach (var f in Directory.GetFiles(kindDir, "*.xml"))
                {
                    var o = LoadRaw(f);
                    if (o != null && o.IsPrimary)
                        return true;
                }
            }
            catch { }
            return false;
        }


        /// <summary>某家网盘没有主账号时补一个（调用时持有 <see cref="Locker"/>，可重入）。</summary>
        private static void EnsurePrimaryForKind(int panKind)
        {
            List<AccountProfile> all;
            try { all = LoadAll((PanKind)panKind); }
            catch { return; }
            if (all == null || all.Count == 0)
                return;
            foreach (var p in all)
            {
                if (p.IsPrimary)
                    return;
            }
            // LoadAll 已按最近使用倒序 → 第一个就是最该当主账号的
            SetPrimary(panKind, all[0].Key);
        }


        /// <summary>把档案原样写回（Cookie 仍是加密态，不经过加解密）。</summary>
        private static bool WriteRaw(string path, AccountProfile p)
        {
            try
            {
                string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    Ser.Serialize(fs, p);
                    fs.Flush(true);
                }
                if (File.Exists(path))
                    File.Replace(tmp, path, null);
                else
                    File.Move(tmp, path);
                return true;
            }
            catch { return false; }
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
