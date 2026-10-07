using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace VeyonCampus.Core;

internal enum WebsitePolicyRegistryTransactionOperation
{
    Apply,
    Expire,
    Remove
}

internal sealed record WebsitePolicyRegistryValueSnapshot(bool Exists, string? Value);

internal sealed record WebsitePolicyRegistryListSnapshot(bool Exists, string[] Values);

internal sealed record WebsitePolicyBrowserRegistrySnapshot(
    WebsitePolicyRegistryListSnapshot PolicyBlocklist,
    WebsitePolicyRegistryListSnapshot PolicyAllowlist,
    WebsitePolicyRegistryListSnapshot ManagedBlocklist,
    WebsitePolicyRegistryListSnapshot ManagedAllowlist,
    WebsitePolicyRegistryValueSnapshot Initialized);

internal sealed record WebsitePolicyRegistrySnapshot(
    bool AgentKeyExists,
    WebsitePolicyRegistryValueSnapshot CampusId,
    WebsitePolicyRegistryValueSnapshot Revision,
    WebsitePolicyRegistryValueSnapshot Mode,
    WebsitePolicyRegistryValueSnapshot ExpiresUtc,
    WebsitePolicyRegistryValueSnapshot ExpiredUtc,
    WebsitePolicyBrowserRegistrySnapshot Edge,
    WebsitePolicyBrowserRegistrySnapshot Chrome,
    WebsitePolicyBrowserRegistrySnapshot? Firefox = null);

internal sealed record WebsitePolicyRegistryTransaction(
    int SchemaVersion,
    Guid TransactionId,
    WebsitePolicyRegistryTransactionOperation Operation,
    WebsitePolicyRegistrySnapshot Before,
    WebsitePolicyRegistrySnapshot After);

internal interface IWebsitePolicyRegistryTransactionBackend
{
    WebsitePolicyRegistrySnapshot ReadSnapshot();
    WebsitePolicyRegistryTransaction? ReadTransaction();
    void SaveTransaction(WebsitePolicyRegistryTransaction transaction);
    void VerifyCanApplyTarget(WebsitePolicyRegistryTransaction transaction);
    void ApplyTarget(WebsitePolicyRegistryTransaction transaction);
    void ClearTransaction();
    void DeleteAgentKey();
}

/// <summary>
/// Persists a complete before/after journal before changing any supported browser. Each
/// URL policy list is swapped as a registry-key rename, so recovery never has to
/// guess whether a partly written numbered list belongs to Windows or this tool.
/// </summary>
internal static class WebsitePolicyRegistryTransactions
{
    private const int MaximumJournalBytes = 900 * 1024;
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static bool Reconcile(IWebsitePolicyRegistryTransactionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        var transaction = backend.ReadTransaction();
        if (transaction is null) return false;
        Validate(transaction);

        var current = backend.ReadSnapshot();
        EnsureValueMayBe(current.AgentKeyExists ? new(true, "1") : new(false, null),
            transaction.Before.AgentKeyExists ? new(true, "1") : new(false, null),
            transaction.After.AgentKeyExists ? new(true, "1") : new(false, null), "Agent 注册表项");
        EnsureValueMayBe(current.CampusId, transaction.Before.CampusId, transaction.After.CampusId, "校区标识");
        EnsureValueMayBe(current.Revision, transaction.Before.Revision, transaction.After.Revision, "策略版本");
        EnsureValueMayBe(current.Mode, transaction.Before.Mode, transaction.After.Mode, "策略模式");
        EnsureValueMayBe(current.ExpiresUtc, transaction.Before.ExpiresUtc, transaction.After.ExpiresUtc, "策略期限");
        EnsureValueMayBe(current.ExpiredUtc, transaction.Before.ExpiredUtc, transaction.After.ExpiredUtc, "到期记录");
        EnsureBrowserMayBe(current.Edge, transaction.Before.Edge, transaction.After.Edge, "Edge");
        EnsureBrowserMayBe(current.Chrome, transaction.Before.Chrome, transaction.After.Chrome, "Chrome");
        EnsureBrowserMayBe(FirefoxSnapshot(current), FirefoxSnapshot(transaction.Before), FirefoxSnapshot(transaction.After), "Firefox");

        backend.VerifyCanApplyTarget(transaction);
        backend.ApplyTarget(transaction);
        var committed = backend.ReadSnapshot();
        if (!Equivalent(committed, transaction.After))
            throw new IOException("网站策略事务写入后读回与目标状态不一致；保留待恢复记录，不报告策略已完成。");

        if (transaction.Operation == WebsitePolicyRegistryTransactionOperation.Remove)
            backend.DeleteAgentKey();
        else
            backend.ClearTransaction();
        return true;
    }

    public static void Commit(IWebsitePolicyRegistryTransactionBackend backend,
        WebsitePolicyRegistryTransactionOperation operation, WebsitePolicyRegistrySnapshot before,
        WebsitePolicyRegistrySnapshot after)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (backend.ReadTransaction() is not null)
            throw new IOException("网站策略已有未完成的注册表事务；没有开始另一项修改。");
        if (!Equivalent(backend.ReadSnapshot(), before))
            throw new IOException("网站策略预检后注册表发生变化；没有覆盖该变化。");

