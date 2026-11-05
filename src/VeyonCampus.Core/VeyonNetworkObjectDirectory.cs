using System.Collections.ObjectModel;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace VeyonCampus.Core;

public sealed record VeyonNetworkComputer(string ComputerName, string Host, string? StudentName = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(StudentName)
        ? ComputerName
        : StudentName.Trim();
}

public sealed record VeyonNetworkObject(string Type, string Name, string Host, string Mac, string Location,
    Guid ObjectId = default, Guid ParentObjectId = default);
/// <summary>Outcome of deleting exactly one Veyon computer object by UUID.</summary>
public sealed record VeyonComputerRemovalResult(string ComputerName, string HostAddress,
    string Location, int RemainingComputerCount);
public sealed record VeyonDirectorySnapshot(string ConfigFingerprint,
    IReadOnlyList<VeyonNetworkObject> Objects);
internal sealed record VeyonConfigurationExport(string CliPath, string WorkingDirectory, byte[] Bytes);
/// <summary>One existing Veyon computer offered as a safe association target for a planned addition.</summary>
public sealed record VeyonExistingComputerMatch(string ComputerName, string Host, Guid ObjectId,
    Guid ParentObjectId, string Location);
public sealed record VeyonAddedComputerTarget(string ComputerName, string Host, Guid ObjectId,
    Guid ParentObjectId, string Location);
public sealed record VeyonLocationImportResult(string LocationName, int ComputerCount, int NamedStudentCount,
    int AddedComputerCount, int SkippedComputerCount, bool LocationCreated,
    IReadOnlyList<VeyonExistingComputerMatch>? AssociatedExisting = null,
    IReadOnlyList<VeyonAddedComputerTarget>? AddedComputerTargets = null)
{
    public VeyonLocationImportResult(string locationName, int computerCount, int namedStudentCount,
        int addedComputerCount, int skippedComputerCount, bool locationCreated)
        : this(locationName, computerCount, namedStudentCount, addedComputerCount,
            skippedComputerCount, locationCreated, Array.Empty<VeyonExistingComputerMatch>(),
            Array.Empty<VeyonAddedComputerTarget>())
    {
    }
}
public sealed record VeyonNetworkImportPreview(string LocationName, bool LocationExists,
    IReadOnlyList<VeyonNetworkComputer> ComputersToAdd, IReadOnlyList<string> SkippedComputers,
    IReadOnlyList<string> Conflicts, IReadOnlyList<VeyonExistingComputerMatch>? ExistingMatches = null)
{
    public VeyonNetworkImportPreview(string locationName, bool locationExists,
        IReadOnlyList<VeyonNetworkComputer> computersToAdd, IReadOnlyList<string> skippedComputers,
        IReadOnlyList<string> conflicts)
        : this(locationName, locationExists, computersToAdd, skippedComputers, conflicts,
            Array.Empty<VeyonExistingComputerMatch>())
    {
    }
}

/// <summary>Reads the local Veyon built-in network object directory without changing it.</summary>
public static class VeyonNetworkObjectDirectory
{
    /// <summary>Last successful read-only directory read in this process, used to show stale data honestly.</summary>
    internal static DateTimeOffset LastSuccessfulReadUtc { get; private set; }
    public static VeyonDirectoryReadDiagnostic? LastReadDiagnostic { get; private set; }
    public static string? LastTrustedDirectoryFingerprint { get; private set; }
    /// <summary>Why the last read fell back to the Veyon registry snapshot, or null when the CLI worked.</summary>
    public static string? LastRegistrySnapshotDetail { get; private set; }
    private const string RegistrySettingsSubKey = @"SOFTWARE\Veyon Solutions\Veyon\BuiltinDirectory";
    private const string RegistryNetworkObjectsValue = "NetworkObjects";
    // Veyon stores complex settings as "@@JsonValue(<base64 utf8 json>)" inside a single registry value.
    private const string JsonValueMarker = "@@JsonValue(";
    // Veyon's 4.11.2 format importer is a placeholder-to-regex parser, not an RFC CSV decoder.
    // Unit Separator cannot occur in accepted fields (control characters are rejected), so
    // semicolons, quotes and Chinese text remain unambiguous without CSV quote escaping.
    private const char FieldSeparator = '\u001f';
    private const string ExportFormat = "%name%\u001f%host%\u001f%location%";
    private const string FullExportFormat = "%type%\u001f%name%\u001f%host%\u001f%mac%\u001f%location%";
    private const string ImportFormat = "%name%\u001f%host%\u001f%mac%";
    private const int VeyonObjectTypeNone = 0;
    private const int VeyonObjectTypeRoot = 1;
    private const int VeyonObjectTypeLocation = 2;
    private const int VeyonObjectTypeHost = 3;
    private const int VeyonObjectTypeLabel = 4;
    private const int VeyonObjectTypeDesktopGroup = 5;

    /// <summary>Formats one safe custom-delimited computer row for Veyon's placeholder importer.</summary>
    public static string FormatComputerImportRecord(VeyonNetworkComputer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        ValidateDirectoryName(computer.DisplayName, "电脑显示名");
        _ = VeyonHostAddress.NormalizeOverride(computer.Host);
        return string.Join(FieldSeparator.ToString(), computer.DisplayName, computer.Host, "");
    }

    /// <summary>Parses a row written with the custom delimiter used by Veyon's export format.</summary>
    public static IReadOnlyList<string> ParseExportRecord(string line, int expectedFieldCount = 3) =>
        Array.AsReadOnly(ParseExportRow(line, expectedFieldCount));

