using P2PChat.Core.Models;

namespace P2PChat.Core.Abstractions;

/// <summary>
/// 联系人服务 — 联系人管理、在线状态跟踪
/// </summary>
public interface IContactService
{
    /// <summary>
    /// 获取所有联系人
    /// </summary>
    Task<IReadOnlyList<Contact>> GetAllContactsAsync(CancellationToken ct = default);

    /// <summary>
    /// 添加联系人
    /// </summary>
    /// <param name="nodeId">对端节点ID</param>
    /// <param name="alias">别名</param>
    /// <param name="endPoint">
    /// 可选显式端点（<c>ip:port</c>）。指定后该对端无需 DHT 发现即可直连，
    /// 这是当前唯一可靠的直连手段（公共 DHT 无法解析任意 NodeId）。
    /// </param>
    /// <param name="ct">取消标记</param>
    Task AddContactAsync(NodeId nodeId, string alias, string? endPoint = null, CancellationToken ct = default);

    /// <summary>
    /// 移除联系人
    /// </summary>
    Task RemoveContactAsync(NodeId nodeId, CancellationToken ct = default);

    /// <summary>
    /// 更新联系人别名
    /// </summary>
    Task UpdateAliasAsync(NodeId nodeId, string alias, CancellationToken ct = default);

    /// <summary>
    /// 更新联系人在线状态
    /// </summary>
    Task UpdateOnlineStatusAsync(NodeId nodeId, bool isOnline, CancellationToken ct = default);

    /// <summary>
    /// 根据节点ID查找联系人
    /// </summary>
    Contact? FindByNodeId(NodeId nodeId);

    /// <summary>
    /// 联系人状态变更事件流
    /// </summary>
    IAsyncEnumerable<ContactStatusEvent> OnStatusChanged { get; }
}
