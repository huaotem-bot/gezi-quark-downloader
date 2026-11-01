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
using GeZi.Core.Api;
using GeZi.Core.Models;
using GeZi.Core.Support;
using GeZi.Providers.Quark;
using GeZi.Providers.Uc;
using GeZi.Providers.Aliyun;
using GeZi.Providers.Xunlei;

namespace GeZi.Providers
{
    /// <summary>创建一个网盘客户端的凭证集合。</summary>
    public sealed class PanCredential
    {
        /// <summary>
        /// 夸克 Cookie（<c>.quark.cn</c> 域）。
        /// <para>
        /// ⚠️ 与 <see cref="UcCookie"/> 是**两个不同的值**：夸克与 UC 的登录态
        /// 虽然都叫 <c>__puus</c>，但域不同、值也不同，混用会拿错账号。
        /// 早前这里只有一个 <c>Cookie</c> 字段，调用方得自己按网盘挑 —— 那是个隐患，
        /// 所以拆成两个显式字段，让类型系统帮忙挡住。
        /// </para>
        /// </summary>
        public string QuarkCookie { get; set; } = "";

        /// <summary>UC 网盘 Cookie（<c>.uc.cn</c> 域，含 <c>__puus</c>）。</summary>
        public string UcCookie { get; set; } = "";

        /// <summary>阿里云盘用 refresh_token。</summary>
        public string RefreshToken { get; set; } = "";

        /// <summary>
        /// 阿里云盘网页登录拿到的 access_token（与 <see cref="RefreshToken"/> 互斥）。
        /// <para>
        /// 🚨 这个字段必须传：新版阿里把 refresh_token 存在服务端 session，
        /// 网页登录只能拿到约 2 小时有效的 access_token。早前这里漏了它，
        /// 于是「网页登录成功后解析分享仍然失败」—— 客户端拿到空 refresh_token
        /// 就判自己没登录。
        /// </para>
        /// </summary>
        public string AccessToken { get; set; } = "";

        /// <summary>
        /// 阿里云盘的设备 ID（设备签名用）。与 <see cref="DevicePrivateKey"/> 成对。
        /// 取链/列目录都要签名，缺了会被拒。
        /// </summary>
        public string AliyunDeviceId { get; set; } = "";

        /// <summary>阿里云盘的设备私钥（64 hex secp256k1），与 <see cref="AliyunDeviceId"/> 成对。</summary>
        public string AliyunDevicePrivateKey { get; set; } = "";

        /// <summary>迅雷云盘用 pan_token（Authorization: Bearer）。</summary>
        public string PanToken { get; set; } = "";

        /// <summary>迅雷云盘的极验验证码 token（x-captcha-token）。</summary>
        public string CaptchaToken { get; set; } = "";

        /// <summary>
        /// 迅雷云盘的设备标识（<c>X-Device-Id</c>）。
        /// <para>
        /// 必须与**签发 token 的那台设备**一致：网页登录时它来自 Cookie 里的
        /// <c>deviceid=div101.&lt;32位&gt;</c>，账号登录时由 <c>devicesign</c> 推导。
        /// 留空会让客户端退回随机 GUID，服务端判"设备不匹配" → HTTP 400
        /// <c>captcha_required</c>（表现为"登录态无效"）。
        /// </para>
        /// </summary>
        public string DeviceId { get; set; } = "";

        public bool HasAny
        {
            get
            {
                return !string.IsNullOrEmpty(QuarkCookie)
                    || !string.IsNullOrEmpty(UcCookie)
                    || !string.IsNullOrEmpty(RefreshToken)
                    || !string.IsNullOrEmpty(AccessToken)
                    || !string.IsNullOrEmpty(PanToken);
            }
        }
    }

    /// <summary>
    /// 网盘客户端注册表：把"网盘种类"映射到"具体实现"，并集中声明能力。
    /// <para>
    /// 上层（UI）只需要 <see cref="Create"/> 拿到 <see cref="IPanProvider"/>，
    /// 不需要知道背后是夸克、UC、阿里还是迅雷 —— 新增网盘时只改这一个文件。
    /// </para>
    /// </summary>
    public static class PanProviderRegistry
    {
        /// <summary>所有支持的网盘（顺序即 UI 展示顺序）。</summary>
        public static readonly PanKind[] All =
        {
            PanKind.Quark, PanKind.Uc, PanKind.Aliyun, PanKind.Xunlei,
        };

