# 第三方组件与许可

本项目自身以 GPL-3.0-only 发布（见 [LICENSE](LICENSE)）。以下是随本项目一起分发的第三方
组件及其许可。分发编译产物（`OpenLauncher.exe`）时请一并保留本文件。

---

## cubiomes

- 文件：`src/OpenLauncher/Assets/cubiomes.dll`（编译进 exe 的嵌入资源）
- 用途：种子地图的世界生成模拟（群系、结构位置）
- 来源：上游 [Cubitect/cubiomes](https://github.com/Cubitect/cubiomes)（MIT）的社区分支
  [Conflux-Union/cubiomes](https://github.com/Conflux-Union/cubiomes)，
  因为上游只到 1.21 Winter Drop，该分支已合入 MC_26_3
- 编译方式：见 `tools/build-cubiomes.ps1`；薄封装层为本项目自写，见 `tools/cubiomes-shim/olshim.c`
- 许可：MIT

```
MIT License

Copyright (c) 2020-2024 cubiomes contributors
Copyright (c) 2020-2024 Cubitect
Copyright (c) 2025 Conflux-Union contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

> 上游 cubiomes 的版权声明以上述方式保留。若你替换了该 DLL，请同时更新本节与
> `Branding.EngineSha256`。

---

## 图标字体与系统组件

界面图标使用 Windows 自带的 **Segoe MDL2 Assets** 字体，不随本项目分发，属于操作系统组件。

## 运行期依赖（不随本项目分发）

本程序面向 Windows 10 / 11 自带的 **.NET Framework 4.8** 编译，未内置或再分发该运行时。
用户在游戏中下载的 Minecraft、加载器（Forge / NeoForge / Fabric / Quilt）、Mod、
资源包与光影包均来自各自的官方渠道，遵循各自许可，本项目不参与其分发。

## 公益镜像服务

BMCLAPI（bangbang93）、MCIM 等由社区成员公益提供，本项目仅作为客户端请求，不缓存、不再分发其内容。
