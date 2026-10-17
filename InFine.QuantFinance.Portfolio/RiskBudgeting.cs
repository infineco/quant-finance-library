using System;

namespace InFine.QuantFinance.Portfolio
{
    /// <summary>
    /// Naive risk-parity baselines, the risk-contribution report and volatility-target scaling. Host-side code.
    /// The covariance is a one-period covariance of returns (symmetric, finite, positive semi-definite); weights are
    /// fractions of capital. The risk-budgeting solver itself is <see cref="RiskParityOptimizer"/>.
    /// </summary>
    public static class RiskBudgeting
    {
        /// <summary>
        /// Inverse-volatility weights <c>w_i proportional to 1/sigma_i</c>, summing to one. Ignores correlations; it is
        /// exactly the equal-risk-contribution portfolio when the covariance is diagonal or when all pairwise
        /// correlations are equal (the common correlation drops out of the first-order conditions).
        /// </summary>
        /// <param name="covariance">Covariance with strictly positive variances on the diagonal.</param>
        public static double[] InverseVolatility(double[,] covariance)
        {
            RiskBudgetingArgs.Covariance(covariance);
            int n = covariance.GetLength(0);
            var vols = new double[n];
            for (int i = 0; i < n; i++) vols[i] = Math.Sqrt(covariance[i, i]);
            return InverseVolatility(vols);
        }

        /// <summary>Inverse-volatility weights from per-asset volatilities, which must be finite and strictly positive.</summary>
        public static double[] InverseVolatility(double[] volatilities)
        {
            ArgumentNullException.ThrowIfNull(volatilities);
            if (volatilities.Length == 0) throw new ArgumentException("At least one asset is required.", nameof(volatilities));
            var w = new double[volatilities.Length];
            double sum = 0.0;
            for (int i = 0; i < w.Length; i++)
            {
                double v = volatilities[i];
                if (!(v > 0.0) || double.IsInfinity(v)) throw new ArgumentException($"Volatility {i} must be finite and strictly positive.", nameof(volatilities));
                w[i] = 1.0 / v;
                sum += w[i];
            }
            for (int i = 0; i < w.Length; i++) w[i] /= sum;
            return w;
        }

        /// <summary>
        /// Inverse-variance weights <c>w_i proportional to 1/sigma_i^2</c>, summing to one: the minimum-variance
        /// portfolio when all correlations are zero and volatilities differ, otherwise only a heuristic. They tilt more
        /// strongly towards low-volatility assets than <see cref="InverseVolatility(double[,])"/> and do not equalise risk contributions.
        /// </summary>
        public static double[] InverseVariance(double[,] covariance)
        {
            RiskBudgetingArgs.Covariance(covariance);
            int n = covariance.GetLength(0);
            var w = new double[n];
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                w[i] = 1.0 / covariance[i, i];
                sum += w[i];
            }
            for (int i = 0; i < n; i++) w[i] /= sum;
            return w;
        }

        /// <summary>
        /// Marginal, absolute and relative risk contributions of <paramref name="weights"/> (see
        /// <see cref="RiskContributions"/> for the conventions, shared with the Risk library). Weights may be any finite
        /// real numbers (long/short, leveraged) as long as the portfolio volatility is positive.
        /// </summary>
        /// <exception cref="InvalidOperationException">The portfolio has zero volatility: contributions are undefined.</exception>
        public static RiskContributions Contributions(double[] weights, double[,] covariance)
        {
            RiskBudgetingArgs.Weights(weights, covariance);
            int n = weights.Length;
            double[] sw = RiskBudgetingArgs.Multiply(covariance, weights);
            double variance = 0.0;
            for (int i = 0; i < n; i++) variance += weights[i] * sw[i];
            if (!(variance > 0.0)) throw new InvalidOperationException("The portfolio has zero volatility; risk contributions are undefined.");
            double sigma = Math.Sqrt(variance);
            var marginal = new double[n];
            var absolute = new double[n];
            var relative = new double[n];
            for (int i = 0; i < n; i++)
            {
                marginal[i] = sw[i] / sigma;
                absolute[i] = weights[i] * marginal[i];
                relative[i] = weights[i] * sw[i] / variance;
            }
            return new RiskContributions(sigma, marginal, absolute, relative);
        }

        /// <summary>Portfolio volatility <c>sqrt(w' Sigma w)</c> in the units of the covariance (zero for a zero portfolio).</summary>
        public static double Volatility(double[] weights, double[,] covariance)
        {
            RiskBudgetingArgs.Weights(weights, covariance);
            double[] sw = RiskBudgetingArgs.Multiply(covariance, weights);
            double variance = 0.0;
            for (int i = 0; i < weights.Length; i++) variance += weights[i] * sw[i];
            return Math.Sqrt(Math.Max(variance, 0.0));
        }

