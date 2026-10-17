using System;
using System.Linq;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Market;
using Xunit;
using static InFine.QuantFinance.Volatility.Tests.TestHelpers;

namespace InFine.QuantFinance.Volatility.Tests
{
    /// <summary>
    /// Surfaces: interpolation and extrapolation in expiry (monotone total variance, flat vol at fixed moneyness
    /// outside the pillars), forwards from yield curves, and Black-76 prices.
    /// </summary>
    public class VolatilitySurfaceTests
    {
        static readonly double[] Maturities = { 0.25, 1.0, 2.0 };
        static readonly SviRawParameters[] Pillars =
        {
            new SviRawParameters(0.005, 0.05, -0.5, 0.02, 0.15),
            new SviRawParameters(0.025, 0.09, -0.45, 0.05, 0.25),
            new SviRawParameters(0.055, 0.11, -0.4, 0.08, 0.3),
        };

        static readonly double[] Times = Enumerable.Range(1, 120).Select(i => 0.05 * i).ToArray(); // 0.05 .. 6, both sides of the pillars
        static readonly double[] Moneyness = ArbitrageChecks.Grid(-3, 3, 121);

        [Fact]
        public void SviSurfaceIsMonotoneInExpiryAndExactAtThePillars()
        {
            var surface = new SviVolatilitySurface(new ConstantForwardCurve(100), Maturities, Pillars);
            Assert.All(surface.CheckCalendar(Moneyness), r => Assert.True(r.IsArbitrageFree));
            foreach (double k in Moneyness)
            {
                Assert.Equal(0.0, surface.TotalVariance(0.0, k));
                double previous = 0.0;
                foreach (double t in Times)
                {
                    double w = surface.TotalVariance(t, k);
                    Assert.True(w >= previous, $"w({t}, {k}) = {w} < {previous}");
                    previous = w;
                }
                for (int i = 0; i < Maturities.Length; i++)
                    Close(Pillars[i].TotalVariance(k), surface.TotalVariance(Maturities[i], k), 1e-15, 0, $"pillar {i}");
                // Linear in total variance between pillars.
                Close(0.5 * (Pillars[1].TotalVariance(k) + Pillars[2].TotalVariance(k)), surface.TotalVariance(1.5, k), 1e-15, 0, "mid");
            }
        }

        [Fact]
        public void SviSurfaceExtrapolatesFlatVolatilityAtFixedMoneyness()
        {
            var surface = new SviVolatilitySurface(new ConstantForwardCurve(100), Maturities, Pillars);
            foreach (double strike in new[] { 60.0, 90.0, 100.0, 120.0, 200.0 })
            {
                double k = Math.Log(strike / 100);
                double first = Math.Sqrt(Pillars[0].TotalVariance(k) / Maturities[0]);
                double last = Math.Sqrt(Pillars[2].TotalVariance(k) / Maturities[2]);
                foreach (double t in new[] { 0.01, 0.1, 0.25 }) Close(first, surface.ImpliedVolatility(t, strike), 1e-14, 0, $"short {t}");
                foreach (double t in new[] { 2.0, 5.0, 30.0 }) Close(last, surface.ImpliedVolatility(t, strike), 1e-14, 0, $"long {t}");
            }
            // Derivatives of an interpolated slice are the blend of the pillar derivatives.
            var slice = surface.Slice(0.625); // halfway between 0.25 and 1.0
            Close(0.5 * (Pillars[0].TotalVarianceSecondDerivative(0.1) + Pillars[1].TotalVarianceSecondDerivative(0.1)),
                slice.TotalVarianceSecondDerivative(0.1), 1e-14, 0, "w''");
        }

