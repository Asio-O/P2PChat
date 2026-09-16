# P2PChat

一个去中心化的 P2P 终端聊天程序：基于 Mainline DHT（BitTorrent 网络）发现对等节点，节点间通过 TCP 直连通信，消息使用 AES-256-GCM 加密。整个程序以 **Native-AOT** 编译为单个原生可执行文件，运行时不需要安装 .NET。

## 特性

- **无中心服务器**：借助公共 Mainline DHT 引导与发现节点，另支持自定义引导节点
- **端到端加密**：ECDH（P-256）协商临时会话密钥，HKDF-SHA256 派生，AES-256-GCM 加解密；群密钥经 ECDH + AES-GCM 包装后分发
- **私聊与群聊**：点对点加密私聊、群组创建/邀请/成员扇出/解散通知
- **文件传输**：分块传输 + SHA-256 完整性校验 + 传输进度回调
- **轻量 TUI**：自绘控制台界面（无第三方 TUI 框架依赖），支持联系人列表、会话切换、滚动与命令集
- **Native-AOT**：默认发布方式即原生编译，产物约 8.7 MB，冷启动无 JIT 开销

## 环境要求

- .NET SDK 11（`global.json` 已固定为 `11.0.100-rc.1`，允许 `latestFeature` 前滚）
- Windows 下编译 Native-AOT 需要 MSVC 工具链（Visual Studio 的「使用 C++ 的桌面开发」工作负载）

## 构建与运行

```powershell
# 构建全部项目
dotnet build P2PChat.slnx

# 运行（开发模式）
dotnet run --project src/P2PChat.App

# 发布为原生可执行文件（默认即 Native-AOT）
dotnet publish src/P2PChat.App/P2PChat.App.csproj -c Release
# 产物: src/P2PChat.App/bin/Release/net11.0/win-x64/publish/P2PChat.App.exe
```

## 测试

```powershell
dotnet test P2PChat.slnx
```

测试分层：`Core`（序列化与 NodeId）、`Crypto`（加解密）、`Networking`（Bencode/DHT 路由表）、`Integration`（多态消息编解码往返、双节点 TCP 加密私聊内容一致性、群组邀请与群消息、文件分块完整性、KBucket 行为）。

双进程端到端验证脚本（真实拉起两个节点实例，校验 DHT 发现、端口绑定、数据目录隔离、日志健康度）：

```powershell
pwsh -NoProfile -File scripts/e2e-verify.ps1
```

## 配置

配置项通过环境变量（前缀 `P2PCHAT_`，嵌套键用 `__`）或命令行参数传入：

| 键 | 说明 | 默认 |
|---|---|---|
| `P2PChat:UdpPort` | UDP 监听端口 | `0`（自动选择） |
| `P2PChat:TcpPort` | TCP 监听端口 | `0`（自动选择） |
| `P2PChat:BootstrapNodes` | 自定义引导节点（`host:port`） | 内置公共 DHT 引导节点 |
| `P2PChat:KBucketSize` / `P2PChat:Alpha` | Kademlia 参数 | `20` / `3` |

额外环境变量：

- `P2PCHAT_DATA_DIR` —— 覆盖数据目录（默认 `<程序目录>/data`）。多实例共存时用它隔离身份与密钥。
- `P2PCHAT_SELFTEST=1` —— 非交互自检模式：打印节点 ID、监听端口、DHT 已知节点数后自动退出，便于无人值守验证。
- `P2PCHAT_SELFTEST_WAIT` —— 自检模式等待 DHT 引导的毫秒数。

示例（同机启动两个互不干扰的实例）：

```powershell
$env:P2PCHAT_DATA_DIR="$env:TEMP\nodeA"; $env:P2PCHAT_P2PChat__UdpPort="20081"; $env:P2PCHAT_P2PChat__TcpPort="20091"
.\P2PChat.App.exe
```

## 项目结构

```
src/
  P2PChat.Core          抽象接口、消息模型、序列化（MessagePack 源生成器）
  P2PChat.Networking    Mainline DHT、Kademlia 路由表、TCP/UDP 传输、Bencode
  P2PChat.Crypto        AES-256-GCM、ECDH/HKDF、文件密钥库（System.Text.Json 源生成）
  P2PChat.Chat          消息路由、私聊/群聊/联系人服务、各类消息处理器
  P2PChat.FileTransfer  分块文件传输与校验
  P2PChat.UI            自绘控制台界面
  P2PChat.App           依赖注入装配与启动
tests/                  单元测试与端到端集成测试
scripts/                端到端验证脚本
```

## 说明

本项目用于学习与研究 P2P 网络与 Native-AOT 实践。DHT 引导依赖公共 BitTorrent 网络，节点发现效果受网络环境影响；如需稳定互联，建议自行部署引导节点并通过 `P2PChat:BootstrapNodes` 指定。
