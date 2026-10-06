using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace VeyonCampus.Core;

[SupportedOSPlatform("windows")]
public sealed class WindowsApplicationPolicyAgent
{
    private readonly WindowsApplicationPolicyBackend _backend;
    private readonly WindowsApplicationPolicyStateStore _store;
    private readonly object _readNonceGate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _readNonces = [];
    public ApplicationPolicyRuntime Runtime { get; }

    private WindowsApplicationPolicyAgent(string campusId, string? publicKeyPem)
    {
        _backend = new WindowsApplicationPolicyBackend(FindProtectedExecutables());
        _store = new WindowsApplicationPolicyStateStore(campusId);
        Runtime = new ApplicationPolicyRuntime(_backend, _store, campusId, publicKeyPem);
    }

    public static WindowsApplicationPolicyAgent Create(string campusId, string publicKeyPem)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("应用策略 Agent 仅支持 Windows。");
        using var key = RSA.Create();
        key.ImportFromPem(publicKeyPem);
        if (key.KeySize is < 2048 or > 4096) throw new InvalidDataException("应用策略校区公钥位长无效。");
        return new WindowsApplicationPolicyAgent(campusId, key.ExportSubjectPublicKeyInfoPem());
    }

    public static WindowsApplicationPolicyAgent CreateForSystemPolicy(string campusId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("应用策略 Agent 仅支持 Windows。");
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        return new WindowsApplicationPolicyAgent(campusId, null);
    }

    public ApplicationPolicyAuditResponse ReadAudit(string signedRequestJson, string publicKeyPem,
        DateTimeOffset nowUtc)
    {
        var request = ApplicationPolicyAuditCryptography.VerifyRequest(signedRequestJson, publicKeyPem,
            _store.CampusId, nowUtc);
        AcceptReadNonce(request.Nonce, request.IssuedUtc, nowUtc);
        var state = Runtime.ReadForAudit(nowUtc);
        if (state?.Policy.Mode == ApplicationPolicyMode.Audit &&
            state.SoftwareRestrictionStudentSids is { Count: > 0 })
            return ApplicationPolicyAuditReader.Simulate(state, WindowsApplicationInventoryReader.Read(), request, nowUtc);
        var events = _backend.ReadEvents(nowUtc.ToUniversalTime().AddHours(-request.LookbackHours));
        return ApplicationPolicyAuditReader.Read(state, events, request, nowUtc);
    }

    public ApplicationInventoryResponse ReadInventory(string signedRequestJson, string publicKeyPem,
        DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var request = ApplicationInventoryCryptography.VerifyRequest(signedRequestJson, publicKeyPem,
            _store.CampusId, nowUtc);
        AcceptReadNonce(request.Nonce, request.IssuedUtc, nowUtc);
        var items = WindowsApplicationInventoryReader.Read(cancellationToken);
        return ApplicationInventoryCryptography.CreateResponse(request, items, nowUtc);
    }

    private void AcceptReadNonce(Guid nonce, DateTimeOffset issuedUtc, DateTimeOffset nowUtc)
    {
        lock (_readNonceGate)
        {
            var cutoff = nowUtc.ToUniversalTime().Subtract(TimeSpan.FromMinutes(5));
            foreach (var expired in _readNonces.Where(item => item.Value < cutoff).Select(item => item.Key).ToArray())
                _readNonces.Remove(expired);
            if (_readNonces.Count >= 4096)
                throw new InvalidDataException("学生端读取请求过多；稍后重试。");
            if (!_readNonces.TryAdd(nonce, issuedUtc))
                throw new InvalidDataException("学生端拒绝重复的签名读取请求。");
        }
    }

    public long RestoreForRemoval() => Runtime.RestoreForRemoval();

    private static IReadOnlyCollection<string> FindProtectedExecutables()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(Environment.ProcessPath);
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"));
        var roots = new[]
        {
            AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VeyonCampus"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Veyon Campus"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Veyon"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Veyon Campus"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Veyon")
        };
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            PathLinkSecurity.RejectLinks(root);
            AddMatching(root);
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                PathLinkSecurity.RejectLinks(directory);
                AddMatching(directory);
            }
        }
        if (candidates.Count == 0) throw new InvalidDataException("没有找到可保护的 Agent/恢复可执行文件。");
        return candidates.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                PathLinkSecurity.RejectLinks(path);
                candidates.Add(Path.GetFullPath(path));
            }
        }
        void AddMatching(string directory)
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (name.StartsWith("VeyonCampus.", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("veyon-", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Veyon.exe", StringComparison.OrdinalIgnoreCase)) Add(path);
            }
        }
    }
}
