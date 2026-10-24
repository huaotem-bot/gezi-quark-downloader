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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

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
        /// 动画进行中的截止时刻（UTC）。此期间 <see cref="OnContainerSizeChanged"/> 直接忽略，
        /// 不做"无动画吸附"。
        /// 滑块自己的 Width 动画会让容器（auto 宽）跟着变 → 触发 SizeChanged →
        /// 旧代码在这里 MoveThumb(animate:false) → **第一帧就把动画打断、直接落到终值**，
        /// 用户看到的就是"选中态瞬间跳过去，没有滑动"。
        /// 实测复现（tmp-links/SepSlideProbe）：点击后 thumb x/Width 一步到位，无中间帧。
        /// 同理，分段控件旁边 UI 显隐（获取直链对话框的"正在切换到…"提示行）也会
        /// 改变容器高度 → 同样踩这个坑。
        /// </summary>
        private static DateTime _animUntil = DateTime.MinValue;

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
            //    在这里重置会把 200ms 滑动在第 1 帧打成瞬移（见 _animUntil 注释）。
            if (DateTime.UtcNow < _animUntil) return;
            MoveThumb(sender as FrameworkElement, animate: false);
        }

        private static void OnAnyChecked(object sender, RoutedEventArgs e)
        {
            var panel = sender as FrameworkElement;
            // 布局未完成（初始 IsChecked）时 ActualWidth=0，交给 Loaded 兜底
            if (panel == null || !panel.IsLoaded) return;
            // 宽限 80ms：真正挡住的是动画头几帧内的 SizeChanged，不是整段动画
            _animUntil = DateTime.UtcNow.AddMilliseconds(SlideMs + 80);
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
