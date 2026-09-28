using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Runtime.Versioning;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Win32;

namespace VeyonCampus.Core;

/// <summary>A user-scoped signing key. Only its public half is placed in student packages.</summary>
public sealed class WebsitePolicySigningKey : IDisposable
{
    internal WebsitePolicySigningKey(RSA privateKey, string publicKeyPem, string certificateThumbprint)
    {
        PrivateKey = privateKey;
        PublicKeyPem = publicKeyPem;
        CertificateThumbprint = certificateThumbprint;
    }

    public RSA PrivateKey { get; }
    public string PublicKeyPem { get; }
    public string CertificateThumbprint { get; }
    public void Dispose() => PrivateKey.Dispose();
}

public sealed class WebsitePolicySigningKeyRecoveryRequiredException(string message) : CryptographicException(message);

/// <summary>Stores teacher signing keys in the current Windows user's certificate store.</summary>
public static class WebsitePolicySigningKeyStore
{
    private const string ThumbprintValueName = "SigningCertificateThumbprint";
    private const int SigningKeySize = 3072;
    private static readonly CngProvider SigningKeyProvider = CngProvider.MicrosoftSoftwareKeyStorageProvider;
    private sealed record CertificateSelection(X509Certificate2? Certificate, bool HasMatchingCertificates, bool Ambiguous);

    [SupportedOSPlatform("windows")]
    public static WebsitePolicySigningKey GetOrCreate(string campusId, bool replaceUnavailableKey = false)
    {
        EnsureWindows();
        ValidateCampusId(campusId);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        var pinnedThumbprint = ReadPinnedThumbprint(campusId);
        if (pinnedThumbprint is not null)
        {
            var pinned = FindByThumbprint(store, pinnedThumbprint);
            if (pinned is not null)
            {
                using (pinned)
                {
                    var pinnedKey = TryGetPrivateKey(pinned);
                    if (pinnedKey is not null)
                    {
                        PinThumbprint(campusId, pinned.Thumbprint);
                        return CreateSigningKey(pinned, pinnedKey);
                    }
                }
            }

            if (!replaceUnavailableKey)
                throw ReplacementRequired("已固定的教师签名证书缺失或没有可用私钥。为保护学生端已信任的公钥，系统没有自动换钥。");

            return CreateAndPin(store, campusId, replacement: true);
        }

        var selection = FindUsableBySubject(store, SubjectFor(campusId));
        if (selection.Certificate is not null)
        {
            using (selection.Certificate)
            {
                var privateKey = TryGetPrivateKey(selection.Certificate);
                if (privateKey is not null)
                {
                    PinThumbprint(campusId, selection.Certificate.Thumbprint);
                    return CreateSigningKey(selection.Certificate, privateKey);
                }
            }
        }

        if (selection.HasMatchingCertificates && !replaceUnavailableKey)
            throw ReplacementRequired(selection.Ambiguous
                ? "同一校区存在多个不同的可用签名密钥，无法安全判断学生端信任哪一个。"
                : "找到同一校区的签名证书，但其中没有可用私钥。");

        return CreateAndPin(store, campusId, replacement: selection.HasMatchingCertificates || replaceUnavailableKey);
    }

    [SupportedOSPlatform("windows")]
    public static WebsitePolicySigningKey Open(string campusId)
    {
        EnsureWindows();
        ValidateCampusId(campusId);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);

        var pinnedThumbprint = ReadPinnedThumbprint(campusId);
        if (pinnedThumbprint is not null)
        {
            using var pinned = FindByThumbprint(store, pinnedThumbprint) ??
                throw new CryptographicException("已固定的教师网站策略证书不在当前 Windows 用户证书库中；未签发策略。");
            return CreateSigningKey(pinned, TryGetPrivateKey(pinned) ??
                throw new CryptographicException("已固定的教师网站策略证书私钥不可用；未签发策略。"));
        }

        var selection = FindUsableBySubject(store, SubjectFor(campusId));
        using var certificate = selection.Certificate ??
            throw new CryptographicException(selection.Ambiguous
                ? "同一校区存在多个不同的教师签名密钥；未签发策略。请在生成配置包时明确更换密钥并重新部署学生包。"
                : "找不到该校区可用的教师网站策略签名证书；未签发策略。请先生成学生校区配置包。");
        var privateKey = TryGetPrivateKey(certificate) ??
            throw new CryptographicException("教师网站策略证书私钥不可用；未签发策略。");
        PinThumbprint(campusId, certificate.Thumbprint);
        return CreateSigningKey(certificate, privateKey);
    }

    [SupportedOSPlatform("windows")]
    private static WebsitePolicySigningKey CreateAndPin(X509Store store, string campusId, bool replacement)
    {
        using var rsa = CreatePersistedSigningKey(campusId, replacement);
        var request = new CertificateRequest(SubjectFor(campusId), rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(20));
        if (!created.HasPrivateKey)
            throw new CryptographicException("新建的教师签名证书没有关联私钥；证书未用于生成配置包。");

        store.Add(created);
        using var stored = FindByThumbprint(store, created.Thumbprint) ??
            throw new CryptographicException("教师网站策略证书写入 Windows 用户证书库后无法按指纹读回。");
        var privateKey = TryGetPrivateKey(stored) ??
            throw new CryptographicException("教师网站策略证书已保存，但私钥未能从当前 Windows 用户证书库重新打开；证书未用于生成配置包。");
        PinThumbprint(campusId, stored.Thumbprint);
        return CreateSigningKey(stored, privateKey);
    }

    [SupportedOSPlatform("windows")]
    private static RSA CreatePersistedSigningKey(string campusId, bool replacement)
    {
        var name = KeyNameFor(campusId) + (replacement ? "-" + Guid.NewGuid().ToString("N") : "");
        CngKey key;
        if (CngKey.Exists(name, SigningKeyProvider))
        {
            key = CngKey.Open(name, SigningKeyProvider);
        }
        else
        {
            var parameters = new CngKeyCreationParameters
            {
                Provider = SigningKeyProvider,
                KeyUsage = CngKeyUsages.Signing,
                ExportPolicy = CngExportPolicies.None,
                KeyCreationOptions = CngKeyCreationOptions.None
            };
            parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(SigningKeySize), CngPropertyOptions.None));
            try
            {
                key = CngKey.Create(CngAlgorithm.Rsa, name, parameters);
            }
            catch (CryptographicException) when (CngKey.Exists(name, SigningKeyProvider))
            {
                key = CngKey.Open(name, SigningKeyProvider);
            }
        }

        using (key)
        {
            if (key.AlgorithmGroup != CngAlgorithmGroup.Rsa || key.KeySize != SigningKeySize)
                throw new CryptographicException("当前用户的校区签名密钥与预期算法或位长不一致；未覆盖该密钥。");
            return new RSACng(key);
        }
    }

    private static RSA? TryGetPrivateKey(X509Certificate2 certificate) => certificate.GetRSAPrivateKey();

    private static WebsitePolicySigningKey CreateSigningKey(X509Certificate2 certificate, RSA privateKey)
    {
        try
        {
            using var publicKey = certificate.GetRSAPublicKey() ??
                throw new CryptographicException("教师网站策略证书没有可用公钥。");
            return new WebsitePolicySigningKey(privateKey, publicKey.ExportSubjectPublicKeyInfoPem(), certificate.Thumbprint);
        }
        catch
        {
            privateKey.Dispose();
            throw;
        }
    }

    private static X509Certificate2? FindByThumbprint(X509Store store, string thumbprint)
    {
        var matches = store.Certificates.Cast<X509Certificate2>()
            .Where(certificate => string.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0) return null;
        foreach (var extra in matches.Skip(1)) extra.Dispose();
        return matches[0];
    }

    private static CertificateSelection FindUsableBySubject(X509Store store, string subject)
    {
        var certificates = store.Certificates.Cast<X509Certificate2>()
            .Where(certificate => string.Equals(certificate.Subject, subject, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (certificates.Length == 0) return new(null, false, false);

        var usable = new List<(X509Certificate2 Certificate, string PublicKey)>();
        foreach (var certificate in certificates)
        {
            using var privateKey = TryGetPrivateKey(certificate);
            if (privateKey is null) continue;
            using var publicKey = certificate.GetRSAPublicKey() ??
                throw new CryptographicException("教师网站策略证书没有可用公钥。");
            usable.Add((certificate, Convert.ToHexString(publicKey.ExportSubjectPublicKeyInfo())));
        }

        if (usable.Count == 0)
        {
            foreach (var certificate in certificates) certificate.Dispose();
            return new(null, true, false);
        }

        if (usable.Select(item => item.PublicKey).Distinct(StringComparer.Ordinal).Skip(1).Any())
        {
            foreach (var certificate in certificates) certificate.Dispose();
            return new(null, true, true);
        }

        var selected = usable.OrderByDescending(item => item.Certificate.NotAfter).First().Certificate;
        foreach (var extra in certificates.Where(certificate => !ReferenceEquals(certificate, selected))) extra.Dispose();
        return new(selected, true, false);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadPinnedThumbprint(string campusId)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PolicyRegistryPath(campusId), writable: false);
        return key?.GetValue(ThumbprintValueName) as string;
    }

    [SupportedOSPlatform("windows")]
    private static void PinThumbprint(string campusId, string thumbprint)
    {
        using var key = Registry.CurrentUser.CreateSubKey(PolicyRegistryPath(campusId), writable: true)
                        ?? throw new IOException("无法保存教师网站策略证书指纹；未完成密钥绑定。");
        key.SetValue(ThumbprintValueName, thumbprint, RegistryValueKind.String);
        key.SetValue("CampusId", campusId, RegistryValueKind.String);
    }

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> ReadCampusIds()
    {
        using var root = Registry.CurrentUser.OpenSubKey(@"Software\VeyonCampus\WebsitePolicy");
        if (root is null) return Array.Empty<string>();
        var campuses = new List<string>();
        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key?.GetValue("CampusId") is not string campus ||
                key.GetValue(ThumbprintValueName) is not string) continue;
            try { ValidateCampusId(campus); }
            catch (InvalidDataException) { continue; }
            if (PolicyRegistryPath(campus).EndsWith("\\" + name, StringComparison.Ordinal)) campuses.Add(campus);
        }
        return campuses;
    }

    private static string PolicyRegistryPath(string campusId) =>
        @"Software\VeyonCampus\WebsitePolicy\" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(campusId)));

    private static string KeyNameFor(string campusId) =>
        "VeyonCampus-WebsitePolicy-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(campusId)))[..24];

    private static string SubjectFor(string campusId) =>
        "CN=VeyonCampus Website Policy " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(campusId)))[..24];

    private static WebsitePolicySigningKeyRecoveryRequiredException ReplacementRequired(string detail) =>
        new(detail + "如确认旧学生配置包可以重新部署，请点“确认更换教师签名密钥并继续”；旧证书会保留，新密钥只用于新配置包。已有学生端需重新部署配置包才能信任新公钥。");

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("教师网站策略密钥仅支持 Windows 用户证书库。");
    }

    public static void ValidateCampusId(string campusId)
    {
        if (string.IsNullOrWhiteSpace(campusId) || campusId.Length > 100 ||
            !string.Equals(campusId, campusId.Trim(), StringComparison.Ordinal) ||
            campusId.Any(char.IsControl))
            throw new InvalidDataException("校区名称不能为空或超过 100 个字符，也不能包含首尾空格或控制字符。");
    }

}

