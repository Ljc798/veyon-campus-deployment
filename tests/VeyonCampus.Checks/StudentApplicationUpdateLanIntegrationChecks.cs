using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using VeyonCampus.Core;

internal static class StudentApplicationUpdateLanIntegrationChecks
{
    public static async Task RunAsync()
    {
        var localAddress = GetActivePrivateAddress();
        var payload = CreatePayload();
        var sha256 = Convert.ToHexString(SHA256.HashData(payload));
        var root = Path.Combine(TestPath.CanonicalTempRoot(),
            "veyon-update-lan-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var artifactPath = Path.Combine(root, "student-setup.exe");
        File.WriteAllBytes(artifactPath, payload);

        var server = StudentApplicationUpdateLanServer.Start(artifactPath, sha256, payload.Length);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var writeAccessRejected = false;
                try
                {
                    using var writeProbe = new FileStream(artifactPath, FileMode.Open, FileAccess.Write,
                        FileShare.Read);
                }
                catch (IOException)
                {
                    writeAccessRejected = true;
                }
                Expect(writeAccessRejected &&
                       Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifactPath))) == sha256);
            }
            else
            {
                Console.WriteLine("SKIP Windows-only artifact lease write-exclusion assertion on this OS.");
            }

            var uri = await server.GetDownloadUriAsync(localAddress.ToString()).ConfigureAwait(false);
            using var client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false
            }) { Timeout = TimeSpan.FromSeconds(15) };

            using (var fullResponse = await client.GetAsync(uri).ConfigureAwait(false))
            {
                Expect(fullResponse.StatusCode == HttpStatusCode.OK &&
                       fullResponse.Content.Headers.ContentLength == payload.Length &&
                       fullResponse.Headers.AcceptRanges.Contains("bytes") &&
                       fullResponse.Headers.ETag?.ToString() ==
                       StudentApplicationUpdateArtifactTransfer.CreateEntityTag(sha256));
                var fullPayload = await fullResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                Expect(fullPayload.SequenceEqual(payload));
            }

            const int concurrentClientCount = 10;
            using (var concurrentClient = new HttpClient(new HttpClientHandler
                   {
                       AllowAutoRedirect = false,
                       UseCookies = false,
                       UseProxy = false,
                       MaxConnectionsPerServer = concurrentClientCount
                   }) { Timeout = TimeSpan.FromSeconds(15) })
            {
                var startTogether = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var downloads = Enumerable.Range(0, concurrentClientCount).Select(async _ =>
                {
                    await startTogether.Task.ConfigureAwait(false);
                    using var response = await concurrentClient.GetAsync(uri).ConfigureAwait(false);
                    if (response.StatusCode != HttpStatusCode.OK ||
                        response.Content.Headers.ContentLength != payload.Length)
                        return false;
                    return (await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false))
                        .SequenceEqual(payload);
                }).ToArray();
                startTogether.SetResult();
                var concurrentResults = await Task.WhenAll(downloads).ConfigureAwait(false);
                Expect(concurrentResults.Length == concurrentClientCount && concurrentResults.All(result => result));
            }

            const int offset = 8193;
            using (var rangeRequest = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                rangeRequest.Headers.Range = new RangeHeaderValue(offset, null);
                rangeRequest.Headers.IfRange = new RangeConditionHeaderValue(
                    new EntityTagHeaderValue(StudentApplicationUpdateArtifactTransfer.CreateEntityTag(sha256)));
                using var rangeResponse = await client.SendAsync(rangeRequest).ConfigureAwait(false);
                Expect(rangeResponse.StatusCode == HttpStatusCode.PartialContent &&
                       rangeResponse.Content.Headers.ContentRange is { From: offset } contentRange &&
                       contentRange.To == payload.Length - 1 && contentRange.Length == payload.Length &&
                       (await rangeResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false))
                       .SequenceEqual(payload[offset..]));
            }

            using (var staleRangeRequest = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                staleRangeRequest.Headers.Range = new RangeHeaderValue(offset, null);
                staleRangeRequest.Headers.IfRange = new RangeConditionHeaderValue(
                    new EntityTagHeaderValue("\"stale-entity\""));
                using var staleRangeResponse = await client.SendAsync(staleRangeRequest).ConfigureAwait(false);
                Expect(staleRangeResponse.StatusCode == HttpStatusCode.OK &&
                       (await staleRangeResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false))
                       .SequenceEqual(payload));
            }

            using (var unsatisfiableRequest = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                unsatisfiableRequest.Headers.Range = new RangeHeaderValue(payload.Length, null);
                using var unsatisfiableResponse = await client.SendAsync(unsatisfiableRequest).ConfigureAwait(false);
                Expect(unsatisfiableResponse.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable &&
                       unsatisfiableResponse.Content.Headers.ContentRange?.Length == payload.Length);
            }

            using (var malformedRequest = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                malformedRequest.Headers.Range = new RangeHeaderValue(0, 1);
                using var malformedResponse = await client.SendAsync(malformedRequest).ConfigureAwait(false);
                Expect(malformedResponse.StatusCode == HttpStatusCode.BadRequest);
            }

            const int existingPrefixBytes = 1021;
            var partialPath = Path.Combine(root, "resume.partial");
            var destinationPath = Path.Combine(root, "resumed.exe");
            await File.WriteAllBytesAsync(partialPath, payload[..existingPrefixBytes]).ConfigureAwait(false);
            var transfer = await StudentApplicationUpdateArtifactTransfer.DownloadAsync(client, uri, partialPath,
                destinationPath, sha256, payload.Length, payload.Length, CancellationToken.None).ConfigureAwait(false);
            Expect(transfer.RetryCount == 0 && transfer.NetworkBytes == payload.Length - existingPrefixBytes &&
                   File.ReadAllBytes(destinationPath).SequenceEqual(payload));

            const int interruptedAfterBytes = 64 * 1024;
            await using var interruptingProxy = StudentUpdateLanInterruptingProxy.Start(localAddress, uri,
                interruptedAfterBytes);
            using var interruptedClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false
            }) { Timeout = TimeSpan.FromSeconds(15) };
            var interruptedPartialPath = Path.Combine(root, "interrupted.partial");
            var interruptedDestinationPath = Path.Combine(root, "interrupted.exe");
            var interruptedTransfer = await StudentApplicationUpdateArtifactTransfer.DownloadAsync(
                interruptedClient, interruptingProxy.DownloadUri, interruptedPartialPath,
                interruptedDestinationPath, sha256, payload.Length, payload.Length,
                CancellationToken.None).ConfigureAwait(false);
            await interruptingProxy.WaitForRetryRequestAsync().ConfigureAwait(false);
            var requests = interruptingProxy.RequestHeaders;
            Expect(requests.Count == 2 && !requests[0].Contains("\r\nRange:", StringComparison.OrdinalIgnoreCase) &&
                   requests[1].Contains($"\r\nRange: bytes={interruptedAfterBytes}-\r\n",
                       StringComparison.OrdinalIgnoreCase) &&
                   requests[1].Contains($"\r\nIf-Range: {StudentApplicationUpdateArtifactTransfer.CreateEntityTag(sha256)}\r\n",
                       StringComparison.OrdinalIgnoreCase));
            Expect(interruptedTransfer.RetryCount == 1 &&
                   interruptedTransfer.NetworkBytes == payload.Length &&
                   File.ReadAllBytes(interruptedDestinationPath).SequenceEqual(payload));

            Console.WriteLine("PASS actual Teacher LAN HTTP full response, 10 concurrent downloads, Range, If-Range, 416, 400, prefix resume, and TCP interruption/reconnect/resume");
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static IPAddress GetActivePrivateAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
        .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
        .Select(unicast => unicast.Address)
        .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork &&
                                   !IPAddress.IsLoopback(address) &&
                                   StudentApplicationUpdateCryptography.IsPrivateLanAddress(address))
        ?? throw new InvalidOperationException("No active private IPv4 interface is available for the LAN HTTP fixture.");

    private static byte[] CreatePayload() => Enumerable.Range(0, 256 * 1024)
        .Select(index => (byte)((index * 37 + 11) % 256)).ToArray();

    private static void Expect(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Teacher LAN HTTP integration fixture failed.");
    }
}

