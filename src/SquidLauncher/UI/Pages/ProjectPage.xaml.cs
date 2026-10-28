using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SquidLauncher.Content;
using SquidLauncher.Core;
using SquidLauncher.Game;

namespace SquidLauncher.UI.Pages
{
    public class FileRow
    {
        public ContentFile File { get; set; }
        /// <summary>这个文件已经装在目标版本里了</summary>
        public bool IsInstalled { get; set; }
        public string DisplayName => string.IsNullOrEmpty(File.DisplayName) ? File.FileName : File.DisplayName;
        public string ReleaseTypeText => File.ReleaseTypeText;
        public DateTime Published => File.Published;
        public string GameVersionsText
        {
            get
            {
                var v = File.GameVersions ?? new List<string>();
                return v.Count == 0 ? "未标注版本" : v.Count <= 4 ? string.Join(", ", v) : $"{v.First()} ~ {v.Last()}（{v.Count} 个版本）";
            }
        }
        public string LoadersText => File.Loaders == null || File.Loaders.Count == 0 ? "通用" : string.Join(" / ", File.Loaders);
        public string SizeText => File.Size > 0 ? Util.FormatSize(File.Size) : "";
    }

    /// <summary>前置列表里的一行（可跳转到它的详情页）</summary>
    public class DepRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        void On([CallerMemberName] string p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        string _title, _note, _desc, _iconUrl;

        public ContentSource Source { get; set; }
        public string ProjectId { get; set; }
        public string Slug { get; set; }
        public string FileName { get; set; }

        public string Title
        {
            get => _title;
            set { _title = value; On(); }
        }
        /// <summary>附加说明（已安装 / 指定版本 / 需要手动找）</summary>
        public string Note
        {
            get => _note;
            set { _note = value; On(); On(nameof(ShowNote)); }
        }
        public bool ShowNote => !string.IsNullOrEmpty(_note);

        /// <summary>一小段简介（接口拿到项目信息后才有，没有就不显示这一行）</summary>
        public string Desc
        {
            get => _desc;
            set { _desc = value; On(); On(nameof(ShowDesc)); }
        }
        public bool ShowDesc => !string.IsNullOrEmpty(_desc);

        /// <summary>项目图标地址，交给 ui:RemoteImage 去下载（带磁盘缓存）</summary>
        public string IconUrl
        {
            get => _iconUrl;
            set { _iconUrl = value; On(); }
        }
    }

    public partial class ProjectPage : UserControl, IPage
    {
        readonly ContentProject _p;
        readonly ContentType _type;
        readonly Instance _target;
        readonly string _mc, _loader;
        List<ContentFile> _files;
        InstalledItem _installed;
        string _installedDir;
        FileRow _depRow;
        /// <summary>列表最上面那行（列表按发布时间倒序，也就是最该下的那个）；没点任何东西时按它的前置显示</summary>
        FileRow _autoRow;
        int _depSeq;
        bool _loaded;

        public ProjectPage(ContentProject p, ContentType type, Instance target, string mc, string loader)
        {
            InitializeComponent();
            _p = p; _type = type; _target = target;
            _mc = string.IsNullOrEmpty(mc) ? null : mc;
            _loader = string.IsNullOrEmpty(loader) ? null : loader;
            PTitle.Text = p.Title;
            PDesc.Text = p.Description;
            ApplyMetaTexts();
            TargetInfo.Text = type == ContentType.Modpack ? "整合包会安装成一个新版本" : target != null ? $"安装到 {target.Id}" : "未选择目标版本";
            OnlyMatch.Visibility = _mc == null && _loader == null ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// 标题下面那行（作者 · 资源站 · 下载量 · 更新时间 · 分类）。
        /// 作者可能拿不到（批量接口不返回），所以这里按"有什么拼什么"来，不留孤零零的「·」。
        /// </summary>
        void ApplyMetaTexts()
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(_p.Author)) parts.Add(_p.Author);
            if (!string.IsNullOrEmpty(_p.SourceText)) parts.Add(_p.SourceText);
            if (_p.Downloads > 0) parts.Add("下载 " + _p.DownloadsText);
            if (_p.Updated != DateTime.MinValue) parts.Add("更新于 " + _p.Updated.ToString("yyyy-MM-dd"));
            if (_p.Categories?.Count > 0) parts.Add(string.Join(" ", _p.Categories.Take(6)));
            PMeta.Text = string.Join(" · ", parts);
        }

