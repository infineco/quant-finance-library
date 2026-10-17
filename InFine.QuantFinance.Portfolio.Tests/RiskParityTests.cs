using System;
using InFine.QuantFinance.Numerics;
using InFine.QuantFinance.Risk;
using Xunit;

namespace InFine.QuantFinance.Portfolio.Tests
{
    public class RiskParityTests
    {
        static readonly RiskParityOptions NewtonOpts = new RiskParityOptions { Method = RiskParityMethod.Newton, FallbackToCoordinateDescent = false };
        static readonly RiskParityOptions CcdOpts = new RiskParityOptions { Method = RiskParityMethod.CoordinateDescent, FallbackToCoordinateDescent = false };

        /// <summary>Random positive definite covariance A A' / (n + k) + 0.1 diag, scaled by random volatilities; deterministic for a seed.</summary>
        static double[,] RandomCovariance(int n, int seed)
        {
            var rng = new System.Random(seed);
            int k = n + 5;
            var a = new double[n, k];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < k; j++) a[i, j] = rng.NextDouble() * 2.0 - 1.0;
            double[,] c = Matrix.MultiplyByTranspose(a);
            var vol = new double[n];
            for (int i = 0; i < n; i++) vol[i] = 0.05 + 0.3 * rng.NextDouble();
            var cov = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    cov[i, j] = (c[i, j] / k + (i == j ? 0.1 : 0.0)) * vol[i] * vol[j];
            return cov;
        }

        static double[] RandomBudgets(int n, int seed)
        {
            var rng = new System.Random(seed);
            var b = new double[n];
            double s = 0.0;
            for (int i = 0; i < n; i++) { b[i] = 0.2 + rng.NextDouble(); s += b[i]; }
            for (int i = 0; i < n; i++) b[i] /= s;
            return b;
        }

        static double[,] Equicorrelated(double[] vols, double rho)
        {
            int n = vols.Length;
            var cov = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    cov[i, j] = (i == j ? 1.0 : rho) * vols[i] * vols[j];
            return cov;
        }

        [Fact]
        public void DiagonalCovarianceGivesInverseVolatilityWeights()
        {
            double[] vols = { 0.10, 0.20, 0.05, 0.40 };
            var cov = new double[4, 4];
            for (int i = 0; i < 4; i++) cov[i, i] = vols[i] * vols[i];
            double[] expected = RiskBudgeting.InverseVolatility(vols);
            foreach (var opts in new[] { NewtonOpts, CcdOpts })
            {
                double[] w = RiskParityOptimizer.EqualRiskContribution(cov, opts).Weights;
                for (int i = 0; i < 4; i++) Assert.Equal(expected[i], w[i], 12);
            }
            // Inverse-vol closed form check: 1/0.1 : 1/0.2 : 1/0.05 : 1/0.4 = 10 : 5 : 20 : 2.5.
            Assert.Equal(10.0 / 37.5, expected[0], 14);
        }

        [Theory]
        [InlineData(-0.9)]
        [InlineData(-0.3)]
        [InlineData(0.0)]
        [InlineData(0.5)]
        [InlineData(0.99)]
        public void TwoAssetErcIsInverseVolatilityWhateverTheCorrelation(double rho)
        {
            double s1 = 0.15, s2 = 0.30;
            var cov = new double[,] { { s1 * s1, rho * s1 * s2 }, { rho * s1 * s2, s2 * s2 } };
            // Closed form: w1 / w2 = s2 / s1 = 2, so w = (2/3, 1/3).
            foreach (var opts in new[] { NewtonOpts, CcdOpts })
            {
                var res = RiskParityOptimizer.EqualRiskContribution(cov, opts);
                Assert.Equal(2.0 / 3.0, res.Weights[0], 11);
                Assert.Equal(1.0 / 3.0, res.Weights[1], 11);
                Assert.Equal(0.5, res.Contributions.Relative[0], 11);
            }
        }

        [Theory]
        [InlineData(0.3)]
        [InlineData(0.8)]
        [InlineData(-0.1)]
        public void UniformCorrelationErcIsInverseVolatility(double rho)
        {
            double[] vols = { 0.08, 0.12, 0.20, 0.35, 0.05, 0.16 };
            double[,] cov = Equicorrelated(vols, rho);
            double[] expected = RiskBudgeting.InverseVolatility(cov);
            foreach (var opts in new[] { NewtonOpts, CcdOpts })
            {
                double[] w = RiskParityOptimizer.EqualRiskContribution(cov, opts).Weights;
                for (int i = 0; i < w.Length; i++) Assert.Equal(expected[i], w[i], 11);
            }
        }

