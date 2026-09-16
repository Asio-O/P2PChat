#Requires -Version 7.0
<#
.SYNOPSIS
  P2PChat 配置注入方式实测（阶段 A-1）。

.DESCRIPTION
  实测确认 Program.cs 中
      new ConfigurationBuilder().AddEnvironmentVariables("P2PCHAT_").AddCommandLine(args)
  接受哪种写法覆盖 UdpPort / TcpPort / BootstrapNodes，以及数据目录的隔离方式。

  隔离方式（重要，已按 Lead 要求改造）：
    * 严禁复制 exe —— 所有场景共用同一个固定 exe 路径，避免 Windows 防火墙对每个新程序路径
      重复弹出安全警报。
    * 数据隔离使用 P2PCHAT_DATA_DIR（见 src/P2PChat.Core/Extensions/DataPath.cs）。
    * 默认启用 P2PCHAT_SELFTEST=1，实例打印自检信息后自行退出，不留挂死进程。

  注意：本脚本产出"环境变量/命令行注入方式"的结论。2026-09-16 首轮实测（当时仍在复制 exe）
  的结论为 8/8 PASS；改造为 `P2PCHAT_DATA_DIR` 隔离后需在 Lead 重新 publish 后复跑确认。

  用法:
    pwsh -NoProfile -File scripts\config-probe.ps1
    pwsh -NoProfile -File scripts\config-probe.ps1 -WaitSeconds 15
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [string]$WorkRoot,
    [int]$WaitSeconds = 12
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ExePath) {
    $ExePath = Join-Path $repoRoot 'src\P2PChat.App\bin\Release\net11.0\win-x64\publish\P2PChat.App.exe'
}
if (-not $WorkRoot) {
    $WorkRoot = Join-Path $env:TEMP 'p2pchat-config-probe'
}
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
$WorkRoot = [System.IO.Path]::GetFullPath($WorkRoot)

Write-Host "== P2PChat 配置注入实测 ==" -ForegroundColor Cyan
Write-Host "exe      : $ExePath   (固定路径，不复制)"
Write-Host "workRoot : $WorkRoot"
Write-Host "wait     : ${WaitSeconds}s / 场景（自检模式，进程会自行退出）"
Write-Host ""

$script:Procs = @()
function Stop-All {
    foreach ($p in $script:Procs) {
        try { if ($p -and -not $p.HasExited) { $p.Kill($true); $null = $p.WaitForExit(5000) } } catch { }
    }
}

# ---- 启动一个实例并采集输出 -------------------------------------------------
function Invoke-Instance {
    param(
        [string]$Dir,
        [hashtable]$EnvVars,
        [string[]]$Arguments,
        [int]$WaitSeconds
    )

    $dataDir = Join-Path $Dir 'data'
    if (Test-Path -LiteralPath $Dir) { Remove-Item -LiteralPath $Dir -Recurse -Force }
    New-Item -ItemType Directory -Path $Dir -Force | Out-Null

    # 基线环境：数据目录隔离 + 自检模式（避免挂死进程）；场景可覆盖其中任意键
    $effective = @{
        'P2PCHAT_DATA_DIR'          = $dataDir
        'P2PCHAT_SELFTEST'          = '1'
        'P2PCHAT_SELFTEST_WAIT'     = '3000'
    }
    foreach ($k in $EnvVars.Keys) { $effective[$k] = [string]$EnvVars[$k] }
    if ($effective.ContainsKey('P2PCHAT_DATA_DIR')) { $dataDir = [string]$effective['P2PCHAT_DATA_DIR'] }

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $ExePath              # 固定同一路径：绝不复制 exe
    $psi.WorkingDirectory = $Dir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardInput = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }
    foreach ($k in $effective.Keys) { $psi.Environment[$k] = [string]$effective[$k] }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = [System.Diagnostics.Process]::Start($psi)
    $script:Procs += $proc
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()

    $swWait = [System.Diagnostics.Stopwatch]::StartNew()
    while ($swWait.Elapsed.TotalSeconds -lt $WaitSeconds -and -not $proc.HasExited) { Start-Sleep -Milliseconds 200 }
    if (-not $proc.HasExited) { try { $proc.Kill($true) } catch { } }
    $null = $proc.WaitForExit(5000)
    $sw.Stop()

    $logText = ''
    $logPath = $null
    $logDir = Join-Path $dataDir 'logs'
    if (Test-Path -LiteralPath $logDir) {
        $lf = Get-ChildItem -LiteralPath $logDir -Filter 'p2pchat-*.log' -File |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($lf) { $logPath = $lf.FullName; $logText = Get-Content -LiteralPath $lf.FullName -Raw -Encoding UTF8 }
    }

    $stdoutText = ''
    try { $stdoutText = $outTask.GetAwaiter().GetResult() } catch { $stdoutText = "<stdout read failed: $($_.Exception.Message)>" }
    $stderrText = ''
    try { $stderrText = $errTask.GetAwaiter().GetResult() } catch { $stderrText = "<stderr read failed: $($_.Exception.Message)>" }

    Set-Content -LiteralPath (Join-Path $Dir 'probe.stdout.txt') -Value $stdoutText -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Dir 'probe.stderr.txt') -Value $stderrText -Encoding UTF8

    [pscustomobject]@{
        Dir          = $Dir
        DataDir      = $dataDir
        Exited       = $proc.HasExited
        ExitCode     = if ($proc.HasExited) { $proc.ExitCode } else { $null }
        LogPath      = $logPath
        StdOut       = $stdoutText
        StdErr       = $stderrText
        LogText      = $logText
        Combined     = ($stdoutText + "`n" + $logText)
        ElapsedMs    = $sw.ElapsedMilliseconds
    }
}

