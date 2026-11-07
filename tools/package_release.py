#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""GeZi 发布打包（零第三方依赖，只用标准库）。

做四件事，对应优化方案里「可交付」那道门：
  1. 只打包白名单文件，绝不把源码/测试/临时产物带进发布包；
  2. 随包带 LICENSE 与 THIRD-PARTY-NOTICES.md（GPL 合规）；
  3. 生成 SHA256SUMS.txt 与 <zip>.sha256（可复核来源）；
  4. 生成 CycloneDX 1.5 的 sbom.json（组件 + 哈希）。

用法：
    python tools/package_release.py --from-build --out artifacts
    python tools/package_release.py --from-dir src/GeZi/bin/Release/net48 --out dist

退出码：0 = 成功；1 = 关键文件缺失或出错。
"""

from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import re
import shutil
import sys
import zipfile

# ---------------------------------------------------------------------------
# 白名单：发布包里**只允许**出现这些文件。
# 新增原生依赖时，这里必须同步（libssl/libcrypto 是 TLS 桥接用的，见 GeZi.Core.csproj）。
# ---------------------------------------------------------------------------
REQUIRED_BINARIES = [
    "GeZi.exe",
    "GeZi.exe.config",
    # 🚨 五层架构（2026-10-24 重构）之后的**全部托管程序集**。
    # 原来这里只列了 GeZi.Core.dll —— 而 GeZi.exe 现在依赖另外三个：
    #   GeZi.Application / GeZi.DownloadEngine / GeZi.Providers
    # 少任何一个都会在启动瞬间抛 FileNotFoundException，
    # 而打包脚本**不会报错**（它只检查列表里的文件在不在）。
    "GeZi.Core.dll",
    "GeZi.Application.dll",
    "GeZi.DownloadEngine.dll",
    "GeZi.Providers.dll",
]

OPTIONAL_BINARIES = [
    # 原生依赖（扫码登录 TLS 桥接 + VC 运行库）
    "libssl-3-x64.dll",
    "libcrypto-3-x64.dll",
    "vcruntime140.dll",
    "vcruntime140_1.dll",
    # 二维码解码（Apache-2.0，见 THIRD-PARTY-NOTICES.md）
    "zxing.dll",
    # 多网盘内嵌登录（Microsoft.Web.WebView2，MIT）。
    # 只随包带托管 DLL 与 loader，**不打包 Runtime** ——
    # 运行时缺失时程序会自动降级到手动粘贴（见 WebView2Runtime）。
    "Microsoft.Web.WebView2.Core.dll",
    "Microsoft.Web.WebView2.Wpf.dll",
    "WebView2Loader.dll",
]

# 🚨🚨 2026-11-04：**发布包不再带 PDB**。
#
# 原来这里列了 PDB，理由是「便于用户回报崩溃栈」。但实测打包后审计发现：
#   PDB 里有一段 SourceLink 的 JSON：
#     {"documents":{"C:\\Users\\<用户名>\\Desktop\\...\\repo\\*":
#                    "https://raw.githubusercontent.com/..."}}
#   **它是磁盘上的真实绝对路径**，会把编译机的用户名和目录结构一起发给用户。
#
# 为什么 PathMap 挡不住：
#   PathMap 改的是「编译期写进 PDB 的文档路径」，
#   而 SourceLink 的 documents 表是**另外生成的**（键 = 源码在磁盘上的位置），
#   两者不是同一份数据。实测加了解决方案级 PathMap + 完整 Rebuild，
#   documents 表里依然是 C:\\Users\\dove\\... —— 挡不住。
#
# 权衡：不带 PDB 后，用户回报的崩溃栈会少行号。
# 但**隐私 > 调试便利** —— 用户名和目录结构属于用户信息，不该进发布包。
# 需要符号时，作者本地的 bin 目录里一直都有（.pdb 只是不打进 zip）。
SYMBOL_FILES = []

DOC_FILES = [
    "LICENSE",
    "THIRD-PARTY-NOTICES.md",
    "README.md",
]


def log(msg: str) -> None:
    print(msg, flush=True)


def sha256_of(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def read_version(repo_root: str) -> str:
    """从 src/GeZi/GeZi.csproj 读 <Version>，保持包名与程序集版本一致。"""
    csproj = os.path.join(repo_root, "src", "GeZi", "GeZi.csproj")
    try:
        with open(csproj, "r", encoding="utf-8-sig") as f:
            text = f.read()
    except OSError:
        return "0.0.0"
    m = re.search(r"<Version>\s*([^<\s]+)\s*</Version>", text)
    return m.group(1) if m else "0.0.0"


def collect(stage_files: dict[str, str], src: str, name: str, required: bool) -> bool:
    path = os.path.join(src, name)
    if os.path.isfile(path):
        stage_files[name] = path
        return True
    if required:
        log("ERROR 缺少必需文件: " + name + "  (" + src + ")")
        return False
    log("WARN  跳过可选文件（不存在）: " + name)
    return True


def build_sbom(version: str, staged: list[str]) -> dict:
    """最小可用的 CycloneDX 1.5 SBOM：主程序 + 每个随包二进制。"""
    components = []
    for name in staged:
        lower = name.lower()
        if lower.endswith(".dll") or lower.endswith(".exe"):
            comp = {
                "type": "application" if lower.startswith("gezi") else "library",
                "name": name,
                "version": version if lower.startswith("gezi") else "bundled",
            }
            components.append(comp)
    return {
        "bomFormat": "CycloneDX",
        "specVersion": "1.5",
        "version": 1,
        "metadata": {
            "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "component": {
                "type": "application",
                "name": "GeZi",
                "version": version,
                "licenses": [{"license": {"id": "GPL-3.0-or-later"}}],
            },
        },
        "components": components,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="打包 GeZi 发布产物")
    parser.add_argument("--from-build", action="store_true",
                        help="使用默认构建输出 src/GeZi/bin/Release/net48")
    parser.add_argument("--from-dir", default=None,
                        help="显式指定构建输出目录")
    parser.add_argument("--out", default="artifacts", help="输出目录（默认 artifacts）")
    parser.add_argument("--no-zip", action="store_true", help="只生成清单，不打包 zip")
    args = parser.parse_args()

    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    src = args.from_dir or os.path.join(repo_root, "src", "GeZi", "bin", "Release", "net48")

    if not os.path.isdir(src):
        log("ERROR 构建输出目录不存在: " + src)
        log("       先执行: dotnet build GeZi.slnx -c Release")
        return 1

    stage_files: dict[str, str] = {}
    ok = True

    for name in REQUIRED_BINARIES:
        ok &= collect(stage_files, src, name, required=True)
    for name in OPTIONAL_BINARIES + SYMBOL_FILES:
        ok &= collect(stage_files, src, name, required=False)
    for name in DOC_FILES:
        ok &= collect(stage_files, repo_root, name, required=False)

    if not ok:
        return 1

    # 🚨🚨 敏感文件防线（2026-11-04 新增）。
    # 白名单机制本身就不会带上它们，但白名单是**人维护的列表** ——
    # 哪天有人往 REQUIRED/OPTIONAL 里加了一个通配或误加名字，
    # 用户的登录 Cookie / 下载历史就会被打进发布包。
    # 这里做一次**硬校验**：一旦命中就中止打包，宁可失败也不泄露。
    FORBIDDEN = [
        "GeZi.config.xml",      # 登录态（DPAPI 加密的 Cookie / 令牌）
        "GeZi.state.json",      # 下载历史 + 未完成任务
        "GeZi.cleanup.txt",     # 转存副本清理登记
        "GeZi.log",             # 运行日志（含账号昵称、直链）
    ]
    FORBIDDEN_PREFIX = [
        "accounts/",            # 账号档案目录
        "accounts\\",
        "webview2-data/",       # 内嵌浏览器的 Cookie/缓存
        "webview2-data\\",
        "profile-",             # 按网盘分的 WebView2 profile
        "Downloads/",           # 误落进构建目录的下载文件
    ]
    leaked = []
    for name in stage_files.keys():
        if name in FORBIDDEN:
            leaked.append(name)
            continue
        for pref in FORBIDDEN_PREFIX:
            if name.startswith(pref):
                leaked.append(name)
                break
    if leaked:
        log("ERROR 发布包中出现了敏感文件，已中止：")
        for name in leaked:
            log("        " + name)
        log("       这些文件含登录态 / 历史 / 账号信息，绝不可打包。")
        return 1

    version = read_version(repo_root)
    package_name = "GeZi-v" + version + "-win-x64"
    out_dir = os.path.abspath(args.out)
    os.makedirs(out_dir, exist_ok=True)

    # 按文件名排序，保证每次打包顺序稳定、清单可 diff
    ordered = sorted(stage_files.keys())

    # 1) SHA256SUMS.txt
    sums_lines = []
    for name in ordered:
        sums_lines.append(sha256_of(stage_files[name]) + "  " + name)
    sums_path = os.path.join(out_dir, "SHA256SUMS.txt")
    with open(sums_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(sums_lines) + "\n")
    log("写出 " + sums_path + "  (" + str(len(ordered)) + " 个文件)")

    # 2) sbom.json
    sbom_path = os.path.join(out_dir, "sbom.json")
    with open(sbom_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(build_sbom(version, ordered), f, ensure_ascii=False, indent=2)
        f.write("\n")
    log("写出 " + sbom_path)

    if args.no_zip:
        log("完成（未打包 zip）: " + package_name)
        return 0

    # 3) zip（先写临时文件再改名，避免中断留下半个包）
    zip_path = os.path.join(out_dir, package_name + ".zip")
    tmp_zip = zip_path + ".tmp"
    with zipfile.ZipFile(tmp_zip, "w", zipfile.ZIP_DEFLATED) as z:
        for name in ordered:
            z.write(stage_files[name], arcname=name)
        for extra in ("SHA256SUMS.txt", "sbom.json"):
            full = os.path.join(out_dir, extra)
            if os.path.isfile(full):
                z.write(full, arcname=extra)
    shutil.move(tmp_zip, zip_path)
    log("写出 " + zip_path)

    # 4) zip 自身的哈希，供发布页展示
    digest = sha256_of(zip_path)
    with open(zip_path + ".sha256", "w", encoding="utf-8", newline="\n") as f:
        f.write(digest + "  " + os.path.basename(zip_path) + "\n")
    log("zip SHA-256 = " + digest)

    log("完成: " + package_name)
    return 0


if __name__ == "__main__":
    sys.exit(main())