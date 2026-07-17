namespace DeadAir.Core;

/// <summary>
/// Pure math for the Lantern-styled pill oscilloscope
/// (docs/superpowers/specs/2026-07-16-lantern-scope-design.md).
/// Envelope/breathing constants carried verbatim from DeadEye LANTERN-SPEC §5;
/// retract is deliberately 450 ms (not Lantern's 4.8 s) so the pill leaves fast.
/// </summary>
public static class ScopeGeometry
{
    public const double IgnitionMs = 300.0;   // Lantern hopMs
    public const double RetractMs = 450.0;

    /// <summary>Endpoint taper sin(π·u). Exactly 0 at/outside the endpoints —
    /// float sin(π) leaks ~1e-16, which would defeat "pinches to nothing".</summary>
    public static double Envelope(double u)
        => u <= 0 || u >= 1 ? 0 : Math.Sin(Math.PI * u);

    /// <summary>Breathing amplitude envelope, 900 ms period, phase 0 (single trace).</summary>
    public static double Breathe(double tMs)
        => 0.72 + 0.28 * Math.Sin(tMs / 900.0);

    /// <summary>Beam head position u, linear 0→1 over IgnitionMs, clamped.</summary>
    public static double IgnitionHead(double tMs)
        => Math.Clamp(tMs / IgnitionMs, 0.0, 1.0);

    /// <summary>Amplitude during ignition: grows toward the head (u/head),
    /// nothing beyond the head, full field once the head arrives.</summary>
    public static double IgnitionAmp(double u, double head)
    {
        if (head >= 1) return 1;
        if (head <= 0 || u > head) return 0;
        return u / head;
    }

    /// <summary>Visible fraction 1→0 over RetractMs, smoothstep-eased.
    /// Doubles as the alpha fade so length and alpha hit zero together — no pop.</summary>
    public static double RetractFraction(double tMs)
    {
        double p = 1 - Math.Clamp(tMs / RetractMs, 0.0, 1.0);
        return p * p * (3 - 2 * p);
    }
}
