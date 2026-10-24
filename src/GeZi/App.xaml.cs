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
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows;
using GeZi.Core.Support;

namespace GeZi
{
    public partial class App : Application
    {
        /// <summary>
        /// 单实例互斥体。命名带 Global\ 前缀会跨用户会话（服务场景），
        /// 这里用默认的 Local\ 即可 —— 同一登录会话里只允许开一个。
        /// </summary>
        private Mutex _instanceMutex;

        /// <summary>
        /// 跨进程「请把主窗拉起来」消息号。用 RegisterWindowMessage 注册，
        /// 保证同一台机器上所有 GeZi 实例拿到同一个值（≥0xC000 的区间，不会与系统消息冲突）。
        /// </summary>
        internal static readonly int WmActivateMain =
            NativeMethods.RegisterWindowMessageW("GeZi_Activate_MainWindow_v1");

        /// <summary>主窗口是否已显示过（供第二实例激活逻辑判断）。</summary>
        internal static bool MainShown;

        /// <summary>主窗口实例（托盘/激活消息要用）。</summary>
        internal static MainWindow MainWin;

        /// <summary>
        /// 启动期崩溃日志。写在 exe 同目录的 startup-error.log。
        /// 为什么需要：XAML 解析错误发生在 MainWindow 构造里，
        /// 此时 DispatcherUnhandledException 的弹窗可能还没来得及显示，
        /// 用户只看到"闪一下就没了"，排查无从下手。落盘一份最直接。
        /// </summary>
        private static void LogFatal(string where, Exception ex)
        {
            try
            {
                string path = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "startup-error.log");
                System.IO.File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + where + "]\r\n"
                    + ex + "\r\n\r\n");
            }
            catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 启动期总兜底：任何构造/初始化异常都落盘，避免"闪退无痕迹"。
            // 同时保留原来的弹窗提示（用户能立刻知道出事了，而不是静默失败）。
            DispatcherUnhandledException += (s, args) =>
            {
                LogFatal("DispatcherUnhandledException", args.Exception);
                // 改用自绘对话框保持外观统一。此处可能在主窗口建立前触发，
                // owner 传当前活动窗口（可能为 null → 自动居中屏幕）。
                try
                {
                    AppDialog.Show(Current != null ? Current.MainWindow : null,
                        "程序发生未处理错误：\n\n" + args.Exception.Message,
                        "鸽子下载", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch
                {
                    // 对话框自身失败时退回系统弹窗，保证用户至少能看到错误
                    MessageBox.Show("程序发生未处理错误：\n\n" + args.Exception.Message,
                        "鸽子下载", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                LogFatal("AppDomain.UnhandledException", args.ExceptionObject as Exception
                    ?? new Exception(args.ExceptionObject?.ToString() ?? "unknown"));
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                LogFatal("UnobservedTaskException", args.Exception);
            };

            // ---------------- 单实例锁 ----------------
            // 移植自 Python Flet 版的 _single_instance_guard / _notify_existing_instance。
            //
            // 为什么需要：此前 C# 版重复启动会开出**两个窗口**，
            // 两个实例各自持有 scheduler 与下载任务，用户分不清哪个在跑，
            // 更糟的是两边的"同路径互斥"是各自进程内的字典，**彼此看不见** ——
            // 同时下同一个文件会互相踩写（数据损坏）。
            //
            // 做法：用命名 Mutex 判重；若已有实例，**广播一条自定义消息**请它自己
            // 把主窗拉起来，然后本进程立刻退出。
            bool createdNew;
            try
            {
                _instanceMutex = new Mutex(true, "GeZi_SingleInstance_Mutex_v1", out createdNew);
            }
            catch
            {
                createdNew = true;   // 拿不到互斥体也不该拦住用户启动
            }

            if (!createdNew)
            {
                // ⚠️ 顺序很重要，且这里必须**直接 Exit**，不能只 Shutdown()：
                //    Shutdown() 是排队执行的，会等启动序列（资源字典 + MainWindow 构造）
                //    跑完才生效 —— 实测第二实例因此白跑 2.2 秒、白占几十 MB。
                //    现在已经没有 StartupUri，这里在**任何资源加载之前**就退出。
                NotifyExistingInstance();
                Environment.Exit(0);
                return;
            }

            // ---------------- 外观主题 ----------------
            // ⚠️ 必须在构造 MainWindow **之前**应用：否则窗口会先按亮色渲染一帧，
            //    深色模式下用户能明显看到一次"白闪"（尤其是冷启动较慢时）。
            //    此时 Application.Resources 已由 App.xaml 加载完毕（Colors.Light 已就位），
            //    ThemeManager 只做"换成 Dark"或"保持 Light"。
            try
            {
                var bootSettings = SettingsStore.Load();
                ThemeManager.Initialize(bootSettings.Theme);
            }
            catch (Exception ex)
            {
                LogFatal("ThemeInit", ex);   // 主题失败不该拦住启动，退回默认亮色
            }

            // ---------------- 主窗口 ----------------
            // 手动创建（App.xaml 里已去掉 StartupUri，见那里的注释）。
            MainWin = new MainWindow();
            // ⚠️ 写 this.MainWindow（属性）而不是 MainWindow：后者与类型名同名，
            //    不限定会有歧义（CS0119/CS0122 那类"名字既像类型又像成员"的老问题）。
            this.MainWindow = MainWin;
            MainWin.Show();

            MainShown = true;
            NetworkConfig.Apply();
        }

        /// <summary>
        /// 请已运行的实例把主窗拉起来。
        ///
        /// 只广播一条自定义消息（<see cref="WmActivateMain"/>），由旧进程的
        /// 窗口钩子收到后自己 <c>Show()+Activate()</c>。
        ///
        /// ⚠️ 为什么不在这里直接 EnumWindows + SetForegroundWindow（旧实现）：
        ///    Windows 有**前台锁定** —— 后台进程调 SetForegroundWindow 往往只让任务栏
        ///    闪一下，窗口不会真的前置。让旧进程自己激活才算"响应用户操作"。
        /// ⚠️ 先 AllowSetForegroundWindow(ASFW_ANY) 把"允许抢前台"的额度交给对方，
        ///    否则对方激活时仍可能被拒。
        /// </summary>
        private static void NotifyExistingInstance()
        {
            try { NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY); } catch { }
            try
            {
                NativeMethods.PostMessage(new IntPtr(NativeMethods.HWND_BROADCAST),
                    WmActivateMain, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
            // 兜底：万一旧实例的窗口钩子还没装上（启动竞态）消息会丢，
            // 这里再用"枚举窗口 + 直接激活"试一次。抢不到前台也无害。
            ActivateExistingInstance();
        }

        /// <summary>
        /// **兜底路径**：找到已运行的 GeZi 主窗口并激活前置。
        /// 首选路径是 <see cref="NotifyExistingInstance"/> 里的广播消息（由旧进程自己激活，
        /// 能绕过前台锁定）；这里只在消息可能丢失（旧实例还没装钩子）时补一刀。
        ///
        /// 只认「进程名是 GeZi 且窗口标题含主标题」的窗口，避免误激活气泡或对话框。
        /// 找不到就静默。
        ///
        /// ⚠️ **必须同时匹配「隐藏」的窗口**：
        /// 本程序的关闭按钮在"最小化到托盘"模式下是 `Hide()` 而不是退出 ——
        /// 进程仍活着、仍持有单实例互斥体，但主窗 `IsWindowVisible == false`。
        /// 早期实现只枚举可见窗口 → 隐藏的主窗被跳过 → 新启动的进程判定
        /// "找不到已有实例" → 什么都不显示就退出。
        /// 用户看到的现象就是：「托盘图标在，但主窗口不弹出，得手动点托盘」。
        /// 所以这里**不看可见性**，只看窗口标题 + 属于 GeZi 进程。
        /// </summary>
        private static void ActivateExistingInstance()
        {
            try
            {
                IntPtr found = IntPtr.Zero;
                NativeMethods.EnumWindows((hWnd, _) =>
                {
                    // 只考虑顶层窗口（EnumWindows 已是顶层，这里再排掉没有标题的）
                    var sb = new StringBuilder(256);
                    NativeMethods.GetWindowTextW(hWnd, sb, sb.Capacity);
                    string title = sb.ToString();
                    if (title.IndexOf("鸽子下载", StringComparison.Ordinal) < 0)
                        return true;   // 继续枚举

                    // 确认属于同名进程（防止匹配到别的程序里含"鸽子下载"字样的窗口）
                    uint pid;
                    NativeMethods.GetWindowThreadProcessId(hWnd, out pid);
                    try
                    {
                        using (var p = Process.GetProcessById((int)pid))
                        {
                            if (p.ProcessName.IndexOf("GeZi", StringComparison.OrdinalIgnoreCase) < 0)
                                return true;
                        }
                    }
                    catch { return true; }

                    found = hWnd;
                    return false;   // 找到即停
                }, IntPtr.Zero);

                if (found != IntPtr.Zero)
                {
                    // 恢复 + 前置。SW_SHOW 能同时处理"隐藏到托盘"与"最小化"两种情况。
                    NativeMethods.ShowWindow(found, NativeMethods.SW_SHOWNORMAL);
                    NativeMethods.ShowWindow(found, NativeMethods.SW_RESTORE);
                    NativeMethods.SetForegroundWindow(found);
                }
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // ⚠️ 必须解绑 SystemEvents.UserPreferenceChanged：它内部会创建一个隐藏窗口
            //    参与消息循环，不解绑会拖住进程不退出（SystemEvents 的经典坑）。
            try { ThemeManager.Shutdown(); } catch { }
            try { _instanceMutex?.ReleaseMutex(); } catch { }
            try { _instanceMutex?.Dispose(); } catch { }
            base.OnExit(e);
        }
    }
}
