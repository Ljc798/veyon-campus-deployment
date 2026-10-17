namespace VeyonCampus.Core;

/// <summary>A signed local signal asking an interactive Companion to refresh Edge platform policies.</summary>
public sealed record StudentAgentEdgePolicyRefreshSignal(int SchemaVersion, Guid RequestId,
    DateTimeOffset? RequestedUtc, long Revision);

/// <summary>Coalesces the latest Edge policy refresh request for the local Student Companion.</summary>
public sealed class StudentAgentEdgePolicyRefreshSignalState
{
    private readonly object _gate = new();
    private Guid _requestId;
    private DateTimeOffset? _requestedUtc;
    private long _revision;

    public void Request(long revision, DateTimeOffset? requestedUtc = null)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        lock (_gate)
        {
            _requestId = Guid.NewGuid();
            _requestedUtc = (requestedUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
            _revision = revision;
        }
    }

    public StudentAgentEdgePolicyRefreshSignal Read()
    {
        lock (_gate) return new StudentAgentEdgePolicyRefreshSignal(1, _requestId, _requestedUtc, _revision);
    }
}
