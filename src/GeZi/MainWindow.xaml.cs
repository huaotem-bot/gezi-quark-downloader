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
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;   // ToggleButton（续传列表的真复选框）
using System.Windows.Data;                  // Binding（同上）
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;   // RenderTargetBitmap（主题切换的双快照）
using System.Windows.Threading;
using GeZi.Core.Api;
using GeZi.Core.Download;
using GeZi.Core.Models;
using GeZi.Core.Support;
using GeZi.Core.Update;
using System.Text.RegularExpressions;

// 说明：MainWindow 里既有 XAML 生成的字段，也有代码动态构建的窗口（如续传询问、
// 诊断面板）。动态构建需要 System.Windows.Controls 下的 Grid/Button/ListBox 等，
// 所以这里必须 using —— WPF 项目不会自动带入。
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using SelectionMode = System.Windows.Controls.SelectionMode;
using MessageBox = System.Windows.MessageBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Run = System.Windows.Documents.Run;
using TextTrimming = System.Windows.TextTrimming;
// 加载态用到的类型与已有名称冲突，统一取别名
using DiagClock = System.Diagnostics.Stopwatch;
using ShapePath = System.Windows.Shapes.Path;   // 避免与 System.IO.Path 撞名

namespace GeZi
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<ShareFileItem> _files = new ObservableCollection<ShareFileItem>();
        private readonly ObservableCollection<TaskItem> _tasks = new ObservableCollection<TaskItem>();

        // ─────────────────────────────────────────────────────────────
        // 任务分类过滤（参考 Motrix 的任务列表分类）
        // 4 个分类**共用同一个 _tasks 集合**，只切换 ICollectionView 的过滤器 ——
        // 这样任务状态变化时不需要重建列表，也不会出现"4 份列表副本状态不同步"。
        // 分类：全部 / 进行中（Queued+Downloading+Paused）/ 失败（Failed+Cancelled）/ 已完成
        // ─────────────────────────────────────────────────────────────
        private string _taskFilter = "all";
        private System.ComponentModel.ICollectionView _taskView;
        private readonly List<string> _pathList = new List<string>();
        private readonly Stack<string> _fidStack = new Stack<string>();

        // ---------------- 托盘 / 小窗 / 退出（2026-10-03） ----------------
        /// <summary>托盘图标（常驻通知区）。</summary>
        private TrayIcon _tray;
        /// <summary>悬浮小窗（按需创建，关掉后置空）。</summary>
        private MiniWindow _mini;
        /// <summary>小窗拖动中的坐标缓存（只在关闭时落盘一次，避免拖动时疯狂写配置）。</summary>
        private double _miniLeft, _miniTop;
        /// <summary>是否正在走"真退出"流程。关闭事件里靠它放行，避免被再次拦下。</summary>
        private bool _exiting;
        /// <summary>托盘气泡只提示一次（首次隐藏时），之后不再打扰。</summary>
        private bool _trayHintShown;

        /// <summary>
        /// 一个模式的导航快照。
        /// </summary>
        /// <remarks>
        /// 用户反馈：「解析完链接之后点击我的网盘再返回这里，发现还需要重新解析，
        /// 没有显示解析后的文件」。根因是 <see cref="OnModeChanged"/> **无条件**调 ResetNav()，
        /// 把 _files/_fidStack/_pathList 全清了，切回来时分享状态已经没了。
        /// 现在两种模式各自留一份快照，来回切换时**原样恢复**（连列表内容都不重新请求）。
        /// </remarks>
        private sealed class NavSnapshot
        {
            public List<ShareFileItem> Files = new List<ShareFileItem>();
            public string CurFid = "0";
            public List<string> FidStack = new List<string>();
            public List<string> PathList = new List<string>();
            public string PwdId;
            public string Stoken;
            public string StartFid;   // 分享进入目录（切模式再切回来不能丢）
        }

        /// <summary>「分享」模式与「我的网盘」模式各自的导航快照。</summary>
        private readonly NavSnapshot _shareNav = new NavSnapshot();
        private readonly NavSnapshot _driveNav = new NavSnapshot();

        /// <summary>文件列表的筛选视图 + 当前筛选词（空 = 不筛选）。</summary>
        private System.ComponentModel.ICollectionView _filesView;
        private string _fileFilter = "";

        /// <summary>
        /// 是否正在加载目录。仅供皮肤层区分「加载中」与「真的空」两种状态 ——
        /// 不影响任何下载/请求逻辑。见 UpdateEmptyStates()。
        /// </summary>
        private volatile bool _loadingDir;

        /// <summary>
        /// 正在用代码给设置控件赋值（LoadSettingsToUi）时为 true。
        /// 挡住控件赋值引发的事件回调，避免启动时把默认值写回、覆盖用户配置。
        /// </summary>
        private bool _applyingSettings;

        private readonly List<string> _pendingCleanupFids = new List<string>();
        private readonly object _cleanupLock = new object();
        private QuarkClient _client;

        /// <summary>
        /// 启动时那次自动登录的任务（可能是 null：没有保存的 Cookie 时不会启动）。
        /// 续传要用它来「等登录态就绪」——见 <see cref="EnsureClientForResumeAsync"/>。
        /// </summary>
        private System.Threading.Tasks.Task _startupLoginTask;
        private string _pwdId;
        private string _stoken;
        /// <summary>当前分享的进入目录 fid（普通分享 "0"，带 fid 的链接指向子目录）。
        /// 自动重解析分享时从这里开始逐级展开。</summary>
        private string _startFid = "0";
        /// <summary>当前分享的提取码。下载器回调跑在线程池线程，
        /// 不能读 PassBox.Text，重解析分享（换 stoken）只能从字段拿。</summary>
        private string _passcode = "";
        private string _curFid = "0";
        private AppSettings _settings;
        private DownloadScheduler _scheduler;

        /// <summary>直链短时缓存（TTL 600s），避免重复取链触发夸克限流。见 LinkCache。</summary>
        private readonly LinkCache _linkCache = new LinkCache();

        // ---------------- 「入队」可终止（2026-10-11） ----------------
        // 用户反馈：「点击下载后一直卡在『入队中』」。
        // 「入队」这一步其实是串起来的多个网络动作（递归展开目录 → 转存/取链，
        // 转存还要轮询任务状态最长 300 秒），全过程没有任何取消入口 ——
        // 一旦某一步卡住 / 接口变慢，界面就永远停在「入队中」，用户只能干等甚至杀进程。
        // 入队期间把「下载」按钮就地变成**可点**的「入队中（点此终止）」，
        // 再点一次即触发下面的取消源；流程各处插入检查点，逐步退回。
        // ⚠️ 取消是「立刻响应」级别：正在飞的那一个 HTTP 请求会等它自己返回（通常几秒），
        // 但后续步骤不会再开始 —— 该粒度足以解决"卡住/等太久"的问题，且不动网络层。

        /// <summary>当前「入队」流程的取消源。非 null = 正在入队（此时再点下载按钮 = 终止）。</summary>
        private CancellationTokenSource _enqueueCts;

        /// <summary>正在显示「入队中」的那个按钮（终止时用来改文案）。</summary>
        private Button _enqueueBusyBtn;

        // ---------------- 剪贴板自动解析（2026-10-08） ----------------

        /// <summary>
        /// 剪贴板监听注册在哪个窗口句柄上（IntPtr.Zero = 没注册）。
        /// 注册是**绑在原生句柄上**的，
        /// 而窗口隐藏→恢复之间句柄有可能被重建（见 <see cref="AttachWindowHooks"/>）。
        /// 只记"已注册/未注册"，句柄一换就会误判成"已注册"而不再重挂。
        /// </summary>
        private IntPtr _clipboardHookHwnd;

        /// <summary>
        /// 「跨进程激活消息钩子 + 剪贴板监听」已经挂在哪个窗口句柄上
        /// （IntPtr.Zero = 还没挂）。见 <see cref="AttachWindowHooks"/>。
        /// </summary>
        private IntPtr _hooksOnHwnd;

        /// <summary>
        /// 上一次因剪贴板自动解析处理过的分享 ID。
        /// 一次"复制"并不保证只来一条 <c>WM_CLIPBOARDUPDATE</c> ——
        /// 有些程序（浏览器扩展、剪贴板管理器）会把同一份内容分多次写剪贴板，
        /// 于是通知会连发好几条。不去重的话每次都要重发换 stoken + 拉目录两个请求，
        /// 白等一次，还有被夸克风控盯上的风险。
        /// </summary>
        private string _clipboardLastPwdId;

        /// <summary>
        /// 从剪贴板文本里圈出候选 URL 的正则。
        /// 只负责"把看起来像 URL 的片段切出来"，**不判断是不是夸克** ——
        /// 域名那一步交给 <see cref="IsQuarkHost"/>。排除集与「粘贴进链接框」时用的
        /// 保持同一份（<see cref="OnLinkBoxPasting"/>），免得两处对"URL 到哪结束"的
        /// 理解不一致。
        /// </summary>
        private static readonly Regex ClipboardUrlRx =
            new Regex(@"https?://[^\s""'<>）】\]]+", RegexOptions.Compiled);

        /// <summary>
        /// 剪贴板文本超过这么多字符就**不看**（约 2 万字符）。
        /// 「随手复制的分享链接」撑死几百字符；一份几十万字的剪贴板内容不可能是它，
        /// 而在 UI 线程上为它跑一遍正则纯属自找卡顿。
        /// </summary>
        private const int MaxClipboardScanChars = 20000;

        private bool ShareMode => ShareModeRadio.IsChecked == true;

        // ---------------- 单次处理文件数保护 ----------------
        // 沿用早期原型定下的 MAX_FILES / CONFIRM_THRESHOLD。
        // 为什么要有这道阀：一次误操作（比如选中一个几万文件的分享目录、或"全选"）
        // 会连带触发几万次取链请求 + 几万个下载任务的目录创建，轻则把自己账号
        // 送进服务端风控，重则把目标盘写爆。Python 版对此是"硬拒 + 超量确认"两道，
        // 这里保持同样语义，不做自动截断 —— 静默丢掉用户选的文件比直接报错更糟。
        // 上限与确认阈值都只在这里定义一处，
        // 不要再在别处写死 1000，否则改一处漏一处就会出不一致的行为。
        private const int MaxFiles = 1000;          // 单次处理文件数上限（硬拒）
        private const int ConfirmThreshold = 100;   // 超过该数量时先弹确认（软提醒）

        public MainWindow()
        {
            InitializeComponent();
            FileList.ItemsSource = _files;
            // 当前目录的筛选视图（名称包含关键字才显示）。空关键字 = 全部显示。
            _filesView = System.Windows.Data.CollectionViewSource.GetDefaultView(_files);
            _filesView.Filter = o =>
            {
                var f = o as ShareFileItem;
                if (f == null) return false;
                var w = _fileFilter ?? "";
                return w.Length == 0 ||
                       (f.Name ?? "").IndexOf(w, StringComparison.CurrentCultureIgnoreCase) >= 0;
            };
            // 用 ICollectionView 包一层：分类 Tab 切换时只 Refresh 过滤器，不动数据源
            _taskView = System.Windows.Data.CollectionViewSource.GetDefaultView(_tasks);
            _taskView.Filter = TaskFilterPredicate;
            TaskList.ItemsSource = _taskView;
            ShareModeRadio.IsChecked = true;

            // 侧栏导航（ListBox）与内容区（TabControl）的初始对齐。
            // 顺序很重要：先建好账号卡初始态，再让选中项落到第 0 页 —— 这样
            // 后续任何 Tabs.SelectedIndex 赋值都会经由 OnTabsSelectionChanged
            // 反过来把 NavList 同步到位，两边永远不会不同步。
            UpdateAccountCard(null, 0, 0);
            NavList.SelectedIndex = 0;
            Tabs.SelectedIndex = 0;
            Tabs.SelectionChanged += OnTabsSelectionChanged;

            // 窗口句柄一（重）建好，就把「跨进程激活消息钩子 + 剪贴板监听」挂上去。
            // ⚠️ 用 SourceInitialized 而不是 Loaded：它正是"HWND 刚刚存在"的时刻
            //    （项目里 ScanQrWindow 给窗口加圆角也是用这个事件），而且**只要新建了
            //    句柄就会再触发一次** —— 窗口隐藏到托盘、再恢复时如果句柄被重建，
            //    这里会跟着重挂，注册不会静默失效。见 AttachWindowHooks。
            SourceInitialized += (s, e) => AttachWindowHooks();

            // 空状态提示：集合有变化就同步一次可见性。
            // 不改动任何业务逻辑，只是"列表为空时显示插画"这一层皮肤。
            // ⚠️ 走**合并**版（RequestUpdateEmptyStates）：分页铺列表时会连着变很多次，
            //    每次都在里面整表数一遍是 O(n²)，几千项的目录会直接卡死。
            _files.CollectionChanged += (s, e) => RequestUpdateEmptyStates();

            // 文件列表快滑到底部时继续加载下一页（分页逻辑见 LoadDirAsync / LoadMoreItems）。
            // 用 AddHandler 挂 ScrollChanged：不必去可视树里翻 ListBox 模板生成的那个
            // ScrollViewer —— 事件里的 OriginalSource 就是它。
            FileList.AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent,
                new System.Windows.Controls.ScrollChangedEventHandler(OnFileListScrollChanged));
            _tasks.CollectionChanged += (s, e) =>
            {
                UpdateEmptyStates();
                // 任务增删后同步分类 Tab 的计数（"共 N 条"）
                UpdateTaskFilterCounts();
                // 任务被移出列表时必须同步「未完成任务」记录，
                // 否则删掉的任务下次启动还会被当成可续传任务弹出来。
                // 放在这里而不是各个删除点，是为了覆盖所有移除路径。
                // SyncPending 内部有签名节流，集合频繁变化也不会反复写盘。
                SyncPending();
            };
            // 筛选视图变化也要刷新（筛选后可能一条都不剩）
            _filesView.CollectionChanged += (s, e) => UpdateEmptyStates();

            // 日志攒批刷新器：把高频日志合并成一次 UI 写入（见 Log 的注释）。
            // 用 Background 优先级 —— 让进度条等更要紧的 UI 更新先跑。
            _logFlushTimer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(LogFlushMs),
                System.Windows.Threading.DispatcherPriority.Background,
                (s, e) => FlushLogs(),
                Dispatcher);
            _logFlushTimer.Start();

            // ---- 保活：下载期间阻止系统空闲睡眠 ----
            // 任务**卡死**时（worker 卡在掐不断的调用里 → `Task.WhenAll` 永不返回）
            // 状态会永远停在 Downloading，而进度回调也可能就此停摆 ——
            // 挂在回调上等于**永不复查**，电脑会整夜不睡。
            // 独立定时器每 30 秒复查一次，卡死也能在"无进展超时"之后释放保活。
            // 用户明确要求「不需要单独按钮」→ 自动跟随任务状态。
            _sleepGuardTimer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromSeconds(SleepGuardTickSec),
                System.Windows.Threading.DispatcherPriority.Background,
                (s, e) => RefreshSleepGuard(),
                Dispatcher);
            _sleepGuardTimer.Start();
            // 把保活的状态变化写进日志 —— 用户能看到"为什么电脑没睡"（不做开关，但保留知情权）。
            SleepGuard.Log = Log;

            // 「总」进度日志：把所有任务**汇总成一行**，每 10 秒最多一条。
            // 原来核心层是**逐任务**每 5 秒打一行
            // 「运行中: 0 B/s，活跃连接 49/49，分片进度 0/49，命中 IP …」——
            // 11 个任务就是每分钟 130+ 行，把"开始/完成/失败"这些真正有用的行全冲掉了
            // （用户：「日志里全是这玩意根本没有用，普通用户也不会看」）。
            // 现在核心那条默认关掉（`SegmentedDownloader.PerTaskDiagLog`），
            // 这里改成**一行总量**；而且内容没变就不重复打（空闲时不会刷屏）。
            _progressLogTimer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromSeconds(ProgressLogSec),
                System.Windows.Threading.DispatcherPriority.Background,
                (s2, e2) => LogAggregateProgress(),
                Dispatcher);
            _progressLogTimer.Start();

            // 定时刷容量（用户要求）。与保活心跳同频 5 分钟 —— 下载会改变已用空间，
            // 光靠启动那一次拉取，卡片上的容量会一直是旧的。
            _capacityTimer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromSeconds(CapacityRefreshSec),
                System.Windows.Threading.DispatcherPriority.Background,
                (s3, e3) => OnCapacityTick(),
                Dispatcher);
            _capacityTimer.Start();

            _settings = SettingsStore.Load();

            // 确定一个「真正可写」的下载目录：已保存的 → 真实下载目录 → 本地应用数据目录 → 程序目录
            string resolved = null;
            string[] candidates =
            {
                _settings.OutDir,
                Path.Combine(Util.GetDownloadsFolder(), "鸽子下载"),
                Util.GetLocalAppDataDir(),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads"),
            };
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c) || !Util.IsWritable(c))
                    continue;
                resolved = c;
                break;
            }
            if (resolved == null)
                resolved = Util.GetLocalAppDataDir();

            if (!string.Equals(resolved, _settings.OutDir, StringComparison.OrdinalIgnoreCase))
            {
                _settings.OutDir = resolved;
                SettingsStore.Save(_settings);
                Log("下载目录不可写（可能是 Windows「受控文件夹访问」拦截），已改用: " + resolved);
            }

            _scheduler = new DownloadScheduler(_settings.ConcurrentTasks);
            _scheduler.Log = LogFromWorker;
            _scheduler.Diagnostics = OnDiagnostics;
            LoadSettingsToUi();

            // 检查更新：把配置交给 UpdateService。
            // 同时用**上次落盘的版本号**恢复红点状态 —— 红点必须跨会话保持，
            // 否则用户重启一次、红点就没了（而本次可能因节流根本没去查）。
            UpdateService.Attach(_settings);

            // 加载完成后再提示未完成任务（此时窗口已就绪，弹窗才有 Owner）
            Loaded += (s, e) =>
            {
                // 确保主窗真的显示在最前。此前的现象是"托盘有图标但主窗不弹出"，
                // 根因在单实例激活逻辑（见 App.ActivateExistingInstance），这里再兜一道：
                // 即便窗口因任何原因处于隐藏/最小化，也强制恢复。
                try
                {
                    if (!IsVisible) Show();
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    Activate();
                }
                catch { }

                // 先启动分片清扫（后台线程），再弹续传询问 ——
                // 清扫只回收"超过阈值没被碰过"的孤儿分片，不会动正在续传的，
                // 但顺序上仍让清扫先跑，避免把刚被判定为待续的分片误伤（mtime 很新，实际不会）。
                StartPartsSweep();

                // 把续传询问推迟到 UI 完全渲染之后（Idle 优先级）再弹：
                // 若直接在 Loaded 里 ShowDialog()，主窗还没画出来就先弹模态框，
                // 用户会误以为"程序没打开"（尤其弹框被主窗或别的窗口挡住时）。
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { ShowPendingOnStartup(); } catch { }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                // 启动检查更新。
                // 同样推到 ApplicationIdle：先让主窗画出来，后台的事晚一点无所谓。
                // ⚠️ 全程静默 —— 网络不通/接口改版都不弹窗（理由见 UpdateService 的注释）。
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try { await UpdateService.RunStartupCheckAsync(_settings, OnUpdateFound); }
                    catch { }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                // 窗口圆角（用户反馈「整个软件窗口四个角太尖了，微微加一些圆角」）。
                // ⚠️ **必须放在 Loaded 里**：它要拿窗口句柄（HWND），
                // 而 HWND 到 Loaded 才存在。曾经写在构造函数里 →
                // `WindowInteropHelper.Handle` 是 IntPtr.Zero → 函数静默返回，
                // 表现为"加了代码但窗口依然没有圆角"（连日志都没打）。
                ApplyWindowRoundedCorners();

                // 托盘图标（用户要求加后台功能）。
                // ⚠️ 也必须在 Loaded 里：NotifyIcon 需要一个窗口句柄来收任务栏消息，
                //    而 WPF 窗口句柄到 Loaded 才存在。
                InitTray();

                // 「再点一次 exe 把窗口拉起来」的消息钩子、以及
                // 「剪贴板自动解析」的监听，都不在这里挂了 —— 它俩都绑在窗口句柄上，
                // 统一交给 AttachWindowHooks（订阅在构造函数里，走 SourceInitialized）。
            };

            if (!string.IsNullOrEmpty(_settings.Cookie))
            {
                Log("正在自动登录…");
                // 记下这个任务：续传前要等它（见 EnsureClientForResumeAsync）。
                _startupLoginTask = ApplyLoginAsync(_settings.Cookie);
            }
            else
            {
                Log("就绪。未登录：可用「分享链接」模式下载公开分享，或到「登录」页扫码。");
            }

            // 首屏必须主动刷一次空状态。
            // 两个空状态（文件列表、任务列表）在 XAML 里初始 Visibility=Collapsed，
            // 只靠 LoadDirAsync/集合变化去点亮。而启动时若未登录、又没粘贴链接，
            // LoadDirAsync 根本不会跑 → 列表区里两个状态都不显示，只剩一片灰底
            // （用户截图反馈的"这里空这么一大块"）。
            UpdateEmptyStates();

            // 合并后的「暂停全部/恢复全部」按钮，Content 是代码里
            // 生成的（XAML 只留了一个空 Button）。不在这里刷一次的话，启动后它就是个
            // **空白方块**（用户截图反馈"这里怎么回事儿"）。
            UpdatePauseAllButton(force: true);
        }

        // ---------------- 窗口圆角 ----------------
        // WPF 自己没法给顶层窗口加圆角（除非开 AllowsTransparency，那会关掉硬件加速、
        // 影响最大化行为，对一个持续刷新的下载界面不划算）。
        // Windows 11 的 DWM 提供了原生圆角，一次调用即可，且由系统裁剪内容、不留锯齿。

        /// <summary>
        /// 给窗口加系统级圆角（实现在 <see cref="DialogChrome.ApplyRoundedCorners"/>，
        /// 小窗共用同一份）。Win11 生效；Win10 上静默忽略 —— 圆角只是观感，
        /// 不该因为它让程序出问题，也**不写日志**（成功是常态，写进日志只是噪音）。
        /// </summary>
        private void ApplyWindowRoundedCorners()
        {
            DialogChrome.ApplyRoundedCorners(this);
        }

        /// <summary>
        /// 主窗消息钩子，处理两条消息：
        ///   · <see cref="App.WmActivateMain"/>：另一个实例请我把窗口拉起来；
        ///   · <see cref="NativeMethods.WM_CLIPBOARDUPDATE"/>：剪贴板内容变了
        ///     （只在设置里开着「剪贴板自动解析」时才注册，见 <see cref="ApplyClipboardListener"/>）。
        /// 为什么让**收到消息的旧进程**自己激活，而不是让新进程去 SetForegroundWindow：
        /// Windows 的前台锁定不允许后台进程抢焦点，新进程调用往往只让任务栏闪一下。
        /// 旧进程是被"用户操作"触发的，激活才有效。
        /// </summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == App.WmActivateMain)
            {
                RestoreFromTray();
                handled = true;
            }
            else if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
            {
                // ⚠️ 不置 handled：这是系统的广播性质通知，本程序只是订阅者之一，
                //    没有理由把它吃掉（也避免将来其它组件想接同一条消息时被拦）。
                OnClipboardChanged();
            }
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _logFlushTimer?.Stop(); } catch { }
            try { _sleepGuardTimer?.Stop(); } catch { }
            // 释放保活。其实进程退出时线程级状态会自动释放，这里显式写更清楚，
            // 也避免"关窗后进程还活着"的路径（如托盘常驻）把保活留着。
            try { SleepGuard.Release(); } catch { }
            try { _client?.StopKeepAlive(); } catch { }
            // ⚠️ 托盘图标必须释放：不释放的话进程退出后图标会留在通知区，
            //    鼠标划过去才消失（最经典的托盘 bug）。
            try { _tray?.Dispose(); _tray = null; } catch { }
            try { _mini?.Shutdown(); } catch { }
            // 摘掉剪贴板监听：进程退出时系统也会回收这条注册，但显式摘掉更干净，
            // 且与 SourceInitialized 里的注册成对出现（窗口在托盘常驻期间一直持有它）。
            try
            {
                if (_clipboardHookHwnd != IntPtr.Zero)
                {
                    NativeMethods.RemoveClipboardFormatListener(_clipboardHookHwnd);
                    _clipboardHookHwnd = IntPtr.Zero;
                }
            }
            catch { }
            base.OnClosed(e);
        }

        // ---------------- 基础 ----------------

        /// <summary>待刷入 LogBox 的日志行（跨线程入队，UI 定时器出队）。</summary>
        private readonly System.Collections.Concurrent.ConcurrentQueue<LogLine> _logQueue =
            new System.Collections.Concurrent.ConcurrentQueue<LogLine>();

        /// <summary>
        /// 一行日志：文本 + 是否"专业级"。
        /// 日志默认只显示日常必要信息（下载/解析/报错的结果），
        /// 过程细节（目录加载、缓存命中、连接分配、内部状态…）算"专业级"，
        /// 只有点右上角「详细」后才显示。见 <see cref="LogDetail"/>。
        /// </summary>
        private sealed class LogLine
        {
            public string Text;
            public bool Detail;
        }

        /// <summary>全量日志历史（UI 线程独占访问）。切「详细」模式时按它重建 LogBox。</summary>
        private readonly List<LogLine> _logAll = new List<LogLine>();

        /// <summary>true = 显示全部（含专业级）；false = 只显示日常级。默认 false。</summary>
        private bool _logVerbose;

        /// <summary>日志攒批刷新器。见 <see cref="Log"/> 的注释。</summary>
        private System.Windows.Threading.DispatcherTimer _logFlushTimer;

        /// <summary>保活复查定时器：有任务活跃下载时阻止系统空闲睡眠。见 <see cref="RefreshSleepGuard"/>。</summary>
        private System.Windows.Threading.DispatcherTimer _sleepGuardTimer;

        /// <summary>保活复查间隔（秒）。30 秒足够——系统空闲睡眠的阈值通常是分钟级。</summary>
        private const int SleepGuardTickSec = 30;

        /// <summary>
        /// 「无进展超时」（分钟）：任务处于 Downloading/Queued 但这么久没有前进，
        /// 就认定它**卡死**了，不再为它保活（让电脑照睡，下次启动会由
        /// 「未完成的任务」对话框提醒用户）。
        /// 为什么是 5 分钟：核心层已有 45 秒的停滞看门狗，但那条走的是"任务失败"路径；
        /// 这里兜的是**看门狗也掐不断**的卡死（worker 卡在不响应令牌的调用里）。
        /// 合并阶段虽然 `Done` 不增长，但有 `Phase`（"正在合并分片"）兜底，不会被误判。
        /// </summary>
        private const int SleepGuardStaleMinutes = 5;

        /// <summary>日志刷入 UI 的间隔（毫秒）。200ms 对肉眼已近乎实时。</summary>
        private const int LogFlushMs = 200;

        /// <summary>窗口隐藏到托盘时的日志刷新间隔（没人在看，降频省点 CPU）。</summary>
        private const int LogFlushMsHidden = 2000;

        /// <summary>
        /// 日志**入队**，不直接写控件 —— 由 <see cref="_logFlushTimer"/> 攒批刷入 LogBox。
        /// 为什么要攒批：原实现是每条日志一次
        /// `LogBox.AppendText(...) + ScrollToEnd()`，而 ScrollToEnd 会触发布局重算；
        /// 且每条都要切一次 UI 线程。512 并发时日志量不小，等于持续往**单线程的 UI**
        /// 上灌小任务 —— 界面就会卡。
        /// 参考 Python Flet 版：它用 `_process_q` 消息泵攒批刷 UI。
        /// 入队（ConcurrentQueue）本身线程安全，所以下载线程可直接调用。
        /// 日常级：与用户操作直接相关的结果/失败（登录、解析、入队、
        /// 完成、报错）。默认显示；专业级见 <see cref="LogDetail"/>。
        /// </summary>
        private void Log(string msg)
        {
            EnqueueLog(msg, detail: false);
        }

        /// <summary>
        /// 专业级日志：过程细节与内部状态（目录加载/缓存命中、连接分配、转存清理、
        /// 内部等待、界面小故障…）。只在「详细」打开时显示。
        /// 用户原话：「没有点击详细之前日志是很简单的，只有一些必要的信息，
        /// 日常使用必要的信息」。把过程性噪音挪到这里，默认视图只留结果。
        /// </summary>
        private void LogDetail(string msg)
        {
            EnqueueLog(msg, detail: true);
        }

        private void EnqueueLog(string msg, bool detail)
        {
            try
            {
                _logQueue.Enqueue(new LogLine
                {
                    Text = DateTime.Now.ToString("HH:mm:ss") + "  " + msg,
                    Detail = detail,
                });
            }
            catch { }
        }

        /// <summary>来自下载工作线程的日志。入队线程安全，无需再切 UI 线程。</summary>
        private void LogFromWorker(string msg)
        {
            // 下载内核的过程日志（换链、抢占、停滞…）整体算专业级 ——
            // 日常视图只看任务最终结果（失败/取消走 OnTaskUpdate，是日常级）。
            EnqueueLog(msg, detail: true);
        }

        /// <summary>LogBox 保留的最大行数（超出后从头砍）。</summary>
        /// <remarks>
        /// 加了"后台驻留"之后这条变得必要：以前关闭=退出，
        /// 日志最多活一次会话；现在窗口一收就能挂好几天，日志会**无限增长**，
        /// 既吃内存又让 TextBox 越来越卡（每次 AppendText 都要重排）。
        /// 2000 行足够回溯一次下载的完整过程，再多也没人翻。
        /// </remarks>
        private const int LogMaxLines = 2000;

        /// <summary>
        /// 网盘剩余容量低于这个值就直接把容量条判红（1 GB）。
        /// 与"占用率 ≥ 90%"是**或**的关系 —— 见 <see cref="UpdateAccountCard"/> 里的说明。
        /// </summary>
        private const long LowFreeBytes = 1024L * 1024L * 1024L;

        /// <summary>定时刷容量的间隔（秒）。5 分钟，与保活心跳同频。</summary>
        private const int CapacityRefreshSec = 300;

        /// <summary>定时刷容量的定时器。</summary>
        private System.Windows.Threading.DispatcherTimer _capacityTimer;

        /// <summary>登录是否已被判定为失效（失效后停止刷容量/心跳，等用户重新登录）。</summary>
        private bool _loginExpired;

        /// <summary>「总」进度日志的间隔（秒）。</summary>
        private const int ProgressLogSec = 10;

        /// <summary>进度汇总日志的定时器。</summary>
        private System.Windows.Threading.DispatcherTimer _progressLogTimer;

        /// <summary>上一行汇总日志的内容，用于去重（没变化就不重复打）。</summary>
        private string _lastProgressLog;

        /// <summary>
        /// 把所有在跑的任务**汇总成一行**写进日志。
        /// 取代原来核心层那种「每个任务每 5 秒一行」的刷屏日志 ——
        /// 普通用户只关心"一共多快、还剩多少"，逐任务的连接数/分片数/命中 IP
        /// 是排查用的，需要时把 <see cref="GeZi.Core.Download.SegmentedDownloader.PerTaskDiagLog"/>
        /// 打开即可，诊断面板本来也一直有。
        /// 去重：内容与上一条完全相同就不打（空闲/卡住时不会一直刷）。
        /// </summary>
        private void LogAggregateProgress()
        {
            try
            {
                var active = _tasks.Where(t => t.State == JobState.Downloading
                                            || t.State == JobState.Queued
                                            || t.State == JobState.Paused
                                            || t.State == JobState.Cancelling).ToList();
                if (active.Count == 0)
                {
                    _lastProgressLog = null;   // 这一批结束了，下次重新开始
                    return;
                }

                double speed = active.Where(t => string.IsNullOrEmpty(t.Phase)).Sum(t => t.Speed);
                long done = active.Sum(t => t.Done);
                long total = active.Sum(t => t.Total);
                int running = active.Count(t => t.State == JobState.Downloading);

                string pct = total > 0 ? (done * 100.0 / total).ToString("F1") + "%" : "—";
                string line = string.Format("下载中：{0} 个任务（{1} 个在跑），总速度 {2}/s，总进度 {3}",
                    active.Count, running,
                    Util.FormatSize((long)speed), pct);

                if (line == _lastProgressLog) return;   // 没变化就不重复打
                _lastProgressLog = line;
                // 每秒进度对日常视图是噪音（任务行已有进度条 + 速度），算专业级
                LogDetail(line);
            }
            catch { }
        }

        /// <summary>把攒下的日志一次性刷进 LogBox（由定时器在 UI 线程上调用）。</summary>
        private void FlushLogs()
        {
            if (_logQueue.IsEmpty)
                return;
            var sb = new StringBuilder();
            LogLine item;
            int n = 0;
            // 单次上限 500 条：万一积压太多，也不让一次刷新占住 UI 太久
            while (n < 500 && _logQueue.TryDequeue(out item))
            {
                _logAll.Add(item);   // 全量历史始终收（切「详细」要能补显）
                // 日常模式跳过专业级行
                if (_logVerbose || !item.Detail)
                {
                    sb.Append(item.Text).Append(Environment.NewLine);
                }
                n++;
            }
            if (sb.Length > 0)
            {
                LogBox.AppendText(sb.ToString());
                TrimLogLines();
                LogBox.ScrollToEnd();
            }
        }

        /// <summary>日志超过 <see cref="LogMaxLines"/> 时，从头部砍掉多余的整行。</summary>
        private void TrimLogLines()
        {
            try
            {
                if (_logAll.Count <= LogMaxLines)
                    return;
                int cut = _logAll.Count - LogMaxLines;
                _logAll.RemoveRange(0, cut);
                // 行列表变了（当前模式可能还显示着被砍的行）→ 按当前模式重建
                RebuildLogBox();
            }
            catch { }
        }

        /// <summary>按当前模式（<see cref="_logVerbose"/>）从全量历史重建 LogBox 文本。</summary>
        private void RebuildLogBox()
        {
            try
            {
                var sb = new StringBuilder();
                bool trimmed = false;
                var start = 0;
                if (_logAll.Count > LogMaxLines)
                {
                    start = _logAll.Count - LogMaxLines;
                    trimmed = true;
                }
                for (int i = start; i < _logAll.Count; i++)
                {
                    var l = _logAll[i];
                    if (_logVerbose || !l.Detail)
                        sb.Append(l.Text).Append(Environment.NewLine);
                }
                LogBox.Text = (trimmed
                    ? "…（更早的日志已省略，仅保留最近 " + LogMaxLines + " 行）" + Environment.NewLine
                    : "") + sb.ToString();
                LogBox.ScrollToEnd();
            }
            catch { }
        }

        /// <summary>
        /// 「详细」按钮：在「日常 / 全部」两种日志视图间切换。
        /// 默认只显示日常必要信息；点「详细」才显示专业级
        /// （目录加载、缓存命中、连接分配、内部状态等过程细节）。
        /// </summary>
        private void OnToggleLogVerbose(object sender, RoutedEventArgs e)
        {
            _logVerbose = !_logVerbose;
            if (LogDetailBtn != null)
                LogDetailBtn.Content = _logVerbose ? "详细 · 开" : "详细";
            RebuildLogBox();
        }

        /// <summary>
        /// 把「会等网络 / 扫磁盘」的按钮统一置灰，避免请求期间被重复触发。
        /// </summary>
        /// <remarks>
        /// ⚠️ 会**跳过正在显示加载态的按钮** —— 那些按钮由 <see cref="BeginBusy"/> 自己管，
        /// 它们要保持原色（加载态要看得清），不能被这里按成 0.45 透明度的"禁用"样子。
        /// </remarks>
        private void SetBusy(bool busy)
        {
            foreach (var b in new[] { ParseBtn, DownloadSelBtn, DownloadAllBtn,
                                      DriveRefreshBtn, DriveUpBtn, FetchLinksBtn })
            {
                if (b == null) continue;
                // 加载中 → 保持原色。⚠️ 可终止的加载态（「入队中」）还必须**保持可点**，
                // 不能被 SetBusy(false) 之外的东西影响 —— IsEnabled 本身不动它。
                if (_busyButtons.Contains(b)) continue;
                b.IsEnabled = !busy;
            }
        }

        // ---------------- 按钮「加载中」态 ----------------
        // 用户反馈：「他们需要加载时间或者是会切换到另一个东西，
        // 每次点击它们的时候都要有一个加载过程，主要是为了让用户看到他在加载」。
        // 本质是消除"点击 → 界面响应"之间那段静默期 —— 没有反馈时，用户无法区分
        // 「程序在忙」和「程序没反应」，于是会重复点击（取直链连点 = 重复发网络请求、
        // 导出连点 = 桌面多出几个 txt）。有反馈时同样的耗时就变成"它在干活，我等一下"。

        /// <summary>正在显示加载态的按钮。用于**拒绝重入**（防重复提交）。</summary>
        private readonly HashSet<Button> _busyButtons = new HashSet<Button>();

        /// <summary>进入加载态前的原始 Content，退出时原样还原（含图标等复杂内容）。</summary>
        private readonly Dictionary<Button, object> _busySavedContent = new Dictionary<Button, object>();

        /// <summary>
        /// 加载态的最短显示时长（毫秒）。实现见 <see cref="UiBusy.MinBusyMs"/>。
        /// </summary>
        private const int MinBusyMs = UiBusy.MinBusyMs;

        /// <summary>取一个计时基准，配合 <see cref="EndBusyAsync"/> 用。</summary>
        private static long BusyClock()
        {
            return DiagClock.GetTimestamp();
        }

        /// <summary>
        /// 把按钮切成「加载中」：内容换成 小转圈 + 短文案，并阻止重复点击。
        /// </summary>
        /// <returns>false = 该按钮已在加载中，本次点击应**直接丢弃**。</returns>
        /// <remarks>
        /// ⚠️ 这里**故意不动 IsEnabled**：按钮样式在 IsEnabled=False 时会把底色压到
        /// 0.45 透明度，那样加载态看起来是"被禁用"而不是"在忙"，转圈也会一起变淡。
        /// 所以改用 `IsHitTestVisible=false` 挡鼠标 + `_busyButtons` 挡键盘（焦点在按钮上
        /// 按空格仍会触发 Click，靠集合兜住）。同时 <see cref="SetBusy"/> 会跳过它。
        /// </remarks>
        private bool BeginBusy(Button btn, string busyText)
        {
            return BeginBusy(btn, busyText, cancelable: false);
        }

        /// <summary>
        /// 把按钮切成「加载中」（可终止版）。
        /// </summary>
        /// <param name="cancelable">
        /// true = **保持按钮可点**，用于"入队"这类可能长时间卡住的流程 ——
        /// 再点一次即触发终止（见 <see cref="RequestCancelEnqueue"/>）。
        /// 普通加载态（解析 / 取直链 / 刷新）传 false，仍需 <c>IsHitTestVisible=false</c> 防重入。
        /// </param>
        private bool BeginBusy(Button btn, string busyText, bool cancelable)
        {
            if (btn == null) return true;                 // 没有按钮就只当普通执行
            if (_busyButtons.Contains(btn)) return false; // 已在加载 → 拒绝重入

            _busyButtons.Add(btn);
            _busySavedContent[btn] = btn.Content;
            // 冻结最小宽度：忙时文案通常比原文案短，不冻结的话按钮会突然缩一下
            if (btn.ActualWidth > 0)
                btn.MinWidth = Math.Max(btn.MinWidth, btn.ActualWidth);
            btn.Content = UiBusy.BuildContent(busyText, btn);
            // 可终止的加载态：按钮仍可点击（点击 = 终止）；普通加载态则挡住重入。
            btn.IsHitTestVisible = cancelable;
            return true;
        }

        /// <summary>退出加载态、还原按钮内容（并保证加载态至少显示了 <see cref="MinBusyMs"/>）。</summary>
        private async Task EndBusyAsync(Button btn, long startedTicks)
        {
            if (btn == null) return;

            long elapsedMs = (DiagClock.GetTimestamp() - startedTicks) * 1000L / DiagClock.Frequency;
            int remain = MinBusyMs - (int)elapsedMs;
            if (remain > 0)
                await Task.Delay(remain);

            if (_busySavedContent.TryGetValue(btn, out var saved))
                btn.Content = saved;
            _busySavedContent.Remove(btn);
            _busyButtons.Remove(btn);
            // 还原后按钮回到"正常可点"（含「正在终止…」阶段被临时收回可点的情况）。
            btn.IsHitTestVisible = true;
        }

        // ---------------- 「入队」可终止（2026-10-11） ----------------

        /// <summary>「入队中」按钮的文案（按钮保持可点，再点一次即终止）。</summary>
        private const string EnqueueBusyText = "入队中";

        /// <summary>
        /// 进入「入队中」（**可终止**）。
        /// </summary>
        /// <returns>
        /// false = 本次点击**不应开始新的入队** —— 要么已在入队（本次点击已被当作
        /// 「终止」处理并触发了取消），要么按钮正被别的加载态占用（拒绝重入）。
        /// </returns>
        private bool BeginEnqueue(Button btn)
        {
            if (_enqueueCts != null)
            {
                // 已在入队 → 再点一次 = 终止
                RequestCancelEnqueue();
                return false;
            }

            // 按钮正显示别的加载态（如「解析中」）→ 拒绝重入
            if (btn != null && _busyButtons.Contains(btn)) return false;

            _enqueueCts = new CancellationTokenSource();
            _enqueueBusyBtn = btn;
            if (btn != null)
                BeginBusy(btn, EnqueueBusyText, cancelable: true);
            return true;
        }

        /// <summary>
        /// 请求终止当前「入队」流程。**不阻塞** —— 只发取消信号并改按钮文案，
        /// 真正退回由入队流程自己那串检查点逐级响应（正在飞的单个请求会等它返回）。
        /// </summary>
        private void RequestCancelEnqueue()
        {
            try
            {
                LogDetail("正在终止入队…");
                _enqueueCts?.Cancel();
                // 立刻给用户反馈：按钮文案改「正在终止…」并**收回可点**
                // （否则用户在流程退干净之前连点会反复触发 Cancel）。
                if (_enqueueBusyBtn != null && _busyButtons.Contains(_enqueueBusyBtn))
                {
                    _enqueueBusyBtn.Content = UiBusy.BuildContent("正在终止…", _enqueueBusyBtn);
                    _enqueueBusyBtn.IsHitTestVisible = false;
                }
            }
            catch { }
        }

        /// <summary>退出「入队中」态：清掉取消源并把按钮还原（保证至少显示 MinBusyMs）。</summary>
        private async Task EndEnqueueAsync(Button btn, long startedTicks)
        {
            try { _enqueueCts?.Dispose(); } catch { }
            _enqueueCts = null;
            _enqueueBusyBtn = null;
            await EndBusyAsync(btn, startedTicks);
        }

        /// <summary>造一个"小转圈"（缺口圆弧 + 无限旋转）。Stroke 由调用方设置。</summary>
        private static ShapePath MakeSpinner()
        {
            return UiBusy.MakeSpinner();
        }

        // ---------------- 模式切换 ----------------

        private async void OnModeChanged(object sender, RoutedEventArgs e)
        {
            if (SharePanel == null)
                return;

            bool nowShare = ShareMode;
            SharePanel.Visibility = nowShare ? Visibility.Visible : Visibility.Collapsed;

            // ① 先把**离开的那个模式**的导航状态存下来
            CaptureNav(nowShare ? _driveNav : _shareNav);

            // ② 再恢复**进入的那个模式**上次的状态。
            //    有快照就直接还原（连列表都不重新请求）—— 用户反馈的
            //    「切到我的网盘再回来要重新解析、解析结果没了」就是这里缺了恢复。
            var snap = nowShare ? _shareNav : _driveNav;
            if (snap.Files.Count > 0)
            {
                ApplyNav(snap);
                LogDetail("已切回上次的目录" + (snap.PathList.Count > 0
                        ? "：" + string.Join(" / ", snap.PathList)
                        : "（根目录）"));
                return;
            }

            // ③ 没有快照（首次进入该模式）才走原来的流程
            ResetNav();
            if (!nowShare)
            {
                if (_client == null)
                {
                    Log("网盘模式需要先登录");
                    return;
                }
                await LoadDirAsync("0");
            }
        }

        /// <summary>把当前导航状态存进指定快照。</summary>
        private void CaptureNav(NavSnapshot into)
        {
            if (into == null) return;
            into.Files = _files.ToList();
            into.CurFid = _curFid;
            into.FidStack = _fidStack.ToList();   // Stack.ToList() = 栈顶在前（pop 顺序）
            into.PathList = _pathList.ToList();
            into.PwdId = _pwdId;
            into.Stoken = _stoken;
            into.StartFid = _startFid;
        }

        /// <summary>把快照还原成当前导航状态（含分享的 pwd_id / stoken）。</summary>
        private void ApplyNav(NavSnapshot s)
        {
            if (s == null) return;
            _files.Clear();
            foreach (var f in s.Files) _files.Add(f);

            _curFid = s.CurFid ?? "0";

            _fidStack.Clear();
            // Stack.ToList() 是"栈顶在前"，还原时要倒着 push 回去才等价
            for (int i = s.FidStack.Count - 1; i >= 0; i--)
                _fidStack.Push(s.FidStack[i]);

            _pathList.Clear();
            _pathList.AddRange(s.PathList);

            _pwdId = s.PwdId;
            _stoken = s.Stoken;
            _startFid = string.IsNullOrEmpty(s.StartFid) ? "0" : s.StartFid;

            UpdatePathText();
            UpdateEmptyStates();
        }

        private void ResetNav()
        {
            _files.Clear();
            _fidStack.Clear();
            _pathList.Clear();
            _curFid = "0";
            UpdatePathText();
        }

        /// <summary>
        /// 换账号（登录成功 / 登出）时把**"网盘侧"的缓存与快照作废**。
        /// </summary>
        /// <remarks>
        /// 用户反馈：「登录了新账号，但从下载页切回我的网盘，
        /// 显示的还是旧账号的网盘内容」。
        /// 根因是换账号时只清了直链缓存（<see cref="LinkCache"/>），没清这两样：
        ///   ① 目录缓存 <c>_dirCache</c> / <c>_dirSizeCache</c> —— fid 是**账号内的编号**，
        ///      换账号后同一个 fid 指向的文件完全不同，留着必然张冠李戴；
        ///   ② 网盘导航快照 <c>_driveNav</c> —— 最要命：<see cref="OnModeChanged"/>
        ///      见到快照非空会**直接把旧列表铺开、连请求都不发**
        ///      （那是为"切回来不丢状态"特意做的），于是旧账号的文件原样重现。
        /// ⚠️ **刻意不动分享侧状态**（<c>_shareNav</c> / <c>_pwdId</c> / <c>_stoken</c>）：
        /// 分享（pwd_id + stoken + 每条的 fid_token）是**账号无关**的，匿名也能列目录/取链，
        /// 其 stoken 与 token 又是成组存放的、切换账号后依旧自洽。
        /// 若一并清掉，当用户正停在「分享」模式时（<c>_files</c> 还显示着分享内容），
        /// 再点下载会因 <c>_pwdId</c>/<c>_stoken</c> 变成 null 而失效 —— 那是自造的回归。
        /// </remarks>
        private void InvalidateAccountCaches()
        {
            _linkCache.Clear();
            // 目录缓存键带模式前缀（"s:"/"d:"），这里整体清掉：
            // 分享侧被顺带清掉只是下次进目录重取一次，无副作用；网盘侧则是必须清的。
            _dirCache.Clear();
            _dirSizeCache.Clear();

            // 网盘快照重置为"没进过"的状态（ShareFileItem 是可变引用集合，整体换新实例）
            _driveNav.Files = new List<ShareFileItem>();
            _driveNav.CurFid = "0";
            _driveNav.FidStack = new List<string>();
            _driveNav.PathList = new List<string>();
            _driveNav.PwdId = null;
            _driveNav.Stoken = null;
        }

        // ---------------- 分享解析 ----------------

        /// <summary>「分享链接」输入框右侧的一键清空（×）。</summary>
        private void OnClearLinkBox(object sender, RoutedEventArgs e)
        {
            try
            {
                LinkBox.Clear();
                LinkBox.Focus();   // 清空后把焦点还回去，方便直接粘贴下一条
            }
            catch { }
        }

        /// <summary>
        /// 「识别二维码」——夸克分享有时只给二维码图片、不给链接。
        /// 打开扫码窗口，用户用「Ctrl+V 粘贴截图 / 选文件 / 拖拽」任一方式给图，
        /// 解出的文本会走 <see cref="ApplyScannedText"/> 自动填进链接框。
        /// </summary>
        private void OnScanQrClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new ScanQrWindow(ApplyScannedText, Log)
                {
                    Owner = this,
                };
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                Log("打开二维码识别窗口失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 把识别出来的文本落到界面上。
        /// 识别结果不保证是链接（可能是提取码文案、口令、
        /// 甚至一张无关的码）。所以这里先判断：像链接就填进去并自动解析，不像就原样填进
        /// 链接框、让用户自己看一眼再决定 —— 悄悄丢弃或强行解析都不合适。
        /// </summary>
        private void ApplyScannedText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();

            try
            {
                LinkBox.Text = text;
                LinkBox.CaretIndex = text.Length;

                // 用现有解析器判断这段文本里有没有分享链接；有就顺手提取码
                var info = ShareUrlParser.Parse(text);
                bool looksLikeLink = !string.IsNullOrEmpty(info.PwdId);

                if (!looksLikeLink)
                {
                    // 不是可识别的分享链接：只填不解析，给出明确提示
                    LogDetail("二维码识别为：" + text);
                    if (info.IsKouling)
                    {
                        AppDialog.Show(this,
                            "识别出的内容是【夸克口令】，不是分享链接：\n\n" + text + "\n\n" +
                            "夸克没有公开的「口令转链接」接口，本工具无法直接解析。\n" +
                            "请在夸克 App 里打开这条口令，进入分享页后点「分享」→「复制链接」，\n" +
                            "再把拿到的 https://pan.quark.cn/s/… 链接粘进来。",
                            "这是夸克口令", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        AppDialog.Show(this,
                            "二维码识别成功，但内容看起来不是分享链接：\n\n" + text + "\n\n" +
                            "已填进链接框，你可以自己确认一下。",
                            "识别结果", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                // 是分享链接 → 把提取码一并回填（识别出的码通常比用户手输的准）
                if (!string.IsNullOrEmpty(info.Passcode))
                    PassBox.Text = info.Passcode;

                LogDetail("二维码识别出分享链接，开始解析…");
                OnParseClick(null, null);
            }
            catch (Exception ex)
            {
                LogDetail("处理识别结果失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 用户常把夸克分享的**整段文案**粘进来，形如：
        /// 「我用夸克网盘给你分享了「xxx.pdf」，点击链接或复制整段内容，打开「夸克APP」即可获取。
        ///   链接：https://pan.quark.cn/s/20ef19d26112  提取码：abcd」
        /// 🚨 为什么必须拦一下：`LinkBox` 是**单行、42px、横向滚动条 Disabled** 的输入框，
        /// 整段文案（近百字符）塞进去后**只能看到开头那截**，链接被挤出可视区域 ——
        /// 用户会以为"链接不见了"（真实反馈）。
        /// 其实解析本身没问题（`ShareUrlParser` 能正确提取），是**看不见**造成的误会。
        /// 所以粘贴时直接把内容替换成「裸链接」，并把文案里带的提取码顺手填进 `PassBox`。
        /// 找不到分享链接时**原样粘贴**，绝不吞掉用户的内容。
        /// </summary>
        private void OnLinkBoxPasting(object sender, DataObjectPastingEventArgs e)
        {
            try
            {
                if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText))
                    return;
                string text = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string;
                if (string.IsNullOrEmpty(text))
                    return;

                // 先尝试从文案里**原样抠出 URL**（保留 fid / 查询串等），比按 PwdId 重建更保真。
                var m = Regex.Match(text, @"https?://[^\s""'<>）】\]]+");
                string url = null;
                if (m.Success && m.Value.IndexOf("/s/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    url = m.Value;
                }
                else
                {
                    // 退路：按解析出的 PwdId 重建（应对"只有纯文本、URL 被截断"等情况）
                    var info = ShareUrlParser.Parse(text);
                    if (!string.IsNullOrEmpty(info.PwdId))
                        url = "https://pan.quark.cn/s/" + info.PwdId;
                }

                if (string.IsNullOrEmpty(url))
                    return;   // 不是分享链接 → 原样粘贴，别动用户的东西

                // 用替换后的数据对象覆盖本次粘贴内容
                e.DataObject = new DataObject(DataFormats.UnicodeText, url);

                // 文案里带提取码时顺手填上（用户看得见"用了什么码"，解析失败时好排查）
                var info2 = ShareUrlParser.Parse(text);
                if (!string.IsNullOrEmpty(info2.Passcode) && string.IsNullOrEmpty(PassBox.Text.Trim()))
                    PassBox.Text = info2.Passcode;
            }
            catch { /* 粘贴绝不因为这里出错而失败 */ }
        }

        private async void OnParseClick(object sender, RoutedEventArgs e)
        {
            string url = LinkBox.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                Log("请先粘贴分享链接");
                return;
            }
            var info = ShareUrlParser.Parse(url);
            if (info.IsKouling)
            {
                // 夸克口令（形如 /~469d3M9zTu~:/）**不是**分享链接，也没有公开的
                // 「口令 → 链接」接口（查过官方开放平台与非官方 API 文档，分享相关
                // 接口只接受 pwd_id）。所以这里不装作能解析，直接给出可操作的指引。
                Log("这是夸克口令（/~" + info.Kouling + "~:/），不是分享链接，无法直接解析。");
                AppDialog.Show(this,
                    "这是一条【夸克口令】，不是分享链接。\n\n" +
                    "夸克没有公开「口令转链接」的接口，本工具无法直接解析它。\n\n" +
                    "请这样拿到链接：\n" +
                    "1. 在夸克 App / 夸克网盘客户端里打开这条口令；\n" +
                    "2. 进入分享页后点「分享」→「复制链接」；\n" +
                    "3. 把复制到的 https://pan.quark.cn/s/… 链接粘回这里即可。\n\n" +
                    "（如果对方同时给了链接和提取码，直接整段粘贴进来也行 —— " +
                    "本工具会自动把链接和提取码都认出来。）",
                    "这是夸克口令", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrEmpty(info.PwdId))
            {
                Log("无法从链接解析出分享 ID");
                return;
            }
            string passcode = string.IsNullOrEmpty(PassBox.Text.Trim()) ? info.Passcode : PassBox.Text.Trim();
            // 整段分享文案里自带提取码时，把它回填到输入框 —— 让用户看得见"用了什么码"，
            // 否则解析失败时完全不知道是码错了还是别的原因。
            if (!string.IsNullOrEmpty(info.Passcode) && PassBox.Text.Trim() != info.Passcode)
                PassBox.Text = info.Passcode;
            _pwdId = info.PwdId;
            // 提取码留下字段副本：下载器回调（线程池线程）不能读 PassBox，
            // 而自动重解析分享（换新 stoken）必须带上它。
            _passcode = passcode;
            _linkCache.Clear();   // 换分享后旧 fid 的直链不再适用
            // 目录缓存与大小缓存同理：fid 是分享内的编号，换了分享再留着会张冠李戴。
            _dirCache.Clear();
            _dirSizeCache.Clear();

            _client?.Dispose();
            _client = new QuarkClient(_settings.Cookie);

            // 解析要发两次网络请求（换 stoken + 拉目录），期间必须让用户看到"在忙"
            long t0 = BusyClock();
            if (!BeginBusy(ParseBtn, "解析中")) return;
            SetBusy(true);
            try
            {
                _stoken = await _client.GetStokenAsync(info.PwdId, passcode);
                if (string.IsNullOrEmpty(_stoken))
                {
                    Log("获取分享凭证失败（可能需登录或提取码错误）");
                    NotifyIfHidden("自动解析失败",
                        "获取分享凭证失败，可能需要登录，或分享提取码不正确。");
                    return;
                }
                ResetNav();
                _curFid = info.StartFid;
                _startFid = string.IsNullOrEmpty(info.StartFid) ? "0" : info.StartFid;
                await LoadDirAsync(_curFid);
                Log("解析成功。双击文件夹进入子目录；选中文件后点「下载选中」即可下载"
                    + "（多选用 Shift / Ctrl+点击，或右键该行）。");
                // 记入解析历史（原文案是「单击进入」，我们保留双击版）
                RecordShareHistory(info.PwdId, passcode, info.StartFid);
                NotifyIfHidden("已解析剪贴板里的分享链接",
                    _files.Count > 0
                        ? "共 " + _files.Count + " 个文件。双击托盘图标打开窗口即可下载。"
                        : "该分享的当前目录是空的（文件可能在子目录里）。双击托盘图标打开窗口查看。");
            }
            catch (Exception ex)
            {
                Log("解析失败: " + ex.Message);
                NotifyIfHidden("自动解析失败", ex.Message);
            }
            finally
            {
                SetBusy(false);
                await EndBusyAsync(ParseBtn, t0);
            }
        }

        // ---------------- 剪贴板自动解析（2026-10-08） ----------------
        // 需求：在**任何程序**里复制到夸克分享链接，本程序直接解析好，不用再切回窗口粘贴。
        //   默认开；设置页「窗口与后台 → 剪贴板」可关，关掉时**根本不注册**监听（零开销）。
        // 实现路线见 NativeMethods：AddClipboardFormatListener + WM_CLIPBOARDUPDATE，
        // 既不用轮询，也不插队进 `SetClipboardViewer` 那条"链"。
        // 通知由系统投递到本窗口的消息队列 → 下面这些回调**都在 UI 线程**上跑。

        /// <summary>
        /// 把两件**绑在窗口句柄上**的事挂到当前句柄上：
        ///   ① 跨进程激活消息钩子（再点一次 exe 时由本进程自己把窗口拉起来）；
        ///   ② 剪贴板监听（按设置，见 <see cref="ApplyClipboardListener"/>）。
        /// 由 <c>SourceInitialized</c> 触发（订阅在构造函数里），**幂等**：同一个句柄只挂一次。
        /// `HwndSource.AddHook` 和
        /// `AddClipboardFormatListener` 都是**绑在原生窗口句柄**上的。窗口在
        /// "隐藏到托盘 → 恢复" 的过程中句柄会不会被 WPF 重建，各版本/资料说法不一，
        /// 赌不起：句柄一旦被换掉，两个注册就都**静默失效** —— 表现为"恢复窗口后
        /// 复制分享链接不再自动解析"、"再点 exe 唤不醒托盘里的窗口"，
        /// 而这两种现象都不会报错，只会让人以为功能没做对。
        /// 所以这里按句柄判断，并在每次句柄（重）建时重挂一遍 —— 两种情况都对。
        /// </summary>
        private void AttachWindowHooks()
        {
            try
            {
                var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero) return;      // 句柄还没建好，等下一次事件
                if (h == _hooksOnHwnd) return;     // 这个句柄已经挂过了

                try
                {
                    var src = System.Windows.Interop.HwndSource.FromHwnd(h);
                    src?.AddHook(WndProc);
                }
                catch { }

                _hooksOnHwnd = h;
                ApplyClipboardListener();
            }
            catch (Exception ex)
            {
                LogDetail("窗口消息钩子安装失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 按设置挂上 / 摘掉剪贴板监听（幂等）。启动、窗口句柄重建、设置页开关都调它。
        /// </summary>
        private void ApplyClipboardListener()
        {
            try
            {
                // ⚠️ 注册必须**有窗口句柄之后**（HWND 存在前是 IntPtr.Zero）——
                //    这也是它不能写在构造函数里的原因。
                var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero) return;

                bool want = _settings.ClipboardAutoParse;

                // 句柄与设置都没变 → 已经是对的，什么都不用做。
                if (want && _clipboardHookHwnd == h) return;
                if (!want && _clipboardHookHwnd == IntPtr.Zero) return;

                // 先摘掉旧注册。旧句柄若已销毁，这次摘除会失败 —— 无所谓，
                // 注册本来就是随句柄一起消失的。
                if (_clipboardHookHwnd != IntPtr.Zero)
                {
                    NativeMethods.RemoveClipboardFormatListener(_clipboardHookHwnd);
                    _clipboardHookHwnd = IntPtr.Zero;
                }
                if (!want) return;

                if (NativeMethods.AddClipboardFormatListener(h))
                {
                    _clipboardHookHwnd = h;
                }
                else
                {
                    // 基本只会在句柄无效时发生；但既然失败了就说清楚 ——
                    // 让用户知道"复制链接没反应"不是链接的问题。
                    LogDetail("剪贴板监听开启失败（Win32 错误码 " + Marshal.GetLastWin32Error()
                        + "），复制分享链接不会自动解析。");
                }
            }
            catch (Exception ex)
            {
                LogDetail("剪贴板监听设置失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 设置页「剪贴板」开关。
        /// 走独立处理器而不是 <see cref="OnSettingChanged"/>（同 <see cref="OnThemeChanged"/>
        /// 的理由）：这个开关只影响"要不要注册一条系统通知"，与并发数/下载目录无关，
        /// 走 ApplySettings() 会顺带 RecreateScheduler() 去重建下载调度器 ——
        /// 为了一个开关打断在途下载，代价完全不成比例。
        /// </summary>
        private void OnClipboardAutoParseChanged(object sender, RoutedEventArgs e)
        {
            // 回填控件时也会触发 Click（走 _applyingSettings 保护，同其它设置项）
            if (_applyingSettings) return;
            try
            {
                bool on = ClipboardAutoParseCheck.IsChecked == true;
                _settings.ClipboardAutoParse = on;
                SettingsStore.Save(_settings);
                ApplyClipboardListener();
                Log(on ? "已开启剪贴板监听：复制夸克分享链接会自动解析。"
                       : "已关闭剪贴板监听。");

                // 关掉时清掉去重标记：重新打开后，用户再复制同一条链接应该能正常解析，
                // 不该被"上次已经处理过"的记忆挡住。
                if (!on) _clipboardLastPwdId = null;
            }
            catch (Exception ex)
            {
                LogDetail("剪贴板设置保存失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 设置页「跟随系统代理」开关（2026-10-11 新增）。
        /// 走独立处理器而不是 <see cref="OnSettingChanged"/>（同 <see cref="OnClipboardAutoParseChanged"/>
        /// 的理由）：这个开关只写一个核心层静态标志，不需要重建下载调度器 ——
        /// 为它重建调度器反而会打断在途任务。
        /// ⚠️ 静态开关只影响**此后新建**的连接：已发起的请求不会改道，
        ///    所以日志里要跟用户讲清楚"重新开始下载 / 重新登录后生效"。
        /// </summary>
        private void OnUseSystemProxyChanged(object sender, RoutedEventArgs e)
        {
            if (_applyingSettings) return;
            try
            {
                bool on = UseSystemProxyCheck.IsChecked == true;
                _settings.UseSystemProxy = on;
                SettingsStore.Save(_settings);
                NetworkOptions.UseSystemProxy = on;
                Log(on
                    ? "已开启「跟随系统代理」：之后新建的连接会走 Windows 系统代理（重新开始下载 / 重新登录后生效）。"
                    : "已关闭「跟随系统代理」：之后的连接一律直连。");
            }
            catch (Exception ex)
            {
                Log("代理设置保存失败: " + ex.Message);
            }
        }

        /// <summary>收到「剪贴板内容变了」的系统通知。</summary>
        private void OnClipboardChanged()
        {
            try
            {
                // 退出流程里不再处理：这时窗口/托盘可能已经拆掉了。
                if (_exiting) return;

                // 关了开关就不再处理。一般走不到这里（关了就直接摘注册），
                // 但"关掉的那一刻"可能已经有通知排进消息队列了 —— 补一道，免得
                // 用户刚勾掉开关、随手复制一下，程序又自作主张解析了一次。
                if (!_settings.ClipboardAutoParse) return;

                string text = TryReadClipboardText();
                if (string.IsNullOrEmpty(text)) return;

                string url = FindQuarkShareUrl(text);
                if (string.IsNullOrEmpty(url))
                {
                    // 不是夸克分享链接（含"夸克口令"）。这里**刻意什么都不做、也不弹窗**：
                    // 剪贴板通知不是用户在本程序里发起的动作，为一句"这不能解析"打断他很粗暴
                    // —— 何况口令在聊天记录里到处都是，复制顺手就会触发。
                    return;
                }

                var info = ShareUrlParser.Parse(url);
                if (string.IsNullOrEmpty(info.PwdId)) return;

                if (string.Equals(info.PwdId, _clipboardLastPwdId, StringComparison.Ordinal))
                {
                    // 同一条分享的重复通知 → 不再重复发请求。见 _clipboardLastPwdId 的注释。
                    // （要重新解析，直接点「解析」按钮即可 —— 手动路径不受这个标记影响。）
                    return;
                }
                _clipboardLastPwdId = info.PwdId;

                // 提取码从**整段文本**里取：链接自带 pwd= 的能认，文案里的
                // 「提取码：abcd」也能认（ShareUrlParser 两种都支持）。
                FillAndParse(url, ShareUrlParser.Parse(text).Passcode);
            }
            catch (Exception ex)
            {
                try { Log("剪贴板自动解析失败: " + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// 从一段文本里找出**夸克网盘**的分享链接；找不到返回 null。
        /// 🚨 这里必须比「粘贴进链接框」（<see cref="OnLinkBoxPasting"/>）严得多：
        ///    · 粘贴是用户在**本程序里**的明确动作，识别宽松些最多是顺手填个框；
        ///    · 剪贴板监听是**后台自己发起**的，认错就会把用户正在复制的别的东西
        ///      （比如别的网盘恰好也叫 /s/ 的链接）当夸克链接去请求、还把他的界面切走。
        /// 所以域名必须真的是夸克的，`/s/` 只是必要条件。
        /// 本程序有唯一一处 Clipboard.SetText
        /// （诊断面板「复制诊断文本」），写的是制表符分隔的文件名 —— 不含夸克域名，
        /// 因此不会触发自己，不需要额外的"自写抑制"标记。
        /// </summary>
        private static string FindQuarkShareUrl(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (text.Length > MaxClipboardScanChars) return null;

            foreach (Match m in ClipboardUrlRx.Matches(text))
            {
                string u = m.Value;
                if (u.IndexOf("/s/", StringComparison.OrdinalIgnoreCase) < 0) continue;

                // URL 后面常紧跟从句标点或中文标点（"…/s/abc。以下略"、"（…/s/abc）"），
                // 正则可列的排除集不可能全，统一在**第一个非 URL 字符**处截断，
                // 再削掉尾部可能残留的句读（`.` `,` 在 URL 里合法，只能靠这一步收尾）。
                int cut = 0;
                while (cut < u.Length && IsUrlChar(u[cut])) cut++;
                u = u.Substring(0, cut).TrimEnd('.', ',', ';', '!', '?');

                if (!IsQuarkHost(u)) continue;
                return u;
            }
            return null;
        }

        /// <summary>URL 里合法的字符（RFC 3986 的 unreserved + 保留字 + `%`）。</summary>
        private static bool IsUrlChar(char c)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                return true;
            return "-._~:/?#[]@!$&'()*+,;=%".IndexOf(c) >= 0;
        }

        /// <summary>URL 的主机是不是夸克自己（<c>quark.cn</c> / <c>*.quark.cn</c>）。</summary>
        private static bool IsQuarkHost(string url)
        {
            try
            {
                Uri u;
                if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
                if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
                string h = u.Host;
                return h.Equals("quark.cn", StringComparison.OrdinalIgnoreCase)
                    || h.EndsWith(".quark.cn", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// 尽力读一次剪贴板文本：不是文本、或实在抢不到剪贴板时返回 null。
        /// 剪贴板是**全局独占**的：写入方还没释放时
        /// <see cref="Clipboard.GetText"/> 会抛 COMException(CLIPBRD_E_CANT_OPEN)。
        /// `WM_CLIPBOARDUPDATE` 是"写完之后"才投递的，正常第一次就能读到；
        /// 但 Office 之流会自己多持有几毫秒，所以留一个很短的退避重试。
        /// 总等待上限 ~120ms，且只在真的被占用时才会发生（正常路径一次就返回）。
        /// </summary>
        private static string TryReadClipboardText()
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    if (!Clipboard.ContainsText()) return null;
                    return Clipboard.GetText();
                }
                catch (Exception)
                {
                    Thread.Sleep(20);
                }
            }
            return null;
        }

        /// <summary>
        /// 把一条分享链接**填进界面并解析**（复用「解析」按钮那套实现，包括按钮加载态与失败提示）。
        /// 两个入口：剪贴板监听到链接、以及历史面板里点某一条。
        /// </summary>
        private void FillAndParse(string url, string passcode)
        {
            try
            {
                // ① 必须切回「分享链接」模式 —— 这不只是"让结果看得见"：
                //    LoadDirAsync 是**按 ShareMode 分流**的（分享走 ShareDetailAsync、
                //    我的网盘走 ListDirAsync），停在「我的网盘」模式下解析会把
                //    自己网盘的根目录列出来。切换本身是同步完成的
                //    （OnModeChanged 到第一个 await 之前就做完了），并且会先把网盘那边的
                //    导航存进快照，用户原来的浏览位置不会丢。
                if (ShareModeRadio.IsChecked != true)
                    ShareModeRadio.IsChecked = true;

                // ② 结果只出现在「下载」页的分享面板里（NavList 顺序：0=下载 1=任务
                //    2=设置 3=登录），所以把页面也指过去，否则用户在设置页复制的链接，
                //    程序在背后解析完他什么也看不见。
                //    ⚠️ 只切页签，不 Show / 不 Activate —— 窗口藏在托盘时静默解析，不抢焦点。
                Tabs.SelectedIndex = 0;
            }
            catch { }

            LinkBox.Text = url;
            LinkBox.CaretIndex = url.Length;

            // ③ 提取码以**本次复制的内容**为准（没有就清空）：PassBox 里可能还留着
            //    上一条分享的码，而 OnParseClick 会优先采用 PassBox 的值 ——
            //    留着它会"用错码"，解析失败时还完全看不出原因。
            PassBox.Text = passcode ?? "";

            LogDetail("剪贴板里发现夸克分享链接，自动解析…");
            OnParseClick(null, null);
        }

        /// <summary>
        /// 窗口不可见（藏在托盘）时用托盘气泡报一句；窗口可见时不弹 ——
        /// 界面上本来就能看到结果，再弹气泡是重复打扰。
        /// </summary>
        private void NotifyIfHidden(string title, string text)
        {
            try
            {
                if (IsVisible) return;
                _tray?.ShowBalloon(title, text);
            }
            catch { }
        }

        // ---------------- 解析历史面板（2026-10-09） ----------------
        // 数据在 HistoryStore（GeZi.state.json 里的 Shares），这里只管画与开关。
        // 布局照选定稿：整窗压暗 + **居中**卡片，列表按 今天 / 昨天 / 更早 分组，
        // 一行 = 文件夹图标 + 名称 + 「时间 · 分享 ID · 提取码」，悬停整行高亮。

        /// <summary>
        /// 窗口级按键。**目前只处理 Esc**（关闭解析历史面板）。
        /// 🚨 第三方分支那一整套快捷键（1/2/3 切页、h 开历史、a/d/x/g 等）
        /// 我们**不采纳**，所以这里刻意不做任何单键分发 —— 只保留
        /// 「面板开着时 Esc 关闭」这一条**必要**行为：面板自己的文案就写着
        /// 「按 Esc、或点面板外的空白处关闭」，不实现就是骗用户。
        /// </summary>
        private void OnMainKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            if (!HistoryOpen) return;
            HideHistoryPanel();
            e.Handled = true;
        }

        /// <summary>历史面板是不是开着（开关按钮与关闭逻辑都看它）。</summary>
        private bool HistoryOpen
        {
            get { return HistoryOverlay != null && HistoryOverlay.Visibility == Visibility.Visible; }
        }

        private void ShowHistoryPanel()
        {
            try
            {
                RebuildHistoryPanel();   // 每次打开都重建：这中间可能又解析过新分享
                if (HistoryOverlay == null) return;
                HistoryOverlay.Visibility = Visibility.Visible;
                // 把焦点收到浮层自己身上：否则之后随手敲的字母会钻进底下那个**看不见的**
                // 链接框（把链接改脏），或者变成文件列表的首字母跳转。
                HistoryOverlay.Focus();
            }
            catch (Exception ex)
            {
                LogDetail("打开解析历史失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 解析历史面板右上角的关闭叉号。
        /// 复用既有的 HideHistoryPanel（与 Esc、点遮罩走同一条路径）。
        /// </summary>
        private void OnHistoryCloseClick(object sender, RoutedEventArgs e)
        {
            HideHistoryPanel();
        }

        private void HideHistoryPanel()
        {
            try { if (HistoryOverlay != null) HistoryOverlay.Visibility = Visibility.Collapsed; } catch { }
        }

        /// <summary>点遮罩（面板外的暗色区域）= 关闭。面板本身是它的兄弟节点，不会误触。</summary>
        private void OnHistoryOverlayClick(object sender, MouseButtonEventArgs e)
        {
            HideHistoryPanel();
        }

        private void OnHistoryClearClick(object sender, RoutedEventArgs e)
        {
            try
            {
                HistoryStore.ClearShareHistory();
                RebuildHistoryPanel();
                Log("已清空解析历史。");
            }
            catch (Exception ex)
            {
                Log("清空解析历史失败: " + ex.Message);
            }
        }

        /// <summary>重建列表：分组标题 + 可点行（都动态生成，见 MakeHistoryRow）。</summary>
        private void RebuildHistoryPanel()
        {
            if (HistoryListPanel == null) return;
            HistoryListPanel.Children.Clear();

            var items = HistoryStore.GetShareHistory();
            if (HistoryCountText != null)
                HistoryCountText.Text = items.Count > 0 ? "共 " + items.Count + " 条" : "";

            if (items.Count == 0)
            {
                HistoryListPanel.Children.Add(new TextBlock
                {
                    Text = "还没有解析记录。\n解析过的分享会留在这里，下次点一下就能重新打开。",
                    Style = (Style)FindResource("HistoryEmptyText"),
                });
                return;
            }

            string lastGroup = null;
            foreach (var it in items)
            {
                // 置顶的单独成组排在最前（GetShareHistory 已经把置顶的排到前面了）
                string g = it.Pinned ? "置顶" : HistoryGroupOf(it.Time);
                if (!string.Equals(g, lastGroup, StringComparison.Ordinal))
                {
                    HistoryListPanel.Children.Add(new TextBlock
                    {
                        Text = g,
                        Style = (Style)FindResource("HistoryGroupLabel"),
                    });
                    lastGroup = g;
                }
                HistoryListPanel.Children.Add(MakeHistoryRow(it));
            }
        }

        /// <summary>分组标签：按"距今天多久"给粗粒度分组（今天 / 昨天 / 更早）。</summary>
        private static string HistoryGroupOf(string raw)
        {
            DateTime t;
            if (!DateTime.TryParse(raw, out t)) return "更早";
            DateTime today = DateTime.Today;
            if (t.Date == today) return "今天";
            if (t.Date == today.AddDays(-1)) return "昨天";
            return "更早";
        }

        /// <summary>行里的时间：「今天 11:58」/「昨天 21:03」/「10-06 17:20」。</summary>
        private static string FormatHistoryTime(string raw)
        {
            DateTime t;
            if (!DateTime.TryParse(raw, out t)) return raw ?? "";
            DateTime today = DateTime.Today;
            if (t.Date == today) return "今天 " + t.ToString("HH:mm");
            if (t.Date == today.AddDays(-1)) return "昨天 " + t.ToString("HH:mm");
            return t.ToString("MM-dd HH:mm");
        }

        /// <summary>行里的副行：「今天 11:58 · /s/1f842a86 · 提取码 4k3p」。</summary>
        private static string HistoryMetaOf(ShareHistoryEntry it)
        {
            var sb = new StringBuilder(FormatHistoryTime(it.Time));
            if (!string.IsNullOrEmpty(it.PwdId))
            {
                sb.Append(" · /s/");
                sb.Append(it.PwdId.Length > 8 ? it.PwdId.Substring(0, 8) : it.PwdId);
            }
            if (!string.IsNullOrEmpty(it.Passcode))
                sb.Append(" · 提取码 ").Append(it.Passcode);
            return sb.ToString();
        }

        /// <summary>造一行历史。整行是个 Button，点 = 重新解析那条分享。</summary>
        private UIElement MakeHistoryRow(ShareHistoryEntry it)
        {
            // 图标：文件夹用"警示色的文件夹"（与文件列表里的文件夹图标同一套语言）；
            // 没有文件夹的（根目录只有散文件）用文件图标 + 次要色。
            var icon = new Border
            {
                Width = 34, Height = 34,
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            icon.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,
                "SurfaceSunkenBrush");
            icon.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty,
                "BorderBrushSoft");

            var glyph = new ShapePath
            {
                Data = (Geometry)FindResource(it.IsFolder ? "IconFolder" : "IconFile"),
                Stretch = Stretch.Uniform,
                Width = 17, Height = 17,
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
                it.IsFolder ? "WarningBrush" : "TextTertiaryBrush");
            icon.Child = glyph;

            var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            meta.Children.Add(new TextBlock
            {
                Text = it.Name ?? "",
                Style = (Style)FindResource("HistoryNameText"),
            });
            meta.Children.Add(new TextBlock
            {
                Text = HistoryMetaOf(it),
                Style = (Style)FindResource("HistoryMetaText"),
            });

            // ⚠️ 外层用 Grid 而不是横向 StackPanel：名称那个省略号只在**宽度被约束**时才生效，
            //    StackPanel 会用无限宽去量孩子，长名字就直接撑出去、不截断。
            var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
            meta.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(icon, 0);
            Grid.SetColumn(meta, 1);
            row.Children.Add(icon);
            row.Children.Add(meta);

            var btn = new Button
            {
                Content = row,
                Style = (Style)FindResource("HistoryRowButton"),
                Tag = it,     // 点击时取回这一条（见 OnHistoryItemClick）
                ToolTip = "重新解析：" + (it.Name ?? ""),
            };
            btn.Click += OnHistoryItemClick;
            btn.ContextMenu = MakeHistoryMenu(it);
            return btn;
        }

        /// <summary>行上右键：置顶 / 取消置顶、删除这条记录。</summary>
        private ContextMenu MakeHistoryMenu(ShareHistoryEntry it)
        {
            // ⚠️ 菜单项是**每次重建行时新建**的，所以 Header 直接按当前状态定；
            //    操作完都会 RebuildHistoryPanel()，标签跟着刷新（不用做双向绑定）。
            var menu = new ContextMenu();

            var pin = new MenuItem { Header = it.Pinned ? "取消置顶" : "置顶" };
            pin.Click += (s, e) =>
            {
                try
                {
                    HistoryStore.SetSharePinned(it.PwdId, !it.Pinned);
                    RebuildHistoryPanel();
                    Log(it.Pinned ? "已取消置顶：" + it.Name : "已置顶：" + it.Name);
                }
                catch (Exception ex) { Log("置顶操作失败: " + ex.Message); }
            };

            var del = new MenuItem { Header = "删除这条记录" };
            del.Click += (s, e) =>
            {
                try
                {
                    HistoryStore.RemoveShareHistory(it.PwdId);
                    RebuildHistoryPanel();
                    Log("已从解析历史里删除：" + it.Name);
                }
                catch (Exception ex) { Log("删除记录失败: " + ex.Message); }
            };

            menu.Items.Add(pin);
            menu.Items.Add(del);
            return menu;
        }

        /// <summary>
        /// 文件区工具栏上那个「解析历史」按钮（h 键之外看得见的入口）。
        /// ⚠️ 别和任务页的「下载历史」（<c>HistoryBtn</c> / OnOpenHistory）搞混 ——
        /// 那个是**本地下载记录**，这个是**解析过的分享**。
        /// </summary>
        private void OnShareHistoryButtonClick(object sender, RoutedEventArgs e)
        {
            ShowHistoryPanel();
        }

        /// <summary>点历史里的一条 = 重新解析那条分享。</summary>
        private void OnHistoryItemClick(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var it = btn != null ? btn.Tag as ShareHistoryEntry : null;
            if (it == null || string.IsNullOrEmpty(it.PwdId)) return;

            HideHistoryPanel();

            // 原来那条链接指向某个子目录时，把 fid 也拼回去：OnParseClick 会把它当成
            // start_fid，于是回到上次看的那个目录，而不是分享根目录。
            string url = "https://pan.quark.cn/s/" + it.PwdId;
            if (!string.IsNullOrEmpty(it.StartFid) && it.StartFid != "0")
                url += "/" + it.StartFid;

            LogDetail("从解析历史打开：「" + (it.Name ?? it.PwdId) + "」");
            FillAndParse(url, it.Passcode);
        }

        /// <summary>解析成功后记一条历史（供 h 面板）。</summary>
        private void RecordShareHistory(string pwdId, string passcode, string startFid)
        {
            try
            {
                // 名字取"解析后看到的第一个文件夹" —— 这正是用户认这条记录的依据；
                // 分享根目录只有散文件时退化成「（根目录）· N 个文件」。
                var firstDir = _files.FirstOrDefault(f => f.IsDir);
                string name = firstDir != null
                    ? firstDir.Name
                    : "（根目录）· " + _files.Count + " 个文件";
                HistoryStore.AddShareHistory(pwdId, passcode, startFid, name, firstDir != null);
            }
            catch { }
        }

        // ---------------- 目录浏览（分享/网盘统一） ----------------

        /// <summary>
        /// 进入文件夹（**双击**文件夹行触发，见 <see cref="OnFileListDoubleClick"/>）。
        /// 它由点击事件直接触发，没有调用方要等结果 ——
        /// 与 <see cref="OnParseClick"/> 等既有处理器同一形态。目录请求失败已在
        /// <see cref="LoadDirAsync"/> 内部消化（打日志），不会漏到这里。
        /// </summary>
        private async void EnterFolder(ShareFileItem dir)
        {
            // 目录正在加载时不响应：列表要等网络返回才刷新，这段窗口里**旧行还能点**。
            // 不挡的话会连着推两次 _fidStack / _pathList（面包屑多一层、"上一级"错位），
            // 两个 LoadDirAsync 还会并发赛跑，谁后回来谁覆盖。
            if (_loadingDir) return;

            _fidStack.Push(_curFid);
            _curFid = dir.Fid;
            _pathList.Add(dir.Name);
            UpdatePathText();
            await LoadDirAsync(dir.Fid);
        }

        /// <summary>
        /// 双击文件夹进入子目录。
        /// 🚨 这是**原版行为**：第三方分支 v2.4 把它改成了「单击直接进入」
        /// 并删掉了本处理器，我们**不采纳**那个改动 —— 单击保持"选中"，双击才进目录。
        /// （该分支里"面包屑路径条可点击跳转"是独立功能，那个我们**采纳了**。）
        /// </summary>
        private async void OnFileListDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var item = FileList.SelectedItem as ShareFileItem;
            if (item == null || !item.IsDir)
                return;
            EnterFolder(item);
        }

        /// <summary>
        /// 文件列表的左键点击策略（在 PreviewMouseLeftButtonDown / 隧道阶段统一裁决）：
        ///   · **Shift+点击 = 离散多选**（只切换点中的这一项），**不做连选**；
        ///   · **Ctrl+点击 = 只选中这一个**；
        ///   · 单击 = WPF 默认行为（选中）；**进目录请双击**（见 <see cref="OnFileListDoubleClick"/>）。
        /// WPF 的 `SelectionMode="Extended"` 里：
        ///   · Ctrl+点击  = 切换单项（离散多选）
        ///   · **Shift+点击 = 连选**：把"锚点项"到当前项**整段**都选上
        /// 用户要的是「可以使用 shift 多选，但**不是连选**」——
        /// 也就是 Shift 只作用于**点中的那一个**，不拉区间。
        /// 所以在隧道阶段把它拦下来自己处理，并 `e.Handled = true` 阻止 WPF 的连选。
        /// ⚠️ 不拦的话会怎样：选中第 1 个、Shift+点第 5 个 → 1~5 全被选上（连选）。
        /// </summary>
        private void OnListPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            var item = FindListBoxItem(e.OriginalSource as DependencyObject);
            if (item == null)
                return;   // 点在空白处：交给默认处理（清空选择）

            // ① Shift+点击 = 离散多选（只切换点中的这一项）
            if ((mods & ModifierKeys.Shift) != 0)
            {
                e.Handled = true;                    // 关键：阻止 WPF 的"连选"
                item.IsSelected = !item.IsSelected;
                try { item.Focus(); } catch { }
                return;
            }

            // ② Ctrl+点击 = **按用户要求关掉**（原来它是"切换单项"）。
            //    现在让它表现为"只选中这一个"，且**不进目录** ——
            //    这是单击变"进入"之后，选中文件夹的主要入口之一。
            if ((mods & ModifierKeys.Control) != 0)
            {
                e.Handled = true;
                var lb = sender as ListBox;
                if (lb != null) lb.SelectedItems.Clear();
                item.IsSelected = true;
                try { item.Focus(); } catch { }
                return;
            }

            // ③ 不按修饰键、点是文件 = WPF 默认行为（选中）。
            // 🚨 原版（第三方分支 v2.4）这里是「单击文件夹 = 直接进入」，
            //    我们**不要**这个行为 —— 用户明确要求保留「双击进入」，单击只做选中。
            //    所以这里直接落到 ④，不做任何拦截。
            // ④ 单击 / 其它情况：完全走 WPF 默认行为（选中）
        }

        /// <summary>从鼠标命中的可视元素往上找 ListBoxItem。</summary>
        private static ListBoxItem FindListBoxItem(DependencyObject d)
        {
            while (d != null)
            {
                if (d is ListBoxItem it) return it;
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        /// <summary>文件列表筛选框变化：只按名称包含关键字过滤，不改动底层集合。</summary>
        private void OnFileFilterChanged(object sender, TextChangedEventArgs e)
        {
            _fileFilter = (FileFilterBox.Text ?? "").Trim();
            try { _filesView?.Refresh(); } catch { }
        }

        // ---------------- 目录分页 / 缓存（2026-10-09） ----------------
        // 一个分享目录可能有几千项，原来的做法有三处叠加的卡：
        //   ① LoadDirAsync 走 ShareDetailAsync / ListDirAsync —— 那是"从第 1 页循环到
        //      第 200 页、把整个目录攒完才返回"，点进去要等十几秒才有东西；
        //   ② 攒完再一次性 Add 几千条进 _files，而集合每变一次都会走 UpdateEmptyStates
        //      （里面 _filesView.Cast<object>().Count() 是**整表枚举**）→ O(n²)；
        //   ③ 退出再进（按 a 返回）把这套重跑一遍。
        // 现在：
        //   · 只取**一页**就先显示，界面里一次只"露" <see cref="DirRevealSize"/> 条；
        //   · 快滑到底部再取下一页（用户要求「每次只加载 10 个，滑到底部再继续加载」）；
        //   · 取到的按 fid 缓存，返回上一层再进来**一个请求都不发**（点「刷新」才重取）；
        //   · UpdateEmptyStates 合并调用。

        /// <summary>界面里一次"露"多少条（用户要求 10）。</summary>
        private const int DirRevealSize = 10;

        /// <summary>一个目录已取到的内容 + 分页游标（缓存里存的就是它）。</summary>
        private sealed class DirCacheEntry
        {
            /// <summary>已取到的条目（已按"目录在前、文件在后"排好）。</summary>
            public List<ShareFileItem> Items = new List<ShareFileItem>();
            /// <summary>下一批要取接口的第几页。</summary>
            public int NextPage = 2;
            /// <summary>接口那边还有没有下一页。</summary>
            public bool HasMore;
            /// <summary>已经放进 <c>_files</c> 的条数。</summary>
            public int RevealCount;
        }

        /// <summary>目录缓存：缓存键 → 已取内容。返回上一层/重进时直接铺开，不再请求。</summary>
        private readonly Dictionary<string, DirCacheEntry> _dirCache =
            new Dictionary<string, DirCacheEntry>();

        /// <summary>当前目录对应的那一份缓存（没加载过时为 null）。</summary>
        private DirCacheEntry _curDir;

        /// <summary>目录加载的"代"：切目录后，旧的异步结果要作废（否则会覆盖新目录）。</summary>
        private int _dirLoadToken;

        /// <summary>正在取下一批（防重入：滚动事件会连发好几条）。</summary>
        private bool _dirLoadingMore;

        /// <summary>
        /// 缓存键。**必须带上模式**：分享的根 fid 和自己网盘的根 fid 都是 "0"，
        /// 不带前缀两个模式会串台（切过去会看到另一个模式的目录）。
        /// </summary>
        private string DirCacheKey(string fid)
        {
            return (ShareMode ? "s:" : "d:") + (fid ?? "");
        }

        /// <summary>取一页目录（分享模式 / 网盘模式走各自接口）。</summary>
        private Task<QuarkClient.DirPage> FetchDirPageAsync(string fid, int page)
        {
            return ShareMode
                ? _client.ShareDetailPageAsync(_pwdId, _stoken, fid, page)
                : _client.ListDirPageAsync(fid, page);
        }

        /// <summary>目录在前、文件在后，同类按名称排序 —— 与 Python Flet 版一致。</summary>
        private static List<ShareFileItem> SortDirEntries(IList<QuarkFileEntry> items)
        {
            return items
                .OrderBy(it => it.IsDir ? 0 : 1)
                .ThenBy(it => it.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(it => new ShareFileItem
                {
                    Fid = it.Fid, Name = it.Name, Size = it.Size,
                    IsDir = it.IsDir, Token = it.Token,
                })
                .ToList();
        }

        /// <summary>把一份目录内容铺到界面上：清列表 → 露第一批 → 重起大小求和。</summary>
        private void ApplyDirView(DirCacheEntry entry)
        {
            _curDir = entry;

            // 换目录时清掉上一次的筛选词，避免用户以为"新目录是空的"
            if (!string.IsNullOrEmpty(_fileFilter))
            {
                _fileFilter = "";
                try { FileFilterBox.Text = ""; } catch { }
            }

            _files.Clear();
            entry.RevealCount = 0;
            ResetDirSizeScan();     // 换目录了：上一轮没算完的不再算
            RevealNextBatch();
            UpdateDirMoreHint();
        }

        /// <summary>把下一批（<see cref="DirRevealSize"/> 条）放进列表。</summary>
        private void RevealNextBatch()
        {
            if (_curDir == null) return;
            int from = _curDir.RevealCount;
            int to = Math.Min(from + DirRevealSize, _curDir.Items.Count);
            if (to <= from) return;

            var freshDirs = new List<ShareFileItem>();
            for (int i = from; i < to; i++)
            {
                var it = _curDir.Items[i];
                _files.Add(it);
                if (it.IsDir) freshDirs.Add(it);
            }
            _curDir.RevealCount = to;

            // 只给"刚露出来这几个文件夹"算大小：几千个目录不可能都算，
            // 而且用户还没滚到的那些他根本看不见。
            QueueDirSizeScan(freshDirs);
        }

        /// <summary>
        /// 滑到底部时继续加载：缓冲里还有没露的直接露，取完了才去要下一页。
        /// </summary>
        private async void LoadMoreItems()
        {
            if (_dirLoadingMore || _curDir == null) return;

            if (_files.Count < _curDir.Items.Count)
            {
                RevealNextBatch();
                UpdateDirMoreHint();
                return;
            }
            if (!_curDir.HasMore) return;      // 真到底了

            _dirLoadingMore = true;
            int token = _dirLoadToken;
            string fid = _curFid;
            try
            {
                var page = await FetchDirPageAsync(fid, _curDir.NextPage);
                if (token != _dirLoadToken) return;   // 期间又切了目录 → 作废
                _curDir.Items.AddRange(SortDirEntries(page.Items));
                _curDir.NextPage++;
                _curDir.HasMore = page.HasMore;
                RevealNextBatch();
                LogDetail("继续加载：本页 " + page.Items.Count + " 项（已取 "
                    + _curDir.Items.Count + " 项）");
            }
            catch (Exception ex)
            {
                Log("加载更多失败: " + ex.Message);
            }
            finally
            {
                _dirLoadingMore = false;
                UpdateDirMoreHint();
            }
        }

        /// <summary>列表底部那行提示：还有没加载完的就说清楚。</summary>
        private void UpdateDirMoreHint()
        {
            // 删除「已显示 N 项 · 下面还有，下滑继续加载」这行提示。
            // 分页加载逻辑本身保留（滚到底自动续），只是不再显示这行文字。
            if (FileMoreHint != null) FileMoreHint.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 文件列表滚动：快到底部就继续加载。
        /// ⚠️ 只在**往下滚**（或内容变高）时判，否则往上滚回顶部也会触发一遍。
        /// </summary>
        private void OnFileListScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            try
            {
                var sv = e.OriginalSource as ScrollViewer;
                if (sv == null) return;
                if (e.VerticalChange < 0) return;
                // 距底部还剩不到一屏就不算"到底"：一屏的高度 ≈ 视口高度，够宽松了
                if (sv.ScrollableHeight - sv.VerticalOffset > sv.ViewportHeight) return;
                LoadMoreItems();
            }
            catch { }
        }

        private async Task LoadDirAsync(string fid)
        {
            SetBusy(true);
            // 置位加载态并立刻刷新一次 —— 让"正在加载…"在请求发出前
            // 就出现，而不是等 SetBusy 的按钮禁用生效后才补上（会闪一下空状态）。
            _loadingDir = true;
            UpdateEmptyStates();
            int token = ++_dirLoadToken;
            try
            {
                // ① 缓存命中 → 直接铺开，**一个请求都不发**（用户反馈"进文件夹再按 a
                //    返回又要重新加载，很卡"）。
                DirCacheEntry hit;
                if (_dirCache.TryGetValue(DirCacheKey(fid), out hit))
                {
                    ApplyDirView(hit);
                    LogDetail("目录来自缓存: 共 " + hit.Items.Count + " 项（未重新请求）");
                    return;
                }

                // ② 只取第一页就先显示，剩下的等滚到底部再取
                var page = await FetchDirPageAsync(fid, 1);
                if (token != _dirLoadToken) return;   // 期间又切了目录 → 这份结果作废

                var entry = new DirCacheEntry
                {
                    Items = SortDirEntries(page.Items),
                    NextPage = 2,
                    HasMore = page.HasMore,
                };
                _dirCache[DirCacheKey(fid)] = entry;
                ApplyDirView(entry);
                LogDetail("目录加载完成: 本页 " + entry.Items.Count + " 项"
                    + (entry.HasMore ? "，下面还有（往下滑继续加载）" : ""));
            }
            catch (Exception ex)
            {
                Log("加载目录失败: " + ex.Message);
            }
            finally
            {
                _loadingDir = false;
                SetBusy(false);
                // _files.CollectionChanged 在清空/填充时已经触发过 UpdateEmptyStates，
                // 但"加载失败且一条都没有"这条路径不会动集合 —— 补一次保证加载态收掉。
                UpdateEmptyStates();
            }
        }

        // ---------------- 文件夹大小（后台递归求和） ----------------

        /// <summary>
        /// 已算出的目录大小缓存（缓存键 → 字节数）。换目录不清，回来时能秒显示。
        /// ⚠️ 键要带模式前缀（见 <see cref="DirSizeKey"/>）—— 分享根与网盘根的 fid 都是 "0"。
        /// </summary>
        private readonly Dictionary<string, long> _dirSizeCache = new Dictionary<string, long>();

        /// <summary>待算大小的文件夹**队列**（随"露出来"的条目增长）。</summary>
        private readonly List<ShareFileItem> _dirSizeQueue = new List<ShareFileItem>();

        /// <summary>队列是否正在被消费（空了就置回 false）。</summary>
        private bool _dirSizeRunning;

        /// <summary>本轮（同一次目录加载）还能发多少次请求。</summary>
        private int _dirSizeBudget;

        /// <summary>当前这轮目录大小扫描的取消源。换目录/重进时取消上一轮。</summary>
        private CancellationTokenSource _dirSizeCts;

        /// <summary>
        /// 单次目录扫描的**请求预算**上限。
        /// </summary>
        /// <remarks>
        /// 递归求和是"每个子目录一次请求"。一个几千文件的分享可能展开成几百次请求 ——
        /// 既慢又容易踩到风控。所以给个预算，超了就停手（剩下的标成「—」而不是 0），
        /// 宁可少算也不要因为一个后台功能把接口打爆。
        /// </remarks>
        private const int DirSizeRequestBudget = 300;

        /// <summary>大小缓存的键：与目录缓存同理，带模式前缀，免得两个模式串台。</summary>
        private string DirSizeKey(string fid)
        {
            return (ShareMode ? "s:" : "d:") + (fid ?? "");
        }

        /// <summary>换目录时重置：停掉上一轮、清空待办、重置预算。</summary>
        private void ResetDirSizeScan()
        {
            try { _dirSizeCts?.Cancel(); } catch { }
            _dirSizeCts = new CancellationTokenSource();
            _dirSizeQueue.Clear();
            _dirSizeBudget = DirSizeRequestBudget;
        }

        /// <summary>
        /// 把要算大小的目录**追加**进队列（已在缓存里的直接填上）。
        /// 原来是"每调一次就取消上一轮、只算这次给的目录"，
        /// 而分页之后目录是**一批批露出来**的 —— 那样每露一批就会把上一批没算完的丢掉。
        /// </summary>
        private void QueueDirSizeScan(IList<ShareFileItem> dirs)
        {
            if (dirs == null || dirs.Count == 0) return;
            foreach (var d in dirs)
            {
                if (d == null || string.IsNullOrEmpty(d.Fid)) continue;
                long cached;
                if (_dirSizeCache.TryGetValue(DirSizeKey(d.Fid), out cached))
                {
                    d.DirSize = cached;
                    continue;
                }
                if (!_dirSizeQueue.Contains(d)) _dirSizeQueue.Add(d);
            }
            if (_dirSizeRunning) return;
            var cts = _dirSizeCts;
            if (cts == null) return;
            _dirSizeRunning = true;
            // 不 await：让它在后台自己跑，界面立刻可用
            var ignored = DrainDirSizeQueueAsync(cts.Token);
        }

        private async Task DrainDirSizeQueueAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _dirSizeQueue.Count > 0)
                {
                    var d = _dirSizeQueue[0];
                    _dirSizeQueue.RemoveAt(0);
                    if (d == null || string.IsNullOrEmpty(d.Fid)) continue;

                    // 预算用完 → 标成"算不了"（显示「—」）。⚠️ 不能落 0：
                    // 几千个目录时预算必然用完，落 0 会让一大片文件夹看起来是空的。
                    if (_dirSizeBudget <= 0)
                    {
                        d.DirSizeFailed = true;
                        continue;
                    }
                    try
                    {
                        long total = await SumDirAsync(d.Fid, ct, () => _dirSizeBudget-- > 0);
                        if (ct.IsCancellationRequested) return;
                        _dirSizeCache[DirSizeKey(d.Fid)] = total;
                        d.DirSize = total;
                    }
                    catch (Exception ex)
                    {
                        if (ct.IsCancellationRequested) return;
                        d.DirSizeFailed = true;
                        LogDetail("计算文件夹大小失败（" + d.Name + "）：" + ex.Message);
                    }
                }
            }
            finally { _dirSizeRunning = false; }
        }

        /// <summary>递归求和：文件夹 = 所有子孙文件字节数之和。</summary>
        /// <param name="takeBudget">每发一次请求调一次；返回 false 表示预算耗尽，停止下钻。</param>
        private async Task<long> SumDirAsync(string fid, CancellationToken ct, Func<bool> takeBudget)
        {
            if (string.IsNullOrEmpty(fid) || ct.IsCancellationRequested) return 0;

            long cached;
            if (_dirSizeCache.TryGetValue(DirSizeKey(fid), out cached)) return cached;
            if (!takeBudget()) return 0;

            var items = ShareMode
                ? await _client.ShareDetailAsync(_pwdId, _stoken, fid)
                : await _client.ListDirAsync(fid);

            long sum = 0;
            foreach (var it in items)
            {
                if (ct.IsCancellationRequested) return sum;
                if (it.IsDir)
                    sum += await SumDirAsync(it.Fid, ct, takeBudget);
                else
                    sum += it.Size;
            }
            _dirSizeCache[DirSizeKey(fid)] = sum;
            return sum;
        }

        private async void OnRefresh(object sender, RoutedEventArgs e)
        {
            // 刷新要重新拉一次目录（网络）→ 按钮就地变成"刷新中…"
            long t0 = BusyClock();
            if (!BeginBusy(DriveRefreshBtn, "刷新中")) return;
            try
            {
                // 「刷新」的语义就是要**绕过缓存**重新取一次
                _dirCache.Remove(DirCacheKey(_curFid));
                await LoadDirAsync(_curFid);
            }
            finally
            {
                await EndBusyAsync(DriveRefreshBtn, t0);
            }
        }

        private async void OnUp(object sender, RoutedEventArgs e)
        {
            if (_fidStack.Count == 0)
            {
                LogDetail("已在根目录");
                return;
            }
            // ⚠️ 重入检查必须放在**改导航状态之前**：否则连点时会把栈 pop 两次却不加载
            long t0 = BusyClock();
            if (!BeginBusy(DriveUpBtn, "返回中")) return;
            try
            {
                _curFid = _fidStack.Pop();
                if (_pathList.Count > 0)
                    _pathList.RemoveAt(_pathList.Count - 1);
                UpdatePathText();
                await LoadDirAsync(_curFid);
            }
            finally
            {
                await EndBusyAsync(DriveUpBtn, t0);
            }
        }

        // 第三方分支 v2.4 曾删掉「双击进入」处理器、改成单击直接进入；
        // 我们**不采纳**那个改动 —— OnFileListDoubleClick 已恢复（见上方），单击只做选中。

        /// <summary>
        /// 右键按下时把光标下的行选中（若该行还没被选中），
        /// 这样「下载选中/导出直链」对准的是用户真正点的文件，
        /// 且保留已有的多选（右键点在已选中的行上时不破坏多选）。
        /// </summary>
        private void OnFileListRightClick(object sender, MouseButtonEventArgs e)
        {
            var el = e.OriginalSource as System.Windows.DependencyObject;
            while (el != null && !(el is System.Windows.Controls.ListBoxItem))
                el = System.Windows.Media.VisualTreeHelper.GetParent(el);
            var lbi = el as System.Windows.Controls.ListBoxItem;
            if (lbi != null && !lbi.IsSelected)
            {
                FileList.SelectedItems.Clear();
                lbi.IsSelected = true;
            }

            // 「免转存下载」只在**分享**模式下有意义：网盘模式的文件本来就在自己网盘里，
            // 不存在"转存"这一步。菜单每次弹出时按当前模式决定它是否出现。
            if (NoSaveDownloadMenuItem != null)
                NoSaveDownloadMenuItem.Visibility = ShareMode
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 「免转存下载」：**不往自己网盘写中转文件**，直接从分享取链下载。
        /// </summary>
        /// <remarks>
        /// 用户要求：「右键解析后的文件应该会有免转存下载选项」。
        /// 原来这个能力是**隐式**的 —— 登录状态下默认走「转存→取直链」（更稳定，但会
        /// 往你网盘写一份、占用空间、还会留下中转垃圾），只有未登录或网盘容量不足时
        /// 才自动降级为免转存。现在给它一个显式入口。
        /// </remarks>
        private async void OnDownloadSelectedNoSave(object sender, RoutedEventArgs e)
        {
            if (!ShareMode)
            {
                Log("「免转存下载」只在分享模式下可用（网盘文件无需转存）。");
                return;
            }
            if (!RequireLogin("下载")) return;
            long t0 = BusyClock();
            if (!BeginEnqueue(DownloadSelBtn)) return;
            try
            {
                await StartDownloadAsync(
                    FileList.SelectedItems.Cast<ShareFileItem>().ToList(), _enqueueCts.Token,
                    forceNoSave: true);
            }
            catch (OperationCanceledException)
            {
                Log("已终止入队（未加入任何下载任务）。");
            }
            finally
            {
                await EndEnqueueAsync(DownloadSelBtn, t0);
            }
        }

        private void OnFileListSelectAll(object sender, RoutedEventArgs e)
        {
            FileList.SelectAll();
        }

        // 原「创建分享链接」入口（右键菜单项 + OnCreateShareSelected）
        // 已按用户要求移除（用户反馈「创建分享链接按钮没有用」）。
        // 同时 `QuarkClient.CreateShareAsync` 也一并删掉了 —— 它当时被"留着当 API 封装"，
        // 但 app 里**没有任何调用点**，属于纯死代码（用户要求"把不需要的死的代码找到并删除"）。
        // 若日后要做「创建分享链接」，照 `/agent/v1/share/create`（入参 fid_list/title/
        // url_type/expired_type）重新实现即可。

        /// <summary>
        /// 「删除网盘文件」：把选中的文件/文件夹从**夸克网盘**删除（进回收站后再清空）。
        /// 移植自 Python Flet 版 `_delete_cloud_selected`。
        /// 限制为「我的网盘」模式：分享模式里的 fid 属于分享者，删不得；
        /// 若用户是在分享模式下转存后又想删中转文件，请用设置里的自动清理或网盘端操作。
        /// </summary>
        private async void OnDeleteCloudSelected(object sender, RoutedEventArgs e)
        {
            if (_client == null) { Log("请先登录"); return; }
            if (ShareMode) { Log("「删除网盘文件」只在「我的网盘」模式下可用（分享里的文件属于分享者）"); return; }

            var selected = FileList.SelectedItems.Cast<ShareFileItem>().ToList();
            if (selected.Count == 0) { Log("没有选中任何文件"); return; }

            var ans = AppDialog.Show(this,
                string.Format("确定从网盘删除选中的 {0} 项吗？\n\n" +
                    "删除后会一并清空回收站，**无法恢复**。", selected.Count),
                "删除网盘文件", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (ans != MessageBoxResult.Yes) return;

            var fids = selected.Select(f => f.Fid).Where(f => !string.IsNullOrEmpty(f)).ToList();
            if (fids.Count == 0) return;

            SetBusy(true);
            try
            {
                LogDetail("正在删除网盘文件 " + fids.Count + " 项…");
                await _client.DeleteAsync(fids);
                try { await _client.PurgeRecycleAsync(fids); } catch { }
                Log("已从网盘删除 " + fids.Count + " 项。");
                await LoadDirAsync(_curFid);   // 刷新列表
                // 删文件会改变已用空间 —— 顺手把账号卡的容量也刷一下（用户要求）
                await RefreshCapacityAsync();
            }
            catch (Exception ex)
            {
                Log("删除网盘文件失败: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ---------------- 目录路径条（面包屑，2026-10-08 改为可点击） ----------------
        // 显示要的是"根 → 当前"这条链上**每一层叫什么、
        // 对应哪个 fid**，而导航状态是分开存的：名字在 _pathList、祖先的 fid 在
        // _fidStack（栈顶 = 上一层）。所以先换算成「层 → {名字, fid}」，再画成
        // 可点的小节 —— 见 BuildCrumbs。

        /// <summary>面包屑的一节。</summary>
        private sealed class Crumb
        {
            /// <summary>显示名（第一段固定是「根目录」）。</summary>
            public string Name;
            /// <summary>这一层对应的 fid；点它就是要跳到这个目录。</summary>
            public string Fid;
            /// <summary>是不是当前所在层（当前层不做成链接）。</summary>
            public bool IsCurrent;
        }

        /// <summary>
        /// 把当前导航状态换算成面包屑（根 → 当前）。
        /// 进子目录时 `_fidStack.Push(上一层 fid)` +
        /// `_pathList.Add(目录名)`，于是：
        ///   · `_fidStack.ToList()` 是"栈顶在前" = [上一层, 上上层, …, 根]；
        ///   · **反转它** = [根, …, 上一层]，正好与 `_pathList` **逐项对齐**
        ///     （两者永远等长：进一层各加一项）。
        /// 所以第 0 段「根目录」用 chain[0]（没进过子目录时就是 "0"），
        /// `_pathList[i]` 这一层用 chain[i+1]，最后一段（当前层）用 `_curFid`。
        /// 解析时 ResetNav 清掉了栈，只有 _curFid 指向
        /// 子目录 → 面包屑就只剩「根目录」一节，而它的 fid 是进入点；
        /// 点不了（本来就是当前层），要出去用「上一级」。
        /// </summary>
        private List<Crumb> BuildCrumbs()
        {
            var chain = _fidStack.ToList();   // 栈顶在前
            chain.Reverse();                  // → 根在最前

            var list = new List<Crumb>();
            list.Add(new Crumb
            {
                Name = "根目录",
                Fid = chain.Count > 0 ? chain[0] : "0",
                IsCurrent = _pathList.Count == 0,
            });
            for (int i = 0; i < _pathList.Count; i++)
            {
                list.Add(new Crumb
                {
                    Name = _pathList[i],
                    Fid = (i + 1 < chain.Count) ? chain[i + 1] : _curFid,
                    IsCurrent = i == _pathList.Count - 1,
                });
            }
            return list;
        }

        /// <summary>
        /// 重建目录路径条。祖先层画成可点的小按钮（悬停高亮，见 BreadcrumbButton 样式），
        /// 当前层画成不可点的普通文字（主色 + 半粗）。
        /// </summary>
        private void UpdatePathText()
        {
            try
            {
                if (DrivePathBar == null) return;
                DrivePathBar.Children.Clear();

                var crumbs = BuildCrumbs();
                var sepStyle = (Style)FindResource("TertiaryText");
                for (int i = 0; i < crumbs.Count; i++)
                {
                    if (i > 0)
                    {
                        DrivePathBar.Children.Add(new TextBlock
                        {
                            Text = "/",
                            Style = sepStyle,
                            VerticalAlignment = VerticalAlignment.Center,
                        });
                    }
                    DrivePathBar.Children.Add(MakeCrumbElement(crumbs[i], i));
                }

                // 路径比可用宽度长时滚到最右 —— 「当前在哪一层」比左边那几节更该看得见。
                // ⚠️ 先 UpdateLayout：刚加完孩子时 ScrollViewer 的 Extent 还是旧的，
                //    直接滚会滚不动（等于什么都没做）。
                if (DrivePathScroll != null)
                {
                    DrivePathScroll.UpdateLayout();
                    DrivePathScroll.ScrollToRightEnd();
                }
            }
            catch { }
        }

        /// <summary>画面包屑的一节：祖先 = 可点按钮；当前层 = 普通文字。</summary>
        private UIElement MakeCrumbElement(Crumb crumb, int level)
        {
            if (crumb.IsCurrent)
            {
                return new TextBlock
                {
                    Text = crumb.Name,
                    Style = (Style)FindResource("BreadcrumbCurrent"),
                };
            }

            var btn = new Button
            {
                Content = crumb.Name,
                Style = (Style)FindResource("BreadcrumbButton"),
                // 存**层号**而不是 fid：跳转要重建 _fidStack / _pathList，那需要知道
                // "跳到第几层"，而层号在 OnBreadcrumbClick 里还能重新核对一遍
                // （重新 BuildCrumbs 比拿着可能过期的 fid 更稳）。
                Tag = level,
            };
            btn.Click += OnBreadcrumbClick;
            return btn;
        }

        /// <summary>
        /// 点面包屑的某一节 = 直接跳到那一层。
        /// </summary>
        private async void OnBreadcrumbClick(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null || !(btn.Tag is int))
                return;

            // 加载中不跳 —— 与 EnterFolder 同一个理由：这段窗口里旧行/旧面包屑还在，
            // 跳一次会和正在回来的那次结果打架（栈和面包屑会被推错）。
            if (_loadingDir) return;

            int level = (int)btn.Tag;
            if (level < 0) return;

            var crumbs = BuildCrumbs();
            if (level >= crumbs.Count) return;
            if (crumbs[level].IsCurrent) return;   // 点的就是当前层，不用动

            // 把导航状态**重建**成"停在目标层"：
            // 祖先链 = crumbs[0..level-1]，逐层 Push 进去（最后压入的正好是最直接的上一层）；
            // _pathList 则是去掉「根目录」那一段后的 crumbs[1..level]。
            _fidStack.Clear();
            for (int i = 0; i < level; i++)
                _fidStack.Push(crumbs[i].Fid);
            _pathList.Clear();
            for (int i = 1; i <= level; i++)
                _pathList.Add(crumbs[i].Name);
            _curFid = crumbs[level].Fid;

            UpdatePathText();
            LogDetail("已跳到目录：" + string.Join(" / ", _pathList));
            await LoadDirAsync(_curFid);
        }

        // ---------------- 下载 ----------------

        /// <summary>
        /// 未登录时使用「必须登录」的功能（下载 / 获取直链等），
        /// **明确弹框提示**并切到登录页。
        /// 原来这些入口只往日志里写一行「请先登录或解析分享」—— 日志在界面下方不起眼，
        /// 用户点了没反应，只会以为程序坏了。
        /// </summary>
        private bool RequireLogin(string action)
        {
            if (_client != null) return true;
            try
            {
                AppDialog.Show(this,
                    action + "需要先登录夸克账号。\n\n"
                    + "登录后可浏览「我的网盘」、转存分享文件、下载文件与获取直链。",
                    "需要登录", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
            // 顺手把用户带到登录页（登录页 = 第 4 个 Tab，索引 3）
            try { Tabs.SelectedIndex = 3; } catch { }
            return false;
        }

        private async void OnDownloadSelected(object sender, RoutedEventArgs e)
        {
            if (!RequireLogin("下载")) return;
            // 下载前要先递归展开目录 + 取直链（网络）→ 按钮就地变成"入队中…"
            // 入队期间按钮保持可点：再点一次 = 终止（见 BeginEnqueue）。
            long t0 = BusyClock();
            if (!BeginEnqueue(DownloadSelBtn)) return;
            try
            {
                await StartDownloadAsync(
                    FileList.SelectedItems.Cast<ShareFileItem>().ToList(), _enqueueCts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("已终止入队（未加入任何下载任务）。");
            }
            finally
            {
                await EndEnqueueAsync(DownloadSelBtn, t0);
            }
        }

        private async void OnDownloadAll(object sender, RoutedEventArgs e)
        {
            if (!RequireLogin("下载")) return;
            long t0 = BusyClock();
            if (!BeginEnqueue(DownloadAllBtn)) return;
            try
            {
                await StartDownloadAsync(_files.ToList(), _enqueueCts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("已终止入队（未加入任何下载任务）。");
            }
            finally
            {
                await EndEnqueueAsync(DownloadAllBtn, t0);
            }
        }

        // ---------------- 获取直链（导出对话框） ----------------

        /// <summary>
        /// 「获取直链」—— 取到链接后弹出对话框，**上方**给出三种导出格式按钮：
        /// aria2c 命令 / curl 命令 / 纯直链；点任一个即按该格式导出（写文件 + 剪贴板）。
        /// <para></para>
        /// <list type="bullet">
        /// <item>原来这个窗口把每条「文件名 Tab 链接」平铺在一个大列表里，用户反馈
        ///   「右边显示了好多直链太乱了不要显示」——现在窗口**不再显示链接文本**，
        ///   只列出将导出的文件名（一行一个），链接本身通过导出格式按钮落到文件/剪贴板。</item>
        /// <item>原来下载页另有一个「导出直链」按钮，功能与这里重复；用户要求删掉它，
        ///   把这个能力合并进本窗口的「上方三个按钮」。</item>
        /// <item>列表右键菜单里的「导出直链」项也一并删除，避免两条入口语义打架。</item>
        /// </list>
        /// <para>注意：取链只做一次，三个格式按钮共用同一份结果 —— 点哪个都能立刻导出，
        /// 不需要重新取链（直链带时效签名，重复取链反而变慢）。</para>
        /// </summary>
        private async void OnFetchLinks(object sender, RoutedEventArgs e)
        {
            if (!RequireLogin("获取直链")) return;
            var selected = FileList.SelectedItems.Cast<ShareFileItem>().ToList();
            if (selected.Count == 0)
            {
                Log("没有选中任何文件");
                return;
            }

            // 取直链是"每个文件一次网络请求"，可能要好几秒；而且取完还要弹窗 ——
            // 期间界面若毫无变化，用户会以为没点上，然后连点导致重复取链。
            // 所以按钮就地变成「获取中」+ 转圈，并在弹窗出现前一直保持。
            long t0 = BusyClock();
            if (!BeginBusy(FetchLinksBtn, "获取中")) return;
            SetBusy(true);
            try
            {
                var files = await CollectFilesAsync(selected, "", 0);
                if (files.Count == 0) { Log("没有可获取链接的文件"); return; }
                if (files.Count > MaxFiles)
                {
                    Log(string.Format("文件数量过多（本次 {0} 个，单次上限 {1} 个），已中止。",
                        files.Count, MaxFiles));
                    return;
                }

                LogDetail("共展开 " + files.Count + " 个文件，正在取直链…");
                var items = await GetDownloadItemsAsync(files);
                if (items == null || items.Count == 0) { Log("未取到任何直链"); return; }

                var withUrl = items.Where(it => !string.IsNullOrEmpty(it.Url)).ToList();
                int noUrl = items.Count - withUrl.Count;
                if (withUrl.Count == 0)
                {
                    Log("全部条目都没有直链（分享可能已失效或限制游客下载）。");
                    return;
                }

                Log(string.Format("取到 {0} 条直链（{1} 条无链接）。", withUrl.Count, noUrl));

                // 先收掉加载态再弹窗，否则转圈会和对话框同时存在
                await EndBusyAsync(FetchLinksBtn, t0);
                t0 = 0;
                ShowLinksDialog(withUrl, noUrl);
            }
            catch (Exception ex)
            {
                Log("获取直链失败：" + ex.Message);
            }
            finally
            {
                SetBusy(false);
                if (t0 != 0) await EndBusyAsync(FetchLinksBtn, t0);
            }
        }

        /// <summary>
        /// 直链导出对话框：**上方**文件管理器式页签（三种直链种类，只负责"选"），
        /// **中间**待导出文件清单（只有文件名，不显示链接文本），
        /// **下方**「导出直链 / 下载文件 / 关闭」。
        /// <para>布局要点（历轮用户反馈累积）：
        /// ① 种类选择器在**最上面**（用户原话「这三个按钮放在上方」）；
        /// ② 不列出 URL（用户原话「显示了好多直链太乱了不要显示」）；
        /// ③ 点击页签**只是切换种类，不导出任何文件** —— 用户原话
        ///    「这三个按钮点击之后不会导出任何文件只有选择作用，真正导出指链是靠下方的按钮」，
        ///    所以这里把「导出」动作**只**挂在下方「导出直链」按钮上（历轮曾经是点页签即导出，已纠正）；
        /// ④ 形态是「顶部文件夹标签」—— 选中页签白底、上沿圆角、底边与列表区连成一体。</para>
        /// </summary>
        private void ShowLinksDialog(List<DownloadItem> items, int noUrlCount)
        {
            var win = new Window
            {
                Owner = this,
                Title = "获取直链",
            };
            // 统一弹窗外观（背景 / 字体 / 尺寸习惯），不再裸用系统默认样式
            DialogChrome.StyleWindow(win, 700, 520);

            var root = new Grid { Margin = new Thickness(22, 18, 22, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 说明
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 页签 + 文件清单
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 底部按钮

            var title = DialogChrome.PageTitle(string.Format("获取直链（{0} 个文件）", items.Count));
            Grid.SetRow(title, 0);
            root.Children.Add(title);

            // ===== 说明：压成一行，说清"页签只选种类、导出靠下方按钮" =====
            // 这一行同时兼作**切换页签时的加载指示区**：切换期间整行原地换成
            // 「转圈 + 正在切换到 xxx…」，切完再换回说明文字（两者尺寸接近，不会跳）。
            // 文案改为「获取直链后可导出为 …（后三者自带 UA 与 Cookie）」，
            // 括号部分用警示红（与下载页底部那行提示同一套语义色）。
            var head = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 12),
            };
            var hintFg = TryFindResource("TextTertiaryBrush") as Brush;
            if (hintFg != null) head.Foreground = hintFg;
            head.Inlines.Add(new Run("获取直链后可导出为 纯直链 / curl 命令 / aria2c 命令 / Motrix 界面 "));
            var hintWarn = new Run("（后三者自带 UA 与 Cookie）");
            var hintWarnBrush = TryFindResource("DangerBrush") as Brush;
            if (hintWarnBrush != null) hintWarn.Foreground = hintWarnBrush;
            head.Inlines.Add(hintWarn);
            if (noUrlCount > 0)
                head.Inlines.Add(new Run(string.Format("（另有 {0} 条未取到链接，已略过）", noUrlCount)));

            var hintBusyText = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var hintBusy = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 12),
            };
            var hintSpin = MakeSpinner();
            hintSpin.Stroke = (TryFindResource("PrimaryBrush") as Brush) ?? Brushes.SteelBlue;
            hintBusy.Children.Add(hintSpin);
            hintBusy.Children.Add(hintBusyText);

            var hintHost = new Grid();
            hintHost.Children.Add(head);
            hintHost.Children.Add(hintBusy);
            Grid.SetRow(hintHost, 1);
            root.Children.Add(hintHost);

            // ===== 直链种类：分段控件 + 滑块指示器 =====
            // 原来的「文件夹页签」改为与全站一致的分段控件，
            // 选中态由一个白底滑块在四项之间平滑滑动表达（SegmentedSlider 附加行为）。
            // ⚠️ 原来「选中页签与下方列表连体」的文件夹观感随之取消 —— 滑块本身
            //    已经足够表达选中态，再叠一条连体边反而脏。
            var segStyle = TryFindResource("SegmentedRadio") as Style;
            var tabPlain = new RadioButton
            {
                Content = "纯直链",
                Style = segStyle,
                GroupName = "LinkFormat",
                IsChecked = true,
                Margin = new Thickness(0),
            };
            var tabCurl = new RadioButton
            {
                Content = "curl 命令",
                Style = segStyle,
                GroupName = "LinkFormat",
                Margin = new Thickness(2, 0, 0, 0),
            };
            var tabAria = new RadioButton
            {
                Content = "aria2c 命令",
                Style = segStyle,
                GroupName = "LinkFormat",
                Margin = new Thickness(2, 0, 0, 0),
            };
            var tabMotrix = new RadioButton
            {
                Content = "Motrix 界面",
                Style = segStyle,
                GroupName = "LinkFormat",
                Margin = new Thickness(2, 0, 0, 0),
            };
            // 「后两者自带 UA 与 Cookie，把它的颜色改红」。
            // curl / aria2c / Motrix 界面 三种格式会把**登录凭证（UA + Cookie）**一起写进导出文件，
            // 属于敏感内容 → 用警示红标出来，和下载页那行提示保持同一套语义色。
            // ⚠️ 这里用的是**本地值**：WPF 里本地值优先级高于样式触发器，
            //    所以 FileTabItem 那个「选中变主色」的触发器不会把红色覆盖掉 ——
            //    正是我们要的（这几个页签任何状态下都是红的）。
            var warnBrush = TryFindResource("DangerBrush") as Brush;
            if (warnBrush != null)
            {
                tabCurl.Foreground = warnBrush;
                tabAria.Foreground = warnBrush;
                tabMotrix.Foreground = warnBrush;
            }
            // 分段控件容器：凹陷底 + 3px 内边距；滑块是容器内第一个子元素（垫底）
            var tabBar = new StackPanel { Orientation = Orientation.Horizontal };
            var segGrid = new Grid();
            SegmentedSlider.SetIsEnabled(segGrid, true);
            var segThumb = new Border
            {
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false,
                Background = TryFindResource("SurfaceBrush") as Brush,
            };
            SegmentedSlider.SetThumb(segGrid, segThumb);
            segGrid.Children.Add(segThumb);
            segGrid.Children.Add(tabBar);

            var segHost = new Border
            {
                Child = segGrid,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(8),
                Background = TryFindResource("SurfaceSunkenBrush") as Brush,
                Padding = new Thickness(3),
                Margin = new Thickness(0, 0, 0, 12),
            };

            tabBar.Children.Add(tabPlain);
            tabBar.Children.Add(tabCurl);
            tabBar.Children.Add(tabAria);
            tabBar.Children.Add(tabMotrix);

            // 互斥 + 默认选中「纯直链」（原首位按钮的位置）
            LinkExporter.Format chosen = LinkExporter.Format.Labeled;
            Action syncTabs = () =>
            {
                tabPlain.IsChecked = chosen == LinkExporter.Format.Labeled;
                tabCurl.IsChecked = chosen == LinkExporter.Format.Curl;
                tabAria.IsChecked = chosen == LinkExporter.Format.Aria2;
                tabMotrix.IsChecked = chosen == LinkExporter.Format.MotrixGui;
            };

            // ===== 切换种类时的加载指示 =====
            // 「切换导出直链种类的那一个要加」加载过程。
            // ⚠️ 说实话：切种类本身**是瞬时的**（只改 chosen 这一个变量，零耗时），
            //    所以这里的加载态不是"真在加载"，而是**给用户一个"点到了、切过去了"的确认** ——
            //    否则点完页签除了高亮变一下，界面没有任何别的动静，容易让人怀疑没生效。
            // 因此刻意停留 MinBusyMs 再收掉，避免一闪而过。
            // 连续快速点不同页签时不阻塞（每次立即生效），只让指示器延长到最后一次点击之后。
            int switchSeq = 0;
            Func<LinkExporter.Format, string, Task> switchTo = async (fmt, name) =>
            {
                chosen = fmt;
                syncTabs();

                int my = ++switchSeq;
                hintBusyText.Text = "正在切换到 " + name + "…";
                head.Visibility = Visibility.Collapsed;
                hintBusy.Visibility = Visibility.Visible;

                await Task.Delay(MinBusyMs);

                if (my != switchSeq) return;   // 期间又切过 → 让最后一次来收尾
                hintBusy.Visibility = Visibility.Collapsed;
                head.Visibility = Visibility.Visible;
            };
            tabPlain.Click += (s, e) => { _ = switchTo(LinkExporter.Format.Labeled, "纯直链"); };
            tabCurl.Click += (s, e) => { _ = switchTo(LinkExporter.Format.Curl, "curl 命令"); };
            tabAria.Click += (s, e) => { _ = switchTo(LinkExporter.Format.Aria2, "aria2c 命令"); };
            tabMotrix.Click += (s, e) => { _ = switchTo(LinkExporter.Format.MotrixGui, "Motrix 界面"); };
            syncTabs();

            var list = DialogChrome.StyledList();
            list.FontSize = 12.5;
            list.SelectionMode = SelectionMode.Extended;
            list.Padding = new Thickness(4, 5, 4, 5);
            var itemStyle = TryFindResource("DialogListItem") as Style;
            if (itemStyle != null) list.ItemContainerStyle = itemStyle;
            // 列表项分两级显示（符合整体 UI 的层次语言）：
            //   路径（含末尾的 \）= 三级文字色，弱化
            //   文件名           = 一级文字色，主体
            // 一整行同色同字重的「路径 \ 文件名」太朴素，是用户说"不符合 UI"的那处。
            var pathBrush = TryFindResource("TextTertiaryBrush") as Brush;
            var nameBrush = TryFindResource("TextPrimaryBrush") as Brush;
            foreach (var it in items)
            {
                var row = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                if (!string.IsNullOrEmpty(it.RelDir) && pathBrush != null)
                {
                    row.Inlines.Add(new Run(it.RelDir + " \\ ") { Foreground = pathBrush });
                }
                var nm = new Run(it.Name);
                if (nameBrush != null) nm.Foreground = nameBrush;
                row.Inlines.Add(nm);
                list.Items.Add(row);
            }
            // 内容区：上面一行是直链种类分段控件，下面是文件清单卡片
            var contentArea = new Grid();
            contentArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid.SetRow(segHost, 0);
            contentArea.Children.Add(segHost);

            var listCard = DialogChrome.Card(list);
            // 列表自带内边距（Padding 4,5），卡片不再重复缩进，保持贴边观感
            listCard.Padding = new Thickness(0);
            Grid.SetRow(listCard, 1);
            contentArea.Children.Add(listCard);

            Grid.SetRow(contentArea, 2);
            root.Children.Add(contentArea);

            // ===== 底部按钮：等宽 =====
            // 「复制全部连接按钮改成导出直链」（按页签选中的种类导出）
            //              「把下载这些文件改成下载文件」
            var copyAll = DialogChrome.PrimaryButton("导出直链");
            var dlBtn = DialogChrome.SecondaryButton("下载文件");
            var close = DialogChrome.SecondaryButton("关闭");
            copyAll.MinWidth = 126;
            dlBtn.MinWidth = 126;
            close.MinWidth = 90;
            var btns = DialogChrome.ButtonRow(copyAll, dlBtn, close);
            // 与列表卡留出明显的间距，避免"按钮压在列表上"（用户反馈「别和这些按钮重叠」）
            btns.Margin = new Thickness(0, 16, 0, 0);
            Grid.SetRow(btns, 3);
            root.Children.Add(btns);

            win.Content = root;

            // 导出动作只在这里发生：读页签当前选中的种类。
            copyAll.Click += (s, e) => ExportLinksFromDialog(win, items, chosen);
            dlBtn.Click += (s, e) =>
            {
                win.Close();
                // 直接复用已取到的链接入队（避免二次取链）
                StartDownloadFromItems(items);
            };
            close.Click += (s, e) => win.Close();

            win.ShowDialog();
        }

        /// <summary>
        /// 在「获取直链」对话框里按选定格式导出（**只写桌面文件**）。
        /// 与旧的「导出直链」按钮共用同一套 LinkExporter 与落盘逻辑。
        /// ⚠️ 用户明确要求：**不要再复制到剪贴板**（原话「无论是导出直链还是导出
        /// cookie都不要复制到粘贴板上」）—— 剪贴板是用户的私有区域，工具不该悄悄占用。
        /// </summary>
        private void ExportLinksFromDialog(Window owner, List<DownloadItem> items,
            LinkExporter.Format format)
        {
            if (_client == null) { Log("请先登录或解析分享"); return; }

            var list = new List<LinkExporter.Item>();
            int skipped = 0;
            foreach (var it in items)
            {
                if (string.IsNullOrEmpty(it.Url)) { skipped++; continue; }
                string rel = string.IsNullOrEmpty(it.RelDir)
                    ? it.Name
                    : Path.Combine(it.RelDir, it.Name);
                list.Add(new LinkExporter.Item { Url = it.Url, OutputPath = rel });
            }

            string text = LinkExporter.Build(list, QuarkConstants.DlUa,
                _client.CookieStr, QuarkConstants.Referer, format,
                // 必须是绝对路径：Motrix 是独立进程，
                // 它把 "." 解析成（实测落到了 D:\motrix\ 安装目录），
                // 不是用户执行命令时所在的目录。curl/aria2c 没这问题（用户自己在 shell 里跑）。
                absoluteSaveDir: _settings.OutDir);
            if (string.IsNullOrEmpty(text))
            {
                Log("没有可导出的直链");
                return;
            }

            string outFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "GeZi-直链导出-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            try
            {
                File.WriteAllText(outFile, text, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log("写导出文件失败：" + ex.Message);
                outFile = null;
            }

            Log(string.Format("已导出 {0} 条直链（格式：{1}），跳过 {2} 条无链接项。",
                list.Count, format, skipped));
            if (outFile != null)
                Log("导出文件：" + outFile);
            Log("注意：直链有时效，请尽快使用。");

            AppDialog.Show(owner,
                string.Format("已导出 {0} 条直链（{1}）。\n\n文件：\n{2}\n\n" +
                    "注意：直链带签名有时效，请尽快交给下载器使用。",
                    list.Count, FormatName(format),
                    outFile ?? "（写入失败）"),
                "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>导出种类的显示名（日志与提示里用）。</summary>
        private static string FormatName(LinkExporter.Format format)
        {
            switch (format)
            {
                case LinkExporter.Format.Aria2: return "aria2c 命令";
                case LinkExporter.Format.Curl: return "curl 命令";
                case LinkExporter.Format.MotrixGui: return "Motrix 界面";
                case LinkExporter.Format.Labeled: return "纯直链";
                default: return "纯直链";
            }
        }

        // ---------------- 连接数按文件大小比例分配 ----------------
        // Python Flet 版有「智能调度」：实际线程数 = min(下载线程数, 总连接预算 / 本批任务数)
        // —— 也就是**多个文件平分**总预算。C# 版当初删掉「全局连接预算」设置项时，
        // 连"多文件共享总预算"这件事一起没了，变成每个任务都开满自己的线程数
        // （用户把单文件并发拉到 512、同时跑 3 个 = 1536 条连接，而夸克是按账号限总连接数的）。
        // 「不要平均分配了，改成比例分配，按文件大小，文件越大分配越多，
        // 文件越小分配越少，但有最低下限，不然别让小文件根本下不了了」。
        // 具体算法在 GeZi.Core.Download.ThreadAllocator（纯函数，可单独测试）。

        /// <summary>总连接预算。见 <see cref="GeZi.Core.Download.ThreadAllocator.DefaultBudget"/>。</summary>
        private const int DownloadConnBudget = GeZi.Core.Download.ThreadAllocator.DefaultBudget;

        /// <summary>单个文件的最低连接数。见 <see cref="GeZi.Core.Download.ThreadAllocator.MinPerFile"/>。</summary>
        private const int MinThreadsPerFile = GeZi.Core.Download.ThreadAllocator.MinPerFile;

        /// <summary>
        /// 按**文件大小比例**把总连接预算分给这一批任务，返回与入参等长的线程数数组。
        /// </summary>
        private int[] AllocateThreads(IList<long> sizes)
        {
            return GeZi.Core.Download.ThreadAllocator.Allocate(
                sizes,
                Math.Min(_settings.Threads, GeZi.Core.Download.SegmentedDownloader.MaxRealConcurrency),
                DownloadConnBudget,
                MinThreadsPerFile,
                // 同时最多跑几个 —— 预算约束的是"同时并发"的量级，不是整批的账面总和
                Math.Max(1, _settings.ConcurrentTasks));
        }

        /// <summary>把分配结果写进日志（只在"不是所有任务都一样"时打，避免刷屏）。</summary>
        private void LogAllocation(int[] threads)
        {
            if (threads == null || threads.Length <= 1) return;
            bool uniform = true;
            for (int i = 1; i < threads.Length; i++)
                if (threads[i] != threads[0]) { uniform = false; break; }
            if (uniform)
            {
                LogDetail(string.Format("本批 {0} 个文件，每个 {1} 路并发。",
                    threads.Length, threads[0]));
                return;
            }
            var sb = new StringBuilder();
            for (int i = 0; i < threads.Length && i < 12; i++)
            {
                if (i > 0) sb.Append('、');
                sb.Append(threads[i]);
            }
            if (threads.Length > 12) sb.Append("…");
            LogDetail(string.Format(
                "已按文件大小分配连接数（总预算 {0}）：{1} —— 大文件多分、小文件少分，每文件不低于 {2} 路。",
                DownloadConnBudget, sb, MinThreadsPerFile));
        }

        /// <summary>
        /// 用**已取到的链接**直接建任务入队（不再重新取链）。
        /// 给「获取直链」对话框的「下载这些文件」按钮用。
        /// </summary>
        private void StartDownloadFromItems(List<DownloadItem> items)
        {
            var alloc = AllocateThreads(items.Select(i => i.Size).ToList());
            var jobs = new List<DownloadJob>();
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (string.IsNullOrEmpty(it.Url)) continue;
                string destDir = Path.Combine(_settings.OutDir, it.RelDir ?? "");
                try { Directory.CreateDirectory(destDir); }
                catch (Exception ex) { Log("创建下载目录失败: " + destDir + " — " + ex.Message); continue; }

                string dest = PathUtil.UniquePath(Path.Combine(destDir, PathUtil.SanitizeFileName(it.Name)));
                var item = it;
                jobs.Add(new DownloadJob
                {
                    Name = item.Name, Url = item.Url, Dest = dest,
                    Cookie = _client.CookieStr,
                    Threads = i < alloc.Length ? alloc[i] : _settings.Threads,
                    // 写入方式统一取设置页的全局值（原来靠下载区的「边下边播」勾选框，
                    // 与设置页重复且容易误导，已移除该勾选框）。
                    PartsRoot = string.IsNullOrWhiteSpace(_settings.PartsRoot) ? null : _settings.PartsRoot,
                    LinkRefresher = ct => RefreshLinkAsync(item, ct),
                    // 免转存（从分享直接取链）→ 续传取不到新链，失败文案别承诺"可续传"
                    ResumeCannotRefresh = item.FromShare,
                    Pending = new PendingInfo
                    {
                        PwdId = _pwdId, Stoken = _stoken, Passcode = item.Passcode,
                        Fid = item.Fid, Token = item.Token, FromShare = item.FromShare,
                        RelDir = item.RelDir ?? "", Dest = dest, Size = item.Size,
                        // 自动重解析分享的上下文（stoken 过期时换新凭证用）
                        StartFid = item.StartFid, ShareRelDir = item.ShareRelDir,
                    },
                });
            }
            if (jobs.Count == 0) { Log("没有可下载的条目。"); return; }

            LogAllocation(alloc);
            foreach (var job in jobs)
                _tasks.Add(new TaskItem(job, job.Name));
            Log("已加入下载队列: " + jobs.Count + " 个文件（复用已取链接）");
            Tabs.SelectedIndex = 1;
            RunJobs(jobs);
        }

        private async Task StartDownloadAsync(List<ShareFileItem> selected,
            CancellationToken ct = default(CancellationToken), bool forceNoSave = false)
        {
            if (_client == null)
            {
                Log("请先登录或解析分享");
                return;
            }
            if (selected.Count == 0)
            {
                Log("没有选中任何文件");
                return;
            }

            SetBusy(true);
            try
            {
                ct.ThrowIfCancellationRequested();
                // 把选中的文件夹递归展开成文件列表（保留相对目录）
                var files = await CollectFilesAsync(selected, "", 0, ct);
                if (files.Count == 0)
                {
                    Log("没有要下载的文件");
                    return;
                }
                LogDetail("共展开 " + files.Count + " 个文件，正在取直链…");

                // ---- 文件数保护（对齐 Python 版 check_limit）----
                // 必须在"取直链"之前拦下：一旦进入 GetDownloadItemsAsync，
                // 就会按批调用服务端接口，此时再中止已经打出去一部分请求了，
                // 既浪费额度又可能踩进风控。所以这道检查要尽量靠前。
                if (files.Count > MaxFiles)
                {
                    // 硬上限：直接拒绝，不提供"继续"选项。
                    // 理由与 Python 一致 —— 这个量级已经超出单次任务的合理范围，
                    // 分批做才是对的（用户可以少选一些重复操作）。
                    Log(string.Format(
                        "文件数量过多（本次 {0} 个，单次上限 {1} 个），已中止。请减少选择后分批下载。",
                        files.Count, MaxFiles));
                    AppDialog.Show(this,
                        string.Format(
                            "本次共展开 {0} 个文件，超过单次处理上限 {1} 个。\n\n" +
                            "请减少选择后分批下载（例如先选中一部分再点「下载选中」）。\n\n" +
                            "这是为了避免一次性发出过多取链请求触发服务端风控。",
                            files.Count, MaxFiles),
                        "文件数量过多", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (files.Count > ConfirmThreshold)
                {
                    // 软提醒：数量偏多但仍在上限内，让用户自己确认。
                    // 默认按钮设为"否"，避免连点两次回车就把大批任务放出去。
                    var ans = AppDialog.Show(this,
                        string.Format(
                            "本次共展开 {0} 个文件，数量较多。\n\n" +
                            "确认要一次性全部加入下载队列吗？",
                            files.Count),
                        "确认下载", MessageBoxButton.YesNo, MessageBoxImage.Question,
                        MessageBoxResult.No);
                    if (ans != MessageBoxResult.Yes)
                    {
                        Log(string.Format("已取消（本次 {0} 个文件，用户未确认）", files.Count));
                        return;
                    }
                }

                ct.ThrowIfCancellationRequested();
                var items = await GetDownloadItemsAsync(files, ct, forceNoSave);
                ct.ThrowIfCancellationRequested();

                // 连接数按文件大小比例分配（大文件多分、小文件少分，各不低于下限）
                var alloc = AllocateThreads(items.Select(x => x.Size).ToList());

                var jobs = new List<DownloadJob>();
                for (int i = 0; i < items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var it = items[i];
                    string destDir = Path.Combine(_settings.OutDir, it.RelDir ?? "");
                    try { Directory.CreateDirectory(destDir); }
                    catch (Exception ex) { Log("创建下载目录失败: " + destDir + " — " + ex.Message); continue; }
                    string dest = PathUtil.UniquePath(Path.Combine(destDir, PathUtil.SanitizeFileName(it.Name)));
                    var item = it;   // 闭包捕获：每次循环一个新变量，避免所有回调指向同一个
                    jobs.Add(new DownloadJob
                    {
                        Name = item.Name, Url = item.Url, Dest = dest,
                        Cookie = _client.CookieStr,
                        Threads = i < alloc.Length ? alloc[i] : _settings.Threads,
                        // 分片根目录：留空 = 与目标同级（默认）。非空则分片集中存放，
                        // 下载目录不被 .qparts 污染，也支持分片跨盘（如放 SSD）。
                        PartsRoot = string.IsNullOrWhiteSpace(_settings.PartsRoot) ? null : _settings.PartsRoot,
                        // 直链带时效签名，长任务下到一半会过期。
                        // 给下载器一个"重新取链"的口子，它就能自动换链续传，而不是整个任务失败。
                        LinkRefresher = ct => RefreshLinkAsync(item, ct),
                        // 免转存（从分享直接取链）→ 续传取不到新链，失败文案别承诺"可续传"
                        ResumeCannotRefresh = item.FromShare,
                        // 续传上下文：写进「未完成任务」记录后，程序重启也能重取链接着下。
                        Pending = new PendingInfo
                        {
                            PwdId = _pwdId, Stoken = _stoken,
                            Passcode = item.Passcode,   // 从 item 拿（await 之后不在 UI 线程）
                            Fid = item.Fid, Token = item.Token,
                            FromShare = item.FromShare, RelDir = item.RelDir ?? "",
                            Dest = dest, Size = item.Size,
                            // 自动重解析分享的上下文（stoken 过期时换新凭证用）
                            StartFid = item.StartFid, ShareRelDir = item.ShareRelDir,
                        },
                    });
                }
                if (jobs.Count == 0)
                {
                    Log("没有取得直链（分享者可能关闭直链或游客额度限制）");
                    return;
                }

                LogAllocation(alloc);
                foreach (var job in jobs)
                    _tasks.Add(new TaskItem(job, job.Name));
                Log("已加入下载队列: " + jobs.Count + " 个文件");
                Tabs.SelectedIndex = 1;
                RunJobs(jobs);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task<List<ShareFileItem>> CollectFilesAsync(IEnumerable<ShareFileItem> items,
            string relPrefix, int depth, CancellationToken ct = default(CancellationToken))
        {
            var result = new List<ShareFileItem>();
            foreach (var it in items)
            {
                ct.ThrowIfCancellationRequested();   // 每一层每个条目都查一次 → 终止响应足够快
                if (it.IsDir)
                {
                    if (depth >= 40)
                        continue;
                    List<QuarkFileEntry> children;
                    try
                    {
                        children = ShareMode
                            ? await _client.ShareDetailAsync(_pwdId, _stoken, it.Fid)
                            : await _client.ListDirAsync(it.Fid);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;   // 取消不能被下面的"容错 continue"吞掉
                    }
                    catch
                    {
                        continue;
                    }
                    ct.ThrowIfCancellationRequested();
                    var kids = children.Select(c => new ShareFileItem
                    {
                        Fid = c.Fid, Name = c.Name, Size = c.Size, IsDir = c.IsDir, Token = c.Token,
                    }).ToList();
                    var sub = await CollectFilesAsync(
                        kids, Path.Combine(relPrefix, PathUtil.SanitizeFileName(it.Name)), depth + 1, ct);
                    result.AddRange(sub);
                }
                else
                {
                    result.Add(new ShareFileItem
                    {
                        Fid = it.Fid, Name = it.Name, Size = it.Size, IsDir = false,
                        Token = it.Token, RelDir = relPrefix,
                    });
                }
            }
            return result;
        }

        private async Task<List<DownloadItem>> GetDownloadItemsAsync(List<ShareFileItem> files,
            CancellationToken ct = default(CancellationToken), bool forceNoSave = false)
        {
            if (ShareMode)
            {
                // 用户要求给「免转存下载」一个显式入口。
                // 默认（登录状态下）走「转存→取直链」，会往用户网盘写一份；
                // 免转存则不碰网盘，直接从分享取链 —— 省空间、也不留中转垃圾。
                if (forceNoSave)
                {
                    LogDetail("按你的选择走「免转存」路径：不写入网盘，直接从分享取链。");
                    return await NoSaveGetItemsAsync(files, ct);
                }

                bool loggedIn = !string.IsNullOrEmpty(_settings.Cookie);
                if (loggedIn)
                {
                    LogDetail("已登录，走「转存→取直链」路径（更稳定）");
                    try
                    {
                        return await SaveAndGetItemsAsync(files, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;   // 终止入队：原样上抛，不能被下面的"容量降级"当成失败处理
                    }
                    catch (CapacityDowngradeException)
                    {
                        // 移植自 Python 版：转存遇「网盘容量不足」时**自动降级**为免转存
                        // （不写入网盘、直接从分享取链），而不是让整批任务失败。
                        // 这是很常见的场景：网盘满了但仍想下载公开分享。
                        Log("网盘容量不足，已自动改用「免转存」路径取直链（不写入网盘）。");
                        ct.ThrowIfCancellationRequested();
                        return await NoSaveGetItemsAsync(files, ct);
                    }
                }
                LogDetail("未登录，走「免转存」路径（游客可能受额度限制）");
                return await NoSaveGetItemsAsync(files, ct);
            }
            return await DriveGetItemsAsync(files, ct);
        }

        /// <summary>
        /// 内部信号：转存因网盘容量不足失败，请求上层降级到免转存。
        /// 只用于本类的控制流，不向外暴露。
        /// </summary>
        private sealed class CapacityDowngradeException : Exception
        {
            public CapacityDowngradeException() : base("网盘容量不足") { }
        }

        private async Task<List<DownloadItem>> NoSaveGetItemsAsync(List<ShareFileItem> files,
            CancellationToken ct = default(CancellationToken))
        {
            ct.ThrowIfCancellationRequested();
            // 重解析分享所需的上下文。
            // ⚠️ 必须在**任何 await 之前**取快照：下载准备期间用户可能双击进别的目录，
            // _pathList 会变（提取码同理，下载器回调更不能回头读 PassBox）。
            string shareDir = CurrentShareRelDir();
            string passcode = _passcode;
            string startFid = _startFid;
            var targets = files.Select(f => new LinkTarget { Fid = f.Fid, Token = f.Token, Name = f.Name }).ToList();
            var urlOf = await FetchUrlsWithRetryAsync(targets, fromShare: true, ct: ct);
            var items = new List<DownloadItem>();
            foreach (var f in files)
            {
                if (urlOf.TryGetValue(f.Fid, out string url))
                    items.Add(new DownloadItem
                    {
                        Name = f.Name, Size = f.Size, Url = url, RelDir = f.RelDir,
                        Fid = f.Fid, Token = f.Token, FromShare = true,
                        // f.RelDir 是「相对当前浏览目录」的，拼上 shareDir 才是「相对分享进入点」的完整路径
                        Passcode = passcode, StartFid = startFid,
                        ShareRelDir = Path.Combine(shareDir, f.RelDir ?? ""),
                    });
                else
                    LogDetail("跳过（无直链）: " + f.Name);
            }
            return items;
        }

        /// <summary>
        /// 当前浏览目录在分享内的完整相对路径（相对分享进入点 <see cref="_startFid"/>）。
        /// 数据来源是导航栈里的 <see cref="_pathList"/>（进一层加一个目录名）。
        /// 供自动重解析分享逐级下钻用；调用方必须在 await 前取快照。
        /// </summary>
        private string CurrentShareRelDir()
        {
            var segs = new List<string>();
            foreach (var s in _pathList)
                if (!string.IsNullOrEmpty(s))
                    segs.Add(s);
            return segs.Count == 0 ? "" : Path.Combine(segs.ToArray());
        }

        private async Task<List<DownloadItem>> DriveGetItemsAsync(List<ShareFileItem> files,
            CancellationToken ct = default(CancellationToken))
        {
            ct.ThrowIfCancellationRequested();
            var targets = files.Select(f => new LinkTarget { Fid = f.Fid, Name = f.Name }).ToList();
            var urlOf = await FetchUrlsWithRetryAsync(targets, fromShare: false, ct: ct);
            var items = new List<DownloadItem>();
            foreach (var f in files)
            {
                if (urlOf.TryGetValue(f.Fid, out string url))
                    items.Add(new DownloadItem
                    {
                        Name = f.Name, Size = f.Size, Url = url, RelDir = f.RelDir,
                        Fid = f.Fid, FromShare = false,
                    });
                else
                    LogDetail("无直链: " + f.Name);
            }
            return items;
        }

        private async Task<List<DownloadItem>> SaveAndGetItemsAsync(List<ShareFileItem> files,
            CancellationToken ct = default(CancellationToken))
        {
            var items = new List<DownloadItem>();
            ct.ThrowIfCancellationRequested();
            string toFid = await EnsureFolderAsync(QuarkConstants.DefaultFolder);
            if (string.IsNullOrEmpty(toFid))
            {
                Log("无法创建中转文件夹，转存失败");
                return items;
            }

            for (int i = 0; i < files.Count; i += 50)
            {
                ct.ThrowIfCancellationRequested();   // 每批转存前查一次 → 终止不必等整批跑完
                var batch = files.Skip(i).Take(50).ToList();
                var fids = batch.Select(f => f.Fid).ToList();
                var tokens = batch.Select(f => f.Token).ToList();
                List<string> newFids;
                try
                {
                    newFids = await _client.SaveShareAndGetFidsAsync(fids, tokens, toFid, _pwdId, _stoken, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;   // 终止入队：原样上抛（否则会被下面的 catch 吞成"转存失败"继续跑）
                }
                catch (QuarkApiException ex) when (QuarkClient.IsCapacityMessage(ex.Message))
                {
                    // 容量不足：不用"continue 跳过这批"，而是整批降级 ——
                    // 容量满时后续每批都会失败，继续试只是浪费时间与请求额度。
                    throw new CapacityDowngradeException();
                }
                catch (Exception ex)
                {
                    Log("转存失败: " + ex.Message);
                    continue;
                }

                if (newFids.Count > 0)
                {
                    // 🚨 转存**一旦成功就立刻登记待清理** —— 不能像原来那样等到本批循环
                    // 末尾再加：中途若被「终止入队」打断（下面的 ThrowIfCancellationRequested
                    // 或取链抛取消），就会漏登记，网盘里留下**孤儿中转文件**。
                    // （这类"删除漏登记"是本项目栽过的坑，宁可提前登记。）
                    lock (_cleanupLock)
                        _pendingCleanupFids.AddRange(newFids);

                    ct.ThrowIfCancellationRequested();
                    // 转存后校验：等文件落盘，过滤无效 fid（对应原版 _resolve_saved_fids）
                    newFids = await ResolveSavedFidsAsync(toFid, newFids);

                    // 用转存后的新 fid 取链，名字沿用分享里的原名（只为日志好读）
                    var targets = new List<LinkTarget>();
                    for (int j = 0; j < newFids.Count; j++)
                    {
                        targets.Add(new LinkTarget
                        {
                            Fid = newFids[j],
                            Name = j < batch.Count ? batch[j].Name : newFids[j],
                        });
                    }
                    var urlOf = await FetchUrlsWithRetryAsync(targets, fromShare: false, ct: ct);

                    for (int j = 0; j < newFids.Count && j < batch.Count; j++)
                    {
                        if (urlOf.TryGetValue(newFids[j], out string url))
                            items.Add(new DownloadItem
                            {
                                Name = batch[j].Name, Size = batch[j].Size, Url = url, RelDir = batch[j].RelDir,
                                // 转存后要按"新 fid"取链，所以这里记的是转存后的 fid，不是分享里的
                                Fid = newFids[j], FromShare = false,
                            });
                        else
                            LogDetail("无直链: " + batch[j].Name);
                    }
                }
            }
            return items;
        }

        /// <summary>取链目标：一个待取直链的文件。</summary>
        private sealed class LinkTarget
        {
            public string Fid;
            public string Token;   // 仅「分享免转存」路径需要
            public string Name;    // 仅用于日志
        }

        /// <summary>
        /// 批量取直链 + 多轮重试。对应原版 fetch_links / fetch_share_links。
        /// 相比早先的实现，补了原版有、C# 之前缺的两点：
        ///  1. **失败原因不再被吞掉**。以前是 `catch { links = new List&lt;DownloadLink&gt;(); }`，
        ///     用户只看到「跳过（无直链）」，无从判断是会员额度、风控还是分享被关。
        ///  2. **多一轮"逐个单请求"兜底**。批量接口更易被限流，剩少量文件时逐个问成功率更高。
        /// </summary>
        private async Task<Dictionary<string, string>> FetchUrlsWithRetryAsync(
            List<LinkTarget> targets, bool fromShare, CancellationToken ct = default(CancellationToken))
        {
            ct.ThrowIfCancellationRequested();
            var urls = new Dictionary<string, string>();
            var pending = new List<LinkTarget>(targets);
            var reason = new Dictionary<string, string>();
            int[] delays = { 0, 800, 1600, 3000, 5000 };

            // ---- 先查直链缓存（对应 Python 的 LINK_TTL 复用）----
            // 命中的直接从 pending 里摘掉，不再发请求。
            int cached = 0;
            var miss = new List<LinkTarget>();
            foreach (var t in pending)
            {
                string hit = _linkCache.Get(t.Fid);
                if (hit != null) { urls[t.Fid] = hit; cached++; }
                else miss.Add(t);
            }
            if (cached > 0)
                LogDetail(string.Format("{0} 个文件命中直链缓存（10 分钟内有效），跳过取链。", cached));
            pending = miss;

            for (int round = 0; round < delays.Length && pending.Count > 0; round++)
            {
                ct.ThrowIfCancellationRequested();
                if (round > 0)
                {
                    LogDetail(string.Format("{0} 个文件未返回直链，重试第 {1} 轮…", pending.Count, round));
                    await Task.Delay(delays[round], ct);
                }
                var next = new List<LinkTarget>();
                for (int i = 0; i < pending.Count; i += 20)
                {
                    ct.ThrowIfCancellationRequested();
                    var batch = pending.Skip(i).Take(20).ToList();
                    List<DownloadLink> links = null;
                    string err = null;
                    try { links = await FetchBatchAsync(batch, fromShare); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { err = ex.Message; }
                    if (err != null)
                    {
                        foreach (var t in batch)
                            reason[t.Fid] = "接口报错: " + err;
                    }

                    var got = new Dictionary<string, string>();
                    if (links != null)
                    {
                        foreach (var l in links)
                        {
                            if (!string.IsNullOrEmpty(l.Fid) && !string.IsNullOrEmpty(l.DownloadUrl))
                                got[l.Fid] = l.DownloadUrl;
                        }
                    }

                    foreach (var t in batch)
                    {
                        string url;
                        if (got.TryGetValue(t.Fid, out url))
                        {
                            urls[t.Fid] = url;
                        }
                        else
                        {
                            if (err == null)
                                reason[t.Fid] = "接口未返回该文件的 download_url";
                            next.Add(t);
                        }
                    }
                }
                pending = next;
            }

            // 逐个单请求兜底：批量接口更容易被限流，剩少量文件时单独问成功率明显更高。
            if (pending.Count > 0 && pending.Count <= 30)
            {
                LogDetail(string.Format("对剩余 {0} 个文件逐个单独重试…", pending.Count));
                var still = new List<LinkTarget>();
                foreach (var t in pending)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(150, ct);
                    try
                    {
                        var one = await FetchBatchAsync(new List<LinkTarget> { t }, fromShare);
                        var hit = one == null ? null : one.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl));
                        if (hit != null)
                        {
                            urls[t.Fid] = hit.DownloadUrl;
                        }
                        else
                        {
                            reason[t.Fid] = "单个请求仍未返回 download_url";
                            still.Add(t);
                        }
                    }
                    catch (Exception ex)
                    {
                        reason[t.Fid] = "单个请求报错: " + ex.Message;
                        still.Add(t);
                    }
                }
                pending = still;
            }

            if (pending.Count > 0)
            {
                Log(string.Format("仍有 {0} 个文件未取得直链，逐条原因:", pending.Count));
                foreach (var t in pending)
                {
                    string r;
                    Log(string.Format("  - {0}  ->  {1}", t.Name, reason.TryGetValue(t.Fid, out r) ? r : "未知"));
                }
                LogDetail("判断: 接口报错或 code 非 0 多为账号/权限/限流；"
                    + "「无 download_url」多为会员额度或文件被风控；"
                    + "分享直链还可能是分享者已关闭直链，或超出游客可获取的大小限制。");
            }

            // ---- 写回缓存：本次取到的链在 TTL 内可被后续操作复用 ----
            foreach (var kv in urls)
                _linkCache.Put(kv.Key, kv.Value);

            return urls;
        }

        private Task<List<DownloadLink>> FetchBatchAsync(List<LinkTarget> batch, bool fromShare)
        {
            if (fromShare)
            {
                return _client.GetShareDownloadAsync(
                    batch.Select(t => t.Fid).ToList(),
                    batch.Select(t => t.Token ?? "").ToList(),
                    _pwdId, _stoken);
            }
            return _client.GetDownloadAsync(batch.Select(t => t.Fid).ToList());
        }

        /// <summary>
        /// 重新获取某个文件的直链。下载中直链签名过期时由下载器回调触发。
        /// 按这个文件当初走的是哪条路径取链（分享免转存 / 我的网盘），保证刷新方式一致。
        /// </summary>
        private async Task<string> RefreshLinkAsync(DownloadItem item, CancellationToken ct)
        {
            if (item == null || string.IsNullOrEmpty(item.Fid))
                return null;

            // 缓存里的链可能仍然有效（例如只是瞬时网络抖动触发了重取），
            // 先看一眼能省一次接口调用。但注意：下载器**是因为旧链失败才来刷新**的，
            // 所以这里必须**绕过命中缓存**，否则会把同一条坏链再喂回去 → 死循环。
            // 因此不用 _linkCache.Get，而是直接取新链后刷新缓存。
            _linkCache.Invalidate(item.Fid);

            List<DownloadLink> links;
            if (item.FromShare)
            {
                // stoken 过期时自动重解析分享：换新 stoken + 新
                // share_fid_token 再取链。写回 _stoken / item.Token ——
                // 当前会话后续取链都用新值（下载器回调在线程池线程，不能读 PassBox，
                // 所以提取码从 item 字段拿）。
                string url = await GetShareLinkWithReResolveAsync(
                    _client, item.Fid, item.Token, _pwdId, _stoken, item.Passcode,
                    item.StartFid, item.ShareRelDir,
                    (freshStoken, freshToken) =>
                    {
                        _stoken = freshStoken;
                        item.Token = freshToken;
                    }, ct).ConfigureAwait(false);
                if (url != null)
                    _linkCache.Put(item.Fid, url);   // 新链写回缓存，供后续同类操作复用
                return url;
            }
            else
            {
                links = await _client.GetDownloadAsync(new List<string> { item.Fid }).ConfigureAwait(false);
            }

            if (links == null)
                return null;
            var hit = links.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl));
            string u = hit == null ? null : hit.DownloadUrl;
            if (u != null)
                _linkCache.Put(item.Fid, u);   // 新链写回缓存，供后续同类操作复用
            return u;
        }

        /// <summary>转存后校验：等文件异步落盘，过滤不在目标目录里的 fid（对应原版 _resolve_saved_fids）。</summary>
        private async Task<List<string>> ResolveSavedFidsAsync(string toFid, List<string> fids)
        {
            int[] delays = { 0, 1000, 1500, 2000, 2500, 3000 };
            for (int round = 0; round < delays.Length; round++)
            {
                if (round > 0)
                    await Task.Delay(delays[round]);
                try
                {
                    var items = await _client.ListDirAsync(toFid, 1);
                    var existing = new HashSet<string>(items.Select(it => it.Fid));
                    if (fids.All(f => existing.Contains(f)))
                        return fids;
                }
                catch { }
            }
            return fids;
        }

        private async Task<string> EnsureFolderAsync(string name)
        {
            string path = "/" + name;
            try
            {
                string fid = await _client.PathFidAsync(path);
                if (!string.IsNullOrEmpty(fid))
                    return fid;
                return await _client.MkdirAsync(path);
            }
            catch (Exception ex)
            {
                LogDetail("中转文件夹处理失败: " + ex.Message);
                return null;
            }
        }

        private async void RunJobs(List<DownloadJob> jobs)
        {
            var progress = new Progress<JobUpdate>(OnJobUpdate);
            // 把 scheduler 抓到局部变量再 await。
            // 直接写 await _scheduler.RunAllAsync(...) 的话，await 期间若 _scheduler
            // 被替换（见 RecreateScheduler），语义就变成"用可能已变化的字段"，
            // 且和已经开始的这批不是同一个对象 —— 限流账目会错乱。
            // 抓成局部引用后，本批任务从头到尾只用同一个 scheduler，清晰且安全。
            var scheduler = _scheduler;
            _batchRunning = true;
            // 新任务已入队 → 合并按钮立刻切成「暂停全部」（不用等第一次进度上报）
            UpdatePauseAllButton(force: true);
            // 立刻开启保活，别等 30 秒的定时器 —— 用户可能点完就去睡觉了
            RefreshSleepGuard();
            try
            {
                await scheduler.RunAllAsync(jobs, progress, CancellationToken.None);
            }
            catch (ObjectDisposedException ex)
            {
                // 兜底：即便将来又有别的路径 Dispose 了 scheduler，也不该让用户
                // 看到"已释放该信号量"这种无信息量的英文异常名。
                LogDetail("下载被中断：下载器被重新初始化（请重新开始该任务，已下载部分会续传）。" + ex.Message);
            }
            catch (Exception ex)
            {
                Log("下载异常: " + ex.Message);
            }
            finally
            {
                _batchRunning = false;
            }
            LogDetail("本批下载结束");

            // 整批结束时在日志里汇总一次（原托盘气泡通知已随"后台功能"一起移除）。
            try
            {
                // ⚠️ 不在任务列表里的（更新包）**不参与**这批汇总 —— 它有自己的收尾提示；
                //    若把它算进来，会被当成"未完成"，打出一句假的「下载失败」。
                int ok = 0, fail = 0;
                foreach (var j in jobs)
                {
                    var t = _tasks.FirstOrDefault(x => x.Job == j);
                    if (t == null) continue;
                    if (t.State == JobState.Completed) ok++;
                    else fail++;
                }
                if (ok + fail == 0)
                {
                    // 整批都不在任务列表里 → 不打汇总
                }
                else if (ok > 0 && fail == 0)
                    Log(string.Format("全部完成：{0} 个文件已下载完成。", ok));
                else if (ok > 0)
                    Log(string.Format("下载结束：{0} 个完成，{1} 个未完成。", ok, fail));
                else if (fail > 0)
                    Log(string.Format("下载失败：{0} 个文件未能完成，请查看任务列表。", fail));
            }
            catch { }

            await CleanupTransferredAsync();
        }

        private async Task CleanupTransferredAsync()
        {
            List<string> fids;
            lock (_cleanupLock)
            {
                if (_pendingCleanupFids.Count == 0)
                    return;
                fids = new List<string>(_pendingCleanupFids);
                _pendingCleanupFids.Clear();
            }
            if (!_settings.AutoClean)
            {
                LogDetail("已保留网盘转存文件 " + fids.Count + " 个（可在设置里关闭自动清理）");
                return;
            }
            if (_client == null)
                return;
            try
            {
                await _client.DeleteAsync(fids);
                await _client.PurgeRecycleAsync(fids);
                LogDetail("已清理网盘转存文件 " + fids.Count + " 个");
            }
            catch (Exception ex)
            {
                LogDetail("清理转存文件失败（不影响下载）: " + ex.Message);
            }
        }

        private void OnJobUpdate(JobUpdate u)
        {
            var item = _tasks.FirstOrDefault(t => t.Job == u.Job);
            if (item == null)
                return;

            // ⚠️ 用户已按暂停、但下载器还在收尾时，**忽略核心报来的 Downloading**。
            // 核心的 JobProgressAdapter 永远报 Downloading（它只是转发进度，不关心暂停），
            // 不拦的话刚设好的 Paused 会被下一帧覆盖 → 图标先变三角又变回竖杠。
            // 进度数据本身照常更新（这样能看到速度渐降到 0，文案随之从「暂停中…」变「已暂停」）。
            bool keepPaused = item.PauseRequested && u.State == JobState.Downloading;
            if (!keepPaused)
            {
                item.State = u.State;
                // 走到终态/被恢复时清掉暂停标志，避免它一直挂着影响后续状态显示
                if (u.State != JobState.Paused)
                    item.PauseRequested = false;
            }
            item.Error = u.Error ?? "";
            if ((u.State == JobState.Failed || u.State == JobState.Cancelled) && !string.IsNullOrEmpty(u.Error))
            {
                Log("[" + item.Name + "] " + (u.State == JobState.Failed ? "失败" : "取消") + ": " + u.Error);
            }
            if (u.Progress != null)
            {
                item.Percent = u.Progress.Percent;
                // 暂停期间**冻结速度**：核心层仍会上报在途请求的残余速度
                // （闸门式暂停，见 TaskItem.PauseRequested 注释），照收就会把刚置的 0
                // 又拉回一个非零值 → 界面又变成"速度慢慢降"。用户要求暂停即时生效 → 不收。
                // 进度（Percent/Done）照常更新：那是真实已下载字节，且暂停后本来就不再增长。
                if (!keepPaused) item.Speed = u.Progress.Speed;
                item.Done = u.Progress.Done;
                item.Total = u.Progress.Total;
                // 阶段说明（如「正在合并分片」）。非下载态一律清空，
                // 免得完成/失败后还挂着「正在合并分片」这种残留文案。
                item.Phase = u.State == JobState.Downloading ? u.Progress.Phase : null;
            }

            // ---- 历史记录：终态各记一条（只记一次）----
            if (!item.HistoryRecorded
                && (u.State == JobState.Completed || u.State == JobState.Failed || u.State == JobState.Cancelled))
            {
                item.HistoryRecorded = true;
                string status = u.State == JobState.Completed ? "完成"
                    : u.State == JobState.Failed ? "失败" : "已取消";
                long sizeBytes = item.Total > 0 ? item.Total : item.Done;
                HistoryStore.AddHistory(item.Name, Util.FormatSize(sizeBytes), item.Job.Dest, status);
            }

            // ---- 未完成任务记录：仅在状态集合发生变化时同步（避免每帧进度都写盘）----
            SyncPending();

            // 合并按钮的文案/图标要跟着任务状态走（有在跑的 → 暂停全部；否则 → 恢复全部）
            UpdatePauseAllButton();

        }

        /// <summary>上次写入 pending 时的状态签名，用于避免高频重复写盘。</summary>
        private string _pendingSig;

        /// <summary>
        /// 把当前"未完成"的任务（排队/下载/暂停）写进持久化记录，
        /// 已完成的从记录里移除。供下次启动提示"有 N 个未完成任务"。
        /// 节流：只有"未完成任务的名字 + 状态"组合变了才真正写盘 ——
        /// OnJobUpdate 每 500ms 就来一次，无条件写盘等于持续小额 IO。
        /// </summary>
        private void SyncPending()
        {
            try
            {
                var sb = new StringBuilder();
                var list = new List<PendingTask>();
                foreach (var t in _tasks)
                {
                    // 更新包下载不持久化：跨重启续传没有意义，且避免 Pending 信息残缺
                    if (t.Job.SkipPersist)
                        continue;
                    if (t.State == JobState.Queued || t.State == JobState.Downloading
                        || t.State == JobState.Paused)
                    {
                        sb.Append(t.Name).Append('|').Append(t.State).Append(';');
                        var pi = t.Job.Pending;
                        list.Add(new PendingTask
                        {
                            Name = t.Name,
                            Dest = t.Job.Dest,
                            Url = t.Job.Url,
                            Fid = pi?.Fid,
                            Token = pi?.Token,
                            FromShare = pi?.FromShare ?? false,
                            Size = t.Total,
                            StreamPlay = true,      // 共写模式已删除，恒可边下边播
                            StreamPlayMode = true,
                            Status = t.StatusText,
                            // ---- 续传所需（重启后重取链）----
                            PwdId = pi?.PwdId,
                            Stoken = pi?.Stoken,
                            Passcode = pi?.Passcode,
                            RelDir = pi?.RelDir,
                            // 自动重解析分享的上下文（stoken 过期自愈用）
                            StartFid = pi?.StartFid,
                            ShareRelDir = pi?.ShareRelDir,
                        });
                    }
                }
                string sig = sb.ToString();
                if (sig == _pendingSig)
                    return;
                _pendingSig = sig;
                HistoryStore.SetPending(list);
            }
            catch { }
        }

        // ---------------- 任务管理 ----------------

        /// <summary>
        /// 工具栏那个合并后的「暂停全部 / 恢复全部」按钮 —— 按当前任务状态决定做什么。
        /// </summary>
        /// <remarks>
        /// 用户反馈「暂停全部和恢复全部可以弄成一个按钮」。原来两个独立按钮
        /// 在任何时刻都只有一个是"有意义"的（没在下载时点「暂停全部」什么也不会发生），
        /// 合并成一个会自己换文案/图标的按钮更省地方也更清楚。
        /// </remarks>
        private void OnPauseAllOrResume(object sender, RoutedEventArgs e)
        {
            bool anyRunning = _tasks.Any(t => t.State == JobState.Downloading || t.State == JobState.Queued);
            if (anyRunning)
            {
                foreach (var t in _tasks)
                {
                    if (t.State == JobState.Downloading || t.State == JobState.Queued)
                    {
                        t.Job.PauseJob();
                        // 与单个暂停同理：先置标志再改状态，否则进度上报会把 Paused 覆盖掉
                        t.PauseRequested = true;
                        t.State = JobState.Paused;
                    }
                }
            }
            else
            {
                foreach (var t in _tasks)
                {
                    if (t.State == JobState.Paused)
                    {
                        t.PauseRequested = false;
                        t.Job.ResumeJob();
                        t.State = JobState.Downloading;
                    }
                }
            }
            UpdatePauseAllButton(force: true);
        }

        /// <summary>上次刷新「暂停全部/恢复全部」按钮时用的状态签名，避免每帧重建 Content。</summary>
        private string _pauseAllSig;

        /// <summary>
        /// 刷新合并按钮的文案与图标：有任务在跑 → 「⏸ 暂停全部」；否则有暂停的 → 「▶ 恢复全部」；
        /// 都没有 → 置灰（点了也没意义）。
        /// </summary>
        /// <param name="force">true = 忽略签名缓存强制重建（按钮刚被点击时用）。</param>
        private void UpdatePauseAllButton(bool force = false)
        {
            if (PauseAllBtn == null) return;

            bool anyRunning = _tasks.Any(t => t.State == JobState.Downloading || t.State == JobState.Queued);
            bool anyPaused = _tasks.Any(t => t.State == JobState.Paused);
            string sig = anyRunning ? "run" : (anyPaused ? "pause" : "idle");
            if (!force && sig == _pauseAllSig) return;
            _pauseAllSig = sig;

            PauseAllBtn.IsEnabled = anyRunning || anyPaused;
            if (anyRunning)
            {
                PauseAllBtn.Content = BuildToolIconText("IconPause", "暂停全部", 2.0);
                PauseAllBtn.ToolTip = "暂停所有正在下载 / 排队的任务";
            }
            else if (anyPaused)
            {
                PauseAllBtn.Content = BuildToolIconText("IconPlay", "恢复全部", 1.6);
                PauseAllBtn.ToolTip = "恢复所有已暂停的任务";
            }
            else
            {
                // 没有任务时按钮置灰。文案固定显示「暂停全部」——
                // 显示「恢复全部」会让人以为"有东西被暂停了、可以恢复"。
                PauseAllBtn.Content = BuildToolIconText("IconPause", "暂停全部", 2.0);
                PauseAllBtn.ToolTip = "当前没有任务";
            }
        }

        /// <summary>
        /// 复查「保活」：有任务在**活跃地**下载就阻止系统空闲睡眠，否则释放。
        /// 用户反馈「中午开着任务后关显示器离开了，下午再看任务全部停了」——
        /// 系统按空闲计划睡眠会掐断所有连接，任务自然全灭。
        /// 状态是 Downloading/Queued，**且**满足下面任一条：
        ///   · 最近 <see cref="SleepGuardStaleMinutes"/> 分钟内进度**真的前进过**；
        ///   · 正在合并分片（<see cref="TaskItem.Phase"/> 非空）——
        ///     合并期间 `Done` 不再增长，但它确实在干活，**不能当卡死**。
        /// 任务**卡死**时（worker 卡在掐不断的调用里、
        /// `Task.WhenAll` 永不返回）状态会永远停在 Downloading。只看状态就会一直保活、
        /// 电脑整夜不睡。加了超时后卡死也能正常睡 —— 未完成的任务下次启动会由
        /// 「未完成的任务」对话框提醒用户（`SyncPending` 收 Downloading 状态）。
        /// ⚠️ 必须在 UI 线程调用（<see cref="SleepGuard"/> 是线程级的）。
        /// </summary>
        private void RefreshSleepGuard()
        {
            try
            {
                var now = DateTime.UtcNow;
                var staleAfter = TimeSpan.FromMinutes(SleepGuardStaleMinutes);

                bool active = _tasks.Any(t =>
                    (t.State == JobState.Downloading || t.State == JobState.Queued)
                    && (!string.IsNullOrEmpty(t.Phase) || (now - t.LastProgressUtc) < staleAfter));

                SleepGuard.Set(active);
            }
            catch { /* 保活失败绝不能影响下载本身 */ }
        }

        /// <summary>工具栏按钮的「图标 + 文字」内容（与 XAML 里手写的结构一致）。</summary>
        private UIElement BuildToolIconText(string iconKey, string text, double strokeThickness)
        {
            var geo = TryFindResource(iconKey) as System.Windows.Media.Geometry;
            var path = new ShapePath
            {
                Data = geo,
                Stretch = Stretch.Uniform,
                Width = 12,
                Height = 12,
                VerticalAlignment = VerticalAlignment.Center,
                StrokeThickness = strokeThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
            var brush = TryFindResource("TextSecondaryBrush") as Brush;
            if (brush != null) path.Stroke = brush;

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(path);
            sp.Children.Add(new TextBlock
            {
                Text = text,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            return sp;
        }

        private void OnClearDone(object sender, RoutedEventArgs e)
        {
            for (int i = _tasks.Count - 1; i >= 0; i--)
            {
                var s = _tasks[i].State;
                // 含 Cancelling：用户点了取消就说明不想要它了，
                // 不该逼他等下载器慢慢退出之后才能清掉。
                if (s == JobState.Completed || s == JobState.Failed
                    || s == JobState.Cancelled || s == JobState.Cancelling)
                    _tasks.RemoveAt(i);
            }
            SyncPending();
            UpdatePauseAllButton(force: true);
        }

        // ---------------- 任务列表右键菜单（单任务操作） ----------------
        // 移植自 Python Flet 版的 _pause_single / _resume_single / _cancel_single /
        // _task_del_local / _file_menu_popup（右键菜单）。
        // 为什么必须有：此前 C# 只有「全部暂停/恢复/取消」，一个跑得慢的任务
        // 会拖住整批，用户没法单独处理它 —— 这是下载工具的基本操作。

        /// <summary>
        /// 右键按下时把光标下的任务选中，这样菜单动作能对准用户真正点的那一行。
        /// 若该处没有任务（点到空白），保持原选中不变。
        /// </summary>
        private void OnTaskListRightClick(object sender, MouseButtonEventArgs e)
        {
            var lbi = FindListBoxItem(e.OriginalSource as DependencyObject);
            if (lbi == null) return;

            // ⚠️ 原来无条件 `TaskList.SelectedItem = ...` ——
            //    这句会**把整个选择替换成这一个**，于是"Shift 离散多选之后再右键"
            //    多选立刻被清掉，菜单里的暂停/删除只作用到一个文件
            //    （用户反馈：「根本无法完成多选的情况下右键使用功能」）。
            //    现在：点到**已选中**的行 → 保持整份多选不动；点到**没选中**的行 →
            //    才把它单独选上（否则右键一个没选中的行却对别的行生效，很反直觉）。
            if (lbi.IsSelected) return;
            TaskList.SelectedItems.Clear();
            lbi.IsSelected = true;
        }

        /// <summary>双击任务行 = 打开文件（与「打开」按钮同义）。</summary>
        private void OnTaskListDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var item = TaskList.SelectedItem as TaskItem;
            if (item != null)
                OpenTaskFileCore(item);
        }

        /// <summary>
        /// 从右键菜单的动作里取出**要操作的全部任务**。
        /// 原来只取 `SelectedItem`（一个）—— 于是"Shift 离散多选之后
        /// 右键暂停/删除"只会作用于其中一个（用户反馈）。现在把选中的全取出来；
        /// 没选中任何行时回退到菜单项自己的 Tag（行内按钮那条路径）。
        /// </summary>
        private List<TaskItem> TasksFromMenu(object sender)
        {
            var list = TaskList.SelectedItems.OfType<TaskItem>().ToList();
            if (list.Count > 0) return list;
            var one = (sender as System.Windows.Controls.MenuItem)?.Tag as TaskItem;
            return one != null ? new List<TaskItem> { one } : new List<TaskItem>();
        }

        /// <summary>从右键菜单的动作里取出目标任务：优先用选中的，回退按钮 Tag。</summary>
        private TaskItem TaskFromMenu(object sender)
        {
            return (TaskList.SelectedItem as TaskItem)
                   ?? ((sender as System.Windows.Controls.MenuItem)?.Tag as TaskItem);
        }

        private void OnTaskPause(object sender, RoutedEventArgs e)
        {
            foreach (var t in TasksFromMenu(sender)) PauseTask(t);
        }

        private void OnTaskResume(object sender, RoutedEventArgs e)
        {
            foreach (var t in TasksFromMenu(sender)) ResumeTask(t);
        }

        /// <summary>
        /// 任务行上那个**图标式播放/暂停切换按钮**（图标会随状态互换）。
        /// 它控制的是**这个下载任务**的暂停/恢复 —— 与右键菜单的「暂停/恢复」同一套逻辑。
        /// ⚠️ 不是"控制播放器"：GeZi 是把文件交给 VLC 等**外部播放器**打开的，控制不了它们。
        /// </summary>
        private void OnTaskTogglePause(object sender, RoutedEventArgs e)
        {
            var t = (sender as System.Windows.Controls.Button)?.Tag as TaskItem;
            if (t == null) return;
            if (t.State == JobState.Paused) ResumeTask(t);
            else PauseTask(t);
        }

        /// <summary>暂停单个任务（右键菜单与行内按钮共用）。</summary>
        private void PauseTask(TaskItem t)
        {
            if (t == null) return;
            if (t.State == JobState.Downloading || t.State == JobState.Queued)
            {
                t.Job.PauseJob();
                // 与 Python 版一致：排队中的任务也允许「暂停」，语义是先挂牌，
                // 轮到它时不会真的开跑，用户恢复后才进队列。
                // ⚠️ 必须先置 PauseRequested 再改 State：进度上报永远报 Downloading，
                //    没有这个标志的话下一帧就会把 Paused 覆盖掉（图标会来回跳）。
                t.PauseRequested = true;
                t.State = JobState.Paused;
                // 立刻把速度归零。
                // 闸门式暂停的语义是"worker 跑完手上那一次读才阻塞"，所以在途请求还会回来
                // 一点数据、核心层会继续上报 EWMA 渐降的速度；UI 若照单全收，用户看到的就是
                // 「按了暂停、速度从 20MB/s 慢慢滑到 0」，以为没生效。
                // 置 0 后配合 OnJobUpdate 里的冻结，界面**一按就**是「已暂停 / 0 B/s」。
                t.Speed = 0;
                Log("已暂停: " + t.Name);
            }
            else
            {
                Log("该任务当前状态不可暂停（" + t.StatusText + "）");
            }
        }

        /// <summary>恢复单个任务（右键菜单与行内按钮共用）。</summary>
        private void ResumeTask(TaskItem t)
        {
            if (t == null) return;
            if (t.State == JobState.Paused)
            {
                t.PauseRequested = false;   // 先清标志，否则会继续忽略核心的 Downloading
                t.Job.ResumeJob();
                t.State = JobState.Downloading;
                Log("已恢复: " + t.Name);
            }
            else
            {
                Log("该任务当前状态不可恢复（" + t.StatusText + "）");
            }
        }

        private void OnTaskCancel(object sender, RoutedEventArgs e)
        {
            foreach (var t in TasksFromMenu(sender)) CancelTask(t);
        }

        /// <summary>取消单个任务（右键菜单可能一次取消多个）。</summary>
        private void CancelTask(TaskItem t)
        {
            if (t == null) return;
            if (t.State == JobState.Downloading || t.State == JobState.Queued
                || t.State == JobState.Paused || t.State == JobState.Cancelling)
            {
                t.Job.CancelJob();
                // 与 CancelAll 同理：立即置「取消中」，否则窗口期内它看着还是
                // 「下载中」，用户点「清空已完成」清不掉会以为按钮坏了。
                t.State = JobState.Cancelling;
                // 「丢弃这些任务选择不进行续传的时候不会删除
                //  取消续传的文件的分片和对应的其他文件」。
                //  实测根因**不在"丢弃"那一步**（那条路径本来就删）：而是**被取消的任务
                //  根本进不了「未完成的任务」列表** —— SyncPending() 只收
                //  Queued/Downloading/Paused，Cancelled 被排除在外，
                //  于是它的分片成了**没有任何入口能清掉的孤儿**，而 PartsSweeper
                //  又只扫用户自定义的分片根目录（默认无自定义根 → 一个目录都不扫），
                //  磁盘上就一直躺着（用户机器上实测残留 59 MB）。
                //  → 按用户选定的方案：**取消 = 彻底放弃，立刻清理本地残留**。
                //    想保留进度以后再续传，请用「暂停」。
                Log("已请求取消: " + t.Name + "（将清理本地分片与未下完的部分）");
                CleanupCanceledTaskAsync(t.Job.Dest, t.Name);
            }
            else
            {
                Log("该任务当前状态不可取消（" + t.StatusText + "）");
            }
        }

        /// <summary>
        /// 取消任务后清理它的本地残留：分片目录 / 续传元数据 / 未下完的目标文件。
        /// <para></para>
        /// <para>
        /// 取消是**异步**的：<c>CancelJob()</c> 只是发个信号，下载器可能还要几百毫秒
        /// 才放手 —— 正在写的分片文件此刻还占着句柄，立刻删会 IOException。
        /// 所以放到后台线程，每轮"先删再查"，直到目标消失或重试用尽。
        /// </para>
        /// <para></para>
        /// <para>
        /// 用户报的正是"删没删不知道"。这里把结果写进日志：成功报释放了多少，
        /// 失败也明说（并提示下次启动的自动清扫会兜底），不再静默。
        /// </para>
        /// </summary>
        private void CleanupCanceledTaskAsync(string dest, string taskName)
        {
            if (string.IsNullOrEmpty(dest)) return;

            // ⚠️ AllPartsRoots() 要读 _settings，必须在 UI 线程先取好再进后台线程。
            var roots = AllPartsRoots();
            string name = taskName ?? "任务";

            var th = new Thread(() =>
            {
                long freed = 0;
                bool ok = false;

                for (int attempt = 0; attempt < 12; attempt++)
                {
                    if (attempt > 0) Thread.Sleep(250);

                    ok = true;
                    try
                    {
                        foreach (string path in SegmentedDownloader.EnumeratePartialPaths(dest, roots))
                        {
                            freed += PathSizeSafe(path);
                            TryDeletePath(path);
                            if (Directory.Exists(path) || File.Exists(path)) ok = false;
                        }

                        // 目标文件本身（未下完的残件；共写模式下它可能是预分配的满尺寸空洞文件）
                        if (File.Exists(dest))
                        {
                            freed += PathSizeSafe(dest);
                            try { File.Delete(dest); } catch { }
                            if (File.Exists(dest)) ok = false;
                        }
                    }
                    catch { ok = false; }

                    if (ok) break;
                }

                try
                {
                    LogFromWorker(ok
                        ? string.Format("已清理「{0}」的本地残留（释放 {1}）。",
                            name, Util.FormatSize(freed))
                        : string.Format("「{0}」的本地残留有文件正被占用，未能全部删除；"
                            + "下次启动的自动清扫会兜底回收。", name));
                }
                catch { }
            })
            {
                IsBackground = true,          // 后台线程：不阻止程序退出
                Name = "GeZi-CancelCleanup",
            };
            th.Start();
        }

        /// <summary>取路径占用的字节数（目录递归求和）；拿不到就返回 0（只用于日志）。</summary>
        private static long PathSizeSafe(string path)
        {
            try
            {
                if (File.Exists(path)) return new FileInfo(path).Length;
                if (Directory.Exists(path))
                {
                    long sum = 0;
                    foreach (string f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    {
                        try { sum += new FileInfo(f).Length; } catch { }
                    }
                    return sum;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>「仅移除记录」：只把任务从列表拿掉，不动磁盘上的文件。</summary>
        private void OnTaskRemoveRecord(object sender, RoutedEventArgs e)
        {
            // 支持 Shift 离散多选：一次移除多条记录。
            var all = TasksFromMenu(sender);
            if (all.Count == 0) return;

            var running = all.Where(x => x.State == JobState.Downloading
                                      || x.State == JobState.Queued
                                      || x.State == JobState.Paused
                                      || x.State == JobState.Cancelling).ToList();
            if (running.Count > 0)
            {
                var ans = AppDialog.Show(this,
                    (running.Count == 1
                        ? "任务「" + running[0].Name + "」还在进行中。\n\n"
                        : "选中的 " + running.Count + " 个任务还在进行中。\n\n")
                    + "仅移除记录不会停止后台下载，文件仍会继续写入。\n"
                    + "确定要先取消并移除记录吗？",
                    "仅移除记录", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No);
                if (ans != MessageBoxResult.Yes)
                    return;
            }

            foreach (var t in all) RemoveTaskRecord(t);
            // 移除后立刻同步「未完成任务」记录 —— 否则它下次启动还会被当成可续传任务
            SyncPending();
            UpdatePauseAllButton(force: true);
        }

        /// <summary>移除单条任务记录（不做确认，确认由调用方统一做一次）。</summary>
        private void RemoveTaskRecord(TaskItem t)
        {
            if (t == null) return;
            // ⚠️ 原来这里**漏了 Paused**：暂停中的任务被移除记录后，
            // 它的 Job 仍然挂在调度器里（worker 阻塞在 PauseToken 的闸门上），
            // 于是 RunAllAsync 永远不返回 → `_batchRunning` 一直是 true
            // → 用户退出时永远弹「还有下载任务正在运行」，而且它还会留在
            // 「未完成任务」记录里，下次启动又被当成可续传任务。
            // 用户原话：「我中途暂停并且删除了，退出后为什么会显示还有任务正在运行，
            //           这意味着续传也会在我重建软件的时候出现呗」—— 确实如此。
            if (t.State == JobState.Downloading || t.State == JobState.Queued
                || t.State == JobState.Paused || t.State == JobState.Cancelling)
            {
                // 暂停中的任务必须先恢复再取消：worker 正卡在闸门上等 Resume，
                // 只发 Cancel 的话它要等闸门放开才会看到取消标志。
                if (t.State == JobState.Paused)
                {
                    try { t.Job.ResumeJob(); } catch { }
                }
                t.Job.CancelJob();
                t.State = JobState.Cancelling;
            }
            _tasks.Remove(t);
            Log("已移除记录（本地文件保留）: " + t.Name);
        }

        /// <summary>
        /// 「删除本地文件」：清掉目标文件 + 分片目录 + .qmeta。
        /// 与 Python 版 `_task_del_local` 对齐。关键细节：
        ///  - 先取消后台任务，否则 worker 还在写，删了又会重新创建；
        ///  - 文件被外部程序（播放器、资源管理器预览）占用时，用 MoveFileEx
        ///    登记为**重启后自动删除**，而不是直接报一个用户看不懂的错误。
        /// </summary>
        /// <summary>
        /// 「删除本地文件」= **不要这个任务了**：先取消下载，再删掉本地文件与分片。
        /// 「在任务中一个任务正在下载中当我选择删除本地文件，
        /// 就相当于我不要这个任务了，所以会进行取消下载并完成删除本地文件。
        /// 日志上只会显示已成功删除本地文件XXX」。
        /// ⚠️ 取消条件里**必须包含 Paused**：暂停中的任务 Job 仍挂在调度器上
        /// （worker 阻塞在 PauseToken 闸门），只把任务从列表移走而不取消，
        /// `RunAllAsync` 就永不返回 → `_batchRunning` 一直 true
        /// → 退出时永远提示"还有任务在运行"、下次启动又被当成可续传任务。
        /// （同样的坑在「仅移除记录」上已经踩过一次，见那里的注释。）
        /// ⚠️ 支持 Shift 离散多选后一次删多个。
        /// </summary>
        private void OnTaskDeleteLocal(object sender, RoutedEventArgs e)
        {
            var list = TasksFromMenu(sender)
                .Where(x => x?.Job != null && !string.IsNullOrEmpty(x.Job.Dest))
                .ToList();
            if (list.Count == 0) return;

            string body = list.Count == 1
                ? list[0].Job.Dest
                : string.Join("\n", list.Take(8).Select(x => "· " + x.Name))
                  + (list.Count > 8 ? "\n…" : "");
            var ans = AppDialog.Show(this,
                string.Format("确定删除{0}本地文件吗？\n\n{1}\n\n" +
                              "（正在下载的会先取消；分片与续传元数据一并清理，不可撤销）",
                    list.Count == 1 ? "以下" : list.Count + " 个任务的", body),
                "删除本地文件", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (ans != MessageBoxResult.Yes)
                return;

            foreach (var t in list)
            {
                // 只要还在"活着"的状态就先取消（含暂停——见上面的注释）
                if (t.State == JobState.Downloading || t.State == JobState.Queued
                    || t.State == JobState.Paused || t.State == JobState.Cancelling)
                {
                    try { t.Job.CancelJob(); } catch { }
                    t.State = JobState.Cancelling;
                }

                string dest = t.Job.Dest;
                string name = t.Name;
                _tasks.Remove(t);
                // 后台删除：文件可能较大，且被占用时要等待句柄释放，不能卡 UI 线程。
                _ = Task.Run(() => DeleteLocalFiles(dest, name));
            }
        }

        private void DeleteLocalFiles(string dest, string name)
        {
            // 单轮删除存在竞态：CancelJob() 只是发信号，
            // worker 可能还在退避重试/ReadAsync 中没退出 —— 删完文件它又把
            // 分片文件、目标文件"复活"了（任务已从列表移除，下载器 finally
            // 的清理也已跑完或不会跑）→ 残留永久留下，重开显示"可续传"。
            // 改为与「取消」同款的多轮重试（每轮先删再查，最多 12 轮 ≈ 3 秒），
            // 给 worker 留出退出时间后再确认删净。
            int deleted = 0, delayed = 0;
            var failed = new List<string>();
            for (int attempt = 0; attempt < 12; attempt++)
            {
                if (attempt > 0) Thread.Sleep(250);
                deleted = 0;
                delayed = 0;
                failed = new List<string>();
                DeleteRound(dest, out deleted, out delayed, failed);
                if (failed.Count == 0 && delayed == 0)
                    break;
            }

            // 日志尽量只有一行（用户要求：「日志上只会显示已成功删除本地文件XXX」）。
            // 出问题时才追加说明 —— 那种情况必须让用户看到。
            if (failed.Count == 0 && delayed == 0)
            {
                LogFromWorker("已成功删除本地文件「" + name + "」");
            }
            else
            {
                LogFromWorker(string.Format("已删除本地文件「{0}」{1}{2}",
                    name,
                    delayed > 0 ? "（" + delayed + " 项正被占用，已登记为重启后删除）" : "",
                    failed.Count > 0 ? "，" + failed.Count + " 项失败：" + string.Join("；", failed) : ""));
            }
        }

        /// <summary>删除本地文件的一轮尝试（删分片路径 + 目标文件 + 占用兜底）。</summary>
        private void DeleteRound(string dest, out int deleted, out int delayed, List<string> failed)
        {
            deleted = 0;
            delayed = 0;

            // 分片目录 + 续传元数据的清理。
            // 这里原来写死 `dest + ".qparts"` / `dest + ".qmeta"`，
            // **完全没有考虑「默认分片位置」设置** —— 一旦用户在设置页把分片根目录
            // 指到别处（或事后改过），分片实际落在 `{root}\{slug}_{fnv8}\parts`，
            // 而删除逻辑还在删目标旁边的 `.qparts`，于是"分片根本没删掉"。
            // 现在改为复用下载器**同一份**路径推导（SegmentedDownloader.ResolvePaths），
            // 并把「当前 + 历史用过的」全部根目录都覆盖到 —— 用户反复改过设置时，
            // 散落在多个旧根目录下的残片也能一次清干净。
            foreach (string p in SegmentedDownloader.EnumeratePartialPaths(dest, AllPartsRoots()))
            {
                try
                {
                    if (Directory.Exists(p))
                    {
                        Directory.Delete(p, true);
                        deleted++;
                    }
                    else if (File.Exists(p))
                    {
                        File.Delete(p);
                        deleted++;
                    }
                }
                catch (Exception ex) { failed.Add(p + " — " + ex.Message); }
            }

            // 目标文件本身
            try
            {
                if (File.Exists(dest))
                {
                    File.Delete(dest);
                    deleted++;
                }
            }
            catch (IOException)
            {
                // 被占用（播放器/预览）：登记重启后删除，比直接失败体验好得多。
                if (NativeMethods.MoveFileEx(dest, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT))
                    delayed++;
                else
                    failed.Add(dest + " — 被其他程序占用，且登记重启删除失败（可能需要管理员权限）");
            }
            catch (Exception ex) { failed.Add(dest + " — " + ex.Message); }
        }

        /// <summary>
        /// 把用户手输的目录规范化：去空白/去首尾引号，并统一分隔符。
        /// <para>用户可以在设置页**手输**下载目录，很容易输成
        /// `C:/Users/<用户名>/Downloads`（正斜杠）。而 `Path.Combine` 拼文件名时用的是 `\`，
        /// 于是得到 `C:/Users/<用户名>/Downloads\xxx.mp4` 这种**混合斜杠**路径。
        /// Windows 自己认，但**外部程序不一定认** —— VLC 就会把 `C:/…` 当成 URL
        /// 协议头（`c:` scheme）而打不开文件（2026-10-04 用户实测）。</para>
        /// </summary>
        private static string NormalizeDir(string dir)
        {
            dir = (dir ?? "").Trim().Trim('"').Trim();
            if (dir.Length == 0) return "";
            try { return Path.GetFullPath(dir); } catch { return dir; }
        }

        /// <summary>
        /// 「打开」：交给**系统默认程序**处理。
        /// ⚠️ 这不是"在 GeZi 里播放" —— 内嵌播放会受 WPF MediaElement 限制
        /// （底层是 Windows Media Player，不支持 mkv/flv）。
        /// 交给系统则**没有格式限制**：装了 VLC 就用 VLC，只装系统自带的就用
        /// 「电影和电视」，都没装则弹「选择打开方式」。
        /// 边下边播时，只要目标文件已按序增长到可播位置，这里就能直接看。
        /// </summary>
        private void OnOpenTaskFile(object sender, RoutedEventArgs e)
        {
            var item = (sender as System.Windows.Controls.Button)?.Tag as TaskItem
                       ?? TaskFromMenu(sender);
            if (item != null)
                OpenTaskFileCore(item);
        }

        private void OpenTaskFileCore(TaskItem item)
        {
            string dest = item?.Job?.Dest;
            if (string.IsNullOrEmpty(dest)) return;

            if (!System.IO.File.Exists(dest))
            {
                Log("文件还没写盘，暂时打不开：" + dest);
                return;
            }

            // 「在软件内点播放无法播放这个文件，但从下载目录里
            //  右键用播放器打开又能看」。
            //  实测：文件本身是**完好的**（987.72MB 全盘扫描无空洞），VLC 也能正常打开。
            //  真正的原因是**点的时机太早** —— 「独立分片（边下边播）」模式的目标文件
            //  是从头**按序拼**出来的：前面的分片还没下到时，文件可能是 0 字节或只有一小段，
            //  播放器只会甩一句「无法播放此文件」，用户很容易误以为文件坏了、白删重下。
            //  → 先看一眼实际长度：还是空的就直接说清楚，别把播放器的报错丢给用户。
            long curSize = 0;
            try { curSize = new System.IO.FileInfo(dest).Length; } catch { }
            if (curSize <= 0)
            {
                Log("这个文件目前是 0 字节，还没到能播放的程度：" + dest);
                AppDialog.Show(this,
                    "这个文件现在还是 0 字节，播放器打不开它。\n\n"
                    + "「独立分片（边下边播）」模式下，目标文件是从头按顺序拼出来的：\n"
                    + "前面的分片还没下到时，文件就是空的。\n"
                    + "等进度涨起来再点「播放」就可以看了。",
                    "暂时还播不了",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            // 还没下完就点播放：给一句准确的说明，免得用户以为是文件坏了。
            // 这不是"GeZi 打不开"—— 即使用写句柄把文件锁住，
            // VLC 照样能打开（实测 0 条错误日志），GeZi 的调用链也是对的。
            // 真正的原因是**文件只有按顺序拼好的那一段前缀**：
            //   · 前面几片还没到齐时，文件可能只有几十 KB；
            //   · 更关键的是 **MP4 的 moov（索引表）通常写在文件末尾**
            //     （没做 faststart 的文件）—— 播放器必须先读到文件尾部才能播，
            //     所以半截文件一律播不了，而**下完之后立刻就能播**。
            if (item.State != JobState.Completed)
            {
                Log(string.Format(
                    "提示: 该文件还没下完（当前 {0} / {1}）。独立分片模式下目标文件是按顺序"
                    + "拼出来的前缀，若视频索引在文件末尾，播放器会提示「无法播放此文件」—— "
                    + "这属于正常现象，等进度更多（或下完）再试即可。",
                    Util.FormatSize(curSize), Util.FormatSize(item.Total)));
            }

            try
            {
                // 走统一入口（内部全用 CreateProcess + UseShellExecute=false），
                // 绕开系统文件关联——这条经验来自今天实测：用户在机器上重启后
                // Python Flet 能用 / GeZi 不能用，差异就在 Process.Start 重载默认走
                // ShellExecuteEx 依赖关联，而关联层在某些机器上坏掉。
                if (!ShellLaunch.OpenFile(dest))
                {
                    Log("打开失败（文件当前 " + Util.FormatSize(curSize) + "），请手动到下载目录查看：" + dest);
                    AppDialog.Show(this,
                        "无法打开文件，请手动到下载目录查看。\n\n" + dest,
                        "需要手动打开",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }
            }
            catch (Exception ex) { Log("打开失败: " + ex.Message); }
        }

        /// <summary>「定位」：在资源管理器中**选中**该文件；还没写盘就打开所在目录。</summary>
        private void OnLocateTaskFile(object sender, RoutedEventArgs e)
        {
            var item = (sender as System.Windows.Controls.Button)?.Tag as TaskItem
                       ?? TaskFromMenu(sender);
            string dest = item?.Job?.Dest;
            if (string.IsNullOrEmpty(dest)) return;

            // 走 Shell API 而不是 Process.Start("explorer.exe", "/select,...")：
            // 后者会另起 explorer 进程，用户实测报过 0xc0000142（进程启动即崩）。
            // 详见 ShellReveal 的注释。
            if (!ShellReveal.Reveal(dest))
                Log("定位失败：找不到该文件或所在目录（" + dest + "）");
        }

        // ---------------- 下载历史 ----------------

        /// <summary>
        /// 「下载历史」：用最简单的窗口列出记录，支持打开/定位/清空。
        /// 移植自 Python Flet 版 `_on_open_history`。
        /// UI 刻意保持朴素（ListBox + 按钮），只求功能可用。
        /// </summary>
        private void OnOpenHistory(object sender, RoutedEventArgs e)
        {
            var win = new Window
            {
                Title = "下载历史",
                Owner = this,
            };
            DialogChrome.StyleWindow(win, 760, 520);

            var grid = new System.Windows.Controls.Grid { Margin = new Thickness(20, 18, 20, 18) };
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var title = DialogChrome.PageTitle("下载历史");
            System.Windows.Controls.Grid.SetRow(title, 0);
            grid.Children.Add(title);

            var bar = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 12),
            };
            // 三个按钮「模板感太浓」→ 改成透明背景的幽灵按钮。
            // 只保留文字 + 悬停反馈；「清空记录」是破坏性操作，用**红字**而不是红底
            // （红底实心块在这一排里视觉重量过重，且和旁边的白底描边不在一个体系）。
            var ghostStyle = TryFindResource("GhostButton") as Style;
            var openBtn = new Button { Content = "打开", Style = ghostStyle };
            var locateBtn = new Button { Content = "定位", Style = ghostStyle };
            var clearBtn = new Button { Content = "清空记录", Style = ghostStyle };
            var clearDanger = TryFindResource("DangerBrush") as Brush;
            if (clearDanger != null) clearBtn.Foreground = clearDanger;
            bar.Children.Add(openBtn);
            locateBtn.Margin = new Thickness(8, 0, 0, 0);
            bar.Children.Add(locateBtn);
            clearBtn.Margin = new Thickness(8, 0, 0, 0);
            bar.Children.Add(clearBtn);
            System.Windows.Controls.Grid.SetRow(bar, 1);
            grid.Children.Add(bar);

            var list = DialogChrome.StyledList();
            list.FontSize = 12;
            var listCard = DialogChrome.Card(list);
            listCard.Padding = new Thickness(0);
            System.Windows.Controls.Grid.SetRow(listCard, 2);
            grid.Children.Add(listCard);

            // 每行：时间  状态  大小  名称（"名称"是主键，路径另存）
            var records = new List<HistoryRecord>();
            var display = new System.Collections.ObjectModel.ObservableCollection<string>();
            list.ItemsSource = display;

            Action reload = () =>
            {
                records = HistoryStore.GetHistory();
                display.Clear();
                foreach (var r in records)
                {
                    display.Add(string.Format("{0}   [{1}]   {2}   {3}",
                        r.Time ?? "", r.Status ?? "", r.Size ?? "", r.Name ?? ""));
                }
                if (display.Count == 0)
                    display.Add("（暂无历史记录）");
            };
            reload();

            HistoryRecord Sel()
            {
                int i = list.SelectedIndex;
                return (i >= 0 && i < records.Count) ? records[i] : null;
            }

            openBtn.Click += (s2, e2) =>
            {
                var r = Sel();
                if (r == null || string.IsNullOrEmpty(r.Path)) { Log("请先选中一条记录"); return; }
                if (!File.Exists(r.Path)) { Log("文件已不存在：" + r.Path); return; }
                OpenTaskFileByPath(r.Path);
            };
            locateBtn.Click += (s2, e2) =>
            {
                var r = Sel();
                if (r == null || string.IsNullOrEmpty(r.Path)) { Log("请先选中一条记录"); return; }
                if (!ShellReveal.Reveal(r.Path))
                    Log("定位失败：" + r.Path);
            };
            clearBtn.Click += (s2, e2) =>
            {
                var ans = AppDialog.Show(win,
                    "确定清空全部下载记录吗？（不会删除磁盘上的文件）",
                    "清空记录", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No);
                if (ans != MessageBoxResult.Yes) return;
                HistoryStore.ClearHistory();
                reload();
                Log("已清空下载历史");
            };

            win.Content = grid;
            win.ShowDialog();
        }

        /// <summary>按路径打开文件（供历史窗口复用「打开」逻辑）。</summary>
        private void OpenTaskFileByPath(string path)
        {
            if (!ShellLaunch.OpenFile(path))
                Log("打开失败，请手动到下载目录查看：" + path);
        }

        // ---------------- 启动时的未完成任务提示 ----------------

        // ---------------- 诊断面板 ----------------

        /// <summary>
        /// 最近一次诊断快照（按任务名分组，最新的覆盖）。
        /// 下载线程高频回调（每 5 秒 × N 个任务），**只存数据不碰 UI**，
        /// 由面板定时器去读 —— 避免高频跨线程 UI 更新。
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DiagnosticsSnapshot>
            _diagLatest = new System.Collections.Concurrent.ConcurrentDictionary<string, DiagnosticsSnapshot>(
                StringComparer.OrdinalIgnoreCase);
        private Window _diagWindow;

        /// <summary>分类过滤谓词（供 ICollectionView 调用）。</summary>
        private bool TaskFilterPredicate(object o)
        {
            var t = o as TaskItem;
            if (t == null) return false;
            switch (_taskFilter)
            {
                case "active":
                    return t.State == JobState.Queued || t.State == JobState.Downloading
                        || t.State == JobState.Paused;
                case "failed":
                    return t.State == JobState.Failed || t.State == JobState.Cancelled;
                case "done":
                    return t.State == JobState.Completed;
                default:
                    return true;
            }
        }

        /// <summary>
        /// 分类 Tab 切换。只改过滤器 + Refresh，
        /// 不重建集合 —— 保证滚动位置、选中项、绑定都不受影响。
        /// </summary>
        private void OnTaskFilterChanged(object sender, RoutedEventArgs e)
        {
            try
            {
                if (TaskFilterActive == null) return;   // 初始化期间控件尚未就绪
                if (TaskFilterAll.IsChecked == true) _taskFilter = "all";
                else if (TaskFilterActive.IsChecked == true) _taskFilter = "active";
                else if (TaskFilterFailed.IsChecked == true) _taskFilter = "failed";
                else _taskFilter = "done";
                if (_taskView != null) _taskView.Refresh();
                UpdateTaskFilterCounts();
            }
            catch { }
        }

        /// <summary>更新分类 Tab 右侧的计数（"共 N 条"），让用户不用切过去就知道有几条。</summary>
        private void UpdateTaskFilterCounts()
        {
            try
            {
                if (TaskFilterCountText == null) return;
                int n = 0;
                foreach (var t in _tasks)
                {
                    if (t == null) continue;
                    bool hit;
                    switch (_taskFilter)
                    {
                        case "active":
                            hit = t.State == JobState.Queued || t.State == JobState.Downloading
                               || t.State == JobState.Paused; break;
                        case "failed":
                            hit = t.State == JobState.Failed || t.State == JobState.Cancelled; break;
                        case "done":
                            hit = t.State == JobState.Completed; break;
                        default:
                            hit = true; break;
                    }
                    if (hit) n++;
                }
                TaskFilterCountText.Text = n > 0 ? "共 " + n + " 条" : "";
            }
            catch { }
        }

        private void OnDiagnostics(DiagnosticsSnapshot snap)
        {
            if (snap == null) return;
            // 必须先封送到 UI 线程，否则后面整段都是白干。
            // `Diagnostics` 是裸 `Action<T>`：SegmentedDownloader 在**自己的主循环线程**里
            // 直接调用它（每 5 秒一次）。这和进度回调不一样 —— 那个走 `IProgress<T>`，
            // WPF 的 `Progress<T>` 会自动捕获 SynchronizationContext 并封送回 UI 线程。
            // 而下面要写 TaskItem 的属性（ActiveConnections / WorkerCount），setter 会触发
            // PropertyChanged → WPF 绑定引擎 → **只有创建绑定目标的那个线程（UI 线程）能碰它**。
            // 在下载线程里调就抛「调用线程无法访问此对象」，然后被本方法外层 try 的
            // `catch { }` 静默吞掉 ⇒ 任务行的「连接车道」永远不更新。
            // 用户实测症状（2026-10-15）：内核日志和**诊断面板**都有「活跃/总连接」数字，
            // 唯独任务行那条空着 —— 因为 `_diagLatest` 是 ConcurrentDictionary、写在异常之前，
            // 只有"写 TaskItem"这一跳断掉。速度/进度一直正常，正因为它们走 IProgress。
            // ⚠️ 这是 `3cfd4ae`（Ketch 式「连接车道」）引入时就带着的缺陷：在它之前
            //    OnDiagnostics 只写线程安全的字典，从不碰任何 UI 对象。
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<DiagnosticsSnapshot>(OnDiagnostics), snap);
                return;
            }
            try
            {
                string key = string.IsNullOrEmpty(snap.Name) ? "(未命名)" : snap.Name;
                _diagLatest[key] = snap;

                // 把连接数喂给任务行，驱动 Ketch 式「连接车道」。
                // 匹配键必须用**目标文件完整路径**，不能用文件名。
                //   旧写法 `t.Name == snap.Name` 拿的是"网盘原始文件名"对"落盘文件名"，
                //   而落盘名经过 SanitizeFileName + UniquePath 加工，经常不等：
                //     · 同名文件已存在 → 加 ` (1)` 后缀（实测用户 32 条历史里有 2 条如此）
                //     · 含 \ / : * ? " < > | → 被替换成 `_`
                //   对不上 ⇒ 车道永远不亮（20 格全灰、没有 "12/16 连接" 文字）。
                //   完整路径在并发任务之间天然唯一（DownloadScheduler 的同路径互斥也用它当 key）。
                //   名字匹配保留为兜底，不破坏任何原本能工作的场景。
                TaskItem hit = null;
                foreach (var t in _tasks)
                {
                    if (t == null || t.Job == null) continue;
                    if (!string.IsNullOrEmpty(snap.Dest)
                        && string.Equals(t.Job.Dest, snap.Dest, StringComparison.OrdinalIgnoreCase))
                    {
                        hit = t;
                        break;
                    }
                }
                if (hit == null && !string.IsNullOrEmpty(snap.Name))
                {
                    foreach (var t in _tasks)
                    {
                        if (t != null && t.Name == snap.Name) { hit = t; break; }
                    }
                }
                if (hit != null)
                {
                    hit.ActiveConnections = snap.ActiveConnections;
                    hit.WorkerCount = snap.WorkerCount;
                }
            }
            catch { }
        }

        /// <summary>
        /// 打开独立诊断面板（移植并强化 Python Flet 版的诊断输出）。
        /// <para>以前只有日志文本框，指标混在几百行日志里，要"看出来"得靠肉眼逐行扫。
        /// 面板把这些指标整理成表格并每 2 秒刷新 —— 一眼就能判断
        /// "是服务端限速还是客户端没发满连接"。</para>
        /// </summary>
        private void OnOpenDiagnostics(object sender, RoutedEventArgs e)
        {
            if (_diagWindow != null)
            {
                try { _diagWindow.Activate(); return; } catch { _diagWindow = null; }
            }

            var win = new Window
            {
                Owner = this,
                Title = "下载诊断",
            };
            DialogChrome.StyleWindow(win, 1020, 560);

            var root = new Grid { Margin = new Thickness(20, 18, 20, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = DialogChrome.PageTitle("下载诊断");
            Grid.SetRow(title, 0);
            root.Children.Add(title);

            var head = DialogChrome.Hint(
                "指标由下载器每 5 秒上报一次。判断口径：\n"
                + "  · 活跃连接接近「并发上限」而速度低 → 服务端限速（客户端无解）\n"
                + "  · 活跃连接明显偏低 → 客户端没把连接发满（可调高线程数）\n"
                + "  · 对端 IP 只有 1 个 → DNS 没分散，慢连接抢占重连仍是同节点\n"
                + "  · 已抢占次数持续增长 → 存在长尾慢连接，抢占正在起作用",
                0, 12);
            Grid.SetRow(head, 1);
            root.Children.Add(head);

            var grid = new DataGrid
            {
                IsReadOnly = true,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                BorderThickness = new Thickness(0),
                FontSize = 12,
                RowHeight = 30,
                Background = Brushes.Transparent,
                HorizontalGridLinesBrush = (Brush)(TryFindResource("BorderBrushSoft") ?? Brushes.Gainsboro),
                Foreground = (Brush)(TryFindResource("TextPrimaryBrush") ?? Brushes.Black),
            };
            // 主题化：WPF 默认的 DataGrid 是"系统表格"观感（灰渐变表头 + 蓝底选中格），
            // 放在这套自绘界面里非常突兀（用户反馈「这个地方的 ui 也太丑了」）。
            var hdrStyle = TryFindResource("DiagColumnHeader") as Style;
            if (hdrStyle != null) grid.ColumnHeaderStyle = hdrStyle;
            var rowStyle = TryFindResource("DiagRow") as Style;
            if (rowStyle != null) grid.RowStyle = rowStyle;
            var cellStyle = TryFindResource("DiagCell") as Style;
            if (cellStyle != null) grid.CellStyle = cellStyle;
            grid.SelectionMode = DataGridSelectionMode.Single;
            grid.SelectionUnit = DataGridSelectionUnit.FullRow;
            grid.CanUserReorderColumns = false;
            grid.CanUserResizeRows = false;
            // 「对端 IP」那一列内容最长（可能好几个 IP），原来是 2* 只有 ~190px，
            // 用户反馈「由于内容太长无法全部显示」。两处一起改：
            //   ① 列权重提到 3*，并从「进度」「分片」里挤一点宽度出来；
            //   ② 单元格允许**换行**（行高会自动长高），再挂 ToolTip 兜底。
            // ⚠️ DataGridTextColumn 的换行/提示要靠 ElementStyle 设，直接给 Column 设没用。
            grid.Columns.Add(new DataGridTextColumn { Header = "任务", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "速度", Binding = new System.Windows.Data.Binding("Speed"), Width = new DataGridLength(84) });
            grid.Columns.Add(new DataGridTextColumn { Header = "活跃/上限", Binding = new System.Windows.Data.Binding("Conns"), Width = new DataGridLength(84) });
            grid.Columns.Add(new DataGridTextColumn { Header = "分片", Binding = new System.Windows.Data.Binding("Parts"), Width = new DataGridLength(84) });
            grid.Columns.Add(new DataGridTextColumn { Header = "进度", Binding = new System.Windows.Data.Binding("Progress"), Width = new DataGridLength(120) });
            grid.Columns.Add(new DataGridTextColumn { Header = "抢占", Binding = new System.Windows.Data.Binding("Preempt"), Width = new DataGridLength(56) });
            var ipCol = new DataGridTextColumn
            {
                Header = "对端 IP",
                Binding = new System.Windows.Data.Binding("PeerIps"),
                Width = new DataGridLength(3, DataGridLengthUnitType.Star),
            };
            var ipCell = new Style(typeof(System.Windows.Controls.TextBlock));
            ipCell.Setters.Add(new Setter(System.Windows.Controls.TextBlock.TextWrappingProperty,
                TextWrapping.Wrap));
            ipCell.Setters.Add(new Setter(System.Windows.Controls.TextBlock.VerticalAlignmentProperty,
                VerticalAlignment.Center));
            ipCell.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,
                new System.Windows.Data.Binding("PeerIps")));
            ipCol.ElementStyle = ipCell;
            grid.Columns.Add(ipCol);
            var gridCard = DialogChrome.Card(grid);
            gridCard.Padding = new Thickness(0);
            Grid.SetRow(gridCard, 2);
            root.Children.Add(gridCard);

            // 三个按钮「模板感太重」→ 透明幽灵按钮
            var diagGhost = TryFindResource("GhostButton") as Style;
            var copyBtn = new Button { Content = "复制诊断文本", Style = diagGhost };
            var clearBtn = new Button { Content = "清空", Style = diagGhost };
            var closeBtn = new Button { Content = "关闭", Style = diagGhost };
            var btns = DialogChrome.ButtonRow(copyBtn, clearBtn, closeBtn);
            btns.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetRow(btns, 3);
            root.Children.Add(btns);

            win.Content = root;

            var rows = new ObservableCollection<DiagRow>();
            grid.ItemsSource = rows;

            var timer = new DispatcherTimer(TimeSpan.FromSeconds(2),
                DispatcherPriority.Background, (s, ev) =>
                {
                    try { RefreshDiagRows(rows); } catch { }
                }, Dispatcher);
            timer.Start();

            copyBtn.Click += (s, ev) =>
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("文件\t速度\t活跃/上限\t分片\t进度\t抢占\t对端IP");
                    foreach (var r in rows)
                        sb.AppendLine(string.Join("\t", r.Name, r.Speed, r.Conns, r.Parts, r.Progress, r.Preempt, r.PeerIps));
                    Clipboard.SetText(sb.ToString());
                    Log("已复制诊断文本到剪贴板。");
                }
                catch { }
            };
            clearBtn.Click += (s, ev) => { _diagLatest.Clear(); rows.Clear(); };

            win.Closed += (s, ev) => { try { timer.Stop(); } catch { } _diagWindow = null; };
            _diagWindow = win;
            win.Show();
        }

        /// <summary>把最新快照刷进面板行（面板定时器调用，已在 UI 线程）。</summary>
        private void RefreshDiagRows(ObservableCollection<DiagRow> rows)
        {
            var snaps = _diagLatest.Values
                .OrderByDescending(s => s.Speed)
                .ToList();

            // 按需增删，避免每次重建导致选中态/滚动位置丢失
            while (rows.Count > snaps.Count) rows.RemoveAt(rows.Count - 1);
            for (int i = 0; i < snaps.Count; i++)
            {
                if (i >= rows.Count) rows.Add(new DiagRow());
                rows[i].Update(snaps[i]);
            }
        }

        /// <summary>诊断面板的一行（把快照格式化成可读文本）。</summary>
        private sealed class DiagRow : INotifyPropertyChanged
        {
            public string Name { get; private set; } = "";
            public string Speed { get; private set; } = "";
            public string Conns { get; private set; } = "";
            public string Parts { get; private set; } = "";
            public string Progress { get; private set; } = "";
            public string Preempt { get; private set; } = "";
            public string PeerIps { get; private set; } = "";

            public void Update(DiagnosticsSnapshot s)
            {
                Name = s.Name ?? "";
                Speed = Util.FormatSize((long)s.Speed) + "/s";
                Conns = s.ActiveConnections + "/" + s.WorkerCount;
                Parts = s.PartsDone + "/" + s.PartsTotal;
                string pct = s.TotalBytes > 0
                    ? (s.DoneBytes * 100.0 / s.TotalBytes).ToString("F1") + "%"
                    : "-";
                Progress = pct + "  " + Util.FormatSize(s.DoneBytes);
                Preempt = s.PreemptedCount.ToString();
                PeerIps = s.PeerIps ?? "";

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        /// <summary>
        /// 启动时在**后台线程**清扫过期分片残留（移植自 Python Flet 版的 `_sweep_stale_parts`）。
        /// <para></para>
        /// <para>
        /// 用户取消下载、改分片目录、或程序被强杀时，分片文件会留在磁盘上。
        /// 此前 C# 版**没有任何自动回收机制** —— 残留只增不减，用户会明显感到
        /// "磁盘越用越少"却找不到元凶（分片目录默认还是隐藏的）。
        /// </para>
        /// <para></para>
        /// <list type="bullet">
        /// <item>只看**文件 mtime**：正在下载的分片 mtime 持续刷新，永远不会被误删；</item>
        /// <item>扫的是 <see cref="AllPartsRoots"/> —— **当前生效 + 历史上用过的**全部根目录，
        ///   只扫当前那个会漏掉旧目录里的残留（这正是过去"分片删不掉"的成因）；</item>
        /// <item>删不掉（被占用/权限不足）就跳过，绝不因此影响下载；</item>
        /// <item>全程 try/catch，任何异常都只记日志。</item>
        /// </list>
        /// <para></para>
        /// <para>
        /// 中断/暂停超过 <see cref="PartsSweeper.DefaultMaxAgeDays"/> 天的下载，
        /// 其分片会在下次启动被清掉，该任务无法续传（需从 0 重下）。
        /// 这是"自动回收磁盘"与"无限期保留断点"之间必须做的取舍。
        /// </para>
        /// </summary>
        private void StartPartsSweep()
        {
            var roots = AllPartsRoots();
            string rootSnapshot = roots.Count == 0 ? "（无自定义根目录）" : string.Join("；", roots);

            var t = new Thread(() =>
            {
                try
                {
                    var r = PartsSweeper.Sweep(roots, PartsSweeper.DefaultMaxAgeDays);
                    if (r.Scanned == 0)
                        return;   // 没有任何分片文件，不必打扰用户

                    if (r.AnythingDone)
                    {
                        LogFromWorker(string.Format(
                            "分片清扫：回收了 {0} 个过期分片（{1}），跳过 {2} 个被占用的文件。",
                            r.Removed, Util.FormatSize(r.FreedBytes), r.Skipped));
                    }
                    else if (r.Skipped > 0)
                    {
                        LogFromWorker(string.Format(
                            "分片清扫：{0} 个过期分片因被占用而跳过（下次启动会重试）。", r.Skipped));
                    }
                }
                catch (Exception ex)
                {
                    try { LogFromWorker("分片清扫失败（已忽略）: " + ex.Message); } catch { }
                }
            })
            {
                IsBackground = true,             // 后台线程：不阻止程序退出
                Name = "GeZi-PartsSweep",
            };
            t.Start();

            LogDetail("已启动分片自动清扫（阈值 " + PartsSweeper.DefaultMaxAgeDays + " 天）。扫描范围: " + rootSnapshot);
        }

        /// <summary>
        /// 启动时读取未完成任务记录，让用户决定**逐条/批量续传**，而不是只提示一句。
        /// <para></para>
        /// <para>
        /// 原实现只弹「有 N 个未完成任务，是否打开下载目录？」—— 用户点「是」也只是
        /// 打开文件夹，要真正继续还得自己重新走一遍解析/选文件的流程，很别扭。
        /// Python Flet 版在这里是**逐条询问 + 支持一键全部继续**，体验明显更好。
        /// </para>
        /// <para></para>
        /// <para>
        /// 直链带时效签名，重启后必然失效，所以必须**重新取链**。为此
        /// <see cref="PendingTask"/> 补齐了 fid / token / 分享上下文（见其注释），
        /// 使这里可以脱离界面上下文重走「取链 → 入队」。分片与 .qmeta 都在原地，
        /// 下载器会自动接着下，不会重下已完成的部分。
        /// </para>
        /// <para></para>
        /// <para>
        /// 批量续传会一次性打出若干取链请求 —— 与"一次误选几万个文件"是同类风险。
        /// 因此数量超过 <see cref="ConfirmThreshold"/> 时先让用户确认；
        /// 无法续传的（免转存/信息不全）单独列出并说明，不会静默丢弃。
        /// </para>
        /// </summary>
        // ---------------- 检查更新（2026-10-11 新增） ----------------

        /// <summary>
        /// 启动检查确认有新版本后的回调（UI 线程）。
        /// 只做两件"不抢焦点"的事：托盘气泡 + 日志行。
        /// 真正的窗口等用户去「关于」页点「检查更新」才出现。
        /// ⚠️ 图标传 null：NotifyToast 会把它隐藏（实测过它处理 null）——
        ///    这里刻意**不用**系统气泡（ShowBalloonTip 在 Win10/11 会变成黑疙瘩图标）。
        /// </summary>
        private void OnUpdateFound(GitHubRelease m)
        {
            try
            {
                if (m == null) return;
                string size = m.AssetSize > 0
                    ? "，" + (m.AssetSize / 1048576.0).ToString("0.0") + " MB"
                    : "";
                NotifyToast.Show("发现新版本 v" + m.Version,
                    "打开「关于 → 检查更新」查看更新说明并下载" + size, null);
                Log("发现新版本 v" + m.Version + "（" + m.TagName + "）— 可在「关于」页点击「检查更新」。");
            }
            catch { }
        }

        private void ShowPendingOnStartup()
        {
            List<PendingTask> all;
            try { all = HistoryStore.GetPending(); }
            catch { return; }
            if (all == null || all.Count == 0)
                return;

            foreach (var p in all)
                LogDetail("  - " + p.Name + "  [" + (p.Status ?? "") + "]");

            var resumable = all.Where(p => p != null && p.CanResume).ToList();
            var notResumable = all.Where(p => p == null || !p.CanResume).ToList();

            var dlg = new Window
            {
                Owner = this,
                Title = "未完成的任务",
                ResizeMode = ResizeMode.CanResize,
            };
            DialogChrome.StyleWindow(dlg, 600, 480);

            var root = new Grid { Margin = new Thickness(20, 18, 20, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = DialogChrome.PageTitle("未完成的任务");
            Grid.SetRow(title, 0);
            root.Children.Add(title);

            var head = DialogChrome.Hint(string.Format(
                "上次有 {0} 个任务未完成，其中 {1} 个可以继续下载。\n"
                + "勾选后点「继续下载」会重新获取直链并接着已下载的部分继续（不会重下）。",
                all.Count, resumable.Count), 0, 12);
            Grid.SetRow(head, 1);
            root.Children.Add(head);

            // 「续传界面的文件前面有个对勾太误导人了，
            //  让人以为已经自动勾选了」。
            //  原来是在 Display 字符串前面拼了一个 "✓"（表示"可续传"），可它长得
            //  就像"已勾选"；再加上 ListBox 的选中蓝底，整行看起来就是"已经选好了"，
            //  于是用户以为可以直接点「继续下载」，结果按钮是灰的（一个都没真勾上）。
            //  → 换成**真正的复选框**：默认全部不勾，勾选态与按钮可用性直接联动。
            var wrappers = new List<PendingRow>();
            foreach (var p in all) wrappers.Add(new PendingRow(p));

            var selAll = DialogChrome.SecondaryButton("全选可续传");
            var resumeBtn = DialogChrome.PrimaryButton("继续下载");
            var discardBtn = DialogChrome.DangerButton("丢弃这些任务");
            var closeBtn = DialogChrome.SecondaryButton("稍后再说");

            // 用 ListBox + 复选（ItemsSource 为包装对象，便于双向绑定 IsChecked）
            var box = new ListBox
            {
                SelectionMode = SelectionMode.Single,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4),
            };
            var plainStyle = TryFindResource("PlainListBox") as Style;
            if (plainStyle != null) box.Style = plainStyle;
            // ⚠️ **必须**给 ItemTemplate：ListBox 的项没有模板时会调 ToString()，
            //    而 PendingRow 没重写它 → 用户看到一列
            //    「GeZi.MainWindow+PendingRow」（用户反馈：「续传的时候为什么都
            //    显示同样的东西，不应该显示需要续传的文件名吗」）。
            box.ItemTemplate = BuildPendingRowTemplate();
            foreach (var w in wrappers) box.Items.Add(w);

            var boxCard = DialogChrome.Card(box);
            boxCard.Padding = new Thickness(0);
            Grid.SetRow(boxCard, 2);
            root.Children.Add(boxCard);

            var btns = DialogChrome.ButtonRow(selAll, resumeBtn, discardBtn, closeBtn);
            btns.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetRow(btns, 3);
            root.Children.Add(btns);

            dlg.Content = root;

            // 「没勾任何文件时『继续下载』是灰的，用不了」。
            // 这个禁用态本身是对的（一个都没勾时点它没有任何意义），
            // 真正的问题是上面那个假对勾让人以为已经勾好了 —— 现在换成真复选框，
            // 并让按钮状态**实时跟着勾选走**：勾一个立刻变亮，全取消立刻变灰。
            Action syncResumeBtn = () =>
            {
                resumeBtn.IsEnabled = wrappers.Any(w => w.IsChecked && w.CanResume);
            };
            foreach (var w in wrappers)
            {
                w.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(PendingRow.IsChecked)) syncResumeBtn();
                };
            }
            syncResumeBtn();

            bool doResume = false;
            bool doDiscard = false;

            selAll.Click += (s, e) =>
            {
                foreach (var w in wrappers)
                    if (w.CanResume) w.IsChecked = true;
            };
            resumeBtn.Click += (s, e) => { doResume = true; dlg.DialogResult = true; };
            discardBtn.Click += (s, e) => { doDiscard = true; dlg.DialogResult = true; };
            closeBtn.Click += (s, e) => { dlg.DialogResult = false; };

            // 默认**一个都不勾**（原来是"全部设 false"，效果一样，但写在这里容易被
            // 误读成"有默认勾选"）。勾选完全交给用户，按钮状态跟着走。
            syncResumeBtn();

            dlg.ShowDialog();

            if (doDiscard)
            {
                // 丢弃 = 清 pending + 清该任务的全部本地残留
                //（分片目录 / 续传元数据 / **未下完的目标文件**；网盘上的文件保留）。
                // 「丢弃这些任务的时候不会删除分片和对应的其他文件」。
                //  这里补两处：
                //   ① 原来只删分片目录与 .qmeta，**没删目标文件** —— 而共写模式的目标文件
                //      是预分配的满尺寸文件（可能是几百 MB 的空洞文件），留着最占地方；
                //   ② 原来 `TryDeletePath` 静默吞异常、`n++` 无条件自增，
                //      于是"删失败"和"删成功"在日志里长得一模一样 —— 用户报的正是
                //      "删没删不知道"。现在分开统计成功/失败，并把失败情况写进日志。
                int n = 0, failed = 0;
                long freed = 0;
                var roots = AllPartsRoots();
                foreach (var p in all)
                {
                    if (p == null) continue;
                    bool anyFail = false;
                    try
                    {
                        foreach (string path in SegmentedDownloader.EnumeratePartialPaths(p.Dest, roots))
                        {
                            if (!Directory.Exists(path) && !File.Exists(path)) continue;
                            freed += PathSizeSafe(path);
                            TryDeletePath(path);
                            if (Directory.Exists(path) || File.Exists(path)) anyFail = true;
                        }
                        // 目标文件本身（未下完的残件）
                        if (!string.IsNullOrEmpty(p.Dest) && File.Exists(p.Dest))
                        {
                            freed += PathSizeSafe(p.Dest);
                            try { File.Delete(p.Dest); } catch { }
                            if (File.Exists(p.Dest)) anyFail = true;
                        }
                    }
                    catch { anyFail = true; }

                    if (anyFail) failed++;
                    else n++;
                }
                HistoryStore.ClearPending();
                _pendingSig = null;
                Log(failed == 0
                    ? string.Format("已丢弃 {0} 个未完成任务，本地残留已清理（释放 {1}），网盘文件保留。",
                        n, Util.FormatSize(freed))
                    : string.Format("已丢弃 {0} 个未完成任务（释放 {1}）；其中 {2} 个有文件正被占用未能删除，"
                        + "下次启动的自动清扫会兜底回收。网盘文件保留。",
                        n + failed, Util.FormatSize(freed), failed));
                return;
            }

            if (!doResume)
            {
                Log("未完成的任务已保留记录，稍后可从下载页重新下载（分片在，会自然续传）。");
                return;
            }

            var chosen = wrappers.Where(w => w.IsChecked && w.CanResume)
                                 .Select(w => w.Task).ToList();
            if (chosen.Count == 0)
            {
                Log("没有勾选任何可续传的任务。");
                return;
            }

            if (chosen.Count > ConfirmThreshold)
            {
                var ans = AppDialog.Show(this,
                    string.Format("将续传 {0} 个任务，会一次性发出相应数量的取链请求。\n\n确认继续吗？",
                        chosen.Count),
                    "确认续传", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (ans != MessageBoxResult.Yes)
                {
                    Log("已取消批量续传。");
                    return;
                }
            }

            Log(string.Format("开始续传 {0} 个任务…", chosen.Count));
            Tabs.SelectedIndex = 1;
            _ = ResumePendingBatchAsync(chosen);
        }

        /// <summary>
        /// 续传列表的行模板：**真正的复选框** + 文件名（+ 大小 / 不可续传的说明）。
        /// <para></para>
        /// <para>
        /// 原来用 <c>DisplayMemberPath = "Display"</c>，只能在文字里拼一个 "✓" 冒充勾选态，
        /// 用户明确反馈「文件前面有个对勾太误导人了，让人以为已经自动勾选了」。
        /// 换成 ItemTemplate 才能放真的 CheckBox，并把勾选态双向绑回 PendingRow.IsChecked
        /// （「继续下载」按钮的可用性就是靠它驱动的）。
        /// </para>
        /// </summary>
        private DataTemplate BuildPendingRowTemplate()
        {
            var cb = new FrameworkElementFactory(typeof(CheckBox));
            var st = TryFindResource("AppCheckBox") as Style;
            if (st != null) cb.SetValue(FrameworkElement.StyleProperty, st);
            cb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            cb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            // 不可续传的行：复选框置灰（文案里已说明原因）
            cb.SetBinding(UIElement.IsEnabledProperty,
                new Binding("CanResume") { Mode = BindingMode.OneWay });
            cb.SetBinding(ToggleButton.IsCheckedProperty,
                new Binding("IsChecked")
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                });
            cb.SetBinding(ContentControl.ContentProperty, new Binding("Display"));

            return new DataTemplate { VisualTree = cb };
        }

        /// <summary>续传对话框里的一行（带勾选）。</summary>
        private sealed class PendingRow : INotifyPropertyChanged
        {
            public PendingTask Task { get; }
            private bool _checked;
            public bool IsChecked
            {
                get => _checked;
                set { _checked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
            }
            public string Display { get; }
            public bool CanResume => Task != null && Task.CanResume;

            public PendingRow(PendingTask t)
            {
                Task = t;
                // 一行要能一眼看出"这是哪个文件、多大、能不能接着下"。
                // 原来这里在文件名前面拼了一个 "✓"/"✕"，
                // 那是"能不能续传"的标记，却长得就像"已经勾选" —— 用户以为文件被
                // 自动勾上了，于是点了「继续下载」却发现按钮是灰的。
                // 现在前缀去掉：能不能续传看**复选框是否可用**（不可续传的复选框置灰），
                // 以及后面那句说明；勾选与否完全由用户点复选框决定。
                string size = (t != null && t.Size > 0) ? "   " + Util.FormatSize(t.Size) : "";
                string why = CanResume ? "" : "   （信息不全，需重新下载）";
                Display = (t?.Name ?? "(未知)") + size + why;
            }

            public event PropertyChangedEventHandler PropertyChanged;

            // ListBoxItem 的**无障碍名**取的是数据项的 ToString()（即使设了
            // DisplayMemberPath，容器元素的名字仍走这条），不重写的话屏幕阅读器/
            // UIA 读到的还是「GeZi.MainWindow+PendingRow」。
            public override string ToString() { return Display; }
        }

        /// <summary>删除文件或目录，失败静默（供丢弃续传用）。</summary>
        private static void TryDeletePath(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        /// <summary>
        /// 批量续传：为每个任务重新取链并入队下载。
        /// <para>取链按 <see cref="PendingTask.FromShare"/> 分流，复用与首次下载相同的接口。
        /// 任一任务取链失败不影响其他任务（逐条 try/catch 并记日志）。</para>
        /// </summary>
        /// <summary>
        /// 续传前确保拿到一个可用的客户端。
        /// <para>启动时的自动登录是异步的，而续传框弹得很早
        /// （Idle 优先级），用户可能先点了「继续下载」。此时全局客户端还没赋值，
        /// 旧代码会直接放弃续传 —— 用户只觉得"点了没反应"。</para>
        /// <para>顺序：① 已有客户端直接用；② 等启动时那次自动登录（最多 15 秒）；
        /// ③ 还不行就用保存的 Cookie 现建一个（取直链只需要 Cookie，
        /// 昵称/头像的校验可以后补）；④ 都没有才返回 null。</para>
        /// </summary>
        private async Task<QuarkClient> EnsureClientForResumeAsync()
        {
            if (_client != null) return _client;

            LogDetail("续传需要登录态，正在等待自动登录完成…");
            var t = _startupLoginTask;
            if (t != null)
            {
                try { await Task.WhenAny(t, Task.Delay(15000)).ConfigureAwait(true); }
                catch { }
            }
            if (_client != null)
            {
                LogDetail("自动登录已完成，开始续传。");
                return _client;
            }

            // 兜底：保存的 Cookie 足够取直链了（账号校验只影响昵称/头像的显示）
            if (!string.IsNullOrEmpty(_settings.Cookie))
            {
                try
                {
                    LogDetail("自动登录尚未完成，已直接用保存的登录态开始续传。");
                    return new QuarkClient(_settings.Cookie);
                }
                catch (Exception ex)
                {
                    LogDetail("用保存的登录态续传失败: " + ex.Message);
                }
            }
            return null;
        }

        private async Task ResumePendingBatchAsync(List<PendingTask> tasks)
        {
            // 「弹出续传窗口时我点继续下载，但左下角的账号卡
            //  还没显示登录，这时会怎么样？」
            // 查证：续传框是在 Idle 优先级弹的，而自动登录（ValidateAsync + 走 Python 桥
            // 取昵称/头像）是**异步**的，很可能还没跑完 —— 此时客户端字段仍是 null，
            // 原来的代码会**直接 return**，只在任务页留一行日志，
            // 用户看到的就是"点了没反应"，而且完全不知道为什么。
            // → 现在：① 先等一会儿自动登录；② 还不行就用保存的 Cookie 现建一个
            //   **局部**客户端（取直链只需要 Cookie，账号昵称/头像的校验可以后补）；
            //   ③ 确实没有登录态才明确弹框告知（而不是静默放弃）。
            // 注意用的是**局部** client：自动登录完成时会换掉全局客户端，
            // 若直接用字段，换的那一刻可能在途请求会被 Dispose 掉。
            var client = await EnsureClientForResumeAsync();
            if (client == null)
            {
                Log("续传需要先登录。请登录后重新下载（分片在，会自然续传）。");
                AppDialog.Show(this,
                    "续传需要重新获取直链，而这必须处于登录状态。\n\n"
                    + "现在还没有可用的登录态，所以这次没有开始续传。\n"
                    + "分片都还在，登录之后再点一次「继续下载」即可（不会重下）。",
                    "需要先登录",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            SetBusy(true);
            var jobs = new List<DownloadJob>();
            try
            {
                foreach (var p in tasks)
                {
                    try
                    {
                        string url = null;

                        if (p.FromShare)
                        {
                            // 分享模式：先用记录里的 pwd_id/stoken/token 重取（GetShareDownloadAsync
                            // 的参数是显式传入的，不依赖客户端的"当前分享"状态）。
                            // stoken 一般几小时~几天就过期，过期后这一路必失败。
                            // 交给 GetShareLinkWithReResolveAsync：旧凭证失败就自动重解析分享
                            // （换新 stoken + 新 share_fid_token）再试 —— 用户点一次续传即自愈。
                            url = await GetShareLinkWithReResolveAsync(
                                client, p.Fid, p.Token, p.PwdId, p.Stoken, p.Passcode,
                                p.StartFid, p.ShareRelDir,
                                (freshStoken, freshToken) =>
                                {
                                    // 只在真正发生重解析时才打（旧 stoken 还有效时不会进这里）
                                    Log("[" + p.Name + "] 分享凭证已过期，已自动重新解析分享。");
                                    // 写回记录：下面构造 job.Pending 时 Stoken = p.Stoken 用的就是新值
                                    p.Stoken = freshStoken;
                                    p.Token = freshToken;
                                }, default(CancellationToken)).ConfigureAwait(false);
                        }
                        else
                        {
                            var links = await client.GetDownloadAsync(new List<string> { p.Fid });
                            url = links?.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl))?.DownloadUrl;
                        }

                        if (string.IsNullOrEmpty(url))
                        {
                            Log("[" + p.Name + "] 续传取链失败（可能分享已失效或需要重新解析）。");
                            continue;
                        }

                        string dest = p.Dest;
                        try { Directory.CreateDirectory(Path.GetDirectoryName(dest)); } catch { }

                        var job = new DownloadJob
                        {
                            Name = p.Name ?? Path.GetFileName(dest),
                            Url = url,
                            Dest = dest,
                            Cookie = client.CookieStr,
                            Threads = _settings.Threads,
                            PartsRoot = string.IsNullOrWhiteSpace(_settings.PartsRoot) ? null : _settings.PartsRoot,
                            Pending = new PendingInfo
                            {
                                PwdId = p.PwdId, Stoken = p.Stoken, Passcode = p.Passcode,
                                Fid = p.Fid, Token = p.Token, FromShare = p.FromShare,
                                RelDir = p.RelDir ?? "", Dest = dest, Size = p.Size,
                                // 自动重解析分享的上下文（stoken 过期自愈用）。
                                // 上面取链若触发过重解析，p.Stoken/p.Token 已是新值。
                                StartFid = p.StartFid, ShareRelDir = p.ShareRelDir,
                            },
                        };
                        var captured = job;
                        job.LinkRefresher = ct => RefreshLinkAsyncForPending(captured, ct);
                        // 免转存任务续传取不到新链（stoken 过期）→ 失败文案要说"需重新解析分享"
                        job.ResumeCannotRefresh = p.FromShare;
                        jobs.Add(job);
                    }
                    catch (Exception ex)
                    {
                        Log("[" + (p.Name ?? "?") + "] 续传失败: " + ex.Message);
                    }
                }

                if (jobs.Count == 0)
                {
                    Log("没有任务成功取到直链。");
                    return;
                }

                foreach (var job in jobs)
                    _tasks.Add(new TaskItem(job, job.Name));
                Log("已续传 " + jobs.Count + " 个任务。");
                RunJobs(jobs);
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>给"续传任务"用的重新取链回调（基于 Job 上的 Pending 上下文）。</summary>
        private async Task<string> RefreshLinkAsyncForPending(DownloadJob job, CancellationToken ct)
        {
            var p = job.Pending;
            if (p == null || string.IsNullOrEmpty(p.Fid))
                return null;
            try
            {
                if (p.FromShare)
                {
                    // 与续传入队同理：旧 stoken 过期就自动重解析分享换新凭证。
                    // 写回 p.Stoken/p.Token —— job.Pending 是引用：本次换链立即用上新值，
                    // 之后落盘的 PendingTask（保存时从 pi 序列化）也是新值。
                    return await GetShareLinkWithReResolveAsync(
                        _client, p.Fid, p.Token, p.PwdId, p.Stoken, p.Passcode,
                        p.StartFid, p.ShareRelDir,
                        (freshStoken, freshToken) =>
                        {
                            p.Stoken = freshStoken;
                            p.Token = freshToken;
                        }, ct).ConfigureAwait(false);
                }

                var links = await _client.GetDownloadAsync(new List<string> { p.Fid }).ConfigureAwait(false);
                return links?.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl))?.DownloadUrl;
            }
            catch { return null; }
        }

        // ---------------- 免转存续传：stoken 过期自愈（2026-10-17） ----------------

        /// <summary>
        /// 免转存取链：先用手头的（可能已过期的）stoken / share_fid_token 试一次；
        /// 失败就**自动重解析分享** —— 重新申请 stoken、沿 ShareRelDir 逐级展开定位
        /// 目标文件拿到新的 share_fid_token，再取一次链。
        /// 免转存续传原先只认"保存下来的旧 stoken"，它一般几小时~几天就过期，
        /// 过期后 <see cref="QuarkClient.GetShareDownloadAsync"/> 一律失败：
        /// 点续传 → 该任务被跳过；下载中换链 → 任务中止。用户只能手动重新解析分享。
        /// 现在改成点一次续传/换链即自愈（分享被取消/改密码仍救不回，如实返回 null
        /// 让上层按原逻辑提示）。
        /// </summary>
        /// <param name="client">取链用的客户端（续传路径可能是局部 client，不能假设是 _client）。</param>
        /// <param name="fid">分享内文件 fid（分享内稳定不变，重解析后仍用它定位）。</param>
        /// <param name="token">share_fid_token。</param>
        /// <param name="pwdId">分享 ID。</param>
        /// <param name="stoken">分享凭证。</param>
        /// <param name="passcode">提取码；无码分享为空串/null。</param>
        /// <param name="startFid">分享进入目录 fid（普通分享为 "0"）。</param>
        /// <param name="shareRelDir">文件在分享内的完整相对目录（相对 startFid；"" = 进入目录本身）。</param>
        /// <param name="onReResolved">重解析成功时回调，把 (新 stoken, 新 token) 写回调用方记录。</param>
        /// <returns>直链；彻底失败返回 null（调用方按原逻辑提示"需重新解析分享"）。</returns>
        private async Task<string> GetShareLinkWithReResolveAsync(
            QuarkClient client, string fid, string token, string pwdId, string stoken,
            string passcode, string startFid, string shareRelDir,
            Action<string, string> onReResolved, CancellationToken ct)
        {
            if (client == null || string.IsNullOrEmpty(fid) || string.IsNullOrEmpty(pwdId))
                return null;

            // ① 先用现有凭据试一次 —— 绝大多数情况这一步就成，不值得惊动重解析
            string url = await TryShareDownloadAsync(client, fid, token, pwdId, stoken).ConfigureAwait(false);
            if (url != null)
                return url;

            ct.ThrowIfCancellationRequested();

            // ② 自动重解析：换新 stoken + 新 share_fid_token
            //    （async 方法不能用 out 参数，用 Tuple 回传：Item1 = 新 stoken，Item2 = 新 token）
            var fresh = await TryReResolveShareTokenAsync(
                client, pwdId, passcode, startFid, shareRelDir, fid, ct).ConfigureAwait(false);
            if (fresh == null)
            {
                LogDetail("分享直链获取失败，自动重新解析分享也没有成功（分享可能已被取消、删除或改了提取码）。");
                return null;
            }

            if (onReResolved != null)
                onReResolved(fresh.Item1, fresh.Item2);

            // ③ 用新凭据再取一次
            url = await TryShareDownloadAsync(client, fid, fresh.Item2, pwdId, fresh.Item1).ConfigureAwait(false);
            if (url != null)
                LogDetail("分享凭证已过期，已自动重新解析分享并拿到新直链。");
            return url;
        }

        /// <summary>取分享直链；吞掉一切接口异常，失败只返回 null（由上层决定提示文案）。</summary>
        private async Task<string> TryShareDownloadAsync(
            QuarkClient client, string fid, string token, string pwdId, string stoken)
        {
            try
            {
                var links = await client.GetShareDownloadAsync(
                    new List<string> { fid },
                    new List<string> { token ?? "" },
                    pwdId, stoken).ConfigureAwait(false);
                return links == null
                    ? null
                    : links.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl)).DownloadUrl;
            }
            catch (Exception ex)
            {
                LogDetail("获取分享直链失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 重新解析分享：申请新 stoken → 从 startFid 起沿 shareRelDir 逐级进目录 →
        /// 在最终目录里**按 fid** 找到目标文件的**新** share_fid_token。
        /// 按 fid 匹配而不是按名字：fid 在分享内不变，比文件名可靠。
        /// </summary>
        /// <returns>null = 失败；否则 Item1 = 新 stoken，Item2 = 新 share_fid_token。
        /// 用 Tuple 而不是 out 参数：async 方法不允许 ref/out。</returns>
        private async Task<Tuple<string, string>> TryReResolveShareTokenAsync(
            QuarkClient client, string pwdId, string passcode, string startFid,
            string shareRelDir, string fid, CancellationToken ct)
        {
            try
            {
                // ① 重新申请分享凭证（分享取消/改密码都会在这一步失败）
                string stoken = await client.GetStokenAsync(pwdId, passcode ?? "").ConfigureAwait(false);
                if (string.IsNullOrEmpty(stoken))
                    return null;

                // ② 从进入目录开始，沿完整相对路径逐级下钻。
                //    名字匹配做两手准备：_pathList 里存的是目录原名，而 RelDir 系列
                //    字段是 SanitizeFileName 规范化过的，原名带非法字符时两者不同。
                string pdirFid = string.IsNullOrEmpty(startFid) ? "0" : startFid;
                string rel = (shareRelDir ?? "").Replace('/', '\\').Trim('\\');
                if (rel.Length > 0)
                {
                    foreach (string seg in rel.Split('\\'))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (seg.Length == 0)
                            continue;
                        var dir = await FindShareEntryAsync(client, pwdId, stoken, pdirFid,
                            e => e.IsDir
                                 && (e.Name == seg || PathUtil.SanitizeFileName(e.Name) == seg),
                            ct).ConfigureAwait(false);
                        if (dir == null)
                            return null;   // 目录被改名/移动/删除 → 按路径找不到了
                        pdirFid = dir.Fid;
                    }
                }

                // ③ 最终目录里按 fid 定位文件，拿它的新 share_fid_token
                var file = await FindShareEntryAsync(client, pwdId, stoken, pdirFid,
                    e => !e.IsDir && e.Fid == fid, ct).ConfigureAwait(false);
                if (file == null || string.IsNullOrEmpty(file.Token))
                    return null;

                return Tuple.Create(stoken, file.Token);
            }
            catch (Exception ex)
            {
                LogDetail("自动重新解析分享失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 在分享的一个目录里**逐页**找第一个满足条件的条目，找到即停。
        /// 用一页一页的 <see cref="QuarkClient.ShareDetailPageAsync"/> 而不是整目录
        /// 拉完的 <see cref="QuarkClient.ShareDetailAsync"/>：目标文件常在靠前的页，
        /// 逐页找能少拉几十页（几千项的分享整目录拉完要十几秒）。
        /// </summary>
        private async Task<QuarkFileEntry> FindShareEntryAsync(
            QuarkClient client, string pwdId, string stoken, string pdirFid,
            Func<QuarkFileEntry, bool> pred, CancellationToken ct)
        {
            for (int page = 1; page <= 200; page++)   // 200 页兜底，与 ShareDetailAsync 一致
            {
                ct.ThrowIfCancellationRequested();
                var dir = await client.ShareDetailPageAsync(pwdId, stoken, pdirFid, page).ConfigureAwait(false);
                if (dir == null || dir.Items == null)
                    return null;
                foreach (var e in dir.Items)
                    if (pred(e))
                        return e;
                if (!dir.HasMore)
                    break;
            }
            return null;
        }

        // ---------------- 播放器探测（边下边播用） ----------------

        /// <summary>刷新设置页的播放器状态文案（同步版，供启动阶段调用）。</summary>
        private void RefreshPlayerStatus()
        {
            ApplyPlayerStatus(PlayerLocator.Find());
        }

        /// <summary>把探测结果落到 UI —— 纯 UI 操作，必须在 UI 线程调用。</summary>
        private void ApplyPlayerStatus(PlayerLocator.Player p)
        {
            try
            {
                if (p == null)
                {
                    // 🚨 只提 VLC（2026-10-04 用户要求）：全项目只对 VLC 做过参数适配，
                    //    其它播放器（mpv / PotPlayer）可能行为不一致 → 干脆不提，避免给出错误预期。
                    PlayerStatusText.Text = "未检测到 VLC。"
                        + "「打开」将回退到系统默认程序 —— 但边下边播未必能用，"
                        + "因为系统自带播放器通常打不开「还没下完」的文件。";
                    GetVlcBtn.Visibility = System.Windows.Visibility.Visible;
                }
                else
                {
                    PlayerStatusText.Text = "已检测到 VLC（" + p.ExePath + "）—— 适合边下边播。";
                    GetVlcBtn.Visibility = System.Windows.Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                PlayerStatusText.Text = "检测播放器失败: " + ex.Message;
            }
        }

        /// <summary>
        /// 「重新检测」—— 探测播放器。
        /// </summary>
        /// <remarks>
        /// ⚠️ 必须异步：<see cref="PlayerLocator.Find"/> 除了读注册表、查几个固定路径，
        /// 还会**遍历整个 PATH** 逐个 <c>File.Exists</c>。PATH 里只要有一个网络路径，
        /// 这一步就会卡好几秒 —— 原来是同步调用，等于把整个窗口冻住（点哪都没反应）。
        /// 现在把探测丢到后台线程，按钮就地显示「检测中」+ 转圈。
        /// </remarks>
        private async void OnRecheckPlayer(object sender, RoutedEventArgs e)
        {
            long t0 = BusyClock();
            if (!BeginBusy(RecheckPlayerBtn, "检测中")) return;
            try
            {
                PlayerLocator.Player p;
                try
                {
                    p = await Task.Run(() => PlayerLocator.Find());
                }
                catch (Exception ex)
                {
                    PlayerStatusText.Text = "检测播放器失败: " + ex.Message;
                    return;
                }
                ApplyPlayerStatus(p);
            }
            finally
            {
                await EndBusyAsync(RecheckPlayerBtn, t0);
            }
        }

        private void OnGetVlc(object sender, RoutedEventArgs e)
        {
            const string url = "https://www.videolan.org/vlc/";
            // 这个按钮只负责打开官方页面，不会偷偷下载或安装播放器。
            // ShellLaunch 与 Python webbrowser.open 对齐，激活已有默认浏览器实例。
            // 都失败就**弹窗**给用户，并把网址原文显示出来供复制 —— Log 里
            // 可能被大量日志淹没，弹窗不会。
            if (!ShellLaunch.OpenUrl(url))
            {
                Log("打不开浏览器，请手动访问：" + url);
                AppDialog.Show(this,
                    "无法启动浏览器。\n\n请手动复制下方地址到浏览器打开：\n\n" + url,
                    "需要手动打开",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
        }

        // ---------------- 设置 ----------------

        /// <summary>
        /// 可选的并发档位。**最后一项必须等于 SegmentedDownloader.MaxRealConcurrency**，
        /// 否则会出现"界面能选、代码却夹不到"的假承诺。
        /// 注意这个数组的顺序必须与 XAML 里 ComboBoxItem 的顺序严格一致。
        /// 一致性由下面的静态构造函数在启动时自检，改漏了会直接抛异常暴露。
        /// </summary>
        private static readonly int[] ThreadPresets = { 16, 32, 64, 128, 256, 512 };

        /// <summary>并发上限的单一事实来源，取自核心层常量。</summary>
        private static int ConcurrencyCap => GeZi.Core.Download.SegmentedDownloader.MaxRealConcurrency;

        static MainWindow()
        {
            // 防止"改了核心常量却忘了改 UI 档位"这类不一致再次发生。
            // 这种 bug 很隐蔽：界面看起来正常，实际选的值被悄悄夹掉，
            // 用户会觉得程序在骗他。宁可启动就炸，也不要静默失配。
            int last = ThreadPresets[ThreadPresets.Length - 1];
            if (last != ConcurrencyCap)
            {
                throw new InvalidOperationException(
                    "并发档位与核心层上限不一致：档位最高 " + last +
                    "，核心上限 " + ConcurrencyCap +
                    "。请同步修改 ThreadPresets 与 XAML 的 ComboBoxItem。");
            }
        }

        private static int PresetToIndex(int threads)
        {
            int best = 1, bestDiff = int.MaxValue;
            for (int i = 0; i < ThreadPresets.Length; i++)
            {
                int diff = Math.Abs(ThreadPresets[i] - threads);
                if (diff < bestDiff) { bestDiff = diff; best = i; }
            }
            return best;
        }

        private int SelectedThreads()
        {
            int i = ThreadsBox.SelectedIndex;
            if (i < 0 || i >= ThreadPresets.Length) i = 1;   // 默认 32
            int v = ThreadPresets[i];
            // 兜底：即使档位表被改错，也不会把超过核心上限的值交给下载器。
            if (v > ConcurrencyCap) v = ConcurrencyCap;
            return v;
        }

        private void LoadSettingsToUi()
        {
            // 置位期间控件赋值会触发 SelectionChanged/Click，必须挡住，
            // 否则会用"界面上的默认值"反向覆盖用户已保存的配置。
            _applyingSettings = true;
            try
            {
                ThreadsBox.SelectedIndex = PresetToIndex(_settings.Threads);
                ConcurrentBox.Text = _settings.ConcurrentTasks.ToString();
                OutDirBox.Text = _settings.OutDir;
                AutoCleanCheck.IsChecked = _settings.AutoClean;
                // "默认留空的是哪一个目录，直接打上去"
                // 原语义「留空 = 与目标文件同目录」没有单一目录，用户看不到分片会去哪。
                // 现在：留空就落到**下载目录**，并且同步写进设置 —— 保证"看到的就是生效的"。
                if (!string.IsNullOrWhiteSpace(_settings.PartsRoot))
                {
                    PartsRootBox.Text = _settings.PartsRoot;
                }
                else
                {
                    PartsRootBox.Text = _settings.OutDir ?? "";
                    if (!string.IsNullOrWhiteSpace(_settings.OutDir))
                        _settings.PartsRoot = _settings.OutDir;
                }
                HidePartsCheck.IsChecked = _settings.HideParts;
                CloseActionBox.SelectedIndex = (int)_settings.OnCloseButton;
                // 剪贴板自动解析（默认开）。注意这里**只回填控件**：真正的注册在窗口句柄
                // 建好之后由 AttachWindowHooks→ApplyClipboardListener 做。
                ClipboardAutoParseCheck.IsChecked = _settings.ClipboardAutoParse;
                // 跟随系统代理（默认关 = 直连）
                UseSystemProxyCheck.IsChecked = _settings.UseSystemProxy;
                // 外观主题：把配置里的选择回填到三个单选（_applyingSettings 已挡掉 Checked）
                SyncThemeRadios();
            }
            finally
            {
                _applyingSettings = false;
            }
            // 把「分片目录隐藏」告知核心层（静态开关，只影响外观）
            SegmentedDownloader.HidePartsDir = _settings.HideParts;
            // 把「跟随系统代理」告知核心层（静态开关，影响之后新建的所有连接）。
            // ⚠️ 必须在**建 QuarkClient / 起下载之前**写进去，否则首批连接仍按旧值建。
            NetworkOptions.UseSystemProxy = _settings.UseSystemProxy;
            // 把诊断日志接进界面日志框：外部启动是否成功、ShellExecute 返回码多少，
            // 都要能看到 —— 否则每次失败都只能猜。
            ShellLaunch.Log = Log;
            // 探测本机播放器，供设置页展示 + 「打开」按钮选用
            RefreshPlayerStatus();
        }

        /// <summary>
        /// 设置改为**即时生效**，不再有「保存设置」按钮。
        /// 各控件的触发时机（见 MainWindow.xaml 的事件绑定）：
        ///   · ComboBox  → SelectionChanged（选中即生效）
        ///   · CheckBox  → Click（勾选即生效）
        ///   · TextBox   → LostFocus（移开焦点才生效）+ KeyDown 回车立刻生效
        /// 为什么文本框不用 TextChanged：每敲一个字符都会 RecreateScheduler()，
        /// 正在下载时反复重建调度器代价很大，而且中间态（如 "1" → "12"）会被
        /// 当成有效值落盘。用 LostFocus 是最稳的折中。
        /// _applyingSettings 是重入保护：LoadSettingsToUi() 里给控件赋值同样会
        /// 触发 SelectionChanged/Click，若不挡住会在启动时把默认值写回覆盖用户配置。
        /// </summary>
        private void OnSettingChanged(object sender, RoutedEventArgs e)
        {
            if (_applyingSettings) return;
            ApplySettings();
        }

        // ================= 外观主题（2026-10-04 新增） =================

        /// <summary>连续点击时只让最后一次收尾。</summary>
        private int _themeRevealSeq;

        /// <summary>
        /// 把当前视觉树抓成位图。
        /// ⚠️ 用 RenderTargetBitmap（同步绘制视觉树），不用屏幕截图 ——
        ///    不会把别的程序拍进来，也不受窗口遮挡/最小化影响。
        /// </summary>
        private static RenderTargetBitmap CaptureRoot(FrameworkElement rootEl, double scale = 1.0)
        {
            // ⚠️ 2026-10-18 用户明确不接受过渡期的模糊（"中间有一整个界面变得特别模糊"）
            //    → 从 0.6 倍改回**全分辨率**。0.6 倍抓图铺满全窗，圆外区域必然糊；
            //    全分辨率虽然抓图耗时回到 ~100ms，但抓的是像素级清晰的旧图，
            //    真正"点击后立刻扩散"的结构不变（中间没有任何额外等待）。
            var dpi = VisualTreeHelper.GetDpi(rootEl);
            double sx = dpi.DpiScaleX * scale;
            double sy = dpi.DpiScaleY * scale;
            int w = Math.Max(1, (int)Math.Ceiling(rootEl.ActualWidth * sx));
            int h = Math.Max(1, (int)Math.Ceiling(rootEl.ActualHeight * sy));
            var bmp = new RenderTargetBitmap(w, h, 96 * sx, 96 * sy, PixelFormats.Pbgra32);
            bmp.Render(rootEl);
            return bmp;
        }

        /// <summary>
        /// 主题切换 = 「双快照 + 圆形揭示」。
        /// 旧实现（已废弃）：动画期间只放大一个**纯色圆**，底层 UI 一直是旧主题 ——
        /// 用户看到的只是"一块颜色在放大"，而不是新界面被揭开。
        /// 新实现（Material You 的做法）：
        ///   ① 切换前先抓一张**旧 UI 快照**；
        ///   ② 立刻换肤 + UpdateLayout，再抓一张**新 UI 快照**；
        ///   ③ 覆盖层叠两张图：底 = 旧快照，上 = 新快照（EllipseGeometry 裁剪）；
        ///   ④ 动画只改裁剪圆半径，从点击点向外扩展 ——
        ///      每一帧露出来的都是**已经完成切换的新 UI**。
        /// ⚠️ ①②③ 全在同一个 UI 线程消息里同步执行（中间没有 await），
        ///    WPF 不会渲染中间态，用户看不到"直接跳变"。
        /// ⚠️ 换肤后的收尾（存配置 / 日志 / 提示文案）放在动画 Completed 里，
        ///    避免磁盘 I/O 卡住动画启动。
        /// </summary>
        private void PlayThemeReveal(FrameworkElement origin, AppTheme want, Action afterApply)
        {
            var rootEl = RootBorder as FrameworkElement;
            if (rootEl == null || origin == null) { ThemeManager.SetTheme(want); afterApply(); return; }

            double w = rootEl.ActualWidth, h = rootEl.ActualHeight;
            if (w < 1 || h < 1) { ThemeManager.SetTheme(want); afterApply(); return; }

            Point center;
            try
            {
                center = origin.TranslatePoint(
                    new Point(origin.ActualWidth / 2, origin.ActualHeight / 2), rootEl);
            }
            catch
            {
                center = new Point(w / 2, h / 2);
            }

            double maxR = Math.Sqrt(
                Math.Pow(Math.Max(center.X, w - center.X), 2) +
                Math.Pow(Math.Max(center.Y, h - center.Y), 2));

            // ① 旧快照垫底（圆外露出来的就是它）。
            //    ⚠️ 2026-10-18 用户实测不接受模糊 → 必须全分辨率（0.6 倍铺满全窗必糊）。
            ThemeOldSnapshot.Source = CaptureRoot(rootEl);
            ThemeOldSnapshot.Visibility = Visibility.Visible;

            // ② 换肤 + 提示文案 + 强制布局
            ThemeManager.SetTheme(want);
            UpdateThemeHint();
            rootEl.UpdateLayout();

            // ③ 真实界面加圆形 Clip（半径 0 → 圆外立刻露出旧图）
            // ⚠️ 用 Clip 而不是"覆盖层 + 双图"：真实 UI 一直在渲染，
            //    分段控件的滑块动画可以继续滑，不会被快照冻在旧位置。
            var clip = new EllipseGeometry(center, 0, 0);
            rootEl.Clip = clip;

            // ④ 动画：只放大裁剪圆
            int my = ++_themeRevealSeq;
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var dur = TimeSpan.FromMilliseconds(420);
            var rx = new DoubleAnimation(0, maxR, dur) { EasingFunction = ease };
            var ry = new DoubleAnimation(0, maxR, dur) { EasingFunction = ease };
            rx.Completed += (s, e) =>
            {
                if (my != _themeRevealSeq) return;
                try { afterApply(); } catch { }
                rootEl.Clip = null;
                ThemeOldSnapshot.Visibility = Visibility.Collapsed;
                ThemeOldSnapshot.Source = null;
            };
            clip.BeginAnimation(EllipseGeometry.RadiusXProperty, rx);
            clip.BeginAnimation(EllipseGeometry.RadiusYProperty, ry);
        }

        /// <summary>
        /// 设置页「个性化」三选一切换。走独立处理器而不是 <see cref="OnSettingChanged"/>，
        /// 因为换主题**不需要**重建调度器 / 重读所有设置项 —— 那会顺带触发一串
        /// 无关的副作用（比如重建下载调度器会打断在途任务）。
        /// </summary>
        private void OnThemeChanged(object sender, RoutedEventArgs e)
        {
            // 加载配置回填控件时也会触发 Checked，必须挡掉（否则会把 System 又写回成具体值）
            if (_applyingSettings) return;

            AppTheme want;
            if (ThemeDarkRadio.IsChecked == true) want = AppTheme.Dark;
            else if (ThemeSystemRadio.IsChecked == true) want = AppTheme.System;
            else want = AppTheme.Light;

            try
            {
                // 换肤后的收尾（动画结束时才做：存盘 / 日志 / 提示文案）
                Action persist = () =>
                {
                    _settings.Theme = want;
                    SettingsStore.Save(_settings);
                    UpdateThemeHint();
                    Log(ThemeLogText(want));
                };

                // 目标明暗（选「跟随系统」时按当前系统判断）
                bool targetDark = want == AppTheme.Dark
                    || (want == AppTheme.System && ThemeManager.IsSystemDark());

                // 明暗实际没变（例如 浅色 ↔ 跟随系统，而系统本来就是浅色）→ 直接切，不播动画
                if (targetDark == ThemeManager.IsDarkEffective)
                {
                    ThemeManager.SetTheme(want);
                    persist();
                    return;
                }

                // 2026-10-18 用户又反馈「切换时整个界面特别模糊」→ 旧快照改回全分辨率
                // （0.6 倍快照铺满全窗必然糊）。现在唯一的前置耗时是抓旧图（全分辨率 ≈ 100ms）；
                // 抓完立刻换肤 + 上圆形 Clip，滑块与扩散**同时进行** ——
                // Clip 方案下真实 UI 一直在渲染，滑块不会被冻住。
                PlayThemeReveal(sender as FrameworkElement, want, persist);
            }
            catch (Exception ex)
            {
                Log("切换主题失败：" + ex.Message);
            }
        }

        /// <summary>把配置里的主题回填到单选按钮（加载设置时调用）。</summary>
        private void SyncThemeRadios()
        {
            try
            {
                switch (_settings.Theme)
                {
                    case AppTheme.Dark: ThemeDarkRadio.IsChecked = true; break;
                    case AppTheme.Light: ThemeLightRadio.IsChecked = true; break;
                    default: ThemeSystemRadio.IsChecked = true; break;
                }
                UpdateThemeHint();
            }
            catch { }
        }

        /// <summary>
        /// 说明行的文案随选择变化：
        /// 选「跟随系统」时要讲清"系统是深色/浅色"以及"改系统会自动跟"，
        /// 否则用户会疑惑"我明明选了跟随系统，现在到底是哪个"。
        /// </summary>
        private void UpdateThemeHint()
        {
            try
            {
                if (ThemeHintText == null) return;

                if (ThemeSystemRadio.IsChecked == true)
                {
                    // 去掉括号里的「（当前系统为浅色）」——
                    // 选了「跟随系统」后当前是什么色，界面本身就是答案，这句是噪音。
                    ThemeHintText.Text = "跟随系统时，Windows 切换深色/浅色，本程序会自动跟着变。";
                }
                else if (ThemeDarkRadio.IsChecked == true)
                {
                    ThemeHintText.Text = "始终使用深色界面，不受系统设置影响。";
                }
                else
                {
                    ThemeHintText.Text = "始终使用浅色界面，不受系统设置影响。";
                }
            }
            catch { }
        }

        private static string ThemeLogText(AppTheme t)
        {
            if (t == AppTheme.Dark) return "外观已切换为深色";
            if (t == AppTheme.Light) return "外观已切换为浅色";
            return "外观已设为跟随系统（当前" + (ThemeManager.IsSystemDark() ? "深色" : "浅色") + "）";
        }

        /// <summary>文本框里按回车 = 立刻生效（不必先点别处）。</summary>
        private void OnSettingKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (_applyingSettings) return;
            ApplySettings();
            e.Handled = true;
        }

        /// <summary>
        /// 从界面读取全部设置项 → 落盘 → 重建调度器。
        /// 由 OnSettingChanged / OnSettingKeyDown 调用；没有"保存"按钮了。
        /// </summary>
        private void ApplySettings()
        {
            try
            {
                int conc;
                int threads = SelectedThreads();
                int.TryParse((ConcurrentBox.Text ?? "").Trim(), out conc);

                _settings.Threads = threads;

                if (conc < 1) conc = 1;
                if (conc > 64) conc = 64;
                _settings.ConcurrentTasks = conc;

                // ⚠️ 必须规范化：用户手输的可能是 `C:/a/b`（正斜杠），
                // 而 Path.Combine 拼文件名时用 `\` → 会得到混合斜杠路径，
                // 外部播放器（VLC）会把它当 URL 协议头而打不开（2026-10-04 实测）。
                _settings.OutDir = NormalizeDir(OutDirBox.Text);
                _settings.AutoClean = AutoCleanCheck.IsChecked == true;
                // 分片根目录变了 → 把**旧值**记入历史清单，否则旧目录里的残留分片
                // 之后既扫不到、也没入口清理（用户只能自己去翻磁盘删）。
                string newPartsRoot = (PartsRootBox.Text ?? "").Trim();
                RegisterPartsDirHistory(_settings.PartsRoot);
                _settings.PartsRoot = newPartsRoot;
                _settings.HideParts = HidePartsCheck.IsChecked == true;
                SegmentedDownloader.HidePartsDir = _settings.HideParts;
                _settings.OnCloseButton = (CloseAction)Math.Max(0,
                    Math.Min(2, CloseActionBox.SelectedIndex));

                SettingsStore.Save(_settings);
                RecreateScheduler();

                Log(string.Format("设置已生效（单文件并发 {0} 路，同时下载 {1} 个）", threads, conc));
            }
            catch (Exception ex)
            {
                Log("设置保存失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 把某个分片根目录记入历史清单（去重、最近使用在末尾、超长时丢弃最旧的）。
        /// 移植自 Python Flet 版的 `_register_part_dir`。空白与「默认位置」不记。
        /// </summary>
        private void RegisterPartsDirHistory(string dir)
        {
            dir = (dir ?? "").Trim();
            if (dir.Length == 0) return;
            try
            {
                string norm = Path.GetFullPath(dir);
                if (_settings.PartsDirsKnown == null)
                    _settings.PartsDirsKnown = new List<string>();
                for (int i = _settings.PartsDirsKnown.Count - 1; i >= 0; i--)
                {
                    string e;
                    try { e = Path.GetFullPath(_settings.PartsDirsKnown[i]); }
                    catch { e = _settings.PartsDirsKnown[i]; }
                    if (string.Equals(e, norm, StringComparison.OrdinalIgnoreCase))
                        _settings.PartsDirsKnown.RemoveAt(i);
                }
                _settings.PartsDirsKnown.Add(dir);
                while (_settings.PartsDirsKnown.Count > AppSettings.PartsDirsMax)
                    _settings.PartsDirsKnown.RemoveAt(0);
            }
            catch { }
        }

        /// <summary>
        /// 当前生效 + 历史用过的**全部分片根目录**，用于清理/统计残留分片。
        /// 移植自 Python Flet 版的 `_part_dirs_all`：程序曾经用过、后来被切走的目录
        /// 也一并返回 —— 否则那些残留分片既扫不到、也清不掉。
        /// </summary>
        private List<string> AllPartsRoots()
        {
            var outList = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string d)
            {
                d = (d ?? "").Trim();
                if (d.Length == 0) return;
                string k;
                try { k = Path.GetFullPath(d); }
                catch { k = d; }
                if (seen.Add(k)) outList.Add(d);
            }

            Add(_settings.PartsRoot);
            if (_settings.PartsDirsKnown != null)
                foreach (var d in _settings.PartsDirsKnown) Add(d);
            return outList;
        }

        private void OnBrowsePartsRoot(object sender, RoutedEventArgs e)
        {
            string start = (PartsRootBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(start)) start = _settings.OutDir;
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = start })
            {
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    PartsRootBox.Text = dlg.SelectedPath;
                    ApplySettings();   // 即时生效（没有"保存"按钮了）
                }
            }
        }

        /// <summary>
        /// 在资源管理器中打开「分片/缓存根目录」。
        /// 原来这一行只有「浏览…」，用户希望像「下载目录」那样
        /// 也能一键打开所在目录。
        /// 与 <see cref="OnOpenOutDir"/> 的差异（刻意如此）：
        ///   - 留空时的语义是"与目标文件同目录"，没有单一目录可开 ——
        ///     此时**回退到下载目录**（目标文件默认就下在那里），而不是报错。
        ///   - 同样**不自动创建**目录：用户只是想在资源管理器里看看；
        ///     若目录不存在就明确提示，别悄悄建一堆空目录。
        /// </summary>
        private void OnOpenPartsRoot(object sender, RoutedEventArgs e)
        {
            string dir = (PartsRootBox.Text ?? "").Trim();
            // 留空 = 分片与目标文件同目录 → 打开下载目录即可。
            if (string.IsNullOrEmpty(dir)) dir = _settings.OutDir;
            if (string.IsNullOrEmpty(dir))
            {
                AppDialog.Show(this, "还没设置分片根目录，也没有下载目录可打开。",
                    "无可打开的目录", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }
            try
            {
                if (!Directory.Exists(dir))
                {
                    // 不自动创建：仅提示，避免误建目录。
                    LogDetail("分片根目录不存在：" + dir);
                    AppDialog.Show(this,
                        "分片/缓存根目录还不存在。\n\n请先点「浏览…」选择一个目录，" +
                        "或留空使用默认（与目标文件同目录）。\n\n路径：\n" + dir,
                        "目录不存在", System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                    return;
                }
                // 走统一入口：先 shell，失败再显式启动 explorer（与 OnOpenOutDir 一致）。
                if (!ShellLaunch.OpenFolder(dir))
                {
                    Log("打不开分片根目录，请手动打开：" + dir);
                    AppDialog.Show(this,
                        "无法打开分片/缓存根目录。\n\n请手动复制下方路径到资源管理器：\n\n" + dir,
                        "需要手动打开", System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                LogDetail("打开分片根目录失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 应用设置变更。
        /// 曾经的实现是无条件 Dispose + new。但保存设置（改并发/连接预算）时
        /// 下载往往正在跑：Dispose 会把内部 SemaphoreSlim 一起释放，
        /// 而此刻 RunOneAsync 正持有它 —— 接下来的 sem.Release() 就抛
        /// ObjectDisposedException("已释放该信号量")，任务被中断且提示莫名其妙。
        /// 更隐蔽的后果：RunJobs 读的是字段 _scheduler，旧对象被 Dispose 后
        /// 新对象上位，于是"正在跑的那批"用旧限流、"新点的"用新限流 ——
        /// 两个 scheduler 并存，ConcurrentTasks 限流直接失效。
        /// 现在的策略：
        ///   - 空闲时：正常重建（新并发数立即生效）
        ///   - 下载中：只更新 SettingsStore，并提示"并发数将在下次下载生效"。
        ///            绝不 Dispose 正在用的对象。
        /// </summary>
        private void RecreateScheduler()
        {
            // 连接上限现在由 SegmentedDownloader / QuarkSession 各自显式设定，
            // 不再暴露"全局连接预算"给用户（原来的设置项实际不起作用）。
            // NetworkConfig.Apply 仍在调度器构造里以固定值调用一次，兜底全局 DefaultConnectionLimit。
            if (_batchRunning)
            {
                // 正在下载：绝不触碰现有 scheduler，避免上面描述的两种事故。
                Log("并发数已保存，将在下一批下载生效（当前批次正在运行，不打断）。");
                return;
            }

            // 空闲：安全地重建
            try { _scheduler?.Dispose(); } catch { }
            _scheduler = new DownloadScheduler(_settings.ConcurrentTasks);
            _scheduler.Log = LogFromWorker;
            _scheduler.Diagnostics = OnDiagnostics;
        }

        /// <summary>当前是否有下载批次在跑。用于阻止"运行中重建 scheduler"这类危险操作。</summary>
        private volatile bool _batchRunning;

        private void OnBrowseOutDir(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = _settings.OutDir })
            {
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    OutDirBox.Text = dlg.SelectedPath;
                    ApplySettings();   // 即时生效（没有"保存"按钮了）
                }
            }
        }

        /// <summary>在文件资源管理器中打开当前下载目录；目录不存在时先创建。</summary>
        private void OnOpenOutDir(object sender, RoutedEventArgs e)
        {
            string dir = (OutDirBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(dir)) dir = _settings.OutDir;
            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    LogDetail("下载目录不存在，已创建: " + dir);
                }
                // 走统一入口：先 shell，失败再显式启动 explorer。
                // 两级都失败就**弹窗**给用户 —— 用户实测 explorer.exe 启动即崩
                // (0xc0000142)，纯 Log 容易被埋没，弹窗强制可见，且完整路径可直接复制。
                if (!ShellLaunch.OpenFolder(dir))
                {
                    Log("打不开下载目录，请手动打开：" + dir);
                    AppDialog.Show(this,
                        "无法打开下载目录。\n\n请手动复制下方路径到资源管理器：\n\n" + dir,
                        "需要手动打开",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                LogDetail("打开下载目录失败: " + ex.Message);
            }
        }

        // ---------------- 登录 ----------------

        // 原来的 OnLoginClick（只是 Tabs.SelectedIndex = 3）已随「登录」按钮一起删除：
        // 那个按钮和左侧「获取二维码」、下方「导入 Cookie」重复，用户反馈「没有任何作用」。

        /// <summary>
        /// 打开账号管理弹窗：列出本机保存的账号，可一键切换 / 删除，或去扫码登录新账号。
        /// 移植自 Flet 版的 _open_account_dialog。
        /// </summary>
        private void OnOpenAccounts(object sender, RoutedEventArgs e)
        {
            // 未登录且**一个已保存账号都没有**时，
            // 直接取二维码并提示扫码（而不是弹一个空列表让人更困惑）。
            if (_client == null)
            {
                int saved = 0;
                try { saved = AccountStore.LoadAll().Count; } catch { }
                if (saved == 0)
                {
                    Log("还没有已保存的账号，正在获取二维码…请用夸克 App 扫码登录。");
                    OnQrLogin(sender, e);
                    return;
                }
            }
            ShowAccountsDialog();
        }

        private void ShowAccountsDialog()
        {
            var list = AccountStore.LoadAll();
            string curCookie = _client?.CookieStr ?? _settings.Cookie ?? "";
            // 当前账号的 Key：优先用它判断"哪一行是当前账号"，Cookie 只作兜底
            string curKey = _settings.CurrentAccountKey ?? "";
            if (string.IsNullOrEmpty(curKey))
                curKey = _client?.AccountSig ?? "";

            var win = new Window
            {
                Owner = this,
                Title = "切换账号",
                ResizeMode = ResizeMode.CanResize,
            };
            DialogChrome.StyleWindow(win, 500, 460);

            var root = new Grid { Margin = new Thickness(20, 18, 20, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 说明
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 列表
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮

            var title = DialogChrome.PageTitle("切换账号");
            Grid.SetRow(title, 0);
            root.Children.Add(title);

            var hint = DialogChrome.Hint(
                "选择一个账号即可切换，不用重新扫码。删除会同时清掉本机保存的登录信息（含 Cookie）；"
                + "删除当前账号即等于退出登录。", 0, 12);
            Grid.SetRow(hint, 1);
            root.Children.Add(hint);

            var listBox = DialogChrome.StyledList();
            listBox.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            var listCard = DialogChrome.Card(listBox);
            listCard.Padding = new Thickness(0);
            Grid.SetRow(listCard, 2);
            root.Children.Add(listCard);

            Action rebuild = null;
            rebuild = () =>
            {
                listBox.Items.Clear();
                var accounts = AccountStore.LoadAll();
                if (accounts.Count == 0)
                {
                    listBox.Items.Add(new TextBlock
                    {
                        Text = "暂无已保存的账号，扫码登录后会自动保存一份。",
                        Foreground = (Brush)(TryFindResource("TextTertiaryBrush")
                                             ?? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))),
                        FontSize = 12,
                        Margin = new Thickness(14),
                    });
                    return;
                }
                foreach (var a in accounts)
                {
                    listBox.Items.Add(BuildAccountRow(a, curKey, curCookie, win, rebuild));
                }
            };
            rebuild();

            var scanBtn = DialogChrome.PrimaryButton("扫码登录新账号");
            // 「退出登录」按钮已按用户要求删除 ——
            // 用户原话：「这里退出登录有啥用啊，也不能一键切回。直接删掉这个功能，
            //           想退出的人直接删账号就行了根本不用点这一个」。
            // 所以退出登录只剩**一条**路径：在账号列表里删掉当前账号
            // （删除会连带清掉配置里的 Cookie，见 delBtn 的处理）。
            var closeBtn = DialogChrome.SecondaryButton("关闭");
            var btns = DialogChrome.ButtonRow(scanBtn, closeBtn);
            btns.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetRow(btns, 3);
            root.Children.Add(btns);

            scanBtn.Click += (s, ev) =>
            {
                // 扫码新账号：切到登录页并收起本弹窗（保留现有账号档案不动）。
                win.Close();
                Tabs.SelectedIndex = 3;
            };
            closeBtn.Click += (s, ev) => win.Close();

            win.Content = root;
            win.ShowDialog();
        }

        /// <summary>账号列表里的一行：昵称 + 副标题 + 「切换」「删除」两个操作。</summary>
        /// <param name="curKey">当前登录账号的 Key（来自 <see cref="AppSettings.CurrentAccountKey"/>）。</param>
        /// <param name="curCookie">
        /// 当前 Cookie。仅在 <paramref name="curKey"/> 为空时作为**兜底**判据
        /// （老配置文件里没有 CurrentAccountKey 字段）。
        /// </param>
        private Border BuildAccountRow(AccountProfile acc, string curKey, string curCookie,
            Window owner, Action rebuild)
        {
            // 原来只拿整串 Cookie 比字符串，而运行时 Cookie 会被
            // 重建/刷新（顺序、条目都会变）→ 永远比不中 → 当前账号的「切换」按钮
            // 居然还是可点的（用户反馈：「当我正在登录对应的账号的时候它的切换应该是不可使用的」）。
            // 现在优先比 Key（夸克返回的 uid，稳定），Cookie 只作兜底。
            bool isCurrent = !string.IsNullOrEmpty(curKey)
                ? string.Equals(acc.Key, curKey, StringComparison.Ordinal)
                : (!string.IsNullOrEmpty(curCookie) &&
                   string.Equals(acc.Cookie, curCookie, StringComparison.Ordinal));

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock
            {
                Text = acc.DisplayName,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            string sub;
            if (isCurrent) sub = "当前账号";
            else if (acc.LastUsedUtc > DateTime.MinValue)
                sub = "最近使用 " + acc.LastUsedUtc.ToLocalTime().ToString("MM-dd HH:mm");
            else sub = "本机已保存";
            texts.Children.Add(new TextBlock
            {
                Text = sub,
                FontSize = 11,
                // 跟随主题：暗色下 #2E7D32 那种暗绿在深底上几乎看不清，
                // 统一走 Success/TextTertiary 令牌，两套主题都达标。
                Foreground = (Brush)(TryFindResource(isCurrent
                    ? "SuccessBrush" : "TextTertiaryBrush")
                    ?? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))),
            });
            Grid.SetColumn(texts, 0);
            grid.Children.Add(texts);

            var switchBtn = new Button
            {
                Content = "切换",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(6, 0, 6, 0),
                IsEnabled = !isCurrent,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var secStyle = TryFindResource("SecondaryButton") as Style;
            if (secStyle != null) switchBtn.Style = secStyle;
            switchBtn.Click += async (s, ev) =>
            {
                switchBtn.IsEnabled = false;
                switchBtn.Content = "切换中…";
                await ApplyLoginAsync(acc.Cookie);
                owner.Close();
            };
            Grid.SetColumn(switchBtn, 1);
            grid.Children.Add(switchBtn);

            var delBtn = new Button
            {
                Content = "删除",
                Padding = new Thickness(12, 4, 12, 4),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var ghostStyle = TryFindResource("GhostButton") as Style;
            if (ghostStyle != null) delBtn.Style = ghostStyle;
            var dangerBrush = TryFindResource("DangerBrush") as Brush;
            if (dangerBrush != null) delBtn.Foreground = dangerBrush;
            delBtn.Click += (s, ev) =>
            {
                string tip = "将删除本机保存的账号「" + acc.DisplayName + "」的登录信息，" +
                             "之后要切回它需要重新扫码登录。";
                // 用户反馈「删除时是否真会把对应的 cookie 完全删除，
                // 旧版 Python Flet 版就删了但是账号还存在」—— 确实是那个问题：
                // 删档案只删了 accounts/{key}.xml，但**当前账号的 Cookie 还留在
                // GeZi.config.xml 里**，所以程序看起来还是登录状态。
                // 现在删当前账号会**同时退出登录**，才算真的删干净。
                if (isCurrent)
                    tip += "\n（这是当前账号：删除后会同时退出登录，需要重新登录才能再用。）";
                var r = AppDialog.Show(owner, tip + "\n\n确定删除吗？", "删除账号",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (r != MessageBoxResult.OK)
                    return;
                if (AccountStore.Remove(acc.Key))
                {
                    Log("已删除本机保存的账号「" + acc.DisplayName + "」");
                    // 删的是当前账号 → 把配置里的 Cookie 一起清掉，否则"账号还在"
                    if (isCurrent)
                    {
                        DoLogout("已删除当前账号，同时退出登录。");
                        owner.Close();
                        return;
                    }
                    rebuild();
                }
                else
                {
                    AppDialog.Show(owner, "删除失败（文件可能被占用）。", "删除账号",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            Grid.SetColumn(delBtn, 2);
            grid.Children.Add(delBtn);

            return new Border
            {
                Child = grid,
                Padding = new Thickness(12, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                CornerRadius = new CornerRadius(10),
                // 用主题令牌而不是硬编码灰，深色主题才能跟着变
                Background = (Brush)(TryFindResource("SurfaceAltBrush")
                                     ?? new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6))),
                BorderBrush = (Brush)(TryFindResource("BorderBrushSoft")
                                     ?? new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6))),
                BorderThickness = new Thickness(1),
            };
        }

        /// <summary>
        /// 当前扫码流程的取消源。点「获取二维码」或「刷新」都会新建一个并取消上一个 ——
        /// 否则旧轮询还在跑：它一旦过期就会调 HideQrImage() 把**新**二维码收起来。
        /// </summary>
        private CancellationTokenSource _qrFlowCts;

        private async void OnQrLogin(object sender, RoutedEventArgs e)
        {
            // 「刷新」按钮又加回来了（用户要求）：它和「获取二维码」共用本方法，
            // 点哪个都是重新取一张。区别只是语义 —— 获取=第一次，刷新=重取。
            var src = sender as Button;

            // 🚨 先取消上一次还没结束的流程（可能是正在等扫码的轮询）。
            //    取消后旧流程会在 OperationCanceledException 处静默退出，
            //    不会再改状态文字、也不会把新二维码收起来。
            var cts = new CancellationTokenSource();
            var prevFlow = _qrFlowCts;
            _qrFlowCts = cts;
            if (prevFlow != null)
            {
                try { prevFlow.Cancel(); } catch { /* 已释放/已取消，忽略 */ }
            }

            QrLoginBtn.IsEnabled = false;
            QrRefreshBtn.IsEnabled = false;
            // 「刷新」只在获取二维码之后出现 —— 点了「获取二维码」
            // 就意味着进入了二维码场景，此时让它显示；收起二维码（过期/登录成功）时再隐藏。
            QrRefreshBtn.Visibility = Visibility.Visible;
            SetQrStatus("正在获取二维码…", "#333");

            // ⚠️ 只给「取二维码」这一段加按钮加载态。
            // 后面的轮询是**等用户拿手机扫码**，可能持续几分钟 —— 那不是"加载"，
            // 一直转圈会让人以为程序卡住了。等二维码画出来就立刻收掉加载态。
            long t0 = BusyClock();
            if (!BeginBusy(src, "获取中"))
            {
                QrLoginBtn.IsEnabled = true;
                QrRefreshBtn.IsEnabled = true;
                return;
            }
            try
            {
                using (var svc = new LoginQrService())
                {
                    var (ok, token, qrUrl, err) = await svc.GetQrCodeAsync();
                    await EndBusyAsync(src, t0);
                    t0 = 0;
                    if (!ok)
                    {
                        SetQrStatus(err, "#C0392B");
                        // 没拿到二维码 → 「刷新」收回（只有真获取过才出现）
                        QrRefreshBtn.Visibility = Visibility.Collapsed;
                        return;
                    }

                    // 在界面里直接画出二维码，用户用夸克 App 扫屏幕即可登录，
                    // 不必再跳到浏览器（浏览器只是备用路径）。
                    ShowQrImage(qrUrl);

                    // ✅ 二维码画出来之后就把两个按钮**放开**：等待扫码期间用户随时可以点
                    //    「刷新」换一张（会取消本次轮询、重新取码），不必干等它超时。
                    QrLoginBtn.IsEnabled = true;
                    QrRefreshBtn.IsEnabled = true;

                    // 状态随轮询实时上报：等待扫码 → 已扫码待确认 → 成功/过期。
                    // 用 Progress<T> 自动切回 UI 线程（本方法运行在 UI 线程上）。
                    var status = new Progress<QrPollResult>(r =>
                    {
                        switch (r.Stage)
                        {
                            case QrLoginStage.WaitingScan:
                                SetQrStatus("请用夸克 App 扫描二维码（等待扫码…）", "#333");
                                break;
                            case QrLoginStage.ScannedWaitConfirm:
                                SetQrStatus("已扫码，请在手机上点击「确认登录」", "#1B6FB8");
                                break;
                            case QrLoginStage.Expired:
                                SetQrStatus(DescribeExpired(r), "#C0392B");
                                QrRefreshBtn.Focus();   // 过期了最顺手的动作是点「刷新」，把焦点给它方便回车重试
                                break;
                            case QrLoginStage.GotTicket:
                                SetQrStatus("已确认，正在换取登录态…", "#1B6FB8");
                                break;
                        }
                    });

                    var poll = await svc.PollForTicketWithStatusAsync(token, 300, status, cts.Token);
                    if (poll.Stage == QrLoginStage.Expired)
                    {
                        // 过期/无效：收起图片，提示用户点「刷新」重新取一张。
                        HideQrImage();
                        SetQrStatus(DescribeExpired(poll) + " 点「刷新」可重新获取。", "#C0392B");
                        return;
                    }

                    var (cok, cookie, cerr) = await svc.ExchangeTicketAsync(poll.Ticket);
                    if (!cok)
                    {
                        SetQrStatus(cerr, "#C0392B");
                        return;
                    }
                    await ApplyLoginAsync(cookie);
                    SetQrStatus("登录成功", "#2E7D32");
                    HideQrImage();
                }
            }
            catch (OperationCanceledException)
            {
                // 被「刷新」/「获取二维码」顶掉了：静默退出。
                // 界面状态归新的那一次流程管，这里**绝不能**去改状态文字或收二维码，
                // 否则会把用户刚拿到的新码盖掉。
            }
            catch (Exception ex)
            {
                SetQrStatus("登录失败: " + ex.Message, "#C0392B");
            }
            finally
            {
                // 只有「自己还是当前流程」时才恢复按钮 —— 否则旧的流程退出时
                // 会把新流程刚禁用的按钮又打开，造成并发取码。
                if (ReferenceEquals(_qrFlowCts, cts))
                {
                    QrLoginBtn.IsEnabled = true;
                    QrRefreshBtn.IsEnabled = true;
                }
                if (t0 != 0) await EndBusyAsync(src, t0);
            }
        }

        /// <summary>把轮询结果翻译成给用户看的过期/失败说明（未识别的码原样展示，不吞信息）。</summary>
        private static string DescribeExpired(QrPollResult r)
        {
            if (r.Status == -1)
                return "扫码超时，二维码可能已失效。";
            if (!string.IsNullOrEmpty(r.Message))
                return "二维码已失效（" + r.Message + "）。";
            return "二维码已失效。";
        }

        /// <summary>
        /// 更新状态提示文本（含颜色与图标），并确保提示框可见。
        /// colorHex 仍是调用方给的十六进制色（保持原有调用点不动），但**图标**按语义挑：
        /// 不再无脑用黄三角 —— 「正在等待扫码」是正常流程，配警告图标会让人以为出错了。
        /// </summary>
        private void SetQrStatus(string text, string colorHex)
        {
            QrStatusBox.Visibility = Visibility.Visible;
            QrStatusText.Text = text;

            var brush = ResolveStatusBrush(colorHex);
            var icon = PickStatusIcon(text, colorHex, out _);
            QrStatusIcon.Data = icon;
            QrStatusIcon.Stroke = brush;
            QrStatusText.Foreground = brush;
        }

        /// <summary>
        /// 把 <see cref="SetQrStatus"/> 传入的色值字符串解析成**跟随主题**的画刷。
        /// 调用方写的是硬编码十六进制（`#333` / `#C0392B`…），
        /// 其中 `#333`（近黑）在**深色主题下会完全看不见** —— 而这是登录页的状态提示，
        /// 用户看不到就不知道"到底在等什么"。所以这里把"色值"当作**语义标签**，
        /// 原样映射到主题令牌：
        ///   · `#333`             → TextSecondaryBrush（中性进行中）
        ///   · `#1B6FB8`（蓝）     → PrimaryBrush（需要用户操作的提示）
        ///   · `#C0392B`（红）     → DangerBrush
        ///   · `#2E7D32`（绿）     → SuccessBrush
        /// 未知色值走 <paramref name="colorHex"/> 原样解析（保持向后兼容），
        /// 解析失败再退回正文色，绝不出现"看不见的字"。
        /// </summary>
        private System.Windows.Media.Brush ResolveStatusBrush(string colorHex)
        {
            string token = null;
            if (!string.IsNullOrEmpty(colorHex))
            {
                string h = colorHex.TrimStart('#').ToUpperInvariant();
                if (h == "333" || h == "333333" || h == "1F2430") token = "TextSecondaryBrush";
                else if (h == "1B6FB8" || h == "4F6BED") token = "PrimaryBrush";
                else if (h == "C0392B" || h == "EF4444") token = "DangerBrush";
                else if (h == "2E7D32" || h == "10B981") token = "SuccessBrush";
            }

            if (token != null)
            {
                var b = TryFindResource(token) as System.Windows.Media.Brush;
                if (b != null) return b;
            }

            try
            {
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                    .ConvertFromString(colorHex);
                return new System.Windows.Media.SolidColorBrush(c);
            }
            catch { }

            return (TryFindResource("TextPrimaryBrush") as System.Windows.Media.Brush)
                   ?? System.Windows.Media.Brushes.Black;
        }

        /// <summary>
        /// 按文案与颜色推断该用哪个状态图标。语义优先于颜色：
        /// 「正在/等待/…」→ 时钟（中性进行中）；「成功/已确认」→ 绿对勾；
        /// 其余带错误色的 → 红叉；都不匹配才退回中性信息圈。
        /// </summary>
        private static System.Windows.Media.Geometry PickStatusIcon(
            string text, string colorHex, out System.Windows.Media.Brush brush)
        {
            text = text ?? "";
            bool bad = colorHex != null &&
                       (colorHex.IndexOf("C0392B", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        colorHex.IndexOf("EF4444", StringComparison.OrdinalIgnoreCase) >= 0);
            bool good = colorHex != null &&
                        (colorHex.IndexOf("2E7D32", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         colorHex.IndexOf("10B981", StringComparison.OrdinalIgnoreCase) >= 0);

            bool pending = text.Contains("正在") || text.Contains("等待") ||
                           text.Contains("请用") || text.Contains("获取二维码");

            string key;
            if (good || text.Contains("成功")) { key = "IconCheckCircle"; }
            else if (bad || text.Contains("失败") || text.Contains("失效") || text.Contains("超时"))
            { key = "IconXCircle"; }
            else if (pending) { key = "IconClock"; }
            else if (text.Contains("已扫码") || text.Contains("已确认")) { key = "IconCheck"; }
            else { key = "IconInfo"; }

            brush = (System.Windows.Media.Brush)System.Windows.Application.Current
                .FindResource(good ? "SuccessBrush" : (bad ? "DangerBrush" : "TextSecondaryBrush"));
            return (System.Windows.Media.Geometry)System.Windows.Application.Current.FindResource(key);
        }

        /// <summary>把二维码 URL 渲染成图像并显示在登录页。</summary>
        private void ShowQrImage(string content)
        {
            try
            {
                // 位图分辨率参考值。⚠️ 不要求与屏幕上的实际显示尺寸完全相等 ——
                // XAML 里是 Stretch=Uniform，会自动缩放；这里只要给一个「够清晰、
                // 又不会被 NearestNeighbor 放大成糊块」的整数模块尺寸就行。
                // （2026-10-17：二维码框已改成自适应宽度，实测显示区约 262×264。）
                int side = 264;
                var probe = QrEncoder.Encode(content);
                int modules = probe.GetLength(0) + 8;   // 含静默区
                int scale = Math.Max(1, side / modules);

                int w, h, stride;
                byte[] bgr = QrEncoder.EncodeToBgr(content, scale, 4, out w, out h, out stride);
                var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                var data = bmp.LockBits(
                    new System.Drawing.Rectangle(0, 0, w, h),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                try
                {
                    for (int y = 0; y < h; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(
                            bgr, y * stride, data.Scan0 + y * data.Stride, w * 3);
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }

                // Bitmap -> BitmapSource
                var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                var decoder = System.Windows.Media.Imaging.BitmapFrame.Create(
                    ms, System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                QrImage.Source = decoder;
                QrImageBox.Visibility = Visibility.Visible;
                if (QrPlaceholderBox != null)
                    QrPlaceholderBox.Visibility = Visibility.Collapsed;
                // 二维码拿到手，「刷新」才有意义（重取一张）
                QrRefreshBtn.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                QrStatusText.Text = "二维码绘制失败: " + ex.Message;
                QrImageBox.Visibility = Visibility.Collapsed;
                if (QrPlaceholderBox != null)
                    QrPlaceholderBox.Visibility = Visibility.Visible;
            }
        }

        private void HideQrImage()
        {
            QrImage.Source = null;
            QrImageBox.Visibility = Visibility.Collapsed;
            // 收起二维码后把占位框放回来，保证左卡高度不跳。
            if (QrPlaceholderBox != null)
                QrPlaceholderBox.Visibility = Visibility.Visible;
            // 没有二维码时「刷新」不露面（回到"未获取"状态）。
            QrRefreshBtn.Visibility = Visibility.Collapsed;
        }

        private async void OnImportCookie(object sender, RoutedEventArgs e)
        {
            var cookie = CookieImportBox.Text.Trim();
            if (string.IsNullOrEmpty(cookie))
            {
                Log("请先粘贴 Cookie");
                return;
            }
            // 导入要联网校验（拉账号信息）→ 按钮就地变成"登录中…"
            long t0 = BusyClock();
            if (!BeginBusy(ImportCookieBtn, "登录中")) return;
            try
            {
                await ApplyLoginAsync(cookie);
            }
            finally
            {
                await EndBusyAsync(ImportCookieBtn, t0);
            }
        }

        /// <summary>
        /// 从**文件**导入 Cookie 并登录。
        /// </summary>
        /// <remarks>
        /// 用户反馈「为什么没有导入 cookie 文件进行登录的功能」——
        /// 原来只能把 Cookie 文本粘进输入框，而浏览器扩展导出的是**文件**。
        /// 支持 .json（Cookie-Editor / EditThisCookie 的导出格式）与 .txt（原始 Cookie 串），
        /// 解析交给 <see cref="GeZi.Core.Support.CookieText"/>。
        /// </remarks>
        private async void OnImportCookieFile(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 Cookie 文件",
                Filter = "Cookie 文件|*.txt;*.json|文本文件 (*.txt)|*.txt|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(this) != true) return;

            string raw;
            try
            {
                raw = File.ReadAllText(dlg.FileName);
            }
            catch (Exception ex)
            {
                Log("读取 Cookie 文件失败：" + ex.Message);
                return;
            }

            string cookie = CookieText.Parse(raw);
            if (string.IsNullOrEmpty(cookie))
            {
                Log("未能从文件中解析出 Cookie：" + dlg.FileName);
                AppDialog.Show(this,
                    "没能从这个文件里解析出 Cookie。\n\n"
                    + "支持两种内容：\n"
                    + "  · 浏览器扩展（如 Cookie-Editor）导出的 .json\n"
                    + "  · 直接复制的 Cookie 文本（.txt，形如 a=1; b=2）",
                    "解析失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 回填到输入框，让用户看得见到底导入了什么
            CookieImportBox.Text = cookie;
            int n = cookie.Split(';').Length;
            Log(string.Format("已从文件导入 Cookie（{0}），共 {1} 项。", dlg.FileName, n));

            long t0 = BusyClock();
            if (!BeginBusy(ImportCookieFileBtn, "登录中")) return;
            try
            {
                await ApplyLoginAsync(cookie);
            }
            finally
            {
                await EndBusyAsync(ImportCookieFileBtn, t0);
            }
        }

        /// <summary>
        /// 导出当前登录 Cookie 到桌面文件。
        /// ⚠️ 用户明确要求：**不要复制到剪贴板**（原话「无论是导出直链还是导出
        /// cookie都不要复制到粘贴板上」）—— 所以这里只写文件。
        /// 写的是**原始 Cookie 串**（`k=v; k=v`），可直接粘回「导入 Cookie」框。
        /// </summary>
        private void OnExportCookie(object sender, RoutedEventArgs e)
        {
            if (_client == null)
            {
                Log("尚未登录");
                return;
            }
            try
            {
                string outFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "GeZi-Cookie-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(outFile, _client.CookieStr, new UTF8Encoding(false));

                Log("已导出 Cookie：" + outFile);
                AppDialog.Show(this,
                    "已把当前登录 Cookie 导出到：\n\n" + outFile +
                    "\n\n注意：Cookie 等同于账号登录凭证，请勿分享给他人。",
                    "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log("导出 Cookie 失败：" + ex.Message);
                AppDialog.Show(this, "导出 Cookie 失败：\n\n" + ex.Message,
                    "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 备选登录：拉起默认浏览器，在网页里扫码登录夸克网盘，
        /// 再把浏览器里的 Cookie 拿回来粘贴导入。
        /// 应用内二维码登录走的是 OpenSSL 桥（pan.quark.cn 那个
        /// 必须过 WAF 的请求），个别网络/系统环境下可能失败；浏览器登录是最通用的兜底。
        /// 夸克网页版本身支持扫码登录，用户登录后从浏览器拿 Cookie 粘回来即可。
        /// </summary>
        private void OnAltLogin(object sender, RoutedEventArgs e)
        {
            try
            {
                // UseShellExecute + URL = 交给 Windows 用默认浏览器打开
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://pan.quark.cn/",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Log("打开浏览器失败：" + ex.Message);
                AppDialog.Show(this,
                    "没能打开默认浏览器。\n\n请手动访问 pan.quark.cn ，登录后在浏览器里"
                    + "复制 Cookie，再粘回上方的「导入 Cookie」输入框。",
                    "备选登录", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Log("已打开浏览器（pan.quark.cn）进行备选登录。");
            AppDialog.Show(this,
                "已在默认浏览器里打开夸克网盘。\n\n"
                + "接下来：\n"
                + "1. 在浏览器里登录（网页版支持扫码）；\n"
                + "2. 按 F12 打开开发者工具 →「网络 / Network」→ 点任意一个请求，\n"
                + "   在请求头里找到 Cookie，整条复制；\n"
                + "   （也可以先用浏览器扩展导出 Cookie 文件，回来点「从文件导入」）；\n"
                + "3. 回到这里粘贴到上方输入框，点「导入并登录」。\n\n"
                + "注意：Cookie 等同于账号登录凭证，请勿泄露给他人。",
                "备选登录", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 退出登录：清掉内存里的客户端 + 配置里保存的 Cookie/昵称/头像。
        /// </summary>
        /// <remarks>
        /// 登录页那个「登出」按钮已按用户要求删掉，但这个能力没丢 ——
        /// 它被挪进了「切换账号」窗口（那里才是账号管理的地方），
        /// 并且**删除当前账号时也会走这里**（否则只删档案、Cookie 还在配置里，
        /// 表现为"删了但账号还存在"，正是用户反馈的旧版问题）。
        /// </remarks>
        private void DoLogout(string reason)
        {
            try { _client?.StopKeepAlive(); } catch { }
            _client?.Dispose();
            _client = null;
            // 登出同样是"换账号"：直链 / 目录缓存 / 导航快照都要作废，
            // 否则下次登录别的账号会看到上一个账号的网盘内容（见 InvalidateAccountCaches）。
            InvalidateAccountCaches();
            _settings.Cookie = "";
            _settings.Who = "";
            _settings.AvatarUrl = "";
            _settings.CurrentAccountKey = "";
            SettingsStore.Save(_settings);
            UpdateAccountCard(null, 0, 0);
            // 登出后**不再隐藏**「切换账号」——
            // 它现在同时是"未登录时的登录入口"（有已保存账号就叫「选择账号」）。
            // 可见性与文案统一由 SyncLoginPageState 控制（UpdateAccountCard 内部会调到）。
            // 若正停在「我的网盘」模式，把文件列表清空 —— 已登出，那些内容不再属于任何人。
            if (!ShareMode)
                ResetNav();
            Log(string.IsNullOrEmpty(reason) ? "已登出" : reason);
        }

        private async Task ApplyLoginAsync(string cookie)
        {
            try
            {
                var client = new QuarkClient(cookie);
                var (valid, nick) = await client.ValidateAsync();

                // ⚠️ account/info 挂在 pan.quark.cn，**.NET 根本连不上**（WAF 按 TLS
                // ClientHello 的曲线组扩展大小做指纹过滤，SChannel 必然被 RST），
                // 所以 ValidateAsync 每次都走兜底 → 昵称是"已登录用户"、头像永远为空
                // —— 这正是用户反馈「都没有名字和头像」的根因。
                // 借 Python 桥（OpenSSL 3.5.7 的 ClientHello 能过）补一次真实账号信息。
                // 昵称/头像走**与手动刷新同一个方法**（这样两处的行为永远一致）
                string realAvatar = null;
                if (valid)
                {
                    var resolved = await ResolveAccountInfoAsync(client, cookie);
                    valid = resolved.valid;
                    if (!string.IsNullOrEmpty(resolved.nick)) nick = resolved.nick;
                    realAvatar = resolved.avatar;
                }

                if (valid)
                {
                    try { _client?.StopKeepAlive(); } catch { }
                    _client?.Dispose();
                    _client = client;
                    // 保活心跳连续失败 → 登录很可能已失效。事件在后台线程上触发，
                    // 必须切回 UI 线程再动界面。
                    _client.LoginExpired += () =>
                    {
                        try
                        {
                            Dispatcher.BeginInvoke(new Action(
                                () => ShowLoginExpired("保活心跳连续失败")));
                        }
                        catch { }
                    };
                    _client.StartKeepAlive();
                    _loginExpired = false;   // 新登录成功 → 清掉"已过期"标记
                    // 换账号后旧账号的**直链 / 目录缓存 / 两个模式的导航快照**全部作废。
                    // （原来只清了直链 → 切回「我的网盘」会显示旧账号的内容，见 InvalidateAccountCaches）
                    InvalidateAccountCaches();
                    _settings.Cookie = client.CookieStr;
                    _settings.Who = nick;
                    // 头像：探测到的就存下来（拿不到存空串 → 界面回退"昵称首字"）
                    _settings.AvatarUrl = realAvatar ?? client.AvatarUrl ?? "";
                    SettingsStore.Save(_settings);

                    // 登录成功后存一份账号档案，之后可在「切换账号」里一键切回，不用再扫码。
                    // 标识优先用夸克返回的稳定 uid，取不到时回退昵称（同名不同账号才会冲突）。
                    string acctKey = !string.IsNullOrEmpty(client.AccountSig) ? client.AccountSig : nick;
                    if (!string.IsNullOrEmpty(acctKey))
                        AccountStore.Save(acctKey, nick, client.CookieStr);
                    // 记住"当前是哪个账号" —— 账号列表用它判断哪一行的「切换」该置灰
                    _settings.CurrentAccountKey = acctKey ?? "";

                    UpdateAccountCard(nick, 0, 0);
                    // 「切换账号」的可见性/文案统一由 SyncLoginPageState 控制
                    // （登录成功 → 显示「切换账号」），这里不再单独设。
                    Log("登录成功: " + nick);

                    // 若此刻正停在「我的网盘」模式，缓存/快照刚被清空，
                    // 界面还挂着旧账号的文件列表 → 立刻重置并加载**新账号**的根目录，
                    // 否则用户会在切页时看到旧内容（正是本次要修的 bug）。
                    if (!ShareMode)
                    {
                        ResetNav();
                        await LoadDirAsync("0");
                    }

                    await RefreshCapacityAsync();
                }
                else
                {
                    client.Dispose();
                    UpdateAccountCard(null, 0, 0);
                    AccountHintText.Text = "Cookie 已失效，请重新登录";
                    Log("登录校验失败: " + nick);
                }
            }
            catch (Exception ex)
            {
                Log("登录出错: " + ex.Message);
            }
        }

        /// <summary>
        /// 账号卡上那个小刷新按钮：**重新校验登录态 + 拉最新昵称/头像/容量**。
        /// 与定时刷容量的区别：定时那条只刷容量（省请求），这个按钮是用户主动点的，
        /// 所以连登录态一起重新校验 —— 万一登录其实已经失效，点一下就能看出来。
        /// </summary>
        /// <summary>
        /// `QuarkClient.ValidateAsync` 在拿不到真名时返回的**兜底昵称**。
        /// ⚠️ 这个值**绝不能写进配置** —— 否则真实昵称就被它覆盖掉了。
        /// </summary>
        private const string FallbackNick = "已登录用户";

        /// <summary>
        /// 取**真实**的账号昵称与头像（登录时、手动刷新时共用这一份）。
        /// `ValidateAsync` 里的昵称**永远**是兜底的"已登录用户" ——
        /// account/info 挂在 `pan.quark.cn`，.NET 的 SChannel 必然被 WAF RST（见上面长注释）。
        /// 真名和头像只能借 Python 桥（OpenSSL 3.5.7 能过）。
        /// ⚠️ **踩过的坑**：手动刷新那一版只调了 `ValidateAsync`、没走 Python 桥，
        /// 于是把好名字覆盖成了"已登录用户"（用户实测：「点了右上角刷新之后我的名字没了」）。
        /// 所以刷新与登录**必须走同一个方法**。
        /// </summary>
        private async Task<(bool valid, string nick, string avatar)> ResolveAccountInfoAsync(
            QuarkClient client, string cookie)
        {
            var (valid, nick) = await client.ValidateAsync();
            if (!valid) return (false, null, null);

            // 昵称是兜底值、或还没有头像 → 借 Python 桥补真实信息
            if (nick == FallbackNick || string.IsNullOrEmpty(client.AvatarUrl))
            {
                try
                {
                    var info = await Task.Run(() => PanAccountBridge.FetchAccountInfo(cookie));
                    if (info.HasValue && info.Value.Ok)
                    {
                        if (!string.IsNullOrEmpty(info.Value.Nickname))
                            nick = info.Value.Nickname;
                        if (!string.IsNullOrEmpty(info.Value.Avatar))
                            client.SetAvatarUrl(info.Value.Avatar);
                    }
                    else if (info.HasValue && !string.IsNullOrEmpty(info.Value.Error))
                    {
                        LogDetail("取账号昵称/头像失败：" + info.Value.Error);
                    }
                }
                catch (Exception ex)
                {
                    // 拿不到就用兜底值，绝不让它影响登录
                    LogDetail("取账号昵称/头像出错：" + ex.Message);
                }
            }
            return (true, nick, client.AvatarUrl);
        }

        private async void OnRefreshAccountClick(object sender, RoutedEventArgs e)
        {
            if (_client == null)
            {
                Log("尚未登录，先扫码登录吧");
                try { Tabs.SelectedIndex = 3; } catch { }
                return;
            }
            long t0 = BusyClock();
            if (!BeginBusy(AccountRefreshBtn, "")) return;   // 忙时按钮显示转圈
            try
            {
                var (valid, nick, avatar) = await ResolveAccountInfoAsync(_client, _settings.Cookie);
                if (!valid)
                {
                    Log("账号状态刷新：登录已失效");
                    ShowLoginExpired("刷新时发现登录已失效");
                    return;
                }

                // 昵称可能变了（改过夸克昵称）→ 存起来并刷新头像。
                // ⚠️ **兜底昵称"已登录用户"绝不写进去** —— 写了就把真名覆盖没了
                //    （这正是上一版刷新把名字刷丢的原因）。
                bool nickOk = !string.IsNullOrWhiteSpace(nick) && nick != FallbackNick;
                if (nickOk && nick != _settings.Who)
                    _settings.Who = nick;
                if (!string.IsNullOrWhiteSpace(avatar) && avatar != _settings.AvatarUrl)
                    _settings.AvatarUrl = avatar;
                try { SettingsStore.Save(_settings); } catch { }

                await RefreshCapacityAsync();
                Log("已刷新账号状态" + (string.IsNullOrWhiteSpace(_settings.Who)
                    ? "" : "：" + _settings.Who));
            }
            catch (Exception ex)
            {
                Log("刷新账号状态失败：" + ex.Message);
            }
            finally
            {
                await EndBusyAsync(AccountRefreshBtn, t0);
            }
        }

        /// <summary>
        /// 登录已失效的统一提示（心跳发现 / 手动刷新发现 都走这里）。
        /// 三件事一起做，缺一不可：
        ///   ① 账号卡变成"登录已过期"的红色状态（否则界面还显示着已登录，用户不知道）；
        ///   ② 弹一条托盘通知（用户可能正把窗口收在托盘里）；
        ///   ③ 写一行日志。
        /// 重新扫码登录成功后，`ApplyLoginAsync` 会把卡片恢复正常。
        /// </summary>
        private void ShowLoginExpired(string reason)
        {
            _loginExpired = true;
            try
            {
                AccountText.Text = "登录已过期";
                AccountText.ToolTip = "登录已过期，请到「登录」页重新扫码";
                AccountHintText.Text = "点这里重新扫码登录";
                AvatarText.Text = "!";
                AvatarText.Visibility = Visibility.Visible;
                AvatarImage.Visibility = Visibility.Collapsed;
                AvatarImage.Source = null;
                AvatarBox.Background = (Brush)FindResource("DangerLightBrush");
                CapacityBar.Value = 0;
                CapacityBar.Foreground = (Brush)FindResource("DangerBrush");
                CapacityBar.ToolTip = null;
                CapacityText.Text = "登录已过期";
                SyncLoginPageState(null, 0, 0);
            }
            catch { }

            Log("登录已过期：" + (reason ?? "") + " 请到「登录」页重新扫码。");
            try
            {
                _tray?.ShowBalloon("登录已过期",
                    "夸克登录状态已失效，请打开主窗口到「登录」页重新扫码。");
            }
            catch { }
        }

        /// <summary>
        /// 定时刷容量（默认 5 分钟，与保活心跳同频）。
        /// 原来容量**只在启动那一次拉取**，所以下载完一批文件后卡片上的
        /// "已用 / 总量"还是启动时的数字（用户问「账号状态多久刷新一次」时发现的）。
        /// 这里补上定时刷新；隐藏到托盘时也照刷（一个很轻的接口）。
        /// </summary>
        private async void OnCapacityTick()
        {
            if (_client == null || _loginExpired) return;
            try { await RefreshCapacityAsync(); } catch { }
        }

        private async Task RefreshCapacityAsync()
        {
            if (_client == null)
                return;
            try
            {
                var (total, used) = await _client.GetCapacityAsync();
                if (total > 0)
                {
                    // 容量条已经显示了用量，这里只更新卡片（昵称保持当前值）。
                    UpdateAccountCard(_settings.Who, used, total);
                }
                else
                {
                    UpdateAccountCard(_settings.Who, 0, 0);
                }
            }
            catch { }
        }

        // ---------------- 窗口关闭 / 托盘驻留 ----------------
        // 2026-10-02 曾整套删掉"关闭到托盘"：那时点 × 就隐藏，但**没有可发现的
        // 退出入口**，用户以为关了其实还在跑，再点 exe 又因为找不到可见窗口而"什么都不弹"。
        // 用户要求加回后台功能 + 小窗，并明确了行为：
        //   「第一次时会询问用户，然后可以选择最小化托盘或者是直接关闭程序，
        //     并且在这个弹窗的右下角可以勾选此后不再提示，在设置中可以修改」。
        // 所以现在的语义是：`AppSettings.OnCloseButton` 决定 ——
        //   Ask（默认，只问一次）→ 弹询问框，勾了「不再提示」就把选择写进设置；
        //   MinimizeToTray → 隐藏到托盘（下载继续）；
        //   Exit → 真退出。
        // ⚠️ 无论哪种模式，**托盘右键菜单里必须有「退出」**，这是唯一不会被误解的出口。

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            if (_exiting)
                return;   // 已经在走退出流程，放行

            var action = _settings.OnCloseButton;
            if (action == CloseAction.Ask)
            {
                bool dontAsk;
                var r = AppDialog.ShowWithDontAsk(this,
                    "要让鸽子下载继续在后台下载吗？\n\n" +
                    "· 最小化到托盘：窗口收进通知区，下载继续跑；\n" +
                    "   想彻底退出，右键托盘图标选「退出」。\n" +
                    "· 直接退出程序：立即结束进程（已下部分会保留，下次可续传）。",
                    "关闭窗口",
                    "最小化到托盘", "直接退出程序",
                    MessageBoxImage.Question, out dontAsk);

                action = (r == MessageBoxResult.OK)
                    ? CloseAction.MinimizeToTray : CloseAction.Exit;

                if (dontAsk)
                {
                    // 勾了「此后不再提示」→ 记住这次的选择（设置页里还能改回来）。
                    // ⚠️ 只改这一个字段 + 落盘，**不要**走 ApplySettings()：
                    //    那会重读所有控件、重建调度器、还打一行"设置已生效"，纯属副作用。
                    _settings.OnCloseButton = action;
                    try { SettingsStore.Save(_settings); } catch { }
                    try { LoadSettingsToUi(); } catch { }
                    Log("已记住「关闭按钮」行为：" +
                        (action == CloseAction.MinimizeToTray ? "最小化到托盘" : "直接退出程序") +
                        "（可在设置页修改）");
                }
            }

            if (action == CloseAction.MinimizeToTray)
            {
                e.Cancel = true;      // 不真关，只隐藏
                HideToTray();
                return;
            }

            // 直接退出：若还有任务在跑，先问一句（避免误关）
            if (_batchRunning)
            {
                var r = AppDialog.Show(this,
                    "还有下载任务正在运行，确定要退出吗？\n（退出会中断本次下载，已下部分会保留，下次可续传。）",
                    "退出确认", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (r != MessageBoxResult.OK)
                {
                    e.Cancel = true;
                    return;
                }
            }
            _exiting = true;
            // 不放行也不行：这里只做清理，真正退出统一走 ExitApp()，
            // 否则"最后关掉的那个窗口"会决定进程去留（App 已设 OnExplicitShutdown）。
            e.Cancel = true;
            ExitApp();
        }

        /// <summary>隐藏到托盘（窗口 Hide，进程与下载继续）。</summary>
        private void HideToTray()
        {
            try
            {
                Hide();
                // 隐藏时把日志刷新降频：没人在看，200ms 一次的布局重排纯属浪费。
                // 队列仍在攒（不丢日志），恢复显示时会一次性刷出来。
                if (_logFlushTimer != null)
                    _logFlushTimer.Interval = TimeSpan.FromMilliseconds(LogFlushMsHidden);
                if (_tray != null && !_trayHintShown)
                {
                    _trayHintShown = true;
                    _tray.ShowBalloon("鸽子下载仍在后台运行",
                        "下载会继续。双击托盘图标可重新打开窗口，右键可选「退出」。");
                }
                Log("已最小化到托盘，下载继续。右键托盘图标可选「退出」。");
            }
            catch { }
        }

        /// <summary>把主窗从托盘/最小化状态拉起来。也被"再点一次 exe"的跨进程消息调用。</summary>
        private void RestoreFromTray()
        {
            try
            {
                if (_logFlushTimer != null)
                    _logFlushTimer.Interval = TimeSpan.FromMilliseconds(LogFlushMs);
                if (!IsVisible) Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                // 有些情况下 Activate() 仍抢不到前台（前台锁定），再用 Win32 兜一刀
                var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (h != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(h, NativeMethods.SW_RESTORE);
                    NativeMethods.SetForegroundWindow(h);
                }
            }
            catch { }
        }

        /// <summary>真正退出程序：清托盘 → 关小窗 → 关主窗 → Shutdown。</summary>
        private void ExitApp()
        {
            _exiting = true;
            try { _tray?.Dispose(); _tray = null; } catch { }
            try { CloseMiniWindow(); } catch { }
            try { Close(); } catch { }
            try { Application.Current.Shutdown(); } catch { }
        }

        /// <summary>创建托盘图标（在 Loaded 里调，此时窗口句柄已存在）。</summary>
        private void InitTray()
        {
            try
            {
                // 提示文案要把"单击/双击"讲清楚 —— 这两种行为不写出来没人猜得到。
                _tray = new TrayIcon("鸽子下载 · 单击开关小窗，双击打开主窗口，右键更多");
                // 用户指定：**单击 = 小窗，双击 = 主窗口**。
                // （单击动作在 TrayIcon 里延迟一个双击间隔执行，否则双击会先触发单击。）
                _tray.SingleClickRequested += ToggleMiniWindow;
                _tray.DoubleClickRequested += RestoreFromTray;
                _tray.ExitRequested += ExitApp;
                _tray.ToggleMiniRequested += ToggleMiniWindow;
                _tray.PauseAllRequested += () => OnPauseAllOrResume(null, null);
                _tray.SetMiniVisible(false);
                _tray.SetAllPaused(false);
                // 主题切换后托盘图标要重画（图标是一次性渲染的位图，不会自动跟资源字典变）。
                // ⚠️ ThemeChanged 在 ThemeManager 里是**同步**触发的（用户在设置页点单选钮 = UI 线程），
                //    但**跟随系统**模式下是 SystemEvents 线程触发的 → ThemeManager 已经切回 UI 线程，
                //    这里再保一道 Dispatcher，别赌调用方。
                ThemeManager.ThemeChanged += OnThemeChangedRedrawTray;
            }
            catch (Exception ex)
            {
                LogDetail("托盘图标创建失败（不影响下载）：" + ex.Message);
            }
        }

        /// <summary>显示 / 隐藏小窗（托盘菜单用）。</summary>
        private void ToggleMiniWindow()
        {
            if (_mini != null && _mini.IsVisible) CloseMiniWindow();
            else ShowMiniWindow();
        }

        /// <summary>
        /// 主题切换后重画托盘图标（2026-10-04 新增）。
        /// 订阅在 <see cref="InitTray"/> 里（此时 _tray 才存在），退出时在
        /// <see cref="ExitApp"/> 前面的 Dispose 路径里已经 Dispatcher 掉了 —— 但为了
        /// 不留悬挂引用，OnExit 之前若窗口已关，事件仍持有 this；这里用弱式保护：
        /// 回调里先看 `_exiting`，退出中直接忽略。
        /// </summary>
        private void OnThemeChangedRedrawTray(bool isDark)
        {
            try
            {
                if (_exiting) return;
                // 保险：跟随系统模式下 ThemeManager 已切回 UI 线程，这里再兜一层。
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(() => OnThemeChangedRedrawTray(isDark)));
                    return;
                }
                _tray?.RefreshIcon();
            }
            catch { }
        }

        private void ShowMiniWindow()
        {
            try
            {
                if (_mini == null)
                {
                    _mini = new MiniWindow(_tasks);
                    _mini.OpenMainRequested += RestoreFromTray;
                    _mini.HideRequested += CloseMiniWindow;
                    _mini.PositionChanged += (l, t) =>
                    {
                        // 拖动过程中会疯狂触发，这里只记值；真正落盘在关闭时做一次。
                        _miniLeft = l; _miniTop = t;
                    };
                    _mini.Closed += (s, e) =>
                    {
                        _settings.MiniLeft = _miniLeft;
                        _settings.MiniTop = _miniTop;
                        _settings.MiniPosSet = true;
                        ApplySettings();
                        _mini = null;
                        _tray?.SetMiniVisible(false);
                    };
                }

                if (_settings.MiniPosSet)
                {
                    _mini.WindowStartupLocation = WindowStartupLocation.Manual;
                    _mini.Left = _settings.MiniLeft;
                    _mini.Top = _settings.MiniTop;
                }
                else
                {
                    // 首次打开：贴到工作区右下角，不挡中间
                    var wa = SystemParameters.WorkArea;
                    _mini.WindowStartupLocation = WindowStartupLocation.Manual;
                    _mini.Left = wa.Right - 360;
                    _mini.Top = wa.Bottom - 260;
                }
                _miniLeft = _mini.Left;
                _miniTop = _mini.Top;

                _mini.Show();
                _mini.Activate();
                _tray?.SetMiniVisible(true);
            }
            catch (Exception ex)
            {
                Log("小窗打开失败：" + ex.Message);
            }
        }

        private void CloseMiniWindow()
        {
            try
            {
                if (_mini != null)
                {
                    _mini.Shutdown();
                    _mini.Close();
                }
            }
            catch { }
            _mini = null;
            _tray?.SetMiniVisible(false);
        }

        // ---------------- 自绘标题栏 ----------------

        // ============================================================
        // 侧栏导航 ↔ 内容区 双向同步
        // ============================================================
        // 上一轮把侧栏放在 TabControl 的 ControlTemplate 里，
        // 结果模板内的 x:Name 不是类字段，账号卡根本访问不到（CS0103）。
        // 这一轮把侧栏搬成 MainWindow 里的独立 ListBox，于是需要两处同步：
        //   · 用户点侧栏 ListBox  → 要把 Tabs.SelectedIndex 跟过去
        //   · 代码里写 Tabs.SelectedIndex（全项目共 6 处：登录跳转、下载后跳任务页…）
        //                         → 要把 NavList.SelectedIndex 跟过来
        // 判等是防死循环的关键：不加判等，两边会互相触发 SelectionChanged
        // 形成无限递归（WPF 里表现为栈溢出崩掉，或界面卡死）。
        // 用 SelectionChanged 事件而不是 SelectedIndex 依赖属性回调：
        // 事件在属性值真的变化时才触发，语义更贴合，也不引入额外的绑定开销。

        private bool _syncingNav;

        private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingNav)
                return;
            try
            {
                int i = NavList.SelectedIndex;
                if (i < 0 || i == Tabs.SelectedIndex)
                    return;
                _syncingNav = true;
                Tabs.SelectedIndex = i;
            }
            catch { }
            finally { _syncingNav = false; }
        }

        private void OnTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingNav)
                return;
            try
            {
                int i = Tabs.SelectedIndex;
                if (i < 0 || i == NavList.SelectedIndex)
                    return;
                _syncingNav = true;
                NavList.SelectedIndex = i;
            }
            catch { }
            finally { _syncingNav = false; }
        }

        /// <summary>点账号卡直接去「登录」页 —— 侧栏底部那块地方小，放不了按钮。</summary>
        /// <summary>侧栏品牌区那个「?」按钮：打开「关于」窗口（软件介绍 + 赞赏码）。</summary>
        private void OnAboutClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var w = new AboutWindow { Owner = this };
                w.ShowDialog();
            }
            catch (Exception ex)
            {
                LogDetail("打开「关于」窗口失败：" + ex.Message);
            }
        }

        private void OnAccountCardClick(object sender, MouseButtonEventArgs e)
        {
            // ⚠️ 卡片上还嵌了个「刷新」小按钮。Button 一般会自己吃掉鼠标事件，
            //    但这里再挡一道：点到按钮时不要顺带跳到登录页。
            var d = e.OriginalSource as DependencyObject;
            while (d != null)
            {
                if (d is Button) return;
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            try { Tabs.SelectedIndex = 3; } catch { }
        }

        // ============================================================
        // 账号卡刷新（侧栏左下角）
        // ============================================================

        /// <summary>
        /// 异步把账号头像图加载到 <paramref name="img"/>；拿不到就保持"昵称首字"回退显示。
        /// </summary>
        /// <remarks>
        /// 用户要求「我登录账号后希望能够显示账号的名称还有账号图片」。
        /// 名称本来就在显示（侧栏账号卡 + 登录页都会显示昵称）；头像原来只有
        /// "昵称首字 + 散列底色"的占位，这里补上真实头像。
        /// ⚠️ 头像 URL 是**探测**出来的（夸克没公开字段名，Python 版也从没用过），
        ///    可能为空、也可能过期 —— 所以**任何失败都必须静默回退**，
        ///    绝不能让"拿不到头像"这件事冒出错误提示或影响登录。
        /// </remarks>
        private async Task LoadAvatarAsync(System.Windows.Controls.Image img,
            System.Windows.Controls.TextBlock fallback, string url)
        {
            if (img == null) return;

            // 同一个 URL 已经加载过就不重复下载。
            // ⚠️ 启动时 UpdateAccountCard 会被调用好几次（自动登录 → 刷容量 → 同步登录页），
            // 不拦的话同一张头像会被下载 4 次（实测日志里出现 4 行「账号头像已加载」）。
            if (img.Source != null &&
                string.Equals(img.Tag as string, url, StringComparison.Ordinal))
                return;

            if (string.IsNullOrEmpty(url))
            {
                img.Source = null;
                img.Tag = null;
                img.Visibility = Visibility.Collapsed;
                if (fallback != null) fallback.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                byte[] bytes = await Task.Run(() =>
                {
                    using (var wc = new System.Net.WebClient())
                    {
                        wc.Headers["User-Agent"] = QuarkConstants.DlUa;
                        return wc.DownloadData(url);
                    }
                });

                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                // OnLoad：立刻把流读进内存，否则流被回收后图会变成空白
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();   // 冻结后可以跨线程/被多处共享

                img.Source = bmp;
                img.Tag = url;      // 记住已加载的 URL，避免重复下载
                img.Visibility = Visibility.Visible;
                if (fallback != null) fallback.Visibility = Visibility.Collapsed;
                // 成功**不写日志** —— 用户要求「这两个就不要在日志中显示了，
                // 他们俩失败时才显示失败」。下面 catch 里的失败日志保留。
            }
            catch (Exception ex)
            {
                // 拿不到就回退首字头像 —— 不打扰用户，但记一行日志便于排查
                LogDetail("账号头像加载失败，已回退为昵称首字：" + ex.GetType().Name + ": " + ex.Message);
                img.Source = null;
                img.Tag = null;
                img.Visibility = Visibility.Collapsed;
                if (fallback != null) fallback.Visibility = Visibility.Visible;
            }
        }

        // 原来"账号/容量"散在 5 处直接给
        // AccountText.Text / CapacityText.Text 赋值，措辞还各不相同
        // （"已登录: 已登录用户" / "未登录（Cookie 无效）"）。字面量拼在
        // 调用点，想统一改个措辞就要翻五处，极易漏改。现在全部走这一个入口。
        // nick 为 null / 空 = 未登录态。

        /// <summary>
        /// 刷新左下角账号卡。
        /// 卡片高度必须**两种状态一致**，
        /// 否则登录/登出时整张卡会明显跳高跳矮（用户截图反馈过）。
        /// 现在未登录也保留容量条的位置，只是画成 0 值的灰色空槽。
        /// </summary>
        /// <param name="nick">昵称；null 或空表示未登录</param>
        /// <param name="used">已用容量（字节）</param>
        /// <param name="total">总容量（字节）；≤0 表示未知，显示空槽</param>
        private void UpdateAccountCard(string nick, long used, long total)
        {
            try
            {
                bool logged = !string.IsNullOrWhiteSpace(nick);

                if (!logged)
                {
                    AvatarText.Text = "?";
                    AvatarBox.Background = (Brush)FindResource("BorderBrushStrong");
                    AvatarImage.Source = null;
                    AvatarImage.Visibility = Visibility.Collapsed;
                    AvatarText.Visibility = Visibility.Visible;
                    AccountText.Text = "未登录";
                    AccountText.ToolTip = null;
                    AccountHintText.Text = "登录后可浏览网盘";
                    // 空槽：值为 0、用最浅的状态色画一条，保持占位不消失
                    CapacityBar.Value = 0;
                    CapacityBar.Foreground = (Brush)FindResource("BorderBrushStrong");
                    CapacityBar.ToolTip = null;
                    CapacityText.Text = "容量未知";

                    SyncLoginPageState(null, 0, 0);
                    return;
                }

                // 头像：取昵称首字符。比画一个通用人形图标更有"这是我的账号"的归属感，
                // 而且不需要额外请求头像图片（夸克接口给的昵称足够）。
                string first = nick.Trim().Substring(0, 1);
                AvatarText.Text = first.ToUpperInvariant();
                AvatarBox.Background = AvatarTintFor(nick);
                // 有真头像就异步换上（拿不到保持首字回退，不打扰用户）
                var ignoredAvatar = LoadAvatarAsync(AvatarImage, AvatarText, _settings.AvatarUrl);

                AccountText.Text = nick;
                AccountText.ToolTip = nick;   // 昵称过长被截断时仍可悬停看全
                AccountHintText.Text = "夸克网盘";

                if (total > 0)
                {
                    double pct = Math.Max(0, Math.Min(100, used * 100.0 / total));
                    CapacityBar.Value = pct;

                    // 状态色编码（绿 → 琥珀 → 红）。
                    //  只看占用率。但纯百分比对两种极端都不合理：
                    //   · 10TB 账号用掉 99.99%（剩 1GB）→ 其实已经快满了，红是对的；
                    //   · 5GB 账号用掉 82%（剩 0.9GB）→ 只剩不到 1G 了却是琥珀，不合理。
                    //  所以补一条**绝对阈值**：剩余不足 1GB 也直接判红。】
                    // 用户问「夸克容量能超过默认值，比如 20G/10.4G，
                    //  这时进度条什么颜色？我希望保持红色」。
                    //  pct 已被上面 Clamp 到 100（所以进度条是**满格**而不是溢出/归零），
                    //  但 free = total - used 会变成负数 → freeTooLow 自然为真 → 红。
                    //  为了不依赖这个"顺带成立"的巧合，这里显式加一条 overQuota 判据：
                    //  ① 语义直白；② 万一以后有人改了上面的 Clamp 也不会翻车；
                    //  ③ 提示语里能把"超额"说清楚，而不是像原来那样显示"剩余 0"（会让人困惑）。
                    long free = total - used;
                    bool overQuota = used > total;
                    bool freeTooLow = free < LowFreeBytes;
                    CapacityBar.Foreground = (Brush)FindResource(
                        (overQuota || pct >= 90 || freeTooLow) ? "CapacityHighBrush"
                        : pct >= 70 ? "CapacityMidBrush"
                        : "CapacityLowBrush");

                    CapacityText.Text = Util.FormatSize(used) + " / " + Util.FormatSize(total);
                    CapacityBar.ToolTip = "已用 " + Util.FormatSize(used)
                                        + " / 共 " + Util.FormatSize(total)
                                        + "（" + pct.ToString("0.#") + "%）"
                                        + (overQuota
                                            ? "\n已超出默认容量 " + Util.FormatSize(used - total)
                                            : "\n剩余 " + Util.FormatSize(free < 0 ? 0 : free)
                                              + (freeTooLow ? "　⚠ 剩余不足 1 GB" : ""));
                }
                else
                {
                    // 已登录但容量接口没返回：同样留空槽，保证高度不变
                    CapacityBar.Value = 0;
                    CapacityBar.Foreground = (Brush)FindResource("BorderBrushStrong");
                    CapacityBar.ToolTip = null;
                    CapacityText.Text = "容量获取中…";
                }

                SyncLoginPageState(nick, used, total);
            }
            catch { }
        }

        /// <summary>
        /// 同步「登录」页里的当前状态卡（与侧栏账号卡同源，避免两处状态不一致）。
        /// 侧栏那块地方小，只放昵称+容量；这里空间大，补上容量明细与操作按钮的可见性。
        /// </summary>
        private void SyncLoginPageState(string nick, long used, long total)
        {
            if (LoginWhoText == null)
                return;
            try
            {
                bool logged = !string.IsNullOrWhiteSpace(nick);
                if (!logged)
                {
                    LoginAvatarText.Text = "?";
                    LoginAvatarBox.Background = (Brush)FindResource("BorderBrushStrong");
                    LoginAvatarImage.Source = null;
                    LoginAvatarImage.Visibility = Visibility.Collapsed;
                    LoginAvatarText.Visibility = Visibility.Visible;
                    LoginWhoText.Text = "未登录";
                    // "我有两个账号，删掉正在登录的那个之后
                    // 就再也没有登录入口了 —— 明明另一个账号还在。"
                    // 根因：未登录时把「切换账号」按钮整个隐藏了，而它同时也是账号选择入口。
                    // 现在未登录也显示，并按"有没有已保存账号"换文案/行为：
                    //   有账号 →「选择账号」→ 打开账号列表一键切回
                    //   没账号 →「登录」    → 直接取二维码并提示扫码
                    int saved = 0;
                    try { saved = AccountStore.LoadAll().Count; } catch { }
                    AccountsBtn.Content = saved > 0 ? "选择账号" : "登录";
                    AccountsBtn.Visibility = Visibility.Visible;
                    LoginSubText.Text = saved > 0
                        ? "本机已保存 " + saved + " 个账号，点右侧「选择账号」直接切回"
                        : "扫码或导入 Cookie 即可登录";
                    return;
                }

                LoginAvatarText.Text = nick.Trim().Substring(0, 1).ToUpperInvariant();
                LoginAvatarBox.Background = AvatarTintFor(nick);
                var ignoredLoginAvatar = LoadAvatarAsync(
                    LoginAvatarImage, LoginAvatarText, _settings.AvatarUrl);
                LoginWhoText.Text = nick;
                LoginSubText.Text = total > 0
                    ? "已用 " + Util.FormatSize(used) + " / 共 " + Util.FormatSize(total)
                    : "已登录（容量获取中…）";
                AccountsBtn.Content = "切换账号";
                AccountsBtn.Visibility = Visibility.Visible;
            }
            catch { }
        }

        /// <summary>按昵称散列挑一个头像底色，同名每次结果一致（不会闪来闪去）。</summary>
        private Brush AvatarTintFor(string nick)        {
            string[] keys =
            {
                "AvatarTint1Brush", "AvatarTint2Brush", "AvatarTint3Brush",
                "AvatarTint4Brush", "AvatarTint5Brush",
            };
            int hash = 0;
            foreach (char c in nick)
                hash = (hash * 31 + c) & 0x7FFFFFFF;
            return (Brush)FindResource(keys[hash % keys.Length]);
        }

        /// <summary>
        /// 同步两个列表的「空状态插画」可见性。
        /// 纯皮肤逻辑：集合没内容（或筛选后全被过滤掉）时显示提示，否则隐藏。
        /// 现在还会参考"是否正在加载"：
        /// 加载中显示"正在加载…"，加载完且为空才显示"还没有内容"。
        /// 不加这个判断的话，加载的几百毫秒里会闪出"还没有内容"，误导用户。
        /// </summary>
        private void UpdateEmptyStates()
        {
            try
            {
                if (FileEmptyState != null)
                {
                    // 用筛选视图的计数：筛选后一条不剩时也应该显示空状态；
                    // 但筛选是本地过滤，跟"正在加载"无关 —— 筛选出 0 条时
                    // 仍应显示空状态，不该显示"正在加载"。
                    // 没有筛选词时 _files.Count 就是可见条数，**不必整表枚举**视图 ——
                    // 几千项的目录里，这个枚举是卡顿的主因之一。
                    int n = string.IsNullOrEmpty(_fileFilter)
                        ? _files.Count
                        : (_filesView != null ? _filesView.Cast<object>().Count() : _files.Count);
                    bool loading = _loadingDir && _files.Count == 0 && string.IsNullOrEmpty(_fileFilter);

                    if (FileLoadingState != null)
                        FileLoadingState.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;

                    FileEmptyState.Visibility = (!loading && n == 0)
                        ? Visibility.Visible : Visibility.Collapsed;

                    // 空状态的文案按模式区分：分享模式该说"粘贴链接"，
                    // 网盘模式该说"这个目录是空的"。一句通用文案两边都不贴切。
                    if (FileEmptyText != null)
                        FileEmptyText.Text = ShareMode ? "还没有内容" : "这个目录是空的";
                }
                if (TaskEmptyState != null)
                    TaskEmptyState.Visibility = _tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        /// <summary>已排队的空状态刷新（把连续多次集合变化合并成一次计算）。</summary>
        private bool _emptyStatePending;

        /// <summary>
        /// 合并版 <see cref="UpdateEmptyStates"/>：置个标记，攒到本轮 UI 空闲时才算一次。
        /// 往列表里铺目录时集合会连着变很多次，而 UpdateEmptyStates
        /// 要数一遍可见条数（有筛选词时是整表枚举）—— 一次铺几千条就是 O(n²)，
        /// 界面直接卡死（几千项目录的用户实测反馈）。合并之后一批变化只算一次。
        /// </summary>
        private void RequestUpdateEmptyStates()
        {
            if (_emptyStatePending) return;
            _emptyStatePending = true;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _emptyStatePending = false;
                    UpdateEmptyStates();
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { _emptyStatePending = false; }
        }

        // 用 WindowChrome 去掉了系统边框，所以拖拽/双击最大化要自己接。
        // 注意：窗口按钮本身标了 WindowChrome.IsHitTestVisibleInChrome=True，
        // 它们的 Click 不会被下面的 MouseLeftButtonDown 吞掉。

        private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                // 双击标题栏 = 最大化 / 还原（与系统行为一致）
                ToggleMaximize();
                return;
            }
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            // 最大化后窗口会贴上屏幕边缘并盖住我们画的 1px 描边，
            // 用 Padding 让内容不被屏幕边缘裁掉。
            RootPadding();
        }

        /// <summary>最大化时给根 Grid 留出边距，否则自定义描边会被屏幕边缘吃掉。</summary>
        private void RootPadding()
        {
            try
            {
                if (Content is Border bd && bd.Child is Grid g)
                    g.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            }
            catch { }
        }

        private void OnMinimizeClick(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OnMaximizeClick(object sender, RoutedEventArgs e)
        {
            ToggleMaximize();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            try
            {
                // 同步最大化/还原图标
                if (MaxIcon != null)
                    MaxIcon.Data = WindowState == WindowState.Maximized
                        ? (Geometry)FindResource("IconRestore")
                        : (Geometry)FindResource("IconMaximize");
            }
            catch { }
            RootPadding();
        }
    }
}
