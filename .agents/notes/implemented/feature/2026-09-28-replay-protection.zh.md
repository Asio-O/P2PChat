# Agent Note: 入站重放防护（时间新鲜度窗口 + MessageId 去重）

Status: implemented

## Problem

阶段 3.2 给每条消息加了 ECDSA 自签名，`SenderId` 也与发送方长期公钥强绑定（`NodeId.FromPublicKey(公钥) == SenderId`）。但**签名只证明「这条消息由持有对应私钥的一方发出」，不证明「这是一条新消息」**。攻击者录下一条完全合法的信封反复重放，仍能通过验签的全部关卡：缺签名检查、发送方公钥与 `SenderId` 绑定、ECDSA 验签——四道关卡一道都不会拦。

两个本该兜底的时间性字段都没在兜底（**已验证**）：

- `MessageEnvelope.SequenceNumber` 由 `MessageRouter.NextSeq()`（`Interlocked.Increment`）写入、也被签名覆盖，但**全仓库没有任何校验点**——唯一读它的地方是 `SendViaConnectionAsync` 末尾的 `LogTrace`（`MessageRouter.cs:125-126`），纯日志。
- `MessageEnvelope.Timestamp` 被签名覆盖，但**从不与当前时间比较**——所有读取点（各 handler 里 `FromUnixTimeMilliseconds`、TUI 的 `ToLocalTime()` 展示）都只把它还原成时间用于显示或落库，没有任何新鲜度判定。

于是防线只剩「消息确实来自那个私钥持有者」，「这条消息是不是刚刚才第一次出现」无人负责。

## Decision

新增一个**有状态、无密码学**的入站准入步骤，与验签并列但不是第五道验签关卡。契约放在依赖图的根 `P2PChat.Core.Abstractions.IReplayGuard`，单方法 `bool TryAccept(MessageEnvelope envelope, out string? reason)`；实现在 `P2PChat.Chat/Routing/MessageReplayGuard`。调用位置在 `MessageRouter.RouteIncomingAsync` 中**验签通过之后、handler 派发之前**。

两层防护，粗细分工：

1. **时间新鲜度（粗筛，兜住陈旧重放）**：`envelope.Timestamp` 必须落在 `[now - maxAge, now + maxFutureSkew]` 闭区间内。`maxAge` 默认 **1 小时**、`maxFutureSkew` 默认 **5 分钟**。越界即拒，`reason` 区分「过旧」与「来自未来」。逃生阀 `maxAge <= 0` 关闭的是**整个**时间窗检查（`MessageReplayGuard.cs:102` 的 `if (IsTimeWindowEnabled && !TryCheckFreshness(...))`），**不是只放宽「过旧」**——超前时间戳同样不再被拦。半开半关是刻意避免的：它会让人误以为「还剩一层时间保护」，而实际并不是；逃生阀打开后剩下的唯一防线是第二层去重，这一点在装配告警与自检显示里都写明。
2. **`MessageId` 去重（细筛，兜住窗口内的重放）**：`MessageId` 是 16 字节 Guid，**已被签名覆盖**，且由 `Message.NewGuid()` 逐条生成（`Message.cs:26`，`set` 而非 `init`，反序列化时不会被重置）。按**对端 NodeId 分桶**保存最近 N 条（默认 **1024**）已接受的 `MessageId`，重复即拒。缓存**有界**：桶是固定长度的环形缓冲；此外「对端 → 桶」这张字典本身也设了上限 `MaxTrackedPeers = 512` 并做 LRU 淘汰，最坏情况内存 `512 × 1024 × 16B = 8 MiB`。之所以连对端数也要封死，是因为只有**能验签通过**（即持有合法私钥）的对端才会新增桶，攻击面本来就很小——但把它也封死，整个结构才能被称作有界。

判定细节：`MessageId == Guid.Empty` 直接拒绝（「无法作为重放去重键」），因为一条没有重放键的信封会让第二层完全失效；对端分桶键取 `SenderId` 的小写十六进制，`SenderId` 长度异常时退化成 `"malformed:<len>"`，避免用畸形数据构造字典键。时间窗取**闭区间** `[now - maxAge, now + maxFutureSkew]`：边界值代表「恰好等于上限」的合法情况不应被误杀，而把时间戳卡在边界上的攻击者没有收益——他无法在不改签名的情况下让 `Timestamp` 越过边界，而边界内的时间戳仍要过第二层去重。

