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
using System.Xml.Serialization;
using GeZi.Core.Support;

namespace GeZi
{
    public class AppSettings
    {
        /// <summary>
        /// 单任务下载线程数（= 单文件最大并发连接）。默认 64，与 Python Flet 版一致
        /// （core 的 DOWNLOAD_THREADS 也是 64）。上限见 SegmentedDownloader.MaxRealConcurrency。
        /// </summary>
        /// <summary>
        /// 单文件并发连接数。
        /// 依据 `docs/CDN并发阈值-实测.md` 的实测拐点：
        /// ```
        ///  并发  总速中位    单连接速
        ///   64   4.00 MB/s   64.0 KB/s
        ///  128   7.98 MB/s   63.8 KB/s
        ///  256  13.94 MB/s   55.7 KB/s   ← 拐点，最快
        ///  512  13.31 MB/s   27.1 KB/s   ← 反而降（连接互相争抢，谁都涨不起来）
        /// ```
        /// 夸克 CDN **按连接限速**（单连接 ~64KB/s），所以加连接线性有效；
        /// 但 256 以上开始出现争抢（TCP 慢启动叠加 + 服务端反制），**不是越高越好**。
        /// ⚠️ 默认值只影响新用户；老配置里已有的值不会被覆盖。
        /// </summary>
        public int Threads { get; set; } = 256;

        public int ConcurrentTasks { get; set; } = 3;
        public string OutDir { get; set; } = "";
        public bool AutoClean { get; set; } = true;

        /// <summary>
        /// 是否跟随 Windows 系统代理（2026-10-11 新增）。默认 **false = 直连**。
        /// 程序历来强制直连（理由见 <see cref="NetworkOptions"/>：
        /// 本机代理软件会把夸克接口劫持到 127.0.0.1；代理会改变 TLS ClientHello
        /// 指纹，而 pan.quark.cn 的 WAF 对指纹敏感）。但"必须经代理才能上网"的
        /// 环境（公司网络、只放行代理出口的校园网）里，直连等于程序完全不可用 ——
        /// 所以补一个**显式开关**，把此前那句没有实现的 "UseSystemProxy" 注释做成真功能。
        /// ⚠️ **零回归**：`XmlSerializer` 反序列化**不执行字段初始化器**，老配置里
        ///    没有这个节点时读到 `default(bool)` = false = 直连，与升级前行为一致。
        /// ⚠️ 开关只影响**此后新建**的连接：改完要新建下载任务 / 重新登录才生效
        ///    （在途请求用的还是旧 handler）。
        /// </summary>
        public bool UseSystemProxy { get; set; }

        /// <summary>
        /// 分片/续传元数据的根目录。留空 = 与目标文件同级（`{目标}.qparts`），即原有行为。
        /// 移植自 Python Flet 版的 part_dir：可把分片集中到另一块盘（如 SSD）。
        /// </summary>
        public string PartsRoot { get; set; } = "";

        /// <summary>
        /// 是否把分片目录设为「隐藏」。移植自 Python Flet 版的 hide_parts。
        /// 只影响新建的分片目录在资源管理器里是否显示（不影响功能）。
        /// </summary>
        public bool HideParts { get; set; } = true;

        /// <summary>
        /// 曾经用过的分片根目录清单（最多 <see cref="PartsDirsMax"/> 个，最近使用的在末尾）。
        /// 移植自 Python Flet 版的 `part_dirs_known`。存在的理由很实际：
        /// 用户改过「默认分片位置」之后，**旧目录里的残留分片既扫不到、也没入口去清**，
        /// 只能一直烂在磁盘上（"分片根本没删掉"就是这么来的）。
        /// 记住历史目录后，清理/统计时可以把这些位置的残留一并处理。
        /// </summary>
        public List<string> PartsDirsKnown { get; set; } = new List<string>();

        /// <summary>历史分片目录清单的长度上限（与 Python 版一致）。</summary>
        public const int PartsDirsMax = 20;

        /// <summary>
        /// 登录 Cookie。内存里始终是明文（供 QuarkClient 使用），
        /// 序列化到磁盘时由 Save/Load 做 DPAPI 加解密，字段本身不感知。
        /// </summary>
        public string Cookie { get; set; } = "";

        public string Who { get; set; } = "";

