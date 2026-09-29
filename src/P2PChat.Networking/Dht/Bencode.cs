using System.Text;

namespace P2PChat.Networking.Dht;

/// <summary>
/// Bencode 编解码器 — Mainline DHT KRPC 协议使用
/// 支持: 字符串、整数、列表、字典
/// </summary>
public static class Bencode
{
    // ─── 编码 ───

    public static byte[] Encode(object value) => value switch
    {
        string s => Encoding.UTF8.GetBytes($"{s.Length}:{s}"),
        byte[] b => EncodeString(b),
        int i => Encoding.UTF8.GetBytes($"i{i}e"),
        long l => Encoding.UTF8.GetBytes($"i{l}e"),
        List<object> list => EncodeList(list),
        Dictionary<string, object> dict => EncodeDict(dict),
        _ => throw new ArgumentException($"不支持的Bencode类型: {value.GetType()}")
    };

    private static byte[] EncodeString(byte[] bytes)
    {
        var header = Encoding.UTF8.GetBytes($"{bytes.Length}:");
        var result = new byte[header.Length + bytes.Length];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        Buffer.BlockCopy(bytes, 0, result, header.Length, bytes.Length);
        return result;
    }

    private static byte[] EncodeList(List<object> list)
    {
        var parts = new List<byte> { (byte)'l' };
        foreach (var item in list)
            parts.AddRange(Encode(item));
        parts.Add((byte)'e');
        return parts.ToArray();
    }

    private static byte[] EncodeDict(Dictionary<string, object> dict)
    {
        var parts = new List<byte> { (byte)'d' };
        // Mainline DHT要求按key字典序排列
        foreach (var key in dict.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            parts.AddRange(Encode(key));
            parts.AddRange(Encode(dict[key]));
        }
        parts.Add((byte)'e');
        return parts.ToArray();
    }

    // ─── 解码 ───

    public static object Decode(byte[] data) { int c = 0; return Decode(data, ref c); }
    public static object Decode(ReadOnlySpan<byte> data) { int c = 0; return Decode(data, ref c); }

    private static object Decode(ReadOnlySpan<byte> data, ref int consumed)
    {
        if (data.Length == 0) throw new ArgumentException("空数据");
        consumed = 0;

        return data[0] switch
        {
            (byte)'i' => DecodeInt(data, ref consumed),
            (byte)'l' => DecodeList(data, ref consumed),
            (byte)'d' => DecodeDict(data, ref consumed),
            >= (byte)'0' and <= (byte)'9' => DecodeString(data, ref consumed),
            _ => throw new ArgumentException($"无效Bencode前缀: {(char)data[0]}")
        };
    }

    private static object DecodeInt(ReadOnlySpan<byte> data, ref int consumed)
    {
        var end = data.IndexOf((byte)'e');
        if (end < 0) throw new ArgumentException("整数未闭合");
        var numStr = Encoding.UTF8.GetString(data[1..end]);
        consumed = end + 1;
        return long.Parse(numStr);
    }

    private static byte[] DecodeString(ReadOnlySpan<byte> data, ref int consumed)
    {
        var colon = data.IndexOf((byte)':');
        if (colon < 0) throw new ArgumentException("字符串无冒号分隔");
        var lenStr = Encoding.UTF8.GetString(data[..colon]);
        var len = int.Parse(lenStr);
        consumed = colon + 1 + len;
        return data.Slice(colon + 1, len).ToArray();
    }

    private static List<object> DecodeList(ReadOnlySpan<byte> data, ref int consumed)
    {
        var list = new List<object>();
        var pos = 1; // 跳过 'l'
        while (pos < data.Length && data[pos] != (byte)'e')
        {
            int itemConsumed = 0;
            var item = Decode(data[pos..], ref itemConsumed);
            list.Add(item);
            pos += itemConsumed;
        }
        if (pos < data.Length && data[pos] == (byte)'e') pos++;
        consumed = pos;
        return list;
    }

    private static Dictionary<string, object> DecodeDict(ReadOnlySpan<byte> data, ref int consumed)
    {
        var dict = new Dictionary<string, object>();
        var pos = 1; // 跳过 'd'
        while (pos < data.Length && data[pos] != (byte)'e')
        {
            int keyConsumed = 0, valConsumed = 0;
            var key = DecodeString(data[pos..], ref keyConsumed);
            pos += keyConsumed;
            var value = Decode(data[pos..], ref valConsumed);
            pos += valConsumed;
            var keyStr = Encoding.UTF8.GetString((byte[])key);
            dict[keyStr] = value;
        }
        if (pos < data.Length && data[pos] == (byte)'e') pos++;
        consumed = pos;
        return dict;
    }

    // ─── 辅助方法 ───

    public static byte B2B(object v) => Convert.ToByte((long)v);
    public static int B2I(object v) => Convert.ToInt32((long)v);

