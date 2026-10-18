using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VeyonCampus.Core;

/// <summary>A narrowly scoped rename target captured from the current Veyon directory.</summary>
public sealed record VeyonComputerRenameTargetPlan(Guid ObjectId, Guid ParentObjectId,
    string OldName, string NewName, string HostAddress);

/// <summary>Per-computer result returned after a rename or recovery attempt.</summary>
public sealed record VeyonComputerRenameTargetResult(Guid ObjectId, string OldName, string NewName,
    string HostAddress, string Status, string Detail);

public sealed record VeyonComputerRenameExecution(StepResult Step, string? BackupPath,
    IReadOnlyList<VeyonComputerRenameTargetResult> Targets);

/// <summary>
/// Renames only Veyon Computer Name properties through config export/import. The original
/// configuration is backed up with current-user DPAPI before import and is used for safe rollback.
/// </summary>
public static class VeyonComputerRenameService
{
    private const int MaximumConfigurationBytes = 4 * 1024 * 1024;
    private const int MaximumTargets = 150;
    private const string StepId = "veyon-computer-rename";
    private static readonly JsonSerializerOptions BackupJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    private sealed record BackupDocument(int SchemaVersion, DateTimeOffset CreatedAtUtc,
        string ConfigFingerprint, string OriginalConfigBase64,
        VeyonComputerRenameTargetPlan[] Targets);

    public static VeyonComputerRenameExecution Apply(string expectedConfigFingerprint,
        IReadOnlyList<VeyonComputerRenameTargetPlan> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("修改 Veyon 电脑显示名称仅支持 Windows 教师端。");
        ValidatePlan(expectedConfigFingerprint, targets);

        var export = VeyonNetworkObjectDirectory.ExportConfiguration();
        string? backupPath = null;
        byte[]? updatedBytes = null;
        byte[]? currentBytes = null;
        string? importPath = null;
        try
        {
            var currentFingerprint = SHA256.HashData(export.Bytes);
            var expectedFingerprint = Convert.FromHexString(expectedConfigFingerprint);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(currentFingerprint, expectedFingerprint))
                    throw new InvalidOperationException("Veyon 目录自生成计划后已有变化；请重新读取并生成换座计划。没有写入配置。");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(currentFingerprint);
                CryptographicOperations.ZeroMemory(expectedFingerprint);
            }

            var originalJson = VeyonNetworkObjectDirectory.DecodeConfiguration(export.Bytes);
            var originalRoot = ParseRoot(originalJson);
            var objects = VeyonNetworkObjectDirectory.ParseConfigurationDirectory(originalJson);
            var selected = ValidateCurrentObjects(objects, targets);
            ValidateNameAvailability(objects, targets);

            var updatedRoot = originalRoot.DeepClone();
            var updatedEntries = GetNetworkObjects(updatedRoot);
            foreach (var target in targets)
                FindEntry(updatedEntries, target.ObjectId)["Name"] = target.NewName;

            var restoredProjection = updatedRoot.DeepClone();
            var restoredEntries = GetNetworkObjects(restoredProjection);
            foreach (var target in targets)
                FindEntry(restoredEntries, target.ObjectId)["Name"] = target.OldName;
            if (!JsonNode.DeepEquals(originalRoot, restoredProjection))
                throw new InvalidDataException("换座计划包含名称以外的配置变化；没有写入 Veyon。");

            var updatedJson = updatedRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            updatedBytes = new UTF8Encoding(false).GetBytes(updatedJson);
            if (updatedBytes.Length is <= 0 or > MaximumConfigurationBytes)
                throw new InvalidDataException("修改后的 Veyon 配置超出读取上限；没有写入 Veyon。");

            backupPath = SaveEncryptedBackup(export.Bytes, expectedConfigFingerprint, targets);
            importPath = Path.Combine(Path.GetDirectoryName(backupPath)!, ".import-" + Guid.NewGuid().ToString("N") + ".json");
            WriteNewFile(importPath, updatedBytes);

