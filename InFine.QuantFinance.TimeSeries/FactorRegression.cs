using System;
using InFine.QuantFinance.Numerics;

namespace InFine.QuantFinance.TimeSeries
{
    /// <summary>
    /// Result of an ordinary-least-squares regression <c>y_t = alpha + beta_1 f_1t + ... + beta_K f_Kt + e_t</c>.
    /// Coefficient vectors have the intercept first: index 0 is alpha, index k is the beta on factor k (1-based).
    /// </summary>
    public sealed class FactorRegressionResult
    {
        internal FactorRegressionResult(int observations, int factors, double[] coefficients, double[] residuals,
            double rSquared, double adjustedRSquared, double residualVariance, double fStatistic, double fPValue,
            int hacLag, double[] classicalSe, double[] hacSe)
        {
            Observations = observations;
            Factors = factors;
            Coefficients = coefficients;
            Residuals = residuals;
            RSquared = rSquared;
            AdjustedRSquared = adjustedRSquared;
            ResidualVariance = residualVariance;
            FStatistic = fStatistic;
            FPValue = fPValue;
            HacLag = hacLag;
            ClassicalStandardErrors = classicalSe;
            HacStandardErrors = hacSe;
            ClassicalTStatistics = Ratio(coefficients, classicalSe);
            HacTStatistics = Ratio(coefficients, hacSe);
            ClassicalPValues = PValues(ClassicalTStatistics, DegreesOfFreedom);
            HacPValues = PValues(HacTStatistics, DegreesOfFreedom);
        }

        static double[] Ratio(double[] a, double[] b)
        {
            var r = new double[a.Length];
            for (int i = 0; i < r.Length; i++) r[i] = a[i] / b[i];
            return r;
        }

        double[] PValues(double[] t, int df)
        {
            var p = new double[t.Length];
            for (int i = 0; i < p.Length; i++) p[i] = StudentT.TwoSidedPValue(t[i], df);
            return p;
        }

        /// <summary>Number of observations T.</summary>
        public int Observations { get; }

        /// <summary>Number of factors K (the intercept is not counted).</summary>
        public int Factors { get; }

        /// <summary>Residual degrees of freedom <c>T - K - 1</c>.</summary>
        public int DegreesOfFreedom => Observations - Factors - 1;

        /// <summary>Estimated coefficients, intercept first (length K + 1).</summary>
        public double[] Coefficients { get; }

        /// <summary>Intercept (per-period alpha, in the units of the regressand).</summary>
        public double Alpha => Coefficients[0];

        /// <summary>Factor loadings (length K).</summary>
        public double[] Betas
        {
            get
            {
                var b = new double[Factors];
                Array.Copy(Coefficients, 1, b, 0, Factors);
                return b;
            }
        }

        /// <summary>Residuals <c>y - fitted</c> (length T).</summary>
        public double[] Residuals { get; }

        /// <summary><c>1 - RSS / TSS</c> with TSS about the sample mean (the model has an intercept).</summary>
        public double RSquared { get; }

        /// <summary><c>1 - (1 - R2) (T - 1) / (T - K - 1)</c>.</summary>
        public double AdjustedRSquared { get; }

        /// <summary>Unbiased residual variance <c>RSS / (T - K - 1)</c>.</summary>
        public double ResidualVariance { get; }

        /// <summary>Overall F statistic for H0: all betas are zero, <c>(ESS / K) / (RSS / (T - K - 1))</c>.</summary>
        public double FStatistic { get; }

        /// <summary>Upper-tail p-value of <see cref="FStatistic"/> under F(K, T - K - 1).</summary>
        public double FPValue { get; }

        /// <summary>Classical standard errors <c>sqrt(s^2 diag((X'X)^-1))</c>.</summary>
        public double[] ClassicalStandardErrors { get; }

        /// <summary>t statistics <c>coefficient / classical standard error</c>.</summary>
        public double[] ClassicalTStatistics { get; }

        /// <summary>Two-sided p-values of <see cref="ClassicalTStatistics"/> under Student-t(T - K - 1).</summary>
        public double[] ClassicalPValues { get; }

        /// <summary>Bartlett-kernel lag used by the Newey-West standard errors.</summary>
        public int HacLag { get; }

