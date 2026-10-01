using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using VeyonCampus.Core;

internal static class DeploymentPackageEndpoints
{
    public const string BucketId = "deployment-package-artifacts";
    private const long MaximumRequestBytes = 128 * 1024;
    private const int DefaultPageSize = 20;
    private const int MaximumPageSize = 49;

    public static void Map(WebApplication app)
    {
        app.MapGet("/v1/deployment-packages", SearchAsync);
        app.MapPost("/v1/deployment-packages", PublishAsync)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBytes));
        app.MapPost("/v1/deployment-packages/{packageId:guid}/download", DownloadAsync)
            .WithMetadata(new RequestSizeLimitAttribute(4 * 1024));
        app.MapPost("/v1/deployment-packages/{packageId:guid}/withdraw", WithdrawAsync)
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024));
    }

    private static async Task<IResult> GetMyPublishableCampusesAsync(
        HttpRequest request,
        CloudBaseDeploymentPackageStore store,
        CancellationToken cancellationToken)
    {
        if (!TryGetBearerToken(request, out var accessToken))
            return Results.Unauthorized();

        try
        {
            var user = await store.GetCurrentUserAsync(accessToken, cancellationToken);
            if (user is null) return Results.Unauthorized();
            var campuses = await store.GetPublishableCampusesAsync(user.UserId, cancellationToken);
            return Results.Ok(new
            {
                items = campuses.Select(campus => new { campusId = campus.CampusId, campusName = campus.CampusName })
            });
        }
        catch (CloudBaseUnavailableException)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
        catch (CloudBaseRejectedException)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
        catch (HttpRequestException)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
    }

    private static async Task<IResult> SearchAsync(
        HttpRequest request,
        CloudBaseDeploymentPackageStore store,
        CancellationToken cancellationToken)
    {
        if (!TryReadPositiveLong(request.Query["campusId"], out var campusId, optional: true))
            return Results.BadRequest(new { error = "campusId must be a positive integer" });

        var query = request.Query["query"].ToString();
        if (query.Length > 100 || query.Any(char.IsControl))
            return Results.BadRequest(new { error = "query must be at most 100 characters" });

        var limit = DefaultPageSize;
        if (request.Query.ContainsKey("limit") &&
            (!int.TryParse(request.Query["limit"], NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
             limit is < 1 or > MaximumPageSize))
            return Results.BadRequest(new { error = $"limit must be between 1 and {MaximumPageSize}" });

        var offset = 0;
        if (request.Query.ContainsKey("offset") &&
            (!int.TryParse(request.Query["offset"], NumberStyles.None, CultureInfo.InvariantCulture, out offset) ||
             offset is < 0 or > 10000))
            return Results.BadRequest(new { error = "offset must be between 0 and 10000" });

        try
        {
            var rows = await store.SearchAsync(query, campusId, limit + 1, offset, cancellationToken);
            var hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(rows.Count - 1);
            var items = rows.Select(ToPublicPackage);
            return Results.Ok(new { items, limit, offset, hasMore });
        }
        catch (HttpRequestException)
        {
            return Results.Problem("Deployment package catalog is temporarily unavailable.", statusCode: 503);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem("Deployment package catalog is temporarily unavailable.", statusCode: 503);
        }
        catch (CloudBaseRejectedException)
        {
            return Results.Problem("Deployment package catalog is temporarily unavailable.", statusCode: 503);
        }
    }

    private static async Task<IResult> PublishAsync(
        HttpRequest request,
        CloudBaseDeploymentPackageStore store,
        CancellationToken cancellationToken)
    {
        if (request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) != true)
            return Results.BadRequest(new { error = "Use multipart/form-data with campusName, publisherName, teacherPhoneLast4 and either archive or files." });

        try
        {
            var form = await request.ReadFormAsync(cancellationToken);
            if (form.Keys.Any(key => key is not ("campusName" or "publisherName" or "teacherPhoneLast4")))
                return Results.BadRequest(new { error = "Only campusName, publisherName, teacherPhoneLast4 and the archive/files upload fields are accepted." });

            var campusName = NormalizePublisherField(form["campusName"].ToString(), "校区名称");
            var publisherName = NormalizePublisherField(form["publisherName"].ToString(), "教师姓名");
            var teacherPhoneLast4 = form["teacherPhoneLast4"].ToString();
            if (teacherPhoneLast4.Length != 4 || !teacherPhoneLast4.All(char.IsAsciiDigit))
                return Results.BadRequest(new { error = "教师手机号后四位必须是 4 位数字。" });

            var input = await CanonicalizeUploadAsync(form, cancellationToken);
            using (input)
            {
                if (!string.Equals(input.Package.Campus.Normalize(NormalizationForm.FormKC).Trim(),
                        campusName, StringComparison.Ordinal))
                    throw new InvalidDataException("上传表单校区名称必须与配置包 manifest.json 一致。");

                var objectKey = DeploymentPackageStorageNaming.CreateObjectKey(campusName, input.PackageId);
                var fileName = DeploymentPackageStorageNaming.CreateFileName(campusName, input.PackageId);
                var digest = Convert.ToHexString(SHA256.HashData(input.ArchiveBytes));
                var publisherFingerprint = store.CreatePublisherFingerprintForName(publisherName);
                var phoneFingerprint = store.CreatePhoneLast4Fingerprint(teacherPhoneLast4);
                await store.UploadAsync(objectKey, input.ArchiveBytes, cancellationToken);
                try
                {
                    await store.PublishPublicAsync(input.PackageId, campusName, publisherName,
                        input.Package.ComputerPrefix, input.ArchiveBytes.Length, digest,
                        publisherFingerprint, phoneFingerprint, cancellationToken);
                }
                catch (CloudBaseRejectedException exception) when (IsDefinitivePublishRejection(exception.StatusCode))
                {
                    await store.TryDeleteAsync(objectKey, CancellationToken.None);
                    throw;
                }

                return Results.Created($"/v1/deployment-packages/{input.PackageId:D}", new
                {
                    packageId = input.PackageId,
                    campusId = (long?)null,
                    campusName,
                    computerPrefix = input.Package.ComputerPrefix,
                    schemaVersion = 3,
                    targetOs = "windows",
                    architecture = "x64",
                    fileName,
                    sizeBytes = input.ArchiveBytes.Length,
                    sha256 = digest
                });
            }
        }
        catch (InvalidDataException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (CloudBaseRejectedException exception) when (exception.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new { error = "This packageId has already been published" });
        }
        catch (CloudBaseRejectedException exception) when (exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new { error = "This packageId has already been published" });
        }
        catch (CloudBaseUnavailableException)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
        catch (CloudBaseRejectedException)
        {
            return Results.Problem("CloudBase rejected the package publication.", statusCode: 502);
        }
        catch (HttpRequestException)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
    }

    private static string NormalizePublisherField(string value, string label)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        if (normalized.Length is < 1 or > 100 || normalized.Any(char.IsControl))
            throw new InvalidDataException($"{label}必须为 1–100 个字符。");
        return normalized;
    }

    private static bool IsDefinitivePublishRejection(HttpStatusCode statusCode) =>
        (int)statusCode is 400 or 401 or 403 or 404 or 409 or 413 or 415 or 422;

    private static async Task<IResult> DownloadAsync(
        Guid packageId,
        DownloadPackageRequest body,
        HttpContext context,
        CloudBaseDeploymentPackageStore store,
        DeploymentPackageDownloadAttemptLimiter attempts,
        CancellationToken cancellationToken)
    {
        var teacherPhoneLast4 = body.TeacherPhoneLast4;
        if (teacherPhoneLast4 is not { Length: 4 } || !teacherPhoneLast4.All(char.IsAsciiDigit))
            return Results.BadRequest(new { error = "教师手机号后四位必须是 4 位数字。" });

        var clientKey = attempts.CreateKey(GetDownloadClientAddress(context), packageId);
        if (attempts.IsBlocked(clientKey, out var retryAfterSeconds))
        {
            context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        try
        {
            var phoneFingerprint = store.CreatePhoneLast4Fingerprint(teacherPhoneLast4);
            var artifact = await store.GetDownloadArtifactAsync(packageId, phoneFingerprint, cancellationToken);
            if (artifact is null)
            {
                if (!attempts.RegisterFailure(clientKey, out retryAfterSeconds))
                {
                    context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                    return Results.StatusCode(StatusCodes.Status429TooManyRequests);
                }
                return Results.Problem("教师手机号后四位不正确，或此配置包已撤回。", statusCode: 403);
            }

            attempts.RegisterSuccess(clientKey);
            var bytes = await store.DownloadAsync(artifact, cancellationToken);
            if (bytes is null) return Results.Problem("The published artifact is temporarily unavailable.", statusCode: 502);
            await store.RecordDownloadAsync(packageId, cancellationToken);

            return Results.File(bytes, "application/zip", artifact.FileName, enableRangeProcessing: false);
        }
        catch (CloudBaseUnavailableException)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
        catch (HttpRequestException)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
        catch (CloudBaseRejectedException)
        {
            return Results.NotFound(new { error = "Published package not found or no longer available" });
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
    }

    private static async Task<IResult> WithdrawAsync(
        Guid packageId,
        WithdrawPackageRequest body,
        HttpRequest request,
        CloudBaseDeploymentPackageStore store,
        CancellationToken cancellationToken)
    {
        if (!TryGetBearerToken(request, out var accessToken))
            return Results.Unauthorized();

        try
        {
            var user = await store.GetCurrentUserAsync(accessToken, cancellationToken);
            if (user is null) return Results.Unauthorized();
            if (body.Reason?.Length > 500 || body.Reason?.Any(char.IsControl) == true)
                return Results.BadRequest(new { error = "reason must be at most 500 characters and contain no control characters" });
            await store.WithdrawAsync(packageId, user.UserId, body.Reason ?? "", cancellationToken);
            return Results.NoContent();
        }
        catch (CloudBaseRejectedException exception) when (exception.Message.Contains("not found or withdrawal is not authorized", StringComparison.OrdinalIgnoreCase))
        {
            return Results.NotFound(new { error = "Published package not found or withdrawal is not authorized" });
        }
        catch (CloudBaseUnavailableException)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
        catch (HttpRequestException)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
        catch (CloudBaseRejectedException)
        {
            return Results.Problem("CloudBase rejected the withdrawal request.", statusCode: 502);
        }
    }

    private static async Task<IResult> SetPublisherAsync(
        SetPublisherRequest body,
        HttpRequest request,
        CloudBaseDeploymentPackageStore store,
        CancellationToken cancellationToken)
    {
        if (!TryGetBearerToken(request, out var accessToken))
            return Results.Unauthorized();
        var publisherUserId = body.UserId?.Trim() ?? "";
        if (publisherUserId.Length is < 1 or > 64 || publisherUserId.Any(char.IsControl))
            return Results.BadRequest(new { error = "userId must be between 1 and 64 characters" });
        if (body.CampusId <= 0 || body.IsActive is null)
            return Results.BadRequest(new { error = "campusId must be a positive integer and isActive is required" });

        try
        {
            var user = await store.GetCurrentUserAsync(accessToken, cancellationToken);
            if (user is null) return Results.Unauthorized();
            await store.SetPublisherAsync(publisherUserId, body.CampusId, body.IsActive.Value, user.UserId, cancellationToken);
            return Results.NoContent();
        }
        catch (CloudBaseRejectedException exception) when (exception.Message.Contains("only owner/admin", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Forbid();
        }
        catch (CloudBaseRejectedException exception) when (exception.Message.Contains("campus does not exist", StringComparison.OrdinalIgnoreCase))
        {
            return Results.NotFound(new { error = "Campus not found" });
        }
        catch (CloudBaseUnavailableException)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
        catch (HttpRequestException)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
        catch (CloudBaseRejectedException)
        {
            return Results.Problem("CloudBase rejected the publisher update.", statusCode: 503);
        }
    }

    private static async Task<IResult> GetPublisherAssignmentsAsync(
        HttpRequest request,
        CloudBaseDeploymentPackageStore store,
        CancellationToken cancellationToken)
    {
        if (!TryGetBearerToken(request, out var accessToken))
            return Results.Unauthorized();
        if (!TryReadPositiveLong(request.Query["campusId"], out var campusId, optional: false))
            return Results.BadRequest(new { error = "campusId must be a positive integer" });

        try
        {
            var user = await store.GetCurrentUserAsync(accessToken, cancellationToken);
            if (user is null) return Results.Unauthorized();
            if (!await store.IsPublisherManagerAsync(user.UserId, cancellationToken))
                return Results.Forbid();
            var items = await store.GetPublisherAssignmentsAsync(campusId, cancellationToken);
            return Results.Ok(new { items });
        }
        catch (CloudBaseUnavailableException)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
        catch (CloudBaseRejectedException)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
        catch (HttpRequestException)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem("CloudBase is temporarily unavailable.", statusCode: 503);
        }
    }

    private static async Task<CanonicalPackageUpload> CanonicalizeUploadAsync(
        IFormCollection form,
        CancellationToken cancellationToken)
    {
        if (form.Keys.Any(key => key is not ("campusName" or "publisherName" or "teacherPhoneLast4")))
            throw new InvalidDataException("Only campusName, publisherName and teacherPhoneLast4 may be provided as text fields.");
        var supportedFields = new HashSet<string>(StringComparer.Ordinal) { "archive", "files" };
        if (form.Files.Any(file => !supportedFields.Contains(file.Name)))
            throw new InvalidDataException("Only the archive or files multipart field is accepted.");
        if (form.Files.Count is < 1 or > 5)
            throw new InvalidDataException("Upload one ZIP archive or a package folder containing at most five files.");

        var archives = form.Files.Where(file => file.Name == "archive").ToArray();
        var folderFiles = form.Files.Where(file => file.Name == "files").ToArray();
        if ((archives.Length > 0 && folderFiles.Length > 0) || archives.Length > 1)
            throw new InvalidDataException("Upload either one archive or the files from one package folder.");

        var stagingRoot = Path.Combine(Path.GetTempPath(), "veyon-package-upload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        try
        {
            string packageDirectory;
            if (archives.Length == 1)
            {
                if (archives[0].Length is < 1 or > CampusConfigurationArchive.MaximumArchiveBytes)
                    throw new InvalidDataException("ZIP archive must be between 1 byte and 64 KiB.");
                var archive = await ReadUploadBoundedAsync(archives[0], CampusConfigurationArchive.MaximumArchiveBytes, cancellationToken);
                var unpackedRoot = Path.Combine(stagingRoot, "validated");
                var context = CampusConfigurationArchive.ExtractToStore(archive, unpackedRoot);
                packageDirectory = context.Root;
            }
            else
            {
                var packageRoot = Path.Combine(stagingRoot, "folder");
                Directory.CreateDirectory(packageRoot);
                long totalBytes = 0;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string? selectedFolder = null;
                var hasFlatName = false;
                foreach (var file in folderFiles)
                {
                    var relativePath = file.FileName.Replace('\\', '/');
                    var parts = relativePath.Split('/');
                    if (string.IsNullOrWhiteSpace(relativePath) || relativePath.StartsWith('/') ||
                        relativePath.Contains(':') || relativePath.Any(char.IsControl) ||
                        parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or "..") || parts.Length is < 1 or > 2)
                        throw new InvalidDataException("Package folder uploads must contain files from one flat folder without links or traversal paths.");
                    if (parts.Length == 2)
                    {
                        if (hasFlatName || (selectedFolder is not null && selectedFolder != parts[0]))
                            throw new InvalidDataException("All uploaded files must come from the same package folder.");
                        selectedFolder = parts[0];
                    }
                    else
                    {
                        if (selectedFolder is not null)
                            throw new InvalidDataException("All uploaded files must come from the same package folder.");
                        hasFlatName = true;
                    }
                    var name = parts[^1];
                    if (!names.Add(name))
                        throw new InvalidDataException("Package folder uploads must contain uniquely named files.");
                    if (file.Length is < 1 or > 16 * 1024)
                        throw new InvalidDataException("Each package folder file must be between 1 byte and 16 KiB.");
                    totalBytes += file.Length;
                    if (totalBytes > CampusConfigurationArchive.MaximumArchiveBytes)
                        throw new InvalidDataException("Package folder content exceeds 64 KiB.");

                    var destination = Path.Combine(packageRoot, name);
                    await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await file.CopyToAsync(output, cancellationToken);
                }

                // Create() enforces schema v3, the fixed public-key file set, hashes,
                // campus.json consistency, no private keys, no extra paths and only the approved telemetry URL.
                var canonicalFolderArchive = CampusConfigurationArchive.Create(packageRoot);
                var unpackedRoot = Path.Combine(stagingRoot, "validated");
                packageDirectory = CampusConfigurationArchive.ExtractToStore(canonicalFolderArchive, unpackedRoot).Root;
            }

            var package = PackageManifest.Load(packageDirectory);
            if (package.SchemaVersion != 3 || package.WebsitePolicyPublicKeyPath is null ||
                !AnonymousUsageHeartbeat.IsAllowedPackageEndpoint(package.TelemetryEndpoint))
                throw new InvalidDataException("Only schemaVersion=3 packages with an empty or approved project telemetry endpoint can be published.");

            using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Combine(packageDirectory, "manifest.json"), cancellationToken));
            var packageIdText = manifest.RootElement.GetProperty("packageId").GetString();
            if (!Guid.TryParse(packageIdText, out var packageId))
                throw new InvalidDataException("manifest.json packageId must be a valid GUID.");

            var archiveBytes = CampusConfigurationArchive.Create(packageDirectory);
            return new CanonicalPackageUpload(stagingRoot, package, packageId, archiveBytes);
        }
        catch
        {
            TryDeleteDirectory(stagingRoot);
            throw;
        }
    }

    private static async Task<byte[]> ReadUploadBoundedAsync(
        IFormFile file,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = file.OpenReadStream();
        using var target = new MemoryStream((int)file.Length);
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (target.Length + read > maximumBytes)
                throw new InvalidDataException("ZIP archive exceeds 64 KiB.");
            target.Write(buffer, 0, read);
        }
        return target.ToArray();
    }

    private static async Task<IResult> FindMyPackagesAsync(
        HttpRequest request,
        CloudBaseDeploymentPackageStore store,
        CancellationToken cancellationToken)
    {
        if (!TryGetBearerToken(request, out var accessToken))
            return Results.Unauthorized();

        try
        {
            var user = await store.GetCurrentUserAsync(accessToken, cancellationToken);
            if (user is null) return Results.Unauthorized();
            var fingerprint = store.CreatePublisherFingerprintForUser(user.UserId);
            var rows = await store.FindByPublisherAsync(fingerprint, 49, 0, cancellationToken);
            return Results.Ok(new { items = rows.Select(ToPublicPackage) });
        }
        catch (CloudBaseUnavailableException)
        {
            return Results.Problem("Deployment package catalog is temporarily unavailable.", statusCode: 503);
        }
        catch (HttpRequestException)
        {
            return Results.Problem("Deployment package catalog is temporarily unavailable.", statusCode: 503);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem("Deployment package catalog is temporarily unavailable.", statusCode: 503);
        }
        catch (CloudBaseRejectedException)
        {
            return Results.Problem("Deployment package catalog is temporarily unavailable.", statusCode: 503);
        }
    }

    private static object ToPublicPackage(DeploymentPackageListItem item) => new
    {
        packageId = item.PackageId,
        campusId = item.CampusId,
        displayName = item.CampusName,
        campusName = item.CampusName,
        computerPrefix = item.ComputerPrefix,
        schemaVersion = item.SchemaVersion,
        targetOs = item.TargetOs,
        architecture = item.Architecture,
        fileName = item.FileName,
        sizeBytes = item.SizeBytes,
        sha256 = item.Sha256,
        downloadCount = item.DownloadCount,
        publishedAt = item.PublishedAt,
        requiresPhoneLast4 = item.RequiresPhoneVerification
    };

    private static bool TryGetBearerToken(HttpRequest request, out string token)
    {
        token = "";
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        token = header[7..].Trim();
        return token.Length is > 0 and <= 8192 && !token.Any(char.IsWhiteSpace);
    }

    private static string? GetDownloadClientAddress(HttpContext context)
    {
        // CloudBase's gateway may expose the original client IP separately from
        // the container connection peer. Accept one valid platform-forwarded IP;
        // reject comma-separated chains and fall back to the socket peer.
        var originalForwardedFor = context.Request.Headers["X-Original-Forwarded-For"];
        var forwardedValue = originalForwardedFor.Count == 1 ? originalForwardedFor[0]?.Trim() : null;
        if (IPAddress.TryParse(forwardedValue, out var forwardedAddress) && forwardedAddress is not null)
            return NormalizeAddress(forwardedAddress);

        return context.Connection.RemoteIpAddress is { } remoteAddress
            ? NormalizeAddress(remoteAddress)
            : null;
    }

    private static string NormalizeAddress(IPAddress address) =>
        (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    private static bool TryReadPositiveLong(string? value, out long result, bool optional)
    {
        result = 0;
        if (optional && string.IsNullOrEmpty(value)) return true;
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result > 0;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class CanonicalPackageUpload(string tempRoot, PackageContext package, Guid packageId, byte[] archiveBytes) : IDisposable
    {
        public PackageContext Package { get; } = package;
        public Guid PackageId { get; } = packageId;
        public byte[] ArchiveBytes { get; } = archiveBytes;
        public void Dispose() => TryDeleteDirectory(tempRoot);
    }

    private sealed record SetPublisherRequest(string? UserId, long CampusId, bool? IsActive);
    private sealed record WithdrawPackageRequest(string? Reason);
    private sealed record DownloadPackageRequest(string? TeacherPhoneLast4);
}

internal sealed class DeploymentPackageDownloadAttemptLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private const int MaximumFailures = 10;
    private const int MaximumEntries = 8192;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AttemptWindow> _windows = new(StringComparer.Ordinal);
    private readonly object _capacitySync = new();
    private DateTimeOffset _nextCapacityPruneUtc = DateTimeOffset.MinValue;

    public string CreateKey(string? clientAddress, Guid packageId) =>
        (string.IsNullOrWhiteSpace(clientAddress) ? "unknown" : clientAddress) + ":" + packageId.ToString("N");

    public bool IsBlocked(string key, out int retryAfterSeconds)
    {
        while (true)
        {
            retryAfterSeconds = 0;
            if (!_windows.TryGetValue(key, out var state))
            {
                lock (_capacitySync)
                {
                    if (_windows.ContainsKey(key)) continue;

                    var now = DateTimeOffset.UtcNow;
                    PruneExpiredEntriesIfNeeded(now);
                    if (_windows.Count < MaximumEntries) return false;

                    // Fail closed for new keys while the bounded table is full.
                    // The HTTP gateway must still provide the primary shared rate limit.
                    retryAfterSeconds = (int)Window.TotalSeconds;
                    return true;
                }
            }

            lock (state.Sync)
            {
                if (!_windows.TryGetValue(key, out var current) || !ReferenceEquals(state, current))
                    continue;

                var now = DateTimeOffset.UtcNow;
                if (state.BlockedUntilUtc <= now) return false;
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((state.BlockedUntilUtc - now).TotalSeconds));
                return true;
            }
        }
    }

    public bool RegisterFailure(string key, out int retryAfterSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        while (true)
        {
            retryAfterSeconds = 0;
            if (!TryGetOrAddWindow(key, now, out var state))
            {
                retryAfterSeconds = (int)Window.TotalSeconds;
                return false;
            }

            lock (state.Sync)
            {
                if (!_windows.TryGetValue(key, out var current) || !ReferenceEquals(state, current))
                    continue;

                if (state.BlockedUntilUtc > now)
                {
                    retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((state.BlockedUntilUtc - now).TotalSeconds));
                    return false;
                }
                if (now - state.WindowStartedUtc >= Window)
                {
                    state.WindowStartedUtc = now;
                    state.Failures = 0;
                }
                state.Failures++;
                state.LastActivityUtc = now;
                if (state.Failures < MaximumFailures) return true;

                state.BlockedUntilUtc = now.Add(Window);
                retryAfterSeconds = (int)Window.TotalSeconds;
                return false;
            }
        }
    }

    public void RegisterSuccess(string key)
    {
        if (!_windows.TryGetValue(key, out var state)) return;
        var now = DateTimeOffset.UtcNow;
        lock (state.Sync)
        {
            if (!_windows.TryGetValue(key, out var current) || !ReferenceEquals(state, current)) return;
            state.WindowStartedUtc = now;
            state.LastActivityUtc = now;
            state.BlockedUntilUtc = DateTimeOffset.MinValue;
            state.Failures = 0;
        }
    }

    private bool TryGetOrAddWindow(string key, DateTimeOffset now, out AttemptWindow state)
    {
        if (_windows.TryGetValue(key, out state!)) return true;

        lock (_capacitySync)
        {
            if (_windows.TryGetValue(key, out state!)) return true;

            PruneExpiredEntriesIfNeeded(now);
            if (_windows.Count >= MaximumEntries)
            {
                state = null!;
                return false;
            }

            state = new AttemptWindow(now);
            return _windows.TryAdd(key, state);
        }
    }

    private void PruneExpiredEntriesIfNeeded(DateTimeOffset now)
    {
        if (_windows.Count < MaximumEntries || now < _nextCapacityPruneUtc) return;
        _nextCapacityPruneUtc = now.AddSeconds(5);

        foreach (var entry in _windows)
        {
            lock (entry.Value.Sync)
            {
                if (now - entry.Value.LastActivityUtc >= Window && entry.Value.BlockedUntilUtc <= now &&
                    _windows.TryGetValue(entry.Key, out var current) && ReferenceEquals(entry.Value, current))
                    _windows.TryRemove(entry.Key, out _);
            }
            if (_windows.Count < MaximumEntries) break;
        }
    }

    private sealed class AttemptWindow(DateTimeOffset now)
    {
        public object Sync { get; } = new();
        public DateTimeOffset WindowStartedUtc { get; set; } = now;
        public DateTimeOffset LastActivityUtc { get; set; } = now;
        public DateTimeOffset BlockedUntilUtc { get; set; } = DateTimeOffset.MinValue;
        public int Failures { get; set; }
    }
}

