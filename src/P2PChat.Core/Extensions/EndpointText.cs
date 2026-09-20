using System.Net;

namespace P2PChat.Core.Extensions;

/// <summary>
/// <c>ip:port</c> 文本与 <see cref="IPEndPoint"/> 之间的转换。
/// 用于手工添加联系人时显式指定端点（<c>/add &lt;节点ID&gt; &lt;ip:port&gt;</c>）。
/// </summary>
/// <remarks>
/// 只接受**字面 IP**（IPv4 / IPv6），不做 DNS 解析 —— 联系人端点是直连地址，
/// 留一个域名在此处会引入每次连接的解析延迟与不确定性。引导节点走另一条路径（支持域名）。
/// </remarks>
public static class EndpointText
{
    /// <summary>把 <see cref="IPEndPoint"/> 格式化为 <c>ip:port</c>。</summary>
    public static string Format(IPEndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        return $"{endPoint.Address}:{endPoint.Port}";
    }

    /// <summary>
    /// 解析 <c>ip:port</c>。端口须落在 1..65535。
    /// IPv6 字面量请写成 <c>[::1]:5000</c> 形式。
    /// </summary>
    public static bool TryParse(string? text, out IPEndPoint endPoint)
    {
        endPoint = null!;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.Trim();
        string host;
        string portText;

        // IPv6 字面量带方括号：[::1]:5000
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close <= 1)
                return false;
            host = value[1..close];
            var rest = value[(close + 1)..];
            if (!rest.StartsWith(':'))
                return false;
            portText = rest[1..];
        }
        else
        {
            var colon = value.LastIndexOf(':');
            if (colon <= 0 || colon == value.Length - 1)
                return false;
            host = value[..colon];
            portText = value[(colon + 1)..];
        }

        if (!IPAddress.TryParse(host, out var address))
            return false;
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
            return false;

        endPoint = new IPEndPoint(address, port);
        return true;
    }
}