    /// <summary>
    /// 从bencoded字典中安全获取字符串键的值
    /// </summary>
    public static T? Get<T>(Dictionary<string, object> dict, string key) where T : class
    {
        return dict.TryGetValue(key, out var val) ? val as T : null;
    }

    /// <summary>
    /// 将紧凑节点信息字符串(26字节)解析为节点列表
    /// 格式: [20字节NodeId][4字节IP][2字节Port BigEndian]
    /// </summary>
    public static List<(byte[] NodeId, System.Net.IPAddress Ip, int Port)> ParseCompactNodes(byte[] nodesData)
    {
        var result = new List<(byte[], System.Net.IPAddress, int)>();
        const int nodeSize = 26;
        for (int i = 0; i + nodeSize <= nodesData.Length; i += nodeSize)
        {
            var nodeId = new byte[20];
            Buffer.BlockCopy(nodesData, i, nodeId, 0, 20);
            var ipBytes = new byte[4];
            Buffer.BlockCopy(nodesData, i + 20, ipBytes, 0, 4);
            var ip = new System.Net.IPAddress(ipBytes);
            var port = (nodesData[i + 24] << 8) | nodesData[i + 25];
            result.Add((nodeId, ip, port));
        }
        return result;
    }

    /// <summary>
    /// 将节点信息编码为紧凑格式 (26字节)
    /// </summary>
    public static byte[] EncodeCompactNode(byte[] nodeId, System.Net.IPAddress ip, int port)
    {
        var result = new byte[26];
        Buffer.BlockCopy(nodeId, 0, result, 0, 20);
        var ipBytes = ip.GetAddressBytes();
        Buffer.BlockCopy(ipBytes, 0, result, 20, 4);
        result[24] = (byte)(port >> 8);
        result[25] = (byte)port;
        return result;
    }

    /// <summary>
    /// 将 P2PChat 扩展紧凑对端格式（26 字节/条目）解析为列表。
    /// 格式：<c>[20B NodeId][4B IPv4][2B Port BE]</c>，与 <see cref="ParseCompactNodes"/> 相同的 26 字节布局，
    /// 但语义是 <c>get_peers</c> 应答中的 <c>p2pc_peers</c> 字段（携带宣告方的 NodeId + 宣告时使用的 TCP 端点）。
    /// </summary>
    /// <remarks>
    /// 为何不用 BitTorrent 标准的 6 字节 <c>values</c>：标准格式只携带 IP+port，
    /// 接收方拿不到宣告方的 NodeId，无法与「我正在找的 NodeId」匹配。
    /// P2PChat 节点之间使用本格式；公网节点不返回 <c>p2pc_peers</c>，视为未发现。
    /// </remarks>
    public static List<(byte[] NodeId, System.Net.IPAddress Ip, int Port)> ParseCompactPeers26(byte[] peersData)
    {
        var result = new List<(byte[], System.Net.IPAddress, int)>();
        if (peersData is null) return result;
        const int entrySize = 26;
        for (int i = 0; i + entrySize <= peersData.Length; i += entrySize)
        {
            var nodeId = new byte[20];
            Buffer.BlockCopy(peersData, i, nodeId, 0, 20);
            var ipBytes = new byte[4];
            Buffer.BlockCopy(peersData, i + 20, ipBytes, 0, 4);
            var ip = new System.Net.IPAddress(ipBytes);
            var port = (peersData[i + 24] << 8) | peersData[i + 25];
            result.Add((nodeId, ip, port));
        }
        return result;
    }

    // ─── p2pc_peers6：IPv6 版扩展条目 ─────────────────────────────────────────
    //
    // 为什么**另开一个字段**而不是给 26 字节条目加宽度标记位：
    // 决定性的理由是**老节点的失败模式**。bencode 字典里老节点找不到 `p2pc_peers6`
    // ⇒ 它只会走 `values` 兜底而**得不到任何条目** ⇒ 干净降级。
    // 而「加标记位」那条路里，老节点遇到不认识的宽度会**按 26 字节错位切分**
    // ⇒ 把新格式解析成错条目。**误解析远比缺失危险**（同阶段 3.2 EnvelopeCodec 事故）。
    // 而 `p2pc_peers`（26B，IPv4）保持**字节级不变**，本就不需要任何向后兼容处理。

    /// <summary>IPv4 扩展条目长度：<c>[20B NodeId][4B IPv4][2B Port]</c>。</summary>
    public const int CompactPeerV4Size = 26;

    /// <summary>IPv6 扩展条目长度：<c>[20B NodeId][16B IPv6][2B Port]</c>。</summary>
    public const int CompactPeerV6Size = 38;

    /// <summary>BitTorrent 标准 <c>values</c> 条目长度：<c>[4B IPv4][2B Port]</c>，**不含 NodeId**。</summary>
    public const int CompactPeerValueSize = 6;

