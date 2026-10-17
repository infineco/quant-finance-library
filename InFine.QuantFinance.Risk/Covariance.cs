using System;
using InFine.QuantFinance.Numerics;

namespace InFine.QuantFinance.Risk
{
    /// <summary>
    /// Sample moments of a T x N matrix of returns (rows = dates, columns = assets) and the usual conversions between
    /// covariance, correlation and volatilities. Matrices are <c>double[,]</c> indexed <c>[row, column]</c>.
    /// </summary>
    public static class Covariance
    {
        /// <summary>Per-asset sample mean (length N).</summary>
        public static double[] Mean(double[,] returns)
        {
            RequireReturns(returns, 1);
            int t = returns.GetLength(0), n = returns.GetLength(1);
            var mean = new double[n];
            for (int i = 0; i < t; i++)
                for (int j = 0; j < n; j++)
                    mean[j] += returns[i, j];
            for (int j = 0; j < n; j++) mean[j] /= t;
            return mean;
        }

        /// <summary>
        /// Sample covariance matrix of the columns of <paramref name="returns"/>, computed on mean-centred data
        /// (two passes, so it is accurate when the mean dwarfs the dispersion).
        /// </summary>
        /// <param name="returns">T x N, finite; T at least 2.</param>
        /// <param name="unbiased">True (default) divides by <c>T - 1</c> (Bessel); false is the maximum-likelihood
        /// estimator and divides by <c>T</c>.</param>
        public static double[,] Sample(double[,] returns, bool unbiased = true) => Sample(returns, out _, unbiased);

        /// <summary>As <see cref="Sample(double[,], bool)"/>, also returning the sample mean.</summary>
        public static double[,] Sample(double[,] returns, out double[] mean, bool unbiased = true)
        {
            RequireReturns(returns, 2);
            double[,] centred = Centre(returns, out mean);
            int t = returns.GetLength(0), n = returns.GetLength(1);
            return Moment(centred, t, n, unbiased ? t - 1 : t);
        }

        /// <summary>Per-asset standard deviations <c>sqrt(diag(covariance))</c>.</summary>
        public static double[] Volatilities(double[,] covariance)
        {
            RequireCovariance(covariance);
            int n = covariance.GetLength(0);
            var vols = new double[n];
            for (int i = 0; i < n; i++)
            {
                if (covariance[i, i] < 0.0) throw new ArgumentException($"Negative variance on the diagonal at {i}.", nameof(covariance));
                vols[i] = Math.Sqrt(covariance[i, i]);
            }
            return vols;
        }

        /// <summary>
        /// Correlation matrix <c>C_ij = S_ij / (sigma_i sigma_j)</c>. Every variance must be strictly positive; a
        /// constant series has no correlation, drop it first. The diagonal is exactly 1 and off-diagonals are
        /// clamped to [-1, 1] against rounding.
        /// </summary>
        public static double[,] ToCorrelation(double[,] covariance)
        {
            RequireCovariance(covariance);
            int n = covariance.GetLength(0);
            double[] vols = Volatilities(covariance);
            var corr = new double[n, n];
            for (int i = 0; i < n; i++)
            {
                if (!(vols[i] > 0.0)) throw new ArgumentException($"Asset {i} has zero variance; its correlation is undefined.", nameof(covariance));
                corr[i, i] = 1.0;
                for (int j = 0; j < i; j++)
                {
                    double c = Math.Max(-1.0, Math.Min(1.0, covariance[i, j] / (vols[i] * vols[j])));
                    corr[i, j] = c;
                    corr[j, i] = c;
                }
            }
            return corr;
        }

        /// <summary>Covariance matrix <c>S_ij = C_ij sigma_i sigma_j</c> from a correlation matrix and volatilities.</summary>
        public static double[,] FromCorrelation(double[,] correlation, double[] volatilities)
        {
            RequireCovariance(correlation);
            ArgumentNullException.ThrowIfNull(volatilities);
            int n = correlation.GetLength(0);
            if (volatilities.Length != n) throw new ArgumentException("One volatility per asset is required.", nameof(volatilities));
            foreach (double v in volatilities)
                if (!(v >= 0.0) || double.IsInfinity(v)) throw new ArgumentException("Volatilities must be finite and non-negative.", nameof(volatilities));
            var cov = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    cov[i, j] = correlation[i, j] * volatilities[i] * volatilities[j];
            return cov;
        }

