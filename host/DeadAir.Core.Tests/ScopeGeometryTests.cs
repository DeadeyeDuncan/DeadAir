using DeadAir.Core;

namespace DeadAir.Core.Tests;

public class ScopeGeometryTests
{
    // ---- Envelope: taper sin(pi*u), exactly zero at/outside both endpoints ----

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Envelope_IsExactlyZeroAtAndOutsideEndpoints(double u)
        => Assert.Equal(0.0, ScopeGeometry.Envelope(u));

    [Fact]
    public void Envelope_PeaksAtMidpoint()
        => Assert.Equal(1.0, ScopeGeometry.Envelope(0.5), 12);

    [Fact]
    public void Envelope_IsSymmetric()
        => Assert.Equal(ScopeGeometry.Envelope(0.25), ScopeGeometry.Envelope(0.75), 12);

    [Fact]
    public void Envelope_QuarterPointMatchesSinPiOver4()
        => Assert.Equal(0.70711, ScopeGeometry.Envelope(0.25), 4);

    // ---- Breathe: 0.72 + 0.28*sin(t/900), bounded [0.44, 1.0] ----

    [Fact]
    public void Breathe_AtZeroIsBaseline()
        => Assert.Equal(0.72, ScopeGeometry.Breathe(0), 12);

    [Fact]
    public void Breathe_PeaksAtQuarterPeriod()
        => Assert.Equal(1.0, ScopeGeometry.Breathe(900.0 * Math.PI / 2), 12);

    [Fact]
    public void Breathe_TroughsAtThreeQuarterPeriod()
        => Assert.Equal(0.44, ScopeGeometry.Breathe(900.0 * 3 * Math.PI / 2), 12);

    [Fact]
    public void Breathe_StaysBounded()
    {
        for (double t = 0; t < 20000; t += 37)
        {
            double b = ScopeGeometry.Breathe(t);
            Assert.InRange(b, 0.44, 1.0);
        }
    }

    // ---- IgnitionHead: linear 0->1 over IgnitionMs (300), clamped ----

    [Theory]
    [InlineData(-50, 0.0)]
    [InlineData(0, 0.0)]
    [InlineData(150, 0.5)]
    [InlineData(300, 1.0)]
    [InlineData(400, 1.0)]
    public void IgnitionHead_LinearAndClamped(double tMs, double expected)
        => Assert.Equal(expected, ScopeGeometry.IgnitionHead(tMs), 12);

    // ---- IgnitionAmp: grows toward the head (u/head), 0 beyond, 1 after arrival ----

    [Fact]
    public void IgnitionAmp_GrowsTowardHead()
        => Assert.Equal(0.5, ScopeGeometry.IgnitionAmp(0.25, 0.5), 12);

    [Fact]
    public void IgnitionAmp_ZeroBeyondHead()
        => Assert.Equal(0.0, ScopeGeometry.IgnitionAmp(0.6, 0.5));

    [Fact]
    public void IgnitionAmp_ZeroWhenHeadNotStarted()
        => Assert.Equal(0.0, ScopeGeometry.IgnitionAmp(0.3, 0.0));

    [Fact]
    public void IgnitionAmp_FullAfterArrival()
        => Assert.Equal(1.0, ScopeGeometry.IgnitionAmp(0.3, 1.0));

    [Fact]
    public void IgnitionAmp_MonotonicTowardHead()
    {
        double prev = -1;
        for (double u = 0; u <= 0.5; u += 0.05)
        {
            double a = ScopeGeometry.IgnitionAmp(u, 0.5);
            Assert.True(a >= prev, $"amp fell at u={u}");
            prev = a;
        }
    }

    // ---- RetractFraction: smoothstep-eased 1->0 over RetractMs (450) ----

    [Theory]
    [InlineData(-10, 1.0)]
    [InlineData(0, 1.0)]
    [InlineData(225, 0.5)]      // smoothstep(0.5) == 0.5
    [InlineData(112.5, 0.84375)] // smoothstep(0.75) = 0.75^2*(3-1.5)
    [InlineData(450, 0.0)]
    [InlineData(500, 0.0)]      // exactly zero at/after completion — no pop
    public void RetractFraction_EasedAndClamped(double tMs, double expected)
        => Assert.Equal(expected, ScopeGeometry.RetractFraction(tMs), 12);

    [Fact]
    public void RetractFraction_MonotonicDecreasing()
    {
        double prev = 2;
        for (double t = 0; t <= 460; t += 10)
        {
            double f = ScopeGeometry.RetractFraction(t);
            Assert.True(f <= prev, $"fraction rose at t={t}");
            prev = f;
        }
    }

    // ---- BuildPoints: fixed-x mapping with a visibility window ----

    private static readonly double[] Bump = { 0.0, 0.0, 1.0, 0.0, 0.0 };

    [Fact]
    public void BuildPoints_MapsSamplesToCanvas()
    {
        var pts = ScopeGeometry.BuildPoints(Bump, 100, 40, _ => 1.0);
        Assert.Equal(5, pts.Length);
        Assert.Equal(new[] { 0.0, 25.0, 50.0, 75.0, 100.0 },
            pts.Select(p => p.X).ToArray());
        Assert.Equal(20.0, pts[0].Y, 12);   // v=0 -> midline
        Assert.Equal(0.0, pts[2].Y, 12);    // v=1, amp 1 -> top
    }

    [Fact]
    public void BuildPoints_AmpScalesDeflectionNotMidline()
    {
        var pts = ScopeGeometry.BuildPoints(Bump, 100, 40, _ => 0.5);
        Assert.Equal(20.0, pts[0].Y, 12);   // v=0 stays on the midline
        Assert.Equal(10.0, pts[2].Y, 12);   // v=1 halved toward midline
    }

    [Fact]
    public void BuildPoints_AmpAtReceivesU()
    {
        var seen = new List<double>();
        ScopeGeometry.BuildPoints(Bump, 100, 40, u => { seen.Add(u); return 1.0; });
        Assert.Equal(new[] { 0.0, 0.25, 0.5, 0.75, 1.0 }, seen.ToArray());
    }

    [Fact]
    public void BuildPoints_VisibleToOmitsPointsBeyondHead()
    {
        var pts = ScopeGeometry.BuildPoints(Bump, 100, 40, _ => 1.0, visibleTo: 0.5);
        Assert.Equal(new[] { 0.0, 25.0, 50.0 }, pts.Select(p => p.X).ToArray());
    }

    [Fact]
    public void BuildPoints_VisibleFromKeepsFixedXPositions()
    {
        // Retract: left edge slides right; surviving xs are NOT remapped to 0.
        var pts = ScopeGeometry.BuildPoints(Bump, 100, 40, _ => 1.0, visibleFrom: 0.5);
        Assert.Equal(new[] { 50.0, 75.0, 100.0 }, pts.Select(p => p.X).ToArray());
    }

    [Fact]
    public void BuildPoints_FewerThanTwoSamplesIsEmpty()
    {
        Assert.Empty(ScopeGeometry.BuildPoints(Array.Empty<double>(), 100, 40, _ => 1.0));
        Assert.Empty(ScopeGeometry.BuildPoints(new[] { 0.7 }, 100, 40, _ => 1.0));
    }
}
