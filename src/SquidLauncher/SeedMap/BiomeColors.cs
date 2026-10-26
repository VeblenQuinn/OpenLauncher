using System.Collections.Generic;

namespace SquidLauncher.SeedMap
{
    /// <summary>
    /// 生物群系 ID → 颜色。
    ///
    /// ID 与 cubiomes 的 BiomeID 枚举一致（同时也是游戏 1.18+ 的数据包 ID 顺序）。
    /// 配色参考原版地图色调，偏向「一眼能分辨地形」而不是完全还原方块颜色。
    /// </summary>
    public static class BiomeColors
    {
        static readonly Dictionary<int, int> Table = new Dictionary<int, int>();

        static int Rgb(int r, int g, int b) => unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;

        static BiomeColors()
        {
            // ---- 海洋 ----
            Table[0] = Rgb(0x30, 0x5A, 0xC0);
            Table[10] = Rgb(0x90, 0xA0, 0xF0);
            Table[24] = Rgb(0x18, 0x36, 0x8A);
            Table[44] = Rgb(0x43, 0xD5, 0xEE);
            Table[45] = Rgb(0x45, 0xAD, 0xF2);
            Table[46] = Rgb(0x3D, 0x57, 0xD6);
            Table[47] = Rgb(0x02, 0xA6, 0xA6);
            Table[48] = Rgb(0x0A, 0x7F, 0xC1);
            Table[49] = Rgb(0x20, 0x3C, 0x9E);
            Table[50] = Rgb(0x39, 0x38, 0xC4);

            // ---- 陆地 ----
            Table[1] = Rgb(0x8D, 0xB3, 0x60);
            Table[2] = Rgb(0xFA, 0x94, 0x18);
            Table[3] = Rgb(0x60, 0x67, 0x67);
            Table[4] = Rgb(0x05, 0x69, 0x33);
            Table[5] = Rgb(0x0B, 0x66, 0x59);
            Table[6] = Rgb(0x07, 0xF9, 0x9B);
            Table[7] = Rgb(0x6F, 0x94, 0xFF);
            Table[11] = Rgb(0xA0, 0xA0, 0xFF);
            Table[12] = Rgb(0xFF, 0xFF, 0xFF);
            Table[13] = Rgb(0xA0, 0xA0, 0xA0);
            Table[14] = Rgb(0xFF, 0x00, 0xFF);
            Table[15] = Rgb(0xA0, 0x00, 0xFF);
            Table[16] = Rgb(0xF7, 0xE9, 0xA3);
            Table[17] = Rgb(0xD9, 0x45, 0x15);
            Table[18] = Rgb(0x22, 0x5B, 0x22);
            Table[19] = Rgb(0x16, 0x3D, 0x33);
            Table[20] = Rgb(0x50, 0x56, 0x56);
            Table[21] = Rgb(0x53, 0x70, 0x09);
            Table[22] = Rgb(0x2C, 0x42, 0x05);
            Table[23] = Rgb(0x62, 0x88, 0x11);
            Table[25] = Rgb(0x8E, 0x8E, 0x8E);
            Table[26] = Rgb(0xFA, 0xF0, 0xC0);
            Table[27] = Rgb(0x30, 0x7C, 0x44);
            Table[28] = Rgb(0x1F, 0x5F, 0x31);
            Table[29] = Rgb(0x40, 0x59, 0x32);
            Table[30] = Rgb(0x31, 0x56, 0x68);
            Table[31] = Rgb(0x24, 0x46, 0x52);
            Table[32] = Rgb(0x59, 0x69, 0x28);
            Table[33] = Rgb(0x41, 0x4F, 0x1A);
            Table[34] = Rgb(0x50, 0x7B, 0x5B);
            Table[35] = Rgb(0xBD, 0xB2, 0x5F);
            Table[36] = Rgb(0xA5, 0x9A, 0x49);
            Table[37] = Rgb(0xD9, 0x45, 0x15);
            Table[38] = Rgb(0xE8, 0x8E, 0x32);
            Table[39] = Rgb(0xCA, 0x8C, 0x2A);
            Table[127] = Rgb(0x00, 0x00, 0x00);
            Table[163] = Rgb(0x8D, 0xB3, 0x60); // sunflower_plains

            // ---- 末地 ----
            Table[9] = Rgb(0x80, 0x80, 0x80);
            Table[40] = Rgb(0x28, 0x28, 0x28);
            Table[41] = Rgb(0x20, 0x20, 0x20);
            Table[42] = Rgb(0x18, 0x18, 0x18);
            Table[43] = Rgb(0x10, 0x10, 0x10);

            // ---- 下界 ----
            Table[8] = Rgb(0xBF, 0x3B, 0x3B);
            Table[170] = Rgb(0x5E, 0x38, 0x38);
            Table[171] = Rgb(0xDD, 0x08, 0x08);
            Table[172] = Rgb(0x16, 0x7E, 0x8C);
            Table[173] = Rgb(0x52, 0x52, 0x52);

            // ---- 1.14 ~ 1.21 ----
            Table[168] = Rgb(0x43, 0xB8, 0x2B);
            Table[169] = Rgb(0x2E, 0x8C, 0x1C);
            Table[174] = Rgb(0x8B, 0x5D, 0x4A);
            Table[175] = Rgb(0x63, 0xB0, 0x3A);
            Table[177] = Rgb(0x83, 0xBB, 0x54);
            Table[178] = Rgb(0x5A, 0x7B, 0x6E);
            Table[179] = Rgb(0xF0, 0xF0, 0xFF);
            Table[180] = Rgb(0xB0, 0xA0, 0x9A);
            Table[181] = Rgb(0xC0, 0xD0, 0xE0);
            Table[182] = Rgb(0x9A, 0x9A, 0x9A);
            Table[183] = Rgb(0x0A, 0x0A, 0x14);
            Table[184] = Rgb(0x2C, 0x75, 0x3B);
            Table[185] = Rgb(0xF2, 0xB2, 0xC4);
            Table[186] = Rgb(0x7A, 0x86, 0x8A); // pale_garden（1.21 Winter Drop）
            Table[187] = Rgb(0xC8, 0xA8, 0x32); // sulfur_caves（26.2）
            Table[188] = Rgb(0x5C, 0x7D, 0x4A); // dappled_forest（26.3）
        }