internal sealed class CloudBaseDeploymentPackageStore(
    IHttpClientFactory httpClientFactory,
    string envId,
    string serviceApiKey,
    string bucketId,
    DeploymentPackageIdentityHasher identityHasher)
{
    private readonly Uri _baseUri = new($"https://{envId}.api.tcloudbasegateway.com/");

    public string CreatePhoneLast4Fingerprint(string teacherPhoneLast4) =>
        identityHasher.CreatePhoneLast4Fingerprint(teacherPhoneLast4);

    public string CreatePublisherFingerprintForUser(string userId) =>
        identityHasher.CreatePublisherFingerprintForUser(userId);

    public string CreatePublisherFingerprintForName(string publisherName) =>
        identityHasher.CreatePublisherFingerprintForName(publisherName);

    public async Task<CloudBaseUser?> GetCurrentUserAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, "auth/v1/user/me"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClientFactory.CreateClient("CloudBasePackages")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new CloudBaseUnavailableException();

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("sub", out var sub) || sub.ValueKind != JsonValueKind.String)
            return null;
        var userId = sub.GetString();
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 64 || userId.Any(char.IsControl)) return null;
        if (json.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String &&
            !string.Equals(status.GetString(), "ACTIVE", StringComparison.OrdinalIgnoreCase))
            return null;
        return new CloudBaseUser(userId);
    }

    public Task<List<DeploymentPackageListItem>> SearchAsync(
        string query, long campusId, int limit, int offset, CancellationToken cancellationToken) =>
        CallRpcAsync<List<DeploymentPackageListItem>>("search_deployment_packages", new
        {
            p_query = query,
            p_campus_id = campusId == 0 ? (long?)null : campusId,
            p_limit = limit,
            p_offset = offset
        }, cancellationToken);

    public Task<List<DeploymentPackageListItem>> FindByPublisherAsync(
        string publisherFingerprint, int limit, int offset, CancellationToken cancellationToken) =>
        CallRpcAsync<List<DeploymentPackageListItem>>("search_deployment_packages_by_publisher", new
        {
            p_publisher_fingerprint = publisherFingerprint,
            p_limit = limit,
            p_offset = offset
        }, cancellationToken);

    public async Task<DeploymentPackagePublisherContext?> ResolvePublisherAsync(
        long campusId, string userId, CancellationToken cancellationToken)
    {
        var results = await CallRpcAsync<List<DeploymentPackagePublisherContext>>(
            "resolve_deployment_package_publisher",
            new { p_campus_id = campusId, p_user_id = userId }, cancellationToken);
        return results.FirstOrDefault();
    }

    public async Task<List<DeploymentPackagePublisherCampus>> GetPublishableCampusesAsync(
        string userId, CancellationToken cancellationToken)
    {
        var encodedUserId = Uri.EscapeDataString("eq." + userId);
        var profiles = await ReadTableAsync<DeploymentPackageAdminProfile>(
            "admin_profiles", $"select=role&user_id={encodedUserId}&limit=1", cancellationToken);
        var isCampusAdministrator = profiles.Any(profile =>
            profile.Role is "owner" or "admin");

        if (isCampusAdministrator)
        {
            return await ReadTableAsync<DeploymentPackagePublisherCampus>(
                "campuses", "select=id,campus_name:name&status=eq.active&order=id.asc&limit=200", cancellationToken);
        }

        var assignments = await ReadTableAsync<DeploymentPackagePublisherAssignment>(
            "deployment_package_publishers",
            $"select=campus_id&user_id={encodedUserId}&is_active=eq.true&limit=200",
            cancellationToken);
        var campusIds = assignments.Select(item => item.CampusId).Distinct().Order().ToArray();
        if (campusIds.Length == 0) return [];

        var campusFilter = Uri.EscapeDataString("in.(" + string.Join(',', campusIds) + ")");
        return await ReadTableAsync<DeploymentPackagePublisherCampus>(
            "campuses", $"select=id,campus_name:name&status=eq.active&id={campusFilter}&order=id.asc&limit=200",
            cancellationToken);
    }

    public async Task<bool> IsPublisherManagerAsync(string userId, CancellationToken cancellationToken)
    {
        var encodedUserId = Uri.EscapeDataString("eq." + userId);
        var profiles = await ReadTableAsync<DeploymentPackageAdminProfile>(
            "admin_profiles", $"select=role&user_id={encodedUserId}&limit=1", cancellationToken);
        return profiles.Any(profile => profile.Role is "owner" or "admin");
    }

    public Task<List<DeploymentPackagePublisherGrant>> GetPublisherAssignmentsAsync(
        long campusId, CancellationToken cancellationToken) =>
        ReadTableAsync<DeploymentPackagePublisherGrant>(
            "deployment_package_publishers",
            $"select=user_id,campus_id,is_active,created_by_user_id,created_at&campus_id=eq.{campusId}&order=created_at.desc&limit=200",
            cancellationToken);

    public async Task UploadAsync(string objectKey, byte[] bytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, StorageObjectUri(objectKey));
        AddServiceAuthorization(request);
        request.Headers.TryAddWithoutValidation("x-upsert", "false");
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        request.Content.Headers.ContentLength = bytes.Length;
        using var response = await httpClientFactory.CreateClient("CloudBasePackages")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode) return;
        var failure = await ReadCloudBaseFailureAsync(response, cancellationToken);
        if (failure.StatusCode == HttpStatusCode.Conflict || failure.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            throw new CloudBaseRejectedException(failure.StatusCode, "package object already exists");
        throw new CloudBaseRejectedException(failure.StatusCode, failure.Message);
    }

    public async Task PublishPublicAsync(
        Guid packageId, string campusName, string publisherName, string prefix, int size, string sha256,
        string publisherFingerprint, string phoneFingerprint, CancellationToken cancellationToken)
    {
        await CallRpcAsync<object>("publish_deployment_package_public", new
        {
            p_package_id = packageId,
            p_campus_name = campusName,
            p_publisher_name = publisherName,
            p_computer_prefix = prefix,
            p_artifact_size_bytes = size,
            p_artifact_sha256 = sha256,
            p_publisher_identity_fingerprint = publisherFingerprint,
            p_phone_fingerprint = phoneFingerprint
        }, cancellationToken);
    }

    public async Task TryDeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, StorageObjectUri(objectKey));
            AddServiceAuthorization(request);
            using var response = await httpClientFactory.CreateClient("CloudBasePackages")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
    }

    public async Task<DeploymentPackageDownloadArtifact?> GetDownloadArtifactAsync(
        Guid packageId, string? phoneFingerprint, CancellationToken cancellationToken)
    {
        var rows = await CallRpcAsync<List<DeploymentPackageDownloadArtifact>>(
            "get_deployment_package_download_with_phone", new
            {
                p_package_id = packageId,
                p_phone_fingerprint = phoneFingerprint
            }, cancellationToken);
        return rows.FirstOrDefault();
    }

    public async Task<byte[]?> DownloadAsync(DeploymentPackageDownloadArtifact artifact, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, StorageObjectUri(artifact.StorageKey));
        AddServiceAuthorization(request);
        using var response = await httpClientFactory.CreateClient("CloudBasePackages")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new CloudBaseUnavailableException();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var target = new MemoryStream(artifact.SizeBytes);
        var buffer = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (target.Length + read > CampusConfigurationArchive.MaximumArchiveBytes)
                return null;
            target.Write(buffer, 0, read);
        }
        var bytes = target.ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        if (bytes.Length != artifact.SizeBytes ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(digest), Convert.FromHexString(artifact.Sha256)))
            return null;
        return bytes;
    }

    public async Task RecordDownloadAsync(Guid packageId, CancellationToken cancellationToken)
    {
        await CallRpcAsync<object>("record_deployment_package_download", new { p_package_id = packageId }, cancellationToken);
    }

    public async Task SetPublisherAsync(string userId, long campusId, bool isActive, string actorUserId, CancellationToken cancellationToken)
    {
        await CallRpcAsync<object>("set_deployment_package_publisher", new
        {
            p_user_id = userId,
            p_campus_id = campusId,
            p_is_active = isActive,
            p_actor_user_id = actorUserId
        }, cancellationToken);
    }

    public async Task WithdrawAsync(Guid packageId, string actorUserId, string reason, CancellationToken cancellationToken)
    {
        await CallRpcAsync<object>("withdraw_deployment_package", new
        {
            p_package_id = packageId,
            p_actor_user_id = actorUserId,
            p_reason = reason
        }, cancellationToken);
    }

    private async Task<T> CallRpcAsync<T>(string functionName, object body, CancellationToken cancellationToken)
    {
        var uri = new Uri(_baseUri, $"v1/rdb/rest/rpc/{Uri.EscapeDataString(functionName)}");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        AddServiceAuthorization(request);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(body);
        using var response = await httpClientFactory.CreateClient("CloudBasePackages")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var failure = await ReadCloudBaseFailureAsync(response, cancellationToken);
            throw new CloudBaseRejectedException(failure.StatusCode, failure.Message);
        }

        if (typeof(T) == typeof(object)) return (T)(object)new object();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken) ??
               throw new CloudBaseUnavailableException();
    }

    private async Task<List<T>> ReadTableAsync<T>(string table, string query, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(_baseUri, $"v1/rdb/rest/{Uri.EscapeDataString(table)}?{query}"));
        AddServiceAuthorization(request);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await httpClientFactory.CreateClient("CloudBasePackages")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var failure = await ReadCloudBaseFailureAsync(response, cancellationToken);
            throw new CloudBaseRejectedException(failure.StatusCode, failure.Message);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<List<T>>(stream, cancellationToken: cancellationToken) ?? [];
    }

    private async Task<CloudBaseFailure> ReadCloudBaseFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string message = "CloudBase request was rejected.";
        try
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (json.RootElement.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.String)
                message = value.GetString() ?? message;
        }
        catch (JsonException) { }
        return new CloudBaseFailure(response.StatusCode, message);
    }

    private Uri StorageObjectUri(string objectKey)
    {
        var escapedKey = string.Join('/', objectKey.Split('/').Select(Uri.EscapeDataString));
        return new Uri(_baseUri, $"v1/storages/object/{Uri.EscapeDataString(bucketId)}/{escapedKey}");
    }

    private void AddServiceAuthorization(HttpRequestMessage request) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceApiKey);
}

