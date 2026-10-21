using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
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

            Console.WriteLine("PASS actual Teacher LAN HTTP full response, Range, If-Range, 416, 400 and resumed client transfer");
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
