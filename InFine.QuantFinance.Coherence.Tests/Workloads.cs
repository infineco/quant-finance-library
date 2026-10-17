using System;
using InFine.QuantFinance.LongstaffSchwartz;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Models.Diffusion;
using InFine.QuantFinance.Payoffs.Concepts;
using InFine.QuantFinance.Payoffs.Equity;
using InFine.QuantFinance.Scheduling;
using Xunit;

namespace InFine.QuantFinance.Coherence.Tests
{
    /// <summary>Market and simulation parameters shared by the coherence tests.</summary>
    public static class Market
    {
        /// <summary>Philox key of every workload.</summary>
        public const ulong Seed = 0x1F3A5C7E9B2D4F60UL;

        /// <summary>
        /// Path count: prime, so that no chunk count divides it and the chunks have unequal sizes.
        /// </summary>
        public const int Paths = 1009;

        public const double Spot = 100.0;
        public const double Rate = 0.03;
        public const double Maturity = 1.0;

        // Black-Scholes-Merton
        public const double Volatility = 0.25;
        public const int BlackScholesSteps = 12;

        // Heston: Fang & Oosterlee (2008) section 5.3 parameters, Feller condition violated (variance can be truncated).
        public const double HestonV0 = 0.0175;
        public const double HestonKappa = 1.5768;
        public const double HestonTheta = 0.0398;
        public const double HestonXi = 0.5751;
        public const double HestonRho = -0.5711;
        public const int HestonSteps = 50;

        public const uint SpotStream = 0;
        public const uint IndependentStream = 1;

        /// <summary>Pillar dates in years: steps + 1 equally spaced dates on [0, maturity].</summary>
        public static double[] Dates(int steps)
        {
            var dates = new double[steps + 1];
            for (int t = 0; t <= steps; ++t)
                dates[t] = Maturity * t / steps;
            return dates;
        }
    }

    /// <summary>
    /// Forces the host allocation of Hybridizer storages before any concurrent use.
    /// </summary>
    /// <remarks>
    /// <c>alignedstorage_*</c> allocate their host buffer lazily, on the first access, without any synchronization
    /// (<c>DoubleResidentArray.CheckRefreshHost</c> / <c>AllocHost</c>). If several threads make that first access
    /// at the same time - which is what the first <c>Simulate</c> chunks of a parallel backend, or
    /// <c>MultiBlockParallelExecutor.For</c>, do on a storage nobody touched yet - each allocates its own buffer,
    /// the last one wins, and the writes made to the others are silently lost (observed: zeros in the Brownian array
    /// of some paths). Every storage that a backend writes in parallel is therefore touched once, by one thread,
    /// first. This is a property of the Hybridizer managed runtime, not of the library kernels, and the same
    /// precaution applies to any host code that fills storages through <c>MultiBlockParallelExecutor</c>.
    /// </remarks>
    public static class Allocation
    {
        public static T Prime<T>(T brownian) where T : IBrownian
        {
            double ignored = brownian[0, 0];
            return brownian;
        }

        public static T PrimeLogSpot<T>(T logSpot) where T : ILogSpot
        {
            double ignored = logSpot[0, 0];
            return logSpot;
        }

        public static ArrayStochasticVolatility Prime(ArrayStochasticVolatility variance)
        {
            double ignored = ((IStochasticVolatility)variance)[0, 0];
            return variance;
        }

        public static ArrayPremium Prime(ArrayPremium premium)
        {
            double ignored = premium[0];
            return premium;
        }
    }

    /// <summary>Row-major [time * paths + path] snapshot of a matrix read through an indexer.</summary>
    public static class Matrices
    {
        public static double[] Snapshot(Func<int, int, double> cell, int paths, int times)
        {
            var values = new double[paths * times];
            for (int t = 0; t < times; ++t)
                for (int p = 0; p < paths; ++p)
                    values[t * paths + p] = cell(p, t);
            return values;
        }

