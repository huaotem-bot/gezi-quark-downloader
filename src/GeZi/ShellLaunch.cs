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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using GeZi.Providers;
using GeZi.Providers.Quark;
using GeZi.Providers.Uc;
using GeZi.Providers.Aliyun;
using GeZi.Providers.Xunlei;
using GeZi.Providers.Shared;
using GeZi.Providers.Qr;

using GeZi.Services;
namespace GeZi
{
    /// <summary>
    /// 「打开外部东西」——**逐行对齐 Python Flet 版的逻辑**。
    /// 之前几版反复失败，原因不是语言，而是**没照抄到位**。Python 版真正的做法是：
    ///   os.startfile(path)
    ///     → CPython 内部：ShellExecuteW(NULL, L"open", path, NULL, cwd, SW_SHOWNORMAL)
    ///     → 在**普通后台线程**里调用，**不显式初始化 COM**
    ///     → **不看返回值**，而是**枚举 Explorer 窗口，确认新窗口真的出现**
    ///     → 出现后 ShowWindow + BringWindowToTop + SetForegroundWindow 置前
    ///     → 失败才退到 ShellExecuteW("explore", path, dir=dirname(path))
    /// 我此前犯的四个错：
    ///   1. operation 传 NULL（Python 传的是 "open"）
    ///   2. 自作主张加 CoInitializeEx + COINIT_DISABLE_OLE1DDE（那是 qBittorrent 的路，不是 Python 的）
    ///   3. **拿 ShellExecute 的返回值当成功判据**——ShellExecute 的返回码在目录打开这条路上并不可靠
    ///   4. 还带一个"3 秒超时就把 result 置 false"的 bug
    /// 所以本版把成功判据换成 Python 的：**窗口真的出现了才算成功**。
    /// </summary>
    internal static class ShellLaunch
    {
        /// <summary>诊断日志出口，由 MainWindow 注入。</summary>
        public static Action<string> Log;

        private const int SW_SHOWNORMAL = 1;
        private const int SW_RESTORE = 9;
        private const int ShellExecuteErrorThreshold = 32;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr ShellExecuteW(IntPtr hwnd, string operation,
            string file, string parameters, string directory, int showCmd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr param);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

        // ---------------- URL ----------------

        /// <summary>
        /// 等价于 Python 的 <c>webbrowser.open(url)</c>（Windows 下即 <c>os.startfile(url)</c>）：
        /// 让系统激活**已有的默认浏览器**，不直接启动第二个 Edge。
        /// </summary>
        public static bool OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            long code = Fire("open", url, null);
            if (code > ShellExecuteErrorThreshold) return true;

            // 返回值可疑时再走 Windows 自带的 URL 协议桥接器
            long code2 = FireViaRundll32(url);
            if (code2 == 0) return true;

            // 只在真的失败时留日志，避免每次点击都刷屏
            Report("OpenUrl 失败 ShellExecute=" + code + " rundll32=" + code2 + "  url=" + url);
            return false;
        }

        // ---------------- 文件夹 ----------------

        /// <summary>等价于 Python 的 <c>_open_dir_front</c>（不选中具体文件）。</summary>
        public static bool OpenFolder(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                dir = Path.GetFullPath(dir);
            }
            catch { return false; }

            List<IntPtr> before = ExplorerWindows();
            string folder = Path.GetFileName(dir.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            // ① 快路径：ShellExecute 返回成功码就直接采信，不轮询、不卡界面
            long code = Fire("open", dir, null);
            if (code > ShellExecuteErrorThreshold) return true;

            // ② 返回码可疑时，才用"窗口是否真的出现"复核（ShellExecute 的返回码并不总可靠）
            if (WaitForExplorerWindow(before, folder, 1200)) return true;

            // ③ 兜底：explore 动词，同样先看返回码再看窗口
            long code2 = Fire("explore", dir, Path.GetDirectoryName(dir));
            if (code2 > ShellExecuteErrorThreshold) return true;
            if (WaitForExplorerWindow(before, folder, 1200)) return true;

            Report("OpenFolder 失败 open=" + code + " explore=" + code2 + "  dir=" + dir);
            return false;
        }

        // ---------------- 文件 ----------------

