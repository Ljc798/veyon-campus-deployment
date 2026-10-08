using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VeyonCampus.Core;

public enum ApplicationPolicyMode { Disabled, Audit, Enforce }
public enum ApplicationRuleKind { Publisher, Hash }

public sealed record ApplicationDenyRule(Guid Id, ApplicationRuleKind Kind, string DisplayName,
    string? PublisherName = null, string? ProductName = null, string? BinaryName = null,
    string? MinimumVersion = null, string? MaximumVersion = null,
    string? FileSha256 = null, string? AppLockerHashSha256 = null,
    string? SourceFileName = null, long? SourceFileLength = null);

public sealed record ApplicationPolicyDocument(int SchemaVersion, string Purpose, string CampusId,
    long Revision, DateTimeOffset IssuedUtc, DateTimeOffset? ExpiresUtc,
    ApplicationPolicyMode Mode, IReadOnlyList<string> StudentSids, IReadOnlyList<ApplicationDenyRule> Rules);

/// <summary>Explicit EXE deny policies. Does not mutate Windows or enable other rule collections.</summary>
public static class ApplicationPolicyCompiler
{
    public const string Purpose = "VeyonCampus.ApplicationPolicy.v1";
    public const int MaximumPayloadBytes = 256 * 1024;
    public const int MaximumRules = 200;
    public const int MaximumStudents = 150;
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(24);
    private static readonly Regex AccountSid = new(@"^S-1-5-21-([0-9]{1,10})-([0-9]{1,10})-([0-9]{1,10})-([0-9]{1,10})$",
        RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ProtectedBinaries = new(StringComparer.OrdinalIgnoreCase)
    {
        // Keep classroom rules from disabling sign-in, shell, settings, servicing, security,
        // and recovery components. This protects rule identities, while Windows allow paths
        // and the runtime-provided AppLocker hashes protect the actual installed files.
        "winlogon.exe", "wininit.exe", "userinit.exe", "logonui.exe", "explorer.exe", "lsass.exe", "services.exe",
        "svchost.exe", "smss.exe", "csrss.exe", "dwm.exe", "sihost.exe", "consent.exe", "msiexec.exe",
        "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "ApplicationFrameHost.exe", "RuntimeBroker.exe",
        "TextInputHost.exe", "ctfmon.exe", "conhost.exe", "dllhost.exe", "taskhostw.exe",
        "SystemSettings.exe", "SystemSettingsAdminFlows.exe", "control.exe",
        "TrustedInstaller.exe", "TiWorker.exe", "MoUsoCoreWorker.exe", "UsoClient.exe", "WaaSMedicAgent.exe",
        "dism.exe", "dismhost.exe", "sfc.exe", "reagentc.exe",
        "MsMpEng.exe", "MpCmdRun.exe", "SecurityHealthService.exe", "SecurityHealthSystray.exe",
        "cmd.exe", "powershell.exe", "pwsh.exe", "rundll32.exe", "regsvr32.exe", "reg.exe", "schtasks.exe", "sc.exe",
        "veyon-service.exe", "veyon-worker.exe", "veyon-master.exe", "veyon-cli.exe",
        "VeyonCampus.Agent.exe", "VeyonCampus.StudentSetup.exe", "VeyonCampus.Teacher.exe",
        "VeyonCampus.Worker.exe", "VeyonCampus.UpdateHelper.exe"
    };
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static ApplicationPolicyDocument Validate(ApplicationPolicyDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        WebsitePolicySigningKeyStore.ValidateCampusId(document.CampusId);
        var internalEmptyPolicy = document.Revision == 0 && document.Mode == ApplicationPolicyMode.Disabled &&
                                  document.StudentSids is { Count: 0 } && document.Rules is { Count: 0 } && document.ExpiresUtc is null;
        if (document.SchemaVersion != 1 || document.Purpose != Purpose || document.Revision < 0 ||
            (document.Revision == 0 && !internalEmptyPolicy) ||
            !Enum.IsDefined(document.Mode) || document.IssuedUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("应用策略版本、用途、签发时间或模式无效。");
        if (document.StudentSids is null || document.Rules is null ||
            document.StudentSids.Count > MaximumStudents || document.Rules.Count > MaximumRules)
            throw new InvalidDataException("应用策略账户或规则数量无效。");
        if (document.Mode == ApplicationPolicyMode.Disabled)
        {
            if (document.Rules.Count != 0 || document.StudentSids.Count != 0 || document.ExpiresUtc is not null)
                throw new InvalidDataException("撤销应用策略必须使用空账户和规则，且不设置期限。");
        }
        else if (document.Rules.Count == 0 || document.StudentSids.Count == 0 ||
                 document.ExpiresUtc is not { } expiry || expiry.Offset != TimeSpan.Zero ||
                 expiry <= document.IssuedUtc || expiry - document.IssuedUtc > MaximumLifetime)
            throw new InvalidDataException("审核或阻止策略需要学生账户、规则和不超过 24 小时的期限。");

        var sids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sid in document.StudentSids)
        {
            if (!IsStandardStudentSid(sid) || !sids.Add(sid!))
                throw new InvalidDataException("目标必须是唯一的普通学生账户 SID，不能使用内置账户或组。");
        }
        var ids = new HashSet<Guid>();
        foreach (var rule in document.Rules)
        {
            if (rule is null || rule.Id == Guid.Empty || !ids.Add(rule.Id) || !Enum.IsDefined(rule.Kind))
                throw new InvalidDataException("应用规则 ID 重复、为空或类型无效。");
            ValidateRule(rule);
        }
        // Freeze lists so subsequent caller edits cannot change a validated policy.
        return document with { StudentSids = Array.AsReadOnly(document.StudentSids.ToArray()),
            Rules = Array.AsReadOnly(document.Rules.ToArray()) };
    }

    public static ApplicationDenyRule ValidateRule(ApplicationDenyRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Id == Guid.Empty || !Enum.IsDefined(rule.Kind))
            throw new InvalidDataException("应用规则 ID 为空或类型无效。");
        ValidateText(rule.DisplayName, 120);
        if (rule.Kind == ApplicationRuleKind.Publisher)
        {
            ValidateText(rule.PublisherName, 512);
            ValidateText(rule.ProductName, 256);
            ValidateExecutableName(rule.BinaryName);
            var minimum = ParseVersion(rule.MinimumVersion);
            var maximum = ParseVersion(rule.MaximumVersion);
            if (minimum > maximum || rule.FileSha256 is not null || rule.AppLockerHashSha256 is not null ||
                rule.SourceFileName is not null || rule.SourceFileLength is not null)
                throw new InvalidDataException("发布者规则版本区间或字段无效。");
        }
        else
        {
            ValidateExecutableName(rule.SourceFileName);
            if (!IsHash(rule.FileSha256) || !IsHash(rule.AppLockerHashSha256) ||
                rule.SourceFileLength is not (> 0 and <= 1024L * 1024 * 1024) ||
                rule.PublisherName is not null || rule.ProductName is not null || rule.BinaryName is not null ||
                rule.MinimumVersion is not null || rule.MaximumVersion is not null)
                throw new InvalidDataException("哈希规则需要文件 SHA-256、Windows AppLocker 哈希及准确长度。");
        }
        return rule;
    }

