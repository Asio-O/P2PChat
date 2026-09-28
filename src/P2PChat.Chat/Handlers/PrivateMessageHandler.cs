using System.Text;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;

namespace P2PChat.Chat.Handlers;

/// <summary>
/// 私聊消息处理器
/// <para>
/// <b>本类不再持有任何 <c>Channel&lt;ChatMessageEvent&gt;</c></b>，也<b>不再暴露</b>
/// <c>OnMessageReceived</c>。解密出的事件一律经 <see cref="IChatEventPublisher"/> 投递到
/// <c>ChatService</c> 持有的那唯一一条事件流 —— 那才是 UI 真正消费的那条。
/// 见 REPAIR-PLAN B3 与 <see cref="IChatEventPublisher"/> 的「唯一来源铁律」。
/// </para>
/// </summary>
public class PrivateMessageHandler : IMessageHandler<TextMessage>
{
    private readonly IEncryptionService _encryption;
    private readonly IKeyStore _keyStore;
    private readonly IChatEventPublisher _events;
    private readonly ILogger<PrivateMessageHandler> _logger;

    public MessageType MessageType => MessageType.PrivateText;

    public PrivateMessageHandler(
        IEncryptionService encryption,
        IKeyStore keyStore,
        IChatEventPublisher events,
        ILogger<PrivateMessageHandler> logger)
    {
        _encryption = encryption;
        _keyStore = keyStore;
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger;
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

        // 投递到 UI 消费的那条事件流。写入失败不能吞掉——但也不能让一条显示失败
        // 变成一次路由异常：记 Error 后正常返回，消息本身已被正确解密与处理。
        try
        {
            await _events.PublishAsync(chatEvent, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "私聊消息已解密但投递到事件流失败（用户可能看不到这条消息）: {Sender}, MessageId={MessageId}",
                senderId.ToHexString()[..8], message.MessageId);
            return;
        }

        _logger.LogDebug("私聊消息已处理: {Sender} -> {Text}",
            senderId.ToHexString()[..8], plainText[..Math.Min(20, plainText.Length)]);
    }
}
