# 发布清单

目标：**用户能验证自己下载的是官方包，且能追溯来源。**
每一项都要能复核，不能只有一句「已发布」。

## 1. 构建

```bash
dotnet build GeZi.slnx -c Release -t:Rebuild -nodeReuse:false -m:1 -p:UseSharedCompilation=false
```

- [ ] 输出必须 **0 警告 / 0 错误**（CI 已用 `-p:TreatWarningsAsErrors=true` 强制）。
- [ ] `GeZi.csproj` 的 `<Version>` 与 `app.manifest` 的 `assemblyIdentity version`、`<Version>` 三者一致。

## 2. 打包

```bash
python tools/package_release.py --from-build --out artifacts
```

产物：

- [ ] `GeZi-v<版本>-win-x64.zip`（白名单文件 + LICENSE + THIRD-PARTY-NOTICES.md + README.md + PDB）
- [ ] `SHA256SUMS.txt`（包内每个文件的 SHA-256）
- [ ] `<zip>.sha256`（zip 自身的 SHA-256，贴到 Release 说明里）
- [ ] `sbom.json`（CycloneDX 1.5，含组件与版本）

## 3. 发布页

- [ ] Release 说明写清：版本号、日期、主要变更、已知问题、**SHA-256**。
- [ ] 说明与代码行为一致（不写没有的功能）。
- [ ] 标注需要 .NET Framework 4.8（或系统自动引导安装）。
- [ ] 附 `LICENSE` 与 `THIRD-PARTY-NOTICES.md`（AGPL-3.0 合规：分发必须带许可证与源码指引）。

## 4. 代码签名（**当前未完成**）

- [ ] 准备代码签名证书（OV/EV）与时间戳服务。
- [ ] 在 CI 中对 `GeZi.exe` 与 `GeZi.Core.dll` 执行 `signtool sign /fd SHA256 /tr <ts> /td SHA256`。
- [ ] 签名后重新生成 `SHA256SUMS.txt` 与 `<zip>.sha256`（签名会改字节）。
- [ ] Release 说明里给出证书指纹，便于用户核对。

> 未签名不影响使用；但 Windows SmartScreen 会提示未知发布者，
> 且用户无法用系统机制验证来源 —— 这就是本项列为待办的原因。

## 5. 合规与供应链

- [ ] 升级 ZXing / OpenSSL 时同步更新 `THIRD-PARTY-NOTICES.md` 的版本与许可证。
- [ ] `lib/OpenSSL/` 与 `lib/ZXing/` 里的二进制保持可复现来源（README 记录获取方式）。
- [ ] 新增原生依赖时同步修改 `tools/package_release.py` 的白名单，否则发布包会缺 DLL。

## 6. 回滚

- [ ] 保留上一版 zip 与哈希，出现严重回归时能直接撤回 Release。
- [ ] 版本号只增不减；修复版走 patch 号（例如 2.3.2）。