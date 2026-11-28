<#
.SYNOPSIS
    启动本地 IIIF 发布服务（Image API 2.0 Level 2 + Presentation API）。

.DESCRIPTION
    两类图源：
      - 默认：data\demo-images（3 页示例，按文件夹发布）；没有该目录时回退单张《历代帝王图》卷（id=scroll）；
      - data\image-source.conf 里指定了文件夹时，按文件夹发布：
        每个子文件夹一个 Manifest，根目录图片另成一个（与 etu 的导入规则一致）。
    服务同时提供：
        {base}/iiif/2/{id}/...        图像 API（任意区域）
        {base}/manifest/{key}.json    Presentation 2 Manifest
        {base}/manifest/{key}-v3.json Presentation 3 Manifest
        {base}/collection.json        Collection

    - 后台启动，日志在 logs\image-server.*.log，PID 写在 logs\iiif-image.pid。
    - 端口已被本服务占用时提示"已在运行"并正常退出。
    - 停止：.\stop-services.ps1（已把 8182 加入默认端口）或 Stop-Process -Id <PID>。

    运行环境：
      - 优先使用环境变量 IIIF_PYTHON；
      - 其次使用 PATH 中的 python（自动跳过 WindowsApps 应用别名）；
      - 再找 %LOCALAPPDATA%\Programs\Python\Python*\python.exe 等常见安装目录；
      - 最后尝试 py -3 启动器。
      需要 Python 3.10+ 且已安装 Pillow。

.PARAMETER Port
    监听端口，默认 8182。

.PARAMETER HostAddress
    绑定地址，默认 127.0.0.1（只本机可访问）。要给局域网用传 0.0.0.0。

.PARAMETER ImagePath
    单图模式下的图片路径；未指定时回退 Research 卷或 demo page1。

.PARAMETER Id
    单图模式下的 IIIF 标识符，默认 scroll。

.EXAMPLE
    .\start-image.ps1
    .\start-image.ps1 -Port 8183 -ImagePath "D:\scan\scroll.tif" -Id myscroll
#>
param(
    [int]$Port = 8182,
    [string]$HostAddress = '127.0.0.1',
    [string]$ImagePath = '',
    [string]$Id = 'scroll'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# 图源：data\image-source.conf 里指定了文件夹时，按文件夹发布（每子文件夹一个 Manifest）
$sourceConf = Join-Path $PSScriptRoot 'data\image-source.conf'
$sourceRoot = $null
if (Test-Path $sourceConf) {
    $candidate = (Get-Content $sourceConf -Raw -Encoding UTF8).Trim()
    if ($candidate -and (Test-Path $candidate -PathType Container)) { $sourceRoot = $candidate }
}

# 默认图源：data\demo-images（按文件夹发布，3 页示例图）
if (-not $sourceRoot -and [string]::IsNullOrWhiteSpace($ImagePath)) {
    $defaultRoot = Join-Path $PSScriptRoot 'data\demo-images'
    if (Test-Path $defaultRoot -PathType Container) { $sourceRoot = $defaultRoot }
}

if (-not $sourceRoot) {
    if ([string]::IsNullOrWhiteSpace($ImagePath)) {
        $candidates = @(
            (Join-Path $root 'Research\The 13 Emperors Scroll\The 13 Emperors Scroll.png'),
            (Join-Path $PSScriptRoot 'data\demo-images\page1.png')
        )
        $ImagePath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $ImagePath -or -not (Test-Path $ImagePath)) {
        throw "找不到图像：$ImagePath。用 -ImagePath 显式指定，或在控制台选择图源文件夹"
    }
}

function Find-Python {
    # 1) 显式指定
    if ($env:IIIF_PYTHON -and (Test-Path $env:IIIF_PYTHON -PathType Leaf)) {
        return @{ Path = $env:IIIF_PYTHON; Prefix = @() }
    }

    # 2) PATH 中的 python；跳过 Microsoft Store 的 WindowsApps 存根
    $cmd = Get-Command python -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source -and $cmd.Source -notmatch '\\WindowsApps\\') {
        return @{ Path = $cmd.Source; Prefix = @() }
    }

    # 3) 常见安装目录（用户级 / 系统级）
    $searchDirs = @()
    if ($env:LOCALAPPDATA) { $searchDirs += (Join-Path $env:LOCALAPPDATA 'Programs\Python') }
    if ($env:ProgramFiles) { $searchDirs += (Join-Path $env:ProgramFiles 'Python') }
    if (${env:ProgramFiles(x86)}) { $searchDirs += (Join-Path ${env:ProgramFiles(x86)} 'Python') }
    foreach ($dir in $searchDirs) {
        if (-not (Test-Path $dir)) { continue }
        $exe = Get-ChildItem $dir -Directory -Filter 'Python*' -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'python.exe' } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
        if ($exe) { return @{ Path = $exe; Prefix = @() } }
    }

    # 4) py 启动器
    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($py -and $py.Source -and $py.Source -notmatch '\\WindowsApps\\') {
        return @{ Path = $py.Source; Prefix = @('-3') }
    }
    return $null
}


