namespace P2PChat.Core.Enums;

/// <summary>
/// DHT RPC 消息类型 (Kademlia协议)
/// </summary>
public enum DhtMessageType : byte
{
    /// <summary>PING - 检测节点存活</summary>
    Ping = 0,
    /// <summary>STORE - 存储键值对</summary>
    Store = 1,
    /// <summary>FIND_NODE - 查找节点</summary>
    FindNode = 2,
    /// <summary>FIND_VALUE - 查找值</summary>
    FindValue = 3,
}
