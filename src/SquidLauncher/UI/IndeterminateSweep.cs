using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SquidLauncher.UI
{
    /// <summary>
    /// 不确定态进度条（IsIndeterminate）里那个来回扫的滑块。
    ///
    /// 模板里写死的位移是绝对像素，进度条一变宽 / 变窄，滑块就会跑到条外去。
    /// 这里按进度条的实际宽度换算，滑块永远只在条内扫过（外加模板上的 ClipToBounds 兜底）。
    /// </summary>
    public static class IndeterminateSweep
    {
        /// <summary>滑块宽度占进度条宽度的比例</summary>
        const double BandRatio = 0.32;
        const double MinBandWidth = 28;
        static readonly Duration SweepDuration = new Duration(TimeSpan.FromSeconds(1.4));

        sealed class State
        {
            public FrameworkElement Band;
            public TranslateTransform Transform;
        }

        static readonly ConditionalWeakTable<ProgressBar, State> States = new ConditionalWeakTable<ProgressBar, State>();

        public static readonly DependencyProperty EnabledProperty =
            DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(IndeterminateSweep),
                new PropertyMetadata(false, OnEnabledChanged));

        public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
        public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

        static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is ProgressBar bar)) return;
            bar.Loaded -= Bar_Loaded;
            bar.SizeChanged -= Bar_SizeChanged;
            if (!(bool)e.NewValue)
            {
                if (States.TryGetValue(bar, out var off)) Stop(off);
                return;
            }
            bar.Loaded += Bar_Loaded;
            bar.SizeChanged += Bar_SizeChanged;
            if (bar.IsLoaded) Bar_Loaded(bar, null);
        }

        static void Bar_Loaded(object sender, RoutedEventArgs e)
        {
            var bar = (ProgressBar)sender;
            if (!(bar.Template?.FindName("Indeterminate", bar) is FrameworkElement band)) return;
            var state = States.GetOrCreateValue(bar);
            if (state.Band != null)
            {
                if (state.Band.IsVisible) Restart(bar, state);
                return;
            }
            state.Band = band;
            state.Transform = new TranslateTransform();
            band.RenderTransform = state.Transform;
            ApplyClip(bar);
            // 滑块显隐由模板触发器跟随 IsIndeterminate；跟着它开关动画，非不确定态就不空转
            band.IsVisibleChanged += (s, args) => Sync(bar, state);
            Sync(bar, state);
        }

        static void Bar_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var bar = (ProgressBar)sender;
            ApplyClip(bar);
            if (States.TryGetValue(bar, out var state) && state.Band != null && state.Band.IsVisible) Restart(bar, state);
        }

        /// <summary>
        /// 把模板根节点从矩形裁剪换成与轨道圆角一致的圆角裁剪。
        /// 模板上的 ClipToBounds 是矩形：滑块扫到两端、半截露在条外时，露出的那半截会被
        /// 直角切掉，正好盖住轨道本身的圆角，看上去就是“圆角变方角”。
        /// </summary>
        static void ApplyClip(ProgressBar bar)
        {
            if (!(bar.Template?.FindName("TemplateRoot", bar) is FrameworkElement host)) return;
            if (!(bar.Template?.FindName("PART_Track", bar) is Border track)) return;
            double w = host.ActualWidth, h = host.ActualHeight;
            if (w <= 0 || h <= 0) return;
            double r = Math.Min(track.CornerRadius.TopLeft, Math.Min(w, h) / 2);
            host.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        }

        static void Sync(ProgressBar bar, State state)
        {
            if (state.Band.IsVisible) Restart(bar, state);
            else Stop(state);
        }

        static void Stop(State state)
        {
            state.Transform?.BeginAnimation(TranslateTransform.XProperty, null);
        }

        static void Restart(ProgressBar bar, State state)
        {
            double width = bar.ActualWidth;
            if (width <= 0 || state.Transform == null) return;
            double bandWidth = Math.Max(MinBandWidth, width * BandRatio);
            state.Band.Width = bandWidth;
            state.Transform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-bandWidth, width, SweepDuration) { RepeatBehavior = RepeatBehavior.Forever });
        }
    }
}
