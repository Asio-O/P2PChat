using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Models;

namespace P2PChat.Chat.Routing;

/// <summary>
/// 基于「时间新鲜度 + MessageId 去重」的重放防护，两层串联。
/// <para>
/// <b>第一层 时间新鲜度（粗筛，兜住陈旧重放）</b>：<see cref="MessageEnvelope.Timestamp"/> 必须落在
/// 闭区间 <c>[now - maxAge, now + maxFutureSkew]</c> 内。默认 <c>maxAge = 1 小时</c>、
/// <c>maxFutureSkew = 5 分钟</c>。
/// </para>
/// <para>
/// <b>第二层 MessageId 去重（细筛，兜住窗口内的重放）</b>：攻击者可以把一条**新鲜**信封在
/// 一小时内狂放上千次，第一层拦不住。<see cref="MessageEnvelope.MessageId"/> 是已被签名覆盖的
/// 16 字节 Guid，天然唯一，按对端 NodeId 分桶记住最近 N 条（默认 1024）已接受的 MessageId。
/// </para>
/// <para>
/// <b>为什么用 MessageId 而不是序号窗口</b>：<see cref="MessageEnvelope.SequenceNumber"/> 由进程内
/// <c>Interlocked.Increment</c> 生成，<b>进程重启即归零</b>。若按序号维护高水位，对端一重启，
/// 它新发的序号 1..N 就会被我方高水位判为「旧包」而<b>全量误杀</b>。MessageId 是 Guid，没有这个问题。
/// </para>
/// </summary>
public sealed class MessageReplayGuard : IReplayGuard
{
    /// <summary>默认允许的最大消息年龄。</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(1);

    /// <summary>默认允许的最大时钟超前（两端时钟不同步时的容忍度）。</summary>
    public static readonly TimeSpan DefaultMaxFutureSkew = TimeSpan.FromMinutes(5);

    /// <summary>默认每个对端记住的 MessageId 条数。</summary>
    public const int DefaultHistorySize = 1024;

    /// <summary>
    /// 最多跟踪多少个对端。时间窗与每对端环形缓冲都是有界的，但「对端 → 缓冲」这张字典
    /// 若不设上限仍是随「见过的对端数」增长的；虽然只有能验签通过（即持有私钥）的对端
    /// 才会新增桶、攻击面很小，但把它也封死才能说整个结构是有界的。
    /// <para>
    /// 最坏情况内存：<c>512 × 1024 × 16B = 8 MiB</c>（每个桶按 <see cref="DefaultHistorySize"/>
    /// 预分配整个环）。取 512 而不是更大，是因为新建一个桶需要**通过验签**（即对端持有合法私钥），
    /// 匿名洪泛造不出桶；512 对一个小型聊天网络已经绰绰有余。
    /// </para>
    /// </summary>
    private const int MaxTrackedPeers = 512;

    private readonly ILogger<MessageReplayGuard> _logger;
    private readonly TimeSpan _maxAge;
    private readonly TimeSpan _maxFutureSkew;
    private readonly int _historySize;
    private readonly Func<DateTimeOffset> _now;

    private readonly ConcurrentDictionary<string, PeerBucket> _buckets = new(StringComparer.Ordinal);
    private long _accessCounter;

    /// <param name="logger">拒绝时记 LogWarning 的日志器。</param>
    /// <param name="maxAge">
    /// 允许的最大消息年龄；默认 1 小时。<b>&lt;= TimeSpan.Zero 时关闭第一层时间窗检查</b>
    /// （逃生阀，仅用于两端时钟严重偏移、否则全网互拒的场合；此时只剩第二层 MessageId 去重）。
    /// </param>
    /// <param name="maxFutureSkew">允许的最大时钟超前；默认 5 分钟。不能为负。</param>
    /// <param name="historySize">每个对端记住的 MessageId 条数；默认 1024。必须为正。</param>
    /// <param name="now">
    /// 时钟源，默认 <c>() => DateTimeOffset.UtcNow</c>。测试应注入固定时钟，
    /// <b>不要</b>用 <c>Thread.Sleep</c> 去撞时间窗。
    /// </param>
    public MessageReplayGuard(
        ILogger<MessageReplayGuard> logger,
        TimeSpan? maxAge = null,
        TimeSpan? maxFutureSkew = null,
        int historySize = DefaultHistorySize,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var age = maxAge ?? DefaultMaxAge;
        var skew = maxFutureSkew ?? DefaultMaxFutureSkew;

        if (skew < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxFutureSkew), skew, "允许的时钟超前不能为负");
        if (historySize <= 0)
            throw new ArgumentOutOfRangeException(nameof(historySize), historySize, "每个对端的消息历史长度必须为正");

