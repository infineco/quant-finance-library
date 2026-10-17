using System;
using InFine.QuantFinance.ClosedForm;
using Xunit;
using Xunit.Abstractions;

namespace InFine.QuantFinance.ClosedForm.Tests
{
    /// <summary>
    /// "Let's Be Rational" implied volatility: high-precision references for the normalised Black function and its
    /// inverse, dense round trips in both directions, out-of-range prices, put-call parity, textbook values and the
    /// batch kernel. Everything runs on plain doubles (no Hybridizer runtime needed).
    /// </summary>
    /// <remarks>
    /// Accuracy is measured against what the input can carry. If the price has relative error eps, the implied total
    /// volatility s is only determined to <c>(1 + |b / (s b')|) eps</c> (b the out-of-the-money normalised price,
    /// b' its derivative in s); for an in-the-money option the price also carries the intrinsic value, so the
    /// information left in the time value is <c>eps price / timeValue</c>.
    /// </remarks>
    public class ImpliedVolatilityTests
    {
        const double Eps = 2.220446049250313e-16;
        readonly ITestOutputHelper _output;

        public ImpliedVolatilityTests(ITestOutputHelper output) { _output = output; }

        static void Close(double expected, double actual, double rel, double abs, string what)
        {
            Assert.True(Math.Abs(expected - actual) <= abs + rel * Math.Abs(expected),
                $"{what}: expected {expected:R}, got {actual:R}");
        }

        /// <summary>b'(s) = exp(-(h^2 + t^2)/2) / sqrt(2 pi), h = x/s, t = s/2.</summary>
        static double NormalisedVega(double x, double s)
        {
            double h = x / s, t = 0.5 * s;
            return Math.Exp(-0.5 * (h * h + t * t)) / Math.Sqrt(2 * Math.PI);
        }

        // (x = ln(F/K) < 0, s = sigma sqrt(T), b(x, s) of a call) from 300-digit decimal arithmetic
        // (b = e^{x/2} Phi(x/s + s/2) - e^{-x/2} Phi(x/s - s/2), Phi from an erf series / erfc continued fraction),
        // rounded to the nearest double. Covers all four branches, the asymptotic and small-s regions of the
        // normalised Black function and the Householder(4) zones |x| > 190 and |x| > 580.
        public static readonly (double X, double S, double B)[] Reference =
        {
            (-0.001, 0.0001, 7.474560245421083e-29),
            (-0.5, 0.05, 3.7361342698631205e-26),
            (-2.0, 0.2, 1.487595408404447e-25),
            (-8.0, 1.0, 6.68671316739767e-17),
            (-5.0, 0.25, 3.398507918808902e-91),
            (-30.0, 2.0, 2.9556608552583065e-52),
            (-1.0, 2.5, 0.4050776053921129),
            (-0.1, 5.0, 0.9388120978196023),
            (-20.0, 12.0, 4.539940196184885e-05),
            (-40.0, 3.0, 5.4169095699679625e-42),
            (-0.02, 0.001, 1.3700123243196603e-93),
            (-1.0, 0.07, 6.514772070894537e-49),
            (-3.0, 0.3, 2.2177499398729317e-25),
            (-200.0, 15.0, 7.203931314627905e-53),
            (-250.0, 21.0, 3.7932288987428755e-56),
            (-600.0, 40.0, 5.148198528166702e-131),
            (-700.0, 42.0, 9.929508688120162e-153),
            (-10.0, 9.0, 0.006734090986448151),
            (-0.3, 0.03, 2.2421205468758902e-26),
            (-7.0, 0.7, 4.926987540705148e-25),
            (-1e-08, 1e-06, 3.939622273492118e-07),
            (-4.0, 2.9, 0.054246671484369985),
        };

        [Fact]
        public void NormalisedBlackMatchesHighPrecisionReference()
        {
            double worst = 0;
            foreach (var (x, s, b) in Reference)
            {
                double got = ImpliedVolatilityKernels.NormalisedBlack(1, x, s);
                // b(x, s) can only be as accurate as the rounding of x / s allows: (1 + |s b' / b|) eps, where s b'/b
                // grows like (x/s)^2 in the wings.
                double attainable = (1 + s * NormalisedVega(x, s) / b) * Eps;
                double err = Math.Abs(got - b) / b;
                worst = Math.Max(worst, err / attainable);
                Assert.True(err <= 8 * attainable, $"b({x}, {s}) = {got:R}, expected {b:R}, relative error {err:E2}");
                // put with the opposite moneyness is the same out-of-the-money option
                Assert.Equal(got, ImpliedVolatilityKernels.NormalisedBlack(-1, -x, s));
            }
            _output.WriteLine($"worst relative error of b(x, s) in units of the attainable accuracy: {worst:F2}");
        }