    public static string CompileXml(ApplicationPolicyDocument document, IReadOnlyCollection<string> protectedAppLockerHashes)
        => CompileXml(document, protectedAppLockerHashes, null, null);

    /// <summary>
    /// Composes the classroom deny rules and the long-lived student software allowlist into one
    /// AppLocker policy. AppLocker has one enforcement mode per collection, so an active software
    /// allowlist stays enforced while classroom Audit rules are held as a preview only.
    /// </summary>
    public static string CompileXml(ApplicationPolicyDocument document, IReadOnlyCollection<string> protectedAppLockerHashes,
        IReadOnlyCollection<string>? softwareRestrictionStudentSids,
        IReadOnlyCollection<string>? softwareRestrictionAllowedSids)
    {
        ArgumentNullException.ThrowIfNull(protectedAppLockerHashes);
        document = Validate(document);
        var root = new XElement("AppLockerPolicy", new XAttribute("Version", "1"));
        var restrictedStudents = softwareRestrictionStudentSids?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        var exemptUsers = softwareRestrictionAllowedSids?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        var hasSoftwareRestriction = restrictedStudents.Length > 0;
        if (softwareRestrictionStudentSids is { Count: > 0 })
        {
            if (restrictedStudents.Length != softwareRestrictionStudentSids.Count ||
                restrictedStudents.Length > MaximumStudents || restrictedStudents.Any(sid => !IsStandardStudentSid(sid)))
                throw new InvalidDataException("软件执行限制的学生 SID 清单无效。");
            if (exemptUsers.Length != (softwareRestrictionAllowedSids?.Count ?? 0) || exemptUsers.Length > 1024 ||
                exemptUsers.Any(sid => !IsAccountSid(sid)) || restrictedStudents.Intersect(exemptUsers, StringComparer.Ordinal).Any())
                throw new InvalidDataException("软件执行限制的非学生账户 SID 清单无效。");
        }
        else if (softwareRestrictionAllowedSids is { Count: > 0 })
            throw new InvalidDataException("没有启用软件执行限制时不能指定豁免账户。");

        if (document.Mode == ApplicationPolicyMode.Disabled && !hasSoftwareRestriction)
            return root.ToString(SaveOptions.DisableFormatting);
        if (document.Mode != ApplicationPolicyMode.Disabled &&
            (protectedAppLockerHashes.Count == 0 || protectedAppLockerHashes.Any(hash => !IsHash(hash))))
            throw new InvalidDataException("必须提供当前及候选更新/恢复组件的 AppLocker 哈希清单。");
        var protectedHashes = new HashSet<string>(protectedAppLockerHashes, StringComparer.OrdinalIgnoreCase);
        if (document.Mode != ApplicationPolicyMode.Disabled &&
            document.Rules.Any(rule => rule.Kind == ApplicationRuleKind.Hash && protectedHashes.Contains(rule.AppLockerHashSha256!)))
            throw new InvalidDataException("拒绝阻止更新、系统或恢复组件的哈希规则。");
        var collection = new XElement("RuleCollection", new XAttribute("Type", "Exe"),
            new XAttribute("EnforcementMode", hasSoftwareRestriction || document.Mode == ApplicationPolicyMode.Enforce
                ? "Enabled" : "AuditOnly"));
        if (hasSoftwareRestriction)
        {
            // Keep core Windows and machine-installed applications available to standard users. The
            // AppLocker path variable PROGRAMFILES covers both native and x86 Program Files folders.
            foreach (var path in new[]
                     {
                         "%WINDIR%\\System32\\*", "%WINDIR%\\SysWOW64\\*", "%WINDIR%\\Microsoft.NET\\*",
                         "%WINDIR%\\SystemApps\\*", "%WINDIR%\\WinSxS\\*", "%PROGRAMFILES%\\*",
                         "%WINDIR%\\explorer.exe"
                     })
                AddPathRule(collection, document.CampusId, "software-path|everyone|" + path,
                    "软件限制：系统与已安装程序目录", "S-1-1-0", "Allow", path);

            // Windows Temp is writable by standard accounts; deny wins over the Windows-folder allows.
            foreach (var sid in restrictedStudents)
                AddPathRule(collection, document.CampusId, "software-temp-deny|" + sid,
                    "软件限制：阻止从 Windows 临时目录启动", sid, "Deny", "%WINDIR%\\Temp\\*");

            foreach (var sid in new[] { "S-1-5-18", "S-1-5-19", "S-1-5-20", "S-1-5-32-544" }.Concat(exemptUsers))
                AddPathRule(collection, document.CampusId, "software-exempt|" + sid,
                    "软件限制：维护账户放行", sid, "Allow", "*");
        }
        else
        {
            // Explicit-deny classroom policies remain opt-in: unrelated EXEs keep running.
            AddPathRule(collection, document.CampusId, "classroom-baseline", "明确阻止模式放行基线",
                "S-1-1-0", "Allow", "*");
        }

        var applyClassroomRules = document.Mode == ApplicationPolicyMode.Enforce ||
                                  (document.Mode == ApplicationPolicyMode.Audit && !hasSoftwareRestriction);
        if (applyClassroomRules)
        foreach (var sid in document.StudentSids)
        foreach (var rule in document.Rules)
        {
            var attributes = Attributes(CompiledRuleId(document.CampusId, sid, rule.Id), rule.DisplayName, sid, "Deny");
            if (rule.Kind == ApplicationRuleKind.Publisher)
                collection.Add(new XElement("FilePublisherRule", attributes, new XElement("Conditions",
                    new XElement("FilePublisherCondition", new XAttribute("PublisherName", rule.PublisherName!),
                        new XAttribute("ProductName", rule.ProductName!), new XAttribute("BinaryName", rule.BinaryName!),
                        new XElement("BinaryVersionRange", new XAttribute("LowSection", rule.MinimumVersion!),
                            new XAttribute("HighSection", rule.MaximumVersion!))))));
            else
                collection.Add(new XElement("FileHashRule", attributes, new XElement("Conditions",
                    new XElement("FileHashCondition", new XElement("FileHash", new XAttribute("Type", "SHA256"),
                        new XAttribute("Data", "0x" + rule.AppLockerHashSha256!.ToUpperInvariant()),
                        new XAttribute("SourceFileName", rule.SourceFileName!), new XAttribute("SourceFileLength", rule.SourceFileLength!))))));
        }

        // AppLocker blocks packaged apps when the EXE collection is enforced unless the Appx
        // collection contains an allow rule. Keep this release scoped to desktop EXEs.
        var appxCollection = new XElement("RuleCollection", new XAttribute("Type", "Appx"),
            new XAttribute("EnforcementMode", "Enabled"),
            new XElement("FilePublisherRule", Attributes(StableId(document.CampusId + "|appx-baseline"),
                "已签名打包应用放行基线", "S-1-1-0", "Allow"),
                new XElement("Conditions", new XElement("FilePublisherCondition",
                    new XAttribute("PublisherName", "*"), new XAttribute("ProductName", "*"),
                    new XAttribute("BinaryName", "*"),
                    new XElement("BinaryVersionRange", new XAttribute("LowSection", "0.0.0.0"),
                        new XAttribute("HighSection", "*"))))));
        root.Add(collection, appxCollection);
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static void AddPathRule(XElement collection, string campusId, string seed, string displayName,
        string sid, string action, string path) =>
        collection.Add(new XElement("FilePathRule", Attributes(StableId(campusId + "|" + seed), displayName, sid, action),
            new XElement("Conditions", new XElement("FilePathCondition", new XAttribute("Path", path)))));

    public static Guid CompiledRuleId(string campusId, string studentSid, Guid ruleId)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        if (!IsStandardStudentSid(studentSid) || ruleId == Guid.Empty)
            throw new InvalidDataException("应用规则审计标识无效。");
        return StableId(campusId + "|" + studentSid + "|" + ruleId);
    }

