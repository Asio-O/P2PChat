using System.Net;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Models;

namespace P2PChat.Core.Extensions;

/// <summary>
/// 信封验签的<b>唯一</b>实现（与 <see cref="EnvelopeCodec"/> 同构：收敛到 Core 依赖图的根）。
/// <para>
/// 背景：本项目里「信封怎么解析」和「信封怎么验签」各自被复制过多份。
/// 编解码那份已在阶段 3.2 被收敛到 <see cref="EnvelopeCodec"/>；
/// <b>验签那份当时漏了 <c>P2PChatTui.ReadHelloResponseAsync</c></b> —— 它只
/// <c>Deserialize</c> 就直接取载荷使用，导致 <c>/connect &lt;ip:port&gt;</c>
/// 对「任何抢在真节点前应答的主机」无条件信任，并把攻击者登记为静态对端。
/// UI 层不引用 Chat 层，引用不到 <c>MessageRouter.VerifyEnvelope</c>，
/// 正确解法是像编解码一样把验签收敛到 Core，而不是在 UI 里再抄一份。
/// </para>
/// <para>
/// <b>验签证明什么、不证明什么</b>（务必分清，否则会写出错误的用户提示）：
/// <list type="bullet">
///   <item>✅ <b>证明</b>：这条信封由持有该公钥对应私钥的一方发出，且未被篡改；
///         且 <c>NodeId.FromPublicKey(公钥) == SenderId</c>，即公钥与身份强绑定。</item>
///   <item>❌ <b>不证明</b>：对方是「我想连接的那个节点」。首次接触未知端点时，
///         攻击者用自己的私钥签出来的信封在密码学上<b>完全有效</b>，
///         没有任何字段能把合法节点与攻击者区分开。</item>
/// </list>
/// 因此「首次接触」是 TOFU（首次使用即信任），只能靠
/// <see cref="EvaluatePeerIdentity"/> 做**端点→身份绑定**的连续性检查（同一端点前后身份
/// 不一致即拒绝），而不能宣称「验签通过 = 身份可信」。
/// </para>
/// <para>
/// <b>AOT 安全</b>：纯静态方法 + <see cref="EnvelopeCodec"/>，无反射、无动态代码。
/// </para>
/// </summary>
public static class EnvelopeVerifier
{
    /// <summary>
    /// 验签并返回是否通过；若失败给出<b>中文明因</b>。
    /// <para>
    /// <b>本函数证明：</b>这条信封由持有 <see cref="MessageEnvelope.SenderPublicKey"/>
    /// 对应私钥的一方签发，且其声明的身份与该公钥自洽（<c>NodeId.FromPublicKey(公钥) == SenderId</c>），
    /// 内容未被篡改。
    /// </para>
    /// <para>
    /// <b>本函数<b>不</b>证明：</b>
    /// <list type="bullet">
    ///   <item><b>不</b>证明「对端是谁」/「对端是否可信」—— 首次接触未知端点时，
    ///         攻击者用自己的私钥签出来的信封在这里同样通过（TOFU，信息论上无法区分）；</item>
    ///   <item><b>不</b>证明「这条消息不是重放」—— 那是 <c>IReplayGuard</c> 的职责。</item>
    /// </list>
    /// 调用方**不得**把「验签通过」表述成「已验证对端身份」或「对端可信」——
    /// 那是本会话反复出现的那类误导（本项目已有多处「注释/提示声称了代码未做的事」）。
    /// 需要「这个端点前后身份是否一致」请用 <see cref="EvaluatePeerIdentity"/>，需要新鲜度请用重放防护。
    /// </para>
    /// <para>
    /// 三道检查，顺序固定（顺序本身就是策略：先排除「什么都不带」的包）：
    /// <list type="number">
    ///   <item>必须携带非空 <c>Signature</c>；</item>
    ///   <item>必须携带非空 <c>SenderPublicKey</c>；</item>
    ///   <item><c>NodeId.FromPublicKey(SenderPublicKey)</c> 必须等于 <c>SenderId</c>（防冒名）；</item>
    ///   <item>对 <see cref="EnvelopeCodec.ComputeSignedBytes"/> 的结果做 ECDSA 验签。</item>
    /// </list>
    /// </para>
    /// <para><b>无状态、可重复调用</b>：对同一条信封调用多次结果一致。重放判定不在这里。</para>
    /// </summary>
    /// <param name="envelope">待验签信封（通常来自 <see cref="EnvelopeCodec.Deserialize"/>）。</param>
    /// <param name="encryption">提供 ECDSA 验签。</param>
    /// <param name="failureReason">失败原因；通过时为 null。</param>
    public static bool Verify(
        MessageEnvelope envelope,
        IEncryptionService encryption,
        out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(encryption);

        if (envelope.Signature == null || envelope.Signature.Length == 0)
        {
            failureReason = "缺少签名";
            return false;
        }
        if (envelope.SenderPublicKey == null || envelope.SenderPublicKey.Length == 0)
        {
            failureReason = "缺少发送方公钥";
            return false;
        }

        // 校验 SenderId 必须等于公钥派生的 NodeId —— 阻止 SenderId 冒名
        try
        {
            var derivedNodeId = NodeId.FromPublicKey(envelope.SenderPublicKey).ToByteArray();
            if (!derivedNodeId.AsSpan().SequenceEqual(envelope.SenderId))
            {
                failureReason = "SenderId 与 SenderPublicKey 不匹配";
                return false;
            }
        }
        catch (Exception ex)
        {
            failureReason = "SenderPublicKey 解析失败: " + ex.Message;
            return false;
        }

        var data = EnvelopeCodec.ComputeSignedBytes(envelope);
        if (!encryption.Verify(data, envelope.Signature, envelope.SenderPublicKey))
        {
            failureReason = "ECDSA 验签失败";
            return false;
        }

        failureReason = null;
        return true;
    }

