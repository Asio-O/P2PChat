# P2PChat

一个去中心化的 P2P 终端聊天程序：基于 Mainline DHT（BitTorrent 网络）发现对等节点，节点间通过 TCP 直连通信，消息使用 AES-256-GCM 加密并对整个信封做 ECDSA 签名。整个程序以 **Native-AOT** 编译为单个原生可执行文件，运行时不需要安装 .NET。

## 特性

- **无中心服务器**：借助公共 Mainline DHT 引导与发现节点，另支持自定义引导节点。节点之间通过 `announce_peer` / `get_peers` 互相宣告与解析「节点 ID → 端点」映射
- **端到端加密**：ECDH（P-256）协商临时会话密钥，HKDF-SHA256 派生，AES-256-GCM 加解密；群密钥经 ECDH + AES-GCM 包装后分发，**群聊消息本身也走 AES-256-GCM 对称加密**（共享同一线路契约，与私聊一致）
- **消息签名与身份绑定**：每条出站信封用长期 ECDSA P-256 私钥签名，签名覆盖除签名自身外的全部内容（含发送方节点 ID、公钥、时间戳与载荷）；接收端强制验签，并校验「公钥派生的节点 ID == 信封里的 SenderId」，杜绝冒名
- **私聊与群聊**：点对点加密私聊、群组创建/邀请/成员扇出/解散通知，群组信息本地持久化
- **NAT 穿透**：启动时通过 UPnP 把本机 TCP/UDP 端口申请映射到公网（无 COM 依赖的原生实现）；失败时明确提示「仅同网段可达」，不静默失败
- **直连通道**：知道 IP 时可直接 `/add <节点ID> <ip:port>`；只知道 IP 时用 `/connect <ip:port>` 盲连握手自动获取对端节点 ID
- **文件传输**：分块传输 + SHA-256 完整性校验 + 传输进度回调
- **轻量 TUI**：自绘控制台界面（无第三方 TUI 框架依赖），支持联系人列表、会话切换、滚动与命令集；`P2PCHAT_PLAIN=1` 可切到逐行输出的线性模式
- **Native-AOT**：默认发布方式即原生编译，产物约 8.7 MB，冷启动无 JIT 开销

## ⚠️ 隐私代价（务必先读）

节点发现靠的是**公共 BitTorrent DHT**，这意味着：

- 程序**启动后会自动**把自己的 **TCP 端口**（以及隐含的公网可达性）通过 `announce_peer` 广播到公共 DHT 网络，随后每 15 分钟重复一次。**目前没有开关可以关闭自动宣告。**
- 知道你的节点 ID 的人，就有机会通过公共 DHT 查到你的地址与端口。节点 ID 是长期身份的 SHA-1 派生值，不随会话变化。
- 公网引导节点只作报文中转，但**任何 BitTorrent 客户端的节点都可能成为转发的旁观者**。
- 消息内容本身始终是端到端加密的，公共 DHT 与引导节点**看不到明文**；但它们**看得到「谁的节点 ID 在什么时候上线、端口是多少」**。

如果你需要更强的隐私，请自行部署引导节点并通过 `P2PChat:BootstrapNodes` 指定（公网节点因此不会参与），并避免在不可信网络下长时间常驻。

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

测试分层：`Core`（序列化与 NodeId、端点字面量解析）、`Crypto`（加解密与签名）、`Networking`（Bencode/DHT 路由表、真实 KRPC 发现闭环、UPnP 客户端）、`Integration`（多态消息编解码往返、双节点 TCP 加密私聊内容一致性、群组邀请与群消息、**消息签名与身份绑定**、文件分块完整性、KBucket 行为）。

当前规模：**179 个用例全部通过**（Core 37 / Crypto 8 / Integration 115 / Networking 19）。

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

