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
using System.Windows;
using Microsoft.Win32;

namespace GeZi
{
    /// <summary>外观主题。存进配置时序列化为字符串（XmlSerializer 对枚举默认写名字）。</summary>
    public enum AppTheme
    {
        /// <summary>跟随系统（读注册表 AppsUseLightTheme，并监听其变化）。</summary>
        System = 0,

        /// <summary>浅色（本程序最初的配色）。</summary>
        Light = 1,

        /// <summary>深色。</summary>
        Dark = 2,
    }

    /// <summary>
    /// 主题切换器（2026-10-04 新增）。
    /// 整个主题系统只有两个色源：<c>Resources/Colors.Light.xaml</c> 与
    /// <c>Resources/Colors.Dark.xaml</c>（键名逐一对齐）。切换时**只把这一层字典换掉**：
    /// <c>Brushes.xaml</c> 里每个画刷的 <c>Color</c> 都是 <c>DynamicResource</c>，
    /// 会在字典替换后自动重解析 —— 于是界面里 **273 处**引用全部跟着变，
    /// 不需要重建任何画刷、也不需要重启。
    /// 那会连 Icons/Styles 一起重建，
    /// 已附着在这些资源上的控件（尤其 <c>Style</c>）可能拿不到新实例；
    /// 只换 Color 层是最小改动面。
    /// ⚠️ <c>Application.Current.Resources.MergedDictionaries</c> 的**顺序不能乱**：
    /// Brushes 必须排在 Colors **之后**（它引用 Colors 的键）。
    /// 所以这里是**原位替换**，不是删除后追加。
    /// </summary>
    internal static class ThemeManager
    {
        /// <summary>把 Colors 字典的 Source 指向对应主题文件（相对 pack URI）。</summary>
        private const string LightSource = "Resources/Colors.Light.xaml";
        private const string DarkSource = "Resources/Colors.Dark.xaml";

        /// <summary>主题真正生效后触发（参数 = 实际生效的是不是深色）。供托盘图标等需要重绘的东西订阅。</summary>
        public static event Action<bool> ThemeChanged;

        private static bool _hooked;
        private static AppTheme _current = AppTheme.System;

        /// <summary>当前用户选择的主题（可能是 System）。</summary>
        public static AppTheme Current { get { return _current; } }

        /// <summary>当前**实际**渲染的是不是深色（System 会解析成真实的亮/暗）。</summary>
        public static bool IsDarkEffective { get; private set; }

        /// <summary>用户改了主题偏好（含「跟随系统」这一选项本身）。</summary>
        public static event Action PreferenceChanged;

        // ================= 对外入口 =================

        /// <summary>
        /// 启动时调用一次：把配置里的主题应用上去，并（只在 System 模式下）挂系统监听。
        /// ⚠️ 必须在 <c>Application.Resources</c> 已加载后调用。
        /// </summary>
        public static void Initialize(AppTheme theme)
        {
            EnsureDictionaryShape();
            Apply(theme, persist: false);
        }

        /// <summary>
        /// 用户在设置里切换主题。会立即生效，并通知调用方去持久化配置。
        /// </summary>
        public static void SetTheme(AppTheme theme)
        {
            if (_current == theme) return;
            Apply(theme, persist: true);
        }

        /// <summary>退出时调用，解绑系统事件（SystemEvents 会创建隐藏窗口，不解绑会拖住进程）。</summary>
        public static void Shutdown()
        {
            if (!_hooked) return;
            try
            {
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            }
            catch { }
            _hooked = false;
        }

        // ================= 核心 =================

        private static void Apply(AppTheme theme, bool persist)
        {
            _current = theme;

            // 解析出实际要用的亮/暗
            bool wantDark = theme == AppTheme.Dark ||
                            (theme == AppTheme.System && IsSystemDark());

            SwapColorsDictionary(wantDark);
            IsDarkEffective = wantDark;

            // 系统监听：只在 System 模式下需要（其它模式下系统变了也不该影响我们）
            UpdateSystemHook(theme == AppTheme.System);

            if (persist)
            {
                var cb = PreferenceChanged;
                if (cb != null) cb();
            }

            var tcb = ThemeChanged;
            if (tcb != null) tcb(wantDark);
        }