internal sealed record CloudBaseUser(string UserId);
internal sealed record CloudBaseFailure(HttpStatusCode StatusCode, string Message);
internal sealed class CloudBaseUnavailableException : Exception { }
internal sealed class CloudBaseRejectedException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

internal sealed record DeploymentPackagePublisherContext(
    [property: JsonPropertyName("campus_name")] string CampusName,
    [property: JsonPropertyName("authorized")] bool Authorized);

internal sealed record DeploymentPackageAdminProfile(
    [property: JsonPropertyName("role")] string Role);

internal sealed record DeploymentPackagePublisherAssignment(
    [property: JsonPropertyName("campus_id")] long CampusId);

internal sealed record DeploymentPackagePublisherGrant(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("campus_id")] long CampusId,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("created_by_user_id")] string CreatedByUserId,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

internal sealed record DeploymentPackagePublisherCampus(
    [property: JsonPropertyName("id")] long CampusId,
    [property: JsonPropertyName("campus_name")] string CampusName);

internal sealed record DeploymentPackageDownloadArtifact(
    [property: JsonPropertyName("package_id")] Guid PackageId,
    [property: JsonPropertyName("storage_key")] string StorageKey,
    [property: JsonPropertyName("artifact_file_name")] string FileName,
    [property: JsonPropertyName("artifact_size_bytes")] int SizeBytes,
    [property: JsonPropertyName("artifact_sha256")] string Sha256);

internal sealed record DeploymentPackageListItem(
    [property: JsonPropertyName("package_id")] Guid PackageId,
    [property: JsonPropertyName("campus_id")] long? CampusId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("campus_name")] string CampusName,
    [property: JsonPropertyName("computer_prefix")] string ComputerPrefix,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("target_os")] string TargetOs,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("artifact_file_name")] string FileName,
    [property: JsonPropertyName("artifact_size_bytes")] int SizeBytes,
    [property: JsonPropertyName("artifact_sha256")] string Sha256,
    [property: JsonPropertyName("download_count")] long DownloadCount,
    [property: JsonPropertyName("published_at")] DateTimeOffset PublishedAt,
    [property: JsonPropertyName("requires_phone_verification")] bool RequiresPhoneVerification);
