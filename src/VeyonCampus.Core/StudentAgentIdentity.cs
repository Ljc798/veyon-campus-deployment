using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

public sealed record SignedStudentAgentResponse(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonPropertyName("publicKeyPem")] string PublicKeyPem);

public sealed record VerifiedStudentAgentResponse<T>(T Payload, string PublicKeyPem,
    string Fingerprint, bool MatchesPinnedKey);

public sealed record StudentAgentIdentityTrustCandidate(string Target, string CampusId,
    string PublicKeyPem, string Fingerprint, string? PreviouslyPinnedFingerprint = null);

public sealed record StudentAgentIdentityTrustPin(string Target, string CampusId,
    string PublicKeyPem, string Fingerprint, DateTimeOffset PinnedUtc);

public sealed record StudentApplicationUpdateResponse(int SchemaVersion, string Purpose, string CampusId,
    Guid CommandId, DateTimeOffset CompletedUtc, string StudentSetupVersion, string AgentVersion,
    bool AgentRestartPending, string Message);

public sealed record StudentAgentCommandAcknowledgement(int SchemaVersion, string Purpose, string CampusId,
    Guid Nonce, string RequestSha256, DateTimeOffset CompletedUtc, int HttpStatusCode, string AgentVersion,
    string Body);

public static class StudentApplicationUpdateResponseCryptography
{
    public const string Purpose = "VeyonCampus.StudentApplicationUpdateResponse.v1";
    public static readonly TimeSpan MaximumResponseAge = TimeSpan.FromMinutes(25);

    public static void Validate(StudentApplicationUpdateResponse response, string campusId, Guid commandId,
        string expectedVersion, bool restartPending, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(response);
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var now = nowUtc.ToUniversalTime();
        try
        {
            _ = ApplicationReleaseClient.CompareVersions(response.StudentSetupVersion, response.StudentSetupVersion);
            _ = ApplicationReleaseClient.CompareVersions(response.AgentVersion, response.AgentVersion);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException("学生 Agent 更新回执版本无效。", exception);
        }
        if (response.SchemaVersion != 1 || response.Purpose != Purpose || response.CampusId != campusId ||
            response.CommandId == Guid.Empty || response.CommandId != commandId ||
            response.CompletedUtc.Offset != TimeSpan.Zero || response.CompletedUtc > now.AddMinutes(2) ||
            response.CompletedUtc < now.Subtract(MaximumResponseAge) ||
            !string.Equals(response.StudentSetupVersion, expectedVersion, StringComparison.Ordinal) ||
            response.AgentRestartPending != restartPending || string.IsNullOrWhiteSpace(response.Message) ||
            response.Message.Length > 2048)
            throw new InvalidDataException("学生 Agent 更新回执与本次命令、版本、校区或 HTTP 状态不匹配。");
    }
}

/// <summary>Authenticates Student Agent replies and pins per-computer identity keys on the Teacher.</summary>
public static class StudentAgentResponseCryptography
{
    public const int MaximumEnvelopeBytes = 64 * 1024;
    public const int MaximumCommandEnvelopeBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = ApplicationPolicyCompiler.JsonOptions;

