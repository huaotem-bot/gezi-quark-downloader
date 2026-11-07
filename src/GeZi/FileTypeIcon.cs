// GeZi —— 多网盘下载器 (Multi-cloud downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/agpl-3.0>.

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GeZi
{
    /// <summary>
    /// 按**文件扩展名**挑图标与颜色。
    ///
    /// 用户反馈原话：
    ///   「我发现除了有文件夹的图标其他文件都是第二个图标，不应该图片是图片的图标
    ///     pdf 是 pdf 的图标，等等各类型文件各类文件都是他们该有的图标吗为什么都是这一个」
    ///
    /// 原来文件列表里**所有**非目录项都用同一个 <c>IconFile</c>（一张折角纸），
    /// 视频、图片、压缩包、PDF 长得一模一样 —— 一屏文件根本扫不出哪个是哪个。
    /// 这里按扩展名分成十来类，每类一个**不同轮廓**的图标 + 一个**不同色系**，
    /// 与资源管理器 / VS Code 的习惯一致（16px 下纯靠形状很难分辨 PDF 和 Word，
    /// 所以形状和颜色一起用）。
    ///
    /// 用法（XAML）：
    /// <code>
    /// Data="{Binding Name, Converter={StaticResource FileTypeIcon}}"
    /// Stroke="{Binding Name, Converter={StaticResource FileTypeIcon}, ConverterParameter=brush}"
    /// </code>
    /// 代码里也可以直接调 <see cref="GeometryFor"/> / <see cref="BrushFor"/>。
    ///
    /// ⚠️ 颜色一律取主题画刷（`TryFindResource`），**不要写死十六进制**，
    ///    否则深色模式下列表里会冒出一片刺眼的浅色图标。
    /// </summary>
    public sealed class FileTypeIconConverter : IValueConverter
    {
        /// <summary>文件类型。图标键 = "IconFile" + 这个名字。</summary>
        private enum Kind
        {
            Generic,
            Image,
            Video,
            Audio,
            Pdf,
            Archive,
            Disc,
            Doc,
            Sheet,
            Slide,
            Code,
            Script,
            Json,
            Text,
            Subtitle,
            Font,
            Database,
            Book,
            Torrent,
            Design,
            Game,
            Package,
            Key,
            Exe,
            Apk,
            Mac,
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var name = value as string;
            string mode = parameter as string;
            if (string.Equals(mode, "brush", StringComparison.OrdinalIgnoreCase))
                return BrushFor(name);
            return GeometryFor(name);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }


        /// <summary>按文件名（只看扩展名）取图标几何。取不到时回退到通用文件图标。</summary>
        public static Geometry GeometryFor(string fileName)
        {
            string key = "IconFile" + KindSuffix(fileName);
            var g = TryRes<Geometry>(key, null);
            if (g != null) return g;
            return TryRes<Geometry>("IconFile", null);
        }

        /// <summary>按文件名取图标描边色（主题画刷，深色模式自动跟着变）。</summary>
        public static Brush BrushFor(string fileName)
        {
            return TryRes<Brush>(BrushKeyFor(fileName), null) ?? TryRes<Brush>("TextTertiaryBrush", null);
        }

        /// <summary>
        /// 按文件名取**主题画刷的键**（如 "SuccessBrush"）。
        /// 代码里优先用这个 + <c>SetResourceReference</c>，比直接拿 Brush 对象更稳：
        /// 换主题时 Brushes.xaml 会被整份重建，握在手里的旧 Brush 实例不一定跟着变。
        /// </summary>
        public static string BrushKeyFor(string fileName)
        {
            return BrushKeyOf(KindOf(fileName));
        }


        // ---------------- 内部 ----------------

        private static string KindSuffix(string fileName)
        {
            switch (KindOf(fileName))
            {
                case Kind.Image: return "Image";
                case Kind.Video: return "Video";
                case Kind.Audio: return "Audio";
                case Kind.Pdf: return "Pdf";
                case Kind.Archive: return "Archive";
                case Kind.Disc: return "Disc";
                case Kind.Doc: return "Doc";
                case Kind.Sheet: return "Sheet";
                case Kind.Slide: return "Slide";
                case Kind.Code: return "Code";
                case Kind.Script: return "Script";
                case Kind.Json: return "Json";
                case Kind.Text: return "Text";
                case Kind.Subtitle: return "Subtitle";
                case Kind.Font: return "Font";
                case Kind.Database: return "Database";
                case Kind.Book: return "Book";
                case Kind.Torrent: return "Torrent";
                case Kind.Design: return "Design";
                case Kind.Game: return "Game";
                case Kind.Package: return "Package";
                case Kind.Key: return "Key";
                case Kind.Exe: return "Exe";
                case Kind.Apk: return "Apk";
                case Kind.Mac: return "Mac";
                default: return "";
            }
        }

        private static string BrushKeyOf(Kind k)
        {
            switch (k)
            {
                case Kind.Image: return "SuccessBrush";
                case Kind.Video: return "PrimaryBrush";
                case Kind.Audio: return "WarningBrush";
                case Kind.Pdf: return "DangerBrush";
                case Kind.Archive: return "WarningBrush";
                case Kind.Disc: return "TextSecondaryBrush";
                case Kind.Doc: return "PrimaryBrush";
                case Kind.Sheet: return "SuccessBrush";
                case Kind.Slide: return "WarningBrush";
                case Kind.Code: return "TextSecondaryBrush";
                case Kind.Script: return "TextSecondaryBrush";
                case Kind.Json: return "WarningBrush";
                case Kind.Text: return "TextTertiaryBrush";
                case Kind.Subtitle: return "TextSecondaryBrush";
                case Kind.Font: return "PrimaryBrush";
                case Kind.Database: return "SuccessBrush";
                case Kind.Book: return "PrimaryBrush";
                case Kind.Torrent: return "SuccessBrush";
                case Kind.Design: return "DangerBrush";
                case Kind.Game: return "WarningBrush";
                case Kind.Package: return "WarningBrush";
                case Kind.Key: return "DangerBrush";
                case Kind.Exe: return "TextSecondaryBrush";
                case Kind.Apk: return "SuccessBrush";
                case Kind.Mac: return "TextSecondaryBrush";
                default: return "TextTertiaryBrush";
            }
        }

        private static Kind KindOf(string fileName)
        {
            string ext = ExtensionOf(fileName);
            if (ext.Length == 0) return Kind.Generic;

            // 🚨 分卷压缩包：`文明.7z.001` / `文明.7z.002` / `包.zip.003` …
            //   扩展名是**纯 3 位数字**。原来只把 "001" 写死在压缩包分支里，
            //   于是 `.002` 之后的分卷全都掉进「通用文件」图标（用户截图反馈
            //   「文明.7z.002 的图标是灰的，和 .001 不一样」）。
            //   这里统一按「3 位数字 = 分卷」处理，不管序号是几。
            if (IsVolumeNumber(ext)) return Kind.Archive;

            switch (ext)
            {
                // 图片
                case "jpg": case "jpeg": case "jpe": case "png": case "gif":
                case "bmp": case "webp": case "svg": case "ico": case "tif":
                case "tiff": case "heic": case "heif": case "avif": case "raw":
                case "cr2": case "nef": case "arw": case "dng":
                    return Kind.Image;

                // 视频
                // ⚠️ "ts" 同时是 TypeScript 和 MPEG-TS 的扩展名，这里判给**视频**：
                //    下载器里 .ts 基本都是 HLS 切片，TypeScript 源码几乎不会出现。
                case "mp4": case "mkv": case "avi": case "mov": case "wmv":
                case "flv": case "rmvb": case "rm": case "ts": case "m2ts":
                case "mts": case "webm": case "m4v": case "mpg": case "mpeg":
                case "3gp": case "vob": case "f4v": case "m2v": case "ogv":
                    return Kind.Video;

                // 音频
                case "mp3": case "flac": case "wav": case "aac": case "m4a":
                case "ogg": case "wma": case "ape": case "opus": case "aiff":
                case "mid": case "midi": case "amr": case "ac3": case "dts":
                case "dsf": case "wv": case "tta":
                    return Kind.Audio;

                // PDF（单独一类：用户点名要它有自己的图标）
                case "pdf":
                    return Kind.Pdf;

                // 压缩包
                // ⚠️ "001"~"999" 这类分卷序号不在这里 —— 见上面的 IsVolumeNumber，
                //    它们已经被提前判成 Archive 了。
                case "zip": case "rar": case "7z": case "tar": case "gz":
                case "tgz": case "bz2": case "xz": case "zst": case "lz4":
                case "cab": case "arj": case "lzh": case "z": case "lz":
                case "ace": case "r00":
                    return Kind.Archive;

                // 光盘镜像（与压缩包分开：镜像要挂载，压缩包要解压，动作不一样）
                case "iso": case "img": case "nrg": case "mdf": case "mds":
                case "bin": case "cue": case "vcd": case "udf": case "toast":
                    return Kind.Disc;

                // 文档
                case "doc": case "docx": case "rtf": case "odt": case "pages":
                case "wps": case "dot": case "dotx": case "wpd": case "abw":
                    return Kind.Doc;

                // 电子书（独立一类：阅读场景与办公文档不同）
                case "epub": case "mobi": case "azw": case "azw3": case "fb2":
                case "djvu": case "cbz": case "cbr": case "ibooks":
                    return Kind.Book;

                // 表格
                case "xls": case "xlsx": case "csv": case "ods": case "numbers":
                case "et": case "tsv": case "xlsm": case "xlsb":
                    return Kind.Sheet;

                // 演示
                case "ppt": case "pptx": case "odp": case "key": case "dps":
                case "pps": case "ppsx": case "potx":
                    return Kind.Slide;

                // 纯文本
                case "txt": case "log": case "md": case "markdown": case "nfo":
                case "me": case "readme": case "rst": case "tex":
                    return Kind.Text;

                // 字幕（从纯文本里独立出来：字幕框 + 两行对白更好认）
                case "srt": case "ass": case "ssa": case "vtt": case "lrc":
                case "sub": case "idx": case "smi": case "ttml":
                    return Kind.Subtitle;

                // 字体
                case "ttf": case "otf": case "woff": case "woff2": case "eot":
                case "fon": case "fnt": case "ttc":
                    return Kind.Font;

                // 数据 / 数据库
                // ⚠️ "mdf" 属于光盘镜像那一类（Media Descriptor File），这里不能再列一次，
                //    C# 的 switch 出现重复 case 标签会直接编译失败。
                case "db": case "sqlite": case "sqlite3": case "mdb": case "accdb":
                case "dbf": case "sql": case "ldf": case "parquet": case "ndf":
                    return Kind.Database;

                // JSON / 配置
                case "json": case "jsonl": case "json5": case "plist":
                    return Kind.Json;

                // 代码（源码）
                case "cs": case "vb": case "js": case "mjs": case "tsx":
                case "jsx": case "py": case "java": case "c": case "h":
                case "cpp": case "cc": case "hpp": case "go": case "rs":
                case "php": case "rb": case "swift": case "kt": case "kts":
                case "scala": case "lua": case "pl": case "pm": case "dart":
                case "html": case "htm": case "css": case "scss": case "less":
                case "vue": case "svelte": case "zig": case "nim": case "hx":
                case "xml": case "xaml": case "xsd": case "xsl": case "svgz":
                    return Kind.Code;

                // 脚本 / 命令行（与「源码」分开：脚本直接可执行）
                case "sh": case "bash": case "zsh": case "fish": case "ksh":
                case "bat": case "cmd": case "ps1": case "psm1": case "vbs":
                case "pyw": case "awk": case "sed": case "tcl": case "ahk":
                    return Kind.Script;

                // 配置
                case "yaml": case "yml": case "toml": case "ini": case "cfg":
                case "conf": case "properties": case "env": case "editorconfig":
                    return Kind.Json;

                // 种子 / 磁力
                case "torrent": case "magnet": case "metalink": case "mgt":
                    return Kind.Torrent;

                // 设计稿
                case "psd": case "psb": case "ai": case "sketch": case "fig":
                case "xd": case "cdr": case "afdesign": case "afphoto":
                case "aseprite": case "ase": case "tga": case "xcf":
                    return Kind.Design;

                // 游戏 / 模拟器镜像
                case "nes": case "snes": case "smc": case "gba": case "gbc":
                case "nds": case "3ds": case "cia": case "nsp": case "xci":
                case "wbfs": case "wad": case "rom": case "save":
                    return Kind.Game;

                // 资源包 / 模组
                case "mcpack": case "mcworld": case "mcaddon": case "omod":
                case "pak": case "vpk": case "pbo": case "unity3d": case "bundle":
                    return Kind.Package;

                // 加密 / 证书
                // ⚠️ 不能列 "key"：那是 Keynote 演示文稿，已经在上面判给 Slide 了。
                case "pem": case "crt": case "cer": case "p12":
                case "pfx": case "ppk": case "keystore": case "jks": case "asc":
                    return Kind.Key;

                // 安卓应用
                case "apk": case "aab": case "apks": case "xapk": case "apkm":
                    return Kind.Apk;

                // 苹果应用 / 安装包
                case "dmg": case "pkg": case "mpkg": case "app": case "ipa":
                    return Kind.Mac;

                // 可执行 / 系统安装包
                case "exe": case "msi": case "appx": case "msix": case "msixbundle":
                case "deb": case "rpm": case "jar": case "flatpak": case "appimage":
                case "snap": case "run": case "elf": case "so":
                    return Kind.Exe;

                default:
                    return Kind.Generic;
            }
        }

        /// <summary>
        /// 取扩展名（小写、不含点）。故意不用 <c>Path.GetExtension</c>：
        /// 网盘文件名可能带 `\`、`:` 等非法字符，走文件系统 API 不划算。
        /// </summary>
        private static string ExtensionOf(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "";
            int dot = fileName.LastIndexOf('.');
            // 没有点 / 点在开头（".gitignore"）/ 点后面太长（不是扩展名）
            if (dot < 0 || dot == fileName.Length - 1) return "";
            if (dot == 0) return "";
            string ext = fileName.Substring(dot + 1);
            if (ext.Length > 8) return "";
            return ext.ToLowerInvariant();
        }

        /// <summary>
        /// 扩展名是不是「分卷序号」——纯 3 位数字（001 / 002 / 999）。
        /// <para>
        /// 只认 3 位：分卷约定就是 3 位补零，而 1~2 位纯数字扩展名在真实文件里
        /// 几乎不会出现（认了反而会把奇怪的东西当压缩包）。4 位以上（如 .0001）
        /// 也有工具在用，但那已经不常见，宁可退回通用图标。
        /// </para>
        /// </summary>
        private static bool IsVolumeNumber(string ext)
        {
            if (ext == null || ext.Length != 3) return false;
            for (int i = 0; i < ext.Length; i++)
            {
                if (ext[i] < '0' || ext[i] > '9') return false;
            }
            return true;
        }

        private static T TryRes<T>(string key, T fallback) where T : class
        {
            try
            {
                if (string.IsNullOrEmpty(key)) return fallback;
                var app = System.Windows.Application.Current;
                if (app == null) return fallback;
                return app.TryFindResource(key) as T ?? fallback;
            }
            catch { return fallback; }
        }
    }
}
