using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace VeyonCampus.Core;

/// <summary>Installs and resolves the trusted, content-addressed student wallpaper.</summary>
public static class WindowsDefaultWallpaper
{
    internal const string AssetFileName = "StudentDefaultWallpaper-v1.jpg";
    internal const string AssetSha256 = "2cbcf8ce55cf0694193f6e7d06754b55f659b6673fa517673dc277ec6dd0e33e";
    private const int MinimumImageBytes = 64;
    private const int MaximumImageBytes = 8 * 1024 * 1024;
    private const int MinimumDimension = 1280;
    private const int MaximumDimension = 16384;

    [SupportedOSPlatform("windows")]
    public static string Resolve()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("统一桌面壁纸资源只在 Windows Student Agent 上安装。");

        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return EnsureBundledAsset(AppContext.BaseDirectory, commonAppData,
            static (path, directory) => AgentFileSecurity.Secure(path, directory,
                executable: !directory && !Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase)));
    }

    internal static string EnsureBundledAsset(string agentDirectory, string commonAppDataDirectory,
        Action<string, bool>? securePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(commonAppDataDirectory);
        securePath ??= static (_, _) => { };

        var agentRoot = Path.GetFullPath(agentDirectory);
        if (!Directory.Exists(agentRoot)) throw new DirectoryNotFoundException("Student Agent 发布目录不存在。");
        PathLinkSecurity.RejectLinks(agentRoot);
        var sourcePath = Path.GetFullPath(Path.Combine(agentRoot, "Assets", AssetFileName));
        EnsureUnderRoot(agentRoot, sourcePath);
        var sourceImage = ReadTrustedAsset(sourcePath);

        var commonRoot = Path.GetFullPath(commonAppDataDirectory);
        if (!Directory.Exists(commonRoot)) throw new DirectoryNotFoundException("ProgramData 目录不存在。");
        PathLinkSecurity.RejectLinks(commonRoot);
        var campusDirectory = Path.Combine(commonRoot, "VeyonCampus");
        var systemPolicyDirectory = Path.Combine(campusDirectory, "SystemPolicy");
        var assetDirectory = Path.Combine(systemPolicyDirectory, "Assets");
        EnsureProtectedDirectory(campusDirectory, securePath);
        EnsureProtectedDirectory(systemPolicyDirectory, securePath);
        EnsureProtectedDirectory(assetDirectory, securePath);

        var targetPath = Path.Combine(assetDirectory, AssetSha256 + ".jpg");
        if (File.Exists(targetPath)) return ValidateInstalledAsset(targetPath, securePath);
        if (Directory.Exists(targetPath)) throw new InvalidDataException("系统壁纸资源路径被目录占用。");

        var temporaryPath = Path.Combine(assetDirectory, "." + AssetSha256 + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 16 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(sourceImage);
                stream.Flush(flushToDisk: true);
            }
            PathLinkSecurity.RejectLinks(temporaryPath);
            securePath(temporaryPath, false);
            _ = ReadTrustedAsset(temporaryPath);
            try
            {
                File.Move(temporaryPath, targetPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                File.Delete(temporaryPath);
            }
            return ValidateInstalledAsset(targetPath, securePath);
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static (int Width, int Height, int Components) ReadJpegDimensions(ReadOnlySpan<byte> image)
    {
        if (image.Length is < MinimumImageBytes or > MaximumImageBytes ||
            image[0] != 0xff || image[1] != 0xd8 || image[^2] != 0xff || image[^1] != 0xd9)
            throw new InvalidDataException("Student Agent 壁纸资源不是完整 JPEG 图像。");

        var offset = 2;
        var width = 0;
        var height = 0;
        var components = 0;
        while (offset < image.Length - 2)
        {
            if (image[offset++] != 0xff) continue;
            while (offset < image.Length && image[offset] == 0xff) offset++;
            if (offset >= image.Length) break;
            var marker = image[offset++];
            if (marker is 0xd9 or 0xda) break;
            if (marker is 0xd8 or 0x01 or >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > image.Length) break;
            var segmentLength = (image[offset] << 8) | image[offset + 1];
            if (segmentLength < 2 || offset + segmentLength > image.Length)
                throw new InvalidDataException("Student Agent 壁纸 JPEG 段长度无效。");
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or
                0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                if (segmentLength < 8) throw new InvalidDataException("Student Agent 壁纸 JPEG 尺寸段无效。");
                height = (image[offset + 3] << 8) | image[offset + 4];
                width = (image[offset + 5] << 8) | image[offset + 6];
                components = image[offset + 7];
            }
            offset += segmentLength;
        }

        if (width is < MinimumDimension or > MaximumDimension ||
            height is < 720 or > MaximumDimension || components != 3)
            throw new InvalidDataException("Student Agent 壁纸必须是 1280×720 以上的 RGB 桌面 JPEG。");
        return (width, height, components);
    }

    private static byte[] ReadTrustedAsset(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Student Agent 缺少固定的统一壁纸资源。", path);
        PathLinkSecurity.RejectLinks(path);
        byte[] image;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length is < MinimumImageBytes or > MaximumImageBytes)
                throw new InvalidDataException("Student Agent 壁纸文件大小不合理。");
            image = new byte[checked((int)stream.Length)];
            stream.ReadExactly(image);
            if (stream.ReadByte() != -1) throw new IOException("Student Agent 壁纸在验证时发生变化。");
        }

        var hash = Convert.ToHexString(SHA256.HashData(image));
        if (!string.Equals(hash, AssetSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Student Agent 壁纸 SHA-256 与内置信任摘要不匹配。");
        var dimensions = ReadJpegDimensions(image);
        var aspectRatio = (double)dimensions.Width / dimensions.Height;
        if (Math.Abs(aspectRatio - 16d / 9d) > 0.03d)
            throw new InvalidDataException("Student Agent 壁纸长宽比超出桌面安全范围。");
        return image;
    }

    private static string ValidateInstalledAsset(string path, Action<string, bool> securePath)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("安装后的 Student Agent 壁纸资源不存在。", path);
        PathLinkSecurity.RejectLinks(path);
        securePath(path, false);
        _ = ReadTrustedAsset(path);
        return path;
    }

    private static void EnsureProtectedDirectory(string path, Action<string, bool> securePath)
    {
        if (!Directory.Exists(path))
        {
            if (File.Exists(path)) throw new InvalidDataException("Student Agent 壁纸目录路径被文件占用。");
            var parent = Path.GetDirectoryName(path)
                         ?? throw new InvalidDataException("Student Agent 壁纸目录路径无效。");
            if (!Directory.Exists(parent)) EnsureProtectedDirectory(parent, securePath);
            else PathLinkSecurity.RejectLinks(parent);
            Directory.CreateDirectory(path);
        }
        PathLinkSecurity.RejectLinks(path);
        securePath(path, true);
    }

    private static void EnsureUnderRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Student Agent 壁纸资源路径超出发布目录。");
    }
}
