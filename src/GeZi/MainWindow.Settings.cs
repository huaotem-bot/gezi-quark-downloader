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
// 成员按功能归组，全部成员声明在同一 partial 类里，作用域与拆分前完全一致。
// 职责：设置读写、启动项、代理

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;   // ToggleButton（续传列表的真复选框）
using System.Windows.Data;                  // Binding（同上）
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;   // RenderTargetBitmap（主题切换的双快照）
using System.Windows.Threading;
using GeZi.Core.Api;
using GeZi.DownloadEngine;
using GeZi.Core.Models;
using GeZi.Core.Support;
using GeZi.Services;
using System.Text.RegularExpressions;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using SelectionMode = System.Windows.Controls.SelectionMode;
using MessageBox = System.Windows.MessageBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Run = System.Windows.Documents.Run;
using TextTrimming = System.Windows.TextTrimming;
using DiagClock = System.Diagnostics.Stopwatch;
using GeZi.Providers;
using GeZi.Providers.Quark;
using GeZi.Providers.Uc;
using GeZi.Providers.Aliyun;
using GeZi.Providers.Xunlei;
using GeZi.Providers.Shared;
using GeZi.Providers.Qr;
using ShapePath = System.Windows.Shapes.Path;   // 避免与 System.IO.Path 撞名

namespace GeZi
{
    public partial class MainWindow : Window
    {

        // ---------------- 窗口圆角 ----------------
        // WPF 自己没法给顶层窗口加圆角（除非开 AllowsTransparency，那会关掉硬件加速、
        // 影响最大化行为，对一个持续刷新的下载界面不划算）。
        // Windows 11 的 DWM 提供了原生圆角，一次调用即可，且由系统裁剪内容、不留锯齿。

        /// <summary>
        /// 给窗口加系统级圆角（实现在 <see cref="DialogChrome.ApplyRoundedCorners"/>，
        /// 小窗共用同一份）。Win11 生效；Win10 上静默忽略 —— 圆角只是观感，
        /// 不该因为它让程序出问题，也**不写日志**（成功是常态，写进日志只是噪音）。
        /// </summary>
        private void ApplyWindowRoundedCorners()
        {
            DialogChrome.ApplyRoundedCorners(this);
        }


        /// <summary>
        /// 设置页「跟随系统代理」开关（2026-10-11 新增）。
        /// 走独立处理器而不是 <see cref="OnSettingChanged"/>（同 <see cref="OnClipboardAutoParseChanged"/>
        /// 的理由）：这个开关只写一个核心层静态标志，不需要重建下载调度器 ——
        /// 为它重建调度器反而会打断在途任务。
        /// ⚠️ 静态开关只影响**此后新建**的连接：已发起的请求不会改道，
        ///    所以日志里要跟用户讲清楚"重新开始下载 / 重新登录后生效"。
        /// </summary>
        private void OnUseSystemProxyChanged(object sender, RoutedEventArgs e)
        {
            if (_applyingSettings) return;
            try
            {
                bool on = UseSystemProxyCheck.IsChecked == true;
                _settings.UseSystemProxy = on;
                SettingsStore.Save(_settings);
                NetworkOptions.UseSystemProxy = on;
                Log(on
                    ? "已开启「跟随系统代理」：之后新建的连接会走 Windows 系统代理（重新开始下载 / 重新登录后生效）。"
                    : "已关闭「跟随系统代理」：之后的连接一律直连。");
            }
            catch (Exception ex)
            {
                Log("代理设置保存失败: " + ex.Message);
            }
        }


        /// <summary>
        /// 滑到底部时继续加载：缓冲里还有没露的直接露，取完了才去要下一页。
        /// </summary>
        private async void LoadMoreItems()
        {
            if (_dirLoadingMore || _curDir == null) return;

            if (_files.Count < _curDir.Items.Count)
            {
                RevealNextBatch();
                UpdateDirMoreHint();
                return;
            }
            if (!_curDir.HasMore) return;      // 真到底了

            _dirLoadingMore = true;
            int token = _dirLoadToken;
            string fid = _curFid;
            try
            {
                var page = await FetchDirPageAsync(fid, _curDir.NextPage);
                if (token != _dirLoadToken) return;   // 期间又切了目录 → 作废
                _curDir.Items.AddRange(SortDirEntries(page.Items));
                _curDir.NextPage++;
                _curDir.HasMore = page.HasMore;
                RevealNextBatch();
                LogDetail("继续加载：本页 " + page.Items.Count + " 项（已取 "
                    + _curDir.Items.Count + " 项）");
            }
            catch (Exception ex)
            {
                Log("加载更多失败: " + ex.Message);
            }
            finally
            {
                _dirLoadingMore = false;
                UpdateDirMoreHint();
            }
        }


