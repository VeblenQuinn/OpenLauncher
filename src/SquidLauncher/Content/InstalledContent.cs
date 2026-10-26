using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SquidLauncher.Core;
using SquidLauncher.Game;

namespace SquidLauncher.Content
{
    /// <summary>实例里已经装上的一个社区资源</summary>
    public sealed class InstalledItem
    {
        public ContentSource Source { get; set; }
        public string ProjectId { get; set; }
        /// <summary>项目在资源站上的 slug（可能为空）；用于同一个 Mod 在 Modrinth / CurseForge 之间对上号</summary>
        public string Slug { get; set; }
        /// <summary>装的是哪个文件（Modrinth version id / CurseForge file id），可能为空</summary>
        public string FileId { get; set; }
        /// <summary>文件夹里的文件名（不含 .disabled）</summary>
        public string FileName { get; set; }
        public string Sha1 { get; set; }
        /// <summary>被 .disabled 关掉时为 false</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>当前筛选的游戏版本 + 加载器下已经有更新的版本</summary>
        public bool Outdated { get; set; }
        /// <summary>更新版本的文件名（没有更新时为 null）</summary>
        public string LatestFileName { get; set; }

        public string Key => ContentIndex.Key(Source, ProjectId);
    }

    /// <summary>
    /// 统计“某个版本里已经装了哪些资源”（Mod / 资源包 / 光影）。
    /// 数据来源是安装时写下的索引（.openlauncher-index.json）；手动丢进去的文件靠 Modrinth 哈希识别补上。
    /// 扫描结果按文件夹缓存，避免每次搜索都重扫一遍。
    /// </summary>
    public static class InstalledContent
    {
        sealed class ScanEntry
        {
            public DateTime At;
            public Dictionary<string, InstalledItem> Map;
        }

        sealed class UpdateEntry
        {
            public DateTime At;
            public Dictionary<string, (bool outdated, string name)> Map;
        }

        static readonly ConcurrentDictionary<string, ScanEntry> Scans = new ConcurrentDictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase);
        static readonly ConcurrentDictionary<string, UpdateEntry> Updates = new ConcurrentDictionary<string, UpdateEntry>(StringComparer.OrdinalIgnoreCase);
        static readonly ConcurrentDictionary<string, bool> Identifying = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        static readonly TimeSpan ScanTtl = TimeSpan.FromSeconds(20);
        static readonly TimeSpan UpdateTtl = TimeSpan.FromMinutes(5);

        /// <summary>本地哈希缓存（算是哪个文件、哪些哈希 Modrinth 上认不出来），避免每次开页都重算重查</summary>
        const string HashCacheFile = ".openlauncher-hashes.json";

        /// <summary>某类资源在实例里的安装目录；整合包 / 数据包返回 null（它们没法跟“选中的版本”一一对应）</summary>
        public static string DirOf(Instance inst, ContentType type)
        {
            if (inst == null || !inst.IsValid) return null;
            if (type != ContentType.Mod && type != ContentType.ResourcePack && type != ContentType.Shader) return null;
            return ContentService.TargetDir(inst, type);
        }

        /// <summary>清掉某个文件夹的缓存（装完东西、删了文件之后调用）</summary>
        public static void Invalidate(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            Scans.TryRemove(dir, out _);
            foreach (var k in Updates.Keys)
                if (k.StartsWith(dir + "|", StringComparison.OrdinalIgnoreCase)) Updates.TryRemove(k, out _);
        }

        // ---------------- 扫描 ----------------

        /// <summary>
        /// 只读本地索引，不发网络请求。返回 项目Key → 已安装信息；整合包 / 数据包 / 没选目标时返回 null。
        /// </summary>
        public static Dictionary<string, InstalledItem> ScanLocal(Instance inst, ContentType type)
        {
            var dir = DirOf(inst, type);
            if (dir == null) return null;
            if (Scans.TryGetValue(dir, out var e) && DateTime.UtcNow - e.At < ScanTtl) return e.Map;
            var map = BuildMap(dir);
            Scans[dir] = new ScanEntry { At = DateTime.UtcNow, Map = map };
            return map;
        }

