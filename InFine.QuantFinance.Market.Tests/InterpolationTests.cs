using System;
using System.Linq;
using InFine.QuantFinance.Numerics;
using Xunit;

namespace InFine.QuantFinance.Market.Tests;

/// <summary>
/// Interpolation kernels. Independent references: exactness at nodes; Bessel slopes reproduce quadratics, so the
/// filtered cubic must reproduce <c>x^2</c> where the filter is inactive (exact Hermite data for a polynomial of degree
/// &lt;= 3); derivatives against central finite differences; monotonicity on the classic test sets of Akima (1970) and
/// Fritsch-Carlson (1980, "RPN 14"), on which unconstrained cubics overshoot.
/// </summary>
public class InterpolationTests
{
    // Akima (1970): flat then steeply increasing, the textbook overshoot case.
    private static readonly double[] AkimaX = [0, 2, 3, 5, 6, 8, 9, 11, 12, 14, 15];
    private static readonly double[] AkimaY = [10, 10, 10, 10, 10, 10, 10.5, 15, 50, 60, 85];

    // Fritsch & Carlson (1980), RPN 14 data: monotone increasing with a very steep part.
    private static readonly double[] RpnX = [7.99, 8.09, 8.19, 8.7, 9.2, 10, 12, 15, 20];
    private static readonly double[] RpnY = [0, 2.76429e-5, 4.37498e-2, 0.169183, 0.469428, 0.943740, 0.998636, 0.999919, 0.999994];

    private static double[] Slopes(double[] x, double[] y)
    {
        var d = new double[x.Length];
        InterpolationKernels.MonotoneCubicSlopes(x, y, x.Length, d);
        return d;
    }

    // Dense samples including the nodes: `perSegment` points on every segment.
    private static double[] Grid(double[] x, int perSegment) =>
        Enumerable.Range(0, x.Length - 1)
            .SelectMany(i => Enumerable.Range(0, perSegment).Select(j => x[i] + (x[i + 1] - x[i]) * j / perSegment))
            .Append(x[^1]).ToArray();

    [Fact]
    public void FindSegmentClampsAndPrefersTheRightSegmentAtNodes()
    {
        double[] x = [0, 1, 2, 4];
        Assert.Equal(0, InterpolationKernels.FindSegment(x, 4, -5));
        Assert.Equal(0, InterpolationKernels.FindSegment(x, 4, 0));
        Assert.Equal(0, InterpolationKernels.FindSegment(x, 4, 0.5));
        Assert.Equal(1, InterpolationKernels.FindSegment(x, 4, 1));
        Assert.Equal(2, InterpolationKernels.FindSegment(x, 4, 2));
        Assert.Equal(2, InterpolationKernels.FindSegment(x, 4, 4));
        Assert.Equal(2, InterpolationKernels.FindSegment(x, 4, 9));
        // Oversized buffers: only the first n entries count.
        Assert.Equal(0, InterpolationKernels.FindSegment([0, 1, 99, 100], 2, 50));
    }

