using P2PChat.Core.Models;

namespace P2PChat.Core.Abstractions;

/// <summary>
/// 群组元数据持久化 —— 群名、成员、创建者、群密钥等。
/// <para>
/// 与 <see cref="IKeyStore"/> 职责分离：<see cref="IKeyStore"/> 仅管密钥，
/// 本接口管 <see cref="GroupInfo"/> 全字段（成员名单、群名、创建时间、群密钥等）。
/// 持久化在密钥拥有层（<c>Crypto</c>）完成，避免 <c>Chat</c> 反向依赖文件路径细节。
/// </para>
/// <para>
/// 文件路径走 <c>~/.p2pc/groups.json</c>，与 <c>identity.json</c> / <c>group_keys.json</c> 同源。
/// </para>
/// </summary>
public interface IGroupMetadataStore
{
    /// <summary>
    /// 加载全部群组元数据。失败（含文件不存在 / 解析失败）应返回空集合，由上层决定是否走静默路径。
    /// </summary>
    IReadOnlyList<GroupInfo> LoadAll();

    /// <summary>
    /// 全量写一次（覆盖式）。调用方按变更频率触发。
    /// </summary>
    void Save(IEnumerable<GroupInfo> groups);

    /// <summary>
    /// 删除单个群组条目。下一次 <see cref="LoadAll"/> 不再包含该 GroupId。
    /// </summary>
    void Remove(string groupId);
}