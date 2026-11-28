<#
.SYNOPSIS
    给 IIIF服务控制台.exe 做本地代码签名（去掉“未知发布者”警告）。

.DESCRIPTION
    使用当前用户证书库里名为 "CN=IIIF Research Workbench" 的自签名代码签名证书：
      - 证书不存在时创建一张（10 年有效）；
      - 把公钥导入“受信任的发布者”和“受信任的根证书颁发机构”（仅当前用户）；
      - 给 exe 签名并校验状态。
    重新编译 exe 后需要再运行一次本脚本。

.EXAMPLE
    .\sign-console.ps1
#>
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'IIIF服务控制台.exe'
$subject = 'CN=IIIF Research Workbench'

if (-not (Test-Path $exe)) { throw "找不到 $exe" }

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $subject } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
        -KeyUsage DigitalSignature -FriendlyName 'IIIF Research Workbench' `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(10)
    $tmp = Join-Path $env:TEMP 'iiif-cert.cer'
    Export-Certificate -Cert $cert -FilePath $tmp -Force | Out-Null
    Import-Certificate -FilePath $tmp -CertStoreLocation Cert:\CurrentUser\TrustedPublisher | Out-Null
    certutil -user -addstore -f Root $tmp | Out-Null
}

Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256 | Out-Null
$sig = Get-AuthenticodeSignature -FilePath $exe
"签名状态：$($sig.Status)  发行者：$($sig.SignerCertificate.Subject)"
if ($sig.Status -ne 'Valid') { exit 1 }