        /// <summary>
        /// 账号头像 URL（登录时从 account/info 探测到的）。
        /// 留空 = 拿不到 → 界面回退到"昵称首字"头像。
        /// 头像链接可能带时效，每次登录/启动校验都会重新探测覆盖。
        /// </summary>
        public string AvatarUrl { get; set; } = "";

        /// <summary>
        /// **当前登录账号的档案 Key**（对应 accounts/{key}.xml 的文件名）。
        /// </summary>
        /// <remarks>
        /// 用来判断"账号列表里哪一行是当前账号"。
        /// 原来是用**整串 Cookie 做字符串比较**（`acc.Cookie == _client.CookieStr`），
        /// 但运行时 Cookie 会被重建/刷新（顺序、条目都可能变），所以永远比不中 ——
        /// 表现为「正在登录的那个账号，它的『切换』按钮居然还是可点的」（用户反馈）。
        /// 存 Key 就没有这个问题：Key 是夸克返回的 uid，稳定不变。
        /// </remarks>
        public string CurrentAccountKey { get; set; } = "";

        /// <summary>
        /// 点主窗口「关闭」按钮时的行为。见 <see cref="CloseAction"/>。
        /// </summary>
        /// <remarks>
        /// 用户要求：「第一次时会询问用户，然后可以选择最小化托盘
        /// 或者是直接关闭程序，并且在这个弹窗的右下角可以勾选此后不再提示，在设置中可以修改」。
        /// ⚠️ 默认值必须是 0 = <see cref="CloseAction.Ask"/>：
        ///    XmlSerializer 反序列化**不会执行字段初始化器**，老配置文件里没有这个节点时
        ///    属性会保持 default(T) = 0 —— 正好就是"第一次要问"。
        /// </remarks>
        public CloseAction OnCloseButton { get; set; } = CloseAction.Ask;

        /// <summary>
        /// 小窗左上角坐标。⚠️ 必须配 <see cref="MiniPosSet"/> 用：
        /// XmlSerializer 反序列化不跑字段初始化器，老配置里缺这个节点时值是 0
        /// （而不是 NaN），光看 0 分不清"没摆过"还是"就摆在 (0,0)"。
        /// </summary>
        public double MiniLeft { get; set; }
        public double MiniTop { get; set; }

        /// <summary>小窗是否已经摆过位置（false = 首次打开，贴屏幕右下角）。</summary>
        public bool MiniPosSet { get; set; }

        /// <summary>
        /// 复制到剪贴板的夸克分享链接是否**自动解析**（2026-10-08 新增）。默认开启。
        /// 关掉后程序根本不注册剪贴板监听（见 <see cref="MainWindow"/> 的 ApplyClipboardListener），
        /// 不是"监听但忽略"—— 关闭状态下零开销，也不存在"读用户剪贴板"这件事。
        /// </summary>
        public bool ClipboardAutoParse { get; set; } = true;

        /// <summary>
        /// <see cref="ClipboardAutoParse"/> 是否已经落过盘。
        /// 🚨 XmlSerializer 反序列化**不执行字段初始化器**：
        ///   老配置文件里没有 ClipboardAutoParse 节点时，属性会保持 default(bool) = false ——
        ///   也就是"老用户升级后，自动解析默认变成关闭"，与要求（默认打开）正好相反，
        ///   而且用户会觉得"你说默认开，我什么都没动怎么是关的"。
        ///   Theme 那边靠"0 = System"绕过了同样的坑，布尔类型没有等价的取巧办法，
        ///   只能额外记一个"写过没有"的标记：没写过 → 按默认值「开」处理（见 SettingsStore.Load）。
        /// </summary>
        public bool ClipboardAutoParseSet { get; set; }

        /// <summary>
        /// 外观主题（2026-10-04 新增）。Light / Dark / System。
        /// ⚠️ 默认值必须是 0 = <see cref="AppTheme.System"/>：
        ///    XmlSerializer 反序列化**不执行字段初始化器**，老配置里没有这个节点时
        ///    属性保持 default(T) = 0。这里刻意让 0 对应 System（而不是 Light），
        ///    于是**老用户升级后自动跟随系统** —— 如果系统是浅色，观感与升级前完全一致；
        ///    系统是深色则界面跟着变深，正是"跟随系统"该有的行为。
        /// </summary>
        public AppTheme Theme { get; set; } = AppTheme.System;

