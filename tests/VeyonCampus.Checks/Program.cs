using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using VeyonCampus.App;
using VeyonCampus.Core;

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
Check("1–150 编号与 99/100 边界", () =>
{
    foreach (var pair in new[] { ("1", "PC-01"), ("9", "PC-09"), ("99", "PC-99"),
                                 ("100", "PC-100"), ("149", "PC-149"), ("150", "PC-150") })
        Expect(MachineNaming.CreateName("PC-", pair.Item1) == pair.Item2);
    foreach (var number in new[] { "0", "151", "-1", "1.0", "０３", "", " 3" })
        Reject(() => MachineNaming.CreateName("PC-", number));
    Expect(MachineNaming.CreateName("ABCDEFGHIJKLM", "1").Length == 15);
    Reject(() => MachineNaming.CreateName("ABCDEFGHIJKLM", "100"));
    foreach (var prefix in new[] { "../", "PC_", "-PC", "123", "ABCDEFGHIJKLMN" })
        Reject(() => MachineNaming.CreateName(prefix, "1"));
});
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
           !VeyonFacts.IsSupportedVersionDetail("版本 4.11.3.0。") &&
           !VeyonFacts.IsSupportedVersionDetail(null));
    var keyId = VeyonAuthKeyId.ForCampus("campus-demo_01");
    Expect(keyId.Length == 33 && keyId.All(c => c is >= 'A' and <= 'P') &&
           VeyonAuthKeyId.PublicKeyForCampus("campus-demo_01") == keyId + "/public");
    Expect(VeyonAuthKeyId.ForCampus("campus-demo_01") == keyId &&
           VeyonAuthKeyId.ForCampus("campus-demo-01") != keyId);
    Reject(() => VeyonAuthKeyId.ForCampus("校区一"));
    Expect(WindowsServiceState.Parse("SERVICE_NAME: VeyonService\n        STATE              : 4  RUNNING") == WindowsServiceState.Running);
    Expect(WindowsServiceState.Parse("服务名: VeyonService\n        状态              : 1  已停止") == WindowsServiceState.Stopped);
    Expect(WindowsServiceState.Parse("SERVICE_NAME: VeyonService\n        STATE              : 3  STOP_PENDING") == WindowsServiceState.StopPending);
    Expect(WindowsServiceState.Parse("SERVICE_NAME: VeyonService\n        TYPE               : 10  WIN32_OWN_PROCESS") is null);
});
Check("机房 150 条唯一清单和起始边界", () =>
{
    var names = MachineNaming.CreateRange("A-PC-", "1", "150");
    Expect(names.Count == 150 && names.Distinct().Count() == 150);
    Expect(names[0] == "A-PC-01" && names[98] == "A-PC-99" && names[99] == "A-PC-100" && names[^1] == "A-PC-150");
    Expect(MachineNaming.CreateRange("PC-", "149", "2").Count == 2);
    Reject(() => MachineNaming.CreateRange("PC-", "149", "3"));
    Reject(() => MachineNaming.CreateRange("PC-", "1", "151"));
    Reject(() => MachineNaming.CreateRange("ABCDEFGHIJKLM", "1", "150"));
});
Check("四项操作独立；无选项不生成计划", () =>
{
    PlanInput Input(OperationSelection ops, PackageContext? package = null) =>
        new("", "PC-", "3", "User", "Admin", ops, package);
    Reject(() => DeploymentPlan.Create(Input(new(false, false, false, false))));
    var rename = DeploymentPlan.Create(Input(new(false, true, false, false)));
    Expect(rename.ComputerName == "PC-03" && rename.Steps.Any(s => s.Id == "rename") &&
           rename.Steps.All(s => !s.Id.StartsWith("veyon") && s.Id != "student-account" && s.Id != "admin-password"));
    var student = DeploymentPlan.Create(Input(new(false, false, true, false)));
    Expect(student.ComputerName is null && student.Steps.Any(s => s.Id == "student-account"));
    var admin = DeploymentPlan.Create(Input(new(false, false, false, true)));
    Expect(admin.Steps.Any(s => s.Id == "admin-password") && admin.Steps.All(s => s.Id != "rename"));
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
Check("界面状态：修改选项清除预览，教师清单同步边界", () =>
{
    var vm = new MainViewModel { RenameComputer = true, Number = "3" };
    vm.GeneratePreview();
    Expect(vm.HasPreview && !vm.HasError && vm.PreviewText.Contains("目标计算机：") &&
           vm.PreviewText.Contains("改名可能需要重启") && !vm.CanStartDeployment);
    vm.Number = "100"; Expect(!vm.HasPreview && vm.ComputerName == "PC-100");
    vm.Navigate(false); Expect(vm.IsTeacher && vm.Number == "100");
    vm.GenerateRoomPreview(); Expect(vm.HasRoomPreview && vm.RoomNames.Count == 150);
    vm.RoomCount = "151"; Expect(!vm.HasRoomPreview);
    vm.GenerateRoomPreview(); Expect(vm.HasRoomError);
    vm.Number = "0"; vm.GeneratePreview(); Expect(vm.HasError && !vm.HasPreview);
    vm.Number = "5"; Expect(!vm.HasError);
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
    else Expect(facts.IsElevated is not null);
});
Check("执行入口必须有当前预检，且拒绝未实现操作组合", () =>
{
    var vm = new MainViewModel { InstallVeyon = true };
    Expect(!vm.CanInstall && !vm.CanStartDeployment);
    vm.CheckEnvironment();
    Expect(!vm.CanInstall && !vm.CanStartDeployment);
    vm.RenameComputer = true;
    Expect(!vm.CanInstall && !vm.CanStartDeployment && !vm.HasPreflight);
});

var temporary = Path.Combine(Path.GetTempPath(), "veyon-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    var configPath = Path.Combine(temporary, "campus.json");
    var publicPath = Path.Combine(temporary, "demo-public.pem");
    using var rsa = RSA.Create(2048);
    var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
    void Config(string key = "demo-public.pem", string prefix = "PC-") => File.WriteAllText(configPath,
        JsonSerializer.Serialize(new { campus = "演示校区", computerPrefix = prefix, keyFile = key }), new UTF8Encoding(true));
    File.WriteAllText(publicPath, publicPem);
    Config();
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
        File.Delete(chosenManifest);
        Expect(!PackageSource.IsCandidate(chosenManifest));
        Reject(() => PackageSource.Resolve(chosenManifest));
    });
    Check("旧 BOM 配置可读取，RSA 指纹稳定且无需 admin.txt", () =>
    {
        var package = PackageContext.LoadLegacy(temporary);
        Expect(package.Campus == "演示校区" && package.ComputerPrefix == "PC-");
        Expect(package.PublicKeyFingerprint.Length == 64 && !File.Exists(Path.Combine(temporary, "admin.txt")));
        package.VerifyUnchanged();
        var vm = new MainViewModel(); vm.LoadPackage(temporary);
        vm.InstallVeyon = true; vm.GeneratePreview(); Expect(vm.HasPreview && !vm.HasError);
        vm.Campus = "别的校区"; Expect(vm.LoadedPackage is null && !vm.HasPreview);
        vm.GeneratePreview(); Expect(vm.HasError);
    });
    Check("公钥和配置变化导致旧计划失效", () =>
    {
        var vm = new MainViewModel(); vm.LoadPackage(temporary); vm.InstallVeyon = true;
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
        var vm = new MainViewModel(); vm.LoadPackage(configPath);
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
        Manifest(campus: new string('A', 101)); Reject(() => PackageContext.Load(root));
        Manifest(key: "keys/" + new string('a', 240) + ".pem"); Reject(() => PackageContext.Load(root));
        Manifest();
        File.WriteAllText(manifestPath, "{\"schemaVersion\":1,\"schemaVersion\":1}");
        Reject(() => PackageContext.Load(root));
    });
    Check("学生包生成将教师私钥置于包外并拒绝目录污染", () =>
    {
        var installer = Path.Combine(temporary, VeyonInstallerTrust.FileName);
        File.WriteAllBytes(installer, "MZ installer fixture"u8.ToArray());
        var installerCheck = VeyonInstallerTrust.Check(installer);
        Expect(!installerCheck.IsAllowed && !installerCheck.HashMatched);
        var viewModelOutput = Path.Combine(temporary, "viewmodel-package");
        var packageVm = new MainViewModel
        {
            CampusId = "campus-demo", RoomPrefix = "PC-", InstallerSource = installer,
            RoomOutputDir = viewModelOutput
        };
        packageVm.GenerateStudentPackage();
        Expect(packageVm.PackageOutput.Length == 0 && packageVm.PackageOutputError.Contains("大小不匹配") &&
               !Directory.Exists(viewModelOutput));
        Reject(() => PackageBuilder.Build(Path.Combine(temporary, "unsafe-campus"), "../escape", "PC-", installer));
        var output = Path.Combine(temporary, "student-package");
        var built = PackageBuilder.Build(output, "campus-demo", "PC-", installer);
        var tree = Directory.EnumerateFileSystemEntries(built, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName).ToArray();
        Expect(tree.Any(x => x == "manifest.json") && tree.Any(x => x == "campus.json"));
        Expect(tree.All(x => x is not null && !x.Contains("private", StringComparison.OrdinalIgnoreCase)));
        var teacherKeyDir = Path.Combine(temporary, "student-package-teacher-only");
        var teacherPrivate = Directory.GetFiles(teacherKeyDir, "*-private.pem").Single();
        Expect(File.ReadAllText(teacherPrivate).Contains("BEGIN RSA PRIVATE KEY"));
        if (OperatingSystem.IsWindows())
            Expect(WindowsDirectoryAclCheck.IsRestrictedToCurrentUserAndSystem(teacherKeyDir));
        else
        {
            const UnixFileMode groupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                              UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            Expect((File.GetUnixFileMode(teacherKeyDir) & groupOrOther) == 0);
            Expect((File.GetUnixFileMode(teacherPrivate) & groupOrOther) == 0);
        }
        var contaminated = Path.Combine(temporary, "contaminated-package");
        Directory.CreateDirectory(contaminated);
        File.WriteAllText(Path.Combine(contaminated, "admin.txt"), "fixture-secret");
        Reject(() => PackageBuilder.Build(contaminated, "campus-demo", "PC-", installer));
        Expect(File.ReadAllText(Path.Combine(contaminated, "admin.txt")) == "fixture-secret");
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
        Config();
        File.WriteAllText(modernManifest, JsonSerializer.Serialize(
            ModernManifest(new { path = "keys/demo-public.pem", size = 0, sha256 = "0" })));
        Reject(() => PackageContext.Load(modernRoot));
        var nullSizeManifest = JsonSerializer.Serialize(
            ModernManifest(new { path = "keys/demo-public.pem", size = 0, sha256 = "0" }));
        File.WriteAllText(modernManifest,
            nullSizeManifest.Replace("\"path\":\"keys/demo-public.pem\",\"size\":0", "\"path\":\"keys/demo-public.pem\",\"size\":null", StringComparison.Ordinal));
        Reject(() => PackageContext.Load(modernRoot));
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

[SupportedOSPlatform("windows")]
static class WindowsDirectoryAclCheck
{
    public static bool IsRestrictedToCurrentUserAndSystem(string directoryPath)
    {
        var acl = new DirectoryInfo(directoryPath).GetAccessControl();
        var allowedSids = new[]
        {
            WindowsIdentity.GetCurrent().User!.Value,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value
        };
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        return acl.AreAccessRulesProtected && rules.Length > 0 &&
               rules.All(rule => rule.AccessControlType == AccessControlType.Allow &&
                                 allowedSids.Contains(((SecurityIdentifier)rule.IdentityReference).Value));
    }
}
