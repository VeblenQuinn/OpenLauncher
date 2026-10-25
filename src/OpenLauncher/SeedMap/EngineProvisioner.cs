using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenLauncher.Core;

namespace OpenLauncher.SeedMap
{
    /// <summary>
    /// 种子地图引擎（cubiomes.dll）的来源与安装。
    ///
    /// **引擎打包在 exe 里**（见 <see cref="BundledEngine"/>），正常情况解出来就能用。
    /// 但这里**不做任何自动兜底**：内置那份解不出来时，本类只如实返回失败，
    /// 由界面弹窗让用户自己决定「从网络下载 / 挑一个本地 DLL / 先不用」。
    /// 程序不会替用户联网。
    ///
    /// 引擎用的是社区维护的 cubiomes 分支（xpple / Conflux-Union），
    /// 上游 Cubitect/cubiomes 只到 1.21 Winter Drop，这个分支已经加了 MC_26_3 。
    /// </summary>
    public static class EngineProvisioner
    {
        /// <summary>引擎是否已就位</summary>
        public static bool Installed => File.Exists(CubiomesEngine.DllPath);

        /// <summary>装好之后记录的版本，用于界面上显示</summary>
        public static string InstalledVersion => Settings.Get("seedmap.engineVersion");

        // ---------- 来源标记 ----------

        /// <summary>内置资源解出来的</summary>
        public const string SourceBundled = "bundled";
        /// <summary>从 Branding.EngineUrl 下载的</summary>
        public const string SourceDownload = "download";
        /// <summary>用户自己挑的文件。**这个来源下绝不自动覆盖**</summary>
        public const string SourceCustom = "custom";

        /// <summary>当前引擎是谁装的。空表示还没记录（老版本升级上来的）。</summary>
        public static string Source => Settings.Get("seedmap.engineSource") ?? "";

        static void SetSource(string source, string version)
        {
            Settings.Set("seedmap.engineSource", JNode.From(source ?? ""));
            Settings.Set("seedmap.engineVersion", JNode.From(version ?? ""));
            Settings.SaveNow();
        }

        /// <summary>当前引擎是不是用户自己挑的（这种情况不自动替换/升级）</summary>
        public static bool IsCustom => string.Equals(Source, SourceCustom, StringComparison.Ordinal);

        // ---------- 内置引擎 ----------

        /// <summary>
        /// 同步地把内置引擎解出来。**不联网、不弹窗**，失败就返回 false 交给界面处理。
        ///
        /// 两种「不覆盖」情况：
        /// · 用户自己挑过引擎（<see cref="IsCustom"/>）且文件还在 —— 尊重用户选择
        /// · 磁盘上已有的文件哈希正好等于内置那份 —— 什么都不用做
        ///
        /// 返回 true 时磁盘上已有可用 DLL，但调用方仍需 <see cref="CubiomesEngine.Reload"/>
        /// 才会真正加载（之前可能加载失败过）。
        /// </summary>
        public static bool TryInstallBundled()
        {
            var target = CubiomesEngine.DllPath;

            // 用户自己挑的引擎：只要文件还在就不动它
            if (IsCustom && File.Exists(target)) return true;

            if (!BundledEngine.TryExtract(out string reason))
            {
                Log.Warn("内置引擎不可用：" + reason);
                return false;
            }

            SetSource(SourceBundled, Branding.EngineVersion);
            return true;
        }

        // ---------- 从网络安装（只有用户明确点了才会走到） ----------

