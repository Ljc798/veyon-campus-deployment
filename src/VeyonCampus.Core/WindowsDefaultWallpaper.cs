namespace VeyonCampus.Core;

/// <summary>Resolves and structurally validates the built-in Windows 10 blue hero wallpaper.</summary>
public static class WindowsDefaultWallpaper
{
    private const int MinimumImageBytes = 64;
    private const int MaximumImageBytes = 32 * 1024 * 1024;

    public static string Resolve(string windowsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsDirectory);
        var windowsRoot = Path.GetFullPath(windowsDirectory);
        var path = Path.GetFullPath(Path.Combine(windowsRoot, "Web", "Wallpaper", "Windows", "img0.jpg"));
        var relative = Path.GetRelativePath(windowsRoot, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            throw new InvalidDataException("默认壁纸路径超出 Windows 安装目录。");
        if (!File.Exists(path)) throw new FileNotFoundException("缺少 Windows 默认蓝色壁纸 img0.jpg。", path);
        PathLinkSecurity.RejectLinks(path);

        byte[] image;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length is < MinimumImageBytes or > MaximumImageBytes)
                throw new InvalidDataException("Windows 默认壁纸文件大小不合理。");
            image = new byte[checked((int)stream.Length)];
            stream.ReadExactly(image);
            if (stream.ReadByte() != -1) throw new IOException("Windows 默认壁纸在验证时发生变化。");
        }
        _ = ReadJpegDimensions(image);
        return path;
    }

    internal static (int Width, int Height, int Components) ReadJpegDimensions(ReadOnlySpan<byte> image)
    {
        if (image.Length is < MinimumImageBytes or > MaximumImageBytes ||
            image[0] != 0xff || image[1] != 0xd8 || image[^2] != 0xff || image[^1] != 0xd9)
            throw new InvalidDataException("Windows 默认壁纸不是完整 JPEG 图像。");

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
                throw new InvalidDataException("Windows 默认壁纸 JPEG 段长度无效。");
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or
                0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                if (segmentLength < 8) throw new InvalidDataException("Windows 默认壁纸 JPEG 尺寸段无效。");
                height = (image[offset + 3] << 8) | image[offset + 4];
                width = (image[offset + 5] << 8) | image[offset + 6];
                components = image[offset + 7];
            }
            offset += segmentLength;
        }

        if (width is < 1280 or > 16384 || height is < 720 or > 16384 || components != 3)
            throw new InvalidDataException("Windows 默认壁纸必须是 1280×720 以上的 RGB 桌面 JPEG。");
        return (width, height, components);
    }
}
