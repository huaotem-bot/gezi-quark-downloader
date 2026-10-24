# 多网盘支持（夸克 / UC / 阿里云盘 / 迅雷云盘）

本文记录四个网盘的接入状态、**实测结论**、以及如何用自己的账号验证。
所有结论都来自真实请求，不是推测。

## 一、架构

```
IPanProvider（统一契约，只抽"取直链"这一条链路）
├── QuarkProviderAdapter  → 包装已验证的 QuarkClient（不重写）
├── UcDriveClient         → drive.uc.cn
├── AliyunClient          → api.aliyundrive.com
└── XunleiClient          → api-pan.xunlei.com
        ↓ 都产出
   List<DownloadLink>
        ↓ 交给
   SegmentedDownloader（现有 256 并发引擎，完全不动）
```

**关键设计**：新增网盘只影响 API 层，不碰下载引擎。
这是刻意为之 —— 那套分片/续传/降档逻辑是项目最值钱的资产，不该为了加网盘去动它。

相关文件：

| 文件 | 作用 |
|---|---|
| `src/GeZi.Core/Api/PanProvider.cs` | `PanKind` / `PanShareRef` / `PanFileEntry` / `PanCapabilities` / `IPanProvider` |
| `src/GeZi.Core/Api/PanUrlParser.cs` | 四网盘分享链接识别 |
| `src/GeZi.Core/Api/PanProviderRegistry.cs` | 注册表 + 能力声明 + 凭证提示 |
| `src/GeZi.Core/Api/QuarkProviderAdapter.cs` | 夸克适配器 |
| `src/GeZi.Core/Api/UcDriveClient.cs` | UC 网盘 |
| `src/GeZi.Core/Api/AliyunClient.cs` | 阿里云盘 |
| `src/GeZi.Core/Api/XunleiClient.cs` | 迅雷云盘 |
| `src/GeZi.Core/Api/PanJson.cs` | 零依赖 JSON 宽容取值 |

## 二、免转存怎么实现

「免转存」= 不把文件保存到自己网盘，直接拿下载直链。四个网盘的实现方式：

| 网盘 | 取链接口 | 需要的凭证 |
|---|---|---|
| 夸克 | `POST /1/clouddrive/file/download`（`pwd_id` + `stoken` + `fids_token`） | Cookie（`__puus`） |
| UC | `POST /1/clouddrive/file/download`（同夸克） | Cookie（`__puus`）+ **CSRF token** |
| 阿里 | `POST /v2/file/get_share_link_download_url`（`x-share-token` 头） | `refresh_token` |
| 迅雷 | `GET /drive/v1/files/{id}` → `web_content_link` | `pan_token` + **`x-captcha-token`** |

## 三、实测结论（重要）

### 3.1 UC 的 API 主机不是 drive.uc.cn（**最关键的一条**）

实测请求同一路径 `/1/clouddrive/share/sharepage/token`：

```
https://drive.uc.cn   → HTTP 200 + SPA 的 index.html（不是 JSON！）
https://pc-api.uc.cn  → HTTP 404 {"status":404,"code":41006,"message":"分享不存在"}
```

**UC 的网页前端在 `drive.uc.cn`，业务接口在 `pc-api.uc.cn`。**
请求 `drive.uc.cn/1/clouddrive/...` 会命中 SPA 的 fallback 路由、返回 index.html，
表现为"接口返回了无法解析的内容"——极难从现象反推出是主机错了。

三条路径都在 `pc-api.uc.cn` 上验证通过（`share_id` 用假值）：

| 接口 | 返回 | 说明 |
|---|---|---|
| `sharepage/token` | `{"code":41006,"message":"分享不存在"}` | 路径 + 请求体格式正确 |
| `sharepage/detail` | `{"code":14001,"message":"Bad Parameter: [分享链接不存在]"}` | 路径正确 |
| `file/download` | `{"code":14001,...}` | 路径正确 |

### 3.2 UC 不需要 CSRF（`pc-api` 上）

```
pc-api.uc.cn 不带 x-csrf-token → 正常返回业务错误 ✅
drive.uc.cn  不带 x-csrf-token → 403 {"message":"missing csrf token"}
```

要求 CSRF 的是**网页主机**的前端路由，不是我们要调的接口。
所以客户端不再为 CSRF 去访问首页（省一次请求），
只从用户粘贴的 Cookie 里捡一个 `ctoken`（有就带上，没有也不影响）。

### 3.3 迅雷必需 device_id

