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
// 职责：日志、诊断、提示、忙态

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


        /// <summary>来自下载工作线程的日志。入队线程安全，无需再切 UI 线程。</summary>
        /// <summary>
        /// 每个网盘能承受的最大并发连接数（0 = 不限，交给全局预算均分）。
        ///
        /// <para>
        /// 这是「不同网盘不同策略」的落点。实测依据：
        /// </para>
        /// <list type="bullet">
        /// <item><b>迅雷 = 16</b>：单 IP 并发上限。137 分片全开时 CDN 大面积 503，
        /// 重试退避把速度压到 1 MB/s；16 并发实测 12.98 MB/s。</item>
        /// <item><b>阿里云盘 = 64</b>：单 IP 约 2.5~2.7 MB/s 硬限速，64 并发已到顶，
        /// 128 只多 11% 却更容易触发限流。</item>
        /// <item><b>夸克 = 0（不限）</b>：没有这个限制，256 并发能跑 20+ MB/s。</item>
        /// <item><b>UC = 256</b>：单 IP 总带宽约 7~8 MB/s，连接再多只会把每条摊薄到看门狗下限以下。</item>
        /// </list>
        /// </summary>
        private static int MaxParallelFor(PanKind kind)
        {
            switch (kind)
            {
                case PanKind.Xunlei:
                    return 16;
                case PanKind.Aliyun:
                    return 64;
                // UC 与夸克共用 QuarkClient，但两者的 CDN 限流表现不同：
                // UC 单 IP 总带宽约 7~8 MB/s，连接堆到 300+ 时每条只剩 13 KB/s 左右，
                // 正好掉到慢连接看门狗下限（16 KB/s）以下 → 大面积断开重连 →
                // 握手开销把总速度压得更低（实测 2 任务 378 连接只有 4.85 MB/s，
                // 关掉一个降到 256 连接反而涨到 7.07 MB/s）。封 256 让单连接保持在 27 KB/s 上下。
                case PanKind.Uc:
                    return 256;
                default:
                    return 0;
            }
        }


        private void LogFromWorker(string msg)
        {
            // 🚨 引擎的 [debug] 级日志一律不进界面。
            // 实测教训：bezzad 的 ChunkDownloader 原本每个 4KB 缓冲打 3 条 debug，
            // 256 连接下每秒上万条 —— 它们全是 detail=true，会把 _logAll 撑爆，
            // TrimLogLines 一砍就把「解析成功/开始下载/下载完成」这些正常日志
            // 全部挤出窗口，于是「简化」视图变空白、只剩「详细」里满屏英文。
            // 逐包级别的追踪没有诊断价值（要看也是看分片级的），直接丢弃。
            if (msg != null && msg.StartsWith("[debug]", StringComparison.Ordinal))
                return;

            // 下载内核的过程日志（换链、抢占、停滞…）整体算专业级 ——
            // 日常视图只看任务最终结果（失败/取消走 OnTaskUpdate，是日常级）。
            EnqueueLog(msg, detail: true);
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
        /// 启动时把 <c>GeZi.log</c> 的尾部灌回 <see cref="_logAll"/>，让「上次关掉前的日志」还在。
        /// <para>
        /// 2026-10-26 用户诉求「日志与下载记录持久化，退出重进不丢」。
        /// 落盘格式里每条**详**级行带 <c>[详] </c> 前缀（见 <see cref="EnqueueLog"/>），
        /// 这里按前缀还原 <see cref="LogLine.Detail"/> —— 否则恢复回来的详细日志
        /// 在「简要」档位下会全部显出来。
        /// </para>
        /// <para>
        /// ⚠️ 恢复的行前面插一条分隔，让用户一眼看出哪些是上次的。
        /// ⚠️ 只填 <see cref="_logAll"/>，最后统一 <see cref="RebuildLogBox"/>。
        /// </para>
        /// </summary>
        private void RestoreLogFromDisk()
        {
            try
            {
                var lines = LogStore.LoadTail(LogMaxLines);
                if (lines == null || lines.Count == 0) return;
                foreach (var raw in lines)
                {
                    if (string.IsNullOrEmpty(raw)) continue;
                    bool detail = raw.StartsWith("[详] ", StringComparison.Ordinal);
                    _logAll.Add(new LogLine
                    {
                        Text = detail ? raw.Substring(4) : raw,
                        Detail = detail,
                    });
                }
                if (_logAll.Count > LogMaxLines)
                    _logAll.RemoveRange(0, _logAll.Count - LogMaxLines);
                RebuildLogBox();
            }
            catch { }
        }


        /// <summary>
        /// 日志档位分段控件（简要 / 详细）切换。
        /// <para>
        /// 2026-10-26 从单个「详细」开关按钮改成两段式 —— 用户反馈
        /// 「日志的详细和简约感觉不是太明确」。原来的按钮要按下才知道自己是哪种，
        /// 现在当前档位由白底滑块直接表达（和「分享链接 / 我的网盘」同一套控件）。
        /// </para>
        /// </summary>
        private void OnLogLevelChanged(object sender, RoutedEventArgs e)
        {
            // 两个 RadioButton 共用这一个处理器，只认「详细」那个是否选中。
            bool verbose = LogVerboseRadio != null && LogVerboseRadio.IsChecked == true;
            if (verbose == _logVerbose) return;
            _logVerbose = verbose;
            RebuildLogBox();
        }


        /// <summary>
        /// 导出诊断包：运行日志 + 环境信息 + 设置摘要（已脱敏）。
        /// 明确不含 Cookie、登录票据与下载直链 —— 用户可以放心把它贴到 issue 里。
        /// </summary>
        private void OnExportDiagnosticsPack(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "导出诊断包",
                    Filter = "ZIP 压缩包|*.zip",
                    FileName = "GeZi-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip",
                    AddExtension = true,
                    OverwritePrompt = true,
                };
                if (dlg.ShowDialog(this) != true)
                    return;

                string logText = null;
                try
                {
                    // 🚨 用**全量历史** `_logAll`，不能用 `LogBox.Text`（2026-11-01 修）。
                    //    LogBox 是「当前档位过滤后」的视图：用户停在「简要」时，
                    //    专业级行一条都不在里面，导出的诊断包会缺一大半内容 ——
                    //    这正是用户说的「日志里显示的东西不够全面」。
                    //    全量导出时给专业级行补上 [详] 前缀，和 GeZi.log 的落盘格式保持一致。
                    var logSb = new StringBuilder();
                    for (int i = 0; i < _logAll.Count; i++)
                    {
                        var l = _logAll[i];
                        if (l == null) continue;
                        if (l.Detail) logSb.Append("[详] ");
                        logSb.Append(l.Text).Append(Environment.NewLine);
                    }
                    logText = logSb.ToString();
                }
                catch { logText = null; }

                // 把 startup-error.log（全局异常堆栈）一并打进包 —— 界面上只显示一句中文，
                // 真正的堆栈只在这个文件里。不带上它，用户反馈的异常就没法定位。
                try
                {
                    string fatalPath = System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "startup-error.log");
                    if (System.IO.File.Exists(fatalPath))
                    {
                        var fatalLines = System.IO.File.ReadAllLines(fatalPath);
                        int take = Math.Min(fatalLines.Length, 400);
                        var fatalSb = new StringBuilder();
                        fatalSb.AppendLine();
                        fatalSb.AppendLine("===== startup-error.log（最近 " + take + " 行）=====");
                        for (int i = fatalLines.Length - take; i < fatalLines.Length; i++)
                            fatalSb.AppendLine(fatalLines[i]);
                        logText = (logText ?? "") + fatalSb.ToString();
                    }
                }
                catch { }

                string version = "";
                try { version = typeof(MainWindow).Assembly.GetName().Version.ToString(); } catch { }

                var info = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("单文件并发", _settings.Threads.ToString()),
                    new KeyValuePair<string, string>("同时下载任务数", _settings.ConcurrentTasks.ToString()),
                    new KeyValuePair<string, string>("下载目录", _settings.OutDir ?? ""),
                    // 2026-11-01（方案 B）：设置页的分片根目录输入框已删除，
                    // 诊断包里改成如实描述「历史残留目录」，避免用户以为它还影响下载。
                    new KeyValuePair<string, string>("历史分片残留目录",
                        _settings.PartsDirsKnown != null && _settings.PartsDirsKnown.Count > 0
                            ? string.Join("；", _settings.PartsDirsKnown)
                            : "(无)"),
                    new KeyValuePair<string, string>("自动清理", _settings.AutoClean ? "开" : "关"),
                    new KeyValuePair<string, string>("使用系统代理", _settings.UseSystemProxy ? "开" : "关"),
                    new KeyValuePair<string, string>("主题", _settings.Theme.ToString()),
                    new KeyValuePair<string, string>("已登录", string.IsNullOrEmpty(_settings.Who) ? "否" : "是"),
                    new KeyValuePair<string, string>("全局连接预算",
                        ThreadAllocator.DefaultBudget.ToString()),
                };

                // 🚨 2026-10-26：原来这里只有「已登录 是/否」一行，是**单网盘时代**的写法。
                //    现在四家各自独立登录、还各有多账号，出问题时根本看不出是哪一家的哪一环。
                //    补上：当前在用哪家、四家各自的登录/凭证/账号数。
                //    ⚠️ 只写「有没有、几个」，绝不写 Cookie / token 本身。
                info.Add(new KeyValuePair<string, string>("当前网盘",
                    IsLoggedInAny ? PanUrlParser.DisplayNameOf(_activeKind) : "(未登录)"));
                info.Add(new KeyValuePair<string, string>("当前模式", ShareMode ? "分享链接" : "我的网盘"));
                foreach (var k in new[] { PanKind.Quark, PanKind.Uc, PanKind.Aliyun, PanKind.Xunlei })
                {
                    string state;
                    try
                    {
                        bool live = PanIsLive(k);
                        bool cred = HasPanCredential(k);
                        int count = AccountStore.LoadAll(k).Count;
                        state = (live ? "在线" : (cred ? "有凭证" : "未登录"))
                                + " · 账号 " + count + " 个";
                    }
                    catch (Exception ex)
                    {
                        state = "(读取失败: " + ex.Message + ")";
                    }
                    info.Add(new KeyValuePair<string, string>(
                        PanUrlParser.DisplayNameOf(k), state));
                }

                string path = DiagnosticsPack.Create(dlg.FileName, "鸽子下载 GeZi", version, logText, info);
                LogDetail("诊断包已导出: " + path);
            }
            catch (Exception ex)
            {
                LogDetail("导出诊断包失败: " + ex.Message);
            }
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


        /// <summary>进入加载态前的原始 Content，退出时原样还原（含图标等复杂内容）。</summary>
        private readonly Dictionary<Button, object> _busySavedContent = new Dictionary<Button, object>();


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


        /// <summary>造一个"小转圈"（缺口圆弧 + 无限旋转）。Stroke 由调用方设置。</summary>
        private static ShapePath MakeSpinner()
        {
            return UiBusy.MakeSpinner();
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


        /// <summary>列表底部那行提示：还有没加载完的就说清楚。</summary>
        private void UpdateDirMoreHint()
        {
            // 删除「已显示 N 项 · 下面还有，下滑继续加载」这行提示。
            // 分页加载逻辑本身保留（滚到底自动续），只是不再显示这行文字。
            if (FileMoreHint != null) FileMoreHint.Visibility = Visibility.Collapsed;
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


        private void OnDiagnostics(DiagnosticsSnapshot snap)
        {
            if (snap == null) return;
            // 必须先封送到 UI 线程，否则后面整段都是白干。
            // `Diagnostics` 是裸 `Action<T>`：下载引擎在**自己的主循环线程**里
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


        // 2026-11-02（用户拍板）：诊断面板 UI 已整体删除。
        // 数据采集（_diagLatest / OnDiagnostics / DiagnosticsSnapshot）保留在引擎侧，
        // 随「打包诊断」一起导出；这里不再有独立窗口。
        // ⚠️ 别把 _diagLatest 一起删 —— 那是排障能力的根，删了以后再也看不到实时连接数。



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
    }
}
