using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

public enum VeyonCampusRole { StudentSetup, TeacherConsole }

public enum PrivilegedWorkerOperation
{
    CreateStudentAccount,
    ChangeAdminPassword,
    RenameComputer,
    InstallVeyon,
    ConfigureVeyon,
    InstallWebsitePolicyAgent,
    UninstallWebsitePolicyAgent,
    ExportTeacherPublicKey,
    ConfigureTeacherAuthentication,
    AddVeyonRoom
}

/// <summary>OS identity and product identity captured by the non-elevated UI before UAC.</summary>
public sealed record WorkerCallerIdentity(
    int ProcessId,
    string UserSid,
    int SessionId,
    string ExecutablePath,
    string ProductVersion,
    VeyonCampusRole Role,
    long StartTimeUtcTicks);

/// <summary>
/// A closed, versioned request contract. Optional fields are accepted only
/// for the exact operation that needs them; the worker never accepts commands,
/// scripts, executable paths, or working directories from the request.
/// </summary>
public sealed record PrivilegedWorkerRequest(
    int ProtocolVersion,
    Guid RequestId,
    WorkerCallerIdentity Caller,
    PrivilegedWorkerOperation Operation,
    string? PackageRoot = null,
    string? InstallerPath = null,
    bool? IsTeacher = null,
    string? ComputerName = null,
    string? AccountName = null,
    string? ExpectedSid = null,
    byte[]? SecretUtf8 = null,
    string? CampusId = null,
    string? LocationName = null,
    IReadOnlyList<VeyonNetworkComputer>? Computers = null);

public sealed record PrivilegedWorkerResponse(
    int ProtocolVersion,
    Guid RequestId,
    StepResult Result,
    string? PublicKeyPem = null,
    bool? TeacherKeyCreated = null,
    string? WorkerUserSid = null,
    int? WorkerSessionId = null);

public static class PrivilegedWorkerProtocol
{
    public const int CurrentVersion = 1;
    public const int MaximumFrameBytes = 64 * 1024;
    public const int MaximumSecretBytes = 512;
    private const int MaximumPathChars = 32_000;
    private const int MaximumTextChars = 256;
    private const int MaximumDetailChars = 8_192;
    private static readonly Regex LocalSidPattern = new(
        "^S-1-5-21-[0-9]+-[0-9]+-[0-9]+-[0-9]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static byte[] SerializeRequest(PrivilegedWorkerRequest request)
    {
        ValidateRequest(request);
        return SerializeBounded(request);
    }

    public static PrivilegedWorkerRequest DeserializeRequest(ReadOnlySpan<byte> payload)
    {
        ValidateFrame(payload);
        RejectDuplicateProperties(payload);
        PrivilegedWorkerRequest request;
        try
        {
            request = JsonSerializer.Deserialize<PrivilegedWorkerRequest>(payload, JsonOptions)
                      ?? throw new InvalidDataException("Worker request is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Worker request does not match protocol v1.", exception);
        }
        ValidateRequest(request);
        return request;
    }

    public static byte[] SerializeResponse(PrivilegedWorkerResponse response)
    {
        ValidateResponse(response);
        return SerializeBounded(response);
    }

    public static PrivilegedWorkerResponse DeserializeResponse(ReadOnlySpan<byte> payload)
    {
        ValidateFrame(payload);
        RejectDuplicateProperties(payload);
        PrivilegedWorkerResponse response;
        try
        {
            response = JsonSerializer.Deserialize<PrivilegedWorkerResponse>(payload, JsonOptions)
                       ?? throw new InvalidDataException("Worker response is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Worker response does not match protocol v1.", exception);
        }
        ValidateResponse(response);
        return response;
    }

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.Length is <= 0 or > MaximumFrameBytes)
            throw new InvalidDataException("Worker frame exceeds the protocol size limit.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(header);
    }

