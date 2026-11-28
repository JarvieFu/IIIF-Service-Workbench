<#
.SYNOPSIS
    启动并打开 SimpleAnnotationServer（SAS）标注页面。

.DESCRIPTION
    被桌面快捷方式调用时：
      1. 8888 已在监听 → 直接打开页面；
      2. 否则从 tools\sas 目录后台启动服务（标注库 data\ 是相对路径，必须在此目录启动），
         等就绪后打开 http://localhost:8888/index.html。
    日志：tools\sas\logs\sas.out.log、sas.err.log。
    停止：tools\stop-services.ps1。

.PARAMETER NoBrowser
    只启动服务，不开浏览器（测试用）。
#>
param(
    [int]$Port = 8888,
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$sasDir = Join-Path $PSScriptRoot 'sas'
$url = "http://localhost:$Port/index.html"

function Test-Port([int]$p) {
    return [bool](& netstat -ano | Where-Object {
        $_ -match '^\s*TCP\s+\S+:(\d+)\s+\S+\s+LISTENING\s+(\d+)\s*$' -and [int]$matches[1] -eq $p
    })
}

function Wait-Ready([string]$u, [int]$timeoutSeconds = 90) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            if ((Invoke-WebRequest -Uri $u -TimeoutSec 3 -UseBasicParsing).StatusCode -eq 200) { return $true }
        } catch { }
        Start-Sleep -Milliseconds 800
    }
    return $false
}

try {
    if (Test-Port $Port) {
        Write-Host "SAS 已在运行：$url" -ForegroundColor Green
    } else {
        $war = Join-Path $sasDir 'simpleAnnotationStore.war'
        $runner = Join-Path $sasDir 'dependency\jetty-runner.jar'
        if (-not (Test-Path $war) -or -not (Test-Path $runner)) {
            throw "SAS 文件不完整：$sasDir 下缺 war 或 jetty-runner.jar"
        }

        $lock = Join-Path $sasDir 'data\tdb.lock'
        if (Test-Path $lock) {
            Remove-Item $lock -Force
            Write-Host "已清理残留的 Jena 锁文件" -ForegroundColor DarkYellow
        }

        $logDir = Join-Path $sasDir 'logs'
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null

        # 优先用 javaw（不带控制台窗口），其次 java
        $javaExe = 'java'
        $javaw = Get-Command javaw -ErrorAction SilentlyContinue
        if ($javaw) { $javaExe = $javaw.Source }

        # Jetty 需要在 java.io.tmpdir 解压 WAR；本机用户 Temp 权限异常会报“拒绝访问”
        # 并让应用变成 UNAVAILABLE（503），所以固定用工作区 tmp 目录。
        $tmpDir = Join-Path $sasDir 'tmp'
        New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
        Get-ChildItem $tmpDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

        Write-Host "SAS 未运行，正在后台启动 ..." -ForegroundColor Cyan
        Start-Process -FilePath $javaExe `
            -ArgumentList @("-Djava.io.tmpdir=$tmpDir", '-jar', 'dependency\jetty-runner.jar', '--port', "$Port", 'simpleAnnotationStore.war') `
            -WorkingDirectory $sasDir `
            -RedirectStandardOutput (Join-Path $logDir 'sas.out.log') `
            -RedirectStandardError (Join-Path $logDir 'sas.err.log')

        if (-not (Wait-Ready $url)) {
            throw "SAS 在 90 秒内没有就绪，请查看 $logDir\sas.err.log"
        }
        Write-Host "SAS 已启动：$url" -ForegroundColor Green
    }

    if (-not $NoBrowser) {
        Start-Process $url
    }
} catch {
    Write-Host ""
    Write-Host "打开失败：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