/// <summary>Monotonic per-campus revisions stored in the teacher's current-user registry hive.</summary>
public static class WebsitePolicyRevisionStore
{
    [SupportedOSPlatform("windows")]
    public static long Next(string campusId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("网站策略版本存储仅支持 Windows。");
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(campusId)));
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\VeyonCampus\WebsitePolicy\{id}", true)
                        ?? throw new IOException("无法保存网站策略版本号。");
        var previous = key.GetValue("Revision") is long value ? value : 0L;
        var next = Math.Max(previous + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        key.SetValue("Revision", next, RegistryValueKind.QWord);
        return next;
    }
}

public sealed record WebsitePolicyAgentConfig(string CampusId, string PublicKeyPem, string? TelemetryEndpoint = null);

/// <summary>Installs the student-only SYSTEM policy receiver and its LAN firewall rule.</summary>
public static class WebsitePolicyAgentInstaller
{
    private const string ScheduledTaskName = "VeyonCampus-WebsitePolicyAgent";
    private const string RestrictedTaskSecurityDescriptor = "D:P(A;;GA;;;SY)(A;;GA;;;BA)";


    private static string InstalledVersionDirectory =>
        typeof(WebsitePolicyAgentInstaller).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : throw new InvalidOperationException("无法读取网站策略代理版本。");

    public static string InstalledExecutablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VeyonCampus", "WebsitePolicyAgent", InstalledVersionDirectory, "VeyonCampus.Agent.exe");

    public static StepResult VerifyInstalled(PackageContext package)
    {
        const string step = "website-agent-verify";
        if (!OperatingSystem.IsWindows())
            return new(step, ExecutionPlan.Failed, "学生网站策略代理验证仅支持 Windows。" );
        if (package.WebsitePolicyPublicKeyPath is null)
            return new(step, ExecutionPlan.Skipped, "当前学生包没有网站策略公钥；未安装网站代理。" );

        try
        {
            package.VerifyUnchanged();
            var executable = InstalledExecutablePath;
            if (!File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
                return new(step, ExecutionPlan.NeedsReview, $"未找到普通文件形式的独立后台代理：{executable}" );

            var campusHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(package.Campus)))[..24];
            var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "VeyonCampus", "WebsitePolicy", campusHash, "agent-" + campusHash + ".json");
            var config = ReadExistingConfig(configPath);
            if (config is null || !string.Equals(config.CampusId, package.Campus, StringComparison.Ordinal))
                return new(step, ExecutionPlan.NeedsReview, "后台代理配置缺失或校区不匹配。" );
            using var installedKey = RSA.Create();
            installedKey.ImportFromPem(config.PublicKeyPem);
            using var packageKey = RSA.Create();
            packageKey.ImportFromPem(File.ReadAllText(package.WebsitePolicyPublicKeyPath));
            if (!CryptographicOperations.FixedTimeEquals(installedKey.ExportSubjectPublicKeyInfo(),
                    packageKey.ExportSubjectPublicKeyInfo()))
                return new(step, ExecutionPlan.NeedsReview, "后台代理持有的校区公钥与学生配置包不匹配。" );

            var task = Run("schtasks.exe", ["/Query", "/TN", ScheduledTaskName, "/XML"]);
            if (task.ExitCode != 0)
                return new(step, ExecutionPlan.NeedsReview, "未能读回网站代理开机任务；后台持续运行状态未确认。" );
            XDocument xml;
            try { xml = XDocument.Parse(task.Stdout); }
            catch (System.Xml.XmlException)
            { return new(step, ExecutionPlan.NeedsReview, "网站代理计划任务 XML 无法解析。" ); }
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var user = xml.Descendants(ns + "UserId").FirstOrDefault()?.Value;
            var command = xml.Descendants(ns + "Command").FirstOrDefault()?.Value;
            var arguments = xml.Descendants(ns + "Arguments").FirstOrDefault()?.Value ?? "";
            if (user != "S-1-5-18" || !PathEquals(command ?? "", executable) ||
                !arguments.Contains("--website-policy-agent", StringComparison.Ordinal) ||
                !arguments.Contains(Quote(configPath), StringComparison.OrdinalIgnoreCase))
                return new(step, ExecutionPlan.NeedsReview,
                    "网站代理开机任务未确认以 SYSTEM 身份运行独立 Agent；没有建议删除部署工具。" );
            if (!HasRestrictedScheduledTaskAcl(ScheduledTaskName))
                return new(step, ExecutionPlan.NeedsReview,
                    "网站代理任务权限未确认限制为 SYSTEM 和本机管理员；普通用户可能影响任务，不能删除部署工具。" );

            WebsitePolicyFirewall.Verify();

            if (!WaitForAgentHealth(TimeSpan.FromSeconds(2)))
                return new(step, ExecutionPlan.NeedsReview, "SYSTEM 网站代理没有返回本机健康响应。" );

