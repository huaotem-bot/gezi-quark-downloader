using System;
using System.Linq;
using System.Windows;
using GeZi.DownloadEngine;

namespace GeZi
{
    /// <summary>
    /// 「有任务在跑时不许改设置」的守卫。
    ///
    /// <para>
    /// 用户要求（2026-11-07）：任务进行中点「网络与下载」「存储」两张卡里的控件，
    /// 用惯用的灵动岛提示「有任务正在进行，无法设置」，并且**这次改动不生效**。
    /// </para>
    ///
    /// <para>
    /// 🚨 为什么必须真拦而不是只提示：
    ///   并发数 / 机械盘上限改完会 <c>RecreateScheduler()</c>，打断在途任务；
    ///   下载目录改了会让同一个任务的分片落到两个目录（合并时直接找不到片）。
    ///   只弹提示却把值改掉，等于骗用户。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 只拦这两张卡。窗口与后台 / 外观 / 播放器不在此列 ——
    ///   它们跟下载调度无关，改了不影响在跑的任务，拦了只会碍事。
    /// </para>
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>忙碌提示文案。与用户原话保持一致，别改得"更礼貌"。</summary>
        private const string SettingsBusyHint = "有任务正在进行，暂停任务后再改设置";

        /// <summary>
        /// 当前是否有任务在跑。
        /// <para>
        /// 判据与工具栏「暂停全部」按钮完全一致（<see cref="JobState.Downloading"/>
        /// 或 <see cref="JobState.Queued"/>）—— 排队中的任务虽然还没开始下，
        /// 但一恢复就要用新的并发数，这时候改设置同样会让口径错乱。
        /// </para>
        /// <para>
        /// ⚠️ 与「我的网盘」里 <c>Paused</c> 不算忙：暂停正是用户被引导去做的事。
        /// </para>
        /// </summary>
        private bool HasRunningTasks()
        {
            try
            {
                return _tasks.Any(t => t.State == JobState.Downloading || t.State == JobState.Queued);
            }
            catch
            {
                // 理论上不会进：_tasks 是 UI 线程独占的 ObservableCollection。
                // 万一在窗口关闭中途被访问到，按"不拦"处理，别让守卫本身把程序搞挂。
                return false;
            }
        }

        /// <summary>
        /// 忙碌拦截：忙就弹灵动岛提示、返回 <c>true</c>（调用方立刻 return）。
        /// </summary>
        /// <param name="origin">
        /// 触发拦截的控件。非 null 时会把它的界面状态**回滚**成配置里的真实值 ——
        /// ComboBox 的 SelectionChanged / CheckBox 的 Click 都是"先变值后通知"，
        /// 不回滚的话界面显示"已改成 6"而实际还是 3，用户会以为设置坏了。
        /// </param>
        private bool BlockSettingsWhenBusy(object origin)
        {
            if (!HasRunningTasks()) return false;

            RevertSettingControl(origin);

            // 复用灵动岛的观感（滑入 + 淡入 + 2 秒自动淡出 + 悬停常驻），
            // 但必须走 ShowIslandNotice 这条一次性通道：
            // SetIslandStage 是为「正在检查文件…」这类持续阶段设计的，到点不收，
            // 用在一次性提示上会让岛永久挂在屏幕上。
            ShowIslandNotice(SettingsBusyHint);
            return true;
        }

        /// <summary>
        /// <paramref name="control"/> 是不是「有任务在跑时不许改」的那几个。
        /// <para>
        /// 🚨 必须逐个点名，不能拿「卡片里的控件一律拦」当判据：
        ///   <c>OnSettingChanged</c> 是**多张卡共用**的处理器 ——
        ///   「窗口与后台」的关闭行为下拉、登录页那三个凭证输入框都用它。
        ///   按卡片拦会把这些无关控件一起挡住。
        /// </para>
        /// <para>
        /// ⚠️ 「打开」按钮是有意排除的（用户 2026-11-07 拍板）：它只是打开资源管理器，
        ///   不改任何设置，拦了用户会觉得莫名其妙。
        /// </para>
        /// </summary>
        private bool IsGuardedSettingControl(object control)
        {
            return ReferenceEquals(control, ConcurrentBox)
                || ReferenceEquals(control, HddThreadsBox)
                || ReferenceEquals(control, OutDirBox)
                || ReferenceEquals(control, AutoCleanCheck)
                || ReferenceEquals(control, UseSystemProxyCheck)
                || ReferenceEquals(control, PerPanThreadsButton);
        }

