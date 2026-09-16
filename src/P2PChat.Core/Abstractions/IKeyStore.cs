using P2PChat.Core.Models;

namespace P2PChat.Core.Abstractions;

/// <summary>
/// 密钥存储 — 持久化身份密钥、会话密钥、群组密钥
/// </summary>
public interface IKeyStore
{
    /// <summary>
    /// 获取或创建身份密钥对 (首次运行时生成)
    /// </summary>
    KeyPair GetOrCreateIdentity();

    /// <summary>
    /// 获取会话密钥 (ECDH协商后生成)
    /// </summary>
    byte[]? GetSessionKey(NodeId peerId);

    /// <summary>
    /// 存储会话密钥
    /// </summary>
    void SetSessionKey(NodeId peerId, byte[] sharedSecret);

    /// <summary>
    /// 删除会话密钥
    /// </summary>
    void RemoveSessionKey(NodeId peerId);

    /// <summary>
    /// 获取群组密钥
    /// </summary>
    byte[]? GetGroupKey(string groupId);

    /// <summary>
    /// 存储群组密钥
    /// </summary>
    void SetGroupKey(string groupId, byte[] key);

    /// <summary>
    /// 删除群组密钥
    /// </summary>
    void RemoveGroupKey(string groupId);

    /// <summary>
    /// 获取所有已知的群组ID
    /// </summary>
    IReadOnlyList<string> GetKnownGroupIds();
}
