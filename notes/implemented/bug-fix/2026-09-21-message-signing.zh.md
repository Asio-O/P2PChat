# Agent Note: 出站消息必须用长期 ECDSA 私钥签名、入站必须验签

Status: implemented

## Problem

`MessageRouter.SendAsync` / `SendViaConnectionAsync`（`src/P2PChat.Chat/Routing/MessageRouter.cs`）把 `Message` 序列化进 `MessageEnvelope` 后直接 `connection.SendAsync(...)` 出栈；接收端 `RouteIncomingAsync` 反序列化后直接派发到对应 `IMessageHandler`，**全程没有任何签名/验签环节**。

后果：`SenderId` 仍是攻击者可控的输入。`notes/implemented/bug-fix/2026-09-20-message-sender-identity.md` 已确认 `SenderId` 统一改用 `KeyPair.NodeId`，但同一 note 的 §"Not covered" 段明确指出：「本变更只保证诚实节点自报真实身份，没有签名，攻击者可任意填写他人的 `SenderId`。」本期（REPAIR-PLAN §3.2）即处理此遗漏。

实测：
- `IEncryptionService.Sign` / `Verify`（`src/P2PChat.Crypto/Encryption/AesGcmEncryptionService.cs:104-128`）已经实现（ECDSA P-256 + SHA-256），但仓库内零调用点。
- 接收端可被注入「伪造 SenderId + 任何 payload」的 TextMessage / FileMetaMessage 等：当前实现仅按 `MessageType` 路由到对应 handler，而 handler 通常从 `message.SenderId` 派生 NodeId → 取 Session Group key → 解密 / 落库 / 推事件。攻击者只要掌握目标群 GroupId + 群密钥（即可被 `HandleInviteAsync` 旁路注入），即可向群里任意注入「看起来来自 Alice / Bob」的消息。
- 私聊同理——攻击者抢在 ECDH 握手前伪造「KeyExchange 响应」，即可插入自己的临时公钥并让会话密钥归他控制。

## Decision

- `Core/Models/MessageEnvelope.cs` 扩两个**可选**字段：
  - `SenderPublicKey: byte[]?` —— 发送方长期身份公钥（P-256 SubjectPublicKeyInfo，91 字节）。接收端验签必须用此公钥；放在信封外意味着可以独立于具体 Message 子类的 wire 字段。
  - `Signature: byte[]?` —— ECDSA P-256 / SHA-256 签名，~64 字节。
- `MessageRouter.SerializeEnvelope` / `DeserializeEnvelope` 升级：固定头之后追加 **4B 大端 SenderPublicKey 长度 + N 公钥字节 + 4B 大端 Signature 长度 + N 签名字节 + N payload**。所有长度用 `BinaryPrimitives.ReadUInt32BigEndian` / `WriteUInt32BigEndian`，与既有 4B 帧头风格一致。两条静态方法（`SerializeEnvelope` / `DeserializeEnvelope`）被 `MessageRouter` 与 `ChatService.ReadKeyExchangeResponseAsync` 共用，避免反序列化走偏。
- `MessageRouter` 构造函数新增两个依赖：`IKeyStore`（取发送方身份）和 `IEncryptionService`（执行 Sign / Verify）。`SendViaConnectionAsync` 出口流程：
  ```csharp
  var identity = _keyStore.GetOrCreateIdentity();
  var unsigned = new MessageEnvelope {
      ...
      SenderId = identity.NodeId.ToByteArray(),          // 强制覆盖
      SenderPublicKey = identity.PublicKey,
      Payload = _serializer.Serialize(message)
  };
  var envelope = SignEnvelope(unsigned, identity, _encryption);
  await connection.SendAsync(SerializeEnvelope(envelope), ct);
  ```
  `ToBeSignedBytes` 拼接顺序固定：`Version || MessageType || Seq || SenderId || MessageId || Timestamp || SenderPublicKey(长度+字节) || Payload`；`Signature` 本身不进入被签数据。
