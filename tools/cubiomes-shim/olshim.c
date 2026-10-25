/*
 * olshim.c —— 给 .NET 用的薄封装层
 *
 * 为什么需要这一层：
 *   cubiomes 的 Generator 是个大联合体，sizeof 高达 192200 字节（1.18+ 的
 *   BiomeNoise 独占 192168）。托管侧猜错这个大小就会写越界并把进程打崩，
 *   而且 allocCache() 返回的是 C 的 malloc 内存，不能拿 Marshal.FreeHGlobal 释放。
 *
 *   所以这里只暴露几个参数全是「整数 + 指针」的函数，Generator 与缓存的
 *   生命周期都在 C 侧管理，.NET 完全不需要知道任何结构体布局。
 */
#include <stdlib.h>
#include <string.h>
#include "generator.h"
#include "biomes.h"
#include "finders.h"

#define OL_API __declspec(dllexport)

/* 创建 / 销毁一个生成器实例（一次创建，多次渲染复用） */
OL_API void *ol_gen_new(void)
{
    return calloc(1, sizeof(Generator));
}

OL_API void ol_gen_free(void *p)
{
    free(p);
}

/* 编译期信息，便于托管侧自检 */
OL_API int ol_gen_sizeof(void)     { return (int)sizeof(Generator); }
OL_API int ol_newest_version(void) { return (int)MC_NEWEST; }
OL_API int ol_min_supported(void)  { return (int)MC_1_18; }

/*
 * 渲染一块群系图，结果写进 out（w*h 个 int，行优先，左上角开始）。
 * 返回 0 成功，-1 参数错误，-2 内存不足。
 *
 * mc    : cubiomes 的 MCVersion 枚举值（26.3 = 35）
 * dim   : -1 下界 / 0 主世界 / +1 末地
 * scale : 每像素代表的方块数（1/4/16/64/256）
 * bx,bz : 视口左上角的世界坐标（方块）
 * y     : 采样高度（按 scale 换算后传入 Range.y）
 */
OL_API int ol_render(void *gen, int mc, int dim, unsigned long long seed,
                     int scale, int bx, int bz, int w, int h, int y, int *out)
{
    if (!gen || !out || w <= 0 || h <= 0 || scale <= 0) return -1;

    Generator *g = (Generator *)gen;
    setupGenerator(g, mc, 0);
    applySeed(g, dim, seed);

    Range r;
    r.scale = scale;
    r.x = bx / scale;
    r.z = bz / scale;
    r.sx = w;
    r.sz = h;
    r.y = y;
    r.sy = 1;

    int *cache = allocCache(g, r);
    if (!cache) return -2;

    genBiomes(g, cache, r);
    memcpy(out, cache, (size_t)w * (size_t)h * sizeof(int));
    free(cache);   /* allocCache 用的是 malloc，必须 free */
    return 0;
}

/* 单点查询，失败返回 -1 */
OL_API int ol_biome_at(void *gen, int mc, int dim, unsigned long long seed,
                       int scale, int x, int y, int z)
{
    if (!gen) return -1;
    Generator *g = (Generator *)gen;
    setupGenerator(g, mc, 0);
    applySeed(g, dim, seed);
    return getBiomeAt(g, scale, x, y, z);
}

/* 结构位置：成功返回 1 并把方块坐标写进 out[0]=x, out[1]=z */
OL_API int ol_structure_pos(int structType, int mc, unsigned long long seed,
                            int regX, int regZ, int *out)
{
    Pos pos;
    if (getStructurePos(structType, mc, seed, regX, regZ, &pos) == 0) return 0;
    if (out) { out[0] = pos.x; out[1] = pos.z; }
    return 1;
}

OL_API int ol_viable_structure(void *gen, int structType, int mc, int dim,
                               unsigned long long seed, int x, int z)
{
    if (!gen) return 0;
    Generator *g = (Generator *)gen;
    setupGenerator(g, mc, 0);
    applySeed(g, dim, seed);
    return isViableStructurePos(structType, g, x, z, 0);
}

/*
 * 批量扫描某一类结构，写进 out（每项两个 int：x, z）。返回命中的个数。
 *
 * 为什么需要它：isViableStructurePos 每次调用都要 setupGenerator + applySeed，
 * 对 1.18+ 来说等于重建 192KB 的 BiomeNoise。若托管侧对每个候选点单独调一次，
 * 扫一片区域就要几百次重建，慢到不可用。这里只重建一次，后面全在同一个
 * Generator 上做合法性判定。
 *
 * structType 用 ol_structure_type() 换算；矩形是方块坐标（含边界）。
 * 结果数量超过 maxOut 时直接返回 maxOut（调用方自己判断是否要缩小范围）。
 *
 * 注意区域大小：结构按 (regionSize 个区块) 为一格放置，而 regionSize 因结构而异
 * （村庄 34 区块 = 544 方块，林地府邸 80 区块 = 1280 方块……）。所以必须
 * getStructureConfig 取真实值，不能按 512 方块硬算——算错的区域编号在原点附近
 * 还能蒙对几个，坐标一大就整体错位，取到的位置全落在查询矩形之外。
 */