        private async Task<List<DownloadItem>> NoSaveGetItemsAsync(List<ShareFileItem> files,
            CancellationToken ct = default(CancellationToken))
        {
            ct.ThrowIfCancellationRequested();
            // 重解析分享所需的上下文。
            // ⚠️ 必须在**任何 await 之前**取快照：下载准备期间用户可能双击进别的目录，
            // _pathList 会变（提取码同理，下载器回调更不能回头读 PassBox）。
            string shareDir = CurrentShareRelDir();
            string passcode = _passcode;
            string startFid = _startFid;
            var targets = files.Select(f => new LinkTarget { Fid = f.Fid, Token = f.Token, Name = f.Name }).ToList();
            var urlOf = await FetchUrlsWithRetryAsync(targets, fromShare: true, ct: ct);
            var items = new List<DownloadItem>();
            foreach (var f in files)
            {
                DownloadLink link;
                if (urlOf.TryGetValue(f.Fid, out link))
                {
                    var item = new DownloadItem
                    {
                        Name = f.Name, Size = f.Size, Url = link.DownloadUrl, RelDir = f.RelDir,
                        Md5 = link.Md5, Sha1 = link.Sha1, Sha256 = link.Sha256,
                        Fid = f.Fid, Token = f.Token, FromShare = true,
                        // f.RelDir 是「相对当前浏览目录」的，拼上 shareDir 才是「相对分享进入点」的完整路径
                        Passcode = passcode, StartFid = startFid,
                        ShareRelDir = Path.Combine(shareDir, f.RelDir ?? ""),
                        StoredKind = _panKind,
                    };
                    // 阿里/迅雷分享取链会先转存一份，这个 id 要留到任务结束再删。
                    // ⚠️ 必须走 NoteStored：它同时落盘，崩溃后下次启动能补删。
                    NoteStored(item, _panKind, link.StoredId);
                    items.Add(item);
                }
                else
                    LogDetail("跳过（无直链）: " + f.Name);
            }
            return items;
        }