        /// <summary>Newey-West heteroskedasticity-and-autocorrelation-consistent standard errors (no small-sample correction).</summary>
        public double[] HacStandardErrors { get; }

        /// <summary>t statistics <c>coefficient / HAC standard error</c>.</summary>
        public double[] HacTStatistics { get; }

        /// <summary>Two-sided p-values of <see cref="HacTStatistics"/> under Student-t(T - K - 1) (a finite-sample convention; HAC theory is asymptotic).</summary>
        public double[] HacPValues { get; }

        /// <summary>Alpha scaled to a year: <c>alpha * periodsPerYear</c> (arithmetic, for returns that are small per period).</summary>
        public double AnnualisedAlpha(double periodsPerYear) => Alpha * periodsPerYear;
    }

    /// <summary>
    /// Multi-factor time-series regressions (CAPM, Fama-French style) by OLS solved through Householder QR, never through the
    /// normal equations, so ill-conditioned designs (NIST Longley, cond about 1e9 after the intercept) keep about 9 to 10 correct digits.
    /// </summary>
    /// <remarks>
    /// Inference. Classical standard errors assume homoskedastic, serially uncorrelated errors. The Newey-West HAC covariance is
    /// <c>(X'X)^-1 S (X'X)^-1</c> with <c>S = G0 + sum_{l=1..L} (1 - l/(L+1)) (Gl + Gl')</c> and <c>Gl = sum_t u_t u_{t-l}'</c>,
    /// <c>u_t = x_t e_t</c>: Bartlett weights keep S positive semi-definite. With L = 0 it reduces to White's HC0
    /// heteroskedasticity-robust covariance. Default lag: <c>floor(4 (T/100)^(2/9))</c> (Newey and West 1994 rule of thumb; 4 for T = 100,
    /// 6 for T = 1000). No finite-sample (T / (T - K - 1)) rescaling is applied. p-values use Student-t with T - K - 1 degrees of freedom.
    /// </remarks>
    public static class FactorRegression
    {
        /// <summary>Newey-West automatic lag <c>floor(4 (T/100)^(2/9))</c>, capped at <c>T - 1</c>.</summary>
        public static int DefaultHacLag(int observations)
        {
            if (observations < 1) throw new ArgumentOutOfRangeException(nameof(observations));
            return Math.Min(observations - 1, (int)Math.Floor(4.0 * Math.Pow(observations / 100.0, 2.0 / 9.0)));
        }

        /// <summary>Regresses <paramref name="y"/> (length T) on the T x K matrix <paramref name="factors"/> with an intercept.</summary>
        /// <param name="y">Regressand, length T.</param>
        /// <param name="factors">T x K factor matrix (no constant column: the intercept is added).</param>
        /// <param name="hacLag">Newey-West lag; <c>null</c> selects <see cref="DefaultHacLag"/>.</param>
        public static FactorRegressionResult Fit(double[] y, double[,] factors, int? hacLag = null)
        {
            ArgumentNullException.ThrowIfNull(y);
            ArgumentNullException.ThrowIfNull(factors);
            int t = y.Length, k = factors.GetLength(1), p = k + 1;
            if (k < 1) throw new ArgumentException("At least one factor is required.", nameof(factors));
            if (factors.GetLength(0) != t) throw new ArgumentException("Factors must have one row per observation.", nameof(factors));
            if (t <= p) throw new ArgumentException("Need more observations than coefficients (T > K + 1).", nameof(y));
            int lag = hacLag ?? DefaultHacLag(t);
            if (lag < 0 || lag >= t) throw new ArgumentOutOfRangeException(nameof(hacLag));

            var x = new double[t, p];
            for (int i = 0; i < t; i++)
            {
                if (!double.IsFinite(y[i])) throw new ArgumentException("Non-finite value in y.", nameof(y));
                x[i, 0] = 1.0;
                for (int j = 0; j < k; j++)
                {
                    if (!double.IsFinite(factors[i, j])) throw new ArgumentException("Non-finite value in factors.", nameof(factors));
                    x[i, j + 1] = factors[i, j];
                }
            }
            var qr = new QrDecomposition(x);
            if (!qr.IsFullRank) throw new ArgumentException("Factor matrix is numerically collinear (rank deficient with the intercept).", nameof(factors));
            double[] beta = qr.Solve(y);

            var resid = new double[t];
            double rss = 0.0, mean = 0.0;
            for (int i = 0; i < t; i++) mean += y[i];
            mean /= t;
            double tss = 0.0;
            for (int i = 0; i < t; i++)
            {
                double fit = 0.0;
                for (int j = 0; j < p; j++) fit += x[i, j] * beta[j];
                resid[i] = y[i] - fit;
                rss += resid[i] * resid[i];
                tss += (y[i] - mean) * (y[i] - mean);
            }
            int df = t - p;
            double s2 = rss / df;
            double r2 = tss > 0.0 ? 1.0 - rss / tss : double.NaN;
            double adj = 1.0 - (1.0 - r2) * (t - 1.0) / df;
            double ess = tss - rss;
            double fStat = rss > 0.0 ? (ess / k) / s2 : double.PositiveInfinity;
            double fP = StudentT.FSurvival(fStat, k, df);

            double[,] inv = qr.NormalMatrixInverse();
            var classical = new double[p];
            for (int j = 0; j < p; j++) classical[j] = Math.Sqrt(s2 * inv[j, j]);
            double[] hac = HacStandardErrors(x, resid, inv, lag);
            return new FactorRegressionResult(t, k, beta, resid, r2, adj, s2, fStat, fP, lag, classical, hac);
        }

