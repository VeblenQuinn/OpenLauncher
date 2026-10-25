# OpenLauncher（精简源码 · 可直接发布）

从 `OpenLauncher-0.1.0` 整理出来的**纯源码副本**，已做发布准备：去掉构建产物与临时文件、删除 `docs/`，**C# / XAML 代码与原目录一致**。

## 与原目录的差异

去掉的内容：

| 内容 | 体积 | 说明 |
|---|---|---|
| `src/OpenLauncher/bin/` | 6.9 MB | 构建产物 |
| `src/OpenLauncher/obj/` | 5.9 MB | 构建中间文件 |
| `src/OpenLauncher/OpenLauncher_*_wpftmp.csproj` | 2 个 | WPF 构建误留的临时工程文件 |
| `tools/Release/bin/`、`tools/Release/obj/` | — | 同上 |
| `dist/` | 0.6 MB | 发布产物，含运行时数据（`cache/version_manifest_v2.json` 265 KB、`config.json`、`accounts.dat`） |
| `release/` | 3.4 MB | `tools/build-cubiomes.ps1` 的默认输出目录（cubiomes.dll + pdb） |
| `docs/` | 28 KB | 维护文档（内容已精简入 `README.md` 的「发版」一节） |

保留的内容：全部源码、`Assets/`（含内嵌的 `cubiomes.dll` 1.5 MB、图标、中文名表）、`tools/`（含 `cubiomes-shim/olshim.c`、`make_icon.py`）、`build.ps1`、`publish.ps1`、`README.md`、`LICENSE`、`.gitignore`、`.vscode/tasks.json`。

> `release/cubiomes.dll` 被去掉了，但 `src/OpenLauncher/Assets/cubiomes.dll` 保留了同一份（1 531 904 字节）并作为嵌入资源打进 exe，所以**编译与运行都不受影响**。需要重新生成引擎时跑 `tools/build-cubiomes.ps1` 即可。

## 规模

- 110 个文件 / 2.8 MB（原目录 669 个文件 / 36.1 MB）

## 版本

`0.1.44`。相对 0.1.43 的代码变更：**删除了手柄支持**（`UI/Gamepad.cs`、`UI/GamepadLatch.cs`、`UI/GamepadNavigation.cs`，约 37 KB），一并去掉焦点框、按键提示条、XInput 轮询、设置页的「手柄」分段与 `input.gamepad` / `input.gamepadDeadzone` 配置项，窗口也不再留第三行给提示条。

## 构建与发布

```powershell
# 编译单文件发布版（产物在 dist\）
.\build.ps1

# 编译 + 签名更新清单（需先有 RSA 密钥，见 README 「发版」一节）
.\publish.ps1 -Version 0.1.45 -Key <私钥.xml> -Pub <公钥.xml> -Urls <下载地址>
```

已验证：本副本 `dotnet build -c Release` 与 `build.ps1` 均通过，**0 警告 0 错误**，产物 `OpenLauncher.exe` 版本 0.1.44。
