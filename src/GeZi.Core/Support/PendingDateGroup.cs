// GeZi —— 多网盘下载器 (Multi-cloud downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 续传任务的「日期分组」判据（今天 / 昨天 / 更早）。
    ///
    /// <para>
    /// 抽到 Core 是为了让 GeZi.SelfTest 能直接断言跨零点、跨年、未知值这些边界 ——
    /// 自检项目不引用 WPF 主项目（见 <see cref="SourceKey"/> 的同款理由）。
    /// UI 侧 <c>PendingTask.DateGroupLabel()</c> 只做一层转发。
    /// </para>
    /// <para>
    /// ⚠️ 判据按**本地日期**而非 UTC：用户在 Asia/Shanghai，UTC 的今天从早上 8 点才开始，
    /// 用 UTC 会把「本地今天凌晨」误分到昨天。ticks 存的是本地时间。
    /// </para>
    /// </summary>
    public static class PendingDateGroup
    {
        /// <summary>分组标签：今天 / 昨天 / M月d日 / 更早。</summary>
        public static string Label(long createdAtTicks, DateTime today)
        {
            if (createdAtTicks <= 0) return "更早";
            DateTime t;
            try { t = new DateTime(createdAtTicks, DateTimeKind.Local); }
            catch { return "更早"; }

            DateTime date = t.Date;
            if (date == today.Date) return "今天";
            if (date == today.Date.AddDays(-1)) return "昨天";
            if (t.Year == today.Year) return t.ToString("M月d日");
            return "更早";
        }

        /// <summary>组间排序：今天 0 / 昨天 1 / 同年更早 2 / 跨年或未知 3。</summary>
        public static int Order(long createdAtTicks, DateTime today)
        {
            if (createdAtTicks <= 0) return 3;
            DateTime t;
            try { t = new DateTime(createdAtTicks, DateTimeKind.Local); }
            catch { return 3; }

            DateTime date = t.Date;
            if (date == today.Date) return 0;
            if (date == today.Date.AddDays(-1)) return 1;
            if (t.Year == today.Year) return 2;
            return 3;
        }

        /// <summary>当前进程所在时区的「今天」（重载里用 DateTime.Today）。</summary>
        public static string Label(long createdAtTicks) { return Label(createdAtTicks, DateTime.Today); }

        /// <summary>当前进程所在时区的分组排序。</summary>
        public static int Order(long createdAtTicks) { return Order(createdAtTicks, DateTime.Today); }
    }
}
