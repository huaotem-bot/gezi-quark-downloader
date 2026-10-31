// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

namespace GeZi.Core.Api
{
    /// <summary>
    /// 夸克接口的业务错误码常量（2026-10-11 新增，由「检查更新」功能引入）。
    /// 以前"容量不足"的判据是散在 <c>QuarkClient</c> 里的两个裸数字
    /// （23017 / 23018），而转存任务的**异步轮询结果**又是另一套码（32003）。
    /// 各处硬编码数字的话，接口一调整就得满项目找数。这里集中定义，
    /// 调用处一律引用常量。将来下载、列表、删除等接口若也返回容量类错误码，
    /// 往这里扩展即可，不要在各模块里自己判断数值。
    /// ⚠️ <b>这个类只做「定义」，不做「判定」</b> —— 判定语义各处不同：
    ///   · 同步响应（sharepage/save 等）的 <c>data.code</c> → 见 <c>QuarkClient</c> 里的容量判定
    ///   · 异步任务（/1/clouddrive/task）的状态与错误码 → 见转存等待处
    /// ⚠️ <c>CapacityInsufficient</c> 的取值来自接口约定说明，<b>本机尚未实测触发过</b>。
    ///    若实际字段名或取值与预期不同，只改这里，不要在各调用处散着改。
    /// </summary>
    public static class QuarkErrorCode
    {
        /// <summary>网盘容量不足：转存任务**异步轮询结果**里返回的容量类错误码。</summary>
        public const long CapacityInsufficient = 32003;

        /// <summary>
        /// 转存接口（<c>sharepage/save</c>）**同步响应**里的「空间不足」业务码。
        /// 取值来自社区与同类项目的常见实现；与 32003 是两条不同路径（同步响应 vs 异步任务）。
        /// </summary>
        public const long ShareSaveCapacityInsufficient = 23017;

        /// <summary>同上，夸克偶发返回的另一个取值。</summary>
        public const long ShareSaveCapacityInsufficientAlt = 23018;

        /// <summary>
        /// 「download file size limit」：file/download 接口对大文件**拒发直链**的业务码。
        /// <b>2026-10-31 实测</b>：限流窗口内该接口返回 <c>HTTP 400, code 23018,
        /// message "download file size limit[fid]"</c>（服务端原文），同请求在窗口重开后即可通过 ——
        /// 是动态开关的账号级限流，不是静态规则。⚠️ 与上面的 ShareSaveCapacityInsufficientAlt
        /// 同码不同义：社区资料把 23018 记成「转存空间不足」，转存路径的容量判定（IsCapacityCode）
        /// 保留兼容；下载取链路径按本常量的语义解读（见 QuarkClient.DescribeLinkError）。
        /// </summary>
        public const long DownloadFileSizeLimit = 23018;

        /// <summary>
        /// 转存任务「成功」的终态值（/1/clouddrive/task 返回的 <c>data.status</c>）。
        /// 这个值是实测确认过的 —— 现有轮询逻辑一直以它作成功判据。
        /// </summary>
        public const long TaskStatusSuccess = 2;
    }
}
