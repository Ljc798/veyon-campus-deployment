using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace VeyonCampus.Core;

/// <summary>
/// Creates and receives the small, fixed set of public campus configuration
/// files used by CloudBase package publishing. This format deliberately excludes
/// arbitrary files and only permits the optional README as local package metadata.
/// </summary>
public static class CampusConfigurationArchive
{
    public const int MaximumArchiveBytes = 64 * 1024;
    private const int MaximumFileBytes = 16 * 1024;
    private const int MaximumFiles = 7;

    public static byte[] Create(string packageDirectory)
    {
        var root = Path.GetFullPath(packageDirectory);
        var context = PackageManifest.Load(root);
        if (context.SchemaVersion is not (3 or 4 or 5 or 6) || context.WebsitePolicyPublicKeyPath is null ||
            !AnonymousUsageHeartbeat.IsAllowedPackageEndpoint(context.TelemetryEndpoint))
            throw new InvalidDataException("当前只接受 schemaVersion=3/4/5/6 的校区配置包。");

        var publicKeyName = GetTopLevelName(root, context.PublicKeyPath);
        var websitePolicyKeyName = GetTopLevelName(root, context.WebsitePolicyPublicKeyPath);
        var applicationPolicyKeyName = context.ApplicationPolicyPublicKeyPath is null ? null :
            GetTopLevelName(root, context.ApplicationPolicyPublicKeyPath);
        var studentSystemPolicyKeyName = context.StudentSystemPolicyPublicKeyPath is null ? null :
            GetTopLevelName(root, context.StudentSystemPolicyPublicKeyPath);
        var names = new[] { "manifest.json", "campus.json", publicKeyName, websitePolicyKeyName }
            .Concat(applicationPolicyKeyName is null ? Array.Empty<string>() : [applicationPolicyKeyName]).ToArray();
        if (studentSystemPolicyKeyName is not null) names = names.Append(studentSystemPolicyKeyName).ToArray();
        if (context.SchemaVersion >= 4) names = names.Append("README.md").ToArray();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new InvalidDataException("校区配置包中的文件名重复，已停止处理。");
        EnsureOnlyFixedFiles(root, names);

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            files.Add(name, ReadBoundedFile(Path.Combine(root, name), MaximumFileBytes));

        VerifyDigest(files["manifest.json"], context.ConfigSha256, "manifest.json");
        VerifyDigest(files[publicKeyName], context.PublicKeySha256, "Veyon 校区公钥");
        VerifyDigest(files[websitePolicyKeyName], context.WebsitePolicyPublicKeySha256!, "网站策略公钥");
        if (applicationPolicyKeyName is not null)
            VerifyDigest(files[applicationPolicyKeyName], context.ApplicationPolicyPublicKeySha256!, "应用策略公钥");
        if (studentSystemPolicyKeyName is not null)
            VerifyDigest(files[studentSystemPolicyKeyName], context.StudentSystemPolicyPublicKeySha256!, "系统策略公钥");

        var snapshotDirectory = Path.Combine(Path.GetDirectoryName(root)!,
            ".VeyonCampus-PackageSnapshot-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(snapshotDirectory);
            foreach (var (name, bytes) in files)
                File.WriteAllBytes(Path.Combine(snapshotDirectory, name), bytes);

            var snapshot = PackageManifest.Load(snapshotDirectory);
            if (snapshot.SchemaVersion != context.SchemaVersion || snapshot.Campus != context.Campus ||
                snapshot.ComputerPrefix != context.ComputerPrefix ||
                !string.Equals(snapshot.TelemetryEndpoint, context.TelemetryEndpoint, StringComparison.Ordinal) ||
                snapshot.RecommendedOperations != context.RecommendedOperations)
                throw new InvalidDataException("校区配置包在生成云端上传快照时发生变化。");
            ValidateCampusJson(files["campus.json"], snapshot, publicKeyName, websitePolicyKeyName,
                applicationPolicyKeyName, studentSystemPolicyKeyName);

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
                throw new InvalidDataException("校区配置包超过 64 KiB 的大小限制。");
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
            if (context.SchemaVersion is not (3 or 4 or 5 or 6) || context.WebsitePolicyPublicKeyPath is null ||
                (context.SchemaVersion >= 4) != (context.ApplicationPolicyPublicKeyPath is not null) ||
                (context.SchemaVersion >= 5) != (context.StudentSystemPolicyPublicKeyPath is not null))
                throw new InvalidDataException("下载内容不是受支持的校区配置包。");
            var publicKeyName = GetTopLevelName(staging, context.PublicKeyPath);
            var websitePolicyKeyName = GetTopLevelName(staging, context.WebsitePolicyPublicKeyPath);
            var applicationPolicyKeyName = context.ApplicationPolicyPublicKeyPath is null ? null :
                GetTopLevelName(staging, context.ApplicationPolicyPublicKeyPath);
            var studentSystemPolicyKeyName = context.StudentSystemPolicyPublicKeyPath is null ? null :
                GetTopLevelName(staging, context.StudentSystemPolicyPublicKeyPath);
            ValidateCampusJson(files["campus.json"], context, publicKeyName, websitePolicyKeyName,
                applicationPolicyKeyName, studentSystemPolicyKeyName);
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
        using var document = ParseUtf8Json(manifestBytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number || version.GetInt32() is not (3 or 4 or 5 or 6))
            throw new InvalidDataException("当前只接受 schemaVersion=3/4/5/6 的校区配置清单。");
        var allowedFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "schemaVersion", "packageId", "targetOs", "architecture", "campus", "computerPrefix",
            "telemetryEndpoint", "publicKey", "websitePolicyPublicKey", "applicationPolicyPublicKey", "studentSystemPolicyPublicKey",
            "compatibility", "files", "recommendedOperations"
        };
        var seenFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!allowedFields.Contains(property.Name) || !seenFields.Add(property.Name))
                throw new InvalidDataException("校区清单含有重复或未允许的字段。");
            if (property.Name == "telemetryEndpoint" &&
                (property.Value.ValueKind != JsonValueKind.String ||
                 !AnonymousUsageHeartbeat.IsAllowedPackageEndpoint(property.Value.GetString())))
                throw new InvalidDataException("遥测端点只能留空或使用项目配置的 CloudBase 心跳地址。");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json", "campus.json" };
        AddManifestFileName(root, "publicKey", names);
        AddManifestFileName(root, "websitePolicyPublicKey", names);
        if (version.GetInt32() >= 4) AddManifestFileName(root, "applicationPolicyPublicKey", names);
        else if (root.TryGetProperty("applicationPolicyPublicKey", out _))
            throw new InvalidDataException("应用策略公钥只允许出现在 schemaVersion=4/5/6。");
        if (version.GetInt32() >= 5) AddManifestFileName(root, "studentSystemPolicyPublicKey", names);
        else if (root.TryGetProperty("studentSystemPolicyPublicKey", out _))
            throw new InvalidDataException("系统策略公钥只允许出现在 schemaVersion=5/6。");
        if (names.Count != (version.GetInt32() >= 5 ? 6 : version.GetInt32() == 4 ? 5 : 4))
            throw new InvalidDataException("校区配置清单中的公钥文件名重复。");
        if (version.GetInt32() >= 4)
        {
            if (!root.TryGetProperty("compatibility", out var compatibility) || compatibility.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("files", out var fileArray) || fileArray.ValueKind != JsonValueKind.Array ||
                fileArray.GetArrayLength() != (version.GetInt32() >= 5 ? 6 : 5))
                throw new InvalidDataException($"schemaVersion={version.GetInt32()} 必须包含 compatibility 和完整 files 清单。");
            names.Add("README.md");
            var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in fileArray.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object || !file.TryGetProperty("path", out var path) ||
                    path.ValueKind != JsonValueKind.String || !declared.Add(path.GetString() ?? ""))
                    throw new InvalidDataException($"schemaVersion={version.GetInt32()} files 清单无效或有重复路径。");
            }
            if (!declared.SetEquals(names.Where(name => name is not "manifest.json" and not "campus.json").Append("campus.json")))
                throw new InvalidDataException($"schemaVersion={version.GetInt32()} files 清单必须完整包含所有载荷文件。");
        }
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
            throw new InvalidDataException("只接受校区配置包根目录中的公钥文件。");
        return relative;
    }

    private static void EnsureOnlyFixedFiles(string root, IReadOnlyCollection<string> allowedNames)
    {
        EnsureOrdinaryDirectory(root);
        var entries = Directory.EnumerateFileSystemEntries(root).ToArray();
        var permittedOnDisk = allowedNames.Append("README.md").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (entries.Length < allowedNames.Count || entries.Length > allowedNames.Count + 1 ||
            entries.Select(path => Path.GetFileName(path)).Any(name => name is null || !permittedOnDisk.Contains(name)))
            throw new InvalidDataException("校区配置目录含有额外或缺少的文件；云端发布只接受固定格式的校区资料。");
        foreach (var path in entries)
        {
            var info = new FileInfo(path);
            if (Directory.Exists(path) || info.LinkTarget is not null ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("校区配置目录不能包含子目录、符号链接或重解析点。");
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
            throw new InvalidDataException($"{label} 在创建校区配置包快照期间发生变化。");
    }

    private static void ValidateCampusJson(byte[] bytes, PackageContext context, string publicKeyName,
        string websitePolicyKeyName, string? applicationPolicyKeyName, string? studentSystemPolicyKeyName)
    {
        using var document = ParseUtf8Json(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("campus.json 必须是 JSON 对象。");
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!values.TryAdd(property.Name, property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null))
                throw new InvalidDataException("campus.json 含有重复字段。");
        }
        if (values.Keys.Any(name => name is not ("campus" or "computerPrefix" or "keyFile" or "websitePolicyKeyFile" or "applicationPolicyKeyFile" or "systemPolicyKeyFile")) ||
            values.GetValueOrDefault("campus") != context.Campus ||
            values.GetValueOrDefault("computerPrefix") != context.ComputerPrefix ||
            values.GetValueOrDefault("keyFile") != publicKeyName ||
            values.GetValueOrDefault("websitePolicyKeyFile") != websitePolicyKeyName ||
            values.GetValueOrDefault("applicationPolicyKeyFile") != applicationPolicyKeyName ||
            values.GetValueOrDefault("systemPolicyKeyFile") != studentSystemPolicyKeyName)
            throw new InvalidDataException("campus.json 与已校验的配置清单不一致。");
    }

    private static JsonDocument ParseUtf8Json(byte[] bytes)
    {
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? 3
            : 0;
        return JsonDocument.Parse(bytes.AsMemory(offset));
    }
}
