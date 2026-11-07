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
        /// 🚨 2026-11-07：引擎侧重复日志抑制器。
        /// 取消一次下载会灌进上百条「分片 N 发生致命错误 / 连接被关闭」，
        /// 每 2 秒一轮的「整体限速中」「下载中…」也会刷屏。
        /// 做法：按「去掉数字与分片 ID 后的模板」归并，同一模板 8 秒内只出 1 条，
        /// 其余累计；窗口到期时补一条「（同类 N 条已合并）」。
        /// ⚠️ 只作用于 LogFromWorker（专业级引擎日志）；日常级 Log() 一条不拦，
        ///    任务完成/失败/删除结果等核心信息永远完整保留。
        /// </summary>
        private readonly Dictionary<string, int> _logDupCount = new Dictionary<string, int>();
        private readonly Dictionary<string, DateTime> _logDupSince = new Dictionary<string, DateTime>();
        private readonly object _logDupGate = new object();
        private static readonly TimeSpan LogDupWindow = TimeSpan.FromSeconds(8);

        /// <summary>把一条日志归一成模板键：抹掉数字、分片 ID、十六进制串，保留中文骨架。</summary>
        private static string LogDupKey(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return "";
            string s = Regex.Replace(msg, @"[0-9a-fA-F]{6,}", "H");
            s = Regex.Replace(s, @"\d+", "N");
            return s.Length > 120 ? s.Substring(0, 120) : s;
        }

        /// <summary>这些是结果性信息，重复也必须每条都显示，不参与抑制。</summary>
        private static bool LogDupExempt(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return true;
            return msg.IndexOf("全部完成", StringComparison.Ordinal) >= 0
                || msg.IndexOf("下载完成", StringComparison.Ordinal) >= 0
                || msg.IndexOf("已成功删除", StringComparison.Ordinal) >= 0
                || msg.IndexOf("删除并移除", StringComparison.Ordinal) >= 0
                || msg.IndexOf("已丢弃", StringComparison.Ordinal) >= 0
                || msg.IndexOf("任务已全部结束", StringComparison.Ordinal) >= 0
                || msg.IndexOf("收尾长度核对", StringComparison.Ordinal) >= 0
                || msg.IndexOf("内容校验", StringComparison.Ordinal) >= 0
                || msg.IndexOf("哈希复核", StringComparison.Ordinal) >= 0
                || msg.IndexOf("连接租约", StringComparison.Ordinal) >= 0;
        }

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
            // 🚨 2026-11-07：只抑制**明确的高频过程类**日常日志（白名单），
            //    用户操作结果、任务结果一条都不合并。
            //    反复切网盘时「目录加载完成 / 已切换到 X」能刷几十行，
            //    反复切主题时「外观已切换」同理 —— 这些不是核心信息。
            if (IsHighFrequencyUiLog(msg))
            {
                string key = "ui:" + LogDupKey(msg);
                bool emit = false;
                string summary = null;
                lock (_logDupGate)
                {
                    DateTime now = DateTime.UtcNow;
                    DateTime since;
                    if (!_logDupSince.TryGetValue(key, out since))
                    {
                        _logDupSince[key] = now;
                        _logDupCount[key] = 0;
                        emit = true;
                    }
                    else if ((now - since) >= TimeSpan.FromSeconds(15))
                    {
                        int n;
                        _logDupCount.TryGetValue(key, out n);
                        _logDupSince[key] = now;
                        _logDupCount[key] = 0;
                        emit = true;
                        if (n > 0) summary = "（同类 " + n + " 条已合并）";
                    }
                    else
                    {
                        int n;
                        _logDupCount.TryGetValue(key, out n);
                        _logDupCount[key] = n + 1;
                    }
                }
                if (summary != null) EnqueueLog(summary, detail: false);
                if (!emit) return;
            }
            EnqueueLog(msg, detail: false);
        }

        /// <summary>明确的高频 UI 过程日志（只这几类参与合并，核心结果一律不拦）。</summary>
        private static bool IsHighFrequencyUiLog(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return false;
            return msg.IndexOf("目录加载完成", StringComparison.Ordinal) >= 0
                || msg.IndexOf("目录来自缓存", StringComparison.Ordinal) >= 0
                || msg.IndexOf("已切换到 ", StringComparison.Ordinal) >= 0
                || msg.IndexOf("外观已切换", StringComparison.Ordinal) >= 0
                || msg.IndexOf("外观已设为跟随系统", StringComparison.Ordinal) >= 0
                || msg.IndexOf("正在校验登录态", StringComparison.Ordinal) >= 0
                || msg.IndexOf("正在自动登录", StringComparison.Ordinal) >= 0;
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
                    return 0;   // 夸克：不限
            }
        }


        /// <summary>
        /// 2026-11-05：这个网盘**实际**可用的并发连接数。
        ///
        /// <para>
        /// 优先级：用户在「四网盘分别设置」里填的值 → 全局 <c>Threads</c> →
        /// 服务端硬上限 <see cref="MaxParallelFor"/>。
        /// </para>
        /// <para>
        /// ⚠️ 硬上限**永远保留**：迅雷 16 / 阿里 64 / UC 256 是实测 503 / 掉速阈值，
        /// 用户配到 512 也只按 16 跑，否则等于让用户一键把下载搞挂。
        /// per-pan 值为 0 表示「没有单独覆盖、回落到默认基准」，是老配置升级后的默认状态。
        /// </para>
        /// <para>
        /// 🚨 2026-11-07：内部拆出 <see cref="DefaultThreadsFor"/> 供弹窗「恢复默认」用，
        ///    本方法行为与拆分前逐字等价（覆盖值优先、否则走默认基准、都叠加上限）。
        /// </para>
        /// </summary>
        private int ThreadsFor(PanKind kind)
        {
            int want = ThreadsOverrideFor(kind);
            if (want <= 0) return DefaultThreadsFor(kind);

            int cap = MaxParallelFor(kind);
            if (cap > 0 && want > cap) return cap;
            return want;
        }

        /// <summary>
        /// 🚨 2026-11-07：这个网盘的**默认**并发连接数（不看 per-pan 覆盖）。
        /// 全局 <c>Threads</c>（默认 256）→ 再叠加各服务端硬上限。
        /// 设置页全局下拉已删、弹窗不再有「跟随全局」项 —— 用这个直接把默认数字显示出来，
        /// 也用作「恢复默认」按钮的目标值。
        /// </summary>
        private int DefaultThreadsFor(PanKind kind)
        {
            int want = _settings.Threads > 0 ? _settings.Threads : 256;
            int cap = MaxParallelFor(kind);
            if (cap > 0 && want > cap) return cap;
            return want;
        }

        /// <summary>读取某网盘单独配置的 per-pan 覆盖值；0 = 无覆盖（回落默认基准）。仍被 <see cref="ThreadsFor"/> 使用。</summary>
        private int ThreadsOverrideFor(PanKind kind)
        {
            switch (kind)
            {
                case PanKind.Uc: return _settings.ThreadsUc;
                case PanKind.Aliyun: return _settings.ThreadsAliyun;
                case PanKind.Xunlei: return _settings.ThreadsXunlei;
                default: return _settings.ThreadsQuark;
            }
        }

        /// <summary>
        /// 每个网盘的「慢连接」绝对下限（字节/秒）。0 = 用引擎默认值（16 KB/s）。
        ///
        /// <para>
        /// 🚨🚨 2026-11-05 新增（P2）。原来四家共用一个 16 KB/s 的绝对下限，
        /// 但四家的限流方式完全不同，同一个数字必然有一边判错：
        /// </para>
        /// <list type="bullet">
        /// <item><b>夸克 = 16 KB/s</b>：按连接限速，单连接 40~80 KB/s。
        /// 低于 16 KB/s 就真的是半死连接，换一条立刻变快，判据不用改。</item>
        /// <item><b>UC = 8 KB/s</b>：单 IP 总带宽 7~8 MB/s。256 路摊下来每路约 30 KB/s，
        /// 但并发一旦超过 256 就掉到 13 KB/s —— 16 KB/s 会把**全部连接**判成慢，
        /// 触发重连风暴（实测 378 连接 4.85 MB/s，砍到 256 反而 7.07 MB/s）。</item>
        /// <item><b>阿里 = 8 KB/s</b>：单 IP 约 2.5~2.7 MB/s。用户日志里 151 次慢重连
        /// 的实测速度全部聚集在 10221~12269 B/s —— 正好卡在 16 KB/s 下方，
        /// 于是每条连接每 10 秒被判一次慢、重连、回来还是 10 KB/s，
        /// 带宽全烧在 TCP 握手和 HTTP 请求头上。降到 8 KB/s 后只抓真正的半死连接。</item>
        /// <item><b>迅雷 = 16 KB/s</b>：并发封 16，单连接实测 60~800 KB/s，阈值不用降。</item>
        /// </list>
        /// <para>
        /// ⚠️ 这只是**绝对下限**。真正生效的阈值 = <c>max(本值, 平均单连接速度 × 0.5)</c>，
        /// 相对阈值负责抓「掉队的个别连接」，绝对下限只负责抓「彻底半死的连接」。
        /// </para>
        /// </summary>
        private static long SlowFloorFor(PanKind kind)
        {
            switch (kind)
            {
                case PanKind.Uc:
                    return 8L * 1024;
                case PanKind.Aliyun:
                    return 8L * 1024;
                case PanKind.Xunlei:
                    return 16L * 1024;
                default:
                    return 16L * 1024;   // 夸克：按连接限速，保持原判据
            }
        }


        private void LogFromWorker(string msg)
        {
            // 🚨 引擎的「调试」级日志一律不进界面。
            // 实测教训：bezzad 的 ChunkDownloader 原本每个 4KB 缓冲打 3 条 debug，
            // 256 连接下每秒上万条 —— 它们全是 detail=true，会把 _logAll 撑爆，
            // TrimLogLines 一砍就把「解析成功/开始下载/下载完成」这些正常日志
            // 全部挤出窗口，于是「简化」视图变空白、只剩「详细」里满屏英文。
            // 逐包级别的追踪没有诊断价值（要看也是看分片级的），直接丢弃。
            // ⚠️ 2026-11-06：级别标签已中文化，判据必须跟着改成 [调试]；
            //    同时保留 [debug] 兜底，兼容旧版引擎 DLL 打出来的老格式日志。
            if (msg != null && (msg.StartsWith("[调试]", StringComparison.Ordinal)
                                || msg.StartsWith("[debug]", StringComparison.Ordinal)))
                return;

            // 🚨 2026-11-07：重复抑制（只作用于引擎专业级日志，核心结果永不拦）。
            //   同一模板 8 秒内只出 1 条，窗口到期补一条「（同类 N 条已合并）」。
            //   这样「取消一次下载灌 200 条分片错误」变成 1~2 行，核心信息仍在。
            if (!LogDupExempt(msg))
            {
                string key = LogDupKey(msg);
                bool emit = false;
                string summary = null;
                lock (_logDupGate)
                {
                    DateTime now = DateTime.UtcNow;
                    DateTime since;
                    if (!_logDupSince.TryGetValue(key, out since))
                    {
                        _logDupSince[key] = now;
                        _logDupCount[key] = 0;
                        emit = true;                    // 窗口内第一条，正常显示
                    }
                    else if ((now - since) >= LogDupWindow)
                    {
                        int n;
                        _logDupCount.TryGetValue(key, out n);
                        _logDupSince[key] = now;
                        _logDupCount[key] = 0;
                        emit = true;                    // 新一轮的第一条
                        if (n > 0)
                            summary = "（同类 " + n + " 条已合并）";
                    }
                    else
                    {
                        int n;
                        _logDupCount.TryGetValue(key, out n);
                        _logDupCount[key] = n + 1;       // 抑制，仅计数
                    }
                }
                if (summary != null)
                    EnqueueLog(summary, detail: true);
                if (!emit)
                    return;
            }

            // 下载内核的过程日志（换链、抢占、停滞…）整体算专业级 ——
            // 日常视图只看任务最终结果（失败/取消走 OnTaskUpdate，是日常级）。
            EnqueueLog(msg, detail: true);
        }


        /// <summary>
        /// 把攒下的日志一次性刷进 LogBox（由定时器在 UI 线程上调用）。
        /// <para>
        /// 🚨🚨 2026-11-07 四修 —— 「日志一多界面就卡」的**最后一层也是最大一层**根因：
        /// 本方法原来**无条件**把文本 <c>AppendText</c> 进 <see cref="LogBox"/>。
        /// 但用户在下载时根本不看日志页（日志在「任务」页最下方），
        /// 而 TextBox 带 <c>TextWrapping="Wrap"</c> 时每次 AppendText 都要把
        /// **已存在的全部文本重新测量、重新折行** —— 2000 行就是每 200ms 一次全量折行，
        /// 一天下来是 43 万次。界面自然会越用越卡，且和日志"多不多"无关，只和"多久"有关。
        /// </para>
        /// <para>
        /// 正解：**界面不在看日志时，一个字都不碰控件**。日志照常进队列、
        /// 照常进内存历史 <see cref="_logAll"/>（切「详细」要能补显），只是不刷 UI，
        /// 同时记一个 <see cref="_logViewDirty"/>。等用户真的切到日志页，
        /// 一次性 <see cref="RebuildLogBox"/> 把整屏拼好 —— 一次赋值、一次折行。
        /// </para>
        /// </summary>
        private void FlushLogs()
        {
            // 🚨 不能只判队列空：上一轮可能已经 dirty 但队列空了，
            //    此时用户刚切回日志页，不重建就永远是旧内容。
            if (_logQueue.IsEmpty && !_logViewDirty)
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

            // 先做纯内存裁剪（它可能报告「需要整体重建」）
            bool trimmed = TrimLogLines();

            if (!IsLogViewOnScreen())
            {
                // 没人在看 —— 不碰控件。这样就彻底消掉了「后台每 200ms 全量折行」。
                _logViewDirty = true;
                return;
            }

            if (trimmed || _logViewDirty)
            {
                // 整体重建一次：一次赋值、一次折行（比逐行 AppendText 便宜一个数量级）
                _logViewDirty = false;
                RebuildLogBox();
                return;
            }

            if (sb.Length > 0)
            {
                // 可见时的常规增量：只有新攒的这几百行，折行成本可忽略
                LogBox.AppendText(sb.ToString());
                LogBox.ScrollToEnd();
            }
        }


        /// <summary>
        /// 日志框此刻是否**真的在屏幕上被人看着**。
        /// <para>
        /// 三个条件缺一不可：日志框可见（页签没被切走时 TabItem 内容会被 Collapsed，
        /// 于是 <c>IsVisible</c> 自动为 false）、窗口不是最小化、窗口有句柄。
        /// </para>
        /// <para>
        /// ⚠️ 一律用 <c>IsVisible</c> 而不是自己比 <c>Tabs.SelectedIndex == 1</c>：
        /// 索引与侧栏一一对应这件事只在 <see cref="OnTabsSelectionChanged"/> 那里维护，
        /// 在这里再硬编码一个魔数，将来加页签必然错位。
        /// </para>
        /// </summary>
        private bool IsLogViewOnScreen()
        {
            try
            {
                if (LogBox == null || !LogBox.IsVisible)
                    return false;
                if (WindowState == WindowState.Minimized)
                    return false;
                return IsVisible;
            }
            catch
            {
                return false;
            }
        }


        /// <summary>
        /// 日志超过 <see cref="LogMaxLines"/> 时，从头部砍掉多余的整行。
        /// <para>
        /// 🚨🚨 2026-11-07 三修（用户第三次报「日志多了界面就卡」，前两次都改错了地方）：
        /// 真正的卡顿源**不是**切档位，而是这里 —— 原来 <c>int cut = _logAll.Count - LogMaxLines;</c>
        /// 每次只砍**超出的那几条**（通常 1~5），但紧跟着无条件 <see cref="RebuildLogBox"/>。
        /// 而本方法是 <see cref="FlushLogs"/> 每次都调的，<see cref="FlushLogs"/> 又是
        /// <b>每 200ms</b> 一次的定时器回调 —— 于是日志一旦累积到满额，
        /// 就变成「每秒 5 次 × 2000 行全量拼串 + 全量赋值 + 全量折行」，
        /// 而且会**永远持续下去**（因为 `_logAll` 再也退不回上限以下）。
        /// 实测这就是「用久了越来越卡、日志一多就卡死」的成因。
        /// </para>
        /// <para>
        /// 正解：**批量腾位置**。触发重建时一次砍到 <c>LogMaxLines - TrimSweep</c>，
        /// 于是下一次重建要等累计 <see cref="TrimSweep"/> 条新日志 ——
        /// 重建频率从「每 200ms」降到「每几百条日志」，日常使用基本无感。
        /// 代价是 LogBox 里最多会短暂多出 <see cref="TrimSweep"/> 行，不影响可读性。
        /// </para>
        /// <para>
        /// ⚠️ 不能靠「降低 <see cref="LogMaxLines"/>」来解决：那只是把每次重建的成本
        /// 降一点，频率问题原封不动，日志少了也照样每 200ms 重建。
        /// </para>
        /// <para>
        /// 🚨 本方法**只做纯内存裁剪，绝不碰控件**，返回「行列表是否变了」。
        /// 要不要重建 LogBox 由调用方决定 —— 因为调用方才知道日志框此刻在不在屏幕上
        /// （见 <c>FlushLogs</c>）：没人在看的时候重建纯属白烧 CPU，
        /// 这也是「日志多了界面卡」的另一大来源。
        /// </para>
        /// </summary>
        /// <returns>true = 确实砍掉了行（视图需要重建）；false = 没动。</returns>
        private bool TrimLogLines()
        {
            try
            {
                if (_logAll.Count <= LogMaxLines)
                    return false;
                // 一次腾出一整批，避免高频重建（见上面注释）
                int target = LogMaxLines - TrimSweep;
                if (target < TrimSweep) target = TrimSweep;
                int cut = _logAll.Count - target;
                if (cut <= 0)
                    return false;
                _logAll.RemoveRange(0, cut);
                return true;
            }
            catch
            {
                return false;
            }
        }


        /// <summary>
        /// 按当前模式（<see cref="_logVerbose"/>）重建 LogBox 文本。
        /// <para>
        /// 🚨🚨 2026-11-07 定稿（用户两轮都报「切换简要/详细卡顿」）：
        /// 11-04 曾改成「清空 + 分段 AppendText」想做流式显示，但 LogBox 是
        /// <c>TextWrapping="Wrap"</c> —— 每调一次 AppendText，WPF 都要把已存在的
        /// 全部文本重新折行/量算，5 段 = 5 次全量折行，2000 行时 O(n²) 反而更卡。
        /// </para>
        /// <para>
        /// 正解：内存里一次拼好整个字符串，**一次**赋给 <c>LogBox.Text</c>，
        /// 只触发一次布局/折行；内容没变就不重复赋值。这么做的代价是切换时
        /// 一次性完成（2000 行约几十毫秒），换来的是不再反复折行造成假死。
        /// 实时新增日志仍走 <see cref="FlushLogs"/> 的增量 AppendText（每次只有几行，
        /// 折行成本可忽略），两者互不冲突。
        /// </para>
        /// </summary>
        private void RebuildLogBox()
        {
            try
            {
                // 🚨🚨 2026-11-07 再修「切换简要/详细还是卡」：
                //   上一版（11-04）改成「清空 + 分段 AppendText」以为能流式显示，
                //   但 LogBox 是 TextWrapping="Wrap" —— **每调一次 AppendText，
                //   WPF 都要把已存在的全部文本重新折行/量算一遍**，5 段就是 5 次全量折行，
                //   2000 行时是 O(n²)，反而比一次性赋值更卡（用户实测仍卡）。
                //   正解：先在内存里拼好整个字符串，**一次**赋给 LogBox.Text，
                //   只触发一次布局/折行；再 ScrollToEnd。
                var sb = new StringBuilder();
                int start = 0;
                if (_logAll.Count > LogMaxLines)
                {
                    start = _logAll.Count - LogMaxLines;
                    sb.Append("…（更早的日志已省略，仅保留最近 ")
                      .Append(LogMaxLines).Append(" 行）").Append(Environment.NewLine);
                }
                for (int i = start; i < _logAll.Count; i++)
                {
                    var l = _logAll[i];
                    if (_logVerbose || !l.Detail)
                        sb.Append(l.Text).Append(Environment.NewLine);
                }
                string text = sb.ToString();
                // 反复快速切档位时内容没变就不重新赋值，避免无谓布局
                if (!string.Equals(LogBox.Text, text, StringComparison.Ordinal))
                {
                    LogBox.Text = text;
                    LogBox.ScrollToEnd();
                }
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
                // 🚨 2026-11-03：启动时先清理过期日志（用户要求「只保留一天」）。
                //   放在读取之前：这样即使要删的正好是即将恢复的文件，也不会先读进来再删。
                try { LogStore.PurgeOldLogs(); } catch { }

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
                    // 🚨 2026-11-07：全局并发下拉已删，这里改成报四家**实际生效**的连接数，
                    //    否则诊断包里那个数已经不是"当前生效值"，排查时会误导。
                    new KeyValuePair<string, string>("并发连接数（夸克/UC/阿里/迅雷）",
                        string.Format("{0} / {1} / {2} / {3}",
                            ThreadsFor(PanKind.Quark), ThreadsFor(PanKind.Uc),
                            ThreadsFor(PanKind.Aliyun), ThreadsFor(PanKind.Xunlei))),
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
        /// <remarks>
        /// 🚨 2026-11-04 播放器卡重做后，状态拆成三个控件：
        ///   · <c>PlayerStatusText</c> —— 一句结论（「VLC 已就绪」/「未检测到 VLC」）；
        ///   · <c>PlayerPathText</c>  —— 路径或补充说明（一行灰字，超长省略号）；
        ///   · <c>PlayerStatusDot</c> —— 状态圆点（绿=可用，橙=降级）。
        /// 旧版把「结论 + 路径 + 长篇解释」全塞进一个 TextBlock，字号一样、读起来是一坨。
        /// </remarks>
        private void ApplyPlayerStatus(PlayerLocator.Player p)
        {
            try
            {
                if (p == null)
                {
                    // 🚨 只提 VLC（2026-10-04 用户要求）：全项目只对 VLC 做过参数适配，
                    //    其它播放器（mpv / PotPlayer）可能行为不一致 → 干脆不提，避免给出错误预期。
                    PlayerStatusText.Text = "未检测到 VLC";
                    if (PlayerPathText != null)
                    {
                        PlayerPathText.Text = "「打开」将回退到系统默认程序，边下边播未必可用"
                            + "（系统自带播放器通常打不开还没下完的文件）。";
                        PlayerPathText.ToolTip = PlayerPathText.Text;
                    }
                    if (PlayerStatusDot != null)
                    {
                        PlayerStatusDot.SetResourceReference(
                            System.Windows.Shapes.Shape.FillProperty, "WarningBrush");
                    }
                    GetVlcBtn.Visibility = System.Windows.Visibility.Visible;
                }
                else
                {
                    PlayerStatusText.Text = "VLC 已就绪";
                    if (PlayerPathText != null)
                    {
                        PlayerPathText.Text = p.ExePath ?? "";
                        PlayerPathText.ToolTip = p.ExePath;
                    }
                    if (PlayerStatusDot != null)
                    {
                        PlayerStatusDot.SetResourceReference(
                            System.Windows.Shapes.Shape.FillProperty, "SuccessBrush");
                    }
                    GetVlcBtn.Visibility = System.Windows.Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                if (PlayerStatusText != null) PlayerStatusText.Text = "检测播放器失败";
                if (PlayerPathText != null)
                {
                    PlayerPathText.Text = ex.Message;
                    PlayerPathText.ToolTip = ex.Message;
                }
                if (PlayerStatusDot != null)
                {
                    PlayerStatusDot.SetResourceReference(
                        System.Windows.Shapes.Shape.FillProperty, "DangerBrush");
                }
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
