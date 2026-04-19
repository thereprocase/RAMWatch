namespace RAMWatch.Services;

/// <summary>
/// Per-rail voltage classifiers that map a live reading to a
/// <see cref="StatusLevel"/> — the border ring around a glyph. A zero
/// reading always maps to <see cref="StatusLevel.None"/> so the ring
/// disappears until the sensor actually reports; drawing Pass on an
/// unread rail would be a false all-clear.
///
/// Thresholds come from the user's <c>project_voltage_tuning_state.md</c>
/// memo and community consensus (Veii, 1usmus, OC.net) for Zen 3 / X3D.
/// They are intentionally conservative on the Crit side — a user who is
/// deliberately running hot (cold-bench, delidded) can read past the
/// warning and keep going; a user who drifted into danger needs to see
/// red.
///
/// Numbers are cited inline on each method so a reviewer can see the
/// source of truth without cross-referencing the memo.
/// </summary>
public static class VoltageThresholds
{
    /// <summary>VCore — Ryzen 5000 X3D sweet spot is tight; 1.40 V is the
    /// informal daily ceiling for the stacked-cache part.</summary>
    public static StatusLevel Vcore(double volts) =>
        volts <= 0    ? StatusLevel.None :
        volts <= 1.35 ? StatusLevel.Pass :
        volts <= 1.40 ? StatusLevel.Warn :
                        StatusLevel.Crit;

    /// <summary>VSoC — 1.20 V is the established degradation wall; 1.15 V
    /// is a comfortable ceiling for FCLK ≤ 1900.</summary>
    public static StatusLevel Vsoc(double volts) =>
        volts <= 0    ? StatusLevel.None :
        volts <= 1.15 ? StatusLevel.Pass :
        volts <= 1.20 ? StatusLevel.Warn :
                        StatusLevel.Crit;

    /// <summary>VDIMM — DDR4 daily safe band. JEDEC calls for 1.2 V; the
    /// enthusiast daily ceiling is 1.50 V. Above that is an XOC bench
    /// voltage, not a daily.</summary>
    public static StatusLevel Vdimm(double volts) =>
        volts <= 0    ? StatusLevel.None :
        volts <= 1.45 ? StatusLevel.Pass :
        volts <= 1.50 ? StatusLevel.Warn :
                        StatusLevel.Crit;

    /// <summary>VDDP — community damage-threshold is ~1.05 V; 1.00 V is a
    /// safe daily.</summary>
    public static StatusLevel Vddp(double volts) =>
        volts <= 0    ? StatusLevel.None :
        volts <= 1.00 ? StatusLevel.Pass :
        volts <= 1.05 ? StatusLevel.Warn :
                        StatusLevel.Crit;

    /// <summary>VDDG IOD — community-safe daily band up to 1.05 V; the
    /// 1.10 V band is the warning zone before damage risk.</summary>
    public static StatusLevel VddgIod(double volts) =>
        volts <= 0    ? StatusLevel.None :
        volts <= 1.05 ? StatusLevel.Pass :
        volts <= 1.10 ? StatusLevel.Warn :
                        StatusLevel.Crit;

    /// <summary>VDDG CCD — same band as IOD. Community guidance is that
    /// IOD should be strictly above CCD, but the damage threshold is
    /// rail-local, so the classifier treats them identically.</summary>
    public static StatusLevel VddgCcd(double volts) =>
        volts <= 0    ? StatusLevel.None :
        volts <= 1.05 ? StatusLevel.Pass :
        volts <= 1.10 ? StatusLevel.Warn :
                        StatusLevel.Crit;
}

/// <summary>
/// Thermal classifiers. Tctl/Tdie is the primary CPU junction reading;
/// Ryzen 5000 family has Tj_max = 90°C (thermal throttle), so 85°C is
/// Warn and above that is Crit. Below 75°C is a comfortable daily soak.
/// </summary>
public static class ThermalThresholds
{
    /// <summary>Tctl / Tdie — primary CPU junction temperature.</summary>
    public static StatusLevel CpuTemp(double celsius) =>
        celsius <= 0    ? StatusLevel.None :
        celsius <= 75   ? StatusLevel.Pass :
        celsius <= 85   ? StatusLevel.Warn :
                          StatusLevel.Crit;

    /// <summary>Per-CCD temperature (Zen 2+). Same band as Tctl/Tdie —
    /// a hot CCD is a hot die whether or not the other CCD is cooler.</summary>
    public static StatusLevel CcdTemp(double celsius) =>
        celsius <= 0    ? StatusLevel.None :
        celsius <= 75   ? StatusLevel.Pass :
        celsius <= 85   ? StatusLevel.Warn :
                          StatusLevel.Crit;
}
