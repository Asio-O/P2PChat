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
        var localNode = new NodeInfo
        {
            NodeId = localNodeId,
            EndPoint = new IPEndPoint(GetLocalIPAddress(), actualTcpPort),
            PublicKey = identity.PublicKey,
            State = Core.Enums.PeerState.Online
        };

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
        services.AddSingleton<IMessageRouter, MessageRouter>();
        services.AddSingleton<PrivateMessageHandler>();
        services.AddSingleton<GroupMessageHandler>();
        services.AddSingleton<KeyExchangeHandler>();
        services.AddSingleton<GroupInviteHandler>();
        services.AddSingleton<GroupNotifyHandler>();
        services.AddSingleton<IChatService, ChatService>();
        services.AddSingleton<IGroupChatService, GroupChatService>();
        services.AddSingleton<IContactService, ContactService>();

        // FileTransfer层
        services.AddSingleton<IFileTransferService,
            P2PChat.FileTransfer.Services.FileTransferService>();

        // UI层
        services.AddSingleton<P2PChatTui>();

        // 6. 构建最终提供器
        var provider = services.BuildServiceProvider();

        try
        {
            // 启动DHT引导 (如果有引导节点)
            var dhtService = provider.GetRequiredService<IDhtService>();
            if (bootstrapEndpoints.Count > 0)
                _ = dhtService.BootstrapAsync(CancellationToken.None);

            var dht = (MainlineDhtService)dhtService;
            var dhtCts = new CancellationTokenSource();
            _ = dht.StartReceivingAsync(dhtCts.Token);

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
