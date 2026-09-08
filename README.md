# XingyiStarry MP

Tactical Annihilation 的 BepInEx 5 局域网遭遇战联机插件。

当前处于开发阶段。协议采用主机权威指令流、严格帧序和 SHA-256 线性哈希链；客机不进行乐观执行。

## 构建

```powershell
& 'D:\Program Files\dotnet\dotnet.exe' build .\XingyiStarry.Mp.sln -c Release
```

默认从相邻的 `Tactical Annihilation` 和参考包内读取游戏、BepInEx 编译引用。可通过 MSBuild 属性 `GameRoot` 与 `BepInExDir` 覆盖。

运行 `build.ps1` 会同时生成可复制到游戏目录的 `dist\BepInEx\plugins\XingyiStarry.Mp`。“联机遭遇战”会先打开原版风格大厅，输入用户名、主机地址和端口并创建/连接；握手后双方进入遭遇战配置页，在 Human 玩家行上选择席位并准备，AI 行继续使用原版 AI。F8 仅保留为诊断入口。

`install.ps1 -NoSteam` 会安装本项目自己的 EarlyPatcher 并创建显式标记，使工作副本不调用 Steam 初始化。该选项用于本机双实例测试；默认安装不会启用它。
