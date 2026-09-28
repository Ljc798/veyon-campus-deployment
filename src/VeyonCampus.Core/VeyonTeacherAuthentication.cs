namespace VeyonCampus.Core;

public static class VeyonTeacherAuthentication
{
    public static StepResult Configure()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || PlatformFacts.Collect().IsElevated != true)
                return new("teacher-auth", ExecutionPlan.Failed, "请以管理员身份运行教师控制台，再配置密钥认证。");
            var cli = WindowsVeyonAdapter.ResolveVeyonCliPath();
            if (cli is null) return new("teacher-auth", ExecutionPlan.Failed, "找不到 Veyon CLI，请先安装教师端 Veyon。");
            return Configure(arguments =>
            {
                var runner = new ProcessRunner();
                runner.Run(cli, arguments, Path.GetDirectoryName(cli)!, TimeSpan.FromSeconds(WindowsVeyonAdapter.CliTimeoutSeconds));
                return (runner.ExitCode, runner.Stdout);
            });
        }
        catch (Exception exception)
        {
            return new("teacher-auth", ExecutionPlan.NeedsReview, "教师密钥认证配置未完成：" + exception.Message);
        }
    }

    internal static StepResult Configure(Func<string[], (int? ExitCode, string Output)> run)
    {
        var write = run(["config", "set", "Authentication/Method", "1"]);
        if (write.ExitCode != 0)
            return new("teacher-auth", ExecutionPlan.Failed, "教师端切换密钥认证失败。", write.ExitCode);
        var read = run(["config", "get", "Authentication/Method"]);
        if (read.ExitCode != 0 || read.Output.Trim() != "1")
            return new("teacher-auth", ExecutionPlan.NeedsReview, "已尝试切换教师认证方式，但读回未确认，请检查 Veyon 配置。", read.ExitCode);
        return new("teacher-auth", ExecutionPlan.Succeeded, "教师端密钥认证已设置并读回。请关闭并重新打开 Veyon Master；教师账户仍需有对应私钥的读取权限。");
    }
}
