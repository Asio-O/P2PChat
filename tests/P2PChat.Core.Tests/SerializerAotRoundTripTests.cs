using System.Buffers;
using P2PChat.Core.Enums;
using P2PChat.Core.Extensions;
using P2PChat.Core.Models;
using P2PChat.Networking.Dht;
using Shouldly;
using MsgPack = MessagePack;

namespace P2PChat.Core.Tests;

/// <summary>
/// AOT 硬化后的序列化往返测试。
/// 覆盖：8 个 Union 子类多态往返、空/大 payload、文件分块、DHT DTO（Networking 程序集）、
/// 与旧反射解析器的线格式兼容性，以及 MsgPack017 修复（缺失键时保留初始化器默认值）。
/// </summary>
public class SerializerAotRoundTripTests
{
    private readonly MessagePackSerializer _serializer = new();

    /// <summary>
    /// 旧的（依赖 Reflection.Emit 的）解析器组合 — 仅用于线格式兼容性对照。
    /// AOT 产物已不再使用 StandardResolver / ContractlessStandardResolver。
    /// </summary>
    private static readonly MsgPack.MessagePackSerializerOptions LegacyOptions =
        MsgPack.MessagePackSerializerOptions.Standard.WithResolver(
            MsgPack.Resolvers.CompositeResolver.Create(
                MsgPack.Resolvers.StandardResolver.Instance,
                MsgPack.Resolvers.ContractlessStandardResolver.Instance));

    private static byte[] NewSenderId()
    {
        var id = new byte[20];
        for (var i = 0; i < id.Length; i++) id[i] = (byte)(i + 1);
        return id;
    }

    private static byte[] RandomBytes(int length)
    {
        var data = new byte[length];
        Random.Shared.NextBytes(data);
        return data;
    }

    private static byte[] RawValue<T>(T value) =>
        MsgPack.MessagePackSerializer.Serialize(value, LegacyOptions);