        [Theory]
        [InlineData(2, 1)]
        [InlineData(5, 2)]
        [InlineData(10, 3)]
        [InlineData(30, 4)]
        [InlineData(100, 5)]
        public void RelativeContributionsEqualEqualBudgets(int n, int seed)
        {
            double[,] cov = RandomCovariance(n, seed);
            var res = RiskParityOptimizer.EqualRiskContribution(cov);
            Assert.Equal(RiskParityMethod.Newton, res.Method);
            Assert.Equal(1.0, Sum(res.Weights), 12);
            foreach (double w in res.Weights) Assert.True(w > 0.0);
            foreach (double rc in res.Contributions.Relative) Assert.True(Math.Abs(rc - 1.0 / n) < 1e-10, $"RC {rc:R} vs {1.0 / n:R}");
        }

        [Theory]
        [InlineData(3, 11)]
        [InlineData(8, 12)]
        [InlineData(25, 13)]
        [InlineData(100, 14)]
        public void RelativeContributionsEqualArbitraryBudgets(int n, int seed)
        {
            double[,] cov = RandomCovariance(n, seed);
            double[] b = RandomBudgets(n, seed + 100);
            var res = RiskParityOptimizer.Solve(cov, b);
            for (int i = 0; i < n; i++) Assert.True(Math.Abs(res.Contributions.Relative[i] - b[i]) < 1e-10, $"asset {i}: {res.Contributions.Relative[i]:R} vs {b[i]:R}");
            Assert.True(res.Residual < 1e-10);
            Assert.True(res.Iterations < 30, $"Newton took {res.Iterations} iterations");
        }

        [Theory]
        [InlineData(2, 21)]
        [InlineData(5, 22)]
        [InlineData(10, 23)]
        [InlineData(20, 24)]
        public void NewtonAndCoordinateDescentAgree(int n, int seed)
        {
            double[,] cov = RandomCovariance(n, seed);
            double[] b = RandomBudgets(n, seed + 100);
            double[] wn = RiskParityOptimizer.Solve(cov, b, NewtonOpts).Weights;
            var ccd = RiskParityOptimizer.Solve(cov, b, CcdOpts);
            Assert.Equal(RiskParityMethod.CoordinateDescent, ccd.Method);
            for (int i = 0; i < n; i++) Assert.True(Math.Abs(wn[i] - ccd.Weights[i]) < 1e-9, $"asset {i}: {wn[i]:R} vs {ccd.Weights[i]:R}");
        }

        [Fact]
        public void CoordinateDescentReachesBudgetsOnLargerProblem()
        {
            double[,] cov = RandomCovariance(100, 31);
            double[] b = RandomBudgets(100, 32);
            var res = RiskParityOptimizer.Solve(cov, b, CcdOpts);
            for (int i = 0; i < b.Length; i++) Assert.True(Math.Abs(res.Contributions.Relative[i] - b[i]) < 1e-10);
            double[] wn = RiskParityOptimizer.Solve(cov, b, NewtonOpts).Weights;
            for (int i = 0; i < b.Length; i++) Assert.True(Math.Abs(wn[i] - res.Weights[i]) < 1e-9);
        }

        [Fact]
        public void HighlyCorrelatedAssetsStillConverge()
        {
            double[] vols = { 0.10, 0.15, 0.25, 0.40, 0.20 };
            double[,] cov = Equicorrelated(vols, 0.97);
            var res = RiskParityOptimizer.EqualRiskContribution(cov);
            Assert.True(res.Residual < 1e-10);
            var ccd = RiskParityOptimizer.EqualRiskContribution(cov, CcdOpts);
            for (int i = 0; i < vols.Length; i++) Assert.True(Math.Abs(res.Weights[i] - ccd.Weights[i]) < 1e-9);
        }

