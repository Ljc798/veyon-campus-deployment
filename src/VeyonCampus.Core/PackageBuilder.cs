using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

/// <summary>
/// Builds a campus configuration package. Veyon itself is embedded in the
/// student app, so the package carries only the campus public key and settings.
/// </summary>
public static class PackageBuilder
{
    public static string Build(string outputDirectory, string campus, string computerPrefix,
        string publicKeySourcePath, string? websitePolicyPublicKeyPem = null)
    {
        if (string.IsNullOrWhiteSpace(campus) || campus.Length > 100 ||
            !System.Text.RegularExpressions.Regex.IsMatch(campus, "^[A-Za-z0-9_-]{1,100}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("校区 ID 只能包含 1–100 个英文字母、数字、连字符或下划线。");
        MachineNaming.CreateRange(computerPrefix, "1", "150");
        var publicPem = ReadPublicKeyPem(publicKeySourcePath);
        var websitePolicyPem = websitePolicyPublicKeyPem is null ? null : ReadRsaPublicKeyPem(websitePolicyPublicKeyPem);

        var finalRoot = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(finalRoot) || File.Exists(finalRoot))
            throw new IOException("输出目录已存在；为防止覆盖资料或密钥，不能复用该路径。");
        var parent = Path.GetDirectoryName(finalRoot) ?? throw new InvalidDataException("输出目录无效。");
        Directory.CreateDirectory(parent);
        var root = Path.Combine(parent, ".student-package-staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);

            // Only the public half exported from Veyon's configured key store is included.
            var keyFileName = campus + "-public.pem";
            var publicPath = Path.Combine(root, keyFileName);
            File.WriteAllText(publicPath, publicPem);

            string? websitePolicyKeyFileName = null;
            string? websitePolicyPublicPath = null;
            if (websitePolicyPem is not null)
            {
                websitePolicyKeyFileName = "website-policy-public.pem";
                websitePolicyPublicPath = Path.Combine(root, websitePolicyKeyFileName);
                File.WriteAllText(websitePolicyPublicPath, websitePolicyPem);
            }

            // campus.json with BOM, matching the legacy teacher script format.
            var campusJson = websitePolicyKeyFileName is null
                ? JsonSerializer.Serialize(new { campus, computerPrefix, keyFile = keyFileName })
                : JsonSerializer.Serialize(new { campus, computerPrefix, keyFile = keyFileName, websitePolicyKeyFile = websitePolicyKeyFileName });
            var bom = new UTF8Encoding(true);
            var jsonBytes = bom.GetPreamble().Concat(Encoding.UTF8.GetBytes(campusJson)).ToArray();
            File.WriteAllBytes(Path.Combine(root, "campus.json"), jsonBytes);

            long Size(string p) => new FileInfo(p).Length;
            string Hash(string p) { using var s = File.OpenRead(p); return Convert.ToHexString(SHA256.HashData(s)); }
            object manifest = websitePolicyPublicPath is null
                ? new
                {
                    schemaVersion = 2,
                    packageId = Guid.NewGuid().ToString(),
                    targetOs = "windows",
                    architecture = "x64",
                    campus,
                    computerPrefix,
                    publicKey = new { path = keyFileName, size = Size(publicPath), sha256 = Hash(publicPath) }
                }
                : new
                {
                    schemaVersion = 3,
                    packageId = Guid.NewGuid().ToString(),
                    targetOs = "windows",
                    architecture = "x64",
                    campus,
                    computerPrefix,
                    publicKey = new { path = keyFileName, size = Size(publicPath), sha256 = Hash(publicPath) },
                    websitePolicyPublicKey = new
                    {
                        path = websitePolicyKeyFileName!,
                        size = Size(websitePolicyPublicPath),
                        sha256 = Hash(websitePolicyPublicPath)
                    }
                };
            File.WriteAllText(Path.Combine(root, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            File.WriteAllText(Path.Combine(root, "README.md"),
                $"# 校区配置包：{campus}\n\n" +
                "本包只含校区公钥、网站策略验证公钥与命名配置，不含教师私钥或 Veyon 安装程序。固定版本 Veyon 已内嵌在 VeyonCampus App 中，学生电脑无需联网下载。\n" +
                "将本目录与完整的 VeyonCampus App 一起交给学生；学生端在 App 中选择本目录后即可离线安装和配置。\n" +
                "网站策略私钥只保留在教师 Windows 用户证书库；学生端代理只接收经签名的策略。\n" +
                "本包不包含教师私钥或 admin.txt；执行前仍须通过预检。\n");

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                keyFileName, "campus.json", "manifest.json", "README.md"
            };
            if (websitePolicyKeyFileName is not null) allowed.Add(websitePolicyKeyFileName);
            var unexpected = Directory.EnumerateFileSystemEntries(root)
                .Select(Path.GetFileName).Where(name => name is null || !allowed.Contains(name)).ToArray();
            if (unexpected.Length != 0)
                throw new InvalidDataException("生成目录包含未允许的文件；学生配置包已拒绝完成。");
            Directory.Move(root, finalRoot);
            return finalRoot;
        }
        catch
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static string ReadPublicKeyPem(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Veyon 导出的公钥文件不存在。", path);
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("公钥文件不能是符号链接或重解析点。");
        if (info.Length is <= 0 or > 64 * 1024)
            throw new InvalidDataException("公钥文件大小无效。");

        var pem = File.ReadAllText(path);
        if (pem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("导出文件包含私钥材料；为防止密钥泄露，学生配置包已停止生成。");
        if (!pem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) &&
            !pem.Contains("-----BEGIN RSA PUBLIC KEY-----", StringComparison.Ordinal))
            throw new InvalidDataException("导出文件不是可识别的 RSA 公钥。");

        return ReadRsaPublicKeyPem(pem);
    }

    internal static string ReadRsaPublicKeyPem(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
            throw new InvalidDataException("RSA 公钥内容为空。");
        if (pem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("网站策略公钥输入包含私钥材料；学生配置包已停止生成。");
        if (!pem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) &&
            !pem.Contains("-----BEGIN RSA PUBLIC KEY-----", StringComparison.Ordinal))
            throw new InvalidDataException("输入不是可识别的 RSA 公钥。");
        using var rsa = RSA.Create();
        try { rsa.ImportFromPem(pem); }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new InvalidDataException("Veyon 导出的 RSA 公钥无效。", ex);
        }
        if (rsa.KeySize is < 2048 or > 4096)
            throw new InvalidDataException("RSA 公钥位长必须在 2048–4096 位范围内。");
        return rsa.ExportSubjectPublicKeyInfoPem();
    }
}
