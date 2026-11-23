param(
    [string]$HostName = "PC-01",
    [string]$BaseUrl = "http://127.0.0.1:11080/api/v1",
    [string]$KeyDir = "C:\ProgramData\Veyon\keys\private\CDPPLHAJJHEOHFJOGOJCGFKGIDKJOKNCA",
    [string]$KeyName = "CDPPLHAJJHEOHFJOGOJCGFKGIDKJOKNCA",
    [int]$Frames = 10,
    [int]$Width = 960,
    [int]$Height = 540,
    [string]$Format = "jpg",
    [string]$OutDir = "F:\github\veyon-campus-deployment\.tmp\webapi-spike\out"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$results = [ordered]@{}

# 1. authentication methods
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$methods = Invoke-RestMethod -Uri "$BaseUrl/authentication/$HostName" -TimeoutSec 20
$sw.Stop()
$results.methodsMs = $sw.ElapsedMilliseconds
$results.methods = ($methods.methods -join ",")
$method = $methods.methods[0]

# 2. perform authentication (keyfile)
$pem = Get-Content -Raw -Path (Join-Path $KeyDir "key")
$body = @{ method = $method; credentials = @{ keyname = $KeyName; keydata = $pem } } | ConvertTo-Json -Compress -Depth 4
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$authResponse = Invoke-WebRequest -Uri "$BaseUrl/authentication/$HostName" -Method Post -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -ContentType "application/json" -UseBasicParsing -TimeoutSec 60
$sw.Stop()
$results.authMs = $sw.ElapsedMilliseconds
$authJson = $authResponse.Content | ConvertFrom-Json
$connectionUid = $authJson.'connection-uid'
if (-not $connectionUid) { $connectionUid = $authResponse.Headers["Connection-Uid"] }
if (-not $connectionUid) { throw "no connection uid: $($authResponse.Content)" }
$results.connectionUid = $connectionUid
$results.authBody = $authResponse.Content

# 3. wait for first framebuffer + timing
$headers = @{ "Connection-Uid" = $connectionUid }
$fbUrl = "$BaseUrl/framebuffer?width=$Width&height=$Height&format=$Format&compression=5&quality=80"
$firstSw = [System.Diagnostics.Stopwatch]::StartNew()
$firstFrame = $null
$attempts = 0
while ($firstSw.Elapsed.TotalSeconds -lt 30) {
    $attempts++
    try {
        $firstFrame = Invoke-WebRequest -Uri $fbUrl -Headers $headers -UseBasicParsing -TimeoutSec 30
        break
    } catch {
        $code = 0
        if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
        if ($code -ne 503) { throw }
        Start-Sleep -Milliseconds 250
    }
}
$firstSw.Stop()
if (-not $firstFrame) { throw "framebuffer never became available" }
$results.timeToFirstFrameMs = $firstSw.ElapsedMilliseconds
$results.firstFrameAttempts = $attempts

# 4. framebuffer timing, sequential
$frameTimes = @()
$firstBytes = $firstFrame.RawContentStream.ToArray()
$results.firstFrameBytes = $firstBytes.Length
[System.IO.File]::WriteAllBytes((Join-Path $OutDir "frame-first.$Format"), $firstBytes)
for ($i = 0; $i -lt $Frames; $i++) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-WebRequest -Uri "$BaseUrl/framebuffer?width=$Width&height=$Height&format=$Format&quality=80" -Headers $headers -UseBasicParsing -TimeoutSec 30
    $sw.Stop()
    $frameTimes += $sw.ElapsedMilliseconds
    $bytes = $r.RawContentStream.ToArray()
    if ($i -eq 0 -or $i -eq ($Frames - 1)) {
        $file = Join-Path $OutDir ("frame-{0}.{1}" -f $i, $Format)
        [System.IO.File]::WriteAllBytes($file, $bytes)
    }
    $results["frame${i}Bytes"] = $bytes.Length
    Start-Sleep -Milliseconds 200
}
$results.frameTimesMs = ($frameTimes -join ",")
$results.frameAvgMs = [math]::Round(($frameTimes | Measure-Object -Average).Average, 1)
$results.frameMinMs = ($frameTimes | Measure-Object -Minimum).Minimum
$results.frameMaxMs = ($frameTimes | Measure-Object -Maximum).Maximum
$results.pngBytes = (Get-Item (Join-Path $OutDir "frame-0.$Format")).Length

# 4. session info
try {
    $session = Invoke-RestMethod -Uri "$BaseUrl/session" -Headers $headers -TimeoutSec 10
    $results.session = ($session | ConvertTo-Json -Compress)
} catch { $results.sessionError = $_.Exception.Message }

# 5. feature list (active features on student)
try {
    $features = Invoke-RestMethod -Uri "$BaseUrl/feature" -Headers $headers -TimeoutSec 10
    $results.features = ($features | ConvertTo-Json -Compress -Depth 5)
} catch { $results.featuresError = $_.Exception.Message }

# 6. error cases
try {
    Invoke-WebRequest -Uri "$BaseUrl/framebuffer?format=jpg" -Headers @{ "Connection-Uid" = "00000000-0000-0000-0000-000000000000" } -UseBasicParsing -TimeoutSec 10 | Out-Null
    $results.badUid = "unexpected 200"
} catch {
    $results.badUid = "HTTP " + [int]$_.Exception.Response.StatusCode + " " + $_.ErrorDetails.Message
}
try {
    Invoke-WebRequest -Uri "$BaseUrl/authentication/NO-SUCH-HOST-XYZ" -UseBasicParsing -TimeoutSec 20 | Out-Null
    $results.badHost = "unexpected 200"
} catch {
    $results.badHost = "HTTP " + [int]$_.Exception.Response.StatusCode
}

# 7. close connection
$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    Invoke-WebRequest -Uri "$BaseUrl/authentication/$HostName" -Method Delete -Headers $headers -UseBasicParsing -TimeoutSec 10 | Out-Null
    $results.closeMs = $sw.ElapsedMilliseconds
} catch {
    $results.closeError = $_.Exception.Message
}

$results.GetEnumerator() | ForEach-Object { "{0}={1}" -f $_.Key, $_.Value }
