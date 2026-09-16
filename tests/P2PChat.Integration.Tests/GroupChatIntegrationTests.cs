using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using P2PChat.Chat.Services;
using P2PChat.Core.Models;
using P2PChat.Crypto.Encryption;
using P2PChat.Integration.Tests.Support;
using Shouldly;

namespace P2PChat.Integration.Tests;

/// <summary>
/// 群组邀请与群消息验证。
/// </summary>
public class GroupChatIntegrationTests
{
    private static NodeInfo MakeNode(int port)
        => new()
        {
            NodeId = NodeId.CreateRandom(),
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            PublicKey = new byte[32]
        };

    private static (GroupChatService Service, InMemoryKeyStore KeyStore, FakeDhtService Dht, RecordingMessageRouter Router)
        BuildService()
    {
        var encryption = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        var keyStore = new InMemoryKeyStore(encryption);
        var identity = keyStore.GetOrCreateIdentity();
        var local = new NodeInfo
        {
            NodeId = NodeId.FromPublicKey(identity.PublicKey),
            EndPoint = new IPEndPoint(IPAddress.Loopback, 46001),
            PublicKey = identity.PublicKey
        };
        var dht = new FakeDhtService(local);
        var router = new RecordingMessageRouter();
        return (new GroupChatService(dht, router, encryption, keyStore), keyStore, dht, router);
    }

    private static async Task<ChatMessageEvent> ReceiveOneAsync(
        IAsyncEnumerable<ChatMessageEvent> stream, int timeoutMs = 20000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await foreach (var evt in stream.WithCancellation(cts.Token))
            return evt;
        throw new TimeoutException($"等待群消息超时 ({timeoutMs}ms)");
    }

    [Fact]
    public async Task 创建群组_本地存储群组与32字节群密钥()
    {
        var (service, keyStore, _, _) = BuildService();

        var group = await service.CreateGroupAsync("测试群", []);

        service.GetGroup(group.GroupId).ShouldNotBeNull();
        service.GetKnownGroups().Count.ShouldBe(1);
        group.GroupKey.Length.ShouldBe(32);
        keyStore.GetGroupKey(group.GroupId).ShouldNotBeNull();
        keyStore.GetGroupKey(group.GroupId)!.ShouldBe(group.GroupKey);
    }

    [Fact]
    public async Task 创建群组_必须把所有初始成员与创建者都记入成员表()
    {
        var (service, _, dht, _) = BuildService();
        var m1 = MakeNode(46010);
        var m2 = MakeNode(46011);
        dht.Register(m1);
        dht.Register(m2);

        var group = await service.CreateGroupAsync("三人群", [m1.NodeId, m2.NodeId]);

        group.MemberIds.Count.ShouldBe(3);   // 2 个成员 + 创建者
        var memberHex = group.MemberIds.Select(b => Convert.ToHexString(b)).ToList();
        memberHex.ShouldContain(m1.NodeId.ToHexString().ToUpperInvariant());
        memberHex.ShouldContain(m2.NodeId.ToHexString().ToUpperInvariant());
    }

    [Fact]
    public async Task 创建群组_必须向每个可发现的成员发送GroupInvite()
    {
        var (service, _, dht, router) = BuildService();
        var m1 = MakeNode(46020);
        var m2 = MakeNode(46021);
        dht.Register(m1);
        dht.Register(m2);

        var group = await service.CreateGroupAsync("邀请群", [m1.NodeId, m2.NodeId]);

        var invites = router.Sent.OfType<GroupInviteMessage>().ToList();
        invites.Count.ShouldBe(2);
        invites.ShouldAllBe(i => i.GroupId == group.GroupId && i.GroupName == "邀请群");
        invites.ShouldAllBe(i => i.EncryptedGroupKey.Length == 32);
    }

    [Fact]
    public async Task 创建群组_成员不可发现时不抛异常_只跳过()
    {
        var (service, _, _, router) = BuildService();
        var offline = MakeNode(46030);   // 未注册到 DHT

        var group = await service.CreateGroupAsync("含离线成员", [offline.NodeId]);

        router.Sent.OfType<GroupInviteMessage>().ShouldBeEmpty();
        service.GetGroup(group.GroupId).ShouldNotBeNull();
    }

