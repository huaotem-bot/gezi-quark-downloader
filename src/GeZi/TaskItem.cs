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
using System.ComponentModel;
using GeZi.DownloadEngine;
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
    /// <summary>任务列表条目（带变更通知，供进度条/状态实时刷新）。</summary>
    public class TaskItem : INotifyPropertyChanged
    {
        public DownloadJob Job { get; }
        public string Name { get; }

        /// <summary>
        /// 任务所属网盘的中文名（夸克 / UC / 阿里云盘 / 迅雷云盘）。
        /// <para>
        /// 任务列表按这个字段分组折叠（用户要求：第一级是网盘，网盘之下才是任务）。
        /// 取的是**这条任务来自哪家网盘**（<c>Pending.PanKind</c>），
        /// 不是当前登录账号的网盘 —— 登录夸克去下 UC 分享时，任务应该归到 UC 下面。
        /// </para>
        /// </summary>
        public string PanGroup { get; }

        /// <summary>
        /// 第二级分组：这条任务属于**哪一次「点下载」**（同一次选中的文件共用一个 ID）。
        /// <para>
        /// 🚨 2026-11-01 用户要求：任务列表要**两级**折叠 ——
        /// 第一级按网盘，第二级按任务（一次下载 = 一个任务，它下面才是各个文件）。
        /// 原来只有一级（网盘），同一个任务里的 10 个文件平铺在网盘下面，
        /// 根本看不出它们是一起的。
        /// </para>
        /// <para>
        /// 用 ID 而不是名字做分组键：两次下载可能选了同名目录，
        /// 按名字分组会把两个不同任务合并成一个。
        /// </para>
        /// </summary>
        public string TaskGroupId { get; }

        /// <summary>
        /// 第二级分组的**显示名**（任务名：优先用选中的目录名，其次"任务 N"）。
        /// 与 <see cref="TaskGroupId"/> 成对使用：ID 决定分组边界，它决定显示什么。
        /// </summary>
        public string TaskGroupName { get; }

        /// <summary>是否已写入下载历史。终态只记一次，防止重复上报把历史刷屏。</summary>
        public bool HistoryRecorded { get; set; }

        public TaskItem(DownloadJob job, string name,
            string taskGroupId = null, string taskGroupName = null)
        {
            Job = job;
            Name = name;

            // 第二级分组：没传就退化成"这个文件自成一组"（用文件名兜底）。
            // 这样即使某个调用点忘了传，列表也不会崩成一级 —— 只是那几条各自成组。
            TaskGroupId = string.IsNullOrWhiteSpace(taskGroupId)
                ? "single:" + (name ?? "")
                : taskGroupId;
            // ⚠️ 分组头显示的**就是这个字段**（CollectionViewGroup.Name 就是分组键），
            //    所以它必须是人话。ID 只用来区分"同名但不同任务"的两批文件。
            TaskGroupName = string.IsNullOrWhiteSpace(taskGroupName)
                ? (name ?? "")
                : taskGroupName;

            // 分组名：兜底必须和 PanUrlParser.DisplayNameOf 的返回值**完全一致**，
            // 否则同一家网盘会裂成两个分组（历史上兜底写的是「夸克」，而正常路径
            // 返回「夸克网盘」⇒ 列表里出现「夸克」和「夸克网盘」两组）。
            string group = PanUrlParser.DisplayNameOf(GeZi.Core.Api.PanKind.Quark);
            try
            {
                if (job != null && job.Pending != null)
                    group = PanUrlParser.DisplayNameOf((GeZi.Core.Api.PanKind)job.Pending.PanKind);
            }
            catch { }
            PanGroup = string.IsNullOrWhiteSpace(group)
                ? PanUrlParser.DisplayNameOf(GeZi.Core.Api.PanKind.Quark)
                : group;
        }

        private JobState _state = JobState.Queued;
        public JobState State
        {
            get => _state;
            set { _state = value; OnChanged(nameof(State)); OnChanged(nameof(StatusText)); OnChanged(nameof(DetailText)); OnChanged(nameof(SpeedText)); OnChanged(nameof(CanPlay)); }
        }

        // ─────────────────────────────────────────────────────────────
        // Ketch 式「连接车道」
        // 参考 Ketch 的招牌设计：把**每条连接**可视化成一条"活的车道"。
        // 我们这里按比例映射 —— 256 并发不可能真画 256 格，所以固定 20 格，
        // 用"填充了几格"表达**并发吃满了没有**（这才是用户真正想看的：
        // 是服务端限速、还是客户端没把连接发出去）。
        // 数据来源：MainWindow.OnDiagnostics 从 DiagnosticsSnapshot 填充
        //（ActiveConnections / WorkerCount 都是内核已经采集好的，无需改下载器）。
        // ─────────────────────────────────────────────────────────────

        /// <summary>车道格子总数（固定 20，按比例映射并发数）。</summary>
        public const int LaneSlots = 20;

        private int _activeConns;
        /// <summary>当前活跃连接数（内核 DiagnosticsSnapshot.ActiveConnections）。</summary>
        public int ActiveConnections
        {
            get => _activeConns;
            set
            {
                if (_activeConns == value) return;
                _activeConns = value;
                OnChanged(nameof(ActiveConnections));
                OnChanged(nameof(LaneFilled));
                OnChanged(nameof(LaneText));
            }
        }

        private int _workerCount;
        /// <summary>本次任务实际启用的并发上限（DiagnosticsSnapshot.WorkerCount）。</summary>
        public int WorkerCount
        {
            get => _workerCount;
            set
            {
                if (_workerCount == value) return;
                _workerCount = value;
                OnChanged(nameof(WorkerCount));
                OnChanged(nameof(LaneFilled));
                OnChanged(nameof(LaneText));
            }
        }

        /// <summary>
        /// 车道格子：长度固定 <see cref="LaneSlots"/>，前 N 个为 true（= 点亮）。
        /// N = round(活跃/上限 × 20)，所以"格子亮了多少"直接反映并发占用率。
        /// </summary>
        /// <remarks>
        /// 🚨 2026-10-31：原先这里是个返回 <c>bool[]</c> 的属性，每次读取都新建数组，
        /// 绑到 <c>ItemsControl.ItemsSource</c> 上 → 进度每 500ms 报一次，每个在跑的
        /// 任务就要把 20 个 Border 全部销毁重建（3 个并发 = 每秒 120 个 Border 的创建销毁）。
        /// 现在改成只暴露**点亮的格子数**，由自绘控件 <see cref="LaneStrip"/> 画出来：
        /// 一个元素、零子项、零数据绑定。
        /// </remarks>
        public int LaneFilled
        {
            get
            {
                int wc = _workerCount;
                int ac = _activeConns;
                if (ac < 0) ac = 0;
                if (wc > 0 && ac > wc) ac = wc;
                // 小并发时按比例四舍五入会得到 0 → 20 格全灰，看起来像「车道根本没显示」。
                // 只要确实有活跃连接就至少点亮一格，让用户看得到「连接在跑」。
                int filled;
                if (wc > 0)
                {
                    filled = (int)System.Math.Round((double)ac / wc * LaneSlots);
                }
                else
                {
                    // 上限还没送到（内核要等分片规划完），但有活跃连接 → 先亮一格占位，
                    // 避免整条车道看起来是空的。
                    filled = ac > 0 ? 1 : 0;
                }
                if (ac > 0 && filled < 1) filled = 1;
                if (filled > LaneSlots) filled = LaneSlots;
                return filled;
            }
        }

        /// <summary>车道文字："12/16 连接"。未开始（无并发上限）时为空串。</summary>
        /// <remarks>
        /// 上限没拿到时（内核要等分片规划完才写 workerCountBox，
        /// 刚起步的前几秒确实会是 0），**不要**把整段文字吞掉 —— 有活跃连接就先显示出来。
        /// 这同时是个探针：若长期只看到「12 连接」而永远变不成「12/16 连接」，
        /// 说明是 WorkerCount 没送到（内核侧），而不是任务匹配失败（UI 侧），一眼可辨。
        /// </remarks>
        public string LaneText
        {
            get
            {
                if (_workerCount > 0) return _activeConns + "/" + _workerCount + " 连接";
                return _activeConns > 0 ? _activeConns + " 连接" : "";
            }
        }

        /// <summary>
        /// 这个任务现在能不能「播放」—— 决定任务行右边那个「播放」按钮显不显示。
        /// </summary>
        /// <remarks>
        /// 共写模式已物理删除，落盘方式唯一「独立分片 + 边合并」：
        /// 目标文件不预分配、从 0 连续增长 → 播放器看到的是"被截断但内容连续"的文件，
        /// 这正是能正常边下边播的情况。因此下载中即可播放，无需再按写入方式判断。
        /// </remarks>
        public bool CanPlay
        {
            get
            {
                if (Job == null) return false;
                return true;
            }
        }

        private string _error = "";
        public string Error
        {
            get => _error;
            set { _error = value ?? ""; OnChanged(nameof(Error)); OnChanged(nameof(DetailText)); }
        }

        private double _percent;
        public double Percent
        {
            get => _percent;
            set
            {
                // 🚨 值没变就什么都不做（2026-10-31）。进度帧每秒上百次，
                //    每次无条件 OnChanged 都会让绑定重算目标属性 ——
                //    2000 行的列表里这是纯粹的浪费（值根本没变，画出来一模一样）。
                if (value == _percent) return;
                _percent = value;
                OnChanged(nameof(Percent));
                OnChanged(nameof(PercentText));
            }
        }

        /// <summary>
        /// 当前阶段说明（来自核心层的 <see cref="GeZi.DownloadEngine.DownloadProgress.Phase"/>）。
        /// 空串 = 常规下载阶段。非空时 <see cref="StatusText"/> 直接显示它
        /// （例如「正在合并分片」），避免收尾期界面看起来像卡死。
        /// </summary>
        private string _phase = "";
        public string Phase
        {
            get => _phase;
            set
            {
                var v = value ?? "";
                if (v == _phase) return;          // 每 500ms 上报一次，值不变就别刷 UI
                _phase = v;
                OnChanged(nameof(Phase));
                OnChanged(nameof(StatusText));
            }
        }

        private double _speed;
        public double Speed
        {
            get => _speed;
            // SpeedText 要看它决定显示速度还是「0 B/s」/留空（见下）。
            set
            {
                // 值没变就别推（暂停中、卡住、0 速度时这一帧毫无意义）
                if (value == _speed) return;
                _speed = value;
                OnChanged(nameof(SpeedText));
                OnChanged(nameof(StatusText));
            }
        }

        /// <summary>
        /// 用户**已经按过暂停**（界面立刻显示「已暂停 / 0 B/s」），
        /// 但下载器内部还在收尾（worker 要跑完手上那一次读）。核心层在此期间仍会报 Downloading。
        /// </summary>
        /// <remarks>
        /// 暂停是**闸门式**的：`PauseToken.WaitIfPausedAsync` 让 worker
        /// 在每次读完之后才阻塞，所以在途的请求要跑完。而核心层的进度上报
        /// `JobProgressAdapter` **永远报 `Downloading`**（见 DownloadScheduler），
        /// 于是 UI 刚设好的 `Paused` 会被下一帧进度覆盖掉 ——
        /// 表现就是用户看到的「图标先变成三角、随后又变回两个竖杠」。
        /// 所以：置位后，<c>OnJobUpdate</c> 会**忽略核心报来的 Downloading**，
        /// 并且**冻结速度**（不收核心上报的残余速度）。
        /// 暂停**即时**生效：不再有「暂停中…」这个渐降中间态，
        /// 一按就是「已暂停 / 0 B/s」（速度由 <c>MainWindow.PauseTask</c> 立刻置 0）。
        /// </remarks>
        private bool _pauseRequested;
        public bool PauseRequested
        {
            get => _pauseRequested;
            set
            {
                _pauseRequested = value;
                OnChanged(nameof(PauseRequested));
                OnChanged(nameof(StatusText));
                OnChanged(nameof(DetailText));
            }
        }

        private long _done;
        public long Done
        {
            get => _done;
            set
            {
                // 只有才刷新时间戳。
                // 进度回调每 500ms 就来一次，若无条件刷新，LastProgressUtc 永远是"刚刚"，
                // 保活那边的"无进展超时"就永远判不出来（卡死的任务会被当成还在跑）。
                if (value != _done)
                {
                    _done = value;
                    LastProgressUtc = DateTime.UtcNow;
                }
                OnChanged(nameof(DoneText));
            }
        }

        /// <summary>
        /// 最近一次**进度真的前进**的时刻（UTC）。
        /// 供 <see cref="SleepGuard"/> 的「无进展超时」判定使用：
        /// 任务卡死时（worker 卡在掐不断的调用里、`Task.WhenAll` 永不返回）
        /// 状态会**永远停在 Downloading**，仅看状态无法区分"在下载"和"卡死了"，
        /// 必须靠"多久没进展"来兜底，否则电脑会整夜不睡。
        /// </summary>
        public DateTime LastProgressUtc { get; private set; } = DateTime.UtcNow;

        private long _total;
        public long Total
        {
            get => _total;
            set
            {
                // 总大小在一次任务里基本恒定，每帧无条件刷 DoneText 纯浪费
                if (value == _total) return;
                _total = value;
                OnChanged(nameof(DoneText));
            }
        }

        public string StatusText
        {
            get
            {
                switch (State)
                {
                    case JobState.Queued: return "排队中";
                    case JobState.Downloading:
                        // 收尾（合并分片）阶段：核心层会上报阶段说明。
                        // 这段**没有任何网络流量**、速度自然为 0，若仍显示「下载中」+ 0 速度，
                        // 用户会以为程序卡死（实测反馈「就显示 0 kb 不动」）。
                        return string.IsNullOrEmpty(_phase) ? "下载中" : _phase;
                    case JobState.Paused:
                        // 暂停**即时**生效：取消「暂停中…」这个中间态。
                        // 原设计是"在途请求跑完、速度渐降到 0 才叫已暂停"，但用户看到速度
                        // 从 20MB/s 慢慢往下滑，第一反应是"暂停没生效、还在下"。
                        // 现在速度由 PauseTask 一按就置 0，这里直接给确定文案。
                        return "已暂停";
                    case JobState.Completed: return "完成";
                    case JobState.Failed: return "失败";
                    case JobState.Cancelled: return "已取消";
                    case JobState.Cancelling: return "取消中…";
                    default: return "";
                }
            }
        }

        /// <summary>状态 + 失败原因，用于任务列表显示，确保错误一眼可见。</summary>
        public string DetailText
        {
            get
            {
                if (State == JobState.Failed && !string.IsNullOrEmpty(Error))
                    return "失败: " + Error;
                if (State == JobState.Completed && !string.IsNullOrEmpty(Error))
                    return StatusText + " · " + Error;
                return StatusText;
            }
        }

        /// <summary>
        /// 百分比文本。
        /// 🚨 **必须截断，不能四舍五入** ——
        /// 原来直接 `ToString("F1")`，于是 99.95% 会被舍入成「**100.0%**」：
        /// 用户看到 100% 却还在跑、文件也没下完（实测反馈：「明明9gb只下了8.99gb却显示100%」）。
        /// 改成先向下取整到 0.1 再格式化 ⇒ 只有**真正** 100% 才会显示 100.0%。
        /// </summary>
        public string PercentText
        {
            get
            {
                if (_percent >= 100.0)
                    return "100.0%";
                double truncated = Math.Floor(_percent * 10.0) / 10.0;
                if (truncated < 0) truncated = 0;
                return truncated.ToString("F1") + "%";
            }
        }

        /// <summary>
        /// 速度文本。只有在"下载中"才展示速度，其余状态返回空串——
        /// 否则任务完成/暂停后最后一帧的残留速度会一直挂在界面上，看起来像还在跑。
        /// </summary>
        public string SpeedText
        {
            get
            {
                // 暂停时明确显示「0 B/s」：
                // 用户要的是"一按暂停、速度就是 0"这个确定感。原来暂停期间还会把在途请求的
                // 残余速度显示出来（渐降到 0），看起来像"暂停没生效"。
                if (State == JobState.Paused)
                    return "0 B/s";
                if (State != JobState.Downloading)
                    return "";       // 完成/失败/取消：不留残余速度
                // 阈值用 < 1 而不是 <= 0：_speed 是 double，
                // 真实速度掉到 0.5 B/s 时 (long)_speed == 0，会显示成"0 B/s" ——
                // 用户看到的就是"速度归零"的假象。真实速度 < 1 B/s 与"还没测出来"
                // 在观感上没区别，一律显示"…"更诚实。
                if (_speed < 1.0)
                    return "…";
                return Util.FormatSize((long)_speed) + "/s";
            }
        }

        public string DoneText => _total > 0
            ? Util.FormatSize(_done) + " / " + Util.FormatSize(_total)
            : Util.FormatSize(_done);

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
