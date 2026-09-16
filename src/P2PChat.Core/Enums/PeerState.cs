namespace P2PChat.Core.Enums;

/// <summary>
/// 对等节点在线状态
/// </summary>
public enum PeerState : byte
{
    /// <summary>离线</summary>
    Offline = 0,
    /// <summary>在线</summary>
    Online = 1,
    /// <summary>忙碌</summary>
    Busy = 2,
}