    /// <summary>
    /// 解析 <c>p2pc_peers6</c>（IPv6 扩展条目，38 字节/条）。
    /// <para>
    /// 与 <see cref="ParseCompactPeers26"/> 的唯一差别是地址字段宽度：16 字节而非 4。
    /// 端口同样是大端 2 字节，NodeId 同样在条目开头 —— 布局刻意与 26B 版保持平行，
    /// 便于对照与将来的独立演进。
    /// </para>
    /// </summary>
    public static List<(byte[] NodeId, System.Net.IPAddress Ip, int Port)> ParseCompactPeers38(byte[] peersData)
    {
        var result = new List<(byte[], System.Net.IPAddress, int)>();
        if (peersData is null) return result;
        const int entrySize = 38;
        for (int i = 0; i + entrySize <= peersData.Length; i += entrySize)
        {
            var nodeId = new byte[20];
            Buffer.BlockCopy(peersData, i, nodeId, 0, 20);
            var ipBytes = new byte[16];
            Buffer.BlockCopy(peersData, i + 20, ipBytes, 0, 16);
            var port = (peersData[i + 36] << 8) | peersData[i + 37];
            result.Add((nodeId, new System.Net.IPAddress(ipBytes), port));
        }
        return result;
    }

    /// <summary>
    /// 解析 BitTorrent 标准的 6 字节 <c>values</c> 条目（<c>[4B IPv4][2B Port]</c>）。
    /// <para>
    /// <b>它不含 NodeId</b>，所以返回的 <c>NodeId</c> 一律为 <c>null</c>：
    /// 标准格式无法让接收方把条目映射回具体节点。调用方必须自己决定怎么补 NodeId
    /// （本项目用「响应者自己的 NodeId」，见 <c>MainlineDhtService</c> 的 <c>get_peers</c> 分支），
    /// <b>不要</b>在这里替调用方编一个。
    /// </para>
    /// <para>
    /// 此前该路径把 6 字节数据交给了按 26 字节切分的解析器（形参被忽略），
    /// 切出来的是错位条目。之所以长期没暴露，是因为那条路径产出的结果
    /// 无论如何都过不了「NodeId 必须等于查询目标」那一关而被下游丢弃 ——
    /// <b>被掩盖的错位解析仍然是错位解析</b>，IPv6 一落地它就会开始产出看似合法的结果。
    /// </para>
    /// </summary>
    public static List<(byte[]? NodeId, System.Net.IPAddress Ip, int Port)> ParseCompactValues6(byte[] valuesData)
    {
        var result = new List<(byte[]?, System.Net.IPAddress, int)>();
        if (valuesData is null) return result;
        const int entrySize = 6;
        for (int i = 0; i + entrySize <= valuesData.Length; i += entrySize)
        {
            var ipBytes = new byte[4];
            Buffer.BlockCopy(valuesData, i, ipBytes, 0, 4);
            var port = (valuesData[i + 4] << 8) | valuesData[i + 5];
            result.Add((null, new System.Net.IPAddress(ipBytes), port));
        }
        return result;
    }

    /// <summary>
    /// 把一条对端条目序列化成对应地址族的扩展格式字节。
    /// </summary>
    /// <param name="nodeId">宣告方 NodeId（20 字节）。</param>
    /// <param name="endPoint">该对端的端点；按其 <see cref="System.Net.Sockets.AddressFamily"/> 选格式。</param>
    /// <exception cref="ArgumentException">
    /// 端点地址族既非 IPv4 也非 IPv6 时抛出。
    /// <para>
    /// <b>刻意抛异常，而不是「截断成 4 字节」或「跳过」。</b>
    /// 旧实现用固定 4 字节 <c>BlockCopy</c> 拷地址，对 IPv6 会**静默截断成一个语义上
    /// 毫不相干的 IPv4 地址** —— 不抛异常、不记日志，下游无从察觉，地址表会开始
    /// 对外播报错误地址。宁可在这里炸掉，也不要静默产出错误地址。
    /// </para>
    /// </exception>
    public static byte[] SerializeCompactPeer(byte[] nodeId, System.Net.IPEndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentNullException.ThrowIfNull(endPoint);
        if (nodeId.Length != 20)
            throw new ArgumentException($"NodeId 必须是 20 字节，实际 {nodeId.Length}", nameof(nodeId));

        var address = endPoint.Address;
        int addressSize = address.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => 4,
            System.Net.Sockets.AddressFamily.InterNetworkV6 => 16,
            _ => throw new ArgumentException(
                $"不支持的地址族 {address.AddressFamily} —— 宁可不发，也不要静默产出错误地址", nameof(endPoint))
        };

        var entry = new byte[20 + addressSize + 2];
        Buffer.BlockCopy(nodeId, 0, entry, 0, 20);
        var ipBytes = address.GetAddressBytes();
        Buffer.BlockCopy(ipBytes, 0, entry, 20, addressSize);
        var port = endPoint.Port;
        entry[entry.Length - 2] = (byte)(port >> 8);
        entry[entry.Length - 1] = (byte)port;
        return entry;
    }
}