        /// <summary>
        /// Leverage <c>L = target / sigma_p</c> that scales <paramref name="weights"/> to the target volatility. Volatility is
        /// homogeneous of degree one in the weights, so the scaled portfolio has volatility exactly the target. The target
        /// and the covariance must be per the same period (annualise both or neither; see <c>Covariance.Annualise</c>).
        /// </summary>
        /// <param name="weights">Risky-asset weights (typically summing to one).</param>
        /// <param name="covariance">One-period covariance.</param>
        /// <param name="targetVolatility">Target portfolio volatility, finite and positive.</param>
        /// <param name="maxLeverage">Optional cap on the leverage (for example 1 for no borrowing); default no cap.</param>
        /// <exception cref="InvalidOperationException">The portfolio has zero volatility.</exception>
        public static double VolatilityTargetLeverage(double[] weights, double[,] covariance, double targetVolatility, double maxLeverage = double.PositiveInfinity)
        {
            if (!(targetVolatility > 0.0) || double.IsInfinity(targetVolatility))
                throw new ArgumentOutOfRangeException(nameof(targetVolatility), "The target volatility must be finite and positive.");
            if (!(maxLeverage > 0.0)) throw new ArgumentOutOfRangeException(nameof(maxLeverage), "The leverage cap must be positive.");
            double sigma = Volatility(weights, covariance);
            if (!(sigma > 0.0)) throw new InvalidOperationException("The portfolio has zero volatility; it cannot be scaled to a target.");
            return Math.Min(targetVolatility / sigma, maxLeverage);
        }

        /// <summary>
        /// Weights scaled by <see cref="VolatilityTargetLeverage"/>. The sum of the result minus one is the amount borrowed
        /// (leverage above one) or held in cash (below one) per unit of capital; the cost of that financing is not modelled.
        /// </summary>
        public static double[] ScaleToVolatility(double[] weights, double[,] covariance, double targetVolatility, double maxLeverage = double.PositiveInfinity)
        {
            double leverage = VolatilityTargetLeverage(weights, covariance, targetVolatility, maxLeverage);
            var scaled = new double[weights.Length];
            for (int i = 0; i < scaled.Length; i++) scaled[i] = leverage * weights[i];
            return scaled;
        }
    }

    internal static class RiskBudgetingArgs
    {
        /// <summary>Square, finite, symmetric (relative 1e-10) with a strictly positive diagonal.</summary>
        public static void Covariance(double[,] covariance)
        {
            ArgumentNullException.ThrowIfNull(covariance);
            int n = covariance.GetLength(0);
            if (n == 0 || covariance.GetLength(1) != n) throw new ArgumentException("Covariance must be a non-empty square matrix.", nameof(covariance));
            double maxAbs = 0.0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double v = covariance[i, j];
                    if (!double.IsFinite(v)) throw new ArgumentException("Covariance must be finite.", nameof(covariance));
                    maxAbs = Math.Max(maxAbs, Math.Abs(v));
                }
            for (int i = 0; i < n; i++)
            {
                if (!(covariance[i, i] > 0.0)) throw new ArgumentException($"Variance {i} must be strictly positive.", nameof(covariance));
                for (int j = 0; j < i; j++)
                    if (Math.Abs(covariance[i, j] - covariance[j, i]) > 1e-10 * maxAbs)
                        throw new ArgumentException("Covariance must be symmetric.", nameof(covariance));
            }
        }

        public static void Weights(double[] weights, double[,] covariance)
        {
            ArgumentNullException.ThrowIfNull(weights);
            ArgumentNullException.ThrowIfNull(covariance);
            int n = weights.Length;
            if (n == 0 || covariance.GetLength(0) != n || covariance.GetLength(1) != n)
                throw new ArgumentException($"Covariance must be {n}x{n}, one row per weight.", nameof(covariance));
            for (int i = 0; i < n; i++)
                if (!double.IsFinite(weights[i])) throw new ArgumentException("Weights must be finite.", nameof(weights));
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    if (!double.IsFinite(covariance[i, j])) throw new ArgumentException("Covariance must be finite.", nameof(covariance));
        }

        public static double[] Multiply(double[,] a, double[] x)
        {
            int n = x.Length;
            var r = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = 0.0;
                for (int j = 0; j < n; j++) s += a[i, j] * x[j];
                r[i] = s;
            }
            return r;
        }
    }
}
