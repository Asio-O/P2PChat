using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Extensions;
using P2PChat.Core.Models;
using P2PChat.Networking.Dht;
using P2PChat.Networking.Transport;
using P2PChat.Crypto.Encryption;
using P2PChat.Crypto.Keys;
using P2PChat.Chat.Routing;
using P2PChat.Chat.Services;
using P2PChat.Chat.Handlers;
using P2PChat.UI.Views;
using Serilog;

namespace P2PChat.App;

public class Program
{
    // Windows 控制台 UTF-8 代码页 API
    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleOutputCP(uint codePage);
    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleCP(uint codePage);

    public static async Task Main(string[] args)
    {
        // 0. 必须在任何输出之前设置控制台为 UTF-8
        //    双击 exe 时 Windows 默认使用系统代码页(如 GBK)而非 UTF-8
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SetConsoleOutputCP(65001); // UTF-8
            SetConsoleCP(65001);
        }
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        // 1. 加载配置
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables("P2PCHAT_")
            .AddCommandLine(args)
            .Build();

        // 2. Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(Path.Combine(
                Core.Extensions.DataPath.GetDirectory("logs"), "p2pchat-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // 3. 创建基础服务
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(b => { b.ClearProviders(); b.AddSerilog(Log.Logger); });

        var chatConfig = configuration.GetSection("P2PChat");
        var preferredUdpPort = chatConfig.GetValue<int>("UdpPort", 0);
        var preferredTcpPort = chatConfig.GetValue<int>("TcpPort", 0);
        var kBucketSize = chatConfig.GetValue<int>("KBucketSize", 20);
        var alpha = chatConfig.GetValue<int>("Alpha", 3);
        // 入站重放防护的时间窗。<= 0 是「关闭时间窗」的逃生阀，
        // 供时钟严重偏移的机器自救 —— 但关闭必须是**显式**的，启动时会 LogWarning 并在
        // 自检块显示「已关闭」，不允许它只悄悄躺在配置文件里。
        // 现行约定（含关闭开关到底关掉了什么）见 Agent.md §7「关闭开关（逃生阀）」。
        // 这里只做解析与钳制，TimeSpan 的换算放在下面 —— 配置写错不能让进程起不来。
        var replayMaxAgeSeconds = chatConfig.GetValue<double>("ReplayMaxAgeSeconds", 3600);
        var bootstrapNodes = chatConfig.GetSection("BootstrapNodes")
            .Get<string[]>() ?? [];

        services.AddSingleton<ISerializer, MessagePackSerializer>();
        services.AddSingleton<IEncryptionService, AesGcmEncryptionService>();
        services.AddSingleton<IKeyStore, FileBackedKeyStore>();

        // 4. 先构建临时提供器获取身份密钥 + 创建传输层(会自动选择可用端口)
        var tempSp = services.BuildServiceProvider();
        var loggerFactory = tempSp.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<Program>();

        // 加载持久化已知节点 + 合并公开DHT引导节点
        var knownPeers = LoadKnownPeers();
        var publicBootstrapNodes = new[]
        {
            "router.bittorrent.com:6881",
            "router.utorrent.com:6881",
            "dht.transmissionbt.com:6881",
            "dht.aelitis.com:6881",
        };
        bootstrapNodes = bootstrapNodes
            .Concat(knownPeers)
            .Concat(publicBootstrapNodes)
            .Distinct()
            .ToArray();
        var keyStore = tempSp.GetRequiredService<IKeyStore>();
        var identity = keyStore.GetOrCreateIdentity();
        var localNodeId = NodeId.FromPublicKey(identity.PublicKey);

        // 创建UDP传输 (构造函数直接绑定)
        var udpTransport = new UdpTransport(preferredUdpPort,
            loggerFactory.CreateLogger<UdpTransport>());
        var actualUdpPort = udpTransport.LocalEndPoint.Port;

        // 创建TCP传输并启动监听 (自动选择端口)
        var tcpTransport = new TcpTransport(
            loggerFactory.CreateLogger<TcpTransport>(), loggerFactory);
        await tcpTransport.StartListeningAsync(preferredTcpPort);
        var actualTcpPort = tcpTransport.ListenPort;

        // 用实际端口创建本地节点信息
        var localIpAddress = GetLocalIPAddress();
        var localNode = new NodeInfo
        {
            NodeId = localNodeId,
            EndPoint = new IPEndPoint(localIpAddress, actualTcpPort),
            PublicKey = identity.PublicKey,
            State = Core.Enums.PeerState.Online
        };

