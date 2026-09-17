# 读取远端 TLS 证书的 SHA-256 指纹，填入 agent.config.json / controller.config.json
# 的 pinnedCertSha256 字段，实现证书固定（防中间人）。
#
# 用法：
#   .\fingerprint.ps1 -ServerName rc.example.com -Port 443
#   .\fingerprint.ps1 -ServerName node.example.com -Port 12345

param(
    [Parameter(Mandatory = $true)]
    [string]$ServerName,

    [int]$Port = 443
)

$ErrorActionPreference = "Stop"

$tcp = New-Object System.Net.Sockets.TcpClient
try {
    $tcp.Connect($ServerName, $Port)
    $callback = [System.Net.Security.RemoteCertificateValidationCallback] { param($s, $c, $ch, $e) $true }
    $ssl = New-Object System.Net.Security.SslStream($tcp.GetStream(), $false, $callback)
    try {
        $ssl.AuthenticateAsClient($ServerName)
        $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($ssl.RemoteCertificate)
        $sha = [System.Security.Cryptography.SHA256]::Create().ComputeHash($cert.RawData)
        $hex = ([System.BitConverter]::ToString($sha) -replace '-', '')

        Write-Host "Subject : $($cert.Subject)"
        Write-Host "Issuer  : $($cert.Issuer)"
        Write-Host "有效期至: $($cert.NotAfter)"
        Write-Host ""
        Write-Host "pinnedCertSha256 = $hex" -ForegroundColor Green
        Write-Host ""
        Write-Host "把上面这行填进 agent.config.json 与 controller.config.json。" -ForegroundColor Cyan
        Write-Host "注意：若使用 Let's Encrypt（Caddy 自动申请），证书每 ~60 天轮换，" -ForegroundColor Yellow
        Write-Host "      此时不要固定指纹，改为 allowUntrustedCert=false + 依赖系统信任链。" -ForegroundColor Yellow
    }
    finally {
        $ssl.Dispose()
    }
}
finally {
    $tcp.Dispose()
}
