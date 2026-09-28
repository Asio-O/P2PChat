#Requires -Version 7.0
<#
.SYNOPSIS
  P2PChat 双节点端到端运行验证（task-5 阶段A）。

.DESCRIPTION
  拉起两个互不干扰的真实实例并断言运行逻辑。

  隔离方式（重要）：
    * 严禁复制 exe。所有节点一律共用同一个固定 exe 路径 —— 复制 exe 会让 Windows 防火墙
      对每个新程序路径重复弹出安全警报（已由 Lead 定位为用户投诉的根因）。
    * 数据隔离使用环境变量 P2PCHAT_DATA_DIR（见 src/P2PChat.Core/Extensions/DataPath.cs）。
      程序默认把运行时数据放在用户主目录的 .p2pc/ 下；同一台机器上跑多个实例时必须用
      P2PCHAT_DATA_DIR 为每个实例指定独立目录，否则两个节点会共用同一份 identity.json
      而导致节点ID 相同、无法作为对等体互相发现。
    * 端口隔离使用 P2PCHAT_P2PChat__UdpPort / P2PCHAT_P2PChat__TcpPort（已实测生效）。
    * 默认启用自检模式 P2PCHAT_SELFTEST=1（见 src/P2PChat.UI/Views/P2PChatTui.cs:42），
      实例打印状态后自行退出，避免遗留阻塞的 TUI 进程；
      P2PCHAT_SELFTEST_WAIT 控制等待 DHT 的毫秒数（默认 30000）。
    * 无论成功失败，finally 都会收掉自己启动的进程，不留挂死实例。

  双节点互动：先启动 B，再让 A 以 B 的 UDP 端口作为唯一自配引导节点
  （P2PCHAT_P2PChat__BootstrapNodes__0=127.0.0.1:<B的UDP端口>），
  从而断言"A 发出 PING -> B 响应 -> A 引导完成"的真实 UDP 往返链路。

  用法:
    pwsh -NoProfile -File scripts\e2e-verify.ps1                     # 默认自检模式
    pwsh -NoProfile -File scripts\e2e-verify.ps1 -SelfTestWaitMs 40000
    pwsh -NoProfile -File scripts\e2e-verify.ps1 -NoSelfTest         # 对照：常驻进程存活断言（会被兜底强杀）
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [string]$WorkRoot,
    [int]$NodeAUdpPort = 20081,
    [int]$NodeATcpPort = 20091,
    [int]$NodeBUdpPort = 20082,
    [int]$NodeBTcpPort = 20092,
    [int]$WaitExitSeconds = 90,
    [int]$SelfTestWaitMs = 30000,
    [switch]$NoSelfTest
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ExePath) {
    $ExePath = Join-Path $repoRoot 'src\P2PChat.App\bin\Release\net11.0\win-x64\publish\P2PChat.App.exe'
}
if (-not $WorkRoot) {
    $WorkRoot = Join-Path $env:TEMP ("p2pchat-e2e-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
$WorkRoot = [System.IO.Path]::GetFullPath($WorkRoot)

$useSelfTest = -not $NoSelfTest
$dataDirA = Join-Path $WorkRoot 'dataA'
$dataDirB = Join-Path $WorkRoot 'dataB'
$runDirA = Join-Path $WorkRoot 'runA'
$runDirB = Join-Path $WorkRoot 'runB'

$script:Results = @()
function Add-Result {
    param([string]$Id, [string]$Desc, [ValidateSet('PASS', 'FAIL', 'SKIP', 'WARN')][string]$Status, [string]$Evidence)
    $script:Results += [pscustomobject]@{ Id = $Id; Desc = $Desc; Status = $Status; Evidence = $Evidence }
}

$script:Nodes = @()
function Stop-AllNodes {
    foreach ($n in $script:Nodes) {
        try {
            if ($n.Proc) {
                if (-not $n.Proc.HasExited) {
                    $n.Proc.Kill($true)
                    $null = $n.Proc.WaitForExit(5000)
                    $n.KilledByScript = $true
                }
            }
        } catch { }
    }
}

function Read-NodeLog {
    param([string]$DataDir)
    $logDir = Join-Path $DataDir 'logs'
    if (-not (Test-Path -LiteralPath $logDir)) { return '' }
    $files = Get-ChildItem -LiteralPath $logDir -Filter 'p2pchat-*.log' -File -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime -Descending
    $sb = [System.Text.StringBuilder]::new()
    foreach ($f in $files) {
        for ($i = 0; $i -lt 4; $i++) {
            try {
                [void]$sb.AppendLine((Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8))
                break
            } catch { Start-Sleep -Milliseconds 120 }
        }
    }
    return $sb.ToString()
}

function Start-P2PNode {
    param(
        [string]$Name,
        [string]$DataDir,
        [string]$RunDir,
        [hashtable]$EnvVars,
        # 保留已存在的 $DataDir 内容（只确保目录存在）。
        # 供「先预置 contacts.json 再启动」的场景使用 —— 否则本函数开头的
        # Remove-Item -Recurse 会把刚写好的 contacts.json 删掉，
        # 导致节点以「零联系人」启动，后续 /msg <别名> 必然失败且毫无日志线索。
        [switch]$PreserveDataDir
    )
    if ($PreserveDataDir) {
        New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    }
    else {
        if (Test-Path -LiteralPath $DataDir) { Remove-Item -LiteralPath $DataDir -Recurse -Force }
        New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    }
    if (Test-Path -LiteralPath $RunDir) { Remove-Item -LiteralPath $RunDir -Recurse -Force }
    New-Item -ItemType Directory -Path $RunDir -Force | Out-Null

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $ExePath            # 固定同一路径：绝不复制 exe
    $psi.WorkingDirectory = $RunDir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardInput = $true  # 保持打开不写入 => 无人值守
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    foreach ($k in $EnvVars.Keys) { $psi.Environment[$k] = [string]$EnvVars[$k] }

    $proc = [System.Diagnostics.Process]::Start($psi)
    $node = [pscustomobject]@{
        Name          = $Name
        ExePath       = $psi.FileName
        DataDir       = $DataDir
        RunDir        = $RunDir
        Proc          = $proc
        OutTask       = $proc.StandardOutput.ReadToEndAsync()
        ErrTask       = $proc.StandardError.ReadToEndAsync()
        KilledByScript = $false
        EnvVars       = $EnvVars
    }
    $script:Nodes += $node
    return $node
}

function Get-NodeOutput {
    param($Node)
    $stdout = ''
    try { $stdout = $Node.OutTask.GetAwaiter().GetResult() } catch { $stdout = "<stdout 读取失败: $($_.Exception.Message)>" }
    $stderr = ''
    try { $stderr = $Node.ErrTask.GetAwaiter().GetResult() } catch { $stderr = "<stderr 读取失败: $($_.Exception.Message)>" }
    $log = Read-NodeLog -DataDir $Node.DataDir
    $fx = Join-Path $Node.RunDir 'e2e.stdout.txt'
    Set-Content -LiteralPath $fx -Value $stdout -Encoding UTF8
    $fe = Join-Path $Node.RunDir 'e2e.stderr.txt'
    Set-Content -LiteralPath $fe -Value $stderr -Encoding UTF8
    return [pscustomobject]@{ StdOut = $stdout; StdErr = $stderr; Log = $log; Combined = ($stdout + "`n" + $log) }
}

function Get-Field {
    param([string]$Text, [string]$Pattern)
    $m = [regex]::Match($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if ($m.Success) { return $m.Groups[1].Value.Trim() } else { return '' }
}

function Test-PortFree {
    param([int]$Port, [ValidateSet('Tcp', 'Udp')][string]$Proto)
    try {
        if ($Proto -eq 'Tcp') {
            $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any, $Port)
            $l.Start(); $l.Stop()
        } else {
            $u = [System.Net.Sockets.UdpClient]::new($Port)
            $u.Close()
        }
        return $true
    } catch { return $false }
}

function Wait-Until {
    param([scriptblock]$Condition, [int]$TimeoutSeconds, [int]$IntervalMs = 500)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        try { if (& $Condition) { return $true } } catch { }
        Start-Sleep -Milliseconds $IntervalMs
    }
    return $false
}

# ============================================================================
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host " P2PChat 双节点端到端验证 (e2e-verify.ps1)" -ForegroundColor Cyan
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host "exe      : $ExePath   (固定路径，不复制)"
Write-Host "workRoot : $WorkRoot"
Write-Host "mode     : $(if ($useSelfTest) { "自检模式 SELFTEST=1 (wait=${SelfTestWaitMs}ms)" } else { '常驻模式（对照，会被兜底强杀）' })"
Write-Host "nodeA    : UDP=$NodeAUdpPort TCP=$NodeATcpPort data=$dataDirA"
Write-Host "nodeB    : UDP=$NodeBUdpPort TCP=$NodeBTcpPort data=$dataDirB"
Write-Host ""

$tcpProbeA = $null
$tcpProbeB = $null
$tcpProbeOk = @{ A = $false; B = $false; AErr = ''; BErr = '' }
$exitCodeA = $null
$exitCodeB = $null

try {
    # ---- 0. 端口预检 -------------------------------------------------------
    Write-Host "[0/7] 端口预检..." -ForegroundColor Yellow
    $busy = @()
    foreach ($pp in @(
            @{ P = $NodeAUdpPort; Proto = 'Udp'; Who = 'nodeA' },
            @{ P = $NodeATcpPort; Proto = 'Tcp'; Who = 'nodeA' },
            @{ P = $NodeBUdpPort; Proto = 'Udp'; Who = 'nodeB' },
            @{ P = $NodeBTcpPort; Proto = 'Tcp'; Who = 'nodeB' })) {
        if (-not (Test-PortFree -Port $pp.P -Proto $pp.Proto)) { $busy += "$($pp.Who)/$($pp.Proto)/$($pp.P)" }
    }
    if ($busy.Count -eq 0) {
        Add-Result 'A00' '端口预检：4 个端口均空闲' 'PASS' "UDP $NodeAUdpPort/$NodeBUdpPort, TCP $NodeATcpPort/$NodeBTcpPort"
    } else {
        Add-Result 'A00' '端口预检：4 个端口均空闲' 'FAIL' ("被占用: " + ($busy -join ', '))
    }

    # ---- 1. 启动 nodeB（先启动，作为引导目标） ------------------------------
    Write-Host "[1/7] 启动 nodeB（UDP $NodeBUdpPort / TCP $NodeBTcpPort / data=$dataDirB）..." -ForegroundColor Yellow
    $envB = @{
        'P2PCHAT_DATA_DIR'          = $dataDirB
        'P2PCHAT_P2PChat__UdpPort'  = "$NodeBUdpPort"
        'P2PCHAT_P2PChat__TcpPort'  = "$NodeBTcpPort"
    }
    if ($useSelfTest) {
        $envB['P2PCHAT_SELFTEST'] = '1'
        $envB['P2PCHAT_SELFTEST_WAIT'] = "$SelfTestWaitMs"
    }
    $nodeB = Start-P2PNode -Name 'nodeB' -DataDir $dataDirB -RunDir $runDirB -EnvVars $envB

    $bReady = Wait-Until -TimeoutSeconds 25 -Condition {
        (Read-NodeLog -DataDir $dataDirB) -match "UDP传输绑定到 0\.0\.0\.0:$NodeBUdpPort"
    }
    Write-Host "    -> nodeB UDP 就绪: $bReady" -ForegroundColor $(if ($bReady) { 'Green' } else { 'Red' })
    if ($bReady) { Start-Sleep -Seconds 2 }   # 留出 nodeB 启动 DHT 接收循环的时间（UDP 已绑定，内核会缓冲入包）

    # ---- 2. 启动 nodeA（以 nodeB 为引导节点） ------------------------------
    Write-Host "[2/7] 启动 nodeA（UDP $NodeAUdpPort / TCP $NodeATcpPort / data=$dataDirA，引导 -> 127.0.0.1:$NodeBUdpPort）..." -ForegroundColor Yellow
    $envA = @{
        'P2PCHAT_DATA_DIR'                   = $dataDirA
        'P2PCHAT_P2PChat__UdpPort'           = "$NodeAUdpPort"
        'P2PCHAT_P2PChat__TcpPort'           = "$NodeATcpPort"
        'P2PCHAT_P2PChat__BootstrapNodes__0' = "127.0.0.1:$NodeBUdpPort"
    }
    if ($useSelfTest) {
        $envA['P2PCHAT_SELFTEST'] = '1'
        $envA['P2PCHAT_SELFTEST_WAIT'] = "$SelfTestWaitMs"
    }
    $nodeA = Start-P2PNode -Name 'nodeA' -DataDir $dataDirA -RunDir $runDirA -EnvVars $envA

    # ---- 3. 等待 DHT 往返 --------------------------------------------------
    Write-Host "[3/7] 等待真实双节点 UDP 往返（最多 45s）..." -ForegroundColor Yellow
    $swRound = [System.Diagnostics.Stopwatch]::StartNew()
    $sawResponse = Wait-Until -TimeoutSeconds 45 -IntervalMs 500 -Condition {
        (Read-NodeLog -DataDir $dataDirA) -match "引导节点 127\.0\.0\.1:$NodeBUdpPort 响应成功"
    }
    Write-Host "    -> 观测到引导响应成功: $sawResponse（耗时 $([math]::Round($swRound.Elapsed.TotalSeconds,1))s）" -ForegroundColor $(if ($sawResponse) { 'Green' } else { 'Red' })

    # ---- 4. TCP 连通性探测（保持连接，避免对端读到 EOF 产生 ERR 日志） -----
    Write-Host "[4/7] TCP 连通性探测（保持连接到进程退出）..." -ForegroundColor Yellow
    foreach ($n in @(@{ K = 'A'; Name = 'nodeA'; Port = $NodeATcpPort; Proc = $nodeA.Proc }, @{ K = 'B'; Name = 'nodeB'; Port = $NodeBTcpPort; Proc = $nodeB.Proc })) {
        if ($n.Proc.HasExited) { $tcpProbeOk[$n.K + 'Err'] = '进程已退出，跳过连接'; continue }
        try {
            $c = [System.Net.Sockets.TcpClient]::new()
            $iar = $c.BeginConnect('127.0.0.1', $n.Port, $null, $null)
            if ($iar.AsyncWaitHandle.WaitOne(3000)) { $c.EndConnect($iar); $tcpProbeOk[$n.K] = $c.Connected } else { $tcpProbeOk[$n.K + 'Err'] = 'connect 超时' }
            if ($n.K -eq 'A') { $tcpProbeA = $c } else { $tcpProbeB = $c }
        } catch { $tcpProbeOk[$n.K + 'Err'] = $_.Exception.Message }
    }
    Write-Host "    -> nodeA=$($tcpProbeOk['A']) nodeB=$($tcpProbeOk['B'])" -ForegroundColor Yellow

    # ---- 5. 等待进程结束 ---------------------------------------------------
    if ($useSelfTest) {
        Write-Host "[5/7] 等待两节点自检完成后自行退出（最多 ${WaitExitSeconds}s）..." -ForegroundColor Yellow
        $bothExited = Wait-Until -TimeoutSeconds $WaitExitSeconds -IntervalMs 500 -Condition {
            $nodeA.Proc.HasExited -and $nodeB.Proc.HasExited
        }
        Write-Host "    -> 两节点已自行退出: $bothExited (nodeA=$($nodeA.Proc.HasExited) nodeB=$($nodeB.Proc.HasExited))" -ForegroundColor $(if ($bothExited) { 'Green' } else { 'Red' })
    } else {
        Write-Host "[5/7] 常驻模式：观测 5s 存活..." -ForegroundColor Yellow
        Start-Sleep -Seconds 5
        $bothExited = $false
    }

    if ($nodeA.Proc.HasExited) { $exitCodeA = $nodeA.Proc.ExitCode }
    if ($nodeB.Proc.HasExited) { $exitCodeB = $nodeB.Proc.ExitCode }

    # 关闭探测连接
    foreach ($c in @($tcpProbeA, $tcpProbeB)) { try { if ($c) { $c.Close() } } catch { } }

    # ---- 6. 采集输出 -------------------------------------------------------
    Write-Host "[6/7] 采集日志与输出..." -ForegroundColor Yellow
    $outA = Get-NodeOutput -Node $nodeA
    $outB = Get-NodeOutput -Node $nodeB

    $aliveA = -not $nodeA.Proc.HasExited
    $aliveB = -not $nodeB.Proc.HasExited

    # ---- 7. 断言 -----------------------------------------------------------
    # (1) 进程状态 / 自检完成
    if ($useSelfTest) {
        Add-Result 'A01' '自检模式：nodeA 自行退出（无挂死进程）' $(if ($nodeA.Proc.HasExited) { 'PASS' } else { 'FAIL' }) "HasExited=$($nodeA.Proc.HasExited)"
        Add-Result 'A02' '自检模式：nodeB 自行退出（无挂死进程）' $(if ($nodeB.Proc.HasExited) { 'PASS' } else { 'FAIL' }) "HasExited=$($nodeB.Proc.HasExited)"
        Add-Result 'A03' 'nodeA 退出码=0' $(if ($exitCodeA -eq 0) { 'PASS' } else { 'FAIL' }) "ExitCode=$exitCodeA"
        Add-Result 'A04' 'nodeB 退出码=0' $(if ($exitCodeB -eq 0) { 'PASS' } else { 'FAIL' }) "ExitCode=$exitCodeB"
        Add-Result 'A05' 'nodeA 自检块输出 "自检完成:   OK"' $(if ($outA.StdOut -match '自检完成:\s*OK') { 'PASS' } else { 'FAIL' }) $(if ($outA.StdOut -match '自检完成:\s*OK') { 'found' } else { '未找到自检完成标记' })
        Add-Result 'A06' 'nodeB 自检块输出 "自检完成:   OK"' $(if ($outB.StdOut -match '自检完成:\s*OK') { 'PASS' } else { 'FAIL' }) $(if ($outB.StdOut -match '自检完成:\s*OK') { 'found' } else { '未找到自检完成标记' })
    } else {
        Add-Result 'A01' '常驻模式：nodeA 观测期内保持存活' $(if ($aliveA) { 'PASS' } else { 'FAIL' }) "HasExited=$($nodeA.Proc.HasExited) ExitCode=$exitCodeA"
        Add-Result 'A02' '常驻模式：nodeB 观测期内保持存活' $(if ($aliveB) { 'PASS' } else { 'FAIL' }) "HasExited=$($nodeB.Proc.HasExited) ExitCode=$exitCodeB"
        Add-Result 'A03' '常驻模式：nodeA 未异常退出' $(if (-not $nodeA.Proc.HasExited) { 'PASS' } else { 'FAIL' }) "ExitCode=$exitCodeA"
        Add-Result 'A04' '常驻模式：nodeB 未异常退出' $(if (-not $nodeB.Proc.HasExited) { 'PASS' } else { 'FAIL' }) "ExitCode=$exitCodeB"
        Add-Result 'A05' '常驻模式：nodeA 自检块不适用' 'SKIP' '-NoSelfTest'
        Add-Result 'A06' '常驻模式：nodeB 自检块不适用' 'SKIP' '-NoSelfTest'
    }

    # (2) 节点ID
    $idA = Get-Field -Text $outA.Combined -Pattern '节点ID:\s+([0-9a-f]{40})'
    $idB = Get-Field -Text $outB.Combined -Pattern '节点ID:\s+([0-9a-f]{40})'
    Add-Result 'A07' 'nodeA 节点ID 为 40 位 hex' $(if ($idA) { 'PASS' } else { 'FAIL' }) "NodeIdA=$idA"
    Add-Result 'A08' 'nodeB 节点ID 为 40 位 hex' $(if ($idB) { 'PASS' } else { 'FAIL' }) "NodeIdB=$idB"
    Add-Result 'A09' '两节点节点ID 互不相同（P2PCHAT_DATA_DIR 数据隔离生效）' $(if ($idA -and $idB -and $idA -ne $idB) { 'PASS' } else { 'FAIL' }) "A=$idA B=$idB"

    # (3) 端口绑定
    $udpA = Get-Field -Text $outA.Combined -Pattern '实际端口: UDP=(\d+),'
    $tcpA = Get-Field -Text $outA.Combined -Pattern '实际端口: UDP=\d+, TCP=(\d+)'
    $udpB = Get-Field -Text $outB.Combined -Pattern '实际端口: UDP=(\d+),'
    $tcpB = Get-Field -Text $outB.Combined -Pattern '实际端口: UDP=\d+, TCP=(\d+)'
    $busyWarnA = [regex]::IsMatch($outA.Combined, '端口 \d+ 被占用')
    $busyWarnB = [regex]::IsMatch($outB.Combined, '端口 \d+ 被占用')
    Add-Result 'A10' 'nodeA UDP/TCP 绑定到指定端口' $(if ($udpA -eq "$NodeAUdpPort" -and $tcpA -eq "$NodeATcpPort") { 'PASS' } else { 'FAIL' }) "UDP=$udpA TCP=$tcpA 期望 $NodeAUdpPort/$NodeATcpPort 端口回退=$busyWarnA"
    Add-Result 'A11' 'nodeB UDP/TCP 绑定到指定端口' $(if ($udpB -eq "$NodeBUdpPort" -and $tcpB -eq "$NodeBTcpPort") { 'PASS' } else { 'FAIL' }) "UDP=$udpB TCP=$tcpB 期望 $NodeBUdpPort/$NodeBTcpPort 端口回退=$busyWarnB"

    # (4) TCP 监听
    $listenA = Get-Field -Text $outA.Combined -Pattern 'TCP监听已启动: (\S+)'
    $listenB = Get-Field -Text $outB.Combined -Pattern 'TCP监听已启动: (\S+)'
    Add-Result 'A12' 'nodeA TCP 监听成功' $(if ($listenA -eq "0.0.0.0:$NodeATcpPort") { 'PASS' } else { 'FAIL' }) "TCP监听已启动: $listenA"
    Add-Result 'A13' 'nodeB TCP 监听成功' $(if ($listenB -eq "0.0.0.0:$NodeBTcpPort") { 'PASS' } else { 'FAIL' }) "TCP监听已启动: $listenB"

    # (5) DHT 引导 PING + 往返
    $pingCountA = [regex]::Matches($outA.Combined, 'PING 引导节点: \S+').Count
    $pingPeerA = [regex]::IsMatch($outA.Combined, "PING 引导节点: 127\.0\.0\.1:$NodeBUdpPort")
    Add-Result 'A14' 'nodeA DHT 引导发出 PING' $(if ($pingCountA -gt 0) { 'PASS' } else { 'FAIL' }) "PING 行数=$pingCountA"
    Add-Result 'A15' "nodeA PING 到对端 127.0.0.1:$NodeBUdpPort" $(if ($pingPeerA) { 'PASS' } else { 'FAIL' }) "匹配=$pingPeerA"
    $respA = [regex]::IsMatch($outA.Combined, "引导节点 127\.0\.0\.1:$NodeBUdpPort 响应成功")
    Add-Result 'A16' '对端 nodeB 响应 PING 成功（真实双节点 UDP 往返）' $(if ($respA) { 'PASS' } else { 'FAIL' }) "日志匹配=$respA 观测窗口=$sawResponse"
    $bootDoneA = Get-Field -Text $outA.Combined -Pattern 'DHT 引导完成, 路由表: (\d+) 节点'
    $foundA = Get-Field -Text $outA.Combined -Pattern '引导完成, 发现 (\d+) 个节点'
    Add-Result 'A17' 'nodeA 引导完成且路由表节点数 >= 1' $(if ($bootDoneA -and [int]$bootDoneA -ge 1) { 'PASS' } else { 'FAIL' }) "引导完成时路由表节点数=$bootDoneA（同批 FIND_NODE 返回：发现 $foundA 个节点）"
    $knownA = Get-Field -Text $outA.StdOut -Pattern 'DHT已知节点:\s*(\d+)'
    $knownB = Get-Field -Text $outB.StdOut -Pattern 'DHT已知节点:\s*(\d+)'
    Add-Result 'A18' 'nodeA 自检 DHT已知节点 >= 1' $(if ($knownA -and [int]$knownA -ge 1) { 'PASS' } else { 'FAIL' }) "DHT已知节点(A)=$knownA"
    Add-Result 'A19' 'nodeB 自检 DHT已知节点 >= 1（反向证据：B 也学到 A）' $(if ($knownB -and [int]$knownB -ge 1) { 'PASS' } else { 'FAIL' }) "DHT已知节点(B)=$knownB"

    # (6) 消息处理器注册
    $handlers = @('PrivateText', 'GroupText', 'KeyExchange', 'GroupInvite', 'GroupNotify')
    foreach ($n in @(@{ N = 'nodeA'; O = $outA }, @{ N = 'nodeB'; O = $outB })) {
        $missing = @()
        foreach ($h in $handlers) { if ($n.O.Combined -notmatch [regex]::Escape($h)) { $missing += $h } }
        Add-Result ('A20-' + $n.N) "$($n.N) 注册全部 5 个消息处理器" $(if ($missing.Count -eq 0) { 'PASS' } else { 'FAIL' }) $(if ($missing.Count -eq 0) { ($handlers -join ', ') } else { '缺失: ' + ($missing -join ', ') })
    }

    # (7) Critical / ERR / stderr
    foreach ($n in @(@{ N = 'nodeA'; O = $outA }, @{ N = 'nodeB'; O = $outB })) {
        $crit = [regex]::Matches($n.O.Combined, '\[(FTL|CRT)\]\s|P2PChat 致命错误|Unhandled exception')
        $critEv = "匹配数=$($crit.Count)"
        if ($crit.Count -gt 0) { $critEv += ' 原文: ' + (($crit | ForEach-Object { $_.Value }) -join ' | ') }
        Add-Result ('A21-' + $n.N) "$($n.N) 无 Critical 日志 / 无未处理异常" $(if ($crit.Count -eq 0) { 'PASS' } else { 'FAIL' }) $critEv

        $errLines = [regex]::Matches($n.O.Combined, '(?m)^.*(\[ERR\]|\sERR\]).*$')
        $errEv = "ERR 行数=$($errLines.Count)"
        if ($errLines.Count -gt 0) { $errEv += ' 原文: ' + (($errLines | ForEach-Object { $_.Value.Trim() }) -join ' | ') }
        Add-Result ('A22-' + $n.N) "$($n.N) 无 ERROR 级日志" $(if ($errLines.Count -eq 0) { 'PASS' } else { 'FAIL' }) $errEv

        $errEmpty = [string]::IsNullOrWhiteSpace($n.O.StdErr)
        Add-Result ('A23-' + $n.N) "$($n.N) stderr 为空" $(if ($errEmpty) { 'PASS' } else { 'FAIL' }) $(if ($errEmpty) { '(空)' } else { '原文: ' + $n.O.StdErr })
    }

    # (8) 数据目录隔离
    $storeA = Get-Field -Text $outA.Combined -Pattern '密钥存储目录: (.+)'
    $storeB = Get-Field -Text $outB.Combined -Pattern '密钥存储目录: (.+)'
    $isoOk = ($storeA -and $storeB -and
              $storeA -like "$dataDirA*" -and $storeB -like "$dataDirB*" -and
              $storeA -ne $storeB)
    Add-Result 'A24' 'P2PCHAT_DATA_DIR 生效且两节点数据目录独立（未被 .p2pc 默认目录取代）' $(if ($isoOk) { 'PASS' } else { 'FAIL' }) "A=$storeA | B=$storeB 期望分别位于 $dataDirA / $dataDirB"
    $identityA = Join-Path $dataDirA 'identity.json'
    $identityB = Join-Path $dataDirB 'identity.json'
    $identityOk = (Test-Path -LiteralPath $identityA) -and (Test-Path -LiteralPath $identityB)
    Add-Result 'A25' '两节点各自生成 identity.json' $(if ($identityOk) { 'PASS' } else { 'FAIL' }) "$identityA 存在=$(Test-Path -LiteralPath $identityA); $identityB 存在=$(Test-Path -LiteralPath $identityB)"

    # (9) TCP 真实连通性
    Add-Result 'A26' "nodeA TCP 端口 $NodeATcpPort 可真实建立连接" $(if ($tcpProbeOk['A']) { 'PASS' } else { 'FAIL' }) $(if ($tcpProbeOk['A']) { 'connect ok' } else { "connect failed: $($tcpProbeOk['AErr'])" })
    Add-Result 'A27' "nodeB TCP 端口 $NodeBTcpPort 可真实建立连接" $(if ($tcpProbeOk['B']) { 'PASS' } else { 'FAIL' }) $(if ($tcpProbeOk['B']) { 'connect ok' } else { "connect failed: $($tcpProbeOk['BErr'])" })

    # (10) 未复制 exe（防火墙弹窗根因防护）
    $sameExe = ($nodeA.ExePath -eq $nodeB.ExePath) -and ($nodeA.ExePath -eq $ExePath)
    Add-Result 'A28' '所有实例共用同一 exe 路径（未复制 exe，避免防火墙重复询问）' $(if ($sameExe) { 'PASS' } else { 'FAIL' }) "nodeA=$($nodeA.ExePath); nodeB=$($nodeB.ExePath)"

    # ---- 8. 明文往返：Phase 2 重启 + 预置联系人 + stdin 注入 --------------------
    # REPAIR-PLAN §4.6：两实例互发一条消息，接收端日志/输出出现明文。
    # Phase 1 已捕获 Node A/B 的 NodeId 与公网入口 IP+端口；本阶段在「线性输出」
    # 模式（`P2PCHAT_PLAIN=1`）下重启两个实例 —— `DrainInbox` 会把每条聊天事件
    # 逐行写入 stdout —— 预置 Node B 到 Node A 的 `contacts.json`（静态对端 + 已知
    # 端点，免 DHT 即可直连），再通过 Node A 的 stdin 注入 `/msg nodeB <plaintext>`
    # 命令，等待 Node B 的 stdout 出现该明文。
    #
    # 与 Phase 1 共用同一 exe、不同 `P2PCHAT_DATA_DIR` 与端口隔离；保证 firewall 不会
    # 因为新进程路径而弹窗（沿用 Phase 1 的 exe 路径）。
    if (-not $useSelfTest) {
        # Phase 1 用 -NoSelfTest 时进程常驻，stdout 不会自动打印自检块，捕获不到 NodeId。
        Add-Result 'A29' '明文往返：跳过（Phase 1 使用 -NoSelfTest，无法捕获 NodeId）' 'SKIP' '移除 -NoSelfTest 后重跑以启用 Phase 2'
    } elseif (-not $idA -or -not $idB) {
        Add-Result 'A29' '明文往返：跳过（Phase 1 未能解析两节点 NodeId）' 'SKIP' "idA=$idA idB=$idB"
    } else {
        Write-Host "[8/8] 明文往返：Phase 2 重启 + 预置联系人 + stdin 注入..." -ForegroundColor Yellow

        $dataDirA2 = Join-Path $WorkRoot 'dataA2'
        $dataDirB2 = Join-Path $WorkRoot 'dataB2'
        $runDirA2 = Join-Path $WorkRoot 'runA2'
        $runDirB2 = Join-Path $WorkRoot 'runB2'

        # 预置 Node B 到 Node A 的 contacts.json（StoredContact 形状：NodeId/Alias/EndPoint/AddedAt）
        # ⚠️ 顶层必须是**恰好一层**的 JSON 数组，元素是 StoredContact 对象。
        #   - 写成裸对象 `{...}`   → List<StoredContact> 反序列化抛 JsonException → 联系人全丢
        #   - 写成嵌套数组 `[[{}]]` → 同样解析不出元素 → 联系人全丢
        #   两者都只在日志里留一行异常，脚本其余部分照跑，最后只表现为「明文没到」。
        #   PowerShell 坑位：`@(...) | ConvertTo-Json` 会在单元素时**塌缩**成对象；
        #   而 `-InputObject @(...) -AsArray` 会**多包一层**变成嵌套数组。正确写法是
        #   `-InputObject @(...)` 且**不加** -AsArray。
        $contactsFile = Join-Path $dataDirA2 'contacts.json'
        if (Test-Path -LiteralPath $dataDirA2) { Remove-Item -LiteralPath $dataDirA2 -Recurse -Force }
        New-Item -ItemType Directory -Path $dataDirA2 -Force | Out-Null
        $contactsJson = ConvertTo-Json -InputObject @(
            @{
                NodeId  = $idB
                Alias   = 'nodeB'
                EndPoint = "127.0.0.1:$NodeBTcpPort"
                AddedAt = (Get-Date).ToUniversalTime().ToString('o')
            }
        ) -Depth 5
        Set-Content -LiteralPath $contactsFile -Value $contactsJson -Encoding UTF8

        # 启动 Phase 2：PLAIN=1（强制 TUI 走线性输出模式，逐行写入 stdout），不启用 SELFTEST
        $envA2 = @{
            'P2PCHAT_DATA_DIR'         = $dataDirA2
            'P2PCHAT_P2PChat__UdpPort' = "$NodeAUdpPort"
            'P2PCHAT_P2PChat__TcpPort' = "$NodeATcpPort"
            'P2PCHAT_PLAIN'            = '1'
        }
        $envB2 = @{
            'P2PCHAT_DATA_DIR'         = $dataDirB2
            'P2PCHAT_P2PChat__UdpPort' = "$NodeBUdpPort"
            'P2PCHAT_P2PChat__TcpPort' = "$NodeBTcpPort"
            'P2PCHAT_PLAIN'            = '1'
        }
        # PreserveDataDir：contacts.json 已在上面预置好，绝不能被 Start-P2PNode 删掉
        # （历史事故：Phase 2 一直以「零联系人」启动，/msg nodeB 静默失败，见 HANDOFF §4.3）
        $nodeA2 = Start-P2PNode -Name 'nodeA2' -DataDir $dataDirA2 -RunDir $runDirA2 -EnvVars $envA2 -PreserveDataDir
        $nodeB2 = Start-P2PNode -Name 'nodeB2' -DataDir $dataDirB2 -RunDir $runDirB2 -EnvVars $envB2

        # 预置是否真的留下来了 —— 少了它后面必然失败，而失败是静默的
        $contactsSurvived = Test-Path -LiteralPath $contactsFile
        Add-Result 'A29a' 'Phase 2：预置的 contacts.json 在 nodeA2 启动后仍然存在（静态对端前提）' $(if ($contactsSurvived) { 'PASS' } else { 'FAIL' }) "contacts.json 存在=$contactsSurvived ($contactsFile)"

        # 形状必须正确：**恰好一层**数组，元素是带 NodeId 的对象。
        # 只检查「是不是数组」不够 —— 嵌套数组 `[[{}]]` 同样是数组，却一样解析不出联系人。
        # 用**原始文本**判定而不是靠 ConvertFrom-Json 的对象类型：PowerShell 会把单元素数组
        # 解包成标量（`$p -is [Array]` 对**正确**文件反而是 False），也会对嵌套数组做成员展平，
        # 两种行为都会让基于类型的检查给出错误结论。首字符判定没有这些歧义。
        $contactsShapeOk = $false
        $contactsShapeEv = '文件不存在'
        if ($contactsSurvived) {
            $raw = (Get-Content -LiteralPath $contactsFile -Raw).TrimStart()
            $isArray  = $raw.StartsWith('[')
            $isNested = $raw.StartsWith('[[')
            $hasNodeId = $false
            try { $hasNodeId = $null -ne (@($raw | ConvertFrom-Json)[0]).PSObject.Properties['NodeId'] } catch { }
            $contactsShapeOk = $isArray -and (-not $isNested) -and $hasNodeId
            $contactsShapeEv = "首字符='$($raw.Substring(0,1))' 是数组=$isArray 是嵌套=$isNested 含NodeId=$hasNodeId"
        }
        Add-Result 'A29b' 'Phase 2：contacts.json 是「单层数组 + StoredContact 对象」（与 LoadContacts 契约一致）' $(if ($contactsShapeOk) { 'PASS' } else { 'FAIL' }) $contactsShapeEv

        # 等两实例 TCP 监听就绪（contacts.json 加载是同步的，无需等待）
        $aReady2 = Wait-Until -TimeoutSeconds 15 -Condition {
            (Read-NodeLog -DataDir $dataDirA2) -match "TCP监听已启动: 0\.0\.0\.0:$NodeATcpPort"
        }
        $bReady2 = Wait-Until -TimeoutSeconds 15 -Condition {
            (Read-NodeLog -DataDir $dataDirB2) -match "TCP监听已启动: 0\.0\.0\.0:$NodeBTcpPort"
        }
        Add-Result 'A29' 'Phase 2：nodeA2/nodeB2 实例启动并 TCP 监听就绪' $(if ($aReady2 -and $bReady2) { 'PASS' } else { 'FAIL' }) "nodeA2=$aReady2 nodeB2=$bReady2"

        # 给 DHT 启动循环与后台线程一点时间（避免 stdin 写入抢跑）
        Start-Sleep -Seconds 2

        # 通过 Node A 的 stdin 注入 `/msg nodeB <plaintext>` 命令
        # TUI 用 Console.ReadKey 单字符读取，所以逐字符写 + 30ms 间隔；明文用 ASCII + '-' 无空格，匹配 /msg 命令切分逻辑。
        $plaintext = "HELLO-E2E-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        $cmd = "/msg nodeB $plaintext"
        foreach ($c in $cmd.ToCharArray()) {
            $nodeA2.Proc.StandardInput.Write($c)
            Start-Sleep -Milliseconds 30
        }
        $nodeA2.Proc.StandardInput.Write("`n")
        $nodeA2.Proc.StandardInput.Flush()

        # 等 Node B 的日志里出现明文（plain 模式 DrainInbox 写入 Serilog 日志）
        $swRound = [System.Diagnostics.Stopwatch]::StartNew()
        $plaintextSeen = Wait-Until -TimeoutSeconds 30 -IntervalMs 500 -Condition {
            (Read-NodeLog -DataDir $dataDirB2) -match [regex]::Escape($plaintext)
        }
        Write-Host ("    -> 明文往返耗时 {0}s  是否在 nodeB2 日志/输出中出现: {1}" -f [math]::Round($swRound.Elapsed.TotalSeconds,1), $plaintextSeen) -ForegroundColor $(if ($plaintextSeen) { 'Green' } else { 'Red' })

        # ---- 入站重放防护：负向对照（二次发送同一明文） --------------------------
        # 目的：给「入站重放防护」一条能发现**误杀正常消息**的断言。
        #
        # ⚠️ 语义澄清（不要误读成「重放」）：
        #   这次注入的 `/msg nodeB <同一明文>` 会走 ChatService.SendPrivateMessageAsync
        #   新建一个 TextMessage。`Message.MessageId` 的默认初始化是
        #   `= Guid.NewGuid()`（src/P2PChat.Core/Models/Message.cs:26），
        #   `Timestamp` 同理为 `DateTimeOffset.UtcNow`，而该方法**不覆盖**这两个字段
        #   （src/P2PChat.Chat/Services/ChatService.cs:82-88）。
        #   ⇒ 第二次注入产生的是一条**全新的合法消息**（新 MessageId + 新时间戳），
        #     **不是重放**。重放防护**应当放行**，nodeB2 **应当**出现第二条事件行。
        #   真正的「重放」= 重发**同一条信封**（同 MessageId），那只能在 TCP 层构造，
        #   属于 task-12 集成测试范畴；stdin 注入无法触发。
        #
        # 等待信号用 Serilog 的 `私聊消息已处理: {Sender} -> {Text}`
        # （src/P2PChat.Chat/Handlers/PrivateMessageHandler.cs:84，[DBG] 级，
        #   每条被 handler 处理的消息恰好一行），而不是 stdout 事件行 ——
        #   stdout 要等进程退出后 ReadToEndAsync 才能整体读到，那时已经太晚。
        $cmdDup = "/msg nodeB $plaintext"
        foreach ($c in $cmdDup.ToCharArray()) {
            $nodeA2.Proc.StandardInput.Write($c)
            Start-Sleep -Milliseconds 30
        }
        $nodeA2.Proc.StandardInput.Write("`n")
        $nodeA2.Proc.StandardInput.Flush()

        $handledRe = '私聊消息已处理:[^\r\n]*' + [regex]::Escape($plaintext)
        $swDup = [System.Diagnostics.Stopwatch]::StartNew()
        $duplicateAccepted = Wait-Until -TimeoutSeconds 25 -IntervalMs 500 -Condition {
            [regex]::Matches((Read-NodeLog -DataDir $dataDirB2), $handledRe).Count -ge 2
        }
        Write-Host ("    -> 二次发送(新 MessageId)后 nodeB2 已处理消息数>=2: {0}（耗时 {1}s）" -f $duplicateAccepted, [math]::Round($swDup.Elapsed.TotalSeconds,1)) -ForegroundColor $(if ($duplicateAccepted) { 'Green' } else { 'Red' })

        # 关掉 Phase 2 实例（让 stdout/stderr 关闭以便 Get-NodeOutput 完成）
        foreach ($n in @($nodeA2, $nodeB2)) {
            try {
                if ($n.Proc -and -not $n.Proc.HasExited) {
                    $n.Proc.Kill($true)
                    $null = $n.Proc.WaitForExit(3000)
                    $n.KilledByScript = $true
                }
            } catch { }
        }
        Start-Sleep -Seconds 1

        $outA2 = Get-NodeOutput -Node $nodeA2
        $outB2 = Get-NodeOutput -Node $nodeB2

        Add-Result 'A30' 'Phase 2：nodeA2 发送的明文出现在 nodeB2 日志（私聊端到端往返）' $(if ($plaintextSeen) { 'PASS' } else { 'FAIL' }) "明文='$plaintext'"
        $plainInCombined = $outB2.Combined -match [regex]::Escape($plaintext)
        Add-Result 'A31' 'Phase 2：nodeB2 StdOut+Log 累计中包含明文（plain 模式 DrainInbox 写入）' $(if ($plainInCombined) { 'PASS' } else { 'FAIL' }) $(if ($plainInCombined) { 'matched' } else { "no match in $($outB2.Combined.Length)B combined" })

        # A32 的**旧断言是错的**，本轮修正（见 HANDOFF §4.3）：
        # 旧版断言「nodeA2 看不到明文」，隐含假设是「plain 模式不回显本地事件流」。
        # 但 `DrainInbox` 在 plain 模式下会把**每一条** inbox 项（含
        # `ChatService.SendPrivateMessageAsync` 本地推送的 IsOutgoing=true 事件）
        # 逐行写到 stdout。发送一旦成功，nodeA2 必然回显这条明文 —— 这是正确行为。
        #
        # 有意义的断言是：nodeA2 的**聊天事件行**若含该明文，**必须**标为本地出站（「我」），
        # 不能被标成他站来信 —— 否则 nodeB2 里的明文就不能作为「真的收到了」的证据。
        #
        # 只看 DrainInbox 的事件行（形如 `[cid] [HH:mm] 发件人: 正文`）；
        # Serilog 的 `私聊消息已发送: x -> 正文` 是**日志**、不是事件行，不在本断言范围内
        # （日志类断言由 A20/A21 覆盖）。
        # 注意：.NET 正则**不支持** \h（那是 PCRE 语法），必须显式写字符类
        $eventLineRe = '\[(?:[0-9a-fA-F]{1,8}|__system__)\]\s*\[\d{2}:\d{2}\]\s*(?<who>[^:]+):'
        $a2EventLines = @($outA2.Combined -split "`r?`n" |
            Where-Object { $_ -match [regex]::Escape($plaintext) -and $_ -match $eventLineRe })
        $a2NotOutgoing = @($a2EventLines | Where-Object { $_ -notmatch '\[\d{2}:\d{2}\]\s*我\s*:' })
        Add-Result 'A32' 'Phase 2：nodeA2 的聊天事件行若含明文，必须标为本地出站「我」（不能是他站来信）' `
            $(if ($a2NotOutgoing.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
            $(if ($a2EventLines.Count -eq 0) { 'nodeA2 无聊天事件行含该明文' } else { "事件行 $($a2EventLines.Count) 行，其中非出站 $($a2NotOutgoing.Count) 行"; $a2EventLines | Select-Object -First 3 | ForEach-Object { "  $_" } })

        # ---- (A33/A34) 入站重放防护的可观测断言 --------------------------------
        # 为什么**不做**「重放同一条消息 → 断言该明文只出现 1 行」：
        #   1) MessageId 每次都是全新 Guid（Message.cs:26），二次注入**不是重放**，
        #      防护应当放行 —— 「恰好 1 行」会 FAIL，而 FAIL 恰恰说明产品是对的；
        #   2) 「行数」本身不可靠：单条消息已让该明文在 Combined 里出现 **>=2 行**
        #      （Serilog `[DBG] 私聊消息已处理: … -> 明文` + plain 模式 stdout 事件行）。
        #   故改为断言**可观测且不恒真**的性质。
        $b2EventLines = @($outB2.Combined -split "`r?`n" |
            Where-Object { $_ -match [regex]::Escape($plaintext) -and $_ -match $eventLineRe })
        $b2HandledLines = @($outB2.Combined -split "`r?`n" |
            Where-Object { $_ -match [regex]::Escape($plaintext) -and $_ -match '私聊消息已处理' })
        $b2EventEv = "事件行=$($b2EventLines.Count) 私聊消息已处理行=$($b2HandledLines.Count) 在线等待确认=$duplicateAccepted"
        if ($b2EventLines.Count -gt 0) { $b2EventEv += '  样本: ' + (($b2EventLines | Select-Object -First 2) -join ' || ') }
        Add-Result 'A33' 'Phase 2：nodeB2 存在含该明文的聊天事件行（正常消息穿过 验签→重放防护→handler→inbox 全链路，未被误杀）' `
            $(if ($b2EventLines.Count -ge 1) { 'PASS' } else { 'FAIL' }) $b2EventEv
        Add-Result 'A34' 'Phase 2：重复发送同一明文（新 MessageId）未被重放防护误拦截 —— 负向对照（重放防护不误杀合法二次发送）' `
            $(if ($b2EventLines.Count -ge 2) { 'PASS' } else { 'FAIL' }) `
            "期望事件行>=2，实际=$($b2EventLines.Count)；$b2EventEv"

        # 收集 Phase 2 关键日志供末尾摘要使用
        $outA.Combined += "`n" + $outA2.Combined
        $outB.Combined += "`n" + $outB2.Combined
    }

    # ---- 输出 --------------------------------------------------------------
    Write-Host ""
    Write-Host "==============================================" -ForegroundColor Cyan
    Write-Host " 断言结果" -ForegroundColor Cyan
    Write-Host "==============================================" -ForegroundColor Cyan
    foreach ($r in $script:Results) {
        $color = switch ($r.Status) {
            'PASS' { 'Green' }
            'FAIL' { 'Red' }
            'WARN' { 'Yellow' }
            default { 'DarkGray' }
        }
        Write-Host ("[{0}] {1}  {2}" -f $r.Status, $r.Id, $r.Desc) -ForegroundColor $color
        Write-Host ("       证据: {0}" -f $r.Evidence)
    }

    $failCount = ($script:Results | Where-Object Status -eq 'FAIL').Count
    $passCount = ($script:Results | Where-Object Status -eq 'PASS').Count
    $skipCount = ($script:Results | Where-Object Status -eq 'SKIP').Count

    # ---- 原始日志摘要 ------------------------------------------------------
    Write-Host ""
    Write-Host "==============================================" -ForegroundColor Cyan
    Write-Host " 原始日志关键行" -ForegroundColor Cyan
    Write-Host "==============================================" -ForegroundColor Cyan
    $keyPattern = '密钥存储目录|UDP传输绑定到|TCP监听已启动|节点ID|实际端口|引导节点|PING 引导节点|引导完成|路由表|注册消息处理器|被占用|重放防护|自检|ERR\]|WRN\]|FTL\]|CRT\]|致命错误'
    foreach ($n in @(@{ N = 'nodeA'; O = $outA; D = $dataDirA }, @{ N = 'nodeB'; O = $outB; D = $dataDirB })) {
        Write-Host ""
        Write-Host "--- $($n.N) (data=$($n.D)) ---" -ForegroundColor Yellow
        $lines = ($n.O.Combined -split "`r?`n") | Where-Object { $_ -match $keyPattern }
        if ($lines) { $lines | ForEach-Object { Write-Host "  $_" } } else { Write-Host "  (无匹配行)" }
        Write-Host "  stderr: $(if ([string]::IsNullOrWhiteSpace($n.O.StdErr)) { '(空)' } else { $n.O.StdErr })"
    }

    # ---- 汇总 --------------------------------------------------------------
    $verdict = if ($failCount -eq 0) { 'PASS' } else { 'FAIL' }
    $summary = [pscustomobject]@{
        Verdict      = $verdict
        Exe          = $ExePath
        WorkRoot     = $WorkRoot
        SelfTestMode = $useSelfTest
        SelfTestWaitMs = $SelfTestWaitMs
        NodeA        = [pscustomobject]@{ UdpPort = $NodeAUdpPort; TcpPort = $NodeATcpPort; NodeId = $idA; DataDir = $dataDirA; Alive = $aliveA; ExitCode = $exitCodeA; KilledByScript = $nodeA.KilledByScript }
        NodeB        = [pscustomobject]@{ UdpPort = $NodeBUdpPort; TcpPort = $NodeBTcpPort; NodeId = $idB; DataDir = $dataDirB; Alive = $aliveB; ExitCode = $exitCodeB; KilledByScript = $nodeB.KilledByScript }
        PassCount    = $passCount
        FailCount    = $failCount
        SkipCount    = $skipCount
        SawBootstrapResponse = $sawResponse
        Results      = $script:Results
    }
    $jsonPath = Join-Path $WorkRoot 'e2e-result.json'
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

    Write-Host ""
    Write-Host "==============================================" -ForegroundColor Cyan
    Write-Host (" 结论: {0}   PASS={1} FAIL={2} SKIP={3}" -f $verdict, $passCount, $failCount, $skipCount) -ForegroundColor $(if ($verdict -eq 'PASS') { 'Green' } else { 'Red' })
    Write-Host (" JSON: {0}" -f $jsonPath)
    Write-Host "==============================================" -ForegroundColor Cyan

    if ($failCount -gt 0) { exit 1 } else { exit 0 }
}
finally {
    # 兜底：无论成功失败都收掉自己启动的进程，绝不留挂死实例
    Stop-AllNodes
    $leftover = @()
    foreach ($n in $script:Nodes) {
        if ($n.Proc) {
            try { if (-not $n.Proc.HasExited) { $leftover += $n.Name } } catch { }
        }
    }
    if ($leftover.Count -eq 0) {
        Write-Host "[cleanup] 无遗留进程（脚本未强杀任何实例）" -ForegroundColor DarkGray
    } else {
        Write-Host ("[cleanup] 已强制清理遗留进程: " + ($leftover -join ', ')) -ForegroundColor Yellow
    }
}
