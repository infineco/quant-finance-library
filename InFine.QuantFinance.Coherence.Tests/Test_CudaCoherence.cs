using System;
using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Market;
using InFine.QuantFinance.Numerics;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Models.Diffusion;
using InFine.QuantFinance.LongstaffSchwartz;
using InFine.QuantFinance.Payoffs.Concepts;
using InFine.QuantFinance.Payoffs.Equity;
using InFine.QuantFinance.Scheduling;
using Xunit;
using Xunit.Abstractions;

namespace InFine.QuantFinance.Coherence.Tests
{
    /// <summary>
    /// GPU = CPU on the kernels the other coherence classes run on the managed backends only: each workload runs on the
    /// reference backend and on <see cref="CudaBackend"/> (diffusions and payoffs on the device, the Philox fills of the
    /// test on the host), then every path is compared.
    /// </summary>
    /// <remarks>
    /// Tolerances. The Philox words are integer arithmetic and identical everywhere; what differs is the device math
    /// library (exp, log, sqrt, erfc, cos are correctly rounded on the host but may differ by an ulp or two on CUDA) and,
    /// for reductions, the summation order. Hence absolute 1e-11 on log-spots of order 5 and on Hull-White states, and
    /// relative 1e-12 (plus 1e-12 absolute for payoffs that are zero) on per-path premiums. A payoff with a discontinuity
    /// (barrier, knock-in, autocall trigger) could jump on a path whose spot sits within an ulp of the level; none does on
    /// these paths, and the tests report the count rather than tolerate it silently.
    /// </remarks>
    public class Test_CudaCoherence
    {
        private const int Paths = Market.Paths;
        private const double LogSpotTolerance = 1e-11;
        private const double PremiumRelativeTolerance = 1e-12;

        private readonly ITestOutputHelper _output;

        public Test_CudaCoherence(ITestOutputHelper output)
        {
            _output = output;
        }

        private static double[] Read(ArrayPremium premium, int paths)
        {
            var values = new double[paths];
            for (int p = 0; p < paths; ++p) values[p] = premium[p];
            return values;
        }

        private static double[] Snapshot(ArrayDoubleSpot matrix, int paths, int columns) =>
            Matrices.Snapshot((p, t) => matrix[p, t], paths, columns);

        private void AssertAbsolute(double[] reference, double[] device, double tolerance, string what)
        {
            Assert.Equal(reference.Length, device.Length);
            double worst = 0.0;
            for (int k = 0; k < reference.Length; ++k)
            {
                double error = Math.Abs(device[k] - reference[k]);
                Assert.True(error <= tolerance, $"{what}: element {k}: device {device[k]:R}, host {reference[k]:R}");
                worst = Math.Max(worst, error);
            }
            _output.WriteLine($"{what}: max |device - host| = {worst:E2} over {reference.Length} values");
        }

        private void AssertPremiums(double[] reference, double[] device, string what)
        {
            Assert.Equal(reference.Length, device.Length);
            double worst = 0.0;
            for (int k = 0; k < reference.Length; ++k)
            {
                double error = Math.Abs(device[k] - reference[k]);
                Assert.True(error <= PremiumRelativeTolerance * Math.Abs(reference[k]) + 1e-12,
                    $"{what}: path {k}: device {device[k]:R}, host {reference[k]:R}");
                if (reference[k] != 0.0) worst = Math.Max(worst, error / Math.Abs(reference[k]));
            }
            _output.WriteLine($"{what}: max relative error {worst:E2} over {reference.Length} paths");
        }

        private static int[] Indices(int from, int to)
        {
            var r = new int[to - from + 1];
            for (int i = 0; i < r.Length; ++i) r[i] = from + i;
            return r;
        }

        // ------------------------------------------------------------------ path-dependent payoffs

        private delegate ISimulatable SingleAssetFactory(
            LogBasedSpot<ArrayDoubleSpot> spot, ConstantRateDiscounting discounting, PrecalculatedSchedule schedule, ArrayPremium premium);

        private static double[] SingleAssetPremiums(ExecutorBackend backend, ArrayDoubleSpot logSpot, int steps, SingleAssetFactory create)
        {
            var schedule = new PrecalculatedSchedule(Market.Dates(steps));
            var premium = Allocation.Prime(new ArrayPremium(Paths));
            backend.Simulate(create(new LogBasedSpot<ArrayDoubleSpot>(logSpot), new ConstantRateDiscounting(Market.Rate, schedule), schedule, premium), 0, Paths);
            return Read(premium, Paths);
        }

