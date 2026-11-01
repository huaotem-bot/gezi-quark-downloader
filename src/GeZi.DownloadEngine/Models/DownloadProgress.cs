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

namespace GeZi.DownloadEngine
{
    /// <summary>下载进度快照。</summary>
    public class DownloadProgress
    {
        public long Done { get; set; }
        public long Total { get; set; }
        /// <summary>最近窗口平均速度（字节/秒）。</summary>
        public double Speed { get; set; }

        public double Percent => Total > 0 ? (Done * 100.0 / Total) : 0.0;

        /// <summary>
        /// 当前阶段的可选说明（例如「正在合并分片」）。空串/null = 常规下载阶段。
        /// </summary>
        /// <remarks>
        /// 在**所有分片都下完之后**，
        /// 还要把剩余分片按序 append 进目标文件 —— 对 100GB 的文件，这一步可能持续好几分钟。
        /// 这段期间**没有任何网络流量**，如果不上报阶段，界面就会冻结在
        /// 「进度 100% + 速度 0 + 状态『下载中』」，用户必然判定为卡死
        /// （用户实测反馈：「就显示 0 kb 不动」）。
        /// 有了它，UI 就能显示「正在合并分片」，让用户知道程序还在干活。
        /// </remarks>
        public string Phase { get; set; }
    }
}
