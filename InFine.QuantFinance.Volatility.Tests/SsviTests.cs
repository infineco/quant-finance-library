using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using static InFine.QuantFinance.Volatility.Tests.TestHelpers;

namespace InFine.QuantFinance.Volatility.Tests
{
    /// <summary>
    /// SSVI (Gatheral-Jacquier 2014, section 4): slice formulas against the raw-SVI mapping of their Lemma 3.1 and
    /// finite differences, the sufficient no-arbitrage conditions against grid checks, and the global calibration.
    /// </summary>
    public class SsviTests
    {
        static readonly SsviParameters ArbitrageFree = new SsviParameters(-0.6, 1.2, 0.45); // eta (1 + |rho|) = 1.92
        static readonly double[] Thetas = { 0.002, 0.01, 0.04, 0.1, 0.25, 0.5, 1.0, 2.0, 5.0 };

        [Fact]
        public void SliceIsTheNaturalSviOfLemma31()
        {
            // SSVI = natural SVI (Delta, mu, rho, omega, zeta) = (0, 0, rho, theta, phi); Lemma 3.1 maps it to raw
            // a = theta/2 (1 - rho^2), b = theta phi / 2, m = -rho / phi, sigma = sqrt(1 - rho^2) / phi.
            foreach (double theta in Thetas)
            {
                var slice = ArbitrageFree.Slice(theta);
                double rho = ArbitrageFree.Rho, phi = ArbitrageFree.Phi(theta);
                var raw = new SviRawParameters(theta / 2 * (1 - rho * rho), theta * phi / 2, rho, -rho / phi, Math.Sqrt(1 - rho * rho) / phi);
                for (double k = -2; k <= 2; k += 0.25)
                {
                    Close(raw.TotalVariance(k), slice.TotalVariance(k), 1e-13, 1e-16, $"w({k}, {theta})");
                    Close(raw.TotalVarianceDerivative(k), slice.TotalVarianceDerivative(k), 1e-12, 1e-15, $"w'({k}, {theta})");
                    Close(raw.TotalVarianceSecondDerivative(k), slice.TotalVarianceSecondDerivative(k), 1e-11, 1e-15, $"w''({k}, {theta})");
                }
                Close(theta, slice.TotalVariance(0.0), 1e-15, 0, "ATM total variance is theta");
                var (first, second) = CentralDifferences(slice.TotalVariance, 0.3, 1e-4);
                // Truncation O(h^2 w''') grows with phi(theta), large at small theta.
                Close(first, slice.TotalVarianceDerivative(0.3), 1e-6, 1e-8, "w' vs FD");
                Close(second, slice.TotalVarianceSecondDerivative(0.3), 1e-5, 1e-8, "w'' vs FD");
            }
        }

        [Fact]
        public void PowerLawConditionImpliesTheoremConditions()
        {
            // Remark 4.4 / eq. (4.5): gamma in (0, 1/2] and eta (1 + |rho|) <= 2 imply Theorem 4.2's two butterfly
            // conditions and Theorem 4.1's calendar condition for every theta; checked on a log grid of theta.
            var cases = new[] { ArbitrageFree, new SsviParameters(0.3, 1.5, 0.5), new SsviParameters(-0.9, 1.05, 0.1) };
            foreach (var p in cases)
            {
                Assert.True(p.SatisfiesPowerLawNoArbitrageCondition, p.ToString());
                for (double logTheta = -10; logTheta <= 6; logTheta += 0.1)
                {
                    double theta = Math.Exp(logTheta);
                    Assert.True(p.SatisfiesButterflyConditions(theta), $"{p} butterfly at theta={theta}");
                    Assert.True(p.SatisfiesCalendarCondition(theta), $"{p} calendar at theta={theta}");
                }
            }
            Assert.False(new SsviParameters(-0.5, 3.0, 0.5).SatisfiesPowerLawNoArbitrageCondition); // eta (1 + |rho|) = 4.5
            Assert.False(new SsviParameters(-0.5, 1.0, 0.7).SatisfiesPowerLawNoArbitrageCondition); // gamma > 1/2
        }