        public static int Argb(int biomeId)
            => Table.TryGetValue(biomeId, out var c) ? c : Rgb(0xFF, 0x00, 0x00); // 未知 ID 标红，方便发现版本不匹配

        /// <summary>
        /// 群系 ID → 中文名。覆盖 Table 里出现的全部 ID，正常情况不会走到最后的兜底。
        /// 名字用原版 zh_cn 的译名，方便和游戏里对上。
        /// </summary>
        public static string Name(int biomeId) => biomeId switch
        {
            // ---- 海洋 ----
            0 => "海洋", 10 => "冻洋", 24 => "深海", 44 => "暖水海洋", 45 => "温水海洋",
            46 => "冷水海洋", 47 => "暖水深海", 48 => "温水深海", 49 => "冷水深海",
            50 => "封冻深海",

            // ---- 平原 / 森林 ----
            1 => "平原", 4 => "森林", 27 => "白桦森林", 28 => "白桦森林丘陵",
            29 => "黑森林", 34 => "疏林山地", 163 => "向日葵平原",

            // ---- 沙漠 / 恶地 ----
            2 => "沙漠", 17 => "沙漠丘陵", 37 => "恶地", 38 => "疏林恶地高原", 39 => "恶地高原",

            // ---- 山地 ----
            3 => "山地", 13 => "雪山", 18 => "疏林丘陵", 20 => "山地边缘",
            25 => "石岸", 179 => "积雪山坡", 180 => "尖峭山峰", 181 => "冰封山峰",
            182 => "裸岩山峰",

            // ---- 针叶林 ----
            5 => "针叶林", 19 => "针叶林丘陵", 30 => "积雪的针叶林",
            31 => "积雪的针叶林丘陵", 32 => "巨型针叶林", 33 => "巨型针叶林丘陵",
            178 => "雪林",

            // ---- 丛林 ----
            21 => "丛林", 22 => "丛林丘陵", 23 => "丛林边缘", 168 => "竹林", 169 => "竹林丘陵",

            // ---- 沼泽 / 河流 / 雪原 ----
            6 => "沼泽", 7 => "河流", 11 => "冻河", 12 => "雪原", 26 => "积雪的沙滩",
            184 => "红树林沼泽", 185 => "樱花树林", 186 => "苍白之园",

            // ---- 沙漠 / 热带草原 ----
            35 => "热带草原", 36 => "热带高原",

            // ---- 蘑菇岛 / 海滩 / 洞穴 ----
            14 => "蘑菇岛", 15 => "蘑菇岛岸", 16 => "沙滩",
            174 => "溶洞", 175 => "繁茂洞穴", 183 => "深暗之域",
            177 => "草甸", 187 => "硫磺洞穴", 188 => "斑斓森林",

            // ---- 下界 ----
            8 => "下界荒地", 170 => "灵魂沙峡谷", 171 => "绯红森林", 172 => "诡异森林",
            173 => "玄武岩三角洲",

            // ---- 末地 ----
            9 => "末地", 40 => "末地小型岛屿", 41 => "末地内陆", 42 => "末地高地",
            43 => "末地荒地", 127 => "虚空",

            _ => "未知群系 #" + biomeId,
        };
    }
}