    public static async Task<byte[]> ReadFrameAsync(Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        CryptographicOperations.ZeroMemory(header);
        if (length is <= 0 or > MaximumFrameBytes)
            throw new InvalidDataException("Worker frame length is invalid.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    public static void ValidateRequest(PrivilegedWorkerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != CurrentVersion || request.RequestId == Guid.Empty)
            throw new InvalidDataException("Worker request protocol or request ID is invalid.");
        ValidateCaller(request.Caller);
        if (!Enum.IsDefined(request.Operation))
            throw new InvalidDataException("Worker operation is not allowed.");

        switch (request.Operation)
        {
            case PrivilegedWorkerOperation.CreateStudentAccount:
                RequireOnly(request, nameof(request.AccountName), nameof(request.ExpectedSid), nameof(request.SecretUtf8));
                ValidateAccountName(request.AccountName);
                ValidateOptionalSid(request.ExpectedSid);
                ValidateSecret(request.SecretUtf8, optional: true);
                break;
            case PrivilegedWorkerOperation.ChangeAdminPassword:
                RequireOnly(request, nameof(request.AccountName), nameof(request.ExpectedSid), nameof(request.SecretUtf8));
                ValidateAccountName(request.AccountName);
                ValidateRequiredSid(request.ExpectedSid);
                ValidateSecret(request.SecretUtf8, optional: false);
                break;
            case PrivilegedWorkerOperation.RenameComputer:
                RequireOnly(request, nameof(request.ComputerName));
                ValidateComputerName(request.ComputerName);
                break;
            case PrivilegedWorkerOperation.InstallVeyon:
                RequireOnly(request, nameof(request.PackageRoot), nameof(request.InstallerPath), nameof(request.IsTeacher));
                ValidatePath(request.InstallerPath, required: true);
                ValidatePath(request.PackageRoot, required: request.IsTeacher != true);
                if (request.IsTeacher is null)
                    throw new InvalidDataException("Veyon role must be explicit.");
                if (request.Caller.Role == VeyonCampusRole.TeacherConsole && request.IsTeacher != true ||
                    request.Caller.Role == VeyonCampusRole.StudentSetup && request.IsTeacher != false)
                    throw new InvalidDataException("The requested Veyon role does not match the calling app role.");
                break;
            case PrivilegedWorkerOperation.ConfigureVeyon:
                RequireOnly(request, nameof(request.PackageRoot), nameof(request.IsTeacher));
                ValidatePath(request.PackageRoot, required: true);
                if (request.IsTeacher is null || request.IsTeacher !=
                    (request.Caller.Role == VeyonCampusRole.TeacherConsole))
                    throw new InvalidDataException("The Veyon configuration role does not match the calling app role.");
                break;
            case PrivilegedWorkerOperation.InstallWebsitePolicyAgent:
                RequireOnly(request, nameof(request.PackageRoot));
                if (request.Caller.Role != VeyonCampusRole.StudentSetup)
                    throw new InvalidDataException("Website policy agent installation is student-only.");
                ValidatePath(request.PackageRoot, required: true);
                break;
            case PrivilegedWorkerOperation.UninstallWebsitePolicyAgent:
                RequireOnly(request);
                if (request.Caller.Role != VeyonCampusRole.StudentSetup)
                    throw new InvalidDataException("Website policy agent removal is student-only.");
                break;
            case PrivilegedWorkerOperation.ExportTeacherPublicKey:
                RequireOnly(request, nameof(request.CampusId));
                if (request.Caller.Role != VeyonCampusRole.TeacherConsole)
                    throw new InvalidDataException("Teacher public key export is teacher-only.");
                ValidateText(request.CampusId, "Campus ID", maximum: 100);
                break;
            case PrivilegedWorkerOperation.ConfigureTeacherAuthentication:
                RequireOnly(request);
                if (request.Caller.Role != VeyonCampusRole.TeacherConsole)
                    throw new InvalidDataException("Teacher authentication configuration is teacher-only.");
                break;
            case PrivilegedWorkerOperation.AddVeyonRoom:
                RequireOnly(request, nameof(request.LocationName), nameof(request.Computers));
                if (request.Caller.Role != VeyonCampusRole.TeacherConsole)
                    throw new InvalidDataException("Veyon room directory writes are teacher-only.");
                ValidateText(request.LocationName, "Location name", maximum: 100);
                if (request.Computers is not { Count: > 0 and <= 150 })
                    throw new InvalidDataException("A Veyon room must contain 1–150 computers.");
                foreach (var computer in request.Computers)
                {
                    if (computer is null) throw new InvalidDataException("Veyon room contains an empty computer record.");
                    ValidateText(computer.ComputerName, "Computer name", maximum: 100);
                    ValidateText(computer.Host, "Computer host", maximum: 253);
                    _ = VeyonHostAddress.NormalizeOverride(computer.Host);
                    ValidateText(computer.DisplayName, "Computer display name", maximum: 100);
                    if (!string.IsNullOrWhiteSpace(computer.StudentName))
                        ValidateText(computer.StudentName, "Student name", maximum: 100);
                }
                break;
            default:
                throw new InvalidDataException("Worker operation is not allowed.");
        }
    }

    public static void ValidateResponse(PrivilegedWorkerResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.ProtocolVersion != CurrentVersion || response.RequestId == Guid.Empty || response.Result is null)
            throw new InvalidDataException("Worker response protocol, request ID, or result is invalid.");
        if (response.Result.StepId is null || response.Result.Detail is null ||
            response.Result.StepId.Length is 0 or > 80 || response.Result.Detail.Length > MaximumDetailChars ||
            response.Result.Status is null || !IsKnownStatus(response.Result.Status))
            throw new InvalidDataException("Worker response status or detail is invalid.");
        if (response.PublicKeyPem is { Length: > 16_384 })
            throw new InvalidDataException("Worker public key response exceeds the size limit.");
        if (response.WorkerUserSid is not null && !IsValidLocalSid(response.WorkerUserSid) ||
            response.WorkerSessionId is < 0)
            throw new InvalidDataException("Worker response identity is invalid.");
    }

