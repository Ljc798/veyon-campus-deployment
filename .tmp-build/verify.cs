using VeyonCampus.Core;
var ctx = PackageContext.Load(@"F:\github\veyon-campus-deployment\test-deployment\校园测试");
Console.WriteLine("campus=" + ctx.Campus + " prefix=" + ctx.ComputerPrefix + " schema=" + ctx.SchemaVersion);
Console.WriteLine("installer=" + ctx.InstallerPath);
ctx.VerifyUnchanged();
Console.WriteLine("VerifyUnchanged OK");
