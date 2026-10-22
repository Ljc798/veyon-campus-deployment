using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VeyonCampus.App;
using VeyonCampus.Core;

namespace VeyonCampus.Checks;

internal static class StudentDeviceHealthChecks
{
    private const string CampusId = "campus-demo";
    private const string Target = "192.0.2.44";

    public static void Run()
    {
        CheckOldStatusCompatibilityAndPrivacy();
        CheckRepairCommandAndLedgerPolicies();
        CheckHealthClassificationAndRepairEligibility();
        CheckBatchRowsAndAlerts();
        CheckCachedHealthAlerts();
        CheckProgressDescriptions();
        CheckSignedProgressStreamAsync().GetAwaiter().GetResult();
    }

    private static void CheckOldStatusCompatibilityAndPrivacy()
    {
        var now = DateTimeOffset.UtcNow;
        var request = new WebsitePolicyStatusRequest(2, WebsitePolicyStatusCryptography.RequestPurposeV2,
            CampusId, Guid.NewGuid(), now);
        var response = new StudentAgentStatusResponse(2, WebsitePolicyStatusCryptography.ResponsePurposeV2,
            CampusId, request.Nonce, now, "1.0.0", new string('A', 64),
            new WebsitePolicyReportedState(3, WebsitePolicyMode.Blocklist, null, null), null);
        var serialized = JsonSerializer.Serialize(response, ApplicationPolicyCompiler.JsonOptions);
        var restored = JsonSerializer.Deserialize<StudentAgentStatusResponse>(serialized,
            ApplicationPolicyCompiler.JsonOptions)!;
        WebsitePolicyStatusCryptography.ValidateResponse(restored, request, now);
        Expect(restored.Health is null && !serialized.Contains("\"health\"", StringComparison.OrdinalIgnoreCase));

        var snapshot = JsonSerializer.Serialize(CreateHealthSnapshot(), ApplicationPolicyCompiler.JsonOptions);
        foreach (var privateField in new[] { "target", "ip", "student", "phone", "username", "macAddress" })
            Expect(!snapshot.Contains(privateField, StringComparison.OrdinalIgnoreCase));
    }

    private static void CheckRepairCommandAndLedgerPolicies()
    {
        var now = DateTimeOffset.UtcNow;
        using var campusKey = RSA.Create(2048);
        var campusPublicKey = campusKey.ExportSubjectPublicKeyInfoPem();
        var command = CreateCommand(StudentAgentHealthRepairAction.EnsureVeyonServiceRunning, now);
        var signed = StudentAgentHealthRepairCommandCryptography.Sign(command, campusKey);
        var verified = StudentAgentHealthRepairCommandCryptography.Verify(signed, campusPublicKey, CampusId, now);
        Expect(verified.CommandId == command.CommandId && verified.Nonce == command.Nonce);
        Reject(() => StudentAgentHealthRepairCommandCryptography.Verify(signed, campusPublicKey, "another-campus", now));
        var parsed = JsonSerializer.Deserialize<SignedStudentAgentHealthRepairCommand>(signed,
            ApplicationPolicyCompiler.JsonOptions)!;
        Reject(() => StudentAgentHealthRepairCommandCryptography.Verify(
            JsonSerializer.Serialize(parsed with { Signature = Convert.ToBase64String(new byte[256]) },
                ApplicationPolicyCompiler.JsonOptions), campusPublicKey, CampusId, now));
        var expired = command with { ExpiresUtc = now.AddMilliseconds(-1) };
        var expiredSigned = StudentAgentHealthRepairCommandCryptography.Sign(expired, campusKey);
        Reject(() => StudentAgentHealthRepairCommandCryptography.Verify(expiredSigned, campusPublicKey, CampusId, now));

        var completed = now.AddSeconds(-30);
        var entries = new[]
        {
            new StudentAgentHealthRepairLedgerEntry(Guid.NewGuid(),
                StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy,
                StudentAgentHealthRepairOutcome.Queued, 11, completed)
        };
        Expect(StudentAgentHealthRepairLedgerPolicy.IsCoolingDown(entries,
            StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy, now));
        Expect(!StudentAgentHealthRepairLedgerPolicy.IsCoolingDown(entries,
            StudentAgentHealthRepairAction.EnsureVeyonServiceRunning, now));
        Expect(!StudentAgentHealthRepairLedgerPolicy.IsCoolingDown(entries,
            StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy, now.AddMinutes(3)));
        foreach (var outcome in new[]
                 {
                     StudentAgentHealthRepairOutcome.BusyUpdating,
                     StudentAgentHealthRepairOutcome.CoolingDown,
                     StudentAgentHealthRepairOutcome.ReplayRejected
                 })
            Expect(!StudentAgentHealthRepairLedgerPolicy.ShouldPersistOutcome(outcome));
        foreach (var outcome in new[]
                 {
                     StudentAgentHealthRepairOutcome.Succeeded,
                     StudentAgentHealthRepairOutcome.Queued,
                     StudentAgentHealthRepairOutcome.Failed,
                     StudentAgentHealthRepairOutcome.NeedsManualReview
                 })
            Expect(StudentAgentHealthRepairLedgerPolicy.ShouldPersistOutcome(outcome));
    }

