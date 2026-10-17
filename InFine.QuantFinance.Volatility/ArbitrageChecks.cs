using System;
using System.Collections.Generic;

namespace InFine.QuantFinance.Volatility
{
    /// <summary>
    /// Static-arbitrage diagnostics of total-variance slices on a log-moneyness grid (Gatheral and Jacquier,
    /// "Arbitrage-free SVI volatility surfaces", Quantitative Finance 14(1), 2014, section 2).
    /// </summary>
    /// <remarks>
    /// A grid check is a diagnostic, not a proof: it finds violations wider than the grid spacing. Sufficient
    /// conditions that hold for every <c>k</c> are on <see cref="SsviParameters"/> for SSVI.
    /// </remarks>
    public static class ArbitrageChecks
    {
        /// <summary>
        /// Gatheral-Jacquier's function (2.1),
        /// <c>g(k) = (1 - k w'/(2w))^2 - (w'^2/4)(1/w + 1/4) + w''/2</c>. The slice has no butterfly arbitrage iff
        /// <c>g &gt;= 0</c> everywhere (and call prices vanish at infinite strike, Lemma 2.2). NaN where <c>w &lt;= 0</c>.
        /// </summary>
        public static double ButterflyDensity<TSlice>(TSlice slice, double k) where TSlice : ITotalVarianceSlice
        {
            double w = slice.TotalVariance(k);
            if (!(w > 0.0)) return double.NaN;
            double w1 = slice.TotalVarianceDerivative(k);
            double w2 = slice.TotalVarianceSecondDerivative(k);
            double u = 1.0 - k * w1 / (2.0 * w);
            return u * u - 0.25 * w1 * w1 * (1.0 / w + 0.25) + 0.5 * w2;
        }

        /// <summary>
        /// Risk-neutral density of the log-moneyness <c>ln(S_T / F)</c> implied by the slice,
        /// <c>p(k) = g(k) / sqrt(2 pi w(k)) exp(-d_-(k)^2 / 2)</c>, <c>d_- = -k / sqrt(w) - sqrt(w) / 2</c> (proof of
        /// Gatheral-Jacquier Lemma 2.2). The density in strike is <c>d^2 C / dK^2 = p(k) / K</c> at <c>K = F e^k</c>
        /// (<c>C</c> the undiscounted call); the paper writes the formula as <c>d^2 C / dK^2</c>, which holds for that
        /// density of <c>k</c>, i.e. up to the Jacobian <c>K</c>.
        /// </summary>
        public static double RiskNeutralDensity<TSlice>(TSlice slice, double k) where TSlice : ITotalVarianceSlice
        {
            double g = ButterflyDensity(slice, k);
            if (double.IsNaN(g)) return double.NaN;
            double w = slice.TotalVariance(k);
            double sqrtW = Math.Sqrt(w);
            double dMinus = -k / sqrtW - 0.5 * sqrtW;
            return g / Math.Sqrt(2.0 * Math.PI * w) * Math.Exp(-0.5 * dMinus * dMinus);
        }

        /// <summary>
        /// Evaluates <see cref="ButterflyDensity{TSlice}"/> on <paramref name="grid"/> and reports the points where it
        /// is below <c>-tolerance</c> (or undefined because the total variance is not positive).
        /// </summary>
        public static ButterflyCheckResult CheckButterfly<TSlice>(TSlice slice, IReadOnlyList<double> grid, double tolerance = 0.0)
            where TSlice : ITotalVarianceSlice
        {
            ValidateGrid(grid, tolerance);
            var violations = new List<double>();
            double min = double.PositiveInfinity, argMin = double.NaN;
            for (int i = 0; i < grid.Count; i++)
            {
                double k = grid[i];
                double g = ButterflyDensity(slice, k);
                if (double.IsNaN(g) || g < -tolerance) violations.Add(k);
                if (double.IsNaN(g)) { min = double.NaN; argMin = k; }
                else if (g < min) { min = g; argMin = k; }
            }
            return new ButterflyCheckResult(min, argMin, violations);
        }

