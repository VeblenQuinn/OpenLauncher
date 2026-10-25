using System.Collections.Generic;

namespace OpenLauncher.SeedMap
{
    /// <summary>一类可以在地图上标出来的结构。</summary>
    public sealed class StructureKind
    {
        /// <summary>ol_structure_type 的序号，必须和 olshim.c 里那张 case 表一致</summary>
        public int Index;
        /// <summary>界面显示名</summary>
        public string Name;
        /// <summary>所属维度：0 主世界 / -1 下界 / 1 末地</summary>
        public int Dimension;
        /// <summary>标记颜色（0xFFRRGGBB），同类结构统一</summary>
        public uint Color;
        /// <summary>写进设置的稳定标识</summary>
        public string Key;

        /// <summary>第一次打开这一页时默认勾上。只挑不太密集的，否则一屏几十个点看不清。</summary>
        public bool DefaultOn;

        public bool IsOverworld => Dimension == 0;
        public bool IsNether => Dimension == -1;
        public bool IsEnd => Dimension == 1;
    }

    /// <summary>
    /// 结构目录。顺序、颜色、维度都在这里定死，界面和扫描都从这张表取。
    ///
    /// 两类「不走区域格」的结构单独处理：
    /// · 埋藏的宝藏（Index 11）—— 按区块生成（getTreasurePos）。cubiomes 的
    ///   getStructurePos 对它能给出位置，实测 4096×4096 内 75 个，可以直接用。
    /// · 要塞（Index 19）—— 1.9 起按环形稀疏放置，没有区域格，必须走
    ///   ol_strongholds（initFirstStronghold / nextStronghold 顺序迭代）。
    ///   整个世界的要塞数量固定（1.9+ 为 128 个），CubiomesEngine 里按种子缓存。
    ///
    /// 故意不列的：紫水晶洞（每 4096 方块范围 1 万个，标出来全是噪点）、
    /// 下界化石（同样过密且慢）、末地小岛（cubiomes 对它返回 0）。
    /// </summary>
    public static class StructureCatalog
    {
        public static readonly StructureKind[] All =
        {
            new StructureKind { Index =  0, Name = "村庄",       Dimension =  0, Color = 0xFFF2C14E, Key = "village",        DefaultOn = true },
            new StructureKind { Index = 10, Name = "沉船",       Dimension =  0, Color = 0xFF7E57C2, Key = "shipwreck" },
            new StructureKind { Index = 12, Name = "海底废墟",   Dimension =  0, Color = 0xFF4DD0E1, Key = "ocean_ruin" },
            new StructureKind { Index =  5, Name = "海底神殿",   Dimension =  0, Color = 0xFF26C6DA, Key = "monument",       DefaultOn = true },
            new StructureKind { Index =  8, Name = "废弃传送门", Dimension =  0, Color = 0xFFAB47BC, Key = "ruined_portal" },
            new StructureKind { Index =  1, Name = "沙漠神殿",   Dimension =  0, Color = 0xFFE8B84B, Key = "desert_pyramid" },
            new StructureKind { Index =  2, Name = "丛林神庙",   Dimension =  0, Color = 0xFF66BB6A, Key = "jungle_temple" },
            new StructureKind { Index =  3, Name = "沼泽小屋",   Dimension =  0, Color = 0xFF8D6E63, Key = "swamp_hut" },
            new StructureKind { Index =  4, Name = "雪屋",       Dimension =  0, Color = 0xFFB0D8F0, Key = "igloo" },
            new StructureKind { Index =  6, Name = "林地府邸",   Dimension =  0, Color = 0xFFC0392B, Key = "mansion",        DefaultOn = true },
            new StructureKind { Index =  7, Name = "掠夺者前哨", Dimension =  0, Color = 0xFFEF6C00, Key = "outpost",        DefaultOn = true },
            new StructureKind { Index = 19, Name = "要塞",       Dimension =  0, Color = 0xFF8BC34A, Key = "stronghold",     DefaultOn = true },
            new StructureKind { Index = 15, Name = "试炼密室",   Dimension =  0, Color = 0xFF9CCC65, Key = "trial_chambers", DefaultOn = true },
            new StructureKind { Index = 14, Name = "古迹废墟",   Dimension =  0, Color = 0xFFD4A373, Key = "trail_ruins",    DefaultOn = true },
            new StructureKind { Index = 13, Name = "远古城市",   Dimension =  0, Color = 0xFF5C6BC0, Key = "ancient_city",   DefaultOn = true },
            new StructureKind { Index = 23, Name = "废弃营地",   Dimension =  0, Color = 0xFFA1887F, Key = "abandoned_camp", DefaultOn = true },
            new StructureKind { Index = 21, Name = "沙漠水井",   Dimension =  0, Color = 0xFFE0C080, Key = "desert_well" },
            new StructureKind { Index = 11, Name = "埋藏的宝藏", Dimension =  0, Color = 0xFFFFD54F, Key = "treasure" },
            new StructureKind { Index = 20, Name = "废弃矿井",   Dimension =  0, Color = 0xFF6D4C41, Key = "mineshaft" },
            new StructureKind { Index = 16, Name = "下界要塞",   Dimension = -1, Color = 0xFFFF5252, Key = "fortress",       DefaultOn = true },
            new StructureKind { Index = 17, Name = "堡垒遗迹",   Dimension = -1, Color = 0xFFFF9800, Key = "bastion",        DefaultOn = true },
            new StructureKind { Index =  9, Name = "废弃传送门", Dimension = -1, Color = 0xFFCE93D8, Key = "ruined_portal_n" },
            new StructureKind { Index = 18, Name = "末地城",     Dimension =  1, Color = 0xFFE040FB, Key = "end_city",       DefaultOn = true },
            new StructureKind { Index = 22, Name = "末地折跃门", Dimension =  1, Color = 0xFFB39DDB, Key = "end_gateway" },
        };

        /// <summary>当前维度下能找的结构（界面只显示这些）</summary>
        public static List<StructureKind> ForDimension(MapDimension dim)
        {
            var list = new List<StructureKind>();
            int d = (int)dim;
            foreach (var k in All) if (k.Dimension == d) list.Add(k);
            return list;
        }

        public static StructureKind ByIndex(int index)
        {
            foreach (var k in All) if (k.Index == index) return k;
            return null;
        }
    }
}