```
不带 device_id → {"error":"invalid_argument","detail":"device_id is empty"}
带 device_id   → {"error":"captcha_invalid","detail":"captcha_token is ..."}
```

加 `device_id` 后错误从"缺参数"推进到"验证码无效"——
证明参数链路上 `device_id` 在 `captcha_token` 之前校验，且它是必需的。
客户端现在会生成一个稳定的设备 ID（同一进程内复用，避免每次换设备触发风控），
并随 `share` / `files` 请求一起发送。

### 3.4 阿里：列目录路径修正；取链端点的 410 是令牌问题，不是下线

实测结果（`share_id` / `share_token` 都用假值）：

| 接口 | 结果 | 结论 |
|---|---|---|
| `/adrive/v3/share_link/get_share_by_anonymous` | `{"code":"NotFound.ShareLink"}` | ✅ 路径正确 |
| `/adrive/v2/file/list_by_anonymous` | `{"message":"uri(/v2/file/list_by_anonymous) not exist"}` | ❌ **路径不存在** |
| `/adrive/v2/file/list?share_id=...` | `{"code":"ShareLinkTokenInvalid"}` | ✅ **正确路径**（只差有效 token） |
| `/v2/file/get_share_link_download_url` | **HTTP 410 Gone**（空响应体） | ⚠️ 见下方说明 |
| `/v2/file/get_download_url` | `{"code":"AccessTokenInvalid","message":"access token is blank"}` | ✅ 存在，要 **access_token** |

**列目录路径已修正**为 `/adrive/v2/file/list`（原来写的 `list_by_anonymous` 根本不存在）。

**关于那个 410：** 我一开始判断是"阿里下线了这个端点"，**这个结论不成立**。
交叉验证：LinkSwift（仍在维护的项目）的 `config/ali.json` 里，`pcs` 字段写的就是
`https://api.aliyundrive.com/v2/file/get_share_link_download_url` —— 和本实现完全一致。

同一台主机对 `/adrive/v2/file/list` 返回的是规范的
`401 {"code":"ShareLinkTokenInvalid"}`，而对取链端点返回空体 410，
更合理的解释是**该端点对无效令牌/可疑请求返回 410**（或触发了反爬），
而不是接口消失。

所以实现保持原路径，并加了一条兜底：主路径失败时改用需要 `access_token` 的
`get_download_url`（覆盖"已转存到自己网盘"的场景）。两条都失败才报错。

> 这一步必须用**真实分享链接 + 真实账号**才能定论。我不会在没证据的情况下
> 改端点或宣称接口下线。

### 3.3 三个新网盘的域名在 .NET 默认 TLS 下都可达

```
drive.uc.cn            → HTTP 200  ✅
pan.xunlei.com         → HTTP 200  ✅
api-pan.xunlei.com     → HTTP 404  ✅（可达）
api.aliyundrive.com    → HTTP 404  ✅（可达）
auth.aliyundrive.com   → HTTP 404  ✅（可达）
pan.quark.cn（对照）    → 连接被强制关闭 ❌
```

**三个新网盘都不需要 OpenSSL 桥接**，普通 `HttpClient` 就能握手。
而 `pan.quark.cn` 依然被 WAF 掐掉 —— 这正是项目当初要内置 OpenSSL 3.5.7 的原因，
也说明那套桥接只对夸克必要。

### 3.4 登录态失效的识别

UC 在登录态无效时会返回 **HTML 登录页**而不是 JSON。实现里专门识别这种情况，
给出「登录态无效或已过期，请重新登录后更新 Cookie」的提示，
而不是让用户看到「接口返回了无法解析的内容」这种无从下手的信息。

## 四、用你自己的账号验证

自测只覆盖「不需要网络就能证明」的部分（链接识别、能力声明、实例创建）。
**真实取链必须用真实账号**，用这个命令：

```bash
# UC
GeZi.SelfTest.exe pan-probe uc \
  "https://drive.uc.cn/s/xxxxxxxx?pwd=abcd" \
  "__puus=<从浏览器复制的值>"

# 阿里云盘
GeZi.SelfTest.exe pan-probe aliyun \
  "https://www.alipan.com/s/xxxxxxxx" \
  "<refresh_token>"

# 迅雷云盘（第 4 个参数是极验滑块过完后拿到的 token）
GeZi.SelfTest.exe pan-probe xunlei \
  "https://pan.xunlei.com/s/xxxxxxxx?pwd=ab12" \
  "<pan_token>" "<x-captcha-token>"
```

