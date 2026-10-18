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

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using GeZi.Core.Download;

namespace GeZi
{
    /// <summary>
    /// 悬浮小窗：主窗收起后仍能看到下载进度。
    /// 直接把主窗那份 <see cref="ObservableCollection{TaskItem}"/> 挂到
    /// ItemsControl 上，**不复制、不重算、不另开定时器**。
    /// 这样小窗本身几乎不占资源，也不会和主窗的统计"对不上"。
    /// 全局统计（总速度/总进度/剩余）依赖集合内容，用一个小 DispatcherTimer
    /// 每秒重算一次 —— 只做加法，不做 IO。TaskItem 自身的速度/进度是绑定的，会自动更新。
    /// </summary>
    public partial class MiniWindow : Window
    {
        private readonly ObservableCollection<TaskItem> _all;
        private readonly System.Windows.Threading.DispatcherTimer _timer;
        private readonly System.Collections.Specialized.NotifyCollectionChangedEventHandler _onChanged;

        /// <summary>用户点了「打开主窗口」/「收起」时通知主窗。</summary>
        public event Action OpenMainRequested;
        public event Action HideRequested;

        /// <summary>小窗位置变化（拖动结束）时回写设置。</summary>
        public event Action<double, double> PositionChanged;

        public MiniWindow(ObservableCollection<TaskItem> tasks)
        {
            InitializeComponent();
            _all = tasks;

            // ⚠️ 事件处理器要存成字段，否则退订时 `-= (s,e)=>{}` 是个**新委托**，退不掉，
            //    小窗关了以后集合变化仍会回调到一个已经没用的窗口。
            _onChanged = (s, e) => Refresh();
            _all.CollectionChanged += _onChanged;
            Refresh();

            // 1 秒重算一次全局统计（纯加法，开销可忽略）
            _timer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromSeconds(1), System.Windows.Threading.DispatcherPriority.Background,
                (s, e) => Refresh(), Dispatcher);
            _timer.Start();

            LocationChanged += (s, e) =>
            {
                if (PositionChanged != null)
                    PositionChanged(Left, Top);
            };

            // 无边框窗口的圆角交给 DWM（**不能**用 AllowsTransparency，那会关掉 ClearType
            // 让文字发糊 —— 用户反馈的「太糊了」就是这个原因）。
            // ⚠️ 必须在这里（句柄已存在）而不是构造函数里。
            Loaded += (s, e) => DialogChrome.ApplyRoundedCorners(this);
        }

        /// <summary>
        /// 重算三个总量：总速度 / 总进度 / 剩余**文件数**。
        /// ⚠️ 2026-10-03 用户要求简化：**不再逐任务列进度条**（任务一多就一长条，
        /// 而且跟主窗重复），只留这一行总量；「剩余」也不再标时间（估出来的时间
        /// 又长又跳，11 个任务时甚至出现过负数），改成**还剩几个文件**，一眼就懂。
        /// </summary>
        private void Refresh()
        {
            var active = _all.Where(t => t.State == JobState.Downloading
                                      || t.State == JobState.Queued
                                      || t.State == JobState.Paused
                                      || t.State == JobState.Cancelling).ToList();
            int finished = _all.Count(t => t.State == JobState.Completed);

            TitleText.Text = active.Count == 0 ? "鸽子下载" : ("下载中（" + active.Count + "）");
            RemainText.Text = active.Count == 0 ? "—" : (active.Count + " 个");

            // 「总进度」不写百分比，写**已完成数/总任务数**（用户要求）。
            // 百分比在一批小文件上跳得很快、看不出还剩几个，计数才直观。
            TotalPercentText.Text = _all.Count == 0
                ? "—"
                : (finished + "/" + _all.Count);

            if (active.Count == 0)
            {
                TotalSpeedText.Text = "—";
                return;
            }

            double speed = active.Where(t => string.IsNullOrEmpty(t.Phase)).Sum(t => t.Speed); // 仅下载吞吐，不计本地合并
            TotalSpeedText.Text = speed > 0 ? Util.FormatSize((long)speed) + "/s" : "0 B/s";
        }

        // 标题栏拖动由 WindowChrome 的 CaptionHeight=38 接管（见 XAML），
        // 不需要再自己 DragMove —— 两者同时上会互相打架（"已有拖动进行中"）。

        private void OnHideClick(object sender, RoutedEventArgs e)
        {
            if (HideRequested != null) HideRequested();
        }

        private void OnOpenMainClick(object sender, MouseButtonEventArgs e)
        {
            if (OpenMainRequested != null) OpenMainRequested();
        }

        /// <summary>关闭前的收尾（由主窗在退出时调用）。</summary>
        internal void Shutdown()
        {
            try { _timer.Stop(); } catch { }
            try { _all.CollectionChanged -= _onChanged; } catch { }
        }
    }
}
