using System.Security.Cryptography;
using System.Net;
using System.Text.Json;
using VeyonCampus.Core;
using VeyonCampus.App;

internal static class TeacherMobileControlChecks
{
    public static void Run()
    {
        CheckProfilesAndStores();
        CheckPairedDeviceCredentials();
        CheckStatusRequestAndResponse();
        CheckLanSubnetBoundaries();
        CheckPairingQrLink();
        CheckClassroomLocationMapping();
    }

    private static void CheckLanSubnetBoundaries()
    {
        var local = IPAddress.Parse("192.168.20.14");
        var mask = IPAddress.Parse("255.255.255.0");
        Expect(MobileControlLanNetworkPolicy.AreOnSameIpv4Subnet(local,
            IPAddress.Parse("192.168.20.200"), mask));
        Expect(!MobileControlLanNetworkPolicy.AreOnSameIpv4Subnet(local,
            IPAddress.Parse("192.168.21.1"), mask));
        Expect(MobileControlLanNetworkPolicy.AreOnSameIpv4Subnet(local,
            IPAddress.Parse("::ffff:192.168.20.200"), mask));
        Expect(!MobileControlLanNetworkPolicy.AreOnSameIpv4Subnet(local,
            IPAddress.Parse("2001:db8::1"), mask));
    }

    private static void CheckPairingQrLink()
    {
        const string bootstrap = "http://192.168.20.14:39177/";
        const string teacher = "https://192.168.20.14:39176/";
        var link = MobilePairingQrLink.Create(bootstrap, teacher, "01234567");
        var uri = new Uri(link);
        Expect(uri.Scheme == Uri.UriSchemeHttp && uri.Port == TeacherMobileControlManager.BootstrapPort &&
               uri.Query.Length == 0 && uri.Fragment == "#pair=01234567");
        Reject(() => MobilePairingQrLink.Create("https://192.168.20.14:39177/", teacher, "01234567"));
        Reject(() => MobilePairingQrLink.Create("http://example.com:39177/", teacher, "01234567"));
        Reject(() => MobilePairingQrLink.Create("http://192.168.20.14:39177/?code=1", teacher, "01234567"));
        Reject(() => MobilePairingQrLink.Create(bootstrap, "https://192.168.20.15:39176/", "01234567"));
        Reject(() => MobilePairingQrLink.Create(bootstrap, teacher, "1234"));
    }

    private static void CheckClassroomLocationMapping()
    {
        var location = new VeyonNetworkLocation("三楼机房", ["192.168.20.21", "student-02.school.test"]);
        var campus = TeacherCampusProfileFromVeyon.MergeLocation(null, "示范校区", location);
        var room = campus.Rooms.Single();
        Expect(campus.DisplayName == "示范校区" && room.DisplayName == "三楼机房" &&
               room.ComputerCount == 2 && room.HostOverrides!.SequenceEqual(location.Targets));
        var session = ClassroomSession.Start(campus, room.RoomId, DateTimeOffset.UtcNow);
        Expect(session.Targets.Select(item => item.DeviceLabel).SequenceEqual(["PC-01", "PC-02"]));

        var refreshed = TeacherCampusProfileFromVeyon.MergeLocation(campus, "ignored",
            new VeyonNetworkLocation("三楼机房", ["192.168.20.31", "192.168.20.32", "192.168.20.33"]));
        Expect(refreshed.ProfileId == campus.ProfileId && refreshed.Rooms.Single().RoomId == room.RoomId &&
               refreshed.Rooms.Single().ComputerCount == 3 &&
               refreshed.Rooms.Single().HostOverrides!.SequenceEqual(["192.168.20.31", "192.168.20.32", "192.168.20.33"]));
        Reject(() => TeacherCampusProfileFromVeyon.MergeLocation(null, "校区",
            new VeyonNetworkLocation("重复名单", ["192.168.20.21", "192.168.20.21"])));
        Reject(() => TeacherCampusProfileFromVeyon.MergeLocation(null, "校区",
            new VeyonNetworkLocation("空机房", [])));
    }