    [Fact]
    public void Union_AllEightSubclasses_PolymorphicRoundTrip_ThroughMessageBase()
    {
        var senderId = NewSenderId();
        var messages = new Message[]
        {
            new TextMessage
            {
                SenderId = senderId, ConversationId = "conv-text",
                Content = "你好, P2PChat", IsGroup = true
            },
            new FileMetaMessage
            {
                SenderId = senderId, ConversationId = "conv-file",
                TransferId = "t-1", FileName = "archive.bin", FileSize = 123456789L,
                FileHash = RandomBytes(32), ChunkSize = 65536, TotalChunks = 42
            },
            new FileChunkMessage
            {
                SenderId = senderId, ConversationId = "conv-file",
                TransferId = "t-1", ChunkIndex = 7, Data = new byte[] { 1, 2, 3, 4 }
            },
            new FileAckMessage
            {
                SenderId = senderId, ConversationId = "conv-file",
                TransferId = "t-1", Accepted = true, ErrorMessage = null
            },
            new KeyExchangeMessage
            {
                SenderId = senderId, ConversationId = "conv-kex",
                EphemeralPublicKey = RandomBytes(32), IsResponse = false
            },
            new GroupInviteMessage
            {
                SenderId = senderId, ConversationId = "grp-1",
                GroupId = "g-1", GroupName = "研发群",
                EncryptedGroupKey = RandomBytes(48),
                MemberIds = new List<byte[]> { senderId, NewSenderId() }
            },
            new GroupNotifyMessage
            {
                SenderId = senderId, ConversationId = "grp-1",
                GroupId = "g-1", Action = "join", OperatorId = senderId
            },
            new DeliveryAckMessage
            {
                SenderId = senderId, ConversationId = "conv-text",
                AcknowledgedMessageId = Guid.NewGuid(), Status = "delivered"
            }
        };

        messages.Length.ShouldBe(8);

        foreach (var original in messages)
        {
            // 以抽象基类 Message 序列化 → 走 [Union] 多态路径
            var bytes = _serializer.Serialize<Message>(original);
            var back = _serializer.Deserialize<Message>(bytes);

            back.GetType().ShouldBe(original.GetType());
            back.MessageId.ShouldBe(original.MessageId);
            back.SenderId.ShouldBe(original.SenderId);
            back.Timestamp.ShouldBe(original.Timestamp);
            back.ConversationId.ShouldBe(original.ConversationId);

            switch (original)
            {
                case TextMessage m:
                {
                    var b = back.ShouldBeOfType<TextMessage>();
                    b.Content.ShouldBe(m.Content);
                    b.IsGroup.ShouldBe(m.IsGroup);
                    break;
                }
                case FileMetaMessage m:
                {
                    var b = back.ShouldBeOfType<FileMetaMessage>();
                    b.TransferId.ShouldBe(m.TransferId);
                    b.FileName.ShouldBe(m.FileName);
                    b.FileSize.ShouldBe(m.FileSize);
                    b.FileHash.ShouldBe(m.FileHash);
                    b.ChunkSize.ShouldBe(m.ChunkSize);
                    b.TotalChunks.ShouldBe(m.TotalChunks);
                    break;
                }
                case FileChunkMessage m:
                {
                    var b = back.ShouldBeOfType<FileChunkMessage>();
                    b.TransferId.ShouldBe(m.TransferId);
                    b.ChunkIndex.ShouldBe(m.ChunkIndex);
                    b.Data.ShouldBe(m.Data);
                    break;
                }
                case FileAckMessage m:
                {
                    var b = back.ShouldBeOfType<FileAckMessage>();
                    b.Accepted.ShouldBe(m.Accepted);
                    b.ErrorMessage.ShouldBeNull();
                    break;
                }
                case KeyExchangeMessage m:
                {
                    var b = back.ShouldBeOfType<KeyExchangeMessage>();
                    b.EphemeralPublicKey.ShouldBe(m.EphemeralPublicKey);
                    b.IsResponse.ShouldBe(m.IsResponse);
                    break;
                }
                case GroupInviteMessage m:
                {
                    var b = back.ShouldBeOfType<GroupInviteMessage>();
                    b.GroupId.ShouldBe(m.GroupId);
                    b.GroupName.ShouldBe(m.GroupName);
                    b.EncryptedGroupKey.ShouldBe(m.EncryptedGroupKey);
                    b.MemberIds.Count.ShouldBe(m.MemberIds.Count);
                    b.MemberIds[0].ShouldBe(m.MemberIds[0]);
                    b.MemberIds[1].ShouldBe(m.MemberIds[1]);
                    break;
                }
                case GroupNotifyMessage m:
                {
                    var b = back.ShouldBeOfType<GroupNotifyMessage>();
                    b.GroupId.ShouldBe(m.GroupId);
                    b.Action.ShouldBe(m.Action);
                    b.OperatorId.ShouldBe(m.OperatorId);
                    break;
                }
                case DeliveryAckMessage m:
                {
                    var b = back.ShouldBeOfType<DeliveryAckMessage>();
                    b.AcknowledgedMessageId.ShouldBe(m.AcknowledgedMessageId);
                    b.Status.ShouldBe(m.Status);
                    break;
                }
                default:
                    throw new ShouldAssertException($"未覆盖的 Union 子类: {original.GetType().FullName}");
            }
        }
    }

    [Fact]
    public void EmptyPayload_RoundTrip()
    {
        var msg = new TextMessage
        {
            SenderId = Array.Empty<byte>(),
            ConversationId = string.Empty,
            Content = string.Empty,
            IsGroup = false
        };

        var back = _serializer.Deserialize<Message>(_serializer.Serialize<Message>(msg))
            .ShouldBeOfType<TextMessage>();

        back.SenderId.ShouldBeEmpty();
        back.ConversationId.ShouldBeEmpty();
        back.Content.ShouldBeEmpty();
        back.MessageId.ShouldBe(msg.MessageId);
    }

