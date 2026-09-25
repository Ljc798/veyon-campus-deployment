using System.Security.Cryptography;
using System.Text.Json;

namespace VeyonCampus.Core;

public sealed record PackageContext(string Root, string Campus, string ComputerPrefix, string PublicKeyPath,
    string ConfigSha256, string PublicKeySha256, string PublicKeyFingerprint,
    int SchemaVersion = 0, string? InstallerPath = null, string? InstallerSha256 = null)
{
    public static PackageContext Load(string directory)
    {
        var root = Path.GetFullPath(directory);
        return File.Exists(Path.Combine(root, "manifest.json"))
            ? PackageManifest.Load(root)
            : LoadLegacy(root);
    }

    public static PackageContext LoadLegacy(string directory)
    {
        var root = Path.GetFullPath(directory);
        if (new DirectoryInfo(root).LinkTarget is not null)
            throw new InvalidDataException("部署包文件夹不能是符号链接。");
        var configPath = Path.Combine(root, "campus.json");
        using var document = JsonDocument.Parse(ReadLimited(configPath));
        var obj = document.RootElement;
        if (obj.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("campus.json 必须是 JSON 对象。");
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject())
            if (!known.Add(property.Name))
                throw new InvalidDataException($"campus.json 中 {property.Name} 字段重复。");
        string Required(string field)
        {
            if (!obj.TryGetProperty(field, out var item) || item.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(item.GetString()))
                throw new InvalidDataException($"campus.json 缺少有效的 {field} 字段。");
            return item.GetString()!;
        }
        var campus = Required("campus");
        var prefix = Required("computerPrefix");
        var keyFile = Required("keyFile");
        if (campus.Length > 100 || campus.Any(char.IsControl))
            throw new InvalidDataException("校区名称过长或包含控制字符。");
        MachineNaming.CreateRange(prefix, "1", "150");
        if (keyFile.Length > 240 || keyFile.Any(char.IsControl) ||
            keyFile.IndexOfAny(['/', '\\', ':']) >= 0 || keyFile is "." or ".." ||
            !keyFile.EndsWith("-public.pem", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("keyFile 必须是部署目录内的 *-public.pem 公钥文件名。");
        var keyPath = Path.Combine(root, keyFile);
        var publicKey = ReadLimited(keyPath);
        if (publicKey.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("学生部署包只能使用公钥。");
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKey);
            return new PackageContext(root, campus, prefix, keyPath,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(configPath))),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(keyPath))),
                Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new InvalidDataException("公钥不是有效的 RSA PEM 文件。", ex);
        }
    }

    public void VerifyUnchanged()
    {
        if (Load(Root) != this)
            throw new InvalidDataException("部署包在读取后发生变化，请重新选择并检查。");
    }

    private static string ReadLimited(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0 || file.Length > 64 * 1024 || file.LinkTarget is not null)
            throw new InvalidDataException($"资料文件不存在、为空、过大或是符号链接：{Path.GetFileName(path)}");
        return File.ReadAllText(path);
    }
}
