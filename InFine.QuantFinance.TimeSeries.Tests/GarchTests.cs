using System;
using InFine.QuantFinance.TimeSeries;
using Xunit;

namespace InFine.QuantFinance.TimeSeries.Tests
{
    /// <summary>
    /// GARCH(1,1): likelihood against an independent Python implementation, maximum-likelihood recovery of known parameters on long
    /// simulated series (within 3 numerical-Hessian standard errors), standard-error calibration across replications, forecasts, simulation and
    /// behaviour at the boundaries of the constraint set.
    /// </summary>
    public class GarchTests
    {
        static readonly GarchParameters Truth = new(Mu: 3e-4, Omega: 1e-6, Alpha: 0.08, Beta: 0.90);

        static void Close(double expected, double actual, double rel, double abs = 0.0) =>
            Assert.True(Math.Abs(expected - actual) <= abs + rel * Math.Abs(expected), $"expected {expected:R}, got {actual:R}");

        // ---- likelihood against the Python reference ----------------------------------------------------------

        [Fact]
        public void LogLikelihoodMatchesTheIndependentPythonImplementation()
        {
            foreach (var c in GarchReferenceData.Cases)
            {
                bool student = c.Nu > 0.0;
                var p = new GarchParameters(c.Mu, c.Omega, c.Alpha, c.Beta, student ? c.Nu : double.PositiveInfinity);
                var dist = student ? InnovationDistribution.StudentT : InnovationDistribution.Normal;
                var init = c.Backcast ? VarianceInitialisation.Backcast : VarianceInitialisation.SampleVariance;
                double ll = Garch11.LogLikelihood(GarchReferenceData.Returns, p, dist, init);
                Close(c.LogLikelihood, ll, 1e-12);
                if (!student)
                {
                    var v = Garch11.ConditionalVariances(GarchReferenceData.Returns, p, init);
                    Assert.Equal(GarchReferenceData.Returns.Length + 1, v.Length);
                    Close(c.NextVariance, v[^1], 1e-12);
                }
            }
        }

        [Fact]
        public void VarianceRecursionStartsAtTheChosenInitialValue()
        {
            var r = GarchReferenceData.Returns;
            var p = new GarchParameters(4e-4, 2e-6, 0.09, 0.89);
            double mean = 0.0;
            foreach (double x in r) mean += (x - 4e-4) * (x - 4e-4);
            var v = Garch11.ConditionalVariances(r, p);
            Close(mean / r.Length, v[0], 1e-14);
            Close(p.Omega + p.Alpha * Math.Pow(r[0] - p.Mu, 2) + p.Beta * v[0], v[1], 1e-14);

            var back = Garch11.ConditionalVariances(r, p, VarianceInitialisation.Backcast);
            double w = 1.0, sum = 0.0, wsum = 0.0;
            for (int i = 0; i < 75; i++) { sum += w * Math.Pow(r[i] - p.Mu, 2); wsum += w; w *= 0.94; }
            Close(sum / wsum, back[0], 1e-14);
        }

