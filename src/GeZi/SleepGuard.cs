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
using System.Windows;

namespace GeZi
{
    /// <summary>
    /// 下载期间的「保活」：阻止系统进入睡眠，让挂机下载不会因为电脑睡着而断掉。
    /// 用户反馈「中午开着任务后关显示器离开了，下午再看任务全部停了」——
    /// 系统睡着会掐断所有连接，任务自然全灭。
    /// 用户明确要求"不要单独按钮"。
    /// 保活**自动跟随任务状态**：有任务在活跃下载就保活，没有就释放 —— 用户无需关心。
    /// 🚨🚨
    /// 本机实测是 **Modern Standby（S0 低电量待机）**，不是传统 S3：
    ///   `powercfg -a` → "待机 (S0 低电量待机) 连接的网络"；"待机 (S3) 当支持 S0 时被禁用"
    /// · `SetThreadExecutionState`（老 API，S3 时代）—— **在 S0ix 上拦不住睡眠**。
    ///   实测：挂下载时仍然睡过去了（而且它返回成功、日志也打了，**假象**）。
    /// · `PowerSetRequest`（新 API）才是 S0ix 的正解：
    ///     - `PowerRequestSystemRequired`    阻止系统自动睡眠
    ///     - `PowerRequestExecutionRequired` 阻止 **DAM（桌面活动调节器）挂起本进程**
    ///       ← S0ix 下网络虽保持连接，但桌面应用会被冻结，所以这条是关键。
    /// ⇒ 两者都请求，S3 机器靠前者、S0ix 机器靠后者，覆盖面最广。
    /// 🚨 `SetThreadExecutionState` 是**线程级**的：状态记在调用线程上，
    /// 在 A 线程置位、B 线程清除是**清不掉的** → 系统永不睡眠（极难排查的僵尸保活）。
    /// ⇒ 统一走 UI 线程；`Set` 内部做了 `CheckAccess` 守卫，非 UI 线程会自动 `BeginInvoke`。
    /// （`PowerSetRequest` 是**句柄级**的，没有线程约束，但为简单起见一起走 UI 线程。）
    /// ✅ 进程退出/崩溃时线程状态自动释放、句柄自动关闭 ⇒ 不会留下僵尸保活。
    /// ✅ 不需要管理员权限（`app.manifest` 是 asInvoker）。
    /// ⚠️ 只防**空闲自动睡眠**；**不阻止**用户手动睡眠/关机，也管不了笔记本合盖。
    /// </summary>
    internal static class SleepGuard
    {
        /// <summary>当前是否已置位（避免重复调用 API）。</summary>
        private static bool _blocking;

        /// <summary>置位失败过一次就不再重试，避免刷日志。</summary>
        private static bool _failed;

        /// <summary>电源请求句柄（进程生命周期内复用）。</summary>
        private static IntPtr _powerHandle = IntPtr.Zero;

        /// <summary>是否已成功创建电源请求句柄。</summary>
        private static bool _powerReady;

        /// <summary>
        /// 状态变化的日志回调（由 MainWindow 挂到界面的 Log）。
        /// 用户能从日志里看到"为什么电脑没睡" —— 不做开关，但保留知情权。
        /// </summary>
        internal static Action<string> Log;

        private static void Say(string msg)
        {
            try
            {
                var log = Log;
                if (log != null) log(msg);
            }
            catch { }
        }

        /// <summary>
        /// 懒创建电源请求句柄。失败不致命 —— S3 机器上老 API 就够用了。
        /// </summary>
        private static void EnsurePowerHandle()
        {
            if (_powerReady)
                return;

            try
            {
                var ctx = new NativeMethods.POWER_REQUEST_CONTEXT
                {
                    Version = NativeMethods.POWER_REQUEST_CONTEXT_VERSION,
                    Flags = NativeMethods.POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                    SimpleReasonString = "GeZi: 正在下载，需要保持系统运行",
                };
                var h = NativeMethods.PowerCreateRequest(ref ctx);
                if (h != IntPtr.Zero && h != new IntPtr(-1))
                {
                    _powerHandle = h;
                    _powerReady = true;
                }
            }
            catch { /* 拿不到句柄就退回老 API */ }
        }

        /// <summary>
        /// 设置保活状态。<paramref name="block"/> 为 true 时阻止系统睡眠，false 时释放。
        /// 重复传入相同值不会重复调用 API。
        /// ⚠️ 必须在 UI 线程调用；内部有守卫会自动切回 UI 线程。
        /// </summary>
        internal static void Set(bool block)
        {
            if (_failed)
                return;

            // 守卫：确保始终在同一个线程（UI 线程）上调用 —— 见类注释里"线程级"的说明。
            try
            {
                var app = Application.Current;
                var disp = app == null ? null : app.Dispatcher;
                if (disp != null && !disp.CheckAccess())
                {
                    disp.BeginInvoke(new Action(() => Set(block)));
                    return;
                }
            }
            catch { /* 取 Dispatcher 失败就按当前线程处理 */ }

            if (block == _blocking)
                return;

            bool legacyOk = false;
            bool modernOk = false;

            try
            {
                // ---- 1) 老 API：S3 机器靠它 ----
                uint flags = block
                    ? (NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED)
                    : NativeMethods.ES_CONTINUOUS;
                NativeMethods.SetThreadExecutionState(flags);
                legacyOk = true;
            }
            catch { }

            // ---- 2) 新 API：S0ix（Modern Standby）靠它 ----
            EnsurePowerHandle();
            if (_powerReady)
            {
                try
                {
                    if (block)
                    {
                        bool a = NativeMethods.PowerSetRequest(
                            _powerHandle, NativeMethods.PowerRequestSystemRequired);
                        bool b = NativeMethods.PowerSetRequest(
                            _powerHandle, NativeMethods.PowerRequestExecutionRequired);
                        modernOk = a || b;
                    }
                    else
                    {
                        NativeMethods.PowerClearRequest(
                            _powerHandle, NativeMethods.PowerRequestExecutionRequired);
                        NativeMethods.PowerClearRequest(
                            _powerHandle, NativeMethods.PowerRequestSystemRequired);
                        modernOk = true;
                    }
                }
                catch { }
            }

            if (!legacyOk && !modernOk)
            {
                // 两条路都不通：记一次就放弃，之后不再尝试。
                _failed = true;
                _blocking = false;
                Say("阻止系统休眠失败（不影响下载）。");
                return;
            }

            _blocking = block;

            if (block)
                Say("有任务在下载，已阻止系统休眠（任务跑完会自动恢复）。");
            else
                Say("任务已全部结束，已恢复系统自动休眠。");
        }

        /// <summary>释放保活并关闭句柄（退出时调用；不调也会随进程退出自动释放）。</summary>
        internal static void Release()
        {
            Set(false);

            try
            {
                if (_powerHandle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(_powerHandle);
                    _powerHandle = IntPtr.Zero;
                    _powerReady = false;
                }
            }
            catch { }
        }
    }
}
