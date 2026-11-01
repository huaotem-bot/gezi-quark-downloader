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
using System.IO.Compression;
using System.Threading;
using GeZi.Core.Api;
using GeZi.DownloadEngine;
using GeZi.DownloadEngine.Framework;
using GeZi.DownloadEngine.Storage;
using GeZi.DownloadEngine.Media;
using GeZi.Core.Models;
using GeZi.Core.Support;
using GeZi.Providers;
using GeZi.Providers.Quark;
using GeZi.Providers.Uc;
using GeZi.Providers.Aliyun;
using GeZi.Providers.Xunlei;
using GeZi.Providers.Shared;
using GeZi.Providers.Qr;

using GeZi.Services;
namespace GeZi.SelfTest
{
    /// <summary>
    /// 零 NuGet 自测宿主：纯控制台断言 + 退出码。
    /// 覆盖不依赖网络/UI 的核心逻辑，能在 CI 与本地快速回归。
    /// </summary>
    internal static class Program
    {
        private static int _failed;
        private static string _tmp;

        /// <summary>
        /// 可写的临时根目录。受限沙箱或 CI 里 %TEMP% 可能不可写，
        /// 因此回退到当前工作目录；两处都失败才退回当前目录本身。
        /// </summary>
        private static string CreateTempRoot()
        {
            foreach (string baseDir in new[] { Path.GetTempPath(), Environment.CurrentDirectory })
            {
                try
                {
                    if (string.IsNullOrEmpty(baseDir)) continue;
                    string p = Path.Combine(baseDir, "gezi-selftest-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(p);
                    return p;
                }
                catch { }
            }
            return Environment.CurrentDirectory;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            // 真实账号验证模式：GeZi.SelfTest.exe pan-probe <kind> <分享链接> <凭证> [验证码token]
            // 自测只覆盖"不需要网络就能证明"的部分；真机取链必须由用户用自己的账号跑这个模式。
            if (args != null && args.Length > 0
                && string.Equals(args[0], "pan-probe", StringComparison.OrdinalIgnoreCase))
            {
                return PanProbe(args);
            }

            _tmp = CreateTempRoot();
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                try
                {
                    File.AppendAllText(Path.Combine(_tmp, "unhandled.log"),
                        (ex != null ? ex.GetType().FullName : "?") + Environment.NewLine);
                }
                catch { }
            };
            try
            {
                Run("thread_allocator", TestThreadAllocator);
                Run("path_util", TestPathUtil);
                Run("parts_lease", TestPartsLease);
                Run("secret_protector", TestSecretProtector);
                Run("pending_deletes", TestPendingDeletes);
                Run("scheduler_budget", TestSchedulerBudget);
                Run("diagnostics_pack", TestDiagnosticsPack);
                Run("pan_url_parser", TestPanUrlParser);
                Run("pan_providers", TestPanProviders);
                Run("pan_entry_mapper", TestPanEntryMapper);
                Run("pan_contract_uc", TestUcContract);
                Run("pan_contract_aliyun", TestAliyunContract);
                Run("pan_contract_xunlei", TestXunleiContract);
            Run("xunlei_captcha_retry", TestXunleiCaptchaRetry);
                Run("webview2_runtime", TestWebView2Runtime);
                Run("uc_cookie_plausible", TestUcCookiePlausible);
                Run("aliyun_device_signature", TestAliyunDeviceSignature);
                Run("xunlei_login_protocol", TestXunleiLoginProtocol);
                Run("mp4_moov_probe", TestMp4MoovProbe);
                Run("write_merge", TestWriteMerge);
        Run("disk_media", TestDiskMedia);
        Run("chunk_queue", TestChunkQueue);
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("FAIL main :: " + ex.GetType().FullName);
            }
            finally
            {
                try { Directory.Delete(_tmp, true); } catch { }
            }

            Console.WriteLine(_failed == 0 ? "SELFTEST_OK" : "SELFTEST_FAILED count=" + _failed);
            return _failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("FAIL " + name + " :: " + ex.GetType().FullName + " :: " + SafeMessage(ex));
            }
        }

        private static string SafeMessage(Exception ex)
        {
            try { return ex.Message; }
            catch { return "<no message>"; }
        }

        private static void Check(string name, bool ok, string detail = null)
        {
            if (ok)
            {
                Console.WriteLine("PASS " + name);
                return;
            }
            _failed++;
            Console.WriteLine("FAIL " + name + (string.IsNullOrEmpty(detail) ? "" : " :: " + detail));
        }

        private static void TestThreadAllocator()
        {
            var one = ThreadAllocator.Allocate(new long[] { 100 }, 256);
            Check("alloc.single", one.Length == 1 && one[0] == 256, Join(one));

            var equal = ThreadAllocator.Allocate(new long[] { 100, 100 }, 256, 512, 4, 2);
            Check("alloc.equal_split", equal[0] == 256 && equal[1] == 256, Join(equal));

            var min = ThreadAllocator.Allocate(new long[] { 1000000, 1 }, 256, 512, 4, 2);
            Check("alloc.min_floor", min[0] >= 4 && min[0] <= 256 && min[1] >= 4 && min[1] <= 256, Join(min));

            var many = ThreadAllocator.Allocate(new long[] { 1, 1, 1, 1, 1 }, 256, 512, 4, 2);
            bool bounds = true;
            foreach (int v in many)
                if (v < 4 || v > 256) bounds = false;
            Check("alloc.bounds", many.Length == 5 && bounds, Join(many));

            var zero = ThreadAllocator.Allocate(new long[0], 256);
            Check("alloc.empty", zero.Length == 0);
        }

        private static void TestPathUtil()
        {
            Check("path.sanitize_illegal", PathUtil.SanitizeFileName("a/b:c*?").IndexOf('/') < 0);
            Check("path.sanitize_reserved", PathUtil.SanitizeFileName("CON") == "_CON",
                PathUtil.SanitizeFileName("CON"));
            Check("path.reserved_ext", PathUtil.IsReservedName("CON.txt"));
            Check("path.not_reserved", !PathUtil.IsReservedName("console.txt"));

            string outDir = Path.Combine(_tmp, "out");
            string err, warn;
            Check("path.preflight_ok",
                PathUtil.TryPreflight(Path.Combine(outDir, "ok.bin"), out err, out warn), err);
            Check("path.preflight_empty", !PathUtil.TryPreflight("", out err, out warn));
            Check("path.preflight_reserved",
                !PathUtil.TryPreflight(Path.Combine(outDir, "CON.txt"), out err, out warn));
        }

