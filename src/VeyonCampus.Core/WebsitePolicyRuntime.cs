using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Runtime.Versioning;
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

/// <summary>Stores teacher signing keys in the current Windows user's certificate store.</summary>
public static class WebsitePolicySigningKeyStore
{
    public static WebsitePolicySigningKey GetOrCreate(string campusId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("教师网站策略密钥仅支持 Windows 用户证书库。");
        ValidateCampusId(campusId);
        var subject = SubjectFor(campusId);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var certificate = Find(store, subject);
        if (certificate is null)
        {
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddYears(20));
            store.Add(created);
            certificate = Find(store, subject) ?? throw new CryptographicException(
                "教师网站策略证书写入 Windows 用户证书库后无法读回。");
        }

        using (certificate)
        {
            var privateKey = certificate.GetRSAPrivateKey();
            if (privateKey is null)
                throw new CryptographicException("教师网站策略证书没有可用私钥；没有创建替代密钥，以免与学生端信任的公钥不匹配。");
            var publicKey = certificate.GetRSAPublicKey()
                            ?? throw new CryptographicException("教师网站策略证书没有可用公钥。");
            return new WebsitePolicySigningKey(privateKey, ExportAndDispose(publicKey), certificate.Thumbprint);
        }
    }

    public static WebsitePolicySigningKey Open(string campusId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("教师网站策略密钥仅支持 Windows 用户证书库。");
        ValidateCampusId(campusId);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        using var certificate = Find(store, SubjectFor(campusId)) ??
            throw new InvalidOperationException("找不到该校区的教师网站策略签名证书。请重新生成学生校区配置包，并将新公钥部署到学生机后再推送。");
        var privateKey = certificate.GetRSAPrivateKey() ??
            throw new CryptographicException("教师网站策略证书私钥不可用；未签发策略。");
        using var publicKey = certificate.GetRSAPublicKey() ??
            throw new CryptographicException("教师网站策略证书公钥不可用；未签发策略。");
        return new WebsitePolicySigningKey(privateKey, publicKey.ExportSubjectPublicKeyInfoPem(), certificate.Thumbprint);
    }

    private static X509Certificate2? Find(X509Store store, string subject)
    {
        var certificates = store.Certificates.Cast<X509Certificate2>()
            .Where(certificate => string.Equals(certificate.Subject, subject, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(certificate => certificate.NotAfter)
            .ToArray();
        if (certificates.Length == 0) return null;
        foreach (var extra in certificates.Skip(1)) extra.Dispose();
        return certificates[0];
    }

    private static string ExportAndDispose(RSA key)
    {
        using (key) return key.ExportSubjectPublicKeyInfoPem();
    }

    private static string SubjectFor(string campusId) =>
        "CN=VeyonCampus Website Policy " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(campusId)))[..24];

    public static void ValidateCampusId(string campusId)
    {
        if (string.IsNullOrWhiteSpace(campusId) || campusId.Length > 100 ||
            campusId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
            throw new InvalidDataException("校区 ID 只能包含 1–100 个英文字母、数字、连字符或下划线。");
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

public sealed record WebsitePolicyAgentConfig(string CampusId, string PublicKeyPem);

/// <summary>Installs the student-only SYSTEM policy receiver and its LAN firewall rule.</summary>
public static class WebsitePolicyAgentInstaller
{
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
            var sourceDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            var sourceExecutable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(sourceExecutable) || !Path.GetFileName(sourceExecutable)
                    .Equals("VeyonCampus.exe", StringComparison.OrdinalIgnoreCase))
                sourceExecutable = Path.Combine(sourceDirectory, "VeyonCampus.exe");
            if (!File.Exists(sourceExecutable))
                throw new FileNotFoundException("找不到 VeyonCampus.exe；不能安装后台网站策略代理。", sourceExecutable);

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var installedDirectory = Path.Combine(programFiles, "VeyonCampus");
            if (!PathEquals(sourceDirectory, installedDirectory))
                InstallApplicationFiles(sourceDirectory, installedDirectory);
            var installedExecutable = Path.Combine(installedDirectory, "VeyonCampus.exe");
            if (!File.Exists(installedExecutable))
                throw new IOException("安装目录中没有 VeyonCampus.exe。");

            var campusHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(package.Campus)))[..24];
            var configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "VeyonCampus", "WebsitePolicy", campusHash);
            Directory.CreateDirectory(configDirectory);
            SecureDirectory(configDirectory);
            var configPath = Path.Combine(configDirectory, "agent-" + campusHash + ".json");
            var config = new WebsitePolicyAgentConfig(package.Campus, canonicalPem);
            var existing = ReadExistingConfig(configPath);
            if (existing is not null && (existing.CampusId != package.Campus ||
                                         !string.Equals(existing.PublicKeyPem, canonicalPem, StringComparison.Ordinal)))
                throw new IOException("网站策略代理已绑定校区或公钥不同；为避免意外更换信任根，没有覆盖现有配置。");
            var configBytes = JsonSerializer.SerializeToUtf8Bytes(config, WebsitePolicyAgent.JsonOptions);
            WriteSecureConfig(configPath, configBytes);

            EnsureUrlReservation();
            EnsureFirewallRule(installedExecutable);
            EnsureScheduledTask(installedExecutable, configPath);
            StartScheduledTask();
            if (!WaitForAgentHealth())
                return new(step, ExecutionPlan.NeedsReview,
                    "SYSTEM 代理任务和网络规则已注册，但没有读到本机代理健康响应；网站推送暂不可用，请检查任务计划程序与防火墙。" );
            return new(step, ExecutionPlan.Succeeded,
                $"学生网站策略代理已安装并以 SYSTEM 身份运行；校区 {package.Campus}，监听端口 {WebsitePolicyAgent.Port}，只部署了教师公钥。" );
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or CryptographicException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or TimeoutException)
        {
            return new(step, ExecutionPlan.NeedsReview,
                $"学生网站策略代理安装或验证未完成：{exception.Message}。请检查已创建的任务、目录和防火墙规则后再重试。" );
        }
    }

    private static void InstallApplicationFiles(string sourceDirectory, string targetDirectory)
    {
        if (Directory.Exists(targetDirectory))
        {
            if (!DirectoryTreesEqual(sourceDirectory, targetDirectory))
                throw new IOException($"程序目录已存在且与当前发布文件不同：{targetDirectory}。为避免覆盖其他版本，请先人工核对并升级。" );
            SecureDirectory(targetDirectory);
            return;
        }

        var staging = targetDirectory + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            CopyDirectoryWithoutLinks(sourceDirectory, staging);
            SecureDirectory(staging);
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

    private static void CopyDirectoryWithoutLinks(string source, string destination)
    {
        var sourceInfo = new DirectoryInfo(source);
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("App 目录包含重解析点；拒绝复制到 SYSTEM 代理目录。" );
        var subdirectories = Directory.EnumerateDirectories(source).ToArray();
        if (subdirectories.Length != 0)
            throw new InvalidDataException("自包含 App 发布目录必须是平面目录；请把学生校区配置包放在 App 目录之外再安装代理。" );
        Directory.CreateDirectory(destination);
        foreach (var file in EnumerateApplicationFiles(source))
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("App 文件包含重解析点；拒绝复制到 SYSTEM 代理目录。" );
            File.Copy(file, Path.Combine(destination, info.Name), overwrite: false);
        }
    }

    private static bool DirectoryTreesEqual(string left, string right)
    {
        if (Directory.EnumerateDirectories(left).Any() || Directory.EnumerateDirectories(right).Any()) return false;
        var leftFiles = EnumerateApplicationFiles(left).Select(Path.GetFileName)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var rightFiles = Directory.EnumerateFiles(right).Select(Path.GetFileName)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!leftFiles.SequenceEqual(rightFiles, StringComparer.OrdinalIgnoreCase)) return false;
        foreach (var relative in leftFiles)
        {
            var leftPath = Path.Combine(left, relative!);
            var rightPath = Path.Combine(right, relative!);
            if ((File.GetAttributes(leftPath) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(rightPath) & FileAttributes.ReparsePoint) != 0 ||
                new FileInfo(leftPath).Length != new FileInfo(rightPath).Length ||
                !string.Equals(HashFile(leftPath), HashFile(rightPath), StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static IEnumerable<string> EnumerateApplicationFiles(string directory) =>
        Directory.EnumerateFiles(directory).Where(path =>
        {
            var name = Path.GetFileName(path);
            if (name.Equals("campus.json", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("README.md", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("admin.txt", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("-public.pem", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("website-policy-public.pem", StringComparison.OrdinalIgnoreCase)) return false;
            return Path.GetExtension(name).ToLowerInvariant() is ".dll" or ".exe" or ".json" or ".config" or ".dat";
        });

    private static WebsitePolicyAgentConfig? ReadExistingConfig(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize<WebsitePolicyAgentConfig>(stream, WebsitePolicyAgent.JsonOptions)
               ?? throw new InvalidDataException("现有学生网站策略代理配置无效。" );
    }

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
        if (query.ExitCode == 0)
        {
            if (query.Stdout.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase) || query.Stdout.Contains("S-1-5-18", StringComparison.OrdinalIgnoreCase)) return;
            throw new IOException("HTTP 监听地址已被其他账户保留；没有更改该 URL ACL。" );
        }
        var add = Run("netsh.exe", ["http", "add", "urlacl", "url=" + url, "user=NT AUTHORITY\\SYSTEM"]);
        if (add.ExitCode != 0)
        {
            var check = Run("netsh.exe", ["http", "show", "urlacl", "url=" + url]);
            if (check.ExitCode != 0 || !(check.Stdout.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase) ||
                                         check.Stdout.Contains("S-1-5-18", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("无法注册 SYSTEM 的 HTTP 监听权限。" );
        }
    }

    private static void EnsureFirewallRule(string executable)
    {
        const string rule = "VeyonCampus Website Policy Agent";
        var query = Run("netsh.exe", ["advfirewall", "firewall", "show", "rule", "name=" + rule]);
        if (query.ExitCode == 0 && query.Stdout.Contains(rule, StringComparison.OrdinalIgnoreCase))
        {
            if (query.Stdout.Contains(WebsitePolicyAgent.Port.ToString(), StringComparison.Ordinal) &&
                query.Stdout.Contains("LocalSubnet", StringComparison.OrdinalIgnoreCase) &&
                query.Stdout.Contains(executable, StringComparison.OrdinalIgnoreCase)) return;
            throw new IOException("同名防火墙规则已存在但设置不匹配；没有覆盖该规则。" );
        }
        var added = Run("netsh.exe", ["advfirewall", "firewall", "add", "rule", "name=" + rule,
            "dir=in", "action=allow", "protocol=TCP", "localport=" + WebsitePolicyAgent.Port,
            "profile=domain,private", "remoteip=LocalSubnet", "program=" + executable, "enable=yes"]);
        if (added.ExitCode != 0) throw new IOException("无法创建仅允许域/专用网络本地子网访问的学生代理防火墙规则。" );
    }

    private static void EnsureScheduledTask(string executable, string configPath)
    {
        const string task = "VeyonCampus-WebsitePolicyAgent";
        var existing = Run("schtasks.exe", ["/Query", "/TN", task, "/XML"]);
        if (existing.ExitCode == 0)
        {
            try
            {
                var xml = XDocument.Parse(existing.Stdout);
                XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                var user = xml.Descendants(ns + "UserId").FirstOrDefault()?.Value;
                var command = xml.Descendants(ns + "Command").FirstOrDefault()?.Value;
                var arguments = xml.Descendants(ns + "Arguments").FirstOrDefault()?.Value ?? "";
                if (user == "S-1-5-18" && PathEquals(command ?? "", executable) &&
                    arguments.Contains("--website-policy-agent", StringComparison.Ordinal) &&
                    arguments.Contains(Quote(configPath), StringComparison.OrdinalIgnoreCase)) return;
            }
            catch (System.Xml.XmlException) { }
            throw new IOException("同名计划任务已存在但不是本项目的 SYSTEM 网站代理；没有覆盖该任务。" );
        }

        var commandLine = Quote(executable) + " --website-policy-agent " + Quote(configPath);
        var created = Run("schtasks.exe", ["/Create", "/TN", task, "/SC", "ONSTART", "/RU", "SYSTEM",
            "/RL", "HIGHEST", "/TR", commandLine, "/F"]);
        if (created.ExitCode != 0) throw new IOException("无法注册 SYSTEM 开机代理任务。" );
    }

    private static void StartScheduledTask()
    {
        var started = Run("schtasks.exe", ["/Run", "/TN", "VeyonCampus-WebsitePolicyAgent"]);
        if (started.ExitCode != 0) throw new IOException("SYSTEM 网站代理任务已注册，但无法立即启动。" );
    }

    private static bool WaitForAgentHealth()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var address = "http://127.0.0.1:" + WebsitePolicyAgent.Port + "/health";
        for (var i = 0; i < 8; i++)
        {
            try
            {
                using var response = client.GetAsync(address).GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            Thread.Sleep(500);
        }
        return false;
    }

    private static void SecureDirectory(string path) => RunIcacls(path, directory: true);
    private static void SecureFile(string path) => RunIcacls(path, directory: false);
    private static void RunIcacls(string path, bool directory)
    {
        var args = new List<string> { path, "/inheritance:r", "/grant:r",
            "*S-1-5-18:" + (directory ? "(OI)(CI)F" : "F"),
            "*S-1-5-32-544:" + (directory ? "(OI)(CI)F" : "F"),
            "*S-1-5-32-545:" + (directory ? "(OI)(CI)RX" : "R") };
        if (directory) { args.Add("/T"); args.Add("/C"); }
        var outcome = Run("icacls.exe", args);
        if (outcome.ExitCode != 0) throw new IOException("无法将代理目录权限限制为 SYSTEM/管理员可写、普通用户只读。" );
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
    public static void ReportAgentStartupFailure(string configPath, string exceptionType)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            if (directory is null || !Directory.Exists(directory)) return;
            var logPath = Path.Combine(directory, "agent-startup.log");
            File.AppendAllText(logPath, $"{DateTimeOffset.UtcNow:O} agent-startup-failed {exceptionType}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
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
                    response.IsSuccessStatusCode ? resultText : $"HTTP {(int)response.StatusCode}：{resultText}");
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException)
            {
                return new WebsitePolicyPushResult(target, false, exception is TaskCanceledException
                    ? "连接超时。请确认学生机已部署代理且位于同一局域网。"
                    : "连接失败。请确认电脑名/IP、防火墙和学生端代理状态。" );
            }
            finally { limit.Release(); }
        }).ToArray();
        return Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false));
    }
}

public sealed record WebsitePolicyPushResult(string Target, bool Succeeded, string Detail);

/// <summary>Long-running SYSTEM HTTP receiver. It has the campus public key, never a teacher private key.</summary>
public sealed class WebsitePolicyAgent
{
    public const int Port = 39173;
    public const string ListenPrefix = "http://+:39173/";
    public const string PolicyPath = "/v1/policy";
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

        using var listener = new HttpListener();
        listener.Prefixes.Add(ListenPrefix);
        listener.Start();
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            _ = HandleAsync(context, config, cancellationToken);
        }
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
            await RespondAsync(response, 200, "policy applied", cancellationToken).ConfigureAwait(false);
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
    public static void Apply(WebsitePolicyDocument document)
    {
        EnsureWindows();
        var compiled = WebsitePolicyCompiler.Compile(document);
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
