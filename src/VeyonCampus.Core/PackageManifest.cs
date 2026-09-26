using System.Security.Cryptography;
using System.Text.Json;

namespace VeyonCampus.Core;

/// <summary>Read-only schema v1/v2 parser. File hashes prove integrity, not the publisher's identity.</summary>
public static class PackageManifest
{
    public static PackageContext Load(string directory)
    {
        var root = Path.GetFullPath(directory);
        EnsureNoLinks(root, root);
        RejectStudentPackageSecrets(root);
        var manifestPath = Path.Combine(root, "manifest.json");
        var manifestInfo = new FileInfo(manifestPath);
        if (!manifestInfo.Exists || manifestInfo.Length is < 1 or > 64 * 1024)
            throw new InvalidDataException("manifest.json 不存在或超过 64 KiB。");
        EnsureNoLinks(root, manifestPath);
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var json = document.RootElement;
        if (json.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("manifest.json 必须是 JSON 对象。");
        NoDuplicateFields(json);
        if (!json.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var version) || version is not (1 or 2 or 3))
            throw new InvalidDataException("不支持此部署包版本；当前只支持 schemaVersion=1、2 或 3。");
        if (!Guid.TryParse(RequiredString(json, "packageId", 64), out _))
            throw new InvalidDataException("packageId 必须是有效的 GUID。");
        if (RequiredString(json, "targetOs", 16) != "windows" || RequiredString(json, "architecture", 16) != "x64")
            throw new InvalidDataException("部署包目标必须是 Windows x64。");
        var campus = RequiredString(json, "campus", 100);
        var prefix = RequiredString(json, "computerPrefix", 15);
        if (campus.Any(char.IsControl))
            throw new InvalidDataException("校区名称无效。");
        MachineNaming.CreateRange(prefix, "1", "150");
        var keyEntry = FileEntry(json, "publicKey", root, 64 * 1024);
        (string Path, string Sha256)? websitePolicyKeyEntry = version == 3
            ? FileEntry(json, "websitePolicyPublicKey", root, 64 * 1024)
            : null;
        (string Path, string Sha256)? installerEntry = null;
        if (version == 1)
        {
            var entry = FileEntry(json, "installer", root, 300L * 1024 * 1024);
            if (!entry.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装程序的文件类型不正确。");
            installerEntry = entry;
        }
        else if (json.TryGetProperty("installer", out _))
            throw new InvalidDataException("schemaVersion=2/3 只允许携带校区配置；Veyon 安装器已内嵌在 App 中。");
        if (!keyEntry.Path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("公钥文件类型不正确。");
        if (new FileInfo(keyEntry.Path).LinkTarget is not null)
            throw new InvalidDataException("公钥文件不能使用符号链接。");
        if (installerEntry is not null && new FileInfo(installerEntry.Value.Path).LinkTarget is not null)
            throw new InvalidDataException("安装资源不能使用符号链接。");
        if (websitePolicyKeyEntry is not null && !websitePolicyKeyEntry.Value.Path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("网站策略公钥文件类型不正确。");
        var keyText = File.ReadAllText(keyEntry.Path);
        if (keyText.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("学生部署包只能包含公钥。");
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(keyText);
            if (rsa.ExportParameters(false).Modulus! is { Length: < 256 or > 512 })
                throw new CryptographicException("公钥位长不支持。");
            string? websitePolicyPath = null;
            string? websitePolicySha256 = null;
            if (websitePolicyKeyEntry is not null)
            {
                var policyPem = File.ReadAllText(websitePolicyKeyEntry.Value.Path);
                if (policyPem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("学生部署包网站策略文件只能包含公钥。");
                using var policyRsa = RSA.Create();
                policyRsa.ImportFromPem(policyPem);
                if (policyRsa.KeySize is < 2048 or > 4096)
                    throw new CryptographicException("网站策略 RSA 公钥位长不支持。");
                websitePolicyPath = websitePolicyKeyEntry.Value.Path;
                websitePolicySha256 = websitePolicyKeyEntry.Value.Sha256;
            }
            return new PackageContext(root, campus, prefix, keyEntry.Path,
                HashFile(manifestPath), keyEntry.Sha256,
                Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())),
                version, installerEntry?.Path, installerEntry?.Sha256,
                websitePolicyPath, websitePolicySha256);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new InvalidDataException("部署包公钥不是有效的 RSA PEM 文件。", ex);
        }
    }

    private static void RejectStudentPackageSecrets(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var name = Path.GetFileName(path);
                if (name.Equals("admin.txt", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("private", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("secret", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"学生部署包包含禁止的敏感文件或目录：{name}");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("学生部署包不能包含符号链接或重解析点。");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
            }
        }
    }

    private static (string Path, string Sha256) FileEntry(JsonElement json, string field, string root, long limit)
    {
        if (!json.TryGetProperty(field, out var entry) || entry.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"缺少 {field} 文件清单。");
        NoDuplicateFields(entry);
        var relative = RequiredString(entry, "path", 240);
        if (relative.Any(char.IsControl) || Path.IsPathRooted(relative) || relative.IndexOfAny(['\\', ':']) >= 0 ||
            relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"{field} 必须是包内规范相对路径。");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Path.GetRelativePath(root, path).StartsWith("..", StringComparison.Ordinal) &&
            !Path.GetRelativePath(root, path).Equals(".", StringComparison.Ordinal))
        {
            EnsureNoLinks(root, path);
        }
        else throw new InvalidDataException($"{field} 文件路径超出部署包。");
        if (!entry.TryGetProperty("size", out var sizeJson) || sizeJson.ValueKind != JsonValueKind.Number ||
            !sizeJson.TryGetInt64(out var size) || size is < 1 || size > limit)
            throw new InvalidDataException($"{field} 文件大小无效。");
        var hash = RequiredString(entry, "sha256", 64);
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException($"{field} SHA-256 格式无效。");
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != size || file.Length > limit ||
            !string.Equals(HashFile(path), hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{field} 文件缺失、大小不符或 SHA-256 不匹配。");
        return (path, hash.ToUpperInvariant());
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void EnsureNoLinks(string root, string path)
    {
        if (new DirectoryInfo(root).LinkTarget is not null)
            throw new InvalidDataException("部署包根目录不能是符号链接。");
        var current = root;
        foreach (var part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
        {
            if (part == ".") continue;
            current = Path.Combine(current, part);
            if (File.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0 ||
                Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("部署包资源不能使用符号链接或重解析点。");
        }
    }

    private static void NoDuplicateFields(JsonElement obj)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in obj.EnumerateObject())
            if (!seen.Add(field.Name)) throw new InvalidDataException($"JSON 字段重复：{field.Name}");
    }

    private static string RequiredString(JsonElement obj, string field, int maximumLength)
    {
        if (!obj.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > maximumLength)
            throw new InvalidDataException($"缺少有效的 {field} 字段。");
        return value.GetString()!;
    }
}
