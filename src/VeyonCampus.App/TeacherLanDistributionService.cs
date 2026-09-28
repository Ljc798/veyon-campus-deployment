using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public sealed record LanDistributionSession(string Url, string PairingCode, string CertificateCode,
    string Campus, DateTimeOffset ExpiresAtLocal, int ArchiveBytes);

/// <summary>
/// Hosts one immutable campus-configuration snapshot over a narrowly bound,
/// short-lived HTTPS endpoint. No general file or command routes are exposed.
/// </summary>
public sealed class TeacherLanDistributionService : IAsyncDisposable
{
    private const string FirewallRuleName = "VeyonCampus-Lan-Distribution";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(20);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _app;
    private X509Certificate2? _certificate;
    private CancellationTokenSource? _expiryCancellation;
    private Semaphore? _singleServiceSemaphore;
    private string? _firewallRuleName;
    private int _completedDownloads;
    private bool _ownsSemaphore;

    public event EventHandler? SessionExpired;
    public event EventHandler? DownloadCompleted;
    public bool IsRunning => _app is not null;
    public bool HasPendingFirewallRule => _firewallRuleName is not null;
    public int CompletedDownloads => Volatile.Read(ref _completedDownloads);

    public async Task<LanDistributionSession> StartAsync(string packageDirectory, IPAddress address,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("局域网分发服务只支持 Windows 教师端。");
        if (address.AddressFamily != AddressFamily.InterNetwork || !IsPrivateLanAddress(address))
            throw new InvalidDataException("请选择当前教师电脑上的 IPv4 局域网地址；不接受公网、回环或任意远程地址。");

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_app is not null) throw new InvalidOperationException("局域网分发已经开启。");
            AcquireSingleServiceLock();

