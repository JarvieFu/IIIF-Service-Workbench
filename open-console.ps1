<#
.SYNOPSIS
    启动 IIIF 服务控制台。

.DESCRIPTION
    通过 CreateProcess 直接启动 exe（UseShellExecute = false），绕过资源管理器
    对可执行文件的“打开文件 - 安全警告”附件检查。桌面快捷方式指向本脚本。
#>
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'IIIF服务控制台.exe'
if (-not (Test-Path $exe)) { throw "找不到 $exe" }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.WorkingDirectory = $PSScriptRoot
$psi.UseShellExecute = $false
[System.Diagnostics.Process]::Start($psi) | Out-Null
