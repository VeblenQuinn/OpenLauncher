using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using OpenLauncher.Accounts;
using OpenLauncher.Core;
using OpenLauncher.UI;
using OpenLauncher.Update;

namespace OpenLauncher
{
    public partial class App : Application
    {
        static App()
        {
            // 与 App.config 相同的长路径开关；写在代码里，单独拷走 exe 也生效
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnUiException;
            AppDomain.CurrentDomain.UnhandledException += (s, ex) => Log.Error("未处理的异常", ex.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, ex) => { Log.Warn("未观察的任务异常：" + ex.Exception?.GetBaseException().Message); ex.SetObserved(); };

            TaskManager.Dispatcher = Dispatcher;
            SmoothScroll.Init();

            // 显卡驱动有问题 / 虚拟机里，WPF 的硬件加速初始化会拖住启动好几百毫秒。
            // 设 OL_SOFTWARE_RENDER=1 可以强制走软件渲染（画面略糙，但启动不会再等 GPU）。
            if (Environment.GetEnvironmentVariable("OL_SOFTWARE_RENDER") == "1")
            {
                try { System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly; }
                catch { }
            }

            Log.Info($"{Branding.AppName} {AppInfo.VersionString} 启动，系统 {Util.OsVersion} {Util.OsArch}，数据目录 {Paths.DataDir}");

            // 启动耗时埋点：慢在哪一步直接看日志，不用猜
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long last = watch.ElapsedMilliseconds;
            void Mark(string what)
            {
                var t = watch.ElapsedMilliseconds;
                Log.Info($"[启动] {what} +{t - last}ms（累计 {t}ms）");
                last = t;
            }
            StartupMark = Mark;

            try
            {
                var started = System.Diagnostics.Process.GetCurrentProcess().StartTime;
                var pre = (DateTime.Now - started).TotalMilliseconds;
                if (pre > 0) Log.Info($"[启动] 运行时与资源加载 +{pre:0}ms，其余从此时开始计");
                Log.Info($"[启动] 渲染层级 {System.Windows.Media.RenderCapability.Tier >> 16}（0=软件渲染），进程模式 {System.Windows.Media.RenderOptions.ProcessRenderMode}");
            }
            catch { }

            // 清理上次更新留下的旧文件：杀软/占用情况下会重试到 1.5 秒，绝不能挡住界面
            System.Threading.Tasks.Task.Run(() =>
            {
                try { Updater.CleanupOld(); } catch (Exception ex) { Log.Warn("清理旧文件失败：" + ex.Message); }
            });

            Settings.Load();
            Mark("读取设置");
            try { AccountStore.Load(); } catch (Exception ex) { Log.Error("读取账号失败", ex); }
            Mark("读取账号");

            // 种子地图引擎打包在 exe 里，趁现在把它解到工具目录。
            // 必须早于任何 CubiomesEngine 的访问：那个类一旦被碰到就会 LoadLibrary 并锁住
            // 文件，之后想覆盖旧引擎就只能等下次重启了（Windows 不允许改写已映射的 DLL）。
            try { SeedMap.EngineProvisioner.TryInstallBundled(); }
            catch (Exception ex) { Log.Warn("解出内置种子地图引擎失败：" + ex.Message); }
            Mark("校验内置引擎");

            Theme.Apply(Settings.ThemeColor);
            Mark("应用主题");

#if DEBUG
            // 诊断：OL_BARE_WINDOW=1 用一个最简窗口替代主窗口，用来分辨「启动慢是 WPF 冷启动」
            // 还是「我们的界面太重」（对比日志里的累计耗时即可）
            if (Environment.GetEnvironmentVariable("OL_BARE_WINDOW") == "1")
            {
                var w = new Window { Width = 900, Height = 600, Content = new System.Windows.Controls.TextBlock { Text = "bare" } };
                MainWindow = w;
                w.Show();
                Mark("创建并显示主窗口（最简）");
                w.ContentRendered += (s, a) => { Mark("首帧呈现（最简）"); StartupMark = null; watch.Stop(); };
                return;
            }
#endif

            var win = new MainWindow();
            MainWindow = win;
            win.Show();
            Mark("创建并显示主窗口");

            // 首帧之后才让窗口真正显形（构造时 Opacity=0，避免见到内容画出来之前的白底）
            win.ContentRendered += (s, a) =>
            {
                Mark("首帧呈现");
                StartupMark = null;
                watch.Stop();
                win.RevealAfterFirstFrame();
                win.RunDeferredStartup();
            };

            // 兜底：ContentRendered 万一没派发（某些远程桌面 / 显卡环境），1.5 秒后强制显示
            var revealTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
            revealTimer.Tick += (s, a) =>
            {
                revealTimer.Stop();
                if (win.Opacity < 1) win.RevealAfterFirstFrame();
            };
            revealTimer.Start();
        }

        /// <summary>启动耗时埋点；主窗口内部用它继续记（首帧后置空）</summary>
        internal static Action<string> StartupMark;

        protected override void OnExit(ExitEventArgs e)
        {
            Settings.SaveNow();
            base.OnExit(e);
        }

        void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("界面异常", e.Exception);
            e.Handled = true;
            try { Dialogs.Alert("出错了", e.Exception.GetBaseException().Message + "\n\n详细信息已写入启动器日志。"); }
            catch { MessageBox.Show(e.Exception.ToString(), Branding.AppName); }
        }
    }
}
