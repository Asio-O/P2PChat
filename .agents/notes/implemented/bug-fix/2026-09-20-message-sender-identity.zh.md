# Agent Note: 消息 SenderId 必须派生自节点身份

Status: implemented

## Problem

线路上所有消息的 `SenderId` 都写成 `identity.PublicKey.Take(20)`，原注释为「简化: 用公钥前20字节作ID」。这**不是**身份，而是一段常量。

身份公钥由 `ECDiffieHellman.ExportSubjectPublicKeyInfo()` 产出，是 P-256 的 **SubjectPublicKeyInfo（DER，91 字节）**。其前 27 字节是与密钥内容无关的**固定算法头**（SEQUENCE + 算法 OID + BIT STRING 头），因此 `Take(20)` 对**每个节点**都产出同一串字节。实测两个节点落盘的公钥：

```
nodeA: MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE UQ8VvJm...
nodeB: MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE l5Xxzmn...
```

前 36 个 base64 字符（= 27 字节）逐字节相同。

而节点身份的真实定义是 `NodeId.FromPublicKey` = **SHA-1(公钥)**（`NodeId.cs:27-31`），它用于 `localNode.NodeId`、路由表、联系人、`/add` 与 `/id`。于是**线路上自报的身份**与**本地认知的身份**成了两套互不相干的值。

后果三处：

- **会话密钥槽位冲突。** `KeyExchangeHandler` 以 `new NodeId(message.SenderId)` 作 `keyStore` 的键，即所有对端共用同一个槽位。两个节点时侥幸可用；**第三个节点一接入就覆盖先前对端的会话密钥**，其消息随即解密失败。
- **联系人别名永远解析不到。** UI 用 `contactService.FindByNodeId(chatEvent.SenderId)` 查别名，而联系人以真实 NodeId 存储 → 恒返回 `null`，发送者显示为一串十六进制。
- **群创建者身份失去意义。** `GroupInviteMessage.CreatorId` 对所有节点都相同。

它同时与既有文档契约冲突：`Agent.md:595` 声明信封中该字段是 `SenderId (NodeId raw bytes)`。

## Decision

`SenderId` 一律取**本节点身份**，且身份只有**一个定义点**：`KeyPair.NodeId`。

```csharp
public record KeyPair
{
    public Models.NodeId NodeId => Models.NodeId.FromPublicKey(PublicKey);
}
```

- 私聊消息、密钥交换请求与响应、群消息、群创建者 ID 全部改用 `identity.NodeId`（`ChatService` 2 处、`KeyExchangeHandler` 1 处、`GroupChatService` 3 处）。
- 不再有任何地方从 `PublicKey` 手工截取字节。
- 测试脚手架同步：`NodeHarness.SenderId` 从「复刻实现的写法」改为复用 `KeyPair.NodeId`。

由于双方现在都自报真实身份，会话密钥自然落在**对端真实 NodeId** 之下：发送方登记于 `recipient.NodeId`，接收方登记于 `new NodeId(message.SenderId)`，两者一致。

## Alternatives considered

**继续从公钥截取一段字节作为 SenderId（只更换偏移量）。** 否决：截取不是派生。它与 `NodeId.FromPublicKey` 并列会形成两个身份来源，二者一旦不一致就重现本次缺陷；且截取结果依赖 DER 编码细节，结构一变即失效。

**保留 `PublicKey.Take(20)`，改为在 `keyStore` 里用完整公钥作键。** 否决：只治会话密钥槽位一处症状，联系人别名与群创建者仍然错，而且线路上依旧没有任何真实身份可供校验。

**在 `NodeId` 之外新增一个「消息身份」类型。** 否决：这会引入第二个身份概念。真正缺的是一个权威派生点，不是缺一个类型。

## Consequences

- 线路上自报的身份与本地认知的身份统一，`Agent.md:595` 的契约重新成立。
- 会话密钥按对端真实身份分槽，**支持任意数量的对端**；第三个节点接入不再破坏已有会话。
- 联系人别名、群创建者、发送者显示全部恢复正常。
- 密钥交换的缓存变得有效。此前因键不一致，双方各自把密钥登记在对方「看不到」的槽位，导致方向性重复握手。
- **代价：线路语义变更，新旧版本不能互通。** 旧版本的 `SenderId` 是常量，新版本收到后按真实 NodeId 查不到会话密钥，消息会被丢弃并记录 `私聊消息缺少会话密钥`。当前不存在需要兼容的可用版本，故不提供迁移。
- 守卫测试 `IdentityAndEndpointTests.身份_SenderId不得取公钥前20字节` 先断言「不同节点的公钥前 20 字节确实相同」，再断言 `SenderId` 不等于它——把缺陷机制本身钉进测试，改回旧写法立刻变红。
- **未覆盖：`SenderId` 仍可伪造。** 本变更只保证诚实节点自报真实身份，没有签名，攻击者可任意填写他人的 `SenderId`。修法是用长期 ECDSA 私钥签名（`IEncryptionService.Sign`/`Verify` 已存在但全仓库无调用点），属后续阶段。