    [Fact]
    public async Task 发送群消息_向所有其他成员扇出_且不发送给自己()
    {
        var (service, _, dht, router) = BuildService();
        var m1 = MakeNode(46040);
        var m2 = MakeNode(46041);
        dht.Register(m1);
        dht.Register(m2);
        var group = await service.CreateGroupAsync("扇出群", [m1.NodeId, m2.NodeId]);
        router.Clear();

        await service.SendGroupMessageAsync(group.GroupId, "群消息正文");

        var texts = router.Sent.OfType<TextMessage>().ToList();
        texts.Count.ShouldBe(2, "应只向 2 个其他成员扇出，不包含创建者自己");
        texts.ShouldAllBe(t => t.IsGroup && t.Content == "群消息正文" && t.ConversationId == group.GroupId);
    }

    [Fact]
    public async Task 发送群消息_未知群组必须报错()
    {
        var (service, _, _, _) = BuildService();

        await Should.ThrowAsync<InvalidOperationException>(
            () => service.SendGroupMessageAsync("不存在的群组ID", "内容"));
    }

    [Fact]
    public async Task 发送群消息_成员全部离线时不抛异常()
    {
        var (service, _, _, _) = BuildService();
        var group = await service.CreateGroupAsync("空群", []);

        await Should.NotThrowAsync(() => service.SendGroupMessageAsync(group.GroupId, "无人接收"));
    }

    [Fact]
    public async Task 处理群组邀请_存储群组信息()
    {
        var (service, _, _, _) = BuildService();
        var invite = new GroupInviteMessage
        {
            SenderId = Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray(),
            ConversationId = "g-1",
            GroupId = "g-1",
            GroupName = "被邀请的群",
            EncryptedGroupKey = Enumerable.Repeat((byte)0x42, 32).ToArray(),
            MemberIds = [Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray()]
        };

        await service.HandleInviteAsync(invite);

        var group = service.GetGroup("g-1");
        group.ShouldNotBeNull();
        group!.GroupName.ShouldBe("被邀请的群");
        service.GetKnownGroups().Count.ShouldBe(1);
    }

    [Fact]
    public async Task 处理解散通知_移除群组与群密钥()
    {
        var (service, keyStore, _, _) = BuildService();
        var group = await service.CreateGroupAsync("待解散群", []);
        keyStore.GetGroupKey(group.GroupId).ShouldNotBeNull();

        await service.HandleNotifyAsync(new GroupNotifyMessage
        {
            SenderId = Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray(),
            ConversationId = group.GroupId,
            GroupId = group.GroupId,
            Action = "dissolve",
            OperatorId = Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray()
        });

        service.GetGroup(group.GroupId).ShouldBeNull();
        keyStore.GetGroupKey(group.GroupId).ShouldBeNull();
    }

    [Fact]
    public async Task 端到端_两节点群消息_接收端解密后内容一致()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);

        var group = await alice.Group.CreateGroupAsync("端到端群", [bob.LocalNode.NodeId]);

        // 邀请在 B 侧成功落地的等价状态（邀请链路本身见 KnownDefectsTests）
        bob.KeyStore.SetGroupKey(group.GroupId, group.GroupKey);

        const string text = "群消息：大家好，这是一条群聊内容 🎉";
        await alice.Group.SendGroupMessageAsync(group.GroupId, text);

        var received = await ReceiveOneAsync(bob.GroupHandler.OnMessageReceived);

        received.Content.ShouldBe(text);
        received.IsGroup.ShouldBeTrue();
        received.IsOutgoing.ShouldBeFalse();
        received.ConversationId.ShouldBe(group.GroupId);
    }

    [Fact]
    public async Task 端到端_两节点缺少群密钥时_群消息被丢弃()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);

        const string unknownGroupId = "group-without-key";
        bob.KeyStore.GetGroupKey(unknownGroupId).ShouldBeNull();

        // 直接向 B 发送一个从未加入过的群组消息，不经过邀请链路，显式构造 B 缺少群密钥。
        await alice.Router.SendAsync(bob.LocalNode, new TextMessage
        {
            SenderId = alice.SenderId,
            ConversationId = unknownGroupId,
            Content = "这条消息应被丢弃",
            IsGroup = true
        });

        using var cts = new CancellationTokenSource(2000);
        var got = false;
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in bob.GroupHandler.OnMessageReceived.WithCancellation(cts.Token))
            {
                got = true;
                break;
            }
        });
        got.ShouldBeFalse("缺少群组密钥时 GroupMessageHandler 应丢弃消息而不是上报");
    }
}