接口契约显式要求**线程安全，且对同一个信封的判定必须是原子的**——两条完全相同的信封并发到达时，有且只有一条能返回 true，否则并发重放就能绕过。实现方式是：每个桶一把锁，「查重 + 环形写入」在**同一个临界区**内完成；拆成 `Contains` 与 `Add` 两步，两条并发到达的相同信封就会双双通过。

`MessageRouter` 构造函数**新增必填**的 `IReplayGuard` 形参，不给默认值：可选参数（`IReplayGuard? x = null`）会让「忘记注入」静默等于「关闭防护」，那是安全陷阱。调用点在 `RouteIncomingAsync` 中排在 `VerifyEnvelope` 之后、`_handlers.TryGetValue` 之前——**验签之后**是为了不对攻击者可控的数据做无谓工作，**派发之前**是为了保证被重放的消息不产生任何副作用。拒绝时由 `IReplayGuard` 记一条 `LogWarning`（带过旧 / 超前 / 重复的明因），`MessageRouter` 只补一条 `LogTrace`，避免同一事件在日志里出现两行。时钟通过构造函数注入 `Func<DateTimeOffset>? now = null`（默认 `() => DateTimeOffset.UtcNow`）以便测试注入固定时钟，不靠 `Thread.Sleep` 测时间窗。

装配与可见性：配置项 `P2PChat:ReplayMaxAgeSeconds`（`double`，默认 `3600`，`<= 0` 关闭时间窗）在 `Program.cs` 解析后换算成 `TimeSpan` 注入 `IReplayGuard`；关闭时记 `LogWarning`，不静默。DI 注册顺序上 `IReplayGuard` 必须先于 `IMessageRouter`（后者形参必填），`ChatServiceCollectionExtensions` 另外用 `TryAddSingleton` 提供一份走默认值的注册。TUI 不去问 `IReplayGuard`（那是只暴露 `TryAccept` 的安全边界），而是由装配处注入一个不可变的 `ReplayGuardStatus` 值记录，自检块打印 `重放防护:   …` 一行——降级要明示，不静默失败。

## Alternatives considered

**按对端维护 `SequenceNumber` 高水位做序号窗口（低于水位的帧一律拒）。** 否决，而且否得有具体机制，不是「不合适」三条理由：

- **进程重启即归零。** `SequenceNumber` 来自 `MessageRouter._seqCounter` 这个**进程内**计数器（`NextSeq() => Interlocked.Increment(ref _seqCounter)`），不持久化。对端重启后序号从 1 重新开始，而本机仍持有重启前累积的高水位 → **对端重启后发出的每一条消息都会低于水位而被全量误杀**。这不是理论风险：节点重启、崩溃恢复、网络切换后重连都是常规操作。
- **同一个节点有两个独立的序号计数器。** 除 `MessageRouter._seqCounter` 外，`KeyExchangeHandler` 还维护着自己的 `_responseSequence`（`KeyExchangeHandler.cs:225`），两者都从 1 开始。也就是说**同一对端的不同消息可能合法地携带相同的 `SequenceNumber`** —— 序号在这个协议里根本不是唯一键，任何基于它的判重都会把「合法重复」判成重放。
- **并发投递下同一对端的帧会乱序到达。** 「低于高水位即拒」会把迟到但完全合法的帧当成重放杀掉，而这个误杀无法与真正的重放在现象上区分。

根因是：序号窗口要求「序号在跨重启、跨代码路径上全局单调且无空洞」，当前协议不具备这个前提。

**把策略塞进 `MessageRouter.VerifyEnvelope`。** 否决：`VerifyEnvelope` 是**纯密码学**的、无状态的、可重复调用的函数，`MessageSigningTests` 有 5 处直接调用 `MessageRouter.VerifyEnvelope(`。往里塞有状态策略会污染语义——**同一信封校验两次，第二次就会失败**，这不是「更严格」，是把一个纯函数变成有副作用的。而这会连带炸红一批语义完全正确的密码学测试，让真正的失败淹没在噪声里。策略与密码学必须分离。

**只做时间窗，不做 `MessageId` 去重。** 否决：1 小时的窗对「录一包、重放一万次」这个最省力的攻击完全无效——攻击者只要在窗口内循环投递就绕过了。

**只做 `MessageId` 去重，不做时间窗。** 否决：去重是**精确但有界**的——环形淘汰之后，先前被淘汰的 `MessageId` 可以再次被接受；而没有时间窗就没有任何上界来限制这个循环能持续多久、重放多旧的包。时间窗提供的是**上界**：超过 `maxAge` 的帧无论缓存状态如何一律拒。