    private static void CheckHealthClassificationAndRepairEligibility()
    {
        var noResponse = CreateRow();
        noResponse.Apply(new StudentAgentStatusDeliveryResult(Target, false, "连接超时"));
        Expect(noResponse.Category == TeacherDeviceHealthCategory.NoResponse &&
               noResponse.Detail.Contains("不能说明电脑已关机", StringComparison.Ordinal));

        var oldAgent = CreateRow();
        oldAgent.Apply(new StudentAgentStatusDeliveryResult(Target, true, "ok", CreateStatus(includeHealth: false)));
        Expect(oldAgent.Category == TeacherDeviceHealthCategory.Attention &&
               !oldAgent.CanEnsureVeyonServiceRunning && !oldAgent.CanRefreshLegacyEdgePolicy);

        var healthy = CreateRow();
        healthy.Apply(new StudentAgentStatusDeliveryResult(Target, true, "ok", CreateStatus()));
        Expect(healthy.Category == TeacherDeviceHealthCategory.Healthy);

        var stoppedService = CreateRow();
        stoppedService.Apply(new StudentAgentStatusDeliveryResult(Target, true, "ok",
            CreateStatus(service: StudentVeyonServiceState.Stopped)));
        Expect(stoppedService.Category == TeacherDeviceHealthCategory.Attention &&
               stoppedService.CanEnsureVeyonServiceRunning);

        var transactionInProgress = CreateRow();
        transactionInProgress.Apply(new StudentAgentStatusDeliveryResult(Target, true, "ok",
            CreateStatus(updateState: "inProgress", service: StudentVeyonServiceState.Stopped)));
        Expect(transactionInProgress.Category == TeacherDeviceHealthCategory.Attention &&
               !transactionInProgress.CanEnsureVeyonServiceRunning);

        var transactionNeedsReview = CreateRow();
        transactionNeedsReview.Apply(new StudentAgentStatusDeliveryResult(Target, true, "ok",
            CreateStatus(updateState: "needsReview", service: StudentVeyonServiceState.Stopped)));
        Expect(transactionNeedsReview.Category == TeacherDeviceHealthCategory.NeedsManualReview &&
               !transactionNeedsReview.CanEnsureVeyonServiceRunning);

        var invalidAgentIdentity = CreateRow();
        invalidAgentIdentity.Apply(new StudentAgentStatusDeliveryResult(Target, true, "ok",
            CreateStatus(health: CreateHealthSnapshot(runningAsSystem: false))));
        Expect(invalidAgentIdentity.Category == TeacherDeviceHealthCategory.NeedsManualReview &&
               !invalidAgentIdentity.CanEnsureVeyonServiceRunning);

        var degradedLegacyEdge = CreateRow();
        degradedLegacyEdge.Apply(new StudentAgentStatusDeliveryResult(Target, true, "ok",
            CreateStatus(edgeDegraded: true)));
        Expect(degradedLegacyEdge.Category == TeacherDeviceHealthCategory.Attention &&
               degradedLegacyEdge.CanRefreshLegacyEdgePolicy);

        var mismatchedEdgeRevision = CreateRow();
        mismatchedEdgeRevision.Apply(new StudentAgentStatusDeliveryResult(Target, true, "ok",
            CreateStatus(edgeDegraded: true, edgeRevision: 9)));
        Expect(mismatchedEdgeRevision.Category == TeacherDeviceHealthCategory.Attention &&
               !mismatchedEdgeRevision.CanRefreshLegacyEdgePolicy);

        var realtimeEdge = CreateRow();
        realtimeEdge.Apply(new StudentAgentStatusDeliveryResult(Target, true, "ok",
            CreateStatus(edgeDegraded: true, executionMode: EdgeWebsitePolicyExecutionMode.ExtensionRealtime)));
        Expect(!realtimeEdge.CanRefreshLegacyEdgePolicy);
    }