- `RouteIncomingAsync` 入口新增验签关卡 `VerifyEnvelope(envelope, _encryption, out failureReason)`：
  1. `Signature == null || Signature.Length == 0` → 警告日志 + 丢弃（**不**投递到 handler）。
  2. `SenderPublicKey == null || SenderPublicKey.Length == 0` → 警告日志 + 丢弃。
  3. `NodeId.FromPublicKey(SenderPublicKey).ToByteArray()` 与 `envelope.SenderId` 不一致 → 警告日志 + 丢弃（防 SenderId 伪造的核心断言：攻击者即使偷到合法签名，也无法冒名另一个 NodeId，因为 `SenderId` 必须等于「公钥 SHA-1 派生的 NodeId」）。
  4. ECDSA 验签失败 → 警告日志 + 丢弃。
  5. 全部通过 → 进入正常 handler 派发。
- `KeyExchangeHandler.SendResponseAsync` 改走 `MessageRouter.SignEnvelope` + `MessageRouter.SerializeEnvelope` 静态方法，保证响应也带签名；不再自带长度拼接逻辑。
- 生产侧影响面：`Program.cs` 的 `AddSingleton<IMessageRouter, MessageRouter>()` 注册不变，容器已持有 `IKeyStore` / `IEncryptionService` 单例自动注入。
- 测试侧影响面：`NodeHarness.Start` 最小必要改动：追加 `encryption, keyStore` 两个参数（与此前 `task-2` 在 `GroupChatService` 构造里加 `InMemoryGroupMetadataStore()` 是同一模式）。`PayloadCapturingMessageRouter` 不需改（只代理 `_inner.SendAsync`）。

## Alternatives considered

**签名字段塞在 `Message` 基类。** 否决：基类上的 `Signature` 会被 8 个现有 `[Union]` 子类一起改 wire 布局，每个子类的源生成 formatter 都要重新生成、每个使用方都要重新走序列化路径。`MessageEnvelope` 是统一加签的更小接地点（`SerializeEnvelope` / `DeserializeEnvelope` 是唯一出入口）。

**只签 `SenderId + MessageType + SequenceNumber`，不签 payload。** 否决：payload 是消息本体的实质内容；不签 payload 等于允许「保留签名、重放 payload」的篡改（如篡改 TextMessage.Content）。本任务的实质攻击面是「攻击者可任意填 SenderId + payload」，所以 payload 必须进签名。

**不签 `SenderPublicKey`，让接收端自行从 keyStore 查对端公钥。** 否决：当前 `IKeyStore` 不存对端长期公钥（只有 SessionKey / GroupKey / Identity），改造 keyStore 涉及持久化 schema 变更，与 3.2 任务无强耦合。把 `SenderPublicKey` 放在 envelope 里意味着接收端可以**独立**完成验签（不需要预先共享过什么），代价是每条消息多 91B 公钥 + 64B 签名。本期接受这个代价。

**用接收方会话密钥（AES-GCM tag）兼任消息源鉴别。** 否决：AES-GCM 是对称鉴别（双方拥有同一密钥才能验），与「唯一持有私钥的发送方才签得出」的语义不同；A/B 群消息共享群密钥，无法区分「A 发的」还是「B 发的」。ECDSA 的非对称语义才是 3.2 想要的。

**等发送方与接收方在公钥层握手一次再签名（KeyExchange 顺手带上 SenderPublicKey）。** 否决：会让首条消息（在 KeyExchange 完成之前）无法签名，而 KeyExchange 本身又成为首条无签名消息——鸡生蛋。`SenderPublicKey` 进 envelope 让首条消息即可签名。

**密钥派生自 SenderId 之外的字段（避免 SignerId 与公钥重复）。** 否决：`NodeId.FromPublicKey` 是**唯一**身份派生点（见 2026-09-20 sender-identity §Decision）；让 SenderId 与公钥从同一来源派生是消除双重身份的最简洁方式。多带一份公钥的成本小（91B），换来接收端零状态校验是值得的。

## Consequences

