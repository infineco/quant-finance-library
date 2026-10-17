using System;
using System.Collections.Generic;
using InFine.QuantFinance.Risk;
using Xunit;
using static InFine.QuantFinance.Portfolio.Tests.QpTestSupport;

namespace InFine.QuantFinance.Portfolio.Tests
{
    /// <summary>Mean-variance optimisation against closed forms, brute force and KKT conditions.</summary>
    public class MeanVarianceOptimizerTests
    {
        // Five assets, one-factor correlation (positive definite by construction).
        static readonly double[] Mu = { 0.06, 0.08, 0.11, 0.07, 0.13 };
        static readonly double[] Vols = { 0.15, 0.20, 0.25, 0.18, 0.30 };
        static readonly double[] Betas = { 0.5, 0.6, 0.7, 0.4, 0.8 };
        static readonly double[,] Sigma = BuildSigma();

        static double[,] BuildSigma()
        {
            int n = Vols.Length;
            var s = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int k = 0; k < n; k++)
                    s[i, k] = Vols[i] * Vols[k] * (i == k ? 1.0 : Betas[i] * Betas[k]);
            return s;
        }

        // Merton (1972) constants of the unconstrained frontier: A = 1'S^-1 1, B = 1'S^-1 mu, C = mu'S^-1 mu.
        static (double[] s1, double[] smu, double a, double b, double c, double d) Merton()
        {
            double[] s1 = SolveOrThrow(Sigma, Ones(5));
            double[] smu = SolveOrThrow(Sigma, Mu);
            double a = Dot(Ones(5), s1), b = Dot(Ones(5), smu), c = Dot(Mu, smu);
            return (s1, smu, a, b, c, a * c - b * b);
        }

        static MeanVarianceOptimizer Unconstrained() => new(Mu, Sigma, PortfolioConstraints.FullyInvested);

        static double Sum(double[] w)
        {
            double s = 0.0;
            foreach (double v in w) s += v;
            return s;
        }

        // ---- unconstrained closed forms ------------------------------------------------------------------------

        [Fact]
        public void UnconstrainedMinimumVarianceIsSigmaInverseOneNormalised()
        {
            var (s1, _, a, _, _, _) = Merton();
            MeanVarianceResult r = Unconstrained().MinimumVariance();
            Assert.Equal(MeanVarianceStatus.Optimal, r.Status);
            Close(Array.ConvertAll(s1, v => v / a), r.Weights, 1e-10, "w");
            Assert.Equal(1.0 / a, r.Variance, 14);
            Assert.Equal(double.PositiveInfinity, r.RiskAversion);
        }

        [Fact]
        public void UnconstrainedTangencyIsSigmaInverseExcessReturnNormalised()
        {
            double rf = 0.03;
            double[] excess = Array.ConvertAll(Mu, m => m - rf);
            double[] se = SolveOrThrow(Sigma, excess);
            double norm = Sum(se);
            Assert.True(norm > 0.0);
            MeanVarianceResult r = Unconstrained().MaximumSharpeRatio(rf);
            Assert.Equal(MeanVarianceStatus.Optimal, r.Status);
            Close(Array.ConvertAll(se, v => v / norm), r.Weights, 1e-10, "w");
            // Maximum Sharpe ratio sqrt(e' S^-1 e).
            Assert.Equal(Math.Sqrt(Dot(excess, se)), r.SharpeRatio(rf), 12);
        }

        [Theory]
        [InlineData(0.07)]
        [InlineData(0.10)]
        [InlineData(0.15)]
        public void UnconstrainedTargetReturnMatchesMertonsTwoFundFormula(double target)
        {
            var (s1, smu, a, b, c, d) = Merton();
            var expected = new double[5];
            for (int i = 0; i < 5; i++) expected[i] = ((c - target * b) * s1[i] + (target * a - b) * smu[i]) / d;
            MeanVarianceResult r = Unconstrained().MinimumVarianceForTargetReturn(target);
            Assert.Equal(MeanVarianceStatus.Optimal, r.Status);
            Close(expected, r.Weights, 1e-10, "w");
            Assert.Equal((a * target * target - 2 * b * target + c) / d, r.Variance, 13);
            Assert.Equal(target, r.ExpectedReturn, 14);
        }

