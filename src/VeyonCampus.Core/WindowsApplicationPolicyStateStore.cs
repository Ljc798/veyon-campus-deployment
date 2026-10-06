using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

/// <summary>Atomic machine-local policy state readable and writable only by SYSTEM and Administrators.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsApplicationPolicyStateStore : IApplicationPolicyStateStore
{
    private const int MaximumStateBytes = 8 * 1024 * 1024;
    private readonly string _campusId;
    private readonly string _directory;
    private readonly string _statePath;
    public string CampusId => _campusId;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public WindowsApplicationPolicyStateStore(string campusId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("应用策略状态存储仅支持 Windows。");
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        _campusId = campusId;
        var campusHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(campusId)));
        _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VeyonCampus", "ApplicationPolicy", campusHash);
        _statePath = Path.Combine(_directory, "state.json");
    }

    public ApplicationPolicyRuntimeState? Read()
    {
        if (!Directory.Exists(_directory)) return null;
        PathLinkSecurity.RejectLinks(_directory);
        VerifyAcl(_directory, directory: true);
        if (!File.Exists(_statePath)) return null;
        PathLinkSecurity.RejectLinks(_statePath);
        VerifyAcl(_statePath, directory: false);
        var info = new FileInfo(_statePath);
        if (info.Length is <= 0 or > MaximumStateBytes) throw new InvalidDataException("应用策略状态文件大小无效。");
        var bytes = File.ReadAllBytes(_statePath);
        if (bytes.Length != info.Length || bytes.Length > MaximumStateBytes)
            throw new InvalidDataException("应用策略状态文件读取期间发生变化。");
        PolicyJson.RejectDuplicateFields(bytes);
        var state = JsonSerializer.Deserialize<ApplicationPolicyRuntimeState>(bytes, JsonOptions)
                    ?? throw new InvalidDataException("应用策略状态文件为空。");
        ValidateState(state);
        return state;
    }

    public static bool HasAnyActiveState()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VeyonCampus", "ApplicationPolicy");
        try
        {
            FileAttributes rootAttributes;
            try { rootAttributes = File.GetAttributes(root); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return false;
            }
            if ((rootAttributes & FileAttributes.Directory) == 0) return true;
            PathLinkSecurity.RejectLinks(root);
            VerifyAcl(root, directory: true);
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                PathLinkSecurity.RejectLinks(directory);
                VerifyAcl(directory, directory: true);
                var statePath = Path.Combine(directory, "state.json");
                FileAttributes stateAttributes;
                try { stateAttributes = File.GetAttributes(statePath); }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    continue;
                }
                if ((stateAttributes & FileAttributes.Directory) != 0) return true;
                PathLinkSecurity.RejectLinks(statePath);
                VerifyAcl(statePath, directory: false);
                var info = new FileInfo(statePath);
                if (info.Length is <= 0 or > MaximumStateBytes) return true;
                var bytes = File.ReadAllBytes(statePath);
                if (bytes.Length != info.Length || bytes.Length > MaximumStateBytes) return true;
                PolicyJson.RejectDuplicateFields(bytes);
                var state = JsonSerializer.Deserialize<ApplicationPolicyRuntimeState>(bytes, JsonOptions);
                if (state is null) return true;
                var store = new WindowsApplicationPolicyStateStore(state.CampusId);
                if (!string.Equals(Path.GetFullPath(statePath), Path.GetFullPath(store._statePath),
                        StringComparison.OrdinalIgnoreCase))
                    return true;
                store.ValidateState(state);
                if (ApplicationPolicyRuntime.RequiresApplicationPolicyCapabilityForUpdate(state)) return true;
            }
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException or System.Text.Json.JsonException or
                                          ArgumentException)
        {
            // Unknown, corrupt, or inaccessible state must block a downgrade until an administrator reviews it.
            return true;
        }
    }

    public void Save(ApplicationPolicyRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state);
        EnsureDirectory();
        if (File.Exists(_statePath))
        {
            PathLinkSecurity.RejectLinks(_statePath);
            VerifyAcl(_statePath, directory: false);
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        if (bytes.Length is <= 0 or > MaximumStateBytes) throw new InvalidDataException("应用策略状态超过大小限制。");
        var temporary = Path.Combine(_directory, ".state-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       16 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            SecurePath(temporary, directory: false);
            File.Move(temporary, _statePath, overwrite: true);
            PathLinkSecurity.RejectLinks(_statePath);
            VerifyAcl(_statePath, directory: false);
            var readBack = File.ReadAllBytes(_statePath);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(readBack), SHA256.HashData(bytes)))
                throw new IOException("应用策略状态原子写入后摘要读回不一致。");
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void ValidateState(ApplicationPolicyRuntimeState state)
    {
        if (!string.Equals(state.CampusId, _campusId, StringComparison.Ordinal) ||
            state.Revision < 0 || state.Policy.CampusId != _campusId || state.Revision != state.Policy.Revision)
            throw new InvalidDataException("应用策略持久状态校区或版本不一致。");
        ApplicationPolicyCompiler.Validate(state.Policy);
        ApplicationPolicyRuntime.ValidateSoftwareScope(state.SoftwareRestrictionStudentSids,
            state.SoftwareRestrictionAllowedSids);
        _ = ApplicationPolicyRuntime.CanonicalXml(state.OriginalXml);
        _ = ApplicationPolicyRuntime.CanonicalXml(state.InstalledXml);
        if (state.Pending && state.PendingPreviousXml is null)
            throw new InvalidDataException("应用策略待处理事务缺少先前 XML。");
        if (state.PendingPreviousXml is not null)
            _ = ApplicationPolicyRuntime.CanonicalXml(state.PendingPreviousXml);
    }

    private void EnsureDirectory()
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var trustedRoot = Path.Combine(common, "VeyonCampus", "ApplicationPolicy");
        Directory.CreateDirectory(trustedRoot);
        PathLinkSecurity.RejectLinks(trustedRoot);
        SecurePath(trustedRoot, directory: true);
        Directory.CreateDirectory(_directory);
        PathLinkSecurity.RejectLinks(_directory);
        SecurePath(_directory, directory: true);
    }

    private static void SecurePath(string path, bool directory)
    {
        FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else new FileInfo(path).SetAccessControl((FileSecurity)security);
        VerifyAcl(path, directory);
    }

    private static void VerifyAcl(string path, bool directory)
    {
        FileSystemSecurity expected = directory ? new DirectorySecurity() : new FileSecurity();
        expected.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            expected.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
        FileSystemSecurity actual = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        var expectedRules = Rules(expected);
        if (!actual.AreAccessRulesProtected || !Rules(actual).SequenceEqual(expectedRules))
            throw new UnauthorizedAccessException("应用策略持久状态权限不是 SYSTEM/Administrators 专用。");
    }

    private static string[] Rules(FileSystemSecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => $"{rule.IdentityReference.Value}:{rule.AccessControlType}:{rule.FileSystemRights}:" +
                            $"{rule.InheritanceFlags}:{rule.PropagationFlags}:{rule.IsInherited}")
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
}