        [Fact]
        public void SsviSurfaceInterpolatesThetaAndIsMonotone()
        {
            var p = new SsviParameters(-0.6, 1.2, 0.45);
            double[] thetas = { 0.01, 0.04, 0.07 };
            var surface = new SsviVolatilitySurface(new ConstantForwardCurve(100), p, Maturities, thetas);
            Assert.True(surface.SatisfiesSufficientNoArbitrageConditions);
            Close(0.5 * (0.04 + 0.07), surface.AtTheMoneyTotalVariance(1.5), 1e-15, 0, "theta mid");
            Close(0.01 * 0.1 / 0.25, surface.AtTheMoneyTotalVariance(0.1), 1e-15, 0, "theta short");
            Close(0.07 * 4.0 / 2.0, surface.AtTheMoneyTotalVariance(4.0), 1e-15, 0, "theta long");
            Close(Math.Sqrt(0.07 / 2.0), surface.ImpliedVolatility(10.0, 100.0), 1e-14, 0, "flat ATM vol beyond the last pillar");
            foreach (double k in Moneyness)
            {
                Assert.Equal(0.0, surface.TotalVariance(0.0, k));
                double previous = 0.0;
                foreach (double t in Times)
                {
                    double w = surface.TotalVariance(t, k);
                    Assert.True(w >= previous, $"w({t}, {k}) = {w} < {previous}");
                    previous = w;
                }
            }

            var decreasing = new SsviVolatilitySurface(new ConstantForwardCurve(100), p, Maturities, new[] { 0.01, 0.05, 0.04 });
            Assert.False(decreasing.IsAtTheMoneyVarianceNonDecreasing);
            Assert.False(decreasing.SatisfiesSufficientNoArbitrageConditions);
            Assert.False(ArbitrageChecks.CheckCalendar(decreasing.Slice(1.0), decreasing.Slice(2.0), Moneyness).IsArbitrageFree);
        }

        [Fact]
        public void PricesAreBlack76OnTheCurveForward()
        {
            // Forward S exp((r - q) t) from flat rate and dividend curves; date overload reads time and discount
            // factor from the discount curve (Actual/365F, so t = days / 365).
            var reference = new DateOnly(2026, 1, 15);
            var rates = new FlatForward(reference, 0.03, DayCounter.Actual365Fixed);
            var dividends = new FlatForward(reference, 0.01, DayCounter.Actual365Fixed);
            var forwards = new SpotForwardCurve(100.0, rates, dividends);
            var surface = new SviVolatilitySurface(forwards, Maturities, Pillars);

            var expiry = reference.AddDays(365);
            double t = 1.0, forward = 100.0 * Math.Exp(0.02 * t), df = Math.Exp(-0.03 * t);
            Close(forward, forwards.Forward(t), 1e-15, 0, "forward");
            foreach (double strike in new[] { 80.0, 100.0, 125.0 })
            {
                double vol = Math.Sqrt(Pillars[1].TotalVariance(Math.Log(strike / forward)) / t);
                Close(vol, surface.ImpliedVolatility(t, strike), 1e-14, 0, "vol");
                double call = surface.Price(OptionType.Call, expiry, strike, rates);
                double put = surface.Price(OptionType.Put, t, strike, df);
                Close(Black76.Price(OptionType.Call, forward, strike, df, t, vol), call, 1e-13, 1e-14, "call");
                Close(df * (forward - strike), call - put, 0, 1e-12, "put-call parity");
            }
            // At expiry: intrinsic value of the forward.
            Close(Math.Max(forwards.Forward(0) - 90.0, 0.0), surface.Price(OptionType.Call, 0.0, 90.0, 1.0), 0, 1e-14, "t = 0");
        }

        [Fact]
        public void ConstructorsValidate()
        {
            var f = new ConstantForwardCurve(100);
            Assert.Throws<ArgumentException>(() => new SviVolatilitySurface(f, new[] { 1.0, 0.5 }, Pillars.Take(2).ToArray()));
            Assert.Throws<ArgumentException>(() => new SviVolatilitySurface(f, Maturities, Pillars.Take(2).ToArray()));
            Assert.Throws<ArgumentException>(() => new SviVolatilitySurface(f, new[] { 1.0 }, new[] { default(SviRawParameters) }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SsviVolatilitySurface(f, new SsviParameters(0, 1, 0.5), new[] { 1.0 }, new[] { 0.0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ConstantForwardCurve(-1));
            var surface = new SviVolatilitySurface(f, Maturities, Pillars);
            Assert.Throws<ArgumentOutOfRangeException>(() => surface.ImpliedVolatility(0.0, 100));
            Assert.Throws<ArgumentOutOfRangeException>(() => surface.TotalVariance(-1.0, 0));
        }
    }
}