        // 2.1 / 2.3 启动时尝试 UPnP 端口映射（尽力而为，失败仅 Debug 日志）。
        // 同一端口同时申请 TCP 与 UDP —— 与现有 DHT/TCP 监听端口一致。
        // 无 UPnP 时 TUI 会显式标注，调用方不必再处理 fallback 路径。
        var upnpClient = new UpnpClient(
            loggerFactory.CreateLogger<UpnpClient>(),
            internalClientIp: localIpAddress.ToString());
        UpnpMapping? upnpMapping = null;
        try
        {
            upnpMapping = await upnpClient.TryMapAsync(actualTcpPort, ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "UPnP TryMapAsync 抛异常 —— 视为未映射");
            upnpMapping = null;
        }
        if (upnpMapping is not null)
        {
            logger.LogInformation("UPnP 映射成功：内网 {Internal}:{Port} → 公网 {External}",
                localIpAddress, actualTcpPort, upnpMapping.ExternalEndPoint);
        }
        else
        {
            logger.LogWarning("UPnP 不可用 —— 本机可能仅对同网段/已有连接可达；公网对端可通过 /add <ip:port> 或 /connect <ip:port> 手工接入");
        }

        // 入站重放防护：把 P2PChat:ReplayMaxAgeSeconds 换算成 TimeSpan。
        // 钳制上界，避免一个手滑的巨大值（例如 1e18）让 TimeSpan.FromSeconds 抛
        // OverflowException 把整个启动干掉 —— 配置写错应该降级，不该崩进程。
        var replayMaxAge = replayMaxAgeSeconds switch
        {
            <= 0 => TimeSpan.Zero,                                              // 显式关闭时间窗（逃生阀）
            _ => TimeSpan.FromSeconds(Math.Min(replayMaxAgeSeconds, TimeSpan.MaxValue.TotalSeconds))
        };
        if (replayMaxAge > TimeSpan.Zero)
        {
            logger.LogInformation("重放防护:   已启用（最大消息年龄 {MaxAge}）", ReplayGuardStatus.FormatAge(replayMaxAge));
        }
        else
        {
            // 「防护已关」不能只存在于配置文件里：必须显式告警，否则用户无从得知
            // 捕获的旧信封可以无限重放（MessageId 去重仍在，但窗口内不再拦）。
            logger.LogWarning(
                "重放防护已关闭 —— P2PChat:ReplayMaxAgeSeconds={Seconds}（<=0 表示关闭时间窗）；" +
                "超过最大消息年龄的旧信封将被接受，捕获的包可被反复重放。" +
                "仅在两端时钟严重偏移、否则全网互拒时才应这样配置。",
                replayMaxAgeSeconds);
        }

        // 解析引导节点 (支持域名和IP)
        var bootstrapEndpoints = new List<IPEndPoint>();
        foreach (var ep in bootstrapNodes)
        {
            try
            {
                var parts = ep.Split(':');
                var host = parts[0];
                var port = int.Parse(parts[1]);
                // 先尝试直接解析IP，失败则DNS查询
                if (!IPAddress.TryParse(host, out var ip))
                {
                    var addresses = await Dns.GetHostAddressesAsync(host);
                    ip = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                         ?? addresses.First();
                }
                bootstrapEndpoints.Add(new IPEndPoint(ip, port));
            }
            catch (Exception ex)
            {
                logger.LogWarning("解析引导节点失败: {Node} - {Msg}", ep, ex.Message);
            }
        }

        logger.LogInformation("P2PChat 启动中...");
        logger.LogInformation("数据目录: {DataDir}", Core.Extensions.DataPath.Root);
        logger.LogInformation("本地节点ID: {NodeId}", localNodeId.ToHexString());
        logger.LogInformation("实际端口: UDP={Udp}, TCP={Tcp}", actualUdpPort, actualTcpPort);
        if (bootstrapEndpoints.Count > 0)
            logger.LogInformation("引导节点: {Nodes}", string.Join(", ", bootstrapNodes));
        else
            logger.LogWarning("未配置引导节点 - 将以隔离模式运行");

        // 5. 注册剩余服务
        services.AddSingleton(localNode);
        services.AddSingleton<IUdpTransport>(udpTransport);
        services.AddSingleton<ITcpTransport>(tcpTransport);
        services.AddSingleton<IUpnpClient>(upnpClient);
        services.AddSingleton<IRoutingTable>(new RoutingTable(localNodeId, kBucketSize));
        services.AddSingleton<IDhtService>(sp =>
        {
            var rt = sp.GetRequiredService<IRoutingTable>();
            var transport = sp.GetRequiredService<IUdpTransport>();
            var dhtLogger = sp.GetRequiredService<ILogger<MainlineDhtService>>();
            return new MainlineDhtService(
                localNode, rt, transport, dhtLogger,
                bootstrapEndpoints.AsReadOnly(), alpha);
        });

