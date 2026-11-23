using System.Diagnostics;
using System.Text;
using VeyonCampus.App;

// Exercises the shipped WebAPI transport (default) and the shipped scheduler + card model (--wall)
// against the real local Veyon service.
// Usage: WallProbe [host] [frames] [--offline]
Console.OutputEncoding = Encoding.UTF8;
var host = args.FirstOrDefault(item => !item.StartsWith("--", StringComparison.Ordinal)) ?? "PC-01";
var frames = 5;
if (args.Length > 1 && int.TryParse(args[1], out var parsed)) frames = parsed;
var offlineCheck = args.Contains("--offline");

using var client = new VeyonWebApiClient();
var probeSw = Stopwatch.StartNew();
var reachable = await client.ProbeAsync(CancellationToken.None);
probeSw.Stop();
Console.WriteLine($"webapi_reachable={reachable} probe_ms={probeSw.ElapsedMilliseconds}");
if (!reachable)
{
    Console.WriteLine("RESULT: WebAPI not listening on 127.0.0.1:11080");
    return 2;
}

var keys = VeyonKeyMaterial.TryLoad();
Console.WriteLine($"keys={keys.Count} names={string.Join(",", keys.Select(key => key.KeyName))}");
if (keys.Count == 0)
{
    Console.WriteLine("RESULT: no readable private key");
    return 3;
}

using var connection = new ScreenWallConnection(Guid.NewGuid(), host, keys, client,
    (_, state, detail) => Console.WriteLine($"  state={state} detail={detail}"));

var firstSw = Stopwatch.StartNew();
var first = await connection.CaptureAsync(ScreenWallQuality.Detail, DateTimeOffset.UtcNow,
    CancellationToken.None);
firstSw.Stop();
Console.WriteLine(first is null
    ? $"first_frame=NONE elapsed_ms={firstSw.ElapsedMilliseconds} state={connection.State}"
    : $"first_frame_bytes={first.Jpeg.Length} http_ms={first.HttpMs} elapsed_ms={firstSw.ElapsedMilliseconds}");

var times = new List<int>();
var bytes = new List<int>();
for (var index = 0; index < frames; index++)
{
    var frame = await connection.CaptureAsync(ScreenWallQuality.Thumb, DateTimeOffset.UtcNow,
        CancellationToken.None);
    if (frame is null)
    {
        Console.WriteLine($"frame_{index}=NONE state={connection.State}");
        continue;
    }

    times.Add(frame.HttpMs);
    bytes.Add(frame.Jpeg.Length);
    await Task.Delay(120);
}

if (times.Count > 0)
{
    Console.WriteLine($"thumb_frames={times.Count} avg_http_ms={times.Average():F1} max_http_ms={times.Max()} " +
                      $"avg_bytes={(int)bytes.Average()} state={connection.State}");
}

if (offlineCheck)
{
    using var unreachable = new ScreenWallConnection(Guid.NewGuid(), "192.0.2.10", keys, client,
        (_, state, detail) => Console.WriteLine($"  offline_state={state} detail={detail}"));
    var offlineSw = Stopwatch.StartNew();
    var frame = await unreachable.CaptureAsync(ScreenWallQuality.Thumb, DateTimeOffset.UtcNow,
        CancellationToken.None);
    offlineSw.Stop();
    Console.WriteLine($"offline_frame={(frame is null ? "null" : "unexpected")} " +
                      $"elapsed_ms={offlineSw.ElapsedMilliseconds} state={unreachable.State}");
}

await connection.ReleaseAsync();
Console.WriteLine("RESULT: OK");
return 0;