    public static string Sign<T>(T payload, RSA privateKey, int maximumEnvelopeBytes = MaximumEnvelopeBytes)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(privateKey);
        if (maximumEnvelopeBytes is < 1024 or > MaximumCommandEnvelopeBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumEnvelopeBytes));
        if (privateKey.KeySize is < 2048 or > 4096)
            throw new InvalidDataException("学生 Agent 身份密钥位长不受支持。");
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        if (payloadBytes.Length is 0 || payloadBytes.Length > maximumEnvelopeBytes * 3 / 4)
            throw new InvalidDataException("学生 Agent 签名回执超过大小限制。");
        var envelope = new SignedStudentAgentResponse(Convert.ToBase64String(payloadBytes),
            Convert.ToBase64String(privateKey.SignData(payloadBytes, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss)), privateKey.ExportSubjectPublicKeyInfoPem());
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > maximumEnvelopeBytes)
            throw new InvalidDataException("学生 Agent 签名回执超过大小限制。");
        return json;
    }

    public static VerifiedStudentAgentResponse<T> Verify<T>(string signedJson,
        string? pinnedPublicKeyPem = null, int maximumEnvelopeBytes = MaximumEnvelopeBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signedJson);
        if (maximumEnvelopeBytes is < 1024 or > MaximumCommandEnvelopeBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumEnvelopeBytes));
        if (Encoding.UTF8.GetByteCount(signedJson) > maximumEnvelopeBytes)
            throw new InvalidDataException("学生 Agent 签名回执超过大小限制。");
        try
        {
            var envelopeBytes = Encoding.UTF8.GetBytes(signedJson);
            PolicyJson.RejectDuplicateFields(envelopeBytes);
            var envelope = JsonSerializer.Deserialize<SignedStudentAgentResponse>(signedJson, JsonOptions)
                           ?? throw new InvalidDataException("学生 Agent 签名回执为空。");
            ArgumentException.ThrowIfNullOrWhiteSpace(envelope.Payload);
            ArgumentException.ThrowIfNullOrWhiteSpace(envelope.Signature);
            ArgumentException.ThrowIfNullOrWhiteSpace(envelope.PublicKeyPem);
            var payloadBytes = DecodeCanonicalBase64(envelope.Payload, "学生 Agent 回执正文");
            var signature = DecodeCanonicalBase64(envelope.Signature, "学生 Agent 回执签名");
            if (payloadBytes.Length is 0 || payloadBytes.Length > maximumEnvelopeBytes * 3 / 4)
                throw new InvalidDataException("学生 Agent 回执正文超过大小限制。");
            PolicyJson.RejectDuplicateFields(payloadBytes);
            using var publicKey = RSA.Create();
            publicKey.ImportFromPem(envelope.PublicKeyPem);
            if (publicKey.KeySize is < 2048 or > 4096 || signature.Length != publicKey.KeySize / 8 ||
                !publicKey.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("学生 Agent 回执签名无效。");
            var canonicalPublicKey = publicKey.ExportSubjectPublicKeyInfoPem();
            if (!string.Equals(envelope.PublicKeyPem, canonicalPublicKey, StringComparison.Ordinal))
                throw new InvalidDataException("学生 Agent 回执携带的身份密钥不是规范的公开密钥格式。");
            var fingerprint = Convert.ToHexString(SHA256.HashData(publicKey.ExportSubjectPublicKeyInfo()));
            var response = JsonSerializer.Deserialize<T>(payloadBytes, JsonOptions)
                           ?? throw new InvalidDataException("学生 Agent 回执正文为空。");
            var canonicalPayload = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
            if (!payloadBytes.AsSpan().SequenceEqual(canonicalPayload))
                throw new InvalidDataException("学生 Agent 回执正文不是规范编码。");

            var matchesPin = false;
            if (pinnedPublicKeyPem is not null)
            {
                using var pinnedKey = RSA.Create();
                pinnedKey.ImportFromPem(pinnedPublicKeyPem);
                matchesPin = pinnedKey.KeySize is >= 2048 and <= 4096 &&
                    CryptographicOperations.FixedTimeEquals(pinnedKey.ExportSubjectPublicKeyInfo(),
                        publicKey.ExportSubjectPublicKeyInfo());
            }
            return new VerifiedStudentAgentResponse<T>(response, canonicalPublicKey, fingerprint, matchesPin);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or CryptographicException or
                                          ArgumentException)
        {
            throw new InvalidDataException("学生 Agent 签名回执编码或公钥无效。", exception);
        }
    }

    public static string GetFingerprint(string publicKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        try
        {
            using var key = RSA.Create();
            key.ImportFromPem(publicKeyPem);
            if (key.KeySize is < 2048 or > 4096)
                throw new InvalidDataException("学生 Agent 身份密钥位长不受支持。");
            return Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("学生 Agent 身份公钥无效。", exception);
        }
    }

    private static byte[] DecodeCanonicalBase64(string value, string description)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (!string.Equals(Convert.ToBase64String(bytes), value, StringComparison.Ordinal))
                throw new InvalidDataException($"{description}不是规范 Base64 编码。");
            return bytes;
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"{description}编码无效。", exception);
        }
    }
}

