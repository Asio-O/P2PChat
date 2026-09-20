namespace P2PChat.Core.Models;

/// <summary>
/// 会话标识 —— 私聊会话键必须**方向无关**：A 与 B 两侧必须算出同一个字符串。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：历史上私聊把 <c>ConversationId</c> 设为**收件人的** NodeId，
/// 于是同一条消息在发送方与接收方得到两个不同的键——
/// 发送方记在「对端 ID」下，接收方收到的却是**自己的** NodeId。
/// 而 UI 的会话桶始终以「对端 ID」为键，导致接收方收到的消息被投进一个永远选不中的桶，
/// 表现为「我发了，对方说没收到」。
/// </para>
/// <para>
/// 因此私聊会话键取两个 NodeId 的十六进制串按序拼接，两侧结果必然相同。
/// 群聊的会话键是 GroupId，本身就是方向无关的，不走这里。
/// </para>
/// </remarks>
public static class ConversationId
{
    /// <summary>
    /// 计算 A 与 B 之间私聊会话的方向无关键。
    /// </summary>
    public static string ForPrivate(NodeId a, NodeId b)
    {
        var x = a.ToHexString();
        var y = b.ToHexString();

        // 两个十六进制串等长，序数比较即数值比较。
        return string.CompareOrdinal(x, y) <= 0 ? x + y : y + x;
    }
}
