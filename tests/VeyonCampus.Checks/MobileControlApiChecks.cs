using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using VeyonCampus.App;
using VeyonCampus.Core;

internal static class MobileControlApiChecks
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record ClassroomLoadAgent(string Target, string PublicKeyPem, string AccessToken,
        string FirstEventEnvelope, Guid FirstEventId, string SecondEventEnvelope, Guid SecondEventId);
    private sealed record ClassroomLoadMeasurement(string MeasurementScope, int TargetCount,
        double DeliveryRoundTripMilliseconds,
        double SubmissionLatencyP50Milliseconds, double SubmissionLatencyP95Milliseconds,
        double SubmissionLatencyMaxMilliseconds);

    public static async Task RunAsync()
    {
        var directory = Path.Combine(TestPath.CanonicalTempRoot(),
            "veyon-mobile-api-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var httpsPort = GetFreePort();
        var bootstrapPort = GetFreePort();
        while (bootstrapPort == httpsPort) bootstrapPort = GetFreePort();
        var identity = CreateIdentity(IPAddress.Loopback);
        const string eventTarget = "PC-08";
        var eventRoom = new TeacherRoomProfile(Guid.NewGuid(), "测试机房", "PC-", 8, 1);
        var eventCampus = new TeacherCampusProfile(Guid.NewGuid(), "demo", [eventRoom]);
        var eventSession = ClassroomSession.Start(eventCampus, eventRoom.RoomId, DateTimeOffset.UtcNow);
        var eventSessionId = eventSession.SessionId;
        var restoreRoom = new TeacherRoomProfile(Guid.NewGuid(), "备份机房", "SAFE-", 1, 1, ["192.0.2.20"]);
        var restoreCampus = new TeacherCampusProfile(Guid.NewGuid(), "demo", [restoreRoom]);
        var classroomHistory = new ClassroomSessionStore(Path.Combine(directory, "classroom-sessions.json"));
        var restoreSession = classroomHistory.StartSession(restoreCampus, restoreRoom.RoomId,
            DateTimeOffset.UtcNow.AddHours(-1));
        restoreSession = classroomHistory.EndSession(restoreSession.SessionId, DateTimeOffset.UtcNow.AddMinutes(-30))!;
        new ClassroomModeStateStore(Path.Combine(directory, "classroom-mode-state.json")).Save(
            new ClassroomModeSessionState(1, restoreSession.SessionId, ClassroomMode.Normal, false,
                DateTimeOffset.UtcNow.AddMinutes(-30), [new ClassroomPolicyOwnership(
                    restoreSession.Targets[0].TargetId, ClassroomPolicyKind.Website, 5, Guid.NewGuid())]));
        var agentTrustStore = new StudentAgentIdentityTrustStore(Path.Combine(directory, "agent-pins.json"));
        using var agentSigningKey = RSA.Create(2048);
        var agentPublicKeyPem = agentSigningKey.ExportSubjectPublicKeyInfoPem();
        var agentFingerprint = StudentAgentResponseCryptography.GetFingerprint(agentPublicKeyPem);
        agentTrustStore.Pin(new StudentAgentIdentityTrustCandidate(eventTarget, "demo", agentPublicKeyPem,
            agentFingerprint));
        using var teacherSigningKey = RSA.Create(2048);
        var teacherPublicKeyPem = teacherSigningKey.ExportSubjectPublicKeyInfoPem();
        var teacherPrivateKeyPem = teacherSigningKey.ExportPkcs8PrivateKeyPem();
        WebsitePolicySigningKey OpenTestTeacherSigningKey(string campusId)
        {
            if (campusId != "demo") throw new InvalidDataException("Unexpected test campus.");
            var key = RSA.Create();
            key.ImportFromPem(teacherPrivateKeyPem);
            return new WebsitePolicySigningKey(key, key.ExportSubjectPublicKeyInfoPem(), "test-only");
        }

        var changes = 0;
        var screenCaptureCount = 0;
        var fakeScreenPng = Convert.FromHexString("89504E470D0A1A0A0000000D4948445200000001000000010806000000");
        await VerifyScreenPreviewCaptureAsync(fakeScreenPng);
        await using var service = new TeacherMobileControlService(identity, () => Interlocked.Increment(ref changes),
            () => "demo", directory, httpsPort, bootstrapPort, agentTrustStore, OpenTestTeacherSigningKey,
            (_, _) =>
            {
                Interlocked.Increment(ref screenCaptureCount);
                return Task.FromResult((byte[])fakeScreenPng.Clone());
            });
        service.SetClassroomSession("demo", eventSessionId, [eventTarget],
            new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
            {
                [eventTarget] = eventSession.Targets[0].TargetId
            }, ClassroomMode.Normal, eventSession);
        var countdownStore = new ClassroomCountdownStore(Path.Combine(directory, "classroom-countdown.json"));
        countdownStore.Start(eventSessionId, 15, DateTimeOffset.UtcNow);
        var taskProgressStore = new ClassroomTaskProgressStore(Path.Combine(directory, "classroom-task-progress.json"));
        var firstTask = taskProgressStore.Add(eventSessionId, "阅读题目", DateTimeOffset.UtcNow);
        taskProgressStore.Add(eventSessionId, "完成练习", DateTimeOffset.UtcNow);
        taskProgressStore.SetCompleted(eventSessionId, firstTask.Tasks[0].TaskId, true, DateTimeOffset.UtcNow);
        try
        {
            await service.StartAsync(CancellationToken.None);
            using (var bootstrapClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{bootstrapPort}/") })
            using (var bootstrapPage = await bootstrapClient.GetAsync("#pair=01234567"))
            {
                var html = await bootstrapPage.Content.ReadAsStringAsync();
                var displayedFingerprint = string.Join(" ", Enumerable.Range(0, identity.RootFingerprint.Length / 2)
                    .Select(index => identity.RootFingerprint.Substring(index * 2, 2)));
                if (bootstrapPage.StatusCode != HttpStatusCode.OK ||
                    !html.Contains("下载教师根证书", StringComparison.Ordinal) ||
                    !html.Contains(displayedFingerprint, StringComparison.Ordinal) ||
                    !html.Contains($"secure.port = \"{httpsPort}\"", StringComparison.Ordinal) ||
                    !html.Contains("VPN 与设备管理", StringComparison.Ordinal) ||
                    !html.Contains("证书信任设置", StringComparison.Ordinal) ||
                    !html.Contains("配对这部手机", StringComparison.Ordinal) ||
                    !html.Contains($"href=\"https://127.0.0.1:{httpsPort}/\"", StringComparison.Ordinal))
                    throw new InvalidDataException($"Bootstrap page check failed: {(int)bootstrapPage.StatusCode}; {html[..Math.Min(html.Length, 800)]}");
            }
            using (var certificateClient = new HttpClient
                   { BaseAddress = new Uri($"http://127.0.0.1:{bootstrapPort}/") })
            using (var certificate = await certificateClient.GetAsync("teacher-mobile-root.cer"))
            {
                var bytes = await certificate.Content.ReadAsByteArrayAsync();
                Expect(certificate.StatusCode == HttpStatusCode.OK &&
                       bytes.SequenceEqual(identity.RootCertificateBytes));
            }
            using var handler = new HttpClientHandler { UseProxy = false };
            var expectedThumbprint = identity.Server.Thumbprint;
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                string.Equals(certificate?.GetCertHashString(), expectedThumbprint, StringComparison.OrdinalIgnoreCase);
            using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{httpsPort}/") };
            var origin = $"https://127.0.0.1:{httpsPort}";

            var websiteProfile = new MobilePolicyProfile(Guid.NewGuid(), "当前校区", "demo",
                MobilePolicyProfileKind.Website, 60, WebsitePolicyMode.Blocklist, ["example.com"]);
            var otherCampusProfile = websiteProfile with { Id = Guid.NewGuid(), Name = "其他校区", CampusId = "other" };
            var systemProfile = new MobilePolicyProfile(Guid.NewGuid(), "长期基线", "demo",
                MobilePolicyProfileKind.System, 0, StudentSids: ["S-1-5-21-123-456-789-1001"],
                SystemSettings: new StudentSystemPolicySettings(true, true, false, false, true, false));
            var profileNow = DateTimeOffset.UtcNow;
            var olderWebsiteProfile = websiteProfile with { Id = Guid.NewGuid(), Name = "旧网站预设",
                UpdatedUtc = profileNow.AddDays(-1) };
            var latestWebsiteProfile = websiteProfile with { Id = Guid.NewGuid(), Name = "最新网站预设",
                UpdatedUtc = profileNow };
            var applicationProfile = new MobilePolicyProfile(Guid.NewGuid(), "最新应用预设", "demo",
                MobilePolicyProfileKind.Application, 60, ApplicationMode: ApplicationPolicyMode.Audit,
                StudentSids: [], ApplicationRules: [], UpdatedUtc: profileNow);
            var selectedClassroomProfiles = TeacherMobileControlService.SelectLatestClassroomProfiles(
                [olderWebsiteProfile, latestWebsiteProfile, applicationProfile, systemProfile, otherCampusProfile],
                "demo");
            Expect(selectedClassroomProfiles.Count == 2 && selectedClassroomProfiles.Contains(latestWebsiteProfile) &&
                   selectedClassroomProfiles.Contains(applicationProfile) &&
                   !selectedClassroomProfiles.Any(profile => profile.Kind == MobilePolicyProfileKind.System));
            MobilePolicyProfileStore.Save(websiteProfile, directory);
            MobilePolicyProfileStore.Save(systemProfile, directory);
            MobilePolicyProfileStore.Save(otherCampusProfile, directory);

            var invitation = service.CreatePairingInvitation();
            using (var duplicateJson = await PostJsonAsync(client, "/api/pair",
                       $"{{\"pairingCode\":\"{invitation.Code}\",\"pairingCode\":\"{invitation.Code}\",\"deviceName\":\"测试手机\"}}",
                       origin))
                Expect(duplicateJson.StatusCode == HttpStatusCode.BadRequest);
            var wrongCode = invitation.Code == "00000000" ? "00000001" : "00000000";
            using (var wrong = await PostJsonAsync(client, "/api/pair",
                       JsonSerializer.Serialize(new { pairingCode = wrongCode, deviceName = "测试手机" }, JsonOptions), origin))
                Expect(wrong.StatusCode == HttpStatusCode.Unauthorized);

            using var pairResponse = await PostJsonAsync(client, "/api/pair",
                JsonSerializer.Serialize(new { pairingCode = invitation.Code, deviceName = "测试手机" }, JsonOptions), origin);
            Expect(pairResponse.StatusCode == HttpStatusCode.OK);
            var pairRequest = await ReadJsonAsync<MobilePairPendingResponse>(pairResponse);
            Expect(!string.IsNullOrWhiteSpace(pairRequest.Ticket) &&
                   !MobilePairedDeviceStore.IsAuthorized(pairRequest.Ticket, directory));
            var pending = service.ListPendingPairings();
            Expect(pending.Count == 1 && pending[0].DeviceName == "测试手机" && pending[0].SourceAddress == "127.0.0.1");

            using (var wrongOrigin = await PostJsonAsync(client, "/api/pair-status",
                       JsonSerializer.Serialize(new MobilePairPollRequest(pairRequest.Ticket), JsonOptions),
                       "https://untrusted.example"))
                Expect(wrongOrigin.StatusCode == HttpStatusCode.Forbidden);
            using (var waiting = await PostJsonAsync(client, "/api/pair-status",
                       JsonSerializer.Serialize(new MobilePairPollRequest(pairRequest.Ticket), JsonOptions), origin))
                Expect((await ReadJsonAsync<MobilePairPollResponse>(waiting)).State == "waiting-approval");

            Expect(service.ApprovePairing(pending[0].Id));
            Expect(service.ListPendingPairings().Count == 0);
            using var approvedResponse = await PostJsonAsync(client, "/api/pair-status",
                JsonSerializer.Serialize(new MobilePairPollRequest(pairRequest.Ticket), JsonOptions), origin);
            var approved = await ReadJsonAsync<MobilePairPollResponse>(approvedResponse);
            Expect(approvedResponse.StatusCode == HttpStatusCode.OK && approved.State == "approved" &&
                   approved.Device?.DisplayName == "测试手机" && !string.IsNullOrWhiteSpace(approved.AccessToken));
            var accessToken = approved.AccessToken!;
            var deviceId = approved.Device!.Id;
            Expect(MobilePairedDeviceStore.IsAuthorized(accessToken, directory));
            using (var approvedRetryResponse = await PostJsonAsync(client, "/api/pair-status",
                       JsonSerializer.Serialize(new MobilePairPollRequest(pairRequest.Ticket), JsonOptions), origin))
            {
                var approvedRetry = await ReadJsonAsync<MobilePairPollResponse>(approvedRetryResponse);
                Expect(approvedRetryResponse.StatusCode == HttpStatusCode.OK &&
                       approvedRetry.State == "approved" && approvedRetry.AccessToken == accessToken &&
                       approvedRetry.Device == approved.Device &&
                       MobilePairedDeviceStore.List(directory).Count(device => device.Id == deviceId) == 1);
            }

            var rejectedInvitation = service.CreatePairingInvitation();
            using var rejectedPairResponse = await PostJsonAsync(client, "/api/pair",
                JsonSerializer.Serialize(new { pairingCode = rejectedInvitation.Code, deviceName = "第二手机" }, JsonOptions), origin);
            var rejectedPair = await ReadJsonAsync<MobilePairPendingResponse>(rejectedPairResponse);
            var rejectedPending = service.ListPendingPairings().Single();
            Expect(service.RejectPairing(rejectedPending.Id));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var rejectedPoll = await PostJsonAsync(client, "/api/pair-status",
                    JsonSerializer.Serialize(new MobilePairPollRequest(rejectedPair.Ticket), JsonOptions), origin);
                Expect((await ReadJsonAsync<MobilePairPollResponse>(rejectedPoll)).State == "rejected");
            }
            using (var restoresRequest = AuthorizedGet("/api/classroom/restores", accessToken))
            using (var restoresResponse = await client.SendAsync(restoresRequest))
            {
                var restores = await ReadJsonAsync<MobileClassroomRestoreListResponse>(restoresResponse);
                var payload = await restoresResponse.Content.ReadAsStringAsync();
                Expect(restoresResponse.StatusCode == HttpStatusCode.OK && restores.Count == 1 &&
                       restores.Items.Single() == new MobileClassroomRestoreItem("备份机房", "SAFE-01", "网站") &&
                       !payload.Contains("192.0.2.20", StringComparison.Ordinal));
            }
            using (var unauthorizedRestoresRequest = AuthorizedGet("/api/classroom/restores", "invalid-token"))
            using (var unauthorizedRestoresResponse = await client.SendAsync(unauthorizedRestoresRequest))
                Expect(unauthorizedRestoresResponse.StatusCode == HttpStatusCode.Unauthorized);
            using (var retryRestoresResponse = await PostAuthorizedJsonAsync(client,
                       "/api/classroom/restores/retry", "{}", origin, accessToken))
                Expect(retryRestoresResponse.StatusCode == (OperatingSystem.IsWindows()
                    ? HttpStatusCode.Conflict : HttpStatusCode.NotImplemented));
            var seatLayoutPath = Path.Combine(directory, "classroom-seat-layouts.json");
            File.WriteAllText(seatLayoutPath, "{\"schemaVersion\":1,\"schemaVersion\":1,\"layouts\":[]}");
            using (var corruptSeatRequest = AuthorizedGet("/api/session", accessToken))
            using (var corruptSeatResponse = await client.SendAsync(corruptSeatRequest))
            {
                var session = await ReadJsonAsync<MobileSessionResponse>(corruptSeatResponse);
                Expect(corruptSeatResponse.StatusCode == HttpStatusCode.OK &&
                       session.ActiveClassroomTargets.SequenceEqual([eventTarget], StringComparer.OrdinalIgnoreCase) &&
                       session.ActiveClassroomSeatLocations is { Count: 0 });
            }
            File.Delete(seatLayoutPath);
            using (var sessionRequest = AuthorizedGet("/api/session", accessToken))
            using (var sessionResponse = await client.SendAsync(sessionRequest))
            {
                var session = await ReadJsonAsync<MobileSessionResponse>(sessionResponse);
                Expect(sessionResponse.StatusCode == HttpStatusCode.OK &&
                       session.ActiveClassroomTargets.SequenceEqual([eventTarget], StringComparer.OrdinalIgnoreCase) &&
                       session.ClassroomMode == "normal" &&
                       session.ActiveClassroomSessionId == eventSessionId &&
                       session.ActiveClassroomSeatLocations is { Count: 1 } seatLocations &&
                       seatLocations[0] == new ClassroomSeatLocation(eventTarget, 1, 1) &&
                       session.ActiveClassroomCountdown is { } activeCountdown &&
                       activeCountdown.DeadlineUtc > DateTimeOffset.UtcNow &&
                       session.ActiveClassroomTaskProgress is { CompletedCount: 1, TotalCount: 2 } taskProgress &&
                       taskProgress.Tasks[0] == new MobileClassroomTaskProgressItem("阅读题目", true) &&
                       taskProgress.Tasks[1] == new MobileClassroomTaskProgressItem("完成练习", false));
            }

            var staleTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5)
                .ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            using (var skewedRequest = AuthorizedGet("/api/session", accessToken,
                       timestamp: staleTimestamp))
            using (var skewedResponse = await client.SendAsync(skewedRequest))
            {
                Expect(skewedResponse.StatusCode == HttpStatusCode.Unauthorized &&
                       skewedResponse.Headers.Contains("X-Veyon-Server-Time"));
                var serverTime = DateTimeOffset.Parse(skewedResponse.Headers.GetValues("X-Veyon-Server-Time").Single(),
                    System.Globalization.CultureInfo.InvariantCulture);
                using var resynchronizedRequest = AuthorizedGet("/api/session", accessToken,
                    timestamp: serverTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                using var resynchronizedResponse = await client.SendAsync(resynchronizedRequest);
                Expect(resynchronizedResponse.StatusCode == HttpStatusCode.OK);
            }

            using var previewStartResponse = await PostAuthorizedJsonAsync(client,
                "/api/classroom/screen-preview/start", "{}", origin, accessToken);
            var previewSession = await ReadJsonAsync<MobileScreenPreviewSessionResponse>(previewStartResponse);
            Expect(previewStartResponse.StatusCode == HttpStatusCode.OK && previewSession.SessionId == eventSessionId &&
                   previewSession.Targets.SequenceEqual([eventTarget], StringComparer.OrdinalIgnoreCase) &&
                   previewSession.TotalTargets == 1 && previewSession.LeaseId != Guid.Empty);
            var previewPath = "/api/classroom/screen-preview/" + Uri.EscapeDataString(eventTarget);
            using (var noLease = AuthorizedGet(previewPath, accessToken))
            using (var noLeaseResponse = await client.SendAsync(noLease))
                Expect(noLeaseResponse.StatusCode == HttpStatusCode.Unauthorized && screenCaptureCount == 0);
            using (var outsideTarget = AuthorizedGet("/api/classroom/screen-preview/192.0.2.80", accessToken))
            {
                outsideTarget.Headers.TryAddWithoutValidation("X-Veyon-Screen-Preview-Lease",
                    previewSession.LeaseId.ToString("N"));
                using var outsideTargetResponse = await client.SendAsync(outsideTarget);
                Expect(outsideTargetResponse.StatusCode == HttpStatusCode.Unauthorized && screenCaptureCount == 0);
            }
            using (var previewRequest = AuthorizedGet(previewPath, accessToken))
            {
                previewRequest.Headers.TryAddWithoutValidation("X-Veyon-Screen-Preview-Lease",
                    previewSession.LeaseId.ToString("N"));
                using var previewResponse = await client.SendAsync(previewRequest);
                var previewBytes = await previewResponse.Content.ReadAsByteArrayAsync();
                Expect(previewResponse.StatusCode == HttpStatusCode.OK &&
                       previewResponse.Content.Headers.ContentType?.MediaType == "image/png" &&
                       previewResponse.Headers.CacheControl?.NoStore == true &&
                       previewResponse.Headers.TryGetValues("X-Screen-Frame", out var frameHeaders) &&
                       frameHeaders.Single() == "captured" && previewBytes.SequenceEqual(fakeScreenPng) &&
                       screenCaptureCount == 1);
            }
            using (var cachedPreviewRequest = AuthorizedGet(previewPath, accessToken))
            {
                cachedPreviewRequest.Headers.TryAddWithoutValidation("X-Veyon-Screen-Preview-Lease",
                    previewSession.LeaseId.ToString("N"));
                using var cachedPreviewResponse = await client.SendAsync(cachedPreviewRequest);
                Expect(cachedPreviewResponse.StatusCode == HttpStatusCode.OK &&
                       cachedPreviewResponse.Headers.TryGetValues("X-Screen-Frame", out var frameHeaders) &&
                       frameHeaders.Single() == "cached" && screenCaptureCount == 1);
            }
            var stopPreviewJson = JsonSerializer.Serialize(
                new MobileScreenPreviewStopRequest(previewSession.LeaseId), JsonOptions);
            using (var stopPreviewResponse = await PostAuthorizedJsonAsync(client,
                       "/api/classroom/screen-preview/stop", stopPreviewJson, origin, accessToken))
                Expect(stopPreviewResponse.StatusCode == HttpStatusCode.OK);
            using (var stoppedPreviewRequest = AuthorizedGet(previewPath, accessToken))
            {
                stoppedPreviewRequest.Headers.TryAddWithoutValidation("X-Veyon-Screen-Preview-Lease",
                    previewSession.LeaseId.ToString("N"));
                using var stoppedPreviewResponse = await client.SendAsync(stoppedPreviewRequest);
                Expect(stoppedPreviewResponse.StatusCode == HttpStatusCode.Unauthorized);
            }
            var previewAudit = MobileControlAuditStore.Read(directory);
            Expect(previewAudit.Count(item => item.Action == "classroom-screen-preview-start") == 1 &&
                   previewAudit.Count(item => item.Action == "classroom-screen-preview-stop") == 1 &&
                   previewAudit.Where(item => item.Action.StartsWith("classroom-screen-preview", StringComparison.Ordinal))
                       .All(item => item.Targets.SequenceEqual([eventTarget], StringComparer.OrdinalIgnoreCase)));

            var countdownPath = Path.Combine(directory, "classroom-countdown.json");
            File.WriteAllText(countdownPath, "{\"schemaVersion\":1,\"schemaVersion\":1}");
            using (var corruptCountdownRequest = AuthorizedGet("/api/session", accessToken))
            using (var corruptCountdownResponse = await client.SendAsync(corruptCountdownRequest))
            {
                var session = await ReadJsonAsync<MobileSessionResponse>(corruptCountdownResponse);
                Expect(corruptCountdownResponse.StatusCode == HttpStatusCode.OK &&
                       session.ActiveClassroomTargets.SequenceEqual([eventTarget], StringComparer.OrdinalIgnoreCase) &&
                       session.ActiveClassroomCountdown is null &&
                       session.ActiveClassroomSeatLocations is { Count: 1 });
            }
            File.Delete(countdownPath);

            var taskProgressPath = Path.Combine(directory, "classroom-task-progress.json");
            File.WriteAllText(taskProgressPath, "{\"schemaVersion\":1,\"schemaVersion\":1}");
            using (var corruptTaskProgressRequest = AuthorizedGet("/api/session", accessToken))
            using (var corruptTaskProgressResponse = await client.SendAsync(corruptTaskProgressRequest))
            {
                var session = await ReadJsonAsync<MobileSessionResponse>(corruptTaskProgressResponse);
                Expect(corruptTaskProgressResponse.StatusCode == HttpStatusCode.OK &&
                       session.ActiveClassroomTargets.SequenceEqual([eventTarget], StringComparer.OrdinalIgnoreCase) &&
                       session.ActiveClassroomTaskProgress is null);
            }
            File.Delete(taskProgressPath);

            var signedGrant = service.CreateStudentEventGrant("demo", eventSessionId, eventTarget,
                IPAddress.Parse("192.168.1.10"), teacherSigningKey, DateTimeOffset.UtcNow);
            var grant = ClassroomEventCryptography.VerifyGrant(signedGrant, "demo", teacherPublicKeyPem,
                eventSessionId, eventTarget, DateTimeOffset.UtcNow);
            Expect(grant.Target == eventTarget && grant.TeacherEndpoint == "https://192.168.1.10:39176/" &&
                   grant.ServerCertificateSha256 == Convert.ToHexString(SHA256.HashData(identity.Server.RawData)));

            var helpEventTime = DateTimeOffset.UtcNow;
            var helpEvent = new ClassroomEvent(1, ClassroomEventCryptography.EventPurpose, "demo", eventSessionId,
                Guid.NewGuid(), eventTarget, ClassroomEventSender.Student, ClassroomEventType.HelpRequested,
                helpEventTime, helpEventTime.Add(ClassroomEventCryptography.MaximumEventLifetime),
                ClassroomHelpReason.NeedHelp, null, null);
            var signedHelpEvent = ClassroomEventCryptography.SignEvent(helpEvent, agentSigningKey);
            using (var submitted = await PostStudentEventAsync(client, origin, grant.AccessToken, signedHelpEvent))
            {
                var result = await ReadJsonAsync<MobileStudentEventSubmitResponse>(submitted);
                Expect(submitted.StatusCode == HttpStatusCode.OK && result.Accepted && !result.Duplicate);
            }

            var prematureResolutionTime = DateTimeOffset.UtcNow;
            var prematureResolution = new ClassroomEvent(1, ClassroomEventCryptography.EventPurpose, "demo",
                eventSessionId, Guid.NewGuid(), eventTarget, ClassroomEventSender.Student,
                ClassroomEventType.HelpResolved, prematureResolutionTime,
                prematureResolutionTime.Add(ClassroomEventCryptography.MaximumEventLifetime), null, null,
                helpEvent.EventId);
            using (var unresolved = await PostStudentEventAsync(client, origin, grant.AccessToken,
                       ClassroomEventCryptography.SignEvent(prematureResolution, agentSigningKey)))
                Expect(unresolved.StatusCode == HttpStatusCode.BadRequest);

            using (var impostor = RSA.Create(2048))
            using (var rejectedStudentEvent = await PostStudentEventAsync(client, origin, grant.AccessToken,
                       ClassroomEventCryptography.SignEvent(helpEvent, impostor)))
                Expect(rejectedStudentEvent.StatusCode == HttpStatusCode.BadRequest);

            using (var mobileEventsRequest = AuthorizedGet("/api/classroom/events?after=0", accessToken))
            using (var mobileEventsResponse = await client.SendAsync(mobileEventsRequest))
            {
                var page = await ReadJsonAsync<MobileClassroomEventPage>(mobileEventsResponse);
                Expect(mobileEventsResponse.StatusCode == HttpStatusCode.OK && page.Events.Count == 1 &&
                       page.SessionId == eventSessionId);
                var verified = ClassroomEventCryptography.VerifyEvent(page.Events[0], "demo", eventSessionId,
                    eventTarget, ClassroomEventSender.Student, agentPublicKeyPem, DateTimeOffset.UtcNow);
                Expect(verified.Event.EventId == helpEvent.EventId && verified.MatchesPinnedKey);
            }

            var replyJson = JsonSerializer.Serialize(
                new { helpEventId = helpEvent.EventId, message = "老师马上来。" }, JsonOptions);
            using (var reply = await PostAuthorizedJsonAsync(client, "/api/classroom/events/reply",
                       replyJson, origin, accessToken))
            {
                var result = await ReadJsonAsync<MobileStudentEventSubmitResponse>(reply);
                Expect(reply.StatusCode == HttpStatusCode.OK && result.Accepted && !result.Duplicate);
            }
            using (var duplicateReply = await PostAuthorizedJsonAsync(client, "/api/classroom/events/reply",
                       replyJson, origin, accessToken))
            {
                var result = await ReadJsonAsync<MobileStudentEventSubmitResponse>(duplicateReply);
                Expect(duplicateReply.StatusCode == HttpStatusCode.OK && result.Accepted && result.Duplicate);
            }

            var resolutionTime = DateTimeOffset.UtcNow;
            var resolvedEvent = prematureResolution with
            {
                EventId = Guid.NewGuid(),
                IssuedUtc = resolutionTime,
                ExpiresUtc = resolutionTime.Add(ClassroomEventCryptography.MaximumEventLifetime)
            };
            using (var resolved = await PostStudentEventAsync(client, origin, grant.AccessToken,
                       ClassroomEventCryptography.SignEvent(resolvedEvent, agentSigningKey)))
            {
                var result = await ReadJsonAsync<MobileStudentEventSubmitResponse>(resolved);
                Expect(resolved.StatusCode == HttpStatusCode.OK && result.Accepted && !result.Duplicate);
            }
            var repeatedResolutionTime = DateTimeOffset.UtcNow;
            var repeatedResolution = resolvedEvent with
            {
                EventId = Guid.NewGuid(),
                IssuedUtc = repeatedResolutionTime,
                ExpiresUtc = repeatedResolutionTime.Add(ClassroomEventCryptography.MaximumEventLifetime)
            };
            using (var duplicate = await PostStudentEventAsync(client, origin, grant.AccessToken,
                       ClassroomEventCryptography.SignEvent(repeatedResolution, agentSigningKey)))
            {
                var result = await ReadJsonAsync<MobileStudentEventSubmitResponse>(duplicate);
                Expect(duplicate.StatusCode == HttpStatusCode.OK && result.Accepted && result.Duplicate);
            }

            using (var mobileResolutionRequest = AuthorizedGet("/api/classroom/events?after=1", accessToken))
            using (var mobileResolutionResponse = await client.SendAsync(mobileResolutionRequest))
            {
                var page = await ReadJsonAsync<MobileClassroomEventPage>(mobileResolutionResponse);
                Expect(mobileResolutionResponse.StatusCode == HttpStatusCode.OK && page.Events.Count == 2 &&
                       page.SessionId == eventSessionId);
                var verifiedTeacherReply = ClassroomEventCryptography.VerifyEvent(page.Events[0], "demo",
                    eventSessionId, eventTarget, ClassroomEventSender.Teacher, teacherPublicKeyPem,
                    DateTimeOffset.UtcNow);
                var verifiedResolution = ClassroomEventCryptography.VerifyEvent(page.Events[1], "demo",
                    eventSessionId, eventTarget, ClassroomEventSender.Student, agentPublicKeyPem,
                    DateTimeOffset.UtcNow);
                Expect(verifiedTeacherReply.Event.Type == ClassroomEventType.TeacherReply &&
                       verifiedResolution.Event.Type == ClassroomEventType.HelpResolved &&
                       verifiedResolution.Event.CorrelationId == helpEvent.EventId &&
                       verifiedResolution.MatchesPinnedKey);
            }

            for (var duplicate = 0; duplicate < 5; duplicate++)
            {
                using var duplicateResponse = await PostStudentEventAsync(client, origin, grant.AccessToken,
                    signedHelpEvent);
                var result = await ReadJsonAsync<MobileStudentEventSubmitResponse>(duplicateResponse);
                Expect(duplicateResponse.StatusCode == HttpStatusCode.OK && result.Accepted && result.Duplicate);
            }
            using (var rateLimited = await PostStudentEventAsync(client, origin, grant.AccessToken, signedHelpEvent))
                Expect(rateLimited.StatusCode == HttpStatusCode.TooManyRequests);

            using (var studentEventsRequest = new HttpRequestMessage(HttpMethod.Get,
                       "/api/classroom/events/student?after=0"))
            {
                studentEventsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.AccessToken);
                using var studentEventsResponse = await client.SendAsync(studentEventsRequest);
                var page = await ReadJsonAsync<MobileClassroomEventPage>(studentEventsResponse);
                Expect(studentEventsResponse.StatusCode == HttpStatusCode.OK && page.Events.Count == 3 &&
                       page.SessionId is null);
                var verifiedStudentEvent = ClassroomEventCryptography.VerifyEvent(page.Events[0], "demo",
                    eventSessionId, eventTarget, ClassroomEventSender.Student, agentPublicKeyPem,
                    DateTimeOffset.UtcNow);
                var verifiedTeacherReply = ClassroomEventCryptography.VerifyEvent(page.Events[1], "demo",
                    eventSessionId, eventTarget, ClassroomEventSender.Teacher, teacherPublicKeyPem,
                    DateTimeOffset.UtcNow);
                var verifiedResolution = ClassroomEventCryptography.VerifyEvent(page.Events[2], "demo",
                    eventSessionId, eventTarget, ClassroomEventSender.Student, agentPublicKeyPem,
                    DateTimeOffset.UtcNow);
                Expect(verifiedStudentEvent.Event.EventId == helpEvent.EventId &&
                       verifiedTeacherReply.Event.Type == ClassroomEventType.TeacherReply &&
                       verifiedTeacherReply.Event.CorrelationId == helpEvent.EventId &&
                       verifiedResolution.Event.Type == ClassroomEventType.HelpResolved &&
                       verifiedResolution.Event.CorrelationId == helpEvent.EventId);
            }

            var invalidNoticeJson = JsonSerializer.Serialize(new { message = "   " }, JsonOptions);
            using (var invalidNotice = await PostAuthorizedJsonAsync(client, "/api/classroom/events/notice",
                       invalidNoticeJson, origin, accessToken))
                Expect(invalidNotice.StatusCode == HttpStatusCode.BadRequest);
            var noticeJson = JsonSerializer.Serialize(new { message = "请在两分钟内保存作业。" }, JsonOptions);
            using (var classroomNoticeResponse = await PostAuthorizedJsonAsync(client,
                       "/api/classroom/events/notice", noticeJson, origin, accessToken))
            {
                var result = await ReadJsonAsync<MobileClassroomNoticeResponse>(classroomNoticeResponse);
                Expect(classroomNoticeResponse.StatusCode == HttpStatusCode.OK && result.Accepted &&
                       result.TargetCount == 1);
            }
            using (var rateLimitedNotice = await PostAuthorizedJsonAsync(client,
                       "/api/classroom/events/notice", noticeJson, origin, accessToken))
                Expect(rateLimitedNotice.StatusCode == HttpStatusCode.TooManyRequests);

            using (var mobileNoticeRequest = AuthorizedGet("/api/classroom/events?after=3", accessToken))
            using (var mobileNoticeResponse = await client.SendAsync(mobileNoticeRequest))
            {
                var page = await ReadJsonAsync<MobileClassroomEventPage>(mobileNoticeResponse);
                Expect(mobileNoticeResponse.StatusCode == HttpStatusCode.OK && page.Events.Count == 1 &&
                       page.SessionId == eventSessionId);
                var verifiedNotice = ClassroomEventCryptography.VerifyEvent(page.Events[0], "demo",
                    eventSessionId, eventTarget, ClassroomEventSender.Teacher, teacherPublicKeyPem,
                    DateTimeOffset.UtcNow);
                Expect(verifiedNotice.Event.Type == ClassroomEventType.ClassroomNotice &&
                       verifiedNotice.Event.Target == ClassroomEventCryptography.ClassroomNoticeTarget &&
                       verifiedNotice.Event.Message == "请在两分钟内保存作业。");
            }
            using (var studentNoticeRequest = new HttpRequestMessage(HttpMethod.Get,
                       "/api/classroom/events/student?after=3"))
            {
                studentNoticeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.AccessToken);
                using var studentNoticeResponse = await client.SendAsync(studentNoticeRequest);
                var page = await ReadJsonAsync<MobileClassroomEventPage>(studentNoticeResponse);
                Expect(studentNoticeResponse.StatusCode == HttpStatusCode.OK && page.Events.Count == 1 &&
                       page.SessionId is null);
                var verifiedNotice = ClassroomEventCryptography.VerifyEvent(page.Events[0], "demo",
                    eventSessionId, eventTarget, ClassroomEventSender.Teacher, teacherPublicKeyPem,
                    DateTimeOffset.UtcNow);
                Expect(verifiedNotice.Event.Target == ClassroomEventCryptography.ClassroomNoticeTarget &&
                       verifiedNotice.Event.Message == "请在两分钟内保存作业。");
            }

            Expect(agentTrustStore.Remove("demo", eventTarget, agentFingerprint));
            using (var revokedStudentGrant = new HttpRequestMessage(HttpMethod.Get,
                       "/api/classroom/events/student?after=0"))
            {
                revokedStudentGrant.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.AccessToken);
                using var revokedGrantResponse = await client.SendAsync(revokedStudentGrant);
                Expect(revokedGrantResponse.StatusCode == HttpStatusCode.Unauthorized);
            }

            var stressRoom = new TeacherRoomProfile(Guid.NewGuid(), "并发验收机房", "LOAD-", 1, 24);
            var stressCampus = new TeacherCampusProfile(Guid.NewGuid(), "demo", [stressRoom]);
            var stressSession = ClassroomSession.Start(stressCampus, stressRoom.RoomId, DateTimeOffset.UtcNow);
            var stressTargets = stressSession.Targets.Select(target => target.DeviceLabel).ToArray();
            Expect(stressTargets.Length == 24);
            service.SetClassroomSession("demo", stressSession.SessionId, stressTargets,
                stressSession.Targets.ToDictionary(target => target.DeviceLabel, target => target.TargetId,
                    StringComparer.OrdinalIgnoreCase), ClassroomMode.Normal, stressSession);

            var stressAgents = new List<ClassroomLoadAgent>(stressTargets.Length);
            foreach (var target in stressTargets)
            {
                using var agentKey = RSA.Create(2048);
                var loadAgentPublicKeyPem = agentKey.ExportSubjectPublicKeyInfoPem();
                var loadAgentFingerprint = StudentAgentResponseCryptography.GetFingerprint(loadAgentPublicKeyPem);
                agentTrustStore.Pin(new StudentAgentIdentityTrustCandidate(target, "demo", loadAgentPublicKeyPem,
                    loadAgentFingerprint));
                var signedStressGrant = service.CreateStudentEventGrant("demo", stressSession.SessionId, target,
                    IPAddress.Parse("192.168.1.10"), teacherSigningKey, DateTimeOffset.UtcNow);
                var stressGrant = ClassroomEventCryptography.VerifyGrant(signedStressGrant, "demo",
                    teacherPublicKeyPem, stressSession.SessionId, target, DateTimeOffset.UtcNow);
                var firstEventId = Guid.NewGuid();
                var firstEventTime = DateTimeOffset.UtcNow;
                var firstEvent = new ClassroomEvent(1, ClassroomEventCryptography.EventPurpose, "demo",
                    stressSession.SessionId, firstEventId, target, ClassroomEventSender.Student,
                    ClassroomEventType.HelpRequested, firstEventTime,
                    firstEventTime.Add(ClassroomEventCryptography.MaximumEventLifetime),
                    ClassroomHelpReason.NeedHelp, null, null);
                var secondEventId = Guid.NewGuid();
                var secondEventTime = DateTimeOffset.UtcNow;
                var secondEvent = firstEvent with
                {
                    EventId = secondEventId,
                    IssuedUtc = secondEventTime,
                    ExpiresUtc = secondEventTime.Add(ClassroomEventCryptography.MaximumEventLifetime)
                };
                stressAgents.Add(new ClassroomLoadAgent(target, loadAgentPublicKeyPem, stressGrant.AccessToken,
                    ClassroomEventCryptography.SignEvent(firstEvent, agentKey), firstEventId,
                    ClassroomEventCryptography.SignEvent(secondEvent, agentKey), secondEventId));
            }

            using (var disconnected = new CancellationTokenSource())
            {
                var interruptedPolls = stressAgents.Select(agent =>
                    GetStudentEventsAsync(client, agent.AccessToken, 0, disconnected.Token)).ToArray();
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                disconnected.Cancel();
                var interrupted = await Task.WhenAll(interruptedPolls.Select(ObserveCancellationAsync))
                    .WaitAsync(TimeSpan.FromSeconds(3));
                Expect(interrupted.All(wasCancelled => wasCancelled));
            }

            var firstRoundClock = Stopwatch.StartNew();
            var firstPolls = stressAgents.Select(agent =>
                GetStudentEventsAsync(client, agent.AccessToken, 0, CancellationToken.None)).ToArray();
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            var firstSubmissionLatencies = await Task.WhenAll(stressAgents.Select(async agent =>
            {
                var requestClock = Stopwatch.StartNew();
                using var submission = await PostStudentEventAsync(client, origin, agent.AccessToken,
                    agent.FirstEventEnvelope);
                var result = await ReadJsonAsync<MobileStudentEventSubmitResponse>(submission);
                Expect(submission.StatusCode == HttpStatusCode.OK && result.Accepted && !result.Duplicate);
                return requestClock.Elapsed.TotalMilliseconds;
            }));
            var firstCursors = new long[stressAgents.Count];
            var firstPages = await Task.WhenAll(firstPolls).WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < stressAgents.Count; index++)
            {
                using var response = firstPages[index];
                var agent = stressAgents[index];
                var page = await ReadJsonAsync<MobileClassroomEventPage>(response);
                Expect(response.StatusCode == HttpStatusCode.OK && page.Events.Count == 1 &&
                       page.SessionId is null && page.Cursor > 0);
                var delivered = ClassroomEventCryptography.VerifyEvent(page.Events.Single(), "demo",
                    stressSession.SessionId, agent.Target, ClassroomEventSender.Student,
                    agent.PublicKeyPem, DateTimeOffset.UtcNow);
                Expect(delivered.Event.EventId == agent.FirstEventId && delivered.Event.Target == agent.Target &&
                       delivered.MatchesPinnedKey);
                firstCursors[index] = page.Cursor;
            }
            firstRoundClock.Stop();
            WriteLoadMeasurement(CreateLoadMeasurement(24, firstRoundClock.Elapsed, firstSubmissionLatencies));

            var secondPolls = stressAgents.Select((agent, index) =>
                GetStudentEventsAsync(client, agent.AccessToken, firstCursors[index], CancellationToken.None))
                .ToArray();
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            var secondSubmissions = await Task.WhenAll(stressAgents.Select(agent =>
                PostStudentEventAsync(client, origin, agent.AccessToken, agent.SecondEventEnvelope)));
            foreach (var submission in secondSubmissions)
            {
                using (submission)
                {
                    var result = await ReadJsonAsync<MobileStudentEventSubmitResponse>(submission);
                    Expect(submission.StatusCode == HttpStatusCode.OK && result.Accepted && !result.Duplicate);
                }
            }

            var secondCursors = new long[stressAgents.Count];
            var secondPages = await Task.WhenAll(secondPolls).WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < stressAgents.Count; index++)
            {
                using var response = secondPages[index];
                var agent = stressAgents[index];
                var page = await ReadJsonAsync<MobileClassroomEventPage>(response);
                Expect(response.StatusCode == HttpStatusCode.OK && page.Events.Count == 1 &&
                       page.Cursor > firstCursors[index]);
                var delivered = ClassroomEventCryptography.VerifyEvent(page.Events.Single(), "demo",
                    stressSession.SessionId, agent.Target, ClassroomEventSender.Student,
                    agent.PublicKeyPem, DateTimeOffset.UtcNow);
                Expect(delivered.Event.EventId == agent.SecondEventId && delivered.Event.Target == agent.Target &&
                       delivered.MatchesPinnedKey);
                secondCursors[index] = page.Cursor;
            }

            using (var allClassEventsRequest = AuthorizedGet("/api/classroom/events?after=0", accessToken))
            using (var allClassEventsResponse = await client.SendAsync(allClassEventsRequest,
                       HttpCompletionOption.ResponseHeadersRead))
            {
                var page = await ReadJsonAsync<MobileClassroomEventPage>(allClassEventsResponse);
                Expect(allClassEventsResponse.StatusCode == HttpStatusCode.OK && page.Events.Count == 48 &&
                       page.SessionId == stressSession.SessionId);
                var eventIds = new HashSet<Guid>();
                foreach (var signedEvent in page.Events)
                {
                    var envelope = JsonDocument.Parse(signedEvent);
                    using (envelope)
                    {
                        var payload = envelope.RootElement.GetProperty("payload").GetString()
                                      ?? throw new InvalidDataException("并发课堂事件签名正文为空。");
                        using var eventJson = JsonDocument.Parse(Convert.FromBase64String(payload));
                        var target = eventJson.RootElement.GetProperty("target").GetString()
                                     ?? throw new InvalidDataException("并发课堂事件目标为空。");
                        var agent = stressAgents.Single(item => item.Target == target);
                        var verified = ClassroomEventCryptography.VerifyEvent(signedEvent, "demo",
                            stressSession.SessionId, target, ClassroomEventSender.Student,
                            agent.PublicKeyPem, DateTimeOffset.UtcNow);
                        Expect(verified.MatchesPinnedKey && eventIds.Add(verified.Event.EventId));
                    }
                }
                Expect(eventIds.Count == 48);
            }

            using var stressPreviewStartResponse = await PostAuthorizedJsonAsync(client,
                "/api/classroom/screen-preview/start", "{}", origin, accessToken);
            var stressPreview = await ReadJsonAsync<MobileScreenPreviewSessionResponse>(stressPreviewStartResponse);
            Expect(stressPreviewStartResponse.StatusCode == HttpStatusCode.OK &&
                   stressPreview.SessionId == stressSession.SessionId && stressPreview.TotalTargets == 24 &&
                   stressPreview.Targets.Count == ClassroomScreenPreviewCapture.MaximumPreviewTargets);
            var endedStudentPolls = stressAgents.Select((agent, index) =>
                GetStudentEventsAsync(client, agent.AccessToken, secondCursors[index], CancellationToken.None))
                .ToArray();
            using var pendingMobilePollRequest = AuthorizedGet("/api/classroom/events?after=999", accessToken);
            var pendingMobilePoll = client.SendAsync(pendingMobilePollRequest,
                HttpCompletionOption.ResponseHeadersRead);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            service.SetClassroomSession(null, null, null);
            using (var revokedPreviewRequest = AuthorizedGet(
                       "/api/classroom/screen-preview/" + Uri.EscapeDataString(stressPreview.Targets[0]), accessToken))
            {
                revokedPreviewRequest.Headers.TryAddWithoutValidation("X-Veyon-Screen-Preview-Lease",
                    stressPreview.LeaseId.ToString("N"));
                using var revokedPreviewResponse = await client.SendAsync(revokedPreviewRequest);
                Expect(revokedPreviewResponse.StatusCode == HttpStatusCode.Unauthorized);
            }
            var endedStudentResponses = await Task.WhenAll(endedStudentPolls)
                .WaitAsync(TimeSpan.FromSeconds(3));
            foreach (var response in endedStudentResponses)
            {
                using (response) Expect(response.StatusCode == HttpStatusCode.Unauthorized);
            }
            using (var endedSessionResponse = await pendingMobilePoll.WaitAsync(TimeSpan.FromSeconds(3)))
            {
                if (endedSessionResponse.StatusCode == HttpStatusCode.OK)
                {
                    var endedPage = await ReadJsonAsync<MobileClassroomEventPage>(endedSessionResponse);
                    Expect(endedPage.SessionId is null && endedPage.Events.Count == 0);
                }
                else Expect(endedSessionResponse.StatusCode == HttpStatusCode.Unauthorized);
            }

            foreach (var targetCount in new[] { 10, 70 })
                await RunScaleScenarioAsync(targetCount, service, agentTrustStore, client, origin,
                    teacherSigningKey, teacherPublicKeyPem, accessToken);

            using (var noClassRequest = AuthorizedGet("/api/classroom/events?after=0", accessToken))
            using (var noClassResponse = await client.SendAsync(noClassRequest))
            {
                var page = await ReadJsonAsync<MobileClassroomEventPage>(noClassResponse);
                Expect(noClassResponse.StatusCode == HttpStatusCode.OK && page.SessionId is null &&
                       page.Events.Count == 0 && page.Cursor == 0);
            }
            using (var noClassSessionRequest = AuthorizedGet("/api/session", accessToken))
            using (var noClassSessionResponse = await client.SendAsync(noClassSessionRequest))
            {
                var session = await ReadJsonAsync<MobileSessionResponse>(noClassSessionResponse);
                Expect(noClassSessionResponse.StatusCode == HttpStatusCode.OK &&
                       session.ActiveClassroomTargets.Count == 0 &&
                       session.ActiveClassroomSeatLocations is { Count: 0 } &&
                       session.ActiveClassroomCountdown is null);
            }
            using (var endedClassNotice = await PostAuthorizedJsonAsync(client, "/api/classroom/events/notice",
                       noticeJson, origin, accessToken))
                Expect(endedClassNotice.StatusCode == HttpStatusCode.Unauthorized);

            using var profilesRequest = AuthorizedGet("/api/profiles", accessToken);
            using var profilesResponse = await client.SendAsync(profilesRequest);
            using var profiles = await JsonDocument.ParseAsync(await profilesResponse.Content.ReadAsStreamAsync());
            var systemSummary = profiles.RootElement.EnumerateArray().Single(element =>
                element.GetProperty("kind").GetString() == "system");
            Expect(profilesResponse.StatusCode == HttpStatusCode.OK && profiles.RootElement.GetArrayLength() == 2 &&
                   profiles.RootElement.EnumerateArray().All(element => element.GetProperty("campusId").GetString() == "demo") &&
                   systemSummary.GetProperty("mode").GetString() == "长期基线" &&
                   systemSummary.GetProperty("lifetimeMinutes").GetInt32() == 0 &&
                   systemSummary.GetProperty("systemSettings").GetProperty("lockWallpaper").GetBoolean() &&
                   !systemSummary.TryGetProperty("studentSids", out _));

            var expectedProtectedStatus = OperatingSystem.IsWindows()
                ? HttpStatusCode.Unauthorized
                : HttpStatusCode.NotImplemented;
            using (var unauthenticatedStatus = await PostJsonAsync(client, "/api/status", "{}", origin))
                Expect(unauthenticatedStatus.StatusCode == expectedProtectedStatus);
            using (var unauthenticatedPolicy = await PostJsonAsync(client, "/api/policy", "{}", origin))
                Expect(unauthenticatedPolicy.StatusCode == expectedProtectedStatus);

            var replayableRequest = AuthorizedGet("/api/session", accessToken);
            var nonce = replayableRequest.Headers.GetValues("X-Veyon-Request-Nonce").Single();
            var timestamp = replayableRequest.Headers.GetValues("X-Veyon-Request-Timestamp").Single();
            using (replayableRequest)
            using (var session = await client.SendAsync(replayableRequest))
                Expect(session.StatusCode == HttpStatusCode.OK);
            using (var replay = AuthorizedGet("/api/session", accessToken, nonce, timestamp))
            using (var replayResponse = await client.SendAsync(replay))
                Expect(replayResponse.StatusCode == HttpStatusCode.Unauthorized);

            var browserTimestamp = DateTimeOffset.UtcNow.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
            Expect(!DateTimeOffset.TryParseExact(browserTimestamp, "O",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out _));
            using (var browserTimestampRequest = AuthorizedGet("/api/session", accessToken,
                       timestamp: browserTimestamp))
            using (var browserTimestampResponse = await client.SendAsync(browserTimestampRequest))
                Expect(browserTimestampResponse.StatusCode == HttpStatusCode.OK);

            var previewRateLimitEnforced = false;
            for (var index = 0; index < 201; index++)
            {
                using var rateLimitedRequest = AuthorizedGet(previewPath, accessToken);
                using var rateLimitedResponse = await client.SendAsync(rateLimitedRequest);
                if (rateLimitedResponse.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    previewRateLimitEnforced = true;
                    break;
                }
                if (rateLimitedResponse.StatusCode != HttpStatusCode.Unauthorized &&
                    rateLimitedResponse.StatusCode != HttpStatusCode.OK)
                    break;
            }
            Expect(previewRateLimitEnforced);

            MobilePairedDeviceStore.Revoke(deviceId, directory);
            using (var revoked = AuthorizedGet("/api/session", accessToken))
            using (var revokedResponse = await client.SendAsync(revoked))
                Expect(revokedResponse.StatusCode == HttpStatusCode.Unauthorized);
            Expect(changes >= 2);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private static async Task RunScaleScenarioAsync(int targetCount, TeacherMobileControlService service,
        StudentAgentIdentityTrustStore agentTrustStore, HttpClient client, string origin, RSA teacherSigningKey,
        string teacherPublicKeyPem, string mobileAccessToken)
    {
        var room = new TeacherRoomProfile(Guid.NewGuid(), $"并发验收机房 {targetCount}",
            $"LOAD{targetCount}-", 1, targetCount);
        var campus = new TeacherCampusProfile(Guid.NewGuid(), "demo", [room]);
        var session = ClassroomSession.Start(campus, room.RoomId, DateTimeOffset.UtcNow);
        var targets = session.Targets.Select(target => target.DeviceLabel).ToArray();
        Expect(targets.Length == targetCount);
        service.SetClassroomSession("demo", session.SessionId, targets,
            session.Targets.ToDictionary(target => target.DeviceLabel, target => target.TargetId,
                StringComparer.OrdinalIgnoreCase), ClassroomMode.Normal, session);

        try
        {
            var agents = new List<ClassroomLoadAgent>(targetCount);
            foreach (var target in targets)
            {
                using var agentKey = RSA.Create(2048);
                var publicKey = agentKey.ExportSubjectPublicKeyInfoPem();
                var fingerprint = StudentAgentResponseCryptography.GetFingerprint(publicKey);
                agentTrustStore.Pin(new StudentAgentIdentityTrustCandidate(target, "demo", publicKey, fingerprint));
                var signedGrant = service.CreateStudentEventGrant("demo", session.SessionId, target,
                    IPAddress.Parse("192.168.1.10"), teacherSigningKey, DateTimeOffset.UtcNow);
                var grant = ClassroomEventCryptography.VerifyGrant(signedGrant, "demo", teacherPublicKeyPem,
                    session.SessionId, target, DateTimeOffset.UtcNow);
                var eventId = Guid.NewGuid();
                var issuedUtc = DateTimeOffset.UtcNow;
                var classroomEvent = new ClassroomEvent(1, ClassroomEventCryptography.EventPurpose, "demo",
                    session.SessionId, eventId, target, ClassroomEventSender.Student,
                    ClassroomEventType.HelpRequested, issuedUtc,
                    issuedUtc.Add(ClassroomEventCryptography.MaximumEventLifetime),
                    ClassroomHelpReason.NeedHelp, null, null);
                agents.Add(new ClassroomLoadAgent(target, publicKey, grant.AccessToken,
                    ClassroomEventCryptography.SignEvent(classroomEvent, agentKey), eventId, "", Guid.Empty));
            }

            var roundClock = Stopwatch.StartNew();
            var pendingStudentPolls = agents.Select(agent =>
                GetStudentEventsAsync(client, agent.AccessToken, 0, CancellationToken.None)).ToArray();
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            var submissionLatencies = await Task.WhenAll(agents.Select(async agent =>
            {
                var requestClock = Stopwatch.StartNew();
                using var response = await PostStudentEventAsync(client, origin, agent.AccessToken,
                    agent.FirstEventEnvelope);
                var result = await ReadJsonAsync<MobileStudentEventSubmitResponse>(response);
                Expect(response.StatusCode == HttpStatusCode.OK && result.Accepted && !result.Duplicate);
                return requestClock.Elapsed.TotalMilliseconds;
            }));

            var studentPages = await Task.WhenAll(pendingStudentPolls).WaitAsync(TimeSpan.FromSeconds(10));
            for (var index = 0; index < agents.Count; index++)
            {
                using var response = studentPages[index];
                var agent = agents[index];
                var page = await ReadJsonAsync<MobileClassroomEventPage>(response);
                Expect(response.StatusCode == HttpStatusCode.OK && page.Events.Count == 1 &&
                       page.SessionId is null && page.Cursor > 0);
                var delivered = ClassroomEventCryptography.VerifyEvent(page.Events.Single(), "demo",
                    session.SessionId, agent.Target, ClassroomEventSender.Student, agent.PublicKeyPem,
                    DateTimeOffset.UtcNow);
                Expect(delivered.Event.EventId == agent.FirstEventId && delivered.MatchesPinnedKey);
            }

            var teacherEvents = new List<string>(targetCount);
            var teacherCursor = 0L;
            while (teacherEvents.Count < targetCount)
            {
                using var teacherEventsRequest = AuthorizedGet(
                    $"/api/classroom/events?after={teacherCursor}", mobileAccessToken);
                using var teacherEventsResponse = await client.SendAsync(teacherEventsRequest,
                    HttpCompletionOption.ResponseHeadersRead);
                var teacherPage = await ReadJsonAsync<MobileClassroomEventPage>(teacherEventsResponse);
                Expect(teacherEventsResponse.StatusCode == HttpStatusCode.OK &&
                       teacherPage.SessionId == session.SessionId && teacherPage.Events.Count > 0);
                teacherEvents.AddRange(teacherPage.Events);
                Expect(teacherPage.Cursor > teacherCursor);
                teacherCursor = teacherPage.Cursor;
            }
            Expect(teacherEvents.Count == targetCount);
            var seenEventIds = new HashSet<Guid>();
            foreach (var envelope in teacherEvents)
            {
                using var json = JsonDocument.Parse(envelope);
                var payload = json.RootElement.GetProperty("payload").GetString()
                              ?? throw new InvalidDataException("并发课堂事件正文为空。");
                using var eventJson = JsonDocument.Parse(Convert.FromBase64String(payload));
                var target = eventJson.RootElement.GetProperty("target").GetString()
                             ?? throw new InvalidDataException("并发课堂事件目标为空。");
                var agent = agents.Single(item => item.Target == target);
                var verified = ClassroomEventCryptography.VerifyEvent(envelope, "demo", session.SessionId,
                    target, ClassroomEventSender.Student, agent.PublicKeyPem, DateTimeOffset.UtcNow);
                Expect(verified.MatchesPinnedKey && seenEventIds.Add(verified.Event.EventId));
            }
            Expect(seenEventIds.Count == targetCount);

            roundClock.Stop();
            WriteLoadMeasurement(CreateLoadMeasurement(targetCount, roundClock.Elapsed, submissionLatencies));
        }
        finally
        {
            service.SetClassroomSession(null, null, null);
        }
    }

    private static ClassroomLoadMeasurement CreateLoadMeasurement(int targetCount, TimeSpan deliveryRoundTrip,
        IReadOnlyCollection<double> submissionLatencies)
    {
        var sorted = submissionLatencies.OrderBy(value => value).ToArray();
        if (sorted.Length != targetCount || sorted.Any(value => !double.IsFinite(value) || value < 0))
            throw new InvalidDataException("并发课堂延迟样本数量或数值无效。");
        return new ClassroomLoadMeasurement("synthetic-local-https-api", targetCount,
            Math.Round(deliveryRoundTrip.TotalMilliseconds, 2),
            Percentile(sorted, 0.50), Percentile(sorted, 0.95), Math.Round(sorted[^1], 2));
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        var index = Math.Clamp((int)Math.Ceiling(percentile * sortedValues.Count) - 1, 0, sortedValues.Count - 1);
        return Math.Round(sortedValues[index], 2);
    }

    private static void WriteLoadMeasurement(ClassroomLoadMeasurement measurement) =>
        Console.WriteLine("CLASSROOM_LOAD_RESULT " + JsonSerializer.Serialize(measurement, JsonOptions));

    private static async Task VerifyScreenPreviewCaptureAsync(byte[] validPng)
    {
        var captureCount = 0;
        var activeCaptures = 0;
        var maximumActiveCaptures = 0;
        var sessionId = Guid.NewGuid();
        var capture = new ClassroomScreenPreviewCapture(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref captureCount);
            var active = Interlocked.Increment(ref activeCaptures);
            while (true)
            {
                var maximum = Volatile.Read(ref maximumActiveCaptures);
                if (active <= maximum || Interlocked.CompareExchange(ref maximumActiveCaptures, active, maximum) == maximum)
                    break;
            }
            try
            {
                await Task.Delay(40, cancellationToken).ConfigureAwait(false);
                return (byte[])validPng.Clone();
            }
            finally { Interlocked.Decrement(ref activeCaptures); }
        });
        capture.SetSession(sessionId);
        var first = await capture.GetAsync(sessionId, "PC-01", CancellationToken.None);
        var cached = await capture.GetAsync(sessionId, "PC-01", CancellationToken.None);
        Expect(first.WasCaptured && !cached.WasCaptured && captureCount == 1 &&
               cached.CapturedUtc == first.CapturedUtc);
        await Task.WhenAll(Enumerable.Range(2, 4).Select(index =>
            capture.GetAsync(sessionId, $"PC-{index:D2}", CancellationToken.None)));
        Expect(maximumActiveCaptures <= 2 && captureCount == 5);

        var tooWide = (byte[])validPng.Clone();
        tooWide[18] = 1;
        tooWide[19] = 65;
        var tooWideSession = Guid.NewGuid();
        var tooWideCapture = new ClassroomScreenPreviewCapture((_, _) => Task.FromResult(tooWide));
        tooWideCapture.SetSession(tooWideSession);
        var rejectedTooWide = false;
        try { await tooWideCapture.GetAsync(tooWideSession, "PC-01", CancellationToken.None); }
        catch (InvalidDataException) { rejectedTooWide = true; }
        Expect(rejectedTooWide);

        var cancellationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationSession = Guid.NewGuid();
        var cancellationCapture = new ClassroomScreenPreviewCapture(async (_, cancellationToken) =>
        {
            cancellationStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return (byte[])validPng.Clone();
        });
        cancellationCapture.SetSession(cancellationSession);
        var pending = cancellationCapture.GetAsync(cancellationSession, "PC-01", CancellationToken.None);
        await cancellationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellationCapture.SetSession(Guid.NewGuid());
        var canceled = false;
        try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { canceled = true; }
        Expect(canceled);
    }

    private static MobileControlTlsIdentity CreateIdentity(IPAddress address)
    {
        using var rootKey = RSA.Create(2048);
        var now = DateTimeOffset.UtcNow;
        var rootRequest = new CertificateRequest("CN=Veyon Campus Mobile API Test CA", rootKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var root = rootRequest.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(2));
        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=127.0.0.1", serverKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var subjectNames = new SubjectAlternativeNameBuilder();
        subjectNames.AddIpAddress(address);
        serverRequest.CertificateExtensions.Add(subjectNames.Build());
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7f;
        using var publicServer = serverRequest.Create(root, now.AddMinutes(-5), now.AddDays(1), serial);
        using var ephemeralServer = publicServer.CopyWithPrivateKey(serverKey);
        var server = MobileControlTlsIdentity.ImportServerCertificateForTls(ephemeralServer);
        var rootBytes = root.Export(X509ContentType.Cert);
        var fingerprint = Convert.ToHexString(SHA256.HashData(rootBytes));
        return new MobileControlTlsIdentity(root, server, rootBytes, fingerprint, [address]);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static HttpRequestMessage AuthorizedGet(string path, string token, string? nonce = null,
        string? timestamp = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-Veyon-Request-Nonce", nonce ?? Guid.NewGuid().ToString("N"));
        request.Headers.TryAddWithoutValidation("X-Veyon-Request-Timestamp",
            timestamp ?? DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return request;
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, string json,
        string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Origin", origin);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostAuthorizedJsonAsync(HttpClient client, string path,
        string json, string origin, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-Veyon-Request-Nonce", Guid.NewGuid().ToString("N"));
        request.Headers.TryAddWithoutValidation("X-Veyon-Request-Timestamp",
            DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Origin", origin);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostStudentEventAsync(HttpClient client, string origin,
        string token, string signedEnvelope)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/classroom/events/student")
        {
            Content = new StringContent(signedEnvelope, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetStudentEventsAsync(HttpClient client, string token,
        long cursor, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/classroom/events/student?after=" + cursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<bool> ObserveCancellationAsync(Task<HttpResponseMessage> request)
    {
        try
        {
            using var response = await request;
            return false;
        }
        catch (OperationCanceledException) { return true; }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response) =>
        await JsonSerializer.DeserializeAsync<T>(await response.Content.ReadAsStreamAsync(), JsonOptions)
        ?? throw new Exception("Mobile API returned empty JSON.");

    private static void Expect(bool condition)
    {
        if (!condition) throw new Exception("Mobile control API contract check failed.");
    }
}
