using System.Buffers.Binary;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;

namespace P2PChat.Core.Extensions;

/// <summary>
/// 消息信封的<b>唯一</b>线路编解码实现。
/// <para>
/// 历史上本编解码逻辑被复制过多份：<c>MessageRouter</c> 内嵌一份，
/// <c>P2PChatTui.ReadHelloResponseAsync</c> 又抄了一份。后者在阶段 3.2 引入签名后
/// <b>没有同步更新</b>，仍在按 50 字节固定头直接切 <c>Payload</c>，
/// 于是 <c>/connect &lt;ip:port&gt;</c> 盲连接的 hello 响应必然解析失败
/// （真实载荷前多了 4+N 公钥长度前缀与 4+M 签名前缀）。
/// </para>
/// <para>
/// <c>ChatService.ReadKeyExchangeResponseAsync</c> 当年正是为了「避免两条反序列化路径走偏」
/// 而改为复用 <c>MessageRouter.DeserializeEnvelope</c>，但 TUI 那份副本被漏掉了。
/// 根本修法不是再同步一次，而是把编解码收敛到 Core（本项目依赖图的根），
/// 让 Chat 层与 UI 层都引用同一份实现，从结构上消除再次漂移的可能。
/// </para>
/// <para>
/// 格式（阶段 3.2 签名后）：
/// <code>
/// [1B Version][1B MessageType][4B Seq(BE)][20B SenderId][16B MessageId]
/// [8B Timestamp(BE)][4B PublicKeyLen(BE)][N PublicKey][4B SignatureLen(BE)][M Signature][Payload]
/// </code>
/// </para>
/// <para>
/// <b>AOT 安全</b>：纯 <see cref="BinaryPrimitives"/> 手工读写，无反射、无动态代码。
/// </para>
/// </summary>
public static class EnvelopeCodec
{
    /// <summary>
    /// 固定头长度：Version(1) + MessageType(1) + Seq(4) + SenderId(20) + MessageId(16) + Timestamp(8) = 50。
    /// <para>
    /// 用 <c>static readonly</c> 而非 <c>const</c>：<c>NodeId.Size</c> 是
    /// <c>public static readonly int</c>，不是编译期常量，不能参与 <c>const</c> 表达式。
    /// </para>
    /// </summary>
    public static readonly int FixedHeaderSize = 1 + 1 + 4 + NodeId.Size + 16 + 8;

    /// <summary>长度前缀字段的字节宽度（4 字节大端）</summary>
    private const int LengthPrefixSize = 4;

    /// <summary>
    /// 单个变长字段（公钥 / 签名 / 负载）的长度上限。
    /// TCP 帧本身已有 100MB 上限，这里再兜一层，避免畸形长度前缀导致巨额分配。
    /// </summary>
    public const int MaxFieldLength = 100 * 1024 * 1024;

    /// <summary>序列化为完整信封字节（收发两侧必须用同一份实现）。</summary>
    public static byte[] Serialize(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        using var ms = new MemoryStream();
        WriteFixedHeader(ms, envelope);
        WriteLengthPrefixed(ms, envelope.SenderPublicKey);
        WriteLengthPrefixed(ms, envelope.Signature);
        ms.Write(envelope.Payload);
        return ms.ToArray();
    }

