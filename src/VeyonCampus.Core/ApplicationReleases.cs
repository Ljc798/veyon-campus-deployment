using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

public enum ApplicationReleaseRole
{
    TeacherConsole,
    StudentSetup
}

public sealed record ApplicationReleasePolicyCapabilities(
    [property: JsonPropertyName("studentSystemPolicy")] int StudentSystemPolicy);

public sealed record ApplicationReleaseManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("downloadUrl")] string DownloadUrl,
    [property: JsonPropertyName("policyCapabilities")] ApplicationReleasePolicyCapabilities? PolicyCapabilities = null);

public sealed record ApplicationReleaseEnvelope(
    [property: JsonPropertyName("manifest")] ApplicationReleaseManifest Manifest,
    [property: JsonPropertyName("signatureAlgorithm")] string SignatureAlgorithm,
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonPropertyName("publishedAt")] DateTimeOffset PublishedAt);

public sealed record ApplicationReleaseCheckResult(
    ApplicationReleaseEnvelope? Release,
    bool IsNewer,
    string CurrentVersion);

public static class ApplicationReleaseTrust
{
    public const string PublicKeyResourceName = "VeyonCampus.Core.ApplicationReleasePublicKey.pem";

    public static string LoadPinnedPublicKeyPem()
    {
        using var stream = typeof(ApplicationReleaseTrust).Assembly.GetManifestResourceStream(PublicKeyResourceName)
                           ?? throw new InvalidOperationException("此版本未嵌入 Developer Release 公钥；已安全停用应用更新。请使用固定公钥重新构建安装器。");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var publicKeyPem = reader.ReadToEnd();
        if (publicKeyPem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Developer Release 密钥资源只能包含公钥，不能包含私钥。");
        using var publicKey = RSA.Create();
        try { publicKey.ImportFromPem(publicKeyPem); }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("嵌入的 Developer Release 公钥无效。", exception);
        }
        if (publicKey.KeySize is < 2048 or > 4096)
            throw new InvalidDataException("嵌入的 Developer Release RSA 公钥位长不受支持。");
        return publicKeyPem;
    }
}

