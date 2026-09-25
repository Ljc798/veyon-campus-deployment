namespace VeyonCampus.Core;

/// <summary>
/// Runs the already-frozen, ordered plan through trusted step handlers. This
/// coordinator does not launch processes or change system state itself; the
/// application supplies fixed handlers for the operations it has implemented.
/// </summary>
public static class ExecutionCoordinator
{
    public static async Task<ExecutionSummary> RunAsync(
        ExecutionPlan plan,
        Func<ExecutionStep, Task<StepResult>> executeStep,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(executeStep);

        var results = new List<StepResult>(plan.Steps.Count);
        var successfulSteps = new HashSet<string>(StringComparer.Ordinal);
        var stopped = false;

        foreach (var step in plan.Steps)
        {
            if (stopped)
            {
                results.Add(new(step.Id, ExecutionPlan.Skipped,
                    "前置步骤未完成；此步骤没有开始。"));
                continue;
            }

            if (step.DependsOn.Any(dependency => !successfulSteps.Contains(dependency)))
            {
                results.Add(new(step.Id, ExecutionPlan.NeedsReview,
                    "计划依赖关系未满足；此步骤没有开始，需要重新检查计划。"));
                stopped = true;
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new(step.Id, ExecutionPlan.Cancelled,
                    "已在安全步骤边界停止；此步骤没有开始。"));
                stopped = true;
                continue;
            }

            StepResult result;
            try
            {
                // Do not pass the cancellation token into a live step. A caller
                // may request cancellation while it runs, but the request is
                // observed only after the handler returns at its safe boundary.
                result = await executeStep(step).ConfigureAwait(false);
                if (!string.Equals(result.StepId, step.Id, StringComparison.Ordinal))
                {
                    result = new(step.Id, ExecutionPlan.NeedsReview,
                        "执行器返回了不匹配的步骤标识；实际状态需要重新检查。");
                }
                else if (!IsKnownStatus(result.Status))
                {
                    result = new(step.Id, ExecutionPlan.NeedsReview,
                        "执行器返回了未知状态；实际状态需要重新检查。");
                }
            }
            catch (Exception ex)
            {
                // An unexpected exception does not prove that the underlying
                // operation made no change. Avoid recording exception text,
                // which could contain sensitive process output or input.
                result = new(step.Id, ExecutionPlan.NeedsReview,
                    $"执行器发生 {ex.GetType().Name}；实际系统状态需要重新检查。");
            }

            results.Add(result);
            if (result.Ok)
                successfulSteps.Add(step.Id);

            if (!result.Ok || result.RebootRequired || result.Status == ExecutionPlan.RequiresReboot)
                stopped = true;
        }

        return ExecutionPlan.Summarize(results);
    }

    private static bool IsKnownStatus(string status) => status is
        ExecutionPlan.NotStarted or ExecutionPlan.Running or ExecutionPlan.Succeeded or
        ExecutionPlan.Failed or ExecutionPlan.Cancelled or ExecutionPlan.Skipped or
        ExecutionPlan.RequiresReboot or ExecutionPlan.PartiallyCompleted or ExecutionPlan.NeedsReview;
}
