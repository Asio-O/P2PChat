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
/// 持久化: %APPDATA%/P2PChat/contacts.json
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
            var stored = JsonSerializer.Deserialize(json, JsonContext.Default.ListStoredContact);
            if (stored != null)
            {
                foreach (var sc in stored)
                {
                    _contacts[sc.NodeId] = new Contact
                    {
                        NodeId = new NodeId(Convert.FromHexString(sc.NodeId)),
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
