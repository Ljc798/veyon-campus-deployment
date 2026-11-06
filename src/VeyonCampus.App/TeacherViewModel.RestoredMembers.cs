using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media;
using VeyonCampus.Core;
using VeyonCampus.Core.Maintenance;
using VeyonCampus.App.Maintenance;

namespace VeyonCampus.App;

public sealed partial class TeacherViewModel
{
    private static readonly ObservableCollection<TeacherClassroomChatMessageItem> EmptyClassroomChatMessages = [];

    private void RefreshRuntimeSummaryCounts()
    {
        Changed(nameof(DeviceHealthAllCount));
        Changed(nameof(DeviceHealthHealthyCount));
        Changed(nameof(DeviceHealthAttentionCount));
        Changed(nameof(DeviceHealthManualReviewCount));
        Changed(nameof(DeviceHealthNoResponseCount));
        Changed(nameof(DeviceHealthPendingCount));
        Changed(nameof(DeviceHealthNeedsActionCount));
        Changed(nameof(DeviceHealthUnconfirmedCount));
        Changed(nameof(DeviceHealthListSummary));
    }

    private void UpdateRuntimeSummaryTargetSelection()
    {
        var selected = GetDeviceHealthTargets().Select(NormalizeTargetKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _deviceRuntimeSummaryPolling.Keys.Where(key => !selected.Contains(key)).ToArray())
            _deviceRuntimeSummaryPolling.Remove(key);
    }

    public bool IsRoomPage { get => _selectedPage == "rooms"; set { if (value) SelectPage("rooms"); } }

    public bool IsSetupPage { get => _selectedPage == "setup"; set { if (value) SelectPage("setup"); } }

    public int TeacherSetupWizardStep => _teacherSetupWizardStep;

    public int TeacherSetupWizardProgress => (_teacherSetupWizardStep - 1) * 50;

    public string TeacherSetupWizardProgressText => $"第 {_teacherSetupWizardStep} 步，共 3 步";

    public bool IsTeacherSetupStep1 => _teacherSetupWizardStep == 1;

    public bool IsTeacherSetupStep2 => _teacherSetupWizardStep == 2;

    public bool IsTeacherSetupStep3 => _teacherSetupWizardStep == 3;

    public bool IsWindowsTeacherSetupStep => IsTeacherSetupStep1 && IsWindowsTeacherHost;

    public bool IsMacTeacherSetupStep => IsTeacherSetupStep1 && IsMacTeacherHost;

    public bool IsTeacherSetupPackageStepVisible => IsSetupPage && IsTeacherSetupStep3;

    public bool IsTeacherSetupRoomComplete => HasSeatMap && SeatGridComputerCount > 0;

    public bool IsTeacherSetupRoomSkippedForNow => _teacherSetupRoomSkippedForNow && !IsTeacherSetupRoomComplete;

    public bool IsMacTeacherHost => OperatingSystem.IsMacOS();

    public string MacTeacherLoginStartStatus => _macTeacherLoginStart.Message;

    public bool CanChangeMacTeacherLoginStart => IsMacTeacherHost &&
        _macTeacherLoginStart.IsPackagedApp && !_isChangingMacTeacherLoginStart;

    public bool CanRefreshMacTeacherLoginStart => IsMacTeacherHost && !_isChangingMacTeacherLoginStart;

    public string MacTeacherLoginStartActionLabel => _macTeacherLoginStart.State switch
    {
        MacTeacherLoginStartState.Enabled => "关闭登录时自动启动",
        MacTeacherLoginStartState.RequiresApproval => "打开登录项设置",
        _ => "启用登录时自动启动"
    };

    public bool IsChangingMacTeacherLoginStart => _isChangingMacTeacherLoginStart;

    public string MacVeyonPrivateKeyImportInstructions => _macVeyonPrivateKeyImportInstructions;

    public bool IsImportingMacVeyonKey => _isImportingMacVeyonKey;
    public string MacVeyonKeyImportStatus => _macVeyonKeyImportStatus;

    public bool CanImportMacVeyonPrivateKey => IsMacTeacherHost && HasTrustMigrationSourcePackageDirectory &&
                                               _trustMigrationSourcePackageValidated &&
                                               !_isValidatingTrustMigrationSourcePackage && !_isImportingMacVeyonKey;

    public bool CanChooseTrustMigrationSourcePackage => IsMacTeacherHost && !IsExecuting &&
        !_isValidatingTrustMigrationSourcePackage && !_isImportingMacVeyonKey;

    public string TrustMigrationSourcePackageValidationStatus =>
        _trustMigrationSourcePackageValidationStatus;

    public bool IsValidatingTrustMigrationSourcePackage => _isValidatingTrustMigrationSourcePackage;

    public bool IsTrustMigrationSourcePackageReady => _trustMigrationSourcePackageValidated;

    public bool IsTrustMigrationSourcePackageNotReady =>
        !_trustMigrationSourcePackageValidated && !_isValidatingTrustMigrationSourcePackage;

    public string MacVeyonHelperStatus => _macVeyonHelperStatus;

    public bool IsMacVeyonHelperReady => MacVeyonHelperStatus.StartsWith(
        "Veyon helper 本机组件已就绪", StringComparison.Ordinal);

    public bool IsPreparingMacVeyonHelper => _isPreparingMacVeyonHelper;

    public bool CanPrepareMacVeyonHelper => IsMacTeacherHost && !IsPreparingMacVeyonHelper;

    public bool CanProceedTeacherSetupToRooms => (IsTeacherSetupVeyonReady || IsMacTeacherHost) && !_isCheckingTeacherSetupVeyon;

    public bool CanProceedTeacherSetupToPackage => (IsTeacherSetupVeyonReady || IsMacTeacherHost) &&
        (IsTeacherSetupRoomComplete || _teacherSetupRoomSkippedForNow) && !_isCheckingTeacherSetupVeyon;

    public bool CanSkipTeacherSetupRoom => IsTeacherSetupStep2 && (IsTeacherSetupVeyonReady || IsMacTeacherHost) &&
        !IsTeacherSetupRoomComplete && !_teacherSetupRoomSkippedForNow;

    public string TeacherSetupRoomSummary
    {
        get
        {
            if (!HasSeatMap)
                return "还没有保存机房座位图。可以先建立桌位并填写学生姓名；暂时没有机房时，也可以跳过此步骤。";

            var summary = $"已保存 {SeatGridSeatCount} 个桌位 · 已填写 {SeatGridNamedCount} 个姓名。";
            if (IsTeacherSetupRoomComplete)
                return summary + $"将为已分配电脑编号的 {SeatGridComputerCount} 个座位生成电脑；学生姓名可以留空。";
            if (SeatGridSeatCount == 0)
                return summary + "请添加桌位并为需要接入 Veyon 的电脑座位分配编号。";
            return summary + "为需要接入 Veyon 的座位分配电脑编号即可继续；姓名可选。";
        }
    }

    public async Task RefreshTeacherSetupVeyonStatusAsync()
    {
        if (_isCheckingTeacherSetupVeyon) return;
        if (!OperatingSystem.IsWindows())
        {
            _teacherSetupVeyonReady = false;
            _teacherSetupVeyonStatus = "教师端 Veyon 状态检查仅支持 Windows。";
            Changed(nameof(TeacherSetupVeyonStatus));
            NotifyTeacherSetupNavigationStateChanged();
            return;
        }

        _isCheckingTeacherSetupVeyon = true;
        Changed(nameof(IsCheckingTeacherSetupVeyon));
        Changed(nameof(CanRefreshTeacherSetupVeyon));
        NotifyTeacherSetupNavigationStateChanged();
        try
        {
            var facts = await Task.Run(VeyonFacts.Probe);
            _teacherSetupVeyonReady = facts.Status == "installed" &&
                VeyonFacts.IsSupportedVersionDetail(facts.VersionDetail);
            _teacherSetupVeyonStatus = _teacherSetupVeyonReady
                ? $"已检测到受支持的教师端 Veyon（{facts.VersionDetail}）。"
                : facts.Status == VeyonFacts.NotInstalled
                    ? "尚未检测到教师端 Veyon。请安装后重新检查。"
                    : facts.Status == "installed"
                        ? $"检测到 Veyon，但版本未能确认或不受当前控制台支持（要求 {VeyonInstallerTrust.Version}）。请先处理版本后重新检查。\n{facts.AsText()}"
                        : $"无法确认 Veyon 状态；请重新检查或查看安装问题。\n{facts.AsText()}";
            Changed(nameof(TeacherSetupVeyonStatus));
        }
        catch (Exception exception)
        {
            _teacherSetupVeyonReady = false;
            _teacherSetupVeyonStatus = "读取 Veyon 状态失败；没有更改系统。" + exception.Message;
            Changed(nameof(TeacherSetupVeyonStatus));
        }
        finally
        {
            _isCheckingTeacherSetupVeyon = false;
            Changed(nameof(IsCheckingTeacherSetupVeyon));
            Changed(nameof(CanRefreshTeacherSetupVeyon));
            NotifyTeacherSetupNavigationStateChanged();
        }
    }

    private void NotifyTeacherSetupNavigationStateChanged()
    {
        Changed(nameof(IsTeacherSetupVeyonReady));
        Changed(nameof(IsTeacherSetupRoomComplete));
        Changed(nameof(IsTeacherSetupRoomSkippedForNow));
        Changed(nameof(CanOpenTeacherSetupStep2));
        Changed(nameof(CanOpenTeacherSetupStep3));
        Changed(nameof(CanProceedTeacherSetupToRooms));
        Changed(nameof(CanProceedTeacherSetupToPackage));
        Changed(nameof(CanSkipTeacherSetupRoom));
        Changed(nameof(IsWindowsTeacherHost));
        Changed(nameof(IsMacTeacherHost));
        Changed(nameof(TrustMigrationPackageGuidance));
        Changed(nameof(TeacherSetupStep1Label));
        Changed(nameof(TeacherSetupStep2Label));
        Changed(nameof(TeacherSetupStep3Label));
        Changed(nameof(TeacherSetupRoomSummary));
    }

    public string TrustMigrationSourcePackageDirectory
    {
        get => _trustMigrationSourcePackageDirectory;
        set
        {
            var path = value?.Trim() ?? "";
            if (_trustMigrationSourcePackageDirectory == path) return;
            _trustMigrationSourcePackageDirectory = path;
            PackageOutput = "";
            PackageOutputError = "";
            SetMacVeyonKeyImportStatus("尚未导入与当前学生配置包匹配的 Veyon 教师私钥。");
            _ = ValidateTrustMigrationSourcePackageAsync(path);
            Changed();
            Changed(nameof(HasTrustMigrationSourcePackageDirectory));
            Changed(nameof(CanImportMacVeyonPrivateKey));
            Changed(nameof(MacVeyonPrivateKeyImportInstructions));
            Changed(nameof(CanGenerateStudentPackage));
            Changed(nameof(TrustMigrationPackageGuidance));
            NotifyTeacherSetupNavigationStateChanged();
        }
    }

