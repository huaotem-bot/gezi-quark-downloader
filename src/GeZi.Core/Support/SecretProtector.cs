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
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 用 Windows DPAPI 加密/解密本地敏感字符串（Cookie 登录态等）。
    ///
    /// 设计要点：
    /// - 作用域 CurrentUser：密文只能被同一台机器的同一个 Windows 用户解开，
    ///   换个用户或把配置文件拷到别的机器都无法读取（这正是我们要的）。
    /// - 额外熵来自独立的 <c>%LOCALAPPDATA%\GeZi\secret.key</c>（32 字节随机数，
    ///   文件 ACL 收紧到当前用户），“密钥”与“状态文件”分离；无法创建时回退到
    ///   内置熵，保证老密文仍能解开。
    /// - 密文以 "enc:v1:" 前缀标记，用于识别与平滑迁移：
    ///   读到没有前缀的字符串 → 视为旧版明文，直接返回原文，下次保存时自动加密。
    /// - 加密失败必须 fail-closed：宁可这次不保存，也不能把明文 Cookie 写进配置。
    ///   解密失败返回空串，让调用方按“未登录”处理。
    /// </summary>
    public static class SecretProtector
    {
        /// <summary>DPAPI 不可用/加密失败。调用方应拒绝保存并提示用户，而不是落明文。</summary>
        public sealed class SecretProtectionException : Exception
        {
            public SecretProtectionException(string message, Exception inner)
                : base(message, inner) { }
        }
        private const string Prefix = "enc:v1:";

        /// <summary>历史内置熵。只在独立密钥文件不可用时使用，且解密时会回退尝试它。</summary>
        private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("GeZi.Downloader.Cookie.v1");

        private static readonly object EntropyLock = new object();
        private static byte[] _entropy;
        private static string _entropyPath;

        /// <summary>独立密钥文件路径（%LOCALAPPDATA%\GeZi\secret.key）。</summary>
        public static string EntropyFilePath
        {
            get
            {
                if (_entropyPath != null)
                    return _entropyPath;
                string dir;
                try
                {
                    dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GeZi");
                }
                catch
                {
                    dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GeZi");
                }
                _entropyPath = Path.Combine(dir, "secret.key");
                return _entropyPath;
            }
        }

        /// <summary>是否已加密（带我们的前缀标记）。</summary>
        public static bool IsProtected(string value)
            => !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>字段级加密：空值原样返回；已是密文原样返回；失败时抛 <see cref="SecretProtectionException"/>。</summary>
        public static string ProtectField(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return plain;
            return IsProtected(plain) ? plain : Protect(plain);
        }

        /// <summary>字段级解密：无前缀视为旧明文原样返回；解不开返回空串（绝不返回乱码）。</summary>
        public static string UnprotectField(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;
            return Unprotect(stored);
        }

        /// <summary>当前额外熵：优先独立密钥文件，退路是内置熵。</summary>
        private static byte[] GetEntropy()
        {
            if (_entropy != null)
                return _entropy;
            lock (EntropyLock)
            {
                if (_entropy != null)
                    return _entropy;
                _entropy = LoadOrCreateEntropy();
                return _entropy;
            }
        }

        private static byte[] LoadOrCreateEntropy()
        {
            string path = EntropyFilePath;
            try
            {
                if (File.Exists(path))
                {
                    byte[] existing = File.ReadAllBytes(path);
                    if (existing.Length >= 32)
                        return existing;
                }
            }
            catch { }

            try
            {
                byte[] fresh = new byte[32];
                using (var rng = RandomNumberGenerator.Create())
                    rng.GetBytes(fresh);
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, fresh);
                HardenAcl(path);
                return fresh;
            }
            catch
            {
                // 无法创建/读取独立密钥：退回内置熵，保证与历史密文兼容。
                return LegacyEntropy;
            }
        }

        /// <summary>把密钥文件 ACL 收紧到当前用户（断开继承）。失败不影响功能。</summary>
        private static void HardenAcl(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                FileSecurity sec = fi.GetAccessControl();
                sec.SetAccessRuleProtection(true, false);
                WindowsIdentity me = WindowsIdentity.GetCurrent();
                if (me != null && me.User != null)
                {
                    sec.AddAccessRule(new FileSystemAccessRule(
                        me.User, FileSystemRights.FullControl, AccessControlType.Allow));
                }
                fi.SetAccessControl(sec);
            }
            catch
            {
                // 某些文件系统（FAT/网络盘）不支持 ACL；密钥仍可用，只是权限依赖目录本身。
            }
        }

        /// <summary>
        /// 加密明文。已是密文则原样返回；空字符串返回空字符串。
        /// </summary>
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain))
                return "";
            if (IsProtected(plain))
                return plain;

            try
            {
                byte[] raw = Encoding.UTF8.GetBytes(plain);
                byte[] enc = ProtectedData.Protect(raw, GetEntropy(), DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(enc);
            }
            catch (Exception ex)
            {
                // 加密不可用（极罕见：用户配置文件损坏等）→ 拒绝保存。
                // 绝不能 return plain：那会把“加密存储”变成事实上的明文存储。
                throw new SecretProtectionException("DPAPI 加密失败，已拒绝保存明文", ex);
            }
        }

        /// <summary>
        /// 解密。无前缀视为旧版明文直接原样返回；解不开（换了机器/换了用户/数据损坏）
        /// 则返回空串 —— 让调用方按“未登录”处理，而不是拿到一堆乱码去请求接口。
        /// </summary>
        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored))
                return "";
            if (!IsProtected(stored))
                return stored;   // 旧版明文，向后兼容

            byte[] enc;
            try
            {
                enc = Convert.FromBase64String(stored.Substring(Prefix.Length));
            }
            catch
            {
                return "";
            }

            byte[] current = GetEntropy();
            byte[] raw;
            if (TryUnprotect(enc, current, out raw))
                return Encoding.UTF8.GetString(raw);

            // 独立密钥文件可能是后生成的：老密文用内置熵，回退再试一次。
            if (!ReferenceEquals(current, LegacyEntropy) && TryUnprotect(enc, LegacyEntropy, out raw))
                return Encoding.UTF8.GetString(raw);

            // 解密失败：多半是配置文件被复制到了别的机器/用户
            return "";
        }

        private static bool TryUnprotect(byte[] enc, byte[] entropy, out byte[] raw)
        {
            try
            {
                raw = ProtectedData.Unprotect(enc, entropy, DataProtectionScope.CurrentUser);
                return true;
            }
            catch
            {
                raw = null;
                return false;
            }
        }
    }
}