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
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    /// <summary>
    /// 分段控件的「滑动指示器（滑块）」：选中项切换时，一个高亮滑块平滑滑到目标位置，
    /// 而不是每个选项各自"亮灭"（Material 分段按钮的标准形态）。
    /// 用法（XAML）：
    ///   &lt;Grid local:SegmentedSlider.IsEnabled="True"
    ///         local:SegmentedSlider.Thumb="{x:Reference ModeThumb}"&gt;
    ///       &lt;Border x:Name="ModeThumb" CornerRadius="6" HorizontalAlignment="Left"
    ///               VerticalAlignment="Stretch" Background="{DynamicResource SurfaceBrush}"/&gt;
    ///       &lt;StackPanel Orientation="Horizontal"&gt;…RadioButton…&lt;/StackPanel&gt;
    ///   &lt;/Grid&gt;
    /// 配套样式约定（见 Styles.xaml）：
    ///   · 滑块是容器的**第一个子元素**（位于最底层，选项文字画在它上面）；
    ///   · 选项 RadioButton 的选中态**不再自绘**白底/下划线（由滑块表达），只切字色/字重；
    ///   · 滑块 HorizontalAlignment=Left + RenderTransform(TranslateTransform) 定位，
    ///     Width 动画同步到目标选项宽度。
    /// 滑块的目标 X / Width 取决于选项的**实测宽度**
    /// （内容自适应，改字号/文案都会变），XAML 触发器写死数值必然走样；用附加行为
    /// 每次布局后现算，一处逻辑服务全部分段控件。
    /// Loaded / 容器尺寸变化 → 无动画直接吸附（避免开窗时滑块"飞"过来）；
    /// 运行中 RadioButton.Checked → CubicEase 200ms 滑动。初始 IsChecked 在布局前
    /// 就会触发 Checked，此时 ActualWidth=0，跳过即可，由 Loaded 兜底。
    /// </summary>
    public static class SegmentedSlider
    {
        /// <summary>滑块元素（容器内第一个子元素，通常是 Border）。</summary>
        public static readonly DependencyProperty ThumbProperty =
            DependencyProperty.RegisterAttached(
                "Thumb", typeof(FrameworkElement), typeof(SegmentedSlider),
                new PropertyMetadata(null));

        public static FrameworkElement GetThumb(DependencyObject obj)
        {
            return (FrameworkElement)obj.GetValue(ThumbProperty);
        }

        public static void SetThumb(DependencyObject obj, FrameworkElement value)
        {
            obj.SetValue(ThumbProperty, value);
        }

        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled", typeof(bool), typeof(SegmentedSlider),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject obj)
        {
            return (bool)obj.GetValue(IsEnabledProperty);
        }

        public static void SetIsEnabled(DependencyObject obj, bool value)
        {
            obj.SetValue(IsEnabledProperty, value);
        }

        /// <summary>滑动时长（ms）。200ms + EaseOut：跟手又不拖沓（Material 分段按钮同数量级）。</summary>
        private const int SlideMs = 200;

        /// <summary>
        /// 每个分段控件各自的「动画截止时刻」。
        /// <para>
        /// 🚨 原来是一个 <c>private static DateTime _animUntil</c> —— 全程序五个分段控件
        /// （模式 / 网盘页签 / 任务筛选 / 日志档位 / 主题）**共用同一个截止时刻**。
        /// 后果：点了 A 控件之后 280ms 内，B 控件只要发生一次容器尺寸变化
        /// （窗口缩放、字号变化、旁边提示行显隐），就会因为「动画中」而跳过吸附，
        /// B 的滑块停在错位置，直到下次点击才纠正。
        /// </para>
        /// <para>
        /// 用 <see cref="ConditionalWeakTable{TKey,TValue}"/> 而不是 Dictionary：
        /// 键是控件，控件被回收时条目自动消失，不会像 Dictionary 那样把整棵可视树钉住。
        /// </para>
        /// </summary>
        private sealed class AnimState { public DateTime Until = DateTime.MinValue; }

        private static readonly ConditionalWeakTable<FrameworkElement, AnimState> _animStates =
            new ConditionalWeakTable<FrameworkElement, AnimState>();

        private static AnimState StateOf(FrameworkElement panel)
        {
            return panel == null ? new AnimState() : _animStates.GetOrCreateValue(panel);
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var panel = d as FrameworkElement;
            if (panel == null) return;

            panel.Loaded -= OnLoaded;
            panel.SizeChanged -= OnContainerSizeChanged;
            panel.RemoveHandler(RadioButton.CheckedEvent, new RoutedEventHandler(OnAnyChecked));

            if (e.NewValue is bool on && on)
            {
                panel.Loaded += OnLoaded;
                panel.SizeChanged += OnContainerSizeChanged;
                // 隧道/冒泡都无所谓：用 AddHandler 接住后代 RadioButton 的 Checked
                panel.AddHandler(RadioButton.CheckedEvent, new RoutedEventHandler(OnAnyChecked));
            }
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            MoveThumb(sender as FrameworkElement, animate: false);
        }

        private static void OnContainerSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 容器变宽/变窄（窗口缩放、字号变化）后重新吸附，不用动画。
            // ⚠️ 但动画进行中必须跳过：滑块 Width 动画本身就会改变容器尺寸，
            //    在这里重置会把 200ms 滑动在第 1 帧打成瞬移（见 _animStates 注释）。
            var panel = sender as FrameworkElement;
            if (DateTime.UtcNow < StateOf(panel).Until) return;
            MoveThumb(panel, animate: false);
        }

        private static void OnAnyChecked(object sender, RoutedEventArgs e)
        {
            var panel = sender as FrameworkElement;
            // 布局未完成（初始 IsChecked）时 ActualWidth=0，交给 Loaded 兜底
            if (panel == null || !panel.IsLoaded) return;
            // 宽限 80ms：真正挡住的是动画头几帧内的 SizeChanged，不是整段动画
            StateOf(panel).Until = DateTime.UtcNow.AddMilliseconds(SlideMs + 80);
            MoveThumb(panel, animate: true);
        }

        /// <summary>把滑块移到当前选中项的位置/宽度。</summary>
        private static void MoveThumb(FrameworkElement panel, bool animate)
        {
            if (panel == null) return;
            var thumb = GetThumb(panel);
            if (thumb == null) return;

            var target = FindChecked(panel);
            if (target == null) return;
            if (target.ActualWidth < 1 || target.ActualHeight < 1) return;

            // 目标位置 = 选中项左上角相对滑块父容器的偏移
            var parent = thumb.Parent as FrameworkElement ?? panel;

            // 🚨 2026-10-26 最终结论（用户二次反馈 + 截图对照）：
            //   「滑块宽度 = 按钮整体宽度」。
            //   中间那版改成按**文字宽度**算（贴合文字），结果相邻按钮的滑块之间留出缝隙，
            //   四个网盘页签切换时中间会露出一截空白 —— 用户原话「那四个网盘不是一个整体，
            //   切换滑块的时候中间会有断的部分」。
            //   正解：滑块覆盖整个按钮，相邻按钮的滑块恰好**首尾相接**，
            //   视觉上就是一条连续的线在滑动，整体感才成立；
            //   「贴合文字」的观感由按钮自身的左右 Padding（10,4 / 18,7）保证，
            //   不需要再单独缩窄滑块。
            Point at = target.TranslatePoint(new Point(0, 0), parent);
            double x = at.X;
            double w = target.ActualWidth;

            if (!animate)
            {
                // 先解绑可能残留的 HoldEnd 动画，否则写不进去
                thumb.BeginAnimation(FrameworkElement.WidthProperty, null);
                var tt0 = thumb.RenderTransform as TranslateTransform;
                if (tt0 != null) tt0.BeginAnimation(TranslateTransform.XProperty, null);
                thumb.Width = w;
                thumb.RenderTransform = new TranslateTransform(x, 0);
                return;
            }

            var tt = thumb.RenderTransform as TranslateTransform;
            if (tt == null)
            {
                tt = new TranslateTransform(x, 0);
                thumb.RenderTransform = tt;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(SlideMs);
            // 不设 From：从当前值起播，连续快速切换也平滑
            tt.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(x, dur) { EasingFunction = ease });
            thumb.BeginAnimation(FrameworkElement.WidthProperty,
                new DoubleAnimation(w, dur) { EasingFunction = ease });
        }

        /// <summary>在后代里找第一个处于选中态的 RadioButton。</summary>
        private static RadioButton FindChecked(DependencyObject root)
        {
            if (root == null) return null;
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                int n = VisualTreeHelper.GetChildrenCount(cur);
                for (int i = 0; i < n; i++)
                {
                    var child = VisualTreeHelper.GetChild(cur, i);
                    var rb = child as RadioButton;
                    if (rb != null && rb.IsChecked == true) return rb;
                    queue.Enqueue(child);
                }
            }
            return null;
        }
    }
}
