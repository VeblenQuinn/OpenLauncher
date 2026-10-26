using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SquidLauncher.Core;

namespace SquidLauncher.UI
{
    /// <summary>
    /// 启动闪屏：主窗口还没画出来之前，先把这张窗口亮出来播加载动画。
    ///
    /// 它取代的是 0.1.42 那种「界面没就绪就完全不显示任何窗口」的做法 —— 那样确实不闪白，
    /// 但用户面对的是几百毫秒到一秒多的纯桌面，不知道程序有没有在启动。现在这段时间里
    /// 屏幕上有个会动的方块乌贼。
    ///
    /// 配色不需要再传：窗口整体透明（AllowsTransparency），屏幕上只有 GIF 本身。
    /// 这也顺带绕开了跨线程问题 —— Application.Resources 归主窗口线程所有，本窗口读不得。
    /// </summary>
    public partial class SplashWindow : Window
    {
        const string GifUri = "pack://application:,,,/Assets/loading.gif";

        /// <summary>最短显示时长：程序太快就绪时也不至于一闪而过</summary>
        static readonly TimeSpan MinVisible = TimeSpan.FromMilliseconds(500);

        readonly Stopwatch _shown = Stopwatch.StartNew();
        GifPlayer _player;
        bool _dismissing;

        public SplashWindow()
        {
            InitializeComponent();

            SourceInitialized += (s, e) =>
            {
                try
                {
                    // 不调 Theme.PaintWindowBack：那会把窗口类背景刷改成主题底色，
                    // 而对分层窗口（AllowsTransparency）而言底色必须完全透明，否则又会变回不透明方块。
                    var h = new WindowInteropHelper(this).Handle;
                    HideFromAltTab(h);
                }
                catch (Exception ex) { Log.Warn("闪屏窗口初始化失败：" + ex.Message); }
            };

            Loaded += (s, e) =>
            {
                try { GifPlayer.Play(Anim, GifUri, p => _player = p); }
                catch (Exception ex) { Log.Warn("加载动画启动失败：" + ex.Message); }
            };
        }

        /// <summary>主窗口首帧已呈现：淡出并关闭本窗口（不早于最短显示时长）。</summary>
        public void Dismiss()
        {
            if (_dismissing) return;
            _dismissing = true;

            var wait = MinVisible - _shown.Elapsed;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;

#if DEBUG
            // 诊断：OL_SPLASH_HOLD_MS=4000 让闪屏多停一会儿，方便截图确认动画真的在动
            if (int.TryParse(Environment.GetEnvironmentVariable("OL_SPLASH_HOLD_MS"), out var hold) && hold > 0)
                wait = TimeSpan.FromMilliseconds(hold);
#endif

            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = wait };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                FadeOut();
            };
            timer.Start();
        }

        void FadeOut()
        {
            try
            {
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                };
                fade.Completed += (s, e) => Close();
                BeginAnimation(OpacityProperty, fade);
            }
            catch
            {
                Close();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _player?.Dispose(); } catch { }
            _player = null;
            base.OnClosed(e);
            // 本窗口跑在独立线程自建的 Dispatcher 上（见 UI/Splash.cs），关掉后要主动结束它
            try { Dispatcher.InvokeShutdown(); } catch { }
        }

        // ---------- 不出现在 Alt+Tab ----------

        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        static void HideFromAltTab(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0) return;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
        }
    }
}
