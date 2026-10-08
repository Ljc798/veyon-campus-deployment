using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using VeyonCampus.App;
using VeyonCampus.Core;

internal static class MobileControlApiChecks
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync()
    {
        var directory = Path.Combine(TestPath.CanonicalTempRoot(),
            "veyon-mobile-api-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var httpsPort = GetFreePort();
        var bootstrapPort = GetFreePort();
        while (bootstrapPort == httpsPort) bootstrapPort = GetFreePort();
        var identity = CreateIdentity(IPAddress.Loopback);
        var changes = 0;
        await using var service = new TeacherMobileControlService(identity, () => Interlocked.Increment(ref changes),
            () => "demo", directory, httpsPort, bootstrapPort);
        try
        {
            await service.StartAsync(CancellationToken.None);
            using var handler = new HttpClientHandler { UseProxy = false };
            var expectedThumbprint = identity.Server.Thumbprint;
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                string.Equals(certificate?.GetCertHashString(), expectedThumbprint, StringComparison.OrdinalIgnoreCase);
            using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{httpsPort}/") };
            var origin = $"https://127.0.0.1:{httpsPort}";

            var websiteProfile = new MobilePolicyProfile(Guid.NewGuid(), "当前校区", "demo",
                MobilePolicyProfileKind.Website, 60, WebsitePolicyMode.Blocklist, ["example.com"]);
            var otherCampusProfile = websiteProfile with { Id = Guid.NewGuid(), Name = "其他校区", CampusId = "other" };
            var systemProfile = new MobilePolicyProfile(Guid.NewGuid(), "长期基线", "demo",
                MobilePolicyProfileKind.System, 0, StudentSids: ["S-1-5-21-123-456-789-1001"],
                SystemSettings: new StudentSystemPolicySettings(true, true, false, false, true, false));
            MobilePolicyProfileStore.Save(websiteProfile, directory);
            MobilePolicyProfileStore.Save(systemProfile, directory);
            MobilePolicyProfileStore.Save(otherCampusProfile, directory);

            var invitation = service.CreatePairingInvitation();
            using (var duplicateJson = await PostJsonAsync(client, "/api/pair",
                       $"{{\"pairingCode\":\"{invitation.Code}\",\"pairingCode\":\"{invitation.Code}\",\"deviceName\":\"测试手机\"}}",
                       origin))
                Expect(duplicateJson.StatusCode == HttpStatusCode.BadRequest);
            var wrongCode = invitation.Code == "00000000" ? "00000001" : "00000000";
            using (var wrong = await PostJsonAsync(client, "/api/pair",
                       JsonSerializer.Serialize(new { pairingCode = wrongCode, deviceName = "测试手机" }, JsonOptions), origin))
                Expect(wrong.StatusCode == HttpStatusCode.Unauthorized);

            using var pairResponse = await PostJsonAsync(client, "/api/pair",
                JsonSerializer.Serialize(new { pairingCode = invitation.Code, deviceName = "测试手机" }, JsonOptions), origin);
            Expect(pairResponse.StatusCode == HttpStatusCode.OK);
            var pairRequest = await ReadJsonAsync<MobilePairPendingResponse>(pairResponse);
            Expect(!string.IsNullOrWhiteSpace(pairRequest.Ticket) &&
                   !MobilePairedDeviceStore.IsAuthorized(pairRequest.Ticket, directory));
            var pending = service.ListPendingPairings();
            Expect(pending.Count == 1 && pending[0].DeviceName == "测试手机" && pending[0].SourceAddress == "127.0.0.1");

            using (var wrongOrigin = await PostJsonAsync(client, "/api/pair-status",
                       JsonSerializer.Serialize(new MobilePairPollRequest(pairRequest.Ticket), JsonOptions),
                       "https://untrusted.example"))
                Expect(wrongOrigin.StatusCode == HttpStatusCode.Forbidden);
            using (var waiting = await PostJsonAsync(client, "/api/pair-status",
                       JsonSerializer.Serialize(new MobilePairPollRequest(pairRequest.Ticket), JsonOptions), origin))
                Expect((await ReadJsonAsync<MobilePairPollResponse>(waiting)).State == "waiting-approval");

            Expect(service.ApprovePairing(pending[0].Id));
            Expect(service.ListPendingPairings().Count == 0);
            using var approvedResponse = await PostJsonAsync(client, "/api/pair-status",
                JsonSerializer.Serialize(new MobilePairPollRequest(pairRequest.Ticket), JsonOptions), origin);
            var approved = await ReadJsonAsync<MobilePairPollResponse>(approvedResponse);
            Expect(approvedResponse.StatusCode == HttpStatusCode.OK && approved.State == "approved" &&
                   approved.Device?.DisplayName == "测试手机" && !string.IsNullOrWhiteSpace(approved.AccessToken));
            var accessToken = approved.AccessToken!;
            var deviceId = approved.Device!.Id;
            Expect(MobilePairedDeviceStore.IsAuthorized(accessToken, directory));

            using var profilesRequest = AuthorizedGet("/api/profiles", accessToken);
            using var profilesResponse = await client.SendAsync(profilesRequest);
            using var profiles = await JsonDocument.ParseAsync(await profilesResponse.Content.ReadAsStreamAsync());
            var systemSummary = profiles.RootElement.EnumerateArray().Single(element =>
                element.GetProperty("kind").GetString() == "system");
            Expect(profilesResponse.StatusCode == HttpStatusCode.OK && profiles.RootElement.GetArrayLength() == 2 &&
                   profiles.RootElement.EnumerateArray().All(element => element.GetProperty("campusId").GetString() == "demo") &&
                   systemSummary.GetProperty("mode").GetString() == "长期基线" &&
                   systemSummary.GetProperty("lifetimeMinutes").GetInt32() == 0 &&
                   systemSummary.GetProperty("systemSettings").GetProperty("lockWallpaper").GetBoolean() &&
                   !systemSummary.TryGetProperty("studentSids", out _));

            var expectedProtectedStatus = OperatingSystem.IsWindows()
                ? HttpStatusCode.Unauthorized
                : HttpStatusCode.NotImplemented;
            using (var unauthenticatedStatus = await PostJsonAsync(client, "/api/status", "{}", origin))
                Expect(unauthenticatedStatus.StatusCode == expectedProtectedStatus);
            using (var unauthenticatedPolicy = await PostJsonAsync(client, "/api/policy", "{}", origin))
                Expect(unauthenticatedPolicy.StatusCode == expectedProtectedStatus);

            var replayableRequest = AuthorizedGet("/api/session", accessToken);
            var nonce = replayableRequest.Headers.GetValues("X-Veyon-Request-Nonce").Single();
            var timestamp = replayableRequest.Headers.GetValues("X-Veyon-Request-Timestamp").Single();
            using (replayableRequest)
            using (var session = await client.SendAsync(replayableRequest))
                Expect(session.StatusCode == HttpStatusCode.OK);
            using (var replay = AuthorizedGet("/api/session", accessToken, nonce, timestamp))
            using (var replayResponse = await client.SendAsync(replay))
                Expect(replayResponse.StatusCode == HttpStatusCode.Unauthorized);

            MobilePairedDeviceStore.Revoke(deviceId, directory);
            using (var revoked = AuthorizedGet("/api/session", accessToken))
            using (var revokedResponse = await client.SendAsync(revoked))
                Expect(revokedResponse.StatusCode == HttpStatusCode.Unauthorized);
            Expect(changes >= 2);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private static MobileControlTlsIdentity CreateIdentity(IPAddress address)
    {
        using var rootKey = RSA.Create(2048);
        var now = DateTimeOffset.UtcNow;
        var rootRequest = new CertificateRequest("CN=Veyon Campus Mobile API Test CA", rootKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var root = rootRequest.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(2));
        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=127.0.0.1", serverKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var subjectNames = new SubjectAlternativeNameBuilder();
        subjectNames.AddIpAddress(address);
        serverRequest.CertificateExtensions.Add(subjectNames.Build());
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7f;
        using var publicServer = serverRequest.Create(root, now.AddMinutes(-5), now.AddDays(1), serial);
        using var ephemeralServer = publicServer.CopyWithPrivateKey(serverKey);
        // Schannel needs a key container for TLS. Import the synthetic fixture
        // with default lifetime-managed storage; do not install it in a trust store.
        var pfxBytes = ephemeralServer.Export(X509ContentType.Pkcs12);
        X509Certificate2 server;
        try { server = X509CertificateLoader.LoadPkcs12(pfxBytes, null); }
        finally { CryptographicOperations.ZeroMemory(pfxBytes); }
        var rootBytes = root.Export(X509ContentType.Cert);
        var fingerprint = Convert.ToHexString(SHA256.HashData(rootBytes));
        return new MobileControlTlsIdentity(root, server, rootBytes, fingerprint, [address]);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static HttpRequestMessage AuthorizedGet(string path, string token, string? nonce = null,
        string? timestamp = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-Veyon-Request-Nonce", nonce ?? Guid.NewGuid().ToString("N"));
        request.Headers.TryAddWithoutValidation("X-Veyon-Request-Timestamp",
            timestamp ?? DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return request;
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, string json,
        string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Origin", origin);
        return await client.SendAsync(request);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response) =>
        await JsonSerializer.DeserializeAsync<T>(await response.Content.ReadAsStreamAsync(), JsonOptions)
        ?? throw new Exception("Mobile API returned empty JSON.");

    private static void Expect(bool condition)
    {
        if (!condition) throw new Exception("Mobile control API contract check failed.");
    }
}
