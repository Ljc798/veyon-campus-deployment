using System.Security.Cryptography;
using VeyonCampus.Core;

namespace VeyonCampus.App;

internal sealed partial class TeacherMobileControlService
{
    public MobileClassroomRestoreListResponse ListEndedClassroomRestores()
    {
        var sessions = _classroomSessionStore.ReadRecent().ToDictionary(item => item.SessionId);
        var pending = ClassroomRestoreLedger.ListPending(_classroomModeStateStore.ReadAll(), sessions.Values);
        var items = pending.Take(50).Select(item => new MobileClassroomRestoreItem(item.RoomName,
            sessions.TryGetValue(item.SessionId, out var session)
                ? GetStudentNameForSessionTarget(session, item.DeviceLabel)
                : "未填写姓名",
            item.Kind == ClassroomPolicyKind.Website ? "网站" : "应用")).ToArray();
        return new MobileClassroomRestoreListResponse(pending.Count, Array.AsReadOnly(items),
            Math.Max(0, pending.Count - items.Length));
    }

    public async Task<MobilePolicyOperationResponse> RetryEndedClassroomRestoresAsync(
        CancellationToken cancellationToken)
    {
        await _policyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_classroomGate)
                if (_classroomSessionId is not null)
                    throw new InvalidOperationException("课堂进行中；下课后再重试旧课堂的待恢复项。");
            if (_classroomSessionStore.ReadActive() is not null)
                throw new InvalidOperationException("本机课堂仍在进行；下课后再重试旧课堂的待恢复项。");

            var states = _classroomModeStateStore.ReadAll()
                .Where(item => !item.Active && item.OwnedPolicies.Count > 0).ToArray();
            if (states.Length == 0)
                return new MobilePolicyOperationResponse("classroom-restores-complete", false, null,
                    "没有待恢复的课堂策略。", null, null, []);

            var sessions = _classroomSessionStore.ReadRecent().ToDictionary(item => item.SessionId);
            var campuses = _campusDirectoryStore.Load();
            var locations = await Task.Run(_networkDirectory.ReadLocations, cancellationToken)
                .ConfigureAwait(false);
            var results = new List<MobilePolicyTargetResult>();
            foreach (var state in states.OrderBy(item => sessions.TryGetValue(item.SessionId, out var session)
                         ? session.StartedAtUtc : item.UpdatedUtc))
            {
                if (!sessions.TryGetValue(state.SessionId, out var endedSession) ||
                    endedSession.Status != ClassroomSessionStatus.Ended)
                {
                    results.AddRange(state.OwnedPolicies.Select(owner => new MobilePolicyTargetResult(
                        "无法匹配的电脑", false, true, "原课堂记录已不可用；未发送命令，恢复项仍保留。")));
                    continue;
                }

                var campusMatches = campuses.Where(item => item.ProfileId == endedSession.Room.CampusProfileId)
                    .ToArray();
                var roomMatches = campusMatches.Length == 1
                    ? campusMatches[0].Rooms.Where(item => item.RoomId == endedSession.Room.RoomId).ToArray()
                    : [];
                if (campusMatches.Length != 1 || roomMatches.Length != 1)
                {
                    results.AddRange(state.OwnedPolicies.Select(owner => new MobilePolicyTargetResult(
                        RestoreLabel(endedSession, owner.TargetId), false, true,
                        "原校区或机房档案无法唯一匹配；未发送命令，恢复项仍保留。")));
                    continue;
                }

                var campusId = ResolveClassroomRestoreSigningCampus(endedSession);
                if (campusId is null)
                {
                    results.AddRange(state.OwnedPolicies.Select(owner => new MobilePolicyTargetResult(
                        RestoreLabel(endedSession, owner.TargetId), false, true,
                        "原课堂签名密钥无法唯一确认；未发送命令，恢复项仍保留。")));
                    continue;
                }

                var matchedTargets = ClassroomRestoreLedger.MatchCurrentTargets(endedSession,
                    campusMatches[0], roomMatches[0], locations);
                if (matchedTargets.Count == 0)
                {
                    results.AddRange(state.OwnedPolicies.Select(owner => new MobilePolicyTargetResult(
                        RestoreLabel(endedSession, owner.TargetId), false, true,
                        "当前机房没有唯一对应的电脑目录项；未发送命令，恢复项仍保留。")));
                    continue;
                }

                var targetIds = matchedTargets.ToDictionary(item => item.Target, item => item.TargetId,
                    StringComparer.OrdinalIgnoreCase);
                var targetLabels = endedSession.Targets.ToDictionary(item => item.TargetId,
                    item => GetStudentNameForSessionTarget(endedSession, item.DeviceLabel));
                var response = await RestoreOwnedPoliciesAsync(campusId, endedSession,
                    matchedTargets.Select(item => item.Target).ToArray(), targetIds, state,
                    cancellationToken, targetLabels).ConfigureAwait(false);
                var labelByTarget = matchedTargets.ToDictionary(item => item.Target,
                    item => GetStudentNameForSessionTarget(endedSession, item.DeviceLabel),
                    StringComparer.OrdinalIgnoreCase);
                results.AddRange(response.Results.Select(item => labelByTarget.TryGetValue(item.Target, out var label)
                    ? item with { Target = label }
                    : item));
            }

