using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

public sealed record StudentSystemPolicySettings(
    bool LockWallpaper = true,
    bool ProhibitTimeChanges = true,
    bool ProhibitNetworkChanges = true,
    bool ProhibitSoftwareInstallation = true,
    bool ProhibitAccountManagement = true,
    bool ProhibitControlPanel = false)
{
    public static StudentSystemPolicySettings Default { get; } = new();
    public bool IsEmpty => !LockWallpaper && !ProhibitTimeChanges && !ProhibitNetworkChanges &&
                           !ProhibitSoftwareInstallation && !ProhibitAccountManagement && !ProhibitControlPanel;
}

public sealed record StudentSystemPolicyDocument(
    int SchemaVersion,
    string Purpose,
    string CampusId,
    long Revision,
    DateTimeOffset IssuedUtc,
    IReadOnlyList<string> StudentSids,
    StudentSystemPolicySettings Settings);

public sealed record SignedStudentSystemPolicy(string Payload, string Signature);

/// <summary>Long-lived, separately signed student-machine baseline. It never expires with a lesson policy.</summary>
public static class StudentSystemPolicyCompiler
{
    public const string Purpose = "VeyonCampus.StudentSystemPolicy.v1";
    public const string SignaturePurpose = "VeyonCampus.StudentSystemPolicy.Signature.v1";
    public const int MaximumPayloadBytes = 64 * 1024;
    public const int MaximumStudents = 150;
    internal const string NetworkSettingsPageVisibilityPolicy =
        "hide:network-status;network-advancedsettings;network-airplanemode;proximity;network-cellular;" +
        "network-dialup;network-directaccess;network-ethernet;network-wifisettings;network-mobilehotspot;" +
        "network-proxy;network-vpn;network-wifi;wifi-provisioning";
    internal static IReadOnlyList<string> SettingsPageVisibilitySupportedEditions { get; } = Array.AsReadOnly(new[]
    {
        "Professional", "ProfessionalN", "ProfessionalEducation", "ProfessionalEducationN",
        "ProfessionalWorkstation", "ProfessionalWorkstationN", "Enterprise", "EnterpriseN", "EnterpriseS",
        "EnterpriseSN", "Education", "EducationN", "IoTEnterprise", "IoTEnterpriseS"
    });

    internal static bool SupportsSettingsPageVisibilityEdition(string editionId) =>
        SettingsPageVisibilitySupportedEditions.Contains(editionId, StringComparer.Ordinal);

    internal static bool SupportsDesktopWallpaperPolicyEdition(string editionId) =>
        SettingsPageVisibilitySupportedEditions.Contains(editionId, StringComparer.Ordinal);