internal sealed class StudentUpdateLanInterruptingProxy : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 16 * 1024;
    private readonly TcpListener _listener;
    private readonly Uri _upstreamUri;
    private readonly int _interruptAfterBytes;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _retryRequestReceived = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _requestHeaders = [];
    private readonly Task _proxyTask;

    private StudentUpdateLanInterruptingProxy(IPAddress listenAddress, Uri upstreamUri,
        int interruptAfterBytes)
    {
        _upstreamUri = upstreamUri;
        _interruptAfterBytes = interruptAfterBytes;
        _listener = new TcpListener(listenAddress, 0);
        _listener.Start(4);
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        DownloadUri = new UriBuilder(upstreamUri)
        {
            Host = listenAddress.ToString(),
            Port = endpoint.Port
        }.Uri;
        _proxyTask = RunAsync(_shutdown.Token);
    }

    public Uri DownloadUri { get; }
    public IReadOnlyList<string> RequestHeaders => _requestHeaders.ToArray();

    public static StudentUpdateLanInterruptingProxy Start(IPAddress listenAddress, Uri upstreamUri,
        int interruptAfterBytes) => new(listenAddress, upstreamUri, interruptAfterBytes);

    public Task WaitForRetryRequestAsync() => _retryRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        try { await _proxyTask.ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or
                                          IOException or ObjectDisposedException) { }
        _shutdown.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var requestNumber = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            using var upstream = new TcpClient(AddressFamily.InterNetwork);
            await upstream.ConnectAsync(_upstreamUri.Host, _upstreamUri.Port, cancellationToken)
                .ConfigureAwait(false);
            await using var clientStream = client.GetStream();
            await using var upstreamStream = upstream.GetStream();

            var requestHeaders = await ReadHeadersAsync(clientStream, cancellationToken).ConfigureAwait(false);
            _requestHeaders.Add(Encoding.ASCII.GetString(requestHeaders));
            requestNumber++;
            await upstreamStream.WriteAsync(requestHeaders, cancellationToken).ConfigureAwait(false);

            var responseHeaders = await ReadHeadersAsync(upstreamStream, cancellationToken).ConfigureAwait(false);
            var responseHeaderText = Encoding.ASCII.GetString(responseHeaders);
            var contentLength = ReadContentLength(responseHeaderText);
            await clientStream.WriteAsync(responseHeaders, cancellationToken).ConfigureAwait(false);

            if (requestNumber == 1)
            {
                if (contentLength <= _interruptAfterBytes)
                    throw new InvalidDataException("LAN interruption fixture cutoff must be within the response body.");
                await CopyExactlyAsync(upstreamStream, clientStream, _interruptAfterBytes, cancellationToken)
                    .ConfigureAwait(false);
                // Closing both TCP connections mid-body forces the production downloader to resume from
                // the exact bytes it flushed to its protected partial file.
                continue;
            }

            _retryRequestReceived.TrySetResult();
            await CopyExactlyAsync(upstreamStream, clientStream, contentLength, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(512);
        var next = new byte[1];
        while (bytes.Count < MaximumHeaderBytes)
        {
            var read = await stream.ReadAsync(next, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("LAN interruption proxy received incomplete headers.");
            bytes.Add(next[0]);
            var count = bytes.Count;
            if (count >= 4 && bytes[count - 4] == '\r' && bytes[count - 3] == '\n' &&
                bytes[count - 2] == '\r' && bytes[count - 1] == '\n')
                return bytes.ToArray();
        }
        throw new InvalidDataException("LAN interruption proxy header exceeded its safety limit.");
    }

    private static long ReadContentLength(string headers)
    {
        foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0 || !line[..separator].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            if (long.TryParse(line[(separator + 1)..].Trim(), out var length) && length >= 0) return length;
        }
        throw new InvalidDataException("LAN interruption proxy response has no valid Content-Length.");
    }

    private static async Task CopyExactlyAsync(Stream source, Stream destination, long byteCount,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        while (byteCount > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, byteCount)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("LAN interruption proxy upstream closed early.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            byteCount -= read;
        }
    }
}
