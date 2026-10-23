using System.IO.Pipes;
using VeyonCampus.Core;
using VeyonCampus.InputGuard;

namespace VeyonCampus.Checks;

internal static class InputGuardIpcChecks
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static async Task RunAsync()
    {
        if (!Environment.UserInteractive || System.Diagnostics.Process.GetCurrentProcess().SessionId == 0)
            throw new InvalidOperationException("This non-locking pipe fixture requires an interactive Windows test session.");
        if (!string.Equals(WindowsProcessImagePath.Read(Environment.ProcessId), Environment.ProcessPath,
                StringComparison.OrdinalIgnoreCase))
            throw new Exception("Limited-information peer executable query did not return the exact process path.");

        var pipeName = "VeyonCampus.StudentInputGuard.Check." + Guid.NewGuid().ToString("N");
        var rejected = 0;
        using var shutdown = new CancellationTokenSource();
        var server = new StudentInputGuardServer(pipeName, (_, _) => Interlocked.Increment(ref rejected));
        var running = Task.Run(() => server.RunAsync(shutdown.Token));
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                    await client.ConnectAsync(2000);
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (Volatile.Read(ref rejected) <= attempt && !running.IsCompleted && DateTime.UtcNow < deadline)
                    await Task.Delay(20);
                if (running.IsCompleted || Volatile.Read(ref rejected) != attempt + 1)
                    throw new Exception("A rejected interactive pipe client terminated the Guard server or bypassed peer checks.");
            }
        }
        finally
        {
            await shutdown.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(4));
        }
        Console.WriteLine("PASS limited-rights process path and three rejected IPC clients; Guard remained alive, no input-blocking commands sent");
    }
}
