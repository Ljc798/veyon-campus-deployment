using System.Security.Cryptography;
using System.Xml.Linq;
using VeyonCampus.App;
using VeyonCampus.Core;

internal static class ApplicationPolicyChecks
{
    public static void Run()
    {
        var writableSids = new HashSet<string>(["S-1-5-32-545"], StringComparer.OrdinalIgnoreCase);
        Expect(AppLockerProgramFilesAclRules.IsStudentWriteAllowance("S-1-5-32-545", allow: true,
                   rights: AppLockerProgramFilesAclRules.StudentWriteRightsMask, studentAccessSids: writableSids) &&
               AppLockerProgramFilesAclRules.IsStudentWriteAllowance("S-1-5-32-545", allow: true,
                   rights: AppLockerProgramFilesAclRules.WriteDataMask, studentAccessSids: writableSids) &&
               !AppLockerProgramFilesAclRules.IsStudentWriteAllowance("S-1-5-32-545", allow: true,
                   rights: AppLockerProgramFilesAclRules.ReadDataMask, studentAccessSids: writableSids) &&
               !AppLockerProgramFilesAclRules.IsStudentWriteAllowance("S-1-5-32-545", allow: false,
                   rights: AppLockerProgramFilesAclRules.StudentWriteRightsMask, studentAccessSids: writableSids) &&
               !AppLockerProgramFilesAclRules.IsStudentWriteAllowance("S-1-5-32-544", allow: true,
                   rights: AppLockerProgramFilesAclRules.StudentWriteRightsMask, studentAccessSids: writableSids));

        var now = DateTimeOffset.UtcNow;
        var sid = "S-1-5-21-123-456-789-1001";
        var rule = new ApplicationDenyRule(Guid.NewGuid(), ApplicationRuleKind.Hash, "Game",
            FileSha256: new string('A', 64), AppLockerHashSha256: new string('B', 64),
            SourceFileName: "game.exe", SourceFileLength: 1234);
        var policy = new ApplicationPolicyDocument(1, ApplicationPolicyCompiler.Purpose, "demo", 1, now,
            now.AddHours(1), ApplicationPolicyMode.Audit, [sid], [rule]);
        using var key = RSA.Create(2048);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var inventoryRequest = new ApplicationInventoryRequest(1, ApplicationInventoryCryptography.RequestPurpose,
            "demo", Guid.NewGuid(), now);
        var inventoryEnvelope = ApplicationInventoryCryptography.SignRequest(inventoryRequest, key);
        var verifiedInventoryRequest = ApplicationInventoryCryptography.VerifyRequest(inventoryEnvelope, pem, "demo", now);
        var inventoryItem = new ApplicationInventoryItem("Game", @"C:\Program Files\Game\game.exe", "game.exe",
            "CN=Game Publisher", "Game", "1.2.3.4", new string('D', 64), new string('E', 64), 1234);
        var inventoryResponse = ApplicationInventoryCryptography.CreateResponse(verifiedInventoryRequest, [inventoryItem], now);
        ApplicationInventoryCryptography.ValidateResponse(inventoryResponse, inventoryRequest, now);
        Reject(() => ApplicationInventoryCryptography.VerifyRequest(inventoryEnvelope, pem, "other", now));
        Reject(() => ApplicationInventoryCryptography.ValidateResponse(inventoryResponse with { Nonce = Guid.NewGuid() },
            inventoryRequest, now));
        Reject(() => ApplicationInventoryCryptography.ValidateResponse(inventoryResponse with
            { Items = [inventoryItem with { FileSha256 = "bad" }] }, inventoryRequest, now));
        var publisherChoice = new ApplicationInventoryChoice("pc-001", inventoryItem);
        Expect(publisherChoice.CanSelect && publisherChoice.RuleLine ==
            "publisher|Game|CN=Game Publisher|Game|game.exe|1.2.3.4|1.2.3.4");
        var hashChoice = new ApplicationInventoryChoice("pc-002", inventoryItem with
            { PublisherName = null, ProductName = null, BinaryVersion = null });
        Expect(hashChoice.CanSelect && hashChoice.RuleLine ==
            $"hash|Game|game.exe|{new string('D', 64)}|{new string('E', 64)}|1234");
        var protectedChoice = new ApplicationInventoryChoice("pc-003", inventoryItem with { BinaryName = "powershell.exe" });
        Expect(!protectedChoice.CanSelect);
        var signed = ApplicationPolicyCryptography.Sign(policy, key);
        var verified = ApplicationPolicyCryptography.Verify(signed, pem, "demo", 0, now);
        var xml = XDocument.Parse(ApplicationPolicyCompiler.CompileXml(verified, [new string('C', 64)]));
        var collection = xml.Root!.Element("RuleCollection")!;
        Expect((string?)collection.Attribute("Type") == "Exe" && (string?)collection.Attribute("EnforcementMode") == "AuditOnly");
        var allow = collection.Element("FilePathRule")!;
        Expect((string?)allow.Attribute("Action") == "Allow" && (string?)allow.Attribute("UserOrGroupSid") == "S-1-1-0" &&
            (string?)allow.Descendants("FilePathCondition").Single().Attribute("Path") == "*");
        var deny = collection.Element("FileHashRule")!;
        Expect((string?)deny.Attribute("Action") == "Deny" && (string?)deny.Attribute("UserOrGroupSid") == sid &&
            (string?)deny.Descendants("FileHash").Single().Attribute("Data") == "0x" + new string('B', 64));
        var softwareBaseline = XDocument.Parse(ApplicationPolicyCompiler.CompileXml(verified,
            [new string('C', 64)], [sid], ["S-1-5-21-123-456-789-2001"]));
        var baselineExe = softwareBaseline.Root!.Element("RuleCollection")!;
        Expect((string?)baselineExe.Attribute("EnforcementMode") == "Enabled" &&
               !baselineExe.Elements("FileHashRule").Any() &&
               baselineExe.Elements("FilePathRule").Any(pathRule =>
                   (string?)pathRule.Descendants("FilePathCondition").Single().Attribute("Path") == "%PROGRAMFILES%\\*") &&
               baselineExe.Elements("FilePathRule").Any(pathRule =>
                   (string?)pathRule.Attribute("Action") == "Deny" &&
                   (string?)pathRule.Descendants("FilePathCondition").Single().Attribute("Path") == "%WINDIR%\\Temp\\*") &&
               baselineExe.Elements("FilePathRule").Any(pathRule =>
                   (string?)pathRule.Attribute("UserOrGroupSid") == "S-1-5-21-123-456-789-2001" &&
                   (string?)pathRule.Descendants("FilePathCondition").Single().Attribute("Path") == "*"));
        var enforcedBaseline = XDocument.Parse(ApplicationPolicyCompiler.CompileXml(
            verified with { Mode = ApplicationPolicyMode.Enforce }, [new string('C', 64)], [sid], []));
        Expect((string?)enforcedBaseline.Root!.Element("RuleCollection")!.Attribute("EnforcementMode") == "Enabled" &&
               enforcedBaseline.Descendants("FileHashRule").Any());
        Reject(() => ApplicationPolicyCompiler.CompileXml(verified, [new string('C', 64)], [sid], [sid]));
        var appx = xml.Root.Element("RuleCollection")!.ElementsAfterSelf("RuleCollection").Single();
        var appxRule = appx.Element("FilePublisherRule")!;
        Expect(xml.Root.Elements().Count() == 2 && (string?)appx.Attribute("Type") == "Appx" &&
            (string?)appx.Attribute("EnforcementMode") == "Enabled" &&
            (string?)appxRule.Attribute("Action") == "Allow" &&
            (string?)appxRule.Attribute("UserOrGroupSid") == "S-1-1-0" &&
            (string?)appxRule.Descendants("FilePublisherCondition").Single().Attribute("PublisherName") == "*");
        Reject(() => ApplicationPolicyCompiler.CompileXml(policy, []));
        Reject(() => ApplicationPolicyCompiler.CompileXml(policy, [new string('B', 64)]));
        Reject(() => ApplicationPolicyCryptography.Verify(signed, pem, "other", 0, now));
        Reject(() => ApplicationPolicyCryptography.Verify(signed, pem, "demo", 1, now));
        Reject(() => ApplicationPolicyCryptography.Verify(signed, pem, "demo", 0, now.AddHours(2)));
        Reject(() => ApplicationPolicyCryptography.Verify(signed, pem, "demo", 0, now.AddHours(-1)));
        foreach (var invalid in new[] { "{}", "null", "{\"Payload\":null}", "{\"Payload\":\"!\",\"Signature\":\"!\"}" })
            Reject(() => ApplicationPolicyCryptography.Verify(invalid, pem, "demo", 0, now));
        using var wrongKey = RSA.Create(2048);
        Reject(() => ApplicationPolicyCryptography.Verify(signed.TrimEnd('}') + ",\"Payload\":\"AAAA\"}", pem, "demo", 0, now));
        Reject(() => ApplicationPolicyCryptography.Verify(signed, wrongKey.ExportSubjectPublicKeyInfoPem(), "demo", 0, now));
        Reject(() => ApplicationPolicyCompiler.Validate(policy with { Purpose = "website" }));
        Reject(() => ApplicationPolicyCompiler.Validate(policy with { ExpiresUtc = now.AddHours(25) }));
        Reject(() => ApplicationPolicyCompiler.Validate(policy with { Rules = [rule, rule] }));
        foreach (var invalidSid in new[] { "S-1-1-0", "S-1-5-18", "S-1-5-32-544", "S-1-5-21-123-456-789-500", "S-1-5-21-01-2-3-1001" })
            Reject(() => ApplicationPolicyCompiler.Validate(policy with { StudentSids = [invalidSid] }));
        foreach (var name in new[]
                 {
                     "powershell.exe", "VeyonCampus.Agent.exe", "SystemSettings.exe", "RuntimeBroker.exe",
                     "StartMenuExperienceHost.exe", "TrustedInstaller.exe", "MsMpEng.exe", "rundll32.exe",
                     "*.exe", "../game.exe"
                 })
            Reject(() => ApplicationPolicyCompiler.Validate(policy with { Rules = [rule with { SourceFileName = name }] }));
        var publisher = new ApplicationDenyRule(Guid.NewGuid(), ApplicationRuleKind.Publisher, "Game & Co",
            PublisherName: "O=Game & Co", ProductName: "Game", BinaryName: "game.exe", MinimumVersion: "1.0.0.0", MaximumVersion: "2.0.0.0");
        foreach (var name in new[]
                 {
                     "SystemSettings.exe", "ApplicationFrameHost.exe", "ShellExperienceHost.exe",
                     "MoUsoCoreWorker.exe", "SecurityHealthService.exe", "rundll32.exe"
                 })
            Reject(() => ApplicationPolicyCompiler.Validate(policy with { Rules = [publisher with { BinaryName = name }] }));
        var publisherXml = XDocument.Parse(ApplicationPolicyCompiler.CompileXml(policy with { Mode = ApplicationPolicyMode.Enforce,
            Rules = [publisher] }, [new string('C', 64)]));
        Expect((string?)publisherXml.Root!.Element("RuleCollection")!.Attribute("EnforcementMode") == "Enabled" &&
            publisherXml.Descendants("FilePublisherCondition").Any(condition =>
                (string?)condition.Attribute("PublisherName") == "O=Game & Co"));
        Reject(() => ApplicationPolicyCompiler.Validate(policy with { Rules = [publisher with { BinaryName = "*" }] }));
        Reject(() => ApplicationPolicyCompiler.Validate(policy with { Rules = [publisher with { MinimumVersion = "3.0.0.0" }] }));
        var disabled = policy with { Mode = ApplicationPolicyMode.Disabled, Rules = [], StudentSids = [], ExpiresUtc = null };
        Expect(!XDocument.Parse(ApplicationPolicyCompiler.CompileXml(disabled, [])).Root!.HasElements);
        Expect(ApplicationPolicyCryptography.Verify(ApplicationPolicyCryptography.Sign(disabled, key), pem, "demo", 0, now).Mode == ApplicationPolicyMode.Disabled);
        // Signing uses a separate purpose: a website envelope cannot become an application policy.
        var website = WebsitePolicyCryptography.Sign(WebsitePolicyCompiler.Create("demo", 1, WebsitePolicyMode.Blocklist, ["game.example"]), key);
        Reject(() => ApplicationPolicyCryptography.Verify(website, pem, "demo", 0, now));
    }

    private static void Expect(bool condition) { if (!condition) throw new Exception("Application policy check failed."); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Invalid application policy accepted.");
    }
}
