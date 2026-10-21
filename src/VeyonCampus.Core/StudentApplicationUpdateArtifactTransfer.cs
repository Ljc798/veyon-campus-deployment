using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace VeyonCampus.Core;

internal sealed record StudentApplicationArtifactTransferResult(long NetworkBytes,
    long IntegrityHashMilliseconds, int RetryCount);

internal enum StudentApplicationUpdateRangeKind
{
    Full,
    Partial,
    Unsatisfiable,
    Invalid
}

internal readonly record struct StudentApplicationUpdateRangeDecision(
    StudentApplicationUpdateRangeKind Kind, long Offset);

internal static class StudentApplicationUpdateArtifactTransfer
{
    internal const int MaximumRetryCount = 2;
    private const int MaximumAttempts = MaximumRetryCount + 1;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    internal static string CreateEntityTag(string sha256)
    {
        ValidateSha256(sha256);
        return "\"sha256-" + sha256.ToLowerInvariant() + "\"";
    }

    internal static StudentApplicationUpdateRangeDecision ResolveRange(string? rangeHeader,
        string? ifRangeHeader, string entityTag, long artifactSize)
    {
        if (artifactSize < 1 || string.IsNullOrWhiteSpace(entityTag))
            return new(StudentApplicationUpdateRangeKind.Invalid, 0);
        if (rangeHeader is null) return new(StudentApplicationUpdateRangeKind.Full, 0);

        var value = rangeHeader.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return new(StudentApplicationUpdateRangeKind.Invalid, 0);
        var range = value[6..];
        var separator = range.IndexOf('-');
        if (separator <= 0 || separator != range.LastIndexOf('-') || separator != range.Length - 1 ||
            !long.TryParse(range.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture,
                out var offset) || offset < 0)
            return new(StudentApplicationUpdateRangeKind.Invalid, 0);
        if (offset >= artifactSize)
            return new(StudentApplicationUpdateRangeKind.Unsatisfiable, offset);
        if (ifRangeHeader is not null && !string.Equals(ifRangeHeader.Trim(), entityTag,
                StringComparison.Ordinal))
            return new(StudentApplicationUpdateRangeKind.Full, 0);
        return new(StudentApplicationUpdateRangeKind.Partial, offset);
    }

