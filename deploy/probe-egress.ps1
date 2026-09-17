#requires -Version 5.1
<#
.SYNOPSIS
    Assess the outbound policy of a target network.

.DESCRIPTION
    Answers one question: which outbound TCP ports actually work from this machine?
    Run it ON the restricted machine. Nothing is installed and nothing is modified.

    Strategy:
      1. DNS      - can we resolve public names at all?
      2. TCP      - can we complete a TCP handshake to a given host:port?
      3. HTTP/TLS - can we actually exchange DATA (defeats transparent proxies that
                    accept every SYN and then silently drop).

    portquiz.net is used as the primary target because its servers listen on EVERY TCP
    port and echo back an HTTP response, so it is the canonical egress test.

.PARAMETER Endpoint
    Optional extra "host:port" to test, e.g. your frp node "node.sakurafrp.com:40123"
    or your candidate relay "rc.example.com:443".

.EXAMPLE
    .\probe-egress.ps1
    .\probe-egress.ps1 -Endpoint "node-xx.sakurafrp.com:40123"
    .\probe-egress.ps1 -Ports 443,80,8080,40123

.NOTES
    ASCII-only on purpose: Windows PowerShell 5.1 reads UTF-8 files without a BOM using the
    ANSI code page, which corrupts non-ASCII text and can break parsing.
#>
param(
    [string[]]$Hosts = @('portquiz.net'),
    [string[]]$Ports = @('443', '80', '8080', '8443', '2052', '2082', '2086', '2095', '10000', '12345', '40123'),
    [string]$Endpoint = '',
    [int]$TimeoutMs = 4000
)

$ErrorActionPreference = 'Continue'

# Normalise. When the script is launched with "powershell -File", an argument like
# "-Ports 443,80,12345" arrives as ONE string, so split every element on commas.
$portList = New-Object System.Collections.Generic.List[int]
foreach ($entry in $Ports) {
    foreach ($piece in ([string]$entry).Split(',')) {
        $value = 0
        if ([int]::TryParse($piece.Trim(), [ref]$value)) { [void]$portList.Add($value) }
    }
}

$hostList = New-Object System.Collections.Generic.List[string]
foreach ($entry in $Hosts) {
    foreach ($piece in ([string]$entry).Split(',')) {
        $trimmed = $piece.Trim()
        if ($trimmed.Length -gt 0) { [void]$hostList.Add($trimmed) }
    }
}

function Get-IpOrNull {
    param([string]$Name)
    try {
        $addresses = [System.Net.Dns]::GetHostAddresses($Name)
        if ($addresses.Count -gt 0) { return $addresses[0].IPAddressToString }
    } catch { }
    return $null
}

function Test-Tcp {
    param([string]$TargetHost, [int]$TargetPort, [int]$Timeout)
    $client = New-Object System.Net.Sockets.TcpClient
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $async = $client.BeginConnect($TargetHost, $TargetPort, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne($Timeout, $false)) {
            return [pscustomobject]@{ Ok = $false; Ms = $Timeout; Note = 'timeout (filtered)' }
        }
        $client.EndConnect($async)
        $watch.Stop()
        return [pscustomobject]@{ Ok = $true; Ms = $watch.ElapsedMilliseconds; Note = 'syn-ack' }
    } catch {
        $inner = $_.Exception.InnerException
        $message = if ($null -ne $inner) { $inner.Message } else { $_.Exception.Message }
        return [pscustomobject]@{ Ok = $false; Ms = $watch.ElapsedMilliseconds; Note = $message }
    } finally {
        $client.Close()
    }
}

function Test-Data {
    param([string]$TargetHost, [int]$TargetPort, [int]$Timeout)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect($TargetHost, $TargetPort, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne($Timeout, $false)) { return $null }
        $client.EndConnect($async)
        $client.ReceiveTimeout = $Timeout
        $client.SendTimeout = $Timeout
        $stream = $client.GetStream()
        $payload = [System.Text.Encoding]::ASCII.GetBytes("GET / HTTP/1.0`r`nHost: $TargetHost`r`nConnection: close`r`n`r`n")
        $stream.Write($payload, 0, $payload.Length)
        $stream.Flush()
        $buffer = New-Object byte[] 256
        $read = $stream.Read($buffer, 0, $buffer.Length)
        if ($read -le 0) { return $null }
        $text = [System.Text.Encoding]::ASCII.GetString($buffer, 0, $read)
        $firstLine = ($text -split "`r?`n")[0]
        if ($firstLine -match '^HTTP/') { return $firstLine }
        return "unexpected: $firstLine"
    } catch {
        return $null
    } finally {
        $client.Close()
    }
}

