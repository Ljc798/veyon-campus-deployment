namespace VeyonCampus.Core;

internal static class PathLinkSecurity
{
    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null;
             current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"网站代理路径包含重解析点：{current}");
        }
    }

    public static void RejectLinksAllowingMissingComponents(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null;
             current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"网站代理路径包含重解析点：{current}");
        }
    }
}
