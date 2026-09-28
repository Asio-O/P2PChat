using MessagePack;

namespace P2PChat.Core.Models;

/// <summary>
/// ECDH(nistP256) 临时密钥交换消息 —— 用于首次通信前协商会话密钥
/// </summary>
[MessagePackObject]
public record KeyExchangeMessage : Message
{
    /// <summary>
    /// 发送方临时公钥：ECDH nistP256 的 SubjectPublicKeyInfo (DER)。
    /// 注意不是 X25519 —— 32 字节固定长度的写法在此不适用。
    /// </summary>
    [Key(10)]
    public required byte[] EphemeralPublicKey { get; init; }

    /// <summary>是否为响应 (true=响应, false=请求)</summary>
    [Key(11)]
    public bool IsResponse { get; init; }

    /// <summary>
    /// 发送方自报的<b>监听</b> TCP 端点，格式 <c>"ip:port"</c>（见 <c>Core.Extensions.EndpointText</c>）。
    /// 只在 <see cref="IsResponse"/> 为 false（hello 请求）时有意义。
    /// <para>
    /// <b>为什么需要这个字段</b>：<c>/connect &lt;ip:port&gt;</c> 要做到<b>双向</b>，
    /// 应答侧（B）就必须知道发起方（A）的监听端点，否则「B 知道对方是谁、却不知道对方在哪」，
    /// 无法回话。而这个信息 <b>B 侧无法从 TCP 连接推断</b> —— 三次握手不携带对端的监听端口，
    /// 出站连接的本地端口是内核分配的 <b>ephemeral 临时端口</b>。
    /// 因此监听端口只能<b>由 A 自己说</b>。
    /// </para>
    /// <para>
    /// <b>可归因性</b>：本字段位于 Payload 内，而 Payload 被
    /// <c>EnvelopeCodec.ComputeSignedBytes</c> <b>整体</b>纳入 ECDSA 签名覆盖，
    /// 因此「A 声称自己监听 P」是可归因的。A 若谎报，B 只是连不上，<b>不构成安全性问题</b>
    /// （最坏是 A 自己 DoS 自己）。
    /// </para>
    /// <para>
    /// <b>适用边界（重要，别当成对所有场景都正确）</b>：发送方填的是
    /// <c>IDhtService.LocalNode.EndPoint</c>，即本机 <b>局域网 IP + TCP 监听端口</b>。
    /// 它对<b>同一局域网内</b>的对端是正确的；对<b>跨 NAT 的对端不可达</b>（私网 IP 出了 NAT 就没有意义）。
    /// 跨 NAT 场景本来靠 DHT 解析拿到公网端点，不走 <c>/connect</c> 的反向登记，因此不受影响。
    /// </para>
    /// <para>
    /// <b>向后兼容</b>：本类型是 <c>[MessagePackObject]</c> + <c>[Key(n)]</c>（key-as-array-index，
    /// 插入新键不移位）。老节点不带本字段时为 null，应答侧退化为
    /// 「只登记身份与公钥、不写静态对端表」。
    /// </para>
    /// <para>
    /// 刻意用 <c>init</c> 且<b>不带初始化器</b>：与 <see cref="Message"/> 里 <c>MessageId</c> /
    /// <c>Timestamp</c> 那条注释同源 —— <c>init</c> + 初始化器在反序列化时会被 MsgPack
    /// 无条件重置为 default；不带初始化器时，<b>键缺失即不赋值、保持 null</b>，
    /// 正是这里需要的降级语义。
    /// </para>
    /// </summary>
    [Key(12)]
    public string? SenderListenEndPoint { get; init; }
}
