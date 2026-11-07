using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace GeZi
{
    /// <summary>
    /// 「灵动岛」——加完下载任务后的轻提示条。
    /// <para>
    /// 用户要求（2026-10-26）：点「下载」后不要直接跳到任务页，先在底部弹一条提示，
    /// 想看再点「查看」过去。多次添加会合并成一条，不会叠一堆出来。
    /// </para>
    /// <para>
    /// 持续时间：用户 2026-10-27 要求从 7 秒改成 <b>2 秒</b>（原来代码里是 8 秒，
    /// 用户按观感说的 7 秒）。鼠标停在上面时暂停计时，移开继续。
    /// </para>
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>灵动岛自动淡出的时长（秒）。</summary>
        private const int IslandAutoCloseSeconds = 2;

        private int _islandJobs;
        private long _islandBytes;
        /// <summary>顺次取链进度：已入队文件数 / 本次要取的总文件数（0 = 不在取链中）。</summary>
        private int _islandChainQueued;
        private int _islandChainTotal;
        /// <summary>
        /// 取链进度文案的动词。默认「正在准备直链」——
        /// 「链」是内部概念，用户看不懂在等什么（2026-10-30 改）。
        /// </summary>
        private string _islandChainVerb = "正在准备直链";
        /// <summary>
        /// 阶段文案（非空时优先于取链进度显示）。
        /// 用户 2026-10-29 要求：「我希望在入队中就会显示灵动岛，
        /// 如果在入队中的时候是检查入队文件就有相应的文案」。
        /// 于是在「展开文件夹 / 统计文件数」这段还没有具体进度可报的时间里，
        /// 用一句「正在检查文件…」把岛先亮出来，别让用户以为点了没反应。
        /// </summary>
        private string _islandStage;
        /// <summary>
        /// 当前阶段文案是不是「一次性通知」（显示 2 秒后自动收起）。
        /// <para>
        /// 🚨 为什么必须区分：<see cref="SetIslandStage"/> 的文案是为「正在检查文件…」
        /// 这类**持续阶段**设计的 —— <see cref="RestartIslandTimer"/> 的 Tick 里
        /// 「有阶段文案就不关，继续重启计时」，所以它压根不会自己消失，只能由调用方
        /// 再调一次 <c>SetIslandStage(null)</c> 清掉。
        /// 如果「有任务正在进行，暂停任务后再改设置」这种一次性提示也走那条路，
        /// 岛会**永久挂在屏幕上**，用户点了叉才能关。
        /// </para>
        /// </summary>
        private bool _islandStageTransient;
        /// <summary>灵动岛上「已入队 N」的真实目标值（每批 30 个跳一次）。</summary>
        private int _islandTargetQueued;
        /// <summary>灵动岛上实际显示的数字（由 <see cref="PumpIslandQueued"/> 平滑推向目标值）。</summary>
        private int _islandShownQueued;
        /// <summary>把显示数字平滑推向目标值的 30ms 定时器。</summary>
        private System.Windows.Threading.DispatcherTimer _islandPumpTimer;
        private bool _islandOpen;
        private System.Windows.Threading.DispatcherTimer _islandTimer;
        private bool _islandHover;
        /// <summary>鼠标进出事件是否已挂过。挂过就不能再挂 —— 每次显示都 += 会累积 lambda。</summary>
        private bool _islandHooked;


        /// <summary>
        /// 显示（或累加）灵动岛提示。
        /// </summary>
        /// <param name="jobs">本次新增的文件数</param>
        /// <param name="bytes">本次新增的总字节数（未知传 0）</param>
        private void ShowIsland(int jobs, long bytes)
        {
            if (Island == null) return;
            try
            {
                // 已经在显示 → 累加，不重新弹（用户要求"合并成一条"）
                if (_islandOpen)
                {
                    _islandJobs += jobs;
                    if (bytes > 0) _islandBytes += bytes;
                }
                else
                {
                    _islandJobs = jobs;
                    _islandBytes = bytes > 0 ? bytes : 0;
                }

                UpdateIslandText();
                EnsureIslandVisible();

                // 每次（含累加）都重新计时：2 秒没动静就自动淡出；
                // 鼠标停在上面时不计时（用户要求「常驻」和「自动消失」都要）。
                RestartIslandTimer();
            }
            catch { }
        }


        /// <summary>
        /// 确保灵动岛处于可见状态（首次显示时做滑入 + 淡入，并挂鼠标进出）。
        /// 抽出来是为了让「只显示取链进度」也能复用同一套动画。
        /// </summary>
        private void EnsureIslandVisible()
        {
            if (Island == null || _islandOpen) return;
            _islandOpen = true;
            Island.Visibility = Visibility.Visible;
            // 从下方 18px 处滑上来 + 淡入
            var shift = new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            IslandShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, shift);

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
            Island.BeginAnimation(UIElement.OpacityProperty, fade);

            // 鼠标停在岛上时保持常驻，移开才继续计时。
            // 🚨 只能挂一次：以前这两行写在「首次显示」分支里，但分支条件失效时会
            // 反复 += 同一对 lambda，N 次显示后就有 N 对订阅（内存泄漏 + 重复计时）。
            if (!_islandHooked)
            {
                _islandHooked = true;
                Island.MouseEnter += (s2, e2) => { _islandHover = true; StopIslandTimer(); };
                Island.MouseLeave += (s2, e2) => { _islandHover = false; RestartIslandTimer(); };
            }
        }


        /// <summary>重启自动淡出计时（2 秒）。</summary>
        private void RestartIslandTimer()
        {
            StopIslandTimer();
            if (!_islandOpen) return;
            _islandTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(IslandAutoCloseSeconds)
            };
            _islandTimer.Tick += (s, e) =>
            {
                StopIslandTimer();
                if (_islandHover) return;   // 鼠标还在上面 → 不关
                // 一次性通知（如「有任务正在进行…」）会被这里收起，符合预期。
                // 有**持续**阶段文案（正在检查文件…）→ 别收起来，继续显示
                if (!string.IsNullOrEmpty(_islandStage) && !_islandStageTransient)
                {
                    RestartIslandTimer();
                    return;
                }
                // 还在顺次取链 → 别收起来，继续显示「正在取链 X/Y」
                if (_islandChainTotal > 0 && _islandChainQueued < _islandChainTotal)
                {
                    RestartIslandTimer();
                    return;
                }
                HideIsland();
            };
            _islandTimer.Start();
        }


        private void StopIslandTimer()
        {
            if (_islandTimer != null)
            {
                try { _islandTimer.Stop(); } catch { }
                _islandTimer = null;
            }
        }


        /// <summary>本次新增任务的总字节数（未知按 0 计）。</summary>
        private static long SumJobBytes(System.Collections.Generic.List<GeZi.DownloadEngine.DownloadJob> jobs)
        {
            if (jobs == null) return 0;
            long sum = 0;
            for (int i = 0; i < jobs.Count; i++)
            {
                var j = jobs[i];
                if (j != null && j.Pending != null && j.Pending.Size > 0) sum += j.Pending.Size;
            }
            return sum;
        }


        /// <summary>
        /// 顺次取链时刷新灵动岛上的取链进度。
        /// queued = 已入队文件数，total = 本次要取的总文件数；
        /// 取完（queued >= total）后恢复普通「已加入下载队列」文案，计时器照常淡出。
        /// </summary>
        private void SetIslandChainProgress(int queued, int total, string verb = null)
        {
            _islandChainQueued = queued;
            _islandChainTotal = total;
            if (verb != null) _islandChainVerb = verb;
            // 进入取链阶段 → 撤掉「正在检查文件…」这类阶段文案
            _islandStage = null;
            _islandStageTransient = false;
            if (Island == null) return;
            try
            {
                // 🚨 目标值一次跳 30，显示值由定时器平滑追上去（用户要求「一个一个涨」）
                _islandTargetQueued = queued;
                if (_islandShownQueued > _islandTargetQueued)
                    _islandShownQueued = _islandTargetQueued;   // 回退（取消/重来）直接对齐
                UpdateIslandText();
                EnsureIslandVisible();
                RestartIslandTimer();
                if (_islandShownQueued < _islandTargetQueued) StartIslandPump();
            }
            catch { }
        }

        /// <summary>
        /// 取链结束（完成或中断）→ 撤掉灵动岛上的取链进度，让它按常规 2 秒淡出。
        /// 若压根没有下载任务（例如「导出直链」），直接收起，别显示「0 个文件」。
        /// </summary>
        private void ClearIslandChainProgress()
        {
            _islandChainQueued = 0;
            _islandChainTotal = 0;
            _islandChainVerb = "正在准备直链";
            _islandStage = null;
            _islandStageTransient = false;
            _islandTargetQueued = 0;
            StopIslandPump();
            try
            {
                if (_islandJobs <= 0) HideIsland();
                else
                {
                    // 收尾时把显示数字一次补齐，别停在半路
                    _islandShownQueued = 0;
                    UpdateIslandText();
                }
            }
            catch { }
        }

        private void UpdateIslandText()
        {
            if (IslandText == null) return;
            string text;
            if (!string.IsNullOrEmpty(_islandStage))
            {
                // 阶段文案优先（正在检查文件…）：此时还没有具体数字可报
                text = _islandStage;
            }
            else if (_islandChainTotal > 0 && _islandShownQueued < _islandChainTotal)
            {
                // 顺次取链进行中 → 只显示取链进度（此时还没开始下载，别再说「已加入下载队列」）
                // 🚨 显示的是**平滑爬升后的** _islandShownQueued，不是真实值 _islandChainQueued。
                //    用户 2026-10-29：「灵动岛上的文件每次三十三十个的刷新感觉特别不流畅，
                //    如果能数字能够快速一个一个增加看起来是不是就流畅很多」。
                //    真实进度是一批 30 个跳一次，这里用 30ms 的定时器把它拆成 1、2、3… 慢慢爬，
                //    取链本身仍然是批量跑的（一次请求带多个 fid 才快），只有显示是细的。
                //
                // 🚨 2026-10-30：已经有文件入队（说明前面的块真的开始下了）时必须双段表达。
                //    2000 个文件时用户看到「正在准备直链 30/2000」会以为一个都没开始，
                //    实际前 8 个早就在下了 —— 这是用户明确反馈过的误解。
                if (_islandJobs > 0)
                {
                    // 2026-11-01（用户反馈「文案不准确，同时执行取链和下载，却只写直链获取」）：
                    // 两个进度都要报 —— 左边是已入队下载的文件数，右边是取链进度。
                    text = "下载中 " + _islandJobs + " · 直链准备 "
                        + _islandShownQueued + "/" + _islandChainTotal;
                }
                else
                {
                    text = _islandChainVerb + "：" + _islandShownQueued + "/" + _islandChainTotal;
                }
            }
            else
            {
                text = "已加入下载队列：" + _islandJobs + " 个文件";
                if (_islandBytes > 0) text += " · " + Util.FormatSize(_islandBytes);
            }
            IslandText.Text = text;
        }


        /// <summary>
        /// 设置阶段文案（例如「正在检查文件…」）。
        /// 传 null 或空串表示清掉阶段文案，回到取链进度 / 入队结果。
        /// </summary>
        private void SetIslandStage(string stage)
        {
            _islandStage = string.IsNullOrEmpty(stage) ? null : stage;
            // 走这条老入口的一律是持续阶段（「正在检查文件…」），保持原来的常驻语义。
            _islandStageTransient = false;
            if (Island == null) return;
            try
            {
                UpdateIslandText();
                EnsureIslandVisible();
                RestartIslandTimer();
            }
            catch { }
        }


        /// <summary>
        /// 弹一条**一次性**轻提示（2 秒后自动淡出，鼠标悬停时暂停）。
        /// <para>
        /// 与 <see cref="SetIslandStage"/> 的区别只有一个：到点会被
        /// <see cref="RestartIslandTimer"/> 的 Tick 正常收起，不需要调用方再清一次。
        /// 用于「有任务正在进行，暂停任务后再改设置」这类不需要用户持续关注的提示。
        /// </para>
        /// </summary>
        private void ShowIslandNotice(string notice)
        {
            _islandStage = string.IsNullOrEmpty(notice) ? null : notice;
            _islandStageTransient = _islandStage != null;
            if (Island == null) return;
            try
            {
                UpdateIslandText();
                EnsureIslandVisible();
                RestartIslandTimer();
            }
            catch { }
        }


        /// <summary>
        /// 把灵动岛上的取链数字从当前显示值平滑推进到目标值。
        /// <para>
        /// 真实进度是一批 30 个跳一次（一个 HTTP 请求带 30 个 fid），
        /// 直接显示就是 0 → 30 → 60，观感上像卡住之后猛跳一下。
        /// 这里用一个 30ms 的定时器把这段差距摊开：差距大就多走几步，差距小就走 1 步，
        /// 大约 150~250ms 收敛 —— 既跟得上真实进度，看起来又是「一个一个涨」。
        /// </para>
        /// </summary>
        private void PumpIslandQueued()
        {
            if (_islandShownQueued >= _islandTargetQueued)
            {
                StopIslandPump();
                if (_islandShownQueued != _islandTargetQueued)
                {
                    _islandShownQueued = _islandTargetQueued;
                    UpdateIslandText();
                }
                return;
            }
            int gap = _islandTargetQueued - _islandShownQueued;
            // 差距越大步长越大，保证最多 ~6 拍追上（6 × 30ms = 180ms）
            int step = gap <= 2 ? 1 : Math.Max(1, gap / 6);
            _islandShownQueued = Math.Min(_islandTargetQueued, _islandShownQueued + step);
            UpdateIslandText();
            if (_islandShownQueued >= _islandTargetQueued)
                StopIslandPump();
        }


        private void StartIslandPump()
        {
            if (_islandPumpTimer != null) return;
            _islandPumpTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(30)
            };
            _islandPumpTimer.Tick += (s, e) => PumpIslandQueued();
            _islandPumpTimer.Start();
        }


        private void StopIslandPump()
        {
            if (_islandPumpTimer != null)
            {
                try { _islandPumpTimer.Stop(); } catch { }
                _islandPumpTimer = null;
            }
        }


        private void OnIslandGoClick(object sender, RoutedEventArgs e)
        {
            try
            {
                // 双保险：NavList 与 Tabs 是双向同步的，但任一侧的判等分支
                // （i == 对方.SelectedIndex 就 return）在某些中间态下会让赋值变成空操作。
                // 这里两个都设一遍，先 Tabs（触发 OnTabsSelectionChanged 同步 NavList），
                // 再把 NavList 补一次，保证「查看」一定能跳到任务页。
                Tabs.SelectedIndex = 1;
                if (NavList != null && NavList.SelectedIndex != 1)
                    NavList.SelectedIndex = 1;
            }
            catch { }
            HideIsland();
        }


        private void OnIslandCloseClick(object sender, RoutedEventArgs e)
        {
            HideIsland();
        }


        private void HideIsland()
        {
            if (Island == null) return;
            try
            {
                _islandOpen = false;
                _islandJobs = 0;
                _islandBytes = 0;
                _islandChainQueued = 0;
                _islandChainTotal = 0;
                _islandStage = null;
                _islandStageTransient = false;
                _islandTargetQueued = 0;
                _islandShownQueued = 0;
                StopIslandPump();
                StopIslandTimer();

                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
                fade.Completed += (s, e2) =>
                {
                    try
                    {
                        Island.Visibility = Visibility.Collapsed;
                        IslandShift.Y = 18;
                    }
                    catch { }
                };
                Island.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            catch { }
        }
    }
}