        /// <summary>Asserts both arrays hold exactly the same bits (not merely equal doubles: also distinguishes -0 and NaN payloads).</summary>
        public static void AssertBitIdentical(double[] expected, double[] actual, string what)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int k = 0; k < expected.Length; ++k)
            {
                if (BitConverter.DoubleToInt64Bits(expected[k]) != BitConverter.DoubleToInt64Bits(actual[k]))
                    Assert.Fail($"{what}: element {k} differs: expected {expected[k]:R}, actual {actual[k]:R}");
            }
        }
    }

    /// <summary>Result of a Heston run: raw variance and log-spot, [time * paths + path].</summary>
    public sealed class HestonPaths
    {
        public double[] Variance;
        public double[] LogSpot;
        public double[] SpotBrownian;
        public double[] IndependentBrownian;
    }

    /// <summary>Runs the library workloads on a given backend.</summary>
    public static class Workloads
    {
        /// <summary>The Brownian array of the spot, [time * paths + path], filled by <paramref name="backend"/>.</summary>
        public static double[] SpotBrownian<TBrownian>(ExecutorBackend backend, Func<int, int, TBrownian> newBrownian, int paths, int steps)
            where TBrownian : IBrownian
        {
            TBrownian brownian = Allocation.Prime(newBrownian(paths, steps));
            backend.Simulate(new PhiloxBrownianFill<TBrownian>(Market.Seed, Market.SpotStream, steps, brownian), 0, paths);
            return Matrices.Snapshot((p, t) => brownian[p, t], paths, steps);
        }

        /// <summary>
        /// Black-Scholes-Merton log-spot, [time * paths + path], through <see cref="BlackScholesMerton{LogSpot,Brownian,Schedule}"/>:
        /// Brownian fill then diffusion, each distributed by <paramref name="backend"/>.
        /// </summary>
        public static double[] BlackScholesLogSpot<TLogSpot, TBrownian>(
            ExecutorBackend backend, Func<int, int, TLogSpot> newLogSpot, Func<int, int, TBrownian> newBrownian, int paths, int steps)
            where TLogSpot : ILogSpot
            where TBrownian : IBrownian
        {
            var schedule = new PrecalculatedSchedule(Market.Dates(steps));
            TLogSpot logSpot = Allocation.PrimeLogSpot(newLogSpot(paths, steps + 1));
            TBrownian brownian = Allocation.Prime(newBrownian(paths, steps));
            for (int p = 0; p < paths; ++p)
                logSpot[p, 0] = Math.Log(Market.Spot);

            backend.Simulate(new PhiloxBrownianFill<TBrownian>(Market.Seed, Market.SpotStream, steps, brownian), 0, paths);
            var model = new BlackScholesMerton<TLogSpot, TBrownian, PrecalculatedSchedule>(
                new ConstantVolatility(Market.Volatility), new ConstantRate(Market.Rate), logSpot, brownian, schedule);
            backend.Simulate(model, 0, paths);

            return Matrices.Snapshot((p, t) => logSpot[p, t], paths, steps + 1);
        }

        /// <summary>
        /// The same diffusion through <see cref="BaseEquityDiffusion"/>, i.e. the generic
        /// <see cref="EquityDiffusion{Volatility,Rate,LogSpot,Brownian,Schedule}"/> kernel over interfaces.
        /// </summary>
        public static double[] EquityDiffusionLogSpot(ExecutorBackend backend, int paths, int steps)
        {
            var schedule = new PrecalculatedSchedule(Market.Dates(steps));
            var logSpot = Allocation.PrimeLogSpot(new ArrayDoubleSpot(paths, steps + 1));
            var brownian = Allocation.Prime(new ArrayBrownian(paths, steps));
            for (int p = 0; p < paths; ++p)
                logSpot[p, 0] = Math.Log(Market.Spot);

            backend.Simulate(new PhiloxBrownianFill<ArrayBrownian>(Market.Seed, Market.SpotStream, steps, brownian), 0, paths);
            backend.Simulate(
                new BaseEquityDiffusion(new ConstantVolatility(Market.Volatility), new ConstantRate(Market.Rate), logSpot, brownian, schedule),
                0, paths);

            return Matrices.Snapshot((p, t) => logSpot[p, t], paths, steps + 1);
        }

        /// <summary>
        /// Heston paths: Philox draws for the spot and for an independent driver, full-truncation variance by
        /// <see cref="HestonVolatilityModel{Volatility,Rate,Brownian,Schedule}"/>, then spot by the equity diffusion
        /// reading the same variance storage. Three stages, each distributed by <paramref name="backend"/>.
        /// </summary>
        public static HestonPaths Heston(ExecutorBackend backend, int paths, int steps)
        {
            var schedule = new PrecalculatedSchedule(Market.Dates(steps));
            var spotBrownian = Allocation.Prime(new ArrayBrownian(paths, steps));
            var independentBrownian = Allocation.Prime(new ArrayBrownian(paths, steps));
            var variance = Allocation.Prime(new ArrayStochasticVolatility(paths, steps + 1));
            var logSpot = Allocation.PrimeLogSpot(new ArrayDoubleSpot(paths, steps + 1));
            for (int p = 0; p < paths; ++p)
            {
                variance[p, 0] = Market.HestonV0;
                logSpot[p, 0] = Math.Log(Market.Spot);
            }

            backend.Simulate(new PhiloxBrownianFill<ArrayBrownian>(Market.Seed, Market.SpotStream, steps, spotBrownian), 0, paths);
            backend.Simulate(new PhiloxBrownianFill<ArrayBrownian>(Market.Seed, Market.IndependentStream, steps, independentBrownian), 0, paths);

            var heston = new HestonVolatilityModel<ArrayStochasticVolatility, ConstantRate, ArrayBrownian, PrecalculatedSchedule>(
                variance, spotBrownian, independentBrownian, schedule,
                Market.HestonXi, Market.HestonKappa, Market.HestonTheta, Market.HestonRho);
            backend.Simulate(heston, 0, paths);

            backend.Simulate(new HestonSpotDiffusion(variance, new ConstantRate(Market.Rate), logSpot, spotBrownian, schedule), 0, paths);

            return new HestonPaths
            {
                Variance = Matrices.Snapshot((p, t) => variance[p, t], paths, steps + 1),
                LogSpot = Matrices.Snapshot((p, t) => logSpot[p, t], paths, steps + 1),
                SpotBrownian = Matrices.Snapshot((p, t) => spotBrownian[p, t], paths, steps),
                IndependentBrownian = Matrices.Snapshot((p, t) => independentBrownian[p, t], paths, steps),
            };
        }

        /// <summary>
        /// Black-Scholes-Merton log-spot kept as an <see cref="ArrayDoubleSpot"/> for the pricing workloads below.
        /// </summary>
        public static ArrayDoubleSpot DiffuseBlackScholes(ExecutorBackend backend, int paths, int steps)
        {
            var schedule = new PrecalculatedSchedule(Market.Dates(steps));
            var logSpot = Allocation.PrimeLogSpot(new ArrayDoubleSpot(paths, steps + 1));
            var brownian = Allocation.Prime(new ArrayBrownian(paths, steps));
            for (int p = 0; p < paths; ++p)
                logSpot[p, 0] = Math.Log(Market.Spot);

            backend.Simulate(new PhiloxBrownianFill<ArrayBrownian>(Market.Seed, Market.SpotStream, steps, brownian), 0, paths);
            backend.Simulate(
                new BlackScholesMerton<ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule>(
                    new ConstantVolatility(Market.Volatility), new ConstantRate(Market.Rate), logSpot, brownian, schedule),
                0, paths);
            return logSpot;
        }

        /// <summary>
        /// Discounted European call payoff of every path through <see cref="EuropeanCallOption{Discounting,Spot,Schedule,Premium}"/>
        /// over an already diffused log-spot; the payoff is distributed by <paramref name="backend"/>.
        /// </summary>
        public static double[] EuropeanCallPremiums(ExecutorBackend backend, ArrayDoubleSpot logSpot, int paths, int steps, double strike)
        {
            var schedule = new PrecalculatedSchedule(Market.Dates(steps));
            var premium = Allocation.Prime(new ArrayPremium(paths));
            var option = new EuropeanCallOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                new LogBasedSpot<ArrayDoubleSpot>(logSpot), new ConstantRateDiscounting(Market.Rate, schedule), schedule, premium, steps, strike);
            backend.Simulate(option, 0, paths);

            var premiums = new double[paths];
            for (int p = 0; p < paths; ++p)
                premiums[p] = premium[p];
            return premiums;
        }

        /// <summary>
        /// The same payoff computed by the backend's <see cref="IParallelExecutor.For"/> (the loop the Longstaff-Schwartz
        /// kernels use) from a log-spot matrix, with exactly the arithmetic of <c>EuropeanCallOption.Simulate</c>.
        /// </summary>
        public static double[] EuropeanCallPremiumsByExecutorFor(
            IParallelExecutor executor, ArrayDoubleSpot logSpot, int paths, int steps, double strike)
        {
            var schedule = new PrecalculatedSchedule(Market.Dates(steps));
            var spot = new LogBasedSpot<ArrayDoubleSpot>(logSpot);
            var discounting = new ConstantRateDiscounting(Market.Rate, schedule);
            var premiums = new double[paths];
            executor.For(0, paths, id =>
            {
                premiums[(int)id] = Math.Max(0.0, spot[id, steps] - strike) * discounting[id, steps];
            });
            return premiums;
        }

        /// <summary>
        /// Longstaff-Schwartz price of an American (Bermudan on the simulation dates) put, with quadratic regression,
        /// on paths diffused by <paramref name="backend"/> and with the executor of that backend inside the
        /// Longstaff-Schwartz kernel. Needs the managed <c>Calculate</c> path: serial reductions.
        /// </summary>
        public static double AmericanPutPrice(ExecutorBackend backend, int paths, int steps, double strike)
        {
            switch (backend.CreateExecutor())
            {
                case SequentialExecutor executor:
                    return AmericanPutPrice(backend, executor, paths, steps, strike);
                case SingleBlockParallelExecutor executor:
                    return AmericanPutPrice(backend, executor, paths, steps, strike);
                case MultiBlockParallelExecutor executor:
                    return AmericanPutPrice(backend, executor, paths, steps, strike);
                default:
                    throw new NotSupportedException("No Longstaff-Schwartz instantiation for backend " + backend.Name);
            }
        }

        private static double AmericanPutPrice<TExecutor>(ExecutorBackend backend, TExecutor executor, int paths, int steps, double strike)
            where TExecutor : IParallelExecutor
        {
            double[] dates = Market.Dates(steps);
            var schedule = new PrecalculatedSchedule(dates);
            var spot = new LogBasedSpot<ArrayDoubleSpot>(DiffuseBlackScholes(backend, paths, steps));

            var exercise = new Hybridizer.Runtime.CUDAImports.alignedstorage_int(paths);
            var premiums = new Hybridizer.Runtime.CUDAImports.alignedstorage_double(paths);
            double touched = premiums[0] + exercise[0]; // see Allocation: allocate before the executor goes parallel
            var ls = new LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpot>, ConstantRateDiscounting, AmericanPut,
                QuadraticUnivariateRegressorStruct, TExecutor>
            {
                _dates = dates,
                _exercise = exercise,
                _premium = premiums,
                _simulationCount = paths,
                _datesCount = dates.Length,
                discounting = new ConstantRateDiscounting(Market.Rate, schedule),
                observable = spot,
                product = new AmericanPut { Strike = strike, Spot = spot },
                regressor = new QuadraticUnivariateRegressorStruct(),
                executor = executor,
            };
            ls.Calculate(ls.GetHelperSerialReduction());
            return ls.res[0] / paths;
        }

        /// <summary>
        /// Sum in index order, on the host: the reference for the reductions.
        /// </summary>
        public static double SequentialSum(double[] values)
        {
            double sum = 0.0;
            for (int k = 0; k < values.Length; ++k)
                sum += values[k];
            return sum;
        }
    }

    /// <summary>
    /// Spot diffusion of the Heston model as an <see cref="ISimulatable"/>: the equity diffusion over the paths
    /// [simFrom, simTo), reading the variance written by <see cref="HestonVolatilityModel{Volatility,Rate,Brownian,Schedule}"/>.
    /// </summary>
    public sealed class HestonSpotDiffusion : ISimulatable
    {
        private readonly ArrayStochasticVolatility _variance;
        private readonly ConstantRate _rate;
        private readonly ArrayDoubleSpot _logSpot;
        private readonly ArrayBrownian _brownian;
        private readonly PrecalculatedSchedule _schedule;
        private readonly EquityDiffusion<ArrayStochasticVolatility, ConstantRate, ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule> _diffusion =
            new EquityDiffusion<ArrayStochasticVolatility, ConstantRate, ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule>();

        public HestonSpotDiffusion(ArrayStochasticVolatility variance, ConstantRate rate, ArrayDoubleSpot logSpot, ArrayBrownian brownian, PrecalculatedSchedule schedule)
        {
            _variance = variance;
            _rate = rate;
            _logSpot = logSpot;
            _brownian = brownian;
            _schedule = schedule;
        }

        public void Simulate(int simFrom, int simTo)
        {
            _diffusion.Diffusion(simFrom, simTo, 0, _schedule.PilarCount, _variance, _rate, _logSpot, _brownian, _schedule);
        }
    }
}
