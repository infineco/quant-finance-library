using System;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Models.Diffusion;
using InFine.QuantFinance.Numerics;
using InFine.QuantFinance.Random;
using InFine.QuantFinance.Random.DotNet;
using InFine.QuantFinance.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace InFine.QuantFinance.Models.Tests
{
    /// <summary>
    /// Monte Carlo Hull-White (exact joint simulation of the state and its integral, <see cref="CompiledHullWhite"/>) against the
    /// analytic prices of <see cref="HullWhite"/>. Normals come from Philox, sub-stream (seed, path, stream). Each Monte Carlo
    /// estimate must be within 3 standard errors of its reference; the seed is fixed, so the outcome is deterministic.
    /// The plain facts drive the Hybridizer-free <see cref="CompiledHullWhite.SimulatePath"/>; the kernel classes
    /// (<see cref="HullWhiteModel{Brownian}"/>, <see cref="HullWhiteDiscounting"/>, <see cref="HullWhiteRate"/>) are checked
    /// against it bit for bit under <see cref="HybridizerFactAttribute"/>.
    /// </summary>
    public class HullWhiteSimulationTests
    {
        const double A = 0.05, Sigma = 0.012;
        const ulong Seed = 20251011UL;
        const int Paths = 40000;

        readonly ITestOutputHelper _output;

        public HullWhiteSimulationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        static CompiledDiscountCurve ShapedCurve()
        {
            // Humped zero curve under the monotone cubic, with a flat forward of 2.4% beyond 4Y.
            double[] t = { 0.25, 0.5, 1.0, 2.0, 4.0 };
            double[] z = { 0.029, 0.027, 0.0245, 0.023, 0.0232 };
            var slopes = new double[t.Length];
            InterpolationKernels.MonotoneCubicSlopes(t, z, t.Length, slopes);
            return new CompiledDiscountCurve(DiscountCurveKernels.HermiteZeroRate, t, z, slopes, -z[4] * t[4], 0.024);
        }

        static double[] QuarterlyGrid(double horizon)
        {
            int n = (int)Math.Round(horizon * 4.0);
            var times = new double[n + 1];
            for (int k = 0; k <= n; ++k) times[k] = 0.25 * k;
            return times;
        }

        /// <summary>Paths of a model: state and log discount factor, [path][pillar], plus extra normals per path if asked.</summary>
        sealed class Simulation
        {
            public double[][] State;
            public double[][] LogDiscount;
            public double[][] Extra;

            public Simulation(CompiledHullWhite model, int paths, int extraNormals = 0)
            {
                int steps = model.StepCount;
                State = new double[paths][];
                LogDiscount = new double[paths][];
                Extra = new double[paths][];
                int count = 2 * steps + extraNormals;
                var draws = new double[count + (count & 1)];
                var z1 = new double[steps];
                var z2 = new double[steps];
                for (int p = 0; p < paths; ++p)
                {
                    new PhiloxGenerator(Seed, (uint)p, 0).GenerateGaussians(GaussianTransformEnum.BoxMuller, 0.0, 1.0, draws.Length, draws);
                    Array.Copy(draws, 0, z1, 0, steps);
                    Array.Copy(draws, steps, z2, 0, steps);
                    State[p] = new double[steps + 1];
                    LogDiscount[p] = new double[steps + 1];
                    model.SimulatePath(z1, z2, State[p], LogDiscount[p]);
                    Extra[p] = new double[extraNormals];
                    Array.Copy(draws, 2 * steps, Extra[p], 0, extraNormals);
                }
            }
        }

        static (double Mean, double StandardError) Estimate(int paths, Func<int, double> sample)
        {
            double sum = 0.0, sumSq = 0.0;
            for (int p = 0; p < paths; ++p)
            {
                double v = sample(p);
                sum += v;
                sumSq += v * v;
            }
            double mean = sum / paths;
            return (mean, Math.Sqrt(Math.Max(0.0, sumSq / paths - mean * mean) / (paths - 1.0)));
        }

        void AssertWithin3Se(double reference, (double Mean, double StandardError) mc, string what)
        {
            double z = (mc.Mean - reference) / mc.StandardError;
            _output.WriteLine($"{what}: Monte Carlo {mc.Mean:R} +/- {mc.StandardError:E2} vs {reference:R} ({z:F2} SE)");
            Assert.True(mc.StandardError > 0.0, $"{what}: zero standard error");
            Assert.True(Math.Abs(z) <= 3.0, $"{what}: Monte Carlo {mc.Mean} vs {reference}, {z:F2} standard errors");
        }

        [Fact]
        public void ExpectedDiscountFactorIsTheCurveOnFineAndSingleStepGrids()
        {
            var curve = ShapedCurve();
            var model = new CompiledHullWhite(A, Sigma, curve, QuarterlyGrid(10.0));
            var sim = new Simulation(model, Paths);
            foreach (double t in new[] { 1.0, 2.0, 3.0, 5.0, 10.0 })
            {
                int k = (int)Math.Round(4.0 * t);
                AssertWithin3Se(curve.DiscountFactor(t), Estimate(Paths, p => Math.Exp(sim.LogDiscount[p][k])), $"E[D(0, {t})]");
            }

            // The transition is exact, so one step to 7 years is as good as 28.
            var coarse = new CompiledHullWhite(A, Sigma, curve, new[] { 0.0, 7.0 });
            var oneStep = new Simulation(coarse, Paths);
            AssertWithin3Se(curve.DiscountFactor(7.0), Estimate(Paths, p => Math.Exp(oneStep.LogDiscount[p][1])), "E[D(0, 7)], one step");
        }

        [Fact]
        public void ZeroVolatilityDiscountsOnTheCurveOnEveryPath()
        {
            var curve = ShapedCurve();
            var model = new CompiledHullWhite(A, 0.0, curve, QuarterlyGrid(5.0));
            var sim = new Simulation(model, 50);
            for (int p = 0; p < 50; ++p)
                for (int k = 0; k <= model.StepCount; ++k)
                {
                    Assert.Equal(0.0, sim.State[p][k]);
                    Assert.Equal(Math.Log(curve.DiscountFactor(0.25 * k)), sim.LogDiscount[p][k], 1e-15);
                }
        }

        [Fact]
        public void ZeroBondOptionsMatchTheAnalyticPrice()
        {
            var curve = ShapedCurve();
            var analytic = new HullWhite(A, Sigma, curve);
            var model = new CompiledHullWhite(A, Sigma, curve, QuarterlyGrid(2.0));
            var sim = new Simulation(model, Paths);
            double expiry = 2.0, maturity = 5.0;
            int k = 8;
            double strike = curve.DiscountFactor(maturity) / curve.DiscountFactor(expiry);
            foreach (var type in new[] { OptionType.Call, OptionType.Put })
            {
                var mc = Estimate(Paths, p =>
                {
                    double bond = Math.Exp(model.LogZeroBond(k, sim.State[p][k], maturity));
                    return Math.Exp(sim.LogDiscount[p][k]) * Math.Max((int)type * (bond - strike), 0.0);
                });
                AssertWithin3Se(analytic.ZeroCouponBondOption(type, expiry, maturity, strike), mc, $"ZCB {type}");
            }
            // The conditional bond matches the analytic one given the same state.
            Assert.Equal(analytic.ZeroCouponBond(expiry, maturity, sim.State[7][k]), Math.Exp(model.LogZeroBond(k, sim.State[7][k], maturity)), 1e-15);
        }

        [Fact]
        public void CapletDiscountedPathwiseToItsPaymentDateMatchesTheAnalyticPrice()
        {
            var curve = ShapedCurve();
            var analytic = new HullWhite(A, Sigma, curve);
            var model = new CompiledHullWhite(A, Sigma, curve, QuarterlyGrid(3.0));
            var sim = new Simulation(model, Paths);
            double start = 2.5, end = 3.0, tau = 0.5;
            int ks = 10, ke = 12;
            double forward = (curve.DiscountFactor(start) / curve.DiscountFactor(end) - 1.0) / tau;
            foreach (double strike in new[] { forward - 0.005, forward, forward + 0.005 })
            {
                var mc = Estimate(Paths, p =>
                {
                    double libor = (Math.Exp(-model.LogZeroBond(ks, sim.State[p][ks], end)) - 1.0) / tau;
                    return Math.Exp(sim.LogDiscount[p][ke]) * tau * Math.Max(libor - strike, 0.0);
                });
                AssertWithin3Se(analytic.Caplet(start, end, tau, strike), mc, $"caplet K {strike:F4}");
            }
        }

        [Fact]
        public void SwaptionMatchesJamshidian()
        {
            var curve = ShapedCurve();
            var analytic = new HullWhite(A, Sigma, curve);
            var model = new CompiledHullWhite(A, Sigma, curve, QuarterlyGrid(1.0));
            var sim = new Simulation(model, Paths);
            double expiry = 1.0;
            int k = 4;
            double[] times = { 2.0, 3.0, 4.0, 5.0, 6.0 }, accruals = { 1.0, 1.0, 1.0, 1.0, 1.0 };
            double atm = analytic.ForwardSwapRate(expiry, times, accruals);
            foreach (double strike in new[] { atm - 0.005, atm, atm + 0.005 })
                foreach (var type in new[] { SwaptionType.Payer, SwaptionType.Receiver })
                {
                    var mc = Estimate(Paths, p =>
                    {
                        double x = sim.State[p][k], fixedLeg = 0.0;
                        for (int i = 0; i < times.Length; ++i)
                            fixedLeg += strike * accruals[i] * Math.Exp(model.LogZeroBond(k, x, times[i]));
                        double payerSwap = 1.0 - Math.Exp(model.LogZeroBond(k, x, times[^1])) - fixedLeg;
                        return Math.Exp(sim.LogDiscount[p][k]) * Math.Max((int)type * payerSwap, 0.0);
                    });
                    AssertWithin3Se(analytic.Swaption(type, expiry, times, accruals, strike), mc, $"{type} K {strike:F4}");
                }
        }

        [Fact]
        public void EquityDiffusedWithTheStepRatesHasTheStochasticRateBlackPrice()
        {
            // ln S_{k+1} = ln S_k + (r_k - s^2/2) dt + s sqrt(dt) z with r_k = ln(D_k / D_{k+1}) / dt (HullWhiteRate on a step),
            // z independent of the rates: D(0, T) S_T = S_0 exp(s W_T - s^2 T / 2), and ln D(0, T) ~ N(ln P - V/2, V) independent
            // of it, so E[D (S_T - K)^+] is Black-76 on F = S_0 / P(0, T) with total variance s^2 T + V(T).
            const double s0 = 100.0, strike = 105.0, equityVol = 0.2, maturity = 3.0;
            var curve = ShapedCurve();
            var model = new CompiledHullWhite(A, Sigma, curve, QuarterlyGrid(maturity));
            int steps = model.StepCount;
            var sim = new Simulation(model, Paths, steps);
            var mc = Estimate(Paths, p =>
            {
                double lnS = Math.Log(s0);
                for (int k = 0; k < steps; ++k)
                {
                    double dt = model.StepLength(k);
                    double rate = (sim.LogDiscount[p][k] - sim.LogDiscount[p][k + 1]) / dt;
                    lnS += (rate - 0.5 * equityVol * equityVol) * dt + equityVol * Math.Sqrt(dt) * sim.Extra[p][k];
                }
                return Math.Exp(sim.LogDiscount[p][steps]) * Math.Max(Math.Exp(lnS) - strike, 0.0);
            });
            double p0 = curve.DiscountFactor(maturity);
            double stdDev = Math.Sqrt(equityVol * equityVol * maturity + HullWhiteKernels.IntegralVariance(A, Sigma, maturity));
            AssertWithin3Se(p0 * Black76Kernels.NormalisedPrice(1, s0 / p0, strike, stdDev), mc, "equity call, stochastic rates");
        }

        [Fact]
        public void CompiledModelValidatesItsGrid()
        {
            var curve = ShapedCurve();
            Assert.Throws<ArgumentException>(() => new CompiledHullWhite(A, Sigma, curve, new[] { 0.5, 1.0 }));
            Assert.Throws<ArgumentException>(() => new CompiledHullWhite(A, Sigma, curve, new[] { 0.0, 1.0, 1.0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CompiledHullWhite(0.0, Sigma, curve, new[] { 0.0, 1.0 }));
            var model = new CompiledHullWhite(A, Sigma, curve, new[] { 0.0, 0.5, 1.0 });
            Assert.Equal(0, model.PillarAtOrBefore(0.0));
            Assert.Equal(0, model.PillarAtOrBefore(0.49));
            Assert.Equal(1, model.PillarAtOrBefore(0.5));
            Assert.Equal(2, model.PillarAtOrBefore(1.0));
            Assert.Equal(2, model.PillarAtOrBefore(3.0));
        }

        [HybridizerFact]
        public void KernelClassesMatchTheHelperAndReadThePaths()
        {
            const int paths = 2000;
            var curve = ShapedCurve();
            var compiled = new CompiledHullWhite(A, Sigma, curve, QuarterlyGrid(3.0));
            int steps = compiled.StepCount;
            var reference = new Simulation(compiled, paths);

            var z1 = new ArrayBrownian(paths, steps);
            var z2 = new ArrayBrownian(paths, steps);
            var draws = new double[2 * steps];
            for (int p = 0; p < paths; ++p)
            {
                new PhiloxGenerator(Seed, (uint)p, 0).GenerateGaussians(GaussianTransformEnum.BoxMuller, 0.0, 1.0, draws.Length, draws);
                for (int k = 0; k < steps; ++k)
                {
                    z1[p, k] = draws[k];
                    z2[p, k] = draws[steps + k];
                }
            }
            var state = new ArrayDoubleSpot(paths, steps + 1);
            var logDiscount = new ArrayDoubleSpot(paths, steps + 1);
            new HullWhiteModel<ArrayBrownian>(compiled, state, logDiscount, z1, z2).Simulate(0, paths);
            var discounting = new HullWhiteDiscounting(compiled, state, logDiscount);
            var rate = new HullWhiteRate(compiled, state, logDiscount);

            for (int p = 0; p < paths; ++p)
            {
                for (int k = 0; k <= steps; ++k)
                {
                    Assert.Equal(reference.State[p][k], state[p, k]);
                    Assert.Equal(reference.LogDiscount[p][k], logDiscount[p, k]);
                    Assert.Equal(Math.Exp(reference.LogDiscount[p][k]), discounting[p, k]);
                }
                for (int k = 0; k < steps; ++k)
                    Assert.Equal((reference.LogDiscount[p][k] - reference.LogDiscount[p][k + 1]) / 0.25, rate[p, k], 1e-12);
                // Off-grid: conditional on the last pillar (t = 1.1 -> pillar 4 at t = 1.0).
                double x = reference.State[p][4];
                Assert.Equal(Math.Exp(reference.LogDiscount[p][4]) * Math.Exp(compiled.LogZeroBond(4, x, 1.1)), discounting[p, 1.1], 1e-15);
                Assert.Equal(x * Math.Exp(-A * 0.1) + compiled.Phi(1.1), rate[p, 1.1], 1e-15);
                Assert.Equal(x + compiled.PhiAt(4), rate[p, 1.0], 1e-15);
            }
        }
    }
}
