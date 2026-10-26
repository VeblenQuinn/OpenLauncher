using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SquidLauncher.Core;
using SquidLauncher.SeedMap;
using SquidLauncher.UI;

namespace SquidLauncher.UI.Pages
{
    /// <summary>
    /// 一张已经算好的地图瓦片。整张地图由瓦片拼成：
    /// 算好一块就往画布上放一块（淡入），没算过的地方就空着。
    /// </summary>
    sealed class MapTile
    {
        public (int mc, int dim, int level, long seed, int tx, int tz) Key;
        /// <summary>瓦片格坐标，世界原点 = (Tx, Tz) × 边长</summary>
        public int Tx, Tz;
        /// <summary>算这块时用的「每像素多少方块」。缩放后旧瓦片先拉伸顶着，新的算好再换掉。</summary>
        public int Level;
        /// <summary>群系 id，用于悬停查询</summary>
        public int[] BiomeIds;
        /// <summary>加入顺序，淘汰时先丢旧的</summary>
        public int Seq;
        public Image Img;
    }

    /// <summary>
    /// 种子地图：离线预测某个种子下的群系分布。
    ///
    /// 世界生成由 cubiomes 完成——MIT 许可的 C 库，算法与游戏逐位一致，
    /// 本项目只负责界面、坐标换算和配色，不自己重写噪声。
    /// 引擎第一次打开这一页时自动下载到 数据目录\tools\cubiomes.dll。
    ///
    /// 性能策略：地图由 256×256 的瓦片拼成，每块单独后台计算、算好淡入。
    /// 拖动只是改瓦片位置（不重算），缩放只是改瓦片尺寸（也不重算）；
    /// 新露出来的区域先空着，谁算好了谁淡入。所以拖动和缩放永远是跟手的，
    /// 只有没算过的地方在等。
    ///
    /// 缩放是连续的：视图倍率是浮点数，滚一格按比例缩放并带缓动，
    /// 画面当场拉伸/收缩。瓦片数据则按 2 的幂分级（LOD，4~256），
    /// 取「不小于当前倍率」的那一级，所以任意倍率下瓦片最多被放大 2 倍。
    /// </summary>
    public partial class SeedMapView : UserControl
    {
        /// <summary>
        /// 版本 → cubiomes 的 MCVersion 枚举值。
        /// 编号来自由社区维护的 cubiomes 分支（xpple / Conflux-Union）的 biomes.h：
        /// 上游只到 MC_1_21_WD，这个分支往后加了 1.21.5 / 1.21.6 / 1.21.9 / 1.21.11 / 26.1~26.3。
        /// 换引擎时这里要跟着对一遍。
        /// </summary>
        static readonly (string name, int mc)[] Versions =
        {
            ("26.3",                 35),
            ("26.2",                 34),
            ("26.1",                 33),
            ("1.21.11",              32),
            ("1.21.9",               31),
            ("1.21.6",               30),
            ("1.21.5",               29),
            ("1.21.4 (Winter Drop)", 28),
            ("1.21.3",               27),
            ("1.21.1",               26),
            ("1.20.6",               25),
            ("1.19.4",               24),
            ("1.18.2",               22),
        };

        /// <summary>最细：每像素 4 方块（再细就没意义，cubiomes 也没法更准）</summary>
        const double ZoomMin = 4;
        /// <summary>最粗：每像素 256 方块</summary>
        const double ZoomMax = 256;
        /// <summary>瓦片数据的最细分级。cubiomes 只接受整数 scale，所以数据按档缓存。</summary>
        const int LevelMin = 4;
        const int LevelMax = 256;

        /// <summary>
        /// 瓦片边长（屏幕像素）。一块 256×256 在 1:16 下覆盖 4096 方块。
        /// 再小的话 cubiomes 每次调用的固定开销（setupGenerator + allocCache）
        /// 会占掉大半时间；再大的话空白区要等太久才补上。
        /// </summary>
        const int TilePx = 256;

        /// <summary>
        /// 同时最多算几块。cubiomes 是纯 CPU 计算，每块独立（每个线程一个 Generator），
        /// 所以按核数开才有意义；但别把整机占满，留点余量给界面线程。
        /// </summary>
        static readonly int TileWorkers = Math.Max(2, Math.Min(4, Environment.ProcessorCount - 1));

        /// <summary>瓦片缓存上限（块）。每块 256×256×4B = 256KB，64 块约 16MB。</summary>
        const int TileCap = 64;

        /// <summary>瓦片淡入时长（毫秒）</summary>
        const int FadeMs = 180;

        /// <summary>滚一格（120）缩放多少倍</summary>
        const double WheelStep = 1.25;

        /// <summary>缩放缓动：每帧向目标逼近的比例（16ms 一帧，约 0.2 秒收敛）</summary>
        const double ZoomEase = 0.28;

        /// <summary>世界坐标安全范围（方块）</summary>
        const double WorldLimit = 30000000;

        long _seed;
        int _mc = 35;                                  // 默认 26.3
        MapDimension _dim = MapDimension.Overworld;

        /// <summary>视图倍率：每个屏幕像素代表多少方块。连续的浮点数，不是档位。</summary>
        double _zoom = 16;
        /// <summary>缓动目标。滚轮只改它，每帧再把 _zoom 往它逼近。</summary>
        double _zoomTarget = 16;
        /// <summary>缩放锚点：这个屏幕位置下的世界坐标在缩放过程中保持不动</summary>
        Point _anchorScreen;
        double _anchorWx, _anchorWz;
        DispatcherTimer _zoomTimer;

        double _cx, _cz;                               // 视图中心（方块坐标）
        Point? _dragFrom;
        double _dragStartCx, _dragStartCz;

        readonly Dictionary<(int mc, int dim, int level, long seed, int tx, int tz), MapTile> _tiles
            = new Dictionary<(int, int, int, long, int, int), MapTile>();
        readonly HashSet<(int mc, int dim, int level, long seed, int tx, int tz)> _pending
            = new HashSet<(int, int, int, long, int, int)>();
        readonly SemaphoreSlim _slots = new SemaphoreSlim(TileWorkers, TileWorkers);

        /// <summary>种子/版本/维度一变就 +1，在途任务的结果直接丢弃</summary>
        int _epoch;
        int _failStreak;
        int _seq;

        // ---------- 结构标记 ----------

        /// <summary>扫描到的一个结构点</summary>
        sealed class StructMark
        {
            public StructureKind Kind;
            public int X, Z;
            public Border Dot;
            public Ellipse Ring;
        }

        /// <summary>勾选了哪些结构（用 StructureKind.Key，跨维度各自独立）</summary>
        readonly HashSet<string> _structOn = new HashSet<string>();

        /// <summary>当前维度可用的结构种类 → 对应的复选框</summary>
        readonly Dictionary<string, CheckBox> _structBoxes = new Dictionary<string, CheckBox>();

        readonly List<StructMark> _marks = new List<StructMark>();
        List<StructureKind> _structKinds = new List<StructureKind>();

        /// <summary>结构扫描的代号：视图一动就 +1，旧结果丢掉</summary>
        int _structEpoch;
        /// <summary>扫描中的标记，避免同一时刻跑两份</summary>
        bool _structBusy;
        /// <summary>扫描完成后是否还要再扫一次（视图在扫描期间变过）</summary>
        bool _structDirty;

        /// <summary>刚按下的位置命中哪个标记（松手时没拖动就弹详情）</summary>
        StructMark _pendingMark;
        Point _downPos;

        /// <summary>当前弹着的详情卡片对应的结构</summary>
        StructMark _cardMark;

        /// <summary>「前往坐标」的目标点（null = 还没跳过，不画标记）</summary>
        int? _jumpX, _jumpZ;

        /// <summary>结构标记上限。超过就不再画，避免极端缩放下铺满屏幕。</summary>
        const int MarkCap = 600;

        /// <summary>
        /// 结构扫描窗口的单边上限（方块）。结构按 512 方块一格遍历，
        /// 16384 方块 = 33×33 = 1089 个区域，七类合计几十毫秒——实测值，
        /// 调到 65536 会变成 129×129，慢到十几秒，绝对不能放大。
        /// 缩得很远时只标出视野中心附近的结构（那时标记本来也密得看不清）。
        /// </summary>
        const int MaxScanBlocks = 16384;

        /// <summary>实际算了多少块瓦片（诊断用）</summary>
        public int RenderCount { get; private set; }
        /// <summary>最近一块瓦片的耗时（毫秒，诊断用）</summary>
        public double LastRenderMs { get; private set; }

        /// <summary>引擎是不是已经装好（内置解出或旧的下载）</summary>
        bool _provisioning;
        bool _versionSynced;
        /// <summary>升级提示只问一次，别每次进页都弹</summary>
        bool _upgradeAsked;
        DispatcherTimer _progressTimer;
        LauncherTask _provisionTask;

        public SeedMapView()
        {
            InitializeComponent();
            Loaded += (_, __) => Init();
        }

        /// <summary>
        /// 瓦片数据的分级（LOD）。视图倍率是连续的，但 cubiomes 的 scale 是整数，
        /// 而且每块瓦片都重算一遍太贵，所以数据按 2 的幂分级缓存。
        /// 取「不小于当前倍率」的那一级 ⇒ 瓦片最多被放大 2 倍。
        /// </summary>
        int Level
        {
            get
            {
                int lv = LevelMin;
                while (lv < _zoom && lv < LevelMax) lv *= 2;
                return lv;
            }
        }

        void Init()
        {
            if (VersionBox.Items.Count == 0)
            {
                foreach (var v in Versions) VersionBox.Items.Add(v.name);
                VersionBox.SelectedIndex = 0;          // 26.3

                VersionBox.SelectionChanged += (_, __) =>
                {
                    int i = VersionBox.SelectedIndex;
                    if (i < 0 || i >= Versions.Length) return;
                    _mc = Versions[i].mc;
                    Render();
                };
                DimBox.SelectionChanged += (_, __) =>
                {
                    _dim = DimBox.SelectedIndex switch
                    {
                        1 => MapDimension.Nether,
                        2 => MapDimension.End,
                        _ => MapDimension.Overworld,
                    };
                    RebuildStructList();
                    Render();
                };
                SeedBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) { ApplySeed(); Render(); } };
                SeedBox.LostFocus += (_, __) => ApplySeed();

