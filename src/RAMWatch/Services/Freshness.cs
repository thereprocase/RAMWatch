namespace RAMWatch.Services;

/// <summary>
/// How recently a sensor's value last moved. The three buckets drive the
/// glyph's fill saturation — bright for actively-updating readings, dim for
/// ones that haven't changed in a while. A cold reading is not an error;
/// plenty of fields (BIOS WMI config, DIMM SPD) are meant to stay put. The
/// bucket communicates activity, not correctness — status does that.
/// </summary>
public enum FreshnessBucket
{
    /// <summary>Value moved within the last <see cref="FreshnessComputer.LiveThreshold"/>.</summary>
    Live,

    /// <summary>Value moved within the last <see cref="FreshnessComputer.WarmThreshold"/>.</summary>
    Warm,

    /// <summary>Value hasn't moved in longer than Warm, or has never been observed.</summary>
    Cold,
}

/// <summary>
/// How the glyph's border ring should render for a given sensor. Drawn by
/// <see cref="Controls.ProvenanceGlyph"/> on top of the fill. None means no
/// ring at all — the default, used for sensors without configured
/// thresholds (most of them).
/// </summary>
public enum StatusLevel
{
    /// <summary>No threshold configured — don't draw a border.</summary>
    None,

    /// <summary>Value inside the safe band — green ring.</summary>
    Pass,

    /// <summary>Value outside safe band but not dangerous — amber ring.</summary>
    Warn,

    /// <summary>Value in the danger band — red ring, demands attention.</summary>
    Crit,
}

/// <summary>
/// Bucket a sensor's age into a <see cref="FreshnessBucket"/>. Live and
/// Warm thresholds are tuned to roughly match the service's hot and warm
/// polling tiers — Live lines up with the 3 s thermal push, Warm lines up
/// with the 30–60 s full state push. A value that fails to advance within
/// two warm cycles falls to Cold, which for boot-time configuration is the
/// correct resting state.
/// </summary>
public static class FreshnessComputer
{
    /// <summary>Values that moved within this age are Live (bright).</summary>
    public static readonly TimeSpan LiveThreshold = TimeSpan.FromSeconds(3);

    /// <summary>Values that moved within this age (but past Live) are Warm.</summary>
    public static readonly TimeSpan WarmThreshold = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Bucket an optional last-change timestamp relative to <paramref name="nowUtc"/>.
    /// Null means the observer never recorded a changing value — treat as
    /// Cold; the glyph dims and the user sees that the field is inert.
    /// </summary>
    public static FreshnessBucket Compute(DateTime? lastChangeUtc, DateTime nowUtc)
    {
        if (lastChangeUtc is null) return FreshnessBucket.Cold;
        TimeSpan age = nowUtc - lastChangeUtc.Value;
        if (age <= LiveThreshold) return FreshnessBucket.Live;
        if (age <= WarmThreshold) return FreshnessBucket.Warm;
        return FreshnessBucket.Cold;
    }
}
