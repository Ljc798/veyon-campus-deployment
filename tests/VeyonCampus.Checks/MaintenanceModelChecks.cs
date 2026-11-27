using System.Text.Json;
using System.Text.Json.Serialization;
using VeyonCampus.Core.Maintenance;

internal static class MaintenanceModelChecks
{
    public static void Run()
    {
        var now = DateTimeOffset.UtcNow;
        var identityFingerprint = new string('A', 64);
        var capabilityDigest = new string('B', 64);
        var releaseDigest = new string('C', 64);
        var identity = new MaintenanceDeviceIdentity("ExampleCampus", identityFingerprint, "192.168.1.20");
        var routedAgain = identity.WithRouteAddress("student-20.example.invalid");
        Assert(identity.Key == routedAgain.Key, "Route changes must retain the pinned device identity key.");
        Assert(identity.Key != new MaintenanceDeviceKey("ExampleCampus", new string('D', 64)),
            "A changed pinned identity must produce another device key.");

        var capabilities = new MaintenanceCapabilities(identityFingerprint, 4, 1, "0.4.56", new string('E', 64), null,
            ["student-update.schema3", "agent.health"], capabilityDigest, "RSA-PSS-SHA256", now);
        var versions = new MaintenanceComponentVersions("1.1.44", "0.4.56", "0.4.56", null);
        var operationId = Guid.NewGuid();
        var plan = new MaintenancePlan(operationId, identity, MaintenanceOperationKind.FullMaintenance,
            versions, "1.1.45", "0.4.57", capabilities, releaseDigest, null,
            MaintenanceReleaseChannel.Stable, "Setup 与 Agent 均需升级", now);
        Assert(plan.CurrentVersions.AgentRuntimeVersion == capabilities.AgentRuntimeVersion,
            "Plan must preserve the authenticated runtime version.");

        var run = MaintenanceRun.Start(operationId, identity, now);
        var identityEvidence = Evidence(identityFingerprint, "identity", now);
        run = run.VerifyIdentity(identityEvidence, "固定 Agent 身份核验通过", now.AddSeconds(1));
        run = run.VerifyCapabilities(capabilities, versions,
            Evidence(identityFingerprint, "signed-capabilities", now.AddSeconds(2), capabilityDigest),
            "签名能力集核验通过", now.AddSeconds(2));
        run = run.SetPlan(plan, Evidence(identityFingerprint, "maintenance-plan", now.AddSeconds(3)),
            "维护目标和发行摘要已绑定", now.AddSeconds(3));

        var commandId = Guid.NewGuid();
        var offerEvidence = Evidence(identityFingerprint, "signed-update-command", now.AddSeconds(4),
            releaseDigest, commandId);
        run = run.OfferUpdate(commandId, offerEvidence, "已发送签名更新命令", now.AddSeconds(4));
        Assert(ReferenceEquals(run, run.OfferUpdate(commandId, offerEvidence, "重复点击", now.AddSeconds(5))),
            "A repeated offer for the same operation and command must be idempotent.");
        Throws<InvalidOperationException>(() => run.OfferUpdate(Guid.NewGuid(), offerEvidence,
            "不得创建第二个命令", now.AddSeconds(5)));
        run = run.TransitionTo(MaintenanceRunState.Updating, "学生端开始更新",
            Evidence(identityFingerprint, "signed-update-status", now.AddSeconds(6), commandId: commandId),
            now.AddSeconds(6));
        run = run.TransitionTo(MaintenanceRunState.WaitingForCommit, "等待事务提交读回",
            Evidence(identityFingerprint, "signed-update-status", now.AddSeconds(7), commandId: commandId),
            now.AddSeconds(7));
        run = run.TransitionTo(MaintenanceRunState.AgentHealthVerified, "Agent 版本和健康已核验",
            Evidence(identityFingerprint, "signed-agent-health", now.AddSeconds(8), commandId: commandId),
            now.AddSeconds(8));
        run = run.TransitionTo(MaintenanceRunState.ConfigurationSyncing, "开始配置同步",
            Evidence(identityFingerprint, "signed-config-command", now.AddSeconds(9), commandId: commandId),
            now.AddSeconds(9));
        run = run.TransitionTo(MaintenanceRunState.Completed, "维护流程完成",
            Evidence(identityFingerprint, "signed-config-result", now.AddSeconds(10), commandId: commandId),
            now.AddSeconds(10));
        Assert(run.IsTerminal && run.Transitions.Count == 9, "The run should retain each verified state transition.");
        Assert(MaintenanceResult.FromRun(run, "已完成").IsComplete,
            "A completed run should produce a completed structured result.");
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var journalJson = JsonSerializer.Serialize(MaintenanceJournal.Empty(now).AddOrGet(run, now), jsonOptions);
        var restoredJournal = JsonSerializer.Deserialize<MaintenanceJournal>(journalJson, jsonOptions)
                              ?? throw new InvalidDataException("Maintenance journal JSON round trip returned null.");
        Assert(restoredJournal.Find(operationId)?.State == MaintenanceRunState.Completed &&
               restoredJournal.Find(operationId)?.Device.RouteAddress == identity.RouteAddress &&
               restoredJournal.Find(operationId)?.Transitions.Count == run.Transitions.Count,
            "The maintenance journal must round-trip typed state, identity, route, and evidence history.");
        Throws<InvalidOperationException>(() => run.TransitionTo(MaintenanceRunState.Updating,
            "terminal state cannot restart", null, now.AddSeconds(11)));

        var activeOperationId = Guid.NewGuid();
        var activeRun = MaintenanceRun.Start(activeOperationId, identity, now);
        var journal = MaintenanceJournal.Empty(now).AddOrGet(activeRun, now.AddSeconds(1));
        var retried = journal.AddOrGet(MaintenanceRun.Start(activeOperationId, routedAgain, now), now.AddSeconds(2));
        Assert(ReferenceEquals(journal, retried) && retried.Runs.Count == 1,
            "Retrying the same operation id must return the persisted run.");
        Throws<InvalidOperationException>(() => journal.AddOrGet(
            MaintenanceRun.Start(Guid.NewGuid(), routedAgain, now), now.AddSeconds(2)));
        journal = journal.Update(activeRun.VerifyIdentity(identityEvidence, "身份已核验", now.AddSeconds(3)),
            now.AddSeconds(3));
        Assert(journal.Find(activeOperationId)?.State == MaintenanceRunState.IdentityVerified,
            "Journal updates should persist a monotonic transition for the same operation.");
        Throws<InvalidDataException>(() => journal.Update(
            MaintenanceRun.Start(activeOperationId, new MaintenanceDeviceIdentity("ExampleCampus",
                new string('D', 64), "192.168.1.20"), now), now.AddSeconds(4)));

        // 回归：journal 每次从磁盘重读都会重建 evidence 实例。事务推进必须按值判定为单调延续，
        // 不能因为 evidence 引用不同而拒绝在 VerifyIdentity 之后继续核验能力集。
        var reloadOperationId = Guid.NewGuid();
        var reloadRun = MaintenanceRun.Start(reloadOperationId, identity, now);
        var reloadJournal = MaintenanceJournal.Empty(now)
            .AddOrGet(reloadRun, now.AddSeconds(1))
            .Update(reloadRun.VerifyIdentity(identityEvidence, "身份已核验", now.AddSeconds(1)),
                now.AddSeconds(1));
        var reloadOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters =
            {
                new JsonStringEnumConverter<MaintenanceRunState>(JsonNamingPolicy.CamelCase,
                    allowIntegerValues: false)
            }
        };
        var reloadedJournal = JsonSerializer.Deserialize<MaintenanceJournal>(
            JsonSerializer.SerializeToUtf8Bytes(reloadJournal, reloadOptions), reloadOptions)!;
        var inMemoryRun = reloadJournal.Find(reloadOperationId)!;
        var advanced = inMemoryRun.VerifyCapabilities(capabilities, versions,
            Evidence(identityFingerprint, "signed-capabilities", now.AddSeconds(2), capabilityDigest),
            "签名能力集核验通过", now.AddSeconds(2));
        var merged = reloadedJournal.Update(advanced, now.AddSeconds(2));
        Assert(merged.Find(reloadOperationId)?.State == MaintenanceRunState.CapabilitiesVerified,
            "A reloaded journal must accept monotonic evidence transitions by value, not by object identity.");
        Assert(reloadJournal.Find(reloadOperationId)!.Transitions[0].Evidence ==
               reloadedJournal.Find(reloadOperationId)!.Transitions[0].Evidence,
            "Maintenance evidence must compare by value across journal reloads.");