        /// <summary>
        /// 从 <see cref="Branding.EngineUrl"/> 下载并安装。
        /// **必须由用户主动触发**，不要在出错处理里自动调用。
        /// 失败会抛出带具体原因的异常（网络、哈希、写入权限）。
        /// </summary>
        public static async Task InstallFromUrlAsync(LauncherTask task, CancellationToken ct = default)
        {
            var url = Branding.EngineUrl;
            if (string.IsNullOrWhiteSpace(url)) throw new Exception("没有配置引擎下载地址");

            var temp = Path.Combine(Paths.Temp, "cubiomes.dll.download");
            Util.TryDelete(temp);

            var item = new DownloadItem
            {
                Url = url,
                AltUrls = new System.Collections.Generic.List<string>(Branding.EngineMirrors),
                Path = temp,
                UseMirror = false,   // 引擎不在游戏镜像表里，别套用
            };

            task.Report(0, "正在下载生成引擎…");
            await Downloader.DownloadFileAsync(item, task, 0, 0.92).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // 可选的整体校验：留空就跳过（HTTPS 已经防了传输损坏）
            if (!string.IsNullOrWhiteSpace(Branding.EngineSha256))
            {
                task.Report(0.94, "校验引擎…");
                var actual = Util.Sha256File(temp);
                if (!string.Equals(actual, Branding.EngineSha256, StringComparison.OrdinalIgnoreCase))
                {
                    Util.TryDelete(temp);
                    throw new Exception("下载到的引擎和预期哈希不一致，已删除。可能是版本更新了，请检查 Branding.EngineSha256。");
                }
            }

            task.Report(0.96, "安装引擎…");
            Directory.CreateDirectory(Paths.Tools);
            var target = CubiomesEngine.DllPath;
            try { if (File.Exists(target)) File.Delete(target); }
            catch (Exception ex) { Log.Warn("删除旧引擎失败（可能正被占用）：" + ex.Message); }

            try { File.Move(temp, target); }
            catch (Exception ex)
            {
                Util.TryDelete(temp);
                throw new Exception("写入工具目录失败：" + ex.Message);
            }

            SetSource(SourceDownload, Branding.EngineVersion);
            Log.Info("种子地图引擎已从网络安装：" + Branding.EngineVersion);
            task.Report(1, "引擎已就绪");
        }

        // ---------- 用用户挑的文件安装 ----------

        /// <summary>
        /// 把用户选定的 DLL 装成引擎，装完立刻试着加载验证。
        ///
        /// 加载不起来会**还原之前那份**再报错——不能让用户挑错一个文件就把好引擎弄没了。
        /// 成功返回 null，失败返回给用户看的原因。
        /// </summary>
        public static string InstallFromFile(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return "找不到这个文件";

            var target = CubiomesEngine.DllPath;
            var backup = target + ".bak";

            try
            {
                Directory.CreateDirectory(Paths.Tools);

                // 先把现有的挪走当备份（改名而不是删，方便回滚）
                Util.TryDelete(backup);
                if (File.Exists(target))
                {
                    try { File.Move(target, backup); }
                    catch (Exception ex)
                    {
                        return "现有引擎正被占用，没法替换（" + ex.Message + "）。请重启启动器后再试。";
                    }
                }

                try { File.Copy(sourcePath, target, true); }
                catch (Exception ex)
                {
                    Restore(backup, target);
                    return "复制文件失败：" + ex.Message;
                }

                // 真的加载一次，确认这个 DLL 能用（版本不匹配、不是 DLL、导出缺失都会在这里暴露）
                CubiomesEngine.Reload();
                if (!CubiomesEngine.Available)
                {
                    Restore(backup, target);
                    CubiomesEngine.Reload();   // 把旧引擎重新挂回去
                    return "这个文件加载不起来：" + (CubiomesEngine.LoadError ?? "未知原因");
                }

                Util.TryDelete(backup);
                SetSource(SourceCustom, CubiomesEngine.SupportsStructureScan && CubiomesEngine.SupportsStrongholds
                    ? (Branding.EngineVersion ?? "自定义")
                    : "自定义（功能受限）");
                Log.Info("种子地图引擎已替换为用户指定的文件：" + sourcePath);
                return null;
            }
            catch (Exception ex)
            {
                Restore(backup, target);
                return ex.Message;
            }
        }

        static void Restore(string backup, string target)
        {
            try
            {
                if (!File.Exists(backup)) return;
                Util.TryDelete(target);
                File.Move(backup, target);
            }
            catch (Exception ex) { Log.Warn("还原旧引擎失败：" + ex.Message); }
        }

        /// <summary>删掉引擎（下次启动会从内置资源重新解出）</summary>
        public static void Uninstall()
        {
            try { File.Delete(CubiomesEngine.DllPath); }
            catch (Exception ex) { Log.Warn("删除引擎失败：" + ex.Message); }
            SetSource("", "");
        }
    }
}
