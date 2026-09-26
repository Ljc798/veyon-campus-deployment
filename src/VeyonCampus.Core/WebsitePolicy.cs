using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

public enum WebsitePolicyMode
{
    Disabled,
    Blocklist,
    Allowlist
}

public sealed record WebsitePolicyDocument(
    int SchemaVersion,
    string CampusId,
    long Revision,
    DateTimeOffset IssuedUtc,
    WebsitePolicyMode Mode,
    IReadOnlyList<string> Domains);

public sealed record SignedWebsitePolicy(string Payload, string Signature);

public sealed record BrowserWebsitePolicy(IReadOnlyList<string> Blocklist, IReadOnlyList<string> Allowlist);

/// <summary>Validates website entries and compiles teacher policies into Chromium URL policy values.</summary>
public static class WebsitePolicyCompiler
{
    public const int MaximumEntries = 1000;
    public const int MaximumPayloadBytes = 128 * 1024;
    private static readonly Regex CampusIdRegex = new("^[A-Za-z0-9_-]{1,100}$", RegexOptions.CultureInvariant);
    private static readonly Regex DomainLabelRegex = new("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static WebsitePolicyDocument Create(string campusId, long revision, WebsitePolicyMode mode,
        IEnumerable<string> domains, DateTimeOffset? issuedUtc = null)
    {
        if (campusId is null || !CampusIdRegex.IsMatch(campusId))
            throw new InvalidDataException("校区 ID 格式无效。只能使用英文字母、数字、连字符和下划线。");
        if (revision <= 0)
            throw new InvalidDataException("网站策略版本必须是正整数。");
        if (!Enum.IsDefined(mode))
            throw new InvalidDataException("网站策略模式无效。");

        var normalized = NormalizeDomains(domains);
        if (mode == WebsitePolicyMode.Disabled && normalized.Count != 0)
            throw new InvalidDataException("停用网站限制时名单必须为空。");
        if (mode != WebsitePolicyMode.Disabled && normalized.Count == 0)
            throw new InvalidDataException("启用黑名单或白名单时至少输入一个网站域名。");

        return new WebsitePolicyDocument(1, campusId, revision,
            (issuedUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(), mode, normalized);
    }

    public static IReadOnlyList<string> NormalizeDomains(IEnumerable<string> domains)
    {
        ArgumentNullException.ThrowIfNull(domains);
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in domains)
        {
            var entry = raw?.Trim();
            if (string.IsNullOrEmpty(entry)) continue;
            var domain = NormalizeDomain(entry);
            result.Add(domain);
            if (result.Count > MaximumEntries)
                throw new InvalidDataException($"网站名单最多允许 {MaximumEntries} 个域名。");
        }
        return Array.AsReadOnly(result.ToArray());
    }

    public static BrowserWebsitePolicy Compile(WebsitePolicyDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var validated = Create(document.CampusId, document.Revision, document.Mode, document.Domains, document.IssuedUtc);
        return document.Mode switch
        {
            WebsitePolicyMode.Disabled => new(Array.Empty<string>(), Array.Empty<string>()),
            WebsitePolicyMode.Blocklist => new(validated.Domains, Array.Empty<string>()),
            WebsitePolicyMode.Allowlist => new(new[] { "*" }, validated.Domains),
            _ => throw new InvalidDataException("网站策略模式无效.")
        };
    }

