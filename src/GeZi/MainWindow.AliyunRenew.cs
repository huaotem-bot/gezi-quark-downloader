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

// ---- 本文件是 MainWindow 的 partial 之一（代码规范化拆分，2026-10-24） ----
// 职责：阿里云盘令牌的**自动续期** —— 察觉令牌失效就后台重开一次内嵌登录抓新令牌。
//
// 为什么阿里要这么做（2026-10-25 用户指出的办法）：
//   阿里新版把 refresh_token 存在**服务端 session** 里，网页 localStorage 只留一个
//   约两小时就过期的 access_token，所以"拿 refresh_token 换新令牌"这条路走不通
//   （迅雷那条 RefreshAsync 的写法在阿里身上没有对应接口）。
//   但内嵌浏览器（WebView2）用的是**按网盘固定的 profile**（profile-aliyun），
//   里面的登录态比 localStorage 里的令牌活得久得多 —— 只要把窗口重新打开一次，
//   页面自己就会带着会话登录好，程序再读一次 localStorage 就拿到了新鲜令牌。
//   ⇒ 令牌失效时"自动重开一次内嵌登录"是可行的续期手段，用户明确要求做成全自动。
//
// 三条纪律（都是踩过坑总结的）：
//   ① **绝不切换当前账号**：用户要求四家网盘各自保持登录（每家一个），阿里在后台
//      悄悄换了令牌，不该把当前账号从迅雷/夸克切走 —— 只有当前账号本来就是阿里时才走
//      完整的 ApplyProviderLoginAsync（那条会重建 provider 并刷新容量）。
//   ② **窗口必须"可见"但放到屏幕外**：WebView2 是 HwndHost，Hide / 最小化 / Opacity=0
//      都会让它不渲染、不执行脚本；只有 WS_VISIBLE 的真实窗口才正常干活。
//   ③ **失败必须冷却**：内嵌浏览器里的会话也可能过期，那样怎么重开都抓不到令牌。
//      不冷却就会变成"每次校验失败都开一次窗口"的死循环。

using System;
using System.Threading.Tasks;
using System.Windows;
using GeZi.Core.Api;
using GeZi.Core.Support;
using GeZi.Services;

namespace GeZi
{
    public partial class MainWindow
    {
        /// <summary>自动续期正在进行中（防重入）。</summary>
        private bool _aliyunAutoRenewing;

        /// <summary>
        /// 手动「内嵌登录」窗口是否正开着。
        /// <para>
        /// WebView2 的 user data folder 按网盘固定（profile-aliyun），**同一份目录不能被
        /// 两个 WebView2 进程同时占用** —— 用户在手动登录阿里时，自动续期必须让路。
        /// </para>
        /// </summary>
        private bool _panWebLoginOpen;

        /// <summary>连续失败后的冷却截止时间。</summary>
        private DateTime _aliyunAutoRenewBlockedUntil = DateTime.MinValue;

        /// <summary>上次尝试时间（防抖：四卡校验与解析失败可能同时触发）。</summary>
        private DateTime _aliyunAutoRenewLastTry = DateTime.MinValue;

        /// <summary>失败后的冷却时长。</summary>
        private static readonly TimeSpan AliyunAutoRenewCooldown = TimeSpan.FromMinutes(10);

        /// <summary>两次自动尝试之间的最小间隔。</summary>
        private static readonly TimeSpan AliyunAutoRenewDebounce = TimeSpan.FromSeconds(30);

        /// <summary>
        /// 单次自动续期的等待上限。
        /// <para>
        /// WebView2 冷启动 3~5 秒 + 页面加载 2~5 秒 + 1.5 秒一轮的令牌轮询，
        /// 正常十来秒就结束；给到 50 秒是留给慢网 / 首次创建 profile 的余量。
        /// </para>
        /// </summary>
        private static readonly TimeSpan AliyunAutoRenewTimeout = TimeSpan.FromSeconds(50);


