# XingyiStarry MP

[简体中文](README.md) | [English](README_en.md)

《湮灭之战》（Tactical Annihilation）的多人联机插件，基于 BepInEx 5。

项目仍处于开发阶段。目前面向游戏 1.0.12，主机与客机必须使用相同的插件和游戏版本。

## 主要功能

- 同一房间同时支持公共中继与 TCP 直连客户端。
- 原版遭遇战式准备房间、选席和准备流程。
- 对局中加入、意外断线快速重连、掉线席位 AI 接管。
- 内置地图与用户地图联机；客机无需预装主机选择的用户地图。
- 从遭遇战存档创建房间，并支持联机存档。
- 支持自主决策，各端同步执行主机选定的具体单位动作。

带脚本地图可以创建联机房间，但脚本行为尚未完成系统性的多人确定性验证。

## 安装

Release 提供两个版本：

- `XingyiStarry-MP-<version>.zip`：标准包，不含 BepInEx，适合已经安装兼容 BepInEx 5 的游戏。
- `XingyiStarry-MP-<version>-with-BepInEx.zip`：整合包，包含官方 BepInEx 5.4.23.5 Windows x64 运行时。

完全退出游戏，将下载内容中的安装文件解压到 `AnnW.exe` 所在目录并保留目录结构。启动游戏后，从“遭遇战”的联机入口创建或加入房间。

需要卸载时，双击游戏根目录的 `Uninstall-XingyiStarry-MP.bat`。默认只删除本插件；也可以选择同时删除整个 BepInEx 框架。后一模式会一并删除其他 BepInEx 模组和配置，请谨慎选择。

## 开发环境

开发需要：

- Windows PowerShell 5.1 或 PowerShell 7；
- .NET SDK 8；
- 合法安装的《湮灭之战》1.0.12；
- 可访问 NuGet、GitHub Packages 和 GitHub Release 的网络。

游戏程序集不属于本项目，也不会提交到仓库。日常开发默认使用仓库相邻目录 `..\Tactical Annihilation` 中的合法游戏安装；其他位置可传入 `GameRoot`。

构建脚本会自动下载并校验官方 BepInEx 依赖，无需另行安装。

### 构建与测试

```powershell
.\build.ps1 -Configuration Release -GameRoot 'D:\Games\Tactical Annihilation'
dotnet .\tests\XingyiStarry.Mp.Tests\bin\Release\net8.0\XingyiStarry.Mp.Tests.dll
```

`build.ps1` 会恢复固定的 BepInEx 依赖、构建解决方案，并把开发安装所需文件整理到 `dist\`。

### 打包

```powershell
.\package.ps1 -Configuration Release `
  -GameReferencesTag game-1.0.12 `
  -OutputDirectory .\releases
```

正式打包使用 `GameReferencesTag` 指定游戏引用版本。维护者需要先用带 `read:packages` 权限的 `gh` 登录，也可以通过 `GH_PACKAGES_TOKEN` 提供令牌。

一次打包同时生成标准包和 `with-BepInEx` 整合包，并输出各自 SHA-256。打包前应确认版本号、游戏引用和测试结果均已更新。

游戏更新后，维护者先审阅并更新 `game-references.json`，再上传新的私有引用包：

```powershell
.\tools\Publish-GameReferences.ps1 `
  -GameRoot 'D:\Games\Tactical Annihilation' `
  -Tag game-1.0.12
```

上传要求 GitHub 令牌具有 `write:packages` 权限。引用包只用于编译，不会进入插件发布压缩包。

### 开发安装

```powershell
.\install.ps1 -GameRoot 'D:\Games\Tactical Annihilation'
```

开发安装默认将 Relay 配置为 `127.0.0.1:24555`，需要先在本机运行 Relay。新配置的游戏直连监听端口为 `24556`，避免与本地 Relay 冲突；已有直连端口保留。需要临时连接其他 Relay 时传入 `-RelayEndpoint '地址:端口'`。正式发布包仍默认连接公共线上 Relay。

### 协议与 Relay

唯一 protobuf 定义位于 `src\XingyiStarry.Mp.Protocol\Protos\multiplayer.proto`。C# 消息由 `Grpc.Tools` 随构建生成。字段哈希编码由 `python tools/generate_authority_hash.py` 生成 C#／Go 实现（需 Python 3 与 protoc）。修改协议后还需要在相邻 Relay 仓库运行 `generate.ps1`，提交重新生成的 Go 代码，并完成 C#、Go 和跨语言测试。


## 许可证

本项目采用 [MIT License](LICENSE)。

第三方组件及整合 BepInEx 包的分发声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 和 [licenses](licenses)。
