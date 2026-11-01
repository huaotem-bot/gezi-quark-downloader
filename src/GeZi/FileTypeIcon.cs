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
            Doc,
            Sheet,
            Slide,
            Code,
            Text,
            Exe,
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
                case Kind.Doc: return "Doc";
                case Kind.Sheet: return "Sheet";
                case Kind.Slide: return "Slide";
                case Kind.Code: return "Code";
                case Kind.Text: return "Text";
                case Kind.Exe: return "Exe";
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
                case Kind.Doc: return "PrimaryBrush";
                case Kind.Sheet: return "SuccessBrush";
                case Kind.Slide: return "WarningBrush";
                case Kind.Code: return "TextSecondaryBrush";
                case Kind.Text: return "TextTertiaryBrush";
                case Kind.Exe: return "TextSecondaryBrush";
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
                case "cr2": case "nef": case "psd": case "ai": case "sketch":
                    return Kind.Image;

                // 视频
                // ⚠️ "ts" 同时是 TypeScript 和 MPEG-TS 的扩展名，这里判给**视频**：
                //    下载器里 .ts 基本都是 HLS 切片，TypeScript 源码几乎不会出现。
                case "mp4": case "mkv": case "avi": case "mov": case "wmv":
                case "flv": case "rmvb": case "rm": case "ts": case "m2ts":
                case "mts": case "webm": case "m4v": case "mpg": case "mpeg":
                case "3gp": case "vob": case "f4v": case "m2v":
                    return Kind.Video;

                // 音频
                case "mp3": case "flac": case "wav": case "aac": case "m4a":
                case "ogg": case "wma": case "ape": case "opus": case "aiff":
                case "mid": case "midi": case "amr":
                    return Kind.Audio;

                // PDF（单独一类：用户点名要它有自己的图标）
                case "pdf":
                    return Kind.Pdf;

                // 压缩包 / 镜像
                // ⚠️ "001"~"999" 这类分卷序号不在这里 —— 见上面的 IsVolumeNumber，
                //    它们已经被提前判成 Archive 了。
                case "zip": case "rar": case "7z": case "tar": case "gz":
                case "tgz": case "bz2": case "xz": case "zst": case "lz4":
                case "iso": case "cab": case "arj": case "lzh":
                    return Kind.Archive;

                // 文档
                case "doc": case "docx": case "rtf": case "odt": case "pages":
                case "wps": case "epub": case "mobi":
                    return Kind.Doc;

                // 表格
                case "xls": case "xlsx": case "csv": case "ods": case "numbers":
                case "et": case "tsv":
                    return Kind.Sheet;

                // 演示
                case "ppt": case "pptx": case "odp": case "key": case "dps":
                    return Kind.Slide;

                // 纯文本 / 字幕 / 配置
                case "txt": case "log": case "md": case "markdown": case "nfo":
                case "srt": case "ass": case "ssa": case "vtt": case "lrc":
                    return Kind.Text;

                // 代码
                case "cs": case "vb": case "js": case "mjs": case "tsx":
                case "jsx": case "py": case "java": case "c": case "h": case "cpp":
                case "cc": case "hpp": case "go": case "rs": case "php": case "rb":
                case "swift": case "kt": case "scala": case "lua": case "pl":
                case "sh": case "bash": case "zsh": case "bat": case "cmd":
                case "ps1": case "sql": case "html": case "htm": case "css":
                case "scss": case "less": case "xml": case "json": case "yaml":
                case "yml": case "toml": case "ini": case "cfg": case "conf":
                    return Kind.Code;

                // 可执行 / 安装包
                case "exe": case "msi": case "apk": case "appx": case "msix":
                case "dmg": case "pkg": case "deb": case "rpm": case "jar":
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