        [Fact]
        public void WeightsAreInvariantToCovarianceScale()
        {
            double[,] cov = RandomCovariance(12, 41);
            double[] b = RandomBudgets(12, 42);
            double[] w = RiskParityOptimizer.Solve(cov, b).Weights;
            foreach (double c in new[] { 1e-6, 0.01, 250.0, 1e6 })
            {
                var scaled = new double[12, 12];
                for (int i = 0; i < 12; i++)
                    for (int j = 0; j < 12; j++) scaled[i, j] = c * cov[i, j];
                foreach (var opts in new[] { NewtonOpts, CcdOpts })
                {
                    double[] ws = RiskParityOptimizer.Solve(scaled, b, opts).Weights;
                    for (int i = 0; i < 12; i++) Assert.True(Math.Abs(w[i] - ws[i]) < 1e-9, $"scale {c}, asset {i}");
                }
            }
        }

        [Fact]
        public void LowerBudgetGivesLowerWeightAndNewtonFallsBackWhenAsked()
        {
            double[,] cov = RandomCovariance(6, 51);
            double[] b = { 0.5, 0.1, 0.1, 0.1, 0.1, 0.1 };
            var res = RiskParityOptimizer.Solve(cov, b);
            Assert.Equal(RiskParityMethod.Newton, res.Method);
            // Starting from CCD with too few sweeps and fallback enabled, Newton finishes the job.
            var viaFallback = RiskParityOptimizer.Solve(cov, b, new RiskParityOptions { Method = RiskParityMethod.CoordinateDescent, MaxSweeps = 2 });
            Assert.Equal(RiskParityMethod.Newton, viaFallback.Method);
            for (int i = 0; i < 6; i++) Assert.Equal(res.Weights[i], viaFallback.Weights[i], 10);
            // Without fallback the limit is reported honestly.
            Assert.Throws<InvalidOperationException>(() => RiskParityOptimizer.Solve(cov, b, new RiskParityOptions { Method = RiskParityMethod.CoordinateDescent, MaxSweeps = 2, FallbackToCoordinateDescent = false }));
        }

        [Fact]
        public void InvalidBudgetsAreRejected()
        {
            double[,] cov = RandomCovariance(3, 61);
            Assert.Throws<ArgumentException>(() => RiskParityOptimizer.Solve(cov, new[] { 0.5, 0.5, 0.0 }));       // zero budget
            Assert.Throws<ArgumentException>(() => RiskParityOptimizer.Solve(cov, new[] { 0.6, 0.6, -0.2 }));      // negative
            Assert.Throws<ArgumentException>(() => RiskParityOptimizer.Solve(cov, new[] { 0.3, 0.3, 0.3 }));       // sum != 1
            Assert.Throws<ArgumentException>(() => RiskParityOptimizer.Solve(cov, new[] { 0.5, 0.5 }));            // wrong length
            Assert.Throws<ArgumentException>(() => RiskParityOptimizer.Solve(cov, new[] { 0.5, 0.5, double.NaN }));
            Assert.Throws<ArgumentNullException>(() => RiskParityOptimizer.Solve(cov, null!));
        }

        [Fact]
        public void InvalidCovariancesAreRejected()
        {
            Assert.Throws<ArgumentException>(() => RiskParityOptimizer.EqualRiskContribution(new double[2, 3]));
            Assert.Throws<ArgumentException>(() => RiskParityOptimizer.EqualRiskContribution(new[,] { { 1.0, 0.5 }, { 0.1, 1.0 } }));   // asymmetric
            Assert.Throws<ArgumentException>(() => RiskParityOptimizer.EqualRiskContribution(new[,] { { 1.0, 0.0 }, { 0.0, 0.0 } }));   // zero variance
            Assert.Throws<ArgumentException>(() => RiskParityOptimizer.EqualRiskContribution(new[,] { { 1.0, double.NaN }, { double.NaN, 1.0 } }));
        }

        [Fact]
        public void IndefiniteCovarianceIsRejected()
        {
            // Correlation 2 is impossible: eigenvalues 3 and -1.
            var cov = new[,] { { 1.0, 2.0 }, { 2.0, 1.0 } };
            Assert.Throws<ArgumentException>(() => RiskParityOptimizer.EqualRiskContribution(cov));
        }

        [Fact]
        public void NaiveBaselinesMatchClosedForms()
        {
            var cov = new[,] { { 0.04, 0.01 }, { 0.01, 0.01 } };           // vols 0.2 and 0.1
            double[] iv = RiskBudgeting.InverseVolatility(cov);
            Assert.Equal(1.0 / 3.0, iv[0], 14);
            Assert.Equal(2.0 / 3.0, iv[1], 14);
            double[] ivar = RiskBudgeting.InverseVariance(cov);             // 25 : 100
            Assert.Equal(0.2, ivar[0], 14);
            Assert.Equal(0.8, ivar[1], 14);
            Assert.Throws<ArgumentException>(() => RiskBudgeting.InverseVolatility(new[] { 0.1, 0.0 }));
        }