    private static void CheckProfilesAndStores()
    {
        var directory = Path.Combine(TestPath.CanonicalTempRoot(),
            "veyon-mobile-control-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var website = MobilePolicyProfileCompiler.Validate(new MobilePolicyProfile(
                Guid.NewGuid(), "课堂网站", "demo", MobilePolicyProfileKind.Website, 60,
                WebsitePolicyMode.Blocklist, ["Example.COM.", "example.com"]));
            Expect(website.WebsiteDomains is ["example.com"]);
            MobilePolicyProfileStore.Save(website, directory);
            var restoredWebsite = MobilePolicyProfileStore.ReadAll(directory).Single();
            Expect(restoredWebsite.Id == website.Id && restoredWebsite.Name == website.Name &&
                   restoredWebsite.WebsiteDomains!.SequenceEqual(["example.com"]));
            Reject(() => MobilePolicyProfileCompiler.Validate(website with { CampusId = " demo" }));
            Reject(() => MobilePolicyProfileCompiler.Validate(website with { WebsiteMode = WebsitePolicyMode.Disabled }));
            Reject(() => MobilePolicyProfileCompiler.Validate(website with { WebsiteDomains = ["https://example.com/path"] }));

            var rule = new ApplicationDenyRule(Guid.NewGuid(), ApplicationRuleKind.Hash, "Game",
                FileSha256: new string('A', 64), AppLockerHashSha256: new string('B', 64),
                SourceFileName: "game.exe", SourceFileLength: 1024);
            var application = MobilePolicyProfileCompiler.Validate(new MobilePolicyProfile(
                Guid.NewGuid(), "课堂应用", "demo", MobilePolicyProfileKind.Application, 45,
                ApplicationMode: ApplicationPolicyMode.Audit,
                StudentSids: ["S-1-5-21-123-456-789-1001"], ApplicationRules: [rule]));
            MobilePolicyProfileStore.Save(application, directory);

            var systemSettings = new StudentSystemPolicySettings(true, true, false, false, true, true);
            var system = MobilePolicyProfileCompiler.Validate(new MobilePolicyProfile(
                Guid.NewGuid(), "长期系统基线", "demo", MobilePolicyProfileKind.System, 0,
                StudentSids: ["S-1-5-21-123-456-789-1001"], UpdatedUtc: DateTimeOffset.UtcNow,
                SystemSettings: systemSettings));
            MobilePolicyProfileStore.Save(system, directory);
            var restoredSystem = MobilePolicyProfileStore.ReadAll(directory).Single(item => item.Id == system.Id);
            Expect(MobilePolicyProfileStore.ReadAll(directory).Count == 3 &&
                   restoredSystem.SystemSettings == systemSettings && restoredSystem.StudentSids!.SequenceEqual(
                       ["S-1-5-21-123-456-789-1001"]) && restoredSystem.LifetimeMinutes == 0);

            var activeSystemPolicy = MobilePolicyProfileCompiler.CreateStudentSystemPolicy(system, 3, true);
            var disabledSystemPolicy = MobilePolicyProfileCompiler.CreateStudentSystemPolicy(system, 4, false);
            using var systemSigningKey = RSA.Create(2048);
            var signedSystemPolicy = StudentSystemPolicyCryptography.Sign(activeSystemPolicy, systemSigningKey);
            var verifiedSystemPolicy = StudentSystemPolicyCryptography.Verify(signedSystemPolicy,
                systemSigningKey.ExportSubjectPublicKeyInfoPem(), "demo", 2);
            Expect(verifiedSystemPolicy.Settings == systemSettings &&
                   verifiedSystemPolicy.StudentSids.SequenceEqual(system.StudentSids!) &&
                   disabledSystemPolicy.Settings.IsEmpty && disabledSystemPolicy.StudentSids.Count == 0);
            var reviewedInstallProfile = MobilePolicyProfileCompiler.Validate(system with
            {
                SystemSettings = StudentSystemPolicySettings.Default,
                SoftwareInstallationImpactReviewed = true
            });
            Expect(reviewedInstallProfile.SystemSettings!.ProhibitSoftwareInstallation &&
                   reviewedInstallProfile.SoftwareInstallationImpactReviewed);
            Reject(() => MobilePolicyProfileCompiler.Validate(system with { LifetimeMinutes = 45 }));
            Reject(() => MobilePolicyProfileCompiler.Validate(system with
            {
                SystemSettings = StudentSystemPolicySettings.Default,
                SoftwareInstallationImpactReviewed = false
            }));
            Reject(() => MobilePolicyProfileCompiler.Validate(system with
            {
                SystemSettings = new StudentSystemPolicySettings(false, false, false, false, false, false)
            }));
            Reject(() => MobilePolicyProfileCompiler.Validate(system with { StudentSids = [] }));
            Reject(() => MobilePolicyProfileCompiler.Validate(application with { StudentSids = ["S-1-1-0"] }));
            Reject(() => MobilePolicyProfileCompiler.Validate(application with { LifetimeMinutes = 0 }));
            Reject(() => MobilePolicyProfileCompiler.CreateStudentSystemPolicy(application, 4, true));
            Expect(MobilePolicyProfileStore.Remove(website.Id, directory));
            Expect(!MobilePolicyProfileStore.Remove(website.Id, directory));

