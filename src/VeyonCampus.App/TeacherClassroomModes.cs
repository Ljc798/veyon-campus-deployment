using System.Runtime.Versioning;
using System.Security.Cryptography;
using VeyonCampus.Core;

namespace VeyonCampus.App;

internal sealed partial class TeacherMobileControlService
{
    [SupportedOSPlatform("windows")]
    public async Task<MobilePolicyOperationResponse> ApplyClassroomModeAsync(Guid deviceId,
        ClassroomMode requestedMode, string? reviewToken, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(requestedMode)) throw new InvalidDataException("课堂模式无效。");
        if (requestedMode == ClassroomMode.Normal && !string.IsNullOrWhiteSpace(reviewToken))
            throw new InvalidDataException("恢复正常课堂不接受应用审核票据。");
        var (campusId, session, targets, targetIds) = CaptureClassroomModeContext();
        await _policyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = _classroomModeStateStore.Read(session.SessionId);
            if (existing is null)
            {
                existing = new ClassroomModeSessionState(ClassroomModeStateStore.CurrentSchemaVersion,
                    session.SessionId, ClassroomMode.Normal, true, DateTimeOffset.UtcNow, []);
                if (requestedMode == ClassroomMode.Practice)
                    _classroomModeStateStore.Save(existing);
            }
            var response = requestedMode switch
            {
                ClassroomMode.Normal => await RestoreOwnedPoliciesAsync(campusId, session, targets,
                    targetIds, existing, cancellationToken).ConfigureAwait(false),
                ClassroomMode.Practice when !string.IsNullOrWhiteSpace(reviewToken) =>
                    await CompletePracticeReviewAsync(deviceId, campusId, session, targets, targetIds,
                        existing, reviewToken, cancellationToken).ConfigureAwait(false),
                ClassroomMode.Practice => await ApplyPracticeAsync(deviceId, campusId, session, targets,
                    targetIds, existing, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidDataException("课堂模式无效。")
            };

            var savedMode = _classroomModeStateStore.Read(session.SessionId)?.Mode ?? ClassroomMode.Normal;
            await PublishClassroomModeAsync(campusId, session, targets, savedMode, cancellationToken)
                .ConfigureAwait(false);
            _stateChanged();
            return response;
        }
        finally { _policyGate.Release(); }
    }

    private (string CampusId, ClassroomSession Session, IReadOnlyList<string> Targets,
        IReadOnlyDictionary<string, Guid> TargetIds) CaptureClassroomModeContext()
    {
        lock (_classroomGate)
        {
            if (_classroomCampusId is not { } campusId || _classroomSession is not { } session ||
                _classroomSessionId != session.SessionId || _classroomTargets.Count == 0 ||
                _classroomTargetIds.Count != _classroomTargets.Count)
                throw new InvalidDataException("当前没有可控制的完整活动课堂。");
            if (!string.Equals(campusId, CurrentCampusId(), StringComparison.Ordinal))
                throw new InvalidDataException("当前校区已变化；请先刷新课堂状态。");
            return (campusId, session, Array.AsReadOnly(_classroomTargets
                    .OrderBy(target => target, StringComparer.OrdinalIgnoreCase).ToArray()),
                new Dictionary<string, Guid>(_classroomTargetIds, StringComparer.OrdinalIgnoreCase));
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task<MobilePolicyOperationResponse> ApplyPracticeAsync(Guid deviceId, string campusId,
        ClassroomSession session, IReadOnlyList<string> targets, IReadOnlyDictionary<string, Guid> targetIds,
        ClassroomModeSessionState existing, CancellationToken cancellationToken)
    {
        var profiles = SelectLatestClassroomProfiles(MobilePolicyProfileStore.ReadAll(_storageDirectory), campusId);
        if (profiles.Count == 0)
            throw new InvalidDataException("当前校区还没有网站或应用临时预设；先在教师端保存一个即可。");
        var selectedProfiles = DescribeSelectedProfiles(profiles);

        using var key = _openTeacherSigningKey(campusId);
        var status = await WebsitePolicyStatusTransport.ReadAsync(targets, campusId, key.PrivateKey,
            cancellationToken).ConfigureAwait(false);
        var statusByTarget = status.ToDictionary(item => item.Target, StringComparer.OrdinalIgnoreCase);
        var results = targets.ToDictionary(target => target, _ => new List<MobilePolicyTargetResult>(),
            StringComparer.OrdinalIgnoreCase);
        var owners = existing.OwnedPolicies.ToList();
        var anyApplied = false;
        var enforceNeedsReview = false;
        string? reviewToken = null;
        IReadOnlyList<MobileApplicationReviewTarget>? applicationReview = null;
        long? lastRevision = null;
        DateTimeOffset? lastExpiry = null;

        foreach (var profile in profiles)
        {
            var kind = ToClassroomPolicyKind(profile.Kind);
            var eligible = new List<string>();
            foreach (var target in targets)
            {
                if (!statusByTarget.TryGetValue(target, out var item) || !item.Succeeded || item.Status is null)
                {
                    results[target].Add(new MobilePolicyTargetResult(target, false, true,
                        "签名状态未知或设备身份待核对；未覆盖当前策略。"));
                    continue;
                }

                var targetId = targetIds[target];
                var owned = owners.SingleOrDefault(owner => owner.TargetId == targetId && owner.Kind == kind);
                var disabled = profile.Kind == MobilePolicyProfileKind.Website
                    ? item.Status.Website.Mode == WebsitePolicyMode.Disabled
                    : item.Status.Application is { Supported: true, Mode: ApplicationPolicyMode.Disabled };
                var currentRevision = profile.Kind == MobilePolicyProfileKind.Website
                    ? item.Status.Website.Revision
                    : item.Status.Application is { Supported: true } app ? app.Revision : null;
                if (disabled) owners.RemoveAll(owner => owner.TargetId == targetId && owner.Kind == kind);
                var canReplace = disabled || owned is not null && currentRevision == owned.Revision;
                if (profile.Kind == MobilePolicyProfileKind.Application &&
                    item.Status.Application is not { Supported: true }) canReplace = false;
                if (owned is not null && currentRevision is not null && currentRevision != owned.Revision && !disabled)
                    owners.Remove(owned);
                if (!canReplace)
                {
                    results[target].Add(new MobilePolicyTargetResult(target, false, true,
                        profile.Kind == MobilePolicyProfileKind.Application && item.Status.Application?.Supported != true
                            ? "电脑不支持应用策略或状态未知；未发送应用策略。"
                            : "电脑当前策略不属于本堂课；已保留原策略。"));
                    continue;
                }
                eligible.Add(target);
            }

            if (eligible.Count == 0) continue;
            MobilePolicyOperationResponse pushed;
            if (profile.Kind == MobilePolicyProfileKind.Application &&
                profile.ApplicationMode == ApplicationPolicyMode.Enforce)
            {
                pushed = await StageApplicationEnforcementAsync(deviceId, profile, eligible,
                    cancellationToken, session.SessionId, ClassroomMode.Practice).ConfigureAwait(false);
                enforceNeedsReview = true;
                reviewToken = pushed.ReviewToken;
                applicationReview = pushed.ApplicationReview;
                if (pushed.RequiresReview)
                {
                    var auditedTargets = await TrackObservedRevisionAsync(key.PrivateKey, campusId, eligible, targetIds, profile.Id,
                        kind, pushed.Revision, owners, cancellationToken,
                        expectedApplicationMode: ApplicationPolicyMode.Audit).ConfigureAwait(false);
                    foreach (var item in pushed.Results)
                        results[item.Target].Add(auditedTargets.Contains(item.Target) && item.AgentAccepted
                            ? item with { Detail = item.Detail + " 审核版本已由签名状态读回。" }
                            : item with { AgentAccepted = false, NeedsReview = true,
                                Detail = "审核版本未能由签名状态确认；未启用应用阻止。" });
                    _classroomModeStateStore.Save(existing with { Mode = ClassroomMode.Normal, Active = true,
                        UpdatedUtc = DateTimeOffset.UtcNow, OwnedPolicies = owners.ToArray() });
                    return pushed with { Results = MergeResults(results, targets),
                        Message = $"{selectedProfiles}已应用网站策略并完成应用审核。请阅读逐台统计并明确确认，确认前不会启用应用阻止。" };
                }
                await TrackObservedRevisionAsync(key.PrivateKey, campusId, eligible, targetIds, profile.Id,
                    kind, pushed.Revision, owners, cancellationToken,
                    expectedApplicationMode: ApplicationPolicyMode.Audit).ConfigureAwait(false);
            }
            else
            {
                pushed = await PushPolicyAsync(profile, eligible, true, cancellationToken).ConfigureAwait(false);
                var readBack = await ReadBackMatchingRevisionAsync(key.PrivateKey, campusId, eligible, kind,
                    pushed.Revision,
                    expectedWebsiteMode: profile.Kind == MobilePolicyProfileKind.Website ? profile.WebsiteMode : null,
                    expectedApplicationMode: profile.Kind == MobilePolicyProfileKind.Application
                        ? profile.ApplicationMode : null, cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (var item in pushed.Results)
                {
                    if (item.AgentAccepted && readBack.Contains(item.Target) &&
                        targetIds.TryGetValue(item.Target, out var targetId) &&
                        pushed.Revision is { } revision)
                    {
                        results[item.Target].Add(item with { Detail = item.Detail + " 策略版本已由签名状态读回。" });
                        UpsertOwnership(owners, new ClassroomPolicyOwnership(targetId, kind,
                            revision, profile.Id));
                        anyApplied = true;
                    }
                    else results[item.Target].Add(item.AgentAccepted
                        ? item with { AgentAccepted = false, NeedsReview = true,
                            Detail = "Agent 已回应，但没有读回本次策略版本；未记录课堂拥有权。" }
                        : item);
                }
            }
            lastRevision = pushed.Revision;
            lastExpiry = pushed.ExpiresUtc;
            if (profile.Kind == MobilePolicyProfileKind.Application &&
                profile.ApplicationMode == ApplicationPolicyMode.Enforce) break;
        }

        var nextMode = !enforceNeedsReview && anyApplied ? ClassroomMode.Practice : ClassroomMode.Normal;
        _classroomModeStateStore.Save(existing with { Mode = nextMode, Active = true,
            UpdatedUtc = DateTimeOffset.UtcNow, OwnedPolicies = owners.ToArray() });
        return new MobilePolicyOperationResponse(enforceNeedsReview ? "needs-review" : "classroom-mode-updated",
            false, reviewToken,
            enforceNeedsReview
                ? $"{selectedProfiles}应用阻止尚未启用。审核回执未能完整匹配；请刷新状态后重新进入练习模式。"
                : anyApplied
                    ? $"{selectedProfiles}已切换到练习模式；逐台结果只表示 Agent 接收或安全跳过，实际效果仍需实机验证。"
                    : $"{selectedProfiles}没有电脑通过安全状态核对；课堂保持正常模式。",
            lastRevision, lastExpiry, MergeResults(results, targets), applicationReview);
    }

    private static string DescribeSelectedProfiles(IReadOnlyList<MobilePolicyProfile> profiles) =>
        "自动选择：" + string.Join("、", profiles.Select(profile =>
            (profile.Kind == MobilePolicyProfileKind.Website ? "网站" : "应用") + "「" + profile.Name + "」")) + "。";

    internal static IReadOnlyList<MobilePolicyProfile> SelectLatestClassroomProfiles(
        IEnumerable<MobilePolicyProfile> profiles, string campusId) => Array.AsReadOnly(profiles
        .Where(profile => profile.CampusId == campusId &&
            profile.Kind is MobilePolicyProfileKind.Website or MobilePolicyProfileKind.Application)
        .GroupBy(profile => profile.Kind)
        .Select(group => group.OrderByDescending(profile => profile.UpdatedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(profile => profile.Id).First())
        .OrderBy(profile => profile.Kind)
        .ToArray());

    [SupportedOSPlatform("windows")]
    private async Task<MobilePolicyOperationResponse> CompletePracticeReviewAsync(Guid deviceId,
        string campusId, ClassroomSession session, IReadOnlyList<string> targets,
        IReadOnlyDictionary<string, Guid> targetIds, ClassroomModeSessionState existing, string reviewToken,
        CancellationToken cancellationToken)
    {
        var tokenHash = MobilePairedDeviceStore.HashToken(reviewToken);
        if (!_reviewGrants.TryGetValue(tokenHash, out var grant) ||
            grant.ClassroomSessionId != session.SessionId || grant.ClassroomMode != ClassroomMode.Practice)
            throw new InvalidDataException("应用审核确认已过期或不属于当前课堂；请重新读取最新统计。");
        var profile = FindProfile(grant.ProfileId);
        if (profile.Kind != MobilePolicyProfileKind.Application ||
            profile.ApplicationMode != ApplicationPolicyMode.Enforce)
            throw new InvalidDataException("应用预设已变化；请重新审核。");
        var response = await CompleteApplicationEnforcementAsync(deviceId, profile, grant.Targets,
            reviewToken, cancellationToken, session.SessionId, ClassroomMode.Practice).ConfigureAwait(false);
        var owners = existing.OwnedPolicies.ToList();
        using var signingKey = _openTeacherSigningKey(campusId);
        var confirmedTargets = await TrackObservedRevisionAsync(signingKey.PrivateKey, campusId, grant.Targets,
            targetIds, profile.Id,
            ClassroomPolicyKind.Application, response.Revision, owners, cancellationToken,
            expectedApplicationMode: ApplicationPolicyMode.Enforce).ConfigureAwait(false);
        var confirmed = confirmedTargets.Count > 0;
        _classroomModeStateStore.Save(existing with { Mode = confirmed ? ClassroomMode.Practice : ClassroomMode.Normal,
            Active = true,
            UpdatedUtc = DateTimeOffset.UtcNow, OwnedPolicies = owners.ToArray() });
        return response with { State = confirmed ? "classroom-mode-updated" : "needs-review",
            Message = confirmed
                ? "教师已确认应用审核统计，练习模式已切换；Agent 接收不代表应用启动效果已实机验证。"
                : "教师确认已提交，但没有读回本次执行版本；课堂保持正常模式并保留审核记录。",
            Results = response.Results.Select(item => confirmedTargets.Contains(item.Target) && item.AgentAccepted
                ? item with { Detail = item.Detail + " 执行版本已由签名状态读回。" }
                : item with { AgentAccepted = false, NeedsReview = true,
                    Detail = "没有读回本次执行版本；课堂保持正常模式并保留审核记录。" }).ToArray() };
    }

    [SupportedOSPlatform("windows")]
    private async Task<MobilePolicyOperationResponse> RestoreOwnedPoliciesAsync(string campusId,
        ClassroomSession session, IReadOnlyList<string> targets, IReadOnlyDictionary<string, Guid> targetIds,
        ClassroomModeSessionState existing, CancellationToken cancellationToken)
    {
        var owners = existing.OwnedPolicies.ToList();
        if (owners.Count == 0)
        {
            if (_classroomModeStateStore.Read(session.SessionId) is not null)
                _classroomModeStateStore.Save(existing with { Mode = ClassroomMode.Normal, Active = true,
                    UpdatedUtc = DateTimeOffset.UtcNow });
            return new MobilePolicyOperationResponse("classroom-mode-updated", false, null,
                "已恢复正常课堂；本堂课没有仍由课堂拥有的临时策略。", null, null, []);
        }

        using var key = _openTeacherSigningKey(campusId);
        var status = await WebsitePolicyStatusTransport.ReadAsync(targets, campusId,
            key.PrivateKey, cancellationToken).ConfigureAwait(false);
        var statusByTarget = status.ToDictionary(item => item.Target, StringComparer.OrdinalIgnoreCase);
        var results = targets.ToDictionary(target => target, _ => new List<MobilePolicyTargetResult>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var owner in existing.OwnedPolicies.ToArray())
        {
            var target = targetIds.SingleOrDefault(pair => pair.Value == owner.TargetId).Key;
            if (target is null || !targets.Contains(target, StringComparer.OrdinalIgnoreCase))
            {
                results[targets[0]].Add(new MobilePolicyTargetResult("待匹配设备", false, true,
                    "本机恢复记录无法匹配当前课堂设备；已保留待恢复项。"));
                continue;
            }
            if (!statusByTarget.TryGetValue(target, out var item) || !item.Succeeded || item.Status is null)
            {
                results[target].Add(new MobilePolicyTargetResult(target, false, true,
                    "设备离线或签名身份无法核对；已保留待恢复项。"));
                continue;
            }
            if (owner.Kind == ClassroomPolicyKind.Application && item.Status.Application is not { Supported: true })
            {
                results[target].Add(new MobilePolicyTargetResult(target, false, true,
                    "应用策略状态无法核对；已保留待恢复项。"));
                continue;
            }
            var currentRevision = owner.Kind == ClassroomPolicyKind.Website
                ? item.Status.Website.Revision
                : item.Status.Application!.Revision;
            var disabled = owner.Kind == ClassroomPolicyKind.Website
                ? item.Status.Website.Mode == WebsitePolicyMode.Disabled
                : item.Status.Application!.Mode == ApplicationPolicyMode.Disabled;
            if (currentRevision != owner.Revision)
            {
                owners.Remove(owner);
                results[target].Add(new MobilePolicyTargetResult(target, false, true,
                    "策略版本已被其他操作更新；保留新版本并交还控制权。"));
                continue;
            }
            if (disabled)
            {
                owners.Remove(owner);
                results[target].Add(new MobilePolicyTargetResult(target, true, false, "限制已解除。"));
                continue;
            }
            var profile = CreateDisabledProfile(campusId, owner.Kind, owner.ProfileId);
            var pushed = await PushPolicyAsync(profile, [target], false, cancellationToken).ConfigureAwait(false);
            var outcome = pushed.Results.SingleOrDefault();
            var readBack = await ReadBackMatchingRevisionAsync(key.PrivateKey, campusId, [target], owner.Kind,
                pushed.Revision,
                expectedWebsiteMode: owner.Kind == ClassroomPolicyKind.Website ? WebsitePolicyMode.Disabled : null,
                    expectedApplicationMode: owner.Kind == ClassroomPolicyKind.Application
                    ? ApplicationPolicyMode.Disabled : null, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (outcome?.AgentAccepted == true && readBack.Contains(target))
            {
                owners.Remove(owner);
                results[target].Add(outcome with { Detail = "本堂课拥有的限制已解除并经签名读回确认。" });
            }
            else results[target].Add(outcome is null
                ? new MobilePolicyTargetResult(target, false, true, "恢复命令未得到确认；保留待恢复项。")
                : outcome with { AgentAccepted = false, NeedsReview = true,
                    Detail = "恢复命令没有读回本次解除版本；保留待恢复项。" });
        }

        _classroomModeStateStore.Save(existing with { Mode = ClassroomMode.Normal, Active = true,
            UpdatedUtc = DateTimeOffset.UtcNow, OwnedPolicies = owners.ToArray() });
        return new MobilePolicyOperationResponse("classroom-mode-updated", false, null,
            owners.Count == 0 ? "已恢复正常课堂。" :
                "有设备离线或需复核；恢复记录已保存在教师电脑。可在下课前再次点“恢复正常”重试。",
            null, null, MergeResults(results, targets));
    }

    private async Task<IReadOnlySet<string>> TrackObservedRevisionAsync(RSA signingKey, string campusId,
        IReadOnlyList<string> targets,
        IReadOnlyDictionary<string, Guid> targetIds, Guid profileId, ClassroomPolicyKind kind,
        long? revision, List<ClassroomPolicyOwnership> owners, CancellationToken cancellationToken,
        ApplicationPolicyMode? expectedApplicationMode = null)
    {
        var matching = await ReadBackMatchingRevisionAsync(signingKey, campusId, targets, kind, revision,
            expectedWebsiteMode: null, expectedApplicationMode: expectedApplicationMode,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var target in matching)
        {
            if (targetIds.TryGetValue(target, out var targetId) && revision is { } value)
                UpsertOwnership(owners, new ClassroomPolicyOwnership(targetId, kind, value, profileId));
        }
        return matching;
    }

    private static async Task<IReadOnlySet<string>> ReadBackMatchingRevisionAsync(RSA signingKey,
        string campusId, IReadOnlyList<string> targets, ClassroomPolicyKind kind, long? revision,
        WebsitePolicyMode? expectedWebsiteMode, ApplicationPolicyMode? expectedApplicationMode,
        CancellationToken cancellationToken)
    {
        if (revision is null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var statuses = await WebsitePolicyStatusTransport.ReadAsync(targets, campusId, signingKey,
            cancellationToken).ConfigureAwait(false);
        return statuses.Where(item => item.Succeeded && item.Status is not null &&
                (kind == ClassroomPolicyKind.Website
                    ? item.Status.Website.Revision == revision && item.Status.Website.Mode == expectedWebsiteMode
                    : item.Status.Application is { Supported: true } app && app.Revision == revision &&
                      app.Mode == expectedApplicationMode))
            .Select(item => item.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private async Task PublishClassroomModeAsync(string campusId, ClassroomSession session,
        IReadOnlyList<string> targets, ClassroomMode mode, CancellationToken cancellationToken)
    {
        using var key = _openTeacherSigningKey(campusId);
        var command = ClassroomStatusCryptography.Create(campusId, session, DateTimeOffset.UtcNow, mode);
        await ClassroomStatusTransport.SendAsync(targets, command, key.PrivateKey, _agentTrustStore,
            cancellationToken).ConfigureAwait(false);
    }

    private static ClassroomPolicyKind ToClassroomPolicyKind(MobilePolicyProfileKind kind) => kind switch
    {
        MobilePolicyProfileKind.Website => ClassroomPolicyKind.Website,
        MobilePolicyProfileKind.Application => ClassroomPolicyKind.Application,
        _ => throw new InvalidDataException("长期系统策略不属于课堂临时模式。")
    };

    private static void UpsertOwnership(List<ClassroomPolicyOwnership> owners,
        ClassroomPolicyOwnership ownership)
    {
        owners.RemoveAll(item => item.TargetId == ownership.TargetId && item.Kind == ownership.Kind);
        owners.Add(ownership);
        if (owners.Count > ClassroomModeStateStore.MaximumOwnedPoliciesPerSession)
            throw new InvalidDataException("课堂策略拥有记录超过本机容量上限。");
    }

    private static MobilePolicyProfile CreateDisabledProfile(string campusId, ClassroomPolicyKind kind,
        Guid profileId) => kind == ClassroomPolicyKind.Website
        ? new MobilePolicyProfile(profileId, "课堂恢复", campusId, MobilePolicyProfileKind.Website, 60,
            WebsitePolicyMode.Blocklist, ["example.invalid"])
        : new MobilePolicyProfile(profileId, "课堂恢复", campusId, MobilePolicyProfileKind.Application, 60,
            ApplicationMode: ApplicationPolicyMode.Audit, StudentSids: [], ApplicationRules: []);

    private static IReadOnlyList<MobilePolicyTargetResult> MergeResults(
        IReadOnlyDictionary<string, List<MobilePolicyTargetResult>> results, IReadOnlyList<string> targets) =>
        Array.AsReadOnly(targets.Select(target =>
        {
            var entries = results[target];
            if (entries.Count == 0)
                return new MobilePolicyTargetResult(target, false, true, "没有应用策略；设备状态未改变。" );
            var accepted = entries.All(item => item.AgentAccepted);
            return new MobilePolicyTargetResult(target, accepted, entries.Any(item => item.NeedsReview) || !accepted,
                string.Join(" ", entries.Select(item => item.Detail).Where(detail => !string.IsNullOrWhiteSpace(detail))));
        }).ToArray());
}