        [CudaBackendFact]
        public void Cuda_PathDependentPremiums_MatchTheManagedReference()
        {
            const int steps = Market.BlackScholesSteps;
            const double strike = 100.0;
            int launches = CudaLauncher.DeviceLaunches;
            ArrayDoubleSpot reference = Workloads.DiffuseBlackScholes(ExecutorBackends.Reference, Paths, steps);
            ArrayDoubleSpot device = Workloads.DiffuseBlackScholes(ExecutorBackends.Cuda, Paths, steps);
            AssertAbsolute(Snapshot(reference, Paths, steps + 1), Snapshot(device, Paths, steps + 1), LogSpotTolerance, "Black-Scholes-Merton log-spot");

            var payoffs = new (string Name, SingleAssetFactory Create)[]
            {
                ("European call", (s, d, sc, p) => new EuropeanCallOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(s, d, sc, p, steps, strike)),
                ("arithmetic Asian", (s, d, sc, p) => new AsianOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(s, d, sc, p, Indices(1, steps), steps, strike, 1, false, false)),
                ("geometric Asian", (s, d, sc, p) => new AsianOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(s, d, sc, p, Indices(1, steps), steps, strike, 1, true, false)),
                ("discrete down-and-out", (s, d, sc, p) => new BarrierOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(s, d, sc, p, Indices(0, steps), steps, strike, 1, false, false, 90.0, 0.0, Market.Volatility, BarrierMonitoring.Discrete)),
                ("bridge down-and-out", (s, d, sc, p) => new BarrierOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(s, d, sc, p, Indices(0, steps), steps, strike, 1, false, false, 90.0, 0.0, Market.Volatility, BarrierMonitoring.BridgeProbability)),
                ("floating lookback", (s, d, sc, p) => new LookbackOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(s, d, sc, p, Indices(1, steps), steps, 0.0, 1, true, 0.0, LookbackMath.ContinuityShift(Market.Volatility, Market.Maturity / steps))),
                ("variance swap", (s, d, sc, p) => new VarianceSwap<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(s, d, sc, p, steps, 0.0625, 0.0)),
            };
            foreach (var (name, create) in payoffs)
                AssertPremiums(SingleAssetPremiums(ExecutorBackends.Reference, reference, steps, create), SingleAssetPremiums(ExecutorBackends.Cuda, device, steps, create), name);

            // Diffusion (the Philox fill runs on the host) and one launch per payoff.
            Assert.Equal(launches + 1 + payoffs.Length, CudaLauncher.DeviceLaunches);
        }

        // ------------------------------------------------------------------ multi-asset

        private const int Assets = 3;
        private const int MultiSteps = 4;
        private static readonly double[] Spots = { 100.0, 80.0, 120.0 };
        private static readonly double[] Sigma = { 0.2, 0.3, 0.25 };
        private static readonly double[] Dividend = { 0.01, 0.0, 0.02 };
        private static readonly double[,] Correlation = { { 1.0, 0.5, 0.3 }, { 0.5, 1.0, 0.6 }, { 0.3, 0.6, 1.0 } };
        private static readonly double[] Weights = { 0.4, 0.35, 0.25 };

        private static ArrayDoubleSpot DiffuseCorrelated(ExecutorBackend backend)
        {
            var schedule = new PrecalculatedSchedule(Market.Dates(MultiSteps));
            var brownian = Allocation.Prime(new ArrayBrownian(Paths, MultiSteps * Assets));
            backend.Simulate(new PhiloxBrownianFill<ArrayBrownian>(Market.Seed, Market.SpotStream, MultiSteps * Assets, brownian), 0, Paths);
            var logSpot = Allocation.PrimeLogSpot(new ArrayDoubleSpot(Paths, (MultiSteps + 1) * Assets));
            for (int p = 0; p < Paths; ++p)
                for (int d = 0; d < Assets; ++d)
                    logSpot[p, d] = Math.Log(Spots[d]);
            backend.Simulate(new CorrelatedLogNormalDiffusion<ConstantRate, ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule>(
                Sigma, Dividend, CholeskyCorrelation.FromCorrelation(Correlation), new ConstantRate(Market.Rate), logSpot, brownian, schedule), 0, Paths);
            return logSpot;
        }

