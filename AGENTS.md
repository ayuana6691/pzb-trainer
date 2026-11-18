# pzb-trainer · 影之刃零修改器

《影之刃零 / Phantom Blade Zero》单人模式的外部修改器（C# / WPF / .NET 8），fork 自
[Kuuga-913/pzb-trainer](https://github.com/Kuuga-913/pzb-trainer)。
给用户和接手者的说明：`README.md`（仓库门面）、`docs/USAGE.txt` 与 `docs/FIRST-RUN.txt`
（会原样打进 zip，包内叫「使用说明.txt」「首次使用.txt」）。

## 怎么跑起来

- 打包：`build.cmd`（等价 `pwsh -NoProfile -File pack.ps1`）。需要 .NET 8 SDK；产物是
  `out\release\`（exe + 两份说明 + LICENSE + NOTICE.md + SHA256SUMS.txt）和
  `out\PBZTrainer-v<版本>-win-x64.zip`。
- 只出界面预览包：`pack.ps1 -NoBridge`。不要手写 `dotnet publish`：必须
  `--self-contained true` + 单文件 + `-p:EmbedBridge=true`，漏了对方机器会提示缺 .NET
  或「没有内置运行模块」。
- 跑程序：双击 exe，manifest 要求管理员；先开游戏进存档。`out\` 全被 gitignore，
  里面的构建残留不能当真相来源。

## 技术栈与关键文件

- `net8.0-windows` WPF，单文件 self-contained。界面基本用 C# 构建，
  只有 `App.xaml`（主题与控件样式）和 `Views/MainWindow.xaml`（主界面布局）是 XAML。
- `Services/Injector.cs` 找游戏进程，把内嵌的 `Data/PBZBridge.dll` 释放到
  `%LOCALAPPDATA%\影之刃零修改器\components\<hash>\`。
- `Services/BridgeClient.cs` 命名管道 `\\.\pipe\ChuwuPBZ_v1_<pid>`，帧 = 4 字节小端长度 +
  UTF-8 JSON，每个请求带 `requestId` / `session` / `clientPid`。
- `Services/StateStore.cs` 状态落在 `%LOCALAPPDATA%\影之刃零修改器\`：
  `trainer_state.json`（页面、开关、热键、每怪物生成绑定）+ `preferences.json`。
- `Data/PBZFeatures.json`（76 项功能）与 `Data/PBZAttributes.json`（223 项属性）是嵌入资源，
  改它们等于改界面功能表，id 必须和游戏侧对得上。
- `Data/PBZBridge.dll` 是运行模块二进制（不是本项目源码），入库只为让 CI 和本地出同一个包；
  `pack.ps1` 会校验它的 SHA-256，替换它前先确认来源与哈希。
- `Data/PBZCatalog.json` 是可选的中文名映射表，被 gitignore，仓库里没有。

## 约定

- 用户可见的提示不出现内部文件名（历史修复方向：让用户不用去找文件）。
- 目录条目数随游戏版本变化，不要把某次观测到的数量写成常量或硬编码。
- `docs/*.txt` 首行写死版本号且会原样进 zip：发版时必须同时改
  `src/PBZTrainer/PBZTrainer.csproj` 的 `<Version>`、`docs/USAGE.txt`、`docs/FIRST-RUN.txt`。

## 发版

`git tag v1.x.y && git push origin v1.x.y` → Actions 跑
`pack.ps1 -OutRoot out/ci -Version <tag>` → 直接建 Release（不上传 workflow artifact，
只保留最新 3 个）。流程细节和刻意取舍见 [README.md 的「发版流程」一节](README.md)。

## 当前状态（2026-11-18）

- 最新 tag `v1.11.18`，Release 已发布：只有 `PBZTrainer-v1.11.18-win-x64.zip` 和 `SHA256SUMS.txt`。
- `v1.11.17` 的 Release 是界面预览包——当时 `PBZBridge.dll` 还没入库，CI 拿不到。
- `v1.11.17` / `v1.11.18` 包内「使用说明.txt」首行写的是 v1.11.16；`main` 上已改正，
  下次发版才会带上。
