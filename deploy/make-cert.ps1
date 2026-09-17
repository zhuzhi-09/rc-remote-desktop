# 生成一个自签名服务器证书（仅用于「无自有域名 + 走 sakurafrp」的场景）
# 有自己域名时请用 Caddy 自动申请 Let's Encrypt，不要用自签名。
#
# 用法：
#   .\make-cert.ps1 -DnsName rc.example.com
#
# 产物：rcrelay.pfx（给中转服务用，密码 changeit）
#       随后用 .\fingerprint.ps1 取指纹填到 agent/controller 配置的 pinnedCertSha256

param(
    [Parameter(Mandatory = $true)]
    [string]$DnsName,

    [string]$OutFile = "rcrelay.pfx",
    [string]$Password = "changeit",
    [int]$Years = 5
)

$ErrorActionPreference = "Stop"

$cert = New-SelfSignedCertificate `
    -DnsName $DnsName `
    -CertStoreLocation "Cert:\LocalMachine\My" `
    -NotAfter (Get-Date).AddYears($Years) `
    -Type SSLServerAuthentication `
    -KeyAlgorithm RSA -KeyLength 2048

$secure = ConvertTo-SecureString -String $Password -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $OutFile -Password $secure | Out-Null

Write-Host "已生成证书: $OutFile  (密码: $Password)" -ForegroundColor Green
Write-Host "DNS 名称  : $DnsName"
Write-Host ""
Write-Host "接下来取指纹（用于证书固定）:" -ForegroundColor Cyan
Write-Host "  .\fingerprint.ps1 -ServerName $DnsName -Port 443"
Write-Host ""
Write-Host "把 $OutFile 上传到中转服务器，并在 appsettings.json 里启用:"
Write-Host @'
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:8443",
        "Certificate": { "Path": "rcrelay.pfx", "Password": "changeit" }
      }
    }
  }
'@
