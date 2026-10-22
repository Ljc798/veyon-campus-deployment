using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

public sealed record ApplicationCatalogRequest(int SchemaVersion, string Purpose, string CampusId,
    Guid Nonce, DateTimeOffset IssuedUtc, IReadOnlyList<string> CandidateIds);

public sealed record ApplicationCatalogItem(string Id, string DisplayName, string? ProductName,
    string? CompanyName, string BinaryName, string FilePath, string? BinaryVersion);

public sealed record ApplicationCatalogResponse(int SchemaVersion, string Purpose, string CampusId,
    Guid Nonce, DateTimeOffset CollectedUtc, IReadOnlyList<ApplicationCatalogItem> Items,
    IReadOnlyList<ApplicationInventoryItem>? Details = null);

public sealed record ApplicationCatalogDeliveryResult(string Target, bool Succeeded, string Detail,
    IReadOnlyList<ApplicationCatalogItem> Items, IReadOnlyList<ApplicationInventoryItem>? Details = null,
    bool NeedsReview = false);

public static class ApplicationCatalogCryptography
{
    public const string RequestPurpose = "VeyonCampus.ApplicationCatalogRequest.v1";
    public const string ResponsePurpose = "VeyonCampus.ApplicationCatalogResponse.v1";
    public const int MaximumCatalogItems = 400;
    public const int MaximumSelectedItems = 80;
    public const int MaximumCatalogResponseBytes = 2 * 1024 * 1024;
    public static readonly TimeSpan MaximumRequestAge = TimeSpan.FromMinutes(5);

    public static string CandidateId(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = Path.GetFullPath(path).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    public static string SignRequest(ApplicationCatalogRequest request, RSA privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        ValidateRequest(request, request.IssuedUtc);
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, ApplicationPolicyCompiler.JsonOptions);
        if (payload.Length > 16 * 1024) throw new InvalidDataException("应用目录请求超过大小限制。");
        return JsonSerializer.Serialize(new SignedWebsitePolicy(Convert.ToBase64String(payload),
            Convert.ToBase64String(privateKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))));
    }

    public static ApplicationCatalogRequest VerifyRequest(string envelopeJson, string publicKeyPem,
        string campusId, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(envelopeJson) || Encoding.UTF8.GetByteCount(envelopeJson) > 32 * 1024)
            throw new InvalidDataException("应用目录请求大小无效。");
        try
        {
            PolicyJson.RejectDuplicateFields(Encoding.UTF8.GetBytes(envelopeJson));
            var envelope = JsonSerializer.Deserialize<SignedWebsitePolicy>(envelopeJson,
                new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                ?? throw new InvalidDataException("应用目录请求信封为空。");
            var payload = Convert.FromBase64String(envelope.Payload);
            var signature = Convert.FromBase64String(envelope.Signature);
            if (payload.Length is 0 or > 16 * 1024 || signature.Length is < 256 or > 512)
                throw new InvalidDataException("应用目录请求字段大小无效。");
            PolicyJson.RejectDuplicateFields(payload);
            using var key = RSA.Create();
            key.ImportFromPem(publicKeyPem);
            if (key.KeySize is < 2048 or > 4096 ||
                !key.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("应用目录请求签名无效。");
            var request = JsonSerializer.Deserialize<ApplicationCatalogRequest>(payload,
                ApplicationPolicyCompiler.JsonOptions) ?? throw new InvalidDataException("应用目录请求正文为空。");
            ValidateRequest(request, nowUtc);
            if (!string.Equals(request.CampusId, campusId, StringComparison.Ordinal))
                throw new InvalidDataException("应用目录请求属于其他校区。");
            return request;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or CryptographicException or ArgumentException)
        { throw new InvalidDataException("应用目录请求编码无效。", exception); }
    }

    public static ApplicationCatalogResponse CreateResponse(ApplicationCatalogRequest request,
        IReadOnlyList<ApplicationCatalogItem> items, IReadOnlyList<ApplicationInventoryItem>? details,
        DateTimeOffset collectedUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = new ApplicationCatalogResponse(1, ResponsePurpose, request.CampusId, request.Nonce,
            collectedUtc.ToUniversalTime(), Array.AsReadOnly(items.ToArray()),
            details is null ? null : Array.AsReadOnly(details.ToArray()));
        ValidateResponse(response, request, collectedUtc);
        return response;
    }

    public static void ValidateResponse(ApplicationCatalogResponse response,
        ApplicationCatalogRequest request, DateTimeOffset nowUtc)
    {
        if (response.SchemaVersion != 1 || response.Purpose != ResponsePurpose || response.CampusId != request.CampusId ||
            response.Nonce != request.Nonce || response.CollectedUtc.Offset != TimeSpan.Zero ||
            response.CollectedUtc < request.IssuedUtc.AddMinutes(-1) ||
            response.CollectedUtc > nowUtc.ToUniversalTime().AddMinutes(2) || response.Items is null ||
            response.Items.Count > MaximumCatalogItems || response.Items.Any(item => item is null ||
                string.IsNullOrWhiteSpace(item.Id) || item.Id.Length != 64 || !item.Id.All(Uri.IsHexDigit) ||
                string.IsNullOrWhiteSpace(item.DisplayName) ||
                item.DisplayName.Length > 160 || item.DisplayName.Any(char.IsControl) ||
                item.ProductName is { Length: > 256 } || item.CompanyName is { Length: > 512 } ||
                string.IsNullOrWhiteSpace(item.BinaryName) || item.BinaryName.Length > 256 ||
                string.IsNullOrWhiteSpace(item.FilePath) || item.FilePath.Length > 2048 || item.FilePath.Any(char.IsControl) ||
                item.BinaryVersion is { Length: > 32 }) ||
            response.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != response.Items.Count)
            throw new InvalidDataException("应用目录回执的校区、随机数、时间或条目字段无效。");

        if (request.CandidateIds.Count == 0)
        {
            if (response.Details is { Count: > 0 }) throw new InvalidDataException("应用目录回执包含未请求的哈希详情。");
            if (response.Items.Any(item => !string.Equals(item.Id, CandidateId(item.FilePath), StringComparison.Ordinal)))
                throw new InvalidDataException("应用目录条目标识与本机文件路径不匹配。");
        }
        else
        {
            if (response.Items.Count != 0 || response.Details is null || response.Details.Count > request.CandidateIds.Count)
                throw new InvalidDataException("应用详情回执与已选软件条目不匹配。");
            var inventoryResponse = new ApplicationInventoryResponse(1, ApplicationInventoryCryptography.ResponsePurpose,
                request.CampusId, request.Nonce, response.CollectedUtc, response.Details);
            var inventoryRequest = new ApplicationInventoryRequest(1, ApplicationInventoryCryptography.RequestPurpose,
                request.CampusId, request.Nonce, request.IssuedUtc);
            ApplicationInventoryCryptography.ValidateResponse(inventoryResponse, inventoryRequest, nowUtc);
            var selectedIds = request.CandidateIds.ToHashSet(StringComparer.Ordinal);
            if (response.Details.Any(item => !selectedIds.Contains(CandidateId(item.FilePath))))
                throw new InvalidDataException("软件详情回执包含未选择的程序。");
        }
    }

    private static void ValidateRequest(ApplicationCatalogRequest request, DateTimeOffset nowUtc)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(request.CampusId);
        var now = nowUtc.ToUniversalTime();
        if (request.SchemaVersion != 1 || request.Purpose != RequestPurpose || request.Nonce == Guid.Empty ||
            request.IssuedUtc.Offset != TimeSpan.Zero || request.IssuedUtc > now.AddMinutes(1) ||
            request.IssuedUtc < now - MaximumRequestAge || request.CandidateIds is null ||
            request.CandidateIds.Count > MaximumSelectedItems ||
            request.CandidateIds.Distinct(StringComparer.Ordinal).Count() != request.CandidateIds.Count ||
            request.CandidateIds.Any(id => id.Length != 64 || !id.All(Uri.IsHexDigit)))
            throw new InvalidDataException("应用目录请求版本、签发时间、随机数或已选条目无效。");
    }
}

