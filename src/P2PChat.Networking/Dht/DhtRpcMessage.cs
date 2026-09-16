using P2PChat.Core.Enums;
using P2PChat.Core.Models;

namespace P2PChat.Networking.Dht;

/// <summary>
/// DHT RPC消息 — Kademlia协议二进制消息
/// 二进制格式: [Version:1B][Type:1B][RequestId:4B BE][SenderId:20B][PayloadLen:2B BE][Payload:可变]
/// </summary>
public record DhtRpcMessage
{
    public byte Version { get; init; } = 2;
    public DhtMessageType MessageType { get; init; }
    public uint RequestId { get; init; }
    public required byte[] SenderId { get; init; }
    public required byte[] Payload { get; init; }

    /// <summary>
    /// 序列化为二进制格式
    /// </summary>
    public byte[] ToBytes()
    {
        var payloadLen = (ushort)Payload.Length;
        var totalLen = 1 + 1 + 4 + NodeId.Size + 2 + Payload.Length;
        var result = new byte[totalLen];
        int offset = 0;

        result[offset++] = Version;
        result[offset++] = (byte)MessageType;
        result[offset++] = (byte)(RequestId >> 24);
        result[offset++] = (byte)(RequestId >> 16);
        result[offset++] = (byte)(RequestId >> 8);
        result[offset++] = (byte)RequestId;
        Array.Copy(SenderId, 0, result, offset, NodeId.Size);
        offset += NodeId.Size;
        result[offset++] = (byte)(payloadLen >> 8);
        result[offset++] = (byte)payloadLen;
        Array.Copy(Payload, 0, result, offset, Payload.Length);

        return result;
    }

    /// <summary>
    /// 从二进制格式反序列化
    /// </summary>
    public static DhtRpcMessage FromBytes(byte[] data)
    {
        if (data.Length < 28) throw new ArgumentException("数据太小, 不是合法的DHT RPC消息");

        int offset = 0;
        var version = data[offset++];
        var type = (DhtMessageType)data[offset++];
        var requestId = (uint)((data[offset++] << 24) | (data[offset++] << 16) | (data[offset++] << 8) | data[offset++]);
        var senderId = new byte[NodeId.Size];
        Array.Copy(data, offset, senderId, 0, NodeId.Size);
        offset += NodeId.Size;
        var payloadLen = (ushort)((data[offset++] << 8) | data[offset++]);
        var payload = new byte[payloadLen];
        Array.Copy(data, offset, payload, 0, payloadLen);

        return new DhtRpcMessage
        {
            Version = version,
            MessageType = type,
            RequestId = requestId,
            SenderId = senderId,
            Payload = payload
        };
    }
}
