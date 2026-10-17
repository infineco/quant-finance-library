using System;
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
    /// End to end through the host-to-kernel seam: a European call defined with real dates (valuation 2025-01-15, expiry
    /// 2026-01-17 adjusted on TARGET, Act/365F), a bootstrapped EUR curve, monthly observation dates refined to steps of at
    /// most half a month, compiled into a kernel schedule, <see cref="CurveRate"/> drift and <see cref="CurveDiscounting"/>;
    /// Black-Scholes log-Euler paths from Philox normals through the generic <see cref="EquityDiffusion{Volatility,Rate,LogSpot,Brownian,Schedule}"/>
    /// kernel, then the <see cref="EuropeanCallOption{Discounting,Spot,Schedule,Premium}"/> payoff kernel.
    /// <para>
    /// Reference: with step forwards, <c>sum r_k dt_k = -ln P(0, T)</c>, so the terminal spot is exactly lognormal with the
    /// drift of the curve zero rate <c>z(T)</c> and the Black-Scholes closed form with rate <c>z(T)</c> is the exact price.
    /// The Monte Carlo price must be within 3 standard errors of it. On every pillar <c>k</c> the mean discounted spot
    /// <c>P(0, t_k) S_k</c> must be within 4 standard errors of <c>S_0</c> (about 30 correlated checks: 4 sigma keeps a
    /// Bonferroni false-alarm bound of 30 * 6.3e-5 = 0.2%, and the seed is fixed so the outcome is deterministic).
    /// </para>
    /// </summary>
    public class Test_MarketCompilation
    {
        private const int Paths = 20011;
        private const double Spot = 100.0;
        private const double Strike = 105.0;
        private const double Volatility = 0.25;

        private static readonly DateOnly Valuation = new DateOnly(2025, 1, 15);

        private readonly ITestOutputHelper _output;

        public Test_MarketCompilation(ITestOutputHelper output)
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
            foreach (var (tenor, quote) in new[] { ("1Y", 0.0245), ("2Y", 0.0230), ("3Y", 0.0228) })
                builder.Add(VanillaSwap.FromTenor(Valuation, Period.Parse(tenor), SwapConventions.EurSixMonth), quote);
            return builder.Build();
        }

        [HybridizerFact]
        public void EuropeanCallWithDatesAndACurve_MatchesBlackScholes_AndDiscountedSpotIsAMartingale()
        {
            // Host model: dates, calendar, conventions, curve.
            var calendar = TargetCalendar.Instance;
            DateOnly expiry = calendar.Adjust(new DateOnly(2026, 1, 17), BusinessDayConvention.Following);
            Assert.Equal(new DateOnly(2026, 1, 19), expiry); // 17 January 2026 is a Saturday
            var gridBuilder = new SimulationGridBuilder(Valuation, DayCounter.Actual365Fixed).WithMaxTimeStep(1.0 / 24.0);
            for (int month = 1; month <= 11; ++month)
                gridBuilder.AddEventDate(calendar.Adjust(Valuation.AddMonths(month), BusinessDayConvention.ModifiedFollowing));
            SimulationGrid grid = gridBuilder.AddEventDate(expiry).Build();
            InterpolatedDiscountCurve curve = EurCurve();

            // Compilation: everything below sees only times in years and flat arrays.
            CompiledDiscountCurve compiled = MarketCompiler.CompileDiscountCurve(curve, grid);
            var schedule = new PrecalculatedSchedule(grid.ToTimesArray());
            var discounting = new CurveDiscounting(compiled, schedule);
            var rate = new CurveRate(compiled, schedule);
            int steps = grid.StepCount;
            int maturity = grid.PillarIndex(expiry);
            Assert.Equal(steps, maturity);
            Assert.True(steps >= 24, $"{steps} steps");
            double expiryTime = schedule.getT(maturity);
            Assert.Equal(DayCounter.Actual365Fixed.YearFraction(Valuation, expiry), expiryTime);
            Assert.Equal(curve.DiscountFactor(expiry), discounting[0, maturity]);

            // Kernels.
            ExecutorBackend backend = ExecutorBackends.Reference;
            var logSpot = Allocation.PrimeLogSpot(new ArrayDoubleSpot(Paths, steps + 1));
            var brownian = Allocation.Prime(new ArrayBrownian(Paths, steps));
            var premium = Allocation.Prime(new ArrayPremium(Paths));
            for (int p = 0; p < Paths; ++p)
                logSpot[p, 0] = Math.Log(Spot);
            backend.Simulate(new PhiloxBrownianFill<ArrayBrownian>(Market.Seed, Market.SpotStream, steps, brownian), 0, Paths);
            backend.Simulate(new CurveDrivenDiffusion(new ConstantVolatility(Volatility), rate, logSpot, brownian, schedule), 0, Paths);
            var spot = new LogBasedSpot<ArrayDoubleSpot>(logSpot);
            backend.Simulate(
                new EuropeanCallOption<CurveDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                    spot, discounting, schedule, premium, maturity, Strike),
                0, Paths);

            // Price against the closed form at the curve zero rate to expiry.
            double sum = 0.0, sumSq = 0.0;
            for (int p = 0; p < Paths; ++p)
            {
                sum += premium[p];
                sumSq += premium[p] * premium[p];
            }
            double price = sum / Paths;
            double standardError = Math.Sqrt((sumSq / Paths - price * price) / (Paths - 1.0));
            double zero = curve.ZeroRate(expiryTime);
            BlackScholesMertonPrimitive.CallPut(out double call, out double _, Spot, Strike, zero, Volatility, expiryTime);
            Assert.True(standardError > 0.0 && standardError < 0.15, $"standard error {standardError}");
            Assert.True(Math.Abs(price - call) <= 3.0 * standardError,
                $"Monte Carlo {price} vs Black-Scholes {call} (zero rate {zero}, T {expiryTime}): {(price - call) / standardError:F2} standard errors");

            // Discounted spot is a martingale on every pillar.
            double worst = 0.0;
            for (int k = 0; k <= steps; ++k)
            {
                double df = discounting[0, k];
                double mean = 0.0, meanSq = 0.0;
                for (int p = 0; p < Paths; ++p)
                {
                    double x = df * spot[p, k];
                    mean += x;
                    meanSq += x * x;
                }
                mean /= Paths;
                // At pillar 0 every path holds S_0: the variance is zero up to rounding, and the mean of 20011 equal terms
                // is off by at most n * eps * S_0 = 2.2e-10.
                double se = Math.Sqrt(Math.Max(0.0, meanSq / Paths - mean * mean) / (Paths - 1.0));
                worst = Math.Max(worst, se > 0.0 ? Math.Abs(mean - Spot) / se : 0.0);
                Assert.True(Math.Abs(mean - Spot) <= 4.0 * se + 1e-9,
                    $"pillar {k} (t = {schedule.getT(k)}): E[P S] = {mean} vs {Spot}, {(mean - Spot) / se:F2} standard errors");
            }
            _output.WriteLine($"{steps} steps, T = {expiryTime}, z(T) = {zero}: Monte Carlo {price} +/- {standardError} vs Black-Scholes {call} "
                + $"({(price - call) / standardError:F2} SE); worst martingale deviation {worst:F2} SE");
        }

        /// <summary>The equity diffusion with a curve-driven drift, over the paths [simFrom, simTo).</summary>
        private sealed class CurveDrivenDiffusion : ISimulatable
        {
            private readonly ConstantVolatility _volatility;
            private readonly CurveRate _rate;
            private readonly ArrayDoubleSpot _logSpot;
            private readonly ArrayBrownian _brownian;
            private readonly PrecalculatedSchedule _schedule;
            private readonly EquityDiffusion<ConstantVolatility, CurveRate, ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule> _diffusion =
                new EquityDiffusion<ConstantVolatility, CurveRate, ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule>();

            public CurveDrivenDiffusion(ConstantVolatility volatility, CurveRate rate, ArrayDoubleSpot logSpot, ArrayBrownian brownian,
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