        [Fact]
        public void ImpliedStandardDeviationOfReferencePricesIsAccurate()
        {
            double worst = 0;
            foreach (var (x, s, b) in Reference)
            {
                double got = ImpliedVolatilityKernels.NormalisedStandardDeviation(1, b, x);
                double attainable = 1 + b / (s * NormalisedVega(x, s));
                double err = Math.Abs(got - s) / s;
                worst = Math.Max(worst, err / (Eps * attainable));
                Assert.True(err <= 4 * Eps * attainable, $"s({b:R}, {x}) = {got:R}, expected {s:R}, relative error {err:E2}");
                // same option seen as an in-the-money put: beta_put = beta_call + normalised intrinsic.
                double intrinsic = 2 * Math.Sinh(-0.5 * x);
                if (b > 1e-15 * intrinsic)
                {
                    double gotItm = ImpliedVolatilityKernels.NormalisedStandardDeviation(-1, b + intrinsic, x);
                    double itmAttainable = attainable * (b + intrinsic) / b;
                    Assert.True(Math.Abs(gotItm - s) / s <= 4 * Eps * itmAttainable,
                        $"ITM put s({b + intrinsic:R}, {x}) = {gotItm:R}, expected {s:R}");
                }
            }
            _output.WriteLine($"worst relative error of s in units of the attainable accuracy: {worst:F2}");
        }

        [Fact]
        public void VolatilityToPriceToVolatilityOnADenseGrid()
        {
            // x = ln(F/K) = k s with k in [-8, 8], s in [1e-4, 5]; calls and puts, so both OTM and ITM.
            const double F = 100.0, Df = 0.97;
            int checkedAt1e12 = 0, total = 0, outOfTheMoney = 0, outOfTheMoneyChecked = 0;
            double worst = 0;
            for (int i = 0; i <= 40; i++)
            {
                double s = 1e-4 * Math.Pow(5e4, i / 40.0);
                for (int j = 0; j <= 64; j++)
                {
                    double k = -8.0 + 0.25 * j;
                    double x = k * s;
                    double K = F * Math.Exp(-x);
                    foreach (int sign in new[] { 1, -1 })
                    {
                        double price = Df * ImpliedVolatilityKernels.UndiscountedBlack(sign, F, K, s);
                        double got = ImpliedVolatilityKernels.StandardDeviation(sign, price, F, K, Df);
                        double err = Math.Abs(got - s) / s;

                        // information left in the price: its time value relative to the price itself
                        double intrinsic = Math.Max(sign * (F - K), 0.0);
                        double otm = ImpliedVolatilityKernels.NormalisedBlack(1, -Math.Abs(x), s);
                        double timeValue = Math.Sqrt(F * K) * otm;
                        double attainable = (1 + otm / (s * NormalisedVega(-Math.Abs(x), s))) * (intrinsic + timeValue) / timeValue * Eps;
                        if (attainable > 1e-3)
                        {
                            // Time value below ~1e-13 of the intrinsic value: the price is the intrinsic value to
                            // double precision, and the result is NaN (below intrinsic after rounding), 0, +Infinity
                            // (price rounded up to the bound, e.g. F - K = F) or a poor s.
                            continue;
                        }
                        total++;
                        if (sign * x <= 0) outOfTheMoney++;
                        if (attainable <= 1e-13)
                        {
                            checkedAt1e12++;
                            if (sign * x <= 0) outOfTheMoneyChecked++;
                            worst = Math.Max(worst, err);
                            Assert.True(err <= 1e-12, $"sign={sign} x={x:R} s={s:R}: got {got:R}, relative error {err:E2}");
                        }
                        else
                        {
                            // The price no longer determines s to 1e-12 (deep in the money): stay within the attainable bound.
                            Assert.True(err <= 16 * attainable, $"sign={sign} x={x:R} s={s:R}: got {got:R}, error {err:E2} vs attainable {attainable:E2}");
                        }
                    }
                }
            }
            _output.WriteLine($"{checkedAt1e12} of {total} informative grid points checked at 1e-12, worst relative error {worst:E2}");
            // Every out-of-the-money (and at-the-money) point carries enough information for 1e-12; in the money
            // that holds until the time value falls to about 1e-3 of the price.
            Assert.Equal(outOfTheMoney, outOfTheMoneyChecked);
        }