                MapHost.MouseLeftButtonDown += OnDown;
                MapHost.MouseLeftButtonUp += OnUp;
                MapHost.MouseMove += OnMove;
                MapHost.MouseLeave += (_, __) => { if (_dragFrom != null) EndDrag(); };
                MapHost.SizeChanged += (_, __) => { LayoutTiles(); UpdateTiles(); RefreshStructures(); };
                MapHost.PreviewMouseWheel += OnWheel;
            }
            RebuildStructList();
            UpdateZoomText();
            UpdateEngineState();
        }

        /// <summary>外部调用：进入这一页时确保引擎就位并刷新</summary>
        public void Activate()
        {
            Init();
            if (_provisioning) { UpdateEngineState(); return; }

            if (!CubiomesEngine.Available)
            {
                // 先把内置那份解出来。这只是写个本地文件，不算「替用户做决定」。
                if (EngineProvisioner.TryInstallBundled()) CubiomesEngine.Reload();
            }

            if (CubiomesEngine.Available)
            {
                UpdateEngineState();
                Render();

                // 旧引擎（缺结构导出、或扫描有 bug）要换，但**换不换由用户定**。
                // 用户自己挑过的引擎一律不动，免得把他选的顶掉。
                if (NeedsEngineUpgrade && !EngineProvisioner.IsCustom) AskEngineUpgradeAsync();
                return;
            }

            // 内置那份也不可用。绝不自作主张去下载，把情况和选项交给用户。
            string why = CurrentEngineProblem();
            ShowFailure("生成引擎不可用", why);
            AskEngineSourceAsync("生成引擎不可用", why);
        }

        /// <summary>把「为什么用不了」说清楚，别只丢个 null 给用户</summary>
        string CurrentEngineProblem()
        {
            if (CubiomesEngine.Available) return "未知原因";
            var load = CubiomesEngine.LoadError;
            if (!string.IsNullOrWhiteSpace(load)) return load;
            if (!EngineProvisioner.Installed) return "引擎文件不存在（" + CubiomesEngine.DllPath + "）";
            return "引擎文件在，但加载失败";
        }

        // ---------- 引擎准备（一律由用户决定，不自动联网） ----------

        /// <summary>用户可以怎么处理引擎问题</summary>
        enum EngineChoice { Cancel, PickFile, UseBundled, Download }

        /// <summary>
        /// 弹窗问用户怎么办。**一切需要联网或替换文件的操作都必须经过这里**，
        /// 程序不替用户选择。
        ///
        /// <paramref name="allowBundled"/> 为 true 时多给一个「用程序自带的引擎」选项
        /// （用于旧引擎升级：新版本程序里就带着新版引擎，重解一次即可，不必联网）。
        /// </summary>
        async void AskEngineSourceAsync(string title, string detail, bool allowBundled = false)
        {
            try
            {
                var body = new StackPanel();

                var t = Dialogs.Text(detail);
                body.Children.Add(t);

                var ask = Dialogs.Text("引擎是计算群系的本地库（cubiomes，MIT 许可）。没有它就算不出地图。\n" +
                                       "你想怎么处理？");
                ask.Margin = new Thickness(0, 10, 0, 0);
                body.Children.Add(ask);

                // EngineUrl 留空时（默认）根本不给「从网络下载」这个选项——
                // 连地址都没配，摆个按钮出来只会让用户白点一次。
                var note = Branding.EngineUrl.Length > 0
                    ? Dialogs.Text("提示：从网络下载只会连 " + Branding.EngineUrl +
                                   " 这一个地址；连不上就是连不上。也可以自己编译一份 cubiomes.dll 再选进来。")
                    : Dialogs.Text("提示：这个版本没有配置引擎下载地址，所以只能从本地选文件。" +
                                   "也可以自己编译一份 cubiomes.dll。");
                note.FontSize = 11;
                note.Opacity = 0.75;
                note.Margin = new Thickness(0, 10, 0, 0);
                body.Children.Add(note);

                var buttons = new List<string>();
                var choices = new List<EngineChoice>();
                void Add(string label, EngineChoice c) { buttons.Add(label); choices.Add(c); }

                Add("暂不处理", EngineChoice.Cancel);
                Add("选择本地文件…", EngineChoice.PickFile);
                if (Branding.EngineUrl.Length > 0) Add("从网络下载", EngineChoice.Download);
                if (allowBundled) Add("用程序自带的", EngineChoice.UseBundled);

                int primary = allowBundled ? buttons.Count - 1 : 1;
                int r = await Dialogs.ShowAsync(title, body, buttons.ToArray(), primary: primary);

                int idx = (r >= 0 && r < choices.Count) ? r : 0;
                switch (choices[idx])
                {
                    case EngineChoice.PickFile: PickEngineFile(); break;
                    case EngineChoice.UseBundled: UseBundledEngine(); break;
                    case EngineChoice.Download: DownloadEngine(); break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("询问引擎来源失败：" + ex);
            }
        }

        /// <summary>
        /// 已装的引擎太旧（缺结构导出、或扫描实现有 bug）。地图其实还能用，
        /// 所以只问一句，用户不想换就继续用旧功能。
        /// </summary>
        async void AskEngineUpgradeAsync()
        {
            if (_upgradeAsked) return;
            _upgradeAsked = true;

            string missing = !CubiomesEngine.SupportsStructureScan
                ? "当前引擎不支持结构查找。"
                : !CubiomesEngine.SupportsStrongholds
                    ? "当前引擎不支持要塞位置计算。"
                    : "当前引擎版本较旧（" + (EngineProvisioner.InstalledVersion ?? "未知") + "）。";

            try
            {
                var body = new StackPanel();
                body.Children.Add(Dialogs.Text(
                    missing + "\n\n程序自带的是 " + Branding.EngineVersion + "。" +
                    "换成自带的那份不联网，但你之前手动指定的引擎会被替换。"));

                var buttons = new List<string> { "继续用现在的", "选择本地文件…" };
                var choices = new List<EngineChoice> { EngineChoice.Cancel, EngineChoice.PickFile };
                if (Branding.EngineUrl.Length > 0)
                {
                    buttons.Add("从网络下载");
                    choices.Add(EngineChoice.Download);
                }
                buttons.Add("用程序自带的");
                choices.Add(EngineChoice.UseBundled);

                int r = await Dialogs.ShowAsync("引擎可以更新", body, buttons.ToArray(),
                    primary: buttons.Count - 1);

                int idx = (r >= 0 && r < choices.Count) ? r : 0;
                switch (choices[idx])
                {
                    case EngineChoice.PickFile: PickEngineFile(); break;
                    case EngineChoice.Download: DownloadEngine(); break;
                    case EngineChoice.UseBundled: UseBundledEngine(); break;
                }
            }
            catch (Exception ex) { Log.Warn("询问引擎更新失败：" + ex); }
        }

        /// <summary>用户选了「用程序自带的」：删掉现有文件后重新解一次</summary>
        void UseBundledEngine()
        {
            try
            {
                // 用户明确要求用自带的，所以即使是自选引擎也应该被替换 —— 先清掉来源标记
                var target = CubiomesEngine.DllPath;
                try { if (File.Exists(target)) File.Delete(target); } catch { /* 占用就覆盖写 */ }

                Settings.Set("seedmap.engineSource", JNode.From(""));
                Settings.SaveNow();
            }
            catch (Exception ex) { Log.Warn("清理旧引擎失败：" + ex.Message); }

            if (EngineProvisioner.TryInstallBundled())
            {
                CubiomesEngine.Reload();
                if (CubiomesEngine.Available) { HideNotice(); UpdateEngineState(); Render(); return; }
            }

            var why = "程序自带的引擎也用不了（可能是这个 exe 里没有打包引擎）";
            ShowFailure("生成引擎不可用", why);
            AskEngineSourceAsync("生成引擎不可用", why);
        }

        /// <summary>用户选了「从网络下载」</summary>
        void DownloadEngine()
        {
            if (_provisioning) return;

            // 没配地址就说明这个版本不支持联网取引擎，直接退回去让用户选文件
            if (Branding.EngineUrl.Length == 0)
            {
                var why0 = "这个版本没有配置引擎下载地址，只能选择本地文件。";
                ShowFailure("无法联网下载", why0);
                AskEngineSourceAsync("无法联网下载", why0);
                return;
            }

            _provisioning = true;

            NoticePanel.Visibility = Visibility.Visible;
            NoticeActions.Visibility = Visibility.Collapsed;
            NoticeProgress.Visibility = Visibility.Visible;
            NoticeProgress.IsIndeterminate = false;
            NoticeProgress.Value = 0;
            NoticeTitle.Text = "正在下载生成引擎";
            NoticeBody.Text = "从 " + Branding.EngineUrl + " 下载。下载完会校验哈希，不一致会自动丢弃。";
            NoticeStatus.Text = "正在连接…";

            _provisionTask = TaskManager.Start("下载种子地图引擎", t => EngineProvisioner.InstallFromUrlAsync(t));

            _progressTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
            _progressTimer.Tick += (_, __) =>
            {
                var t = _provisionTask;
                if (t == null) return;
                var p = t.Progress;
                NoticeProgress.IsIndeterminate = p < 0;
                if (p >= 0) NoticeProgress.Value = Math.Min(1, p);
                if (!string.IsNullOrEmpty(t.Detail)) NoticeStatus.Text = t.Detail;
            };
            _progressTimer.Start();

            void onFinished(LauncherTask finished)
            {
                if (finished != _provisionTask) return;
                TaskManager.Finished -= onFinished;
                TaskManager.OnUi(() => EndProvision(finished));
            }
            TaskManager.Finished += onFinished;
        }

        /// <summary>用户选了「选择本地文件」</summary>
        void PickEngineFile()
        {
            var path = Pickers.PickFile("选择 cubiomes.dll", "动态链接库 (*.dll)|*.dll|所有文件 (*.*)|*.*");
            if (string.IsNullOrWhiteSpace(path)) { AskEngineSourceAsync("生成引擎不可用", CurrentEngineProblem()); return; }

            var error = EngineProvisioner.InstallFromFile(path);
            if (error == null)
            {
                HideNotice();
                UpdateEngineState();
                Render();
                return;
            }

            ShowFailure("这个引擎文件用不了", error);
            AskEngineSourceAsync("这个引擎文件用不了", error);
        }

        void EndProvision(LauncherTask task)
        {
            _provisioning = false;
            if (_progressTimer != null) { _progressTimer.Stop(); _progressTimer = null; }
            NoticeProgress.Visibility = Visibility.Collapsed;

            if (task.State == TaskState.Done)
            {
                CubiomesEngine.Reload();
                if (CubiomesEngine.Available)
                {
                    HideNotice();
                    UpdateEngineState();
                    Render();
                    return;
                }
                var why = "下载完成了，但加载不起来：" + (CubiomesEngine.LoadError ?? "未知原因");
                ShowFailure("生成引擎不可用", why);
                AskEngineSourceAsync("生成引擎不可用", why);
                return;
            }

            // 失败或取消：都不自动重试，交回给用户
            var reason = task.State == TaskState.Canceled ? "下载已取消" : (task.Error ?? "下载失败");
            ShowFailure("下载失败", reason);
            AskEngineSourceAsync("下载失败", reason);
        }

        /// <summary>遮罩式的「引擎不可用」提示板，带一个选择引擎的按钮</summary>
        void ShowFailure(string title, string detail)
        {
            NoticePanel.Visibility = Visibility.Visible;
            NoticeActions.Visibility = Visibility.Visible;
            NoticeTitle.Text = title;
            NoticeBody.Text = "引擎打包在程序里，正常应该自动解出。现在用不了，可以自己选一个引擎文件" +
                              "（自己编译的、或从别处拿到的 cubiomes.dll）" +
                              (Branding.EngineUrl.Length > 0 ? "，或者联网下载一份。" : "。");
            NoticeStatus.Text = detail + "\n" + CubiomesEngine.DllPath;
            UpdateEngineState();
        }

        void HideNotice() => NoticePanel.Visibility = Visibility.Collapsed;

        /// <summary>遮罩上的「重试」：只是重新问一次，不替用户做决定</summary>
        void Retry_Click(object sender, RoutedEventArgs e)
        {
            if (_provisioning) return;
            var why = CurrentEngineProblem();
            AskEngineSourceAsync("生成引擎不可用", why);
        }

        /// <summary>
        /// 已装的引擎要不要换。两种都换：
        /// · 缺 ol_structure_scan 导出（更早的 DLL，连结构都找不到）
        /// · 版本字符串和当前 Branding.EngineVersion 不一致（例如 0.1.21 那份
        ///   结构扫描区域算错、离原点远了就找不到结构的 DLL）
        /// </summary>
        bool NeedsEngineUpgrade
        {
            get
            {
                if (!CubiomesEngine.SupportsStructureScan) return true;
                var installed = EngineProvisioner.InstalledVersion;
                var want = Branding.EngineVersion;
                if (string.IsNullOrWhiteSpace(want)) return false;
                return !string.Equals(installed ?? "", want, StringComparison.Ordinal);
            }
        }

        void UpdateEngineState()
        {
            bool ok = CubiomesEngine.Available;
            if (ok)
            {
                // 就绪是正常状态，不用占着状态条的版面；只有出问题时才显示一行说明
                EngineState.Text = "";
                EngineState.Visibility = Visibility.Collapsed;
                HeaderSub.Text = "离线和游戏一致的群系预测";
                NoticePanel.Visibility = Visibility.Collapsed;
                SyncVersionToEngine();
            }
            else
            {
                var ver = EngineProvisioner.InstalledVersion;
                EngineState.Text = "生成引擎未就绪 · " + (CubiomesEngine.LoadError ?? "还没下载")
                    + (string.IsNullOrWhiteSpace(ver) ? "" : " · " + ver);
                EngineState.Visibility = Visibility.Visible;
                HeaderSub.Text = "正在准备生成引擎…";
            }
        }

        /// <summary>
        /// 引擎支持的最高版本可能比界面版本表低（用户自己换了旧引擎），
        /// 这时把下拉切到引擎真能算的那一档，而不是让用户看“算不出来”。
        /// 只在第一次同步，之后尊重用户自己的选择。
        /// </summary>
        void SyncVersionToEngine()
        {
            if (_versionSynced) return;
            _versionSynced = true;
            int newest = CubiomesEngine.NewestVersion;
            if (newest < 0) return;
            int idx = -1;
            for (int i = 0; i < Versions.Length; i++)
                if (Versions[i].mc <= newest) { idx = i; break; }
            if (idx > 0 && idx < Versions.Length)
            {
                VersionBox.SelectedIndex = idx;
                _mc = Versions[idx].mc;
                // 状态条上不再显示引擎信息，这种自动校正只记日志
                Log.Info($"种子地图：引擎最高支持到 {Versions[idx].name}，版本下拉已自动切换");
            }
        }

        // ---------- 种子 ----------

        void ApplySeed()
        {
            var text = (SeedBox.Text ?? "").Trim();
            if (long.TryParse(text, out var n)) { _seed = n; return; }
            // 游戏对非数字种子用 Java 的 String.hashCode()
            _seed = string.IsNullOrEmpty(text) ? 0 : JavaHash(text);
        }

        /// <summary>Java String.hashCode()：s[0]*31^(n-1) + ... + s[n-1]，32 位有符号</summary>
        static long JavaHash(string s)
        {
            unchecked
            {
                int h = 0;
                foreach (var ch in s) h = 31 * h + ch;
                return h;
            }
        }

        // ---------- 缩放（连续 + 缓动） ----------

        void UpdateZoomText()
        {
            if (ZoomText == null) return;
            ZoomText.Text = _zoom < 1.05 ? "1 : 1" : $"1 : {_zoom:0.#}";
        }

        /// <summary>
        /// 把倍率改成 target。anchor 是「缩放中心」的屏幕坐标：
        /// 那个屏幕点底下的世界坐标在缩放过程中保持不动，所以滚轮缩放是「对着鼠标放大」。
        /// 不是立刻跳过去，而是每帧向目标逼近（约 0.2 秒），滚轮连滚也不会一顿一顿。
        /// </summary>
        void ZoomTo(double target, Point? anchor)
        {
            target = Math.Max(ZoomMin, Math.Min(ZoomMax, target));
            if (Math.Abs(target - _zoomTarget) < 1e-6 && _zoomTimer == null) return;

            int vw = Math.Max(1, (int)MapHost.ActualWidth);
            int vh = Math.Max(1, (int)MapHost.ActualHeight);
            var a = anchor ?? new Point(vw / 2.0, vh / 2.0);

            // 记下锚点现在对应的世界坐标
            _anchorScreen = a;
            _anchorWx = _cx + (a.X - vw / 2.0) * _zoom;
            _anchorWz = _cz + (a.Y - vh / 2.0) * _zoom;

            _zoomTarget = target;
            if (_zoomTimer == null)
            {
                _zoomTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
                _zoomTimer.Tick += ZoomTick;
                _zoomTimer.Start();
            }
        }

        void ZoomTick(object sender, EventArgs e)
        {
            double diff = _zoomTarget - _zoom;
            if (Math.Abs(diff) <= _zoomTarget * 0.002)
            {
                _zoom = _zoomTarget;
                StopZoomAnim();
                LayoutTiles();
                UpdateTiles();
                RefreshStructures();
                UpdateZoomText();
                return;
            }

            _zoom += diff * ZoomEase;
            ApplyAnchor();
            LayoutTiles();
            UpdateZoomText();
        }

        void StopZoomAnim()
        {
            if (_zoomTimer != null) { _zoomTimer.Stop(); _zoomTimer.Tick -= ZoomTick; _zoomTimer = null; }
        }

        bool ZoomAnimating => _zoomTimer != null;

        /// <summary>按当前 _zoom 反推中心，使锚点世界坐标仍落在锚点屏幕位置</summary>
        void ApplyAnchor()
        {
            int vw = Math.Max(1, (int)MapHost.ActualWidth);
            int vh = Math.Max(1, (int)MapHost.ActualHeight);
            _cx = _anchorWx - (_anchorScreen.X - vw / 2.0) * _zoom;
            _cz = _anchorWz - (_anchorScreen.Y - vh / 2.0) * _zoom;
            ClampCenter();
        }

        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            // 高精度滚轮（触摸板）delta 不一定是 120 的整数倍，所以按比例算
            double notches = e.Delta / 120.0;
            ZoomTo(_zoomTarget * Math.Pow(WheelStep, -notches), e.GetPosition(MapHost));
            e.Handled = true;
        }

        // ---------- 瓦片 ----------

        /// <summary>清空重来：换了种子/版本/维度就整张图作废</summary>
        void Render()
        {
            if (!CubiomesEngine.Available) return;

            _epoch++;
            _failStreak = 0;
            foreach (var t in _tiles.Values) MapCanvas.Children.Remove(t.Img);
            _tiles.Clear();
            _pending.Clear();

            NoticePanel.Visibility = Visibility.Collapsed;

            // 换种子/版本/维度后，之前「前往坐标」那个点已经没有意义了（那儿的东西变了），
            // 留着会误导，直接收掉
            _jumpX = _jumpZ = null;
            if (JumpMark != null) JumpMark.Visibility = Visibility.Collapsed;

            LayoutTiles();
            UpdateTiles();
            RefreshStructures();
        }

        /// <summary>当前渲染参数的指纹前缀（不含瓦片坐标）</summary>
        (int mc, int dim, int level, long seed, int tx, int tz) KeyOf(int tx, int tz)
            => (_mc, (int)_dim, Level, _seed, tx, tz);

        /// <summary>视口（外扩 margin 圈瓦片）覆盖的瓦片格范围</summary>
        void TileRange(int margin, out int tx0, out int tz0, out int tx1, out int tz1)
        {
            int vw = Math.Max(1, (int)MapHost.ActualWidth);
            int vh = Math.Max(1, (int)MapHost.ActualHeight);
            double world = (double)TilePx * Level;
            double left = _cx - vw * _zoom / 2.0;
            double top = _cz - vh * _zoom / 2.0;

            tx0 = (int)Math.Floor(left / world) - margin;
            tz0 = (int)Math.Floor(top / world) - margin;
            tx1 = (int)Math.Floor((left + vw * _zoom) / world) + margin;
            tz1 = (int)Math.Floor((top + vh * _zoom) / world) + margin;
        }

        /// <summary>这块瓦片还在（或接近）视口里吗——在途任务用它决定要不要白算</summary>
        bool StillNeeded(int tx, int tz)
        {
            TileRange(1, out int x0, out int z0, out int x1, out int z1);
            return tx >= x0 && tx <= x1 && tz >= z0 && tz <= z1;
        }

        /// <summary>
        /// 把每块瓦片摆到它该在的屏幕位置。纯几何计算，跟算地形完全无关，
        /// 所以拖动/缩放的每一帧都只是改几个 Image 的坐标和尺寸。
        /// </summary>
        void LayoutTiles()
        {
            int vw = (int)MapHost.ActualWidth;
            int vh = (int)MapHost.ActualHeight;
            if (vw <= 0 || vh <= 0) return;
            int level = Level;
            foreach (var t in _tiles.Values) PlaceTile(t, level, vw, vh);
            LayoutMarks();   // 标记跟着一起走，拖动缩放都是同一个几何变换
            LayoutJumpMark();
        }

        void PlaceTile(MapTile t, int level, int vw, int vh)
        {
            // 分级差太远的旧瓦片（比如连滚好几格）拉伸出来只会更糊，
            // 不如按设计留空，等新一档算好淡入。
            bool usable = t.Level == level || t.Level == level * 2 || t.Level * 2 == level;
            var vis = usable ? Visibility.Visible : Visibility.Collapsed;
            if (t.Img.Visibility != vis) t.Img.Visibility = vis;
            if (!usable) return;

            var r = TileRect(t, vw, vh);
            t.Img.Width = r.w;
            t.Img.Height = r.h;
            // 当前分级永远盖在「上一档拿来做占位」的旧瓦片上面
            Panel.SetZIndex(t.Img, t.Level == level ? 1 : 0);
            Canvas.SetLeft(t.Img, r.x);
            Canvas.SetTop(t.Img, r.y);
        }

        /// <summary>
        /// 瓦片在屏幕上的整数像素矩形。
        ///
        /// 关键是**按瓦片的左右/上下边缘取整**，而不是「左上角取整 + 保留小数的宽高」。
        /// 后者的相邻两块各自独立取整，误差最多差 1 像素：缩放时一会儿露一条缝、
        /// 一会儿又重叠半个像素，看起来就是瓦片在抖。
        /// 按边缘取整后，A 的右边缘和 B 的左边缘算出来是同一个整数，
        /// 无论倍率多零碎都严丝合缝。
        /// </summary>
        (int x, int y, int w, int h) TileRect(MapTile t, int vw, int vh)
        {
            double world = (double)TilePx * t.Level;
            double originX = _cx - vw * _zoom / 2.0;
            double originZ = _cz - vh * _zoom / 2.0;

            int x0 = (int)Math.Round((t.Tx * world - originX) / _zoom);
            int x1 = (int)Math.Round(((t.Tx + 1) * world - originX) / _zoom);
            int z0 = (int)Math.Round((t.Tz * world - originZ) / _zoom);
            int z1 = (int)Math.Round(((t.Tz + 1) * world - originZ) / _zoom);

            return (x0, z0, Math.Max(1, x1 - x0), Math.Max(1, z1 - z0));
        }

        /// <summary>缺哪些瓦片就排哪些：视口内的先算，外圈余量后算，越靠中间越先</summary>
        void UpdateTiles()
        {
            if (!CubiomesEngine.Available) return;
            if (ZoomAnimating) return;      // 缓动中途排的队马上就会作废，等落定再排
            int vw = (int)MapHost.ActualWidth;
            int vh = (int)MapHost.ActualHeight;
            if (vw <= 0 || vh <= 0) return;

            TileRange(1, out int tx0, out int tz0, out int tx1, out int tz1);
            if ((long)(tx1 - tx0 + 1) * (tz1 - tz0 + 1) > 400) return;   // 尺寸异常时别失控

            TileRange(0, out int vx0, out int vz0, out int vx1, out int vz1);

            double world = (double)TilePx * Level;
            var want = new List<(int rank, double d, int tx, int tz)>();
            for (int tz = tz0; tz <= tz1; tz++)
            {
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    var k = KeyOf(tx, tz);
                    if (_tiles.ContainsKey(k) || _pending.Contains(k)) continue;
                    double dx = (tx + 0.5) * world - _cx;
                    double dz = (tz + 0.5) * world - _cz;
                    bool visible = tx >= vx0 && tx <= vx1 && tz >= vz0 && tz <= vz1;
                    want.Add((visible ? 0 : 1, dx * dx + dz * dz, tx, tz));
                }
            }
            want.Sort((a, b) => a.rank != b.rank ? a.rank.CompareTo(b.rank) : a.d.CompareTo(b.d));
            foreach (var w in want) RequestTile(KeyOf(w.tx, w.tz));
        }

        void RequestTile((int mc, int dim, int level, long seed, int tx, int tz) k)
        {
            if (_pending.Contains(k)) return;
            _pending.Add(k);
            var ignored = RenderTileAsync(k, _epoch);
        }

        async Task RenderTileAsync((int mc, int dim, int level, long seed, int tx, int tz) k, int epoch)
        {
            try
            {
                // 排队的瓦片可能已经被拖出视口了，先粗筛一遍，别白算
                if (epoch != _epoch || !StillNeeded(k.tx, k.tz)) return;

                await _slots.WaitAsync();
                try
                {
                    if (epoch != _epoch || !StillNeeded(k.tx, k.tz)) return;

                    int blockX = k.tx * TilePx * k.level;
                    int blockZ = k.tz * TilePx * k.level;

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    MapRender data;
                    try
                    {
                        data = await Task.Run(() => CubiomesEngine.RenderBiomes(
                            k.mc, (MapDimension)k.dim, k.seed, k.level, blockX, blockZ, TilePx, TilePx));
                    }
                    catch (Exception ex) { Log.Warn("渲染地图瓦片失败：" + ex.Message); return; }
                    sw.Stop();

                    RenderCount++;
                    LastRenderMs = sw.Elapsed.TotalMilliseconds;

                    if (data == null)
                    {
                        // 连续几块都算不出来，基本就是这个版本引擎不认识
                        if (++_failStreak >= 3 && epoch == _epoch) ShowRenderFailure();
                        return;
                    }
                    _failStreak = 0;

                    if (epoch != _epoch || !StillNeeded(k.tx, k.tz)) return;
                    CommitTile(k, data);
                }
                finally { _slots.Release(); }
            }
            finally { _pending.Remove(k); }
        }

        void CommitTile((int mc, int dim, int level, long seed, int tx, int tz) k, MapRender data)
        {
            if (_tiles.ContainsKey(k)) return;

            var bmp = BitmapSource.Create(TilePx, TilePx, 96, 96, PixelFormats.Bgra32, null, data.Pixels, TilePx * 4);
            bmp.Freeze();

            var img = new Image
            {
                Source = bmp,
                Stretch = Stretch.Fill,
                Opacity = 0,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
            };
            // 缩放是连续的，瓦片多少会被放大/缩小一点；线性采样比最近邻看起来顺，
            // 而 1:1 时两者没有差别。
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);
            RenderOptions.SetEdgeMode(img, EdgeMode.Aliased);

            var tile = new MapTile
            {
                Key = k,
                Tx = k.tx,
                Tz = k.tz,
                Level = k.level,
                BiomeIds = data.BiomeIds,
                Seq = ++_seq,
                Img = img,
            };
            _tiles[k] = tile;
            MapCanvas.Children.Add(img);
            PlaceTile(tile, Level, Math.Max(1, (int)MapHost.ActualWidth), Math.Max(1, (int)MapHost.ActualHeight));

            // 淡入：这一块从空白变成有内容，用户能看出「刚加载好的是这块」
            img.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeMs))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

            TrimTiles();
        }

        /// <summary>
        /// 超出上限时丢瓦片。先丢分级不对的（它们只是缩放时的垫底），再按加入先后丢。
        /// 保留相邻分级是有用的：来回缩放时那一档是现成的，不用重算。
        /// </summary>
        void TrimTiles()
        {
            if (_tiles.Count <= TileCap) return;

            int level = Level;
            var list = new List<MapTile>(_tiles.Count);
            foreach (var t in _tiles.Values) list.Add(t);
            list.Sort((a, b) =>
            {
                int ra = a.Level == level ? 1 : 0, rb = b.Level == level ? 1 : 0;
                if (ra != rb) return ra.CompareTo(rb);     // 分级不对的排前面（先丢）
                return a.Seq.CompareTo(b.Seq);             // 同优先级先丢旧的
            });

            int remove = _tiles.Count - TileCap;
            for (int i = 0; i < remove && i < list.Count; i++)
            {
                MapCanvas.Children.Remove(list[i].Img);
                _tiles.Remove(list[i].Key);
            }
        }

        void ShowRenderFailure()
        {
            NoticePanel.Visibility = Visibility.Visible;
            NoticeActions.Visibility = Visibility.Visible;
            NoticeTitle.Text = "这个版本算不出来";
            NoticeBody.Text = "当前引擎不认识所选版本的地形数据。换一个更旧的版本试试，" +
                              "或者换一份支持新版本的 cubiomes 引擎。";
            NoticeStatus.Text = CubiomesEngine.DllPath;
        }

        // ---------- 结构 ----------

        /// <summary>
        /// 按当前维度重建结构勾选列表。切换维度时主世界/下界/末地的结构不通用，
        /// 所以列表也跟着换，但勾选状态按 Key 保留。
        /// </summary>
        void RebuildStructList()
        {
            if (StructList == null) return;

            StructList.Children.Clear();
            _structBoxes.Clear();
            _structKinds = StructureCatalog.ForDimension(_dim);

            bool firstBuild = _structOn.Count == 0;
            foreach (var k in _structKinds)
            {
                if (firstBuild && k.DefaultOn) _structOn.Add(k.Key);

                var box = new CheckBox
                {
                    Content = k.Name,
                    IsChecked = _structOn.Contains(k.Key),
                    // 单列排列，行距收一点，不然十几个结构要滚很久
                    Margin = new Thickness(0, 1, 0, 1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    ToolTip = k.Name + "（" + (k.IsNether ? "下界" : k.IsEnd ? "末地" : "主世界") + "）",
                };
                // 用 Tag 记住种类；Key 可能重复显示名（废弃传送门在两个维度都有）
                box.Tag = k.Key;
                // 前景显式绑主题画刷：默认是 SystemColors.ControlTextBrush（固定黑），
                // 暗色模式下勾选框和文字都会糊在黑底上。
                box.SetResourceReference(ForegroundProperty, "Text");
                box.Checked += StructBox_Changed;
                box.Unchecked += StructBox_Changed;

                // 名字前放一个同色小方块，和地图上的标记对得上
                var dot = new Border
                {
                    Width = 8,
                    Height = 8,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(Color.FromArgb(255,
                        (byte)((k.Color >> 16) & 0xFF), (byte)((k.Color >> 8) & 0xFF), (byte)(k.Color & 0xFF))),
                    Margin = new Thickness(0, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                // 文字必须显式绑到主题画刷：TextBlock 的默认前景是
                // SystemColors.ControlTextBrush（固定黑），不会跟着暗色模式变，
                // 也不会从 CheckBox 继承。
                var label = new TextBlock { Text = k.Name, VerticalAlignment = VerticalAlignment.Center };
                label.SetResourceReference(TextBlock.ForegroundProperty, "Text");

                var panel = new StackPanel { Orientation = Orientation.Horizontal };
                panel.Children.Add(dot);
                panel.Children.Add(label);
                box.Content = panel;

                _structBoxes[k.Key] = box;
                StructList.Children.Add(box);
            }
            UpdateStructMaster();
        }

        void StructBox_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox box && box.Tag is string key)
            {
                if (box.IsChecked == true) _structOn.Add(key); else _structOn.Remove(key);
            }
            UpdateStructMaster();
            RefreshStructures();
        }

        void UpdateStructMaster()
        {
            if (StructAll == null) return;
            int on = 0;
            foreach (var b in _structBoxes.Values) if (b.IsChecked == true) on++;
            StructAll.IsChecked = on > 0 && on == _structBoxes.Count;
            UpdateStructHint();
        }

        void StructAll_Click(object sender, RoutedEventArgs e)
        {
            bool want = StructAll.IsChecked == true;
            foreach (var b in _structBoxes.Values)
            {
                // 逐个改会触发多次刷新，先摘掉事件、改完统一刷一次
                b.Checked -= StructBox_Changed;
                b.Unchecked -= StructBox_Changed;
                b.IsChecked = want;
                b.Checked += StructBox_Changed;
                b.Unchecked += StructBox_Changed;

                if (b.Tag is string key)
                {
                    if (want) _structOn.Add(key); else _structOn.Remove(key);
                }
            }
            UpdateStructMaster();
            RefreshStructures();
        }

        void UpdateStructHint()
        {
            if (StructHint == null) return;
            string text = null;
            if (!CubiomesEngine.Available) text = "引擎未就绪，无法查找结构";
            else if (!CubiomesEngine.SupportsStructureScan) text = "当前引擎不支持结构查找，请到“更多 → 种子地图”点重试";
            else if (_structOn.Count == 0) text = "勾选上面的结构就会在地图上标出来";
            else if (_marks.Count > 0) text = $"地图上标出 {_marks.Count} 个结构，点击标记看详情";

            if (string.IsNullOrEmpty(text))
            {
                StructHint.Visibility = Visibility.Collapsed;
                return;
            }
            StructHint.Text = text;
            StructHint.Visibility = Visibility.Visible;
        }

        /// <summary>当前维度里勾上的结构种类</summary>
        List<StructureKind> EnabledKinds()
        {
            var list = new List<StructureKind>();
            foreach (var k in _structKinds) if (_structOn.Contains(k.Key)) list.Add(k);
            return list;
        }

        /// <summary>当前视口（外扩 35%）对应的方块矩形。页还没布局好时返回 false。</summary>
        bool ViewScanRect(out int x0, out int z0, out int x1, out int z1)
        {
            int vw = (int)MapHost.ActualWidth;
            int vh = (int)MapHost.ActualHeight;
            if (vw < 64 || vh < 64) { x0 = z0 = x1 = z1 = 0; return false; }

            // 外扩 35%：小幅拖动时标记已经在手上了，不用等
            const double span = 1.35;
            // 结构按 512 方块一格遍历，扫的范围必须封顶：
            // 缩到 1:256 时视口覆盖 14 万方块，不封顶就是十几万个区域，直接把界面卡死。
            // 封顶后缩得很远时只标出视野中心附近的结构，反正那时标记本来就密得看不清。
            double spanX = Math.Min(span, MaxScanBlocks / Math.Max(1.0, vw * _zoom));
            double spanZ = Math.Min(span, MaxScanBlocks / Math.Max(1.0, vh * _zoom));

            x0 = (int)(_cx - vw * _zoom * spanX / 2);
            z0 = (int)(_cz - vh * _zoom * spanZ / 2);
            x1 = (int)(_cx + vw * _zoom * spanX / 2);
            z1 = (int)(_cz + vh * _zoom * spanZ / 2);
            return true;
        }

        /// <summary>同步扫描当前视口。返回 null 表示引擎太旧。</summary>
        List<(StructureKind kind, int x, int z)> ScanCurrentView(List<StructureKind> kinds)
        {
            if (!ViewScanRect(out int x0, out int z0, out int x1, out int z1)) return null;

            int mc = _mc, dim = (int)_dim;
            long seed = _seed;
            double cx = _cx, cz = _cz;

            // 每种类型先尽量都取回来（上限远大于要显示的数量），再按「离视图中心近」各挑一份。
            // 如果直接在引擎里按条数截断，截断是按区域行序来的，标记会挤在窗口的上半边；
            // 如果所有类型混在一起挑最近的，又会被最密集的那类（试炼密室）独占屏幕。
            // 所以按类型给配额。
            int perKindHits = 4096;
            int quota = Math.Max(24, MarkCap / Math.Max(1, kinds.Count));

            var acc = new List<(StructureKind kind, int x, int z)>();
            var one = new List<(int x, int z, double d)>();
            foreach (var k in kinds)
            {
                // 要塞不走区域格：它按环形稀疏分布，必须走 ol_strongholds 的顺序迭代。
                // 那个导出会一次算完整个世界（约 1.6 秒），所以 CubiomesEngine 里
                // 按 (版本, 种子) 缓存了全部 128 个位置，这里过滤视口是纯内存操作。
                var pts = k.Key == "stronghold"
                    ? CubiomesEngine.ScanStrongholds(mc, (MapDimension)dim, seed, x0, z0, x1, z1, perKindHits)
                    : CubiomesEngine.ScanStructures(mc, k.Index, (MapDimension)dim,
                        seed, x0, z0, x1, z1, perKindHits);
                if (pts == null) return null;          // 旧引擎

                one.Clear();
                foreach (var p in pts)
                {
                    double dx = p.x - cx, dz = p.z - cz;
                    one.Add((p.x, p.z, dx * dx + dz * dz));
                }
                one.Sort((a, b) => a.d.CompareTo(b.d));
                int take = Math.Min(quota, one.Count);
                for (int i = 0; i < take; i++) acc.Add((k, one[i].x, one[i].z));
            }
            return acc;
        }

        /// <summary>
        /// 扫描当前视口并更新标记。
        /// 扫描期间视图又变了就再扫一次，保证最后画出来的和当前视口一致。
        /// </summary>
        async void RefreshStructures()
        {
            if (!CubiomesEngine.Available || !CubiomesEngine.SupportsStructureScan) { ClearMarks(); return; }

            var kinds = EnabledKinds();
            if (kinds.Count == 0) { ClearMarks(); return; }

            // 页还没布局（ActualWidth 是 1）时算出来的矩形只有几十个方块，
            // 扫出来必定是空的。等 SizeChanged 再扫，别白跑一趟。
            if (!ViewScanRect(out _, out _, out _, out _)) return;

            if (_structBusy) { _structDirty = true; return; }
            _structBusy = true;
            try
            {
                do
                {
                    _structDirty = false;
                    int epoch = ++_structEpoch;

                    var found = await Task.Run(() => ScanCurrentView(kinds));

                    if (epoch != _structEpoch) continue;           // 期间又变了，重来
                    if (found == null) { ClearMarks(); UpdateStructHint(); return; }
                    ApplyMarks(found);
                }
                while (_structDirty);
            }
            catch (Exception ex)
            {
                Log.Warn("刷新结构标记失败：" + ex.Message);
            }
            finally
            {
                _structBusy = false;
                UpdateStructHint();
            }
        }

        /// <summary>把扫描结果套到标记上：能复用的复用，多出来的删掉，新的补上。</summary>
        void ApplyMarks(List<(StructureKind kind, int x, int z)> found)
        {
            var reused = new Dictionary<(string key, int x, int z), StructMark>();
            foreach (var m in _marks) reused[(m.Kind.Key, m.X, m.Z)] = m;

            var next = new List<StructMark>(found.Count);
            foreach (var f in found)
            {
                var id = (f.kind.Key, f.x, f.z);
                if (reused.TryGetValue(id, out var old))
                {
                    reused.Remove(id);
                    next.Add(old);
                    continue;
                }
                if (next.Count >= MarkCap) continue;
                next.Add(CreateMark(f.kind, f.x, f.z));
            }

            // 没被复用的就是已经不在范围内的，收掉
            foreach (var dead in reused.Values)
            {
                if (_cardMark == dead) HideCard();
                MarkerCanvas.Children.Remove(dead.Ring);
                MarkerCanvas.Children.Remove(dead.Dot);
            }

            _marks.Clear();
            _marks.AddRange(next);
            LayoutMarks();
            UpdateStructHint();
        }

        StructMark CreateMark(StructureKind kind, int x, int z)
        {
            var color = Color.FromArgb(255,
                (byte)((kind.Color >> 16) & 0xFF), (byte)((kind.Color >> 8) & 0xFF), (byte)(kind.Color & 0xFF));

            // 外圈半透明光环 + 实心点：在任何底色上都能看清，又不遮住地形
            var ring = new Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = new SolidColorBrush(Color.FromArgb(64, color.R, color.G, color.B)),
                Stroke = new SolidColorBrush(Color.FromArgb(150, color.R, color.G, color.B)),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            var dot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false,
                ToolTip = null,
            };
            MarkerCanvas.Children.Add(ring);
            MarkerCanvas.Children.Add(dot);

            return new StructMark { Kind = kind, X = x, Z = z, Ring = ring, Dot = dot };
        }

        /// <summary>把标记摆到对应的屏幕位置。跟着 LayoutTiles 一起刷新，所以拖动缩放都跟手。</summary>
        void LayoutMarks()
        {
            if (MarkerCanvas == null) return;
            int vw = (int)MapHost.ActualWidth;
            int vh = (int)MapHost.ActualHeight;
            if (vw <= 0 || vh <= 0) return;

            double x0 = _cx - vw * _zoom / 2.0;
            double z0 = _cz - vh * _zoom / 2.0;

            foreach (var m in _marks)
            {
                double sx = (m.X - x0) / _zoom;
                double sz = (m.Z - z0) / _zoom;
                bool visible = sx > -24 && sx < vw + 24 && sz > -24 && sz < vh + 24;

                var vis = visible ? Visibility.Visible : Visibility.Collapsed;
                if (m.Dot.Visibility != vis) m.Dot.Visibility = vis;
                if (m.Ring.Visibility != vis) m.Ring.Visibility = vis;
                if (!visible) continue;

                Canvas.SetLeft(m.Dot, Math.Round(sx - 4));
                Canvas.SetTop(m.Dot, Math.Round(sz - 4));
                Canvas.SetLeft(m.Ring, Math.Round(sx - 8));
                Canvas.SetTop(m.Ring, Math.Round(sz - 8));
            }

            // 详情卡片跟着它指向的标记走，拖动时不会飘在错的地方
            if (_cardMark != null && StructCard.Visibility == Visibility.Visible)
                PlaceCard((_cardMark.X - x0) / _zoom, (_cardMark.Z - z0) / _zoom);
        }

        void ClearMarks()
        {
            foreach (var m in _marks)
            {
                MarkerCanvas.Children.Remove(m.Ring);
                MarkerCanvas.Children.Remove(m.Dot);
            }
            _marks.Clear();
            HideCard();
            UpdateStructHint();
        }

        /// <summary>屏幕位置命中哪个标记（取最近的，半径 10px）</summary>
        StructMark MarkAt(Point p)
        {
            int vw = (int)MapHost.ActualWidth;
            int vh = (int)MapHost.ActualHeight;
            if (vw <= 0 || vh <= 0) return null;

            double x0 = _cx - vw * _zoom / 2.0;
            double z0 = _cz - vh * _zoom / 2.0;

            StructMark best = null;
            double bestD = 10 * 10;
            foreach (var m in _marks)
            {
                double sx = (m.X - x0) / _zoom;
                double sz = (m.Z - z0) / _zoom;
                double dx = sx - p.X, dz = sz - p.Y;
                double d = dx * dx + dz * dz;
                if (d <= bestD) { bestD = d; best = m; }
            }
            return best;
        }

        // ---------- 结构详情卡片 ----------

        void ShowCard(StructMark mark)
        {
            if (mark == null) { HideCard(); return; }
            _cardMark = mark;

            var kind = mark.Kind;
            CardTitle.Text = kind.Name;
            CardSwatch.Background = new SolidColorBrush(Color.FromArgb(255,
                (byte)((kind.Color >> 16) & 0xFF), (byte)((kind.Color >> 8) & 0xFF), (byte)(kind.Color & 0xFF)));
            CardSub.Text = kind.IsNether ? "下界结构"
                          : kind.IsEnd ? "末地结构"
                          : "主世界结构";

            int dist = (int)Math.Round(Math.Sqrt((double)mark.X * mark.X + (double)mark.Z * mark.Z));
            int chunkX = mark.X >> 4, chunkZ = mark.Z >> 4;
            CardCoord.Text = $"X {mark.X}   Z {mark.Z}";
            CardMeta.Text = $"区块 {chunkX}, {chunkZ}（/tp {mark.X} ~ {mark.Z}）\n" +
                            $"距原点 {dist} 格\n" +
                            $"群系 {BiomeNameAt(mark.X, mark.Z)}";

            int vw = (int)MapHost.ActualWidth;
            int vh = (int)MapHost.ActualHeight;
            double sx = (mark.X - (_cx - vw * _zoom / 2.0)) / _zoom;
            double sz = (mark.Z - (_cz - vh * _zoom / 2.0)) / _zoom;
            StructCard.Visibility = Visibility.Visible;
            PlaceCard(sx, sz);
        }

        /// <summary>卡片摆在标记旁边，但不跑出视口</summary>
        void PlaceCard(double sx, double sz)
        {
            int vw = (int)MapHost.ActualWidth;
            int vh = (int)MapHost.ActualHeight;
            StructCard.Measure(new Size(248, double.PositiveInfinity));
            double cw = 248;
            double ch = StructCard.DesiredSize.Height > 0 ? StructCard.DesiredSize.Height : 170;

            double left = sx + 16;
            if (left + cw > vw - 8) left = sx - 16 - cw;
            if (left < 8) left = 8;
            double top = sz - ch / 2;
            if (top + ch > vh - 8) top = vh - 8 - ch;
            if (top < 8) top = 8;

            Canvas.SetLeft(StructCard, Math.Round(left));
            Canvas.SetTop(StructCard, Math.Round(top));
        }

        void HideCard()
        {
            _cardMark = null;
            if (StructCard != null) StructCard.Visibility = Visibility.Collapsed;
        }

        void CardClose_Click(object sender, RoutedEventArgs e) => HideCard();

        /// <summary>结构详情卡的「复制指令」：复制可直接粘进聊天框的传送指令。</summary>
        void CardCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_cardMark == null) return;
            try { Clipboard.SetText($"/tp {_cardMark.X} ~ {_cardMark.Z}"); }
            catch (Exception ex) { Log.Warn("复制指令失败：" + ex.Message); }
        }

        /// <summary>取某个世界坐标的群系名。只有算过的瓦片才有数据，否则如实说还没算。</summary>
        string BiomeNameAt(int bx, int bz)
        {
            int level = Level;
            foreach (var t in _tiles.Values)
            {
                if (t.Level != level) continue;
                double world = (double)TilePx * t.Level;
                double ox = t.Tx * world, oz = t.Tz * world;
                if (bx < ox || bx >= ox + world) continue;
                if (bz < oz || bz >= oz + world) continue;

                int px = (int)((bx - ox) / t.Level);
                int pz = (int)((bz - oz) / t.Level);
                if (px < 0 || px >= TilePx || pz < 0 || pz >= TilePx) break;

                int id = t.BiomeIds[pz * TilePx + px];
                // 已知群系后面附上 ID 方便和游戏核对；Name() 对未知 ID 已经带上编号，不再重复
                var name = BiomeColors.Name(id);
                return name.IndexOf('#') >= 0 ? name : name + $" (#{id})";
            }
            return "这块还没算出来";
        }

        // ---------- 交互 ----------

        void OnDown(object sender, MouseButtonEventArgs e)
        {
            // 用 MapHost（没有变换）取坐标：拿被缩放/平移过的元素坐标会形成反馈，抖动
            var pos = e.GetPosition(MapHost);
            _dragFrom = pos;
            _downPos = pos;
            _pendingMark = MarkAt(pos);
            _dragStartCx = _cx;
            _dragStartCz = _cz;
            MapHost.CaptureMouse();
        }

        void OnMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(MapHost);
            if (_dragFrom == null) { UpdateReadout(pos); return; }

            var d = pos - _dragFrom.Value;
            _cx = _dragStartCx - d.X * _zoom;
            _cz = _dragStartCz - d.Y * _zoom;
            ClampCenter();

            LayoutTiles();   // 已经算好的瓦片跟着鼠标走（只改坐标，不算地形）
            UpdateTiles();   // 新露出来的空白排队去算，算好一块淡入一块
            UpdateReadout(pos);
        }

        void OnUp(object sender, MouseButtonEventArgs e)
        {
            MapHost.ReleaseMouseCapture();
            if (_dragFrom == null) return;

            // 没拖动就是一次点击：命中标记就弹详情，否则把卡片收起来
            var moved = e.GetPosition(MapHost) - _downPos;
            bool click = Math.Abs(moved.X) < 4 && Math.Abs(moved.Y) < 4;
            var hit = _pendingMark;
            _pendingMark = null;

            EndDrag();

            if (!click) return;
            if (hit != null) ShowCard(hit);
            else HideCard();
        }

        void EndDrag()
        {
            _dragFrom = null;
            LayoutTiles();
            UpdateTiles();
            RefreshStructures();
        }

        void ClampCenter()
        {
            if (_cx > WorldLimit) _cx = WorldLimit; else if (_cx < -WorldLimit) _cx = -WorldLimit;
            if (_cz > WorldLimit) _cz = WorldLimit; else if (_cz < -WorldLimit) _cz = -WorldLimit;
        }

        void UpdateReadout(Point pos)
        {
            int vw = (int)MapHost.ActualWidth;
            int vh = (int)MapHost.ActualHeight;
            if (vw <= 0 || vh <= 0) return;

            var bx = (int)(_cx + (pos.X - vw / 2.0) * _zoom);
            var bz = (int)(_cz + (pos.Y - vh / 2.0) * _zoom);
            PosText.Text = $"X {bx}   Z {bz}";
            // 瓦片还没算好（或者鼠标落在旧倍率的占位瓦片上）时如实说明，别报群系名
            var biome = BiomeNameAt(bx, bz);
            BiomeText.Text = biome == "这块还没算出来" ? "加载中…" : biome;
        }

        // ---------- 按钮 ----------

        void Render_Click(object sender, RoutedEventArgs e) { ApplySeed(); Render(); }

        void Home_Click(object sender, RoutedEventArgs e) => JumpTo(0, 0);

        // ---------- 前往坐标 ----------

        void Goto_Click(object sender, RoutedEventArgs e) => JumpToTyped();

        void GotoBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            JumpToTyped();          // 回车等同于点「前往」，省一次点击
        }

        /// <summary>
        /// 解析输入框里的坐标并跳转。
        ///
        /// 只认数字、不管分隔符，所以下面这些都能用（方便直接贴 F3 或 /tp 命令）：
        /// · "100 200"     → X=100, Z=200
        /// · "100,200"     → X=100, Z=200
        /// · "100 64 200"  → X=100, Z=200（中间的 Y 忽略）
        /// · "/tp @s 100 64 200"
        /// </summary>
        void JumpToTyped()
        {
            if (GotoBox == null) return;

            var nums = ParseInts(GotoBox.Text);
            if (nums.Count < 2)
            {
                // 没有单独的错误提示控件，就借用状态条上的群系位显示一行提示
                BiomeText.Text = "坐标至少要填 X 和 Z 两个数字";
                GotoBox.SelectAll();
                GotoBox.Focus();
                return;
            }

            int x = nums[0];
            int z = nums.Count >= 3 ? nums[2] : nums[1];   // 三个数时中间那个是 Y，跳过
            JumpTo(x, z);
        }

        /// <summary>抓出文本里所有整数（负数带符号）。int 放不下的大数直接丢弃。</summary>
        static List<int> ParseInts(string text)
        {
            var list = new List<int>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(text, @"-?\d+"))
                if (int.TryParse(m.Value, out int v)) list.Add(v);
            return list;
        }

        /// <summary>
        /// 把视图中心移到指定方块坐标。
        ///
        /// 故意**不清空**已算好的瓦片：拖出视口的那批会被 TrimTiles 自然淘汰，
        /// 近距离跳转还能直接复用，本质上和拖动一下是一回事，所以是瞬时的。
        /// </summary>
        void JumpTo(int x, int z)
        {
            _cx = x;
            _cz = z;
            ClampCenter();          // 超出世界边界的输入夹到 ±30000000

            // 在目标点画个图钉，否则「跳过去」之后看不出跳到哪了
            _jumpX = (int)_cx;
            _jumpZ = (int)_cz;
            if (JumpMark != null) JumpMark.Visibility = Visibility.Visible;

            HideCard();
            LayoutTiles();
            UpdateTiles();
            RefreshStructures();

            // 跳过去时鼠标多半不在图上，先按目标点填一次读数，给个明确反馈
            PosText.Text = $"X {_cx:0}   Z {_cz:0}";
            var biome = BiomeNameAt((int)_cx, (int)_cz);
            BiomeText.Text = biome == "这块还没算出来" ? "加载中…" : biome;
        }

        /// <summary>
        /// 摆放跳转标记。只做平移、不做缩放——所以地图放大缩小时图钉大小不变，
        /// 尖端始终指着目标点。跟着 LayoutTiles 一起刷新，拖动缩放都跟手。
        /// </summary>
        void LayoutJumpMark()
        {
            if (JumpMark == null) return;
            if (_jumpX == null || _jumpZ == null)
            {
                JumpMark.Visibility = Visibility.Collapsed;
                return;
            }

            int vw = (int)MapHost.ActualWidth;
            int vh = (int)MapHost.ActualHeight;
            if (vw <= 0 || vh <= 0) return;

            double x0 = _cx - vw * _zoom / 2.0;
            double z0 = _cz - vh * _zoom / 2.0;
            double sx = (_jumpX.Value - x0) / _zoom;
            double sz = (_jumpZ.Value - z0) / _zoom;

            // 视口外就收起来（留点余量，边缘卡半截比突然消失好）
            bool visible = sx > -60 && sx < vw + 60 && sz > -60 && sz < vh + 60;
            var vis = visible ? Visibility.Visible : Visibility.Collapsed;
            if (JumpMark.Visibility != vis) JumpMark.Visibility = vis;
            if (!visible) return;

            Canvas.SetLeft(JumpMark, Math.Round(sx));
            Canvas.SetTop(JumpMark, Math.Round(sz));
        }

