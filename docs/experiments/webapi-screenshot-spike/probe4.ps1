param(
    [string]$HostName = "PC-01",
    [string]$BaseUrl = "http://127.0.0.1:11080/api/v1",
    [string]$KeyDir = "C:\ProgramData\Veyon\keys\private\CDPPLHAJJHEOHFJOGOJCGFKGIDKJOKNCA",
    [string]$KeyName = "CDPPLHAJJHEOHFJOGOJCGFKGIDKJOKNCA"
)

$pem = Get-Content -Raw -Path (Join-Path $KeyDir "key")
$body = @{ method = "0c69b301-81b4-42d6-8fae-128cdd113314"; credentials = @{ keyname = $KeyName; keydata = $pem } } | ConvertTo-Json -Compress -Depth 4
$r = Invoke-WebRequest -Uri "$BaseUrl/authentication/$HostName" -Method Post -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -ContentType "application/json" -UseBasicParsing -TimeoutSec 60
$uid = ($r.Content | ConvertFrom-Json).'connection-uid'
$h = @{ "Connection-Uid" = $uid }
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt 30) {
    try { Invoke-WebRequest -Uri "$BaseUrl/framebuffer?width=320&height=180&format=jpg" -Headers $h -UseBasicParsing -TimeoutSec 20 | Out-Null; break }
    catch { Start-Sleep -Milliseconds 200 }
}

foreach ($cfg in @(
    @{ w = 320; h = 180; f = "jpg"; q = 60 },
    @{ w = 640; h = 360; f = "jpg"; q = 80 },
    @{ w = 960; h = 540; f = "jpg"; q = 80 },
    @{ w = 1280; h = 720; f = "jpg"; q = 85 },
    @{ w = 0; h = 0; f = "jpg"; q = 80 },
    @{ w = 0; h = 0; f = "png"; q = 80 }
)) {
    $q = if ($cfg.w -gt 0) { "&width=$($cfg.w)&height=$($cfg.h)" } else { "" }
    $url = "$BaseUrl/framebuffer?format=$($cfg.f)&compression=5&quality=$($cfg.q)$q"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $resp = Invoke-WebRequest -Uri $url -Headers $h -UseBasicParsing -TimeoutSec 30
    $sw.Stop()
    "{0}x{1} {2} q{3}: {4} KB in {5} ms" -f $cfg.w, $cfg.h, $cfg.f, $cfg.q, [math]::Round($resp.RawContentStream.Length/1024,1), $sw.ElapsedMilliseconds
}

# degraded auth: unknown key name
$badBody = @{ method = "0c69b301-81b4-42d6-8fae-128cdd113314"; credentials = @{ keyname = "UNKNOWNKEY"; keydata = $pem } } | ConvertTo-Json -Compress -Depth 4
try {
    $resp = Invoke-WebRequest -Uri "$BaseUrl/authentication/$HostName" -Method Post -Body ([System.Text.Encoding]::UTF8.GetBytes($badBody)) -ContentType "application/json" -UseBasicParsing -TimeoutSec 30
    "bad_keyname=HTTP $($resp.StatusCode)"
} catch {
    $code = 0; if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
    $msg = ($_.ErrorDetails.Message -replace "`r?`n", " ").Trim()
    "bad_keyname=HTTP $code $msg"
}

# degraded auth: malformed private key
$malBody = @{ method = "0c69b301-81b4-42d6-8fae-128cdd113314"; credentials = @{ keyname = $KeyName; keydata = "not-a-pem" } } | ConvertTo-Json -Compress -Depth 4
try {
    $resp = Invoke-WebRequest -Uri "$BaseUrl/authentication/$HostName" -Method Post -Body ([System.Text.Encoding]::UTF8.GetBytes($malBody)) -ContentType "application/json" -UseBasicParsing -TimeoutSec 30
    "bad_pem=HTTP $($resp.StatusCode)"
} catch {
    $code = 0; if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
    $msg = ($_.ErrorDetails.Message -replace "`r?`n", " ").Trim()
    "bad_pem=HTTP $code $msg"
}

Invoke-WebRequest -Uri "$BaseUrl/authentication/$HostName" -Method Delete -Headers $h -UseBasicParsing -TimeoutSec 10 | Out-Null
"closed"