    internal static async Task<StudentApplicationArtifactTransferResult> DownloadAsync(HttpClient client, Uri uri,
        string partialPath, string destinationPath, string expectedSha256, long expectedSize, long maximumSize,
        CancellationToken cancellationToken, Action<long, long, bool?, int>? reportMeasurements = null,
        Action<string>? secureVerifiedArtifact = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(partialPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ValidateSha256(expectedSha256);
        if (expectedSize < 1 || expectedSize > maximumSize)
            throw new InvalidDataException("学生安装器大小超出允许范围。");

        partialPath = Path.GetFullPath(partialPath);
        destinationPath = Path.GetFullPath(destinationPath);
        if (string.Equals(partialPath, destinationPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("学生更新暂存路径与最终安装包路径冲突。");
        RejectLinksForPotentialFile(partialPath);
        RejectLinksForPotentialFile(destinationPath);
        if (File.Exists(destinationPath))
            throw new IOException("学生更新目标安装包已存在；不会覆盖已有文件。");

        var expectedDigest = Convert.FromHexString(expectedSha256);
        var entityTag = CreateEntityTag(expectedSha256);
        long networkBytes = 0;
        long integrityHashTicks = 0;
        var requestCount = 0;
        var retryCount = 0;
        bool hashVerificationCompleted = false;
        bool hashVerificationSucceeded = false;
        try
        {
            Exception? lastTransientFailure = null;
            for (var attempt = 0; attempt < MaximumAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var offset = ReadPartialLength(partialPath, expectedSize);
                    if (offset == expectedSize)
                    {
                        using var completedDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        await AppendFileToHashAsync(partialPath, offset, completedDigest,
                            value => integrityHashTicks += value, cancellationToken).ConfigureAwait(false);
                        var completedHash = completedDigest.GetHashAndReset();
                        hashVerificationCompleted = true;
                        hashVerificationSucceeded = CryptographicOperations.FixedTimeEquals(
                            completedHash, expectedDigest);
                        if (!hashVerificationSucceeded)
                        {
                            TryDelete(partialPath);
                            throw new InvalidDataException("续传暂存包 SHA-256 与开发者签名清单不符。");
                        }
                        secureVerifiedArtifact?.Invoke(partialPath);
                        File.Move(partialPath, destinationPath, overwrite: false);
                        return new StudentApplicationArtifactTransferResult(networkBytes,
                            ToMilliseconds(integrityHashTicks), retryCount);
                    }

                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                    if (offset > 0)
                    {
                        request.Headers.Range = new RangeHeaderValue(offset, null);
                        request.Headers.IfRange = new RangeConditionHeaderValue(
                            new EntityTagHeaderValue(entityTag));
                    }

                    if (requestCount > 0) retryCount++;
                    requestCount++;
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                    if (IsTransientStatusCode(response.StatusCode))
                        throw new HttpRequestException($"学生更新局域网下载返回可重试的 HTTP {(int)response.StatusCode}。",
                            null, response.StatusCode);

                    var append = response.StatusCode == HttpStatusCode.PartialContent;
                    if (append)
                    {
                        var contentRange = response.Content.Headers.ContentRange;
                        if (offset <= 0 || contentRange is null ||
                            !string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
                            contentRange.From != offset || contentRange.To != expectedSize - 1 ||
                            contentRange.Length != expectedSize ||
                            !string.Equals(response.Headers.ETag?.ToString(), entityTag, StringComparison.Ordinal))
                            throw new InvalidDataException("学生更新续传响应没有匹配本次签名文件的摘要、范围或总大小。");
                        ValidateContentLength(response, expectedSize - offset);
                    }
                    else if (response.StatusCode == HttpStatusCode.OK)
                    {
                        // Older Teacher versions may ignore Range. Restart from byte zero; never append
                        // a full response to a prefix from a previous response.
                        offset = 0;
                        ValidateContentLength(response, expectedSize);
                    }
                    else
                    {
                        throw new HttpRequestException($"学生更新局域网下载返回 HTTP {(int)response.StatusCode}。",
                            null, response.StatusCode);
                    }

                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    if (append && offset > 0)
                        await AppendFileToHashAsync(partialPath, offset, digest,
                            value => integrityHashTicks += value, cancellationToken).ConfigureAwait(false);

                    var mode = append ? FileMode.OpenOrCreate : FileMode.Create;
                    await using var target = new FileStream(partialPath, mode, FileAccess.Write, FileShare.None,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                    if (append)
                    {
                        if (target.Length != offset)
                            throw new InvalidDataException("学生更新续传文件在校验后发生变化。");
                        target.Position = offset;
                    }
                    else
                    {
                        target.SetLength(0);
                    }

                    long writtenBytes = offset;
                    await using var source = await ReadResponseStreamAsync(response, cancellationToken)
                        .ConfigureAwait(false);
                    var buffer = new byte[64 * 1024];
                    while (true)
                    {
                        int bytesRead;
                        try
                        {
                            bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        }
                        catch (IOException exception)
                        {
                            await target.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                            throw new StudentArtifactTransferInterruptedException(
                                "学生更新下载连接中断；将按已落盘偏移继续。", exception);
                        }
                        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                        {
                            await target.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                            throw new StudentArtifactTransferInterruptedException(
                                "学生更新下载等待超时；将按已落盘偏移继续。", exception);
                        }
                        if (bytesRead == 0) break;
                        if (writtenBytes > expectedSize - bytesRead)
                            throw new InvalidDataException("学生安装器超过开发者签名清单声明的大小。");
                        networkBytes += bytesRead;
                        var hashStarted = Stopwatch.GetTimestamp();
                        digest.AppendData(buffer, 0, bytesRead);
                        integrityHashTicks += Math.Max(0, Stopwatch.GetTimestamp() - hashStarted);
                        await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                            .ConfigureAwait(false);
                        writtenBytes += bytesRead;
                    }
                    await target.FlushAsync(cancellationToken).ConfigureAwait(false);

                    if (writtenBytes < expectedSize)
                    {
                        lastTransientFailure = new StudentArtifactTransferInterruptedException(
                            "学生安装器下载提前结束；将按已接收偏移继续。");
                        if (attempt + 1 < MaximumAttempts)
                        {
                            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        break;
                    }

                    var actualDigest = digest.GetHashAndReset();
                    hashVerificationCompleted = true;
                    hashVerificationSucceeded = CryptographicOperations.FixedTimeEquals(actualDigest,
                        expectedDigest);
                    if (!hashVerificationSucceeded)
                    {
                        TryDelete(partialPath);
                        throw new InvalidDataException("学生安装器 SHA-256 与开发者签名清单不符。");
                    }

                    secureVerifiedArtifact?.Invoke(partialPath);
                    File.Move(partialPath, destinationPath, overwrite: false);
                    return new StudentApplicationArtifactTransferResult(networkBytes,
                        ToMilliseconds(integrityHashTicks), retryCount);
                }
                catch (Exception exception) when (IsTransient(exception, cancellationToken))
                {
                    lastTransientFailure = exception;
                    if (attempt + 1 >= MaximumAttempts) break;
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new IOException("学生安装器局域网下载在三次有界尝试后仍未完成。",
                lastTransientFailure);
        }
        finally
        {
            try
            {
                reportMeasurements?.Invoke(networkBytes, ToMilliseconds(integrityHashTicks),
                    hashVerificationCompleted ? hashVerificationSucceeded : null, retryCount);
            }
            catch (Exception) { /* Measurement reporting must not change the update result. */ }
        }
    }

    private static async Task<Stream> ReadResponseStreamAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try { return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false); }
        catch (IOException exception)
        {
            throw new StudentArtifactTransferInterruptedException(
                "学生更新下载连接中断；将重新请求文件。", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StudentArtifactTransferInterruptedException(
                "学生更新下载等待超时；将重新请求文件。", exception);
        }
    }

    private static async Task AppendFileToHashAsync(string path, long expectedLength,
        IncrementalHash digest, Action<long> addHashTicks, CancellationToken cancellationToken)
    {
        PathLinkSecurity.RejectLinks(path);
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != expectedLength)
            throw new InvalidDataException("学生更新续传文件大小在校验期间发生变化。");
        var buffer = new byte[64 * 1024];
        long hashedBytes = 0;
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            var started = Stopwatch.GetTimestamp();
            digest.AppendData(buffer, 0, bytesRead);
            addHashTicks(Math.Max(0, Stopwatch.GetTimestamp() - started));
            hashedBytes += bytesRead;
        }
        if (hashedBytes != expectedLength || source.Length != expectedLength)
            throw new InvalidDataException("学生更新续传文件内容在校验期间发生变化。");
    }

    private static long ReadPartialLength(string partialPath, long expectedSize)
    {
        RejectLinksForPotentialFile(partialPath);
        if (!File.Exists(partialPath)) return 0;
        var length = new FileInfo(partialPath).Length;
        if (length <= expectedSize) return length;
        TryDelete(partialPath);
        return 0;
    }

    private static void RejectLinksForPotentialFile(string path)
    {
        try { PathLinkSecurity.RejectLinks(path); }
        catch (FileNotFoundException)
        {
            var parent = Path.GetDirectoryName(path)
                         ?? throw new InvalidDataException("学生更新暂存目录无效。");
            PathLinkSecurity.RejectLinks(parent);
        }
    }

    private static void ValidateContentLength(HttpResponseMessage response, long expectedLength)
    {
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength != expectedLength)
            throw new InvalidDataException("学生安装器响应长度与开发者签名清单不符。");
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        if (exception is StudentArtifactTransferInterruptedException or OperationCanceledException) return true;
        if (exception is not HttpRequestException requestException) return false;
        return requestException.StatusCode is null || IsTransientStatusCode(requestException.StatusCode.Value);
    }

    private static long ToMilliseconds(long stopwatchTicks) =>
        (long)Math.Max(0, stopwatchTicks * 1000d / Stopwatch.Frequency);

    private static void ValidateSha256(string sha256)
    {
        if (sha256 is null || sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("学生更新文件 SHA-256 清单格式无效。");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private sealed class StudentArtifactTransferInterruptedException(string message, Exception? innerException = null)
        : IOException(message, innerException);
}
