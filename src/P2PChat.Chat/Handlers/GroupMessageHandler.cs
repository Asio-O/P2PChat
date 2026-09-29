using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;

namespace P2PChat.Chat.Handlers;

/// <summary>
/// 群聊消息处理器
/// <para>
/// <b>本类不再持有任何 <c>Channel&lt;ChatMessageEvent&gt;</c></b>，也<b>不再暴露</b>
/// <c>OnMessageReceived</c>。解密出的事件一律经 <see cref="IChatEventPublisher"/> 投递到
/// <c>ChatService</c> 持有的那唯一一条事件流 —— 那才是 UI 真正消费的那条。
/// 见 <see cref="IChatEventPublisher"/> 的「唯一来源铁律」与 <c>Agent.md</c> §2.3 / §8.1 第 14 条；
/// 该事故的由来见归档快照 <c>.agents/notes/archived/process/2026-09-20-p2pchat-repair-plan.md</c>。
/// </para>
/// </summary>
public class GroupMessageHandler : IMessageHandler<TextMessage>
{
    private readonly IEncryptionService _encryption;
    private readonly IKeyStore _keyStore;
    private readonly IChatEventPublisher _events;
    private readonly ILogger<GroupMessageHandler> _logger;

    public MessageType MessageType => MessageType.GroupText;

    public GroupMessageHandler(
        IEncryptionService encryption,
        IKeyStore keyStore,
        IChatEventPublisher events,
        ILogger<GroupMessageHandler> logger)
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

        // 投递到 UI 消费的那条事件流（与私聊对称，见 PrivateMessageHandler）。
        try
        {
            await _events.PublishAsync(chatEvent, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "群聊消息已解密但投递到事件流失败（用户可能看不到这条消息）: Group={Group}, Sender={Sender}",
                message.ConversationId[..Math.Min(8, message.ConversationId.Length)],
                senderId.ToHexString()[..8]);
            return;
        }

        _logger.LogDebug("群聊消息已处理: Group={Group} Sender={Sender}",
            message.ConversationId[..Math.Min(8, message.ConversationId.Length)],
            senderId.ToHexString()[..8]);
    }
}