        /// <summary>
        /// 把 MergedDictionaries 里那个 Colors 字典的 Source 换成目标主题。
        /// 用**原位替换**保证它在 Brushes 之前（Brushes 依赖它的键）。
        /// 🚨🚨
        /// 只换 Colors 字典是**不够的**！实测（`tmp-links` 渲染宿主 + 像素采样）：
        ///   - 换完 Colors 后 `TryFindResource("AppBgColor")` 确实返回新值 #FF1A1D23 ✅
        ///   - 但 `TryFindResource("AppBgBrush").Color` **仍是旧的 #FFF5F6FA** ❌
        /// 原因：`Brushes.xaml` 里 `<SolidColorBrush Color="{DynamicResource AppBgColor}"/>`
        /// 的 DynamicResource 是在**该字典首次被解析时**解析一次的；兄弟字典被整体替换
        /// **不会**让它失效重算（WPF 的失效传播是按"同一个字典实例被改动"来的）。
        /// → **必须把 Brushes 字典也原位重挂一次**，它的 SolidColorBrush 才会照着
        ///   新 Colors 重新求值。（重挂后实测 AppBgBrush = #FF1A1D23 ✅）
        /// ⚠️ 顺序：先 Colors，再 Brushes —— 否则 Brushes 重建时取到的还是旧 Color。
        /// ⚠️ 同理，若以后在 Colors 之后还有别的"引用 Colors"的字典，也要一起重挂。
        /// </summary>
        private static void SwapColorsDictionary(bool dark)
        {
            var app = Application.Current;
            if (app == null) return;

            var md = app.Resources.MergedDictionaries;
            string want = dark ? DarkSource : LightSource;

            bool colorsSwapped = false;
            for (int i = 0; i < md.Count; i++)
            {
                var d = md[i];
                if (d == null || d.Source == null) continue;

                string src = d.Source.OriginalString;
                if (!IsColorsSource(src)) continue;

                // 已经是目标主题 → 无需替换
                // （⚠️ 必须比较"文件名"，不能比较整串 OriginalString：
                //   同一个文件可能写成不同的相对/绝对形式，比字符串会误判。）
                if (SourceMatches(src, want)) { colorsSwapped = true; break; }

                // 原位替换：new ResourceDictionary { Source = ... } 即可
                md[i] = new ResourceDictionary { Source = new Uri(want, UriKind.Relative) };
                colorsSwapped = true;
                break;
            }

            // 没找到（理论上不该发生）→ 补一个到最前面
            if (!colorsSwapped)
            {
                md.Insert(0, new ResourceDictionary { Source = new Uri(want, UriKind.Relative) });
            }

            // 🚨 关键第二步：把 Brushes 字典原位重挂，让 SolidColorBrush 的
            //    `Color="{DynamicResource XxxColor}"` 照新 Colors 重新求值。
            RemountBrushes();
        }

        /// <summary>
        /// 原位重挂 Brushes 字典（见 <see cref="SwapColorsDictionary"/> 里的实测说明）。
        /// 保持它在 MergedDictionaries 中的**位置不变**（必须在 Colors 之后、其余之前）。
        /// </summary>
        private static void RemountBrushes()
        {
            var app = Application.Current;
            if (app == null) return;

            var md = app.Resources.MergedDictionaries;
            for (int i = 0; i < md.Count; i++)
            {
                var d = md[i];
                if (d == null || d.Source == null) continue;
                string s = d.Source.OriginalString.Replace('\\', '/').ToLowerInvariant();
                if (!s.Contains("brushes.xaml")) continue;

                // 用同一个 Source 重建 = 重新解析一遍（这次会取到新 Colors 的值）
                var src = d.Source;
                md[i] = new ResourceDictionary { Source = src };
                return;
            }
        }