    [Fact]
    public void LinearIsExactAtNodesAndLinearInBetween()
    {
        double[] x = [0.5, 1, 3, 7];
        double[] y = [0.02, 0.025, 0.031, 0.029];
        for (var i = 0; i < x.Length; i++)
            Assert.Equal(y[i], InterpolationKernels.LinearValue(x, y, 4, x[i]));
        Assert.Equal(0.028, InterpolationKernels.LinearValue(x, y, 4, 2.0), 15);
        Assert.Equal(0.003, InterpolationKernels.LinearDerivative(x, y, 4, 2.0), 15);
        Assert.Equal(0.02, InterpolationKernels.LinearValue(x, y, 1, 3.0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CubicIsExactAtNodes(int dataset)
    {
        var (x, y) = dataset switch
        {
            0 => (AkimaX, AkimaY),
            1 => (RpnX, RpnY),
            _ => (new double[] { 0.1, 1, 2, 5, 10, 30 }, new double[] { 0.031, 0.027, 0.024, 0.026, 0.029, 0.025 }),
        };
        var d = Slopes(x, y);
        for (var i = 0; i < x.Length; i++)
            Assert.Equal(y[i], InterpolationKernels.HermiteValue(x, y, d, x.Length, x[i]));
    }

    [Fact]
    public void CubicReproducesAQuadraticWhereTheFilterIsInactive()
    {
        // Bessel slopes of x^2 are exactly 2x, and they satisfy the Hyman bound 2 x_i <= 3 (x_{i-1} + x_i), so the filter
        // leaves them alone; a cubic Hermite with exact slopes reproduces any polynomial of degree <= 3.
        double[] x = [0.5, 1, 1.7, 2.5, 4, 6, 9];
        var y = x.Select(v => v * v).ToArray();
        var d = Slopes(x, y);
        for (var i = 0; i < x.Length; i++)
            Assert.Equal(2 * x[i], d[i], 12);
        foreach (var v in Grid(x, 37))
        {
            Assert.Equal(v * v, InterpolationKernels.HermiteValue(x, y, d, x.Length, v), 12);
            Assert.Equal(2 * v, InterpolationKernels.HermiteDerivative(x, y, d, x.Length, v), 12);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CubicIsMonotoneOnMonotoneData(int dataset)
    {
        var (x, y) = MonotoneDataset(dataset);
        var d = Slopes(x, y);
        var sign = Math.Sign(y[^1] - y[0]);
        var scale = y.Max(Math.Abs);
        var grid = Grid(x, 200);
        var previous = InterpolationKernels.HermiteValue(x, y, d, x.Length, grid[0]);
        foreach (var v in grid.Skip(1))
        {
            var value = InterpolationKernels.HermiteValue(x, y, d, x.Length, v);
            // Monotone up to rounding of the Hermite basis (a few ulps of the data scale).
            Assert.True(sign * (value - previous) >= -1e-14 * scale, $"dataset {dataset}: not monotone at {v} ({previous} -> {value})");
            Assert.True(sign * InterpolationKernels.HermiteDerivative(x, y, d, x.Length, v) >= -1e-12 * scale, $"dataset {dataset}: derivative sign at {v}");
            previous = value;
        }
    }

    [Fact]
    public void UnfilteredBesselSlopesOvershootOnAkimaData()
    {
        // Control for the test above: without the Hyman filter the cubic dips below 10 on [6, 8] (flat data next to a
        // rising one), so the monotonicity test does exercise the filter.
        var d = new double[AkimaX.Length];
        InterpolationKernels.BesselSlopes(AkimaX, AkimaY, AkimaX.Length, d);
        var min = Grid(AkimaX, 200).Min(v => InterpolationKernels.HermiteValue(AkimaX, AkimaY, d, AkimaX.Length, v));
        Assert.True(min < 10 - 1e-3, $"expected an undershoot, min = {min}");
    }

    [Fact]
    public void CubicNeverLeavesTheRangeOfASegmentOnHumpedData()
    {
        // A humped zero curve: Hyman makes every segment monotone, so values stay between the two end values.
        double[] x = [0.02, 0.25, 0.5, 1, 2, 3, 5, 7, 10, 15, 20, 30];
        double[] y = [0.029, 0.0285, 0.027, 0.0245, 0.0228, 0.0226, 0.0231, 0.0238, 0.0249, 0.0256, 0.025, 0.0233];
        var d = Slopes(x, y);
        for (var i = 0; i < x.Length - 1; i++)
        {
            var lo = Math.Min(y[i], y[i + 1]);
            var hi = Math.Max(y[i], y[i + 1]);
            for (var j = 0; j <= 100; j++)
            {
                var v = InterpolationKernels.HermiteValue(x, y, d, x.Length, x[i] + (x[i + 1] - x[i]) * j / 100.0);
                Assert.InRange(v, lo - 1e-16, hi + 1e-16);
            }
        }
    }

    [Fact]
    public void HymanFilterRules()
    {
        double[] x = [0, 1, 2, 3, 4];
        double[] y = [0, 1, 0.5, 0.6, 2];
        // Secants: 1, -0.5, 0.1, 1.4.
        double[] d = [-0.3, 0.7, -0.4, 0.9, 9.0];
        InterpolationKernels.HymanFilter(x, y, 5, d);
        Assert.Equal(0.0, d[0]);            // wrong sign against the end secant 1 -> 0
        Assert.Equal(0.0, d[1]);            // local maximum of the data -> 0
        Assert.Equal(0.0, d[2]);            // local minimum -> 0
        Assert.Equal(0.3, d[3], 15);        // capped at 3 min(0.1, 1.4)
        Assert.Equal(4.2, d[4], 15);        // capped at 3 x end secant 1.4
        double[] ok = [0.5, 0.5];
        InterpolationKernels.HymanFilter([0, 1], [0, 1], 2, ok);
        Assert.Equal(new[] { 0.5, 0.5 }, ok); // admissible slopes are kept
    }

    [Fact]
    public void DerivativesMatchFiniteDifferences()
    {
        double[] x = [0.1, 1, 2, 5, 10, 30];
        double[] y = [0.031, 0.027, 0.024, 0.026, 0.029, 0.025];
        var d = Slopes(x, y);
        const double h = 1e-6;
        foreach (var v in new[] { 0.3, 1.5, 2.2, 4.9, 7.7, 21.0 })
        {
            var fd = (InterpolationKernels.HermiteValue(x, y, d, 6, v + h) - InterpolationKernels.HermiteValue(x, y, d, 6, v - h)) / (2 * h);
            // Central difference error ~ h^2 f''' / 6 plus rounding 1e-17 / h: below 1e-9 here.
            Assert.Equal(fd, InterpolationKernels.HermiteDerivative(x, y, d, 6, v), 9);
        }
    }

    [Fact]
    public void InterpolatorsValidateNodes()
    {
        Assert.Throws<ArgumentException>(() => LinearInterpolator.Instance.Interpolate([], []));
        Assert.Throws<ArgumentException>(() => LinearInterpolator.Instance.Interpolate([1, 1], [0, 0]));
        Assert.Throws<ArgumentException>(() => MonotoneCubicInterpolator.Instance.Interpolate([1, 2], [0]));
        Assert.True(LinearInterpolator.Instance.IsLocal);
        Assert.False(MonotoneCubicInterpolator.Instance.IsLocal);
    }

    private static (double[] X, double[] Y) MonotoneDataset(int index)
    {
        switch (index)
        {
            case 0: return (AkimaX, AkimaY);
            case 1: return (RpnX, RpnY);
            case 2:
                // Decreasing discount factors with an irregular grid.
                double[] t = [0.02, 0.1, 0.25, 0.5, 1, 2, 3, 5, 10, 30];
                return (t, t.Select(v => Math.Exp(-(0.03 - 0.01 * Math.Exp(-v)) * v)).ToArray());
            default:
                // Random non-decreasing data with plateaus (deterministic seed).
                var random = new Random(20250115);
                var xs = new double[25];
                var ys = new double[25];
                for (var i = 1; i < 25; i++)
                {
                    xs[i] = xs[i - 1] + 0.1 + random.NextDouble();
                    ys[i] = ys[i - 1] + (random.NextDouble() < 0.3 ? 0.0 : Math.Pow(random.NextDouble(), 3) * 5);
                }
                return (xs, ys);
        }
    }
}
