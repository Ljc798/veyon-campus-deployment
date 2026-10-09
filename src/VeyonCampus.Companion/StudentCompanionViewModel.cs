using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VeyonCampus.Companion;

public enum StudentCompanionConnectionState
{
    Disconnected,
    ConnectedWithoutClass,
    ClassroomActive
}

public sealed record StudentCompanionStatusSnapshot(
    StudentCompanionConnectionState ConnectionState,
    string? RoomName = null,
    int TargetCount = 0);

/// <summary>Maps a small, read-only classroom status snapshot to student-facing text.</summary>
public sealed class StudentCompanionViewModel : INotifyPropertyChanged
{
    private StudentCompanionStatusSnapshot _snapshot =
        new(StudentCompanionConnectionState.Disconnected);

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StatusTitle => _snapshot.ConnectionState switch
    {
        StudentCompanionConnectionState.Disconnected => "等待教师端连接",
        StudentCompanionConnectionState.ConnectedWithoutClass => "当前没有进行中的课堂",
        StudentCompanionConnectionState.ClassroomActive => "课堂进行中",
        _ => "课堂状态未知"
    };

    public string StatusDescription => _snapshot.ConnectionState switch
    {
        StudentCompanionConnectionState.Disconnected => "教师开始课堂后，这里会自动显示课堂状态。",
        StudentCompanionConnectionState.ConnectedWithoutClass => "教师开始课堂后，教室状态会自动显示。",
        StudentCompanionConnectionState.ClassroomActive => "你已连接到当前课堂。",
        _ => "暂时无法确认课堂状态。"
    };

    public string ClassroomSummary => _snapshot.ConnectionState switch
    {
        StudentCompanionConnectionState.ClassroomActive =>
            $"{_snapshot.RoomName} · {_snapshot.TargetCount} 台电脑",
        StudentCompanionConnectionState.ConnectedWithoutClass => "教师端已连接",
        _ => "未连接"
    };

    public bool HasClassroomSummary =>
        _snapshot.ConnectionState is StudentCompanionConnectionState.ConnectedWithoutClass or
            StudentCompanionConnectionState.ClassroomActive;

    public void ApplyStatus(StudentCompanionStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);
        if (_snapshot == snapshot) return;
        _snapshot = snapshot;
        Changed(nameof(StatusTitle));
        Changed(nameof(StatusDescription));
        Changed(nameof(ClassroomSummary));
        Changed(nameof(HasClassroomSummary));
    }

    private static void Validate(StudentCompanionStatusSnapshot snapshot)
    {
        switch (snapshot.ConnectionState)
        {
            case StudentCompanionConnectionState.Disconnected:
            case StudentCompanionConnectionState.ConnectedWithoutClass:
                if (snapshot.RoomName is not null || snapshot.TargetCount != 0)
                    throw new InvalidDataException("非课堂状态不能包含教室或目标电脑信息。");
                break;
            case StudentCompanionConnectionState.ClassroomActive:
                if (string.IsNullOrWhiteSpace(snapshot.RoomName) || snapshot.RoomName.Length > 100 ||
                    snapshot.RoomName != snapshot.RoomName.Trim() || snapshot.RoomName.Any(char.IsControl) ||
                    snapshot.TargetCount is < 1 or > 150)
                    throw new InvalidDataException("当前课堂状态无效。");
                break;
            default:
                throw new InvalidDataException("课堂连接状态不受支持。");
        }
    }

    private void Changed([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
