using System.Reflection;
using VeyonCampus.App;
using VeyonCampus.Core;

internal static class TeacherWorkflowChecks
{
    internal static void Run()
    {
        var vm = new TeacherViewModel();
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
}
