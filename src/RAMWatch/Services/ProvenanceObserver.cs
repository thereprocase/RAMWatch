namespace RAMWatch.Services;

/// <summary>
/// Per-sensor behavioural ledger. Every time a sensor value is recorded,
/// the observer updates a running min/max/count. Callers ask it to
/// <see cref="Adjust"/> a declared provenance against observed behaviour:
/// a "Measured" sensor that never varies across many samples is demoted
/// to Unknown, and a "Static" sensor that drifts is also demoted. The
/// observer never promotes — only the registry (or future pipe-side
/// source tags) can confer Measured status.
/// </summary>
public sealed class ProvenanceObserver
{
    /// <summary>How many samples before the demotion heuristic engages.</summary>
    public int MinSamples { get; set; } = 30;

    /// <summary>Below this (max − min), a "varied" sensor is treated as flat.</summary>
    public double FlatEpsilon { get; set; } = 1e-9;

    /// <summary>Above this (max − min), a "Static" sensor is treated as drifted.</summary>
    public double DriftEpsilon { get; set; } = 1e-4;

    private sealed class Obs
    {
        public int Count;
        public double Min = double.PositiveInfinity;
        public double Max = double.NegativeInfinity;

        // Last value we accepted, and when it arrived. LastChangeUtc only
        // advances on an actual delta — repeating the same value keeps the
        // timestamp pinned to the first time that value appeared. Freshness
        // tracks whether the sensor is moving, not whether it's being polled.
        public double LastValue = double.NaN;
        public DateTime LastChangeUtc = DateTime.MinValue;
    }

    private readonly Dictionary<string, Obs> _map = new();
    private readonly object _lock = new();

    /// <summary>
    /// Fires once per <see cref="Record"/> call with the sensor key. The
    /// glyph control subscribes to this so it can re-render when the
    /// observer's verdict on its sensor might have changed.
    /// </summary>
    public event Action<string>? SensorUpdated;

    public void Record(string key, double value)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (double.IsNaN(value)) return;

        lock (_lock)
        {
            if (!_map.TryGetValue(key, out var obs))
            {
                obs = new Obs();
                _map[key] = obs;
            }
            obs.Count++;
            if (value < obs.Min) obs.Min = value;
            if (value > obs.Max) obs.Max = value;

            // First sample or actual value change — advance the change
            // timestamp. A UMC readback that returns the same integer every
            // warm tick stays anchored to its first observation, so the
            // glyph correctly dims to Cold instead of pulsing Live on every
            // redundant poll.
            bool changed = double.IsNaN(obs.LastValue) || obs.LastValue != value;
            if (changed)
            {
                obs.LastValue = value;
                obs.LastChangeUtc = DateTime.UtcNow;
            }
        }

        SensorUpdated?.Invoke(key);
    }

    /// <summary>
    /// Last time the recorded value for <paramref name="key"/> actually
    /// changed, or null if no value has been recorded. Freshness-bucketing
    /// callers use this with <see cref="FreshnessComputer.Compute"/>.
    /// </summary>
    public DateTime? GetLastChangeUtc(string key)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(key, out var obs)) return null;
            return obs.LastChangeUtc == DateTime.MinValue ? null : obs.LastChangeUtc;
        }
    }

    /// <summary>
    /// Return the declared provenance unchanged, or a demoted Unknown
    /// variant carrying a detail message that explains the demotion. The
    /// observer stays silent until <see cref="MinSamples"/> observations
    /// have accrued — before that, whatever the registry said stands.
    /// </summary>
    public SensorProvenanceInfo Adjust(string key, SensorProvenanceInfo declared)
    {
        int count; double min, max;
        lock (_lock)
        {
            if (!_map.TryGetValue(key, out var obs)) return declared;
            count = obs.Count;
            min = obs.Min;
            max = obs.Max;
        }

        if (count < MinSamples) return declared;

        double spread = max - min;

        // Live-ish tiers that haven't moved at all → the declaration is
        // suspect. Either the sensor is genuinely flat (the device isn't
        // doing anything) or the pipe is reading a cached value. Either
        // way, don't claim Measured/Reported with a confident circle.
        if ((declared.Provenance == Provenance.Measured ||
             declared.Provenance == Provenance.Reported) &&
            spread <= FlatEpsilon)
        {
            return SensorProvenanceInfo.Unknown with
            {
                Source = declared.Source,
                Detail = $"Declared {declared.Provenance} from {declared.Source}, "
                       + $"but has not varied across {count} samples — demoted to Unknown.",
            };
        }

        // Static that has drifted → either the "static" assumption is
        // wrong, or someone changed a BIOS setting mid-boot (rare but
        // possible via vendor tools). Demote so the user sees the
        // inconsistency.
        if (declared.Provenance == Provenance.Static && spread > DriftEpsilon)
        {
            return SensorProvenanceInfo.Unknown with
            {
                Source = declared.Source,
                Detail = $"Declared Static from {declared.Source}, "
                       + $"but drifted by {spread:G3} across {count} samples — demoted to Unknown.",
            };
        }

        return declared;
    }
}

/// <summary>
/// Process-wide facade. One set of sensors, one trust ledger — there is no
/// reason to scope provenance per view model. Resolve is the single entry
/// point any caller (glyph control, clipboard export, test harness)
/// should use; it consults the registry and then runs the result through
/// the observer so a declaration the behaviour contradicts gets demoted.
/// </summary>
public static class SensorProvenanceResolver
{
    public static ProvenanceObserver Observer { get; } = new();

    public static SensorProvenanceInfo Resolve(string key)
        => Observer.Adjust(key, SensorProvenanceRegistry.For(key));

    public static SensorProvenanceInfo Resolve(string key, double value)
        => Observer.Adjust(key, SensorProvenanceRegistry.ForVoltage(key, value));
}