#if DEBUG
        /// <summary>诊断：回到原点并重算</summary>
        public void ResetView() { _cx = 0; _cz = 0; Render(); }

        /// <summary>诊断：直接把倍率设成某个值（跳过缓动），用于量测</summary>
        public void ProbeSetZoom(double zoom)
        {
            StopZoomAnim();
            _zoom = _zoomTarget = Math.Max(ZoomMin, Math.Min(ZoomMax, zoom));
            LayoutTiles();
            UpdateTiles();
            UpdateZoomText();
        }

        /// <summary>
        /// 诊断：模拟连续拖动，报告每一步需要多少瓦片、其中多少已经有了。
        /// 只走几何部分，不触发地形计算。
        /// </summary>
        public string ProbeDrag(int steps, double dxPerStep, double dyPerStep)
        {
            var sb = new System.Text.StringBuilder();
            int vw = (int)MapHost.ActualWidth, vh = (int)MapHost.ActualHeight;
            sb.AppendLine($"viewport={vw}x{vh} zoom=1:{_zoom:0.##} level={Level} tile={TilePx}px");
            sb.AppendLine($"tiles={_tiles.Count} pending={_pending.Count} renderCount={RenderCount} lastTileMs={LastRenderMs:0.0}");

            for (int i = 1; i <= steps; i++)
            {
                int before = _tiles.Count;
                _cx -= dxPerStep * _zoom;
                _cz -= dyPerStep * _zoom;
                ClampCenter();
                LayoutTiles();

                TileRange(1, out int x0, out int z0, out int x1, out int z1);
                int need = (x1 - x0 + 1) * (z1 - z0 + 1);
                if (i <= 3 || _tiles.Count != before)
                    sb.AppendLine($"step {i,2}: cx={_cx,10:0} cz={_cz,10:0} 需要={need} 已有={_tiles.Count}（+{_tiles.Count - before}）");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 诊断：模拟拖动的每一帧，只做瓦片重新布局，测量每帧耗时。
        /// 这是判断「拖动跟不跟手」的直接指标——应该远小于 16ms。
        /// </summary>
        public string ProbeDragLive(int frames, double dPerFrame)
        {
            var sb = new System.Text.StringBuilder();
            int before = RenderCount;
            double savedCx = _cx, savedCz = _cz;

            double worst = 0, sum = 0;
            for (int i = 1; i <= frames; i++)
            {
                _cx = savedCx - i * dPerFrame * _zoom;
                var f = System.Diagnostics.Stopwatch.StartNew();
                LayoutTiles();
                f.Stop();
                sum += f.Elapsed.TotalMilliseconds;
                if (f.Elapsed.TotalMilliseconds > worst) worst = f.Elapsed.TotalMilliseconds;
            }
            _cx = savedCx;
            _cz = savedCz;
            LayoutTiles();

            sb.AppendLine($"frames={frames} totalMs={sum:0.0} avgMs={sum / frames:0.00} worstMs={worst:0.00}");
            sb.AppendLine($"renderCount before={before} after={RenderCount}（拖动中应该完全不增加）");
            return sb.ToString();
        }

        /// <summary>
        /// 诊断：模拟一整段连续缩放（不排队算瓦片），报告每帧布局耗时、
        /// 以及过程中出现的分级序列和最终外观倍率。
        /// </summary>
        public string ProbeZoom(double from, double to, int frames)
        {
            var sb = new System.Text.StringBuilder();
            StopZoomAnim();
            _zoom = Math.Max(ZoomMin, Math.Min(ZoomMax, from));
            LayoutTiles();

            var levels = new List<int>();
            double worst = 0, sum = 0;
            double start = Math.Log(_zoom), end = Math.Log(Math.Max(ZoomMin, Math.Min(ZoomMax, to)));

            for (int i = 1; i <= frames; i++)
            {
                double t = (double)i / frames;
                _zoom = Math.Exp(start + (end - start) * t);

                var f = System.Diagnostics.Stopwatch.StartNew();
                LayoutTiles();
                f.Stop();
                sum += f.Elapsed.TotalMilliseconds;
                if (f.Elapsed.TotalMilliseconds > worst) worst = f.Elapsed.TotalMilliseconds;

                int lv = Level;
                if (levels.Count == 0 || levels[levels.Count - 1] != lv) levels.Add(lv);
            }

            sb.AppendLine($"zoom {from:0.##} -> {_zoom:0.##}  frames={frames} avgMs={sum / frames:0.00} worstMs={worst:0.00}");
            sb.AppendLine("分级序列: " + string.Join(" → ", levels));
            sb.AppendLine($"当前分级={Level} 瓦片在屏上的边长≈{TilePx * Level / _zoom:0.#}px（1~2 倍拉伸）");
            return sb.ToString();
        }

        /// <summary>
        /// 诊断：在 4~256 之间扫一遍倍率，检查同一分级下相邻瓦片的
        /// 「右边缘」和「下一块的左边缘」是否完全相等。
        /// 只要出现 ±1 像素的差，屏幕上就是一条缝。这是判断缩放稳不稳的直接指标。
        /// </summary>
        public string ProbeSeams(int samples)
        {
            var sb = new System.Text.StringBuilder();
            int vw = Math.Max(64, (int)MapHost.ActualWidth);
            int vh = Math.Max(64, (int)MapHost.ActualHeight);
            double saved = _zoom;

            int badEdges = 0, badRows = 0, checkedEdges = 0;
            int worst = 0;
            double worstAt = 0, lo = double.MaxValue, hi = 0;
            string firstBad = null;

            for (int i = 0; i < samples; i++)
            {
                double t = samples <= 1 ? 0 : (double)i / (samples - 1);
                _zoom = Math.Exp(Math.Log(ZoomMin) + (Math.Log(ZoomMax) - Math.Log(ZoomMin)) * t);

                int level = Level;
                double world = (double)TilePx * level;
                double originX = _cx - vw * _zoom / 2.0;
                double originZ = _cz - vh * _zoom / 2.0;

                int prevX1 = int.MinValue, prevZ1 = int.MinValue;
                for (int tx = -3; tx <= 3; tx++)
                {
                    int x0 = (int)Math.Round((tx * world - originX) / _zoom);
                    int x1 = (int)Math.Round(((tx + 1) * world - originX) / _zoom);
                    if (prevX1 != int.MinValue)
                    {
                        checkedEdges++;
                        int gap = x0 - prevX1;
                        if (gap != 0)
                        {
                            badEdges++;
                            if (Math.Abs(gap) > worst) { worst = Math.Abs(gap); worstAt = _zoom; }
                            if (firstBad == null) firstBad = $"zoom={_zoom:0.###} level={level} 横向缝隙={gap}px";
                        }
                    }
                    prevX1 = x1;
                }

                for (int tz = -3; tz <= 3; tz++)
                {
                    int z0 = (int)Math.Round((tz * world - originZ) / _zoom);
                    int z1 = (int)Math.Round(((tz + 1) * world - originZ) / _zoom);
                    if (prevZ1 != int.MinValue)
                    {
                        checkedEdges++;
                        int gap = z0 - prevZ1;
                        if (gap != 0)
                        {
                            badRows++;
                            if (Math.Abs(gap) > worst) { worst = Math.Abs(gap); worstAt = _zoom; }
                            if (firstBad == null) firstBad = $"zoom={_zoom:0.###} level={level} 纵向缝隙={gap}px";
                        }
                    }
                    prevZ1 = z1;
                }

                if (_zoom < lo) lo = _zoom;
                if (_zoom > hi) hi = _zoom;
            }

            _zoom = saved;
            _zoomTarget = saved;
            LayoutTiles();
            UpdateZoomText();

            sb.AppendLine($"扫描 {samples} 个倍率（{lo:0.##} ~ {hi:0.##}）共检查 {checkedEdges} 条相邻边");
            sb.AppendLine($"缝隙边数={badEdges + badRows}（横向 {badEdges} / 纵向 {badRows}）最大缝隙={worst}px" +
                          (worst > 0 ? $" @ 1:{worstAt:0.##}" : ""));
            if (firstBad != null) sb.AppendLine("首个问题: " + firstBad);
            else sb.AppendLine("结论：所有倍率下相邻瓦片边缘完全重合，不会露缝");
            return sb.ToString();
        }

        /// <summary>
        /// 诊断：报告结构标记的情况，并给出第一个可见标记的屏幕坐标
        /// （探针用它去点一下，验证详情卡片）。
        /// </summary>
        public string ProbeStructures()
        {
            // 先同步扫一次：异步那次可能还在跑，报告应当反映此刻的视口
            var kinds = EnabledKinds();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var fresh = kinds.Count == 0 ? null : ScanCurrentView(kinds);
            sw.Stop();
            if (fresh != null) ApplyMarks(fresh);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"同步扫描 {kinds.Count} 类耗时 {sw.ElapsedMilliseconds}ms");
            sb.AppendLine($"引擎支持结构扫描={CubiomesEngine.SupportsStructureScan}");
            sb.AppendLine($"维度={_dim} 勾选种类={_enabledCount()} 标记数={_marks.Count}");

            var byKind = new Dictionary<string, int>();
            foreach (var m in _marks)
            {
                byKind.TryGetValue(m.Kind.Name, out int c);
                byKind[m.Kind.Name] = c + 1;
            }
            var parts = new List<string>();
            foreach (var kv in byKind) parts.Add($"{kv.Key}×{kv.Value}");
            sb.AppendLine("分布: " + (parts.Count == 0 ? "(无)" : string.Join(" ", parts)));

            int vw = (int)MapHost.ActualWidth, vh = (int)MapHost.ActualHeight;
            double x0 = _cx - vw * _zoom / 2.0;
            double z0 = _cz - vh * _zoom / 2.0;
            int listed = 0;
            foreach (var m in _marks)
            {
                int sx = (int)Math.Round((m.X - x0) / _zoom);
                int sz = (int)Math.Round((m.Z - z0) / _zoom);
                if (sx < 20 || sx > vw - 20 || sz < 20 || sz > vh - 20) continue;
                sb.AppendLine($"可见标记: {m.Kind.Name} 世界({m.X},{m.Z}) 屏幕({sx},{sz})");
                if (++listed >= 6) break;
            }
            if (listed == 0) sb.AppendLine("可见标记: (无)");
            return sb.ToString();
        }

        int _enabledCount()
        {
            int n = 0;
            foreach (var k in _structKinds) if (_structOn.Contains(k.Key)) n++;
            return n;
        }

        /// <summary>诊断：绕过异步流程，同步直扫原点附近，确认引擎路径本身没问题</summary>
        public string ProbeScanRaw()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"mc={_mc} dim={(int)_dim} seed={_seed} zoom={_zoom:0.##} cx={_cx:0} cz={_cz:0}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var k in _structKinds)
            {
                if (!_structOn.Contains(k.Key)) continue;
                var t0 = sw.ElapsedMilliseconds;
                var pts = CubiomesEngine.ScanStructures(_mc, k.Index, _dim, _seed, -2048, -2048, 2048, 2048, 200);
                long ms = sw.ElapsedMilliseconds - t0;
                sb.AppendLine($"  {k.Name} idx={k.Index} -> {(pts == null ? "null(旧引擎)" : pts.Count + " 个 " + ms + "ms")}");
            }
            sb.AppendLine("合计 " + sw.ElapsedMilliseconds + "ms");
            return sb.ToString();
        }

        /// <summary>
        /// 诊断：把视图中心移到指定坐标后同步扫描，报告命中数。
        /// 用来确认「离原点很远时结构标记仍然存在」。
        /// </summary>
        public string ProbeStructAt(int centerX, int centerZ)
        {
            _cx = centerX; _cz = centerZ;
            ClampCenter();
            var kinds = EnabledKinds();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var fresh = kinds.Count == 0 ? null : ScanCurrentView(kinds);
            sw.Stop();
            if (fresh != null) ApplyMarks(fresh);
            LayoutTiles();

            string head = $"中心({centerX},{centerZ}) 扫描 {kinds.Count} 类 {sw.ElapsedMilliseconds}ms";
            if (fresh == null) return head + " → 引擎不支持结构扫描";

            var byKind = new Dictionary<string, int>();
            foreach (var m in _marks)
            {
                byKind.TryGetValue(m.Kind.Name, out int c);
                byKind[m.Kind.Name] = c + 1;
            }
            var parts = new List<string>();
            foreach (var kv in byKind) parts.Add($"{kv.Key}×{kv.Value}");
            return head + $" → 标记 {_marks.Count} 个: " + (parts.Count == 0 ? "(无)" : string.Join(" ", parts));
        }

        /// <summary>诊断：把第一个可见标记当成点击，直接弹卡片（不依赖真实鼠标）</summary>
        public string ProbeClickMark()
        {
            int vw = (int)MapHost.ActualWidth, vh = (int)MapHost.ActualHeight;
            double x0 = _cx - vw * _zoom / 2.0;
            double z0 = _cz - vh * _zoom / 2.0;
            foreach (var m in _marks)
            {
                int sx = (int)Math.Round((m.X - x0) / _zoom);
                int sz = (int)Math.Round((m.Z - z0) / _zoom);
                if (sx < 40 || sx > vw - 40 || sz < 40 || sz > vh - 40) continue;

                var hit = MarkAt(new Point(sx, sz));
                ShowCard(hit);
                return hit == null
                    ? $"MarkAt 未命中 屏幕({sx},{sz})"
                    : $"命中 {hit.Kind.Name} 世界({hit.X},{hit.Z}) 卡片可见={StructCard.Visibility} 标题={CardTitle.Text}";
            }
            return "没有可见标记，无法测试点击";
        }

        /// <summary>
        /// 诊断：验证「滚结构列表时左侧面板不会跟着滚」。
        ///
        /// 用 mouse_event 产生**真实**滚轮输入（进系统队列，走完整 WPF 输入管线），并且
        /// 必须异步——两次滚轮之间要让出 UI 线程，消息才会被处理。
        /// 不能用 SendMessage 发给自己的窗口：UI 线程等消息泵 = 死锁。
        ///
        /// 「左栏有没有滚」用 SeedBox 相对本控件的位置判定：它不在滚动区里，只要位置变了
        /// 就说明左栏整体动了。
        /// </summary>
        public async Task<string> ProbeScrollConflictAsync()
        {
            if (StructScroll == null) return "控件缺失";

            var sb = new System.Text.StringBuilder();
            void Sb(string s) => sb.AppendLine(s);

            var win = Window.GetWindow(this);
            if (win == null) return "窗口还没就绪";
            var hwnd = new WindowInteropHelper(win).Handle;
            if (hwnd == IntPtr.Zero) return "窗口句柄无效";

            double SeedTop()
            {
                try { return SeedBox.TransformToAncestor(this).Transform(new Point(0, 0)).Y; }
                catch { return double.NaN; }
            }

            Sb($"列表可滚高度={StructScroll.ScrollableHeight:0.#} 当前={StructScroll.VerticalOffset:0.#}");
            Sb($"面板高={SidePanel.ActualHeight:0.#} Grid高={SideGrid.ActualHeight:0.#} 列表高={StructScroll.ActualHeight:0.#} 列表内容高={StructScroll.ExtentHeight:0.#}");
            var sizes = new List<string>();
            foreach (var rd in SideGrid.RowDefinitions) sizes.Add(rd.ActualHeight.ToString("0.#"));
            Sb("各行实际高: " + string.Join(" / ", sizes));

            if (StructScroll.ScrollableHeight <= 0)
                return sb.Append("列表内容没有超出高度，无法测试（先把窗口调矮一点或减少勾选）").ToString();

            var screen = StructScroll.PointToScreen(
                new Point(StructScroll.ActualWidth / 2, StructScroll.ActualHeight / 2));
            Sb($"列表屏幕矩形: ({screen.X:0},{screen.Y:0}) 宽{StructScroll.ActualWidth:0.#} 高{StructScroll.ActualHeight:0.#}");
            SetCursorPos((int)screen.X, (int)screen.Y);
            SetForegroundWindow(hwnd);
            await Task.Delay(120);
            Mouse.Synchronize();
            win.UpdateLayout();
            Sb($"鼠标移到列表中心 ({screen.X:0},{screen.Y:0}) 在列表上={StructScroll.IsMouseOver}");

            StructScroll.ScrollToVerticalOffset(0);
            win.UpdateLayout();

            double seedBefore = SeedTop();
            double structBefore = StructScroll.VerticalOffset;

            for (int i = 0; i < 3; i++)
            {
                mouse_event(0x0800, 0, 0, -120, UIntPtr.Zero);   // MOUSEEVENTF_WHEEL
                await Task.Delay(80);
                win.UpdateLayout();
                Sb($"  第 {i + 1} 格后：列表={StructScroll.VerticalOffset:0.#} 左栏SeedY={SeedTop():0.#} 在列表上={StructScroll.IsMouseOver}");
            }

            double seedAfter = SeedTop();
            double structAfter = StructScroll.VerticalOffset;

            Sb($"合计：列表 {structBefore:0.#} → {structAfter:0.#}（变化 {structAfter - structBefore:0.#}），" +
               $"左栏位移 {seedAfter - seedBefore:0.#}");
            Sb(structAfter > structBefore && Math.Abs(seedAfter - seedBefore) < 0.5
                ? "结论：只有列表滚了，左栏没动 —— 冲突已消除"
                : structAfter <= structBefore
                    ? "结论：列表没滚（鼠标可能没落在列表上，或被别的窗口抢了焦点）"
                    : "结论：左栏跟着动了 —— 未修好");
            return sb.ToString();
        }

        /// <summary>
        /// 诊断：走一遍「前往坐标」的输入解析与跳转，报告标记位置是否落在视口中心。
        /// </summary>
        public string ProbeGoto()
        {
            var sb = new System.Text.StringBuilder();
            if (GotoBox == null || JumpMark == null) return "控件缺失";

            // 解析：几种常见写法都应该得到同一组坐标
            foreach (var text in new[] { "100 200", "100,200", "100 64 200", "/tp @s 100 64 200", "-1234 5678" })
            {
                var nums = ParseInts(text);
                string got = nums.Count < 2
                    ? "(解析失败)"
                    : $"{nums[0]} , {(nums.Count >= 3 ? nums[2] : nums[1])}";
                sb.AppendLine($"  \"{text}\" → {got}");
            }

            int vw = (int)MapHost.ActualWidth, vh = (int)MapHost.ActualHeight;
            GotoBox.Text = "12345 -6789";
            JumpToTyped();
            // 标记刚从 Collapsed 变 Visible，不跑一次布局它的 ActualWidth 还是 0
            JumpMark.UpdateLayout();
            MapHost.UpdateLayout();
            sb.AppendLine($"跳转后中心=({_cx:0},{_cz:0}) 视口={vw}×{vh}");
            sb.AppendLine($"标记可见={JumpMark.Visibility} 尺寸={JumpMark.RenderSize.Width:0}×{JumpMark.RenderSize.Height:0}");
            double left = Canvas.GetLeft(JumpMark), top = Canvas.GetTop(JumpMark);
            sb.AppendLine($"标记屏幕位置=({left:0},{top:0}) 视口中心应为 ({vw / 2.0:0},{vh / 2.0:0})");

            // JumpMark 是 0×0 的容器，GetDescendantBounds 对它返回空；
            // 改成直接量第一个子元素（转 45° 的尖角方块）在 MapHost 里的包围盒。
            // 尖角中心应在 (视口中心.x, 视口中心.y-8.5)，对角线半长 8.49 → 底边贴 0。
            string geom = "(无子元素)";
            bool tipOnTarget = false;
            if (JumpMark.Children.Count > 0 && JumpMark.Children[0] is FrameworkElement tip)
            {
                try
                {
                    var p0 = tip.TransformToAncestor(MapHost).Transform(new Point(0, 0));
                    var p1 = tip.TransformToAncestor(MapHost).Transform(new Point(tip.ActualWidth, tip.ActualHeight));
                    double cxp = (p0.X + p1.X) / 2, cyp = (p0.Y + p1.Y) / 2;
                    geom = $"尖角元素 {tip.ActualWidth:0}×{tip.ActualHeight:0}，" +
                           $"包围盒 x=[{p0.X:0.#},{p1.X:0.#}] y=[{p0.Y:0.#},{p1.Y:0.#}] 中心=({cxp:0.#},{cyp:0.#})";
                    // 转 45° 后方块包围盒是 16.97 见方，中心距离视口中心 8.5，底边就在视口中心
                    tipOnTarget = Math.Abs(cxp - vw / 2.0) <= 1.5
                                  && Math.Abs(cyp - (vh / 2.0 - 8.5)) <= 1.5;
                }
                catch (Exception ex) { geom = "量不出来：" + ex.Message; }
            }
            sb.AppendLine(geom);

            bool centered = Math.Abs(left - vw / 2.0) <= 1 && Math.Abs(top - vh / 2.0) <= 1;
            sb.Append(centered && tipOnTarget && JumpMark.Visibility == Visibility.Visible
                ? "结论：尖端落在视口中心、图钉朝上 —— 正常"
                : "结论：位置或形状不对");
            return sb.ToString();
        }

        /// <summary>
        /// 诊断：确认引擎是「从程序内置资源解出来的」，而不是从网上下载的。
        /// 报告资源是否存在、磁盘哈希是否与 Branding.EngineSha256 一致、能否加载。
        /// </summary>
        public string ProbeEngineBundle()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"程序内置引擎={BundledEngine.Exists}");
            sb.AppendLine($"目标路径={CubiomesEngine.DllPath}");
            sb.AppendLine($"文件存在={File.Exists(CubiomesEngine.DllPath)}");

            if (File.Exists(CubiomesEngine.DllPath))
            {
                var actual = Core.Util.Sha256File(CubiomesEngine.DllPath);
                bool match = string.Equals(actual, Branding.EngineSha256, StringComparison.OrdinalIgnoreCase);
                sb.AppendLine($"磁盘 SHA-256={actual}");
                sb.AppendLine($"期望 SHA-256={Branding.EngineSha256}  一致={match}");
            }

            sb.AppendLine($"引擎已加载={CubiomesEngine.Available} 最新版本枚举={CubiomesEngine.NewestVersion}");
            sb.AppendLine($"结构扫描={CubiomesEngine.SupportsStructureScan} 要塞迭代={CubiomesEngine.SupportsStrongholds}");
            sb.AppendLine($"记录的版本={EngineProvisioner.InstalledVersion}");

            bool ok = BundledEngine.Exists && CubiomesEngine.Available;
            sb.Append(ok
                ? "结论：引擎来自程序内置资源，无需联网 —— 正常"
                : "结论：内置引擎不可用");
            return sb.ToString();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetCursorPos(int x, int y);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
