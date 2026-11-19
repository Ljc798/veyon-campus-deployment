using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

/// <summary>
/// Classroom-local distribution of a campus package. A teacher's machine serves the package it just
/// generated to the classroom LAN, so a first deployment never needs the internet or a repository.
/// </summary>
/// <remarks>
/// This deliberately runs on its own port instead of <see cref="StudentApplicationUpdateCryptography.LocalHttpPort"/>:
/// the update service is started only while a student update is in flight and shuts down afterwards,
/// and a first deployment can easily happen while one is running.
/// </remarks>
public static class DeploymentPackageLanDistribution
{
    /// <summary>
    /// 39178, not 39176: the teacher's mobile-control service already owns 39176 and its bootstrap
    /// owns 39177. 39173-39175 belong to the browser policy agent and the student-update share.
    /// </summary>
    public const int Port = 39178;
    public const string CatalogPath = "/v1/catalog";
    public const string HealthPath = "/v1/health";
    public const string PackagePathPrefix = "/v1/packages/";
    public const string ArchiveFileName = "campus-package.zip";

    internal const int MaximumHeaderBytes = 8192;
    internal const int MaximumCatalogBytes = 256 * 1024;

    public static Uri CatalogUri(IPAddress address) =>
        new($"http://{address}:{Port}{CatalogPath}", UriKind.Absolute);

    public static Uri CatalogUri(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (!IPAddress.TryParse(host, out var address))
            throw new InvalidDataException("教师机的局域网地址必须是 IPv4 字面量。");
        return CatalogUri(address);
    }

    /// <summary>
    /// True for the shape this server publishes: plain HTTP, a private IPv4 literal, the fixed port.
    /// The cloud catalogue insists on HTTPS; a classroom has no certificate authority, so the LAN
    /// path trades transport secrecy for reachability and leans on the SHA-256 check instead.
    /// </summary>
    public static bool IsLanCatalogUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp && uri.Port == Port &&
        IPAddress.TryParse(uri.Host, out var address) &&
        StudentApplicationUpdateCryptography.IsPrivateLanAddress(address);

    public static bool IsLanPackageUri(Uri uri) =>
        IsLanCatalogUri(uri) && uri.AbsolutePath.StartsWith(PackagePathPrefix, StringComparison.Ordinal);
}

/// <summary>One classroom that is currently offering a package.</summary>
public sealed record DeploymentPackageLanProvider(
    IPAddress Address, string CampusName, Guid PackageId, int SchemaVersion, int SizeBytes, string Sha256);

/// <summary>The result of knocking on one address, kept small so a /24 sweep prints as a table.</summary>
public sealed record DeploymentPackageLanProbe(
    string Target, string? Address, bool HasAgent, bool HasProvider, string? AgentVersion, string? CampusName,
    string? Detail);

/// <summary>
/// Serves one already-validated campus package over the classroom LAN for as long as the teacher
/// leaves the sharing panel open.
/// </summary>
public sealed class DeploymentPackageLanServer : IAsyncDisposable
{
    private const int MaximumConnections = 16;

    private readonly TcpListener[] _listeners;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _connectionLimit = new(MaximumConnections, MaximumConnections);
    private readonly System.Collections.Concurrent.ConcurrentBag<Task> _clientTasks = [];
    private readonly Task _acceptTask;
    private readonly byte[] _archive;
    private readonly IProgress<DeploymentPackageLanDownload>? _downloads;