        [Fact]
        public void LikelihoodRejectsParametersOutsideTheConstraintSet()
        {
            var r = GarchReferenceData.Returns;
            Assert.Throws<ArgumentException>(() => Garch11.LogLikelihood(r, new GarchParameters(0, 0.0, 0.1, 0.8)));
            Assert.Throws<ArgumentException>(() => Garch11.LogLikelihood(r, new GarchParameters(0, -1e-6, 0.1, 0.8)));
            Assert.Throws<ArgumentException>(() => Garch11.LogLikelihood(r, new GarchParameters(0, 1e-6, -0.01, 0.8)));
            Assert.Throws<ArgumentException>(() => Garch11.LogLikelihood(r, new GarchParameters(0, 1e-6, 0.1, -0.01)));
            Assert.Throws<ArgumentException>(() => Garch11.LogLikelihood(r, new GarchParameters(0, 1e-6, 0.1, 0.9)));
            Assert.Throws<ArgumentException>(() => Garch11.LogLikelihood(r, new GarchParameters(0, 1e-6, 0.5, 0.6)));
            Assert.Throws<ArgumentException>(() => Garch11.LogLikelihood(r, new GarchParameters(0, 1e-6, 0.1, 0.8, 2.0), InnovationDistribution.StudentT));
            // The boundary values themselves are legal: alpha = 0, beta = 0, and alpha + beta arbitrarily close to 1.
            Assert.True(double.IsFinite(Garch11.LogLikelihood(r, new GarchParameters(0, 1e-5, 0.0, 0.8))));
            Assert.True(double.IsFinite(Garch11.LogLikelihood(r, new GarchParameters(0, 1e-5, 0.1, 0.0))));
            Assert.True(double.IsFinite(Garch11.LogLikelihood(r, new GarchParameters(0, 1e-9, 0.1, 1.0 - 0.1 - 1e-9))));
        }

        [Fact]
        public void StudentTLikelihoodTendsToTheNormalForLargeNu()
        {
            var r = GarchReferenceData.Returns;
            var normal = Garch11.LogLikelihood(r, new GarchParameters(4e-4, 2e-6, 0.09, 0.89));
            var big = Garch11.LogLikelihood(r, new GarchParameters(4e-4, 2e-6, 0.09, 0.89, 1e5), InnovationDistribution.StudentT);
            Close(normal, big, 1e-5);
        }

        // ---- simulation -----------------------------------------------------------------------------------------

        [Fact]
        public void SimulationIsDeterministicAndSeedDependent()
        {
            var a = Garch11.Simulate(Truth, 500, 42);
            var b = Garch11.Simulate(Truth, 500, 42);
            var c = Garch11.Simulate(Truth, 500, 43);
            Assert.Equal(a.Returns, b.Returns);
            Assert.Equal(a.ConditionalVariance, b.ConditionalVariance);
            Assert.NotEqual(a.Returns, c.Returns);
            Assert.Equal(500, a.Returns.Length);
            Assert.Equal(500, a.ConditionalVariance.Length);
        }

        [Fact]
        public void SimulationFollowsTheVarianceRecursion()
        {
            var s = Garch11.Simulate(Truth, 1000, 7);
            for (int t = 1; t < 1000; t++)
            {
                double eps = s.Returns[t - 1] - Truth.Mu;
                Close(Truth.Omega + Truth.Alpha * eps * eps + Truth.Beta * s.ConditionalVariance[t - 1], s.ConditionalVariance[t], 1e-12);
            }
        }

        [Theory]
        [InlineData(InnovationDistribution.Normal)]
        [InlineData(InnovationDistribution.StudentT)]
        public void SimulatedSampleVarianceMatchesTheUnconditionalVariance(InnovationDistribution distribution)
        {
            // Unconditional variance 1e-6 / 0.02 = 5e-5. Standard error of the sample variance is several percent (GARCH clustering), so 15%.
            var p = new GarchParameters(0.0, 1e-6, 0.05, 0.93, distribution == InnovationDistribution.StudentT ? 7.0 : double.PositiveInfinity);
            var s = Garch11.Simulate(p, 200_000, 11, distribution);
            double mean = 0.0, ss = 0.0;
            foreach (double x in s.Returns) mean += x;
            mean /= s.Returns.Length;
            foreach (double x in s.Returns) ss += (x - mean) * (x - mean);
            Close(p.UnconditionalVariance, ss / s.Returns.Length, 0.15);
        }

