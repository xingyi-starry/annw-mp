# XingyiStarry MP

《湮灭之战》（Tactical Annihilation）的多人联机插件，基于 BepInEx 5。

项目仍处于开发阶段。目前面向游戏 1.0.8，主机与客机必须使用相同的插件和游戏版本。

## 主要功能

- 公共中继联机与局域网直连。
- 原版遭遇战式准备房间、选席和准备流程。
- 对局中加入、意外断线快速重连、掉线席位 AI 接管。
- 内置地图与用户地图联机；客机无需预装主机选择的用户地图。
- 从遭遇战存档创建房间，并支持联机安全边界保存。
- 主机权威的回合、单位操作、AI 行动和战局随机结果同步。

带脚本地图可以创建联机房间，但脚本行为尚未完成系统性的多人确定性验证。

## 安装

Release 提供两个压缩包：

- `XingyiStarry-MP-<version>.zip`：标准包，不含 BepInEx，适合已经安装兼容 BepInEx 5 的游戏。
- `XingyiStarry-MP-<version>-with-BepInEx.zip`：整合包，包含官方 BepInEx 5.4.23.5 Windows x64 运行时。

完全退出游戏，把所选压缩包内的全部文件解压到 `AnnW.exe` 所在目录并保留目录结构。启动游戏后，从“遭遇战”的联机入口创建或加入房间。

需要卸载时，双击游戏根目录的 `Uninstall-XingyiStarry-MP.bat`。默认只删除本插件；也可以选择同时删除整个 BepInEx 框架。后一模式会一并删除其他 BepInEx 模组和配置，请谨慎选择。

## 开发环境

开发需要：

- Windows PowerShell 5.1 或 PowerShell 7；
- .NET SDK 8；
- 合法安装的《湮灭之战》1.0.8；
- 可访问 NuGet 和 GitHub Release 的网络。

游戏程序集不属于本项目，也不会提交到仓库。默认假定游戏位于仓库相邻目录 `..\Tactical Annihilation`；其他位置可传入 `GameRoot`。

BepInEx 编译引用由 `tools\Ensure-BepInEx.ps1` 从官方 Release 下载。版本与 SHA-256 固定，缓存位于 `.deps\`，不再依赖其他插件仓库或本机已有 BepInEx 安装。

### 构建与测试

```powershell
.\build.ps1 -Configuration Release -GameRoot 'D:\Games\Tactical Annihilation'
dotnet .\tests\XingyiStarry.Mp.Tests\bin\Release\net8.0\XingyiStarry.Mp.Tests.dll
```

`build.ps1` 会恢复固定的 BepInEx 依赖、构建解决方案，并把开发安装所需文件整理到 `dist\`。

### 打包

```powershell
.\package.ps1 -Configuration Release `
  -GameRoot 'D:\Games\Tactical Annihilation' `
  -OutputDirectory .\releases
```

一次打包同时生成标准包和 `with-BepInEx` 整合包，并输出各自 SHA-256。正式包不包含 DebugTools、EarlyPatcher、免 Steam 标记或其他联机插件。

### 开发安装

```powershell
.\install.ps1 -GameRoot 'D:\Games\Tactical Annihilation'
```

开发安装包含 DebugTools 和按标记启用的 EarlyPatcher，用于本地双实例测试；这些内容不进入正式包。安装和更新前应完全退出所有游戏进程。

### 协议与 Relay

唯一 protobuf 定义位于 `src\XingyiStarry.Mp.Protocol\Protos\multiplayer.proto`。修改协议后还需要在相邻 Relay 仓库运行 `generate.ps1`，提交重新生成的 Go 代码，并完成 C#、Go 和跨语言测试。

## 许可证

本项目推荐并采用 [MIT License](LICENSE)。它简短、宽松，允许社区修改和再分发，同时保留版权与免责声明，适合独立游戏插件。

第三方组件及整合 BepInEx 包的分发声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 和 [licenses](licenses)。