        /// <summary>
        /// 把被改动的控件回滚成配置里的真实值。
        /// <para>
        /// 🚨 必须用 <c>_applyingSettings</c> 包住：给控件赋值会再次触发
        /// SelectionChanged / Click，不挡住就会递归回到守卫里，而且会把
        /// "回滚动作"当成一次新的用户操作写盘。
        /// </para>
        /// </summary>
        private void RevertSettingControl(object origin)
        {
            if (origin == null) return;

            bool was = _applyingSettings;
            _applyingSettings = true;
            try
            {
                if (ReferenceEquals(origin, ConcurrentBox))
                {
                    int conc = _settings.ConcurrentTasks;
                    if (conc < 1) conc = 1;
                    if (conc > 6) conc = 6;
                    ConcurrentBox.SelectedIndex = conc - 1;
                }
                else if (ReferenceEquals(origin, HddThreadsBox))
                {
                    HddThreadsBox.SelectedIndex = HddPresetToIndex(_settings.HddMaxConcurrency);
                }
                else if (ReferenceEquals(origin, UseSystemProxyCheck))
                {
                    UseSystemProxyCheck.IsChecked = _settings.UseSystemProxy;
                }
                else if (ReferenceEquals(origin, AutoCleanCheck))
                {
                    AutoCleanCheck.IsChecked = _settings.AutoClean;
                }
                else if (ReferenceEquals(origin, OutDirBox))
                {
                    OutDirBox.Text = _settings.OutDir;
                }
                // 其它 sender（「四家网盘分别设置」「浏览…」）在弹窗前就被拦，
                // 界面状态根本没变，无需回滚。
            }
            catch { }
            finally
            {
                _applyingSettings = was;
            }
        }


        // ==================== 事件入口包装 ====================
        // 每个入口都是「先问守卫，忙就 return」。
        // 🚨 守卫必须写在最前面：ApplySettings() 一旦跑起来就已经落盘 + 重建调度器了。

        /// <summary>「同时下载任务数」「机械盘连接上限」两个下拉。</summary>
        private void OnGuardedSettingChanged(object sender, RoutedEventArgs e)
        {
            if (_applyingSettings) return;
            if (BlockSettingsWhenBusy(sender)) return;
            ApplySettings();
        }


        /// <summary>下载目录输入框（LostFocus / 回车）。</summary>
        private void OnGuardedSettingKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            if (_applyingSettings) return;
            if (BlockSettingsWhenBusy(sender)) return;
            ApplySettings();
            e.Handled = true;
        }


        /// <summary>「转存清理」勾选框。</summary>
        private void OnGuardedSettingClick(object sender, RoutedEventArgs e)
        {
            if (_applyingSettings) return;
            if (BlockSettingsWhenBusy(sender)) return;
            ApplySettings();
        }


        /// <summary>「跟随 Windows 系统代理」勾选框。</summary>
        private void OnGuardedProxyChanged(object sender, RoutedEventArgs e)
        {
            if (_applyingSettings) return;
            if (BlockSettingsWhenBusy(sender)) return;
            OnUseSystemProxyChanged(sender, e);
        }


        /// <summary>「四家网盘分别设置」按钮 —— 弹窗前就拦，别让用户白开一个弹窗。</summary>
        private void OnGuardedPerPanThreadsClick(object sender, RoutedEventArgs e)
        {
            if (BlockSettingsWhenBusy(sender)) return;
            OnPerPanThreadsClick(sender, e);
        }


        /// <summary>「浏览…」按钮 —— 选完目录才会 ApplySettings，但开了对话框才发现不让改很气人。</summary>
        private void OnGuardedBrowseOutDir(object sender, RoutedEventArgs e)
        {
            if (BlockSettingsWhenBusy(sender)) return;
            OnBrowseOutDir(sender, e);
        }
    }
}
