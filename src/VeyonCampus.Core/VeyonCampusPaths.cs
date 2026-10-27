namespace VeyonCampus.Core;

/// <summary>Resolves a stable, per-user data root for Veyon Campus state.</summary>
public static class VeyonCampusPaths
{
    public static string GetLocalApplicationDataDirectory()
    {
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (!string.IsNullOrWhiteSpace(directory)) return RequireAbsolutePath(directory);

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrWhiteSpace(home)) home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home))
                return RequireAbsolutePath(Path.Combine(home, "Library", "Application Support"));
        }

        throw new IOException("无法确定 Veyon Campus 当前用户的数据目录；没有写入相对路径。请检查用户目录配置后重试。");
    }

    private static string RequireAbsolutePath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new IOException("Veyon Campus 当前用户的数据目录不是绝对路径；没有写入相对路径。");
        return Path.GetFullPath(path);
    }
}
