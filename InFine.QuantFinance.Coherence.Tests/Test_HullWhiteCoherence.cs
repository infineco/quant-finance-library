using System;
using System.Collections.Generic;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Market;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Models.Diffusion;
using InFine.QuantFinance.Numerics;
using InFine.QuantFinance.Payoffs.Concepts;
using InFine.QuantFinance.Payoffs.Equity;
using InFine.QuantFinance.Scheduling;
using InFine.QuantFinance.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace InFine.QuantFinance.Coherence.Tests
{
    /// <summary>
    /// Hull-White through the host-to-kernel seam: a bootstrapped EUR curve (<see cref="IYieldCurve"/>), a 1Y into 4Y swaption
    /// defined with real dates and the EUR fixed-leg conventions, a <see cref="SimulationGrid"/> with the expiry as event date,
    /// <see cref="MarketCompiler"/>, then <see cref="CompiledHullWhite"/>. The analytic side (<see cref="HullWhite"/>) reads the
    /// host curve directly; the Monte Carlo side reads only the compiled arrays. Monte Carlo estimates must be within 3 standard
    /// errors of the analytic values (fixed seed, deterministic).
    /// </summary>
    public class Test_HullWhiteCoherence
    {
        private const double MeanReversion = 0.04;
        private const double RateVolatility = 0.011;
        private const ulong Seed = 0x5EED_4B1D_2025_1011UL;
        private const uint StateStream = 11, IntegralStream = 12, SpotStream = 13;

        private static readonly DateOnly Valuation = new DateOnly(2025, 1, 15);

        private readonly ITestOutputHelper _output;

        public Test_HullWhiteCoherence(ITestOutputHelper output)
        {
            _output = output;
        }

        private static InterpolatedDiscountCurve EurCurve()
        {
            var calendar = TargetCalendar.Instance;
            var builder = new PiecewiseYieldCurveBuilder(Valuation, DayCounter.Actual365Fixed)
                .WithInterpolation(CurveInterpolation.MonotoneCubicZero);
            foreach (var (tenor, quote) in new[] { ("1M", 0.0285), ("3M", 0.0275), ("6M", 0.0260) })
                builder.Add(Deposit.FromTenor(Valuation, Period.Parse(tenor), calendar), quote);
            foreach (var (tenor, quote) in new[] { ("1Y", 0.0245), ("2Y", 0.0230), ("3Y", 0.0228), ("5Y", 0.0235), ("7Y", 0.0245) })
                builder.Add(VanillaSwap.FromTenor(Valuation, Period.Parse(tenor), SwapConventions.EurSixMonth), quote);
            return builder.Build();
        }

        private void AssertWithin3Se(double reference, double mean, double standardError, string what)
        {
            double z = (mean - reference) / standardError;
            _output.WriteLine($"{what}: Monte Carlo {mean:R} +/- {standardError:E2} vs {reference:R} ({z:F2} SE)");
            Assert.True(standardError > 0.0 && Math.Abs(z) <= 3.0, $"{what}: Monte Carlo {mean} vs {reference}, {z:F2} standard errors");
        }

        private static (double Mean, double StandardError) Estimate(double[] samples)
        {
            double sum = 0.0, sumSq = 0.0;
            foreach (double v in samples)
            {
                sum += v;
                sumSq += v * v;
            }
            double mean = sum / samples.Length;
            return (mean, Math.Sqrt(Math.Max(0.0, sumSq / samples.Length - mean * mean) / (samples.Length - 1.0)));
        }

        [Fact]
        public void SwaptionWithDatesAndABootstrappedCurve_MonteCarloMatchesJamshidian()
        {
            const int paths = 40000;
            InterpolatedDiscountCurve curve = EurCurve();
            var swap = VanillaSwap.FromForwardTenor(Valuation, Period.Parse("1Y"), Period.Parse("4Y"), SwapConventions.EurSixMonth);
            DateOnly expiry = swap.StartDate;
            var paymentTimes = new List<double>();
            var accruals = new List<double>();
            foreach (AccrualPeriod period in swap.FixedPeriods)
            {
                paymentTimes.Add(curve.TimeFromReference(period.End));
                accruals.Add(period.YearFraction);
            }
            double strike = swap.ParRate(curve);

            // Analytic, on the host curve.
            var analytic = new HullWhite(MeanReversion, RateVolatility, curve.DiscountFactor, curve.InstantaneousForward);
            double expiryTime = curve.TimeFromReference(expiry);
            foreach (AccrualPeriod period in swap.FixedPeriods)
                Assert.Equal(curve.DiscountFactor(period.End), analytic.ZeroCouponBond(0.0, curve.TimeFromReference(period.End), 0.0), 1e-15);
            // Single curve: the fixed leg at the par rate is worth the floating leg 1 - P(T0, Tn) seen from 0.
            Assert.Equal(strike, analytic.ForwardSwapRate(expiryTime, paymentTimes, accruals), 1e-12);

            // Compilation.
            SimulationGrid grid = new SimulationGridBuilder(Valuation, DayCounter.Actual365Fixed)
                .WithMaxTimeStep(0.25).AddEventDate(expiry).Build();
            CompiledDiscountCurve compiled = MarketCompiler.CompileDiscountCurve(curve, grid);
            var model = new CompiledHullWhite(MeanReversion, RateVolatility, compiled, grid.ToTimesArray());
            int k = grid.PillarIndex(expiry);
            Assert.Equal(expiryTime, model.Time(k));

            // Monte Carlo.
            int steps = model.StepCount;
            var z1 = new double[steps];
            var z2 = new double[steps];
            var state = new double[steps + 1];
            var logDiscount = new double[steps + 1];
            var discount = new double[paths];
            var payer = new double[paths];
            var receiver = new double[paths];
            for (int p = 0; p < paths; ++p)
            {
                PhiloxNormals.FillPath(Seed, (uint)p, StateStream, z1, steps);
                PhiloxNormals.FillPath(Seed, (uint)p, IntegralStream, z2, steps);
                model.SimulatePath(z1, z2, state, logDiscount);
                double x = state[k], fixedLeg = 0.0;
                for (int i = 0; i < paymentTimes.Count; ++i)
                    fixedLeg += strike * accruals[i] * Math.Exp(model.LogZeroBond(k, x, paymentTimes[i]));
                double payerSwap = 1.0 - Math.Exp(model.LogZeroBond(k, x, paymentTimes[^1])) - fixedLeg;
                discount[p] = Math.Exp(logDiscount[k]);
                payer[p] = discount[p] * Math.Max(payerSwap, 0.0);
                receiver[p] = discount[p] * Math.Max(-payerSwap, 0.0);
            }
            var d = Estimate(discount);
            AssertWithin3Se(curve.DiscountFactor(expiry), d.Mean, d.StandardError, $"E[D(0, {expiry:yyyy-MM-dd})]");
            var mcPayer = Estimate(payer);
            AssertWithin3Se(analytic.Swaption(SwaptionType.Payer, expiryTime, paymentTimes, accruals, strike), mcPayer.Mean, mcPayer.StandardError, "payer 1Yx4Y ATM");
            var mcReceiver = Estimate(receiver);
            AssertWithin3Se(analytic.Swaption(SwaptionType.Receiver, expiryTime, paymentTimes, accruals, strike), mcReceiver.Mean, mcReceiver.StandardError, "receiver 1Yx4Y ATM");
        }

        [HybridizerFact]
        public void EquityCallUnderHullWhiteRates_KernelsMatchTheStochasticRateBlackPrice()
        {
            // Kernels only: HullWhiteModel fills the rate paths, EquityDiffusion reads HullWhiteRate as its drift, and the existing
            // EuropeanCallOption payoff discounts with HullWhiteDiscounting. With independent equity and rate Brownians, the price
            // is Black-76 on F = S0 / P(0, T) with total variance s^2 T + Var(int_0^T x ds).
            const int paths = 20011;
            const double spot = 100.0, strike = 105.0, equityVolatility = 0.2;
            InterpolatedDiscountCurve curve = EurCurve();
            DateOnly expiry = TargetCalendar.Instance.Adjust(Valuation.AddYears(2), BusinessDayConvention.Following);
            SimulationGrid grid = new SimulationGridBuilder(Valuation, DayCounter.Actual365Fixed)
                .WithMaxTimeStep(1.0 / 12.0).AddEventDate(expiry).Build();
            var schedule = new PrecalculatedSchedule(grid.ToTimesArray());
            var model = new CompiledHullWhite(MeanReversion, RateVolatility, MarketCompiler.CompileDiscountCurve(curve, grid), schedule);
            int steps = grid.StepCount;
            int maturity = grid.PillarIndex(expiry);

            ExecutorBackend backend = ExecutorBackends.Reference;
            var z1 = Allocation.Prime(new ArrayBrownian(paths, steps));
            var z2 = Allocation.Prime(new ArrayBrownian(paths, steps));
            var spotBrownian = Allocation.Prime(new ArrayBrownian(paths, steps));
            var state = Allocation.PrimeLogSpot(new ArrayDoubleSpot(paths, steps + 1));
            var logDiscount = Allocation.PrimeLogSpot(new ArrayDoubleSpot(paths, steps + 1));
            var logSpot = Allocation.PrimeLogSpot(new ArrayDoubleSpot(paths, steps + 1));
            var premium = Allocation.Prime(new ArrayPremium(paths));
            backend.Simulate(new PhiloxBrownianFill<ArrayBrownian>(Seed, StateStream, steps, z1), 0, paths);
            backend.Simulate(new PhiloxBrownianFill<ArrayBrownian>(Seed, IntegralStream, steps, z2), 0, paths);
            backend.Simulate(new PhiloxBrownianFill<ArrayBrownian>(Seed, SpotStream, steps, spotBrownian), 0, paths);
            for (int p = 0; p < paths; ++p)
                logSpot[p, 0] = Math.Log(spot);

            backend.Simulate(new HullWhiteModel<ArrayBrownian>(model, state, logDiscount, z1, z2), 0, paths);
            var rate = new HullWhiteRate(model, state, logDiscount);
            var discounting = new HullWhiteDiscounting(model, state, logDiscount);
            backend.Simulate(new RateDrivenDiffusion(new ConstantVolatility(equityVolatility), rate, logSpot, spotBrownian, schedule), 0, paths);
            backend.Simulate(
                new EuropeanCallOption<HullWhiteDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                    new LogBasedSpot<ArrayDoubleSpot>(logSpot), discounting, schedule, premium, maturity, strike),
                0, paths);

            var samples = new double[paths];
            for (int p = 0; p < paths; ++p) samples[p] = premium[p];
            var mc = Estimate(samples);
            double t = model.Time(maturity);
            double p0 = curve.DiscountFactor(expiry);
            double stdDev = Math.Sqrt(equityVolatility * equityVolatility * t + HullWhiteKernels.IntegralVariance(MeanReversion, RateVolatility, t));
            AssertWithin3Se(p0 * Black76Kernels.NormalisedPrice(1, spot / p0, strike, stdDev), mc.Mean, mc.StandardError, "equity call, Hull-White rates");
        }

        /// <summary>The equity diffusion with a Hull-White drift, over the paths [simFrom, simTo).</summary>
        private sealed class RateDrivenDiffusion : ISimulatable
        {
            private readonly ConstantVolatility _volatility;
            private readonly HullWhiteRate _rate;
            private readonly ArrayDoubleSpot _logSpot;
            private readonly ArrayBrownian _brownian;
            private readonly PrecalculatedSchedule _schedule;
            private readonly EquityDiffusion<ConstantVolatility, HullWhiteRate, ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule> _diffusion =
                new EquityDiffusion<ConstantVolatility, HullWhiteRate, ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule>();

            public RateDrivenDiffusion(ConstantVolatility volatility, HullWhiteRate rate, ArrayDoubleSpot logSpot, ArrayBrownian brownian,
                PrecalculatedSchedule schedule)
            {
                _volatility = volatility;
                _rate = rate;
                _logSpot = logSpot;
                _brownian = brownian;
                _schedule = schedule;
            }

            public void Simulate(int simFrom, int simTo)
            {
                _diffusion.Diffusion(simFrom, simTo, 0, _schedule.PilarCount, _volatility, _rate, _logSpot, _brownian, _schedule);
            }
        }
    }
}