        [Theory]
        [InlineData(1.0)]
        [InlineData(4.0)]
        [InlineData(25.0)]
        public void UnconstrainedUtilityMatchesClosedForm(double lambda)
        {
            // w = S^-1 (mu - gamma 1) / lambda with gamma = (B - lambda) / A from the budget.
            var (s1, smu, a, b, _, _) = Merton();
            double gamma = (b - lambda) / a;
            var expected = new double[5];
            for (int i = 0; i < 5; i++) expected[i] = (smu[i] - gamma * s1[i]) / lambda;
            MeanVarianceResult r = Unconstrained().MaximumUtility(lambda);
            Assert.Equal(MeanVarianceStatus.Optimal, r.Status);
            Close(expected, r.Weights, 1e-10, "w");
            // Multipliers are those of the stated utility: lambda S w - mu + y 1 = 0, so the budget multiplier is gamma.
            Assert.Equal(gamma, r.Solution!.EqualityMultipliers[0], 12);
        }

        [Theory]
        [InlineData(0.02)]
        [InlineData(0.05)]
        public void UnconstrainedReturnForTargetVarianceIsOnTheUpperFrontier(double targetVariance)
        {
            var (_, _, a, b, _, d) = Merton();
            double expectedReturn = (b + Math.Sqrt(d * (a * targetVariance - 1.0))) / a;
            MeanVarianceResult r = Unconstrained().MaximumReturnForTargetVariance(targetVariance);
            Assert.Equal(MeanVarianceStatus.Optimal, r.Status);
            Assert.Equal(targetVariance, r.Variance, 13);
            Assert.Equal(expectedReturn, r.ExpectedReturn, 12);
            Close(Unconstrained().MinimumVarianceForTargetReturn(expectedReturn).Weights, r.Weights, 1e-10, "w");
        }

        [Fact]
        public void UnconstrainedMaximumReturnIsUnbounded()
        {
            MeanVarianceResult r = Unconstrained().MaximumReturn();
            Assert.Equal(MeanVarianceStatus.Unbounded, r.Status);
            Assert.Equal(MeanVarianceStatus.Unbounded, Unconstrained().EfficientFrontier(5).Status);
        }

        // ---- constrained: brute force and KKT ------------------------------------------------------------------

        static PortfolioConstraints Textbook() =>
            PortfolioConstraints.LongOnly(5, 0.35).With(LinearConstraint.Group(5, new[] { 2, 4 }, 0.0, 0.4, "growth"));

        // The same feasible set written out as a QP for the independent references.
        static QuadraticProgram TextbookProgram(double[,] q, double[] c) => new(q, c)
        {
            EqualityMatrix = new double[,] { { 1, 1, 1, 1, 1 } },
            EqualityVector = new double[] { 1 },
            InequalityMatrix = new double[,] { { 0, 0, 1, 0, 1 } },
            InequalityVector = new double[] { 0.4 },
            LowerBounds = new double[5],
            UpperBounds = new double[] { 0.35, 0.35, 0.35, 0.35, 0.35 },
        };

        [Fact]
        public void LongOnlyMinimumVarianceWithBindingBoundsMatchesBruteForce()
        {
            MeanVarianceResult r = new MeanVarianceOptimizer(Mu, Sigma, Textbook()).MinimumVariance();
            Assert.Equal(MeanVarianceStatus.Optimal, r.Status);
            QuadraticProgram p = TextbookProgram(Sigma, new double[5]);
            double[] reference = BruteForce(p, out _);
            Close(reference, r.Weights, 1e-10, "w");
            // The cap binds: the unconstrained minimum-variance portfolio puts more than 35% in asset 0.
            var (s1, _, a, _, _, _) = Merton();
            Assert.True(s1[0] / a > 0.35);
            Assert.Contains(0, r.Solution!.ActiveUpperBounds);
            Residuals(p, r.Solution).AssertBelow(1e-9);
        }

        [Theory]
        [InlineData(2.0)]
        [InlineData(6.0)]
        public void LongOnlyUtilityWithGroupCapMatchesBruteForceAndKkt(double lambda)
        {
            MeanVarianceResult r = new MeanVarianceOptimizer(Mu, Sigma, Textbook()).MaximumUtility(lambda);
            Assert.Equal(MeanVarianceStatus.Optimal, r.Status);
            var q = new double[5, 5];
            for (int i = 0; i < 5; i++)
                for (int k = 0; k < 5; k++) q[i, k] = lambda * Sigma[i, k];
            QuadraticProgram p = TextbookProgram(q, Array.ConvertAll(Mu, m => -m));
            double[] reference = BruteForce(p, out _);
            Close(reference, r.Weights, 1e-10, "w");
            Residuals(p, r.Solution!).AssertBelow(1e-9);
            if (lambda < 3.0) Assert.Equal(new[] { 0 }, r.Solution!.ActiveInequalities); // the group cap binds
        }