    internal static byte[] Serialize(WebsitePolicyDocument document) => JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);

    internal static WebsitePolicyDocument Deserialize(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var document = JsonSerializer.Deserialize<WebsitePolicyDocument>(utf8, JsonOptions)
                           ?? throw new InvalidDataException("网站策略正文为空。");
            if (document.SchemaVersion != 1)
                throw new InvalidDataException("网站策略版本不受支持。");
            var validated = Create(document.CampusId, document.Revision, document.Mode, document.Domains, document.IssuedUtc);
            if (!validated.Domains.SequenceEqual(document.Domains, StringComparer.Ordinal))
                throw new InvalidDataException("网站名单未规范化或包含重复项。");
            return validated;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("网站策略 JSON 格式无效。", ex);
        }
    }

    private static string NormalizeDomain(string input)
    {
        var candidate = input;
        if (candidate.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
                uri.Port != (uri.Scheme == "https" ? 443 : 80) ||
                (uri.AbsolutePath.Length > 1 && uri.AbsolutePath != "/") ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidDataException($"网站项必须是域名，或不带路径的 HTTP/HTTPS 地址：{input}");
            candidate = uri.Host;
        }

        candidate = candidate.TrimEnd('.');
        if (candidate.Length == 0 || candidate.Contains('*') || candidate.Contains('/') ||
            candidate.Contains('\\') || candidate.Contains(':') || IPAddress.TryParse(candidate, out _))
            throw new InvalidDataException($"网站项必须是域名，不能包含通配符、路径、端口或 IP 地址：{input}");

        string ascii;
        try { ascii = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(candidate).ToLowerInvariant(); }
        catch (ArgumentException ex) { throw new InvalidDataException($"域名格式无效：{input}", ex); }
        if (ascii.Length > 253 || ascii.Split('.').Any(label => !DomainLabelRegex.IsMatch(label)))
            throw new InvalidDataException($"域名格式无效：{input}");
        return ascii;
    }

    private static JsonSerializerOptions CreateJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

/// <summary>Signs and verifies policy messages using the existing campus RSA authentication key.</summary>
public static class WebsitePolicyCryptography
{
    public static string Sign(WebsitePolicyDocument document, RSA teacherPrivateKey)
    {
        ArgumentNullException.ThrowIfNull(teacherPrivateKey);
        var payload = WebsitePolicyCompiler.Serialize(document);
        if (payload.Length > WebsitePolicyCompiler.MaximumPayloadBytes)
            throw new InvalidDataException("网站策略消息超过大小限制。");
        var signature = teacherPrivateKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return JsonSerializer.Serialize(new SignedWebsitePolicy(Convert.ToBase64String(payload), Convert.ToBase64String(signature)));
    }

    public static WebsitePolicyDocument Verify(string envelopeJson, string studentPublicKeyPem,
        string expectedCampusId, long currentRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envelopeJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(studentPublicKeyPem);
        if (Encoding.UTF8.GetByteCount(envelopeJson) > WebsitePolicyCompiler.MaximumPayloadBytes * 2)
            throw new InvalidDataException("网站策略消息超过大小限制。");

        SignedWebsitePolicy envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SignedWebsitePolicy>(envelopeJson)
                       ?? throw new InvalidDataException("网站策略消息为空。");
        }
        catch (JsonException ex) { throw new InvalidDataException("网站策略信封格式无效。", ex); }

        byte[] payload;
        byte[] signature;
        try
        {
            payload = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException ex) { throw new InvalidDataException("网站策略信封编码无效。", ex); }
        if (payload.Length is 0 or > WebsitePolicyCompiler.MaximumPayloadBytes || signature.Length > 1024)
            throw new InvalidDataException("网站策略正文或签名大小无效。");

        using var publicKey = RSA.Create();
        try { publicKey.ImportFromPem(studentPublicKeyPem); }
        catch (CryptographicException ex) { throw new InvalidDataException("学生端校区公钥无效。", ex); }
        if (!publicKey.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("网站策略签名无效；学生端没有应用该策略。");

        var document = WebsitePolicyCompiler.Deserialize(payload);
        if (!string.Equals(document.CampusId, expectedCampusId, StringComparison.Ordinal))
            throw new InvalidDataException("网站策略属于其他校区；学生端没有应用该策略。");
        if (document.Revision <= currentRevision)
            throw new InvalidDataException("网站策略版本不是新版本；学生端拒绝重放或旧策略。");
        return document;
    }
}