    private static void CheckBatchRowsAndAlerts()
    {
        var row = CreateRow();
        var batchId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var action = StudentAgentHealthRepairAction.EnsureVeyonServiceRunning;
        row.BeginBatchOperation(batchId, action, TeacherDeviceHealthBatchState.Queued, "等待发送");
        Expect(row.IsRepairing && row.BatchOperationState == TeacherDeviceHealthBatchState.Queued);
        row.SetBatchOperation(batchId, commandId, action, TeacherDeviceHealthBatchState.Delivered, "已签收");
        Expect(row.BatchOperationCommandId == commandId && row.BatchOperationState == TeacherDeviceHealthBatchState.Delivered);
        row.SetBatchOperation(batchId, commandId, action, TeacherDeviceHealthBatchState.Executing, "执行中");
        Expect(row.IsRepairing && row.BatchOperationState == TeacherDeviceHealthBatchState.Executing);
        row.SetBatchOperation(batchId, commandId, action, TeacherDeviceHealthBatchState.Succeeded, "完成");
        Expect(!row.IsRepairing && row.BatchOperationState == TeacherDeviceHealthBatchState.Succeeded &&
               row.HasBatchOperationStatus);
        row.SetBatchOperation(batchId, commandId, action, TeacherDeviceHealthBatchState.Failed, "迟到状态");
        Expect(row.BatchOperationState == TeacherDeviceHealthBatchState.Succeeded &&
               !row.BatchOperationStatus.Contains("迟到状态", StringComparison.Ordinal));
        row.BeginBatchOperation(Guid.NewGuid(), action, TeacherDeviceHealthBatchState.Skipped, "新批次跳过");
        Expect(row.BatchOperationState == TeacherDeviceHealthBatchState.Skipped && !row.IsRepairing);

        var first = CreateRow("192.0.2.44", "学生甲", "PC-01");
        first.Apply(new StudentAgentStatusDeliveryResult(first.Target, true, "ok",
            CreateStatus(service: StudentVeyonServiceState.Stopped)));
        var alert = new TeacherDeviceHealthAlert(first.IssueCode, first.IssueTitle,
            TeacherDeviceHealthAlertSeverity.Warning);
        alert.Update([first]);
        Expect(alert.RecommendedAction == StudentAgentHealthRepairAction.EnsureVeyonServiceRunning &&
               alert.CanAttemptRepair && alert.CanRecheck && alert.AffectedCount == 1);
        alert.Acknowledge();
        alert.Update([first]);
        Expect(alert.IsAcknowledged && alert.OccurrenceCount == 2);
        var second = CreateRow("192.0.2.45", "学生乙", "PC-02");
        second.Apply(new StudentAgentStatusDeliveryResult(second.Target, true, "ok",
            CreateStatus(service: StudentVeyonServiceState.Stopped)));
        alert.Update([first, second]);
        Expect(!alert.IsAcknowledged && alert.AffectedCount == 2 && alert.Diagnostics.Contains("学生乙"));
        alert.SetBusy(true);
        Expect(!alert.CanRecheck && !alert.CanAttemptRepair);
        alert.SetBusy(false);

        var edgeRow = CreateRow("192.0.2.46", "学生丙", "PC-03");
        edgeRow.Apply(new StudentAgentStatusDeliveryResult(edgeRow.Target, true, "ok",
            CreateStatus(edgeDegraded: true)));
        var edgeAlert = new TeacherDeviceHealthAlert(edgeRow.IssueCode, edgeRow.IssueTitle,
            TeacherDeviceHealthAlertSeverity.Warning);
        edgeAlert.Update([edgeRow]);
        Expect(edgeAlert.RecommendedAction == StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy);
    }