        [Fact]
        public void StudentTInnovationsHaveUnitVarianceAndHeavyTails()
        {
            // alpha = beta = 0: i.i.d. returns mu + sigma z with z standardised t(8): variance 1, excess kurtosis 6 / (nu - 4) = 1.5.
            var p = new GarchParameters(0.0, 1.0, 0.0, 0.0, 8.0);
            var s = Garch11.Simulate(p, 400_000, 5, InnovationDistribution.StudentT, burnIn: 0);
            double m2 = 0.0, m4 = 0.0;
            foreach (double x in s.Returns) { m2 += x * x; m4 += x * x * x * x; }
            m2 /= s.Returns.Length; m4 /= s.Returns.Length;
            Close(1.0, m2, 0.02);
            Assert.InRange(m4 / (m2 * m2) - 3.0, 1.0, 2.2);
        }

        // ---- estimation: parameter recovery -------------------------------------------------------------------

        static void AssertWithinStandardErrors(GarchParameters truth, GarchFit fit, double k, bool checkMu = true, bool checkNu = false)
        {
            Assert.True(fit.HessianPositiveDefinite);
            var e = fit.Parameters; var se = fit.StandardErrors;
            if (checkMu) Assert.True(Math.Abs(e.Mu - truth.Mu) <= k * se.Mu, $"mu {e.Mu:R} vs {truth.Mu:R} (se {se.Mu:R})");
            Assert.True(Math.Abs(e.Omega - truth.Omega) <= k * se.Omega, $"omega {e.Omega:R} vs {truth.Omega:R} (se {se.Omega:R})");
            Assert.True(Math.Abs(e.Alpha - truth.Alpha) <= k * se.Alpha, $"alpha {e.Alpha:R} vs {truth.Alpha:R} (se {se.Alpha:R})");
            Assert.True(Math.Abs(e.Beta - truth.Beta) <= k * se.Beta, $"beta {e.Beta:R} vs {truth.Beta:R} (se {se.Beta:R})");
            if (checkNu) Assert.True(Math.Abs(e.Nu - truth.Nu) <= k * se.Nu, $"nu {e.Nu:R} vs {truth.Nu:R} (se {se.Nu:R})");
        }

        [Fact]
        public void NormalGarchParametersAreRecoveredOnALongSeries()
        {
            var sim = Garch11.Simulate(Truth, 20_000, 2024);
            var fit = Garch11.Fit(sim.Returns);
            Assert.True(fit.Converged);
            AssertWithinStandardErrors(Truth, fit, 3.0);
            // The standard errors themselves are plausible: alpha and beta are pinned to a few thousandths at T = 20 000.
            Assert.InRange(fit.StandardErrors.Alpha, 0.002, 0.012);
            Assert.InRange(fit.StandardErrors.Beta, 0.003, 0.02);
            Assert.InRange(fit.Parameters.Persistence, 0.96, 0.995);
            Assert.False(fit.AtBoundary);
            Assert.Equal(4, fit.EstimatedParameterCount);
            Assert.Equal(new[] { "mu", "omega", "alpha", "beta" }, fit.ParameterNames);
        }

        [Fact]
        public void FitReachesALocalMaximumAtLeastAsHighAsTheTruth()
        {
            var sim = Garch11.Simulate(Truth, 5_000, 99);
            var fit = Garch11.Fit(sim.Returns);
            double atTruth = Garch11.LogLikelihood(sim.Returns, Truth);
            Assert.True(fit.LogLikelihood >= atTruth - 1e-6, $"fit {fit.LogLikelihood:R} < truth {atTruth:R}");
            Close(fit.LogLikelihood, Garch11.LogLikelihood(sim.Returns, fit.Parameters), 1e-12);

            // First-order condition: no coordinate step of 0.5% improves the likelihood.
            var p = fit.Parameters;
            foreach (double f in new[] { 0.995, 1.005 })
            {
                Assert.True(Garch11.LogLikelihood(sim.Returns, p with { Omega = p.Omega * f }) <= fit.LogLikelihood + 1e-9);
                Assert.True(Garch11.LogLikelihood(sim.Returns, p with { Alpha = p.Alpha * f }) <= fit.LogLikelihood + 1e-9);
                Assert.True(Garch11.LogLikelihood(sim.Returns, p with { Beta = p.Beta * f }) <= fit.LogLikelihood + 1e-9);
                Assert.True(Garch11.LogLikelihood(sim.Returns, p with { Mu = p.Mu + (f - 1.0) * Math.Sqrt(p.UnconditionalVariance) }) <= fit.LogLikelihood + 1e-9);
            }
        }