        /// <summary>
        /// Calendar-spread check between two expiries (Gatheral-Jacquier Lemma 2.1, proportional dividends): at fixed
        /// log-forward moneyness the total variance must not decrease, <c>w_later(k) &gt;= w_earlier(k)</c>. Reports the
        /// grid points where <c>w_later - w_earlier &lt; -tolerance</c>.
        /// </summary>
        public static CalendarCheckResult CheckCalendar<TEarlier, TLater>(TEarlier earlier, TLater later,
            IReadOnlyList<double> grid, double tolerance = 0.0)
            where TEarlier : ITotalVarianceSlice
            where TLater : ITotalVarianceSlice
        {
            ValidateGrid(grid, tolerance);
            var violations = new List<double>();
            double min = double.PositiveInfinity, argMin = double.NaN;
            for (int i = 0; i < grid.Count; i++)
            {
                double k = grid[i];
                double d = later.TotalVariance(k) - earlier.TotalVariance(k);
                if (!(d >= -tolerance)) violations.Add(k);
                if (double.IsNaN(d)) { min = double.NaN; argMin = k; }
                else if (d < min) { min = d; argMin = k; }
            }
            return new CalendarCheckResult(min, argMin, violations);
        }

        /// <summary><paramref name="count"/> equally spaced log-moneyness points from <paramref name="kMin"/> to <paramref name="kMax"/>.</summary>
        public static double[] Grid(double kMin, double kMax, int count)
        {
            if (!double.IsFinite(kMin) || !double.IsFinite(kMax) || !(kMax > kMin))
                throw new ArgumentException("The grid needs finite bounds with kMax > kMin.");
            if (count < 2) throw new ArgumentOutOfRangeException(nameof(count), count, "A grid needs at least two points.");
            var grid = new double[count];
            for (int i = 0; i < count; i++) grid[i] = kMin + (kMax - kMin) * i / (count - 1);
            grid[count - 1] = kMax;
            return grid;
        }

        static void ValidateGrid(IReadOnlyList<double> grid, double tolerance)
        {
            ArgumentNullException.ThrowIfNull(grid);
            if (grid.Count == 0) throw new ArgumentException("The grid is empty.", nameof(grid));
            if (!(tolerance >= 0.0)) throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "The tolerance must be non-negative.");
        }
    }

    /// <summary>Outcome of <see cref="ArbitrageChecks.CheckButterfly{TSlice}"/>.</summary>
    public sealed class ButterflyCheckResult
    {
        internal ButterflyCheckResult(double minimum, double argMinimum, IReadOnlyList<double> violations)
        {
            MinimumDensity = minimum; ArgMinimum = argMinimum; Violations = violations;
        }

        /// <summary>True when no grid point violates the condition.</summary>
        public bool IsArbitrageFree => Violations.Count == 0;

        /// <summary>Smallest <c>g(k)</c> on the grid (NaN if the total variance is not positive somewhere).</summary>
        public double MinimumDensity { get; }

        /// <summary>Grid point of <see cref="MinimumDensity"/>.</summary>
        public double ArgMinimum { get; }

        /// <summary>Grid points where <c>g(k) &lt; -tolerance</c>, in grid order.</summary>
        public IReadOnlyList<double> Violations { get; }
    }

    /// <summary>Outcome of <see cref="ArbitrageChecks.CheckCalendar{TEarlier, TLater}"/>.</summary>
    public sealed class CalendarCheckResult
    {
        internal CalendarCheckResult(double minimum, double argMinimum, IReadOnlyList<double> violations)
        {
            MinimumDifference = minimum; ArgMinimum = argMinimum; Violations = violations;
        }

        /// <summary>True when no grid point violates the condition.</summary>
        public bool IsArbitrageFree => Violations.Count == 0;

        /// <summary>Smallest <c>w_later(k) - w_earlier(k)</c> on the grid.</summary>
        public double MinimumDifference { get; }

        /// <summary>Grid point of <see cref="MinimumDifference"/>.</summary>
        public double ArgMinimum { get; }

        /// <summary>Grid points where the later slice is below the earlier one by more than the tolerance.</summary>
        public IReadOnlyList<double> Violations { get; }
    }
}