        // Chat层
        // IReplayGuard 必须在 IMessageRouter 之前注册（MessageRouter 的 replayGuard 形参是必填的）。
        // 这里用 AddSingleton 而非 TryAddSingleton：本条携带 P2PChat:ReplayMaxAgeSeconds 的解析结果，
        // 是**权威**注册；即便将来有人改成调用 AddP2PChatChat()，它内部那条 1 小时默认值的
        // TryAddSingleton 因为本条已存在而不会生效，配置永远不会「悄悄失效回默认值」。
        services.AddSingleton<IReplayGuard>(sp => new MessageReplayGuard(
            sp.GetRequiredService<ILogger<MessageReplayGuard>>(),
            replayMaxAge));
        services.AddSingleton<IMessageRouter, MessageRouter>();
        services.AddSingleton<IGroupMetadataStore, FileBackedGroupMetadataStore>();
        services.AddSingleton<PrivateMessageHandler>();
        services.AddSingleton<GroupMessageHandler>();
        services.AddSingleton<KeyExchangeHandler>();
        services.AddSingleton<GroupInviteHandler>();
        services.AddSingleton<GroupNotifyHandler>();
        // ⚠️ ChatService 必须以【单例 + 接口转发】的方式注册，且 IChatService 与
        // IChatEventPublisher 必须解析到**同一个实例**。
        //
        // 原因：handler 把收到的事件投递进 ChatService 持有的那一条 Channel，
        // 而 TUI（`P2PChatTui.ProcessIncomingMessagesAsync`）读的是 IChatService.OnMessageReceived。
        // 两者必须是**同一条通道**。若写成两个独立的注册，DI 会建出两个 ChatService 实例 ——
        // 症状是「消息收得到、界面不显示」，且 `dotnet build` 完全不报错（DI 解析是运行期的），
        // 单元测试也会全绿（它们读的是 handler 自己的通道）。这正是本缺陷能长期潜伏的原因。
        //
        // 另注：本文件把 Chat 层注册**内联**了一份，并未调用 `AddP2PChatChat()`。
        // 所以在 `ChatServiceCollectionExtensions` 里新增的任何注册都不会在真实 App 生效 ——
        // 改这里时必须同步核对那个扩展方法，两处不能只改一处。
        services.AddSingleton<ChatService>();
        services.AddSingleton<IChatService>(sp => sp.GetRequiredService<ChatService>());
        services.AddSingleton<IChatEventPublisher>(sp => sp.GetRequiredService<ChatService>());
        services.AddSingleton<IGroupChatService, GroupChatService>();
        services.AddSingleton<IContactService, ContactService>();

        // FileTransfer层
        services.AddSingleton<IFileTransferService,
            P2PChat.FileTransfer.Services.FileTransferService>();

        // UI层
        // 自检块要显示「重放防护: 已启用/已关闭」。UI 只引用 Core，拿不到 Chat 层的
        // MessageReplayGuard，因此在装配处把状态**值**交给它（ReplayGuardStatus），
        // 而不是让 TUI 去问 IReplayGuard（该接口是安全边界，只有 TryAccept）。
        services.AddSingleton(new ReplayGuardStatus(replayMaxAge > TimeSpan.Zero, replayMaxAge));
        services.AddSingleton<P2PChatTui>();

        // 6. 构建最终提供器
        var provider = services.BuildServiceProvider();