        [Fact]
        public void ArbitrageFreeParametersPassGridChecks()
        {
            // Butterfly g(k) >= 0 for every slice and w non-decreasing in theta (hence in t) at every k.
            var grid = ArbitrageChecks.Grid(-4, 4, 801);
            ITotalVarianceSlice? previous = null;
            foreach (double theta in Thetas)
            {
                var slice = ArbitrageFree.Slice(theta);
                var butterfly = ArbitrageChecks.CheckButterfly(slice, grid);
                Assert.True(butterfly.IsArbitrageFree, $"theta={theta}: min g {butterfly.MinimumDensity} at {butterfly.ArgMinimum}");
                if (previous != null)
                    Assert.True(ArbitrageChecks.CheckCalendar(previous, slice, grid).IsArbitrageFree, $"calendar up to theta={theta}");
                previous = slice;
            }
        }

        [Fact]
        public void ViolatingTheNecessaryWingConditionIsDetected()
        {
            // Lemma 4.2: theta phi (1 + |rho|) > 4 forces butterfly arbitrage, with
            // g -> (16 - theta^2 phi^2 (1 - rho)^2) / 64 < 0 as k -> -infinity for rho < 0. Here eta = 3, rho = -0.5,
            // gamma = 1/2, theta = 10: theta phi (1 + |rho|) = 4.29, so the limit is (16 - 18.4) / 64 = -0.037.
            var p = new SsviParameters(-0.5, 3.0, 0.5);
            const double theta = 10.0;
            Assert.False(p.SatisfiesButterflyConditions(theta));
            var result = ArbitrageChecks.CheckButterfly(p.Slice(theta), ArbitrageChecks.Grid(-200, 200, 4001));
            Assert.False(result.IsArbitrageFree);
            Assert.True(result.Violations.All(k => k < 0), "violations in the left wing");
            double phi = p.Phi(theta), limit = (16 - Math.Pow(theta * phi * 1.5, 2)) / 64;
            Close(limit, ArbitrageChecks.ButterflyDensity(p.Slice(theta), -1e6), 0, 1e-4, "g(-infinity)");
        }

        static List<SmileQuotes> Generate(SsviParameters p, double[] maturities, double[] thetas, Random? noise = null, double amplitude = 0)
        {
            var k = Enumerable.Range(0, 15).Select(i => -0.7 + 0.1 * i).ToArray(); // -0.7 .. 0.7
            var slices = new List<SmileQuotes>();
            for (int s = 0; s < maturities.Length; s++)
            {
                double forward = 100 * Math.Exp(0.02 * maturities[s]);
                var slice = p.Slice(thetas[s]);
                var strikes = k.Select(x => forward * Math.Exp(x * Math.Sqrt(maturities[s]))).ToArray();
                var vols = strikes.Select(K => Math.Sqrt(slice.TotalVariance(Math.Log(K / forward)) / maturities[s])
                                               + (noise == null ? 0 : amplitude * (2 * noise.NextDouble() - 1))).ToArray();
                slices.Add(new SmileQuotes(maturities[s], forward, strikes, vols));
            }
            return slices;
        }

        [Fact]
        public void GlobalCalibrationRecoversExactParameters()
        {
            var truth = ArbitrageFree;
            double[] maturities = { 0.25, 0.5, 1.0, 2.0 }, thetas = { 0.012, 0.022, 0.04, 0.075 };
            var quotes = Generate(truth, maturities, thetas);
            quotes.Reverse(); // any order
            var result = SsviCalibrator.Calibrate(quotes);

            // Exact data: total-variance residuals at rounding level, parameters back to ~1e-7.
            Assert.True(result.Converged, result.Status.ToString());
            Assert.True(result.RootMeanSquareTotalVarianceError < 1e-12, $"rmse {result.RootMeanSquareTotalVarianceError:E3}");
            Close(truth.Rho, result.Parameters.Rho, 0, 1e-7, "rho");
            Close(truth.Eta, result.Parameters.Eta, 0, 1e-7, "eta");
            Close(truth.Gamma, result.Parameters.Gamma, 0, 1e-7, "gamma");
            for (int s = 0; s < maturities.Length; s++)
            {
                Close(maturities[s], result.Maturities[s], 0, 0, "maturity order");
                Close(thetas[s], result.AtTheMoneyTotalVariances[s], 1e-8, 0, $"theta {s}");
            }
            Assert.True(result.CreateSurface(new ConstantForwardCurve(100)).SatisfiesSufficientNoArbitrageConditions);
        }

