using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

/// <summary>
/// A signed teacher instruction that turns the student machine's own network switches on or off.
/// </summary>
/// <remarks>
/// Two independent switches are carried in one command because the teacher gestures that drive them
/// come from the same place, but they mean different things:
/// <list type="bullet">
/// <item><description><see cref="HardeningEnabled"/> hides the Settings page, the classic connection
/// UI and the Quick Settings flyout, so the ordinary account cannot switch the radio off at all.
/// It is a durable machine policy.</description></item>
/// <item><description><see cref="WifiEnabled"/> actually disables the wireless adapter. On an
/// all-in-one that only has Wi-Fi this also removes the teacher channel, so turning it off is always
/// time-boxed by <see cref="WifiRestoreSeconds"/>. The student Agent restores the radio on its own
/// clock even when it can no longer reach the teacher.</description></item>
/// </list>
/// A null field means "leave this switch as it is", which lets one button change one thing without
/// silently reverting the other.
/// </remarks>
public sealed record StudentNetworkControlCommand(
    int SchemaVersion,
    string Purpose,
    string CampusId,
    Guid Nonce,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc,
    bool? HardeningEnabled,
    bool? WifiEnabled,
    int WifiRestoreSeconds);

/// <summary>What the student Agent reports back after applying a network control command.</summary>
public sealed record StudentNetworkControlReceipt(
    int SchemaVersion,
    string CampusId,
    Guid CommandNonce,
    DateTimeOffset CompletedUtc,
    StudentNetworkControlOutcome Outcome,
    bool? HardeningEnabled,
    bool? WifiEnabled,
    DateTimeOffset? WifiRestoreUtc,
    string Detail);

/// <summary>Signed read-back so the teacher can see the switch state without issuing a change.</summary>
public sealed record StudentNetworkControlStatus(
    int SchemaVersion,
    string CampusId,
    Guid RequestNonce,
    DateTimeOffset CollectedUtc,
    bool Supported,
    bool HardeningEnforced,
    bool WifiAdapterPresent,
    bool? WifiEnabled,
    DateTimeOffset? WifiRestoreUtc,
    string Detail);

public enum StudentNetworkControlOutcome
{
    Succeeded = 0,
    Failed = 1,
    Unsupported = 2,
    ReplayRejected = 3,
    Unknown = 4
}

public static class StudentNetworkControlCommandCryptography
{
    public const string Purpose = "VeyonCampus.StudentNetworkControlCommand.v1";
    public const int MaximumEnvelopeBytes = 16 * 1024;
    public static readonly TimeSpan MaximumCommandLifetime = TimeSpan.FromMinutes(5);
    public const int MinimumRestoreSeconds = 60;
    public const int MaximumRestoreSeconds = 7200;
    public const int DefaultRestoreSeconds = 600;

    private static readonly JsonSerializerOptions JsonOptions = ApplicationPolicyCompiler.JsonOptions;

