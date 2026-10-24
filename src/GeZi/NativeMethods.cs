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
using System.Runtime.InteropServices;

namespace GeZi
{
    /// <summary>
    /// 平台原生调用集中处（net48、零 NuGet，只能走 P/Invoke）。
    /// 注意：本工作区内的 exe 会被以**低完整性**启动（沙箱给目录打了
    /// Low 标签），低完整性进程不能与 shell 交互 —— 所以涉及外部程序启动/激活的
    /// 调用，**必须在工作区外运行才能验证成功**。
    /// </summary>
    internal static class NativeMethods
    {
        // ---------------- 文件删除（占用时登记重启后删除） ----------------

        internal const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);

        // ---------------- 窗口激活（单实例：把已有实例的主窗拉起来） ----------------

        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        internal const int SW_SHOWNORMAL = 1;
        internal const int SW_RESTORE = 9;

        // ---------------- 单实例：跨进程「请把主窗拉起来」通知 ----------------
        // 为什么不用「新进程自己 SetForegroundWindow」：Windows 有**前台锁定**，
        // 后台进程调 SetForegroundWindow 常常只让任务栏闪一下，窗口并不会真的前置
        // （尤其是旧窗口处于隐藏/最小化时）。可靠做法是让**旧进程自己**去激活 ——
        // 它收到消息后调 Show()+Activate()，属于"响应用户操作"，允许抢前台。
        // 用 RegisterWindowMessage 注册一条全局唯一消息 + HWND_BROADCAST 广播：
        // 不需要先找到窗口句柄，也不需要跨进程管道。
        // ⚠️ 广播消息对**隐藏**的顶层窗口同样会送达（IsWindowVisible 不影响投递），
        //    所以"最小化到托盘"状态下再点 exe 也能被唤醒。

        internal const int HWND_BROADCAST = 0xFFFF;
        internal const int ASFW_ANY = -1;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int RegisterWindowMessageW(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool AllowSetForegroundWindow(int dwProcessId);

        // ---------------- 原生标题栏深浅（深色模式） ----------------

        /// <summary>
        /// 让窗口的非客户区（标题栏）按新设置重画。
        /// 改完 `DWMWA_USE_IMMERSIVE_DARK_MODE` 后用 SWP_FRAMECHANGED 触发一次重算，
        /// 否则个别 Windows 版本上标题栏要等窗口被激活才刷新。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_FRAMECHANGED = 0x0020;

        // ---------------- 剪贴板变更通知 ----------------

        /// <summary>剪贴板内容已变化（由系统投递给已注册的窗口）。</summary>
        internal const int WM_CLIPBOARDUPDATE = 0x031D;

        /// <summary>
        /// 注册"剪贴板有变化请通知我"。
        ///   · 轮询（每几百毫秒读一次剪贴板）会无谓唤醒 UI 线程，还必然带上一个间隔的延迟；
        ///   · `SetClipboardViewer` 是 Win2000 时代的老机制，要插进"剪贴板查看器链"并转发
        ///     `WM_CHANGECBCHAIN` —— 链上任何一个程序崩掉，它后面的全都收不到通知。
        /// `AddClipboardFormatListener`（Vista+）只登记一条 <see cref="WM_CLIPBOARDUPDATE"/>，
        /// 由系统直接投递，不参与任何链式转发。
        /// ⚠️ 必须**在有窗口句柄之后**调用，且只在顶层窗口上有效。
        /// ⚠️ 窗口隐藏到托盘时**不要**摘掉注册 —— 托盘状态下仍要能收到通知，
        ///    这正是"窗口不可见时静默解析 + 气泡提示"这条需求的前提。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool AddClipboardFormatListener(IntPtr hwnd);

        /// <summary>注销剪贴板变更通知。进程退出时系统也会自动清理，显式摘掉更稳妥。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        // ---------------- 保活：阻止系统空闲睡眠 ----------------

        /// <summary>
        /// 告知系统"本程序正在使用中"，从而阻止**空闲自动睡眠**（以及可选地阻止息屏）。
        /// 用户挂着下载去睡觉，若系统按电源计划进入睡眠，
        /// 网络会断、所有连接作废 —— 这正是用户反馈"中午开着下载，下午回来全停了"的成因之一。
        /// 🚨 **本 API 是的**：状态记录在**调用它的那个线程**上，
        /// 系统只要有一个线程声明了 `ES_SYSTEM_REQUIRED` 就不会睡。
        /// 因此在 A 线程置位、在 B 线程清除是**清不掉的**（A 的状态还在）→ 系统永不睡眠。
        /// ⇒ **必须始终由同一个线程调用**（本项目统一用 UI 线程，见 SleepGuard）。
        /// ✅ 线程退出时状态自动释放 ⇒ 程序崩溃/退出**不会**留下"永不睡眠"的僵尸状态。
        /// ✅ 不需要管理员权限。
        /// ⚠️ 只防**空闲自动睡眠**；**不阻止**用户手动睡眠/关机，也管不了笔记本合盖。
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint SetThreadExecutionState(uint esFlags);

        /// <summary>与上一次的状态合并（不清除之前设置的其他标志）。</summary>
        internal const uint ES_CONTINUOUS = 0x80000000;

        /// <summary>要求系统保持可用（阻止空闲睡眠）。</summary>
        internal const uint ES_SYSTEM_REQUIRED = 0x00000001;

        // ---------------- 保活（Modern Standby 路径）----------------
        // 🚨
        // 实测发现本机是 **Modern Standby（S0 低电量待机）**，不是传统 S3：
        //     `powercfg -a` → "待机 (S0 低电量待机) 连接的网络"；"待机 (S3) 当支持 S0 时被禁用"
        // `SetThreadExecutionState` 是 S3 时代的老 API，在 S0ix 上**拦不住睡眠**
        // —— 实测挂了下载仍然睡过去了（这正是用户最初"中午开着下载下午全停了"的成因）。
        // ✅ 正确做法是 `PowerSetRequest`：
        //   · `PowerRequestSystemRequired`    阻止系统自动进入睡眠
        //   · `PowerRequestExecutionRequired` 阻止 **DAM（桌面活动调节器）挂起本进程**
        //     ← 这条是 S0ix 特有的关键：S0ix 下网络虽在，但桌面应用会被冻结。
        // 两个一起请求才稳。

        /// <summary>电源请求上下文（对应 POWER_REQUEST_CONTEXT）。</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct POWER_REQUEST_CONTEXT
        {
            public uint Version;
            public uint Flags;
            [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
        }

        /// <summary>POWER_REQUEST_CONTEXT_VERSION（当前唯一取值）。</summary>
        internal const uint POWER_REQUEST_CONTEXT_VERSION = 0;

        /// <summary>POWER_REQUEST_CONTEXT_SIMPLE_STRING —— 用简单字符串作为申请理由。</summary>
        internal const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1;

        /// <summary>阻止系统自动睡眠。</summary>
        internal const int PowerRequestSystemRequired = 1;
        /// <summary>阻止 DAM 在 Modern Standby 期间挂起本进程。</summary>
        internal const int PowerRequestExecutionRequired = 3;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr PowerCreateRequest(ref POWER_REQUEST_CONTEXT Context);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool PowerSetRequest(IntPtr PowerRequest, int RequestType);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool PowerClearRequest(IntPtr PowerRequest, int RequestType);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr hObject);
    }
}