    /// <summary>
    /// True when <c>veyon-cli</c> can actually start and list the directory. Reading falls back to
    /// the registry snapshot when the CLI is broken, but every write still needs the CLI - callers
    /// must probe this before pausing a lesson or promising a write.
    /// </summary>
    public static bool IsCliWritable()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var cliPath = ResolveCliPathOrThrow("cli probe");
            var runner = new ProcessRunner();
            runner.Run(cliPath, ["networkobjects", "dump"], Path.GetDirectoryName(cliPath)!,
                TimeSpan.FromSeconds(30), outputLimitChars: 4096, redirectedOutputEncoding: Encoding.UTF8);
            return runner.ExitCode is 0;
        }
        catch (Exception exception) when (exception is TimeoutException or UnauthorizedAccessException or
                                          System.ComponentModel.Win32Exception or IOException or
                                          InvalidOperationException or PlatformNotSupportedException or
                                          ArgumentException)
        {
            return false;
        }
    }

    public static IReadOnlyList<VeyonNetworkLocation> ReadLocations() =>
        ReadLocations(TimeSpan.FromSeconds(45));

    /// <summary>Returns a stable target-membership fingerprint for comparing two directory reads.</summary>
    public static string FingerprintLocations(IEnumerable<VeyonNetworkLocation> locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        return ComputeLocationFingerprint(locations);
    }

    /// <summary>Returns the target-membership fingerprint represented by a complete UUID object snapshot.</summary>
    public static string FingerprintObjects(IEnumerable<VeyonNetworkObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        return ComputeLocationFingerprint(objects);
    }

    public static IReadOnlyList<VeyonNetworkLocation> ReadLocations(TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("读取 Veyon 机房列表仅支持 Windows 教师端。");
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var cliPath = ResolveCliPathOrThrow("networkobjects export");
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"veyon-campus-targets-{Guid.NewGuid():N}.csv");
        var runner = new ProcessRunner();
        try
        {
            try
            {
                runner.Run(cliPath,
                    ["networkobjects", "export", temporaryPath, "format", ExportFormat],
                    Path.GetDirectoryName(cliPath)!, timeout, outputLimitChars: 1024);
            }
            catch (Exception exception) when (exception is TimeoutException or UnauthorizedAccessException or
                                              System.ComponentModel.Win32Exception or IOException or
                                              InvalidOperationException or ArgumentException)
            {
                ThrowWithDiagnostic(VeyonDirectoryReadDiagnostic.For(
                    exception switch
                    {
                        TimeoutException => VeyonDirectoryReadDiagnostic.Timeout,
                        UnauthorizedAccessException => VeyonDirectoryReadDiagnostic.AccessDenied,
                        _ => VeyonDirectoryReadDiagnostic.CliFailed,
                    },
                    exception.Message, cliPath, null, ProcessDetail(runner), "networkobjects export",
                    exception is TimeoutException), exception);
                return [];
            }
            if (runner.ExitCode is not 0)
            {
                // `networkobjects export` can fail (observed exit code 255) while the trusted
                // `config export` snapshot still works; use that single read for display only.
                var exportFailure = VeyonDirectoryReadDiagnostic.For(
                    VeyonDirectoryReadDiagnostic.CliFailed,
                    $"Veyon 导出电脑目录失败（退出码 {runner.ExitCode?.ToString() ?? "未知"}）。",
                    cliPath, runner.ExitCode, ProcessDetail(runner), "networkobjects export",
                    runner.TimedOut);
                var fallback = TryReadLocationsFromFallbackRead(cliPath,
                    Path.GetDirectoryName(cliPath)!);
                if (fallback.Locations is { } fallbackLocations)
                {
                    // Only a partial fallback is untrusted for write-back; a complete registry
                    // snapshot carries the same UUIDs, so keep its fingerprint.
                    LastTrustedDirectoryFingerprint =
                        fallback.Diagnostic.FailureStage is VeyonDirectoryReadDiagnostic.None
                            ? ComputeLocationFingerprint(fallbackLocations)
                            : null;
                    LastReadDiagnostic = CreateFallbackSuccessDiagnostic(exportFailure, fallback.Diagnostic);
                    return fallbackLocations;
                }
                ThrowWithDiagnostic(CombineFallbackFailure(exportFailure, fallback.Diagnostic), null);
                return [];
            }
            if (!File.Exists(temporaryPath))
            {
                var exportFailure = VeyonDirectoryReadDiagnostic.For(
                    VeyonDirectoryReadDiagnostic.ExportFileMissing,
                    "Veyon 没有生成电脑目录导出文件。", cliPath, runner.ExitCode,
                    ProcessDetail(runner), "networkobjects export", runner.TimedOut);
                var fallback = TryReadLocationsFromFallbackRead(cliPath,
                    Path.GetDirectoryName(cliPath)!);
                if (fallback.Locations is { } fallbackLocations)
                {
                    LastTrustedDirectoryFingerprint =
                        fallback.Diagnostic.FailureStage is VeyonDirectoryReadDiagnostic.None
                            ? ComputeLocationFingerprint(fallbackLocations)
                            : null;
                    LastReadDiagnostic = CreateFallbackSuccessDiagnostic(exportFailure, fallback.Diagnostic);
                    return fallbackLocations;
                }
                ThrowWithDiagnostic(CombineFallbackFailure(exportFailure, fallback.Diagnostic), null);
                return [];
            }
            try
            {
                var locations = ParseLocationsExport(File.ReadLines(temporaryPath, Encoding.UTF8));
                LastSuccessfulReadUtc = DateTimeOffset.UtcNow;
                LastTrustedDirectoryFingerprint = ComputeLocationFingerprint(locations);
                LastReadDiagnostic = VeyonDirectoryReadDiagnostic.Success(cliPath, "networkobjects export");
                return locations;
            }
            catch (InvalidDataException exception)
            {
                ThrowWithDiagnostic(VeyonDirectoryReadDiagnostic.For(
                    VeyonDirectoryReadDiagnostic.ParseFailed, exception.Message, cliPath,
                    runner.ExitCode, ProcessDetail(runner), "networkobjects export", false), exception);
                return [];
            }
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Last resort: rebuild the display-only locations from one verified directory read.</summary>
    private static (IReadOnlyList<VeyonNetworkLocation>? Locations,
        VeyonDirectoryReadDiagnostic Diagnostic) TryReadLocationsFromFallbackRead(
        string cliPath, string workingDirectory)
    {
        var read = SafeReadNetworkObjects(cliPath, workingDirectory);
        if (read.Result is not { } objects) return (null, read.Diagnostic);
        ParseLocationsFromSnapshot(objects, out var locations, out var error);
        if (error is not null)
            return (null, VeyonDirectoryReadDiagnostic.For(
                VeyonDirectoryReadDiagnostic.ParseFailed,
                "Veyon 目录快照无法转换为地点列表：" + error, cliPath, null, error,
                "config export", false));
        return (locations, read.Diagnostic);
    }

    private static VeyonDirectoryReadDiagnostic CombineFallbackFailure(
        VeyonDirectoryReadDiagnostic original, VeyonDirectoryReadDiagnostic fallback)
    {
        var detail = string.Join(" | ", new[] { original.Detail, fallback.Detail }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (detail.Length > 2000) detail = detail[..2000];
        return original with
        {
            Message = original.Message + " 只读回退失败：" + fallback.Message,
            Detail = detail
        };
    }

    private static VeyonDirectoryReadDiagnostic CreateFallbackSuccessDiagnostic(
        VeyonDirectoryReadDiagnostic original, VeyonDirectoryReadDiagnostic fallback)
    {
        var detail = string.Join(" | ", new[] { original.Detail, fallback.Detail }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (detail.Length > 2000) detail = detail[..2000];
        if (fallback.FailureStage is VeyonDirectoryReadDiagnostic.None)
        {
            // The fallback produced a complete directory (the registry snapshot is Veyon's own
            // store), so keep the read trustworthy instead of flagging it as partial.
            return VeyonDirectoryReadDiagnostic.For(VeyonDirectoryReadDiagnostic.None, "",
                original.CliPath, 0, detail,
                original.SubCommand + " → " + fallback.SubCommand, false,
                fallback.TrustedSnapshotFingerprint) with
            {
                LastSuccessfulReadUtc = LastSuccessfulReadUtc
            };
        }
        return VeyonDirectoryReadDiagnostic.For(
            VeyonDirectoryReadDiagnostic.PartialRead,
            original.Message + " 已使用只读目录回退结果；写入操作仍要求完整 UUID 读回。",
            original.CliPath, original.ExitCode, detail,
            original.SubCommand + " → " + fallback.SubCommand,
            original.TimedOut, fallback.TrustedSnapshotFingerprint) with
        {
            LastSuccessfulReadUtc = LastSuccessfulReadUtc
        };
    }

    private static void ParseLocationsFromSnapshot(IReadOnlyList<VeyonNetworkObject> objects,
        out IReadOnlyList<VeyonNetworkLocation> locations, out string? error)
    {
        error = null;
        try
        {
            var duplicateTarget = objects
                .Where(entry => entry.Type.Equals("computer", StringComparison.OrdinalIgnoreCase))
                .Select(entry => (
                    Location: entry.Location.Length > 0 ? entry.Location : "未分组电脑",
                    Target: entry.Host.Length > 0 ? entry.Host : entry.Name.Trim()))
                .Where(entry => entry.Target.Length > 0)
                .GroupBy(entry => entry.Location + "\u001f" + entry.Target,
                    StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateTarget is not null)
                throw new InvalidDataException("Veyon 配置快照在同一地点含有重复 Host，不能安全生成学生名单。");

            var byLocation = new Dictionary<string, List<(string Target, string DisplayName)>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var entry in objects)
            {
                if (!entry.Type.Equals("computer", StringComparison.OrdinalIgnoreCase)) continue;
                var computerName = entry.Name.Trim();
                var target = entry.Host.Length > 0 ? entry.Host : computerName;
                if (target.Length == 0) continue;
                if (!byLocation.TryGetValue(entry.Location.Length > 0 ? entry.Location : "未分组电脑",
                        out var targets))
                    byLocation.Add(entry.Location.Length > 0 ? entry.Location : "未分组电脑",
                        targets = []);
                targets.Add((target, computerName));
            }
            var built = byLocation
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair =>
                {
                    var computers = pair.Value.ToArray();
                    var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var computer in computers)
                        if (computer.DisplayName.Length > 0)
                            names[computer.Target] = computer.DisplayName;
                    return new VeyonNetworkLocation(pair.Key,
                        Array.AsReadOnly(computers.Select(computer => computer.Target).ToArray()),
                        new ReadOnlyDictionary<string, string>(names));
                })
                .Where(location => location.Targets.Count > 0)
                .ToArray();
            locations = Array.AsReadOnly(built);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or
                                          FormatException or OverflowException)
        {
            locations = [];
            error = exception.Message;
        }
    }

    /// <summary>
    /// Removes exactly one Veyon computer object by UUID. Verifies before and after the write;
    /// never removes a location, another computer, or an object identified only by display name.
    /// </summary>
    public static VeyonComputerRemovalResult RemoveComputer(Guid objectId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("只有 Windows 教师端可以修改 Veyon 目录。");
        if (objectId == Guid.Empty)
            throw new InvalidOperationException("缺少有效的 Veyon 电脑 UUID；没有删除任何内容。");

        using var mutationGate = VeyonDirectoryMutationGate.Acquire();
        var cliPath = ResolveCliPathOrThrow("networkobjects remove");
        var workingDirectory = Path.GetDirectoryName(cliPath) ??
            throw new InvalidOperationException("无法确定 Veyon CLI 工作目录。");

        var before = ReadDirectorySnapshot();
        var matches = before.Objects.Where(item =>
            item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) &&
            item.ObjectId == objectId).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("Veyon 目录里找不到唯一匹配的电脑对象；没有删除任何内容。");
        var target = matches[0];
        var beforeIds = before.Objects.Where(item =>
            item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.ObjectId).ToHashSet();

        var runner = new ProcessRunner();
        runner.Run(cliPath, ["networkobjects", "remove", objectId.ToString("D")],
            workingDirectory, TimeSpan.FromSeconds(45), outputLimitChars: 4096);
        if (runner.ExitCode is not 0)
            throw new InvalidOperationException(
                $"Veyon 删除电脑失败（退出码 {runner.ExitCode?.ToString() ?? "未知"}）：{ProcessDetail(runner)}");

        var after = ReadDirectorySnapshot();
        if (after.Objects.Any(item => item.ObjectId == objectId))
            throw new InvalidOperationException("删除后仍能读到该电脑 UUID；请核对 Veyon 目录，教师端不会重复删除。");
        var afterIds = after.Objects.Where(item =>
            item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.ObjectId).ToHashSet();
        if (afterIds.Count != beforeIds.Count - 1 || afterIds.Any(id => !beforeIds.Contains(id)))
            throw new InvalidOperationException("删除后的 Veyon 电脑名单与预期不一致；请导出配置备份并核对目录。");

        LastReadDiagnostic = VeyonDirectoryReadDiagnostic.Success(cliPath, "networkobjects remove",
            after.ConfigFingerprint);
        return new VeyonComputerRemovalResult(target.Name, target.Host, target.Location, afterIds.Count);
    }

    /// <summary>Reads Veyon objects with their stable UUIDs and a fingerprint of the exact config export.</summary>
    public static VeyonDirectorySnapshot ReadDirectorySnapshot()
    {
        var export = ExportConfiguration();
        try
        {
            var hash = SHA256.HashData(export.Bytes);
            var fingerprint = Convert.ToHexString(hash).ToLowerInvariant();
            CryptographicOperations.ZeroMemory(hash);
            var json = DecodeConfiguration(export.Bytes);
            var objects = ParseConfigurationDirectory(json);
            var snapshot = new VeyonDirectorySnapshot(fingerprint, objects);
            LastSuccessfulReadUtc = DateTimeOffset.UtcNow;
            LastTrustedDirectoryFingerprint = ComputeLocationFingerprint(objects);
            LastReadDiagnostic = VeyonDirectoryReadDiagnostic.Success(export.CliPath, "config export", fingerprint);
            return snapshot;
        }
        finally { CryptographicOperations.ZeroMemory(export.Bytes); }
    }

    private static string ResolveCliPathOrThrow(string subCommand)
    {
        var cliPath = WindowsVeyonAdapter.ResolveVeyonCliPath();
        if (cliPath is not null) return cliPath;
        ThrowWithDiagnostic(VeyonDirectoryReadDiagnostic.For(
            VeyonDirectoryReadDiagnostic.CliNotFound,
            "找不到 Veyon CLI；请先安装教师端 Veyon。", "", null, "", subCommand, false), null);
        return "";
    }

    /// <summary>Builds the UI location projection from the same complete UUID object snapshot.</summary>
    public static IReadOnlyList<VeyonNetworkLocation> ParseLocationsFromObjects(
        IReadOnlyList<VeyonNetworkObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ParseLocationsFromSnapshot(objects, out var locations, out var error);
        if (error is not null) throw new InvalidDataException(error);
        return locations;
    }

    /// <summary>Reads stable object IDs through Veyon's directory command, without exporting full configuration.</summary>
    public static IReadOnlyList<VeyonNetworkObject> ReadNetworkObjects()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("读取 Veyon 电脑目录仅支持 Windows 教师端。");
        var cliPath = ResolveCliPathOrThrow("networkobjects dump");
        return ReadNetworkObjects(cliPath, Path.GetDirectoryName(cliPath)!);
    }

    private static volatile int _lastIdenticalDuplicateObjectCount;

    /// <summary>
    /// Number of identical duplicate rows collapsed by the most recent ParseNetworkObjectsDump call.
    /// Veyon 4.x can persist the very same location object twice in JsonStoreArray; the
    /// config-export reader already de-duplicates such entries, so the dump reader does the same.
    /// Entries sharing a UUID but differing in content are still rejected as a real conflict.
    /// </summary>
    public static int LastIdenticalDuplicateObjectCount => _lastIdenticalDuplicateObjectCount;

    /// <summary>Parses the bordered table printed by `veyon-cli networkobjects dump`.</summary>
    public static IReadOnlyList<VeyonNetworkObject> ParseNetworkObjectsDump(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _lastIdenticalDuplicateObjectCount = 0;
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var rulerIndex = Array.FindIndex(lines, IsNetworkObjectsRuler);
        if (rulerIndex < 0)
            throw new InvalidDataException("Veyon 电脑目录清单格式无法识别。");

        var boundaries = GetNetworkObjectsColumnBoundaries(lines[rulerIndex]);
        if (boundaries.Length != 7 || rulerIndex + 2 >= lines.Length ||
            !IsNetworkObjectsRow(lines[rulerIndex + 1], boundaries) ||
            !IsNetworkObjectsRuler(lines[rulerIndex + 2]) ||
            !boundaries.SequenceEqual(GetNetworkObjectsColumnBoundaries(lines[rulerIndex + 2])))
            throw new InvalidDataException("Veyon 电脑目录清单列格式无法识别。");

        var objects = new List<VeyonNetworkObject>();
        // Value = the identity of every other column. A repeated UUID is only safe to collapse
        // when the whole row is identical, matching ParseConfigurationDirectory's semantics.
        var objectIds = new Dictionary<Guid, string>();
        var foundEnd = false;
        var identicalDuplicates = 0;
        for (var lineIndex = rulerIndex + 3; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (IsNetworkObjectsRuler(line))
            {
                if (!boundaries.SequenceEqual(GetNetworkObjectsColumnBoundaries(line)))
                    throw new InvalidDataException("Veyon 电脑目录清单结束标记无效。");
                foundEnd = true;
                break;
            }
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!IsNetworkObjectsRow(line, boundaries))
                throw new InvalidDataException("Veyon 电脑目录清单行格式无法识别。");

            var fields = ParseNetworkObjectsRow(line, boundaries);
            if (!Guid.TryParse(fields[0], out var objectId) || objectId == Guid.Empty)
                throw new InvalidDataException("Veyon 电脑目录对象标识无效（缺少有效 UUID）。");
            var identity = string.Join("\u001f", fields.Skip(1));
            if (objectIds.TryGetValue(objectId, out var existingIdentity))
            {
                if (!string.Equals(existingIdentity, identity, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Veyon 电脑目录里同一个对象 UUID 出现了多次且内容不一致（UUID {objectId:D}，类型 {fields[2].Trim()}，名称「{fields[3].Trim()}」）：" +
                        "已停止读取，避免把电脑绑定到错误地点。请先导出并备份 Veyon 配置，核对 BuiltinDirectory/NetworkObjects/JsonStoreArray 中该 UUID 的条目；教师端不会自动删除或合并 Veyon 对象。");
                identicalDuplicates++;
                continue;
            }
            objectIds.Add(objectId, identity);
            var parentObjectId = Guid.Empty;
            if (fields[1].Length > 0 && !Guid.TryParse(fields[1], out parentObjectId))
                throw new InvalidDataException("Veyon 电脑目录地点标识无效。");

            var typeLabel = fields[2];
            // Veyon 4.11.2 prints a top-level Location with an all-zero Parent UUID and empty
            // Host/MAC columns. It is a real Location object (name-derived UUID), not the Root
            // pseudo-row: dropping it leaves every computer with an unresolvable parent UUID,
            // which marks the whole device directory as conflicting and blocks seat/Veyon sync.
            // Only genuine Root rows and unrecognised rows without Host/MAC are excluded.
            if (IsRootTypeLabel(typeLabel) ||
                (!IsLocationTypeLabel(typeLabel) && !IsComputerTypeLabel(typeLabel) &&
                 parentObjectId == Guid.Empty && fields[4].Length == 0 && fields[5].Length == 0))
                continue;
            var type = IsLocationTypeLabel(typeLabel) ? "location" :
                IsComputerTypeLabel(typeLabel) ? "computer" :
                fields[4].Length > 0 || fields[5].Length > 0
                    ? "computer"
                    : parentObjectId != Guid.Empty ? "location"
                    : throw new InvalidDataException("Veyon 电脑目录对象类型无法识别。");
            objects.Add(new VeyonNetworkObject(type, fields[3], fields[4], fields[5], "",
                objectId, parentObjectId));
        }

        if (!foundEnd)
            throw new InvalidDataException("Veyon 电脑目录清单不完整。");
        _lastIdenticalDuplicateObjectCount = identicalDuplicates;
        var locationNames = objects.Where(item => item.Type.Equals("location", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.ObjectId, item => item.Name);
        return Array.AsReadOnly(objects.Select(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase)
            ? item with { Location = locationNames.GetValueOrDefault(item.ParentObjectId, "") }
            : item).ToArray());
    }

    /// <summary>Parses Veyon room exports while preserving each target's computer display name.</summary>
    public static IReadOnlyList<VeyonNetworkLocation> ParseLocationsExport(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var byLocation = new Dictionary<string, List<(string Target, string DisplayName)>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ParseExportRow(line);
            var computerName = fields[0].Trim();
            var host = fields[1].Trim();
            var location = fields[2].Trim();
            var target = host.Length > 0 ? host : computerName;
            if (target.Length == 0) continue;

            if (location.Length == 0) location = "未分组电脑";
            if (!byLocation.TryGetValue(location, out var targets))
                byLocation.Add(location, targets = []);
            targets.Add((target, computerName));
        }

        var locations = byLocation
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair =>
            {
                var computers = pair.Value
                    .ToArray();
                var duplicateTargets = computers.GroupBy(item => item.Target,
                        StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
                if (duplicateTargets.Length > 0)
                    throw new InvalidDataException(
                        $"Veyon 地点“{pair.Key}”导出包含重复 Host/电脑目标：{string.Join("、", duplicateTargets.Take(5))}。" +
                        "若 Veyon 配置器里有两个同名地点且电脑完全一样，通常是同一对象被重复引用（删一个会两个一起消失）；" +
                        "请先导出并备份 Veyon 配置再核对重复对象。目录读取未应用。");
                var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var computer in computers)
                {
                    if (!string.IsNullOrWhiteSpace(computer.DisplayName))
                        names[computer.Target] = computer.DisplayName;
                }
                return new VeyonNetworkLocation(pair.Key,
                    Array.AsReadOnly(computers.Select(computer => computer.Target).ToArray()),
                    new ReadOnlyDictionary<string, string>(names));
            })
            .Where(location => location.Targets.Count > 0)
            .ToArray();
        return Array.AsReadOnly(locations);
    }

    public static IReadOnlySet<string> FindTargetsInMultipleLocations(
        IEnumerable<VeyonNetworkLocation> locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        var membership = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var locationIndex = 0;
        foreach (var location in locations)
        {
            ArgumentNullException.ThrowIfNull(location);
            foreach (var rawTarget in location.Targets)
            {
                var target = rawTarget?.Trim();
                if (string.IsNullOrWhiteSpace(target)) continue;
                if (!membership.TryGetValue(target, out var locationsForTarget))
                    membership.Add(target, locationsForTarget = []);
                locationsForTarget.Add(locationIndex);
            }
            locationIndex++;
        }
        return membership.Where(pair => pair.Value.Count > 1)
            .Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reads the current Veyon directory and previews location/host conflicts without writing.</summary>
    public static IReadOnlyList<string> ReadImportConflicts(string locationName,
        IReadOnlyList<VeyonNetworkComputer> computers)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("检查 Veyon 机房目录仅支持 Windows 教师端。");
        ValidateImportPlan(locationName, computers);
        var cliPath = ResolveCliPathOrThrow("networkobjects dump");
        return BuildImportPreview(locationName, computers,
            ReadNetworkObjects(cliPath, Path.GetDirectoryName(cliPath)!)).Conflicts;
    }

    /// <summary>Reads current Veyon objects and previews additions, preserved duplicates and hard conflicts.</summary>
    public static VeyonNetworkImportPreview ReadImportPreview(string locationName,
        IReadOnlyList<VeyonNetworkComputer> computers)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("检查 Veyon 机房目录仅支持 Windows 教师端。");
        ValidateImportPlan(locationName, computers);
        var cliPath = ResolveCliPathOrThrow("networkobjects dump");
        return BuildImportPreview(locationName, computers,
            ReadNetworkObjects(cliPath, Path.GetDirectoryName(cliPath)!));
    }

    /// <summary>Pure conflict check used by the UI and regression fixtures.</summary>
    public static IReadOnlyList<string> FindImportConflicts(string locationName,
        IReadOnlyList<VeyonNetworkComputer> computers, IReadOnlyList<VeyonNetworkObject> existing)
    {
        return BuildImportPreview(locationName, computers, existing).Conflicts;
    }

    /// <summary>Pure import diff used by the UI and regression fixtures.</summary>
    public static VeyonNetworkImportPreview BuildImportPreview(string locationName,
        IReadOnlyList<VeyonNetworkComputer> computers, IReadOnlyList<VeyonNetworkObject> existing)
    {
        ValidateImportPlan(locationName, computers);
        ArgumentNullException.ThrowIfNull(existing);
        var conflicts = new List<string>();
        var existingLocations = existing.Where(entry => entry.Type.Equals("location", StringComparison.OrdinalIgnoreCase) &&
            entry.Name.Equals(locationName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var locationExists = existingLocations.Length > 0;
        if (existingLocations.Length > 1)
            conflicts.Add($"Veyon 中有多个同名地点“{locationName}”，无法安全选择目标。");
        if (existingLocations.Length == 1 && existingLocations[0].ObjectId == Guid.Empty)
            conflicts.Add($"地点“{locationName}”缺少可核对的 Veyon UUID，无法安全写入。");
        if (existingLocations.Length == 0 && existing.Any(entry =>
                entry.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) &&
                entry.Location.Equals(locationName, StringComparison.OrdinalIgnoreCase)))
            conflicts.Add($"地点“{locationName}”有电脑记录但缺少地点对象 UUID；请刷新目录并人工核对。");

        var existingComputers = existing
            .Where(entry => entry.Type.Equals("computer", StringComparison.OrdinalIgnoreCase)).ToArray();
        var locationComputers = existingComputers.Where(entry =>
            entry.Location.Equals(locationName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var plannedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plannedComputerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingMatches = new List<VeyonExistingComputerMatch>();
        var toAdd = new List<VeyonNetworkComputer>();
        var skipped = new List<string>();
        foreach (var computer in computers)
        {
            if (!plannedComputerNames.Add(computer.ComputerName))
                conflicts.Add($"电脑编号“{computer.ComputerName}”在本次清单中重复。");
            if (!plannedHosts.Add(computer.Host))
                conflicts.Add($"主机名/IP“{computer.Host}”在本次清单中重复。");
            // A unique Host match inside this exact Veyon location can preserve an existing
            // object even when its Veyon display name differs; the reader binds its UUID
            // without treating the display label as device identity.
            var hostMatches = locationComputers.Where(entry => string.Equals(
                entry.Host.Length > 0 ? entry.Host : entry.Name, computer.Host,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (existingComputers.Any(entry =>
                    !entry.Location.Equals(locationName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entry.Host.Length > 0 ? entry.Host : entry.Name, computer.Host,
                        StringComparison.OrdinalIgnoreCase)))
            {
                conflicts.Add($"电脑“{computer.ComputerName}”的 Host 已存在于其他或未确认地点；" +
                    "为避免跨地点误绑定，请先核对目录再写入。");
                continue;
            }
            if (hostMatches.Length == 1)
            {
                var match = hostMatches[0];
                if (existingLocations.Length != 1 || match.ObjectId == Guid.Empty ||
                    match.ParentObjectId == Guid.Empty ||
                    match.ParentObjectId != existingLocations[0].ObjectId)
                {
                    conflicts.Add($"主机“{computer.Host}”已有同地点对象，但 Veyon UUID/父地点关系无法核对；拒绝自动关联。");
                    continue;
                }
                skipped.Add($"{computer.ComputerName} → {computer.Host}：保留本地点现有 Veyon Name“{match.Name}”，按唯一 Host/UUID 关联。");
                existingMatches.Add(new VeyonExistingComputerMatch(match.Name,
                    match.Host.Length > 0 ? match.Host : match.Name, match.ObjectId,
                    match.ParentObjectId, match.Location));
                continue;
            }
            if (hostMatches.Length > 0)
            {
                conflicts.Add($"电脑“{computer.ComputerName}”的 Host 在当前地点重复或 UUID/父地点无法唯一核对；不会自动绑定或覆盖。");
                continue;
            }
            toAdd.Add(computer);
        }
        return new VeyonNetworkImportPreview(locationName, locationExists,
            toAdd.AsReadOnly(), skipped.AsReadOnly(), conflicts.AsReadOnly(),
            existingMatches.AsReadOnly());
    }

    /// <summary>
    /// Verifies that manually added computers still match the existing Veyon objects before
    /// the seat grid associates them. The association uses stable object UUIDs, never a second
    /// import, so existing Veyon computers are never duplicated.
    /// </summary>
    public static IReadOnlyList<VeyonExistingComputerMatch> VerifyExistingComputerAssociation(
        string locationName, IReadOnlyList<VeyonNetworkComputer> computers)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("核对 Veyon 电脑关联仅支持 Windows 教师端。");
        if (computers is not { Count: > 0 })
            throw new InvalidDataException("关联核对至少需要一台电脑。");
        var cliPath = ResolveCliPathOrThrow("networkobjects dump");
        var current = ReadNetworkObjects(cliPath, Path.GetDirectoryName(cliPath)!);
        var preview = BuildImportPreview(locationName, computers, current);
        if (preview.Conflicts.Count > 0)
            throw new InvalidDataException("关联核对发现问题：" + string.Join("、", preview.Conflicts));
        return (preview.ExistingMatches ?? []).Where(match =>
                computers.Any(computer =>
                    string.Equals(match.Host, computer.Host, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    /// <summary>Reuses an existing location, preserves duplicate objects and imports only new computers.</summary>
    public static VeyonLocationImportResult AddLocation(string locationName,
        IReadOnlyList<VeyonNetworkComputer> computers)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("写入 Veyon 机房目录仅支持 Windows 教师端。");
        ValidateImportPlan(locationName, computers);

        using var mutationGate = VeyonDirectoryMutationGate.Acquire();
        var cliPath = ResolveCliPathOrThrow("networkobjects dump");
        var workingDirectory = Path.GetDirectoryName(cliPath)!;
        var current = ReadNetworkObjects(cliPath, workingDirectory);
        var preview = BuildImportPreview(locationName, computers, current);
        if (preview.Conflicts.Count > 0)
            throw new InvalidDataException("添加前冲突检查发现问题，未写入任何内容：" + string.Join("、", preview.Conflicts));
        var namedMatches = (preview.ExistingMatches ?? [])
            .Where(match => computers.Any(computer =>
                string.Equals(match.Host, computer.Host, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (preview.ComputersToAdd.Count == 0)
            return new VeyonLocationImportResult(locationName, computers.Count,
                computers.Count(computer => !string.IsNullOrWhiteSpace(computer.StudentName)),
                AddedComputerCount: 0, SkippedComputerCount: preview.SkippedComputers.Count,
                LocationCreated: false, AssociatedExisting: namedMatches,
                AddedComputerTargets: Array.Empty<VeyonAddedComputerTarget>());

        var locationCreated = false;
        var runner = new ProcessRunner();
        var existingLocationIds = current.Where(item =>
            item.Type.Equals("location", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Name, locationName, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.ObjectId).ToArray();
        var locationId = existingLocationIds.Length == 1 ? existingLocationIds[0] : Guid.Empty;

        if (locationId == Guid.Empty)
        {
            runner.Run(cliPath, ["networkobjects", "add", "location", locationName],
                workingDirectory, TimeSpan.FromSeconds(45), outputLimitChars: 4096);
            if (runner.ExitCode is not 0)
                throw new InvalidOperationException($"创建 Veyon 地点失败（退出码 {runner.ExitCode?.ToString() ?? "未知"}）：{ProcessDetail(runner)}");
            locationCreated = true;
            var created = ReadNetworkObjects(cliPath, workingDirectory);
            var createdMatches = created.Where(item =>
                item.Type.Equals("location", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Name, locationName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (createdMatches.Length != 1 || createdMatches[0].ObjectId == Guid.Empty)
                throw new InvalidDataException("地点已创建，但读回 UUID 不唯一；请人工核对 Veyon 目录。");
            locationId = createdMatches[0].ObjectId;
        }

        // Veyon 4.11.2 `networkobjects import ... location <name>` resolves the parent with
        // findByName() and, on a miss, appends a NEW Location object. Location UUIDs are
        // name-derived, so the appended object has the SAME UUID as the existing one and
        // Veyon Configurator then shows two identical rooms. Adding each computer with an
        // explicit parent UUID uses findNetworkObject() and never creates a location.
        foreach (var computer in preview.ComputersToAdd)
        {
            runner.Run(cliPath,
                ["networkobjects", "add", "computer", computer.DisplayName,
                    computer.Host, "", locationId.ToString("D")],
                workingDirectory, TimeSpan.FromSeconds(45), outputLimitChars: 4096);
            if (runner.ExitCode is not 0)
                throw new InvalidOperationException(
                    $"添加电脑“{computer.DisplayName}”失败（退出码 {runner.ExitCode?.ToString() ?? "未知"}）：{ProcessDetail(runner)}。" +
                    "地点对象未被修改；请返回教师控制台重试，系统会先读取现有目录。");
        }

        IReadOnlyList<VeyonNetworkObject> after;
        try { after = ReadNetworkObjects(cliPath, workingDirectory); }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"地点“{locationName}”和新增电脑已提交，但教师控制台暂时无法读回核对；请稍后在教师控制台重新执行，系统会先读取现有目录：{exception.Message}", exception);
        }
        var locationMatches = after.Where(item =>
            item.Type.Equals("location", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Name, locationName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (locationMatches.Length != 1 || locationMatches[0].ObjectId == Guid.Empty)
            throw new InvalidDataException("地点操作已提交，但读回时地点对象 UUID 不唯一；请人工核对 Veyon 目录。");
        var locationObject = locationMatches[0];
        var beforeIds = current.Select(item => item.ObjectId).ToHashSet();
        var addedTargets = new List<VeyonAddedComputerTarget>(preview.ComputersToAdd.Count);
        foreach (var computer in preview.ComputersToAdd)
        {
            var expectedHost = VeyonHostAddress.NormalizeOverride(computer.Host);
            var matches = after.Where(item =>
            {
                if (!item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) ||
                    item.ParentObjectId != locationObject.ObjectId ||
                    !string.Equals(item.Name, computer.DisplayName, StringComparison.Ordinal))
                    return false;
                try
                {
                    return string.Equals(VeyonHostAddress.NormalizeOverride(item.Host), expectedHost,
                        StringComparison.OrdinalIgnoreCase);
                }
                catch (InvalidDataException) { return false; }
            }).ToArray();
            if (matches.Length != 1 || matches[0].ObjectId == Guid.Empty ||
                beforeIds.Contains(matches[0].ObjectId))
                throw new InvalidDataException(
                    $"地点操作已提交，但新增电脑“{computer.DisplayName}”未获得唯一的新 UUID 读回确认；请人工核对 Veyon 目录。");
            addedTargets.Add(new VeyonAddedComputerTarget(computer.DisplayName, expectedHost,
                matches[0].ObjectId, locationObject.ObjectId, locationObject.Name));
        }

        foreach (var existing in namedMatches)
        {
            var matches = after.Where(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) &&
                item.ObjectId == existing.ObjectId && item.ParentObjectId == existing.ParentObjectId &&
                string.Equals(item.Name, existing.ComputerName, StringComparison.Ordinal) &&
                string.Equals(item.Location, existing.Location, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(VeyonHostAddress.NormalizeOverride(item.Host), existing.Host,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException(
                    $"地点操作已提交，但已存在电脑“{existing.ComputerName}”的 UUID/Host/地点读回不一致；请人工核对目录。");
        }

        return new VeyonLocationImportResult(locationName, computers.Count,
            computers.Count(computer => !string.IsNullOrWhiteSpace(computer.StudentName)),
            preview.ComputersToAdd.Count, preview.SkippedComputers.Count, locationCreated,
            namedMatches, Array.AsReadOnly(addedTargets.ToArray()));
    }

    private static void ValidateImportPlan(string locationName, IReadOnlyList<VeyonNetworkComputer> computers)
    {
        ValidateDirectoryName(locationName, "地点名称");
        if (computers is null || computers.Count is < 1 or > 150)
            throw new InvalidDataException("一个地点需要 1–150 台电脑。");
        foreach (var computer in computers)
        {
            if (computer is null) throw new InvalidDataException("电脑清单不能包含空项目。");
            ValidateDirectoryName(computer.ComputerName, "电脑编号");
            var host = VeyonHostAddress.NormalizeOverride(computer.Host);
            if (!string.Equals(host, computer.ComputerName, StringComparison.OrdinalIgnoreCase) ||
                Uri.CheckHostName(host) != UriHostNameType.Dns)
                throw new InvalidDataException("Veyon Host 必须直接使用固定 Windows 电脑名；不支持 IP 或 Host 映射。");
            ValidateDirectoryName(computer.DisplayName, "电脑显示名");
            if (!string.IsNullOrWhiteSpace(computer.StudentName))
                ValidateDirectoryName(computer.StudentName.Trim(), "学生姓名");
        }
    }

    private static IReadOnlyList<VeyonNetworkObject> ReadNetworkObjects(string cliPath, string workingDirectory)
    {
        var diagnostic = SafeReadNetworkObjects(cliPath, workingDirectory);
        if (diagnostic.Result is { } objects && !diagnostic.PartialRead)
            return objects;
        ThrowWithDiagnostic(diagnostic.Diagnostic, null);
        return [];
    }

    /// <summary>
    /// Attempts `networkobjects dump` and, when that command fails, falls back to the trusted
    /// `config export` snapshot so teachers keep a fingerprinted read instead of a bare exit code.
    /// The fallback result is always flagged; it must not be used as a write-back identity source.
    /// </summary>
    internal static (IReadOnlyList<VeyonNetworkObject>? Result, VeyonDirectoryReadDiagnostic Diagnostic,
        bool PartialRead) SafeReadNetworkObjects(string cliPath, string workingDirectory)
    {
        try
        {
            var objects = TryReadNetworkObjectsDump(cliPath, workingDirectory, out var dumpDiagnostic);
            if (objects is not null)
            {
                LastSuccessfulReadUtc = DateTimeOffset.UtcNow;
                LastTrustedDirectoryFingerprint = ComputeLocationFingerprint(objects);
                var diagnostic = dumpDiagnostic is null
                    ? VeyonDirectoryReadDiagnostic.Success(cliPath, "networkobjects dump")
                    : dumpDiagnostic;
                LastReadDiagnostic = diagnostic;
                return (objects, diagnostic, false);
            }
            if (dumpDiagnostic is null) dumpDiagnostic = VeyonDirectoryReadDiagnostic.For(
                VeyonDirectoryReadDiagnostic.CliFailed, "Veyon 电脑目录清单无法读取。",
                cliPath, null, "", "networkobjects dump", false);
            if (dumpDiagnostic.FailureStage is VeyonDirectoryReadDiagnostic.CliNotFound or
                VeyonDirectoryReadDiagnostic.AccessDenied or
                VeyonDirectoryReadDiagnostic.UnsupportedBackend)
                return (null, dumpDiagnostic, false);
            try
            {
                var export = ExportConfiguration(cliPath, workingDirectory);
                var snapshot = ReadDirectorySnapshot(export);
                if (LastRegistrySnapshotDetail is { Length: > 0 })
                {
                    // The registry snapshot IS Veyon's own directory store: the same objects,
                    // UUIDs and parent links `config export` returns. Only the CLI is unusable,
                    // so this is a complete read, not a partial one - otherwise the teacher loses
                    // the classroom session, seat binding and policy actions too. Write-back still
                    // goes through the CLI and reports its own failure.
                    var registryDiagnostic = VeyonDirectoryReadDiagnostic.Success(cliPath,
                        "config export → 注册表目录快照");
                    LastTrustedDirectoryFingerprint = ComputeLocationFingerprint(snapshot.Objects);
                    LastReadDiagnostic = registryDiagnostic;
                    return (snapshot.Objects, registryDiagnostic, false);
                }
                var diagnostic = VeyonDirectoryReadDiagnostic.For(
                       VeyonDirectoryReadDiagnostic.PartialRead,
                       "Veyon 电脑目录清单读取失败，已改用可信配置快照（" +
                       SnapshotFingerprintLabel(snapshot.ConfigFingerprint) +
                       "）；该快照只用于只读核对，不能作为按 UUID 写回的身份依据。" +
                       dumpDiagnostic.Detail,
                       cliPath, null, dumpDiagnostic.Detail,
                       "networkobjects dump → config export",
                       dumpDiagnostic.TimedOut, snapshot.ConfigFingerprint) with
                {
                    LastSuccessfulReadUtc = LastSuccessfulReadUtc
                };
                LastTrustedDirectoryFingerprint = null;
                LastReadDiagnostic = diagnostic;
                return (snapshot.Objects, diagnostic, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or InvalidOperationException or
                                              TimeoutException or System.ComponentModel.Win32Exception or
                                              System.Text.Json.JsonException)
            {
                var stage = exception switch
                {
                    UnauthorizedAccessException => VeyonDirectoryReadDiagnostic.AccessDenied,
                    TimeoutException => VeyonDirectoryReadDiagnostic.Timeout,
                    InvalidDataException or System.Text.Json.JsonException => VeyonDirectoryReadDiagnostic.ParseFailed,
                    _ => VeyonDirectoryReadDiagnostic.CliFailed
                };
                var detail = string.Join(" | ", new[] { dumpDiagnostic.Detail, exception.Message }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                if (detail.Length > 2000) detail = detail[..2000];
                return (null, VeyonDirectoryReadDiagnostic.For(stage,
                    $"Veyon 电脑目录读取失败（{dumpDiagnostic.Message}），且配置快照回退不可用：{exception.Message}",
                    cliPath, null, detail, "networkobjects dump → config export",
                    exception is TimeoutException), false);
            }
        }
        catch (VeyonDirectoryReadException failure)
        {
            return (null, failure.Diagnostic, false);
        }
    }

    /// <summary>Reads a directory snapshot from an already exported configuration block.</summary>
    internal static VeyonDirectorySnapshot ReadDirectorySnapshot(VeyonConfigurationExport export)
    {
        try
        {
            var hash = SHA256.HashData(export.Bytes);
            var fingerprint = Convert.ToHexString(hash).ToLowerInvariant();
            CryptographicOperations.ZeroMemory(hash);
            var json = DecodeConfiguration(export.Bytes);
            var snapshot = new VeyonDirectorySnapshot(fingerprint, ParseConfigurationDirectory(json));
            LastSuccessfulReadUtc = DateTimeOffset.UtcNow;
            LastTrustedDirectoryFingerprint = ComputeLocationFingerprint(snapshot.Objects);
            LastReadDiagnostic = VeyonDirectoryReadDiagnostic.Success(export.CliPath, "config export", fingerprint);
            return snapshot;
        }
        finally { CryptographicOperations.ZeroMemory(export.Bytes); }
    }

    private static IReadOnlyList<VeyonNetworkObject>? TryReadNetworkObjectsDump(string cliPath,
        string workingDirectory, out VeyonDirectoryReadDiagnostic? dumpDiagnostic)
    {
        dumpDiagnostic = null;
        var runner = new ProcessRunner();
        try
        {
            runner.Run(cliPath, ["networkobjects", "dump"], workingDirectory,
                TimeSpan.FromSeconds(45), outputLimitChars: 8 * 1024 * 1024,
                redirectedOutputEncoding: Encoding.UTF8);
        }
        catch (Exception exception) when (exception is TimeoutException or UnauthorizedAccessException or
                                          PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            dumpDiagnostic = VeyonDirectoryReadDiagnostic.For(
                exception switch
                {
                    TimeoutException => VeyonDirectoryReadDiagnostic.Timeout,
                    UnauthorizedAccessException => VeyonDirectoryReadDiagnostic.AccessDenied,
                    PlatformNotSupportedException => VeyonDirectoryReadDiagnostic.UnsupportedBackend,
                    _ => VeyonDirectoryReadDiagnostic.CliFailed,
                },
                exception.Message, cliPath, null, ProcessDetail(runner), "networkobjects dump",
                exception is TimeoutException);
            return null;
        }
        try
        {
            if (runner.ExitCode is not 0)
            {
                dumpDiagnostic = VeyonDirectoryReadDiagnostic.For(
                    VeyonDirectoryReadDiagnostic.CliFailed,
                    $"Veyon 读取电脑目录失败（退出码 {runner.ExitCode?.ToString() ?? "未知"}）。",
                    cliPath, runner.ExitCode, ProcessDetail(runner), "networkobjects dump",
                    runner.TimedOut);
                return null;
            }
            if (runner.StdoutTruncated)
            {
                dumpDiagnostic = VeyonDirectoryReadDiagnostic.For(
                    VeyonDirectoryReadDiagnostic.ExportFileMissing,
                    "Veyon 电脑目录清单超过读取上限；请检查目录。",
                    cliPath, runner.ExitCode, "", "networkobjects dump", false);
                return null;
            }
            return ParseNetworkObjectsDump(runner.Stdout);
        }
        catch (InvalidDataException exception)
        {
            dumpDiagnostic = VeyonDirectoryReadDiagnostic.For(
                VeyonDirectoryReadDiagnostic.ParseFailed, exception.Message, cliPath,
                runner.ExitCode, ProcessDetail(runner), "networkobjects dump", false);
            return null;
        }
    }

    private static string SnapshotFingerprintLabel(string fingerprint) =>
        fingerprint.Length >= 12 ? fingerprint[..12] : fingerprint;

    private static void ThrowWithDiagnostic(VeyonDirectoryReadDiagnostic diagnostic, Exception? inner)
    {
        LastTrustedDirectoryFingerprint = null;
        var enriched = diagnostic with
        {
            LastSuccessfulReadUtc = LastSuccessfulReadUtc
        };
        LastReadDiagnostic = enriched;
        throw new VeyonDirectoryReadException(enriched, inner);
    }

    private static string ComputeLocationFingerprint(IEnumerable<VeyonNetworkObject> objects)
    {
        var locations = objects.Where(item => item.Type.Equals("location", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.ObjectId, item => item.Name);
        var targets = objects.Where(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase))
            .Select(item => (Location: locations.GetValueOrDefault(item.ParentObjectId,
                    string.IsNullOrWhiteSpace(item.Location) ? "未分组电脑" : item.Location),
                Target: string.IsNullOrWhiteSpace(item.Host) ? item.Name : item.Host))
            .Where(item => item.Target.Length > 0)
            .Select(item => item.Location.Trim().ToUpperInvariant() + "\u001f" +
                            item.Target.Trim().ToUpperInvariant())
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        return FingerprintTargets(targets);
    }

    private static string ComputeLocationFingerprint(IEnumerable<VeyonNetworkLocation> locations)
    {
        var targets = locations.SelectMany(location => location.Targets.Select(target =>
                location.Name.Trim().ToUpperInvariant() + "\u001f" + target.Trim().ToUpperInvariant()))
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        return FingerprintTargets(targets);
    }

    private static string FingerprintTargets(IEnumerable<string> targets)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", targets));
        try
        {
            var hash = SHA256.HashData(bytes);
            try { return Convert.ToHexString(hash).ToLowerInvariant(); }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool IsNetworkObjectsRuler(string line) =>
        line.Length >= 2 && line[0] == '+' && line[^1] == '+' &&
        line.All(character => character is '+' or '=' or '-');

    private static int[] GetNetworkObjectsColumnBoundaries(string ruler) =>
        ruler.Select((character, index) => (character, index))
            .Where(item => item.character == '+')
            .Select(item => item.index).ToArray();

    private static bool IsNetworkObjectsRow(string line, IReadOnlyList<int> boundaries)
    {
        if (line.Length <= boundaries[^1] || line[0] != '|' || line[boundaries[^1]] != '|')
            return false;
        return boundaries.All(index => index < line.Length && line[index] == '|');
    }

    private static string[] ParseNetworkObjectsRow(string line, IReadOnlyList<int> boundaries)
    {
        var fields = new string[boundaries.Count - 1];
        for (var index = 0; index < fields.Length; index++)
            fields[index] = line.Substring(boundaries[index] + 1,
                boundaries[index + 1] - boundaries[index] - 1).Trim();
        return fields;
    }

    private static bool IsLocationTypeLabel(string value) =>
        value.Equals("Location", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Room", StringComparison.OrdinalIgnoreCase) ||
        value is "地点" or "位置" or "机房";

    private static bool IsComputerTypeLabel(string value) =>
        value.Equals("Computer", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
        value is "计算机" or "电脑";

    private static bool IsRootTypeLabel(string value) =>
        value.Equals("Root", StringComparison.OrdinalIgnoreCase) || value == "根";

    internal static VeyonConfigurationExport ExportConfiguration()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("读取 Veyon 目录仅支持 Windows 教师端。");
        var cliPath = ResolveCliPathOrThrow("config export");
        return ExportConfiguration(cliPath, Path.GetDirectoryName(cliPath)!);
    }

    private static VeyonConfigurationExport ExportConfiguration(string cliPath, string workingDirectory)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"veyon-campus-directory-{Guid.NewGuid():N}.txt");
        try
        {
            try
            {
                return ExportConfigurationViaCli(cliPath, workingDirectory, temporaryPath);
            }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or
                                              System.ComponentModel.Win32Exception or IOException or
                                              UnauthorizedAccessException or InvalidDataException)
            {
                // veyon-cli is unusable on some real teacher machines: a security or classroom
                // product injects a DLL into every child process and the CLI aborts with
                // "R6031 - Attempt to initialize the CRT more than once" (exit code 255). The
                // Builtin Directory lives in Veyon's own registry store, so read that snapshot
                // instead of losing the roster for the whole lesson.
                if (!TryReadConfigurationFromRegistry(out var registryBytes, out var registryDetail))
                {
                    if (exception is TimeoutException) throw;
                    throw new InvalidOperationException(
                        $"{exception.Message} 注册表目录快照也不可用：{registryDetail}", exception);
                }
                LastRegistrySnapshotDetail = DescribeRegistryFallback(exception.Message, registryDetail);
                return new VeyonConfigurationExport(cliPath, workingDirectory, registryBytes);
            }
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static VeyonConfigurationExport ExportConfigurationViaCli(string cliPath, string workingDirectory,
        string temporaryPath)
    {
        var runner = new ProcessRunner();
        runner.Run(cliPath, ["config", "export", temporaryPath],
            workingDirectory, TimeSpan.FromSeconds(45), outputLimitChars: 4096);
        if (runner.ExitCode is not 0)
            throw new InvalidOperationException(
                $"Veyon 导出电脑目录失败（退出码 {runner.ExitCode?.ToString() ?? "未知"}）：{ProcessDetail(runner)}");
        if (!File.Exists(temporaryPath))
            throw new InvalidDataException("Veyon 没有生成电脑目录导出文件；没有添加地点。");

        var info = new FileInfo(temporaryPath);
        if (info.Length is <= 0 or > 4 * 1024 * 1024)
            throw new InvalidDataException("Veyon 配置超过读取上限；请核对目录。");
        PathLinkSecurity.RejectLinks(temporaryPath);
        LastRegistrySnapshotDetail = null;
        return new VeyonConfigurationExport(cliPath, workingDirectory, File.ReadAllBytes(temporaryPath));
    }

    /// <summary>
    /// Reads the Builtin Directory straight from Veyon's registry store. Veyon keeps
    /// <c>BuiltinDirectory\NetworkObjects</c> as <c>@@JsonValue(base64(json))</c>; the payload is the
    /// same object array <c>config export</c> produces, so it is re-wrapped into the exported shape
    /// and parsed by the regular reader. Only used when the CLI cannot run at all.
    /// </summary>
    internal static bool TryReadConfigurationFromRegistry(out byte[] bytes, out string detail)
    {
        bytes = [];
        detail = "";
        if (!OperatingSystem.IsWindows())
        {
            detail = "非 Windows 环境，无法读取 Veyon 配置注册表。";
            return false;
        }

        string? raw = null;
        var source = "";
        try
        {
            // The location directory is machine-scoped; only fall back to the per-user hive when
            // the machine hive carries no directory value at all.
            foreach (var (hive, view, label) in new[]
                     {
                         (RegistryHive.LocalMachine, RegistryView.Registry64, @"HKLM (64 位)"),
                         (RegistryHive.LocalMachine, RegistryView.Registry32, @"HKLM (32 位)"),
                         (RegistryHive.CurrentUser, RegistryView.Default, @"HKCU")
                     })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(RegistrySettingsSubKey);
                if (key?.GetValue(RegistryNetworkObjectsValue) is string value && value.Length > 0)
                {
                    raw = value;
                    source = label;
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or
                                          IOException)
        {
            detail = "读取 Veyon 配置注册表失败：" + exception.Message;
            return false;
        }

        if (raw is null)
        {
            detail = "注册表中没有 Veyon 目录配置（可能使用外部目录后端）。";
            return false;
        }

        return TryDecodeRegistryDirectoryValue(raw, source, out bytes, out detail);
    }

    /// <summary>
    /// Decodes one raw <c>BuiltinDirectory\NetworkObjects</c> registry value into the JSON shape
    /// <c>config export</c> produces. Split from the hive lookup so the decoding can be exercised
    /// without writing to the registry.
    /// </summary>
    internal static bool TryDecodeRegistryDirectoryValue(string raw, string source,
        out byte[] bytes, out string detail)
    {
        bytes = [];
        detail = "";
        ArgumentNullException.ThrowIfNull(raw);

        string json;
        if (raw.StartsWith(JsonValueMarker, StringComparison.Ordinal) &&
            raw.EndsWith(')') && raw.Length > JsonValueMarker.Length)
        {
            var encoded = raw[JsonValueMarker.Length..^1];
            try
            {
                json = new UTF8Encoding(false, true).GetString(
                    Convert.FromBase64String(encoded.ReplaceLineEndings("")));
            }
            catch (Exception exception) when (exception is FormatException or DecoderFallbackException or
                                              ArgumentException)
            {
                detail = "Veyon 目录注册表值的编码无法识别。";
                return false;
            }
        }
        else if (raw.TrimStart() is ['{' or '[', ..])
        {
            json = raw;
        }
        else
        {
            detail = "Veyon 目录注册表值的格式无法识别。";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var array = ExtractDirectoryArray(document.RootElement);
            if (array is null)
            {
                detail = "Veyon 目录注册表值中没有对象数组。";
                return false;
            }
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("BuiltinDirectory");
                writer.WriteStartObject();
                writer.WritePropertyName("NetworkObjects");
                writer.WriteStartArray();
                foreach (var entry in array.Value.EnumerateArray())
                    entry.WriteTo(writer);
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            bytes = buffer.ToArray();
            detail = string.IsNullOrWhiteSpace(source) ? "注册表目录快照" : $"{source} 注册表目录快照";
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            detail = "Veyon 目录注册表值无法解析：" + exception.Message;
            return false;
        }
    }

    private static JsonElement? ExtractDirectoryArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return null;
        // JsonStoreArray serializes as {"a":[...]}.
        if (root.TryGetProperty("a", out var store) && store.ValueKind == JsonValueKind.Array) return store;
        if (root.TryGetProperty("NetworkObjects", out var networkObjects))
        {
            if (networkObjects.ValueKind == JsonValueKind.Array) return networkObjects;
            if (networkObjects.ValueKind == JsonValueKind.Object &&
                networkObjects.TryGetProperty("JsonStoreArray", out var jsonStore) &&
                jsonStore.ValueKind == JsonValueKind.Array)
                return jsonStore;
        }
        if (root.TryGetProperty("BuiltinDirectory", out var builtin) &&
            builtin.ValueKind == JsonValueKind.Object)
            return ExtractDirectoryArray(builtin);
        return null;
    }

    private static string DescribeRegistryFallback(string cliFailure, string registryDetail)
    {
        var reason = IsCrtReinitializationFailure(cliFailure)
            ? "Veyon 命令行被系统组件干扰而无法启动（R6031：CRT 重复初始化），通常是安全/管控软件向子进程注入 DLL 所致。"
            : "Veyon 命令行无法完成配置导出。";
        return $"{reason}已改用只读注册表目录快照（{registryDetail}）；地点与电脑的写回仍需可用的 veyon-cli。";
    }

    private static bool IsCrtReinitializationFailure(string text) =>
        text.Contains("R6031", StringComparison.Ordinal) ||
        text.Contains("initialize the CRT more than once", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<VeyonNetworkObject> ParseConfigurationDirectory(string json)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        try { PolicyJson.RejectDuplicateFields(jsonBytes); }
        finally { CryptographicOperations.ZeroMemory(jsonBytes); }
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Veyon 配置格式无法识别。");
        JsonElement objects = default;
        // Veyon 4.1 moved the built-in directory from LocalData/NetworkObjects
        // to BuiltinDirectory/NetworkObjects. Older keys can remain in a 4.x
        // registry export even though the Configurator no longer displays them.
        // Once the modern section exists it is authoritative, including when it
        // has no NetworkObjects value; never resurrect the hidden legacy copy.
        if (root.TryGetProperty("BuiltinDirectory", out var builtinDirectory))
        {
            if (builtinDirectory.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Veyon 目录配置格式无法识别。");
            builtinDirectory.TryGetProperty("NetworkObjects", out objects);
        }
        else if (root.TryGetProperty("LocalData", out var legacyLocalData))
        {
            // Compatibility for exports created before the 4.1 directory migration.
            if (legacyLocalData.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Veyon 目录配置格式无法识别。");
            legacyLocalData.TryGetProperty("NetworkObjects", out objects);
        }
        if (objects.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException(
                "Veyon 配置中没有可识别的 Builtin Directory；可能使用外部目录后端，已拒绝将其误判为空目录。");
        if (objects.ValueKind == JsonValueKind.Object && objects.TryGetProperty("JsonStoreArray", out var array))
            objects = array;
        if (objects.ValueKind != JsonValueKind.Array || objects.GetArrayLength() > 10000)
            throw new InvalidDataException("Veyon 目录对象格式或数量无法识别。");
        var entries = new List<(Guid Id, Guid Parent, int Type, string Name, string Host, string Mac)>();
        var identities = new Dictionary<Guid, (int Type, Guid Parent, string Name, string Host, string Mac)>();
        var entryIndex = 0;
        foreach (var entry in objects.EnumerateArray())
        {
            entryIndex++;
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("Type", out var type) ||
                !type.TryGetInt32(out var numericType))
                throw new InvalidDataException($"Veyon 目录第 {entryIndex} 个对象的类型字段无效。");
            if (numericType is < VeyonObjectTypeNone or > VeyonObjectTypeDesktopGroup)
                throw new InvalidDataException($"Veyon 目录第 {entryIndex} 个对象使用了不受支持的类型 {numericType}。");

            var isLocationOrComputer = numericType is VeyonObjectTypeLocation or VeyonObjectTypeHost;
            var id = Guid.Empty;
            var hasUid = entry.TryGetProperty("Uid", out var uid) && uid.ValueKind == JsonValueKind.String &&
                         Guid.TryParse(uid.GetString(), out id) && id != Guid.Empty;
            if (!hasUid)
            {
                if (isLocationOrComputer)
                    throw new InvalidDataException($"Veyon 目录第 {entryIndex} 个机房或电脑对象缺少有效 UUID。");
                continue;
            }
            // Root, labels, and desktop groups are not exposed as identity targets by
            // this reader. Only location/computer UUID collisions make seat binding ambiguous.
            if (!isLocationOrComputer) continue;

            string Text(string field) => entry.TryGetProperty(field, out var value)
                ? value.ValueKind == JsonValueKind.String ? value.GetString()! :
                    throw new InvalidDataException("Veyon 目录字段类型无效。") : "";
            var parentText = Text("ParentUid");
            var parent = Guid.Empty;
            if (parentText.Length > 0 && !Guid.TryParse(parentText, out parent))
                throw new InvalidDataException("Veyon 地点关联标识无效。");
            var name = Text("Name");
            var host = Text("HostAddress");
            var mac = Text("MacAddress");
            var identity = (numericType, parent, name, host, mac);
            if (identities.TryGetValue(id, out var existing))
            {
                if (existing != identity)
                    throw new InvalidDataException(
                        $"Veyon 目录第 {entryIndex} 个机房或电脑对象与 UUID 相同的对象内容不一致；为避免误绑定，已停止读取。");
                continue;
            }
            identities.Add(id, identity);
            entries.Add((id, parent, numericType, name, host, mac));
        }
        var locations = entries.Where(entry => entry.Type == VeyonObjectTypeLocation)
            .ToDictionary(entry => entry.Id, entry => entry.Name);
        return entries.Where(entry => entry.Type is VeyonObjectTypeLocation or VeyonObjectTypeHost)
            .Select(entry => new VeyonNetworkObject(
            entry.Type == VeyonObjectTypeLocation ? "location" : "computer", entry.Name, entry.Host, entry.Mac,
            locations.GetValueOrDefault(entry.Parent, ""), entry.Id, entry.Parent)).ToArray();
    }

    private static string ProcessDetail(ProcessRunner runner)
    {
        // stdout may contain the full computer directory. Keep diagnostics useful without
        // copying student names, Hosts, MAC addresses, or UUIDs into UI error details.
        var detail = runner.Stderr.Trim();
        detail = new string(detail.Where(character => !char.IsControl(character) ||
            character is '\r' or '\n' or '\t').ToArray());
        if (IsCrtReinitializationFailure(detail))
            detail = "Veyon 命令行无法启动（R6031：CRT 重复初始化，通常是安全或管控软件向子进程注入 DLL 所致）。" +
                     "请在这台电脑上修复安装 Veyon，或让安全软件不再干扰 veyon-cli 后重试。原始输出：" + detail;
        return detail.Length > 1000 ? detail[..1000] : detail;
    }

    internal static string DecodeConfiguration(byte[] bytes)
    {
        var value = new UTF8Encoding(false, true).GetString(bytes);
        return value.Length > 0 && value[0] == '\uFEFF' ? value[1..] : value;
    }

    private static void ValidateDirectoryName(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
            throw new InvalidDataException($"{label}不能为空，最多 100 个字符，不能有首尾空格或控制字符。");
    }

    private static string[] ParseExportRow(string line, int expectedFieldCount = 3)
    {
        var fields = line.Split(FieldSeparator, StringSplitOptions.None);
        if (fields.Length != expectedFieldCount)
            throw new InvalidDataException("Veyon 电脑目录导出字段数无法识别；没有更改目录或导入目标。");
        return fields;
    }
}
