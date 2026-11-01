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

using System.ComponentModel;
using GeZi.Providers;
using GeZi.Providers.Quark;
using GeZi.Providers.Uc;
using GeZi.Providers.Aliyun;
using GeZi.Providers.Xunlei;
using GeZi.Providers.Shared;
using GeZi.Providers.Qr;

using GeZi.Services;
namespace GeZi
{
    /// <summary>
    /// 分享文件条目（供列表展示与下载）。
    /// </summary>
    public class ShareFileItem : INotifyPropertyChanged
    {
        public string Fid { get; set; }
        public string Token { get; set; }
        public string Name { get; set; }

        /// <summary>文件自身的字节数。**文件夹恒为 0** —— 夸克列表接口对目录不返回大小。</summary>
        public long Size { get; set; }

        public string RelDir { get; set; } = "";
        public bool IsDir { get; set; }

        private long? _dirSize;
        private bool _dirSizeFailed;

        /// <summary>
        /// 文件夹的**递归总大小**（后台算出来后填进来）。
        /// null = 还没算出来 → 显示「计算中…」。
        /// </summary>
        public long? DirSize
        {
            get { return _dirSize; }
            set
            {
                _dirSize = value;
                Raise(nameof(DirSize));
                Raise(nameof(SizeText));
            }
        }

        /// <summary>该文件夹大小算失败了（接口报错/被取消）→ 显示「—」，不显示误导性的 0 B。</summary>
        public bool DirSizeFailed
        {
            get { return _dirSizeFailed; }
            set
            {
                _dirSizeFailed = value;
                Raise(nameof(DirSizeFailed));
                Raise(nameof(SizeText));
            }
        }

        /// <summary>
        /// 列表右侧显示的大小文案。
        /// 原来文件夹直接显示接口给的 <c>Size</c>，而夸克对目录**恒返回 0**
        /// → 所有文件夹都是「0 B」（用户反馈「文件夹右边显示文件大小一直是 0 B」）。
        /// 现在文件夹改显示后台递归求和的结果；没算出来时显示「计算中…」而不是 0 B。
        /// </summary>
        public string SizeText
        {
            get
            {
                if (!IsDir)
                    return FormatSize(Size);
                if (_dirSizeFailed)
                    return "—";
                if (_dirSize == null)
                    return "计算中…";
                return FormatSize(_dirSize.Value);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }

        private static string FormatSize(long n)
        {
            // 🚨 同 Util.FormatSize：原来 1024 进制却标 "KB/MB/GB"，
            // 与夸克分享页（1000 进制）对不上，用户以为文件变小了。统一改 1000 进制。
            double v = n;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (v >= 1000 && i < units.Length - 1)
            {
                v /= 1000.0;
                i++;
            }
            return i == 0 ? n + " B" : v.ToString("F2") + " " + units[i];
        }
    }
}
