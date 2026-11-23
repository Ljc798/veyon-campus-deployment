param(
    [string]$HostName = "PC-01",
    [string]$BaseUrl = "http://127.0.0.1:11080/api/v1",
    [string]$KeyDir = "C:\ProgramData\Veyon\keys\private\CDPPLHAJJHEOHFJOGOJCGFKGIDKJOKNCA",
    [string]$KeyName = "CDPPLHAJJHEOHFJOGOJCGFKGIDKJOKNCA"
)

$pem = Get-Content -Raw -Path (Join-Path $KeyDir "key")
$body = @{ method = "0c69b301-81b4-42d6-8fae-128cdd113314"; credentials = @{ keyname = $KeyName; keydata = $pem } } | ConvertTo-Json -Compress -Depth 4

function New-Session {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $swA = [System.Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-WebRequest -Uri "$BaseUrl/authentication/$HostName" -Method Post -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -ContentType "application/json" -UseBasicParsing -TimeoutSec 60
    $swA.Stop()
    $uid = ($r.Content | ConvertFrom-Json).'connection-uid'
    $h = @{ "Connection-Uid" = $uid }
    $url = "$BaseUrl/framebuffer?width=960&height=540&format=jpg&compression=5&quality=80"
    $attempts = 0
    $firstMs = -1
    while ($sw.Elapsed.TotalSeconds -lt 30) {
        $attempts++
        try { Invoke-WebRequest -Uri $url -Headers $h -UseBasicParsing -TimeoutSec 30 | Out-Null; $firstMs = $sw.ElapsedMilliseconds; break }
        catch {
            $code = 0; if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
            if ($code -ne 503) { "unexpected HTTP $code"; break }
            Start-Sleep -Milliseconds 200
        }
    }
    return [pscustomobject]@{ Uid = $uid; AuthMs = $swA.ElapsedMilliseconds; FirstFrameMs = $firstMs; Attempts = $attempts }
}

# 3 cold connection cycles
for ($i = 1; $i -le 3; $i++) {
    $s = New-Session
    "cycle_$i auth_ms=$($s.AuthMs) first_frame_ms=$($s.FirstFrameMs) attempts=$($s.Attempts)"

    if ($i -eq 1) {
        # invalid image format on a live connection
        try {
            Invoke-WebRequest -Uri "$BaseUrl/framebuffer?format=xyz" -Headers @{ "Connection-Uid" = $s.Uid } -UseBasicParsing -TimeoutSec 10 | Out-Null
            "bad_format=unexpected 200"
        } catch {
            $code = 0; if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
            "bad_format=HTTP $code"
        }
        # CPU cost at a realistic 10 fps polling rate
        $url = "$BaseUrl/framebuffer?width=960&height=540&format=jpg&compression=5&quality=80"
        $h = @{ "Connection-Uid" = $s.Uid }
        $proc = Get-Process veyon-service
        $cpu0 = $proc.TotalProcessorTime.TotalMilliseconds
        $frames = 0
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($sw.Elapsed.TotalSeconds -lt 10) {
            Invoke-WebRequest -Uri $url -Headers $h -UseBasicParsing -TimeoutSec 10 | Out-Null
            $frames++
            Start-Sleep -Milliseconds 95
        }
        $proc.Refresh()
        $cpu1 = $proc.TotalProcessorTime.TotalMilliseconds
        "ten_fps frames=$frames wall_ms=$($sw.ElapsedMilliseconds) service_cpu_ms=$([math]::Round($cpu1-$cpu0,0)) service_cpu_pct=$([math]::Round(($cpu1-$cpu0)/$sw.ElapsedMilliseconds*100,1))"
    }

    $swC = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-WebRequest -Uri "$BaseUrl/authentication/$HostName" -Method Delete -Headers @{ "Connection-Uid" = $s.Uid } -UseBasicParsing -TimeoutSec 10 | Out-Null
    $swC.Stop()
    "cycle_$i close_ms=$($swC.ElapsedMilliseconds)"
}
