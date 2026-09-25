using System.Security.Cryptography;
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
void Expect(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
void Reject(Action action)
{
    try { action(); } catch (InvalidDataException) { return; }
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
Check("界面状态：修改选项清除预览，教师清单同步边界", () =>
{
    var vm = new MainViewModel { RenameComputer = true, Number = "3" };
    vm.GeneratePreview(); Expect(vm.HasPreview && !vm.HasError);
    vm.Number = "100"; Expect(!vm.HasPreview && vm.ComputerName == "PC-100");
    vm.Navigate(false); Expect(vm.IsTeacher && vm.Number == "100");
    vm.GenerateRoomPreview(); Expect(vm.HasRoomPreview && vm.RoomNames.Count == 150);
    vm.RoomCount = "151"; Expect(!vm.HasRoomPreview);
    vm.GenerateRoomPreview(); Expect(vm.HasRoomError);
    vm.Number = "0"; vm.GeneratePreview(); Expect(vm.HasError && !vm.HasPreview);
    vm.Number = "5"; Expect(!vm.HasError);
});
Check("只读环境检查保留未知状态且表单变化使结果失效", () =>
{
    var vm = new MainViewModel { RenameComputer = true, Number = "100" };
    vm.CheckEnvironment(); Expect(vm.HasPreflight && !vm.HasError);
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
    vm.CheckEnvironment(); Expect(vm.HasPreflight);
    vm.RenameComputer = false; vm.CheckEnvironment(); Expect(vm.HasError && !vm.HasPreflight);
});
Check("平台只读事实：不修改系统，未知项保留", () =>
{
    var facts = PlatformFacts.Collect();
    Expect(facts.ComputerName == Environment.MachineName && facts.IsWindows == OperatingSystem.IsWindows());
    Expect(facts.OperatingSystemVersion.Length > 0 && facts.SystemArchitecture.Length > 0);
    foreach (var detail in new[] { facts.ElevationDetail, facts.RebootDetail, facts.DiskDetail, facts.VeyonDetail })
        Expect(detail.Length > 0);
    if (!OperatingSystem.IsWindows())
    {
        Expect(facts.RebootDetail.Contains("不适用") && facts.ElevationDetail.Contains("不适用")
            && facts.DiskDetail.Contains("不适用"));
    }
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
        loaded.VerifyUnchanged();
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
