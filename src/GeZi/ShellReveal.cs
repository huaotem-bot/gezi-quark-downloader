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
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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
    /// 「定位到文件」——对齐 Python Flet 的 <c>_open_dir_front(path, select=...)</c>：
    /// 先尝试选中文件，再**用窗口是否出现**判断成功（不看 API 返回码），
    /// 最后退到"打开所在目录"。
    /// </summary>
    internal static class ShellReveal
    {
        private const int CoInitApartmentThreaded = 0x2;
        private const int CoInitDisableOle1Dde = 0x4;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr ILCreateFromPathW(string path);
        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern void ILFree(IntPtr pidl);
        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl,
                                                             IntPtr apidl, uint flags);
        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, int coInit);
        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        /// <summary>0x80070005 = E_ACCESSDENIED：进程权限不够（最常见是程序放在受限制目录里运行）。</summary>
        private const int EAccessDenied = unchecked((int)0x80070005);

        public static bool Reveal(string path)
        {
            string ignored;
            return Reveal(path, out ignored);
        }

        /// <summary>
        /// 定位并在资源管理器中选中文件。
        /// <paramref name="failReason"/> 失败时给出<b>人话原因</b>，成功时为 null。
        /// <para></para>
        /// <para>
        /// 为什么要区分原因：早先一律报「找不到该文件或所在目录」，但实测最常见的失败
        /// 其实是<b>权限不足</b>（日志里 <c>hr=-2147024891</c> = 0x80070005、<c>open=5</c>），
        /// 文件明明就在那里。报错文案指错方向，用户会以为文件丢了。
        /// </para>
        /// </summary>
        public static bool Reveal(string path, out string failReason)
        {
            failReason = null;
            if (string.IsNullOrEmpty(path))
            {
                failReason = "路径为空";
                return false;
            }

            string parent = Path.GetDirectoryName(path);
            if (!File.Exists(path))
            {
                // 文件还没落盘（例如下载中被取消）→ 退到打开所在目录
                if (!string.IsNullOrEmpty(parent) && ShellLaunch.OpenFolder(parent))
                {
                    failReason = null;
                    return true;
                }
                failReason = Directory.Exists(parent)
                    ? "文件还没写盘，打开所在目录也被系统拒绝"
                    : "找不到该文件或所在目录";
                return false;
            }

            List<IntPtr> before = ShellLaunch.ExplorerWindows();
            string folderName = Path.GetFileName(parent ?? "");

            // ① 选中该文件：S_OK 直接采信（快路径）
            int hr = SelectOnStaThread(path);
            if (hr == 0) return true;

            // ② 返回码可疑时再用"窗口是否出现"复核
            if (ShellLaunch.WaitForExplorerWindow(before, folderName, 1200)) return true;

            // ③ 退到打开所在目录（与 Python 的兜底一致）
            if (!string.IsNullOrEmpty(parent) && ShellLaunch.OpenFolder(parent)) return true;

            try { ShellLaunch.Log?.Invoke("[Shell] Reveal 失败 hr=" + hr + " path=" + path); } catch { }

            // 文件确实存在，那失败就不是"找不到"，而是权限/环境问题
            failReason = (hr == EAccessDenied)
                ? "系统拒绝了资源管理器调用（权限不足）。把本程序复制到普通目录（如 D:\\GeZi）再运行即可"
                : "系统拒绝了资源管理器调用（错误码 " + hr + "），可手动打开下载目录查看";
            return false;
        }

        private static int SelectOnStaThread(string path)
        {
            int hr = -1;
            var done = new ManualResetEventSlim(false);
            var t = new Thread(() =>
            {
                int co = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded | CoInitDisableOle1Dde);
                IntPtr pidl = IntPtr.Zero;
                try
                {
                    if (co < 0) return;
                    pidl = ILCreateFromPathW(Path.GetFullPath(path));
                    if (pidl != IntPtr.Zero)
                        hr = SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0);
                }
                catch { hr = -1; }
                finally
                {
                    if (pidl != IntPtr.Zero) ILFree(pidl);
                    if (co >= 0) CoUninitialize();
                    done.Set();
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            done.Wait(3000);
            return hr;
        }
    }
}
