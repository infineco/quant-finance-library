using System;
using InFine.QuantFinance.Numerics;
using InFine.QuantFinance.Risk;

namespace InFine.QuantFinance.TimeSeries
{
    /// <summary>Split of a portfolio's variance into factor-driven and asset-specific parts (same units as the model's covariance).</summary>
    public sealed class FactorRiskDecomposition
    {
        internal FactorRiskDecomposition(double[] exposures, double[] factorContributions, double factorVariance, double specificVariance)
        {
            FactorExposures = exposures;
            FactorContributions = factorContributions;
            FactorVariance = factorVariance;
            SpecificVariance = specificVariance;
        }

        /// <summary>Portfolio factor exposures <c>B' w</c> (length K).</summary>
        public double[] FactorExposures { get; }

        /// <summary>Euler contributions <c>e_k (Sf e)_k</c> of each factor to <see cref="FactorVariance"/>; they sum to it.</summary>
        public double[] FactorContributions { get; }

        /// <summary><c>w' B Sf B' w</c>.</summary>
        public double FactorVariance { get; }

        /// <summary><c>sum_i w_i^2 d_i</c>.</summary>
        public double SpecificVariance { get; }

        /// <summary>Total variance <c>w' (B Sf B' + D) w</c>.</summary>
        public double TotalVariance => FactorVariance + SpecificVariance;

        /// <summary>Total volatility.</summary>
        public double TotalVolatility => Math.Sqrt(TotalVariance);

        /// <summary>Volatility of the factor part.</summary>
        public double FactorVolatility => Math.Sqrt(Math.Max(FactorVariance, 0.0));

        /// <summary>Volatility of the specific part.</summary>
        public double SpecificVolatility => Math.Sqrt(SpecificVariance);

        /// <summary>Fraction of total variance explained by the factors.</summary>
        public double FactorShare => FactorVariance / TotalVariance;

        /// <summary>Fraction of total variance that is asset-specific.</summary>
        public double SpecificShare => SpecificVariance / TotalVariance;
    }

    /// <summary>
    /// Linear factor covariance model <c>Sigma = B Sf B' + D</c> for N assets and K factors: <c>B</c> is the N x K loading matrix,
    /// <c>Sf</c> the K x K factor covariance and <c>D</c> the diagonal of residual (specific) variances. All second moments are in the
    /// units of the return period they were estimated on; <see cref="Annualise"/> rescales them with the periods-per-year convention
    /// used everywhere else in this library (covariances scale linearly in time, loadings do not change).
    /// </summary>
    public sealed class FactorCovarianceModel
    {
        /// <summary>Builds a model from its parts (arrays are copied). <paramref name="specificVariances"/> must be non-negative.</summary>
        public FactorCovarianceModel(double[,] loadings, double[,] factorCovariance, double[] specificVariances)
        {
            ArgumentNullException.ThrowIfNull(loadings);
            ArgumentNullException.ThrowIfNull(factorCovariance);
            ArgumentNullException.ThrowIfNull(specificVariances);
            int n = loadings.GetLength(0), k = loadings.GetLength(1);
            if (factorCovariance.GetLength(0) != k || factorCovariance.GetLength(1) != k) throw new ArgumentException("Factor covariance must be K x K.", nameof(factorCovariance));
            if (specificVariances.Length != n) throw new ArgumentException("One specific variance per asset.", nameof(specificVariances));
            foreach (double d in specificVariances)
                if (!(d >= 0.0) || double.IsInfinity(d)) throw new ArgumentException("Specific variances must be finite and non-negative.", nameof(specificVariances));
            Loadings = (double[,])loadings.Clone();
            FactorCovariance = (double[,])factorCovariance.Clone();
            SpecificVariances = (double[])specificVariances.Clone();
        }

        /// <summary>N x K loading matrix B (internal storage; do not mutate).</summary>
        public double[,] Loadings { get; }

        /// <summary>K x K factor covariance Sf.</summary>
        public double[,] FactorCovariance { get; }