        private static bool IsColorsSource(string src)
        {
            if (string.IsNullOrEmpty(src)) return false;
            string s = src.Replace('\\', '/').ToLowerInvariant();
            return s.Contains("colors.light.xaml") || s.Contains("colors.dark.xaml")
                   || s.EndsWith("colors.xaml");
        }

        private static bool SourceMatches(string src, string want)
        {
            string a = src.Replace('\\', '/').ToLowerInvariant();
            string b = want.Replace('\\', '/').ToLowerInvariant();
            return a.EndsWith(b);
        }

        // ================= 系统主题 =================

        /// <summary>
        /// 读注册表判断系统是否为深色。Win10 1809+ / Win11 都有这个值。
        /// 读不到时**保守返回 false（亮色）** —— 这也是本程序原本的观感，
        /// 万一在某些精简系统上读不到，用户看到的至少不是个意外的黑界面。
        /// </summary>
        public static bool IsSystemDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k == null) return false;
                    object v = k.GetValue("AppsUseLightTheme");
                    if (v is int) return (int)v == 0;
                }
            }
            catch { }
            return false;
        }

        private static void UpdateSystemHook(bool needed)
        {
            if (needed && !_hooked)
            {
                try
                {
                    SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                    _hooked = true;
                }
                catch { }
            }
            else if (!needed && _hooked)
            {
                Shutdown();
            }
        }

        /// <summary>
        /// ⚠️ 这个事件在**独立线程**上触发（SystemEvents 有自己的消息循环），
        /// 必须切回 UI 线程再动资源字典，否则会抛 InvalidOperationException
        /// （"调用线程无法访问此对象，因为另一个线程拥有该对象"）。
        /// </summary>
        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // 只关心与配色有关的两类；其它偏好（鼠标、键盘…）直接忽略
            if (e.Category != UserPreferenceCategory.General &&
                e.Category != UserPreferenceCategory.Color &&
                e.Category != UserPreferenceCategory.VisualStyle)
                return;

            var app = Application.Current;
            if (app == null) return;

            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // 只在「跟随系统」模式下才真正换（用户显式选了亮/暗就别动）
                    if (_current != AppTheme.System) return;

                    bool dark = IsSystemDark();
                    if (dark == IsDarkEffective) return;   // 没实际变化

                    SwapColorsDictionary(dark);
                    IsDarkEffective = dark;

                    var tcb = ThemeChanged;
                    if (tcb != null) tcb(dark);
                }
                catch { }
            }));
        }

        /// <summary>
        /// 启动期自检：确认 Colors 字典存在且排在 Brushes 之前。
        /// 顺序错了（Brushes 先于 Colors）会让画刷取不到 Color →
        /// 控件回退到系统默认色，在深色模式下往往表现为刺眼的白块。
        /// </summary>
        private static void EnsureDictionaryShape()
        {
            var app = Application.Current;
            if (app == null) return;

            var md = app.Resources.MergedDictionaries;
            int colorsIdx = -1, brushesIdx = -1;
            for (int i = 0; i < md.Count; i++)
            {
                var d = md[i];
                if (d == null || d.Source == null) continue;
                string s = d.Source.OriginalString.Replace('\\', '/').ToLowerInvariant();
                if (IsColorsSource(s) && colorsIdx < 0) colorsIdx = i;
                else if (s.Contains("brushes.xaml") && brushesIdx < 0) brushesIdx = i;
            }

            // Brushes 排在 Colors 前面 / Colors 缺失 → 把 Colors 提到最前
            if (colorsIdx < 0)
            {
                md.Insert(0, new ResourceDictionary { Source = new Uri(LightSource, UriKind.Relative) });
            }
            else if (brushesIdx >= 0 && brushesIdx < colorsIdx)
            {
                var c = md[colorsIdx];
                md.RemoveAt(colorsIdx);
                md.Insert(0, c);
            }
        }
    }
}