    /// <summary>
    /// 反序列化完整信封。
    /// <para>
    /// <b>失败即抛</b>：长度不足、长度前缀越界、长度前缀声明超过实际剩余字节，一律抛
    /// <see cref="InvalidDataException"/>。宁可整条消息被丢弃，也不能让越界的
    /// <c>Slice</c> 抛出语义不明的 <c>ArgumentOutOfRangeException</c>，
    /// 更不能因为一个畸形长度前缀就分配任意大小的数组。
    /// </para>
    /// </summary>
    public static MessageEnvelope Deserialize(ReadOnlyMemory<byte> rawData)
    {
        var data = rawData.Span;

        if (data.Length < FixedHeaderSize)
            throw new InvalidDataException($"信封过短: {data.Length}B < 固定头 {FixedHeaderSize}B");

        var offset = 0;
        var version = data[offset++];
        var msgType = (MessageType)data[offset++];

        var seq = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        offset += 4;

        var senderId = data.Slice(offset, NodeId.Size).ToArray();
        offset += NodeId.Size;

        var messageId = new Guid(data.Slice(offset, 16));
        offset += 16;

        var ts = BinaryPrimitives.ReadInt64BigEndian(data.Slice(offset, 8));
        offset += 8;

        var pubKey = ReadLengthPrefixed(data, ref offset, "SenderPublicKey");
        var signature = ReadLengthPrefixed(data, ref offset, "Signature");

        // 剩余全部是载荷
        var payload = data[offset..].ToArray();

        return new MessageEnvelope
        {
            Version = version,
            MessageType = msgType,
            SequenceNumber = seq,
            SenderId = senderId,
            MessageId = messageId,
            Timestamp = ts,
            SenderPublicKey = pubKey.Length == 0 ? null : pubKey,
            Signature = signature.Length == 0 ? null : signature,
            Payload = payload
        };
    }

    /// <summary>
    /// 计算「待签数据」：固定头 || SenderPublicKey(4B 大端长度 + N) || Payload。
    /// <para>
    /// 签名与验签两侧必须按<b>完全相同的顺序</b>拼接，否则全部消息验签失败。
    /// 注意：<c>Signature</c> 字段本身不参与签名（否则自指）。
    /// </para>
    /// </summary>
    public static byte[] ComputeSignedBytes(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        using var ms = new MemoryStream();
        WriteFixedHeader(ms, envelope);

        var pubKey = envelope.SenderPublicKey ?? Array.Empty<byte>();
        WriteLengthPrefixed(ms, pubKey);

        ms.Write(envelope.Payload);
        return ms.ToArray();
    }

    private static void WriteFixedHeader(MemoryStream ms, MessageEnvelope envelope)
    {
        ms.WriteByte(envelope.Version);
        ms.WriteByte((byte)envelope.MessageType);

        Span<byte> seqBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(seqBuf, envelope.SequenceNumber);
        ms.Write(seqBuf);

        ms.Write(envelope.SenderId);
        ms.Write(envelope.MessageId.ToByteArray());

        Span<byte> tsBuf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(tsBuf, envelope.Timestamp);
        ms.Write(tsBuf);
    }

    private static void WriteLengthPrefixed(MemoryStream ms, byte[]? data)
    {
        var bytes = data ?? Array.Empty<byte>();
        Span<byte> lenBuf = stackalloc byte[LengthPrefixSize];
        BinaryPrimitives.WriteUInt32BigEndian(lenBuf, (uint)bytes.Length);
        ms.Write(lenBuf);
        if (bytes.Length > 0) ms.Write(bytes);
    }

    /// <summary>
    /// 读取一个 4 字节大端长度前缀字段，并做完整边界校验。
    /// <para>
    /// 三重防护，缺一不可：
    /// (1) 剩余字节必须够读长度前缀本身；
    /// (2) 声明长度不得超过 <see cref="MaxFieldLength"/>；
    /// (3) 声明长度不得超过实际剩余字节。
    /// 全部用 <see cref="long"/> 运算，避免 <c>uint</c>→<c>int</c> 截断在
    /// 长度 &gt; <c>int.MaxValue</c> 时变成负数。
    /// </para>
    /// </summary>
    private static byte[] ReadLengthPrefixed(ReadOnlySpan<byte> data, ref int offset, string fieldName)
    {
        if (data.Length - offset < LengthPrefixSize)
            throw new InvalidDataException($"{fieldName} 长度前缀被截断: 剩余 {data.Length - offset}B");

        var len = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, LengthPrefixSize));
        offset += LengthPrefixSize;

        if (len == 0) return Array.Empty<byte>();

        if (len > MaxFieldLength)
            throw new InvalidDataException($"{fieldName} 长度 {len} 超过上限 {MaxFieldLength}");

        var remaining = (long)data.Length - offset;
        if (len > remaining)
            throw new InvalidDataException($"{fieldName} 长度 {len} 超过实际剩余 {remaining}B");

        var slice = data.Slice(offset, (int)len).ToArray();
        offset += (int)len;
        return slice;
    }
}
