using System.Security.Principal;

namespace VeyonCampus.EdgePolicyExecutor;

internal static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        if (args.Length > 1 || args.Length == 1 && args[0] != "--startup") return 2;

        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is null || identity.User.Value == "S-1-5-18" ||
            new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return 3;

        using var instance = new Mutex(initiallyOwned: true, "Local\\VeyonCampus.EdgePolicyExecutor", out var created);
        if (!created) return 0;

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        try
        {
            await new StudentEdgePolicyRefreshExecutor().RunAsync(shutdown.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
    }
}