        [Fact]
        public void PriceToVolatilityToPriceOnADenseGrid()
        {
            // Out-of-the-money normalised prices spread over (0, b_max) on a log scale near 0 and near b_max.
            double worst = 0;
            foreach (double x in new[] { -1e-6, -0.01, -0.3, -1.0, -3.0, -10.0, -40.0 })
            {
                double bMax = Math.Exp(0.5 * x);
                for (int i = 1; i < 60; i++)
                {
                    double u = i / 60.0;
                    foreach (double beta in new[] { bMax * Math.Pow(1e-200, 1 - u), bMax * (1 - Math.Pow(1e-15, u)) })
                    {
                        if (!(beta > 0 && beta < bMax)) continue;
                        double s = ImpliedVolatilityKernels.NormalisedStandardDeviation(1, beta, x);
                        double back = ImpliedVolatilityKernels.NormalisedBlack(1, x, s);
                        // relative accuracy attainable for b(s) when s is rounded: (1 + |s b'/b|) eps
                        double attainable = (1 + s * NormalisedVega(x, s) / beta) * Eps;
                        double err = Math.Abs(back - beta) / beta;
                        worst = Math.Max(worst, err / attainable);
                        Assert.True(err <= 8 * attainable, $"x={x} beta={beta:R}: s={s:R}, b(s)={back:R}, error {err:E2} vs {attainable:E2}");
                    }
                }
            }
            _output.WriteLine($"worst price round-trip error in units of the attainable accuracy: {worst:F2}");
        }

        [Fact]
        public void ExtremeWingsAndTinyTimeValues()
        {
            // |x| up to 10 total-vol units at very small and very large s, and ITM options with time value
            // down to ~1e-12 of the intrinsic value.
            foreach (double s in new[] { 1e-6, 1e-3, 0.5, 3.0 })
            foreach (double k in new[] { -10.0, -9.5, 9.5, 10.0 })
            {
                double x = k * s;
                foreach (int sign in new[] { 1, -1 })
                {
                    double beta = ImpliedVolatilityKernels.NormalisedBlack(sign, x, s);
                    double intrinsic = Math.Max(2 * Math.Sinh(0.5 * sign * x), 0.0);
                    double tv = beta - intrinsic;
                    if (tv <= 1e-12 * intrinsic) continue; // no information left in the price
                    double got = ImpliedVolatilityKernels.NormalisedStandardDeviation(sign, beta, x);
                    double attainable = (1 + tv / (s * NormalisedVega(-Math.Abs(x), s))) * beta / tv * Eps;
                    Assert.True(Math.Abs(got - s) / s <= Math.Max(1e-12, 16 * attainable),
                        $"sign={sign} x={x:R} s={s:R}: got {got:R}");
                }
            }
        }

