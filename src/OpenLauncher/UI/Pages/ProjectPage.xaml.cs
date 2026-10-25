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
using OpenLauncher.Content;
using OpenLauncher.Core;
using OpenLauncher.Game;

namespace OpenLauncher.UI.Pages
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

        string _title, _note;

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
        int _depSeq;
        bool _loaded;

        public ProjectPage(ContentProject p, ContentType type, Instance target, string mc, string loader)
        {
            InitializeComponent();
            _p = p; _type = type; _target = target;
            _mc = string.IsNullOrEmpty(mc) ? null : mc;
            _loader = string.IsNullOrEmpty(loader) ? null : loader;
            PTitle.Text = p.Title;
            PMeta.Text = $"{p.Author} · {p.SourceText} · 下载 {p.DownloadsText} · 更新于 {p.Updated:yyyy-MM-dd}" + (p.Categories?.Count > 0 ? " · " + string.Join(" ", p.Categories.Take(6)) : "");
            PDesc.Text = p.Description;
            TargetInfo.Text = type == ContentType.Modpack ? "整合包会安装成一个新版本" : target != null ? $"安装到 {target.Id}" : "未选择目标版本";
            OnlyMatch.Visibility = _mc == null && _loader == null ? Visibility.Collapsed : Visibility.Visible;
        }

        public async void OnShown()
        {
            if (_loaded) return;
            _loaded = true;
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
            Files.ItemsSource = rows;
            FilesTitle.Text = $"选择要下载的文件（{rows.Count}）" + (_type == ContentType.Mod ? " · 点一行可看前置" : "");
            if (rows.Count == 0)
            {
                StatusPanel.Visibility = Visibility.Visible;
                StatusBar.Visibility = Visibility.Collapsed;
                StatusText.Text = _files.Count == 0 ? "这个项目没有可下载的文件" : $"没有适配 {_mc} {_loader} 的文件，关掉右上角的筛选看看全部文件";
            }
            else StatusPanel.Visibility = Visibility.Collapsed;
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

        void Files_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDependencies();

        /// <summary>选中一行时，把它的必需前置列在上方（已安装的会标出来）</summary>
        async void UpdateDependencies()
        {
            var row = Files.SelectedItem as FileRow;
            if (_type != ContentType.Mod || row == null) { ShowDependencies(null); return; }
            if (ReferenceEquals(row, _depRow)) return;
            _depRow = row;
            var seq = ++_depSeq;

            var deps = row.File.Dependencies?.Where(d => d.Required).ToList();
            if (deps == null || deps.Count == 0) { ShowDependencies(null); return; }

            Dictionary<string, InstalledItem> map = null;
            try { map = InstalledContent.ScanLocal(_target, _type); }
            catch (Exception ex) { Log.Warn("读取已安装记录失败：" + ex.Message); }

            var items = deps.Select(d => new DepRow
            {
                Source = _p.Source,
                ProjectId = d.ProjectId,
                FileName = d.FileName,
                Title = !string.IsNullOrEmpty(d.FileName) ? d.FileName : d.ProjectId ?? d.FileId ?? "未知前置",
            }).ToList();
            foreach (var it in items) it.Note = DepNote(it, map);
            ShowDependencies(items);

            // 用项目 ID 把真实名字拿回来（接口失败就先用文件名顶着）
            var ids = deps.Where(d => !string.IsNullOrEmpty(d.ProjectId)).Select(d => d.ProjectId).Distinct().ToList();
            if (ids.Count == 0) return;
            try
            {
                var projects = await ContentService.GetProjectsAsync(_p.Source, ids, CancellationToken.None);
                if (seq != _depSeq) return;
                foreach (var it in items)
                {
                    var proj = projects.FirstOrDefault(x => x != null && string.Equals(x.Id, it.ProjectId, StringComparison.OrdinalIgnoreCase));
                    if (proj == null) continue;
                    if (!string.IsNullOrEmpty(proj.Title)) it.Title = proj.Title;
                    it.Slug = proj.Slug;
                }
            }
            catch (Exception ex) { Log.Warn("获取前置名称失败：" + ex.Message); }
        }

        string DepNote(DepRow d, Dictionary<string, InstalledItem> map)
        {
            if (InstalledContent.FilePresent(_installedDir, d.FileName)) return "已安装";
            if (map != null && !string.IsNullOrEmpty(d.ProjectId)
                && map.ContainsKey(ContentIndex.Key(d.Source, d.ProjectId))) return "已安装";
            return string.IsNullOrEmpty(d.ProjectId) ? "需要手动找" : "";
        }

        void ShowDependencies(List<DepRow> items)
        {
            if (items == null || items.Count == 0) { DepPanel.Visibility = Visibility.Collapsed; DepList.ItemsSource = null; return; }
            DepList.ItemsSource = items;
            int installed = items.Count(i => i.Note == "已安装");
            DepTitle.Text = $"前置 Mod（{items.Count}）";
            DepHint.Text = installed > 0 ? $"其中 {installed} 个已安装" : "下载时可以一键补齐";
            DepPanel.Visibility = Visibility.Visible;
        }

        /// <summary>点前置标签上的“跳转”：打开它自己的详情页</summary>
        async void DepJump_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is DepRow row)) return;
            if (string.IsNullOrEmpty(row.ProjectId) && string.IsNullOrEmpty(row.Slug))
            {
                Dialogs.Toast("这个前置只给了文件名，资源站上没有对应项目", MainWindow.ToastKind.Error);
                return;
            }
            ContentProject proj = null;
            try
            {
                var ids = new List<string> { row.ProjectId ?? row.Slug };
                var list = await ContentService.GetProjectsAsync(row.Source, ids, CancellationToken.None);
                proj = list.FirstOrDefault(x => x != null &&
                    (string.Equals(x.Id, row.ProjectId, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(x.Slug, row.Slug, StringComparison.OrdinalIgnoreCase)));
                proj = proj ?? list.FirstOrDefault();
            }
            catch (Exception ex) { Log.Warn("打开前置详情失败：" + ex.Message); }
            if (proj == null)
            {
                Dialogs.Toast("打不开这个前置的详情页", MainWindow.ToastKind.Error);
                return;
            }
            if (string.IsNullOrEmpty(proj.Slug)) proj.Slug = row.Slug;
            MainWindow.Current.Push(new ProjectPage(proj, ContentType.Mod, _target, _mc, _loader));
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

            if (_target == null) { await Dialogs.Alert("没有目标版本", "先在上一页的“安装到”里选一个版本（没有的话先去装一个）。"); return; }

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

            // 装 Mod 之前先把必需前置解析出来，由用户决定要不要一并下载（不替用户做主）
            ContentService.DependencyPlan plan = null;
            if (deps && file.Dependencies != null && file.Dependencies.Any(d => d.Required))
            {
                StatusPanel.Visibility = Visibility.Visible;
                StatusText.Text = "正在检查前置 Mod…";
                StatusBar.Visibility = Visibility.Visible;
                try { plan = await Task.Run(() => ContentService.PlanDependenciesAsync(file, dir, mc, loader, CancellationToken.None)); }
                catch (Exception ex) { Log.Warn("解析前置失败：" + ex.Message); }
                finally { StatusPanel.Visibility = Visibility.Collapsed; }
            }

            bool withDeps = deps;
            if (plan != null && plan.Any)
            {
                var r = await Dialogs.ShowAsync("这个 Mod 有前置", DepPromptBody(plan),
                    new[] { "只装这个", $"一并下载（{plan.Files.Count} 个）" }, primary: 1);
                if (r < 0) return;
                withDeps = r == 1;
            }
            else if (plan != null && plan.Missing.Count > 0)
                Dialogs.Toast("有前置找不到适配版本：" + string.Join("、", plan.Missing), MainWindow.ToastKind.Error);

            bool withPlan = withDeps && plan != null && plan.Any;
            TaskManager.Start($"下载 {title}", async t =>
            {
                Directory.CreateDirectory(dir);
                var installed = await ContentService.InstallAsync(file, dir, withDeps, mc, loader, t);
                if (installed != null && installed.Count > 1) t.Detail = $"已安装 {installed.Count} 个文件（含前置）";
                TaskManager.OnUi(() =>
                {
                    RefreshInstalled();
                    Apply();
                });
            });
            var extra = withPlan ? $"（含 {plan.Files.Count} 个前置）"
                      : plan != null && !plan.Any && plan.Skipped.Count > 0 ? "（前置都已安装）"
                      : "";
            Dialogs.Toast($"开始下载 {title}" + extra);
        }

        /// <summary>询问是否一并下载前置时的正文</summary>
        internal static UIElement DepPromptBody(ContentService.DependencyPlan plan)
        {
            var panel = new StackPanel();
            panel.Children.Add(Dialogs.Text($"要装好这些前置才能正常运行（共 {plan.Files.Count} 个）："));
            var shown = plan.Names.Take(12).ToList();
            var text = string.Join("\n", shown.Select(n => "· " + n));
            if (plan.Names.Count > shown.Count) text += $"\n… 等共 {plan.Names.Count} 个";
            panel.Children.Add(new TextBlock
            {
                Text = text,
                Style = (Style)Application.Current.FindResource("Body"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });
            if (plan.Skipped.Count > 0)
                panel.Children.Add(new TextBlock
                {
                    Text = $"已有 {plan.Skipped.Count} 个前置装好了，会跳过：" + string.Join("、", plan.Skipped.Take(6)) + (plan.Skipped.Count > 6 ? "…" : ""),
                    Style = (Style)Application.Current.FindResource("Sub"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                });
            if (plan.Missing.Count > 0)
                panel.Children.Add(new TextBlock
                {
                    Text = "这些前置找不到适配当前版本的文件，需要自己找：" + string.Join("、", plan.Missing.Take(6)) + (plan.Missing.Count > 6 ? "…" : ""),
                    Style = (Style)Application.Current.FindResource("Sub"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                });
            return panel;
        }
    }
}