        [Fact]
        public void StudentTParametersAreRecoveredOnALongSeries()
        {
            var truth = new GarchParameters(3e-4, 1e-6, 0.08, 0.90, 6.0);
            var sim = Garch11.Simulate(truth, 20_000, 77, InnovationDistribution.StudentT);
            var fit = Garch11.Fit(sim.Returns, new GarchOptions { Distribution = InnovationDistribution.StudentT });
            Assert.True(fit.Converged);
            AssertWithinStandardErrors(truth, fit, 3.0, checkNu: true);
            Assert.InRange(fit.Parameters.Nu, 5.0, 7.5);
            Assert.Equal(5, fit.EstimatedParameterCount);

            // A t fit beats the normal quasi-likelihood on t data by a wide margin of log-likelihood.
            var normalFit = Garch11.Fit(sim.Returns);
            Assert.True(fit.LogLikelihood > normalFit.LogLikelihood + 50.0);
            Assert.True(fit.Aic < normalFit.Aic);
        }

        [Fact]
        public void VarianceTargetingTiesOmegaToTheSampleVariance()
        {
            var sim = Garch11.Simulate(Truth, 20_000, 2024);
            var fit = Garch11.Fit(sim.Returns, new GarchOptions { VarianceTargeting = true });
            Assert.True(fit.Converged);
            Assert.Equal(3, fit.EstimatedParameterCount);
            Assert.Equal(new[] { "mu", "alpha", "beta" }, fit.ParameterNames);
            double mean = 0.0;
            foreach (double x in sim.Returns) mean += x;
            mean /= sim.Returns.Length;
            double v = 0.0;
            foreach (double x in sim.Returns) v += (x - mean) * (x - mean);
            v /= sim.Returns.Length;
            Close((1.0 - fit.Parameters.Persistence) * v, fit.Parameters.Omega, 1e-9);
            Close(v, fit.Parameters.UnconditionalVariance, 1e-9);
            Assert.True(Math.Abs(fit.Parameters.Alpha - Truth.Alpha) <= 3.0 * fit.StandardErrors.Alpha);
            Assert.True(Math.Abs(fit.Parameters.Beta - Truth.Beta) <= 3.0 * fit.StandardErrors.Beta);
            Assert.True(fit.StandardErrors.Omega > 0.0);
        }

        [Fact]
        public void BackcastAndFixedMeanOptions()
        {
            var truth = new GarchParameters(0.0, 1e-6, 0.08, 0.90);
            var sim = Garch11.Simulate(truth, 10_000, 31);
            var a = Garch11.Fit(sim.Returns, new GarchOptions { EstimateMean = false });
            var b = Garch11.Fit(sim.Returns, new GarchOptions { EstimateMean = false, Initialisation = VarianceInitialisation.Backcast });
            Assert.Equal(0.0, a.Parameters.Mu);
            Assert.True(double.IsNaN(a.StandardErrors.Mu));
            Assert.Equal(3, a.EstimatedParameterCount);
            // The initial value only matters at the start of the sample.
            Close(a.Parameters.Alpha, b.Parameters.Alpha, 0.05);
            Close(a.Parameters.Beta, b.Parameters.Beta, 0.01);
            Assert.NotEqual(a.InitialVariance, b.InitialVariance);
        }