输出形如：

```
== 网盘: UC网盘
OK   识别: shareId=xxxxxxxx passcode=有
OK   客户端就绪，能力: 免转存=True 需验证码=False
OK   列目录: 3 项
       - 文件名.mp4  104857600 B  fid=abc123…
OK   直链: host=xxx.uc.cn size=104857600 hash=-
       url(截断)=https://…
PROBE_OK
```

退出码：`0` 成功 / `2` 参数错误 / `3` 识别或登录失败 / `4` 列目录失败 / `5` 取直链失败。

### 凭证从哪拿

| 网盘 | 位置 |
|---|---|
| UC | 登录 `drive.uc.cn` → F12 → Application → Cookies → `__puus` |
| 阿里 | 登录 `aliyundrive.com` → F12 → Application → Local Storage → `token` 里的 `refresh_token` |
| 迅雷 | 登录 `pan.xunlei.com` → F12 → Network → 任一 `api-pan.xunlei.com` 请求的 `Authorization: Bearer xxx`；`x-captcha-token` 在同请求的头里（过完滑块才有） |

## 五、验证状态（诚实标注）

| 项目 | 状态 |
|---|---|
| 四网盘链接识别（含域名伪装拒绝、`pwd1234` 陷阱） | ✅ 自测 13 项全过 |
| 能力声明 / 实例创建 / 未登录判定 / 凭证不混用 | ✅ 自测 32 项全过 |
| 夸克适配器接入统一接口 | ✅ 编译 + 自测通过 |
| UC CSRF 机制 | ✅ **实测确认**（403 → 200） |
| 阿里协议结构 | ✅ **实测确认**（服务端返回结构化业务错误） |
| 三个域名 TLS 可达性 | ✅ **实测确认** |
| 登录态失效提示 | ✅ 实测确认 |
| **UI 接线**（设置页凭证 + 解析入口分支） | ✅ 完成，启动验证通过 |
| **递归路径**（下载文件夹 / 目录大小求和） | ✅ 已分支，避免静默少收文件 |
| **续传**（重启后按来源网盘重取链） | ✅ `PanKind` 持久化，向后兼容 |
| **契约测试**（本地 mock 服务器，无账号） | ✅ 31 项，验证请求构造与响应解析 |
| **UC API 主机**（`pc-api.uc.cn`） | ✅ **实测确认**（三条路径全部被服务端接受） |
| **迅雷 device_id** | ✅ **实测确认**（加上后错误推进到验证码阶段） |
| **阿里列目录路径**（`/adrive/v2/file/list`） | ✅ **实测确认**（原路径不存在，已修正） |
| **阿里取链端点路径** | ✅ 与 LinkSwift 的 `ali.json` 交叉核对一致 |
| **UC 内嵌登录（WebView2）** | ✅ **实测通过**（加载登录页 + 读到 9 个 Cookie） |
| **UC 真实分享取链** | ⚠️ **未验证**（缺账号；协议已实测到"分享不存在"这一步） |
| **阿里真实分享取链** | ⚠️ **未验证**（缺账号；假令牌会得到 410，需真实令牌定论） |
| **迅雷真实分享取链** | ⚠️ **未验证**（缺账号 + 需验证码 token） |

## 五点半、契约测试（不需要账号的确定性验证）

自测里内置了一个极简 HTTP 服务器（`tests/GeZi.SelfTest/MockHttpServer.cs`，
基于 `TcpListener` —— 不用 `HttpListener`，因为后者需要 URL ACL，非管理员注册不了）。
三个客户端都支持把 base URL 指向它，于是可以**确定性地**验证：

| 验证点 | 网盘 |
|---|---|
| 请求路径正确（token / detail / download） | UC |
| `x-csrf-token` 头存在且值取自 `ctoken` cookie | UC |
| 请求体含 `pwd_id` + `passcode` + `stoken` + `fids` + `fids_token` | UC |
| UA 是 UC 客户端身份（不是夸克、不是浏览器） | UC |
| `x-share-token` 头出现在列目录与取链请求上 | 阿里 |
| 取链请求体是 `share_id` + `file_id`，**不含** `drive_id`（免转存的关键） | 阿里 |
| `refresh_token` 轮换被接住 | 阿里 |
| `Authorization: Bearer` + `x-captcha-token` 头 | 迅雷 |
| 分享请求带 `share_id` + `pass_code` | 迅雷 |
| 缺验证码时给出可操作报错 | 迅雷 |
| 响应解析：文件名 / 大小 / 目录标记 / 令牌 / 直链 / 哈希 | 全部 |