        [Theory]
        [InlineData(0.075)]
        [InlineData(0.09)]
        public void LongOnlyTargetReturnMatchesBruteForce(double target)
        {
            MeanVarianceResult r = new MeanVarianceOptimizer(Mu, Sigma, Textbook()).MinimumVarianceForTargetReturn(target);
            Assert.Equal(MeanVarianceStatus.Optimal, r.Status);
            QuadraticProgram p = TextbookProgram(Sigma, new double[5]) with
            {
                EqualityMatrix = new double[,] { { 1, 1, 1, 1, 1 }, { Mu[0], Mu[1], Mu[2], Mu[3], Mu[4] } },
                EqualityVector = new double[] { 1, target },
            };
            Close(BruteForce(p, out _), r.Weights, 1e-10, "w");
            Residuals(p, r.Solution!).AssertBelow(1e-9);
        }

        [Fact]
        public void LongOnlyMaximumSharpeMatchesSupportEnumeration()
        {
            // Long only, no cap: the tangency portfolio is S_SS^-1 e_S normalised on its support S, and the best
            // support among those giving non-negative weights is the optimum.
            double rf = 0.04;
            double[] mu = { 0.05, 0.09, 0.07, 0.12, 0.045 };
            double[,] sigma = RandomSpd(5, new System.Random(3), 0.01);
            var opt = new MeanVarianceOptimizer(mu, sigma, PortfolioConstraints.LongOnly(5));
            MeanVarianceResult r = opt.MaximumSharpeRatio(rf);
            Assert.Equal(MeanVarianceStatus.Optimal, r.Status);

            double bestSharpe = double.NegativeInfinity;
            double[]? best = null;
            for (int mask = 1; mask < 32; mask++)
            {
                var idx = new List<int>();
                for (int i = 0; i < 5; i++) if ((mask & (1 << i)) != 0) idx.Add(i);
                var s = new double[idx.Count, idx.Count];
                var e = new double[idx.Count];
                for (int i = 0; i < idx.Count; i++)
                {
                    e[i] = mu[idx[i]] - rf;
                    for (int k = 0; k < idx.Count; k++) s[i, k] = sigma[idx[i], idx[k]];
                }
                double[] y = SolveOrThrow(s, e);
                double total = Sum(y);
                if (!(total > 0.0)) continue;
                var w = new double[5];
                bool ok = true;
                for (int i = 0; i < idx.Count; i++) { w[idx[i]] = y[i] / total; ok &= w[idx[i]] >= 0.0; }
                if (!ok) continue;
                double sharpe = (Dot(mu, w) - rf) / Math.Sqrt(Dot(w, Multiply(sigma, w)));
                if (sharpe > bestSharpe) { bestSharpe = sharpe; best = w; }
            }
            Close(best!, r.Weights, 1e-10, "w");
            Assert.Equal(bestSharpe, r.SharpeRatio(rf), 12);
            Assert.Contains(r.Weights, w => w == 0.0); // some asset is excluded
        }

        [Fact]
        public void CappedMaximumSharpeBeatsEveryFrontierPoint()
        {
            double rf = 0.02;
            var opt = new MeanVarianceOptimizer(Mu, Sigma, Textbook());
            MeanVarianceResult r = opt.MaximumSharpeRatio(rf);
            Assert.Equal(MeanVarianceStatus.Optimal, r.Status);
            Assert.Equal(1.0, Sum(r.Weights), 14);
            Assert.True(r.Weights[2] + r.Weights[4] <= 0.4 + 1e-14);
            foreach (double w in r.Weights) Assert.InRange(w, -1e-14, 0.35 + 1e-14);
            EfficientFrontier f = opt.EfficientFrontier(201);
            double sr = r.SharpeRatio(rf);
            foreach (MeanVarianceResult point in f.Points) Assert.True(point.SharpeRatio(rf) <= sr + 1e-12);
            // ... and is on the frontier itself.
            MeanVarianceResult same = opt.MinimumVarianceForTargetReturn(r.ExpectedReturn);
            Assert.Equal(same.Variance, r.Variance, 13);
        }

