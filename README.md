<p align="center"><img src="src/OpenLauncher/Assets/icon.png" width="96" style="image-rendering:pixelated"></p>

<h1 align="center">OpenLauncher</h1>
<p align="center">去中心化的 Minecraft 启动器 · 没有服务器 · 单个 exe · GPL-3.0</p>

---

**我们不包更新，但保证你不需要我们也能一直用、一直更新。**

- 游戏、加载器、Mod 全部直接从官方源和公益镜像下载，版本列表实时获取，启动器不内置任何版本号。
- 启动器自己的更新走**签名清单**：任何人都能镜像分发，只有签名对得上才会安装；维护者失联时社区可以换钥匙接手。
- 不收集任何数据。账号令牌用 Windows DPAPI 加密，只存在你这台电脑上。

## 功能

| 模块 | 功能 |
|---|---|
| 启动 | 一键启动；自动补全缺失文件；自动选择 / 下载合适的 Java；自动分配内存；版本隔离；自定义窗口标题、窗口大小、全屏；启动后自动进服；启动后最小化 / 隐藏 / 关闭；实时游戏日志；导出启动脚本 |
| 账号 | 离线账号；微软正版（设备代码登录，Client ID 可配置）；外置登录（authlib-injector，LittleSkin / Blessing Skin 等）；皮肤头像 |
| 下载游戏 | 正式版 / 快照 / 远古版 / 愚人节版；Forge、NeoForge、Fabric（可同时装 Fabric API）、Quilt、OptiFine；自己执行 Forge 安装器的处理器，下载可走镜像 |
| 社区资源 | Modrinth + CurseForge 双源搜索 Mod / 整合包 / 资源包 / 光影包 / 数据包；常见中文名搜索；自动补齐前置 Mod；按版本和加载器筛选；详情页标出「已安装 / 有更新」 |
| 整合包 | 导入 Modrinth（.mrpack）和 CurseForge（.zip）整合包；导出为 .mrpack |
| 管理 | 多个 .minecraft 目录；版本重命名 / 删除（进回收站）/ 描述；Mod 启用禁用、批量检查更新、一键更新；资源包 / 光影 / 存档 / 截图管理，支持拖放安装；支持超过 260 字符的长路径 |
| 种子地图 | 内置 cubiomes 引擎，按版本和种子离线渲染主世界 / 下界 / 末地的群系地图，与游戏内生成结果一致；标出村庄、要塞、远古城市、试炼密室、海底神殿等 20 余种结构，可复制传送指令 |
| 崩溃分析 | 识别内存不足、Java 版本不对、缺前置、重复 Mod、Mixin 冲突、显卡驱动等常见原因并给建议；一键导出错误报告 |
| 下载 | 多线程、断点换源、全部文件校验；BMCLAPI / MCIM 镜像与官方源错峰竞速，自动避开连不上的源；可自定义镜像；限速 |
| 个性化 | 主题色；浅色 / 深色 / 跟随系统；背景图 |
| 更新 | 多更新源并行拉取签名清单；可添加信任的发布者公钥；镜像表可随签名清单下发 |

## 下载使用

从 https://github.com/VeblenQuinn/OpenLauncher/releases 下载 `OpenLauncher.exe`（同一页面也能下载源码），放进一个空文件夹双击即可（Windows 10 / 11 自带 .NET Framework 4.8，无需安装任何东西）。
游戏文件默认放在 exe 旁边的 `.minecraft`，启动器配置在 `OpenLauncher` 文件夹里——整个文件夹拷走就能在别的电脑上用。

也可以在 **版本选择 → 添加已有的游戏目录** 里直接使用官方启动器、PCL、HMCL 的 `.minecraft`。

## 自己编译

只需要 [.NET SDK](https://dotnet.microsoft.com/download)（8 或更新），不需要 Visual Studio：

```powershell
.\build.ps1        # 输出 dist\OpenLauncher.exe
```

## 去中心化是怎么做到的

```
           官方源                       公益镜像（可增删）
  Mojang / Forge / NeoForge /     BMCLAPI / MCIM / 你自己加的
  Fabric / Quilt / Modrinth /  ⇄  ─────────────────────────
  CurseForge                      错峰竞速，谁快用谁，失败自动换
                 ╲                   ╱
                  ╲                 ╱
                   OpenLauncher.exe
                  ╱                 ╲
 签名更新清单（GitHub / jsDelivr / Gitee / 任何人的镜像）
   ↑ 只认被信任公钥签名的清单；exe 用 SHA-256 校验
```

- 改名、换仓库、换维护者都只需要改 `src/OpenLauncher/Branding.cs` 一个文件。
- 发版流程、钥匙轮换、社区接手：见 [docs/发布与接手.md](docs/发布与接手.md)。
- 微软登录需要自己申请 Client ID：见 [docs/微软登录.md](docs/微软登录.md)。

## 项目结构

```
src/OpenLauncher/
  Core/       JSON、HTTP、镜像表、多线程下载器、设置、任务
  Game/       版本 JSON 合并、安装器（原版 / Fabric / Quilt / Forge / NeoForge / OptiFine）、启动参数、Java 管理、崩溃分析
  Accounts/   离线 / 微软 / 外置登录、authlib-injector、皮肤
  Content/    Modrinth / CurseForge、已装资源识别、本地 Mod 管理、整合包导入导出
  SeedMap/    种子地图（内置 cubiomes 引擎）
  Update/     签名清单与自更新
  UI/         WPF 界面
tools/Release/  发布工具：生成密钥、签名清单、校验
tools/          构建与校验 cubiomes 引擎
```

## 声明

本项目与 Mojang、Microsoft 无关，Minecraft 是 Mojang AB 的商标。
本项目从零编写，没有使用 PCL 的代码与美术资源；功能设计参考了 PCL、HMCL 等国内启动器前辈，向他们致敬。
镜像服务由 bangbang93（BMCLAPI）、MCIM 等社区成员公益提供，请合理使用。
