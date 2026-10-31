// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GeZi
{
    /// <summary>
    /// Windows 自带的现代目录选择对话框（Vista 风格：IFileDialog + FOS_PICKFOLDERS）。
    /// 取代 WinForms 的 FolderBrowserDialog —— net48 上后者仍是 Win2000 时代的树形列表：
    /// 没有地址栏、不能粘贴路径、不能拖动。这里直接 COM interop 系统对话框，
    /// 外观与资源管理器/所有现代应用一致，自带「新建文件夹」按钮，零外部依赖。
    /// </summary>
    /// <remarks>
    /// 🚨 接口方法**必须按 COM vtable 顺序**声明。IFileDialog 实际是 24 个方法
    /// （Show 之后：SetFileTypes…SetFileNameLabel、**GetResult 在第 18 位**、
    /// AddPlace、SetDefaultExtension、Close、SetClientGuid、ClearClientData、SetFilter）
    /// —— 既没有 AddFilter 也没有 GetFileNameLabel。首次实现凭（错误的）记忆多写了
    /// 这两个方法，把 GetResult 顶到第 20 位，实际调到 SetDefaultExtension 上：
    /// 返回 S_OK 但出参没人写 → 封送出 null → Pick 返回 null →
    /// 用户明明选了目录却提示「已取消」。已用 WindowsAPICodePack 反射对照 +
    /// 自动确认的诊断程序实机复现定位（2026-10-31）。
    /// </remarks>
    internal static class FolderPicker
    {
        /// <summary>
        /// 弹出目录选择框。
        /// </summary>
        /// <param name="owner">属主窗口（可为 null；可见时对话框居中于它并保持模态）。</param>
        /// <param name="initialDir">初始目录（不存在或非法时忽略，用系统记住的默认位置）。</param>
        /// <param name="title">对话框标题（null/空 = 系统默认）。</param>
        /// <returns>选中的目录绝对路径；用户取消或出错返回 null。</returns>
        public static string Pick(Window owner, string initialDir, string title)
        {
            IntPtr hOwner = (owner != null && owner.IsVisible)
                ? new WindowInteropHelper(owner).Handle
                : IntPtr.Zero;

            IFileOpenDialog dlg = (IFileOpenDialog)new FileOpenDialogRcw();
            try
            {
                uint options;
                dlg.GetOptions(out options);
                dlg.SetOptions(options | FosPickFolders | FosForceFileSystem);

                if (!string.IsNullOrEmpty(initialDir))
                {
                    try
                    {
                        IShellItem item;
                        var iidShellItem = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                        SHCreateItemFromParsingName(initialDir, IntPtr.Zero, ref iidShellItem, out item);
                        if (item != null)
                            dlg.SetFolder(item);
                    }
                    catch { }   // 目录不存在 → 用系统默认位置，不拦着选
                }

                if (!string.IsNullOrEmpty(title))
                    dlg.SetTitle(title);

                if (dlg.Show(hOwner) != 0)   // S_OK = 0；取消（0x800704C7）等一律视为放弃
                    return null;

                IShellItem result;
                dlg.GetResult(out result);
                if (result == null)
                    return null;
                string path;
                result.GetDisplayName(SigdnFilesysPath, out path);
                return path;
            }
            catch
            {
                return null;
            }
            finally
            {
                Marshal.ReleaseComObject(dlg);
            }
        }

        private const uint FosPickFolders = 0x20;       // FOS_PICKFOLDERS：选目录而非文件
        private const uint FosForceFileSystem = 0x40;   // FOS_FORCEFILESYSTEM：只收真实文件系统路径
        private const uint SigdnFilesysPath = 0x80058000; // SIGDN_FILESYSPATH

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc,
            ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        // ⚠ 不能 sealed：编译器要允许「占位类 → COM 接口」的运行时 QI 转换，
        //   sealed 会直接报 CS0030（编译期认定不可能有实现）。
        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRcw { }   // CLSID_FileOpenDialog 的 RCW 占位

        /// <summary>
        /// IFileOpenDialog。前 18 个方法是 IFileDialog 的前缀（vtable 顺序，已与
        /// WindowsAPICodePack 的官方声明逐一对齐），后 2 个是 IFileOpenDialog 自有。
        /// 只用到 Show / GetOptions / SetOptions / SetFolder / SetTitle / GetResult。
        /// </summary>
        [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr hwndOwner);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void GetResult(out IShellItem ppsi);
            // IFileOpenDialog 自有（本程序不用，仅占位保持 vtable 完整）
            void GetResults(out IntPtr ppenumShellItems);
            void GetSelectedItems(out IntPtr psai);
        }

        /// <summary>IShellItem。只用 GetDisplayName（第 3 个方法，前面的按 vtable 顺序占位）。</summary>
        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }
}
