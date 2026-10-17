using System;

namespace InFine.QuantFinance.Risk
{
    /// <summary>A shrinkage covariance estimate <c>delta F + (1 - delta) S</c> of the sample covariance <c>S</c> and a structured target <c>F</c>.</summary>
    public sealed class ShrinkageEstimate
    {
        internal ShrinkageEstimate(double[,] covariance, double[,] sample, double[,] target, double intensity)
        {
            Covariance = covariance;
            Sample = sample;
            Target = target;
            Intensity = intensity;
        }

        /// <summary>The shrunk covariance <c>delta F + (1 - delta) S</c>.</summary>
        public double[,] Covariance { get; }

        /// <summary>The sample covariance matrix <c>S</c> (maximum-likelihood, divisor T) the estimate shrinks.</summary>
        public double[,] Sample { get; }

        /// <summary>The shrinkage target <c>F</c>.</summary>
        public double[,] Target { get; }

        /// <summary>Optimal shrinkage intensity <c>delta</c> in [0, 1]: 0 is the sample covariance, 1 the target.</summary>
        public double Intensity { get; }
    }

    /// <summary>
    /// Ledoit-Wolf shrinkage covariance estimators with the asymptotically optimal intensity. Both estimators
    /// work on the maximum-likelihood sample covariance <c>S = (1/T) X^T X</c> of mean-centred returns, as in the
    /// authors' published code, and need no tuning parameter.
    /// </summary>
    public static class LedoitWolf
    {
        /// <summary>
        /// Shrinkage towards the constant-correlation matrix (Ledoit and Wolf, "Honey, I shrunk the sample covariance
        /// matrix", Journal of Portfolio Management 30(4), 2004; the reference MATLAB code is <c>covCor.m</c>). The
        /// target keeps the sample variances and replaces every correlation by their average <c>rBar</c>:
        /// <c>F_ii = s_ii</c>, <c>F_ij = rBar sqrt(s_ii s_jj)</c>. With <c>pi-hat = sum_ij pi_ij</c>,
        /// <c>rho-hat</c> the asymptotic covariance of target and sample, and <c>gamma-hat = ||F - S||_F^2</c>, the
        /// intensity is <c>delta = max(0, min(1, (pi-hat - rho-hat) / (gamma-hat T)))</c> (section 2 of the paper).
        /// </summary>
        /// <param name="returns">T x N returns (rows = dates), N at least 2, every variance strictly positive.</param>
        /// <remarks>
        /// Cost O(T N^2). For N = 2 the target equals the sample covariance, so the result is the sample covariance
        /// and the intensity is reported as 1 (any intensity gives the same matrix).
        /// </remarks>
        public static ShrinkageEstimate ConstantCorrelation(double[,] returns)
        {
            Covariance.RequireReturns(returns, 2);
            int t = returns.GetLength(0), n = returns.GetLength(1);
            if (n < 2) throw new ArgumentException("Constant-correlation shrinkage needs at least two assets.", nameof(returns));
            double[,] x = Covariance.Centre(returns, out _);
            double[,] s = Covariance.Moment(x, t, n, t);

            var sd = new double[n];
            for (int i = 0; i < n; i++)
            {
                if (!(s[i, i] > 0.0)) throw new ArgumentException($"Asset {i} has zero variance.", nameof(returns));
                sd[i] = Math.Sqrt(s[i, i]);
            }

            // Average sample correlation and the target.
            double sumCorr = 0.0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < i; j++)
                    sumCorr += s[i, j] / (sd[i] * sd[j]);
            double rBar = 2.0 * sumCorr / ((double)n * (n - 1));
            var f = new double[n, n];
            for (int i = 0; i < n; i++)
            {
                f[i, i] = s[i, i];
                for (int j = 0; j < i; j++)
                {
                    f[i, j] = rBar * sd[i] * sd[j];
                    f[j, i] = f[i, j]; // exactly symmetric, not just up to rounding
                }
            }

