using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;

namespace P2PChat.Chat.Handlers;

/// <summary>
/// 私聊消息处理器
/// </summary>
public class PrivateMessageHandler : IMessageHandler<TextMessage>
{
    private readonly IEncryptionService _encryption;
    private readonly IKeyStore _keyStore;
    private readonly ILogger<PrivateMessageHandler> _logger;
    private readonly Channel<ChatMessageEvent> _messageChannel;

    public MessageType MessageType => MessageType.PrivateText;
    public IAsyncEnumerable<ChatMessageEvent> OnMessageReceived => _messageChannel.Reader.ReadAllAsync();

    public PrivateMessageHandler(
        IEncryptionService encryption,
        IKeyStore keyStore,
        ILogger<PrivateMessageHandler> logger)
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
        var sessionKey = _keyStore.GetSessionKey(senderId);

        if (sessionKey == null)
        {
            _logger.LogWarning("私聊消息缺少会话密钥: {Sender}", senderId.ToHexString()[..8]);
            return;
        }

        if (sessionKey.Length != 32)
        {
            _logger.LogWarning("私聊消息会话密钥长度无效: {Sender}, Length={Length}",
                senderId.ToHexString()[..8], sessionKey.Length);
            return;
        }

        // 私聊线路契约与 ChatService 对称：TextMessage.Content 是 AES-256-GCM
        // 密文的 Base64 编码；只有解密并按 UTF-8 还原后才发布 ChatMessageEvent。
        string plainText;
        try
        {
            var ciphertext = Convert.FromBase64String(message.Content);
            var plaintext = _encryption.Decrypt(ciphertext, sessionKey);
            plainText = Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "私聊消息解密失败，消息已丢弃: {Sender}, MessageId={MessageId}",
                senderId.ToHexString()[..8], message.MessageId);
            return;
        }

        var chatEvent = new ChatMessageEvent
        {
            Content = plainText,
            SenderId = senderId,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(message.Timestamp).UtcDateTime,
            ConversationId = message.ConversationId,
            IsGroup = false,
            IsOutgoing = false
        };

        await _messageChannel.Writer.WriteAsync(chatEvent, ct);
        _logger.LogDebug("私聊消息已处理: {Sender} -> {Text}",
            senderId.ToHexString()[..8], plainText[..Math.Min(20, plainText.Length)]);
    }
}
