using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;

namespace P2PChat.Chat.Handlers;

/// <summary>
/// 群组通知消息处理器 (加入/离开/解散)
/// </summary>
public class GroupNotifyHandler : IMessageHandler<GroupNotifyMessage>
{
    private readonly ILogger<GroupNotifyHandler> _logger;
    private readonly Channel<GroupNotifyMessage> _notifyChannel;

    public MessageType MessageType => MessageType.GroupNotify;
    public IAsyncEnumerable<GroupNotifyMessage> OnNotifyReceived => _notifyChannel.Reader.ReadAllAsync();

    public GroupNotifyHandler(ILogger<GroupNotifyHandler> logger)
    {
        _logger = logger;
        _notifyChannel = Channel.CreateUnbounded<GroupNotifyMessage>();
    }

    public async Task HandleAsync(
        GroupNotifyMessage message,
        ITcpConnection senderConnection,
        MessageEnvelope envelope,
        CancellationToken ct = default)
    {
        _logger.LogInformation("群组通知: Group={GroupId} Action={Action} Operator={Operator}",
            message.GroupId[..8], message.Action, Convert.ToHexString(message.OperatorId).ToLower()[..8]);

        await _notifyChannel.Writer.WriteAsync(message, ct);
    }
}
