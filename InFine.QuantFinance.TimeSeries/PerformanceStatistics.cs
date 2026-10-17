using System;
using InFine.QuantFinance.Risk;

namespace InFine.QuantFinance.TimeSeries
{
    /// <summary>Divisor and bias correction used by the variance, skewness and kurtosis estimators.</summary>
    public enum MomentConvention
    {
        /// <summary>Plain sample moments about the mean, divisor <c>n</c>: <c>m2 = sum d^2 / n</c>, skewness <c>g1 = m3 / m2^1.5</c>, excess kurtosis <c>g2 = m4 / m2^2 - 3</c>.</summary>
        Population,

        /// <summary>
        /// The small-sample corrected estimators of Excel (<c>STDEV.S</c>, <c>SKEW</c>, <c>KURT</c>) and pandas: variance
        /// with divisor <c>n - 1</c>; Fisher-Pearson adjusted skewness <c>G1 = g1 sqrt(n (n - 1)) / (n - 2)</c>; adjusted
        /// excess kurtosis <c>G2 = (n - 1) / ((n - 2) (n - 3)) ((n + 1) g2 + 6)</c>. Needs <c>n &gt;= 2</c>, <c>3</c> and <c>4</c> respectively.
        /// </summary>
        SampleAdjusted,
    }

    /// <summary>
    /// Where the largest peak-to-trough fall of a level series (wealth index or price) happens. Indices are positions in
    /// the level series: for <see cref="PerformanceStatistics.MaxDrawdown(double[])"/> position 0 is the start (wealth 1)
    /// and position <c>k</c> is the wealth after the first <c>k</c> returns.
    /// </summary>
    /// <param name="Depth">Largest fall <c>1 - level / runningPeak</c> as a non-negative fraction (0.25 is a 25% fall).</param>
    /// <param name="PeakIndex">Position of the peak preceding the trough (first one if tied).</param>
    /// <param name="TroughIndex">Position of the lowest point of the deepest drawdown (first one if tied).</param>
    /// <param name="RecoveryIndex">First position after the trough where the level is back at or above the peak, or -1 if it never recovers.</param>
    public readonly record struct DrawdownInfo(double Depth, int PeakIndex, int TroughIndex, int RecoveryIndex)
    {
        /// <summary>Number of periods from peak to trough.</summary>
        public int PeakToTroughLength => TroughIndex - PeakIndex;

        /// <summary>Number of periods from trough to recovery, or -1 if the level never recovers.</summary>
        public int RecoveryLength => RecoveryIndex < 0 ? -1 : RecoveryIndex - TroughIndex;
    }

    /// <summary>The headline statistics of a return series; see <see cref="PerformanceStatistics.Summarise"/>.</summary>
    public sealed record PerformanceSummary(
        int Count, double TotalReturn, double AnnualisedReturn, double AnnualisedMean, double AnnualisedVolatility,
        double Skewness, double ExcessKurtosis, double SharpeRatio, double SortinoRatio, DrawdownInfo MaxDrawdown,
        double CalmarRatio, double HitRatio);

