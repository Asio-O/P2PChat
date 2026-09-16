using MessagePack;
using P2PChat.Core.Enums;

namespace P2PChat.Core.Models;

/// <summary>
/// 消息基类 — 所有聊天/文件/DHT消息的基类
/// </summary>
[MessagePackObject]
[Union(0, typeof(TextMessage))]
[Union(1, typeof(FileMetaMessage))]
[Union(2, typeof(FileChunkMessage))]
[Union(3, typeof(FileAckMessage))]
[Union(4, typeof(KeyExchangeMessage))]
[Union(5, typeof(GroupInviteMessage))]
[Union(6, typeof(GroupNotifyMessage))]
[Union(7, typeof(DeliveryAckMessage))]
public abstract record Message
{
    /// <summary>消息唯一ID (Guid)</summary>
    /// <remarks>
    /// 使用 set 而非 init：init 属性 + 初始化器在反序列化时会被无条件重置为 default(MsgPack017)，
    /// 而 set 访问器允许生成 formatter 仅在数据中确实存在该键时才赋值。
    /// </remarks>
    [Key(0)]
    public Guid MessageId { get; set; } = Guid.NewGuid();

    /// <summary>发送者节点ID</summary>
    [Key(1)]
    public required byte[] SenderId { get; init; }

    /// <summary>UTC毫秒时间戳</summary>
    /// <remarks>set 而非 init，理由同 MessageId (MsgPack017)。</remarks>
    [Key(2)]
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>会话ID (私聊: 目标节点ID hex, 群聊: 群组ID)</summary>
    [Key(3)]
    public required string ConversationId { get; init; }
}
