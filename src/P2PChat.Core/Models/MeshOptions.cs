namespace P2PChat.Core.Models;

/// <summary>
/// mesh 拓扑的可部署性配置。按「配置即契约」，这类影响部署行为的选择必须能从配置文件改，
/// 不允许硬编码 —— 见 2026-10-09-mesh-topology-and-flooding。
/// </summary>
public sealed record MeshOptions
{
    /// <summary>
    /// mesh 泛洪开关（P2PChat:Mesh:FloodingEnabled，默认开）。关闭时入站消息只做本地处理，
    /// 数据平面退回「按需直连、无转发」的旧行为 —— 行为对照与回退阀，不是安全边界。
    /// </summary>
    public bool FloodingEnabled { get; init; } = true;

    /// <summary>
    /// mesh 连接维护循环的轮间隔秒数（P2PChat:Mesh:MaintainIntervalSeconds，默认 30）。
    /// 每轮对「已知但无活跃连接」的节点补建连接；必须为正。
    /// </summary>
    public int MaintainIntervalSeconds { get; init; } = 30;
}