        var transaction = new WebsitePolicyRegistryTransaction(SchemaVersion, Guid.NewGuid(), operation,
            before, after with { AgentKeyExists = true });
        Validate(transaction);
        var serialized = Serialize(transaction);
        if (Encoding.UTF8.GetByteCount(serialized) > MaximumJournalBytes)
            throw new InvalidDataException("网站策略事务记录超过安全大小上限；没有修改浏览器策略。");

        backend.SaveTransaction(transaction);
        Reconcile(backend);
    }

    public static bool Equivalent(WebsitePolicyRegistrySnapshot left, WebsitePolicyRegistrySnapshot right) =>
        left.AgentKeyExists == right.AgentKeyExists &&
        left.CampusId == right.CampusId && left.Revision == right.Revision && left.Mode == right.Mode &&
        left.ExpiresUtc == right.ExpiresUtc && left.ExpiredUtc == right.ExpiredUtc &&
        Equivalent(left.Edge, right.Edge) && Equivalent(left.Chrome, right.Chrome) &&
        Equivalent(FirefoxSnapshot(left), FirefoxSnapshot(right));

    internal static WebsitePolicyBrowserRegistrySnapshot FirefoxSnapshot(WebsitePolicyRegistrySnapshot snapshot) =>
        snapshot.Firefox ?? EmptyBrowser();

    private static WebsitePolicyBrowserRegistrySnapshot EmptyBrowser() =>
        new(new WebsitePolicyRegistryListSnapshot(false, []), new WebsitePolicyRegistryListSnapshot(false, []),
            new WebsitePolicyRegistryListSnapshot(false, []), new WebsitePolicyRegistryListSnapshot(false, []),
            new WebsitePolicyRegistryValueSnapshot(false, null));

    internal static bool IsValidStagingPrefix(WebsitePolicyRegistryListSnapshot staged,
        WebsitePolicyRegistryListSnapshot target) =>
        !staged.Exists || target.Exists && staged.Values.Length <= target.Values.Length &&
        staged.Values.SequenceEqual(target.Values.Take(staged.Values.Length), StringComparer.Ordinal);

    internal static void VerifyListSwapState(WebsitePolicyRegistryListSnapshot current,
        WebsitePolicyRegistryListSnapshot staged, WebsitePolicyRegistryListSnapshot backup,
        WebsitePolicyRegistryListSnapshot before, WebsitePolicyRegistryListSnapshot after, string label)
    {
        if (!IsValidStagingPrefix(staged, after))
            throw new IOException($"{label} 网站策略暂存键不是事务目标的有效前缀；没有覆盖。");
        if (backup.Exists && (!before.Exists || !Equivalent(backup, before)))
            throw new IOException($"{label} 网站策略备份键与事务原值不符；没有覆盖。");

        if (Equivalent(current, after))
        {
            if (Equivalent(current, before) && backup.Exists)
                throw new IOException($"{label} 事务原键和备份键同时存在；没有继续替换。");
            return;
        }
        if (Equivalent(current, before))
        {
            if (backup.Exists)
                throw new IOException($"{label} 事务原键和备份键同时存在；没有继续替换。");
            return;
        }
        if (!current.Exists && before.Exists && backup.Exists) return;
        if (!current.Exists && !before.Exists) return;
        throw new IOException($"{label} 策略与事务原值/目标值均不相同；保留外部策略。");
    }

    public static string Serialize(WebsitePolicyRegistryTransaction transaction)
    {
        Validate(transaction);
        return JsonSerializer.Serialize(transaction, JsonOptions);
    }

    public static WebsitePolicyRegistryTransaction Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaximumJournalBytes) throw new InvalidDataException("网站策略事务记录过大。");
        PolicyJson.RejectDuplicateFields(bytes);
        WebsitePolicyRegistryTransaction transaction;
        try
        {
            transaction = JsonSerializer.Deserialize<WebsitePolicyRegistryTransaction>(bytes, JsonOptions)
                ?? throw new InvalidDataException("网站策略事务记录为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("网站策略事务记录格式无效。", exception);
        }
        Validate(transaction);
        return transaction;
    }

    private static void Validate(WebsitePolicyRegistryTransaction transaction)
    {
        if (transaction.SchemaVersion != SchemaVersion || transaction.TransactionId == Guid.Empty ||
            !Enum.IsDefined(transaction.Operation) || transaction.Before is null || transaction.After is null ||
            !transaction.After.AgentKeyExists)
            throw new InvalidDataException("网站策略事务头部无效。");
        Validate(transaction.Before);
        Validate(transaction.After);
        ValidateOwnedSnapshot(transaction.Before);

        if (transaction.Operation == WebsitePolicyRegistryTransactionOperation.Remove)
        {
            if (transaction.After.CampusId.Exists || transaction.After.Revision.Exists || transaction.After.Mode.Exists ||
                transaction.After.ExpiresUtc.Exists || transaction.After.ExpiredUtc.Exists ||
                !Empty(transaction.After.Edge) || !Empty(transaction.After.Chrome) || !Empty(FirefoxSnapshot(transaction.After)))
                throw new InvalidDataException("网站策略卸载事务的目标状态无效。");
        }
        else if (!transaction.After.CampusId.Exists || !transaction.After.Revision.Exists ||
                 !transaction.After.Mode.Exists || !Enum.TryParse<WebsitePolicyMode>(transaction.After.Mode.Value,
                     ignoreCase: false, out var mode) || !Enum.IsDefined(mode))
        {
            throw new InvalidDataException("网站策略事务目标元数据无效。");
        }
        else
        {
            ValidateOwnedSnapshot(transaction.After);
            var beforeRevision = transaction.Before.Revision.Exists
                ? long.Parse(transaction.Before.Revision.Value!, CultureInfo.InvariantCulture) : 0L;
            var afterRevision = long.Parse(transaction.After.Revision.Value!, CultureInfo.InvariantCulture);
            if (transaction.Operation == WebsitePolicyRegistryTransactionOperation.Apply)
            {
                if (afterRevision <= beforeRevision ||
                    transaction.Before.CampusId.Exists && transaction.Before.CampusId != transaction.After.CampusId ||
                    mode == WebsitePolicyMode.Disabled && transaction.After.ExpiresUtc.Exists)
                    throw new InvalidDataException("网站策略应用事务版本、校区或期限无效。");
            }
            else if (transaction.Before.Mode.Value is not (nameof(WebsitePolicyMode.Blocklist) or nameof(WebsitePolicyMode.Allowlist)) ||
                     !transaction.Before.ExpiresUtc.Exists || afterRevision != beforeRevision ||
                     transaction.Before.CampusId != transaction.After.CampusId ||
                     transaction.After.Mode.Value != nameof(WebsitePolicyMode.Disabled) ||
                     transaction.After.ExpiresUtc.Exists || !transaction.After.ExpiredUtc.Exists)
            {
                throw new InvalidDataException("网站策略到期事务的原值或目标值无效。");
            }
        }
    }

    private static void Validate(WebsitePolicyRegistrySnapshot snapshot)
    {
        if (snapshot is null || snapshot.Edge is null || snapshot.Chrome is null)
            throw new InvalidDataException("网站策略事务快照不完整。");
        Validate(snapshot.CampusId);
        Validate(snapshot.Revision);
        Validate(snapshot.Mode);
        Validate(snapshot.ExpiresUtc);
        Validate(snapshot.ExpiredUtc);
        Validate(snapshot.Edge);
        Validate(snapshot.Chrome);
        Validate(FirefoxSnapshot(snapshot));
        if (snapshot.Revision.Exists &&
            (!long.TryParse(snapshot.Revision.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0))
            throw new InvalidDataException("网站策略事务版本快照无效。");
        if (snapshot.CampusId.Exists) WebsitePolicySigningKeyStore.ValidateCampusId(snapshot.CampusId.Value!);
        if (snapshot.Mode.Exists && (!Enum.TryParse<WebsitePolicyMode>(snapshot.Mode.Value,
                ignoreCase: false, out var mode) || !Enum.IsDefined(mode)))
            throw new InvalidDataException("网站策略事务模式快照无效。");
        ValidateUtcValue(snapshot.ExpiresUtc);
        ValidateUtcValue(snapshot.ExpiredUtc);
        if (snapshot.ExpiresUtc.Exists && snapshot.ExpiredUtc.Exists)
            throw new InvalidDataException("网站策略事务期限快照同时含活动期限和到期记录。");
        if (!snapshot.AgentKeyExists && (snapshot.CampusId.Exists || snapshot.Revision.Exists || snapshot.Mode.Exists ||
            snapshot.ExpiresUtc.Exists || snapshot.ExpiredUtc.Exists ||
            snapshot.Edge.ManagedBlocklist.Exists || snapshot.Edge.ManagedAllowlist.Exists || snapshot.Edge.Initialized.Exists ||
            snapshot.Chrome.ManagedBlocklist.Exists || snapshot.Chrome.ManagedAllowlist.Exists || snapshot.Chrome.Initialized.Exists ||
            FirefoxSnapshot(snapshot).ManagedBlocklist.Exists || FirefoxSnapshot(snapshot).ManagedAllowlist.Exists || FirefoxSnapshot(snapshot).Initialized.Exists))
            throw new InvalidDataException("网站策略事务快照与 Agent 注册表项状态不一致。");
    }

    private static void Validate(WebsitePolicyBrowserRegistrySnapshot snapshot)
    {
        if (snapshot is null) throw new InvalidDataException("网站策略浏览器快照缺失。");
        Validate(snapshot.PolicyBlocklist);
        Validate(snapshot.PolicyAllowlist);
        Validate(snapshot.ManagedBlocklist);
        Validate(snapshot.ManagedAllowlist);
        Validate(snapshot.Initialized);
        if (snapshot.Initialized.Exists && snapshot.Initialized.Value is not ("0" or "1"))
            throw new InvalidDataException("网站策略所有权标记无效。");
    }

    private static void Validate(WebsitePolicyRegistryListSnapshot snapshot)
    {
        if (snapshot is null || snapshot.Values is null || snapshot.Values.Length > WebsitePolicyCompiler.MaximumEntries + 1 ||
            (!snapshot.Exists && snapshot.Values.Length != 0) ||
            snapshot.Values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl)))
            throw new InvalidDataException("网站策略事务名单快照无效。");
    }

    private static void Validate(WebsitePolicyRegistryValueSnapshot snapshot)
    {
        if (snapshot is null || (snapshot.Exists && snapshot.Value is null) ||
            (!snapshot.Exists && snapshot.Value is not null) || snapshot.Value?.Length > 4096)
            throw new InvalidDataException("网站策略事务注册表值快照无效。");
    }

    private static bool Equivalent(WebsitePolicyBrowserRegistrySnapshot left,
        WebsitePolicyBrowserRegistrySnapshot right) =>
        Equivalent(left.PolicyBlocklist, right.PolicyBlocklist) && Equivalent(left.PolicyAllowlist, right.PolicyAllowlist) &&
        Equivalent(left.ManagedBlocklist, right.ManagedBlocklist) && Equivalent(left.ManagedAllowlist, right.ManagedAllowlist) &&
        left.Initialized == right.Initialized;

    private static bool Equivalent(WebsitePolicyRegistryListSnapshot left, WebsitePolicyRegistryListSnapshot right) =>
        left.Exists == right.Exists && left.Values.SequenceEqual(right.Values, StringComparer.Ordinal);

    private static bool Empty(WebsitePolicyBrowserRegistrySnapshot snapshot) =>
        !snapshot.PolicyBlocklist.Exists && !snapshot.PolicyAllowlist.Exists &&
        !snapshot.ManagedBlocklist.Exists && !snapshot.ManagedAllowlist.Exists && !snapshot.Initialized.Exists;

    private static void ValidateOwnedSnapshot(WebsitePolicyRegistrySnapshot snapshot)
    {
        ValidateOwnedBrowserSnapshot(snapshot.Edge);
        ValidateOwnedBrowserSnapshot(snapshot.Chrome);
        ValidateOwnedBrowserSnapshot(FirefoxSnapshot(snapshot));
        var hasOwner = HasOwner(snapshot.Edge) || HasOwner(snapshot.Chrome) || HasOwner(FirefoxSnapshot(snapshot));
        var hasMetadata = snapshot.CampusId.Exists || snapshot.Revision.Exists || snapshot.Mode.Exists ||
                          snapshot.ExpiresUtc.Exists || snapshot.ExpiredUtc.Exists;
        if (hasOwner && (!snapshot.CampusId.Exists || !snapshot.Revision.Exists || !snapshot.Mode.Exists))
            throw new InvalidDataException("网站策略事务原值缺少完整的校区、版本或模式元数据。");
        if (hasMetadata && (!snapshot.CampusId.Exists || !snapshot.Revision.Exists || !snapshot.Mode.Exists))
            throw new InvalidDataException("网站策略事务元数据快照不完整。");
    }

    private static void ValidateOwnedBrowserSnapshot(WebsitePolicyBrowserRegistrySnapshot browser)
    {
        if (browser.Initialized is { Exists: true, Value: "1" })
        {
            if (!Equivalent(browser.PolicyBlocklist, browser.ManagedBlocklist) ||
                !Equivalent(browser.PolicyAllowlist, browser.ManagedAllowlist) ||
                browser.PolicyBlocklist.Exists != (browser.PolicyBlocklist.Values.Length > 0) ||
                browser.PolicyAllowlist.Exists != (browser.PolicyAllowlist.Values.Length > 0))
                throw new InvalidDataException("网站策略事务名单与其所有权记录不一致。");
            return;
        }
        if (browser.PolicyBlocklist.Exists || browser.PolicyAllowlist.Exists ||
            browser.ManagedBlocklist.Exists || browser.ManagedAllowlist.Exists)
            throw new InvalidDataException("网站策略事务试图接管没有完整所有权记录的浏览器策略。");
    }

    private static bool HasOwner(WebsitePolicyBrowserRegistrySnapshot browser) =>
        browser.Initialized.Exists || browser.ManagedBlocklist.Exists || browser.ManagedAllowlist.Exists;

    private static void ValidateUtcValue(WebsitePolicyRegistryValueSnapshot value)
    {
        if (!value.Exists) return;
        if (!DateTimeOffset.TryParseExact(value.Value, "O", CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) || parsed.Offset != TimeSpan.Zero)
            throw new InvalidDataException("网站策略事务 UTC 时间快照无效。");
    }

    private static void EnsureBrowserMayBe(WebsitePolicyBrowserRegistrySnapshot current,
        WebsitePolicyBrowserRegistrySnapshot before, WebsitePolicyBrowserRegistrySnapshot after, string browser)
    {
        // Policy list keys are checked and recovered with their staging/backup siblings
        // by ApplyTarget. The remaining values are individual atomic Registry values.
        EnsureValueMayBe(current.ManagedBlocklist.Exists ? new(true, string.Join("\0", current.ManagedBlocklist.Values)) : new(false, null),
            before.ManagedBlocklist.Exists ? new(true, string.Join("\0", before.ManagedBlocklist.Values)) : new(false, null),
            after.ManagedBlocklist.Exists ? new(true, string.Join("\0", after.ManagedBlocklist.Values)) : new(false, null), browser + " 所有权黑名单");
        EnsureValueMayBe(current.ManagedAllowlist.Exists ? new(true, string.Join("\0", current.ManagedAllowlist.Values)) : new(false, null),
            before.ManagedAllowlist.Exists ? new(true, string.Join("\0", before.ManagedAllowlist.Values)) : new(false, null),
            after.ManagedAllowlist.Exists ? new(true, string.Join("\0", after.ManagedAllowlist.Values)) : new(false, null), browser + " 所有权白名单");
        EnsureValueMayBe(current.Initialized, before.Initialized, after.Initialized, browser + " 所有权标记");
    }

    private static void EnsureValueMayBe(WebsitePolicyRegistryValueSnapshot current,
        WebsitePolicyRegistryValueSnapshot before, WebsitePolicyRegistryValueSnapshot after, string label)
    {
        if (current != before && current != after)
            throw new IOException($"网站策略事务期间 {label} 被外部修改；保留外部值并停止恢复。");
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsWebsitePolicyRegistryTransactionBackend :
    IWebsitePolicyRegistryTransactionBackend, IDisposable
{
    private sealed record Browser(string Name, string Key, string BlocklistName, string AllowlistName);
    private const string AgentKey = @"SOFTWARE\VeyonCampus\WebsitePolicy";
    private const string TransactionValue = "PendingTransactionJson";
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private readonly RegistryKey _root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    private static readonly Browser[] Browsers =
    [
        new("Edge", @"SOFTWARE\Policies\Microsoft\Edge", "URLBlocklist", "URLAllowlist"),
        new("Chrome", @"SOFTWARE\Policies\Google\Chrome", "URLBlocklist", "URLAllowlist"),
        new("Firefox", @"SOFTWARE\Policies\Mozilla\Firefox\WebsiteFilter", "Block", "Exceptions")
    ];

    public WebsitePolicyRegistrySnapshot ReadSnapshot()
    {
        using var agent = _root.OpenSubKey(AgentKey, writable: false);
        if (agent is not null)
        {
            var allowedValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "CampusId", "Revision", "Mode", "ExpiresUtc", "ExpiredUtc", TransactionValue };
            if (agent.GetValueNames().Any(name => !allowedValues.Contains(name)) ||
                agent.GetSubKeyNames().Any(name => !string.Equals(name, "Managed", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("网站策略 Agent 注册表包含未知内容；事务没有修改浏览器策略。");
            using var managedRoot = agent.OpenSubKey("Managed", writable: false);
            if (managedRoot is not null && managedRoot.GetSubKeyNames().Any(name =>
                    !Browsers.Any(browser => string.Equals(browser.Name, name, StringComparison.OrdinalIgnoreCase))))
                throw new IOException("网站策略所有权记录包含未知浏览器；事务没有修改浏览器策略。");
        }

        return new WebsitePolicyRegistrySnapshot(agent is not null,
            ReadStringValue(agent, "CampusId"), ReadQwordValue(agent, "Revision"), ReadStringValue(agent, "Mode"),
            ReadStringValue(agent, "ExpiresUtc"), ReadStringValue(agent, "ExpiredUtc"),
            ReadBrowserSnapshot("Edge", agent), ReadBrowserSnapshot("Chrome", agent),
            ReadBrowserSnapshot("Firefox", agent));
    }

    public WebsitePolicyRegistryTransaction? ReadTransaction()
    {
        using var agent = _root.OpenSubKey(AgentKey, writable: false);
        if (agent is null) return null;
        var raw = agent.GetValue(TransactionValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null) return null;
        if (agent.GetValueKind(TransactionValue) != RegistryValueKind.String || raw is not string json)
            throw new InvalidDataException("网站策略待恢复事务格式无效。");
        return WebsitePolicyRegistryTransactions.Deserialize(json);
    }

    public void SaveTransaction(WebsitePolicyRegistryTransaction transaction)
    {
        using var agent = _root.CreateSubKey(AgentKey, writable: true)
            ?? throw new IOException("无法保存网站策略事务记录。");
        if (agent.GetValue(TransactionValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not null)
            throw new IOException("网站策略已有未完成事务；没有覆盖事务记录。");
        var json = WebsitePolicyRegistryTransactions.Serialize(transaction);
        if (Encoding.UTF8.GetByteCount(json) > 900 * 1024)
            throw new InvalidDataException("网站策略事务记录超过注册表值上限。");
        agent.SetValue(TransactionValue, json, RegistryValueKind.String);
        agent.Flush();
    }

    public void ApplyTarget(WebsitePolicyRegistryTransaction transaction)
    {
        VerifyCanApplyTarget(transaction);
        foreach (var browser in Browsers)
        {
            var before = Snapshot(transaction.Before, browser.Name);
            var after = Snapshot(transaction.After, browser.Name);
            ReplacePolicyList(browser.Key, browser.Name, "Blocklist", browser.BlocklistName,
                before.PolicyBlocklist, after.PolicyBlocklist, transaction.TransactionId);
            ReplacePolicyList(browser.Key, browser.Name, "Allowlist", browser.AllowlistName,
                before.PolicyAllowlist, after.PolicyAllowlist, transaction.TransactionId);
            WriteManagedList(browser.Name, "URLBlocklist", before.ManagedBlocklist, after.ManagedBlocklist);
            WriteManagedList(browser.Name, "URLAllowlist", before.ManagedAllowlist, after.ManagedAllowlist);
            WriteInitialized(browser.Name, before.Initialized, after.Initialized);
        }

        WriteMetadataValue("CampusId", transaction.Before.CampusId, transaction.After.CampusId, RegistryValueKind.String);
        WriteMetadataValue("Revision", transaction.Before.Revision, transaction.After.Revision, RegistryValueKind.QWord);
        WriteMetadataValue("Mode", transaction.Before.Mode, transaction.After.Mode, RegistryValueKind.String);
        WriteMetadataValue("ExpiresUtc", transaction.Before.ExpiresUtc, transaction.After.ExpiresUtc, RegistryValueKind.String);
        WriteMetadataValue("ExpiredUtc", transaction.Before.ExpiredUtc, transaction.After.ExpiredUtc, RegistryValueKind.String);
    }

    public void VerifyCanApplyTarget(WebsitePolicyRegistryTransaction transaction)
    {
        foreach (var browser in Browsers)
        {
            var before = Snapshot(transaction.Before, browser.Name);
            var after = Snapshot(transaction.After, browser.Name);
            VerifyPolicyListSwap(browser.Key, browser.Name, "Blocklist", browser.BlocklistName,
                before.PolicyBlocklist, after.PolicyBlocklist, transaction.TransactionId);
            VerifyPolicyListSwap(browser.Key, browser.Name, "Allowlist", browser.AllowlistName,
                before.PolicyAllowlist, after.PolicyAllowlist, transaction.TransactionId);
        }
    }

    public void ClearTransaction()
    {
        using var agent = _root.OpenSubKey(AgentKey, writable: true)
            ?? throw new IOException("网站策略事务记录丢失；不能确认完成状态。");
        agent.DeleteValue(TransactionValue, throwOnMissingValue: false);
        agent.Flush();
    }

    public void DeleteAgentKey() => _root.DeleteSubKeyTree(AgentKey, throwOnMissingSubKey: false);

    public void Dispose() => _root.Dispose();

    private WebsitePolicyBrowserRegistrySnapshot ReadBrowserSnapshot(string browser, RegistryKey? agent)
    {
        var browserDefinition = Browsers.Single(item => item.Name == browser);
        var parent = browserDefinition.Key;
        using var policy = _root.OpenSubKey(parent, writable: false);
        using var managed = agent?.OpenSubKey("Managed\\" + browser, writable: false);
        if (managed is not null)
        {
            var allowedValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "Initialized", "URLBlocklist", "URLAllowlist" };
            if (managed.GetValueNames().Any(name => !allowedValues.Contains(name)) || managed.SubKeyCount != 0)
                throw new IOException($"{browser} 网站策略所有权记录包含未知内容；事务没有修改浏览器策略。");
        }
        return new WebsitePolicyBrowserRegistrySnapshot(
            ReadPolicyList(policy, browserDefinition.BlocklistName), ReadPolicyList(policy, browserDefinition.AllowlistName),
            ReadManagedList(managed, "URLBlocklist"), ReadManagedList(managed, "URLAllowlist"),
            ReadDwordValue(managed, "Initialized"));
    }

    private void ReplacePolicyList(string policyPath, string browser, string listKind, string listName,
        WebsitePolicyRegistryListSnapshot before, WebsitePolicyRegistryListSnapshot after, Guid transactionId)
    {
        var stageName = $"__VCtxn_{transactionId:N}_{browser}_{listKind}_new";
        var backupName = $"__VCtxn_{transactionId:N}_{browser}_{listKind}_old";
        using var parent = _root.OpenSubKey(policyPath, writable: true) ??
            (after.Exists ? _root.CreateSubKey(policyPath, writable: true) : null);
        if (parent is null)
        {
            if (before.Exists) throw new IOException($"{browser} {listName} 策略键在事务恢复前消失。");
            return;
        }

        var current = ReadPolicyList(parent, listName);
        var stage = ReadPolicyList(parent, stageName, requireContiguous: true);
        var backup = ReadPolicyList(parent, backupName);
        WebsitePolicyRegistryTransactions.VerifyListSwapState(current, stage, backup, before, after,
            $"{browser} {listName}");

        if (ListsEqual(current, after))
        {
            if (stage.Exists) parent.DeleteSubKeyTree(stageName, throwOnMissingSubKey: false);
            if (backup.Exists) parent.DeleteSubKeyTree(backupName, throwOnMissingSubKey: false);
            parent.Flush();
            return;
        }

        var sourceAlreadyBackedUp = !current.Exists && before.Exists && backup.Exists;
        if (!ListsEqual(current, before) && !sourceAlreadyBackedUp)
            throw new IOException($"{browser} {listName} 策略与事务原值/目标值均不相同；保留外部策略。");

        if (after.Exists && (!stage.Exists || !ListsEqual(stage, after)))
        {
            using var staged = parent.OpenSubKey(stageName, writable: true) ?? parent.CreateSubKey(stageName, writable: true)
                ?? throw new IOException($"无法准备 {browser} 网站策略原子暂存键。");
            AppendNumberedValues(staged, stage.Values.Length, after.Values);
            staged.Flush();
            stage = ReadPolicyList(parent, stageName, requireContiguous: true);
            if (!ListsEqual(stage, after))
                throw new IOException($"{browser} {listName} 暂存名单读回不符；保留事务记录。");
        }

        if (current.Exists)
        {
            RenameSubKey(parent, listName, backupName);
            parent.Flush();
        }
        else if (before.Exists && !backup.Exists)
        {
            throw new IOException($"{browser} {listName} 原策略键消失，且没有事务备份；保留当前状态。");
        }

        if (after.Exists)
        {
            if (ReadPolicyList(parent, listName).Exists)
                throw new IOException($"{browser} {listName} 目标策略键意外存在；没有覆盖。");
            RenameSubKey(parent, stageName, listName);
            parent.Flush();
        }

        if (!ListsEqual(ReadPolicyList(parent, listName), after))
            throw new IOException($"{browser} {listName} 原子替换后读回不符；保留事务记录。");
        if (ReadPolicyList(parent, backupName).Exists)
        {
            if (!ListsEqual(ReadPolicyList(parent, backupName), before))
                throw new IOException($"{browser} {listName} 备份读回不符；保留事务记录。");
            parent.DeleteSubKeyTree(backupName, throwOnMissingSubKey: false);
            parent.Flush();
        }
    }

    private void VerifyPolicyListSwap(string policyPath, string browser, string listKind, string listName,
        WebsitePolicyRegistryListSnapshot before, WebsitePolicyRegistryListSnapshot after, Guid transactionId)
    {
        var stageName = $"__VCtxn_{transactionId:N}_{browser}_{listKind}_new";
        var backupName = $"__VCtxn_{transactionId:N}_{browser}_{listKind}_old";
        using var parent = _root.OpenSubKey(policyPath, writable: false);
        if (parent is null)
        {
            if (before.Exists) throw new IOException($"{browser} {listName} 策略键在事务恢复前消失。");
            return;
        }

        var current = ReadPolicyList(parent, listName);
        var stage = ReadPolicyList(parent, stageName, requireContiguous: true);
        var backup = ReadPolicyList(parent, backupName);
        WebsitePolicyRegistryTransactions.VerifyListSwapState(current, stage, backup, before, after,
            $"{browser} {listName}");
    }

    private void WriteManagedList(string browser, string name, WebsitePolicyRegistryListSnapshot before,
        WebsitePolicyRegistryListSnapshot after)
    {
        using var agent = _root.OpenSubKey(AgentKey, writable: true)
            ?? (after.Exists ? _root.CreateSubKey(AgentKey, writable: true) : null);
        if (agent is null)
        {
            if (before.Exists) throw new IOException("网站策略所有权记录在事务期间消失。");
            return;
        }
        using var managedRoot = agent.CreateSubKey("Managed", writable: true)
            ?? throw new IOException("无法更新网站策略所有权记录。");
        using var managed = managedRoot.OpenSubKey(browser, writable: true) ??
            (after.Exists || after.Values.Length == 0 && before.Exists
                ? managedRoot.CreateSubKey(browser, writable: true) : null);
        var current = ReadManagedList(managed, name);
        if (ListValueEquals(current, after)) return;
        if (!ListValueEquals(current, before))
            throw new IOException($"{browser} {name} 所有权值发生外部变化；没有覆盖。");
        if (managed is null)
        {
            if (after.Exists) throw new IOException("网站策略所有权子项不存在。");
            return;
        }
        if (after.Exists) managed.SetValue(name, after.Values, RegistryValueKind.MultiString);
        else managed.DeleteValue(name, throwOnMissingValue: false);
        managed.Flush();
    }

    private void WriteInitialized(string browser, WebsitePolicyRegistryValueSnapshot before,
        WebsitePolicyRegistryValueSnapshot after)
    {
        using var agent = _root.OpenSubKey(AgentKey, writable: true)
            ?? _root.CreateSubKey(AgentKey, writable: true)
            ?? throw new IOException("无法更新网站策略所有权标记。");
        using var managedRoot = agent.CreateSubKey("Managed", writable: true)
            ?? throw new IOException("无法更新网站策略所有权标记。");
        using var managed = managedRoot.CreateSubKey(browser, writable: true)
            ?? throw new IOException("无法更新网站策略所有权标记。");
        var current = ReadDwordValue(managed, "Initialized");
        if (current == after) return;
        if (current != before) throw new IOException($"{browser} 网站策略所有权标记被外部修改；没有覆盖。");
        if (after.Exists) managed.SetValue("Initialized", int.Parse(after.Value!, CultureInfo.InvariantCulture), RegistryValueKind.DWord);
        else managed.DeleteValue("Initialized", throwOnMissingValue: false);
        managed.Flush();
    }

    private void WriteMetadataValue(string name, WebsitePolicyRegistryValueSnapshot before,
        WebsitePolicyRegistryValueSnapshot after, RegistryValueKind kind)
    {
        using var agent = _root.OpenSubKey(AgentKey, writable: true) ??
            (after.Exists ? _root.CreateSubKey(AgentKey, writable: true) : null);
        if (agent is null)
        {
            if (before.Exists) throw new IOException("网站策略元数据在事务期间消失。");
            return;
        }
        var current = kind == RegistryValueKind.QWord ? ReadQwordValue(agent, name) : ReadStringValue(agent, name);
        if (current == after) return;
        if (current != before) throw new IOException($"网站策略元数据 {name} 被外部修改；没有覆盖。");
        if (after.Exists)
        {
            object value = kind == RegistryValueKind.QWord
                ? long.Parse(after.Value!, CultureInfo.InvariantCulture)
                : after.Value!;
            agent.SetValue(name, value, kind);
        }
        else agent.DeleteValue(name, throwOnMissingValue: false);
        agent.Flush();
    }

    private static WebsitePolicyBrowserRegistrySnapshot Snapshot(WebsitePolicyRegistrySnapshot snapshot, string browser) =>
        browser switch
        {
            "Edge" => snapshot.Edge,
            "Chrome" => snapshot.Chrome,
            "Firefox" => WebsitePolicyRegistryTransactions.FirefoxSnapshot(snapshot),
            _ => throw new InvalidDataException("网站策略浏览器未注册。")
        };

    private static WebsitePolicyRegistryListSnapshot ReadPolicyList(RegistryKey? parent, string name,
        bool requireContiguous = false)
    {
        using var list = parent?.OpenSubKey(name, writable: false);
        if (list is null) return new WebsitePolicyRegistryListSnapshot(false, []);
        if (list.SubKeyCount != 0) throw new IOException("浏览器网址策略键包含未知子项。");
        var indexed = new List<(int Index, string Value)>();
        foreach (var valueName in list.GetValueNames())
        {
            if (!int.TryParse(valueName, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index <= 0 ||
                list.GetValueKind(valueName) != RegistryValueKind.String || list.GetValue(valueName) is not string value)
                throw new IOException("浏览器网址策略键包含不支持的值。");
            indexed.Add((index, value));
        }
        indexed.Sort((left, right) => left.Index.CompareTo(right.Index));
        if (indexed.Select(item => item.Index).Distinct().Count() != indexed.Count)
            throw new IOException("浏览器网址策略键存在重复编号。");
        if (requireContiguous && indexed.Where((item, offset) => item.Index != offset + 1).Any())
            throw new IOException("网站策略暂存键的名单编号不连续；保留待恢复事务。");
        return new WebsitePolicyRegistryListSnapshot(true, indexed.Select(item => item.Value).ToArray());
    }

    private static WebsitePolicyRegistryListSnapshot ReadManagedList(RegistryKey? key, string name)
    {
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            return new WebsitePolicyRegistryListSnapshot(false, []);
        var kind = key.GetValueKind(name);
        var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return kind switch
        {
            RegistryValueKind.MultiString when raw is string[] values => new(true, values),
            RegistryValueKind.String when raw is string text => new(true, [text]),
            _ => throw new IOException("网站策略所有权名单注册表类型不受支持。")
        };
    }

    private static WebsitePolicyRegistryValueSnapshot ReadStringValue(RegistryKey? key, string name)
    {
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return new(false, null);
        if (key.GetValueKind(name) != RegistryValueKind.String ||
            key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value)
            throw new IOException($"网站策略注册表值 {name} 类型无效。");
        return new(true, value);
    }

    private static WebsitePolicyRegistryValueSnapshot ReadQwordValue(RegistryKey? key, string name)
    {
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return new(false, null);
        if (key.GetValueKind(name) != RegistryValueKind.QWord || key.GetValue(name) is not long value)
            throw new IOException($"网站策略注册表值 {name} 类型无效。");
        return new(true, value.ToString(CultureInfo.InvariantCulture));
    }

    private static WebsitePolicyRegistryValueSnapshot ReadDwordValue(RegistryKey? key, string name)
    {
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return new(false, null);
        if (key.GetValueKind(name) != RegistryValueKind.DWord || key.GetValue(name) is not int value)
            throw new IOException($"网站策略注册表值 {name} 类型无效。");
        return new(true, value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendNumberedValues(RegistryKey key, int existingCount, IReadOnlyList<string> values)
    {
        for (var index = existingCount; index < values.Count; index++)
            key.SetValue((index + 1).ToString(CultureInfo.InvariantCulture), values[index], RegistryValueKind.String);
    }

    private static bool ListsEqual(WebsitePolicyRegistryListSnapshot left, WebsitePolicyRegistryListSnapshot right) =>
        left.Exists == right.Exists && left.Values.SequenceEqual(right.Values, StringComparer.Ordinal);

    private static bool ListValueEquals(WebsitePolicyRegistryListSnapshot left, WebsitePolicyRegistryListSnapshot right) =>
        left.Exists == right.Exists && left.Values.SequenceEqual(right.Values, StringComparer.Ordinal);

    private static void RenameSubKey(RegistryKey parent, string oldName, string newName)
    {
        var status = RegRenameKey(parent.Handle, oldName, newName);
        if (status is not 0 and not ErrorFileNotFound and not ErrorPathNotFound)
            throw new Win32Exception(status, "Windows 无法原子替换浏览器网址策略注册表键。");
        if (status != 0) throw new IOException("网站策略原子替换时注册表键已变化；保留待恢复事务。");
    }

    [DllImport("advapi32.dll", EntryPoint = "RegRenameKey", CharSet = CharSet.Unicode)]
    private static extern int RegRenameKey(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, string oldName, string newName);
}
