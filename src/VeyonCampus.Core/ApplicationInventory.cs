using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VeyonCampus.Core;

public sealed record ApplicationInventoryRequest(int SchemaVersion, string Purpose, string CampusId,
    Guid Nonce, DateTimeOffset IssuedUtc);

public sealed record ApplicationInventoryItem(string DisplayName, string FilePath, string BinaryName,
    string? PublisherName, string? ProductName, string? BinaryVersion,
    string FileSha256, string AppLockerHashSha256, long FileLength);

public sealed record ApplicationInventoryResponse(int SchemaVersion, string Purpose, string CampusId,
    Guid Nonce, DateTimeOffset CollectedUtc, IReadOnlyList<ApplicationInventoryItem> Items);

public static class ApplicationInventoryCryptography
{
    public const string RequestPurpose = "VeyonCampus.ApplicationInventoryRequest.v1";
    public const string ResponsePurpose = "VeyonCampus.ApplicationInventoryResponse.v1";
    public const int MaximumItems = 200;
    public static readonly TimeSpan MaximumRequestAge = TimeSpan.FromMinutes(5);

    public static string SignRequest(ApplicationInventoryRequest request, RSA privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        ValidateRequest(request, request.IssuedUtc);
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, ApplicationPolicyCompiler.JsonOptions);
        if (payload.Length > 4096) throw new InvalidDataException("应用清单请求超过大小限制。");
        return JsonSerializer.Serialize(new SignedWebsitePolicy(Convert.ToBase64String(payload),
            Convert.ToBase64String(privateKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))));
    }

    public static ApplicationInventoryRequest VerifyRequest(string envelopeJson, string publicKeyPem,
        string campusId, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(envelopeJson) || Encoding.UTF8.GetByteCount(envelopeJson) > 8192)
            throw new InvalidDataException("应用清单请求大小无效。");
        try
        {
            PolicyJson.RejectDuplicateFields(Encoding.UTF8.GetBytes(envelopeJson));
            var envelope = JsonSerializer.Deserialize<SignedWebsitePolicy>(envelopeJson,
                new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                ?? throw new InvalidDataException("应用清单请求信封为空。");
            var payload = Convert.FromBase64String(envelope.Payload);
            var signature = Convert.FromBase64String(envelope.Signature);
            if (payload.Length is 0 or > 4096 || signature.Length is < 256 or > 512)
                throw new InvalidDataException("应用清单请求字段大小无效。");
            PolicyJson.RejectDuplicateFields(payload);
            using var key = RSA.Create();
            key.ImportFromPem(publicKeyPem);
            if (key.KeySize is < 2048 or > 4096 ||
                !key.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("应用清单请求签名无效。");
            var request = JsonSerializer.Deserialize<ApplicationInventoryRequest>(payload,
                ApplicationPolicyCompiler.JsonOptions) ?? throw new InvalidDataException("应用清单请求正文为空。");
            ValidateRequest(request, nowUtc);
            if (!string.Equals(request.CampusId, campusId, StringComparison.Ordinal))
                throw new InvalidDataException("应用清单请求属于其他校区。");
            return request;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or CryptographicException or ArgumentException)
        { throw new InvalidDataException("应用清单请求编码无效。", exception); }
    }

    public static ApplicationInventoryResponse CreateResponse(ApplicationInventoryRequest request,
        IReadOnlyList<ApplicationInventoryItem> items, DateTimeOffset collectedUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(items);
        var response = new ApplicationInventoryResponse(1, ResponsePurpose, request.CampusId, request.Nonce,
            collectedUtc.ToUniversalTime(), Array.AsReadOnly(items.ToArray()));
        ValidateResponse(response, request, collectedUtc);
        return response;
    }

    public static void ValidateResponse(ApplicationInventoryResponse response,
        ApplicationInventoryRequest request, DateTimeOffset nowUtc)
    {
        if (response.SchemaVersion != 1 || response.Purpose != ResponsePurpose ||
            response.CampusId != request.CampusId || response.Nonce != request.Nonce ||
            response.CollectedUtc.Offset != TimeSpan.Zero || response.CollectedUtc < request.IssuedUtc.AddMinutes(-1) ||
            response.CollectedUtc > nowUtc.ToUniversalTime().AddMinutes(2) || response.Items is null ||
            response.Items.Count > MaximumItems || response.Items.Any(item => item is null ||
                string.IsNullOrWhiteSpace(item.DisplayName) || item.DisplayName.Length > 160 ||
                string.IsNullOrWhiteSpace(item.FilePath) || item.FilePath.Length > 2048 || item.FilePath.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(item.BinaryName) || item.BinaryName.Length > 256 ||
                item.PublisherName is { Length: > 512 } || item.ProductName is { Length: > 256 } ||
                item.BinaryVersion is { Length: > 32 } || !IsHash(item.FileSha256) || !IsHash(item.AppLockerHashSha256) ||
                item.FileLength is <= 0 or > 1024L * 1024 * 1024))
            throw new InvalidDataException("应用清单回执的校区、随机数、时间或条目字段无效。");
    }

    private static void ValidateRequest(ApplicationInventoryRequest request, DateTimeOffset nowUtc)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(request.CampusId);
        var now = nowUtc.ToUniversalTime();
        if (request.SchemaVersion != 1 || request.Purpose != RequestPurpose || request.Nonce == Guid.Empty ||
            request.IssuedUtc.Offset != TimeSpan.Zero || request.IssuedUtc > now.AddMinutes(1) ||
            request.IssuedUtc < now - MaximumRequestAge)
            throw new InvalidDataException("应用清单请求版本、签发时间或随机数无效。");
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

/// <summary>Reads installed desktop executables and asks Windows' AppLocker cmdlet for rule conditions.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsApplicationInventoryReader
{
    private const int MaximumCandidates = 400;
    private const int BatchSize = 32;
    private const int MaximumPowerShellOutputCharacters = 512 * 1024;
    private const string PowerShellScript = "Import-Module AppLocker -ErrorAction Stop; " +
        "$json=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:VEYONCAMPUS_APP_PATHS)); " +
        "$paths=@(ConvertFrom-Json -InputObject $json); " +
        "$rows=@(Get-AppLockerFileInformation -Path $paths -ErrorAction SilentlyContinue | ForEach-Object { " +
        "[pscustomobject]@{Path=[string]$_.Path; Publisher=[string]$_.Publisher; Hash=[string]$_.Hash} }); " +
        "ConvertTo-Json -InputObject $rows -Compress -Depth 3";

    public static IReadOnlyList<ApplicationInventoryItem> Read(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("应用清单读取仅支持 Windows。");
        var candidates = ReadRegisteredExecutables().Take(MaximumCandidates).ToArray();
        var results = new List<ApplicationInventoryItem>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        foreach (var batch in candidates.Chunk(BatchSize))
        {
            deadline.Token.ThrowIfCancellationRequested();
            var appLockerRows = ReadAppLockerRows(batch.Select(item => item.Path).ToArray(), deadline.Token);
            foreach (var row in appLockerRows)
            {
                if (!TryResolveCandidate(row.Path, batch, out var candidate)) continue;
                try
                {
                    PathLinkSecurity.RejectLinks(candidate.Path);
                    var file = new FileInfo(candidate.Path);
                    if (!file.Exists || file.Length is <= 0 or > 1024L * 1024 * 1024) continue;
                    var publisher = ParsePublisher(row.Publisher);
                    var appLockerHash = ParseHash(row.Hash);
                    if (appLockerHash is null) continue;
                    using var stream = new FileStream(candidate.Path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                    var sha256 = Convert.ToHexString(SHA256.HashData(stream));
                    var binary = Path.GetFileName(candidate.Path);
                    var display = !string.IsNullOrWhiteSpace(candidate.DisplayName)
                        ? candidate.DisplayName
                        : publisher?.ProductName ?? Path.GetFileNameWithoutExtension(binary);
                    results.Add(new ApplicationInventoryItem(display, candidate.Path, binary,
                        publisher?.PublisherName, publisher?.ProductName, publisher?.Version,
                        sha256, appLockerHash, file.Length));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  System.Security.SecurityException or ArgumentException or CryptographicException)
                { }
            }
            if (results.Count >= ApplicationInventoryCryptography.MaximumItems) break;
        }
        return Array.AsReadOnly(results.GroupBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).Take(ApplicationInventoryCryptography.MaximumItems).ToArray());
    }

    private static IReadOnlyList<(string Path, string DisplayName)> ReadRegisteredExecutables()
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            AddUninstallTree(machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"));
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, view);
            foreach (var sid in users.GetSubKeyNames().Where(name => name.StartsWith("S-1-5-21-", StringComparison.Ordinal)))
            {
                using var user = users.OpenSubKey(sid + @"\Software\Microsoft\Windows\CurrentVersion\Uninstall");
                AddUninstallTree(user);
            }
        }
        return paths.Select(item => (item.Key, item.Value)).ToArray();

        void AddUninstallTree(RegistryKey? root)
        {
            if (root is null) return;
            using (root)
            foreach (var subKeyName in root.GetSubKeyNames())
            {
                using var subKey = root.OpenSubKey(subKeyName);
                if (subKey is null) continue;
                var displayName = (subKey.GetValue("DisplayName") as string)?.Trim();
                if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 160 || displayName.Any(char.IsControl)) continue;
                if (subKey.GetValue("DisplayIcon") is string icon && TryParseIconPath(icon, out var iconPath))
                    Add(iconPath, displayName);
                if (subKey.GetValue("InstallLocation") is string install && Path.IsPathFullyQualified(install))
                    AddInstallLocation(install, displayName);
            }
        }

        void AddInstallLocation(string directory, string displayName)
        {
            try
            {
                directory = Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"'));
                if (!Directory.Exists(directory)) return;
                PathLinkSecurity.RejectLinks(directory);
                foreach (var path in Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly)
                             .Take(8)) Add(path, displayName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              System.Security.SecurityException or ArgumentException or NotSupportedException)
            { }
        }

        void Add(string path, string displayName)
        {
            try
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim());
                if (!Path.IsPathFullyQualified(path) || path.Length > 400 ||
                    !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return;
                path = Path.GetFullPath(path);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
                paths.TryAdd(path, displayName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              System.Security.SecurityException or ArgumentException or NotSupportedException)
            { }
        }
    }

    private static bool TryParseIconPath(string icon, out string path)
    {
        path = "";
        icon = Environment.ExpandEnvironmentVariables(icon.Trim());
        if (icon.Length == 0) return false;
        if (icon[0] == '"')
        {
            var end = icon.IndexOf('"', 1);
            if (end < 0) return false;
            path = icon[1..end];
        }
        else
        {
            var suffix = Regex.Match(icon, @",-?[0-9]+$");
            path = suffix.Success ? icon[..suffix.Index] : icon;
            path = path.Trim();
        }
        return Path.IsPathFullyQualified(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<(string Path, string Publisher, string Hash)> ReadAppLockerRows(
        IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (paths.Count == 0) return [];
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(PowerShellScript)));
        start.Environment["VEYONCAMPUS_APP_PATHS"] = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(paths)));
        using var process = Process.Start(start) ?? throw new IOException("无法启动 AppLocker 文件信息读取器。");
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (output.Length > MaximumPowerShellOutputCharacters)
            throw new InvalidDataException("AppLocker 应用清单读取结果超过大小限制。");
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Windows 无法读取 AppLocker 文件信息：" +
                new string(error.Where(character => !char.IsControl(character)).Take(300).ToArray()));
        using var document = JsonDocument.Parse(output);
        var rows = new List<(string, string, string)>();
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object) AddRow(root);
        else if (root.ValueKind == JsonValueKind.Array)
            foreach (var item in root.EnumerateArray()) if (item.ValueKind == JsonValueKind.Object) AddRow(item);
        return rows;

        void AddRow(JsonElement item)
        {
            string Read(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : "";
            var path = Read("Path");
            var hash = Read("Hash");
            if (path.Length > 0 && hash.Length > 0) rows.Add((path, Read("Publisher"), hash));
        }
    }

    private static bool TryResolveCandidate(string reportedPath,
        IReadOnlyList<(string Path, string DisplayName)> candidates, out (string Path, string DisplayName) candidate)
    {
        var expanded = Environment.ExpandEnvironmentVariables(reportedPath);
        foreach (var item in candidates)
        {
            if (string.Equals(item.Path, expanded, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Path, expanded.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))
            { candidate = item; return true; }
        }
        candidate = default;
        return false;
    }

    private static (string PublisherName, string ProductName, string BinaryName, string Version)? ParsePublisher(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var firstSeparator = value.IndexOf('\\');
        var secondSeparator = firstSeparator < 0 ? -1 : value.IndexOf('\\', firstSeparator + 1);
        var versionSeparator = value.LastIndexOf(',');
        if (firstSeparator <= 0 || secondSeparator <= firstSeparator + 1 || versionSeparator <= secondSeparator + 1)
            return null;
        var publisher = value[..firstSeparator].Trim();
        var product = value[(firstSeparator + 1)..secondSeparator].Trim();
        var binaryAndVersion = value[(secondSeparator + 1)..];
        var binary = binaryAndVersion[..(versionSeparator - secondSeparator - 1)].Trim();
        var version = value[(versionSeparator + 1)..].Trim();
        if (publisher.Length is 0 or > 512 || product.Length is 0 or > 256 ||
            binary.Length is 0 or > 256 || !binary.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(version, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$"))
            return null;
        return (publisher, product, binary, version);
    }

    private static string? ParseHash(string value)
    {
        var match = Regex.Match(value, @"(?:^|\s)0x(?<hash>[A-Fa-f0-9]{64})(?:$|\s)");
        return match.Success ? match.Groups["hash"].Value.ToUpperInvariant() : null;
    }
}
