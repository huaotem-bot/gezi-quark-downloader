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

// ---- 本文件是 MainWindow 的 partial 之一（代码规范化拆分，2026-10-24） ----
// 成员按功能归组，全部成员声明在同一 partial 类里，作用域与拆分前完全一致。
// 职责：二维码扫码登录、网页登录、验证码

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
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using SelectionMode = System.Windows.Controls.SelectionMode;
using MessageBox = System.Windows.MessageBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Run = System.Windows.Documents.Run;
using TextTrimming = System.Windows.TextTrimming;
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
                            "识别出的内容是一条夸克口令：\n\n" + text + "\n\n"
                            + "它不是分享链接，本工具无法直接解析。\n"
                            + "请到夸克 App 里打开它，进入分享页后点「分享」→「复制链接」，"
                            + "再把链接粘进来。",
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
        /// 给迅雷 provider 挂上「网页取验证码」的钩子。
        /// <para>
        /// 迅雷的 captcha 盾按 client_id 分体系：网页签发的 pan_token 必须配
        /// 网页签发的 captcha，用 App 体系申请的会被判 client_id not match。
        /// 钩子由 <see cref="XunleiCaptchaFetcher"/> 实现（开一个隐藏 WebView2
        /// 让网页自己生成，再读 localStorage）。
        /// </para>
        /// <para>非迅雷 provider 原样返回，调用方不必判断类型。</para>
        /// </summary>
        private static IPanProvider AttachXunleiCaptcha(IPanProvider provider)
        {
            var xunlei = provider as XunleiClient;
            if (xunlei != null)
                xunlei.CaptchaRefresher = XunleiCaptchaFetcher.FetchAsync;
            return provider;
        }


        // ---------------- 多网盘内嵌登录（WebView2） ----------------

        /// <summary>
        /// UC 内嵌登录：开 WebView2 窗口加载官方登录页，登录成功后自动把 Cookie 填回输入框。
        /// <para>
        /// 三层降级的第一层就在这里：<see cref="WebView2Runtime.IsInstalled"/> 为 false 时
        /// 按钮会被禁用（见 <see cref="RefreshUcRuntimeStatus"/>），但手动粘贴框始终可用。
        /// </para>
        /// </summary>
        /// <summary>
        /// 「缺少 WebView2 运行时」的统一提示。
        /// 原来三处各写一份几乎一样的文案（UC / 夸克 / 阿里迅雷），只有中间一句不同；
        /// 收成一处，改文案不用再改三遍。
        /// </summary>
        private void ShowWebView2Missing(string fallbackHint)
        {
            AppDialog.Show(this,
                "缺少 WebView2 运行时，内嵌登录用不了。\n\n"
                + "不影响使用 —— " + fallbackHint + "\n\n"
                + "想用内嵌登录的话，装一下 WebView2 运行时即可（约 2 MB）：\n"
                + WebView2Runtime.ConsumerDownloadUrl,
                "缺少 WebView2 运行时", MessageBoxButton.OK, MessageBoxImage.Information);
        }


        private void OnUcWebLogin(object sender, RoutedEventArgs e)
        {
            if (!WebView2Runtime.IsInstalled)
            {
                ShowWebView2Missing("仍然可以在下方输入框里手动粘贴 Cookie。");
                return;
            }

            try
            {
                var win = new WebLoginWindow(PanKind.Uc,
                    cookie =>
                    {
                        // 拿到的 Cookie 直接回填并保存，用户不用再操作
                        if (UcCookieBox != null)
                            UcCookieBox.Text = cookie;
                        ApplySettings();
                        Log("UC网盘 内嵌登录成功，凭证已保存。");
                        RefreshUcRuntimeStatus();
                    },
                    msg => LogDetail(msg))
                {
                    Owner = this,
                };
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                Log("打开内嵌登录失败: " + ex.Message);
            }
        }


        /// <summary>
        /// 夸克内嵌登录：与 UC 同一套 WebView2 方案。
        /// <para>
        /// 为什么夸克也需要它：扫码登录依赖**手机端**完成确认 —— 手机夸克 App 必须在
        /// 登录态、且 H5 桥（account.getUserInfo）可用，否则 App 侧会直接显示
        /// 「登录请求已过期」（实测该文案的触发条件很宽：只要
        /// loginWithKpsAndQrcodeToken 返回非 2000000 就显示，见 b.quark.cn 的
        /// loginconfirm bundle）。这不是二维码本身的问题，本机无从修复，只能给一条
        /// 不依赖手机的兜底路径 —— 内嵌浏览器登录后直接读 Cookie，与 YunX / wenxi
        /// 的做法一致，成功率不受 App 环境影响。
        /// </para>
        /// </summary>
        private void OnQuarkWebLogin(object sender, RoutedEventArgs e)
        {
            if (!WebView2Runtime.IsInstalled)
            {
                ShowWebView2Missing("仍然可以用上面的二维码登录。");
                return;
            }

            try
            {
                var win = new WebLoginWindow(PanKind.Quark,
                    cookie =>
                    {
                        // 复用扫码登录同一条落地路径：校验 → 保存 → 启动保活 → 存档
                        _ = ApplyLoginAsync(cookie, PanKind.Quark);
                        Log("夸克网盘 内嵌登录成功，凭证已保存。");
                    },
                    msg => LogDetail(msg))
                {
                    Owner = this,
                };
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                Log("打开内嵌登录失败: " + ex.Message);
            }
        }


        /// <summary>阿里云盘内嵌登录：登录后自动从 localStorage 取 refresh_token。</summary>
        private void OnAliyunWebLogin(object sender, RoutedEventArgs e)
        {
            OpenProviderWebLogin(PanKind.Aliyun);
        }


        /// <summary>迅雷云盘内嵌登录：登录后自动从 localStorage 取 pan_token。</summary>
        private void OnXunleiWebLogin(object sender, RoutedEventArgs e)
        {
            OpenProviderWebLogin(PanKind.Xunlei);
        }


        /// <summary>
        /// 阿里 / 迅雷共用的内嵌登录入口。
        /// <para>
        /// 与 UC 那家的区别：这两家的登录态是 localStorage 里的令牌，
        /// <see cref="WebLoginWindow"/> 内部已经按 kind 切成"读令牌"模式，
        /// 这里只负责把拿到的令牌回填到输入框并立刻落地成当前账号。
        /// </para>
        /// </summary>
        private void OpenProviderWebLogin(PanKind kind)
        {
            if (!WebView2Runtime.IsInstalled)
            {
                ShowWebView2Missing("仍然可以在下方输入框里手动粘贴令牌。");
                return;
            }

            try
            {
                var win = new WebLoginWindow(kind,
                    token =>
                    {
                        // 迅雷内嵌登录返回的是打包记录（access_token + refresh_token +
                        // sub + device_id + captcha_token），先把 refresh_token 那几个
                        // 存下来，输入框里只显示 access_token —— 用户看到的就是一个令牌。
                        string shown = kind == PanKind.Xunlei ? ConsumeXunleiRecord(token) : token;
                        var box = kind == PanKind.Aliyun ? AliyunTokenBox : XunleiTokenBox;
                        if (box != null)
                            box.Text = shown;
                        ApplySettings();
                        Log(PanUrlParser.DisplayNameOf(kind) + " 内嵌登录成功，凭证已保存。");
                        // 拿到就立刻设成当前账号，用户不用再点"验证"
                        _ = ApplyProviderLoginAsync(kind, shown);
                    },
                    msg => LogDetail(msg))
                {
                    Owner = this,
                };
                // 手动登录期间让自动续期让路：WebView2 的 user data folder 按网盘固定
                // （profile-aliyun），同一份目录不能被两个 WebView2 进程同时占用。
                _panWebLoginOpen = true;
                try
                {
                    win.ShowDialog();
                }
                finally
                {
                    _panWebLoginOpen = false;
                }
            }
            catch (Exception ex)
            {
                Log("打开内嵌登录失败: " + ex.Message);
            }
        }


        /// <summary>换目录时重置：停掉上一轮、清空待办、重置预算。</summary>
        private void ResetDirSizeScan()
        {
            try { _dirSizeCts?.Cancel(); } catch { }
            _dirSizeCts = new CancellationTokenSource();
            _dirSizeQueue.Clear();
            _dirSizeBudget = DirSizeRequestBudget;
            // 换代：旧一代的后台循环即使取消晚了，也会因为序号对不上而整轮作废。
            _dirSizeGen++;
            _dirSizeCtx = null;
            _dirSizeRunning = false;
        }


        /// <summary>把当前「模式 + 账号 + 目录」拍成快照，供后台扫描比对。</summary>
        private DirSizeScanContext CaptureDirSizeContext()
        {
            return new DirSizeScanContext
            {
                Gen = _dirSizeGen,
                ShareMode = ShareMode,
                Kind = _activeKind,
                PwdId = ShareMode ? (_pwdId ?? "") : "",
                Stoken = ShareMode ? (_stoken ?? "") : "",
                AccountTag = ShareMode ? "" : DirCacheAccountTag,
            };
        }


        /// <summary>这一轮扫描的上下文是否仍然有效（模式/账号/分享都没换）。</summary>
        private bool IsDirSizeContextCurrent(DirSizeScanContext ctx)
        {
            if (ctx == null || ctx.Gen != _dirSizeGen) return false;
            if (ctx.ShareMode != ShareMode) return false;
            if (ctx.ShareMode)
            {
                return string.Equals(ctx.PwdId, _pwdId ?? "", StringComparison.Ordinal)
                    && string.Equals(ctx.Stoken, _stoken ?? "", StringComparison.Ordinal);
            }
            return ctx.Kind == _activeKind
                && string.Equals(ctx.AccountTag, DirCacheAccountTag, StringComparison.Ordinal);
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
            // 分页会多次调用这里：同一轮里必须**复用**同一个上下文，
            // 只有模式/账号/分享真的变了才重新拍快照（否则每批都换一代，前一批白算）。
            if (_dirSizeCtx == null || !IsDirSizeContextCurrent(_dirSizeCtx))
                _dirSizeCtx = CaptureDirSizeContext();
            _dirSizeRunning = true;
            // 不 await：让它在后台自己跑，界面立刻可用
            var ignored = DrainDirSizeQueueAsync(cts.Token, _dirSizeCtx);
        }


        // ---------------- 扫码登录弹窗 ----------------

        /// <summary>
        /// 扫码登录窗口。null = 还没开过、或已经被用户关掉。
        /// <para>
        /// 登录页原来把二维码、状态、按钮全铺在左列，光二维码就占 264×264，
        /// 把「多网盘账号」四卡挤到下面、整页一大半是二维码框（用户明确要求改掉）。
        /// 现在扫码是**夸克卡上的一个按钮**，点了才弹窗，登录页只留四卡 + 状态 + 导入。
        /// </para>
        /// </summary>
        private QrLoginWindow _qrWin;


        /// <summary>
        /// 取（必要时新建并显示）扫码登录窗口。
        /// <para>
        /// ⚠️ 用 <see cref="Window.Show"/> 而不是 <c>ShowDialog</c>：扫码要等用户在手机上确认，
        /// 可能持续几分钟。<c>ShowDialog</c> 会再起一层消息循环、把主窗口完全锁死，
        /// 用户连「换个网盘登录」或「先去看看别的」都做不到。非模态 + <c>Owner</c> 关联，
        /// 既能盖在主窗口之上，又不冻结主界面。
        /// </para>
        /// <para>
        /// ⚠️ 窗口关闭必须把 <see cref="_qrFlowCts"/> 取消掉：否则用户关窗后
        /// 轮询还在后台每 2 秒打一次夸克接口，直到 180 秒超时才停。
        /// </para>
        /// </summary>
        private QrLoginWindow EnsureQrWindow()
        {
            if (_qrWin != null) return _qrWin;

            var win = new QrLoginWindow { Owner = this };
            // 弹窗里只剩一个按钮（GetBtn 是 RefreshBtn 的别名，同一个对象），
            // 所以**只挂一次** —— 两边各挂一次会让一次点击取两次码。
            win.RefreshBtn.Click += OnQrLogin;
            win.Closed += (s, e) =>
            {
                if (ReferenceEquals(_qrWin, win)) _qrWin = null;
                var cts = _qrFlowCts;
                _qrFlowCts = null;
                if (cts != null)
                {
                    try { cts.Cancel(); } catch { /* 已释放/已取消，忽略 */ }
                }
            };
            _qrWin = win;
            win.Show();
            return win;
        }


        private async void OnQrLogin(object sender, RoutedEventArgs e)
        {
            var win = EnsureQrWindow();
            if (win == null) return;

            // 弹窗里只有一个「刷新二维码」按钮：取码与重取都是同一个动作，
            // 点它永远是「重新取一张」。
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

            // 只剩一个按钮（GetBtn 是它的别名），禁一次即可。
            win.RefreshBtn.IsEnabled = false;
            SetQrStatus("正在获取二维码…", "#333");

            // ⚠️ 这里**故意不用** BeginBusy 给按钮加转圈：
            //    按钮在主窗口的夸克卡上，而二维码在弹窗里，用户看的是弹窗；
            //    后面的轮询是「等用户拿手机扫码」，可能持续几分钟，那不是"加载"。
            //    弹窗内的两个按钮已经禁用，状态条也写了「正在获取二维码…」，够了。
            try
            {
                using (var svc = new LoginQrService())
                {
                    var (ok, token, qrUrl, err) = await svc.GetQrCodeAsync();
                    if (!ok)
                    {
                        SetQrStatus(err, "#C0392B");
                        return;
                    }

                    // 在界面里直接画出二维码，用户用夸克 App 扫屏幕即可登录，
                    // 不必再跳到浏览器（浏览器只是备用路径）。
                    ShowQrImage(qrUrl);

                    // ✅ 二维码画出来之后就把按钮**放开**：等待扫码期间用户随时可以点
                    //    「刷新二维码」换一张（会取消本次轮询、重新取码），不必干等它超时。
                    win.RefreshBtn.IsEnabled = true;

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
                                // 过期了最顺手的动作是点「刷新」，把焦点给它方便回车重试
                                win.RefreshBtn.Focus();
                                break;
                            case QrLoginStage.GotTicket:
                                SetQrStatus("已确认，正在换取登录态…", "#1B6FB8");
                                break;
                        }
                    });

                    // 超时与 token 的真实有效期对齐：实测 token 在 ~180 秒后变成
                    // 50004002（Token Not Found），早先设 300 秒会让界面在最后 2 分钟里
                    // 一直等一个已经失效的码。
                    var poll = await svc.PollForTicketWithStatusAsync(token, 180, status, cts.Token);
                    if (poll.Stage == QrLoginStage.Expired)
                    {
                        // 过期/无效：收起图片，提示用户点「刷新」重新取一张。
                        HideQrImage();
                        SetQrStatus(DescribeExpired(poll) + " 点「刷新二维码」可重新获取。", "#C0392B");
                        return;
                    }

                    var (cok, cookie, cerr) = await svc.ExchangeTicketAsync(poll.Ticket);
                    if (!cok)
                    {
                        SetQrStatus(cerr, "#C0392B");
                        return;
                    }
                    // 这个页面就是夸克扫码页 —— 恒为 Quark，不能跟着 _activeKind 走
                    await ApplyLoginAsync(cookie, PanKind.Quark);
                    SetQrStatus("登录成功", "#2E7D32");
                    HideQrImage();

                    // 登录成功后自动收起弹窗：用户的下一步是回主界面用网盘，
                    // 没必要盯着二维码框。稍等一下再关，让人看清「登录成功」。
                    try
                    {
                        await Task.Delay(900);
                        if (ReferenceEquals(_qrWin, win)) win.Close();
                    }
                    catch { /* 用户已经手动关了，忽略 */ }
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
                    win.RefreshBtn.IsEnabled = true;
                }
            }
        }


        /// <summary>
        /// 更新状态提示文本（含颜色与图标），并确保提示框可见。
        /// colorHex 仍是调用方给的十六进制色（保持原有调用点不动），但**图标**按语义挑：
        /// 不再无脑用黄三角 —— 「正在等待扫码」是正常流程，配警告图标会让人以为出错了。
        /// </summary>
        private void SetQrStatus(string text, string colorHex)
        {
            var win = _qrWin;
            if (win == null) return;
            win.QrStatusBox.Visibility = Visibility.Visible;
            win.QrStatusText.Text = text;

            var brush = ResolveStatusBrush(colorHex);
            var icon = PickStatusIcon(text, colorHex, out _);
            win.QrStatusIcon.Data = icon;
            win.QrStatusIcon.Stroke = brush;
            win.QrStatusText.Foreground = brush;
        }


        /// <summary>把二维码 URL 渲染成图像并显示在扫码弹窗里。</summary>
        private void ShowQrImage(string content)
        {
            var win = _qrWin;
            if (win == null) return;
            try
            {
                // 位图分辨率参考值。⚠️ 不要求与屏幕上的实际显示尺寸完全相等 ——
                // 弹窗里是 Stretch=Uniform，会自动缩放；这里只要给一个「够清晰、
                // 又不会被 NearestNeighbor 放大成糊块」的整数模块尺寸就行。
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
                win.QrImage.Source = decoder;
                win.QrImageBox.Visibility = Visibility.Visible;
                win.QrPlaceholderBox.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                win.QrStatusText.Text = "二维码绘制失败: " + ex.Message;
                win.QrImageBox.Visibility = Visibility.Collapsed;
                win.QrPlaceholderBox.Visibility = Visibility.Visible;
            }
        }


        private void HideQrImage()
        {
            var win = _qrWin;
            if (win == null) return;
            win.QrImage.Source = null;
            win.QrImageBox.Visibility = Visibility.Collapsed;
            // 收起二维码后把占位框放回来，保证弹窗高度不跳。
            win.QrPlaceholderBox.Visibility = Visibility.Visible;
        }
    }
}