**这套测试当场抓出一个真 bug**：`EnsureCsrfAsync` 原本靠"Cookie 罐 + 硬编码域名列表"
取 `ctoken`，一旦服务端把 cookie 种在别的主机（或客户端指向代理/测试桩）就取不到，
表现为**所有请求都缺 CSRF**，而报错完全指不出原因。
现在改成直接从响应的 `Set-Cookie` 头解析，域名无关。

## 六、已知限制

1. **迅雷是半自动的。** `x-captcha-token` 由极验滑块生成，必须真人操作。
   本项目**不会**实现自动过验证码 —— 那既不可靠，也是在帮用户绕过服务方的安全机制。
   流程是「浏览器过验证 → 复制 token → 粘进程序」，且随时可能失效。
2. **阿里受账号等级与分享者设置限制**，部分分享可能取不到直链（服务端会明确报错）。
3. **UC 的 CSRF token 会随会话变化**，实现里做了缓存与自动获取；
   如果长时间运行后失效，重建客户端即可。
4. **三个新网盘的 UI 已接线**，但凭证需要在「设置 → 多网盘账号」里手动粘贴
   （这三个盘目前没有扫码登录）。夸克链路一行未改：`_panKind` 默认 `Quark`，
   所有分支都以 `if (IsOtherPan)` 开头，未命中时走的还是原来的代码。
5. **这三个网盘暂不支持"我的网盘"浏览**，只有分享免转存。
   切到「我的网盘」时会被挡下并说明原因 —— 否则会用夸克客户端去请求别人的网盘，
   报错信息对用户毫无意义。
6. **续传支持**：任务的 `PanKind` 随状态文件持久化（旧记录反序列化为 0 = 夸克，
   天然兼容），重启后续传会按来源网盘重建客户端取链。
   非夸克任务的下载请求**不带夸克 Cookie** —— 签名直链不需要，带上等于把
   夸克登录态发给别家 CDN。

## 六点五、UI 怎么用

1. 打开「设置」页，滚到底部找到 **「多网盘账号（UC / 阿里云盘 / 迅雷云盘）」** 卡片；
2. 按卡片里的提示粘贴对应凭证（每个输入框下方都写了去哪拿）；
3. 回到主界面，把 UC / 阿里 / 迅雷的分享链接粘进解析框，点「解析」。

程序会按域名自动判断是哪个网盘：夸克走原有链路，其余三个走 `IPanProvider`。
凭证与夸克 Cookie 一样用 DPAPI 加密落盘，重启后自动回填到输入框。

## 七、许可证

