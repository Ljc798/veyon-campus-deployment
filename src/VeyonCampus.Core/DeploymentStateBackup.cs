using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace VeyonCampus.Core;

public sealed record DeploymentStateBackupResult(bool Succeeded, string? Path, string Detail);
public sealed record DeploymentStateBackupReviewResult(bool Succeeded, DateTimeOffset? CreatedAtUtc,
    bool HasVeyonConfig, string? VeyonConfigSha256, string Detail);
public sealed record DeploymentStateBackupComparisonResult(bool Succeeded, bool? MatchesSnapshot, string Detail);
public sealed record DeploymentStateBackupSystemComparisonResult(bool Succeeded, string Detail);
public sealed record DeploymentStateBackupExportResult(bool Succeeded, string Detail);

/// <summary>
/// Captures the local facts needed to review or restore selected changes before
/// a deployment starts. The archive is protected with Windows DPAPI for the
/// current user because Veyon configuration exports may contain credentials.
/// It never stores entered passwords or private authentication keys.
/// </summary>
public static partial class DeploymentStateBackup
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("VCAMPUS-BACKUP-1\0");
    private const int MaximumVeyonConfigBytes = 8 * 1024 * 1024;
    private const int MaximumArchiveBytes = 12 * 1024 * 1024;
    private const int MaximumProtectedBytes = 16 * 1024 * 1024;
    private const int MaximumMetadataBytes = 256 * 1024;
    private const string TemporaryConfigPrefix = ".veyon-config-";

    /// <summary>Decrypts and validates a snapshot for review by its creating Windows user.</summary>
    public static DeploymentStateBackupReviewResult ReadReview(string backupPath)
    {
        try
        {
            using var snapshot = ReadValidatedSnapshot(backupPath);
            var metadata = snapshot.Metadata;
            var lines = new List<string>
            {
                $"时间：{metadata.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
                metadata.ComputerNameBefore is { } computer
                    ? $"电脑名：{computer.ActiveName}" +
                      (string.Equals(computer.ActiveName, computer.ConfiguredName, StringComparison.OrdinalIgnoreCase)
                          ? "" : $"（待生效 {computer.ConfiguredName}）")
                    : "电脑名：未修改",
                metadata.VeyonInstalledBefore
                    ? $"Veyon：已安装 {metadata.VeyonVersionBefore ?? "未知版本"}"
                    : "Veyon：未安装",
                metadata.VeyonConfigIncluded
                    ? "已保存 Veyon 原配置。"
                    : "未保存 Veyon 原配置。"
            };
            return new DeploymentStateBackupReviewResult(true, metadata.CreatedAtUtc,
                metadata.VeyonConfigIncluded, metadata.VeyonConfigSha256, string.Join(Environment.NewLine, lines));
        }
        catch (Exception exception) when (IsSnapshotException(exception))
        {
            return new DeploymentStateBackupReviewResult(false, null, false, null,
                SnapshotFailureDetail(exception));
        }
    }

    /// <summary>Compares saved computer and account facts with current read-only Windows probes.</summary>
    public static DeploymentStateBackupSystemComparisonResult CompareCurrentSystemFacts(string backupPath,
        IProcessLauncher? launcher = null)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return new(false, "当前名称和账户比对仅支持 Windows。未修改系统状态。");
            using var snapshot = ReadValidatedSnapshot(backupPath);
            var metadata = snapshot.Metadata;
            var processLauncher = launcher ?? new DefaultProcessLauncher();
            var lines = new List<string> { "当前状态仅作只读检查；差异本身不代表部署失败或可以直接恢复。" };

            if (metadata.ComputerNameBefore is { } computerBefore)
            {
                var current = new WindowsRenameAdapter(processLauncher).ReadCurrentName();
                if (current is null)
                {
                    lines.Add("电脑名：当前活动／待生效名称读取失败，无法比较。");
                }
                else
                {
                    var matchesBefore = SameName(computerBefore.ActiveName, current.ActiveName) &&
                                        SameName(computerBefore.ConfiguredName, current.ConfiguredName);
                    var targetState = metadata.ComputerNameTarget is not { } target ? "旧快照未保存改名目标" :
                        SameName(target, current.ActiveName) ? "已是计划目标" :
                        SameName(target, current.ConfiguredName) ? "计划目标待重启生效" : "尚未达到计划目标";
                    lines.Add($"电脑名：当前活动名称 {current.ActiveName}；待生效名称 {current.ConfiguredName}；" +
                              (matchesBefore ? "与执行前名称一致" : "与执行前名称不同") + $"；{targetState}。");
                }
            }
            else
            {
                lines.Add("电脑名：本次未选择改名。");
            }

            AppendAccountComparison(lines, "学生账户", metadata.StudentAccountBefore,
                new WindowsAccountAdapter(processLauncher));
            AppendAccountComparison(lines, "管理员账户", metadata.AdminAccountBefore,
                new WindowsAccountAdapter(processLauncher));
            if (metadata.Operations.ChangeAdminPassword)
                lines.Add("管理员密码：Windows 不提供密码值读回；这里只能核对账户身份和状态，不能确认新密码是否可用。");
            return new DeploymentStateBackupSystemComparisonResult(true, string.Join(Environment.NewLine, lines));
        }
        catch (Exception exception) when (IsSnapshotException(exception))
        {
            return new DeploymentStateBackupSystemComparisonResult(false,
                "无法解密快照或读取本机状态；未修改系统状态。" + SnapshotFailureDetail(exception));
        }
    }

    /// <summary>
    /// Compares the saved Veyon configuration hash with a fresh, read-only CLI export.
    /// The temporary plaintext export is created in the protected run folder and removed.
    /// </summary>
    public static DeploymentStateBackupComparisonResult CompareCurrentVeyonConfig(string backupPath,
        IProcessLauncher? launcher = null)
    {
        string? temporaryPath = null;
        byte[]? currentConfig = null;
        try
        {
            if (!OperatingSystem.IsWindows())
                return new(false, null, "当前配置比对仅支持 Windows。未修改系统配置。");
            using var snapshot = ReadValidatedSnapshot(backupPath);
            if (!snapshot.Metadata.VeyonConfigIncluded || snapshot.VeyonConfig is null ||
                snapshot.Metadata.VeyonConfigSha256 is null)
                return new(false, null, "该快照没有 Veyon 原配置，无法进行哈希比对。未修改系统配置。");

            var runDirectory = Path.GetDirectoryName(ValidateBackupPath(backupPath))!;
            var cliPath = WindowsVeyonAdapter.ResolveVeyonCliPath();
            if (cliPath is null)
                return new(false, null, "当前电脑找不到 Veyon CLI，无法读取现有配置。未修改系统配置。");

            temporaryPath = Path.Combine(runDirectory,
                TemporaryConfigPrefix + "review-" + Guid.NewGuid().ToString("N") + ".json");
            var outcome = (launcher ?? new DefaultProcessLauncher()).Run(cliPath,
                ["config", "export", temporaryPath], Path.GetDirectoryName(cliPath)!, TimeSpan.FromSeconds(60));
            if (!outcome.Ok)
                return new(false, null, "读取当前 Veyon 配置失败；未修改系统配置。请在管理员维护页重新检查 Veyon。");

            var info = new FileInfo(temporaryPath);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                info.Length is <= 0 or > MaximumVeyonConfigBytes)
                return new(false, null, "当前 Veyon 配置导出缺失或超出安全大小限制。未修改系统配置。");
            currentConfig = File.ReadAllBytes(temporaryPath);
            if (currentConfig.Length is <= 0 or > MaximumVeyonConfigBytes)
                return new(false, null, "当前 Veyon 配置导出大小发生变化。未修改系统配置。");
            using (var document = JsonDocument.Parse(currentConfig,
                       new JsonDocumentOptions { MaxDepth = 64 }))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return new(false, null, "当前 Veyon 配置格式无法确认。未修改系统配置。");
            }

            var currentHash = SHA256.HashData(currentConfig);
            var backupHash = Convert.FromHexString(snapshot.Metadata.VeyonConfigSha256);
            var matches = CryptographicOperations.FixedTimeEquals(currentHash, backupHash);
            CryptographicOperations.ZeroMemory(currentHash);
            CryptographicOperations.ZeroMemory(backupHash);
            File.Delete(temporaryPath);
            if (File.Exists(temporaryPath))
                throw new IOException("无法删除当前 Veyon 临时配置导出文件。");
            temporaryPath = null;
            return new DeploymentStateBackupComparisonResult(true, matches, matches
                ? "当前配置与执行前快照的 SHA-256 一致；哈希相同表示文件内容一致。未修改系统配置。"
                : "当前配置与执行前快照的 SHA-256 不同；这只说明文件内容有变化，不会自动恢复。导出旧配置前请由管理员检查后续变更。未修改系统配置。");
        }
        catch (Exception exception) when (IsSnapshotException(exception) ||
                                          exception is System.ComponentModel.Win32Exception)
        {
            return new DeploymentStateBackupComparisonResult(false, null,
                "读取或校验当前配置失败；未修改系统配置。" + SnapshotFailureDetail(exception));
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            if (currentConfig is not null) CryptographicOperations.ZeroMemory(currentConfig);
        }
    }

    /// <summary>Exports the saved Veyon configuration to a new, user-selected local JSON file.</summary>
    public static DeploymentStateBackupExportResult ExportVeyonConfig(string backupPath, string destinationPath)
    {
        string? createdPath = null;
        byte[]? config = null;
        try
        {
            using var snapshot = ReadValidatedSnapshot(backupPath);
            if (!snapshot.Metadata.VeyonConfigIncluded || snapshot.VeyonConfig is null)
                return new(false, "该快照不含 Veyon 配置，无法导出。");
            if (string.IsNullOrWhiteSpace(destinationPath))
                return new(false, "请选择一个本地 JSON 文件位置。");

            var fullDestination = Path.GetFullPath(destinationPath);
            if (!Path.IsPathRooted(fullDestination) ||
                !string.Equals(Path.GetExtension(fullDestination), ".json", StringComparison.OrdinalIgnoreCase))
                return new(false, "导出位置必须是本地 .json 文件。");
            if (OperatingSystem.IsWindows() && fullDestination.StartsWith("\\\\", StringComparison.Ordinal))
                return new(false, "为保护配置文件，请先导出到本机磁盘，再由管理员决定是否转存到网络位置。");
            var internalRoot = Path.GetFullPath(DeploymentRunLog.GetDefaultRoot()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (fullDestination.StartsWith(internalRoot, comparison))
                return new(false, "请将恢复文件另存到运行记录目录以外的位置。");
            if (!Directory.Exists(Path.GetDirectoryName(fullDestination)!))
                return new(false, "导出文件夹不存在；请选择现有的本地文件夹。");
            if (File.Exists(fullDestination) || Directory.Exists(fullDestination))
                return new(false, "目标位置已存在文件或文件夹；为避免覆盖，本次没有导出。");
            var parentInfo = new DirectoryInfo(Path.GetDirectoryName(fullDestination)!);
            if ((parentInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                return new(false, "导出文件夹是重解析路径；请改选普通本地文件夹。");

            config = snapshot.VeyonConfig.ToArray();
            using (var stream = new FileStream(fullDestination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.WriteThrough))
            {
                createdPath = fullDestination;
                stream.Write(config);
                stream.Flush(flushToDisk: true);
            }
            var hash = Convert.ToHexString(SHA256.HashData(config));
            return new(true, $"执行前 Veyon 配置已导出到：{fullDestination}\nSHA-256：{hash}\n文件可能包含敏感设置。请管理员先检查它与当前配置的差异，再决定是否手动恢复；应用没有修改 Veyon 配置。");
        }
        catch (Exception exception) when (IsSnapshotException(exception))
        {
            if (createdPath is not null)
            {
                try { if (File.Exists(createdPath)) File.Delete(createdPath); }
                catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException) { }
            }
            return new DeploymentStateBackupExportResult(false, SnapshotFailureDetail(exception));
        }
        finally
        {
            if (config is not null) CryptographicOperations.ZeroMemory(config);
        }
    }

    private static ValidatedSnapshot ReadValidatedSnapshot(string backupPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("执行前快照仅支持 Windows 当前用户 DPAPI。");
        var fullPath = ValidateBackupPath(backupPath);
        byte[]? protectedFile = null;
        byte[]? ciphertext = null;
        byte[]? archiveBytes = null;
        byte[]? metadataBytes = null;
        byte[]? configBytes = null;
        try
        {
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                       64 * 1024, FileOptions.SequentialScan))
            {
                if (stream.Length <= Magic.Length || stream.Length > Magic.Length + MaximumProtectedBytes)
                    throw new InvalidDataException("加密快照大小无效。");
                protectedFile = new byte[checked((int)stream.Length)];
                stream.ReadExactly(protectedFile);
            }
            if (protectedFile.Length <= Magic.Length ||
                !protectedFile.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                throw new InvalidDataException("加密快照头无效。");

            var runId = Path.GetFileName(Path.GetDirectoryName(fullPath)!);
            ciphertext = protectedFile.AsSpan(Magic.Length).ToArray();
            var entropy = Encoding.UTF8.GetBytes("VeyonCampus/DeploymentStateBackup/v1/" + runId);
            try { archiveBytes = CurrentUserDataProtection.Unprotect(ciphertext, entropy); }
            finally { CryptographicOperations.ZeroMemory(entropy); }
            if (archiveBytes.Length is <= 0 or > MaximumArchiveBytes)
                throw new InvalidDataException("解密后的快照归档大小无效。");

            BackupMetadata metadata;
            using (var memory = new MemoryStream(archiveBytes, writable: false))
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false))
            {
                if (archive.Entries.Count is < 1 or > 2)
                    throw new InvalidDataException("快照归档包含非预期文件数量。");
                var metadataEntries = archive.Entries.Where(entry => entry.FullName == "metadata.json").ToArray();
                var configEntries = archive.Entries.Where(entry => entry.FullName == "veyon-config.json").ToArray();
                if (metadataEntries.Length != 1 || configEntries.Length > 1 ||
                    metadataEntries.Length + configEntries.Length != archive.Entries.Count)
                    throw new InvalidDataException("快照归档文件清单无效。");
                foreach (var entry in archive.Entries) ValidateArchiveEntry(entry);

                metadataBytes = ReadArchiveEntry(metadataEntries[0], MaximumMetadataBytes);
                metadata = JsonSerializer.Deserialize<BackupMetadata>(metadataBytes,
                                new JsonSerializerOptions { MaxDepth = 64 })
                            ?? throw new InvalidDataException("快照元数据为空。");
                if (configEntries.Length == 1)
                    configBytes = ReadArchiveEntry(configEntries[0], MaximumVeyonConfigBytes);
            }

            ValidateMetadata(metadata, runId, configBytes);
            var validated = new ValidatedSnapshot(metadata, configBytes);
            configBytes = null;
            return validated;
        }
        finally
        {
            if (protectedFile is not null) CryptographicOperations.ZeroMemory(protectedFile);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
            if (archiveBytes is not null) CryptographicOperations.ZeroMemory(archiveBytes);
            if (metadataBytes is not null) CryptographicOperations.ZeroMemory(metadataBytes);
            if (configBytes is not null) CryptographicOperations.ZeroMemory(configBytes);
        }
    }

    private static string ValidateBackupPath(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            throw new InvalidDataException("快照路径为空。");
        var fullPath = Path.GetFullPath(backupPath);
        if (!string.Equals(Path.GetFileName(fullPath), "pre-change-state.vcbak", StringComparison.Ordinal))
            throw new InvalidDataException("快照文件名不属于受支持的运行记录格式。");
        var runDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("快照运行目录无效。");
        _ = ValidateRunDirectory(runDirectory);
        var info = new FileInfo(fullPath);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("快照文件不存在或是重解析路径。");
        return fullPath;
    }

    private static void ValidateArchiveEntry(ZipArchiveEntry entry)
    {
        if (entry.FullName != entry.Name || entry.FullName.Contains('/') || entry.FullName.Contains('\\') ||
            entry.Name.Length > 64 || entry.Length <= 0 || entry.Length > MaximumArchiveBytes ||
            entry.CompressedLength < 0 || entry.CompressedLength > MaximumArchiveBytes)
            throw new InvalidDataException("快照归档中的文件名或大小无效。");
        var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixFileType is not 0 and not 0x8000)
            throw new InvalidDataException("快照归档不得包含目录、链接或特殊文件。");
    }

    private static byte[] ReadArchiveEntry(ZipArchiveEntry entry, int maximumBytes)
    {
        if (entry.Length <= 0 || entry.Length > maximumBytes)
            throw new InvalidDataException("快照归档成员超出大小限制。");
        using var input = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        var buffer = new byte[32 * 1024];
        try
        {
            var total = 0;
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                total = checked(total + read);
                if (total > maximumBytes)
                    throw new InvalidDataException("快照归档成员解压后超出大小限制。");
                output.Write(buffer, 0, read);
            }
            if (total != entry.Length)
                throw new InvalidDataException("快照归档成员长度校验失败。");
            return output.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (output.TryGetBuffer(out var segment))
                CryptographicOperations.ZeroMemory(segment.AsSpan());
        }
    }

    private static void ValidateMetadata(BackupMetadata metadata, string runId, byte[]? configBytes)
    {
        if (metadata.SchemaVersion is not (1 or 2) || !string.Equals(metadata.RunId, runId, StringComparison.Ordinal) ||
            metadata.CreatedAtUtc == default || metadata.CreatedAtUtc.Offset != TimeSpan.Zero ||
            string.IsNullOrEmpty(metadata.PlanId) || metadata.PlanId.Length > 128 ||
            string.IsNullOrEmpty(metadata.PlanFingerprint) || metadata.PlanFingerprint.Length > 128 || metadata.Operations is null ||
            !metadata.Operations.Any || metadata.PasswordsIncluded || metadata.PrivateKeysIncluded ||
            metadata.VeyonConfigIncluded != (configBytes is not null))
            throw new InvalidDataException("快照元数据版本、标识或安全标记无效。");

        ValidateComputerName(metadata.ComputerNameBefore);
        if (metadata.ComputerNameTarget is { } target &&
            (target.Length is < 1 or > 15 || !target.Any(char.IsAsciiLetter) ||
             !Regex.IsMatch(target, "^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?$", RegexOptions.CultureInvariant)))
            throw new InvalidDataException("快照计划电脑名无效。");
        ValidateAccountFacts(metadata.StudentAccountBefore);
        ValidateAccountFacts(metadata.AdminAccountBefore);
        if (metadata.Operations.RenameComputer != (metadata.ComputerNameBefore is not null) ||
            metadata.Operations.CreateStudent != (metadata.StudentAccountBefore is not null) ||
            metadata.Operations.ChangeAdminPassword != (metadata.AdminAccountBefore is not null))
            throw new InvalidDataException("快照的操作选择与保存状态不一致。");
        if (metadata.SchemaVersion == 2 && metadata.Operations.RenameComputer !=
            (metadata.ComputerNameTarget is not null))
            throw new InvalidDataException("快照的计划电脑名与操作选择不一致。");

        if (metadata.Operations.InstallVeyon)
        {
            if (metadata.VeyonInstalledBefore != (metadata.VeyonVersionBefore is not null) ||
                metadata.VeyonInstalledBefore != metadata.VeyonConfigIncluded)
                throw new InvalidDataException("快照中的 Veyon 安装状态不一致。");
        }
        else if (metadata.VeyonInstalledBefore || metadata.VeyonVersionBefore is not null || metadata.VeyonConfigIncluded)
            throw new InvalidDataException("快照记录了未选择的 Veyon 操作状态。");

        if (metadata.VeyonVersionBefore is { Length: > 80 } || metadata.VeyonConfigIncluded != (metadata.VeyonConfigSha256 is not null))
            throw new InvalidDataException("快照中的 Veyon 版本或摘要无效。");
        if (metadata.VeyonConfigSha256 is not null)
        {
            if (!RegexSha256.IsMatch(metadata.VeyonConfigSha256) || configBytes is null)
                throw new InvalidDataException("快照配置摘要格式无效。");
            var actualHash = SHA256.HashData(configBytes);
            var expectedHash = Convert.FromHexString(metadata.VeyonConfigSha256);
            var matches = CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
            CryptographicOperations.ZeroMemory(actualHash);
            CryptographicOperations.ZeroMemory(expectedHash);
            if (!matches)
                throw new InvalidDataException("快照配置摘要校验失败。");
        }
        if (configBytes is not null)
        {
            using var document = JsonDocument.Parse(configBytes, new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("快照中的 Veyon 配置格式无效。");
        }
    }

    private static void ValidateComputerName(ComputerNameState? value)
    {
        if (value is not null && (string.IsNullOrWhiteSpace(value.ActiveName) || value.ActiveName.Length > 15 ||
                                  string.IsNullOrWhiteSpace(value.ConfiguredName) || value.ConfiguredName.Length > 15))
            throw new InvalidDataException("快照电脑名称无效。");
    }

    private static void ValidateAccountFacts(LocalAccountFacts? value)
    {
        if (value is null) return;
        if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 20 ||
            value.PrincipalSource is { Length: > 40 } ||
            (value.Exists && (value.Sid is null || !RegexSidPattern.IsMatch(value.Sid))) ||
            (!value.Exists && value.Sid is not null))
            throw new InvalidDataException("快照账户状态无效。");
    }

    private static void AppendAccountComparison(ICollection<string> lines, string label,
        LocalAccountFacts? before, WindowsAccountAdapter adapter)
    {
        if (before is null)
        {
            lines.Add($"{label}：本次未选择账户操作。");
            return;
        }
        var current = adapter.ReadLocalAccountFacts(before.Name);
        if (current is null)
        {
            lines.Add($"{label}“{before.Name}”：当前账户或组状态读取失败，无法比较。");
            return;
        }
        var matches = before.Exists == current.Exists &&
                      (!before.Exists || (SameSid(before.Sid, current.Sid) &&
                       string.Equals(before.PrincipalSource, current.PrincipalSource, StringComparison.OrdinalIgnoreCase) &&
                       before.Enabled == current.Enabled && before.IsAdministrator == current.IsAdministrator &&
                       before.IsUsersMember == current.IsUsersMember && before.HasOtherLocalGroups == current.HasOtherLocalGroups));
        var currentState = current.Exists
            ? $"当前{(current.Enabled == true ? "已启用" : current.Enabled == false ? "已禁用" : "启用状态未知")}，" +
              (current.IsAdministrator == true ? "管理员组成员" : current.IsAdministrator == false ? "非管理员账户" : "权限组未知")
            : "当前不存在";
        lines.Add($"{label}“{before.Name}”：执行前{(before.Exists ? "存在" : "不存在")}，{currentState}；" +
                  (matches ? "身份和可读状态与执行前一致。" : "身份或可读状态与执行前不同。"));
    }

    private static bool SameSid(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool SameName(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsSnapshotException(Exception exception) => exception is
        UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException or
        System.ComponentModel.Win32Exception or PlatformNotSupportedException or OverflowException or
        ArgumentException or InvalidOperationException or NotSupportedException or IOException;

    private static string SnapshotFailureDetail(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "当前用户无权访问执行前快照。",
        CryptographicException => "Windows 当前用户 DPAPI 无法解密此快照；请使用创建快照的 Windows 用户。",
        PlatformNotSupportedException => "执行前快照只支持 Windows 当前用户 DPAPI。",
        JsonException => "快照元数据或配置 JSON 校验失败。",
        InvalidDataException => "快照文件、路径或内容校验失败。",
        IOException => "无法读取或写入快照文件。",
        _ => "快照格式或路径无效。"
    };

    private sealed class ValidatedSnapshot(BackupMetadata metadata, byte[]? veyonConfig) : IDisposable
    {
        public BackupMetadata Metadata { get; } = metadata;
        public byte[]? VeyonConfig { get; } = veyonConfig;

        public void Dispose()
        {
            if (VeyonConfig is not null) CryptographicOperations.ZeroMemory(VeyonConfig);
        }
    }

    public static DeploymentStateBackupResult Capture(ExecutionPlan plan, string runDirectory,
        IProcessLauncher? launcher = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!OperatingSystem.IsWindows())
            return Failed("执行前快照仅支持 Windows；系统修改已阻断。");

        byte[]? veyonConfig = null;
        byte[]? archiveBytes = null;
        byte[]? protectedBytes = null;
        var configTempPath = (string?)null;
        var stagingPath = (string?)null;
        var backupPath = (string?)null;
        var backupCreatedByThisCall = false;
        byte[]? protectedFile = null;
        try
        {
            var safeRunDirectory = ValidateRunDirectory(runDirectory);
            if (!TryCleanupStaleTemporaryExports(Path.GetDirectoryName(safeRunDirectory)!))
                return Failed("无法清理此前中断的临时配置导出文件；系统修改已阻断。");
            var processLauncher = launcher ?? new DefaultProcessLauncher();

            var computerName = plan.Input.Operations.RenameComputer
                ? new WindowsRenameAdapter(processLauncher).ReadCurrentName()
                : null;
            if (plan.Input.Operations.RenameComputer && computerName is null)
                return Failed("无法保存当前活动名称和待生效名称；系统修改已阻断。");

            LocalAccountFacts? studentAccount = null;
            LocalAccountFacts? adminAccount = null;
            if (plan.Input.Operations.CreateStudent || plan.Input.Operations.ChangeAdminPassword)
            {
                var accountAdapter = new WindowsAccountAdapter(processLauncher);
                if (plan.Input.Operations.CreateStudent)
                {
                    if (plan.Accounts?.StudentAccountName != plan.Input.StudentAccountName)
                        return Failed("学生账户的冻结身份信息缺失；系统修改已阻断。");
                    studentAccount = accountAdapter.ReadLocalAccountFacts(plan.Input.StudentAccountName);
                    if (!MatchesStudentSnapshot(studentAccount, plan.Accounts.StudentSid))
                        return Failed("学生账户当前状态与确认计划不一致；系统修改已阻断。");
                }
                if (plan.Input.Operations.ChangeAdminPassword)
                {
                    if (plan.Accounts?.AdminAccountName != plan.Input.AdminAccountName ||
                        string.IsNullOrWhiteSpace(plan.Accounts.AdminSid))
                        return Failed("管理员账户的冻结身份信息缺失；系统修改已阻断。");
                    adminAccount = accountAdapter.ReadLocalAccountFacts(plan.Input.AdminAccountName);
                    if (!MatchesAdminSnapshot(adminAccount, plan.Accounts.AdminSid))
                        return Failed("管理员账户当前状态与确认计划不一致；系统修改已阻断。");
                }
            }

            var veyonFacts = plan.Input.Operations.InstallVeyon ? VeyonFacts.Probe() : null;
            var veyonConfigSha256 = (string?)null;
            var veyonConfigBackedUp = false;
            if (veyonFacts is not null)
            {
                if (veyonFacts.Status == "unknown")
                    return Failed("Veyon 安装状态未知，无法安全建立原配置快照；系统修改已阻断。");
                if (veyonFacts.Status == "installed")
                {
                    if (!VeyonFacts.IsSupportedVersionDetail(veyonFacts.VersionDetail))
                        return Failed("现有 Veyon 版本不符合固定基线，无法建立受支持的配置快照；系统修改已阻断。");
                    var cliPath = WindowsVeyonAdapter.ResolveVeyonCliPath();
                    if (cliPath is null)
                        return Failed("已检测到 Veyon，但找不到用于导出配置的 CLI；系统修改已阻断。");

                    configTempPath = Path.Combine(safeRunDirectory,
                        TemporaryConfigPrefix + Guid.NewGuid().ToString("N") + ".json");
                    var export = processLauncher.Run(cliPath, ["config", "export", configTempPath],
                        Path.GetDirectoryName(cliPath)!, TimeSpan.FromSeconds(60));
                    if (!export.Ok)
                        return Failed("Veyon 配置导出没有返回成功；系统修改已阻断。");

                    var fileInfo = new FileInfo(configTempPath);
                    if (!fileInfo.Exists || (fileInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
                        fileInfo.Length is <= 0 or > MaximumVeyonConfigBytes)
                        return Failed("Veyon 配置导出文件缺失、异常或超出安全大小限制；系统修改已阻断。");
                    veyonConfig = File.ReadAllBytes(configTempPath);
                    if (veyonConfig.Length is <= 0 or > MaximumVeyonConfigBytes)
                        return Failed("Veyon 配置导出文件大小发生变化；系统修改已阻断。");
                    using (var document = JsonDocument.Parse(veyonConfig,
                               new JsonDocumentOptions { MaxDepth = 64 }))
                    {
                        if (document.RootElement.ValueKind != JsonValueKind.Object)
                            return Failed("Veyon 配置导出格式无法确认；系统修改已阻断。");
                    }
                    veyonConfigSha256 = Convert.ToHexString(SHA256.HashData(veyonConfig));
                    veyonConfigBackedUp = true;
                    File.Delete(configTempPath);
                    if (File.Exists(configTempPath))
                        throw new IOException("无法删除明文 Veyon 临时导出文件。");
                    configTempPath = null;
                }
                else if (veyonFacts.Status != VeyonFacts.NotInstalled)
                    return Failed("Veyon 安装状态无法确认；系统修改已阻断。");
            }

            var metadata = new BackupMetadata(
                SchemaVersion: 2,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                RunId: Path.GetFileName(safeRunDirectory),
                PlanId: plan.PlanId,
                PlanFingerprint: plan.PlanFingerprint,
                Operations: plan.Input.Operations,
                ComputerNameBefore: computerName,
                StudentAccountBefore: studentAccount,
                AdminAccountBefore: adminAccount,
                VeyonInstalledBefore: veyonFacts?.Status == "installed",
                VeyonVersionBefore: veyonFacts?.VersionDetail,
                VeyonConfigIncluded: veyonConfigBackedUp,
                VeyonConfigSha256: veyonConfigSha256,
                PasswordsIncluded: false,
                PrivateKeysIncluded: false,
                ComputerNameTarget: plan.Input.Operations.RenameComputer
                    ? MachineNaming.CreateName(plan.Input.Prefix, plan.Input.Number) : null);

            archiveBytes = BuildArchive(metadata, veyonConfig);
            if (archiveBytes.Length is <= 0 or > MaximumArchiveBytes)
                return Failed("加密快照内容超出安全大小限制；系统修改已阻断。");

            var entropy = Encoding.UTF8.GetBytes("VeyonCampus/DeploymentStateBackup/v1/" + metadata.RunId);
            try { protectedBytes = CurrentUserDataProtection.Protect(archiveBytes, entropy); }
            finally { CryptographicOperations.ZeroMemory(entropy); }
            if (protectedBytes.Length is <= 0 or > MaximumProtectedBytes)
                return Failed("Windows 加密快照无效或超出安全大小限制；系统修改已阻断。");

            protectedFile = new byte[Magic.Length + protectedBytes.Length];
            Magic.CopyTo(protectedFile, 0);
            protectedBytes.CopyTo(protectedFile, Magic.Length);
            backupPath = Path.Combine(safeRunDirectory, "pre-change-state.vcbak");
            stagingPath = backupPath + ".tmp-" + Guid.NewGuid().ToString("N");
            WriteNewFile(stagingPath, protectedFile);
            File.Move(stagingPath, backupPath, overwrite: false);
            stagingPath = null;
            backupCreatedByThisCall = true;

            // Read back and decrypt once before allowing any system modification.
            var persistedInfo = new FileInfo(backupPath);
            if (!persistedInfo.Exists || (persistedInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
                persistedInfo.Length > Magic.Length + MaximumProtectedBytes)
                throw new InvalidDataException("快照文件大小或类型无效。");
            var persisted = File.ReadAllBytes(backupPath);
            if (persisted.Length <= Magic.Length || !persisted.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                throw new InvalidDataException("快照头无效。");
            var persistedCiphertext = persisted.AsSpan(Magic.Length).ToArray();
            byte[]? persistedPlaintext = null;
            var verifyEntropy = Encoding.UTF8.GetBytes("VeyonCampus/DeploymentStateBackup/v1/" + metadata.RunId);
            try
            {
                persistedPlaintext = CurrentUserDataProtection.Unprotect(persistedCiphertext, verifyEntropy);
                if (!CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(persistedPlaintext), SHA256.HashData(archiveBytes)))
                    throw new InvalidDataException("快照读回校验不一致。");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(verifyEntropy);
                CryptographicOperations.ZeroMemory(persisted);
                CryptographicOperations.ZeroMemory(persistedCiphertext);
                if (persistedPlaintext is not null) CryptographicOperations.ZeroMemory(persistedPlaintext);
            }

            var limitations = new List<string>();
            if (plan.Input.Operations.ChangeAdminPassword)
                limitations.Add("管理员密码不保存，也不能自动回滚");
            if (plan.Input.Operations.CreateStudent)
                limitations.Add("不会自动删除已创建的账户或用户数据");
            var suffix = limitations.Count == 0 ? "" : "；" + string.Join("；", limitations);
            var configText = veyonConfigBackedUp ? "已包括现有 Veyon 配置" :
                veyonFacts?.Status == VeyonFacts.NotInstalled ? "Veyon 尚未安装，无现有配置可导出" : "未选择 Veyon 修改";
            return new DeploymentStateBackupResult(true, backupPath,
                $"执行前状态快照已加密保存并读回校验（{configText}）；不会单独保存表单输入密码或 Veyon 私钥文件，原 Veyon 配置可能含敏感设置{suffix}。需要恢复时须由管理员检查快照并手动处理，不会自动回滚。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           CryptographicException or InvalidOperationException or
                                           ArgumentException or System.ComponentModel.Win32Exception or JsonException)
        {
            if (backupCreatedByThisCall && backupPath is not null)
            {
                try { if (File.Exists(backupPath)) File.Delete(backupPath); }
                catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException) { }
            }
            var reason = exception switch
            {
                UnauthorizedAccessException => "当前用户无权访问快照目录",
                CryptographicException => "Windows 当前用户加密或读回校验失败",
                JsonException => "Veyon 配置导出不是有效 JSON",
                InvalidDataException => "快照路径或内容校验失败",
                IOException => "无法读写快照文件或删除临时导出文件",
                System.ComponentModel.Win32Exception => "Windows 系统状态读取失败",
                _ => "快照格式或路径无效"
            };
            return Failed($"{reason}；所有系统修改已阻断。");
        }
        finally
        {
            if (configTempPath is not null)
            {
                try
                {
                    if (File.Exists(configTempPath)) File.Delete(configTempPath);
                }
                catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
                {
                    // Capture already fails before any system mutation when it cannot
                    // complete the encrypted snapshot. Startup cleanup retries this file.
                }
            }
            if (stagingPath is not null)
            {
                try { if (File.Exists(stagingPath)) File.Delete(stagingPath); }
                catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException) { }
            }
            if (veyonConfig is not null) CryptographicOperations.ZeroMemory(veyonConfig);
            if (archiveBytes is not null) CryptographicOperations.ZeroMemory(archiveBytes);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
            if (protectedFile is not null) CryptographicOperations.ZeroMemory(protectedFile);
        }
    }

    /// <summary>Removes only orphaned Veyon export files from valid app-owned run folders.</summary>
    public static void CleanupStaleTemporaryExports(string root)
        => _ = TryCleanupStaleTemporaryExports(root);

    private static bool TryCleanupStaleTemporaryExports(string root)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(root)) return true;
        string fullRoot;
        try { fullRoot = Path.GetFullPath(root); }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { return false; }

        IEnumerable<string> directories;
        try { directories = Directory.EnumerateDirectories(fullRoot).ToArray(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }

        var succeeded = true;
        foreach (var directory in directories)
        {
            try
            {
                var info = new DirectoryInfo(directory);
                if (!Guid.TryParseExact(info.Name, "N", out _)) continue;
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    succeeded = false;
                    continue;
                }
                foreach (var file in Directory.EnumerateFiles(info.FullName, TemporaryConfigPrefix + "*.json"))
                {
                    var fileInfo = new FileInfo(file);
                    if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        succeeded = false;
                        continue;
                    }
                    File.Delete(file);
                    if (File.Exists(file)) succeeded = false;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                succeeded = false;
            }
        }
        return succeeded;
    }

    private static string ValidateRunDirectory(string runDirectory)
    {
        var fullRoot = Path.GetFullPath(DeploymentRunLog.GetDefaultRoot());
        var fullDirectory = Path.GetFullPath(runDirectory);
        var separator = Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(fullDirectory), fullRoot.TrimEnd(separator), comparison) ||
            !Guid.TryParseExact(Path.GetFileName(fullDirectory), "N", out _))
            throw new InvalidDataException("运行目录不属于 VeyonCampus 当前用户记录目录。");

        var rootInfo = new DirectoryInfo(fullRoot);
        var directoryInfo = new DirectoryInfo(fullDirectory);
        var ownerDirectoryInfo = new DirectoryInfo(Path.GetDirectoryName(fullRoot)!);
        if (!rootInfo.Exists || !directoryInfo.Exists ||
            (rootInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
            (ownerDirectoryInfo.Exists && (ownerDirectoryInfo.Attributes & FileAttributes.ReparsePoint) != 0) ||
            (directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("运行目录不存在或是重解析路径。");
        return fullDirectory;
    }

    private static bool MatchesStudentSnapshot(LocalAccountFacts? facts, string? expectedSid)
    {
        if (facts is null) return false;
        if (expectedSid is null) return !facts.Exists && facts.Sid is null;
        return facts.Exists && string.Equals(facts.Sid, expectedSid, StringComparison.OrdinalIgnoreCase) &&
               facts.PrincipalSource == "Local" && facts.Enabled == true && facts.IsAdministrator == false &&
               facts.IsUsersMember == true && facts.HasOtherLocalGroups == false;
    }

    private static bool MatchesAdminSnapshot(LocalAccountFacts? facts, string expectedSid) =>
        facts is { Exists: true, Enabled: true, IsAdministrator: true } &&
        facts.PrincipalSource == "Local" &&
        string.Equals(facts.Sid, expectedSid, StringComparison.OrdinalIgnoreCase) &&
        RegexSidPattern.IsMatch(expectedSid);

    private static byte[] BuildArchive(BackupMetadata metadata, byte[]? veyonConfig)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var metadataEntry = archive.CreateEntry("metadata.json", CompressionLevel.Optimal);
            using (var stream = metadataEntry.Open())
                JsonSerializer.Serialize(stream, metadata, new JsonSerializerOptions { WriteIndented = true });
            if (veyonConfig is not null)
            {
                var configEntry = archive.CreateEntry("veyon-config.json", CompressionLevel.Optimal);
                using var stream = configEntry.Open();
                stream.Write(veyonConfig);
            }
        }
        return memory.ToArray();
    }

    private static void WriteNewFile(string path, byte[] contents)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.WriteThrough);
        stream.Write(contents);
        stream.Flush(flushToDisk: true);
    }

    private static DeploymentStateBackupResult Failed(string detail) => new(false, null, detail);

    private sealed record BackupMetadata(int SchemaVersion, DateTimeOffset CreatedAtUtc, string RunId,
        string PlanId, string PlanFingerprint, OperationSelection Operations,
        ComputerNameState? ComputerNameBefore, LocalAccountFacts? StudentAccountBefore,
        LocalAccountFacts? AdminAccountBefore, bool VeyonInstalledBefore, string? VeyonVersionBefore,
        bool VeyonConfigIncluded, string? VeyonConfigSha256, bool PasswordsIncluded, bool PrivateKeysIncluded,
        string? ComputerNameTarget = null);

    private static partial class RegexSidPattern
    {
        [System.Text.RegularExpressions.GeneratedRegex(@"^S-1-5-21-\d+-\d+-\d+-\d+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
        private static partial System.Text.RegularExpressions.Regex SidRegex();
        public static bool IsMatch(string value) => SidRegex().IsMatch(value);
    }

    private static partial class RegexSha256
    {
        [System.Text.RegularExpressions.GeneratedRegex(@"^[0-9A-Fa-f]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
        private static partial System.Text.RegularExpressions.Regex Sha256Regex();
        public static bool IsMatch(string value) => Sha256Regex().IsMatch(value);
    }

    private static class CurrentUserDataProtection
    {
        private const int UiForbidden = 0x1;

        public static byte[] Protect(byte[] plaintext, byte[] entropy) => Transform(plaintext, entropy, protect: true);
        public static byte[] Unprotect(byte[] ciphertext, byte[] entropy) => Transform(ciphertext, entropy, protect: false);

        private static byte[] Transform(byte[] input, byte[] entropy, bool protect)
        {
            DataBlob inputBlob = default;
            DataBlob entropyBlob = default;
            DataBlob outputBlob = default;
            IntPtr description = IntPtr.Zero;
            try
            {
                inputBlob = Allocate(input);
                entropyBlob = Allocate(entropy);
                var succeeded = protect
                    ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                        UiForbidden, out outputBlob)
                    : CryptUnprotectData(ref inputBlob, out description, ref entropyBlob, IntPtr.Zero,
                        IntPtr.Zero, UiForbidden, out outputBlob);
                if (!succeeded)
                    throw new CryptographicException(Marshal.GetLastWin32Error());
                if (outputBlob.Length <= 0 || outputBlob.Length > MaximumProtectedBytes)
                    throw new CryptographicException("Windows returned an invalid protected-data length.");
                var result = new byte[outputBlob.Length];
                Marshal.Copy(outputBlob.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                FreeInput(ref inputBlob);
                FreeInput(ref entropyBlob);
                if (outputBlob.Data != IntPtr.Zero) _ = LocalFree(outputBlob.Data);
                if (description != IntPtr.Zero) _ = LocalFree(description);
            }
        }

        private static DataBlob Allocate(byte[] value)
        {
            var memory = Marshal.AllocHGlobal(value.Length);
            Marshal.Copy(value, 0, memory, value.Length);
            return new DataBlob { Length = value.Length, Data = memory };
        }

        private static void FreeInput(ref DataBlob blob)
        {
            if (blob.Data == IntPtr.Zero) return;
            for (var offset = 0; offset < blob.Length; offset += sizeof(long))
            {
                var count = Math.Min(sizeof(long), blob.Length - offset);
                for (var index = 0; index < count; index++) Marshal.WriteByte(blob.Data, offset + index, 0);
            }
            Marshal.FreeHGlobal(blob.Data);
            blob = default;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Length;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref DataBlob input, string? description,
            ref DataBlob optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description,
            ref DataBlob optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
