using System;
using System.Threading;
using System.Windows.Threading;
using SquidLauncher.Core;

namespace SquidLauncher.UI
{
    /// <summary>
    /// 启动闪屏的宿主：把闪屏窗口放到**独立 UI 线程**上跑。
    ///
    /// 为什么必须分线程：主窗口从构造到画出首帧要一秒多（WPF 冷启动 + 骨架 + 首页），
    /// 这段时间主线程完全被占住。闪屏如果挂在同一个线程上，它的动画、它的淡出、甚至
    /// 它的重绘全都停摆 —— 实测日志里 GIF 直到主窗口建完（+1.7s）才「就绪」，
    /// 再 41ms 就被关掉，等于压根没播。
    ///
    /// 分线程后两边互不干扰：主线程忙它的，闪屏线程自己 60Hz 跑 <see cref="GifPlayer"/>。
    /// 代价是闪屏不能引用 Application.Resources（跨线程），所以配色由调用方传进来。
    /// </summary>
    public static class Splash
    {
        static Thread _thread;
        static SplashWindow _win;
        static readonly ManualResetEventSlim Ready = new ManualResetEventSlim(false);
        static volatile bool _dismissRequested;

        /// <summary>正在显示或已请求显示</summary>
        public static bool Active => _thread != null;

        /// <summary>
        /// 建窗口并等它真正显示出来再返回（主线程最多等 3 秒）。
        /// 必须等：不然主窗口可能抢在闪屏前面出来，顺序就反了。
        /// </summary>
        public static void Show()
        {
            if (_thread != null) return;

            var t = new Thread(() =>
            {
                try
                {
                    _win = new SplashWindow();
                    _win.Closed += (s, e) => { _win = null; };
                    _win.Show();
                }
                catch (Exception ex)
                {
                    Log.Warn("创建加载动画窗口失败：" + ex.Message);
                    _win = null;
                    Ready.Set();
                    return;
                }

                Ready.Set();
                Dispatcher.Run();   // 闪屏线程自己的消息循环：动画、淡出全靠它
            })
            {
                IsBackground = true,
                Name = "Splash",
            };
            t.SetApartmentState(ApartmentState.STA);

            _thread = t;
            t.Start();
            Ready.Wait(TimeSpan.FromSeconds(3));
        }

        /// <summary>主窗口首帧已呈现：让闪屏线程自己淡出关闭（不阻塞主线程）</summary>
        public static void Dismiss()
        {
            if (_dismissRequested) return;
            _dismissRequested = true;

            var w = _win;
            if (w == null) return;

            try
            {
                w.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { w.Dismiss(); }
                    catch (Exception ex) { Log.Warn("关闭加载动画失败：" + ex.Message); }
                }));
            }
            catch (Exception ex) { Log.Warn("通知加载动画关闭失败：" + ex.Message); }
        }
    }
}