        [Fact]
        public void RobustStandardErrorsAreCloseToTheClassicalOnesForNormalData()
        {
            var sim = Garch11.Simulate(Truth, 20_000, 2024);
            var fit = Garch11.Fit(sim.Returns, new GarchOptions { RobustStandardErrors = true });
            var robust = fit.RobustStandardErrors;
            Assert.NotNull(robust);
            Close(fit.StandardErrors.Alpha, robust!.Value.Alpha, 0.3);
            Close(fit.StandardErrors.Beta, robust.Value.Beta, 0.3);
            Close(fit.StandardErrors.Mu, robust.Value.Mu, 0.3);
            Assert.True(Math.Abs(fit.Parameters.Alpha - Truth.Alpha) <= 3.0 * robust.Value.Alpha);
        }

        [Fact]
        public void StandardErrorsAreCalibratedAcrossReplications()
        {
            // 40 independent T = 4000 series: the spread of the estimates must match the reported standard errors, and about 95% of the
            // |z| should fall below 2 (tolerance for 40 draws: at least 30).
            const int replications = 40;
            var alpha = new double[replications];
            var beta = new double[replications];
            double meanSeAlpha = 0.0, meanSeBeta = 0.0;
            int coveredAlpha = 0, coveredBeta = 0;
            for (int i = 0; i < replications; i++)
            {
                var sim = Garch11.Simulate(Truth, 4_000, 1000UL + (ulong)i);
                var fit = Garch11.Fit(sim.Returns);
                Assert.True(fit.HessianPositiveDefinite && !fit.AtBoundary, $"replication {i}");
                alpha[i] = fit.Parameters.Alpha; beta[i] = fit.Parameters.Beta;
                meanSeAlpha += fit.StandardErrors.Alpha / replications;
                meanSeBeta += fit.StandardErrors.Beta / replications;
                if (Math.Abs(fit.Parameters.Alpha - Truth.Alpha) < 2.0 * fit.StandardErrors.Alpha) coveredAlpha++;
                if (Math.Abs(fit.Parameters.Beta - Truth.Beta) < 2.0 * fit.StandardErrors.Beta) coveredBeta++;
            }
            Assert.True(coveredAlpha >= 33, $"alpha coverage {coveredAlpha}/{replications}");
            Assert.True(coveredBeta >= 33, $"beta coverage {coveredBeta}/{replications}");
            // Monte Carlo ratio of empirical spread to mean standard error; sampling error of a standard deviation over 40 draws is about 11%.
            Assert.InRange(StdDev(alpha) / meanSeAlpha, 0.6, 1.5);
            Assert.InRange(StdDev(beta) / meanSeBeta, 0.6, 1.5);
        }

        static double StdDev(double[] x)
        {
            double m = 0.0;
            foreach (double v in x) m += v;
            m /= x.Length;
            double s = 0.0;
            foreach (double v in x) s += (v - m) * (v - m);
            return Math.Sqrt(s / (x.Length - 1));
        }

        // ---- constraints and boundaries -----------------------------------------------------------------------

        static void AssertFeasible(GarchParameters p)
        {
            Assert.True(p.Omega > 0.0 && double.IsFinite(p.Omega), $"omega {p.Omega:R}");
            Assert.True(p.Alpha >= 0.0 && p.Beta >= 0.0, $"alpha {p.Alpha:R}, beta {p.Beta:R}");
            Assert.True(p.Alpha + p.Beta < 1.0, $"alpha + beta = {p.Alpha + p.Beta:R}");
        }

        [Fact]
        public void IidDataGiveAFeasibleEstimateWithNoVolatilityClustering()
        {
            // The true model has alpha = 0 (and beta unidentified): the MLE sits at or near the boundary but must stay feasible and finite.
            var rng = new System.Random(5);
            var r = new double[3000];
            for (int i = 0; i < r.Length; i++)
            {
                double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
                r[i] = 0.01 * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            }
            var fit = Garch11.Fit(r);
            AssertFeasible(fit.Parameters);
            Assert.True(double.IsFinite(fit.LogLikelihood));
            Assert.True(fit.Parameters.Alpha < 0.05, $"alpha {fit.Parameters.Alpha}");
            // The long-run variance is still estimated sensibly.
            Close(1e-4, fit.Parameters.UnconditionalVariance, 0.25);
            if (fit.AtBoundary)
                Assert.True(double.IsNaN(fit.StandardErrors.Alpha) || fit.StandardErrors.Alpha > 0.0);
        }

