# 优化方案实施状态

对照《鸽子下载-全面优化方案报告》的 P0/P1/P2/P3，逐项记录**已完成 / 部分完成 / 未完成**，
便于下一位维护者接手。所有改动都遵循原来的红线：不改 256 默认并发、
不改 `gcServer=false`、不重写下载引擎、保留 `WriteMode.SharedFile` 枚举成员。

## P0 信任底线

| 项 | 状态 | 落地位置与行为 |
|---|---|---|
| P0-1 TLS 校验 | ✅ 完成 | `OpenSslTls` 使用 `SSL_VERIFY_PEER` + `SSL_set1_host` + 校验后检查 `SSL_get_verify_result`；CA 来源为系统存储导出的 `cacert.pem`（`%LOCALAPPDATA%\GeZi` → 程序目录 → `%TEMP%`）；`GEZI_CA_BUNDLE` 只覆盖路径，不提供"忽略证书"开关 |
| P0-2 内容哈希 | ✅ 完成（方式不同） | `QuarkClient` 读取 `md5/sha1/sha256`（含 `file_*` 别名）；`SegmentedDownloader` 完成前按 SHA-256 → SHA-1 → MD5 校验；**失败保留分片与 `.qmeta` 供续传**；接口未给哈希时明确标记"未校验"；哈希随待续传任务持久化 |
| P0-3 `.qmeta` 事务化 | ✅ 完成 | v2 格式（魔数 `GEZI-QMETA 2` + `total=` / `count=`）；**能读 v1、只写 v2**；主文件损坏时回退 `.bak`；保留上一份 `.bak`；写入为「锁内快照 + 唯一临时文件 + `Flush(true)` + `File.Replace`」 |
| P0-4 恢复预算 | ✅ 完成 | `DownloadScheduler` 内**进程级静态连接租约**（512），任务按需申请、结束释放；跨批次、跨调度器实例都不超上限 |
| P0-5 任务感知清扫 | ✅ 完成 | `PartsLease` 记录 `pid/state/heartbeatUtc/expiresUtc`；下载期间每 5 秒刷新心跳；本进程持有的租约永久有效，进程退出后按 30 天宽限；旧格式（单行过期时间）仍可解析 |

## P1 韧性

| 项 | 状态 | 落地位置与行为 |
|---|---|---|
| P1-1 探测状态机 | ✅ 完成 | `ProbeAsync` 分类为 RangeSupported / LengthOnly / AuthFailed / NotFound / ServerError / NetworkError；5xx/429/网络异常最多重试 3 次（300ms 退避），401/403/404/410 立即返回不浪费请求 |
| P1-2 原子写 | ✅ 完成 | `Settings` / `AccountStore` / `HistoryStore` 全部为「唯一临时文件 + `Flush(true)` + `File.Replace`」 |
| P1-3 DPAPI fail-closed | ✅ 完成 | 加密失败抛 `SecretProtectionException`，绝不落明文；额外熵来自独立 `%LOCALAPPDATA%\GeZi\secret.key`（ACL 收紧到当前用户），可回退内置熵解老密文；状态文件里的 `Url/Token/Stoken/Passcode` 同样 DPAPI 加密，旧明文自动兼容 |
| P1-4 路径 / 长路径预检 | ✅ 完成 | `PathUtil.TryPreflight` 检查路径长度、保留设备名、非法段、目录可创建与可写；`app.manifest` 增加 `longPathAware`，`App.config` 增加 `UseLegacyPathHandling=false` / `BlockLongPaths=false` |
| P1-5 自适应降档 | ✅ 完成 | 限流时并发减半（256→128→64→32，下限 32），静默 60 秒才允许翻倍回升（迟滞 + 冷却） |
| P1-6 链接刷新状态 | ✅ 完成 | `LinkRefreshReason` 枚举（401/403/412）替代字符串包含判断，日志打印判定原因 |
| P1-7 取消时的文件处理 | ✅ 完成 | 目标文件先改名为 `*.gezi-deleting` 再删除；删不掉就留给下次启动的 `SweepPendingDeletes` 收尾；残留枚举同步覆盖该后缀 |

## P2 工程化

| 项 | 状态 | 说明 |
|---|---|---|
| P2-1 `MainWindow` 拆分 | ❌ 未做 | 仍是 7,600+ 行单体（92 处空 `catch {}`、20 处 `async void`）。拆分属于高风险重构，需要「绞杀者 + 行为不变提交 + golden 测试」，建议单独排期 |
| P2-2 测试与 CI | ✅ 完成 | `tests/GeZi.SelfTest`（零 NuGet，控制台断言，**28 项全绿**）+ `.github/workflows/build.yml`（0 警告 0 错误 + 自测 + 打包） |
| P2-3 soak / benchmark | ⚠️ 部分 | 已有可脚本化的 Range 服务器冒烟脚手架（仓库外），但没有 6 小时压测与性能基线回归 |
| P2-4 诊断包 | ✅ 完成 | `DiagnosticsPack`（info.txt + runtime.log + README，**不含 Cookie/票据/直链**）+ 日志面板「导出诊断包」按钮 |

## P3 交付与社区

| 项 | 状态 | 说明 |
|---|---|---|
| 校验和 / SBOM / PDB / LICENSE 随包 | ✅ 完成 | `tools/package_release.py`：白名单打包 + `SHA256SUMS.txt` + `<zip>.sha256` + CycloneDX `sbom.json` + 随包 `LICENSE` / `THIRD-PARTY-NOTICES.md` / PDB |
| 代码签名 | ❌ 未做 | 需要 OV/EV 证书与时间戳服务，纯外部依赖；步骤已写入 `docs/RELEASE-CHECKLIST.md` 第 4 节 |
| README / docs | ✅ 完成 | README 增补构建-测试-打包章节与新目录结构；新增 `docs/TESTING.md`、`docs/RELEASE-CHECKLIST.md`、本文件 |

## 仍剩余的小项（评审报告提及）

这些不影响"信任版"闭环，但都在待办里：

1. `LinkQuality` 只给 https 加分，**不拒绝** http 直链。
2. `QuarkClient` 用共享 `static Random`；`QuarkSession` 每请求 `new Random`（并发下偏弱）。
3. `PauseToken` 仍是 100ms 轮询，可改为事件唤醒。
4. `LoginQrService` 仍会提示"未找到可用的 Python"（实际早已改走 OpenSSLTLS，属历史文案）。
5. `CollectFilesAsync` 对子目录失败静默 `continue`，没有把跳过的目录回显给用户。
6. `.qmeta` 有版本号但没有"影子写/回滚开关"（当前靠 `.bak` 回退）。
7. 哈希校验是"完成后整文件读一遍"，不是报告建议的"合并时增量计算"；大文件会多一次全盘读。
8. UI 未给"未校验 / 校验失败"独立的视觉徽标（目前显示在任务详情文本里）。

## 验证证据（本次改动）

- 构建：`dotnet build GeZi.slnx -c Release -t:Rebuild` → **0 警告 / 0 错误**。
- 自测：`GeZi.SelfTest.exe` → 28 项 PASS，`SELFTEST_OK`，退出码 0。
- 冒烟（仓库外脚手架）：哈希正常/错误/重试/`.qmeta` 保留、租约保护与清扫、`pan.quark.cn` TLS 200。
- 打包：13 个白名单文件 + 校验和 + SBOM + zip 成功生成。

> 状态：以上改动位于开发分支工作区，**尚未提交、尚未部署**；
> 部署前请确认无 GeZi 进程运行，并按 `docs/RELEASE-CHECKLIST.md` 复核。