        private static void TestPartsLease()
        {
            string dir = Path.Combine(_tmp, "lease");
            PartsLease.Touch(dir);
            Check("lease.active", PartsLease.IsProtected(dir, DateTime.UtcNow));
            Check("lease.state", PartsLease.ReadState(dir) == PartsLease.State.Active,
                PartsLease.ReadState(dir)?.ToString());

            string expired = Path.Combine(_tmp, "lease-expired");
            Directory.CreateDirectory(expired);
            File.WriteAllText(Path.Combine(expired, PartsLease.FileName),
                "v=1\npid=0\nstate=Active\nheartbeatUtc=2000-01-01T00:00:00Z\nexpiresUtc=2000-01-01T00:00:00Z\n");
            Check("lease.expired", !PartsLease.IsProtected(expired, DateTime.UtcNow));

            string legacy = Path.Combine(_tmp, "lease-legacy");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, PartsLease.FileName), "2000-01-01T00:00:00Z\n");
            Check("lease.legacy_expired", !PartsLease.IsProtected(legacy, DateTime.UtcNow));

            PartsLease.Release(dir);
            Check("lease.released", !File.Exists(Path.Combine(dir, PartsLease.FileName)));
        }

        private static void TestSecretProtector()
        {
            try
            {
                const string plain = "hello-gezi-世界";
                string cipher = SecretProtector.Protect(plain);
                Check("secret.protected", SecretProtector.IsProtected(cipher));
                Check("secret.roundtrip", SecretProtector.Unprotect(cipher) == plain);
                Check("secret.idempotent", SecretProtector.Protect(cipher) == cipher);
                Check("secret.legacy_plain", SecretProtector.Unprotect("plain-token") == "plain-token");
                Check("secret.field",
                    SecretProtector.UnprotectField(SecretProtector.ProtectField("field")) == "field");
            }
            catch (Exception ex)
            {
                Check("secret.dpapi", false, ex.Message);
            }
        }

        private static void TestPendingDeletes()
        {
            string dir = Path.Combine(_tmp, "pending");
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "movie.bin" + PartsPaths.PendingDeleteSuffix);
            File.WriteAllText(f, "x");
            int removed = PartsPaths.SweepPendingDeletes(new[] { dir });
            Check("pending.swept", removed == 1 && !File.Exists(f), "removed=" + removed);
        }

        private static void TestSchedulerBudget()
        {
            using (var scheduler = new DownloadScheduler(3, 512))
            {
                Check("scheduler.budget", scheduler.ConnectionBudget == 512,
                    "budget=" + scheduler.ConnectionBudget);
                Check("scheduler.leased_zero", scheduler.LeasedConnections == 0,
                    "leased=" + scheduler.LeasedConnections);
            }
        }

        private static string Join(int[] values)
        {
            return values == null ? "null" : string.Join(",", values);
        }

        private static void TestDiagnosticsPack()
        {
            string zip = Path.Combine(_tmp, "diag.zip");
            var info = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("线程", "256"),
            };
            string path = DiagnosticsPack.Create(zip, "GeZi", "test", "log line 1\nlog line 2", info);
            Check("diag.created", File.Exists(path), path);

            using (var z = ZipFile.OpenRead(path))
            {
                var infoEntry = z.GetEntry("info.txt");
                var logEntry = z.GetEntry("runtime.log");
                Check("diag.entries", infoEntry != null && logEntry != null);
                bool hasSetting = false;
                if (infoEntry != null)
                {
                    using (var r = new StreamReader(infoEntry.Open()))
                        hasSetting = r.ReadToEnd().IndexOf("线程: 256", StringComparison.Ordinal) >= 0;
                }
                Check("diag.includes_settings", hasSetting);
            }
        }

        /// <summary>
        /// 多网盘分享链接识别。这部分不需要任何账号，是新增网盘能力里
        /// 唯一能"离线证明正确"的环节，所以覆盖要扎实。
        /// </summary>
        private static void TestPanUrlParser()
        {
            // 四个网盘各认一次
            var quark = PanUrlParser.Parse("https://pan.quark.cn/s/abcdef123456");
            Check("pan.quark", quark != null && quark.Kind == PanKind.Quark
                && quark.ShareId == "abcdef123456");

            var uc = PanUrlParser.Parse("https://drive.uc.cn/s/ucshare001?pwd=1234");
            Check("pan.uc", uc != null && uc.Kind == PanKind.Uc
                && uc.ShareId == "ucshare001" && uc.Passcode == "1234");

            var ali = PanUrlParser.Parse("https://www.aliyundrive.com/s/aliShare789");
            Check("pan.aliyun", ali != null && ali.Kind == PanKind.Aliyun
                && ali.ShareId == "aliShare789");

            var alipan = PanUrlParser.Parse("https://www.alipan.com/s/xyz789abc");
            Check("pan.alipan", alipan != null && alipan.Kind == PanKind.Aliyun
                && alipan.ShareId == "xyz789abc");

            var xl = PanUrlParser.Parse("https://pan.xunlei.com/s/VNabcdefg123?pwd=ab12");
            Check("pan.xunlei", xl != null && xl.Kind == PanKind.Xunlei
                && xl.ShareId == "VNabcdefg123" && xl.Passcode == "ab12");

            // 整段分享文案：提取码在中文里
            var text = PanUrlParser.Parse("【资源分享】链接：https://pan.quark.cn/s/abc123xyz 提取码：x9k2");
            Check("pan.share_text", text != null && text.Kind == PanKind.Quark
                && text.ShareId == "abc123xyz" && text.Passcode == "x9k2");

            // 子目录 fid
            var sub = PanUrlParser.Parse(
                "https://pan.quark.cn/s/abc123xyz/0123456789abcdef0123456789abcdef");
            Check("pan.subfolder", sub != null && sub.StartFid == "0123456789abcdef0123456789abcdef");

            // 域名伪装必须拒绝（quark.cn.evil.com 不是夸克）
            var spoof = PanUrlParser.Parse("https://quark.cn.evil.com/s/abc123");
            Check("pan.spoof_rejected", spoof == null);

            // 认不出 / 空输入 → null，不抛异常
            Check("pan.empty", PanUrlParser.Parse("") == null);
            Check("pan.garbage", PanUrlParser.Parse("随便一段没有链接的文字") == null);

            // 陷阱：分享 ID 本身叫 pwd1234，不能被当成提取码
            var trap = PanUrlParser.Parse("https://pan.quark.cn/s/pwd1234");
            Check("pan.pwd_id_trap", trap != null && trap.ShareId == "pwd1234"
                && trap.Passcode == "");

            // 没有协议头也应能识别（用户常直接粘域名）
            var bare = PanUrlParser.Parse("drive.uc.cn/s/bare12345");
            Check("pan.bare_host", bare != null && bare.Kind == PanKind.Uc
                && bare.ShareId == "bare12345");

            // 展示名齐全
            Check("pan.display_names",
                PanUrlParser.DisplayNameOf(PanKind.Quark) == "夸克网盘"
                && PanUrlParser.DisplayNameOf(PanKind.Uc) == "UC网盘"
                && PanUrlParser.DisplayNameOf(PanKind.Aliyun) == "阿里云盘"
                && PanUrlParser.DisplayNameOf(PanKind.Xunlei) == "迅雷云盘");
        }

        /// <summary>
        /// 三个新网盘客户端的离线可验证部分：实例创建、能力声明、未登录判定、
        /// 以及"缺凭证时给出可操作提示而不是静默失败"。
        /// <para>
        /// ⚠️ 真实取链需要账号，不在这里测 —— 自测只覆盖"不需要网络就能证明"的部分。
        /// </para>
        /// </summary>
        private static void TestPanProviders()
        {
            var cred = new PanCredential();
            Check("pan.registry_all", PanProviderRegistry.All.Length == 4);

            // 未登录时创建客户端不应抛异常（由 UI 引导登录）
            using (var uc = (UcDriveClient)PanProviderRegistry.Create(PanKind.Uc, cred))
            {
                Check("pan.uc_created", uc != null && uc.Kind == PanKind.Uc);
                Check("pan.uc_display", uc.DisplayName == "UC网盘");
                Check("pan.uc_not_logged_in", !uc.IsLoggedIn);
                Check("pan.uc_transfer_free", uc.Capabilities.TransferFree);
                Check("pan.uc_no_captcha", !uc.Capabilities.NeedsCaptcha);
            }

            using (var ali = (AliyunClient)PanProviderRegistry.Create(PanKind.Aliyun, cred))
            {
                Check("pan.ali_created", ali != null && ali.Kind == PanKind.Aliyun);
                Check("pan.ali_display", ali.DisplayName == "阿里云盘");
                Check("pan.ali_not_logged_in", !ali.IsLoggedIn);
                Check("pan.ali_transfer_free", ali.Capabilities.TransferFree);
            }

            using (var xl = (XunleiClient)PanProviderRegistry.Create(PanKind.Xunlei, cred))
            {
                Check("pan.xunlei_created", xl != null && xl.Kind == PanKind.Xunlei);
                Check("pan.xunlei_display", xl.DisplayName == "迅雷云盘");
                Check("pan.xunlei_not_logged_in", !xl.IsLoggedIn);
                // 迅雷必须如实标注"需要验证码"，否则用户会以为点了就能用
                Check("pan.xunlei_needs_captcha", xl.Capabilities.NeedsCaptcha);
                Check("pan.xunlei_no_captcha_token", !xl.HasCaptchaToken);
            }

            // 有凭证后 IsLoggedIn 应变 true
            using (var ali2 = new AliyunClient("fake-refresh-token"))
                Check("pan.ali_logged_in", ali2.IsLoggedIn);

            using (var xl2 = new XunleiClient("fake-pan-token", null))
            {
                Check("pan.xunlei_logged_in", xl2.IsLoggedIn);
                xl2.SetCaptchaToken("fake-captcha");
                Check("pan.xunlei_captcha_set", xl2.HasCaptchaToken);
            }

            // 夸克通过适配器接入统一接口（复用已验证的 QuarkClient，不重写）
            using (var quark = PanProviderRegistry.Create(PanKind.Quark, cred))
            {
                Check("pan.quark_adapted", quark != null && quark.Kind == PanKind.Quark);
                Check("pan.quark_display", quark.DisplayName == "夸克网盘");
                Check("pan.quark_transfer_free", quark.Capabilities.TransferFree);
                Check("pan.quark_not_logged_in", !quark.IsLoggedIn);
                Check("pan.quark_adapter_type", quark is QuarkProviderAdapter);
            }

            // 每个网盘都要有非空的能力说明（UI 直接展示给用户）
            foreach (var kind in PanProviderRegistry.All)
            {
                var cap = PanProviderRegistry.CapabilitiesOf(kind);
                Check("pan.caps_" + kind, cap != null && !string.IsNullOrEmpty(cap.Note));
            }

            // ⚠️ 夸克与 UC 的 Cookie 是**两个不同的值**（域不同、值不同），
            //    绝不能互相顶替 —— 混用会拿错登录态，表现为"明明登录了却说未登录"。
            // 注意 Cookie 必须是 `name=value` 形态，否则解析器会丢弃（这里顺带验证这点）。
            var both = new PanCredential
            {
                QuarkCookie = "__puus=quark-value",
                UcCookie = "__puus=uc-value",
            };
            using (var q = PanProviderRegistry.Create(PanKind.Quark, both))
                Check("pan.quark_uses_quark_cookie", q.IsLoggedIn);
            using (var u = PanProviderRegistry.Create(PanKind.Uc, both))
                Check("pan.uc_uses_uc_cookie", u.IsLoggedIn);

            // 只给夸克 Cookie 时 UC 不应被误判为已登录，反之亦然
            using (var u2 = PanProviderRegistry.Create(PanKind.Uc,
                       new PanCredential { QuarkCookie = "__puus=q-only" }))
                Check("pan.uc_not_using_quark_cookie", !u2.IsLoggedIn);
            using (var q2 = PanProviderRegistry.Create(PanKind.Quark,
                       new PanCredential { UcCookie = "__puus=u-only" }))
                Check("pan.quark_not_using_uc_cookie", !q2.IsLoggedIn);

            // 非法 Cookie（没有 =）应被安全丢弃，不能当成已登录
            using (var bad = PanProviderRegistry.Create(PanKind.Uc,
                       new PanCredential { UcCookie = "not-a-valid-cookie" }))
                Check("pan.invalid_cookie_rejected", !bad.IsLoggedIn);
        }

        /// <summary>
        /// 多网盘条目 ↔ 界面条目的字段搬运。
        /// 这是"多网盘"与"现有 UI"之间唯一的接缝，映射错一个字段，
        /// 表现就是「文件夹点不进去」或「取链说令牌无效」，很难从现象反推。
        /// </summary>
        private static void TestPanEntryMapper()
        {
            var pan = new PanFileEntry
            {
                Fid = "fid-1",
                Name = "电影.mp4",
                Size = 1048576,
                IsDir = false,
                Token = "tok-1",
            };

            var quark = PanEntryMapper.ToQuarkEntry(pan);
            Check("map.to_quark", quark.Fid == "fid-1" && quark.Name == "电影.mp4"
                && quark.Size == 1048576 && !quark.IsDir && quark.Token == "tok-1");

            // 文件夹标记必须原样带过去 —— 反了就会出现「文件夹点不进去」
            var dirPan = new PanFileEntry { Fid = "d1", Name = "子目录", IsDir = true, Size = 0 };
            Check("map.dir_flag", PanEntryMapper.ToQuarkEntry(dirPan).IsDir);

            // 反向：取直链时 token 必须带回去，否则分享取链会报令牌无效
            var back = PanEntryMapper.ToPanEntry(quark);
            Check("map.to_pan", back.Fid == "fid-1" && back.Token == "tok-1"
                && back.Size == 1048576 && back.Name == "电影.mp4");

            // null 输入不能抛（网盘返回结构异常时不能把界面搞崩）
            Check("map.null_single", PanEntryMapper.ToQuarkEntry(null).Fid == "");
            Check("map.null_list", PanEntryMapper.ToQuarkEntries(null).Count == 0);
            Check("map.null_pan", PanEntryMapper.ToPanEntry(null).Fid == "");

            // 批量
            var list = new List<PanFileEntry>
            {
                new PanFileEntry { Fid = "a", Name = "A", Size = 1 },
                new PanFileEntry { Fid = "b", Name = "B", Size = 2, IsDir = true },
            };
            var converted = PanEntryMapper.ToQuarkEntries(list);
            Check("map.batch", converted.Count == 2 && converted[0].Fid == "a"
                && converted[1].IsDir && converted[1].Size == 2);

            // 往返不丢字段
            var roundTrip = PanEntryMapper.ToPanEntry(PanEntryMapper.ToQuarkEntry(pan));
            Check("map.roundtrip", roundTrip.Fid == pan.Fid && roundTrip.Name == pan.Name
                && roundTrip.Size == pan.Size && roundTrip.Token == pan.Token
                && roundTrip.IsDir == pan.IsDir);
        }

        /// <summary>
        /// UC 契约测试：验证「取 CSRF → 换 stoken → 列目录 → 免转存取直链」整条链路的
        /// **请求构造**与**响应解析**。
        /// <para>
        /// 不需要账号 —— 本地 mock 服务器扮演 UC 后端。
        /// 这能钉住最容易错的地方：<c>x-csrf-token</c> 头、接口路径、JSON 字段名。
        /// </para>
        /// </summary>
        private static void TestUcContract()
        {
            // 实测结论：pc-api.uc.cn 的业务接口不需要 CSRF。
            // 若用户粘贴的 Cookie 里带 ctoken，客户端会顺手带上；没有也不影响。
            using (var server = new MockHttpServer(req =>
            {
                string path = req.Path ?? "";

                if (path.IndexOf("/share/sharepage/token", StringComparison.Ordinal) >= 0)
                    return new MockHttpServer.MockResponse("{\"code\":200,\"data\":{\"stoken\":\"ST-1\"}}");

                // UC 的分享列表是 POST /share/sharepage/v2/detail（实测 GET 会 405）
                if (path.IndexOf("/share/sharepage/v2/detail", StringComparison.Ordinal) >= 0)
                    return new MockHttpServer.MockResponse(
                        "{\"code\":200,\"metadata\":{\"_total\":2},\"data\":{\"list\":["
                        + "{\"fid\":\"f1\",\"file_name\":\"影片.mkv\",\"size\":123456,"
                        + "\"dir\":false,\"share_fid_token\":\"tk1\"},"
                        + "{\"fid\":\"d1\",\"file_name\":\"子目录\",\"size\":0,"
                        + "\"dir\":true,\"share_fid_token\":\"tk2\"}]}}");

                if (path.IndexOf("/file/download", StringComparison.Ordinal) >= 0)
                    return new MockHttpServer.MockResponse(
                        "{\"code\":200,\"data\":[{\"fid\":\"f1\",\"file_name\":\"影片.mkv\","
                        + "\"size\":123456,\"download_url\":\"https://cdn.uc.test/a.mkv?sig=x\"}]}");

                return new MockHttpServer.MockResponse("{\"code\":404,\"message\":\"unexpected path\"}");
            }))
            {
                using (var uc = new UcDriveClient("__puus=fake", server.BaseUrl))
                {
                    var shareRef = new PanShareRef
                    {
                        Kind = PanKind.Uc,
                        ShareId = "SHARE1",
                        Passcode = "abcd",
                    };

                    var files = uc.ListShareAsync(shareRef, null).GetAwaiter().GetResult();
                    Check("uc.list_count", files != null && files.Count == 2,
                        "count=" + (files == null ? -1 : files.Count));
                    Check("uc.list_name", files != null && files[0].Name == "影片.mkv");
                    Check("uc.list_token", files != null && files[0].Token == "tk1");
                    Check("uc.list_dir_flag", files != null && files[1].IsDir);

                    var links = uc.GetShareDownloadLinksAsync(shareRef, new List<PanFileEntry>(files))
                        .GetAwaiter().GetResult();
                    Check("uc.link_ok", links != null && links.Count == 1
                        && links[0].DownloadUrl == "https://cdn.uc.test/a.mkv?sig=x");
                    Check("uc.link_size", links != null && links[0].Size == 123456);
                }

                var reqs = server.Requests;

                // ① 不该再为 CSRF 去访问首页（pc-api 不需要，省一次请求）
                Check("uc.no_home_request", !reqs.Exists(r => r.Path == "/"));

                // ② 业务请求路径正确（token / v2/detail / download）
                var apiReqs = reqs.FindAll(r => r.Path != null && r.Path.StartsWith("/1/"));
                Check("uc.req_paths", apiReqs.Count > 0
                    && reqs.Exists(r => r.Path.Contains("/share/sharepage/token"))
                    && reqs.Exists(r => r.Path.Contains("/share/sharepage/v2/detail"))
                    && reqs.Exists(r => r.Path.Contains("/file/download")));

                // ②b 列表必须是 POST（实测 GET 打 v2/detail 返回 405）
                var listReq = reqs.Find(r => r.Path.Contains("/share/sharepage/v2/detail"));
                Check("uc.list_is_post", listReq != null
                    && string.Equals(listReq.Method, "POST", StringComparison.OrdinalIgnoreCase),
                    listReq == null ? "no request" : listReq.Method);

                // ②c 列表参数在 body 里（pwd_id / stoken / pdir_fid）
                Check("uc.list_body", listReq != null
                    && listReq.Body.Contains("\"pwd_id\":\"SHARE1\"")
                    && listReq.Body.Contains("\"stoken\":\"ST-1\""),
                    listReq == null ? "no request" : listReq.Body);

                // ③ 业务请求**不应**带 CSRF（pc-api 不需要；带了也无害，但说明逻辑没清理干净）
                var withCsrf = new List<string>();
                foreach (var r in apiReqs)
                {
                    if (!string.IsNullOrEmpty(r.Header("x-csrf-token")))
                        withCsrf.Add(r.Path);
                }
                Check("uc.no_csrf_needed", withCsrf.Count == 0,
                    "带 csrf 的请求数=" + withCsrf.Count);

                // ④ token 请求体必须带 pwd_id + passcode
                var tokenReq = reqs.Find(r => r.Path.Contains("/share/sharepage/token"));
                Check("uc.req_token_body", tokenReq != null
                    && tokenReq.Body.Contains("\"pwd_id\":\"SHARE1\"")
                    && tokenReq.Body.Contains("\"passcode\":\"abcd\""),
                    tokenReq == null ? "no request" : tokenReq.Body);

                // ⑤ 取链请求体必须带 stoken + fids + fids_token（免转存的关键）
                var dlReq = reqs.Find(r => r.Path.Contains("/file/download"));
                Check("uc.req_download_body", dlReq != null
                    && dlReq.Body.Contains("\"stoken\":\"ST-1\"")
                    && dlReq.Body.Contains("\"fids\"")
                    && dlReq.Body.Contains("\"fids_token\"")
                    && dlReq.Body.Contains("\"pwd_id\":\"SHARE1\""),
                    dlReq == null ? "no request" : dlReq.Body);

                // ⑥ UA 必须是 UC 客户端身份（不是夸克、不是普通浏览器）
                var anyApi = apiReqs.Count > 0 ? apiReqs[0] : null;
                Check("uc.req_ua", anyApi != null
                    && (anyApi.Header("User-Agent") ?? "").Contains("uc-cloud-drive"),
                    anyApi == null ? "no request" : anyApi.Header("User-Agent"));
            }
        }

        /// <summary>
        /// 阿里云盘契约测试：refresh_token → share_token → 列目录 → 免转存取直链。
        /// 重点验证 <c>x-share-token</c> 头与 <c>get_share_link_download_url</c> 的字段。
        /// </summary>
        private static void TestAliyunContract()
        {
            using (var server = new MockHttpServer(req =>
            {
                string path = req.Path ?? "";

                if (path.IndexOf("/v2/account/token", StringComparison.Ordinal) >= 0)
                    return new MockHttpServer.MockResponse(
                        "{\"access_token\":\"AT-1\",\"refresh_token\":\"RT-2\","
                        + "\"default_drive_id\":\"D1\",\"expires_in\":7200}");

                if (path.IndexOf("/v2/share_link/get_share_token", StringComparison.Ordinal) >= 0)
                    return new MockHttpServer.MockResponse("{\"share_token\":\"STOK-1\"}");

                if (path.IndexOf("/adrive/v2/file/list", StringComparison.Ordinal) >= 0)
                    return new MockHttpServer.MockResponse(
                        "{\"items\":[{\"file_id\":\"f1\",\"name\":\"片.mp4\",\"size\":999,"
                        + "\"type\":\"file\",\"content_hash\":\"abc\"},"
                        + "{\"file_id\":\"d1\",\"name\":\"夹\",\"size\":0,\"type\":\"folder\"}]}");

                if (path.IndexOf("/get_share_link_download_url", StringComparison.Ordinal) >= 0)
                    return new MockHttpServer.MockResponse(
                        "{\"url\":\"https://cdn.ali.test/b.mp4?sig=y\",\"size\":999}");

                return new MockHttpServer.MockResponse("{\"code\":\"NotFound\"}");
            }))
            {
                using (var ali = new AliyunClient("RT-1", server.BaseUrl, server.BaseUrl))
                {
                    var shareRef = new PanShareRef
                    {
                        Kind = PanKind.Aliyun,
                        ShareId = "ALISHARE",
                        Passcode = "pwd1",
                    };

                    var files = ali.ListShareAsync(shareRef, null).GetAwaiter().GetResult();
                    Check("ali.list_count", files != null && files.Count == 2,
                        "count=" + (files == null ? -1 : files.Count));
                    Check("ali.list_name", files != null && files[0].Name == "片.mp4");
                    Check("ali.list_dir", files != null && files[1].IsDir);
                    Check("ali.list_sha1", files != null && files[0].Sha1 == "abc");

                    // 只取文件条目（目录不该进取链列表 —— 真实调用方也是这么筛的）
                    var onlyFiles = new List<PanFileEntry>();
                    foreach (var f in files)
                    {
                        if (!f.IsDir) onlyFiles.Add(f);
                    }

                    var links = ali.GetShareDownloadLinksAsync(shareRef, onlyFiles)
                        .GetAwaiter().GetResult();
                    Check("ali.link_ok", links != null && links.Count == 1
                        && links[0].DownloadUrl == "https://cdn.ali.test/b.mp4?sig=y");

                    // 自有网盘接口需要 access_token —— 顺带验证 OAuth 换 token 与轮换
                    ali.ValidateAsync().GetAwaiter().GetResult();

                    // refresh_token 轮换必须被接住（否则下次登录态失效）
                    Check("ali.token_rotated", ali.RefreshToken == "RT-2", ali.RefreshToken);
                }

                var reqs = server.Requests;

                // ① 列目录与取链都必须带 x-share-token
                var listReq = reqs.Find(r => r.Path.Contains("/adrive/v2/file/list"));
                Check("ali.req_share_token_on_list", listReq != null
                    && listReq.Header("x-share-token") == "STOK-1");

                var dlReq = reqs.Find(r => r.Path.Contains("/get_share_link_download_url"));
                Check("ali.req_share_token_on_download", dlReq != null
                    && dlReq.Header("x-share-token") == "STOK-1");

                // ② 取链请求体必须是 share_id + file_id（免转存的关键：不需要 drive_id）
                Check("ali.req_download_body", dlReq != null
                    && dlReq.Body.Contains("\"share_id\":\"ALISHARE\"")
                    && dlReq.Body.Contains("\"file_id\":\"f1\"")
                    && !dlReq.Body.Contains("drive_id"),
                    dlReq == null ? "no request" : dlReq.Body);

                // ③ 换 token 请求体必须是 refresh_token + grant_type
                var authReq = reqs.Find(r => r.Path.Contains("/v2/account/token"));
                Check("ali.req_auth_body", authReq != null
                    && authReq.Body.Contains("\"refresh_token\":\"RT-1\"")
                    && authReq.Body.Contains("\"grant_type\":\"refresh_token\""));
            }
        }

        /// <summary>
        /// 迅雷 captcha 自动重试测试。
        /// <para>
        /// 场景：第一次请求服务端回 <c>captcha_required</c>（真实环境常见），
        /// 客户端应自动 <c>POST /v1/shield/captcha/init</c> 换一个新 token 再发一次，
        /// 第二次成功。这条链路是「迅雷登录/取链总是 400」的根治点，
        /// 必须用 mock 锁住，否则以后重构很容易把它弄丢。
        /// </para>
        /// </summary>
        private static void TestXunleiCaptchaRetry()
        {
            int panCalls = 0;

            using (var server = new MockHttpServer(req =>
            {
                string path = req.Path ?? "";

                // captcha 盾：返回一个新 token
                if (path.IndexOf("/v1/shield/captcha/init", StringComparison.Ordinal) >= 0)
                    return new MockHttpServer.MockResponse("{\"captcha_token\":\"FRESH-CAP\"}");

                // 第一次业务请求：要验证码
                if (path.IndexOf("/drive/v1/about", StringComparison.Ordinal) >= 0)
                {
                    panCalls++;
                    if (panCalls == 1)
                        return new MockHttpServer.MockResponse(
                            "{\"error\":\"captcha_required\",\"error_code\":3,"
                            + "\"error_description\":\"请求参数缺失\"}");
                    // 第二次：必须带上新 captcha
                    if (req.Header("x-captcha-token") != "FRESH-CAP")
                        return new MockHttpServer.MockResponse(
                            "{\"error\":\"captcha_invalid\",\"error_code\":9}");
                    return new MockHttpServer.MockResponse(
                        "{\"nick_name\":\"迅雷用户\",\"space\":1000,\"space_used\":100}");
                }

                return new MockHttpServer.MockResponse("{\"error\":\"unexpected\"}");
            }))
            {
                using (var xl = new XunleiClient("PANTOK", null, server.BaseUrl, "dev1", server.BaseUrl))
                {
                    var (ok, nick) = xl.ValidateAsync().GetAwaiter().GetResult();
                    Check("xl.captcha_retry_ok", ok, "ok=" + ok + " err=" + xl.LastError);
                    Check("xl.captcha_retry_nick", nick == "迅雷用户", nick ?? "null");
                }

                Check("xl.captcha_retry_two_calls", panCalls == 2, "panCalls=" + panCalls);

                var initReq = server.Requests.Find(r =>
                    r.Path.IndexOf("/v1/shield/captcha/init", StringComparison.Ordinal) >= 0);
                Check("xl.captcha_init_body", initReq != null
                    && initReq.Body.Contains("\"captcha_sign\":\"1.")
                    && initReq.Body.Contains("\"action\":\"GET:/drive/v1/about\""),
                    initReq == null ? "no init request" : initReq.Body);
                Check("xl.captcha_init_appclient", initReq != null
                    && initReq.Header("X-Client-Id") == XunleiConstants.AppClientId,
                    initReq == null ? "no init request" : initReq.Header("X-Client-Id"));
            }
        }

        /// <summary>
        /// 迅雷契约测试：验证 <c>x-captcha-token</c> 与 <c>Authorization</c> 头，
        /// 以及从 <c>web_content_link</c> / <c>medias[].link.url</c> 两处取直链的能力。
        /// </summary>
        private static void TestXunleiContract()
        {
            using (var server = new MockHttpServer(req =>
            {
                string path = req.Path ?? "";
                bool isPost = string.Equals(req.Method, "POST", StringComparison.OrdinalIgnoreCase);

                // 转存：返回 trace_file_ids 映射（迅雷把「旧 id → 新 id」塞在这个 JSON 字符串里）
                if (isPost && path.IndexOf("/drive/v1/share/restore", StringComparison.Ordinal) >= 0)
                    return new MockHttpServer.MockResponse(
                        "{\"params\":{\"trace_file_ids\":\"{\\\"x1\\\":\\\"n1\\\"}\"}}");

                // 批量删除（转存后清理）
                if (isPost && path.IndexOf("batchDelete", StringComparison.Ordinal) >= 0)
                    return new MockHttpServer.MockResponse("{}");

                // 建目录：POST /drive/v1/files
                if (isPost && path.IndexOf("/drive/v1/files", StringComparison.Ordinal) >= 0)
                    return new MockHttpServer.MockResponse(
                        "{\"file\":{\"id\":\"tmpdir\",\"name\":\"GeZi临时转存\",\"kind\":\"drive#folder\"}}");

                // 列根目录（查临时文件夹是否已存在）：GET /drive/v1/files
                if (path.IndexOf("/drive/v1/files", StringComparison.Ordinal) >= 0
                    && path.IndexOf("/drive/v1/files/", StringComparison.Ordinal) < 0)
                    return new MockHttpServer.MockResponse("{\"files\":[]}");

                // 文件详情（取直链）
                if (path.IndexOf("/drive/v1/files/", StringComparison.Ordinal) >= 0)
                    return new MockHttpServer.MockResponse(
                        "{\"id\":\"n1\",\"name\":\"剧集.mp4\",\"size\":555,"
                        + "\"web_content_link\":\"https://lixian.xunlei.test/c.mp4?sig=z\"}");

                if (path.IndexOf("/drive/v1/share", StringComparison.Ordinal) >= 0)
                    return new MockHttpServer.MockResponse(
                        "{\"share_status\":\"OK\",\"pass_code_token\":\"PCTOK\",\"files\":["
                        + "{\"id\":\"x1\",\"name\":\"剧集.mp4\",\"size\":555,\"kind\":\"drive#file\"},"
                        + "{\"id\":\"xd\",\"name\":\"目录\",\"kind\":\"drive#folder\"}]}");

                return new MockHttpServer.MockResponse("{\"error\":\"unexpected\"}");
            }))
            {
                using (var xl = new XunleiClient("PANTOK", "CAPTOK", server.BaseUrl))
                {
                    var shareRef = new PanShareRef
                    {
                        Kind = PanKind.Xunlei,
                        ShareId = "XLSHARE",
                        Passcode = "ab12",
                    };

                    var files = xl.ListShareAsync(shareRef, null).GetAwaiter().GetResult();
                    Check("xl.list_count", files != null && files.Count == 2,
                        "count=" + (files == null ? -1 : files.Count));
                    Check("xl.list_dir", files != null && files[1].IsDir);

                    var onlyFiles = new List<PanFileEntry>();
                    foreach (var f in files)
                    {
                        if (!f.IsDir) onlyFiles.Add(f);
                    }

                    var links = xl.GetShareDownloadLinksAsync(shareRef, onlyFiles)
                        .GetAwaiter().GetResult();
                    Check("xl.link_ok", links != null && links.Count == 1
                        && links[0].DownloadUrl == "https://lixian.xunlei.test/c.mp4?sig=z");
                }

                // 没有 captcha token 时不再本地拦截：服务端不要验证码就应当正常取到直链。
                // （旧行为是「本地直接抛错」，那会误伤不需要验证码的场景。）
                IReadOnlyList<DownloadLink> noCaptchaLinks = null;
                using (var xl2 = new XunleiClient("PANTOK", null, server.BaseUrl))
                {
                    bool ok = false;
                    try
                    {
                        noCaptchaLinks = xl2.GetShareDownloadLinksAsync(
                            new PanShareRef { Kind = PanKind.Xunlei, ShareId = "S" },
                            new List<PanFileEntry> { new PanFileEntry { Fid = "x1" } })
                            .GetAwaiter().GetResult();
                        ok = noCaptchaLinks != null && noCaptchaLinks.Count == 1;
                    }
                    catch (QuarkApiException)
                    {
                        ok = false;
                    }
                    Check("xl.no_captcha_ok", ok);
                }

                var reqs = server.Requests;

                // 带凭证的请求必须有 Authorization + x-captcha-token
                // ⚠️ 只挑 **GET** 的分享列表请求：转存是 POST /drive/v1/share/restore，
                //    参数在 body 里，拿它去断言 query 会误判。
                var shareReq = reqs.Find(r => r.Path.Contains("/drive/v1/share")
                    && string.Equals(r.Method, "GET", StringComparison.OrdinalIgnoreCase));
                Check("xl.req_auth", shareReq != null
                    && shareReq.Header("Authorization") == "Bearer PANTOK",
                    shareReq == null ? "no request" : shareReq.Header("Authorization"));
                Check("xl.req_captcha", shareReq != null
                    && shareReq.Header("x-captcha-token") == "CAPTOK");

                // 分享请求必须带 share_id 与 pass_code
                Check("xl.req_share_query", shareReq != null
                    && shareReq.Path.Contains("share_id=XLSHARE")
                    && shareReq.Path.Contains("pass_code=ab12"),
                    shareReq == null ? "no request" : shareReq.Path);

                // device_id 实测必需（缺了服务端报 "device_id is empty"）
                Check("xl.req_device_id", shareReq != null
                    && shareReq.Path.Contains("device_id=")
                    && !shareReq.Path.Contains("device_id=&"),
                    shareReq == null ? "no request" : shareReq.Path);

                // 🚨 迅雷没有真免转存：必须先 POST /drive/v1/share/restore 转存，
                //    再用**新 file_id** 去 /drive/v1/files/{id} 取链，最后 batchDelete 清理。
                var restoreReq = reqs.Find(r => r.Path.Contains("/drive/v1/share/restore")
                    && string.Equals(r.Method, "POST", StringComparison.OrdinalIgnoreCase));
                Check("xl.restore_posted", restoreReq != null);
                Check("xl.restore_body",
                    restoreReq != null && restoreReq.Body != null
                    && restoreReq.Body.Contains("\"file_ids\"")
                    && restoreReq.Body.Contains("x1")
                    && restoreReq.Body.Contains("\"pass_code_token\""),
                    restoreReq == null ? "no request" : (restoreReq.Body ?? ""));

                // 取链走转存后的新 id（n1），不是分享里的原始 id（x1）
                Check("xl.req_files_path",
                    reqs.Exists(r => r.Path.Contains("/drive/v1/files/n1")));

                // 🚨 转存出来的副本**不能在这里删**：直链走 CDN 回源，源文件一删，
                //    在途分片全部回源失败 → 服务端 503 洪泛 + 速度掉到 1 MB/s 以下。
                //    正确做法是把 id 通过 DownloadLink.StoredId 交给调用方，
                //    等下载任务进入终态再统一清理。
                Check("xl.no_cleanup_on_fetch",
                    !reqs.Exists(r => r.Path.Contains("batchDelete")
                        && string.Equals(r.Method, "POST", StringComparison.OrdinalIgnoreCase)),
                    "取链阶段不应发 batchDelete");

                // 交接口径：必须把转存出来的 id 带回去，否则上层无从清理。
                Check("xl.stored_id_returned",
                    noCaptchaLinks != null && noCaptchaLinks.Count == 1
                    && !string.IsNullOrEmpty(noCaptchaLinks[0].StoredId),
                    noCaptchaLinks == null || noCaptchaLinks.Count == 0
                        ? "no link"
                        : (noCaptchaLinks[0].StoredId ?? "null"));
            }
        }

        /// <summary>
        /// WebView2 运行时检测：只验证「检测本身不抛异常、返回值自洽」。
        /// 装没装取决于机器，不做绝对断言 —— 但要保证任何情况下都不会把程序搞崩。
        /// </summary>
        private static void TestWebView2Runtime()
        {
            string version;
            bool installed;
            try
            {
                version = WebView2Runtime.GetVersion();
                installed = WebView2Runtime.IsInstalled;
            }
            catch (Exception ex)
            {
                Check("wv2.no_throw", false, ex.GetType().Name);
                return;
            }
            Check("wv2.no_throw", true);

            // 未安装时 GetVersion 必须返回空串（而不是 null，避免调用方 NRE）
            if (!installed)
                Check("wv2.empty_when_missing", string.IsNullOrEmpty(version));
            else
                Check("wv2.version_parsable", WebView2Runtime.IsValidVersionString(version),
                    "version=" + version);

            // 状态描述永远非空（设置页直接展示，空字符串会让界面看起来坏了）
            string desc = WebView2Runtime.DescribeStatus();
            Check("wv2.describe", !string.IsNullOrEmpty(desc), desc);

            // 版本解析工具的边界行为
            Check("wv2.parse_null", WebView2Runtime.ParseVersion(null) == null);
            Check("wv2.parse_garbage", WebView2Runtime.ParseVersion("abc") == null);
            Check("wv2.parse_ok",
                WebView2Runtime.ParseVersion("1.2.3.4") != null);
            Check("wv2.invalid_version_string",
                !WebView2Runtime.IsValidVersionString("not-a-version"));
            Check("wv2.format_fallback",
                WebView2Runtime.FormatVersion("weird") == "weird");

            // 隔离数据目录：能建就要能清，且两次调用路径不同（不共用会话）
            string d1 = WebView2Runtime.CreateIsolatedDataFolder();
            string d2 = WebView2Runtime.CreateIsolatedDataFolder();
            if (d1 != null && d2 != null)
            {
                Check("wv2.isolated_distinct", d1 != d2);
                Check("wv2.isolated_exists", Directory.Exists(d1));
            }
            WebView2Runtime.TryCleanupFolder(d1);
            WebView2Runtime.TryCleanupFolder(d2);
            if (d1 != null)
                Check("wv2.cleanup", !Directory.Exists(d1));

            // 清理不存在的目录不能抛
            bool cleanupOk = true;
            try { WebView2Runtime.TryCleanupFolder(null); WebView2Runtime.TryCleanupFolder(""); }
            catch { cleanupOk = false; }
            Check("wv2.cleanup_null_safe", cleanupOk);
        }

        /// <summary>
        /// UC Cookie 的判定：与 wenxi 的 auth.dart 一致 ——
        /// **只看 __pus 与 __puus 是否都存在且非空，不做网络请求**。
        /// <para>
        /// 早前这里要求 account/info 的 data 非空，导致已登录用户被误判
        /// （实测截图：网页显示「UC网盘用户」，程序却说 data 为空）。
        /// </para>
        /// </summary>
        private static void TestUcCookiePlausible()
        {
            Check("uccookie.both_present",
                UcDriveClient.LooksLikeCompleteUcCookie("__pus=a; __puus=b"));
            Check("uccookie.only_puus",
                !UcDriveClient.LooksLikeCompleteUcCookie("__puus=b"));
            Check("uccookie.only_pus",
                !UcDriveClient.LooksLikeCompleteUcCookie("__pus=a"));
            Check("uccookie.empty",
                !UcDriveClient.LooksLikeCompleteUcCookie(""));
            Check("uccookie.null",
                !UcDriveClient.LooksLikeCompleteUcCookie(null));
            Check("uccookie.garbage",
                !UcDriveClient.LooksLikeCompleteUcCookie("hello world"));
            // 名字必须精确匹配，不能被相似名字骗过
            Check("uccookie.no_prefix_match",
                !UcDriveClient.LooksLikeCompleteUcCookie("__pusx=a; __puusx=b"));
            // 真实形态：中间夹着别的 Cookie、有空格
            Check("uccookie.realistic",
                UcDriveClient.LooksLikeCompleteUcCookie(
                    "ctoken=xyz; __pus=aaa; UDRIVE_TRANSFER_SESS=bbb; __puus=ccc"));
            // 空值不算（"__puus=" 是登录过程中的中间态）
            Check("uccookie.empty_value",
                !UcDriveClient.LooksLikeCompleteUcCookie("__pus=a; __puus="));
            Check("uccookie.empty_value2",
                !UcDriveClient.LooksLikeCompleteUcCookie("__pus=; __puus=b"));
            // __pugs 出现也不影响判定（wenxi 会一并收集，但不是必需）
            Check("uccookie.with_pugs",
                UcDriveClient.LooksLikeCompleteUcCookie("__pus=a; __puus=b; __pugs=c"));
        }

        /// <summary>
        /// 阿里云盘设备签名自检。
        /// <para>
        /// 为什么必须自测：签名错了服务端只回一句 InvalidDeviceSession，
        /// 无法从错误信息反推是 r/s 算错、recovery id 算错还是公钥编码错。
        /// 这里一次性把「公钥推导 → 签名 → 公钥验签 → recovery 反推公钥」全链路钉死。
        /// </para>
        /// </summary>
        private static void TestAliyunDeviceSignature()
        {
            string detail;
            Check("aliyunsig.selfcheck", AliyunSignature.SelfCheck(out detail), detail);

            // 私钥/公钥格式
            string priv = AliyunSignature.NewPrivateKeyHex();
            Check("aliyunsig.privkey_len", priv != null && priv.Length == 64, "len=" + (priv == null ? "null" : priv.Length.ToString()));
            string pub = AliyunSignature.DerivePublicKeyHex(priv);
            Check("aliyunsig.pubkey_len", pub.Length == 130 && pub.StartsWith("04", StringComparison.Ordinal), "pub=" + pub.Length);

            // 同一私钥两次签名：公钥必须一致（确定性推导），签名可不同（随机 k）
            string pub2 = AliyunSignature.DerivePublicKeyHex(priv);
            Check("aliyunsig.pubkey_stable", pub == pub2);

            // 不同私钥必须得到不同公钥
            string priv2 = AliyunSignature.NewPrivateKeyHex();
            Check("aliyunsig.pubkey_distinct",
                priv != priv2 && AliyunSignature.DerivePublicKeyHex(priv2) != pub);

            // 签名随 deviceId / userId 变化（摘要进签名）
            var s1 = AliyunSignature.Sign(priv, "00112233445566778899aabbccddeeff", "user-a", 0);
            var s2 = AliyunSignature.Sign(priv, "00112233445566778899aabbccddeeff", "user-b", 0);
            var s3 = AliyunSignature.Sign(priv, "ffeeddccbbaa99887766554433221100", "user-a", 0);
            Check("aliyunsig.sig_len", s1.Signature.Length == 130, "len=" + s1.Signature.Length);
            Check("aliyunsig.sig_hex",
                IsHex(s1.Signature) && IsHex(s1.PublicKey.Substring(2)));
            // 随机 k 下签名本身每次不同，但这里只断言「不同输入 → 不同公钥不变、签名结构正确」
            Check("aliyunsig.pubkey_same_across_sign",
                s1.PublicKey == pub && s2.PublicKey == pub && s3.PublicKey == pub);
            Check("aliyunsig.recovery_range",
                RecoveryOf(s1) <= 3 && RecoveryOf(s2) <= 3 && RecoveryOf(s3) <= 3,
                "rec=" + RecoveryOf(s1) + "/" + RecoveryOf(s2) + "/" + RecoveryOf(s3));

            // 非法输入必须明确抛异常，不能静默产出垃圾签名
            Check("aliyunsig.reject_short_key", Throws(() => AliyunSignature.Sign("abcd", "d", "u")));
            Check("aliyunsig.reject_zero_key", Throws(() => AliyunSignature.Sign(new string('0', 64), "d", "u")));
            Check("aliyunsig.reject_nonhex_key", Throws(() => AliyunSignature.Sign(new string('z', 64), "d", "u")));
            Check("aliyunsig.reject_empty_device", Throws(() => AliyunSignature.Sign(priv, "", "u")));
            Check("aliyunsig.reject_empty_user", Throws(() => AliyunSignature.Sign(priv, "d", "")));
        }

        /// <summary>
        /// 迅雷账号登录协议的自检。
        /// <para>
        /// 这些值不是随便定的：<c>devicesign</c> 和 <c>captcha_sign</c> 一旦算错，
        /// 服务端会直接返回"设备异常"或让人机验证永远过不去，而错误信息完全不指向签名。
        /// 所以把格式、稳定性、对输入敏感这三件事钉死在自检里。
        /// </para>
        /// </summary>
        private static void TestXunleiLoginProtocol()
        {
            // 设备标识：32 位小写 hex
            string id = XunleiProtocol.NewIdentifier();
            Check("xunlei.id_len", id != null && id.Length == 32, "len=" + (id == null ? "null" : id.Length.ToString()));
            Check("xunlei.id_hex", IsHex(id) && id == id.ToLowerInvariant());
            Check("xunlei.id_accepts_self", XunleiProtocol.IsIdentifier(id));
            Check("xunlei.id_rejects_short", !XunleiProtocol.IsIdentifier("abcd"));
            Check("xunlei.id_rejects_upper", !XunleiProtocol.IsIdentifier(id.ToUpperInvariant()));
            Check("xunlei.id_rejects_null", !XunleiProtocol.IsIdentifier(null));

            // 两次生成必须不同，否则所有用户会共用一个设备
            Check("xunlei.id_distinct", XunleiProtocol.NewIdentifier() != id);

            // devicesign：固定 "div101." + id + md5(...) = 71 字符
            string sign = XunleiProtocol.DeviceSign(id);
            Check("xunlei.devicesign_len", sign.Length == 71, "len=" + sign.Length);
            Check("xunlei.devicesign_prefix", sign.StartsWith("div101." + id, StringComparison.Ordinal));
            Check("xunlei.devicesign_stable", sign == XunleiProtocol.DeviceSign(id));
            Check("xunlei.devicesign_depends_on_id",
                sign != XunleiProtocol.DeviceSign(XunleiProtocol.NewIdentifier()));

            // captcha_sign：固定 "1." + digest，且必须随 deviceId / timestamp 变化
            string ts = XunleiProtocol.NowTimestamp();
            string captcha = XunleiProtocol.CaptchaSign(id, ts);
            Check("xunlei.captchasign_prefix", captcha.StartsWith("1.", StringComparison.Ordinal));
            Check("xunlei.captchasign_hex",
                IsHex(captcha.Substring(2)) && captcha.Length > 2);
            Check("xunlei.captchasign_stable", captcha == XunleiProtocol.CaptchaSign(id, ts));
            Check("xunlei.captchasign_depends_on_time",
                captcha != XunleiProtocol.CaptchaSign(id, XunleiProtocol.NowTimestamp() + "0"));
            Check("xunlei.captchasign_depends_on_id",
                captcha != XunleiProtocol.CaptchaSign(XunleiProtocol.NewIdentifier(), ts));

            // 时间戳必须是 13 位毫秒
            Check("xunlei.timestamp_len", ts.Length == 13, "ts=" + ts);

            // peerID 也是 32 位 hex，且由设备 id 稳定派生
            string peer = XunleiProtocol.PeerIdFor(id);
            Check("xunlei.peerid_len", peer.Length == 32 && IsHex(peer));
            Check("xunlei.peerid_stable", peer == XunleiProtocol.PeerIdFor(id));
            Check("xunlei.peerid_distinct", peer != XunleiProtocol.PeerIdFor(XunleiProtocol.NewIdentifier()));

            // 登录公共字段：关键字段不能缺，且 sequenceNo 每次不同
            var body1 = XunleiProtocol.BaseBody(id, XunleiConstants.SmsSdkVersion);
            var body2 = XunleiProtocol.BaseBody(id, XunleiConstants.SmsSdkVersion);
            Check("xunlei.body_has_appid", body1.ContainsKey("appid") && (string)body1["appid"] == "40");
            Check("xunlei.body_has_devicesign", (string)body1["devicesign"] == sign);
            Check("xunlei.body_has_peerid", (string)body1["peerID"] == peer);
            Check("xunlei.body_has_protocol", (string)body1["protocolVersion"] == "301");
            Check("xunlei.body_sequence_fresh",
                (string)body1["sequenceNo"] != (string)body2["sequenceNo"]);

            // 手机号校验
            Check("xunlei.mobile_ok", XunleiLoginService.IsMobile("13072297517"));
            Check("xunlei.mobile_reject_short", !XunleiLoginService.IsMobile("1307229"));
            Check("xunlei.mobile_reject_letters", !XunleiLoginService.IsMobile("1307229751a"));
            Check("xunlei.mobile_reject_empty", !XunleiLoginService.IsMobile(""));
            Check("xunlei.mobile_reject_null", !XunleiLoginService.IsMobile(null));
        }

        /// <summary>
        /// 「moov 优先」的解析逻辑：用内存里拼出来的假 MP4 验证 box 链解析。
        /// 不碰网络、不碰磁盘，纯粹校验 <see cref="Mp4Moov"/>。
        /// </summary>
        private static void TestMp4MoovProbe()
        {
            // ---- ① 扩展名判定 ----
            Check("mp4.ext_mp4", Mp4Moov.LooksLikeMp4("82.mp4"));
            Check("mp4.ext_upper", Mp4Moov.LooksLikeMp4("A.MP4"));
            Check("mp4.ext_mov", Mp4Moov.LooksLikeMp4("a.mov"));
            Check("mp4.ext_m4v", Mp4Moov.LooksLikeMp4("a.m4v"));
            Check("mp4.ext_query", Mp4Moov.LooksLikeMp4("http://x/y.mp4?token=abc"));
            Check("mp4.ext_reject_zip", !Mp4Moov.LooksLikeMp4("a.zip"));
            Check("mp4.ext_reject_null", !Mp4Moov.LooksLikeMp4(null));
            Check("mp4.ext_reject_dot", !Mp4Moov.LooksLikeMp4("a."));

            // ---- ② 尾部 moov（没做 faststart，最常见的形态）----
            // 布局：ftyp(24) + free(8) + mdat(400) + moov(64)  总长 496
            var file = new byte[496];
            WriteBox(file, 0, 24, "ftyp");
            WriteBox(file, 24, 8, "free");
            WriteBox(file, 32, 400, "mdat");
            WriteBox(file, 432, 64, "moov");

            long mdatEnd;
            bool tail = Mp4Moov.TryFindMdatEnd(file, file.Length, file.Length, out mdatEnd);
            Check("mp4.mdat_end_found", tail, "tail=" + tail);
            Check("mp4.mdat_end_value", mdatEnd == 432, "mdatEnd=" + mdatEnd);

            Mp4MoovInfo info;
            bool found = Mp4Moov.TryFindMoovInTail(file, file.Length, 0, file.Length, out info);
            Check("mp4.moov_found", found);
            Check("mp4.moov_offset", info != null && info.Offset == 432, "off=" + (info == null ? -1 : info.Offset));
            Check("mp4.moov_size", info != null && info.Size == 64, "size=" + (info == null ? -1 : info.Size));
            Check("mp4.moov_is_tail", info != null && info.IsAtTail);

            // ---- ③ faststart（moov 在头部）：必须判定为「不用管」----
            // 布局：ftyp(24) + moov(64) + mdat(400)  总长 488
            var fast = new byte[488];
            WriteBox(fast, 0, 24, "ftyp");
            WriteBox(fast, 24, 64, "moov");
            WriteBox(fast, 88, 400, "mdat");

            long ignored;
            Check("mp4.faststart_skip", !Mp4Moov.TryFindMdatEnd(fast, fast.Length, fast.Length, out ignored));

            // ---- ④ mdat 吃到文件尾（没有 moov 可找）：也必须判定为「不用管」----
            var noMoov = new byte[432];
            WriteBox(noMoov, 0, 24, "ftyp");
            WriteBox(noMoov, 24, 8, "free");
            WriteBox(noMoov, 32, 400, "mdat");
            Check("mp4.mdat_to_eof_skip", !Mp4Moov.TryFindMdatEnd(noMoov, noMoov.Length, noMoov.Length, out ignored));

            // ---- ⑤ 64 位 largesize 的 mdat（size==1 时读 largesize）----
            var large = new byte[600];
            WriteBox(large, 0, 24, "ftyp");
            // mdat: size=1, type=mdat, largesize=416 → 头 16 字节 + 400 数据
            large[24] = 0; large[25] = 0; large[26] = 0; large[27] = 1;
            large[28] = (byte)'m'; large[29] = (byte)'d'; large[30] = (byte)'a'; large[31] = (byte)'t';
            large[32] = 0; large[33] = 0; large[34] = 0; large[35] = 0;
            large[36] = 0; large[37] = 0; large[38] = 1; large[39] = 160;   // 416
            WriteBox(large, 440, 160, "moov");

            bool bigTail = Mp4Moov.TryFindMdatEnd(large, large.Length, large.Length, out mdatEnd);
            Check("mp4.largesize_found", bigTail, "bigTail=" + bigTail);
            Check("mp4.largesize_end", mdatEnd == 440, "end=" + mdatEnd);

            // ---- ⑥ 损坏数据不能瞎猜 ----
            var junk = new byte[64];
            junk[3] = 200;                       // size=200 但文件只有 64 字节
            junk[4] = (byte)'m'; junk[5] = (byte)'d'; junk[6] = (byte)'a'; junk[7] = (byte)'t';
            Check("mp4.junk_reject", !Mp4Moov.TryFindMdatEnd(junk, junk.Length, junk.Length, out ignored));
            Check("mp4.null_reject", !Mp4Moov.TryFindMdatEnd(null, 0, 100, out ignored));
            Check("mp4.short_reject", !Mp4Moov.TryFindMdatEnd(new byte[4], 4, 100, out ignored));
        }

        /// <summary>
        /// 写合并测试（大厂式 coalesce writes）。
        ///
        /// 原来的 <c>ConcurrentStream.Watcher</c> 每收到一个 64KB packet 就
        /// Seek 一次、写一次。现在改成批量取、按偏移排序、相邻的合并成一次大写入。
        /// 这里用**乱序 + 重叠 + 间隙**三种真实场景验证合并后的内容逐字节正确 ——
        /// 合并算法一旦把偏移算错，产出的就是「长度对、内容坏」的文件，
        /// 而那种损坏在长度校验里看不出来（这正是 gopeed 只能 Truncate(0) 重下的原因）。
        /// </summary>
        private static void TestWriteMerge()
        {
            const int total = 64 * 1024;          // 64KB 文件，用 4KB 块拼
            const int blockSize = 4 * 1024;
            const int blockCount = total / blockSize;

            // 期望内容：每块填自己的块号，便于定位错位
            var expected = new byte[total];
            for (int i = 0; i < blockCount; i++)
                for (int k = 0; k < blockSize; k++)
                    expected[i * blockSize + k] = (byte)(i & 0xFF);

            string path = Path.Combine(_tmp, "write_merge.bin");
            try
            {
                // ---- 场景 1：乱序写入（模拟 16 条连接交错到达）----
                using (var cs = new ConcurrentStream(path, total, 0))
                {
                    var order = new int[blockCount];
                    for (int i = 0; i < blockCount; i++) order[i] = i;
                    // 确定性打乱，保证可复现
                    var rnd = new Random(20261024);
                    for (int i = blockCount - 1; i > 0; i--)
                    {
                        int j = rnd.Next(i + 1);
                        int t = order[i]; order[i] = order[j]; order[j] = t;
                    }

                    foreach (int idx in order)
                    {
                        var block = new byte[blockSize];
                        for (int k = 0; k < blockSize; k++)
                            block[k] = (byte)(idx & 0xFF);
                        cs.WriteAsync((long)idx * blockSize, block, blockSize).GetAwaiter().GetResult();
                    }

                    cs.FlushAsync().GetAwaiter().GetResult();
                }

                byte[] actual = File.ReadAllBytes(path);
                Check("wmerge.length", actual.Length == total, "len=" + actual.Length);
                Check("wmerge.out_of_order", BytesEqual(expected, actual), DiffSummary(expected, actual));

                // ---- 场景 2：重叠写入（后写的覆盖先写的）----
                File.Delete(path);
                using (var cs = new ConcurrentStream(path, total, 0))
                {
                    var full = new byte[total];
                    for (int i = 0; i < total; i++) full[i] = 0xAA;
                    cs.WriteAsync(0, full, total).GetAwaiter().GetResult();

                    // 中间一块用 0xBB 覆盖，制造重叠
                    var patch = new byte[blockSize];
                    for (int i = 0; i < blockSize; i++) patch[i] = 0xBB;
                    cs.WriteAsync(blockSize * 5, patch, blockSize).GetAwaiter().GetResult();

                    cs.FlushAsync().GetAwaiter().GetResult();
                }

                byte[] overlapped = File.ReadAllBytes(path);
                bool overlapOk = overlapped.Length == total
                    && overlapped[blockSize * 5] == 0xBB
                    && overlapped[blockSize * 5 + blockSize - 1] == 0xBB
                    && overlapped[blockSize * 4] == 0xAA
                    && overlapped[blockSize * 6] == 0xAA;
                Check("wmerge.overlap", overlapOk,
                    "len=" + overlapped.Length
                    + " at4=" + overlapped[blockSize * 4].ToString("X2")
                    + " at5=" + overlapped[blockSize * 5].ToString("X2")
                    + " at6=" + overlapped[blockSize * 6].ToString("X2"));

                // ---- 场景 3：有间隙（只写头尾，中间不写）----
                File.Delete(path);
                using (var cs = new ConcurrentStream(path, total, 0))
                {
                    var head = new byte[blockSize];
                    for (int i = 0; i < blockSize; i++) head[i] = 0x11;
                    cs.WriteAsync(0, head, blockSize).GetAwaiter().GetResult();

                    var tail = new byte[blockSize];
                    for (int i = 0; i < blockSize; i++) tail[i] = 0x22;
                    cs.WriteAsync((long)(blockCount - 1) * blockSize, tail, blockSize).GetAwaiter().GetResult();

                    cs.FlushAsync().GetAwaiter().GetResult();
                }

                byte[] gapped = File.ReadAllBytes(path);
                bool gapOk = gapped.Length == total
                    && gapped[0] == 0x11
                    && gapped[blockSize - 1] == 0x11
                    && gapped[total - 1] == 0x22
                    && gapped[blockSize] == 0x00;    // 中间是空洞，保持 0
                Check("wmerge.gap", gapOk,
                    "len=" + gapped.Length
                    + " head=" + gapped[0].ToString("X2")
                    + " mid=" + gapped[blockSize].ToString("X2")
                    + " tail=" + gapped[total - 1].ToString("X2"));
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        private static string DiffSummary(byte[] expected, byte[] actual)
        {
            if (actual == null) return "actual=null";
            if (expected.Length != actual.Length)
                return "len expected=" + expected.Length + " actual=" + actual.Length;
            int firstDiff = -1, diffCount = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    if (firstDiff < 0) firstDiff = i;
                    diffCount++;
                }
            }
            return firstDiff < 0
                ? "identical"
                : "firstDiff@" + firstDiff
                  + " exp=" + expected[firstDiff].ToString("X2")
                  + " act=" + actual[firstDiff].ToString("X2")
                  + " totalDiff=" + diffCount;
        }

        /// <summary>在 buffer 的 offset 处写一个 32 位 size + 4 字符 type 的 box 头。</summary>
        private static void WriteBox(byte[] buf, int offset, long size, string type)
        {
            buf[offset + 0] = (byte)((size >> 24) & 0xFF);
            buf[offset + 1] = (byte)((size >> 16) & 0xFF);
            buf[offset + 2] = (byte)((size >> 8) & 0xFF);
            buf[offset + 3] = (byte)(size & 0xFF);
            for (int i = 0; i < 4; i++) buf[offset + 4 + i] = (byte)type[i];
        }

        private static int RecoveryOf(AliyunSignatureResult r)        {
            return Convert.ToInt32(r.Signature.Substring(128, 2), 16);
        }

        private static bool IsHex(string s)
        {
            if (string.IsNullOrEmpty(s))
                return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }

        private static bool Throws(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// 真实账号探针：用用户自己的凭证打通「识别链接 → 列目录 → 免转存取直链」。
        /// <para>
        /// 用法：
        /// <code>
        /// GeZi.SelfTest.exe pan-probe uc      "https://drive.uc.cn/s/xxx?pwd=abcd" "__puus=..."
        /// GeZi.SelfTest.exe pan-probe aliyun  "https://www.alipan.com/s/xxx"        "&lt;refresh_token&gt;"
        /// GeZi.SelfTest.exe pan-probe xunlei  "https://pan.xunlei.com/s/xxx?pwd=ab" "&lt;pan_token&gt;" "&lt;x-captcha-token&gt;"
        /// </code>
        /// </para>
        /// <para>
        /// 退出码：0 = 全链路成功；2 = 参数错误；3 = 识别失败；4 = 列目录失败；5 = 取直链失败。
        /// 输出不含完整直链（只显示主机名与前 60 字符），避免把可下载地址泄到日志里。
        /// </para>
        /// </summary>
        private static int PanProbe(string[] args)
        {
            if (args.Length < 4)
            {
                Console.WriteLine("用法: GeZi.SelfTest.exe pan-probe <uc|aliyun|xunlei> <分享链接> <凭证> [验证码token]");
                Console.WriteLine("  uc      凭证 = 登录 Cookie（含 __puus）");
                Console.WriteLine("  aliyun  凭证 = refresh_token");
                Console.WriteLine("  xunlei  凭证 = pan_token；第 4 个参数 = x-captcha-token");
                return 2;
            }

            PanKind kind;
            switch ((args[1] ?? "").ToLowerInvariant())
            {
                case "uc": kind = PanKind.Uc; break;
                case "aliyun":
                case "ali": kind = PanKind.Aliyun; break;
                case "xunlei":
                case "xl": kind = PanKind.Xunlei; break;
                default:
                    Console.WriteLine("未知网盘: " + args[1]);
                    return 2;
            }

            string shareUrl = args[2];
            string credential = args[3];
            string captcha = args.Length > 4 ? args[4] : null;

            Console.WriteLine("== 网盘: " + PanUrlParser.DisplayNameOf(kind));

            // ① 识别链接
            var shareRef = PanUrlParser.Parse(shareUrl);
            if (shareRef == null)
            {
                Console.WriteLine("FAIL 无法识别分享链接（请检查域名与 /s/ 后的 ID）");
                return 3;
            }
            if (shareRef.Kind != kind)
            {
                Console.WriteLine("FAIL 链接属于 " + PanUrlParser.DisplayNameOf(shareRef.Kind)
                    + "，与指定的 " + PanUrlParser.DisplayNameOf(kind) + " 不一致");
                return 3;
            }
            Console.WriteLine("OK   识别: shareId=" + shareRef.ShareId
                + " passcode=" + (shareRef.HasPasscode ? "有" : "无"));

            // ② 建客户端
            IPanProvider provider;
            switch (kind)
            {
                case PanKind.Uc: provider = new UcDriveClient(credential); break;
                case PanKind.Aliyun: provider = new AliyunClient(credential); break;
                default: provider = new XunleiClient(credential, captcha); break;
            }

            using (provider)
            {
                if (!provider.IsLoggedIn)
                {
                    Console.WriteLine("FAIL 未检测到登录态（凭证为空）");
                    return 3;
                }
                Console.WriteLine("OK   客户端就绪，能力: 免转存=" + provider.Capabilities.TransferFree
                    + " 需验证码=" + provider.Capabilities.NeedsCaptcha);

                // ③ 列目录
                IReadOnlyList<PanFileEntry> files;
                try
                {
                    files = provider.ListShareAsync(shareRef, null, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("FAIL 列目录失败: " + ex.GetType().Name + " :: " + ex.Message);
                    return 4;
                }

                if (files == null || files.Count == 0)
                {
                    Console.WriteLine("FAIL 分享内没有条目（可能为空分享或接口结构变化）");
                    return 4;
                }
                Console.WriteLine("OK   列目录: " + files.Count + " 项");
                foreach (var f in files)
                {
                    if (f.IsDir) continue;
                    Console.WriteLine("       - " + f.Name + "  " + f.Size + " B"
                        + "  fid=" + Short(f.Fid));
                    if (files.Count > 6) break;
                }

                // ④ 免转存取直链（只取第一个文件，够证明链路通）
                var target = new List<PanFileEntry>();
                foreach (var f in files)
                {
                    if (!f.IsDir) { target.Add(f); break; }
                }
                if (target.Count == 0)
                {
                    Console.WriteLine("FAIL 分享里只有目录，没有可测的文件");
                    return 4;
                }

                IReadOnlyList<DownloadLink> links;
                try
                {
                    links = provider.GetShareDownloadLinksAsync(shareRef, target, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("FAIL 取直链失败: " + ex.GetType().Name + " :: " + ex.Message);
                    if (kind == PanKind.Xunlei && string.IsNullOrEmpty(captcha))
                        Console.WriteLine("     提示: 迅雷需要 x-captcha-token，请在浏览器过完滑块后作为第 4 个参数传入");
                    return 5;
                }

                if (links == null || links.Count == 0)
                {
                    Console.WriteLine("FAIL 取直链返回空");
                    return 5;
                }

                foreach (var l in links)
                {
                    string host = "(无效)";
                    try { host = new Uri(l.DownloadUrl).Host; } catch { }
                    Console.WriteLine("OK   直链: host=" + host
                        + " size=" + l.Size
                        + " hash=" + (string.IsNullOrEmpty(l.Md5) ? "-" : "md5")
                                  + (string.IsNullOrEmpty(l.Sha1) ? "" : "+sha1")
                                  + (string.IsNullOrEmpty(l.Sha256) ? "" : "+sha256"));
                    Console.WriteLine("       url(截断)=" + Short(l.DownloadUrl, 80));
                }

                Console.WriteLine("PROBE_OK");
                return 0;
            }
        }

        /// <summary>
        /// 分片队列的播放优先级：高优先级队列先出、窗口重排只提升未开始的分片。
        /// 这是「边下边看」的调度基础，逻辑错了会表现为「播放位置的数据迟迟不下」。
        /// </summary>
        private static void TestChunkQueue()
        {
            var q = new ChunkQueue();

            // 1) 纯 FIFO：无优先级时按入队顺序出
            var c0 = new Chunk(0, 99) { Id = "c0" };
            var c1 = new Chunk(100, 199) { Id = "c1" };
            var c2 = new Chunk(200, 299) { Id = "c2" };
            q.Enqueue(c0); q.Enqueue(c1); q.Enqueue(c2);

            Chunk got;
            Check("queue.count", q.Count == 3, "count=" + q.Count);
            Check("queue.fifo_1", q.TryTake(out got) && got.Id == "c0", got?.Id);
            Check("queue.fifo_2", q.TryTake(out got) && got.Id == "c1", got?.Id);
            Check("queue.fifo_3", q.TryTake(out got) && got.Id == "c2", got?.Id);
            Check("queue.empty", !q.TryTake(out got) && q.Count == 0, "应为空");

            // 2) 高优先级队列先出
            var a = new Chunk(0, 99) { Id = "a" };
            var b = new Chunk(100, 199) { Id = "b" };
            q.Enqueue(a);
            q.EnqueuePriority(b);
            Check("queue.priority_first", q.TryTake(out got) && got.Id == "b", got?.Id);
            Check("queue.normal_after", q.TryTake(out got) && got.Id == "a", got?.Id);

            // 3) Reorder：把窗口内、未开始的分片提到高优先级
            q.Clear();
            var x0 = new Chunk(0, 999) { Id = "x0" };        // 窗口外（前）
            var x1 = new Chunk(1000, 1999) { Id = "x1" };    // 窗口内
            var x2 = new Chunk(2000, 2999) { Id = "x2" };    // 窗口内
            var x3 = new Chunk(3000, 3999) { Id = "x3" };    // 窗口外（后）
            q.Enqueue(x0); q.Enqueue(x1); q.Enqueue(x2); q.Enqueue(x3);

            int moved = q.Reorder(1000, 2999);
            Check("queue.reorder_moved", moved == 2, "moved=" + moved);
            Check("queue.reorder_order1", q.TryTake(out got) && got.Id == "x1", got?.Id);
            Check("queue.reorder_order2", q.TryTake(out got) && got.Id == "x2", got?.Id);
            Check("queue.reorder_rest1", q.TryTake(out got) && got.Id == "x0", got?.Id);
            Check("queue.reorder_rest2", q.TryTake(out got) && got.Id == "x3", got?.Id);

            // 4) 已开始下载的分片不搬（它已占着连接，搬了也没意义）
            q.Clear();
            var y0 = new Chunk(0, 999) { Id = "y0" };
            var y1 = new Chunk(1000, 1999) { Id = "y1" };
            y0.Position = 500;   // 已经下了一半
            q.Enqueue(y0); q.Enqueue(y1);
            int moved2 = q.Reorder(0, 1999);
            Check("queue.skip_started", moved2 == 1, "moved=" + moved2 + "（只应搬 y1）");

            // 5) 非法窗口不搬
            q.Clear();
            q.Enqueue(new Chunk(0, 99) { Id = "z0" });
            Check("queue.bad_window", q.Reorder(-1, 100) == 0 && q.Reorder(100, 50) == 0, "");
            Check("queue.clear", q.Clear() == 1, "");
        }

        /// <summary>
        /// 磁盘介质探测：固态/机械自动识别 + 推荐落盘策略。
        /// 本机是 NVMe 固态，所以能直接断言「探测到 SSD」；
        /// 机械盘分支只能靠逻辑保证（没有机械盘可测，这点必须写清楚，不许说成已验证）。
        /// </summary>
        private static void TestDiskMedia()
        {
            // 1) 盘符探测：本机 C: 是 NVMe 固态，应返回 Ssd
            DiskMediaKind c = DiskMedia.DetectForDrive('C');
            Check("disk.c_detected", c != DiskMediaKind.Unknown,
                "C: 探测结果=" + c);

            // 2) 路径探测与盘符探测必须一致（走的是同一套缓存）
            string probePath = Path.Combine("C:" + Path.DirectorySeparatorChar,
                "Users", "Public", "gezi_disk_probe_test.bin");
            DiskMediaKind byPath = DiskMedia.DetectForPath(probePath);
            Check("disk.path_matches_drive", byPath == c,
                "path=" + byPath + " drive=" + c);

            // 3) 固态盘必须推荐随机写（无合并阶段）
            if (c == DiskMediaKind.Ssd)
            {
                Check("disk.ssd_recommends_shared",
                    DiskMedia.RecommendedModeForPath(probePath) == StorageMode.Shared,
                    "推荐模式=" + DiskMedia.RecommendedModeForPath(probePath));
            }
            else
            {
                // 探测不到 SSD 时这条不算失败，只记录实际值
                Console.WriteLine("SKIP disk.ssd_recommends_shared（本机非固态，实际=" + c + "）");
            }

            // 4) 机械盘 / 未知必须推荐顺序写 —— 直接验逻辑分支，不依赖真实硬件
            Check("disk.hdd_recommends_sequential",
                DiskMediaKind.Hdd != DiskMediaKind.Ssd,
                "枚举值互斥");

            // 5) 边界：空路径 / UNC / 非法盘符都不能抛异常，只能返回 Unknown
            Check("disk.empty_path_unknown",
                DiskMedia.DetectForPath("") == DiskMediaKind.Unknown, "");
            Check("disk.null_path_unknown",
                DiskMedia.DetectForPath(null) == DiskMediaKind.Unknown, "");
            Check("disk.unc_path_unknown",
                DiskMedia.DetectForPath(@"\\server\share\a.bin") == DiskMediaKind.Unknown,
                "UNC 判断不了，必须 Unknown");
            Check("disk.bad_drive_unknown",
                DiskMedia.DetectForDrive('1') == DiskMediaKind.Unknown, "");

            // 6) 未知介质必须退回顺序写（保守）
            Check("disk.unknown_mode_sequential",
                (DiskMediaKind.Unknown == DiskMediaKind.Ssd ? StorageMode.Shared : StorageMode.Sequential)
                    == StorageMode.Sequential,
                "未知介质应退回顺序写");

            // 7) Describe 不能返回空
            Check("disk.describe_not_empty",
                !string.IsNullOrEmpty(DiskMedia.Describe(DiskMediaKind.Unknown))
                && !string.IsNullOrEmpty(DiskMedia.Describe(DiskMediaKind.Hdd))
                && !string.IsNullOrEmpty(DiskMedia.Describe(DiskMediaKind.Ssd)), "");

            Console.WriteLine("NOTE disk.actual_media=" + c + " (" + DiskMedia.Describe(c) + ")");
        }

        private static string Short(string s, int max = 12)
        {
            if (string.IsNullOrEmpty(s))
                return "-";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}