using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using VeyonCampus.Core;

internal static class StudentApplicationUpdateArtifactTransferChecks
{
    public static void Run()
    {
        CheckRangeDecisions();
        CheckInterruptedDownloadResumesAndVerifies();
        CheckIgnoredRangeRestartsFromZero();
        CheckRetryBudgetIsReported();
        CheckMismatchedEntityTagIsRejected();
        CheckPackageDigestMismatchIsRejected();
        Console.WriteLine("PASS student update HTTP range/resume fixtures");
    }

    private static void CheckRangeDecisions()
    {
        const string entityTag = "\"sha256-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"";
        Expect(StudentApplicationUpdateArtifactTransfer.ResolveRange(null, null, entityTag, 100).Kind ==
               StudentApplicationUpdateRangeKind.Full);
        var partial = StudentApplicationUpdateArtifactTransfer.ResolveRange("bytes=42-", entityTag,
            entityTag, 100);
        Expect(partial.Kind == StudentApplicationUpdateRangeKind.Partial && partial.Offset == 42);
        Expect(StudentApplicationUpdateArtifactTransfer.ResolveRange("bytes=42-", "\"stale\"", entityTag,
            100).Kind == StudentApplicationUpdateRangeKind.Full);
        Expect(StudentApplicationUpdateArtifactTransfer.ResolveRange("bytes=100-", entityTag, entityTag, 100)
                   .Kind == StudentApplicationUpdateRangeKind.Unsatisfiable);
        Expect(StudentApplicationUpdateArtifactTransfer.ResolveRange("bytes=42-50", entityTag, entityTag, 100)
                   .Kind == StudentApplicationUpdateRangeKind.Invalid);
        Expect(StudentApplicationUpdateArtifactTransfer.ResolveRange("bytes=1-,-2", entityTag, entityTag, 100)
                   .Kind == StudentApplicationUpdateRangeKind.Invalid);
    }

