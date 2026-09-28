# Agent Note: 信封线路编解码收敛到 Core 唯一真相源

Status: implemented

## Problem

消息信封的线路编解码历史上**同时存在过三份手写实现**：

1. `MessageRouter` 内嵌一份（`SerializeEnvelope` / `DeserializeEnvelope`）；
2. `ChatService` 曾有一份私有副本，后来为避免两条反序列化路径走偏，改成转发到 `MessageRouter.DeserializeEnvelope`；
3. `P2PChatTui.ReadHelloResponseAsync`（`/connect <ip:port>` 盲连接读取 hello 响应的那段）又抄了一份。

阶段 3.2 引入 ECDSA 自签名后，信封固定头之后、真实载荷之前多了两个字段：**「4+N 公钥长度前缀」**与**「4+M 签名前缀」**。第 3 份**没有同步更新** —— 它仍按 50 字节固定头之后把剩余字节整个当成 `Payload`。于是 `/connect <ip:port>` 解析 hello 响应时，MessagePack 拿到的是「两个长度前缀 + 真载荷」而不是载荷本身，反序列化必然失败：**自签名上线后，`/connect` 盲连接必然连不上**。

关键在于**为什么编译器和既有测试都没发现**：

- **编译期无感。** 三份实现的签名一模一样，彼此谁也不引用谁，各自都是自洽的私有方法。少读两个长度前缀不是类型错误，是逻辑错误。
- **运行期零覆盖。** `ReadHelloResponseAsync` 只在 `/connect` 路径上执行，而既有集成测试走的全是 `ChatService` / `MessageRouter` 路径，用的正是**已经更新过**的那一份。换句话说：唯一会读到错误格式的那条代码路径，恰好是唯一没有测试的那条。`scripts/e2e-verify.ps1` 也没有任何 `/connect` 断言（它的 Phase 2 覆盖的是 `/msg` 私聊往返）。

还有一层更深的成因，它决定了修法：**第三份副本是被依赖图逼出来的。** `P2PChat.UI` 只引用 `P2PChat.Core`，**不引用 `P2PChat.Chat`**，因此 TUI 在架构上根本无法调用 `MessageRouter.DeserializeEnvelope`。当初有人抄一份，不是疏忽，是当时唯一可行的走法。

## Decision

编解码收敛到**依赖图的根** `P2PChat.Core.Extensions.EnvelopeCodec`，作为信封线路格式的唯一真相源。Core 不引用任何项目，而 Chat、UI、Networking、Crypto、FileTransfer 全部引用 Core —— 这是唯一能让「发送侧」与「接收侧」共同引用的位置。

- `EnvelopeCodec` 提供 `Serialize` / `Deserialize` / `ComputeSignedBytes`，以及 `FixedHeaderSize`（50）与 `MaxFieldLength`（100MB）。
- `MessageRouter.SerializeEnvelope` / `DeserializeEnvelope` 保留，但降级为 **public 薄封装**，直接转发到 `EnvelopeCodec`；既有调用方（`ChatService`、`GroupChatService`、测试）不需要改动。`ChatService` 的私有转发层同样保留。
- `P2PChatTui.ReadHelloResponseAsync` 改为直接调用 `EnvelopeCodec.Deserialize(raw)`。
- 两份手写实现已删除。线路格式（阶段 3.2 之后）为：

```text
[1B Version][1B MessageType][4B Seq(BE)][20B SenderId][16B MessageId]
[8B Timestamp(BE)][4B PublicKeyLen(BE)][N PublicKey][4B SignatureLen(BE)][M Signature][Payload]
```

同一处顺手把畸形输入的语义定死：反序列化遇到信封过短、长度前缀被截断、声明长度超过 `MaxFieldLength`、或声明长度超过实际剩余字节，一律抛 `InvalidDataException`。理由是宁可让整条消息被丢弃，也不能让越界 `Slice` 抛一个语义不明的 `ArgumentOutOfRangeException`，更不能被一个畸形长度前缀诱导去做巨额分配。长度比较全程用 `long` 运算，避免 `uint` → `int` 截断在长度大于 `int.MaxValue` 时变成负数而绕过校验。

AOT 约束不受影响：纯 `BinaryPrimitives` 手工读写，无反射、无动态代码、不新增任何需要注册的序列化类型。

**为什么不是「再同步一次」**：再同步只把这一次事故归零，下次任何人再动格式仍然要人工记得改三处；而副本的存在是结构性的 —— 只要「实现在 Chat 层」这个事实不变，「在 UI 层抄一份」这个诱惑就一直在，事故迟早复发。收敛改的是结构，不是记性。

## Alternatives considered

