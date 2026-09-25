namespace VeyonCampus.Core;

/// <summary>
/// Immutable execution plan (P3-02/03). Steps are produced in dependency order;
/// no step may run until the plan is frozen. Passwords never appear here.
/// </summary>
public sealed record ExecutionPlan(
    string PlanId,
    PlanInput Input,
    IReadOnlyList<ExecutionStep> Steps,
    PackageContext? Package,
    string PlanFingerprint)
{
    public const string NotStarted = "not-started";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Skipped = "skipped";

    public static ExecutionPlan Create(PlanInput input, PackageContext? package)
    {
        var frozen = DeploymentPlan.Create(input);
        var steps = new List<ExecutionStep>();
        // 与 DeploymentPlan.Create 保持同一顺序（架构文档 §3）：
        // 账户 → 改密 → Veyon → 改名。
        if (input.Operations.CreateStudent)
            steps.Add(new ExecutionStep(
                frozen.Steps.First(s => s.Id == "student-account").Description,
                frozen.Steps.First(s => s.Id == "student-account").Id));
        if (input.Operations.ChangeAdminPassword)
            steps.Add(new ExecutionStep(
                frozen.Steps.First(s => s.Id == "admin-password").Description,
                frozen.Steps.First(s => s.Id == "admin-password").Id));
        if (input.Operations.InstallVeyon)
        {
            steps.Add(new ExecutionStep(
                frozen.Steps.First(s => s.Id == "veyon-install").Description,
                frozen.Steps.First(s => s.Id == "veyon-install").Id));
            steps.Add(new ExecutionStep(
                frozen.Steps.First(s => s.Id == "veyon-key").Description,
                frozen.Steps.First(s => s.Id == "veyon-key").Id));
        }
        if (input.Operations.RenameComputer)
            steps.Add(new ExecutionStep(
                frozen.Steps.First(s => s.Id == "rename").Description,
                frozen.Steps.First(s => s.Id == "rename").Id));
        var planId = Guid.NewGuid().ToString("N");
        var planFingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    $"{planId}|{frozen.Campus}|{frozen.ComputerName}|{input.Operations}|{package?.PackageFingerprint}")));
        return new ExecutionPlan(planId, input, steps, package, planFingerprint);
    }
}

public sealed record ExecutionStep(string Description, string Id)
{
    public string Status { get; internal set; } = ExecutionPlan.NotStarted;
    public string? ErrorDetail { get; internal set; }
}

/// <summary>Structured, log-safe result for one step. No passwords or secrets.</summary>
public sealed record StepResult(
    string StepId,
    string Status,
    string Detail,
    int? ExitCode = null,
    bool RebootRequired = false)
{
    public bool Ok => Status == ExecutionPlan.Succeeded || Status == ExecutionPlan.Skipped;
}
