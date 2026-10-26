using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SquidLauncher.Core;

namespace SquidLauncher.SeedMap
{
    /// <summary>地图维度</summary>
    public enum MapDimension { Overworld = 0, Nether = -1, End = 1 }

    /// <summary>一次渲染的结果：像素用于显示，群系 id 用于悬停查询</summary>
    public sealed class MapRender
    {
        public int Width, Height;
        public int[] Pixels;
        public int[] BiomeIds;
    }

    /// <summary>
    /// cubiomes 引擎的托管封装。
    ///
    /// cubiomes 是 MIT 许可的 C 库，生物群系生成与游戏逐位一致。启动器用的是
    /// 社区维护的分支（xpple / Conflux-Union）：上游只到 1.21 Winter Drop，
    /// 这个分支已经合入 MC_26_3 和 26.2/26.3 的新群系。
    ///
    /// 这里**不直接 P/Invoke cubiomes 的原生 API**，而是走 DLL 里的 ol_* 薄封装：
    /// cubiomes 的 Generator 结构 sizeof 高达 192200 字节（1.18+ 的 BiomeNoise
    /// 独占 192168），托管侧猜错大小就会写越界崩掉进程；allocCache() 返回的又是
    /// C 的 malloc 内存，不能拿 Marshal.FreeHGlobal 释放。把结构体布局和内存
    /// 生命周期都留在 C 侧、托管侧只传整数和数组，就没有踩坑的余地了。
    ///
    /// DLL 放在 数据目录\tools\cubiomes.dll，第一次打开种子地图时自动下载。
    /// </summary>
    public static class CubiomesEngine
    {
        public static string DllPath => Path.Combine(Paths.Tools, "cubiomes.dll");

        public static bool Available { get; private set; }
        public static string LoadError { get; private set; }

        /// <summary>引擎支持的最新版本枚举值（DLL 自报，用来校验版本表是否同步）</summary>
        public static int NewestVersion { get; private set; } = -1;

        const string DllName = "cubiomes.dll";

        // ---------- 原生导出（ol_* 来自 tools/cubiomes-shim/olshim.c） ----------
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr ol_gen_new();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern void ol_gen_free(IntPtr g);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern int ol_gen_sizeof();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern int ol_newest_version();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern int ol_render(IntPtr g, int mc, int dim, ulong seed,
            int scale, int bx, int bz, int w, int h, int y, int[] outIds);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern int ol_biome_at(IntPtr g, int mc, int dim, ulong seed,
            int scale, int x, int y, int z);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern int ol_structure_pos(int structType, int mc, ulong seed,
            int regX, int regZ, int[] outXZ);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern int ol_viable_structure(IntPtr g, int structType, int mc, int dim,
            ulong seed, int x, int z);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern int ol_structure_type(int which);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern int ol_structure_scan(IntPtr g, int structType, int mc, int dim,
            ulong seed, int x0, int z0, int x1, int z1, int[] outBuf, int maxOut);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern int ol_strongholds(IntPtr g, int mc, ulong seed,
            int centerX, int centerZ, int radius, int[] outBuf, int maxOut);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetDllDirectoryW(string path);

        /// <summary>预加载指定路径的 DLL，让按名字查找命中它（而不是误中 PATH 里的同名文件）</summary>
        static bool Preload()
        {
            if (!File.Exists(DllPath)) { LoadError = "还没下载生成引擎"; return false; }
            SetDllDirectoryW(Paths.Tools);
            if (LoadLibraryW(DllPath) == IntPtr.Zero)
            {
                var code = Marshal.GetLastWin32Error();
                LoadError = code == 193
                    ? "引擎文件不完整或不是有效的 DLL（错误码 193），请重启程序自动解出"
                    : "引擎加载失败（错误码 " + code + "）";
                return false;
            }
            return true;
        }

        static CubiomesEngine()
        {
            Reload();
        }

        /// <summary>重新尝试加载（下载完成、或用户手动放入 DLL 之后调用）</summary>
        public static void Reload()
        {
            Available = false;
            LoadError = null;
            NewestVersion = -1;
            try
            {
                if (!Preload()) return;
                // 触摸一次导出函数：DLL 能加载不代表 ABI 对得上，这里做真正的自检
                var size = ol_gen_sizeof();
                if (size <= 0) { LoadError = "引擎自检失败：Generator 大小异常"; return; }
                NewestVersion = ol_newest_version();
                var probe = ol_gen_new();
                if (probe == IntPtr.Zero) { LoadError = "引擎自检失败：无法分配生成器"; return; }
                ol_gen_free(probe);
                Available = true;
                InvalidateStrongholds();
                ProbeStructureScan();
                Log.Info($"种子地图引擎就绪：Generator={size}B，最新版本枚举={NewestVersion}，结构扫描={SupportsStructureScan}，要塞迭代={SupportsStrongholds}");
            }
            catch (Exception ex)
            {
                LoadError = "引擎加载失败：" + ex.Message;
                Log.Warn("cubiomes 加载失败：" + ex);
            }
        }