    public static bool IsValidLocalSid(string? sid) => sid is not null && LocalSidPattern.IsMatch(sid);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            MaxDepth = 16,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter<VeyonCampusRole>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<PrivilegedWorkerOperation>(allowIntegerValues: false));
        return options;
    }

    private static byte[] SerializeBounded<T>(T value)
    {
        var result = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (result.Length is <= 0 or > MaximumFrameBytes)
        {
            CryptographicOperations.ZeroMemory(result);
            throw new InvalidDataException("Serialized Worker message exceeds the protocol size limit.");
        }
        return result;
    }

    private static void ValidateFrame(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is <= 0 or > MaximumFrameBytes)
            throw new InvalidDataException("Worker frame size is invalid.");
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> payload)
    {
        try
        {
            var objects = new Stack<HashSet<string>>();
            var reader = new Utf8JsonReader(payload, new JsonReaderOptions { MaxDepth = 16 });
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objects.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        if (objects.Count == 0) throw new InvalidDataException("Worker frame has invalid JSON structure.");
                        objects.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        if (objects.Count == 0 || !objects.Peek().Add(reader.GetString() ?? string.Empty))
                            throw new InvalidDataException("Worker frame contains a duplicate JSON property.");
                        break;
                }
            }
            if (objects.Count != 0) throw new InvalidDataException("Worker frame has invalid JSON structure.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Worker frame is not valid JSON.", exception);
        }
    }

    private static void ValidateCaller(WorkerCallerIdentity caller)
    {
        ValidateCallerIdentity(caller);
    }

    public static void ValidateCallerIdentity(WorkerCallerIdentity caller)
    {
        if (caller is null || caller.ProcessId <= 0 || caller.SessionId < 0 || caller.StartTimeUtcTicks <= 0 ||
            !IsValidLocalSid(caller.UserSid) || !Enum.IsDefined(caller.Role) || caller.ExecutablePath is null ||
            caller.ProductVersion is null)
            throw new InvalidDataException("Worker caller identity is incomplete.");
        ValidatePath(caller.ExecutablePath, required: true);
        if (caller.ProductVersion.Length is 0 or > MaximumTextChars || caller.ProductVersion.Any(char.IsControl))
            throw new InvalidDataException("Worker caller product version is invalid.");
    }

    private static void RequireOnly(PrivilegedWorkerRequest request, params string[] allowed)
    {
        if (request.PackageRoot is not null && !allowed.Contains(nameof(request.PackageRoot), StringComparer.Ordinal) ||
            request.InstallerPath is not null && !allowed.Contains(nameof(request.InstallerPath), StringComparer.Ordinal) ||
            request.IsTeacher is not null && !allowed.Contains(nameof(request.IsTeacher), StringComparer.Ordinal) ||
            request.ComputerName is not null && !allowed.Contains(nameof(request.ComputerName), StringComparer.Ordinal) ||
            request.AccountName is not null && !allowed.Contains(nameof(request.AccountName), StringComparer.Ordinal) ||
            request.ExpectedSid is not null && !allowed.Contains(nameof(request.ExpectedSid), StringComparer.Ordinal) ||
            request.SecretUtf8 is not null && !allowed.Contains(nameof(request.SecretUtf8), StringComparer.Ordinal) ||
            request.CampusId is not null && !allowed.Contains(nameof(request.CampusId), StringComparer.Ordinal) ||
            request.LocationName is not null && !allowed.Contains(nameof(request.LocationName), StringComparer.Ordinal) ||
            request.Computers is not null && !allowed.Contains(nameof(request.Computers), StringComparer.Ordinal))
            throw new InvalidDataException("Worker request contains fields that do not apply to this operation.");
    }

    private static void ValidateAccountName(string? value)
    {
        ValidateText(value, "Account name", maximum: 20);
        if (!WindowsAccountAdapter.IsValidAccountName(value!, "Worker account"))
            throw new InvalidDataException("Worker account name contains unsupported characters.");
    }

    private static void ValidateRequiredSid(string? value)
    {
        if (!IsValidLocalSid(value)) throw new InvalidDataException("Worker target SID is invalid.");
    }

    private static void ValidateOptionalSid(string? value)
    {
        if (value is not null) ValidateRequiredSid(value);
    }

    private static void ValidateSecret(byte[]? secret, bool optional)
    {
        if (secret is null)
        {
            if (!optional) throw new InvalidDataException("Worker operation requires a secret input.");
            return;
        }
        if (secret.Length is 0 or > MaximumSecretBytes)
            throw new InvalidDataException("Worker secret size is invalid.");
        Span<char> characters = stackalloc char[MaximumSecretBytes];
        try
        {
            var encoding = new System.Text.UTF8Encoding(false, true);
            var characterCount = encoding.GetChars(secret, characters);
            var hasControl = false;
            foreach (var character in characters[..characterCount]) hasControl |= char.IsControl(character);
            if (characterCount is 0 or > 127 || hasControl)
                throw new InvalidDataException("Worker secret has an invalid length or contains control characters.");
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Worker secret is not valid UTF-8.", exception);
        }
        finally { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters)); }
    }

    private static void ValidateComputerName(string? value)
    {
        ValidateText(value, "Computer name", maximum: 15);
        var computerName = value!;
        if (!computerName.Any(char.IsAsciiLetter) ||
            !Regex.IsMatch(computerName, "^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Worker computer name is invalid.");
    }

    private static void ValidatePath(string? value, bool required)
    {
        if (value is null)
        {
            if (required) throw new InvalidDataException("Worker file-system path is required.");
            return;
        }
        if (value.Length is 0 or > MaximumPathChars || value.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(value) || value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException("Worker file-system path is invalid.");
    }

    private static void ValidateText(string? value, string label, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(character => char.IsControl(character) || character is ';' or '"'))
            throw new InvalidDataException($"Worker {label.ToLowerInvariant()} is invalid.");
    }

    private static bool IsKnownStatus(string status) => status is
        ExecutionPlan.NotStarted or ExecutionPlan.Running or ExecutionPlan.Succeeded or ExecutionPlan.Failed or
        ExecutionPlan.Cancelled or ExecutionPlan.Skipped or ExecutionPlan.RequiresReboot or
        ExecutionPlan.PartiallyCompleted or ExecutionPlan.NeedsReview;
}