        static Dictionary<string, InstalledItem> BuildMap(string dir)
        {
            var map = new Dictionary<string, InstalledItem>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(dir)) return map;
            ContentIndex index;
            try { index = ContentIndex.Load(dir); }
            catch (Exception ex) { Log.Warn("读取资源索引失败：" + ex.Message); return map; }
            var slugs = LoadHashCache(dir)["slugs"];
            foreach (var en in index.Entries())
            {
                var path = Path.Combine(dir, en.FileName);
                bool enabled = true;
                if (!File.Exists(path))
                {
                    if (!File.Exists(path + ".disabled")) continue;   // 文件已经被删了
                    enabled = false;
                    path += ".disabled";
                }
                var item = new InstalledItem
                {
                    Source = en.Source,
                    ProjectId = en.ProjectId,
                    FileId = en.FileId,
                    FileName = en.FileName,
                    Sha1 = en.Sha1,
                    Enabled = enabled,
                    Slug = slugs[ContentIndex.Key(en.Source, en.ProjectId)].Str,
                };
                // 同一个项目装了多个文件时，留最后写入的那个
                if (map.TryGetValue(item.Key, out var old) && WriteTime(old, dir) >= WriteTime(item, dir)) continue;
                map[item.Key] = item;
            }
            return map;
        }

        static DateTime WriteTime(InstalledItem it, string dir)
        {
            try
            {
                var p = Path.Combine(dir, it.FileName);
                if (File.Exists(p)) return File.GetLastWriteTimeUtc(p);
                if (File.Exists(p + ".disabled")) return File.GetLastWriteTimeUtc(p + ".disabled");
            }
            catch { }
            return DateTime.MinValue;
        }

        /// <summary>
        /// 索引里没有的文件按 SHA-1 拿去 Modrinth 认一下（认出来写进资源索引）。返回是否有变化。
        /// 算过的哈希和「Modrinth 上认不出来」的结论都记在 .openlauncher-hashes.json 里，
        /// 所以只有新丢进来的文件才会重算 + 重新联网。
        /// </summary>
        public static async Task<bool> IdentifyAsync(Instance inst, ContentType type, CancellationToken ct)
        {
            var dir = DirOf(inst, type);
            if (dir == null || !Directory.Exists(dir)) return false;
            if (!Identifying.TryAdd(dir, true)) return false;   // 同一个文件夹同时只跑一份
            try
            {
                var files = ContentService.LocalFiles(dir);
                if (files.Count == 0) return false;
                var index = ContentIndex.Load(dir);
                var node = LoadHashCache(dir);
                if (!node["files"].IsObj) node.Set("files", JNode.Obj());
                if (!node["unknown"].IsArr) node.Set("unknown", JNode.Arr());
                var filesNode = node["files"];
                var unknownNode = node["unknown"];
                var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var u in unknownNode) if (u.Str != null) unknown.Add(u.Str);

                // 1. 先看缓存里有没有这个文件的哈希（大小 + 修改时间一致就算同一个文件）
                var hashed = new List<(string name, long len, long ticks, string sha1)>();
                var toHash = new List<(string path, string name, long len, long ticks)>();
                foreach (var f in files)
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        var name = CleanName(f);
                        var len = fi.Length;
                        var ticks = fi.LastWriteTimeUtc.Ticks;
                        var stamp = len.ToString(CultureInfo.InvariantCulture) + ":" + ticks.ToString(CultureInfo.InvariantCulture) + ":";
                        var cached = filesNode[name].Str;
                        if (cached != null && cached.StartsWith(stamp, StringComparison.Ordinal) && cached.Length > stamp.Length)
                            hashed.Add((name, len, ticks, cached.Substring(stamp.Length)));
                        else
                            toHash.Add((f, name, len, ticks));
                    }
                    catch { /* 文件被占用时跳过 */ }
                }

                // 2. 新文件算哈希（并行，结果会写进上面的缓存文件）
                if (toHash.Count > 0)
                {
                    var shas = new string[toHash.Count];
                    await Task.Run(() =>
                        Parallel.For(0, toHash.Count, new ParallelOptions { CancellationToken = ct },
                            i => shas[i] = ContentService.CachedSha1(toHash[i].path)), ct).ConfigureAwait(false);
                    for (int i = 0; i < toHash.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var t = toHash[i];
                        if (shas[i] == null) continue;
                        hashed.Add((t.name, t.len, t.ticks, shas[i]));
                        filesNode.Set(t.name, t.len.ToString(CultureInfo.InvariantCulture) + ":" +
                                               t.ticks.ToString(CultureInfo.InvariantCulture) + ":" + shas[i]);
                    }
                }
                if (hashed.Count == 0) return false;

                // 3. 先把算好的哈希落盘（网络失败也不至于下次重算）
                SaveHashCache(dir, node);

                // 4. 只查还不认识的（索引里没记录、之前也没查过）
                var ask = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);   // sha1 → 文件名们
                foreach (var h in hashed)
                {
                    if (h.sha1 == null || unknown.Contains(h.sha1)) continue;
                    if (index.ProjectOf(h.name) != null) continue;
                    if (!ask.TryGetValue(h.sha1, out var names)) ask[h.sha1] = names = new List<string>();
                    names.Add(h.name);
                }
                var changed = false;
                if (ask.Count > 0)
                {
                    var found = await ModrinthProvider.LookupHashesAsync(ask.Keys, ct).ConfigureAwait(false);
                    foreach (var kv in ask)
                    {
                        if (!found.TryGetValue(kv.Key, out var v))
                        {
                            if (unknown.Add(kv.Key)) unknownNode.Add(kv.Key);
                            continue;
                        }
                        var cf = ModrinthProvider.ParseVersion(v, kv.Key);
                        if (cf == null) continue;
                        foreach (var name in kv.Value) { index.Set(name, cf); changed = true; }
                    }
                    if (changed) index.Save();
                }
                SaveHashCache(dir, node);
                if (changed) Invalidate(dir);
                return changed;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Warn("识别已安装资源失败：" + ex.Message);
                return false;
            }
            finally { Identifying.TryRemove(dir, out _); }
        }

        /// <summary>
        /// 把已装项目的 slug 补全（存在 .openlauncher-hashes.json 的 slugs 里，只查一次）。
        /// 有了 slug 就能把「在 CurseForge 页面上搜到的项目」和「当年从 Modrinth 装下的文件」对上号。
        /// </summary>
        public static async Task ResolveSlugsAsync(string dir, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                var node = LoadHashCache(dir);
                if (!node["slugs"].IsObj) node.Set("slugs", JNode.Obj());
                var slugs = node["slugs"];
                var index = ContentIndex.Load(dir);
                var present = new HashSet<string>(ContentService.LocalFiles(dir).Select(CleanName), StringComparer.OrdinalIgnoreCase);

                var wants = new Dictionary<ContentSource, List<string>>();
                var keys = new Dictionary<string, (ContentSource src, string id)>(StringComparer.OrdinalIgnoreCase);
                foreach (var en in index.Entries())
                {
                    if (!present.Contains(en.FileName)) continue;
                    var key = ContentIndex.Key(en.Source, en.ProjectId);
                    if (!slugs[key].IsNull || keys.ContainsKey(key)) continue;
                    if (!wants.TryGetValue(en.Source, out var list)) wants[en.Source] = list = new List<string>();
                    list.Add(en.ProjectId);
                    keys[key] = (en.Source, en.ProjectId);
                }
                if (keys.Count == 0) return;

                var changed = false;
                foreach (var kv in wants)
                {
                    ct.ThrowIfCancellationRequested();
                    List<ContentProject> projects;
                    try { projects = await ContentService.GetProjectsAsync(kv.Key, kv.Value, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { Log.Warn($"获取项目 slug 失败（{kv.Key}）：{ex.Message}"); continue; }
                    var got = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in projects)
                    {
                        if (string.IsNullOrEmpty(p?.Id)) continue;
                        got.Add(p.Id);
                        if (string.IsNullOrEmpty(p.Slug)) continue;
                        slugs.Set(ContentIndex.Key(p.Source, p.Id), p.Slug);
                        changed = true;
                    }
                    // 查不到的项目记成空串，免得每次开页都重查
                    foreach (var id in kv.Value)
                        if (!got.Contains(id)) { slugs.Set(ContentIndex.Key(kv.Key, id), ""); changed = true; }
                }
                if (changed) { SaveHashCache(dir, node); Invalidate(dir); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log.Warn("补全项目 slug 失败：" + ex.Message); }
        }

        /// <summary>目录里是不是已经有这个文件（含 .disabled）。用于详情页判断某个具体版本装了没</summary>
        public static bool FilePresent(string dir, string fileName)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(fileName)) return false;
            try
            {
                var p = Path.Combine(dir, fileName);
                return File.Exists(p) || File.Exists(p + ".disabled");
            }
            catch { return false; }
        }

        /// <summary>文件名去掉 .disabled</summary>
        static string CleanName(string path)
        {
            var n = Path.GetFileName(path);
            return n != null && n.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? n.Substring(0, n.Length - 9) : n;
        }

        static JNode LoadHashCache(string dir)
        {
            try
            {
                var p = Path.Combine(dir, HashCacheFile);
                if (File.Exists(p))
                {
                    var n = JNode.ParseFile(p);
                    if (n.IsObj) return n;
                }
            }
            catch (Exception ex) { Log.Warn("本地哈希缓存损坏，忽略：" + ex.Message); }
            return JNode.Obj().Set("version", 1);
        }

        static void SaveHashCache(string dir, JNode node)
        {
            try { node.Set("version", 1).SaveFile(Path.Combine(dir, HashCacheFile)); }
            catch (Exception ex) { Log.Warn("保存本地哈希缓存失败：" + ex.Message); }
        }

        // ---------------- 有没有更新 ----------------

        /// <summary>
        /// 查“已装的这些在当前游戏版本 + 加载器下是不是旧版本”。不管当初是从哪个源装的，
        /// 只要这个文件在 Modrinth 上存在就能查到（CurseForge 没有批量哈希接口）。
        /// </summary>
        public static async Task CheckOutdatedAsync(string dir, Dictionary<string, InstalledItem> map, string gameVersion, string loader, bool byLoader, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(dir) || map == null || map.Count == 0) return;
            var cacheKey = dir + "|" + (gameVersion ?? "") + "|" + (byLoader ? loader ?? "" : "");
            if (Updates.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.At < UpdateTtl)
            {
                Apply(map, cached.Map);
                return;
            }

            var items = map.Values.Where(i => !string.IsNullOrEmpty(i.Sha1)).ToList();
            var result = new Dictionary<string, (bool outdated, string name)>(StringComparer.OrdinalIgnoreCase);
            if (items.Count > 0)
            {
                var shas = items.Select(i => i.Sha1).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var cur = await ModrinthProvider.LookupHashesAsync(shas, ct).ConfigureAwait(false);
                var latest = cur.Count == 0
                    ? new Dictionary<string, JNode>()
                    : await ModrinthProvider.LookupUpdatesAsync(cur.Keys,
                        byLoader ? ModrinthProvider.LoaderFilter(loader) : null,
                        string.IsNullOrWhiteSpace(gameVersion) ? null : new List<string> { gameVersion.Trim() },
                        ct).ConfigureAwait(false);
                foreach (var it in items)
                {
                    bool outdated = false;
                    string name = null;
                    if (cur.TryGetValue(it.Sha1, out var c) && latest.TryGetValue(it.Sha1, out var v))
                    {
                        var nf = ModrinthProvider.ParseVersion(v);
                        outdated = nf != null
                            && v["id"].Str != c["id"].Str
                            && v["id"].Str != it.FileId
                            && !string.Equals(nf.Sha1, it.Sha1, StringComparison.OrdinalIgnoreCase)
                            && ContentJson.Date(v["date_published"]) > ContentJson.Date(c["date_published"]);
                        if (outdated) name = nf.FileName;
                    }
                    result[it.Key] = (outdated, name);
                }
            }
            Updates[cacheKey] = new UpdateEntry { At = DateTime.UtcNow, Map = result };
            Apply(map, result);
        }

        static void Apply(Dictionary<string, InstalledItem> map, Dictionary<string, (bool outdated, string name)> result)
        {
            foreach (var kv in map)
            {
                if (result.TryGetValue(kv.Key, out var r)) { kv.Value.Outdated = r.outdated; kv.Value.LatestFileName = r.name; }
                else { kv.Value.Outdated = false; kv.Value.LatestFileName = null; }
            }
        }

        /// <summary>把“已安装”状态写进搜索结果（先按项目 ID 对，对不上再按 slug 跨站匹配）</summary>
        public static void MarkProjects(IEnumerable<ContentProject> projects, Dictionary<string, InstalledItem> map)
        {
            if (projects == null) return;
            Dictionary<string, InstalledItem> bySlug = null;
            foreach (var p in projects)
            {
                if (p == null) continue;
                InstalledItem it = null;
                if (map != null && !string.IsNullOrEmpty(p.Id)) map.TryGetValue(ContentIndex.Key(p.Source, p.Id), out it);
                if (it == null && map != null && !string.IsNullOrEmpty(p.Slug))
                {
                    if (bySlug == null)
                    {
                        bySlug = new Dictionary<string, InstalledItem>(StringComparer.OrdinalIgnoreCase);
                        foreach (var v in map.Values)
                        {
                            var s = NormSlug(v.Slug);
                            if (s != null && !bySlug.ContainsKey(s)) bySlug[s] = v;
                        }
                    }
                    if (NormSlug(p.Slug) is string ns) bySlug.TryGetValue(ns, out it);
                }
                p.Installed = it == null ? InstalledState.None : it.Outdated ? InstalledState.Outdated : InstalledState.Installed;
            }
        }

        /// <summary>slug 归一化（只留字母数字）；太短的不参与匹配，避免误判</summary>
        static string NormSlug(string slug)
        {
            if (string.IsNullOrEmpty(slug)) return null;
            var sb = new System.Text.StringBuilder(slug.Length);
            foreach (var c in slug)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.Length >= 3 ? sb.ToString() : null;
        }

        /// <summary>某个项目在实例里的安装记录；没装返回 null</summary>
        public static InstalledItem Find(Instance inst, ContentType type, ContentSource source, string projectId, string slug = null)
        {
            var map = ScanLocal(inst, type);
            if (map == null) return null;
            if (!string.IsNullOrEmpty(projectId) && map.TryGetValue(ContentIndex.Key(source, projectId), out var it)) return it;
            var ns = NormSlug(slug);
            if (ns == null) return null;
            foreach (var v in map.Values)
                if (NormSlug(v.Slug) == ns) return v;
            return null;
        }
    }
}