    public bool HasTrustMigrationSourcePackageDirectory =>
        Directory.Exists(TrustMigrationSourcePackageDirectory);

    public string TrustMigrationPackageGuidance => !IsMacTeacherHost
        ? "首次部署时生成配置包，并与最新 StudentSetup 一起交给部署人员。"
        : _isValidatingTrustMigrationSourcePackage
            ? "正在验证原学生配置包；通过校验后才能生成策略密钥更新包。"
            : HasTrustMigrationSourcePackageDirectory && !_trustMigrationSourcePackageValidated
                ? "原学生配置包未通过校验；请根据下方原因重新选择完整配置包。"
            : _trustMigrationSourcePackageValidated
            ? "将保留原配置包中的 Veyon 公钥和电脑前缀，仅替换为此 Mac 钥匙串中的策略公钥。请先更新 StudentSetup，再在每台学生机以管理员身份重新应用此配置包；旧策略会先按原流程恢复。"
            : "选择最初由 Windows 教师端生成的学生配置包。Mac 会保留其中的 Veyon 公钥，使用本机钥匙串中的新策略密钥生成更新包；随后需在每台学生机运行最新 StudentSetup。";

    private void SetMacVeyonKeyImportStatus(string status)
    {
        if (string.Equals(_macVeyonKeyImportStatus, status, StringComparison.Ordinal)) return;
        _macVeyonKeyImportStatus = status;
        Changed(nameof(MacVeyonKeyImportStatus));
    }

    public string TeacherHeartbeatStatus
    {
        get => _teacherHeartbeatStatus;
        private set
        {
            if (_teacherHeartbeatStatus == value) return;
            _teacherHeartbeatStatus = value;
            Changed();
            Changed(nameof(HasTeacherHeartbeatStatus));
        }
    }

    public bool HasTeacherHeartbeatStatus => _teacherHeartbeatState?.PackageId is not null;

    public string AppVersion => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "版本未知";

    public bool IsInstallingTeacherVeyon
    {
        get => _isInstallingTeacherVeyon;
        private set { if (_isInstallingTeacherVeyon == value) return; _isInstallingTeacherVeyon = value; Changed(); }
    }

    public bool CanInstallTeacherVeyon => OperatingSystem.IsWindows() && !IsExecuting;

    public bool CanCheckTeacherUpdate => OperatingSystem.IsWindows() && _releaseClient is not null && !IsExecuting && !_isCheckingTeacherUpdate && !_isDownloadingTeacherUpdate && !_isVerifyingOfflineTeacherUpdate;

    public bool CanDownloadTeacherUpdate => OperatingSystem.IsWindows() && !IsExecuting && !_isCheckingTeacherUpdate &&
        !_isDownloadingTeacherUpdate && !_isVerifyingOfflineTeacherUpdate && _teacherUpdateAvailable &&
        _teacherUpdateRelease is not null && HasRequiredPolicyCapabilities(_teacherUpdateRelease.Manifest);

    public bool CanExportOfflineTeacherUpdate => CanDownloadTeacherUpdate;

    public bool CanVerifyOfflineTeacherUpdate => OperatingSystem.IsWindows() && _releaseClient is not null && !IsExecuting &&
        !_isCheckingTeacherUpdate && !_isDownloadingTeacherUpdate && !_isVerifyingOfflineTeacherUpdate;

    public bool CanInstallOfflineTeacherUpdate => OperatingSystem.IsWindows() && _releaseClient is not null &&
        !IsExecuting && !_isCheckingTeacherUpdate && !_isDownloadingTeacherUpdate && !_isVerifyingOfflineTeacherUpdate &&
        _offlineTeacherUpdateRelease is not null && _offlineTeacherInstallerPath is not null &&
        HasRequiredPolicyCapabilities(_offlineTeacherUpdateRelease.Manifest) &&
        ApplicationReleaseClient.CompareVersions(_offlineTeacherUpdateRelease.Manifest.Version, AppVersion) > 0;

    public bool CanTrustStudentAgentIdentities => !IsExecuting && AreStudentUpdateTargetsValid();

    private static bool IsHealthRepairAllowed(TeacherDeviceHealthRow row,
        StudentAgentHealthRepairAction action) => action switch
    {
        StudentAgentHealthRepairAction.EnsureVeyonServiceRunning => row.CanEnsureVeyonServiceRunning,
        StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy => row.CanRefreshLegacyEdgePolicy,
        _ => false
    };