- `P2PCHAT_DATA_DIR` —— **可选**，覆盖数据目录。默认数据目录为 `%USERPROFILE%\.p2pc`（Unix 为 `$HOME/.p2pc`）；仅在同一台机器上同时运行多个需要各自独立身份的实例时才需要设置。
- `P2PCHAT_SELFTEST=1` —— 非交互自检模式：打印节点 ID、数据目录、监听端口、DHT 已知节点数、联系人数、群组数、宣告状态、UPnP 状态后自动退出，便于无人值守验证。
- `P2PCHAT_SELFTEST_WAIT` —— 自检模式等待 DHT 引导的毫秒数。
- `P2PCHAT_PLAIN=1` —— **线性输出模式**：跳过全屏 TUI，改为逐行写 stdout；stdin 改为按行读取，命令与交互模式完全一致。适合脚本抓取明文做端到端断言、stdout 被重定向或 CI 场景。

示例（同机启动两个互不干扰的实例）：

```powershell
$env:P2PCHAT_DATA_DIR="$env:TEMP\nodeA"; $env:P2PCHAT_P2PChat__UdpPort="20081"; $env:P2PCHAT_P2PChat__TcpPort="20091"
.\P2PChat.App.exe
```

### 数据目录

程序的所有运行时文件集中存放在用户主目录下的 **`.p2pc`** 文件夹中，与程序安装位置解耦：

| 文件 | 内容 |
|---|---|
| `identity.json` | 长期身份密钥对（ECDH P-256） |
| `session_keys.json` | 各对端会话密钥 |
| `group_keys.json` | 各群组密钥 |
| `groups.json` | 群组元数据：群 ID、群名、创建者、成员名单、群密钥、创建时间（重启后群列表不丢） |
| `contacts.json` | 联系人列表（含可选的显式 `ip:port`） |
| `peers.txt` | 已知节点（启动加载、退出保存） |
| `logs/p2pchat-YYYYMMDD.log` | 按日滚动的运行日志 |

默认路径为 `%USERPROFILE%\.p2pc`（Windows）或 `$HOME/.p2pc`（Unix）。

> 这样设计的原因是：程序可能位于只读目录，重新发布覆盖 exe 也不应连带丢失身份密钥与聊天数据；
> 同时同一用户的多份程序副本天然共享同一身份。

## 项目结构

```
src/
  P2PChat.Core          抽象接口、消息模型、序列化（MessagePack 源生成器）、数据目录解析
  P2PChat.Networking    Mainline DHT（宣告/解析）、Kademlia 路由表、TCP/UDP 传输、Bencode、UPnP
  P2PChat.Crypto        AES-256-GCM、ECDH/HKDF、ECDSA 签名、密钥库与群组元数据持久化（System.Text.Json 源生成）
  P2PChat.Chat          消息路由（含出入站签名）、私聊/群聊/联系人服务、各类消息处理器
  P2PChat.FileTransfer  分块文件传输与校验
  P2PChat.UI            自绘控制台界面
  P2PChat.App           依赖注入装配与启动
tests/                  单元测试与端到端集成测试
scripts/                端到端验证脚本
```

更多实现细节（模块职责、协议布局、AOT 约束、已知陷阱）见 [`Agent.md`](Agent.md)。

## 说明

本项目用于学习与研究 P2P 网络与 Native-AOT 实践。DHT 引导依赖公共 BitTorrent 网络，节点发现效果受网络环境影响；如需稳定互联，建议自行部署引导节点并通过 `P2PChat:BootstrapNodes` 指定。

**互操作性**：自 2026-09-21 起消息信封强制携带签名，老版本节点与新版本节点之间无法互通 —— 升级任一端即要求两端同时升级。

**已知限制**：自动宣告到公共 DHT 无法关闭（见上文「隐私代价」）；NAT 穿透目前只支持 UPnP IGD，不支持 NAT-PMP；路由器不支持 UPnP 时只能被同网段或已建立连接的节点连入，此时请用 `/add <节点ID> <ip:port>` 或 `/connect <ip:port>` 手工指定地址。

