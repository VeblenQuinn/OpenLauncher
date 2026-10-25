using System;
using System.IO;
using System.Reflection;
using OpenLauncher.Core;

namespace OpenLauncher.SeedMap
{
    /// <summary>
    /// 随程序一起打包的种子地图引擎（cubiomes.dll）。
    ///
    /// 引擎是原生 C 库，必须落地成真实文件才能 LoadLibrary（不能直接从内存加载），
    /// 所以这里把 exe 里的嵌入资源解出来写进工具目录。
    ///
    /// 只在「本地没有 / 哈希对不上」时才写盘，所以正常情况下这是零成本的：
    /// 只算一次 1.5MB 的 SHA-256（约 3~5ms）。
    ///
    /// 资源名固定为 <c>cubiomes.dll</c>（见 csproj 里的 LogicalName）。
    /// 换引擎：把新 DLL 覆盖到 <c>src\OpenLauncher\Assets\cubiomes.dll</c>，
    /// 同时更新 <see cref="Branding.EngineSha256"/>。
    /// </summary>
    public static class BundledEngine
    {
        /// <summary>嵌入资源里的名字（csproj 的 LogicalName 写的就是这个）</summary>
        const string ResourceName = "cubiomes.dll";

        /// <summary>exe 里到底有没有打包引擎</summary>
        public static bool Exists
        {
            get
            {
                try { return FindResource() != null; }
                catch { return false; }
            }
        }

        static Stream FindResource()
        {
            var asm = Assembly.GetExecutingAssembly();
            var s = asm.GetManifestResourceStream(ResourceName);
            if (s != null) return s;

            // 兜底：LogicalName 没生效时会变成 <RootNamespace>.<路径>，
            // 这里按后缀再找一次，免得因为改 csproj 就整段失效
            foreach (var name in asm.GetManifestResourceNames())
                if (name.EndsWith("cubiomes.dll", StringComparison.OrdinalIgnoreCase))
                    return asm.GetManifestResourceStream(name);
            return null;
        }

        /// <summary>
        /// 把引擎解到 <see cref="CubiomesEngine.DllPath"/>。
        /// 返回 true 表示「现在磁盘上有一份可用的引擎」（可能是原本就有的）。
        /// 失败不抛异常，只返回 false 并把原因写进 <paramref name="reason"/>，
        /// 由调用方决定后续（本类**不会**自己跑去联网下载）。
        /// </summary>
        public static bool TryExtract(out string reason) => TryExtractCore(out reason);

        /// <summary>旧签名，忽略原因</summary>
        public static bool TryExtract()
        {
            var ignored = TryExtractCore(out _);
            return ignored;
        }

        static bool TryExtractCore(out string reason)
        {
            var target = CubiomesEngine.DllPath;
            string expected = Branding.EngineSha256;
            reason = null;

#if DEBUG
            // 诊断开关：模拟「exe 里的引擎损坏」，用来验证界面走的是「弹窗问用户」，
            // 而不是悄悄自己去下载。设了 OL_ENGINE_BROKEN=1 才生效。
            if (Environment.GetEnvironmentVariable("OL_ENGINE_BROKEN") == "1")
            {
                reason = "（诊断）内置引擎被 OL_ENGINE_BROKEN 强制标记为损坏";
                Log.Warn(reason);
                return false;
            }
#endif

            try
            {
                // 已经有一份且哈希对得上 → 什么都不用做
                if (File.Exists(target) && HashMatches(target, expected)) return true;

                Stream src;
                try { src = FindResource(); }
                catch (Exception ex)
                {
                    reason = "读取内置引擎失败：" + ex.Message;
                    Log.Warn(reason);
                    return false;
                }
                if (src == null)
                {
                    reason = "程序里没有内置引擎（资源 " + ResourceName + " 不存在）";
                    Log.Warn(reason);
                    return false;
                }

                Directory.CreateDirectory(Paths.Tools);
                Directory.CreateDirectory(Paths.Temp);

                // 先写临时文件再替换：中途崩溃也不会留下半个 DLL 冒充好文件
                var temp = Path.Combine(Paths.Temp, "cubiomes.dll.bundled");
                Util.TryDelete(temp);
                using (src)
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    src.CopyTo(fs, 81920);

                if (!HashMatches(temp, expected))
                {
                    Util.TryDelete(temp);
                    reason = "内置引擎已损坏：解出来的文件哈希与 Branding.EngineSha256 不一致";
                    Log.Warn(reason);
                    return false;
                }

                // 旧文件正在被占用（例如上一次的进程还没退干净）：删不掉就直接覆盖写
                try { if (File.Exists(target)) File.Delete(target); }
                catch (Exception ex) { Log.Warn("删除旧引擎失败，将尝试覆盖：" + ex.Message); }

                try { File.Move(temp, target); }
                catch (IOException)
                {
                    File.Copy(temp, target, true);
                    Util.TryDelete(temp);
                }

                Log.Info("已从程序内置资源解出种子地图引擎：" + Branding.EngineVersion);
                return true;
            }
            catch (Exception ex)
            {
                reason = "解出内置引擎失败：" + ex.Message;
                Log.Warn(reason);
                return false;
            }
        }

        /// <summary>文件的 SHA-256 是否等于期望值；期望值为空时只要求文件存在</summary>
        static bool HashMatches(string path, string expected)
        {
            if (!File.Exists(path)) return false;
            if (string.IsNullOrWhiteSpace(expected)) return true;
            try
            {
                return string.Equals(Util.Sha256File(path), expected, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Warn("校验引擎哈希失败：" + ex.Message);
                return false;
            }
        }
    }
}