    private void ReconcileDeviceHealthAlerts()
    {
        var hadAlerts = _deviceHealthAlerts.Count > 0;
        var liveGroups = _deviceHealthRows.SelectMany(row => row.Issues.Select(issue => (Row: row, Issue: issue)))
            .GroupBy(pair => pair.Issue.Code, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        // A partial runtime poll can briefly omit an issue while the device row and its
        // previous alert are still in the current selection. Preserve that alert locally
        // until a later signed snapshot confirms recovery; otherwise the summary card
        // disappears between poll results even though the device row remains visible.
        var selectedRowsByTarget = _deviceHealthRows.ToDictionary(row => NormalizeTargetKey(row.Target),
            StringComparer.OrdinalIgnoreCase);
        var selectedTargetKeys = GetDeviceHealthTargets().Select(NormalizeTargetKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preservedRows = new List<TeacherDeviceHealthRow>();
        foreach (var alert in _deviceHealthAlerts.Where(alert => !liveGroups.ContainsKey(alert.IssueCode)))
        {
            var currentRows = alert.AffectedRows
                .Select(row => selectedRowsByTarget.GetValueOrDefault(NormalizeTargetKey(row.Target)))
                .Where(row => row is not null)
                .Cast<TeacherDeviceHealthRow>()
                .Distinct()
                .ToArray();
            if (currentRows.Length == 0) continue;
            foreach (var row in currentRows)
                if (row.PreserveAlertForLocalReview(alert)) preservedRows.Add(row);
        }

        var cachedGroups = _deviceHealthRows
            .SelectMany(row => row.CachedIssueSummaries
                .Where(issue => !row.Issues.Any(live => live.Code == issue.Code))
                .Select(issue => (Row: row, Issue: issue)))
            .GroupBy(pair => pair.Issue.Code, StringComparer.Ordinal)
            .Where(group => !liveGroups.ContainsKey(group.Key))
            .ToArray();
        var desired = new Dictionary<string,
            (TeacherDeviceHealthRow[] Rows, TeacherDeviceHealthIssue Issue, bool IsCached,
                DateTimeOffset CachedAt, TeacherDeviceHealthCachedIssue? CachedIssue)>(StringComparer.Ordinal);
        foreach (var (issueCode, items) in liveGroups)
        {
            var rows = items.Select(item => item.Row).Distinct().ToArray();
            var severity = items.Any(item => item.Issue.Severity == TeacherDeviceHealthAlertSeverity.Critical)
                ? TeacherDeviceHealthAlertSeverity.Critical
                : items.Any(item => item.Issue.Severity == TeacherDeviceHealthAlertSeverity.Warning)
                    ? TeacherDeviceHealthAlertSeverity.Warning
                    : rows.All(row => row.Category == TeacherDeviceHealthCategory.NoResponse)
                    ? TeacherDeviceHealthAlertSeverity.Informational
                    : items.Min(item => item.Issue.Severity);
            desired[issueCode] = (rows, items[0].Issue with { Severity = severity }, false,
                DateTimeOffset.MinValue, null);
        }
        foreach (var group in cachedGroups)
        {
            var rows = group.Select(item => item.Row).Distinct().ToArray();
            var cachedIssues = group.Select(item => item.Issue).ToArray();
            var unresolvedIssue = cachedIssues.Where(issue => !issue.IsResolved)
                .OrderByDescending(issue => issue.LastSeenUtc ?? DateTimeOffset.MinValue)
                .FirstOrDefault();
            var cachedIssue = (unresolvedIssue ?? cachedIssues
                    .OrderByDescending(issue => issue.LastSeenUtc ?? DateTimeOffset.MinValue)
                    .First()) with
            {
                IsResolved = unresolvedIssue is null,
                ResolvedUtc = unresolvedIssue is null
                    ? cachedIssues.Select(issue => issue.ResolvedUtc).OfType<DateTimeOffset>()
                        .DefaultIfEmpty(DateTimeOffset.UtcNow).Max()
                    : null,
                LastSeenUtc = cachedIssues.Select(issue => issue.LastSeenUtc).Max()
            };
            // Keep the same issue key when a live alert becomes a cached record or is later
            // confirmed recovered. Replacing the collection item made the alert briefly vanish.
            var issueCode = group.Key;
            var issue = new TeacherDeviceHealthIssue(issueCode, cachedIssue.Title,
                TeacherDeviceHealthAlertSeverity.Informational, cachedIssue.Detail);
            desired[issueCode] = (rows, issue, true, rows.Max(row => row.CacheSavedUtc), cachedIssue);
        }

        var existing = _deviceHealthAlerts.ToDictionary(alert => alert.IssueCode, StringComparer.Ordinal);
        foreach (var (issueCode, group) in desired)
        {
            if (!existing.TryGetValue(issueCode, out var alert))
            {
                alert = new TeacherDeviceHealthAlert(issueCode, group.Issue.Title, group.Issue.Severity);
                _deviceHealthAlerts.Add(alert);
            }
            var isNewAlert = !existing.ContainsKey(issueCode);
            if (group.IsCached)
                alert.UpdateFromCache(group.Rows, group.CachedIssue!, group.CachedAt);
            else
                alert.Update(group.Rows, isNewAlert, group.Issue.Severity);
            alert.SetBusy(IsExecuting);
        }

        foreach (var stale in _deviceHealthAlerts.Where(alert => !desired.ContainsKey(alert.IssueCode)).ToArray())
        {
            // A roster refresh can temporarily publish an empty selection before the same
            // devices are added back. Keep the alert as a local, read-only record during
            // that handoff; otherwise the summary panel disappears even though the cached
            // device rows return as soon as the refreshed roster is applied.
            if (selectedTargetKeys.Count == 0 && stale.AffectedRows.Count > 0)
            {
                var reviewRows = stale.AffectedRows.Distinct().ToArray();
                foreach (var row in reviewRows)
                {
                    row.PreserveAlertForLocalReview(stale);
                    if (!preservedRows.Contains(row)) preservedRows.Add(row);
                }
                stale.MarkPendingReview(reviewRows);
                continue;
            }

            // A transient snapshot can omit an issue while its device row is still present.
            // Keep the original card as a read-only pending record whenever any affected
            // target is still selected; row/cache reconciliation will update it when the
            // next trusted status arrives. This also covers a row handoff where its new row
            // has not been attached yet.
            var stillSelectedRows = stale.AffectedRows.Where(row =>
                selectedTargetKeys.Contains(NormalizeTargetKey(row.Target))).ToArray();
            if (stillSelectedRows.Length > 0)
            {
                var changed = false;
                var reviewRows = stillSelectedRows.Select(previousRow =>
                        selectedRowsByTarget.GetValueOrDefault(NormalizeTargetKey(previousRow.Target)) ?? previousRow)
                    .Distinct().ToArray();
                foreach (var row in reviewRows)
                    changed |= row.PreserveAlertForLocalReview(stale);
                if (changed)
                    foreach (var row in reviewRows)
                        if (!preservedRows.Contains(row)) preservedRows.Add(row);
                if (stale.MarkPendingReview(reviewRows))
                    foreach (var row in reviewRows)
                        if (!preservedRows.Contains(row)) preservedRows.Add(row);
                continue;
            }

            _deviceHealthAlerts.Remove(stale);
        }

        if (preservedRows.Count > 0 && _deviceHealthCacheStore is not null)
            SaveDeviceHealthCache(CampusId.Trim(), preservedRows);

        var ordered = _deviceHealthAlerts.OrderBy(alert => alert.Severity)
            .ThenByDescending(alert => alert.LastSeenUtc).ThenBy(alert => alert.Title, StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var currentIndex = _deviceHealthAlerts.IndexOf(ordered[index]);
            if (currentIndex >= 0 && currentIndex != index)
                _deviceHealthAlerts.Move(currentIndex, index);
        }
        Changed(nameof(DeviceHealthAlertsHeader));
        Changed(nameof(DeviceHealthAlertsDescription));
        if (hadAlerts != (_deviceHealthAlerts.Count > 0)) Changed(nameof(HasDeviceHealthAlerts));
    }

    private static TeacherDeviceHealthBatchState ToTeacherDeviceHealthBatchState(
        StudentAgentHealthRepairProgressState state) => state switch
    {
        StudentAgentHealthRepairProgressState.Queued => TeacherDeviceHealthBatchState.Queued,
        StudentAgentHealthRepairProgressState.Delivered => TeacherDeviceHealthBatchState.Delivered,
        StudentAgentHealthRepairProgressState.Executing => TeacherDeviceHealthBatchState.Executing,
        StudentAgentHealthRepairProgressState.Succeeded => TeacherDeviceHealthBatchState.Succeeded,
        StudentAgentHealthRepairProgressState.TimedOut => TeacherDeviceHealthBatchState.TimedOut,
        _ => TeacherDeviceHealthBatchState.Failed
    };

    private void UpdateDeviceHealthSummary(int healthy, int attention, int manual, int noResponse) =>
        DeviceHealthStatus = $"已读取 {_deviceHealthRows.Count} 台：正常 {healthy} · 需关注 {attention} · 需人工处理 {manual} · 未收到回执 {noResponse}";

    private void RefreshDeviceHealthView()
    {
        var search = DeviceHealthSearchText.Trim();
        var filterIndex = DeviceHealthCategoryFilterIndex;
        var filtered = _deviceHealthRows
            .Where(row => filterIndex switch
            {
                1 => row.Category == TeacherDeviceHealthCategory.Healthy,
                2 => row.Category is TeacherDeviceHealthCategory.Attention or
                    TeacherDeviceHealthCategory.NeedsManualReview,
                3 => row.Category is TeacherDeviceHealthCategory.NoResponse or
                    TeacherDeviceHealthCategory.Pending,
                _ => true
            })
            .Where(row => search.Length == 0 ||
                ContainsHealthSearch(row.StudentName, search) ||
                ContainsHealthSearch(row.CategoryLabel, search) ||
                ContainsHealthSearch(row.AgentSummary, search) ||
                ContainsHealthSearch(row.ServiceSummary, search) ||
                ContainsHealthSearch(row.PolicySummary, search) ||
                ContainsHealthSearch(row.UpdateSummary, search) ||
                ContainsHealthSearch(row.Detail, search))
            .OrderBy(row => row.Category switch
            {
                TeacherDeviceHealthCategory.NeedsManualReview => 0,
                TeacherDeviceHealthCategory.Attention => 1,
                TeacherDeviceHealthCategory.NoResponse => 2,
                TeacherDeviceHealthCategory.Pending => 3,
                _ => 4
            })
            .ThenBy(row => row.StudentName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        _deviceHealthFilteredCount = filtered.Length;
        var pageCount = Math.Max(1, (int)Math.Ceiling(filtered.Length / (double)DeviceHealthPageSize));
        _deviceHealthPageIndex = Math.Clamp(_deviceHealthPageIndex, 0, pageCount - 1);
        var page = filtered.Skip(_deviceHealthPageIndex * DeviceHealthPageSize)
            .Take(DeviceHealthPageSize).ToArray();
        if (_deviceHealthPageRows.Count != page.Length || !_deviceHealthPageRows.SequenceEqual(page))
        {
            _deviceHealthPageRows = Array.AsReadOnly(page);
            Changed(nameof(DeviceHealthPageRows));
        }
        Changed(nameof(HasDeviceHealthRows));
        Changed(nameof(HasDeviceHealthFilteredRows));
        Changed(nameof(HasDeviceHealthFilterEmpty));
        Changed(nameof(DeviceHealthAllCount));
        Changed(nameof(DeviceHealthListSummary));
        Changed(nameof(DeviceHealthHealthyCount));
        Changed(nameof(DeviceHealthAttentionCount));
        Changed(nameof(DeviceHealthManualReviewCount));
        Changed(nameof(DeviceHealthNoResponseCount));
        Changed(nameof(DeviceHealthPendingCount));
        Changed(nameof(DeviceHealthNeedsActionCount));
        Changed(nameof(DeviceHealthUnconfirmedCount));
        Changed(nameof(DeviceHealthPageCount));
        Changed(nameof(HasMultipleDeviceHealthPages));
        Changed(nameof(DeviceHealthPageLabel));
        Changed(nameof(CanPreviousDeviceHealthPage));
        Changed(nameof(CanNextDeviceHealthPage));
    }

    public bool HasStudentUpdatePerformanceReport => _lastStudentUpdatePerformanceReport is not null;

    public ObservableCollection<StudentConfigurationSyncTargetProgressItem> StudentConfigurationSyncTargetProgressItems =>
        _studentConfigurationSyncTargetProgressItems;

    public bool IsLocalTestStudentUpdateSourceAvailable
    {
        get
        {
#if VEYONCAMPUS_LOCAL_TEST_UPDATE_UI
            return ApplicationReleaseTrust.HasPinnedPublicKey;
#else
            return false;
#endif
        }
    }

    public bool CanDeployStudentUpdate => OperatingSystem.IsWindows() && !IsExecuting && _releaseClient is not null &&
        AreStudentUpdateTargetsValid() && (!_useLocalTestStudentUpdateSource ||
            (IsLocalTestStudentUpdateSourceAvailable && !string.IsNullOrWhiteSpace(_localTestStudentSetupInstallerPath)));

    public bool CanStartMaintenanceOnSelectedTargets => OperatingSystem.IsWindows() && !IsExecuting &&
        _maintenanceWorkflow is not null && AreStudentUpdateTargetsValid();

    public bool CanMaintainStudentDevices => CanStartMaintenanceOnSelectedTargets &&
        (!_useLocalTestStudentUpdateSource || IsLocalTestStudentUpdateSourceAvailable &&
            !string.IsNullOrWhiteSpace(_localTestStudentSetupInstallerPath));

    // 「一键静默更新」自动作用于当前地点全部学生电脑，不依赖教师事先勾选。
    public bool CanOneClickUpdateAllStudentDevices => OperatingSystem.IsWindows() && !IsExecuting &&
        _maintenanceWorkflow is not null && IsCampusIdValid() && WebsitePolicyTargetChoices.Count > 0 &&
        AreWebsiteTargetsTrustedForActions() &&
        AreTargetsConflictFree(WebsitePolicyTargetChoices.Select(item => item.Target)) &&
        (!_useLocalTestStudentUpdateSource || IsLocalTestStudentUpdateSourceAvailable &&
            !string.IsNullOrWhiteSpace(_localTestStudentSetupInstallerPath));

    public bool CanUpdateMaintenanceService => CanStartMaintenanceOnSelectedTargets &&
        (!_useLocalTestStudentUpdateSource || IsLocalTestStudentUpdateSourceAvailable &&
            !string.IsNullOrWhiteSpace(_localTestMaintenanceInstallerPath));

    public string StudentMaintenanceStatus
    {
        get => _studentMaintenanceStatus;
        private set { if (_studentMaintenanceStatus == value) return; _studentMaintenanceStatus = value; Changed(); }
    }

    public string MaintenanceEventReportExportStatus { get; private set; } = "";

    public ObservableCollection<TeacherMaintenanceIssueItem> MaintenanceIssueItems { get; } = [];

    public bool HasMaintenanceIssueItems => MaintenanceIssueItems.Count > 0;

    public ObservableCollection<TeacherMaintenanceConfigurationItem> MaintenanceConfigurationItems { get; } = [];

    public bool HasMaintenanceConfigurationItems => MaintenanceConfigurationItems.Count > 0;

    public ObservableCollection<PendingMaintenanceIdentityRotationItem> PendingMaintenanceIdentityRotations { get; } = [];

    public bool HasPendingMaintenanceIdentityRotations => PendingMaintenanceIdentityRotations.Count > 0;

    public string MaintenanceIdentityRotationStatus { get; private set; } = "";

    public bool HasMaintenanceIdentityRotationStatus => !string.IsNullOrWhiteSpace(MaintenanceIdentityRotationStatus);

    public string MaintenanceOverviewSummary { get; private set; } = "尚未读取本机 Maintenance 记录。";

    public string MaintenanceConfigurationOverviewSummary { get; private set; } =
        "尚无本机配置对账记录。";

    public bool UseLocalTestStudentUpdateSource
    {
        get => _useLocalTestStudentUpdateSource;
        set
        {
            if (value && !IsLocalTestStudentUpdateSourceAvailable) return;
            if (_useLocalTestStudentUpdateSource == value) return;
            _useLocalTestStudentUpdateSource = value;
            Changed();
            Changed(nameof(StudentUpdateSourceSummary));
            Changed(nameof(CanDeployStudentUpdate));
            Changed(nameof(CanUpdateMaintenanceService));
        }
    }

    public string LocalTestStudentSetupInstallerPath => _localTestStudentSetupInstallerPath ?? "未选择本机测试安装器";

    public string LocalTestMaintenanceInstallerPath => _localTestMaintenanceInstallerPath ?? "未选择本机测试 MaintenanceService 安装器";

    public string StudentUpdateSourceSummary => _useLocalTestStudentUpdateSource && IsLocalTestStudentUpdateSourceAvailable
        ? "当前来源：本机测试包。使用 Developer Release 公钥校验；安装器只从本机读取，不上传。" +
          (_localTestStudentReleaseSummary is null ? "" : " " + _localTestStudentReleaseSummary) +
          (_localTestMaintenanceReleaseSummary is null ? "" : " " + _localTestMaintenanceReleaseSummary)
        : "当前来源：线上正式版。版本来自线上签名发布目录。";

    public bool CanSyncStudentConfiguration => OperatingSystem.IsWindows() && !IsExecuting &&
        AreStudentUpdateTargetsValid();

    public string StudentConfigurationSyncStatus
    {
        get => _studentConfigurationSyncStatus;
        private set { if (_studentConfigurationSyncStatus == value) return; _studentConfigurationSyncStatus = value; Changed(); }
    }

    public bool HasStudentConfigurationSyncTargetProgressItems => _studentConfigurationSyncTargetProgressItems.Count > 0;

    public bool IsStudentConfigurationSyncProgressVisible => _isStudentConfigurationSyncProgressVisible;

    public double StudentConfigurationSyncProgressPercent => _studentConfigurationSyncProgressPercent;

    public string StudentConfigurationSyncProgressText => _studentConfigurationSyncProgressText;

    private static bool HasRequiredPolicyCapabilities(ApplicationReleaseManifest manifest) =>
        ApplicationReleaseCompatibility.SupportsApplicationPolicy(manifest) &&
        ApplicationReleaseCompatibility.SupportsStudentSystemPolicy(manifest);

    public string TeacherUpdateStatus
    {
        get => _teacherUpdateStatus;
        private set
        {
            if (_teacherUpdateStatus == value) return;
            _teacherUpdateStatus = value;
            Changed();
        }
    }

    public bool IsTeacherUpdateProgressVisible => _isTeacherUpdateProgressVisible;

    public double TeacherUpdateProgressPercent => _teacherUpdateProgressPercent;

    public bool TeacherUpdateProgressIsIndeterminate => _teacherUpdateProgressIsIndeterminate;

    public string TeacherUpdateProgressText => _teacherUpdateProgressText;

    public string TeacherUpdateLatestVersion
    {
        get => _teacherUpdateLatestVersion;
        private set
        {
            if (_teacherUpdateLatestVersion == value) return;
            _teacherUpdateLatestVersion = value;
            Changed();
            Changed(nameof(TeacherUpdateVersionSummary));
        }
    }

    public string OfflineTeacherUpdateStatus
    {
        get => _offlineTeacherUpdateStatus;
        private set { if (_offlineTeacherUpdateStatus == value) return; _offlineTeacherUpdateStatus = value; Changed(); }
    }

    public bool HasTeacherUpdate => _teacherUpdateAvailable && _teacherUpdateRelease is not null;

    public string StudentUpdateStatus
    {
        get => _studentUpdateStatus;
        private set { if (_studentUpdateStatus == value) return; _studentUpdateStatus = value; Changed(); }
    }

    public string StudentUpdatePerformanceReportExportStatus { get; private set; } = "";

    public bool IsStudentUpdateProgressVisible => _isStudentUpdateProgressVisible;

    public double StudentUpdateProgressPercent => _studentUpdateProgressPercent;

    public bool StudentUpdateProgressIsIndeterminate => _studentUpdateProgressIsIndeterminate;

    public string StudentUpdateProgressText => _studentUpdateProgressText;

    public ObservableCollection<StudentApplicationUpdateTargetProgressItem> StudentUpdateTargetProgressItems { get; } = [];

    public bool HasStudentUpdateTargetProgressItems => StudentUpdateTargetProgressItems.Count > 0;

    public string UpdateDiagnosticsStatus
    {
        get => _updateDiagnosticsStatus;
        private set
        {
            if (_updateDiagnosticsStatus == value) return;
            _updateDiagnosticsStatus = value;
            Changed();
            Changed(nameof(HasUpdateDiagnosticsStatus));
        }
    }

    public bool CanGenerateStudentPackage => !IsExecuting &&
        (OperatingSystem.IsWindows() || (OperatingSystem.IsMacOS() &&
            _trustMigrationSourcePackageValidated && !_isValidatingTrustMigrationSourcePackage));

    public bool CanPublishStudentPackage => OperatingSystem.IsWindows() && !IsExecuting &&
        IsPackagePublisherDetailsValid() && Directory.Exists(PublishPackageDirectory);

    public bool CanPushWebsitePolicy => !IsExecuting && !IsReadingWebsiteLocations &&
        !_websiteLocationSelectionPending && IsWebsitePolicyInputValid();

    public bool CanDisableWebsitePolicy => !IsExecuting && !IsReadingWebsiteLocations &&
        !_websiteLocationSelectionPending && GetSelectedWebsitePolicyTargets().Count > 0 && IsCampusIdValid();

    public bool CanReadWebsitePolicyStatus => !IsExecuting &&
        GetSelectedWebsitePolicyTargets().Count > 0 && IsCampusIdValid();

    public bool CanMigrateEdgeRealtime => OperatingSystem.IsWindows() && !IsExecuting &&
        ConfiguredEdgeExtensionId() is not null && GetSelectedWebsitePolicyTargets().Count > 0 && IsCampusIdValid();

    public bool CanRestoreEdgeLegacy => CanMigrateEdgeRealtime;

    public string EdgeRealtimeMigrationButtonLabel
    {
        get
        {
#if VEYONCAMPUS_EDGE_REALTIME_PILOT
            return "启用 Edge 实时同步（试用）";
#else
            return "启用 Edge 实时同步";
#endif
        }
    }

    public bool IsEdgeRealtimePilotBuild
    {
        get
        {
#if VEYONCAMPUS_EDGE_REALTIME_PILOT
            return true;
#else
            return false;
#endif
        }
    }

    public bool CanFillFailedWebsiteTargets => !IsExecuting &&
        string.Equals(CampusId.Trim(), _lastFailedWebsiteTargetsCampus, StringComparison.Ordinal) &&
        _lastFailedWebsiteTargets.Select(NormalizeTargetKey).Intersect(
            WebsitePolicyTargetChoices.Select(choice => NormalizeTargetKey(choice.Target)),
            StringComparer.OrdinalIgnoreCase).Any();

    public bool CanPushApplicationPolicy => !IsExecuting &&
        IsApplicationPolicyInputValid() && (ApplicationPolicyModeIndex == 0 ||
            (ApplicationEnforcementReviewed && HasMatchingApplicationAudit));

    public bool CanDisableApplicationPolicy => !IsExecuting && AreWebsitePolicyTargetsValid();

    public bool CanPushStudentSystemPolicy => !IsExecuting &&
        IsStudentSystemPolicyInputValid() &&
        (!_studentSystemPolicyProhibitSoftwareInstallation || _studentSystemPolicySoftwareInstallReviewed) &&
        _studentSystemPolicyImpactReviewed;

    public string StudentSystemPolicyPushGuidance => GetStudentSystemPolicyPushGuidance();

    public bool CanDisableStudentSystemPolicy => !IsExecuting && AreWebsitePolicyTargetsValid();

    public bool HasSelectedStudentSystemPolicy => !CurrentStudentSystemPolicySettings().IsEmpty;

    public bool CanReadApplicationPolicyAudit => !IsExecuting && AreWebsitePolicyTargetsValid();

    public bool CanReadApplicationInventory => !IsExecuting && AreWebsitePolicyTargetsValid();

    public bool CanGenerateApplicationCandidates => !IsExecuting && ApplicationCatalogAllTargetsRead &&
        _allApplicationCatalogChoices.Any(choice => choice.IsSelected) &&
        SelectedApplicationCatalogCandidatesByTarget().All(pair => pair.Value.Count <= ApplicationCatalogCryptography.MaximumSelectedItems);

    public bool CanAddSelectedApplicationRules => !IsExecuting && _allApplicationInventoryChoices.Any(item => item.IsSelected && item.CanSelect);

    public bool CanCheckRoomConflicts => OperatingSystem.IsWindows() && !IsExecuting && !IsCheckingRoomConflicts &&
        !_isClassroomTransitioning;

    public bool CanEditRoomPlan => !IsExecuting;

    public bool CanAddRoomToVeyon => CanStartRoomCreation && HasRoomPreview && RoomError.Length == 0 &&
        SeatGridUnboundComputerCount > 0 && !_isClassroomTransitioning &&
        _roomConflictCheckCompleted && _roomConflictCheckRevision == _roomPlanRevision &&
        _roomConflictResults.Length == 0;

    public bool CanReadWebsiteLocations =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) &&
        !IsExecuting && !IsReadingWebsiteLocations;

    public bool CanUseSelectedClassroomLocation => !IsExecuting && !IsReadingWebsiteLocations &&
        ClassroomLocationIndex >= 0 && ClassroomLocationIndex < WebsiteLocations.Count &&
        (_classroomDirectorySelectionPending ||
         !IsCurrentClassroomLocation(WebsiteLocations[ClassroomLocationIndex]));

    public bool CanSyncSeatGridWithVeyon => OperatingSystem.IsWindows() && !IsExecuting &&
        !IsReadingWebsiteLocations && !_isReadingSeatVeyonTargets && !IsPreparingSeatSwapPlan &&
        (!_classroomDirectorySelectionPending || CanUseSelectedClassroomLocation);

    public string SeatGridVeyonSyncButtonLabel => _classroomDirectorySelectionPending
        ? "使用所选地点并读取电脑" : "同步 Veyon 地点和电脑";

    public bool CanApplyWebsiteLocation => !IsExecuting && !IsReadingWebsiteLocations &&
        _websiteLocationIndex >= 0 && _websiteLocationIndex < WebsiteLocations.Count;

    private void RecordUpdateSuccess(UpdateDiagnosticModule module, UpdateDiagnosticOperation operation,
        string? targetVersion, UpdateDiagnosticCounts? counts = null) =>
        TryAppendUpdateDiagnostic(UpdateDiagnosticEntry.Create(module, operation, AppVersion, targetVersion,
            counts: counts));

    private void RecordUpdateHandoffStarted(UpdateDiagnosticModule module, UpdateDiagnosticOperation operation,
        string? targetVersion) =>
        TryAppendUpdateDiagnostic(UpdateDiagnosticEntry.HandoffStarted(module, operation, AppVersion, targetVersion));

    private async Task SendCampusOperationsReportsAsync()
    {
        if (!OperatingSystem.IsWindows() ||
            Interlocked.CompareExchange(ref _operationsTelemetryInFlight, 1, 0) != 0) return;

        var cancellation = new CancellationTokenSource();
        lock (_operationsTelemetryGate) _operationsTelemetryCancellation = cancellation;
        try
        {
            var preference = _operationsTelemetryStore.LoadPreference();
            if (!preference.Enabled || preference.EnabledAtUtc is not { } enabledAtUtc) return;
            if (_teacherHeartbeatState is not { PackageId: not null } identity)
            {
                OperationsTelemetryStatus = "已开启；发布校区配置包后开始按日汇总。";
                return;
            }
            if (_operationsTelemetryClient is null)
            {
                OperationsTelemetryStatus = _operationsTelemetryClientError ?? "上报 API 配置不可用；数据仍留在本机。";
                return;
            }

            var reports = CampusOperationsTelemetrySummaryBuilder.Build(
                _updateDiagnostics.ReadRecent(), _classroomSessionStore.ReadRecent(), enabledAtUtc,
                CampusOperationsTelemetrySummaryBuilder.HongKongDate(DateTimeOffset.UtcNow));
            _operationsTelemetryStore.QueueReports(reports);
            var pending = _operationsTelemetryStore.ReadPending();
            if (pending.Count == 0)
            {
                OperationsTelemetryStatus = "已开启；只汇总启用后的数据，目前没有待上报结果。";
                return;
            }

            foreach (var report in pending)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!_operationsTelemetryStore.LoadPreference().Enabled) return;
                await _operationsTelemetryClient.UploadAsync(identity, report, cancellation.Token)
                    .ConfigureAwait(false);
                _operationsTelemetryStore.MarkSent(report.ReportDate);
            }
            OperationsTelemetryStatus = $"已发送 {pending.Count} 天的匿名运维汇总。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // User disabled reporting while a bounded request was in progress.
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
                                          UnauthorizedAccessException or InvalidDataException or InvalidOperationException or
                                          TaskCanceledException)
        {
            OperationsTelemetryStatus = "暂时无法上报；匿名汇总留在本机，联网后自动重试。";
        }
        finally
        {
            lock (_operationsTelemetryGate)
            {
                if (ReferenceEquals(_operationsTelemetryCancellation, cancellation))
                    _operationsTelemetryCancellation = null;
            }
            cancellation.Dispose();
            Volatile.Write(ref _operationsTelemetryInFlight, 0);
        }
    }

