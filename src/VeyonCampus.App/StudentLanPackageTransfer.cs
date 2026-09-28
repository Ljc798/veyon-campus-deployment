using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;
using VeyonCampus.Core;

namespace VeyonCampus.App;

internal sealed record ReceivedCampusConfiguration(string DirectoryPath, string Campus, int SchemaVersion, long ArchiveBytes);

internal static class StudentLanPackageTransfer
{
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(90);

    public static async Task<ReceivedCampusConfiguration> DownloadAsync(string serviceAddress, string pairingCode,
        string certificateCode, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(serviceAddress?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !IPAddress.TryParse(uri.Host, out var address) ||
            !IsPrivateLanAddress(address) || uri.Port != LanDistributionProtocol.HttpsPort ||
            uri.AbsolutePath != LanDistributionProtocol.PackageRoute ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("请输入教师端显示的完整 HTTPS 局域网地址；只接受教师电脑的 IPv4 地址和固定校区配置路径。");

        var normalizedAccessCode = LanDistributionProtocol.NormalizeAccessCode(pairingCode);
        var expectedCertificateCode = LanDistributionProtocol.NormalizeCertificateCode(certificateCode);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "VeyonCampus-LanDownload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryDirectoryInfo = new DirectoryInfo(temporaryDirectory);
        if (temporaryDirectoryInfo.LinkTarget is not null ||
            (temporaryDirectoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("下载暂存目录不能是符号链接或重解析点。");
        var partPath = Path.Combine(temporaryDirectory, "campus-configuration.zip.part");
        try
        {
            progress?.Report("正在连接教师电脑并核对临时 HTTPS 证书……");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TransferTimeout);
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                    IsPinnedTeacherCertificate(certificate, expectedCertificateCode)
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", normalizedAccessCode);
            request.Headers.ConnectionClose = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new InvalidDataException("配对码不正确或已过期；请让教师重新开启局域网分发。");
            if (response.StatusCode == HttpStatusCode.Gone)
                throw new InvalidDataException("教师端局域网分发已过期；请让教师重新开启服务。");
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidDataException("教师端未提供校区配置下载，请核对完整地址。");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new InvalidDataException("教师端当前下载人数较多，请稍后重试。");
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException($"教师端返回 HTTP {(int)response.StatusCode}，没有下载配置文件。");

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is null or < 1 or > CampusConfigurationArchive.MaximumArchiveBytes)
                throw new InvalidDataException("教师端返回的文件大小缺失或超过允许上限。");
            if (response.Content.Headers.ContentType?.MediaType != "application/zip")
                throw new InvalidDataException("教师端返回的内容类型不是校区配置 ZIP。");

            progress?.Report($"正在接收校区配置（{contentLength.Value:N0} 字节）……");
            long received = 0;
            await using (var destination = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 32 * 1024, options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            {
                var buffer = new byte[32 * 1024];
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > CampusConfigurationArchive.MaximumArchiveBytes || received > contentLength.Value)
                        throw new InvalidDataException("下载内容超过教师端声明的大小；已丢弃不完整文件。");
                    await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
                await destination.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            if (received != contentLength.Value)
                throw new InvalidDataException($"下载中断（收到 {received:N0}/{contentLength.Value:N0} 字节）；未加载不完整配置。");

            progress?.Report("文件接收完成，正在校验清单、公钥摘要和 ZIP 内容……");
            var archiveBytes = await File.ReadAllBytesAsync(partPath, timeout.Token).ConfigureAwait(false);
            var storageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VeyonCampus", "ReceivedPackages");
            var package = CampusConfigurationArchive.ExtractToStore(archiveBytes, storageRoot);
            progress?.Report("校区配置通过来源连接和完整性检查，正在载入部署表单……");
            return new ReceivedCampusConfiguration(package.Root, package.Campus, package.SchemaVersion, received);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("局域网下载超过 90 秒，未使用不完整文件；请确认两台电脑仍在同一局域网并重试。");
        }
        finally
        {
            try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsPinnedTeacherCertificate(X509Certificate2? certificate, byte[] expectedCode)
    {
        if (certificate is null) return false;
        try
        {
            var now = DateTime.UtcNow;
            if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime() ||
                !certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData))
                return false;
            using var rsa = certificate.GetRSAPublicKey();
            if (rsa is null || rsa.KeySize < 2048) return false;
            var hasServerAuthenticationUsage = certificate.Extensions
                .OfType<X509EnhancedKeyUsageExtension>()
                .SelectMany(extension => extension.EnhancedKeyUsages.Cast<Oid>())
                .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1");
            if (!hasServerAuthenticationUsage) return false;

            var actualCode = Convert.FromHexString(new string(LanDistributionProtocol.CertificateCode(certificate)
                .Where(Uri.IsHexDigit).ToArray()));
            return CryptographicOperations.FixedTimeEquals(actualCode, expectedCode);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool IsPrivateLanAddress(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] == 169 && bytes[1] == 254;
    }
}
