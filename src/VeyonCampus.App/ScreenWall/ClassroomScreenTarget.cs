namespace VeyonCampus.App;

/// <summary>
/// One student machine the screen wall may capture: the stable classroom target id, the address the
/// local Veyon WebAPI can reach, and the seat/name projection used for ordering and labels.
/// Produced by <see cref="TeacherMobileControlManager"/> from the active classroom session so the wall
/// never re-derives seats or identities on its own.
/// </summary>
internal sealed record ClassroomScreenTarget(Guid TargetId, string HostAddress, int SeatNumber,
    int Row, int Column, string StudentName, string ComputerName)
{
    internal string BuildDisplayName()
    {
        var name = string.IsNullOrWhiteSpace(StudentName) ? "" : StudentName.Trim();
        var computer = string.IsNullOrWhiteSpace(ComputerName) ? HostAddress : ComputerName.Trim();
        if (name.Length == 0) return computer;
        return string.Equals(name, computer, StringComparison.OrdinalIgnoreCase) ? name : $"{name} · {computer}";
    }
}