#endif

        void OpenTools_Click(object sender, RoutedEventArgs e) => Util.OpenFolder(Paths.Tools);

        async void Explain_Click(object sender, RoutedEventArgs e)
        {
            await Dialogs.Long("种子地图是怎么算出来的",
                "Minecraft 的地形完全由种子决定，中间没有随机数参与，所以同一个种子在任何机器上生成的世界都一样。\n\n" +
                "一个只算「哪里是什么群系」的库不需要真的生成方块——它只要复现游戏的噪声函数和结构放置规则，" +
                "这比开一次游戏快上千倍。cubiomes 就是这样的库：MIT 许可、纯 C、和游戏逐位一致。\n\n" +
                "本启动器不自己重写这套算法。重写就意味着要和每个版本的地形改动同步对拍，迟早会对不上；" +
                "所以算法交给 cubiomes，我们只做界面、坐标换算和配色。\n\n" +
                "地图被切成一块块小方格分别计算：拖动和缩放都只是移动/缩放已经算好的格子，" +
                "新露出来的区域先空着，谁算完谁淡入——所以操作永远是跟手的。\n\n" +
                "缩放是连续的：滚轮或按钮按比例改变倍率，并带一点缓动，所以不会一格一格跳。" +
                "瓦片数据按 2 的幂分级缓存，任意倍率下取最接近的那一级，最多放大两倍。\n\n" +
                "引擎来自社区维护的 cubiomes 分支（上游只更新到 1.21 Winter Drop，这个分支已经合入 26.3 的地形数据和新的群系）。" +
                "引擎缺失或版本不认识时，这一页会直接告诉你，而不是画一张看起来对、其实错的地图。");
        }
    }
}
