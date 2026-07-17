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

    // ---- WispEnv: nebula strand envelope sin(pi*u)^0.75, exact-zero endpoints ----

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void WispEnv_IsExactlyZeroAtAndOutsideEndpoints(double u)
        => Assert.Equal(0.0, ScopeGeometry.WispEnv(u));

    [Fact]
    public void WispEnv_PeaksAtMidpoint()
        => Assert.Equal(1.0, ScopeGeometry.WispEnv(0.5), 12);

    [Fact]
    public void WispEnv_IsSymmetric()
        => Assert.Equal(ScopeGeometry.WispEnv(0.25), ScopeGeometry.WispEnv(0.75), 12);

    [Fact]
    public void WispEnv_QuarterPointMatchesPow()
        => Assert.Equal(0.7711, ScopeGeometry.WispEnv(0.25), 4);

    // ---- WispNoff: three layered sines normalized to [-1, 1] ----

    [Fact]
    public void WispNoff_StaysBounded()
    {
        for (double u = 0; u <= 1.0001; u += 0.03)
            for (double t = 0; t < 60000; t += 1700)
                Assert.InRange(ScopeGeometry.WispNoff(u, t, 3.7, 1.0), -1.0, 1.0);
    }

    [Fact]
    public void WispNoff_IsDeterministic()
        => Assert.Equal(ScopeGeometry.WispNoff(0.4, 1234, 9.4, 1.03),
                        ScopeGeometry.WispNoff(0.4, 1234, 9.4, 1.03));

    [Fact]
    public void WispNoff_SeedChangesStrand()
        => Assert.NotEqual(ScopeGeometry.WispNoff(0.5, 1000, 3.7, 1.0),
                           ScopeGeometry.WispNoff(0.5, 1000, 9.4, 1.0));

    [Fact]
    public void WispNoff_DriftsOverTime()
        => Assert.NotEqual(ScopeGeometry.WispNoff(0.5, 0, 3.7, 1.0),
                           ScopeGeometry.WispNoff(0.5, 50000, 3.7, 1.0));

    // ---- MeanAbs: mean |sample| over the buffer (the sole audio->visual coupling) ----

    [Fact]
    public void MeanAbs_EmptyIsZero()
        => Assert.Equal(0.0, ScopeGeometry.MeanAbs(Array.Empty<double>()));

    [Fact]
    public void MeanAbs_AllZeroIsZero()
        => Assert.Equal(0.0, ScopeGeometry.MeanAbs(new double[8]));

    [Fact]
    public void MeanAbs_AlternatingHalfMagnitudeIsHalf()
        => Assert.Equal(0.5, ScopeGeometry.MeanAbs(new[] { -0.5, 0.5, -0.5, 0.5 }), 12);

    [Fact]
    public void MeanAbs_SingleValueIsItsMagnitude()
        => Assert.Equal(0.8, ScopeGeometry.MeanAbs(new[] { -0.8 }), 12);

    // ---- BuildStrandPoints: smooth wisp strand along the midline, NO PCM spine ----

    [Fact]
    public void BuildStrandPoints_EmptyWhenSegsBelowOne()
        => Assert.Empty(ScopeGeometry.BuildStrandPoints(296, 40, 0, 10, 0, 3.7, 1.0, 1.0));

    [Fact]
    public void BuildStrandPoints_EmptyWhenWindowNonPositive()
        => Assert.Empty(ScopeGeometry.BuildStrandPoints(296, 40, 16, 10, 0, 3.7, 1.0, 1.0, 0.5, 0.5));

    [Fact]
    public void BuildStrandPoints_ReturnsSegsPlusOnePoints()
        => Assert.Equal(17, ScopeGeometry.BuildStrandPoints(296, 40, 16, 10, 0, 3.7, 1.0, 1.0).Length);

    [Fact]
    public void BuildStrandPoints_EndpointXIsExactTrueU()
    {
        var p = ScopeGeometry.BuildStrandPoints(296, 40, 16, 10, 0, 3.7, 1.0, 1.0, 0.25, 0.75);
        Assert.Equal(0.25 * 296, p[0].X, 12);
        Assert.Equal(0.75 * 296, p[16].X, 12);
    }

    [Fact]
    public void BuildStrandPoints_FullWindowEndpointsPinnedToMidline()
    {
        var p = ScopeGeometry.BuildStrandPoints(296, 40, 16, 10, 0, 3.7, 1.0, 1.0);
        Assert.Equal(20.0, p[0].Y, 12);    // WispEnv(0) == 0
        Assert.Equal(20.0, p[16].Y, 12);   // WispEnv(1) == 0
    }

    [Fact]
    public void BuildStrandPoints_OffsetBoundedByAmp()
    {
        var p = ScopeGeometry.BuildStrandPoints(296, 40, 16, 10, 1234, 9.4, 1.03, 1.0);
        Assert.All(p, q => Assert.True(Math.Abs(q.Y - 20.0) <= 10.0 + 1e-9));
    }

    [Fact]
    public void BuildStrandPoints_StaysWithinClipAtMaxAmp()
    {
        // A=13.0 (loud), outer strand factor 1.45 -> amp 18.85; must never clip the 40px canvas.
        var p = ScopeGeometry.BuildStrandPoints(296, 40, 16, 13.0 * 1.45, 777, 32.2, 1.55, 1.0);
        Assert.All(p, q => Assert.True(Math.Abs(q.Y - 20.0) < 19.45));
    }

    [Fact]
    public void BuildStrandPoints_IgnitionGatesPointsBeyondHead()
    {
        var p = ScopeGeometry.BuildStrandPoints(296, 40, 16, 10, 0, 3.7, 1.0, 0.5); // head 0.5
        for (int i = 0; i < p.Length; i++)
        {
            double u = (double)i / 16;
            if (u > 0.5) Assert.Equal(20.0, p[i].Y, 12);   // IgnitionAmp(u>head) == 0
        }
    }
}