[SupportedOSPlatform("windows")]
public static class WindowsApplicationCatalogReader
{
    public static IReadOnlyList<ApplicationCatalogItem> Read(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("应用目录读取仅支持 Windows。");
        var candidates = WindowsApplicationInventoryReader.ReadRegisteredExecutables()
            .Take(ApplicationCatalogCryptography.MaximumCatalogItems).ToArray();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var results = new List<ApplicationCatalogItem>(candidates.Length);
        foreach (var candidate in candidates)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                PathLinkSecurity.RejectLinks(candidate.Path);
                var file = new FileInfo(candidate.Path);
                if (!file.Exists || file.Length is <= 0 or > 1024L * 1024 * 1024) continue;
                var version = FileVersionInfo.GetVersionInfo(candidate.Path);
                var product = Clean(version.ProductName, 256);
                var company = Clean(version.CompanyName, 512);
                var binaryVersion = FourPartVersion(version.FileVersion);
                var binary = Path.GetFileName(candidate.Path);
                var displayName = !string.IsNullOrWhiteSpace(candidate.DisplayName)
                    ? candidate.DisplayName : product ?? Path.GetFileNameWithoutExtension(binary);
                results.Add(new ApplicationCatalogItem(ApplicationCatalogCryptography.CandidateId(candidate.Path),
                    displayName, product, company, binary, candidate.Path, binaryVersion));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              System.Security.SecurityException or ArgumentException or
                                              System.ComponentModel.Win32Exception or NotSupportedException)
            { }
        }
        return Array.AsReadOnly(results.GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First()).ToArray());
    }

    public static IReadOnlyList<ApplicationInventoryItem> ReadSelected(IReadOnlyList<string> candidateIds,
        CancellationToken cancellationToken = default) =>
        WindowsApplicationInventoryReader.ReadSelectedCandidateIds(candidateIds, cancellationToken);

    private static string? Clean(string? value, int maximumLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > maximumLength || trimmed.Any(char.IsControl)
            ? null : trimmed;
    }

    private static string? FourPartVersion(string? value)
    {
        if (!Version.TryParse(value?.Trim(), out var version) || version.Major < 0 || version.Minor < 0) return null;
        return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}.{Math.Max(version.Revision, 0)}";
    }
}