OL_API int ol_structure_scan(void *gen, int structType, int mc, int dim,
                             unsigned long long seed,
                             int x0, int z0, int x1, int z1,
                             int *out, int maxOut)
{
    if (!gen || !out || maxOut <= 0) return -1;
    if (x1 < x0 || z1 < z0) return -1;

    StructureConfig sc;
    if (!getStructureConfig(structType, mc, &sc)) return -2;

    Generator *g = (Generator *)gen;
    setupGenerator(g, mc, 0);
    applySeed(g, dim, seed);

    /* 一格区域覆盖 rc 个方块；区域编号是有符号的，用 floordiv 才不会在负坐标错位 */
    int rc = sc.regionSize << 4;
    int rx0 = floordiv(x0, rc), rx1 = floordiv(x1, rc);
    int rz0 = floordiv(z0, rc), rz1 = floordiv(z1, rc);

    int n = 0;
    for (int rz = rz0; rz <= rz1; rz++)
    {
        for (int rx = rx0; rx <= rx1; rx++)
        {
            Pos p;
            if (!getStructurePos(structType, mc, seed, rx, rz, &p)) continue;
            if (p.x < x0 || p.x > x1 || p.z < z0 || p.z > z1) continue;
            if (!isViableStructurePos(structType, g, p.x, p.z, 0)) continue;
            if (n >= maxOut) return n;
            out[2 * n] = p.x;
            out[2 * n + 1] = p.z;
            n++;
        }
    }
    return n;
}

/* 结构类型的枚举值，供托管侧传参（避免硬编码猜错） */
OL_API int ol_structure_type(int which)
{
    switch (which)
    {
        case 0: return Village;
        case 1: return Desert_Pyramid;
        case 2: return Jungle_Temple;
        case 3: return Swamp_Hut;
        case 4: return Igloo;
        case 5: return Monument;
        case 6: return Mansion;
        case 7: return Outpost;
        case 8: return Ruined_Portal;
        case 9: return Ruined_Portal_N;
        case 10: return Shipwreck;
        case 11: return Treasure;
        case 12: return Ocean_Ruin;
        case 13: return Ancient_City;
        case 14: return Trail_Ruins;
        case 15: return Trial_Chambers;
        case 16: return Fortress;
        case 17: return Bastion;
        case 18: return End_City;
        case 19: return Stronghold;
        /* 下面这些是 0.1.24 补的：之前目录里漏了，但 cubiomes 完全支持 */
        case 20: return Mineshaft;
        case 21: return Desert_Well;
        case 22: return End_Gateway;
        case 23: return Abandoned_Camp;
        default: return -1;
    }
}

/*
 * 要塞（Stronghold）不能走 getStructurePos：1.9 起它按「环形」稀疏分布，
 * 位置由 initFirstStronghold / nextStronghold 这套迭代器给出，没有区域格。
 *
 * 世界要塞总数固定：1.9+ 是 128 个。迭代器是**顺序**的——想知道第 N 个的准确
 * 位置，必须先把前 N-1 个都走一遍，所以这里只能全量迭代再按半径过滤。
 *
 * 慢点：1.19.3+ 的每个要塞都要 locateBiome 搜索 ±112 方块，实测 128 个约
 * 1.5~3 秒。绝对不能在外层循环里迭代更多次（早先误写成 512 次，直接卡死）。
 *
 * 返回命中个数；-1 参数错误，-2 这个版本不支持。
 */
OL_API int ol_strongholds(void *gen, int mc, unsigned long long seed,
                          int centerX, int centerZ, int radius,
                          int *out, int maxOut)
{
    if (!gen || !out || maxOut <= 0) return -1;
    if (mc < MC_1_9) return -2;

    Generator *g = (Generator *)gen;
    setupGenerator(g, mc, 0);
    applySeed(g, 0, seed);

    StrongholdIter sh;
    initFirstStronghold(&sh, mc, seed);

    const int total = 128;   /* 1.9+ */
    int n = 0;
    for (int i = 0; i < total; i++)
    {
        if (nextStronghold(&sh, g) <= 0) break;

        int dx = sh.pos.x - centerX, dz = sh.pos.z - centerZ;
        if (dx > -radius && dx < radius && dz > -radius && dz < radius)
        {
            if (n >= maxOut) return n;
            out[2 * n] = sh.pos.x;
            out[2 * n + 1] = sh.pos.z;
            n++;
        }
    }
    return n;
}
