using System.Threading.Channels;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;

namespace P2PChat.Chat.Handlers;

/// <summary>
/// 群组邀请消息处理器
/// </summary>
public class GroupInviteHandler : IMessageHandler<GroupInviteMessage>
{
    private const int GroupKeyLength = 32;
    private const int EcdhPublicKeyLength = 91; // P-256 SubjectPublicKeyInfo
    private const int AesGcmNonceLength = 12;
    private const int AesGcmTagLength = 16;
    private const int AesGcmNonceAndTagLength = AesGcmNonceLength + AesGcmTagLength;

    private readonly IEncryptionService _encryption;
    private readonly IKeyStore _keyStore;
    private readonly ILogger<GroupInviteHandler> _logger;
    private readonly Channel<GroupInviteMessage> _inviteChannel;

    public MessageType MessageType => MessageType.GroupInvite;
    public IAsyncEnumerable<GroupInviteMessage> OnInviteReceived => _inviteChannel.Reader.ReadAllAsync();

    public GroupInviteHandler(
        IEncryptionService encryption,
        IKeyStore keyStore,
        ILogger<GroupInviteHandler> logger)
    {
        _encryption = encryption;
        _keyStore = keyStore;
        _logger = logger;
        _inviteChannel = Channel.CreateUnbounded<GroupInviteMessage>();
    }

    public async Task HandleAsync(
        GroupInviteMessage message,
        ITcpConnection senderConnection,
        MessageEnvelope envelope,
        CancellationToken ct = default)
    {
        if (message is null)
        {
            _logger.LogError("群组邀请消息对象为空，原因=无法验证加密元数据，已丢弃邀请");
            return;
        }

        byte[] groupKey;
        try
        {
            groupKey = DecryptGroupKey(message);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex,
                "群组邀请解密失败，原因=ECDH或AES-GCM认证未通过，具体原因={Reason}，已丢弃邀请: {GroupId}",
                ex.Message, ShortGroupId(message.GroupId));
            return;
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex,
                "群组邀请格式或字段长度非法，具体原因={Reason}，已丢弃邀请: {GroupId}",
                ex.Message, ShortGroupId(message.GroupId));
            return;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex,
                "群组邀请密钥协商失败，具体原因={Reason}，已丢弃邀请: {GroupId}",
                ex.Message, ShortGroupId(message.GroupId));
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "群组邀请处理失败，具体原因={Reason}，已丢弃邀请: {GroupId}",
                ex.Message, ShortGroupId(message.GroupId));
            return;
        }

        if (groupKey is null)
        {
            _logger.LogError("群组邀请解密结果为空，原因=AES-GCM未产生群组密钥，已丢弃邀请: {GroupId}",
                ShortGroupId(message.GroupId));
            return;
        }

        if (groupKey.Length != GroupKeyLength)
        {
            _logger.LogError("群组邀请解密结果长度非法：实际{Length}字节，期望{Expected}字节，已丢弃邀请: {GroupId}",
                groupKey.Length, GroupKeyLength, ShortGroupId(message.GroupId));
            return;
        }

        _keyStore.SetGroupKey(message.GroupId, groupKey);

        _logger.LogInformation("收到群组邀请: {GroupName} ({GroupId})",
            message.GroupName, ShortGroupId(message.GroupId));

        await _inviteChannel.Writer.WriteAsync(message, ct);
    }

    private byte[] DecryptGroupKey(GroupInviteMessage message)
    {
        var encryptedGroupKey = message.EncryptedGroupKey;
        var senderPublicKey = message.SenderPublicKey;
        var nonceAndTag = message.EncryptedGroupKeyNonceAndTag;

        if (encryptedGroupKey is null)
            throw new ArgumentException("群组邀请缺少EncryptedGroupKey加密主体", nameof(message));
        if (encryptedGroupKey.Length != GroupKeyLength)
            throw new ArgumentException(
                $"群组邀请EncryptedGroupKey长度非法：实际{encryptedGroupKey.Length}字节，期望{GroupKeyLength}字节密文主体",
                nameof(message));

        if (senderPublicKey is null || senderPublicKey.Length == 0)
            throw new ArgumentException("群组邀请缺少SenderPublicKey发送方公钥元数据", nameof(message));
        if (senderPublicKey.Length != EcdhPublicKeyLength)
            throw new ArgumentException(
                $"群组邀请SenderPublicKey长度非法：实际{senderPublicKey.Length}字节，期望{EcdhPublicKeyLength}字节P-256公钥",
                nameof(message));

        if (nonceAndTag is null || nonceAndTag.Length == 0)
            throw new ArgumentException("群组邀请缺少EncryptedGroupKeyNonceAndTag nonce/tag元数据", nameof(message));
        if (nonceAndTag.Length != AesGcmNonceAndTagLength)
            throw new ArgumentException(
                $"群组邀请EncryptedGroupKeyNonceAndTag长度非法：实际{nonceAndTag.Length}字节，期望{AesGcmNonceAndTagLength}字节（12字节nonce+16字节tag）",
                nameof(message));

        var identity = _keyStore.GetOrCreateIdentity();
        if (identity.PrivateKey is null || identity.PrivateKey.Length == 0)
            throw new ArgumentException("本地身份私钥为空，无法执行ECDH", nameof(message));

        // 身份私钥只用于ECDH，不直接作为AES密钥。
        var sharedSecret = _encryption.DeriveSharedSecret(identity.PrivateKey, senderPublicKey);
        if (sharedSecret is null || sharedSecret.Length == 0)
            throw new CryptographicException("ECDH未派生出共享密钥");

        var wrappingKey = _encryption.DeriveSessionKey(sharedSecret);
        if (wrappingKey is null || wrappingKey.Length != GroupKeyLength)
            throw new CryptographicException(
                $"ECDH派生的AES包装密钥长度非法：实际{wrappingKey?.Length ?? 0}字节，期望{GroupKeyLength}字节");

        // 协议格式固定为 [12字节nonce][32字节密文主体][16字节tag]。
        var ciphertext = new byte[AesGcmNonceLength + encryptedGroupKey.Length + AesGcmTagLength];
        nonceAndTag.AsSpan(0, AesGcmNonceLength).CopyTo(ciphertext);
        encryptedGroupKey.AsSpan().CopyTo(ciphertext.AsSpan(AesGcmNonceLength));
        nonceAndTag.AsSpan(AesGcmNonceLength, AesGcmTagLength)
            .CopyTo(ciphertext.AsSpan(AesGcmNonceLength + encryptedGroupKey.Length));

        return _encryption.Decrypt(ciphertext, wrappingKey);
    }

    private static string ShortGroupId(string? groupId)
    {
        if (string.IsNullOrEmpty(groupId)) return "<empty>";
        return groupId[..Math.Min(8, groupId.Length)];
    }
}
