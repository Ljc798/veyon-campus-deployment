using System.Reflection;
using VeyonCampus.App;
using VeyonCampus.Core;

internal static class TeacherWorkflowChecks
{
    internal static void Run()
    {
        var vm = new TeacherViewModel();
        CheckClassroomEventFeed(vm);
        if (vm.SelectedWebsiteMode != WebsitePolicyMode.Blocklist) throw new Exception("Default mode changed.");
        vm.WebsiteModeIndex = 1;
        if (vm.SelectedWebsiteMode != WebsitePolicyMode.Allowlist) throw new Exception("Allowlist index incorrect.");
        vm.WebsiteModeIndex = 2;
        if (vm.SelectedWebsiteMode == WebsitePolicyMode.Disabled) throw new Exception("Disable is selectable.");
        Console.WriteLine("PASS website selector contains only blocklist and allowlist");
        vm.CampusId = "";
        vm.DisableWebsitePolicyAsync().GetAwaiter().GetResult(); // Invalid identity stops before signing/network writes.
        if (vm.WebsiteModeIndex != 1 || vm.SelectedWebsiteMode != WebsitePolicyMode.Allowlist)
            throw new Exception("Release action changed selected restriction mode.");
        Console.WriteLine("PASS release action preserves selected mode");

        var locationsProperty = typeof(TeacherViewModel).GetProperty("WebsiteLocations")!;
        var location = new VeyonNetworkLocation("Computer room", new[] { "PC-01" });
        locationsProperty.SetValue(vm, new[] { location });
        vm.WebsiteLocationIndex = 0;
        vm.CampusId = "Existing key campus";
        vm.SelectedWebsiteSigningCampus = null;
        if (vm.CampusId != "Existing key campus") throw new Exception("Refreshing key choices erased manual identity.");
        vm.FillWebsiteTargetsFromSelectedLocation();
        if (vm.CampusId != "Existing key campus" || vm.WebsiteTargets != "PC-01") throw new Exception("Room overwrote key campus.");
        vm.CampusId = "";
        vm.FillWebsiteTargetsFromSelectedLocation();
        if (vm.CampusId != "") throw new Exception("Room inferred a key campus.");
        Console.WriteLine("PASS room selection fills targets and preserves key identity, including blank identity");

        vm.CampusId = "policy-navigation-fixture";
        var summaryNotified = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.ClassroomTargetSummary)) summaryNotified = true; };
        vm.WebsiteTargets = "PC-01\nPC-02";
        if (!summaryNotified || !vm.ClassroomTargetSummary.EndsWith("2 台电脑"))
            throw new Exception("Shared target summary did not refresh after editing computers.");
        vm.WebsiteTargets = "PC-01";
        vm.WebsiteDomains = "example.com";
        vm.IsApplicationPolicyPage = true;
        if (!vm.IsApplicationPolicyPage || vm.IsWebsitePolicyPage || vm.IsSystemPolicyPage || vm.AreClassroomTargetsExpanded)
            throw new Exception("Policy navigation did not isolate and reveal the selected editor.");
        vm.IsSystemPolicyPage = true;
        vm.IsWebsitePolicyPage = true;
        if (vm.WebsiteDomains != "example.com" || vm.WebsiteTargets != "PC-01" || vm.CampusId != "policy-navigation-fixture")
            throw new Exception("Policy switching lost shared targets or existing rules.");
        Console.WriteLine("PASS policy categories share targets and preserve inputs without invalidating controls");

        var configure = typeof(VeyonTeacherAuthentication).GetMethod("Configure", BindingFlags.NonPublic | BindingFlags.Static)!;
        StepResult Run(Func<string[], (int?, string)> callback) => (StepResult)configure.Invoke(null, [callback])!;
        var calls = new List<string>();
        var result = Run(args => { calls.Add(string.Join(" ", args)); return (0, args[1] == "get" ? "1\r\n" : ""); });
        if (!result.Ok || !calls.SequenceEqual(new[] { "config set Authentication/Method 1", "config get Authentication/Method" }))
            throw new Exception("Authentication did not write and verify key mode.");
        calls.Clear();
        if (Run(args => { calls.Add(args[1]); return (1, ""); }).Ok || calls.Count != 1)
            throw new Exception("Failed write did not stop.");
        if (Run(args => (0, "0")).Ok || Run(args => args[1] == "get" ? (1, "1") : (0, "")).Ok)
            throw new Exception("Invalid readback accepted.");
        Console.WriteLine("PASS teacher authentication writes key mode and rejects failed writes/incorrect readback");
    }

    private static void CheckClassroomEventFeed(TeacherViewModel viewModel)
    {
        var sessionId = Guid.NewGuid();
        var helpEventId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var helpRequest = new ClassroomEvent(1, ClassroomEventCryptography.EventPurpose, "demo", sessionId,
            helpEventId, "PC-01", ClassroomEventSender.Student, ClassroomEventType.HelpRequested,
            now, now.Add(ClassroomEventCryptography.MaximumEventLifetime), ClassroomHelpReason.NeedHelp,
            null, null);
        viewModel.ApplyClassroomEvents(sessionId, [helpRequest]);
        if (!viewModel.HasClassroomEventItems || viewModel.ClassroomEventItems.Count != 1 ||
            !viewModel.ClassroomEventItems[0].CanReply || viewModel.ClassroomEventItems[0].Status != "等待回复" ||
            viewModel.PendingClassroomHelpCount != 1 || !viewModel.ClassroomNavigationLabel.Contains("1 个待回复"))
            throw new Exception("Teacher classroom help request was not shown as replyable.");

        var reply = new ClassroomEvent(1, ClassroomEventCryptography.EventPurpose, "demo", sessionId,
            Guid.NewGuid(), "PC-01", ClassroomEventSender.Teacher, ClassroomEventType.TeacherReply,
            now.AddSeconds(1), now.AddSeconds(1).Add(ClassroomEventCryptography.MaximumEventLifetime),
            null, "我马上来看。", helpEventId);
        viewModel.ApplyClassroomEvents(sessionId, [reply]);
        if (viewModel.ClassroomEventItems[0].CanReply || viewModel.ClassroomEventItems[0].Status != "已回复" ||
            viewModel.PendingClassroomHelpCount != 0 ||
            viewModel.ClassroomEventItems[0].ReplyMessage != "我马上来看。")
            throw new Exception("Teacher event feed did not correlate the reply with the student request.");
        viewModel.ResetClassroomEventFeed(null);
        if (viewModel.HasClassroomEventItems) throw new Exception("Teacher event feed survived the end of class.");
        Console.WriteLine("PASS teacher classroom inbox correlates help replies and clears on session end");
    }
}
