using System.Net;
using System.Reflection;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            if (StudentSystemPolicySigningKeyStore.IsInternalNamespace(campus) ||
                ApplicationPolicySigningKeyStore.IsInternalNamespace(campus)) continue;
            if (PolicyRegistryPath(campus).EndsWith("\\" + name, StringComparison.Ordinal)) campuses.Add(campus);
        }
        return campuses;
    }

    private static string PolicyRegistryPath(string campusId) =>
        @"Software\VeyonCampus\WebsitePolicy\" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(campusId)));

    private static string KeyNameFor(string campusId) =>
        "VeyonCampus-WebsitePolicy-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(campusId)))[..24];

    private static string SubjectFor(string campusId) =>
        "CN=VeyonCampus " + (StudentSystemPolicySigningKeyStore.IsInternalNamespace(campusId)
            ? "Student System Policy " : "Website Policy ") +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(campusId)))[..24];

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

public sealed record WebsitePolicyAgentConfig(string CampusId, string PublicKeyPem, string? TelemetryEndpoint = null,
    Guid? DeploymentId = null, string? ApplicationVersion = null, string? ApplicationPolicyPublicKeyPem = null,
    string? StudentSystemPolicyPublicKeyPem = null);

public sealed record StagedWebsitePolicyAgentUpdate(string TargetExecutablePath, string PreviousExecutablePath);

/// <summary>Installs the student-only SYSTEM policy receiver and its LAN firewall rule.</summary>
public static class WebsitePolicyAgentInstaller
{
    private const string ScheduledTaskName = "VeyonCampus-WebsitePolicyAgent";
    private const string RestrictedTaskSecurityDescriptor = "D:P(A;;GA;;;SY)(A;;GA;;;BA)";


