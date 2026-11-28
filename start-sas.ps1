<#
.SYNOPSIS
    启动 SimpleAnnotationServer（IIIF 标注服务器，自带 Mirador 2 标注界面）。

.DESCRIPTION
    - 默认监听 8888 端口，界面地址 http://localhost:8888/index.html
    - 标注数据存放在 tools\sas\data（Apache Jena TDB），路径相对“启动时的工作目录”，
      所以本脚本会先切到 tools\sas 再启动，切勿从别处启动，否则会另建一个空库。
    - 上次进程被强杀会留下 data\tdb.lock；在确认端口没被占用时自动清理。

.PARAMETER Port
    监听端口，默认 8888。

.EXAMPLE
    .\start-sas.ps1
    .\start-sas.ps1 -Port 8899
#>
param(
    [int]$Port = 8888
)

$ErrorActionPreference = 'Stop'
$sasDir = Join-Path $PSScriptRoot 'sas'
$war = Join-Path $sasDir 'simpleAnnotationStore.war'
$runner = Join-Path $sasDir 'dependency\jetty-runner.jar'

if (-not (Test-Path $war)) { throw "找不到 $war，请先解压 sas.zip 到 tools\sas" }
if (-not (Test-Path $runner)) { throw "找不到 $runner" }

$listener = & netstat -ano | Where-Object {
    $_ -match '^\s*TCP\s+\S+:(\d+)\s+\S+\s+LISTENING\s+(\d+)\s*$' -and [int]$matches[1] -eq $Port
}
if ($listener) {
    $ownerPid = ($listener[0] -split '\s+')[-1]
    throw "端口 $Port 已被进程 $ownerPid 占用；先运行 .\stop-services.ps1 或换端口"
}

$lock = Join-Path $sasDir 'data\tdb.lock'
if (Test-Path $lock) {
    Remove-Item $lock -Force
    Write-Host "已清理上次残留的 Jena 锁文件 data\tdb.lock" -ForegroundColor DarkYellow
}

# Jetty 启动时要在 java.io.tmpdir 下解压 WAR。本机用户 Temp 目录权限异常时会
# 报 "java.io.IOException: 拒绝访问"，Web 应用变成 UNAVAILABLE（访问即 503）。
# 因此固定使用工作区内的 tmp 目录，不依赖系统 Temp。
$tmpDir = Join-Path $sasDir 'tmp'
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
Get-ChildItem $tmpDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "SimpleAnnotationServer 启动中 ..." -ForegroundColor Cyan
Write-Host "  界面      http://localhost:$Port/index.html" -ForegroundColor Gray
Write-Host "  标注数据  $sasDir\data" -ForegroundColor Gray
Write-Host "  临时目录  $tmpDir" -ForegroundColor Gray
Write-Host "  停止      Ctrl+C" -ForegroundColor Gray

Set-Location $sasDir
java "-Djava.io.tmpdir=$tmpDir" -jar 'dependency\jetty-runner.jar' --port $Port 'simpleAnnotationStore.war'