        /// <summary>
        /// 从前置行打开时手里只有标题 / 图标 / 一句话简介，先把页面推出来不让人干等，
        /// 作者、下载量、更新时间这些细节在后台补上（补到就重画那行文字）。
        /// </summary>
        async Task FillMetaAsync()
        {
            try
            {
                var full = await ContentService.GetProjectAsync(_p.Source, _p.Id, CancellationToken.None);
                if (full == null) return;
                Dispatcher.Invoke(() =>
                {
                    if (!string.IsNullOrEmpty(full.Title)) { _p.Title = full.Title; PTitle.Text = full.Title; }
                    if (!string.IsNullOrEmpty(full.Description)) { _p.Description = full.Description; PDesc.Text = full.Description; }
                    if (!string.IsNullOrEmpty(full.Author)) _p.Author = full.Author;
                    if (full.Downloads > _p.Downloads) _p.Downloads = full.Downloads;
                    if (full.Updated > _p.Updated) _p.Updated = full.Updated;
                    if (full.Categories != null && full.Categories.Count > 0) _p.Categories = full.Categories;
                    ApplyMetaTexts();
                });
            }
            catch (Exception ex) { Log.Warn("补齐项目信息失败：" + ex.Message); }
        }

        public async void OnShown()
        {
            if (_loaded) return;
            _loaded = true;
            // 作者为空基本就是从"前置"那一行点进来的（搜索 / 列表页都自带作者）
            if (string.IsNullOrEmpty(_p.Author)) _ = FillMetaAsync();
            Icon.Source = await RemoteImage.LoadAsync(_p.IconUrl ?? "", 128);
            await LoadFilesAsync();
        }

        async Task LoadFilesAsync()
        {
            StatusPanel.Visibility = Visibility.Visible;
            StatusText.Text = "加载文件列表…";
            StatusBar.Visibility = Visibility.Visible;
            try
            {
                var prov = ContentService.Get(_p.Source);
                _files = await Task.Run(() => prov.GetFilesAsync(_p.Id, null, null, CancellationToken.None));
                RefreshInstalled();
                Apply();
            }
            catch (Exception ex)
            {
                StatusText.Text = "加载失败：" + ex.Message;
                StatusBar.Visibility = Visibility.Collapsed;
            }
        }

        void Apply()
        {
            if (_files == null) return;
            IEnumerable<ContentFile> q = _files;
            if (OnlyMatch.IsChecked == true && OnlyMatch.Visibility == Visibility.Visible)
            {
                if (_mc != null) q = q.Where(f => f.GameVersions == null || f.GameVersions.Count == 0 || f.GameVersions.Contains(_mc));
                if (_loader != null && (_type == ContentType.Mod || _type == ContentType.Modpack))
                    q = q.Where(f => f.Loaders == null || f.Loaders.Count == 0 || f.Loaders.Any(l => string.Equals(l, _loader, StringComparison.OrdinalIgnoreCase)));
            }
            var rows = q.Select(f => new FileRow { File = f, IsInstalled = IsInstalledFile(f) }).ToList();
            _autoRow = rows.FirstOrDefault();
            Files.ItemsSource = rows;
            FilesTitle.Text = $"选择要下载的文件（{rows.Count}）";
            if (rows.Count == 0)
            {
                StatusPanel.Visibility = Visibility.Visible;
                StatusBar.Visibility = Visibility.Collapsed;
                StatusText.Text = _files.Count == 0 ? "这个项目没有可下载的文件" : $"没有适配 {_mc} {_loader} 的文件，关掉右上角的筛选看看全部文件";
            }
            else StatusPanel.Visibility = Visibility.Collapsed;
            // 没选任何行也把最上面那行的前置摆出来（换筛选 / 装完刷新都会跟着变）
            _depRow = null;
            UpdateDependencies(Files.SelectedItem as FileRow);
        }

