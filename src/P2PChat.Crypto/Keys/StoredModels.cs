namespace P2PChat.Crypto.Keys;

/// <summary>
/// 持久化联系人
/// </summary>
public class StoredContact
{
    public required string NodeId { get; set; }
    public string Alias { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; }

    /// <summary>显式端点（<c>ip:port</c>），可选。存在时优先于 DHT 查找结果。</summary>
    public string? EndPoint { get; set; }
}

/// <summary>
/// 持久化群组（<c>~/.p2pc/groups.json</c> 的 JSON 形态）。
/// <para>
/// 字段命名遵循 <c>System.Text.Json</c> 源生成器的可观测性要求：
/// <c>byte[]</c> 字段一律走 hex 字符串（与 <see cref="Models.NodeId.ToHexString"/> 风格一致），
/// 不引入二进制 <c>JsonPropertyName</c> 转换器。
/// </para>
/// <para>
/// 群密钥明文 base64 存储 —— 与现有 <c>group_keys.json</c> 同源（已是 base64），
/// 保持架构一致性；加密化超出本阶段范围。
/// </para>
/// </summary>
public class StoredGroup
{
    /// <summary>群组 ID（SHA-256 hex）。</summary>
    public required string GroupId { get; set; }

    /// <summary>群组显示名。</summary>
    public required string GroupName { get; set; }

    /// <summary>创建者节点 ID（20B SHA-1 hex，大写）。</summary>
    public required string CreatorIdHex { get; set; }

    /// <summary>成员节点 ID 列表（每项 20B SHA-1 hex，大写）。</summary>
    public List<string> MemberIdsHexList { get; set; } = new();

    /// <summary>群密钥 32 字节明文的 base64。</summary>
    public required string GroupKeyBase64 { get; set; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTime CreatedAt { get; set; }
}
