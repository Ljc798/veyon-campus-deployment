using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

/// <summary>
/// Teacher-side slice of P7-08: generates a self-contained student
/// deployment folder (campus.json, RSA public key, installer resource,
/// manifest.json) from the fixed Veyon 4.11.2.0 installer and a campus name.
/// Stores the teacher private key in a sibling teacher-only directory, never in the student package.
/// </summary>
public static class PackageBuilder
{
    public static string Build(string outputDirectory, string campus, string computerPrefix,
        string installerSourcePath, int keyBits = 2048)
    {
        if (string.IsNullOrWhiteSpace(campus) || campus.Length > 100 ||
            !System.Text.RegularExpressions.Regex.IsMatch(campus, "^[A-Za-z0-9_-]{1,100}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("校区 ID 只能包含 1–100 个英文字母、数字、连字符或下划线。");
        MachineNaming.CreateRange(computerPrefix, "1", "150");
        if (!File.Exists(installerSourcePath))
            throw new FileNotFoundException("安装程序文件不存在。", installerSourcePath);
        if (new FileInfo(installerSourcePath).LinkTarget is not null)
            throw new InvalidDataException("Veyon 安装程序不能通过符号链接选择。");
        const string installerDestName = VeyonInstallerTrust.FileName;
        if (!Path.GetFileName(installerSourcePath).Equals(installerDestName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"请选择固定名称的 Veyon {VeyonInstallerTrust.Version} x64 安装程序。");

        var finalRoot = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(finalRoot) || File.Exists(finalRoot))
            throw new IOException("输出目录已存在；为防止覆盖资料或密钥，不能复用该路径。");
        var parent = Path.GetDirectoryName(finalRoot) ?? throw new InvalidDataException("输出目录无效。");
        Directory.CreateDirectory(parent);
        var teacherKeyDirectory = Path.Combine(parent, Path.GetFileName(finalRoot) + "-teacher-only");
        if (Directory.Exists(teacherKeyDirectory) || File.Exists(teacherKeyDirectory))
            throw new IOException("教师密钥目录已存在；为防止覆盖私钥，不能复用该路径。");
        var root = Path.Combine(parent, ".student-package-staging-" + Guid.NewGuid().ToString("N"));
        var teacherKeyStaging = Path.Combine(parent, ".teacher-key-staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            CreatePrivateDirectory(teacherKeyStaging);

            // 1. RSA key pair: public key into the package, private key stays in a restricted sibling directory.
            using var rsa = RSA.Create(keyBits);
            var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
            var privatePem = rsa.ExportRSAPrivateKeyPem();
            var keyFileName = campus + "-public.pem";
            var privatePath = Path.Combine(teacherKeyStaging, campus + "-private.pem");
            var publicPath = Path.Combine(root, keyFileName);
            File.WriteAllText(publicPath, publicPem);
            File.WriteAllText(privatePath, privatePem);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(privatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            // 2. campus.json with BOM, matching the legacy teacher script format.
            var campusJson = JsonSerializer.Serialize(new
            {
                campus, computerPrefix, keyFile = keyFileName
            });
            var bom = new UTF8Encoding(true);
            var jsonBytes = bom.GetPreamble().Concat(Encoding.UTF8.GetBytes(campusJson)).ToArray();
            File.WriteAllBytes(Path.Combine(root, "campus.json"), jsonBytes);

            // 3. copy the installer into the package.
            var installerDest = Path.Combine(root, installerDestName);
            File.Copy(installerSourcePath, installerDest, overwrite: true);

            // 4. manifest.json (schema 1) binding resources to their digests.
            long Size(string p) => new FileInfo(p).Length;
            string Hash(string p) { using var s = File.OpenRead(p); return Convert.ToHexString(SHA256.HashData(s)); }
            var manifest = new
            {
                schemaVersion = 1,
                packageId = Guid.NewGuid().ToString(),
                targetOs = "windows",
                architecture = "x64",
                campus,
                computerPrefix,
                publicKey = new { path = keyFileName, size = Size(publicPath), sha256 = Hash(publicPath) },
                installer = new { path = installerDestName, size = Size(installerDest), sha256 = Hash(installerDest) }
            };
            File.WriteAllText(Path.Combine(root, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            // 5. README so the folder is self-explaining.
            File.WriteAllText(Path.Combine(root, "README.md"),
                $"# 校区部署包：{campus}\n\n" +
                $"由教师端 App 生成。内含 Veyon {VeyonInstallerTrust.Version} 安装程序、校区公钥与 manifest.json。\n" +
                "不包含教师私钥或 admin.txt；学生端 App 读取本文件夹，执行前仍须通过预检。\n");
            // Defense in depth: only the explicit student-package allowlist may exist in root.
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                keyFileName, "campus.json", installerDestName, "manifest.json", "README.md"
            };
            var unexpected = Directory.EnumerateFileSystemEntries(root)
                .Select(Path.GetFileName).Where(name => name is null || !allowed.Contains(name)).ToArray();
            if (unexpected.Length != 0)
                throw new InvalidDataException("生成目录包含未允许的文件；学生包已拒绝完成。");
            Directory.Move(teacherKeyStaging, teacherKeyDirectory);
            Directory.Move(root, finalRoot);
            return finalRoot;
        }
        catch
        {
            // Remove only our own incomplete student staging tree; preserve generated private material.
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (Directory.Exists(teacherKeyStaging) && !Directory.Exists(teacherKeyDirectory))
            {
                if (Directory.EnumerateFileSystemEntries(teacherKeyStaging).Any())
                    Directory.Move(teacherKeyStaging, teacherKeyDirectory);
                else Directory.Delete(teacherKeyStaging);
            }
            throw;
        }
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var userSid = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("无法识别当前 Windows 用户，不能安全保存教师私钥。");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            AddFullControlRule(security, userSid);
            AddFullControlRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
            FileSystemAclExtensions.CreateDirectory(security, path);
            return;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(path, ownerOnly);
            File.SetUnixFileMode(path, ownerOnly);
            return;
        }

        throw new PlatformNotSupportedException("当前平台不支持创建仅教师可访问的密钥目录。");
    }

    [SupportedOSPlatform("windows")]
    private static void AddFullControlRule(DirectorySecurity security, SecurityIdentifier sid) =>
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
}