    internal static bool IsStandardStudentSid(string? sid) => IsCanonicalAccountSid(sid, minimumRid: 1000);

    internal static bool IsAccountSid(string? sid) => IsCanonicalAccountSid(sid, minimumRid: 1);

    private static bool IsCanonicalAccountSid(string? sid, uint minimumRid)
    {
        var match = AccountSid.Match(sid ?? "");
        if (!match.Success) return false;
        for (var index = 1; index <= 4; index++)
        {
            if (!uint.TryParse(match.Groups[index].Value, out var value) ||
                value.ToString(System.Globalization.CultureInfo.InvariantCulture) != match.Groups[index].Value)
                return false;
        }
        return uint.TryParse(match.Groups[4].Value, out var rid) && rid >= minimumRid;
    }

    private static object[] Attributes(Guid id, string name, string sid, string action) =>
        [new XAttribute("Id", id), new XAttribute("Name", "VeyonCampus: " + name),
            new XAttribute("Description", Purpose), new XAttribute("UserOrGroupSid", sid), new XAttribute("Action", action)];
    private static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static Version ParseVersion(string? value)
    {
        if (value is null || !Regex.IsMatch(value, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(value, out var result) || new[] { result.Major, result.Minor, result.Build, result.Revision }.Any(part => part > 65535))
            throw new InvalidDataException("应用版本必须是四段 0–65535 的精确版本号。");
        return result;
    }
    private static void ValidateExecutableName(string? value)
    {
        ValidateText(value, 256);
        if (!value!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || value.IndexOfAny(['/', '\\', ':']) >= 0 || ProtectedBinaries.Contains(value))
            throw new InvalidDataException("规则必须精确指定 EXE 名称，且不能命中系统、更新或恢复组件。");
    }
    private static void ValidateText(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit || value != value.Trim() ||
            value.Any(char.IsControl) || value.Contains('*') || value.Contains('?'))
            throw new InvalidDataException("应用规则文本不能为空、包含通配符或控制字符。");
    }
}