        /// <summary>
        /// 创建指定网盘的客户端。凭证不足时不抛异常 —— 客户端自己会报告
        /// <see cref="IPanProvider.IsLoggedIn"/> 为 false，由 UI 引导登录。
        /// </summary>
        public static IPanProvider Create(PanKind kind, PanCredential cred)
        {
            if (cred == null)
                cred = new PanCredential();

            switch (kind)
            {
                case PanKind.Uc:
                    return new UcDriveClient(cred.UcCookie);

                case PanKind.Aliyun:
                    // 阿里有两条登录路线：refresh_token（可续期）与网页 access_token（约 2 小时）。
                    // 两者都要传进去，客户端自己按有没有 refresh_token 决定续期策略。
                    return new AliyunClient(cred.RefreshToken, null, null, cred.AccessToken,
                                            cred.AliyunDeviceId, cred.AliyunDevicePrivateKey);

                case PanKind.Xunlei:
                    return new XunleiClient(cred.PanToken, cred.CaptchaToken, null, cred.DeviceId);

                case PanKind.Quark:
                    // 夸克复用已验证的 QuarkClient，用适配器接入统一接口（不重写网络逻辑）。
                    // 注意用 QuarkCookie 而不是 UcCookie —— 两者是不同的登录态。
                    return new QuarkProviderAdapter(cred.QuarkCookie);

                default:
                    throw new ArgumentOutOfRangeException("kind");
            }
        }

        /// <summary>各网盘的能力声明（不需要创建客户端即可查询，UI 可用来预置界面）。</summary>
        public static PanCapabilities CapabilitiesOf(PanKind kind)
        {
            switch (kind)
            {
                case PanKind.Quark:
                    return new PanCapabilities
                    {
                        TransferFree = true,
                        NeedsCaptcha = false,
                        NeedsSignature = false,
                        SupportsPasscode = true,
                        SupportsSubFolder = true,
                        RequiresLogin = true,
                        Note = "本项目的主力网盘，取链链路已长期验证。",
                    };
                case PanKind.Uc:
                    return new PanCapabilities
                    {
                        TransferFree = true,
                        NeedsCaptcha = false,
                        NeedsSignature = false,
                        SupportsPasscode = true,
                        SupportsSubFolder = true,
                        RequiresLogin = true,
                        Note = "与夸克同一套后端；免转存取链只需登录 Cookie（__puus）。",
                    };
                case PanKind.Aliyun:
                    return new PanCapabilities
                    {
                        TransferFree = true,
                        NeedsCaptcha = false,
                        NeedsSignature = false,
                        SupportsPasscode = true,
                        SupportsSubFolder = true,
                        RequiresLogin = true,
                        Note = "用 refresh_token 鉴权；受账号等级与分享者设置限制。",
                    };
                case PanKind.Xunlei:
                    return new PanCapabilities
                    {
                        TransferFree = true,
                        NeedsCaptcha = true,
                        NeedsSignature = false,
                        SupportsPasscode = true,
                        SupportsSubFolder = true,
                        RequiresLogin = true,
                        Note = "取直链需要极验验证码 token —— 客户端会自动申请，无需用户手动操作；"
                             + "分享文件需先转存到自己的网盘再取链，取完立即清理。",
                    };
                default:
                    return new PanCapabilities();
            }
        }

        /// <summary>需要哪些凭证字段（UI 据此显示对应的输入框）。</summary>
        public static string CredentialHint(PanKind kind)
        {
            switch (kind)
            {
                case PanKind.Quark:
                case PanKind.Uc:
                    return "登录 Cookie（含 __puus）";
                case PanKind.Aliyun:
                    return "refresh_token（阿里云盘开放平台/网页登录后取得）";
                case PanKind.Xunlei:
                    return "pan_token + x-captcha-token（后者需在浏览器过完滑块验证）";
                default:
                    return "";
            }
        }
    }
}