    public static string Sign(StudentNetworkControlCommand command, RSA teacherPrivateKey)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(teacherPrivateKey);
        Validate(command, command.CampusId, DateTimeOffset.UtcNow);
        var payload = JsonSerializer.SerializeToUtf8Bytes(command, JsonOptions);
        if (payload.Length > MaximumEnvelopeBytes / 2)
            throw new InvalidDataException("网络开关命令超过大小限制。");
        var signature = teacherPrivateKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var envelope = new SignedWebsitePolicy(Convert.ToBase64String(payload), Convert.ToBase64String(signature));
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumEnvelopeBytes)
            throw new InvalidDataException("网络开关命令超过大小限制。");
        return json;
    }

    public static StudentNetworkControlCommand Verify(string signedJson, string publicKeyPem,
        string expectedCampusId, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signedJson);
        if (Encoding.UTF8.GetByteCount(signedJson) > MaximumEnvelopeBytes)
            throw new InvalidDataException("网络开关命令超过大小限制。");
        try
        {
            var envelopeBytes = Encoding.UTF8.GetBytes(signedJson);
            PolicyJson.RejectDuplicateFields(envelopeBytes);
            var envelope = JsonSerializer.Deserialize<SignedWebsitePolicy>(envelopeBytes, JsonOptions)
                           ?? throw new InvalidDataException("网络开关命令为空。");
            var payload = Convert.FromBase64String(envelope.Payload);
            var signature = Convert.FromBase64String(envelope.Signature);
            if (payload.Length is 0 or > MaximumEnvelopeBytes / 2 || signature.Length is < 256 or > 512)
                throw new InvalidDataException("网络开关命令正文或签名大小无效。");
            using var publicKey = RSA.Create();
            publicKey.ImportFromPem(publicKeyPem);
            if (publicKey.KeySize is < 2048 or > 4096 ||
                !publicKey.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss))
                throw new InvalidDataException("网络开关命令签名无效。");
            PolicyJson.RejectDuplicateFields(payload);
            var command = JsonSerializer.Deserialize<StudentNetworkControlCommand>(payload, JsonOptions)
                          ?? throw new InvalidDataException("网络开关命令正文为空。");
            Validate(command, expectedCampusId, nowUtc);
            return command;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or
                                              CryptographicException or ArgumentException)
        {
            throw new InvalidDataException("网络开关命令格式、编码或签名无效。", exception);
        }
    }

    private static void Validate(StudentNetworkControlCommand command, string expectedCampusId,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(command);
        WebsitePolicySigningKeyStore.ValidateCampusId(expectedCampusId);
        var now = nowUtc.ToUniversalTime();
        if (command.SchemaVersion != 1 || command.Purpose != Purpose ||
            !string.Equals(command.CampusId, expectedCampusId, StringComparison.Ordinal) ||
            command.Nonce == Guid.Empty || command.IssuedUtc.Offset != TimeSpan.Zero ||
            command.ExpiresUtc.Offset != TimeSpan.Zero ||
            command.IssuedUtc > now.AddMinutes(1) ||
            command.IssuedUtc < now.Subtract(MaximumCommandLifetime) ||
            command.ExpiresUtc <= command.IssuedUtc ||
            command.ExpiresUtc > command.IssuedUtc.Add(MaximumCommandLifetime * 2))
            throw new InvalidDataException("网络开关命令校区、随机数或期限无效。");
        if (command.HardeningEnabled is null && command.WifiEnabled is null)
            throw new InvalidDataException("网络开关命令没有指定任何开关。");
        if (command.WifiEnabled is false &&
            command.WifiRestoreSeconds is < MinimumRestoreSeconds or > MaximumRestoreSeconds)
            throw new InvalidDataException(
                $"关闭 WiFi 必须带 {MinimumRestoreSeconds}-{MaximumRestoreSeconds} 秒的自动恢复时限。");
        if (command.WifiEnabled is not false && command.WifiRestoreSeconds != 0)
            throw new InvalidDataException("只有关闭 WiFi 时才需要自动恢复时限。");
    }
}

/// <summary>
/// Persists the teacher's network switches and applies them, including after a reboot.
/// </summary>
/// <remarks>
/// The file lives beside the Agent configuration inside the already-private WebsitePolicy directory,
/// so a standard account cannot rewrite the intended state.
///
/// Reboots always restore the radio. A machine whose only uplink is Wi-Fi cannot be recovered
/// remotely once the adapter is down, so a crash, a power cut or a deliberate restart must never
/// leave it unreachable: the durable part of a Wi-Fi hold is the deadline, not the outage.
/// </remarks>
public sealed class StudentNetworkControlState
{
    private const int StoredSchemaVersion = 1;
    private const string FileName = "network-control.json";
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private sealed record DesiredDocument(int SchemaVersion, bool? HardeningEnabled, bool? WifiEnabled,
        DateTimeOffset? WifiRestoreUtc, string? WifiAdapterName, DateTimeOffset UpdatedUtc);

    private readonly string _path;

