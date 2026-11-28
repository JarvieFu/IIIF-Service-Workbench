<#
.SYNOPSIS
    构建 IIIF服务控制台.exe（界面版本固定 v1.0.0，并写入仅开发可见的构建戳）。

.DESCRIPTION
    - 生成 ui\BuildInfo.cs：AssemblyVersion / FileVersion = 1.0.0.0，
      AssemblyInformationalVersion 与 BuildInfo.Stamp = v1.0.0+时间戳；
    - 用 csc 编译源码（图标 + DPI 清单），再调用 sign-console.ps1 签名；
    - 把 时间、构建戳、SHA256 追加到 ui\build-history.txt。
    构建戳不显示在任何界面；需要识别构建时运行 exe --test version，
    或查看文件属性里的“产品版本”，或查 build-history.txt。

.PARAMETER NoSign
    只编译，不签名（调试用）。
#>
param(
    [switch]$NoSign
)

$ErrorActionPreference = 'Stop'
$ui = $PSScriptRoot
$bundle = Split-Path -Parent $ui
$stamp = 'v1.0.0+' + (Get-Date -Format 'yyyyMMdd-HHmmss')

$cs = @"
using System.Reflection;
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("$stamp")]

namespace IiifConsole
{
    internal static class BuildInfo
    {
        public const string Stamp = "$stamp";
    }
}
"@
[System.IO.File]::WriteAllText((Join-Path $ui 'BuildInfo.cs'), $cs, (New-Object System.Text.UTF8Encoding($true)))

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $bundle 'IIIF服务控制台.exe'
Remove-Item $exe -Force -ErrorAction SilentlyContinue

& $csc /nologo /codepage:65001 /target:winexe /optimize+ `
    /win32icon:"$ui\app.ico" /win32manifest:"$ui\app.manifest" `
    /out:"$exe" /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    (Join-Path $ui 'ServiceConsole.cs') (Join-Path $ui 'BuildInfo.cs')

if ($LASTEXITCODE -ne 0) { throw "编译失败（csc 退出码 $LASTEXITCODE）" }

if (-not $NoSign) { & (Join-Path $bundle 'sign-console.ps1') }

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
$line = '{0}  {1}  {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $stamp, $hash
Add-Content -Path (Join-Path $ui 'build-history.txt') -Value $line -Encoding utf8

"stamp : $stamp"
"sha256: $hash"