    /// <summary>端点→身份绑定的连续性判定结果。</summary>
    public enum PeerIdentityVerdict
    {
        /// <summary>该端点此前没有已登记身份 —— 首次接触，属 TOFU，只能信任并告知用户。</summary>
        FirstContact,

        /// <summary>该端点此前登记的身份与本次应答一致 —— 连续性成立。</summary>
        MatchesExistingPin,

        /// <summary>该端点此前登记的是<b>另一个</b>身份 —— 中间人或对端换身份的强信号，必须拒绝。</summary>
        ConflictsWithExistingPin
    }

    /// <summary>
    /// 判断「本端点上一次见到的身份」与「这一次应答的身份」是否一致。
    /// <para>
    /// 这是 <c>/connect &lt;ip:port&gt;</c> 唯一真正能拒绝「攻击者用自己的合法私钥应答」的手段 ——
    /// 因为在首次接触时，攻击者的信封与合法节点的信封在密码学上不可区分，
    /// 唯一可用的额外信息就是<b>我们自己此前为这个端点记下的身份</b>。
    /// 换句话说：没有历史就没有判据，此时只能 TOFU 并如实告知用户。
    /// </para>
    /// <para>
    /// 纯函数、无副作用，便于单测；<b>不</b>依赖任何存储实现，调用方负责提供已有的绑定。
    /// </para>
    /// </summary>
    /// <param name="pinnedIdentities">本端已知的「端点 → 节点ID」绑定（可为空集合）。</param>
    /// <param name="observedEndPoint">本次应答来自的端点。</param>
    /// <param name="observedNodeId">本次应答自称的节点ID（已由验签确保与公钥自洽）。</param>
    public static PeerIdentityVerdict EvaluatePeerIdentity(
        IEnumerable<(IPEndPoint EndPoint, NodeId NodeId)> pinnedIdentities,
        IPEndPoint observedEndPoint,
        NodeId observedNodeId)
    {
        ArgumentNullException.ThrowIfNull(pinnedIdentities);
        ArgumentNullException.ThrowIfNull(observedEndPoint);

        // 扫描全部绑定再做判定，**不得**在首个匹配处 early-return：
        // 同一端点既有匹配又有不匹配，说明本地绑定数据已损坏，此时「有一条能对上」
        // 不足以放行。全量扫完、任一冲突即判冲突（宁可拒绝），冲突优先于匹配。
        var matched = false;
        var conflict = false;

        foreach (var (endPoint, nodeId) in pinnedIdentities)
        {
            if (endPoint is null || !endPoint.Equals(observedEndPoint))
                continue;

            if (nodeId.Equals(observedNodeId))
            {
                matched = true;
                continue;
            }

            conflict = true;
        }

        if (conflict) return PeerIdentityVerdict.ConflictsWithExistingPin;
        return matched ? PeerIdentityVerdict.MatchesExistingPin : PeerIdentityVerdict.FirstContact;
    }
}
