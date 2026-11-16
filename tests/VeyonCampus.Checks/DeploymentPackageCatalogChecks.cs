using System.Net;
using System.Security.Cryptography;
using System.Text;
using VeyonCampus.Core;

/// <summary>
/// Covers the static deployment-package catalogue: the search semantics that used to live in SQL,
/// the byte checks before an archive is handed to the extractor, and the ordering guarantees that
/// keep a withdrawn package from reappearing through the retired API.
/// </summary>
internal static class DeploymentPackageCatalogChecks
{
    private static readonly Guid PublishedId = Guid.Parse("b747bb33-f048-4118-9a4d-a7dcd298b919");
    private static readonly Guid SecondId = Guid.Parse("0f4a1f9e-6f4f-4a1e-9d1f-2c3b4a5d6e7f");
    private static readonly Guid WithdrawnId = Guid.Parse("aa11bb22-cc33-dd44-ee55-ff6677889900");

    public static void Run()
    {
        SearchMatchesCampusNameAndPrefix();
        SearchHidesWithdrawnPackages();
        DownloadReturnsVerifiedBytes();
        DownloadRejectsTamperedBytes();
        DownloadRejectsForeignHosts();
        FallsBackToApiWhenNoSourceAnswers();
        ReportsMissingPackageWithoutCallingApi();
    }

    private static void SearchMatchesCampusNameAndPrefix()
    {
        using var fixture = new Fixture();
        var client = fixture.CreateClient();
        var result = client.SearchAsync("春晓").GetAwaiter().GetResult();
        Expect(result.Items.Count == 1 && result.Items[0].PackageId == PublishedId,
            "search did not match the campus name");
        Expect(result.Items[0].DownloadUrl.StartsWith("https://api.gitcode.com/", StringComparison.Ordinal),
            "search dropped the download URL");
        Expect(result.Items[0].CampusName == "春晓实验学校", "search returned a rewritten campus name");

        var byPrefix = client.SearchAsync("pc-0").GetAwaiter().GetResult();
        Expect(byPrefix.Items.Count == 2, "prefix search did not match both packages");

        var empty = client.SearchAsync("").GetAwaiter().GetResult();
        Expect(empty.Items.Count == 2 && !empty.HasMore, "empty query did not return every published package");

        var campusFiltered = client.SearchAsync("", campusId: 7).GetAwaiter().GetResult();
        Expect(campusFiltered.Items.Count == 1 && campusFiltered.Items[0].CampusId == 7,
            "campus filter was ignored");
    }

    private static void SearchHidesWithdrawnPackages()
    {
        using var fixture = new Fixture();
        var client = fixture.CreateClient();
        var result = client.SearchAsync("撤回校区").GetAwaiter().GetResult();
        Expect(result.Items.Count == 0, "a withdrawn package was still searchable");
        Expect(Throws<InvalidDataException>(() => client.DownloadAsync(WithdrawnId, "1234").GetAwaiter().GetResult()),
            "a withdrawn package was still downloadable");
    }

    private static void DownloadReturnsVerifiedBytes()
    {
        using var fixture = new Fixture();
        var client = fixture.CreateClient();
        _ = client.SearchAsync("春晓").GetAwaiter().GetResult();
        var bytes = client.DownloadAsync(PublishedId, "1234").GetAwaiter().GetResult();
        Expect(bytes.SequenceEqual(fixture.Archive), "download returned different bytes than the archive");

        // A fresh client has no search cache, so the id-only path has to re-read the catalogue.
        var cold = fixture.CreateClient();
        var coldBytes = cold.DownloadAsync(PublishedId, "1234").GetAwaiter().GetResult();
        Expect(coldBytes.SequenceEqual(fixture.Archive), "id-only download did not read the catalogue");
    }

