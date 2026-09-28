using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;

namespace P2PChat.Networking.Transport;

/// <summary>
/// UPnP IGD 端口映射客户端 —— 通过裸 HTTP/1.1 over SSDP + SOAP 实现，
/// 不依赖 COM / Windows NATUPnP，与 Native-AOT 兼容。
/// </summary>
/// <remarks>
/// <para>
/// 协议路径：
/// </para>
/// <list type="number">
///   <item>向多播地址 239.255.255.250:1900 发 M-SEARCH，等待 ST = <c>urn:schemas-upnp-org:device:InternetGatewayDevice:1</c> 的回应；</item>
///   <item>从回应中取 <c>Location</c> HTTP header，TCP 连接该 URL；</item>
///   <item>向该 URL 发 SOAP POST：<c>AddPortMapping</c>（TCP 与 UDP 各一次，公网与内网同号）；</item>
///   <item>再发 <c>GetExternalIPAddress</c> 取本机公网入口 IP，组装 <see cref="UpnpMapping"/> 返回。</item>
/// </list>
/// <para>
/// 所有失败模式（无 UPnP / SSDP 超时 / SOAP 失败 / 解析失败）一律记 Debug 日志并返回 null，
/// 不抛、不阻塞。调用方在 null 时应让用户改走 <c>/connect</c> 或 <c>/add</c> 路径。
/// </para>
/// </remarks>
public sealed class UpnpClient : IUpnpClient
{
    private const string SsdpMulticastIp = "239.255.255.250";
    private const int SsdpMulticastPort = 1900;
    private const string SearchTarget = "urn:schemas-upnp-org:device:InternetGatewayDevice:1";
    private const string IgdServiceType = "urn:schemas-upnp-org:service:WANIPConn:1";

    private static readonly TimeSpan DiscoverTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SoapTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<UpnpClient> _logger;
    private readonly string _internalClientIp;

    public UpnpClient(ILogger<UpnpClient> logger, string internalClientIp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(internalClientIp);
        _logger = logger;
        _internalClientIp = internalClientIp;
    }

    /// <inheritdoc />
    public async Task<UpnpMapping?> TryMapAsync(int port, int leaseDurationSeconds = 3600, CancellationToken ct = default)
    {
        if (port <= 0 || port > 65535)
        {
            _logger.LogDebug("UPnP 端口非法: {Port}", port);
            return null;
        }

        Uri? gateway;
        try
        {
            gateway = await DiscoverGatewayAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UPnP SSDP 发现失败");
            return null;
        }
        if (gateway is null)
        {
            _logger.LogDebug("UPnP 路由器未发现（SSDP 无响应）");
            return null;
        }

        // 为 TCP 与 UDP 各申请一次 AddPortMapping。任一失败即视为整体失败；不影响 TUI 提示。
        var tcpOk = await AddPortMappingAsync(gateway, port, "TCP", leaseDurationSeconds, ct);
        var udpOk = await AddPortMappingAsync(gateway, port, "UDP", leaseDurationSeconds, ct);
        if (!tcpOk || !udpOk)
        {
            _logger.LogDebug("UPnP 端口映射失败: TCP={Tcp}, UDP={Udp}", tcpOk, udpOk);
            // 部分成功时尝试把成功的协议撤掉，避免留半边映射。
            if (tcpOk) await DeletePortMappingAsync(gateway, port, "TCP", ct);
            if (udpOk) await DeletePortMappingAsync(gateway, port, "UDP", ct);
            return null;
        }

        var externalIp = await GetExternalIpAddressAsync(gateway, ct);
        if (externalIp is null)
        {
            // 映射已经在路由器上生效，但拿不到公网 IP —— 无法通过 announce_peer 对外宣告，
            // 这条映射对本程序「不可见、不可用、也不受 RemoveAsync 管辖」（调用方只拿到 null，
            // Program.cs 的退出路径会跳过 RemoveAsync）。
            // 留着它 = 在用户路由器上开一个我们既不宣告、又在退出时不撤销的洞，直到租约过期，
            // 同时 UI 还会显示「无 UPnP」—— 与事实相反。
            // 因此这里主动回滚：宁可没有映射，也不要留下无法撤销的映射。
            _logger.LogDebug("UPnP 端口映射已建立但 GetExternalIPAddress 失败 —— 回滚 TCP/UDP 映射，视为未映射");
            await DeletePortMappingAsync(gateway, port, "TCP", ct);
            await DeletePortMappingAsync(gateway, port, "UDP", ct);
            return null;
        }

        var mapping = new UpnpMapping(
            ExternalEndPoint: new IPEndPoint(externalIp, port),
            Gateway: gateway,
            LeaseDuration: TimeSpan.FromSeconds(leaseDurationSeconds));
        _logger.LogInformation("UPnP 映射成功: 内网 {Internal}:{Port} → 公网 {External}:{Port} (网关 {Gateway})",
            _internalClientIp, port, externalIp, port, gateway);
        return mapping;
    }

