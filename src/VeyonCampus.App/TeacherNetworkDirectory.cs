using VeyonCampus.Core;

namespace VeyonCampus.App;

internal interface ITeacherNetworkDirectory
{
    IReadOnlyList<VeyonNetworkLocation> ReadLocations();
}

internal static class TeacherNetworkDirectoryFactory
{
    public static ITeacherNetworkDirectory CreateForCurrentPlatform(
        Func<IReadOnlyList<TeacherCampusProfile>> readCampusProfiles)
    {
        ArgumentNullException.ThrowIfNull(readCampusProfiles);
        if (OperatingSystem.IsWindows()) return new WindowsTeacherNetworkDirectory();
        if (OperatingSystem.IsMacOS()) return new MacTeacherNetworkDirectory(readCampusProfiles);
        return new UnsupportedTeacherNetworkDirectory();
    }
}

internal sealed class WindowsTeacherNetworkDirectory : ITeacherNetworkDirectory
{
    public IReadOnlyList<VeyonNetworkLocation> ReadLocations() => VeyonNetworkObjectDirectory.ReadLocations();
}

internal sealed class MacTeacherNetworkDirectory(
    Func<IReadOnlyList<TeacherCampusProfile>> readCampusProfiles) : ITeacherNetworkDirectory
{
    public IReadOnlyList<VeyonNetworkLocation> ReadLocations() =>
        readCampusProfiles().SelectMany(campus => campus.Rooms
                .Where(room => room.ComputerCount > 0)
                .Select(room => CreateLocation(campus, room)))
            .ToArray();

    internal static VeyonNetworkLocation CreateLocation(TeacherCampusProfile campus, TeacherRoomProfile room)
    {
        var computerNames = room.GetComputerNames();
        var targets = computerNames.Select((name, index) =>
        {
            var host = room.HostOverrides is { } hosts && index < hosts.Count
                ? hosts[index]
                : "";
            return string.IsNullOrWhiteSpace(host) ? name : VeyonHostAddress.NormalizeOverride(host);
        }).ToArray();
        var displayNames = targets.Select((target, index) => (Target: target, Name: computerNames[index]))
            .GroupBy(item => item.Target, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
        return new VeyonNetworkLocation(campus.DisplayName + " / " + room.DisplayName,
            Array.AsReadOnly(targets), displayNames)
        {
            CampusProfileId = campus.ProfileId,
            RoomId = room.RoomId
        };
    }

    internal static bool HasSameIdentity(VeyonNetworkLocation location,
        TeacherCampusProfile campus, TeacherRoomProfile room) =>
        location.CampusProfileId == campus.ProfileId && location.RoomId == room.RoomId;

    internal static bool MatchesCurrentRoom(VeyonNetworkLocation location,
        TeacherCampusProfile campus, TeacherRoomProfile room)
    {
        if (!HasSameIdentity(location, campus, room)) return false;
        try
        {
            var current = CreateLocation(campus, room);
            return location.Targets.SequenceEqual(current.Targets, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or
                                          InvalidOperationException or OverflowException)
        {
            return false;
        }
    }
}

internal sealed class UnsupportedTeacherNetworkDirectory : ITeacherNetworkDirectory
{
    public IReadOnlyList<VeyonNetworkLocation> ReadLocations() =>
        throw new PlatformNotSupportedException("地点和电脑目录只支持 Windows Veyon 或 Mac 本机档案。");
}
