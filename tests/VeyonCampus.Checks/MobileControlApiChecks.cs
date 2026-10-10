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
        await using var service = new TeacherMobileControlService(identity, () => Interlocked.Increment(ref changes),
            () => "demo", directory, httpsPort, bootstrapPort, agentTrustStore, OpenTestTeacherSigningKey);
        service.SetClassroomSession("demo", eventSessionId, [eventTarget],
            new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
            {
                [eventTarget] = eventSession.Targets[0].TargetId
            }, ClassroomMode.Normal, eventSession);
        try
        {
            await service.StartAsync(CancellationToken.None);
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
            using (var sessionRequest = AuthorizedGet("/api/session", accessToken))
            using (var sessionResponse = await client.SendAsync(sessionRequest))
            {
                var session = await ReadJsonAsync<MobileSessionResponse>(sessionResponse);
                Expect(sessionResponse.StatusCode == HttpStatusCode.OK &&
                       session.ActiveClassroomTargets.SequenceEqual([eventTarget], StringComparer.OrdinalIgnoreCase) &&
                       session.ClassroomMode == "normal");
            }

            var signedGrant = service.CreateStudentEventGrant("demo", eventSessionId, eventTarget,
                IPAddress.Parse("192.168.1.10"), teacherSigningKey, DateTimeOffset.UtcNow);
            var grant = ClassroomEventCryptography.VerifyGrant(signedGrant, "demo", teacherPublicKeyPem,
                eventSessionId, eventTarget, DateTimeOffset.UtcNow);
            Expect(grant.Target == eventTarget && grant.TeacherEndpoint == "https://192.168.1.10:39176/" &&
                   grant.ServerCertificateSha256 == Convert.ToHexString(SHA256.HashData(identity.Server.RawData)));

            var helpEvent = new ClassroomEvent(1, ClassroomEventCryptography.EventPurpose, "demo", eventSessionId,
                Guid.NewGuid(), eventTarget, ClassroomEventSender.Student, ClassroomEventType.HelpRequested,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(2), ClassroomHelpReason.NeedHelp, null, null);
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

            using var pendingMobilePollRequest = AuthorizedGet("/api/classroom/events?after=999", accessToken);
            var pendingMobilePoll = client.SendAsync(pendingMobilePollRequest,
                HttpCompletionOption.ResponseHeadersRead);
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            service.SetClassroomSession(null, null, null);
            using (var endedSessionResponse = await pendingMobilePoll.WaitAsync(TimeSpan.FromSeconds(3)))
                Expect(endedSessionResponse.StatusCode == HttpStatusCode.Unauthorized);

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
                       session.ActiveClassroomTargets.Count == 0);
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
        // Schannel needs a key container for TLS. Import the synthetic fixture
        // with default lifetime-managed storage; do not install it in a trust store.
        var pfxBytes = ephemeralServer.Export(X509ContentType.Pkcs12);
        X509Certificate2 server;
        try { server = X509CertificateLoader.LoadPkcs12(pfxBytes, null); }
        finally { CryptographicOperations.ZeroMemory(pfxBytes); }
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

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response) =>
        await JsonSerializer.DeserializeAsync<T>(await response.Content.ReadAsStreamAsync(), JsonOptions)
        ?? throw new Exception("Mobile API returned empty JSON.");

    private static void Expect(bool condition)
    {
        if (!condition) throw new Exception("Mobile control API contract check failed.");
    }
}
