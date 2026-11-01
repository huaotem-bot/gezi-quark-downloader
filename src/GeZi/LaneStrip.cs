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
using System.Windows.Media;

namespace GeZi
{
    /// <summary>
    /// 「连接车道」—— 一排小格子，前 <see cref="Filled"/> 个点亮，用来一眼看出
    /// 「是服务端限速，还是客户端没把连接发出去」。
    ///
    /// <para>
    /// 🚨 为什么要有这个控件：原先是 XAML 里一个 <c>ItemsControl</c> 绑
    /// <c>ItemsSource="{Binding Lanes}"</c>，而 <c>Lanes</c> 是个每次读取都
    /// <c>new bool[20]</c> 的 getter —— 进度每 500ms 报一次，每个在跑的任务
    /// 就要把 20 个 <c>Border</c> 全部销毁重建。3 个并发就是每秒 120 个
    /// Border 的创建/销毁，2000 个任务时这是任务列表卡顿的主因之一。
    ///
    /// 现在改成**一个元素自绘 20 个矩形**：单行视觉元素从 33 个降到 14 个，
    /// 且这 20 个格子不再参与布局、不再有子元素、不再有数据绑定。
    /// 只有 <see cref="Filled"/> 真的变了才 <see cref="UIElement.InvalidateVisual"/>。
    /// </para>
    ///
    /// 外观刻意与原来的 XAML 保持像素级一致：格宽 3、高 10、间距 2、圆角 1.5。
    /// </summary>
    public sealed class LaneStrip : FrameworkElement
    {
        /// <summary>格子总数。默认 20（与 <c>TaskItem.LaneSlots</c> 一致）。</summary>
        public static readonly DependencyProperty SlotsProperty =
            DependencyProperty.Register("Slots", typeof(int), typeof(LaneStrip),
                new FrameworkPropertyMetadata(20,
                    FrameworkPropertyMetadataOptions.AffectsMeasure |
                    FrameworkPropertyMetadataOptions.AffectsRender,
                    OnSlotsChanged));

        /// <summary>点亮的格子数（0 ~ <see cref="Slots"/>）。</summary>
        public static readonly DependencyProperty FilledProperty =
            DependencyProperty.Register("Filled", typeof(int), typeof(LaneStrip),
                new FrameworkPropertyMetadata(0,
                    FrameworkPropertyMetadataOptions.AffectsRender,
                    OnFilledChanged));

        /// <summary>点亮格子的画刷（默认取主题里的次要文字色）。</summary>
        public static readonly DependencyProperty OnBrushProperty =
            DependencyProperty.Register("OnBrush", typeof(Brush), typeof(LaneStrip),
                new FrameworkPropertyMetadata(null,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>未点亮格子的画刷（默认取主题里的浅描边色）。</summary>
        public static readonly DependencyProperty OffBrushProperty =
            DependencyProperty.Register("OffBrush", typeof(Brush), typeof(LaneStrip),
                new FrameworkPropertyMetadata(null,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>格宽。与旧 XAML 的 <c>Width="3"</c> 一致。</summary>
        private const double CellWidth = 3.0;
        /// <summary>格高。与旧 XAML 的 <c>Height="10"</c> 一致。</summary>
        private const double CellHeight = 10.0;
        /// <summary>格间距。与旧 XAML 的 <c>Margin="0,0,2,0"</c> 一致。</summary>
        private const double CellGap = 2.0;
        /// <summary>圆角。与旧 XAML 的 <c>CornerRadius="1.5"</c> 一致。</summary>
        private const double CellRadius = 1.5;

        public int Slots
        {
            get { return (int)GetValue(SlotsProperty); }
            set { SetValue(SlotsProperty, value); }
        }

        public int Filled
        {
            get { return (int)GetValue(FilledProperty); }
            set { SetValue(FilledProperty, value); }
        }

        public Brush OnBrush
        {
            get { return (Brush)GetValue(OnBrushProperty); }
            set { SetValue(OnBrushProperty, value); }
        }

        public Brush OffBrush
        {
            get { return (Brush)GetValue(OffBrushProperty); }
            set { SetValue(OffBrushProperty, value); }
        }

        private static void OnFilledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            // AffectsRender 已经会让它重画；这里只在**真的变了**时补一次，
            // 避免绑定反复推同一个值造成的无谓失效。
            var strip = d as LaneStrip;
            if (strip == null) return;
            int oldV = e.OldValue is int ? (int)e.OldValue : 0;
            int newV = e.NewValue is int ? (int)e.NewValue : 0;
            if (oldV == newV) return;
            strip.InvalidateVisual();
        }

        private static void OnSlotsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var strip = d as LaneStrip;
            if (strip == null) return;
            strip.InvalidateMeasure();
            strip.InvalidateVisual();
        }

        /// <summary>期望宽度 = 格子数 × 格宽 + 间隔 × (格子数 - 1)。</summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            int slots = Slots;
            if (slots <= 0) return new Size(0, 0);
            double w = slots * CellWidth + (slots - 1) * CellGap;
            return new Size(w, CellHeight);
        }

        protected override void OnRender(DrawingContext dc)
        {
            int slots = Slots;
            if (slots <= 0) return;

            Brush on = OnBrush ?? (TryFindResource("TextSecondaryBrush") as Brush) ?? Brushes.Gray;
            Brush off = OffBrush ?? (TryFindResource("BorderBrushSoft") as Brush) ?? Brushes.LightGray;

            int filled = Filled;
            if (filled < 0) filled = 0;
            if (filled > slots) filled = slots;

            // 整条车道垂直居中（父容器给的高度可能比 10 大）
            double top = Math.Max(0, (ActualHeight - CellHeight) / 2.0);

            for (int i = 0; i < slots; i++)
            {
                double left = i * (CellWidth + CellGap);
                dc.DrawRoundedRectangle(i < filled ? on : off, null,
                    new Rect(left, top, CellWidth, CellHeight), CellRadius, CellRadius);
            }
        }
    }
}
