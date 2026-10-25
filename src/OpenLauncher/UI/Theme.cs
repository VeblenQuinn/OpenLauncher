using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenLauncher.Core;

namespace OpenLauncher.UI
{
    /// <summary>界面明暗模式</summary>
    public enum ThemeMode { Light, Dark, System }

    public static class Theme
    {
        public static readonly (string name, string hex)[] Presets =
        {
            ("天空蓝", "#2F7BF5"), ("草方块绿", "#22A06B"), ("末影紫", "#7C5CFA"), ("岩浆橙", "#F08A24"),
            ("樱花粉", "#E5568B"), ("海晶青", "#0EA5B7"), ("红石红", "#D9483B"), ("黑曜石", "#3C4454"),
        };

        /// <summary>当前设置的模式（System = 跟随系统）</summary>
        public static ThemeMode Mode { get; private set; } = ThemeMode.System;
        /// <summary>实际生效的是不是深色</summary>
        public static bool IsDark { get; private set; }

        /// <summary>明暗真正发生变化后触发（UI 线程）</summary>
        public static event Action Changed;

        /// <summary>当前主题的窗口背景色（给原生窗口类画刷用）</summary>
        public static Color Background { get; private set; } = Color.FromRgb(0xEE, 0xF2, 0xF8);

        public static Color Parse(string hex, Color fallback)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
        }

        public static ThemeMode ParseMode(string value) => (value ?? "").Trim().ToLowerInvariant() switch
        {
            "dark" => ThemeMode.Dark,
            "light" => ThemeMode.Light,
            _ => ThemeMode.System,
        };

        public static string ModeKey(ThemeMode m) => m switch
        {
            ThemeMode.Dark => "dark",
            ThemeMode.Light => "light",
            _ => "system",
        };

        public static string ModeName(ThemeMode m) => m switch
        {
            ThemeMode.Dark => "深色",
            ThemeMode.Light => "浅色",
            _ => "跟随系统",
        };

