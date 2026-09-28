#requires -Version 7.0
# 最小复现：验证 plain 模式（P2PCHAT_PLAIN=1 + 重定向 stdin）下 TUI 是否真的读 stdin 并分派命令。
# 只用 /id（纯本地、不联网、不需对端），排除网络因素。
# 严禁复制 exe —— 沿用同一个固定 exe 路径。

$ErrorActionPreference = 'Stop'
$Exe = 'E:\dream\P2PChat\src\P2PChat.App\bin\Release\net11.0\win-x64\publish\P2PChat.App.exe'
$Work = Join-Path $env:TEMP ("p2pc-plainprobe-" + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $Work -Force | Out-Null

function Show([string]$label, [string]$text) {
    Write-Host "--- $label ---" -ForegroundColor Yellow
    if ([string]::IsNullOrWhiteSpace($text)) { Write-Host "  (空)" -ForegroundColor DarkGray }
    else { $text -split "`r?`n" | Select-Object -Last 25 | ForEach-Object { Write-Host "  $_" } }
}

foreach ($mode in @('whole', 'charwise')) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Exe
    $psi.WorkingDirectory = $Work
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardInput = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.Environment['P2PCHAT_DATA_DIR'] = Join-Path $Work "d-$mode"
    $psi.Environment['P2PCHAT_P2PChat__UdpPort'] = '21081'
    $psi.Environment['P2PCHAT_P2PChat__TcpPort'] = '21091'
    $psi.Environment['P2PCHAT_PLAIN'] = '1'
    $psi.Environment['P2PCHAT_P2PChat__BootstrapNodes'] = '127.0.0.1:21099'   # 不可达，避免公网 DHT 干扰
    # BootstrapNodes 是数组，配置绑定用逗号；这里单值即可

    $p = [System.Diagnostics.Process]::Start($psi)
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()

    Start-Sleep -Milliseconds 4500

    Write-Host "=== 注入 /id （模式=$mode）===" -ForegroundColor Cyan
    if ($mode -eq 'whole') {
        $p.StandardInput.Write("/id")
        $p.StandardInput.Flush()
        Start-Sleep -Milliseconds 200
        $p.StandardInput.Write("`n")
        $p.StandardInput.Flush()
    }
    else {
        # 精确复刻 e2e-verify.ps1 的注入方式：逐字符 + 30ms 间隔，再写换行
        foreach ($c in "/id".ToCharArray()) {
            $p.StandardInput.Write($c)
            Start-Sleep -Milliseconds 30
        }
        $p.StandardInput.Write("`n")
        $p.StandardInput.Flush()
    }

    Start-Sleep -Seconds 4
    $p.StandardInput.Close()      # 触发 EOF
    Start-Sleep -Seconds 2
    if (-not $p.HasExited) { $p.Kill($true); $null = $p.WaitForExit(5000) }

    $out = $outTask.GetAwaiter().GetResult()
    $err = $errTask.GetAwaiter().GetResult()
    Show "stdout (tail)" $out
    if (-not [string]::IsNullOrWhiteSpace($err)) { Show "stderr" $err }
    Write-Host ""
}

Write-Host "工作目录: $Work" -ForegroundColor DarkGray