    /// <summary>
    /// Performance statistics of a series of simple periodic returns <c>r_t = P_t / P_{t-1} - 1</c>, for example from
    /// <see cref="Returns.Simple(double[])"/>. Every annualised quantity takes the number of periods per year explicitly
    /// (see <see cref="Periodicity"/>).
    /// </summary>
    /// <remarks>
    /// <para>Definitions (<c>n</c> returns, <c>k</c> = periods per year):</para>
    /// <list type="bullet">
    /// <item>Annualised return (geometric, CAGR): <c>prod(1 + r_t)^(k / n) - 1</c>; -1 if the wealth is wiped out.</item>
    /// <item>Annualised volatility: <c>sd(r) sqrt(k)</c>, square-root-of-time scaling (exact for i.i.d. returns only).</item>
    /// <item>Sharpe: <c>mean(r - rf / k) / sd(r) sqrt(k)</c>. The annual risk-free rate is converted to a period rate by
    /// <c>rf / k</c>; the mean excess return is arithmetic.</item>
    /// <item>Sortino: <c>(mean(r) - mar / k) / DD sqrt(k)</c> with the target downside deviation
    /// <c>DD = sqrt( (1/n) sum min(r_t - mar / k, 0)^2 )</c> (Sortino and Price, 1994): the average runs over all <c>n</c>
    /// observations, not only the shortfalls, and uses a zero-based target semi-deviation rather than a deviation from
    /// the mean of the shortfalls.</item>
    /// <item>Drawdown at position <c>t</c> of the wealth index <c>W</c> (with <c>W_0 = 1</c>): <c>1 - W_t / max_{s &lt;= t} W_s</c>, a non-negative fraction.</item>
    /// <item>Calmar: annualised return over maximum drawdown depth.</item>
    /// <item>Hit ratio: the share of returns strictly above a threshold (0 by default).</item>
    /// </list>
    /// <para>
    /// Ratios with a zero denominator follow IEEE arithmetic (+/-infinity or NaN) rather than throwing. Moments use the
    /// <see cref="MomentConvention"/> argument; the default is <see cref="MomentConvention.SampleAdjusted"/>, the one
    /// spreadsheets and pandas report.
    /// </para>
    /// </remarks>
    public static class PerformanceStatistics
    {
        // ---- moments -------------------------------------------------------------------------------------------

        /// <summary>Arithmetic mean per period.</summary>
        public static double Mean(double[] returns)
        {
            Require(returns, 1, nameof(returns));
            return MeanOf(returns, 0, returns.Length);
        }

        /// <summary>Arithmetic mean scaled to a year: <c>mean * periodsPerYear</c>.</summary>
        public static double AnnualisedMean(double[] returns, double periodsPerYear) =>
            Mean(returns) * PeriodsPerYear(periodsPerYear);

        /// <summary>Variance per period.</summary>
        public static double Variance(double[] returns, MomentConvention convention = MomentConvention.SampleAdjusted)
        {
            Require(returns, convention == MomentConvention.Population ? 1 : 2, nameof(returns));
            return VarianceOf(returns, 0, returns.Length, convention);
        }

        /// <summary>Standard deviation per period (not annualised).</summary>
        public static double StandardDeviation(double[] returns, MomentConvention convention = MomentConvention.SampleAdjusted) =>
            Math.Sqrt(Variance(returns, convention));

        /// <summary>Annualised volatility <c>sd * sqrt(periodsPerYear)</c>.</summary>
        public static double Volatility(double[] returns, double periodsPerYear, MomentConvention convention = MomentConvention.SampleAdjusted) =>
            StandardDeviation(returns, convention) * Math.Sqrt(PeriodsPerYear(periodsPerYear));

        /// <summary>Skewness; zero-variance series give NaN. See <see cref="MomentConvention"/> for the two estimators.</summary>
        public static double Skewness(double[] returns, MomentConvention convention = MomentConvention.SampleAdjusted)
        {
            Require(returns, convention == MomentConvention.Population ? 1 : 3, nameof(returns));
            int n = returns.Length;
            double mean = MeanOf(returns, 0, n), m2 = 0.0, m3 = 0.0;
            foreach (double r in returns)
            {
                double d = r - mean;
                m2 += d * d;
                m3 += d * d * d;
            }
            m2 /= n; m3 /= n;
            if (!(m2 > 0.0) || IsConstant(returns)) return double.NaN;
            double g1 = m3 / Math.Pow(m2, 1.5);
            return convention == MomentConvention.Population ? g1 : g1 * Math.Sqrt((double)n * (n - 1)) / (n - 2);
        }