    private DeploymentPackageLanServer(PackageContext package, byte[] archive, int port,
        IProgress<DeploymentPackageLanDownload>? downloads)
    {
        var deploymentId = package.DeploymentId ?? DerivePackageId(archive);
        PackageId = deploymentId;
        CampusName = package.Campus;
        ComputerPrefix = package.ComputerPrefix;
        SchemaVersion = package.SchemaVersion;
        Sha256 = Convert.ToHexString(SHA256.HashData(archive));
        SizeBytes = archive.Length;
        _archive = archive;
        _downloads = downloads;

        var addresses = NetworkInterfaceAddresses();
        if (addresses.Length == 0)
            throw new InvalidOperationException("未发现活动的校区私有 IPv4 网络接口；无法在局域网分发配置包。");
        var listeners = new List<TcpListener>(addresses.Length);
        try
        {
            foreach (var address in addresses)
            {
                var listener = new TcpListener(address, port);
                listener.Start(32);
                listeners.Add(listener);
            }
        }
        catch
        {
            foreach (var listener in listeners) listener.Stop();
            throw;
        }
        Addresses = addresses;
        _listeners = listeners.ToArray();
        _acceptTask = Task.WhenAll(_listeners.Select(listener => AcceptAsync(listener, _shutdown.Token)));
    }

    public Guid PackageId { get; }
    public string CampusName { get; }
    public string ComputerPrefix { get; }
    public int SchemaVersion { get; }
    public string Sha256 { get; }
    public int SizeBytes { get; }
    public IReadOnlyList<IPAddress> Addresses { get; }

    public IReadOnlyList<string> CatalogUrls =>
        Addresses.Select(address => DeploymentPackageLanDistribution.CatalogUri(address).AbsoluteUri).ToArray();

    /// <summary>
    /// Validates the folder exactly the way a publish would, then serves the resulting bytes. Starting
    /// a share on a folder that would fail a cloud publish makes no sense: the students would get a
    /// package the installer then rejects.
    /// </summary>
    public static DeploymentPackageLanServer Start(string packageDirectory,
        IProgress<DeploymentPackageLanDownload>? downloads = null, int port = DeploymentPackageLanDistribution.Port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        var package = PackageContext.Load(packageDirectory);
        if (package.SchemaVersion is not (3 or 4 or 5 or 6 or 7 or 8) || package.WebsitePolicyPublicKeyPath is null)
            throw new InvalidDataException("局域网分发只接受 schemaVersion=3 至 8 且文件清单完整的学生配置包。");
        var archive = CampusConfigurationArchive.Create(packageDirectory);
        if (archive.Length is < 1 or > CampusConfigurationArchive.MaximumArchiveBytes)
            throw new InvalidDataException("配置包大小无效或超过 64 KiB。");
        return new DeploymentPackageLanServer(package, archive, port, downloads);
    }

