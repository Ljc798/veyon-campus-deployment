param(
    [string]$HostName = "PC-01",
    [string]$BaseUrl = "http://127.0.0.1:11080/api/v1",
    [string]$KeyDir = "C:\ProgramData\Veyon\keys\private\CDPPLHAJJHEOHFJOGOJCGFKGIDKJOKNCA",
    [string]$KeyName = "CDPPLHAJJHEOHFJOGOJCGFKGIDKJOKNCA"
)

$ErrorActionPreference = "Stop"
$pem = Get-Content -Raw -Path (Join-Path $KeyDir "key")

function Connect-Host([string]$h) {
    $methods = Invoke-RestMethod -Uri "$BaseUrl/authentication/$h" -TimeoutSec 30
    $method = $methods.methods[0]
    $body = @{ method = $method; credentials = @{ keyname = $KeyName; keydata = $pem } } | ConvertTo-Json -Compress -Depth 4
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-WebRequest -Uri "$BaseUrl/authentication/$h" -Method Post -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -ContentType "application/json" -UseBasicParsing -TimeoutSec 60
    $sw.Stop()
    $json = $r.Content | ConvertFrom-Json
    return @{ Uid = $json.'connection-uid'; Ms = $sw.ElapsedMilliseconds }
}

# A. burst throughput (no artificial pacing)
$conn = Connect-Host $HostName
"burst_auth_ms=$($conn.Ms)"
$h = @{ "Connection-Uid" = $conn.Uid }
$url = "$BaseUrl/framebuffer?width=960&height=540&format=jpg&compression=5&quality=80"
$waitSw = [System.Diagnostics.Stopwatch]::StartNew()
$attempts = 0
while ($waitSw.Elapsed.TotalSeconds -lt 30) {
    $attempts++
    try { $probe = Invoke-WebRequest -Uri $url -Headers $h -UseBasicParsing -TimeoutSec 30; break }
    catch {
        $code = 0; if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
        if ($code -ne 503) { throw }
        Start-Sleep -Milliseconds 200
    }
}
$waitSw.Stop()
"first_frame_ms=$($waitSw.ElapsedMilliseconds) attempts=$attempts"

$proc = Get-Process veyon-service
$cpu0 = $proc.TotalProcessorTime.TotalMilliseconds
$times = @()
$sizes = @()
$swAll = [System.Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt 40; $i++) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-WebRequest -Uri $url -Headers $h -UseBasicParsing -TimeoutSec 30
    $sw.Stop()
    $times += $sw.ElapsedMilliseconds
    $sizes += $r.RawContentStream.Length
}
$swAll.Stop()
$proc.Refresh()
$cpu1 = $proc.TotalProcessorTime.TotalMilliseconds
"burst_frames=40 total_ms=$($swAll.ElapsedMilliseconds) fps=$([math]::Round(40000/$swAll.ElapsedMilliseconds,1))"
"burst_avg_ms=$([math]::Round(($times|Measure-Object -Average).Average,1)) min=$(($times|Measure-Object -Minimum).Minimum) max=$(($times|Measure-Object -Maximum).Maximum)"
"burst_avg_kb=$([math]::Round((($sizes|Measure-Object -Average).Average)/1024,1)) service_cpu_ms_over_burst=$([math]::Round($cpu1-$cpu0,0))"

# B. idle expiry (IdleTimeout default 60s)
"idle_sleep_s=65"
Start-Sleep -Seconds 65
try {
    $r = Invoke-WebRequest -Uri $url -Headers $h -UseBasicParsing -TimeoutSec 15
    "after_idle=HTTP $($r.StatusCode)"
} catch {
    $code = 0; if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
    "after_idle=HTTP $code body=$($_.ErrorDetails.Message)"
}

# C. unreachable host (auth timing / error surface)
$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $r = Invoke-WebRequest -Uri "$BaseUrl/authentication/192.0.2.10" -UseBasicParsing -TimeoutSec 120
    "offline_host=HTTP $($r.StatusCode) in $($sw.ElapsedMilliseconds) ms"
} catch {
    $sw.Stop()
    $code = 0; if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
    "offline_host=HTTP $code in $($sw.ElapsedMilliseconds) ms body=$($_.ErrorDetails.Message -replace "`r?`n",' ')"
}

# D. re-auth after idle works
$conn2 = Connect-Host $HostName
"reauth_ms=$($conn2.Ms) uid=$($conn2.Uid)"
try {
    $r = Invoke-WebRequest -Uri $url -Headers @{ "Connection-Uid" = $conn2.Uid } -UseBasicParsing -TimeoutSec 30
    "reauth_frame=HTTP $($r.StatusCode) bytes=$($r.RawContentStream.Length)"
} catch {
    "reauth_frame=ERR $($_.Exception.Message)"
}
Invoke-WebRequest -Uri "$BaseUrl/authentication/$HostName" -Method Delete -Headers @{ "Connection-Uid" = $conn2.Uid } -UseBasicParsing -TimeoutSec 10 | Out-Null

# E. unsupported format + oversized dimensions
try {
    Invoke-WebRequest -Uri "$BaseUrl/framebuffer?format=xyz" -Headers @{ "Connection-Uid" = $conn2.Uid } -UseBasicParsing -TimeoutSec 10 | Out-Null
    "bad_format=unexpected 200"
} catch {
    $code = 0; if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
    "bad_format=HTTP $code"
}
