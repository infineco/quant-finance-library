using System;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Models.Diffusion;
using InFine.QuantFinance.Payoffs.Concepts;
using InFine.QuantFinance.Payoffs.Equity;
using InFine.QuantFinance.Scheduling;
using InFine.QuantFinance.TestSupport;
using Xunit;

namespace InFine.QuantFinance.Coherence.Tests
{
    /// <summary>
    /// Multi-asset chain (Philox normals, correlated log-normal diffusion kernel, basket / worst-of / autocallable
    /// kernels): the diffused spots and per-path premiums are bit-identical on every backend, and the geometric basket
    /// mean agrees with its closed form.
    /// </summary>
    public class Test_MultiAssetCoherence
    {
        private const int Paths = Market.Paths;
        private const int Steps = 4;
        private const int Assets = 3;
        private static readonly double[] Spots = { 100.0, 80.0, 120.0 };
        private static readonly double[] Sigma = { 0.2, 0.3, 0.25 };
        private static readonly double[] Dividend = { 0.01, 0.0, 0.02 };
        private static readonly double[,] Correlation = { { 1.0, 0.5, 0.3 }, { 0.5, 1.0, 0.6 }, { 0.3, 0.6, 1.0 } };
        private static readonly double[] Weights = { 0.4, 0.35, 0.25 };

        private static ArrayDoubleSpot Diffuse(ExecutorBackend backend)
        {
            var schedule = new PrecalculatedSchedule(Market.Dates(Steps));
            var brownian = Allocation.Prime(new ArrayBrownian(Paths, Steps * Assets));
            backend.Simulate(new PhiloxBrownianFill<ArrayBrownian>(Market.Seed, Market.SpotStream, Steps * Assets, brownian), 0, Paths);
            var logSpot = Allocation.PrimeLogSpot(new ArrayDoubleSpot(Paths, (Steps + 1) * Assets));
            for (int p = 0; p < Paths; ++p)
                for (int d = 0; d < Assets; ++d)
                    logSpot[p, d] = Math.Log(Spots[d]);
            backend.Simulate(new CorrelatedLogNormalDiffusion<ConstantRate, ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule>(
                Sigma, Dividend, CholeskyCorrelation.FromCorrelation(Correlation), new ConstantRate(Market.Rate), logSpot, brownian, schedule), 0, Paths);
            return logSpot;
        }

        private static double[] Run(ExecutorBackend backend, ArrayDoubleSpot logSpot, Func<LogBasedSpot<ArrayDoubleSpot>, ConstantRateDiscounting, PrecalculatedSchedule, ArrayPremium, ISimulatable> create)
        {
            var schedule = new PrecalculatedSchedule(Market.Dates(Steps));
            var premium = Allocation.Prime(new ArrayPremium(Paths));
            backend.Simulate(create(new LogBasedSpot<ArrayDoubleSpot>(logSpot), new ConstantRateDiscounting(Market.Rate, schedule), schedule, premium), 0, Paths);
            var values = new double[Paths];
            for (int p = 0; p < Paths; ++p) values[p] = premium[p];
            return values;
        }

        private static ISimulatable Basket(LogBasedSpot<ArrayDoubleSpot> s, ConstantRateDiscounting d, PrecalculatedSchedule sc, ArrayPremium p, bool geometric) =>
            new BasketOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                s, d, sc, p, Assets, Weights, Spots, Steps, 1.0, 1, geometric);

        private static ISimulatable WorstOf(LogBasedSpot<ArrayDoubleSpot> s, ConstantRateDiscounting d, PrecalculatedSchedule sc, ArrayPremium p) =>
            new RainbowOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                s, d, sc, p, Assets, Spots, Steps, 0.9, -1, false);

        private static ISimulatable Note(LogBasedSpot<ArrayDoubleSpot> s, ConstantRateDiscounting d, PrecalculatedSchedule sc, ArrayPremium p) =>
            new Autocallable<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                s, d, sc, p, Allocation.Prime(new ArrayPremium(Paths)), Assets, Spots, new[] { 1, 2, 3, 4 }, new[] { 1.0, 1.0, 0.95, 0.95 },
                new[] { 0.7, 0.7, 0.7, 0.7 }, new[] { 0.02, 0.02, 0.02, 0.02 }, true, new[] { 1, 2, 3, 4 }, 0.6, 1.0);

        [HybridizerTheory]
        [MemberData(nameof(ExecutorBackends.ManagedNames), MemberType = typeof(ExecutorBackends))]
        public void SpotsAndPremiums_AreBitIdenticalAcrossBackends(string backendName)
        {
            ExecutorBackend backend = ExecutorBackends.Get(backendName);
            ArrayDoubleSpot reference = Diffuse(ExecutorBackends.Reference);
            ArrayDoubleSpot actual = Diffuse(backend);
            int columns = (Steps + 1) * Assets;
            Matrices.AssertBitIdentical(Matrices.Snapshot((p, t) => reference[p, t], Paths, columns), Matrices.Snapshot((p, t) => actual[p, t], Paths, columns), backend.Name);
            Matrices.AssertBitIdentical(Run(ExecutorBackends.Reference, reference, (s, d, sc, p) => Basket(s, d, sc, p, false)), Run(backend, actual, (s, d, sc, p) => Basket(s, d, sc, p, false)), backend.Name);
            Matrices.AssertBitIdentical(Run(ExecutorBackends.Reference, reference, WorstOf), Run(backend, actual, WorstOf), backend.Name);
            Matrices.AssertBitIdentical(Run(ExecutorBackends.Reference, reference, Note), Run(backend, actual, Note), backend.Name);
        }

        [HybridizerFact]
        public void GeometricBasketMean_AgreesWithTheClosedForm()
        {
            ArrayDoubleSpot logSpot = Diffuse(ExecutorBackends.Reference);
            double[] premiums = Run(ExecutorBackends.Reference, logSpot, (s, d, sc, p) => Basket(s, d, sc, p, true));
            double sum = 0, squares = 0;
            foreach (double v in premiums) { sum += v; squares += v * v; }
            double mean = sum / Paths, se = Math.Sqrt((squares / Paths - mean * mean) / (Paths - 1));
            var correlation = new double[Assets * Assets];
            for (int i = 0; i < Assets; ++i)
                for (int j = 0; j < Assets; ++j) correlation[i * Assets + j] = Correlation[i, j];
            double exact = MultiAssetKernels.GeometricBasketPrice(1, Assets, new[] { 1.0, 1.0, 1.0 }, Weights, Dividend, Sigma, correlation, 1.0, Market.Maturity, Market.Rate);
            Assert.True(Math.Abs(mean - exact) < 4 * se, $"geometric basket: MC {mean} +- {se}, closed form {exact}");
        }
    }
}