            try
            {

            byte[] archive;
            string campus;
            try
            {
                var package = PackageManifest.Load(packageDirectory);
                campus = package.Campus;
                archive = CampusConfigurationArchive.Create(packageDirectory);
            }
            catch
            {
                ReleaseSingleServiceLock();
                throw;
            }

            var accessCode = LanDistributionProtocol.CreateAccessCode();
            var accessCodeHash = LanDistributionProtocol.HashAccessCode(accessCode);
            X509Certificate2? certificate = null;
            var certificateCode = "";
            var expiresAt = DateTimeOffset.UtcNow.Add(SessionLifetime);
            var ruleName = FirewallRuleName;
            WebApplication? app = null;
            try
            {
                var serverCertificate = CreateServerCertificate(address);
                certificate = serverCertificate;
                certificateCode = LanDistributionProtocol.CertificateCode(serverCertificate);
                // A fixed scoped rule is removed before reuse, which also cleans up a stale
                // rule left behind if a previous teacher process was forcibly terminated.
                await RemoveFirewallRuleAsync(ruleName, ignoreFailure: true, cancellationToken).ConfigureAwait(false);
                _firewallRuleName = ruleName;
                await AddFirewallRuleAsync(ruleName, address, LanDistributionProtocol.HttpsPort, cancellationToken).ConfigureAwait(false);

                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    Args = Array.Empty<string>(),
                    ApplicationName = typeof(TeacherLanDistributionService).Assembly.GetName().Name,
                    EnvironmentName = "Production"
                });
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.AddServerHeader = false;
                    options.Limits.MaxRequestBodySize = 1024;
                    options.Limits.MaxConcurrentConnections = 16;
                    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
                    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(10);
                    options.Listen(address, LanDistributionProtocol.HttpsPort, listen => listen.UseHttps(serverCertificate));
                });

                app = builder.Build();
                var semaphore = new SemaphoreSlim(4, 4);
                app.MapGet(LanDistributionProtocol.PackageRoute, async context =>
                {
                    if (context.Request.Headers.ContainsKey("Origin") || context.Request.Headers.ContainsKey("Referer"))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }
                    if (context.Request.ContentLength is > 0)
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        return;
                    }
                    if (DateTimeOffset.UtcNow >= expiresAt)
                    {
                        context.Response.StatusCode = StatusCodes.Status410Gone;
                        return;
                    }
                    var authorization = context.Request.Headers["Authorization"].ToString();
                    const string bearerPrefix = "Bearer ";
                    var suppliedCode = authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
                        ? authorization[bearerPrefix.Length..]
                        : "";
                    if (!LanDistributionProtocol.AccessCodeMatches(suppliedCode, accessCodeHash))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.Headers["Cache-Control"] = "no-store";
                        return;
                    }
                    if (!await semaphore.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
                    {
                        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                        context.Response.Headers["Retry-After"] = "3";
                        return;
                    }
                    try
                    {
                        context.Response.StatusCode = StatusCodes.Status200OK;
                        context.Response.ContentType = "application/zip";
                        context.Response.ContentLength = archive.Length;
                        context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                        context.Response.Headers["Pragma"] = "no-cache";
                        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                        context.Response.Headers["Content-Disposition"] = "attachment; filename=campus-configuration.zip";
                        await context.Response.Body.WriteAsync(archive, context.RequestAborted).ConfigureAwait(false);
                        Interlocked.Increment(ref _completedDownloads);
                        try { DownloadCompleted?.Invoke(this, EventArgs.Empty); }
                        catch (Exception) { }
                    }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                    finally { semaphore.Release(); }
                });
                app.MapFallback(async context =>
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response.WriteAsync("Not found", context.RequestAborted).ConfigureAwait(false);
                });

                Interlocked.Exchange(ref _completedDownloads, 0);
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
                _app = app;
                app = null;
                _certificate = serverCertificate;
                certificate = null;
                _expiryCancellation = new CancellationTokenSource();
                _ = ExpireSessionAsync(expiresAt, _expiryCancellation.Token);
                return new LanDistributionSession($"https://{address}:{LanDistributionProtocol.HttpsPort}{LanDistributionProtocol.PackageRoute}", accessCode,
                    certificateCode, campus, expiresAt.ToLocalTime(), archive.Length);
            }
            catch
            {
                if (app is not null)
                {
                    try { await app.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { }
                    try { await app.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception) { }
                }
                certificate?.Dispose();
                if (_firewallRuleName == ruleName)
                {
                    try
                    {
                        await RemoveFirewallRuleAsync(ruleName, ignoreFailure: false, CancellationToken.None).ConfigureAwait(false);
                        _firewallRuleName = null;
                    }
                    catch (Exception) { }
                }
                ReleaseSingleServiceLock();
                throw;
            }
            }
            catch
            {
                if (_app is null) ReleaseSingleServiceLock();
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _expiryCancellation?.Cancel();
            _expiryCancellation?.Dispose();
            _expiryCancellation = null;
            var app = _app;
            _app = null;
            Exception? shutdownError = null;
            if (app is not null)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await app.StopAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) { shutdownError = exception; }
                try { await app.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { shutdownError ??= exception; }
            }

            var certificate = _certificate;
            _certificate = null;
            certificate?.Dispose();
            var ruleName = _firewallRuleName;
            if (ruleName is not null)
            {
                try
                {
                    await RemoveFirewallRuleAsync(ruleName, ignoreFailure: false, CancellationToken.None).ConfigureAwait(false);
                    _firewallRuleName = null;
                }
                catch (Exception exception) { shutdownError ??= exception; }
                finally { ReleaseSingleServiceLock(); }
            }
            else ReleaseSingleServiceLock();
            if (shutdownError is not null)
                throw new InvalidOperationException("局域网监听已停止，但清理时遇到问题。" + shutdownError.Message, shutdownError);
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally { _lifecycle.Dispose(); }
    }

    private async Task ExpireSessionAsync(DateTimeOffset expiry, CancellationToken cancellationToken)
    {
        try { await Task.Delay(expiry - DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try { await StopAsync().ConfigureAwait(false); }
        catch (Exception) { }
        try { SessionExpired?.Invoke(this, EventArgs.Empty); }
        catch (Exception) { }
    }

    private void AcquireSingleServiceLock()
    {
        _singleServiceSemaphore = new Semaphore(1, 1, "Global\\VeyonCampus.LanDistribution");
        _ownsSemaphore = _singleServiceSemaphore.WaitOne(0);
        if (!_ownsSemaphore)
        {
            _singleServiceSemaphore.Dispose();
            _singleServiceSemaphore = null;
            throw new InvalidOperationException("另一個教師控制台已開啟局域網分發；請先在原視窗停止服務。");
        }
    }

    private void ReleaseSingleServiceLock()
    {
        if (_ownsSemaphore)
        {
            try { _singleServiceSemaphore?.Release(); }
            catch (SemaphoreFullException) { }
            _ownsSemaphore = false;
        }
        _singleServiceSemaphore?.Dispose();
        _singleServiceSemaphore = null;
    }

    private static bool IsPrivateLanAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] == 169 && bytes[1] == 254;
    }

    private static X509Certificate2 CreateServerCertificate(IPAddress address)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=VeyonCampus LAN Distribution", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        var usages = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, critical: true));
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddIpAddress(address);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build(critical: false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(25));
    }

    private static Task AddFirewallRuleAsync(string name, IPAddress address, int port, CancellationToken cancellationToken) =>
        RunNetshAsync([
            "advfirewall", "firewall", "add", "rule", "name=" + name,
            "dir=in", "action=allow", "enable=yes", "protocol=TCP", "profile=any",
            "localip=" + address, "localport=" + port, "remoteip=LocalSubnet",
            "program=" + (Environment.ProcessPath ?? throw new InvalidOperationException("無法取得教師控制台程序路徑。"))
        ], cancellationToken, "無法建立限於所選局域網網卡的臨時 Windows 防火牆規則。請以管理員身分啟動教師控制台並確認 Windows 防火牆可用。");

    private static async Task RemoveFirewallRuleAsync(string name, bool ignoreFailure, CancellationToken cancellationToken)
    {
        try
        {
            await RunNetshAsync(["advfirewall", "firewall", "delete", "rule", "name=" + name],
                cancellationToken, "無法移除局域網分發的臨時 Windows 防火牆規則。").ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (ignoreFailure) { }
        catch (OperationCanceledException) when (ignoreFailure) { }
    }

    private static async Task RunNetshAsync(string[] arguments, CancellationToken cancellationToken, string errorPrefix)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "netsh.exe");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException(errorPrefix);
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{errorPrefix}\nnetsh：{output.Trim()} (exit {process.ExitCode})");
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new InvalidOperationException(errorPrefix + " 操作超時。");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException(errorPrefix + " " + exception.Message, exception);
        }
    }
}
