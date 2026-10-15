namespace VeyonCampus.Core;

/// <summary>A short-lived signal telling interactive Student Companion sessions to restart open Edge/Chrome windows.</summary>
public sealed record StudentAgentBrowserRestartSignal(int SchemaVersion, Guid RequestId,
    DateTimeOffset? RequestedUtc);

/// <summary>In-memory, coalescing restart signal shared by the SYSTEM Agent's HTTP handlers.</summary>
public sealed class StudentAgentBrowserRestartSignalState
{
    private readonly object _gate = new();
    private Guid _requestId;
    private DateTimeOffset? _requestedUtc;

    public void Request(DateTimeOffset? requestedUtc = null)
    {
        lock (_gate)
        {
            _requestId = Guid.NewGuid();
            _requestedUtc = (requestedUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        }
    }

    public StudentAgentBrowserRestartSignal Read()
    {
        lock (_gate) return new StudentAgentBrowserRestartSignal(1, _requestId, _requestedUtc);
    }
}
