namespace P2PChat.Core.Enums;

/// <summary>
/// 消息类型标识，用于网络协议和消息路由
/// </summary>
public enum MessageType : byte
{
    /// <summary>未知类型</summary>
    Unknown = 0,
    /// <summary>私聊文字消息</summary>
    PrivateText = 1,
    /// <summary>群聊文字消息</summary>
    GroupText = 2,
    /// <summary>文件传输元数据</summary>
    FileMeta = 3,
    /// <summary>文件分块数据</summary>
    FileChunk = 4,
    /// <summary>文件传输确认</summary>
    FileAck = 5,
    /// <summary>DHT RPC消息</summary>
    DhtRpc = 6,
    /// <summary>密钥交换请求/响应</summary>
    KeyExchange = 7,
    /// <summary>群组邀请</summary>
    GroupInvite = 8,
    /// <summary>群组操作通知</summary>
    GroupNotify = 9,
    /// <summary>消息送达确认</summary>
    DeliveryAck = 10,
}
