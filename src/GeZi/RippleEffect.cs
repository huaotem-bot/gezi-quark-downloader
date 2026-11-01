using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
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
    /// 侧栏导航波纹效果（Material 手感）：鼠标按下时从按下点扩散一圈渐隐波纹。
    /// 用法（两处配套，缺一不可）：
    ///   1. ListBox 上挂 attached property：
    ///      &lt;ListBox local:RippleEffect.IsEnabled="True" …&gt;
    ///   2. ItemContainerStyle 的 ControlTemplate 里放一个波纹画布：
    ///      &lt;Canvas x:Name="RippleHost" ClipToBounds="True" IsHitTestVisible="False"/&gt;
    ///      位置：选中/悬停底色层之上、内容（图标/文字）之下。
    /// 波纹颜色取主题令牌 RippleBrush（浅色=深色波纹 / 深色=浅色波纹，见 Colors.*.xaml），
    /// 随主题切换自动生效；找不到资源时静默跳过，不会崩。
    ///   Preview 在 ListBox 内部选中逻辑之前触发，保证波纹和选中变化同帧启动，
    ///   视觉上"按下即有反馈"。
    ///   保持 XAML 声明式挂载（一处属性即用），且不依赖具体窗口类型，将来别的
    ///   列表（右键菜单等）想加波纹直接挂同一个属性。
    /// </summary>
    public static class RippleEffect
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled", typeof(bool), typeof(RippleEffect),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject obj)
        {
            return (bool)obj.GetValue(IsEnabledProperty);
        }

        public static void SetIsEnabled(DependencyObject obj, bool value)
        {
            obj.SetValue(IsEnabledProperty, value);
        }

        /// <summary>扩散时长（ms）。450~600 是 Material 规范区间，550 手感最稳。</summary>
        private const int RippleMs = 550;
        /// <summary>波纹峰值不透明度（刷子自带 alpha ≈ 12%，总视觉 ≈ 11%，与 Material 一致）。</summary>
        private const double RipplePeak = 0.9;
        /// <summary>半径外扩系数：覆盖到最远角后再放大 15%，边缘柔化不留直边。</summary>
        private const double RadiusGrow = 1.15;

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var list = d as ListBox;
            if (list == null) return;
            list.PreviewMouseLeftButtonDown -= OnPreviewMouseDown;
            if (e.NewValue is bool on && on)
                list.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        }

        private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (item == null || !item.IsEnabled) return;

            var host = item.Template == null
                ? null
                : item.Template.FindName("RippleHost", item) as Canvas;
            if (host == null) return;
            if (host.ActualWidth < 2 || host.ActualHeight < 2) return;

            var brush = item.TryFindResource("RippleBrush") as Brush;
            if (brush == null) return;

            SpawnRipple(host, e.GetPosition(host), brush, CornerRadiusOf(item));
        }

        private static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T hit) return hit;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        /// <summary>生成一圈波纹：椭圆以按下点为圆心，0→满径扩散同时渐隐，播完自删。</summary>
        /// <param name="radius">底板圆角（从模板 Bd 读，保证波纹被裁成圆角而不是直角方框）。</param>
        private static void SpawnRipple(Canvas host, Point origin, Brush fill, double radius)
        {
            double w = host.ActualWidth, h = host.ActualHeight;

            // 半径 = 按下点到四个角的最远距离（波纹要铺满整项，不能截在中间），再外扩。
            double r = Math.Max(
                Math.Max(Len(origin.X, origin.Y), Len(w - origin.X, origin.Y)),
                Math.Max(Len(origin.X, h - origin.Y), Len(w - origin.X, h - origin.Y)));
            r = Math.Max(r, Math.Max(w, h) * 0.6) * RadiusGrow;

            // 波纹画布置圆角裁剪：不依赖 Border 是否裁剪子级（各 WPF 版本行为不一），
            // 直接从模板取圆角显式裁 —— 每次都重建，窗口尺寸变化后依然准。
            if (radius > 0)
                host.Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);

            var ripple = new Ellipse
            {
                Width = r * 2,
                Height = r * 2,
                Fill = fill,
                Opacity = 0,
                IsHitTestVisible = false,
                RenderTransform = new ScaleTransform(0, 0),
            };
            Canvas.SetLeft(ripple, origin.X - r);
            Canvas.SetTop(ripple, origin.Y - r);
            host.Children.Add(ripple);

            // 扩散：0→1，先快后慢（CubicEase EaseOut）—— Material 的核心手感。
            var expand = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(RippleMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            // 渐隐用 EaseIn：先稳住再淡出（直线淡出会有"中途就没了"的错觉）。
            // t=0 时缩放为 0 什么也看不见，所以不设 From 直接从峰值淡出即可。
            var fade = new DoubleAnimation(RipplePeak, 0, TimeSpan.FromMilliseconds(RippleMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };

            var scale = (ScaleTransform)ripple.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, expand);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, expand);
            ripple.BeginAnimation(UIElement.OpacityProperty, fade);

            fade.Completed += delegate
            {
                host.Children.Remove(ripple);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            };
        }

        /// <summary>取模板里底板 Border（x:Name="Bd"）的圆角；取不到返回 0（不裁）。</summary>
        private static double CornerRadiusOf(ListBoxItem item)
        {
            var bd = item.Template.FindName("Bd", item) as Border;
            return bd == null ? 0 : bd.CornerRadius.TopLeft;
        }

        private static double Len(double dx, double dy)
        {
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