public static class ApplicationPolicyCryptography
{
    public static string Sign(ApplicationPolicyDocument document, RSA applicationPrivateKey)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(ApplicationPolicyCompiler.Validate(document), ApplicationPolicyCompiler.JsonOptions);
        if (payload.Length > ApplicationPolicyCompiler.MaximumPayloadBytes) throw new InvalidDataException("应用策略超过大小限制。");
        return JsonSerializer.Serialize(new SignedWebsitePolicy(Convert.ToBase64String(payload),
            Convert.ToBase64String(applicationPrivateKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))));
    }

    public static ApplicationPolicyDocument Verify(string envelopeJson, string applicationPublicKeyPem,
        string campusId, long currentRevision, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(envelopeJson) || Encoding.UTF8.GetByteCount(envelopeJson) > ApplicationPolicyCompiler.MaximumPayloadBytes * 2)
            throw new InvalidDataException("应用策略信封大小无效。");
        try
        {
            PolicyJson.RejectDuplicateFields(Encoding.UTF8.GetBytes(envelopeJson));
            var envelope = JsonSerializer.Deserialize<SignedWebsitePolicy>(envelopeJson,
                new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
            if (envelope is null || string.IsNullOrWhiteSpace(envelope.Payload) || string.IsNullOrWhiteSpace(envelope.Signature))
                throw new InvalidDataException("应用策略缺少正文或签名。");
            var payload = Convert.FromBase64String(envelope.Payload);
            var signature = Convert.FromBase64String(envelope.Signature);
            if (payload.Length is 0 or > ApplicationPolicyCompiler.MaximumPayloadBytes || signature.Length is < 256 or > 512)
                throw new InvalidDataException("应用策略正文或签名大小无效。");
            PolicyJson.RejectDuplicateFields(payload);
            using var key = RSA.Create();
            key.ImportFromPem(applicationPublicKeyPem);
            if (key.KeySize is < 2048 or > 4096 || !key.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("应用策略签名无效。");
            var document = ApplicationPolicyCompiler.Validate(JsonSerializer.Deserialize<ApplicationPolicyDocument>(payload,
                ApplicationPolicyCompiler.JsonOptions) ?? throw new InvalidDataException("应用策略正文为空。"));
            if (document.CampusId != campusId || document.Revision <= currentRevision ||
                document.IssuedUtc > nowUtc.ToUniversalTime().AddMinutes(1) || document.ExpiresUtc <= nowUtc)
                throw new InvalidDataException("应用策略校区、递增版本或有效时间不符。");
            return document;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or ArgumentException)
        { throw new InvalidDataException("应用策略编码、结构或公钥无效。", ex); }
    }
}

