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
// 职责：剪贴板监听与链接解析

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

        // ---------------- 剪贴板自动解析（2026-10-08） ----------------
        // 需求：在**任何程序**里复制到网盘分享链接（夸克/UC/阿里/迅雷），本程序直接解析好。
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
                Log(on ? "已开启剪贴板监听：复制网盘分享链接会自动解析。"
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

                // 🚨🚨 2026-11-01（用户要求「自动解析链接只会解析夸克这一个，
                //     我要求它会解析所有网盘的链接，并且都会显示他们网盘的文件目录」）：
                //     原来这里调的是 FindQuarkShareUrl —— 只看 quark.cn 域名，
                //     UC / 阿里 / 迅雷的链接一律被当成"不认识"直接 return，什么都不做。
                //     改用 PanUrlParser.Parse（它已经支持四家：quark.cn / uc.cn /
                //     aliyundrive.com·alipan.com / xunlei.com，按 host 精确匹配），
                //     识别出是哪家后交给 FillAndParse 走完整解析链路（含目录列表）。
                var share = PanUrlParser.Parse(text);
                if (share == null || string.IsNullOrEmpty(share.ShareId))
                {
                    // 不是任何已知网盘的分享链接（含"夸克口令"这类纯文本）。
                    // 这里**刻意什么都不做、也不弹窗**：剪贴板通知不是用户在本程序里
                    // 发起的动作，为一句"这不能解析"打断他很粗暴 —— 何况口令在聊天记录里
                    // 到处都是，复制顺手就会触发。
                    return;
                }

                // 去重键要带上网盘类型：不同网盘的分享 ID 可能撞车。
                string dedupKey = share.Kind + ":" + share.ShareId;
                if (string.Equals(dedupKey, _clipboardLastPwdId, StringComparison.Ordinal))
                {
                    // 同一条分享的重复通知 → 不再重复发请求。见 _clipboardLastPwdId 的注释。
                    // （要重新解析，直接点「解析」按钮即可 —— 手动路径不受这个标记影响。）
                    return;
                }
                _clipboardLastPwdId = dedupKey;

                // 提取码已由 PanUrlParser 从**整段文本**里解析好
                // （链接自带 pwd= 的能认，文案里的「提取码：abcd」也能认）。
                FillAndParse(share.RawUrl, share.Passcode);
            }
            catch (Exception ex)
            {
                try { Log("剪贴板自动解析失败: " + ex.Message); } catch { }
            }
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

            LogDetail("剪贴板里发现网盘分享链接，自动解析…");
            OnParseClick(null, null);
        }
    }
}
