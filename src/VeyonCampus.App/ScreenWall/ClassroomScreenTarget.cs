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
    /// <summary>
    /// Card title. The seat name recorded in this app is the authority: renaming a seat here never
    /// renames the machine in Veyon, so appending the Veyon computer name produced two conflicting
    /// labels on one card ("曹家乐左边 · 未命名2"). The computer name only stands in when no seat name
    /// has been entered at all.
    /// </summary>
    internal string BuildDisplayName()
    {
        var name = string.IsNullOrWhiteSpace(StudentName) ? "" : StudentName.Trim();
        if (name.Length > 0) return name;
        return string.IsNullOrWhiteSpace(ComputerName) ? HostAddress : ComputerName.Trim();
    }
}
