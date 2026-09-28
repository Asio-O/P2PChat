using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Models;

namespace P2PChat.Crypto.Keys;

/// <summary>
/// 文件支持的群组元数据存储 —— 持久化 <see cref="GroupInfo"/> 到
/// <c>~/.p2pc/groups.json</c>。文件格式走 <see cref="JsonContext"/>（<c>System.Text.Json</c> 源生成器，AOT 安全）。
/// <para>
/// 与 <see cref="FileBackedKeyStore"/> 同源：同一数据目录、同一种序列化方式、同一种
/// "加载失败静默吞" 容错策略；本类只负责群组元数据，密钥持久化仍在
/// <see cref="IKeyStore"/>。
/// </para>
/// <para>
/// 字段说明见 <see cref="StoredGroup"/>。所有 <c>byte[]</c> 字段以 hex 字符串
/// 写入（与 <see cref="NodeId.ToHexString"/> 风格一致），群密钥以 base64 写入。
/// </para>
/// </summary>
public class FileBackedGroupMetadataStore : IGroupMetadataStore
{
    private const string FileName = "groups.json";

    private readonly ILogger<FileBackedGroupMetadataStore> _logger;
    private readonly string _path;
    private readonly object _saveLock = new();

    // AOT友好的JSON选项 —— 必须使用源生成器上下文，禁止反射
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        TypeInfoResolver = JsonContext.Default,
        WriteIndented = true
    };

    public FileBackedGroupMetadataStore(ILogger<FileBackedGroupMetadataStore> logger)
    {
        _logger = logger;
        _path = Core.Extensions.DataPath.GetPath(FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _logger.LogInformation("群组元数据持久化文件: {Path}", _path);
    }

    /// <inheritdoc />
    public IReadOnlyList<GroupInfo> LoadAll()
    {
        if (!File.Exists(_path))
        {
            _logger.LogDebug("群组元数据文件不存在，跳过加载: {Path}", _path);
            return Array.Empty<GroupInfo>();
        }

        try
        {
            var json = File.ReadAllText(_path);
            var stored = JsonSerializer.Deserialize(json, JsonContext.Default.ListStoredGroup);
            if (stored == null)
            {
                _logger.LogWarning("群组元数据文件解析为空: {Path}", _path);
                return Array.Empty<GroupInfo>();
            }

            var groups = new List<GroupInfo>(stored.Count);
            foreach (var s in stored)
            {
                var key = TryFromBase64(s.GroupKeyBase64);
                if (key == null || key.Length != 32)
                {
                    _logger.LogWarning("群组 {GroupId} 的群密钥缺失或长度异常，已跳过", s.GroupId);
                    continue;
                }

                var memberIds = new List<byte[]>(s.MemberIdsHexList.Count);
                foreach (var hex in s.MemberIdsHexList)
                {
                    try { memberIds.Add(Convert.FromHexString(hex)); }
                    catch (FormatException)
                    {
                        _logger.LogWarning("群组 {GroupId} 的成员ID格式异常: {Hex}", s.GroupId, hex);
                    }
                }

                byte[] creatorBytes;
                try { creatorBytes = Convert.FromHexString(s.CreatorIdHex); }
                catch (FormatException)
                {
                    _logger.LogWarning("群组 {GroupId} 的创建者ID格式异常，已跳过", s.GroupId);
                    continue;
                }

                groups.Add(new GroupInfo
                {
                    GroupId = s.GroupId,
                    GroupName = s.GroupName,
                    CreatorId = new NodeId(creatorBytes),
                    CreatedAt = s.CreatedAt,
                    MemberIds = memberIds,
                    GroupKey = key
                });
            }

            _logger.LogInformation("已加载 {Count} 个群组元数据", groups.Count);
            return groups;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载群组元数据失败，返回空列表: {Path}", _path);
            return Array.Empty<GroupInfo>();
        }
    }

    /// <inheritdoc />
    public void Save(IEnumerable<GroupInfo> groups)
    {
        if (groups == null) throw new ArgumentNullException(nameof(groups));

        var stored = new List<StoredGroup>();
        foreach (var g in groups)
        {
            stored.Add(new StoredGroup
            {
                GroupId = g.GroupId,
                GroupName = g.GroupName,
                CreatorIdHex = Convert.ToHexString(g.CreatorId.ToByteArray()).ToUpperInvariant(),
                MemberIdsHexList = g.MemberIds
                    .Select(b => Convert.ToHexString(b).ToUpperInvariant())
                    .ToList(),
                GroupKeyBase64 = Convert.ToBase64String(g.GroupKey),
                CreatedAt = g.CreatedAt
            });
        }

        lock (_saveLock)
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(stored, JsonContext.Default.ListStoredGroup));
        }
        _logger.LogDebug("群组元数据已写入: {Count} 个群组 -> {Path}", stored.Count, _path);
    }

    /// <inheritdoc />
    public void Remove(string groupId)
    {
        if (string.IsNullOrEmpty(groupId)) return;

        var all = LoadAll();
        var filtered = all.Where(g => g.GroupId != groupId).ToList();
        if (filtered.Count == all.Count)
        {
            // 没有匹配项 —— 文件本来就干净
            return;
        }

        Save(filtered);
        _logger.LogInformation("群组 {GroupId} 已从 {Path} 移除", groupId, _path);
    }

    private static byte[]? TryFromBase64(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }
}