        [Fact]
        public void LargeLongOnlyProblemWithSectorCapsSatisfiesKkt()
        {
            int n = 150, sectors = 10;
            var rng = new System.Random(2024);
            double[,] sigma = RandomSpd(n, rng, 0.002);
            var mu = new double[n];
            for (int i = 0; i < n; i++) mu[i] = 0.02 + 0.1 * rng.NextDouble();
            PortfolioConstraints constraints = PortfolioConstraints.LongOnly(n, 0.04);
            var g = new double[sectors, n];
            var h = new double[sectors];
            for (int s = 0; s < sectors; s++)
            {
                var members = new List<int>();
                for (int i = s; i < n; i += sectors) { members.Add(i); g[s, i] = 1.0; }
                h[s] = 0.12;
                constraints = constraints.With(LinearConstraint.Group(n, members, double.NegativeInfinity, 0.12));
            }
            var opt = new MeanVarianceOptimizer(mu, sigma, constraints);
            foreach (double lambda in new[] { 1.0, 10.0 })
            {
                MeanVarianceResult r = opt.MaximumUtility(lambda);
                Assert.Equal(MeanVarianceStatus.Optimal, r.Status);
                var q = new double[n, n];
                for (int i = 0; i < n; i++)
                    for (int k = 0; k < n; k++) q[i, k] = lambda * sigma[i, k];
                var p = new QuadraticProgram(q, Array.ConvertAll(mu, m => -m))
                {
                    EqualityMatrix = ToRow(Ones(n)),
                    EqualityVector = new double[] { 1 },
                    InequalityMatrix = g,
                    InequalityVector = h,
                    LowerBounds = new double[n],
                    UpperBounds = Array.ConvertAll(new double[n], _ => 0.04),
                };
                Residuals(p, r.Solution!).AssertBelow(1e-9);
                Assert.True(r.Solution!.ActiveUpperBounds.Length > 0 && r.Solution.ActiveLowerBounds.Length > 0 && r.Solution.ActiveInequalities.Length > 0);
            }
        }

        // ---- frontier ------------------------------------------------------------------------------------------

        [Fact]
        public void LongOnlyFrontierIsIncreasingAndConvex()
        {
            var opt = new MeanVarianceOptimizer(Mu, Sigma, PortfolioConstraints.LongOnly(5));
            EfficientFrontier f = opt.EfficientFrontier(41);
            Assert.Equal(MeanVarianceStatus.Optimal, f.Status);
            Assert.Equal(41, f.Points.Count);
            // Ends: the minimum-variance portfolio and 100% in the highest-return asset.
            Close(opt.MinimumVariance().Weights, f.Points[0].Weights, 1e-14, "first");
            Close(new[] { 0.0, 0, 0, 0, 1 }, f.Points[40].Weights, 1e-12, "last");
            double step = f.Returns[1] - f.Returns[0];
            for (int k = 1; k < 41; k++)
            {
                Assert.Equal(step, f.Returns[k] - f.Returns[k - 1], 12);
                Assert.True(f.Points[k].Variance > f.Points[k - 1].Variance, $"variance not increasing at {k}");
                Assert.True(f.Volatilities[k] > f.Volatilities[k - 1]);
            }
            for (int k = 1; k < 40; k++)
            {
                double second = f.Points[k + 1].Variance - 2 * f.Points[k].Variance + f.Points[k - 1].Variance;
                Assert.True(second >= -1e-13, $"variance not convex at {k}: {second:E2}");
                double secondVol = f.Volatilities[k + 1] - 2 * f.Volatilities[k] + f.Volatilities[k - 1];
                Assert.True(secondVol >= -1e-13, $"volatility not convex at {k}: {secondVol:E2}");
            }
        }

        [Fact]
        public void LongOnlyMaximumReturnWithTiesPicksMinimumVariance()
        {
            // Assets 2 and 4 share the highest return: the upper end of the frontier is their minimum-variance mix.
            double[] mu = { 0.06, 0.08, 0.13, 0.07, 0.13 };
            MeanVarianceResult r = new MeanVarianceOptimizer(mu, Sigma, PortfolioConstraints.LongOnly(5)).MaximumReturn();
            Assert.Equal(MeanVarianceStatus.Optimal, r.Status);
            double s22 = Sigma[2, 2], s44 = Sigma[4, 4], s24 = Sigma[2, 4];
            double w2 = (s44 - s24) / (s22 + s44 - 2 * s24);
            Close(new[] { 0.0, 0, w2, 0, 1 - w2 }, r.Weights, 1e-9, "w");
            Assert.Equal(0.13, r.ExpectedReturn, 14);
        }

