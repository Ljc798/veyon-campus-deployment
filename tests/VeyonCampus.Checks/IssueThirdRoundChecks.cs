using System.Reflection;
using System.Text.Json;
using VeyonCampus.App;
using VeyonCampus.Core;

namespace VeyonCampus.Checks;

internal static class IssueThirdRoundChecks
{
    public static void Run()
    {
        var checks = new[] { new PreflightCheck("pass", CheckLevel.Pass, "normal"),
            new PreflightCheck("warning1", CheckLevel.Warning, "warning"),
            new PreflightCheck("blocked", CheckLevel.Blocked, "error"),
            new PreflightCheck("unknown", CheckLevel.Unknown, "review"),
            new PreflightCheck("warning2", CheckLevel.Warning, "warning"),
            new PreflightCheck("na", CheckLevel.NotApplicable, "n/a") };
        var report = new PreflightReport(DateTimeOffset.UtcNow, "digest", null, checks);
        Expect(report.ChecksByPriority.Select(c => c.Id).SequenceEqual(
            ["blocked", "unknown", "warning1", "warning2", "pass", "na"]), "preflight priority/stability");
        Expect(report.Checks[0].Id == "pass" && report.HasBlocker, "original report retained");

        var pending = new WebsitePolicyPushResult("PC-01", false, "signed report", true, true);
        Expect(!pending.Succeeded && pending.StatusLabel.Contains("报告已应用") &&
            !new WebsitePolicyPushResult("PC-02", false, "unreachable", true).StatusLabel.Contains("已应用"),
            "untrusted applied receipt stays distinct from connection failure and trusted success");
        var roundTrip = JsonSerializer.Deserialize<WebsitePolicyPushResult>(JsonSerializer.Serialize(pending))!;
        Expect(roundTrip.ReportedApplied && !roundTrip.Succeeded, "history retains review state");
        var legacy = JsonSerializer.Deserialize<WebsitePolicyPushResult>(
            "{\"Target\":\"PC-01\",\"Succeeded\":true,\"Detail\":\"ok\",\"NeedsReview\":false}")!;
        Expect(legacy.Succeeded && !legacy.ReportedApplied, "older history remains readable");

        const string sid1 = "S-1-5-21-123-456-789-1001", sid2 = "S-1-5-21-987-654-321-1001";
        ApplicationInventoryCryptography.ValidateStudentAccounts([new("学生", sid1)]);
        Reject(() => ApplicationInventoryCryptography.ValidateStudentAccounts([new("管理员", "S-1-5-32-544")]));
        Reject(() => ApplicationInventoryCryptography.ValidateStudentAccounts([new("a", sid1), new("b", sid1)]));
        Reject(() => ApplicationInventoryCryptography.ValidateStudentAccounts([new("a\n", sid1)]));
        var vm = new TeacherViewModel { CampusId = "third-round-fixture", WebsiteTargets = "PC-01\nPC-02" };
        var a = new StudentAccountChoice("PC-01", new("student", sid1));
        var b = new StudentAccountChoice("PC-02", new("student", sid2));
        vm.SetStudentAccountChoices([a,b]);
        a.IsSelected = true;
        Reject(() => vm.StudentSidsByTarget(["PC-01", "PC-02"], false));
        b.IsSelected = true;
        var perDevice = vm.StudentSidsByTarget(["PC-01", "PC-02"], false);
        Expect(perDevice["PC-01"].SequenceEqual([sid1]) && perDevice["PC-02"].SequenceEqual([sid2]), "same account names keep independent local SIDs");
        Expect(vm.StudentSidsByTarget(["PC-01"], true)["PC-01"].Length == 0, "recovery does not require selected account");
        var app = new ApplicationInventoryChoice("PC-01", new("QQ", @"C:\Program Files\QQ\QQ.exe", "QQ.exe",
            "O=TENCENT", "QQ", "1.0.0.0", new string('A',64), new string('B',64), 123));
        var protectedApp = new ApplicationInventoryChoice("PC-01", app.Item with { BinaryName = "VeyonCampus.Agent.exe" });
        Expect(app.CanSelect && !protectedApp.CanSelect, "ordinary software selectable, maintenance component protected");
        vm.SetApplicationInventoryChoices([app, protectedApp]);
        app.IsSelected = true;
        Expect(vm.ApplicationRules == app.RuleLine && vm.SelectedApplicationSummary == "QQ", "checkbox directly generates exact software rule");
        typeof(TeacherViewModel).GetField("_hasMatchingApplicationAudit", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, true);
        vm.ApplicationEnforcementReviewed = true;
        app.IsSelected = false;
        Expect(vm.ApplicationRules.Length == 0 && !vm.HasMatchingApplicationAudit && !vm.ApplicationEnforcementReviewed, "changing selection invalidates previous audit");
        vm.WebsiteTargets = "PC-03";
        Expect(vm.StudentAccountChoices.Count == 0 && vm.ApplicationInventoryChoices.Count == 0, "target change discards old device data");
        vm.UseManualApplicationInputs = true;
        vm.ApplicationStudentSids = sid1;
        Expect(vm.StudentSidsByTarget(["PC-03"], false)["PC-03"].SequenceEqual([sid1]), "explicit advanced input remains supported");
        vm.CampusId = "智学前程-third-round";
        vm.StudentSystemPolicyLockWallpaper = true;
        if (OperatingSystem.IsWindows())
        {
            vm.UseManualApplicationInputs = false;
            Expect(vm.StudentSystemPolicyPushGuidance.Contains("应用 → 账户"), "disabled system push explains account selection requirement");
            vm.UseManualApplicationInputs = true;
            vm.ApplicationStudentSids = sid1;
            Expect(vm.StudentSystemPolicyPushGuidance.Contains("请核对目标电脑和限制后推送"), "system push explains when it is ready");
        }
        else
        {
            Expect(vm.StudentSystemPolicyPushGuidance.Contains("仅支持 Windows 教师端"),
                "system policy guidance remains platform-specific");
        }
    }
    public static void CheckWindowsAccountReader()
    {
        if (!OperatingSystem.IsWindows()) return;
        var type = typeof(WindowsApplicationPolicyBackend).Assembly.GetType("VeyonCampus.Core.WindowsApplicationPolicyScripts")!;
        var script = (string)type.GetField("ReadStudentAccounts", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        var fixtures = """
            function Get-LocalUser {
                foreach ($row in @(@('student',1001,$true,'Local'),@('admin',1002,$true,'Local'),
                    @('disabled',1003,$false,'Local'),@('domain',1004,$true,'ActiveDirectory'),
                    @('networkOperator',1005,$true,'Local'),@('guest',501,$true,'Local'))) {
                    [pscustomobject]@{ Name=$row[0]; Enabled=$row[2]; PrincipalSource=$row[3];
                        SID=[pscustomobject]@{Value=('S-1-5-21-123-456-789-'+$row[1])} }
                }
            }
            function Get-LocalGroup {
                foreach ($rid in @(544,545,548,556,599)) {
                    [pscustomobject]@{SID=[pscustomobject]@{Value=('S-1-5-32-'+$rid)}}
                }
            }
            function Get-LocalGroupMember {
                param($SID)
                $values = switch ($SID.Value) {
                    'S-1-5-32-544' { 'S-1-5-32-599' }
                    'S-1-5-32-599' { 'S-1-5-21-123-456-789-1002' }
                    'S-1-5-32-556' { 'S-1-5-21-123-456-789-1005' }
                    'S-1-5-32-545' { foreach ($rid in @(1001,1002,1003,1004,1005,501)) { 'S-1-5-21-123-456-789-'+$rid } }
                }
                foreach ($value in $values) { [pscustomobject]@{SID=[pscustomobject]@{Value=$value}} }
            }
            """;
        var invoke = typeof(WindowsApplicationPolicyBackend).GetMethod("Invoke", BindingFlags.Static | BindingFlags.NonPublic)!;
        var json = (string)invoke.Invoke(null, [fixtures + "\n" + script, new { }, null])!;
        var accounts = JsonSerializer.Deserialize<StudentAccountInventoryItem[]>(json)!;
        Expect(accounts.Length == 1 && accounts[0].Name == "student", "nested privileged, disabled, domain and built-in accounts excluded");
    }

    private static void Expect(bool condition, string detail) { if (!condition) throw new Exception(detail); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("invalid account selection accepted");
    }
}