        [Fact]
        public void OutOfRangePricesGiveDocumentedSentinels()
        {
            const double F = 100, K = 90, Df = 0.95, T = 2;
            double intrinsic = Df * (F - K);
            // below the intrinsic value: NaN; exactly at it: 0
            Assert.True(double.IsNaN(ImpliedVolatilityKernels.Black76(1, 0.999 * intrinsic, F, K, Df, T)));
            Assert.Equal(0.0, ImpliedVolatilityKernels.Black76(1, intrinsic, F, K, Df, T));
            Assert.Equal(0.0, ImpliedVolatilityKernels.Black76(-1, 0.0, F, K, Df, T)); // OTM put worth nothing
            Assert.True(double.IsNaN(ImpliedVolatilityKernels.Black76(-1, -1e-10, F, K, Df, T)));
            // at or above the upper bound: +Infinity (call: DF F, put: DF K)
            Assert.Equal(double.PositiveInfinity, ImpliedVolatilityKernels.Black76(1, Df * F, F, K, Df, T));
            Assert.Equal(double.PositiveInfinity, ImpliedVolatilityKernels.Black76(1, 2 * Df * F, F, K, Df, T));
            Assert.Equal(double.PositiveInfinity, ImpliedVolatilityKernels.Black76(-1, Df * K, F, K, Df, T));
            // just below the upper bound: finite and large
            double big = ImpliedVolatilityKernels.Black76(1, Df * F * (1 - 1e-12), F, K, Df, T);
            Assert.True(big > 1 && !double.IsInfinity(big), $"near-maximum price gave {big}");
            // invalid inputs: NaN
            Assert.True(double.IsNaN(ImpliedVolatilityKernels.Black76(1, double.NaN, F, K, Df, T)));
            Assert.True(double.IsNaN(ImpliedVolatilityKernels.Black76(1, 5, 0.0, K, Df, T)));
            Assert.True(double.IsNaN(ImpliedVolatilityKernels.Black76(1, 5, F, -1, Df, T)));
            Assert.True(double.IsNaN(ImpliedVolatilityKernels.Black76(1, 5, F, K, 0.0, T)));
            Assert.True(double.IsNaN(ImpliedVolatilityKernels.Black76(1, 5, F, K, Df, 0.0)));
            Assert.True(double.IsNaN(ImpliedVolatilityKernels.Black76(0, 5, F, K, Df, T)));
            Assert.True(double.IsNaN(ImpliedVolatilityKernels.NormalisedStandardDeviation(1, 0.1, double.NaN)));
            // the host API reports the same sentinels and throws on invalid market data
            Assert.True(double.IsNaN(ImpliedVolatility.Black76(F, K, T, Df, 0.5 * intrinsic, OptionType.Call)));
            Assert.Equal(double.PositiveInfinity, ImpliedVolatility.Black76(F, K, T, Df, Df * K, OptionType.Put));
            Assert.Throws<ArgumentOutOfRangeException>(() => ImpliedVolatility.Black76(F, K, T, Df, double.NaN, OptionType.Call));
            Assert.Throws<ArgumentOutOfRangeException>(() => ImpliedVolatility.Black76(F, K, T, Df, -1, OptionType.Call));
            Assert.Throws<ArgumentOutOfRangeException>(() => ImpliedVolatility.Black76(0, K, T, Df, 5, OptionType.Call));
            Assert.Throws<ArgumentOutOfRangeException>(() => ImpliedVolatility.Black76(F, K, 0, Df, 5, OptionType.Call));
            Assert.Throws<ArgumentOutOfRangeException>(() => ImpliedVolatility.Black76(F, K, T, Df, 5, (OptionType)0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ImpliedVolatility.BlackScholes(100, 100, 1, double.NaN, 0, 5, OptionType.Call));
            Assert.Throws<ArgumentException>(() => ImpliedVolatility.Black76(new[] { F }, new[] { K }, new[] { T }, new[] { Df }, new[] { 5.0, 6.0 }, new[] { OptionType.Call }));
        }

        [Fact]
        public void TextbookValuesAreRecovered()
        {
            // Hull: S=42, K=40, r=10%, T=0.5, sigma=20%: call 4.759422392871535, put 0.8085993729000958.
            Close(0.2, ImpliedVolatility.BlackScholes(42, 40, 0.5, 0.10, 0.0, 4.759422392871535, OptionType.Call), 1e-13, 0, "Hull call");
            Close(0.2, ImpliedVolatility.BlackScholes(42, 40, 0.5, 0.10, 0.0, 0.8085993729000958, OptionType.Put), 1e-13, 0, "Hull put");
            // Haug Black-76: F=19, K=19, T=0.75, r=10%, sigma=28%: call = put = 1.7010507252362679 (exactly at the money).
            double df = Math.Exp(-0.1 * 0.75);
            Close(0.28, ImpliedVolatility.Black76(19, 19, 0.75, df, 1.7010507252362679, OptionType.Call), 1e-14, 0, "Haug ATM call");
            Close(0.28, ImpliedVolatility.Black76(19, 19, 0.75, df, 1.7010507252362679, OptionType.Put), 1e-14, 0, "Haug ATM put");
            // Haug generalised BSM: S=60, K=65, T=0.25, r=8%, sigma=30%: call 2.1333684449162043.
            Close(0.3, ImpliedVolatility.BlackScholes(60, 65, 0.25, 0.08, 0.0, 2.1333684449162043, OptionType.Call), 1e-13, 0, "Haug call");
            // Total volatility entry point
            Close(0.28 * Math.Sqrt(0.75), ImpliedVolatility.Black76StandardDeviation(19, 19, df, 1.7010507252362679, OptionType.Call), 1e-14, 0, "ATM s");
        }

        [Fact]
        public void PutCallParityGivesTheSameVolatility()
        {
            var rng = new System.Random(20251011);
            for (int n = 0; n < 500; n++)
            {
                double F = 50 + 100 * rng.NextDouble(), K = 50 + 100 * rng.NextDouble();
                double T = 0.05 + 5 * rng.NextDouble(), sigma = 0.05 + 0.8 * rng.NextDouble();
                double df = Math.Exp(-0.03 * T);
                double call = df * ImpliedVolatilityKernels.UndiscountedBlack(1, F, K, sigma * Math.Sqrt(T));
                double put = call - df * (F - K); // put-call parity
                if (put <= 0) continue;
                double vc = ImpliedVolatility.Black76(F, K, T, df, call, OptionType.Call);
                double vp = ImpliedVolatility.Black76(F, K, T, df, put, OptionType.Put);
                // the in-the-money side's time value is the other side's price
                double ratio = Math.Max(call, put) / Math.Min(call, put);
                Close(sigma, vc, 1e-12 + 1e-14 * ratio, 0, "call");
                Close(vc, vp, 1e-12 + 1e-14 * ratio, 0, $"put vs call F={F} K={K} T={T} sigma={sigma}");
            }
        }

        [Fact]
        public void AgreesWithTheBlack76PricerInTheUsualRange()
        {
            // Prices from Black76Kernels (two-term formula) invert to the input volatility.
            foreach (double K in new[] { 60.0, 90.0, 100.0, 110.0, 160.0 })
            foreach (double T in new[] { 0.1, 1.0, 10.0 })
            foreach (double sigma in new[] { 0.05, 0.2, 0.6, 1.5 })
            foreach (var type in new[] { OptionType.Call, OptionType.Put })
            {
                double df = Math.Exp(-0.02 * T);
                double price = Black76.Price(type, 100, K, df, T, sigma);
                // the two-term formula loses relative accuracy far out of the money; deep in the money the time value
                // is a small part of the price
                double timeValue = price - df * Math.Max((int)type * (100 - K), 0.0);
                if (price < 1e-10 || timeValue < 1e-4 * price) continue;
                double vol = ImpliedVolatility.Black76(100, K, T, df, price, type);
                Close(sigma, vol, 1e-10, 0, $"{type} K={K} T={T} sigma={sigma}");
                Close(price, ImpliedVolatilityKernels.UndiscountedBlack((int)type, 100, K, sigma * Math.Sqrt(T)) * df, 1e-10, 0, "pricers agree");
            }
        }

        [Fact]
        public void BlackScholesIsBlack76OnTheForward()
        {
            double S = 100, K = 95, T = 1.5, r = 0.04, q = 0.015, sigma = 0.33;
            foreach (var type in new[] { OptionType.Call, OptionType.Put })
            {
                double price = BlackScholes.Price(type, S, K, T, r, q, sigma);
                Close(sigma, ImpliedVolatility.BlackScholes(S, K, T, r, q, price, type), 1e-13, 0, type.ToString());
                double fwd = S * Math.Exp((r - q) * T), df = Math.Exp(-r * T);
                Assert.Equal(ImpliedVolatilityKernels.Black76((int)type, price, fwd, K, df, T),
                    ImpliedVolatilityKernels.BlackScholes((int)type, price, S, K, T, r, q));
            }
        }

        [Fact]
        public void BatchEqualsScalar()
        {
            var rng = new System.Random(4242);
            const int n = 300;
            var forward = new double[n]; var strike = new double[n]; var maturity = new double[n];
            var df = new double[n]; var price = new double[n]; var type = new OptionType[n]; var sign = new int[n];
            for (int i = 0; i < n; i++)
            {
                forward[i] = 50 + 100 * rng.NextDouble();
                strike[i] = 50 + 100 * rng.NextDouble();
                maturity[i] = 0.01 + 5 * rng.NextDouble();
                df[i] = Math.Exp(-0.03 * maturity[i]);
                type[i] = rng.Next(2) == 0 ? OptionType.Call : OptionType.Put;
                sign[i] = (int)type[i];
                double sigma = 0.01 + rng.NextDouble();
                price[i] = df[i] * ImpliedVolatilityKernels.UndiscountedBlack(sign[i], forward[i], strike[i], sigma * Math.Sqrt(maturity[i]));
                if (i % 50 == 0) price[i] = 0.0; // a few degenerate entries
            }
            double[] host = ImpliedVolatility.Black76(forward, strike, maturity, df, price, type);
            var kernel = new double[n];
            for (int i = 0; i < n; i++)
                ImpliedVolatilityKernels.Black76At(i, kernel, sign, price, forward, strike, df, maturity);
            for (int i = 0; i < n; i++)
            {
                double scalar = ImpliedVolatility.Black76(forward[i], strike[i], maturity[i], df[i], price[i], type[i]);
                Assert.Equal(scalar, host[i]);
                Assert.Equal(scalar, kernel[i]);
            }
        }
    }
}
