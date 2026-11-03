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
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
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
    /// 网盘内嵌登录窗口：加载官方登录页，用户登录后自动把 Cookie 抓回来。
    /// <para>
    /// <b>为什么是 WebView2：</b>UC/阿里/迅雷都没有可用的扫码登录接口，
    /// 同类项目（YunX、wenxi 的 Windows 版）一律用"内嵌浏览器打开登录页 → 读 Cookie"。
    /// Windows 上能做这件事的只有 WebView2 —— WPF 自带的 WebBrowser 是 IE 内核，
    /// 实测打开 drive.uc.cn 会被跳转到 /u/upgrade「浏览器升级」页。
    /// </para>
    /// <para>
    /// <b>与用户浏览器的隔离：</b>使用独立的用户数据目录，不碰本机 Edge 的配置与会话，
    /// 关闭窗口即清理。用户不会因此"被登录"到自己的浏览器里，也不会被浏览器的登录态干扰。
    /// </para>
    /// <para>
    /// <b>自动检测策略：</b>登录页是 SPA，登录完成没有明确的跳转信号，所以轮询读 Cookie：
    /// 先做廉价预检（关键字段是否齐全），再节流地打一次真实校验接口。
    /// 校验通过才回传，避免把"登录中间态"当成成功。
    /// </para>
    /// </summary>
    internal sealed class WebLoginWindow : Window
    {
        /// <summary>轮询间隔：读 Cookie 很便宜，1.5 秒足够灵敏。</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);

        /// <summary>真实网络校验的最小间隔（登录过程会先出现中间态 Cookie）。</summary>
        private static readonly TimeSpan ValidateThrottle = TimeSpan.FromSeconds(5);

        private readonly PanKind _kind;
        private readonly Action<string> _onCaptured;     // 拿到有效 Cookie 后回调（UI 线程）
        private readonly Action<string> _log;

        private WebView2 _web;
        private TextBlock _status;

        /// <summary>状态圆点（2026-11-03 新增）：颜色随状态变，替代原来的一长串技术说明。</summary>
        private System.Windows.Shapes.Ellipse _statusDot;

        /// <summary>
        /// 打开时是否先清掉内嵌浏览器的登录状态（见构造函数的参数说明）。
        /// <para>true = 登新账号；false = 自动续期（必须保留会话）。</para>
        /// </summary>
        private readonly bool _clearSessionOnStart;
        private Button _manualBtn;
        // 🚨 2026-11-03：「换账号」按钮已从界面删除（用户要求），
        //   _switchBtn 字段随之移除。SwitchAccountAsync 里那几处引用已改成无操作，
        //   避免留下一个永远为 null 的死字段（编译器会报 CS0649）。

        private System.Windows.Threading.DispatcherTimer _poll;
        private DateTime _lastValidateAt = DateTime.MinValue;
        private string _lastFailedCookie = "";
        /// <summary>localStorage 键名诊断是否已经记过（只记一次，避免刷日志）。</summary>
        private bool _diagLogged;

        /// <summary>
        /// 状态栏上一次写过的文本。存储诊断每两秒刷新一次，
        /// 内容没变就不重设 —— 否则文本块会一直重绘、肉眼可见地闪。
        /// </summary>
        private string _lastStatusText = "";
        private bool _finished;
        private bool _validating;
        private string _dataFolder;

        /// <param name="kind">要登录的网盘。</param>
        /// <param name="onCaptured">拿到并验证通过的 Cookie（原始整串）。</param>
        /// <param name="log">日志回调（可为 null）。</param>
        /// <param name="clearSessionOnStart">
        /// 打开时是否先清掉内嵌浏览器记住的登录状态。
        /// <para>
        /// 🚨🚨 2026-11-03 新增，**这是本轮最关键的一个参数**。
        /// </para>
        /// <para>
        /// 背景：内嵌浏览器按网盘固定 profile（profile-aliyun / -xunlei …），
        /// 上一次登录的 Cookie 一直在，页面一打开服务端就把你当老用户，
        /// 直接跳进已登录首页 —— 用户报「点登录账号，几乎瞬间就关掉了，根本没法登新账号」。
        /// </para>
        /// <para>
        /// <b>true</b> = 用户主动要登**新账号**（点「登录账号」/ 账号列表「去登录新账号」/ 注册）→ 先清 Cookie。
        /// </para>
        /// <para>
        /// <b>false</b> = **自动续期**那条路（<c>MainWindow.AliyunRenew</c>）→ 绝对不能清！
        /// 阿里的续期手段正是「重开一次内嵌登录，让 profile 里的老会话自己把页面登好」，
        /// 清了 Cookie 这个机制会 100% 失效，用户会看到「凭证已保存但一直用不了」。
        /// </para>
        /// </param>
        public WebLoginWindow(PanKind kind, Action<string> onCaptured, Action<string> log,
            bool clearSessionOnStart = false)
        {
            _kind = kind;
            _onCaptured = onCaptured;
            _log = log;
            _clearSessionOnStart = clearSessionOnStart;

            Title = PanUrlParser.DisplayNameOf(kind) + " 登录";
            // 与其它弹窗统一：自绘标题栏 + 主题背景 + 主题字体。
            // ⚠️ 这里**不再**自己设 Width/Height/Background —— StyleWindow 会把高度补上标题栏那一截。
            DialogChrome.StyleWindow(this, 960, 760);

            BuildLayout();
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private void BuildLayout()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ---------- 顶部说明 ----------
            // 原来是一整条 #F5F5F4 死灰 + 两行小字，跟主界面完全是两套东西。
            // 现在改成「网盘名大标题 + 一行副标题 + 状态」，与其它弹窗的 PageTitle 一致。
            var top = new Border
            {
                Padding = new Thickness(20, 14, 20, 14),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            top.SetResourceReference(Border.BackgroundProperty, "AppBgBrush");
            top.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");

            // 🚨 2026-11-03 重做顶部（用户报「怎么这么多奇怪的字，普通用户根本不懂」）：
            //   原来三行：标题 / 「在下方页面完成登录，成功后会自动保存凭证。」/ 状态。
            //   现在两行：标题 + 状态，状态前面带一个彩色圆点。
            //   技术细节（Cookie 数量、__pus 等）全部进日志，不进界面。
            var topPanel = new StackPanel();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            var titleText = new TextBlock
            {
                Text = PanUrlParser.DisplayNameOf(_kind) + " 登录",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            titleRow.Children.Add(titleText);
            topPanel.Children.Add(titleRow);

            // 状态行：圆点 + 一句话（圆点颜色随状态变，比长句更直观）
            var statusRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 0),
            };
            _statusDot = new System.Windows.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            _statusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextTertiaryBrush");
            statusRow.Children.Add(_statusDot);

            _status = new TextBlock
            {
                Text = "正在加载…",
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            statusRow.Children.Add(_status);
            topPanel.Children.Add(statusRow);
            top.Child = topPanel;
            Grid.SetRow(top, 0);
            root.Children.Add(top);

            // WebView2 容器
            _web = new WebView2 { Visibility = Visibility.Hidden };
            Grid.SetRow(_web, 1);
            root.Children.Add(_web);

            // ---------- 底部按钮 ----------
            var bottom = new Border
            {
                Padding = new Thickness(20, 12, 20, 14),
                BorderThickness = new Thickness(0, 1, 0, 0),
            };
            bottom.SetResourceReference(Border.BackgroundProperty, "AppBgBrush");
            bottom.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");

            _manualBtn = DialogChrome.SecondaryButton("导入 Cookie");
            _manualBtn.ToolTip = "自动检测没反应时，从浏览器 DevTools 复制整串 Cookie 粘贴进来";
            _manualBtn.Click += (s, e) => ShowManualInput();

            // 🚨 2026-10-26 用户反馈：「点了内嵌登录之后，窗口一出现就自动登录回之前的账号，
            //   这样我永远没法登录阿里的新账号。」
            //   根因：内嵌浏览器用的是**按网盘固定的 profile 目录**（profile-aliyun / -quark …），
            //   上一次登录留下的 Cookie 一直在，页面一打开服务端就把你当老用户，直接跳进已登录页。
            //   修法：给一个「换账号」——只清**内嵌浏览器自己的** Cookie 与本地存储，
            //   软件里已经保存的账号凭证（Settings 里的 Cookie / refresh_token）**一个字都不动**，
            //   所以自动续期的账号不会掉；清完重新打开登录页就是干净的登录界面。
            // 🚨🚨 2026-11-03（用户明确要求）：**删掉「换账号」按钮**。
            //   理由：窗口现在**默认就是新账号模式**（打开时先清 Cookie，见 OnLoaded），
            //   「换账号」已经冗余。用户要换账号只需关掉窗口重新点一次「登录账号」。
            //   ⚠️ SwitchAccountAsync 方法保留 —— 它被「打开时清 Cookie」复用。

            var closeBtn = DialogChrome.SecondaryButton("取消");
            closeBtn.IsCancel = true;
            closeBtn.Click += (s, e) => Close();

            var btnRow = DialogChrome.ButtonRow(_manualBtn, closeBtn);
            btnRow.HorizontalAlignment = HorizontalAlignment.Right;
            bottom.Child = btnRow;
            Grid.SetRow(bottom, 2);
            root.Children.Add(bottom);

            Content = root;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 独立的用户数据目录：不污染本机 Edge 的配置与会话。
                // 按网盘固定（profile-aliyun / -xunlei / -quark / -uc）：
                // 同一家再次登录时能复用上一次的会话，不用每次重新扫码/输密码。
                _dataFolder = WebView2Runtime.CreateIsolatedDataFolder(_kind.ToString());

                var env = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: _dataFolder);

                await _web.EnsureCoreWebView2Async(env);

                var s = _web.CoreWebView2.Settings;
                s.AreDevToolsEnabled = false;
                s.AreDefaultContextMenusEnabled = false;
                s.IsStatusBarEnabled = false;
                s.IsPasswordAutosaveEnabled = false;
                s.IsGeneralAutofillEnabled = false;
                // 保留脚本/存储/第三方 Cookie —— 登录页是 SPA，全靠这些
                s.IsScriptEnabled = true;
                s.AreHostObjectsAllowed = false;

                // 桌面 UA：不伪装会拿到移动版布局，UC 登录页在窄视口下几乎没法操作
                _web.CoreWebView2.Settings.UserAgent =
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                    + "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

                _web.CoreWebView2.NavigationCompleted += (s2, e2) =>
                {
                    _status.Text = e2.IsSuccess
                        ? "请在页面中完成登录。登录成功后本窗口会自动关闭。"
                        : "页面加载失败（" + e2.WebErrorStatus + "），可点「导入 Cookie」。";
                };

                // 🚨🚨 2026-11-03：登新账号时必须先清掉内嵌浏览器记住的登录状态。
                //   否则阿里/迅雷的页面会**瞬间跳进已登录首页**（用户报「点登录账号
                //   几乎瞬间就把页面关掉了」），根本没法输入新账号。
                //   ⚠️ 自动续期那条路传的是 false，**绝不能**清 —— 它正是靠 profile 里
                //      的老会话把页面自动登好来抓新令牌的。
                if (_clearSessionOnStart)
                {
                    SetStatus("正在准备登录…");
                    try
                    {
                        await ClearBrowsingDataAsync(_web.CoreWebView2);
                        _log?.Invoke("[登录] 已清除内嵌浏览器的登录状态（新账号模式）");
                    }
                    catch (Exception ex)
                    {
                        // 清不掉不阻塞：最坏情况是用户看到已登录页，还能点「导入 Cookie」。
                        _log?.Invoke("[登录] 清除登录状态失败（继续打开）：" + ex.Message);
                    }
                }

                _web.Visibility = Visibility.Visible;
                _web.CoreWebView2.Navigate(LoginUrlFor(_kind));

                StartPolling();
            }
            catch (Exception ex)
            {
                // 运行时缺失 / 创建失败：明确告知，并引导手动通道
                _status.Text = "内嵌登录无法启动：" + ex.Message
                    + "。可点下方「导入 Cookie」继续。";
                _log?.Invoke("[登录] WebView2 启动失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 「换账号」：清掉内嵌浏览器记住的登录状态，回到干净的登录页。
        /// <para>
        /// ⚠️ 只动 **WebView2 自己的** Cookie / localStorage / IndexedDB，
        /// 绝不碰软件已保存的凭证（<c>Settings</c> 里的 Cookie、refresh_token）——
        /// 所以「换账号登录」不会把当前正在自动续期的账号踢掉。
        /// </para>
        /// 清完必须**重新导航**：有些站点的登录态存在内存里的 SPA store，光清 Cookie 页面不会变。
        /// </summary>
        /// <summary>
        /// 清掉内嵌浏览器记住的登录状态（Cookie / localStorage / IndexedDB）。
        /// <para>
        /// 🚨 2026-11-03 从 <see cref="SwitchAccountAsync"/> 里抽出来，
        /// 因为「打开时清」和「换账号清」用的是同一套逻辑 —— 复制两份必然漂移。
        /// 这里只清数据、**不导航**，导航由调用方决定。
        /// </para>
        /// </summary>
        private static async Task ClearBrowsingDataAsync(CoreWebView2 core)
        {
            if (core == null) return;

            // 🚨 2026-10-28 的教训（保留说明）：原来的做法是两半：
            //   ① core.CookieManager.DeleteAllCookies()   —— 清整个 profile 的 Cookie，够彻底；
            //   ② core.ExecuteScriptAsync(wipe)          —— 清 localStorage / IndexedDB，
            //      但脚本只跑在**当前页面**所在的 origin 上。
            //   于是阿里这类多子域的站点清不干净：当前页在 www.alipan.com，
            //   passport.alipan.com / auth.alipan.com 下的登录态原封不动，
            //   用户清完刷新一下还是已登录的那个号。
            //
            //   现在改用 Profile.ClearBrowsingDataAsync(kinds)：按**数据类型**清，
            //   一次覆盖 profile 里所有 origin，不再受「当前页面在哪个域」限制。
            //   只清登录态相关的几类，保留 Cache —— 否则重新加载登录页会慢一大截。
            await core.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.Cookies
                | CoreWebView2BrowsingDataKinds.LocalStorage
                | CoreWebView2BrowsingDataKinds.IndexedDb);

            // 再补一发页面内清理：有些 SPA 把状态挂在内存 store 里，
            // 光清存储不清内存，页面自己还以为登着。这发只作为兜底。
            try
            {
                const string wipe =
                    "try{localStorage.clear();}catch(e){}"
                    + "try{sessionStorage.clear();}catch(e){}";
                await core.ExecuteScriptAsync(wipe);
            }
            catch { }

            // 给 Cookie 管理器一点落盘时间
            await Task.Delay(300);
        }


        private async Task SwitchAccountAsync()
        {
            var core = _web != null ? _web.CoreWebView2 : null;
            if (core == null) return;

            _status.Text = "正在清除内嵌浏览器记住的登录状态…";
            try
            {
                // 清理逻辑已抽到 ClearBrowsingDataAsync（打开时与换账号共用同一份）
                await ClearBrowsingDataAsync(core);

                // 重新打开登录页（加时间戳绕开缓存）
                string url = LoginUrlFor(_kind);
                core.Navigate(url + (url.Contains("?") ? "&" : "?") + "_r="
                    + DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture));

                _status.Text = "已清除上次的登录状态，请用另一个账号登录。已保存的账号不受影响。";
                _log?.Invoke("[登录] 已清除内嵌浏览器的登录状态（换账号）");
            }
            catch (Exception ex)
            {
                _status.Text = "清除登录状态失败：" + ex.Message + "，可直接点「导入 Cookie」。";
                _log?.Invoke("[登录] 清除内嵌登录状态失败: " + ex.Message);
            }
            finally
            {
            }
        }

        private static string LoginUrlFor(PanKind kind)
        {
            switch (kind)
            {
                case PanKind.Uc: return UcConstants.LoginUrl;
                // 阿里：必须用 alipan.com 域。2024 年起官网主体迁到这里，
                // 登录令牌（localStorage["token"]）只写在 alipan.com 下；
                // 停在老的 aliyundrive.com/sign/in 上会读不到令牌。
                case PanKind.Aliyun: return "https://www.alipan.com/drive";
                case PanKind.Xunlei: return "https://pan.xunlei.com/";
                default: return "https://pan.quark.cn/";
            }
        }

        private void StartPolling()
        {
            _poll = new System.Windows.Threading.DispatcherTimer { Interval = PollInterval };
            _poll.Tick += async (s, e) => await PollOnceAsync();
            _poll.Start();
        }

        /// <summary>
        /// 阿里 / 迅雷是不是"令牌型"网盘 —— 它们的登录态**不在 Cookie 里**，
        /// 而是网页登录后写进 localStorage 的一个令牌（阿里是 OAuth 的
        /// refresh_token，迅雷是 pan_token，都是 JWT）。所以这两家走
        /// <see cref="PollTokenOnceAsync"/> 那条轮询，不能沿用 Cookie 那套。
        /// </summary>
        private bool IsTokenKind
        {
            get { return _kind == PanKind.Aliyun || _kind == PanKind.Xunlei; }
        }

        private async Task PollOnceAsync()
        {
            if (_finished || _validating || _web.CoreWebView2 == null)
                return;

            if (IsTokenKind)
            {
                await PollTokenOnceAsync();
                return;
            }

            string cookie;
            try
            {
                cookie = await ReadCookieStringAsync();
            }
            catch
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(cookie))
                return;

            // ① 廉价预检：关键字段不齐就是登录中间态，别浪费一次网络请求。
            //    但要**把当前拿到了什么显示出来** —— 否则用户只能看到"一直在等"，
            //    完全不知道是没登录、还是登录了但程序没读到 Cookie。
            if (!LooksPlausible(cookie))
            {
                // 🚨 2026-11-03：界面只留一句人话，技术细节进日志。
                //   原来这里显示「已读到 10 个 Cookie，尚未出现 __pus / __puus」——
                //   普通用户完全不懂，而且 __pus/__puus 是内部字段名，
                //   AGENTS.md 二十·文案规范明确禁止它们出现在界面上。
                SetStatus("等待登录…");
                _log?.Invoke("[登录] 已读到 " + CountCookies(cookie) + " 个 Cookie，尚未出现 "
                    + RequiredFieldsHint() + "（技术细节，仅日志）");
                return;
            }

            // ② 同一份凭证刚校验失败过，等它变化（或超时）再试
            if (cookie == _lastFailedCookie)
                return;

            // ③ 节流：校验走真实网络
            if (DateTime.UtcNow - _lastValidateAt < ValidateThrottle)
                return;

            _lastValidateAt = DateTime.UtcNow;
            _validating = true;
            try
            {
                SetStatus("正在验证…", "TextPrimaryBrush");

                var (ok, nick, error) = await ValidateAsync(cookie);
                if (ok)
                {
                    _finished = true;
                    try { _poll?.Stop(); } catch { }

                    SetStatus("登录成功：" + nick, "SuccessBrush");
                    _log?.Invoke("[登录] " + PanUrlParser.DisplayNameOf(_kind) + " 登录成功: " + nick);

                    _onCaptured?.Invoke(cookie);
                    Close();
                    return;
                }

                // 校验失败视为中间态，记下来继续等
                _lastFailedCookie = cookie;
                SetStatus("等待登录…");
            }
            finally
            {
                _validating = false;
            }
        }

        /// <summary>
        /// 阿里 / 迅雷的轮询：从 localStorage 取令牌 → 真实校验 → 回调。
        /// <para>
        /// 与 Cookie 那条的区别：令牌就一个字符串，没有"字段齐不齐"这一说，
        /// 所以省掉预检，直接节流校验。校验用各家的 <c>ValidateAsync</c>
        /// （阿里打 user/get，迅雷打 drive/v1/about）。
        /// </para>
        /// </summary>
        private async Task PollTokenOnceAsync()
        {
            string token;
            try
            {
                token = await ReadTokenAsync();
            }
            catch
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                // 把存储结构直接摆到状态栏上：用户截一张图就能定位
                // 「令牌到底落在哪个 storage、叫什么键、长什么形状」，
                // 不用再让用户去翻日志文件。
                string diag = await DescribeStorageAsync();
                string text = string.IsNullOrEmpty(diag)
                    ? "等待登录完成…（尚未在网页里发现令牌）"
                    : "等待登录完成…（未发现令牌｜" + diag + "）";
                if (text != _lastStatusText)
                {
                    _lastStatusText = text;
                    _status.Text = text;
                }
                await DumpStorageKeysOnceAsync();
                return;
            }

            if (token == _lastFailedCookie)
                return;

            if (DateTime.UtcNow - _lastValidateAt < ValidateThrottle)
                return;

            _lastValidateAt = DateTime.UtcNow;
            _validating = true;
            try
            {
                SetStatus("正在验证…", "TextPrimaryBrush");

                var (ok, nick, error) = await ValidateTokenAsync(token);
                if (ok)
                {
                    _finished = true;
                    try { _poll?.Stop(); } catch { }

                    SetStatus("登录成功：" + nick, "SuccessBrush");
                    _log?.Invoke("[登录] " + PanUrlParser.DisplayNameOf(_kind) + " 登录成功: " + nick);

                    _onCaptured?.Invoke(token);
                    Close();
                    return;
                }

                _lastFailedCookie = token;
                SetStatus("等待登录…");
            }
            finally
            {
                _validating = false;
            }
        }

        /// <summary>
        /// 从网页的 localStorage 里读登录令牌。
        /// <para>
        /// 两家存储结构不同，各用一段脚本：先按官方那个固定 key 取，
        /// 再按 key 名特征兜底 —— 站点换 key 也不会立刻失效。
        /// 详见 <see cref="AliyunTokenScript"/> 与 <see cref="XunleiTokenScript"/>。
        /// </para>
        /// </summary>
        private async Task<string> ReadTokenAsync()
        {
            if (_web?.CoreWebView2 == null)
                return "";

            string script = _kind == PanKind.Aliyun ? AliyunTokenScript : XunleiTokenScript;

            string raw = await _web.CoreWebView2.ExecuteScriptAsync(script);
            return UnquoteJsString(raw);
        }

        /// <summary>
        /// 读一次 sessionStorage / localStorage 的「键名 = 值结构」，拼成一行短文本。
        /// <para>
        /// 只输出结构（JSON 给字段名、数组给长度、裸字符串给长度），
        /// 任何令牌值本身都不会出现在返回值里。
        /// 状态栏与日志共用这一份结果，避免两处 JS 各写一遍。
        /// </para>
        /// </summary>
        private async Task<string> DescribeStorageAsync()
        {
            if (_web?.CoreWebView2 == null)
                return "";
            try
            {
                // 输出「键名=值结构」而不是键名列表：只看键名无法区分
                // 「值是 JSON 对象 / 是 JSON 数组 / 是裸字符串」，而阿里新版
                // 恰好把 token 包了一层，光看键名会误判成"键不存在"。
                string js = "(function(){try{"
                          + "function shape(v){"
                          + "  if(!v) return 'empty';"
                          + "  try{"
                          + "    var o=JSON.parse(v);"
                          + "    if(Array.isArray(o)) return 'array['+o.length+']';"
                          + "    if(o&&typeof o==='object') return 'obj{'+Object.keys(o).join('|')+'}';"
                          + "    return typeof o;"
                          + "  }catch(e){ return 'raw'+v.length; }"
                          + "}"
                          + "function dump(store,label){"
                          + "  var out=[];"
                          + "  try{"
                          + "    for(var i=0;i<store.length;i++){"
                          + "      var k=store.key(i)||'';"
                          + "      out.push(k+'='+shape(store.getItem(k)||''));"
                          + "    }"
                          + "  }catch(e){ out.push('<'+e.message+'>'); }"
                          + "  return out.length? label+'{'+out.join('; ')+'}' : label+'{}';"
                          + "}"
                          + "return dump(sessionStorage,'S')+' '+dump(localStorage,'L');"
                          + "}catch(e){return '';}})()";
                string raw = await _web.CoreWebView2.ExecuteScriptAsync(js);
                string keys = UnquoteJsString(raw);
                if (string.IsNullOrWhiteSpace(keys))
                    return "";
                keys = keys.Replace("\n", " ").Replace("\r", " ");
                // 状态栏高度有限，超长就截断：键名通常够定位，值结构后半段可省。
                return keys.Length > 200 ? keys.Substring(0, 200) + "…" : keys;
            }
            catch
            {
                // 诊断失败不影响主流程
                return "";
            }
        }

        /// <summary>
        /// 读不到令牌时把存储结构记一次日志（只记一次：轮询每两秒一次，否则刷屏）。
        /// 站点改版换 key 名时，这一行就能直接指出新 key，不用再猜。
        /// </summary>
        private async Task DumpStorageKeysOnceAsync()
        {
            if (_diagLogged)
                return;
            _diagLogged = true;
            try
            {
                string keys = await DescribeStorageAsync();
                if (!string.IsNullOrWhiteSpace(keys))
                {
                    _log?.Invoke("[登录] " + PanUrlParser.DisplayNameOf(_kind)
                        + " 未找到令牌；storage：" + keys);
                }
            }
            catch
            {
                // 诊断失败不影响主流程
            }
        }

        /// <summary>
        /// 阿里云盘：令牌是 <c>token</c> 这个键里的一个 JSON 对象，
        /// 含 <c>access_token</c>，**可能**含 <c>refresh_token</c>。
        /// <para>
        /// <b>关键（实测）：</b>同一个 <c>token</c> 键在 sessionStorage 与 localStorage
        /// 里可能是**两份不同的东西** —— 阿里新版首页会在 sessionStorage 放一份精简的
        /// （只有 access_token），localStorage 里另有一份完整的。早先那版"先
        /// sessionStorage、有就返回"，于是永远拿到精简版，只认 refresh_token 就永远读空，
        /// 表现为"登录成功但一直提示没发现令牌"。
        /// 现在两个 storage 都扫，**优先返回带 refresh_token 的那份**。
        /// </para>
        /// <para>
        /// 参照 refs/wenxi 的 <c>lib/domain/auth.dart:949</c>：它对阿里只有
        /// <c>localStorage.getItem('token')</c> 一句，配合
        /// <c>WebTokens.fields</c> 硬性要求 refresh_token 非空 —— 说明它依赖的正是
        /// localStorage 那份完整令牌。
        /// </para>
        /// <para>
        /// 返回格式：<c>rt:&lt;refresh_token&gt;</c>（可续期，最优）或
        /// <c>at:&lt;access_token&gt;</c>（只有短期令牌，约两小时）。调用方按前缀分派。
        /// </para>
        /// </summary>
        private const string AliyunTokenScript =
            @"(function(){
                try{
                  function store(name){
                    try{ return window[name]; }catch(e){ return null; }
                  }
                  function get(name, key){
                    var s=store(name);
                    if(!s) return null;
                    try{ return s.getItem(key); }catch(e){ return null; }
                  }
                  function keysOf(name){
                    var out=[];
                    var s=store(name);
                    if(!s) return out;
                    try{ for(var i=0;i<s.length;i++) out.push(s.key(i)); }catch(e){}
                    return out;
                  }
                  function parse(v){
                    if(!v) return null;
                    try{ return JSON.parse(v); }catch(e){ return null; }
                  }
                  // 递归找含 access_token / refresh_token 的那一层。
                  // 阿里把令牌包在对象里，有时还套一层字符串，所以要看穿几层。
                  function fields(o, depth){
                    if(!o || depth>4) return null;
                    if(typeof o==='string'){ return fields(parse(o), depth+1); }
                    if(Array.isArray(o)){
                      for(var i=0;i<o.length;i++){ var r=fields(o[i],depth+1); if(r) return r; }
                      return null;
                    }
                    if(typeof o!=='object') return null;
                    var a=o.access_token||o.accessToken||'';
                    var r=o.refresh_token||o.refreshToken||'';
                    if(typeof a==='string' && typeof r==='string' && (a||r)) return {a:a,r:r};
                    var nest=o.credentials||o.token||o.data||o.tokenInfo;
                    if(nest) return fields(nest, depth+1);
                    return null;
                  }
                  function good(t, min){
                    if(!t||typeof t!=='string') return '';
                    t=t.trim();
                    if(t.length<min) return '';
                    if(t==='null'||t==='undefined') return '';
                    return t;
                  }
                  var STORES=['sessionStorage','localStorage'];
                  var KEYS=['token','aliyundrive_token','aliyun_token','token_v2','openToken'];
                  var bestAccess='';
                  // 第一轮：已知键名，两个 storage 都看
                  for(var si=0; si<STORES.length; si++){
                    for(var ki=0; ki<KEYS.length; ki++){
                      var f=fields(parse(get(STORES[si],KEYS[ki])),0);
                      if(!f) continue;
                      var r=good(f.r,60);
                      if(r) return 'rt:'+r;                 // 有 refresh_token 立刻收工
                      var a=good(f.a,60);
                      if(a && !bestAccess) bestAccess=a;    // 先记下，继续找 refresh_token
                    }
                  }
                  // 第二轮：键名里带 token 的都扫一遍
                  for(var sj=0; sj<STORES.length; sj++){
                    var ks=keysOf(STORES[sj]);
                    for(var j=0;j<ks.length;j++){
                      var k=ks[j]||'';
                      if(k.toLowerCase().indexOf('token')<0) continue;
                      var f2=fields(parse(get(STORES[sj],k)),0);
                      if(!f2) continue;
                      var r2=good(f2.r,60);
                      if(r2) return 'rt:'+r2;
                      var a2=good(f2.a,60);
                      if(a2 && !bestAccess) bestAccess=a2;
                    }
                  }
                  return bestAccess ? ('at:'+bestAccess) : '';
                }catch(e){ return ''; }
              })()";
        /// <summary>
        /// 迅雷云盘：令牌在 <c>localStorage["credentials_Xqp0kJBXWhwaTpB6"]</c>
        /// （多账号时带 <c>@&lt;sub&gt;</c> 后缀，<c>sub</c> 存在
        /// <c>current_sub</c> 里），值是 JSON，含
        /// <c>access_token</c> / <c>refresh_token</c> / <c>sub</c>。
        /// <para>
        /// 要的是 <b>access_token</b>：<c>XunleiClient</c> 用它做
        /// <c>Authorization: Bearer …</c> 调 <c>/drive/v1/about</c>。
        /// 早先那版按 <c>eyJ</c>（JWT 前缀）找 token 是错的 —— 迅雷这个
        /// access_token 不是 JWT，于是脚本只能捞到页面里别的 JWT 字符串，
        /// 校验必然失败，窗口就卡在"pan_token 无效或已过期"。
        /// </para>
        /// </summary>
        private const string XunleiTokenScript =
            @"(function(){
                try{
                  var CID='Xqp0kJBXWhwaTpB6';
                  function get(store,key){
                    try{ return store.getItem(key); }catch(e){ return null; }
                  }
                  // 迅雷把登录态写在 localStorage；sessionStorage 只在个别
                  // 版本里出现，所以 local 优先、session 兜底。
                  function raw(k){
                    var v=get(localStorage,k);
                    if(v) return v;
                    return get(sessionStorage,k)||null;
                  }
                  function allKeys(){
                    var out=[];
                    try{ for(var i=0;i<localStorage.length;i++) out.push(localStorage.key(i)); }catch(x){}
                    try{ for(var j=0;j<sessionStorage.length;j++) out.push(sessionStorage.key(j)); }catch(x){}
                    return out;
                  }
                  function parseObj(v){
                    if(!v||typeof v!=='string') return null;
                    try{
                      var o=JSON.parse(v);
                      return (o&&typeof o==='object')?o:null;
                    }catch(e){ return null; }
                  }
                  function hasToken(o){
                    return !!(o&&typeof o==='object'
                      &&(o.access_token||o.accessToken||o.token));
                  }
                  // 值可能被包成字符串 / 数组 / 对象若干层，逐层解到有令牌的那层。
                  function deep(o,d){
                    if(!o||d>4) return null;
                    if(typeof o==='string'){
                      var p=parseObj(o);
                      return p?deep(p,d+1):null;
                    }
                    if(Array.isArray(o)){
                      for(var i=0;i<o.length;i++){
                        var r=deep(o[i],d+1);
                        if(hasToken(r)) return r;
                      }
                      return null;
                    }
                    if(typeof o!=='object') return null;
                    if(hasToken(o)) return o;
                    var names=['credentials','token','data','auth','value'];
                    for(var k=0;k<names.length;k++){
                      if(o[names[k]]){
                        var r2=deep(o[names[k]],d+1);
                        if(hasToken(r2)) return r2;
                      }
                    }
                    return null;
                  }
                  function str(o,names){
                    for(var i=0;i<names.length;i++){
                      var v=o[names[i]];
                      if(typeof v==='string'&&v.trim()) return v.trim();
                    }
                    return '';
                  }
                  function good(t){
                    if(!t||typeof t!=='string') return '';
                    t=t.trim();
                    if(t.length<16) return '';
                    if(t==='null'||t==='undefined') return '';
                    return t;
                  }
                  var sub=raw('current_sub')||'';
                  var cands=[];
                  if(sub) cands.push('credentials_'+CID+'@'+sub);
                  cands.push('credentials_'+CID);
                  var o=null;
                  for(var i=0;i<cands.length;i++){
                    var p=deep(parseObj(raw(cands[i])),0);
                    if(hasToken(p)){ o=p; break; }
                  }
                  if(!o){
                    var ks=allKeys();
                    for(var j=0;j<ks.length;j++){
                      var k=(''+ks[j]).toLowerCase();
                      if(k.indexOf('credential')<0&&k.indexOf('xunlei')<0) continue;
                      var p2=deep(parseObj(raw(ks[j])),0);
                      if(hasToken(p2)){ o=p2; break; }
                    }
                  }
                  if(!o) return '';
                  var at=good(str(o,['access_token','accessToken','token']));
                  if(!at) return '';
                  var rt=good(str(o,['refresh_token','refreshToken']));
                  var sb=str(o,['sub'])||sub;
                  // captcha_<CID> 里存的是 pan 接口要的 X-Captcha-Token，
                  // 过期了就不能再带上，否则反而被判无效。
                  var cap='';
                  try{
                    var c=parseObj(raw('captcha_'+CID));
                    if(c&&c.token){
                      var exp=c.expires_at?Date.parse(c.expires_at):0;
                      if(!exp||exp>Date.now()) cap=good(''+c.token);
                    }
                  }catch(e){}
                  // deviceid 必须用网页自己那份：网页把它存在
                  // localStorage 的 'deviceid' key 里，形如 wdi10.<64 位 hex>。
                  // 实测：cookie 里那份 div101.<32 位>截断后上传会被
                  // 判 captcha_invalid / device not match。
                  var dev='';
                  try{
                    var dv=raw('deviceid');
                    if(dv) dev=good(''+dv);
                  }catch(e){}
                  if(!dev){
                    try{
                      var cs=(''+document.cookie).split(';');
                      for(var m=0;m<cs.length;m++){
                        var d=cs[m].replace(/^\s+/,'');
                        if(d.indexOf('deviceid=')===0){ dev=good(d.substring(9)); break; }
                      }
                    }catch(e){}
                  }
                  return 'xlrec:'+[at,rt,sb,dev,cap].join('|');
                }catch(e){ return ''; }
              })()";
        /// <summary>
        /// <c>ExecuteScriptAsync</c> 返回的是**JSON 编码过的**脚本结果
        /// （字符串结果会带一层引号并转义），这里去掉那层壳。
        /// <para>
        /// 不用 <c>PanJson</c>：它在 GeZi.Core 里是 <c>internal</c>，
        /// 跨程序集访问会 CS0122。这里反转义规则很少，自己实现更省事。
        /// </para>
        /// </summary>
        private static string UnquoteJsString(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "";
            string s = raw.Trim();
            if (s.Length < 2 || s[0] != '"' || s[s.Length - 1] != '"')
                return s;

            // 去掉首尾引号后按 JSON 转义规则还原
            var sb = new StringBuilder(s.Length);
            int end = s.Length - 1;
            for (int i = 1; i < end; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= end)
                {
                    sb.Append(c);
                    continue;
                }

                char n = s[++i];
                switch (n)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        // \uXXXX —— 4 位十六进制
                        if (i + 4 < end)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i + 1, 4),
                                    System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                                break;
                            }
                        }
                        sb.Append('u');
                        break;
                    default:
                        // 未知转义：原样保留，别吞字符
                        sb.Append('\\').Append(n);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>校验阿里 / 迅雷的令牌（真实网络请求）。</summary>
        private async Task<(bool ok, string nick, string error)> ValidateTokenAsync(string token)
        {
            try
            {
                if (_kind == PanKind.Aliyun)
                {
                    // 脚本会带回 rt: / at: 前缀：前者是能续期的 refresh_token，
                    // 后者是网页只给了短期 access_token（阿里新版常见）。
                    bool shortLived = token.StartsWith("at:", StringComparison.Ordinal);
                    string value = shortLived || token.StartsWith("rt:", StringComparison.Ordinal)
                        ? token.Substring(3)
                        : token;   // 手动粘贴等旧路径没有前缀，按 refresh_token 处理
                    using (var a = shortLived
                        ? new AliyunClient(null, null, null, value)
                        : new AliyunClient(value))
                    {
                        var (ok, nick) = await a.ValidateAsync().ConfigureAwait(true);
                        return (ok, ok ? nick : "",
                            ok ? "" : (shortLived ? "access_token 无效或已过期" : "refresh_token 无效或已过期"));
                    }
                }

                // 网页登录返回的是打包记录（xlrec:at|rt|sub|dev|cap），
                // 拆出 access_token / device_id / captcha_token 再校验；
                // 手动粘贴的裸 token 不是记录，按原样用。
                string at = token;
                string cap = null;
                string rt, sb, dev;
                if (XunleiProtocol.TryParseRecord(token, out at, out rt, out sb, out dev, out cap))
                {
                    // 记录里的 refresh_token / sub 由上层负责保存，
                    // 但 device_id 必须现在就传给客户端 —— 迅雷把设备标识和登录态绑定，
                    // 用随机 device_id 配网页 token 会被判 captcha_required（400）。
                }
                using (var x = new XunleiClient(at, string.IsNullOrEmpty(cap) ? null : cap,
                           null, string.IsNullOrEmpty(dev) ? null : dev))
                {
                    var (ok, nick) = await x.ValidateAsync().ConfigureAwait(true);
                    if (ok)
                        return (true, nick, "");
                    string why = string.IsNullOrEmpty(x.LastError)
                        ? "pan_token 无效或已过期"
                        : "pan_token 无效：" + x.LastError;
                    return (false, "", why);
                }
            }
            catch (Exception ex)
            {
                return (false, "", ex.Message);
            }
        }

        /// <summary>
        /// 从当前 WebView 实例读 Cookie（原生 API，不走外部浏览器、不碰加密数据库）。
        /// <para>
        /// <b>按域名分别读取再合并</b>，而不是只传空串取全部：
        /// 实测空串在某些 WebView2 版本上可能拿不到全部 Cookie（尤其 HttpOnly 的登录态），
        /// 而 <c>__puus</c> 恰好可能是 HttpOnly —— 漏掉它就会表现为
        /// 「明明登录成功，程序却一直说 Cookie 不完整」。
        /// </para>
        /// </summary>
        private async Task<string> ReadCookieStringAsync()
        {
            // 登录态可能分散在这几个域下，逐个读再合并（参考同类项目 wenxi 的做法）
            string[] urls = CookieUrlsFor(_kind);

            var merged = new List<KeyValuePair<string, string>>();
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var url in urls)
            {
                IReadOnlyList<CoreWebView2Cookie> list = null;
                try
                {
                    list = await _web.CoreWebView2.CookieManager.GetCookiesAsync(url);
                }
                catch
                {
                    continue;
                }
                if (list == null)
                    continue;

                foreach (var c in list)
                {
                    if (c == null || string.IsNullOrEmpty(c.Name))
                        continue;
                    string value = c.Value ?? "";
                    // 后读到的覆盖先读到的（同域名优先级更高，按 urls 顺序由泛到具体）
                    int pos;
                    if (index.TryGetValue(c.Name, out pos))
                        merged[pos] = new KeyValuePair<string, string>(c.Name, value);
                    else
                    {
                        index[c.Name] = merged.Count;
                        merged.Add(new KeyValuePair<string, string>(c.Name, value));
                    }
                }
            }

            // 兜底：万一上面按域名一个都没读到，再试一次"取全部"
            if (merged.Count == 0)
            {
                try
                {
                    var all = await _web.CoreWebView2.CookieManager.GetCookiesAsync("");
                    if (all != null)
                    {
                        foreach (var c in all)
                        {
                            if (c == null || string.IsNullOrEmpty(c.Name))
                                continue;
                            if (index.ContainsKey(c.Name))
                                continue;
                            index[c.Name] = merged.Count;
                            merged.Add(new KeyValuePair<string, string>(c.Name, c.Value ?? ""));
                        }
                    }
                }
                catch { }
            }

            if (merged.Count == 0)
                return "";

            var sb = new StringBuilder();
            foreach (var kv in merged)
            {
                if (sb.Length > 0)
                    sb.Append("; ");
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }

        /// <summary>该网盘的 Cookie 所属域名（由泛到具体，后面的覆盖前面的）。</summary>
        private static string[] CookieUrlsFor(PanKind kind)
        {
            switch (kind)
            {
                case PanKind.Uc:
                    return new[]
                    {
                        "https://uc.cn",
                        "https://drive.uc.cn",
                        "https://pc-api.uc.cn",
                        "https://fast.uc.cn",
                    };
                case PanKind.Aliyun:
                    return new[]
                    {
                        "https://aliyundrive.com",
                        "https://www.aliyundrive.com",
                        "https://alipan.com",
                        "https://www.alipan.com",
                        "https://api.aliyundrive.com",
                    };
                case PanKind.Xunlei:
                    return new[]
                    {
                        "https://xunlei.com",
                        "https://pan.xunlei.com",
                        "https://api-pan.xunlei.com",
                    };
                default:
                    return new[] { "https://pan.quark.cn" };
            }
        }

        private bool LooksPlausible(string cookie)
        {
            switch (_kind)
            {
                case PanKind.Uc:
                    // 与 wenxi 一致：__pus 与 __puus 都存在即可（不做网络校验）
                    return UcDriveClient.LooksLikeCompleteUcCookie(cookie);
                case PanKind.Quark:
                    // 夸克与 UC 同源（ucpro 后端），登录态同样是 __pus + __puus。
                    // 少了 __puus 就只是游客态，真实校验必然失败 —— 先在这里挡掉，
                    // 免得每 1.5 秒白跑一次网络校验。
                    return cookie.IndexOf(UcConstants.CookiePus, StringComparison.Ordinal) >= 0
                        && cookie.IndexOf(QuarkConstants.LoginCookieKey, StringComparison.Ordinal) >= 0;
                default:
                    // 其它网盘暂时只要求非空，交给真实校验判定
                    return cookie.Length > 20;
            }
        }

        /// <summary>统计 Cookie 串里有几个键值对（只用于状态提示）。</summary>
        private static int CountCookies(string cookie)
        {
            if (string.IsNullOrEmpty(cookie))
                return 0;
            int n = 0;
            foreach (var part in cookie.Split(';'))
            {
                if (part.IndexOf('=') > 0)
                    n++;
            }
            return n;
        }

        /// <summary>提示当前网盘需要哪几个关键 Cookie。</summary>
        /// <summary>
        /// 统一设置状态：文字 + 圆点颜色。
        /// <para>
        /// 🚨 2026-11-03：原来各处直接赋 <c>_status.Text</c>，圆点不会跟着变。
        /// 抽成一个方法后，「等待 / 验证 / 成功 / 失败」四种状态颜色自动对应。
        /// </para>
        /// </summary>
        /// <param name="text">给人看的一句话。</param>
        /// <param name="brushKey">圆点颜色的资源键；null = 默认三级色。</param>
        private void SetStatus(string text, string brushKey = null)
        {
            try
            {
                if (_status != null) _status.Text = text ?? "";
                if (_statusDot != null)
                {
                    _statusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
                        string.IsNullOrEmpty(brushKey) ? "TextTertiaryBrush" : brushKey);
                }
            }
            catch { }
        }


        private string RequiredFieldsHint()
        {
            switch (_kind)
            {
                case PanKind.Uc:
                    return UcConstants.CookiePus + " / " + UcConstants.CookiePuus;
                case PanKind.Quark:
                    return UcConstants.CookiePus + " / " + QuarkConstants.LoginCookieKey;
                default:
                    return "登录凭证";
            }
        }

        private async Task<(bool ok, string nick, string error)> ValidateAsync(string cookie)
        {
            switch (_kind)
            {
                case PanKind.Uc:
                    using (var uc = new UcDriveClient(cookie))
                        return await uc.ValidateCookieAsync(cookie).ConfigureAwait(true);
                case PanKind.Quark:
                    // 夸克走 QuarkClient.ValidateAsync：它先试 account/info（.NET 连不上
                    // pan.quark.cn，会被 TLS 指纹 RST），失败后自动回退到 ListDirAsync，
                    // 后者走 drive-pc.quark.cn —— 那个域 .NET 可达，所以校验结论是可信的。
                    using (var q = new QuarkClient(cookie))
                    {
                        var (qok, qnick) = await q.ValidateAsync().ConfigureAwait(true);
                        return (qok, qok ? qnick : "", qok ? "" : "Cookie 校验未通过（可能未登录或已过期）");
                    }
                default:
                    // 其它网盘：先按"拿到 Cookie"接受（阿里/迅雷用的是 token 不是 Cookie，
                    // 后续单独实现），这里保持行为明确而不是假装成功。
                    return (false, "", "该网盘的内嵌登录尚未实现，请用手动粘贴。");
            }
        }

        private void ShowManualInput()
        {
            var dlg = new ManualCookieDialog(this, _kind);
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Cookie))
            {
                _finished = true;
                try { _poll?.Stop(); } catch { }
                _onCaptured?.Invoke(dlg.Cookie.Trim());
                Close();
            }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            try { _poll?.Stop(); } catch { }

            try
            {
                if (_web != null)
                {
                    _web.Dispose();
                    _web = null;
                }
            }
            catch { }

            // 不删数据目录：这里是按网盘固定的 profile，删掉就等于把登录态一起扔了。
            // 目录本身在 %LOCALAPPDATA%\GeZi\webview2 下，属于用户配置，应当保留。
            _dataFolder = null;
        }
    }

    /// <summary>手动粘贴 Cookie 的兜底对话框。</summary>
    internal sealed class ManualCookieDialog : Window
    {
        public string Cookie { get; private set; }

        public ManualCookieDialog(Window owner, PanKind kind)
        {
            Owner = owner;
            Title = "导入 " + PanUrlParser.DisplayNameOf(kind) + " Cookie";
            // 与其它弹窗统一外壳（自绘标题栏 + 主题背景 + 主题字体）
            DialogChrome.StyleWindow(this, 640, 340);

            var root = DialogChrome.DialogRoot();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ⚠️ 这里**不再**画 <c>PageTitle</c>：标题栏（BuildCaption）已经把
            //    「导入 UC网盘 Cookie」画在顶部条里了，内容区再画一遍就是上下两个标题。
            //    DialogChrome 的约定是「标题只在标题栏里出现一次」。

            string hint = kind == PanKind.Uc
                ? "需要同时包含 __pus 与 __puus。获取方式：登录 drive.uc.cn → F12 → Application → Cookies → 复制整串。"
                : kind == PanKind.Quark
                    ? "需要同时包含 __pus 与 __puus。获取方式：登录 pan.quark.cn → F12 → Application → Cookies → 复制整串。"
                    : "请粘贴完整的凭证串。";

            var body = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var tip = DialogChrome.Hint(hint, 0, 8);
            Grid.SetRow(tip, 0);
            body.Children.Add(tip);

            // 🚨 2026-10-26 用户反馈（附截图）：「这个输入框太丑太僵硬」。
            //   根因：这个 TextBox 原来**没有套任何样式**，走的是 WPF 默认外观 ——
            //   直角、1px 浅灰边、内边距几乎没有，和周围圆角卡片完全不搭。
            //   → 套上全项目通用的 AppTextBox（圆角 10 + 主题描边 + 聚焦高亮），
            //     再按多行输入的需要覆盖三处：滚动条改 Auto（默认是 Disabled，长 Cookie 会看不到末尾）、
            //     内容贴顶（默认居中，多行会飘在中间）、内边距放大到 12,10。
            var box = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas, monospace"),
                FontSize = 12,
                Padding = new Thickness(12, 10, 12, 10),
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            var tbStyle = TryFindResource("AppTextBox") as Style;
            if (tbStyle != null) box.Style = tbStyle;
            // 样式里的单行约定（Disabled）在样式应用之后覆盖，顺序不能反。
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            box.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Grid.SetRow(box, 1);
            body.Children.Add(box);
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            var ok = DialogChrome.PrimaryButton("确定");
            ok.IsDefault = true;
            ok.MinWidth = 84;
            ok.Click += (s, e) =>
            {
                Cookie = box.Text;
                DialogResult = true;
            };
            var cancel = DialogChrome.SecondaryButton("取消");
            cancel.IsCancel = true;
            cancel.MinWidth = 84;

            var row = DialogChrome.ButtonRow(ok, cancel);
            row.HorizontalAlignment = HorizontalAlignment.Right;
            row.Margin = new Thickness(0, 12, 0, 0);
            Grid.SetRow(row, 2);
            root.Children.Add(row);

            Content = root;
        }
    }
}