**把 codec 留在 Chat 层（留在 `MessageRouter` 里，把 `DeserializeEnvelope` 从 private 提成 public）。** 否决：这只解决了「UI 能不能看见」，没解决「UI 愿不愿意依赖 Chat」。为了一个字节级解析器，把「会话编排 + 加密 + 路由」的整个 Chat 层变成 UI 的依赖，耦合代价与收益不成比例，而且 UI（视图）与 Chat（会话编排）的职责边界本就不该互相引用。

**给 `P2PChat.UI` 加一条 ProjectReference 指向 `P2PChat.Chat`。** 否决：同上，且更进一步 —— 它把「两份消费者共用一份实现」的技术问题，换成了「UI 依赖整个 Chat 层」的架构问题，还顺带拖进 Crypto / Networking 的传递依赖。

**继续维护多副本，只把 UI 那份再同步一次。** 否决：见上文「为什么不是再同步一次」。这份副本不是疏忽的产物，是依赖图逼出来的；同步一次之后，下一次格式变更时它还会再漂一次，而漂移的代价（一条用户可见的功能必然失败）远高于同步成本。

**新建一个中立项目（如 `P2PChat.Wire`）专门承载线路格式。** 否决：分层更「干净」，但 Core 本身已经是不引用任何项目、且本就拥有 `Models` / `Extensions` 的根层；为一个静态类新增一层项目引用，只会让依赖图变长、构建变慢，并没有多买到任何隔离性。

## Consequences

- `/connect <ip:port>` 的 hello 响应现在与其它所有收发路径走**同一份**解析器；线路格式再变，改一处即可。
- `MessageRouter.SerializeEnvelope` / `DeserializeEnvelope` 的角色从「唯一实现」降为「public 薄封装」，既有调用方与测试零改动，行为等价。
- **代价：一条畸形帧会终止该连接的接收循环。** `ProcessIncomingConnectionAsync` 在 `DeserializeEnvelope` 外层的 catch 会记 error 并 `break`。这是「失败即抛」契约的直接后果 —— 明确失败好过静默解析出垃圾，但要清楚它不是「丢掉一帧、继续收」。
- **代价：Core 里多了一个带字节布局知识（大端序、长度前缀）的类型。** Core 此前更偏模型与抽象层。这是明确取舍：线路契约属于所有层共享的知识，只有放在依赖图的根才不会被复制。
- **代价：收信方与发信方从此共用一段代码路径。** 这是想要的（否则就是本次事故本身），但也意味着改 `EnvelopeCodec` 必须同时考虑两侧。

## Testing

- `tests/P2PChat.Core.Tests/EnvelopeCodecTests.cs`：13 个测试方法（12 个 `[Fact]` + 1 个带 3 组 `[InlineData]` 的 `[Theory]`），共 15 条用例，覆盖固定头长度、带公钥与签名的逐字段往返、无公钥无签名时的载荷起点（正是 TUI 旧副本的 bug 形态）、空载荷、缺省字段反序列化为 `null`、`ComputeSignedBytes` 的覆盖面，以及五类畸形长度前缀必须抛 `InvalidDataException`。
- 结构层面的保障：实现位于 Core，Chat 与 UI **必须**引用同一份。两处消费点（`MessageRouter` 与 `P2PChatTui.ReadHelloResponseAsync`）的 XML 注释都写明「信封格式只有一份实现，绝不能在本文件里再抄一份解析器」，并记下了本次事故。

## Deferred

- **重放保护仍然缺失。** `MessageRouter.VerifyEnvelope` 只做四件事：有签名、有发送方公钥、`NodeId.FromPublicKey(SenderPublicKey) == SenderId`（防 `SenderId` 冒名）、ECDSA 验签通过。`SequenceNumber` **写了但从不校验**，`Timestamp` **参与签名但从不校验新鲜度** —— 因此一条被捕获的合法帧可以被无限重放。本轮**不修**：加序号窗口要处理乱序到达与重启后的序号基准，加时间窗要处理时钟偏移，两者都会改变现有协议语义，应作为独立的线路变更推进，而不是搭在一次重构里顺手加。
- `/connect` 的 hello 往返仍无自动化端到端覆盖：`P2PChatTui` 没有测试工程引用，而 e2e 脚本没有 `/connect` 断言。当前靠 `EnvelopeCodecTests` 的单元覆盖 + 手工验证兜底。

## Related

- [Message signing](./2026-09-21-message-signing.md) —— 阶段 3.2 自签名上线，正是本次事故的触发变更。
- [Message SenderId must derive from the node identity](./2026-09-20-message-sender-identity.md) —— `VerifyEnvelope` 中「公钥派生 NodeId 必须等于 SenderId」这条防冒名守卫的由来。
- [/connect <ip:port> — blind connect](../feature/2026-09-21-blind-connect.md) —— 被本缺陷打死的正是这条路径。