function Get-Field {
    param([string]$Text, [string]$Pattern)
    $m = [regex]::Match($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if ($m.Success) { return $m.Groups[1].Value.Trim() } else { return '<未出现>' }
}

$scenarios = @(
    @{
        Name  = 'E1-env-double-underscore'
        Note  = 'P2PCHAT_P2PChat__UdpPort / __TcpPort / __BootstrapNodes__0'
        Env   = @{
            'P2PCHAT_P2PChat__UdpPort'           = '18001'
            'P2PCHAT_P2PChat__TcpPort'           = '18002'
            'P2PCHAT_P2PChat__BootstrapNodes__0' = '127.0.0.1:18002'
        }
        Args  = @()
    },
    @{
        Name  = 'E2-env-single-underscore'
        Note  = 'P2PCHAT_UdpPort / P2PCHAT_TcpPort（缺少 P2PChat__ 段，预期不被识别）'
        Env   = @{
            'P2PCHAT_UdpPort' = '18011'
            'P2PCHAT_TcpPort' = '18012'
        }
        Args  = @()
    },
    @{
        Name  = 'E3-cmdline-colon'
        Note  = '--P2PChat:UdpPort=... --P2PChat:TcpPort=... --P2PChat:BootstrapNodes:0=...'
        Env   = @{}
        Args  = @(
            '--P2PChat:UdpPort=18021',
            '--P2PChat:TcpPort=18022',
            '--P2PChat:BootstrapNodes:0=127.0.0.1:18022'
        )
    },
    @{
        Name  = 'E4-configkey-datapath-ignored'
        Note  = 'P2PChat:DataPath 配置键（源码未读取该键，预期无效）'
        Env   = @{
            'P2PCHAT_P2PChat__DataPath' = (Join-Path $WorkRoot 'E4-should-be-ignored')
        }
        Args  = @()
    },
    @{
        Name  = 'E5-precedence'
        Note  = 'env UdpPort=18031 + cmdline --P2PChat:UdpPort=18041（预期命令行胜出）'
        Env   = @{
            'P2PCHAT_P2PChat__UdpPort' = '18031'
            'P2PCHAT_P2PChat__TcpPort' = '18032'
        }
        Args  = @('--P2PChat:UdpPort=18041')
    },
    @{
        Name  = 'E6-env-datadir-override'
        Note  = 'P2PCHAT_DATA_DIR 生效（数据目录覆盖）'
        Env   = @{
            'P2PCHAT_DATA_DIR' = (Join-Path $WorkRoot 'E6-custom-data')
        }
        Args  = @()
    }
)

$results = @()
foreach ($s in $scenarios) {
    Write-Host ("--- {0} : {1}" -f $s.Name, $s.Note) -ForegroundColor Yellow
    $dir = Join-Path $WorkRoot $s.Name
    $r = Invoke-Instance -Dir $dir -EnvVars $s.Env -Arguments $s.Args -WaitSeconds $WaitSeconds

    $udp = Get-Field -Text $r.Combined -Pattern '实际端口: UDP=(\d+), TCP=\d+'
    $tcp = Get-Field -Text $r.Combined -Pattern '实际端口: UDP=\d+, TCP=(\d+)'
    $nodeId = Get-Field -Text $r.Combined -Pattern '本地节点ID: ([0-9a-fA-F]{40})'
    $boot = Get-Field -Text $r.Combined -Pattern '引导节点: (.+)'
    $storeDir = Get-Field -Text $r.Combined -Pattern '密钥存储目录: (.+)'
    $ping = Get-Field -Text $r.Combined -Pattern 'PING引导节点: (\S+)'
    $udpBusy = Get-Field -Text $r.Combined -Pattern 'UDP端口 (\d+) 被占用'
    $tcpBusy = Get-Field -Text $r.Combined -Pattern 'TCP端口 (\d+) 被占用'
    $attemptedUdp = if ($udpBusy -ne '<未出现>') { $udpBusy } else { $udp }
    $attemptedTcp = if ($tcpBusy -ne '<未出现>') { $tcpBusy } else { $tcp }
    $crit = [regex]::Matches($r.Combined, '\[(FTL|CRT)\]\s|P2PChat 致命错误|Unhandled exception').Count
    $unhandled = [regex]::Matches($r.Combined, 'Unhandled exception|未处理的异常').Count

    $obj = [pscustomobject]@{
        Scenario     = $s.Name
        Note         = $s.Note
        EnvVars      = (($s.Env.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' | ')
        Args         = ($s.Args -join ' ')
        Exited       = $r.Exited
        ExitCode     = $r.ExitCode
        UdpActual    = $udp
        TcpActual    = $tcp
        AttemptedUdp = $attemptedUdp
        AttemptedTcp = $attemptedTcp
        PortBusyWarn = ($udpBusy -ne '<未出现>') -or ($tcpBusy -ne '<未出现>')
        NodeId       = $nodeId
        BootStrap    = $boot
        StoreDir     = $storeDir
        Ping         = $ping
        Critical     = $crit
        Unhandled    = $unhandled
        Dir          = $r.Dir
        DataDir      = $r.DataDir
        ElapsedMs    = $r.ElapsedMs
    }
    $results += $obj

    Write-Host ("    自检退出={0} ExitCode={1} UDP={2} TCP={3} (尝试绑定 UDP={4} TCP={5})" -f $obj.Exited, $obj.ExitCode, $obj.UdpActual, $obj.TcpActual, $obj.AttemptedUdp, $obj.AttemptedTcp)
    Write-Host ("    引导: {0}" -f $obj.BootStrap)
    Write-Host ("    密钥存储目录: {0}" -f $obj.StoreDir)
    Write-Host ("    PING: {0} | Critical={1} Unhandled={2} 端口冲突={3}" -f $obj.Ping, $obj.Critical, $obj.Unhandled, $obj.PortBusyWarn)
    Write-Host ""
}

# ---- 结论判定 ---------------------------------------------------------------
$e1 = $results | Where-Object Scenario -eq 'E1-env-double-underscore'
$e2 = $results | Where-Object Scenario -eq 'E2-env-single-underscore'
$e3 = $results | Where-Object Scenario -eq 'E3-cmdline-colon'
$e4 = $results | Where-Object Scenario -eq 'E4-configkey-datapath-ignored'
$e5 = $results | Where-Object Scenario -eq 'E5-precedence'
$e6 = $results | Where-Object Scenario -eq 'E6-env-datadir-override'

$checks = @(
    [pscustomobject]@{
        Check    = 'E1 环境变量 P2PCHAT_P2PChat__UdpPort/__TcpPort 生效'
        Pass     = ($e1.AttemptedUdp -eq '18001' -and $e1.AttemptedTcp -eq '18002')
        Evidence = "尝试绑定 UDP=$($e1.AttemptedUdp) TCP=$($e1.AttemptedTcp) 实际 UDP=$($e1.UdpActual) TCP=$($e1.TcpActual) 期望 18001/18002"
    },
    [pscustomobject]@{
        Check    = 'E1 环境变量 BootstrapNodes__0 生效'
        Pass     = ($e1.BootStrap -like '*127.0.0.1:18002*')
        Evidence = "引导=$($e1.BootStrap)"
    },
    [pscustomobject]@{
        Check    = 'E2 单下划线写法 P2PCHAT_UdpPort 不生效（端口为随机非 18011/18012）'
        Pass     = ($e2.AttemptedUdp -ne '18011' -and $e2.AttemptedTcp -ne '18012' -and $e2.AttemptedUdp -ne '<未出现>')
        Evidence = "尝试绑定 UDP=$($e2.AttemptedUdp) TCP=$($e2.AttemptedTcp)（随机）"
    },
    [pscustomobject]@{
        Check    = 'E3 命令行 --P2PChat:UdpPort= 生效'
        Pass     = ($e3.AttemptedUdp -eq '18021' -and $e3.AttemptedTcp -eq '18022')
        Evidence = "尝试绑定 UDP=$($e3.AttemptedUdp) TCP=$($e3.AttemptedTcp) 实际 UDP=$($e3.UdpActual) TCP=$($e3.TcpActual) 期望 18021/18022"
    },
    [pscustomobject]@{
        Check    = 'E3 命令行 --P2PChat:BootstrapNodes:0= 生效'
        Pass     = ($e3.BootStrap -like '*127.0.0.1:18022*')
        Evidence = "引导=$($e3.BootStrap)"
    },
    [pscustomobject]@{
        Check    = 'E4 配置键 P2PChat:DataPath 不可注入（仍用 P2PCHAT_DATA_DIR 指定的目录）'
        Pass     = ($e4.StoreDir -ne '<未出现>' -and $e4.StoreDir -like "$($e4.DataDir)*")
        Evidence = "StoreDir=$($e4.StoreDir) 期望以 DATA_DIR=$($e4.DataDir) 开头"
    },
    [pscustomobject]@{
        Check    = 'E5 命令行优先于环境变量（首选绑定端口=命令行值 18041）'
        Pass     = ($e5.AttemptedUdp -eq '18041')
        Evidence = "尝试绑定 UDP=$($e5.AttemptedUdp) 实际 UDP=$($e5.UdpActual) env=18031 cmdline=18041"
    },
    [pscustomobject]@{
        Check    = 'E6 P2PCHAT_DATA_DIR 生效（密钥存储目录 = 指定目录）'
        Pass     = ($e6.StoreDir -ne '<未出现>' -and $e6.StoreDir -like "$($e6.DataDir)*")
        Evidence = "StoreDir=$($e6.StoreDir) 期望以 $($e6.DataDir) 开头"
    },
    [pscustomobject]@{
        Check    = '所有场景均未出现端口占用回退（端口选择有效）'
        Pass     = (-not ($results | Where-Object PortBusyWarn))
        Evidence = "端口冲突场景: " + (((($results | Where-Object PortBusyWarn).Scenario) -join ', ') -replace '^$', '无')
    },
    [pscustomobject]@{
        Check    = '所有场景自检模式均正常退出且退出码 0（无挂死进程）'
        Pass     = (-not ($results | Where-Object { -not $_.Exited -or $_.ExitCode -ne 0 }))
        Evidence = "非正常退出场景: " + (((($results | Where-Object { -not $_.Exited -or $_.ExitCode -ne 0 }).Scenario) -join ', ') -replace '^$', '无')
    }
)

Write-Host "== 配置注入实测结论 ==" -ForegroundColor Cyan
$fail = 0
foreach ($c in $checks) {
    $tag = if ($c.Pass) { 'PASS' } else { 'FAIL' }
    if (-not $c.Pass) { $fail++ }
    $color = if ($c.Pass) { 'Green' } else { 'Red' }
    Write-Host ("[{0}] {1}" -f $tag, $c.Check) -ForegroundColor $color
    Write-Host ("       证据: {0}" -f $c.Evidence)
}

$jsonPath = Join-Path $WorkRoot 'config-probe-result.json'
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
Write-Host ""
Write-Host ("JSON: {0}" -f $jsonPath)
Write-Host ("结论: {0} 项检查, {1} 项失败" -f $checks.Count, $fail)

Stop-All
if ($fail -gt 0) { exit 1 } else { exit 0 }
