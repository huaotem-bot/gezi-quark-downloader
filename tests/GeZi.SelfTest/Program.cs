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
using GeZi.Core.Download;
using GeZi.Core.Models;
using GeZi.Core.Support;

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
            string f = Path.Combine(dir, "movie.bin" + SegmentedDownloader.PendingDeleteSuffix);
            File.WriteAllText(f, "x");
            int removed = SegmentedDownloader.SweepPendingDeletes(new[] { dir });
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

                if (path.IndexOf("/drive/v1/share", StringComparison.Ordinal) >= 0)
                    return new MockHttpServer.MockResponse(
                        "{\"share_status\":\"OK\",\"files\":["
                        + "{\"id\":\"x1\",\"name\":\"剧集.mp4\",\"size\":555,\"kind\":\"drive#file\"},"
                        + "{\"id\":\"xd\",\"name\":\"目录\",\"kind\":\"drive#folder\"}]}");

                if (path.IndexOf("/drive/v1/files/", StringComparison.Ordinal) >= 0)
                    return new MockHttpServer.MockResponse(
                        "{\"id\":\"x1\",\"name\":\"剧集.mp4\",\"size\":555,"
                        + "\"web_content_link\":\"https://lixian.xunlei.test/c.mp4?sig=z\"}");

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
                using (var xl2 = new XunleiClient("PANTOK", null, server.BaseUrl))
                {
                    bool ok = false;
                    try
                    {
                        var ls = xl2.GetShareDownloadLinksAsync(
                            new PanShareRef { Kind = PanKind.Xunlei, ShareId = "S" },
                            new List<PanFileEntry> { new PanFileEntry { Fid = "x1" } })
                            .GetAwaiter().GetResult();
                        ok = ls != null && ls.Count == 1;
                    }
                    catch (QuarkApiException)
                    {
                        ok = false;
                    }
                    Check("xl.no_captcha_ok", ok);
                }

                var reqs = server.Requests;

                // 带凭证的请求必须有 Authorization + x-captcha-token
                var shareReq = reqs.Find(r => r.Path.Contains("/drive/v1/share"));
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

                // 取链走 /drive/v1/files/{id}
                Check("xl.req_files_path",
                    reqs.Exists(r => r.Path.Contains("/drive/v1/files/x1")));
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

        private static string Short(string s, int max = 12)
        {
            if (string.IsNullOrEmpty(s))
                return "-";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}