        /// <summary>
        /// Scales a per-period covariance to a year, assuming independent, identically distributed periods:
        /// multiplied by <paramref name="periodsPerYear"/> (252 daily, 52 weekly, 12 monthly).
        /// </summary>
        public static double[,] Annualise(double[,] covariance, double periodsPerYear)
        {
            RequireCovariance(covariance);
            RequirePeriods(periodsPerYear);
            int n = covariance.GetLength(0);
            var result = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    result[i, j] = covariance[i, j] * periodsPerYear;
            return result;
        }

        /// <summary>Scales a per-period volatility to a year: multiplied by <c>sqrt(periodsPerYear)</c>.</summary>
        public static double AnnualiseVolatility(double volatility, double periodsPerYear)
        {
            RequirePeriods(periodsPerYear);
            return volatility * Math.Sqrt(periodsPerYear);
        }

        /// <summary>Per-period volatilities scaled to a year (new array).</summary>
        public static double[] AnnualiseVolatilities(double[] volatilities, double periodsPerYear)
        {
            ArgumentNullException.ThrowIfNull(volatilities);
            RequirePeriods(periodsPerYear);
            var result = new double[volatilities.Length];
            double scale = Math.Sqrt(periodsPerYear);
            for (int i = 0; i < result.Length; i++) result[i] = volatilities[i] * scale;
            return result;
        }

        /// <summary>Mean-centres the columns; returns the centred copy and the means.</summary>
        internal static double[,] Centre(double[,] returns, out double[] mean)
        {
            mean = Mean(returns);
            int t = returns.GetLength(0), n = returns.GetLength(1);
            var centred = new double[t, n];
            for (int i = 0; i < t; i++)
                for (int j = 0; j < n; j++)
                    centred[i, j] = returns[i, j] - mean[j];
            return centred;
        }

        /// <summary><c>x^T x / divisor</c> for an already centred T x N matrix.</summary>
        internal static double[,] Moment(double[,] x, int t, int n, double divisor)
        {
            var s = new double[n, n];
            for (int i = 0; i < t; i++)
                for (int a = 0; a < n; a++)
                {
                    double xa = x[i, a];
                    for (int b = 0; b <= a; b++) s[a, b] += xa * x[i, b];
                }
            for (int a = 0; a < n; a++)
                for (int b = 0; b <= a; b++)
                {
                    double v = s[a, b] / divisor;
                    s[a, b] = v;
                    s[b, a] = v;
                }
            return s;
        }

        internal static void RequireReturns(double[,] returns, int minDates)
        {
            ArgumentNullException.ThrowIfNull(returns);
            int t = returns.GetLength(0), n = returns.GetLength(1);
            if (n < 1) throw new ArgumentException("At least one asset (column) is required.", nameof(returns));
            if (t < minDates) throw new ArgumentException($"At least {minDates} dates (rows) are required, got {t}.", nameof(returns));
            if (!Matrix.IsFinite(returns)) throw new ArgumentException("Returns must be finite.", nameof(returns));
        }

        static void RequireCovariance(double[,] m)
        {
            ArgumentNullException.ThrowIfNull(m);
            if (m.GetLength(0) != m.GetLength(1)) throw new ArgumentException("Matrix must be square.", nameof(m));
            if (!Matrix.IsFinite(m)) throw new ArgumentException("Matrix entries must be finite.", nameof(m));
        }

        static void RequirePeriods(double periodsPerYear)
        {
            if (!(periodsPerYear > 0.0) || double.IsInfinity(periodsPerYear))
                throw new ArgumentOutOfRangeException(nameof(periodsPerYear), "Periods per year must be positive and finite.");
        }
    }
}