        [Fact]
        public void PurelyArchDataPutsBetaOnTheLowerBoundary()
        {
            var truth = new GarchParameters(0.0, 1e-5, 0.4, 0.0);
            var sim = Garch11.Simulate(truth, 20_000, 3);
            var fit = Garch11.Fit(sim.Returns, new GarchOptions { EstimateMean = false });
            AssertFeasible(fit.Parameters);
            Assert.True(fit.Parameters.Beta < 0.1, $"beta {fit.Parameters.Beta}");
            Close(0.4, fit.Parameters.Alpha, 0.12);
        }

        [Fact]
        public void NearlyIntegratedDataStayInsideTheStationarityRegion()
        {
            var truth = new GarchParameters(0.0, 2e-7, 0.07, 0.929);
            var sim = Garch11.Simulate(truth, 20_000, 17);
            var fit = Garch11.Fit(sim.Returns);
            AssertFeasible(fit.Parameters);
            Assert.InRange(fit.Parameters.Persistence, 0.97, 1.0);
            Assert.True(double.IsFinite(fit.LogLikelihood));
            AssertFeasible(Truth);
            Assert.True(double.IsFinite(fit.NextVariance) && fit.NextVariance > 0.0);
        }

        [Fact]
        public void ExplosiveDataCannotLeaveTheFeasibleSet()
        {
            // Returns whose variance grows like a random walk: alpha + beta is pushed to its upper bound but never beyond.
            var r = new double[2000];
            var rng = new System.Random(9);
            double scale = 0.001;
            for (int i = 0; i < r.Length; i++)
            {
                scale *= 1.0 + 0.002;
                r[i] = scale * (rng.NextDouble() - 0.5) * 3.4641016151377544;
            }
            var fit = Garch11.Fit(r);
            AssertFeasible(fit.Parameters);
            Assert.True(double.IsFinite(fit.LogLikelihood));
            if (fit.AtBoundary) Assert.True(double.IsNaN(fit.StandardErrors.Alpha) || fit.Parameters.Persistence < 1.0);
        }

        [Fact]
        public void FitRejectsDegenerateInput()
        {
            Assert.Throws<ArgumentException>(() => Garch11.Fit(new double[100]));
            Assert.Throws<ArgumentException>(() => Garch11.Fit(new double[10]));
            Assert.Throws<ArgumentNullException>(() => Garch11.Fit(null!));
            var bad = new double[50];
            bad[3] = double.NaN;
            Assert.Throws<ArgumentException>(() => Garch11.Fit(bad));
        }

        // ---- forecasts ------------------------------------------------------------------------------------------

        [Fact]
        public void ForecastsFollowTheClosedFormAndConvergeToTheUnconditionalVariance()
        {
            var p = new GarchParameters(0.0, 2e-6, 0.08, 0.90);
            double v = p.UnconditionalVariance; // 2e-6 / 0.02 = 1e-4
            Close(1e-4, v, 1e-12);
            double next = 4e-4;
            var f = Garch11.ForecastVariance(p, next, 2000);
            Close(next, f[0], 1e-14);
            // h = 2 by direct expectation: E[sigma^2_{T+2}] = omega + (alpha + beta) sigma^2_{T+1}.
            Close(p.Omega + p.Persistence * next, f[1], 1e-14);
            Close(p.Omega + p.Persistence * f[1], f[2], 1e-14);
            // Closed form at h = 10.
            Close(v + Math.Pow(0.98, 9) * (next - v), f[9], 1e-13);
            // Geometric convergence to the unconditional variance, from above and from below.
            Close(v, f[^1], 1e-10);
            for (int h = 1; h < f.Length; h++) Assert.True(f[h] <= f[h - 1]);
            var below = Garch11.ForecastVariance(p, 2e-5, 2000);
            for (int h = 1; h < below.Length; h++) Assert.True(below[h] >= below[h - 1]);
            Close(v, below[^1], 1e-10);
        }