        /// <summary>Diagonal of D (length N).</summary>
        public double[] SpecificVariances { get; }

        /// <summary>Number of assets N.</summary>
        public int Assets => Loadings.GetLength(0);

        /// <summary>Number of factors K.</summary>
        public int Factors => Loadings.GetLength(1);

        /// <summary>
        /// Estimates the model by time-series regression of every asset on the factors: B from the OLS betas, Sf the sample covariance
        /// (T - 1 divisor) of the factors and D from the unbiased residual variances <c>RSS / (T - K - 1)</c>.
        /// </summary>
        public static FactorCovarianceModel Estimate(double[,] assetReturns, double[,] factorReturns)
        {
            FactorRegressionResult[] fits = FactorRegression.FitMany(assetReturns, factorReturns);
            return FromRegressions(fits, factorReturns);
        }

        /// <summary>Assembles a model from per-asset regressions already computed on <paramref name="factorReturns"/>.</summary>
        public static FactorCovarianceModel FromRegressions(FactorRegressionResult[] fits, double[,] factorReturns)
        {
            ArgumentNullException.ThrowIfNull(fits);
            ArgumentNullException.ThrowIfNull(factorReturns);
            int n = fits.Length, k = factorReturns.GetLength(1);
            var b = new double[n, k];
            var d = new double[n];
            for (int i = 0; i < n; i++)
            {
                if (fits[i].Factors != k) throw new ArgumentException("Regression and factor matrix disagree on K.", nameof(fits));
                double[] betas = fits[i].Betas;
                for (int j = 0; j < k; j++) b[i, j] = betas[j];
                d[i] = fits[i].ResidualVariance;
            }
            return new FactorCovarianceModel(b, Covariance.Sample(factorReturns), d);
        }

        /// <summary>The implied N x N asset covariance <c>B Sf B' + D</c>.</summary>
        public double[,] AssetCovariance()
        {
            int n = Assets, k = Factors;
            double[,] bs = Matrix.Multiply(Loadings, FactorCovariance);
            var cov = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j <= i; j++)
                {
                    double s = 0.0;
                    for (int m = 0; m < k; m++) s += bs[i, m] * Loadings[j, m];
                    if (i == j) s += SpecificVariances[i];
                    cov[i, j] = s;
                    cov[j, i] = s;
                }
            return cov;
        }

        /// <summary>The model rescaled to a year: Sf and D multiplied by <paramref name="periodsPerYear"/>, loadings unchanged (so <c>AssetCovariance()</c> scales by the same factor).</summary>
        public FactorCovarianceModel Annualise(double periodsPerYear)
        {
            double[] d = new double[Assets];
            for (int i = 0; i < d.Length; i++) d[i] = SpecificVariances[i] * periodsPerYear;
            return new FactorCovarianceModel(Loadings, Covariance.Annualise(FactorCovariance, periodsPerYear), d);
        }

