# ZXing.Net（本地固化引用）

本目录下的 DLL **不作为 NuGet 包依赖**，而是以普通程序集形式随仓库分发，
这样本项目依然满足「零 NuGet、可完全离线构建」的约束 —— 任何机器 clone 下来直接 build 即可。

## 来源

| 项 | 值 |
|---|---|
| 包 | `ZXing.Net` |
| 版本 | **0.16.11** |
| 作者 | Michael Jahn |
| 许可 | **Apache-2.0**（https://licenses.nuget.org/Apache-2.0） |
| 项目地址 | https://github.com/micjahn/ZXing.Net/ |
| 取用文件 | `lib/net48/zxing.dll` → `zxing.dll`（532 KB） |
| 附带 | `zxing.xml`（XML 文档注释，IntelliSense 用，不参与编译） |

> ⚠️ **文件名一律小写**。包内本来就是小写 `zxing.dll`，而
> `GeZi.exe` 经 ProjectReference 传递复制时 MSBuild 也会规范化成小写 ——
> 源文件跟着写小写，才能保证各平台/打包工具下名字完全一致
> （Windows 大小写不敏感，大小写不一致的坑在本机看不出来）。

## 重新获取方式

```bash
curl -sL -o zxing.nupkg \
  https://api.nuget.org/v3-flatcontainer/zxing.net/0.16.11/zxing.net.0.16.11.nupkg
# 解压其中的 lib/net48/zxing.dll，保持**小写文件名**放回本目录
```

## 为什么不用 PackageReference

1. 保持「离线构建」：不需要网络还原、不依赖 NuGet 源可用性。
2. 分发更简单：`ZXing.dll` 与 `GeZi.Core.dll` 同级复制到输出目录，不引入包还原这一环。
3. 体积可控：net48 版是**单文件、零依赖**（不走 netstandard，不需要 `NETStandard.Library`）。

## 用途

`GeZi.Core.Support.QrDecoder` 用它把图片中的二维码解码成文本
（夸克分享有时只给二维码图片而不给链接）。与 `QrEncoder`（自写的零依赖编码器）配对：
编码自己写，解码用成熟库 —— 解码要处理透视校正与 Reed-Solomon 纠错，不值得自己造。
