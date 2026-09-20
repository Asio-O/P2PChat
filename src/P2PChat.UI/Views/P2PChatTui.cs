using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;
using static P2PChat.UI.Views.ConsoleScreen;

namespace P2PChat.UI.Views;

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
    IKeyStore keyStore,
    ILogger<P2PChatTui> logger) : IDisposable
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
    private string _input = string.Empty;
    private int _contactIndex, _scrollFromEnd;
    private bool _disposed;
    /// <summary>启动 TUI；P2PCHAT_SELFTEST=1 时只打印自检信息后立即返回（不进入阻塞输入循环）</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        RefreshDhtStatus();
        await RefreshContactsAsync(ct);
        if (Environment.GetEnvironmentVariable("P2PCHAT_SELFTEST") == "1") { await RunSelfTestAsync(ct); return; }
        _running = true;
        try { Console.Title = "P2PChat - P2P DHT Chat"; Console.Clear(); }
        catch (Exception ex) { _plainMode = true; logger.LogDebug(ex, "控制台初始化失败，改用线性输出模式"); }
        _ = ProcessIncomingMessagesAsync(ct);   // 后台循环 1：接收聊天消息
        _ = ProcessFileOffersAsync(ct);         // 后台循环 2：接收文件 Offer
        _ = RefreshDhtPeriodicallyAsync(ct);    // 后台循环 3：周期性刷新 DHT / 在线状态
        try
        {
            while (_running && !ct.IsCancellationRequested)
            {
                DrainInbox();
                Render();
                var key = ReadKeyOrNull();
                if (key is { } k) { HandleKey(k); continue; }
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
    /// <summary>非交互自检：打印节点ID / 监听端口 / DHT已知节点数 / 联系人 / 群组数</summary>

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
        Console.WriteLine("自检完成:   OK");
        Console.Out.Flush();
    }
    private ConsoleKeyInfo? ReadKeyOrNull()
    {
        if (_plainMode) return null;
        try { return Console.KeyAvailable ? Console.ReadKey(intercept: true) : null; }
        catch (Exception) { _plainMode = true; return null; }
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
            if (cmd is "msg" or "pm" or "file" or "add" or "accept" or "reject" or "group" && args.Length == 0)
            {
                AddSystemMessage(cmd is "msg" or "pm" ? "用法: /msg <联系人|节点ID> <消息>"
                    : cmd == "file" ? "用法: /file send <联系人> <文件路径>"
                    : cmd == "add" ? "用法: /add <节点ID(hex,40位)> [ip:port] [别名]"
                    : cmd is "accept" or "reject" ? $"用法: /{cmd} <传输ID>"
                    : "用法: /group create <名称> 或 /group send <ID> <消息>");
                return;
            }
            switch (cmd)
            {
                case "msg" or "pm": await SendPrivateMessageByCommandAsync(args); break;
                case "group": await HandleGroupCommandAsync(args); break;
                case "file": await HandleFileCommandAsync(args); break;
                case "add": await AddContactCommandAsync(args); break;
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
                PublicKey = [],
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
            if (_plainMode) Console.WriteLine(item.Cid.Length > 0 ? $"[{Short(item.Cid)}] {item.Text}" : item.Text);
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