    /// <inheritdoc />
    public async Task RemoveAsync(int port, CancellationToken ct = default)
    {
        Uri? gateway;
        try
        {
            gateway = await DiscoverGatewayAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UPnP 删除阶段 SSDP 失败（可能路由器已下线）");
            return;
        }
        if (gateway is null) return;

        // 与申请配对删 TCP + UDP。失败仅记日志 —— 进程退出阶段无需重试。
        await DeletePortMappingAsync(gateway, port, "TCP", ct);
        await DeletePortMappingAsync(gateway, port, "UDP", ct);
    }

    #region SSDP 发现

    /// <summary>
    /// M-SEARCH → 收响应 → 取第一个 <c>Location</c> URL。
    /// </summary>
    private async Task<Uri?> DiscoverGatewayAsync(CancellationToken ct)
    {
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveTimeout = (int)DiscoverTimeout.TotalMilliseconds
        };
        udp.Bind(new IPEndPoint(IPAddress.Any, 0));

        var searchPacket = BuildMsSearchPacket();
        var bytesSent = await SendToAsync(udp, searchPacket, ct);
        if (bytesSent == 0)
        {
            _logger.LogDebug("UPnP SSDP 发送失败");
            return null;
        }

        var buffer = new byte[2048];
        var deadline = DateTime.UtcNow + DiscoverTimeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
                var received = udp.ReceiveFrom(buffer, ref remoteEp);
                var text = Encoding.ASCII.GetString(buffer, 0, received);
                var location = ExtractHttpHeader(text, "Location");
                if (location is null) continue;
                if (Uri.TryCreate(location, UriKind.Absolute, out var uri))
                    return uri;
                _logger.LogDebug("UPnP 响应 Location 不是合法 URL: {Location}", location);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                // 自然超时：未收到响应，视为无 UPnP。
                return null;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "UPnP SSDP 接收异常");
                return null;
            }
        }
        return null;
    }

    private static byte[] BuildMsSearchPacket()
    {
        // 标准 SSDP M-SEARCH：host = 239.255.255.250:1900；MAN = "ssdp:discover"；ST = IGD；MX = 2 秒。
        var sb = new StringBuilder()
            .Append("M-SEARCH * HTTP/1.1\r\n")
            .Append("HOST: 239.255.255.250:1900\r\n")
            .Append("MAN: \"ssdp:discover\"\r\n")
            .Append("MX: 2\r\n")
            .Append("ST: ").Append(SearchTarget).Append("\r\n")
            .Append("\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static async Task<int> SendToAsync(Socket socket, byte[] data, CancellationToken ct)
    {
        try
        {
            return await socket.SendToAsync(data, SocketFlags.None,
                new IPEndPoint(IPAddress.Parse(SsdpMulticastIp), SsdpMulticastPort), ct);
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode == SocketError.Success ? 0 : 0;
        }
    }

    private static string? ExtractHttpHeader(string response, string headerName)
    {
        // 极简 HTTP header 解析：按行扫，找形如 "Location: ..." 的行。
        // SSDP 响应里 header 大小写不敏感。
        foreach (var rawLine in response.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimStart();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            if (!string.Equals(name, headerName, StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[(colon + 1)..].Trim();
            return value.Length == 0 ? null : value;
        }
        return null;
    }

    #endregion

    #region SOAP

    private async Task<bool> AddPortMappingAsync(Uri gateway, int port, string protocol, int leaseSeconds, CancellationToken ct)
    {
        var body = BuildSoapBody("AddPortMapping",
            ("NewRemoteHost", ""),
            ("NewExternalPort", port.ToString()),
            ("NewProtocol", protocol),
            ("NewInternalPort", port.ToString()),
            ("NewInternalClient", _internalClientIp),
            ("NewEnabled", "1"),
            ("NewPortMappingDescription", $"P2PChat-{protocol}"),
            ("NewLeaseDuration", leaseSeconds.ToString()));
        var response = await SendSoapAsync(gateway, body, ct);
        return response is not null;
    }

    private async Task<bool> DeletePortMappingAsync(Uri gateway, int port, string protocol, CancellationToken ct)
    {
        var body = BuildSoapBody("DeletePortMapping",
            ("NewRemoteHost", ""),
            ("NewExternalPort", port.ToString()),
            ("NewProtocol", protocol));
        var response = await SendSoapAsync(gateway, body, ct);
        return response is not null;
    }

    private async Task<IPAddress?> GetExternalIpAddressAsync(Uri gateway, CancellationToken ct)
    {
        var body = BuildSoapBody("GetExternalIPAddress",
            ("NewExternalIPAddress", ""));
        var response = await SendSoapAsync(gateway, body, ct);
        if (response is null) return null;
        // 响应是 SOAP XML —— 仅取 <NewExternalIPAddress>...</NewExternalIPAddress> 之间的文本，
        // 走 string.Substring 而非 XML 解析，避免引入 XmlDocument / LINQ-to-XML（AOT 友好）。
        var tag = "NewExternalIPAddress";
        var open = response.IndexOf($"<{tag}>", StringComparison.Ordinal);
        if (open < 0) return null;
        var close = response.IndexOf($"</{tag}>", StringComparison.Ordinal);
        if (close <= open) return null;
        var ipText = response[(open + tag.Length + 2)..close];
        return IPAddress.TryParse(ipText, out var ip) ? ip : null;
    }

    private static string BuildSoapBody(string action, params (string Name, string Value)[] args)
    {
        var sb = new StringBuilder()
            .Append("<?xml version=\"1.0\"?>\n")
            .Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" ")
            .Append("s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\n")
            .Append("<s:Body><u:").Append(action).Append(" xmlns:u=\"").Append(IgdServiceType).Append("\">\n");
        foreach (var (name, value) in args)
        {
            if (value.Length == 0) continue;
            sb.Append('<').Append(name).Append('>').Append(value).Append("</").Append(name).Append(">\n");
        }
        sb.Append("</u:").Append(action).Append("></s:Body></s:Envelope>");
        return sb.ToString();
    }

    private async Task<string?> SendSoapAsync(Uri gateway, string body, CancellationToken ct)
    {
        // 从 body 中提取 <u:ActionName ...> 的 ActionName 作为 SOAPAction。
        var actionOpen = body.IndexOf("<u:", StringComparison.Ordinal);
        if (actionOpen < 0)
        {
            _logger.LogDebug("UPnP SOAP body 无 <u:...> action 标签");
            return null;
        }
        var actionEnd = body.IndexOfAny(new[] { ' ', '>' }, actionOpen + 3);
        if (actionEnd < 0) return null;
        var actionName = body.Substring(actionOpen + 3, actionEnd - actionOpen - 3);
        var soapAction = $"{IgdServiceType}#{actionName}";

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var request = new StringBuilder()
            .Append("POST ").Append(gateway.AbsolutePath).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(gateway.Host).Append(':').Append(gateway.Port).Append("\r\n")
            .Append("Content-Type: text/xml; charset=\"utf-8\"\r\n")
            .Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n")
            .Append("Connection: close\r\n")
            .Append("SOAPAction: \"").Append(soapAction).Append("\"\r\n\r\n");
        var requestBytes = Encoding.ASCII.GetBytes(request.ToString());

        using var tcp = new TcpClient { ReceiveTimeout = (int)SoapTimeout.TotalMilliseconds };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SoapTimeout);
        try
        {
            await tcp.ConnectAsync(gateway.Host, gateway.Port, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UPnP SOAP 连接失败: {Host}:{Port}", gateway.Host, gateway.Port);
            return null;
        }
        try
        {
            var stream = tcp.GetStream();
            await stream.WriteAsync(requestBytes, cts.Token);
            await stream.WriteAsync(bodyBytes, cts.Token);
            await stream.FlushAsync(cts.Token);

            var responseBuffer = new List<byte>(4096);
            var chunk = new byte[4096];
            while (true)
            {
                int n;
                try { n = await stream.ReadAsync(chunk, cts.Token); }
                catch { break; }
                if (n <= 0) break;
                responseBuffer.AddRange(chunk.AsSpan(0, n).ToArray());
                if (responseBuffer.Count > 65536) break;   // 简单上限保护
            }

            var raw = Encoding.UTF8.GetString(responseBuffer.ToArray());
            // HTTP/1.1 status line: "HTTP/1.1 200 OK" —— 200 即视为成功；5xx 失败；4xx 视为失败。
            var statusLine = raw.Split("\r\n", 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (!statusLine.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("UPnP SOAP 响应无 HTTP 头: {Preview}", statusLine);
                return null;
            }
            var codeIdx = statusLine.IndexOf(' ');
            if (codeIdx < 0) return null;
            var codeText = statusLine[(codeIdx + 1)..].TrimStart().Split(' ')[0];
            if (!int.TryParse(codeText, out var code)) return null;
            return code is >= 200 and < 300 ? raw : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UPnP SOAP 收发失败: {Host}:{Port}", gateway.Host, gateway.Port);
            return null;
        }
    }

    #endregion
}