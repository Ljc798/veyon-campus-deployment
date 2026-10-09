using VeyonCampus.Companion;

namespace VeyonCampus.Checks;

internal static class StudentCompanionChecks
{
    public static void Run()
    {
        var viewModel = new StudentCompanionViewModel();
        Expect(viewModel.StatusTitle == "等待教师端连接");
        Expect(viewModel.ClassroomSummary == "未连接");
        Expect(!viewModel.HasClassroomSummary);

        viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.ConnectedWithoutClass));
        Expect(viewModel.StatusTitle == "当前没有进行中的课堂");
        Expect(viewModel.ClassroomSummary == "教师端已连接");
        Expect(viewModel.HasClassroomSummary);

        viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.ClassroomActive, "LAB-01", 1));
        Expect(viewModel.StatusTitle == "课堂进行中");
        Expect(viewModel.ClassroomSummary == "LAB-01 · 1 台电脑");
        Expect(viewModel.HasClassroomSummary);

        viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.ClassroomActive, "机房 A", 150));
        Expect(viewModel.ClassroomSummary == "机房 A · 150 台电脑");

        Reject(() => viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.ClassroomActive, "LAB-01", 151)));
        Reject(() => viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.ClassroomActive, " LAB-01", 1)));
        Reject(() => viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.ClassroomActive, "LAB\n01", 1)));
        Reject(() => viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.Disconnected, TargetCount: 1)));
        Reject(() => viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            (StudentCompanionConnectionState)42)));
    }

    private static void Expect(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Student Companion status assertion failed.");
    }

    private static void Reject(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException("Student Companion accepted an invalid status snapshot.");
    }
}