        [Fact]
        public void VolatilityTermStructureAveragesTheVarianceForecasts()
        {
            var p = new GarchParameters(0.0, 2e-6, 0.08, 0.90);
            double next = 4e-4;
            var f = Garch11.ForecastVariance(p, next, 60);
            var ts = Garch11.AnnualisedVolatilityTermStructure(p, next, 60, 252.0);
            Close(Math.Sqrt(252.0 * next), ts[0], 1e-14);
            double cumulative = 0.0;
            for (int h = 1; h <= 60; h++)
            {
                cumulative += f[h - 1];
                Close(Math.Sqrt(252.0 * cumulative / h), ts[h - 1], 1e-12);
            }
            // Long horizon: annualised long-run volatility sqrt(252 * 1e-4) = 15.87%; the average over H still carries the initial excess
            // (next - v) / (1 - p) / H = 7.5e-7 relative to v at H = 20 000, i.e. 0.4% in volatility.
            var longTs = Garch11.AnnualisedVolatilityTermStructure(p, next, 20_000, 252.0);
            Close(Math.Sqrt(252.0 * (1e-4 + (next - 1e-4) / 0.02 / 20_000)), longTs[^1], 1e-6);
            Close(Math.Sqrt(252.0 * p.UnconditionalVariance), longTs[^1], 5e-3);
            // High current variance: the term structure slopes down; low current variance: up.
            Assert.True(ts[59] < ts[0]);
            Assert.True(Garch11.AnnualisedVolatilityTermStructure(p, 2e-5, 60, 252.0)[59] > Math.Sqrt(252.0 * 2e-5));
        }

        [Fact]
        public void FitForecastsStartFromTheFilteredNextVariance()
        {
            var sim = Garch11.Simulate(Truth, 5_000, 123);
            var fit = Garch11.Fit(sim.Returns);
            double eps = sim.Returns[^1] - fit.Parameters.Mu;
            Close(fit.Parameters.Omega + fit.Parameters.Alpha * eps * eps + fit.Parameters.Beta * fit.ConditionalVariance[^1], fit.NextVariance, 1e-12);
            var f = fit.ForecastVariance(5);
            Close(fit.NextVariance, f[0], 1e-14);
            Assert.Equal(5, f.Length);
            Assert.Equal(sim.Returns.Length, fit.ConditionalVariance.Length);
            Assert.Equal(sim.Returns.Length, fit.StandardisedResiduals.Length);
            // Standardised residuals of a well-specified model have mean ~0 and variance ~1.
            double m = 0.0, s = 0.0;
            foreach (double z in fit.StandardisedResiduals) { m += z; s += z * z; }
            Assert.InRange(m / 5000.0, -0.05, 0.05);
            Assert.InRange(s / 5000.0, 0.93, 1.07);
            var ts = fit.AnnualisedVolatilityTermStructure(30, 252.0);
            Assert.Equal(30, ts.Length);
            Close(Math.Sqrt(252.0 * fit.NextVariance), ts[0], 1e-14);
        }

        [Fact]
        public void ForecastRejectsInvalidArguments()
        {
            var p = new GarchParameters(0.0, 2e-6, 0.08, 0.90);
            Assert.Throws<ArgumentOutOfRangeException>(() => Garch11.ForecastVariance(p, 1e-4, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Garch11.ForecastVariance(p, 0.0, 5));
            Assert.Throws<ArgumentOutOfRangeException>(() => Garch11.AnnualisedVolatilityTermStructure(p, 1e-4, 5, 0.0));
            Assert.Throws<ArgumentException>(() => Garch11.ForecastVariance(new GarchParameters(0, 1e-6, 0.5, 0.5), 1e-4, 5));
        }
    }
}