    private static void CheckCachedHealthAlerts()
    {
        var now = DateTimeOffset.UtcNow;
        var issue = new TeacherDeviceHealthCachedIssue("veyon-service", "Veyon 服务异常",
            TeacherDeviceHealthCacheIssueSeverity.Warning, "上次回执显示 Veyon 服务未运行。");
        var entry = new TeacherDeviceHealthCacheEntry(CampusId, Target, "学生甲", "需关注",
            "Agent 1.0.0", "VeyonService 已停止", "网站 r8", "当前没有未结更新事务",
            now.AddMinutes(-1), now, [issue]);
        var row = CreateRow();
        row.ApplyLocalCache(entry);
        var alert = new TeacherDeviceHealthAlert("cached:" + issue.Code, issue.Title,
            TeacherDeviceHealthAlertSeverity.Informational);
        alert.UpdateFromCache([row], issue, entry.SavedUtc);
        Expect(row.IsLocalCache && row.CachedIssueSummaries.Single().Code == issue.Code &&
               alert.IsCached && alert.SeverityLabel == "缓存待确认" && alert.CanRecheck &&
               !alert.CanAttemptRepair && !alert.CanAcknowledge &&
               alert.Diagnostics.Contains("上次回执显示 Veyon 服务未运行", StringComparison.Ordinal));

        row.Apply(new StudentAgentStatusDeliveryResult(Target, true, "ok", CreateStatus()));
        Expect(!row.IsLocalCache && row.CachedIssueSummaries is [{ IsResolved: true }] && row.Issues.Count == 0);

        var guardIssue = new TeacherDeviceHealthCachedIssue("input-guard-unknown", "键鼠状态未知",
            TeacherDeviceHealthCacheIssueSeverity.Informational, "上次 Agent 未读回键鼠状态。");
        var guardEntry = entry with
        {
            Target = "192.0.2.45",
            StudentName = "学生乙",
            PreviousIssues = [guardIssue]
        };
        var guardRow = CreateRow(guardEntry.Target, guardEntry.StudentName);
        guardRow.ApplyLocalCache(guardEntry);
        guardRow.Apply(new StudentAgentStatusDeliveryResult(guardRow.Target, true, "ok", CreateStatus()));
        Expect(!guardRow.IsLocalCache &&
               guardRow.CachedIssueSummaries.Single().Code == guardIssue.Code);

        var firstRuntimeRead = CreateRuntimeSummary(DateTimeOffset.UtcNow, null);
        guardRow.ApplyRuntimeSummary(new StudentAgentRuntimeSummaryDeliveryResult(guardRow.Target, true,
            "ok", firstRuntimeRead), 0);
        Expect(guardRow.CachedIssueSummaries.Single().Code == guardIssue.Code);
        var firstUnlockedReadUtc = DateTimeOffset.UtcNow;
        var unlockedGuard = CreateInputGuardSnapshot(StudentInputGuardPhase.Unlocked, firstUnlockedReadUtc);
        guardRow.ApplyRuntimeSummary(new StudentAgentRuntimeSummaryDeliveryResult(guardRow.Target, true,
            "ok", CreateRuntimeSummary(firstUnlockedReadUtc, unlockedGuard)), 0);
        Expect(guardRow.CachedIssueSummaries.Single().Code == guardIssue.Code);
        var secondUnlockedReadUtc = firstUnlockedReadUtc.AddMilliseconds(10);
        var unlockedGuardAgain = CreateInputGuardSnapshot(StudentInputGuardPhase.Unlocked, secondUnlockedReadUtc);
        guardRow.ApplyRuntimeSummary(new StudentAgentRuntimeSummaryDeliveryResult(guardRow.Target, true,
            "ok", CreateRuntimeSummary(secondUnlockedReadUtc, unlockedGuardAgain)), 0);
        Expect(guardRow.CachedIssueSummaries is [{ IsResolved: true, ResolvedUtc: not null }]);

        var liveGuardRow = CreateRow("192.0.2.47", "学生丙");
        var unknownGuardTime = DateTimeOffset.UtcNow;
        liveGuardRow.ApplyRuntimeSummary(new StudentAgentRuntimeSummaryDeliveryResult(liveGuardRow.Target,
            true, "ok", CreateRuntimeSummary(unknownGuardTime,
                CreateInputGuardSnapshot(StudentInputGuardPhase.Unknown, unknownGuardTime))), 0);
        Expect(liveGuardRow.Issues.Any(item => item.Code == "input-guard-unknown") &&
               liveGuardRow.CachedIssueSummaries is [{ IsResolved: false }]);
        var recoveredOnceUtc = unknownGuardTime.AddSeconds(5);
        liveGuardRow.ApplyRuntimeSummary(new StudentAgentRuntimeSummaryDeliveryResult(liveGuardRow.Target,
            true, "ok", CreateRuntimeSummary(recoveredOnceUtc,
                CreateInputGuardSnapshot(StudentInputGuardPhase.Unlocked, recoveredOnceUtc))), 0);
        Expect(liveGuardRow.CachedIssueSummaries is [{ IsResolved: false }]);
        var recoveredTwiceUtc = unknownGuardTime.AddSeconds(10);
        liveGuardRow.ApplyRuntimeSummary(new StudentAgentRuntimeSummaryDeliveryResult(liveGuardRow.Target,
            true, "ok", CreateRuntimeSummary(recoveredTwiceUtc,
                CreateInputGuardSnapshot(StudentInputGuardPhase.Unlocked, recoveredTwiceUtc))), 0);
        Expect(liveGuardRow.Issues.Count == 0 &&
               liveGuardRow.CachedIssueSummaries is [{ IsResolved: true, ResolvedUtc: not null }]);
        var resolvedIssue = liveGuardRow.CachedIssueSummaries.Single();
        var resolvedAlert = new TeacherDeviceHealthAlert("cached:" + resolvedIssue.Code + ":resolved",
            resolvedIssue.Title, TeacherDeviceHealthAlertSeverity.Informational);
        resolvedAlert.UpdateFromCache([liveGuardRow], resolvedIssue, recoveredTwiceUtc);
        Expect(resolvedAlert.IsCached && resolvedAlert.IsResolved &&
               resolvedAlert.SeverityLabel == "已恢复 · 本地记录" && resolvedAlert.AffectedCountSummary == "1 台已恢复");

        var directory = Path.Combine(Path.GetTempPath(), "veyon-health-cache-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var issueCachePath = Path.Combine(directory, "device-health-cache-v2.json");
            var issueStore = new TeacherDeviceHealthCacheStore(issueCachePath);
            issueStore.Save([entry]);
            var restoredIssueEntry = issueStore.Load().Single();
            Expect(restoredIssueEntry.PreviousIssues is { Count: 1 } restoredIssues &&
                   restoredIssues[0] == issue);
            var restoredIssueRow = CreateRow();
            restoredIssueRow.ApplyLocalCache(restoredIssueEntry);
            var restoredIssueAlert = new TeacherDeviceHealthAlert("cached:" + issue.Code, issue.Title,
                TeacherDeviceHealthAlertSeverity.Informational);
            restoredIssueAlert.UpdateFromCache([restoredIssueRow], issue, restoredIssueEntry.SavedUtc);
            Expect(restoredIssueRow.IsLocalCache && restoredIssueAlert.IsCached &&
                   restoredIssueAlert.Diagnostics.Contains(issue.Detail, StringComparison.Ordinal));

            var resolvedIssueEntry = liveGuardRow.CreateLocalCacheEntry(CampusId, recoveredTwiceUtc);
            Expect(resolvedIssueEntry?.PreviousIssues is [{ IsResolved: true, ResolvedUtc: not null }]);
            issueStore.Save([resolvedIssueEntry!]);
            var restoredResolvedIssue = issueStore.Load().Single().PreviousIssues!.Single();
            Expect(restoredResolvedIssue.IsResolved && restoredResolvedIssue.ResolvedUtc == resolvedIssue.ResolvedUtc);

            var cachePath = Path.Combine(directory, "device-health-cache.json");
            var legacyEntry = new
            {
                campusId = CampusId,
                target = Target,
                studentName = "学生甲",
                previousStatus = "需关注",
                agentSummary = "Agent 1.0.0",
                serviceSummary = "VeyonService 已停止",
                policySummary = "网站 r8",
                updateSummary = "当前没有未结更新事务",
                lastCollectedUtc = now.AddMinutes(-1),
                savedUtc = now
            };
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            File.WriteAllText(cachePath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entries = new[] { legacyEntry }
            }, jsonOptions));

