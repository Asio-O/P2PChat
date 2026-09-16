using System.Diagnostics;
using System.Security.Cryptography;

namespace P2PChat.Core.Models;

/// <summary>
/// 160位节点ID (SHA-1), 用于Kademlia DHT寻址
/// </summary>
[DebuggerDisplay("{ToHexString()}")]
public readonly record struct NodeId
{
    public static readonly int Size = 20; // 160 bits
    private readonly byte[] _value;

    public NodeId(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != Size)
            throw new ArgumentException($"节点ID必须是{Size}字节", nameof(value));
        _value = new byte[Size];
        Array.Copy(value, _value, Size);
    }

    /// <summary>
    /// 从公钥生成节点ID (SHA-1哈希)
    /// </summary>
    public static NodeId FromPublicKey(byte[] publicKey)
    {
        var hash = SHA1.HashData(publicKey);
        return new NodeId(hash);
    }

    /// <summary>
    /// 生成随机节点ID
    /// </summary>
    public static NodeId CreateRandom()
    {
        var bytes = new byte[Size];
        RandomNumberGenerator.Fill(bytes);
        return new NodeId(bytes);
    }

    /// <summary>
    /// 计算与目标节点ID的XOR距离
    /// </summary>
    public byte[] XorDistanceTo(NodeId other)
    {
        var result = new byte[Size];
        for (int i = 0; i < Size; i++)
            result[i] = (byte)(_value[i] ^ other._value[i]);
        return result;
    }

    /// <summary>
    /// 返回前缀长度 (XOR后相同前导bit数)，用于桶索引计算
    /// </summary>
    public int CommonPrefixLength(NodeId other)
    {
        var xor = XorDistanceTo(other);
        int bits = 0;
        for (int i = 0; i < Size; i++)
        {
            if (xor[i] == 0) { bits += 8; continue; }
            int mask = 0x80;
            while ((xor[i] & mask) == 0 && mask > 0) { bits++; mask >>= 1; }
            break;
        }
        return bits;
    }

    public byte[] ToByteArray()
    {
        var copy = new byte[Size];
        Array.Copy(_value, copy, Size);
        return copy;
    }

    public string ToHexString() => Convert.ToHexString(_value).ToLowerInvariant();

    public override int GetHashCode() => BitConverter.ToInt32(_value, 0);
    public bool Equals(NodeId other) => _value.AsSpan().SequenceEqual(other._value);
}
