namespace VeyonCampus.App;

/// <summary>
/// Exposes only the single-frame screenshot operation needed by classroom previews.
/// Platform-specific Veyon commands stay behind this boundary.
/// </summary>
internal interface ITeacherVeyonControlPlane
{
    Task<byte[]> CaptureScreenAsync(string campusId, string target, CancellationToken cancellationToken);
}

internal sealed class WindowsTeacherVeyonControlPlane : ITeacherVeyonControlPlane
{
    public Task<byte[]> CaptureScreenAsync(string campusId, string target, CancellationToken cancellationToken)
    {
        _ = campusId;
        return ClassroomScreenPreviewCapture.VeyonCliScreenshotCapture.CaptureAsync(target, cancellationToken);
    }
}

internal sealed class MacTeacherVeyonControlPlane : ITeacherVeyonControlPlane
{
    public Task<byte[]> CaptureScreenAsync(string campusId, string target, CancellationToken cancellationToken) =>
        MacVeyonScreenshotCapture.CaptureAsync(campusId, target, cancellationToken);
}

internal static class TeacherVeyonControlPlaneFactory
{
    public static ITeacherVeyonControlPlane? CreateForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows()) return new WindowsTeacherVeyonControlPlane();
        if (OperatingSystem.IsMacOS()) return new MacTeacherVeyonControlPlane();
        return null;
    }
}