    private async Task SendTeacherCampusHeartbeatAsync()
    {
        if (!OperatingSystem.IsWindows() || Interlocked.CompareExchange(ref _teacherHeartbeatInFlight, 1, 0) != 0)
            return;
        try
        {
            if (_teacherHeartbeatState is not { Enabled: true, PackageId: not null } state) return;
            if (_teacherHeartbeatClient is null)
            {
                TeacherHeartbeatStatus = _teacherHeartbeatClientError ?? "Teacher 心跳 API 配置不可用。";
                return;
            }
            var now = DateTimeOffset.UtcNow;
            if (!TeacherCampusHeartbeatStateStore.IsDue(state,
                    TeacherCampusHeartbeatStateStore.GetHongKongDate(), now))
            {
                TeacherHeartbeatStatus = state.LastSentDay != TeacherCampusHeartbeatStateStore.GetHongKongDate() &&
                                         state.FirstHeartbeatNotBeforeUtc is { } notBefore && notBefore > now
                    ? "首次校区心跳已安排，将在配置包发布 1 小时后发送。"
                    : "今日校区状态已同步。";
                return;
            }
            TeacherHeartbeatStatus = "正在读取 Veyon 机房电脑总数并发送匿名校区汇总……";
            var locations = await Task.Run(VeyonNetworkObjectDirectory.ReadLocations);
            var configuredComputerCount = locations.SelectMany(location => location.Targets)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var heartbeat = await _teacherHeartbeatClient.TrySendOnceDailyAsync(state, AppVersion,
                AppVersion, configuredComputerCount);
            _teacherHeartbeatState = TeacherCampusHeartbeatStateStore.LoadOrCreate();
            if (heartbeat is not null)
                QueueNewerReleaseNotice(heartbeat.LatestReleases);
            TeacherHeartbeatStatus = heartbeat is not null
                ? $"服务已确认今日心跳发送：电脑总数 {configuredComputerCount}，教师版本 {AppVersion}。"
                : "今日校区状态已同步。";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException or
                                          UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or PlatformNotSupportedException)
        {
            TeacherHeartbeatStatus = "校区心跳未发送；不会影响 Teacher 功能：" + exception.Message;
        }
        finally { Volatile.Write(ref _teacherHeartbeatInFlight, 0); }
    }

