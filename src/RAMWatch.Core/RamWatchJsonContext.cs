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
[JsonSerializable(typeof(ErrorSource))]
[JsonSerializable(typeof(IntegrityState))]
[JsonSerializable(typeof(StateMessage))]
[JsonSerializable(typeof(EventMessage))]
[JsonSerializable(typeof(ResponseMessage))]
[JsonSerializable(typeof(GetStateMessage))]
[JsonSerializable(typeof(RunIntegrityMessage))]
[JsonSerializable(typeof(UpdateSettingsMessage))]
[JsonSerializable(typeof(List<ErrorSource>))]
[JsonSerializable(typeof(List<MonitoredEvent>))]
public partial class RamWatchJsonContext : JsonSerializerContext
{
}
