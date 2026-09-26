using System.Reflection;
using System.Text;
using System.Text.Json;
using VeyonCampus.App;
using VeyonCampus.Core;

internal static class ReviewRegressionChecks
{
    private static void Expect(bool condition)
    {
        if (!condition) throw new Exception("Review regression assertion failed.");
    }

    public static void Run(Action<string, Action> check, string temporary)
    {
        check("进程边界：成功、非零退出码、启动失败与超时", () =>
        {
            var launcher = new DefaultProcessLauncher();
            ProcessOutcome Child(string mode, TimeSpan? timeout = null)
            {
                var executable = Environment.ProcessPath!;
                var arguments = new List<string>();
                if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    arguments.Add(Assembly.GetExecutingAssembly().Location);
                arguments.AddRange(["--process-fixture", mode]);
                return launcher.Run(executable, arguments, temporary, timeout ?? TimeSpan.FromSeconds(15));
            }
            var success = Child("success");
            Expect(success.Ok && success.ExitCode == 0 && success.Stdout == "fixture-output");
            var failed = Child("fail");
            Expect(!failed.Ok && failed.Kind == ProcessOutcomeKind.Failed && failed.ExitCode == 7);
            var missing = launcher.Run(Path.Combine(temporary, "missing-executable"), [], temporary, TimeSpan.FromSeconds(1));
            Expect(!missing.Ok && missing.Kind == ProcessOutcomeKind.LaunchRefused);
            var timeout = Child("timeout", TimeSpan.FromMilliseconds(500));
            Expect(!timeout.Ok && timeout.Kind == ProcessOutcomeKind.TimedOut && timeout.ModifiedBeforeFailure);
        });

        check("账户操作：所有组合在修改前阻断，直接调用也不启动进程", () =>
        {
            var launcher = new FakeProcessLauncher((_, _) => throw new Exception("Account mutation was attempted."));
            var adapter = new WindowsAccountAdapter(launcher);
            Expect(!adapter.CreateStudentAccount("Student").Ok);
            Expect(!adapter.ChangeAdminPassword("Admin", "fixture-password").Ok);
            Expect(launcher.Calls.Count == 0);
            for (var mask = 4; mask < 16; mask++)
            {
                var vm = new MainViewModel
                {
                    InstallVeyon = (mask & 1) != 0, RenameComputer = (mask & 2) != 0,
                    CreateStudent = (mask & 4) != 0, ChangeAdminPassword = (mask & 8) != 0,
                    Number = "3", AdminAccountName = "Admin"
                };
                if (!vm.CreateStudent && !vm.ChangeAdminPassword) continue;
                vm.GeneratePreview();
                Expect(!vm.CanStartDeployment);
                vm.RunDeploymentAsync().GetAwaiter().GetResult();
                Expect(vm.Error == WindowsAccountAdapter.PreviewOnlyReason && !vm.HasExecution && !vm.IsExecuting);
            }
            var input = new PlanInput("", "PC-", "3", "Student", "Admin",
                new(false, false, true, false), null);
            Expect(ReadOnlyPreflight.Check(input).Checks.Any(c =>
                c.Id == "account-execution" && c.Level == CheckLevel.Blocked));
        });

        check("最终验证：公钥、服务、版本未确认不能汇总成功，日志保留待重启", () =>
        {
            var good = new VeyonVerification("已安装", $"版本 {VeyonInstallerTrust.Version}。", true,
                "指纹已核对", "VeyonService 正在运行。");
            Expect(good.ToStepResult().Ok);
            foreach (var verification in new[]
            {
                good with { KeyImported = false }, good with { ServiceState = "未知" },
                good with { Version = "版本未确认" }, good with { InstallState = "未安装" }
            })
            {
                var summary = ExecutionPlan.Summarize([
                    new("veyon-key", ExecutionPlan.Succeeded, "命令返回"),
                    new("rename", ExecutionPlan.RequiresReboot, "待重启", RebootRequired: true),
                    verification.ToStepResult()]);
                Expect(summary.Status == ExecutionPlan.NeedsReview && summary.RebootRequired);
                var log = DeploymentRunLog.Create(Path.Combine(temporary, "runs"), "fixture-plan");
                log.ReportEvent("verification", "verify", verification.ToStepResult(), null);
                log.Finish(summary.Status, summary.RebootRequired);
                using var end = JsonDocument.Parse(File.ReadLines(log.LogPath).Last());
                Expect(end.RootElement.GetProperty("status").GetString() == summary.Status &&
                       end.RootElement.GetProperty("rebootRequired").GetBoolean());
            }
        });

        // Windows management calls below are fakes; no real mutation is launched.
        if (!OperatingSystem.IsWindows()) return;
        check("域检查：工作组、域成员、空输出、错误字段及非零退出码", () =>
        {
            foreach (var (output, status) in new[]
            {
                ("{\"PartOfDomain\":false}", ExecutionPlan.Succeeded),
                ("{\"PartOfDomain\":true}", ExecutionPlan.Failed),
                ("", ExecutionPlan.NeedsReview), ("{}", ExecutionPlan.NeedsReview),
                ("{\"PartOfDomain\":\"false\"}", ExecutionPlan.NeedsReview)
            })
            {
                var fake = new FakeProcessLauncher((_, args) =>
                {
                    Expect(Script(args).Contains("Get-CimInstance -ClassName Win32_ComputerSystem"));
                    return FakeProcessLauncher.Success(output);
                });
                Expect(new WindowsRenameAdapter(fake).CheckDomainMembership().Status == status);
                if (status != ExecutionPlan.Succeeded)
                {
                    Expect(!new WindowsRenameAdapter(fake).RequestRename("PC-03").Ok);
                    Expect(fake.Calls.All(c => !Script(c.Arguments).Contains("Rename-Computer")));
                }
            }
            var failed = new FakeProcessLauncher((_, _) => FakeProcessLauncher.Success("{\"PartOfDomain\":false}")
                with { Kind = ProcessOutcomeKind.Failed, ExitCode = 1 });
            Expect(new WindowsRenameAdapter(failed).CheckDomainMembership().Status == ExecutionPlan.NeedsReview);
        });

        check("改名：正确命令、精确待生效名称、幂等与超时阻断", () =>
        {
            FakeProcessLauncher RenameFixture(string configured, bool alreadyPending = false,
                bool timeout = false)
            {
                var reads = 0;
                return new((_, args) =>
                {
                    var script = Script(args);
                    if (script.Contains("Get-CimInstance")) return FakeProcessLauncher.Success("{\"PartOfDomain\":false}");
                    if (script.Contains("Get-ItemProperty"))
                        return FakeProcessLauncher.Success(JsonSerializer.Serialize(new ComputerNameState("PC-01",
                            reads++ == 0 && !alreadyPending ? "PC-01" : configured)));
                    Expect(script.Contains("Rename-Computer -NewName 'PC-03'") && !script.Contains("-Restart"));
                    return timeout ? FakeProcessLauncher.Success() with { Kind = ProcessOutcomeKind.TimedOut }
                        : FakeProcessLauncher.Success();
                });
            }
            var correct = RenameFixture("PC-03");
            var result = new WindowsRenameAdapter(correct).RequestRename("PC-03");
            Expect(result.Status == ExecutionPlan.RequiresReboot && result.RebootRequired);
            Expect(correct.Calls.Count(c => Script(c.Arguments).Contains("Rename-Computer")) == 1);
            Expect(new WindowsRenameAdapter(RenameFixture("PC-99")).RequestRename("PC-03").Status == ExecutionPlan.NeedsReview);
            foreach (var target in new[] { "PC-03", "PC-99" })
            {
                var pending = RenameFixture(target, alreadyPending: true);
                var pendingResult = new WindowsRenameAdapter(pending).RequestRename("PC-03");
                Expect(pendingResult.Status == (target == "PC-03" ? ExecutionPlan.RequiresReboot : ExecutionPlan.NeedsReview));
                Expect(!pending.Calls.Any(c => Script(c.Arguments).Contains("Rename-Computer")));
            }
            var timedOut = RenameFixture("PC-03", timeout: true);
            Expect(new WindowsRenameAdapter(timedOut).RequestRename("PC-03").Status == ExecutionPlan.NeedsReview);
            Expect(timedOut.Calls.Count(c => Script(c.Arguments).Contains("Rename-Computer")) == 1);
            var unchanged = RenameFixture("PC-01");
            Expect(new WindowsRenameAdapter(unchanged).RequestRename("PC-01").Status == ExecutionPlan.Skipped);
            Expect(!unchanged.Calls.Any(c => Script(c.Arguments).Contains("Rename-Computer")));
        });

        check("账户 SID：名称必须匹配，只接受结构化 SID，正确转义单引号", () =>
        {
            const string sid = "S-1-5-21-111-222-333-1001";
            var launcher = new FakeProcessLauncher((_, args) =>
            {
                Expect(Script(args).Contains("Get-LocalUser -Name 'O''Brien'"));
                return FakeProcessLauncher.Success(JsonSerializer.Serialize(new { Name = "O'Brien", Sid = sid }));
            });
            Expect(new WindowsAccountAdapter(launcher).ReadAccount("O'Brien").Sid == sid);
            foreach (var output in new[] { "{}", "garbage", "{\"Name\":\"Other\",\"Sid\":\"" + sid + "\"}",
                         "{\"Name\":\"Student\",\"Sid\":\"invalid\"}" })
                Expect(new WindowsAccountAdapter(new FakeProcessLauncher((_, _) => FakeProcessLauncher.Success(output)))
                    .ReadAccount("Student").Sid is null);
        });
    }

    private static string Script(IReadOnlyList<string> arguments) =>
        Encoding.Unicode.GetString(Convert.FromBase64String(arguments[^1]));
}

internal sealed class FakeProcessLauncher(Func<string, IReadOnlyList<string>, ProcessOutcome> run) : IProcessLauncher
{
    public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];
    public ProcessOutcome Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        TimeSpan timeout, Action<string, string, int?, string>? log = null)
    {
        Calls.Add((fileName, arguments));
        return run(fileName, arguments);
    }
    public static ProcessOutcome Success(string output = "") =>
        new("fixture", [], ProcessOutcomeKind.Success, 0, output, "");
}