    private void ScheduleInitialTeacherHeartbeat(DateTimeOffset sendAtUtc)
    {
        if (!OperatingSystem.IsWindows() ||
            Interlocked.CompareExchange(ref _initialTeacherHeartbeatWaitScheduled, 1, 0) != 0)
            return;
        _ = WaitForInitialTeacherHeartbeatAsync(sendAtUtc);
    }

    public string RoomPrefix { get => _roomPrefix; set { _roomPrefix = value ?? ""; Changed(); ClearRoomPreview(); } }

    public string RoomStart { get => _roomStart; set { _roomStart = value ?? ""; Changed(); ClearRoomPreview(); } }

    public string RoomLocationName
    {
        get => _roomLocationName;
        set
        {
            if (_roomLocationName == value) return;
            _roomLocationName = value ?? "";
            Changed();
            InvalidateRoomReview();
            ClearRoomCreateFeedback();
            Changed(nameof(CanAddRoomToVeyon));
            Changed(nameof(CanPrepareRoomSeatGrid));
            Changed(nameof(CanStartRoomCreation));
            Changed(nameof(RoomSummary));
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
        }
    }

    public IReadOnlyList<TeacherCampusProfile> CampusProfiles
    {
        get => _campusProfiles;
        private set { _campusProfiles = value; Changed(); Changed(nameof(HasCampusProfiles)); Changed(nameof(HasMultipleCampusProfiles)); Changed(nameof(ShowRoomLocationSettings)); }
    }

    public bool ShowRoomLocationSettings => _seatMapProfileSelectionRequired || (WebsiteLocations.Count > 0
        ? WebsiteLocations.Count > 1
        : CampusProfiles.SelectMany(campus => campus.Rooms.Select(room =>
                room.DisplayName + "\n" + string.Join("\n", MacTeacherNetworkDirectory.CreateLocation(campus, room)
                    .Targets.OrderBy(target => target, StringComparer.OrdinalIgnoreCase))))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() > 1);


public decimal ClassroomCountdownMinutes
    {
        get => _classroomCountdownMinutes;
        set
        {
            var next = Math.Clamp(decimal.Round(value, 0, MidpointRounding.AwayFromZero),
                ClassroomCountdown.MinimumMinutes, ClassroomCountdown.MaximumMinutes);
            if (_classroomCountdownMinutes == next) return;
            _classroomCountdownMinutes = next;
            Changed();
        }
    }

public bool HasClassroomCountdown => _activeClassroomCountdown is not null;

public bool IsClassroomCountdownRunning => _activeClassroomCountdown is { } countdown &&
        countdown.Remaining(DateTimeOffset.UtcNow) > TimeSpan.Zero;

public bool CanStartClassroomCountdown => HasActiveClassroomSession && !HasClassroomCountdown &&
                                             !HasClassroomCountdownError &&
                                             IsClassroomDirectoryScopeVerified;

public bool CanEndClassroomCountdown => HasActiveClassroomSession && HasClassroomCountdown;

public string ClassroomCountdownText
    {
        get
        {
            if (_activeClassroomCountdown is not { } countdown) return "";
            var seconds = (int)Math.Ceiling(countdown.Remaining(DateTimeOffset.UtcNow).TotalSeconds);
            if (seconds <= 0) return "时间到";
            var remaining = TimeSpan.FromSeconds(seconds);
            return remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
                : $"{remaining.Minutes:00}:{remaining.Seconds:00}";
        }
    }

public string ClassroomCountdownStatus
    {
        get => _classroomCountdownStatus;
        private set
        {
            if (_classroomCountdownStatus == value) return;
            _classroomCountdownStatus = value;
            Changed();
            Changed(nameof(HasClassroomCountdownStatus));
            Changed(nameof(HasClassroomCountdownError));
            Changed(nameof(HasClassroomCountdownInfo));
            Changed(nameof(CanStartClassroomCountdown));
        }
    }

public string ClassroomTaskProgressDraft
    {
        get => _classroomTaskProgressDraft;
        set
        {
            var next = value ?? "";
            if (_classroomTaskProgressDraft == next) return;
            _classroomTaskProgressDraft = next;
            Changed();
            Changed(nameof(CanAddClassroomTask));
        }
    }

public int ClassroomTaskProgressCompletedCount => _activeClassroomTaskProgress?.CompletedCount ?? 0;

public int ClassroomTaskProgressTotalCount => _activeClassroomTaskProgress?.Tasks.Length ?? 0;

public bool CanAddClassroomTask => HasActiveClassroomSession && !HasClassroomTaskProgressError &&
        ClassroomTaskProgressTotalCount < ClassroomTaskProgress.MaximumTasks &&
        !string.IsNullOrWhiteSpace(ClassroomTaskProgressDraft);

public string ClassroomTaskProgressStatus
    {
        get => _classroomTaskProgressStatus;
        private set
        {
            if (_classroomTaskProgressStatus == value) return;
            _classroomTaskProgressStatus = value;
            Changed();
            Changed(nameof(HasClassroomTaskProgressStatus));
            Changed(nameof(HasClassroomTaskProgressError));
            Changed(nameof(CanAddClassroomTask));
        }
    }

public bool HasClassroomRestores => _pendingClassroomRestores.Count > 0 ||
                                        !string.IsNullOrWhiteSpace(_classroomRestoreStatus);

public bool HasPendingClassroomRestoreItems => _pendingClassroomRestores.Count > 0;

public int PendingClassroomRestoreCount => _pendingClassroomRestores.Count;

public string ClassroomRestoreSummary
    {
        get
        {
            IReadOnlyDictionary<Guid, ClassroomSession> sessions;
            try
            {
                sessions = _classroomSessionStore.ReadRecent().ToDictionary(item => item.SessionId);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                sessions = new Dictionary<Guid, ClassroomSession>();
            }

            var lines = _pendingClassroomRestores.Take(24).Select(item =>
                $"{item.RoomName} · {(sessions.TryGetValue(item.SessionId, out var session)
                    ? GetStudentNameForSessionTarget(session, item.DeviceLabel)
                    : "未填写姓名")} · {(item.Kind == ClassroomPolicyKind.Website ? "学生代理" : "应用")}")
                .ToList();
            if (_pendingClassroomRestores.Count > lines.Count)
                lines.Add($"还有 {_pendingClassroomRestores.Count - lines.Count} 项");
            var summary = _pendingClassroomRestores.Count > 0
                ? $"{_pendingClassroomRestores.Count} 项限制尚未恢复：\n" +
                  string.Join(Environment.NewLine, lines)
                : "";
            return string.Join(Environment.NewLine,
                new[] { summary, _classroomRestoreStatus }.Where(item => !string.IsNullOrWhiteSpace(item)));
        }
    }