    public StudentNetworkControlState(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath))
                        ?? throw new InvalidDataException("学生 Agent 配置目录无效。");
        _path = Path.Combine(directory, FileName);
    }

    /// <summary>Applies one verified command and returns what the machine now enforces.</summary>
    public StudentNetworkControlReceipt Apply(StudentNetworkControlCommand command, string configPath,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (Gate)
        {
            if (command.ExpiresUtc.ToUniversalTime() < nowUtc.ToUniversalTime())
                return Receipt(command, nowUtc, StudentNetworkControlOutcome.ReplayRejected,
                    "网络开关命令已过期；没有修改任何设置。");

            var current = Read();
            var hardening = command.HardeningEnabled ?? current?.HardeningEnabled;
            var wifi = command.WifiEnabled ?? current?.WifiEnabled;
            var restoreUtc = current?.WifiRestoreUtc;
            var adapterName = current?.WifiAdapterName;
            string? deferDisable = null;
            var detail = new List<string>();

            if (command.WifiEnabled is false)
            {
                restoreUtc = nowUtc.ToUniversalTime()
                    .AddSeconds(command.WifiRestoreSeconds);
            }
            else if (command.WifiEnabled is true)
            {
                restoreUtc = null;
            }

            if (command.HardeningEnabled is true)
            {
                var applied = NetworkPolicyHardening.Apply();
                if (applied.Failure is { } failure)
                    return Receipt(command, nowUtc, StudentNetworkControlOutcome.Failed,
                        "禁止修改网络设置失败：" + failure);
                detail.Add(applied.Changes.Count == 0 ? "网络设置已处于禁止修改状态。" : "已禁止学生修改网络设置。");
            }
            else if (command.HardeningEnabled is false)
            {
                var reverted = NetworkPolicyHardening.Revert();
                if (reverted.Failure is { } failure)
                    return Receipt(command, nowUtc, StudentNetworkControlOutcome.Failed,
                        "恢复网络设置失败：" + failure);
                detail.Add("已恢复学生的网络设置入口。");
            }

            if (command.WifiEnabled is { } wanted)
            {
                var radio = StudentWifiRadio.FindAdapterName(adapterName);
                if (radio is null)
                {
                    if (wanted)
                    {
                        detail.Add("本机没有检测到无线网卡；无需恢复。");
                        wifi = null;
                        restoreUtc = null;
                    }
                    else
                    {
                        return Receipt(command, nowUtc, StudentNetworkControlOutcome.Failed,
                            "本机没有检测到无线网卡，无法关闭 WiFi。");
                    }
                }
                else
                {
                    adapterName = radio;
                    if (wanted)
                    {
                        var switched = StudentWifiRadio.SetEnabled(radio, true);
                        if (!switched.Ok)
                            return Receipt(command, nowUtc, StudentNetworkControlOutcome.Failed,
                                "恢复 WiFi 失败：" + switched.Detail);
                        // Turning the radio back on means the hold is over, so the deadline is cleared.
                        wifi = true;
                        restoreUtc = null;
                        detail.Add("已恢复 WiFi。");
                    }
                    else
                    {
                        // Disabling the adapter takes the teacher channel down with it, so the switch
                        // is deferred a couple of seconds: the signed receipt has to reach the teacher,
                        // otherwise a command that worked looks like one that failed.
                        wifi = false;
                        deferDisable = radio;
                        detail.Add(
                            $"已安排关闭 WiFi，{command.WifiRestoreSeconds / 60.0:0.#} 分钟后自动恢复。");
                    }
                }
            }

            var document = new DesiredDocument(StoredSchemaVersion, hardening, wifi, restoreUtc,
                adapterName, nowUtc.ToUniversalTime());
            try
            {
                Write(document);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  System.Security.SecurityException)
            {
                return Receipt(command, nowUtc, StudentNetworkControlOutcome.Failed,
                    "网络开关已执行，但无法保存为持久状态：" + exception.Message);
            }

            Report(configPath, string.Join(" ", detail));
            if (deferDisable is { } adapter)
                ScheduleWifiDisable(adapter, configPath);
            return new StudentNetworkControlReceipt(1, command.CampusId, command.Nonce,
                nowUtc.ToUniversalTime(), StudentNetworkControlOutcome.Succeeded, hardening, wifi,
                restoreUtc, detail.Count == 0 ? "没有需要修改的开关。" : string.Join(" ", detail));
        }
    }

    /// <summary>Reads the current switch state for the teacher's status panel.</summary>
    public StudentNetworkControlStatus ReadStatus(string campusId, Guid requestNonce, DateTimeOffset nowUtc)
    {
        var current = Read();
        var adapterName = current?.WifiAdapterName ?? StudentWifiRadio.FindAdapterName(null);
        var enabled = adapterName is null ? (bool?)null : StudentWifiRadio.IsEnabled(adapterName);
        var hardening = NetworkPolicyHardening.ReadSnapshot().FullyEnforced;
        return new StudentNetworkControlStatus(1, campusId, requestNonce, nowUtc.ToUniversalTime(),
            true, hardening, adapterName is not null, enabled, current?.WifiRestoreUtc,
            hardening ? "已禁止学生修改网络设置。" : "学生仍可修改网络设置。");
    }

    /// <summary>Whether the durable hardening policy should currently be applied.</summary>
    public bool HardeningWanted(bool configDefault) => Read()?.HardeningEnabled ?? configDefault;

    /// <summary>
    /// True while the teacher deliberately holds the radio down. <see cref="NetworkGuardian"/> reads
    /// this so an intentional Wi-Fi cut is not mistaken for a student disconnecting to escape.
    /// </summary>
    public bool IsWifiHoldActive(DateTimeOffset nowUtc)
    {
        var current = Read();
        return current is { WifiEnabled: false } && current.WifiRestoreUtc is { } restore &&
               restore.ToUniversalTime() > nowUtc.ToUniversalTime();
    }

    /// <summary>Restores the durable switches when the Agent starts.</summary>
    public void ApplyPersistedAtStartup(string configPath)
    {
        try
        {
            var current = Read();
            if (current is not { WifiEnabled: false }) return;

            // A reboot always brings the uplink back: an unreachable all-in-one cannot be fixed by
            // the teacher, so losing the radio across a restart would end the classroom.
            var adapterName = StudentWifiRadio.FindAdapterName(current.WifiAdapterName);
            if (adapterName is null) return;
            var switched = StudentWifiRadio.SetEnabled(adapterName, enabled: true);
            if (!switched.Ok)
            {
                Report(configPath, "重启恢复 WiFi 失败：" + switched.Detail);
                return;
            }
            Write(current with { WifiEnabled = true, WifiRestoreUtc = null,
                UpdatedUtc = DateTimeOffset.UtcNow });
            Report(configPath, "重启后已自动恢复 WiFi（避免设备失联）。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              System.Security.SecurityException or InvalidDataException or
                                              PlatformNotSupportedException or NotSupportedException)
        {
            Report(configPath, "处理持久化网络开关失败：" + exception.Message);
        }
    }

    /// <summary>Periodic pass: restore an expired Wi-Fi hold and re-apply hardening.</summary>
    public void Sweep(string configPath, DateTimeOffset nowUtc, bool configDefault)
    {
        try
        {
            var current = Read();
            if (current is { WifiEnabled: false, WifiRestoreUtc: { } restore } &&
                restore.ToUniversalTime() <= nowUtc.ToUniversalTime())
            {
                var adapterName = StudentWifiRadio.FindAdapterName(current.WifiAdapterName);
                if (adapterName is not null && StudentWifiRadio.SetEnabled(adapterName, true).Ok)
                {
                    Write(current with { WifiEnabled = true, WifiRestoreUtc = null,
                        UpdatedUtc = nowUtc.ToUniversalTime() });
                    Report(configPath, "关闭 WiFi 的时限已到，已自动恢复。");
                }
                else
                {
                    Report(configPath, "关闭 WiFi 的时限已到，但恢复无线网卡失败；下次复核会重试。");
                }
            }

            if (HardeningWanted(configDefault) && !NetworkPolicyHardening.ReadSnapshot().FullyEnforced)
            {
                var applied = NetworkPolicyHardening.Apply();
                if (applied.Failure is { } failure)
                    Report(configPath, failure);
                else if (applied.Changes.Count > 0)
                    Report(configPath, "已重新加固普通账户网络开关：" + string.Join(" ", applied.Changes));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              System.Security.SecurityException or InvalidDataException or
                                              PlatformNotSupportedException or NotSupportedException or
                                              InvalidOperationException)
        {
            Report(configPath, "网络开关复核失败：" + exception.Message);
        }
    }

    private StudentNetworkControlReceipt Receipt(StudentNetworkControlCommand command,
        DateTimeOffset nowUtc, StudentNetworkControlOutcome outcome, string detail) =>
        new(1, command.CampusId, command.Nonce, nowUtc.ToUniversalTime(), outcome, null, null, null,
            detail);

    /// <summary>
    /// Turns the adapter off a couple of seconds after the receipt is written. A fire-and-forget
    /// task is deliberate: the alternative is losing the acknowledgement every time, and the timer
    /// that restores the radio lives in <see cref="Sweep"/> rather than in this task.
    /// </summary>
    private static void ScheduleWifiDisable(string adapterName, string configPath) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var switched = StudentWifiRadio.SetEnabled(adapterName, enabled: false);
                if (!switched.Ok) Report(configPath, "关闭 WiFi 失败：" + switched.Detail);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or
                                                  UnauthorizedAccessException or
                                                  System.ComponentModel.Win32Exception or
                                                  System.Security.SecurityException)
            {
                Report(configPath, "关闭 WiFi 失败：" + exception.Message);
            }
        });

    private DesiredDocument? Read()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var bytes = File.ReadAllBytes(_path);
            if (bytes.Length is 0 or > 8192) return null;
            var document = JsonSerializer.Deserialize<DesiredDocument>(bytes, JsonOptions);
            return document is { SchemaVersion: StoredSchemaVersion } ? document : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              JsonException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private void Write(DesiredDocument document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        try { AgentFileSecurity.SecurePrivateFile(temporary); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or
                                              System.Security.SecurityException or IOException) { }
        File.Move(temporary, _path, overwrite: true);
    }

    private static void Report(string configPath, string message)
    {
        try { WebsitePolicyAgentInstaller.ReportAgentOperationNote(configPath, "network-control", message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              System.Security.SecurityException or InvalidOperationException) { }
    }
}

