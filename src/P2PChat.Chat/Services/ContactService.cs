using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Models;
using P2PChat.Crypto.Keys;

namespace P2PChat.Chat.Services;

/// <summary>
/// 联系人服务 — 联系人管理、在线状态跟踪
/// 持久化: 用户主目录/.p2pc/contacts.json（默认；P2PCHAT_DATA_DIR 可覆盖）
/// </summary>
public class ContactService : IContactService
{
    private readonly ILogger<ContactService> _logger;
    private readonly string _contactsPath;
    private readonly ConcurrentDictionary<string, Contact> _contacts = new(); // key: NodeId hex
    private readonly Channel<ContactStatusEvent> _statusChannel;

    public IAsyncEnumerable<ContactStatusEvent> OnStatusChanged => _statusChannel.Reader.ReadAllAsync();

    public ContactService(ILogger<ContactService> logger)
    {
        _logger = logger;
        _statusChannel = Channel.CreateUnbounded<ContactStatusEvent>();
        _contactsPath = Core.Extensions.DataPath.GetPath("contacts.json");
        LoadContacts();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Contact>> GetAllContactsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<Contact>>(
            _contacts.Values.OrderBy(c => c.IsOnline ? 0 : 1).ThenBy(c => c.Alias).ToList()
        );
    }

    /// <inheritdoc />
    public Task AddContactAsync(NodeId nodeId, string alias, string? endPoint = null, CancellationToken ct = default)
    {
        var contact = new Contact
        {
            NodeId = nodeId,
            Alias = alias,
            EndPoint = string.IsNullOrWhiteSpace(endPoint) ? null : endPoint.Trim(),
            AddedAt = DateTime.UtcNow
        };
        _contacts[nodeId.ToHexString()] = contact;
        SaveContacts();
        _logger.LogInformation("添加联系人: {Alias} ({NodeId}){EndPoint}",
            alias, nodeId.ToHexString()[..8],
            contact.EndPoint is null ? "" : $" @ {contact.EndPoint}");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveContactAsync(NodeId nodeId, CancellationToken ct = default)
    {
        _contacts.TryRemove(nodeId.ToHexString(), out _);
        SaveContacts();
        _logger.LogInformation("移除联系人: {NodeId}", nodeId.ToHexString()[..8]);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateAliasAsync(NodeId nodeId, string alias, CancellationToken ct = default)
    {
        var key = nodeId.ToHexString();
        if (_contacts.TryGetValue(key, out var contact))
        {
            contact.Alias = alias;
            SaveContacts();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task UpdateOnlineStatusAsync(NodeId nodeId, bool isOnline, CancellationToken ct = default)
    {
        var key = nodeId.ToHexString();
        if (_contacts.TryGetValue(key, out var contact))
        {
            contact.IsOnline = isOnline;
            contact.State = isOnline ? Core.Enums.PeerState.Online : Core.Enums.PeerState.Offline;
            if (isOnline) contact.LastOnlineAt = DateTime.UtcNow;

            await _statusChannel.Writer.WriteAsync(new ContactStatusEvent
            {
                NodeId = nodeId,
                IsOnline = isOnline,
                State = contact.State
            }, ct);
        }
    }

    /// <inheritdoc />
    public Contact? FindByNodeId(NodeId nodeId)
    {
        return _contacts.TryGetValue(nodeId.ToHexString(), out var c) ? c : null;
    }

    private void LoadContacts()
    {
        if (!File.Exists(_contactsPath)) return;
        try
        {
            var json = File.ReadAllText(_contactsPath);
            var stored = DeserializeContacts(json);
            if (stored != null)
            {
                foreach (var sc in stored)
                {
                    // 单条坏数据不应连累其余联系人：NodeId 十六进制非法（长度不对 / 含非 hex 字符）
                    // 时只跳过这一条并告警，而不是让整个文件加载失败、全部联系人静默消失。
                    if (!TryParseNodeId(sc.NodeId, out var nodeId))
                    {
                        _logger.LogWarning("联系人 [{Alias}] 的 NodeId 非法，已跳过: {NodeId}", sc.Alias, sc.NodeId);
                        continue;
                    }

                    // 键必须是**规范化后**的小写 hex（NodeId.ToHexString 即小写），
                    // 与 Add/Remove/FindByNodeId 用的键同源。
                    // 历史上这里直接用磁盘里的原始 sc.NodeId 当键，于是任何写成大写 hex 的
                    // contacts.json（手工编辑 / 从别处导入）加载后全部联系人静默丢失 ——
                    // 查找不到，但文件读起来「成功」。
                    _contacts[nodeId.ToHexString()] = new Contact
                    {
                        NodeId = nodeId,
                        Alias = sc.Alias,
                        EndPoint = sc.EndPoint,
                        AddedAt = sc.AddedAt
                    };
                }
            }
            _logger.LogDebug("加载了 {Count} 个联系人", _contacts.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载联系人失败");
        }
    }

    /// <summary>
    /// 解析 contacts.json。正常形状是**数组**（<c>SaveContacts</c> 写出的就是这个）。
    /// <para>
    /// 但单个联系人被写成裸对象 <c>{...}</c> 是最常见的手改失误（PowerShell 的
    /// <c>@(...) | ConvertTo-Json</c> 在单元素时就会折叠成对象）。此时直接按
    /// <c>List&lt;StoredContact&gt;</c> 反序列化会抛 <c>JsonException</c>，
    /// 而外层 catch 会把**全部**联系人静默清空 —— 用户看到的是「联系人都没了」。
    /// </para>
    /// <para>
    /// 因此这里退化重试一次单对象形状。两种形状都不成立才交给外层按「加载失败」处理。
    /// </para>
    /// </summary>
    private List<StoredContact>? DeserializeContacts(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, JsonContext.Default.ListStoredContact);
        }
        catch (JsonException)
        {
            try
            {
                var single = JsonSerializer.Deserialize(json, JsonContext.Default.StoredContact);
                if (single is not null)
                {
                    _logger.LogWarning("contacts.json 顶层是单个对象而非数组，已按 1 条联系人兼容处理（建议改回数组）");
                    return [single];
                }
            }
            catch (JsonException)
            {
                // 两种形状都不成立 —— 交给外层记录「加载联系人失败」
            }
            return null;
        }
    }

    /// <summary>把磁盘上的 NodeId 文本解析为 <see cref="NodeId"/>；失败返回 false 而不抛。</summary>
    private static bool TryParseNodeId(string? hex, out NodeId nodeId)
    {
        nodeId = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try
        {
            nodeId = new NodeId(Convert.FromHexString(hex.Trim()));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void SaveContacts()
    {
        try
        {
            var stored = _contacts.Values.Select(c => new StoredContact
            {
                NodeId = c.NodeId.ToHexString(),
                Alias = c.Alias,
                EndPoint = c.EndPoint,
                AddedAt = c.AddedAt
            }).ToList();

            File.WriteAllText(_contactsPath,
                JsonSerializer.Serialize(stored, JsonContext.Default.ListStoredContact));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存联系人失败");
        }
    }
}
