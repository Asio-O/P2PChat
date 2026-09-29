namespace P2PChat.Core.Models;

/// <summary>
/// 密钥交换的**应答**未通过校验 —— 发起方拒绝建立会话。
/// <para>
/// 与 <c>P2PChatTui</c> 内部的 <c>HelloRejectedException</c> 是同一类事件，但**故意不复用**：
/// 那一条属于 <c>/connect &lt;ip:port&gt;</c> 命令的局部失败处理，作用域限于那一个命令；
/// 本类型走的是 <c>ChatService.SendPrivateMessageAsync</c> 的主链路 —— 用户发一条私聊就会走到，
/// 且 <b>抛它就意味着消息没有发出去</b>。两者失败时的用户心智不同（"连不上" vs "消息没发出去"），
/// UI 需要能分开措辞。
/// </para>
/// <para>
/// 语义约定：抛出本异常时，<b>会话密钥一定没有被写入</b>，且明文一定没有上线。
/// 任何在写会话密钥<b>之后</b>才发生的失败都不得用本类型 —— 那会让 UI 误以为可以安全重试。
/// </para>
/// </summary>
public sealed class KeyExchangeRejectedException : Exception
{
    public KeyExchangeRejectedException(string message) : base(message)
    {
    }

    public KeyExchangeRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