        // ---------------- 检查更新（2026-10-11 新增） ----------------

        /// <summary>
        /// 上次检查更新的时刻（<see cref="DateTime.Ticks"/>，本地时间）。0 = 从没查过。
        /// 启动检查要节流 —— 每次启动都打一次接口既没意义
        /// （版本不会几分钟变一次），又会在接口失效时白白拖慢启动。
        /// </summary>
        public long UpdateLastCheckTicks { get; set; }

        /// <summary>
        /// 上次查到的最新版本号（"2.5.0"）。空/null = 目前没有待提示的更新。
        /// ⚠️ 存它是为了**红点能跨会话保持**：检查只在启动时做，而「关于」窗口是后来才打开的，
        ///    打开时不能再去查一次（会卡一下 UI），只能读这里。
        /// </summary>
        public string UpdateLatestVersion { get; set; }

        /// <summary>用户点过「忽略此版本」的版本号。与 <see cref="UpdateLatestVersion"/> 相同则不再提示。</summary>
        public string UpdateIgnoredVersion { get; set; }

        /// <summary>
        /// 点「查找」时是否先弹确认。只由「此后不再提示」写入，设置页不给入口。
        /// ⚠️ 默认必须是 0 = Ask：XmlSerializer 反序列化不执行字段初始化器，
        /// 老配置缺这个节点时保持 default = 0，正好是"每次问"。
        /// </summary>
        public QrResolveMode OnQrNonQuarkLink { get; set; } = QrResolveMode.Ask;
    }

    /// <summary>点「查找」时是否先弹联网确认。</summary>
    public enum QrResolveMode
    {
        /// <summary>每次都问一次（默认）。</summary>
        Ask = 0,
        /// <summary>不再问，直接查找。</summary>
        Always = 1,
        /// <summary>不再问，也不查找。</summary>
        Never = 2,
    }

    /// <summary>主窗口「关闭」按钮的行为。</summary>
    public enum CloseAction
    {
        /// <summary>弹一次询问框（默认）。用户勾了「此后不再提示」后会被改成下面两个之一。</summary>
        Ask = 0,
        /// <summary>隐藏到托盘，下载继续跑。</summary>
        MinimizeToTray = 1,
        /// <summary>直接退出程序。</summary>
        Exit = 2,
    }

    public static class SettingsStore
    {
        private static readonly XmlSerializer Ser = new XmlSerializer(typeof(AppSettings));

        /// <summary>
        /// 配置文件读写的互斥锁。
        /// 🚨 Save 走的是"写 .tmp → 删原文件 → File.Move"，
        /// 而临时文件名是固定的（<c>FilePath + ".tmp"</c>）。**两个线程同时 Save 时**：
        ///   A 写完 tmp → B 也写同一个 tmp → A 删原文件 → A 把 tmp 移走 →
        ///   B 再去移 tmp 时文件已经没了 → 抛异常 → **B 这次设置更新被静默丢弃**。
        /// 现实中确实存在并发：设置页/登录刷新在 **UI 线程** Save，而
        /// <c>RunPendingUpdateDeletesAsync</c> 里因为用了 <c>ConfigureAwait(false)</c>，
        /// 它的 Save 落在**线程池线程**上。两者一撞就可能丢设置。
        /// </summary>
        private static readonly object IoLock = new object();

        private static string _filePath;
        private static bool _loaded;

        /// <summary>
        /// 配置文件路径：优先 exe 同目录（绿色便携，数据随 U 盘走）；
        /// 同目录不可写（装在 Program Files、"受控文件夹访问" 拦截等）时
        /// 回退到 %LOCALAPPDATA%\鸽子下载\，保证任何情况下都能存住设置。
        /// </summary>
        public static string FilePath
        {
            get
            {
                if (_loaded && !string.IsNullOrEmpty(_filePath))
                    return _filePath;

                _filePath = ResolveFilePath();
                _loaded = true;
                return _filePath;
            }
        }