    private static void CheckInterruptedDownloadResumesAndVerifies()
    {
        var payload = CreatePayload();
        var sha256 = Convert.ToHexString(SHA256.HashData(payload));
        var entityTag = StudentApplicationUpdateArtifactTransfer.CreateEntityTag(sha256);
        const int firstResponseBytes = 257;
        var handler = new ResumeHandler(payload, entityTag, firstResponseBytes,
            ResumeResponseMode.PartialContent);
        var root = CreateTempDirectory();
        try
        {
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var reportedRetryCount = -1;
            var result = StudentApplicationUpdateArtifactTransfer.DownloadAsync(client,
                new Uri("http://update-fixture.invalid/artifact"), Path.Combine(root, "payload.partial"),
                Path.Combine(root, "payload.exe"), sha256, payload.Length, payload.Length,
                CancellationToken.None, (_, _, _, retries) => reportedRetryCount = retries)
                .GetAwaiter().GetResult();
            Expect(handler.Requests.Count == 2);
            Expect(handler.Requests[0].Range is null);
            Expect(handler.Requests[1].Range == $"bytes={firstResponseBytes}-" &&
                   handler.Requests[1].IfRange == entityTag);
            Expect(result.NetworkBytes == payload.Length && result.RetryCount == 1 && reportedRetryCount == 1);
            Expect(File.ReadAllBytes(Path.Combine(root, "payload.exe")).SequenceEqual(payload));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CheckIgnoredRangeRestartsFromZero()
    {
        var payload = CreatePayload();
        var sha256 = Convert.ToHexString(SHA256.HashData(payload));
        var handler = new ResumeHandler(payload,
            StudentApplicationUpdateArtifactTransfer.CreateEntityTag(sha256), 193,
            ResumeResponseMode.FullContentWhenResumed);
        var root = CreateTempDirectory();
        try
        {
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var reportedRetryCount = -1;
            var result = StudentApplicationUpdateArtifactTransfer.DownloadAsync(client,
                new Uri("http://update-fixture.invalid/artifact"), Path.Combine(root, "payload.partial"),
                Path.Combine(root, "payload.exe"), sha256, payload.Length, payload.Length,
                CancellationToken.None, (_, _, _, retries) => reportedRetryCount = retries)
                .GetAwaiter().GetResult();
            Expect(handler.Requests.Count == 2 && handler.Requests[1].Range == "bytes=193-");
            Expect(result.NetworkBytes == 193 + payload.Length && result.RetryCount == 1 &&
                   reportedRetryCount == 1);
            Expect(File.ReadAllBytes(Path.Combine(root, "payload.exe")).SequenceEqual(payload));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CheckRetryBudgetIsReported()
    {
        var payload = CreatePayload();
        var sha256 = Convert.ToHexString(SHA256.HashData(payload));
        var handler = new TemporarilyUnavailableHandler();
        var root = CreateTempDirectory();
        var reportedRetryCount = -1;
        try
        {
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                StudentApplicationUpdateArtifactTransfer.DownloadAsync(client,
                    new Uri("http://update-fixture.invalid/artifact"), Path.Combine(root, "payload.partial"),
                    Path.Combine(root, "payload.exe"), sha256, payload.Length, payload.Length,
                    CancellationToken.None, (_, _, _, retries) => reportedRetryCount = retries)
                    .GetAwaiter().GetResult();
            }
            catch (IOException)
            {
                Expect(handler.RequestCount == StudentApplicationUpdateArtifactTransfer.MaximumRetryCount + 1 &&
                       reportedRetryCount == StudentApplicationUpdateArtifactTransfer.MaximumRetryCount &&
                       !File.Exists(Path.Combine(root, "payload.exe")));
                return;
            }
            throw new InvalidOperationException("A permanently unavailable transfer did not fail.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CheckMismatchedEntityTagIsRejected()
    {
        var payload = CreatePayload();
        var sha256 = Convert.ToHexString(SHA256.HashData(payload));
        var handler = new ResumeHandler(payload,
            StudentApplicationUpdateArtifactTransfer.CreateEntityTag(sha256), 101,
            ResumeResponseMode.WrongEntityTagWhenResumed);
        var root = CreateTempDirectory();
        try
        {
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                StudentApplicationUpdateArtifactTransfer.DownloadAsync(client,
                    new Uri("http://update-fixture.invalid/artifact"), Path.Combine(root, "payload.partial"),
                    Path.Combine(root, "payload.exe"), sha256, payload.Length, payload.Length,
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (InvalidDataException)
            {
                Expect(!File.Exists(Path.Combine(root, "payload.exe")));
                Expect(handler.Requests.Count == 2);
                return;
            }
            throw new InvalidOperationException("A resumed response with the wrong ETag was accepted.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CheckPackageDigestMismatchIsRejected()
    {
        var payload = CreatePayload();
        var wrongDigest = (byte[])payload.Clone();
        wrongDigest[wrongDigest.Length / 2] ^= 0xFF;
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(wrongDigest));
        var handler = new ResumeHandler(payload,
            StudentApplicationUpdateArtifactTransfer.CreateEntityTag(Convert.ToHexString(SHA256.HashData(payload))),
            payload.Length, ResumeResponseMode.PartialContent);
        var root = CreateTempDirectory();
        var partialPath = Path.Combine(root, "payload.partial");
        var destinationPath = Path.Combine(root, "payload.exe");
        try
        {
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                StudentApplicationUpdateArtifactTransfer.DownloadAsync(client,
                    new Uri("http://update-fixture.invalid/artifact"), partialPath, destinationPath,
                    expectedSha256, payload.Length, payload.Length, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (InvalidDataException)
            {
                Expect(!File.Exists(destinationPath) && !File.Exists(partialPath));
                Expect(handler.Requests.Count == 1);
                return;
            }
            throw new InvalidOperationException("A package with the wrong signed digest was accepted.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] CreatePayload() => Enumerable.Range(0, 4096)
        .Select(index => (byte)((index * 37 + 11) % 256)).ToArray();

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(TestPath.CanonicalTempRoot(), "veyon-update-range-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Expect(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Student update Range/resume fixture failed.");
    }

    private sealed class ResumeHandler(byte[] payload, string entityTag, int initialBytes,
        ResumeResponseMode mode) : HttpMessageHandler
    {
        public List<(string? Range, string? IfRange)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((request.Headers.Range?.ToString(), request.Headers.IfRange?.EntityTag?.ToString()));
            if (Requests.Count == 1)
                return Task.FromResult(CreateResponse(HttpStatusCode.OK,
                    new FailingReadStream(payload, initialBytes), payload.Length, entityTag));

            var start = request.Headers.Range?.Ranges.Single().From
                        ?? throw new InvalidOperationException("Expected the retry to include a Range header.");
            var offset = checked((int)start);
            if (mode == ResumeResponseMode.FullContentWhenResumed)
                return Task.FromResult(CreateResponse(HttpStatusCode.OK, new MemoryStream(payload), payload.Length,
                    "\"stale-entity\""));

            var returnedTag = mode == ResumeResponseMode.WrongEntityTagWhenResumed
                ? "\"sha256-" + new string('0', 64) + "\""
                : entityTag;
            var response = CreateResponse(HttpStatusCode.PartialContent,
                new MemoryStream(payload[offset..]), payload.Length - offset, returnedTag);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, payload.Length - 1,
                payload.Length);
            return Task.FromResult(response);
        }

        private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, Stream content,
            long contentLength, string entityTag)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StreamContent(content)
            };
            response.Content.Headers.ContentLength = contentLength;
            response.Headers.ETag = new EntityTagHeaderValue(entityTag);
            return response;
        }
    }

    private sealed class TemporarilyUnavailableHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new ByteArrayContent(Array.Empty<byte>())
            });
        }
    }

    private sealed class FailingReadStream(byte[] payload, int availableBytes) : Stream
    {
        private int _position;
        private bool _thrown;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsMemory(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadCore(buffer));
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int ReadCore(Memory<byte> buffer)
        {
            if (_position >= availableBytes)
            {
                if (!_thrown)
                {
                    _thrown = true;
                    throw new IOException("Synthetic broken connection.");
                }
                return 0;
            }
            var count = Math.Min(buffer.Length, availableBytes - _position);
            payload.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }

    private enum ResumeResponseMode
    {
        PartialContent,
        FullContentWhenResumed,
        WrongEntityTagWhenResumed
    }
}
