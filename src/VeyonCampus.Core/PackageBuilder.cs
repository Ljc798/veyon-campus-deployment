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
        string publicKeySourcePath, string? websitePolicyPublicKeyPem = null,
        bool enableAnonymousTelemetry = false, CancellationToken cancellationToken = default)
        => BuildCore(outputDirectory, campus, computerPrefix, publicKeySourcePath,
            websitePolicyPublicKeyPem, enableAnonymousTelemetry,
            cancellationToken, PhysicalPackageBuildFileSystem.Instance);

    internal static string BuildCore(string outputDirectory, string campus, string computerPrefix,
        string publicKeySourcePath, string? websitePolicyPublicKeyPem, bool enableAnonymousTelemetry,
        CancellationToken cancellationToken, IPackageBuildFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        cancellationToken.ThrowIfCancellationRequested();
        if (enableAnonymousTelemetry && websitePolicyPublicKeyPem is null)
            throw new InvalidDataException("匿名每日统计要求生成 schemaVersion=3 校区配置包。");
        WebsitePolicySigningKeyStore.ValidateCampusId(campus);
        MachineNaming.CreateRange(computerPrefix, "1", "150");
        var publicPem = ReadPublicKeyPem(publicKeySourcePath);
        var websitePolicyPem = websitePolicyPublicKeyPem is null ? null : ReadRsaPublicKeyPem(websitePolicyPublicKeyPem);
        var finalRoot = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(finalRoot) || File.Exists(finalRoot))
            throw new IOException("输出目录已存在；为防止覆盖资料或密钥，不能复用该路径。");
        var parent = Path.GetDirectoryName(finalRoot) ?? throw new InvalidDataException("输出目录无效。");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(parent);
        var root = Path.Combine(parent, ".student-package-staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            fileSystem.CreateDirectory(root);

            // Only the public half exported from Veyon's configured key store is included.
            var keyFileName = campus + "-public.pem";
            var telemetryEndpoint = enableAnonymousTelemetry ? AnonymousUsageHeartbeat.DefaultEndpoint : "";
            var publicPath = Path.Combine(root, keyFileName);
            fileSystem.WriteAllText(publicPath, publicPem);
            cancellationToken.ThrowIfCancellationRequested();

            string? websitePolicyKeyFileName = null;
            string? websitePolicyPublicPath = null;
            if (websitePolicyPem is not null)
            {
                websitePolicyKeyFileName = "website-policy-public.pem";
                websitePolicyPublicPath = Path.Combine(root, websitePolicyKeyFileName);
                fileSystem.WriteAllText(websitePolicyPublicPath, websitePolicyPem);
                cancellationToken.ThrowIfCancellationRequested();
            }

            // campus.json with BOM, matching the legacy teacher script format.
            var campusJson = websitePolicyKeyFileName is null
                ? JsonSerializer.Serialize(new { campus, computerPrefix, keyFile = keyFileName })
                : JsonSerializer.Serialize(new { campus, computerPrefix, keyFile = keyFileName, websitePolicyKeyFile = websitePolicyKeyFileName });
            var bom = new UTF8Encoding(true);
            var jsonBytes = bom.GetPreamble().Concat(Encoding.UTF8.GetBytes(campusJson)).ToArray();
            fileSystem.WriteAllBytes(Path.Combine(root, "campus.json"), jsonBytes);
            cancellationToken.ThrowIfCancellationRequested();

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
                    telemetryEndpoint,
                    publicKey = new { path = keyFileName, size = Size(publicPath), sha256 = Hash(publicPath) },
                    websitePolicyPublicKey = new
                    {
                        path = websitePolicyKeyFileName!,
                        size = Size(websitePolicyPublicPath),
                        sha256 = Hash(websitePolicyPublicPath)
                    }
                };
            fileSystem.WriteAllText(Path.Combine(root, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            cancellationToken.ThrowIfCancellationRequested();

            fileSystem.WriteAllText(Path.Combine(root, "README.md"),
                $"# 校区配置包：{campus}\n\n" +
                "本包只含校区公钥、网站策略验证公钥与命名配置，不含教师私钥或 Veyon 安装程序。固定版本 Veyon 已内嵌在 VeyonCampus.StudentSetup 学生部署工具中，学生电脑无需联网下载。\n" +
                "请从可信发布页单独下载 StudentSetup 学生部署工具，将本目录与完整的学生工具文件夹配套交给部署人员；不要把 Teacher Console 教师控制台交给学生。学生端在 StudentSetup 中选择本目录后即可离线安装和配置。\n" +
                "网站策略私钥只保留在教师 Windows 用户证书库；学生端代理只接收经签名的策略。\n" +
                "本包不包含教师私钥或 admin.txt；执行前仍须通过预检。\n");
            cancellationToken.ThrowIfCancellationRequested();

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                keyFileName, "campus.json", "manifest.json", "README.md"
            };
            if (websitePolicyKeyFileName is not null) allowed.Add(websitePolicyKeyFileName);
            var actual = fileSystem.EnumerateFileSystemEntries(root)
                .Select(Path.GetFileName).Where(name => name is not null)
                .Select(name => name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!actual.SetEquals(allowed))
                throw new InvalidDataException("生成目录文件不完整或包含未允许的文件；学生配置包已拒绝完成。");

            // Re-read the staged package and all manifest digests before publishing it.
            var stagedPackage = PackageContext.Load(root);
            var expectedSchema = websitePolicyKeyFileName is null ? 2 : 3;
            if (stagedPackage.SchemaVersion != expectedSchema || stagedPackage.Campus != campus ||
                stagedPackage.ComputerPrefix != computerPrefix || stagedPackage.InstallerPath is not null)
                throw new InvalidDataException("生成的校区配置包与输入不一致；学生配置包已拒绝完成。");
            stagedPackage.VerifyUnchanged();
            cancellationToken.ThrowIfCancellationRequested();
            fileSystem.MoveDirectory(root, finalRoot);
            return finalRoot;
        }
        catch (Exception buildException)
        {
            try { if (fileSystem.DirectoryExists(root)) fileSystem.DeleteDirectory(root, recursive: true); }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"学生配置包未发布，且临时目录清理失败。请检查并删除临时目录：{root}",
                    new AggregateException(buildException, cleanupException));
            }
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

        var bytes = PackageManifest.ReadBytesLimited(path, 64 * 1024);
        var pem = PackageManifest.DecodeUtf8Text(bytes, path);
        using var rsa = VeyonPublicKeyValidator.Import(pem);
        return rsa.ExportSubjectPublicKeyInfoPem();
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

internal interface IPackageBuildFileSystem
{
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    void WriteAllText(string path, string contents);
    void WriteAllBytes(string path, byte[] contents);
    IEnumerable<string> EnumerateFileSystemEntries(string path);
    void MoveDirectory(string source, string destination);
    void DeleteDirectory(string path, bool recursive);
}

internal sealed class PhysicalPackageBuildFileSystem : IPackageBuildFileSystem
{
    public static PhysicalPackageBuildFileSystem Instance { get; } = new();
    private PhysicalPackageBuildFileSystem() { }
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);
    public void WriteAllBytes(string path, byte[] contents) => File.WriteAllBytes(path, contents);
    public IEnumerable<string> EnumerateFileSystemEntries(string path) => Directory.EnumerateFileSystemEntries(path);
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);
    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
}