            var preImportExport = VeyonNetworkObjectDirectory.ExportConfiguration();
            try
            {
                var preImportFingerprint = SHA256.HashData(preImportExport.Bytes);
                var expected = Convert.FromHexString(expectedConfigFingerprint);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(preImportFingerprint, expected))
                        throw new InvalidOperationException("Veyon 目录在准备改名文件期间发生变化；没有导入更改。请重新生成计划。");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(preImportFingerprint);
                    CryptographicOperations.ZeroMemory(expected);
                }
            }
            finally { CryptographicOperations.ZeroMemory(preImportExport.Bytes); }

            string? importFailure = null;
            int? importExitCode = null;
            try
            {
                var runner = new ProcessRunner();
                runner.Run(export.CliPath, ["config", "import", importPath],
                    export.WorkingDirectory, TimeSpan.FromSeconds(90), outputLimitChars: 4096);
                importExitCode = runner.ExitCode;
                if (runner.ExitCode is not 0)
                    importFailure = $"Veyon 配置导入退出码 {runner.ExitCode?.ToString() ?? "未知"}。";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidOperationException or TimeoutException or
                                               System.ComponentModel.Win32Exception)
            {
                importFailure = "Veyon 配置导入状态未确认：" + exception.Message;
            }

            var readback = TryReadCurrentConfiguration();
            if (readback.Bytes is not null)
            {
                currentBytes = readback.Bytes;
                var currentRoot = ParseRoot(VeyonNetworkObjectDirectory.DecodeConfiguration(currentBytes));
                if (JsonEquivalent(updatedRoot, currentRoot))
                {
                    var successTargets = targets.Select(target => TargetResult(target, selected[target.ObjectId],
                        "succeeded", "名称已读回，原地址与 Veyon 对象标识保持不变。")).ToArray();
                    var importNote = importFailure is null ? "" : "导入进程返回异常，但完整读回结果与计划一致。";
                    return new VeyonComputerRenameExecution(
                        new StepResult(StepId, ExecutionPlan.Succeeded,
                            $"已修改并读回确认 {targets.Count} 台 Veyon 电脑名称。备份：{backupPath}。{importNote}",
                            importExitCode), backupPath, Array.AsReadOnly(successTargets));
                }

                if (JsonEquivalent(originalRoot, currentRoot))
                {
                    var unchanged = targets.Select(target => TargetResult(target, selected[target.ObjectId],
                        "unchanged", "目录仍保留原名称；没有应用换座改名。")).ToArray();
                    return new VeyonComputerRenameExecution(
                        new StepResult(StepId, ExecutionPlan.Failed,
                            "Veyon 目录仍是原配置；没有更改学生座位记录。可重新读取目录后再试。备份：" + backupPath,
                            importExitCode), backupPath, Array.AsReadOnly(unchanged));
                }

                if (IsEquivalentOutsideTargetNames(originalRoot, currentRoot, targets))
                {
                    var rollback = TryRestore(export, export.Bytes, importPath);
                    if (rollback.Succeeded)
                    {
                        var rolledBack = targets.Select(target => TargetResult(target, selected[target.ObjectId],
                            "rolled-back", "部分改名已自动恢复为原名称；原地址和对象标识已读回。")).ToArray();
                        return new VeyonComputerRenameExecution(
                            new StepResult(StepId, ExecutionPlan.Failed,
                                "名称更新未完整通过读回核对，已恢复更改前配置。请刷新目录并重新生成计划后重试。备份：" + backupPath,
                                importExitCode), backupPath, Array.AsReadOnly(rolledBack));
                    }

                    var rollbackRows = targets.Select(target => TargetResult(target, selected[target.ObjectId],
                        "needs-review", "部分结果未确认，自动恢复也未通过读回；请在教师控制台重新读取电脑目录并核对备份。")).ToArray();
                    return new VeyonComputerRenameExecution(
                        new StepResult(StepId, ExecutionPlan.NeedsReview,
                            "改名结果不完整，自动恢复未能确认；不要直接重试。请先检查 Veyon 目录。" +
                            rollback.Detail + " 备份：" + backupPath, importExitCode),
                        backupPath, Array.AsReadOnly(rollbackRows));
                }

                var changedRows = targets.Select(target => TargetResult(target, selected[target.ObjectId],
                    "needs-review", "读回发现名称以外的目录差异；为避免覆盖并发修改，未自动导入备份。")).ToArray();
                return new VeyonComputerRenameExecution(
                    new StepResult(StepId, ExecutionPlan.NeedsReview,
                        "Veyon 配置出现计划以外的差异；未自动恢复，以免覆盖其他更改。请先核对目录。" +
                        (importFailure is null ? "" : " " + importFailure) + " 备份：" + backupPath,
                        importExitCode), backupPath, Array.AsReadOnly(changedRows));
            }

            var unknownRows = targets.Select(target => TargetResult(target, selected[target.ObjectId],
                "needs-review", "无法读回此对象；请核对 Veyon 目录和备份后再决定是否重试。")).ToArray();
            return new VeyonComputerRenameExecution(
                new StepResult(StepId, ExecutionPlan.NeedsReview,
                    "Veyon 改名后无法读回目录；操作结果未确认，请在教师控制台重新读取电脑目录。" +
                    (importFailure is null ? "" : " " + importFailure) + " 备份：" + backupPath,
                    importExitCode), backupPath, Array.AsReadOnly(unknownRows));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(export.Bytes);
            if (updatedBytes is not null) CryptographicOperations.ZeroMemory(updatedBytes);
            if (currentBytes is not null) CryptographicOperations.ZeroMemory(currentBytes);
            if (importPath is not null) DeleteFileBestEffort(importPath);
        }
    }

    private static (bool Succeeded, string Detail) TryRestore(VeyonConfigurationExport export,
        byte[] originalBytes, string importPath)
    {
        try
        {
            File.Delete(importPath);
            WriteNewFile(importPath, originalBytes);
            string? importIssue = null;
            try
            {
                var runner = new ProcessRunner();
                runner.Run(export.CliPath, ["config", "import", importPath],
                    export.WorkingDirectory, TimeSpan.FromSeconds(90), outputLimitChars: 4096);
                if (runner.ExitCode is not 0)
                    importIssue = $"恢复导入退出码 {runner.ExitCode?.ToString() ?? "未知"}。";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidOperationException or TimeoutException or
                                               System.ComponentModel.Win32Exception)
            {
                importIssue = exception.Message;
            }
            var readback = TryReadCurrentConfiguration();
            if (readback.Bytes is null)
                return (false, "恢复后无法读回 Veyon 配置。" + (importIssue is null ? "" : " " + importIssue));
            try
            {
                var originalRoot = ParseRoot(VeyonNetworkObjectDirectory.DecodeConfiguration(originalBytes));
                var restoredRoot = ParseRoot(VeyonNetworkObjectDirectory.DecodeConfiguration(readback.Bytes));
                return JsonEquivalent(originalRoot, restoredRoot)
                    ? (true, "已读回原始配置。")
                    : (false, "恢复后的目录与原始备份不一致。" + (importIssue is null ? "" : " " + importIssue));
            }
            finally { CryptographicOperations.ZeroMemory(readback.Bytes); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidOperationException or TimeoutException or
                                           System.ComponentModel.Win32Exception or InvalidDataException)
        {
            return (false, exception.Message);
        }
    }

    private static (byte[]? Bytes, string? Error) TryReadCurrentConfiguration()
    {
        try
        {
            var export = VeyonNetworkObjectDirectory.ExportConfiguration();
            return (export.Bytes, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidOperationException or TimeoutException or
                                           System.ComponentModel.Win32Exception or InvalidDataException)
        {
            return (null, exception.Message);
        }
    }

    private static bool IsEquivalentOutsideTargetNames(JsonNode original, JsonNode current,
        IReadOnlyList<VeyonComputerRenameTargetPlan> targets)
    {
        try
        {
            var before = original.DeepClone();
            var after = current.DeepClone();
            var beforeEntries = GetNetworkObjects(before);
            var afterEntries = GetNetworkObjects(after);
            foreach (var target in targets)
            {
                var beforeEntry = FindEntry(beforeEntries, target.ObjectId);
                var afterEntry = FindEntry(afterEntries, target.ObjectId);
                beforeEntry["Name"] = "__VEYON_CAMPUS_RENAME__";
                afterEntry["Name"] = "__VEYON_CAMPUS_RENAME__";
            }
            return JsonEquivalent(before, after);
        }
        catch (InvalidDataException) { return false; }
    }

    private static IReadOnlyDictionary<Guid, VeyonNetworkObject> ValidateCurrentObjects(
        IReadOnlyList<VeyonNetworkObject> objects, IReadOnlyList<VeyonComputerRenameTargetPlan> targets)
    {
        var byId = objects.Where(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.ObjectId).ToDictionary(group => group.Key, group => group.ToArray());
        var selected = new Dictionary<Guid, VeyonNetworkObject>();
        foreach (var target in targets)
        {
            if (!byId.TryGetValue(target.ObjectId, out var matches) || matches.Length != 1)
                throw new InvalidDataException("计划中的 Veyon 电脑标识不存在或重复；没有写入目录。");
            var item = matches[0];
            var normalizedCurrentHost = VeyonHostAddress.NormalizeOverride(item.Host);
            if (item.ParentObjectId != target.ParentObjectId ||
                !string.Equals(item.Name, target.OldName, StringComparison.Ordinal) ||
                !string.Equals(normalizedCurrentHost, target.HostAddress, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("计划中的名称、地点或主机地址已变化；请重新读取并生成计划。");
            selected.Add(target.ObjectId, item);
        }
        return selected;
    }

    private static void ValidateNameAvailability(IReadOnlyList<VeyonNetworkObject> objects,
        IReadOnlyList<VeyonComputerRenameTargetPlan> targets)
    {
        var selectedIds = targets.Select(target => target.ObjectId).ToHashSet();
        var desiredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            if (!desiredNames.Add(target.NewName))
                throw new InvalidDataException("换座计划包含重复的新电脑显示名；没有写入目录。");
            if (objects.Any(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) &&
                    !selectedIds.Contains(item.ObjectId) &&
                    string.Equals(item.Name, target.NewName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"新名称“{target.NewName}”已被其他 Veyon 电脑使用；没有写入目录。");
        }
    }

    private static void ValidatePlan(string fingerprint, IReadOnlyList<VeyonComputerRenameTargetPlan> targets)
    {
        if (fingerprint is null || fingerprint.Length != 64 || fingerprint.Any(character =>
                !char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Veyon 目录计划指纹无效。");
        if (targets.Count is < 1 or > MaximumTargets)
            throw new InvalidDataException("换座计划必须包含 1–150 台电脑。");
        var ids = new HashSet<Guid>();
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            if (target is null || target.ObjectId == Guid.Empty || target.ParentObjectId == Guid.Empty ||
                !ids.Add(target.ObjectId))
                throw new InvalidDataException("换座计划包含无效或重复的 Veyon 对象标识。");
            ValidateName(target.OldName, "原电脑名称");
            ValidateName(target.NewName, "新电脑名称");
            var host = VeyonHostAddress.NormalizeOverride(target.HostAddress);
            if (host.Length == 0 || !hosts.Add(host))
                throw new InvalidDataException("换座计划包含空地址或重复主机地址。");
        }
    }

    private static void ValidateName(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100 || value != value.Trim() ||
            value.Any(char.IsControl))
            throw new InvalidDataException($"{label}须为 1–100 个字符，不含首尾空格或控制字符。");
    }

    private static VeyonComputerRenameTargetResult TargetResult(VeyonComputerRenameTargetPlan target,
        VeyonNetworkObject source, string status, string detail) =>
        new(target.ObjectId, target.OldName, target.NewName, source.Host, status, detail);

    private static JsonNode ParseRoot(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            PolicyJson.RejectDuplicateFields(bytes);
            return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 64 })
                   ?? throw new InvalidDataException("Veyon 配置为空。");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool JsonEquivalent(JsonNode? first, JsonNode? second) =>
        string.Equals(CanonicalJson(first), CanonicalJson(second), StringComparison.Ordinal);

    private static string CanonicalJson(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject obj => "{" + string.Join(",", obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + CanonicalJson(pair.Value))) + "}",
        JsonArray array => "[" + string.Join(",", array.Select(CanonicalJson)) + "]",
        JsonValue value => value.ToJsonString(),
        _ => throw new InvalidDataException("Veyon 配置包含无法识别的 JSON 值。")
    };

    private static JsonArray GetNetworkObjects(JsonNode root)
    {
        foreach (var sectionName in new[] { "BuiltinDirectory", "LocalData" })
        {
            if (root is not JsonObject rootObject || rootObject[sectionName] is not JsonObject section)
                continue;
            if (section["NetworkObjects"] is JsonArray directArray) return directArray;
            if (section["NetworkObjects"] is JsonObject wrapper && wrapper["JsonStoreArray"] is JsonArray wrappedArray)
                return wrappedArray;
        }
        throw new InvalidDataException("Veyon 配置不包含可识别的内置目录。");
    }

    private static JsonObject FindEntry(JsonArray entries, Guid objectId)
    {
        var matches = entries.OfType<JsonObject>().Where(entry =>
        {
            var value = ReadText(entry, "Uid");
            return Guid.TryParse(value, out var id) && id == objectId;
        }).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException("Veyon 配置中的对象标识不存在或重复。");
    }

    private static string ReadText(JsonObject entry, string field)
    {
        if (entry[field] is null) return "";
        if (entry[field] is JsonValue value && value.TryGetValue<string>(out var result)) return result ?? "";
        throw new InvalidDataException("Veyon 配置对象字段格式无效。");
    }

    private static string SaveEncryptedBackup(byte[] originalConfig, string fingerprint,
        IReadOnlyList<VeyonComputerRenameTargetPlan> targets)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData) || !Path.IsPathFullyQualified(localAppData))
            throw new InvalidDataException("无法确定教师本机备份目录；没有修改 Veyon。");
        var directory = Path.Combine(localAppData, "VeyonCampus", "Teacher", "VeyonRenameBackups");
        Directory.CreateDirectory(directory);
        PathLinkSecurity.RejectLinks(directory);
        var backupId = Guid.NewGuid();
        var path = Path.Combine(directory, DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" +
            backupId.ToString("N") + ".vcrb");
        var document = new BackupDocument(1, DateTimeOffset.UtcNow, fingerprint,
            Convert.ToBase64String(originalConfig), targets.ToArray());
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, BackupJsonOptions);
        var entropy = Encoding.UTF8.GetBytes("VeyonCampus/VeyonComputerRenameBackup/v1/" + backupId.ToString("N"));
        byte[]? protectedBytes = null;
        byte[]? verified = null;
        string? temporary = null;
        try
        {
            protectedBytes = DeploymentStateBackup.CurrentUserDataProtection.Protect(plaintext, entropy);
            if (protectedBytes.Length is <= 0 or > 16 * 1024 * 1024)
                throw new InvalidDataException("Veyon 改名备份超过安全大小限制。");
            temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            WriteNewFile(temporary, protectedBytes);
            File.Move(temporary, path, overwrite: false);
            temporary = null;
            PathLinkSecurity.RejectLinks(path);
            verified = DeploymentStateBackup.CurrentUserDataProtection.Unprotect(File.ReadAllBytes(path), entropy);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(verified), SHA256.HashData(plaintext)))
                throw new InvalidDataException("Veyon 改名备份读回校验失败。");
            return path;
        }
        catch
        {
            DeleteFileBestEffort(path);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(entropy);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
            if (verified is not null) CryptographicOperations.ZeroMemory(verified);
            if (temporary is not null) DeleteFileBestEffort(temporary);
        }
    }

    private static void WriteNewFile(string path, byte[] bytes)
    {
        if (File.Exists(path)) PathLinkSecurity.RejectLinks(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void DeleteFileBestEffort(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
