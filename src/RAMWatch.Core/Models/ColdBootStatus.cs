namespace RAMWatch.Core.Models;

/// <summary>
/// Per-reader first-success timestamps for the cold-tier readers whose data
/// is boot-time (doesn't change during runtime). Populated by the service as
/// each reader produces its first non-null result; once every field is set,
/// <see cref="IsComplete"/> flips to true and downstream consumers (drift
/// detection, stale-tuning-button gating on peer clients) unblock.
///
/// Three stamps, not five: UMC timings, SMU PM table, and BIOS WMI all feed
/// a single <c>TimingSnapshot</c> from one hardware read cycle, so they
/// succeed or fail together. Splitting them into separate stamps would be
/// ceremony without substance — they share a fate.
/// </summary>
public sealed class ColdBootStatus
{
    /// <summary>
    /// First time a non-null <c>TimingSnapshot</c> was published. Carries the
    /// combined UMC / SMU PM / BIOS WMI cold reads.
    /// </summary>
    public DateTime? TimingsStampedUtc { get; init; }

    /// <summary>
    /// First time <c>List&lt;DimmInfo&gt;</c> was populated from the WMI DIMM
    /// enumeration (Win32_PhysicalMemory).
    /// </summary>
    public DateTime? DimmsStampedUtc { get; init; }

    /// <summary>
    /// First time the UMC DRAM address-map configuration finished reading.
    /// </summary>
    public DateTime? AddressMapStampedUtc { get; init; }

    /// <summary>
    /// True when every cold-tier reader has stamped. Consumers that must not
    /// mistake a half-populated startup state for a drift condition gate on
    /// this.
    /// </summary>
    public bool IsComplete =>
        TimingsStampedUtc    is not null &&
        DimmsStampedUtc      is not null &&
        AddressMapStampedUtc is not null;
}