/// <summary>Enables or disables the wireless adapter, the only switch that really cuts an all-in-one off.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class StudentWifiRadio
{
    public static string? FindAdapterName(string? preferred)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .Select(adapter => adapter.Name)
                .ToArray();
            if (adapters.Length == 0) return null;
            if (preferred is not null &&
                adapters.Contains(preferred, StringComparer.OrdinalIgnoreCase))
                return preferred;
            return adapters[0];
        }
        catch (NetworkInformationException) { return null; }
    }

    public static bool? IsEnabled(string? adapterName)
    {
        if (!OperatingSystem.IsWindows() || adapterName is null) return null;
        var output = RunNetsh("interface show interface");
        if (output.ExitCode != 0 || string.IsNullOrWhiteSpace(output.Output)) return null;
        var line = output.Output.Split('\n')
            .Select(text => text.TrimEnd('\r', ' '))
            .FirstOrDefault(text => text.EndsWith(adapterName, StringComparison.OrdinalIgnoreCase));
        if (line is null) return null;
        var head = line[..^adapterName.Length];
        // "Admin State" column: Enabled / Disabled, or 已启用 / 已禁用 in a Chinese UI.
        if (head.Contains("Disabled", StringComparison.OrdinalIgnoreCase) || head.Contains("已禁用") ||
            head.Contains("已停用"))
            return false;
        if (head.Contains("Enabled", StringComparison.OrdinalIgnoreCase) || head.Contains("已启用"))
            return true;
        return null;
    }

    public static (bool Ok, string Detail) SetEnabled(string adapterName, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterName);
        var verb = enabled ? "ENABLED" : "DISABLED";
        var result = RunNetsh($"interface set interface name=\"{adapterName}\" admin={verb}");
        if (result.ExitCode != 0)
            return (false, string.IsNullOrWhiteSpace(result.Output) ? "netsh 返回失败。" : result.Output);

        var wanted = enabled;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var current = IsEnabled(adapterName);
            if (current == wanted) return (true, "已切换无线网卡。");
            Thread.Sleep(400);
        }
        // netsh returned success; the read-back is only an extra check, so do not fail the command on
        // a UI-language mismatch in the status column.
        return (true, "已发送切换命令，读回状态待下次复核确认。");
    }

    private static (int ExitCode, string Output) RunNetsh(string arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("netsh", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null) return (-1, "无法启动 netsh。");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(15));
        return (process.HasExited ? process.ExitCode : -1, output.Trim());
    }
}