    private static readonly Regex StudentSid = new(
        @"^S-1-5-21-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-([1-9][0-9]{0,9})$",
        RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static StudentSystemPolicyDocument Create(string campusId, long revision,
        IEnumerable<string> studentSids, StudentSystemPolicySettings? settings = null,
        DateTimeOffset? issuedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(studentSids);
        return Validate(new StudentSystemPolicyDocument(1, Purpose, campusId, revision,
            (issuedUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(), studentSids.ToArray(),
            settings ?? StudentSystemPolicySettings.Default));
    }

    public static StudentSystemPolicyDocument Validate(StudentSystemPolicyDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        WebsitePolicySigningKeyStore.ValidateCampusId(document.CampusId);
        if (document.SchemaVersion != 1 || document.Purpose != Purpose || document.Revision <= 0 ||
            document.IssuedUtc.Offset != TimeSpan.Zero || document.StudentSids is null ||
            document.StudentSids.Count > MaximumStudents || document.Settings is null)
            throw new InvalidDataException("学生机系统策略版本、校区、时间、账户或设置无效。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sid in document.StudentSids)
        {
            var match = StudentSid.Match(sid ?? "");
            if (!match.Success || match.Groups.Cast<Group>().Skip(1).Any(group =>
                    !uint.TryParse(group.Value, out var value) ||
                    value.ToString(System.Globalization.CultureInfo.InvariantCulture) != group.Value) ||
                !uint.TryParse(match.Groups[4].Value, out var rid) || rid < 1000 || !seen.Add(sid!))
                throw new InvalidDataException("系统策略只能指定唯一的本地学生账户 SID。");
        }
        if (!document.Settings.IsEmpty && document.StudentSids.Count == 0)
            throw new InvalidDataException("启用学生机限制时必须指定至少一个学生账户。");
        return document with { StudentSids = Array.AsReadOnly(document.StudentSids.ToArray()) };
    }

    internal static byte[] Serialize(StudentSystemPolicyDocument document) =>
        JsonSerializer.SerializeToUtf8Bytes(Validate(document), JsonOptions);

    internal static StudentSystemPolicyDocument Deserialize(ReadOnlySpan<byte> payload)
    {
        try
        {
            PolicyJson.RejectDuplicateFields(payload.ToArray());
            var document = JsonSerializer.Deserialize<StudentSystemPolicyDocument>(payload, JsonOptions)
                           ?? throw new InvalidDataException("学生机系统策略正文为空。");
            var validated = Validate(document);
            if (!payload.SequenceEqual(Serialize(validated)))
                throw new InvalidDataException("学生机系统策略正文不是规范 JSON 编码。");
            return validated;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("学生机系统策略 JSON 格式无效。", exception);
        }
    }

    /// <summary>Returns only the exact settings this policy owns. Empty/disabled fields are omitted for restoration.</summary>
    public static IReadOnlyDictionary<string, StudentSystemPolicyValue> DesiredValues(
        StudentSystemPolicyDocument document, string? defaultWallpaperPath = null)
    {
        var policy = Validate(document);
        var result = new SortedDictionary<string, StudentSystemPolicyValue>(StringComparer.Ordinal);
        void User(string sid, string subKey, string name, int value) =>
            result.Add(StudentSystemPolicyResource.UserRegistry(sid, subKey, name), StudentSystemPolicyValue.Dword(value));
        void UserString(string sid, string subKey, string name, string value) =>
            result.Add(StudentSystemPolicyResource.UserRegistry(sid, subKey, name), StudentSystemPolicyValue.String(value));
        foreach (var sid in policy.StudentSids)
        {
            if (policy.Settings.LockWallpaper)
            {
                User(sid, @"Software\Microsoft\Windows\CurrentVersion\Policies\ActiveDesktop", "NoChangingWallPaper", 1);
                if (string.IsNullOrWhiteSpace(defaultWallpaperPath))
                    throw new InvalidDataException("锁定统一 Windows 默认壁纸需要完整图片路径。");
                result.Add(StudentSystemPolicyResource.UserRegistry(sid,
                    @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "Wallpaper"),
                    StudentSystemPolicyValue.String(Path.GetFullPath(defaultWallpaperPath)));
                result.Add(StudentSystemPolicyResource.UserRegistry(sid,
                    @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "WallpaperStyle"),
                    StudentSystemPolicyValue.String("10"));
            }
            if (policy.Settings.ProhibitNetworkChanges)
            {
                var path = @"Software\Policies\Microsoft\Windows\Network Connections";
                User(sid, path, "NC_LanProperties", 1);
                User(sid, path, "NC_AddRemoveComponents", 1);
                User(sid, path, "NC_AllowAdvancedTCPIPConfig", 1);
                User(sid, path, "NC_AdvancedSettings", 1);
                User(sid, path, "NC_NewConnectionWizard", 1);
                User(sid, path, "NC_RasMyProperties", 1);
                User(sid, path, "NC_RasAllUserProperties", 1);
                User(sid, path, "NC_RasChangeProperties", 1);
                User(sid, path, "NC_DeleteConnection", 1);
                User(sid, path, "NC_DeleteAllUserConnection", 1);
                User(sid, path, "NC_RenameConnection", 1);
                User(sid, path, "NC_RenameLanConnection", 1);
                User(sid, path, "NC_RenameMyRasConnection", 1);
                User(sid, path, "NC_RenameAllUserRasConnection", 1);
                // Keep the Settings app itself and network connectivity available, while blocking every
                // documented Settings page used to configure network interfaces, Wi-Fi, VPN, proxy, etc.
                UserString(sid, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer",
                    "SettingsPageVisibility", NetworkSettingsPageVisibilityPolicy);
            }
            if (policy.Settings.ProhibitControlPanel)
                User(sid, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoControlPanel", 1);
            if (policy.Settings.ProhibitAccountManagement)
            {
                // The registry setting hides the UI entry; the account resource enforces the SAM flag.
                User(sid, @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "DisableChangePassword", 1);
                result.Add(StudentSystemPolicyResource.AccountPasswordChangeable(sid), StudentSystemPolicyValue.Boolean(false));
            }
            if (policy.Settings.ProhibitSoftwareInstallation)
                User(sid, @"Software\Policies\Microsoft\WindowsStore", "RemoveWindowsStore", 1);
            if (policy.Settings.ProhibitTimeChanges)
            {
                result.Add(StudentSystemPolicyResource.LsaRight(sid, "SeSystemtimePrivilege"), StudentSystemPolicyValue.Boolean(false));
                result.Add(StudentSystemPolicyResource.LsaRight(sid, "SeTimeZonePrivilege"), StudentSystemPolicyValue.Boolean(false));
            }
        }
        // Machine installation policies must preserve the administrator's maintenance path. Store visibility
        // is scoped to each student hive above; DisableMSI=1 blocks unmanaged installations without disabling
        // managed deployment and repair operations.
        if (policy.StudentSids.Count > 0 && policy.Settings.ProhibitSoftwareInstallation)
        {
            var installer = @"Software\Policies\Microsoft\Windows\Installer";
            result.Add(StudentSystemPolicyResource.MachineRegistry(installer, "DisableMSI"), StudentSystemPolicyValue.Dword(1));
            result.Add(StudentSystemPolicyResource.MachineRegistry(installer, "DisableUserInstalls"), StudentSystemPolicyValue.Dword(1));
            result.Add(StudentSystemPolicyResource.MachineRegistry(@"Software\Policies\Microsoft\Windows\Appx", "BlockNonAdminUserInstall"), StudentSystemPolicyValue.Dword(1));
        }
        return result;
    }
}

public static class StudentSystemPolicyCryptography
{
    private static readonly JsonSerializerOptions EnvelopeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string Sign(StudentSystemPolicyDocument document, RSA privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        if (privateKey.KeySize is < 2048 or > 4096) throw new CryptographicException("系统策略密钥位长不受支持。");
        var payload = StudentSystemPolicyCompiler.Serialize(document);
        if (payload.Length > StudentSystemPolicyCompiler.MaximumPayloadBytes)
            throw new InvalidDataException("学生机系统策略超过大小限制。");
        var signature = privateKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return JsonSerializer.Serialize(new SignedStudentSystemPolicy(Convert.ToBase64String(payload),
            Convert.ToBase64String(signature)), EnvelopeOptions);
    }

    public static StudentSystemPolicyDocument Verify(string envelopeJson, string publicKeyPem,
        string expectedCampusId, long currentRevision, DateTimeOffset? nowUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envelopeJson);
        if (Encoding.UTF8.GetByteCount(envelopeJson) > StudentSystemPolicyCompiler.MaximumPayloadBytes * 2)
            throw new InvalidDataException("学生机系统策略信封超过大小限制。");
        PolicyJson.RejectDuplicateFields(Encoding.UTF8.GetBytes(envelopeJson));
        SignedStudentSystemPolicy envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SignedStudentSystemPolicy>(envelopeJson, EnvelopeOptions)
                       ?? throw new InvalidDataException("学生机系统策略信封为空。");
        }
        catch (JsonException exception) { throw new InvalidDataException("学生机系统策略信封格式无效。", exception); }
        byte[] payload;
        byte[] signature;
        try
        {
            payload = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
            if (Convert.ToBase64String(payload) != envelope.Payload || Convert.ToBase64String(signature) != envelope.Signature)
                throw new FormatException();
        }
        catch (FormatException exception) { throw new InvalidDataException("学生机系统策略信封编码无效。", exception); }
        if (payload.Length is 0 or > StudentSystemPolicyCompiler.MaximumPayloadBytes)
            throw new InvalidDataException("学生机系统策略正文大小无效。");
        using var key = RSA.Create();
        try { key.ImportFromPem(publicKeyPem); }
        catch (CryptographicException exception) { throw new InvalidDataException("系统策略公钥无效。", exception); }
        if (key.KeySize is < 2048 or > 4096 || signature.Length != key.KeySize / 8 ||
            !key.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("学生机系统策略签名无效；未应用限制。");
        var document = StudentSystemPolicyCompiler.Deserialize(payload);
        if (!string.Equals(document.CampusId, expectedCampusId, StringComparison.Ordinal) ||
            document.Revision <= currentRevision || document.IssuedUtc > (nowUtc ?? DateTimeOffset.UtcNow).AddMinutes(1))
            throw new InvalidDataException("学生机系统策略校区、版本或签发时间不匹配。");
        return document;
    }
}

/// <summary>Uses a distinct persisted key namespace and monotonic counter from classroom website/application policies.</summary>
public static class StudentSystemPolicySigningKeyStore
{
    private const string NamespacePrefix = "VeyonCampus-SystemPolicy-";
    public static WebsitePolicySigningKey GetOrCreate(string campusId, bool replaceUnavailableKey = false)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("学生机系统策略签名密钥只支持 Windows。");
        return WebsitePolicySigningKeyStore.GetOrCreate(NamespaceId(campusId), replaceUnavailableKey);
    }
    public static WebsitePolicySigningKey Open(string campusId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("学生机系统策略签名密钥只支持 Windows。");
        return WebsitePolicySigningKeyStore.Open(NamespaceId(campusId));
    }
    public static long NextRevision(string campusId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("学生机系统策略签名密钥只支持 Windows。");
        return WebsitePolicyRevisionStore.Next(NamespaceId(campusId));
    }
    public static bool IsInternalNamespace(string campusId) => campusId.StartsWith(NamespacePrefix, StringComparison.Ordinal);
    private static string NamespaceId(string campusId)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        return NamespacePrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(campusId)));
    }
}

