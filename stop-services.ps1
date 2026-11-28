<#
.SYNOPSIS
    停止本工作区启动的 IIIF 服务（SAS、etu 与本地图像服务）。

.DESCRIPTION
    按端口找到监听进程并结束：默认 8888（SAS）、3000/3001（etu 的 v3 / v2 项目）、
    8182（本地 IIIF Image API Level 2 服务，start-image.ps1 启动）。
    结束后顺手清理 SAS 的 Jena 锁文件，避免下次启动报锁占用。

    用 netstat 解析端口占用（受限沙箱里 Get-NetTCPConnection 会被拒绝访问）。

.PARAMETER Ports
    要停止的端口列表，默认 8888,3000,3001,8182。

.EXAMPLE
    .\stop-services.ps1
    .\stop-services.ps1 -Ports 3000
#>
param(
    [int[]]$Ports = @(8888, 3000, 3001, 8182)
)

$ErrorActionPreference = 'Continue'

$listeners = & netstat -ano | ForEach-Object {
    if ($_ -match '^\s*TCP\s+\S+:(\d+)\s+\S+\s+LISTENING\s+(\d+)\s*$') {
        [pscustomobject]@{ Port = [int]$matches[1]; ProcessId = [int]$matches[2] }
    }
}

foreach ($port in $Ports) {
    $owners = $listeners | Where-Object { $_.Port -eq $port } | Select-Object -ExpandProperty ProcessId -Unique
    if (-not $owners) {
        Write-Host "端口 $port 没有监听进程" -ForegroundColor DarkGray
        continue
    }
    foreach ($procId in $owners) {
        try {
            $proc = Get-Process -Id $procId -ErrorAction Stop
            Stop-Process -Id $procId -Force
            Write-Host "已停止端口 $port 上的进程 $procId（$($proc.ProcessName)）" -ForegroundColor Yellow
        } catch {
            Write-Host "无法停止端口 $port 上的进程 $procId：$($_.Exception.Message)" -ForegroundColor Red
        }
    }
}

Start-Sleep -Milliseconds 800

$lock = Join-Path $PSScriptRoot 'sas\data\tdb.lock'
if (Test-Path $lock) {
    Remove-Item $lock -Force -ErrorAction SilentlyContinue
    Write-Host "已清理 Jena 锁文件 data\tdb.lock" -ForegroundColor DarkYellow
}