        [Fact]
        public void TargetVarianceUnderConstraintsLiesOnTheFrontier()
        {
            var opt = new MeanVarianceOptimizer(Mu, Sigma, Textbook());
            double target = 0.025;
            MeanVarianceResult r = opt.MaximumReturnForTargetVariance(target);
            Assert.Equal(MeanVarianceStatus.Optimal, r.Status);
            Assert.Equal(target, r.Variance, 14);
            MeanVarianceResult onFrontier = opt.MinimumVarianceForTargetReturn(r.ExpectedReturn);
            Assert.Equal(target, onFrontier.Variance, 13);
            Close(onFrontier.Weights, r.Weights, 1e-8, "w");
            // It is the utility optimum at the risk aversion found by the search.
            Close(opt.MaximumUtility(r.RiskAversion).Weights, r.Weights, 1e-10, "utility");
        }

        [Fact]
        public void RiskTargetsOutsideTheFrontierAreReported()
        {
            var opt = new MeanVarianceOptimizer(Mu, Sigma, PortfolioConstraints.LongOnly(5));
            MeanVarianceResult low = opt.MaximumReturnForTargetVariance(1e-4);
            Assert.Equal(MeanVarianceStatus.TargetUnattainable, low.Status);
            Close(opt.MinimumVariance().Weights, low.Weights, 1e-14, "low");
            MeanVarianceResult high = opt.MaximumReturnForTargetVariance(1.0);
            Assert.Equal(MeanVarianceStatus.TargetUnattainable, high.Status);
            Close(new[] { 0.0, 0, 0, 0, 1 }, high.Weights, 1e-10, "high");
        }

        // ---- benchmark-relative --------------------------------------------------------------------------------

        static readonly double[] Benchmark = { 0.3, 0.25, 0.15, 0.2, 0.1 };

        [Fact]
        public void MinimumTrackingErrorOfAFeasibleBenchmarkIsTheBenchmark()
        {
            MeanVarianceResult r = new MeanVarianceOptimizer(Mu, Sigma).MinimumTrackingError(Benchmark);
            Assert.Equal(MeanVarianceStatus.Optimal, r.Status);
            Close(Benchmark, r.Weights, 1e-14, "w");
            Assert.True(r.TrackingError < 1e-8);
        }

        [Theory]
        [InlineData(0.01)]
        [InlineData(0.03)]
        public void TrackingErrorTargetIsHitByTheRiskAversionSearch(double te)
        {
            var constraints = PortfolioConstraints.ActiveBounds(Benchmark, 0.1, 0.1);
            var opt = new MeanVarianceOptimizer(Mu, Sigma, constraints);
            MeanVarianceResult r = opt.MaximumActiveReturnForTrackingError(Benchmark, te);
            Assert.Equal(MeanVarianceStatus.Optimal, r.Status);
            Assert.Equal(te, r.TrackingError, 13);
            Assert.True(r.RiskAversion > 0.0 && double.IsFinite(r.RiskAversion));
            // Lagrangian duality: the utility optimum at lambda* with TE(lambda*) = target maximises the active
            // return under the tracking-error budget. Cross-check the search against the utility itself...
            MeanVarianceResult u = opt.MaximumUtility(r.RiskAversion, Benchmark);
            Close(u.Weights, r.Weights, 1e-10, "w");
            // ...and against neighbours: less risk aversion buys more active return with more tracking error.
            MeanVarianceResult bolder = opt.MaximumUtility(0.9 * r.RiskAversion, Benchmark);
            Assert.True(bolder.TrackingError > te && bolder.ActiveReturn >= r.ActiveReturn - 1e-15);
            foreach (double w in r.Weights) Assert.True(w >= -1e-14);
            for (int i = 0; i < 5; i++) Assert.InRange(r.Weights[i] - Benchmark[i], -0.1 - 1e-14, 0.1 + 1e-14);
        }

        // ---- degenerate and infeasible inputs ------------------------------------------------------------------