        /// <summary>
        /// 察觉到阿里令牌失效时调用：在后台重开一次内嵌登录，抓新令牌并落盘。
        /// <para>
        /// 调用点见 <see cref="ValidatePanSessionsAsync"/>（四卡实测）与
        /// <see cref="MainWindow.Browse"/> 的阿里分享解析失败分支。
        /// 全程不需要用户点任何按钮。
        /// </para>
        /// </summary>
        /// <param name="reason">失效原因（写进日志，便于事后判断是真过期还是网络问题）。</param>
        private void TryAutoRenewAliyunCredential(string reason)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => TryAutoRenewAliyunCredential(reason)));
                return;
            }

            // 启动早期 _settings 可能还是 null（LoadSettingsToUi 之前）
            if (_settings == null)
                return;
            if (_aliyunAutoRenewing || _panWebLoginOpen)
                return;

            // 没有 WebView2 运行时就没有"重开内嵌登录"这条路，只能靠手动粘贴
            if (!WebView2Runtime.IsInstalled)
                return;

            // 从没登录过就不该偷偷去登：首次登录必须用户主动（否则等于替用户登录）。
            if (string.IsNullOrWhiteSpace(_settings.AliyunRefreshToken)
                && string.IsNullOrWhiteSpace(_settings.AliyunAccessToken))
                return;

            DateTime now = DateTime.UtcNow;
            if (now < _aliyunAutoRenewBlockedUntil)
                return;
            // 🚨 2026-11-07：冷却期跨重启持久化。
            //   凭证长期失效时，原来每次启动都白开一次 WebView2（50 秒）→ 启动卡顿 + 日志刷屏。
            //   上次失败还在 10 分钟冷却内就别再启动了；用户可随时点阿里卡「登录账号」手动重试。
            try
            {
                long failedTicks = _settings.AliyunRenewFailedTicks;
                if (failedTicks > 0)
                {
                    var failedAt = new DateTime(failedTicks, DateTimeKind.Utc);
                    if (now - failedAt < AliyunAutoRenewCooldown)
                        return;
                }
            }
            catch { }
            if (now - _aliyunAutoRenewLastTry < AliyunAutoRenewDebounce)
                return;

            _aliyunAutoRenewing = true;
            _aliyunAutoRenewLastTry = now;

            Log("阿里云盘令牌已失效（"
                + (string.IsNullOrEmpty(reason) ? "校验未通过" : reason)
                + "），正在后台自动续期…");
            var ignored = RunAliyunAutoRenewAsync();
        }


        /// <summary>
        /// 真正干活的那一半：开一个屏幕外的内嵌登录窗口，等它自己抓到并校验通过令牌。
        /// </summary>
        private async Task RunAliyunAutoRenewAsync()
        {
            bool captured = false;
            string capturedToken = null;
            WebLoginWindow win = null;

            try
            {
                // WebLoginWindow 内部会：加载 https://www.alipan.com/drive（profile 已登录）
                // → 每 1.5 秒读一次 localStorage → 用 AliyunClient.ValidateAsync 真校验
                // → 通过才回调，然后自己 Close()。
                win = new WebLoginWindow(PanKind.Aliyun,
                    tok =>
                    {
                        captured = true;
                        capturedToken = tok;
                    },
                    msg => LogDetail(msg),
                    // 🚨🚨🚨 这里**必须**是 false，绝对不能传 true！
                    //   自动续期的原理正是「profile 里的老会话还活着 →
                    //   重开一次窗口页面自己就登好了 → 读 localStorage 拿到新令牌」。
                    //   一旦清了 Cookie，这个机制会 100% 失效，
                    //   用户会看到「凭证已保存」但阿里一直用不了，且无法自动恢复。
                    //   （2026-11-03 加参数时特意显式写出来，防止以后有人顺手改成 true。）
                    clearSessionOnStart: false);

                // ⚠️ 必须放到虚拟屏幕之外，不能 Hide / 最小化 / 设 Opacity：
                //    WebView2 是 HwndHost，窗口不可见时它不渲染、也不执行脚本。
                win.WindowStartupLocation = WindowStartupLocation.Manual;
                win.Left = SystemParameters.VirtualScreenLeft - 4000;
                win.Top = SystemParameters.VirtualScreenTop - 4000;
                win.ShowInTaskbar = false;
                win.Show();

                DateTime deadline = DateTime.UtcNow + AliyunAutoRenewTimeout;
                while (!captured && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(400).ConfigureAwait(true);
                    // 窗口被关掉（用户强行关 / 内部出错）就没必要再等
                    if (win != null && !win.IsVisible)
                        break;
                }
            }
            catch (Exception ex)
            {
                Log("阿里自动续期出错：" + ex.Message);
            }
            finally
            {
                try
                {
                    if (win != null && win.IsVisible)
                        win.Close();
                }
                catch { }
                _aliyunAutoRenewing = false;
            }

            if (captured && !string.IsNullOrWhiteSpace(capturedToken))
            {
                Log("阿里自动续期成功，正在应用新令牌…");
                try
                {
                    if (_activeKind == PanKind.Aliyun)
                    {
                        // 当前账号就是阿里 → 走完整落地：重建 provider、刷新容量与列表
                        await ApplyProviderLoginAsync(PanKind.Aliyun, capturedToken, false)
                            .ConfigureAwait(true);
                    }
                    else
                    {
                        // 当前账号是别家 → 只静默存下新令牌，**绝不切换当前账号**
                        SaveAliyunCredentialQuietly(capturedToken);
                    }

                    // 令牌是 WebLoginWindow 真校验通过才回调的，这里直接记成"已登录"，
                    // 不必再打一次网络（四卡实测那轮可能正好还在跑，重验会被它挡掉）。
                    _panLive[PanKind.Aliyun] = true;
                    if (!_panNick.ContainsKey(PanKind.Aliyun))
                        _panNick[PanKind.Aliyun] = "";
                    // 🚨 2026-11-07：续期成功 → 清掉跨重启冷却标记。
                    try { _settings.AliyunRenewFailedTicks = 0; SettingsStore.Save(_settings); } catch { }
                    RefreshPanCards();
                    RefreshPanStateList(force: true);
                }
                catch (Exception ex)
                {
                    Log("阿里自动续期应用失败：" + ex.Message);
                }
            }
            else
            {
                _aliyunAutoRenewBlockedUntil = DateTime.UtcNow + AliyunAutoRenewCooldown;
                // 🚨 2026-11-07：失败时刻落盘，重启后仍在冷却期内直接跳过，不再白开 WebView2。
                try { _settings.AliyunRenewFailedTicks = DateTime.UtcNow.Ticks; SettingsStore.Save(_settings); } catch { }
                Log("阿里自动续期未成功（内嵌浏览器里的登录态可能也已过期），"
                    + (int)AliyunAutoRenewCooldown.TotalMinutes
                    + " 分钟内不再自动尝试；需要时点阿里卡的「登录账号」重新登录。");
                RefreshPanCards();
                RefreshPanStateList(force: true);
            }
        }


        /// <summary>
        /// 只把新令牌写进配置并落盘，**不改当前账号**。
        /// <para>
        /// 与 <see cref="ApplyProviderLoginAsync"/> 的区别就在最后这一句：那条会把
        /// <c>_activeKind</c> 改成阿里（"登录即设为当前账号"），而自动续期是后台行为，
        /// 用户当前正在用迅雷/夸克时不该被切走。
        /// </para>
        /// </summary>
        private void SaveAliyunCredentialQuietly(string credential)
        {
            string raw = credential ?? "";
            string at = "";
            string rt = "";
            if (raw.StartsWith("at:", StringComparison.Ordinal))
                at = raw.Substring(3);
            else if (raw.StartsWith("rt:", StringComparison.Ordinal))
                rt = raw.Substring(3);
            else
                rt = raw;   // 手动粘贴 / 旧配置没有前缀，按 refresh_token 处理

            if (!string.IsNullOrEmpty(at))
                _settings.AliyunAccessToken = at;
            if (!string.IsNullOrEmpty(rt))
                _settings.AliyunRefreshToken = rt;

            SettingsStore.Save(_settings);
            Log("阿里云盘新令牌已保存（当前账号保持不变）。");
        }
    }
}
