namespace VeyonCampus.Core;

/// <summary>Rejects package paths that pass through a symbolic link, junction or other reparse point.</summary>
internal static class PackagePathGuard
{
    public static void EnsureNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var volumeRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(volumeRoot))
            throw new InvalidDataException("部署包路径不是有效的本机路径。");

        var current = volumeRoot;
        RejectIfReparsePoint(current);
        foreach (var segment in fullPath[volumeRoot.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectIfReparsePoint(current);
        }
    }

    private static void RejectIfReparsePoint(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("部署包路径不能经过符号链接、目录联接或其他重解析点。");
    }
}
