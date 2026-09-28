using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Extensions;
using P2PChat.Core.Models;
using static P2PChat.UI.Views.ConsoleScreen;

namespace P2PChat.UI.Views;

/// <summary>
/// 入站重放防护的<b>可见状态快照</b> —— 由 <c>Program.cs</c> 在解析
/// <c>P2PChat:ReplayMaxAgeSeconds</c> 之后构造，注入 TUI 供自检块显示。
/// <para>
/// 刻意做成<b>不可变值</b>，而不是让 UI 去问 <c>IReplayGuard</c>：
/// 该接口属于 Core 且只暴露 <c>TryAccept(MessageEnvelope, out string?)</c>，
/// 没有任何策略可见性 —— 为了「自检块显示一行」去给安全边界接口加展示成员是错的。
/// 类型放在本文件内，是因为 <c>P2PChat.UI</c> 只引用 <c>P2PChat.Core</c>（不引用 Chat），
/// 而 <c>Program.cs</c>（App）引用 UI，可以直接构造它并注册进 DI。
/// </para>
/// <para>
/// 对应 REPAIR-PLAN 阶段 2.3 的「降级要明示，不要静默失败」：用户必须能一眼看出
/// 「超过 N 分钟的消息会被丢弃」，以及在时钟严重偏移的机器上是否已关掉这个时间窗。
/// </para>
/// </summary>
/// <param name="TimeWindowEnabled">时间窗是否启用。<c>false</c> 表示
/// <c>P2PChat:ReplayMaxAgeSeconds &lt;= 0</c>，即逃生阀已打开。</param>
/// <param name="MaxAge">允许的最大消息年龄。<see cref="TimeWindowEnabled"/> 为
/// <c>false</c> 时该值无意义，但仍原样携带以便日志/诊断。</param>
public sealed record ReplayGuardStatus(bool TimeWindowEnabled, TimeSpan MaxAge)
{
    /// <summary>自检块显示用的「已启用 / 已关闭」一句话（不含字段名前缀）。</summary>
    public string Describe()
        => TimeWindowEnabled
            ? $"已启用 (最大消息年龄 {FormatAge(MaxAge)})"
            : "已关闭 —— 超过最大年龄的消息会被接受，请确认这是有意为之";

    /// <summary>
    /// 把时间窗长度格式化成人类可读文本：小于 1 分钟用秒，否则用分钟；
    /// 达到 1 天才改用小时，避免出现「1440 分钟」这种没人愿意读的数字。
    /// <para>
    /// 默认值 3600s 因此显示为「60 分钟」—— 与 REPAIR-PLAN 阶段 2.3 的示例文案一致，
    /// 也让 e2e / config-probe 的断言可以按这个字面量写。
    /// </para>
    /// </summary>
    public static string FormatAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60) return $"{age.TotalSeconds:0.##} 秒";
        if (age.TotalDays >= 1)
        {
            var hours = age.TotalHours;
            return hours % 1 == 0 ? $"{hours:0} 小时" : $"{hours:0.#} 小时";
        }
        var minutes = age.TotalMinutes;
        return minutes % 1 == 0 ? $"{minutes:0} 分钟" : $"{minutes:0.#} 分钟";
    }
}