**把 `MessageId` 去重缓存持久化到磁盘。** 否决：持久化后「我上次见过什么」会跨进程存活，而对端重启后本应照常通信；这既引入 IO 与一致性代价，也让缓存的生命周期与身份/会话生命周期脱节。当前的取舍是：跨重启的长期重放交给时间窗兜，不追求跨进程的精确去重。

## Consequences

- 入站多一道准入：**验签通过但被判为重放的帧，在 handler 派发之前就被拒**，不会触发业务逻辑。拒绝原因（过旧 / 超前 / 重复）以 `LogWarning` 记录，可查。
- **代价：时钟偏移会误杀。** 两台机器时间相差超过 `maxFutureSkew`（默认 5 分钟）时，正常消息会被判「来自未来」而拒；默认 1 小时的 `maxAge` 同样会误杀长连接上陈旧但合法的消息。这是**用「误杀少量陈旧消息」换「挡住无限重放」的自觉取舍**——不做时间新鲜度判定，就等于把重放完全敞开。逃生阀是 `ReplayMaxAgeSeconds <= 0`（关闭**整个**时间窗检查，过旧与超前都不再拦），用于时钟严重偏移的机器；关掉即意味着该机器上时间这一层防护归零，所以关掉时必须有显式告警，不能让它只存在于配置文件里。
- **代价：`MessageId` 去重是有界的。** 每对端只保留最近 1024 条，淘汰后的极旧重放仍可能通过；时间窗只覆盖「仍在窗口内」的那部分。两层是叠加的，不是等价的替代。
- **代价：拒绝是策略性的，与密码学判定正交。** 被重放防护拒绝的帧**并不代表它签名无效**；反过来，一条帧绕过重放防护后仍是有效签名。日志与告警必须让维护者能区分这两件事，否则排障时会往错误的方向查。
- 构造函数新增**必填**形参：忘记注入 `IReplayGuard` 会在构造时失败，而不是静默降级成「防护关闭」。代价是每个构造点（DI 装配、测试脚手架）都要跟着改，这是刻意的。
- **与文件传输的交互已核对**：每个 `FileChunkMessage` / `FileMetaMessage` 都是新实例，`MessageId` 走 `Guid.NewGuid()`，因此分块不会被去重误杀。
- **hello 应答不受「对端挂了多久」影响。** `KeyExchangeHandler` 构造应答消息时只显式赋了 4 个字段（`SenderId` / `ConversationId` / `EphemeralPublicKey` / `IsResponse`，`KeyExchangeHandler.cs:149-156`），`MessageId` 与 `Timestamp` 走 `Message` 基类的属性初始化器（`Message.cs:26`、`:35`），因此拿到的是**全新 Guid 与应答生成时刻**的时间戳。时间窗判定针对的是应答自身产生的时刻，所以对端即使长时间挂起后才回包，该应答仍会被正常接受，不会被误判为「过旧」。
- **群扇出决定了去重必须按对端分桶。** `GroupChatService.SendGroupMessageAsync` 把**同一个 `Message` 对象**（因而同一个 `MessageId`）发给群内每个不同对端（`GroupChatService.cs:222-231`）。若去重缓存做成全局集合，**第二条群消息会被全网丢弃**——而单节点测试完全测不出这个缺陷，因为单节点场景下根本不存在第二个收件人。

## Testing

`tests/P2PChat.Chat.Tests/ReplayGuardTests.cs`（30 条：28 个 `[Fact]` + 1 个两参数 `[Theory]`）与 `tests/P2PChat.Integration.Tests/ReplayProtectionTests.cs`（4 条，真实 TCP + 真实 ECDSA 签名）共同锁住本决策。`dotnet build P2PChat.slnx` 0 错 0 警，`dotnet test` 331 通过 / 0 失败（Crypto 8 / Core 52 / Chat 110 / Integration 138 / Networking 23）。其中三处写法值得记，因为它们正是「契约与实现都证明不了、只有测试能证明」的地方：