            return new(step, ExecutionPlan.Succeeded,
                $"独立网站代理文件、校区公钥、SYSTEM 开机任务、任务权限、防火墙规则及本机健康响应均已读回；校区 {package.Campus}。" );
        }
        catch (Exception exception) when (exception is COMException or RuntimeBinderException or IOException or UnauthorizedAccessException or
                                          InvalidDataException or CryptographicException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or TimeoutException or HttpRequestException or
                                          OperationCanceledException or System.Security.SecurityException)
        {
            return new(step, ExecutionPlan.NeedsReview, $"网站代理只读验证未完成：{exception.Message}" );
        }
    }

    public static StepResult Install(PackageContext package, PackageResourceSnapshot snapshot)
    {
        const string step = "website-agent";
        if (!OperatingSystem.IsWindows())
            return new(step, ExecutionPlan.Failed, "学生网站策略代理仅支持 Windows。");
        if (package.WebsitePolicyPublicKeyPath is null || snapshot.WebsitePolicyPublicKeyPath is null)
            return new(step, ExecutionPlan.Skipped, "旧版学生配置包没有网站策略公钥；网站访问控制代理未安装。请使用教师端新生成的配置包重新部署。" );

        try
        {
            package.VerifyUnchanged();
            snapshot.VerifyUnchanged();
            if (!IsElevated())
                return new(step, ExecutionPlan.Failed, "安装学生网站策略代理需要管理员权限；没有注册 SYSTEM 任务或防火墙规则。");

            var publicPem = File.ReadAllText(snapshot.WebsitePolicyPublicKeyPath);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicPem);
            var canonicalPem = rsa.ExportSubjectPublicKeyInfoPem();
            var sourceDirectory = Path.Combine(Path.GetFullPath(AppContext.BaseDirectory), "WebsitePolicyAgent");
            var sourceExecutable = Path.Combine(sourceDirectory, "VeyonCampus.Agent.exe");
            if (!File.Exists(sourceExecutable))
                throw new FileNotFoundException("找不到独立的 VeyonCampus.Agent.exe；不能安装后台网站策略代理。", sourceExecutable);

            var commonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            CreateSecureDirectory(Path.Combine(commonApplicationData, "VeyonCampus"));
            var installedDirectory = Path.Combine(commonApplicationData,
                "VeyonCampus", "WebsitePolicyAgent",
                InstalledVersionDirectory);
            if (!PathEquals(sourceDirectory, installedDirectory))
                InstallApplicationFiles(sourceDirectory, installedDirectory);
            else
                AgentFileSecurity.SecureTree(installedDirectory);
            var installedExecutable = Path.Combine(installedDirectory, "VeyonCampus.Agent.exe");
            if (!File.Exists(installedExecutable))
                throw new IOException("安装目录中没有 VeyonCampus.Agent.exe。");

            var campusHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(package.Campus)))[..24];
            var configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "VeyonCampus", "WebsitePolicy", campusHash);
            CreateSecureDirectory(Path.GetDirectoryName(configDirectory)!);
            CreateSecureDirectory(configDirectory);
            AgentFileSecurity.SecureTree(configDirectory, executable: false);
            var configPath = Path.Combine(configDirectory, "agent-" + campusHash + ".json");
            var existing = ReadExistingConfig(configPath);
            if (existing is not null && (existing.CampusId != package.Campus ||
                                         !string.Equals(existing.PublicKeyPem, canonicalPem, StringComparison.Ordinal)))
                throw new IOException("网站策略代理已绑定校区或公钥不同；为避免意外更换信任根，没有覆盖现有配置。");
            var telemetryEndpoint = package.TelemetryEndpoint ?? existing?.TelemetryEndpoint;
            if (!string.IsNullOrWhiteSpace(telemetryEndpoint))
                AnonymousUsageHeartbeat.ValidateEndpoint(telemetryEndpoint);
            var config = new WebsitePolicyAgentConfig(package.Campus, canonicalPem, telemetryEndpoint);
            var configBytes = JsonSerializer.SerializeToUtf8Bytes(config, WebsitePolicyAgent.JsonOptions);
            WriteSecureConfig(configPath, configBytes);

            EnsureUrlReservation();
            EnsureFirewallRule(installedExecutable);
            EnsureScheduledTask(installedExecutable, configPath);
            var startupLogPath = Path.Combine(configDirectory, "agent-startup.log");
            var startupLogOffset = File.Exists(startupLogPath) ? new FileInfo(startupLogPath).Length : 0;
            StartScheduledTask();
            if (!WaitForAgentHealth(TimeSpan.FromSeconds(12)))
                return new(step, ExecutionPlan.NeedsReview,
                    "SYSTEM 代理任务和网络规则已注册，但 Agent 没有返回本机健康响应；网站推送暂不可用。" +
                    ReadNewAgentStartupDiagnostic(startupLogPath, startupLogOffset));
            return new(step, ExecutionPlan.Succeeded,
                $"学生网站策略代理已安装并以 SYSTEM 身份运行；校区 {package.Campus}，监听端口 {WebsitePolicyAgent.Port}，只部署了教师公钥。" );
        }
        catch (Exception exception) when (exception is COMException or RuntimeBinderException or IOException or UnauthorizedAccessException or
                                          InvalidDataException or CryptographicException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or TimeoutException)
        {
            return new(step, ExecutionPlan.NeedsReview,
                $"学生网站策略代理安装或验证未完成：{exception.Message}。请检查已创建的任务、目录和防火墙规则后再重试。" );
        }
    }

    [SupportedOSPlatform("windows")]
    private static void InstallApplicationFiles(string sourceDirectory, string targetDirectory)
    {
        var installationRoot = Path.GetDirectoryName(targetDirectory)
                               ?? throw new InvalidDataException("网站策略代理安装目录无效。" );
        CreateSecureDirectory(installationRoot);
        if ((File.GetAttributes(installationRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("网站策略代理安装根目录是重解析点；拒绝复制 SYSTEM 代理。" );
        SecureDirectory(installationRoot);

        if (Directory.Exists(targetDirectory))
        {
            if ((File.GetAttributes(targetDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("网站策略代理版本目录是重解析点；拒绝覆盖。" );
            // An interrupted earlier deployment may leave this version directory behind.
            // Restore the expected ACL before opening and hashing its files.
            AgentFileSecurity.SecureTree(targetDirectory);
            if (!DirectoryTreesEqual(sourceDirectory, targetDirectory))
                throw new IOException($"程序目录已存在且与当前发布文件不同：{targetDirectory}。为避免覆盖其他版本，请先人工核对并升级。" );
            return;
        }

        var staging = targetDirectory + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            CopyDirectoryWithoutLinks(sourceDirectory, staging);
            AgentFileSecurity.SecureTree(staging);
            if (!DirectoryTreesEqual(sourceDirectory, staging))
                throw new IOException("网站代理暂存文件与发布文件不一致；未注册启动任务。");
            Directory.Move(staging, targetDirectory);
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CopyDirectoryWithoutLinks(string source, string destination)
    {
        var sourceInfo = new DirectoryInfo(source);
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("App 目录包含重解析点；拒绝复制到 SYSTEM 代理目录。" );
        CreateSecureDirectory(destination);
        foreach (var file in EnumerateApplicationFiles(source))
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("App 文件包含重解析点；拒绝复制到 SYSTEM 代理目录。" );
            var relativePath = Path.GetRelativePath(source, file);
            var targetPath = Path.Combine(destination, relativePath);
            CreateSecureDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: false);
            AgentFileSecurity.Secure(targetPath, directory: false, executable: true);
        }
    }

    private static bool DirectoryTreesEqual(string left, string right)
    {
        var leftFiles = EnumerateApplicationFiles(left).Select(path => Path.GetRelativePath(left, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var rightFiles = EnumerateApplicationFiles(right).Select(path => Path.GetRelativePath(right, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!leftFiles.SequenceEqual(rightFiles, StringComparer.OrdinalIgnoreCase)) return false;
        foreach (var relative in leftFiles)
        {
            var leftPath = Path.Combine(left, relative);
            var rightPath = Path.Combine(right, relative);
            if ((File.GetAttributes(leftPath) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(rightPath) & FileAttributes.ReparsePoint) != 0 ||
                new FileInfo(leftPath).Length != new FileInfo(rightPath).Length ||
                !string.Equals(HashFile(leftPath), HashFile(rightPath), StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static IEnumerable<string> EnumerateApplicationFiles(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("App 目录或文件包含重解析点；拒绝复制到 SYSTEM 代理目录。" );
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var child in EnumerateApplicationFiles(path)) yield return child;
                continue;
            }
            if (!IsApplicationFile(path)) continue;
            yield return path;
        }
    }

    private static bool IsApplicationFile(string path)
    {
            var name = Path.GetFileName(path);
            if (name.Equals("campus.json", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("README.md", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("admin.txt", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("-public.pem", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("website-policy-public.pem", StringComparison.OrdinalIgnoreCase)) return false;
            return Path.GetExtension(name).ToLowerInvariant() is ".dll" or ".exe" or ".json" or ".config" or ".dat";
    }

    private static WebsitePolicyAgentConfig? ReadExistingConfig(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize<WebsitePolicyAgentConfig>(stream, WebsitePolicyAgent.JsonOptions)
               ?? throw new InvalidDataException("现有学生网站策略代理配置无效。" );
    }

    [SupportedOSPlatform("windows")]
    private static void WriteSecureConfig(string path, byte[] bytes)
    {
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            SecureFile(tempPath);
            if (File.Exists(path))
            {
                SecureFile(path);
                if (File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                {
                    File.Delete(tempPath);
                    return;
                }
                throw new IOException("拒绝替换已部署的学生网站策略信任配置。" );
            }
            else File.Move(tempPath, path);
            SecureFile(path);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (IOException) { }
        }
    }

    private static void EnsureUrlReservation()
    {
        var url = WebsitePolicyAgent.ListenPrefix;
        var query = Run("netsh.exe", ["http", "show", "urlacl", "url=" + url]);
        if (HasUrlReservation(query.Stdout, url))
        {
            if (HasSystemUrlReservation(query.Stdout, url)) return;
            throw new IOException($"HTTP 监听地址 {url} 已被其他账户保留；为避免改动非本工具的 URL ACL，没有覆盖它。详情：{TruncateDiagnostic(query.Stdout)}" );
        }

        var add = Run("netsh.exe", ["http", "add", "urlacl", "url=" + url, "user=NT AUTHORITY\\SYSTEM"]);
        var check = Run("netsh.exe", ["http", "show", "urlacl", "url=" + url]);
        if (HasSystemUrlReservation(check.Stdout, url)) return;
        if (HasUrlReservation(check.Stdout, url))
            throw new IOException($"HTTP 监听地址 {url} 已被其他账户保留；为避免改动非本工具的 URL ACL，没有覆盖它。详情：{TruncateDiagnostic(check.Stdout)}" );
        throw new IOException($"无法确认 SYSTEM 已获得 HTTP 监听地址 {url} 的权限。{TruncateDiagnostic(add.Stdout + Environment.NewLine + add.Stderr + Environment.NewLine + check.Stdout + Environment.NewLine + check.Stderr)}" );
    }

    private static bool HasUrlReservation(string output, string url) =>
        output.Contains(url, StringComparison.OrdinalIgnoreCase) &&
        Regex.IsMatch(output, @"(?im)^\s*SDDL\s*:");

    private static bool HasSystemUrlReservation(string output, string url) =>
        HasUrlReservation(output, url) &&
        (output.Contains("NT AUTHORITY\\SYSTEM", StringComparison.OrdinalIgnoreCase) ||
         output.Contains("S-1-5-18", StringComparison.OrdinalIgnoreCase));

    [SupportedOSPlatform("windows")]
    private static void EnsureFirewallRule(string executable) =>
        WebsitePolicyFirewall.Ensure(GetPreviousAgentExecutablePaths().Append(executable));

    [SupportedOSPlatform("windows")]
    private static void EnsureScheduledTask(string executable, string configPath)
    {
        var existing = Run("schtasks.exe", ["/Query", "/TN", ScheduledTaskName, "/XML"]);
        if (existing.ExitCode == 0)
        {
            var isCurrentTask = false;
            var isPreviousAgentTask = false;
            try
            {
                var xml = XDocument.Parse(existing.Stdout);
                XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                var user = xml.Descendants(ns + "UserId").FirstOrDefault()?.Value;
                var command = xml.Descendants(ns + "Command").FirstOrDefault()?.Value;
                var arguments = xml.Descendants(ns + "Arguments").FirstOrDefault()?.Value ?? "";
                var recognizedArguments = user == "S-1-5-18" &&
                    arguments.Contains("--website-policy-agent", StringComparison.Ordinal) &&
                    arguments.Contains(Quote(configPath), StringComparison.OrdinalIgnoreCase);
                isCurrentTask = recognizedArguments && PathEquals(command ?? "", executable);
                isPreviousAgentTask = recognizedArguments && GetPreviousAgentExecutablePaths()
                    .Any(path => PathEquals(command ?? "", path));
            }
            catch (System.Xml.XmlException) { }
            if (isCurrentTask)
            {
                ProtectScheduledTaskAcl(ScheduledTaskName);
                return;
            }
            if (!isPreviousAgentTask)
                throw new IOException("同名计划任务已存在但无法确认为本工具上一版本的 SYSTEM 网站代理；没有覆盖该任务。" );

            StopRunningScheduledTask(ScheduledTaskName);
        }

        var commandLine = Quote(executable) + " --website-policy-agent " + Quote(configPath);
        var created = Run("schtasks.exe", ["/Create", "/TN", ScheduledTaskName, "/SC", "ONSTART", "/RU", "SYSTEM",
            "/RL", "HIGHEST", "/TR", commandLine, "/F"]);
        if (created.ExitCode != 0) throw new IOException("无法注册 SYSTEM 开机代理任务。" );
        ProtectScheduledTaskAcl(ScheduledTaskName);
    }

    private static string[] GetPreviousAgentExecutablePaths()
    {
        var commonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var roots = new[]
        {
            Path.Combine(commonApplicationData, "VeyonCampus", "WebsitePolicyAgent"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VeyonCampus",
                "WebsitePolicyAgent")
        };
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            roots = roots.Append(Path.Combine(programFilesX86, "VeyonCampus", "WebsitePolicyAgent")).ToArray();

        var paths = new List<string>();
        foreach (var versionRoot in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(versionRoot) ||
                (File.GetAttributes(versionRoot) & FileAttributes.ReparsePoint) != 0) continue;
            paths.Add(Path.Combine(versionRoot, "VeyonCampus.Agent.exe"));
            foreach (var directory in Directory.EnumerateDirectories(versionRoot, "*", SearchOption.TopDirectoryOnly))
            {
                var version = Path.GetFileName(directory);
                if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant) ||
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                paths.Add(Path.Combine(directory, "VeyonCampus.Agent.exe"));
            }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string TruncateDiagnostic(string text)
    {
        text = text.Trim();
        return text.Length > 800 ? text[..800] + "…" : text;
    }

    [SupportedOSPlatform("windows")]
    private static void StopRunningScheduledTask(string taskName)
    {
        var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
        if (schedulerType is null) throw new IOException("无法连接任务计划程序以升级旧版网站代理。" );
        object? scheduler = null;
        object? folder = null;
        object? task = null;
        try
        {
            scheduler = Activator.CreateInstance(schedulerType)
                        ?? throw new IOException("无法创建任务计划程序 COM 对象。" );
            ((dynamic)scheduler).Connect();
            folder = ((dynamic)scheduler).GetFolder("\\");
            task = ((dynamic)folder).GetTask("\\" + taskName);
            var state = Convert.ToInt32((object)((dynamic)task).State,
                System.Globalization.CultureInfo.InvariantCulture);
            if (state != 4) return; // TASK_STATE_RUNNING

            ((dynamic)task).Stop(0);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                state = Convert.ToInt32((object)((dynamic)task).State,
                    System.Globalization.CultureInfo.InvariantCulture);
                if (state != 4) return;
                Thread.Sleep(100);
            }
            throw new IOException("旧版网站代理任务仍在运行；没有替换其启动项。" );
        }
        catch (Exception exception) when (exception is COMException or RuntimeBinderException or
                                          InvalidComObjectException or MemberAccessException)
        {
            throw new IOException("无法安全停止本工具上一版本的网站代理任务。", exception);
        }
        finally
        {
            ReleaseComObject(task);
            ReleaseComObject(folder);
            ReleaseComObject(scheduler);
        }
    }

    private static void StartScheduledTask()
    {
        var started = Run("schtasks.exe", ["/Run", "/TN", ScheduledTaskName]);
        if (started.ExitCode != 0) throw new IOException("SYSTEM 网站代理任务已注册，但无法立即启动。" );
    }

    /// <summary>Returns true only when a task ACL is protected and grants full access solely to SYSTEM and local administrators.</summary>
    public static bool IsRestrictedTaskSecurityDescriptor(string securityDescriptor)
    {
        if (string.IsNullOrWhiteSpace(securityDescriptor)) return false;
        var descriptor = Regex.Match(securityDescriptor,
            @"^O:(?<owner>SY|BA)(?:G:(?:SY|BA|S-\d+(?:-\d+)+))?D:(?<daclFlags>P(?:AI|AR)?)(?<aces>(?:\([^()]*\))*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!descriptor.Success) return false;
        var aces = Regex.Matches(descriptor.Groups["aces"].Value,
            @"\((?<type>[^;]*);(?<flags>[^;]*);(?<rights>[^;]*);(?<object>[^;]*);(?<inherited>[^;]*);(?<sid>[^;]*)\)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (aces.Count != 2 || string.Concat(aces.Select(match => match.Value)) != descriptor.Groups["aces"].Value)
            return false;
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match ace in aces)
        {
            var sid = ace.Groups["sid"].Value.ToUpperInvariant();
            var rights = ace.Groups["rights"].Value.ToUpperInvariant();
            if (ace.Groups["type"].Value.ToUpperInvariant() != "A" ||
                ace.Groups["flags"].Value.Length != 0 ||
                ace.Groups["object"].Value.Length != 0 ||
                ace.Groups["inherited"].Value.Length != 0 ||
                rights is not ("GA" or "FA" or "0X001F01FF") ||
                sid is not ("SY" or "S-1-5-18" or "BA" or "S-1-5-32-544"))
                return false;
            var identity = sid is "SY" or "S-1-5-18" ? "SY" : "BA";
            if (!identities.Add(identity)) return false;
        }
        return identities.SetEquals(["SY", "BA"]);
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectScheduledTaskAcl(string taskName)
    {
        var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
        if (schedulerType is null) throw new IOException("无法连接 Windows 任务计划程序 COM 服务。");
        object? scheduler = null;
        object? folder = null;
        object? task = null;
        var operation = "连接任务计划程序";
        try
        {
            scheduler = Activator.CreateInstance(schedulerType)
                        ?? throw new IOException("无法创建任务计划程序 COM 对象。");
            ((dynamic)scheduler).Connect();
            folder = ((dynamic)scheduler).GetFolder("\\");
            operation = "读取网站代理任务";
            task = ((dynamic)folder).GetTask("\\" + taskName);
            operation = "设置受保护的任务访问控制表";
            ((dynamic)task).SetSecurityDescriptor(RestrictedTaskSecurityDescriptor, 0x10);
            operation = "读回受保护的任务访问控制表";
            var actual = (string)((dynamic)task).GetSecurityDescriptor(0x7);
            if (!IsRestrictedTaskSecurityDescriptor(actual))
                throw new IOException("任务计划程序读回的访问控制表仍允许普通用户访问。");
        }
        catch (Exception exception) when (exception is COMException or RuntimeBinderException or
                                          InvalidComObjectException or MemberAccessException)
        {
            var cause = exception.GetBaseException();
            throw new IOException(
                $"无法{operation}（{cause.GetType().Name}，HRESULT 0x{unchecked((uint)cause.HResult):X8}）：{cause.Message}",
                exception);
        }
        finally
        {
            ReleaseComObject(task);
            ReleaseComObject(folder);
            ReleaseComObject(scheduler);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasRestrictedScheduledTaskAcl(string taskName)
    {
        var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
        if (schedulerType is null) return false;
        object? scheduler = null;
        object? folder = null;
        object? task = null;
        try
        {
            scheduler = Activator.CreateInstance(schedulerType);
            if (scheduler is null) return false;
            ((dynamic)scheduler).Connect();
            folder = ((dynamic)scheduler).GetFolder("\\");
            task = ((dynamic)folder).GetTask("\\" + taskName);
            var actual = (string)((dynamic)task).GetSecurityDescriptor(0x7);
            return IsRestrictedTaskSecurityDescriptor(actual);
        }
        catch (Exception exception) when (exception is COMException or RuntimeBinderException or
                                          InvalidComObjectException or MemberAccessException)
        {
            return false;
        }
        finally
        {
            ReleaseComObject(task);
            ReleaseComObject(folder);
            ReleaseComObject(scheduler);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private static bool WaitForAgentHealth(TimeSpan timeout)
    {
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) };
        using var deadline = new CancellationTokenSource(timeout);
        var address = "http://127.0.0.1:" + WebsitePolicyAgent.Port + "/health";
        while (!deadline.IsCancellationRequested)
        {
            try
            {
                using var response = client.GetAsync(address, deadline.Token).GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!deadline.IsCancellationRequested) { }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return false; }
            if (deadline.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(500))) return false;
        }
        return false;
    }

    private static string ReadNewAgentStartupDiagnostic(string path, long previousLength)
    {
        var diagnostics = new List<string>();
        try
        {
            if (!File.Exists(path))
            {
                diagnostics.Add($"没有生成 Agent 启动异常日志（{path}）。");
            }
            else
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length <= previousLength)
                {
                    diagnostics.Add("没有新增 Agent 启动异常日志。");
                }
                else
                {
                    stream.Position = stream.Length > previousLength ? previousLength : 0;
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                        bufferSize: 1024, leaveOpen: false);
                    var detail = reader.ReadToEnd().Trim();
                    if (detail.Length > 1800) detail = detail[^1800..];
                    if (!string.IsNullOrWhiteSpace(detail)) diagnostics.Add($"Agent 启动异常：{detail}");
                }
            }
        }
        catch (Exception logReadException) when (logReadException is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add($"读取 Agent 启动日志失败：{logReadException.Message}");
        }

        try
        {
            var task = Run("schtasks.exe", ["/Query", "/TN", ScheduledTaskName, "/V", "/FO", "LIST"]);
            var taskOutput = (task.Stdout + Environment.NewLine + task.Stderr).Trim();
            var taskLines = taskOutput.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => Regex.IsMatch(line,
                    @"(?i)(task name|last run|last result|status|任务名|上次运行|上次结果|计划任务状态|任务计划状态|要运行的任务)"))
                .Take(8).ToArray();
            diagnostics.Add(task.ExitCode == 0 && taskLines.Length > 0
                ? "计划任务读回：" + string.Join("；", taskLines)
                : "计划任务读回：" + (taskOutput.Length > 700 ? taskOutput[..700] + "…" : taskOutput));
        }
        catch (Exception taskReadException) when (taskReadException is IOException or TimeoutException or
                                                   System.ComponentModel.Win32Exception)
        {
            diagnostics.Add($"读取计划任务结果失败：{taskReadException.Message}");
        }

        return " 启动诊断：" + string.Join(" ", diagnostics);
    }

    [SupportedOSPlatform("windows")]
    private static void SecureDirectory(string path) => AgentFileSecurity.Secure(path, directory: true);
    [SupportedOSPlatform("windows")]
    private static void SecureFile(string path) => AgentFileSecurity.Secure(path, directory: false);

    [SupportedOSPlatform("windows")]
    private static void CreateSecureDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            var parent = Path.GetDirectoryName(path)
                         ?? throw new InvalidDataException("网站代理目录无效。");
            if (!Directory.Exists(parent)) CreateSecureDirectory(parent);
            AgentFileSecurity.RejectLinks(parent);
            Directory.CreateDirectory(path);
        }
        SecureDirectory(path);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (System.Security.SecurityException) { return false; }
    }

    private static ProcessRunner Run(string executable, IReadOnlyList<string> arguments)
    {
        var runner = new ProcessRunner();
        runner.Run(executable, arguments, Environment.GetFolderPath(Environment.SpecialFolder.System), TimeSpan.FromSeconds(30));
        return runner;
    }

    private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    public static void ReportAgentStartupFailure(string configPath, Exception exception)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            if (directory is null || !Directory.Exists(directory)) return;
            var logPath = Path.Combine(directory, "agent-startup.log");
            var details = exception.ToString();
            if (details.Length > 12000) details = details[..12000] + "…";
            AppendBoundedAgentLog(logPath,
                $"{DateTimeOffset.UtcNow:O} agent-startup-failed{Environment.NewLine}{details}{Environment.NewLine}");
        }
        catch (Exception logWriteException) when (logWriteException is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    public static void ReportAgentExpirationFailure(string configPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            if (directory is null || !Directory.Exists(directory)) return;
            var logPath = Path.Combine(directory, "agent-runtime.log");
            AppendBoundedAgentLog(logPath,
                $"{DateTimeOffset.UtcNow:O} policy-expiration-cleanup-failed{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static bool PathEquals(string left, string right)
    {
        static string RemoveWrappingQuotes(string value)
        {
            value = value.Trim();
            return value.Length >= 2 && value[0] == '"' && value[^1] == '"'
                ? value[1..^1]
                : value;
        }

        return string.Equals(Path.GetFullPath(RemoveWrappingQuotes(left)),
            Path.GetFullPath(RemoveWrappingQuotes(right)), StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendBoundedAgentLog(string path, string line)
    {
        const long maximumBytes = 64 * 1024;
        var contents = new UTF8Encoding(false);
        if (File.Exists(path) && new FileInfo(path).Length + contents.GetByteCount(line) > maximumBytes)
            File.WriteAllText(path, line, contents);
        else File.AppendAllText(path, line, contents);
    }
}

/// <summary>Pushes a signed policy to selected student computers over the classroom LAN.</summary>
public static class WebsitePolicyTransport
{
    public static IReadOnlyList<string> NormalizeTargets(IEnumerable<string> rawTargets)
    {
        ArgumentNullException.ThrowIfNull(rawTargets);
        var targets = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in rawTargets)
        {
            var target = raw?.Trim();
            if (string.IsNullOrEmpty(target)) continue;
            if (target.Length > 253 || target.Any(char.IsControl) || target.Contains('/') || target.Contains('\\') ||
                target.Contains('@') || target.Contains('#') || target.Contains('?') ||
                (Uri.CheckHostName(target) is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)))
                throw new InvalidDataException($"目标必须是电脑名或 IP 地址，不能是 URL：{target}");
            targets.Add(target);
            if (targets.Count > 150) throw new InvalidDataException("一次最多向 150 台学生电脑推送。");
        }
        if (targets.Count == 0) throw new InvalidDataException("请至少填写一个学生电脑名或 IP 地址。");
        return Array.AsReadOnly(targets.ToArray());
    }

    public static async Task<IReadOnlyList<WebsitePolicyPushResult>> PushAsync(IEnumerable<string> targets,
        string signedPolicyJson, CancellationToken cancellationToken = default)
    {
        var validated = NormalizeTargets(targets);
        ArgumentException.ThrowIfNullOrWhiteSpace(signedPolicyJson);
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        using var limit = new SemaphoreSlim(16, 16);
        var tasks = validated.Select(async target =>
        {
            await limit.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var uri = new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port,
                    WebsitePolicyAgent.PolicyPath).Uri;
                using var content = new StringContent(signedPolicyJson, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
                var resultText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return new WebsitePolicyPushResult(target, response.IsSuccessStatusCode,
                    response.IsSuccessStatusCode ? resultText : $"HTTP {(int)response.StatusCode}：{resultText}",
                    NeedsReview: (int)response.StatusCode >= 500);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException)
            {
                var endpoint = $"{target}:{WebsitePolicyAgent.Port}";
                return new WebsitePolicyPushResult(target, false, exception is TaskCanceledException
                    ? $"连接 {endpoint} 超时。请确认学生端代理已通过部署后核验、电脑名可解析，且双方处于允许通信的局域网。"
                    : $"无法连接 {endpoint}：{exception.Message} 请检查电脑名/IP、学生端代理以及当前网络的防火墙规则。", NeedsReview: true);
            }
            finally { limit.Release(); }
        }).ToArray();
        return Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false));
    }
}

public sealed record WebsitePolicyPushResult(string Target, bool Succeeded, string Detail, bool NeedsReview = false);

public sealed record WebsitePolicyPushHistoryEntry(DateTimeOffset CreatedUtc, string CampusId, long Revision,
    WebsitePolicyMode Mode, DateTimeOffset? ExpiresUtc, IReadOnlyList<WebsitePolicyPushResult> Results);

/// <summary>Stores the latest teacher push outcomes locally without the signed payload or domain list.</summary>
public static class WebsitePolicyPushHistoryStore
{
    public const int MaximumRuns = 50;
    private const long MaximumHistoryBytes = 4 * 1024 * 1024;
    private const int MaximumDetailCharacters = 512;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeyonCampus", "WebsitePolicy");

    public static WebsitePolicyPushHistoryEntry? ReadLatest(string? directory = null)
    {
        lock (Gate)
        {
            var entries = ReadEntries(GetHistoryPath(directory));
            return entries.Count == 0 ? null : entries[^1];
        }
    }

    public static void Append(WebsitePolicyPushHistoryEntry entry, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (Gate)
        {
            var path = GetHistoryPath(directory);
            var parent = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(parent);
            var entries = ReadEntries(path);
            entries.Add(ValidateAndSanitize(entry));
            if (entries.Count > MaximumRuns) entries.RemoveRange(0, entries.Count - MaximumRuns);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions);
            if (bytes.Length > MaximumHistoryBytes)
                throw new IOException("本机策略推送记录超过大小限制；本次推送结果仍可见，但没有覆盖已有记录。" );
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
            }
        }
    }

    private static string GetHistoryPath(string? directory) => Path.Combine(
        Path.GetFullPath(directory ?? DefaultDirectory), "push-history.json");

    private static List<WebsitePolicyPushHistoryEntry> ReadEntries(string path)
    {
        if (!File.Exists(path)) return new List<WebsitePolicyPushHistoryEntry>();
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaximumHistoryBytes ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("本机网站策略推送记录为空、过大或包含链接；没有覆盖记录。" );
        var entries = JsonSerializer.Deserialize<List<WebsitePolicyPushHistoryEntry>>(File.ReadAllBytes(path), JsonOptions)
                      ?? throw new InvalidDataException("本机网站策略推送记录格式无效。" );
        if (entries.Count > MaximumRuns || entries.Any(entry => entry is null))
            throw new InvalidDataException("本机网站策略推送记录数量或内容无效。" );
        return entries.Select(ValidateAndSanitize).ToList();
    }

    private static WebsitePolicyPushHistoryEntry ValidateAndSanitize(WebsitePolicyPushHistoryEntry entry)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(entry.CampusId);
        if (entry.Revision <= 0 || !Enum.IsDefined(entry.Mode) || entry.Results is null ||
            entry.Results.Count is 0 or > 150 || entry.Results.Any(result => result is null ||
                string.IsNullOrWhiteSpace(result.Target) || result.Detail is null))
            throw new InvalidDataException("本机网站策略推送记录字段无效。" );
        var normalizedTargets = WebsitePolicyTransport.NormalizeTargets(entry.Results.Select(result => result.Target));
        if (normalizedTargets.Count != entry.Results.Count)
            throw new InvalidDataException("本机网站策略推送记录含重复设备。" );
        var safeResults = entry.Results.Select(result => result with
        {
            Detail = new string((result.Detail ?? "").Where(character => !char.IsControl(character)).Take(MaximumDetailCharacters).ToArray())
        }).ToArray();
        return entry with { CreatedUtc = entry.CreatedUtc.ToUniversalTime(), Results = Array.AsReadOnly(safeResults) };
    }
}

/// <summary>Long-running SYSTEM HTTP receiver. It has the campus public key, never a teacher private key.</summary>
public sealed class WebsitePolicyAgent
{
    public const int Port = 39174;
    public const string ListenPrefix = "http://+:39174/";
    public const string PolicyPath = "/v1/policy";
    public const string PolicyAppliedAcknowledgement =
        "policy applied; 策略已写入 Edge/Chrome 机器策略。若 Edge 或 Chrome 在推送前已打开，" +
        "且受限网站仍可访问，请在对应浏览器地址栏打开 edge://restart 或 chrome://restart 后再验证；" +
        "代理不会强制关闭浏览器。";
    private static readonly SemaphoreSlim ApplyGate = new(1, 1);
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    [SupportedOSPlatform("windows")]
    public static async Task RunAsync(string configPath, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("学生网站策略代理仅支持 Windows。");
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            if (identity.User?.Value != "S-1-5-18")
                throw new UnauthorizedAccessException("学生网站策略代理只能由已注册的 SYSTEM 任务运行。");
        var config = JsonSerializer.Deserialize<WebsitePolicyAgentConfig>(await File.ReadAllBytesAsync(configPath, cancellationToken), JsonOptions)
                     ?? throw new InvalidDataException("学生网站策略代理配置为空。");
        WebsitePolicySigningKeyStore.ValidateCampusId(config.CampusId);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(config.PublicKeyPem);
        if (rsa.KeySize is < 2048 or > 4096) throw new InvalidDataException("学生网站策略公钥位长无效。");
        if (!string.IsNullOrWhiteSpace(config.TelemetryEndpoint))
        {
            AnonymousUsageHeartbeat.ValidateEndpoint(config.TelemetryEndpoint);
            var directory = Path.GetDirectoryName(Path.GetFullPath(configPath))
                            ?? throw new InvalidDataException("学生网站策略代理配置目录无效。");
            _ = AnonymousUsageHeartbeat.RunAsync(config.TelemetryEndpoint,
                Path.Combine(directory, "usage-installation-id"), cancellationToken);
        }

        await TryExpirePolicyAsync(configPath, cancellationToken).ConfigureAwait(false);
        using var listener = new HttpListener();
        listener.Prefixes.Add(ListenPrefix);
        listener.Start();
        using var expiryTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        var nextRequest = listener.GetContextAsync();
        var nextExpiryCheck = expiryTimer.WaitForNextTickAsync(cancellationToken).AsTask();
        while (!cancellationToken.IsCancellationRequested)
        {
            var completed = await Task.WhenAny(nextRequest, nextExpiryCheck).ConfigureAwait(false);
            if (completed == nextRequest)
            {
                var context = await nextRequest.ConfigureAwait(false);
                nextRequest = listener.GetContextAsync();
                _ = HandleAsync(context, config, cancellationToken);
                continue;
            }

            try
            {
                if (!await nextExpiryCheck.ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            await TryExpirePolicyAsync(configPath, cancellationToken).ConfigureAwait(false);
            nextExpiryCheck = expiryTimer.WaitForNextTickAsync(cancellationToken).AsTask();
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task TryExpirePolicyAsync(string configPath, CancellationToken cancellationToken)
    {
        await ApplyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { WebsitePolicyRegistryStore.ExpireIfDue(DateTimeOffset.UtcNow); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException or InvalidOperationException)
        {
            WebsitePolicyAgentInstaller.ReportAgentExpirationFailure(configPath);
        }
        finally { ApplyGate.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private static async Task HandleAsync(HttpListenerContext context, WebsitePolicyAgentConfig config,
        CancellationToken cancellationToken)
    {
        using var response = context.Response;
        try
        {
            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/health")
            {
                await RespondAsync(response, 200, "ready", cancellationToken).ConfigureAwait(false);
                return;
            }
            if (context.Request.HttpMethod != "POST" || context.Request.Url?.AbsolutePath != PolicyPath)
            {
                await RespondAsync(response, 404, "not found", cancellationToken).ConfigureAwait(false);
                return;
            }
            if (context.Request.ContentLength64 is > WebsitePolicyCompiler.MaximumPayloadBytes * 2)
            {
                await RespondAsync(response, 413, "policy too large", cancellationToken).ConfigureAwait(false);
                return;
            }
            var body = await ReadBoundedAsync(context.Request.InputStream,
                WebsitePolicyCompiler.MaximumPayloadBytes * 2, cancellationToken).ConfigureAwait(false);
            await ApplyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var currentRevision = WebsitePolicyRegistryStore.ReadRevision(config.CampusId);
                var policy = WebsitePolicyCryptography.Verify(body, config.PublicKeyPem, config.CampusId, currentRevision);
                WebsitePolicyRegistryStore.Apply(policy);
            }
            finally { ApplyGate.Release(); }
            await RespondAsync(response, 200, PolicyAppliedAcknowledgement, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            await RespondAsync(response, 409, exception.Message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException or InvalidOperationException or
                                          CryptographicException)
        {
            await RespondAsync(response, 500, "policy could not be applied; check browser policy conflicts and SYSTEM permissions",
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try { response.Close(); } catch (ObjectDisposedException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes) throw new InvalidDataException("网站策略消息过大。");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task RespondAsync(HttpListenerResponse response, int statusCode, string text,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.StatusCode = statusCode;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Writes policy-owned Edge and Chrome HKLM values and refuses pre-existing GPO policy.</summary>
[SupportedOSPlatform("windows")]
public static class WebsitePolicyRegistryStore
{
    private sealed record Browser(string Name, string PolicyKey);
    private static readonly Browser[] Browsers =
    [
        new("Edge", @"SOFTWARE\Policies\Microsoft\Edge"),
        new("Chrome", @"SOFTWARE\Policies\Google\Chrome")
    ];
    private const string AgentKey = @"SOFTWARE\VeyonCampus\WebsitePolicy";

    [SupportedOSPlatform("windows")]
    public static long ReadRevision(string campusId)
    {
        EnsureWindows();
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(AgentKey, writable: false);
        var storedCampus = key?.GetValue("CampusId") as string;
        if (storedCampus is not null && !string.Equals(storedCampus, campusId, StringComparison.Ordinal))
            throw new InvalidDataException("学生网站策略注册表状态属于其他校区；拒绝跨校区应用策略。");
        return key?.GetValue("Revision") is long revision ? revision : 0L;
    }

    [SupportedOSPlatform("windows")]
    public static bool ExpireIfDue(DateTimeOffset nowUtc)
    {
        EnsureWindows();
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var agentKey = root.OpenSubKey(AgentKey, writable: true);
        if (agentKey is null) return false;
        var rawExpires = agentKey.GetValue("ExpiresUtc", null);
        if (rawExpires is null) return false;
        if (rawExpires is not string expiresText || string.IsNullOrWhiteSpace(expiresText))
            throw new InvalidDataException("学生网站策略到期时间记录类型无效；没有清除浏览器策略。" );
        if (!DateTimeOffset.TryParseExact(expiresText, "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var expiresUtc))
            throw new InvalidDataException("学生网站策略到期时间记录无效；没有清除浏览器策略。" );
        if (expiresUtc > nowUtc.ToUniversalTime()) return false;
        var mode = agentKey.GetValue("Mode") as string;
        if (mode is not (nameof(WebsitePolicyMode.Blocklist) or nameof(WebsitePolicyMode.Allowlist)))
            throw new InvalidDataException("已到期的网站策略模式无法确认；没有清除浏览器策略。" );

        var plans = new List<(RegistryKey Policy, RegistryKey Managed)>();
        try
        {
            foreach (var browser in Browsers)
            {
                var policy = root.OpenSubKey(browser.PolicyKey, writable: true)
                             ?? throw new IOException($"无法读取 {browser.Name} 策略；到期清理未执行。" );
                var managed = root.OpenSubKey(AgentKey + @"\Managed\" + browser.Name, writable: true);
                if (managed is null)
                {
                    policy.Dispose();
                    throw new IOException($"无法读取 {browser.Name} 策略所有权；到期清理未执行。" );
                }
                plans.Add((policy, managed));
                var initialized = managed.GetValue("Initialized") is int initializedValue && initializedValue == 1;
                var currentBlock = ReadBrowserList(policy, "URLBlocklist", out var blockExists);
                var currentAllow = ReadBrowserList(policy, "URLAllowlist", out var allowExists);
                var ownedBlock = ReadManagedList(managed, "URLBlocklist");
                var ownedAllow = ReadManagedList(managed, "URLAllowlist");
                if (!initialized || !ListsEqual(currentBlock, ownedBlock) || !ListsEqual(currentAllow, ownedAllow) ||
                    blockExists != (ownedBlock.Length > 0) || allowExists != (ownedAllow.Length > 0))
                    throw new IOException($"检测到 {browser.Name} 网站策略被外部修改；到期清理不会删除外部规则。" );
            }

            foreach (var (policy, managed) in plans)
            {
                WriteBrowserList(policy, "URLBlocklist", Array.Empty<string>());
                WriteBrowserList(policy, "URLAllowlist", Array.Empty<string>());
                WriteManagedList(managed, "URLBlocklist", Array.Empty<string>());
                WriteManagedList(managed, "URLAllowlist", Array.Empty<string>());
            }
            agentKey.SetValue("Mode", WebsitePolicyMode.Disabled.ToString(), RegistryValueKind.String);
            agentKey.DeleteValue("ExpiresUtc", throwOnMissingValue: false);
            agentKey.SetValue("ExpiredUtc", nowUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                RegistryValueKind.String);
            return true;
        }
        finally
        {
            foreach (var (policy, managed) in plans) { policy.Dispose(); managed.Dispose(); }
        }
    }

    [SupportedOSPlatform("windows")]
    public static void Apply(WebsitePolicyDocument document)
    {
        EnsureWindows();
        var compiled = WebsitePolicyCompiler.Compile(document);
        if (document.ExpiresUtc is { } expiresUtc && expiresUtc <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("网站限制策略已到期；没有修改浏览器策略。" );
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var agentKey = root.CreateSubKey(AgentKey, writable: true)
                             ?? throw new IOException("无法打开网站策略代理状态注册表项。");
        var storedCampus = agentKey.GetValue("CampusId") as string;
        if (storedCampus is not null && !string.Equals(storedCampus, document.CampusId, StringComparison.Ordinal))
            throw new InvalidDataException("学生网站策略代理已绑定到其他校区；拒绝覆盖。");
        var currentRevision = agentKey.GetValue("Revision") is long revision ? revision : 0L;
        if (document.Revision <= currentRevision)
            throw new InvalidDataException("网站策略版本已过期或重复；学生端拒绝重放。");

        var plans = new List<(Browser Browser, RegistryKey Policy, RegistryKey Managed, string[] OldBlock,
            string[] OldAllow, string[] NewBlock, string[] NewAllow, bool Initialized)>();
        try
        {
            foreach (var browser in Browsers)
            {
                var policy = root.CreateSubKey(browser.PolicyKey, writable: true)
                             ?? throw new IOException($"无法打开 {browser.Name} 机器策略注册表项。");
                var managed = root.CreateSubKey(AgentKey + @"\Managed\" + browser.Name, writable: true)
                              ?? throw new IOException($"无法打开 {browser.Name} 策略所有权注册表项。");
                var initialized = managed.GetValue("Initialized") is int initializedValue && initializedValue == 1;
                var oldBlock = ReadBrowserList(policy, "URLBlocklist", out var oldBlockExists);
                var oldAllow = ReadBrowserList(policy, "URLAllowlist", out var oldAllowExists);
                var ownedBlock = ReadManagedList(managed, "URLBlocklist");
                var ownedAllow = ReadManagedList(managed, "URLAllowlist");
                if (initialized)
                {
                    if (!ListsEqual(oldBlock, ownedBlock) || !ListsEqual(oldAllow, ownedAllow) ||
                        oldBlockExists != (ownedBlock.Length > 0) || oldAllowExists != (ownedAllow.Length > 0))
                    {
                        managed.Dispose();
                        policy.Dispose();
                        throw new IOException($"检测到 {browser.Name} 网站策略被组策略或管理员手动修改；没有覆盖外部策略。" );
                    }
                }
                else if (oldBlockExists || oldAllowExists)
                {
                    managed.Dispose();
                    policy.Dispose();
                    throw new IOException($"{browser.Name} 已有 URLBlocklist/URLAllowlist 策略；为避免覆盖组策略，代理拒绝接管。" );
                }
                plans.Add((browser, policy, managed, oldBlock, oldAllow,
                    compiled.Blocklist.ToArray(), compiled.Allowlist.ToArray(), initialized));
            }

            // Registry conflict checks for both browsers complete before the first policy write.
            foreach (var item in plans)
            {
                WriteBrowserList(item.Policy, "URLBlocklist", item.NewBlock);
                WriteBrowserList(item.Policy, "URLAllowlist", item.NewAllow);
                WriteManagedList(item.Managed, "URLBlocklist", item.NewBlock);
                WriteManagedList(item.Managed, "URLAllowlist", item.NewAllow);
                item.Managed.SetValue("Initialized", 1, RegistryValueKind.DWord);
            }
            agentKey.SetValue("CampusId", document.CampusId, RegistryValueKind.String);
            agentKey.SetValue("Revision", document.Revision, RegistryValueKind.QWord);
            agentKey.SetValue("Mode", document.Mode.ToString(), RegistryValueKind.String);
            if (document.ExpiresUtc is { } expires)
                agentKey.SetValue("ExpiresUtc", expires.ToUniversalTime().ToString("O",
                    System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
            else agentKey.DeleteValue("ExpiresUtc", throwOnMissingValue: false);
            agentKey.DeleteValue("ExpiredUtc", throwOnMissingValue: false);
        }
        finally
        {
            foreach (var plan in plans) { plan.Policy.Dispose(); plan.Managed.Dispose(); }
        }
    }

    private static string[] ReadManagedList(RegistryKey key, string name)
    {
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value switch
        {
            null => Array.Empty<string>(),
            string[] strings => strings,
            string text => [text],
            _ => throw new IOException($"注册表策略 {name} 格式不支持；没有修改。" )
        };
    }

    private static string[] ReadBrowserList(RegistryKey policyRoot, string name, out bool exists)
    {
        using var listKey = policyRoot.OpenSubKey(name, writable: false);
        exists = listKey is not null;
        if (listKey is null) return Array.Empty<string>();
        if (listKey.SubKeyCount != 0)
            throw new IOException($"浏览器的 {name} 策略包含未知子项；没有修改。" );
        var names = listKey.GetValueNames();
        var indexed = new List<(int Index, string Value)>();
        foreach (var valueName in names)
        {
            if (!int.TryParse(valueName, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var index) || index <= 0 ||
                listKey.GetValueKind(valueName) != RegistryValueKind.String ||
                listKey.GetValue(valueName) is not string value)
                throw new IOException($"浏览器的 {name} 策略格式不受支持；没有修改。" );
            indexed.Add((index, value));
        }
        indexed.Sort((left, right) => left.Index.CompareTo(right.Index));
        if (indexed.Select(item => item.Index).Distinct().Count() != indexed.Count)
            throw new IOException($"浏览器的 {name} 策略编号重复；没有修改。" );
        return indexed.Select(item => item.Value).ToArray();
    }

    private static void WriteBrowserList(RegistryKey policyRoot, string name, string[] values)
    {
        if (values.Length == 0)
        {
            policyRoot.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
            return;
        }
        using var listKey = policyRoot.CreateSubKey(name, writable: true)
                            ?? throw new IOException($"无法创建浏览器 {name} 策略项。");
        foreach (var oldName in listKey.GetValueNames()) listKey.DeleteValue(oldName, throwOnMissingValue: false);
        for (var index = 0; index < values.Length; index++)
            listKey.SetValue((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), values[index], RegistryValueKind.String);
    }

    private static void WriteManagedList(RegistryKey key, string name, string[] values)
    {
        if (values.Length == 0) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, values, RegistryValueKind.MultiString);
    }

    private static bool ListsEqual(string[] left, string[] right) =>
        left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("浏览器机器策略仅支持 Windows。");
    }
}