        /// <summary>重新读一遍目标版本里的安装记录（同一 Mod 装在另一资源站的也能靠 slug 对上）</summary>
        void RefreshInstalled()
        {
            try
            {
                _installedDir = InstalledContent.DirOf(_target, _type);
                _installed = InstalledContent.Find(_target, _type, _p.Source, _p.Id, _p.Slug);
            }
            catch (Exception ex)
            {
                Log.Warn("读取已安装记录失败：" + ex.Message);
                _installed = null;
                _installedDir = null;
            }
        }

        /// <summary>这个文件是不是就是目标版本里已经装着的那个</summary>
        bool IsInstalledFile(ContentFile f)
        {
            if (f == null) return false;
            // 1. 文件名对得上就是同一个文件（不管当初从哪个源装的）
            if (InstalledContent.FilePresent(_installedDir, f.FileName)) return true;
            if (_installed == null) return false;
            // 2. 索引里记的版本 ID / 文件名对得上
            if (!string.IsNullOrEmpty(_installed.FileId) && !string.IsNullOrEmpty(f.FileId)
                && string.Equals(_installed.FileId, f.FileId, StringComparison.OrdinalIgnoreCase)) return true;
            return !string.IsNullOrEmpty(_installed.FileName) && !string.IsNullOrEmpty(f.FileName)
                && string.Equals(_installed.FileName, f.FileName, StringComparison.OrdinalIgnoreCase);
        }

        void OnlyMatch_Click(object sender, RoutedEventArgs e) => Apply();
        void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Current.Back();
        void Web_Click(object sender, RoutedEventArgs e) { if (!string.IsNullOrEmpty(_p.WebUrl)) Util.OpenUrl(_p.WebUrl); }

        // ---------------- 前置 Mod ----------------

        void Files_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDependencies(Files.SelectedItem as FileRow);

