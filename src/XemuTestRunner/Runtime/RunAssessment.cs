namespace XemuTestRunner.Runtime;

public enum ExecutionOutcome
{
    NotStarted,
    Completed,
    Failed,
    TimedOut,
    Cancelled,
    Unresponsive,
    InvalidInput,
    RunnerFailure,
    CleanupFailure,
    Held,
    Crashed
}

public enum CorrectnessOutcome
{
    NotEvaluated,
    Passed,
    Failed,
    Incomplete
}

public enum EvidenceOutcome
{
    NotEvaluated,
    Complete,
    Incomplete,
    Invalid
}

public enum ComparisonEligibility
{
    NotApplicable,
    NotEvaluated,
    Eligible,
    Ineligible
}

public sealed record AssessmentCheck(
    string Name,
    bool Passed,
    string Category,
    string Detail);

public sealed record RunAssessment(
    ExecutionOutcome Execution,
    CorrectnessOutcome Correctness,
    EvidenceOutcome Evidence,
    ComparisonEligibility Comparison,
    IReadOnlyList<string> ComparisonReasons,
    IReadOnlyList<AssessmentCheck> Checks)
{
    public bool IsExecutionSuccess =>
        Execution == ExecutionOutcome.Completed;

    public bool IsCorrectnessSuccess =>
        Correctness is CorrectnessOutcome.Passed or
        CorrectnessOutcome.NotEvaluated;
}
