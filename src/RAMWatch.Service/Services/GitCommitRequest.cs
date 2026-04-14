using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

public enum GitCommitReason
{
    ConfigChange,
    ValidationTest,
    DriftDetected,
    ManualSnapshot,
    LkgUpdated
}

public sealed class GitCommitRequest
{
    public required GitCommitReason Reason { get; init; }
    public required TimingSnapshot CurrentSnapshot { get; init; }
    public TimingSnapshot? LkgSnapshot { get; init; }
    public ConfigChange? Change { get; init; }
    public ValidationResult? Validation { get; init; }
    public List<DriftEvent>? DriftEvents { get; init; }
    public DesignationMap? Designations { get; init; }
    public List<ValidationResult>? RecentValidations { get; init; }
}
