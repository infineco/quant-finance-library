using System;
using System.Diagnostics;
using InFine.QuantFinance.LongstaffSchwartz;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Models.Diffusion;
using InFine.QuantFinance.Random;
using InFine.QuantFinance.Random.DotNet;
using InFine.QuantFinance.Scheduling;
using Hybridizer.Runtime.CUDAImports;

namespace InFine.QuantFinance.Benchmarks
{
    /// <summary>
    /// Scenario builders that touch Hybridizer.Runtime.CUDAImports types (alignedstorage, alignedindex...).
    /// They are isolated here, and benchmark classes only hold the returned delegates, so that merely
    /// loading a benchmark class never needs the Hybridizer assembly. On hosts where it cannot load
    /// (arm64: the package is stamped AMD64) Program filters these benchmarks out.
    /// </summary>
    internal static class HybridizerScenarios
    {
        internal const double S0 = 100.0;
        internal const double Volatility = 0.45;
        internal const double Rate = 0.01;

        private static double[] Dates(int datesCount)
        {
            var dates = new double[datesCount];
            for (int k = 0; k < datesCount; ++k)
                dates[k] = k / 365.0;
            return dates;
        }

        /// <summary>Managed BSM diffusion over a precalculated schedule (generic, no interface dispatch).</summary>
        internal static Action BuildDiffusion(int simCount, int datesCount)
        {
            var schedule = new PrecalculatedSchedule(Dates(datesCount));
            var vol = new ConstantVolatility { Volatility = Volatility };
            var rate = new ConstantRate { YearlyRate = Rate };
            var logSpot = new ArrayDoubleSpot(simCount, datesCount);
            var brownian = new ArrayBrownian(simCount, datesCount);
            FillBrownian(brownian, simCount, datesCount);
            for (int k = 0; k < simCount; ++k)
                logSpot[k, 0] = Math.Log(S0);

            var bsm = new BlackScholesMerton<ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule>(vol, rate, logSpot, brownian, schedule);
            return () => bsm.Simulate(0, simCount);
        }

        /// <summary>Managed Longstaff-Schwartz American put, serial reduction. Returns the premium.</summary>
        internal static Func<double> BuildLongstaffSchwartz(int simCount, int datesCount)
        {
            double[] dates = Dates(datesCount);
            var schedule = new PrecalculatedSchedule(dates);
            var vol = new ConstantVolatility { Volatility = Volatility };
            var rate = new ConstantRate { YearlyRate = Rate };
            var spot = new ArrayDoubleSpot(simCount, datesCount);
            var brownian = new ArrayBrownian(simCount, datesCount);
            FillBrownian(brownian, simCount, datesCount);
            for (int k = 0; k < simCount; ++k)
                spot[k, 0] = Math.Log(1.0);
            new BlackScholesMerton<ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule>(vol, rate, spot, brownian, schedule)
                .Simulate(0, simCount);

            var ls = new LongstaffSchwartz<ArrayDoubleSpot, ConstantRateDiscounting, AmericanPut, QuadraticUnivariateRegressorStruct, SequentialExecutor>
            {
                _dates = dates,
                _exercise = new alignedstorage_int(simCount),
                _premium = new alignedstorage_double(simCount),
                _simulationCount = simCount,
                _datesCount = dates.Length,
                discounting = new ConstantRateDiscounting(0.06, schedule),
                observable = spot,
                product = new AmericanPut { Strike = 1.10, Spot = spot },
                regressor = new QuadraticUnivariateRegressorStruct(),
                executor = new SequentialExecutor(),
            };
            return () =>
            {
                ls.Calculate(ls.GetHelperSerialReduction());
                return ls.res[0] / ls._simulationCount;
            };
        }

        private static void FillBrownian(ArrayBrownian brownian, int simCount, int datesCount)
        {
            var gaussians = new double[simCount * datesCount];
            new MRG32K3A(20151114UL).GenerateGaussians(GaussianTransformEnum.BoxMuller2, 0.0, 1.0, gaussians.Length, gaussians);
            for (int t = 0; t < datesCount; ++t)
                for (int s = 0; s < simCount; ++s)
                    brownian[s, t] = gaussians[s + simCount * t];
        }

        internal static (Action<int, double[]> Uniform, Action<int, double[]> Gaussian) CreateMrg32k3a(ulong seed)
        {
            var rng = new MRG32K3A(seed);
            return (
                (count, target) => rng.GenerateUniforms(UniformTransformEnum.STANDARD, 0.0, 1.0, count, target),
                (count, target) => rng.GenerateGaussians(GaussianTransformEnum.BoxMuller2, 0.0, 1.0, count, target));
        }

        /// <summary>Null when Hybridizer.Runtime.CUDAImports loads in this process, else the reason it does not.</summary>
        internal static string Probe()
        {
            try
            {
                System.Reflection.Assembly.Load("Hybridizer.Runtime.CUDAImports");
                return null;
            }
            catch (Exception e)
            {
                return e.GetType().Name + ": " + e.Message;
            }
        }
    }
}
