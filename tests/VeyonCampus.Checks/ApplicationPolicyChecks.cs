using System.Security.Cryptography;
using System.Xml.Linq;
using VeyonCampus.App;
using VeyonCampus.Core;

internal static class ApplicationPolicyChecks
{
    public static void Run()
    {
        var preflightScript = WindowsApplicationPolicyScripts.VerifyEnvironmentAndStudents;
        var domainCheck = preflightScript.IndexOf("Domain-joined computer requires administrator review", StringComparison.Ordinal);
        var mdmCheck = preflightScript.IndexOf("MDM enrollment requires administrator review", StringComparison.Ordinal);
        var studentCheck = preflightScript.IndexOf("Student must be an enabled local account", StringComparison.Ordinal);
        var membershipMapRead = preflightScript.IndexOf("$membership = Get-LocalGroupMembershipMap", StringComparison.Ordinal);
        var membershipMapEmit = preflightScript.IndexOf("ConvertTo-Json -InputObject", StringComparison.Ordinal);
        var writePolicyScript = WindowsApplicationPolicyScripts.WritePolicy;
        var appIdServiceStart = writePolicyScript.IndexOf("Start-Service AppIDSvc", StringComparison.Ordinal);
        var policyWrite = writePolicyScript.IndexOf("Set-AppLockerPolicy", StringComparison.Ordinal);
        Expect(domainCheck >= 0 && mdmCheck > domainCheck && studentCheck > mdmCheck &&
               membershipMapRead > studentCheck && membershipMapEmit > membershipMapRead &&
               preflightScript.Contains("Get-LocalGroup -ErrorAction Stop", StringComparison.Ordinal) &&
               !preflightScript.Contains("Start-Service AppIDSvc", StringComparison.Ordinal) &&
               appIdServiceStart >= 0 && policyWrite > appIdServiceStart);
        Expect(writePolicyScript.Contains("$wasDisabled -and $hasRules", StringComparison.Ordinal) &&
               writePolicyScript.Contains("APPIDSVC_DISABLED_WITH_ACTIVE_POLICY", StringComparison.Ordinal) &&
               writePolicyScript.Contains("Set-Service AppIDSvc -StartupType Manual", StringComparison.Ordinal) &&
               writePolicyScript.Contains("Set-Service AppIDSvc -StartupType Disabled", StringComparison.Ordinal) &&
               writePolicyScript.Contains("$startedByUs", StringComparison.Ordinal));
        var appLockerFailure = AppLockerInvocationFailure.Format("恢复策略", 1,
            "PowerShell warning\r\nAPPLOCKER_OPERATION_FAILED|SetAppLockerPolicy|UnauthorizedAccessException|0x80070005|QWNjZXNzIGRlbmllZCBieSBsb2NhbCBwb2xpY3ku\r\n",
            "", stdoutTruncated: false, stderrTruncated: false);
        Expect(appLockerFailure.Contains("恢复策略", StringComparison.Ordinal) &&
               appLockerFailure.Contains("SetAppLockerPolicy", StringComparison.Ordinal) &&
               appLockerFailure.Contains("0x80070005", StringComparison.Ordinal) &&
               appLockerFailure.Contains("Access denied by local policy.", StringComparison.Ordinal));
        var appLockerFallback = AppLockerInvocationFailure.Format("读取策略", 1,
            "Get-AppLockerPolicy : The local policy could not be read.\r\nAt line:1 char:1", "",
            stdoutTruncated: false, stderrTruncated: false);
        Expect(appLockerFallback.Contains("The local policy could not be read.", StringComparison.Ordinal) &&
               !appLockerFallback.Contains("\r", StringComparison.Ordinal));
        Expect(AppLockerInvocationFailure.Format("读取策略", 1, "",
                   "", stdoutTruncated: true, stderrTruncated: false).Contains("8 MiB", StringComparison.Ordinal));

        const string scheduledTask = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers>
              <Principals><Principal><UserId>S-1-5-18</UserId><LogonType>ServiceAccount</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings><Enabled>true</Enabled></Settings>
            </Task>
            """;
        Expect(WebsitePolicyAgentInstaller.HasRequiredScheduledTaskSettings(scheduledTask) &&
               !WebsitePolicyAgentInstaller.HasRequiredScheduledTaskSettings(scheduledTask.Replace(
                   "<Settings><Enabled>true</Enabled></Settings>", "<Settings><Enabled>false</Enabled></Settings>",
                   StringComparison.Ordinal)) &&
               !WebsitePolicyAgentInstaller.HasRequiredScheduledTaskSettings(scheduledTask.Replace(
                   "<Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers>", "<Triggers />",
                   StringComparison.Ordinal)) &&
               !WebsitePolicyAgentInstaller.HasRequiredScheduledTaskSettings(scheduledTask.Replace(
                   "<BootTrigger><Enabled>true</Enabled></BootTrigger>", "<BootTrigger><Enabled>false</Enabled></BootTrigger>",
                   StringComparison.Ordinal)) &&
               !WebsitePolicyAgentInstaller.HasRequiredScheduledTaskSettings(scheduledTask.Replace(
                   "<UserId>S-1-5-18</UserId>", "<UserId>S-1-5-32-545</UserId>", StringComparison.Ordinal)));
        var testProgramFiles = Path.Combine(TestPath.CanonicalTempRoot(), "program-files");
        var expectedStudentInstall = Path.Combine(testProgramFiles, "Veyon Campus", "Student");
        Expect(StudentSetupUninstaller.IsExpectedInstallRoot(expectedStudentInstall, testProgramFiles) &&
               !StudentSetupUninstaller.IsExpectedInstallRoot(
                   Path.Combine(testProgramFiles, "Veyon Campus", "Teacher"), testProgramFiles) &&
               !StudentSetupUninstaller.IsExpectedInstallRoot(
                   Path.Combine(TestPath.CanonicalTempRoot(), "Public", "Veyon Campus", "Student"),
                   testProgramFiles));
        var teacherInstallRoot = Path.Combine(TestPath.CanonicalTempRoot(),
            "veyon-teacher-install-marker-" + Guid.NewGuid().ToString("N"));
        try
        {
            Expect(!StudentSetupUninstaller.HasTeacherInstallation(teacherInstallRoot));
            Directory.CreateDirectory(Path.Combine(teacherInstallRoot, "Veyon Campus", "Teacher"));
            Expect(StudentSetupUninstaller.HasTeacherInstallation(teacherInstallRoot));
        }
        finally
        {
            if (Directory.Exists(teacherInstallRoot)) Directory.Delete(teacherInstallRoot, recursive: true);
        }
        const string ownedUrlReservation = "URL Reservation : http://+:39174/\nSDDL : D:(A;;GX;;;SY)";
        Expect(WebsitePolicyAgentInstaller.IsProductUrlReservation(ownedUrlReservation, "http://+:39174/") &&
               !WebsitePolicyAgentInstaller.IsProductUrlReservation(
                   "URL Reservation : http://+:39174/\nSDDL : D:(A;;GX;;;SY)(A;;GX;;;BU)",
                   "http://+:39174/"));
        var uninstallDataRoot = Path.Combine(TestPath.CanonicalTempRoot(), "veyon-student-uninstall-data-" + Guid.NewGuid().ToString("N"));
        var productData = Path.Combine(uninstallDataRoot, "VeyonCampus");
        try
        {
            foreach (var name in new[] { "deployment-packages", "snapshots", "runs", "StudentAgentTrust" })
            {
                var directory = Path.Combine(productData, name);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "sentinel.txt"), name);
            }
            StudentSetupUninstaller.RemoveStudentUserData(uninstallDataRoot, preserveTeacherData: true);
            Expect(File.Exists(Path.Combine(productData, "deployment-packages", "sentinel.txt")) &&
                   File.Exists(Path.Combine(productData, "snapshots", "sentinel.txt")) &&
                   File.Exists(Path.Combine(productData, "runs", "sentinel.txt")) &&
                   File.Exists(Path.Combine(productData, "StudentAgentTrust", "sentinel.txt")));
            StudentSetupUninstaller.RemoveStudentUserData(uninstallDataRoot, preserveTeacherData: false);
            Expect(!Directory.Exists(productData));
        }
        finally
        {
            if (Directory.Exists(uninstallDataRoot)) Directory.Delete(uninstallDataRoot, recursive: true);
        }
        Expect(WindowsLocalGroupMembershipScript.Functions.Contains(
            "Local group member SID enumeration is incomplete", StringComparison.Ordinal));
        var systemPreflightScript = WindowsStudentSystemPolicyScripts.PreflightStudents;
        Expect(systemPreflightScript.Contains("$membership = Get-SelectedLocalGroupMembershipMap @('S-1-5-32-544')", StringComparison.Ordinal) &&
               systemPreflightScript.Contains("Get-LocalGroup -ErrorAction Stop", StringComparison.Ordinal) &&
               !systemPreflightScript.Contains("$admins=@(Get-LocalGroupMember", StringComparison.Ordinal));
        if (OperatingSystem.IsWindows())
        {
            var systemPolicyDiagnostic = WindowsStudentSystemPolicyBackend.FormatInvocationFailure("注册表写入", 1,
                "STUDENT_SYSTEM_POLICY_FAILED|SetItem|UnauthorizedAccessException|0x80070005|QWNjZXNzIGRlbmllZCBieSBsb2NhbCBwb2xpY3ku\r\n");
            Expect(systemPolicyDiagnostic.Contains("注册表写入", StringComparison.Ordinal) &&
                   systemPolicyDiagnostic.Contains("SetItem", StringComparison.Ordinal) &&
                   systemPolicyDiagnostic.Contains("0x80070005", StringComparison.Ordinal) &&
                   systemPolicyDiagnostic.Contains("Access denied by local policy.", StringComparison.Ordinal));
        }

        const string nestedStudent = "S-1-5-21-123-456-789-1001";
        var localGroups = WindowsLocalGroupMembershipGraph.Parse("""
            {"groups":{
              "S-1-5-32-544":["S-1-5-21-123-456-789-2000"],
              "S-1-5-21-123-456-789-2000":["S-1-5-21-123-456-789-2001"],
              "S-1-5-21-123-456-789-2001":["S-1-5-21-123-456-789-2000","S-1-5-21-123-456-789-1001"],
              "S-1-5-32-548":["S-1-5-21-123-456-789-2002"],
              "S-1-5-21-123-456-789-2002":["S-1-5-21-123-456-789-1002"],
              "S-1-5-32-556":[]
            }}
            """);
        Expect(WindowsLocalGroupMembershipGraph.IsMemberOf(localGroups, "S-1-5-32-544", nestedStudent) &&
               !WindowsLocalGroupMembershipGraph.IsMemberOf(localGroups, "S-1-5-32-556", nestedStudent) &&
               !WindowsLocalGroupMembershipGraph.IsMemberOf(localGroups, "S-1-5-32-556", "S-1-5-21-123-456-789-1099"));
        Reject(() => WindowsApplicationPolicyPreflight.VerifyStudentsNotAdministrators([nestedStudent], localGroups));
        var safeGroups = WindowsLocalGroupMembershipGraph.Parse("""
            {"groups":{"S-1-5-32-544":[],"S-1-5-32-548":[],"S-1-5-32-556":[]}}
            """);
        WindowsApplicationPolicyPreflight.VerifyStudentsNotAdministrators([nestedStudent], safeGroups);
        Reject(() => WindowsStudentSystemPolicyPreflight.VerifyStudentsNotPrivileged(
            ["S-1-5-21-123-456-789-1002"], localGroups));
        Expect(WindowsLocalGroupMembershipGraph.IsMemberOf(localGroups, "S-1-5-32-544",
                   "S-1-5-21-123-456-789-2000") &&
               !WindowsLocalGroupMembershipGraph.IsMemberOf(localGroups, "S-1-5-32-544",
                   "S-1-5-21-123-456-789-1099"));
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
        Expect(publisherChoice.Summary == "Game · CN=Game Publisher · 1 台：pc-001");
        Expect(publisherChoice.CanSelect && publisherChoice.RuleLine ==
            "publisher|Game|CN=Game Publisher|Game|game.exe|1.2.3.4|1.2.3.4");
        Expect(publisherChoice.RuleAvailability == "仅适用于 1.2.3.4");
        var hashChoice = new ApplicationInventoryChoice("pc-002", inventoryItem with
            { PublisherName = null, ProductName = null, BinaryVersion = null });
        Expect(hashChoice.CanSelect && hashChoice.RuleLine ==
            $"hash|Game|game.exe|{new string('D', 64)}|{new string('E', 64)}|1234");
        Expect(hashChoice.Publisher == "发布者未知" && hashChoice.RuleAvailability == "文件更新后需重新读取");
        var protectedChoice = new ApplicationInventoryChoice("pc-003", inventoryItem with { BinaryName = "powershell.exe" });
        Expect(!protectedChoice.CanSelect);
        var unsupportedChoice = new ApplicationInventoryChoice("pc-004", inventoryItem with
            { PublisherName = null, ProductName = null, BinaryVersion = null, FileSha256 = "", AppLockerHashSha256 = "" });
        Expect(!unsupportedChoice.CanSelect && unsupportedChoice.RuleAvailability == "暂不支持限制此程序");
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
        var administratorExemption = XDocument.Parse(ApplicationPolicyCompiler.CompileXml(verified,
            [new string('C', 64)], [sid], ["S-1-5-21-123-456-789-500"]));
        Expect(administratorExemption.Root!.Element("RuleCollection")!.Elements("FilePathRule").Any(pathRule =>
            (string?)pathRule.Attribute("UserOrGroupSid") == "S-1-5-21-123-456-789-500" &&
            (string?)pathRule.Descendants("FilePathCondition").Single().Attribute("Path") == "*"));
        Reject(() => ApplicationPolicyCompiler.CompileXml(verified,
            [new string('C', 64)], ["S-1-5-21-123-456-789-500"], []));
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