    [Fact]
    public void LargePayload_1MiB_RoundTrip()
    {
        var data = RandomBytes(1024 * 1024);
        var msg = new FileChunkMessage
        {
            SenderId = NewSenderId(),
            ConversationId = "conv-big",
            TransferId = "big-1",
            ChunkIndex = 0,
            Data = data
        };

        var back = _serializer.Deserialize<Message>(_serializer.Serialize<Message>(msg))
            .ShouldBeOfType<FileChunkMessage>();

        back.Data.Length.ShouldBe(data.Length);
        back.Data.ShouldBe(data);
    }

    [Fact]
    public void FileChunks_MultiChunkSequence_RoundTripAndReassemble()
    {
        var payload = RandomBytes(300_000);
        const int chunkSize = 65536;
        var totalChunks = (payload.Length + chunkSize - 1) / chunkSize;

        var reassembled = new MemoryStream();
        for (var i = 0; i < totalChunks; i++)
        {
            var slice = payload.AsSpan(i * chunkSize, Math.Min(chunkSize, payload.Length - i * chunkSize)).ToArray();
            var chunk = new FileChunkMessage
            {
                SenderId = NewSenderId(),
                ConversationId = "conv-file",
                TransferId = "multi-1",
                ChunkIndex = i,
                Data = slice
            };

            var back = _serializer.Deserialize<Message>(_serializer.Serialize<Message>(chunk))
                .ShouldBeOfType<FileChunkMessage>();

            back.ChunkIndex.ShouldBe(i);
            back.TransferId.ShouldBe("multi-1");
            back.Data.ShouldBe(slice);
            reassembled.Write(back.Data);
        }

        reassembled.ToArray().ShouldBe(payload);
    }

    [Fact]
    public void FileMetaMessage_WhenTrailingKeysMissing_KeepsInitializerDefaults()
    {
        // MsgPack017 修复验证：模拟"旧版本数据"只写到 [Key(13)]，缺失 [Key(14)] ChunkSize 与 [Key(15)] TotalChunks。
        // 若使用 init 访问器，生成 formatter 会把这两者重置为 0；改为 set 后应保留初始化器默认值。
        var raw = new ArrayBufferWriter<byte>();
        var writer = new MsgPack.MessagePackWriter(raw);
        writer.WriteArrayHeader(14);
        writer.WriteRaw(RawValue(Guid.NewGuid()));          // 0  MessageId
        writer.WriteRaw(RawValue(NewSenderId()));            // 1  SenderId
        writer.WriteRaw(RawValue(1700000000000L));           // 2  Timestamp
        writer.WriteRaw(RawValue("conv-file"));              // 3  ConversationId
        for (var i = 4; i <= 9; i++) writer.WriteNil();      // 4-9 未使用索引
        writer.WriteRaw(RawValue("t-1"));                    // 10 TransferId
        writer.WriteRaw(RawValue("archive.bin"));            // 11 FileName
        writer.WriteRaw(RawValue(4096L));                    // 12 FileSize
        writer.WriteRaw(RawValue(RandomBytes(32)));          // 13 FileHash
        writer.Flush();

        var back = _serializer.Deserialize<FileMetaMessage>(raw.WrittenSpan.ToArray());

        back.FileName.ShouldBe("archive.bin");
        back.ChunkSize.ShouldBe(65536);
        back.TotalChunks.ShouldBe(0);
    }

