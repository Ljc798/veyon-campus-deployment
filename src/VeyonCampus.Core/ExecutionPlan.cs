using System.Security.Cryptography;
using System.Text.Json;

namespace VeyonCampus.Core;

/// <summary>
/// Frozen execution plan. The plan and its ordered steps are immutable; live
/// outcomes are carried separately in <see cref="StepResult"/> records.
/// </summary>
public sealed class ExecutionPlan
{
    public const string NotStarted = "not-started";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Skipped = "skipped";
    public const string RequiresReboot = "requires-reboot";
    public const string PartiallyCompleted = "partially-completed";
    public const string NeedsReview = "needs-review";

    private ExecutionPlan(string planId, PlanInput input, IReadOnlyList<ExecutionStep> steps,
        PackageContext? package, string planFingerprint)
    {
        PlanId = planId;
        Input = input;
        Steps = Array.AsReadOnly(steps.ToArray());
        Package = package;
        PlanFingerprint = planFingerprint;
    }

    public string PlanId { get; }
    public PlanInput Input { get; }
    public IReadOnlyList<ExecutionStep> Steps { get; }
    public PackageContext? Package { get; }
    public string PlanFingerprint { get; }

    public static ExecutionPlan Create(PlanInput input, PackageContext? package)
    {
        if (input.Package != package)
            throw new InvalidDataException("冻结计划的部署包与输入资料不一致。");

        var frozen = DeploymentPlan.Create(input);
        var descriptions = frozen.Steps.ToDictionary(step => step.Id, step => step.Description, StringComparer.Ordinal);
        var steps = new List<ExecutionStep>();
        var priorStepIds = new List<string>();

        // Keep the architecture's operation order explicit in each dependency list:
        // accounts → Veyon install → Veyon key import → computer rename.
        void Add(string id, bool mayRequireReboot = false, bool automaticallyReversible = false)
        {
            steps.Add(new ExecutionStep(id, descriptions[id], priorStepIds,
                mayRequireReboot, automaticallyReversible));
            priorStepIds.Add(id);
        }

        if (input.Operations.CreateStudent) Add("student-account");
        if (input.Operations.ChangeAdminPassword) Add("admin-password");
        if (input.Operations.InstallVeyon)
        {
            Add("veyon-install", mayRequireReboot: true);
            Add("veyon-key");
        }
        if (input.Operations.RenameComputer) Add("rename", mayRequireReboot: true);

        var planId = Guid.NewGuid().ToString("N");
        var fingerprintInput = new
        {
            planId,
            frozen.Campus,
            frozen.ComputerName,
            input.Operations,
            PackageFingerprint = package?.PackageFingerprint,
            Steps = steps.Select(step => new
            {
                step.Id,
                step.Description,
                step.DependsOn,
                step.MayRequireReboot,
                step.AutomaticallyReversible
            })
        };
        var planFingerprint = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(fingerprintInput)));
        return new ExecutionPlan(planId, input, steps, package, planFingerprint);
    }

    public static ExecutionSummary Summarize(IEnumerable<StepResult> results)
    {
        var steps = results.ToArray();
        var rebootRequired = steps.Any(step => step.RebootRequired || step.Status == RequiresReboot);
        var completed = steps.Any(step => step.Status == Succeeded);
        string status;

        if (steps.Length == 0)
            status = NotStarted;
        else if (steps.Any(step => step.Status == NeedsReview))
            status = NeedsReview;
        else if (steps.Any(step => step.Status == PartiallyCompleted))
            status = PartiallyCompleted;
        else if (steps.Any(step => step.Status is Cancelled or Failed))
            status = completed ? PartiallyCompleted : rebootRequired ? RequiresReboot :
                steps.Any(step => step.Status == Cancelled) ? Cancelled : Failed;
        else if (rebootRequired)
            status = RequiresReboot;
        else if (steps.All(step => step.Status is Succeeded or Skipped))
            status = Succeeded;
        else
            status = NeedsReview;

        return new ExecutionSummary(status, rebootRequired, Array.AsReadOnly(steps));
    }
}

public sealed class ExecutionStep
{
    internal ExecutionStep(string id, string description, IEnumerable<string> dependsOn,
        bool mayRequireReboot, bool automaticallyReversible)
    {
        Id = id;
        Description = description;
        DependsOn = Array.AsReadOnly(dependsOn.ToArray());
        MayRequireReboot = mayRequireReboot;
        AutomaticallyReversible = automaticallyReversible;
    }

    public string Id { get; }
    public string Description { get; }
    public IReadOnlyList<string> DependsOn { get; }
    public bool MayRequireReboot { get; }
    public bool AutomaticallyReversible { get; }
}

public sealed record ExecutionSummary(string Status, bool RebootRequired, IReadOnlyList<StepResult> Steps);

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