        [Fact]
        public void ContributionsFollowTheClosedFormAndSumCorrectly()
        {
            var cov = new[,] { { 0.04, 0.006 }, { 0.006, 0.01 } };
            double[] w = { 0.3, 0.7 };
            // Sigma w = (0.012 + 0.0042, 0.0018 + 0.007) = (0.0162, 0.0088); variance = 0.00486 + 0.00616 = 0.01102.
            var rc = RiskBudgeting.Contributions(w, cov);
            double sigma = Math.Sqrt(0.01102);
            Assert.Equal(sigma, rc.Volatility, 14);
            Assert.Equal(0.0162 / sigma, rc.Marginal[0], 13);
            Assert.Equal(0.0088 / sigma, rc.Marginal[1], 13);
            Assert.Equal(0.3 * 0.0162 / sigma, rc.Absolute[0], 13);
            Assert.Equal(0.00486 / 0.01102, rc.Relative[0], 13);
            Assert.Equal(sigma, Sum(rc.Absolute), 14);
            Assert.Equal(1.0, Sum(rc.Relative), 14);
            Assert.Equal(sigma, RiskBudgeting.Volatility(w, cov), 14);
        }

        [Fact]
        public void ContributionsAgreeWithRiskParametricDecomposition()
        {
            double[,] cov = RandomCovariance(7, 71);
            double[] w = RiskParityOptimizer.EqualRiskContribution(cov).Weights;
            double[] positions = { 1e6, -2e5, 3e5, 4e5, 5e5, 6e5, -7e4 };      // long/short currency positions
            double z = InFine.QuantFinance.ClosedForm.NormalDistribution.InverseCdf(0.99);
            foreach (double[] p in new[] { w, positions })
            {
                var rc = RiskBudgeting.Contributions(p, cov);
                RiskDecomposition d = ParametricRisk.Decompose(p, cov, 0.99);
                for (int i = 0; i < p.Length; i++)
                {
                    Assert.Equal(d.MarginalVar[i] / z, rc.Marginal[i], 10);
                    Assert.Equal(d.ComponentVar[i] / z, rc.Absolute[i], 10);
                    Assert.Equal(d.ComponentVar[i] / d.Total.Var, rc.Relative[i], 12);
                }
            }
        }

        [Fact]
        public void ZeroPortfolioHasNoContributions()
        {
            var cov = new[,] { { 0.04, 0.0 }, { 0.0, 0.01 } };
            Assert.Throws<InvalidOperationException>(() => RiskBudgeting.Contributions(new[] { 0.0, 0.0 }, cov));
        }

        [Fact]
        public void VolatilityTargetScalingHitsTheTarget()
        {
            double[,] cov = RandomCovariance(9, 81);
            double[] w = RiskParityOptimizer.EqualRiskContribution(cov).Weights;
            double sigma = RiskBudgeting.Volatility(w, cov);
            double target = 0.1;
            double lev = RiskBudgeting.VolatilityTargetLeverage(w, cov, target);
            Assert.Equal(target / sigma, lev, 13);
            double[] scaled = RiskBudgeting.ScaleToVolatility(w, cov, target);
            Assert.Equal(target, RiskBudgeting.Volatility(scaled, cov), 13);
            Assert.Equal(lev, Sum(scaled), 12);
            // Risk contributions are unchanged by leverage.
            var a = RiskBudgeting.Contributions(w, cov).Relative;
            var c = RiskBudgeting.Contributions(scaled, cov).Relative;
            for (int i = 0; i < a.Length; i++) Assert.Equal(a[i], c[i], 13);
            // Cap.
            Assert.Equal(1.0, RiskBudgeting.VolatilityTargetLeverage(w, cov, 100.0, maxLeverage: 1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => RiskBudgeting.VolatilityTargetLeverage(w, cov, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => RiskBudgeting.VolatilityTargetLeverage(w, cov, double.NaN));
        }

        static double Sum(double[] x)
        {
            double s = 0.0;
            foreach (double v in x) s += v;
            return s;
        }
    }
}