    private static Guid DerivePackageId(byte[] archive)
    {
        // Legacy campus.json packages carry no packageId. Deriving one from the content keeps the id
        // stable across restarts and different machines, which is what a student's download cache and
        // the teacher's share panel both key on.
        var digest = SHA256.HashData(archive);
        Span<byte> guidBytes = stackalloc byte[16];
        digest.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x40);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }

    /// <summary>
    /// Every private IPv4 the machine can be reached on, with the classroom NIC first. A teacher
    /// machine that also hosts WSL, Hyper-V or a VPN has several, and the one shown to the class has
    /// to be the one a student can actually route to - so an address that owns a default gateway and
    /// sits on a real adapter is ranked above a virtual switch.
    /// </summary>
    private static IPAddress[] NetworkInterfaceAddresses() =>
        DeploymentPackageLanDiscovery.FindClassroomNetworks()
            .Select(network => network.Address)
            .Distinct()
            .ToArray();

    /// <summary>
    /// The catalogue is built per request because the download URL has to point at the address the
    /// client actually reached. A teacher machine with a wired and a wireless NIC has two, and a
    /// student that discovered one of them cannot necessarily route to the other.
    /// </summary>
    private byte[] BuildCatalog(string authority)
    {
        var downloadUrl = $"http://{authority}{DeploymentPackageLanDistribution.PackagePathPrefix}" +
                          $"{PackageId:D}.zip";
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            generatedAt = DateTimeOffset.UtcNow.ToString("O"),
            packages = new[]
            {
                new
                {
                    packageId = PackageId.ToString("D"),
                    campusId = (long?)null,
                    campusName = CampusName,
                    displayName = CampusName,
                    computerPrefix = ComputerPrefix,
                    schemaVersion = SchemaVersion,
                    targetOs = "windows",
                    architecture = "x64",
                    fileName = DeploymentPackageLanDistribution.ArchiveFileName,
                    sizeBytes = SizeBytes,
                    sha256 = Sha256,
                    downloadUrl,
                    mirrors = Array.Empty<string>(),
                    downloadCount = 0,
                    publishedAt = DateTimeOffset.UtcNow.ToString("O"),
                    requiresPhoneLast4 = false,
                    status = "published"
                }
            }
        }, JsonOptions);
        return Encoding.UTF8.GetBytes(json);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private async Task AcceptAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            try { await _connectionLimit.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
                break;
            }
            _clientTasks.Add(HandleAndReleaseAsync(client, cancellationToken));
        }
    }

    private async Task HandleAndReleaseAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try { await HandleClientAsync(client, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or
                                          ObjectDisposedException or UnauthorizedAccessException) { }
        finally
        {
            client.Dispose();
            try { _connectionLimit.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        if (remote is null || !StudentApplicationUpdateCryptography.IsPrivateLanAddress(remote))
            return;
        await using var stream = client.GetStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        var headerBuffer = new byte[DeploymentPackageLanDistribution.MaximumHeaderBytes];
        var totalBytes = 0;
        var headerEnd = -1;
        while (totalBytes < headerBuffer.Length && headerEnd < 0)
        {
            var read = await stream.ReadAsync(headerBuffer.AsMemory(totalBytes), timeout.Token).ConfigureAwait(false);
            if (read == 0) return;
            totalBytes += read;
            headerEnd = headerBuffer.AsSpan(0, totalBytes).IndexOf("\r\n\r\n"u8);
        }
        if (headerEnd < 0) return;

        var requestLine = Encoding.ASCII.GetString(headerBuffer, 0, headerEnd).Split("\r\n")[0]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3 || requestLine[0] != "GET" ||
            requestLine[2] is not ("HTTP/1.0" or "HTTP/1.1"))
        {
            await WriteTextAsync(stream, 404, "not found", timeout.Token).ConfigureAwait(false);
            return;
        }

        var path = requestLine[1];
        if (path == DeploymentPackageLanDistribution.HealthPath)
        {
            var health = JsonSerializer.Serialize(new
            {
                campus = CampusName,
                packageId = PackageId.ToString("D"),
                schemaVersion = SchemaVersion,
                sizeBytes = SizeBytes,
                sha256 = Sha256,
                protocolVersion = 1
            });
            await WriteAsync(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(health),
                timeout.Token).ConfigureAwait(false);
            return;
        }
        if (path == DeploymentPackageLanDistribution.CatalogPath)
        {
            var authority = client.Client.LocalEndPoint is IPEndPoint local
                ? $"{local.Address}:{local.Port}"
                : $"{Addresses[0]}:{DeploymentPackageLanDistribution.Port}";
            await WriteAsync(stream, 200, "application/json; charset=utf-8", BuildCatalog(authority),
                timeout.Token).ConfigureAwait(false);
            return;
        }
        if (path == $"{DeploymentPackageLanDistribution.PackagePathPrefix}{PackageId:D}.zip")
        {
            await WriteAsync(stream, 200, "application/zip", _archive, timeout.Token).ConfigureAwait(false);
            _downloads?.Report(new DeploymentPackageLanDownload(remote.ToString(), PackageId, _archive.Length,
                DateTimeOffset.UtcNow));
            return;
        }
        await WriteTextAsync(stream, 404, "not found", timeout.Token).ConfigureAwait(false);
    }

    private static Task WriteTextAsync(Stream stream, int status, string text, CancellationToken cancellationToken) =>
        WriteAsync(stream, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), cancellationToken);

    private static async Task WriteAsync(Stream stream, int status, string contentType, byte[] body,
        CancellationToken cancellationToken)
    {
        var header = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n" +
                     $"Content-Type: {contentType}\r\nContent-Length: {body.Length}\r\n" +
                     "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        foreach (var listener in _listeners) listener.Stop();
        try { await _acceptTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        try { await Task.WhenAll(_clientTasks.ToArray()).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or
                                          ObjectDisposedException) { }
        _shutdown.Dispose();
        _connectionLimit.Dispose();
    }
}