        /// <summary>
        /// 每个线程一个 Generator 实例。渲染在线程池上跑，共享实例会有数据竞争；
        /// 而 192KB 的 calloc 也不值得每次渲染都做一遍。
        /// </summary>
        [ThreadStatic] static IntPtr _gen;

        static IntPtr Gen
        {
            get
            {
                if (_gen == IntPtr.Zero) _gen = ol_gen_new();
                return _gen;
            }
        }

        /// <summary>渲染一块生物群系图。scale：每像素多少方块。失败返回 null。</summary>
        public static MapRender RenderBiomes(int mcVersion, MapDimension dim, long seed,
            int scale, int blockX, int blockZ, int width, int height)
        {
            if (!Available || width <= 0 || height <= 0 || scale <= 0) return null;

            try
            {
                var ids = new int[width * height];
                // 采样高度固定在 y≈64（地表附近）：1.18+ 的高度会参与 depth 气候参数，
                // 取深了算地下群系、取高了偏向山地。按 scale 换算回同一个方块高度。
                int y = Math.Max(1, 64 / scale);
                int rc = ol_render(Gen, mcVersion, (int)dim, unchecked((ulong)seed),
                    scale, blockX, blockZ, width, height, y, ids);
                if (rc != 0)
                {
                    Log.Warn("渲染群系失败，返回码 " + rc);
                    return null;
                }

                var pixels = new int[ids.Length];
                for (int i = 0; i < ids.Length; i++)
                    pixels[i] = BiomeColors.Argb(ids[i]);

                return new MapRender { Width = width, Height = height, Pixels = pixels, BiomeIds = ids };
            }
            catch (Exception ex)
            {
                Log.Warn("渲染群系异常：" + ex.Message);
                return null;
            }
        }

        /// <summary>单点查询群系 id，失败返回 -1</summary>
        public static int BiomeAt(int mcVersion, MapDimension dim, long seed, int scale, int blockX, int blockZ)
        {
            if (!Available) return -1;
            try
            {
                return ol_biome_at(Gen, mcVersion, (int)dim, unchecked((ulong)seed),
                    scale, blockX, 64, blockZ);
            }
            catch { return -1; }
        }

        /// <summary>
        /// 在圆形范围内找某类结构，返回世界坐标列表。
        /// structureIndex 是 olshim 里 ol_structure_type 的序号（见 StructureCatalog）。
        /// 逐个候选点做合法性判定，每个点都会重建 Generator，所以只适合零星查询；
        /// 扫一片区域请用 ScanStructures。
        /// </summary>
        public static List<(int x, int z)> FindStructures(int mcVersion, int structureIndex,
            long seed, int centerX, int centerZ, int radiusBlocks)
        {
            var list = new List<(int, int)>();
            if (!Available) return list;

            try
            {
                int structType = ol_structure_type(structureIndex);
                if (structType < 0) return list;

                int regions = radiusBlocks / 512 + 1;
                int cx = centerX / 512, cz = centerZ / 512;
                var outXZ = new int[2];
                for (int rz = cz - regions; rz <= cz + regions; rz++)
                {
                    for (int rx = cx - regions; rx <= cx + regions; rx++)
                    {
                        if (ol_structure_pos(structType, mcVersion, unchecked((ulong)seed), rx, rz, outXZ) == 0) continue;
                        if (Math.Abs(outXZ[0] - centerX) > radiusBlocks || Math.Abs(outXZ[1] - centerZ) > radiusBlocks) continue;
                        if (ol_viable_structure(Gen, structType, mcVersion, 0,
                                unchecked((ulong)seed), outXZ[0], outXZ[1]) == 0) continue;
                        list.Add((outXZ[0], outXZ[1]));
                    }
                }
            }
            catch (Exception ex) { Log.Warn("查找结构失败：" + ex.Message); }
            return list;
        }

        /// <summary>结构序号 → 显示名。顺序必须和 olshim.c 的 ol_structure_type 一致。</summary>
        public static readonly string[] StructureNames =
        {
            "村庄", "沙漠神殿", "丛林神庙", "沼泽小屋", "雪屋", "海底神殿", "林地府邸",
            "掠夺者前哨", "废弃传送门", "下界传送门", "沉船", "埋藏的宝藏", "海底废墟",
            "远古城市", "古迹废墟", "试炼密室", "下界要塞", "堡垒遗迹", "末地城", "要塞",
            "废弃矿井", "沙漠水井", "末地折跃门", "废弃营地",
        };