        /// <summary>Excess kurtosis (normal = 0); zero-variance series give NaN. See <see cref="MomentConvention"/> for the two estimators.</summary>
        public static double ExcessKurtosis(double[] returns, MomentConvention convention = MomentConvention.SampleAdjusted)
        {
            Require(returns, convention == MomentConvention.Population ? 1 : 4, nameof(returns));
            int n = returns.Length;
            double mean = MeanOf(returns, 0, n), m2 = 0.0, m4 = 0.0;
            foreach (double r in returns)
            {
                double d2 = (r - mean) * (r - mean);
                m2 += d2;
                m4 += d2 * d2;
            }
            m2 /= n; m4 /= n;
            if (!(m2 > 0.0) || IsConstant(returns)) return double.NaN;
            double g2 = m4 / (m2 * m2) - 3.0;
            return convention == MomentConvention.Population
                ? g2
                : (double)(n - 1) / ((double)(n - 2) * (n - 3)) * ((n + 1) * g2 + 6.0);
        }

        // ---- return, ratios ------------------------------------------------------------------------------------

        /// <summary>Compounded return over the whole series, <c>prod(1 + r_t) - 1</c>.</summary>
        public static double TotalReturn(double[] returns)
        {
            Require(returns, 1, nameof(returns));
            double wealth = 1.0;
            foreach (double r in returns)
            {
                if (r <= -1.0) return -1.0;
                wealth *= 1.0 + r;
            }
            return wealth - 1.0;
        }

        /// <summary>Geometric annualised return <c>prod(1 + r)^(periodsPerYear / n) - 1</c>; -1 when the wealth is wiped out.</summary>
        public static double AnnualisedReturn(double[] returns, double periodsPerYear)
        {
            Require(returns, 1, nameof(returns));
            double k = PeriodsPerYear(periodsPerYear);
            double logWealth = 0.0;
            foreach (double r in returns)
            {
                if (r <= -1.0) return -1.0;
                logWealth += Math.Log(1.0 + r);
            }
            return Math.Exp(logWealth * k / returns.Length) - 1.0;
        }

        /// <summary>
        /// Annualised Sharpe ratio <c>mean(r - rf / k) / sd(r) sqrt(k)</c>.
        /// </summary>
        /// <param name="returns">Simple returns per period.</param>
        /// <param name="periodsPerYear">Periods per year <c>k</c>.</param>
        /// <param name="riskFreeRate">Annual risk-free rate, converted to a period rate by division by <c>k</c>.</param>
        /// <param name="convention">Divisor of the standard deviation.</param>
        public static double SharpeRatio(double[] returns, double periodsPerYear, double riskFreeRate = 0.0,
            MomentConvention convention = MomentConvention.SampleAdjusted)
        {
            double k = PeriodsPerYear(periodsPerYear);
            return SharpeOf(returns, 0, returns.Length, k, riskFreeRate, convention, nameof(returns));
        }

        /// <summary>
        /// Target downside deviation per period: <c>sqrt( (1/n) sum min(r_t - m, 0)^2 )</c> with <c>m</c> the minimum
        /// acceptable return <b>per period</b>.
        /// </summary>
        public static double DownsideDeviation(double[] returns, double minimumAcceptableReturnPerPeriod = 0.0)
        {
            Require(returns, 1, nameof(returns));
            return DownsideOf(returns, minimumAcceptableReturnPerPeriod);
        }

        /// <summary>
        /// Annualised Sortino ratio <c>(mean(r) - mar / k) / DD sqrt(k)</c>, <c>DD</c> being the target downside deviation
        /// of <see cref="DownsideDeviation"/>. No shortfall at all gives +infinity (or NaN if the mean equals the target).
        /// </summary>
        /// <param name="returns">Simple returns per period.</param>
        /// <param name="periodsPerYear">Periods per year <c>k</c>.</param>
        /// <param name="minimumAcceptableReturn">Annual minimum acceptable return (MAR), converted to a period target by division by <c>k</c>.</param>
        public static double SortinoRatio(double[] returns, double periodsPerYear, double minimumAcceptableReturn = 0.0)
        {
            Require(returns, 1, nameof(returns));
            double k = PeriodsPerYear(periodsPerYear);
            double mar = minimumAcceptableReturn / k;
            double dd = DownsideOf(returns, mar);
            return (MeanOf(returns, 0, returns.Length) - mar) / dd * Math.Sqrt(k);
        }

