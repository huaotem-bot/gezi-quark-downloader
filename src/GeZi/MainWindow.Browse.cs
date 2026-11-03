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
// 职责：网盘浏览：目录加载、导航面包屑、文件选择

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

        // ---------------- 自有网盘访问层（当前账号） ----------------
        // 四个 Own* 助手把"夸克系 / 阿里 / 迅雷"的差异挡在这里，
        // 上层（目录加载、容量、取直链、递归求和）只认这几个方法。
        // ⚠️ 它们都只服务「我的网盘」；分享模式另有 _panProvider 那条路。

        /// <summary>列当前账号网盘的某个目录（全部条目，不分页）。</summary>
        private async Task<IReadOnlyList<QuarkFileEntry>> OwnListDirAsync(string fid)
        {
            if (OwnIsQuarkLike)
                return await _client.ListDirAsync(fid).ConfigureAwait(true);

            if (_ownProvider == null)
                throw new InvalidOperationException(ActivePanDisplayName + " 客户端未就绪，请重新登录。");
            return await _ownProvider.ListOwnDirAsync(fid).ConfigureAwait(true);
        }


        /// <summary>
        /// 列当前账号网盘目录的**一页**。阿里/迅雷的客户端一次性取完整个目录，
        /// 所以只认第 1 页、HasMore=false —— 与分享模式的处理保持一致。
        /// </summary>
        private async Task<QuarkClient.DirPage> OwnListDirPageAsync(string fid, int page)
        {
            if (OwnIsQuarkLike)
            {
                // 2026-11-01 修复「切换网盘后一直卡在正在加载」（用户实测截图）：
                //   原来这里直接解引用 _client，没有判空。
                //   而 ActivateCachedPanAsync 切换网盘时会先 _client = null 再赋新值，
                //   目录加载的 await 续体一旦落在那个窗口里就是 NullReferenceException。
                //   异常被 LoadDirAsync 的 catch 吞掉（只写日志），界面就永远停在转圈。
                //   这里先判空：拿不到客户端就返回空页（而不是抛异常），
                //   让界面正常显示空状态，用户至少能看到这个目录是空的而不是无限转圈。
                var c = _client;
                if (c == null)
                {
                    LogDetail("目录加载被跳过：当前没有可用的网盘客户端（可能正在切换网盘）。");
                    return new QuarkClient.DirPage();
                }
                return await c.ListDirPageAsync(fid, page).ConfigureAwait(true);
            }

            var one = new QuarkClient.DirPage();
            if (page > 1)
                return one;
            var entries = await OwnListDirAsync(fid).ConfigureAwait(true);
            if (entries != null)
                one.Items.AddRange(entries);
            one.HasMore = false;
            return one;
        }


        // ---------------- 模式切换 ----------------

        private async void OnModeChanged(object sender, RoutedEventArgs e)
        {
            if (SharePanel == null)
                return;

            bool nowShare = ShareMode;

            // ⚠️ 这里原来有一个「当前账号能不能浏览自有网盘」的拦截弹窗
            //    （`if (!nowShare && !CanBrowseOwnDrive)` → 弹「暂不支持」并把
            //    分段控件退回分享模式）。四家网盘都实现了
            //    ListOwnDirAsync / GetSpaceAsync / GetOwnDownloadLinksAsync 之后，
            //    那个属性恒为 true，分支永远不会进 —— 是死代码，已删除。
            //    （留这段注释是防止有人照着旧记忆把它加回来。）

            SharePanel.Visibility = nowShare ? Visibility.Visible : Visibility.Collapsed;
            // 「我的网盘」专属的四家网盘切换条：分享模式下没有意义
            // （分享链接自己就带着网盘类型，不需要选）。
            if (DrivePanTabsHost != null)
                DrivePanTabsHost.Visibility = nowShare ? Visibility.Collapsed : Visibility.Visible;
            if (!nowShare)
                RefreshDrivePanTabs();

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
                if (!IsLoggedInAny)
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

            // 🚨 2026-10-25 修「解析完分享再切到我的网盘，分享里的文件会显示在我的网盘里」：
            //    原来这里只换了 _files，**完全没管 _curDir**。
            //    _curDir 还指着**上一个模式的目录**（解析分享时就是分享目录），
            //    而 LoadMoreItems 的判据是 `_files.Count < _curDir.Items.Count`
            //    → 用户一滚动就 RevealNextBatch()，从**分享目录**接着往下取，
            //    把分享的文件追加进 _files。看起来就像"我的网盘里凭空多了分享的文件"，
            //    而且没有真转存（确实没有），刷新几下（走 ApplyDirView 重设 _curDir）就恢复正常。
            //    片数多的分享目录（没露完）必然命中，所以"解析了任何东西再进任何网盘都会串"。
            //    修法：把快照内容同步成一份"已全部揭示"的目录条目，
            //    RevealCount 顶到 Items.Count → RevealNextBatch 里 to<=from 直接返回，永不追加。
            _curDir = new DirCacheEntry
            {
                Items = new List<ShareFileItem>(s.Files),
                RevealCount = s.Files.Count,
                HasMore = false,
                NextPage = 2
            };
            // 上一轮没算完的目录大小属于别的目录，作废重来
            ResetDirSizeScan();
            UpdateDirMoreHint();

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
        /// 「这是夸克口令」的共用文案 —— 解析入口和扫码识别入口用的是同一个提示，
        /// 原来两处各写一份（一份 5 行、一份 4 行），内容还不一致。
        /// </summary>
        internal static string KoulingText(string kouling)
        {
            return "这是一条夸克口令，不是分享链接，本工具无法直接解析。\n\n"
                 + "请到夸克 App 里打开它，进入分享页后点「分享」→「复制链接」，"
                 + "再把链接粘回这里。\n"
                 + "（对方同时给了链接和提取码的话，整段粘贴进来也能自动识别。）";
        }


        /// <summary>
        /// 「解析」是否正在跑。
        /// <para>
        /// 🚨 为什么需要它：<c>BeginBusy</c> 那道闸门原本写在方法**中段**
        /// （副作用之后），而副作用包括
        /// ① 把 <c>_panKind</c> 改成这次的网盘；
        /// ② <c>_panProvider.Dispose()</c> 并置空；
        /// ③ <c>_shareClient.Dispose()</c> 并置空。
        /// 而 <c>BeginBusy</c> 只是把按钮 <c>IsHitTestVisible = false</c> ——
        /// **只挡鼠标，挡不住键盘**：焦点在「解析」按钮上按空格/回车照样进第二次，
        /// 于是第二次把第一次**正在用**的客户端 Dispose 掉 →
        /// 第一次的 await 回来抛 <c>ObjectDisposedException</c> →
        /// 全局异常处理弹「程序遇到一个内部错误」。
        /// </para>
        /// <para>所以闸门必须提到方法第一行，早于任何副作用。</para>
        /// </summary>
        private bool _parseInFlight;

        private async void OnParseClick(object sender, RoutedEventArgs e)
        {
            if (_parseInFlight)
            {
                Log("正在解析，请稍候…");
                return;
            }

            _parseInFlight = true;
            try
            {
                await ParseShareCoreAsync().ConfigureAwait(true);
            }
            finally
            {
                _parseInFlight = false;
            }
        }

        /// <summary>解析分享的实际流程（闸门已在 <see cref="OnParseClick"/> 里）。</summary>
        private async Task ParseShareCoreAsync()
        {
            string url = LinkBox.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                Log("请先粘贴分享链接");
                return;
            }

            // 🚨 解析分享必须处于「分享链接」模式。
            //    FetchDirPageAsync 的第一句是 `if (!ShareMode) return OwnListDirPageAsync(...)`，
            //    也就是在「我的网盘」标签下点解析会去列**自己网盘**的目录 —— 四家全会失败
            //    （以前夸克"能用"只是因为启动默认值恰好是分享模式，不是代码保证）。
            if (ShareModeRadio != null && ShareModeRadio.IsChecked != true)
                ShareModeRadio.IsChecked = true;

            // 多网盘入口：先按域名判断是不是 UC / 阿里 / 迅雷。
            // 只有**阿里 / 迅雷**才走各自独立的 IPanProvider。
            //
            // 🚨 UC 必须跟夸克走同一条链路（2026-10-25 修）：
            //    UC 与夸克是同一套 ucpro 后端，本程序里 UC 一直复用 QuarkClient
            //    （构造参数带 kind）。之前这里把 UC 也推给 ParseOtherPanAsync，
            //    等于用 UcDriveClient 那套**独立、未被实测过**的实现去替代
            //    已验证可用的夸克链路 —— 用户实测结果就是「UC 解析不了链接」。
            //    UC 支持免转存（与夸克同源），走夸克链路天然就有这个能力。
            var otherPan = PanUrlParser.Parse(url);
            if (otherPan != null
                && otherPan.Kind != PanKind.Quark
                && otherPan.Kind != PanKind.Uc)
            {
                await ParseOtherPanAsync(otherPan).ConfigureAwait(true);
                return;
            }

            // UC 走夸克链路时，只是把「用哪份 Cookie、打哪个域」换成 UC 的。
            var parseKind = otherPan != null ? otherPan.Kind : PanKind.Quark;
            _panKind = parseKind;
            _panShareRef = null;
            if (_panProvider != null)
            {
                try { _panProvider.Dispose(); } catch { }
                _panProvider = null;
            }

            var info = ShareUrlParser.Parse(url);
            if (info.IsKouling)
            {
                // 夸克口令（形如 /~469d3M9zTu~:/）**不是**分享链接，也没有公开的
                // 「口令 → 链接」接口（查过官方开放平台与非官方 API 文档，分享相关
                // 接口只接受 pwd_id）。所以这里不装作能解析，直接给出可操作的指引。
                Log("这是夸克口令（/~" + info.Kouling + "~:/），不是分享链接，无法直接解析。");
                AppDialog.Show(this, KoulingText(info.Kouling),
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
            // 🚨 2026-11-03：记下**原始分享链接**（续传的百分百兜底）。
            //   PwdId 只是 12 位短码，丢了就再也找不回这个分享；
            //   完整链接可以随时重新解析、重新拿 stoken + share_fid_token。
            //   优先用规范化后的链接（去掉口令文本、补全协议），没有就退回输入框原文。
            //   （ShareUrlInfo 没有规范化链接字段，直接用输入框原文 ——
            //    它本来就是用户粘的那段，重新解析时原样喂回去即可。）
            _shareUrl = LinkBox != null ? LinkBox.Text.Trim() : null;
            // 提取码留下字段副本：下载器回调（线程池线程）不能读 PassBox，
            // 而自动重解析分享（换新 stoken）必须带上它。
            _passcode = passcode;
            _linkCache.Clear();   // 换分享后旧 fid 的直链不再适用
            // 目录缓存与大小缓存同理：fid 是分享内的编号，换了分享再留着会张冠李戴。
            _dirCache.Clear();
            _dirSizeCache.Clear();

            // ⚠️ 这里**绝不能**动 _client —— 那是"当前登录账号"的客户端（账号卡片、
            //    我的网盘、容量都靠它）。早先这里写的是 `_client?.Dispose();
            //    _client = new QuarkClient(_settings.Cookie)`，结果登录着 UC / 阿里 /
            //    迅雷 时一解析夸克分享，当前账号就被顶掉，于是用户以为"必须提前切账号"。
            //    现在分享用独立的 _shareClient：当前账号本来就是夸克就直接复用，不新建。
            if (_shareClient != null)
            {
                try { _shareClient.Dispose(); } catch { }
                _shareClient = null;
            }
            if (_client == null || _client.Kind != parseKind)
            {
                // 当前账号不是这家（或还没登录）→ 单独建一个只用这家 Cookie 的客户端。
                // UC 用 UcCookie，夸克用 Cookie —— 两者是不同的值、不同的域，绝不能混用。
                string shareCookie = parseKind == PanKind.Uc
                    ? (_settings.UcCookie ?? "")
                    : (_settings.Cookie ?? "");
                _shareClient = new QuarkClient(shareCookie, parseKind);
            }

            // 解析要发两次网络请求（换 stoken + 拉目录），期间必须让用户看到"在忙"
            long t0 = BusyClock();
            if (!BeginBusy(ParseBtn, "解析中")) return;
            SetBusy(true);
            try
            {
                _stoken = await ShareClient.GetStokenAsync(info.PwdId, passcode);
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


        /// <summary>
        /// 解析非夸克网盘（UC / 阿里 / 迅雷）的分享。
        /// <para>
        /// 与夸克流程的区别：这些网盘还没有扫码登录，凭证由用户在设置页手动粘贴，
        /// 所以"未登录"时必须给出**去哪拿凭证**的指引，而不是一句"解析失败"。
        /// </para>
        /// </summary>
        private async Task ParseOtherPanAsync(PanShareRef shareRef)
        {
            string panName = PanUrlParser.DisplayNameOf(shareRef.Kind);

            // ① 拿凭证建客户端
            IPanProvider provider;
            try
            {
                provider = CreateProviderFor(shareRef.Kind);
            }
            catch (Exception ex)
            {
                Log(panName + "客户端创建失败: " + ex.Message);
                return;
            }

            if (!provider.IsLoggedIn)
            {
                // 阿里「一条凭证都没有」时不该弹"尚未配置凭证"的框 —— 它的凭证可以
                // 靠自动重开内嵌登录拿到，让自动续期去换一张新的更省事。
                // （注意：持有短期 access_token 的情况 IsLoggedIn 已为 true，
                //   真正过期会在取数时由 EnsureAccessTokenAsync 给出明确提示。）
                if (shareRef.Kind == PanKind.Aliyun)
                {
                    provider.Dispose();
                    TryAutoRenewAliyunCredential("解析时缺少可用令牌");
                    Log("阿里云盘令牌已过期，正在后台自动续期；完成后请再点一次「解析」。");
                    return;
                }

                string hint = PanProviderRegistry.CredentialHint(shareRef.Kind);
                Log(panName + "尚未配置登录凭证（需要：" + hint + "）。");
                // ⚠️ 指引必须指向**现在**的入口：设置页那块「多网盘账号」已整块删除，
                //    四张网盘卡现在只在「登录」页（2026-10-26 修正过期指引）。
                AppDialog.Show(this,
                    "还没有登录" + panName + "。\n\n"
                    + "请到「登录」页，点" + panName + "卡片上的「内嵌登录」登录一次即可。",
                    panName + "未登录", MessageBoxButton.OK, MessageBoxImage.Information);
                provider.Dispose();
                return;
            }

            // ② 切到多网盘态，清掉上一个分享的所有缓存
            _panProvider?.Dispose();
            _panProvider = provider;
            _panKind = shareRef.Kind;
            _panShareRef = shareRef;
            _passcode = shareRef.Passcode ?? "";

            // 2026-11-02：把分享 ID 同步写进 _pwdId。
            //   原来这里只设 _panShareRef，没设 _pwdId ——
            //   而 BuildJobs 构造 PendingInfo 时写的是 PwdId = _pwdId，
            //   于是阿里/迅雷分享任务的「未完成记录」里 PwdId 永远是空的。
            //   后果：这类记录被 ResumePolicy 判成「缺分享凭证」→ 永远不可续传。
            //   分享 ID 本来就是现成的（shareRef.ShareId），没有理由不存。
            _pwdId = shareRef.ShareId ?? "";
            // 🚨 2026-11-03：同样记下原始链接（续传兜底）。
            //   阿里/迅雷的 PwdId 尤其重要 —— 它们的 provider 内部要先转存，
            //   而转存需要 share_id；丢了 share_id 就只能靠原始链接重解析。
            _shareUrl = LinkBox != null ? LinkBox.Text.Trim() : null;
            _stoken = "";   // 阿里/迅雷不走 stoken 那套（provider 内部自己处理）
            _linkCache.Clear();
            _dirCache.Clear();
            _dirSizeCache.Clear();

            long t0 = BusyClock();
            if (!BeginBusy(ParseBtn, "解析中")) return;
            SetBusy(true);
            try
            {
                ResetNav();
                // 根目录 ID 各网盘不一样，不能一律用 "0"：
                //   夸克 / UC → "0"
                //   阿里云盘  → "root"（refs/wenxi aliyun.dart:535 rootId: 'root'）
                //   迅雷云盘  → ""（refs/wenxi xunlei.dart:508 rootId: ''）
                // 早先这里写死 "0"，导致阿里/迅雷第一层目录就报错。
                _curFid = RootShareFidOf(shareRef.Kind, shareRef.StartFid);
                _startFid = _curFid;
                await LoadDirAsync(_curFid);
                // 🚨 别对阿里/迅雷说「免转存」—— 它们必须转存才能取直链（用户已明确指出这句误导）。
                bool canFree = shareRef.Kind == PanKind.Quark || shareRef.Kind == PanKind.Uc;
                Log("解析成功（" + panName + (canFree ? "，支持免转存" : "，下载时自动转存")
                    + "）。双击文件夹进入子目录；选中文件后点「下载选中」即可下载。");
                NotifyIfHidden("已解析 " + panName + " 分享",
                    _files.Count > 0
                        ? "共 " + _files.Count + " 个文件。双击托盘图标打开窗口即可下载。"
                        : "该分享的当前目录是空的（文件可能在子目录里）。");
            }
            catch (Exception ex)
            {
                Log(panName + "解析失败: " + ex.Message);
                NotifyIfHidden(panName + "解析失败", ex.Message);

                // 解析失败最常见的原因就是令牌过期（服务端 401 / token invalid）。
                // 阿里没有 refresh 接口，只能重开内嵌登录换新的 —— 这里顺手触发，
                // 用户下次点「解析」时凭证通常已经换好了。
                if (shareRef.Kind == PanKind.Aliyun)
                    TryAutoRenewAliyunCredential("解析失败：" + ex.Message);
            }
            finally
            {
                SetBusy(false);
                await EndBusyAsync(ParseBtn, t0);
            }
        }


        /// <summary>
        /// 分享浏览的「根目录 ID」。各家协议不一样，写死一个值必然有一家挂掉。
        /// <para>
        /// · 夸克 / UC：<c>"0"</c>（pdir_fid / pdir_fid 的根约定）
        /// · 阿里云盘：<c>"root"</c>（refs/wenxi aliyun.dart:535 <c>rootId: 'root'</c>）
        /// · 迅雷云盘：<c>""</c>（refs/wenxi xunlei.dart:508 <c>rootId: ''</c>；
        ///   YunX 的 <c>getShareDetail</c> 也是 parent_id 空串表示根）
        /// </para>
        /// 链接里带了子目录 fid 时优先用它（目前只有夸克/UC 的
        /// <c>/s/{id}/{fid}</c> 形态能被 <see cref="PanUrlParser"/> 抓到）。
        /// </summary>
        private static string RootShareFidOf(PanKind kind, string startFid)
        {
            // 子目录 fid 只对夸克/UC 有意义；其它两家即便抓到了也不该用。
            if (kind == PanKind.Quark || kind == PanKind.Uc)
                return string.IsNullOrEmpty(startFid) ? "0" : startFid;

            switch (kind)
            {
                case PanKind.Aliyun: return "root";
                case PanKind.Xunlei: return "";
                default: return "0";
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
        /// 缓存键。**必须带上模式**：分享的根 fid 和自己网盘的根 fid 都是 "0"，
        /// 不带前缀两个模式会串台（切过去会看到另一个模式的目录）。
        /// <para>
        /// 2026-10-25 又加上**当前账号指纹**：同一家网盘切到另一个账号时，
        /// 目录内容完全不同，但 fid 还是那套（根目录都叫 "0"）。
        /// 只带模式前缀的话，切账号后会立刻命中上一个账号的缓存 ——
        /// 用户看到的是别人的网盘内容，而且看起来"切换很快"其实是错的。
        /// 见 <see cref="DirCacheAccountTag"/>。
        /// </para>
        /// </summary>
        private string DirCacheKey(string fid)
        {
            return (ShareMode ? "s:" : "d:" + DirCacheAccountTag + ":") + (fid ?? "");
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
                // 2026-11-01：失败时不能只写日志 —— 用户看到的是目录区无限转圈，
                //   完全不知道发生了什么。这里把原因也写进界面（空状态区），
                //   并明确说"可以点刷新重试"，避免让人以为软件卡死。
                Log("加载目录失败: " + ex.Message);
                try
                {
                    _files.Clear();
                    if (FileEmptyText != null)
                        FileEmptyText.Text = "目录加载失败：" + ErrorText.Describe(ex) + "，可以点右上角「刷新」重试。";
                }
                catch { }
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


        /// <summary>大小缓存的键：与目录缓存同理，带模式前缀 + 账号指纹，免得两个模式/两个账号串台。</summary>
        private string DirSizeKey(string fid)
        {
            // 分享模式还要带上 pwdId：不同分享的 fid 空间是**各自独立**的，
            // 同一个 fid 在两个分享里可能是完全不同的两个目录。
            return (ShareMode ? "s:" + (_pwdId ?? "") + ":" : "d:" + DirCacheAccountTag + ":")
                 + (fid ?? "");
        }


        /// <summary>
        /// 当前账号的缓存指纹（形如 <c>0x1a2b3c4d</c>：网盘序号 + 凭证的 FNV-1a 哈希）。
        /// <para>
        /// 只用于**缓存键隔离**，不参与任何鉴权判断；用哈希而不是明文，
        /// 是为了避免把 Cookie 写进字典键、日志、异常信息里。
        /// </para>
        /// <para>
        /// 凭证一变（重新登录 / 切换账号 / 粘贴新值）必须由
        /// <c>InvalidateAccountCaches()</c> 置空重算，否则新旧账号会共用同一个键。
        /// </para>
        /// </summary>
        private string DirCacheAccountTag
        {
            get
            {
                if (_dirCacheAccountTag != null)
                    return _dirCacheAccountTag;

                string cred = "";
                if (IsLoggedInAny && _settings != null)
                {
                    switch (_activeKind)
                    {
                        case PanKind.Uc: cred = _settings.UcCookie ?? ""; break;
                        case PanKind.Aliyun:
                            cred = !string.IsNullOrEmpty(_settings.AliyunRefreshToken)
                                ? _settings.AliyunRefreshToken
                                : (_settings.AliyunAccessToken ?? "");
                            break;
                        case PanKind.Xunlei: cred = _settings.XunleiPanToken ?? ""; break;
                        default: cred = _settings.Cookie ?? ""; break;
                    }
                }

                // FNV-1a 32 位：够区分账号，且不可能从中还原出凭证。
                uint h = 2166136261;
                for (int i = 0; i < cred.Length; i++)
                {
                    h ^= cred[i];
                    h *= 16777619;
                }
                _dirCacheAccountTag = ((int)_activeKind).ToString() + "x" + h.ToString("x8");
                return _dirCacheAccountTag;
            }
        }

        private string _dirCacheAccountTag;


        private async Task DrainDirSizeQueueAsync(CancellationToken ct, DirSizeScanContext ctx)
        {
            try
            {
                while (!ct.IsCancellationRequested && _dirSizeQueue.Count > 0)
                {
                    // 🚨 模式/账号/分享已经换了 → 整轮作废。
                    //    绝不能用**新网盘的客户端**去请求**旧网盘的 fid**：
                    //    迅雷会报 file_not_found，夸克会报 guest missing pwd_id or stoken，
                    //    然后被下面的 catch 记成「这个文件夹算不出来」——日志里就是
                    //    「计算文件夹大小失败（来自：分享）」，看起来像 bug 其实是用错了客户端。
                    if (!IsDirSizeContextCurrent(ctx))
                        return;

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
                        long total = await SumDirAsync(d.Fid, ct, () => _dirSizeBudget-- > 0, ctx);
                        if (ct.IsCancellationRequested) return;
                        if (!IsDirSizeContextCurrent(ctx)) return;
                        _dirSizeCache[DirSizeKey(d.Fid)] = total;
                        d.DirSize = total;
                    }
                    catch (OperationCanceledException)
                    {
                        // 换目录/换网盘导致的取消：不是失败，不标红
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (ct.IsCancellationRequested) return;
                        if (!IsDirSizeContextCurrent(ctx)) return;
                        d.DirSizeFailed = true;
                        LogDetail("计算文件夹大小失败（" + d.Name + "）：" + ex.Message);
                    }
                }
            }
            finally
            {
                // 只有自己这一代才能放掉「正在跑」标记：
                // 否则旧循环退出时会把新一轮的标记清掉 → 两个循环同时跑。
                if (ctx != null && ctx.Gen == _dirSizeGen)
                    _dirSizeRunning = false;
            }
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

            // ⚠️ 「免转存下载」的可见性**不在这里设** —— 见 OnFileContextMenuOpened。
            //    这里只负责"右键时把该行选中"（多选时保留已选）。
        }


        /// <summary>
        /// 文件列表右键菜单**即将显示**时，按当前模式/网盘决定「免转存下载」是否出现。
        /// <para>
        /// 🚨 2026-11-01（用户报「支持免转存的两个网盘右键文件不能出现免转存选项了」）：
        /// 原来这段逻辑写在 ListBox 的 <c>MouseRightButtonUp</c> 里 —— 但 WPF 的
        /// <see cref="System.Windows.Controls.ContextMenu"/> 在鼠标按下到抬起的过程中
        /// 就已经弹出并完成布局，等到 Up 事件里再改 <c>Visibility</c>，
        /// 菜单早已渲染完毕，改了也看不见（表现为"选项一直不出现"）。
        /// ⇒ 必须挂 <c>ContextMenu.Opened</c>，在菜单**即将显示**时同步。
        /// 这与任务列表的 <see cref="OnTaskContextMenuOpened"/> 是同一套做法。
        /// </para>
        /// <para>
        /// 判定规则：**分享模式** 且 **不是阿里/迅雷**（<c>UsesProvider</c>）。
        /// 只有夸克与 UC 支持真正的免转存：
        ///   · 阿里  get_share_link_download_url 受账号等级与分享者设置限制，
        ///          大部分分享取不到直链，实际必须转存；
        ///   · 迅雷  provider 内部就是「转存到临时目录 → 取直链 → 立刻删」，
        ///          对用户隐形但确实会写一次网盘。
        /// 给它们显示"免转存"，等于承诺一个点了也不生效的功能。
        /// </para>
        /// </summary>
        private void OnFileContextMenuOpened(object sender, RoutedEventArgs e)
        {
            // 「免转存下载」只在分享模式、且不是阿里/迅雷（它们必须转存）时有意义。
            if (NoSaveDownloadMenuItem != null)
            {
                NoSaveDownloadMenuItem.Visibility =
                    ShareMode && !UsesProvider ? Visibility.Visible : Visibility.Collapsed;
            }

            // 🚨 2026-11-03：新增「删除文件」的可见性。
            //   只在**我的网盘**模式 + 有选中项时出现：
            //   · 分享模式里的 fid 属于分享者，删不得（删了也是别人的文件）；
            //   · 没选中任何东西时删谁？直接不显示，避免误点。
            bool canDelete = !ShareMode
                && FileList != null
                && FileList.SelectedItems != null
                && FileList.SelectedItems.Count > 0;
            if (DeleteFileMenuItem != null)
                DeleteFileMenuItem.Visibility = canDelete ? Visibility.Visible : Visibility.Collapsed;
            if (DeleteFileSeparator != null)
                DeleteFileSeparator.Visibility = canDelete ? Visibility.Visible : Visibility.Collapsed;

            // 🚨 2026-11-02 空菜单兜底（用户报「右键只剩一个白点」）：
            //   原来这个菜单只有「免转存下载」一项，我的网盘模式下它被折叠 →
            //   弹出的是**完全空白的菜单**，视觉上就是一个白色小点。
            //   现在补了「下载文件」且始终可见，理论上不会再空；
            //   但任何未来改动都可能再次让它变空，所以这里做**通用防御**：
            //   统计可见项，一个都没有就取消弹出。
            var cm = sender as System.Windows.Controls.ContextMenu;
            if (cm == null) return;

            bool anyVisible = false;
            foreach (var item in cm.Items)
            {
                var mi = item as System.Windows.Controls.MenuItem;
                if (mi != null && mi.Visibility == Visibility.Visible)
                {
                    anyVisible = true;
                    break;
                }
                // 非 MenuItem（如 Separator）也算内容，但这里菜单里只有 MenuItem
            }

            if (!anyVisible)
            {
                // 必须异步关：Opened 事件里直接 IsOpen=false 会被 WPF 重新打开。
                e.Handled = true;
                cm.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { cm.IsOpen = false; } catch { }
                }), System.Windows.Threading.DispatcherPriority.Background);
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
        /// <summary>
        /// 右键菜单「删除文件」的入口。
        /// <para>
        /// 🚨 2026-11-03：删除实现一直存在（<see cref="OnDeleteCloudSelected"/>），
        /// 但**从来没挂到右键菜单上** —— 用户只能靠某个隐藏入口触发，
        /// 于是在「我的网盘」里找不到删除按钮。现在补成正式菜单项，
        /// 这里只做一次转发，避免把删除逻辑复制成两份。
        /// </para>
        /// </summary>
        private void OnDeleteSelectedFiles(object sender, RoutedEventArgs e)
        {
            OnDeleteCloudSelected(sender, e);
        }


        private async void OnDeleteCloudSelected(object sender, RoutedEventArgs e)
        {
            if (!IsLoggedInAny) { Log("请先登录"); return; }
            if (ShareMode) { Log("「删除网盘文件」只在「我的网盘」模式下可用（分享里的文件属于分享者）"); return; }

            var selected = FileList.SelectedItems.Cast<ShareFileItem>().ToList();
            if (selected.Count == 0) { Log("没有选中任何文件"); return; }

            var ans = AppDialog.Show(this,
                string.Format("确定从网盘删除选中的 {0} 项吗？\n\n" +
                    "删除后会一并清空回收站，无法恢复。", selected.Count),
                "删除网盘文件", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (ans != MessageBoxResult.Yes) return;

            var fids = selected.Select(f => f.Fid).Where(f => !string.IsNullOrEmpty(f)).ToList();
            if (fids.Count == 0) return;

            // 阿里/迅雷的客户端目前只实现了"浏览 + 取直链"，没有删除接口。
            // 与其让它抛一个晦涩的异常，不如直接说清楚。
            if (!OwnIsQuarkLike)
            {
                AppDialog.Show(this,
                    ActivePanDisplayName + " 目前只支持浏览与下载，暂不支持在软件内删除文件。\n\n"
                    + "需要删除的话，请到网页版操作。",
                    "暂不支持", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

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
            // 🚨 这里是「我的网盘」路径（fromShare: false），归属网盘就是**当前登录账号**那一家的
            // _activeKind。以前用的是 _panKind —— 它只在「解析分享」时被赋值，
            // 我的网盘路径从不赋值 ⇒ 恒为初值 Quark。后果：
            //   ① 转存副本记成夸克 → 清理时用夸克客户端删阿里/迅雷的副本 → 删不掉，永久残留；
            //   ② 任务分组的 PanKind 也错（虽然分组最终取的是 job.Pending.PanKind）。
            PanKind driveKind = _activeKind;
            var targets = files.Select(f => new LinkTarget { Fid = f.Fid, Name = f.Name }).ToList();
            var urlOf = await FetchUrlsWithRetryAsync(targets, fromShare: false, ct: ct);
            var items = new List<DownloadItem>();
            foreach (var f in files)
            {
                DownloadLink link;
                if (urlOf.TryGetValue(f.Fid, out link))
                {
                    var item = new DownloadItem
                    {
                        Name = f.Name, Size = f.Size, Url = link.DownloadUrl, RelDir = f.RelDir,
                        Md5 = link.Md5, Sha1 = link.Sha1, Sha256 = link.Sha256,
                        Fid = f.Fid, FromShare = false,
                        StoredKind = driveKind,
                    };
                    // 阿里/迅雷取链时会先转存一份，这个 id 要留到任务结束再删。
                    // ⚠️ 必须走 NoteStored 而不是 item.AddStored —— 它同时落盘到
                    //    GeZi.cleanup.txt，程序崩溃/被强杀时下次启动才能补删。
                    NoteStored(item, driveKind, link.StoredId);
                    items.Add(item);
                }
                else
                    LogDetail("无直链: " + f.Name);
            }
            return items;
        }


        /// <summary>转存后校验：等文件异步落盘，过滤不在目标目录里的 fid（对应原版 _resolve_saved_fids）。</summary>
        private async Task<List<string>> ResolveSavedFidsAsync(string toFid, List<string> fids)
        {
            // 转存（保存到自己网盘）目前只有夸克系实现了 —— 阿里/迅雷没有转存接口，
            // 它们那条路根本不会调到这里；真调到了就原样返回，别拿夸克客户端去问。
            // 🚨 必须用 TransitClient（当前分享那一家），不是 _client（当前登录账号）。
            QuarkClient cli = TransitClient;
            if (cli == null)
                return fids;

            int[] delays = { 0, 1000, 1500, 2000, 2500, 3000 };
            for (int round = 0; round < delays.Length; round++)
            {
                if (round > 0)
                    await Task.Delay(delays[round]);
                try
                {
                    var items = await cli.ListDirAsync(toFid, 1);
                    var existing = new HashSet<string>(items.Select(it => it.Fid));
                    if (fids.All(f => existing.Contains(f)))
                        return fids;
                }
                catch { }
            }
            return fids;
        }


        /// <summary>中转文件夹 fid 缓存（一次运行内路径不变）。</summary>
        private string _transitFolderFid;

        /// <summary>缓存归属的账号凭据；换账号后必须作废，否则会把文件转存到上一个账号的目录。</summary>
        private string _transitFolderOwner;

        private async Task<string> EnsureFolderAsync(string name)
        {
            string path = "/" + name;
            try
            {
                // 中转文件夹只在「转存到夸克系网盘（夸克 / UC）」时用到；
                // 阿里 / 迅雷没有转存接口（它们在自己的 provider 内部完成取链），
                // 走到这里就返回 null，调用方会跳过清理。
                //
                // 🚨 客户端与判据都必须是**当前分享那一家**（TransitClient），
                //    不能用 _client / OwnIsQuarkLike —— 那两个看的是「当前登录账号」。
                //    用户实测：登录着迅雷时下 UC 分享，会被误判成"没有转存能力"而整批失败。
                QuarkClient cli = TransitClient;
                if (cli == null)
                    return null;

                // 命中缓存直接返回：PathFidAsync 是一次完整网络往返（0.5~1 秒），
                // 而中转文件夹名固定，一次运行内不可能变。这是「点下载要等几秒」的一环。
                string owner = cli.CookieStr ?? "";
                if (!string.IsNullOrEmpty(_transitFolderFid) && _transitFolderOwner == owner)
                    return _transitFolderFid;

                string fid = await cli.PathFidAsync(path);
                if (string.IsNullOrEmpty(fid))
                    fid = await cli.MkdirAsync(path);

                if (!string.IsNullOrEmpty(fid))
                {
                    _transitFolderFid = fid;
                    _transitFolderOwner = owner;
                }
                return fid;
            }
            catch (Exception ex)
            {
                LogDetail("中转文件夹处理失败: " + ex.Message);
                return null;
            }
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
            // ⚠️ 2026-11-02 更正这句注释 —— 原来写「阿里/迅雷没有转存」是**错的**：
            //    阿里/迅雷分享**必须先转存到自己网盘**才能取直链（见 AGENTS.md 10.2 / 10.3）。
            //    它们只是**不走这条路径**：
            //      · 这条 = 「夸克/UC 的共享中转文件夹」那套（_pendingCleanupFids + _client.DeleteAsync）；
            //      · 阿里/迅雷走 CleanupStoredForItemAsync → provider.CleanupStoredAsync。
            //    两条路**都必须看 _settings.AutoClean** —— 缺一条就会让这个开关对那两家失效。
            //    （2026-11-02 就是这么坏的：CleanupStoredForItemAsync 完全没判 AutoClean，已补。）
            if (!OwnIsQuarkLike)
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
                    "文件还没下到能播放的部分，所以现在打不开。\n"
                    + "等进度涨起来再点「播放」就可以了。",
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
                // 🚨 这里必须用「已下载字节数」，不能用文件长度（2026-10-24 修）。
                // 共写 + 预分配之后，文件长度从第一秒就是全量（573 MB），
                // 拿它当进度会打出「573 MB / 573 MB」这种自相矛盾的提示，
                // 而且拿它当播放优先级起点会让引擎以为「已经下到文件尾」。
                long doneBytes = item.Done;
                if (doneBytes <= 0) doneBytes = curSize;

                Log(string.Format(
                    "提示: 该文件还没下完（当前已下 {0} / {1}）。文件已预分配成完整大小，"
                    + "若视频索引（moov）在文件末尾，需要等它被优先写入后才能播 —— "
                    + "这属于正常现象，稍等再试即可。",
                    Util.FormatSize(doneBytes), Util.FormatSize(item.Total)));

                // 边下边看：告诉融合引擎「播放位置往后这段最优先」。
                //
                // 为什么要这么做：分片虽然按偏移顺序入队，但抢占切分、慢片重连
                // 都会把顺序打乱，播放器读到缺口就会卡。把窗口提到最高优先级，
                // 能让「马上要读的数据」先到，卡顿明显减少。
                //
                // 起点用当前文件长度：顺序写模式下它就是「已连续刷盘位置」，
                // 引擎内部还会再夹一次；随机写 + 预分配时长度是总长，
                // 引擎会自动退化为从头优先（见 ApplyPriorityWindowToQueue）。
                // 老引擎（非融合）没有这个能力，SetPlaybackPriority 会返回 false，忽略即可。
                try
                {
                    // 起点用「已下载字节数」：预分配后 curSize 恒等于总长，
                    // 拿它当起点会让优先级窗口落到文件最末尾（那里还没下），等于没设。
                    bool ok = _scheduler != null
                              && _scheduler.SetPlaybackPriority(dest, doneBytes, 64L * 1024 * 1024);
                    if (ok)
                        Log("已把播放位置往后的 64MB 设为最优先下载（边下边看）。");
                }
                catch (Exception ex) { Log("设置播放优先级失败: " + ex.Message); }
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
            string why;
            if (ShellReveal.Reveal(dest, out why))
                return;

            string reason = string.IsNullOrEmpty(why) ? "未知原因" : why;
            Log("定位失败：" + reason + "（" + dest + "）");

            // 把路径直接放进剪贴板：即使调不起资源管理器，用户也能立刻 Ctrl+V 用。
            bool copied = TryCopyToClipboard(dest);

            // 权限不足是**低完整性进程**的固有限制（不是 bug），必须说清楚怎么绕开，
            // 否则用户只会反复点「定位」然后以为软件坏了。
            bool denied = reason.IndexOf("权限", StringComparison.Ordinal) >= 0
                          || reason.IndexOf("拒绝", StringComparison.Ordinal) >= 0;
            string tip = denied
                ? "这是 Windows 的权限限制：程序运行在受管目录里，被当作低权限进程。\n"
                  + "把 GeZi.exe 复制到普通目录（例如 D:\\GeZi）再运行就能正常定位。"
                : "系统拒绝了资源管理器调用，请手动打开文件所在目录。";

            AppDialog.Show(this,
                "无法在资源管理器中定位该文件。\n\n"
                + tip
                + "\n\n文件完整路径（" + (copied ? "已复制到剪贴板" : "请手动复制") + "）：\n" + dest,
                "无法定位文件",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }

        /// <summary>
        /// 尽力把文本放进剪贴板。剪贴板可能被别的进程独占（COM 异常），
        /// 所以失败是正常分支，调用方据此调整提示文案。
        /// </summary>
        private static bool TryCopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch
            {
                return false;
            }
        }


        /// <summary>按路径打开文件（供历史窗口复用「打开」逻辑）。</summary>
        private void OpenTaskFileByPath(string path)
        {
            if (!ShellLaunch.OpenFile(path))
                Log("打开失败，请手动到下载目录查看：" + path);
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
    }
}