/// <summary>Signs a command result against its one-time teacher nonce and exact request body.</summary>
public static class StudentAgentCommandAcknowledgementCryptography
{
    public const string Purpose = "VeyonCampus.StudentAgentCommandAcknowledgement.v1";
    public static readonly TimeSpan MaximumAcknowledgementAge = TimeSpan.FromMinutes(2);

    public static string Sign(string campusId, Guid nonce, string requestBody, int httpStatusCode, string body,
        RSA privateKey, DateTimeOffset? completedUtc = null)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestBody);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(privateKey);
        if (nonce == Guid.Empty || httpStatusCode is < 200 or > 299)
            throw new InvalidDataException("学生 Agent 命令回执的随机数或 HTTP 状态码无效。");
        var payload = new StudentAgentCommandAcknowledgement(1, Purpose, campusId, nonce,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestBody))),
            (completedUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(), httpStatusCode,
            WebsitePolicyAgent.GetRuntimeVersion(), body);
        return StudentAgentResponseCryptography.Sign(payload, privateKey,
            StudentAgentResponseCryptography.MaximumCommandEnvelopeBytes);
    }

    public static VerifiedStudentAgentResponse<StudentAgentCommandAcknowledgement> Verify(string signedJson,
        string campusId, Guid nonce, string requestBody, int httpStatusCode, string? pinnedPublicKeyPem,
        DateTimeOffset nowUtc, int maximumBodyBytes)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestBody);
        if (nonce == Guid.Empty || maximumBodyBytes is < 0 or > 2 * 1024 * 1024)
            throw new InvalidDataException("学生 Agent 命令回执验证参数无效。");
        var verified = StudentAgentResponseCryptography.Verify<StudentAgentCommandAcknowledgement>(signedJson,
            pinnedPublicKeyPem, StudentAgentResponseCryptography.MaximumCommandEnvelopeBytes);
        var acknowledgement = verified.Payload;
        var expectedRequestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestBody)));
        var now = nowUtc.ToUniversalTime();
        if (acknowledgement.SchemaVersion != 1 || acknowledgement.Purpose != Purpose ||
            acknowledgement.CampusId != campusId || acknowledgement.Nonce != nonce ||
            acknowledgement.RequestSha256 != expectedRequestHash || acknowledgement.HttpStatusCode != httpStatusCode ||
            acknowledgement.CompletedUtc.Offset != TimeSpan.Zero || acknowledgement.CompletedUtc > now.AddMinutes(2) ||
            acknowledgement.CompletedUtc < now.Subtract(MaximumAcknowledgementAge) ||
            string.IsNullOrWhiteSpace(acknowledgement.AgentVersion) || acknowledgement.AgentVersion.Length > 64 ||
            acknowledgement.Body is null ||
            Encoding.UTF8.GetByteCount(acknowledgement.Body) > maximumBodyBytes)
            throw new InvalidDataException("学生 Agent 命令回执与本次请求、校区、HTTP 状态或时间不匹配。");
        return verified;
    }
}

/// <summary>Teacher-side trust pins; a changed key is surfaced and never accepted implicitly.</summary>
public sealed class StudentAgentIdentityTrustStore
{
    private const int MaximumFileBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _path;

    public StudentAgentIdentityTrustStore(string? path = null) => _path = path ?? DefaultPath;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeyonCampus", "StudentAgentTrust", "agents.json");

