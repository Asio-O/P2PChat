using P2PChat.Core.Models;

namespace P2PChat.Core.Abstractions;

/// <summary>
/// 重放防护 — 判断一条**已验签**的信封是不是被重放的旧包。
/// <para>
/// 为什么需要它（见 2026-09-21-message-signing 的遗留缺口）：ECDSA 签名只能证明
/// 「这条消息由持有对应私钥的一方发出」，不能证明「这是一条**新**消息」。攻击者录下一条
/// 合法信封反复重放，仍能通过验签的全部关卡（缺签名检查 / 公钥-SenderId 绑定 / ECDSA 验签）。
/// <c>MessageEnvelope.SequenceNumber</c> 虽被签名覆盖，但由进程内 <c>Interlocked.Increment</c>
/// 生成、**进程重启即归零**，因此不能用作跨重启的单调水位；<c>Timestamp</c> 虽被签名覆盖，
/// 却从未被校验新鲜度。真正可作重放键的是已被签名覆盖的 <see cref="MessageEnvelope.MessageId"/>
/// （16 字节 Guid，天然唯一）。
/// </para>
/// <para>
/// 契约刻意与密码学验证分离：<c>MessageRouter.VerifyEnvelope</c> 是**纯密码学**的、无状态、
/// 可重复调用的函数，被 <c>MessageSigningTests</c> 大量直接调用；把「有状态的策略」塞进去会
/// 污染它的语义（同一信封校验两次第二次就会失败），并连带炸红一批语义正确的测试。
/// </para>
/// </summary>
public interface IReplayGuard
{
    /// <summary>
    /// 判断信封是否应被接受。<b>实现必须是线程安全的，且对同一个信封的判定必须是原子的</b> ——
    /// 两条完全相同的信封并发到达时，有且只有一条能返回 true，否则并发重放就能绕过。
    /// </summary>
    /// <param name="envelope">已完成密码学校验的信封。</param>
    /// <param name="reason">被拒绝时的明因（如「过旧」「来自未来」「重复」）；接受时为 null。</param>
    /// <returns>true 表示接受（并应记住该消息）；false 表示判定为重放并拒绝。</returns>
    bool TryAccept(MessageEnvelope envelope, out string? reason);
}
