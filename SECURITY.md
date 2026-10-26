# 安全说明

## 报告漏洞

请不要在公开 issue 里贴出可直接利用的细节。用 GitHub 的
[私密漏洞报告](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing/privately-reporting-a-security-vulnerability)
（仓库 → Security → Report a vulnerability），或直接联系仓库维护者。

## 本项目的安全边界

**账号令牌**用 Windows DPAPI（`ProtectedData`，当前用户作用域）加密后存在
`SquidLauncher\accounts.dat`，只有同一台电脑上的同一个 Windows 用户能解密。
文件名以 `.dat` 结尾只是习惯，不是自己实现的加密格式。

**更新**只认被内置或用户手动信任的 RSA 公钥签过名的清单；exe 另外用 SHA-256 校验。
私钥不在仓库里，也不应该出现在任何构建产物里。

**下载**的每个文件都带官方提供的 SHA-1 / SHA-256，校验失败会重试换源，不会静默接受。

## 提交前请检查

以下文件**绝对不能**提交到公开仓库：

| 文件 | 原因 |
|---|---|
| `private-key.xml` / `*.private.xml` | 更新签名私钥，泄露等于任何人都能给你的用户推送任意程序 |
| `SquidLauncher/accounts.dat` | 加密的账号令牌，虽然绑定了本机仍然不该外流 |
| `SquidLauncher/config.json` | 可能含 CurseForge API Key、个人游戏目录、账号选择 |
| `dist/`、`bin/`、`obj/` | 构建产物，已在 `.gitignore` 里 |
| `release/cubiomes.dll`、`*.pdb` | 引擎编译产物，真正打包的是 `src/SquidLauncher/Assets/cubiomes.dll` |

## 第三方组件

`src/SquidLauncher/Assets/cubiomes.dll` 由 [cubiomes](https://github.com/Cubitect/cubiomes)（MIT）
的社区分支编译而来，来源与编译方式见 `tools/build-cubiomes.ps1` 与
`tools/cubiomes-shim/olshim.c`。分发时请保留其 MIT 许可声明，
完整第三方清单见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
