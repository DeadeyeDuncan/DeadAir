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

    /// <summary>
    /// Map samples to canvas points. x is FIXED per index (true-u rule: ignition
    /// and retract unveil/withdraw the wave, never compress it); y = mid −
    /// v·mid·ampAt(u). Points with u &lt; visibleFrom or u &gt; visibleTo are
    /// omitted. Fewer than two samples → empty.
    /// </summary>
    public static (double X, double Y)[] BuildPoints(
        IReadOnlyList<double> samples, double width, double height,
        Func<double, double> ampAt, double visibleFrom = 0.0, double visibleTo = 1.0)
    {
        int n = samples.Count;
        if (n < 2) return Array.Empty<(double, double)>();
        double mid = height / 2.0, step = width / (n - 1);
        var pts = new List<(double, double)>(n);
        for (int i = 0; i < n; i++)
        {
            double u = (double)i / (n - 1);
            if (u < visibleFrom || u > visibleTo) continue;
            pts.Add((i * step, mid - samples[i] * mid * ampAt(u)));
        }
        return pts.ToArray();
    }

    /// <summary>Nebula strand noise (DeadEye wispNoff verbatim): three layered
    /// sines normalized to [-1, 1]. The caller pre-slows t (tamed drift = t·0.33).</summary>
    public static double WispNoff(double u, double t, double seed, double k)
        => (Math.Sin(u * 5.1 + seed * 3.7 + t * 0.00021 * k)
          + Math.Sin(u * 11.7 + seed * 9.1 - t * 0.00013 * k) * 0.55
          + Math.Sin(u * 23.3 + seed * 17.3 + t * 0.00034 * k) * 0.28) / 1.83;

    /// <summary>Nebula strand envelope sin(π·u)^0.75 (DeadEye wispEnv verbatim,
    /// including the exact-zero endpoint clamp — float sin(π)^0.75 leaks ~1e-12,
    /// which would defeat "strands pinch to nothing").</summary>
    public static double WispEnv(double u)
        => u <= 0 || u >= 1 ? 0 : Math.Pow(Math.Sin(Math.PI * u), 0.75);

    /// <summary>Mean |sample| over the buffer — the sole audio→visual coupling for
    /// the nebula skin. On the min/max peak-envelope buffer this is peak magnitude;
    /// ~0 at silence, ~0.3–0.45 for loud speech (samples in [-1,1]).</summary>
    public static double MeanAbs(IReadOnlyList<double> samples)
    {
        int n = samples.Count;
        if (n == 0) return 0;
        double a = 0;
        for (int i = 0; i < n; i++) a += Math.Abs(samples[i]);
        return a / n;
    }

    /// <summary>One smooth nebula strand: a straight spine at y=height/2 with a
    /// WispNoff perpendicular offset (NO PCM waveform), faithfully ported from
    /// DeadEye's wispLitStrand. Sampled at `segs` segments (segs+1 points) across
    /// the true-u window [visibleFrom, visibleTo] — low sampling is what keeps the
    /// curve smooth. WispEnv pinches the ends; IgnitionAmp gates the ignition sweep.</summary>
    public static (double X, double Y)[] BuildStrandPoints(
        double width, double height, int segs, double amp,
        double tSlow, double seed, double k, double head,
        double visibleFrom = 0.0, double visibleTo = 1.0)
    {
        if (segs < 1 || visibleTo <= visibleFrom) return Array.Empty<(double, double)>();
        double mid = height / 2.0, span = visibleTo - visibleFrom;
        var pts = new (double, double)[segs + 1];
        for (int i = 0; i <= segs; i++)
        {
            double u = visibleFrom + span * ((double)i / segs);
            double off = WispNoff(u, tSlow, seed, k) * amp * WispEnv(u) * IgnitionAmp(u, head);
            pts[i] = (u * width, mid + off);
        }
        return pts;
    }
}