function Get-ListenerPid([int]$p) {
    $line = & netstat -ano | Where-Object {
        $_ -match '^\s*TCP\s+\S+:(\d+)\s+\S+\s+LISTENING\s+(\d+)\s*$' -and [int]$matches[1] -eq $p
    } | Select-Object -First 1
    if (-not $line) { return $null }
    return [int](($line -split '\s+')[-1])
}

try {
    $owner = Get-ListenerPid $Port
    if ($owner) {
        $proc = Get-Process -Id $owner -ErrorAction SilentlyContinue
        $name = if ($proc) { $proc.ProcessName } else { '未知' }
        if ($name -match '^python') {
            Write-Host "图像服务已在运行：http://${HostAddress}:$Port/（进程 $owner）" -ForegroundColor Green
            Write-Host "如需重启：先 .\stop-services.ps1，再运行本脚本" -ForegroundColor Gray
            exit 0
        }
        throw "端口 $Port 被其他程序占用（进程 $owner，$name）。换端口：.\start-image.ps1 -Port 8183"
    }

    $pyInfo = Find-Python
    if (-not $pyInfo) {
        throw "找不到 Python 3.10+。请安装 Python 并加入 PATH，或设置环境变量 IIIF_PYTHON 指向 python.exe。"
    }
    $py = $pyInfo.Path
    $pyPrefix = $pyInfo.Prefix
    $script = Join-Path $PSScriptRoot 'iiif_image_server.py'
    if (-not (Test-Path $script)) { throw "找不到 $script" }

    $pillowArgs = @()
    $pillowArgs += $pyPrefix
    $pillowArgs += @('-c', 'import PIL')
    & $py @pillowArgs 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Python $py 缺少 Pillow。请运行：& '$py' -m pip install Pillow==12.3.0"
    }

    $logDir = Join-Path $PSScriptRoot 'logs'
    New-Item -ItemType Directory -Force $logDir | Out-Null
    $outLog = Join-Path $logDir 'image-server.out.log'
    $errLog = Join-Path $logDir 'image-server.err.log'

    Write-Host "图像服务启动中 ..." -ForegroundColor Cyan
    if ($sourceRoot) {
        Write-Host "  图源  $sourceRoot（按文件夹发布）" -ForegroundColor Gray
    } else {
        Write-Host "  图像  $ImagePath" -ForegroundColor Gray
    }
    Write-Host "  端口  ${HostAddress}:$Port" -ForegroundColor Gray
    Write-Host "  Python $py" -ForegroundColor Gray

    if ($sourceRoot) {
        $argList = @("`"$script`"", '--host', $HostAddress, '--port', "$Port",
                     '--root', "`"$sourceRoot`"")
    } else {
        $argList = @("`"$script`"", '--host', $HostAddress, '--port', "$Port",
                     '--image', "`"$Id=$ImagePath`"")
    }
    $fullArgs = @()
    $fullArgs += $pyPrefix
    $fullArgs += $argList
    $proc = Start-Process -FilePath $py -ArgumentList $fullArgs `
        -WindowStyle Hidden -RedirectStandardOutput $outLog -RedirectStandardError $errLog -PassThru

    Set-Content -Path (Join-Path $logDir 'iiif-image.pid') -Value $proc.Id -Encoding ASCII

    $up = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        if (Get-ListenerPid $Port) { $up = $true; break }
        if ($proc.HasExited) { break }
    }
    if (-not $up) { throw "启动失败（进程 $($proc.Id)）。看日志：$errLog" }

    Write-Host "IIIF 发布服务已启动（进程 $($proc.Id)）" -ForegroundColor Green
    Write-Host "  索引       http://${HostAddress}:$Port/"
    Write-Host "  Collection http://${HostAddress}:$Port/collection.json"
    if (-not $sourceRoot) {
        Write-Host "  Manifest   http://${HostAddress}:$Port/manifest/$Id.json"
        Write-Host "  区域示例   http://${HostAddress}:$Port/iiif/2/$Id/4761,34,58,114/full/0/default.jpg"
    } else {
        Write-Host "  Manifest   见索引页（每个子文件夹一个）"
    }
    Write-Host "  停止       .\stop-services.ps1" -ForegroundColor Gray
} catch {
    Write-Host ""
    Write-Host "启动失败：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
