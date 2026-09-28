using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;

namespace P2PChat.Chat.Handlers;

/// <summary>
/// 群聊消息处理器
/// </summary>
public class GroupMessageHandler : IMessageHandler<TextMessage>
{
    private readonly IEncryptionService _encryption;
    private readonly IKeyStore _keyStore;
    private readonly ILogger<GroupMessageHandler> _logger;
    private readonly Channel<ChatMessageEvent> _messageChannel;

    public MessageType MessageType => MessageType.GroupText;
    public IAsyncEnumerable<ChatMessageEvent> OnMessageReceived => _messageChannel.Reader.ReadAllAsync();

    public GroupMessageHandler(
        IEncryptionService encryption,
        IKeyStore keyStore,
        ILogger<GroupMessageHandler> logger)
    {
        _encryption = encryption;
        _keyStore = keyStore;
        _logger = logger;
        _messageChannel = Channel.CreateUnbounded<ChatMessageEvent>();
    }

    public async Task HandleAsync(
        TextMessage message,
        ITcpConnection senderConnection,
        MessageEnvelope envelope,
        CancellationToken ct = default)
    {
        var senderId = new NodeId(message.SenderId);
        var groupKey = _keyStore.GetGroupKey(message.ConversationId);

        if (groupKey == null)
        {
            _logger.LogWarning("群聊消息缺少群组密钥: {GroupId}", message.ConversationId);
            return;
        }

        // 群消息 Content 必为 Base64(AES-256-GCM 密文) = Base64([12B nonce][ct][16B tag])。
        // 解密失败 → 丢弃 + 告警日志，不上报 ChatMessageEvent —— 与 PrivateMessageHandler 对称。
        // 见 2026-09-21-group-message-encryption。
        string plainText;
        try
        {
            var ciphertext = Convert.FromBase64String(message.Content);
            var plaintextBytes = _encryption.Decrypt(ciphertext, groupKey);
            plainText = System.Text.Encoding.UTF8.GetString(plaintextBytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "群聊消息解密失败，消息已丢弃: Group={Group}, Sender={Sender}",
                message.ConversationId[..Math.Min(8, message.ConversationId.Length)],
                senderId.ToHexString()[..8]);
            return;
        }

        var chatEvent = new ChatMessageEvent
        {
            Content = plainText,
            SenderId = senderId,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(message.Timestamp).UtcDateTime,
            ConversationId = message.ConversationId,
            IsGroup = true,
            IsOutgoing = false
        };

        await _messageChannel.Writer.WriteAsync(chatEvent, ct);
        _logger.LogDebug("群聊消息已处理: Group={Group} Sender={Sender}",
            message.ConversationId[..Math.Min(8, message.ConversationId.Length)],
            senderId.ToHexString()[..8]);
    }
}
