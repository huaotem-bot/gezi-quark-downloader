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
using System.Security.Cryptography;
using System.Text;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 迅雷账号登录协议里那几个"必须算对否则一律失败"的签名。
    /// <para>
    /// 这些值的算法没有公开文档，是从安卓客户端的登录流程里逆出来的。
    /// 对照实现：<c>refs/wenxi/lib/data/providers/xunlei_protocol.dart</c>。
    /// </para>
    /// <para>
    /// 三个值的用途各不相同，混用不会报错、只会让服务端判无效：
    /// <list type="bullet">
    /// <item><c>DeviceSign</c> —— 请求体里的 <c>devicesign</c> 字段（<c>div101.</c> 前缀）。</item>
    /// <item><c>CaptchaSign</c> —— <c>captcha/init</c> 的 <c>meta.captcha_sign</c>（<c>1.</c> 前缀）。</item>
    /// <item><c>PeerId</c> —— 请求体里的 <c>peerID</c>，只是一个 32 位随机 hex。</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class XunleiProtocol
    {
        /// <summary>
        /// 网页登录凭据打包后的前缀。
        /// <para>
        /// 内嵌网页登录一次能拿到 5 个值（access_token / refresh_token / sub /
        /// device_id / captcha_token），而 <c>ExecuteScriptAsync</c> 每多跑一次脚本
        /// 就多一次与页面状态不同步的机会 —— 所以脚本一次返回一条打包记录，
        /// 由 <see cref="TryParseRecord"/> 拆开。
        /// </para>
        /// <para>
        /// 分隔符用 <c>|</c>：这些值都是 base64url / JWT 字符集，
        /// 不会出现竖线，所以不需要转义。
        /// </para>
        /// </summary>
        public const string RecordPrefix = "xlrec:";

        /// <summary>把网页登录拿到的 5 个字段打包成一条记录（空字段保留占位）。</summary>
        public static string BuildRecord(string accessToken, string refreshToken,
            string sub, string deviceId, string captchaToken)
        {
            return RecordPrefix
                + Clean(accessToken) + "|"
                + Clean(refreshToken) + "|"
                + Clean(sub) + "|"
                + Clean(deviceId) + "|"
                + Clean(captchaToken);
        }

        /// <summary>判断一段文本是不是打包记录（区分"裸 token"与"记录"）。</summary>
        public static bool IsRecord(string text)
        {
            return !string.IsNullOrEmpty(text)
                && text.StartsWith(RecordPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// 拆开打包记录。不是记录（例如用户手动粘贴的裸 token）时返回 false，
        /// 由调用方按裸 token 处理。
        /// </summary>
        public static bool TryParseRecord(string text, out string accessToken,
            out string refreshToken, out string sub, out string deviceId, out string captchaToken)
        {
            accessToken = "";
            refreshToken = "";
            sub = "";
            deviceId = "";
            captchaToken = "";

            if (!IsRecord(text))
                return false;

            string body = text.Substring(RecordPrefix.Length);
            string[] parts = body.Split('|');
            if (parts.Length > 0) accessToken = parts[0];
            if (parts.Length > 1) refreshToken = parts[1];
            if (parts.Length > 2) sub = parts[2];
            if (parts.Length > 3) deviceId = parts[3];
            if (parts.Length > 4) captchaToken = parts[4];
            return true;
        }

        /// <summary>去掉会破坏打包格式的字符（换行与分隔符本身）。</summary>
        private static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("|", "").Replace("\r", "").Replace("\n", "").Trim();
        }

        /// <summary>生成一个 32 位小写 hex 标识（设备 id / peer id 都用它）。</summary>
        public static string NewIdentifier()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);

            var sb = new StringBuilder(32);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// 设备标识是否合法（32 位**小写** hex）—— 用于校验存下来的旧值。
        /// <para>
        /// 这里刻意不接受大写：<see cref="DeviceSign"/> 会把标识原样拼进摘要，
        /// 大小写不同就会算出不同的 <c>devicesign</c>，服务端会当成另一台设备。
        /// 生成侧永远是 <see cref="NewIdentifier"/> 产出的小写，所以拒绝大写只是把
        /// 「存坏了的值」挡在门外，不会误伤正常数据。
        /// </para>
        /// </summary>
        public static bool IsIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 32)
                return false;

            foreach (var c in value)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 设备签名：<c>"div101." + id + md5(sha1(id + 包名 + 固定盐))</c>，固定 71 字符。
        /// <para>
        /// 注意内层是 **sha1 的十六进制字符串** 再参与 md5，不是 sha1 的原始字节。
        /// </para>
        /// </summary>
        public static string DeviceSign(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
                throw new ArgumentException("设备标识不能为空。", "deviceId");

            string inner = deviceId + XunleiConstants.AppPackageName + XunleiConstants.DeviceSignSalt;
            string sha1Hex = Sha1Hex(inner);
            return "div101." + deviceId + Md5Hex(sha1Hex);
        }

        /// <summary>
        /// 验证码签名：以 <c>clientId+version+包名+设备id+时间戳</c> 为种子，
        /// 按固定顺序对 10 个盐逐轮 md5，最后加 <c>"1."</c> 前缀。
        /// </summary>
        public static string CaptchaSign(string deviceId, string timestamp)
        {
            if (string.IsNullOrEmpty(deviceId))
                throw new ArgumentException("设备标识不能为空。", "deviceId");
            if (string.IsNullOrEmpty(timestamp))
                throw new ArgumentException("时间戳不能为空。", "timestamp");

            string digest = XunleiConstants.AppClientId + XunleiConstants.AppVersion
                          + XunleiConstants.AppPackageName + deviceId + timestamp;

            foreach (var salt in XunleiConstants.CaptchaSalts)
                digest = Md5Hex(digest + salt);

            return "1." + digest;
        }

        /// <summary>当前毫秒时间戳字符串（captcha_sign 用的就是它）。</summary>
        public static string NowTimestamp()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        }

        /// <summary>
        /// 登录请求体的公共字段。每次请求都要重新生成 <c>sequenceNo</c>，
        /// 复用同一个值会被服务端判重。
        /// </summary>
        public static System.Collections.Generic.Dictionary<string, object> BaseBody(
            string deviceId, string sdkVersion, string creditKey = "")
        {
            return new System.Collections.Generic.Dictionary<string, object>
            {
                { "protocolVersion", "301" },
                { "sequenceNo", (10000000 + NextInt(89999999)).ToString() },
                { "platformVersion", "10" },
                { "isCompressed", "0" },
                { "appid", "40" },
                { "clientVersion", XunleiConstants.AppVersion },
                { "peerID", PeerIdFor(deviceId) },
                { "appName", "ANDROID-" + XunleiConstants.AppPackageName },
                { "sdkVersion", sdkVersion },
                { "devicesign", DeviceSign(deviceId) },
                { "netWorkType", "WIFI" },
                { "providerName", "NONE" },
                { "deviceModel", "M2004J7AC" },
                { "deviceName", "Xiaomi_M2004j7ac" },
                { "OSVersion", "12" },
                { "creditkey", creditKey ?? "" },
                { "hl", "zh-CN" },
            };
        }

        /// <summary>
        /// peerID。参考实现里它和设备 id 是两个独立的随机值，
        /// 这里由设备 id 派生 —— 结果同样是 32 位 hex，且不需要多存一个字段。
        /// </summary>
        public static string PeerIdFor(string deviceId)
        {
            return Md5Hex("gezi-peer:" + (deviceId ?? ""));
        }

        private static int NextInt(int maxExclusive)
        {
            // 用加密随机源，避免多个实例同时启动时 sequenceNo 撞车。
            var bytes = new byte[4];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return (int)(BitConverter.ToUInt32(bytes, 0) % (uint)maxExclusive);
        }

        private static string Sha1Hex(string value)
        {
            using (var sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(value));
                return ToHex(hash);
            }
        }

        private static string Md5Hex(string value)
        {
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(value));
                return ToHex(hash);
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
