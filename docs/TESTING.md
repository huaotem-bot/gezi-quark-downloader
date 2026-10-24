# 测试与质量门禁

本项目约定：**零 NuGet、离线可构建、可复现**。所以测试不引入 xUnit/NUnit，
而是一个零依赖的控制台断言宿主 —— clone 下来直接跑，不联网。

## 一条命令跑完

```bash
dotnet build GeZi.slnx -c Release -t:Rebuild
dotnet run --project tests/GeZi.SelfTest/GeZi.SelfTest.csproj -c Release
```

- 退出码 `0` = 全部通过；`1` = 有失败（失败行以 `FAIL ` 前缀输出，便于 grep）。
- 输出末尾固定一行 `SELFTEST_OK` 或 `SELFTEST_FAILED count=N`。

## 当前覆盖范围

| 用例组 | 覆盖内容 | 为什么值得测 |
|---|---|---|
| `alloc.*` | `ThreadAllocator.Allocate` 的单文件上限、等大平分、下限保护、越界回填 | 连接预算是「256 路甜点」的核心，改一行就影响所有人 |
| `path.*` | 非法字符、Windows 保留名（`CON`/`NUL`/`COM1`…）、下载前预检 | 网盘文件名千奇百怪，落盘失败往往发生在几十分钟之后 |
| `lease.*` | 租约心跳、过期判定、旧格式兼容、释放 | 清扫器误删断点是「暂停 7 天后全没了」的元凶 |
| `secret.*` | DPAPI 加解密、旧明文兼容、幂等、字段级接口 | 登录态与取链凭证不能落明文 |
| `pending.*` | `*.gezi-deleting` 残留清理 | 取消下载时删不掉的文件必须能被下次启动收尾 |
| `scheduler.budget` | 全局连接预算上限与占用计数 | 多批次重叠时不能超开 |
| `diag.*` | 诊断包 zip 结构、设置摘要写入、不含机密 | 反馈问题时用户只需要点一个按钮 |
| `pan.*`（45 项） | 四网盘链接识别（含域名伪装拒绝、`pwd1234` 陷阱、子目录 fid）、能力声明、实例创建、未登录判定、迅雷缺验证码提示、夸克适配器、**夸克/UC 凭证不混用** | 新增网盘里唯一能「离线证明正确」的部分，必须扎实 |
| `map.*`（8 项） | `PanFileEntry` ↔ `QuarkFileEntry` 字段搬运、目录标记、令牌往返、null 容错 | 多网盘与现有 UI 之间唯一的接缝，错一个字段就表现为"文件夹点不进去" |
| `uc.*` / `ali.*` / `xl.*`（31 项） | **契约测试**：本地 mock 服务器扮演后端，验证请求路径、CSRF 头、share-token 头、captcha 头、`device_id`、请求体字段，以及响应解析成 `DownloadLink` 是否正确 | 不需要账号就能确定性地证明协议实现没写错 |

### 契约测试是怎么做的

`MockHttpServer.cs` 用 `TcpListener` 起一个极简 HTTP/1.1 服务器
（**不用** `HttpListener` —— 它需要 URL ACL，非管理员注册不了，CI 里会挂）。
三个客户端都支持把 base URL 指向它，于是：

```csharp
using (var uc = new UcDriveClient("__puus=fake", server.BaseUrl))
{
    var files = uc.ListShareAsync(shareRef, null).GetAwaiter().GetResult();
    // 然后断言 server.Requests 里每个请求的路径/头/体
}
```

这套测试已经抓出过一个真 bug（UC 的 CSRF token 取法太脆弱，见 MULTI-PAN.md）。

## 真实账号探针（`pan-probe`）

自测不碰网络。真实取链必须用真实账号，用同一个 exe 的探针模式：

```bash
GeZi.SelfTest.exe pan-probe uc     "<分享链接>" "<__puus 的值>"
GeZi.SelfTest.exe pan-probe aliyun "<分享链接>" "<refresh_token>"
GeZi.SelfTest.exe pan-probe xunlei "<分享链接>" "<pan_token>" "<x-captcha-token>"
```

退出码：`0` 成功 / `2` 参数错误 / `3` 识别或登录失败 / `4` 列目录失败 / `5` 取直链失败。
成功时末尾打印 `PROBE_OK`；输出里的直链只显示主机名与前 80 字符，不会把可下载地址泄进日志。

详见 [MULTI-PAN.md](MULTI-PAN.md)（含凭证从哪拿、以及各项的验证状态）。

## 本地环境注意事项

- 自测需要**可写的临时目录**：优先 `%TEMP%`，不可写时自动回退到当前工作目录。
- 受限沙箱里 `%LOCALAPPDATA%\GeZi` 可能不可写，此时 `SecretProtector` 会回退到内置熵
  （`secret.*` 用例仍会通过，只是不生成独立密钥文件）。
- 涉及真实网络与 CDN 的用例（吞吐、直链刷新、合并、各网盘取链）**不在自测里** —— 它们依赖账号与
  线上策略，适合放在手工回归清单与 `pan-probe` 中执行。

## 手工回归清单（发版前）

1. 大文件（≥ 1GB）完整下载 + 暂停/继续 + 取消后清理。
2. 多任务（≥ 3 个）同时下载，观察日志里的连接分配与总并发是否 ≤ 512。
3. 断点续传：关闭程序后再启动，确认从断点继续而不是重下。
4. 扫码登录（真实账号）：确认 `pan.quark.cn` 证书校验通过；拒绝自签名证书。
5. 诊断包：导出后解压，确认只有 `info.txt`、`runtime.log`、`README.txt`。
6. 四个网盘各跑一次 `pan-probe`，确认 `PROBE_OK`。

## CI

`.github/workflows/build.yml` 在 windows-latest 上执行：

1. `dotnet build GeZi.slnx -c Release -t:Rebuild -p:TreatWarningsAsErrors=true`
2. `dotnet run --project tests/GeZi.SelfTest/...`
3. `python tools/package_release.py --from-build --out artifacts`（产物随 workflow 上传）

任何一步失败即红线，不允许「先合并再修」。