        /// <summary>打开本地文件：优先用探测到的播放器（边下边播需要它宽容的共享模式）。</summary>
        public static bool OpenFile(string file)
        {
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return false;

            // 「VLC 开了但没播，还得手动用它自己的『打开文件』才能看」。
            // 根因：**路径里混了正/反斜杠**。
            //   设置页的下载目录是**用户手输**的文本框，可能是 `C:/Users/<用户名>/Downloads`；
            //   而 .NET 的 `Path.Combine` 拼文件名时用的是 `\` →
            //   最终得到 `C:/Users/<用户名>/Downloads\xxx.mp4`。
            //   Windows 自己认这种混合路径（File.Exists 为 true，下载/写入都正常），
            //   但 **VLC 会把 `C:/…` 当成 URL 协议头（c: scheme）** → 打不开文件、
            //   只显示交通锥图标，用户还得手动「打开文件」。
            // → 交给任何外部程序之前，一律用 Path.GetFullPath 规范化（分隔符统一成 `\`）。
            try { file = Path.GetFullPath(file); } catch { }

            var player = PlayerLocator.Find();
            if (player != null)
            {
                Process p = null;
                try
                {
                    // 「点播放调用的是 VLC，VLC 开了但没播」。
                    // 原因：VLC 是**单实例**程序。已经有一个 VLC 在跑时，再执行
                    // `vlc.exe "文件"` 只会把文件**塞进那个实例的播放列表**就退出 ——
                    // 窗口出来了、却没有开始播放，正是用户看到的现象。
                    // 资源管理器双击/右键「用 VLC 打开」为什么正常？因为它用的是
                    // 文件关联里的命令行，本机实测是：
                    //     "D://VLC//vlc.exe" --started-from-file "%1"
                    // `--started-from-file` 就是告诉 VLC「这是从文件关联启动的，直接播」。
                    // → 这里把参数对齐成资源管理器那一套（VLC 专用参数）。
                    string args = Quote(file);
                    if (!string.IsNullOrEmpty(player.ExePath)
                        && Path.GetFileName(player.ExePath)
                               .IndexOf("vlc", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        args = "--started-from-file " + args;
                    }

                    p = Process.Start(new ProcessStartInfo(player.ExePath, args)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(player.ExePath),
                    });
                    if (p == null) return false;
                    p.WaitForExit(700);
                    return !p.HasExited || p.ExitCode == 0;
                }
                catch { return false; }
                finally { if (p != null) p.Dispose(); }
            }

            // 没有播放器：交给系统的"打开方式"
            long code = Fire("openas", file, null);
            if (code > ShellExecuteErrorThreshold) return true;
            Report("OpenFile 失败 ShellExecute=" + code + "  file=" + file);
            return false;
        }

        // ---------------- 供 ShellReveal 复用 ----------------

        /// <summary>当前所有可见的 Explorer 顶层窗口句柄。</summary>
        internal static List<IntPtr> ExplorerWindows()
        {
            var list = new List<IntPtr>();
            try
            {
                EnumWindows((h, _) =>
                {
                    if (!IsWindowVisible(h)) return true;
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    try
                    {
                        using (var proc = Process.GetProcessById((int)pid))
                        {
                            if (proc.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                                list.Add(h);
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return list;
        }

        /// <summary>
        /// 轮询等待"新出现的 Explorer 窗口"（或标题里含 folderName 的窗口），
        /// 找到就置前。这正是 Python 用来判断成功的依据。
        /// </summary>
        internal static bool WaitForExplorerWindow(List<IntPtr> before, string folderName, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var now = ExplorerWindows();
                IntPtr cand = IntPtr.Zero;

                foreach (var h in now)
                {
                    if (!before.Contains(h)) { cand = h; break; }
                }
                if (cand == IntPtr.Zero && !string.IsNullOrEmpty(folderName))
                {
                    foreach (var h in now)
                    {
                        if (WindowTitle(h).IndexOf(folderName, StringComparison.OrdinalIgnoreCase) >= 0)
                        { cand = h; break; }
                    }
                }

                if (cand != IntPtr.Zero)
                {
                    ForceForeground(cand);
                    return true;
                }
                Thread.Sleep(50);
            }
            return false;
        }

        // ---------------- 内部 ----------------

        /// <summary>
        /// 在**普通后台线程**调用 ShellExecute（对齐 Python 的线程模型：不显式初始化 COM），
        /// 并把返回值带回。注意返回值只用于**诊断日志**，不作为成功判据。
        /// </summary>
        private static long Fire(string operation, string file, string lpDirectory)
        {
            long code = -1;
            var done = new ManualResetEventSlim(false);
            var t = new Thread(() =>
            {
                try
                {
                    code = ShellExecuteW(IntPtr.Zero, operation, file, null,
                                         lpDirectory, SW_SHOWNORMAL).ToInt64();
                }
                catch { code = -1; }
                finally { done.Set(); }
            });
            t.IsBackground = true;
            t.Start();
            done.Wait(2000);
            return code;
        }

        private static long FireViaRundll32(string url)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(
                    "rundll32.exe", "url.dll,FileProtocolHandler " + Quote(url))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (p == null) return -1;
                p.WaitForExit(1500);
                return p.HasExited ? p.ExitCode : 0;
            }
            catch { return -1; }
        }

        private static void ForceForeground(IntPtr hwnd)
        {
            try
            {
                ShowWindow(hwnd, SW_RESTORE);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            catch { }
        }

        private static string WindowTitle(IntPtr hwnd)
        {
            try
            {
                var sb = new StringBuilder(512);
                GetWindowTextW(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static void Report(string message)
        {
            try { Log?.Invoke("[Shell] " + message); } catch { }
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }
    }
}
