using System.Buffers.Binary;
using P2PChat.Core.Enums;
using P2PChat.Core.Extensions;
using P2PChat.Core.Models;
using Shouldly;
using Xunit;

namespace P2PChat.Core.Tests;

/// <summary>
/// <see cref="EnvelopeCodec"/> 回归守卫。
/// <para>
/// <b>为什么这些用例必须存在</b>：信封线路编解码历史上被复制过多份
/// （<c>MessageRouter</c> 一份、<c>P2PChatTui.ReadHelloResponseAsync</c> 又抄一份）。
/// UI 那份在阶段 3.2 引入签名后没有同步更新，仍按 50 字节固定头直接切 <c>Payload</c>，
/// 于是 <c>/connect &lt;ip:port&gt;</c> 盲连接的 hello 响应必然解析失败 ——
/// 而 <c>dotnet build</c> 与既有测试都发现不了，因为那条路径<b>零测试覆盖</b>。
/// </para>
/// <para>
/// 现在编解码已收敛到 Core 的 <see cref="EnvelopeCodec"/> 作为唯一真相源，
/// 且它对畸形长度前缀做了完整边界校验。本类把这两点都钉死。
/// </para>
/// </summary>
public class EnvelopeCodecTests
{
    private static NodeId NewNodeId() => NodeId.CreateRandom();

    private static MessageEnvelope NewEnvelope(
        byte[]? publicKey = null,
        byte[]? signature = null,
        byte[]? payload = null,
        MessageType type = MessageType.PrivateText)
    {
        var senderId = NewNodeId();
        return new MessageEnvelope
        {
            Version = 1,
            MessageType = type,
            SequenceNumber = 0xDEADBEEF,
            SenderId = senderId.ToByteArray(),
            MessageId = Guid.NewGuid(),
            Timestamp = 1_700_000_000_000L,
            SenderPublicKey = publicKey,
            Signature = signature,
            Payload = payload ?? [1, 2, 3, 4, 5]
        };
    }

    // ---- 基本往返 --------------------------------------------------------

    [Fact]
    public void 固定头长度是50字节()
    {
        // 1B Version + 1B Type + 4B Seq + 20B SenderId + 16B MessageId + 8B Timestamp
        EnvelopeCodec.FixedHeaderSize.ShouldBe(50);
    }

    [Fact]
    public void 往返_带公钥与签名_所有字段逐项相等()
    {
        var envelope = NewEnvelope(
            publicKey: [9, 8, 7, 6, 5],
            signature: [1, 1, 2, 3, 5, 8],
            payload: "载荷内容"u8.ToArray());

        var round = EnvelopeCodec.Deserialize(EnvelopeCodec.Serialize(envelope));

        round.Version.ShouldBe(envelope.Version);
        round.MessageType.ShouldBe(envelope.MessageType);
        round.SequenceNumber.ShouldBe(envelope.SequenceNumber);
        round.SenderId.ShouldBe(envelope.SenderId);
        round.MessageId.ShouldBe(envelope.MessageId);
        round.Timestamp.ShouldBe(envelope.Timestamp);
        round.SenderPublicKey.ShouldBe(envelope.SenderPublicKey);
        round.Signature.ShouldBe(envelope.Signature);
        round.Payload.ShouldBe(envelope.Payload);
    }

    [Fact]
    public void 往返_无公钥无签名_载荷起点不被固定头吃掉()
    {
        // 这正是 TUI 旧副本的 bug 形态：签名上线后载荷前多了两个长度前缀字段，
        // 任何「按 50 字节切 Payload」的解析器都会把前缀当成载荷开头。
        var payload = "HELLO-E2E-1234"u8.ToArray();
        var envelope = NewEnvelope(payload: payload);

        var bytes = EnvelopeCodec.Serialize(envelope);
        var round = EnvelopeCodec.Deserialize(bytes);

        round.Payload.ShouldBe(payload);
        // 载荷必须精确落在 50 + 4(公钥长度前缀) + 4(签名长度前缀) 处
        bytes.Length.ShouldBe(EnvelopeCodec.FixedHeaderSize + 4 + 4 + payload.Length);
    }

