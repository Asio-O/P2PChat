namespace P2PChat.Core.Abstractions;

/// <summary>
/// 序列化器 — MessagePack实现
/// </summary>
public interface ISerializer
{
    /// <summary>
    /// 将对象序列化为字节数组
    /// </summary>
    byte[] Serialize<T>(T obj);

    /// <summary>
    /// 从字节数组反序列化为对象
    /// </summary>
    T Deserialize<T>(byte[] data);

    /// <summary>
    /// 从字节序列反序列化为对象
    /// </summary>
    T Deserialize<T>(ReadOnlySpan<byte> data);

    /// <summary>
    /// 从流中反序列化
    /// </summary>
    T Deserialize<T>(Stream stream);
}
