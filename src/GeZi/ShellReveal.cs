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

        public static bool Reveal(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            string parent = Path.GetDirectoryName(path);
            if (!File.Exists(path))
                return !string.IsNullOrEmpty(parent) && ShellLaunch.OpenFolder(parent);

            List<IntPtr> before = ShellLaunch.ExplorerWindows();
            string folderName = Path.GetFileName(parent ?? "");

            // ① 选中该文件：S_OK 直接采信（快路径）
            int hr = SelectOnStaThread(path);
            if (hr == 0) return true;

            // ② 返回码可疑时再用"窗口是否出现"复核
            if (ShellLaunch.WaitForExplorerWindow(before, folderName, 1200)) return true;

            // ② 退到打开所在目录（与 Python 的兜底一致）
            if (!string.IsNullOrEmpty(parent) && ShellLaunch.OpenFolder(parent)) return true;

            try { ShellLaunch.Log?.Invoke("[Shell] Reveal 失败 hr=" + hr + " path=" + path); } catch { }
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
