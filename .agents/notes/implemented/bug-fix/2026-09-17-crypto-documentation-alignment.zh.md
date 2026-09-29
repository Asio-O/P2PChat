# Agent Note: 加密算法注释订正为实际实现

Status: implemented

## Problem

源码注释描述的算法与实现不符，且所有不符之处都指向**另一套**密码学方案：

| 位置 | 注释原文（错误） | 实现实际 |
|---|---|---|
| `NodeInfo.PublicKey` | Ed25519 公钥 (32 字节) | ECDH nistP256，SubjectPublicKeyInfo (DER, 91 字节) |
| `IEncryptionService.GenerateKeyPair` | 生成 ECDH 密钥对 (X25519) | `ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)` |
| `IEncryptionService.Sign` / `Verify` | Ed25519 签名 / 验签（或 ECDsa 回退） | `ECDsa.Create(nistP256)` + SHA-256 |
| `AesGcmEncryptionService` 类注释 | ECDH(X25519) 密钥协商 | ECDH(nistP256) |
| `KeyExchangeMessage.EphemeralPublicKey` | 发送方临时公钥 (X25519, 32 字节) | P-256 SubjectPublicKeyInfo (DER, 91 字节) |

这不是措辞不够精确，而是**指向了另一套方案**：X25519 / Ed25519 的公钥是 32 字节定长，而 P-256 的 SubjectPublicKeyInfo 是 91 字节 DER。任何按注释编写的代码——尤其是按 32 字节定长去解析或构造 `EphemeralPublicKey`、`NodeInfo.PublicKey` 的代码——都会直接失败。

注释在此承担的是**跨包契约**的角色：`IEncryptionService` 位于 `P2PChat.Core`，实现位于 `P2PChat.Crypto`，两个包之间没有任何编译期约束能发现「注释声称的算法」与「实现使用的算法」不一致。

## Decision

把注释订正为实际实现，代码不动。

实际交付的密码学方案（现已写明于注释）：

- 密钥协商：**ECDH nistP256**
- 会话密钥派生：**HKDF-SHA256**（输出 32 字节）
- 加解密：**AES-256-GCM**
- 签名 / 验签：**ECDSA P-256 + SHA-256**
- 密钥编码：公钥为 **SubjectPublicKeyInfo (DER)**，私钥为 **ECPrivateKey (DER)**，由 `ExportSubjectPublicKeyInfo` / `ExportECPrivateKey` 产生，与对应的 Import 方法配套。

订正后的注释同时承载两条**负面保证**：`NodeInfo.PublicKey` 明确写「不是 Ed25519」；`KeyExchangeMessage.EphemeralPublicKey` 明确写「不是 X25519 —— 32 字节固定长度的写法在此不适用」。

## Alternatives considered

**改代码去匹配注释，即真正实现 X25519 + Ed25519。** 否决：没有任何功能需求要求更换算法；P-256 方案已在初始提交中交付并通过端到端测试；且更换方案会改变 `EphemeralPublicKey` 与 `NodeInfo.PublicKey` 的线上字节格式（91 字节 DER → 32 字节定长），属于破坏性协议变更。让文档迁就现实，而不是反过来。

**从注释中删除算法名，只保留「生成密钥对」「签名」这类中性描述。** 否决：算法身份是调用方必须知道的契约——它决定了公钥的字节长度与编码格式，也正是本次漂移造成风险的地方。删掉算法名会让契约更模糊，而非更清晰。

**保持注释不变，只在实现处补写注释。** 否决：错误注释位于 `P2PChat.Core` 的公共抽象上，是绝大多数读者第一眼看到的位置；在实现处补注释治不了根因。

**引入编译期或测试期断言，使注释与实现无法再次漂移。** 未采用（非否决）：注释文本无法被自动校验，能做到的只有针对**行为**的测试，而行为本来已被现有测试覆盖。真正的护栏是把契约写进注释、靠评审维持——这是本仓库当前的取舍。

## Consequences

- 读者不再被引向一套并不存在的 X25519 / Ed25519 方案；按注释实现解析代码的人不会踩 32 字节 vs 91 字节的坑。
- 线上与磁盘字节格式**未变**：`NodeInfo.PublicKey` 仍是 91 字节 SPKI DER，`EphemeralPublicKey` 同理。已生成的 `identity.json` 与既有节点 ID 不受影响。
- 本变更是纯注释改动，无行为变化，现有测试全部照常通过——这也意味着**没有任何测试能防止同类漂移再次发生**。注释与实现的一致性依赖评审，而非工具。
- 记录一项事实供后续参考：本项目**从未**使用 X25519 或 Ed25519。历史注释中的这两个名字是文档错误，不是被放弃的旧实现。