- 真实 ECDSA 签名/验签往返通过测试：`alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, "...")` 跑完后，`MessageRouter.SerializeEnvelope` 出站 MessageEnvelope 末尾附带 91B 公钥 + ~64B 签名；Bob 的 `RouteIncomingAsync` 用同一条 SenderPublicKey 验签通过，`PrivateHandler.OnMessageReceived` 收到解密后的明文（`MessageSigningTests.出站消息_签名并附带SenderPublicKey_接收端验签通过明文还原`）。
- 篡改 `SenderId` 后验签失败 → 丢弃并产生可观测日志（`MessageSigningTests.篡改SenderId_接收端验签失败丢弃且无ChatMessageEvent`、`MessageSigningTests.双节点_Alice篡改SenderId_Bob的路由拒绝并产生告警日志`、`MessageSigningTests.MessageRouter_SenderId与公钥派生NodeId不一致_验签失败_原因为不匹配`）。
- 篡改 `Payload` 后验签失败 → 同样丢弃 + 日志告警（`MessageSigningTests.篡改Payload_接收端验签失败丢弃且无ChatMessageEvent`）。
- 缺签名/缺公钥也被拒（`MessageRouter_缺签名_VerifyEnvelope返回false_原因为缺少签名`、`MessageRouter_缺SenderPublicKey_VerifyEnvelope返回false_原因为缺少发送方公钥`）。
- 序列化往返一致（`MessageRouter_线缆_签名Envelope序列化与反序列化后验签仍通过`）：Serialize → Deserialize 后验签仍通过。
- 静态层往返（`MessageRouter静态SignEnvelope与VerifyEnvelope往返一致`）：`SignEnvelope` 与 `VerifyEnvelope` 不依赖网络栈即可完成。
- README 群聊端到端加密语义保持「成立」；本任务为签名（新增的安全维度），与 3.1 加密不重叠。
- `dotnet build` 0 错 0 警；`dotnet test` 全绿（基线 160 通过不变，新增 9 条守卫）。
- **线路语义变更，新旧版本不可互通。** 旧版本客户端发出的 envelope 无 Signature / SenderPublicKey，本版本在 `RouteIncomingAsync` 入口就会丢弃并告警。与 2026-09-20 sender-identity 同批次；当前阶段无可用版本，无需提供迁移。
- **信封体积增加。** 每条消息多 ~155B（91B 公钥 + 64B 签名）。TCP 长度前缀成帧已支持任意大小（`TcpConnection` 100MB 上限），无实质影响。
- **ECDSA P-256 + SHA-256 单次签名开销。** 实测 `< 1ms`（无基准需求，本任务只要求语义正确）。
- **`MessageRouter` 现在依赖 `IKeyStore` / `IEncryptionService`。** 两者在生产 DI 已单例化；测试侧 `NodeHarness.Start` 与现有 `FileBackedGroupMetadataStore` 注入模式相同——只是再增加两个参数；已有 `PayloadCapturingMessageRouter`（仅调 `_inner.SendAsync`）不需要改。
- **`SenderPublicKey` 与 `Signature` 均为可选项（`byte[]?`）**——`VerifyEnvelope` 在调用顺序上先检查 `Signature`、再 `SenderPublicKey`、再 NodeId 一致性、最后 ECDSA 验签；任一关卡失败即短路返回 false + 失败原因。
- **签名覆盖到 `Payload`，意味着篡改 Message 子类的任何 wire 字段都会被验签失败路径捕获。** 这是本任务想要的——攻击者改 SenderId / ConversationId / Content / FileMeta.ChunkSize 全部都会被验签拦截。
- **Verification：** 9 条新增 `MessageSigningTests` + 既有 106 条测试不变覆盖「端到端还原 / SenderId 篡改 / Payload 篡改 / 缺签名 / 缺公钥 / NodeId 一致性 / 线缆序列化往返 / 静态往返 / 双节点集成」全部路径。`Agent.md` §"群消息安全语义" 段无需更新（本任务为签名，与加密并列）。
- **同步修订 `notes/implemented/bug-fix/2026-09-20-message-sender-identity.md` §"Not covered" 段**——按 README §"Moving between lifecycles" 允许 editing implemented note to track where its existing decision lives 的规定，把 §"Not covered" 段从「未覆盖 SenderId 可伪造」改写为「已在 [2026-09-21-message-signing](2026-09-21-message-signing.md) 修复」并加交叉链接。决策本身未改。