    [Fact]
    public void DhtDtos_ListAndStore_RoundTrip_ViaCoreSerializer()
    {
        // NodeInfoDto / StoreRequest 定义在 P2PChat.Networking（Core 不可引用），
        // 它们依赖 SourceGeneratedFormatterResolver 发现下游程序集编译期生成的 formatter。
        var nodes = new List<NodeInfoDto>
        {
            new() { NodeId = NewSenderId(), IpAddress = "router.bittorrent.com", Port = 6881, PublicKey = RandomBytes(32) },
            new() { NodeId = NewSenderId(), IpAddress = "127.0.0.1", Port = 0, PublicKey = Array.Empty<byte>() }
        };

        var nodeBytes = _serializer.Serialize(nodes);
        var nodesBack = _serializer.Deserialize<List<NodeInfoDto>>(nodeBytes);

        nodesBack.Count.ShouldBe(2);
        nodesBack[0].IpAddress.ShouldBe("router.bittorrent.com");
        nodesBack[0].Port.ShouldBe(6881);
        nodesBack[0].NodeId.ShouldBe(nodes[0].NodeId);
        nodesBack[0].PublicKey.ShouldBe(nodes[0].PublicKey);
        nodesBack[1].PublicKey.ShouldBeEmpty();

        var store = new StoreRequest { Key = NewSenderId(), Value = RandomBytes(1024) };
        var storeBack = _serializer.Deserialize<StoreRequest>(_serializer.Serialize(store));

        storeBack.Key.ShouldBe(store.Key);
        storeBack.Value.ShouldBe(store.Value);
    }

    [Fact]
    public void DhtPayload_ByteArray_RoundTrip()
    {
        // KademliaDhtService 直接序列化 byte[]（节点ID / 键）作为 RPC payload。
        var payload = NewSenderId();
        var back = _serializer.Deserialize<byte[]>(_serializer.Serialize(payload));
        back.ShouldBe(payload);

        var empty = Array.Empty<byte>();
        _serializer.Deserialize<byte[]>(_serializer.Serialize(empty)).ShouldBeEmpty();
    }

    [Fact]
    public void DhtRpcMessage_BinaryFormat_RoundTrip()
    {
        // DhtRpcMessage 使用手写二进制格式（不经过 MessagePack），一并回归验证。
        var payload = RandomBytes(4096);
        var original = new DhtRpcMessage
        {
            MessageType = DhtMessageType.FindValue,
            RequestId = 0xDEADBEEF,
            SenderId = NewSenderId(),
            Payload = payload
        };

        var back = DhtRpcMessage.FromBytes(original.ToBytes());

        back.Version.ShouldBe(original.Version);
        back.MessageType.ShouldBe(DhtMessageType.FindValue);
        back.RequestId.ShouldBe(0xDEADBEEF);
        back.SenderId.ShouldBe(original.SenderId);
        back.Payload.ShouldBe(payload);
    }

    [Fact]
    public void LegacyDynamicResolver_And_AotSerializer_AreWireCompatible()
    {
        var original = new TextMessage
        {
            SenderId = NewSenderId(),
            ConversationId = "conv-compat",
            Content = "wire-compat",
            IsGroup = true
        };

        // 旧解析器写 → 新（AOT）解析器读
        var legacyBytes = MsgPack.MessagePackSerializer.Serialize<Message>(original, LegacyOptions);
        var readByNew = _serializer.Deserialize<Message>(legacyBytes).ShouldBeOfType<TextMessage>();
        readByNew.Content.ShouldBe("wire-compat");
        readByNew.MessageId.ShouldBe(original.MessageId);
        readByNew.IsGroup.ShouldBeTrue();

        // 新（AOT）解析器写 → 旧解析器读
        var newBytes = _serializer.Serialize<Message>(original);
        var readByLegacy = MsgPack.MessagePackSerializer.Deserialize<Message>(newBytes, LegacyOptions)
            .ShouldBeOfType<TextMessage>();
        readByLegacy.Content.ShouldBe("wire-compat");
        readByLegacy.MessageId.ShouldBe(original.MessageId);

        // 索引键数组格式，两条路径应产生完全相同的字节
        newBytes.ShouldBe(legacyBytes);
    }
}