GeZi 以 **AGPL-3.0** 发布。多网盘协议实现参考了同为 AGPL-3.0 的开源项目
（[LinkSwift](https://github.com/hmjz100/LinkSwift)、
[YunX](https://github.com/CYQawa/YunX)、
[wenxi](https://github.com/z7786/wenxi)）。
AGPL-3.0 与 GPL-3.0 单向兼容（GPL 可并入 AGPL，反之不行），
所以许可证统一切到 AGPL-3.0 才能合法复用这些项目的实现经验。

## 八、版本

本次多网盘支持是功能级变更，版本从 `2.3.1` 升到 **`2.4.0`**
（`GeZi.csproj` / `GeZi.Core.csproj` / `app.manifest` / README 同步）。
发布包：`GeZi-v2.4.0-win-x64.zip`（18 个条目：15 个白名单文件 + 校验和 + SBOM）。

## 九、内嵌登录（WebView2）

### 踩过的坑：0x8007000B（位数不匹配）

第一次点「内嵌登录」直接弹：

```
内嵌登录无法启动：试图加载格式不正确的程序。
（异常来自 HRESULT:0x8007000B）
```

**根因：主程序一直是 32 位进程。**

`GeZi.csproj` 从未配置 `PlatformTarget`，默认 `AnyCPU + Prefer32Bit=true`，
在 64 位系统上**实际以 32 位运行**。而 `WebView2Loader.dll` 是 x64 的，
32 位进程加载它就会得到 `ERROR_BAD_FORMAT`。

**为什么以前没暴露：** 项目此前只依赖托管程序集；OpenSSL 那套虽也是 x64 原生 DLL，
但只在扫码登录路径按需加载，没被这样验证过。

**修复：**

```xml
<PlatformTarget>x64</PlatformTarget>
<Prefer32Bit>false</Prefer32Bit>
```

同时给 `GeZi.Core` 也补上（纯托管库本可 AnyCPU，但混用是这类错误的温床），
并**撤销**了之前"删除 x86 loader 省体积"的优化 —— WebView2 按进程位数挑 loader，
删错那份就会重演这个问题。

**修复后实测：**

```
proc_is64=True                 64 位进程
runtime=155.0.4283.62          运行时可用
ENV_OK / CORE_OK               WebView2 初始化成功
NAV success=True               导航成功
title="UC网盘-免费大空间…"      登录页正常渲染
cookie_count=9                 读到 9 个 Cookie
RESULT=OK
```

> 📌 教训：`0x8007000B` 的官方含义是 `ERROR_BAD_FORMAT`，但它**从不提示"位数不匹配"**。
> 遇到原生 DLL 加载失败且报这个码，第一件事是核对进程位数与 DLL 的 PE 头 machine 字段。

UC 没有可用的扫码登录接口（`uop.uc.cn` 域名根本不存在），
同类项目一律用「内嵌浏览器打开登录页 → 读 Cookie」：
YunX 用安卓 WebView，wenxi 的 Windows 版用 WebView2。

### 为什么不是别的方案

| 方案 | 结果 |
|---|---|
| WPF 自带 `WebBrowser` | ❌ 实测打开 `drive.uc.cn` 被跳到 `/u/upgrade`「浏览器升级」页（IE 内核被拒） |
| 读外部浏览器 Cookie 数据库 | ❌ Edge 155 的 Cookie 用 `v20` App-Bound Encryption，第三方进程解不开（这是微软专门为阻止此类窃取设计的） |
| 调用系统 Edge 浏览器 | ❌ 拿不到 Cookie（除非注入进程，那是恶意软件手法） |
| **WebView2** | ✅ **实测通过** |

### 实测验证结果

```
runtime=155.0.4283.62          运行时可用
env ok / core ok               WebView2 初始化成功
NAV success=True               导航成功
url=https://drive.uc.cn/       加载的是 UC 登录页
title="UC网盘-免费大空间…"      DOM 正常渲染（不是"浏览器升级"页）
cookie_count=9                 成功读到 9 个 Cookie
has_pus=False / has_puus=False 未登录状态（符合预期）
```

### 实现要点

| 点 | 做法 | 原因 |
|---|---|---|
| 隔离数据目录 | `%LOCALAPPDATA%\GeZi\webview2\profile-xxx` → `%TEMP%` → 程序目录（三级回退） | 不污染用户浏览器；实测 `%LOCALAPPDATA%` 可能被 ACL 拒绝 |
| 桌面 UA | 强制 Chrome/120 桌面 UA | 不伪装会拿到移动版布局，登录页没法操作 |
| Cookie 读取 | `CookieManager.GetCookiesAsync("")` | 原生 API，走 WebView 自己的实例，不碰加密数据库 |
| 自动检测 | 1.5 秒轮询 + 廉价预检（`__pus` 与 `__puus` 齐全）+ 5 秒校验节流 | 登录页是 SPA，没有明确跳转信号；预检挡掉登录中间态 |
| 失败处理 | 同一凭证校验失败后不立即重试，等它变化 | 中间态 Cookie 会反复出现，无脑重试只会打爆接口 |
| 手动兜底 | 窗口底部「手动复制 Cookie…」按钮 | 自动检测失效时（页面改版）用户仍有出路 |

### 三层降级（运行时缺失也不影响使用）

| 情况 | 表现 |
|---|---|
| Win11 / Win10 已有 Runtime | 内嵌登录开箱即用 |
| 没有 Runtime | 设置页按钮置灰 + 一行说明；**手动粘贴始终可用** |
| 想启用内嵌登录 | 提示安装（Bootstrapper 约 2 MB），不阻塞其他功能 |

检测走微软官方推荐的注册表 `pv` 值（HKLM per-machine / HKCU per-user）。

### 体积代价（实测）

| | 之前 | 之后 | 增量 |
|---|---|---|---|
| zip | 3.21 MB | **3.45 MB** | +0.24 MB |
| 解压后 | 10.76 MB | 11.58 MB | +0.82 MB |

已从包中剔除用不上的 `Microsoft.Web.WebView2.WinForms.dll` 与 x86/arm64 的 loader。

> ⚠️ **这是项目第一个也是唯一一个 NuGet 依赖**，构建时需要能还原。
> 运行时**不打包** WebView2 Runtime（那是微软随 Edge/Windows 分发的系统组件）。