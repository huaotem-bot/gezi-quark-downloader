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
using GeZi.DownloadEngine;
using GeZi.Core.Models;
using GeZi.Core.Support;
using GeZi.Services;
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
using GeZi.Providers;
using GeZi.Providers.Quark;
using GeZi.Providers.Uc;
using GeZi.Providers.Aliyun;
using GeZi.Providers.Xunlei;
using GeZi.Providers.Shared;
using GeZi.Providers.Qr;
using ShapePath = System.Windows.Shapes.Path;   // 避免与 System.IO.Path 撞名

namespace GeZi
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<ShareFileItem> _files = new ObservableCollection<ShareFileItem>();

        // ⚠️ 批量入队时**必须**逐条 Add（走 AddTaskRows 的挂起开关），
        //    绝不能再用 DeferRefresh 包住一批 Add —— 那是非法用法，会抛
        //    InvalidOperationException（详见 AddTaskRows 的注释）。
        //    也不能改成「一次多 item 的 Add」：WPF 的 ListCollectionView 明确拒绝
        //    范围通知（实测抛 NotSupportedException「不支持范围操作」）。
        private readonly ObservableCollection<TaskItem> _tasks = new ObservableCollection<TaskItem>();

        // 批量插入任务行期间挂起集合变更的副作用（空态/计数/待办落盘），
        // 由 AddTaskRows 在末尾统一同步一次。只影响这三项同步，不影响数据本身。
        private bool _suppressTaskSync;


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
        /// 四家网盘**已经登录过**的客户端实例，按网盘存放 —— 切走时不销毁，切回来直接复用。
        /// <para>
        /// 🚨 为什么要有它（2026-10-25 用户反馈「我在我的网盘中切换网盘，在我的账号中又会重新登录，
        /// 这也太浪费性能了」）：原来 <see cref="SwitchToPanAsync"/> 每次都走完整登录流程
        /// （新建客户端 → <c>ValidateAsync</c> 网络往返 → <c>ResolveAccountInfoAsync</c>，
        /// 夸克那条还要起 Python 桥，实测 5~6 秒）。但这家网盘其实**早就验过、还活着**，
        /// 账号没变、凭证没变，没有任何理由重新登录一遍。
        /// </para>
        /// <para>
        /// 现在四家的实例都留着：切走只 <c>StopKeepAlive()</c> 停心跳（避免多份心跳同时跑），
        /// 切回来 <c>StartKeepAlive()</c> 继续用。切换因此变成**零网络、零重建**。
        /// 只有登出、凭证变更、校验失败时才 <c>Dispose()</c> 并移出字典。
        /// </para>
        /// </summary>
        private readonly Dictionary<PanKind, QuarkClient> _quarkCache =
            new Dictionary<PanKind, QuarkClient>();


        /// <summary>阿里 / 迅雷的已登录实例缓存（理由同 <see cref="_quarkCache"/>）。</summary>
        private readonly Dictionary<PanKind, IPanProvider> _providerCache =
            new Dictionary<PanKind, IPanProvider>();


        /// <summary>
        /// **解析/浏览分享**专用的夸克系客户端，与「当前登录账号」的 <see cref="_client"/> 分开。
        /// <para>
        /// 为什么要分开：<see cref="_client"/> 是"我的账号"（账号卡片、我的网盘、容量都看它）。
        /// 早先解析夸克分享时直接 `_client.Dispose(); _client = new QuarkClient(设置里的夸克Cookie)`，
        /// 等于把当前账号的客户端换掉 —— 登录着 UC / 阿里 / 迅雷 时一解析夸克分享，
        /// 账号就被挤成夸克了。所以分享用独立客户端，绝不碰 <see cref="_client"/>。
        /// </para>
        /// <para>
        /// 当前账号本来就是夸克时它是 null，<see cref="ShareClient"/> 直接回落给 <see cref="_client"/>，
        /// 行为和以前一模一样（不多建一个客户端、不多一份 Cookie）。
        /// </para>
        /// </summary>
        private QuarkClient _shareClient;


        /// <summary>分享路径统一从这里取客户端（当前账号是夸克时就是 <see cref="_client"/>）。</summary>
        private QuarkClient ShareClient
        {
            get { return _shareClient ?? _client; }
        }


        /// <summary>
        /// 「转存到自己网盘再取直链」这条路径该用哪个客户端（null = 当前分享不属于夸克系，没有转存这条路）。
        /// <para>
        /// 🚨 判据必须是<b>当前分享属于哪家</b>（<see cref="_panKind"/>），
        /// 而<b>不是</b>当前登录的账号（<see cref="_activeKind"/>）。
        /// </para>
        /// <para>
        /// 历史 bug（2026-10-25 用户实测）：原来 <c>EnsureFolderAsync</c> 用
        /// <c>OwnIsQuarkLike</c>（判 <c>_activeKind</c>）+ <c>_client</c>，
        /// 于是「登录着迅雷、去下 UC 的分享」时直接被判定为"这家没有转存能力"，
        /// 打印「无法创建中转文件夹，转存失败」并整批放弃 ——
        /// 表现就是用户说的「UC 用转存下载会失败，得先把『我的网盘』调成 UC 才行」。
        /// </para>
        /// </summary>
        private QuarkClient TransitClient
        {
            get
            {
                PanKind kind = ShareMode ? _panKind : _activeKind;
                if (kind != PanKind.Quark && kind != PanKind.Uc)
                    return null;
                return ShareMode ? ShareClient : _client;
            }
        }


        /// <summary>
        /// 启动时那次自动登录的任务（可能是 null：没有保存的 Cookie 时不会启动）。
        /// 续传要用它来「等登录态就绪」——见 <see cref="EnsureClientForResumeAsync"/>。
        /// </summary>
        private System.Threading.Tasks.Task _startupLoginTask;

        private string _pwdId;

        private string _stoken;

        /// <summary>
        /// 当前解析的**原始分享链接**（完整 URL，含提取码若有）。
        /// <para>
        /// 🚨 2026-11-03：续传的「百分百兜底」。
        /// PwdId 只有 12 位短码，丢了就再也找不回这个分享；
        /// 而完整链接可以随时重新解析，重新拿到 stoken + share_fid_token。
        /// 所以每次解析成功都要记下来，并随任务一起落盘。
        /// </para>
        /// </summary>
        private string _shareUrl;


        /// <summary>
        /// 当前解析的分享属于哪个网盘。默认夸克 —— 也就是说，
        /// 只要不碰多网盘，所有既有逻辑走的都是原来的路径，行为完全不变。
        /// </summary>
        private PanKind _panKind = PanKind.Quark;


        /// <summary>非夸克网盘的客户端。夸克走 <see cref="_client"/>，这里为 null。</summary>
        private IPanProvider _panProvider;


        /// <summary>非夸克网盘的分享引用（夸克用 <see cref="_pwdId"/> + <see cref="_stoken"/>）。</summary>
        private PanShareRef _panShareRef;


        /// <summary>
        /// **当前登录的账号属于哪个网盘**（账号卡片、「我的网盘」都跟着它走）。
        /// <para>
        /// 与 <see cref="_panKind"/> 是两件事：那个是"正在解析的分享属于谁"，
        /// 这个是"我自己的账号是谁"。两者可以不同 —— 比如登录着 UC，
        /// 却在解析一个夸克分享。
        /// </para>
        /// <para>
        /// 夸克与 UC 共用 <see cref="QuarkClient"/>（同一套 ucpro 后端，靠 Kind 切
        /// 域名/pr/请求头）；阿里云盘与迅雷云盘协议完全不同，走
        /// <see cref="_ownProvider"/>（<see cref="IPanProvider"/>）。
        /// 四条路都收敛到下面那几个 Own* 助手方法，调用方不必关心区别。
        /// </para>
        /// </summary>
        private PanKind _activeKind = PanKind.Quark;


        /// <summary>
        /// 「我的网盘」切换的**序号**：每次点一家网盘就 +1。
        /// <para>
        /// 🚨 2026-10-25 用户反馈「当我疯狂在我的网盘中切换网盘的时候有时候会提示
        /// 某某某网盘未登录」。根因是 <c>OnDrivePanTabClick</c> 是 <c>async void</c>：
        /// 连点三家会有三个切换同时跑，A 切换完成后回来看 <see cref="_activeKind"/>
        /// 已经被 B 改成别家，于是把「本来成功」判成失败 → 弹「还没登录」。
        /// </para>
        /// <para>
        /// 现在用序号做「最后一次点击说了算」：回来的结果序号不等于当前序号就直接丢弃，
        /// 既不弹窗也不刷新 UI。
        /// </para>
        /// </summary>
        private int _panSwitchSeq;


        /// <summary>串行化网盘切换：同一时刻只允许一个切换在跑，避免两个切换互相改状态。</summary>
        private readonly SemaphoreSlim _panSwitchGate = new SemaphoreSlim(1, 1);


        /// <summary>
        /// 下一次重建「我的网盘」切换条时，滑块要不要走滑动动画。
        /// 只有「用户点了另一家网盘」这一条路径置 true —— 切换完成后
        /// <c>RefreshDrivePanTabs()</c> 会重建按钮，滑块需要从旧位置滑到新位置；
        /// 其余重建（首次显示、登录态变化）都直接落位。
        /// </summary>
        private bool _panThumbAnimate;


        /// <summary>
        /// 阿里 / 迅雷的**自有网盘**客户端（当前账号那一家）。
        /// 夸克 / UC 时恒为 null —— 那两家用 <see cref="_client"/>。
        /// </summary>
        private IPanProvider _ownProvider;


        /// <summary>当前登录账号的展示名（"夸克网盘" / "UC网盘" / "阿里云盘" / "迅雷云盘"）。</summary>
        private string ActivePanDisplayName
        {
            get
            {
                switch (_activeKind)
                {
                    case PanKind.Uc: return "UC网盘";
                    case PanKind.Aliyun: return "阿里云盘";
                    case PanKind.Xunlei: return "迅雷云盘";
                    default: return "夸克网盘";
                }
            }
        }


        /// <summary>
        /// 当前账号是不是"夸克系"（夸克 / UC）—— 决定自有网盘走
        /// <see cref="_client"/> 还是 <see cref="_ownProvider"/>。
        /// </summary>
        private bool OwnIsQuarkLike
        {
            get { return _activeKind == PanKind.Quark || _activeKind == PanKind.Uc; }
        }


        // ⚠️ 原来这里有一个 CanBrowseOwnDrive 属性（判断当前账号能否浏览自有网盘）。
        //    四家网盘全部实现了 ListOwnDirAsync / GetSpaceAsync / GetOwnDownloadLinksAsync
        //    之后它恒为 true，所有 `if (!CanBrowseOwnDrive)` 分支都是死代码，已一并删除
        //    （见 MainWindow.Browse.cs 的 OnModeChanged）。
        //    别再按旧记忆加回来。


        /// <summary>
        /// 这条下载「来自哪家网盘」—— 分享下载看 <see cref="_panKind"/>，自有网盘下载看 <see cref="_activeKind"/>。
        /// <para>
        /// 🚨 下面几个下载头（Referer / UserAgent / Origin / UseOpenSsl / Cookie）必须用它，
        /// <b>不能直接用 <see cref="_activeKind"/></b>：那是「当前登录账号」，
        /// 而下载任务来自「当前解析的分享」。
        /// </para>
        /// <para>
        /// 历史 bug（2026-10-25 用户实测）：登录着夸克、去下阿里分享时，
        /// <c>_activeKind</c> 是 Quark 而 <c>_panKind</c> 是 Aliyun，
        /// 于是 <see cref="OwnDownloadReferer"/> 返回 null → 请求一个 Referer 都不带 →
        /// 阿里 OSS 直链（签名里含 <c>x-oss-additional-headers=referer</c>）直接回
        /// <c>400 AuthorizationArgumentError</c>，界面显示「HTTP 400: Bad Request」。
        /// 转存本身是成功的（所以日志里有「已清理临时转存 1 项」），死在最后一步 OSS 请求上。
        /// </para>
        /// <para>
        /// 与 <see cref="TransitClient"/> 同一个判据，改这里时两边要一致。
        /// </para>
        /// </summary>
        private PanKind DownloadPanKind
        {
            get { return ShareMode ? _panKind : _activeKind; }
        }


        /// <summary>
        /// 下载时该带的 Referer（null = 用下载器默认的夸克 Referer）。
        /// <para>
        /// 各家 CDN 的直链签名绑了不同的 Referer，带错会被 403：
        /// 阿里 OSS 签名里含 <c>x-oss-additional-headers=referer</c>，必须是
        /// <c>https://www.alipan.com/</c>；迅雷是 <c>https://pan.xunlei.com/</c>。
        /// 夸克系返回 null，保持原有行为。
        /// </para>
        /// </summary>
        private string OwnDownloadReferer
        {
            get { return DownloadRefererFor(DownloadPanKind); }
        }


        // ---------------- 按网盘取下载头（两处共用，别各写一份） ----------------
        // 调用方有两个：① MainWindow.Download.cs 新建任务（DownloadPanKind）
        //               ② MainWindow.Pending.cs 重启后续传（记录里的 PanKindValue）
        // 抽成静态方法是因为续传那条路原来**一个头都没设**，用的是夸克默认值 ——
        // 阿里续传必 400、迅雷必 403、UC 必 403（实测同类症状）。

        /// <summary>
        /// 按网盘取下载 Referer。四家都从 <see cref="PanClientProfile"/> 取，
        /// 不再只给 UC 特殊处理 —— 阿里/迅雷/夸克原来落到 null（回退成夸克默认值），
        /// 与它们取链时带的头不一致。
        /// </summary>
        internal static string DownloadRefererFor(PanKind kind)
        {
            return PanClientProfile.For(kind).Referer;
        }


        /// <summary>按网盘取下载 User-Agent（与取链同族同版本）。</summary>
        internal static string DownloadUserAgentFor(PanKind kind)
        {
            return PanClientProfile.For(kind).DownloadUserAgent;
        }


        /// <summary>按网盘取下载 Origin。</summary>
        internal static string DownloadOriginFor(PanKind kind)
        {
            return PanClientProfile.For(kind).Origin;
        }


        /// <summary>
        /// 按网盘取下载数据面的**额外请求头**（Sec-Ch-Ua / Sec-Fetch-* / Accept-Language 等）。
        ///
        /// <para>
        /// 🚨🚨 2026-11-05：原来下载层只有 UA / Referer / Origin / Cookie 四个头，
        /// 而取链层带了完整浏览器头 —— 身份自相矛盾。UC 对非会员账号的视频
        /// 下发「请使用 UC 浏览器播放」引导流（固定 15,340,287 字节），与这个缺口相关。
        /// </para>
        /// <para>
        /// 这些头会经 RequestConfiguration.Headers → ChunkDownloader.ExtraHeaders
        /// 透传到数据面，无需改动下载引擎。
        /// </para>
        /// </summary>
        internal static Dictionary<string, string> DownloadExtraHeadersFor(PanKind kind)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            PanClientProfile.ApplyHeaders(kind, d);
            // Cookie / Origin / Accept / User-Agent 由调用方单独设置，别在这里重复。
            d.Remove("Cookie");
            return d;
        }


        /// <summary>按网盘决定下载数据面是否走 OpenSSL 桥（只有 UC 需要）。</summary>
        internal static bool DownloadUseOpenSslFor(PanKind kind)
        {
            return kind == PanKind.Uc;
        }


        /// <summary>
        /// 2026-11-04：拼「源文件指纹」，写进 .geziresume，用于识别
        /// 「同一目标路径换成了另一个源文件」。
        /// <para>
        /// 🚨 为什么需要：续传状态是按**目标路径**定位的（PartsPaths.ResolvePaths 只看 dest），
        /// 而 PathUtil.UniquePath 会把同名文件落成「名称 (1)」。
        /// 真实事故：用户先下夸克的「示例视频」(15,340,287 字节)，
        /// 之后从 UC 网盘下**同名**的另一个文件 (186,678,707 字节)，
        /// 新任务命中旧路径的续传状态 → 拿旧进度续新文件。
        /// </para>
        /// <para>
        /// 为什么不靠 ETag：那是服务端给的，CDN 不回 / 探测超时 / 走 OpenSSL 桥时都会缺失，
        /// 缺失时校验被静默跳过 —— 所以这里用**本地必然有值**的三元组。
        /// </para>
        /// </summary>
        /// <param name="size">源文件字节数（网盘接口给的，0 表示未知）。</param>
        /// <param name="panKind">源文件所在网盘（PanKind 的整数值）。</param>
        /// <param name="fid">源文件在网盘上的 fid（为空则退回只用前两项）。</param>
        /// <remarks>
        /// 实现放在 <see cref="GeZi.Core.Support.SourceKey"/>（Core 层），
        /// 因为**自检项目不引用 WPF 主项目**，放这里它测不到。
        /// 这里只做一层转发，保证 UI 侧调用点不用改。
        /// </remarks>
        internal static string BuildSourceKey(long size, int panKind, string fid)
        {
            return GeZi.Core.Support.SourceKey.Build(size, panKind, fid);
        }


        /// <summary>
        /// 下载任务该带的 User-Agent。
        /// <para>
        /// 与 <see cref="OwnDownloadReferer"/> 成对使用：UC 的 CDN 只认 UC 自己的
        /// 客户端 UA（<c>UcConstants.DlUa</c>），拿夸克的 <c>QuarkConstants.DlUa</c>
        /// 去请求同样会被防盗链拒。空 = 用下载器默认值（夸克 UA）。
        /// </para>
        /// </summary>
        private string OwnDownloadUserAgent
        {
            get { return DownloadUserAgentFor(DownloadPanKind); }
        }


        /// <summary>
        /// 下载任务该带的 Origin，必须与 <see cref="OwnDownloadReferer"/> 同源。
        /// 空 = 用下载器默认值（<c>https://pan.quark.cn</c>）。
        /// </summary>
        private string OwnDownloadOrigin
        {
            get { return DownloadOriginFor(DownloadPanKind); }
        }


        /// <summary>
        /// 下载数据面是否走 OpenSSL 桥。
        /// <para>
        /// <b>UC 必须为 true</b>：UC 的下载 CDN <c>dl-pc-sz-cf.pds.uc.cn</c> 按 TLS
        /// ClientHello 指纹过滤，Windows 自带的 SChannel 一律回
        /// <c>403 RequestDeniedByCallback / require login [auth not found]</c>。
        /// 同一个直链、同一份 Cookie 换到 OpenSSL 通道立刻回 206（实测）。
        /// </para>
        /// </summary>
        private bool OwnDownloadUseOpenSsl
        {
            get { return DownloadUseOpenSslFor(DownloadPanKind); }
        }


        /// <summary>
        /// 下载任务该带的 Cookie。
        /// <para>
        /// <b>必须判空</b>：阿里/迅雷登录后 <see cref="_client"/> 是 null
        /// （见 <c>ApplyProviderLoginAsync</c> 里的 <c>_client = null</c>），
        /// 这里直接取 <c>_client.CookieStr</c> 会在点「下载」的瞬间抛
        /// NullReferenceException，表现为「程序发生未处理错误」弹窗。
        /// 而且非夸克网盘本来就不该带夸克登录态（别把夸克 Cookie 发给别家 CDN）。
        /// </para>
        /// </summary>
        private string DownloadCookie
        {
            get
            {
                // 阿里/迅雷的直链是 OSS 预签名，带了 Cookie 反而可能被判非法请求。
                // 判据用 DownloadPanKind（分享看 _panKind、自有看 _activeKind），
                // 不能只看 ShareMode —— 自有网盘下阿里时 _client 是 null，
                // ShareClient 会回落到残留的 _shareClient（可能是夸克），把夸克 Cookie 发出去。
                PanKind kind = DownloadPanKind;
                if (kind == PanKind.Aliyun || kind == PanKind.Xunlei) return "";
                // ⚠️ 用 ShareClient 而不是 _client：UC 分享时真正取链的客户端是
                //    _shareClient（带 UC Cookie），_client 可能挂着**夸克**账号 ——
                //    拿夸克 Cookie 去请求 UC 的 CDN 只会 401。
                var c = ShareMode ? ShareClient : _client;
                return c != null ? c.CookieStr : "";
            }
        }


        /// <summary>
        /// 当前是否**已经登录了某个账号**（四家任意一家）。
        /// <para>
        /// 原来到处写 <c>_client != null</c> —— 那只对夸克/UC 成立。
        /// 阿里/迅雷登录后 <see cref="_client"/> 仍是 null（走 <see cref="_ownProvider"/>），
        /// 用旧判据会把这些账号当成"未登录"，功能全部点不动。
        /// </para>
        /// </summary>
        private bool IsLoggedInAny
        {
            get { return OwnIsQuarkLike ? _client != null : _ownProvider != null; }
        }


        /// <summary>
        /// 当前分享是否由**独立 provider**（阿里 / 迅雷）承载。
        /// <para>
        /// 🚨 这里**不能**写成「不是夸克」（<c>_panKind != PanKind.Quark</c>）：
        /// UC 虽然也是"另一家网盘"，但它在数据层与夸克同源 —— 走的是
        /// <see cref="_shareClient"/> / <see cref="_client"/> 这套 QuarkClient 链路，
        /// 只是 Cookie 与域名换成 UC 的，<see cref="_panProvider"/> 对它**始终是 null**。
        /// 用「不是夸克」当判据会把 UC 分享推给 provider 分支，然后立刻抛
        /// 「多网盘客户端未就绪」—— 用户实测到的「UC 无法解析链接 / 无法入队下载」
        /// 就是这条错判据造成的（2026-10-25 修）。
        /// </para>
        /// </summary>
        private bool UsesProvider
        {
            get { return _panKind == PanKind.Aliyun || _panKind == PanKind.Xunlei; }
        }


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


        // ---------------- 单次处理文件数提醒 ----------------
        // 曾经还有一道 MaxFiles = 1000 的**硬拒**（超过就整个中止）。
        // 2026-10-26 用户要求「不再有文件限制」，硬拒已删除：
        // 选了多少就处理多少，软件不替用户决定"这个量级太多了"。
        //
        // 只保留这道**软确认**：数量偏多时先问一句「确定继续吗」，
        // 用户点「确定」就照常处理。它是提示，不是闸门 ——
        // 误操作（全选一个几万文件的目录）时用户还有机会反悔，
        // 但不会出现"我明明想下，软件却不让我下"。
        // 阈值只在这里定义一处，别在别处写死。
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
            // 任务列表按「网盘」分组（用户要求：第一级是网盘，网盘之下才是任务）。
            // 分组和上面四个状态页签不冲突：页签先按状态过滤，剩下的再按网盘归组。
            // 组的先后 = 该网盘第一条任务出现的先后，不做额外排序，
            // 免得把用户看着的任务顺序打乱。
            _taskView.GroupDescriptions.Add(
                new System.Windows.Data.PropertyGroupDescription("PanGroup"));
            // 第二级：同一个「任务」（一次点下载选中的那批文件）折叠在一起。
            // ⚠️ 这里的个数必须与 MainWindow.xaml 里 ListBox.GroupStyle 的个数**相等**，
            //    且顺序一一对应（① 网盘 ② 任务）。少一个 WPF 就静默回落默认模板，
            //    多一个则最后一个分组头是空白 —— 两种都表现为"分组没生效"。
            // ⚠️ 分组键直接用 **TaskGroupName** 而不是 ID：CollectionViewGroup.Name
            //    就是分组头绑定的 {Binding Name}，用 ID 会显示成 "tg:638..."。
            //    重名问题在 MakeTaskGroup 里解决（同名任务自动加序号），
            //    比在这里挂 ValueConverter 简单，也不会踩绑定失败静默回落的坑。
            _taskView.GroupDescriptions.Add(
                new System.Windows.Data.PropertyGroupDescription("TaskGroupName"));
            TaskList.ItemsSource = _taskView;
            ShareModeRadio.IsChecked = true;

            // 侧栏导航（ListBox）与内容区（TabControl）的初始对齐。
            // 顺序很重要：先建好账号卡初始态，再让选中项落到第 0 页 —— 这样
            // 后续任何 Tabs.SelectedIndex 赋值都会经由 OnTabsSelectionChanged
            // 反过来把 NavList 同步到位，两边永远不会不同步。
            UpdateAccountCard(null, 0, 0);
            // 老配置升级上来时档案里没有「主账号」标记 —— 每家网盘补一个，
            // 否则账号列表会出现「有账号但没一个在用」的中间态。
            // 失败不抛（档案属于锦上添花，不能挡住启动）。
            try { AccountStore.EnsurePrimaryFlags(); } catch { }
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
                // 批量插入期间由 AddTaskRows 挂起，末尾统一同步一次，避免 N 次 O(n) 重算。
                if (_suppressTaskSync) return;
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

            // 🚨 2026-11-03：日志清理定时器（每 6 小时一次）。
            //   为什么需要它：启动时清一次不够 —— 程序可能连续运行好几天，
            //   跨零点后旧的日志就超期了，但没人去删。
            //   6 小时是个折中：足够及时，又不会频繁碰磁盘。
            _logPurgeTimer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromHours(6),
                System.Windows.Threading.DispatcherPriority.Background,
                (s, e) => { try { LogStore.PurgeOldLogs(); } catch { } },
                Dispatcher);
            _logPurgeTimer.Start();

            // 全局未处理异常也灌进界面日志（见 App.LogFatal）。
            // 用户反馈「日志里显示的东西不够全面」：原来异常只写 startup-error.log，
            // 界面和诊断包里都看不到，出问题时切「详细」也一个字都没有。
            App.LogSink = (text, detail) => EnqueueLog(text, detail);

            // 把上次关闭前的日志灌回来（2026-10-26 用户诉求「退出重进不丢」）。
            // ⚠️ 放在 FlushLogs 定时器启动之后、且**只写 _logAll 不直接改 LogBox**：
            //    统一走 RebuildLogBox，才能按当前「简要/详细」档位正确过滤。
            //    LogStore.LoadTail 内部只读文件尾部 2MB，不会拖慢启动。
            RestoreLogFromDisk();

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
            // 现在核心那条默认关掉（逐任务诊断日志），
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

            // 历史记录文件读坏时（state.json 损坏/被占用）HistoryStore 会把它改名留档，
            // 但那个类不依赖 UI，只能通过这个回调把话传到日志面板，否则用户
            // 只会发现「未完成任务和历史记录都空了」，完全不知道发生了什么。
            HistoryStore.LogSink = msg =>
            {
                try { Log(msg); } catch { }
            };

            // 迅雷取码器与「内嵌登录」窗口共用 profile-xunlei（deviceid 必须一致，
            // 不能换目录），而 WebView2 不允许同一用户数据目录开两个环境。
            // 登录窗口开着时让它先让路，别去抢一个必然失败的窗口。
            XunleiCaptchaFetcher.IsBlocked = () => _panWebLoginOpen;

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
            ApplyFusionSettingsToScheduler();
            LoadSettingsToUi();

            // 检查更新：把配置交给 UpdateService。
            // 同时用**上次落盘的版本号**恢复红点状态 —— 红点必须跨会话保持，
            // 否则用户重启一次、红点就没了（而本次可能因节流根本没去查）。
            UpdateService.Attach(_settings);

            // 加载完成后再提示未完成任务（此时窗口已就绪，弹窗才有 Owner）
            Loaded += (s, e) =>            {
                // 🚨 小屏笔记本（1366×768，工作区约 728 高）上 XAML 里写死的
                // Height=780 会超出屏幕 —— 底部状态栏和按钮被裁到屏幕外，用户够不到。
                // 这里按工作区收一次：只在**装不下**时缩，正常屏幕尺寸完全不受影响。
                try { FitToWorkArea(); } catch { }

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
                //
                // 🚨🚨 2026-11-01 修复「重启后续传不动、账号一直显示未登录」：
                //   原来这里与下面的自动登录是**同时**发起的，谁先跑完完全看运气。
                //   续传框（模态 ShowDialog）一旦先弹出，就接管了消息泵，
                //   而自动登录的续体排在 Normal 优先级上 —— 模态框不关就一直排队，
                //   于是 _client 始终是 null：界面显示"未登录"、续传取链拿不到登录态、
                //   点了「继续下载」什么都不会发生。用户关掉软件重开时不再弹续传框，
                //   登录得以正常跑完，所以"重开一次反而显示已登录"——正是这个错位。
                //   现在改成：**先等自动登录跑完（无论成败），再弹续传框**。
                //   登录本身有超时兜底（ValidateAsync / 桥接通道都不会无限等），
                //   万一卡住，下面的 WaitAsync 也会在 20 秒后放行，不会永远不弹。
                // 🚨🚨 2026-11-01 又一个坑（本次实测抓到）：
                //   `new Action(async () => {...})` 是 **async void** ——
                //   await 之后的异常**不会**被外层 try/catch 捕获，也不会有任何日志，
                //   表现就是「日志说正在等登录，然后再无下文，续传框永远不弹」。
                //   现在把整个异步体包进一个真正的 async Task 方法，
                //   并在最外层兜住所有异常 + 落日志，绝不静默。
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _ = ShowPendingAfterLoginAsync();
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                // 顺手把「未完成 N」入口点亮（不弹窗，只是让入口在页签行右侧可见）。
                try { RefreshPendingEntry(); } catch { }

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

            // 启动自动登录：**先看上次浏览的是哪一家**（LastBrowsePanKind），
            // 没记过（-1，首次升级的用户）再退回 ActivePanKind 的旧逻辑；
            // 那家没凭证就按固定顺序找一家有凭证的（夸克 → UC → 阿里 → 迅雷）。
            // ⚠️ 顺序不能乱：旧版本只认识夸克/UC，用户升级后旧 Cookie 仍在，
            //    所以这两家必须排在前面，保持"升级后行为不变"。
            {
                // 启动时要先打开的那一家：
                //   ① 浏览记忆优先（用户在页签里最后点的那家）
                //   ② 没记过就退回「上次登录身份」（旧行为，逐字不变）
                PanKind want = _settings.LastBrowsePanKind >= 0
                    ? (PanKind)_settings.LastBrowsePanKind
                    : (_settings.ActivePanKind == (int)PanKind.Uc ? PanKind.Uc
                        : _settings.ActivePanKind == (int)PanKind.Aliyun ? PanKind.Aliyun
                        : _settings.ActivePanKind == (int)PanKind.Xunlei ? PanKind.Xunlei
                        : PanKind.Quark);

                var order = new List<PanKind>
                {
                    want,
                    PanKind.Quark, PanKind.Uc, PanKind.Aliyun, PanKind.Xunlei,
                };

                PanKind pick = PanKind.Quark;
                string cred = null;
                var seen = new HashSet<PanKind>();
                foreach (var k in order)
                {
                    if (!seen.Add(k)) continue;      // 去重（第一项可能与后面的重复）
                    string c;
                    switch (k)
                    {
                        case PanKind.Uc: c = _settings.UcCookie; break;
                        case PanKind.Aliyun:
                            // 优先可续期的 refresh_token；网页只给过短期令牌时退回 access_token。
                            c = !string.IsNullOrEmpty(_settings.AliyunRefreshToken)
                                ? "rt:" + _settings.AliyunRefreshToken
                                : (!string.IsNullOrEmpty(_settings.AliyunAccessToken)
                                    ? "at:" + _settings.AliyunAccessToken
                                    : null);
                            break;
                        case PanKind.Xunlei:
                            // 🚨 2026-11-01 修复：原来只取 XunleiPanToken（两小时寿命的 access_token），
                            // 一旦过期就必须重新登录 —— 哪怕手里明明有能续期的 refresh_token。
                            // 现在按"可用性"优先选：
                            //   ① XunleiPanToken（网页登录的打包记录 xlrec:，里面有 at+rt，最完整）；
                            //   ② 退而用 XunleiWebRefreshToken / XunleiRefreshToken 现换一张新的。
                            c = !string.IsNullOrEmpty(_settings.XunleiPanToken)
                                ? _settings.XunleiPanToken
                                : (!string.IsNullOrEmpty(_settings.XunleiWebRefreshToken)
                                    ? "xlrt:" + _settings.XunleiWebRefreshToken
                                    : (!string.IsNullOrEmpty(_settings.XunleiRefreshToken)
                                        ? "xlrt:" + _settings.XunleiRefreshToken
                                        : null));
                            break;
                        default: c = _settings.Cookie; break;
                    }
                    if (!string.IsNullOrEmpty(c))
                    {
                        pick = k;
                        cred = c;
                        break;
                    }
                }

                if (cred != null)
                {
                    _activeKind = pick;
                    Log("正在自动登录（" + ActivePanDisplayName + "）…");
                    // 校验是异步的（UC/夸克要过网络，必要时还要借 Python 桥），
                    // 这期间卡片不能一直挂着"未登录" —— 用户会以为登录失败
                    // （反馈过"明明登录了却显示没登录、没名字没头像"）。
                    // 有上次的昵称就先显示出来，提示语改成"正在校验登录态…"。
                    // 侧栏账号卡已删除，昵称/容量只进「登录」页的「当前状态」卡。
                    UpdateAccountCard(string.IsNullOrEmpty(_settings.Who) ? null : _settings.Who, 0, 0);
                    LogDetail("正在校验登录态…");
                    // 记下这个任务：续传前要等它（见 EnsureClientForResumeAsync）。
                    _startupLoginTask = pick == PanKind.Aliyun || pick == PanKind.Xunlei
                        ? ApplyProviderLoginAsync(pick, cred)
                        : ApplyLoginAsync(cred, pick);

                    // 校验期间把下载按钮变灰并给出提示，完成后再解禁
                    //（用户反馈：刚进软件就点下载会直接 401）。
                    UpdateLoginPendingUi();
                    var loginTask = _startupLoginTask;
                    if (loginTask != null)
                    {
                        loginTask.ContinueWith(_ =>
                        {
                            try
                            {
                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    try { UpdateLoginPendingUi(); } catch { }
                                }));
                            }
                            catch { }
                        }, System.Threading.Tasks.TaskScheduler.Default);
                    }
                }
                else
                {
                    Log("就绪。未登录：可用「分享链接」模式下载公开分享，或到「登录」页扫码。");
                }
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

            // 2026-11-02：关于页的版本号在启动时填一次（XAML 里是占位 "v?"）。
            try { RefreshAboutPage(); } catch { }
        }


        /// <summary>
        /// 等登录就绪后再弹续传框（真正异步，异常不会静默）。
        /// <para>
        /// 🚨 必须独立成 <c>async Task</c> 方法，不能写成 <c>new Action(async () => ...)</c>：
        /// 后者是 async void，await 之后的异常无人接，连日志都没有 ——
        /// 实测症状就是「日志停在『正在等登录态就绪』，续传框永远不出现」。
        /// </para>
        /// </summary>
        private async Task ShowPendingAfterLoginAsync()
        {
            try
            {
                await WaitStartupLoginBeforePendingAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                LogDetail("等待登录态时出错（不影响弹窗）：" + ex.Message);
            }

            try
            {
                ShowPendingOnStartup();
            }
            catch (Exception ex)
            {
                // 弹窗失败必须留下痕迹，否则用户只看到「没弹」而无从排查。
                Log("未完成任务提示打开失败：" + ex.Message);
            }
        }


        /// <summary>
        /// 把主窗口收进当前工作区（已扣掉任务栏）。
        /// <para>
        /// 背景：XAML 里写死 <c>Height="780" Width="1240"</c>。在 1366×768 的笔记本上
        /// 工作区只有约 1366×728 —— 窗口比屏幕还高，底部状态栏和「暂停全部」那一排
        /// 直接被裁到屏幕外，用户根本够不到，也没法靠拖窗口解决（标题栏已经在顶部）。
        /// </para>
        /// <para>
        /// 只在**装不下**时缩，装得下时一个像素都不动，所以大屏上的默认观感不变。
        /// 用 <c>SystemParameters.WorkArea</c> 而不是 <c>PrimaryScreenBounds</c>：
        /// 后者含任务栏区域，按它算会再次把底部压到任务栏下面。
        /// </para>
        /// </summary>
        private void FitToWorkArea()
        {
            Rect wa = SystemParameters.WorkArea;
            if (wa.Width <= 0 || wa.Height <= 0) return;

            // 留一点边距，别贴着屏幕边缘（看起来像被挤住）。
            const double margin = 16;
            double maxW = Math.Max(640, wa.Width - margin * 2);
            double maxH = Math.Max(480, wa.Height - margin * 2);

            if (Width > maxW) Width = maxW;
            if (Height > maxH) Height = maxH;

            // 居中到工作区（WindowStartupLocation=CenterScreen 是按整屏算的，
            // 收小之后要重新摆一次，否则会偏向屏幕右上）。
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
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


        /// <summary>
        /// 日志**视图**落后于内存历史 <see cref="_logAll"/>（需要下一次可见时整体重建）。
        /// <para>
        /// 🚨🚨 2026-11-07 引入，是「日志多了界面就卡」的最终解法的一半。
        /// 用户下载时根本不在日志页（日志在「任务」页最下方），
        /// 但 <c>FlushLogs</c> 原来照样每 200ms 往 LogBox 里 <c>AppendText</c> ——
        /// TextBox 带 <c>TextWrapping="Wrap"</c>，每次都要把已有的 2000 行重新测量折行。
        /// 现在不看就不碰控件，只把这里置 true；等真正切到日志页时
        /// 一次性 <see cref="RebuildLogBox"/> 补上，代价只有一次。
        /// </para>
        /// <para>
        /// ⚠️ 所有「离开 → 回来」的路径都必须走 <see cref="MarkLogViewStaleIfHidden"/> 或
        /// 直接置 true，绝不能让视图悄悄落后却没人重建。
        /// </para>
        /// </summary>
        private bool _logViewDirty;


        /// <summary>日志攒批刷新器。见 <see cref="Log"/> 的注释。</summary>
        private System.Windows.Threading.DispatcherTimer _logFlushTimer;

        /// <summary>日志过期清理定时器（2026-11-03，每 6 小时跑一次 PurgeOldLogs）。</summary>
        private System.Windows.Threading.DispatcherTimer _logPurgeTimer;


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


        /// <summary>LogBox 保留的最大行数（超出后从头砍）。</summary>
        /// <remarks>
        /// 加了"后台驻留"之后这条变得必要：以前关闭=退出，
        /// 日志最多活一次会话；现在窗口一收就能挂好几天，日志会**无限增长**，
        /// 既吃内存又让 TextBox 越来越卡（每次 AppendText 都要重排）。
        /// 2000 行足够回溯一次下载的完整过程，再多也没人翻。
        /// 🚨 但**别靠调小这个数来解决卡顿**：卡顿的真因是「满额后每 200ms 全量重建一次」，
        /// 行数调小只是让每次重建便宜一点，频率问题原封不动。
        /// 正解见 <c>TrimSweep</c> 与 <c>TrimLogLines</c> 的注释。
        /// </remarks>
        private const int LogMaxLines = 2000;


        /// <summary>
        /// 日志满额后**一次腾出多少行**（批量裁剪的步长）。
        /// <para>
        /// 🚨🚨 2026-11-07 定稿：这是「界面日志用久了就卡」的真正解药。
        /// 原来 <c>TrimLogLines</c> 每次只砍「超出的那几条」，砍完就无条件全量重建 ——
        /// 而它被 <c>FlushLogs</c> 每 200ms 调一次，于是日志一满额就变成
        /// 「每秒 5 次 × 2000 行全量拼串 + 全量折行」，且**永不停止**（永远退不回上限以下）。
        /// </para>
        /// <para>
        /// 现在一次腾 <see cref="TrimSweep"/> 行 → 下次重建要等再攒够这么多条日志。
        /// 500 行 ≈ 几千行日志的间隔，正常使用时几个月也碰不到几次；
        /// 而代价只是日志视图里最多多留 500 行（<see cref="LogMaxLines"/> + 该值仍很小）。
        /// </para>
        /// </summary>
        private const int TrimSweep = 500;


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


        // ---------------- 按钮「加载中」态 ----------------
        // 用户反馈：「他们需要加载时间或者是会切换到另一个东西，
        // 每次点击它们的时候都要有一个加载过程，主要是为了让用户看到他在加载」。
        // 本质是消除"点击 → 界面响应"之间那段静默期 —— 没有反馈时，用户无法区分
        // 「程序在忙」和「程序没反应」，于是会重复点击（取直链连点 = 重复发网络请求、
        // 导出连点 = 桌面多出几个 txt）。有反馈时同样的耗时就变成"它在干活，我等一下"。

        /// <summary>正在显示加载态的按钮。用于**拒绝重入**（防重复提交）。</summary>
        private readonly HashSet<Button> _busyButtons = new HashSet<Button>();


        /// <summary>
        /// 加载态的最短显示时长（毫秒）。实现见 <see cref="UiBusy.MinBusyMs"/>。
        /// </summary>
        private const int MinBusyMs = UiBusy.MinBusyMs;


        // ---------------- 「入队」可终止（2026-10-11） ----------------

        /// <summary>「入队中」按钮的文案（按钮保持可点，再点一次即终止）。</summary>
        private const string EnqueueBusyText = "入队中";


        /// <summary>历史面板是不是开着（开关按钮与关闭逻辑都看它）。</summary>
        private bool HistoryOpen
        {
            get { return HistoryOverlay != null && HistoryOverlay.Visibility == Visibility.Visible; }
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


        /// <summary>待算大小的文件夹**队列**（随"露出来"的条目增长）。</summary>
        private readonly List<ShareFileItem> _dirSizeQueue = new List<ShareFileItem>();


        /// <summary>队列是否正在被消费（空了就置回 false）。</summary>
        private bool _dirSizeRunning;


        /// <summary>本轮（同一次目录加载）还能发多少次请求。</summary>
        private int _dirSizeBudget;


        /// <summary>当前这轮目录大小扫描的取消源。换目录/重进时取消上一轮。</summary>
        private CancellationTokenSource _dirSizeCts;


        /// <summary>目录大小扫描的「代」：每次换目录/换网盘 +1，旧一代的后台任务必须整轮作废。</summary>
        private int _dirSizeGen;


        /// <summary>
        /// 一轮目录大小扫描的**上下文快照**。
        /// <para>
        /// 🚨 2026-10-25 修「计算文件夹大小失败（来自：分享）」：
        /// <c>DrainDirSizeQueueAsync</c> 是常驻后台循环，但它调的 <c>SumDirAsync</c>
        /// 读的是**全局可变状态**（ShareMode / _pwdId / _stoken / _activeKind / _ownProvider）。
        /// 用户切网盘或退出分享时，旧循环那一轮请求还没回来，回来时全局状态已经变了，
        /// 于是拿着**新网盘的客户端**去请求**旧网盘的 fid** ——
        /// 迅雷报 file_not_found、夸克报 guest missing pwd_id or stoken。
        /// </para>
        /// <para>
        /// 现在每轮扫描开始时把这几项拍成快照，执行每一项前比对；不一致就整轮退出，
        /// 不再把「模式/账号已经换了」误当成「这个文件夹算不出来」。
        /// </para>
        /// </summary>
        private sealed class DirSizeScanContext
        {
            public int Gen;
            public bool ShareMode;
            public PanKind Kind;
            public string PwdId;
            public string Stoken;
            public string AccountTag;
        }


        /// <summary>本轮扫描的上下文；null = 还没有扫描。</summary>
        private DirSizeScanContext _dirSizeCtx;


        /// <summary>
        /// 单次目录扫描的**请求预算**上限。
        /// </summary>
        /// <remarks>
        /// 递归求和是"每个子目录一次请求"。一个几千文件的分享可能展开成几百次请求 ——
        /// 既慢又容易踩到风控。所以给个预算，超了就停手（剩下的标成「—」而不是 0），
        /// 宁可少算也不要因为一个后台功能把接口打爆。
        /// </remarks>
        private const int DirSizeRequestBudget = 300;


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


        // ---------------- 连接数按文件大小比例分配 ----------------
        // Python Flet 版有「智能调度」：实际线程数 = min(下载线程数, 总连接预算 / 本批任务数)
        // —— 也就是**多个文件平分**总预算。C# 版当初删掉「全局连接预算」设置项时，
        // 连"多文件共享总预算"这件事一起没了，变成每个任务都开满自己的线程数
        // （用户把单文件并发拉到 512、同时跑 3 个 = 1536 条连接，而夸克是按账号限总连接数的）。
        // 「不要平均分配了，改成比例分配，按文件大小，文件越大分配越多，
        // 文件越小分配越少，但有最低下限，不然别让小文件根本下不了了」。
        // 具体算法在 GeZi.DownloadEngine.ThreadAllocator（纯函数，可单独测试）。

        /// <summary>总连接预算。见 <see cref="GeZi.DownloadEngine.ThreadAllocator.DefaultBudget"/>。</summary>
        private const int DownloadConnBudget = GeZi.DownloadEngine.ThreadAllocator.DefaultBudget;


        /// <summary>单个文件的最低连接数。见 <see cref="GeZi.DownloadEngine.ThreadAllocator.MinPerFile"/>。</summary>
        private const int MinThreadsPerFile = GeZi.DownloadEngine.ThreadAllocator.MinPerFile;


        /// <summary>
        /// 内部信号：转存因网盘容量不足失败，请求上层降级到免转存。
        /// 只用于本类的控制流，不向外暴露。
        /// </summary>
        private sealed class CapacityDowngradeException : Exception
        {
            public CapacityDowngradeException() : base("网盘容量不足") { }
        }


        /// <summary>取链目标：一个待取直链的文件。</summary>
        private sealed class LinkTarget
        {
            public string Fid;
            public string Token;   // 仅「分享免转存」路径需要
            public string Name;    // 仅用于日志
        }


        /// <summary>上次写入 pending 时的状态签名，用于避免高频重复写盘。</summary>
        private string _pendingSig;


        /// <summary>
        /// 上次 <see cref="SyncPending"/> 看到的「任务状态分布」轻量指纹。
        /// <para>
        /// 🚨 2026-10-29 用户实测：2000 个文件时「取完 30 个直链就整个软件卡死」。
        /// 根因是 <c>SyncPending()</c> 在**每一帧进度上报**里都会被调用，而它内部
        /// ① 遍历全部任务拼 2000 条签名字符串 ② 调 <c>HistoryStore.GetPending()</c>
        /// —— 后者是 `Load()`，**每次都读盘 + JSON 反序列化 + DPAPI 解密全部密文字段**。
        /// 30 个并发任务每秒上百帧 → 每秒上百次全量读盘解密 → UI 线程被彻底淹没，
        /// 取链循环（也在 UI 线程）再也推进不了。
        /// </para>
        /// <para>
        /// 进度帧**不可能**改变 pending 的内容（签名只含 Name|State|Dest，
        /// 条数也只随状态迁移变化），所以先用 O(n) 的纯枚举算一个状态分布指纹，
        /// 没变就直接返回 —— 微秒级，且完全跳过读盘。
        /// </para>
        /// </summary>
        private int _pendingLightSig = -1;


        /// <summary>
        /// 「取链抑制」深度：&gt;0 表示正在顺次取链入队，此时抑制 pending 落盘。
        /// <para>
        /// 🚨 2026-10-29 用户实测：2000 个文件点下载 → 每入队 30 个就卡一次。
        /// <see cref="_pendingLightSig"/> 的指纹里含 <c>_tasks.Count</c>（必须含，
        /// 否则加任务/删任务检测不到），而顺次取链每块 30 个正好让 Count 变一次 →
        /// 每块都触发一次 <c>HistoryStore.GetPending()</c> + <c>SetPending()</c>，
        /// 即「读整个 state.json + 反序列化 + 8000 次 DPAPI 解密」再「加密 + 写盘 + fsync」。
        /// 67 块 ≈ 30~40 秒全压在 UI 线程上 → 界面冻结、取链推进不了。
        /// </para>
        /// <para>
        /// 抑制不是「整段不落盘」：每跳过 <see cref="_pendingSkipLimit"/> 次就强制落一次，
        /// 这样即使取链中途崩溃，最多也只丢最后几块（可以重新点下载），不会丢整批。
        /// </para>
        /// </summary>
        private int _pendingSuppress;

        /// <summary>抑制期间已跳过的次数；达到上限后强制落盘一次再清零。</summary>
        private int _pendingSkipped;

        /// <summary>抑制期间最多连续跳过几次（每 10 块 ≈ 300 个文件强制落一次）。</summary>
        private const int _pendingSkipLimit = 10;


        /// <summary>
        /// Job → 任务行的反查索引。
        /// <para>
        /// 🚨 原来 <c>OnJobUpdate</c> 每帧都 <c>_tasks.FirstOrDefault(t =&gt; t.Job == u.Job)</c>，
        /// 那是 O(n) 线性扫描。2000 个任务 + 30 路并发（每秒上百帧进度上报）时
        /// 每秒要做几十万次引用比较，是「任务一多界面就发木」的第二个原因。
        /// <c>DownloadJob</c> 没有重写 Equals/GetHashCode，所以字典就是引用相等，
        /// 与原来的 <c>==</c> 语义完全一致。
        /// </para>
        /// </summary>
        private readonly System.Collections.Generic.Dictionary<DownloadJob, TaskItem> _taskByJob
            = new System.Collections.Generic.Dictionary<DownloadJob, TaskItem>();


        /// <summary>上次刷新「暂停全部/恢复全部」按钮时用的状态签名，避免每帧重建 Content。</summary>
        private string _pauseAllSig;


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

        // 2026-11-02：诊断面板 UI 已删除，_diagWindow 字段随之移除。


        // 2026-11-02：DiagRow 随诊断面板一起删除。



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

            /// <summary>文件名（行模板的主文本）。</summary>
            public string FileName { get; }

            /// <summary>副文本：文件大小。</summary>
            public string SubText { get; }

            /// <summary>右侧状态标记：不可续传时显示 ResumePolicy 给的徽章文字，否则空串。</summary>
            public string BadgeText { get; }

            // ---- 2026-11-05：按日期分组（今天 / 昨天 / 更早）----

            /// <summary>分组标题（界面 GroupStyle 显示这一行）。</summary>
            public string DateGroup { get; }

            /// <summary>分组排序键：今天 0 / 昨天 1 / 同年更早 2 / 跨年或未知 3。</summary>
            public int DateGroupOrder { get; }

            /// <summary>组内排序用（同一组内按创建时刻倒序，新的在上）。</summary>
            public long CreatedAtTicks { get; }

            /// <summary>能否续传。委托给 <see cref="CanResumeValue"/>（ResumePolicy 的结论）。</summary>
            public bool CanResume => CanResumeValue;

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

                // 2026-11-01 重做续传界面：行模板改成三段式，需要拆开的字段。
                FileName = t?.Name ?? "(未知)";
                SubText = (t != null && t.Size > 0) ? Util.FormatSize(t.Size) : "大小未知";

                // 🚨 2026-11-02：徽章只在**真正缺凭证**时才出现。
                //   原来一律写「需重新解析」，但 Size=0（刚下载没写盘就重启）与「解析」
                //   毫无关系 —— 那是下载记录没存下大小，引擎续传时自己会探测真实大小。
                //   把这种情况也标成「需重新解析」会让用户跑去重新解析分享，白费力气。
                //   现在：只有 FromShare 且缺 Token/PwdId/Stoken 才提示需要重新解析。
                // 2026-11-02：判据必须与 ResumePolicy.CanResume **完全一致**，
                //   否则会出现「徽章说需重新解析、复选框却是可勾的」这种自相矛盾的界面。
                //   阿里/迅雷（PanKind 2/3）的分享任务不需要 stoken，
                //   所以它们**不该**出现这个徽章。
                // 🚨 2026-11-03：判据**统一委托给 ResumePolicy.Check**，不再自己写一份。
                //   原来这里自己拼了一套 `providerShare / lackShareCred` 逻辑，
                //   与 ResumePolicy 各算各的 —— 结果就是用户截图里那种
                //   「徽章不显示、但实际根本续传不了」的自相矛盾界面。
                //   判据只许有一份实现，界面只负责展示它给的结论。
                var chk = t != null ? t.Check() : null;
                CanResumeValue = chk == null || chk.Can;
                BadgeText = chk != null && !chk.Can ? (chk.Badge ?? "不可续传") : "";
                // 悬停看完整原因 + 下一步动作（行内放不下长文案）
                TipText = chk != null && !chk.Can
                    ? ((chk.Reason ?? "") + (string.IsNullOrEmpty(chk.NextAction) ? "" : "\n\n" + chk.NextAction))
                    : null;
                Display = FileName + size + (CanResumeValue ? "" : "   （" + (chk?.Badge ?? "不可续传") + "）");

                // 2026-11-05：按「任务创建日期」分组。旧记录 CreatedAtTicks=0 → 归到「更早」，
                // 绝不能 fallback 成今天（否则所有老任务一升级都变成"今天"）。
                DateGroup = t != null ? t.DateGroupLabel() : "更早";
                DateGroupOrder = t != null ? t.DateGroupOrder() : 3;
                CreatedAtTicks = t != null ? t.CreatedAtTicks : 0;
            }

            /// <summary>这条任务到底能不能续传（由 ResumePolicy 判定，界面只展示）。</summary>
            public bool CanResumeValue { get; private set; }

            /// <summary>不可续传时的完整原因 + 下一步（鼠标悬停显示）。</summary>
            public string TipText { get; private set; }

            public event PropertyChangedEventHandler PropertyChanged;

            // ListBoxItem 的**无障碍名**取的是数据项的 ToString()（即使设了
            // DisplayMemberPath，容器元素的名字仍走这条），不重写的话屏幕阅读器/
            // UIA 读到的还是「GeZi.MainWindow+PendingRow」。
            public override string ToString() { return Display; }
        }


        // ---------------- 设置 ----------------

        /// <summary>
        /// 可选的并发档位。**最后一项必须等于 ChunkSplitter.MaxRealConcurrency**，
        /// 否则会出现"界面能选、代码却夹不到"的假承诺。
        /// 注意这个数组的顺序必须与 XAML 里 ComboBoxItem 的顺序严格一致。
        /// 一致性由下面的静态构造函数在启动时自检，改漏了会直接抛异常暴露。
        /// </summary>
        private static readonly int[] ThreadPresets = { 16, 32, 64, 128, 256, 512, 1024, 2048, 3000, 4096 };


        /// <summary>
        /// 「机械盘连接上限」的可选档位。顺序必须与 XAML 里 HddThreadsBox 的
        /// ComboBoxItem 顺序严格一致（与 ThreadPresets 同理）。
        /// <para>
        /// 上限只到 256：机械盘超过这个数寻道就压不住了，再高没有意义。
        /// 下限 32 保留给「特别在意硬盘寿命 / 笔记本机械盘」的用户。
        /// </para>
        /// </summary>
        private static readonly int[] HddThreadPresets = { 32, 64, 96, 128, 192, 256 };


        /// <summary>并发上限的单一事实来源，取自核心层常量。</summary>
        private static int ConcurrencyCap => GeZi.DownloadEngine.Planning.ChunkSplitter.MaxRealConcurrency;


        // ================= 外观主题（2026-10-04 新增） =================

        /// <summary>连续点击时只让最后一次收尾。</summary>
        private int _themeRevealSeq;


        /// <summary>
        /// 当前是否有下载批次在跑。用于阻止"运行中重建 scheduler"这类危险操作。
        /// 2026-10-29 起改成计数器：顺次取链/下载会**同时**跑多个块，
        /// 用 bool 的话先结束的块会把标志清掉，后跑的块就"隐身"了。
        /// </summary>
        private int _batchRunningCount;

        private bool _batchRunning
        {
            get { return System.Threading.Volatile.Read(ref _batchRunningCount) > 0; }
        }


        /// <summary>
        /// 当前扫码流程的取消源。点「获取二维码」或「刷新」都会新建一个并取消上一个 ——
        /// 否则旧轮询还在跑：它一旦过期就会调 HideQrImage() 把**新**二维码收起来。
        /// </summary>
        private CancellationTokenSource _qrFlowCts;


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


        /// <summary>已排队的空状态刷新（把连续多次集合变化合并成一次计算）。</summary>
        private bool _emptyStatePending;
    }
}