/// <summary>
/// Knocks on the addresses a teacher already has from the Veyon directory. Two different ports answer:
/// the Agent's health endpoint says "this machine runs Veyon Campus", the distribution endpoint says
/// "this machine is offering a package right now".
/// </summary>
/// <summary>One private IPv4 network this machine is attached to, with the gateway that fronts it.</summary>
public sealed record ClassroomNetwork(IPAddress Address, int PrefixLength, IPAddress? Gateway)
{
    /// <summary>"192.168.31.0/24", for status text a technician can compare against ipconfig.</summary>
    public string SubnetDescription
    {
        get
        {
            var bytes = Address.GetAddressBytes();
            var prefix = PrefixLength is >= 8 and <= 30 ? PrefixLength : 24;
            var mask = uint.MaxValue << (32 - prefix);
            var host = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
            var network = host & mask;
            return $"{(network >> 24) & 0xFF}.{(network >> 16) & 0xFF}.{(network >> 8) & 0xFF}.{network & 0xFF}/{prefix}";
        }
    }
}

public static class DeploymentPackageLanDiscovery
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A classroom subnet is a /24 or /23. The ceiling here is a /20, which is what a Hyper-V or
    /// VMware host-only switch hands out - walking all 4094 hosts is slow but it is the difference
    /// between discovering the teacher machine and silently scanning the wrong 254 addresses.
    /// </summary>
    private const int MaximumSweepHosts = 4094;

    /// <summary>
    /// The private IPv4 networks this machine can reach a classroom through, best candidate first.
    /// Virtual switches (WSL, Hyper-V's Default Switch, VMware host-only) are private too, and on a
    /// machine that has one they are frequently the address the operating system enumerates first.
    /// Picking that one and sweeping its /24 is the single most common reason a student never finds
    /// the teacher, so the ranking prefers an adapter that owns a default gateway and is not virtual.
    /// </summary>
    public static IReadOnlyList<ClassroomNetwork> FindClassroomNetworks()
    {
        var candidates = new List<(int Rank, ClassroomNetwork Network)>();
        foreach (var networkInterface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                continue;
            if (networkInterface.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                continue;
            var properties = networkInterface.GetIPProperties();
            var gateway = properties.GatewayAddresses
                .Select(candidate => candidate.Address)
                .FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork &&
                                             !candidate.Equals(IPAddress.Any));
            var realAdapter = networkInterface.NetworkInterfaceType is
                System.Net.NetworkInformation.NetworkInterfaceType.Ethernet or
                System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 or
                System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet;
            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(address)) continue;
                if (!StudentApplicationUpdateCryptography.IsPrivateLanAddress(address)) continue;
                var rank = (gateway is null ? 10 : 0) + (realAdapter ? 0 : 5);
                candidates.Add((rank, new ClassroomNetwork(address, unicast.PrefixLength, gateway)));
            }
        }
        return candidates
            .OrderBy(candidate => candidate.Rank)
            .ThenBy(candidate => candidate.Network.Address.ToString(), StringComparer.Ordinal)
            .Select(candidate => candidate.Network)
            .GroupBy(network => network.Address)
            .Select(group => group.First())
            .ToArray();
    }

    /// <summary>
    /// The hosts of the subnet the given address belongs to. A classroom is one subnet, and the
    /// teacher machine cannot read the Veyon directory's addresses reliably before the first
    /// deployment has run - so the sweep is address-based rather than directory-based.
    /// </summary>
    public static IReadOnlyList<string> EnumerateClassroomTargets(IPAddress localAddress, int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(localAddress);
        if (localAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException("局域网扫描只支持 IPv4。");
        if (!StudentApplicationUpdateCryptography.IsPrivateLanAddress(localAddress))
            throw new InvalidDataException("局域网扫描只接受校区内网地址。");

        var prefix = prefixLength is >= 8 and <= 30 ? prefixLength : 24;
        var bytes = localAddress.GetAddressBytes();
        var host = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        var mask = uint.MaxValue << (32 - prefix);
        var network = host & mask;
        var broadcast = network | ~mask;
        if (broadcast - network - 1 > MaximumSweepHosts)
        {
            // Too large to walk (a /16 corporate VLAN, say). Fall back to the /24 that holds this
            // machine: that is where a classroom teacher is, if the mask was merely generous.
            network = host & 0xFFFFFF00;
            broadcast = network | 0xFF;
        }

        var targets = new List<string>((int)(broadcast - network - 1));
        for (var candidate = network + 1; candidate < broadcast; candidate++)
        {
            if (candidate == host) continue;
            targets.Add($"{(candidate >> 24) & 0xFF}.{(candidate >> 16) & 0xFF}.{(candidate >> 8) & 0xFF}.{candidate & 0xFF}");
        }
        return targets;
    }

    /// <summary>The 254 hosts of the /24 the address sits in - the fast first pass.</summary>
    public static IReadOnlyList<string> EnumerateClassroomTargets(IPAddress localAddress) =>
        EnumerateClassroomTargets(localAddress, 24);

    /// <summary>Every candidate address across the given networks; the gateways come first.</summary>
    public static IReadOnlyList<string> EnumerateClassroomTargets(IReadOnlyList<ClassroomNetwork> networks)
    {
        ArgumentNullException.ThrowIfNull(networks);
        var targets = new List<string>();
        foreach (var network in networks)
        {
            if (network.Gateway is { } gateway) targets.Add(gateway.ToString());
            targets.AddRange(EnumerateClassroomTargets(network.Address, network.PrefixLength));
        }
        return targets.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var connect = timeout ?? ProbeTimeout;
        return new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = connect
        })
        {
            Timeout = connect
        };
    }

    public static async Task<DeploymentPackageLanProbe> ProbeAsync(string target,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidDataException("探测目标不能为空。");
        var name = target.Trim();
        IPAddress? address = null;
        try
        {
            var addresses = IPAddress.TryParse(name, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(name, cancellationToken).ConfigureAwait(false);
            address = addresses.FirstOrDefault(candidate =>
                candidate.AddressFamily == AddressFamily.InterNetwork &&
                StudentApplicationUpdateCryptography.IsPrivateLanAddress(candidate));
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException or IOException)
        {
            return new DeploymentPackageLanProbe(name, null, false, false, null, null,
                "无法解析地址：" + exception.Message);
        }
        if (address is null)
            return new DeploymentPackageLanProbe(name, null, false, false, null, null, "没有校区内网 IPv4 地址");

        using var client = CreateClient();
        var hasAgent = false;
        string? agentVersion = null;
        try
        {
            using var response = await client.GetAsync(
                new Uri($"http://{address}:{WebsitePolicyAgent.Port}/health"), cancellationToken).ConfigureAwait(false);
            hasAgent = response.IsSuccessStatusCode &&
                       response.Headers.Contains("X-VeyonCampus-Agent-Version");
            if (hasAgent)
                agentVersion = response.Headers.TryGetValues("X-VeyonCampus-Agent-Version", out var values)
                    ? values.FirstOrDefault()
                    : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // No Agent answering on the policy port: the machine may still run Veyon without our Agent.
        }

        string? campusName = null;
        var hasProvider = false;
        try
        {
            using var response = await client.GetAsync(
                new Uri($"http://{address}:{DeploymentPackageLanDistribution.Port}" +
                        DeploymentPackageLanDistribution.HealthPath), cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await response.Content
                    .ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                hasProvider = true;
                campusName = document.RootElement.TryGetProperty("campus", out var campus)
                    ? campus.GetString()
                    : null;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
        }

        return new DeploymentPackageLanProbe(name, address.ToString(), hasAgent, hasProvider, agentVersion,
            campusName, hasAgent ? null : (hasProvider ? null : "未响应"));
    }

    /// <summary>
    /// Sweeps for teacher machines that are currently offering a package. This asks only the
    /// distribution port, so a student setup screen can finish in one timeout per address instead of
    /// two.
    /// </summary>
    public static async Task<IReadOnlyList<DeploymentPackageLanProvider>> FindProvidersAsync(
        IEnumerable<string> targets, int parallelism = 48, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var distinct = targets
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Select(target => target.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinct.Length == 0) return [];
        return await SweepAsync(distinct, parallelism, ProbeTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds a teacher machine sharing a package across every network this machine is attached to.
    /// Gateways and the local /24 are asked first: on a bridged classroom that is the entire sweep, and
    /// in a NAT'd test VM the gateway is the host - which is usually the teacher itself. A generous
    /// mask (the /20 a virtual switch hands out) is only walked in full when the fast pass finds
    /// nothing, so the common case still finishes in about two seconds.
    /// </summary>
    public static async Task<IReadOnlyList<DeploymentPackageLanProvider>> FindProvidersAsync(
        IReadOnlyList<ClassroomNetwork> networks, IEnumerable<string>? extraTargets = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(networks);
        var nearby = new List<string>();
        var remaining = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in extraTargets ?? [])
        {
            if (string.IsNullOrWhiteSpace(target)) continue;
            var trimmed = target.Trim();
            if (seen.Add(trimmed)) nearby.Add(trimmed);
        }
        foreach (var network in networks)
        {
            if (network.Gateway is { } gateway && seen.Add(gateway.ToString()))
                nearby.Add(gateway.ToString());
            foreach (var target in EnumerateClassroomTargets(network.Address, 24))
                if (seen.Add(target)) nearby.Add(target);
            if (network.PrefixLength is >= 16 and < 24)
                foreach (var target in EnumerateClassroomTargets(network.Address, network.PrefixLength))
                    if (seen.Add(target)) remaining.Add(target);
        }
        if (nearby.Count == 0 && remaining.Count == 0) return [];

        var fast = await SweepAsync(nearby, 128, ProbeTimeout, cancellationToken).ConfigureAwait(false);
        if (fast.Count > 0 || remaining.Count == 0) return fast;
        return await SweepAsync(remaining, 128, TimeSpan.FromMilliseconds(800), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<DeploymentPackageLanProvider>> SweepAsync(
        IReadOnlyList<string> targets, int parallelism, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var providers = new System.Collections.Concurrent.ConcurrentBag<DeploymentPackageLanProvider>();
        using var throttle = new SemaphoreSlim(Math.Clamp(parallelism, 1, 128));
        var tasks = targets.Select(async target =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var provider = await ProbeProviderAsync(target, timeout, cancellationToken).ConfigureAwait(false);
                if (provider is not null) providers.Add(provider);
            }
            finally { throttle.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return providers.OrderBy(provider => provider.Address.ToString(), StringComparer.Ordinal).ToArray();
    }

    private static async Task<DeploymentPackageLanProvider?> ProbeProviderAsync(string target, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(target, out var address) ||
            !StudentApplicationUpdateCryptography.IsPrivateLanAddress(address))
            return null;
        using var client = CreateClient(timeout);
        try
        {
            using var response = await client.GetAsync(
                new Uri($"http://{address}:{DeploymentPackageLanDistribution.Port}" +
                        DeploymentPackageLanDistribution.HealthPath), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content
                .ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            if (!root.TryGetProperty("packageId", out var packageIdJson) ||
                !Guid.TryParse(packageIdJson.GetString(), out var packageId))
                return null;
            var campusName = root.TryGetProperty("campus", out var campus) ? campus.GetString() ?? "" : "";
            var schemaVersion = root.TryGetProperty("schemaVersion", out var schema) ? schema.GetInt32() : 0;
            var sizeBytes = root.TryGetProperty("sizeBytes", out var size) ? size.GetInt32() : 0;
            var sha256 = root.TryGetProperty("sha256", out var digest) ? digest.GetString() ?? "" : "";
            return new DeploymentPackageLanProvider(address, campusName, packageId, schemaVersion, sizeBytes,
                sha256);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                          JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public static async Task<IReadOnlyList<DeploymentPackageLanProvider>> FindProvidersAsync(
        IPAddress localAddress, CancellationToken cancellationToken = default) =>
        await FindProvidersAsync(EnumerateClassroomTargets(localAddress), 48, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>The best private IPv4 for a classroom, or null when this machine is not on one.</summary>
    public static IPAddress? FindLocalClassroomAddress() =>
        FindClassroomNetworks().FirstOrDefault()?.Address;
    public static async Task<IReadOnlyList<DeploymentPackageLanProbe>> ProbeAsync(
        IEnumerable<string> targets, int parallelism = 24, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (parallelism is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(parallelism), "并发数必须在 1 至 128 之间。");
        var distinct = targets
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Select(target => target.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinct.Length == 0) return [];
        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, DeploymentPackageLanProbe>(
            StringComparer.OrdinalIgnoreCase);
        using var throttle = new SemaphoreSlim(parallelism, parallelism);
        var tasks = distinct.Select(async target =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var probe = await ProbeAsync(target, cancellationToken).ConfigureAwait(false);
                results[target] = probe;
            }
            finally { throttle.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return distinct.Select(target => results[target]).ToArray();
    }
}

public sealed record DeploymentPackageLanDownload(string ClientAddress, Guid PackageId, long BytesSent,
    DateTimeOffset At);

/// <summary>
/// Student-side entry point: sweeps the classroom for teacher machines that are sharing a package and
/// reads their catalogues through the same client the internet path uses. A first deployment in a
/// room with no internet is exactly the case this exists for.
/// </summary>
/// <summary>The result of a classroom sweep, including where it looked so the status text can say so.</summary>
public sealed record LanPackageScanResult(
    IReadOnlyList<DeploymentPackageCatalogEntry> Items,
    IReadOnlyList<ClassroomNetwork> Networks,
    int Providers);

/// <summary>
/// Student-side entry point: sweeps the classroom for teacher machines that are sharing a package and
/// reads their catalogues through the same client the internet path uses. A first deployment in a
/// room with no internet is exactly the case this exists for.
/// </summary>
public static class LanPackageScanner
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    /// <param name="manualTargets">
    /// Addresses the technician typed in. They are probed first, which is the only way out when the
    /// student and the teacher sit on different subnets (a NAT'd test VM, two classroom VLANs).
    /// </param>
    public static async Task<LanPackageScanResult> FindPackagesAsync(string query = "",
        IEnumerable<string>? manualTargets = null, CancellationToken cancellationToken = default)
    {
        var networks = DeploymentPackageLanDiscovery.FindClassroomNetworks();
        var manual = (manualTargets ?? [])
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Select(target => target.Trim())
            .ToArray();
        if (networks.Count == 0 && manual.Length == 0) return new LanPackageScanResult([], [], 0);

        var providers = await DeploymentPackageLanDiscovery
            .FindProvidersAsync(networks, manual, cancellationToken).ConfigureAwait(false);
        var items = new List<DeploymentPackageCatalogEntry>();
        foreach (var provider in providers)
        {
            var client = new DeploymentPackageCatalogClient(
                [DeploymentCatalogSource.Local(provider.Address)], null, Client);
            var result = await client.SearchAsync(query, null, cancellationToken).ConfigureAwait(false);
            items.AddRange(result.Items);
        }
        return new LanPackageScanResult(items, networks, providers.Count);
    }
}
