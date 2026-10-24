# 第三方组件声明（Third-Party Notices）

**鸽子下载（GeZi）** 在开发与分发过程中使用了以下第三方开源组件。
本文件汇总各组件的来源、版本、许可证，并附上许可证全文（见文末附录）。

> 本项目的自有代码以 **GNU Affero General Public License v3.0**（见根目录 `LICENSE`）发布，
> 版权归 **Yi Yuan** 所有（Copyright (C) 2026 Yi Yuan）。
> 下列第三方组件的许可证均与 AGPL-3.0 **兼容**（Apache-2.0 是宽松许可，
> 允许被 AGPL-3.0 项目包含）。

---

## 一、组件一览

| 组件 | 版本 | 许可证 | 用途 | 分发形式 |
|---|---|---|---|---|
| [ZXing.Net](#1-zxingnet) | 0.16.11 | Apache-2.0 | 二维码解码 | `zxing.dll`（随程序集分发） |
| [OpenSSL](#3-openssl) | 3.5.7 | Apache-2.0 | 扫码登录 / 账号信息通道（P/Invoke） | `src/GeZi.Core/lib/OpenSSL/libssl-3-x64.dll`、`libcrypto-3-x64.dll` |
| [.NET Framework](#4-microsoft-组件) | 4.8 | 微软系统组件 | 应用运行时 | 目标框架（用户系统自带） |
| [Microsoft VC++ 运行时](#4-microsoft-组件) | 14.x | 微软可再分发条款 | OpenSSL 的 C 运行时 | `src/GeZi.Core/lib/OpenSSL/vcruntime140*.dll` |
| [Microsoft.Web.WebView2](#5-microsoftwebwebview2) | 1.0.3351.48 | MIT | 多网盘内嵌登录（UC 等） | `Microsoft.Web.WebView2.Core.dll`、`Microsoft.Web.WebView2.Wpf.dll`、`WebView2Loader.dll`（**不含 Runtime**） |

> ⚠️ **本项目不再是「零 NuGet 依赖」**：2026-10 引入 `Microsoft.Web.WebView2`（唯一的 NuGet 包），
> 用于多网盘内嵌登录。构建需要能还原该包。
> 运行时侧不打包：机器上没有 WebView2 Runtime 时，内嵌登录入口会自动降级，
> 手动粘贴凭证的通道始终可用 —— 程序不会因此无法启动。

---

## 1. ZXing.Net

- **版本**：0.16.11
- **作者**：Michael Jahn
- **项目地址**：<https://github.com/micjahn/ZXing.Net/>
- **许可证**：Apache License 2.0
- **取用文件**：`lib/net48/zxing.dll` → 本仓库 `src/GeZi.Core/lib/ZXing/zxing.dll`（532 KB）
- **用途**：把图片中的二维码解码成文本（夸克分享有时只给二维码图片、不给链接）。

> 本项目**未修改** ZXing.Net 的源代码，仅以二进制程序集形式引用。
> 该 DLL 以普通程序集引用（非 NuGet 包）随仓库分发，以保证「零 NuGet、可离线构建」。

---

## 2. OpenSSL

- **版本**：3.5.7
- **版权**：Copyright © 1998-2026 The OpenSSL Project Authors. Copyright © 1995-1998 Eric A. Young, Tim J. Hudson.
- **项目地址**：<https://www.openssl.org/>
- **许可证**：Apache License 2.0
- **位置**：`src/GeZi.Core/lib/OpenSSL/libssl-3-x64.dll`、`libcrypto-3-x64.dll`
- **用途**：**扫码登录换登录态**与**账号昵称/头像**这两个请求的 TLS 通道 ——
  夸克 `pan.quark.cn` 的 ALB 按 TLS ClientHello 指纹过滤（实测只有 OpenSSL 3.5.7 能过），
  .NET 自带的 SChannel 无法访问该域，因此通过 P/Invoke 直接使用本 OpenSSL。

> 这两个 DLL 取自 CPython 3.13 官方 Windows 发行版（OpenSSL 3.5.7），本项目未作任何修改，
> 仅从「随 CPython 分发」改为「随本程序直接分发」。

---

## 4. Microsoft 组件

### 4.1 .NET Framework 4.8

- 本项目的目标框架为 `.NET Framework 4.8`。
- .NET Framework 是 **Windows 操作系统的组成部分**，随系统提供，不随本仓库分发。
- 用户若未安装，Windows 会提示并引导安装（见 README 的「系统要求」）。

### 4.2 Microsoft Visual C++ 运行时（vcruntime140.dll / vcruntime140_1.dll）

- **位置**：`src/GeZi.Core/lib/OpenSSL/vcruntime140.dll`、`vcruntime140_1.dll`
- **来源**：与 OpenSSL DLL 同源（CPython 官方 Windows 发行版自带）
- **用途**：OpenSSL 运行所需的 C 运行时库
- **许可**：受 Microsoft 的 Visual Studio 可再分发组件条款约束
  （<https://visualstudio.microsoft.com/license-terms/>）。
  本项目按「可再分发」条款随 OpenSSL 一并分发，未作修改。

---

## 5. 参考与致谢（非代码依赖）

以下项目/资料在开发过程中被**阅读参考**（用于对标设计、验证思路），
**未复制其源代码**，故不构成本项目的代码依赖。在此致谢：

- `H1d3rOne/QuarkDownloader`（MIT）—— 参考了客户端伪装 UA 的取值方式与直链导出命令格式。
- 通用下载器（aria2 / curl / wget2 等）—— 阅读其文档，对标分片调度与并发策略。
- 其他同类下载器 —— 仅阅读其 README / 公开文档进行功能对标。

> ⚠️ 上述项目中包含 **GPL-2.0 / AGPL-3.0** 等强传染性许可的软件。
> 本项目**仅阅读其公开文档与思路，未复制任何源代码**，
> 因此不存在许可证传染问题。如发现任何遗漏，欢迎提 issue 指出。

---

# 附录：许可证全文

## Apache License 2.0



                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS

   APPENDIX: How to apply the Apache License to your work.

      To apply the Apache License to your work, attach the following
      boilerplate notice, with the fields enclosed by brackets "[]"
      replaced with your own identifying information. (Don't include
      the brackets!)  The text should be enclosed in the appropriate
      comment syntax for the file format. We also recommend that a
      file or class name and description of purpose be included on the
      same "printed page" as the copyright notice for easier
      identification within third-party archives.

   Copyright [yyyy] [name of copyright owner]

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.


---

---

## 5. Microsoft.Web.WebView2

- **许可证**：MIT
- **版本**：1.0.3351.48
- **项目地址**：<https://github.com/MicrosoftEdge/WebView2Samples>
- **NuGet**：<https://www.nuget.org/packages/Microsoft.Web.WebView2>
- **用途**：多网盘内嵌登录（在程序内打开官方登录页，读取 Cookie）

**分发内容**：仅托管程序集与加载器

| 文件 | 说明 |
|---|---|
| `Microsoft.Web.WebView2.Core.dll` | 核心托管 API |
| `Microsoft.Web.WebView2.Wpf.dll` | WPF 控件封装 |
| `WebView2Loader.dll` | 原生加载器（x64） |

**不包含**：WebView2 Runtime 本体（由微软随 Edge / Windows 分发，用户系统自带或另行安装）。
本项目在运行时检测其存在性；缺失时自动降级为「手动粘贴凭证」，不影响其他功能。

> 本项目**未修改** WebView2 的源代码，仅以二进制程序集形式引用。

**MIT 许可证要点**：允许商业使用、修改、分发，需保留版权声明与许可声明。