        private async Task<List<DownloadItem>> SaveAndGetItemsAsync(List<ShareFileItem> files,
            CancellationToken ct = default(CancellationToken))
        {
            var items = new List<DownloadItem>();
            ct.ThrowIfCancellationRequested();

            // 🚨 转存用的客户端必须是**当前分享那一家**（夸克 / UC），不能用 _client。
            //    用户实测：登录着迅雷时下 UC 分享，_client 是 null → 整批失败。
            QuarkClient transit = TransitClient;
            if (transit == null)
            {
                Log("当前分享网盘不支持「转存后下载」，已改走免转存路径。");
                return await NoSaveGetItemsAsync(files, ct);
            }

            string toFid = await EnsureFolderAsync(QuarkConstants.DefaultFolder);
            if (string.IsNullOrEmpty(toFid))
            {
                Log("无法创建中转文件夹，转存失败");
                return items;
            }

            for (int i = 0; i < files.Count; i += 50)
            {
                ct.ThrowIfCancellationRequested();   // 每批转存前查一次 → 终止不必等整批跑完
                var batch = files.Skip(i).Take(50).ToList();
                var fids = batch.Select(f => f.Fid).ToList();
                var tokens = batch.Select(f => f.Token).ToList();
                List<string> newFids;
                try
                {
                    newFids = await transit.SaveShareAndGetFidsAsync(fids, tokens, toFid, _pwdId, _stoken, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;   // 终止入队：原样上抛（否则会被下面的 catch 吞成"转存失败"继续跑）
                }
                catch (QuarkApiException ex) when (QuarkClient.IsCapacityMessage(ex.Message))
                {
                    // 容量不足：不用"continue 跳过这批"，而是整批降级 ——
                    // 容量满时后续每批都会失败，继续试只是浪费时间与请求额度。
                    throw new CapacityDowngradeException();
                }
                catch (Exception ex)
                {
                    Log("转存失败: " + ex.Message);
                    continue;
                }

                if (newFids.Count > 0)
                {
                    // 🚨 转存**一旦成功就立刻登记待清理** —— 不能像原来那样等到本批循环
                    // 末尾再加：中途若被「终止入队」打断（下面的 ThrowIfCancellationRequested
                    // 或取链抛取消），就会漏登记，网盘里留下**孤儿中转文件**。
                    // （这类"删除漏登记"是本项目栽过的坑，宁可提前登记。）
                    lock (_cleanupLock)
                        _pendingCleanupFids.AddRange(newFids);

                    ct.ThrowIfCancellationRequested();

                    // 懒校验（2026-10-24 修「点下载要等 5~6 秒」）。
                    // 转存接口返回的 save_as_top_fids 就是转存后的新 fid，可以直接取链。
                    // 原实现先 ListDirAsync 校验一遍，但列表排序是 file_type:asc（文件夹优先），
                    // 中转文件夹里旧文件夹一多，刚转存的文件就排不进前 50 条 -> 永远命中不了
                    // -> 白跑满 6 轮（0+1+1.5+2+2.5+3 = 10 秒）。改成只在取链失败时才回头校验。
                    var targets = new List<LinkTarget>();
                    for (int j = 0; j < newFids.Count; j++)
                    {
                        targets.Add(new LinkTarget
                        {
                            Fid = newFids[j],
                            Name = j < batch.Count ? batch[j].Name : newFids[j],
                        });
                    }
                    // 🚨 transit: true —— 这些 fid 在「转存用的中转文件夹」里，
                    //    归属**当前分享那一家**（夸克或 UC）的账号，必须用 TransitClient 取链。
                    //    用 _client 会在「登录迅雷、下 UC 分享」时拿错客户端 → 取链全失败。
                    var urlOf = await FetchUrlsWithRetryAsync(targets, fromShare: false, ct: ct, transit: true);

                    if (urlOf.Count == 0)
                    {
                        // 一个都没取到 -> 可能是转存没真正落盘，这时才做 ListDir 校验
                        ct.ThrowIfCancellationRequested();
                        newFids = await ResolveSavedFidsAsync(toFid, newFids);
                        if (newFids.Count == 0)
                        {
                            LogDetail("转存后校验：目标目录里找不到刚转存的文件，本批跳过");
                            continue;
                        }
                        targets = new List<LinkTarget>();
                        for (int j = 0; j < newFids.Count; j++)
                        {
                            targets.Add(new LinkTarget
                            {
                                Fid = newFids[j],
                                Name = j < batch.Count ? batch[j].Name : newFids[j],
                            });
                        }
                        urlOf = await FetchUrlsWithRetryAsync(targets, fromShare: false, ct: ct, transit: true);
                    }

                    for (int j = 0; j < newFids.Count && j < batch.Count; j++)
                    {
                        DownloadLink link;
                        if (urlOf.TryGetValue(newFids[j], out link))
                            items.Add(new DownloadItem
                            {
                                Name = batch[j].Name, Size = batch[j].Size, Url = link.DownloadUrl, RelDir = batch[j].RelDir,
                                Md5 = link.Md5, Sha1 = link.Sha1, Sha256 = link.Sha256,
                                // 转存后要按"新 fid"取链，所以这里记的是转存后的 fid，不是分享里的
                                Fid = newFids[j], FromShare = false,
                            });
                        else
                            LogDetail("无直链: " + batch[j].Name);
                    }
                }
            }
            return items;
        }


        private void LoadSettingsToUi()
        {
            // 置位期间控件赋值会触发 SelectionChanged/Click，必须挡住，
            // 否则会用"界面上的默认值"反向覆盖用户已保存的配置。
            _applyingSettings = true;
            try
            {
                ThreadsBox.SelectedIndex = PresetToIndex(_settings.Threads);
                HddThreadsBox.SelectedIndex = HddPresetToIndex(_settings.HddMaxConcurrency);
                ConcurrentBox.Text = _settings.ConcurrentTasks.ToString();
                OutDirBox.Text = _settings.OutDir;
                AutoCleanCheck.IsChecked = _settings.AutoClean;
                // 2026-11-01（用户拍板方案 B）：设置页「分片/缓存根目录」输入框已整块删除，
                // 这里不再回填任何控件。
                // ⚠️ 不要顺手把 PartsRoot 也清空 —— 老配置里它可能指向一个真实存在的老分片目录，
                //    AllPartsRoots() 还要靠它去扫历史 .qparts 残留（见 MainWindow.Download.cs）。
                // 2026-10-26：「落盘方式」整行已从设置页删除（策略固定，无选项），
                // 这里不再刷新 StorageModeInfo 的悬停说明。
                CloseActionBox.SelectedIndex = (int)_settings.OnCloseButton;
                // 剪贴板自动解析（默认开）。注意这里**只回填控件**：真正的注册在窗口句柄
                // 建好之后由 AttachWindowHooks→ApplyClipboardListener 做。
                ClipboardAutoParseCheck.IsChecked = _settings.ClipboardAutoParse;
                // 跟随系统代理（默认关 = 直连）
                UseSystemProxyCheck.IsChecked = _settings.UseSystemProxy;
                // 外观主题：把配置里的选择回填到三个单选（_applyingSettings 已挡掉 Checked）
                SyncThemeRadios();

                // 多网盘凭证回填。明文直接显示在输入框里 —— 它们只在本机、只给本机用户看，
                // 且落盘是加密的；这里再遮成圆点反而会让用户没法核对是否粘对。
                try
                {
                    UcCookieBox.Text = _settings.UcCookie ?? "";
                    // 回填：优先可续期的 refresh_token；只有短期令牌时带 at: 前缀显示，
                    // 让用户一眼看出"这条两小时后会失效"，而不是以为永久有效。
                    AliyunTokenBox.Text = !string.IsNullOrEmpty(_settings.AliyunRefreshToken)
                        ? _settings.AliyunRefreshToken
                        : (!string.IsNullOrEmpty(_settings.AliyunAccessToken)
                            ? "at:" + _settings.AliyunAccessToken
                            : "");
                    XunleiTokenBox.Text = _settings.XunleiPanToken ?? "";
                    // ⚠️ 不再回填 x-captcha-token：登录页已删掉那个输入框
                    //    （XunleiClient.InitCaptchaAsync() 会自动申请，见 AGENTS.md 10.3）。
                    //    _settings.XunleiCaptchaToken 字段保留，只作为自动申请失败时的历史兜底。
                }
                catch { }

                // 内嵌登录的可用性（运行时缺失时置灰按钮，不阻塞任何东西）
                RefreshUcRuntimeStatus();
                // 四张网盘卡的状态（已登录 / 凭证已保存 / 未登录）
                RefreshPanCards();
            }
            finally
            {
                _applyingSettings = false;
            }
            // 把「跟随系统代理」告知核心层（静态开关，影响之后新建的所有连接）。
            // ⚠️ 必须在**建 QuarkClient / 起下载之前**写进去，否则首批连接仍按旧值建。
            NetworkOptions.UseSystemProxy = _settings.UseSystemProxy;
            // 把诊断日志接进界面日志框：外部启动是否成功、ShellExecute 返回码多少，
            // 都要能看到 —— 否则每次失败都只能猜。
            ShellLaunch.Log = Log;
            // 探测本机播放器，供设置页展示 + 「打开」按钮选用
            RefreshPlayerStatus();
        }


        /// <summary>
        /// 设置改为**即时生效**，不再有「保存设置」按钮。
        /// 各控件的触发时机（见 MainWindow.xaml 的事件绑定）：
        ///   · ComboBox  → SelectionChanged（选中即生效）
        ///   · CheckBox  → Click（勾选即生效）
        ///   · TextBox   → LostFocus（移开焦点才生效）+ KeyDown 回车立刻生效
        /// 为什么文本框不用 TextChanged：每敲一个字符都会 RecreateScheduler()，
        /// 正在下载时反复重建调度器代价很大，而且中间态（如 "1" → "12"）会被
        /// 当成有效值落盘。用 LostFocus 是最稳的折中。
        /// _applyingSettings 是重入保护：LoadSettingsToUi() 里给控件赋值同样会
        /// 触发 SelectionChanged/Click，若不挡住会在启动时把默认值写回覆盖用户配置。
        /// </summary>
        private void OnSettingChanged(object sender, RoutedEventArgs e)
        {
            if (_applyingSettings) return;
            ApplySettings();
        }


        /// <summary>
        /// 把「机械盘连接上限」的字节档位映射成下拉框索引（取最接近的档）。
        /// 老配置文件里没有这个字段时会被 Settings.Load 补成 128，这里再兜一次。
        /// </summary>
        private static int HddPresetToIndex(int threads)
        {
            int best = 3, bestDiff = int.MaxValue;
            for (int i = 0; i < HddThreadPresets.Length; i++)
            {
                int diff = Math.Abs(HddThreadPresets[i] - threads);
                if (diff < bestDiff) { bestDiff = diff; best = i; }
            }
            return best;
        }


        /// <summary>文本框里按回车 = 立刻生效（不必先点别处）。</summary>
        private void OnSettingKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (_applyingSettings) return;
            ApplySettings();
            e.Handled = true;
        }


