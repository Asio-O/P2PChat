using MessagePack;

namespace P2PChat.Core.Models;

/// <summary>
/// 群组邀请消息
/// </summary>
[MessagePackObject]
public record GroupInviteMessage : Message
{
    /// <summary>群组ID</summary>
    [Key(10)]
    public required string GroupId { get; init; }

    /// <summary>群组名称</summary>
    [Key(11)]
    public required string GroupName { get; init; }

    /// <summary>加密后的群组密钥 (用被邀请者公钥加密)</summary>
    [Key(12)]
    public required byte[] EncryptedGroupKey { get; init; }

    /// <summary>初始成员列表</summary>
    [Key(13)]
    public required List<byte[]> MemberIds { get; init; }

    /// <summary>
    /// 邀请发送方的身份公钥，用于接收方通过ECDH派生群组密钥包装密钥。
    /// 这是追加字段，保留既有消息索引不变。
    /// </summary>
    [Key(14)]
    public byte[]? SenderPublicKey { get; init; }

    /// <summary>
    /// AES-GCM群组密钥密文的附加认证数据: [12字节nonce] + [16字节tag]。
    /// EncryptedGroupKey 保存密文主体；这是追加字段，保留既有消息索引不变。
    /// </summary>
    [Key(15)]
    public byte[]? EncryptedGroupKeyNonceAndTag { get; init; }
}
