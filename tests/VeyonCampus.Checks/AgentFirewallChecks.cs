using System.Reflection;
using System.Runtime.Versioning;
using VeyonCampus.Core;

internal static class AgentFirewallChecks
{
    [SupportedOSPlatform("windows")]
    internal static void Run()
    {
        var name = "VeyonCampus firewall fixture " + Guid.NewGuid().ToString("N");
        var executable = Path.Combine(AppContext.BaseDirectory, "VeyonCampus.Checks.exe");
        var type = typeof(WebsitePolicyAgentInstaller).Assembly.GetType("VeyonCampus.Core.WebsitePolicyFirewall")!;
        void Ensure() => type.GetMethod("Ensure", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new[] { executable }, name]);
        void Verify() => type.GetMethod("Verify", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [name]);
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        try
        {
            Ensure();
            Verify();
            Console.WriteLine("PASS fresh System HTTP.sys rule with TCP/port/subnet/profile readback");
            Ensure();
            Console.WriteLine("PASS repeat deployment recognizes managed System rule");
            dynamic rule = policy.Rules.Item(name);
            rule.ApplicationName = executable;
            rule.Description = "legacy agent rule";
            rule.Profiles = 3;
            try { Verify(); throw new Exception("Legacy rule unexpectedly passed verification."); }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException) { }
            Ensure();
            Console.WriteLine("PASS legacy executable rule rejected by verification and migrated");
            rule = policy.Rules.Item(name);
            rule.ApplicationName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
            try { Ensure(); throw new Exception("Unknown rule unexpectedly replaced."); }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException) { }
            if (!((string)policy.Rules.Item(name).ApplicationName).EndsWith("notepad.exe"))
                throw new Exception("Unknown rule changed.");
            Console.WriteLine("PASS unknown same-name rule preserved");
        }
        finally { policy.Rules.Remove(name); }
    }
}