        /// <summary>Fits every column of the T x N matrix <paramref name="returns"/> on the same factors.</summary>
        public static FactorRegressionResult[] FitMany(double[,] returns, double[,] factors, int? hacLag = null)
        {
            ArgumentNullException.ThrowIfNull(returns);
            int t = returns.GetLength(0), n = returns.GetLength(1);
            var results = new FactorRegressionResult[n];
            for (int a = 0; a < n; a++)
            {
                var y = new double[t];
                for (int i = 0; i < t; i++) y[i] = returns[i, a];
                results[a] = Fit(y, factors, hacLag);
            }
            return results;
        }

        /// <summary>
        /// Rolling-window regression for time-varying betas. Element <c>i</c> is the fit on observations <c>i .. i + window - 1</c>
        /// (so it is known at time index <c>i + window - 1</c>); there are <c>T - window + 1</c> elements.
        /// </summary>
        public static FactorRegressionResult[] Rolling(double[] y, double[,] factors, int window, int? hacLag = null)
        {
            ArgumentNullException.ThrowIfNull(y);
            ArgumentNullException.ThrowIfNull(factors);
            int t = y.Length, k = factors.GetLength(1);
            if (factors.GetLength(0) != t) throw new ArgumentException("Factors must have one row per observation.", nameof(factors));
            if (window <= k + 1 || window > t) throw new ArgumentOutOfRangeException(nameof(window), "Need K + 1 < window <= T.");
            var results = new FactorRegressionResult[t - window + 1];
            var yw = new double[window];
            var fw = new double[window, k];
            for (int start = 0; start < results.Length; start++)
            {
                for (int i = 0; i < window; i++)
                {
                    yw[i] = y[start + i];
                    for (int j = 0; j < k; j++) fw[i, j] = factors[start + i, j];
                }
                results[start] = Fit(yw, fw, hacLag);
            }
            return results;
        }

        static double[] HacStandardErrors(double[,] x, double[] e, double[,] xtxInv, int lag)
        {
            int t = e.Length, p = x.GetLength(1);
            var s = new double[p, p];
            for (int l = 0; l <= lag; l++)
            {
                double w = l == 0 ? 1.0 : 1.0 - l / (lag + 1.0);
                for (int a = 0; a < p; a++)
                    for (int b = 0; b < p; b++)
                    {
                        double g = 0.0;
                        for (int i = l; i < t; i++) g += x[i, a] * e[i] * x[i - l, b] * e[i - l];
                        if (l == 0) s[a, b] += g;
                        else { s[a, b] += w * g; s[b, a] += w * g; }
                    }
            }
            var se = new double[p];
            for (int a = 0; a < p; a++)
            {
                double v = 0.0;
                for (int b = 0; b < p; b++)
                    for (int c = 0; c < p; c++) v += xtxInv[a, b] * s[b, c] * xtxInv[c, a];
                se[a] = Math.Sqrt(v);
            }
            return se;
        }
    }
}
