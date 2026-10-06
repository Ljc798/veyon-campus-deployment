using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VeyonCampus.App;
using VeyonCampus.Core;
using VeyonCampus.Checks;

if (args is ["--teacher-workflow-fixtures"])
{
    TeacherWorkflowChecks.Run();
    return;
}

if (args is ["--teacher-heartbeat-fixtures"])
{
    TeacherHeartbeatChecks.Run();
    Console.WriteLine("PASS Teacher 心跳携带最新签名版本并仅在验签后报告新版本");
    return;
}

if (args is ["--worker-protocol-fixtures"])
{
    WorkerProtocolChecks.Run();
    Console.WriteLine("PASS 提权 Worker 有界协议与字段校验");
    return;
}

if (args is ["--worker-package-fixtures", var workerPackageDirectory, var workerPackageRole])
{
    WorkerPackageChecks.Run(workerPackageDirectory, workerPackageRole);
    Console.WriteLine($"PASS {workerPackageRole} Worker 发布目录与版本匹配");
    return;
}

if (args is ["--agent-firewall-fixtures"])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows firewall fixtures only.");
    AgentFirewallChecks.Run();
    return;
}

if (args is ["--agent-installation-fixtures"])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows ACL fixtures only.");
    AgentInstallationChecks.Run();
    return;
}

if (args is ["--agent-removal-preflight"])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows task fixtures only.");
    object? InvokeAgentInstaller(string methodName) =>
        typeof(WebsitePolicyAgentInstaller)
            .GetMethod(methodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, null);

    var task = InvokeAgentInstaller("ReadTaskForRemoval");
    var rawConfigs = (System.Collections.IEnumerable)(InvokeAgentInstaller("FindAgentConfigurationFiles")
                    ?? Array.Empty<object>());
    var campuses = new HashSet<string>(StringComparer.Ordinal);
    var configurationCount = 0;
    foreach (var item in rawConfigs)
    {
        if (item is null) continue;
        configurationCount++;
        var config = item.GetType().GetProperty("Config")?.GetValue(item);
        var campus = config?.GetType().GetProperty("CampusId")?.GetValue(config) as string;
        if (!string.IsNullOrWhiteSpace(campus)) campuses.Add(campus);
    }
    var owner = WebsitePolicyRegistryStore.ReadCampusForAgentRemoval();
    Console.WriteLine($"TaskPresent={task is not null}; Configurations={configurationCount}; " +
                      $"DistinctCampuses={campuses.Count}; OwnershipRecordPresent={owner is not null}");
    return;
}

// A portable child process for launcher tests; never enters deployment checks.
if (args is ["--process-fixture", var fixtureMode, .. var fixtureArguments])
{
    switch (fixtureMode)
    {
        case "timeout":
            Thread.Sleep(TimeSpan.FromSeconds(30));
            break;
        case "timeout-marker" when fixtureArguments is [var markerPath]:
            Thread.Sleep(TimeSpan.FromSeconds(1));
            File.WriteAllText(markerPath, "completed");
            break;
        case "argument-echo" when fixtureArguments is [var argument, var resultPath]:
            File.WriteAllText(resultPath, argument, new UTF8Encoding(false));
            Console.WriteLine(Environment.CurrentDirectory);
            break;
        case "large-output":
            Console.WriteLine(new string('O', 128 * 1024));
            Console.Error.WriteLine(new string('E', 128 * 1024));
            break;
        case "stdin-echo":
            Console.Write(Console.In.ReadToEnd());
            break;
        case "task-lease-hold" when fixtureArguments is [var readyPath]:
        {
            using var lease = new NamedPipeTaskLease();
            if (!lease.TryAcquire(out _))
            {
                Environment.ExitCode = 3;
                break;
            }
            File.WriteAllText(readyPath, "locked");
            Thread.Sleep(TimeSpan.FromSeconds(30));
            break;
        }
        default:
            Console.WriteLine("fixture-output");
            Environment.ExitCode = fixtureMode == "fail" ? 7 : 0;
            break;
    }
    return;
}

