using P2PChat.Core.Extensions;
using P2PChat.Core.Models;
using Shouldly;

namespace P2PChat.Core.Tests;

public class SerializationTests
{
    private readonly MessagePackSerializer _serializer = new();

    [Fact]
    public void Serialize_TextMessage_RoundTrip()
    {
        var msg = new TextMessage
        {
            SenderId = new byte[20],
            ConversationId = "test-conv",
            Content = "Hello, World!",
            IsGroup = false
        };

        var bytes = _serializer.Serialize(msg);
        var deserialized = _serializer.Deserialize<TextMessage>(bytes);

        deserialized.Content.ShouldBe("Hello, World!");
        deserialized.ConversationId.ShouldBe("test-conv");
        deserialized.IsGroup.ShouldBeFalse();
    }

    [Fact]
    public void Serialize_FileMetaMessage_RoundTrip()
    {
        var msg = new FileMetaMessage
        {
            SenderId = new byte[20],
            ConversationId = "test-conv",
            TransferId = "abc123",
            FileName = "test.pdf",
            FileSize = 1024000,
            FileHash = new byte[32],
            TotalChunks = 16
        };

        var bytes = _serializer.Serialize(msg);
        var deserialized = _serializer.Deserialize<FileMetaMessage>(bytes);

        deserialized.FileName.ShouldBe("test.pdf");
        deserialized.FileSize.ShouldBe(1024000);
        deserialized.TotalChunks.ShouldBe(16);
    }

    [Fact]
    public void Serialize_KeyExchangeMessage_RoundTrip()
    {
        var msg = new KeyExchangeMessage
        {
            SenderId = new byte[20],
            ConversationId = "test-conv",
            EphemeralPublicKey = new byte[32],
            IsResponse = true
        };

        var bytes = _serializer.Serialize(msg);
        var deserialized = _serializer.Deserialize<KeyExchangeMessage>(bytes);

        deserialized.IsResponse.ShouldBeTrue();
        deserialized.EphemeralPublicKey.Length.ShouldBe(32);
    }
}