            var store = new TeacherDeviceHealthCacheStore(cachePath);
            var legacy = store.Load().Single();
            Expect(legacy.PreviousIssues is null);
            store.Save([legacy]);
            using var document = JsonDocument.Parse(File.ReadAllText(cachePath));
            Expect(document.RootElement.GetProperty("schemaVersion").GetInt32() ==
                   TeacherDeviceHealthCacheStore.CurrentSchemaVersion &&
                   store.Load().Single().PreviousIssues is null);

            var versionTwoPath = Path.Combine(directory, "device-health-cache-v2-schema.json");
            var versionTwoStore = new TeacherDeviceHealthCacheStore(versionTwoPath);
            versionTwoStore.Save([entry]);
            var versionTwoDocument = JsonNode.Parse(File.ReadAllText(versionTwoPath))!;
            versionTwoDocument["schemaVersion"] = 2;
            var versionTwoIssue = versionTwoDocument["entries"]![0]!["previousIssues"]![0]!;
            versionTwoIssue.AsObject().Remove("lastSeenUtc");
            versionTwoIssue.AsObject().Remove("isResolved");
            versionTwoIssue.AsObject().Remove("resolvedUtc");
            File.WriteAllText(versionTwoPath, versionTwoDocument.ToJsonString());
            Expect(versionTwoStore.Load().Single().PreviousIssues is [{ IsResolved: false, ResolvedUtc: null }]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckProgressDescriptions()
    {
        var describe = typeof(StudentAgentHealthRepairTransport).GetMethod("Describe",
            BindingFlags.NonPublic | BindingFlags.Static, binder: null,
            types: [typeof(StudentAgentHealthRepairOutcome), typeof(bool)], modifiers: null)
            ?? throw new MissingMethodException("Health repair outcome description was not found.");
        foreach (var outcome in Enum.GetValues<StudentAgentHealthRepairOutcome>())
            Expect(!string.IsNullOrWhiteSpace(describe.Invoke(null, [outcome, false]) as string));
        var queued = (string)describe.Invoke(null, [StudentAgentHealthRepairOutcome.Queued, false])!;
        Expect(queued.Contains("重新读取回执", StringComparison.Ordinal));
        var replayed = (string)describe.Invoke(null, [StudentAgentHealthRepairOutcome.Succeeded, true])!;
        Expect(replayed.Contains("此前已经处理", StringComparison.Ordinal) &&
               replayed.Contains("未重复执行", StringComparison.Ordinal));
    }

    private static async Task CheckSignedProgressStreamAsync()
    {
        using var agentKey = RSA.Create(2048);
        using var wrongKey = RSA.Create(2048);
        var pinned = agentKey.ExportSubjectPublicKeyInfoPem();
        var now = DateTimeOffset.UtcNow;
        var command = CreateCommand(StudentAgentHealthRepairAction.EnsureVeyonServiceRunning, now);
        using var commandSigningKey = RSA.Create(2048);
        var signedCommand = StudentAgentHealthRepairCommandCryptography.Sign(command, commandSigningKey);
        var batchId = Guid.NewGuid();
        var validFrames = Frames(command, StudentAgentHealthRepairOutcome.Succeeded);
        var validLines = SignFrames(validFrames, command, signedCommand, agentKey);
        var (receipt, updates) = await ReadProgressAsync(validLines, command, signedCommand, pinned, batchId);
        Expect(receipt.Outcome == StudentAgentHealthRepairOutcome.Succeeded &&
               updates.Select(item => item.State).SequenceEqual([
                   StudentAgentHealthRepairProgressState.Delivered,
                   StudentAgentHealthRepairProgressState.Executing,
                   StudentAgentHealthRepairProgressState.Succeeded]));
        Expect(updates.All(item => item.BatchId == batchId && item.Target == Target &&
                                   item.CommandId == command.CommandId));

        var edgeCommand = CreateCommand(StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy, now, 23);
        var signedEdgeCommand = StudentAgentHealthRepairCommandCryptography.Sign(edgeCommand, commandSigningKey);
        var queuedFrames = Frames(edgeCommand, StudentAgentHealthRepairOutcome.Queued, websiteRevision: 23);
        var queued = await ReadProgressAsync(SignFrames(queuedFrames, edgeCommand, signedEdgeCommand, agentKey),
            edgeCommand, signedEdgeCommand, pinned, Guid.NewGuid());
        Expect(queued.Receipt.Outcome == StudentAgentHealthRepairOutcome.Queued &&
               queued.Updates[^1].Detail.Contains("重新读取回执", StringComparison.Ordinal));

        await RejectAsync(() => ReadProgressAsync(validLines, command, signedCommand,
            wrongKey.ExportSubjectPublicKeyInfoPem(), batchId));
        await RejectAsync(() => ReadProgressAsync(validLines, command, signedCommand, null, batchId));
        await RejectAsync(() => ReadProgressAsync(validLines, command, signedCommand + "x", pinned, batchId));
        await RejectAsync(() => ReadProgressAsync(validLines, command, signedCommand, pinned, batchId,
            requestBodyOverride: signedCommand + " "));
        await RejectAsync(() => ReadProgressAsync(SignFrames(
                [Frame(command, StudentAgentHealthRepairProgressPhase.Executing), .. validFrames.Skip(1)],
                command, signedCommand, agentKey), command, signedCommand, pinned, batchId));
        foreach (var mutate in new Func<StudentAgentHealthRepairProgressFrame, StudentAgentHealthRepairProgressFrame>[]
                 {
                     frame => frame with { Nonce = Guid.NewGuid() },
                     frame => frame with { CampusId = "other-campus" },
                     frame => frame with { Action = StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy },
                     frame => frame with { WebsiteRevision = 91 }
                 })
        {
            var invalidFrames = validFrames.ToArray();
            invalidFrames[0] = mutate(invalidFrames[0]);
            await RejectAsync(() => ReadProgressAsync(SignFrames(invalidFrames, command, signedCommand, agentKey),
                command, signedCommand, pinned, batchId));
        }
        await RejectAsync(() => ReadProgressAsync(SignFrames(validFrames.Append(validFrames[^1]),
            command, signedCommand, agentKey), command, signedCommand, pinned, batchId));
        await RejectAsync(() => ReadProgressAsync(validLines, command, signedCommand, pinned, batchId,
            terminateWithNewline: false));
        await RejectAsync(() => ReadProgressAsync([new string('A',
                StudentAgentHealthRepairCommandCryptography.MaximumEnvelopeBytes + 1) + "\n"],
            command, signedCommand, pinned, batchId));

        var queuedWithoutExecuting = Frames(edgeCommand, StudentAgentHealthRepairOutcome.Queued, websiteRevision: 23)
            .Where(frame => frame.Phase != StudentAgentHealthRepairProgressPhase.Executing).ToArray();
        await RejectAsync(() => ReadProgressAsync(SignFrames(queuedWithoutExecuting, edgeCommand, signedEdgeCommand, agentKey),
            edgeCommand, signedEdgeCommand, pinned, batchId));
    }

    private static async Task<(StudentAgentHealthRepairReceipt Receipt,
        IReadOnlyList<StudentAgentHealthRepairProgressUpdate> Updates)> ReadProgressAsync(
        IReadOnlyList<string> signedLines, StudentAgentHealthRepairCommand command, string signedCommand,
        string? pinnedKey, Guid batchId, bool terminateWithNewline = true, string? requestBodyOverride = null)
    {
        var raw = string.Join("", signedLines.Select(line => line + "\n"));
        if (!terminateWithNewline && raw.Length > 0) raw = raw[..^1];
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(raw));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
        var progress = new RecordingProgress<StudentAgentHealthRepairProgressUpdate>();
        var method = typeof(StudentAgentHealthRepairTransport).GetMethod("ReadVerifiedProgressAsync",
            BindingFlags.NonPublic | BindingFlags.Static)
                     ?? throw new MissingMethodException("Signed health progress reader was not found.");
        var task = (Task<StudentAgentHealthRepairReceipt>)method.Invoke(null,
            [content, CampusId, Target, pinnedKey, command, requestBodyOverride ?? signedCommand,
                batchId, progress, CancellationToken.None])!;
        var receipt = await task.ConfigureAwait(false);
        return (receipt, progress.Items);
    }

