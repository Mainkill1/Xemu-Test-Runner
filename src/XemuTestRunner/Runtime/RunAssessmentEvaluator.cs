using XemuTestRunner.Reliability;
using XemuTestRunner.Queue;

namespace XemuTestRunner.Runtime;

public static class RunAssessmentEvaluator
{
    public static RunAssessment Evaluate(
        string status,
        WorkloadEvaluation workload,
        ActivitySummary activity,
        JobDefinition? job)
    {
        var execution = MapExecution(status);
        var reasons = new List<string>();

        var experiment = job?.Experiment;
        var comparison =
            string.IsNullOrWhiteSpace(experiment?.Id)
                ? ComparisonEligibility.NotApplicable
                : ComparisonEligibility.Eligible;

        if (comparison == ComparisonEligibility.Eligible)
        {
            if (execution != ExecutionOutcome.Completed)
                reasons.Add(
                    $"Execution outcome is {execution}.");

            if (experiment!.RequireCorrectnessPass &&
                workload.Correctness !=
                    CorrectnessOutcome.Passed)
            {
                reasons.Add(
                    $"Correctness outcome is {workload.Correctness}; a passing correctness contract is required.");
            }

            if (experiment.RequireCompleteEvidence &&
                workload.Evidence !=
                    EvidenceOutcome.Complete)
            {
                reasons.Add(
                    $"Evidence outcome is {workload.Evidence}; complete evidence is required.");
            }

            var operatorInterventions =
                activity.ManualInputs +
                activity.Pauses +
                activity.Screenshots +
                activity.PreviewCaptures +
                activity.BulkTransfers +
                activity.HostStateChanges;

            if (!experiment.AllowOperatorIntervention &&
                operatorInterventions > 0)
            {
                reasons.Add(
                    "Operator, transfer, preview, or host-state intervention was recorded.");
            }

            if (!experiment.AllowDiagnostics &&
                activity.Diagnostics > 0)
            {
                reasons.Add(
                    "Diagnostic tooling was used during the attempt.");
            }

            if (reasons.Count > 0)
                comparison =
                    ComparisonEligibility.Ineligible;
        }

        return new RunAssessment(
            execution,
            workload.Correctness,
            workload.Evidence,
            comparison,
            reasons,
            workload.Checks);
    }

    private static ExecutionOutcome MapExecution(
        string status) =>
        status.ToLowerInvariant() switch
        {
            "completed" =>
                ExecutionOutcome.Completed,
            "failed" or
            "plan_failed" or
            "incomplete_plan" =>
                ExecutionOutcome.Failed,
            "timeout" =>
                ExecutionOutcome.TimedOut,
            "cancelled" =>
                ExecutionOutcome.Cancelled,
            "unresponsive" =>
                ExecutionOutcome.Unresponsive,
            "invalid_job" or
            "preflight_failed" or
            "package_changed" =>
                ExecutionOutcome.InvalidInput,
            "runner_error" or
            "control_error" or
            "start_failed" or
            "evidence_failure" =>
                ExecutionOutcome.RunnerFailure,
            "cleanup_failed" =>
                ExecutionOutcome.CleanupFailure,
            _ => ExecutionOutcome.NotStarted
        };
}
