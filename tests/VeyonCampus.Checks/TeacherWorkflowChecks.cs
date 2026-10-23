using System.Reflection;
using VeyonCampus.App;
using VeyonCampus.Core;

internal static class TeacherWorkflowChecks
{
    internal static void Run()
    {
        var fixtureDirectory = Path.GetFullPath(Path.Combine("artifacts", "test-fixtures", "teacher-workflow",
            Guid.NewGuid().ToString("N")));
        var vm = new TeacherViewModel(websitePolicyDraftDirectory: fixtureDirectory);
        if (!vm.IsClassroomExperiencePage || vm.IsSetupPage ||
            !vm.ClassroomInteractionNavigationLabel.StartsWith("课堂控制", StringComparison.Ordinal))
            throw new Exception("Teacher should open the classroom control workspace by default.");
        CheckClassroomEventFeed(vm);
        if (vm.SelectedWebsiteMode != WebsitePolicyMode.Blocklist) throw new Exception("Default mode changed.");
        if (vm.WebsiteDurationSummary != "限制时长：60 分钟")
            throw new Exception("The collapsed duration summary does not show the default lifetime.");
        vm.WebsiteDurationIndex = 5;
        if (vm.WebsiteDurationSummary != "限制时长：不自动到期")
            throw new Exception("The collapsed duration summary did not follow the selected lifetime.");
        vm.WebsiteDurationIndex = 1;
        vm.WebsiteModeIndex = 1;
        if (vm.SelectedWebsiteMode != WebsitePolicyMode.Allowlist) throw new Exception("Allowlist index incorrect.");
        vm.WebsiteModeIndex = 2;
        if (vm.SelectedWebsiteMode == WebsitePolicyMode.Disabled) throw new Exception("Disable is selectable.");
        if (vm.SelectedWebsiteMode != WebsitePolicyMode.Combined)
            throw new Exception("Combined mode is not mapped to the combined policy.");
        Console.WriteLine("PASS website selector supports blocklist, allowlist and combined mode");
        vm.WebsiteModeIndex = 1;
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
        if (!summaryNotified || vm.WebsitePolicyTargetChoices.Count != 2 ||
            vm.WebsitePolicyTargetChoices.Any(choice => !choice.IsSelected))
            throw new Exception("Shared target summary did not refresh after editing computers.");
        vm.WebsiteTargets = "PC-01";
        vm.WebsiteBlockDomains = "example.com";
        vm.IsApplicationPolicyPage = true;
        if (!vm.IsApplicationPolicyPage || vm.IsWebsitePolicyPage || vm.IsSystemPolicyPage || vm.AreClassroomTargetsExpanded)
            throw new Exception("Policy navigation did not isolate and reveal the selected editor.");
        vm.IsSystemPolicyPage = true;
        vm.IsWebsitePolicyPage = true;
        if (vm.WebsiteBlockDomains != "example.com" || vm.WebsiteTargets != "PC-01" || vm.CampusId != "policy-navigation-fixture")
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

    internal static void RunHealthSelection() => CheckHealthScopeAndRetrySelection(new TeacherViewModel());

    private static void CheckHealthScopeAndRetrySelection(TeacherViewModel model)
    {
        model.WebsiteTargets = "PC-01\nPC-02\nPC-03";
        model.SelectAllWebsitePolicyTargets(true);
        var rows = model.DeviceHealthRows.ToArray();
        var choices = model.WebsitePolicyTargetChoices.ToArray();
        var revisionField = typeof(TeacherViewModel).GetField("_deviceHealthSelectionRevision",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var revision = (long)revisionField.GetValue(model)!;
        rows[0].IsDetailsExpanded = true;
        var selectionNotifications = 0;
        void Observe(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(model.WebsitePolicyTargetSelectionSummary)) selectionNotifications++;
        }
        model.PropertyChanged += Observe;
        model.SelectAllWebsitePolicyTargets(false);
        if (model.WebsitePolicyTargetChoices.Any(choice => choice.IsSelected) ||
            model.DeviceHealthSelectedCount != 3 || !model.DeviceHealthRows.SequenceEqual(rows) ||
            !rows[0].IsDetailsExpanded || (long)revisionField.GetValue(model)! != revision ||
            selectionNotifications != 1)
            throw new Exception("Website deselection must preserve health scope, row identity and expanded details; bulk selection must notify once.");
        model.SelectAllWebsitePolicyTargets(true);
        typeof(TeacherViewModel).GetField("_lastFailedWebsiteTargets", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(model, new[] { "pc-02" });
        var failedCampusField = typeof(TeacherViewModel).GetField("_lastFailedWebsiteTargetsCampus",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        failedCampusField.SetValue(model, model.CampusId.Trim());
        model.FillFailedWebsiteTargets();
        if (!model.WebsitePolicyTargetChoices.SequenceEqual(choices) ||
            !model.SelectedWebsitePolicyStudentNames.Any() ||
            choices[0].IsSelected || !choices[1].IsSelected || choices[2].IsSelected ||
            !model.DeviceHealthRows.SequenceEqual(rows) || (long)revisionField.GetValue(model)! != revision)
            throw new Exception("Retry must select only failed devices without deleting students or invalidating health.");
        model.WebsiteTargets = "PC-01\nPC-02";
        if (model.DeviceHealthRows.Count != 2 || !ReferenceEquals(model.DeviceHealthRows[0], rows[0]))
            throw new Exception("A roster change must remove absent health devices while retaining the identities of remaining devices.");
        model.PropertyChanged -= Observe;
        model.SelectAllWebsitePolicyTargets(true);

        var snapshot = new ClassroomQuickControlTargetSnapshot(Guid.NewGuid(), "学生甲",
            DeviceControlMode.WebsiteRestriction, ClassroomQuickControlPhase.WebsiteReported, DateTimeOffset.UtcNow,
            1, null, null, "Browser verification pending");
        var item = new TeacherClassroomQuickControlDeviceItem(snapshot) { IsExpanded = true };
        if (!item.StatusText.Contains("待核对") || !item.Summary.Contains("浏览器效果待核对"))
            throw new Exception("An Agent acknowledgement cannot be presented as verified browser enforcement.");
        item.Update(snapshot with { Phase = ClassroomQuickControlPhase.WebsiteApplied });
        if (item.StatusText != "策略已应用" || !item.IsExpanded)
            throw new Exception("Verified browser state should update the existing row without collapsing it.");
        var other = snapshot with { TargetId = Guid.NewGuid(), DisplayName = "学生乙", Phase = ClassroomQuickControlPhase.WaitingForAgent };
        var session = new ClassroomQuickControlSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, [snapshot, other]);
        model.UpdateClassroomQuickControlSnapshot(session);
        var progressRows = model.ClassroomQuickControlDevices.ToArray();
        progressRows[0].IsExpanded = true;
        model.UpdateClassroomQuickControlSnapshot(session with
        {
            Devices = [snapshot with { Phase = ClassroomQuickControlPhase.WaitingForAgent },
                other with { Phase = ClassroomQuickControlPhase.WebsiteApplied }]
        });
        if (!model.ClassroomQuickControlDevices.SequenceEqual(progressRows) || !progressRows[0].IsExpanded)
            throw new Exception("Progress phase changes must not move students or collapse the row being read.");
        var beforeSelection = model.WebsitePolicyTargetChoices.Select(choice => choice.IsSelected).ToArray();
        failedCampusField.SetValue(model, "unrelated-history-campus");
        if (model.CanFillFailedWebsiteTargets)
            throw new Exception("Failed targets from another campus must not be selectable.");
        model.FillFailedWebsiteTargets();
        if (!model.WebsitePolicyTargetChoices.Select(choice => choice.IsSelected).SequenceEqual(beforeSelection))
            throw new Exception("Unrelated history changed the current student selection.");
        failedCampusField.SetValue(model, model.CampusId.Trim());
        typeof(TeacherViewModel).GetField("_lastFailedWebsiteTargets", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(model, new[] { "PC-99" });
        if (model.CanFillFailedWebsiteTargets)
            throw new Exception("Failed devices absent from the current roster must not be selectable.");
        Console.WriteLine("PASS website selection and retry preserve health scope, rows and expansion; browser verification remains distinct");
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
            viewModel.PendingClassroomHelpCount != 1 ||
            !viewModel.ClassroomInteractionNavigationLabel.Contains("1 条待回复"))
            throw new Exception("Teacher classroom help request was not shown as replyable.");

        var reply = new ClassroomEvent(1, ClassroomEventCryptography.EventPurpose, "demo", sessionId,
            Guid.NewGuid(), "PC-01", ClassroomEventSender.Teacher, ClassroomEventType.TeacherReply,
            now.AddSeconds(1), now.AddSeconds(1).Add(ClassroomEventCryptography.MaximumEventLifetime),
            null, "我马上来看。", helpEventId);
        viewModel.ApplyClassroomEvents(sessionId, [reply]);
        if (viewModel.ClassroomEventItems[0].CanReply ||
            viewModel.ClassroomEventItems[0].Status != "等待学生确认" ||
            viewModel.PendingClassroomHelpCount != 0 ||
            viewModel.ClassroomEventItems[0].ReplyMessage != "我马上来看。")
            throw new Exception("Teacher event feed did not correlate the reply with the student request.");
        var resolved = new ClassroomEvent(1, ClassroomEventCryptography.EventPurpose, "demo", sessionId,
            Guid.NewGuid(), "PC-01", ClassroomEventSender.Student, ClassroomEventType.HelpResolved,
            now.AddSeconds(2), now.AddSeconds(2).Add(ClassroomEventCryptography.MaximumEventLifetime),
            null, null, helpEventId);
        viewModel.ApplyClassroomEvents(sessionId, [resolved]);
        if (viewModel.ClassroomEventItems[0].CanReply || viewModel.ClassroomEventItems[0].Status != "已解决" ||
            viewModel.PendingClassroomHelpCount != 0)
            throw new Exception("Teacher event feed did not mark the correlated request as resolved.");
        var noticeTime = now.AddSeconds(3);
        var notice = new ClassroomEvent(1, ClassroomEventCryptography.EventPurpose, "demo", sessionId,
            Guid.NewGuid(), ClassroomEventCryptography.ClassroomNoticeTarget, ClassroomEventSender.Teacher,
            ClassroomEventType.ClassroomNotice, noticeTime,
            noticeTime.Add(ClassroomEventCryptography.MaximumEventLifetime), null, "请准备下课。", null);
        viewModel.ApplyClassroomEvents(sessionId, [notice]);
        if (viewModel.ClassroomEventItems.Count != 2 || viewModel.ClassroomEventItems[0].Title != "全班通知" ||
            viewModel.ClassroomEventItems[0].Detail != "请准备下课。")
            throw new Exception("Teacher event feed did not show a class-wide classroom notice.");
        viewModel.ResetClassroomEventFeed(null);
        if (viewModel.HasClassroomEventItems) throw new Exception("Teacher event feed survived the end of class.");
        Console.WriteLine("PASS teacher classroom inbox tracks reply, student resolution, and session end");
    }
}
