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
// 职责：下载：取链、入队、线程分配、暂停取消

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

        /// <summary>取当前账号网盘里这些文件的直链。</summary>
        private async Task<IReadOnlyList<DownloadLink>> OwnLinksAsync(IList<PanFileEntry> files)
        {
            if (files == null || files.Count == 0)
                return new List<DownloadLink>();

            if (OwnIsQuarkLike)
            {
                var fids = new List<string>(files.Count);
                foreach (var f in files)
                    fids.Add(f.Fid);
                return await _client.GetDownloadAsync(fids).ConfigureAwait(true);
            }

            if (_ownProvider == null)
                throw new InvalidOperationException(ActivePanDisplayName + " 客户端未就绪，请重新登录。");
            var links = await _ownProvider.GetOwnDownloadLinksAsync(files).ConfigureAwait(true);
            PersistProviderToken();      // 同上：取直链同样会走换 token
            return links;
        }


        private void EnqueueLog(string msg, bool detail)
        {
            try
            {
                string text = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
                _logQueue.Enqueue(new LogLine
                {
                    Text = text,
                    Detail = detail,
                });
                // 2026-10-26 用户诉求「日志与下载记录持久化，退出重进不丢」：
                // 下载记录/解析历史本来就在 GeZi.state.json 里，只有运行日志是纯内存的。
                // 这里同步追加到 GeZi.log（内部加锁，失败静默 —— 写日志绝不能影响下载）。
                //
                // 🚨 2026-11-07 落盘前**必须脱敏**：GeZi.log 是用户会主动发给别人求助的文件，
                //    而直链查询串里带 CDN 签名（阿里 Signature=、夸克/UC sign=、迅雷 auth_key=），
                //    拿到的人在有效期内能直接下别人的文件。
                //    ⚠️ 只有落盘这一路脱敏，**界面 LogBox 保留原文** ——
                //       界面上看到的确切内容用户自己负责，而文件会离开这台机器。
                LogStore.Append(LogRedactor.Redact((detail ? "[详] " : "") + text));
            }
            catch { }
        }


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


        /// <summary>
        /// 拆开迅雷内嵌登录返回的打包记录，把 refresh_token / sub / device_id /
        /// captcha_token 分别存进设置，只把 access_token 交给界面与登录流程。
        /// <para>
        /// 为什么要存 refresh_token：网页令牌约两小时过期，没有它就只能让用户
        /// 反复扫码。网页 token 属于另一套 OAuth 客户端，所以单独存一份，
        /// 刷新时也要走 <see cref="XunleiLoginService.RefreshWebAsync"/>。
        /// </para>
        /// <para>
        /// 传入的不是打包记录（例如用户手动粘贴的裸 token）时原样返回。
        /// </para>
        /// </summary>
        private string ConsumeXunleiRecord(string record)
        {
            string at, rt, sub, dev, cap;
            if (!XunleiProtocol.TryParseRecord(record, out at, out rt, out sub, out dev, out cap))
                return record;

            if (!string.IsNullOrEmpty(rt))
                _settings.XunleiWebRefreshToken = rt;
            if (!string.IsNullOrEmpty(sub))
                _settings.XunleiWebSub = sub;
            if (!string.IsNullOrEmpty(dev))
                _settings.XunleiWebDeviceId = dev;
            if (!string.IsNullOrEmpty(cap))
                _settings.XunleiCaptchaToken = cap;

            if (!string.IsNullOrEmpty(rt))
                LogDetail("[登录] 迅雷网页登录已取到 refresh_token，之后可自动续期，无需反复扫码。");
            else
                LogDetail("[登录] 迅雷网页登录未取到 refresh_token（网页可能尚未完成登录），到期后需重新扫码。");

            return at;
        }


        /// <summary>取一页目录（分享模式 / 网盘模式走各自接口）。</summary>
        private Task<QuarkClient.DirPage> FetchDirPageAsync(string fid, int page)
        {
            // 「我的网盘」永远走**当前账号**的接口（夸克/UC 用 _client 自带 Kind，
            // 阿里/迅雷用 _ownProvider —— 统一由 OwnListDirPageAsync 分派），
            // 与当前解析的分享属于哪家无关。必须放在 UsesProvider 判断之前，
            // 否则解析过 UC 分享后切到「我的网盘」会跑到分享接口上去。
            if (!ShareMode)
                return OwnListDirPageAsync(fid, page);

            // 分享模式下的非夸克网盘：走 IPanProvider。这些客户端的 ListShareAsync
            // 内部已经翻完所有页，所以只认第 1 页、HasMore=false ——
            // 界面本身还是按批揭示的，体验不受影响。
            if (UsesProvider)
                return FetchOtherPanDirPageAsync(fid, page);

            return ShareClient.ShareDetailPageAsync(_pwdId, _stoken, fid, page);
        }


        /// <summary>非夸克网盘的目录页：一次性取完，转成夸克的条目类型交给界面。</summary>
        private async Task<QuarkClient.DirPage> FetchOtherPanDirPageAsync(string fid, int page)
        {
            var one = new QuarkClient.DirPage();
            if (page > 1)
                return one;     // 已经把整份目录取回来了，没有第二页

            if (_panProvider == null || _panShareRef == null)
                throw new InvalidOperationException("多网盘客户端未就绪。");

            var entries = await _panProvider.ListShareAsync(_panShareRef, fid)
                .ConfigureAwait(false);

            // 字段搬运统一走 Core 的 PanEntryMapper（可被自测覆盖）
            one.Items.AddRange(PanEntryMapper.ToQuarkEntries(entries));
            one.HasMore = false;
            return one;
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


        /// <summary>递归求和：文件夹 = 所有子孙文件字节数之和。</summary>
        /// <param name="takeBudget">每发一次请求调一次；返回 false 表示预算耗尽，停止下钻。</param>
        /// <param name="ctx">
        /// 本轮扫描的上下文快照。每进一层都比对一次：用户中途切网盘/退出分享时立刻停手，
        /// 绝不拿新客户端去请求旧 fid（那会报 file_not_found / guest missing pwd_id）。
        /// </param>
        private async Task<long> SumDirAsync(string fid, CancellationToken ct, Func<bool> takeBudget,
            DirSizeScanContext ctx = null)
        {
            if (string.IsNullOrEmpty(fid) || ct.IsCancellationRequested) return 0;
            if (ctx != null && !IsDirSizeContextCurrent(ctx)) return 0;

            long cached;
            if (_dirSizeCache.TryGetValue(DirSizeKey(fid), out cached)) return cached;
            if (!takeBudget()) return 0;

            // 非夸克网盘：目录大小求和也要走 provider，否则会拿夸克客户端去请求，
            // 结果是异常或 0 —— 用户看到「文件夹 0 B」会以为文件是空的。
            // 但「我的网盘」模式永远用当前账号的客户端（见 FetchDirPageAsync 的同款说明）。
            List<QuarkFileEntry> items;
            if (!ShareMode)
            {
                items = new List<QuarkFileEntry>(await OwnListDirAsync(fid).ConfigureAwait(true));
            }
            else if (UsesProvider)
            {
                var page = await FetchOtherPanDirPageAsync(fid, 1).ConfigureAwait(true);
                items = page.Items;
            }
            else
            {
                items = await ShareClient.ShareDetailAsync(_pwdId, _stoken, fid);
            }

            long sum = 0;
            foreach (var it in items)
            {
                if (ct.IsCancellationRequested) return sum;
                if (ctx != null && !IsDirSizeContextCurrent(ctx)) return sum;
                if (it.IsDir)
                    sum += await SumDirAsync(it.Fid, ct, takeBudget, ctx);
                else
                    sum += it.Size;
            }
            // 上下文变了就不要写缓存：那会把旧网盘的结果记到新账号名下
            if (ctx == null || IsDirSizeContextCurrent(ctx))
                _dirSizeCache[DirSizeKey(fid)] = sum;
            return sum;
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
                // 文件数**不再设上限**（2026-10-26 用户要求）。
                // 原来这里有一道 MaxFiles=1000 的硬拒，会直接中止并弹窗；
                // 现在选多少就取多少。

                LogDetail("共展开 " + files.Count + " 个文件，正在取直链…");
                var items = await FetchItemsChunkedAsync(files, CancellationToken.None);
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

            var root = DialogChrome.DialogRoot();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 说明
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 页签 + 文件清单
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 底部按钮

            // 标题已画在自绘标题栏里（2026-10-26），内容区不再重复。
            // 「N 个文件」这个计数原来在标题里，现在并进下面那行说明的开头。

            // ===== 说明：压成一行，说清"页签只选种类、导出靠下方按钮" =====
            // 这一行同时兼作**切换页签时的加载指示区**：切换期间整行原地换成
            // 「转圈 + 正在切换到 xxx…」，切完再换回说明文字（两者尺寸接近，不会跳）。
            // 文案改为「获取直链后可导出为 …（后三者自带 UA 与 Cookie）」，
            // 括号部分用警示红（与下载页底部那行提示同一套语义色）。
            var head = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                LineHeight = 19,
                Margin = new Thickness(0, 0, 0, 12),
            };
            // 与其他弹窗的 Hint 同一档灰（原来是三级灰，12px 在浅底上偏虚）
            var hintFg = TryFindResource("TextSecondaryBrush") as Brush;
            if (hintFg != null) head.Foreground = hintFg;
            head.Inlines.Add(new Run(string.Format("共 {0} 个文件 · ", items.Count)) { FontWeight = FontWeights.SemiBold });
            head.Inlines.Add(new Run("获取直链后可导出为 纯直链 / curl 命令 / aria2c 命令 / Motrix 界面 "));
            var hintWarn = new Run("（后三者自带 UA 与 Cookie）");
            var hintWarnBrush = TryFindResource("DangerBrush") as Brush;
            if (hintWarnBrush != null) hintWarn.Foreground = hintWarnBrush;
            head.Inlines.Add(hintWarn);
            // 🚨 必须告诉用户链接会过期（2026-10-30）。
            //    导出之后我们就不再管这些链接了，用户若过一阵才粘进 aria2，拿到的就是死链，
            //    只会以为「这软件导出的链接是坏的」。服务端有效期取全局唯一来源。
            head.Inlines.Add(new Run(string.Format(
                " · 链接约 {0} 分钟内有效，请尽快使用",
                GeZi.Core.Support.LinkLifetime.ServerSeconds / 60)));
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
            if (!IsLoggedInAny && _panProvider == null) { Log("请先登录或解析分享"); return; }

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
                ShareClient.CookieStr, QuarkConstants.Referer, format,
                // 必须是绝对路径：Motrix 是独立进程，
                // 它把 "." 解析成（实测落到了 D:\motrix\ 安装目录），
                // 不是用户执行命令时所在的目录。curl/aria2c 没这问题（用户自己在 shell 里跑）。
                absoluteSaveDir: EffectiveOutDir());
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


        /// <summary>
        /// 按**文件大小比例**把总连接预算分给这一批任务，返回与入参等长的线程数数组。
        /// </summary>
        /// <param name="kind">
        /// 这一批任务来自哪家网盘。**必须传任务的真实来源**（分享看 <c>_panKind</c>、
        /// 我的网盘看 <c>_activeKind</c>，统一走 <see cref="DownloadPanKind"/>），
        /// 不能传「当前登录账号」。
        /// </param>
        /// <remarks>
        /// 🚨🚨 2026-11-07 修「设置了各网盘线程却完全没生效（只有调低有效）」。
        /// <para>
        /// 旧实现这里是 <c>Math.Min(_settings.Threads, MaxRealConcurrency)</c> —— 只读**全局**
        /// <c>_settings.Threads</c>，一个字都没读 per-pan 配置。而 per-pan 值只在调度器里
        /// 被当作 <c>MaxParallelCount</c>（上限）夹一次：
        /// </para>
        /// <code>
        /// DownloadScheduler: int want = job.Threads &gt; 0 ? job.Threads : DownloadThreads;
        ///                    if (job.MaxParallelCount &gt; 0 &amp;&amp; want &gt; job.MaxParallelCount) want = job.MaxParallelCount;
        /// </code>
        /// <para>
        /// ⇒ per-pan 值**只能往下压、永远不能往上抬**。迅雷设 16 有效（256 被压下来）、
        /// 阿里设 64 有效，但夸克设 512 **完全无效** —— 基数早就被锁成 256，而 512 这个上限
        /// 比 256 大，夹了个寂寞。用户看到的就是「怎么改都只像作用于全局」。
        /// </para>
        /// <para>
        /// 现在基数直接取 <see cref="ThreadsFor"/>(kind)：per-pan 覆盖值优先、否则走
        /// <see cref="DefaultThreadsFor"/>（全局 <c>Threads</c> 回落基准），最后叠服务端硬上限。
        /// 这样调高调低都真正生效，而迅雷 16 / 阿里 64 / UC 256 这些实测阈值仍然保留。
        /// </para>
        /// </remarks>
        private int[] AllocateThreads(IList<long> sizes, PanKind kind)
        {
            return AllocateThreads(sizes, kind, 0);
        }


        /// <summary>
        /// 同上，但可以指定这一桶能用的**连接预算**（只给混合网盘分桶用）。
        /// </summary>
        /// <param name="budget">
        /// 0 = 用全局 <see cref="DownloadConnBudget"/>（512）—— 单网盘批走这条，行为与改造前逐字一致。
        /// &gt;0 = 分桶后分摊到的预算，会同时压 <c>perFileCap</c>，
        /// 否则「n == 1 直接给满 cap」会让四个桶各拿满额、绕过 512 总预算。
        /// </param>
        private int[] AllocateThreads(IList<long> sizes, PanKind kind, int budget)
        {
            int cap = ThreadsFor(kind);
            // 分桶预算比该网盘的上限还小时，单文件也不能拿超过预算的连接数。
            if (budget > 0 && budget < cap) cap = budget;
            return GeZi.DownloadEngine.ThreadAllocator.Allocate(
                sizes,
                cap,
                budget > 0 ? budget : DownloadConnBudget,
                MinThreadsPerFile,
                // 同时最多跑几个 —— 预算约束的是"同时并发"的量级，不是整批的账面总和
                Math.Max(1, _settings.ConcurrentTasks));
        }


        /// <summary>
        /// 给**混合网盘**的一批任务分配线程数（只给续传用，新建任务时整批同源）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="ThreadAllocator.Allocate"/> 的 <c>perFileCap</c> 是**单值**参数，
        /// 而「继续下载」一批里可能同时有夸克、UC、阿里、迅雷的记录。
        /// 整批用一个 cap 必然错：按夸克算会撑爆迅雷（503 洪泛），
        /// 按迅雷算会把夸克饿到 16 路。
        /// </para>
        /// <para>
        /// ⇒ 按网盘**分桶**各算一次 <c>Allocate</c>，再按原始下标合并回去。
        /// 每家的份额仍在自己的桶内按大小比例分，语义与单网盘批次完全一致。
        /// </para>
        /// </remarks>
        private int[] AllocateThreadsByPan(IList<long> sizes, IList<PanKind> kinds)
        {
            int n = sizes == null ? 0 : sizes.Count;
            var result = new int[n];
            if (n == 0) return result;
            if (kinds == null || kinds.Count < n)
            {
                // 兜底：来源未知时按夸克（不限流）算，与升级前行为一致。
                return AllocateThreads(sizes, PanKind.Quark);
            }

            // 按网盘分桶（保持原始下标）
            var buckets = new Dictionary<PanKind, List<int>>();
            for (int i = 0; i < n; i++)
            {
                PanKind k = kinds[i];
                List<int> idx;
                if (!buckets.TryGetValue(k, out idx))
                {
                    idx = new List<int>();
                    buckets[k] = idx;
                }
                idx.Add(i);
            }

            foreach (var kv in buckets)
            {
                var sub = new List<long>(kv.Value.Count);
                foreach (int i in kv.Value) sub.Add(sizes[i]);
                // 🚨 每个桶必须分到**自己的那份预算**，不能都拿满 512。
                //   ThreadAllocator.Allocate 在 n == 1 时会**直接返回 cap**
                //   （源码注释：「单文件：保持原行为」），四个桶各 1 个文件 =
                //   夸克 512 + UC 256 + 阿里 64 + 迅雷 16 = 848 路，直接绕过 512 总预算。
                //   ⇒ 分摊规则抽成 Core 纯函数 DownloadSizing.PanBucketBudget（便于自检钉死），
                //      单桶（= 单网盘批）时占比 1、得满额，与改造前**逐字等价**。
                int bucketBudget = DownloadSizing.PanBucketBudget(
                    DownloadConnBudget, kv.Value.Count, n, MinThreadsPerFile);
                var subAlloc = AllocateThreads(sub, kv.Key, bucketBudget);
                for (int j = 0; j < kv.Value.Count && j < subAlloc.Length; j++)
                    result[kv.Value[j]] = subAlloc[j];
            }
            return result;
        }


        /// <summary>
        /// 真正可用的下载根目录。
        ///
        /// <para>
        /// 启动时会挑一个确实可写的目录写进 <c>_settings.OutDir</c>，但**用户可以在设置页把输入框清空**
        /// —— <c>_settings.OutDir = NormalizeDir(OutDirBox.Text)</c> 会存成空串。
        /// 之后 <c>Path.Combine("", "子目录")</c> 得到的是**相对路径**，会落到进程当前工作目录
        /// （可能是 Program Files、也可能是任意快捷方式的起始目录），用户会以为「文件不见了」。
        /// </para>
        /// <para>
        /// 所以这里再兜一层：空值就回退到系统下载目录下的「鸽子下载」，
        /// 与启动时那份候选列表的第一项保持一致。
        /// </para>
        /// </summary>
        private string EffectiveOutDir()
        {
            string dir = (_settings.OutDir ?? "").Trim();
            if (dir.Length > 0) return dir;
            return Path.Combine(Util.GetDownloadsFolder(), "鸽子下载");
        }


        /// <summary>
        /// 用**已取到的链接**直接建任务入队（不再重新取链）。
        /// 给「获取直链」对话框的「下载这些文件」按钮用。
        /// </summary>
        private void StartDownloadFromItems(List<DownloadItem> items)
        {
            // 🚨 归属判定必须用 DownloadPanKind（分享看 _panKind、我的网盘看 _activeKind）。
            // 以前这里直接用 _panKind，而 _panKind **只在「解析分享」时被赋值**，
            // 「我的网盘」路径从不赋值 ⇒ 恒等于初值 Quark。三个后果：
            //   ① 任务分组全归「夸克」；
            //   ② 迅雷拿夸克的 0（不限流）→ 256 并发 → 大面积 503 失败；
            //   ③ StoredKind 错 → 清理时用夸克客户端去删阿里/迅雷的转存副本 → 永久残留。
            PanKind srcKind = DownloadPanKind;
            var alloc = AllocateThreads(items.Select(i => i.Size).ToList(), srcKind);
            var jobs = new List<DownloadJob>();
            // 同一批里可能出现同名文件（不同目录各有一个 video.mp4）。只看磁盘时
            // 两个 dest 都还没落盘 → 会算出同一个路径 → 第二个任务被调度器当成重复丢掉。
            var takenDests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (string.IsNullOrEmpty(it.Url)) continue;
                string destDir = Path.Combine(EffectiveOutDir(), it.RelDir ?? "");
                try { Directory.CreateDirectory(destDir); }
                catch (Exception ex) { Log("创建下载目录失败: " + destDir + " — " + ex.Message); continue; }

                string dest = PathUtil.UniquePath(Path.Combine(destDir, PathUtil.SanitizeFileName(it.Name)), takenDests);
                var item = it;
                jobs.Add(new DownloadJob
                {
                    Name = item.Name, Url = item.Url, Dest = dest,
                    // 非夸克网盘用签名直链，不带夸克 Cookie（避免把夸克登录态发给别家 CDN）
                    Cookie = DownloadCookie,
                    Referer = OwnDownloadReferer,
                    UserAgent = OwnDownloadUserAgent,
                    Origin = OwnDownloadOrigin,
                    UseOpenSsl = OwnDownloadUseOpenSsl,
                    // 按「本任务来自哪家网盘」限流，不能按 _activeKind（当前登录账号）：
                    // 登录夸克却解析 UC 分享时 _activeKind 仍是 Quark，UC 的 256 上限会失效；
                    // 迅雷更危险 —— 32 条就大面积 503，按夸克（不限）跑必然失败。
                    MaxParallelCount = ThreadsFor(srcKind),
                    // 慢连接的绝对下限也按网盘分档（见 SlowFloorFor 的注释）：
                    // 阿里/UC 单 IP 总配额低，用夸克的 16KB/s 会把全部连接误判成慢。
                    SlowFloorBps = SlowFloorFor(srcKind),
                    // 2026-11-05：下载数据面按网盘补齐客户端头，与取链身份一致。
                    PanKind = (int)srcKind,
                    ExtraHeaders = DownloadExtraHeadersFor(srcKind),
                    Threads = i < alloc.Length ? alloc[i] : ThreadsFor(srcKind),
                    Md5 = item.Md5, Sha1 = item.Sha1, Sha256 = item.Sha256,
                    // 写入方式统一取设置页的全局值（原来靠下载区的「边下边播」勾选框，
                    // 与设置页重复且容易误导，已移除该勾选框）。
                    PartsRoot = string.IsNullOrWhiteSpace(_settings.PartsRoot) ? null : _settings.PartsRoot,
                    // 2026-11-04：源文件指纹，写进 .geziresume，防止「同一路径换成另一个源文件」时串进度。
                    SourceKey = BuildSourceKey(item.Size, (int)srcKind, item.Fid),
                    LinkRefresher = ct => RefreshLinkAsync(item, ct),
                    // 免转存（从分享直接取链）→ 续传取不到新链，失败文案别承诺"可续传"
                    ResumeCannotRefresh = item.FromShare,
                    Pending = new PendingInfo
                    {
                        PwdId = _pwdId, Stoken = _stoken, Passcode = item.Passcode,
                        Fid = item.Fid, Token = item.Token, FromShare = item.FromShare,
                        // 🚨 2026-11-03：把「原始分享链接」和「转存后 fid」也带进续传记录。
                        //   链接是百分百兜底（PwdId 丢了还能重解析），
                        //   SavedFid 决定取链该用哪个 fid（转存后文件在网盘里）。
                        ShareUrl = item.ShareUrl ?? _shareUrl,
                        SavedFid = item.SavedFid,
                        RelDir = item.RelDir ?? "", Dest = dest, Size = item.Size,
                        Md5 = item.Md5, Sha1 = item.Sha1, Sha256 = item.Sha256,
                        // 自动重解析分享的上下文（stoken 过期时换新凭证用）
                        StartFid = item.StartFid, ShareRelDir = item.ShareRelDir,
                        // 来源网盘（0=夸克）：续传时要据此选客户端
                        PanKind = (int)srcKind,
                    },
                });
                // 终态要靠 dest 反查这个条目，才能删掉取链时转存的临时副本。
                TrackItemForDest(dest, item);
            }
            if (jobs.Count == 0) { Log("没有可下载的条目。"); return; }

            LogAllocation(alloc);
            // 「下载这些文件」也是**一次下载**，同样自成一个二级分组。
            // 这条路径没有 ShareFileItem，用条目里的 RelDir 顶层目录名当任务名。
            var taskGroup = MakeTaskGroupFromItems(items);
            // 一次性加：可能一次几百条，逐条加会让分组视图反复重排
            var reuseRows = new List<TaskItem>(jobs.Count);
            foreach (var job in jobs)
                reuseRows.Add(new TaskItem(job, job.Name,
                    taskGroup != null ? taskGroup.Id : null,
                    taskGroup != null ? taskGroup.Name : null));
            AddTaskRows(reuseRows);
            Log("已加入下载队列: " + jobs.Count + " 个文件（复用已取链接）");
            // 不直接跳页签，改用灵动岛提示（用户 2026-10-26 要求）
            ShowIsland(jobs.Count, SumJobBytes(jobs));
            RunJobs(jobs);
        }


        private async Task StartDownloadAsync(List<ShareFileItem> selected,
            CancellationToken ct = default(CancellationToken), bool forceNoSave = false)
        {
            if (!IsLoggedInAny && _panProvider == null)
            {
                // 🚨 2026-10-26：原来这里只往日志写一行「请先登录或解析分享」，
                //   用户点了「下载」看起来毫无反应（日志在下方不起眼）。
                //   而且启动时登录态还在恢复，会被误判成"没登录"→ 直接 401。
                //   统一走 RequireLogin：它区分「正在恢复」和「真没登录」。
                RequireLogin("下载");
                return;
            }
            if (selected.Count == 0)
            {
                Log("没有选中任何文件");
                return;
            }

            // 明确告诉用户分片怎么落盘 ——
            // 用户问过「默认不是先下载引擎吗，先下载引擎有没有分片」。
            LogDetail("下载引擎：融合引擎（多连接并发 + 顺序写入单文件，支持断点续传）");

            SetBusy(true);
            try
            {
                // 🚨 点下「下载」就立刻把灵动岛亮出来（用户 2026-10-29 要求：
                //    「我希望在入队中就会显示灵动岛，如果在入队中的时候是检查入队文件
                //      就有相应的文案」）。
                //    展开文件夹 + 统计文件数这段还没有任何进度可报，但可能要好几百毫秒，
                //    期间界面什么都不动，用户会以为点了没反应。
                SetIslandStage("正在统计文件…");
                // 🚨 归属判定必须在**任何 await 之前**取好快照。
                // 原因有两个：① DownloadPanKind 内部读 ShareModeRadio.IsChecked，
                // 而 WPF 控件只能在 UI 线程访问，await 之后可能已经在线程池线程上；
                // ② 同 StartDownloadFromItems 的注释 —— _panKind 在「我的网盘」路径恒为 Quark。
                PanKind srcKind = DownloadPanKind;
                ct.ThrowIfCancellationRequested();
                // 把选中的文件夹递归展开成文件列表（保留相对目录）
                // 🚨 展开阶段要报「已找到几个」（2026-10-30）：原来只写「正在检查文件…」，
                //    2000 个文件时要转好几百毫秒，数字不动用户会以为卡死。
                //    每 20 个刷一次，避免每个文件都改一次界面。
                int foundCount = 0;
                var files = await CollectFilesAsync(selected, "", 0, ct, () =>
                {
                    foundCount++;
                    if (foundCount % 20 == 1)
                        SetIslandStage("正在统计文件…（已找到 " + foundCount + " 个）");
                });
                if (files.Count == 0)
                {
                    Log("没有要下载的文件");
                    return;
                }
                LogDetail("共展开 " + files.Count + " 个文件，正在取直链…");

                // ---- 文件数只提醒、不限制（2026-10-26 用户要求）----
                // 原来这里有一道 MaxFiles=1000 的硬拒：超过就整个中止。
                // 已删除 —— 选了多少就下多少。下面只保留"数量偏多先问一句"。
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

                // 顺次取链 + 顺次下载（2026-10-29 用户要求）：
                // 以前要等**全部**文件取完直链才建任务、才开始下载 ——
                // 2000 个文件时要干等好几分钟，界面像卡死。
                // 现在按块取链，每块取到立刻入队并开始下载，后面的块在后台继续取。
                // 🚨 2026-11-01 两级分组：这一次「点下载」选中的所有文件 = 一个任务，
                //   它们要在列表里折叠到同一个二级分组下面（用户要求）。
                //   ID 必须唯一 —— 两次下载可能选同名目录，按名字分会把两个任务并成一个。
                var taskGroup = MakeTaskGroup(selected);

                await QueueInChunksAsync(files, forceNoSave, srcKind, ct, taskGroup);
            }
            finally
            {
                SetBusy(false);
            }
        }


        // ==================== 取链水位（2026-10-31） ====================

        /// <summary>
        /// 队列里「排队中 + 下载中」的任务数低于这个值就补货（保留给旧路径，见
        /// <see cref="WatermarkSatisfied"/> 里新的判据）。
        /// </summary>
        private int _queueLowWater = 6;

        /// <summary>
        /// 🚨 2026-11-01 用户定的硬规则：**已取到直链但还没开始下载的，最多只允许
        /// 比「正在下载的」多两个**。
        /// <para>
        /// 为什么：直链是有 TTL 的（夸克/UC 约几小时，阿里/迅雷更短）。原来按
        /// <c>并发 × 2</c> 补货，并发 8 就一次预取十几条直链 —— 排在后面的那些
        /// 等轮到它时链路可能已经过期，只能重取，既浪费请求又拖慢整体。
        /// </para>
        /// <para>
        /// 这个数字**不要调大**：预取多一条就多一条可能过期的直链；
        /// 也不能调到 0 —— 那样每次都要等上一个下完才开始取链，
        /// 取链延迟（0.3~2 秒，转存路径更慢）会直接变成空转。
        /// </para>
        /// </summary>
        private const int QueueAheadLimit = 2;

        /// <summary>
        /// 剩余时间预算（秒）。在跑的任务按当前总速度还能跑这么久时，也提前补货 ——
        /// 这是「取链有延迟」的提前量：一块 30 个大约 0.3~2 秒，走转存路径更慢，
        /// 再留一次重试余量，20 秒是实测下来比较稳的提前量（10 秒太紧）。
        /// </summary>
        private const double QueueLeadSeconds = 20.0;

        /// <summary>水位满足时由 <see cref="PumpWatermark"/> 唤醒等待方。</summary>
        private TaskCompletionSource<bool> _watermarkTcs;

        /// <summary>
        /// 按当前并发数重算低水位。
        /// <para>
        /// 🚨 2026-11-01 改：原来是 <c>并发 × 2</c>（并发 8 → 一次预取 16 条直链），
        /// 预取太多会让排在后面的直链白白过期。现在只留
        /// 「正在下载的 + <see cref="QueueAheadLimit"/>」这一条缓冲，
        /// 与实际并发数解耦 —— 并发再高也不会多囤直链。
        /// </para>
        /// </summary>
        private void ResetQueueWatermark()
        {
            int conc = _settings != null ? _settings.ConcurrentTasks : 3;
            if (conc < 1) conc = 1;
            // 下限 3：至少要够「一两条在跑 + 两条缓冲」，否则取链延迟会变成空转。
            _queueLowWater = Math.Max(3, conc + QueueAheadLimit);
        }

        /// <summary>
        /// 当前队列是否已经降到该补货的位置。
        /// <para>两条判据取「或」：</para>
        /// <list type="number">
        /// <item>剩余任务数（排队 + 下载中）≤ 低水位 —— 覆盖大多数情况；</item>
        /// <item>在跑任务按当前速度还能跑 ≤ <see cref="QueueLeadSeconds"/> 秒 ——
        /// 覆盖「队列里还有十几条但都是小文件」的情况，提前补货避免空转。</item>
        /// </list>
        /// <para>
        /// ⚠️ 另有一条兜底：如果一个任务都没在下（全失败 / 全暂停 / 还没起来），
        /// 等下去没有意义 —— 直接放行，否则取链循环会永久卡死在这里。
        /// </para>
        /// </summary>
        private bool WatermarkSatisfied()
        {
            // 🚨 2026-11-01 用户定的硬规则：
            //   **已取到直链但还没开始下载的，最多只比「正在下载的」多两个。**
            //   所以判据从"总数不超过低水位"改成"排队数 ≤ 下载中数 + 2"。
            //   这样并发 8 时最多囤 2 条直链，而不是原来的 16 条 ——
            //   直链 TTL 有限，囤多了轮到它时已经过期，只能重取。
            int downloading = 0;   // 正在下载
            int queued = 0;        // 已拿到直链、排队等着下
            long remainBytes = 0;
            double speedSum = 0.0;

            for (int i = 0; i < _tasks.Count; i++)
            {
                var t = _tasks[i];
                if (t == null) continue;
                var st = t.State;
                if (st == JobState.Queued) { queued++; continue; }
                if (st != JobState.Downloading) continue;

                downloading++;
                if (t.Speed <= 0) continue;

                long total = t.Total;
                long done = (long)(total * (t.Percent / 100.0));
                long left = total - done;
                if (left > 0) remainBytes += left;
                speedSum += t.Speed;
            }

            // ① 主判据：排队数不超过「下载中 + 2」。
            if (queued <= downloading + QueueAheadLimit) return true;

            // ② 兜底：一个都没在下（全失败 / 全暂停 / 还没起来）时，
            //    等下去没有意义 —— 直接放行，否则取链循环会永久卡死在这里。
            if (downloading == 0 && queued > 0) return true;

            // ③ 时间兜底：在跑的任务按当前速度马上要跑完，提前补货避免空转。
            if (speedSum > 0 && (remainBytes / speedSum) <= QueueLeadSeconds) return true;

            return false;
        }

        /// <summary>
        /// 进度回调里调一次：水位满足就唤醒正在等的取链循环。
        /// 不满足时什么都不做（**不要**在这里做任何 UI 或磁盘操作）。
        /// </summary>
        private void PumpWatermark()
        {
            var tcs = _watermarkTcs;
            if (tcs == null || tcs.Task.IsCompleted) return;
            if (!WatermarkSatisfied()) return;
            tcs.TrySetResult(true);
        }

        /// <summary>
        /// 等到队列降到水位以下再返回。
        /// <para>
        /// 主路径靠 <see cref="PumpWatermark"/> 被动唤醒（零轮询）；
        /// 另挂 2 秒超时兜底 —— 万一某段时间没有任何进度回调（下载端全部卡住、
        /// 或者任务刚好在「检查/建连」阶段不报进度），也不会永久等下去。
        /// </para>
        /// </summary>
        private async Task WaitForWatermarkAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (WatermarkSatisfied()) return;

            int idleRounds = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (WatermarkSatisfied()) return;

                var tcs = new TaskCompletionSource<bool>();
                _watermarkTcs = tcs;
                bool woken = false;
                try
                {
                    var done = await Task.WhenAny(tcs.Task, Task.Delay(2000)).ConfigureAwait(true);
                    woken = done == tcs.Task;
                }
                finally
                {
                    _watermarkTcs = null;
                }
                if (woken) return;

                // 🚨 逃生阀：连续多轮（每轮 2 秒）没有任何进度回调 —— 说明下载端
                //    要么卡住、要么全部失败、要么在某个闸门上排队。这时不能无限等，
                //    否则整批文件的取链会永远停在这里（用户看到的就是「卡在入队中」）。
                if (++idleRounds >= 5)
                {
                    Log("取链等待超时（队列长时间没有进度），继续取下一批。");
                    return;
                }
            }
        }


        // ==================== 二级分组：一次下载 = 一个任务 ====================

        /// <summary>
        /// 一次「点下载」对应的任务分组（ID 决定分组边界，Name 决定显示什么）。
        /// <para>为什么用类而不是直接传两个 string：分组 ID 与名字必须**成对**传递，
        /// 拆成两个参数很容易只传一个，导致列表里出现"有名字但归错组"。</para>
        /// </summary>
        private sealed class TaskGroupRef
        {
            public string Id;
            public string Name;
        }

        /// <summary>任务分组的单调自增序号，**只用于生成唯一 ID**，不再参与显示名。</summary>
        private int _taskGroupSeq;

        /// <summary>
        /// 已经用过的任务显示名。第二级分组键用的是**显示名**，
        /// 而 WPF 按「键相等」归组 —— 两次都叫「新建文件夹」的任务会被并成一组，
        /// 用户会看到"一个任务里混了两批文件"。这里保证同名自动追加序号。
        /// </summary>
        private readonly HashSet<string> _taskGroupNamesUsed = new HashSet<string>();

        /// <summary>
        /// 为这一次「点下载」造一个任务分组。
        /// <para>
        /// 名字优先用**选中的那个目录名**（用户下了一整个文件夹时，"任务 1" 毫无意义，
        /// 文件夹名才看得出这是什么）；只选了散文件时才退回"任务 N"。
        /// </para>
        /// <para>
        /// ID 用「时间戳 + 序号」而不是名字：两次下载选同名目录是常事，
        /// 按名字分组会把两个不同任务并成一个，用户会看到"一个任务里混了两批文件"。
        /// </para>
        /// </summary>
        /// <summary>
        /// 为「下载这些文件」（<see cref="StartDownloadFromItems"/>）造任务分组。
        /// <para>与 <see cref="MakeTaskGroup"/> 的区别只是名字来源：这里没有
        /// <see cref="ShareFileItem"/>，只能用条目 <c>RelDir</c> 的顶层目录名。</para>
        /// </summary>
        private TaskGroupRef MakeTaskGroupFromItems(IList<DownloadItem> items)
        {
            string name = null;
            if (items != null && items.Count > 0)
            {
                string top = null;
                bool same = true;
                for (int i = 0; i < items.Count; i++)
                {
                    string rel = items[i] != null ? (items[i].RelDir ?? "") : "";
                    int cut = rel.IndexOfAny(new[] { '/', '\\' });
                    string seg = cut > 0 ? rel.Substring(0, cut) : "";
                    if (i == 0) top = seg;
                    else if (!string.Equals(top, seg, StringComparison.Ordinal)) { same = false; break; }
                }
                if (same && !string.IsNullOrWhiteSpace(top))
                    name = top.Trim();
                else if (items.Count == 1 && !string.IsNullOrWhiteSpace(items[0]?.Name))
                    name = items[0].Name.Trim();   // 单文件散选：用文件名兜底
            }
            return NewTaskGroupRef(name, DownloadPanKind);
        }


        /// <summary>
        /// 造一个任务分组（两条下载入口共用）。
        /// <para>
        /// <paramref name="name"/> 为空 → 自动叫「任务 N」。
        /// 重名自动追加序号 —— 第二级分组键用的就是**显示名**，
        /// 而 WPF 按"键相等"归组，两次都叫「新建文件夹」的任务会被并成一个。
        /// </para>
        /// </summary>
        private TaskGroupRef NewTaskGroupRef(string name, PanKind kind)
        {
            int seq = ++_taskGroupSeq;   // 仅用于生成唯一 ID，不参与显示

            // 名字来源（用户 2026-11-08 拍板「任务 N 误导」）：
            //   优先用调用方给的目录名/文件名；没给（散文件也取不到顶层目录名）时
            //   不再叫「任务 N」——那是个「本会话累计下载次数」计数器，不是「当前并发序号」，
            //   单组下载却显示「任务 3」纯属误导。兜底统一叫「下载任务」。
            if (string.IsNullOrWhiteSpace(name))
                name = "下载任务";

            if (!_taskGroupNamesUsed.Add(name))
            {
                int dup = 2;
                string candidate;
                do { candidate = name + " (" + dup + ")"; dup++; }
                while (!_taskGroupNamesUsed.Add(candidate));
                name = candidate;
            }

            return new TaskGroupRef
            {
                Id = "tg:" + DateTime.Now.Ticks.ToString() + ":" + seq,
                Name = name,
            };
        }


        private TaskGroupRef MakeTaskGroup(IList<ShareFileItem> selected)
        {
            // 只选了一个**目录**时用目录名当任务名（最常见的"下整个文件夹"场景）。
            string name = null;
            if (selected != null && selected.Count > 0)
            {
                ShareFileItem dir = null;
                for (int i = 0; i < selected.Count; i++)
                {
                    var it = selected[i];
                    if (it == null || !it.IsDir) { dir = null; break; }
                    if (dir == null) dir = it;
                }
                if (dir != null && selected.Count == 1 && !string.IsNullOrWhiteSpace(dir.Name))
                    name = dir.Name.Trim();
                else if (selected.Count == 1 && selected[0] != null && !selected[0].IsDir
                         && !string.IsNullOrWhiteSpace(selected[0].Name))
                    name = selected[0].Name.Trim();   // 单文件散选：用文件名兜底
            }
            return NewTaskGroupRef(name, DownloadPanKind);
        }


        /// <summary>
        /// 顺次取链 + 顺次下载。
        /// 按块取链，每块取到立刻建任务、入队、开始下载；后面的块继续在后台取。
        /// 取链进度通过灵动岛显示（见 <see cref="SetIslandChainProgress"/>）。
        /// </summary>
        private async Task QueueInChunksAsync(List<ShareFileItem> files, bool forceNoSave,
            PanKind srcKind, CancellationToken ct, TaskGroupRef taskGroup = null)
        {
            // 每块 30 个：够填满并发，又不至于让首块等太久。
            // 🚨 首块单独缩小到 8 个：2000 个文件时用户最在意的是「点了多久才真正开始下载」，
            //    首块越小越快开出第一批下载，后面的块再按 30 个走满吞吐。
            const int ChunkSize = 30;
            const int FirstChunkSize = 8;
            var chunks = new List<List<ShareFileItem>>();
            int pos = 0;
            if (files.Count > 0)
            {
                int first = Math.Min(FirstChunkSize, files.Count);
                chunks.Add(files.Take(first).ToList());
                pos = first;
            }
            for (; pos < files.Count; pos += ChunkSize)
                chunks.Add(files.Skip(pos).Take(ChunkSize).ToList());

            var chain = new ChainRun(chunks.Count, files.Count);
            var takenDests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int queued = 0;
            // 🚨 取链循环是否正常走完。只有「没走完」（取消/异常）才需要在 finally 里
            //    作废剩余块并兜底收尾；正常走完时收尾由最后一块的 NotifyChunkDone 负责，
            //    否则这里会再收一次 → 汇总日志打两遍、中转文件清理两次。
            bool loopCompleted = false;

            // 🚨 立刻把灵动岛亮出来，别等第一块取完。
            // 2000 个文件第一块要等 1~2 秒，用户点完「下载」后界面什么都不动，
            // 看起来就像卡死了（用户 m12025 明确要求「不应该在入队中的时候就显示灵动岛了吗」）。
            SetIslandChainProgress(0, files.Count);

            // 🚨 抑制 pending 落盘：取链期间每块入队都会让 _tasks.Count 变一次，
            // 不抑制就是每块一次「读整个 state.json + DPAPI 全解密 + 加密写盘」，
            // 2000 个文件 67 块 → UI 线程冻死（见 _pendingSuppress 注释）。
            _pendingSuppress++;
            try
            {
                // 🚨 水位驱动取链（2026-10-31）。
                //
                //    以前这里是「按块顺序狂取」：每块取完就立刻 `_ = RunJobsCoreAsync(...)`
                //    火忘，然后马上取下一块。取链一块只要 1~2 个 HTTP 请求（几百毫秒），
                //    而下载端默认只有 3 个并发 —— 结果 2000 个文件的直链在几十秒内全部取完，
                //    排在队列里干等半小时以上，5 分钟的直链 TTL 早就过了。
                //    表现就是用户说的「为什么仍会连续获取大量直链」+「直链过期」。
                //
                //    现在改成**按需补货**：只有当队列里剩下的活快干完了，才去取下一批。
                //    判据见 WatermarkSatisfied()（剩余任务数 或 剩余时间预算）。
                //    首块不等 —— 用户刚点下载，必须立刻看到东西动起来。
                ResetQueueWatermark();

                for (int ci = 0; ci < chunks.Count; ci++)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = chunks[ci];

                    if (ci > 0)
                        await WaitForWatermarkAsync(ct);

                    ct.ThrowIfCancellationRequested();

                    List<DownloadItem> items;
                    try
                    {
                        items = await GetDownloadItemsAsync(chunk, ct, forceNoSave);
                    }
                    catch (CapacityDowngradeException) when (!forceNoSave)
                    {
                        // 容量不足：余下全部改走免转存，不再每块都撞一次墙。
                        Log("网盘空间不足，后续文件改为免转存直接下载。");
                        forceNoSave = true;
                        items = await GetDownloadItemsAsync(chunk, ct, true);
                    }

                    var jobs = BuildJobs(items, srcKind, ct, takenDests);
                    if (jobs.Count == 0)
                    {
                        chain.SkipChunk();
                        continue;
                    }

                    // 整块一次性加进列表：30 条只触发 1 次视图刷新（见 AddTaskRows 注释）
                    var newRows = new List<TaskItem>(jobs.Count);
                    foreach (var job in jobs)
                        newRows.Add(new TaskItem(job, job.Name,
                            taskGroup != null ? taskGroup.Id : null,
                            taskGroup != null ? taskGroup.Name : null));
                    AddTaskRows(newRows);
                    chain.AllJobs.AddRange(jobs);
                    queued += jobs.Count;

                    if (chunks.Count > 1)
                        Log("已加入下载队列: " + jobs.Count + " 个文件（累计 " + queued + "/" + files.Count + "）");
                    else
                        Log("已加入下载队列: " + jobs.Count + " 个文件");

                    // 灵动岛：累加已入队数量，并刷新取链进度
                    ShowIsland(jobs.Count, SumJobBytes(jobs));
                    SetIslandChainProgress(queued, files.Count);

                    chain.ChunkStarted();
                    _ = RunJobsCoreAsync(jobs, chain);
                }
                loopCompleted = true;
            }
            finally
            {
                // 解除抑制，并强制把最终状态落一次盘（抑制期间可能跳过了最后一次）
                _pendingSuppress--;
                if (_pendingSuppress <= 0)
                {
                    _pendingSuppress = 0;
                    _pendingSkipped = 0;
                    _pendingLightSig = -1;   // 让指纹失效，保证下面这次一定真落盘
                    SyncPending();
                }
                // 取链结束（无论完成还是异常/取消）→ 撤掉灵动岛上的取链进度
                ClearIslandChainProgress();
                // 只有「取链没走完」（取消/异常）才需要作废剩余块；
                // 若此时已经没有任何块在跑，就立刻收尾，避免中转文件永远不清理。
                if (!loopCompleted && chain.AbortRemaining())
                    await FinishBatchAsync(chain.AllJobs).ConfigureAwait(true);
            }

            if (queued == 0)
            {
                // 🚨 文案按网盘区分：阿里/迅雷必须转存才有直链，扯「游客额度」纯属误导；
                //    夸克/UC 才有免转存（游客额度）这条路。
                bool canFree = srcKind == PanKind.Quark || srcKind == PanKind.Uc;
                Log(canFree
                    ? "没有取得直链（分享者可能关闭直链或游客额度限制）"
                    : "没有取得直链（" + PanUrlParser.DisplayNameOf(srcKind)
                      + " 需要先转存到自己网盘才能下载，请确认该网盘已登录且空间充足）");
            }
            else
            {
                LogDetail("取链完成：共 " + queued + " 个文件已加入队列。");
            }
        }


        /// <summary>
        /// 分批取链并累计结果（「获取直链 / 导出直链」用）。
        /// <para>与下载不同：导出必须拿齐全部链接才能弹窗，但可以边取边报进度，
        /// 避免 2000 个文件时界面长时间毫无反馈（用户 2026-10-29 要求）。</para>
        /// </summary>
        private async Task<List<DownloadItem>> FetchItemsChunkedAsync(
            List<ShareFileItem> files, CancellationToken ct, bool forceNoSave = false)
        {
            const int ChunkSize = 30;
            var all = new List<DownloadItem>();
            int done = 0;

            try
            {
                for (int i = 0; i < files.Count; i += ChunkSize)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = files.Skip(i).Take(ChunkSize).ToList();

                    List<DownloadItem> part;
                    try
                    {
                        part = await GetDownloadItemsAsync(chunk, ct, forceNoSave);
                    }
                    catch (CapacityDowngradeException) when (!forceNoSave)
                    {
                        Log("网盘空间不足，后续文件改为免转存直接下载。");
                        forceNoSave = true;
                        part = await GetDownloadItemsAsync(chunk, ct, true);
                    }

                    if (part != null && part.Count > 0) all.AddRange(part);
                    done += chunk.Count;

                    // 只有分多批时才值得报进度（单批报进度反而闪一下）
                    if (files.Count > ChunkSize)
                    {
                        LogDetail("正在取直链 " + done + "/" + files.Count + "…");
                        SetIslandChainProgress(done, files.Count, "正在准备直链");
                    }
                }
            }
            finally
            {
                ClearIslandChainProgress();
            }
            return all;
        }


        /// <summary>
        /// 把取到直链的文件组装成下载任务
        /// （唯一目标路径、连接数分配、续传上下文）。
        /// </summary>
        private List<DownloadJob> BuildJobs(List<DownloadItem> items, PanKind srcKind,
            CancellationToken ct, HashSet<string> takenDests)
        {
            var jobs = new List<DownloadJob>();
            if (items == null || items.Count == 0)
                return jobs;

            // 连接数按文件大小比例分配（大文件多分、小文件少分，各不低于下限）
            // 🚨 2026-11-07：必须带上 srcKind —— 否则 per-pan 线程设置对新建任务完全无效。
            var alloc = AllocateThreads(items.Select(x => x.Size).ToList(), srcKind);
            LogAllocation(alloc);

            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var it = items[i];
                string destDir = Path.Combine(EffectiveOutDir(), it.RelDir ?? "");
                try { Directory.CreateDirectory(destDir); }
                catch (Exception ex) { Log("创建下载目录失败: " + destDir + " — " + ex.Message); continue; }
                string dest = PathUtil.UniquePath(Path.Combine(destDir, PathUtil.SanitizeFileName(it.Name)), takenDests);
                var item = it;   // 闭包捕获：每次循环一个新变量，避免所有回调指向同一个
                jobs.Add(new DownloadJob
                {
                    Name = item.Name, Url = item.Url, Dest = dest,
                    Cookie = DownloadCookie,
                    Referer = OwnDownloadReferer,
                    UserAgent = OwnDownloadUserAgent,
                    Origin = OwnDownloadOrigin,
                    UseOpenSsl = OwnDownloadUseOpenSsl,
                    // 同上：按任务实际来源网盘限流，不按当前登录账号。
                    MaxParallelCount = ThreadsFor(srcKind),
                    // 慢连接的绝对下限也按网盘分档（见 SlowFloorFor 的注释）：
                    // 阿里/UC 单 IP 总配额低，用夸克的 16KB/s 会把全部连接误判成慢。
                    SlowFloorBps = SlowFloorFor(srcKind),
                    // 2026-11-05：下载数据面按网盘补齐客户端头，与取链身份一致。
                    PanKind = (int)srcKind,
                    ExtraHeaders = DownloadExtraHeadersFor(srcKind),
                    Threads = i < alloc.Length ? alloc[i] : ThreadsFor(srcKind),
                    Md5 = item.Md5, Sha1 = item.Sha1, Sha256 = item.Sha256,
                    // 分片根目录：留空 = 与目标同级（默认）。非空则分片集中存放，
                    // 下载目录不被 .qparts 污染，也支持分片跨盘（如放 SSD）。
                    PartsRoot = string.IsNullOrWhiteSpace(_settings.PartsRoot) ? null : _settings.PartsRoot,
                    // 2026-11-04：源文件指纹（同上一处，见 BuildSourceKey 的注释）。
                    SourceKey = BuildSourceKey(item.Size, (int)srcKind, item.Fid),
                    // 直链带时效签名，长任务下到一半会过期。
                    // 给下载器一个"重新取链"的口子，它就能自动换链续传，而不是整个任务失败。
                    LinkRefresher = ct2 => RefreshLinkAsync(item, ct2),
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
                        Md5 = item.Md5, Sha1 = item.Sha1, Sha256 = item.Sha256,
                        // 自动重解析分享的上下文（stoken 过期时换新凭证用）
                        StartFid = item.StartFid, ShareRelDir = item.ShareRelDir,
                        PanKind = (int)srcKind,
                        // 🚨 2026-11-03：同第一处 —— 链接与转存 fid 都要落进续传记录。
                        ShareUrl = item.ShareUrl ?? _shareUrl,
                        SavedFid = item.SavedFid,
                    },
                });
                TrackItemForDest(dest, item);
            }
            return jobs;
        }


        private async Task<List<ShareFileItem>> CollectFilesAsync(IEnumerable<ShareFileItem> items,
            string relPrefix, int depth, CancellationToken ct = default(CancellationToken),
            Action onFileFound = null)
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
                        // 「我的网盘」永远走当前账号的客户端（与 FetchDirPageAsync 同款说明）；
                        // 分享模式下的非夸克网盘才走 provider。
                        // ⚠️ 这里必须显式分支：下面的 catch 是"跳过该目录继续"，
                        //    如果漏走 provider 就会**静默少收文件** ——
                        //    用户点「下载文件夹」却只下到一部分，且没有任何报错。
                        if (!ShareMode)
                        {
                            children = new List<QuarkFileEntry>(
                                await OwnListDirAsync(it.Fid).ConfigureAwait(true));
                        }
                        else if (UsesProvider)
                        {
                            var page = await FetchOtherPanDirPageAsync(it.Fid, 1)
                                .ConfigureAwait(true);
                            children = page.Items;
                        }
                        else
                        {
                            children = await ShareClient.ShareDetailAsync(_pwdId, _stoken, it.Fid);
                        }
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
                        kids, Path.Combine(relPrefix, PathUtil.SanitizeFileName(it.Name)), depth + 1, ct,
                        onFileFound);
                    result.AddRange(sub);
                }
                else
                {
                    result.Add(new ShareFileItem
                    {
                        Fid = it.Fid, Name = it.Name, Size = it.Size, IsDir = false,
                        Token = it.Token, RelDir = relPrefix,
                    });
                    if (onFileFound != null)
                        onFileFound();
                }
            }
            return result;
        }


        private async Task<List<DownloadItem>> GetDownloadItemsAsync(List<ShareFileItem> files,
            CancellationToken ct = default(CancellationToken), bool forceNoSave = false)
        {
            if (ShareMode)
            {
                // 🚨 非夸克网盘（阿里 / 迅雷）必须走各自 provider 的取链路径（2026-10-25 修）。
                //
                //    以前这里只判断「有没有夸克 Cookie」：用户登录着夸克时 loggedIn 恒为 true，
                //    于是阿里 / 迅雷分享解析成功后点下载，会掉进下面 SaveAndGetItemsAsync 的
                //    **夸克转存**逻辑 —— 拿阿里/迅雷的分享 fid 去调夸克的 sharepage/save，
                //    必然失败，表现就是用户说的「除了夸克以外所有网盘解析文件后无法入队下载」。
                //
                //    各家的取链方式本来就不同（见 IPanProvider.GetShareDownloadLinksAsync）：
                //    迅雷内部是「转存到自己网盘的临时目录 → 取直链 → 立刻删掉」，
                //    阿里走 get_share_link_download_url。这些差异全部封装在 provider 里，
                //    上层只需要把「从分享取链」这个意图交给它，不要在这里替它做转存决策。
                // ⚠️ 只把**阿里 / 迅雷**分流出去。UC 不在此列：UC 与夸克同源，
                //    走的就是下面这套夸克链路（只是 Cookie 与域名换成 UC 的，
                //    见 OnParseClick 里 _shareClient 的构造），转存 / 免转存两条路都通。
                if (_panKind == PanKind.Aliyun || _panKind == PanKind.Xunlei)
                {
                    LogDetail("按「" + PanUrlParser.DisplayNameOf(_panKind) + "」的取链方式入队。");
                    return await NoSaveGetItemsAsync(files, ct);
                }

                // 用户要求给「免转存下载」一个显式入口。
                // 默认（登录状态下）走「转存→取直链」，会往用户网盘写一份；
                // 免转存则不碰网盘，直接从分享取链 —— 省空间、也不留中转垃圾。
                if (forceNoSave)
                {
                    LogDetail("按你的选择走「免转存」路径：不写入网盘，直接从分享取链。");
                    return await NoSaveGetItemsAsync(files, ct);
                }

                // ⚠️ 这里必须按**当前分享属于哪家**取 Cookie，不能用夸克那份：
                //    UC 分享时夸克 Cookie 可能是空的（用户只登了 UC），
                //    用 _settings.Cookie 判断会把「已登录的 UC」当成游客。
                string shareCookie = _panKind == PanKind.Uc
                    ? _settings.UcCookie
                    : _settings.Cookie;
                bool loggedIn = !string.IsNullOrEmpty(shareCookie);
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
        /// 批量取直链 + 多轮重试。对应原版 fetch_links / fetch_share_links。
        /// 相比早先的实现，补了原版有、C# 之前缺的两点：
        ///  1. **失败原因不再被吞掉**。以前是 `catch { links = new List&lt;DownloadLink&gt;(); }`，
        ///     用户只看到「跳过（无直链）」，无从判断是会员额度、风控还是分享被关。
        ///  2. **多一轮"逐个单请求"兜底**。批量接口更易被限流，剩少量文件时逐个问成功率更高。
        /// </summary>
        /// <param name="transit">
        /// true = 这些 fid 在「转存用的中转文件夹」里，归属**当前分享那一家**的账号，
        /// 必须用 <see cref="TransitClient"/> 取链，而不是当前登录账号的客户端。
        /// </param>
        private async Task<Dictionary<string, DownloadLink>> FetchUrlsWithRetryAsync(
            List<LinkTarget> targets, bool fromShare, CancellationToken ct = default(CancellationToken),
            bool transit = false)
        {
            ct.ThrowIfCancellationRequested();
            // fid → 完整取链结果（含 download_url 与内容哈希）。
            // 只存 string 会把哈希丢掉，完成时只能显示“未校验”。
            var urls = new Dictionary<string, DownloadLink>();
            var pending = new List<LinkTarget>(targets);
            var reason = new Dictionary<string, string>();
            int[] delays = { 0, 800, 1600, 3000, 5000 };

            // ---- 先查直链缓存（对应 Python 的 LINK_TTL 复用）----
            // 命中的直接从 pending 里摘掉，不再发请求。
            int cached = 0;
            var miss = new List<LinkTarget>();
            foreach (var t in pending)
            {
                var hit = _linkCache.GetLink(t.Fid);
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

                // 并发取链（2026-10-29）：原来是 20 个一批纯串行，2000 个文件要排队上百轮，
                // 每轮都要干等一次网络往返。现在仍然 20 个一批，但让 4 批同时跑，
                // 把「等网络往返」的时间叠起来。
                // 注意：真正限制请求频率的是 QuarkSession 的令牌桶
                // （平均每 MinIntervalMs 一个请求），这里的并发不会提高对服务端的请求频率。
                const int FetchConcurrency = 4;
                var batches = new List<List<LinkTarget>>();
                for (int i = 0; i < pending.Count; i += 20)
                    batches.Add(pending.Skip(i).Take(20).ToList());

                var sync = new object();
                using (var gate = new SemaphoreSlim(FetchConcurrency))
                {
                    async Task RunBatchAsync(List<LinkTarget> batch)
                    {
                        await gate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            List<DownloadLink> links = null;
                            string err = null;
                            try { links = await FetchBatchAsync(batch, fromShare, transit).ConfigureAwait(false); }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex) { err = ex.Message; }

                            var got = new Dictionary<string, DownloadLink>();
                            if (links != null)
                            {
                                foreach (var l in links)
                                {
                                    if (!string.IsNullOrEmpty(l.Fid) && !string.IsNullOrEmpty(l.DownloadUrl))
                                        got[l.Fid] = l;
                                }
                            }

                            lock (sync)
                            {
                                if (err != null)
                                {
                                    foreach (var t in batch)
                                        reason[t.Fid] = "接口报错: " + err;
                                }
                                foreach (var t in batch)
                                {
                                    DownloadLink link;
                                    if (got.TryGetValue(t.Fid, out link))
                                    {
                                        urls[t.Fid] = link;
                                    }
                                    else
                                    {
                                        if (err == null)
                                            reason[t.Fid] = "接口未返回该文件的 download_url";
                                        next.Add(t);
                                    }
                                }
                            }
                        }
                        finally { gate.Release(); }
                    }

                    var tasks = new List<Task>(batches.Count);
                    foreach (var b in batches) tasks.Add(RunBatchAsync(b));
                    await Task.WhenAll(tasks).ConfigureAwait(false);
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
                        var one = await FetchBatchAsync(new List<LinkTarget> { t }, fromShare, transit);
                        var hit = one == null ? null : one.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl));
                        if (hit != null)
                        {
                            urls[t.Fid] = hit;
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

            // ---- 写回缓存：本次取到的链 + 哈希在 TTL 内可被后续操作复用 ----
            foreach (var kv in urls)
                _linkCache.PutLink(kv.Value);

            return urls;
        }


        private async Task<List<DownloadLink>> FetchBatchAsync(List<LinkTarget> batch, bool fromShare,
            bool transit = false)
        {
            // 转存到中转文件夹后的取链：文件属于**当前分享那一家**的账号（夸克或 UC），
            // 必须用 TransitClient。用 _client 会在「登录迅雷、下 UC 分享」时拿错客户端。
            if (transit)
            {
                QuarkClient cli = TransitClient;
                if (cli == null)
                    throw new InvalidOperationException("转存目标网盘客户端未就绪，无法取直链。");
                var transitFids = new List<string>(batch.Count);
                foreach (var t in batch)
                    transitFids.Add(t.Fid);
                return await cli.GetDownloadAsync(transitFids).ConfigureAwait(true);
            }

            // 非夸克网盘：分享模式走 provider 的免转存取链；
            // 自有网盘（阿里/迅雷）走 _ownProvider —— 两者都由下面的助手分派，
            // 所以这里必须先按"是不是分享"分开，否则解析过分享后回到
            // 「我的网盘」下文件会走错分支。
            if (fromShare && ShareMode && UsesProvider)
                return await FetchOtherPanLinksAsync(batch).ConfigureAwait(true);

            if (fromShare)
            {
                return await ShareClient.GetShareDownloadAsync(
                    batch.Select(t => t.Fid).ToList(),
                    batch.Select(t => t.Token ?? "").ToList(),
                    _pwdId, _stoken).ConfigureAwait(true);
            }

            var files = new List<PanFileEntry>(batch.Count);
            foreach (var t in batch)
            {
                files.Add(new PanFileEntry
                {
                    Fid = t.Fid,
                    Token = t.Token ?? "",
                    Name = t.Name ?? "",
                });
            }
            var links = await OwnLinksAsync(files).ConfigureAwait(true);
            return links == null ? new List<DownloadLink>() : new List<DownloadLink>(links);
        }


        /// <summary>非夸克网盘的免转存取直链。</summary>
        private async Task<List<DownloadLink>> FetchOtherPanLinksAsync(List<LinkTarget> batch)
        {
            if (_panProvider == null || _panShareRef == null)
                throw new InvalidOperationException("多网盘客户端未就绪。");

            var files = PanEntryMapper.ToPanEntries(batch.Select(t =>
                new QuarkFileEntry { Fid = t.Fid, Name = t.Name, Token = t.Token ?? "" }));

            var links = await _panProvider
                .GetShareDownloadLinksAsync(_panShareRef, files)
                .ConfigureAwait(false);

            var result = new List<DownloadLink>();
            if (links != null)
                result.AddRange(links);
            return result;
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
            if (item.FromShare && UsesProvider)
            {
                // 非夸克网盘：直接向对应 provider 重新取一次链。
                // 这些客户端的取链接口每次都重新换 token，天然支持"过期重取"，
                // 不需要夸克那套 GetShareLinkWithReResolveAsync（重解析分享换 stoken）。
                try
                {
                    var one = new List<PanFileEntry>
                    {
                        new PanFileEntry { Fid = item.Fid, Name = item.Name, Token = item.Token ?? "" },
                    };
                    var fresh = await _panProvider
                        .GetShareDownloadLinksAsync(_panShareRef, one, ct)
                        .ConfigureAwait(false);
                    var hit2 = fresh == null
                        ? null
                        : fresh.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl));
                    if (hit2 != null)
                    {
                        // 重取链又转存了一份 —— 记下来（含落盘），终态一起删。
                        NoteStored(item, _panKind, hit2.StoredId);
                        _linkCache.Put(item.Fid, hit2.DownloadUrl);
                        return hit2.DownloadUrl;
                    }
                }
                catch (Exception ex)
                {
                    LogDetail("刷新直链失败（" + PanUrlParser.DisplayNameOf(_panKind) + "）: " + ex.Message);
                }
                return null;
            }

            if (item.FromShare)
            {
                // stoken 过期时自动重解析分享：换新 stoken + 新
                // share_fid_token 再取链。写回 _stoken / item.Token ——
                // 当前会话后续取链都用新值（下载器回调在线程池线程，不能读 PassBox，
                // 所以提取码从 item 字段拿）。
                string url = await GetShareLinkWithReResolveAsync(
                    ShareClient, item.Fid, item.Token, _pwdId, _stoken, item.Passcode,
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
                // 自有网盘：夸克/UC 走 _client，阿里/迅雷走 _ownProvider（由助手分派）。
                var one = new List<PanFileEntry>
                {
                    new PanFileEntry { Fid = item.Fid, Token = item.Token ?? "" },
                };
                links = (await OwnLinksAsync(one).ConfigureAwait(false)).ToList();
            }

            if (links == null)
                return null;
            var hit = links.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl));
            string u = hit == null ? null : hit.DownloadUrl;
            if (u != null)
            {
                // 新链可能带新的哈希；更新到 item，完成校验时用同一份。
                item.Md5 = hit.Md5;
                item.Sha1 = hit.Sha1;
                item.Sha256 = hit.Sha256;
                // 自有网盘重取链（阿里走转存兜底）也可能再转存一份。
                if (!string.IsNullOrEmpty(hit.StoredId))
                {
                    // 兜底判据用 DownloadPanKind（分享看 _panKind、自有看 _activeKind），
                    // 不能用 _activeKind：登录夸克却下阿里分享时，会把转存副本记到夸克名下，
                    // 终态清理就会去找夸克客户端删 —— 删不掉，还留一堆残留目录。
                    PanKind storedKind = item.StoredKind == default(PanKind) ? DownloadPanKind : item.StoredKind;
                    NoteStored(item, storedKind, hit.StoredId);
                }
                _linkCache.PutLink(hit);   // 新链写回缓存，供后续同类操作复用
            }
            return u;
        }


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
                Log("已请求取消: " + t.Name + "（将清理本地未下完的部分）");
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
                        foreach (string path in PartsPaths.EnumeratePartialPaths(dest, roots))
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
        /// <summary>
        /// 浅层探测：这些目录里**有没有任何** `.qparts` 目录（不递归深扫）。
        /// <para>
        /// 🚨 2026-11-04：融合引擎不产生 .qparts，所以绝大多数用户这里返回 false，
        /// 直接跳过 <c>SearchOption.AllDirectories</c> 的深度递归 ——
        /// 那是启动时一次很重的 I/O（下载目录可能几十万文件）。
        /// </para>
        /// <para>只探测「根目录下」和「第一层子目录」，足够判断有没有历史遗留。</para>
        /// </summary>
        private static bool HasAnyQparts(IList<string> roots, string outDir)
        {
            var dirs = new List<string>();
            if (roots != null) dirs.AddRange(roots);
            if (!string.IsNullOrWhiteSpace(outDir)) dirs.Add(outDir);

            foreach (string raw in dirs)
            {
                string dir = (raw ?? "").Trim();
                if (dir.Length == 0) continue;
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    // ① 根本身就是 .qparts
                    if (dir.EndsWith(".qparts", StringComparison.OrdinalIgnoreCase))
                        return true;
                    // ② 根下一层有没有 .qparts
                    if (Directory.GetDirectories(dir, "*.qparts").Length > 0)
                        return true;
                }
                catch
                {
                    // 权限/占用 —— 当作没有，下次再试
                }
            }
            return false;
        }


        private void StartPartsSweep()
        {
            var roots = AllPartsRoots();
            string outDir = EffectiveOutDir();

            // 🚨🚨 2026-11-04 修「用户：我的下载器根本没有分片这个东西，为什么还清扫分片」：
            //
            //   事实核查：**用户说得对**。
            //   · PartsSweeper 只扫 `*.qparts` 目录（PartsSweeper.cs:130）；
            //   · 而 `.qparts` 是**老引擎**（Legacy/SegmentedDownloader）的产物；
            //   · 老引擎已于 2026-10-29 物理删除，融合引擎是唯一引擎；
            //   · 融合引擎的续传记录只有**一个文件** `{目标}.geziresume`，
            //     **不产生任何 .qparts 目录**。
            //   ⇒ 这个清扫对当前版本来说是**纯死代码**：每次启动白扫一遍磁盘
            //      （SearchOption.AllDirectories 递归！），然后什么都不删，
            //      还打一条让用户困惑的日志。
            //
            //   保留的唯一价值：老用户旧目录里可能还有历史 .qparts 残留。
            //   但那属于「一次性迁移」场景，不该每次启动都跑。
            //   ⇒ 现在改成：**只在真的扫到东西时才扫**（先做一次浅层探测），
            //      且日志只在**真删了东西**时才打。
            var t = new Thread(() =>
            {
                try
                {
                    // 🚨 先做**浅层探测**：融合引擎不产生 .qparts，
                    //   绝大多数用户的目录里一个都没有 → 直接跳过深度递归扫描。
                    //   （SearchOption.AllDirectories 在大下载目录下是很重的 I/O）
                    if (!HasAnyQparts(roots, outDir))
                        return;

                    var r = PartsSweeper.Sweep(roots, PartsSweeper.DefaultMaxAgeDays);
                    // 默认 {dest}.qparts 不在 roots 里，单独扫一遍下载目录。
                    var d = PartsSweeper.SweepDefault(
                        string.IsNullOrWhiteSpace(outDir) ? null : new[] { outDir },
                        PartsSweeper.DefaultMaxAgeDays);

                    int removed = r.Removed + d.Removed;
                    int skipped = r.Skipped + d.Skipped;
                    long freed = r.FreedBytes + d.FreedBytes;
                    // 🚨 只有**真的删了东西**才打日志。
                    //   原来启动时无条件打一条「已启动分片自动清扫…」，
                    //   而当前版本根本没有分片 → 纯噪音，用户会困惑「哪来的分片」。
                    if (removed > 0)
                    {
                        LogFromWorker(string.Format(
                            "已清理历史遗留的临时下载文件 {0} 个（{1}），跳过 {2} 个被占用的。",
                            removed, Util.FormatSize(freed), skipped));
                    }

                    // 上次“软删除”时文件仍被占用而留下的 *.gezi-deleting：
                    // 现在进程刚启动，不可能再被占用，直接收尾。
                    var sweepRoots = new List<string>();
                    sweepRoots.AddRange(roots);
                    if (!string.IsNullOrWhiteSpace(outDir))
                        sweepRoots.Add(outDir);
                    int pending = PartsPaths.SweepPendingDeletes(sweepRoots);
                    if (pending > 0)
                        LogFromWorker(string.Format(
                            "残留清理：删除了 {0} 个上次未能删除的半成品文件。", pending));
                }
                catch (Exception ex)
                {
                    try { LogFromWorker("临时下载文件清扫失败（已忽略）: " + ex.Message); } catch { }
                }
            })
            {
                IsBackground = true,             // 后台线程：不阻止程序退出
                Name = "GeZi-PartsSweep",
            };
            t.Start();

            // 🚨 2026-11-04：这条日志**删掉了**。
            //   它原来每次启动都无条件打「已启动分片自动清扫（阈值 1 天）。扫描范围: …」，
            //   而融合引擎根本不产生分片 → 用户看到只会困惑「我的下载器哪来的分片」。
            //   现在只在**真的清理了历史遗留**时才由后台线程打一条（见上面的 removed > 0）。
        }


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


        // 🚨 2026-11-07：`SelectedThreads()` **已彻底删除**。
        //   历史：它原来读设置页的「单文件并发连接数」下拉（ThreadsBox）。
        //   后来全局下拉被删掉、只留「四家网盘分别设置」，它退化成"读 _settings.Threads"，
        //   而 _settings.Threads 当时还只能往下压 per-pan 值 —— 于是出现
        //   「夸克设 512 完全无效、界面始终显示 256」这个用户报的 bug。
        //   现在并发数的**唯一来源**是 `ThreadsFor(PanKind)`（见 MainWindow.Log.cs），
        //   它直接读 per-pan 覆盖值、再按各家服务端上限夹取。
        //   `_settings.Threads` 字段仍保留（老 GeZi.config.xml 存过，删了反序列化会炸），
        //   语义降级为「四家都没单独配置时的回落基准」，只被 `DefaultThreadsFor` 读。


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

            // 2026-11-01（方案 B）：设置页的「分片/缓存根目录」输入框已删除，
            // 但 **历史清单必须继续读** —— 老用户旧目录里的 .qparts 残留就靠它才扫得到、清得掉。
            // PartsRoot 字段本身保留（老配置里可能存着值），仍然纳入清理范围。
            Add(_settings.PartsRoot);
            if (_settings.PartsDirsKnown != null)
                foreach (var d in _settings.PartsDirsKnown) Add(d);
            return outList;
        }

        // ⚠️ 2026-11-01（用户拍板方案 B）：OnBrowsePartsRoot / OnOpenPartsRoot 两个方法已删除，
        //    它们只服务于设置页那块「分片/缓存根目录」输入框，该输入框已整块移除。
        //    历史残留的清理不受影响：AllPartsRoots() 仍然会读 Settings.PartsDirsKnown，
        //    只是不再有"当前生效值"（PartsRoot 字段保留，仅用于兼容老配置反序列化）。
    }
}
