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

Check("编号标准化及命名边界", () =>
{
    Expect(DeploymentPlan.ValidateComputerName("A-PC-", "3") == "A-PC-03");
    Expect(DeploymentPlan.ValidateComputerName("PC-", "99") == "PC-99");
    Expect(DeploymentPlan.ValidateComputerName("ABCDEFGHIJKLM", "1").Length == 15);
    foreach (var number in new[] { "0", "100", "-1", "1.0", "０３", "", " 3" })
        Reject(() => DeploymentPlan.ValidateComputerName("PC-", number));
    foreach (var prefix in new[] { "../", "PC_", "-PC", "123", "ABCDEFGHIJKLMN" })
        Reject(() => DeploymentPlan.ValidateComputerName(prefix, "1"));
});
Check("默认不改名，显式选择后才进入计划", () =>
{
    var plan = DeploymentPlan.Create("演示校区", "PC-", "1", false);
    Expect(!plan.RenameComputer && !plan.Steps.Any(s => s.Contains("重命名")));
    Expect(DeploymentPlan.Create("演示校区", "PC-", "1", true).Steps.Any(s => s.Contains("重命名")));
    Reject(() => DeploymentPlan.Create(" ", "PC-", "1", false));
});
Check("修改表单使旧预览失效；导航保留输入", () =>
{
    var vm = new MainViewModel { Campus = "演示", Number = "3" };
    vm.GeneratePreview(); Expect(vm.HasPreview && !vm.HasError);
    vm.Number = "4"; Expect(!vm.HasPreview && vm.ComputerName == "PC-04");
    vm.Navigate(false); Expect(vm.IsTeacher && vm.Number == "4");
    vm.Number = "0"; vm.GeneratePreview(); Expect(vm.HasError && !vm.HasPreview);
    vm.Number = "5"; Expect(!vm.HasError);
});

var temporary = Path.Combine(Path.GetTempPath(), "veyon-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    var configPath = Path.Combine(temporary, "campus.json");
    var publicPath = Path.Combine(temporary, "demo-public.pem");
    void Config(string key = "demo-public.pem") => File.WriteAllText(configPath,
        JsonSerializer.Serialize(new { campus = "演示校区", computerPrefix = "PC-", keyFile = key }), new UTF8Encoding(true));
    // Synthetic fixture: format checks only; cryptographic verification belongs to the deployment stage.
    File.WriteAllText(publicPath, "-----BEGIN PUBLIC KEY-----\npreview-only\n-----END PUBLIC KEY-----");
    Config();
    Check("兼容 PowerShell BOM 配置且无需 admin.txt", () =>
    {
        var package = CampusPackage.Load(temporary);
        Expect(package.Campus == "演示校区" && package.ComputerPrefix == "PC-");
        Expect(!File.Exists(Path.Combine(temporary, "admin.txt")));
    });
    Check("拒绝目录穿越及私钥文件引用", () =>
    {
        foreach (var key in new[] { "../demo-public.pem", "..\\demo-public.pem", "C:demo-public.pem", "private.pem" })
        {
            Config(key); Reject(() => CampusPackage.Load(temporary));
        }
        Config();
        File.WriteAllText(publicPath, "-----BEGIN PRIVATE KEY-----");
        Reject(() => CampusPackage.Load(temporary));
        File.WriteAllText(publicPath, ""); Reject(() => CampusPackage.Load(temporary));
    });
    Check("无效包不会保留上次校区或预览", () =>
    {
        File.WriteAllText(publicPath, "-----BEGIN PUBLIC KEY-----\npreview-only");
        var vm = new MainViewModel(); vm.LoadPackage(temporary);
        vm.Number = "3"; vm.GeneratePreview(); Expect(vm.HasPreview);
        File.WriteAllText(configPath, "[]"); vm.LoadPackage(temporary);
        Expect(vm.HasError && !vm.HasPreview && vm.Campus == "");
        File.WriteAllText(configPath, "{broken"); vm.LoadPackage(temporary);
        Expect(vm.HasError);
    });
}
finally { Directory.Delete(temporary, recursive: true); }
Console.WriteLine($"All {passed} checks passed.");