public sealed class ApplicationReleaseClient
{
    public const long MaximumArtifactBytes = 512L * 1024 * 1024;
    private const int MaximumMetadataBytes = 64 * 1024;
    private const string SignatureAlgorithm = "RSA-PSS-SHA256";
    private static readonly Regex VersionPattern = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)" +
        "(?:-((?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)" +
        "(?:\\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?" +
        "(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HttpClient SharedClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None
    }) { Timeout = TimeSpan.FromMinutes(20) };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly Uri _apiBaseAddress;
    private readonly HttpClient _client;
    private readonly string _publicKeyPem;
    public Uri ApiBaseAddress => _apiBaseAddress;

    public ApplicationReleaseClient()
        : this(DeploymentPackageApiConfiguration.GetApiBaseAddress(), SharedClient,
            ApplicationReleaseTrust.LoadPinnedPublicKeyPem())
    {
    }

    public ApplicationReleaseClient(Uri apiBaseAddress, HttpClient client, string publicKeyPem)
    {
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        if ((apiBaseAddress.Scheme != Uri.UriSchemeHttps &&
             !(apiBaseAddress.Scheme == Uri.UriSchemeHttp && apiBaseAddress.IsLoopback)) ||
            !string.IsNullOrEmpty(apiBaseAddress.UserInfo) || !string.IsNullOrEmpty(apiBaseAddress.Query) ||
            !string.IsNullOrEmpty(apiBaseAddress.Fragment) ||
            apiBaseAddress.AbsolutePath != "/")
            throw new InvalidDataException("更新 API 地址必须是 HTTPS 根地址；仅本机回环地址允许使用 HTTP。");
        _apiBaseAddress = apiBaseAddress;
        _client = client;
        _publicKeyPem = publicKeyPem;
        using var publicKey = ImportPublicKey(publicKeyPem);
    }

    public async Task<ApplicationReleaseCheckResult> CheckLatestAsync(ApplicationReleaseRole role,
        string currentVersion, CancellationToken cancellationToken = default)
    {
        _ = ParseVersion(currentVersion);
        var roleName = RoleName(role);
        var url = new Uri(_apiBaseAddress,
            $"v2/releases/latest?role={Uri.EscapeDataString(roleName)}&architecture=win-x64");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("版本目录暂时不可用。", null, response.StatusCode);
        var responseBytes = await ReadBoundedAsync(response.Content, MaximumMetadataBytes, cancellationToken)
            .ConfigureAwait(false);
        LatestReleaseResponse result;
        try
        {
            result = JsonSerializer.Deserialize<LatestReleaseResponse>(responseBytes, JsonOptions)
                     ?? throw new InvalidDataException("版本目录响应为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("版本目录响应格式无效。", exception);
        }
        return EvaluateLatest(result.Release, role, currentVersion);
    }

    public ApplicationReleaseCheckResult EvaluateLatest(ApplicationReleaseEnvelope? release,
        ApplicationReleaseRole role, string currentVersion)
    {
        _ = ParseVersion(currentVersion);
        if (release is null) return new(null, false, currentVersion);
        Verify(release, role, _apiBaseAddress, _publicKeyPem);
        return new(release, CompareVersions(release.Manifest.Version, currentVersion) > 0, currentVersion);
    }

    public async Task<string> DownloadAsync(ApplicationReleaseEnvelope release,
        ApplicationReleaseRole expectedRole, string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Verify(release, expectedRole, _apiBaseAddress, _publicKeyPem);

        var targetDirectory = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(targetDirectory);
        if ((File.GetAttributes(targetDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("更新暂存目录不能是符号链接或重解析点。");
        var fileName = release.Manifest.FileName;
        var targetPath = Path.Combine(targetDirectory, fileName);
        var envelopePath = targetPath + ".release.json";
        if (File.Exists(targetPath) || Directory.Exists(targetPath) || File.Exists(envelopePath) ||
            Directory.Exists(envelopePath))
        {
            if (File.Exists(targetPath) && File.Exists(envelopePath) && !Directory.Exists(envelopePath))
            {
                var existingRelease = ReadVerifiedStagedRelease(targetPath, expectedRole, _apiBaseAddress,
                    _publicKeyPem);
                if (existingRelease.Manifest == release.Manifest &&
                    existingRelease.SignatureAlgorithm == release.SignatureAlgorithm &&
                    existingRelease.Signature == release.Signature)
                    return targetPath;
            }
            throw new IOException("更新安装器已存在且不匹配当前签名发布；请人工核对后处理。");
        }
        var temporaryPath = Path.Combine(targetDirectory, "." + Guid.NewGuid().ToString("N") + ".partial");
        var envelopeTemporaryPath = temporaryPath + ".release.json";
        var artifactMoved = false;

        try
        {
            using var response = await SendArtifactRequestAsync(new Uri(release.Manifest.DownloadUrl),
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("安装器下载暂时不可用。", null, response.StatusCode);
            if (response.Content.Headers.ContentLength is { } contentLength &&
                contentLength != release.Manifest.SizeBytes)
                throw new InvalidDataException("安装器大小与已签名发布清单不符。");

            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                var buffer = new byte[64 * 1024];
                long totalBytes = 0;
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > release.Manifest.SizeBytes || totalBytes > MaximumArtifactBytes)
                        throw new InvalidDataException("安装器超过已签名清单声明的大小。");
                    digest.AppendData(buffer, 0, bytesRead);
                    await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (totalBytes != release.Manifest.SizeBytes)
                    throw new InvalidDataException("安装器下载不完整。");
            }

            var actualSha256 = Convert.ToHexString(digest.GetHashAndReset());
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actualSha256),
                    Encoding.ASCII.GetBytes(release.Manifest.Sha256)))
                throw new InvalidDataException("安装器 SHA-256 与已签名发布清单不符。");
            await using (var envelopeStream = new FileStream(envelopeTemporaryPath, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(envelopeStream, release, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await envelopeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, targetPath, overwrite: false);
            artifactMoved = true;
            File.Move(envelopeTemporaryPath, envelopePath, overwrite: false);
            return targetPath;
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try { if (File.Exists(envelopeTemporaryPath)) File.Delete(envelopeTemporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (artifactMoved)
            {
                try { if (File.Exists(targetPath)) File.Delete(targetPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    public static ApplicationReleaseEnvelope ReadVerifiedStagedRelease(string installerPath,
        ApplicationReleaseRole expectedRole, Uri apiBaseAddress, string publicKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        var fullInstallerPath = Path.GetFullPath(installerPath);
        if ((File.GetAttributes(fullInstallerPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("暂存安装器不能是符号链接或重解析点。");
        var envelopePath = fullInstallerPath + ".release.json";
        if ((File.GetAttributes(envelopePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("暂存发布清单不能是符号链接或重解析点。");
        var envelopeInfo = new FileInfo(envelopePath);
        if (envelopeInfo.Length is < 1 or > MaximumMetadataBytes)
            throw new InvalidDataException("暂存发布清单大小无效。");
        ApplicationReleaseEnvelope release;
        try
        {
            release = JsonSerializer.Deserialize<ApplicationReleaseEnvelope>(File.ReadAllBytes(envelopePath),
                          JsonOptions) ?? throw new InvalidDataException("暂存发布清单为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("暂存发布清单格式无效。", exception);
        }
        Verify(release, expectedRole, apiBaseAddress, publicKeyPem);
        if (!string.Equals(Path.GetFileName(fullInstallerPath), release.Manifest.FileName,
                StringComparison.Ordinal))
            throw new InvalidDataException("暂存安装器文件名与已签名发布清单不符。");
        using var artifact = new FileStream(fullInstallerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (artifact.Length != release.Manifest.SizeBytes)
            throw new InvalidDataException("暂存安装器大小与已签名发布清单不符。");
        var actualSha256 = Convert.ToHexString(SHA256.HashData(artifact));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actualSha256),
                Encoding.ASCII.GetBytes(release.Manifest.Sha256)))
            throw new InvalidDataException("暂存安装器 SHA-256 与已签名发布清单不符。");
        return release;
    }

    public static string StageVerifiedOfflineRelease(string installerPath, string stagingDirectory,
        ApplicationReleaseRole expectedRole, Uri apiBaseAddress, string publicKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        var sourcePath = Path.GetFullPath(installerPath);
        var release = ReadVerifiedStagedRelease(sourcePath, expectedRole, apiBaseAddress, publicKeyPem);
        var sourceEnvelopePath = sourcePath + ".release.json";
        var targetDirectory = Path.GetFullPath(stagingDirectory);
        Directory.CreateDirectory(targetDirectory);
        if ((File.GetAttributes(targetDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("离线更新暂存目录不能是符号链接或重解析点。");

        var targetPath = Path.Combine(targetDirectory, release.Manifest.FileName);
        var targetEnvelopePath = targetPath + ".release.json";
        var targetExists = PathExistsOrLink(targetPath);
        var targetEnvelopeExists = PathExistsOrLink(targetEnvelopePath);
        if (targetExists || targetEnvelopeExists)
        {
            if (targetExists && targetEnvelopeExists)
            {
                var existing = ReadVerifiedStagedRelease(targetPath, expectedRole, apiBaseAddress, publicKeyPem);
                if (existing == release) return targetPath;
            }
            throw new IOException("离线更新暂存位置已有不同或不完整的文件；未覆盖现有内容。");
        }

        var temporaryPath = Path.Combine(targetDirectory, "." + Guid.NewGuid().ToString("N") + ".partial");
        var temporaryEnvelopePath = temporaryPath + ".release.json";
        var artifactMoved = false;
        var envelopeMoved = false;
        try
        {
            CopyBoundedRegularFile(sourcePath, temporaryPath, release.Manifest.SizeBytes,
                release.Manifest.SizeBytes, "离线安装器");
            CopyBoundedRegularFile(sourceEnvelopePath, temporaryEnvelopePath, null, MaximumMetadataBytes,
                "离线发布清单");
            File.Move(temporaryPath, targetPath, overwrite: false);
            artifactMoved = true;
            File.Move(temporaryEnvelopePath, targetEnvelopePath, overwrite: false);
            envelopeMoved = true;

            var staged = ReadVerifiedStagedRelease(targetPath, expectedRole, apiBaseAddress, publicKeyPem);
            if (staged != release)
                throw new InvalidDataException("暂存后的离线发布清单与所选清单不一致。");
            return targetPath;
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            TryDeleteFile(temporaryEnvelopePath);
            if (artifactMoved) TryDeleteFile(targetPath);
            if (envelopeMoved) TryDeleteFile(targetEnvelopePath);
            throw;
        }
    }

    public static int CompareVersions(string left, string right)
    {
        var leftVersion = ParseVersion(left);
        var rightVersion = ParseVersion(right);
        for (var index = 0; index < 3; index++)
        {
            var comparison = leftVersion.Core[index].CompareTo(rightVersion.Core[index]);
            if (comparison != 0) return comparison;
        }
        if (leftVersion.Prerelease is null || rightVersion.Prerelease is null)
        {
            if (leftVersion.Prerelease is null && rightVersion.Prerelease is null) return 0;
            return leftVersion.Prerelease is null ? 1 : -1;
        }
        for (var index = 0; index < Math.Max(leftVersion.Prerelease.Length, rightVersion.Prerelease.Length); index++)
        {
            if (index >= leftVersion.Prerelease.Length) return -1;
            if (index >= rightVersion.Prerelease.Length) return 1;
            var leftPart = leftVersion.Prerelease[index];
            var rightPart = rightVersion.Prerelease[index];
            if (leftPart == rightPart) continue;
            var leftNumeric = IsNumericIdentifier(leftPart);
            var rightNumeric = IsNumericIdentifier(rightPart);
            if (leftNumeric && rightNumeric)
            {
                var numericComparison = BigInteger.Parse(leftPart, CultureInfo.InvariantCulture)
                    .CompareTo(BigInteger.Parse(rightPart, CultureInfo.InvariantCulture));
                if (numericComparison != 0) return numericComparison;
                continue;
            }
            if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
            var ordinalComparison = string.CompareOrdinal(leftPart, rightPart);
            if (ordinalComparison != 0) return ordinalComparison;
        }
        return 0;
    }

    public static byte[] CreateCanonicalPayload(ApplicationReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", manifest.SchemaVersion);
            writer.WriteString("product", manifest.Product);
            writer.WriteString("role", manifest.Role);
            writer.WriteString("version", manifest.Version);
            writer.WriteString("architecture", manifest.Architecture);
            writer.WriteString("fileName", manifest.FileName);
            writer.WriteNumber("sizeBytes", manifest.SizeBytes);
            writer.WriteString("sha256", manifest.Sha256);
            writer.WriteString("downloadUrl", manifest.DownloadUrl);
            if (manifest.SchemaVersion == 2)
            {
                writer.WriteStartObject("policyCapabilities");
                writer.WriteNumber("studentSystemPolicy", manifest.PolicyCapabilities!.StudentSystemPolicy);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static void Verify(ApplicationReleaseEnvelope release, ApplicationReleaseRole expectedRole,
        Uri apiBaseAddress, string publicKeyPem)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        if (publicKeyPem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新验证器只能使用 Developer Release 公钥。");
        var manifest = release.Manifest ?? throw new InvalidDataException("发布清单缺失。");
        var roleName = RoleName(expectedRole);
        var product = expectedRole == ApplicationReleaseRole.TeacherConsole
            ? "VeyonCampus.TeacherConsole"
            : "VeyonCampus.StudentSetup";
        var roleFileName = expectedRole == ApplicationReleaseRole.TeacherConsole ? "Teacher" : "Student";
        if (manifest.SchemaVersion is not (1 or 2) || manifest.Role != roleName || manifest.Product != product ||
            manifest.Architecture != "win-x64" || !IsSemanticVersion(manifest.Version) ||
            manifest.FileName != $"VeyonCampus-{roleFileName}-Setup-{manifest.Version}-win-x64.exe" ||
            manifest.SizeBytes is < 1 or > MaximumArtifactBytes ||
            manifest.Sha256 is null || !Regex.IsMatch(manifest.Sha256, "^[A-F0-9]{64}$", RegexOptions.CultureInvariant) ||
            release.SignatureAlgorithm != SignatureAlgorithm || string.IsNullOrEmpty(release.Signature) ||
            release.PublishedAt == default ||
            (manifest.SchemaVersion == 1 && manifest.PolicyCapabilities is not null) ||
            (manifest.SchemaVersion == 2 && (manifest.PolicyCapabilities is null ||
                manifest.PolicyCapabilities.StudentSystemPolicy < 1)))
            throw new InvalidDataException("发布清单的角色、版本、文件或签名元数据无效。");

        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != apiBaseAddress.Scheme ||
            !string.Equals(downloadUri.Authority, apiBaseAddress.Authority, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(downloadUri.UserInfo) || !string.IsNullOrEmpty(downloadUri.Fragment))
            throw new InvalidDataException("发布清单下载地址不属于固定的版本 API 来源。");
        var pathSegments = downloadUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathSegments.Length != 4 || pathSegments[0] != "v1" || pathSegments[1] != "releases" ||
            pathSegments[3] != "artifact" || !Guid.TryParse(pathSegments[2], out var releaseId) || releaseId == Guid.Empty ||
            !string.IsNullOrEmpty(downloadUri.Query))
            throw new InvalidDataException("发布清单下载地址路径无效。");

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(release.Signature);
            if (Convert.ToBase64String(signature) != release.Signature)
                throw new FormatException();
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("发布清单签名编码无效。", exception);
        }
        using var publicKey = ImportPublicKey(publicKeyPem);
        if (signature.Length != publicKey.KeySize / 8 ||
            !publicKey.VerifyData(CreateCanonicalPayload(manifest), signature,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("Developer Release 签名无效；已拒绝此版本。");
    }

    private async Task<HttpResponseMessage> SendArtifactRequestAsync(Uri requestUri,
        CancellationToken cancellationToken)
    {
        var currentUri = requestUri;
        for (var redirectCount = 0; ; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Redirect)
                return response;
            if (redirectCount >= 2 || response.Headers.Location is not { } location)
            {
                response.Dispose();
                throw new InvalidDataException("安装器下载重定向次数或地址无效。");
            }
            Uri nextUri;
            try { nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location); }
            finally { response.Dispose(); }
            if ((nextUri.Scheme != Uri.UriSchemeHttps &&
                 !(_apiBaseAddress.IsLoopback && nextUri.Scheme == Uri.UriSchemeHttp && nextUri.IsLoopback)) ||
                !string.IsNullOrEmpty(nextUri.UserInfo) || !string.IsNullOrEmpty(nextUri.Fragment))
                throw new InvalidDataException("安装器下载重定向必须指向无凭据的 HTTPS 地址。");
            currentUri = nextUri;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } contentLength && contentLength > maximumBytes)
            throw new InvalidDataException("版本目录响应过大。");
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var target = new MemoryStream();
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (target.Length + bytesRead > maximumBytes)
                throw new InvalidDataException("版本目录响应过大。");
            target.Write(buffer, 0, bytesRead);
        }
        return target.ToArray();
    }

    private static RSA ImportPublicKey(string publicKeyPem)
    {
        var key = RSA.Create();
        try
        {
            key.ImportFromPem(publicKeyPem);
            if (key.KeySize is < 2048 or > 4096)
                throw new CryptographicException("Developer Release RSA 公钥位长不受支持。");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static void CopyBoundedRegularFile(string sourcePath, string destinationPath, long? expectedLength,
        long maximumLength, string description)
    {
        if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"{description}不能是符号链接或重解析点。");
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length < 1 || source.Length > maximumLength ||
            (expectedLength is { } expected && source.Length != expected))
            throw new InvalidDataException($"{description}大小在暂存前发生变化。");
        using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.WriteThrough);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int bytesRead;
        while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += bytesRead;
            if (total > maximumLength || (expectedLength is { } expectedBytes && total > expectedBytes))
                throw new InvalidDataException($"{description}在暂存时超过已验证大小。");
            destination.Write(buffer, 0, bytesRead);
        }
        if (expectedLength is { } required && total != required)
            throw new InvalidDataException($"{description}在暂存时被截断。");
        destination.Flush(flushToDisk: true);
    }

    private static bool PathExistsOrLink(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) return true;
        try
        {
            return new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string RoleName(ApplicationReleaseRole role) => role switch
    {
        ApplicationReleaseRole.TeacherConsole => "TeacherConsole",
        ApplicationReleaseRole.StudentSetup => "StudentSetup",
        _ => throw new InvalidDataException("更新角色无效。")
    };

    private static bool IsSemanticVersion(string value) => value is { Length: <= 64 } && VersionPattern.IsMatch(value);

    private static SemanticVersion ParseVersion(string value)
    {
        if (value is null || !IsSemanticVersion(value))
            throw new InvalidDataException("版本必须符合 SemVer X.Y.Z 格式。");
        var withoutBuild = value.Split('+', 2)[0];
        var coreAndPrerelease = withoutBuild.Split('-', 2);
        var core = coreAndPrerelease[0].Split('.')
            .Select(part => BigInteger.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        var prerelease = coreAndPrerelease.Length == 1 ? null : coreAndPrerelease[1].Split('.');
        return new(core, prerelease);
    }

    private static bool IsNumericIdentifier(string value) =>
        value.Length > 0 && value.All(char.IsAsciiDigit);

    private sealed record LatestReleaseResponse(
        [property: JsonPropertyName("release")] ApplicationReleaseEnvelope? Release);

    private sealed record SemanticVersion(BigInteger[] Core, string[]? Prerelease);
}
