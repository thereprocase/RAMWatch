namespace RAMWatch.Core.Models;

public enum CbsStatus
{
    Unknown,
    Clean,
    CorruptionFound,
    CorruptionRepaired,
    ScanFailed
}

public enum IntegrityCheckStatus
{
    NotRun,
    Running,
    Clean,
    CorruptionFound,
    CorruptionRepaired,
    Failed,
    Unknown
}

public sealed record IntegrityState(
    int CbsCorruptionCount,
    IntegrityCheckStatus SfcStatus,
    IntegrityCheckStatus DismStatus
);