var passed = 0;
void Check(string name, Action check)
{
    check();
    Console.WriteLine($"PASS {name}");
    passed++;
}
async Task CheckAsync(string name, Func<Task> check)
{
    await check();
    Console.WriteLine($"PASS {name}");
    passed++;
}
void Expect(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
void Reject(Action action)
{
    try { action(); } catch (Exception ex) when (ex is InvalidDataException or IOException) { return; }
    throw new Exception("Invalid input was accepted");
}
void CheckStudentSetupCleanup()
{
    var temporary = Path.Combine(TestPath.CanonicalTempRoot(), "veyon-cleanup-check-" + Guid.NewGuid().ToString("N"));
    try
    {
        foreach (var (appHostName, shouldClean) in new[]
                 {
                     ("VeyonCampus.StudentSetup.exe", true),
                     ("VeyonCampus.exe", true),
                     ("VeyonCampus.Teacher.exe", false)
                 })
        {
            var bundle = Path.Combine(temporary, Path.GetFileNameWithoutExtension(appHostName));
            var agentDirectory = Path.Combine(bundle, "WebsitePolicyAgent");
            Directory.CreateDirectory(agentDirectory);
            var appPath = Path.Combine(bundle, appHostName);
            var agentPath = Path.Combine(agentDirectory, "VeyonCampus.Agent.exe");
            var extraPath = Path.Combine(bundle, "school-package-note.txt");
            File.WriteAllText(appPath, "portable gui");
            File.WriteAllText(agentPath, "portable cleanup helper");
            File.WriteAllText(extraPath, "keep this unlisted file");
            File.WriteAllText(Path.Combine(bundle, StudentSetupBundleCleanup.MarkerFileName),
                "VeyonCampusStudentSetupBundle-v1");
            var files = new[] { appPath, agentPath }.Select(path => new StudentSetupBundleFile(
                Path.GetRelativePath(bundle, path).Replace('\\', '/'),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();
            File.WriteAllText(Path.Combine(bundle, StudentSetupBundleCleanup.ManifestFileName),
                JsonSerializer.Serialize(new StudentSetupBundleManifest(1, "VeyonCampus.StudentSetup", files),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

            Expect(StudentSetupBundleCleanup.WaitAndRemove(int.MaxValue, 0, bundle) == shouldClean);
            Expect(File.Exists(appPath) == !shouldClean && File.Exists(agentPath) == !shouldClean &&
                   File.Exists(extraPath) && Directory.Exists(bundle));
        }
    }
    finally
    {
        if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
    }
}
void CheckTaskAclDescriptors()
{
    Expect(WebsitePolicyAgentInstaller.IsRestrictedTaskSecurityDescriptor(
        "O:SYG:SYD:P(A;;GA;;;SY)(A;;GA;;;BA)"));
    Expect(WebsitePolicyAgentInstaller.IsRestrictedTaskSecurityDescriptor(
        "O:SYG:SYD:P(A;;FA;;;BA)(A;;FA;;;SY)"));
    Expect(!WebsitePolicyAgentInstaller.IsRestrictedTaskSecurityDescriptor(
        "D:(A;;GA;;;SY)(A;;GA;;;BA)"));
    Expect(!WebsitePolicyAgentInstaller.IsRestrictedTaskSecurityDescriptor(
        "O:SYG:SYD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;BU)"));
    Expect(!WebsitePolicyAgentInstaller.IsRestrictedTaskSecurityDescriptor(
        "O:SYG:SYD:P(A;;GA;;;SY)(A;;GA;;;S-1-5-18)"));
}
void CheckWebsitePolicyExpirations()
{
    using var key = RSA.Create(2048);
    var publicPem = key.ExportSubjectPublicKeyInfoPem();
    var issued = DateTimeOffset.UtcNow;
    var timed = WebsitePolicyCompiler.Create("campus-demo", 8, WebsitePolicyMode.Blocklist,
        new[] { "temporary.example" }, issued, issued.AddMinutes(60));
    Expect(timed.SchemaVersion == 2 && timed.ExpiresUtc == issued.AddMinutes(60));
    var signed = WebsitePolicyCryptography.Sign(timed, key);
    var verified = WebsitePolicyCryptography.Verify(signed, publicPem, "campus-demo", 7);
    Expect(verified.ExpiresUtc == timed.ExpiresUtc);
    Reject(() => WebsitePolicyCryptography.Verify(signed, publicPem, "campus-demo", 8));
    Reject(() => WebsitePolicyCompiler.Create("campus-demo", 9, WebsitePolicyMode.Disabled,
        Array.Empty<string>(), issued, issued.AddMinutes(10)));
    Reject(() => WebsitePolicyCompiler.Create("campus-demo", 9, WebsitePolicyMode.Blocklist,
        new[] { "temporary.example" }, issued, issued.AddHours(25)));
    var expired = WebsitePolicyCompiler.Create("campus-demo", 10, WebsitePolicyMode.Blocklist,
        new[] { "temporary.example" }, issued.AddHours(-2), issued.AddHours(-1));
    var signedExpired = WebsitePolicyCryptography.Sign(expired, key);
    Reject(() => WebsitePolicyCryptography.Verify(signedExpired, publicPem, "campus-demo", 0));
}
void CheckWebsitePolicyHistory()
{
    var temporary = Path.Combine(TestPath.CanonicalTempRoot(), "veyon-policy-history-check-" + Guid.NewGuid().ToString("N"));
    try
    {
        for (var revision = 1; revision <= 51; revision++)
        {
            var entry = new WebsitePolicyPushHistoryEntry(DateTimeOffset.UtcNow, "campus-demo", revision,
                WebsitePolicyMode.Blocklist, DateTimeOffset.UtcNow.AddMinutes(45),
                [new WebsitePolicyPushResult("PC-01", false, "timeout\r\nretry", NeedsReview: true)]);
            WebsitePolicyPushHistoryStore.Append(entry, temporary);
        }
        var latest = WebsitePolicyPushHistoryStore.ReadLatest(temporary)
                     ?? throw new Exception("Push history did not return the latest entry.");
        Expect(latest.Revision == 51 && latest.Results.Count == 1 && latest.Results[0].NeedsReview &&
               latest.Results[0].Detail == "timeoutretry");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(temporary, "push-history.json")));
        Expect(json.RootElement.GetArrayLength() == WebsitePolicyPushHistoryStore.MaximumRuns &&
               json.RootElement[0].GetProperty("revision").GetInt64() == 2L &&
               !File.ReadAllText(Path.Combine(temporary, "push-history.json")).Contains("secret.example", StringComparison.Ordinal));
    }
    finally
    {
        if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
    }
}
void CheckApplicationPolicyHistory()
{
    var temporary = Path.Combine(TestPath.CanonicalTempRoot(), "veyon-app-policy-history-check-" + Guid.NewGuid().ToString("N"));
    try
    {
        var entry = new ApplicationPolicyPushHistoryEntry(DateTimeOffset.UtcNow, "campus-demo", 9,
            ApplicationPolicyMode.Audit, 2, 3,
            [new ApplicationPolicyDeliveryResult("PC-01", true, "accepted\r\nrevision=9")]);
        ApplicationPolicyPushHistoryStore.Append(entry, temporary);
        var latest = ApplicationPolicyPushHistoryStore.ReadLatest(temporary)
                     ?? throw new Exception("Application policy history did not return the saved entry.");
        Expect(latest.Revision == 9 && latest.Mode == ApplicationPolicyMode.Audit && latest.RuleCount == 2 &&
               latest.StudentCount == 3 && latest.Results.Single().Detail == "acceptedrevision=9");
        var text = File.ReadAllText(Path.Combine(temporary, "push-history.json"));
        Expect(!text.Contains("PublisherName", StringComparison.Ordinal) &&
               !text.Contains("secret.example", StringComparison.Ordinal));
    }
    finally
    {
        if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
    }
}
void CheckAgentRemovalDecisions()
{
    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
    var resolve = typeof(WebsitePolicyAgentInstaller).GetMethod("ResolveCampusForRemoval", flags)
                  ?? throw new Exception("Removal owner selector not found.");
    string? Resolve(string? registry, string? task, params string[] configs) =>
        resolve.Invoke(null, [registry, task, configs]) as string;
    Expect(Resolve("registry-campus", "task-campus", "config-campus") == "registry-campus");
    Expect(Resolve(null, "task-campus", "config-campus") == "task-campus");
    Expect(Resolve(null, null, "one-campus", "one-campus") == "one-campus");
    Expect(Resolve(null, null, "campus-a", "campus-b") is null);

    var missingTask = typeof(WebsitePolicyAgentInstaller).GetMethod("IsMissingScheduledTaskError", flags)
                      ?? throw new Exception("Missing-task classifier not found.");
    bool IsMissing(uint code) => (bool)missingTask.Invoke(null, [unchecked((int)code)])!;
    Expect(IsMissing(0x80070002) && IsMissing(0x80070003) && IsMissing(0x8004130F));
    Expect(!IsMissing(0x80070005));

    var missingTaskException = typeof(WebsitePolicyAgentInstaller).GetMethod("IsMissingScheduledTaskException", flags)
                               ?? throw new Exception("Missing-task exception classifier not found.");
    bool IsMissingException(Exception exception) =>
        (bool)missingTaskException.Invoke(null, [exception])!;
    Expect(IsMissingException(new FileNotFoundException()) &&
           IsMissingException(new DirectoryNotFoundException()) &&
           IsMissingException(new System.Runtime.InteropServices.COMException(
               "task absent", unchecked((int)0x80070003))));
    Expect(!IsMissingException(new UnauthorizedAccessException()));
}
void CheckWebsitePolicyApplyAcknowledgement()
{
    var acknowledgement = WebsitePolicyAgent.PolicyAppliedAcknowledgement;
    Expect(acknowledgement.StartsWith("policy applied;", StringComparison.Ordinal) &&
           acknowledgement.Contains("edge://restart", StringComparison.Ordinal) &&
           acknowledgement.Contains("chrome://restart", StringComparison.Ordinal) &&
           acknowledgement.Contains("不会强制关闭浏览器", StringComparison.Ordinal));
}
if (args is ["--student-setup-fixtures"])
{
    Check("学生工具新旧入口清理及教师包拒绝", CheckStudentSetupCleanup);
    Check("网站代理任务 ACL 只允许 SYSTEM 和管理员", CheckTaskAclDescriptors);
    Check("网站策略卸载处理缺失任务及多校区孤立配置", CheckAgentRemovalDecisions);
    Check("课堂策略到期签名、重放与时长上限", CheckWebsitePolicyExpirations);
    Check("教师逐台推送结果本机保留、脱敏并限制为最近 50 次", CheckWebsitePolicyHistory);
    Check("网站策略确认明确提示 Edge/Chrome 刷新方式", CheckWebsitePolicyApplyAcknowledgement);
    return;
}
Check("1–150 编号与 99/100 边界", () =>
{
    foreach (var pair in new[] { ("1", "PC-01"), ("9", "PC-09"), ("10", "PC-10"), ("99", "PC-99"),
                                 ("100", "PC-100"), ("149", "PC-149"), ("150", "PC-150") })
        Expect(MachineNaming.CreateName("PC-", pair.Item1) == pair.Item2);
    foreach (var number in new[] { "0", "151", "-1", "1.0", "０３", "999999999999999999999", "", " 3" })
        Reject(() => MachineNaming.CreateName("PC-", number));
    Expect(MachineNaming.CreateName("ABCDEFGHIJKLM", "1").Length == 15 &&
           MachineNaming.CreateName("ABCDEFGHIJKLM", "99").Length == 15);
    Reject(() => MachineNaming.CreateName("ABCDEFGHIJKLM", "100"));
    Expect(MachineNaming.CreateName("PC-", "03") == "PC-03");
    foreach (var prefix in new[] { "../", "PC_", "-PC", "123", "ABCDEFGHIJKLMN" })
        Reject(() => MachineNaming.CreateName(prefix, "1"));
});
Check("提权 Worker 协议：请求边界、重复/未知字段、密码缓冲区及结果 ID", WorkerProtocolChecks.Run);
Check("Veyon 固定发布资产、校区密钥标识和服务状态解析", () =>
{
    Expect(VeyonInstallerTrust.MatchesPinnedArtifact(VeyonInstallerTrust.FileName,
        VeyonInstallerTrust.FileSize, VeyonInstallerTrust.Sha256));
    Expect(!VeyonInstallerTrust.MatchesPinnedArtifact("veyon-4.11.2-win64-setup.exe",
        VeyonInstallerTrust.FileSize, VeyonInstallerTrust.Sha256));
    Expect(!VeyonInstallerTrust.MatchesPinnedArtifact(VeyonInstallerTrust.FileName,
        VeyonInstallerTrust.FileSize - 1, VeyonInstallerTrust.Sha256));
    Expect(!VeyonInstallerTrust.MatchesPinnedArtifact(VeyonInstallerTrust.FileName,
        VeyonInstallerTrust.FileSize, new string('0', 64)));
    Expect(VeyonFacts.IsSupportedVersionDetail("版本 4.11.2.0。") &&
           VeyonFacts.IsSupportedVersionDetail("版本 4.11.2。") &&
           !VeyonFacts.IsSupportedVersionDetail("版本 4.11.2.1。") &&
           !VeyonFacts.IsSupportedVersionDetail("版本 4.11.3.0。") &&
           !VeyonFacts.IsSupportedVersionDetail(null));
    var keyId = VeyonAuthKeyId.ForCampus("campus-demo_01");
    Expect(keyId.Length == 33 && keyId.All(c => c is >= 'A' and <= 'P') &&
           VeyonAuthKeyId.PublicKeyForCampus("campus-demo_01") == keyId + "/public");
    Expect(VeyonAuthKeyId.ForCampus("campus-demo_01") == keyId &&
           VeyonAuthKeyId.ForCampus("campus-demo-01") != keyId);
    var unicodeKeyId = VeyonAuthKeyId.ForCampus("校区一");
    Expect(unicodeKeyId.Length == 33 && unicodeKeyId.All(c => c is >= 'A' and <= 'P'));
    Reject(() => VeyonAuthKeyId.ForCampus(" 校区一"));
    Expect(WindowsServiceState.Parse("SERVICE_NAME: VeyonService\n        STATE              : 4  RUNNING") == WindowsServiceState.Running);
    Expect(WindowsServiceState.Parse("服务名: VeyonService\n        状态              : 1  已停止") == WindowsServiceState.Stopped);
    Expect(WindowsServiceState.Parse("SERVICE_NAME: VeyonService\n        STATE              : 3  STOP_PENDING") == WindowsServiceState.StopPending);
    Expect(WindowsServiceState.Parse("SERVICE_NAME: VeyonService\n        TYPE               : 10  WIN32_OWN_PROCESS") is null);
});
Check("应用发布签名、SemVer、摘要验证和自更新失败回滚", ApplicationReleaseChecks.Run);
Check("应用策略独立用途签名、学生 SID、EXE 黑名单基线及恢复组件保护", ApplicationPolicyChecks.Run);
Check("应用策略事务恢复、离线到期、防重放和外部策略冲突保护", ApplicationPolicyRuntimeChecks.Run);
Check("应用策略逐台结果历史有界存储且不保存策略规则", CheckApplicationPolicyHistory);
Check("手机策略预设校验、配对凭据哈希/撤销、审计存储和签名状态协议", TeacherMobileControlChecks.Run);
await CheckAsync("手机控制 API：教师批准配对、同源、配置校区过滤、防重放及撤销", MobileControlApiChecks.RunAsync);
Check("Student 更新命令校区/开发者双重签名、私网限制和重放保护", StudentApplicationUpdateChecks.Run);
Check("云端部署包文件名采用校区名称且不附加电脑名前缀", () =>
{
    var packageId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    var fileName = DeploymentPackageStorageNaming.CreateFileName(" 智学前程-test11 ", packageId);
    Expect(fileName == "智学前程-test11-00112233445566778899aabbccddeeff.zip");
    Expect(DeploymentPackageStorageNaming.CreateObjectKey("智学前程-test11", packageId) ==
           "deployment-packages/v3/智学前程-test11-00112233445566778899aabbccddeeff.zip");
    Expect(DeploymentPackageStorageNaming.CreateObjectKey("智学前程-test11", packageId, 4) ==
           "deployment-packages/v4/智学前程-test11-00112233445566778899aabbccddeeff.zip");
    try
    {
        DeploymentPackageStorageNaming.CreateObjectKey("校区", packageId, 5);
        throw new Exception("Unsupported package schema version was accepted");
    }
    catch (ArgumentOutOfRangeException) { }
    Expect(DeploymentPackageStorageNaming.CreateFileName("学校/东区", packageId) ==
           "学校-东区-00112233445566778899aabbccddeeff.zip");
    Expect(DeploymentPackageStorageNaming.CreateFileName("...", packageId).StartsWith("campus-", StringComparison.Ordinal));
    var package = new DeploymentPackageCatalogEntry(packageId, null, "智学前程-test11", "智学前程-test11", "PC-",
        3, "windows", "x64", fileName, 10, new string('A', 64), 0, DateTimeOffset.UtcNow, true);
    Expect(!package.Summary.Contains("PC-", StringComparison.Ordinal));
});
Check("Teacher 校区心跳按 UTC+8 去重且只发送最小聚合字段", TeacherHeartbeatChecks.Run);
Check("Veyon 端点隔离：学生安装排除 Master，教师安装包含 Master", () =>
{
    var studentArguments = WindowsVeyonAdapter.BuildInstallerArguments(isTeacher: false);
    var teacherArguments = WindowsVeyonAdapter.BuildInstallerArguments(isTeacher: true);
    Expect(studentArguments.Contains("/NoMaster") && !teacherArguments.Contains("/NoMaster") &&
           studentArguments.Contains("/S") && teacherArguments.Contains("/S"));
});
Check("免费网站策略：域名规范化、黑白名单编译和签名防伪/重放", () =>
{
    CheckTaskAclDescriptors();

    var block = WebsitePolicyCompiler.Create("campus-demo", 1, WebsitePolicyMode.Blocklist,
        new[] { " bad.example ", "https://blocked.example/", "BAD.example", "münich.example" },
        DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
    var blockValues = WebsitePolicyCompiler.Compile(block);
    Expect(block.Domains.Count == 3 && block.Domains.Contains("bad.example") &&
           block.Domains.Contains("blocked.example") && block.Domains.Any(x => x.StartsWith("xn--", StringComparison.Ordinal)) &&
           blockValues.Blocklist.SequenceEqual(block.Domains) && blockValues.Allowlist.Count == 0);

    var allow = WebsitePolicyCompiler.Create("campus-demo", 2, WebsitePolicyMode.Allowlist,
        new[] { "school.example", "intranet.example" });
    var allowValues = WebsitePolicyCompiler.Compile(allow);
    Expect(allowValues.Blocklist.SequenceEqual(new[] { "*" }) &&
           allowValues.Allowlist.SequenceEqual(allow.Domains));
    Expect(WebsitePolicyCompiler.Compile(WebsitePolicyCompiler.Create("campus-demo", 3,
        WebsitePolicyMode.Disabled, Array.Empty<string>())).Blocklist.Count == 0);

    foreach (var invalid in new[] { "*.example.com", "https://example.com/path", "bad.example:8080",
                 "javascript:alert(1)", "127.0.0.1", "-bad.example", "bad..example" })
        Reject(() => WebsitePolicyCompiler.NormalizeDomains(new[] { invalid }));
    Reject(() => WebsitePolicyCompiler.Create("campus-demo", 1, WebsitePolicyMode.Allowlist, Array.Empty<string>()));
    Reject(() => WebsitePolicyCompiler.NormalizeDomains(Enumerable.Range(0, 1001).Select(i => $"s{i}.example")));

    using var teacherKey = RSA.Create(2048);
    var publicPem = teacherKey.ExportSubjectPublicKeyInfoPem();
    var issued = DateTimeOffset.UtcNow;
    var timed = WebsitePolicyCompiler.Create("campus-demo", 3, WebsitePolicyMode.Blocklist,
        new[] { "temporary.example" }, issued, issued.AddMinutes(60));
    Expect(timed.SchemaVersion == 2 && timed.ExpiresUtc == issued.AddMinutes(60));
    var timedEnvelope = WebsitePolicyCryptography.Sign(timed, teacherKey);
    var verifiedTimed = WebsitePolicyCryptography.Verify(timedEnvelope, publicPem, "campus-demo", 2);
    Expect(verifiedTimed.ExpiresUtc == timed.ExpiresUtc && verifiedTimed.SchemaVersion == 2);
    Reject(() => WebsitePolicyCryptography.Verify(timedEnvelope, publicPem, "campus-demo", 3));
    Reject(() => WebsitePolicyCompiler.Create("campus-demo", 4, WebsitePolicyMode.Disabled,
        Array.Empty<string>(), issued, issued.AddMinutes(10)));
    Reject(() => WebsitePolicyCompiler.Create("campus-demo", 4, WebsitePolicyMode.Blocklist,
        new[] { "temporary.example" }, issued, issued.AddHours(25)));
    var expired = WebsitePolicyCompiler.Create("campus-demo", 5, WebsitePolicyMode.Blocklist,
        new[] { "temporary.example" }, issued.AddHours(-2), issued.AddHours(-1));
    var expiredEnvelope = WebsitePolicyCryptography.Sign(expired, teacherKey);
    Reject(() => WebsitePolicyCryptography.Verify(expiredEnvelope, publicPem, "campus-demo", 0));
    var envelopeJson = WebsitePolicyCryptography.Sign(allow, teacherKey);
    var verified = WebsitePolicyCryptography.Verify(envelopeJson, publicPem, "campus-demo", 1);
    Expect(verified.Revision == 2 && verified.Domains.SequenceEqual(allow.Domains));
    Reject(() => WebsitePolicyCryptography.Verify(envelopeJson, publicPem, "other-campus", 1));
    Reject(() => WebsitePolicyCryptography.Verify(envelopeJson, publicPem, "campus-demo", 2));
    var envelope = JsonSerializer.Deserialize<SignedWebsitePolicy>(envelopeJson)!;
    var tamperedPayload = Convert.FromBase64String(envelope.Payload);
    tamperedPayload[^1] ^= 1;
    var tampered = JsonSerializer.Serialize(envelope with { Payload = Convert.ToBase64String(tamperedPayload) });
    Reject(() => WebsitePolicyCryptography.Verify(tampered, publicPem, "campus-demo", 1));
    foreach (var malformed in new[] { "{}", "null", "{\"Payload\":null,\"Signature\":null}",
                 "{\"Payload\":\"\",\"Signature\":\"\"}" })
        Reject(() => WebsitePolicyCryptography.Verify(malformed, publicPem, "campus-demo", 0));
    var unknownField = envelopeJson.TrimEnd('}') + ",\"unexpected\":true}";
    Reject(() => WebsitePolicyCryptography.Verify(unknownField, publicPem, "campus-demo", 0));
    var duplicateField = envelopeJson.TrimEnd('}') + ",\"Payload\":\"AAAA\"}";
    Reject(() => WebsitePolicyCryptography.Verify(duplicateField, publicPem, "campus-demo", 0));
    var future = WebsitePolicyCompiler.Create("campus-demo", 6, WebsitePolicyMode.Blocklist,
        new[] { "future.example" }, DateTimeOffset.UtcNow.AddHours(1));
    Reject(() => WebsitePolicyCryptography.Verify(WebsitePolicyCryptography.Sign(future, teacherKey),
        publicPem, "campus-demo", 0));
    Reject(() => WebsitePolicyCryptography.Sign(allow with { SchemaVersion = 99 }, teacherKey));
});
Check("网站策略推送目标校验与去重", () =>
{
    var targets = WebsitePolicyTransport.NormalizeTargets(new[] { " pc-01 ", "192.168.1.20", "PC-01", "" });
    Expect(targets.Count == 2 && targets.Any(target => target.Equals("PC-01", StringComparison.OrdinalIgnoreCase)) &&
           targets.Contains("192.168.1.20"));
    foreach (var invalid in new[] { "https://pc-01", "pc-01/path", "user@pc-01", "bad host", "" })
        Reject(() => WebsitePolicyTransport.NormalizeTargets(new[] { invalid }));
    Reject(() => WebsitePolicyTransport.NormalizeTargets(Enumerable.Range(1, 151).Select(i => $"pc-{i}.school")));
    WebsitePolicySigningKeyStore.ValidateCampusId("campus_demo-01");
    WebsitePolicySigningKeyStore.ValidateCampusId("校园");
    Reject(() => WebsitePolicySigningKeyStore.ValidateCampusId(" 校园"));
});
Check("网站策略确认明确提示 Edge/Chrome 刷新方式", CheckWebsitePolicyApplyAcknowledgement);
Check("机房 150 条唯一清单和起始边界", () =>
{
    var names = MachineNaming.CreateRange("A-PC-", "1", "150");
    var expectedNames = Enumerable.Range(1, 150).Select(number => $"A-PC-{number:D2}").ToArray();
    Expect(names.Count == 150 && names.Distinct().Count() == 150);
    Expect(names.SequenceEqual(expectedNames));
    Expect(names[0] == "A-PC-01" && names[98] == "A-PC-99" && names[99] == "A-PC-100" && names[^1] == "A-PC-150");
    Expect(MachineNaming.CreateRange("PC-", "149", "2").Count == 2);
    Reject(() => MachineNaming.CreateRange("PC-", "149", "3"));
    Reject(() => MachineNaming.CreateRange("PC-", "1", "151"));
    Reject(() => MachineNaming.CreateRange("ABCDEFGHIJKLM", "1", "150"));
});
Check("Veyon 主机名/IP 覆盖规范化并拒绝 URL、端口和无效 IPv4", () =>
{
    Expect(VeyonHostAddress.NormalizeOverride(null) == "");
    Expect(VeyonHostAddress.NormalizeOverride("192.168.001.010") == "192.168.1.10");
    Expect(VeyonHostAddress.NormalizeOverride("2001:0db8:0:0::1") == "2001:db8::1");
    Expect(VeyonHostAddress.NormalizeOverride("机房.example") == "xn--7out4i.example");
    foreach (var invalid in new[] { "https://pc-01", "pc-01:5900", "256.1.1.1", "10.1", "pc name", " pc-01" })
        Reject(() => VeyonHostAddress.NormalizeOverride(invalid));
});
Check("Veyon 地点导入预览复用地点、跳过重复项并拒绝内部冲突", () =>
{
    var computers = new[]
    {
        new VeyonNetworkComputer("PC-01", "192.168.1.10", "张三"),
        new VeyonNetworkComputer("PC-02", "192.168.1.11", "李四")
    };
    var emptyPreview = VeyonNetworkObjectDirectory.BuildImportPreview("三楼机房", computers, []);
    Expect(emptyPreview.Conflicts.Count == 0 && !emptyPreview.LocationExists &&
           emptyPreview.ComputersToAdd.Count == 2 && emptyPreview.SkippedComputers.Count == 0);
    var existingComputer = new VeyonNetworkObject("computer", "已有电脑", "192.168.1.10", "", "旧机房");
    var existingLocation = new VeyonNetworkObject("location", "三楼机房", "", "", "");
    var existingPreview = VeyonNetworkObjectDirectory.BuildImportPreview("三楼机房", computers,
        [existingComputer, existingLocation]);
    Expect(existingPreview.Conflicts.Count == 0 && existingPreview.LocationExists &&
           existingPreview.ComputersToAdd.SequenceEqual([computers[1]]) &&
           existingPreview.SkippedComputers.Count == 1 &&
           existingPreview.SkippedComputers[0].Contains("192.168.1.10", StringComparison.Ordinal));
    var sameDisplayName = new VeyonNetworkObject("computer", "张三", "192.168.1.99", "", "旧机房");
    var preservedName = VeyonNetworkObjectDirectory.BuildImportPreview("三楼机房", computers,
        [sameDisplayName]);
    Expect(preservedName.SkippedComputers.Count == 1 && preservedName.ComputersToAdd.Count == 1);
    var specialName = new VeyonNetworkComputer("PC-03", "pc-03.school.local", "七年级; \"一班\"");
    var specialPreview = VeyonNetworkObjectDirectory.BuildImportPreview("三楼;\"机房\"", [specialName], []);
    Expect(specialPreview.Conflicts.Count == 0 && specialPreview.ComputersToAdd.Single().DisplayName == specialName.DisplayName);
    var renamedDisplay = specialName with { StudentName = "新显示名" };
    Expect(renamedDisplay.ComputerName == "PC-03" && renamedDisplay.Host == "pc-03.school.local" &&
           renamedDisplay.DisplayName == "新显示名");
    var encoded = VeyonNetworkObjectDirectory.FormatComputerImportRecord(specialName);
    Expect(VeyonNetworkObjectDirectory.ParseExportRecord(encoded).SequenceEqual(
        [specialName.DisplayName, specialName.Host, ""]));
    var exportRow = string.Join('\u001f', "computer", specialName.DisplayName, specialName.Host, "", "三楼;\"机房\"");
    Expect(VeyonNetworkObjectDirectory.ParseExportRecord(exportRow, 5).SequenceEqual(
        ["computer", specialName.DisplayName, specialName.Host, "", "三楼;\"机房\""]));
    var duplicateDisplay = computers[1] with { StudentName = "张三" };
    Expect(VeyonNetworkObjectDirectory.FindImportConflicts("新机房", [computers[0], duplicateDisplay], []).
        Any(message => message.Contains("显示名称", StringComparison.Ordinal)));
    var duplicateHost = computers[1] with { Host = computers[0].Host };
    Expect(VeyonNetworkObjectDirectory.FindImportConflicts("新机房", [computers[0], duplicateHost], []).
        Any(message => message.Contains("主机名/IP", StringComparison.Ordinal)));
    var repeatedRoomPrefix = new VeyonNetworkComputer("PC-01", "PC-01", "");
    var priorRoomComputer = new VeyonNetworkObject("computer", "PC-01", "PC-01", "", "一楼机房");
    var repeatedPrefixPreview = VeyonNetworkObjectDirectory.BuildImportPreview("二楼机房",
        [repeatedRoomPrefix], [priorRoomComputer]);
    Expect(repeatedPrefixPreview.Conflicts.Count == 0 && repeatedPrefixPreview.ComputersToAdd.Count == 0 &&
           repeatedPrefixPreview.SkippedComputers.Single().Contains("与显示名均已存在，保留已有项", StringComparison.Ordinal));
});
Check("本机校区/机房档案稳定 ID、多机房持久化与 150 台上限", () =>
{
    var directory = Path.Combine(TestPath.CanonicalTempRoot(), "veyon-campus-directory-check-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var path = Path.Combine(directory, "campus-directory.json");
        var store = new TeacherCampusDirectoryStore(path);
        Expect(store.Load().Count == 0);
        var campusId = Guid.NewGuid();
        var firstRoomId = Guid.NewGuid();
        var secondRoomId = Guid.NewGuid();
        var firstRoom = new TeacherRoomProfile(firstRoomId, "三楼机房", "PC-", 1, 150,
            ["192.168.1.10", "", "pc-03.school.local"]);
        var secondRoom = new TeacherRoomProfile(secondRoomId, "四楼机房", "LAB-", 3, 2);
        var firstCampus = new TeacherCampusProfile(campusId, "同名校区", [firstRoom, secondRoom]);
        var secondCampus = new TeacherCampusProfile(Guid.NewGuid(), "同名校区", []);
        store.Save([firstCampus, secondCampus]);

        var loaded = store.Load();
        Expect(loaded.Count == 2 && loaded[0].ProfileId == campusId &&
               loaded[0].ProfileId != loaded[1].ProfileId && loaded[0].DisplayName == loaded[1].DisplayName &&
               loaded[0].Rooms.Count == 2 && loaded[0].Rooms[0].RoomId == firstRoomId &&
               loaded[0].Rooms[0].HostOverrides!.SequenceEqual(["192.168.1.10", "", "pc-03.school.local"]) &&
               loaded[0].Rooms[0].ComputerRangeLabel == "PC-01–PC-150 · 150 台" &&
               loaded[0].Rooms[1].RoomId == secondRoomId);

        var savedText = File.ReadAllText(path);
        Expect(!savedText.Contains("张三", StringComparison.Ordinal) &&
               !savedText.Contains("studentRoster", StringComparison.OrdinalIgnoreCase));
        var originalBytes = File.ReadAllBytes(path);
        var overLimitRoom = firstRoom with { ComputerCount = 151 };
        Reject(() => store.Save([firstCampus with { Rooms = [overLimitRoom, secondRoom] }, secondCampus]));
        Expect(File.ReadAllBytes(path).SequenceEqual(originalBytes));
        Reject(() => store.Save([firstCampus, firstCampus]));
        Expect(File.ReadAllBytes(path).SequenceEqual(originalBytes));

        File.WriteAllText(path, "{");
        var corruptText = File.ReadAllText(path);
        Reject(() => store.Load());
        Reject(() => store.Save([firstCampus, secondCampus]));
        Expect(File.ReadAllText(path) == corruptText);

        var legacyPath = Path.Combine(directory, "legacy-directory.json");
        File.WriteAllText(legacyPath, $$"""
            {"schemaVersion":1,"campuses":[{"profileId":"{{Guid.NewGuid()}}","displayName":"旧校区","rooms":[{"roomId":"{{Guid.NewGuid()}}","displayName":"旧机房","prefix":"PC-","startNumber":1,"computerCount":2}]}]}
            """);
        var legacyRoom = new TeacherCampusDirectoryStore(legacyPath).Load().Single().Rooms.Single();
        Expect(legacyRoom.HostOverrides is null && legacyRoom.ComputerCount == 2);
    }
    finally { Directory.Delete(directory, recursive: true); }
});
Check("四项操作独立；无选项不生成计划", () =>
{
    PlanInput Input(OperationSelection ops, PackageContext? package = null) =>
        new("", "PC-", "3", "User", "Admin", ops, package);
    Reject(() => DeploymentPlan.Create(Input(new(false, false, false, false))));
    Reject(() => ExecutionPlan.Create(Input(new(false, false, false, false)), null));
    var rename = DeploymentPlan.Create(Input(new(false, true, false, false)));
    Expect(rename.ComputerName == "PC-03" && rename.Steps.Any(s => s.Id == "rename") &&
           rename.Steps.All(s => !s.Id.StartsWith("veyon") && s.Id != "student-account" && s.Id != "admin-password"));
    var student = DeploymentPlan.Create(Input(new(false, false, true, false)));
    Expect(student.ComputerName is null && student.Steps.Any(s => s.Id == "student-account"));
    var admin = DeploymentPlan.Create(Input(new(false, false, false, true)));
    Expect(admin.Steps.Any(s => s.Id == "admin-password") && admin.Steps.All(s => s.Id != "rename"));
    var accountSelections = new MainViewModel();
    accountSelections.CreateStudent = true;
    Expect(accountSelections.CreateStudent && !accountSelections.ChangeAdminPassword);
    accountSelections.ChangeAdminPassword = true;
    Expect(accountSelections.CreateStudent && accountSelections.ChangeAdminPassword);
    accountSelections.CreateStudent = false;
    Expect(!accountSelections.CreateStudent && accountSelections.ChangeAdminPassword);
    Reject(() => DeploymentPlan.Create(Input(new(true, false, false, false))));
    Reject(() => DeploymentPlan.Create(new PlanInput("", "", "", "", "Admin", new(false, false, true, false), null)));
});
Check("执行计划深度只读并汇总成功、失败、取消、待重启和部分完成", () =>
{
    var plan = ExecutionPlan.Create(new PlanInput("", "PC-", "3", "Student", "Admin",
        new OperationSelection(false, true, false, false), null), null);
    Expect(plan.Steps.Count == 1 && plan.Steps[0].Id == "rename" &&
           plan.Steps[0].MayRequireReboot && !plan.Steps[0].AutomaticallyReversible &&
           plan.Steps[0].DependsOn.Count == 0);
    var listIsReadOnly = false;
    try { ((IList<ExecutionStep>)plan.Steps).Clear(); }
    catch (NotSupportedException) { listIsReadOnly = true; }
    var dependenciesAreReadOnly = false;
    try { ((IList<string>)plan.Steps[0].DependsOn).Add("unexpected"); }
    catch (NotSupportedException) { dependenciesAreReadOnly = true; }
    Expect(listIsReadOnly && dependenciesAreReadOnly && plan.Steps.Count == 1);

    StepResult Result(string status, bool reboot = false) => new("step", status, "fixture", RebootRequired: reboot);
    Expect(ExecutionPlan.Summarize(Array.Empty<StepResult>()).Status == ExecutionPlan.NotStarted);
    Expect(ExecutionPlan.Summarize([Result(ExecutionPlan.Succeeded)]).Status == ExecutionPlan.Succeeded);
    Expect(ExecutionPlan.Summarize([Result(ExecutionPlan.Failed)]).Status == ExecutionPlan.Failed);
    Expect(ExecutionPlan.Summarize([Result(ExecutionPlan.Cancelled)]).Status == ExecutionPlan.Cancelled);
    var restart = ExecutionPlan.Summarize([Result(ExecutionPlan.Failed, reboot: true)]);
    Expect(restart.Status == ExecutionPlan.RequiresReboot && restart.RebootRequired);
    Expect(ExecutionPlan.Summarize([Result(ExecutionPlan.Succeeded), Result(ExecutionPlan.Failed)]).Status ==
           ExecutionPlan.PartiallyCompleted);
    Expect(ExecutionPlan.Summarize([Result(ExecutionPlan.PartiallyCompleted)]).Status ==
           ExecutionPlan.PartiallyCompleted);
    Expect(ExecutionPlan.Summarize([Result(ExecutionPlan.NeedsReview)]).Status == ExecutionPlan.NeedsReview);
});
Check("五步向导阻止跳步并保留管理员维护返回位置", () =>
{
    var vm = new MainViewModel();
    Expect(vm.WizardPage == 0 && !vm.CanNavigateWizardPage(2));
    vm.NavigateWizardPage(2);
    Expect(vm.WizardPage == 0);
    vm.NextWizardPage();
    Expect(vm.WizardPage == 1 && !vm.HasSelectedOperation);
    vm.InstallVeyon = true;
    Expect(vm.HasVeyonPackageRequirement && !vm.CanGoNextWizardPage);
    vm.InstallVeyon = false;
    vm.RenameComputer = true;
    vm.NextWizardPage();
    Expect(vm.WizardPage == 2 && !vm.CanGoNextWizardPage && !vm.CanNavigateWizardPage(3));
    var canOpenMaintenance = vm.CanOpenMaintenance;
    vm.OpenMaintenancePage();
    Expect(vm.WizardPage == (canOpenMaintenance ? 4 : 2));
    if (canOpenMaintenance)
    {
        Expect(vm.IsCompletePage && !vm.CanReviewExecutionResult);
        vm.PreviousWizardPage();
        Expect(vm.WizardPage == 2);
    }
    vm.Reset();
    Expect(vm.WizardPage == 0 && !vm.HasSelectedOperation && vm.HasNoLoadedPackage);
});
Check("界面状态：修改选项清除预览，教师清单同步边界", () =>
{
    var vm = new MainViewModel { RenameComputer = true, Number = "3" };
    vm.GeneratePreview();
    Expect(vm.HasPreview && !vm.HasError && vm.PreviewText.Contains("目标计算机：") &&
           vm.PreviewText.Contains("改名可能需要重启") && !vm.CanStartDeployment);
    vm.Number = "100"; Expect(!vm.HasPreview && vm.ComputerName == "PC-100");
    vm.Navigate(false); Expect(vm.IsTeacher && vm.Number == "100");
    vm.GenerateRoomPreview();
    var expectedNames = Enumerable.Range(1, 150)
        .Select(number => MachineNaming.CreateName("PC-", number.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        .ToArray();
    Expect(vm.HasRoomPreview && vm.RoomNames.SequenceEqual(expectedNames));
    vm.RoomCount = "151"; Expect(!vm.HasRoomPreview);
    vm.GenerateRoomPreview(); Expect(vm.HasRoomError);
    vm.Number = "0"; vm.GeneratePreview(); Expect(vm.HasError && !vm.HasPreview);
    vm.Number = "5"; Expect(!vm.HasError);
});
Check("账户表单：学生初始密码可留空，管理员名默认为 Administrator", () =>
{
    var vm = new MainViewModel();
    Expect(vm.StudentAccountName == "User" && vm.AdminAccountName == "Administrator");
    vm.CreateStudent = true;
    vm.GeneratePreview();
    Expect(!vm.DeploymentAvailabilityText.Contains("学生初始密码", StringComparison.Ordinal));
    vm.SetStudentPasswordInput("student-pass", "");
    Expect(vm.DeploymentAvailabilityText.Contains("请两次输入学生初始密码", StringComparison.Ordinal));
    vm.SetStudentPasswordInput("first", "second");
    Expect(vm.DeploymentAvailabilityText.Contains("不一致", StringComparison.Ordinal));
    vm.SetStudentPasswordInput("", "");
    Expect(!vm.DeploymentAvailabilityText.Contains("学生初始密码", StringComparison.Ordinal));
    vm.ChangeAdminPassword = true;
    vm.GeneratePreview();
    Expect(vm.AdminAccountName == "Administrator" &&
           vm.DeploymentAvailabilityText.Contains("请两次输入管理员新密码", StringComparison.Ordinal));
    vm.Reset();
    Expect(vm.StudentAccountName == "User" && vm.AdminAccountName == "Administrator");
});
await CheckAsync("异步只读环境检查保留未知状态且表单变化使结果失效", async () =>
{
    var vm = new MainViewModel { RenameComputer = true, Number = "100" };
    await vm.CheckEnvironmentAsync(); Expect(vm.HasPreflight && !vm.HasError);
    PlanInput Input(string number) => new("", "PC-", number, "User", "Admin",
        new OperationSelection(false, true, false, false), null);
    var first = ReadOnlyPreflight.Check(Input("100"));
    var same = ReadOnlyPreflight.Check(Input("100"));
    var changed = ReadOnlyPreflight.Check(Input("101"));
    Expect(first.PlanSha256 == same.PlanSha256 && first.PlanSha256 != changed.PlanSha256 &&
           first.PackageSha256 is null && first.CheckedAt <= DateTimeOffset.UtcNow);
    if (!OperatingSystem.IsWindows())
    {
        Expect(vm.PreflightText.Contains("不是 Windows") && vm.PreflightText.Contains("不适用"));
        Expect(first.HasBlocker && first.Checks.Any(c => c.Level == CheckLevel.NotApplicable));
    }
    vm.Number = "101"; Expect(!vm.HasPreflight);
    await vm.CheckEnvironmentAsync(); Expect(vm.HasPreflight);
    vm.RenameComputer = false; await vm.CheckEnvironmentAsync(); Expect(vm.HasError && !vm.HasPreflight);
});
Check("平台只读事实：不修改系统，未知项保留", () =>
{
    var facts = PlatformFacts.Collect();
    Expect(facts.ComputerName == Environment.MachineName && facts.IsWindows == OperatingSystem.IsWindows());
    Expect(facts.OperatingSystemVersion.Length > 0 && facts.SystemArchitecture.Length > 0);
    Expect(ReadOnlyPreflight.EvaluatePrivilege(true, "admin").Level == CheckLevel.Pass &&
           ReadOnlyPreflight.EvaluatePrivilege(false, "not elevated").Level == CheckLevel.Blocked &&
           ReadOnlyPreflight.EvaluatePrivilege(null, "unknown").Level == CheckLevel.Unknown);
    var elevatedReport = new PreflightReport(DateTimeOffset.UtcNow, "plan", null,
        [ReadOnlyPreflight.EvaluatePrivilege(true, "admin")]);
    var unknownPrivilegeReport = new PreflightReport(DateTimeOffset.UtcNow, "plan", null,
        [ReadOnlyPreflight.EvaluatePrivilege(null, "unknown")]);
    Expect(ReadOnlyPreflight.IsExecutable(elevatedReport) &&
           !ReadOnlyPreflight.IsExecutable(unknownPrivilegeReport) &&
           !ReadOnlyPreflight.IsExecutable(null));
    foreach (var detail in new[] { facts.ElevationDetail, facts.RebootDetail, facts.DiskDetail, facts.VeyonDetail })
        Expect(detail.Length > 0);
    if (!OperatingSystem.IsWindows())
    {
        Expect(facts.RebootDetail.Contains("不适用") && facts.ElevationDetail.Contains("不适用")
            && facts.DiskDetail.Contains("不适用") && facts.IsElevated is null);
    }
    else
    {
        Expect(facts.IsElevated is not null && facts.DomainMembership is not null &&
               facts.DomainMembership.Detail.Length > 0 && facts.RestoreEnvironment is not null &&
               facts.RestoreEnvironment.Detail.Length > 0);
        var domainState = facts.DomainMembership!.IsDomainJoined switch
        {
            true => "domain",
            false => "workgroup",
            null => "unknown"
        };
        Console.WriteLine($"INFO Windows read-only facts: OS={facts.OperatingSystemVersion}; arch={facts.SystemArchitecture}; elevated={facts.IsElevated}; reboot={facts.RebootDetail}; interactiveVeyon={facts.HasInteractiveVeyonProcess}; domain={domainState}; restore={facts.RestoreEnvironment!.Evidence}.");
    }
});
await CheckAsync("执行入口必须有当前预检；组合选择在修改前被安全拒绝", async () =>
{
    for (var mask = 1; mask < 16; mask++)
    {
        if (mask == 1) continue; // 单独 Veyon 配置路径：macOS 预检被平台阻断。
        var vm = new MainViewModel
        {
            InstallVeyon = (mask & 1) != 0,
            RenameComputer = (mask & 2) != 0,
            CreateStudent = (mask & 4) != 0,
            ChangeAdminPassword = (mask & 8) != 0
        };
        await vm.RunDeploymentAsync();
        Expect(vm.Error.Length > 0 && !vm.IsExecuting && !vm.HasCurrentExecution);
    }

    var veyonOnly = new MainViewModel { InstallVeyon = true };
    Expect(!veyonOnly.CanInstall && !veyonOnly.CanStartDeployment);
    veyonOnly.CheckEnvironment();
    Expect(!veyonOnly.CanInstall && !veyonOnly.CanStartDeployment);
    veyonOnly.RenameComputer = true;
    Expect(!veyonOnly.CanInstall && !veyonOnly.HasPreflight);

    var combo = new MainViewModel { InstallVeyon = true, RenameComputer = true, Number = "3" };
    combo.GeneratePreview();
    combo.CheckEnvironment();
    await combo.RunDeploymentAsync();
    // 未载入校区包/安装器时组合执行在修改前被拒绝；错误信息指向缺失资料。
    Expect(combo.Error.Contains("请先载入校区公钥配置包", StringComparison.Ordinal) &&
           !combo.HasCurrentExecution && !combo.IsExecuting);

    // 仅改名的计划可以预览并运行只读检查；非 Windows 平台被阻断，组合执行不会开始修改。
    var renameOnly = new MainViewModel { RenameComputer = true, Number = "3" };
    renameOnly.GeneratePreview();
    renameOnly.CheckEnvironment();
    Expect(renameOnly.HasPreflight);
    // Never run a valid rename plan on an elevated Windows test host.
    renameOnly.Number = "4";
    Expect(!renameOnly.CanStartDeployment && !renameOnly.HasPreflight);
    await renameOnly.RunDeploymentAsync();
    Expect(renameOnly.Error.Length > 0 && !renameOnly.HasCurrentExecution && !renameOnly.IsExecuting);
});

Check("任务租约：并发入口互斥，忙碌期间第二请求被拒", () =>
{
    var lease = new TaskLease();
    // 第一次获取成功，拒绝原因为空。
    Expect(lease.TryAcquire(out var first) && first.Length == 0);
    // 持有期间第二次获取必须被拒绝，且携带用户可见的拒绝原因。
    Expect(!lease.TryAcquire(out var second) && second.Length > 0);
    // 释放后再次获取成功，租约不泄漏。
    lease.Dispose();
    Expect(lease.TryAcquire(out _));
    lease.Dispose();
    // 并发场景：任务 A 持有期间，任务 B 的获取尝试必须失败。
    var lease2 = new TaskLease();
    using var acquired = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var taskA = Task.Run(() =>
    {
        Expect(lease2.TryAcquire(out _));
        acquired.Set();
        release.Wait();
        lease2.Dispose();
    });
    Expect(acquired.Wait(TimeSpan.FromSeconds(10)));
    var bRejected = !lease2.TryAcquire(out _);
    release.Set();
    taskA.Wait();
    Expect(bRejected);
    // 任务 A 释放后，租约可再次获取（证明没有死锁）。
    Expect(lease2.TryAcquire(out _));
    lease2.Dispose();
});
var temporary = Path.Combine(TestPath.CanonicalTempRoot(), "veyon-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    ReviewRegressionChecks.Run(Check, temporary);
    Check("教师逐台推送结果本机保留、脱敏并限制为最近 50 次", CheckWebsitePolicyHistory);
    Check("学生配置包完整校验、原子发布、取消及失败清理", () => PackageBuilderFailureChecks.Run(temporary));

    await CheckAsync("Veyon 安装器从 App 内嵌资源离线提取并复用", async () =>
    {
        var cache = Path.Combine(temporary, "embedded-installer-cache");
        var store = new VeyonInstallerStore(cache);
        var extracted = await store.EnsureAvailableAsync();
        Expect(extracted.ExtractedFromApp && extracted.Trust.IsAllowed &&
               new FileInfo(extracted.InstallerPath).Length == VeyonInstallerTrust.FileSize);
        var reused = await store.EnsureAvailableAsync();
        Expect(!reused.ExtractedFromApp && reused.Trust.IsAllowed && reused.InstallerPath == extracted.InstallerPath);
    });
    var configPath = Path.Combine(temporary, "campus.json");
    var publicPath = Path.Combine(temporary, "demo-public.pem");
    using var rsa = RSA.Create(2048);
    var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
    void Config(string key = "demo-public.pem", string prefix = "PC-") => File.WriteAllText(configPath,
        JsonSerializer.Serialize(new { campus = "演示校区", computerPrefix = prefix, keyFile = key }), new UTF8Encoding(true));
    File.WriteAllText(publicPath, publicPem);
    Check("公钥资源快照：实际导入副本，源替换不影响 CLI 消费的字节", () =>
    {
        Config();
        File.WriteAllText(publicPath, publicPem);
        var snapshotRoot = Path.Combine(temporary, "snapshots");
        var package = PackageContext.Load(temporary) with { Campus = "campus-demo" };
        using var snapshot = PackageResourceSnapshot.Create(snapshotRoot, package);
        Expect(snapshot.PublicKeySha256 == PackageContext.Load(temporary).PublicKeySha256);
        using (var replaced = RSA.Create(2048))
            File.WriteAllText(publicPath, replaced.ExportSubjectPublicKeyInfoPem());
        snapshot.VerifyUnchanged();
        var importer = new FakeProcessLauncher((file, arguments) =>
        {
            Expect(arguments[0] == "authkeys" && arguments[1] == "import");
            Expect(arguments[3] == snapshot.PublicKeyPath && arguments[3] != publicPath);
            Expect(File.ReadAllText(arguments[3]) == publicPem);
            return FakeProcessLauncher.Success();
        });
        Expect(snapshot.ImportPublicKey(Path.Combine(temporary, "veyon-cli.exe"), package, importer).Ok);
        if (OperatingSystem.IsWindows())
        {
            var denied = false;
            try { using var writer = File.OpenWrite(snapshot.PublicKeyPath); }
            catch (IOException) { denied = true; }
            Expect(denied);
        }
        snapshot.Dispose();
        File.WriteAllText(publicPath, publicPem);
        Config();
        Expect(!Directory.Exists(snapshot.WorkingDirectory));
    });

    await CheckAsync("四项操作的 16 种独立与组合计划按固定依赖执行", async () =>
    {
        var package = PackageContext.Load(temporary);
        for (var mask = 1; mask < 16; mask++)
        {
            var operations = new OperationSelection(
                InstallVeyon: (mask & 1) != 0,
                RenameComputer: (mask & 2) != 0,
                CreateStudent: (mask & 4) != 0,
                ChangeAdminPassword: (mask & 8) != 0);
            var selectedPackage = operations.InstallVeyon ? package : null;
            var input = new PlanInput(package.Campus, "PC-", "3", "Student", "Admin",
                operations, selectedPackage);
            var plan = ExecutionPlan.Create(input, selectedPackage);

            var expectedIds = new List<string>();
            if (operations.CreateStudent) expectedIds.Add("student-account");
            if (operations.InstallVeyon) expectedIds.AddRange(["veyon-install", "veyon-key"]);
            if (operations.ChangeAdminPassword) expectedIds.Add("admin-password");
            if (operations.RenameComputer) expectedIds.Add("rename");
            Expect(plan.Steps.Select(step => step.Id).SequenceEqual(expectedIds));
            for (var index = 0; index < plan.Steps.Count; index++)
                Expect(plan.Steps[index].DependsOn.SequenceEqual(expectedIds.Take(index)));

            var invoked = new List<string>();
            var summary = await ExecutionCoordinator.RunAsync(plan, step =>
            {
                invoked.Add(step.Id);
                return Task.FromResult(new StepResult(step.Id, ExecutionPlan.Succeeded, "模拟完成"));
            });
            Expect(summary.Status == ExecutionPlan.Succeeded &&
                   invoked.SequenceEqual(expectedIds) &&
                   summary.Steps.Select(step => step.StepId).SequenceEqual(expectedIds));
        }

        var allOperations = new OperationSelection(true, true, true, true);
        var allInput = new PlanInput(package.Campus, "PC-", "3", "Student", "Admin", allOperations, package);
        var allPlan = ExecutionPlan.Create(allInput, package);
        var failureCalls = new List<string>();
        var failed = await ExecutionCoordinator.RunAsync(allPlan, step =>
        {
            failureCalls.Add(step.Id);
            var status = step.Id == "admin-password" ? ExecutionPlan.Failed : ExecutionPlan.Succeeded;
            return Task.FromResult(new StepResult(step.Id, status, "模拟结果"));
        });
        Expect(failureCalls.SequenceEqual(["student-account", "veyon-install", "veyon-key", "admin-password"]));
        Expect(failed.Status == ExecutionPlan.PartiallyCompleted &&
               failed.Steps.Single(step => step.StepId == "admin-password").Status == ExecutionPlan.Failed &&
               failed.Steps.Single(step => step.StepId == "rename").Status == ExecutionPlan.Skipped);

        using var cancel = new CancellationTokenSource();
        var cancelCalls = 0;
        var cancelled = await ExecutionCoordinator.RunAsync(allPlan, step =>
        {
            cancelCalls++;
            cancel.Cancel();
            return Task.FromResult(new StepResult(step.Id, ExecutionPlan.Succeeded, "模拟安全边界"));
        }, cancel.Token);
        Expect(cancelCalls == 1 && cancelled.Status == ExecutionPlan.PartiallyCompleted &&
               cancelled.Steps[1].Status == ExecutionPlan.Cancelled &&
               cancelled.Steps.Skip(2).All(step => step.Status == ExecutionPlan.Skipped));

        var veyonInput = new PlanInput(package.Campus, "PC-", "", "Student", "Admin",
            new OperationSelection(true, false, false, false), package);
        var veyonPlan = ExecutionPlan.Create(veyonInput, package);
        var reboot = await ExecutionCoordinator.RunAsync(veyonPlan, step =>
            Task.FromResult(new StepResult(step.Id, ExecutionPlan.Succeeded, "模拟安装",
                RebootRequired: step.MayRequireReboot)));
        Expect(reboot.Status == ExecutionPlan.RequiresReboot &&
               reboot.Steps[1].Status == ExecutionPlan.Skipped);

        var combinedInput = new PlanInput(package.Campus, "PC-", "3", "Student", "Admin",
            new OperationSelection(true, true, true, true), package);
        var combinedPlan = ExecutionPlan.Create(combinedInput, package);
        var combinedCalls = new List<string>();
        var combinedReboot = await ExecutionCoordinator.RunAsync(combinedPlan, step =>
        {
            combinedCalls.Add(step.Id);
            return Task.FromResult(new StepResult(step.Id, ExecutionPlan.Succeeded, "模拟安装",
                RebootRequired: step.Id == "veyon-install"));
        });
        Expect(combinedCalls.SequenceEqual(["student-account", "veyon-install"]));
        Expect(combinedReboot.Status == ExecutionPlan.RequiresReboot &&
               combinedReboot.Steps.Single(step => step.StepId == "admin-password").Status == ExecutionPlan.Skipped &&
               combinedReboot.Steps.Single(step => step.StepId == "rename").Status == ExecutionPlan.Skipped);

        var combinedFailureCalls = new List<string>();
        var combinedFailure = await ExecutionCoordinator.RunAsync(combinedPlan, step =>
        {
            combinedFailureCalls.Add(step.Id);
            var status = step.Id == "veyon-install" ? ExecutionPlan.Failed : ExecutionPlan.Succeeded;
            return Task.FromResult(new StepResult(step.Id, status, "模拟 Veyon 安装失败"));
        });
        Expect(combinedFailureCalls.SequenceEqual(["student-account", "veyon-install"]));
        Expect(combinedFailure.Steps.Single(step => step.StepId == "admin-password").Status == ExecutionPlan.Skipped &&
               combinedFailure.Steps.Single(step => step.StepId == "rename").Status == ExecutionPlan.Skipped);

        var review = await ExecutionCoordinator.RunAsync(allPlan, _ =>
            throw new InvalidOperationException("不要写入运行记录的异常内容"));
        Expect(review.Status == ExecutionPlan.NeedsReview &&
               review.Steps[0].Status == ExecutionPlan.NeedsReview &&
               review.Steps.Skip(1).All(step => step.Status == ExecutionPlan.Skipped) &&
               !review.Steps[0].Detail.Contains("不要写入"));
    });
    Check("部署包入口统一解析文件夹与清单文件", () =>
    {
        var spaced = Path.Combine(temporary, "中文 资料");
        Directory.CreateDirectory(spaced);
        var chosenManifest = Path.Combine(spaced, "manifest.json");
        var chosenLegacy = Path.Combine(spaced, "campus.json");
        File.WriteAllText(chosenManifest, "{}");
        File.WriteAllText(chosenLegacy, "{}");
        Expect(PackageSource.Resolve(spaced) == spaced);
        Expect(PackageSource.Resolve(chosenManifest) == spaced);
        Expect(PackageSource.Resolve(chosenLegacy) == spaced);
        Expect(PackageSource.IsCandidate(chosenManifest));
        var zip = Path.Combine(spaced, "package.zip");
        File.WriteAllText(zip, "ZIP preview");
        Expect(!PackageSource.IsCandidate(zip));
        Reject(() => PackageSource.Resolve(zip));
        var missing = Path.Combine(spaced, "已移动的部署包");
        Expect(!PackageSource.IsCandidate(missing));
        try
        {
            PackageSource.Resolve(missing);
            throw new Exception("Missing package path was accepted");
        }
        catch (InvalidDataException ex) { Expect(ex.Message.Contains("权限不足")); }
        File.Delete(chosenManifest);
        Expect(!PackageSource.IsCandidate(chosenManifest));
        Reject(() => PackageSource.Resolve(chosenManifest));
    });
    Check("旧 BOM 配置读取忽略 admin.txt，RSA 指纹稳定", () =>
    {
        var adminPath = Path.Combine(temporary, "admin.txt");
        File.WriteAllText(adminPath, "fixture-only-not-a-real-password");
        var package = PackageContext.LoadLegacy(temporary);
        Expect(package.Campus == "演示校区" && package.ComputerPrefix == "PC-");
        Expect(package.PublicKeyFingerprint.Length == 64 && File.Exists(adminPath));
        package.VerifyUnchanged();
        File.WriteAllText(adminPath, "changed-fixture-only-content");
        package.VerifyUnchanged();
        var vm = new MainViewModel(new VeyonInstallerStore(Path.Combine(temporary, "viewmodel-cache"))); vm.LoadPackage(temporary);
        vm.InstallVeyon = true; vm.GeneratePreview(); Expect(vm.HasPreview && !vm.HasError);
        vm.Campus = "别的校区"; Expect(vm.LoadedPackage is null && !vm.HasPreview);
        vm.GeneratePreview(); Expect(vm.HasError);
    });
    Check("公钥和配置变化导致旧计划失效", () =>
    {
        var vm = new MainViewModel(new VeyonInstallerStore(Path.Combine(temporary, "viewmodel-cache-2"))); vm.LoadPackage(temporary); vm.InstallVeyon = true;
        vm.GeneratePreview(); Expect(vm.HasPreview);
        using var replacement = RSA.Create(2048);
        File.WriteAllText(publicPath, replacement.ExportSubjectPublicKeyInfoPem());
        vm.GeneratePreview(); Expect(vm.HasError && !vm.HasPreview);
        File.WriteAllText(publicPath, publicPem);
        Config(prefix: "A-PC-");
        vm.GeneratePreview(); Expect(vm.HasError);
        Config();
    });
    Check("拒绝路径越界、重复字段、私钥、假公钥", () =>
    {
        foreach (var key in new[] { "../demo-public.pem", "..\\demo-public.pem", "C:demo-public.pem", "private.pem" })
        {
            Config(key); Reject(() => PackageContext.LoadLegacy(temporary));
        }
        File.WriteAllText(configPath, "{\"campus\":\"A\",\"campus\":\"B\",\"computerPrefix\":\"PC-\",\"keyFile\":\"demo-public.pem\"}");
        Reject(() => PackageContext.LoadLegacy(temporary));
        Config();
        File.WriteAllText(publicPath, rsa.ExportRSAPrivateKeyPem()); Reject(() => PackageContext.LoadLegacy(temporary));
        File.WriteAllText(publicPath, "-----BEGIN PUBLIC KEY-----\npreview-only\n-----END PUBLIC KEY-----");
        Reject(() => PackageContext.LoadLegacy(temporary));
        File.WriteAllText(publicPath, publicPem);
        Config(prefix: "ABCDEFGHIJKLM"); Reject(() => PackageContext.LoadLegacy(temporary));
        Config();
    });
    Check("无效包不保留上次资料，取消选择不调用载入", () =>
    {
        var vm = new MainViewModel(new VeyonInstallerStore(Path.Combine(temporary, "viewmodel-cache-3"))); vm.LoadPackage(configPath);
        Expect(vm.LoadedPackage is not null);
        var zip = Path.Combine(temporary, "invalid.zip");
        File.WriteAllText(zip, "not a package");
        vm.LoadPackage(zip);
        Expect(vm.HasError && vm.HasPackageError && !vm.HasGlobalError && vm.LoadedPackage is null && vm.Campus == "");
        vm.LoadPackage(temporary);
        Expect(vm.LoadedPackage is not null && !vm.HasPackageError);
        File.WriteAllText(configPath, "[]"); vm.LoadPackage(temporary);
        Expect(vm.HasError && !vm.HasPreview && vm.Campus == "" && vm.LoadedPackage is null);
        File.WriteAllText(configPath, "{broken"); vm.LoadPackage(temporary); Expect(vm.HasError);
        Config();
    });
    Check("Veyon 只读探测：未安装显示需要安装，不误报服务状态", () =>
    {
        var veyon = VeyonFacts.Probe();
        if (!OperatingSystem.IsWindows())
        {
            Expect(veyon.Status == VeyonFacts.NotApplicable && veyon.Detail.Contains("不适用"));
            return;
        }
        switch (veyon.Status)
        {
            case VeyonFacts.NotInstalled:
                Expect(veyon.Detail.Contains("默认路径未检测到 Veyon") && veyon.ServiceDetail.Contains("未注册"));
                break;
            case "installed":
                Expect(veyon.Detail.Length > 0);
                break;
            default:
                throw new Exception("Veyon 探测状态非法：" + veyon.Status);
        }
        Expect(veyon.AsText().Length > 0);
    });
    Check("新版清单校验安装资源和公钥，变化后失效", () =>
    {
        var root = Path.Combine(temporary, "modern");
        Directory.CreateDirectory(Path.Combine(root, "keys"));
        Directory.CreateDirectory(Path.Combine(root, "resources"));
        var keyPath = Path.Combine(root, "keys", "demo-public.pem");
        var setupPath = Path.Combine(root, "resources", "veyon-test.exe");
        File.WriteAllText(keyPath, publicPem);
        File.WriteAllBytes(setupPath, "MZ test resource only"u8.ToArray());
        object Entry(string path) => new {
            path, size = new FileInfo(Path.Combine(root, path)).Length,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, path))))
        };
        var manifestPath = Path.Combine(root, "manifest.json");
        void Manifest(string key = "keys/demo-public.pem", int schema = 1, string campus = "演示校区") => File.WriteAllText(manifestPath,
            JsonSerializer.Serialize(new {
                schemaVersion = schema, packageId = Guid.NewGuid().ToString(), targetOs = "windows",
                architecture = "x64", campus, computerPrefix = "PC-",
                publicKey = key == "keys/demo-public.pem" ? Entry(key) : new { path = key, size = 1L, sha256 = new string('0', 64) },
                installer = Entry("resources/veyon-test.exe")
            }));
        Manifest();
        var loaded = PackageContext.Load(root);
        Expect(loaded.SchemaVersion == 1 && loaded.InstallerPath == setupPath);
        var preflight = ReadOnlyPreflight.Check(new PlanInput(loaded.Campus, loaded.ComputerPrefix, "", "User", "Admin",
            new OperationSelection(true, false, false, false), loaded));
        Expect(preflight.HasBlocker);
        var executionPlan = ExecutionPlan.Create(new PlanInput(loaded.Campus, loaded.ComputerPrefix, "", "User", "Admin",
            new OperationSelection(true, false, false, false), loaded), loaded);
        Expect(executionPlan.Steps.Select(step => step.Id).SequenceEqual(new[] { "veyon-install", "veyon-key" }) &&
               executionPlan.Steps[0].DependsOn.Count == 0 &&
               executionPlan.Steps[1].DependsOn.SequenceEqual(new[] { "veyon-install" }) &&
               executionPlan.Steps[0].MayRequireReboot &&
               executionPlan.Package?.PackageFingerprint == loaded.PackageFingerprint);
        if (OperatingSystem.IsWindows())
            Expect(preflight.Checks.Any(c => c.Id == "installer-trust" && c.Level == CheckLevel.Blocked));
        loaded.VerifyUnchanged();
        var movedRoot = root + "-moved";
        Directory.Move(root, movedRoot);
        Reject(loaded.VerifyUnchanged);
        Directory.Move(movedRoot, root);
        var injectedPrivate = Path.Combine(root, "keys", "school-private.pem");
        File.WriteAllText(injectedPrivate, "fixture");
        Reject(() => PackageContext.Load(root));
        File.Delete(injectedPrivate);
        Manifest();
        File.AppendAllText(setupPath, "changed");
        Reject(loaded.VerifyUnchanged);
        File.WriteAllBytes(setupPath, "MZ test resource only"u8.ToArray());
        Manifest("../demo-public.pem"); Reject(() => PackageContext.Load(root));
        Manifest(schema: 2); Reject(() => PackageContext.Load(root));
        Manifest(schema: 4); Reject(() => PackageContext.Load(root));
        Manifest(campus: new string('A', 101)); Reject(() => PackageContext.Load(root));
        Manifest(key: "keys/" + new string('a', 240) + ".pem"); Reject(() => PackageContext.Load(root));
        File.WriteAllText(manifestPath, new string(' ', 64 * 1024 + 1));
        Reject(() => PackageContext.Load(root));
        Manifest();
        Manifest();
        File.WriteAllText(manifestPath, "{\"schemaVersion\":1,\"schemaVersion\":1}");
        Reject(() => PackageContext.Load(root));
    });
    Check("学生配置包只含校区资料，拒绝私钥和目录污染", () =>
    {
        Reject(() => PackageBuilder.Build(Path.Combine(temporary, "unsafe-campus"), "../escape", "PC-",
            Path.Combine(temporary, "unused-public.pem")));
        var publicKeySource = Path.Combine(temporary, "source-public.pem");
        using (var sourceKey = RSA.Create(2048))
            File.WriteAllText(publicKeySource, sourceKey.ExportSubjectPublicKeyInfoPem());
        var output = Path.Combine(temporary, "student-package");
        var built = PackageBuilder.Build(output, "campus-demo", "PC-", publicKeySource);
        var tree = Directory.EnumerateFileSystemEntries(built, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName).ToArray();
        Expect(tree.Any(x => x == "manifest.json") && tree.Any(x => x == "campus.json"));
        Expect(tree.All(x => x is not null && !x.Contains("private", StringComparison.OrdinalIgnoreCase)));
        Expect(tree.Length == 4 && tree.All(x => !x!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
        var parsed = PackageContext.Load(built);
        Expect(parsed.SchemaVersion == 2 && parsed.InstallerPath is null);
        parsed.VerifyUnchanged();
        using (var sourceRsa = RSA.Create())
        using (var publicRsa = RSA.Create())
        {
            sourceRsa.ImportFromPem(File.ReadAllText(publicKeySource));
            publicRsa.ImportFromPem(File.ReadAllText(Path.Combine(built, "campus-demo-public.pem")));
            Expect(Convert.ToHexString(sourceRsa.ExportSubjectPublicKeyInfo()) ==
                   Convert.ToHexString(publicRsa.ExportSubjectPublicKeyInfo()));
        }
        var secondBuild = PackageBuilder.Build(Path.Combine(temporary, "student-package-again"),
            "campus-demo", "PC-", publicKeySource);
        Expect(File.ReadAllText(Path.Combine(built, "campus-demo-public.pem")) ==
               File.ReadAllText(Path.Combine(secondBuild, "campus-demo-public.pem")));
        using var policySigner = RSA.Create(3072);
        var websitePackagePath = PackageBuilder.Build(Path.Combine(temporary, "student-package-website"),
            "campus-demo", "PC-", publicKeySource, policySigner.ExportSubjectPublicKeyInfoPem());
        var websitePackage = PackageContext.Load(websitePackagePath);
        Expect(websitePackage.SchemaVersion == 3 && websitePackage.WebsitePolicyPublicKeyPath is not null &&
               websitePackage.WebsitePolicyPublicKeySha256 is { Length: 64 });
        var websitePackageFiles = Directory.EnumerateFiles(websitePackagePath).Select(Path.GetFileName).ToArray();
        Expect(websitePackageFiles.Contains("website-policy-public.pem") &&
               websitePackageFiles.All(name => name is not null && !name.Contains("private", StringComparison.OrdinalIgnoreCase)));
        var websitePlan = ExecutionPlan.Create(new PlanInput("campus-demo", "PC-", "", "User", "Admin",
            new OperationSelection(true, false, false, false), websitePackage), websitePackage);
        Expect(websitePlan.Steps.Select(step => step.Id).SequenceEqual(new[] { "veyon-install", "veyon-key", "website-agent" }) &&
               websitePlan.Steps[2].DependsOn.SequenceEqual(new[] { "veyon-install", "veyon-key" }));
        using (var packageSnapshot = PackageResourceSnapshot.Create(Path.Combine(temporary, "policy-snapshots"), websitePackage))
            Expect(packageSnapshot.WebsitePolicyPublicKeyPath is not null &&
                   packageSnapshot.WebsitePolicyPublicKeySha256 == websitePackage.WebsitePolicyPublicKeySha256);
        using var applicationPolicySigner = RSA.Create(3072);
        var applicationPackagePath = PackageBuilder.Build(Path.Combine(temporary, "student-package-application"),
            "campus-demo", "PC-", publicKeySource, policySigner.ExportSubjectPublicKeyInfoPem(),
            enableAnonymousTelemetry: true,
            applicationPolicyPublicKeyPem: applicationPolicySigner.ExportSubjectPublicKeyInfoPem(),
            compatibility: PackageCompatibility.ForExactVersions("0.4.47", VeyonInstallerTrust.Version));
        var applicationPackage = PackageContext.Load(applicationPackagePath);
        Expect(applicationPackage.SchemaVersion == 4 && applicationPackage.WebsitePolicyPublicKeyPath is not null &&
               applicationPackage.ApplicationPolicyPublicKeyPath is not null &&
               applicationPackage.ApplicationPolicyPublicKeySha256 is { Length: 64 } &&
               applicationPackage.Compatibility is not null && applicationPackage.PayloadFiles?.Count == 5);
        using (var applicationSnapshot = PackageResourceSnapshot.Create(Path.Combine(temporary, "application-policy-snapshots"), applicationPackage))
            Expect(applicationSnapshot.ApplicationPolicyPublicKeyPath is not null &&
                   applicationSnapshot.ApplicationPolicyPublicKeySha256 == applicationPackage.ApplicationPolicyPublicKeySha256);
        var appArchive = CampusConfigurationArchive.Create(applicationPackagePath);
        var extractedApplication = CampusConfigurationArchive.ExtractToStore(appArchive, Path.Combine(temporary, "application-cloud-package"));
        Expect(extractedApplication.SchemaVersion == 4 &&
               extractedApplication.ApplicationPolicyPublicKeySha256 == applicationPackage.ApplicationPolicyPublicKeySha256);
        var privateKeySource = Path.Combine(temporary, "source-private.pem");
        using (var privateKey = RSA.Create(2048))
            File.WriteAllText(privateKeySource, privateKey.ExportRSAPrivateKeyPem());
        Reject(() => PackageBuilder.Build(Path.Combine(temporary, "private-key-package"),
            "campus-demo", "PC-", privateKeySource));
        Reject(() => PackageBuilder.Build(Path.Combine(temporary, "private-policy-key-package"),
            "campus-demo", "PC-", publicKeySource, File.ReadAllText(privateKeySource)));
        Reject(() => PackageBuilder.Build(Path.Combine(temporary, "private-app-policy-key-package"),
            "campus-demo", "PC-", publicKeySource, policySigner.ExportSubjectPublicKeyInfoPem(),
            applicationPolicyPublicKeyPem: File.ReadAllText(privateKeySource)));
        var contaminated = Path.Combine(temporary, "contaminated-package");
        Directory.CreateDirectory(contaminated);
        File.WriteAllText(Path.Combine(contaminated, "admin.txt"), "fixture-secret");
        Reject(() => PackageBuilder.Build(contaminated, "campus-demo", "PC-", publicKeySource));
        Expect(File.ReadAllText(Path.Combine(contaminated, "admin.txt")) == "fixture-secret");
    });
    Check("Windows 父级目录 junction 会被部署包入口和加载器拒绝", () =>
    {
        if (!OperatingSystem.IsWindows()) return;

        var targetParent = Path.Combine(temporary, "junction-target");
        var legacyRoot = Path.Combine(targetParent, "legacy");
        var modernRoot = Path.Combine(targetParent, "modern");
        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "campus.json"), JsonSerializer.Serialize(new
        {
            campus = "演示校区", computerPrefix = "PC-", keyFile = "demo-public.pem"
        }));
        File.Copy(publicPath, Path.Combine(legacyRoot, "demo-public.pem"));
        PackageBuilder.Build(modernRoot, "演示校区", "PC-", publicPath);

        var junction = Path.Combine(temporary, "junction-alias");
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", junction, targetParent })
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo)
                ?? throw new Exception("无法启动 Windows junction 回归测试。");
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                throw new Exception("Windows junction 创建超时。");
            }
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (process.ExitCode != 0)
                throw new Exception("Windows junction 创建失败：" + output);
            Expect(Directory.Exists(junction) &&
                   (File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);

            var legacyAlias = Path.Combine(junction, "legacy");
            var modernAlias = Path.Combine(junction, "modern");
            Expect(!PackageSource.IsCandidate(legacyAlias));
            Reject(() => PackageSource.Resolve(Path.Combine(legacyAlias, "campus.json")));
            Reject(() => PackageContext.LoadLegacy(legacyAlias));
            Reject(() => PackageContext.Load(modernAlias));
            Reject(() => PackageManifest.Load(modernAlias));
            Reject(() => PackageSource.Resolve(Path.Combine(modernAlias, "manifest.json")));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction, recursive: false);
        }
        Expect(Directory.Exists(targetParent));
    });
    Check("Veyon 密钥清单识别完整密钥、缺失密钥与异常状态", () =>
    {
        var keyId = VeyonAuthKeyId.ForCampus("campus-demo");
        Expect(VeyonTeacherKeyProvisioner.ParseListing("", keyId) == VeyonAuthKeyListingState.Missing);
        Expect(VeyonTeacherKeyProvisioner.ParseListing($"other/public\r\n{keyId}/private\n{keyId}/public", keyId) ==
               VeyonAuthKeyListingState.CompletePair);
        Expect(VeyonTeacherKeyProvisioner.ParseListing($"{keyId}/public", keyId) == VeyonAuthKeyListingState.PublicOnly);
        Expect(VeyonTeacherKeyProvisioner.ParseListing($"{keyId}/private", keyId) == VeyonAuthKeyListingState.PrivateOnly);
        Expect(VeyonTeacherKeyProvisioner.ParseListing("Veyon key listing unavailable", keyId) == VeyonAuthKeyListingState.Unrecognized);
    });
    Check("空值、错误类型与短位长公钥被拒绝", () =>
    {
        var modernRoot = Path.Combine(temporary, "modern");
        var modernManifest = Path.Combine(modernRoot, "manifest.json");
        var modernSetup = Path.Combine(modernRoot, "resources", "veyon-test.exe");
        object ModernManifest(object publicKey) => new {
            schemaVersion = 1,
            packageId = "d2b7de4e-0c8b-4d2e-9f3a-1b2c3d4e5f60",
            targetOs = "windows", architecture = "x64",
            campus = "演示校区", computerPrefix = "PC-",
            publicKey,
            installer = new {
                path = "resources/veyon-test.exe",
                size = new FileInfo(modernSetup).Length,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modernSetup)))
            }
        };
        File.WriteAllText(configPath, """{"campus":"演示校区","computerPrefix":"PC-"}""");
        Reject(() => PackageContext.LoadLegacy(temporary));
        File.WriteAllText(configPath, """{"campus":"演示校区","computerPrefix":12,"keyFile":"demo-public.pem"}""");
        Reject(() => PackageContext.LoadLegacy(temporary));
        File.WriteAllText(configPath, """{"campus":null,"computerPrefix":"PC-","keyFile":"demo-public.pem"}""");
        Reject(() => PackageContext.LoadLegacy(temporary));
        File.WriteAllText(configPath, new string(' ', 64 * 1024 + 1));
        Reject(() => PackageContext.LoadLegacy(temporary));
        Config();
        File.WriteAllText(modernManifest, JsonSerializer.Serialize(
            ModernManifest(new { path = "keys/demo-public.pem", size = 0, sha256 = "0" })));
        Reject(() => PackageContext.Load(modernRoot));
        var nullSizeManifest = JsonSerializer.Serialize(
            ModernManifest(new { path = "keys/demo-public.pem", size = 0, sha256 = "0" }));
        File.WriteAllText(modernManifest,
            nullSizeManifest.Replace("\"path\":\"keys/demo-public.pem\",\"size\":0", "\"path\":\"keys/demo-public.pem\",\"size\":null", StringComparison.Ordinal));
        Reject(() => PackageContext.Load(modernRoot));
        var oversizedKeyPath = Path.Combine(modernRoot, "keys", "oversized-public.pem");
        File.WriteAllBytes(oversizedKeyPath, new byte[64 * 1024 + 1]);
        File.WriteAllText(modernManifest, JsonSerializer.Serialize(ModernManifest(new {
            path = "keys/oversized-public.pem",
            size = new FileInfo(oversizedKeyPath).Length,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(oversizedKeyPath)))
        })));
        Reject(() => PackageContext.Load(modernRoot));
        File.Delete(oversizedKeyPath);
        using var shortKey = RSA.Create(1024);
        var shortKeyPem = shortKey.ExportSubjectPublicKeyInfoPem();
        Expect(shortKeyPem.Contains("BEGIN PUBLIC KEY") && !shortKeyPem.Contains("PRIVATE KEY"));
        var shortKeyPath = Path.Combine(modernRoot, "keys", "short-public.pem");
        File.WriteAllText(shortKeyPath, shortKeyPem);
        File.WriteAllText(modernManifest, JsonSerializer.Serialize(ModernManifest(new {
            path = "keys/short-public.pem",
            size = new FileInfo(shortKeyPath).Length,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(shortKeyPath)))
        })));
        Reject(() => PackageContext.Load(modernRoot));
    });
}
finally { Directory.Delete(temporary, recursive: true); }
Console.WriteLine($"All {passed} checks passed.");
