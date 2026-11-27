using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace VeyonCampus.Core;

/// <summary>
/// Holds the keyboard and mouse locked while the student machine has lost its usable network path.
/// </summary>
/// <remarks>
/// A classroom website policy is stored on the machine and re-applied by the Agent, so pulling the
/// network does not remove an existing policy. What disconnecting still buys a student is a window
/// in which no new policy can be delivered, plus the freedom to play an offline game that never
/// needed the network at all. This guardian closes that window: once connectivity has been gone for
/// longer than a short grace period the machine is locked, so disconnecting costs the student the
/// machine instead of buying free play time.
///
/// The lock is issued through the Agent's own InputGuard path. Command signatures are verified only
/// at the HTTP boundary that the teacher machine reaches; the Agent-to-Guard pipe accepts a locally
/// built command, which is exactly what lets the Agent react while the teacher machine cannot be
/// reached at all.
///
/// Connectivity is judged from the adapter and its default gateway, never from whether the teacher
/// machine answers. A teacher console restart or a server outage must not lock a whole classroom.
///
/// When connectivity returns the guardian releases its own lease immediately. It never touches a
/// lock it did not sign, so a teacher-initiated lock keeps running untouched, and an absolute
/// <see cref="MaximumHold"/> cap keeps a machine that stays offline from being held forever.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class NetworkGuardian
{
    internal static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan OfflineThreshold = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan MaximumHold = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan LeaseLifetime = TimeSpan.FromSeconds(20);
    private const string Operation = "network-guardian";

    /// <summary>
    /// Probes connectivity and locks the machine while it stays offline. Runs for the lifetime of
    /// the Agent; a probe failure never throws into the caller's loop.
    /// </summary>
    public static async Task RunAsync(string campusId, StudentInputGuardAgentState inputGuardState,
        string configPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campusId);
        ArgumentNullException.ThrowIfNull(inputGuardState);
        if (!OperatingSystem.IsWindows()) return;

        var roomId = DeriveRoomId(campusId);
        DateTimeOffset? offlineSince = null;
        DateTimeOffset? holdStarted = null;
        Guid? ownLease = null;
        // Set once the hard cap releases a machine that has been offline for a whole lesson. The
        // guardian then stays out of the way until connectivity returns, instead of re-locking after
        // the grace period and turning the cap into a slow flicker.
        var holdAbandoned = false;
        var reportedState = string.Empty;

        using var timer = new PeriodicTimer(ProbeInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var now = DateTimeOffset.UtcNow;
                bool online;
                try { online = HasUsableNetwork(); }
                catch (NetworkInformationException) { online = true; }

                if (online)
                {
                    offlineSince = null;
                    holdAbandoned = false;
                    if (ownLease is { } lease)
                    {
                        await TryReleaseAsync(inputGuardState, campusId, roomId, lease, now,
                            cancellationToken).ConfigureAwait(false);
                        ownLease = null;
                        holdStarted = null;
                        Report(configPath, ref reportedState, "online",
                            "网络已恢复，已解除断网锁定。");
                    }
                    else
                    {
                        holdStarted = null;
                        Report(configPath, ref reportedState, "online", "网络正常。");
                    }
                    continue;
                }

                offlineSince ??= now;
                var offlineFor = now - offlineSince.Value;
                Report(configPath, ref reportedState, "offline",
                    $"检测到网络断开 {offlineFor.TotalSeconds:F0} 秒。");
                if (offlineFor < OfflineThreshold) continue;
                if (holdAbandoned) continue;

                // Only while a classroom policy is actually in force. Locking outside a lesson would
                // punish a student for switching the radio off at the end of the day, which is
                // ordinary use rather than an escape; the whole point is to protect a lesson.
                if (!IsPolicyActive(campusId))
                {
                    if (ownLease is { } stale)
                    {
                        await TryReleaseAsync(inputGuardState, campusId, roomId, stale, now,
                            cancellationToken).ConfigureAwait(false);
                        ownLease = null;
                    }
                    holdStarted = null;
                    offlineSince = null;
                    Report(configPath, ref reportedState, "no-policy",
                        "网络不通，但当前没有生效的课堂策略，不锁定键鼠。");
                    continue;
                }

                // A machine that stays offline for a whole lesson is released on a hard cap so a
                // cut cable or a dead access point cannot hold a student's desktop indefinitely.
                if (holdStarted is { } startedAt && now - startedAt >= MaximumHold)
                {
                    if (ownLease is { } expiredLease)
                    {
                        await TryReleaseAsync(inputGuardState, campusId, roomId, expiredLease, now,
                            cancellationToken).ConfigureAwait(false);
                        ownLease = null;
                    }
                    Report(configPath, ref reportedState, "hold-timeout",
                        $"断网锁定已持续 {(now - startedAt).TotalMinutes:F0} 分钟，超过上限，已解除；网络仍不通。");
                    // Stay out until connectivity returns; the clock is not restarted so the cap is
                    // not turned into a slow re-lock every grace period.
                    holdAbandoned = true;
                    offlineSince = null;
                    holdStarted = null;
                    continue;
                }

                var status = await StudentInputGuardIpcClient.ReadStatusAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!status.Supported) continue;

                if (status.Phase == StudentInputGuardPhase.Locked)
                {
                    if (ownLease is { } mine && status.LeaseId == mine)
                    {
                        if (await TryRenewAsync(inputGuardState, campusId, roomId, mine, now,
                                cancellationToken).ConfigureAwait(false))
                            continue;
                        ownLease = null;
                    }
                    // Already locked by the teacher (or by a previous guardian run whose lease we
                    // no longer track): do not take over and do not release.
                    continue;
                }

                if (status.Phase is not (StudentInputGuardPhase.Unlocked or StudentInputGuardPhase.Expired))
                    continue;

                var leaseId = Guid.NewGuid();
                if (await TryActivateAsync(inputGuardState, campusId, roomId, leaseId, now,
                        cancellationToken).ConfigureAwait(false))
                {
                    ownLease = leaseId;
                    holdStarted = now;
                    Report(configPath, ref reportedState, "locked",
                        $"网络已断开 {offlineFor.TotalSeconds:F0} 秒，已锁定键鼠；网络恢复后自动解除。");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>
    /// Whether a real, routable IPv4 path exists: an up adapter that is neither loopback nor a
    /// tunnel, carrying a non-APIPA address and a default gateway. Turning the radio off, pulling
    /// the link or disabling the adapter all remove one of these; a teacher machine going away
    /// removes none.
    /// </summary>
    internal static bool HasUsableNetwork()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or
                NetworkInterfaceType.Tunnel) continue;
            var properties = adapter.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                !gateway.Address.Equals(IPAddress.Any));
            if (!hasGateway) continue;
            var hasAddress = properties.UnicastAddresses.Any(address =>
                address.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address.Address) &&
                !IsAutomaticPrivate(address.Address));
            if (hasAddress) return true;
        }
        return false;
    }

    private static bool IsAutomaticPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    /// <summary>
    /// Whether the machine currently holds a classroom policy that has not expired. A policy that
    /// was never applied, was released after the lesson or has passed its deadline leaves the
    /// machine alone even when the network is gone.
    /// </summary>
    private static bool IsPolicyActive(string campusId)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var status = WebsitePolicyRegistryStore.ReadStatus(campusId, now, includeDomains: false);
            return status.Mode != WebsitePolicyMode.Disabled &&
                   (status.ExpiresUtc is not { } expiry || expiry > now) &&
                   status.ExpiredUtc is null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or System.Security.SecurityException or
                                              InvalidOperationException or ArgumentException)
        {
            // A registry read that cannot be trusted must not create a lock; failing open is the safe
            // direction for a machine that may be offline for unrelated reasons.
            return false;
        }
    }

    private static async Task<bool> TryActivateAsync(StudentInputGuardAgentState state, string campusId,
        Guid roomId, Guid leaseId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await ExecuteWithEpochRetryAsync(state, campusId, roomId, leaseId,
            StudentInputGuardAction.Activate, now, cancellationToken).ConfigureAwait(false);

    private static async Task<bool> TryRenewAsync(StudentInputGuardAgentState state, string campusId,
        Guid roomId, Guid leaseId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await ExecuteWithEpochRetryAsync(state, campusId, roomId, leaseId,
            StudentInputGuardAction.Renew, now, cancellationToken).ConfigureAwait(false);

    private static async Task TryReleaseAsync(StudentInputGuardAgentState state, string campusId,
        Guid roomId, Guid leaseId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await ExecuteWithEpochRetryAsync(state, campusId, roomId, leaseId,
            StudentInputGuardAction.Release, now, cancellationToken).ConfigureAwait(false);

    private static async Task<bool> ExecuteWithEpochRetryAsync(StudentInputGuardAgentState state,
        string campusId, Guid roomId, Guid leaseId, StudentInputGuardAction action, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The control epoch moves whenever any command succeeds, so a conflict here is expected
        // rather than exceptional; one retry with the freshly read epoch is enough.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var command = BuildCommand(state, campusId, roomId, leaseId, action, now);
            var receipt = await state.ExecuteAsync(command, now, cancellationToken).ConfigureAwait(false);
            if (receipt.Outcome == StudentInputGuardOutcome.Succeeded) return true;
            if (receipt.Outcome != StudentInputGuardOutcome.Conflict) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private static StudentInputGuardCommand BuildCommand(StudentInputGuardAgentState state,
        string campusId, Guid roomId, Guid leaseId, StudentInputGuardAction action, DateTimeOffset now) =>
        new(1,
            StudentInputGuardCommandCryptography.Purpose,
            campusId,
            roomId,
            state.DeviceFingerprint,
            "local-network-guardian",
            state.AgentRunId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            state.ControlEpoch,
            leaseId,
            now,
            now.AddSeconds(25),
            action,
            now.AddSeconds(20));

    private static Guid DeriveRoomId(string campusId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("VeyonCampus.NetworkGuardian.Room." + campusId));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static void Report(string configPath, ref string reportedState, string state, string detail)
    {
        if (reportedState == state) return;
        reportedState = state;
        try
        {
            WebsitePolicyAgentInstaller.ReportAgentOperationNote(configPath, Operation, detail);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              ArgumentException or InvalidOperationException or
                                              System.Security.SecurityException) { }
    }
}
