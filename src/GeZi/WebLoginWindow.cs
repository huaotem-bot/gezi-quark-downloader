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
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using GeZi.Core.Api;
using GeZi.Core.Support;

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
        private Button _manualBtn;

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
        public WebLoginWindow(PanKind kind, Action<string> onCaptured, Action<string> log)
        {
            _kind = kind;
            _onCaptured = onCaptured;
            _log = log;

            Title = PanUrlParser.DisplayNameOf(kind) + " 登录";
            Width = 960;
            Height = 760;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = Brushes.White;

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

            // 顶部说明
            var top = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF4)),
                Padding = new Thickness(16, 10, 16, 10),
            };
            var topPanel = new StackPanel();
            topPanel.Children.Add(new TextBlock
            {
                Text = "在下方页面完成登录，成功后会自动填好凭证（无需手动复制 Cookie）。",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x40, 0x3C)),
            });
            _status = new TextBlock
            {
                Text = "正在加载登录页…",
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x78, 0x71, 0x6C)),
                TextWrapping = TextWrapping.Wrap,
            };
            topPanel.Children.Add(_status);
            top.Child = topPanel;
            Grid.SetRow(top, 0);
            root.Children.Add(top);

            // WebView2 容器
            _web = new WebView2 { Visibility = Visibility.Hidden };
            Grid.SetRow(_web, 1);
            root.Children.Add(_web);

            // 底部兜底
            var bottom = new Border
            {
                Padding = new Thickness(16, 10, 16, 12),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE7, 0xE5, 0xE4)),
                BorderThickness = new Thickness(0, 1, 0, 0),
            };
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };

            _manualBtn = new Button
            {
                Content = "手动复制 Cookie…",
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "自动检测没反应时，从浏览器 DevTools 复制整串 Cookie 粘贴进来",
            };
            _manualBtn.Click += (s, e) => ShowManualInput();
            btnRow.Children.Add(_manualBtn);

            var closeBtn = new Button
            {
                Content = "取消",
                Padding = new Thickness(14, 6, 14, 6),
                IsCancel = true,
            };
            closeBtn.Click += (s, e) => Close();
            btnRow.Children.Add(closeBtn);

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
                        : "页面加载失败（" + e2.WebErrorStatus + "），可点「手动复制 Cookie…」。";
                };

                _web.Visibility = Visibility.Visible;
                _web.CoreWebView2.Navigate(LoginUrlFor(_kind));

                StartPolling();
            }
            catch (Exception ex)
            {
                // 运行时缺失 / 创建失败：明确告知，并引导手动通道
                _status.Text = "内嵌登录无法启动：" + ex.Message
                    + "。可点下方「手动复制 Cookie…」继续。";
                _log?.Invoke("[登录] WebView2 启动失败: " + ex.Message);
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
                _status.Text = "等待登录完成…（已读到 " + CountCookies(cookie)
                    + " 个 Cookie，尚未出现 " + RequiredFieldsHint() + "）";
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
                _status.Text = "检测到登录凭证，正在校验…";

                var (ok, nick, error) = await ValidateAsync(cookie);
                if (ok)
                {
                    _finished = true;
                    try { _poll?.Stop(); } catch { }

                    _status.Text = "登录成功：" + nick;
                    _log?.Invoke("[登录] " + PanUrlParser.DisplayNameOf(_kind) + " 登录成功: " + nick);

                    _onCaptured?.Invoke(cookie);
                    Close();
                    return;
                }

                // 校验失败视为中间态，记下来继续等
                _lastFailedCookie = cookie;
                _status.Text = "等待登录完成…（" + error + "）";
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
                _status.Text = "检测到登录令牌，正在校验…";

                var (ok, nick, error) = await ValidateTokenAsync(token);
                if (ok)
                {
                    _finished = true;
                    try { _poll?.Stop(); } catch { }

                    _status.Text = "登录成功：" + nick;
                    _log?.Invoke("[登录] " + PanUrlParser.DisplayNameOf(_kind) + " 登录成功: " + nick);

                    _onCaptured?.Invoke(token);
                    Close();
                    return;
                }

                _lastFailedCookie = token;
                _status.Text = "等待登录完成…（" + error + "）";
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
            Title = "手动粘贴 " + PanUrlParser.DisplayNameOf(kind) + " Cookie";
            Width = 640;
            Height = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            string hint = kind == PanKind.Uc
                ? "需要同时包含 __pus 与 __puus。获取方式：登录 drive.uc.cn → F12 → Application → Cookies → 复制整串。"
                : kind == PanKind.Quark
                    ? "需要同时包含 __pus 与 __puus。获取方式：登录 pan.quark.cn → F12 → Application → Cookies → 复制整串。"
                    : "请粘贴完整的凭证串。";

            var tip = new TextBlock
            {
                Text = hint,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8),
            };
            Grid.SetRow(tip, 0);
            grid.Children.Add(tip);

            var box = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas, monospace"),
                FontSize = 12,
            };
            Grid.SetRow(box, 1);
            grid.Children.Add(box);

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
            };
            var ok = new Button { Content = "确定", Padding = new Thickness(18, 6, 18, 6), IsDefault = true };
            ok.Click += (s, e) =>
            {
                Cookie = box.Text;
                DialogResult = true;
            };
            var cancel = new Button
            {
                Content = "取消",
                Padding = new Thickness(18, 6, 18, 6),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true,
            };
            row.Children.Add(ok);
            row.Children.Add(cancel);
            Grid.SetRow(row, 2);
            grid.Children.Add(row);

            Content = grid;
        }
    }
}