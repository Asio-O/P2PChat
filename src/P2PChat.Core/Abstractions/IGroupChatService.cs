using P2PChat.Core.Models;

namespace P2PChat.Core.Abstractions;

/// <summary>
/// 群聊服务 — 群组创建、成员管理、群消息收发
/// </summary>
public interface IGroupChatService
{
    /// <summary>
    /// 创建群组
    /// </summary>
    /// <param name="groupName">群组名称</param>
    /// <param name="initialMembers">初始成员节点ID列表</param>
    Task<GroupInfo> CreateGroupAsync(string groupName, IReadOnlyList<NodeId> initialMembers, CancellationToken ct = default);

    /// <summary>
    /// 发送群消息 (向所有在线成员扇出)
    /// </summary>
    Task SendGroupMessageAsync(string groupId, string text, CancellationToken ct = default);

    /// <summary>
    /// 获取群组信息
    /// </summary>
    GroupInfo? GetGroup(string groupId);

    /// <summary>
    /// 获取所有已知群组
    /// </summary>
    IReadOnlyList<GroupInfo> GetKnownGroups();

    /// <summary>
    /// 获取群组在线成员
    /// </summary>
    IReadOnlyList<NodeInfo> GetOnlineMembers(string groupId);

    /// <summary>
    /// 处理收到的群组邀请
    /// </summary>
    Task HandleInviteAsync(GroupInviteMessage invite, CancellationToken ct = default);

    /// <summary>
    /// 处理群组通知
    /// </summary>
    Task HandleNotifyAsync(GroupNotifyMessage notify, CancellationToken ct = default);
}
