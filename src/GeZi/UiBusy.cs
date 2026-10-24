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
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GeZi
{
    /// <summary>
    /// 按钮「加载中」态的公共实现。
    /// 用户反馈：「他们需要加载时间或者是会切换到另一个东西，
    /// 每次点击它们的时候都要有一个加载过程，主要是为了让用户看到他在加载」。
    /// 本质是消除"点击 → 界面响应"之间那段静默期 —— 没有反馈时，用户无法区分
    /// 「程序在忙」和「程序没反应」，于是会重复点击（取直链连点 = 重复发网络请求、
    /// 导出连点 = 桌面多出几个 txt）。有反馈时，同样的耗时就变成"它在干活，我等一下"。
    /// 放在这里而不是塞进 MainWindow：验证宿主（tmp-links）要链入同一份源码去
    /// 离屏渲染加载态，才能逐像素确认"转圈真的画出来了、按钮宽度没有塌"。
    /// </summary>
    internal static class UiBusy
    {
        /// <summary>
        /// 加载态的最短显示时长（毫秒）。
        /// </summary>
        /// <remarks>
        /// 操作本身只要几十毫秒时，加载态会**一闪而过** —— 看起来像界面抽搐，比不显示更差。
        /// 所以统一给一个下限：快操作也稳定显示 280ms（低于"感觉卡顿"的阈值，
        /// 不会让人觉得软件变慢），慢操作则按真实耗时显示。
        /// </remarks>
        public const int MinBusyMs = 280;

        /// <summary>造一个"小转圈"（缺口圆弧 + 无限旋转）。Stroke 由调用方设置。</summary>
        public static System.Windows.Shapes.Path MakeSpinner()
        {
            var arc = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 12,4 A 8,8 0 1 1 4,12"),
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 12,
                Height = 12,
                Stretch = Stretch.Uniform,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ((RotateTransform)arc.RenderTransform).BeginAnimation(
                RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(900)))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                });
            return arc;
        }

        /// <summary>
        /// 构造「小转圈 + 短文案」的按钮内容。busyText 传 null / 空则只显示转圈。
        /// </summary>
        /// <param name="owner">
        /// 转圈的 Stroke 会**绑定到该按钮的 Foreground** —— 这样主按钮（白字）
        /// 与次按钮（深字）都能自动取到对的颜色，不必为每种按钮各写一套。
        /// </param>
        public static UIElement BuildContent(string busyText, Button owner)
        {
            var arc = MakeSpinner();
            if (owner != null)
                arc.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
                    new Binding("Foreground") { Source = owner });

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(arc);
            if (!string.IsNullOrEmpty(busyText))
            {
                sp.Children.Add(new TextBlock
                {
                    Text = busyText,
                    Margin = new Thickness(7, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            return sp;
        }
    }
}