        private static string ResolveFilePath()
        {
            string portable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GeZi.config.xml");

            // exe 同目录已经有一份配置文件 → 无条件沿用（避免老用户设置"搬家"到别处）
            try
            {
                if (File.Exists(portable))
                    return portable;
            }
            catch { }

            // 试着真的在 exe 同目录落一个文件，确认可写
            if (Util.IsWritable(AppDomain.CurrentDomain.BaseDirectory))
                return portable;

            string fallbackDir = Util.GetLocalAppDataDir();
            try
            {
                Directory.CreateDirectory(fallbackDir);
            }
            catch { }
            return Path.Combine(fallbackDir, "GeZi.config.xml");
        }

        /// <summary>上一次 Load/Save 是否踩到了异常（供 UI 提示用户设置未能持久化）。</summary>
        public static string LastError { get; private set; }

        /// <summary>读配置。加锁 —— 与 <see cref="Save"/> 共用同一个 .tmp 路径，不能并发。</summary>
        public static AppSettings Load()
        {
            lock (IoLock) { return LoadCore(); }
        }

        private static AppSettings LoadCore()
        {
            LastError = null;
            var s = new AppSettings();
            try
            {
                if (File.Exists(FilePath))
                {
                    using (var fs = File.OpenRead(FilePath))
                    {
                        var loaded = Ser.Deserialize(fs) as AppSettings;
                        if (loaded != null)
                            s = loaded;
                    }
                }
                else
                {
                    // 便携路径没文件时，兜底再看一眼 %LOCALAPPDATA%，实现两种位置互认
                    string alt = Path.Combine(Util.GetLocalAppDataDir(), "GeZi.config.xml");
                    if (File.Exists(alt))
                    {
                        using (var fs = File.OpenRead(alt))
                        {
                            var loaded = Ser.Deserialize(fs) as AppSettings;
                            if (loaded != null)
                                s = loaded;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }

            // 兼容旧版明文 Cookie：解出来是明文就原样用，下次 Save 时会被自动加密
            s.Cookie = SecretProtector.Unprotect(s.Cookie);
            // 并发上限与核心层的 MaxRealConcurrency 保持一致。
            // 旧配置里可能存着 512/8192 这类超出范围的值，读进来时夹到有效范围，
            // 免得 UI 上落不到任何一个合法档位。上限取自核心层常量，避免两处失配。
            if (s.Threads <= 0) s.Threads = 256;   // 与上面 Threads 的默认值保持一致
            if (s.Threads > GeZi.Core.Download.SegmentedDownloader.MaxRealConcurrency)
                s.Threads = GeZi.Core.Download.SegmentedDownloader.MaxRealConcurrency;
            if (s.ConcurrentTasks <= 0) s.ConcurrentTasks = 3;
            // 老配置（没有 ClipboardAutoParseSet 节点）→ 按默认值「开」，
            // 而不是让 default(bool) 的 false 把功能悄悄关掉。见该属性的注释。
            if (!s.ClipboardAutoParseSet) s.ClipboardAutoParse = true;
            return s;
        }

        /// <summary>写配置。加锁 —— 见 <see cref="IoLock"/> 的说明（并发写会丢设置）。</summary>
        public static void Save(AppSettings s)
        {
            lock (IoLock) { SaveCore(s); }
        }

        private static void SaveCore(AppSettings s)
        {
            LastError = null;
            if (s == null)
                return;

            string rawCookie = s.Cookie;   // 内存中始终明文，落盘前临时加密
            var tmpPath = FilePath + ".tmp";
            try
            {
                s.Cookie = SecretProtector.Protect(rawCookie);
                // 落过盘就置位：下次 Load 才能区分"用户主动关掉了"与"老配置里没这个节点"
                // （见 ClipboardAutoParseSet 的注释）。放在这里而不是各个调用点，
                // 是为了覆盖所有写盘路径，避免漏一处就退回"默认关"。
                s.ClipboardAutoParseSet = true;

                // 先写临时文件再原子替换：避免写一半断电/异常留下半截 XML，
                // 下次启动 Load 直接抛异常、用户设置全丢。
                using (var fs = File.Create(tmpPath))
                    Ser.Serialize(fs, s);

                if (File.Exists(FilePath))
                    File.Delete(FilePath);
                File.Move(tmpPath, FilePath);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            }
            finally
            {
                s.Cookie = rawCookie;   // 还原内存明文，调用方拿到的对象不被污染
            }
        }
    }
}