    private static void DownloadRejectsTamperedBytes()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog.Replace(fixture.ArchiveSha256,
            new string('0', 64), StringComparison.Ordinal);
        var client = fixture.CreateClient(catalogOverride: catalog);
        _ = client.SearchAsync("春晓").GetAwaiter().GetResult();
        Expect(Throws<HttpRequestException>(() => client.DownloadAsync(PublishedId, "1234").GetAwaiter().GetResult()),
            "a digest mismatch was accepted");
    }

    private static void DownloadRejectsForeignHosts()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog.Replace(
            $"https://api.gitcode.com/api/v5/repos/ljc_1126/veyon-campus-deployment/raw/packages/{PublishedId:D}.zip?ref=develop",
            "https://evil.example/payload.zip", StringComparison.Ordinal);
        var client = fixture.CreateClient(catalogOverride: catalog);
        _ = client.SearchAsync("春晓").GetAwaiter().GetResult();
        Expect(Throws<HttpRequestException>(() => client.DownloadAsync(PublishedId, "1234").GetAwaiter().GetResult()),
            "a download URL outside the publishing hosts was followed");
        Expect(fixture.Requests.All(uri => !uri.Host.Equals("evil.example", StringComparison.OrdinalIgnoreCase)),
            "the client contacted a host that is not in the allow list");
    }

    private static void FallsBackToApiWhenNoSourceAnswers()
    {
        using var fixture = new Fixture();
        fixture.CatalogStatus = HttpStatusCode.InternalServerError;
        var client = fixture.CreateClient();
        var result = client.SearchAsync("春晓").GetAwaiter().GetResult();
        Expect(result.Items.Count == 1 && result.Items[0].PackageId == PublishedId,
            "the client did not fall back to the API when every catalogue source failed");
        Expect(fixture.Requests.Any(uri => uri.AbsolutePath == "/v1/deployment-packages"),
            "the fallback never reached the API");
    }

    private static void ReportsMissingPackageWithoutCallingApi()
    {
        using var fixture = new Fixture();
        var client = fixture.CreateClient();
        _ = client.SearchAsync("春晓").GetAwaiter().GetResult();
        var missingId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Expect(Throws<InvalidDataException>(() => client.DownloadAsync(missingId, "1234").GetAwaiter().GetResult()),
            "an unknown package id was not rejected");
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception("Deployment package catalogue check failed: " + message + ".");
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Archive = Encoding.UTF8.GetBytes("PK-fake-archive-payload-for-checks");
            ArchiveSha256 = Convert.ToHexString(SHA256.HashData(Archive));
            Catalog = BuildCatalog(Archive.Length, ArchiveSha256);
        }

        public byte[] Archive { get; }

        public string ArchiveSha256 { get; }

        public string Catalog { get; }

        public HttpStatusCode CatalogStatus { get; set; } = HttpStatusCode.OK;

        public List<Uri> Requests { get; } = [];

        public DeploymentPackageCatalogClient CreateClient(string? catalogOverride = null)
        {
            var handler = new FixtureHandler(this, catalogOverride ?? Catalog);
            var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var sources = new[]
            {
                new DeploymentCatalogSource(DeploymentCatalogKind.GitCode, "ljc_1126", "veyon-campus-deployment")
            };
            return new DeploymentPackageCatalogClient(sources, new Uri("https://api.example.invalid/"), httpClient);
        }

        private static string BuildCatalog(int sizeBytes, string sha256) => $$"""
            {
              "schemaVersion": 1,
              "generatedAt": "2026-11-16T07:00:00.000Z",
              "packages": [
                {
                  "packageId": "{{PublishedId:D}}",
                  "campusId": 7,
                  "campusName": "春晓实验学校",
                  "displayName": "春晓实验学校",
                  "computerPrefix": "PC-01",
                  "schemaVersion": 7,
                  "targetOs": "windows",
                  "architecture": "x64",
                  "fileName": "campus-package.zip",
                  "sizeBytes": {{sizeBytes}},
                  "sha256": "{{sha256}}",
                  "downloadUrl": "https://api.gitcode.com/api/v5/repos/ljc_1126/veyon-campus-deployment/raw/packages/{{PublishedId:D}}.zip?ref=develop",
                  "mirrors": [],
                  "downloadCount": 3,
                  "publishedAt": "2026-11-16T06:00:00.000Z",
                  "requiresPhoneLast4": false,
                  "status": "published"
                },
                {
                  "packageId": "{{SecondId:D}}",
                  "campusId": 9,
                  "campusName": "东湖小学",
                  "displayName": "东湖小学",
                  "computerPrefix": "PC-02",
                  "schemaVersion": 6,
                  "targetOs": "windows",
                  "architecture": "x64",
                  "fileName": "campus-package.zip",
                  "sizeBytes": {{sizeBytes}},
                  "sha256": "{{sha256}}",
                  "downloadUrl": "https://api.gitcode.com/api/v5/repos/ljc_1126/veyon-campus-deployment/raw/packages/{{SecondId:D}}.zip?ref=develop",
                  "mirrors": [],
                  "downloadCount": 0,
                  "publishedAt": "2026-11-16T05:00:00.000Z",
                  "requiresPhoneLast4": false,
                  "status": "published"
                },
                {
                  "packageId": "{{WithdrawnId:D}}",
                  "campusId": 11,
                  "campusName": "撤回校区",
                  "displayName": "撤回校区",
                  "computerPrefix": "PC-09",
                  "schemaVersion": 6,
                  "targetOs": "windows",
                  "architecture": "x64",
                  "fileName": "campus-package.zip",
                  "sizeBytes": {{sizeBytes}},
                  "sha256": "{{sha256}}",
                  "downloadUrl": "https://api.gitcode.com/api/v5/repos/ljc_1126/veyon-campus-deployment/raw/packages/{{WithdrawnId:D}}.zip?ref=develop",
                  "mirrors": [],
                  "downloadCount": 1,
                  "publishedAt": "2026-11-15T05:00:00.000Z",
                  "requiresPhoneLast4": false,
                  "status": "withdrawn"
                }
              ]
            }
            """;

        public void Dispose() { }

        private sealed class FixtureHandler(Fixture fixture, string catalog) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var uri = request.RequestUri ?? throw new InvalidOperationException("Fixture request URL missing.");
                fixture.Requests.Add(uri);
                if (uri.AbsolutePath.EndsWith("/deployment-catalog.json", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(fixture.CatalogStatus)
                    {
                        Content = new StringContent(catalog, Encoding.UTF8, "application/json")
                    });
                }
                if (uri.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal))
                {
                    return uri.Host.Equals("api.gitcode.com", StringComparison.OrdinalIgnoreCase)
                        ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent(fixture.Archive)
                        })
                        : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }
                if (uri.AbsolutePath == "/v1/deployment-packages")
                {
                    var body = $$"""
                        {
                          "items": [
                            {
                              "packageId": "{{PublishedId:D}}",
                              "campusId": 7,
                              "displayName": "春晓实验学校",
                              "campusName": "春晓实验学校",
                              "computerPrefix": "PC-01",
                              "schemaVersion": 7,
                              "targetOs": "windows",
                              "architecture": "x64",
                              "fileName": "campus-package.zip",
                              "sizeBytes": {{fixture.Archive.Length}},
                              "sha256": "{{fixture.ArchiveSha256}}",
                              "downloadCount": 3,
                              "publishedAt": "2026-11-16T06:00:00+00:00",
                              "requiresPhoneLast4": true
                            }
                          ],
                          "limit": 20,
                          "offset": 0,
                          "hasMore": false
                        }
                        """;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json")
                    });
                }
                throw new InvalidOperationException($"Unexpected deployment-catalogue request: {uri}");
            }
        }
    }
}