        _logger = logger;
        _maxAge = age;
        _maxFutureSkew = skew;
        _historySize = historySize;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>时间窗检查是否已启用（<c>maxAge &gt; TimeSpan.Zero</c>）。自检/状态展示用。</summary>
    public bool IsTimeWindowEnabled => _maxAge > TimeSpan.Zero;

    /// <inheritdoc />
    public bool TryAccept(MessageEnvelope envelope, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        // 第一层：时间新鲜度（可关闭的逃生阀）。
        if (IsTimeWindowEnabled && !TryCheckFreshness(envelope, out reason))
            return false;

        // MessageId 为空意味着这条信封没有可用的重放键，去重层对它完全失效。
        // MessageRouter 永远会填（MessageEnvelope.MessageId 默认 Guid.NewGuid()），
        // 因此空值只可能来自构造错误或恶意构造，直接拒绝而不是放行一个「去重无效」的信封。
        if (envelope.MessageId == Guid.Empty)
        {
            reason = "MessageId 为空，无法作为重放去重键（信封构造非法）";
            _logger.LogWarning("重放防护拒绝: {Reason}, Type={Type}, From={Sender}",
                reason, envelope.MessageType, DescribeSender(envelope));
            return false;
        }

        // 第二层：按对端分桶的 MessageId 去重。
        var peerKey = PeerKeyOf(envelope);
        var bucket = GetOrAddBucket(peerKey);

        // 关键：判定「是否见过」与「记下它」必须在**同一个临界区**内完成。
        // 若拆成 Contains + Add 两步，两条并发到达的相同信封会双双通过 —— 并发重放即可绕过。
        lock (bucket.Gate)
        {
            for (var i = 0; i < bucket.Count; i++)
            {
                if (bucket.Ids[i] != envelope.MessageId)
                    continue;

                reason = $"MessageId 重复: {envelope.MessageId} 近期已被同一对端接受过（重放）";
                _logger.LogWarning("重放防护拒绝: {Reason}, Type={Type}, From={Sender}",
                    reason, envelope.MessageType, DescribeSender(envelope));
                return false;
            }

            // 环形写入：满了就覆盖最旧的那一格，内存占用恒为 historySize。
            bucket.Ids[bucket.Next] = envelope.MessageId;
            bucket.Next++;
            if (bucket.Next == bucket.Ids.Length)
                bucket.Next = 0;
            if (bucket.Count < bucket.Ids.Length)
                bucket.Count++;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// 时间窗判定：<see cref="MessageEnvelope.Timestamp"/> 必须落在**闭区间**
    /// <c>[now - maxAge, now + maxFutureSkew]</c> 内（含两端）。
    /// <para>
    /// 为什么闭区间：窗口的语义是「允许的年龄范围」，边界值代表「恰好等于上限/下限」的合法情况，
    /// 不应被误杀；真正的攻击者把时间戳卡在边界上没有任何收益——他无法在不改签名的情况下
    /// 让 Timestamp 越过边界，而边界内的时间戳本身仍要过第二层 MessageId 去重。
    /// </para>
    /// </summary>
    private bool TryCheckFreshness(MessageEnvelope envelope, out string? reason)
    {
        reason = null;

        DateTimeOffset messageTime;
        try
        {
            messageTime = DateTimeOffset.FromUnixTimeMilliseconds(envelope.Timestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            reason = $"时间戳非法: {envelope.Timestamp} 超出 Unix 毫秒可表示范围";
            _logger.LogWarning("重放防护拒绝: {Reason}, Type={Type}, From={Sender}",
                reason, envelope.MessageType, DescribeSender(envelope));
            return false;
        }

        var now = _now();
        var lowerBound = now - _maxAge;
        var upperBound = now + _maxFutureSkew;

        if (messageTime < lowerBound)
        {
            reason = $"过旧: 消息时间 {messageTime:o} 早于允许窗口下界 {lowerBound:o}（maxAge={_maxAge}）";
            _logger.LogWarning("重放防护拒绝: {Reason}, Type={Type}, From={Sender}",
                reason, envelope.MessageType, DescribeSender(envelope));
            return false;
        }

        if (messageTime > upperBound)
        {
            reason = $"来自未来: 消息时间 {messageTime:o} 晚于允许窗口上界 {upperBound:o}（maxFutureSkew={_maxFutureSkew}）";
            _logger.LogWarning("重放防护拒绝: {Reason}, Type={Type}, From={Sender}",
                reason, envelope.MessageType, DescribeSender(envelope));
            return false;
        }

        return true;
    }

    private PeerBucket GetOrAddBucket(string peerKey)
    {
        var bucket = _buckets.GetOrAdd(peerKey, _ => new PeerBucket(_historySize));

        // 先打上「最近用过」再判容量，避免刚用过的桶在淘汰扫描里被自己淘汰掉。
        bucket.LastAccess = Interlocked.Increment(ref _accessCounter);

        if (_buckets.Count > MaxTrackedPeers)
            EvictLeastRecentlyUsed(peerKey);

        return bucket;
    }

    /// <summary>
    /// 对端桶超过上限时淘汰最久未使用的一个。触发条件是「见过的**不同**对端数」超过
    /// <see cref="MaxTrackedPeers"/>，与运行时长、消息条数都无关，因此扫描是罕见事件。
    /// </summary>
    private void EvictLeastRecentlyUsed(string currentPeerKey)
    {
        string? victimKey = null;
        var victimAccess = long.MaxValue;

        foreach (var pair in _buckets)
        {
            if (string.Equals(pair.Key, currentPeerKey, StringComparison.Ordinal))
                continue;

            var access = pair.Value.LastAccess;
            if (access >= victimAccess)
                continue;

            victimAccess = access;
            victimKey = pair.Key;
        }

        if (victimKey is null)
            return;

        if (_buckets.TryRemove(victimKey, out _))
            _logger.LogWarning(
                "已跟踪对端数超过上限 {Max}，淘汰最久未使用的对端去重桶（其近期消息将失去去重保护，直到时间窗过期）: {Peer}",
                MaxTrackedPeers, victimKey);
    }

    /// <summary>
    /// 对端分桶键。正常路径下 <see cref="MessageEnvelope.SenderId"/> 已在验签阶段被校验为
    /// 20 字节且与公钥派生一致；这里仍对长度异常做兜底，避免用畸形数据构造字典键。
    /// </summary>
    private static string PeerKeyOf(MessageEnvelope envelope)
        => envelope.SenderId.Length == NodeId.Size
            ? Convert.ToHexString(envelope.SenderId).ToLowerInvariant()
            : "malformed:" + envelope.SenderId.Length;

    private static string DescribeSender(MessageEnvelope envelope)
    {
        try
        {
            return envelope.SenderId.Length == NodeId.Size
                ? Convert.ToHexString(envelope.SenderId).ToLowerInvariant()[..8]
                : "<malformed>";
        }
        catch (Exception)
        {
            return "<unknown>";
        }
    }

    /// <summary>
    /// 单个对端的消息历史环形缓冲。<b>非线程安全</b>，所有读写都在 <see cref="Gate"/> 内进行。
    /// </summary>
    private sealed class PeerBucket
    {
        public PeerBucket(int capacity) => Ids = new Guid[capacity];

        /// <summary>判定与写入的互斥锁 —— 保证「查重 + 记录」原子的关键。</summary>
        public object Gate { get; } = new();

        /// <summary>环形缓冲，恒定长度 <c>historySize</c>。</summary>
        public Guid[] Ids { get; }

        /// <summary>已填充的有效槽位数（写入始终从 0 连续推进，故 <c>[0, Count)</c> 均有效）。</summary>
        public int Count { get; set; }

        /// <summary>下一个写入槽位；到达末尾后回绕到 0。</summary>
        public int Next { get; set; }

        /// <summary>最近一次被接受的单调时间戳，仅用于淘汰时选最久未使用者。</summary>
        public long LastAccess { get; set; }
    }
}
