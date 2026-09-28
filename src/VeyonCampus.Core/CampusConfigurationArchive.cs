using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace VeyonCampus.Core;

/// <summary>
/// Creates and receives the small, fixed set of public campus configuration
/// files used by the LAN distribution flow. This format deliberately excludes
/// arbitrary files and the optional README from the transfer.
/// </summary>
public static class CampusConfigurationArchive
{
    public const int MaximumArchiveBytes = 512 * 1024;
    private const int MaximumFileBytes = 64 * 1024;
    private const int MaximumFiles = 4;

    public static byte[] Create(string packageDirectory)
    {
        var root = Path.GetFullPath(packageDirectory);
        var context = PackageManifest.Load(root);
        if (context.SchemaVersion != 3 || context.WebsitePolicyPublicKeyPath is null ||
            !string.IsNullOrWhiteSpace(context.TelemetryEndpoint))
            throw new InvalidDataException("局域网分发只接受当前 schemaVersion=3 的校区配置包。");

        var publicKeyName = GetTopLevelName(root, context.PublicKeyPath);
        var websitePolicyKeyName = GetTopLevelName(root, context.WebsitePolicyPublicKeyPath);
        var names = new[] { "manifest.json", "campus.json", publicKeyName, websitePolicyKeyName };
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new InvalidDataException("校区配置包中的文件名重复，已停止局域网分发。");
        EnsureOnlyFixedFiles(root, names);

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            files.Add(name, ReadBoundedFile(Path.Combine(root, name), MaximumFileBytes));

        VerifyDigest(files["manifest.json"], context.ConfigSha256, "manifest.json");
        VerifyDigest(files[publicKeyName], context.PublicKeySha256, "Veyon 校区公钥");
        VerifyDigest(files[websitePolicyKeyName], context.WebsitePolicyPublicKeySha256!, "网站策略公钥");

        var snapshotDirectory = Path.Combine(Path.GetTempPath(), "VeyonCampus-LanSnapshot-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(snapshotDirectory);
            foreach (var (name, bytes) in files)
                File.WriteAllBytes(Path.Combine(snapshotDirectory, name), bytes);

            var snapshot = PackageManifest.Load(snapshotDirectory);
            if (snapshot.SchemaVersion != 3 || snapshot.Campus != context.Campus ||
                snapshot.ComputerPrefix != context.ComputerPrefix || !string.IsNullOrWhiteSpace(snapshot.TelemetryEndpoint))
                throw new InvalidDataException("校区配置包在生成传输快照时发生变化。");
            ValidateCampusJson(files["campus.json"], snapshot, publicKeyName, websitePolicyKeyName);

            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var name in names)
                {
                    var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    entryStream.Write(files[name]);
                }
            }
            if (buffer.Length > MaximumArchiveBytes)
                throw new InvalidDataException("校区配置包超过局域网分发大小限制。");
            return buffer.ToArray();
        }
        finally
        {
            try { if (Directory.Exists(snapshotDirectory)) Directory.Delete(snapshotDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Extracts a received archive into an application-owned directory and returns its path.</summary>
    public static PackageContext ExtractToStore(byte[] archiveBytes, string storageRoot)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        if (archiveBytes.Length is < 1 or > MaximumArchiveBytes)
            throw new InvalidDataException("下载的校区配置包大小无效或超过限制。");

        var root = Path.GetFullPath(storageRoot);
        Directory.CreateDirectory(root);
        EnsureOrdinaryDirectory(root);
        var staging = Path.Combine(root, ".lan-staging-" + Guid.NewGuid().ToString("N"));
        var final = Path.Combine(root, "lan-package-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var files = ReadArchiveEntries(archiveBytes);
            var expected = GetExpectedFileNames(files["manifest.json"]);
            if (files.Count != expected.Count || files.Keys.Any(name => !expected.Contains(name, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("下载内容含有未允许的文件；只接受固定校区配置资料。");

            foreach (var (name, bytes) in files)
                File.WriteAllBytes(Path.Combine(staging, name), bytes);

            var context = PackageManifest.Load(staging);
            if (context.SchemaVersion != 3 || context.WebsitePolicyPublicKeyPath is null)
                throw new InvalidDataException("下载内容不是受支持的校区配置包。");
            var publicKeyName = GetTopLevelName(staging, context.PublicKeyPath);
            var websitePolicyKeyName = GetTopLevelName(staging, context.WebsitePolicyPublicKeyPath);
            ValidateCampusJson(files["campus.json"], context, publicKeyName, websitePolicyKeyName);
            Directory.Move(staging, final);
            try { return PackageManifest.Load(final); }
            catch
            {
                try { if (Directory.Exists(final)) Directory.Delete(final, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static Dictionary<string, byte[]> ReadArchiveEntries(byte[] archiveBytes)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using var input = new MemoryStream(archiveBytes, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count is < 3 or > MaximumFiles)
            throw new InvalidDataException("下载 ZIP 的文件数量不符合校区配置包格式。");

        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) ||
                name.IndexOfAny(['/', '\\', ':']) >= 0 || name.Any(char.IsControl) ||
                (entry.ExternalAttributes >> 16 & (int)FileAttributes.ReparsePoint) != 0 ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("下载 ZIP 含有目录、路径穿越或链接条目。");
            if (entry.Length is < 1 or > MaximumFileBytes)
                throw new InvalidDataException("下载 ZIP 中的文件大小无效或超过限制。");
            totalBytes += entry.Length;
            if (totalBytes > MaximumArchiveBytes || files.ContainsKey(name))
                throw new InvalidDataException("下载 ZIP 文件重复或解压后超过大小限制。");

            using var source = entry.Open();
            using var target = new MemoryStream((int)entry.Length);
            var buffer = new byte[8192];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (target.Length + read > MaximumFileBytes || target.Length + read > entry.Length)
                    throw new InvalidDataException("下载 ZIP 条目实际内容超过其声明大小。");
                target.Write(buffer, 0, read);
            }
            if (target.Length != entry.Length)
                throw new InvalidDataException("下载 ZIP 条目不完整。");
            files.Add(name, target.ToArray());
        }

        if (!files.ContainsKey("manifest.json") || !files.ContainsKey("campus.json"))
            throw new InvalidDataException("下载 ZIP 缺少校区清单文件。");
        return files;
    }

    private static HashSet<string> GetExpectedFileNames(byte[] manifestBytes)
    {
        using var document = JsonDocument.Parse(manifestBytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number || version.GetInt32() != 3)
            throw new InvalidDataException("局域网下载只接受 schemaVersion=3 的校区配置清单。");
        var allowedFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "schemaVersion", "packageId", "targetOs", "architecture", "campus", "computerPrefix",
            "telemetryEndpoint", "publicKey", "websitePolicyPublicKey"
        };
        var seenFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!allowedFields.Contains(property.Name) || !seenFields.Add(property.Name))
                throw new InvalidDataException("校区清单含有重复或未允许的字段。");
            if (property.Name == "telemetryEndpoint" &&
                (property.Value.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(property.Value.GetString())))
                throw new InvalidDataException("局域网校区配置不允许设置遥测端点。");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json", "campus.json" };
        AddManifestFileName(root, "publicKey", names);
        AddManifestFileName(root, "websitePolicyPublicKey", names);
        if (names.Count != 4)
            throw new InvalidDataException("校区配置清单中的公钥文件名重复。");
        return names;
    }

    private static void AddManifestFileName(JsonElement root, string propertyName, HashSet<string> names)
    {
        if (!root.TryGetProperty(propertyName, out var file) || file.ValueKind != JsonValueKind.Object ||
            !file.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"校区清单缺少 {propertyName} 文件路径。");
        var name = path.GetString() ?? "";
        if (name.Length is < 5 or > 200 || name != Path.GetFileName(name) ||
            name.IndexOfAny(['/', '\\', ':']) >= 0 || name.Any(char.IsControl) ||
            !name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("private", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("admin.txt", StringComparison.OrdinalIgnoreCase) || !names.Add(name))
            throw new InvalidDataException($"{propertyName} 必须是包内固定位置的普通公钥 PEM 文件。");
    }

    private static string GetTopLevelName(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) ||
            relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new InvalidDataException("局域网分发只接受校区配置包根目录中的公钥文件。");
        return relative;
    }

    private static void EnsureOnlyFixedFiles(string root, IReadOnlyCollection<string> allowedNames)
    {
        EnsureOrdinaryDirectory(root);
        var entries = Directory.EnumerateFileSystemEntries(root).ToArray();
        var permittedOnDisk = allowedNames.Append("README.md").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (entries.Length < allowedNames.Count || entries.Length > allowedNames.Count + 1 ||
            entries.Select(path => Path.GetFileName(path)).Any(name => name is null || !permittedOnDisk.Contains(name)))
            throw new InvalidDataException("校区配置目录含有额外或缺少的文件；局域网服务只接受固定校区资料。");
        foreach (var path in entries)
        {
            var info = new FileInfo(path);
            if (Directory.Exists(path) || info.LinkTarget is not null ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("局域网分发目录不能包含子目录、符号链接或重解析点。");
        }
    }

    private static void EnsureOrdinaryDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("校区配置目录必须是本机普通目录，不能是链接或重解析点。");
        EnsureNoReparsePointsInAncestors(path);
    }

    private static void EnsureNoReparsePointsInAncestors(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var current = Path.GetPathRoot(fullPath) ?? throw new InvalidDataException("校区配置目录路径无效。");
        foreach (var part in fullPath[current.Length..].Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("校区配置目录的父路径含有符号链接或重解析点。");
        }
    }

    private static byte[] ReadBoundedFile(string path, int limit)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 1 || info.Length > limit || info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"校区配置文件缺失、过大或不是普通文件：{info.Name}");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != info.Length || stream.Length > limit)
            throw new InvalidDataException($"读取校区配置文件时发现其大小发生变化：{info.Name}");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void VerifyDigest(byte[] bytes, string expected, string label)
    {
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label} 在创建局域网快照期间发生变化。");
    }

    private static void ValidateCampusJson(byte[] bytes, PackageContext context, string publicKeyName, string websitePolicyKeyName)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("campus.json 必须是 JSON 对象。");
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!values.TryAdd(property.Name, property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null))
                throw new InvalidDataException("campus.json 含有重复字段。");
        }
        if (values.Keys.Any(name => name is not ("campus" or "computerPrefix" or "keyFile" or "websitePolicyKeyFile")) ||
            values.GetValueOrDefault("campus") != context.Campus ||
            values.GetValueOrDefault("computerPrefix") != context.ComputerPrefix ||
            values.GetValueOrDefault("keyFile") != publicKeyName ||
            values.GetValueOrDefault("websitePolicyKeyFile") != websitePolicyKeyName)
            throw new InvalidDataException("campus.json 与已校验的配置清单不一致。");
    }
}