    [Fact]
    public void 往返_空载荷_合法()
    {
        var round = EnvelopeCodec.Deserialize(EnvelopeCodec.Serialize(NewEnvelope(payload: [])));
        round.Payload.ShouldBeEmpty();
    }

    [Fact]
    public void 缺省公钥与签名反序列化为null而非空数组()
    {
        var round = EnvelopeCodec.Deserialize(EnvelopeCodec.Serialize(NewEnvelope()));
        round.SenderPublicKey.ShouldBeNull();
        round.Signature.ShouldBeNull();
    }

    // ---- 签名覆盖面 ------------------------------------------------------

    [Fact]
    public void 待签数据不包含签名字段本身()
    {
        // 签名不可能覆盖自身，否则自指。两侧必须对「同一份不含签名的信封」算出同一段字节。
        var pub = new byte[] { 1, 2, 3 };
        var unsigned = NewEnvelope(publicKey: pub, signature: null, payload: [7, 7, 7]);
        var signed = unsigned with { Signature = [9, 9, 9, 9] };

        EnvelopeCodec.ComputeSignedBytes(unsigned).ShouldBe(EnvelopeCodec.ComputeSignedBytes(signed));
    }

    [Fact]
    public void 待签数据_载荷变化会改变结果()
    {
        var a = NewEnvelope(publicKey: [1], payload: [1, 1]);
        var b = NewEnvelope(publicKey: [1], payload: [1, 2]);
        EnvelopeCodec.ComputeSignedBytes(a).ShouldNotBe(EnvelopeCodec.ComputeSignedBytes(b));
    }

    [Fact]
    public void 待签数据_公钥变化会改变结果()
    {
        // 防止「用 A 的公钥签、用 B 的公钥验」这类混淆通过验签
        var a = NewEnvelope(publicKey: [1, 1], payload: [1]);
        var b = NewEnvelope(publicKey: [2, 2], payload: [1]);
        EnvelopeCodec.ComputeSignedBytes(a).ShouldNotBe(EnvelopeCodec.ComputeSignedBytes(b));
    }

    // ---- 畸形输入：必须失败得明确，而不是靠 Slice 抛 ArgumentOutOfRange ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(49)]
    public void 短于固定头的信封_明确抛InvalidDataException(int length)
    {
        var truncated = new byte[length];
        Should.Throw<InvalidDataException>(() => EnvelopeCodec.Deserialize(truncated));
    }

    [Fact]
    public void 公钥长度前缀被截断_明确抛InvalidDataException()
    {
        // 固定头 50 字节，正好没有长度前缀的位置
        var raw = new byte[EnvelopeCodec.FixedHeaderSize];
        Should.Throw<InvalidDataException>(() => EnvelopeCodec.Deserialize(raw));
    }

    [Fact]
    public void 长度前缀声明超过实际剩余字节_明确抛InvalidDataException()
    {
        // 构造：固定头 + 长度前缀=9999，但实际只剩 0 字节
        var raw = new byte[EnvelopeCodec.FixedHeaderSize + 4];
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(EnvelopeCodec.FixedHeaderSize, 4), 9999);
        Should.Throw<InvalidDataException>(() => EnvelopeCodec.Deserialize(raw));
    }

    [Fact]
    public void 长度前缀超过上限_明确抛InvalidDataException且不巨额分配()
    {
        // 0xFFFFFFFF 若被 (int) 截断会变成 -1；若不校验则会被拿去 Slice。
        // 无论哪种都必须抛 InvalidDataException，且不能尝试分配 4GB。
        var raw = new byte[EnvelopeCodec.FixedHeaderSize + 4];
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(EnvelopeCodec.FixedHeaderSize, 4), uint.MaxValue);
        Should.Throw<InvalidDataException>(() => EnvelopeCodec.Deserialize(raw));
    }

    [Fact]
    public void 签名长度前缀越界_明确抛InvalidDataException()
    {
        // 公钥长度合法为 0，签名长度前缀声明 5000 但没有内容
        var raw = new byte[EnvelopeCodec.FixedHeaderSize + 4 + 4];
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(EnvelopeCodec.FixedHeaderSize, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(EnvelopeCodec.FixedHeaderSize + 4, 4), 5000);
        Should.Throw<InvalidDataException>(() => EnvelopeCodec.Deserialize(raw));
    }
}