        /// <summary>Share of returns strictly above <paramref name="threshold"/> (the hit ratio, or win rate).</summary>
        public static double HitRatio(double[] returns, double threshold = 0.0)
        {
            Require(returns, 1, nameof(returns));
            int hits = 0;
            foreach (double r in returns) if (r > threshold) hits++;
            return (double)hits / returns.Length;
        }

        // ---- drawdowns -----------------------------------------------------------------------------------------

        /// <summary>
        /// Drawdown series of the wealth index of <paramref name="returns"/>. The result has <c>n + 1</c> entries aligned
        /// with the wealth positions (entry 0 is the start and is 0): <c>1 - W_t / max_{s &lt;= t} W_s</c>, non-negative.
        /// </summary>
        public static double[] Drawdowns(double[] returns) => DrawdownsOfLevels(WealthIndex(returns));

        /// <summary>Drawdown series of a level series (prices or a wealth index): <c>1 - L_t / max_{s &lt;= t} L_s</c>. Levels must be finite and positive.</summary>
        public static double[] DrawdownsOfLevels(double[] levels)
        {
            RequireLevels(levels);
            var result = new double[levels.Length];
            double peak = levels[0];
            for (int i = 0; i < levels.Length; i++)
            {
                if (levels[i] > peak) peak = levels[i];
                result[i] = 1.0 - levels[i] / peak;
            }
            return result;
        }

        /// <summary>Maximum drawdown of the wealth index of <paramref name="returns"/>, with peak, trough and recovery positions.</summary>
        public static DrawdownInfo MaxDrawdown(double[] returns) => MaxDrawdownOfLevels(WealthIndex(returns));

        /// <summary>
        /// Maximum drawdown of a level series (typically prices), with the positions of the peak, the trough and the first
        /// recovery. With no fall at all the depth is 0 and all three indices are 0.
        /// </summary>
        public static DrawdownInfo MaxDrawdownOfLevels(double[] levels)
        {
            RequireLevels(levels);
            int peakIdx = 0, bestPeak = 0, bestTrough = 0;
            double peak = levels[0], depth = 0.0;
            for (int i = 1; i < levels.Length; i++)
            {
                if (levels[i] > peak) { peak = levels[i]; peakIdx = i; }
                double dd = 1.0 - levels[i] / peak;
                if (dd > depth) { depth = dd; bestPeak = peakIdx; bestTrough = i; }
            }
            if (depth == 0.0) return new DrawdownInfo(0.0, 0, 0, 0);
            int recovery = -1;
            for (int i = bestTrough + 1; i < levels.Length; i++)
                if (levels[i] >= levels[bestPeak]) { recovery = i; break; }
            return new DrawdownInfo(depth, bestPeak, bestTrough, recovery);
        }

        /// <summary>Calmar ratio: <see cref="AnnualisedReturn"/> over the depth of <see cref="MaxDrawdown(double[])"/>. No drawdown gives +infinity (or NaN at zero return).</summary>
        public static double CalmarRatio(double[] returns, double periodsPerYear) =>
            AnnualisedReturn(returns, periodsPerYear) / MaxDrawdown(returns).Depth;

        // ---- Value-at-Risk passthrough ------------------------------------------------------------------------

        /// <summary>
        /// Historical VaR and ES of the return series, delegating to <see cref="HistoricalRisk.Measures(double[], double, QuantileMethod)"/>
        /// with the returns as P&amp;L per unit of capital: both come out as positive losses, in return units, for one period.
        /// </summary>
        public static VarEs HistoricalVarEs(double[] returns, double confidence, QuantileMethod method = QuantileMethod.Lower)
        {
            Require(returns, 1, nameof(returns));
            return HistoricalRisk.Measures(returns, confidence, method);
        }

