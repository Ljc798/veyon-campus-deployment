namespace VeyonCampus.Core;

/// <summary>Maps a selected folder or its manifest/config file to one package root without parsing resources.</summary>
public static class PackageSource
{
    public static string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("请选择一个部署包文件夹或其中的 manifest.json／campus.json。");

        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath)) return fullPath;
        if (!File.Exists(fullPath))
            throw new InvalidDataException("部署包路径不存在或已经移动，请重新选择。");

        var fileName = Path.GetFileName(fullPath);
        if (fileName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("campus.json", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(fullPath)!;

        throw new InvalidDataException("请拖入部署包文件夹、manifest.json 或 campus.json；ZIP 暂不支持。");
    }

    public static bool IsCandidate(string? path)
    {
        if (path is null) return false;
        try { Resolve(path); return true; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }
}
