using System.Security.Cryptography;
using System.Text.Json;
using VeyonCampus.Core;

internal static class TeacherMobileControlChecks
{
    public static void Run()
    {
        CheckProfilesAndStores();
        CheckPairedDeviceCredentials();
        CheckStatusRequestAndResponse();
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
            Expect(MobilePolicyProfileStore.ReadAll(directory).Count == 2);
            Reject(() => MobilePolicyProfileCompiler.Validate(application with { StudentSids = ["S-1-1-0"] }));
            Reject(() => MobilePolicyProfileCompiler.Validate(application with { LifetimeMinutes = 0 }));
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
            new WebsitePolicyReportedState(0, WebsitePolicyMode.Disabled, null, null), null);
        WebsitePolicyStatusCryptography.ValidateResponse(response, request, now);
        Reject(() => WebsitePolicyStatusCryptography.ValidateResponse(response with { Nonce = Guid.NewGuid() }, request, now));
        Reject(() => WebsitePolicyStatusCryptography.ValidateResponse(response with { CampusId = "other" }, request, now));
        Reject(() => WebsitePolicyStatusCryptography.ValidateResponse(response with
        {
            Website = response.Website with { Revision = -1 }
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