            var pendingCount = ListEndedClassroomRestores().Count;
            return new MobilePolicyOperationResponse("classroom-restores-retried", false, null,
                pendingCount == 0
                    ? "待恢复项已处理；外部更新的策略保持不变。"
                    : $"已重试；仍有 {pendingCount} 项因离线、目录不匹配或版本变化而保留。",
                null, null, Array.AsReadOnly(results.ToArray()));
        }
        finally { _policyGate.Release(); }
    }

    private string RestoreLabel(ClassroomSession session, Guid targetId)
    {
        var label = session.Targets.FirstOrDefault(item => item.TargetId == targetId)?.DeviceLabel;
        return label is null ? "未填写姓名" : GetStudentNameForSessionTarget(session, label);
    }

    private string GetStudentNameForSessionTarget(ClassroomSession session, string deviceLabel)
    {
        var targetIndex = -1;
        for (var index = 0; index < session.Targets.Length; index++)
            if (string.Equals(session.Targets[index].DeviceLabel, deviceLabel,
                    StringComparison.OrdinalIgnoreCase))
            {
                targetIndex = index;
                break;
            }
        try
        {
            var layout = _classroomSeatGridStore.GetLayout(session.Room.CampusProfileId,
                session.Room.RoomId, session.Room.StartNumber);
            var matches = layout.Seats.Where(item =>
                string.Equals(item.VeyonComputerName, deviceLabel, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.VeyonHostAddress, deviceLabel, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            var computerNumbers = session.Room.ComputerNumbers ??
                Enumerable.Range(session.Room.StartNumber, session.Room.ComputerCount).ToArray();
            if (matches.Length == 0 && targetIndex >= 0 && targetIndex < computerNumbers.Length)
                matches = layout.Seats.Where(item => item.ComputerNumber == computerNumbers[targetIndex])
                    .Take(2).ToArray();
            return matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0].StudentName)
                ? "未填写姓名" : matches[0].StudentName.Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return "未填写姓名";
        }
    }

    private string? ResolveClassroomRestoreSigningCampus(ClassroomSession session)
    {
        var available = WebsitePolicySigningKeyStore.ReadCampusIds()
            .Where(item => !ApplicationPolicySigningKeyStore.IsInternalNamespace(item) &&
                           !StudentSystemPolicySigningKeyStore.IsInternalNamespace(item))
            .Distinct(StringComparer.Ordinal).ToArray();
        var pinned = _classroomSigningContextStore.Read(session.SessionId);
        if (pinned is not null) return available.Contains(pinned, StringComparer.Ordinal) ? pinned : null;
        if (available.Contains(session.Room.CampusName, StringComparer.Ordinal)) return session.Room.CampusName;
        var selected = _activeCampusId().Trim();
        if (available.Contains(selected, StringComparer.Ordinal)) return selected;
        return available.Length == 1 ? available[0] : null;
    }

    public async Task<MobilePolicyOperationResponse> RestoreLegacyClassroomPoliciesAsync(
        CancellationToken cancellationToken)
    {
        var (campusId, session, targets, targetIds) = CaptureClassroomRestoreContext();
        await _policyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = _classroomModeStateStore.Read(session.SessionId);
            existing ??= new ClassroomModeSessionState(ClassroomModeStateStore.CurrentSchemaVersion,
                session.SessionId, ClassroomMode.Normal, true, DateTimeOffset.UtcNow, []);
            var response = await RestoreOwnedPoliciesAsync(campusId, session, targets,
                targetIds, existing, cancellationToken,
                session.Targets.ToDictionary(item => item.TargetId,
                    item => GetStudentNameForSessionTarget(session, item.DeviceLabel))).ConfigureAwait(false);

            await PublishClassroomPresenceAsync(campusId, session, targets, cancellationToken)
                .ConfigureAwait(false);
            _stateChanged();
            return response;
        }
        finally { _policyGate.Release(); }
    }

    private (string CampusId, ClassroomSession Session, IReadOnlyList<string> Targets,
    IReadOnlyDictionary<string, Guid> TargetIds) CaptureClassroomRestoreContext()
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

    internal static IReadOnlyList<MobilePolicyProfile> SelectLatestClassroomProfiles(
        IEnumerable<MobilePolicyProfile> profiles, string campusId)
    {
        var eligible = profiles.Where(profile => profile.CampusId == campusId &&
            profile.Kind is MobilePolicyProfileKind.Website or MobilePolicyProfileKind.Application or
                MobilePolicyProfileKind.WebsiteApplication).ToArray();
        var latestCombined = eligible.Where(profile => profile.Kind == MobilePolicyProfileKind.WebsiteApplication)
            .OrderByDescending(profile => profile.UpdatedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(profile => profile.Id).FirstOrDefault();
        if (latestCombined is not null) return Array.AsReadOnly([latestCombined]);
        return Array.AsReadOnly(eligible.Where(profile =>
                profile.Kind is MobilePolicyProfileKind.Website or MobilePolicyProfileKind.Application)
            .GroupBy(profile => profile.Kind)
            .Select(group => group.OrderByDescending(profile => profile.UpdatedUtc ?? DateTimeOffset.MinValue)
                .ThenBy(profile => profile.Id).First())
            .OrderBy(profile => profile.Kind)
            .ToArray());
    }

    private async Task<MobilePolicyOperationResponse> RestoreOwnedPoliciesAsync(string campusId,
        ClassroomSession session, IReadOnlyList<string> targets, IReadOnlyDictionary<string, Guid> targetIds,
        ClassroomModeSessionState existing, CancellationToken cancellationToken,
        IReadOnlyDictionary<Guid, string>? targetLabels = null)
    {
        var owners = existing.OwnedPolicies.ToList();
        if (owners.Count == 0)
        {
            if (_classroomModeStateStore.Read(session.SessionId) is not null)
            {
                if (existing.Active)
                    _classroomModeStateStore.Save(existing with { Mode = ClassroomMode.Normal,
                        UpdatedUtc = DateTimeOffset.UtcNow });
                else _classroomModeStateStore.Remove(session.SessionId);
            }
            return new MobilePolicyOperationResponse("classroom-policy-restored", false, null,
                "已恢复正常课堂；本堂课没有仍由课堂拥有的临时策略。", null, null, []);
        }

        using var key = _openTeacherSigningKey(campusId);
        var status = await WebsitePolicyStatusTransport.ReadAsync(targets, campusId,
            key.PrivateKey, cancellationToken).ConfigureAwait(false);
        var statusByTarget = status.ToDictionary(item => item.Target, StringComparer.OrdinalIgnoreCase);
        var results = targets.ToDictionary(target => target, _ => new List<MobilePolicyTargetResult>(),
            StringComparer.OrdinalIgnoreCase);
        var unmatchedResults = new List<MobilePolicyTargetResult>();
        foreach (var owner in existing.OwnedPolicies.ToArray())
        {
            var target = targetIds.SingleOrDefault(pair => pair.Value == owner.TargetId).Key;
            if (target is null || !targets.Contains(target, StringComparer.OrdinalIgnoreCase))
            {
                unmatchedResults.Add(new MobilePolicyTargetResult(
                    targetLabels?.GetValueOrDefault(owner.TargetId) ?? "无法匹配的电脑", false, true,
                    "当前机房没有唯一对应的电脑目录项；未发送命令，恢复项仍保留。"));
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
            if (!SupportsPolicyCompareAndSet(item.Status.AgentVersion))
            {
                results[target].Add(new MobilePolicyTargetResult(target, false, true,
                    "学生 Agent 版本不支持策略冲突保护；未发送恢复命令，待恢复项仍保留。请先更新 Agent。"));
                continue;
            }
            var profile = CreateDisabledProfile(campusId, owner.Kind, owner.ProfileId);
            var expected = owner.Kind == ClassroomPolicyKind.Website
                ? new MobilePolicyExpectedRevisions(Website: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                    { [target] = owner.Revision })
                : new MobilePolicyExpectedRevisions(Application: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                    { [target] = owner.Revision });
            var pushed = await PushPolicyAsync(profile, [target], false, cancellationToken, expected).ConfigureAwait(false);
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

        if (owners.Count == 0 && !existing.Active)
            _classroomModeStateStore.Remove(session.SessionId);
        else
            _classroomModeStateStore.Save(existing with { Mode = ClassroomMode.Normal,
                UpdatedUtc = DateTimeOffset.UtcNow, OwnedPolicies = owners.ToArray() });
        return new MobilePolicyOperationResponse("classroom-policy-restored", false, null,
            owners.Count == 0 ? "已恢复正常课堂。" : existing.Active
                ? "有设备离线或需复核；恢复记录已保存在教师电脑。可在下课前再次点“恢复正常”重试。"
                : "有设备离线或目录未匹配；恢复记录仍保存在教师电脑。可稍后重试。",
            null, null, Array.AsReadOnly(MergeResults(results, targets).Concat(unmatchedResults).ToArray()));
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

    private async Task PublishClassroomPresenceAsync(string campusId, ClassroomSession session,
        IReadOnlyList<string> targets, CancellationToken cancellationToken)
    {
        using var key = _openTeacherSigningKey(campusId);
        var command = ClassroomStatusCryptography.Create(campusId, session, DateTimeOffset.UtcNow);
        await ClassroomStatusTransport.SendAsync(targets, command, key.PrivateKey, _agentTrustStore,
            cancellationToken).ConfigureAwait(false);
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