private string GetStudentNameForComputerLabel(string computerLabel)
    {
        if (string.IsNullOrWhiteSpace(computerLabel)) return "未填写姓名";
        var key = NormalizeTargetKey(computerLabel);
        var matches = new Dictionary<(Guid CampusId, Guid RoomId, Guid SeatId), string>();
        foreach (var campus in CampusProfiles)
        foreach (var room in campus.Rooms)
        {
            ClassroomSeatGridLayout layout;
            try { layout = _classroomSeatGridStore.GetLayout(campus.ProfileId, room.RoomId, room.StartNumber); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            { continue; }
            IReadOnlyList<string> computerNames;
            IReadOnlyList<int> computerNumbers;
            try
            {
                computerNames = room.GetComputerNames();
                computerNumbers = room.GetComputerNumbers();
            }
            catch (InvalidDataException) { computerNames = Array.Empty<string>(); computerNumbers = Array.Empty<int>(); }
            foreach (var seat in layout.Seats.Where(item => !string.IsNullOrWhiteSpace(item.StudentName)))
            {
                if (string.Equals(seat.StudentName.Trim(), computerLabel.Trim(), StringComparison.OrdinalIgnoreCase))
                    matches[(campus.ProfileId, room.RoomId, seat.SeatId)] = seat.StudentName.Trim();
                if ((!string.IsNullOrWhiteSpace(seat.VeyonComputerName) &&
                     NormalizeTargetKey(seat.VeyonComputerName) == key) ||
                    (!string.IsNullOrWhiteSpace(seat.VeyonHostAddress) &&
                     NormalizeTargetKey(seat.VeyonHostAddress) == key))
                    matches[(campus.ProfileId, room.RoomId, seat.SeatId)] = seat.StudentName.Trim();
                if (seat.ComputerNumber is not { } number) continue;
                var index = -1;
                for (var candidate = 0; candidate < computerNumbers.Count; candidate++)
                    if (computerNumbers[candidate] == number) { index = candidate; break; }
                if (index < 0) continue;
                if (index < computerNames.Count && NormalizeTargetKey(computerNames[index]) == key)
                    matches[(campus.ProfileId, room.RoomId, seat.SeatId)] = seat.StudentName.Trim();
                if (room.HostOverrides is { } hosts && index < hosts.Count &&
                    !string.IsNullOrWhiteSpace(hosts[index]) && NormalizeTargetKey(hosts[index]) == key)
                    matches[(campus.ProfileId, room.RoomId, seat.SeatId)] = seat.StudentName.Trim();
            }
        }
        return matches.Count == 1 ? matches.Values.Single() : "未填写姓名";
    }

public bool CanRetryClassroomRestores => PendingClassroomRestoreCount > 0 &&
        !_isRetryingClassroomRestores && !_isClassroomTransitioning && !IsExecuting;

public string ClassroomDeliveryStatus
    {
        get => _classroomDeliveryStatus;
        private set
        {
            if (_classroomDeliveryStatus == value) return;
            _classroomDeliveryStatus = value;
            Changed();
            Changed(nameof(HasClassroomDeliveryStatus));
        }
    }

public bool HasClassroomDeliveryStatus => !string.IsNullOrWhiteSpace(ClassroomDeliveryStatus);

public string ClassroomEventStatus
    {
        get => _classroomEventStatus;
        private set
        {
            if (_classroomEventStatus == value) return;
            _classroomEventStatus = value;
            Changed();
            Changed(nameof(HasActiveClassroomEventStatus));
        }
    }

public string ClassroomNoticeDraft
    {
        get => _classroomNoticeDraft;
        set
        {
            if (_classroomNoticeDraft == value) return;
            _classroomNoticeDraft = value ?? "";
            Changed();
            Changed(nameof(CanSendClassroomNotice));
            ClassroomNoticeStatus = "";
        }
    }

public bool CanSendClassroomNotice => HasActiveClassroomSession && !_isClassroomTransitioning &&
        IsClassroomDirectoryScopeVerified && !HasPendingClassroomScopeChange &&
        !string.IsNullOrWhiteSpace(_classroomNoticeDraft) && _classroomNoticeDraft.Trim().Length <=
        ClassroomEventCryptography.MaximumMessageCharacters &&
        !_classroomNoticeDraft.Any(character => char.IsControl(character) &&
            character is not '\r' and not '\n' and not '\t');

public string ClassroomNoticeStatus
    {
        get => _classroomNoticeStatus;
        private set
        {
            if (_classroomNoticeStatus == value) return;
            _classroomNoticeStatus = value;
            Changed();
            Changed(nameof(HasClassroomNoticeStatus));
        }
    }

public ObservableCollection<TeacherClassroomEventItem> ClassroomEventItems { get; } = [];

public bool HasClassroomEventItems => ClassroomEventItems.Count > 0;

public int PendingClassroomHelpCount => ClassroomEventItems.Count(item => item.IsHelpRequest && item.CanReply);

public ObservableCollection<TeacherClassroomChatThread> ClassroomChatThreads { get; } = [];

public bool HasClassroomChatThreads => ClassroomChatThreads.Count > 0;

public int UnreadClassroomChatCount => ClassroomChatThreads.Sum(item => item.UnreadCount);

public string ClassroomChatUnreadSummary => $"未读 {UnreadClassroomChatCount} 条";

public bool HasUnreadClassroomChatMessages => UnreadClassroomChatCount > 0;

public string UnreadClassroomChatBadgeText => UnreadClassroomChatCount > 99 ? "99+" :
        UnreadClassroomChatCount.ToString(CultureInfo.CurrentCulture);

public string ClassroomChatSearch
    {
        get => _classroomChatSearch;
        set
        {
            if (_classroomChatSearch == value) return;
            _classroomChatSearch = value ?? "";
            Changed();
            Changed(nameof(FilteredClassroomChatThreads));
            Changed(nameof(HasClassroomChatSearchResults));
            Changed(nameof(HasNoClassroomChatSearchResults));
        }
    }

public IReadOnlyList<TeacherClassroomChatThread> FilteredClassroomChatThreads =>
        ClassroomChatThreads.Where(thread => string.IsNullOrWhiteSpace(ClassroomChatSearch) ||
            thread.Title.Contains(ClassroomChatSearch.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
            thread.Detail.Contains(ClassroomChatSearch.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
            thread.Target.Contains(ClassroomChatSearch.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();

public bool HasClassroomChatSearchResults => FilteredClassroomChatThreads.Count > 0;

public bool HasNoClassroomChatSearchResults => HasClassroomChatThreads && !HasClassroomChatSearchResults;

public bool HasSelectedClassroomChatThread => SelectedClassroomChatThread is not null;

public bool HasSelectedClassroomChatMessages =>
        SelectedClassroomChatThread is { } thread && thread.Messages.Count > 0;

public bool HasEmptySelectedClassroomChatThread => HasSelectedClassroomChatThread &&
        !HasSelectedClassroomChatMessages;

public bool HasNoSelectedClassroomChatThread => !HasSelectedClassroomChatThread;

public bool IsClassroomChatDrawerOpen
    {
        get => _isClassroomChatDrawerOpen;
        set
        {
            if (_isClassroomChatDrawerOpen == value) return;
            _isClassroomChatDrawerOpen = value;
            if (value) SelectedClassroomChatThread?.MarkRead();
            Changed();
            Changed(nameof(UnreadClassroomChatCount));
            Changed(nameof(ClassroomChatUnreadSummary));
            Changed(nameof(HasUnreadClassroomChatMessages));
            Changed(nameof(UnreadClassroomChatBadgeText));
            Changed(nameof(ClassroomInteractionNavigationLabel));
        }
    }

public TeacherClassroomChatThread? SelectedClassroomChatThread
    {
        get => _selectedClassroomChatThread;
        set
        {
            if (ReferenceEquals(_selectedClassroomChatThread, value)) return;
            _selectedClassroomChatThread?.SetDraft(ClassroomChatDraft);
            _selectedClassroomChatThread?.SetSelected(false);
            _selectedClassroomChatThread = value;
            _selectedClassroomChatThread?.SetSelected(true);
            if (_isClassroomChatDrawerOpen) _selectedClassroomChatThread?.MarkRead();
            _classroomChatDraft = _selectedClassroomChatThread?.Draft ?? "";
            ClassroomChatStatus = "";
            Changed();
            Changed(nameof(ClassroomChatDraft));
            Changed(nameof(UnreadClassroomChatCount));
            Changed(nameof(ClassroomChatUnreadSummary));
            Changed(nameof(HasUnreadClassroomChatMessages));
            Changed(nameof(UnreadClassroomChatBadgeText));
            Changed(nameof(ClassroomInteractionNavigationLabel));
            Changed(nameof(SelectedClassroomChatMessages));
            Changed(nameof(HasSelectedClassroomChatThread));
            Changed(nameof(HasSelectedClassroomChatMessages));
            Changed(nameof(HasEmptySelectedClassroomChatThread));
            Changed(nameof(HasNoSelectedClassroomChatThread));
            Changed(nameof(CanSendClassroomChat));
        }
    }

public string ClassroomChatDraft
    {
        get => _classroomChatDraft;
        set
        {
            if (_classroomChatDraft == value) return;
            _classroomChatDraft = value ?? "";
            _selectedClassroomChatThread?.SetDraft(_classroomChatDraft);
            Changed();
            Changed(nameof(CanSendClassroomChat));
        }
    }

public string ClassroomChatStatus
    {
        get => _classroomChatStatus;
        private set
        {
            if (_classroomChatStatus == value) return;
            _classroomChatStatus = value;
            Changed();
            Changed(nameof(HasClassroomChatStatus));
        }
    }

public bool CanSendClassroomChat => HasActiveClassroomSession && !_isClassroomTransitioning &&
        IsClassroomDirectoryScopeVerified && !HasPendingClassroomScopeChange &&
        SelectedClassroomChatThread?.IsSupported == true && !string.IsNullOrWhiteSpace(ClassroomChatDraft) &&
        ClassroomEventCryptography.IsValidChatMessage(ClassroomChatDraft);

public string ClassroomInteractionNavigationLabel
    {
        get
        {
            var unread = UnreadClassroomChatCount;
            var pending = PendingClassroomHelpCount;
            if (unread == 0 && pending == 0) return "课堂控制";
            var details = new List<string>(2);
            if (unread > 0) details.Add($"{unread} 条新消息");
            if (pending > 0) details.Add($"{pending} 条待回复");
            return "课堂控制 · " + string.Join(" · ", details);
        }
    }

public string ClassroomNavigationLabel => "学生策略";

private IReadOnlyDictionary<string, string> ResolveClassroomStudentDisplayNames(ClassroomSession session,
        IReadOnlyList<string> targets)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var room = CampusProfiles.FirstOrDefault(item => item.ProfileId == session.Room.CampusProfileId)?
            .Rooms.FirstOrDefault(item => item.RoomId == session.Room.RoomId);
        IReadOnlyList<ClassroomSeatGridLocation> seats = Array.Empty<ClassroomSeatGridLocation>();
        if (room is not null)
        {
            try
            {
                var layout = ReadClassroomSeatGridForSession(session);
                seats = ClassroomSeatGridStore.GetLocations(layout);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _ = exception;
            }
        }

        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var sessionTarget = index < session.Targets.Length ? session.Targets[index] : null;
            var deviceLabel = sessionTarget?.DeviceLabel ?? "";
            var seat = FindUniqueClassroomSeat(seats, sessionTarget?.TargetId, target, deviceLabel);

            var studentName = seat?.StudentName?.Trim();
            if ((string.IsNullOrWhiteSpace(studentName) || studentName == "未填写姓名") &&
                SelectedCampusProfile?.ProfileId == session.Room.CampusProfileId)
                studentName = FindConfiguredStudentName(target, deviceLabel);
            names[target] = string.IsNullOrWhiteSpace(studentName) ? "未填写姓名" : studentName;
            if (!string.IsNullOrWhiteSpace(deviceLabel)) names[deviceLabel] = names[target];
            if (!string.IsNullOrWhiteSpace(seat?.VeyonComputerName))
                names[seat.VeyonComputerName] = names[target];
            if (!string.IsNullOrWhiteSpace(seat?.VeyonHostAddress))
                names[seat.VeyonHostAddress] = names[target];
        }
        return names;
    }

private static ClassroomSeatGridLocation? FindUniqueClassroomSeat(
        IReadOnlyList<ClassroomSeatGridLocation> seats, Guid? deviceId, string target, string deviceLabel)
    {
        if (deviceId is { } id)
        {
            var identityMatches = seats.Where(item => item.VeyonObjectId == id)
                .DistinctBy(item => item.SeatId).Take(2).ToArray();
            if (identityMatches.Length > 0) return identityMatches.Length == 1 ? identityMatches[0] : null;
        }

        var aliasMatches = seats.Where(item => !string.IsNullOrWhiteSpace(item.VeyonHostAddress) &&
                (string.Equals(NormalizeTargetKey(item.VeyonHostAddress), NormalizeTargetKey(target),
                     StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(NormalizeTargetKey(item.VeyonHostAddress), NormalizeTargetKey(deviceLabel),
                     StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(item => item.SeatId).Take(2).ToArray();
        if (aliasMatches.Length > 0) return aliasMatches.Length == 1 ? aliasMatches[0] : null;
        return null;
    }

internal void RefreshClassroomRestoreLedger(string? status = null)
    {
        try
        {
            _pendingClassroomRestores = ClassroomRestoreLedger.ListPending(_classroomModeStateStore.ReadAll(),
                _classroomSessionStore.ReadRecent());
            if (status is not null) _classroomRestoreStatus = status;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _classroomRestoreStatus = "无法读取本机待恢复记录；原记录已保留。";
        }
        Changed(nameof(HasClassroomRestores));
        Changed(nameof(HasPendingClassroomRestoreItems));
        Changed(nameof(PendingClassroomRestoreCount));
        Changed(nameof(ClassroomRestoreSummary));
        Changed(nameof(CanRetryClassroomRestores));
    }

public bool CanToggleClassroomSession => !IsExecuting &&
        !_isClassroomTransitioning && !_isRetryingClassroomRestores && (HasActiveClassroomSession ||
            !_classroomDirectorySelectionPending && !HasClassroomDirectoryError &&
            SelectedCampusProfile is not null &&
            (!OperatingSystem.IsWindows() || IsVeyonDeviceDirectoryCurrent) &&
            (OperatingSystem.IsWindows() ? CurrentClassroomDirectoryTargetCount is > 0 :
                SelectedRoomProfile is { ComputerCount: > 0 }) &&
            WebsiteSigningCampuses.Contains(CampusId, StringComparer.Ordinal));

private void SelectClassroomPolicy(int index)
    {
        _classroomPolicyIndex = index;
        if (!string.IsNullOrWhiteSpace(CampusId) && !string.IsNullOrWhiteSpace(WebsiteTargets))
            AreClassroomTargetsExpanded = false;
        if (index == 0) LoadWebsitePolicyLists(CampusId);
        Changed(nameof(IsWebsitePolicyPage)); Changed(nameof(IsApplicationPolicyPage)); Changed(nameof(IsSystemPolicyPage));
    }

public bool IsClassroomPage
    {
        get => _selectedPage == "classroom";
        set
        {
            if (!value) return;
            SelectPage("classroom");
        }
    }

public bool IsClassroomExperiencePage
    {
        get => _selectedPage == "lesson";
        set
        {
            if (!value) return;
            SelectPage("lesson");
            _ = ReadClassroomLocationsAsync();
        }
    }

public bool IsClassroomWorkspacePage => IsClassroomPage || IsClassroomExperiencePage;

private int _deviceRuntimeSummaryPollInFlight;

public bool IsCheckingTeacherSetupVeyon => _isCheckingTeacherSetupVeyon;

public string TeacherSetupVeyonStatus => _teacherSetupVeyonStatus;

public bool HasTeacherSetupVeyonStatus => TeacherSetupVeyonStatus.Length > 0;

public bool IsTeacherSetupVeyonReady => _teacherSetupVeyonReady;

public bool CanRefreshTeacherSetupVeyon => !_isCheckingTeacherSetupVeyon;

public bool IsWindowsTeacherHost => OperatingSystem.IsWindows();

public bool CanOpenTeacherSetupStep2 => IsTeacherSetupStep2 || IsTeacherSetupStep3 ||
        IsTeacherSetupVeyonReady || IsMacTeacherHost;

public bool CanOpenTeacherSetupStep3 => IsTeacherSetupStep3 || CanProceedTeacherSetupToPackage;

public string TeacherSetupStep1Label => IsMacTeacherHost ? "1 · Mac 教师端 ✓" :
        IsTeacherSetupVeyonReady ? "1 · 教师端 ✓" : "1 · 教师端 · 待完成";

public string TeacherSetupStep2Label => IsTeacherSetupRoomComplete
        ? "2 · 机房与座位 ✓"
        : IsTeacherSetupRoomSkippedForNow ? "2 · 机房与座位 · 稍后" : "2 · 机房与座位 · 待完成";

public string TeacherSetupStep3Label => HasPackageOutput
        ? "3 · 学生配置包 ✓" : "3 · 学生配置包 · 待完成";

public string TeacherInstallPlanText => "无需联网；已安装时不会重复安装。";

public string TeacherInstallSafetyText => "安装后可能需要重启。";

private async Task LoadClassroomSessionProgressAsync(Guid sessionId)
    {
        var loaded = await Task.Run(() =>
        {
            ClassroomCountdown? countdown = null;
            var countdownStatus = "";
            try { countdown = _classroomCountdownStore.Read(sessionId); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException)
            {
                countdownStatus = "无法读取本机倒计时；原文件已保留。";
            }

            ClassroomTaskProgress? taskProgress = null;
            var taskStatus = "";
            try { taskProgress = _classroomTaskProgressStore.Read(sessionId); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException)
            {
                taskStatus = "课堂任务进度无法读取；原文件已保留。";
            }
            return (Countdown: countdown, CountdownStatus: countdownStatus,
                TaskProgress: taskProgress, TaskStatus: taskStatus);
        }).ConfigureAwait(true);
        if (_activeClassroomSession?.SessionId != sessionId) return;
        _activeClassroomCountdown = loaded.Countdown;
        ClassroomCountdownStatus = loaded.CountdownStatus;
        RefreshClassroomCountdownDisplay();
        Changed(nameof(HasClassroomCountdown));
        Changed(nameof(IsClassroomCountdownRunning));
        Changed(nameof(CanStartClassroomCountdown));
        Changed(nameof(CanEndClassroomCountdown));
        _activeClassroomTaskProgress = loaded.TaskProgress;
        ClassroomTaskProgressStatus = loaded.TaskStatus;
        RefreshClassroomTaskProgressItems();
    }

private async Task PublishClassroomStatusAsync(ClassroomSession? activeSession, ClassroomSession targetSession,
        bool requireCurrentSession = false, IReadOnlyList<string>? explicitTargets = null)
    {
        await _classroomStatusPushGate.WaitAsync();
        try
        {
            if (requireCurrentSession && _activeClassroomSession?.SessionId != activeSession?.SessionId) return;
            var campusId = ResolveClassroomSigningCampus(targetSession);
            var targets = explicitTargets is null
                ? ResolveClassroomNetworkTargets(targetSession)
                : WebsitePolicyTransport.NormalizeTargets(explicitTargets);
            using var signingKey = WebsitePolicySigningKeyStore.Open(campusId);
            var command = ClassroomStatusCryptography.Create(campusId, activeSession, DateTimeOffset.UtcNow);
            var results = await ClassroomStatusTransport.SendAsync(targets, command, signingKey.PrivateKey);
            var confirmed = results.Count(result => result.Succeeded);
            var needsReview = results.Count(result => result.NeedsReview);
            var failed = results.Count - confirmed - needsReview;
            ClassroomDeliveryStatus = results.Count == 0
                ? "暂无学生电脑。"
                : confirmed == results.Count
                    ? $"课堂状态已确认 {confirmed}/{results.Count} 台。"
                    : $"课堂状态确认：{confirmed}/{results.Count} 台 · 待核对 {needsReview} · 未响应 {failed}";
        }
        finally { _classroomStatusPushGate.Release(); }
    }

private string ResolveClassroomSigningCampus(ClassroomSession session)
    {
        ReadWebsiteSigningCampuses();
        var pinnedCampus = _classroomSigningContextStore.Read(session.SessionId);
        if (pinnedCampus is not null)
        {
            if (WebsiteSigningCampuses.Contains(pinnedCampus, StringComparer.Ordinal)) return pinnedCampus;
            throw new InvalidDataException("当前互动使用的校区设置不可用；互动记录已保留，学生状态同步已停止。");
        }
        var savedCampus = CampusProfiles.FirstOrDefault(item => item.ProfileId == session.Room.CampusProfileId);
        var profileCampus = savedCampus?.DisplayName ?? session.Room.CampusName;
        var inferredCampus = profileCampus is not null && WebsiteSigningCampuses.Contains(profileCampus, StringComparer.Ordinal)
            ? profileCampus
            : WebsiteSigningCampuses.Contains(CampusId, StringComparer.Ordinal)
                ? CampusId
                : WebsiteSigningCampuses.Count == 1 ? WebsiteSigningCampuses[0] : null;
        if (inferredCampus is not null)
        {
            SaveClassroomSigningContext(session.SessionId, inferredCampus);
            return inferredCampus;
        }
        throw new InvalidDataException(WebsiteSigningCampuses.Count == 0
            ? "请先生成学生校区配置包。"
            : "请选择与学生配置包相同的校区。");
    }

private void RestoreActiveClassroomSigningContext()
    {
        if (_activeClassroomSession is not { } active) return;
        try
        {
            _ = ResolveClassroomSigningCampus(active);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or CryptographicException)
        {
            ClassroomDeliveryStatus = "无法确认当前互动校区；学生状态同步已暂停。";
        }
    }

private IReadOnlyList<string> ResolveClassroomNetworkTargets(ClassroomSession session)
    {
        var targets = session.Targets.Select((target, index) =>
        {
            var host = session.Room.HostOverrides is { } overrides && index < overrides.Length
                ? overrides[index]
                : null;
            return string.IsNullOrWhiteSpace(host) ? target.DeviceLabel : host.Trim();
        });
        return WebsitePolicyTransport.NormalizeTargets(targets);
    }

private void LoadActiveClassroomSession()
    {
        try
        {
            _activeClassroomSession = _classroomSessionStore.ReadActive();
            if (_activeClassroomSession is { } active)
            {
                var campus = CampusProfiles.FirstOrDefault(item => item.ProfileId == active.Room.CampusProfileId);
                if (campus is not null)
                {
                    SelectedCampusProfile = campus;
                    SelectedRoomProfile = RoomProfiles.FirstOrDefault(item => item.RoomId == active.Room.RoomId) ??
                                          RoomProfiles.FirstOrDefault(item => item.ComputerCount > 0);
                }
            }
            RefreshClassroomSessionProperties();
            if (_activeClassroomSession is not null)
            {
                ClassroomDeliveryStatus = "已恢复学生互动；正在等待电脑状态确认。";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _activeClassroomSession = null;
            ClassroomDeliveryStatus = "本机学生互动记录无法读取；原文件已保留。";
        }
        LoadActiveClassroomCountdown();
        LoadActiveClassroomTaskProgress();
    }

private void RefreshClassroomTaskProgressItems()
    {
        ClassroomTaskProgressItems.Clear();
        if (_activeClassroomTaskProgress is { } progress)
            foreach (var task in progress.Tasks)
                ClassroomTaskProgressItems.Add(new TeacherClassroomTaskItem(task.TaskId, task.Title,
                    task.IsCompleted));
        Changed(nameof(ClassroomTaskProgressCompletedCount));
        Changed(nameof(ClassroomTaskProgressTotalCount));
        Changed(nameof(HasClassroomTaskProgress));
        Changed(nameof(ClassroomTaskProgressSummary));
        Changed(nameof(CanAddClassroomTask));
    }

private void RefreshClassroomSessionProperties()
    {
        if (!HasActiveClassroomSession)
        {
            if (_classroomNoticeDraft.Length > 0) ClassroomNoticeDraft = "";
            if (_classroomTaskProgressDraft.Length > 0) ClassroomTaskProgressDraft = "";
        }
        Changed(nameof(HasActiveClassroomSession));
        Changed(nameof(HasNoActiveClassroomSession));
        Changed(nameof(HasPendingClassroomScopeChange));
        Changed(nameof(CanSwitchClassroomScope));
        Changed(nameof(ClassroomNoticeTargetSummary));
        Changed(nameof(ClassroomOverviewDeviceCount));
        Changed(nameof(ClassroomRoomContextSummary));
        Changed(nameof(CanStartClassroomQuickControl));
        Changed(nameof(IsClassroomQuickControlPilotDisabled));
        Changed(nameof(CanReleaseClassroomQuickControl));
        Changed(nameof(HasActiveClassroomEventStatus));
        Changed(nameof(ClassroomSessionActionText));
        Changed(nameof(ClassroomSessionToggleText));
        Changed(nameof(ClassroomSessionSummary));
        Changed(nameof(CanToggleClassroomSession));
        Changed(nameof(CanSwitchClassroomScope));
        Changed(nameof(CanDeleteRoomProfile));
        Changed(nameof(CanCheckRoomConflicts));
        Changed(nameof(CanAddRoomToVeyon));
        Changed(nameof(CanSendClassroomNotice));
        Changed(nameof(CanSendClassroomChat));
        Changed(nameof(CanRetryClassroomRestores));
        Changed(nameof(HasClassroomCountdown));
        Changed(nameof(ClassroomCountdownText));
        Changed(nameof(IsClassroomCountdownRunning));
        Changed(nameof(CanStartClassroomCountdown));
        Changed(nameof(CanEndClassroomCountdown));
        Changed(nameof(ClassroomTaskProgressCompletedCount));
        Changed(nameof(ClassroomTaskProgressTotalCount));
        Changed(nameof(HasClassroomTaskProgress));
        Changed(nameof(ClassroomTaskProgressSummary));
        Changed(nameof(CanAddClassroomTask));
    }

public string TeacherUpdateVersionSummary => $"当前版本：{AppVersion} · 最新版本：{_teacherUpdateLatestVersion}";

private static bool ContainsHealthSearch(string value, string search) =>
        value.Contains(search, StringComparison.CurrentCultureIgnoreCase);

public bool HasUpdateDiagnosticsStatus => UpdateDiagnosticsStatus.Length > 0;

public string OperationsTelemetryStatus
    {
        get => _operationsTelemetryStatus;
        private set { if (_operationsTelemetryStatus == value) return; _operationsTelemetryStatus = value; Changed(); }
    }

private bool IsCurrentClassroomLocation(VeyonNetworkLocation location) => OperatingSystem.IsMacOS()
        ? SelectedCampusProfile is { } campus && SelectedRoomProfile is { } room &&
          MacTeacherNetworkDirectory.MatchesCurrentRoom(location, campus, room)
        : string.Equals(location.Name, SelectedRoomProfile?.DisplayName, StringComparison.OrdinalIgnoreCase);

public bool HasCampusProfiles => CampusProfiles.Count > 0;
public bool HasMultipleCampusProfiles => CampusProfiles.Count > 1;

private bool _seatMapProfileSelectionRequired;

private static int ParseComputerNumber(string prefix, string computerName)
    {
        if (!computerName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(computerName.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture,
                out var number) || number is < 1 or > MachineNaming.MaximumNumber ||
            !string.Equals(MachineNaming.CreateName(prefix, number.ToString(CultureInfo.InvariantCulture)),
                computerName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"电脑名称“{computerName}”无法解析为有效编号。");
        return number;
    }



public bool HasClassroomCountdownStatus => !string.IsNullOrWhiteSpace(ClassroomCountdownStatus);

public bool HasClassroomCountdownError => ClassroomCountdownStatus.StartsWith("无法", StringComparison.Ordinal) ||
                                              ClassroomCountdownStatus.StartsWith("课堂倒计时无法", StringComparison.Ordinal);

public bool HasClassroomCountdownInfo => HasClassroomCountdownStatus && !HasClassroomCountdownError;

public bool HasClassroomTaskProgress => ClassroomTaskProgressTotalCount > 0;

public string ClassroomTaskProgressSummary => HasClassroomTaskProgress
        ? $"{ClassroomTaskProgressCompletedCount}/{ClassroomTaskProgressTotalCount} 项已完成"
        : "";

public bool HasClassroomTaskProgressStatus => !string.IsNullOrWhiteSpace(ClassroomTaskProgressStatus);

public bool HasClassroomTaskProgressError => ClassroomTaskProgressStatus.StartsWith("无法", StringComparison.Ordinal) ||
        ClassroomTaskProgressStatus.StartsWith("课堂任务进度无法", StringComparison.Ordinal);

private string GetStudentNameForSessionTarget(ClassroomSession session, string deviceLabel)
    {
        IReadOnlyList<ClassroomSeatGridLocation> seats;
        try
        {
            var layout = ReadClassroomSeatGridForSession(session);
            seats = ClassroomSeatGridStore.GetLocations(layout);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return "未填写姓名";
        }

        var sessionTarget = session.Targets.SingleOrDefault(item =>
            string.Equals(item.DeviceLabel, deviceLabel, StringComparison.OrdinalIgnoreCase));
        if (sessionTarget is null) return "未填写姓名";
        var seat = FindUniqueClassroomSeat(seats, sessionTarget.TargetId, deviceLabel, deviceLabel);
        return string.IsNullOrWhiteSpace(seat?.StudentName) ? "未填写姓名" : seat.StudentName.Trim();
    }

public bool HasActiveClassroomEventStatus => HasActiveClassroomSession &&
        !string.IsNullOrWhiteSpace(ClassroomEventStatus);

public string ClassroomNoticeTargetSummary =>
        $"将发送到本堂课的 {_activeClassroomSession?.Targets.Length ?? 0} 台学生电脑";

public bool HasClassroomNoticeStatus => !string.IsNullOrWhiteSpace(_classroomNoticeStatus);

public ObservableCollection<TeacherClassroomTaskItem> ClassroomTaskProgressItems { get; } = [];

public ObservableCollection<TeacherClassroomChatMessageItem> SelectedClassroomChatMessages =>
        SelectedClassroomChatThread?.Messages ?? EmptyClassroomChatMessages;

public bool HasClassroomChatStatus => !string.IsNullOrWhiteSpace(ClassroomChatStatus);

private ClassroomSeatGridLayout ReadClassroomSeatGridForSession(ClassroomSession session)
    {
        var layout = _classroomSeatGridStore.GetLayout(session.Room.CampusProfileId,
            session.Room.RoomId, session.Room.StartNumber);
        if (layout.Seats.Length > 0) return layout;

        var targets = session.Room.HostOverrides is { } hosts && hosts.Length == session.Targets.Length
            ? hosts
            : session.Targets.Select(item => item.DeviceLabel).ToArray();
        var recoveredCampus = FindSavedSeatMapCampus(new VeyonNetworkLocation(
            session.Room.RoomName, Array.AsReadOnly(targets)), SelectedCampusProfile);
        if (recoveredCampus is null || recoveredCampus.ProfileId == session.Room.CampusProfileId) return layout;

        var targetSet = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchingRoom = recoveredCampus.Rooms.SingleOrDefault(room =>
            string.Equals(room.DisplayName, session.Room.RoomName, StringComparison.OrdinalIgnoreCase) &&
            targetSet.SetEquals(MacTeacherNetworkDirectory.CreateLocation(recoveredCampus, room).Targets));
        return matchingRoom is null ? layout : _classroomSeatGridStore.GetLayout(
            recoveredCampus.ProfileId, matchingRoom.RoomId, matchingRoom.StartNumber);
    }

private string FindConfiguredStudentName(params string[] targetAliases)
    {
        var aliases = targetAliases.Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(NormalizeTargetKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = WebsitePolicyTargetChoices.Where(item => aliases.Contains(NormalizeTargetKey(item.Target)))
            .Select(item => item.StudentName.Trim())
            .Where(item => item.Length > 0 && !string.Equals(item, "未填写姓名", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return names.Length == 1 ? names[0] : "未填写姓名";
    }

private void SaveClassroomSigningContext(Guid sessionId, string campusId) =>
        _classroomSigningContextStore.Save(sessionId, campusId);

private void LoadActiveClassroomCountdown()
    {
        _activeClassroomCountdown = null;
        if (_activeClassroomSession is not { } active)
        {
            ClassroomCountdownStatus = "";
        }
        else
        {
            try
            {
                _activeClassroomCountdown = _classroomCountdownStore.Read(active.SessionId);
                ClassroomCountdownStatus = "";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                ClassroomCountdownStatus = "无法读取本机倒计时；原文件已保留。";
            }
        }
        RefreshClassroomCountdownDisplay();
        Changed(nameof(HasClassroomCountdown));
        Changed(nameof(IsClassroomCountdownRunning));
        Changed(nameof(CanStartClassroomCountdown));
        Changed(nameof(CanEndClassroomCountdown));
    }

private void LoadActiveClassroomTaskProgress()
    {
        _activeClassroomTaskProgress = null;
        if (_activeClassroomSession is not { } active)
        {
            ClassroomTaskProgressStatus = "";
        }
        else
        {
            try
            {
                _activeClassroomTaskProgress = _classroomTaskProgressStore.Read(active.SessionId);
                ClassroomTaskProgressStatus = "";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                ClassroomTaskProgressStatus = "课堂任务进度无法读取；原文件已保留。";
            }
        }
        RefreshClassroomTaskProgressItems();
    }
}
