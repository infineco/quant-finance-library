using System;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Models.Diffusion;
using InFine.QuantFinance.TestSupport;
using Xunit;

namespace InFine.QuantFinance.Models.Tests
{
    /// <summary>
    /// Heston through the real kernel classes (plain .NET execution): variance by
    /// <see cref="HestonVolatilityModel{Volatility, Rate, Brownian, Schedule}"/>, spot by
    /// <see cref="EquityDiffusion{Volatility, Rate, LogSpot, Brownian, Schedule}"/> reading the same storage.
    /// </summary>
    public class UnitTest_HestonKernel
    {
        [HybridizerFact]
        public void VarianceThenSpotDiffusion_MatchesHelpersAndCharacteristicFunctionPrice()
        {
            var p = HestonParameters.FangOosterlee;
            const int simCount = 40000, steps = 100, datesCount = steps + 1;
            const double strike = 100.0;

            var dates = new double[datesCount];
            for (int t = 0; t < datesCount; ++t)
                dates[t] = p.T * t / steps;
            var schedule = new PrecalculatedSchedule(dates);

            var spotBrownian = new ArrayBrownian(simCount, datesCount);
            var independentBrownian = new ArrayBrownian(simCount, datesCount);
            var normals = new BoxMuller(1);
            for (int t = 0; t < steps; ++t)
            {
                for (int s = 0; s < simCount; ++s)
                {
                    normals.NextPair(out double zS, out double zPerp);
                    spotBrownian[s, t] = zS;
                    independentBrownian[s, t] = zPerp;
                }
            }

            var variance = new ArrayStochasticVolatility(simCount, datesCount);
            var logSpot = new ArrayDoubleSpot(simCount, datesCount);
            for (int s = 0; s < simCount; ++s)
            {
                variance[s, 0] = p.V0;
                logSpot[s, 0] = Math.Log(p.S0);
            }
            var rate = new ConstantRate(p.R);

            var heston = new HestonVolatilityModel<ArrayStochasticVolatility, ConstantRate, ArrayBrownian, PrecalculatedSchedule>(
                variance, spotBrownian, independentBrownian, schedule, p.Xi, p.Kappa, p.Theta, p.Rho);
            heston.Simulate(0, simCount);
            new EquityDiffusion<ArrayStochasticVolatility, ConstantRate, ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule>()
                .Diffusion(0, simCount, 0, schedule.PilarCount, variance, rate, logSpot, spotBrownian, schedule);

            // path by path against the Hybridizer-free helpers
            for (int s = 0; s < 200; ++s)
            {
                double v = p.V0, lnS = Math.Log(p.S0);
                for (int t = 0; t < steps; ++t)
                {
                    double dt = schedule.getDT(t), sqrtDt = schedule.getSqrtDT(t);
                    Assert.Equal(HestonStep.Volatility(v), variance[0.0, s, t]); // same arithmetic: bit-exact
                    lnS = HestonStep.LogSpotEuler(lnS, v, p.R, dt, sqrtDt, spotBrownian[s, t]);
                    v = HestonStep.FullTruncationEuler(v, p.Kappa, p.Theta, p.Xi, dt, sqrtDt,
                        HestonStep.CorrelatedDriver(spotBrownian[s, t], independentBrownian[s, t], p.Rho));
                    Assert.Equal(v, variance[s, t + 1]); // the kernel calls the same helpers: bit-exact
                    // EquityDiffusion squares sqrt(v+) and sums in another order: last-bit differences only
                    Assert.Equal(lnS, logSpot[s, t + 1], 1e-12);
                }
            }

            // price against the semi-analytic reference
            double sum = 0.0, sumSq = 0.0, discount = Math.Exp(-p.R * p.T);
            for (int s = 0; s < simCount; ++s)
            {
                double payoff = discount * Math.Max(Math.Exp(logSpot[s, steps]) - strike, 0.0);
                sum += payoff;
                sumSq += payoff * payoff;
            }
            double mean = sum / simCount;
            double stdErr = Math.Sqrt((sumSq / simCount - mean * mean) / (simCount - 1.0));
            double reference = HestonParameters.FangOosterleeAtmCall;
            Assert.True(Math.Abs(mean - reference) < 3.0 * stdErr, $"MC {mean} +/- {stdErr}, reference {reference}");

            logSpot.Free();
        }
    }
}