        [Fact]
        public void NoisyCalibrationStaysArbitrageFree()
        {
            // 30 bp uniform vol noise; the fit is constrained to the sufficient region, so the surface it returns
            // passes the closed-form conditions and the grid checks, and fits at least as well as the truth.
            var truth = new SsviParameters(-0.7, 1.1, 0.4);
            double[] maturities = { 0.1, 0.5, 1.0, 3.0 }, thetas = { 0.004, 0.02, 0.038, 0.11 };
            var quotes = Generate(truth, maturities, thetas, new Random(7), 0.003);
            var result = SsviCalibrator.Calibrate(quotes, new SsviCalibrationOptions { Weighting = SmileWeighting.AsGiven });
            Assert.True(result.Converged, result.Status.ToString());

            double truthCost = 0;
            for (int s = 0; s < quotes.Count; s++)
                for (int i = 0; i < quotes[s].Count; i++)
                {
                    double e = truth.TotalVariance(quotes[s].LogMoneyness(i), thetas[s]) - quotes[s].TotalVariance(i);
                    truthCost += 0.5 * e * e;
                }
            Assert.True(result.WeightedCost <= truthCost * (1 + 1e-9), $"fit {result.WeightedCost:E6} vs truth {truthCost:E6}");
            Assert.True(result.MaxAbsoluteVolatilityError < 2 * 0.003, $"max vol error {result.MaxAbsoluteVolatilityError:E3}");
            Close(truth.Rho, result.Parameters.Rho, 0, 0.1, "rho");

            var surface = result.CreateSurface(new ConstantForwardCurve(100));
            Assert.True(surface.SatisfiesSufficientNoArbitrageConditions, result.Parameters.ToString());
            var grid = ArbitrageChecks.Grid(-3, 3, 601);
            double[] times = { 0.05, 0.1, 0.3, 0.5, 0.8, 1.0, 2.0, 3.0, 5.0 };
            for (int i = 0; i < times.Length; i++)
            {
                Assert.True(ArbitrageChecks.CheckButterfly(surface.Slice(times[i]), grid).IsArbitrageFree, $"butterfly at t={times[i]}");
                if (i > 0) Assert.True(ArbitrageChecks.CheckCalendar(surface.Slice(times[i - 1]), surface.Slice(times[i]), grid).IsArbitrageFree);
            }
        }

        [Fact]
        public void UnconstrainedCalibrationCanLeaveTheSufficientRegion()
        {
            // Data generated outside eq. (4.5) (eta (1 + |rho|) = 2.4): the unconstrained fit recovers it exactly,
            // the constrained fit cannot and stays inside.
            var truth = new SsviParameters(-0.5, 1.6, 0.4);
            double[] maturities = { 0.5, 1.0 }, thetas = { 0.02, 0.04 };
            var quotes = Generate(truth, maturities, thetas);
            var free = SsviCalibrator.Calibrate(quotes, new SsviCalibrationOptions { EnforceNoArbitrage = false });
            Close(truth.Eta, free.Parameters.Eta, 0, 1e-6, "eta (free)");
            Assert.False(free.Parameters.SatisfiesPowerLawNoArbitrageCondition);
            var constrained = SsviCalibrator.Calibrate(quotes);
            Assert.True(constrained.Parameters.SatisfiesPowerLawNoArbitrageCondition, constrained.Parameters.ToString());
            Assert.True(constrained.WeightedCost > free.WeightedCost);
        }
    }
}
