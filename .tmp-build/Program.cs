using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VeyonCampus.App;
using VeyonCampus.Core;

// Load the real test package the user will drag into the App
var pkgPath = @"F:\github\veyon-campus-deployment\test-deployment\校园测试";
var ctx = PackageContext.Load(pkgPath);
Console.WriteLine($"package root       = {ctx.Root}");
Console.WriteLine($"campus             = {ctx.Campus}");
Console.WriteLine($"computerPrefix     = {ctx.ComputerPrefix}");
Console.WriteLine($"schemaVersion      = {ctx.SchemaVersion}");
Console.WriteLine($"publicKeyPath      = {ctx.PublicKeyPath}");
Console.WriteLine($"publicKeyFingerprint = {ctx.PublicKeyFingerprint[..12]}…");
Console.WriteLine($"installerPath      = {ctx.InstallerPath}");
ctx.VerifyUnchanged();
Console.WriteLine("VerifyUnchanged OK — installer resource intact");

// Simulate what the App's "开始部署" button will do, without actually installing:
// build a PlanInput that only selects InstallVeyon and confirm the plan validates.
var input = new PlanInput(
    Campus: ctx.Campus,
    Prefix: ctx.ComputerPrefix,
    Number: "1",
    StudentAccountName: "User",
    AdminAccountName: "Admin",
    Operations: new OperationSelection(true, false, false, false),
    Package: ctx);
var plan = DeploymentPlan.Create(input);
Console.WriteLine("deployment plan created:");
foreach (var step in plan.Steps)
    Console.WriteLine($"  [{step.Id}] {step.Description}");

// Confirm the Veyon read-only probe reports "not installed" on this machine (first run)
var veyon = VeyonFacts.Probe();
Console.WriteLine($"\nVeyon probe: {veyon.Status}");
Console.WriteLine($"  {veyon.AsText()}");

// Run the full preflight to confirm it still passes on Windows with the package loaded
var report = ReadOnlyPreflight.Check(input);
Console.WriteLine($"\npre-flight plan hash = {report.PlanSha256[..12]}…");
Console.WriteLine($"\n  package hash       = {report.PackageSha256[..12]}…");
foreach (var c in report.Checks)
    Console.WriteLine($"  [{c.Id}] {c.Level}: {c.Detail}");