        /// <summary>
        /// 批量扫描矩形区域内的某一类结构，返回 (x,z) 列表。
        ///
        /// 走 ol_structure_scan：整个扫描只重建一次 Generator，里面循环
        /// getStructurePos + isViableStructurePos。实测主世界 18 类结构扫
        /// 4096×4096 方块合计约 17ms，所以可以随着视图刷新而重扫。
        ///
        /// 引擎太旧、没有这个导出时返回 null（调用方据此提示更新引擎）。
        /// </summary>
        public static List<(int x, int z)> ScanStructures(int mcVersion, int structureIndex,
            MapDimension dim, long seed, int x0, int z0, int x1, int z1, int maxHits)
        {
            if (!Available) return null;
            if (x1 < x0 || z1 < z0 || maxHits <= 0) return new List<(int, int)>();

            try
            {
                int structType = ol_structure_type(structureIndex);
                if (structType < 0) return new List<(int, int)>();

                var buf = new int[2 * maxHits];
                int n = ol_structure_scan(Gen, structType, mcVersion, (int)dim,
                    unchecked((ulong)seed), x0, z0, x1, z1, buf, maxHits);

                var list = new List<(int, int)>(Math.Max(0, n));
                for (int i = 0; i < n; i++) list.Add((buf[2 * i], buf[2 * i + 1]));
                return list;
            }
            catch (EntryPointNotFoundException)
            {
                return null;   // 旧引擎
            }
            catch (Exception ex)
            {
                Log.Warn("扫描结构失败：" + ex.Message);
                return new List<(int, int)>();
            }
        }

        /// <summary>引擎是否带批量结构扫描导出</summary>
        public static bool SupportsStructureScan { get; private set; }

        /// <summary>
        /// 要塞（Stronghold）位置。它不走区域格：1.9 起按环形稀疏分布，必须用
        /// initFirstStronghold / nextStronghold 顺序迭代。整个世界的要塞总数固定
        /// （1.9+ 为 128 个），而且是**顺序**的——要拿第 N 个就得先把前面全算一遍，
        /// 每个要塞都要 locateBiome 搜 ±112 方块，实测 128 个约 1.6 秒。
        ///
        /// 所以这里按 (版本, 种子) 把全世界 128 个位置缓存起来，之后按视野过滤是纯内存操作。
        /// </summary>
        public static List<(int x, int z)> ScanStrongholds(int mcVersion, MapDimension dim, long seed,
            int x0, int z0, int x1, int z1, int maxHits)
        {
            if (!Available) return null;
            if (x1 < x0 || z1 < z0 || maxHits <= 0) return new List<(int, int)>();
            if (dim != MapDimension.Overworld) return new List<(int, int)>();

            List<(int x, int z)> all;
            lock (_shLock)
            {
                if (!_shCacheValid || _shCacheMc != mcVersion || _shCacheSeed != seed)
                {
                    all = LoadAllStrongholds(mcVersion, seed);
                    if (all == null) return null;          // 旧引擎
                    _shCache = all;
                    _shCacheMc = mcVersion;
                    _shCacheSeed = seed;
                    _shCacheValid = true;
                }
                all = _shCache;
            }

            var list = new List<(int, int)>();
            foreach (var p in all)
            {
                if (p.x < x0 || p.x > x1 || p.z < z0 || p.z > z1) continue;
                if (list.Count >= maxHits) break;
                list.Add(p);
            }
            return list;
        }

        static List<(int x, int z)> _shCache;
        static int _shCacheMc = int.MinValue;
        static long _shCacheSeed = long.MinValue;
        static bool _shCacheValid;
        static readonly object _shLock = new object();

        /// <summary>算出全世界的要塞位置（不缓存版本，供上面的缓存填充）</summary>
        static List<(int x, int z)> LoadAllStrongholds(int mcVersion, long seed)
        {
            try
            {
                const int cap = 256;   // 1.9+ 是 128，留余量
                var buf = new int[2 * cap];
                int n = ol_strongholds(Gen, mcVersion, unchecked((ulong)seed),
                    0, 0, 30000000, buf, cap);

                var list = new List<(int, int)>(Math.Max(0, n));
                for (int i = 0; i < n; i++) list.Add((buf[2 * i], buf[2 * i + 1]));
                return list;
            }
            catch (EntryPointNotFoundException)
            {
                SupportsStrongholds = false;
                return null;   // 旧引擎
            }
            catch (Exception ex)
            {
                Log.Warn("计算要塞位置失败：" + ex.Message);
                return new List<(int, int)>();
            }
        }

        /// <summary>引擎是否带要塞迭代导出</summary>
        public static bool SupportsStrongholds { get; private set; }

        /// <summary>换种子/版本后丢缓存（可选，ScanStrongholds 会自己比对）</summary>
        public static void InvalidateStrongholds()
        {
            lock (_shLock) { _shCacheValid = false; _shCache = null; }
        }

        /// <summary>探测 ol_structure_scan / ol_strongholds 是否存在（旧引擎缺这些导出）</summary>
        static void ProbeStructureScan()
        {
            try
            {
                // 参数故意非法：存在就返回 -1，不存在会抛 EntryPointNotFoundException
                ol_structure_scan(IntPtr.Zero, 0, 0, 0, 0, 0, 0, 0, 0, null, 0);
                SupportsStructureScan = true;
            }
            catch (EntryPointNotFoundException) { SupportsStructureScan = false; }
            catch (Exception) { SupportsStructureScan = true; }   // 其它异常说明符号在

            try
            {
                ol_strongholds(IntPtr.Zero, 0, 0, 0, 0, 0, null, 0);
                SupportsStrongholds = true;
            }
            catch (EntryPointNotFoundException) { SupportsStrongholds = false; }
            catch (Exception) { SupportsStrongholds = true; }
        }
    }
}
