namespace OpenLauncher
{
    /// <summary>
    /// 换名字、换仓库、换维护者，只改这一个文件。
    /// 任何人 fork 之后改掉这里 + 换成自己的签名公钥，就是一个独立发行版。
    /// </summary>
    public static class Branding
    {
        public const string AppName = "OpenLauncher";
        public const string DisplayName = "OpenLauncher";
        public const string Tagline = "去中心化 Minecraft 启动器";

        /// <summary>主页 / 源码下载（仅用于"关于"页面和 User-Agent，不是更新的唯一来源）</summary>
        public const string RepoUrl = "https://github.com/VeblenQuinn/OpenLauncher";

        /// <summary>
        /// 内置的更新清单地址。任意一个能访问到、且签名验证通过就行，
        /// 用户也可以在 设置 → 更新 里自己加镜像。顺序不代表优先级：会全部尝试并取版本最高的有效清单。
        /// </summary>
        public static readonly string[] DefaultUpdateSources =
        {
            "https://raw.githubusercontent.com/VeblenQuinn/OpenLauncher/main/release/update.json",
            "https://cdn.jsdelivr.net/gh/VeblenQuinn/OpenLauncher@main/release/update.json",
            "https://fastly.jsdelivr.net/gh/VeblenQuinn/OpenLauncher@main/release/update.json",
            "https://gitee.com/veblenquinn1/OpenLauncher/raw/main/release/update.json",
            "https://ghfast.top/https://raw.githubusercontent.com/VeblenQuinn/OpenLauncher/main/release/update.json",
        };

        /// <summary>
        /// 种子地图引擎（cubiomes.dll）的下载地址。**默认留空**：引擎已经打包在 exe 里，
        /// 不需要也不应该从网络取。留空时界面上的「从网络下载」选项直接不出现。
        ///
        /// 需要的话可以填一个镜像地址，界面会多出「从网络下载」按钮，但**始终由用户点**，
        /// 程序不会自动联网。引擎用的是社区 cubiomes 分支（xpple / Conflux-Union）：
        /// 上游 Cubitect/cubiomes 只到 1.21 Winter Drop，这个分支已经合入 MC_26_3。
        /// </summary>
        public const string EngineUrl = "";

        /// <summary>
        /// 引擎的备用地址（EngineUrl 连不上时依次尝试）。EngineUrl 留空时这里也不起作用。
        /// </summary>
        public static readonly string[] EngineMirrors = new string[0];

        /// <summary>引擎版本号（显示用；换了引擎就改这里，它也是「旧引擎」的判据）</summary>
        public const string EngineVersion = "26.3 (cubiomes MC_26_3, struct-scan r3)";

        /// <summary>
        /// 引擎的 SHA-256。内置引擎解出来后会校验这个值；留空则跳过校验。
        /// 换 Assets\cubiomes.dll 后这里必须同步更新。
        /// </summary>
        public const string EngineSha256 = "a0847054518196b34caa7a7e7164e06b6aa093e39e4499f959c2833bb6c2e82f";

        /// <summary>
        /// 内置信任的发布公钥（RSA，.NET XML 格式）。用 tools/Release 的 keygen 生成。
        /// 更新清单必须由其中任意一把私钥签名才会被接受。
        /// 维护者失联时，社区可以发布新公钥，由用户在 设置 → 更新 → 信任的发布者 里手动添加。
        /// </summary>
        public static readonly string[] TrustedKeys =
        {
            // 指纹 68c5befabe5c9b5f（OpenLauncher 初始发布钥匙）
            "<RSAKeyValue><Modulus>y7Y66yR9SQWglkxY7pOyEmJtHkU/NkmBNbPAy5aC/cGqd+XRTowb4+etiqeW6jSzdYRqDm62bNxTwyRqsleTxsc3ozIj+pIs2+3ZkP1gLiE6IIQTnxeb4bYK8YdcBW1EwDSP3CFg/JgWQ3GSn5bLoDLsve/sCZHdyvj5pUAcrvHs8ZvSKvnh2EhlsaCRWIivEFR21CYM0X7/izdRa7GnrAKc+5qt8YKKfD19+xqhR4xsEtY0bK4PGLByocjgYnZiZLhRk0yu4v8JpsLj7uDKFt+oy17JBu8+XnX6vj49wnGm7jDD8qPcTJdVnayITG7v2tuipaei9g3UDaJ0QQTfMpA2E6wdx2gQVO03dNqWPA55vrXbSRiKofaWHLadc3f2Bq5Wbtn0QlWJIaDaUfwcuQXroTzGYF0TJS35uXkUr83akzmaBDOUmpE/mfxM9HOp2Orvw/00PMYvuTkSVGzQQy37jYynrnXfEIuwoTd6/GiAyBuu9ErxkAazZmgN3lHV</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>",
        };

        /// <summary>
        /// 微软正版登录用的 Azure 应用 Client ID。
        /// 需要自己在 Azure 注册应用并向 Mojang 申请 Minecraft API 权限后填入；
        /// 留空时用户可在 设置 → 账户 里填自己的。
        /// </summary>
        public const string DefaultMicrosoftClientId = "";

        /// <summary>CurseForge API Key（可选）。留空时自动走无需 Key 的镜像。</summary>
        public const string DefaultCurseForgeApiKey = "";
    }
}
