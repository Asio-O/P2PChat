using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using P2PChat.Chat.Services;
using P2PChat.Chat.Tests.Support;
using P2PChat.Core.Enums;
using P2PChat.Core.Extensions;
using P2PChat.Core.Models;
using P2PChat.Crypto.Keys;
using Shouldly;

namespace P2PChat.Chat.Tests;

/// <summary>
/// 联系人服务 <c>ContactService</c> 的单元测试。
/// <para>
/// 此前该服务 0 覆盖，这里锁住三条真实契约：
/// <list type="number">
///   <item><c>/add &lt;节点ID&gt; [ip:port] [别名]</c> 的第二个 token 落到 <c>Contact.EndPoint</c>，
///         未给时必须是 null（而不是空串），否则端点优先级判定会走错分支；</item>
///   <item><c>contacts.json</c> 往返：别名、显式端点、添加时间必须原样恢复；</item>
///   <item>磁盘文件损坏时加载必须静默降级为空列表，而不是让节点启动失败。</item>
/// </list>
/// 全部 JSON 均走源生成器 <c>JsonContext</c>（AOT 安全，不引入反射）。
/// </para>
/// </summary>
public class ContactServiceTests
{
    private static ContactService NewService() => new(NullLogger<ContactService>.Instance);

    private static List<StoredContact> ReadStoredContacts(string path)
        => JsonSerializer.Deserialize(
               File.ReadAllText(path), JsonContext.Default.ListStoredContact) ?? [];