        [Fact]
        public void InfeasibleConstraintsAreReportedCleanly()
        {
            var opt = new MeanVarianceOptimizer(Mu, Sigma, PortfolioConstraints.LongOnly(5, 0.15));
            Assert.Equal(MeanVarianceStatus.Infeasible, opt.MinimumVariance().Status);
            Assert.Equal(MeanVarianceStatus.Infeasible, opt.MaximumUtility(3.0).Status);
            Assert.Equal(MeanVarianceStatus.Infeasible, opt.MaximumSharpeRatio(0.01).Status);
            EfficientFrontier f = opt.EfficientFrontier(10);
            Assert.Equal(MeanVarianceStatus.Infeasible, f.Status);
            Assert.Empty(f.Points);

            var longOnly = new MeanVarianceOptimizer(Mu, Sigma);
            MeanVarianceResult tooHigh = longOnly.MinimumVarianceForTargetReturn(0.2);
            Assert.Equal(MeanVarianceStatus.Infeasible, tooHigh.Status);
            Assert.NotNull(tooHigh.Message);
            // Every asset below the risk-free rate: no positive Sharpe ratio exists.
            Assert.Equal(MeanVarianceStatus.Infeasible, longOnly.MaximumSharpeRatio(0.2).Status);

            var contradictory = PortfolioConstraints.LongOnly(5).With(LinearConstraint.Group(5, new[] { 0, 1 }, 0.6, 0.5));
            Assert.Equal(MeanVarianceStatus.Infeasible, new MeanVarianceOptimizer(Mu, Sigma, contradictory).MinimumVariance().Status);
        }

        [Fact]
        public void SingularSampleCovarianceWithMoreAssetsThanDatesIsRegularised()
        {
            int n = 12, t = 8;
            var rng = new System.Random(42);
            var returns = new double[t, n];
            for (int d = 0; d < t; d++)
                for (int i = 0; i < n; i++) returns[d, i] = 0.02 * (rng.NextDouble() - 0.5);
            double[,] sample = Covariance.Sample(returns);
            double[] mu = Covariance.Mean(returns);
            var opt = new MeanVarianceOptimizer(mu, sample, PortfolioConstraints.LongOnly(n, 0.3));
            MeanVarianceResult r = opt.MinimumVariance();
            Assert.Equal(MeanVarianceStatus.Optimal, r.Status);
            Assert.True(r.Solution!.Regularization > 0.0);
            Assert.Equal(1.0, Sum(r.Weights), 13);
            foreach (double w in r.Weights) Assert.InRange(w, -1e-14, 0.3 + 1e-14);
            // KKT of the ridged programme.
            var p = new QuadraticProgram(sample, new double[n])
            {
                EqualityMatrix = ToRow(Ones(n)),
                EqualityVector = new double[] { 1 },
                LowerBounds = new double[n],
                UpperBounds = Array.ConvertAll(new double[n], _ => 0.3),
            };
            Residuals(p, r.Solution).AssertBelow(1e-9);

            var refused = new MeanVarianceOptimizer(mu, sample, PortfolioConstraints.LongOnly(n), new QuadraticProgramOptions { RegularizationFloor = 0.0 });
            Assert.Equal(MeanVarianceStatus.NotConvex, refused.MinimumVariance().Status);
        }

        [Fact]
        public void EqualExpectedReturnsGiveASinglePointFrontier()
        {
            double[] mu = { 0.05, 0.05, 0.05, 0.05, 0.05 };
            EfficientFrontier f = new MeanVarianceOptimizer(mu, Sigma).EfficientFrontier(10);
            Assert.Equal(MeanVarianceStatus.Optimal, f.Status);
            Assert.Single(f.Points);
        }

        [Fact]
        public void InvalidArgumentsThrow()
        {
            Assert.Throws<ArgumentException>(() => new MeanVarianceOptimizer(new double[4], Sigma));
            Assert.Throws<ArgumentException>(() => new MeanVarianceOptimizer(new[] { 0.1, double.NaN, 0, 0, 0 }, Sigma));
            var asym = (double[,])Sigma.Clone();
            asym[0, 1] += 0.01;
            Assert.Throws<ArgumentException>(() => new MeanVarianceOptimizer(Mu, asym));
            Assert.Throws<ArgumentException>(() => new MeanVarianceOptimizer(Mu, Sigma, PortfolioConstraints.LongOnly(4)));
            var opt = new MeanVarianceOptimizer(Mu, Sigma);
            Assert.Throws<ArgumentOutOfRangeException>(() => opt.MaximumUtility(0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => opt.EfficientFrontier(1));
            Assert.Throws<ArgumentException>(() => opt.MinimumTrackingError(new double[3]));
            var noBudget = new MeanVarianceOptimizer(Mu, Sigma, PortfolioConstraints.LongOnly(5) with { Budget = null });
            Assert.Throws<InvalidOperationException>(() => noBudget.MaximumSharpeRatio(0.0));
        }

        static double[,] ToRow(double[] v)
        {
            var a = new double[1, v.Length];
            for (int i = 0; i < v.Length; i++) a[0, i] = v[i];
            return a;
        }
    }
}