Write-Host ""
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host " Egress probe  (run this on the machine whose network you want to test)" -ForegroundColor Cyan
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host ""

# ---- 1. DNS ----------------------------------------------------------------
Write-Host "[1] DNS resolution" -ForegroundColor Yellow
foreach ($name in @('portquiz.net', 'www.baidu.com', 'one.one.one.one')) {
    $ip = Get-IpOrNull -Name $name
    if ($null -ne $ip) {
        Write-Host ("    {0,-20} -> {1}" -f $name, $ip)
    } else {
        Write-Host ("    {0,-20} -> FAILED" -f $name) -ForegroundColor Red
    }
}
Write-Host ""

# ---- 2. Per-port TCP + data test -------------------------------------------
foreach ($target in $hostList) {
    Write-Host "[2] Target host: $target" -ForegroundColor Yellow
    Write-Host ("    {0,-8} {1,-14} {2,-10} {3}" -f 'PORT', 'TCP', 'LATENCY', 'DATA EXCHANGE')
    Write-Host ("    {0,-8} {1,-14} {2,-10} {3}" -f '----', '---', '-------', '-------------')

    foreach ($port in $portList) {
        $tcp = Test-Tcp -TargetHost $target -TargetPort $port -Timeout $TimeoutMs
        if ($tcp.Ok) {
            $data = Test-Data -TargetHost $target -TargetPort $port -Timeout $TimeoutMs
            $dataText = if ($null -ne $data) { $data } else { 'no response (likely MITM/drop)' }
            $dataOk = ($null -ne $data) -and ($data -like 'HTTP/*')
            $colour = if ($dataOk) { 'Green' } else { 'DarkYellow' }
            Write-Host ("    {0,-8} {1,-14} {2,-10} {3}" -f $port, 'OPEN', "$($tcp.Ms)ms", $dataText) -ForegroundColor $colour
        } else {
            Write-Host ("    {0,-8} {1,-14} {2,-10} {3}" -f $port, 'BLOCKED', "$($tcp.Ms)ms", $tcp.Note) -ForegroundColor Red
        }
    }
    Write-Host ""
}

# ---- 3. Explicit endpoint (your frp node / candidate relay) ----------------
if ($Endpoint -ne '') {
    $parts = $Endpoint.Split(':')
    if ($parts.Length -ne 2) {
        Write-Host "Endpoint must look like host:port" -ForegroundColor Red
    } else {
        $hostName = $parts[0]
        $portNumber = [int]$parts[1]
        Write-Host "[3] Your endpoint: $hostName : $portNumber" -ForegroundColor Yellow
        $ip = Get-IpOrNull -Name $hostName
        Write-Host ("    DNS        : {0}" -f $(if ($null -ne $ip) { $ip } else { 'FAILED' }))
        $tcp = Test-Tcp -TargetHost $hostName -TargetPort $portNumber -Timeout $TimeoutMs
        if ($tcp.Ok) {
            Write-Host ("    TCP        : OPEN ({0} ms)" -f $tcp.Ms) -ForegroundColor Green
            $data = Test-Data -TargetHost $hostName -TargetPort $portNumber -Timeout $TimeoutMs
            if ($null -ne $data) {
                Write-Host ("    Data       : {0}" -f $data) -ForegroundColor Green
            } else {
                Write-Host "    Data       : connected but no HTTP response (expected for a raw ws:// relay - TCP is what matters)" -ForegroundColor DarkYellow
            }
        } else {
            Write-Host ("    TCP        : BLOCKED - {0}" -f $tcp.Note) -ForegroundColor Red
        }
        Write-Host ""
    }
}

# ---- 4. Verdict ------------------------------------------------------------
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host " How to read this" -ForegroundColor Cyan
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host " * 443 OPEN  with HTTP data  -> you can reach a normal HTTPS site."
Write-Host " * a high port OPEN with HTTP data -> Route A (frp on that port) will work."
Write-Host " * only 443/80 OPEN, high ports BLOCKED -> you MUST get a 443 relay:"
Write-Host "     - Cloudflare Tunnel (free, needs a domain)  -> see deploy/README.md Route C"
Write-Host "     - or a cheap HK VPS on port 443"
Write-Host " * 'OPEN' but 'no response' on every port -> transparent proxy / MITM device:"
Write-Host "     the network accepts connections then inspects them. Expect TLS interception."
Write-Host ""