        /// <summary>
        /// 把某个文件的必需前置列在文件列表上方（已安装的会标出来）。
        /// row 为空时用列表最上面那行——这样进详情页不点任何东西也能看到前置。
        /// </summary>
        async void UpdateDependencies(FileRow row)
        {
            if (_type != ContentType.Mod) { ShowDependencies(null, null); return; }
            if (row == null) row = _autoRow;
            if (row == null) { ShowDependencies(null, null); return; }
            if (ReferenceEquals(row, _depRow)) return;
            _depRow = row;
            var seq = ++_depSeq;

            var deps = row.File.Dependencies?.Where(d => d.Required).ToList();
            if (deps == null || deps.Count == 0) { ShowDependencies(null, row); return; }

            Dictionary<string, InstalledItem> map = null;
            try { map = InstalledContent.ScanLocal(_target, _type); }
            catch (Exception ex) { Log.Warn("读取已安装记录失败：" + ex.Message); }

            var items = deps.Select(d => new DepRow
            {
                Source = _p.Source,
                ProjectId = d.ProjectId,
                FileName = d.FileName,
            }).ToList();
            // 有声明的项目先占位「加载中…」——别把接口 ID 或 jar 名当名字闪一下
            foreach (var it in items)
            {
                it.Title = string.IsNullOrEmpty(it.ProjectId) ? FallbackTitle(it) : LoadingText;
                it.Note = DepNote(it, map);
            }
            ShowDependencies(items, row);

            // 再用项目 ID 把真实名字、图标、简介拿回来
            var ids = deps.Where(d => !string.IsNullOrEmpty(d.ProjectId)).Select(d => d.ProjectId).Distinct().ToList();
            if (ids.Count == 0) return;
            try
            {
                var projects = await ContentService.GetProjectsAsync(_p.Source, ids, CancellationToken.None);
                if (seq != _depSeq) return;
                foreach (var it in items)
                {
                    if (it.Title != LoadingText) continue;
                    var proj = projects.FirstOrDefault(x => x != null && string.Equals(x.Id, it.ProjectId, StringComparison.OrdinalIgnoreCase));
                    if (proj == null) { it.Title = FallbackTitle(it); continue; }
                    if (!string.IsNullOrEmpty(proj.Title)) it.Title = proj.Title;
                    it.Slug = proj.Slug;
                    it.IconUrl = proj.IconUrl;
                    it.Desc = ShortSummary(proj.Description);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("获取前置信息失败：" + ex.Message);
                foreach (var it in items) if (it.Title == LoadingText) it.Title = FallbackTitle(it);
            }
        }

        string DepNote(DepRow d, Dictionary<string, InstalledItem> map)
        {
            if (InstalledContent.FilePresent(_installedDir, d.FileName)) return "已安装";
            if (map != null && !string.IsNullOrEmpty(d.ProjectId)
                && map.ContainsKey(ContentIndex.Key(d.Source, d.ProjectId))) return "已安装";
            return string.IsNullOrEmpty(d.ProjectId) ? "需要手动找" : "";
        }

        const string LoadingText = "加载中…";

        /// <summary>拿不到项目信息时顶着用的名字：优先文件名，其次项目 ID</summary>
        static string FallbackTitle(DepRow it) =>
            !string.IsNullOrEmpty(it.FileName) ? it.FileName
            : !string.IsNullOrEmpty(it.ProjectId) ? it.ProjectId
            : "未知前置";

        /// <summary>简介压成一行、砍到一小段，够看一眼就行</summary>
        static string ShortSummary(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
            return t.Length <= 72 ? t : t.Substring(0, 72).TrimEnd(' ', ',', '.', '，', '。') + "…";
        }

        void ShowDependencies(List<DepRow> items, FileRow row)
        {
            if (items == null || items.Count == 0)
            {
                DepPanel.Visibility = Visibility.Collapsed;
                DepList.ItemsSource = null;
                DepHint.ToolTip = null;
                return;
            }
            DepList.ItemsSource = items;
            int installed = items.Count(i => i.Note == "已安装");
            DepTitle.Text = $"前置 Mod（{items.Count}）";
            // 前置是跟着文件走的：写清楚这一份对应哪个文件，鼠标悬停给出 jar 名
            var whose = row == null ? "" : "对应 " + row.DisplayName + " · ";
            DepHint.Text = whose + (installed > 0 ? $"其中 {installed} 个已安装" : "下载时可以一键补齐");
            DepHint.ToolTip = row == null ? null : row.File.FileName;
            DepPanel.Visibility = Visibility.Visible;
        }

        /// <summary>点前置那一行：打开它自己的详情页</summary>
        void DepRow_Click(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is DepRow row)) return;
            e.Handled = true;
            OpenDepAsync(row);
        }

        /// <summary>焦点在前置行上时，回车或空格也能打开</summary>
        void DepRow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Space) return;
            if (!((sender as FrameworkElement)?.DataContext is DepRow row)) return;
            e.Handled = true;
            OpenDepAsync(row);
        }

        void OpenDepAsync(DepRow row)
        {
            if (string.IsNullOrEmpty(row.ProjectId) && string.IsNullOrEmpty(row.Slug))
            {
                Dialogs.Toast("这个前置只给了文件名，资源站上没有对应项目", MainWindow.ToastKind.Error);
                return;
            }
            // 前置行上已经有标题 / 图标 / 一句话简介了，先拿它把页面推出来，别为了等接口把人晾在原地；
            // 作者、下载量、更新时间由 ProjectPage.FillMetaAsync 在后台补齐。
            var id = string.IsNullOrEmpty(row.ProjectId) ? row.Slug : row.ProjectId;
            var prov = new ContentProject
            {
                Source = row.Source,
                Id = id,
                Slug = row.Slug,
                Title = string.IsNullOrEmpty(row.Title) || row.Title == LoadingText ? id : row.Title,
                Description = row.Desc,
                IconUrl = row.IconUrl,
                Type = ContentType.Mod,
            };
            MainWindow.Current.Push(new ProjectPage(prov, ContentType.Mod, _target, _mc, _loader));
        }

        async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is FileRow row)) return;
            var file = row.File;

            if (_type == ContentType.Modpack)
            {
                var name = await Dialogs.Prompt("安装整合包", "版本名", Util.SafeFileName(_p.Title));
                if (string.IsNullOrWhiteSpace(name)) return;
                var repo = GameRepo.Current;
                TaskManager.Start($"安装整合包 {_p.Title}", async t =>
                {
                    var id = await ModpackImporter.InstallFromFileAsync(repo, file, name, t);
                    Settings.SelectedVersion = id;
                    TaskManager.OnUi(() => MainWindow.Current.LaunchPage.RefreshVersion());
                });
                MainWindow.Current.ToggleTasks(true);
                return;
            }

            if (_target == null) { await Dialogs.Alert("没有目标版本", "先在上一页的“安装到”里选一个版本（没有的话先去装一个）。\n只想把文件存到电脑上，就点下载右边的箭头用“另存为”。"); return; }

            string dir;
            if (_type == ContentType.DataPack)
            {
                var saves = Directory.Exists(_target.SavesDir) ? Directory.GetDirectories(_target.SavesDir).Select(Path.GetFileName).ToList() : new List<string>();
                if (saves.Count == 0) { await Dialogs.Alert("没有存档", "数据包要装进某个存档里。先进游戏创建一个世界吧。"); return; }
                var idx = await Dialogs.Choose("装到哪个存档", "数据包只对选中的世界生效：", saves);
                if (idx < 0) return;
                dir = Path.Combine(_target.SavesDir, saves[idx], "datapacks");
            }
            else dir = ContentService.TargetDir(_target, _type);

            if (_type == ContentType.Mod && file.Loaders != null && file.Loaders.Count > 0 && _target.PrimaryLoader != LoaderType.None)
            {
                var slug = LoaderInfo.Slug(_target.PrimaryLoader);
                if (!file.Loaders.Any(l => string.Equals(l, slug, StringComparison.OrdinalIgnoreCase)) &&
                    !await Dialogs.Confirm("加载器不匹配", $"这个文件是给 {string.Join("/", file.Loaders)} 的，而 {_target.Id} 用的是 {LoaderInfo.Name(_target.PrimaryLoader)}，装了大概率不能用。仍然下载？"))
                    return;
            }

            var mc = _target.McVersion;
            var loader = LoaderInfo.Slug(_target.PrimaryLoader);
            bool deps = _type == ContentType.Mod;
            var title = _p.Title;

            // 装 Mod 之前先把必需前置解析出来，由用户勾选要哪几个（不替用户做主）
            ContentService.DependencyPlan plan = null;
            Dictionary<string, ContentProject> infos = null;
            if (deps && file.Dependencies != null && file.Dependencies.Any(d => d.Required))
            {
                using (var busy = Dialogs.Busy("正在检查前置 Mod", "正在查询它需要哪些前置…"))
                {
                    try { plan = await Task.Run(() => ContentService.PlanDependenciesAsync(file, dir, mc, loader, CancellationToken.None)); }
                    catch (Exception ex) { Log.Warn("解析前置失败：" + ex.Message); }
                    if (plan != null && plan.Any)
                    {
                        busy.Text = "正在获取前置的图标和简介…";
                        infos = await LoadDepInfosAsync(plan);
                    }
                }
            }

            bool withDeps = deps;
            List<ContentFile> picked = null;
            if (plan != null && plan.Any)
            {
                var picks = new List<DepPick>();
                var r = await Dialogs.ShowAsync("这个 Mod 有前置", DepPickBody(plan, infos, picks),
                    new[] { "取消", "只装这个", "下载勾选的" }, primary: 2, width: 560);
                // ShowAsync 返回的是按钮下标：0 = 取消、1 = 只装这个、2 = 下载勾选的（Esc 才是 -1）
                if (r <= 0) return;
                picked = r == 2 ? picks.Where(x => x.Box.IsChecked == true).Select(x => x.File).ToList() : new List<ContentFile>();
                withDeps = picked.Count > 0;
            }
            else if (plan != null && plan.Missing.Count > 0)
                Dialogs.Toast("有前置找不到适配版本：" + string.Join("、", plan.Missing), MainWindow.ToastKind.Error);

            // picked 非 null 时 InstallWithReportAsync 就照这份清单下载，一个没勾也不装
            TaskManager.Start($"下载 {title}", async t =>
            {
                Directory.CreateDirectory(dir);
                var installed = await ContentService.InstallAsync(file, dir, withDeps, mc, loader, t, picked);
                if (installed != null && installed.Count > 1) t.Detail = $"已安装 {installed.Count} 个文件（含前置）";
                TaskManager.OnUi(() =>
                {
                    RefreshInstalled();
                    Apply();
                });
            });
            var extra = picked != null && picked.Count > 0 ? $"（含 {picked.Count} 个前置）"
                      : plan != null && !plan.Any && plan.Skipped.Count > 0 ? "（前置都已安装）"
                      : "";
            Dialogs.Toast($"开始下载 {title}" + extra);
        }

        /// <summary>菜单关掉的时间点：再点一下箭头只算「收回」，不能又弹开</summary>
        int _menuClosedAt = -100000;

        /// <summary>下载按钮右边的下拉箭头：开/关这一行的菜单，箭头跟着翻上去</summary>
        void DownloadMenu_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is FileRow row)) return;
            var btn = (FrameworkElement)sender;
            var menu = btn.ContextMenu;
            if (menu == null) return;
            // 菜单开着时点这里，是刚被菜单自己关掉的那一下，别再重开
            if (menu.IsOpen || Environment.TickCount - _menuClosedAt < 400) return;
            // ContextMenu 在独立的可视树里，不会继承 DataContext，得手动传
            menu.DataContext = row;
            menu.PlacementTarget = btn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.Opened -= Menu_Opened;
            menu.Opened += Menu_Opened;
            menu.Closed -= Menu_Closed;
            menu.Closed += Menu_Closed;
            menu.IsOpen = true;
        }

        /// <summary>菜单开着的时候再点箭头：直接收回（这次点击不再走 Click，免得又弹开）</summary>
        void DownloadMenu_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var menu = (sender as FrameworkElement)?.ContextMenu;
            if (menu == null || !menu.IsOpen) return;
            menu.IsOpen = false;
            _menuClosedAt = Environment.TickCount;
            e.Handled = true;
        }

        void Menu_Opened(object sender, RoutedEventArgs e) => SpinArrow(sender as ContextMenu, 180);

        void Menu_Closed(object sender, RoutedEventArgs e)
        {
            _menuClosedAt = Environment.TickCount;
            SpinArrow(sender as ContextMenu, 0);
        }

        /// <summary>箭头转到指定角度：开 180（朝上），关 0（朝下）</summary>
        static void SpinArrow(ContextMenu menu, double angle)
        {
            if (menu == null) return;
            if (!(menu.PlacementTarget is ContentControl btn)) return;
            if (!(btn.Content is FrameworkElement arrow)) return;
            // 模板里的 Freezable 会被冻结（激活不了动画），第一次用时换成一个自己的
            var rot = arrow.RenderTransform as RotateTransform;
            if (rot == null || rot.IsFrozen)
            {
                rot = new RotateTransform(angle == 180 ? 0 : 180);
                arrow.RenderTransform = rot;
            }
            var anim = new DoubleAnimation(angle, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            rot.BeginAnimation(RotateTransform.AngleProperty, anim);
        }

        /// <summary>另存为：只把文件下到用户选的位置，不装进任何版本</summary>
        async void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is FileRow row)) return;
            var file = row.File;
            if (string.IsNullOrEmpty(file.Url)) { Dialogs.Toast("这个文件没有下载地址", MainWindow.ToastKind.Error); return; }

            var name = Util.SafeFileName(string.IsNullOrEmpty(file.FileName) ? _p.Title : file.FileName);
            var ext = Path.GetExtension(name);
            var filter = string.IsNullOrEmpty(ext)
                ? "所有文件 (*.*)|*.*"
                : $"{ext.TrimStart('.').ToUpperInvariant()} 文件 (*{ext})|*{ext}|所有文件 (*.*)|*.*";
            var path = Pickers.SaveFile("另存为", filter, name);
            if (string.IsNullOrEmpty(path)) return;

            var label = Path.GetFileName(path);
            var item = new DownloadItem
            {
                Url = file.Url,
                AltUrls = file.AltUrls == null ? null : file.AltUrls.Where(u => !string.IsNullOrEmpty(u)).ToList(),
                Path = path,
                Sha1 = file.Sha1,
                Sha512 = string.IsNullOrEmpty(file.Sha1) ? file.Sha512 : null,
                Size = file.Size,
            };
            TaskManager.Start($"另存 {label}", async t =>
            {
                await Downloader.RunAsync(new[] { item }, t, 0, 1, "下载 " + label);
                TaskManager.OnUi(() => Dialogs.Toast("已保存到 " + path));
            });
        }

        /// <summary>前置勾选对话框里的一行</summary>
        internal sealed class DepPick
        {
            public ContentFile File { get; set; }
            public CheckBox Box { get; set; }
        }

        /// <summary>
        /// 询问要下载哪些前置：每行一个复选框（默认全选）+ 图标 + 名称 + 一句话简介。
        /// 勾选结果由调用方从 picks 里读。
        /// </summary>
        internal static UIElement DepPickBody(ContentService.DependencyPlan plan, Dictionary<string, ContentProject> infos, List<DepPick> picks)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = $"要有这些前置才能正常运行（共 {plan.Files.Count} 个）。不想装哪个就取消勾选：",
                Style = S("Body"),
                TextWrapping = TextWrapping.Wrap,
            });

            var counter = new TextBlock { Style = S("Sub"), VerticalAlignment = VerticalAlignment.Center };
            var all = new CheckBox { Content = "全选", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
            var head = new DockPanel { Margin = new Thickness(0, 14, 0, 6) };
            DockPanel.SetDock(all, Dock.Right);
            head.Children.Add(all);
            head.Children.Add(counter);
            panel.Children.Add(head);

            // 全选与每一行会互相改 IsChecked，用一个标记挡住回环
            bool syncing = false;
            void refresh()
            {
                var n = picks.Count(x => x.Box.IsChecked == true);
                counter.Text = $"已勾选 {n} / {picks.Count} 个";
            }
            void syncAll()
            {
                syncing = true;
                all.IsChecked = picks.All(x => x.Box.IsChecked == true);
                syncing = false;
            }

            var rows = new StackPanel();
            for (int i = 0; i < plan.Files.Count; i++)
            {
                var f = plan.Files[i];
                var title = i < plan.Names.Count && !string.IsNullOrEmpty(plan.Names[i]) ? plan.Names[i]
                          : (!string.IsNullOrEmpty(f.DisplayName) ? f.DisplayName : f.FileName);

                ContentProject proj = null;
                if (!string.IsNullOrEmpty(f.ProjectId) && infos != null) infos.TryGetValue(ContentIndex.Key(f.Source, f.ProjectId), out proj);

                var box = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
                picks.Add(new DepPick { File = f, Box = box });

                var icon = new Border
                {
                    Width = 34,
                    Height = 34,
                    CornerRadius = new CornerRadius(8),
                    ClipToBounds = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0),
                };
                icon.SetResourceReference(Border.BackgroundProperty, "Card");
                if (proj != null && !string.IsNullOrEmpty(proj.IconUrl))
                {
                    var img = new Image { Stretch = Stretch.UniformToFill };
                    RemoteImage.SetUrl(img, proj.IconUrl);
                    icon.Child = img;
                }

                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var titleText = new TextBlock
                {
                    Text = title,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                };
                titleText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
                text.Children.Add(titleText);
                var desc = proj == null ? "" : ShortSummary(proj.Description);
                if (!string.IsNullOrEmpty(desc))
                {
                    var sub = new TextBlock
                    {
                        Text = desc,
                        Style = S("Sub"),
                        FontSize = 11.5,
                        Margin = new Thickness(0, 1, 0, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextWrapping = TextWrapping.NoWrap,
                    };
                    text.Children.Add(sub);
                }

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(box, 0);
                Grid.SetColumn(icon, 1);
                Grid.SetColumn(text, 2);
                grid.Children.Add(box);
                grid.Children.Add(icon);
                grid.Children.Add(text);

                var row = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 3),
                    Child = grid,
                    ToolTip = f.FileName,
                };
                row.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
                // 点这一行的任意位置都算勾选/取消，不用非得点在方块上
                row.MouseLeftButtonUp += (s, e) => box.IsChecked = box.IsChecked != true;
                // 用 Checked/Unchecked 而不是 Click：用户点、键盘空格、自动化都走这两个事件
                box.Checked += (s, e) => { if (syncing) return; syncAll(); refresh(); };
                box.Unchecked += (s, e) => { if (syncing) return; syncAll(); refresh(); };
                rows.Children.Add(row);
            }
            refresh();

            all.Checked += (s, e) => SetAll(true);
            all.Unchecked += (s, e) => SetAll(false);
            void SetAll(bool on)
            {
                if (syncing) return;
                syncing = true;
                foreach (var x in picks) x.Box.IsChecked = on;
                syncing = false;
                refresh();
            }

            panel.Children.Add(new ScrollViewer
            {
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = rows,
            });

            if (plan.Skipped.Count > 0)
                panel.Children.Add(new TextBlock
                {
                    Text = $"已有 {plan.Skipped.Count} 个前置装好了，会跳过：" + string.Join("、", plan.Skipped.Take(6)) + (plan.Skipped.Count > 6 ? "…" : ""),
                    Style = S("Sub"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                });
            if (plan.Missing.Count > 0)
                panel.Children.Add(new TextBlock
                {
                    Text = "这些前置找不到适配当前版本的文件，需要自己找：" + string.Join("、", plan.Missing.Take(6)) + (plan.Missing.Count > 6 ? "…" : ""),
                    Style = S("Sub"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                });
            return panel;
        }

        static Style S(string key) => (Style)Application.Current.FindResource(key);

        /// <summary>按项目 ID 批量取前置的图标和简介；取不到就只显示名字</summary>
        static async Task<Dictionary<string, ContentProject>> LoadDepInfosAsync(ContentService.DependencyPlan plan)
        {
            var map = new Dictionary<string, ContentProject>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var g in plan.Files.Where(f => !string.IsNullOrEmpty(f.ProjectId)).GroupBy(f => f.Source))
                {
                    var ids = g.Select(f => f.ProjectId).Distinct().ToList();
                    var projects = await ContentService.GetProjectsAsync(g.Key, ids, CancellationToken.None);
                    foreach (var p in projects)
                        if (p != null && !string.IsNullOrEmpty(p.Id)) map[ContentIndex.Key(g.Key, p.Id)] = p;
                }
            }
            catch (Exception ex) { Log.Warn("获取前置信息失败：" + ex.Message); }
            return map;
        }
    }
}