    public static string BuildVersion => typeof(WebsitePolicyAgentInstaller).Assembly
        .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "StudentAgentVersion").Value
        ?? throw new InvalidOperationException("无法读取随包构建的网站策略代理版本。");

    private static string InstalledVersionDirectory => BuildVersion;

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
            if (package.ApplicationPolicyPublicKeyPath is not null)
            {
                if (string.IsNullOrWhiteSpace(config.ApplicationPolicyPublicKeyPem))
                    return new(step, ExecutionPlan.NeedsReview, "后台代理缺少应用策略信任公钥；需要使用 schemaVersion=4 配置包修复。" );
                using var installedApplicationKey = RSA.Create();
                installedApplicationKey.ImportFromPem(config.ApplicationPolicyPublicKeyPem);
                using var packageApplicationKey = RSA.Create();
                packageApplicationKey.ImportFromPem(File.ReadAllText(package.ApplicationPolicyPublicKeyPath));
                if (!CryptographicOperations.FixedTimeEquals(installedApplicationKey.ExportSubjectPublicKeyInfo(),
                        packageApplicationKey.ExportSubjectPublicKeyInfo()))
                    return new(step, ExecutionPlan.NeedsReview, "后台代理应用策略公钥与学生配置包不匹配。" );
            }

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

            if (!WaitForAgentHealth(TimeSpan.FromSeconds(2), WebsitePolicyAgent.ConfigFingerprint(config)))
                return new(step, ExecutionPlan.NeedsReview, "SYSTEM 网站代理没有返回本机健康响应。" );

            var identityFingerprint = StudentAgentIdentityKeyStore.ReadFingerprint(configPath);
            return new(step, ExecutionPlan.Succeeded,
                $"独立网站代理文件、校区公钥、SYSTEM 开机任务、任务权限、防火墙规则及本机健康响应均已读回；校区 {package.Campus}；学生 Agent 身份指纹 {identityFingerprint}。请记录此完整指纹，供教师端首次固定身份时核对。" );
        }
        catch (Exception exception) when (exception is COMException or RuntimeBinderException or IOException or UnauthorizedAccessException or
                                          InvalidDataException or CryptographicException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or TimeoutException or HttpRequestException or
                                          OperationCanceledException or System.Security.SecurityException)
        {
            return new(step, ExecutionPlan.NeedsReview, $"网站代理只读验证未完成：{exception.Message}" );
        }
    }

    public static StepResult VerifyAbsent()
    {
        const string step = "website-agent-absent";
        if (!OperatingSystem.IsWindows())
            return new(step, ExecutionPlan.Failed, "网站策略代理核验仅支持 Windows。" );
        try
        {
            VerifyAgentInstallationRootsAreSafe();
            VerifyAgentConfigurationRootIsSafe();
            if (IsScheduledTaskPresent() || GetPreviousAgentExecutablePaths().Any(File.Exists) ||
                WebsitePolicyFirewall.HasNamedRule() || WebsitePolicyRegistryStore.HasAgentState() ||
                WindowsStudentSystemPolicyAgent.HasAnyActiveState() || FindAgentConfigurationFiles().Count > 0 || HasAnyAgentHealthResponse())
                return new(step, ExecutionPlan.NeedsReview, "仍发现网站策略代理任务、文件、配置、策略状态、防火墙规则或运行中的 Agent。" );
            return new(step, ExecutionPlan.Succeeded, "网站策略代理已卸载；本机不会再接收或应用该校区的网站策略。" );
        }
        catch (Exception exception)
        {
            return new(step, ExecutionPlan.NeedsReview, $"无法确认网站策略代理已完整移除：{exception.Message}" );
        }
    }

    public static StepResult Uninstall()
    {
        const string step = "website-agent-uninstall";
        var stage = "检查管理员权限";
        if (!OperatingSystem.IsWindows())
            return new(step, ExecutionPlan.Failed, "学生网站策略代理卸载仅支持 Windows。" );

        try
        {
            if (!IsElevated())
                return new(step, ExecutionPlan.Failed, "卸载学生网站策略代理需要管理员权限；请以管理员身份运行学生部署工具。" );

            stage = "核验 Agent 程序目录";
            VerifyAgentInstallationRootsAreSafe();
            stage = "核验 Agent 配置目录";
            VerifyAgentConfigurationRootIsSafe();
            stage = "读取计划任务";
            var task = ReadTaskForRemoval();
            stage = "读取 Agent 配置";
            var configs = FindAgentConfigurationFiles();
            stage = "读取网站策略所有权";
            var registryCampus = WebsitePolicyRegistryStore.ReadCampusForAgentRemoval();
            var taskCampus = task?.Config?.CampusId;
            // These records can legitimately disagree after an interrupted campus switch:
            // the task identifies the Agent to remove, while the registry identifies which
            // browser values this product still owns. Validate and remove each independently.
            var campus = ResolveCampusForRemoval(registryCampus, taskCampus,
                configs.Select(item => item.Config.CampusId));
            WindowsApplicationPolicyAgent? appLockerAgent = null;
            WebsitePolicyAgentConfig? appConfig = null;
            WebsitePolicyAgentConfig? systemConfig = null;
            if (campus is not null)
            {
                stage = "核验网站策略所有权";
                foreach (var item in configs.Where(item => string.Equals(item.Config.CampusId, campus, StringComparison.Ordinal)))
                    VerifyConfigPathIdentity(item.Path, item.Config);
                WebsitePolicyRegistryStore.VerifyCanRemoveOwnedState(campus);
                appConfig = configs.Select(item => item.Config).FirstOrDefault(config =>
                    string.Equals(config.CampusId, campus, StringComparison.Ordinal) &&
                    config.ApplicationPolicyPublicKeyPem is not null);
                systemConfig = configs.Select(item => item.Config).FirstOrDefault(config =>
                    string.Equals(config.CampusId, campus, StringComparison.Ordinal) &&
                    config.StudentSystemPolicyPublicKeyPem is not null);
                var systemStatePresent = WindowsStudentSystemPolicyAgent.HasState(campus);
                if (appConfig is not null)
                    appLockerAgent = WindowsApplicationPolicyAgent.Create(campus, appConfig.ApplicationPolicyPublicKeyPem!);
                else if (systemConfig is not null && systemStatePresent)
                    appLockerAgent = WindowsApplicationPolicyAgent.CreateForSystemPolicy(campus);
                appLockerAgent?.Runtime.ReadForAudit(DateTimeOffset.UtcNow);
                if (systemStatePresent)
                {
                    if (systemConfig is null)
                        throw new IOException("发现本校区系统策略状态，但 Agent 配置缺少对应公钥；保留策略和 Agent，需先恢复可信配置。");
                }
            }
            else
            {
                if (WindowsStudentSystemPolicyAgent.HasAnyActiveState())
                    throw new IOException("发现活动系统策略状态，但无法确认校区和受信公钥；保留 Agent 供管理员核对。");
                stage = "确认没有遗留浏览器策略";
                WebsitePolicyRegistryStore.RemoveEmptyAgentState();
            }

            var knownExecutables = GetPreviousAgentExecutablePaths();
            stage = "核验防火墙规则归属";
            WebsitePolicyFirewall.VerifyRemovable(knownExecutables);

            if (task is not null)
            {
                stage = "停止网站策略计划任务";
                StopRunningScheduledTask(ScheduledTaskName);
            }

            stage = "停止网站策略 Agent 进程";
            StopManagedAgentProcesses(knownExecutables);
            stage = "确认代理端口已释放";
            if (!WaitForNoAgentHealth(TimeSpan.FromSeconds(10)))
                throw new IOException("停止已确认归属的 Agent 后，39174 端口仍有网站策略代理健康响应；任务和文件尚未删除。" );

            if (campus is not null)
            {
                if (appLockerAgent is not null)
                {
                    stage = "安全恢复并解除应用策略";
                    appLockerAgent.RestoreForRemoval();
                }
                if (WindowsStudentSystemPolicyAgent.HasState(campus))
                {
                    if (systemConfig is null)
                        throw new IOException("系统策略信任公钥缺失；保留 Agent 和策略状态，未继续卸载。");
                    stage = "安全恢复并解除学生机系统策略";
                    new WindowsStudentSystemPolicyAgent(campus, systemConfig.StudentSystemPolicyPublicKeyPem!, appLockerAgent?.Runtime)
                        .Runtime.RestoreForRemoval();
                }
            }

            if (task is not null)
            {
                stage = "删除网站策略计划任务";
                DeleteScheduledTask();
            }

            stage = "清理网站策略所有权记录";
            if (campus is not null)
                WebsitePolicyRegistryStore.RemoveOwnedState(campus);
            else
                WebsitePolicyRegistryStore.RemoveEmptyAgentState();

            stage = "移除本工具防火墙规则";
            WebsitePolicyFirewall.RemoveOwned(knownExecutables);
            stage = "移除 Agent 配置目录";
            RemoveAgentConfigurationFiles();
            stage = "移除 Agent 程序目录";
            RemoveAgentInstallationRoots();

            stage = "复核卸载结果";
            var readback = VerifyAbsent();
            if (readback.Status != ExecutionPlan.Succeeded)
                return new(step, ExecutionPlan.NeedsReview,
                    "卸载操作已执行，但只读复核仍发现残留：" + readback.Detail);
            return new(step, ExecutionPlan.Succeeded,
                "VeyonCampus 网站策略 Agent、SYSTEM 计划任务、本工具防火墙规则及可确认归属的 Edge、Chrome、Firefox 网址策略已移除；Veyon 和学生部署工具未改动。" );
        }
        catch (Exception exception)
        {
            return new(step, ExecutionPlan.NeedsReview,
                $"网站策略代理卸载在“{stage}”时停止：{exception.Message}。未清理无法确认归属的策略或文件；请核对后重试。" );
        }
    }

    public static StepResult Install(PackageContext package, PackageResourceSnapshot snapshot,
        string? agentSourceDirectory = null)
    {
        const string step = "website-agent";
        var stage = "检查校区配置";
        if (!OperatingSystem.IsWindows())
            return new(step, ExecutionPlan.Failed, "学生网站策略代理仅支持 Windows。");
        if (package.WebsitePolicyPublicKeyPath is null || snapshot.WebsitePolicyPublicKeyPath is null)
            return new(step, ExecutionPlan.Skipped, "旧版学生配置包没有网站策略公钥；网站访问控制代理未安装。请使用教师端新生成的配置包重新部署。" );

        try
        {
            stage = "验证校区配置未被修改";
            package.VerifyUnchanged();
            snapshot.VerifyUnchanged();
            if (!IsElevated())
                return new(step, ExecutionPlan.Failed, "安装学生网站策略代理需要管理员权限；没有注册 SYSTEM 任务或防火墙规则。");

            stage = "检查本机旧 Agent 残留";
            EnsureExistingAgentCampusMatches(package.Campus);

            stage = "读取并验证校区公钥";
            var publicPem = File.ReadAllText(snapshot.WebsitePolicyPublicKeyPath);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicPem);
            var canonicalPem = rsa.ExportSubjectPublicKeyInfoPem();
            string? applicationPolicyPem = null;
            if (package.ApplicationPolicyPublicKeyPath is not null)
            {
                if (snapshot.ApplicationPolicyPublicKeyPath is null)
                    throw new InvalidDataException("schemaVersion=4 缺少应用策略公钥快照。");
                using var applicationRsa = RSA.Create();
                applicationRsa.ImportFromPem(File.ReadAllText(snapshot.ApplicationPolicyPublicKeyPath));
                applicationPolicyPem = applicationRsa.ExportSubjectPublicKeyInfoPem();
            }
            string? studentSystemPolicyPem = null;
            if (package.StudentSystemPolicyPublicKeyPath is not null)
            {
                if (snapshot.StudentSystemPolicyPublicKeyPath is null)
                    throw new InvalidDataException("schemaVersion=5 缺少系统策略公钥快照。");
                using var systemRsa = RSA.Create();
                systemRsa.ImportFromPem(File.ReadAllText(snapshot.StudentSystemPolicyPublicKeyPath));
                studentSystemPolicyPem = systemRsa.ExportSubjectPublicKeyInfoPem();
            }
            var sourceDirectory = Path.GetFullPath(agentSourceDirectory ??
                Path.Combine(AppContext.BaseDirectory, "WebsitePolicyAgent"));
            var sourceExecutable = Path.Combine(sourceDirectory, "VeyonCampus.Agent.exe");
            if (!File.Exists(sourceExecutable))
                throw new FileNotFoundException("找不到独立的 VeyonCampus.Agent.exe；不能安装后台网站策略代理。", sourceExecutable);
            if (!WorkerInstallationGuard.ProductVersionMatches(sourceExecutable, BuildVersion))
                throw new InvalidDataException("学生代理文件与安装包版本不一致；请修复学生部署工具安装。");

            stage = "复制并保护 Agent 程序文件";
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
            stage = "写入 Agent 校区配置";
            CreateSecureDirectory(Path.GetDirectoryName(configDirectory)!);
            CreateSecureDirectory(configDirectory);
            AgentFileSecurity.SecureTree(configDirectory, executable: false);
            var configPath = Path.Combine(configDirectory, "agent-" + campusHash + ".json");
            var existing = ReadExistingConfig(configPath);
            if (existing is not null && (existing.CampusId != package.Campus ||
                                         !string.Equals(existing.PublicKeyPem, canonicalPem, StringComparison.Ordinal)))
                throw new IOException("网站策略代理已绑定校区或公钥不同；为避免意外更换信任根，没有覆盖现有配置。");
            if (existing?.ApplicationPolicyPublicKeyPem is not null && applicationPolicyPem is not null &&
                !string.Equals(existing.ApplicationPolicyPublicKeyPem, applicationPolicyPem, StringComparison.Ordinal))
                throw new IOException("应用策略代理已绑定其他校区公钥；为避免意外更换信任根，没有覆盖现有配置。");
            if (existing?.StudentSystemPolicyPublicKeyPem is not null && studentSystemPolicyPem is not null &&
                !string.Equals(existing.StudentSystemPolicyPublicKeyPem, studentSystemPolicyPem, StringComparison.Ordinal) &&
                WindowsStudentSystemPolicyAgent.HasState(package.Campus))
                throw new IOException("系统策略密钥正在保护本机活动策略；不能在配置升级时静默更换信任根。");
            var telemetryEndpoint = package.TelemetryEndpoint ?? existing?.TelemetryEndpoint;
            if (!string.IsNullOrWhiteSpace(telemetryEndpoint))
                AnonymousUsageHeartbeat.ValidateEndpoint(telemetryEndpoint);
            var studentSetupVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
                                      ?? "unknown";
            var config = new WebsitePolicyAgentConfig(package.Campus, canonicalPem, telemetryEndpoint,
                package.DeploymentId ?? existing?.DeploymentId, studentSetupVersion,
                applicationPolicyPem ?? existing?.ApplicationPolicyPublicKeyPem,
                studentSystemPolicyPem ?? existing?.StudentSystemPolicyPublicKeyPem);
            var configBytes = JsonSerializer.SerializeToUtf8Bytes(config, WebsitePolicyAgent.JsonOptions);

            var existingAgentMatchesConfiguration = existing is not null &&
                WaitForAgentHealth(TimeSpan.FromSeconds(2), WebsitePolicyAgent.ConfigFingerprint(existing));
            if (existing is not null && ShouldRestartAgentForReconfiguration(
                    existing, config, existingAgentMatchesConfiguration))
            {
                stage = "重启现有代理以载入新配置";
                var existingTask = ReadTaskForRemoval();
                if (existingTask is not null) StopRunningScheduledTask(ScheduledTaskName);
                StopManagedAgentProcesses(GetPreviousAgentExecutablePaths()
                    .Append(installedExecutable)
                    .Append(existingTask?.ExecutablePath ?? installedExecutable));
                if (!WaitForNoAgentHealth(TimeSpan.FromSeconds(10)))
                    throw new IOException("旧代理仍在运行；没有替换配置。请稍后重试。");
            }

            WriteSecureConfig(configPath, configBytes);

            stage = "预留 HTTP.sys 监听地址";
            EnsureUrlReservation();
            stage = "创建或核验 Windows 防火墙规则";
            EnsureFirewallRule(installedExecutable);
            stage = "创建或核验 SYSTEM 计划任务";
            EnsureScheduledTask(installedExecutable, configPath);
            var startupLogPath = Path.Combine(configDirectory, "agent-startup.log");
            var startupLogOffset = File.Exists(startupLogPath) ? new FileInfo(startupLogPath).Length : 0;
            var expectedConfigFingerprint = WebsitePolicyAgent.ConfigFingerprint(config);
            if (!WaitForAgentHealth(TimeSpan.FromSeconds(1), expectedConfigFingerprint))
            {
                stage = "启动 SYSTEM 计划任务";
                StartScheduledTask();
            }
            stage = "等待 Agent 健康响应";
            if (!WaitForAgentHealth(TimeSpan.FromSeconds(12), expectedConfigFingerprint))
                return new(step, ExecutionPlan.NeedsReview,
                    "SYSTEM 代理任务和网络规则已注册，但 Agent 没有返回本机健康响应；网站推送暂不可用。" +
                    ReadNewAgentStartupDiagnostic(startupLogPath, startupLogOffset));
            stage = "读回 Agent 文件、校区配置、任务权限和健康状态";
            var verification = VerifyInstalled(package);
            if (verification.Status != ExecutionPlan.Succeeded)
                return new(step, ExecutionPlan.NeedsReview,
                    "网站代理启动后，独立只读复核未通过；请核对安装状态，不要盲目重试。" + verification.Detail);
            return new(step, ExecutionPlan.Succeeded,
                $"学生网站策略代理已安装并通过独立只读复核；校区 {package.Campus}，监听端口 {WebsitePolicyAgent.Port}，只部署了教师公钥。" );
        }
        catch (Exception exception) when (exception is COMException or RuntimeBinderException or IOException or UnauthorizedAccessException or
                                          InvalidDataException or CryptographicException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or TimeoutException)
        {
            return new(step, ExecutionPlan.NeedsReview,
                $"学生网站策略代理在“{stage}”阶段未完成：{exception.Message}。请检查已创建的任务、目录和防火墙规则后再重试。" );
        }
    }

    public static StagedWebsitePolicyAgentUpdate StageAgentUpdate(string sourceDirectory, string targetVersion,
        string configPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("学生网站策略代理更新仅支持 Windows。");
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            if (identity.User?.Value != "S-1-5-18")
                throw new UnauthorizedAccessException("学生网站策略代理更新只能由 SYSTEM Agent 执行。");

        _ = ApplicationReleaseClient.CompareVersions(targetVersion, targetVersion);
        var fullSourceDirectory = Path.GetFullPath(sourceDirectory);
        var expectedSourceDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Veyon Campus", "Student", "WebsitePolicyAgent");
        if (!PathEquals(fullSourceDirectory, expectedSourceDirectory))
            throw new InvalidDataException("新 Agent 必须来自固定的 StudentSetup 安装目录。");
        PathLinkSecurity.RejectLinks(fullSourceDirectory);
        var sourceExecutable = Path.Combine(fullSourceDirectory, "VeyonCampus.Agent.exe");
        if (!File.Exists(sourceExecutable) ||
            (File.GetAttributes(sourceExecutable) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("StudentSetup 中的新 Agent 缺失或不是普通文件。");

        var fullConfigPath = Path.GetFullPath(configPath);
        PathLinkSecurity.RejectLinks(fullConfigPath);
        var config = ReadExistingConfig(fullConfigPath)
                     ?? throw new InvalidDataException("Student Agent 校区配置缺失；未切换启动任务。");
        VerifyConfigPathIdentity(fullConfigPath, config);
        var existingTask = ReadTaskForRemoval()
                           ?? throw new InvalidDataException("SYSTEM Agent 启动任务缺失；未切换更新版本。");
        if (!PathEquals(existingTask.ConfigPath, fullConfigPath) || existingTask.Config is null ||
            !string.Equals(existingTask.Config.CampusId, config.CampusId, StringComparison.Ordinal))
            throw new InvalidDataException("当前 SYSTEM Agent 任务与校区配置不匹配；未切换更新版本。");

        var commonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var targetDirectory = Path.Combine(commonApplicationData, "VeyonCampus", "WebsitePolicyAgent", targetVersion);
        if (!PathEquals(fullSourceDirectory, targetDirectory))
            InstallApplicationFiles(fullSourceDirectory, targetDirectory);
        else
            AgentFileSecurity.SecureTree(targetDirectory);
        var targetExecutable = Path.Combine(targetDirectory, "VeyonCampus.Agent.exe");
        if (!File.Exists(targetExecutable) ||
            (File.GetAttributes(targetExecutable) & FileAttributes.ReparsePoint) != 0 ||
            !DirectoryTreesEqual(fullSourceDirectory, targetDirectory))
            throw new InvalidDataException("新 Agent 的受保护副本与 StudentSetup 安装文件不一致。");

        EnsureFirewallRule(targetExecutable);
        var commandLine = Quote(targetExecutable) + " --website-policy-agent " + Quote(fullConfigPath);
        var updated = Run("schtasks.exe", ["/Create", "/TN", ScheduledTaskName, "/SC", "ONSTART", "/RU",
            "SYSTEM", "/RL", "HIGHEST", "/TR", commandLine, "/F"]);
        if (updated.ExitCode != 0)
            throw new IOException("无法将 SYSTEM Agent 启动任务切换到新版本；旧进程保持运行。");
        ProtectScheduledTaskAcl(ScheduledTaskName);

        var updatedTask = ReadTaskForRemoval();
        if (updatedTask is null || !PathEquals(updatedTask.ExecutablePath, targetExecutable) ||
            !PathEquals(updatedTask.ConfigPath, fullConfigPath) || !HasRestrictedScheduledTaskAcl(ScheduledTaskName))
            throw new IOException("新 Agent 计划任务或受限 ACL 读回不匹配；没有报告更新成功。");
        return new StagedWebsitePolicyAgentUpdate(targetExecutable, existingTask.ExecutablePath);
    }

    public static void StartStagedAgentUpdate(string expectedExecutablePath, string configPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("学生网站策略代理更新仅支持 Windows。");
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            if (identity.User?.Value != "S-1-5-18")
                throw new UnauthorizedAccessException("学生网站策略代理更新只能由 SYSTEM Agent 执行。");

        var executablePath = Path.GetFullPath(expectedExecutablePath);
        var configFullPath = Path.GetFullPath(configPath);
        PathLinkSecurity.RejectLinks(executablePath);
        PathLinkSecurity.RejectLinks(configFullPath);
        var task = ReadTaskForRemoval()
                   ?? throw new InvalidDataException("SYSTEM Agent 更新任务已丢失。");
        if (!PathEquals(task.ExecutablePath, executablePath) || !PathEquals(task.ConfigPath, configFullPath) ||
            task.Config is null || !HasRestrictedScheduledTaskAcl(ScheduledTaskName))
            throw new InvalidDataException("SYSTEM Agent 更新任务目标、配置或 ACL 读回不匹配。");
        StartScheduledTask();
    }

    public static void ValidateAgentUpdateRollbackPaths(string expectedTargetExecutablePath,
        string previousExecutablePath, string configPath, string targetVersion, string previousVersion)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("学生网站策略代理更新仅支持 Windows。");
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            if (identity.User?.Value != "S-1-5-18")
                throw new UnauthorizedAccessException("学生网站策略代理更新只能由 SYSTEM Agent 执行。");

        if (ApplicationReleaseClient.CompareVersions(targetVersion, previousVersion) <= 0)
            throw new InvalidDataException("Agent 更新版本必须高于可恢复的当前版本。");
        var expectedTarget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VeyonCampus", "WebsitePolicyAgent", targetVersion, "VeyonCampus.Agent.exe");
        var fullTarget = Path.GetFullPath(expectedTargetExecutablePath);
        var fullPrevious = Path.GetFullPath(previousExecutablePath);
        var fullConfig = Path.GetFullPath(configPath);
        if (!PathEquals(fullTarget, expectedTarget) || PathEquals(fullTarget, fullPrevious))
            throw new InvalidDataException("Agent 更新目标或回滚路径无效。");
        PathLinkSecurity.RejectLinks(fullTarget);
        PathLinkSecurity.RejectLinks(fullPrevious);
        PathLinkSecurity.RejectLinks(fullConfig);
        if (!File.Exists(fullTarget) || !File.Exists(fullPrevious) ||
            !GetPreviousAgentExecutablePaths().Any(path => PathEquals(path, fullPrevious)))
            throw new InvalidDataException("Agent 更新目标或旧版本文件缺失，不能安全切换。");

        var previousDirectory = Path.GetFileName(Path.GetDirectoryName(fullPrevious));
        if (previousDirectory is not null && IsValidAgentVersionDirectory(previousDirectory) &&
            !string.Equals(previousDirectory, previousVersion, StringComparison.Ordinal))
            throw new InvalidDataException("旧 Agent 目录版本与当前运行版本不匹配。");

        var task = ReadTaskForRemoval()
                   ?? throw new InvalidDataException("SYSTEM Agent 更新任务已丢失。");
        if (!PathEquals(task.ExecutablePath, fullTarget) || !PathEquals(task.ConfigPath, fullConfig) ||
            task.Config is null || !HasRestrictedScheduledTaskAcl(ScheduledTaskName))
            throw new InvalidDataException("切换后的 SYSTEM Agent 任务、配置或 ACL 读回不匹配。");
    }

    public static void RestorePreviousAgentTask(string expectedTargetExecutablePath,
        string previousExecutablePath, string configPath, string targetVersion, string previousVersion,
        string expectedConfigFingerprint)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("学生网站策略代理更新仅支持 Windows。");
        if (!Regex.IsMatch(expectedConfigFingerprint, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("旧 Agent 健康读回指纹无效。");
        ValidateAgentUpdateRollbackPaths(expectedTargetExecutablePath, previousExecutablePath, configPath,
            targetVersion, previousVersion);
        var fullTarget = Path.GetFullPath(expectedTargetExecutablePath);
        var fullPrevious = Path.GetFullPath(previousExecutablePath);
        var fullConfig = Path.GetFullPath(configPath);

        StopRunningScheduledTask(ScheduledTaskName);
        StopManagedAgentProcesses([fullTarget]);
        var commandLine = Quote(fullPrevious) + " --website-policy-agent " + Quote(fullConfig);
        var restored = Run("schtasks.exe", ["/Create", "/TN", ScheduledTaskName, "/SC", "ONSTART", "/RU",
            "SYSTEM", "/RL", "HIGHEST", "/TR", commandLine, "/F"]);
        if (restored.ExitCode != 0)
            throw new IOException("无法将 SYSTEM Agent 任务恢复为旧版本。");
        ProtectScheduledTaskAcl(ScheduledTaskName);
        var restoredTask = ReadTaskForRemoval();
        if (restoredTask is null || !PathEquals(restoredTask.ExecutablePath, fullPrevious) ||
            !PathEquals(restoredTask.ConfigPath, fullConfig) || restoredTask.Config is null ||
            !HasRestrictedScheduledTaskAcl(ScheduledTaskName))
            throw new IOException("旧版本 SYSTEM Agent 任务或受限 ACL 恢复读回不匹配。");
        EnsureFirewallRule(fullPrevious);
        if (WaitForUpdatedAgentHealth(TimeSpan.FromSeconds(10), expectedConfigFingerprint, previousVersion))
            return;
        if (!WaitForNoAgentHealth(TimeSpan.FromSeconds(10)))
            throw new IOException("新 Agent 停止后仍有进程占用健康端口；旧任务已恢复但未启动。");
        StartScheduledTask();
    }

    public static void WaitForManagedAgentExit(int processId, long startTimeUtcTicks, TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("学生网站策略代理更新仅支持 Windows。");
        if (processId <= 0 || startTimeUtcTicks <= 0 || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
            throw new InvalidDataException("待退出 Agent 进程标识或等待期限无效。");

        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; }
        using (process)
        {
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startTimeUtcTicks) return;
            var executablePath = process.MainModule?.FileName
                                 ?? throw new InvalidDataException("待退出 Agent 进程路径不可读。");
            if (!GetPreviousAgentExecutablePaths().Any(path => PathEquals(path, executablePath)))
                throw new InvalidDataException("待退出进程不是受管的 VeyonCampus Agent。");
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                throw new TimeoutException("等待旧版 Student Agent 退出超时；没有启动新版本。");
        }
    }

    public static bool WaitForUpdatedAgentHealth(TimeSpan timeout, string expectedConfigFingerprint,
        string expectedVersion)
    {
        _ = ApplicationReleaseClient.CompareVersions(expectedVersion, expectedVersion);
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        using var deadline = new CancellationTokenSource(timeout);
        var address = "http://127.0.0.1:" + WebsitePolicyAgent.Port + "/health";
        while (!deadline.IsCancellationRequested)
        {
            try
            {
                using var response = client.GetAsync(address, deadline.Token).GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode &&
                    response.Headers.TryGetValues("X-VeyonCampus-Agent-Config", out var fingerprints) &&
                    fingerprints.Contains(expectedConfigFingerprint, StringComparer.OrdinalIgnoreCase) &&
                    response.Headers.TryGetValues("X-VeyonCampus-Agent-Version", out var versions) &&
                    versions.Contains(expectedVersion, StringComparer.Ordinal))
                    return true;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!deadline.IsCancellationRequested) { }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return false; }
            if (deadline.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(500))) return false;
        }
        return false;
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
            AgentFileSecurity.Secure(targetPath, directory: false,
                executable: !Path.GetExtension(targetPath).Equals(".jpg", StringComparison.OrdinalIgnoreCase));
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
            return Path.GetExtension(name).ToLowerInvariant() is ".dll" or ".exe" or ".json" or ".config" or ".dat" or ".jpg";
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
            var isManagedAgentTask = false;
            try
            {
                var xml = XDocument.Parse(existing.Stdout);
                XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                var user = xml.Descendants(ns + "UserId").FirstOrDefault()?.Value;
                var command = xml.Descendants(ns + "Command").FirstOrDefault()?.Value;
                var arguments = xml.Descendants(ns + "Arguments").FirstOrDefault()?.Value ?? "";
                if (user == "S-1-5-18" && !string.IsNullOrWhiteSpace(command) &&
                    TryGetManagedAgentConfigPath(arguments, out var existingConfigPath))
                {
                    var knownExecutables = GetPreviousAgentExecutablePaths().Append(executable).ToArray();
                    isManagedAgentTask = knownExecutables.Any(path => PathEquals(command, path));
                    isCurrentTask = isManagedAgentTask && PathEquals(command, executable) &&
                        PathEquals(existingConfigPath, configPath);
                }
            }
            catch (Exception exception) when (exception is System.Xml.XmlException or ArgumentException or
                                              IOException or UnauthorizedAccessException or
                                              System.Security.SecurityException) { }
            if (isCurrentTask)
            {
                ProtectScheduledTaskAcl(ScheduledTaskName);
                return;
            }
            if (!isManagedAgentTask)
                throw new IOException("同名计划任务已存在但无法确认为本工具上一版本的 SYSTEM 网站代理；没有覆盖该任务。" );

            StopRunningScheduledTask(ScheduledTaskName);
            StopManagedAgentProcesses(GetPreviousAgentExecutablePaths().Append(executable));
            if (!WaitForNoAgentHealth(TimeSpan.FromSeconds(10)))
                throw new IOException("旧版网站策略 Agent 停止任务后仍占用监听端口；没有注册新任务。请先使用学生端卸载入口清理旧 Agent。" );
        }

        var commandLine = Quote(executable) + " --website-policy-agent " + Quote(configPath);
        var created = Run("schtasks.exe", ["/Create", "/TN", ScheduledTaskName, "/SC", "ONSTART", "/RU", "SYSTEM",
            "/RL", "HIGHEST", "/TR", commandLine, "/F"]);
        if (created.ExitCode != 0) throw new IOException("无法注册 SYSTEM 开机代理任务。" );
        ProtectScheduledTaskAcl(ScheduledTaskName);
    }

    private static bool TryGetManagedAgentConfigPath(string arguments, out string configPath)
    {
        configPath = "";
        var match = Regex.Match(arguments,
            "^\\s*--website-policy-agent\\s+\"(?<path>[^\"]+)\"\\s*$",
            RegexOptions.CultureInvariant);
        if (!match.Success) return false;

        try
        {
            var fullPath = Path.GetFullPath(match.Groups["path"].Value);
            var configRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "VeyonCampus", "WebsitePolicy"));
            var rootPrefix = configRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                             Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return false;

            var relative = Path.GetRelativePath(configRoot, fullPath);
            var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !Regex.IsMatch(parts[0], "^[0-9A-Fa-f]{24}$", RegexOptions.CultureInvariant) ||
                !string.Equals(parts[1], "agent-" + parts[0] + ".json", StringComparison.OrdinalIgnoreCase))
                return false;

            configPath = fullPath;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
                                          UnauthorizedAccessException or NotSupportedException or
                                          System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string[] GetPreviousAgentExecutablePaths()
    {
        var paths = new List<string>();
        foreach (var versionRoot in GetAgentInstallationRoots())
        {
            if (!Directory.Exists(versionRoot) ||
                (File.GetAttributes(versionRoot) & FileAttributes.ReparsePoint) != 0) continue;
            paths.Add(Path.Combine(versionRoot, "VeyonCampus.Agent.exe"));
            foreach (var directory in Directory.EnumerateDirectories(versionRoot, "*", SearchOption.TopDirectoryOnly))
            {
                var version = Path.GetFileName(directory);
                if (!IsValidAgentVersionDirectory(version) ||
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                paths.Add(Path.Combine(directory, "VeyonCampus.Agent.exe"));
            }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsValidAgentVersionDirectory(string version)
    {
        try
        {
            _ = ApplicationReleaseClient.CompareVersions(version, version);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private sealed record AgentConfigFile(string Path, WebsitePolicyAgentConfig Config);
    private sealed record AgentTaskForRemoval(
        string ExecutablePath,
        string ConfigPath,
        string CampusHash,
        WebsitePolicyAgentConfig? Config);

    private static string? ResolveCampusForRemoval(
        string? registryCampus,
        string? taskCampus,
        IEnumerable<string> configCampuses)
    {
        if (!string.IsNullOrWhiteSpace(registryCampus)) return registryCampus;
        if (!string.IsNullOrWhiteSpace(taskCampus)) return taskCampus;
        var campuses = configCampuses
            .Where(campus => !string.IsNullOrWhiteSpace(campus))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        return campuses.Length == 1 ? campuses[0] : null;
    }

    private static bool HasUnrecognizedAgentConfiguration(IReadOnlyCollection<AgentConfigFile> configurations)
    {
        var root = GetAgentConfigurationRoot();
        if (!Directory.Exists(root)) return false;
        var recognizedPaths = configurations
            .Select(item => Path.GetFullPath(item.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var hash = Path.GetFileName(directory);
            if (!Regex.IsMatch(hash, "^[0-9A-Fa-f]{24}$", RegexOptions.CultureInvariant)) continue;
            var configPath = Path.GetFullPath(Path.Combine(directory, "agent-" + hash + ".json"));
            if (File.Exists(configPath) && !recognizedPaths.Contains(configPath)) return true;
        }
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureExistingAgentCampusMatches(string requestedCampus)
    {
        var requestedHash = CampusHash(requestedCampus);
        var registryCampus = WebsitePolicyRegistryStore.ReadCampusForAgentRemoval();
        var task = ReadTaskForRemoval();
        VerifyAgentConfigurationRootIsSafe();
        var configurations = FindAgentConfigurationFiles();
        var hasUnrecognizedConfiguration = HasUnrecognizedAgentConfiguration(configurations);
        if ((registryCampus is not null && !string.Equals(registryCampus, requestedCampus, StringComparison.Ordinal)) ||
            (task is not null && !string.Equals(task.CampusHash, requestedHash, StringComparison.OrdinalIgnoreCase)) ||
            configurations.Any(item => !string.Equals(item.Config.CampusId, requestedCampus, StringComparison.Ordinal)) ||
            hasUnrecognizedConfiguration)
            throw new IOException("本机已有其他校区或中断部署残留的网站策略 Agent。为避免混用校区密钥，请先使用“卸载本机网站策略代理”入口清理旧 Agent，再重新部署此校区配置。" );
        if (task is { Config: null })
            throw new IOException("本机 Agent 配置缺失或损坏。请先使用“卸载本机网站策略代理”入口清理旧 Agent，再重新部署。" );
        if (task is null && ReadActiveAgentFingerprints().Count > 0)
            throw new IOException("本机有正在运行但未登记计划任务的网站策略 Agent。为避免 39174 端口冲突，请先使用学生端卸载入口清理旧 Agent，再重新部署。" );
    }

    internal static bool ShouldRestartAgentForReconfiguration(WebsitePolicyAgentConfig current,
        WebsitePolicyAgentConfig replacement, bool agentMatchesCurrentConfiguration) =>
        !agentMatchesCurrentConfiguration ||
        !string.Equals(WebsitePolicyAgent.ConfigFingerprint(current),
            WebsitePolicyAgent.ConfigFingerprint(replacement), StringComparison.Ordinal);

    private static string[] GetAgentInstallationRoots()
    {
        var commonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var roots = new List<string>
        {
            Path.Combine(commonApplicationData, "VeyonCampus", "WebsitePolicyAgent"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VeyonCampus",
                "WebsitePolicyAgent")
        };
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            roots.Add(Path.Combine(programFilesX86, "VeyonCampus", "WebsitePolicyAgent"));
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string GetAgentConfigurationRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VeyonCampus", "WebsitePolicy");

    private static void VerifyAgentConfigurationPathAncestors()
    {
        var campusRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VeyonCampus");
        if (Directory.Exists(campusRoot) && (File.GetAttributes(campusRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("VeyonCampus 数据目录是重解析点；拒绝读取或删除网站策略 Agent 数据。" );
    }

    private static string CampusHash(string campusId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(campusId)))[..24];

    [SupportedOSPlatform("windows")]
    private static AgentTaskForRemoval? ReadTaskForRemoval()
    {
        var query = Run("schtasks.exe", ["/Query", "/TN", ScheduledTaskName, "/XML"]);
        if (query.ExitCode != 0)
        {
            if (!IsScheduledTaskPresent()) return null;
            throw new IOException("网站策略计划任务存在，但无法读回其定义；没有卸载未知任务。" );
        }

        XDocument xml;
        try { xml = XDocument.Parse(query.Stdout); }
        catch (System.Xml.XmlException exception)
        { throw new IOException("网站策略计划任务 XML 无法解析；没有卸载未知任务。", exception); }
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var user = xml.Descendants(ns + "UserId").FirstOrDefault()?.Value;
        var executable = xml.Descendants(ns + "Command").FirstOrDefault()?.Value;
        var arguments = xml.Descendants(ns + "Arguments").FirstOrDefault()?.Value ?? "";
        var knownExecutables = GetPreviousAgentExecutablePaths();
        if (user != "S-1-5-18" || string.IsNullOrWhiteSpace(executable) ||
            !knownExecutables.Any(path => PathEquals(path, executable)) ||
            !TryGetManagedAgentConfigPath(arguments, out var configPath))
            throw new IOException("同名计划任务不能确认是本工具注册的 SYSTEM 网站策略 Agent；没有删除该任务。" );
        var campusHash = Path.GetFileName(Path.GetDirectoryName(configPath))!;
        WebsitePolicyAgentConfig? config;
        try
        {
            config = ReadExistingConfig(configPath);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            // A prior failed deployment can leave a truncated config. The task identity and
            // hash-scoped product path still establish ownership; browser policies are only
            // removed later when their separate registry ownership record validates.
            config = null;
        }
        if (config is not null) VerifyConfigPathIdentity(configPath, config);
        return new AgentTaskForRemoval(executable, configPath, campusHash, config);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsScheduledTaskPresent()
    {
        var schedulerType = Type.GetTypeFromProgID("Schedule.Service")
                            ?? throw new IOException("无法连接任务计划程序以核对网站策略任务。" );
        object? scheduler = null;
        object? folder = null;
        object? task = null;
        try
        {
            scheduler = Activator.CreateInstance(schedulerType)
                        ?? throw new IOException("无法创建任务计划程序 COM 对象。" );
            ((dynamic)scheduler).Connect();
            folder = ((dynamic)scheduler).GetFolder("\\");
            task = ((dynamic)folder).GetTask("\\" + ScheduledTaskName);
            return task is not null;
        }
        catch (Exception exception) when (IsMissingScheduledTaskException(exception))
        {
            return false;
        }
        catch (Exception exception) when (exception is COMException or RuntimeBinderException or InvalidComObjectException)
        {
            throw new IOException("无法确认网站策略计划任务是否存在。", exception);
        }
        finally
        {
            ReleaseComObject(task);
            ReleaseComObject(folder);
            ReleaseComObject(scheduler);
        }
    }

    private static bool IsMissingScheduledTaskError(int hresult) =>
        unchecked((uint)hresult) is 0x80070002 or 0x80070003 or 0x8004130F;

    private static bool IsMissingScheduledTaskException(Exception exception) =>
        (exception is COMException or FileNotFoundException or DirectoryNotFoundException) &&
        IsMissingScheduledTaskError(exception.HResult);

    [SupportedOSPlatform("windows")]
    private static void DeleteScheduledTask()
    {
        var deleted = Run("schtasks.exe", ["/Delete", "/TN", ScheduledTaskName, "/F"]);
        if (IsScheduledTaskPresent())
            throw new IOException("网站策略计划任务删除失败；没有清理 Agent 程序文件。" +
                                  (deleted.Stderr.Length > 0 ? " " + TruncateDiagnostic(deleted.Stderr) : ""));
    }

    private static List<AgentConfigFile> FindAgentConfigurationFiles()
    {
        var root = GetAgentConfigurationRoot();
        VerifyAgentConfigurationPathAncestors();
        if (!Directory.Exists(root)) return [];
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("网站策略 Agent 配置根目录是重解析点；拒绝读取或删除。" );
        var configs = new List<AgentConfigFile>();
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var hash = Path.GetFileName(directory);
            if (!Regex.IsMatch(hash, "^[0-9A-Fa-f]{24}$", RegexOptions.CultureInvariant)) continue;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("网站策略 Agent 校区配置目录是重解析点；拒绝读取或删除。" );
            var configPath = Path.Combine(directory, "agent-" + hash + ".json");
            if (!File.Exists(configPath)) continue;
            if ((File.GetAttributes(configPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("网站策略 Agent 配置文件是重解析点；拒绝读取或删除。" );
            WebsitePolicyAgentConfig? config;
            try
            {
                config = ReadExistingConfig(configPath);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                // Keep malformed but correctly named files in the product-owned configuration
                // tree removable; a valid task path or registry record must still identify campus.
                continue;
            }
            if (config is null) continue;
            VerifyConfigPathIdentity(configPath, config);
            configs.Add(new AgentConfigFile(configPath, config));
        }
        return configs;
    }

    private static void VerifyConfigPathIdentity(string configPath, WebsitePolicyAgentConfig config)
    {
        VerifyAgentConfigurationPathAncestors();
        var configDirectory = Path.GetDirectoryName(configPath);
        if (configDirectory is null || !Directory.Exists(configDirectory) ||
            (File.GetAttributes(configDirectory) & FileAttributes.ReparsePoint) != 0 ||
            !File.Exists(configPath) || (File.GetAttributes(configPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Agent 配置目录或文件不存在，或是重解析点；卸载已停止。" );
        if (string.IsNullOrWhiteSpace(config.CampusId) ||
            !string.Equals(Path.GetFileName(configPath), "agent-" + CampusHash(config.CampusId) + ".json",
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("Agent 配置中的校区标识与其哈希目录不一致；卸载已停止。" );
    }

    private static void VerifyAgentInstallationRootsAreSafe()
    {
        foreach (var root in GetAgentInstallationRoots())
        {
            var campusRoot = Path.GetDirectoryName(root);
            if (campusRoot is not null && Directory.Exists(campusRoot) &&
                (File.GetAttributes(campusRoot) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("VeyonCampus 安装数据目录是重解析点；拒绝删除网站策略 Agent 文件。" );
            if (!Directory.Exists(root)) continue;
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("网站策略 Agent 安装目录是重解析点；拒绝删除。" );
            foreach (var entry in EnumerateAgentTreeWithoutLinks(root))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) == 0 && !IsApplicationFile(entry))
                    throw new IOException($"网站策略 Agent 安装目录含无法确认归属的文件：{entry}；拒绝删除。" );
            }
        }
    }

    private static void RemoveAgentInstallationRoots()
    {
        foreach (var root in GetAgentInstallationRoots())
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static IEnumerable<string> EnumerateAgentTreeWithoutLinks(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("网站策略 Agent 安装目录含重解析点；拒绝递归删除。" );
            yield return entry;
            if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                foreach (var child in EnumerateAgentTreeWithoutLinks(entry)) yield return child;
        }
    }

    private static void VerifyAgentConfigurationRootIsSafe()
    {
        var root = GetAgentConfigurationRoot();
        VerifyAgentConfigurationPathAncestors();
        if (!Directory.Exists(root)) return;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("網站策略 Agent 配置根目录是重解析点；拒绝删除。" );
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (!Regex.IsMatch(Path.GetFileName(directory), "^[0-9A-Fa-f]{24}$", RegexOptions.CultureInvariant) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"网站策略 Agent 配置目录无法确认归属：{directory}；拒绝删除。" );
            var hash = Path.GetFileName(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("网站策略 Agent 配置目录含重解析点；拒绝递归删除。" );
                var name = Path.GetFileName(entry);
                var expectedConfig = string.Equals(name, "agent-" + hash + ".json", StringComparison.OrdinalIgnoreCase);
                var expectedIdentity = string.Equals(name, StudentAgentIdentityKeyStore.FileName, StringComparison.OrdinalIgnoreCase);
                var updateReplayState = string.Equals(name, "student-update-replay.json", StringComparison.OrdinalIgnoreCase);
                var usageInstallationId = string.Equals(name, "usage-installation-id", StringComparison.OrdinalIgnoreCase);
                var usageLastSentState = string.Equals(name, "usage-installation-id.last-hkt-day", StringComparison.OrdinalIgnoreCase);
                var startupLog = string.Equals(name, "agent-startup.log", StringComparison.OrdinalIgnoreCase);
                var runtimeLog = string.Equals(name, "agent-runtime.log", StringComparison.OrdinalIgnoreCase);
                var temporary = Regex.IsMatch(name, "^agent-" + Regex.Escape(hash) + @"\.json\.tmp-[0-9a-f]{32}$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var identityTemporary = Regex.IsMatch(name, "^" + Regex.Escape(StudentAgentIdentityKeyStore.FileName) +
                    @"\.tmp-[0-9a-f]{32}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!expectedConfig && !expectedIdentity && !updateReplayState && !usageInstallationId &&
                    !usageLastSentState && !startupLog && !runtimeLog && !temporary && !identityTemporary)
                    throw new IOException($"网站策略 Agent 配置目录含无法确认归属的文件：{entry}；拒绝删除。" );
            }
        }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            throw new IOException($"网站策略 Agent 配置根目录含无法确认归属的文件：{file}；拒绝删除。" );
    }

    private static void RemoveAgentConfigurationFiles()
    {
        var root = GetAgentConfigurationRoot();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static List<string> ReadActiveAgentFingerprints()
    {
        var fingerprints = new List<string>();
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) };
        try
        {
            using var response = client.GetAsync("http://127.0.0.1:" + WebsitePolicyAgent.Port + "/health")
                .GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode &&
                response.Headers.TryGetValues("X-VeyonCampus-Agent-Config", out var values))
                fingerprints.AddRange(values.Where(value => Regex.IsMatch(value, "^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException) { }
        return fingerprints;
    }

    private static bool HasAnyAgentHealthResponse() => ReadActiveAgentFingerprints().Count > 0;

    private static bool WaitForNoAgentHealth(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ReadActiveAgentFingerprints().Count == 0) return true;
            Thread.Sleep(200);
        }
        return ReadActiveAgentFingerprints().Count == 0;
    }

    [SupportedOSPlatform("windows")]
    private static void StopManagedAgentProcesses(IEnumerable<string> executablePaths)
    {
        var knownPaths = executablePaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcessesByName("VeyonCampus.Agent"))
        {
            using (process)
            {
                string? executablePath;
                try { executablePath = process.MainModule?.FileName; }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    continue;
                }

                if (executablePath is null || !knownPaths.Contains(Path.GetFullPath(executablePath))) continue;
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(5000))
                        throw new IOException("已确认归属的 VeyonCampus Agent 进程在 5 秒内未停止。" );
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    if (!process.HasExited)
                        throw new IOException("无法停止已确认归属的 VeyonCampus Agent 进程。", exception);
                }
            }
        }
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
            try { task = ((dynamic)folder).GetTask("\\" + taskName); }
            catch (Exception exception) when (IsMissingScheduledTaskException(exception)) { return; }
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

    private static bool WaitForAgentHealth(TimeSpan timeout, string expectedConfigFingerprint)
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
                if (response.IsSuccessStatusCode &&
                    response.Headers.TryGetValues("X-VeyonCampus-Agent-Config", out var fingerprints) &&
                    fingerprints.Contains(expectedConfigFingerprint, StringComparer.OrdinalIgnoreCase))
                    return true;
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
            PathLinkSecurity.RejectLinks(parent);
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

    public static void ReportAgentCompatibilityMigrationFailure(string configPath, Exception exception)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            if (directory is null || !Directory.Exists(directory)) return;
            var details = exception.ToString();
            if (details.Length > 12000) details = details[..12000] + "…";
            AppendBoundedAgentLog(Path.Combine(directory, "agent-runtime.log"),
                $"{DateTimeOffset.UtcNow:O} website-policy-compatibility-migration-failed{Environment.NewLine}" +
                $"{details}{Environment.NewLine}");
        }
        catch (Exception logWriteException) when (logWriteException is IOException or UnauthorizedAccessException or ArgumentException) { }
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
        string signedPolicyJson, string campusId, CancellationToken cancellationToken = default)
    {
        var validated = NormalizeTargets(targets);
        ArgumentException.ThrowIfNullOrWhiteSpace(signedPolicyJson);
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
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
                var nonce = Guid.NewGuid();
                using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
                request.Headers.TryAddWithoutValidation("X-VeyonCampus-Request-Nonce", nonce.ToString("D"));
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var resultText = await ReadBoundedResponseAsync(response.Content,
                    StudentAgentResponseCryptography.MaximumCommandEnvelopeBytes,
                    cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return new WebsitePolicyPushResult(target, false,
                        $"HTTP {(int)response.StatusCode}：{resultText}", NeedsReview: (int)response.StatusCode >= 500);
                try
                {
                    var pinnedKey = new StudentAgentIdentityTrustStore().FindTrustedPublicKey(campusId, target);
                    var verified = StudentAgentCommandAcknowledgementCryptography.Verify(resultText, campusId,
                        nonce, signedPolicyJson, (int)response.StatusCode, pinnedKey, DateTimeOffset.UtcNow,
                        maximumBodyBytes: 8192);
                    if (!verified.MatchesPinnedKey)
                        return new WebsitePolicyPushResult(target, false,
                            $"Agent 身份需先核对；指纹 {verified.Fingerprint}。请点击“核对学生电脑身份”，核对后重新推送。{verified.Payload.Body}",
                            NeedsReview: true, ReportedApplied: verified.Payload.Body == WebsitePolicyAgent.PolicyAppliedAcknowledgement);
                    return new WebsitePolicyPushResult(target, true, verified.Payload.Body);
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or
                                                  UnauthorizedAccessException or CryptographicException)
                {
                    return new WebsitePolicyPushResult(target, false, "Agent 策略回执未通过签名和身份核验：" + exception.Message,
                        NeedsReview: true);
                }
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

    private static async Task<string> ReadBoundedResponseAsync(HttpContent content, int maximumBytes,
        CancellationToken cancellationToken)
    {
        var contentLength = content.Headers.ContentLength;
        if (contentLength is > 0 && contentLength > maximumBytes)
            throw new InvalidDataException("学生端 HTTP 响应超过大小限制。");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var chunk = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException("学生端 HTTP 响应超过大小限制。");
            buffer.Write(chunk, 0, read);
        }
        try { return new UTF8Encoding(false, true).GetString(buffer.ToArray()); }
        catch (DecoderFallbackException exception)
        { throw new InvalidDataException("学生端 HTTP 响应不是有效 UTF-8。", exception); }
    }
}

public sealed record WebsitePolicyPushResult(string Target, bool Succeeded, string Detail, bool NeedsReview = false,
    bool ReportedApplied = false)
{
    public string StatusLabel => Succeeded ? "已确认应用" : ReportedApplied ? "学生机报告已应用，身份待核对" : NeedsReview ? "未确认，请检查连接或回执" : "已拒绝";
}

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
    public const string StatusPath = "/v1/status";
    public const string ApplicationPolicyPath = "/v1/application-policy";
    public const string StudentSystemPolicyPath = "/v1/system-policy";
    public const string ApplicationPolicyAuditPath = "/v1/application-policy/audit";
    public const string ApplicationInventoryPath = "/v1/application-inventory";
    public const string StudentAccountsPath = "/v1/student-accounts";
    public const string StudentUpdatePath = "/v1/update";
    public const string PolicyAppliedAcknowledgement =
        "policy applied; 策略已写入 Edge、Chrome 和 Firefox 机器策略。每次推送或取消策略后，请在学生电脑上手动重启这些浏览器；" +
        "Edge/Chrome 可在地址栏打开 edge://restart 或 chrome://restart；代理回执只确认策略已写入，不代表浏览器页面效果已验证；代理不会强制关闭浏览器。";
    private static readonly SemaphoreSlim ApplyGate = new(1, 1);
    private static readonly object StatusNonceGate = new();
    private static readonly Dictionary<Guid, DateTimeOffset> StatusNonces = [];
    private static readonly object CommandNonceGate = new();
    private static readonly Dictionary<Guid, DateTimeOffset> CommandNonces = [];
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    internal static string ConfigFingerprint(WebsitePolicyAgentConfig config) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            config.CampusId + "\n" + config.PublicKeyPem + "\n" + (config.TelemetryEndpoint ?? "") + "\n" +
            config.DeploymentId + "\n" + config.ApplicationVersion + "\n" + (config.ApplicationPolicyPublicKeyPem ?? "") + "\n" +
            (config.StudentSystemPolicyPublicKeyPem ?? ""))));

    public static string GetRuntimeVersion()
    {
        var executablePath = Environment.ProcessPath;
        var version = executablePath is null ? null : Path.GetFileName(Path.GetDirectoryName(executablePath));
        if (version is null) return "unknown";
        try
        {
            _ = ApplicationReleaseClient.CompareVersions(version, version);
            return version;
        }
        catch (InvalidDataException)
        {
            return "unknown";
        }
    }

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
        using var agentIdentityKey = StudentAgentIdentityKeyStore.LoadOrCreateForSystemAgent(configPath);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(config.PublicKeyPem);
        if (rsa.KeySize is < 2048 or > 4096) throw new InvalidDataException("学生网站策略公钥位长无效。");
        WindowsApplicationPolicyAgent? applicationPolicyAgent = null;
        if (config.ApplicationPolicyPublicKeyPem is not null)
            applicationPolicyAgent = WindowsApplicationPolicyAgent.Create(config.CampusId, config.ApplicationPolicyPublicKeyPem);
        else if (config.StudentSystemPolicyPublicKeyPem is not null)
            applicationPolicyAgent = WindowsApplicationPolicyAgent.CreateForSystemPolicy(config.CampusId);
        WindowsStudentSystemPolicyAgent? studentSystemPolicyAgent = null;
        if (config.StudentSystemPolicyPublicKeyPem is not null)
            studentSystemPolicyAgent = new WindowsStudentSystemPolicyAgent(config.CampusId,
                config.StudentSystemPolicyPublicKeyPem, applicationPolicyAgent?.Runtime);
        if (!string.IsNullOrWhiteSpace(config.TelemetryEndpoint))
        {
            AnonymousUsageHeartbeat.ValidateEndpoint(config.TelemetryEndpoint);
            var directory = Path.GetDirectoryName(Path.GetFullPath(configPath))
                            ?? throw new InvalidDataException("学生网站策略代理配置目录无效。");
            _ = AnonymousUsageHeartbeat.RunAsync(config.TelemetryEndpoint,
                Path.Combine(directory, "usage-installation-id"), config.ApplicationVersion ?? "unknown",
                config.DeploymentId, cancellationToken);
        }

        await TryExpirePolicyAsync(configPath, applicationPolicyAgent, studentSystemPolicyAgent, cancellationToken).ConfigureAwait(false);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var listener = new HttpListener();
        listener.Prefixes.Add(ListenPrefix);
        listener.Start();
        _ = Task.Run(() => TryMigrateLegacyChromeUrlListAsync(configPath, shutdown.Token), CancellationToken.None);
        using var shutdownRegistration = shutdown.Token.Register(() =>
        {
            try { listener.Stop(); }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException) { }
        });
        using var expiryTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        var nextRequest = listener.GetContextAsync();
        var nextExpiryCheck = expiryTimer.WaitForNextTickAsync(shutdown.Token).AsTask();
        while (!shutdown.IsCancellationRequested)
        {
            var completed = await Task.WhenAny(nextRequest, nextExpiryCheck).ConfigureAwait(false);
            if (completed == nextRequest)
            {
                HttpListenerContext context;
                try { context = await nextRequest.ConfigureAwait(false); }
                catch (HttpListenerException) when (shutdown.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { break; }
                nextRequest = listener.GetContextAsync();
                _ = HandleAsync(context, configPath, config, applicationPolicyAgent, studentSystemPolicyAgent,
                    agentIdentityKey, shutdown);
                continue;
            }

            try
            {
                if (!await nextExpiryCheck.ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            try { await TryExpirePolicyAsync(configPath, applicationPolicyAgent, studentSystemPolicyAgent, shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            nextExpiryCheck = expiryTimer.WaitForNextTickAsync(shutdown.Token).AsTask();
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task TryExpirePolicyAsync(string configPath, WindowsApplicationPolicyAgent? applicationPolicyAgent,
        WindowsStudentSystemPolicyAgent? studentSystemPolicyAgent,
        CancellationToken cancellationToken)
    {
        await ApplyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WebsitePolicyRegistryStore.ExpireIfDue(DateTimeOffset.UtcNow);
            applicationPolicyAgent?.Runtime.ExpireIfDue(DateTimeOffset.UtcNow);
            studentSystemPolicyAgent?.Runtime.ReadForAudit(DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or TimeoutException or CryptographicException)
        {
            WebsitePolicyAgentInstaller.ReportAgentExpirationFailure(configPath);
        }
        finally { ApplyGate.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private static async Task TryMigrateLegacyChromeUrlListAsync(string configPath, CancellationToken cancellationToken)
    {
        await ApplyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WebsitePolicyRegistryStore.MigrateLegacyChromeUrlList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or TimeoutException or CryptographicException)
        {
            WebsitePolicyAgentInstaller.ReportAgentCompatibilityMigrationFailure(configPath, exception);
        }
        finally { ApplyGate.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private static async Task HandleAsync(HttpListenerContext context, string configPath,
        WebsitePolicyAgentConfig config, WindowsApplicationPolicyAgent? applicationPolicyAgent,
        WindowsStudentSystemPolicyAgent? studentSystemPolicyAgent,
        RSA agentIdentityKey,
        CancellationTokenSource agentShutdown)
    {
        var cancellationToken = agentShutdown.Token;
        using var response = context.Response;
        try
        {
            if (context.Request.HttpMethod == "POST" && IsCommandEndpoint(context.Request.Url?.AbsolutePath))
                AcceptCommandNonce(context.Request, DateTimeOffset.UtcNow);
            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/health")
            {
                response.Headers["X-VeyonCampus-Agent-Config"] = ConfigFingerprint(config);
                response.Headers["X-VeyonCampus-Agent-Version"] = GetRuntimeVersion();
                await RespondAsync(response, 200, "ready", cancellationToken).ConfigureAwait(false);
                return;
            }
            if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath == StatusPath)
            {
                if (context.Request.ContentLength64 is > 8192)
                {
                    await RespondAsync(response, 413, "status request too large", cancellationToken).ConfigureAwait(false);
                    return;
                }
                var signedRequest = await ReadBoundedAsync(context.Request.InputStream, 8192, cancellationToken)
                    .ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                var request = WebsitePolicyStatusCryptography.VerifyRequest(signedRequest, config.PublicKeyPem,
                    config.CampusId, now);
                AcceptStatusNonce(request.Nonce, request.IssuedUtc, now);
                await TryExpirePolicyAsync(configPath, applicationPolicyAgent, studentSystemPolicyAgent, cancellationToken)
                    .ConfigureAwait(false);
                await ApplyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var website = WebsitePolicyRegistryStore.ReadStatus(config.CampusId, now);
                    var appState = applicationPolicyAgent?.Runtime.ReadForAudit(now);
                    var application = applicationPolicyAgent is null || config.ApplicationPolicyPublicKeyPem is null
                        ? null
                        : new ApplicationPolicyReportedState(true, appState?.Revision ?? 0,
                            appState?.Policy.Mode ?? ApplicationPolicyMode.Disabled, appState?.Policy.ExpiresUtc);
                    var systemState = studentSystemPolicyAgent?.Runtime.ReadForAudit(now);
                    var systemPolicy = studentSystemPolicyAgent is null ? null :
                        new StudentSystemPolicyReportedState(true, systemState?.Revision ?? 0,
                            systemState?.Policy.Settings, systemState?.Pending ?? false);
                    var status = new StudentAgentStatusResponse(1, WebsitePolicyStatusCryptography.ResponsePurpose,
                        config.CampusId, request.Nonce, now, GetRuntimeVersion(), ConfigFingerprint(config), website,
                        application, systemPolicy);
                    var bytes = Encoding.UTF8.GetBytes(StudentAgentResponseCryptography.Sign(status, agentIdentityKey));
                    await RespondBytesAsync(response, 200, bytes, "application/json; charset=utf-8", cancellationToken)
                        .ConfigureAwait(false);
                }
                finally { ApplyGate.Release(); }
                return;
            }
            if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath == StudentUpdatePath)
            {
                if (context.Request.ContentLength64 is > StudentApplicationUpdateCryptography.MaximumEnvelopeBytes)
                {
                    await RespondAsync(response, 413, "student update command too large", cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                var signedCommand = await ReadBoundedAsync(context.Request.InputStream,
                    StudentApplicationUpdateCryptography.MaximumEnvelopeBytes, cancellationToken).ConfigureAwait(false);
                await ApplyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var result = await StudentApplicationUpdateProcessor.ProcessAsync(signedCommand, configPath, config,
                        cancellationToken).ConfigureAwait(false);
                    var signedResult = StudentAgentResponseCryptography.Sign(result.Response, agentIdentityKey);
                    await RespondBytesAsync(response, result.AgentRestartPending ? 202 : 200,
                        Encoding.UTF8.GetBytes(signedResult), "application/json; charset=utf-8",
                        CancellationToken.None).ConfigureAwait(false);
                    if (result.AgentRestartPending)
                    {
                        response.Close();
                        agentShutdown.Cancel();
                    }
                }
                finally { ApplyGate.Release(); }
                return;
            }
            if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath == ApplicationPolicyPath)
            {
                if (applicationPolicyAgent is null || config.ApplicationPolicyPublicKeyPem is null)
                {
                    await RespondAsync(response, 404, "application policy is not enabled for this package", cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (context.Request.ContentLength64 is > ApplicationPolicyCompiler.MaximumPayloadBytes * 2)
                {
                    await RespondAsync(response, 413, "application policy too large", cancellationToken).ConfigureAwait(false);
                    return;
                }
                var signedPolicy = await ReadBoundedAsync(context.Request.InputStream,
                    ApplicationPolicyCompiler.MaximumPayloadBytes * 2, cancellationToken).ConfigureAwait(false);
                await ApplyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    applicationPolicyAgent.Runtime.Apply(signedPolicy, DateTimeOffset.UtcNow);
                    var state = applicationPolicyAgent.Runtime.ReadForAudit(DateTimeOffset.UtcNow);
                    await RespondCommandAcknowledgementAsync(response, context.Request, 200,
                        $"application policy accepted; revision={state?.Revision}; mode={state?.Policy.Mode}",
                        config.CampusId, signedPolicy, agentIdentityKey, cancellationToken).ConfigureAwait(false);
                }
                finally { ApplyGate.Release(); }
                return;
            }
            if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath == StudentSystemPolicyPath)
            {
                if (studentSystemPolicyAgent is null)
                {
                    await RespondAsync(response, 404, "student system policy is not enabled for this package", cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (context.Request.ContentLength64 is > StudentSystemPolicyCompiler.MaximumPayloadBytes * 2)
                {
                    await RespondAsync(response, 413, "student system policy too large", cancellationToken).ConfigureAwait(false);
                    return;
                }
                var signedPolicy = await ReadBoundedAsync(context.Request.InputStream,
                    StudentSystemPolicyCompiler.MaximumPayloadBytes * 2, cancellationToken).ConfigureAwait(false);
                await ApplyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var state = studentSystemPolicyAgent.Runtime.Apply(signedPolicy, DateTimeOffset.UtcNow);
                    await RespondCommandAcknowledgementAsync(response, context.Request, 200,
                        $"system policy accepted; revision={state.Revision}; settings are locally read back, not independently verified",
                        config.CampusId, signedPolicy, agentIdentityKey, cancellationToken).ConfigureAwait(false);
                }
                finally { ApplyGate.Release(); }
                return;
            }
            if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath == ApplicationPolicyAuditPath)
            {
                if (applicationPolicyAgent is null || config.ApplicationPolicyPublicKeyPem is null)
                {
                    await RespondAsync(response, 404, "application policy is not enabled for this package", cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (context.Request.ContentLength64 is > 8192)
                {
                    await RespondAsync(response, 413, "application audit request too large", cancellationToken).ConfigureAwait(false);
                    return;
                }
                var auditRequest = await ReadBoundedAsync(context.Request.InputStream, 8192, cancellationToken).ConfigureAwait(false);
                await ApplyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var audit = applicationPolicyAgent.ReadAudit(auditRequest, config.ApplicationPolicyPublicKeyPem,
                        DateTimeOffset.UtcNow);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(audit, JsonOptions);
                    await RespondCommandAcknowledgementAsync(response, context.Request, 200,
                        Encoding.UTF8.GetString(bytes), config.CampusId, auditRequest, agentIdentityKey, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally { ApplyGate.Release(); }
                return;
            }
            if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath is ApplicationInventoryPath or StudentAccountsPath)
            {
                if (applicationPolicyAgent is null || config.ApplicationPolicyPublicKeyPem is null)
                {
                    await RespondAsync(response, 404, "application policy is not enabled for this package", cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (context.Request.ContentLength64 is > 8192)
                {
                    await RespondAsync(response, 413, "application inventory request too large", cancellationToken).ConfigureAwait(false);
                    return;
                }
                var inventoryRequest = await ReadBoundedAsync(context.Request.InputStream, 8192, cancellationToken).ConfigureAwait(false);
                await ApplyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var accountsOnly = context.Request.Url?.AbsolutePath == StudentAccountsPath;
                    var inventory = await Task.Run(() => accountsOnly
                        ? applicationPolicyAgent.ReadStudentAccounts(inventoryRequest, config.ApplicationPolicyPublicKeyPem, DateTimeOffset.UtcNow)
                        : applicationPolicyAgent.ReadInventory(inventoryRequest, config.ApplicationPolicyPublicKeyPem, DateTimeOffset.UtcNow, cancellationToken), cancellationToken)
                        .ConfigureAwait(false);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(inventory, JsonOptions);
                    if (bytes.Length > 512 * 1024) throw new InvalidDataException("应用清单结果超过大小限制。");
                    await RespondCommandAcknowledgementAsync(response, context.Request, 200,
                        Encoding.UTF8.GetString(bytes), config.CampusId, inventoryRequest, agentIdentityKey,
                        cancellationToken).ConfigureAwait(false);
                }
                finally { ApplyGate.Release(); }
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
            await RespondCommandAcknowledgementAsync(response, context.Request, 200, PolicyAppliedAcknowledgement,
                config.CampusId, body, agentIdentityKey, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            await RespondAsync(response, 409, exception.Message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException or InvalidOperationException or
                                          CryptographicException or HttpRequestException or OperationCanceledException or
                                          System.ComponentModel.Win32Exception or TimeoutException)
        {
            await RespondAsync(response, 500, "agent operation failed; check logs and the current target state",
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
        await RespondBytesAsync(response, statusCode, bytes, "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
    }

    private static async Task RespondCommandAcknowledgementAsync(HttpListenerResponse response,
        HttpListenerRequest request, int statusCode, string body, string campusId, string requestBody,
        RSA agentIdentityKey, CancellationToken cancellationToken)
    {
        var nonce = ReadCommandNonce(request);
        if (nonce is null)
        {
            await RespondAsync(response, statusCode, body, cancellationToken).ConfigureAwait(false);
            return;
        }
        var signed = StudentAgentCommandAcknowledgementCryptography.Sign(campusId, nonce.Value, requestBody,
            statusCode, body, agentIdentityKey);
        await RespondBytesAsync(response, statusCode, Encoding.UTF8.GetBytes(signed),
            "application/json; charset=utf-8", cancellationToken).ConfigureAwait(false);
    }

    private static void AcceptCommandNonce(HttpListenerRequest request, DateTimeOffset nowUtc)
    {
        var nonce = ReadCommandNonce(request);
        if (nonce is null) return;
        lock (CommandNonceGate)
        {
            var cutoff = nowUtc.ToUniversalTime().Subtract(TimeSpan.FromMinutes(5));
            foreach (var expired in CommandNonces.Where(item => item.Value < cutoff).Select(item => item.Key).ToArray())
                CommandNonces.Remove(expired);
            if (CommandNonces.Count >= 4096)
                throw new InvalidDataException("教师命令请求过多；稍后重试。");
            if (!CommandNonces.TryAdd(nonce.Value, nowUtc.ToUniversalTime()))
                throw new InvalidDataException("学生端拒绝重复的教师命令随机数。");
        }
    }

    private static Guid? ReadCommandNonce(HttpListenerRequest request)
    {
        var nonces = request.Headers.GetValues("X-VeyonCampus-Request-Nonce");
        if (nonces is null) return null;
        if (nonces.Length != 1 || !Guid.TryParseExact(nonces[0], "D", out var nonce) || nonce == Guid.Empty)
            throw new InvalidDataException("教师命令随机数格式无效。");
        return nonce;
    }

    private static bool IsCommandEndpoint(string? path) => path is PolicyPath or ApplicationPolicyPath or
        StudentSystemPolicyPath or ApplicationPolicyAuditPath or ApplicationInventoryPath or StudentAccountsPath;

    private static void AcceptStatusNonce(Guid nonce, DateTimeOffset issuedUtc, DateTimeOffset nowUtc)
    {
        lock (StatusNonceGate)
        {
            var cutoff = nowUtc.ToUniversalTime().Subtract(WebsitePolicyStatusCryptography.MaximumRequestAge);
            foreach (var expired in StatusNonces.Where(item => item.Value < cutoff).Select(item => item.Key).ToArray())
                StatusNonces.Remove(expired);
            if (StatusNonces.Count >= 4096)
                throw new InvalidDataException("学生端状态读取请求过多；稍后重试。");
            if (!StatusNonces.TryAdd(nonce, issuedUtc))
                throw new InvalidDataException("学生端拒绝重复的状态读取请求。");
        }
    }

    private static async Task RespondBytesAsync(HttpListenerResponse response, int statusCode, byte[] bytes,
        string contentType, CancellationToken cancellationToken)
    {
        response.StatusCode = statusCode;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Writes policy-owned Edge, Chrome and Firefox HKLM values and refuses pre-existing policy.</summary>
[SupportedOSPlatform("windows")]
public static class WebsitePolicyRegistryStore
{
    private sealed record Browser(string Name, string PolicyKey, string BlocklistName, string AllowlistName);
    private static readonly Browser[] Browsers =
    [
        new("Edge", @"SOFTWARE\Policies\Microsoft\Edge", "URLBlocklist", "URLAllowlist"),
        new("Chrome", @"SOFTWARE\Policies\Google\Chrome", "URLBlocklist", "URLAllowlist"),
        new("Firefox", @"SOFTWARE\Policies\Mozilla\Firefox\WebsiteFilter", "Block", "Exceptions")
    ];
    private const string AgentKey = @"SOFTWARE\VeyonCampus\WebsitePolicy";

    [SupportedOSPlatform("windows")]
    public static long ReadRevision(string campusId)
    {
        EnsureWindows();
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        ReconcilePendingTransaction();
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(AgentKey, writable: false);
        var storedCampus = key?.GetValue("CampusId") as string;
        if (storedCampus is not null && !string.Equals(storedCampus, campusId, StringComparison.Ordinal))
            throw new InvalidDataException("学生网站策略注册表状态属于其他校区；拒绝跨校区应用策略。");
        return key?.GetValue("Revision") is long revision ? revision : 0L;
    }

    [SupportedOSPlatform("windows")]
    public static WebsitePolicyReportedState ReadStatus(string campusId, DateTimeOffset nowUtc)
    {
        EnsureWindows();
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        ReconcilePendingTransaction();
        using (var backend = new WindowsWebsitePolicyRegistryTransactionBackend())
            ValidateCurrentOwnership(backend.ReadSnapshot(), campusId);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(AgentKey, writable: false);
        if (key is null) return new WebsitePolicyReportedState(0, WebsitePolicyMode.Disabled, null, null);
        var storedCampus = key.GetValue("CampusId", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (storedCampus is not string campus)
        {
            if (key.GetValueNames().Length == 0 && key.SubKeyCount == 0)
                return new WebsitePolicyReportedState(0, WebsitePolicyMode.Disabled, null, null);
            throw new InvalidDataException("学生网站策略状态缺少校区标识。");
        }
        if (!string.Equals(campus, campusId, StringComparison.Ordinal))
            throw new InvalidDataException("学生网站策略状态属于其他校区。");
        if (key.GetValue("Revision", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not long revision ||
            revision < 0 || key.GetValue("Mode", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string modeText ||
            !Enum.TryParse<WebsitePolicyMode>(modeText, ignoreCase: false, out var mode) || !Enum.IsDefined(mode))
            throw new InvalidDataException("学生网站策略版本或模式状态无效。");
        var expires = ReadUtc("ExpiresUtc");
        var expired = ReadUtc("ExpiredUtc");
        if (mode == WebsitePolicyMode.Disabled && expires is not null)
            throw new InvalidDataException("已解除的网站策略仍带有到期时间。");
        if (expires is { } until && until <= nowUtc.ToUniversalTime() && expired is not null)
            throw new InvalidDataException("学生网站策略同时标记为到期和仍有活动期限。");
        return new WebsitePolicyReportedState(revision, mode, expires, expired);

        DateTimeOffset? ReadUtc(string valueName)
        {
            var raw = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (raw is null) return null;
            if (raw is not string text || !DateTimeOffset.TryParseExact(text, "O",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ||
                parsed.Offset != TimeSpan.Zero)
                throw new InvalidDataException($"学生网站策略 {valueName} 状态无效。");
            return parsed;
        }
    }

    [SupportedOSPlatform("windows")]
    public static string? ReadCampusForAgentRemoval()
    {
        EnsureWindows();
        ReconcilePendingTransaction();
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(AgentKey, writable: false);
        if (key is null) return null;
        var value = key.GetValue("CampusId", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is string campus && !string.IsNullOrWhiteSpace(campus)) return campus;
        if (key.GetValueNames().Length == 0 && key.GetSubKeyNames().Length == 0) return null;
        throw new IOException("网站策略代理注册表状态缺少有效校区标识；为避免清理其他策略，卸载已停止。" );
    }

    [SupportedOSPlatform("windows")]
    public static bool HasAgentState()
    {
        EnsureWindows();
        ReconcilePendingTransaction();
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(AgentKey, writable: false);
        return key is not null;
    }

    [SupportedOSPlatform("windows")]
    public static void RemoveEmptyAgentState()
    {
        EnsureWindows();
        ReconcilePendingTransaction();
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(AgentKey, writable: false);
        if (key is null) return;
        if (key.GetValueNames().Length != 0 || key.GetSubKeyNames().Length != 0)
            throw new IOException("网站策略注册表并非空状态；没有删除无法确认归属的记录。" );
        foreach (var browser in Browsers)
        {
            using var policy = root.OpenSubKey(browser.PolicyKey, writable: false);
            if (policy is null) continue;
            ReadBrowserList(policy, browser.BlocklistName, out var blockExists);
            ReadBrowserList(policy, browser.AllowlistName, out var allowExists);
            if (blockExists || allowExists)
                throw new IOException($"{browser.Name} 存在网址策略，但本工具没有所有权记录；没有删除该策略或空状态。" );
        }
        root.DeleteSubKey(AgentKey, throwOnMissingSubKey: false);
    }

    [SupportedOSPlatform("windows")]
    public static void VerifyCanRemoveOwnedState(string campusId)
    {
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(campusId);
        ReconcilePendingTransaction();
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        ValidateOwnedState(root, campusId);
    }

    [SupportedOSPlatform("windows")]
    public static void RemoveOwnedState(string campusId)
    {
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(campusId);
        ReconcilePendingTransaction();
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        ValidateOwnedState(root, campusId);
        using var backend = new WindowsWebsitePolicyRegistryTransactionBackend();
        var before = backend.ReadSnapshot();
        var after = new WebsitePolicyRegistrySnapshot(true, EmptyValue(), EmptyValue(), EmptyValue(), EmptyValue(), EmptyValue(),
            EmptyBrowser(), EmptyBrowser());
        WebsitePolicyRegistryTransactions.Commit(backend, WebsitePolicyRegistryTransactionOperation.Remove, before, after);
    }

    private static void ValidateOwnedState(RegistryKey root, string campusId)
    {
        using var agentKey = root.OpenSubKey(AgentKey, writable: false);
        if (agentKey is null)
        {
            foreach (var browser in Browsers)
            {
                using var policy = root.OpenSubKey(browser.PolicyKey, writable: false);
                if (policy is null) continue;
                ReadBrowserList(policy, browser.BlocklistName, out var blockExists);
                ReadBrowserList(policy, browser.AllowlistName, out var allowExists);
                if (blockExists || allowExists)
                    throw new IOException($"{browser.Name} 存在网址策略，但找不到本工具的所有权记录；为避免删除外部策略，卸载已停止。" );
            }
            return;
        }

        var allowedAgentValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "CampusId", "Revision", "Mode", "ExpiresUtc", "ExpiredUtc", "PendingTransactionJson" };
        if (agentKey.GetValueNames().Any(name => !allowedAgentValues.Contains(name)) ||
            agentKey.GetSubKeyNames().Any(name => !string.Equals(name, "Managed", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("网站策略代理注册表包含未知值或子项；卸载没有修改浏览器策略。" );

        var storedCampusValue = agentKey.GetValue("CampusId", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (storedCampusValue is not string storedCampus || !string.Equals(storedCampus, campusId, StringComparison.Ordinal))
            throw new IOException("网站策略所有权记录与当前 Agent 校区不一致；卸载没有修改浏览器策略。" );
        if (agentKey.GetValue("Revision", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } revision && revision is not long ||
            agentKey.GetValue("Mode", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } mode && mode is not string ||
            agentKey.GetValue("ExpiresUtc", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } expires && expires is not string ||
            agentKey.GetValue("ExpiredUtc", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } expired && expired is not string)
            throw new IOException("网站策略所有权元数据格式无效；卸载没有修改浏览器策略。" );

        using var managedRoot = agentKey.OpenSubKey("Managed", writable: false);
        if (managedRoot is not null && managedRoot.GetSubKeyNames().Any(name =>
                !Browsers.Any(browser => string.Equals(browser.Name, name, StringComparison.OrdinalIgnoreCase))))
            throw new IOException("网站策略所有权记录包含未知浏览器；卸载没有修改浏览器策略。" );

        foreach (var browser in Browsers)
        {
            using var policy = root.OpenSubKey(browser.PolicyKey, writable: false);
            using var managed = managedRoot?.OpenSubKey(browser.Name, writable: false);
            if (managed is not null && (managed.SubKeyCount != 0 || managed.GetValueNames().Any(name =>
                    name is not ("Initialized" or "URLBlocklist" or "URLAllowlist"))))
                throw new IOException($"{browser.Name} 网站策略所有权记录包含未知内容；卸载没有修改浏览器策略。" );

            var initializedRaw = managed?.GetValue("Initialized", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (initializedRaw is not null && initializedRaw is not int)
                throw new IOException($"{browser.Name} 网站策略所有权标记格式无效；卸载没有修改浏览器策略。" );
            var initialized = initializedRaw is int initializedValue && initializedValue == 1;
            if (initializedRaw is int value && value is not (0 or 1))
                throw new IOException($"{browser.Name} 网站策略所有权标记未知；卸载没有修改浏览器策略。" );

            var ownedBlock = managed is null ? Array.Empty<string>() : ReadManagedList(managed, "URLBlocklist");
            var ownedAllow = managed is null ? Array.Empty<string>() : ReadManagedList(managed, "URLAllowlist");
            var blockExists = false;
            var allowExists = false;
            var currentBlock = policy is null ? Array.Empty<string>() : ReadBrowserList(policy, browser.BlocklistName, out blockExists);
            var currentAllow = policy is null ? Array.Empty<string>() : ReadBrowserList(policy, browser.AllowlistName, out allowExists);
            var actualBlockExists = policy is not null && blockExists;
            var actualAllowExists = policy is not null && allowExists;
            if (initialized)
            {
                if (!ListsEqual(currentBlock, ownedBlock) || !ListsEqual(currentAllow, ownedAllow) ||
                    actualBlockExists != (ownedBlock.Length > 0) || actualAllowExists != (ownedAllow.Length > 0))
                    throw new IOException($"检测到 {browser.Name} 网站策略已被外部修改；卸载没有删除该浏览器策略。" );
            }
            else if (ownedBlock.Length > 0 || ownedAllow.Length > 0 || actualBlockExists || actualAllowExists)
                throw new IOException($"无法确认 {browser.Name} 网址策略由本工具管理；卸载没有修改浏览器策略。" );
        }
    }

    [SupportedOSPlatform("windows")]
    public static bool ExpireIfDue(DateTimeOffset nowUtc)
    {
        EnsureWindows();
        using var backend = new WindowsWebsitePolicyRegistryTransactionBackend();
        WebsitePolicyRegistryTransactions.Reconcile(backend);
        var before = backend.ReadSnapshot();
        if (!before.AgentKeyExists || !before.ExpiresUtc.Exists) return false;

        ValidateCurrentOwnership(before, before.CampusId.Value);
        if (!DateTimeOffset.TryParseExact(before.ExpiresUtc.Value, "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var expiresUtc) || expiresUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("学生网站策略到期时间记录无效；没有清除浏览器策略。" );
        if (expiresUtc > nowUtc.ToUniversalTime()) return false;
        if (before.Mode.Value is not (nameof(WebsitePolicyMode.Blocklist) or nameof(WebsitePolicyMode.Allowlist)))
            throw new InvalidDataException("已到期的网站策略模式无法确认；没有清除浏览器策略。" );

        var after = before with
        {
            Mode = TextValue(WebsitePolicyMode.Disabled.ToString()),
            ExpiresUtc = EmptyValue(),
            ExpiredUtc = TextValue(nowUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            Edge = ClearedBrowserPolicy(before.Edge),
            Chrome = ClearedBrowserPolicy(before.Chrome),
            Firefox = ClearedBrowserPolicy(WebsitePolicyRegistryTransactions.FirefoxSnapshot(before))
        };
        WebsitePolicyRegistryTransactions.Commit(backend, WebsitePolicyRegistryTransactionOperation.Expire, before, after);
        return true;
    }

    [SupportedOSPlatform("windows")]
    public static bool MigrateLegacyChromeUrlList()
    {
        EnsureWindows();
        using var backend = new WindowsWebsitePolicyRegistryTransactionBackend();
        WebsitePolicyRegistryTransactions.Reconcile(backend);
        var before = backend.ReadSnapshot();
        ValidateCurrentOwnership(before, before.CampusId.Exists ? before.CampusId.Value : null);
        var after = WebsitePolicyRegistryTransactions.CreateLegacyChromeUrlListMigrationTarget(before);
        if (WebsitePolicyRegistryTransactions.Equivalent(before, after)) return false;
        WebsitePolicyRegistryTransactions.Commit(backend,
            WebsitePolicyRegistryTransactionOperation.MigrateChromeUrlList, before, after);
        return true;
    }

    [SupportedOSPlatform("windows")]
    public static void Apply(WebsitePolicyDocument document)
    {
        EnsureWindows();
        // These browsers use different URL-pattern grammars. Compile equivalent
        // teacher-entered domain coverage for each browser.
        var edgeCompiled = WebsitePolicyCompiler.CompileForEdge(document);
        var chromeCompiled = WebsitePolicyCompiler.CompileForChrome(document);
        var firefoxCompiled = WebsitePolicyCompiler.CompileForFirefox(document);
        if (document.ExpiresUtc is { } expiresUtc && expiresUtc <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("网站限制策略已到期；没有修改浏览器策略。" );

        using var backend = new WindowsWebsitePolicyRegistryTransactionBackend();
        WebsitePolicyRegistryTransactions.Reconcile(backend);
        var before = backend.ReadSnapshot();
        ValidateCurrentOwnership(before, document.CampusId);
        var currentRevision = before.Revision.Exists
            ? long.Parse(before.Revision.Value!, System.Globalization.CultureInfo.InvariantCulture)
            : 0L;
        if (document.Revision <= currentRevision)
            throw new InvalidDataException("网站策略版本已过期或重复；学生端拒绝重放。");

        var after = CreateAppliedSnapshot(before, document, edgeCompiled, chromeCompiled, firefoxCompiled);
        WebsitePolicyRegistryTransactions.Commit(backend, WebsitePolicyRegistryTransactionOperation.Apply, before, after);
    }

    private static void ReconcilePendingTransaction()
    {
        using var backend = new WindowsWebsitePolicyRegistryTransactionBackend();
        WebsitePolicyRegistryTransactions.Reconcile(backend);
    }

    private static void ValidateCurrentOwnership(WebsitePolicyRegistrySnapshot snapshot, string? expectedCampusId)
    {
        var hasMetadata = snapshot.CampusId.Exists || snapshot.Revision.Exists || snapshot.Mode.Exists ||
                          snapshot.ExpiresUtc.Exists || snapshot.ExpiredUtc.Exists;
        var hasOwnedState = HasOwnedState(snapshot.Edge) || HasOwnedState(snapshot.Chrome) ||
                            HasOwnedState(WebsitePolicyRegistryTransactions.FirefoxSnapshot(snapshot));
        if (expectedCampusId is not null)
        {
            WebsitePolicySigningKeyStore.ValidateCampusId(expectedCampusId);
            if (snapshot.CampusId.Exists && !string.Equals(snapshot.CampusId.Value, expectedCampusId, StringComparison.Ordinal))
                throw new InvalidDataException("学生网站策略代理已绑定到其他校区；拒绝覆盖。");
        }
        if (hasMetadata || hasOwnedState)
        {
            if (!snapshot.CampusId.Exists || !snapshot.Revision.Exists || !snapshot.Mode.Exists ||
                !long.TryParse(snapshot.Revision.Value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var revision) || revision < 0 ||
                !Enum.TryParse<WebsitePolicyMode>(snapshot.Mode.Value, ignoreCase: false, out var mode) || !Enum.IsDefined(mode))
                throw new InvalidDataException("学生网站策略所有权元数据不完整或无效；没有修改浏览器策略。");
            if (mode == WebsitePolicyMode.Disabled && snapshot.ExpiresUtc.Exists)
                throw new InvalidDataException("已解除的网站策略仍带有到期时间；没有修改浏览器策略。");
            if (snapshot.ExpiresUtc.Exists && snapshot.ExpiredUtc.Exists)
                throw new InvalidDataException("网站策略同时存在活动期限和到期记录；没有修改浏览器策略。");
            ValidateStoredUtc(snapshot.ExpiresUtc);
            ValidateStoredUtc(snapshot.ExpiredUtc);
            if (expectedCampusId is not null && !string.Equals(snapshot.CampusId.Value, expectedCampusId, StringComparison.Ordinal))
                throw new InvalidDataException("学生网站策略代理已绑定到其他校区；拒绝覆盖。");
        }

        ValidateBrowserOwnership(snapshot.Edge, "Edge");
        ValidateBrowserOwnership(snapshot.Chrome, "Chrome");
        ValidateBrowserOwnership(WebsitePolicyRegistryTransactions.FirefoxSnapshot(snapshot), "Firefox");
    }

    private static void ValidateStoredUtc(WebsitePolicyRegistryValueSnapshot value)
    {
        if (!value.Exists) return;
        if (!DateTimeOffset.TryParseExact(value.Value, "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) || parsed.Offset != TimeSpan.Zero)
            throw new InvalidDataException("网站策略期限记录格式无效；没有修改浏览器策略。");
    }

    private static bool HasOwnedState(WebsitePolicyBrowserRegistrySnapshot browser) =>
        browser.ManagedBlocklist.Exists || browser.ManagedAllowlist.Exists || browser.Initialized.Exists;

    private static void ValidateBrowserOwnership(WebsitePolicyBrowserRegistrySnapshot browser, string name)
    {
        if (browser.Initialized.Exists && browser.Initialized.Value is not ("0" or "1"))
            throw new InvalidDataException($"{name} 网站策略所有权标记无效；没有修改浏览器策略。");
        var initialized = browser.Initialized is { Exists: true, Value: "1" };
        if (initialized)
        {
            if (!RegistryListsEqual(browser.PolicyBlocklist, browser.ManagedBlocklist) ||
                !RegistryListsEqual(browser.PolicyAllowlist, browser.ManagedAllowlist) ||
                browser.PolicyBlocklist.Exists != (browser.PolicyBlocklist.Values.Length > 0) ||
                browser.PolicyAllowlist.Exists != (browser.PolicyAllowlist.Values.Length > 0))
                throw new IOException($"检测到 {name} 网站策略被组策略或管理员手动修改；没有覆盖外部策略。");
        }
        else if (browser.PolicyBlocklist.Exists || browser.PolicyAllowlist.Exists ||
                 browser.ManagedBlocklist.Exists || browser.ManagedAllowlist.Exists)
        {
            throw new IOException($"{name} 已有网址策略但本工具没有完整所有权记录；没有接管或覆盖。");
        }
    }

    private static bool RegistryListsEqual(WebsitePolicyRegistryListSnapshot left,
        WebsitePolicyRegistryListSnapshot right) => left.Exists == right.Exists &&
        left.Values.SequenceEqual(right.Values, StringComparer.Ordinal);

    private static WebsitePolicyRegistrySnapshot CreateAppliedSnapshot(WebsitePolicyRegistrySnapshot before,
        WebsitePolicyDocument document, BrowserWebsitePolicy edgeCompiled, BrowserWebsitePolicy chromeCompiled,
        BrowserWebsitePolicy firefoxCompiled)
    {
        var edge = CreateBrowserPolicy(edgeCompiled);
        var chrome = CreateBrowserPolicy(chromeCompiled);
        var firefox = CreateBrowserPolicy(firefoxCompiled);
        return before with
        {
            AgentKeyExists = true,
            CampusId = TextValue(document.CampusId),
            Revision = TextValue(document.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Mode = TextValue(document.Mode.ToString()),
            ExpiresUtc = document.ExpiresUtc is { } expires
                ? TextValue(expires.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture))
                : EmptyValue(),
            ExpiredUtc = EmptyValue(),
            Edge = edge,
            Chrome = chrome,
            Firefox = firefox
        };
    }

    private static WebsitePolicyBrowserRegistrySnapshot CreateBrowserPolicy(BrowserWebsitePolicy compiled)
    {
        var block = CreatePolicyList(compiled.Blocklist);
        var allow = CreatePolicyList(compiled.Allowlist);
        return new WebsitePolicyBrowserRegistrySnapshot(block, allow,
            ClonePolicyList(block), ClonePolicyList(allow), TextValue("1"));
    }

    private static WebsitePolicyBrowserRegistrySnapshot ClearedBrowserPolicy(
        WebsitePolicyBrowserRegistrySnapshot before) => before with
    {
        PolicyBlocklist = CreatePolicyList(Array.Empty<string>()),
        PolicyAllowlist = CreatePolicyList(Array.Empty<string>()),
        ManagedBlocklist = CreatePolicyList(Array.Empty<string>()),
        ManagedAllowlist = CreatePolicyList(Array.Empty<string>())
    };

    private static WebsitePolicyBrowserRegistrySnapshot EmptyBrowser() =>
        new(CreatePolicyList(Array.Empty<string>()), CreatePolicyList(Array.Empty<string>()),
            CreatePolicyList(Array.Empty<string>()), CreatePolicyList(Array.Empty<string>()), EmptyValue());

    private static WebsitePolicyRegistryListSnapshot CreatePolicyList(IReadOnlyList<string> values)
    {
        var copy = values.ToArray();
        return new WebsitePolicyRegistryListSnapshot(copy.Length > 0, copy);
    }

    private static WebsitePolicyRegistryListSnapshot ClonePolicyList(WebsitePolicyRegistryListSnapshot value) =>
        new(value.Exists, value.Values.ToArray());

    private static WebsitePolicyRegistryValueSnapshot TextValue(string value) => new(true, value);
    private static WebsitePolicyRegistryValueSnapshot EmptyValue() => new(false, null);

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

    private static bool ListsEqual(string[] left, string[] right) =>
        left.SequenceEqual(right, StringComparer.Ordinal);

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("浏览器机器策略仅支持 Windows。");
    }
}