/// <summary>The application policy signing trust is stored under a distinct, one-way campus namespace.</summary>
public static class ApplicationPolicySigningKeyStore
{
    public static ApplicationPolicySigningKey GetOrCreate(string campusId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("应用策略签名密钥仅支持 Windows 用户证书库。");
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var namespaceId = NamespaceFor(campusId);
        var key = WebsitePolicySigningKeyStore.GetOrCreate(namespaceId);
        return new ApplicationPolicySigningKey(key);
    }

    public static ApplicationPolicySigningKey Open(string campusId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("应用策略签名密钥仅支持 Windows 用户证书库。理由：此密钥只在教师 Windows 用户证书库中保存私钥。");
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        return new ApplicationPolicySigningKey(WebsitePolicySigningKeyStore.Open(NamespaceFor(campusId)));
    }

    private static string NamespaceFor(string campusId) => "app-policy-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(campusId))).ToLowerInvariant();

    public static bool IsInternalNamespace(string campusId) => campusId.StartsWith("app-policy-", StringComparison.Ordinal) &&
        campusId.Length == "app-policy-".Length + 64 && campusId["app-policy-".Length..].All(char.IsAsciiHexDigit);
}

public sealed class ApplicationPolicySigningKey : IDisposable
{
    private readonly WebsitePolicySigningKey _key;
    internal ApplicationPolicySigningKey(WebsitePolicySigningKey key) => _key = key;
    public RSA PrivateKey => _key.PrivateKey;
    public string PublicKeyPem => _key.PublicKeyPem;
    public string CertificateThumbprint => _key.CertificateThumbprint;
    public void Dispose() => _key.Dispose();
}