        // ---- rolling windows -----------------------------------------------------------------------------------

        /// <summary>
        /// Applies <paramref name="statistic"/> to every window of <paramref name="window"/> consecutive returns. The result
        /// has the length of the input and is aligned on the window's last observation: entry <c>i</c> uses
        /// <c>returns[i - window + 1 .. i]</c>, and the first <c>window - 1</c> entries are NaN.
        /// </summary>
        public static double[] Rolling(double[] returns, int window, Func<double[], double> statistic)
        {
            ArgumentNullException.ThrowIfNull(statistic);
            RequireWindow(returns, window);
            var result = new double[returns.Length];
            var buffer = new double[window];
            for (int i = 0; i < returns.Length; i++)
            {
                if (i < window - 1) { result[i] = double.NaN; continue; }
                Array.Copy(returns, i - window + 1, buffer, 0, window);
                result[i] = statistic(buffer);
            }
            return result;
        }

        /// <summary>Rolling arithmetic mean per period; same alignment as <see cref="Rolling"/>.</summary>
        public static double[] RollingMean(double[] returns, int window)
        {
            RequireWindow(returns, window);
            var result = new double[returns.Length];
            for (int i = 0; i < returns.Length; i++)
                result[i] = i < window - 1 ? double.NaN : MeanOf(returns, i - window + 1, window);
            return result;
        }

        /// <summary>Rolling annualised volatility (window of at least 2); same alignment as <see cref="Rolling"/>.</summary>
        public static double[] RollingVolatility(double[] returns, int window, double periodsPerYear,
            MomentConvention convention = MomentConvention.SampleAdjusted)
        {
            RequireWindow(returns, window);
            double scale = Math.Sqrt(PeriodsPerYear(periodsPerYear));
            if (window < (convention == MomentConvention.Population ? 1 : 2))
                throw new ArgumentException("The window is too short for the chosen convention.", nameof(window));
            var result = new double[returns.Length];
            for (int i = 0; i < returns.Length; i++)
                result[i] = i < window - 1 ? double.NaN : Math.Sqrt(VarianceOf(returns, i - window + 1, window, convention)) * scale;
            return result;
        }

        /// <summary>Rolling annualised Sharpe ratio (window of at least 2); same alignment as <see cref="Rolling"/>.</summary>
        public static double[] RollingSharpe(double[] returns, int window, double periodsPerYear, double riskFreeRate = 0.0,
            MomentConvention convention = MomentConvention.SampleAdjusted)
        {
            RequireWindow(returns, window);
            double k = PeriodsPerYear(periodsPerYear);
            if (window < (convention == MomentConvention.Population ? 1 : 2))
                throw new ArgumentException("The window is too short for the chosen convention.", nameof(window));
            var result = new double[returns.Length];
            for (int i = 0; i < returns.Length; i++)
                result[i] = i < window - 1 ? double.NaN : SharpeOf(returns, i - window + 1, window, k, riskFreeRate, convention, nameof(returns));
            return result;
        }

        // ---- summary -------------------------------------------------------------------------------------------

        /// <summary>All headline statistics in one pass over the definitions above.</summary>
        public static PerformanceSummary Summarise(double[] returns, double periodsPerYear, double riskFreeRate = 0.0,
            double minimumAcceptableReturn = 0.0, MomentConvention convention = MomentConvention.SampleAdjusted)
        {
            Require(returns, 4, nameof(returns));
            var dd = MaxDrawdown(returns);
            double annualised = AnnualisedReturn(returns, periodsPerYear);
            return new PerformanceSummary(
                returns.Length, TotalReturn(returns), annualised, AnnualisedMean(returns, periodsPerYear),
                Volatility(returns, periodsPerYear, convention), Skewness(returns, convention), ExcessKurtosis(returns, convention),
                SharpeRatio(returns, periodsPerYear, riskFreeRate, convention), SortinoRatio(returns, periodsPerYear, minimumAcceptableReturn),
                dd, annualised / dd.Depth, HitRatio(returns));
        }