            // pi_ij = (1/T) sum_t (x_i x_j - s_ij)^2 ; theta_ij = (1/T) sum_t (x_i^2 - s_ii)(x_i x_j - s_ij).
            var theta = new double[n, n];
            var pi = new double[n, n];
            for (int k = 0; k < t; k++)
                for (int i = 0; i < n; i++)
                {
                    double xi = x[k, i], dii = xi * xi - s[i, i];
                    for (int j = 0; j < n; j++)
                    {
                        double d = xi * x[k, j] - s[i, j];
                        pi[i, j] += d * d;
                        theta[i, j] += dii * d;
                    }
                }
            double piSum = 0.0, piDiag = 0.0, thetaWeighted = 0.0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    piSum += pi[i, j] / t;
                    if (i == j) piDiag += pi[i, j] / t;
                    else thetaWeighted += sd[j] / sd[i] * theta[i, j] / t;
                }
            double rho = piDiag + rBar * thetaWeighted;

            double gamma = 0.0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double d = s[i, j] - f[i, j];
                    gamma += d * d;
                }

            double delta = gamma > 0.0 ? Clamp01((piSum - rho) / gamma / t) : 1.0;
            return Combine(s, f, delta);
        }

        /// <summary>
        /// Shrinkage towards a scaled identity (Ledoit and Wolf, "A well-conditioned estimator for large-dimensional
        /// covariance matrices", Journal of Multivariate Analysis 88(2), 2004; the reference MATLAB code is
        /// <c>cov1Para.m</c>, section 3 of the paper). With <c>m = tr(S)/N</c>,
        /// <c>d^2 = ||S - m I||^2</c>, <c>b-bar^2 = (1/T^2) sum_t ||x_t x_t^T - S||^2</c>,
        /// <c>b^2 = min(b-bar^2, d^2)</c> (all norms Frobenius, same normalisation in numerator and denominator),
        /// the estimator is <c>(b^2/d^2) m I + (a^2/d^2) S</c> with <c>a^2 = d^2 - b^2</c>, so <c>delta = b^2 / d^2</c>.
        /// The result is positive definite whenever <c>m &gt; 0</c> and <c>delta &gt; 0</c> and its condition number is
        /// no worse than that of <c>S</c>; it is well-conditioned even when N exceeds T. Equals scikit-learn's
        /// <c>ledoit_wolf</c> shrinkage.
        /// </summary>
        /// <param name="returns">T x N returns (rows = dates), T at least 2.</param>
        /// <remarks>
        /// Cost O(T N^2). If <c>S</c> is already a multiple of the identity (<c>d^2 = 0</c>) the intensity is
        /// reported as 1: the target is the sample covariance.
        /// </remarks>
        public static ShrinkageEstimate ScaledIdentity(double[,] returns)
        {
            Covariance.RequireReturns(returns, 2);
            int t = returns.GetLength(0), n = returns.GetLength(1);
            double[,] x = Covariance.Centre(returns, out _);
            double[,] s = Covariance.Moment(x, t, n, t);

            double m = 0.0;
            for (int i = 0; i < n; i++) m += s[i, i];
            m /= n;

            double d2 = 0.0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double d = s[i, j] - (i == j ? m : 0.0);
                    d2 += d * d;
                }

            double b2bar = 0.0;
            for (int k = 0; k < t; k++)
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                    {
                        double d = x[k, i] * x[k, j] - s[i, j];
                        b2bar += d * d;
                    }
            b2bar /= (double)t * t;

            double delta = d2 > 0.0 ? Clamp01(Math.Min(b2bar, d2) / d2) : 1.0;
            var f = new double[n, n];
            for (int i = 0; i < n; i++) f[i, i] = m;
            return Combine(s, f, delta);
        }

        static ShrinkageEstimate Combine(double[,] s, double[,] f, double delta)
        {
            int n = s.GetLength(0);
            var sigma = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    sigma[i, j] = delta * f[i, j] + (1.0 - delta) * s[i, j];
            return new ShrinkageEstimate(sigma, s, f, delta);
        }

        static double Clamp01(double x) => double.IsNaN(x) ? 1.0 : Math.Max(0.0, Math.Min(1.0, x));
    }
}