    public string? FindTrustedPublicKey(string campusId, string target)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var normalizedTarget = NormalizeTarget(target);
        var pin = ReadPins().SingleOrDefault(item => item.CampusId == campusId &&
            string.Equals(item.Target, normalizedTarget, StringComparison.OrdinalIgnoreCase));
        return pin?.PublicKeyPem;
    }

    public IReadOnlyList<StudentAgentIdentityTrustPin> List() => Array.AsReadOnly(ReadPins().ToArray());

    public StudentAgentIdentityTrustPin Pin(StudentAgentIdentityTrustCandidate candidate, bool replaceChangedKey = false)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        WebsitePolicySigningKeyStore.ValidateCampusId(candidate.CampusId);
        var target = NormalizeTarget(candidate.Target);
        using var key = RSA.Create();
        key.ImportFromPem(candidate.PublicKeyPem);
        if (key.KeySize is < 2048 or > 4096)
            throw new InvalidDataException("学生 Agent 身份密钥位长不受支持。");
        var canonicalPem = key.ExportSubjectPublicKeyInfoPem();
        var fingerprint = StudentAgentResponseCryptography.GetFingerprint(canonicalPem);
        if (!string.Equals(candidate.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("待信任的学生 Agent 指纹与公钥不匹配。");

        var pins = ReadPins().ToList();
        var existingIndex = pins.FindIndex(item => item.CampusId == candidate.CampusId &&
            string.Equals(item.Target, target, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            if (string.Equals(pins[existingIndex].Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                return pins[existingIndex];
            if (!replaceChangedKey)
                throw new InvalidDataException("该学生电脑的 Agent 身份密钥已变化；须先核对新旧指纹并明确批准轮换。");
            pins.RemoveAt(existingIndex);
        }

        var pin = new StudentAgentIdentityTrustPin(target, candidate.CampusId, canonicalPem, fingerprint,
            DateTimeOffset.UtcNow);
        pins.Add(pin);
        WritePins(pins);
        return pin;
    }

    public bool Remove(string campusId, string target, string expectedFingerprint)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        if (!Regex.IsMatch(expectedFingerprint, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("确认移除的学生 Agent 指纹无效。");
        var normalizedTarget = NormalizeTarget(target);
        var pins = ReadPins().ToList();
        var index = pins.FindIndex(item => item.CampusId == campusId &&
            string.Equals(item.Target, normalizedTarget, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || !string.Equals(pins[index].Fingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase))
            return false;
        pins.RemoveAt(index);
        WritePins(pins);
        return true;
    }

    private IReadOnlyList<StudentAgentIdentityTrustPin> ReadPins()
    {
        if (!File.Exists(_path)) return Array.Empty<StudentAgentIdentityTrustPin>();
        PathLinkSecurity.RejectLinks(_path);
        var info = new FileInfo(_path);
        if (info.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("本机学生 Agent 信任清单大小无效。");
        var bytes = File.ReadAllBytes(_path);
        PolicyJson.RejectDuplicateFields(bytes);
        var document = JsonSerializer.Deserialize<TrustDocument>(bytes, JsonOptions)
                       ?? throw new InvalidDataException("本机学生 Agent 信任清单为空。");
        if (document.SchemaVersion != 1 || document.Pins is null || document.Pins.Count > 5000)
            throw new InvalidDataException("本机学生 Agent 信任清单版本或数量无效。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in document.Pins)
        {
            WebsitePolicySigningKeyStore.ValidateCampusId(pin.CampusId);
            var normalizedTarget = NormalizeTarget(pin.Target);
            using var key = RSA.Create();
            key.ImportFromPem(pin.PublicKeyPem);
            var fingerprint = StudentAgentResponseCryptography.GetFingerprint(pin.PublicKeyPem);
            var uniqueKey = pin.CampusId + "\n" + normalizedTarget;
            if (pin.Target != normalizedTarget || pin.Fingerprint != fingerprint ||
                pin.PinnedUtc.Offset != TimeSpan.Zero || !seen.Add(uniqueKey))
                throw new InvalidDataException("本机学生 Agent 信任条目无效或重复。");
        }
        return document.Pins;
    }

    private void WritePins(IReadOnlyCollection<StudentAgentIdentityTrustPin> pins)
    {
        var fullPath = Path.GetFullPath(_path);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidDataException("本机学生 Agent 信任清单目录无效。");
        Directory.CreateDirectory(directory);
        PathLinkSecurity.RejectLinks(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new TrustDocument(1, pins.ToArray()), JsonOptions);
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("本机学生 Agent 信任清单超过大小限制。");
        var temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(fullPath)) PathLinkSecurity.RejectLinks(fullPath);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }

    private static string NormalizeTarget(string target)
    {
        var normalized = WebsitePolicyTransport.NormalizeTargets([target]);
        if (normalized.Count != 1) throw new InvalidDataException("学生 Agent 信任目标无效。");
        return normalized[0].ToLowerInvariant();
    }

    private sealed record TrustDocument(int SchemaVersion, IReadOnlyList<StudentAgentIdentityTrustPin> Pins);
}

/// <summary>Per-install key held by the SYSTEM Agent, independent of replaceable binaries.</summary>
public static class StudentAgentIdentityKeyStore
{
    public const string FileName = "agent-identity.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [SupportedOSPlatform("windows")]
    public static RSA LoadOrCreateForSystemAgent(string configPath)
    {
        EnsureSystemIdentity();
        var path = IdentityPath(configPath);
        PathLinkSecurity.RejectLinks(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            PathLinkSecurity.RejectLinks(path);
            AgentFileSecurity.SecurePrivateFile(path);
            return ImportStoredPrivateKey(File.ReadAllBytes(path));
        }

        using var generated = RSA.Create(2048);
        var document = new StoredIdentity(1, generated.ExportPkcs8PrivateKeyPem());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            // Create an empty file first and lock its DACL before writing any private-key bytes.
            using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            AgentFileSecurity.SecurePrivateFile(temporary);
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path))
            {
                PathLinkSecurity.RejectLinks(path);
                AgentFileSecurity.SecurePrivateFile(path);
                return ImportStoredPrivateKey(File.ReadAllBytes(path));
            }
            AgentFileSecurity.SecurePrivateFile(path);
            return ImportStoredPrivateKey(bytes);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }

    [SupportedOSPlatform("windows")]
    public static string ReadPublicKeyPem(string configPath)
    {
        var path = IdentityPath(configPath);
        PathLinkSecurity.RejectLinks(path);
        AgentFileSecurity.SecurePrivateFile(path);
        using var key = ImportStoredPrivateKey(File.ReadAllBytes(path));
        return key.ExportSubjectPublicKeyInfoPem();
    }

    [SupportedOSPlatform("windows")]
    public static string ReadFingerprint(string configPath) =>
        StudentAgentResponseCryptography.GetFingerprint(ReadPublicKeyPem(configPath));

    private static RSA ImportStoredPrivateKey(byte[] bytes)
    {
        if (bytes.Length is 0 or > 32 * 1024) throw new InvalidDataException("学生 Agent 身份密钥文件大小无效。");
        PolicyJson.RejectDuplicateFields(bytes);
        var document = JsonSerializer.Deserialize<StoredIdentity>(bytes, JsonOptions)
                       ?? throw new InvalidDataException("学生 Agent 身份密钥文件为空。");
        if (document.SchemaVersion != 1 || string.IsNullOrWhiteSpace(document.PrivateKeyPem))
            throw new InvalidDataException("学生 Agent 身份密钥文件版本或内容无效。");
        var key = RSA.Create();
        try
        {
            key.ImportFromPem(document.PrivateKeyPem);
            if (key.KeySize is < 2048 or > 4096 || key.ExportPkcs8PrivateKeyPem() != document.PrivateKeyPem)
                throw new InvalidDataException("学生 Agent 私钥格式或位长无效。");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static string IdentityPath(string configPath)
    {
        var fullConfig = Path.GetFullPath(configPath);
        if (!Regex.IsMatch(Path.GetFileName(fullConfig), "^agent-[A-Fa-f0-9]{24}\\.json$",
                RegexOptions.CultureInvariant))
            throw new InvalidDataException("学生 Agent 身份密钥配置路径无效。");
        var directory = Path.GetDirectoryName(fullConfig)
                        ?? throw new InvalidDataException("学生 Agent 身份密钥目录无效。");
        return Path.Combine(directory, FileName);
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureSystemIdentity()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18")
            throw new UnauthorizedAccessException("学生 Agent 身份密钥只能由 SYSTEM 代理读取或创建。");
    }

    private sealed record StoredIdentity(int SchemaVersion, string PrivateKeyPem);
}
