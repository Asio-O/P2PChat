using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Models;

namespace P2PChat.Crypto.Keys;

/// <summary>
/// 文件支持的密钥存储 — 身份密钥、会话密钥、群组密钥持久化
/// 存储目录: %APPDATA%/P2PChat/
/// </summary>
public class FileBackedKeyStore : IKeyStore
{
    private readonly ILogger<FileBackedKeyStore> _logger;
    private readonly string _storeDirectory;
    private readonly IEncryptionService _encryption;
    private KeyPair? _identityKey;
    private readonly ConcurrentDictionary<string, byte[]> _sessionKeys = new();
    private readonly ConcurrentDictionary<string, byte[]> _groupKeys = new();
    private readonly object _saveLock = new();

    // AOT友好的JSON选项 — 必须使用源生成器上下文
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        TypeInfoResolver = JsonContext.Default
    };

    public FileBackedKeyStore(IEncryptionService encryption, ILogger<FileBackedKeyStore> logger)
    {
        _encryption = encryption;
        _logger = logger;
        _storeDirectory = Core.Extensions.DataPath.Root;
        Directory.CreateDirectory(_storeDirectory);
        _logger.LogInformation("密钥存储目录: {Dir}", _storeDirectory);

        LoadAll();
    }

    /// <inheritdoc />
    public KeyPair GetOrCreateIdentity()
    {
        if (_identityKey != null) return _identityKey;

        var identityPath = Path.Combine(_storeDirectory, "identity.json");
        if (File.Exists(identityPath))
        {
            var json = File.ReadAllText(identityPath);
            var stored = JsonSerializer.Deserialize(json, JsonContext.Default.StoredKeyPair);
            if (stored != null)
            {
                _identityKey = new KeyPair
                {
                    PublicKey = Convert.FromBase64String(stored.PublicKey),
                    PrivateKey = Convert.FromBase64String(stored.PrivateKey)
                };
                _logger.LogInformation("已加载身份密钥");
                return _identityKey;
            }
        }

        // 首次运行: 生成新密钥对
        _identityKey = _encryption.GenerateKeyPair();
        var toStore = new StoredKeyPair
        {
            PublicKey = Convert.ToBase64String(_identityKey.PublicKey),
            PrivateKey = Convert.ToBase64String(_identityKey.PrivateKey),
            CreatedAt = DateTime.UtcNow
        };
        File.WriteAllText(identityPath, JsonSerializer.Serialize(toStore, JsonContext.Default.StoredKeyPair));
        _logger.LogInformation("生成并存储新的身份密钥");
        return _identityKey;
    }

    /// <inheritdoc />
    public byte[]? GetSessionKey(NodeId peerId)
    {
        var key = peerId.ToHexString();
        return _sessionKeys.TryGetValue(key, out var value) ? value : null;
    }

    /// <inheritdoc />
    public void SetSessionKey(NodeId peerId, byte[] sharedSecret)
    {
        var key = peerId.ToHexString();
        _sessionKeys[key] = sharedSecret;
        SaveSessionKeys();
    }

    /// <inheritdoc />
    public void RemoveSessionKey(NodeId peerId)
    {
        _sessionKeys.TryRemove(peerId.ToHexString(), out _);
        SaveSessionKeys();
    }

    /// <inheritdoc />
    public byte[]? GetGroupKey(string groupId)
    {
        return _groupKeys.TryGetValue(groupId, out var key) ? key : null;
    }

    /// <inheritdoc />
    public void SetGroupKey(string groupId, byte[] key)
    {
        _groupKeys[groupId] = key;
        SaveGroupKeys();
    }

    /// <inheritdoc />
    public void RemoveGroupKey(string groupId)
    {
        _groupKeys.TryRemove(groupId, out _);
        SaveGroupKeys();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetKnownGroupIds()
    {
        return _groupKeys.Keys.ToList();
    }

    private void LoadAll()
    {
        // 加载会话密钥
        var sessionPath = Path.Combine(_storeDirectory, "session_keys.json");
        if (File.Exists(sessionPath))
        {
            try
            {
                var json = File.ReadAllText(sessionPath);
                var stored = JsonSerializer.Deserialize(json, JsonContext.Default.DictionaryStringString);
                if (stored != null)
                {
                    foreach (var (k, v) in stored)
                        _sessionKeys[k] = Convert.FromBase64String(v);
                }
            }
            catch { }
        }

        // 加载群组密钥
        var groupsPath = Path.Combine(_storeDirectory, "group_keys.json");
        if (File.Exists(groupsPath))
        {
            try
            {
                var json = File.ReadAllText(groupsPath);
                var stored = JsonSerializer.Deserialize(json, JsonContext.Default.DictionaryStringString);
                if (stored != null)
                {
                    foreach (var (k, v) in stored)
                        _groupKeys[k] = Convert.FromBase64String(v);
                }
            }
            catch { }
        }
    }

    private void SaveSessionKeys()
    {
        lock (_saveLock)
        {
            var dict = _sessionKeys.ToDictionary(
                kvp => kvp.Key,
                kvp => Convert.ToBase64String(kvp.Value)
            );
            var path = Path.Combine(_storeDirectory, "session_keys.json");
            File.WriteAllText(path, JsonSerializer.Serialize(dict, JsonContext.Default.DictionaryStringString));
        }
    }

    private void SaveGroupKeys()
    {
        lock (_saveLock)
        {
            var dict = _groupKeys.ToDictionary(
                kvp => kvp.Key,
                kvp => Convert.ToBase64String(kvp.Value)
            );
            var path = Path.Combine(_storeDirectory, "group_keys.json");
            File.WriteAllText(path, JsonSerializer.Serialize(dict, JsonContext.Default.DictionaryStringString));
        }
    }
}

/// <summary>
/// 持久化密钥对
/// </summary>
public class StoredKeyPair
{
    public required string PublicKey { get; set; }
    public required string PrivateKey { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// JSON源生成器上下文 (AOT友好)
/// </summary>
[JsonSerializable(typeof(StoredKeyPair))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<StoredContact>))]
[JsonSerializable(typeof(StoredContact))]
[JsonSerializable(typeof(StoredGroup))]
public partial class JsonContext : JsonSerializerContext
{
}