        try
        {
            // 启动DHT引导 (如果有引导节点)
            var dhtService = provider.GetRequiredService<IDhtService>();
            if (bootstrapEndpoints.Count > 0)
                _ = dhtService.BootstrapAsync(CancellationToken.None);

            // 把带显式端点的联系人登记为「静态对端」：手工添加的联系人无需任何 DHT 发现即可直连。
            // 公共 Mainline DHT 上没有任何节点为我们宣告 NodeId→端点 映射，因此这是当前唯一可靠的直连手段。
            RegisterStaticPeersFromContacts(provider, dhtService, logger);

            var dht = (MainlineDhtService)dhtService;
            var dhtCts = new CancellationTokenSource();
            _ = dht.StartReceivingAsync(dhtCts.Token);

            // 2.2 启动阶段把 UPnP 探测结果应用到 DHT 服务（更新 LocalNode.ExternalEndPoint + MappingState）。
            // ApplyMapping 内部在成功时会立即补一次 AnnounceNowAsync，让公共 DHT 看到我们的公网入口。
            dht.ApplyMapping(upnpMapping);

            // 注册消息处理器到路由器
            var router = provider.GetRequiredService<IMessageRouter>();
            router.RegisterHandler(provider.GetRequiredService<PrivateMessageHandler>());
            router.RegisterHandler(provider.GetRequiredService<GroupMessageHandler>());
            router.RegisterHandler(provider.GetRequiredService<KeyExchangeHandler>());
            router.RegisterHandler(provider.GetRequiredService<GroupInviteHandler>());
            router.RegisterHandler(provider.GetRequiredService<GroupNotifyHandler>());

            // 启动TCP入站连接处理
            var appCts = new CancellationTokenSource();
            _ = ProcessIncomingTcpAsync(tcpTransport, router, provider, appCts.Token);

            // 启动TUI (阻塞主线程)
            var tui = provider.GetRequiredService<P2PChatTui>();
            await tui.RunAsync(appCts.Token);

            appCts.Cancel();
            dhtCts.Cancel();

            // 保存已知节点供下次启动使用
            var knownNodes = dhtService.GetAllKnownNodes()
                .Select(n => $"{n.EndPoint.Address}:{n.EndPoint.Port}")
                .Distinct()
                .ToArray();
            SaveKnownPeers(knownNodes);

            logger.LogInformation("P2PChat 已退出");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "P2PChat 致命错误");
        }
        finally
        {
            // 2.1 进程退出时撤销 UPnP 映射（如果之前成功）—— 仅在映射成功的前提下尝试，
            // 否则路由器会直接拒响应（找不到对应映射），徒增延迟。
            if (upnpMapping is not null)
            {
                try
                {
                    await upnpClient.RemoveAsync(actualTcpPort, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "UPnP 映射撤销失败 —— 路由器租约会自动过期");
                }
            }
            Log.CloseAndFlush();
        }
    }

    private static async Task ProcessIncomingTcpAsync(
        ITcpTransport transport, IMessageRouter router,
        IServiceProvider sp, CancellationToken ct)
    {
        var logger = sp.GetRequiredService<ILogger<Program>>();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var connection = await transport.AcceptAsync(ct);
                logger.LogDebug("新TCP连接: {Endpoint}", connection.RemoteEndPoint);
                var msgRouter = (MessageRouter)router;
                _ = msgRouter.ProcessIncomingConnectionAsync(connection, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "TCP接受连接异常");
            }
        }
    }

    /// <summary>
    /// 把持久化联系人里带显式端点的那些登记为静态对端。
    /// <para>
    /// 这样重启后无需重新 <c>/add</c> 即可直连；端点无法解析的条目跳过并告警，不阻断启动。
    /// </para>
    /// </summary>
    private static void RegisterStaticPeersFromContacts(
        IServiceProvider provider, IDhtService dhtService, Microsoft.Extensions.Logging.ILogger logger)
    {
        try
        {
            var contacts = provider.GetRequiredService<IContactService>()
                .GetAllContactsAsync().GetAwaiter().GetResult();

            foreach (var contact in contacts)
            {
                if (string.IsNullOrWhiteSpace(contact.EndPoint))
                    continue;

                if (!Core.Extensions.EndpointText.TryParse(contact.EndPoint, out var endPoint))
                {
                    logger.LogWarning("联系人 {Alias} 的端点无法解析，已忽略: {EndPoint}",
                        contact.Alias, contact.EndPoint);
                    continue;
                }

                dhtService.RegisterStaticPeer(new NodeInfo
                {
                    NodeId = contact.NodeId,
                    EndPoint = endPoint,
                    // 联系人只存 NodeId + ip:port，对端长期公钥未知 → 显式 null。
                    // 真实公钥由 GroupChatService 在需要包装群密钥时主动握手取回。
                    PublicKey = null,
                    State = Core.Enums.PeerState.Online
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "加载静态对端失败");
        }
    }

    /// <summary>
    /// 获取本机局域网IP地址
    /// </summary>
    private static IPAddress GetLocalIPAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            var endPoint = (IPEndPoint)socket.LocalEndPoint!;
            return endPoint.Address;
        }
        catch
        {
            return IPAddress.Loopback;
        }
    }

    private static string[] LoadKnownPeers()
    {
        try
        {
            var path = Core.Extensions.DataPath.GetPath("peers.txt");
            if (File.Exists(path))
                return File.ReadAllLines(path)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith('#'))
                    .ToArray();
        }
        catch { }
        return [];
    }

    private static void SaveKnownPeers(string[] peers)
    {
        try
        {
            var path = Core.Extensions.DataPath.GetPath("peers.txt");
            File.WriteAllLines(path, peers);
        }
        catch { }
    }
}
