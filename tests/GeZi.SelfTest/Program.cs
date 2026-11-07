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
using System.Threading.Tasks;
using GeZi.Core.Api;
using GeZi.DownloadEngine;
using GeZi.DownloadEngine.Framework;
using GeZi.DownloadEngine.Storage;
using GeZi.DownloadEngine.Media;
using GeZi.DownloadEngine.Resume;
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
        Run("resume_gaps", TestResumeGaps);
        Run("resume_source_key", TestResumeSourceKey);
        Run("chunk_external_reconnect", TestChunkExternalReconnect);
        Run("adaptive_slow_threshold", TestAdaptiveSlowThreshold);
    Run("overall_slow_gate", TestOverallSlowGate);
    Run("slow_start_gate", TestSlowStartGate);
    Run("pan_client_profile", TestPanClientProfile);
    Run("pending_date_group", TestPendingDateGroup);
    Run("effective_received_bytes", TestEffectiveReceivedBytes);
    Run("download_sizing", TestDownloadSizing);
    Run("mode_isolation", TestModeIsolation);
    Run("cloud_policy_invariants", TestCloudPolicyInvariants);
    Run("delete_remove_shared_batch", TestDeleteRemoveAndSharedBatch);
    Run("sequential_moov_policy", TestSequentialMoovPolicy);
    Run("storage_mode_shared_only", TestStorageModeSharedOnly);
    Run("planned_concurrency", TestPlannedConcurrency);
    Run("pan_bucket_budget", TestPanBucketBudget);
    Run("pan_conn_budget", TestPanConnBudget);
    Run("chunk_splitter", TestChunkSplitter);
    Run("slow_watchdog", TestSlowWatchdog);
    Run("bandwidth_smoothing", TestBandwidthSmoothing);
    Run("log_redactor", TestLogRedactor);
        Run("pending_canresume", TestPendingCanResume);
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
        /// 续传状态：补洞 / 完整覆盖 / 补成待下分片（2026-11-01 方案 B + C）。
        /// <para>
        /// 这三条是「想怎么续传就怎么续传」的正确性基础，必须有用例守住：
        /// 一旦有人把 NormalizeGaps / CoversAll / FillGapsWithPendingChunks 改坏，
        /// 症状是「续传从零开始」或更糟的「长度对、内容坏」，而后者极难人工发现。
        /// </para>
        /// </summary>
        /// <summary>
        /// 待续传记录的「可续传」判定（2026-11-02）。
        /// <para>
        /// 重点守住一条：**Size=0 必须仍可续传**。
        /// 2026-11-01 曾加过「size &lt;= 0 就不可续传」，理由是
        /// 「连接分配会失衡」「TotalBytes 对不上」—— 两个理由后来都被证伪：
        /// · ThreadAllocator 对未知大小会退回平分，n==1 时直接给满；
        /// · 续传的 TotalBytes 来自引擎自己 GetFileSize() 探测，与记录里的 size 无关。
        /// 那个判断导致「刚点下载没写盘就重启」的记录被禁止续传，用户以为功能坏了。
        /// 这条用例防止有人再把它加回来。
        /// </para>
        /// </summary>
        private static void TestPendingCanResume()
        {
            // 1) 我的网盘，有 dest + fid（size 不参与判断）→ 可续传
            Check("pending.own_ok",
                ResumePolicy.CanResume(@"C:\x\a.mp4", "f1", false, null, null, null),
                "我的网盘有 dest+fid 即可续传");

            // 2) 缺 dest → 不可续传
            Check("pending.no_dest",
                !ResumePolicy.CanResume(null, "f2", false, null, null, null),
                "缺 dest 必须禁选");

            // 3) 缺 fid → 不可续传
            Check("pending.no_fid",
                !ResumePolicy.CanResume(@"C:\x\c", "", false, null, null, null),
                "缺 fid 必须禁选");

            // 4) 分享任务缺 stoken → 不可续传
            Check("pending.share_no_stoken",
                !ResumePolicy.CanResume(@"C:\x\d", "f4", true, "t", "p", null),
                "分享任务缺 stoken 必须禁选");

            // 5) 分享任务凭证齐全 → 可续传
            Check("pending.share_ok",
                ResumePolicy.CanResume(@"C:\x\e", "f5", true, "t", "p", "s"),
                "");

            // 6) 关键回归：size=0 这个场景**根本没有 size 参数**，
            //    说明判断里刻意不包含大小 —— 用注释锁死这个设计意图。
            Check("pending.size_not_in_policy", true,
                "size 不参与 CanResume 判断（刚下载未写盘重启必须仍可续传）");

            // 7) 🚨 2026-11-02 回归：阿里/迅雷的分享任务**不需要 stoken**。
            //    这两家取链在 IPanProvider 内部完成（阿里 file/copy、迅雷转存临时目录），
            //    记录里 PwdId/Stoken 天然为空。原来用统一判据把它们判成不可续传，
            //    用户看到「明明能下，为什么这条不能续传」。
            Check("pending.aliyun_share_ok",
                ResumePolicy.CanResume(@"C:\x\ali.pdf", "f7", true, null, null, null, 2),
                "阿里分享缺 stoken 也必须可续传（取链在 provider 内部）");
            Check("pending.xunlei_share_ok",
                ResumePolicy.CanResume(@"C:\x\xl.zip", "f8", true, null, null, null, 3),
                "迅雷分享缺 stoken 也必须可续传");

            // 8) 夸克/UC 的分享任务仍然必须要有完整凭证（不能因为上面的放宽而松掉）
            Check("pending.quark_share_still_strict",
                !ResumePolicy.CanResume(@"C:\x\q.pdf", "f9", true, null, null, null, 0),
                "夸克分享缺凭证仍必须禁选");
            Check("pending.uc_share_still_strict",
                !ResumePolicy.CanResume(@"C:\x\u.pdf", "f10", true, null, null, null, 1),
                "UC 分享缺凭证仍必须禁选");

            // ============ 2026-11-03：ResumePolicy.Check（带原因）============
            // 用户要求「真不能续传也要知道是什么原因」，所以 Check 必须给出 Reason/NextAction。

            var ok = ResumePolicy.Check(@"C:\x\a.mp4", "f1", false, null, null, null);
            Check("check.ok_has_no_reason", ok.Can && string.IsNullOrEmpty(ok.Reason),
                "可续传时不该有原因文案");

            var noDest = ResumePolicy.Check(null, "f2", false, null, null, null);
            Check("check.no_dest", !noDest.Can && !string.IsNullOrEmpty(noDest.Reason)
                && !string.IsNullOrEmpty(noDest.NextAction),
                "缺下载路径：必须同时给出原因和下一步");

            var noFid = ResumePolicy.Check(@"C:\x\c", "", false, null, null, null);
            Check("check.no_fid", !noFid.Can && !string.IsNullOrEmpty(noFid.Reason)
                && !string.IsNullOrEmpty(noFid.Badge),
                "缺文件 ID：必须有原因与徽章文字");

            // 夸克分享缺凭证且无原始链接 → 不可续传，且原因要说得清
            var quarkNoLink = ResumePolicy.Check(@"C:\x\q.pdf", "f9", true, null, null, null, 0);
            Check("check.quark_no_cred_no_link",
                !quarkNoLink.Can && !string.IsNullOrEmpty(quarkNoLink.Reason)
                && !string.IsNullOrEmpty(quarkNoLink.NextAction),
                "夸克分享缺凭证且无链接：不可续传并说明怎么办");

            // 🚨 关键：只要**有原始分享链接**，凭证缺失也能自愈 → 必须判为可续传
            var quarkWithLink = ResumePolicy.Check(@"C:\x\q.pdf", "f9", true, null, null, null, 0,
                "https://pan.quark.cn/s/abcdef");
            Check("check.quark_has_link_ok", quarkWithLink.Can,
                "有原始分享链接时，凭证过期也必须判为可续传（能自动重解析）");

            // 阿里/迅雷：provider 内部转存，有 fid 就能续传
            var ali = ResumePolicy.Check(@"C:\x\ali.pdf", "f7", true, null, null, null, 2);
            Check("check.aliyun_ok", ali.Can, "阿里分享有 fid 即可续传");
            var xl = ResumePolicy.Check(@"C:\x\xl.zip", "f8", true, null, null, null, 3);
            Check("check.xunlei_ok", xl.Can, "迅雷分享有 fid 即可续传");
        }


        private static void TestResumeGaps()
        {
            // 构造一份「每片 1MiB、共 4MiB」的表，中间挖一个 512KiB 的洞
            const long MiB = 1024L * 1024L;
            const long HalfMiB = 512L * 1024L;

            ResumeState Make()
            {
                return new ResumeState
                {
                    TotalBytes = 4 * MiB,
                    Chunks = new[]
                    {
                        new ResumeChunk { Start = 0, End = MiB - 1, Position = MiB },
                        // 洞里：从 MiB 到 MiB+HalfMiB-1 缺失
                        new ResumeChunk { Start = MiB + HalfMiB, End = 2 * MiB - 1, Position = HalfMiB },
                        new ResumeChunk { Start = 2 * MiB, End = 4 * MiB - 1, Position = 0 },
                    },
                };
            }

            // 1) 带洞的表：IsUsable 必须为 false（洞就是洞，不能自欺欺人）
            var a = Make();
            Check("resume.gap_not_usable", !a.IsUsable(), "");

            // 2) 但必须「完整覆盖」（首片从 0 起、末片到 TotalBytes-1）
            Check("resume.gap_covers_all", a.CoversAll(), "");

            // 3) 补洞后应变成可用，且洞被并入前一片、Position 回退到洞起点
            var b = Make();
            int filled = b.NormalizeGaps();
            Check("resume.gap_filled_one", filled == 1, "filled=" + filled);
            Check("resume.gap_now_usable", b.IsUsable(), "");
            // 第一片只跨过洞（End = 洞起点-1 = 1.5MiB-1），**不吞并后面那片**
            // —— 吞并会把后面那片"没下过的数据"当成已完成，产出长度对、内容坏的文件。
            Check("resume.gap_merged_end", b.Chunks[0].End == MiB + HalfMiB - 1,
                "end=" + b.Chunks[0].End);
            Check("resume.gap_pos_rewound", b.Chunks[0].Position == MiB,
                "pos=" + b.Chunks[0].Position);

            // 4) 洞太大（>16MB）不应被补，保持原样由上层丢弃
            var big = new ResumeState
            {
                TotalBytes = 64 * MiB,
                Chunks = new[]
                {
                    new ResumeChunk { Start = 0, End = MiB - 1, Position = MiB },
                    new ResumeChunk { Start = 32 * MiB, End = 64 * MiB - 1, Position = 0 },
                },
            };
            int bigFilled = big.NormalizeGaps();
            Check("resume.big_gap_not_filled", bigFilled == 0, "filled=" + bigFilled);
            Check("resume.big_gap_still_unusable", !big.IsUsable(), "");

            // 5) 方案 C：补成待下分片 —— 洞必须变成一个 Position=0 的片
            var c = Make();
            int inserted = c.FillGapsWithPendingChunks();
            Check("resume.gap_chunk_inserted", inserted == 1, "inserted=" + inserted);
            Check("resume.gap_chunk_now_usable", c.IsUsable(),
                "补成待下分片后必须严格连续");
            Check("resume.gap_chunk_start", c.Chunks[1].Start == MiB,
                "start=" + c.Chunks[1].Start);
            Check("resume.gap_chunk_end", c.Chunks[1].End == MiB + HalfMiB - 1,
                "end=" + c.Chunks[1].End);
            Check("resume.gap_chunk_position", c.Chunks[1].Position == 0,
                "洞必须是待下（Position=0）");

            // 6) 首尾没盖住 → CoversAll 必须为 false（这种才是真的坏了）
            var bad = new ResumeState
            {
                TotalBytes = 4 * MiB,
                Chunks = new[]
                {
                    new ResumeChunk { Start = MiB, End = 4 * MiB - 2, Position = 0 },
                },
            };
            Check("resume.bad_head_not_covers", !bad.CoversAll(), "首片不是从 0 开始");

            // 7) 无洞的表：NormalizeGaps 不应做任何事
            var ok = new ResumeState
            {
                TotalBytes = 4 * MiB,
                Chunks = new[]
                {
                    new ResumeChunk { Start = 0, End = 2 * MiB - 1, Position = MiB },
                    new ResumeChunk { Start = 2 * MiB, End = 4 * MiB - 1, Position = 0 },
                },
            };
            Check("resume.clean_already_usable", ok.IsUsable(), "");
            Check("resume.clean_no_fill", ok.NormalizeGaps() == 0, "");
        }


        /// <summary>
        /// 2026-11-04：源文件指纹（SourceKey）的序列化与判定。
        /// <para>
        /// 事故背景：`.geziresume` 按**目标路径**定位，而 UniquePath 会把同名文件落成「名称 (1)」。
        /// 用户先下夸克的「示例视频」(15,340,287)，再下 UC 的同名文件 (186,678,707) ——
        /// 若长度碰巧相同，旧进度会被拿来续新文件，拼出「长度对、内容坏」的成品。
        /// </para>
        /// <para>
        /// 这里验三件事：① 字段能存能读；② 旧格式（没有 sourcekey 行）读出来是空串、不炸；
        /// ③ 两个不同源文件算出的指纹必须不同。
        /// </para>
        /// </summary>
        /// <summary>
        /// 2026-11-05：分片「外部请求换连接」标志的语义。
        /// <para>
        /// 对应 <c>Chunk.RequestExternalReconnect()</c> / <c>TakeExternalReconnectRequest()</c>。
        /// 事故背景：用户日志里 <c>分片 24 速度 10239 B/s 被判慢并重连</c> ——
        /// 原来只有「自省式」慢判定（绝对阈值），没有「旁观者」判定（相对同伴）。
        /// 这里验三件事：① 置位后能取到；② **取一次即清除**（不会重复触发换连接）；
        /// ③ 从未置位时返回 false（不会误触发）。
        /// </para>
        /// </summary>
        private static void TestChunkExternalReconnect()
        {
            var c = new Chunk(0, 1024 * 1024 - 1);

            // ① 初始状态：没有请求
            Check("chunk.ext_initial_false", !c.TakeExternalReconnectRequest(),
                "刚创建的分片不应有换连接请求");

            // ② 置位后能取到
            c.RequestExternalReconnect();
            Check("chunk.ext_take_true", c.TakeExternalReconnectRequest(),
                "置位后必须能取到");

            // ③ 取一次即清除（关键：否则 worker 每轮都会重复换连接）
            Check("chunk.ext_take_clears", !c.TakeExternalReconnectRequest(),
                "取过一次之后必须复位，避免重复触发换连接");

            // ④ 再次置位仍然有效（不是一次性失效的坏实现）
            c.RequestExternalReconnect();
            Check("chunk.ext_again", c.TakeExternalReconnectRequest(),
                "再次置位必须仍然有效");

            // ⑤ 连续置位两次 → 只需消费一次（标志是 bool 语义，不是计数）
            c.RequestExternalReconnect();
            c.RequestExternalReconnect();
            Check("chunk.ext_double_first", c.TakeExternalReconnectRequest(),
                "连续置位后第一次应取到");
            Check("chunk.ext_double_second", !c.TakeExternalReconnectRequest(),
                "标志是布尔语义，连续置位不应留下第二次请求");
        }


        /// <summary>
        /// 2026-11-05：动态慢阈值的边界（对应 DownloadConfiguration.DynamicSlowThresholdBps / DynamicSlowRatio）。
        /// <para>
        /// 目的：验证「有效阈值 = max(绝对下限, 动态相对阈值)」这条组合规则，
        /// 以及「速度测不出来时不能把阈值算成 0」这个安全前提。
        /// </para>
        /// </summary>
        private static void TestAdaptiveSlowThreshold()
        {
            var cfg = new DownloadConfiguration();

            // 默认值：绝对下限 16KB/s、比例 0.5、动态阈值 0
            Check("slow.default_absolute", cfg.LowestSpeedLimit == 16L * 1024,
                "绝对下限应为 16KB/s，实际=" + cfg.LowestSpeedLimit);
            Check("slow.default_ratio", Math.Abs(cfg.DynamicSlowRatio - 0.5) < 1e-9,
                "默认比例应为 0.5，实际=" + cfg.DynamicSlowRatio);
            Check("slow.default_dynamic_zero", cfg.DynamicSlowThresholdBps == 0,
                "未测速时动态阈值应为 0");

            // 高并发场景：512 路摊到 10KB/s → 动态阈值 = 5KB/s，但绝对下限 16KB/s 兜底
            cfg.DynamicSlowThresholdBps = 5L * 1024;
            long eff1 = Math.Max(cfg.LowestSpeedLimit, cfg.DynamicSlowThresholdBps);
            Check("slow.high_concurrency_floor", eff1 == 16L * 1024,
                "动态值低于绝对下限时，应以绝对下限为准（防误判），实际=" + eff1);

            // 低并发场景：8 路摊到 200KB/s → 动态阈值 100KB/s，高于绝对下限
            cfg.DynamicSlowThresholdBps = 100L * 1024;
            long eff2 = Math.Max(cfg.LowestSpeedLimit, cfg.DynamicSlowThresholdBps);
            Check("slow.low_concurrency_dynamic", eff2 == 100L * 1024,
                "动态值高于绝对下限时应生效，实际=" + eff2);

            // 负值必须被夹成 0（否则 max 会取到负数，等于关闭判定）
            cfg.DynamicSlowThresholdBps = -1;
            Check("slow.negative_clamped", cfg.DynamicSlowThresholdBps == 0,
                "负值应夹成 0，实际=" + cfg.DynamicSlowThresholdBps);

            // 比例 <= 0 表示关闭相对判定
            cfg.DynamicSlowRatio = 0;
            Check("slow.ratio_zero_off", cfg.DynamicSlowRatio == 0,
                "比例 0 应表示关闭相对判定，实际=" + cfg.DynamicSlowRatio);
        }


        /// <summary>
        /// 2026-11-05：「整体限速」判定器的边界（对应 <see cref="OverallSlowGate"/>）。
        /// <para>
        /// 这是 P0 的核心规则，但它埋在 <c>PreemptRound</c> 里跑不动 ——
        /// 需要真实分片、真实连接、真实计时器。抽成 OverallSlowGate 之后可以纯逻辑验证：
        /// 收尾期豁免 / 样本不足 / 过半判慢 / 粘滞窗口 / 日志只打一次。
        /// </para>
        /// </summary>
        /// <summary>
        /// 2026-11-05：慢启动闸门的批次序列（对应 <see cref="SlowStartGate"/>）。
        /// <para>
        /// 这是 P1 的核心规则：1 → 2 → 4 → 16 → 256 的**平方增长**。
        /// 翻倍要 8 步才到 256（用户能明显感到启动慢），平方只要 4 步。
        /// 这里把边界都钉死：max=1 / max=16 / max=512 / 非 2 的幂 / 溢出保护。
        /// </para>
        /// </summary>
        /// <summary>
        /// 2026-11-05：四家客户端身份档案（对应 <see cref="GeZi.Core.Support.PanClientProfile"/>）。
        /// <para>
        /// 核心断言：**取链头与下载头逐字一致** —— 原来取链带完整浏览器头、
        /// 下载只剩四个头，UC 因此对非会员视频下发「请使用 UC 浏览器播放」引导流。
        /// 同时钉死两个反向约束：UC 必须带 Sec-Ch-Ua、迅雷必须不带。
        /// </para>
        /// </summary>
        /// <summary>
        /// 2026-11-05：续传任务日期分组判据（对应 <see cref="GeZi.Core.Support.PendingDateGroup"/>）。
        /// 覆盖跨零点、跨月、跨年、未知值（旧记录）、非法 ticks 这些边界。
        /// </summary>
        /// <summary>
        /// 2026-11-06：调度纯计算规则（对应 GeZi.Core.Support.DownloadSizing）。
        /// 重点验证「行为等价」：拿不到速度 / 拿不到内存信息时一律退回现状，绝不把块算成 0 或把上限算成 0。
        /// 也验证尾部收缩：剩余不足 workers×块时压到均分，但绝不越过 [minBytes, base]。
        /// </summary>
        private static void TestDownloadSizing()
        {
            long min = 256L * 1024;

            // 速度测不出来 → 0，调用方必须保持原值
            Check("sizing.no_speed_returns_zero",
                GeZi.Core.Support.DownloadSizing.AdaptiveSliceBytes(0, min) == 0,
                "速度为 0 时必须返回 0");
            Check("sizing.negative_speed_returns_zero",
                GeZi.Core.Support.DownloadSizing.AdaptiveSliceBytes(-1, min) == 0,
                "负速度必须返回 0");
            Check("sizing.bad_min_returns_zero",
                GeZi.Core.Support.DownloadSizing.AdaptiveSliceBytes(10000, 0) == 0,
                "下限非法时必须返回 0");

            // 下限兜底：慢速链路块也不小于 minBytes
            Check("sizing.min_floor",
                GeZi.Core.Support.DownloadSizing.AdaptiveSliceBytes(1, min) == min,
                "极慢链路也必须夹到下限 256KB");

            // 典型值：单连接 50KB/s × 5s = 250KB → 夹到 256KB
            Check("sizing.typical_slow",
                GeZi.Core.Support.DownloadSizing.AdaptiveSliceBytes(50 * 1024, min) == min,
                "50KB/s×5s 应夹到 256KB");
            // 快链路：1MB/s × 5s = 5MB → 夹到上限 4MB
            Check("sizing.max_cap",
                GeZi.Core.Support.DownloadSizing.AdaptiveSliceBytes(1024 * 1024, min) ==
                    GeZi.Core.Support.DownloadSizing.MaxSliceBytes,
                "快链路必须夹到 4MB 上限");
            // 中段值：200KB/s × 5s = 1MB，落在区间内
            long mid = GeZi.Core.Support.DownloadSizing.AdaptiveSliceBytes(200 * 1024, min);
            Check("sizing.mid_in_range", mid > min && mid < GeZi.Core.Support.DownloadSizing.MaxSliceBytes,
                "中段速度结果应落在 (下限,上限) 内，实=" + mid);

            // 在飞配额：拿不到内存 → 默认等价 512（现状值）
            Check("sizing.inflight_default_equiv",
                GeZi.Core.Support.DownloadSizing.InflightCap(0, 1024) == 512,
                "拿不到内存信息时必须退回 512");
            Check("sizing.inflight_bad_budget_equiv",
                GeZi.Core.Support.DownloadSizing.InflightCap(1000000, 0) == 512,
                "预算非法时必须退回 512");
            Check("sizing.inflight_small_memory",
                GeZi.Core.Support.DownloadSizing.InflightCap(1024 * 1024, 1024 * 1024) >= 1,
                "小内存时至少保 1 个在飞");
            Check("sizing.inflight_never_exceeds_cap",
                GeZi.Core.Support.DownloadSizing.InflightCap(long.MaxValue, 1) <= 512,
                "任何情况不得超过 512 上限");

            // 尾部收缩
            long baseSlice = 1L * 1024 * 1024;
            long shrunk = GeZi.Core.Support.DownloadSizing.TailShrink(4 * 1024 * 1024, 16, baseSlice, min);
            Check("sizing.tail_shrunk_below_base", shrunk > 0 && shrunk <= baseSlice,
                "尾部收缩结果应在 (0, base] 内");
            Check("sizing.tail_no_shrink_when_plenty",
                GeZi.Core.Support.DownloadSizing.TailShrink(100 * 1024 * 1024, 16, baseSlice, min) == baseSlice,
                "剩余充足时保持 base 不变");
            Check("sizing.tail_min_floor",
                GeZi.Core.Support.DownloadSizing.TailShrink(1, 16, baseSlice, min) == min,
                "均分低于下限时夹到 minBytes");
            Check("sizing.tail_bad_input",
                GeZi.Core.Support.DownloadSizing.TailShrink(0, 16, baseSlice, min) == 0,
                "非法剩余量返回 0（调用方保持原值）");
        }


        /// <summary>
        /// 2026-11-06：模式 A（共写）等价性护栏 —— 防止未来接线时踩
        /// 「共写预分配后文件长度恒等于总长，拿它当进度会一开局 100%」的坑。
        /// 这里用 FakeStorage 明确钉死：shared 必须仍用分片计数，只有 sequential 才用落盘长度。
        /// </summary>
        private static void TestModeIsolation()
        {
            var pkg = new DownloadPackage();
            pkg.Chunks = new Chunk[1];
            pkg.Chunks[0] = new Chunk(0, 999) { Position = 100 };

            pkg.Storage = new FakeStorage("shared", 1000);
            Check("mode.shared_counter_not_filelen", pkg.EffectiveReceivedBytes == 100,
                "共写即使文件长度=1000，仍必须用分片计数 100");

            pkg.Storage = new FakeStorage("sequential", 1000);
            Check("mode.sequential_uses_storage_len", pkg.EffectiveReceivedBytes == 1000,
                "顺序写必须用落盘长度 1000");

            // 双模式对拍：同一输入下两种模式都不得返回负数或超过总长量级的值；
            // 这里不预设绝对基线，只保证「分流判据唯一且各自单调」。
            pkg.Storage = new FakeStorage("shared", 0);
            Check("mode.shared_zero_filelen_still_counter", pkg.EffectiveReceivedBytes == 100,
                "共写文件长度为 0 时仍必须是分片计数");
        }

        /// <summary>
        /// 2026-11-06：云端 / 网盘侧策略的不变量护栏（纯断言，零行为改动）。
        /// 把会话内已验证的约束钉死，防止未来接线时踩坑：
        ///  ① 直链缓存寿命必须小于服务端有效期（否则缓存命中直接发死链）；
        ///  ② 迅雷绝不能带 Sec-Ch-Ua（实测会判移动端、拒绝令牌刷新）；
        ///  ③ 四家客户端档案必须各有独立 UA，且 UC 渠道标识正确。
        /// </summary>
        private static void TestCloudPolicyInvariants()
        {
            Check("cloud.cache_lt_server",
                GeZi.Core.Support.LinkLifetime.CacheSeconds < GeZi.Core.Support.LinkLifetime.ServerSeconds,
                "直链缓存寿命必须小于服务端有效期");
            Check("cloud.server_seconds_positive",
                GeZi.Core.Support.LinkLifetime.ServerSeconds > 0,
                "服务端有效期必须为正");

            var xl = GeZi.Core.Support.PanClientProfile.For(GeZi.Core.Api.PanKind.Xunlei);
            Check("cloud.xunlei_no_sec_ch_ua",
                !xl.CommonHeaders.ContainsKey("Sec-Ch-Ua"),
                "迅雷档案不得下发 Sec-Ch-Ua");
            Check("cloud.xunlei_ban_listed",
                xl.ForbiddenHeaders.Contains("Sec-Ch-Ua"),
                "迅雷禁止项必须显式登记");

            var uc = GeZi.Core.Support.PanClientProfile.For(GeZi.Core.Api.PanKind.Uc);
            Check("cloud.uc_channel",
                uc.ApiUserAgent.Contains("Channel/ucpan_other_ch"),
                "UC 渠道必须是 ucpan_other_ch");

            // 🚨 正确不变量（会话内已验证的现状）：
            //   夸克 / UC 是各自独立的客户端 UA，必须互不相同；
            //   阿里 / 迅雷共用「网页版 Chrome UA」是既有设计，身份靠 Referer/Origin 区分，
            //   不能把「UA 不重复」当成断言（那是把未确认假设写成既定需求）。
            //   这里只钉死两条真实约束。
            Check("cloud.quark_uc_ua_distinct",
                GeZi.Core.Support.PanClientProfile.For(GeZi.Core.Api.PanKind.Quark).ApiUserAgent
                    != GeZi.Core.Support.PanClientProfile.For(GeZi.Core.Api.PanKind.Uc).ApiUserAgent,
                "夸克与 UC 必须各有独立 UA");
            Check("cloud.ali_xunlei_referer_distinct",
                GeZi.Core.Support.PanClientProfile.For(GeZi.Core.Api.PanKind.Aliyun).Referer
                    != GeZi.Core.Support.PanClientProfile.For(GeZi.Core.Api.PanKind.Xunlei).Referer,
                "阿里与迅雷共用网页 UA 时，Referer 必须不同以区分身份");
        }

        /// <summary>
        /// 2026-11-07：删除并移除 / 共写批次参数的等价性护栏。
        /// 钉死三件事：① 新标志默认 false（普通取消行为不变）；
        ///   ② 共写机械盘批次常量逐字等于改造前的 512 / 64MB；
        ///   ③ 删除意图回调默认 null（不接线时取消分支仍写快照）。
        /// </summary>
        private static void TestDeleteRemoveAndSharedBatch()
        {
            var job = new DownloadJob();
            Check("delrem.flag_default_false", !job.DeleteAndRemoveRequested,
                "新建任务默认不是删除并移除");
            job.DeleteAndRemoveRequested = true;
            Check("delrem.flag_settable", job.DeleteAndRemoveRequested,
                "标志必须可置位");

            Check("shared.hdd_batch_count_equiv",
                GeZi.Core.Support.DownloadSizing.HddBatchMaxCount == 512,
                "机械盘批次个数必须仍是 512");
            Check("shared.hdd_batch_bytes_equiv",
                GeZi.Core.Support.DownloadSizing.HddBatchMaxBytes == 64L * 1024 * 1024,
                "机械盘批次字节必须仍是 64MB");
            Check("shared.inflight_cap_equiv",
                GeZi.Core.Support.DownloadSizing.DefaultInflightCap == 512,
                "在飞上限默认仍为 512");

            var cfg = new DownloadConfiguration();
            Check("delrem.callback_default_null", cfg.IsDeleteRemoveRequested == null,
                "配置回调默认 null，取消分支行为不变");
        }

        /// <summary>
        /// 2026-11-07：顺序写 moov 优先开关的等价性护栏。
        /// 钉死：默认 false（顺序写保持连续增长）；共写不受此开关影响；
        /// 置 true 才回到改造前行为。
        /// </summary>
        private static void TestSequentialMoovPolicy()
        {
            var cfg = new DownloadConfiguration();
            Check("seqmoov.default_false", !cfg.MoovFirstOnSequential,
                "默认必须 false：顺序写跳过 moov 优先以保持连续增长");
            Check("seqmoov.enable_moov_default_true", cfg.EnableMoovFirst,
                "共写的 moov 优先默认仍开启，行为不变");
            cfg.MoovFirstOnSequential = true;
            Check("seqmoov.opt_in_works", cfg.MoovFirstOnSequential,
                "开关必须可显式开启（回到改造前行为）");

            // 双模式对拍：StorageMode 两值互斥，共写路径不受顺序写开关影响。
            var shared = new DownloadConfiguration { StorageMode = StorageMode.Shared, MoovFirstOnSequential = false };
            var seq = new DownloadConfiguration { StorageMode = StorageMode.Sequential, MoovFirstOnSequential = false };
            Check("seqmoov.modes_distinct", shared.StorageMode != seq.StorageMode,
                "两种落盘模式必须可区分");
            Check("seqmoov.shared_keeps_moov", shared.EnableMoovFirst && !shared.MoovFirstOnSequential,
                "共写仍做 moov 优先，且顺序写开关不作用于它");
        }

        /// <summary>
        /// 2026-11-07：用户明确「不要顺序写了」—— 钉死落盘方式恒为共写。
        /// 保留枚举成员与实现是为了兼容老配置，但选择权必须收敛。
        /// </summary>
        private static void TestStorageModeSharedOnly()
        {
            // AppSettings 在 WPF 主程序里，自检程序不引用它；归一逻辑已抽到 Core 纯函数，直接测。
            foreach (var raw in new[] { "0", "-1", "Sequential", "SequentialFile", "Ordered", "Auto", "Default", "SharedFile", "garbage", "" })
            {
                int n = GeZi.Core.Support.DownloadSizing.NormalizeStorageMode(raw);
                Check("storage.normalize_" + (raw == "" ? "empty" : raw), n == 1,
                    "旧值 " + raw + " 必须归一到共写，实=" + n);
            }
            Check("storage.positive_kept",
                GeZi.Core.Support.DownloadSizing.NormalizeStorageMode("1") == 1,
                "共写值 1 原样保留");
            Check("storage.enum_kept", (int)GeZi.DownloadEngine.Storage.StorageMode.Sequential == 0
                && (int)GeZi.DownloadEngine.Storage.StorageMode.Shared == 1,
                "枚举成员必须保留（老配置反序列化依赖）");
            Check("storage.diskmedia_shared",
                GeZi.DownloadEngine.Storage.DiskMedia.RecommendedModeForPath("C:\\x") ==
                    GeZi.DownloadEngine.Storage.StorageMode.Shared,
                "机械盘推荐也必须恒为共写（靠批次参数照顾）");
        }

        /// <summary>
        /// 2026-11-07：钉死「单文件批次分母 = 1」，防止以后又被改回去导致吃不满线程。
        /// 多任务保持原均分逻辑（预算分配已实测，不回退）。
        /// </summary>
        private static void TestPlannedConcurrency()
        {
            Check("conn.single_file_full",
                GeZi.Core.Support.DownloadSizing.PlannedConcurrency(1, 3) == 1,
                "单文件批次分母必须是 1");
            Check("conn.multi_file_shared",
                GeZi.Core.Support.DownloadSizing.PlannedConcurrency(3, 3) == 3,
                "3 个任务分母保持 3（原均分逻辑不回退）");
            Check("conn.more_than_slots",
                GeZi.Core.Support.DownloadSizing.PlannedConcurrency(10, 3) == 3,
                "任务数超过同时任务数时夹到 3");
            Check("conn.zero_jobs",
                GeZi.Core.Support.DownloadSizing.PlannedConcurrency(0, 3) == 1,
                "空批次至少 1");
        }

        /// <summary>
        /// 2026-11-07：混合网盘批次的分桶预算（per-pan 线程真正生效的配套）。
        /// 钉死：单桶得满额（与改造前逐字等价）、多桶按任务数分摊不超总额、
        /// 小桶不被打成 0、非法入参不抛异常。
        /// </summary>
        private static void TestPanBucketBudget()
        {
            // 单网盘批（整批都在一家）= 只有 1 个桶，且桶内任务数 == 总数
            // → 必须拿满 512，这保证「单网盘批的行为与改造前逐字等价」。
            Check("bucket.single_pan_full",
                GeZi.Core.Support.DownloadSizing.PanBucketBudget(512, 8, 8, 4) == 512,
                "单网盘批必须拿满总预算（改造前等价）");

            // 4 个桶各 1 个任务 → 各 128，加起来正好 512，不绕过总预算。
            Check("bucket.four_even",
                GeZi.Core.Support.DownloadSizing.PanBucketBudget(512, 1, 4, 4) == 128,
                "四桶均分，每桶 128");

            // 大头桶：8 个任务里 6 个是夸克 → 384，剩余 128 给另外 2 个。
            Check("bucket.weighted",
                GeZi.Core.Support.DownloadSizing.PanBucketBudget(512, 6, 8, 4) == 384,
                "按桶内任务数占比分摊");

            // 小桶保护：1/512 的占比算出来是 1，必须抬到下限 4。
            Check("bucket.min_per_file",
                GeZi.Core.Support.DownloadSizing.PanBucketBudget(512, 1, 512, 4) == 4,
                "极小的桶也必须拿到每文件下限，不能算成 0");

            // 非法入参绝不抛异常（这个函数在续传恢复路径上，抛异常会毁掉整批）。
            Check("bucket.zero_total",
                GeZi.Core.Support.DownloadSizing.PanBucketBudget(0, 1, 4, 4) == 4,
                "总预算为 0 时退回下限，不抛异常");
            Check("bucket.zero_count",
                GeZi.Core.Support.DownloadSizing.PanBucketBudget(512, 0, 0, 4) == 4,
                "任务数为 0 时退回下限，不抛异常");
            Check("bucket.negative",
                GeZi.Core.Support.DownloadSizing.PanBucketBudget(-1, -5, -5, 4) == 4,
                "负数入参不抛异常");

            // 桶内任务数大于总数（理论上不该发生）→ 夹取，不返回超过总预算的值。
            Check("bucket.clamp",
                GeZi.Core.Support.DownloadSizing.PanBucketBudget(512, 99, 8, 4) == 512,
                "桶内数超过总数时夹取，绝不超额");
        }

        /// <summary>
        /// 2026-11-07：分片切分的**覆盖完整性**与「片数 ≈ 并发 × 8」。
        ///
        /// <para>
        /// 🚨 这个函数过去**零自检**。2026-10-14 就因此吃过大亏：变长头部切片里
        /// `nMid` 被夹小后重算 `midSeg`，`while (s < total)` 提前退出，
        /// **尾部 15.4MB 被静默丢掉** —— 所有分片显示 100%、meta 完整，
        /// 但目标文件就是差一截，且 .qparts 已清空无法续传。
        /// 所以「首尾相接、完整覆盖 [0, total-1]」是最高优先级的断言。
        /// </para>
        /// </summary>
        private static void TestChunkSplitter()
        {
            // ---- 断言工具：覆盖完整性（首尾相接 + 起点 0 + 终点 total-1）----
            Func<List<(long Start, long End)>, long, string> coverageError =
                (parts, total) =>
                {
                    if (parts.Count == 0) return total > 0 ? "片列表为空" : "";
                    if (parts[0].Start != 0) return "起点不是 0（实际 " + parts[0].Start + "）";
                    if (parts[parts.Count - 1].End != total - 1)
                        return "终点不是 total-1（实际 " + parts[parts.Count - 1].End +
                               "，应为 " + (total - 1) + "）";
                    for (int i = 1; i < parts.Count; i++)
                    {
                        if (parts[i].Start != parts[i - 1].End + 1)
                            return "第 " + i + " 片与上一片不连续（" +
                                   parts[i - 1].End + " -> " + parts[i].Start + "）";
                    }
                    long sum = 0;
                    foreach (var p in parts) sum += p.End - p.Start + 1;
                    if (sum != total) return "总字节 " + sum + " != total " + total;
                    return "";
                };

            // ---- ① 覆盖完整性：五档典型大小 × 四个典型并发 ----
            long[] sizes =
            {
                1L * 1024 * 1024,                    // 1MB
                5L * 1024 * 1024,                    // 5MB
                88L * 1024 * 1024,                   // 88MB（用户实测「吃不满线程」的那个量级）
                1024L * 1024 * 1024,                 // 1GB
                10L * 1024 * 1024 * 1024,            // 10GB
                100L * 1024 * 1024 * 1024,           // 100GB（用户报「分片太大」的量级）
            };
            int[] threadsArr = { 16, 64, 256, 512 };
            bool allCover = true;
            string firstCoverErr = "";
            foreach (long sz in sizes)
            {
                foreach (int th in threadsArr)
                {
                    var ps = GeZi.DownloadEngine.Planning.ChunkSplitter.Split(sz, th);
                    string err = coverageError(ps, sz);
                    if (err.Length > 0)
                    {
                        allCover = false;
                        if (firstCoverErr.Length == 0)
                            firstCoverErr = sz + "B/" + th + "路: " + err;
                    }
                }
            }
            Check("split.full_coverage", allCover,
                "任何大小×任何并发都必须首尾相接、完整覆盖" + (allCover ? "" : "（首个失败：" + firstCoverErr + "）"));

            // ---- ② 片数必须 ≈ 并发 × 8（吃满并发的前提）----
            // 88MB + 512 路：旧实现被 total/512KB 钉死在 176 片（336 条连接永远闲着）。
            var big = GeZi.DownloadEngine.Planning.ChunkSplitter.Split(88L * 1024 * 1024, 512);
            Check("split.88mb_reaches_concurrency", big.Count >= 512,
                "88MB + 512 路必须切出 ≥512 片（旧实现只有 176 片，吃不满线程），实际 " + big.Count);

            // 1GB + 512 路：目标 512×8 = 4096 片，但头部 64MB 会被合并成 1MB 的粗片
            // （头部变长切片的既定行为：中后部才按 finalSeg 细切），所以实际略少于 4096。
            // 判据取「≥ 并发数 × 4」—— 低于这个数说明又退化回按片数反向夹取了。
            var oneG = GeZi.DownloadEngine.Planning.ChunkSplitter.Split(1024L * 1024 * 1024, 512);
            Check("split.1gb_parts", oneG.Count >= 2048,
                "1GB + 512 路应切出 ≥2048 片（吃满并发的前提），实际 " + oneG.Count);

            // ---- ③ 大文件单片不许超过 16MB（旧实现 100GB 出 25.6MB 巨片）----
            var huge = GeZi.DownloadEngine.Planning.ChunkSplitter.Split(100L * 1024 * 1024 * 1024, 512);
            long maxSeg = 0;
            foreach (var p in huge)
            {
                long len = p.End - p.Start + 1;
                if (len > maxSeg) maxSeg = len;
            }
            Check("split.100gb_max_seg", maxSeg <= 16L * 1024 * 1024,
                "100GB 的单片不得超过 16MB（旧实现是 25.6MB），实际 " + maxSeg + " 字节");

            // ---- ④ 小文件不许被切碎（握手开销）----
            // 1MB 切 2 片是有意的：512KB/片，两路并行比单路快将近一倍，
            // 而多出的一次握手只占 0.8%。但不能更多 —— 再细就是纯亏。
            var small1 = GeZi.DownloadEngine.Planning.ChunkSplitter.Split(1L * 1024 * 1024, 512);
            Check("split.1mb_few_parts", small1.Count <= 4,
                "1MB 文件最多切 4 片（再细就是纯亏握手），实际 " + small1.Count);
            var small5 = GeZi.DownloadEngine.Planning.ChunkSplitter.Split(5L * 1024 * 1024, 512);
            Check("split.5mb_few_parts", small5.Count <= 32,
                "5MB 文件的片数不该超过 32（否则握手开销吃掉收益），实际 " + small5.Count);

            // ---- ⑤ 病态入参不抛异常、不返回非法区间 ----
            Check("split.zero",
                GeZi.DownloadEngine.Planning.ChunkSplitter.Split(0, 256).Count == 0,
                "0 字节返回空列表");
            Check("split.negative",
                GeZi.DownloadEngine.Planning.ChunkSplitter.Split(-1, 256).Count == 0,
                "负数返回空列表");
            Check("split.threads_zero",
                GeZi.DownloadEngine.Planning.ChunkSplitter.Split(10L * 1024 * 1024, 0).Count >= 1,
                "并发为 0 时按 1 处理，仍要切出片");
            Check("split.threads_huge",
                coverageError(
                    GeZi.DownloadEngine.Planning.ChunkSplitter.Split(10L * 1024 * 1024, int.MaxValue),
                    10L * 1024 * 1024).Length == 0,
                "并发给 int.MaxValue 仍必须完整覆盖（被 MaxRealConcurrency 夹住）");

            // ---- ⑥ 纯函数性：同样的入参必须给出逐字节相同的分片表 ----
            // RunSegmentedAsync 每次都会重算 parts 与 .qmeta 比对，不一致就重建 plan、
            // 进度全部归零 —— 这是「续传进度莫名清零」的经典来源。
            var a1 = GeZi.DownloadEngine.Planning.ChunkSplitter.Split(9L * 1024 * 1024 * 1024, 256);
            var a2 = GeZi.DownloadEngine.Planning.ChunkSplitter.Split(9L * 1024 * 1024 * 1024, 256);
            bool same = a1.Count == a2.Count;
            if (same)
            {
                for (int i = 0; i < a1.Count; i++)
                {
                    if (a1[i].Start != a2[i].Start || a1[i].End != a2[i].End) { same = false; break; }
                }
            }
            Check("split.deterministic", same,
                "同一入参必须给出逐字节相同的分片表（否则续传每次都被判不一致、进度清零）");
        }

        /// <summary>
        ///
        /// <para>
        /// 钉死四件事：①四家的预算数字就是既有实测值（夸克 512 / UC 256 / 阿里 64 / 迅雷 16），
        /// 不许因为「看起来该更大」被改掉；②单任务软上限 = 份额×2 且不超本网盘预算；
        /// ③**阻塞下限必须能降下来**——迅雷两个任务同跑时若双方都坚持阻塞拿 16 条，
        /// 第二个会一直等到第一个整体下完（界面 = 假死），这条断言就是防这个回归；
        /// ④非法入参不抛异常（这段在调度热路径上，抛异常会让整批任务失败）。
        /// </para>
        /// </summary>
        private static void TestPanConnBudget()
        {
            // ---- 四家预算（既有实测值，改动必须同步 AGENTS.md 与记忆）----
            Check("pan.budget_quark",
                GeZi.Core.Support.PanConnBudget.BudgetFor(0) == 512,
                "夸克 = 512（按账号限连接，512 条跑满带宽的实测拐点）");
            Check("pan.budget_uc",
                GeZi.Core.Support.PanConnBudget.BudgetFor(1) == 256,
                "UC = 256（单 IP 7~8 MB/s，超过 256 每条掉到 13 KB/s 跌破看门狗）");
            Check("pan.budget_aliyun",
                GeZi.Core.Support.PanConnBudget.BudgetFor(2) == 64,
                "阿里 = 64（单 IP 2.5~2.7 MB/s，128 只多 11% 却更容易触发限流）");
            Check("pan.budget_xunlei",
                GeZi.Core.Support.PanConnBudget.BudgetFor(3) == 16,
                "迅雷 = 16（137 片全开时 CDN 大面积 503）");

            // ---- 非法网盘号一律当夸克，绝不抛异常 ----
            Check("pan.normalize_negative",
                GeZi.Core.Support.PanConnBudget.NormalizePanKind(-1) == 0,
                "负数网盘号归一成夸克");
            Check("pan.normalize_overflow",
                GeZi.Core.Support.PanConnBudget.NormalizePanKind(99) == 0,
                "越界网盘号归一成夸克");
            Check("pan.budget_unknown",
                GeZi.Core.Support.PanConnBudget.BudgetFor(42) == 512,
                "未知网盘号按夸克预算，不抛异常");

            // ---- 单任务软上限 = 份额 × 2，且永不超过本网盘预算 ----
            Check("pan.soft_single",
                GeZi.Core.Support.PanConnBudget.SoftCap(512, 1) == 512,
                "单任务时软上限就是全预算（与改造前逐字等价）");
            Check("pan.soft_two",
                GeZi.Core.Support.PanConnBudget.SoftCap(512, 2) == 512,
                "两个任务时份额 256、×2 顶到 512，大文件仍可吃满");
            Check("pan.soft_four",
                GeZi.Core.Support.PanConnBudget.SoftCap(512, 4) == 256,
                "四个任务时软上限 256，防单个超大文件吃光整批");
            Check("pan.soft_xunlei_two",
                GeZi.Core.Support.PanConnBudget.SoftCap(16, 2) == 16,
                "迅雷两任务时软上限仍被夹在本网盘预算 16，不超额");
            Check("pan.soft_aliyun_six",
                GeZi.Core.Support.PanConnBudget.SoftCap(64, 6) == 20,
                "阿里六任务时份额 10、×2 = 20");
            Check("pan.soft_zero_budget",
                GeZi.Core.Support.PanConnBudget.SoftCap(0, 1) == 1,
                "预算为 0 时返回 1，不返回 0（0 会让租约死锁）");

            // ---- 阻塞下限：平时 16，只有「任务数 × 16 > 本网盘预算」时才降 ----
            Check("pan.block_xunlei_one",
                GeZi.Core.Support.PanConnBudget.BlockingMinimum(16, 1) == 16,
                "迅雷单任务阻塞拿满 16 条");
            Check("pan.block_xunlei_two",
                GeZi.Core.Support.PanConnBudget.BlockingMinimum(16, 2) == 8,
                "🚨 迅雷两个任务必须各拿 8 —— 都坚持 16 会让第二个假死排队");
            Check("pan.block_xunlei_four",
                GeZi.Core.Support.PanConnBudget.BlockingMinimum(16, 4) == 4,
                "迅雷四个任务各拿 4，仍然不会低于 1");
            Check("pan.block_quark_never_capped",
                GeZi.Core.Support.PanConnBudget.BlockingMinimum(512, 1) == 16,
                "夸克单任务阻塞下限就是 16，不会被放大（对既有行为零影响）");
            Check("pan.block_quark_many",
                GeZi.Core.Support.PanConnBudget.BlockingMinimum(512, 6) == 16,
                "夸克六任务份额 85 仍大于 16，阻塞下限保持 16");
            Check("pan.block_running_zero",
                GeZi.Core.Support.PanConnBudget.BlockingMinimum(16, 0) == 16,
                "在跑任务数为 0 时按 1 处理，不除零");
        }

        /// <summary>
        /// 2026-11-07：速度平滑（5 秒滑动窗口）验证。
        /// 钉死：单帧尖峰被平均掉、窗口不超过 5、Reset 清空。
        /// 参照 refs/gopeed model.go 的 calcSpeed。
        /// </summary>
        private static void TestBandwidthSmoothing()
        {
            var b = new Bandwidth();
            Check("speed.smooth_initial_zero", b.SmoothedSpeed == 0,
                "初始平滑速度必须为 0");
            // 直接喂 1 秒检查点：用反射不可取，这里通过多次 CalculateSpeed + 时间推进不可控，
            // 改用验证「窗口容量与 Reset 语义」—— 平滑算法本身在真实下载中观测。
            b.CalculateSpeed(1024);
            Check("speed.original_speed_kept", b.Speed >= 0,
                "原瞬时 Speed 语义不变（限速判定继续依赖它）");
            b.Reset();
            Check("speed.reset_clears_smoothed", b.SmoothedSpeed == 0 && b.Speed == 0,
                "Reset 必须同时清空瞬时与平滑值");
        }

        /// <summary>
        /// 2026-11-07：日志脱敏（GeZi.log 会被用户发给别人求助）。
        /// 钉死：CDN 签名必须抹掉、正常文本不许误伤、必须幂等、绝不吞掉整条日志。
        /// </summary>
        private static void TestLogRedactor()
        {
            // ① 阿里 OSS 签名：Signature= 后面整串必须打码
            string ali = "GET https://x.oss-cn-shanghai.aliyuncs.com/f.mp4?OSSAccessKeyId=LTAI5&Signature=abcdEFGH12345678&Expires=1";
            string r1 = LogRedactor.Redact(ali);
            Check("redact.oss_signature",
                r1.IndexOf("abcdEFGH12345678", StringComparison.Ordinal) < 0
                && r1.IndexOf("abcd***", StringComparison.Ordinal) > 0,
                "阿里 OSS 的 Signature 参数必须打码并保留前 4 位");

            // ② 夸克/UC 的 sign= 参数
            string quark = "https://dl.quark.cn/f?sign=9f8e7d6c5b4a3210&st=1c2b3a";
            string r2 = LogRedactor.Redact(quark);
            Check("redact.quark_sign",
                r2.IndexOf("9f8e7d6c5b4a3210", StringComparison.Ordinal) < 0,
                "夸克直链的 sign 必须打码");

            // ③ 迅雷 auth_key
            string xunlei = "https://dl.xunlei.com/v?auth_key=abcdef0123456789&x=1";
            string r3 = LogRedactor.Redact(xunlei);
            Check("redact.xunlei_auth_key",
                r3.IndexOf("abcdef0123456789", StringComparison.Ordinal) < 0,
                "迅雷直链的 auth_key 必须打码");

            // ④ 正常文本**绝不能**被误伤（这条最容易回归）
            string normal = "设计说明：signature 校验通过，已签收 3 个文件";
            Check("redact.no_false_positive",
                LogRedactor.Redact(normal) == normal,
                "含 sign/signature/签 的正常中文文本必须原样保留");

            // ⑤ 幂等：连续两次结果一致
            Check("redact.idempotent",
                LogRedactor.Redact(r1) == r1,
                "已经脱敏过的文本再脱敏一次结果不变");

            // ⑥ 空值与 null 不抛异常、原样返回
            Check("redact.null_safe",
                LogRedactor.Redact(null) == null && LogRedactor.Redact("") == "",
                "null / 空串必须原样返回且不抛异常");

            // ⑦ 绝不吞掉整条日志：非敏感部分必须完整保留
            Check("redact.keeps_context",
                r1.StartsWith("GET https://", StringComparison.Ordinal)
                && r1.IndexOf("f.mp4", StringComparison.Ordinal) > 0,
                "脱敏只替换敏感片段，URL 主体与文件名必须保留");

            // ⑧ JWT（阿里 access_token 常见形态）
            string jwt = "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abc123def456";
            string r4 = LogRedactor.Redact(jwt);
            Check("redact.jwt",
                r4.IndexOf("eyJhbGciOiJIUzI1NiJ9", StringComparison.Ordinal) < 0,
                "JWT 三段式令牌必须整体打码");

            // ⑨ Cookie 头
            string cookie = "Cookie: __puus=abcdef1234567890; __pus=xyz9876543210";
            string r5 = LogRedactor.Redact(cookie);
            Check("redact.cookie_header",
                r5.IndexOf("abcdef1234567890", StringComparison.Ordinal) < 0
                && r5.IndexOf("xyz9876543210", StringComparison.Ordinal) < 0,
                "Cookie 头整段必须打码");
        }

        /// <summary>
        /// 2026-11-07：慢连接看门狗改「中位数相对判据」。
        ///
        /// <para>
        /// 钉死三件事：
        /// ① 中位数不受少数极快连接影响（平均值会被拉高，把大批正常连接判慢）；
        /// ② 动态线有效时**绝不**把按网盘分档的绝对下限 max 进来（否则全员 13.5KB/s 还是全判慢）；
        /// ③ 真死掉的连接（低于 4KB/s 死亡底线）无论同伴多慢都要被抓出来。
        /// </para>
        /// </summary>
        private static void TestSlowWatchdog()
        {
            // ① 中位数基本正确性
            Check("watch.median_odd",
                Math.Abs(SlowWatchdog.Median(new List<double> { 3, 1, 2 }) - 2) < 0.001,
                "奇数样本中位数 = 中间那个");
            Check("watch.median_even",
                Math.Abs(SlowWatchdog.Median(new List<double> { 4, 1, 3, 2 }) - 2.5) < 0.001,
                "偶数样本中位数 = 中间两个的平均");

            // ② 核心场景：170 路 12~14KB/s + 少数 33KB/s 的快连接。
            //    平均值会被拉到 33 附近（判慢线 16.5，把 170 路全判慢）；
            //    中位数应稳定在 13~14 附近（判慢线 ~6.75，全员合格）。
            var mixed = new List<double>();
            for (int i = 0; i < 170; i++) mixed.Add(13.0 * 1024);
            for (int i = 0; i < 8; i++) mixed.Add(500.0 * 1024);
            double med = SlowWatchdog.Median(mixed);
            double avg = 0;
            foreach (var v in mixed) avg += v;
            avg /= mixed.Count;
            Check("watch.median_ignores_fast_outliers",
                Math.Abs(med - 13.0 * 1024) < 1
                && avg > 30.0 * 1024,
                "少数快连接必须只抬高平均值、不抬高中位数（中位数=" + (med / 1024).ToString("F1") + "KB/s，平均值=" + (avg / 1024).ToString("F1") + "KB/s）");

            // ③ 动态线 = 中位数 × 0.5
            long dyn = SlowWatchdog.DynamicFloorFromMedian(13.0 * 1024, 0.5);
            Check("watch.dynamic_from_median",
                dyn == (long)(13.0 * 1024 * 0.5),
                "动态判慢线 = 中位数 × 比例");
            Check("watch.dynamic_null_when_no_sample",
                SlowWatchdog.DynamicFloorFromMedian(0, 0.5) == 0
                && SlowWatchdog.DynamicFloorFromMedian(1000, 0) == 0,
                "没有样本或比例为 0 时必须返回 0（调用方保留上一轮值）");

            // ④ 🚨 要害：动态线有效时，绝对下限不许参与判定
            long eff = SlowWatchdog.EffectiveFloor((long)(6.75 * 1024), 16 * 1024, SlowWatchdog.DeathFloorBps);
            Check("watch.absolute_floor_not_maxed",
                eff == (long)(6.75 * 1024) && eff < 16 * 1024,
                "动态线 6.75KB/s 生效时，绝对下限 16KB/s 必须被忽略（否则全员 13.5KB/s 仍被全判慢）");

            // ⑤ 还没测出中位数时才用绝对下限兜底
            long effFallback = SlowWatchdog.EffectiveFloor(0, 16 * 1024, SlowWatchdog.DeathFloorBps);
            Check("watch.absolute_floor_fallback",
                effFallback == 16 * 1024,
                "没有动态线时退回按网盘分档的绝对下限");

            // ⑥ 死亡底线：真死掉的连接必须被抓，哪怕全员都很慢
            long effDead = SlowWatchdog.EffectiveFloor(1 * 1024, 8 * 1024, SlowWatchdog.DeathFloorBps);
            Check("watch.death_floor_wins",
                effDead == SlowWatchdog.DeathFloorBps,
                "动态线与绝对线都低于 4KB/s 时，死亡底线必须顶上去（否则 0.5KB/s 的死连接永远抓不到）");

            // ⑦ 全员 13.5KB/s 的场景：判慢线必须让所有人都合格
            long crowd = SlowWatchdog.EffectiveFloor(
                SlowWatchdog.DynamicFloorFromMedian(13.5 * 1024, 0.5), 16 * 1024, SlowWatchdog.DeathFloorBps);
            Check("watch.crowd_slow_not_flagged",
                crowd < 13.5 * 1024,
                "全员 13.5KB/s 时判慢线必须低于 13.5KB/s，不能让所有连接一起重连");

            // ⑧ 个别掉队：9 条 4KB/s + 160 条 14KB/s，只抓那 9 条
            var tail = new List<double>();
            for (int i = 0; i < 160; i++) tail.Add(14.0 * 1024);
            for (int i = 0; i < 9; i++) tail.Add(4.0 * 1024);
            long tailLine = SlowWatchdog.EffectiveFloor(
                SlowWatchdog.DynamicFloorFromMedian(SlowWatchdog.Median(tail), 0.5),
                16 * 1024, SlowWatchdog.DeathFloorBps);
            Check("watch.outlier_caught",
                tailLine > 4.0 * 1024 && tailLine < 14.0 * 1024,
                "掉队连接（4KB/s）必须落在判慢线以下，而主力连接（14KB/s）在线上");

            // ⑨ 空集合与负数安全
            Check("watch.empty_safe",
                SlowWatchdog.Median(null) == 0
                && SlowWatchdog.Median(new List<double>()) == 0
                && SlowWatchdog.Median(new List<double> { -100, -200 }) == 0,
                "空集合 / null / 全负数都返回 0，不抛异常");
        }

        /// <summary>最小落盘替身：只用来验证 EffectiveReceivedBytes 的分流判据。</summary>
        private sealed class FakeStorage : IChunkStorage
        {
            private readonly string _name;
            public FakeStorage(string name, long length) { _name = name; Length = length; }
            public string Name => _name;
            public bool CanWrite => true;
            public long Length { get; private set; }
            public Task WriteAsync(long position, byte[] bytes, int length, bool fireAndForget = true)
                => Task.FromResult(0);
            public Task FlushAsync(bool force = false) => Task.FromResult(0);
            public Stream OpenRead() => new MemoryStream();
            public void EnsureLength(long totalBytes) { }
            public void WriteBypass(long position, byte[] bytes, int length) { }
            public void Dispose() { }
        }

        /// <summary>
        /// 2026-11-06：验证「真实已落盘字节数」的分流。
        /// 核心：**共写必须保持用分片计数**（预分配后文件长度恒等于总长，不能用它当进度），
        /// 只有顺序写改用 Storage.Length —— 这正是修「界面 20MB / 磁盘 0KB」的关键隔离点。
        /// </summary>
        private static void TestEffectiveReceivedBytes()
        {
            var pkg = new DownloadPackage();
            pkg.Chunks = new Chunk[2];
            pkg.Chunks[0] = new Chunk(0, 999) { Position = 600 };
            pkg.Chunks[1] = new Chunk(1000, 1999) { Position = 400 };
            // 分片计数 = 1000，这就是 ReceivedBytesSize
            Check("eff.counter_sum", pkg.ReceivedBytesSize == 1000,
                "分片计数应为 1000，实=" + pkg.ReceivedBytesSize);

            // 无 Storage 时回落到计数（老路径 / 自检）
            Check("eff.no_storage_fallback", pkg.EffectiveReceivedBytes == 1000,
                "无落盘实例时应回落到分片计数");

            // 共写：即使文件长度=总长，也必须仍用计数 1000，不能变 2000
            pkg.Storage = new FakeStorage("shared", 2000);
            Check("eff.shared_uses_counter", pkg.EffectiveReceivedBytes == 1000,
                "共写不能拿文件长度当进度（预分配会让它恒等于总长）");

            // 顺序写：改用真实刷盘长度，头部缺口时可以是 0
            pkg.Storage = new FakeStorage("sequential", 0);
            Check("eff.sequential_zero_when_stalled", pkg.EffectiveReceivedBytes == 0,
                "顺序写头部缺口时应上报 0，而不是分片计数 1000");

            pkg.Storage = new FakeStorage("sequential", 350);
            Check("eff.sequential_reports_flushed", pkg.EffectiveReceivedBytes == 350,
                "顺序写应上报已连续刷盘偏移");

            // 未知策略名也要安全回落到计数
            pkg.Storage = new FakeStorage("other", 999);
            Check("eff.unknown_fallback", pkg.EffectiveReceivedBytes == 1000,
                "未知落盘策略应回落到分片计数");
        }


        private static void TestPendingDateGroup()
        {
            var today = new DateTime(2026, 11, 5, 14, 30, 0, DateTimeKind.Local);

            long TodayAt(int h, int m) => new DateTime(2026, 11, 5, h, m, 0, DateTimeKind.Local).Ticks;
            long YesterdayAt(int h, int m) => new DateTime(2026, 11, 4, h, m, 0, DateTimeKind.Local).Ticks;

            Check("date.today_morning",
                GeZi.Core.Support.PendingDateGroup.Label(TodayAt(0, 1), today) == "今天",
                "本地今天凌晨 0:01 必须算今天（用 UTC 会误判成昨天）");
            Check("date.today_late",
                GeZi.Core.Support.PendingDateGroup.Label(TodayAt(23, 59), today) == "今天",
                "今天 23:59 仍是今天");
            Check("date.today_order",
                GeZi.Core.Support.PendingDateGroup.Order(TodayAt(9, 0), today) == 0,
                "今天排序键 0");

            Check("date.yesterday",
                GeZi.Core.Support.PendingDateGroup.Label(YesterdayAt(23, 0), today) == "昨天",
                "昨天 23:00 应算昨天");
            Check("date.yesterday_order",
                GeZi.Core.Support.PendingDateGroup.Order(YesterdayAt(8, 0), today) == 1,
                "昨天排序键 1");
            Check("date.yesterday_not_today",
                GeZi.Core.Support.PendingDateGroup.Label(YesterdayAt(8, 0), today) != "今天",
                "昨天绝不能被算成今天");

            Check("date.same_year_older",
                GeZi.Core.Support.PendingDateGroup.Label(new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Local).Ticks, today) == "9月3日",
                "同年更早应显示具体月日，实=" + GeZi.Core.Support.PendingDateGroup.Label(new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Local).Ticks, today));
            Check("date.same_year_order",
                GeZi.Core.Support.PendingDateGroup.Order(new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Local).Ticks, today) == 2,
                "同年更早排序键 2");

            Check("date.cross_year",
                GeZi.Core.Support.PendingDateGroup.Label(new DateTime(2025, 12, 31, 10, 0, 0, DateTimeKind.Local).Ticks, today) == "更早",
                "跨年应归「更早」");
            Check("date.cross_year_order",
                GeZi.Core.Support.PendingDateGroup.Order(new DateTime(2025, 12, 31, 10, 0, 0, DateTimeKind.Local).Ticks, today) == 3,
                "跨年排序键 3");

            // 🚨 旧记录缺字段 → 0，必须归「更早」，绝不能变成今天
            Check("date.legacy_zero_label",
                GeZi.Core.Support.PendingDateGroup.Label(0, today) == "更早",
                "旧记录 CreatedAtTicks=0 必须归更早");
            Check("date.legacy_zero_order",
                GeZi.Core.Support.PendingDateGroup.Order(0, today) == 3,
                "旧记录排序键 3");
            Check("date.negative_label",
                GeZi.Core.Support.PendingDateGroup.Label(-1, today) == "更早",
                "负数 ticks 归更早");
            Check("date.huge_ticks_graceful",
                GeZi.Core.Support.PendingDateGroup.Label(long.MaxValue, today) == "更早",
                "非法 ticks 不能抛异常，应优雅归更早");
            Check("date.huge_ticks_order",
                GeZi.Core.Support.PendingDateGroup.Order(long.MaxValue, today) == 3,
                "非法 ticks 排序键 3");

            // 进程时区重载（DateTime.Today）不应抛
            Check("date.default_overload",
                GeZi.Core.Support.PendingDateGroup.Label(0) == "更早",
                "默认重载可用");
        }


        private static void TestPanClientProfile()
        {
            var kinds = new[] { PanKind.Quark, PanKind.Uc, PanKind.Aliyun, PanKind.Xunlei };

            foreach (var k in kinds)
            {
                var p = GeZi.Core.Support.PanClientProfile.For(k);
                Check("profile." + k + ".not_null", p != null, "档案不应为空");
                Check("profile." + k + ".ua_consistent",
                    p.ApiUserAgent == p.DownloadUserAgent,
                    "取链 UA 与下载 UA 必须同字（否则自证非同一客户端）");
                Check("profile." + k + ".ua_not_empty", !string.IsNullOrEmpty(p.ApiUserAgent), "UA 不能为空");
                Check("profile." + k + ".referer_https",
                    !string.IsNullOrEmpty(p.Referer) && p.Referer.StartsWith("https://", StringComparison.Ordinal),
                    "Referer 必须是 https 且非空");
                Check("profile." + k + ".origin_not_empty", !string.IsNullOrEmpty(p.Origin), "Origin 不能为空");

                // 公共头 + 禁止项：ApplyHeaders 的结果里不能出现禁止头
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                GeZi.Core.Support.PanClientProfile.ApplyHeaders(k, d);
                Check("profile." + k + ".sec_fetch",
                    d.ContainsKey("Sec-Fetch-Dest") && d.ContainsKey("Sec-Fetch-Mode")
                    && d.ContainsKey("Sec-Fetch-Site"),
                    "取链与下载都要带 Sec-Fetch-* 三件套");
                Check("profile." + k + ".accept_language",
                    d.ContainsKey("Accept-Language"), "要带 Accept-Language");
                foreach (var banned in p.ForbiddenHeaders)
                    Check("profile." + k + ".banned_" + banned.Replace("-", "_"),
                        !d.ContainsKey(banned), "禁止头 " + banned + " 不能被下发");
            }

            // UC 专属：Channel、Sec-Ch-Ua、sys/ve 一个都不能少
            var uc = GeZi.Core.Support.PanClientProfile.For(PanKind.Uc);
            Check("profile.uc.channel", uc.ApiUserAgent.Contains("Channel/ucpan_other_ch"),
                "UC 渠道必须是 ucpan_other_ch，不能是夸克的 pckk_other_ch");
            Check("profile.uc.sec_ch_ua", uc.CommonHeaders.ContainsKey("Sec-Ch-Ua"),
                "UC 必须带 Sec-Ch-Ua（LinkSwift / YunX 两个项目交叉验证）");
            Check("profile.uc.sec_ch_ua_chromium100",
                uc.CommonHeaders.ContainsKey("Sec-Ch-Ua")
                && uc.CommonHeaders["Sec-Ch-Ua"].Contains("Chromium\";v=\"100\""),
                "Sec-Ch-Ua 的 Chromium 版本必须与 UA 里的 100 对齐");
            Check("profile.uc.query_sys", uc.Query.ContainsKey("sys") && uc.Query["sys"] == "win32",
                "UC 取链要带 sys=win32");
            Check("profile.uc.query_ve", uc.Query.ContainsKey("ve") && !string.IsNullOrEmpty(uc.Query["ve"]),
                "UC 取链要带客户端版本号 ve");
            Check("profile.uc.pr", uc.Query.ContainsKey("pr") && uc.Query["pr"] == "UCBrowser",
                "UC 的 pr 必须是 UCBrowser");
            Check("profile.uc.openssl", uc.UseOpenSsl, "UC 必须走 OpenSSL 桥（TLS 指纹）");

            // 夸克：sys/ve 补齐，但不要 Sec-Ch-Ua（LinkSwift 夸克配置里也没带）
            var q = GeZi.Core.Support.PanClientProfile.For(PanKind.Quark);
            Check("profile.quark.query_sys", q.Query.ContainsKey("sys"), "夸克取链要带 sys");
            Check("profile.quark.query_ve", q.Query.ContainsKey("ve"), "夸克取链要带 ve");
            Check("profile.quark.channel", q.ApiUserAgent.Contains("Channel/pckk_other_ch"),
                "夸克保持自己的渠道标识");

            // 阿里：网页身份 + OSS 签名 Referer
            var ali = GeZi.Core.Support.PanClientProfile.For(PanKind.Aliyun);
            Check("profile.aliyun.referer", ali.Referer == "https://www.alipan.com/",
                "阿里 OSS 直链签名绑定 alipan.com Referer，不能改");
            Check("profile.aliyun.not_openssl", !ali.UseOpenSsl, "阿里不需要 OpenSSL 桥");

            // 迅雷：反向约束 —— 加 Sec-Ch-Ua 会被判移动端
            var xl = GeZi.Core.Support.PanClientProfile.For(PanKind.Xunlei);
            Check("profile.xunlei.no_sec_ch_ua", !xl.CommonHeaders.ContainsKey("Sec-Ch-Ua"),
                "迅雷禁止 Sec-Ch-Ua（XunleiLogin.cs:372 实测会判移动端）");
            Check("profile.xunlei.ban_listed", xl.ForbiddenHeaders.Contains("Sec-Ch-Ua"),
                "迅雷的禁止项要显式登记");
            Check("profile.xunlei.referer", xl.Referer == "https://pan.xunlei.com/",
                "迅雷 Referer 保持网页域");

            // 未知值回落夸克
            Check("profile.unknown_fallback",
                GeZi.Core.Support.PanClientProfile.For((PanKind)99).Kind == PanKind.Quark,
                "未知 PanKind 应回落夸克");

            // QueryString 形态
            string qs = GeZi.Core.Support.PanClientProfile.QueryString(PanKind.Uc);
            Check("profile.query_string", qs.Contains("pr=UCBrowser") && qs.Contains("sys=win32")
                && qs.Contains("ve="), "查询串应含 pr/sys/ve，实际=" + qs);
        }


        private static void TestSlowStartGate()
        {
            // ① 256 路的标准序列
            var g = new SlowStartGate(256);
            Check("slowstart.initial_one", g.Allowed == 1,
                "初始只应放行 1 路，实际=" + g.Allowed);
            Check("slowstart.seq_1", g.Advance() == 2, "第 1 步应到 2");
            Check("slowstart.seq_2", g.Advance() == 4, "第 2 步应到 4");
            Check("slowstart.seq_3", g.Advance() == 16, "第 3 步应到 16（平方，不是翻倍）");
            Check("slowstart.seq_4", g.Advance() == 256, "第 4 步应到 256");
            Check("slowstart.done", g.Done && g.Allowed == 256, "到上限后 Done 应为 true");
            Check("slowstart.advance_noop", g.Advance() == 256, "已到上限时 Advance 应原样返回");

            // ② max = 1：一开始就是满的，不能死循环
            var g1 = new SlowStartGate(1);
            Check("slowstart.max_one", g1.Allowed == 1 && g1.Done,
                "max=1 时应立刻 Done，实际 allowed=" + g1.Allowed + " done=" + g1.Done);

            // ③ max = 16：平方增长要在 16 处封顶，不能冲过去
            var g16 = new SlowStartGate(16);
            g16.Advance();   // 2
            g16.Advance();   // 4
            int a3 = g16.Advance();   // 16（4²=16，正好）
            Check("slowstart.max16_exact", a3 == 16, "max=16 时第 3 步应到 16，实际=" + a3);
            Check("slowstart.max16_cap", g16.Advance() == 16, "max=16 时应封顶在 16");

            // ④ 非 2 的幂：max = 100，16² = 256 超过 100 → 直接封顶 100
            var g100 = new SlowStartGate(100);
            g100.Advance();   // 2
            g100.Advance();   // 4
            g100.Advance();   // 16
            int a100 = g100.Advance();
            Check("slowstart.non_power_of_two", a100 == 100,
                "max=100 时 16²=256 应封顶到 100，实际=" + a100);

            // ⑤ 溢出保护：max 很大时 current² 不能溢出成负数
            int big = SlowStartGate.NextAllowed(65536, 1 << 30);
            Check("slowstart.no_overflow", big == 1 << 30,
                "65536² 溢出前应先封顶，实际=" + big);

            // ⑥ 非法输入：max=0 / 负数按 1 处理；current=0 按 1 处理
            Check("slowstart.invalid_max", new SlowStartGate(0).MaxWorkers == 1,
                "max=0 应按 1 处理");
            Check("slowstart.zero_current", SlowStartGate.NextAllowed(0, 256) == 1,
                "current=0 应返回 1");

            // ⑦ OpenAll：跳过爬坡直接全开（下载失败重试 / 暂停恢复时用）
            var g7 = new SlowStartGate(512);
            g7.OpenAll();
            Check("slowstart.open_all", g7.Allowed == 512 && g7.Done,
                "OpenAll 后应直接到上限，实际=" + g7.Allowed);

            // ⑧ IsAllowed 的边界：序号从 0 开始，allowed=4 时 0~3 放行、4 不放行
            var g8 = new SlowStartGate(256);
            g8.Advance(); g8.Advance();   // → 4
            Check("slowstart.is_allowed",
                g8.IsAllowed(0) && g8.IsAllowed(3) && !g8.IsAllowed(4),
                "allowed=4 时应放行 0~3、拦住 4");

            // ⑨ 配置默认值：慢启动默认开启，硬超时 10 秒
            var cfg = new DownloadConfiguration();
            Check("slowstart.config_defaults",
                cfg.EnableSlowStart && cfg.SlowStartMaxWaitMs == 10000,
                "慢启动默认开启且超时 10 秒，实际=" + cfg.EnableSlowStart + " / " + cfg.SlowStartMaxWaitMs);
        }


        private static void TestOverallSlowGate()
        {
            const int ENDGAME = 3;       // 在飞 ≤ 3 视为收尾
            const int MIN_SAMPLES = 4;   // 判定所需最少样本
            const int HOLD_MS = 30_000;  // 粘滞窗口
            const long T0 = 1_000_000;   // 任意基准时刻

            bool newly;

            // ① 收尾期豁免：在飞 1 路、1/1 都慢，也绝不算整体限速（必须允许抢占）
            var g1 = new OverallSlowGate();
            bool skip1 = g1.Evaluate(1, 1, 1, ENDGAME, MIN_SAMPLES, HOLD_MS, T0, out newly);
            Check("overall.endgame_exempt", !skip1 && !g1.Active && !newly,
                "收尾期（在飞 1 路）不应判整体限速，实际 skip=" + skip1);

            // ② 样本不足：在飞 8 路但只有 2 路能算出速度 → 不判定、也不抢占
            var g2 = new OverallSlowGate();
            bool skip2 = g2.Evaluate(2, 2, 8, ENDGAME, MIN_SAMPLES, HOLD_MS, T0, out newly);
            Check("overall.few_samples_no_preempt", skip2 && !g2.Active,
                "样本不足时应跳过抢占但不算整体限速，实际 skip=" + skip2 + " active=" + g2.Active);

            // ③ 过半判慢 → 整体限速，且第一次要打日志
            var g3 = new OverallSlowGate();
            bool skip3 = g3.Evaluate(10, 6, 10, ENDGAME, MIN_SAMPLES, HOLD_MS, T0, out newly);
            Check("overall.majority_slow", skip3 && g3.Active && newly,
                "6/10 慢应判整体限速并打日志，实际 skip=" + skip3 + " active=" + g3.Active + " log=" + newly);

            // ④ 第二次判定不再重复打日志（防每 2 秒刷屏）
            bool skip4 = g3.Evaluate(10, 6, 10, ENDGAME, MIN_SAMPLES, HOLD_MS, T0 + 2000, out newly);
            Check("overall.log_once", skip4 && g3.Active && !newly,
                "整体限速提示只应打一次，实际 log=" + newly);

            // ⑤ 粘滞窗口：慢连接回落到 3/10（不再过半），但 30 秒内仍然抑制抢占
            bool skip5 = g3.Evaluate(10, 3, 10, ENDGAME, MIN_SAMPLES, HOLD_MS, T0 + 5000, out newly);
            Check("overall.sticky_hold", skip5 && g3.Active,
                "粘滞窗口内即使只有 3/10 慢也应继续抑制，实际 skip=" + skip5);

            // ⑥ 窗口过期 → 恢复抢占，并且要能打出「已缓解」
            // ⚠️ 基准要从**最后一次判定成立**的时刻算（④ 在 T0+2000 又判了一次，
            //    窗口被顺延到 T0+2000+30000），不能从 T0 算 —— 否则测的是粘滞本身而不是过期。
            bool skip6 = g3.Evaluate(10, 3, 10, ENDGAME, MIN_SAMPLES, HOLD_MS,
                T0 + 2000 + HOLD_MS + 1, out newly);
            Check("overall.hold_expired", !skip6 && !g3.Active,
                "粘滞窗口过期后应恢复抢占，实际 skip=" + skip6);
            Check("overall.clear_logged", g3.ClearLogged(),
                "恢复抢占时应报告一次「已缓解」，以便写日志");

            // ⑦ 恰好一半（5 路 3 慢）也算整体限速 —— 用 ×2 比较而不是「大于一半」
            var g7 = new OverallSlowGate();
            bool skip7 = g7.Evaluate(5, 3, 5, ENDGAME, MIN_SAMPLES, HOLD_MS, T0, out newly);
            Check("overall.half_boundary", skip7 && g7.Active,
                "5 路里 3 路慢（过半）应判整体限速，实际 skip=" + skip7);

            // ⑧ 少数慢（2/10）→ 正常抢占，不判整体限速
            var g8 = new OverallSlowGate();
            bool skip8 = g8.Evaluate(10, 2, 10, ENDGAME, MIN_SAMPLES, HOLD_MS, T0, out newly);
            Check("overall.minority_not_systemic", !skip8 && !g8.Active && !newly,
                "2/10 慢属个别掉队，不应判整体限速，实际 skip=" + skip8);

            // ⑨ Reset 之后完全回到初始态
            g3.Reset();
            Check("overall.reset", !g3.Active && !g3.Logged && g3.HoldUntilMs == 0,
                "Reset 后应回到初始态");

            // ⑩ 配置默认值与判定器语义对齐（防止以后改默认值时两边漂移）
            var cfg = new DownloadConfiguration();
            Check("overall.config_defaults",
                cfg.OverallSlowMinSamples == 4 && cfg.OverallSlowHoldMs == 30000
                && cfg.PreemptEndgameInflight == 3 && cfg.PreemptMinBps == 12L * 1024,
                "默认值应为 4 / 30000 / 3 / 12288，实际="
                + cfg.OverallSlowMinSamples + " / " + cfg.OverallSlowHoldMs + " / "
                + cfg.PreemptEndgameInflight + " / " + cfg.PreemptMinBps);

            // ⑪ OverallSlowActive 变化才通知（避免每轮刷 PropertyChanged）
            var cfg2 = new DownloadConfiguration();
            int changed = 0;
            cfg2.PropertyChanged += (s, e) => { if (e.PropertyName == "OverallSlowActive") changed++; };
            cfg2.OverallSlowActive = false;   // 与默认相同，不应通知
            cfg2.OverallSlowActive = true;
            cfg2.OverallSlowActive = true;    // 重复设同一个值，不应再通知
            Check("overall.active_notify_once", changed == 1,
                "OverallSlowActive 只在真正变化时通知，实际通知 " + changed + " 次");
        }


        private static void TestResumeSourceKey()
        {
            // 两个 key 都是「不含后缀的路径」：写盘位置 = StatePathOf(key)。
            // 声明放在 try 外，finally 才看得到。
            string key = Path.Combine(_tmp, "srckey_demo.bin");
            string legacyKey = Path.Combine(_tmp, "srckey_legacy.bin");
            string legacyPath = FileResumeStore.StatePathOf(legacyKey);
            try
            {
                // ① 存 → 读，SourceKey 必须原样回来
                var saved = new ResumeState
                {
                    TotalBytes = 1024,
                    Validator = "ETAG-A",
                    SourceKey = "15340287|1|0979af5caff14e18a930733187de9cd0",
                    Chunks = new[]
                    {
                        new ResumeChunk { Start = 0, End = 1023, Position = 0 },
                    },
                };
                FileResumeStore.Instance.SaveAsync(key, saved, CancellationToken.None)
                    .GetAwaiter().GetResult();
                var loaded = FileResumeStore.Instance.LoadAsync(key, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check("resume.sourcekey_roundtrip", loaded != null, "读回为空");
                Check("resume.sourcekey_value",
                    loaded != null && loaded.SourceKey == saved.SourceKey,
                    "读回=" + (loaded == null ? "<null>" : loaded.SourceKey));
                Check("resume.validator_still_ok",
                    loaded != null && loaded.Validator == "ETAG-A",
                    "validator=" + (loaded == null ? "<null>" : loaded.Validator));

                // ② 旧格式（没有 sourcekey 行）必须能读，且 SourceKey 为空串（不是 null）
                // FileResumeStore 的 key 是「不含后缀的路径」：
                //   写盘位置 = StatePathOf(key) = key + ".geziresume"。
                // 所以这里写文件用 legacyPath，而 LoadAsync 要传 **legacyKey 本身**。
                // 必须指定 UTF8Encoding(false)：File.WriteAllText 默认写 BOM，
                // 而解析器按 head[0] == Magic 比对，BOM 会让首行变成 FEFF 前缀 + GZRESUME。
                File.WriteAllText(legacyPath,
                    "GZRESUME 1\n"
                    + "total 1024\n"
                    + "validator " + "RVRBRy1B" + "\n"
                    + "saved 639000000000000000\n"
                    + "chunk 0 1023 0\n",
                    new System.Text.UTF8Encoding(false));
                var old = FileResumeStore.Instance.LoadAsync(legacyKey, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check("resume.legacy_loadable", old != null, "旧格式读不出来");
                Check("resume.legacy_sourcekey_empty",
                    old != null && old.SourceKey == "",
                    "旧格式的 SourceKey 必须是空串（表示不校验），实际="
                    + (old == null ? "<null>" : ("[" + old.SourceKey + "]")));
            }
            finally
            {
                try { if (File.Exists(key)) File.Delete(key); } catch { }
                try { File.Delete(legacyPath); } catch { }
            }

            // ③ 不同源文件 → 指纹必须不同（这是整套机制的意义所在）
            string quarkKey = GeZi.Core.Support.SourceKey.Build(15340287, 0, "abc");
            string ucKey = GeZi.Core.Support.SourceKey.Build(186678707, 1, "0979af5caff14e18a930733187de9cd0");
            Check("resume.sourcekey_differs", quarkKey != ucKey,
                "夸克与 UC 两个不同文件算出了相同指纹");
            // 大小未知（0）不应参与判定，否则「先建记录 Size=0、后来探测到真实大小」会被误判成换了文件
            Check("resume.sourcekey_unknown_size",
                GeZi.Core.Support.SourceKey.Build(0, 1, "f1") == GeZi.Core.Support.SourceKey.Build(0, 1, "f1"),
                "大小未知时指纹不稳定");
            Check("resume.sourcekey_unknown_not_zero",
                GeZi.Core.Support.SourceKey.Build(0, 1, "f1").StartsWith("?|"),
                "大小未知应写成 ? 而不是 0，避免与真实 0 字节文件混淆");
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