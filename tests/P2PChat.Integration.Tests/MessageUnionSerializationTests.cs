using P2PChat.Core.Extensions;
using P2PChat.Core.Models;
using Shouldly;

namespace P2PChat.Integration.Tests;

/// <summary>
/// 多态消息 Union 编解码往返 — Message 基类带 8 个 [Union] 子类，
/// 必须保证"以 Message 静态类型序列化 → 以 Message 反序列化"能还原出正确的运行时子类型与全部字段。
/// 这正是 T3 (source-generated resolver / 锚定类型) 的整合点。
/// </summary>
public class MessageUnionSerializationTests
{
    private static readonly P2PChat.Core.Extensions.MessagePackSerializer Serializer = new();

    private static byte[] Sender() => Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray();

    public static TheoryData<Message> AllUnionCases() => new()
    {
        new TextMessage
        {
            MessageId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            SenderId = Sender(), ConversationId = "conv-private", Timestamp = 1700000000000,
            Content = "你好，世界 Hello World", IsGroup = false
        },
        new FileMetaMessage
        {
            SenderId = Sender(), ConversationId = "conv-file",
            TransferId = "transfer-1", FileName = "报告 2026.pdf", FileSize = 123456789,
            FileHash = Enumerable.Repeat((byte)0xAB, 32).ToArray(), ChunkSize = 65536, TotalChunks = 1884
        },
        new FileChunkMessage
        {
            SenderId = Sender(), ConversationId = "conv-file",
            TransferId = "transfer-1", ChunkIndex = 7,
            Data = Enumerable.Range(0, 1000).Select(i => (byte)(i % 256)).ToArray()
        },
        new FileAckMessage
        {
            SenderId = Sender(), ConversationId = "conv-file",
            TransferId = "transfer-1", Accepted = true, ErrorMessage = null
        },
        new KeyExchangeMessage
        {
            SenderId = Sender(), ConversationId = "conv-kex",
            EphemeralPublicKey = Enumerable.Repeat((byte)0x5A, 91).ToArray(), IsResponse = false
        },
        new GroupInviteMessage
        {
            SenderId = Sender(), ConversationId = "group-1",
            GroupId = "group-1", GroupName = "开发组",
            EncryptedGroupKey = Enumerable.Repeat((byte)0x33, 32).ToArray(),
            MemberIds = [Sender(), Enumerable.Repeat((byte)0x77, 20).ToArray()]
        },
        new GroupNotifyMessage
        {
            SenderId = Sender(), ConversationId = "group-1",
            GroupId = "group-1", Action = "leave", OperatorId = Sender()
        },
        new DeliveryAckMessage
        {
            SenderId = Sender(), ConversationId = "conv-private",
            AcknowledgedMessageId = Guid.Parse("22222222-2222-2222-2222-222222222222"), Status = "delivered"
        }
    };

    [Theory]
    [MemberData(nameof(AllUnionCases))]
    public void 八个Union子类_以Message序列化后_能还原运行时类型与公共字段(Message original)
    {
        // 注意：变量静态类型必须是 Message，否则 MessagePack 不会写入 union 判别键。
        var payload = Serializer.Serialize(original);
        var restored = Serializer.Deserialize<Message>(payload);

        restored.GetType().ShouldBe(original.GetType(), "union 判别键未能还原运行时子类型");
        restored.MessageId.ShouldBe(original.MessageId);
        restored.SenderId.ShouldBe(original.SenderId);
        restored.Timestamp.ShouldBe(original.Timestamp);
        restored.ConversationId.ShouldBe(original.ConversationId);
    }

    [Fact]
    public void TextMessage_内容与群聊标志往返一致()
    {
        Message original = new TextMessage
        {
            SenderId = Sender(), ConversationId = "c", Content = "带 emoji 的消息 🚀 中文", IsGroup = true
        };

        var restored = Serializer.Deserialize<Message>(Serializer.Serialize(original)).ShouldBeOfType<TextMessage>();

        restored.Content.ShouldBe("带 emoji 的消息 🚀 中文");
        restored.IsGroup.ShouldBeTrue();
    }

    [Fact]
    public void FileMetaMessage_哈希与分块参数往返一致()
    {
        var hash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Message original = new FileMetaMessage
        {
            SenderId = Sender(), ConversationId = "c",
            TransferId = "t", FileName = "a.bin", FileSize = 999999, FileHash = hash,
            ChunkSize = 65536, TotalChunks = 16
        };

        var restored = Serializer.Deserialize<Message>(Serializer.Serialize(original)).ShouldBeOfType<FileMetaMessage>();

        restored.FileHash.ShouldBe(hash);
        restored.FileSize.ShouldBe(999999);
        restored.ChunkSize.ShouldBe(65536);   // set 属性：验证 MsgPack017 (init 被重置) 修复
        restored.TotalChunks.ShouldBe(16);
        restored.FileName.ShouldBe("a.bin");
    }

    [Fact]
    public void GroupInviteMessage_集合字段往返一致_锚定类型整合点()
    {
        // GroupInviteMessage.MemberIds 是 List<byte[]> —— T3 的"锚定类型"修复点
        var members = new List<byte[]> { Sender(), Enumerable.Repeat((byte)0x99, 20).ToArray(), Sender() };
        Message original = new GroupInviteMessage
        {
            SenderId = Sender(), ConversationId = "g",
            GroupId = "g", GroupName = "群", EncryptedGroupKey = new byte[32], MemberIds = members
        };

        var restored = Serializer.Deserialize<Message>(Serializer.Serialize(original)).ShouldBeOfType<GroupInviteMessage>();

        restored.MemberIds.Count.ShouldBe(3);
        for (var i = 0; i < members.Count; i++)
            restored.MemberIds[i].ShouldBe(members[i]);
    }

    [Fact]
    public void FileChunkMessage_大数据分块往返字节完全一致()
    {
        var data = new byte[65536];
        Random.Shared.NextBytes(data);
        Message original = new FileChunkMessage
        {
            SenderId = Sender(), ConversationId = "c", TransferId = "t", ChunkIndex = 0, Data = data
        };

        var restored = Serializer.Deserialize<Message>(Serializer.Serialize(original)).ShouldBeOfType<FileChunkMessage>();

        restored.Data.Length.ShouldBe(data.Length);
        restored.Data.ShouldBe(data);
        restored.ChunkIndex.ShouldBe(0);
    }

    [Fact]
    public void DeliveryAckMessage_Guid字段往返一致()
    {
        var ackId = Guid.NewGuid();
        Message original = new DeliveryAckMessage
        {
            SenderId = Sender(), ConversationId = "c", AcknowledgedMessageId = ackId, Status = "error"
        };

        var restored = Serializer.Deserialize<Message>(Serializer.Serialize(original)).ShouldBeOfType<DeliveryAckMessage>();

        restored.AcknowledgedMessageId.ShouldBe(ackId);
        restored.Status.ShouldBe("error");
    }

    [Fact]
    public void MessageId_未显式设置时_反序列化后不应被重置为GuidEmpty()
    {
        // MessageId 是 set 属性，T3 修复的核心：init 初始化器会在反序列化时被无条件重置
        Message original = new TextMessage { SenderId = Sender(), ConversationId = "c", Content = "x" };

        var restored = Serializer.Deserialize<Message>(Serializer.Serialize(original));

        restored.MessageId.ShouldNotBe(Guid.Empty);
        restored.MessageId.ShouldBe(original.MessageId);
    }
}
