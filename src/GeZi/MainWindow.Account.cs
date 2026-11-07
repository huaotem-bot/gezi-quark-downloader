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
// 职责：账号与登录态（夸克/UC/阿里/迅雷）、头像、容量

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
        /// 登录入口按钮的**显示名**（四个网盘卡上统一用这个）。
        /// <para>
        /// 🚨 2026-11-03：按钮文案从「内嵌登录」改成「登录账号」之后，
        /// 代码里有 5 处提示还写着「请点『内嵌登录』」，用户找不到那个按钮。
        /// 抽成常量后，以后再改名只改这一处，不会再有漏网的文案。
        /// </para>
        /// </summary>
        private const string LoginEntryName = "登录账号";

        /// <summary>
        /// 把 provider **当前**的令牌写回配置（只在真的变了时才写盘）。
        /// <para>
        /// 为什么需要它：阿里会**轮换** refresh_token —— 每次换 access_token 都可能下发
        /// 新的 refresh_token，旧的那个随即失效。如果只把登录那一刻的值存起来，
        /// 下次启动就用一个已作废的令牌去登录，表现为「明明昨天还好好的，今天就登录失效」。
        /// 迅雷的 pan_token 目前不轮换，但同一份代码顺手覆盖，成本为零。
        /// </para>
        /// </summary>
        private void PersistProviderToken()
        {
            if (_ownProvider == null)
                return;
            try
            {
                if (_activeKind == PanKind.Aliyun)
                {
                    var a = _ownProvider as AliyunClient;
                    if (a == null)
                        return;
                    string cur = a.RefreshToken ?? "";
                    bool changed = false;
                    if (!string.IsNullOrEmpty(cur) && cur != _settings.AliyunRefreshToken)
                    {
                        _settings.AliyunRefreshToken = cur;
                        _settings.AliyunAccessToken = "";   // 有了可续期凭证就不再留短期令牌
                        changed = true;
                        LogDetail("阿里 refresh_token 已轮换，已更新保存。");
                    }
                    // 只有短期令牌的账号：把（可能已被服务端续过的）access_token 落盘，
                    // 否则重启后还是那条旧的。
                    string shortLived = a.IsShortLivedOnly ? (a.AccessToken ?? "") : "";
                    if (a.IsShortLivedOnly && !string.IsNullOrEmpty(shortLived)
                        && shortLived != _settings.AliyunAccessToken)
                    {
                        _settings.AliyunAccessToken = shortLived;
                        changed = true;
                    }
                    if (changed)
                        SettingsStore.Save(_settings);
                }
                else if (_activeKind == PanKind.Xunlei)
                {
                    var x = _ownProvider as XunleiClient;
                    if (x == null)
                        return;
                    string cur = x.PanToken ?? "";
                    if (!string.IsNullOrEmpty(cur) && cur != _settings.XunleiPanToken)
                    {
                        _settings.XunleiPanToken = cur;
                        SettingsStore.Save(_settings);
                        LogDetail("迅雷 pan_token 已更新，已保存。");
                    }
                }
            }
            catch { }
        }


        /// <summary>
        /// 当前阿里/迅雷账号的**凭证原文**（阿里是 refresh_token，迅雷是 pan_token）。
        /// 夸克系没有这个 —— 它们用的是 Cookie，直接取 <see cref="_client"/>.CookieStr。
        /// 用于「导出凭证」和账号卡片展示，不参与任何网络请求。
        /// </summary>
        private string OwnProviderToken()
        {
            try
            {
                if (_activeKind == PanKind.Aliyun)
                {
                    var a = _ownProvider as AliyunClient;
                    return a == null ? "" : (a.RefreshToken ?? "");
                }
                if (_activeKind == PanKind.Xunlei)
                {
                    var x = _ownProvider as XunleiClient;
                    return x == null ? "" : (x.PanToken ?? "");
                }
            }
            catch { }
            return "";
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
            // 目录缓存键带模式前缀（"s:"/"d:"）与账号指纹，这里整体清掉：
            // 分享侧被顺带清掉只是下次进目录重取一次，无副作用；网盘侧则是必须清的。
            _dirCache.Clear();
            _dirSizeCache.Clear();

            // 账号变了 → 指纹必须重算，否则新账号会命中旧账号的缓存键。
            _dirCacheAccountTag = null;

            // 网盘快照重置为"没进过"的状态（ShareFileItem 是可变引用集合，整体换新实例）
            _driveNav.Files = new List<ShareFileItem>();
            _driveNav.CurFid = "0";
            _driveNav.FidStack = new List<string>();
            _driveNav.PathList = new List<string>();
            _driveNav.PwdId = null;
            _driveNav.Stoken = null;
        }


        /// <summary>按网盘种类用设置里的凭证建客户端。</summary>
        private IPanProvider CreateProviderFor(PanKind kind)
        {
            // ⚠️ 夸克与 UC 都用 Cookie 字段，但两者是**不同的值、不同的域**：
            //    夸克是 .quark.cn 的 Cookie，UC 是 .uc.cn 的。混用会拿错登录态，
            //    表现为"明明登录了却提示未登录/无效"。所以这里按 kind 分发。
            var cred = new PanCredential
            {
                QuarkCookie = _settings.Cookie ?? "",
                UcCookie = _settings.UcCookie ?? "",
                RefreshToken = _settings.AliyunRefreshToken ?? "",
                // 🚨 阿里还有网页登录这条路线：它只给 access_token（约 2 小时），
                //    没有 refresh_token。漏传这两个字段会让「网页登录成功但解析分享失败」。
                AccessToken = _settings.AliyunAccessToken ?? "",
                AliyunDeviceId = _settings.AliyunDeviceId ?? "",
                AliyunDevicePrivateKey = _settings.AliyunDevicePrivateKey ?? "",
                PanToken = _settings.XunleiPanToken ?? "",
                CaptchaToken = _settings.XunleiCaptchaToken ?? "",
                // 设备标识优先取网页登录抓到的真实 deviceid，其次账号登录那套。
                DeviceId = !string.IsNullOrEmpty(_settings.XunleiWebDeviceId)
                    ? _settings.XunleiWebDeviceId
                    : (_settings.XunleiDeviceId ?? ""),
            };
            return AttachXunleiCaptcha(PanProviderRegistry.Create(kind, cred));
        }


        /// <summary>验证当前输入框里的 UC Cookie 是否真的有效（调 account/info）。</summary>
        /// <summary>
        /// 把 <c>UcDriveClient.ValidateCookieAsync</c> 返回的技术原因翻译成人话。
        /// 它原文是「Cookie 不完整：需要同时包含非空的 __pus 与 __puus。」——
        /// 用户不知道这两个词是什么，也不知道怎么补（2026-10-26 用户要求）。
        /// </summary>
        private static string UcCookieReason(string error)
        {
            if (string.IsNullOrEmpty(error)) return "登录态无效，请重新登录。";

            // 缺令牌 / 不完整
            if (error.IndexOf("不完整", StringComparison.Ordinal) >= 0
                || error.IndexOf("__pus", StringComparison.Ordinal) >= 0
                || error.IndexOf("__puus", StringComparison.Ordinal) >= 0)
                return "缺少登录令牌。请在网页版登录后，用浏览器扩展重新导出整条 Cookie 再粘贴。";

            if (error.IndexOf("为空", StringComparison.Ordinal) >= 0)
                return "没有填写 Cookie。";

            if (error.IndexOf("过期", StringComparison.Ordinal) >= 0
                || error.IndexOf("失效", StringComparison.Ordinal) >= 0
                || error.IndexOf("未登录", StringComparison.Ordinal) >= 0)
                return "登录已过期。请重新登录后再导出 Cookie。";

            // 剩下的可能是网络类原文，交给统一归因
            return "请重新登录后再试。";
        }


        private async void OnUcVerifyLogin(object sender, RoutedEventArgs e)
        {
            string cookie = UcCookieBox == null ? "" : (UcCookieBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(cookie))
            {
                AppDialog.Show(this, "请先填入或通过内嵌登录获取 UC Cookie。",
                    "没有凭证", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            long t0 = BusyClock();
            try
            {
                if (!BeginBusy(UcVerifyBtn, "验证中")) return;
                using (var uc = new UcDriveClient(cookie))
                {
                    var (ok, nick, error) = await uc.ValidateCookieAsync(cookie);
                    if (ok)
                    {
                        if (UcCookieBox != null)
                            UcCookieBox.Text = cookie;
                        ApplySettings();
                        Log("UC网盘 登录态有效：" + nick);

                        // ⚠️ 光"校验通过"是不够的 —— 早先这里校验完弹个框就结束，
                        // 于是账号卡片和「我的网盘」用的还是**夸克**客户端，
                        // 用户看到的就是"登录成功了，但什么都不显示"。
                        // 现在校验通过即把它设为**当前账号**：换客户端、存 UC 凭证、
                        // 拉容量、加载 UC 自己的根目录（ApplyLoginAsync 内部全都会做）。
                        await ApplyLoginAsync(cookie, PanKind.Uc);
                        AppDialog.Show(this,
                            "UC网盘 登录态有效，已切换为当前账号。\n\n账号：" + nick,
                            "验证通过", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        Log("UC网盘 登录态无效: " + error);
                        AppDialog.Show(this,
                            "这串 Cookie 不能用来登录 UC网盘：" + UcCookieReason(error),
                            "验证未通过", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("验证 UC 登录态失败: " + ex.Message);
            }
            finally
            {
                await EndBusyAsync(UcVerifyBtn, t0);
            }
        }


        /// <summary>
        /// 迅雷账号登录（手机号 + 短信验证码）。
        /// <para>
        /// 与内嵌登录的区别：这条路拿到的是 <c>refresh_token</c>，能长期续期；
        /// 内嵌登录只有约两小时就过期的 <c>pan_token</c>。
        /// 这是迅雷"一直显示 pan_token 无效"的根本解法。
        /// </para>
        /// </summary>
        private async void OnAliyunVerifyLogin(object sender, RoutedEventArgs e)
        {
            await VerifyProviderLoginAsync(PanKind.Aliyun);
        }


        private async void OnXunleiVerifyLogin(object sender, RoutedEventArgs e)
        {
            await VerifyProviderLoginAsync(PanKind.Xunlei);
        }


        /// <summary>阿里 / 迅雷共用的"验证登录态"：校验输入框里的令牌并设为当前账号。</summary>
        private async Task VerifyProviderLoginAsync(PanKind kind)
        {
            var box = kind == PanKind.Aliyun ? AliyunTokenBox : XunleiTokenBox;
            var btn = kind == PanKind.Aliyun ? AliyunVerifyBtn : XunleiVerifyBtn;
            string token = box == null ? "" : (box.Text ?? "").Trim();
            string panName = PanUrlParser.DisplayNameOf(kind);

            if (string.IsNullOrEmpty(token))
            {
                AppDialog.Show(this, "请先填入或通过内嵌登录获取 " + panName + " 令牌。",
                    "没有凭证", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            long t0 = BusyClock();
            try
            {
                if (!BeginBusy(btn, "验证中")) return;
                // notifyFailure: true —— 这是用户主动点的，必须有可见结果
                await ApplyProviderLoginAsync(kind, token, true);
            }
            finally
            {
                await EndBusyAsync(btn, t0);
            }
        }


        /// <summary>
        /// 刷新"运行时是否可用"的界面状态。
        /// 缺失时只置灰内嵌登录按钮 + 一行说明，不弹窗、不阻塞 —— 这是第二层降级。
        /// </summary>
        private void RefreshUcRuntimeStatus()
        {
            try
            {
                bool installed = WebView2Runtime.IsInstalled;

                if (UcWebLoginBtn != null)
                {
                    UcWebLoginBtn.IsEnabled = installed;
                    UcWebLoginBtn.ToolTip = installed
                        ? "打开官方登录页，登录成功后自动填入 Cookie"
                        : "需要 WebView2 运行时（Windows 11 预装；Win10 多数已有）。缺失时请用手动粘贴。";
                }

                if (UcRuntimeStatusText != null)
                {
                    // 🎨 2026-11-01（用户 m13541 第 4 项「更简约现代、去 AI 味」）：
                    //    原来这里**恒定**显示「WebView2 运行时已就绪 (156.0.4314.58)，可使用内嵌登录。」
                    //    —— 一行版本号加一句"能用"，是纯粹的噪音：正常用户不需要知道
                    //    运行时版本，而且"已就绪"这件事本身没有任何操作含义。
                    //    现在只在**缺失**时才出现（那才是用户需要知道并处理的信息）。
                    bool runtimeOk = WebView2Runtime.IsInstalled;
                    UcRuntimeStatusText.Text = runtimeOk ? "" : WebView2Runtime.DescribeStatus();
                    UcRuntimeStatusText.Visibility = runtimeOk
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                }

                // 阿里 / 迅雷的内嵌登录按钮同理：缺运行时只置灰，手动粘贴始终可用。
                string tip = installed
                    ? "打开官方登录页，登录成功后自动填入令牌"
                    : "需要 WebView2 运行时（Windows 11 预装；Win10 多数已有）。缺失时请手动粘贴。";
                if (AliyunWebLoginBtn != null)
                {
                    AliyunWebLoginBtn.IsEnabled = installed;
                    AliyunWebLoginBtn.ToolTip = tip;
                }
                if (XunleiWebLoginBtn != null)
                {
                    XunleiWebLoginBtn.IsEnabled = installed;
                    XunleiWebLoginBtn.ToolTip = tip;
                }
            }
            catch { }
        }


        // ---------------- 多网盘账号卡片（登录页） ----------------

        /// <summary>
        /// 刷新登录页四张网盘卡的状态（夸克 / UC / 阿里 / 迅雷）。
        ///
        /// 三级语义（用户拍板）：
        ///   已登录     = 该网盘的客户端真实存在且是当前激活账号 → 绿
        ///   凭证已保存 = 有凭证但当前没激活 → 琥珀
        ///   未登录     = 没有任何凭证 → 灰
        ///
        /// ⚠️ 刻意**不引入额外标志字段**，直接用 IsLoggedInAny + _activeKind 判定。
        ///    这样"已登录"永远等于真实运行态，不会出现卡上写已登录、实际却用不了的情况。
        /// ⚠️ 验证失败时 _client / _ownProvider 会被置 null（见 ApplyProviderLoginAsync），
        ///    这里自然降级回"凭证已保存"，不需要额外的失败回调。
        /// </summary>
        private void RefreshPanCards()
        {
            try
            {
                // ⚠️ 2026-11-01（用户要求）：「自动续期」这类**技术机制**后缀全部删掉。
                //    原来已登录时显示「雨过晴 · __pus 自动续期」「迅雷用户 · 网页凭证 + 自动续期」，
                //    这些是给开发者看的实现细节，用户只需要知道"登的是谁"。
                //    ⇒ loggedHint 一律传空串：已登录时那一行**只显示账号昵称**。
                //    savedHint 改成一句操作指引（不是技术描述）：告诉用户怎么刷新。
                //    emptyHint 保留原样 —— 它是「这家怎么登录」的操作指路，不是噪音。
                // 🚨 2026-11-03：按钮文案已统一改成「登录账号」，所有提示必须跟着改，
                //    否则用户会去找一个界面上根本不存在的「内嵌登录」按钮。
                //    统一用 LoginEntryName 常量，以后改名只改一处。
                const string SavedTip = "点「" + LoginEntryName + "」刷新";

                ApplyPanCard(PanKind.Quark, QuarkPanStatusText, QuarkPanHintText,
                    !string.IsNullOrWhiteSpace(_settings.Cookie), PanIsLive(PanKind.Quark),
                    PanAccountName(PanKind.Quark),
                    "", SavedTip, "扫码或" + LoginEntryName);

                ApplyPanCard(PanKind.Uc, UcPanStatusText, UcPanHintText,
                    !string.IsNullOrWhiteSpace(_settings.UcCookie), PanIsLive(PanKind.Uc),
                    PanAccountName(PanKind.Uc),
                    "", SavedTip, LoginEntryName + "或粘贴 Cookie");

                ApplyPanCard(PanKind.Aliyun, AliyunPanStatusText, AliyunPanHintText,
                    !string.IsNullOrWhiteSpace(_settings.AliyunRefreshToken)
                        || !string.IsNullOrWhiteSpace(_settings.AliyunAccessToken),
                    PanIsLive(PanKind.Aliyun),
                    PanAccountName(PanKind.Aliyun),
                    "", SavedTip, LoginEntryName + "或粘贴令牌");

                // 迅雷的「短信登录」入口已按用户要求删掉，空态文案不能再说「或短信登录」。
                ApplyPanCard(PanKind.Xunlei, XunleiPanStatusText, XunleiPanHintText,
                    !string.IsNullOrWhiteSpace(_settings.XunleiPanToken), PanIsLive(PanKind.Xunlei),
                    PanAccountName(PanKind.Xunlei),
                    "", SavedTip, LoginEntryName + "或粘贴令牌");
            }
            catch { }
        }

        /// <summary>
        /// 这张网盘卡该显示哪个账号名。
        /// <para>
        /// 优先用运行态实测昵称（<see cref="PanLiveNick"/>），它来自真实接口、最可信；
        /// 软件刚启动、还没验证过时回退到档案里记的主账号（有备注用备注）。
        /// 都取不到就返回空串，卡片退回只显示技术说明，不留空白。
        /// </para>
        /// </summary>
        private string PanAccountName(PanKind kind)
        {
            string nick = PanLiveNick(kind);
            if (!string.IsNullOrWhiteSpace(nick))
                return nick.Trim();
            try
            {
                var p = AccountStore.GetPrimary((int)kind);
                if (p != null)
                {
                    string cap = p.Caption;
                    if (!string.IsNullOrWhiteSpace(cap))
                        return cap.Trim();
                }
            }
            catch { }
            return "";
        }

        /// <summary>单张网盘卡的状态渲染（RefreshPanCards 的辅助方法）。</summary>
        private void ApplyPanCard(PanKind kind, TextBlock status, TextBlock hint,
            bool hasCredential, bool live, string accountName,
            string loggedHint, string savedHint, string emptyHint)
        {
            if (status == null) return;

            if (live)
            {
                status.Text = "已登录";
                status.Foreground = (Brush)FindResource("CapacityLowBrush");
                if (hint != null)
                {
                    // 2026-11-01：loggedHint 现在可以为空（技术后缀已按用户要求删掉）。
                    // 必须在这里判空，否则会拼出「雨过晴  ·  」这种带孤立分隔符的尾巴。
                    if (string.IsNullOrEmpty(loggedHint))
                        hint.Text = accountName ?? "";
                    else
                        hint.Text = string.IsNullOrEmpty(accountName)
                            ? loggedHint
                            : accountName + "  ·  " + loggedHint;
                }
                return;
            }

            if (hasCredential)
            {
                status.Text = "凭证已保存";
                status.Foreground = (Brush)FindResource("CapacityMidBrush");
                if (hint != null) hint.Text = savedHint;
                return;
            }

            status.Text = "未登录";
            status.Foreground = (Brush)FindResource("TextTertiaryBrush");
            if (hint != null) hint.Text = emptyHint;
        }

        /// <summary>
        /// 夸克卡上的「扫码登录」按钮：弹出扫码登录窗口并立刻取一张二维码。
        /// <para>
        /// 原来这里要把左侧的二维码卡片滚进视野 —— 现在二维码搬进了独立弹窗
        /// （登录页只留四卡 + 状态 + 导入），不需要再滚了。
        /// </para>
        /// </summary>
        private void OnQuarkPanScan(object sender, RoutedEventArgs e)
        {
            OnQrLogin(sender, e);
        }

        /// <summary>当前展开的粘贴面板（"Uc"/"Aliyun"/"Xunlei"，null = 全部收起）。</summary>
        private string _panPasteKind;

        /// <summary>
        /// 展开 / 收起「手动粘贴」输入区。
        /// <para>
        /// Tag 决定操作哪一家：Uc / Aliyun / Xunlei；Tag="close"（或无 Tag）表示全部收起。
        /// 点同一个按钮再点一次 = 收起。
        /// </para>
        /// <para>
        /// ⚠️ 三块面板原来各自塞在卡片内部，展开时那张卡会被撑高、其余三卡跟着等高，
        /// 整行高度一起跳。现在三块都挂在四卡下方的**通栏行**里，只显示一块，
        /// 卡片高度恒定，页面不会因为展开而抖动。
        /// </para>
        /// </summary>
        private void OnTogglePanPaste(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            string key = btn != null ? (btn.Tag as string) : null;

            // 「收起」按钮（Tag="close" 或没 Tag）：三块全部关掉
            if (string.IsNullOrEmpty(key) || key == "close")
            {
                SetPanPaste(null);
                return;
            }

            // 点同一个按钮 = 收起；点另一个 = 换成那一块
            bool sameOpen = key == _panPasteKind;
            SetPanPaste(sameOpen ? null : key);
            if (sameOpen) return;

            try
            {
                // 2026-11-01：夸克那块的 CookieImportBox 已随「导入 Cookie」卡片删除，
                // 不再需要 Focus（夸克走扫码 / 内嵌登录）。
                if (key == "Uc" && UcCookieBox != null) UcCookieBox.Focus();
                else if (key == "Aliyun" && AliyunTokenBox != null) AliyunTokenBox.Focus();
                else if (key == "Xunlei" && XunleiTokenBox != null) XunleiTokenBox.Focus();
            }
            catch { }
        }


        /// <summary>
        /// 统一设置三块粘贴面板的显隐。
        /// ⚠️ 只允许一块可见：三块叠在同一个通栏里会让页面一下长出一大截。
        /// </summary>
        private void SetPanPaste(string kind)
        {
            _panPasteKind = kind;
            bool show = kind != null;

            if (QuarkPastePanel != null)
                QuarkPastePanel.Visibility = kind == "Quark" ? Visibility.Visible : Visibility.Collapsed;
            if (UcPastePanel != null)
                UcPastePanel.Visibility = kind == "Uc" ? Visibility.Visible : Visibility.Collapsed;
            if (AliyunPastePanel != null)
                AliyunPastePanel.Visibility = kind == "Aliyun" ? Visibility.Visible : Visibility.Collapsed;
            if (XunleiPastePanel != null)
                XunleiPastePanel.Visibility = kind == "Xunlei" ? Visibility.Visible : Visibility.Collapsed;

            if (PanPasteRow != null)
                PanPasteRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            if (PanPasteLabel != null)
            {
                PanPasteLabel.Text =
                    kind == "Quark" ? "夸克网盘 Cookie"
                    : kind == "Uc" ? "UC 网盘 Cookie"
                    : kind == "Aliyun" ? "阿里云盘 refresh_token"
                    : kind == "Xunlei" ? "迅雷云盘 Bearer 令牌"
                    : "";
            }
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
            if (IsLoggedInAny) return true;

            // 🚨 2026-10-26 用户反馈：「我刚进软件就点续传/下载，直接报 401 登录态已失效，
            //   可我账号明明还没登录完成呢 —— 说好了在没登录成功之前要给提示。」
            //   根因：启动时会异步恢复上次的登录态（_startupLoginTask），这期间
            //   IsLoggedInAny 还是 false、_client 还是 null，用户一点就发请求 → 服务端 401。
            //   这里区分「真的没登录」和「正在恢复」：后者给的是"稍候"，不是"去登录"。
            if (IsLoginPending)
            {
                try
                {
                    AppDialog.Show(this,
                        "登录态正在恢复中（上次的账号还在校验），请稍等几秒再试。\n\n"
                        + "这不是登录失败，不需要重新扫码。",
                        "请稍候", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch { }
                Log("登录态正在恢复，已跳过本次「" + action + "」。稍等几秒再试即可。");
                return false;
            }

            try
            {
                AppDialog.Show(this,
                    action + "需要先登录。已为你切到登录页。",
                    "需要登录", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
            // 顺手把用户带到登录页（登录页 = 第 4 个 Tab，索引 3）
            try { Tabs.SelectedIndex = 3; } catch { }
            return false;
        }


        /// <summary>
        /// 启动时那次「恢复上次登录态」还在跑吗？
        /// <para>
        /// 这期间点下载/续传会拿到 401 —— 不是账号失效，只是校验没完成。
        /// 用它把按钮变灰并给出说明，恢复完成后自动解禁。
        /// </para>
        /// </summary>
        private bool IsLoginPending
        {
            get
            {
                var t = _startupLoginTask;
                return t != null && !t.IsCompleted;
            }
        }


        /// <summary>
        /// 按「登录态是否就绪」刷新下载按钮的可用性与提示。
        /// 恢复期间变灰 + 悬停说明；恢复完成后（无论成功失败）都解禁 ——
        /// 失败时该走正常的「需要登录」提示，不能把按钮永久锁死。
        /// </summary>
        private void UpdateLoginPendingUi()
        {
            bool pending = IsLoginPending && !IsLoggedInAny;
            try
            {
                if (DownloadSelBtn != null)
                {
                    DownloadSelBtn.IsEnabled = !pending;
                    DownloadSelBtn.ToolTip = pending ? "登录态正在恢复中，请稍候…" : null;
                }
                if (DownloadAllBtn != null)
                {
                    DownloadAllBtn.IsEnabled = !pending;
                    DownloadAllBtn.ToolTip = pending ? "登录态正在恢复中，请稍候…" : null;
                }
            }
            catch { }
        }


        // ---------------- 登录 ----------------

        // 原来的 OnLoginClick（只是 Tabs.SelectedIndex = 3）已随「登录」按钮一起删除：
        // 那个按钮和左侧「获取二维码」、下方「导入 Cookie」重复，用户反馈「没有任何作用」。

        /// <summary>
        /// 打开账号管理弹窗：列出**这一家网盘**保存的账号，可一键切换 / 删除。
        /// </summary>
        /// <remarks>
        /// 用户要求「点夸克的账号列表时只显示夸克的账号，点迅雷的显示迅雷的」，
        /// 所以按发送者身上的 Tag（网盘名）过滤。Tag 为空时（理论上不会）退化为全部。
        /// </remarks>
        private void OnOpenAccounts(object sender, RoutedEventArgs e)
        {
            PanKind? filter = null;
            var fe = sender as FrameworkElement;
            string tag = fe != null ? (fe.Tag as string) : null;
            if (!string.IsNullOrEmpty(tag))
            {
                PanKind parsed;
                if (Enum.TryParse(tag, true, out parsed))
                    filter = parsed;
            }

            // 未登录且**这家一个已保存账号都没有**时，别弹一个空列表让人更困惑。
            // 注意用 IsLoggedInAny：阿里/迅雷账号下 _client 是 null，
            // 但它们已经登录了，不该再弹二维码。
            if (!IsLoggedInAny)
            {
                int saved = CountSavedAccounts(filter);
                if (saved == 0)
                {
                    if (filter == null || filter == PanKind.Quark)
                    {
                        // 只有夸克能扫码，其余三家必须走内嵌登录
                        Log("还没有已保存的账号，正在获取二维码…请用夸克 App 扫码登录。");
                        OnQrLogin(sender, e);
                        return;
                    }
                    AppDialog.Show(this,
                        "还没有保存的" + PanUrlParser.DisplayNameOf(filter.Value)
                        + "账号。请点该卡片上的「内嵌登录」，登录成功后会自动保存。",
                        "账号列表", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            ShowAccountsDialog(filter);
        }


        /// <summary>数一数本机保存了几个账号（可按网盘过滤）。</summary>
        private static int CountSavedAccounts(PanKind? filter)
        {
            try
            {
                // 只读对应网盘的子目录 —— 别家的文件根本不会被打开。
                return AccountStore.LoadAll(filter).Count;
            }
            catch { return 0; }
        }


        private void ShowAccountsDialog(PanKind? filter)
        {
            string curCookie = _client?.CookieStr ?? _settings.Cookie ?? "";
            // 当前账号的 Key：优先用它判断"哪一行是当前账号"，Cookie 只作兜底
            string curKey = _settings.CurrentAccountKey ?? "";
            if (string.IsNullOrEmpty(curKey))
                curKey = _client?.AccountSig ?? "";

            bool allPans = !filter.HasValue;
            string panName = allPans ? "" : PanUrlParser.DisplayNameOf(filter.Value);
            string titleText = allPans ? "账号列表" : panName + " 账号";

            var win = new Window
            {
                Owner = this,
                Title = titleText,
                ResizeMode = ResizeMode.CanResize,
            };
            DialogChrome.StyleWindow(win, 520, 520);

            var root = DialogChrome.DialogRoot();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 说明
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 列表
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮

            // 标题已画在自绘标题栏里（2026-10-26），内容区不再重复。
            var hint = DialogChrome.Tip(
                allPans
                    ? "点一行即可切换，不用重新登录；删除会同时清掉本机保存的登录信息。"
                    : "这里只列出" + panName + "的账号。点一行即可切换；删除会清掉本机保存的这份登录信息。",
                8, 12);
            Grid.SetRow(hint, 1);
            root.Children.Add(hint);

            var listBox = DialogChrome.StyledList();
            listBox.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            listBox.BorderThickness = new Thickness(0);
            listBox.Background = Brushes.Transparent;
            var listCard = DialogChrome.Card(listBox);
            listCard.Padding = new Thickness(10);
            Grid.SetRow(listCard, 2);
            root.Children.Add(listCard);

            Action rebuild = null;
            // 折叠状态记在弹窗这一层：rebuild 会清空列表重画，状态不能丢。
            var collapsed = new System.Collections.Generic.HashSet<PanKind>();
            rebuild = () =>
            {
                listBox.Items.Clear();
                var accounts = new List<AccountProfile>();
                try
                {
                    // 只看本家目录（filter 为空时读四家全部）。
                    accounts.AddRange(AccountStore.LoadAll(filter));
                }
                catch { }

                if (accounts.Count == 0)
                {
                    var empty = new StackPanel { Margin = new Thickness(16, 28, 16, 28) };
                    empty.Children.Add(new TextBlock
                    {
                        Text = "还没有保存的" + (allPans ? "" : panName) + "账号",
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = (Brush)(TryFindResource("TextSecondaryBrush")
                                             ?? new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55))),
                    });
                    empty.Children.Add(new TextBlock
                    {
                        Text = "登录成功后会自动保存一份，下次可直接切换。",
                        FontSize = 11,
                        Margin = new Thickness(0, 6, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                        Foreground = (Brush)(TryFindResource("TextTertiaryBrush")
                                             ?? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))),
                    });
                    listBox.Items.Add(empty);
                    return;
                }
                if (allPans)
                {
                    // 「全部账号」按网盘分节（夸克 → UC → 阿里 → 迅雷）。
                    // 混在一起时色块能区分，但账号一多还是得靠标题才扫得清。
                    foreach (var kind in new[] { PanKind.Quark, PanKind.Uc, PanKind.Aliyun, PanKind.Xunlei })
                    {
                        var group = accounts.FindAll(x => x.PanKind == (int)kind);
                        if (group.Count == 0) continue;
                        // 用户要求（2026-10-26）：每家网盘的分组可点标题折叠/展开，
                        // 账号多的时候能先把不关心的收起来。
                        bool folded = collapsed.Contains(kind);
                        var k = kind;
                        listBox.Items.Add(BuildAccountGroupHeader(kind, group.Count, folded, () =>
                        {
                            if (!collapsed.Remove(k)) collapsed.Add(k);
                            rebuild();
                        }));
                        if (!folded)
                        {
                            // 已经有分节标题了，行内副标题不用再重复网盘名
                            foreach (var a in group)
                                listBox.Items.Add(BuildAccountRow(a, curKey, curCookie, win, rebuild, false));
                        }
                    }
                }
                else
                {
                    foreach (var a in accounts)
                        listBox.Items.Add(BuildAccountRow(a, curKey, curCookie, win, rebuild, false));
                }
            };
            rebuild();

            // 底部：登录新账号（夸克能扫码，其余三家只能内嵌登录）+ 关闭
            var loginBtn = DialogChrome.PrimaryButton(
                (filter == PanKind.Quark || allPans) ? "扫码登录新账号" : "去登录新账号");
            var closeBtn = DialogChrome.SecondaryButton("关闭");
            var btns = DialogChrome.ButtonRow(loginBtn, closeBtn);
            btns.Margin = new Thickness(0, 16, 0, 0);
            Grid.SetRow(btns, 3);
            root.Children.Add(btns);

            loginBtn.Click += (s, ev) =>
            {
                // 收起本弹窗，回到登录页；夸克直接拉起扫码弹窗，其余三家提示点「内嵌登录」。
                win.Close();
                Tabs.SelectedIndex = 3;
                if (filter == PanKind.Quark || allPans)
                {
                    OnQrLogin(this, new RoutedEventArgs());
                }
                else
                {
                    // 🚨🚨 2026-11-03 修「去登录新账号只弹一句提示，没真打开登录」：
                    //   原来这里只 AppDialog.Show 一句话，让用户自己去找卡片按钮 ——
                    //   用户报「点了去登录新账号，却提示我去找内嵌登录」。
                    //   现在直接打开该家的内嵌登录窗口（新账号模式：先清 Cookie）。
                    // filter 是 PanKind?（单家视图时非空），allPans 时回落到当前账号那家。
                    OpenWebLoginForNewAccount(filter ?? _activeKind);
                }
            };
            closeBtn.Click += (s, ev) => win.Close();

            win.Content = root;
            win.ShowDialog();
        }


        /// <summary>
        /// 「全部账号」视图里的网盘分节标题（夸克 / UC / 阿里云盘 / 迅雷云盘）。
        /// 只在混合视图出现；单家视图本身就只有一家，不需要再分节。
        /// </summary>
        private Border BuildAccountGroupHeader(PanKind kind, int count, bool folded, Action onToggle)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // 折叠箭头：展开时朝下，收起时朝右（用户要求可折叠）
            var chevron = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(folded ? "M0,0 L4,4 L8,0" : "M0,0 L4,4 L0,8"),
                Stroke = (Brush)(TryFindResource("TextTertiaryBrush")
                                 ?? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))),
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 8,
                Height = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
            };
            row.Children.Add(chevron);

            // 一条 3px 竖色条，和下面账号行的色块同色，扫一眼就知道这节是哪家
            row.Children.Add(new Border
            {
                Width = 3,
                Height = 13,
                CornerRadius = new CornerRadius(2),
                Background = PanTintBrush(kind),
                Margin = new Thickness(4, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });

            row.Children.Add(new TextBlock
            {
                Text = PanUrlParser.DisplayNameOf(kind),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)(TryFindResource("TextSecondaryBrush")
                                     ?? new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55))),
            });

            row.Children.Add(new TextBlock
            {
                Text = "  " + count + " 个",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)(TryFindResource("TextTertiaryBrush")
                                     ?? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))),
            });

            var header = new Border
            {
                Child = row,
                Padding = new Thickness(0, 10, 0, 6),
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = folded ? "点击展开该网盘的账号" : "点击收起该网盘的账号",
            };
            header.MouseLeftButtonUp += (s, e) =>
            {
                if (onToggle != null) onToggle();
            };
            return header;
        }


        /// <summary>账号列表里的一行：网盘色块 + 昵称/副标题 + 「切换」「删除」。</summary>
        /// <param name="curKey">当前登录账号的 Key（来自 <see cref="AppSettings.CurrentAccountKey"/>）。</param>
        /// <param name="curCookie">
        /// 当前 Cookie。仅在 <paramref name="curKey"/> 为空时作为**兜底**判据
        /// （老配置文件里没有 CurrentAccountKey 字段）。
        /// </param>
        /// <param name="showPan">是否在副标题里带上网盘名（「全部账号」视图才需要）。</param>
        private Border BuildAccountRow(AccountProfile acc, string curKey, string curCookie,
            Window owner, Action rebuild, bool showPan)
        {
            // 原来只拿整串 Cookie 比字符串，而运行时 Cookie 会被
            // 重建/刷新（顺序、条目都会变）→ 永远比不中 → 当前账号的「切换」按钮
            // 居然还是可点的（用户反馈：「当我正在登录对应的账号的时候它的切换应该是不可使用的」）。
            // 现在优先比 Key（夸克返回的 uid，稳定），Cookie 只作兜底。
            //
            // ⚠️ 2026-10-25：`IsPrimary` 是「**这家网盘**下次切过去用哪个」—— 四家各自都有一个，
            // 不能直接当「正在使用」用，否则四家的主账号按钮会全部置灰、用户点不了切到别家。
            // 「正在使用」= 这家网盘是当前激活的网盘 **且** 这条是它的主账号。
            bool isPrimary = acc.IsPrimary;
            bool isCurrent = (isPrimary && _activeKind == (PanKind)acc.PanKind)
                || (!string.IsNullOrEmpty(curKey)
                    ? string.Equals(acc.Key, curKey, StringComparison.Ordinal)
                    : (!string.IsNullOrEmpty(curCookie) &&
                       string.Equals(acc.Cookie, curCookie, StringComparison.Ordinal)));

            var kind = (PanKind)acc.PanKind;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 网盘色块：同一列表里混着不同网盘时也能一眼分清
            var badge = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(9),
                Background = PanTintBrush(kind),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 11, 0),
                Child = new TextBlock
                {
                    Text = PanShortName(kind),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Grid.SetColumn(badge, 0);
            grid.Children.Add(badge);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock
            {
                // 有备注显示备注，没有就显示昵称（备注是用户自己起的，优先级最高）
                Text = acc.Caption,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            string sub;
            if (acc.CredentialUnreadable) sub = "凭据已失效 · 请重新登录";
            else if (isCurrent) sub = "主账号 · 正在使用";
            else if (isPrimary) sub = "主账号";   // 这家网盘下次切过去就用它
            else if (acc.LastUsedUtc > DateTime.MinValue)
                sub = "最近使用 " + acc.LastUsedUtc.ToLocalTime().ToString("MM-dd HH:mm");
            else sub = "本机已保存";
            // 起了备注时把真实昵称也露出来，免得改了名认不出是谁
            if (!string.IsNullOrEmpty(acc.Remark) && !string.IsNullOrEmpty(acc.Nick))
                sub = acc.Nick + "  ·  " + sub;
            if (showPan)
                sub = PanUrlParser.DisplayNameOf(kind) + "  ·  " + sub;
            texts.Children.Add(new TextBlock
            {
                Text = sub,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
                // 跟随主题：暗色下 #2E7D32 那种暗绿在深底上几乎看不清，
                // 统一走 Success/TextTertiary 令牌，两套主题都达标。
                Foreground = (Brush)(TryFindResource(isCurrent
                    ? "SuccessBrush" : "TextTertiaryBrush")
                    ?? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))),
            });
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);

            // 「改名」：同家网盘两个账号昵称一样时靠它区分（备注只影响显示，不动登录信息）
            var ghostStyle = TryFindResource("GhostButton") as Style;
            var renameBtn = new Button
            {
                Content = "改名",
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (ghostStyle != null) renameBtn.Style = ghostStyle;
            renameBtn.Click += (s, ev) =>
            {
                string input = PromptText(owner, "给账号起个名字",
                    "当前昵称：" + acc.DisplayName + "（留空 = 恢复显示昵称）", acc.Remark ?? "");
                if (input == null)
                    return;
                AccountStore.SetRemark(acc.PanKind, acc.Key, input);
                Log("账号「" + acc.DisplayName + "」的备注已改为「" +
                    (string.IsNullOrEmpty(input.Trim()) ? acc.DisplayName : input.Trim()) + "」");
                rebuild();
            };
            Grid.SetColumn(renameBtn, 2);
            grid.Children.Add(renameBtn);

            var switchBtn = new Button
            {
                Content = isCurrent ? "使用中" : "切换",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(6, 0, 6, 0),
                IsEnabled = !isCurrent,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // 原来是白底描边的 SecondaryButton，而旁边的「改名」「删除」是透明幽灵按钮 ——
            // 同一行三个按钮两套语言，看着最乱的就是这里。统一成幽灵按钮。
            if (ghostStyle != null) switchBtn.Style = ghostStyle;
            switchBtn.Click += async (s, ev) =>
            {
                // 凭据在换机/换 Windows 用户后解不开（DPAPI 绑定机器+用户）。
                // 直接说清楚，别让用户点完只看到一句「登录失败」。
                if (acc.CredentialUnreadable)
                {
                    AppDialog.Show(owner,
                        "账号「" + acc.Caption + "」的登录凭据是加密保存的，"
                        + "只能在同一台电脑、同一个 Windows 用户下解密。\n\n"
                        + "现在解不开了（换了电脑、重装过系统，或换了 Windows 用户），"
                        + "请重新登录一次，登录后会重新生成一份本机可用的凭据。",
                        "凭据已失效", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                switchBtn.IsEnabled = false;
                switchBtn.Content = "切换中…";
                // 按档案里记的网盘类型登录 —— 绝不能用 _activeKind：
                // 那会拿「当前账号的网盘」冒充「这个档案的网盘」（夸克 Cookie
                // 交给迅雷客户端 = 必然失败）。
                // 🚨 2026-10-28：还必须按网盘**分流到两个不同的入口**。
                //   ApplyLoginAsync 内部无条件 new QuarkClient，只认夸克/UC 的 Cookie；
                //   阿里/迅雷的凭证（rt:/at:/xlrec:）交给它必然校验失败，
                //   而且失败分支会把当前账号卡刷成「未登录」——
                //   表现就是「账号列表里点阿里/迅雷的『切换』永远点不动」。
                PanKind accKind = (PanKind)acc.PanKind;
                try
                {
                    if (accKind == PanKind.Aliyun || accKind == PanKind.Xunlei)
                        await ApplyProviderLoginAsync(accKind, acc.Cookie, true);
                    else
                        await ApplyLoginAsync(acc.Cookie, accKind);
                }
                catch (Exception ex)
                {
                    Log("切换到账号「" + acc.DisplayName + "」失败：" + ex.Message);
                }
                owner.Close();
            };
            Grid.SetColumn(switchBtn, 3);
            grid.Children.Add(switchBtn);

            var delBtn = new Button
            {
                Content = "删除",
                Padding = new Thickness(12, 4, 12, 4),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (ghostStyle != null) delBtn.Style = ghostStyle;
            var dangerBrush = TryFindResource("DangerBrush") as Brush;
            if (dangerBrush != null) delBtn.Foreground = dangerBrush;
            delBtn.Click += (s, ev) =>
            {
                string tip = "将删除本机保存的账号「" + acc.DisplayName + "」的登录信息，" +
                             "之后要切回它需要重新登录。";
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
                if (AccountStore.Remove(acc.PanKind, acc.Key))
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
                    AppDialog.Show(owner,
                        "删除失败：账号文件正被占用。\n\n"
                        + "请关闭其他鸽子下载窗口后重试。",
                        "删除账号", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            Grid.SetColumn(delBtn, 4);
            grid.Children.Add(delBtn);

            // 2026-10-26：右键菜单已按用户要求删除 —— 三个按钮就够，右键是多余的一层。
            // （曾加过「切换/改名/删除」菜单，用 RaiseEvent 转发到按钮；现在整段移除。）
            return new Border
            {
                Child = grid,
                Padding = new Thickness(12, 9, 8, 9),
                Margin = new Thickness(0, 0, 0, 8),
                CornerRadius = new CornerRadius(11),
                // 当前账号加一圈高亮边框，扫一眼就知道在用哪个
                BorderThickness = new Thickness(isCurrent ? 1.5 : 1),
                Background = (Brush)(TryFindResource(isCurrent ? "SurfaceAltBrush" : "SurfaceBrush")
                                     ?? new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6))),
                BorderBrush = (Brush)(TryFindResource(isCurrent ? "AccentBrush" : "BorderBrushSoft")
                                     ?? new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6))),
            };
        }


        /// <summary>
        /// 一个极简的「输入一行文字」弹窗（项目里原来没有输入框对话框）。
        /// 返回 null = 用户取消；返回空串 = 用户清空了内容（调用方按"恢复默认"处理）。
        /// </summary>
        private string PromptText(Window owner, string title, string hint, string initial)
        {
            var win = new Window
            {
                Owner = owner,
                Title = title,
                ResizeMode = ResizeMode.NoResize,
            };
            // 内容高：上下留白 40 + 标题 26 + 说明最多两行 42 + 输入框 48 + 按钮行 44 ≈ 200，
            // 留 10px 余量防止说明换行时把按钮挤出窗口。
            DialogChrome.StyleWindow(win, 380, 210);

            var root = DialogChrome.DialogRoot();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 说明
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 输入框
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮

            // 标题已画在自绘标题栏里（2026-10-26），内容区不再重复。
            var hintText = DialogChrome.Hint(hint, 4, 0);
            Grid.SetRow(hintText, 1);
            root.Children.Add(hintText);

            var box = new TextBox
            {
                Text = initial ?? "",
                Height = 36,
                MaxLength = 24,
                Margin = new Thickness(0, 12, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(box, 2);
            root.Children.Add(box);

            string result = null;
            var ok = DialogChrome.PrimaryButton("保存");
            var cancel = DialogChrome.SecondaryButton("取消");
            ok.IsDefault = true;
            cancel.IsCancel = true;
            var row = DialogChrome.ButtonRow(ok, cancel);
            row.Margin = new Thickness(0, 12, 0, 0);
            row.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(row, 3);
            root.Children.Add(row);

            ok.Click += (s, ev) => { result = box.Text ?? ""; win.Close(); };
            cancel.Click += (s, ev) => win.Close();
            win.Loaded += (s, ev) => { box.Focus(); box.SelectAll(); };

            win.Content = root;
            win.ShowDialog();
            return result;
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
        /// <summary>
        /// 导出当前登录 Cookie 到桌面文件。
        /// ⚠️ 用户明确要求：**不要复制到剪贴板**（原话「无论是导出直链还是导出
        /// cookie都不要复制到粘贴板上」）—— 所以这里只写文件。
        /// 写的是**原始 Cookie 串**（`k=v; k=v`），可直接粘回「导入 Cookie」框。
        /// </summary>
        private void OnExportCookie(object sender, RoutedEventArgs e)
        {
            if (!IsLoggedInAny)
            {
                Log("尚未登录");
                return;
            }
            try
            {
                // 夸克系导出 Cookie；阿里/迅雷没有 Cookie，导出的是令牌本身，
                // 文件名与提示文案都跟着变，免得用户拿到一串令牌却以为坏掉了。
                bool quarkLike = OwnIsQuarkLike && _client != null;
                string secret = quarkLike ? _client.CookieStr : (OwnProviderToken() ?? "");
                string label = quarkLike
                    ? "Cookie"
                    : (PanUrlParser.DisplayNameOf(_activeKind) + " 令牌");
                if (string.IsNullOrWhiteSpace(secret))
                {
                    Log("当前账号没有可导出的凭证");
                    return;
                }
                string outFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "GeZi-" + (quarkLike ? "Cookie" : "Token") + "-"
                        + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(outFile, secret, new UTF8Encoding(false));

                Log("已导出 " + label + "：" + outFile);
                AppDialog.Show(this,
                    "已把当前登录 " + label + " 导出到：\n\n" + outFile +
                    "\n\n注意：它等同于账号登录凭证，请勿分享给他人。",
                    "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log("导出凭证失败：" + ex.Message);
                AppDialog.Show(this,
                    "导出失败：" + ErrorText.Describe(ex) + "。",
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
                "已在浏览器打开夸克网盘。登录后按 F12 →「网络」→ 任选一个请求，"
                + "复制请求头里的整条 Cookie，粘回上方输入框即可。\n"
                + "（也可以用浏览器扩展导出 Cookie 文件，回来点「从文件导入」）",
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
            // 阿里/迅雷的自有网盘客户端同样要释放并置空，否则登出后
            // IsLoggedInAny 仍是 true（_ownProvider 还在），界面看起来"还登录着"。
            try { _ownProvider?.Dispose(); } catch { }
            _ownProvider = null;
            // 缓存里这一家的实例也必须销毁 —— 登出就是要它彻底消失，
            // 否则下次点这家标签会从缓存里"复活"一个已登出的会话。
            DropCachedPan(_activeKind);
            // 登出同样是"换账号"：直链 / 目录缓存 / 导航快照都要作废，
            // 否则下次登录别的账号会看到上一个账号的网盘内容（见 InvalidateAccountCaches）。
            InvalidateAccountCaches();
            // 只清**当前这家**的凭证：登出 UC 不该顺手把夸克的 Cookie 也抹掉，
            // 否则用户切回夸克还得重新扫码。
            switch (_activeKind)
            {
                case PanKind.Uc:
                    _settings.UcCookie = "";
                    break;
                case PanKind.Aliyun:
                    _settings.AliyunRefreshToken = "";
                    _settings.AliyunAccessToken = "";
                    break;
                case PanKind.Xunlei:
                    _settings.XunleiPanToken = "";
                    break;
                default:
                    _settings.Cookie = "";
                    break;
            }
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


        /// <summary>
        /// 销毁并移除某一家网盘的缓存实例（登出、凭证被替换、校验失败时用）。
        /// </summary>
        private void DropCachedPan(PanKind kind)
        {
            QuarkClient qc;
            if (_quarkCache.TryGetValue(kind, out qc))
            {
                _quarkCache.Remove(kind);
                if (qc != null && !ReferenceEquals(qc, _client))
                {
                    try { qc.StopKeepAlive(); } catch { }
                    try { qc.Dispose(); } catch { }
                }
            }

            IPanProvider pp;
            if (_providerCache.TryGetValue(kind, out pp))
            {
                _providerCache.Remove(kind);
                if (pp != null && !ReferenceEquals(pp, _ownProvider))
                {
                    try { pp.Dispose(); } catch { }
                }
            }
        }


        /// <summary>
        /// 把「当前正在用」的那家网盘实例**存进缓存**（停心跳，但不销毁）。
        /// <para>
        /// 🚨 2026-10-25：用户反馈「我在我的网盘中切换网盘，在我的账号中又会重新登录，
        /// 这也太浪费性能了」。根因是切换时把旧实例 Dispose 掉了，切回来只能重新登录。
        /// 账号和凭证都没变，实例本来可以直接复用 —— 所以切走时只是「停用」。
        /// </para>
        /// <para>
        /// 必须 <c>StopKeepAlive()</c>：四家的心跳同时跑会各自定时发请求，
        /// 白白多三份流量与线程。
        /// </para>
        /// </summary>
        private void ParkCurrentPan(PanKind? replacing = null)
        {
            // replacing 有值 = 这一家马上要换成新凭证/新实例，旧的存进缓存也没人会用了
            // （下次切回来会用新实例），所以直接销毁，避免字典里堆废实例。
            bool dropQuark = replacing.HasValue && _activeKind == replacing.Value;
            bool dropProvider = replacing.HasValue && _activeKind == replacing.Value;

            if (_client != null)
            {
                try { _client.StopKeepAlive(); } catch { }
                if (IsLoggedInAny && !dropQuark)
                    _quarkCache[_activeKind] = _client;
                else
                {
                    try { _client.Dispose(); } catch { }
                }
            }

            if (_ownProvider != null)
            {
                if (IsLoggedInAny && !dropProvider)
                    _providerCache[_activeKind] = _ownProvider;
                else
                {
                    try { _ownProvider.Dispose(); } catch { }
                }
            }
        }


        /// <summary>
        /// 直接启用**缓存里已经登录好的**那家网盘，不发任何校验请求。
        /// </summary>
        /// <returns>缓存里有这家就返回 true（切换完成）；没有返回 false（调用方去走完整登录）。</returns>
        /// <remarks>
        /// 只做「指针切换 + 界面同步 + 载目录」，**不做** ValidateAsync / ResolveAccountInfoAsync
        /// —— 那两步正是「切一次卡 5~6 秒」的全部来源。
        /// </remarks>
        private async Task<bool> ActivateCachedPanAsync(PanKind kind)
        {
            QuarkClient qc;
            IPanProvider pp;
            bool haveQuark = _quarkCache.TryGetValue(kind, out qc) && qc != null;
            bool haveProvider = _providerCache.TryGetValue(kind, out pp) && pp != null;
            if (!haveQuark && !haveProvider)
                return false;

            // 取出来就从字典里移除 —— 它现在变成「当前实例」，由 _client / _ownProvider 持有。
            // 不移除的话，登出时 DropCachedPan 会因为 ReferenceEquals 保护而漏掉它（泄漏）。
            _quarkCache.Remove(kind);
            _providerCache.Remove(kind);

            // 🚨 旧的当前实例必须先**存进缓存**（不是直接扔掉）——用户随时会切回来。
            //    原来这里只 StopKeepAlive 就把 _client 置 null，实例凭空丢了：
            //    实测「夸克 → 阿里 → 夸克」第二次要重新完整登录 3~4 秒，
            //    正是因为这个坑（而阿里在缓存里，所以 UC→阿里 是秒切）。
            ParkCurrentPan();
            _client = null;
            _ownProvider = null;

            if (haveQuark)
            {
                _client = qc;
                _activeKind = kind;
                _client.LoginExpired -= _cachedLoginExpiredHandler;
                _client.LoginExpired += _cachedLoginExpiredHandler;
                _client.CookieChanged -= _cachedCookieChangedHandler;
                _client.CookieChanged += _cachedCookieChangedHandler;
                _client.StartKeepAlive();
            }
            else
            {
                _ownProvider = pp;
                _activeKind = kind;
            }

            _loginExpired = false;
            InvalidateAccountCaches();

            // 🚨 方案A（2026-10-25 用户拍板）：「我的网盘」里切换网盘 = **只切浏览**，
            //    不改「我是谁登录的」。
            //    原来这里会把 ActivePanKind / Who / AvatarUrl / CurrentAccountKey 一起改写，
            //    于是用户只是想去看看 UC 的文件，登录页的当前账号、启动自动登录的网盘
            //    全被顺带改掉 —— 用户原话「每次在我的网盘里选择不同的网盘为什么会影响我的账号登录」。
            //    现在这四项只由**真正的登录动作**（ApplyLoginAsync / ApplyProviderLoginAsync）写入。
            string nick = PanLiveNick(kind);
            if (string.IsNullOrEmpty(nick) && haveQuark && !string.IsNullOrEmpty(qc.AccountSig))
                nick = qc.AccountSig;
            if (string.IsNullOrEmpty(nick) && haveProvider && !string.IsNullOrEmpty(pp.AccountName))
                nick = pp.AccountName;
            if (string.IsNullOrEmpty(nick))
                nick = PanUrlParser.DisplayNameOf(kind);

            // 显式传 kind：这个 nick 属于「刚切到的那家」，不能让它去猜 _activeKind
            //（2026-11-02 昵称串台修复）。
            UpdateAccountCard(nick, 0, 0, kind);
            LogDetail("已切换到 " + PanUrlParser.DisplayNameOf(kind) + "（复用已登录会话，未重新登录）");

            // 记住「最后浏览的是这一家」—— 下次启动直接打开它。
            // ⚠️ 只写 LastBrowsePanKind，不碰上面注释里那四项「登录身份」，方案A 不受影响。
            RememberBrowsePan(kind);

            if (!ShareMode)
            {
                ResetNav();
                await LoadDirAsync("0");
            }
            await RefreshCapacityAsync();
            return true;
        }


        /// <summary>
        /// 记住「最后浏览的是哪家网盘」，下次启动先打开它。
        /// <para>
        /// 🚨 这个字段与「登录身份」（<c>ActivePanKind</c> / <c>Who</c> / <c>AvatarUrl</c> /
        /// <c>CurrentAccountKey</c>）**是两件事**，别合并 —— 合并会重演 2026-10-25
        /// 用户骂过的问题（「在我的网盘里选不同的网盘为什么会影响我的账号登录」）。
        /// </para>
        /// <para>
        /// 为什么要单独记：原来启动只读 <c>ActivePanKind</c>，而它只在**登录成功**时写入，
        /// 在「我的网盘」里点页签走的是浏览快路径、压根不落盘 —— 于是
        /// 「切回夸克 → 重启又跳迅雷」会一直复现，记忆从来没被更新过。
        /// </para>
        /// </summary>
        private void RememberBrowsePan(PanKind kind)
        {
            try
            {
                if (_settings.LastBrowsePanKind == (int)kind) return;
                _settings.LastBrowsePanKind = (int)kind;
                SettingsStore.Save(_settings);
            }
            catch (Exception ex)
            {
                // 记不住只是下次启动回退到旧逻辑，不该影响当前浏览
                LogDetail("记住上次浏览的网盘失败：" + ex.Message);
            }
        }


        /// <summary>
        /// 上次把续期后的 Cookie 落盘的时刻（UTC）。
        /// 用于给 <see cref="_cachedCookieChangedHandler"/> 做 60 秒节流：
        /// 401 风暴时每个请求都会触发一次续期，不能每次都全量写配置。
        /// </summary>
        private DateTime _lastCookiePersistUtc = DateTime.MinValue;

        /// <summary>缓存实例复用时挂的「登录过期」回调（与完整登录时行为一致）。</summary>
        private void _cachedLoginExpiredHandler()        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() => ShowLoginExpired("保活心跳连续失败")));
            }
            catch { }
        }


        /// <summary>
        /// 会话 Cookie 被自动续期后的落盘回调。
        /// <para>
        /// 🚨 2026-10-28：<c>QuarkClient.RefreshPuusAsync</c> 拿到新的 <c>__puus</c>
        /// 只写内存，唯一落盘点是登录成功那一段 —— 于是：
        /// ① 重启后用的还是启动时的旧 <c>__puus</c>，第一个请求 401，
        ///   而重试前有一段 <c>Task.Delay(2~4s)</c> 的抖动退避 ⇒ 「每次启动都慢几秒」；
        /// ② <c>__pus</c> 若也轮换了，旧值落盘 = 下次启动直接失效，用户被迫重新扫码。
        /// </para>
        /// <para>
        /// 事件在后台线程触发，这里只做「读当前 Cookie → 写配置/档案」，
        /// 两者内部各自有锁，不需要切 UI 线程。
        /// </para>
        /// </summary>
        private void _cachedCookieChangedHandler()
        {
            try
            {
                // 节流：401 风暴时每个请求都会触发续期，不能每次都全量写配置。
                if ((DateTime.UtcNow - _lastCookiePersistUtc).TotalSeconds < 60)
                    return;
                _lastCookiePersistUtc = DateTime.UtcNow;

                var c = _client;
                if (c == null)
                    return;
                string cookie = c.CookieStr;
                if (string.IsNullOrEmpty(cookie))
                    return;

                PanKind kind = _activeKind;
                // 只写「这一家」的字段：夸克与 UC 的 Cookie 互不相通，
                // 混着存会让下次启动拿 UC 的 Cookie 去登录夸克。
                if (kind == PanKind.Uc)
                    _settings.UcCookie = cookie;
                else if (kind == PanKind.Quark)
                    _settings.Cookie = cookie;
                else
                    return;   // 阿里/迅雷不走 Cookie，别在这里乱写
                SettingsStore.Save(_settings);

                // 账号档案里的那份也要同步，否则「切换账号」切回来的还是旧 Cookie。
                string acctKey = _settings.CurrentAccountKey;
                if (!string.IsNullOrEmpty(acctKey))
                {
                    AccountStore.Save(acctKey, _settings.Who, cookie, (int)kind);
                }

                // 🚨 2026-10-28：续期成功说明登录态其实还活着，必须撤掉「已过期」标记。
                //    原来这个标记只在**重新登录**时清，于是一次网络抖动触发保活失败后，
                //    界面会一直挂着红字「登录已过期」，而实际早就能正常用了 ——
                //    用户只能重启软件才能让它消失。
                //    事件在后台线程触发，切回 UI 线程再动界面状态。
                if (_loginExpired)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            _loginExpired = false;
                            UpdateAccountCard(PanLiveNick(_activeKind), 0, 0);
                        }
                        catch { }
                    }));
                }
            }
            catch { }
        }


        /// <summary>
        /// 校验并启用一个账号的凭证。
        /// <para>
        /// <paramref name="kind"/> 决定这次登录归属哪个网盘：
        /// 夸克与 UC 共用同一套 ucpro 协议，<see cref="QuarkClient"/> 靠 Kind
        /// 切域名/pr/请求头即可服务两家，所以这里只有一处 new，不需要两个客户端类。
        /// </para>
        /// </summary>
        private async Task ApplyLoginAsync(string cookie, PanKind kind)
        {
            try
            {
                // 新凭证进来了 → **只**作废这一家的实测结果，另外三家的「已登录」保持不动。
                InvalidatePanLive(kind);
                var client = new QuarkClient(cookie, kind);
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
                    var resolved = await ResolveAccountInfoAsync(client, cookie, valid, nick);
                    valid = resolved.valid;
                    if (!string.IsNullOrEmpty(resolved.nick)) nick = resolved.nick;
                    realAvatar = resolved.avatar;
                }

                if (valid)
                {
                    // 换账号：旧实例**不销毁**，存进缓存 —— 用户可能马上切回来
                    // （「我的网盘」四家标签来回点），销毁了就得重新登录一遍。
                    // 但这家（kind）马上就要被新实例替换，旧的那份直接销毁。
                    ParkCurrentPan(kind);

                    _client = client;
                    // 从阿里/迅雷切回夸克系：旧 provider 也必须进缓存而不是 Dispose，
                    // 理由同上（切回去要是重新登录，用户点两下就卡两次）。
                    _ownProvider = null;
                    // 账号归属的网盘跟着这次登录走 —— 账号卡片的展示名、
                    // 「我的网盘」用的是哪家的接口，全都由它决定。
                    _activeKind = kind;
                    _quarkCache[kind] = client;
                    // 保活心跳连续失败 → 登录很可能已失效。事件在后台线程上触发，
                    // 必须切回 UI 线程再动界面。
                    // ⚠️ 用固定方法而不是匿名 lambda：实例会进缓存反复复用，
                    //    匿名 lambda 没法 -=，来回切几次就会挂上好几个重复回调。
                    _client.LoginExpired -= _cachedLoginExpiredHandler;
                    _client.LoginExpired += _cachedLoginExpiredHandler;
                    _client.CookieChanged -= _cachedCookieChangedHandler;
                    _client.CookieChanged += _cachedCookieChangedHandler;
                    _client.StartKeepAlive();
                    _loginExpired = false;   // 新登录成功 → 清掉"已过期"标记
                    // 换账号后旧账号的**直链 / 目录缓存 / 两个模式的导航快照**全部作废。
                    // （原来只清了直链 → 切回「我的网盘」会显示旧账号的内容，见 InvalidateAccountCaches）
                    InvalidateAccountCaches();
                    // 凭证写回**对应网盘**的字段：两家的 Cookie 互不相通，
                    // 混着存会让下次启动用 UC 的 Cookie 去登录夸克（必然失败）。
                    if (kind == PanKind.Uc)
                        _settings.UcCookie = client.CookieStr;
                    else
                        _settings.Cookie = client.CookieStr;
                    _settings.ActivePanKind = (int)kind;
                    // 登录成功也顺带记一次「最后浏览」—— 刚登录完用户就在看这一家，
                    // 重启后留在这里最符合预期。
                    _settings.LastBrowsePanKind = (int)kind;
                    _settings.Who = nick;
                    // 记住昵称：切走之后再切回来走的是"复用缓存实例"的快速路径，
                    // 那时不会再调 ValidateAsync，昵称只能从这里取。
                    _panNick[kind] = nick ?? "";
                    // 头像：探测到的就存下来（拿不到存空串 → 界面回退"昵称首字"）
                    _settings.AvatarUrl = realAvatar ?? client.AvatarUrl ?? "";

                    // 登录成功后存一份账号档案，之后可在「切换账号」里一键切回，不用再扫码。
                    // 标识优先用夸克返回的稳定 uid，取不到时回退昵称（同名不同账号才会冲突）。
                    string acctKey = !string.IsNullOrEmpty(client.AccountSig) ? client.AccountSig : nick;
                    // 记住"当前是哪个账号" —— 账号列表用它判断哪一行的「切换」该置灰。
                    // ⚠️ 必须在 SettingsStore.Save **之前**赋值：写在 Save 之后的话
                    // 这个字段永远不会落盘，配置里会一直留着上一个账号的 key
                    // （实测踩到：登录「雨过晴」之后配置里仍是「迅雷用户」）。
                    _settings.CurrentAccountKey = acctKey ?? "";
                    SettingsStore.Save(_settings);

                    if (!string.IsNullOrEmpty(acctKey))
                    {
                        AccountStore.Save(acctKey, nick, client.CookieStr, (int)kind);
                        // 这次登录用的账号 = 这家网盘的主账号（同家其余档案自动取消标记）
                        AccountStore.SetPrimary((int)kind, acctKey);
                    }

                    // 显式传 kind：登录动作的网盘是最权威的事实来源（2026-11-02）。
                    UpdateAccountCard(nick, 0, 0, kind);
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
                    // 失败原因要**说清楚是哪一种**，否则用户只知道"失效"，
                    // 却不知道是缺 __pus（只能重新登录）还是 __puus 过期（已尝试自动刷新）。
                    LogDetail("登录校验失败原因：" + DescribeLoginFailure(cookie, nick));
                    Log("登录校验失败: " + nick);
                }
            }
            catch (Exception ex)
            {
                Log("登录出错: " + ex.Message);
            }
        }


        /// <summary>
        /// 登录校验失败时给出一句**能指导用户动作**的提示。
        /// <para>
        /// 原来一律写「Cookie 已失效，请重新登录」，把三种完全不同的情况混成了一句：
        /// ① 缺 <c>__pus</c>（长效凭证根本没抓到，只能重新登录）；
        /// ② 有 <c>__pus</c>，但 <c>__puus</c> 过期且自动续期也被拒（服务端不认了）；
        /// ③ 网络/风控抖动（等一会儿重试即可）。
        /// 用户看到笼统的"失效"只会反复重新登录，却不知道是抓取不完整。
        /// </para>
        /// </summary>
        private static string DescribeLoginFailure(string cookie, string reason)
        {
            var dict = QuarkSession.ParseCookies(cookie);
            string pus, puus;
            dict.TryGetValue(UcConstants.CookiePus, out pus);
            dict.TryGetValue(UcConstants.CookiePuus, out puus);

            if (string.IsNullOrEmpty(pus))
                return "登录凭证不完整：缺少 __pus（长效凭证），请重新登录";
            if (string.IsNullOrEmpty(puus))
                return "登录凭证不完整：缺少 __puus（会话凭证），请重新登录";

            // 两种凭证都在，说明是服务端已经作废了这个会话（或风控拦截）
            if (!string.IsNullOrEmpty(reason)
                && (reason.Contains("401") || reason.Contains("412")))
                return "登录态已被服务端作废（自动续期未成功），请重新登录";
            if (!string.IsNullOrEmpty(reason)
                && (reason.Contains("超时") || reason.Contains("网络")))
                return "网络异常，未能确认登录态，请稍后重试";

            return "Cookie 已失效，请重新登录";
        }


        /// <summary>
        /// 校验并启用一个**阿里云盘 / 迅雷云盘**账号（自有网盘）。
        /// <para>
        /// 与 <see cref="ApplyLoginAsync(string, PanKind)"/> 的区别：那两家不走
        /// <see cref="QuarkClient"/>（协议完全不同），凭证也不是 Cookie 而是一个
        /// 令牌（阿里是 refresh_token，迅雷是 pan_token）。所以这里单独一条路：
        /// 建 provider → 校验 → 存凭证 → 换账号 → 拉容量与根目录。
        /// </para>
        /// </summary>
        /// <param name="kind">只接受 Aliyun / Xunlei。</param>
        /// <param name="credential">
        /// 阿里 = <c>rt:&lt;refresh_token&gt;</c> 或 <c>at:&lt;access_token&gt;</c>（可无前缀，按 refresh_token）；
        /// 迅雷 = pan_token。
        /// </param>
        /// <param name="notifyFailure">
        /// 失败时是否弹窗告知原因。用户**主动点**「验证登录态」时为 true ——
        /// 否则失败只写左下角提示和日志，用户在设置页根本看不到任何反应
        /// （这正是"点了验证登录态根本没有用"的由来）。
        /// 内嵌登录成功后自动调用时为 false，避免登录窗口刚关就弹一个失败框。
        /// </param>
        private async Task ApplyProviderLoginAsync(PanKind kind, string credential,
            bool notifyFailure = false)
        {
            if (kind != PanKind.Aliyun && kind != PanKind.Xunlei)
            {
                Log("内部错误：ApplyProviderLoginAsync 只处理阿里/迅雷。");
                return;
            }

            // 新凭证进来了 → **只**作废这一家的实测结果，另外三家保持不动。
            InvalidatePanLive(kind);

            // 存档用的原始凭证：迅雷续期分支会把 credential 覆盖成新的 access_token
            // （丢掉了 xlrec: 里的设备号与 refresh_token），下次切换就少了信息。
            string credentialForArchive = credential;

            // 阿里云盘的两种凭证（见 WebLoginWindow.AliyunTokenScript 的返回值）：
            //   rt:… 可续期的 refresh_token，最优；
            //   at:… 网页只给了短期 access_token（阿里新版 sessionStorage 就只有它），
            //        约两小时后失效，到期后需要重新登录。
            // 没有前缀的（手动粘贴、旧配置）一律按 refresh_token 处理，保持向后兼容。
            string aliyunRt = "";
            string aliyunAt = "";
            if (kind == PanKind.Aliyun)
            {
                string raw = credential ?? "";
                if (raw.StartsWith("at:", StringComparison.Ordinal))
                    aliyunAt = raw.Substring(3);
                else if (raw.StartsWith("rt:", StringComparison.Ordinal))
                    aliyunRt = raw.Substring(3);
                else
                    aliyunRt = raw;
            }

            IPanProvider provider = null;
            try
            {
                // 迅雷网页登录给的是打包记录（xlrec:at|rt|sub|dev|cap）：
                // 设备标识必须跟着 token 一起用 —— 迅雷把登录态和设备绑定，
                // 拿随机 device_id 去请求会被判 captcha_required（HTTP 400）。
                string xunleiToken = credential;
                // ⚠️ 两个字段要分清：XunleiWebDeviceId 是网页登录时从 Cookie 的
                // deviceid 取到的**真实设备号**（与签 token 的那台一致），
                // XunleiDeviceId 是账号登录（短信）那条路用的。
                // 用错字段 = 设备对不上 = 400 captcha_required。
                string xunleiDeviceId = !string.IsNullOrEmpty(_settings.XunleiWebDeviceId)
                    ? _settings.XunleiWebDeviceId
                    : _settings.XunleiDeviceId;
                string xunleiCaptcha = _settings.XunleiCaptchaToken;
                string xlRt = null, xlSub = null;
                string recAt, recDev, recCap;
                if (kind == PanKind.Xunlei
                    && XunleiProtocol.TryParseRecord(credential ?? "",
                        out recAt, out xlRt, out xlSub, out recDev, out recCap))
                {
                    xunleiToken = recAt;
                    if (!string.IsNullOrEmpty(recDev)) xunleiDeviceId = recDev;
                    if (!string.IsNullOrEmpty(recCap)) xunleiCaptcha = recCap;

                    // 🚨🚨 2026-11-01 修复（用户反馈「迅雷网盘的相关问题」）：
                    // 网页登录给的打包记录里**带着 refresh_token**（xlrec:at|rt|sub|dev|cap 的 rt 段），
                    // 但原来解析出 xlRt 之后**从来没有保存**，直接丢弃 ——
                    // 后果是：XunleiWebRefreshToken 永远是空的 → 下面第 1981 行的续期分支
                    // 只能去试账号登录那套（那是另一个 OAuth 客户端，token 根本不通用）→
                    // 必然失败 → 每次启动都只能拿两小时寿命的 access_token 硬登录。
                    // 日志实证：GeZi.log 里只有「登录成功: 迅雷用户」，**从来没有「迅雷令牌续期成功」**，
                    // 而且 16:03:41 与 16:04:10 相隔 29 秒就又重新登录了一次。
                    // ⇒ 这里必须把 rt 存进 XunleiWebRefreshToken，续期链路才真正接通。
                    if (!string.IsNullOrEmpty(xlRt))
                        _settings.XunleiWebRefreshToken = xlRt;
                }

                // 🚨 2026-11-01 新增：`xlrt:` 前缀 = 只有 refresh_token、没有 access_token
                // （启动时 XunleiPanToken 为空、只剩续期令牌的情形，见 MainWindow.xaml.cs 的
                //  「启动选凭证」分支）。这里**先用它换一张新的 access_token**，
                // 否则拿 refresh_token 当 access_token 用，ValidateAsync 必然 401。
                if (kind == PanKind.Xunlei && !string.IsNullOrEmpty(credential)
                    && credential.StartsWith("xlrt:", StringComparison.Ordinal))
                {
                    string rt = credential.Substring(5);
                    bool isWeb = string.Equals(rt, _settings.XunleiWebRefreshToken, StringComparison.Ordinal);
                    try
                    {
                        using (var refresher = new XunleiLoginService(xunleiDeviceId))
                        {
                            XunleiLoginResult renewed = isWeb
                                ? await refresher.RefreshWebAsync(rt, _settings.XunleiWebDeviceId).ConfigureAwait(true)
                                : await refresher.RefreshAsync(rt).ConfigureAwait(true);

                            if (renewed != null && renewed.Ok
                                && !string.IsNullOrEmpty(renewed.AccessToken))
                            {
                                xunleiToken = renewed.AccessToken;
                                if (!string.IsNullOrEmpty(refresher.DeviceId))
                                    _settings.XunleiDeviceId = refresher.DeviceId;
                                if (!string.IsNullOrEmpty(renewed.RefreshToken))
                                {
                                    if (isWeb) _settings.XunleiWebRefreshToken = renewed.RefreshToken;
                                    else _settings.XunleiRefreshToken = renewed.RefreshToken;
                                }
                                Log("迅雷令牌续期成功（已用刷新令牌换新）。");
                            }
                            else
                            {
                                Log("迅雷令牌续期失败：" + (refresher.LastError ?? "需要重新登录。"));
                            }
                        }
                    }
                    catch (Exception rex)
                    {
                        Log("迅雷令牌续期异常：" + rex.Message);
                    }
                }

                provider = kind == PanKind.Aliyun
                    ? (IPanProvider)(!string.IsNullOrEmpty(aliyunAt)
                        ? new AliyunClient(null, null, null, aliyunAt,
                            _settings.AliyunDeviceId, _settings.AliyunDevicePrivateKey)
                        : new AliyunClient(aliyunRt, null, null, null,
                            _settings.AliyunDeviceId, _settings.AliyunDevicePrivateKey))
                    : (IPanProvider)AttachXunleiCaptcha(
                        new XunleiClient(xunleiToken, xunleiCaptcha, null, xunleiDeviceId));

                var (valid, nick) = await provider.ValidateAsync().ConfigureAwait(true);

                // 迅雷：pan_token 只有约两小时寿命。若校验失败但我们手里有 refresh_token，
                // 就静默换一张新的再试一次 —— 用户不该为"令牌到期"这件事重新登录。
                // 两种 refresh_token 属于两套 OAuth 客户端，刷新方式不同，所以依次尝试：
                // 先账号登录那套（带 client_secret 的表单），再网页登录那套（JSON + 桌面 UA）。
                if (!valid && kind == PanKind.Xunlei
                    && (!string.IsNullOrEmpty(_settings.XunleiRefreshToken)
                        || !string.IsNullOrEmpty(_settings.XunleiWebRefreshToken)))
                {
                    Log("迅雷令牌已过期，正在用刷新令牌自动续期…");
                    try
                    {
                        using (var refresher = new XunleiLoginService(_settings.XunleiDeviceId))
                        {
                            XunleiLoginResult renewed = null;

                            if (!string.IsNullOrEmpty(_settings.XunleiRefreshToken))
                            {
                                renewed = await refresher.RefreshAsync(_settings.XunleiRefreshToken)
                                    .ConfigureAwait(true);
                                if (renewed != null && renewed.Ok)
                                {
                                    if (!string.IsNullOrEmpty(refresher.DeviceId))
                                        _settings.XunleiDeviceId = refresher.DeviceId;
                                    if (!string.IsNullOrEmpty(renewed.RefreshToken))
                                        _settings.XunleiRefreshToken = renewed.RefreshToken;
                                }
                                else
                                {
                                    Log("账号登录刷新令牌续期失败：" + refresher.LastError);
                                    renewed = null;
                                }
                            }

                            if (renewed == null && !string.IsNullOrEmpty(_settings.XunleiWebRefreshToken))
                            {
                                renewed = await refresher.RefreshWebAsync(
                                    _settings.XunleiWebRefreshToken, _settings.XunleiWebDeviceId)
                                    .ConfigureAwait(true);
                                if (renewed != null && renewed.Ok)
                                {
                                    if (!string.IsNullOrEmpty(renewed.RefreshToken))
                                        _settings.XunleiWebRefreshToken = renewed.RefreshToken;
                                }
                                else
                                {
                                    Log("网页登录刷新令牌续期失败：" + refresher.LastError);
                                    renewed = null;
                                }
                            }

                            if (renewed != null && renewed.Ok)
                            {
                                credential = renewed.AccessToken;
                                if (XunleiTokenBox != null)
                                    XunleiTokenBox.Text = renewed.AccessToken;
                                _settings.XunleiPanToken = renewed.AccessToken;

                                try { provider.Dispose(); } catch { }
                                provider = AttachXunleiCaptcha(
                                    new XunleiClient(renewed.AccessToken, _settings.XunleiCaptchaToken,
                                        null, xunleiDeviceId));
                                var again = await provider.ValidateAsync().ConfigureAwait(true);
                                valid = again.Item1;
                                nick = again.Item2;
                                Log(valid ? "迅雷令牌续期成功。" : "迅雷令牌续期后仍无效：" + provider.LastError);
                            }
                            else
                            {
                                Log("迅雷令牌续期失败：需要重新登录。");
                            }

                            // 🚨 刷新令牌是**单次有效、用一次就轮换**的：
                            // 上面无论成功还是失败，服务端都可能已经作废旧值并下发新值。
                            // 原来只有走到方法末尾（登录成功）才 Save，一旦这次校验失败
                            // 就在下面 return 了 —— 新 refresh_token 留在内存里随窗口一起消失，
                            // 磁盘上还是那条**已作废**的旧值，下次启动必然"登录失效"。
                            // 所以这里立刻落盘。
                            try { SettingsStore.Save(_settings); } catch { }
                        }
                    }
                    catch (Exception rex)
                    {
                        Log("迅雷令牌续期异常：" + rex.Message);
                    }
                }

                if (!valid)
                {
                    // 失败原因：优先用 provider 自己记下的（令牌过期/服务端拒绝/网络不通），
                    // 拿不到再退回一句通用文案。**不能只说"无效"** —— 用户没法据此行动。
                    string reason = provider.LastError;
                    string panName = PanUrlParser.DisplayNameOf(kind);
                    string hint = string.IsNullOrEmpty(reason)
                        ? panName + " 登录态无效，请重新登录"
                        : panName + " 登录态无效：" + reason;

                    try { provider.Dispose(); } catch { }
                    UpdateAccountCard(null, 0, 0);
                    LogDetail(hint);
                    Log(panName + " 登录校验失败" + (string.IsNullOrEmpty(reason) ? "" : "：" + reason));

                    if (notifyFailure)
                    {
                        AppDialog.Show(this,
                            hint + "\n\n请重新点「" + LoginEntryName + "」；"
                            + "手动粘贴的话注意要把整条凭证复制完整。",
                            panName + " 登录失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    return;
                }

                // 换账号：旧的夸克客户端与旧的 provider 都**不销毁**，存进缓存 ——
                // 用户可能在「我的网盘」四家标签之间来回切，销毁了就得重新登录一遍。
                // 但这家（kind）马上被新实例替换，旧的那份直接销毁。
                ParkCurrentPan(kind);

                _client = null;                  // 阿里/迅雷不用 QuarkClient
                _ownProvider = provider;
                _activeKind = kind;
                _providerCache[kind] = provider;

                InvalidateAccountCaches();

                // 凭证写回对应网盘的字段。
                // 阿里两个字段互斥：能续期就存 refresh_token，只有短期令牌就存 access_token。
                if (kind == PanKind.Aliyun)
                {
                    _settings.AliyunRefreshToken = aliyunRt;
                    _settings.AliyunAccessToken = aliyunAt;
                    // 设备身份必须落盘：每次启动都生成新设备会被阿里当成"一堆新设备登录"，
                    // 既污染官网的「登录设备」列表，也更容易触发风控。
                    var aliClient = provider as AliyunClient;
                    if (aliClient != null)
                    {
                        if (!string.IsNullOrEmpty(aliClient.DeviceId))
                            _settings.AliyunDeviceId = aliClient.DeviceId;
                        if (!string.IsNullOrEmpty(aliClient.DevicePrivateKey))
                            _settings.AliyunDevicePrivateKey = aliClient.DevicePrivateKey;
                    }
                }
                else
                    _settings.XunleiPanToken = credential;
                _settings.ActivePanKind = (int)kind;
                // 登录成功也顺带记一次「最后浏览」（同上：刚登录完就在看这一家）。
                _settings.LastBrowsePanKind = (int)kind;
                _settings.Who = string.IsNullOrEmpty(nick) ? PanUrlParser.DisplayNameOf(kind) : nick;
                // 记住昵称：切走后再切回来走快速路径，不会再 ValidateAsync。
                _panNick[kind] = _settings.Who;
                // 头像：阿里从 /v2/user/get 的 avatar、迅雷从 /drive/v1/about 的头像字段取。
                // 该账号没设头像时 provider 返回空串 → 界面回退"昵称首字"头像。
                _settings.AvatarUrl = provider.AvatarUrl ?? "";
                _settings.CurrentAccountKey = _settings.Who;
                SettingsStore.Save(_settings);

                // 存一份账号档案 —— 原来这里**只有夸克/UC 会存**（ApplyLoginAsync 里那处），
                // 阿里/迅雷登录成功后从不写档案，结果「账号列表」里永远是空的，
                // 用户看到的「阿里里没有账号了」就是这个原因。
                // 标识优先用 provider 给的账号名，取不到回退昵称。
                // 🚨🚨 2026-11-03 修「迅雷账号列表冒出 4 个『迅雷用户』」：
                //   根因：迅雷网页登录**拿不到真实昵称**（AccountName 恒空），
                //   于是 acctKey 回退成昵称「迅雷用户」—— 四个账号算出同一个 Key，
                //   靠 AccountStore 的「同名不同号加后缀」才没互相覆盖，
                //   结果就是 迅雷用户 / -8715cdad / -9e173f7f / -03064786 四条。
                //   ⇒ 阿里/迅雷必须用**设备号**（稳定）参与 Key 生成。
                string acctKey = provider.AccountName;
                if (string.IsNullOrEmpty(acctKey))
                {
                    string dev = AccountStore.ExtractDeviceIdForDisplay(credentialForArchive ?? "");
                    acctKey = string.IsNullOrEmpty(dev)
                        ? _settings.Who
                        : _settings.Who + "-" + dev.Substring(0, Math.Min(8, dev.Length));
                }
                if (!string.IsNullOrEmpty(acctKey))
                {
                    AccountStore.Save(acctKey, _settings.Who, credentialForArchive ?? "", (int)kind);
                    // 同上：这次登录的账号即这家网盘的主账号
                    AccountStore.SetPrimary((int)kind, acctKey);
                }
                // 校验那一步可能已经轮换过 refresh_token，这里再同步一次
                PersistProviderToken();

                // 显式传 kind：这里刚登录的是 kind 那家，不能用可变的 _activeKind。
                UpdateAccountCard(_settings.Who, 0, 0, kind);
                Log("登录成功: " + _settings.Who + "（" + ActivePanDisplayName + "）");
                if (kind == PanKind.Aliyun && !string.IsNullOrEmpty(aliyunAt))
                    Log("提示：本次只拿到阿里的短期令牌（无 refresh_token），约两小时后失效，届时需重新登录。");
                // 设备会话建不起来时「我的网盘」列不了目录，必须当场说清楚，
                // 否则用户会以为登录成功就等于能用了。
                var aliForNote = provider as AliyunClient;
                if (aliForNote != null && !string.IsNullOrEmpty(aliForNote.DeviceSessionNote))
                {
                    Log("阿里云盘设备会话未建立：" + aliForNote.DeviceSessionNote);
                    LogDetail("已登录（设备会话未建立，网盘列表可能打不开）");
                }

                if (!ShareMode)
                {
                    ResetNav();
                    await LoadDirAsync("0");
                }

                await RefreshCapacityAsync();
            }
            catch (Exception ex)
            {
                if (provider != null && !ReferenceEquals(provider, _ownProvider))
                {
                    try { provider.Dispose(); } catch { }
                }
                Log("登录出错: " + ex.Message);
                if (notifyFailure)
                {
                    AppDialog.Show(this,
                        PanUrlParser.DisplayNameOf(kind) + " 登录失败："
                        + ErrorText.Describe(ex) + "。",
                        "登录失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
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
                // 侧栏账号卡已删除，这里只把「当前状态」卡刷成未登录态。
                // 「登录已过期」这几个字由 BuildPanStateRow 按 _loginExpired 自己渲染，
                // 不再需要在这里涂红某个控件。
                SyncLoginPageState(null, 0, 0);
            }
            catch { }

            Log("登录已过期：" + (reason ?? "") + " 请到「登录」页重新扫码。");
            try
            {
                _tray?.ShowBalloon("登录已过期",
                    ActivePanDisplayName + " 登录状态已失效，请打开主窗口到「登录」页重新登录。");
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
            if (_loginExpired) return;
            try { await RefreshCapacityAsync(); } catch { }
        }


        /// <summary>
        /// 当前登录账号的昵称（按网盘各自取）。
        /// <para>
        /// 🚨 不能直接用 <c>_settings.Who</c>：它是「最后登录的那个账号」的昵称，
        /// 四家网盘来回切换后它和 <see cref="_activeKind"/> 不是一回事 ——
        /// 用户实测的「夸克网盘 + 迅雷用户」串位就是它造成的。
        /// </para>
        /// </summary>
        private string ActivePanNick()
        {
            string nick = PanLiveNick(_activeKind);
            if (!string.IsNullOrEmpty(nick))
                return nick;
            return _settings.Who ?? "";
        }


        private async Task RefreshCapacityAsync()
        {
            try
            {
                var (total, used) = await OwnCapacityAsync();
                if (total > 0)
                {
                    // 容量条已经显示了用量，这里只更新卡片（昵称保持当前值）。
                    UpdateAccountCard(ActivePanNick(), used, total);
                }
                else
                {
                    UpdateAccountCard(ActivePanNick(), 0, 0);
                }
            }
            catch { }
        }


        /// <summary>
        /// 账号状态的**唯一入口**：昵称与容量进「登录」页的「当前状态」卡。
        /// <para>
        /// 侧栏左下角那张账号卡已按用户要求删除（「软件左下角的账号卡不需要了」），
        /// 所以这里不再碰任何控件，只把状态转给 <see cref="SyncLoginPageState"/>。
        /// 方法名与签名保持不变 —— 它有 10 个调用点，改名只会制造无谓的改动面。
        /// </para>
        /// </summary>
        /// <param name="nick">昵称；null 或空表示未登录</param>
        /// <param name="used">已用容量（字节）</param>
        /// <param name="total">总容量（字节）；≤0 表示未知</param>
        /// <param name="kind">
        /// 这个昵称/容量属于哪家网盘。null = 当前账号那家。
        /// <para>
        /// 🚨 2026-11-02：必须支持显式指定，否则「登录阿里时顺手刷新夸克容量」
        /// 这类调用会把阿里的昵称写进夸克的槽位（见 SyncLoginPageState 的注释）。
        /// </para>
        /// </param>
        private void UpdateAccountCard(string nick, long used, long total, PanKind? kind = null)
        {
            SyncLoginPageState(nick, used, total, kind);
        }
    }
}
