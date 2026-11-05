namespace VeyonCampus.Core;

public sealed record VeyonNetworkLocation(string Name, IReadOnlyList<string> Targets,
    IReadOnlyDictionary<string, string>? TargetDisplayNames = null)
{
    /// <summary>
    /// Stable Veyon location UUID from a UUID-aware object snapshot; null on name-only backends
    /// and on the legacy export projection. Never serialized into campus-directory.json.
    /// </summary>
    public Guid? ObjectId { get; init; }
    /// <summary>Stable scope for a native teacher directory; Windows Veyon exports remain name-based.</summary>
    public Guid? CampusProfileId { get; init; }
    public Guid? RoomId { get; init; }
    public string DisplayName => $"{Name}（{Targets.Count} 台）";
}