- **闭区间用成对构造钉死**：恰好等于 `now - maxAge` 被接受、恰好等于 `now + maxFutureSkew` 被接受、各再往外 1 毫秒即被拒，另有一条验证两个边界落在同一守卫上不会因先后顺序漂移。只测窗口中间值的话，「闭区间被写成开区间」这种偏差根本测不出来。
- **并发原子性用裸 `Thread` + `Barrier`**：64 个线程同时投递同一信封，断言恰好一条通过；另一条用 8 个对端 × 24 条消息 × 4 个副本共 768 次投递，断言每条恰好被接受一次。刻意**不用 `Task.Run`** —— `Barrier` 会阻塞参与者，若全部丢进线程池，线程池会按秒级扩张去补线程，既慢又让注入时机不确定，测试反而变得不稳定。
- **缓存有界断言的是行为上界而不是内部条目数**：`historySize = 8` 时灌入 9 条，断言最早那条再投递被接受（已淘汰）、而最近的仍被拒。这里有个**顺序陷阱**——每次「被接受」都会写进环形缓冲并挤掉该桶最旧的一格，所以必须先断言「仍被记住」、最后才断言「已被淘汰」，否则前一次接受会把待断言的淘汰对象挤掉，造成假红。之所以断言行为而不是条目数，是因为 `PeerBucket` 是 private 且没有公开的计数访问器。

集成侧的 4 条走真实链路：抓帧靠 `NodeHarness.Start` 的**连接装饰器**钩子，在 router 把字节写出去的那一刻截获**真正上线的字节**，攻击者用自己的 `TcpTransport` 开**第二条连接**原样重发。有两个刻意的选择：测试**不自己重签**（重签测的是「重新签名」，不是「重放」），且在重放之前**先断言该帧 `VerifyEnvelope` 通过**，以确保测的确实是重放而不是一个非法包。其余三条是对照组：连续 5 次重放每一条都被拦下并各自留下「重复」告警、连续多条正常消息条条送达不被误杀、同一条群消息扇出给两个成员两人都收到（这条把「按对端分桶」的真实理由直接演了出来）。

可查性上有一处容易踩的细节：`MessageRouter` 对重放拒绝只记 `LogTrace`（刻意避免同一事件打两行），真正的 `LogWarning` 由 `MessageReplayGuard` 记——所以「告警可查」的断言必须挂在 guard 的 logger 上。

所有关键断言都过了变异测试：14 处改反后全部变红，再逐条还原。

## Deferred

以下残余风险**没有被本次实现解决**（与 `Agent.md` §7 记录一致）：

- **`MessageId` 被环形淘汰后的重放仍可能通过。** 每桶只记 1024 条；某对端在时间窗内发来超过 1024 条后，最早的那些 `MessageId` 被挤出缓存，重放它们只能靠第一层时间窗兜底。
- **对端桶被 LRU 淘汰后，该对端的去重保护立即失效。** 超过 512 个不同对端时最久未用的桶被丢弃，它近期已收到的消息重放时不再被第二层拦住，同样只能靠时间窗兜底（淘汰时打 `LogWarning` 明示）。
- **`ReplayMaxAgeSeconds <= 0` 时只剩 MessageId 去重一层。** 时间窗两层全关，叠加前两条，被淘汰过的 `MessageId` 可以被反复重放。攻击者**无法**伪造新的 `MessageId`——它在签名覆盖内，改了就验签失败——所以残余的是「重放旧 id」，不是「注入新 id」。
- **去重状态纯内存，进程重启即清空。** 重启后重放本机启动前收到的旧信封不会被第二层拦住，只能靠时间窗兜底。
- **挡不住流量重定向与拒绝服务。** 它只决定「这条消息能不能进 handler」，不涉及链路层与网络层攻击。
- 跨重启的长期重放（对端重启后重放很久以前的旧包）要根治需要持久化的去重状态，当前只由时间窗兜底。

另有一条与本决策的论证相关、但不属残余风险的事实：**`MessageRouter.MaxConnectionsPerPeer = 3` 目前是一个未被使用的常量**——全仓库只命中声明处 `MessageRouter.cs:27`（另一处在 `Agent.md` 的文档里）。也就是说「每对端最多 3 条连接」今天并不成立；本记录的乱序论证不依赖它。

## Related

- [Message signing](../bug-fix/2026-09-21-message-signing.md) —— 阶段 3.2 自签名上线，也是本条遗留缺口的来源。
- [Message SenderId must derive from the node identity](../bug-fix/2026-09-20-message-sender-identity.md) —— 「公钥派生 NodeId 必须等于 SenderId」这条防冒名守卫，是重放防护要依附的那道验签关卡之一。
- [Envelope wire codec converged onto a single source of truth in Core](../bug-fix/2026-09-28-envelope-codec-single-source.md) —— `MessageId` 与 `Timestamp` 都在签名覆盖范围内，其字节布局由 `EnvelopeCodec` 唯一定义；「同一信封」正是在那个布局下才成立的判定对象。
