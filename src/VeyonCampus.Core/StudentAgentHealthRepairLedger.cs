using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

/// <summary>Stores a small, protected, local idempotency and cooldown ledger for fixed Agent repairs.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class StudentAgentHealthRepairLedger
{
    public const int MaximumEntries = 64;
    public static readonly TimeSpan Cooldown = StudentAgentHealthRepairLedgerPolicy.Cooldown;
    private const int SchemaVersion = 1;
    private const int MaximumBytes = 32 * 1024;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    private readonly string _configDirectory;
    private readonly string _directory;
    private readonly string _path;
    private readonly string _legacyPath;

    public StudentAgentHealthRepairLedger(string configPath)
    {
        var fullConfigPath = Path.GetFullPath(configPath);
        _configDirectory = Path.GetDirectoryName(fullConfigPath)
                           ?? throw new InvalidDataException("Agent 健康修复状态目录无效。");
        PathLinkSecurity.RejectLinks(_configDirectory);
        _directory = Path.Combine(_configDirectory, "health-repair");
        _path = Path.Combine(_directory, "health-repair-ledger.json");
        _legacyPath = Path.Combine(_configDirectory, "health-repair-ledger.json");
    }

    public StudentAgentHealthRepairLedgerEntry? FindCommand(Guid commandId)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("修复命令编号无效。", nameof(commandId));
        lock (Gate) return ReadEntries().LastOrDefault(entry => entry.CommandId == commandId);
    }

    public bool IsCoolingDown(StudentAgentHealthRepairAction action, DateTimeOffset nowUtc)
    {
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        lock (Gate) return StudentAgentHealthRepairLedgerPolicy.IsCoolingDown(ReadEntries(), action, nowUtc);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Record(StudentAgentHealthRepairReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var entry = new StudentAgentHealthRepairLedgerEntry(receipt.CommandId, receipt.Action,
            receipt.Outcome, receipt.WebsiteRevision, receipt.CompletedUtc.ToUniversalTime());
        ValidateEntry(entry);
        lock (Gate)
        {
            var entries = ReadEntries();
            if (entries.Any(existing => existing.CommandId == entry.CommandId))
                throw new InvalidDataException("健康修复命令编号已记录。");
            entries.Add(entry);
            if (entries.Count > MaximumEntries) entries.RemoveRange(0, entries.Count - MaximumEntries);
            WriteEntries(entries);
        }
    }

    private List<StudentAgentHealthRepairLedgerEntry> ReadEntries()
    {
        EnsureProtectedDirectory();
        if (!File.Exists(_path)) return [];
        PathLinkSecurity.RejectLinks(_path);
        var info = new FileInfo(_path);
        if (info.Length is <= 0 or > MaximumBytes || !AgentFileSecurity.IsPrivate(_path, directory: false))
            throw new InvalidDataException("健康修复记录大小或 ACL 无效；已停止执行修复。");
        var bytes = File.ReadAllBytes(_path);
        PolicyJson.RejectDuplicateFields(bytes);
        var document = JsonSerializer.Deserialize<StudentAgentHealthRepairLedgerDocument>(bytes, JsonOptions)
                       ?? throw new InvalidDataException("健康修复记录为空。");
        if (document.SchemaVersion != SchemaVersion || document.Entries is null ||
            document.Entries.Count > MaximumEntries ||
            document.Entries.Select(entry => entry.CommandId).Distinct().Count() != document.Entries.Count)
            throw new InvalidDataException("健康修复记录 schema 或条目数量无效。");
        foreach (var entry in document.Entries) ValidateEntry(entry);
        return document.Entries.ToList();
    }

    private void WriteEntries(IReadOnlyList<StudentAgentHealthRepairLedgerEntry> entries)
    {
        EnsureProtectedDirectory();
        if (File.Exists(_path)) PathLinkSecurity.RejectLinks(_path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new StudentAgentHealthRepairLedgerDocument(SchemaVersion, entries), JsonOptions);
        if (bytes.Length is 0 or > MaximumBytes)
            throw new IOException("健康修复记录超过大小上限；本次修复未被记录。");
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            AgentFileSecurity.SecurePrivateFile(temporary);
            File.Move(temporary, _path, overwrite: true);
            AgentFileSecurity.SecurePrivateFile(_path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private void EnsureProtectedDirectory()
    {
        if (!Directory.Exists(_configDirectory))
            throw new DirectoryNotFoundException("Agent 配置目录不存在。");
        PathLinkSecurity.RejectLinks(_configDirectory);

        if (Directory.Exists(_directory))
        {
            PathLinkSecurity.RejectLinks(_directory);
            if (!AgentFileSecurity.IsPrivate(_directory, directory: true))
                throw new InvalidDataException("Agent 健康修复状态目录 ACL 无效；已停止执行修复。");
        }
        else
        {
            Directory.CreateDirectory(_directory);
            AgentFileSecurity.SecurePrivateDirectory(_directory);
        }

        PathLinkSecurity.RejectLinks(_directory);
        if (!AgentFileSecurity.IsPrivate(_directory, directory: true))
            throw new InvalidDataException("Agent 健康修复状态目录 ACL 无效；已停止执行修复。");

        if (!File.Exists(_legacyPath)) return;
        PathLinkSecurity.RejectLinks(_legacyPath);
        if (!AgentFileSecurity.IsPrivate(_legacyPath, directory: false))
            throw new InvalidDataException("旧版 Agent 健康修复记录 ACL 无效；已停止执行修复。");
        if (File.Exists(_path))
            throw new InvalidDataException("新旧 Agent 健康修复记录同时存在；已停止执行修复。");
        File.Move(_legacyPath, _path);
        PathLinkSecurity.RejectLinks(_path);
        if (!AgentFileSecurity.IsPrivate(_path, directory: false))
            throw new InvalidDataException("迁移后的 Agent 健康修复记录 ACL 无效；已停止执行修复。");
    }

    private static void ValidateEntry(StudentAgentHealthRepairLedgerEntry entry)
    {
        if (entry is null || entry.CommandId == Guid.Empty || !Enum.IsDefined(entry.Action) ||
            !Enum.IsDefined(entry.Outcome) || entry.CompletedUtc.Offset != TimeSpan.Zero ||
            (entry.Action == StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy
                ? entry.WebsiteRevision is null or <= 0
                : entry.WebsiteRevision is not null))
            throw new InvalidDataException("健康修复记录条目无效。");
    }
}

public sealed record StudentAgentHealthRepairLedgerEntry(
    Guid CommandId,
    StudentAgentHealthRepairAction Action,
    StudentAgentHealthRepairOutcome Outcome,
    long? WebsiteRevision,
    DateTimeOffset CompletedUtc);

public sealed record StudentAgentHealthRepairLedgerDocument(
    int SchemaVersion,
    IReadOnlyList<StudentAgentHealthRepairLedgerEntry> Entries);

public static class StudentAgentHealthRepairProcessor
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static StudentAgentHealthRepairReceipt Process(
        StudentAgentHealthRepairCommand command,
        string configPath,
        WebsitePolicyAgentConfig config,
        StudentAgentEdgePolicyRefreshSignalState edgePolicyRefreshSignal,
        Action<StudentAgentHealthRepairProgressPhase>? reportProgress = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(edgePolicyRefreshSignal);
        var now = DateTimeOffset.UtcNow;
        var ledger = new StudentAgentHealthRepairLedger(configPath);
        var previous = ledger.FindCommand(command.CommandId);
        if (command.ExpiresUtc <= now)
            return CreateReceipt(command, StudentAgentHealthRepairOutcome.Expired, now);
        if (previous is not null)
        {
            var matches = previous.Action == command.Action &&
                (command.Action == StudentAgentHealthRepairAction.EnsureVeyonServiceRunning ||
                 previous.WebsiteRevision == command.ExpectedWebsiteRevision);
            return CreateReceipt(command, matches ? previous.Outcome :
                StudentAgentHealthRepairOutcome.ReplayRejected, now, wasAlreadyProcessed: true);
        }

        try
        {
            using var updateLock = StudentUpdateAtomicTransaction.AcquireMachineLock(TimeSpan.Zero);
            if (command.ExpiresUtc <= DateTimeOffset.UtcNow)
                return CreateReceipt(command, StudentAgentHealthRepairOutcome.Expired, DateTimeOffset.UtcNow);
            var transactionState = StudentUpdateAtomicTransaction.ReadCurrentState(configPath);
            if (transactionState is "inProgress" or "needsReview")
                return Record(command, StudentAgentHealthRepairOutcome.NeedsManualReview, now, ledger);
            if (ledger.IsCoolingDown(command.Action, now))
                return CreateReceipt(command, StudentAgentHealthRepairOutcome.CoolingDown, now);
            var unavailableOutcome = GetUnavailableOutcome(command, config, now);
            if (unavailableOutcome is { } unavailable)
            {
                if (command.ExpiresUtc <= DateTimeOffset.UtcNow)
                    return CreateReceipt(command, StudentAgentHealthRepairOutcome.Expired, DateTimeOffset.UtcNow);
                return Record(command, unavailable, DateTimeOffset.UtcNow, ledger);
            }
            if (command.ExpiresUtc <= DateTimeOffset.UtcNow)
                return CreateReceipt(command, StudentAgentHealthRepairOutcome.Expired, DateTimeOffset.UtcNow);

            reportProgress?.Invoke(StudentAgentHealthRepairProgressPhase.Executing);

            var outcome = command.Action switch
            {
                StudentAgentHealthRepairAction.EnsureVeyonServiceRunning =>
                    EnsureVeyonServiceRunning(),
                StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy =>
                    QueueActivePolicyRefresh(command, config, edgePolicyRefreshSignal, now),
                _ => StudentAgentHealthRepairOutcome.NeedsManualReview
            };
            return Record(command, outcome, DateTimeOffset.UtcNow, ledger);
        }
        catch (StudentUpdateLockBusyException)
        {
            // The machine mutex is also held by the updater and recovery helper.
            return CreateReceipt(command, StudentAgentHealthRepairOutcome.BusyUpdating, DateTimeOffset.UtcNow);
        }
        catch (UnauthorizedAccessException)
        {
            return Record(command, StudentAgentHealthRepairOutcome.NeedsManualReview,
                DateTimeOffset.UtcNow, ledger);
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                          IOException or System.ComponentModel.Win32Exception or TimeoutException or
                                          System.Security.SecurityException)
        {
            return Record(command, StudentAgentHealthRepairOutcome.Failed,
                DateTimeOffset.UtcNow, ledger);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static StudentAgentHealthRepairOutcome EnsureVeyonServiceRunning()
    {
        var initial = StudentVeyonServiceInspector.ReadState();
        if (initial == StudentVeyonServiceState.Running) return StudentAgentHealthRepairOutcome.AlreadyHealthy;
        if (initial is StudentVeyonServiceState.NotInstalled or StudentVeyonServiceState.Unknown)
            return StudentAgentHealthRepairOutcome.NotAvailable;
        return StudentVeyonServiceInspector.EnsureRunning() == StudentVeyonServiceState.Running
            ? StudentAgentHealthRepairOutcome.Succeeded
            : StudentAgentHealthRepairOutcome.Failed;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static StudentAgentHealthRepairOutcome? GetUnavailableOutcome(
        StudentAgentHealthRepairCommand command, WebsitePolicyAgentConfig config, DateTimeOffset nowUtc)
    {
        if (command.Action == StudentAgentHealthRepairAction.EnsureVeyonServiceRunning)
        {
            var state = StudentVeyonServiceInspector.ReadState();
            return state switch
            {
                StudentVeyonServiceState.Running => StudentAgentHealthRepairOutcome.AlreadyHealthy,
                StudentVeyonServiceState.NotInstalled or StudentVeyonServiceState.Unknown =>
                    StudentAgentHealthRepairOutcome.NotAvailable,
                _ => null
            };
        }

        var website = WebsitePolicyRegistryStore.ReadStatus(config.CampusId, nowUtc);
        return website.Revision != command.ExpectedWebsiteRevision ||
               website.Mode == WebsitePolicyMode.Disabled || website.ExpiredUtc is not null ||
               website.ExpiresUtc is { } expiry && expiry <= nowUtc ||
               WebsitePolicyRegistryStore.ReadEdgeExecutionMode(config.CampusId) !=
               EdgeWebsitePolicyExecutionMode.LegacyRegistry
            ? StudentAgentHealthRepairOutcome.NotAvailable
            : null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static StudentAgentHealthRepairOutcome QueueActivePolicyRefresh(
        StudentAgentHealthRepairCommand command,
        WebsitePolicyAgentConfig config,
        StudentAgentEdgePolicyRefreshSignalState edgePolicyRefreshSignal,
        DateTimeOffset nowUtc)
    {
        var website = WebsitePolicyRegistryStore.ReadStatus(config.CampusId, nowUtc);
        if (website.Revision != command.ExpectedWebsiteRevision ||
            website.Mode == WebsitePolicyMode.Disabled ||
            website.ExpiredUtc is not null ||
            website.ExpiresUtc is { } expiry && expiry <= nowUtc ||
            WebsitePolicyRegistryStore.ReadEdgeExecutionMode(config.CampusId) !=
            EdgeWebsitePolicyExecutionMode.LegacyRegistry)
            return StudentAgentHealthRepairOutcome.NotAvailable;

        edgePolicyRefreshSignal.Request(website.Revision, nowUtc, force: true);
        return StudentAgentHealthRepairOutcome.Queued;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static StudentAgentHealthRepairReceipt Record(StudentAgentHealthRepairCommand command,
        StudentAgentHealthRepairOutcome outcome, DateTimeOffset completedUtc,
        StudentAgentHealthRepairLedger ledger)
    {
        var receipt = CreateReceipt(command, outcome, completedUtc);
        if (StudentAgentHealthRepairLedgerPolicy.ShouldPersistOutcome(outcome))
            ledger.Record(receipt);
        return receipt;
    }

    private static StudentAgentHealthRepairReceipt CreateReceipt(StudentAgentHealthRepairCommand command,
        StudentAgentHealthRepairOutcome outcome, DateTimeOffset completedUtc, bool wasAlreadyProcessed = false) =>
        new(1, StudentAgentHealthRepairCommandCryptography.ReceiptPurpose, command.CampusId,
            command.CommandId, command.Nonce, command.Action, outcome,
            command.Action == StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy
                ? command.ExpectedWebsiteRevision
                : null,
            completedUtc.ToUniversalTime(), wasAlreadyProcessed);
}
