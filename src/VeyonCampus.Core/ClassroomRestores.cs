using System.Collections.ObjectModel;

namespace VeyonCampus.Core;

/// <summary>A display-only view of one locally retained classroom policy restoration.</summary>
public sealed record ClassroomPendingRestore(Guid SessionId, string RoomName, DateTimeOffset SessionStartedAtUtc,
    string DeviceLabel, ClassroomPolicyKind Kind);

/// <summary>A transient match to a current Veyon directory entry. Targets are never persisted here.</summary>
public sealed record ClassroomRestoreTarget(Guid TargetId, string DeviceLabel, string Target);

public static class ClassroomRestoreLedger
{
    public static IReadOnlyList<ClassroomPendingRestore> ListPending(
        IEnumerable<ClassroomModeSessionState> states, IEnumerable<ClassroomSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(sessions);
        var sessionsById = sessions.ToDictionary(item => item.SessionId);
        var pending = new List<ClassroomPendingRestore>();
        foreach (var state in states.Where(item => !item.Active && item.OwnedPolicies.Count > 0)
                     .OrderBy(item => item.UpdatedUtc))
        {
            sessionsById.TryGetValue(state.SessionId, out var session);
            var endedSession = session is { Status: ClassroomSessionStatus.Ended } ? session : null;
            var labels = endedSession?.Targets.ToDictionary(item => item.TargetId, item => item.DeviceLabel)
                         ?? new Dictionary<Guid, string>();
            foreach (var owner in state.OwnedPolicies.OrderBy(item => labels.GetValueOrDefault(item.TargetId) ?? "",
                         StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Kind))
            {
                pending.Add(new ClassroomPendingRestore(state.SessionId,
                    endedSession?.Room.RoomName ?? "未知机房",
                    endedSession?.StartedAtUtc ?? state.UpdatedUtc,
                    labels.GetValueOrDefault(owner.TargetId) ?? "无法匹配的电脑", owner.Kind));
            }
        }
        return new ReadOnlyCollection<ClassroomPendingRestore>(pending);
    }

    /// <summary>
    /// Resolves the old stable labels to the current room entries. Any changed room definition,
    /// ambiguous room, missing entry, or duplicate target fails closed and returns no target.
    /// </summary>
    public static IReadOnlyList<ClassroomRestoreTarget> MatchCurrentTargets(ClassroomSession session,
        TeacherCampusProfile campus, TeacherRoomProfile room, IReadOnlyList<VeyonNetworkLocation> locations)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(campus);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(locations);
        ClassroomSession.Validate(session);
        if (session.Status != ClassroomSessionStatus.Ended || campus.ProfileId != session.Room.CampusProfileId ||
            room.RoomId != session.Room.RoomId ||
            !string.Equals(room.DisplayName, session.Room.RoomName, StringComparison.Ordinal) ||
            !string.Equals(room.Prefix, session.Room.ComputerPrefix, StringComparison.Ordinal) ||
            room.StartNumber != session.Room.StartNumber || room.ComputerCount != session.Room.ComputerCount)
            return Array.Empty<ClassroomRestoreTarget>();

        var matchingLocations = locations.Where(item =>
            string.Equals(item.Name, session.Room.RoomName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matchingLocations.Length != 1) return Array.Empty<ClassroomRestoreTarget>();

        var locationTargets = matchingLocations[0].Targets.ToArray();
        var targets = new List<ClassroomRestoreTarget>(session.Targets.Length);
        for (var index = 0; index < session.Targets.Length; index++)
        {
            var oldTarget = session.Targets[index];
            var currentTarget = room.HostOverrides is { } overrides && index < overrides.Count &&
                                !string.IsNullOrWhiteSpace(overrides[index])
                ? VeyonHostAddress.NormalizeOverride(overrides[index])
                : oldTarget.DeviceLabel;
            if (locationTargets.Count(item => string.Equals(item, currentTarget,
                    StringComparison.OrdinalIgnoreCase)) != 1)
                continue;
            targets.Add(new ClassroomRestoreTarget(oldTarget.TargetId, oldTarget.DeviceLabel, currentTarget));
        }

        var duplicateTargets = targets.GroupBy(item => item.Target, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Array.AsReadOnly(targets.Where(item => !duplicateTargets.Contains(item.Target)).ToArray());
    }
}
