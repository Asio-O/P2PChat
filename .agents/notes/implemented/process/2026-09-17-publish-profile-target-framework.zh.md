# Agent Note: 发布配置文件的 TargetFramework 由 net10.0 订正为 net11.0

Status: implemented

## Problem

`src/P2PChat.App/Properties/PublishProfiles/FolderProfile.pubxml` 固定了 `<TargetFramework>net10.0</TargetFramework>`，而解决方案中所有项目（含 `Directory.Build.props`）的目标框架都是 `net11.0`，`global.json` 锁定的 SDK 为 `11.0.100-rc.1.26425.128`。

发布配置文件中的 TFM 与项目实际 TFM 不一致时，该配置文件不再指向任何存在的构建输出；`PublishDir` 同样被固定为 `bin\Release\net10.0\publish\win-x64\`，与实际布局不符。结果是通过该配置文件发布（或在 IDE 中选用它）会失败或产出到错误目录，而报错指向「找不到 net10.0 目标」这一表层现象，与真实原因（配置陈旧）相距较远。

## Decision

把发布配置文件的 TFM 与发布目录订正为实际值：

- `<TargetFramework>net10.0</TargetFramework>` → `net11.0`
- `<PublishDir>bin\Release\net10.0\publish\win-x64\</PublishDir>` → `bin\Release\net11.0\win-x64\publish\`

目录结构也一并修正：原值为 `net10.0\publish\win-x64\`，而 .NET 实际生成的布局是 `net11.0\win-x64\publish\`（RID 紧随 TFM，`publish` 在最末）。

其余发布设置保持不变：`Release` 配置、`Any CPU`、`win-x64`、`SelfContained=true`、`PublishSingleFile=true`、`PublishReadyToRun=false` —— 与 Native-AOT 单文件发布一致。

## Alternatives considered

**删除发布配置文件，发布时改用命令行参数（`dotnet publish -r win-x64 …`）。** 否决：AOT 与单文件相关开关较多，把它们固化在受版本控制的配置文件中，比让每个发布者自行拼命令行更不易出错；IDE 的发布向导也依赖该文件。

**把发布配置文件改为多目标（`net10.0;net11.0`）。** 否决：项目本身只目标 `net11.0`，多目标是凭空增加一个没有任何代码支持的构建目标，也没有任何消费者。

**保留 net10.0 不动，等真正发布时再临时改。** 否决：这正是问题现状——陈旧配置会一直留在仓库里，直到发布时才以「找不到目标」的形式暴露，排查成本高于一次订正。

**移除配置文件中的 `TargetFramework`，让它从项目继承。** 未采用：`FolderProfile.pubxml` 是 IDE 生成的配置文件，字段由发布向导读写，删掉的字段可能被向导重新写回。此处选择订正而非移除，代价是 TFM 在 `Directory.Build.props` 与发布配置中各出现一次。

## Consequences

- 通过 `FolderProfile` 发布（命令行或 IDE）可正常解析到 `net11.0`，产物落在 `bin\Release\net11.0\win-x64\publish\`。
- **残留风险：TFM 现在有两处事实来源。** `Directory.Build.props` 与 `FolderProfile.pubxml` 各写一次 `net11.0`，两者可以再次漂移。本次未引入自动校验——`Directory.Build.props` 中的 TFM 变更不会被发布配置文件感知，下一次升级 TFM 时需要同时改两处。
- 本变更不改变已发布产物的行为，仅修正构建与发布路径；AOT 单文件发布的开关未作改动。