    [Fact]
    public async Task 添加联系人_第二个token是ip_port时登记为显式端点_并去除首尾空白()
    {
        using var dir = new TempDataDir();
        var service = NewService();
        var peer = NodeId.CreateRandom();

        // 对应 TUI 的 /add <节点ID> 192.168.1.10:5000 老王
        await service.AddContactAsync(peer, "老王", "  192.168.1.10:5000  ");

        var contact = service.FindByNodeId(peer);
        contact.ShouldNotBeNull();
        contact!.Alias.ShouldBe("老王");
        contact.EndPoint.ShouldBe("192.168.1.10:5000");
        EndpointText.TryParse(contact.EndPoint, out var endpoint).ShouldBeTrue();
        endpoint!.Port.ShouldBe(5000);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 添加联系人_未给出有效端点时端点必须为null_而不是空串(string? rawEndPoint)
    {
        using var dir = new TempDataDir();
        var service = NewService();
        var peer = NodeId.CreateRandom();

        await service.AddContactAsync(peer, "无端点", rawEndPoint);

        var contact = service.FindByNodeId(peer);
        contact.ShouldNotBeNull();
        contact!.EndPoint.ShouldBeNull("空串会被端点优先级逻辑误判为「有显式端点」");
    }

    [Fact]
    public async Task 按节点ID查找_命中返回该联系人_未命中返回null()
    {
        using var dir = new TempDataDir();
        var service = NewService();
        var known = NodeId.CreateRandom();
        var stranger = NodeId.CreateRandom();
        await service.AddContactAsync(known, "张三", "10.0.0.1:7000");

        service.FindByNodeId(known).ShouldNotBeNull();
        service.FindByNodeId(known)!.Alias.ShouldBe("张三");
        service.FindByNodeId(stranger).ShouldBeNull();
    }

    [Fact]
    public async Task 联系人写入磁盘后重建服务可原样读回_包含别名与显式端点()
    {
        using var dir = new TempDataDir();
        var peer = NodeId.CreateRandom();
        var noEndPointPeer = NodeId.CreateRandom();

        var first = NewService();
        await first.AddContactAsync(peer, "带端点的联系人", "10.0.0.9:9100");
        await first.AddContactAsync(noEndPointPeer, "无端点的联系人");

        File.Exists(dir.ContactsFile).ShouldBeTrue("添加联系人必须落盘，否则重启后丢失");

        var reloaded = NewService();
        var withEndPoint = reloaded.FindByNodeId(peer);
        withEndPoint.ShouldNotBeNull();
        withEndPoint!.Alias.ShouldBe("带端点的联系人");
        withEndPoint.EndPoint.ShouldBe("10.0.0.9:9100");
        withEndPoint.AddedAt.ShouldBe(
            first.FindByNodeId(peer)!.AddedAt,
            "contacts.json 往返必须保留添加时间");

        reloaded.FindByNodeId(noEndPointPeer)!.EndPoint.ShouldBeNull();

        var all = await reloaded.GetAllContactsAsync();
        all.Count.ShouldBe(2);
    }

    [Fact]
    public async Task 移除联系人后查找返回null_且磁盘文件里也不再包含该节点()
    {
        using var dir = new TempDataDir();
        var service = NewService();
        var keep = NodeId.CreateRandom();
        var drop = NodeId.CreateRandom();
        await service.AddContactAsync(keep, "保留", "10.0.0.1:1");
        await service.AddContactAsync(drop, "删除", "10.0.0.2:2");

        await service.RemoveContactAsync(drop);

        service.FindByNodeId(drop).ShouldBeNull();
        service.FindByNodeId(keep).ShouldNotBeNull();
        var onDisk = ReadStoredContacts(dir.ContactsFile);
        onDisk.Count.ShouldBe(1);
        onDisk[0].NodeId.ShouldBe(keep.ToHexString(), "磁盘上按 NodeId.ToHexString() 的小写 hex 存储");
    }

    [Fact]
    public async Task 更新别名_只改别名不改显式端点()
    {
        using var dir = new TempDataDir();
        var service = NewService();
        var peer = NodeId.CreateRandom();
        await service.AddContactAsync(peer, "旧名", "10.0.0.3:3000");

        await service.UpdateAliasAsync(peer, "新名");

        var contact = service.FindByNodeId(peer)!;
        contact.Alias.ShouldBe("新名");
        contact.EndPoint.ShouldBe("10.0.0.3:3000", "改别名不得把直连端点弄丢");

        // 落到磁盘上
        ReadStoredContacts(dir.ContactsFile).Single().Alias.ShouldBe("新名");
    }

    [Fact]
    public async Task 更新未知节点的别名_静默忽略_不抛异常()
    {
        using var dir = new TempDataDir();
        var service = NewService();
        var stranger = NodeId.CreateRandom();

        await service.UpdateAliasAsync(stranger, "不存在");

        service.FindByNodeId(stranger).ShouldBeNull();
    }

    [Fact]
    public async Task 更新在线状态_推送状态事件_并写入最后在线时间()
    {
        using var dir = new TempDataDir();
        var service = NewService();
        var peer = NodeId.CreateRandom();
        await service.AddContactAsync(peer, "上线吧");

        await service.UpdateOnlineStatusAsync(peer, true);

        var evt = await AsyncStream.FirstAsync(service.OnStatusChanged);
        evt.NodeId.ShouldBe(peer);
        evt.IsOnline.ShouldBeTrue();
        evt.State.ShouldBe(PeerState.Online);

        var contact = service.FindByNodeId(peer)!;
        contact.IsOnline.ShouldBeTrue();
        contact.State.ShouldBe(PeerState.Online);
        contact.LastOnlineAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task 更新未知节点的在线状态_不推送任何状态事件()
    {
        using var dir = new TempDataDir();
        var service = NewService();

        await service.UpdateOnlineStatusAsync(NodeId.CreateRandom(), true);

        // 用超时证明「确实没有事件」而不是「没等到」
        await Should.ThrowAsync<TimeoutException>(
            () => AsyncStream.FirstAsync(service.OnStatusChanged, timeoutMs: 300));
    }

    [Fact]
    public async Task 获取全部联系人_在线的排前面_同状态内按别名排序()
    {
        using var dir = new TempDataDir();
        var service = NewService();
        var offlineZ = NodeId.CreateRandom();
        var offlineA = NodeId.CreateRandom();
        var onlineB = NodeId.CreateRandom();
        await service.AddContactAsync(offlineZ, "离线Z");
        await service.AddContactAsync(offlineA, "离线A");
        await service.AddContactAsync(onlineB, "在线B");
        await service.UpdateOnlineStatusAsync(onlineB, true);

        var all = await service.GetAllContactsAsync();

        all.Select(c => c.Alias).ShouldBe(new[] { "在线B", "离线A", "离线Z" });
    }

    [Fact]
    public void 加载损坏的contacts_json_必须静默降级为空列表_不得让节点启动失败()
    {
        using var dir = new TempDataDir();
        dir.WriteContactsFile("{ 这不是合法 JSON");

        var service = NewService();   // 构造即加载，不得抛异常

        service.FindByNodeId(NodeId.CreateRandom()).ShouldBeNull();
    }

    [Fact]
    public void 加载大写hex的contacts_json_联系人必须仍可查到大写不得静默丢失()
    {
        using var dir = new TempDataDir();
        var peer = NodeId.CreateRandom();
        // 手工编辑或从别处导入的 contacts.json 很可能写成大写 hex。
        // 修复前：LoadContacts 用磁盘原始 sc.NodeId 当字典键（大写），
        // 而 Add/Remove/FindByNodeId 一律用 NodeId.ToHexString()（小写），
        // 于是这条联系人**加载成功却永远查不到** —— 文件读起来没问题，人却凭空消失。
        dir.WriteContactsFile(TestNodes.BuildContactsJson(new StoredContact
        {
            NodeId = peer.ToHexString().ToUpperInvariant(),
            Alias = "大写hex联系人",
            EndPoint = "127.0.0.1:20001",
            AddedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        }));

        var service = NewService();

        var contact = service.FindByNodeId(peer);
        contact.ShouldNotBeNull();
        contact!.Alias.ShouldBe("大写hex联系人");
        contact.NodeId.ShouldBe(peer);
    }

    [Fact]
    public void 加载顶层为单个对象而非数组的contacts_json_必须按一条联系人兼容()
    {
        using var dir = new TempDataDir();
        var peer = NodeId.CreateRandom();
        // 裸对象是最常见的手改失误（PowerShell `@(...) | ConvertTo-Json` 单元素时就会折叠成对象）。
        // 修复前：按 List<StoredContact> 反序列化抛 JsonException → 外层 catch → **全部**联系人静默清空。
        dir.WriteContactsFile(TestNodes.BuildSingleContactObjectJson(new StoredContact
        {
            NodeId = peer.ToHexString(),
            Alias = "单对象联系人",
            EndPoint = "127.0.0.1:20002",
            AddedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        }));

        var service = NewService();

        var contact = service.FindByNodeId(peer);
        contact.ShouldNotBeNull();
        contact!.Alias.ShouldBe("单对象联系人");
    }

    [Fact]
    public async Task 加载含单条非法NodeId的contacts_json_只跳过该条_其余联系人必须保留()
    {
        using var dir = new TempDataDir();
        var good = NodeId.CreateRandom();
        // 一条合法 + 一条 NodeId 长度非法。修复前整个 foreach 抛异常，
        // 被外层 catch 吞掉 → **全部**联系人静默清空。
        dir.WriteContactsFile(TestNodes.BuildContactsJson(
            new StoredContact
            {
                NodeId = good.ToHexString(),
                Alias = "好联系人",
                AddedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
            },
            new StoredContact
            {
                NodeId = "ABC",   // 非法：不是 20 字节
                Alias = "坏联系人",
                AddedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
            }));

        var service = NewService();

        service.FindByNodeId(good).ShouldNotBeNull();
        (await service.GetAllContactsAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public void 加载缺少显式端点字段的旧版contacts_json_仍能恢复别名()    {
        using var dir = new TempDataDir();
        var peer = NodeId.CreateRandom();
        // 旧版 contacts.json 没有 EndPoint 字段；NodeId 沿用 SaveContacts 写出的小写 hex
        dir.WriteContactsFile(TestNodes.BuildContactsJson(new StoredContact
        {
            NodeId = peer.ToHexString(),
            Alias = "旧版联系人",
            AddedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        }));

        var service = NewService();

        var contact = service.FindByNodeId(peer);
        contact.ShouldNotBeNull();
        contact!.Alias.ShouldBe("旧版联系人");
        contact.EndPoint.ShouldBeNull();
    }
}
