internal static class TestPath
{
    public static string CanonicalTempRoot()
    {
        var full = Path.GetFullPath(Path.GetTempPath());
        var root = Path.GetPathRoot(full) ?? throw new InvalidOperationException("Temp root is invalid.");
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, segment);
            FileSystemInfo item = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            current = item.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
        }
        return Path.GetFullPath(current);
    }
}
