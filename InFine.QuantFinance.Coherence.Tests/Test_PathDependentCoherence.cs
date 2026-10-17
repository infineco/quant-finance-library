using System;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Payoffs.Concepts;
using InFine.QuantFinance.Payoffs.Equity;
using InFine.QuantFinance.Scheduling;
using InFine.QuantFinance.TestSupport;
using Xunit;

namespace InFine.QuantFinance.Coherence.Tests
{
    /// <summary>
    /// Asian, barrier and lookback options on the full Monte Carlo chain (Philox normals, Black-Scholes-Merton diffusion,
    /// payoff kernel): per-path premiums are bit-identical on every backend, and the means agree with the closed forms
    /// of the same contracts within Monte Carlo error.
    /// </summary>
    public class Test_PathDependentCoherence
    {
        private const int Paths = Market.Paths;
        private const int Steps = Market.BlackScholesSteps;
        private const double Strike = 100.0;

        private delegate ISimulatable OptionFactory(
            LogBasedSpot<ArrayDoubleSpot> spot, ConstantRateDiscounting discounting, PrecalculatedSchedule schedule, ArrayPremium premium);

        private static double[] Premiums(ExecutorBackend backend, ArrayDoubleSpot logSpot, OptionFactory create)
        {
            var schedule = new PrecalculatedSchedule(Market.Dates(Steps));
            var premium = Allocation.Prime(new ArrayPremium(Paths));
            var spot = new LogBasedSpot<ArrayDoubleSpot>(logSpot);
            backend.Simulate(create(spot, new ConstantRateDiscounting(Market.Rate, schedule), schedule, premium), 0, Paths);
            var premiums = new double[Paths];
            for (int p = 0; p < Paths; ++p)
                premiums[p] = premium[p];
            return premiums;
        }

        private static OptionFactory Asian(bool geometric) => (spot, discounting, schedule, premium) =>
            new AsianOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                spot, discounting, schedule, premium, Indices(1, Steps), Steps, Strike, 1, geometric, false);

        private static OptionFactory Barrier(int mode) => (spot, discounting, schedule, premium) =>
            new BarrierOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                spot, discounting, schedule, premium, Indices(0, Steps), Steps, Strike, 1, false, false, 90.0, 0.0, Market.Volatility, mode);

        private static OptionFactory Lookback() => (spot, discounting, schedule, premium) =>
            new LookbackOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                spot, discounting, schedule, premium, Indices(1, Steps), Steps, 0.0, 1, true, 0.0, LookbackMath.ContinuityShift(Market.Volatility, Market.Maturity / Steps));

        private static int[] Indices(int from, int to)
        {
            var r = new int[to - from + 1];
            for (int i = 0; i < r.Length; ++i) r[i] = from + i;
            return r;
        }

        private static void MeanAndError(double[] values, out double mean, out double se)
        {
            double s = 0, ss = 0;
            foreach (double v in values) { s += v; ss += v * v; }
            mean = s / values.Length;
            se = Math.Sqrt((ss / values.Length - mean * mean) / (values.Length - 1));
        }

        [HybridizerTheory]
        [MemberData(nameof(ExecutorBackends.ManagedNames), MemberType = typeof(ExecutorBackends))]
        public void PremiumPerPath_IsBitIdenticalAcrossBackends(string backendName)
        {
            ExecutorBackend backend = ExecutorBackends.Get(backendName);
            ArrayDoubleSpot reference = Workloads.DiffuseBlackScholes(ExecutorBackends.Reference, Paths, Steps);
            ArrayDoubleSpot actual = Workloads.DiffuseBlackScholes(backend, Paths, Steps);
            foreach (var create in new[] { Asian(false), Asian(true), Barrier(BarrierMonitoring.Discrete), Barrier(BarrierMonitoring.BridgeProbability), Lookback() })
                Matrices.AssertBitIdentical(Premiums(ExecutorBackends.Reference, reference, create), Premiums(backend, actual, create), backend.Name);
        }

        [HybridizerFact]
        public void MonteCarloMeans_AgreeWithTheClosedForms()
        {
            ExecutorBackend backend = ExecutorBackends.Reference;
            ArrayDoubleSpot logSpot = Workloads.DiffuseBlackScholes(backend, Paths, Steps);
            double mean, se;

            // geometric Asian, 12 monthly fixings: Kemna-Vorst (no dividend in this model)
            MeanAndError(Premiums(backend, logSpot, Asian(true)), out mean, out se);
            double kv = AsianKernels.GeometricPriceEquallySpaced(1, Market.Spot, Strike, Market.Maturity, Steps, Market.Rate, 0.0, Market.Volatility);
            Assert.True(Math.Abs(mean - kv) < 4 * se, $"geometric Asian: MC {mean} +- {se}, Kemna-Vorst {kv}");

            // down-and-out call, bridge-weighted: continuous barrier closed form, exact on any grid
            MeanAndError(Premiums(backend, logSpot, Barrier(BarrierMonitoring.BridgeProbability)), out mean, out se);
            double barrier = BarrierKernels.Price(1, false, false, Market.Spot, Strike, 90.0, 0.0, Market.Maturity, Market.Rate, 0.0, Market.Volatility);
            Assert.True(Math.Abs(mean - barrier) < 4 * se, $"down-and-out call: MC {mean} +- {se}, closed form {barrier}");

            // floating-strike lookback call with the continuity shift: continuous closed form (12 dates: O(dt) residual bias)
            MeanAndError(Premiums(backend, logSpot, Lookback()), out mean, out se);
            double lookback = LookbackKernels.FloatingStrikePrice(1, Market.Spot, Market.Spot, Market.Maturity, Market.Rate, 0.0, Market.Volatility);
            Assert.True(Math.Abs(mean - lookback) < 4 * se + 0.05 * lookback, $"floating lookback call: MC {mean} +- {se}, closed form {lookback}");
        }
    }
}