public sealed record StudentSystemPolicyValue(string Kind, string Value)
{
    public static StudentSystemPolicyValue Dword(int value) => new("dword", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static StudentSystemPolicyValue String(string value) => new("string", value);
    public static StudentSystemPolicyValue Boolean(bool value) => new("boolean", value ? "true" : "false");
}

public static class StudentSystemPolicyResource
{
    public static string UserRegistry(string sid, string subKey, string name) => $"user|{sid}|{subKey}|{name}";
    public static string MachineRegistry(string subKey, string name) => $"machine|{subKey}|{name}";
    public static string LsaRight(string sid, string right) => $"lsa|{sid}|{right}";
    public static string AccountPasswordChangeable(string sid) => $"account|{sid}|PasswordChangeable";
}

public sealed record StudentSystemPolicyValueState(bool Exists, StudentSystemPolicyValue? Value);
public sealed record StudentSystemPolicyRuntimeState(string CampusId, long Revision,
    StudentSystemPolicyDocument Policy,
    IReadOnlyDictionary<string, StudentSystemPolicyValueState> OriginalValues,
    IReadOnlyDictionary<string, StudentSystemPolicyValueState> InstalledValues,
    bool Pending = false,
    IReadOnlyDictionary<string, StudentSystemPolicyValueState>? PendingPreviousValues = null,
    IReadOnlyDictionary<string, StudentSystemPolicyValueState>? PendingTargetValues = null);

public interface IStudentSystemPolicyBackend
{
    string DefaultWallpaperPath { get; }
    void VerifyEnvironmentAndStudents(IReadOnlyList<string> studentSids,
        bool requireNetworkSettingsPageVisibility, bool requireDesktopWallpaperPolicy);
    IReadOnlyDictionary<string, StudentSystemPolicyValueState> ReadValues(IReadOnlyCollection<string> resources);
    void WriteValues(IReadOnlyDictionary<string, StudentSystemPolicyValueState> values);
}

public interface IStudentSystemPolicyStateStore
{
    StudentSystemPolicyRuntimeState? Read();
    void Save(StudentSystemPolicyRuntimeState state);
}

/// <summary>Shares ownership of the single local EXE/Appx AppLocker policy with classroom application rules.</summary>
public interface IStudentSoftwareExecutionPolicyCoordinator
{
    void SetStudentSoftwareRestriction(IReadOnlyList<string> studentSids, bool enabled);
}

/// <summary>Transactional, conflict-aware system policy owner. Writes its durable journal before touching Windows.</summary>
public sealed class StudentSystemPolicyRuntime(IStudentSystemPolicyBackend backend,
    IStudentSystemPolicyStateStore store, string campusId, string publicKeyPem,
    IStudentSoftwareExecutionPolicyCoordinator? softwareExecutionPolicy = null)
{
    public StudentSystemPolicyRuntimeState? ReadForAudit(DateTimeOffset nowUtc)
    {
        _ = nowUtc;
        var state = Reconcile();
        if (state is not null)
        {
            SyncSoftwareExecutionPolicy(state.Policy);
            backend.VerifyEnvironmentAndStudents(state.Policy.StudentSids,
                state.Policy.Settings.ProhibitNetworkChanges, state.Policy.Settings.LockWallpaper);
            var current = backend.ReadValues(state.InstalledValues.Keys.ToArray());
            if (!ValuesEqual(current, state.InstalledValues))
                throw new IOException("学生机系统策略值已被外部修改；保留现状并报告冲突。");
        }
        return state;
    }

    public StudentSystemPolicyRuntimeState Apply(string signedEnvelope, DateTimeOffset nowUtc)
    {
        var previous = Reconcile();
        var document = StudentSystemPolicyCryptography.Verify(signedEnvelope, publicKeyPem, campusId,
            previous?.Revision ?? 0, nowUtc);
        backend.VerifyEnvironmentAndStudents(document.StudentSids,
            document.Settings.ProhibitNetworkChanges, document.Settings.LockWallpaper);
        var desired = StudentSystemPolicyCompiler.DesiredValues(document,
            document.Settings.LockWallpaper ? backend.DefaultWallpaperPath : null);
        var oldInstalled = previous?.InstalledValues ?? EmptyValues();
        var oldOriginal = previous?.OriginalValues ?? EmptyValues();
        var resources = oldInstalled.Keys.Concat(desired.Keys).Distinct(StringComparer.Ordinal).ToArray();
        var current = backend.ReadValues(resources);
        foreach (var pair in oldInstalled)
        {
            if (!current.TryGetValue(pair.Key, out var value) || value != pair.Value)
                throw new IOException($"系统策略值 {SafeResourceName(pair.Key)} 已被外部修改；保留现状并停止覆盖。");
        }
        var originals = new SortedDictionary<string, StudentSystemPolicyValueState>(StringComparer.Ordinal);
        foreach (var resource in resources)
            originals[resource] = oldOriginal.TryGetValue(resource, out var oldValue)
                ? oldValue : current.GetValueOrDefault(resource) ?? Missing;
        var target = new SortedDictionary<string, StudentSystemPolicyValueState>(StringComparer.Ordinal);
        foreach (var resource in resources)
            target[resource] = desired.TryGetValue(resource, out var requested)
                ? new StudentSystemPolicyValueState(true, requested)
                : originals[resource];
        var installed = desired.Keys.ToDictionary(key => key,
            key => new StudentSystemPolicyValueState(true, desired[key]), StringComparer.Ordinal);
        var retainedOriginals = desired.Keys.ToDictionary(key => key, key => originals[key], StringComparer.Ordinal);
        var next = new StudentSystemPolicyRuntimeState(campusId, document.Revision, document,
            retainedOriginals, installed, Pending: true, PendingPreviousValues: current, PendingTargetValues: target);
        Commit(next);
        return next with { Pending = false, PendingPreviousValues = null, PendingTargetValues = null };
    }

    public long RestoreForRemoval()
    {
        var state = Reconcile();
        if (state is null) return 0;
        backend.VerifyEnvironmentAndStudents(state.Policy.StudentSids,
            requireNetworkSettingsPageVisibility: false, requireDesktopWallpaperPolicy: false);
        var current = backend.ReadValues(state.InstalledValues.Keys.ToArray());
        foreach (var pair in state.InstalledValues)
            if (!current.TryGetValue(pair.Key, out var value) || value != pair.Value)
                throw new IOException($"系统策略值 {SafeResourceName(pair.Key)} 与工具写入值不同；卸载时保留外部值。");
        var target = state.OriginalValues.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        Commit(state with { Policy = state.Policy with { StudentSids = Array.Empty<string>(),
                Settings = new StudentSystemPolicySettings(false, false, false, false, false, false) },
            InstalledValues = EmptyValues(), Pending = true, PendingPreviousValues = current, PendingTargetValues = target });
        return state.Revision;
    }

    private StudentSystemPolicyRuntimeState? Reconcile()
    {
        var state = store.Read();
        if (state is null) return null;
        ValidateState(state);
        if (!state.Pending) return state;
        var previous = state.PendingPreviousValues ?? throw new InvalidDataException("系统策略待处理事务缺少先前值。");
        var target = state.PendingTargetValues ?? throw new InvalidDataException("系统策略待处理事务缺少目标值。");
        var resources = previous.Keys.Union(target.Keys, StringComparer.Ordinal).ToArray();
        backend.VerifyEnvironmentAndStudents(state.Policy.StudentSids,
            requireNetworkSettingsPageVisibility: false, requireDesktopWallpaperPolicy: false);
        var current = backend.ReadValues(resources);
        if (!ValuesCompatible(current, previous, target))
            throw new IOException("系统策略事务恢复时发现外部修改；未覆盖该值。");
        SyncSoftwareExecutionPolicy(state.Policy);
        if (!ValuesEqual(current, target)) backend.WriteValues(target);
        var readBack = backend.ReadValues(resources);
        if (!ValuesEqual(readBack, target)) throw new IOException("系统策略事务恢复后读回不匹配。");
        state = state with { Pending = false, PendingPreviousValues = null, PendingTargetValues = null };
        store.Save(state);
        return state;
    }

    private void Commit(StudentSystemPolicyRuntimeState state)
    {
        ValidateState(state);
        store.Save(state);
        var previous = state.PendingPreviousValues ?? throw new InvalidDataException("系统策略事务缺少先前值。");
        var target = state.PendingTargetValues ?? throw new InvalidDataException("系统策略事务缺少目标值。");
        var resources = previous.Keys.Union(target.Keys, StringComparer.Ordinal).ToArray();
        if (!ValuesEqual(backend.ReadValues(resources), previous))
            throw new IOException("系统策略写入前发现并发外部变更；未覆盖该值。");
        SyncSoftwareExecutionPolicy(state.Policy);
        backend.WriteValues(target);
        if (!ValuesEqual(backend.ReadValues(resources), target))
            throw new IOException("系统策略写入后读回不匹配；保留待恢复事务。");
        store.Save(state with { Pending = false, PendingPreviousValues = null, PendingTargetValues = null });
    }

    private void ValidateState(StudentSystemPolicyRuntimeState state)
    {
        if (state.CampusId != campusId || state.Revision <= 0 || state.Revision != state.Policy.Revision ||
            state.Policy.CampusId != campusId)
            throw new InvalidDataException("本机系统策略状态校区或版本不匹配。");
        StudentSystemPolicyCompiler.Validate(state.Policy);
        if (!state.OriginalValues.Keys.ToHashSet(StringComparer.Ordinal).IsSupersetOf(state.InstalledValues.Keys))
            throw new InvalidDataException("系统策略原值快照与已安装值不匹配。");
        if (state.Pending && (state.PendingPreviousValues is null || state.PendingTargetValues is null))
            throw new InvalidDataException("系统策略待恢复状态缺少完整事务快照。");
    }

    private void SyncSoftwareExecutionPolicy(StudentSystemPolicyDocument policy) =>
        softwareExecutionPolicy?.SetStudentSoftwareRestriction(policy.StudentSids,
            policy.Settings.ProhibitSoftwareInstallation && policy.StudentSids.Count > 0);

    private static bool ValuesEqual(IReadOnlyDictionary<string, StudentSystemPolicyValueState> left,
        IReadOnlyDictionary<string, StudentSystemPolicyValueState> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
    private static bool ValuesCompatible(IReadOnlyDictionary<string, StudentSystemPolicyValueState> current,
        IReadOnlyDictionary<string, StudentSystemPolicyValueState> previous,
        IReadOnlyDictionary<string, StudentSystemPolicyValueState> target) =>
        current.Count == previous.Count && current.All(pair =>
            previous.TryGetValue(pair.Key, out var before) && target.TryGetValue(pair.Key, out var after) &&
            (pair.Value == before || pair.Value == after));
    private static IReadOnlyDictionary<string, StudentSystemPolicyValueState> EmptyValues() =>
        new Dictionary<string, StudentSystemPolicyValueState>(StringComparer.Ordinal);
    private static readonly StudentSystemPolicyValueState Missing = new(false, null);
    private static string SafeResourceName(string value) => value.Split('|').LastOrDefault() ?? "policy";
}