        [CudaBackendFact]
        public void Cuda_MultiAssetSpotsAndPremiums_MatchTheManagedReference()
        {
            int launches = CudaLauncher.DeviceLaunches;
            ArrayDoubleSpot reference = DiffuseCorrelated(ExecutorBackends.Reference);
            ArrayDoubleSpot device = DiffuseCorrelated(ExecutorBackends.Cuda);
            int columns = (MultiSteps + 1) * Assets;
            AssertAbsolute(Snapshot(reference, Paths, columns), Snapshot(device, Paths, columns), LogSpotTolerance, "correlated log-normal log-spot");

            var payoffs = new (string Name, SingleAssetFactory Create)[]
            {
                ("arithmetic basket", (s, d, sc, p) => new BasketOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(s, d, sc, p, Assets, Weights, Spots, MultiSteps, 1.0, 1, false)),
                ("geometric basket", (s, d, sc, p) => new BasketOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(s, d, sc, p, Assets, Weights, Spots, MultiSteps, 1.0, 1, true)),
                ("worst-of put", (s, d, sc, p) => new RainbowOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(s, d, sc, p, Assets, Spots, MultiSteps, 0.9, -1, false)),
                ("best-of call", (s, d, sc, p) => new RainbowOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(s, d, sc, p, Assets, Spots, MultiSteps, 1.0, 1, true)),
                ("worst-of down-and-in put", (s, d, sc, p) => new WorstOfDownAndInPut<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(s, d, sc, p, Assets, Spots, new[] { 1, 2, 3, 4 }, MultiSteps, 1.0, 0.7)),
                ("autocallable", (s, d, sc, p) => new Autocallable<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                    s, d, sc, p, Allocation.Prime(new ArrayPremium(Paths)), Assets, Spots, new[] { 1, 2, 3, 4 }, new[] { 1.0, 1.0, 0.95, 0.95 },
                    new[] { 0.7, 0.7, 0.7, 0.7 }, new[] { 0.02, 0.02, 0.02, 0.02 }, true, new[] { 1, 2, 3, 4 }, 0.6, 1.0)),
            };
            foreach (var (name, create) in payoffs)
                AssertPremiums(SingleAssetPremiums(ExecutorBackends.Reference, reference, MultiSteps, create), SingleAssetPremiums(ExecutorBackends.Cuda, device, MultiSteps, create), name);
            Assert.Equal(launches + 1 + payoffs.Length, CudaLauncher.DeviceLaunches);
        }

        // ------------------------------------------------------------------ curve and Hull-White discounting

        private static readonly DateOnly Valuation = new DateOnly(2025, 1, 15);

        private static InterpolatedDiscountCurve EurCurve()
        {
            var calendar = TargetCalendar.Instance;
            var builder = new PiecewiseYieldCurveBuilder(Valuation, DayCounter.Actual365Fixed).WithInterpolation(CurveInterpolation.MonotoneCubicZero);
            foreach (var (tenor, quote) in new[] { ("1M", 0.0285), ("3M", 0.0275), ("6M", 0.0260) })
                builder.Add(Deposit.FromTenor(Valuation, Period.Parse(tenor), calendar), quote);
            foreach (var (tenor, quote) in new[] { ("1Y", 0.0245), ("2Y", 0.0230), ("3Y", 0.0228) })
                builder.Add(VanillaSwap.FromTenor(Valuation, Period.Parse(tenor), SwapConventions.EurSixMonth), quote);
            return builder.Build();
        }

        private sealed class RatesWorkload
        {
            public double[] LogSpot, State, LogDiscount, CurvePremiums, HullWhitePremiums;
        }

