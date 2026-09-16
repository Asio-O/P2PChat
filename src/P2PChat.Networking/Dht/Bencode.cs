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
}
