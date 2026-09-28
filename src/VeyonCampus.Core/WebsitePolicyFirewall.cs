using System.Runtime.Versioning;

namespace VeyonCampus.Core;

/// <summary>HTTP.sys owns the listening socket, so the application filter must be System.</summary>
[SupportedOSPlatform("windows")]
internal static class WebsitePolicyFirewall
{
    internal const string RuleName = "VeyonCampus Website Policy Agent";
    private const string Marker = "VeyonCampus Website Policy HTTP.sys v1";

    internal static void Ensure(IEnumerable<string> knownExecutables, string name = RuleName)
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        var existing = Find(policy, name);
        // Check every same-name rule before replacing any of them. Never remove an unknown rule.
        foreach (dynamic rule in existing)
        {
            var legacy = knownExecutables.Contains((string)rule.ApplicationName, StringComparer.OrdinalIgnoreCase);
            var current = (string)rule.ApplicationName == "System" && (string)rule.Description == Marker;
            if ((!legacy && !current) || (int)rule.Protocol != 6 ||
                ((string)rule.LocalPorts != "39173" && (string)rule.LocalPorts != WebsitePolicyAgent.Port.ToString()))
                throw new IOException("同名防火墙规则无法确认属于本工具，未修改该规则。");
        }
        foreach (var unused in existing) policy.Rules.Remove(name);
        dynamic added = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
        added.Name = name;
        added.Description = Marker;
        added.ApplicationName = "System";
        added.Protocol = 6; // TCP
        added.LocalPorts = WebsitePolicyAgent.Port.ToString();
        added.RemoteAddresses = "LocalSubnet";
        added.Direction = 1; // inbound
        added.Action = 1; // allow
        added.Profiles = int.MaxValue; // includes Public VM adapters
        added.Enabled = true;
        policy.Rules.Add(added);
        Verify(name);
    }

    internal static void Verify(string name = RuleName)
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        var rules = Find(policy, name);
        if (rules.Count != 1) throw new IOException("网站代理防火墙规则缺失或重复。");
        dynamic rule = rules[0];
        if ((string)rule.ApplicationName != "System" || (string)rule.Description != Marker ||
            (int)rule.Protocol != 6 || (string)rule.LocalPorts != WebsitePolicyAgent.Port.ToString() ||
            !string.Equals((string)rule.RemoteAddresses, "LocalSubnet", StringComparison.OrdinalIgnoreCase) ||
            (int)rule.Direction != 1 || (int)rule.Action != 1 ||
            (int)rule.Profiles != int.MaxValue || !(bool)rule.Enabled ||
            !string.IsNullOrEmpty((string)rule.ServiceName) || (string)rule.LocalAddresses != "*" ||
            (string)rule.RemotePorts != "*")
            throw new IOException("网站代理防火墙规则读回不符：需要 System/HTTP.sys、TCP 39174、LocalSubnet 和所有网络类别。");
    }

    private static List<object> Find(dynamic policy, string name)
    {
        var found = new List<object>();
        foreach (dynamic rule in policy.Rules)
            if (string.Equals((string)rule.Name, name, StringComparison.OrdinalIgnoreCase)) found.Add(rule);
        return found;
    }
}