/// <summary>
/// P2PChat 控制台界面 —— 零依赖自绘 UI（不使用任何第三方 TUI 框架，兼容 Native-AOT）
/// 布局：标题栏 / 左栏联系人(含在线状态) / 右栏当前会话聊天记录 / 底部输入行 / 状态栏
/// </summary>
public sealed class P2PChatTui(
    IChatService chatService,
    IGroupChatService groupChatService,
    IContactService contactService,
    IFileTransferService fileTransferService,
    IDhtService dhtService,
    IMessageRouter router,
    IKeyStore keyStore,
    IEncryptionService encryption,
    ILogger<P2PChatTui> logger,
    ReplayGuardStatus replayGuard) : IDisposable
{
    private const int MaxHistory = 500;                    // 每个会话保留的历史行数
    private const string SystemConversation = "__system__";
    private readonly ConsoleScreen _screen = new();
    private readonly object _sync = new();
    private readonly List<Contact> _contacts = [];
    private readonly Dictionary<string, List<string>> _messageHistory = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Text, string Cid)> _inbox = new();   // 后台线程 → UI 线程
    private string _currentConversationId = string.Empty;
    /// <summary>
    /// 当前私聊会话的对端节点ID。
    /// <para>
    /// 必须与 <see cref="_currentConversationId"/> 分开保存：后者是**方向无关**的会话键
    /// （两个 NodeId 拼接），不能反解出对端；发送时需要的是对端节点ID本身。
    /// 群聊会话下为 <c>null</c>。
    /// </para>
    /// </summary>
    private NodeId? _currentPeerId;
    private string _currentChatTitle = "未选择会话 - 使用 /help 查看命令";
    private volatile string _dhtStatus = "DHT: --";
    private volatile bool _isGroupChat, _dirty = true, _running, _plainMode;

    /// <summary>
    /// 线性（plain）模式的行式输入通道。
    /// <para>
    /// plain 模式没有可用的控制台按键事件（<see cref="ReadKeyOrNull"/> 直接返回 <c>null</c>），
    /// 因此 stdin 只能按「行」读取。后台任务 <see cref="ReadPlainInputAsync"/> 阻塞在
    /// <see cref="Console.ReadLine()"/> 上，把读到的行投递到这里，主循环消费后走与
    /// <see cref="SubmitInput"/> 完全相同的命令分派路径。
    /// </para>
    /// <para>
    /// 与 plain 模式的输出语义（<see cref="DrainInbox"/> 逐行 <c>Console.WriteLine</c>）对称，
    /// 使 <c>P2PCHAT_PLAIN=1</c> / stdout 被重定向 / CI 等场景下程序仍然可用。
    /// </para>
    /// </summary>
    private readonly Channel<string> _plainInput = Channel.CreateUnbounded<string>();

    /// <summary>
    /// <see cref="ReadPlainInputAsync"/> 是否已启动（0=否）。用 <see cref="Interlocked"/> 保证幂等。
    /// </summary>
    private int _plainInputStarted;

    private string _input = string.Empty;
    private int _contactIndex, _scrollFromEnd;
    private bool _disposed;
    /// <summary>启动 TUI；P2PCHAT_SELFTEST=1 时只打印自检信息后立即返回（不进入阻塞输入循环）</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        // P2PCHAT_PLAIN=1 强制启用「线性输出」模式：每条聊天消息逐行写入 stdout，
        // 供 e2e-verify.ps1 之类的脚本直接抓取明文断言。
        // 默认只有控制台初始化失败才会进入该模式（参见下方 catch）。
        if (Environment.GetEnvironmentVariable("P2PCHAT_PLAIN") == "1") _plainMode = true;

        RefreshDhtStatus();
        await RefreshContactsAsync(ct);
        if (Environment.GetEnvironmentVariable("P2PCHAT_SELFTEST") == "1") { await RunSelfTestAsync(ct); return; }
        _running = true;
        try { Console.Title = "P2PChat - P2P DHT Chat"; Console.Clear(); }
        catch (Exception ex) { _plainMode = true; logger.LogDebug(ex, "控制台初始化失败，改用线性输出模式"); }
        _ = ProcessIncomingMessagesAsync(ct);   // 后台循环 1：接收聊天消息
        _ = ProcessFileOffersAsync(ct);         // 后台循环 2：接收文件 Offer
        _ = RefreshDhtPeriodicallyAsync(ct);    // 后台循环 3：周期性刷新 DHT / 在线状态
        EnsurePlainInputStarted(ct);   // 后台循环 4：行式 stdin（plain 模式专用；幂等，中途降级也会补启动）
        try
        {
            while (_running && !ct.IsCancellationRequested)
            {
                DrainInbox();
                Render();
                var key = ReadKeyOrNull();
                if (key is { } k) { HandleKey(k); continue; }
                // plain 模式没有按键事件，改为消费后台读到的整行命令。
                // 先 Ensure：_plainMode 可能在运行中途才被降级路径翻成 true。
                if (_plainMode)
                {
                    EnsurePlainInputStarted(ct);
                    if (_plainInput.Reader.TryRead(out var line)) { SubmitLine(line); continue; }
                }
                try { await Task.Delay(50, ct); } catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            _running = false;
            if (!_plainMode) { try { Console.ResetColor(); Console.Clear(); } catch { } }
            Console.WriteLine("P2PChat 已退出");
        }
    }
    /// <summary>非交互自检：打印节点ID / 监听端口 / DHT已知节点数 / 联系人 / 群组数 / 宣告状态</summary>

    private async Task RunSelfTestAsync(CancellationToken ct)
    {
        var waitMs = int.TryParse(Environment.GetEnvironmentVariable("P2PCHAT_SELFTEST_WAIT"), out var v) && v >= 0 ? v : 3000;
        for (var deadline = DateTime.UtcNow.AddMilliseconds(waitMs); DateTime.UtcNow < deadline && !ct.IsCancellationRequested;)
        {
            try { await Task.Delay(200, ct); } catch (OperationCanceledException) { break; }
            RefreshDhtStatus();
        }
        int contacts;
        lock (_sync) contacts = _contacts.Count;
        var local = dhtService.LocalNode;
        Console.WriteLine("===== P2PChat 自检 =====");
        Console.WriteLine($"节点ID:     {local.NodeId.ToHexString()}");
        Console.WriteLine($"数据目录:   {Core.Extensions.DataPath.Root}");
        Console.WriteLine($"监听端口:   {local.EndPoint}");
        Console.WriteLine($"DHT已知节点: {dhtService.GetAllKnownNodes().Count}");
        Console.WriteLine($"联系人:     {contacts}");
        Console.WriteLine($"群组:       {groupChatService.GetKnownGroups().Count}");
        // 宣告 (Phase 1.1)：MainlineDhtService.BootstrapAsync 会立即尝试一次 announce_peer，
        // 然后随 RefreshLoopAsync 每 15 分钟重复一次。状态字段挂在 IDhtService 接口上，
        // 不需要 UI 直接引用 Networking 项目。
        var lastAnnounce = dhtService.LastAnnounceUtc;
        var lastText = lastAnnounce == DateTime.MinValue
            ? "(尚未完成首次宣告)"
            : lastAnnounce.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        Console.WriteLine($"宣告状态:   已宣告节点数={dhtService.AnnouncedPeerCount}, 最近一次={lastText}");
        // Phase 2 / 2.3: NAT 穿透状态。无 UPnP 时显式标注可达性范围，不静默失败。
        var natState = dhtService.NatMappingState;
        var natText = natState switch
        {
            NatMappingState.NotAttempted => "(未尝试 UPnP)",
            NatMappingState.Mapped => $"已映射 → 公网 {dhtService.LocalExternalEndPoint}",
            NatMappingState.Unavailable => "无 UPnP —— 仅同网段/公网可达方可主动连入",
            _ => natState.ToString()
        };
        Console.WriteLine($"UPnP 状态:  {natState} ({natText})");
        // 入站重放防护（REPAIR-PLAN 阶段 2.3：降级要明示）。与「宣告状态」「UPnP 状态」同风格，
        // 冒号后补 3 个空格对齐到第 12 列。状态是注入的不可变值（ReplayGuardStatus），
        // 不是现场探测 —— 配置在启动时解析一次，这里如实展示。
        Console.WriteLine($"重放防护:   {replayGuard.Describe()}");
        Console.WriteLine("自检完成:   OK");
        Console.Out.Flush();
    }
    private ConsoleKeyInfo? ReadKeyOrNull()
    {
        if (_plainMode) return null;
        try { return Console.KeyAvailable ? Console.ReadKey(intercept: true) : null; }
        catch (Exception) { _plainMode = true; return null; }
    }
    /// <summary>
    /// 幂等地启动 plain 模式的行式 stdin 读取（后台循环 4）。
    /// <para>
    /// <b>必须惰性启动</b>。<c>_plainMode</c> 有三条进入路径，其中
    /// <see cref="ReadKeyOrNull"/> 的 <c>catch</c> 与 <see cref="Render"/> 的
    /// <c>InvalidOperationException</c> 降级都可能在<b>运行中途</b>才把它翻成 <c>true</c>。
    /// 若只在 <c>RunAsync</c> 开头判断一次，程序会退化回修复前的老症状：
    /// 永远读不到按键、stdin 读取任务又没启动 —— 静默无输入，且此时通道永不完成、
    /// 主循环只会空转，连 EOF 都不会退出。
    /// </para>
    /// <para>
    /// 用 <see cref="Interlocked.Exchange(ref _plainInputStarted, 1)"/> 做「检查并置位」的原子化，
    /// 避免主循环每 50ms 重复启动一个阻塞在 <c>Console.ReadLine()</c> 的任务。
    /// </para>
    /// </summary>
    private void EnsurePlainInputStarted(CancellationToken ct)
    {
        if (!_plainMode) return;
        if (Interlocked.Exchange(ref _plainInputStarted, 1) != 0) return;
        logger.LogDebug("plain 模式：启动后台 stdin 读取任务");
        _ = ReadPlainInputAsync(ct);
    }

    /// <summary>
    /// 后台循环 4：plain 模式按行读取 stdin，把整行命令投递进 <see cref="_plainInput"/>。
    /// <para>
    /// <see cref="Console.ReadLine()"/> 是阻塞调用，因此必须放在后台任务上，绝不能占用主循环。
    /// 读到 <c>null</c> 表示 stdin 已关闭（EOF，例如脚本注入完毕后关闭管道），此时结束通道，
    /// 让 <c>TryRead</c> 恒返回 <c>false</c> 而不会自旋。
    /// </para>
    /// </summary>
    private async Task ReadPlainInputAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await Task.Run(() => Console.ReadLine(), ct).ConfigureAwait(false);
                logger.LogDebug("plain 模式：stdin 读到 {IsEof} (长度 {Len})",
                    line is null, line?.Length ?? -1);
                if (line is null) break;                 // stdin 已 EOF
                if (line.Length > 0) await _plainInput.Writer.WriteAsync(line, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogDebug(ex, "线性模式 stdin 读取结束"); }
        finally { _plainInput.Writer.TryComplete(); }
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter: SubmitInput(); break;
            case ConsoleKey.Backspace: if (_input.Length > 0) { _input = _input[..^1]; MarkDirty(); } break;
            case ConsoleKey.Escape: _input = string.Empty; MarkDirty(); break;
            case ConsoleKey.Tab: SelectNextContact(key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : 1); break;
            case ConsoleKey.UpArrow: _scrollFromEnd++; MarkDirty(); break;
            case ConsoleKey.PageUp: _scrollFromEnd += 10; MarkDirty(); break;
            case ConsoleKey.DownArrow: _scrollFromEnd = Math.Max(0, _scrollFromEnd - 1); MarkDirty(); break;
            case ConsoleKey.PageDown: _scrollFromEnd = Math.Max(0, _scrollFromEnd - 10); MarkDirty(); break;
            case ConsoleKey.F1: ShowHelp(); break;
            case ConsoleKey.F2: AddSystemMessage("使用 /add <节点ID(hex)> [别名] 添加联系人"); break;
            case ConsoleKey.F3: AddSystemMessage("使用 /group create <名称> 创建群组"); break;
            case ConsoleKey.F10: _running = false; break;
            case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control): _running = false; break;
            default: if (!char.IsControl(key.KeyChar)) { _input += key.KeyChar; MarkDirty(); } break;
        }
    }
    private void SubmitInput()
    {
        var text = _input.Trim();
        _input = string.Empty;
        MarkDirty();
        SubmitLine(text);
    }
    /// <summary>
    /// 提交一行用户输入（<c>Enter</c> 键或 plain 模式的整行 stdin）。
    /// 交互模式与线性模式共用此分派，保证两条路径的命令语义完全一致。
    /// </summary>
    private void SubmitLine(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text[0] == '/') _ = Task.Run(() => ProcessCommandAsync(text));
        else if (_currentConversationId.Length > 0) _ = Task.Run(() => SendMessageAsync(text));
        else AddSystemMessage("请先选择联系人或群组，或使用 /msg <ID> <消息> 发送私聊");
    }
    /// <summary>Tab / Shift+Tab 在联系人列表中选择当前会话对象</summary>
    private void SelectNextContact(int delta)
    {
        Contact[] list;
        lock (_sync) list = _contacts.ToArray();
        if (list.Length == 0) { AddSystemMessage("暂无联系人，使用 /add <节点ID> [ip:port] [别名] 添加"); return; }
        _contactIndex = ((_contactIndex + delta) % list.Length + list.Length) % list.Length;
        var contact = list[_contactIndex];
        _currentPeerId = contact.NodeId;
        _currentConversationId = PrivateConversationKey(contact.NodeId);
        _isGroupChat = false;
        _currentChatTitle = $"私聊: {contact.Alias} ({Short(contact.NodeId.ToHexString())})";
        _scrollFromEnd = 0;
        MarkDirty();
    }

    /// <summary>
    /// 本机与指定对端之间私聊会话的方向无关键。
    /// 必须与 <c>ChatService.SendPrivateMessageAsync</c> 使用同一个函数，
    /// 否则接收到的消息会落进一个 UI 选不中的会话桶。
    /// </summary>
    private string PrivateConversationKey(NodeId peerId)
        => ConversationId.ForPrivate(dhtService.LocalNode.NodeId, peerId);

    private async Task ProcessCommandAsync(string command)
    {
        var parts = command[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        var cmd = parts[0].ToLowerInvariant();
        var args = parts.Length > 1 ? parts[1] : "";
        try
        {
            if (cmd is "msg" or "pm" or "file" or "add" or "accept" or "reject" or "group" or "connect" && args.Length == 0)
            {
                AddSystemMessage(cmd is "msg" or "pm" ? "用法: /msg <联系人|节点ID> <消息>"
                    : cmd == "file" ? "用法: /file send <联系人> <文件路径>"
                    : cmd == "add" ? "用法: /add <节点ID(hex,40位)> [ip:port] [别名]"
                    : cmd is "accept" or "reject" ? $"用法: /{cmd} <传输ID>"
                    : cmd == "connect" ? "用法: /connect <ip:port>（不需知道对端节点ID）"
                    : "用法: /group create <名称> 或 /group send <ID> <消息>");
                return;
            }
            switch (cmd)
            {
                case "msg" or "pm": await SendPrivateMessageByCommandAsync(args); break;
                case "group": await HandleGroupCommandAsync(args); break;
                case "file": await HandleFileCommandAsync(args); break;
                case "add": await AddContactCommandAsync(args); break;
                case "connect": await ConnectCommandAsync(args); break;
                case "id" or "me": ShowNodeInfo(); break;
                case "net" or "dht": ShowDhtStatus(); break;
                case "contacts": await ShowContactsListAsync(); break;
                case "accept" or "reject": await HandleTransferAsync(args, cmd == "accept"); break;
                case "help": ShowHelp(); break;
                case "quit" or "exit": _running = false; break;
                default: AddSystemMessage($"未知命令: /{parts[0]}。输入 /help 查看命令列表"); break;
            }
        }
        catch (Exception ex)
        {
            AddSystemMessage($"命令执行失败: {ex.Message}");
            logger.LogWarning(ex, "命令执行失败: {Command}", command);
        }
    }
    private async Task SendMessageAsync(string text)
    {
        try
        {
            if (_isGroupChat) await groupChatService.SendGroupMessageAsync(_currentConversationId, text);
            else if (_currentPeerId is { } peer) await chatService.SendPrivateMessageAsync(peer, text);
            else { AddSystemMessage("请先选择联系人或群组"); return; }
            AddMessage($"我: {text}");
        }
        catch (Exception ex) { AddSystemMessage($"发送失败: {ex.Message}"); logger.LogWarning(ex, "发送消息失败"); }
    }
    private async Task SendPrivateMessageByCommandAsync(string args)
    {
        var spaceIdx = args.IndexOf(' ');
        if (spaceIdx < 0) { AddSystemMessage("用法: /msg <联系人|节点ID> <消息>"); return; }
        var target = args[..spaceIdx];
        var text = args[(spaceIdx + 1)..];
        var contact = FindContact(target);
        if (contact == null) { AddSystemMessage($"未找到联系人: {target}"); return; }
        await chatService.SendPrivateMessageAsync(contact.NodeId, text);
        _currentPeerId = contact.NodeId;
        _currentConversationId = PrivateConversationKey(contact.NodeId);
        _isGroupChat = false;
        _currentChatTitle = $"私聊: {contact.Alias} ({Short(contact.NodeId.ToHexString())})";
        AddMessage($"我 -> {contact.Alias}: {text}");
    }
    private async Task HandleGroupCommandAsync(string args)
    {
        var spaceIdx = args.IndexOf(' ');
        var subCmd = spaceIdx < 0 ? args : args[..spaceIdx];
        var rest = spaceIdx < 0 ? "" : args[(spaceIdx + 1)..];
        switch (subCmd.ToLowerInvariant())
        {
            case "create":
                if (rest.Length == 0) { AddSystemMessage("用法: /group create <名称>"); return; }
                var group = await groupChatService.CreateGroupAsync(rest, Array.Empty<NodeId>());
                AddSystemMessage($"群组已创建: {group.GroupName} (ID: {Short(group.GroupId)})");
                break;
            case "send":
                var idx = rest.IndexOf(' ');
                if (idx < 0) { AddSystemMessage("用法: /group send <群ID前缀> <消息>"); return; }
                var known = groupChatService.GetKnownGroups()
                    .FirstOrDefault(g => g.GroupId.StartsWith(rest[..idx], StringComparison.OrdinalIgnoreCase));
                if (known == null) { AddSystemMessage("未找到群组"); return; }
                var msg = rest[(idx + 1)..];
                await groupChatService.SendGroupMessageAsync(known.GroupId, msg);
                _currentConversationId = known.GroupId;
                _currentPeerId = null;
                _isGroupChat = true;
                _currentChatTitle = $"群聊: {known.GroupName}";
                AddMessage($"我: {msg}");
                break;
            case "list":
                var groups = groupChatService.GetKnownGroups();
                AddSystemMessage($"已知群组 ({groups.Count}):");
                foreach (var g in groups) AddSystemMessage($"  {g.GroupName} - {Short(g.GroupId)} ({g.MemberIds.Count}人)");
                break;
            default: AddSystemMessage("用法: /group create <名称> | send <ID> <消息> | list"); break;
        }
    }
    private async Task HandleFileCommandAsync(string args)
    {
        var spaceIdx = args.IndexOf(' ');
        if (spaceIdx < 0) { AddSystemMessage("用法: /file send <联系人> <文件路径>"); return; }
        var target = args[..spaceIdx];
        var filePath = args[(spaceIdx + 1)..];
        var contact = FindContact(target);
        if (contact == null) { AddSystemMessage($"未找到联系人: {target}"); return; }
        try
        {
            var transferId = await fileTransferService.SendOfferAsync(contact.NodeId, filePath);
            AddSystemMessage($"文件Offer已发送: {Path.GetFileName(filePath)} (传输ID: {Short(transferId)})");
        }
        catch (Exception ex) { AddSystemMessage($"发送文件失败: {ex.Message}"); }
    }
    /// <summary>
    /// <c>/add &lt;节点ID(hex,40位)&gt; [ip:port] [别名]</c>
    /// <para>
    /// 给出 <c>ip:port</c> 时该对端会被登记为「静态对端」，无需任何 DHT 发现即可直连 ——
    /// 这是当前唯一可靠的直连手段，因为公共 Mainline DHT 上没有任何节点为我们宣告
    /// <c>NodeId → 端点</c> 映射，迭代 <c>find_node</c> 不可能命中对端。
    /// </para>
    /// </summary>
    private async Task AddContactCommandAsync(string args)
    {
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            AddSystemMessage("用法: /add <节点ID(hex,40位)> [ip:port] [别名]");
            return;
        }

        NodeId nodeId;
        try
        {
            var bytes = Convert.FromHexString(tokens[0]);
            if (bytes.Length != NodeId.Size)
            {
                AddSystemMessage($"节点ID 必须是 {NodeId.Size} 字节（{NodeId.Size * 2} 位十六进制），当前 {bytes.Length} 字节");
                return;
            }
            nodeId = new NodeId(bytes);
        }
        catch (FormatException)
        {
            AddSystemMessage($"节点ID 不是合法的十六进制串: {tokens[0]}");
            return;
        }

        // 第二个 token 若能解析成 ip:port 就当作端点，否则当作别名。
        System.Net.IPEndPoint? endPoint = null;
        var aliasStart = 1;
        if (tokens.Length > 1 && Core.Extensions.EndpointText.TryParse(tokens[1], out var parsed))
        {
            endPoint = parsed;
            aliasStart = 2;
        }

        var alias = tokens.Length > aliasStart ? string.Join(' ', tokens[aliasStart..]) : "未知";
        var endPointText = endPoint is null ? null : Core.Extensions.EndpointText.Format(endPoint);

        await contactService.AddContactAsync(nodeId, alias, endPointText);
        await RefreshContactsAsync();

        if (endPoint is not null)
        {
            // 登记为静态对端 → FindNodeAsync 直接命中，不经过 DHT。
            dhtService.RegisterStaticPeer(new NodeInfo
            {
                NodeId = nodeId,
                EndPoint = endPoint,
                // 手工 /add 只知道 ip:port，对端长期公钥未知 → 显式 null（而不是空数组占位）。
                // 需要它的地方（群邀请包装群密钥）会主动握手取回。
                PublicKey = null,
                State = PeerState.Online
            });
            AddSystemMessage($"已添加联系人: {alias} ({Short(nodeId.ToHexString())}) @ {endPointText}");
            AddSystemMessage($"已登记为静态对端，现在可直接 /msg {alias} <消息>");
        }
        else
        {
            AddSystemMessage($"已添加联系人: {alias} ({Short(nodeId.ToHexString())})");
            AddSystemMessage("提示: 未指定 ip:port。公共 DHT 无法发现任意节点，发送时很可能报「目标节点未找到」。");
            AddSystemMessage("      建议改用: /add <节点ID> <ip:port> [别名]");
        }
    }
    /// <summary>
    /// <c>/connect <ip:port></c> —— 不预先知道对端节点 ID 的直连。
    /// <para>
    /// 思路：构造一个 NodeId 随机但 EndPoint 已知的临时 <see cref="NodeInfo"/>，
    /// 通过路由器建立一条 TCP 连接，发一次 <see cref="KeyExchangeMessage"/> 充当 hello；
    /// 读取对端的响应信封，从中拿到 <c>envelope.SenderId</c> = 对端真实 NodeId
    /// （由 <see cref="Handlers.KeyExchangeHandler"/> 写入），随后：
    /// </para>
    /// <list type="number">
    ///   <item>用本地临时私钥 + 响应中的对端临时公钥派生会话密钥并写入 <see cref="IKeyStore"/>；</item>
    ///   <item>以「真实 NodeId + 已知 ip:port」重新登记为静态对端；</item>
    ///   <item>作为联系人落盘（若尚不存在）。</item>
    /// </list>
    /// <para>
    /// 之后 <c>/msg <别名> <消息></c> 会直接命中 <see cref="IDhtService.FindNodeAsync"/> 中的静态对端，
    /// 跳过 DHT 查找与会话密钥重协商。
    /// </para>
    /// <para>
    /// 临时连接本身在使用后立刻关闭 —— 路由器连接池以 NodeId 为键，下一次 /msg
    /// 会以「真实 NodeId」重新建立连接，因此原连接不进入池。
    /// </para>
    /// </summary>
    private async Task ConnectCommandAsync(string args)
    {
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 1)
        {
            AddSystemMessage("用法: /connect <ip:port>");
            return;
        }
        if (!Core.Extensions.EndpointText.TryParse(tokens[0], out var endPoint))
        {
            AddSystemMessage($"无法解析端点: {tokens[0]}（必须是字面 ip:port，不做 DNS）");
            return;
        }

        var identity = keyStore.GetOrCreateIdentity();
        var ephemeral = encryption.GenerateKeyPair();
        var tempId = NodeId.CreateRandom();
        var probeInfo = new NodeInfo
        {
            NodeId = tempId,
            EndPoint = endPoint,
            // 未知就是未知：这里**不能**填本机公钥。probeInfo 描述的是对端，
            // 填本机公钥会让 NodeId.FromPublicKey(PublicKey) 算出我们的 NodeId 而不是对端的。
            // MessageRouter.GetOrCreateConnectionAsync 只用 NodeId + EndPoint，null 无影响。
            PublicKey = null,
            State = PeerState.Online
        };

        AddSystemMessage($"尝试直连 {endPoint}，等待 hello 响应（最长 10s）...");
        logger.LogInformation("/connect 发起 hello 到 {EndPoint}", endPoint);

        ITcpConnection? connection = null;
        var swHello = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var helloCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            connection = await router.GetOrCreateConnectionAsync(probeInfo, helloCts.Token);
            var hello = new KeyExchangeMessage
            {
                SenderId = identity.NodeId.ToByteArray(),
                ConversationId = $"connect-{tempId.ToHexString()[..8]}",
                EphemeralPublicKey = ephemeral.PublicKey,
                IsResponse = false
            };
            await router.SendViaConnectionAsync(connection, hello, helloCts.Token);

            var (response, peerPublicKey) = await ReadHelloResponseAsync(connection, helloCts.Token);
            var peerId = new NodeId(response.SenderId);
            var peerHex = peerId.ToHexString();

            // 用响应中的对端临时公钥派生会话密钥，双方 ECDH 派生结果一致。
            var sharedSecret = encryption.DeriveSharedSecret(ephemeral.PrivateKey, response.EphemeralPublicKey);
            var sessionKey = encryption.DeriveSessionKey(sharedSecret);
            keyStore.SetSessionKey(peerId, sessionKey);

            // 重新登记为「真实 NodeId + 已知端点」的静态对端：连接池下一次会用真 ID 建连。
            dhtService.RegisterStaticPeer(new NodeInfo
            {
                NodeId = peerId,
                EndPoint = endPoint,
                // 这里**能**拿到对端真实长期公钥：hello 响应是一条已通过 MessageRouter 验签的
                // 签名信封，其 SenderPublicKey 被纳入 ECDSA 签名覆盖，且
                // VerifyEnvelopeCore 已强制 NodeId.FromPublicKey(公钥) == SenderId —— 即公钥与身份已绑定。
                // （历史上这里填的是**本机**公钥当「占位」，会让任何 NodeId.FromPublicKey(node.PublicKey)
                //   算出来的都是我们的 NodeId，对端防冒名守卫在路由表/静态对端层形同虚设。）
                // 取不到时才为 null，由 GroupChatService 在需要包装群密钥时主动握手取回。
                PublicKey = peerPublicKey,
                State = PeerState.Online
            });

            // 联系人落盘
            var alias = $"peer-{peerHex[..8]}";
            var existing = contactService.FindByNodeId(peerId);
            if (existing is null)
            {
                await contactService.AddContactAsync(peerId, alias, Core.Extensions.EndpointText.Format(endPoint));
                await RefreshContactsAsync();
            }
            else
            {
                alias = existing.Alias;
            }

            AddSystemMessage($"已连接到 {endPoint}（耗时 {swHello.ElapsedMilliseconds}ms）");
            AddSystemMessage($"对端 NodeId: {peerHex}");
            AddSystemMessage($"已登记为静态对端 + 联系人（{alias}），可使用 /msg {alias} <消息>");
            logger.LogInformation("/connect 成功: peer {Peer} @ {EndPoint}", peerHex, endPoint);
        }
        catch (OperationCanceledException)
        {
            AddSystemMessage($"/connect 超时：{endPoint} 在 10s 内未回应 hello");
            logger.LogWarning("/connect 超时: {EndPoint}", endPoint);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"/connect 失败: {ex.Message}");
            logger.LogWarning(ex, "/connect 失败: {EndPoint}", endPoint);
        }
        finally
        {
            if (connection is not null)
            {
                try { await connection.DisposeAsync(); } catch { }
            }
        }
    }
    /// <summary>
    /// 沿给定 TCP 连接读取消息，直到拿到 <see cref="KeyExchangeMessage.IsResponse"/> = true 的应答，
    /// 其他类型（理论上不应出现）静默丢弃并继续读取。
    /// 镜像 <c>ChatService.ReadKeyExchangeResponseAsync</c> 的语义但只取本节点的 hello 响应。
    /// <para>
    /// <b>⚠️ 信封格式只有一份实现</b>：必须用 <c>Core.Extensions.EnvelopeCodec</c>，
    /// 绝不能在本文件里再抄一份解析器。历史事故：本文件曾自带一份「50 字节固定头直接切
    /// Payload」的解析器副本，阶段 3.2 引入签名（公钥长度前缀 + 签名前缀）后没有同步更新，
    /// 导致 <c>/connect &lt;ip:port&gt;</c> 盲连接的 hello 响应必然解析失败。
    /// UI 层不引用 Chat 层，所以正确解法是把编解码收敛到 Core 依赖图的根，而不是再抄一份。
    /// </para>
    /// <para>
    /// 返回值第二项是对端的<b>长期公钥</b>，取自该条已验签响应信封的
    /// <c>MessageEnvelope.SenderPublicKey</c>。它被纳入 ECDSA 签名覆盖，且
    /// <c>MessageRouter.VerifyEnvelopeCore</c> 已强制
    /// <c>NodeId.FromPublicKey(公钥) == SenderId</c>，因此「公钥 ↔ 身份」的绑定是可信的。
    /// 载荷里的 <c>message.SenderId</c> 是对端可控输入，<b>不能</b>用来推导身份。
    /// </para>
    /// </summary>
    private async Task<(KeyExchangeMessage Message, byte[]? SenderPublicKey)> ReadHelloResponseAsync(
        ITcpConnection connection, CancellationToken ct)
    {
        var serializer = new MessagePackSerializer();
        while (true)
        {
            var raw = await connection.ReceiveMessageAsync(ct);
            var envelope = EnvelopeCodec.Deserialize(raw);
            if (envelope.MessageType != MessageType.KeyExchange) continue;
            var msg = serializer.Deserialize<Message>(envelope.Payload);
            if (msg is KeyExchangeMessage { IsResponse: true } response)
                return (response, envelope.SenderPublicKey);
        }
    }
    private async Task HandleTransferAsync(string transferId, bool accept)
    {
        try
        {
            if (accept)
            {
                await fileTransferService.AcceptTransferAsync(transferId, Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
                AddSystemMessage($"文件传输已接受: {Short(transferId)}, 保存到桌面");
            }
            else
            {
                await fileTransferService.RejectTransferAsync(transferId);
                AddSystemMessage($"文件传输已拒绝: {Short(transferId)}");
            }
        }
        catch (Exception ex) { AddSystemMessage($"{(accept ? "接受" : "拒绝")}失败: {ex.Message}"); }
    }
    private void ShowNodeInfo()
    {
        var local = dhtService.LocalNode;
        int contacts;
        lock (_sync) contacts = _contacts.Count;
        AddSystemMessage("====== 本机节点信息 ======");
        AddSystemMessage($"  节点ID: {local.NodeId.ToHexString()}");
        AddSystemMessage($"  简写:   {Short(local.NodeId.ToHexString())}    TCP: {local.EndPoint}");
        AddSystemMessage($"  公钥:   {Short(Convert.ToBase64String(keyStore.GetOrCreateIdentity().PublicKey), 40)}...");
        AddSystemMessage($"  联系人: {contacts} / 群组: {groupChatService.GetKnownGroups().Count}");
    }
    private void ShowDhtStatus()
    {
        var allNodes = dhtService.GetAllKnownNodes();
        var online = allNodes.Count(n => n.State == PeerState.Online);
        AddSystemMessage("====== DHT 网络状态 ======");
        AddSystemMessage($"  已知节点总数: {allNodes.Count}   在线: {online}   离线: {allNodes.Count - online}");
        AddSystemMessage($"  本机监听:     {dhtService.LocalNode.EndPoint}");
        foreach (var n in allNodes.Take(10))
            AddSystemMessage($"    [{(n.State == PeerState.Online ? "+" : "-")}] {Short(n.NodeId.ToHexString())} @ {n.EndPoint}");
        if (allNodes.Count > 10) AddSystemMessage($"    ... 还有 {allNodes.Count - 10} 个节点");
    }
    private async Task ShowContactsListAsync()
    {
        var contacts = await RefreshContactsAsync();
        AddSystemMessage($"联系人列表 ({contacts.Count}):");
        foreach (var c in contacts)
            AddSystemMessage($"  {c.Alias} - {Short(c.NodeId.ToHexString())} [{(c.IsOnline ? "●在线" : "○离线")}]");
    }
    private void ShowHelp()
    {
        AddSystemMessage("====== 命令列表 ======");
        AddSystemMessage("  /id 或 /me 查看本机节点ID与公钥 | /net 或 /dht 查看DHT网络状态 | /contacts 联系人列表");
        AddSystemMessage("  /msg 或 /pm <联系人|节点ID> <消息>   发送私聊消息");
        AddSystemMessage("  /group create <名称> | /group send <群ID前缀> <消息> | /group list 列出已知群组");
        AddSystemMessage("  /file send <联系人> <文件路径> | /accept <传输ID> 接受 | /reject <传输ID> 拒绝");
        AddSystemMessage("  /add <节点ID(hex,40位)> [ip:port] [别名]   添加联系人（给出 ip:port 可免 DHT 直连）");
        AddSystemMessage("  /connect <ip:port>   直连对端（不需知道对方节点ID，hello 握手后自动登记）");
        AddSystemMessage("  /help 帮助 | /quit 退出");
        AddSystemMessage("快捷键: F1=帮助 F2=添加联系人 F3=新建群组 F10=退出 Tab=切换联系人 PgUp/PgDn=滚动聊天");
        AddSystemMessage("P2PChat v1.1 - 基于Kademlia DHT的P2P聊天软件 (.NET 11 / 自绘控制台UI / AES-256-GCM)");
    }
    private Contact? FindContact(string target) => _contacts.FirstOrDefault(c =>
        c.Alias.Equals(target, StringComparison.OrdinalIgnoreCase) ||
        c.NodeId.ToHexString().StartsWith(target, StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<Contact>> RefreshContactsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<Contact> contacts;
        try { contacts = await contactService.GetAllContactsAsync(ct); }
        catch (Exception ex) { logger.LogWarning(ex, "加载联系人失败"); return []; }
        lock (_sync) { _contacts.Clear(); _contacts.AddRange(contacts); }
        MarkDirty();
        return contacts;
    }
    private void RefreshDhtStatus()
    {
        var allNodes = dhtService.GetAllKnownNodes();
        var online = allNodes.Count(n => n.State == PeerState.Online);
        int contacts;
        lock (_sync) contacts = _contacts.Count;
        _dhtStatus = $"DHT: {online}/{allNodes.Count} | ID:{Short(dhtService.LocalNode.NodeId.ToHexString(), 6)} | 联系人:{contacts}";
        MarkDirty();
    }
    /// <summary>后台循环 1：接收聊天消息 → 入队给 UI 线程渲染</summary>
    private async Task ProcessIncomingMessagesAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var chatEvent in chatService.OnMessageReceived.WithCancellation(ct))
            {
                var senderName = chatEvent.IsOutgoing ? "我"
                    : (contactService.FindByNodeId(chatEvent.SenderId)?.Alias ?? Short(chatEvent.SenderId.ToHexString()));
                var prefix = chatEvent.IsGroup ? $"[{Short(chatEvent.ConversationId, 4)}]" : "";
                var time = chatEvent.Timestamp.ToLocalTime().ToString("HH:mm");
                _inbox.Enqueue(($"{prefix}[{time}] {senderName}: {chatEvent.Content}", chatEvent.ConversationId));
                MarkDirty();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogWarning(ex, "接收消息循环异常"); }
    }
    /// <summary>后台循环 2：接收文件 Offer → 入队确认提示（保持原 ProcessFileOffersAsync 语义）</summary>
    private async Task ProcessFileOffersAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var offer in fileTransferService.OnFileOfferReceived.WithCancellation(ct))
            {
                _inbox.Enqueue(($"[系统] 收到文件Offer: {offer.FileName} ({offer.FileSize / 1024}KB)", ""));
                _inbox.Enqueue(($"[系统]   传输ID: {offer.TransferId}", ""));
                _inbox.Enqueue(($"[系统]   使用 /accept {Short(offer.TransferId)} 接受或 /reject {Short(offer.TransferId)} 拒绝", ""));
                MarkDirty();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogWarning(ex, "接收文件Offer循环异常"); }
    }
    /// <summary>后台循环 3：每 5 秒刷新 DHT 状态与联系人在线状态（保持原 RefreshDhtPeriodicallyAsync 语义）</summary>
    private async Task RefreshDhtPeriodicallyAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(5000, ct);
                RefreshDhtStatus();
                await RefreshContactsAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogWarning(ex, "DHT状态刷新循环异常"); }
    }
    private void DrainInbox()
    {
        while (_inbox.TryDequeue(out var item))
        {
            if (_plainMode)
            {
                Console.WriteLine(item.Cid.Length > 0 ? $"[{Short(item.Cid)}] {item.Text}" : item.Text);
                // plain 模式的**存在意义**就是让脚本（e2e-verify.ps1、CI、管道）抓取输出。
                // 而 stdout 被重定向时 Console.Out 是**带缓冲**的：脚本在读到期望内容后
                // 直接 Kill 进程，缓冲区里还没落盘的聊天事件行就**整个丢失**。
                // 症状与「消息没被渲染」几乎一样（Serilog 文件日志里有、stdout 里没有），
                // 极具误导性。plain 模式本就是机器消费路径，逐行 flush 的开销可以接受。
                // （注：2026-09-28 首次加这行时，我把它误判成 A33 失败的原因；真实原因是
                //   handler 的事件压根没进 `_inbox`。修复见 IChatEventPublisher。此处的 flush 独立成立。）
                Console.Out.Flush();
            }
            else AddMessage(item.Text, item.Cid.Length > 0 ? item.Cid : null);
        }
    }
    private void MarkDirty() => _dirty = true;
    /// <summary>重绘整屏；控制台不可用时降级为线性输出模式</summary>
    private void Render()
    {
        if (_plainMode || !_dirty) return;
        try
        {
            _screen.Draw(_dhtStatus, _currentChatTitle, BuildContactLines(), CurrentMessages(), _scrollFromEnd, _input,
                _currentConversationId.Length > 0 ? Short(_currentConversationId) : "无");
            _dirty = false;
        }
        catch (InvalidOperationException ex) { _plainMode = true; logger.LogWarning(ex, "控制台全屏渲染不可用，改用线性输出模式"); }
        catch (Exception ex) { logger.LogDebug(ex, "渲染本帧失败（窗口尺寸/光标越界），下一帧重试"); }
    }
    private string[] CurrentMessages()
    {
        lock (_sync)
        {
            var cid = _currentConversationId.Length > 0 ? _currentConversationId : SystemConversation;
            return _messageHistory.TryGetValue(cid, out var list) ? list.ToArray() : [];
        }
    }
    private List<string> BuildContactLines()
    {
        Contact[] contacts;
        lock (_sync) contacts = _contacts.ToArray();
        var lines = new List<string>(contacts.Length + 1) { $"({contacts.Length} 位联系人)" };
        foreach (var c in contacts) lines.Add($"{(c.IsOnline ? "●" : "○")} {c.Alias} {Short(c.NodeId.ToHexString(), 6)}");
        if (contacts.Length == 0) lines.Add("使用 /add 添加联系人");
        return lines;
    }
    private void AddMessage(string text, string? conversationId = null)
    {
        var cid = string.IsNullOrEmpty(conversationId)
            ? (_currentConversationId.Length > 0 ? _currentConversationId : SystemConversation)
            : conversationId;
        lock (_sync)
        {
            if (!_messageHistory.TryGetValue(cid, out var list)) _messageHistory[cid] = list = [];
            list.Add(text);
            if (list.Count > MaxHistory) list.RemoveRange(0, list.Count - MaxHistory);
        }
        MarkDirty();
    }
    private void AddSystemMessage(string text) => AddMessage($"[系统] {text}");
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        if (!_plainMode) { try { Console.ResetColor(); Console.CursorVisible = true; } catch { } }
    }
}