    private static IReadOnlyList<string> SignFrames(IEnumerable<StudentAgentHealthRepairProgressFrame> frames,
        StudentAgentHealthRepairCommand command, string signedCommand, RSA agentKey,
        string? requestBody = null) => Array.AsReadOnly(frames.Select(frame =>
    {
        var body = JsonSerializer.Serialize(frame, ApplicationPolicyCompiler.JsonOptions);
        return StudentAgentCommandAcknowledgementCryptography.Sign(CampusId, command.Nonce,
            requestBody ?? signedCommand, 200, body, agentKey);
    }).ToArray());

    private static StudentAgentHealthRepairProgressFrame[] Frames(StudentAgentHealthRepairCommand command,
        StudentAgentHealthRepairOutcome outcome, long? websiteRevision = null)
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            Frame(command, StudentAgentHealthRepairProgressPhase.Delivered, updatedUtc: now,
                websiteRevision: websiteRevision),
            Frame(command, StudentAgentHealthRepairProgressPhase.Executing, updatedUtc: now.AddMilliseconds(1),
                websiteRevision: websiteRevision),
            Frame(command, StudentAgentHealthRepairProgressPhase.Completed, outcome,
                updatedUtc: now.AddMilliseconds(2), websiteRevision: websiteRevision)
        ];
    }

    private static StudentAgentHealthRepairProgressFrame Frame(StudentAgentHealthRepairCommand command,
        StudentAgentHealthRepairProgressPhase phase, StudentAgentHealthRepairOutcome? outcome = null,
        DateTimeOffset? updatedUtc = null, long? websiteRevision = null) => new(1,
        StudentAgentHealthRepairCommandCryptography.ProgressPurpose, command.CampusId, command.CommandId,
        command.Nonce, command.Action, phase, outcome,
        command.Action == StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy
            ? websiteRevision ?? command.ExpectedWebsiteRevision
            : null,
        (updatedUtc ?? DateTimeOffset.UtcNow).ToUniversalTime());

    private static StudentAgentHealthRepairCommand CreateCommand(StudentAgentHealthRepairAction action,
        DateTimeOffset now, long? websiteRevision = null)
    {
        var issued = now.AddSeconds(-1).ToUniversalTime();
        return new StudentAgentHealthRepairCommand(1, StudentAgentHealthRepairCommandCryptography.Purpose,
            CampusId, Guid.NewGuid(), Guid.NewGuid(), issued, now.AddSeconds(45).ToUniversalTime(), action,
            websiteRevision);
    }

    private static StudentAgentHealthSnapshot CreateHealthSnapshot(bool? runningAsSystem = true,
        bool? taskMatches = true, bool? taskAclRestricted = true,
        StudentVeyonServiceState service = StudentVeyonServiceState.Running, string updateState = "notFound") =>
        new(runningAsSystem, taskMatches, taskAclRestricted, service, updateState, DateTimeOffset.UtcNow);

    private static StudentInputGuardRuntimeSnapshot CreateInputGuardSnapshot(StudentInputGuardPhase phase,
        DateTimeOffset collectedUtc) => new(1, true, phase, collectedUtc, Guid.NewGuid(), Guid.NewGuid(), null, null);

    private static StudentAgentRuntimeSummary CreateRuntimeSummary(DateTimeOffset collectedUtc,
        StudentInputGuardRuntimeSnapshot? inputGuard) => new(
        StudentAgentRuntimeSummaryCryptography.SchemaVersion,
        StudentAgentRuntimeSummaryCryptography.Purpose,
        CampusId,
        Guid.NewGuid(),
        collectedUtc,
        "1.0.0",
        new string('C', 64),
        StudentVeyonServiceState.Running,
        8,
        WebsitePolicyMode.Blocklist,
        null,
        false,
        null,
        null,
        null,
        false,
        null,
        null,
        null,
        null,
        null,
        null,
        "notFound",
        InputGuard: inputGuard);

    private static StudentAgentStatusResponse CreateStatus(bool includeHealth = true,
        StudentAgentHealthSnapshot? health = null,
        StudentVeyonServiceState service = StudentVeyonServiceState.Running, string updateState = "notFound",
        bool edgeDegraded = false, long websiteRevision = 8, long? edgeRevision = null,
        EdgeWebsitePolicyExecutionMode executionMode = EdgeWebsitePolicyExecutionMode.LegacyRegistry)
    {
        if (includeHealth) health ??= CreateHealthSnapshot(service: service, updateState: updateState);
        var now = DateTimeOffset.UtcNow;
        EdgeWebsitePolicyReportedState? edge = null;
        if (edgeDegraded)
        {
            var revision = edgeRevision ?? websiteRevision;
            var hash = new string('A', 64);
            var profiles = new[]
            {
                new EdgeWebsitePolicyApplyResult(1, "websitePolicy.applyResult",
                    Guid.NewGuid().ToString("D"), revision, hash, new string('B', 64), "degraded", 0,
                    now, "apply-failed")
            };
            edge = new EdgeWebsitePolicyReportedState(executionMode, revision, hash, profiles,
                executionMode == EdgeWebsitePolicyExecutionMode.ExtensionRealtime ? new string('a', 32) : null);
        }
        return new StudentAgentStatusResponse(4, WebsitePolicyStatusCryptography.ResponsePurposeV4, CampusId,
            Guid.NewGuid(), now, "1.0.0", new string('C', 64),
            new WebsitePolicyReportedState(websiteRevision, WebsitePolicyMode.Blocklist, null, null), null,
            EdgeWebsite: edge, StudentUpdateProtocolVersion: WebsitePolicyStatusCryptography.CurrentStudentUpdateProtocolVersion,
            Health: includeHealth ? health : null);
    }

    private static TeacherDeviceHealthRow CreateRow(string target = Target, string student = "学生甲",
        string computer = "PC-01") => new(target, student, computer);

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or CryptographicException)
        { return; }
        throw new InvalidOperationException("An invalid health protocol value was accepted.");
    }

    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or CryptographicException)
        { return; }
        throw new InvalidOperationException("An invalid signed health progress stream was accepted.");
    }

    private static void Expect(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Student device health check failed.");
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        private readonly List<T> _items = [];
        public IReadOnlyList<T> Items => _items;
        public void Report(T value) => _items.Add(value);
    }
}