        // ---- internals -----------------------------------------------------------------------------------------

        static double[] WealthIndex(double[] returns)
        {
            Require(returns, 1, nameof(returns));
            var wealth = new double[returns.Length + 1];
            wealth[0] = 1.0;
            for (int i = 0; i < returns.Length; i++)
            {
                // A return of -100% or worse wipes the wealth out; keep it at a tiny positive floor so levels stay positive.
                wealth[i + 1] = wealth[i] * Math.Max(1.0 + returns[i], double.Epsilon);
            }
            return wealth;
        }

        static double MeanOf(double[] x, int start, int count)
        {
            double sum = 0.0;
            for (int i = start; i < start + count; i++) sum += x[i];
            return sum / count;
        }

        static double VarianceOf(double[] x, int start, int count, MomentConvention convention)
        {
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            for (int i = start; i < start + count; i++) { min = Math.Min(min, x[i]); max = Math.Max(max, x[i]); }
            if (min == max) return 0.0;
            double mean = MeanOf(x, start, count), ss = 0.0;
            for (int i = start; i < start + count; i++) ss += (x[i] - mean) * (x[i] - mean);
            return ss / (convention == MomentConvention.Population ? count : count - 1);
        }

        static double SharpeOf(double[] x, int start, int count, double k, double riskFreeRate, MomentConvention convention, string name)
        {
            if (x == null) throw new ArgumentNullException(name);
            if (count < (convention == MomentConvention.Population ? 1 : 2)) throw new ArgumentException("Too few returns for the chosen convention.", name);
            for (int i = start; i < start + count; i++) if (!double.IsFinite(x[i])) throw new ArgumentException("Returns must be finite.", name);
            double excess = MeanOf(x, start, count) - riskFreeRate / k;
            return excess / Math.Sqrt(VarianceOf(x, start, count, convention)) * Math.Sqrt(k);
        }

        static double DownsideOf(double[] x, double mar)
        {
            double sum = 0.0;
            foreach (double r in x)
            {
                double shortfall = Math.Min(r - mar, 0.0);
                sum += shortfall * shortfall;
            }
            return Math.Sqrt(sum / x.Length);
        }

        static bool IsConstant(double[] x)
        {
            for (int i = 1; i < x.Length; i++) if (x[i] != x[0]) return false;
            return true;
        }

        static double PeriodsPerYear(double k)
        {
            if (!(k > 0.0) || double.IsInfinity(k)) throw new ArgumentOutOfRangeException("periodsPerYear", "Periods per year must be positive and finite.");
            return k;
        }

        static void Require(double[] x, int minimum, string name)
        {
            ArgumentNullException.ThrowIfNull(x, name);
            if (x.Length < minimum) throw new ArgumentException($"At least {minimum} return(s) are needed.", name);
            foreach (double v in x) if (!double.IsFinite(v)) throw new ArgumentException("Returns must be finite.", name);
        }

        static void RequireLevels(double[] levels)
        {
            ArgumentNullException.ThrowIfNull(levels);
            if (levels.Length < 1) throw new ArgumentException("At least one level is needed.", nameof(levels));
            foreach (double v in levels)
                if (!(v > 0.0) || double.IsInfinity(v)) throw new ArgumentException("Levels must be finite and strictly positive.", nameof(levels));
        }

        static void RequireWindow(double[] x, int window)
        {
            Require(x, 1, nameof(x));
            if (window < 1 || window > x.Length) throw new ArgumentOutOfRangeException(nameof(window), "The window must be between 1 and the series length.");
        }
    }
}