        // 回归：两次状态写入之间系统时钟被校正（NTP 或有人改了日期）时，新的转移时间早于上一条，
        // 这条记录以前会让整条 run 在下次读取时报「状态历史不连续」，设备永久停在 Failed。
        var clockStepOperationId = Guid.NewGuid();
        var clockStep = MaintenanceRun.Start(clockStepOperationId, identity, now);
        clockStep = clockStep.TransitionTo(MaintenanceRunState.ManualRequired, "先进入人工核对", null,
            now.AddSeconds(30));
        var steppedBack = clockStep.TransitionTo(MaintenanceRunState.Failed, "时钟回拨后的写入", null,
            now.AddSeconds(60).AddMinutes(-5));
        var clockStepJournal = MaintenanceJournal.Empty(now)
            .AddOrGet(MaintenanceRun.Start(clockStepOperationId, identity, now), now)
            .Update(clockStep, now.AddSeconds(1))
            .Update(steppedBack, now.AddSeconds(2));
        Assert(clockStepJournal.Find(clockStepOperationId)?.Transitions[^1].OccurredUtc >=
               clockStep.Transitions[^1].OccurredUtc,
            "A transition stamped before the previous one must be clamped, not break the run history.");

        // 回归：学生端签名状态按秒打时间戳，同一次点击里同一事实常被读回两次（对账后紧跟配置阶段）。
        // 过去第二次读回因「时间戳相同」被判为非单调延续，整批设备直接失败。
        var sameStampOperationId = Guid.NewGuid();
        var sameStampVersions = new MaintenanceComponentVersions("1.1.45", "0.4.57", "0.4.57", null);
        var sameStamp = now.AddSeconds(60);
        var sameStampJournal = MaintenanceJournal.Empty(now)
            .AddOrGet(MaintenanceRun.Start(sameStampOperationId, identity, now), now);
        MaintenanceRun VerifiedFact(string? transactionState, DateTimeOffset updated) =>
            new(sameStampOperationId, identity, MaintenanceRunState.Discovered, null, null, null, null, now,
                updated, Array.Empty<MaintenanceTransition>(), sameStampVersions, transactionState, sameStamp);
        sameStampJournal = sameStampJournal.Update(VerifiedFact("inProgress", now.AddSeconds(1)), now.AddSeconds(1));
        sameStampJournal = sameStampJournal.Update(VerifiedFact("committed", now.AddSeconds(2)), now.AddSeconds(2));
        sameStampJournal = sameStampJournal.Update(VerifiedFact("committed", now.AddSeconds(3)), now.AddSeconds(3));
        Assert(sameStampJournal.Find(sameStampOperationId)?.VerifiedUpdateTransactionState == "committed",
            "An identical signed status re-read within the same stamp must be accepted, not rejected as non-monotonic.");
        var backward = VerifiedFact("inProgress", now.AddSeconds(4));
        try
        {
            _ = sameStampJournal.Update(backward, now.AddSeconds(4));
            throw new InvalidOperationException("A same-stamp rewrite back to another transaction state must be rejected.");
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains("单调延续", StringComparison.Ordinal))
        {
        }
    }

    private static MaintenanceEvidence Evidence(string identityFingerprint, string kind, DateTimeOffset time,
        string? digest = null, Guid? commandId = null) =>
        new(kind, identityFingerprint, digest, commandId, time);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