            MobileControlAuditStore.Append(new MobileControlAuditEntry(DateTimeOffset.UtcNow, Guid.NewGuid(),
                "policy-enable", application.Id, ["PC-01"], "1/1 responded",
                [new MobileControlAuditTargetResult("PC-01", "agent-accepted")]), directory);
            var audit = MobileControlAuditStore.Read(directory);
            Expect(audit.Count == 1 && audit[0].Targets.SequenceEqual(["PC-01"]) &&
                   audit[0].Results is [{ Outcome: "agent-accepted" }]);
            Reject(() => MobileControlAuditStore.Append(new MobileControlAuditEntry(DateTimeOffset.UtcNow,
                Guid.NewGuid(), "bad\nvalue", application.Id, ["PC-01"], "ok"), directory));
            Reject(() => MobileControlAuditStore.Append(new MobileControlAuditEntry(DateTimeOffset.UtcNow,
                Guid.NewGuid(), "policy-enable", application.Id, ["PC-01"], "ok",
                [new MobileControlAuditTargetResult("PC-02", "agent-accepted")]), directory));
            File.WriteAllText(Path.Combine(directory, "policy-profiles.json"),
                "{\"schemaVersion\":1,\"schemaVersion\":1,\"profiles\":[]}");
            Reject(() => MobilePolicyProfileStore.ReadAll(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void CheckPairedDeviceCredentials()
    {
        var directory = Path.Combine(TestPath.CanonicalTempRoot(),
            "veyon-mobile-paired-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string token = "mobile-test-token-0123456789-abcdefghijklmnopqrstuvwxyz";
            var device = MobilePairedDeviceStore.Add(token, "测试手机", directory);
            Expect(MobilePairedDeviceStore.IsAuthorized(token, directory));
            Expect(MobilePairedDeviceStore.FindAuthorized("another-test-token-0123456789-abcdefghijklmnopqrstuvwxyz", directory) is null);
            var raw = File.ReadAllText(Path.Combine(directory, "paired-devices.json"));
            Expect(!raw.Contains(token, StringComparison.Ordinal) &&
                   raw.Contains(MobilePairedDeviceStore.HashToken(token), StringComparison.Ordinal));
            Expect(MobilePairedDeviceStore.Revoke(device.Id, directory));
            Expect(!MobilePairedDeviceStore.IsAuthorized(token, directory) && MobilePairedDeviceStore.List(directory).Count == 0);
            var firstHistoryId = Guid.Empty;
            MobilePairedDeviceView? newest = null;
            for (var index = 0; index < 205; index++)
            {
                var historyToken = "history-token-" + index.ToString("D3") + "-abcdefghijklmnopqrstuvwxyz";
                newest = MobilePairedDeviceStore.Add(historyToken, "测试手机", directory);
                if (index == 0) firstHistoryId = newest.Id;
                Expect(MobilePairedDeviceStore.Revoke(newest.Id, directory));
            }
            Expect(MobilePairedDeviceStore.Find(firstHistoryId, directory) is null && newest is not null &&
                   MobilePairedDeviceStore.Find(newest.Id, directory) is not null &&
                   MobilePairedDeviceStore.List(directory).Count == 0);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void CheckStatusRequestAndResponse()
    {
        using var key = RSA.Create(2048);
        using var wrongKey = RSA.Create(2048);
        using var agentKey = RSA.Create(2048);
        var now = DateTimeOffset.UtcNow;
        var request = new WebsitePolicyStatusRequest(1, WebsitePolicyStatusCryptography.RequestPurpose,
            "demo", Guid.NewGuid(), now);
        var signed = WebsitePolicyStatusCryptography.SignRequest(request, key);
        var verified = WebsitePolicyStatusCryptography.VerifyRequest(signed,
            key.ExportSubjectPublicKeyInfoPem(), "demo", now);
        Expect(verified.Nonce == request.Nonce && verified.CampusId == "demo");
        Reject(() => WebsitePolicyStatusCryptography.VerifyRequest(signed,
            key.ExportSubjectPublicKeyInfoPem(), "other", now));
        Reject(() => WebsitePolicyStatusCryptography.VerifyRequest(signed,
            wrongKey.ExportSubjectPublicKeyInfoPem(), "demo", now));
        var stale = request with { IssuedUtc = now.AddMinutes(-6) };
        var staleSigned = WebsitePolicyStatusCryptography.SignRequest(stale, key);
        Reject(() => WebsitePolicyStatusCryptography.VerifyRequest(staleSigned,
            key.ExportSubjectPublicKeyInfoPem(), "demo", now));

        var response = new StudentAgentStatusResponse(1, WebsitePolicyStatusCryptography.ResponsePurpose,
            "demo", request.Nonce, now, "1.2.3", new string('A', 64),
            new WebsitePolicyReportedState(0, WebsitePolicyMode.Disabled, null, null), null,
            new StudentSystemPolicyReportedState(true, 5,
                new StudentSystemPolicySettings(true, true, false, false, true, false), false));
        WebsitePolicyStatusCryptography.ValidateResponse(response, request, now);
        var signedResponse = StudentAgentResponseCryptography.Sign(response, agentKey);
        var checkedResponse = StudentAgentResponseCryptography.Verify<StudentAgentStatusResponse>(signedResponse,
            agentKey.ExportSubjectPublicKeyInfoPem());
        Expect(checkedResponse.MatchesPinnedKey && checkedResponse.Payload == response &&
               checkedResponse.Payload.SystemPolicy?.Settings?.ProhibitAccountManagement == true);
        Expect(!StudentAgentResponseCryptography.Verify<StudentAgentStatusResponse>(signedResponse,
            wrongKey.ExportSubjectPublicKeyInfoPem()).MatchesPinnedKey);
        Reject(() => WebsitePolicyStatusCryptography.ValidateResponse(response with { Nonce = Guid.NewGuid() }, request, now));
        Reject(() => WebsitePolicyStatusCryptography.ValidateResponse(response with { CampusId = "other" }, request, now));
        Reject(() => WebsitePolicyStatusCryptography.ValidateResponse(response with
        {
            Website = response.Website with { Revision = -1 }
        }, request, now));
        Reject(() => WebsitePolicyStatusCryptography.ValidateResponse(response with
        {
            SystemPolicy = response.SystemPolicy! with { Revision = -1 }
        }, request, now));
        Reject(() => WebsitePolicyStatusCryptography.ValidateResponse(response with
        {
            SystemPolicy = response.SystemPolicy! with { Supported = false }
        }, request, now));
        using var json = JsonDocument.Parse(signed);
        Expect(json.RootElement.TryGetProperty("payload", out _) && json.RootElement.TryGetProperty("signature", out _));
    }

    private static void Expect(bool condition)
    {
        if (!condition) throw new Exception("Teacher mobile control check failed.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException) { return; }
        throw new Exception("Invalid teacher mobile control input was accepted.");
    }
}