        /// <summary>
        /// 从界面读取全部设置项 → 落盘 → 重建调度器。
        /// 由 OnSettingChanged / OnSettingKeyDown 调用；没有"保存"按钮了。
        /// </summary>
        private void ApplySettings()
        {
            try
            {
                int conc;
                int threads = SelectedThreads();
                int.TryParse((ConcurrentBox.Text ?? "").Trim(), out conc);

                _settings.Threads = threads;

                if (conc < 1) conc = 1;
                if (conc > 64) conc = 64;
                _settings.ConcurrentTasks = conc;

                // ⚠️ 必须规范化：用户手输的可能是 `C:/a/b`（正斜杠），
                // 而 Path.Combine 拼文件名时用 `\` → 会得到混合斜杠路径，
                // 外部播放器（VLC）会把它当 URL 协议头而打不开（2026-10-04 实测）。
                _settings.OutDir = NormalizeDir(OutDirBox.Text);
                // 🚨 用户把输入框清空 → 存成空串 → 之后 Path.Combine("", 子目录) 是相对路径，
                // 会落到进程当前工作目录（用户会以为「文件不见了」）。
                // 这里直接补回系统下载目录，别把空值写进配置。
                if (string.IsNullOrEmpty(_settings.OutDir))
                {
                    _settings.OutDir = Path.Combine(Util.GetDownloadsFolder(), "鸽子下载");
                    OutDirBox.Text = _settings.OutDir;
                }
                _settings.AutoClean = AutoCleanCheck.IsChecked == true;
                // 2026-11-01（方案 B）：输入框已删除，这里不再取值、不再改写 PartsRoot，
                // 也不再调 RegisterPartsDirHistory（没有入口能再改它，记历史已无意义）。
                // 落盘策略 2026-10-24 起固定为「共写 + 写合并」（大厂形态），不再由用户选择。
                // 字段保留只为兼容老配置：这里恒写 1，避免旧的 -1/0 在新版里被误读。
                _settings.StorageMode = 1;
                // 机械盘连接上限：用户自己选档位（32~256）。固态盘任务不受影响。
                int hddIdx = HddThreadsBox.SelectedIndex;
                if (hddIdx < 0 || hddIdx >= HddThreadPresets.Length) hddIdx = 3;   // 默认 128
                _settings.HddMaxConcurrency = HddThreadPresets[hddIdx];
                _settings.OnCloseButton = (CloseAction)Math.Max(0,
                    Math.Min(2, CloseActionBox.SelectedIndex));

                // 多网盘凭证：手动粘贴，落盘时由 SettingsStore 统一 DPAPI 加密。
                // 用 try 兜住 —— 这几个控件在旧布局里可能不存在，不能因为一个 null 就丢掉整次保存。
                try
                {
                    _settings.UcCookie = (UcCookieBox.Text ?? "").Trim();
                    {
                        // 允许用户在框里贴 at:… （短期令牌）或 rt:… / 裸 refresh_token。
                        string aliRaw = (AliyunTokenBox.Text ?? "").Trim();
                        if (aliRaw.StartsWith("at:", StringComparison.Ordinal))
                        {
                            _settings.AliyunAccessToken = aliRaw.Substring(3);
                            _settings.AliyunRefreshToken = "";
                        }
                        else
                        {
                            _settings.AliyunRefreshToken = aliRaw.StartsWith("rt:", StringComparison.Ordinal)
                                ? aliRaw.Substring(3)
                                : aliRaw;
                            _settings.AliyunAccessToken = "";
                        }
                    }
                    _settings.XunleiPanToken = (XunleiTokenBox.Text ?? "").Trim();
                    // x-captcha-token 输入框已删除，这里不再写 _settings.XunleiCaptchaToken：
                    // 自动申请的 token 由 XunleiClient 内部持有，不落盘（临时、5 分钟过期）。
                }
                catch { }

                SettingsStore.Save(_settings);
                RecreateScheduler();
                // 凭证可能刚被粘贴或清空 → 四张网盘卡的状态跟着变
                RefreshPanCards();

                Log(string.Format("设置已生效（单文件并发 {0} 路，同时下载 {1} 个）", threads, conc));
            }
            catch (Exception ex)
            {
                Log("设置保存失败：" + ex.Message);
            }
        }
    }
}
