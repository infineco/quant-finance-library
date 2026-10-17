using System;
using InFine.QuantFinance.Numerics;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// Compiles host market objects into the flat data the simulation kernels read. Together with
    /// <see cref="SimulationGridBuilder"/> (dates to pillar times) this is the seam between the idiomatic market model and
    /// the kernels, which only see arrays of doubles on the simulation clock.
    /// </summary>
    public static class MarketCompiler
    {
        /// <summary>
        /// Compiles <paramref name="curve"/> for a simulation on <paramref name="grid"/>.
        /// <list type="bullet">
        /// <item>An <see cref="InterpolatedDiscountCurve"/> interpolated linearly or with the monotone cubic is copied node
        /// for node: the compiled curve evaluates through the same <see cref="InterpolationKernels"/> on the same arrays, so it
        /// equals the host curve bit for bit at any time, between pillars and beyond the last one included.</item>
        /// <item>Any other curve is sampled at the grid pillars and interpolated log-linearly on discount factors in between,
        /// with the curve's instantaneous forward at the last pillar beyond it. Discount factors are then exact at every
        /// pillar (and everywhere for a flat-forward curve), and approximate between pillars.</item>
        /// </list>
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The curve's reference date is not the valuation date of the grid, or the curve measures time with another day
        /// counter than the grid: a kernel has a single clock, so both must agree.
        /// </exception>
        public static CompiledDiscountCurve CompileDiscountCurve(IYieldCurve curve, SimulationGrid grid)
        {
            ArgumentNullException.ThrowIfNull(curve);
            ArgumentNullException.ThrowIfNull(grid);
            if (curve.ReferenceDate != grid.ValuationDate)
                throw new ArgumentException(
                    $"The curve is seen from {curve.ReferenceDate:yyyy-MM-dd} but the simulation starts on {grid.ValuationDate:yyyy-MM-dd}.", nameof(curve));
            if (!ReferenceEquals(curve.DayCounter, grid.DayCounter) && curve.DayCounter.Name != grid.DayCounter.Name)
                throw new ArgumentException(
                    $"The curve measures time with {curve.DayCounter.Name} but the simulation with {grid.DayCounter.Name}.", nameof(curve));

            if (curve is InterpolatedDiscountCurve interpolated && CompileNodes(interpolated) is { } exact)
                return exact;
            return Sample(curve, grid);
        }

        private static CompiledDiscountCurve? CompileNodes(InterpolatedDiscountCurve curve)
        {
            bool hermite;
            if (ReferenceEquals(curve.Interpolation.Interpolator, LinearInterpolator.Instance)) hermite = false;
            else if (ReferenceEquals(curve.Interpolation.Interpolator, MonotoneCubicInterpolator.Instance)) hermite = true;
            else return null;

            var times = curve.PillarTimes;
            var discounts = curve.DiscountFactors;
            var n = times.Count;
            double[] x, y;
            int mode;
            // Same node arrays as InterpolatedDiscountCurve builds for its interpolant.
            if (curve.Interpolation.Quantity == InterpolatedQuantity.ZeroRate)
            {
                x = new double[n];
                y = new double[n];
                for (var i = 0; i < n; i++)
                {
                    x[i] = times[i];
                    y[i] = -Math.Log(discounts[i]) / times[i];
                }
                mode = hermite ? DiscountCurveKernels.HermiteZeroRate : DiscountCurveKernels.LinearZeroRate;
            }
            else
            {
                x = new double[n + 1];
                y = new double[n + 1];
                for (var i = 0; i < n; i++)
                {
                    x[i + 1] = times[i];
                    y[i + 1] = Math.Log(discounts[i]);
                }
                mode = hermite ? DiscountCurveKernels.HermiteLogDiscount : DiscountCurveKernels.LinearLogDiscount;
            }
            var slopes = Array.Empty<double>();
            if (hermite)
            {
                slopes = new double[x.Length];
                InterpolationKernels.MonotoneCubicSlopes(x, y, x.Length, slopes);
            }
            var last = times[n - 1];
            return new CompiledDiscountCurve(mode, x, y, slopes, Math.Log(discounts[n - 1]), curve.InstantaneousForward(last));
        }

        private static CompiledDiscountCurve Sample(IYieldCurve curve, SimulationGrid grid)
        {
            var times = grid.Times;
            var y = new double[times.Count];
            for (var k = 1; k < y.Length; k++)
                y[k] = Math.Log(curve.DiscountFactor(times[k]));
            var last = times[^1];
            return new CompiledDiscountCurve(DiscountCurveKernels.LinearLogDiscount, grid.ToTimesArray(), y, ReadOnlySpan<double>.Empty,
                y[^1], curve.InstantaneousForward(last));
        }
    }
}