        /// <summary>Factor / specific decomposition of the variance of the portfolio with the given weights (length N).</summary>
        public FactorRiskDecomposition Decompose(double[] weights)
        {
            ArgumentNullException.ThrowIfNull(weights);
            int n = Assets, k = Factors;
            if (weights.Length != n) throw new ArgumentException("One weight per asset.", nameof(weights));
            var e = new double[k];
            for (int m = 0; m < k; m++)
                for (int i = 0; i < n; i++) e[m] += Loadings[i, m] * weights[i];
            var contrib = new double[k];
            double factorVar = 0.0;
            for (int m = 0; m < k; m++)
            {
                double se = 0.0;
                for (int q = 0; q < k; q++) se += FactorCovariance[m, q] * e[q];
                contrib[m] = e[m] * se;
                factorVar += contrib[m];
            }
            double specific = 0.0;
            for (int i = 0; i < n; i++) specific += weights[i] * weights[i] * SpecificVariances[i];
            return new FactorRiskDecomposition(e, contrib, factorVar, specific);
        }
    }

    /// <summary>Principal-component (statistical) factors extracted from a covariance matrix.</summary>
    public sealed class StatisticalFactorResult
    {
        internal StatisticalFactorResult(double[] eigenvalues, double[,] loadings)
        {
            Eigenvalues = eigenvalues;
            Loadings = loadings;
            double total = 0.0;
            foreach (double v in eigenvalues) total += v;
            ExplainedVarianceRatio = new double[eigenvalues.Length];
            for (int i = 0; i < eigenvalues.Length; i++) ExplainedVarianceRatio[i] = eigenvalues[i] / total;
        }

        /// <summary>All N eigenvalues, descending.</summary>
        public double[] Eigenvalues { get; }

        /// <summary>Fraction <c>lambda_i / trace</c> of total variance carried by each component (all N; sums to 1).</summary>
        public double[] ExplainedVarianceRatio { get; }

        /// <summary>N x k matrix of the leading unit eigenvectors as columns; each column's largest-magnitude entry is made positive for a reproducible sign.</summary>
        public double[,] Loadings { get; }

        /// <summary>Fraction of total variance explained by the k retained components.</summary>
        public double RetainedVarianceRatio
        {
            get
            {
                double s = 0.0;
                for (int i = 0; i < Loadings.GetLength(1); i++) s += ExplainedVarianceRatio[i];
                return s;
            }
        }

        /// <summary>Factor scores <c>(R - mean) V</c> (T x k) of the T x N return matrix on the retained components.</summary>
        public double[,] Scores(double[,] returns)
        {
            ArgumentNullException.ThrowIfNull(returns);
            int t = returns.GetLength(0), n = Loadings.GetLength(0), k = Loadings.GetLength(1);
            if (returns.GetLength(1) != n) throw new ArgumentException("Returns must have N columns.", nameof(returns));
            double[] mean = Covariance.Mean(returns);
            var s = new double[t, k];
            for (int i = 0; i < t; i++)
                for (int c = 0; c < k; c++)
                {
                    double sum = 0.0;
                    for (int a = 0; a < n; a++) sum += (returns[i, a] - mean[a]) * Loadings[a, c];
                    s[i, c] = sum;
                }
            return s;
        }
    }

    /// <summary>PCA factors: eigen-decomposition of an asset covariance matrix (Jacobi, see <see cref="SymmetricEigen"/>).</summary>
    public static class StatisticalFactors
    {
        /// <summary>Top-<paramref name="factors"/> principal components of a symmetric covariance matrix.</summary>
        public static StatisticalFactorResult FromCovariance(double[,] covariance, int factors)
        {
            ArgumentNullException.ThrowIfNull(covariance);
            int n = covariance.GetLength(0);
            if (factors < 1 || factors > n) throw new ArgumentOutOfRangeException(nameof(factors), "1 <= factors <= N.");
            SymmetricEigenDecomposition eig = SymmetricEigen.Decompose(covariance);
            var values = new double[n];
            var vectors = new double[n, factors];
            for (int c = 0; c < n; c++) values[c] = eig.Values[n - 1 - c];   // ascending -> descending
            for (int c = 0; c < factors; c++)
            {
                int src = n - 1 - c;
                int argmax = 0;
                for (int a = 1; a < n; a++) if (Math.Abs(eig.Vectors[a, src]) > Math.Abs(eig.Vectors[argmax, src])) argmax = a;
                double sign = eig.Vectors[argmax, src] < 0.0 ? -1.0 : 1.0;
                for (int a = 0; a < n; a++) vectors[a, c] = sign * eig.Vectors[a, src];
            }
            return new StatisticalFactorResult(values, vectors);
        }

        /// <summary>Top-<paramref name="factors"/> principal components of the sample covariance of the T x N return matrix.</summary>
        public static StatisticalFactorResult FromReturns(double[,] returns, int factors) =>
            FromCovariance(Covariance.Sample(returns), factors);
    }
}