        /// <summary>Windows 的"应用使用浅色主题"开关；读不到时按浅色处理</summary>
        public static bool SystemPrefersDark()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k?.GetValue("AppsUseLightTheme") is int light) return light == 0;
                }
            }
            catch { }
            return false;
        }

        /// <summary>按设置里保存的模式应用（启动时调用）</summary>
        public static void Apply(string hex) => Apply(hex, ParseMode(Settings.ThemeMode));

        public static void Apply(string hex, ThemeMode mode)
        {
            var app = Application.Current;
            if (app == null) return;

            Mode = mode;
            bool dark = mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemPrefersDark());
            bool changed = dark != IsDark;
            IsDark = dark;

            ApplyPalette(app.Resources, dark);
            ApplyAccent(app.Resources, Parse(hex, Color.FromRgb(0x2F, 0x7B, 0xF5)), dark);
            WatchSystem();

            if (changed)
            {
                try { Changed?.Invoke(); }
                catch (Exception ex) { Log.Warn("主题切换回调失败：" + ex.Message); }
            }
        }

        /// <summary>
        /// 把窗口的原生背景画刷改成当前主题背景色。
        ///
        /// WPF 的窗口在第一次渲染出内容之前，Windows 会用窗口类的背景画刷擦一遍客户区，
        /// 默认是白色 —— 这就是启动时那一闪而过的白框。这里在窗口刚创建（HWND 已存在、
        /// 还没画第一帧）时把类画刷换成主题底色，那段时间就只会看到正常的窗口底色。
        ///
        /// 画刷交给窗口类持有，进程退出时才释放，所以**不要**在这里 DeleteObject。
        /// </summary>
        public static void PaintWindowBack(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || !_classBrushPatched) return;
                var c = Background;
                var brush = CreateSolidBrush((uint)(c.R | (c.G << 8) | (c.B << 16)));
                if (brush == IntPtr.Zero) return;
                // GCLP_HBRBACKGROUND = -10
                var old = SetClassLongPtr(hwnd, -10, brush);
                Log.Info($"[启动] 窗口类背景画刷 {old.ToInt64():X} → {brush.ToInt64():X}（主题底色 #{c.R:X2}{c.G:X2}{c.B:X2}）");
            }
            catch (Exception ex) { Log.Warn("设置窗口背景画刷失败：" + ex.Message); }
        }

        /// <summary>是否允许改窗口类背景（诊断开关 OL_NO_CLASS_BRUSH=1 可关掉做对比）</summary>
        static readonly bool _classBrushPatched = Environment.GetEnvironmentVariable("OL_NO_CLASS_BRUSH") != "1";

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        static extern IntPtr CreateSolidBrush(uint color);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
        static extern IntPtr SetClassLongPtr(IntPtr hWnd, int index, IntPtr value);

        /// <summary>
        /// "跟随系统"时定时看一眼系统设置。Windows 在 .NET Framework 下没有轻量的主题变更通知，
        /// 隔几秒读一次注册表比引 Windows.Forms 更划算。
        /// </summary>
        static DispatcherTimer _watch;

        static void WatchSystem()
        {
            if (Mode != ThemeMode.System || _watch != null) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            _watch = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(4) };
            _watch.Tick += (s, e) =>
            {
                if (Mode != ThemeMode.System) return;
                // 用户在设置里改过主题色，这里沿用
                bool dark = SystemPrefersDark();
                if (dark != IsDark) Apply(Settings.ThemeColor, ThemeMode.System);
            };
            _watch.Start();
        }

        /// <summary>中性色。所有键在 Styles.xaml 里都有同名默认值，这里只是运行时覆盖。</summary>
        static void ApplyPalette(ResourceDictionary r, bool dark)
        {
            void set(string key, string hex) => r[key] = Frozen(Parse(hex, Colors.Transparent));
            Background = Parse(dark ? "#10151B" : "#EEF2F8", Background);

            if (dark)
            {
                set("Bg", "#10151B");
                set("Card", "#1A212A");
                set("CardHover", "#232C37");
                set("Line", "#2C3743");
                set("Text", "#E7ECF4");
                set("Text2", "#A6B1C0");
                set("Text3", "#737F8D");
                set("Danger", "#FF7075");
                set("DangerSoft", "#3A2226");
                set("Ok", "#3AC089");
                set("OkSoft", "#15322A");
                set("Warn", "#E9A23B");
                set("WarnSoft", "#372C18");
                set("LogBg", "#0B0F14");
                set("SwitchOff", "#3B4653");
                set("ScrollThumb", "#40FFFFFF");
                set("ScrollThumbHover", "#78FFFFFF");
                set("Scrim", "#8C090D12");
                set("ScrimSoft", "#33000000");
                set("ToastBg", "#F22A3441");
            }
            else
            {
                set("Bg", "#EEF2F8");
                set("Card", "#FFFFFF");
                set("CardHover", "#F7F9FC");
                set("Line", "#E3E8F0");
                set("Text", "#1F2633");
                set("Text2", "#5B6474");
                set("Text3", "#8E97A6");
                set("Danger", "#E5484D");
                set("DangerSoft", "#FDECEC");
                set("Ok", "#22A06B");
                set("OkSoft", "#E3F6EE");
                set("Warn", "#E08A00");
                set("WarnSoft", "#FFF4E0");
                set("LogBg", "#FAFBFD");
                set("SwitchOff", "#C9D0DB");
                set("ScrollThumb", "#33000000");
                set("ScrollThumbHover", "#66000000");
                set("Scrim", "#73101828");
                set("ScrimSoft", "#22101828");
                set("ToastBg", "#EE1F2633");
            }
        }

        /// <summary>
        /// 主题色派生的画刷。深色模式下"淡色"要往卡片底色方向调，否则淡蓝底会亮得刺眼。
        /// </summary>
        static void ApplyAccent(ResourceDictionary r, Color c, bool dark)
        {
            var softBase = dark ? Parse("#1A212A", Colors.Black) : Colors.White;
            r["AccentColor"] = c;
            r["Accent"] = Frozen(c);
            r["AccentHover"] = Frozen(Mix(c, Colors.White, dark ? 0.18 : 0.14));
            r["AccentPressed"] = Frozen(Mix(c, Colors.Black, dark ? 0.24 : 0.12));
            r["AccentSoft"] = Frozen(Mix(c, softBase, dark ? 0.8 : 0.9));
            r["AccentSoft2"] = Frozen(Mix(c, softBase, dark ? 0.62 : 0.78));

            // 标题栏渐变：深色模式下整体压暗，白字才不刺眼
            var from = c;
            var to = Mix(c, Color.FromRgb(0x7C, 0x5C, 0xFA), 0.22);
            if (dark)
            {
                var deep = Parse("#0D1117", Colors.Black);
                from = Mix(from, deep, 0.42);
                to = Mix(to, deep, 0.42);
            }
            var title = new LinearGradientBrush(from, to, 0);
            title.Freeze();
            r["TitleBrush"] = title;
        }

        static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        public static Color Mix(Color a, Color b, double t)
        {
            byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
            return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
        }

        /// <summary>读本地图片为冻结的 BitmapImage（不锁文件）</summary>
        public static BitmapImage LoadImage(string path, int decodeWidth = 0)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                using (var fs = File.OpenRead(path)) { bmp.StreamSource = fs; bmp.EndInit(); }
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        public static BitmapImage LoadImage(byte[] data, int decodeWidth = 0)
        {
            try
            {
                if (data == null || data.Length == 0) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                bmp.StreamSource = new MemoryStream(data);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }
}
