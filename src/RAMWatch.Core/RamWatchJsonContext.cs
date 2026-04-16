using System.Text.Json.Serialization;
using RAMWatch.Core.Models;

namespace RAMWatch.Core;

/// <summary>
/// Single source-generated JSON context for all serializable types.
/// Required for Native AOT (service) and consistent serialization (GUI).
/// Add every type that crosses the IPC boundary or is persisted to disk.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ServiceState))]
[JsonSerializable(typeof(MonitoredEvent))]
[JsonSerializable(typeof(McaDetails))]
[JsonSerializable(typeof(McaClassification))]
[JsonSerializable(typeof(LiveKernelReportSummary))]
[JsonSerializable(typeof(ErrorSource))]
[JsonSerializable(typeof(IntegrityState))]
[JsonSerializable(typeof(StateMessage))]
[JsonSerializable(typeof(EventMessage))]
[JsonSerializable(typeof(ResponseMessage))]
[JsonSerializable(typeof(GetStateMessage))]
[JsonSerializable(typeof(RunIntegrityMessage))]
[JsonSerializable(typeof(UpdateSettingsMessage))]
[JsonSerializable(typeof(LogValidationMessage))]
[JsonSerializable(typeof(DeleteValidationMessage))]
[JsonSerializable(typeof(DeleteChangeMessage))]
[JsonSerializable(typeof(GetDesignationsMessage))]
[JsonSerializable(typeof(UpdateDesignationsMessage))]
[JsonSerializable(typeof(DesignationsResponseMessage))]
[JsonSerializable(typeof(SaveSnapshotMessage))]
[JsonSerializable(typeof(GetSnapshotsMessage))]
[JsonSerializable(typeof(GetDigestMessage))]
[JsonSerializable(typeof(SnapshotsResponseMessage))]
[JsonSerializable(typeof(DigestResponseMessage))]
[JsonSerializable(typeof(DeleteSnapshotMessage))]
[JsonSerializable(typeof(RenameSnapshotMessage))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<ErrorSource>))]
[JsonSerializable(typeof(List<MonitoredEvent>))]
// Phase 3 — TuningJournal types
[JsonSerializable(typeof(TimingSnapshot))]
[JsonSerializable(typeof(ConfigChange))]
[JsonSerializable(typeof(TimingDelta))]
[JsonSerializable(typeof(DriftEvent))]
[JsonSerializable(typeof(ValidationResult))]
[JsonSerializable(typeof(TimingDesignation))]
[JsonSerializable(typeof(DesignationMap))]
[JsonSerializable(typeof(Dictionary<string, TimingDelta>))]
[JsonSerializable(typeof(Dictionary<string, TimingDesignation>))]
[JsonSerializable(typeof(List<TimingSnapshot>))]
[JsonSerializable(typeof(List<ConfigChange>))]
[JsonSerializable(typeof(List<DriftEvent>))]
[JsonSerializable(typeof(List<ValidationResult>))]
// BIOS layout
[JsonSerializable(typeof(BoardVendor))]
// DriftDetector rolling window
[JsonSerializable(typeof(DriftWindow))]
[JsonSerializable(typeof(BootEntry))]
[JsonSerializable(typeof(List<BootEntry>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSerializable(typeof(BootCountEntry))]
[JsonSerializable(typeof(List<BootCountEntry>))]
[JsonSerializable(typeof(BaselineStat))]
[JsonSerializable(typeof(Dictionary<string, BaselineStat>))]
// Eras
[JsonSerializable(typeof(TuningEra))]
[JsonSerializable(typeof(List<TuningEra>))]
[JsonSerializable(typeof(CreateEraMessage))]
[JsonSerializable(typeof(CloseEraMessage))]
[JsonSerializable(typeof(MoveToEraMessage))]
// Boot fails
[JsonSerializable(typeof(BootFailEntry))]
[JsonSerializable(typeof(BootFailKind))]
[JsonSerializable(typeof(List<BootFailEntry>))]
[JsonSerializable(typeof(LogBootFailMessage))]
[JsonSerializable(typeof(DeleteBootFailMessage))]
// Minimums
[JsonSerializable(typeof(FrequencyMinimums))]
[JsonSerializable(typeof(List<FrequencyMinimums>))]
// DIMM enumeration
[JsonSerializable(typeof(DimmInfo))]
[JsonSerializable(typeof(List<DimmInfo>))]
// Thermal/power telemetry
[JsonSerializable(typeof(ThermalPowerSnapshot))]
[JsonSerializable(typeof(ThermalDataSource))]
[JsonSerializable(typeof(double[]))]
public partial class RamWatchJsonContext : JsonSerializerContext
{
}