        private static RatesWorkload Rates(ExecutorBackend backend)
        {
            const double meanReversion = 0.04, rateVolatility = 0.011, strike = 105.0;
            DateOnly expiry = TargetCalendar.Instance.Adjust(Valuation.AddYears(2), BusinessDayConvention.Following);
            SimulationGrid grid = new SimulationGridBuilder(Valuation, DayCounter.Actual365Fixed).WithMaxTimeStep(1.0 / 12.0).AddEventDate(expiry).Build();
            double[] times = grid.ToTimesArray();
            var schedule = new PrecalculatedSchedule(times);
            CompiledDiscountCurve curve = MarketCompiler.CompileDiscountCurve(EurCurve(), grid);
            var model = new CompiledHullWhite(meanReversion, rateVolatility, curve, schedule);
            int steps = grid.StepCount;
            int maturity = grid.PillarIndex(expiry);

            // Equity paths: Philox on the host, Black-Scholes-Merton on the backend.
            var logSpot = Allocation.PrimeLogSpot(new ArrayDoubleSpot(Paths, steps + 1));
            var brownian = Allocation.Prime(new ArrayBrownian(Paths, steps));
            for (int p = 0; p < Paths; ++p) logSpot[p, 0] = Math.Log(Market.Spot);
            backend.Simulate(new PhiloxBrownianFill<ArrayBrownian>(Market.Seed, Market.SpotStream, steps, brownian), 0, Paths);
            backend.Simulate(new BlackScholesMerton<ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule>(
                new ConstantVolatility(Market.Volatility), new ConstantRate(Market.Rate), logSpot, brownian, schedule), 0, Paths);

            // Hull-White state and discount factor on the backend.
            var z1 = Allocation.Prime(new ArrayBrownian(Paths, steps));
            var z2 = Allocation.Prime(new ArrayBrownian(Paths, steps));
            var state = Allocation.PrimeLogSpot(new ArrayDoubleSpot(Paths, steps + 1));
            var logDiscount = Allocation.PrimeLogSpot(new ArrayDoubleSpot(Paths, steps + 1));
            backend.Simulate(new PhiloxBrownianFill<ArrayBrownian>(Market.Seed, 11, steps, z1), 0, Paths);
            backend.Simulate(new PhiloxBrownianFill<ArrayBrownian>(Market.Seed, 12, steps, z2), 0, Paths);
            backend.Simulate(new HullWhiteModel<ArrayBrownian>(model, state, logDiscount, z1, z2), 0, Paths);

            // The same European call discounted on the compiled curve, then with the stochastic Hull-White discount factor.
            var spot = new LogBasedSpot<ArrayDoubleSpot>(logSpot);
            var curvePremium = Allocation.Prime(new ArrayPremium(Paths));
            backend.Simulate(new EuropeanCallOption<CurveDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                spot, new CurveDiscounting(curve, schedule), schedule, curvePremium, maturity, strike), 0, Paths);
            var hullWhitePremium = Allocation.Prime(new ArrayPremium(Paths));
            backend.Simulate(new EuropeanCallOption<HullWhiteDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                spot, new HullWhiteDiscounting(model, state, logDiscount), schedule, hullWhitePremium, maturity, strike), 0, Paths);

            return new RatesWorkload
            {
                LogSpot = Snapshot(logSpot, Paths, steps + 1),
                State = Snapshot(state, Paths, steps + 1),
                LogDiscount = Snapshot(logDiscount, Paths, steps + 1),
                CurvePremiums = Read(curvePremium, Paths),
                HullWhitePremiums = Read(hullWhitePremium, Paths),
            };
        }

        [CudaBackendFact]
        public void Cuda_CurveAndHullWhiteDiscounting_MatchTheManagedReference()
        {
            int launches = CudaLauncher.DeviceLaunches;
            RatesWorkload reference = Rates(ExecutorBackends.Reference);
            RatesWorkload device = Rates(ExecutorBackends.Cuda);
            AssertAbsolute(reference.LogSpot, device.LogSpot, LogSpotTolerance, "log-spot on the curve grid");
            AssertAbsolute(reference.State, device.State, LogSpotTolerance, "Hull-White state x");
            AssertAbsolute(reference.LogDiscount, device.LogDiscount, LogSpotTolerance, "Hull-White ln D");
            AssertPremiums(reference.CurvePremiums, device.CurvePremiums, "European call, CurveDiscounting");
            AssertPremiums(reference.HullWhitePremiums, device.HullWhitePremiums, "European call, HullWhiteDiscounting");
            // Black-Scholes-Merton, Hull-White and the two payoffs.
            Assert.Equal(launches + 4, CudaLauncher.DeviceLaunches);
        }

        // ------------------------------------------------------------------ SimulateGroup

        [CudaBackendFact]
        public void Cuda_SimulateGroup_RunsEachSimulatableOfTheGroup()
        {
            // Two Black-Scholes-Merton diffusions with different volatilities and a payoff on paths diffused beforehand, all
            // in one SimulatableScheduler.SimulateGroup launch (one block per simulatable).
            const int steps = Market.BlackScholesSteps;
            var schedule = new PrecalculatedSchedule(Market.Dates(steps));
            ArrayDoubleSpot fixedPaths = Workloads.DiffuseBlackScholes(ExecutorBackends.Reference, Paths, steps);

            ISimulatable[] Group(out ArrayDoubleSpot low, out ArrayDoubleSpot high, out ArrayPremium premium)
            {
                var brownian = Allocation.Prime(new ArrayBrownian(Paths, steps));
                new PhiloxBrownianFill<ArrayBrownian>(Market.Seed, Market.SpotStream, steps, brownian).Simulate(0, Paths);
                low = Allocation.PrimeLogSpot(new ArrayDoubleSpot(Paths, steps + 1));
                high = Allocation.PrimeLogSpot(new ArrayDoubleSpot(Paths, steps + 1));
                for (int p = 0; p < Paths; ++p) { low[p, 0] = Math.Log(Market.Spot); high[p, 0] = Math.Log(Market.Spot); }
                premium = Allocation.Prime(new ArrayPremium(Paths));
                return new ISimulatable[]
                {
                    new BlackScholesMerton<ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule>(new ConstantVolatility(0.15), new ConstantRate(Market.Rate), low, brownian, schedule),
                    new BlackScholesMerton<ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule>(new ConstantVolatility(0.35), new ConstantRate(Market.Rate), high, brownian, schedule),
                    new AsianOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                        new LogBasedSpot<ArrayDoubleSpot>(fixedPaths), new ConstantRateDiscounting(Market.Rate, schedule), schedule, premium, Indices(1, steps), steps, 100.0, 1, false, false),
                };
            }

            ISimulatable[] hostGroup = Group(out var hostLow, out var hostHigh, out var hostPremium);
            foreach (ISimulatable s in hostGroup) s.Simulate(0, Paths);

            ISimulatable[] deviceGroup = Group(out var deviceLow, out var deviceHigh, out var devicePremium);
            lock (CudaLauncher.Gate)
            {
                foreach (ISimulatable s in deviceGroup) CudaLauncher.RefreshStaleHostCopies(s);
                HybRunner runner = HybRunner.Cuda(CudaLauncher.SatellitePath).SetDistrib(deviceGroup.Length, CudaLauncher.BlockDimX);
                dynamic scheduler = runner.Wrap(new SimulatableScheduler());
                scheduler.SimulateGroup(0, Paths, deviceGroup.Length, deviceGroup);
                Assert.Equal(cudaError_t.cudaSuccess, cuda.DeviceSynchronize());
            }

            AssertAbsolute(Snapshot(hostLow, Paths, steps + 1), Snapshot(deviceLow, Paths, steps + 1), LogSpotTolerance, "group member 1 (vol 15%)");
            AssertAbsolute(Snapshot(hostHigh, Paths, steps + 1), Snapshot(deviceHigh, Paths, steps + 1), LogSpotTolerance, "group member 2 (vol 35%)");
            AssertPremiums(Read(hostPremium, Paths), Read(devicePremium, Paths), "group member 3 (arithmetic Asian)");
        }

        [CudaBackendFact]
        public void Cuda_SimulateSingle_LargeGridCoversEveryPath()
        {
            // 1048583 paths on 4096 blocks: paths x blockIdx.x exceeds 2^31 from block 2048 on, which overflowed the
            // 32-bit chunk bounds of SimulateSingle. With zero Brownian increments every path ends on the same forward.
            const int paths = 1048583, grid = 4096;
            var schedule = new PrecalculatedSchedule(Market.Dates(1));
            var brownian = new ArrayBrownian(paths, 1);
            var logSpot = new ArrayDoubleSpot(paths, 2);
            double ignored = brownian[0, 0];
            for (int p = 0; p < paths; ++p) logSpot[p, 0] = Math.Log(Market.Spot);
            var model = new BlackScholesMerton<ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule>(
                new ConstantVolatility(Market.Volatility), new ConstantRate(Market.Rate), logSpot, brownian, schedule);
            lock (CudaLauncher.Gate)
            {
                HybRunner runner = HybRunner.Cuda(CudaLauncher.SatellitePath).SetDistrib(grid, CudaLauncher.BlockDimX);
                dynamic scheduler = runner.Wrap(new SimulatableScheduler());
                scheduler.SimulateSingle(0, paths, model);
                Assert.Equal(cudaError_t.cudaSuccess, cuda.DeviceSynchronize());
            }
            double expected = Math.Log(Market.Spot) + (Market.Rate - 0.5 * Market.Volatility * Market.Volatility) * Market.Maturity;
            int missed = 0, first = -1;
            for (int p = 0; p < paths; ++p)
            {
                if (Math.Abs(logSpot[p, 1] - expected) > LogSpotTolerance)
                {
                    missed++;
                    if (first < 0) first = p;
                }
            }
            Assert.True(missed == 0, $"{missed} of {paths} paths not diffused (first {first})");
        }

        [CudaBackendFact]
        public void Cuda_ResultsCanBeReadByManyThreadsAfterALaunch()
        {
            // Without the refresh CudaLauncher makes after each launch, the first concurrent host reads of a storage the
            // device wrote fail with CUDA error 700 (unsynchronized CheckRefreshHost in Hybridizer.Runtime.CUDAImports 3.7.1).
            const int paths = 200_003, steps = 4;
            var schedule = new PrecalculatedSchedule(Market.Dates(steps));
            var logSpot = new ArrayDoubleSpot(paths, steps + 1);
            var brownian = new ArrayBrownian(paths, steps);
            ExecutorBackends.Cuda.Simulate(new BlackScholesMerton<ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule>(
                new ConstantVolatility(Market.Volatility), new ConstantRate(Market.Rate), logSpot, brownian, schedule), 0, paths);
            double expected = (Market.Rate - 0.5 * Market.Volatility * Market.Volatility) * Market.Maturity;   // log-spot starts at 0
            int wrong = 0;
            System.Threading.Tasks.Parallel.For(0, paths, p =>
            {
                if (Math.Abs(logSpot[p, steps] - expected) > LogSpotTolerance) System.Threading.Interlocked.Increment(ref wrong);
            });
            Assert.Equal(0, wrong);
        }

        // ------------------------------------------------------------------ Longstaff-Schwartz

        /// <summary>
        /// Why the Longstaff-Schwartz device tests are skipped. The pricer hands its kernels delegates created on the host
        /// (<see cref="LSHelper"/> actions, map and init functions of the reductions); HybRunner needs a device function
        /// pointer for each, which Hybridizer generates only with delegate support, and the free edition (7.5.1 and 7.6.3 alike) has no way
        /// to turn it on (DelegateSupport="false" in the generated configuration, no command-line switch). The host code
        /// paths are covered by Test_LongstaffSchwartzCoherence; the fix is to replace the delegates by generic functors
        /// registered with HybridRegisterTemplate, the pattern the rest of the library uses.
        /// </summary>
        private const string LongstaffSchwartzDeviceBlocked =
            "The Hybridizer free edition (7.5.1, 7.6.3) generates no device function pointer for delegates created on the host "
            + "(applyRegressor): the LSHelper delegates cannot be marshalled. See docs/verification-windows-gpu.md.";

        private static LongstaffSchwartz<ArrayDoubleSpot, ConstantRateDiscounting, AmericanPut, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>
            AmericanPutPricer(ArrayDoubleSpot spot, double[] dates, ConstantRateDiscounting discounting, int paths, double strike)
        {
            return new LongstaffSchwartz<ArrayDoubleSpot, ConstantRateDiscounting, AmericanPut, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>
            {
                _dates = dates,
                _exercise = new alignedstorage_int(paths),
                _premium = new alignedstorage_double(paths),
                _simulationCount = paths,
                _datesCount = dates.Length,
                discounting = discounting,
                observable = spot,
                product = new AmericanPut { Strike = strike, Spot = spot },
                regressor = new QuadraticUnivariateRegressorStruct(),
                executor = new SingleBlockParallelExecutor(),
            };
        }

        /// <summary>Runs <paramref name="call"/> on an <see cref="LSEntryPoint"/> wrapped for one block of <paramref name="blockDim"/> threads.</summary>
        private static unsafe void RunOnDevice<TPricer>(TPricer pricer, LSHelper helper, int blockDim, Action<dynamic, TPricer, LSHelper> call)
        {
            lock (CudaLauncher.Gate)
            {
                cuda.DeviceSetLimit(cudaLimit.cudaLimitStackSize, 4096);
                HybRunner runner = HybRunner.Cuda(CudaLauncher.SatellitePath).SetDistrib(1, blockDim);
                // One RegressorData per thread for the block reduction of the regression sums.
                runner.SetShared(blockDim * sizeof(QuadraticUnivariateRegressorStruct.RegressorData));
                dynamic wrapped = runner.Wrap(new LSEntryPoint());
                call(wrapped, pricer, helper);
                Assert.Equal(cudaError_t.cudaSuccess, cuda.DeviceSynchronize());
            }
        }

        [Fact(Skip = LongstaffSchwartzDeviceBlocked)]
        public void Cuda_LongstaffSchwartz_PaperExample_GivesThePublishedPrice()
        {
            // Longstaff and Schwartz (2001), section 1: 8 paths, 3 exercise dates, strike 1.10, rate 6%: 0.1144.
            double[,] values =
            {
                { 1.00, 1.09, 1.08, 1.34 }, { 1.00, 1.16, 1.26, 1.54 }, { 1.00, 1.22, 1.07, 1.03 }, { 1.00, 0.93, 0.97, 0.92 },
                { 1.00, 1.11, 1.56, 1.52 }, { 1.00, 0.76, 0.77, 0.90 }, { 1.00, 0.92, 0.84, 1.01 }, { 1.00, 0.88, 1.22, 1.34 },
            };
            const int paths = 8;
            var spot = new ArrayDoubleSpot(paths, 4);
            for (int i = 0; i < paths; ++i)
                for (int j = 0; j < 4; ++j) spot[i, j] = values[i, j];
            var daily = new double[365];
            for (int k = 0; k < daily.Length; ++k) daily[k] = k / 365.0;
            var discounting = new ConstantRateDiscounting(0.06, new PrecalculatedSchedule(daily));
            double[] dates = { 0.0, 1.0, 2.0, 3.0 };

            var host = AmericanPutPricer(spot, dates, discounting, paths, 1.10);
            host.Calculate(host.GetHelperSerialReduction());
            double hostPrice = host.res[0] / paths;

            var device = AmericanPutPricer(spot, dates, discounting, paths, 1.10);
            RunOnDevice(device, device.GetHelperParallelReduction(), 32, (w, ls, h) => w.Calculate_Paper(ls, h));
            double devicePrice = device.res[0] / paths;

            _output.WriteLine($"Longstaff-Schwartz paper example: host {hostPrice:R}, device {devicePrice:R}");
            Assert.Equal(0.1144, hostPrice, 0.00005);
            Assert.Equal(hostPrice, devicePrice, 1e-12);
        }

        [Fact(Skip = LongstaffSchwartzDeviceBlocked)]
        public void Cuda_LongstaffSchwartz_AmericanPut_MatchesTheHostPrice()
        {
            // 4096 Black-Scholes paths, 50 exercise dates. The device regression sums with warp shuffles and shared memory
            // instead of one thread: same price up to the summation order of the regression sums.
            const int paths = 4096, steps = 50;
            const double strike = 100.0;
            ArrayDoubleSpot logSpot = Workloads.DiffuseBlackScholes(ExecutorBackends.Reference, paths, steps);
            var spot = new ArrayDoubleSpot(paths, steps + 1);
            for (int p = 0; p < paths; ++p)
                for (int t = 0; t <= steps; ++t) spot[p, t] = Math.Exp(logSpot[p, t]);
            double[] dates = Market.Dates(steps);
            var discounting = new ConstantRateDiscounting(Market.Rate, new PrecalculatedSchedule(dates));

            var host = AmericanPutPricer(spot, dates, discounting, paths, strike);
            host.Calculate(host.GetHelperSerialReduction());
            double hostPrice = host.res[0] / paths;

            var device = AmericanPutPricer(spot, dates, discounting, paths, strike);
            RunOnDevice(device, device.GetHelperParallelReduction(), 128, (w, ls, h) => w.Calculate_Paper(ls, h));
            double devicePrice = device.res[0] / paths;

            _output.WriteLine($"Longstaff-Schwartz American put, {paths} paths x {steps} dates: host {hostPrice:R}, device {devicePrice:R}");
            Assert.True(hostPrice > Math.Max(strike - Market.Spot, 0.0));
            Assert.Equal(hostPrice, devicePrice, 1e-10 * hostPrice);
        }
    }
}
