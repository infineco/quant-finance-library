using System;
using System.Collections.Generic;
using InFine.QuantFinance.ClosedForm;
using Xunit;

namespace InFine.QuantFinance.ClosedForm.Tests
{
    /// <summary>
    /// Black-Scholes-Merton and Black-76: textbook and high-precision reference values, analytic Greeks against
    /// finite differences of the price, parity relations, degenerate limits and batch/scalar agreement.
    /// All of it runs on plain doubles, so it does not need the Hybridizer runtime.
    /// </summary>
    public class BlackScholesTests
    {
        static void Close(double expected, double actual, double rel, double abs, string what)
        {
            Assert.True(Math.Abs(expected - actual) <= abs + rel * Math.Abs(expected),
                $"{what}: expected {expected:R}, got {actual:R}");
        }

        // ---- reference prices --------------------------------------------------------------------------------

        [Fact]
        public void HullTextbookExample()
        {
            // Hull, Options, Futures and Other Derivatives: S=42, K=40, r=10%, sigma=20%, T=0.5 -> call 4.76, put 0.81.
            double call = BlackScholes.Price(OptionType.Call, 42, 40, 0.5, 0.10, 0.0, 0.20);
            double put = BlackScholes.Price(OptionType.Put, 42, 40, 0.5, 0.10, 0.0, 0.20);
            Close(4.76, call, 0, 5e-3, "Hull call");
            Close(0.81, put, 0, 5e-3, "Hull put");
            // Full precision: erfc-based Python computation (math.erfc).
            Close(4.759422392871535, call, 1e-13, 0, "call");
            Close(0.8085993729000958, put, 1e-13, 0, "put");
        }

        [Fact]
        public void HaugGeneralisedBsmStockOptionExample()
        {
            // Haug, The Complete Guide to Option Pricing Formulas: S=60, K=65, T=0.25, r=8%, b=r (no dividend),
            // sigma=30% -> call 2.1334, put 5.8463 (4 decimals; full precision from math.erfc).
            double call = BlackScholes.Price(OptionType.Call, 60, 65, 0.25, 0.08, 0.0, 0.30);
            double put = BlackScholes.Price(OptionType.Put, 60, 65, 0.25, 0.08, 0.0, 0.30);
            Close(2.1334, call, 0, 5e-5, "Haug call");
            Close(5.8463, put, 0, 5e-5, "Haug put");
            Close(2.1333684449162043, call, 1e-13, 0, "call");
            Close(5.846282209855296, put, 1e-13, 0, "put");
        }

        [Fact]
        public void HaugBlack76Example()
        {
            // Haug: F=19, K=19, T=0.75, r=10%, sigma=28%: call = put = 1.7011.
            double call = Black76.Price(OptionType.Call, 19, 19, Math.Exp(-0.1 * 0.75), 0.75, 0.28);
            double put = Black76.Price(OptionType.Put, 19, 19, Math.Exp(-0.1 * 0.75), 0.75, 0.28);
            Close(1.7011, put, 0, 5e-5, "Haug Black-76 put");
            Close(1.7010507252362679, call, 1e-13, 0, "call");
            Close(1.7010507252362679, put, 1e-13, 0, "put");
        }

        [Fact]
        public void Black76EqualsBsmWithDividendYieldEqualToRate()
        {
            // With q = r the forward is the spot: BSM and Black-76 on F = S are the same option.
            foreach (var type in new[] { OptionType.Call, OptionType.Put })
            {
                double bsm = BlackScholes.Price(type, 19, 17.5, 0.75, 0.1, 0.1, 0.28);
                double b76 = Black76.Price(type, 19, 17.5, Math.Exp(-0.1 * 0.75), 0.75, 0.28);
                Close(bsm, b76, 1e-14, 0, "bsm vs black76");
            }
        }

        [Fact]
        public void NormalisedBlackMatchesDiscountedForm()
        {
            double v = 0.3 * Math.Sqrt(2.0);
            double c = Black76.NormalisedPrice(OptionType.Call, 105, 100, v);
            double p = Black76.NormalisedPrice(OptionType.Put, 105, 100, v);
            Close(0.97 * c, Black76.Price(OptionType.Call, 105, 100, 0.97, 2.0, 0.3), 1e-14, 0, "call");
            Close(0.97 * p, Black76.Price(OptionType.Put, 105, 100, 0.97, 2.0, 0.3), 1e-14, 0, "put");
            // undiscounted parity: C - P = F - K
            Close(5.0, c - p, 1e-13, 0, "parity");
            // d(price)/dv = F phi(d1), against a central difference
            double h = 1e-4;
            double fd = (Black76.NormalisedPrice(OptionType.Call, 105, 100, v + h)
                       - Black76.NormalisedPrice(OptionType.Call, 105, 100, v - h)) / (2 * h);
            Close(fd, Black76Kernels.NormalisedVega(105, 100, v), 1e-7, 0, "normalised vega");
        }

        [Fact]
        public void DeepOutOfTheMoneyKeepsRelativeAccuracy()
        {
            // 250-digit Python decimal reference (Taylor series of erf). The d's are about -10.9 and -12.9: a
            // 1 - Phi implementation would return 0 or noise here. Tolerance 1e-10: the call is a difference of two
            // tails that agree to about 1 - 1/100, so the 1e-14 error of Phi is amplified by about 100.
            Close(4.55743002220578181e-28, BlackScholes.Price(OptionType.Call, 100, 300, 0.25, 0.01, 0.0, 0.2), 1e-10, 0, "OTM call");
            Close(3.62382521195218607e-34, BlackScholes.Price(OptionType.Put, 100, 30, 0.25, 0.01, 0.0, 0.2), 1e-10, 0, "OTM put");
            Close(1.34022727621883593e-38, BlackScholes.Price(OptionType.Call, 100, 400, 0.5, 0.03, 0.01, 0.15), 1e-10, 0, "OTM call, q>0");
            // Deep in the money.
            Close(1.99250936719238041e+02, BlackScholes.Price(OptionType.Put, 100, 300, 0.25, 0.01, 0.0, 0.2), 1e-13, 0, "ITM put");
            Close(7.00749063280761959e+01, BlackScholes.Price(OptionType.Call, 100, 30, 0.25, 0.01, 0.0, 0.2), 1e-13, 0, "ITM call");
        }

        [Fact]
        public void PrimitiveCallPutDelegatesToTheKernel()
        {
            BlackScholesMertonPrimitive.CallPut(out double call, out double put, 42, 40, 0.1, 0.2, 0.5);
            Close(4.759422392871535, call, 1e-13, 0, "call");
            Close(0.8085993729000958, put, 1e-13, 0, "put");

            var callArr = new double[1];
            var putArr = new double[1];
            BlackScholesMertonPrimitive.CallPut(0, callArr, putArr, 42, 40, 0.1, 0.2, 0.5);
            Assert.Equal(call, callArr[0]);
            Assert.Equal(put, putArr[0]);
        }

        // ---- Greeks against golden values --------------------------------------------------------------------

        [Fact]
        public void GreeksMatchIndependentPythonValues()
        {
            // S=100 K=95 T=1.5 r=4% q=1.5% sigma=25%; values from an independent Python implementation (math.erfc).
            var c = BlackScholes.Greeks(OptionType.Call, 100, 95, 1.5, 0.04, 0.015, 0.25);
            Close(16.016721651069076, c.Price, 1e-13, 0, "call price");
            Close(0.6562177117028573, c.Delta, 1e-13, 0, "call delta");
            Close(0.011548362372421616, c.Gamma, 1e-13, 0, "gamma");
            Close(43.30635889658106, c.Vega, 1e-13, 0, "vega");
            Close(-4.608738654596136, c.Theta, 1e-13, 0, "call theta");
            Close(74.40757427882498, c.Rho, 1e-13, 0, "call rho");
            Close(-98.43265675542858, c.DividendRho, 1e-13, 0, "call dividend rho");
            Close(-0.19363506144851214, c.Vanna, 1e-13, 0, "vanna");
            Close(10.50805540813927, c.Volga, 1e-13, 0, "volga");
            Close(-0.5221584159917543, c.DualDelta, 1e-13, 0, "call dual delta");

            var p = BlackScholes.Greeks(OptionType.Put, 100, 95, 1.5, 0.04, 0.015, 0.25);
            Close(7.70922862223906, p.Price, 1e-13, 0, "put price");
            Close(-0.32153352549047914, p.Delta, 1e-13, 0, "put delta");
            Close(0.011548362372421616, p.Gamma, 1e-13, 0, "put gamma");
            Close(43.30635889658106, p.Vega, 1e-13, 0, "put vega");
            Close(-2.4966602827659954, p.Theta, 1e-13, 0, "put theta");
            Close(-59.793871756930464, p.Rho, 1e-13, 0, "put rho");
            Close(48.23002882357187, p.DividendRho, 1e-13, 0, "put dividend rho");
            Close(-0.19363506144851214, p.Vanna, 1e-13, 0, "put vanna");
            Close(10.50805540813927, p.Volga, 1e-13, 0, "put volga");
            Close(0.4196061175924945, p.DualDelta, 1e-13, 0, "put dual delta");
        }

        // ---- Greeks against finite differences ---------------------------------------------------------------

        // Central difference with one Richardson step (4 D(h/2) - D(h)) / 3, error O(h^4): the step can stay large
        // compared with the rounding error while the truncation error stays far below the 1e-6 target.
        static double Diff1(Func<double, double> f, double x, double h)
        {
            double D(double s) => (f(x + s) - f(x - s)) / (2 * s);
            return (4 * D(h / 2) - D(h)) / 3;
        }

        static double Diff2(Func<double, double> f, double x, double h)
        {
            double fx = f(x);
            double D(double s) => (f(x + s) - 2 * fx + f(x - s)) / (s * s);
            return (4 * D(h / 2) - D(h)) / 3;
        }

        public static IEnumerable<object[]> Scenarios()
        {
            // spot, strike, maturity, rate, dividend yield, sigma
            var list = new (double S, double K, double T, double r, double q, double sigma)[]
            {
                (100, 100, 1.0, 0.03, 0.01, 0.20),   // ATM
                (100, 90, 0.5, 0.05, 0.0, 0.25),     // ITM call
                (100, 115, 2.0, 0.02, 0.03, 0.30),   // OTM call, q > r
                (42, 40, 0.5, 0.10, 0.0, 0.20),      // Hull
                (100, 100, 0.1, 0.01, 0.0, 0.15),    // short maturity, low vol
                (80, 100, 5.0, -0.005, 0.02, 0.40),  // long maturity, negative rate
                (100, 95, 1.5, 0.04, 0.015, 0.25),
            };
            foreach (var s in list)
            {
                yield return new object[] { 1, s.S, s.K, s.T, s.r, s.q, s.sigma };
                yield return new object[] { -1, s.S, s.K, s.T, s.r, s.q, s.sigma };
            }
        }

        [Theory]
        [MemberData(nameof(Scenarios))]
        public void BsmGreeksMatchFiniteDifferencesOfThePrice(int sign, double S, double K, double T, double r, double q, double sigma)
        {
            double Price(double s, double k, double t, double rr, double qq, double sg)
                => BlackScholesKernels.Price(sign, s, k, t, rr, qq, sg);

            double v = sigma * Math.Sqrt(T);
            // Steps: the price varies on the scale v in log-spot, sigma in volatility, T in time, v/T in the rates.
            double hS = 0.01 * v * S, hK = 0.01 * v * K, hSig = 0.005 * sigma, hT = 0.01 * T, hR = 0.01 * v / T;

            var g = BlackScholes.Greeks(sign == 1 ? OptionType.Call : OptionType.Put, S, K, T, r, q, sigma);
            const double Rel = 1e-6;
            const double Abs = 1e-9;

            Close(Price(S, K, T, r, q, sigma), g.Price, 1e-14, 0, "price");
            Close(Diff1(x => Price(x, K, T, r, q, sigma), S, hS), g.Delta, Rel, Abs, "delta");
            Close(Diff2(x => Price(x, K, T, r, q, sigma), S, hS), g.Gamma, Rel, Abs, "gamma");
            Close(Diff1(x => Price(S, K, T, r, q, x), sigma, hSig), g.Vega, Rel, Abs, "vega");
            Close(-Diff1(x => Price(S, K, x, r, q, sigma), T, hT), g.Theta, Rel, Abs, "theta");
            Close(Diff1(x => Price(S, K, T, x, q, sigma), r, hR), g.Rho, Rel, Abs, "rho");
            Close(Diff1(x => Price(S, K, T, r, x, sigma), q, hR), g.DividendRho, Rel, Abs, "dividend rho");
            // Volga vanishes near the money at some (T, sigma), where a second difference of the price is pure
            // noise relative to the volga itself: the floor is 1e-7 of the natural scale vega / sigma.
            Close(Diff2(x => Price(S, K, T, r, q, x), sigma, hSig), g.Volga, Rel, 1e-7 * g.Vega / sigma, "volga");
            Close(Diff1(x => BlackScholes.Greeks(sign == 1 ? OptionType.Call : OptionType.Put, S, K, T, r, q, x).Vega, sigma, hSig),
                g.Volga, Rel, 1e-9 * g.Vega / sigma, "volga vs vega difference");
            Close(Diff1(x => Price(S, x, T, r, q, sigma), K, hK), g.DualDelta, Rel, Abs, "dual delta");
            // vanna = d(delta)/d(sigma), delta itself being a finite difference of the price
            Close(Diff1(sg => Diff1(x => Price(x, K, T, r, q, sg), S, hS), sigma, hSig), g.Vanna, Rel, Abs, "vanna");
        }

        [Theory]
        [MemberData(nameof(Scenarios))]
        public void Black76GreeksMatchFiniteDifferencesOfThePrice(int sign, double F, double K, double T, double r, double q, double sigma)
        {
            // The scenarios are shared with BSM, with F = spot; the dividend yield does not enter Black-76.
            _ = q;
            double Price(double f, double k, double t, double rr, double sg)
                => Black76Kernels.Price(sign, f, k, Math.Exp(-rr * t), t, sg);

            double v = sigma * Math.Sqrt(T);
            double hF = 0.01 * v * F, hK = 0.01 * v * K, hSig = 0.005 * sigma, hT = 0.01 * T, hR = 0.01 * v / T;

            var g = Black76.Greeks(sign == 1 ? OptionType.Call : OptionType.Put, F, K, r, T, sigma);
            const double Rel = 1e-6;
            const double Abs = 1e-9;

            Close(Price(F, K, T, r, sigma), g.Price, 1e-14, 0, "price");
            Close(Diff1(x => Price(x, K, T, r, sigma), F, hF), g.Delta, Rel, Abs, "delta");
            Close(Diff2(x => Price(x, K, T, r, sigma), F, hF), g.Gamma, Rel, Abs, "gamma");
            Close(Diff1(x => Price(F, K, T, r, x), sigma, hSig), g.Vega, Rel, Abs, "vega");
            Close(-Diff1(x => Price(F, K, x, r, sigma), T, hT), g.Theta, Rel, Abs, "theta");
            Close(Diff1(x => Price(F, K, T, x, sigma), r, hR), g.Rho, Rel, Abs, "rho");
            Assert.Equal(0.0, g.DividendRho);
            Close(Diff2(x => Price(F, K, T, r, x), sigma, hSig), g.Volga, Rel, 1e-7 * g.Vega / sigma, "volga");
            Close(Diff1(x => Black76.Greeks(sign == 1 ? OptionType.Call : OptionType.Put, F, K, r, T, x).Vega, sigma, hSig),
                g.Volga, Rel, 1e-9 * g.Vega / sigma, "volga vs vega difference");
            Close(Diff1(x => Price(F, x, T, r, sigma), K, hK), g.DualDelta, Rel, Abs, "dual delta");
            Close(Diff1(sg => Diff1(x => Price(x, K, T, r, sg), F, hF), sigma, hSig), g.Vanna, Rel, Abs, "vanna");
        }

        [Fact]
        public void Black76RhoIsBsmRhoPlusDividendRhoWhenDividendEqualsRate()
        {
            // F = S e^{(r-q)T} moves with r in BSM, not in Black-76: rho_76 = rho_BSM + phi_BSM.
            var bsm = BlackScholes.Greeks(OptionType.Put, 19, 18, 0.75, 0.1, 0.1, 0.28);
            var b76 = Black76.Greeks(OptionType.Put, 19, 18, 0.1, 0.75, 0.28);
            Close(b76.Rho, bsm.Rho + bsm.DividendRho, 1e-12, 0, "rho");
            Close(b76.Theta, bsm.Theta, 1e-12, 0, "theta");
            Close(b76.Delta, bsm.Delta, 1e-12, 0, "delta");
        }

        // ---- parity ------------------------------------------------------------------------------------------

        [Theory]
        [MemberData(nameof(Scenarios))]
        public void ParityRelations(int sign, double S, double K, double T, double r, double q, double sigma)
        {
            if (sign != 1) return; // each scenario is generated for both signs; check once per pair
            var c = BlackScholes.Greeks(OptionType.Call, S, K, T, r, q, sigma);
            var p = BlackScholes.Greeks(OptionType.Put, S, K, T, r, q, sigma);
            double eq = Math.Exp(-q * T), er = Math.Exp(-r * T);

            Close(S * eq - K * er, c.Price - p.Price, 1e-12, 1e-12, "put-call parity");
            Close(eq, c.Delta - p.Delta, 1e-12, 0, "delta parity");
            Close(c.Gamma, p.Gamma, 1e-14, 0, "gamma");
            Close(c.Vega, p.Vega, 1e-14, 0, "vega");
            Close(c.Vanna, p.Vanna, 1e-14, 0, "vanna");
            Close(c.Volga, p.Volga, 1e-14, 0, "volga");
            Close(-r * K * er + q * S * eq, c.Theta - p.Theta, 1e-12, 1e-12, "theta parity");
            Close(K * T * er, c.Rho - p.Rho, 1e-12, 0, "rho parity");
            Close(-S * T * eq, c.DividendRho - p.DividendRho, 1e-12, 0, "dividend rho parity");
            Close(-er, c.DualDelta - p.DualDelta, 1e-12, 0, "dual delta parity");

            BlackScholes.Greeks(OptionType.Call, S, K, T, r, q, sigma); // no state: repeatable
            BlackScholesKernels.CallPut(out double call, out double put, S, K, T, r, q, sigma);
            Close(c.Price, call, 1e-14, 0, "CallPut call");
            Close(p.Price, put, 1e-14, 0, "CallPut put");
        }

        // ---- limits ------------------------------------------------------------------------------------------

        static void AssertNoNaN(OptionGreeks g, string what)
        {
            foreach (double x in new[] { g.Price, g.Delta, g.Gamma, g.Vega, g.Theta, g.Rho, g.DividendRho, g.Vanna, g.Volga, g.DualDelta })
                Assert.False(double.IsNaN(x), what + ": NaN in " + g);
        }

        [Theory]
        [InlineData(100, 90)]
        [InlineData(100, 100)]
        [InlineData(100, 110)]
        public void ZeroMaturityGivesTheIntrinsicValue(double S, double K)
        {
            foreach (double sigma in new[] { 0.0, 0.2 })
            {
                var c = BlackScholes.Greeks(OptionType.Call, S, K, 0.0, 0.05, 0.02, sigma);
                var p = BlackScholes.Greeks(OptionType.Put, S, K, 0.0, 0.05, 0.02, sigma);
                Assert.Equal(Math.Max(S - K, 0.0), c.Price, 14);
                Assert.Equal(Math.Max(K - S, 0.0), p.Price, 14);
                AssertNoNaN(c, "call T=0");
                AssertNoNaN(p, "put T=0");
                Assert.Equal(S > K ? 1.0 : (S < K ? 0.0 : 0.5), c.Delta);
                Assert.Equal(c.Delta - 1.0, p.Delta);
                Assert.Equal(0.0, c.Vega);
                Assert.Equal(0.0, c.Volga);
                Assert.Equal(0.0, c.Vanna);
                if (S != K) Assert.Equal(0.0, c.Gamma);
            }
        }

        [Fact]
        public void TinyMaturityConvergesToTheIntrinsicValue()
        {
            Close(10.0, BlackScholes.Price(OptionType.Call, 110, 100, 1e-12, 0.05, 0.02, 0.3), 1e-10, 0, "ITM call");
            Close(0.0, BlackScholes.Price(OptionType.Call, 90, 100, 1e-12, 0.05, 0.02, 0.3), 0, 1e-300, "OTM call");
            double atm = BlackScholes.Price(OptionType.Call, 100, 100, 1e-12, 0.0, 0.0, 0.3);
            Close(100 * 0.3 * Math.Sqrt(1e-12) / Math.Sqrt(2 * Math.PI), atm, 1e-9, 0, "ATM call ~ F sigma sqrt(T) / sqrt(2 pi)");
            AssertNoNaN(BlackScholes.Greeks(OptionType.Put, 100, 80, 1e-300, 0.05, 0.02, 0.3), "T=1e-300");
        }

        [Theory]
        [InlineData(90.0)]
        [InlineData(100.0)]
        [InlineData(120.0)]
        public void ZeroVolatilityGivesTheDiscountedIntrinsicValueOfTheForward(double S)
        {
            const double K = 100, T = 2.0, r = 0.05, q = 0.02;
            double fwdMinusK = S * Math.Exp(-q * T) - K * Math.Exp(-r * T);
            var c = BlackScholes.Greeks(OptionType.Call, S, K, T, r, q, 0.0);
            var p = BlackScholes.Greeks(OptionType.Put, S, K, T, r, q, 0.0);
            Close(Math.Max(fwdMinusK, 0.0), c.Price, 1e-14, 0, "call");
            Close(Math.Max(-fwdMinusK, 0.0), p.Price, 1e-14, 0, "put");
            AssertNoNaN(c, "call sigma=0");
            AssertNoNaN(p, "put sigma=0");
            if (fwdMinusK > 0)
            {
                // deterministic call: V = S e^{-qT} - K e^{-rT}
                Close(Math.Exp(-q * T), c.Delta, 1e-14, 0, "delta");
                Close(-q * S * Math.Exp(-q * T) + r * K * Math.Exp(-r * T), -c.Theta, 1e-12, 0, "theta");
                Close(K * T * Math.Exp(-r * T), c.Rho, 1e-14, 0, "rho");
                Close(-Math.Exp(-r * T), c.DualDelta, 1e-14, 0, "dual delta");
            }
            Assert.Equal(0.0, c.Gamma);
        }

        [Fact]
        public void SmallVolatilityIsContinuousWithZeroVolatility()
        {
            double zero = BlackScholes.Price(OptionType.Call, 120, 100, 1.0, 0.03, 0.0, 0.0);
            Close(zero, BlackScholes.Price(OptionType.Call, 120, 100, 1.0, 0.03, 0.0, 1e-8), 1e-12, 0, "ITM");
            Close(0.0, BlackScholes.Price(OptionType.Call, 80, 100, 1.0, 0.03, 0.0, 1e-8), 0, 1e-300, "OTM");
            // ATM and sigma -> 0: the call price tends to the discounted forward intrinsic, here 100 - 100 e^{-rT}
            double atm0 = BlackScholes.Price(OptionType.Call, 100, 100, 1.0, 0.03, 0.0, 0.0);
            Close(atm0, BlackScholes.Price(OptionType.Call, 100, 100, 1.0, 0.03, 0.0, 1e-9), 0, 1e-6, "ATM");
        }

        [Fact]
        public void LargeTotalVolatilityIsStable()
        {
            // v = sigma sqrt(T) of 500 and 1e4: call -> S e^{-qT}, put -> K e^{-rT}.
            foreach (var (sigma, T) in new[] { (50.0, 100.0), (100.0, 1e4) })
            {
                var c = BlackScholes.Greeks(OptionType.Call, 100, 100, T, 0.0, 0.0, sigma);
                var p = BlackScholes.Greeks(OptionType.Put, 100, 100, T, 0.0, 0.0, sigma);
                AssertNoNaN(c, "large v call");
                AssertNoNaN(p, "large v put");
                Close(100.0, c.Price, 1e-12, 0, "call");
                Close(100.0, p.Price, 1e-12, 0, "put");
                Assert.True(c.Gamma >= 0 && c.Vega >= 0);
            }
        }

        [Fact]
        public void PricesAreNonNegativeAndNeverNaNOnAGrid()
        {
            foreach (double S in new[] { 0.0, 1e-3, 1, 100, 1e6 })
            foreach (double K in new[] { 0.0, 1e-3, 1, 100, 1e6 })
            foreach (double T in new[] { 0.0, 1e-9, 0.25, 30 })
            foreach (double sigma in new[] { 0.0, 1e-6, 0.2, 5 })
            foreach (var type in new[] { OptionType.Call, OptionType.Put })
            {
                var g = BlackScholes.Greeks(type, S, K, T, 0.03, 0.01, sigma);
                AssertNoNaN(g, $"{type} S={S} K={K} T={T} sigma={sigma}");
                Assert.True(g.Price >= 0.0);
            }
        }

        [Fact]
        public void NaNInputGivesNaNPrice()
        {
            Assert.True(double.IsNaN(BlackScholesKernels.Price(1, double.NaN, 100, 1, 0.01, 0, 0.2)));
            Assert.True(double.IsNaN(BlackScholesKernels.Price(1, 100, 100, double.NaN, 0.01, 0, 0.2)));
            Assert.True(double.IsNaN(BlackScholesKernels.Price(1, 100, 100, 1, 0.01, 0, double.NaN)));
        }

        [Fact]
        public void HostApiRejectsInvalidArguments()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => BlackScholes.Price(OptionType.Call, -1, 100, 1, 0.01, 0, 0.2));
            Assert.Throws<ArgumentOutOfRangeException>(() => BlackScholes.Price(OptionType.Call, 100, 100, -1, 0.01, 0, 0.2));
            Assert.Throws<ArgumentOutOfRangeException>(() => BlackScholes.Price(OptionType.Call, 100, 100, 1, 0.01, 0, -0.2));
            Assert.Throws<ArgumentOutOfRangeException>(() => BlackScholes.Price(OptionType.Call, 100, 100, 1, double.NaN, 0, 0.2));
            Assert.Throws<ArgumentOutOfRangeException>(() => BlackScholes.Price((OptionType)0, 100, 100, 1, 0.01, 0, 0.2));
            Assert.Throws<ArgumentOutOfRangeException>(() => Black76.Price(OptionType.Call, 100, 100, -0.9, 1, 0.2));
        }

        // ---- batch vs scalar ---------------------------------------------------------------------------------

        [Fact]
        public void BatchKernelsEqualScalarKernels()
        {
            var rng = new System.Random(12345);
            const int n = 200;
            var sign = new int[n];
            var spot = new double[n]; var strike = new double[n]; var mat = new double[n];
            var rate = new double[n]; var div = new double[n]; var sigma = new double[n]; var df = new double[n];
            for (int i = 0; i < n; i++)
            {
                sign[i] = rng.Next(2) == 0 ? 1 : -1;
                spot[i] = 50 + 100 * rng.NextDouble();
                strike[i] = 50 + 100 * rng.NextDouble();
                mat[i] = i % 20 == 0 ? 0.0 : 0.05 + 4 * rng.NextDouble();
                rate[i] = -0.01 + 0.08 * rng.NextDouble();
                div[i] = 0.04 * rng.NextDouble();
                sigma[i] = i % 25 == 0 ? 0.0 : 0.05 + 0.5 * rng.NextDouble();
                df[i] = Math.Exp(-rate[i] * mat[i]);
            }

            var price = new double[n];
            var d = new double[11][];
            for (int k = 0; k < d.Length; k++) d[k] = new double[n];
            var price76 = new double[n];
            var d76 = new double[10][];
            for (int k = 0; k < d76.Length; k++) d76[k] = new double[n];

            for (int i = 0; i < n; i++)
            {
                BlackScholesKernels.PriceAt(i, price, sign, spot, strike, mat, rate, div, sigma);
                BlackScholesKernels.GreeksAt(i, d[0], d[1], d[2], d[3], d[4], d[5], d[6], d[7], d[8], d[9],
                    sign, spot, strike, mat, rate, div, sigma);
                Black76Kernels.PriceAt(i, price76, sign, spot, strike, df, mat, sigma);
                Black76Kernels.GreeksAt(i, d76[0], d76[1], d76[2], d76[3], d76[4], d76[5], d76[6], d76[7], d76[8],
                    sign, spot, strike, rate, mat, sigma);
            }

            for (int i = 0; i < n; i++)
            {
                double p = BlackScholesKernels.Price(sign[i], spot[i], strike[i], mat[i], rate[i], div[i], sigma[i]);
                BlackScholesKernels.Greeks(sign[i], spot[i], strike[i], mat[i], rate[i], div[i], sigma[i],
                    out double gp, out double de, out double ga, out double ve, out double th,
                    out double rh, out double dr, out double va, out double vo, out double du);
                Assert.Equal(p, price[i]);
                Assert.Equal(gp, d[0][i]);
                Assert.Equal(de, d[1][i]);
                Assert.Equal(ga, d[2][i]);
                Assert.Equal(ve, d[3][i]);
                Assert.Equal(th, d[4][i]);
                Assert.Equal(rh, d[5][i]);
                Assert.Equal(dr, d[6][i]);
                Assert.Equal(va, d[7][i]);
                Assert.Equal(vo, d[8][i]);
                Assert.Equal(du, d[9][i]);
                // the price returned with the Greeks is the price
                Close(p, gp, 1e-14, 0, "price vs greeks price");

                double p76 = Black76Kernels.Price(sign[i], spot[i], strike[i], df[i], mat[i], sigma[i]);
                Assert.Equal(p76, price76[i]);
                Close(p76, d76[0][i], 1e-14, 1e-300, "Black-76 price vs greeks price");
            }
        }

        [Fact]
        public void LegacyArrayEntryPointsSurviveTheDelegation()
        {
            // CallPutArray itself loops over threadIdx (Hybridizer); its per-element body is checked here.
            var call = new double[2]; var put = new double[2];
            BlackScholesMertonPrimitive.CallPut(1, call, put, 60, 65, 0.08, 0.30, 0.25);
            Close(2.1333684449162043, call[1], 1e-13, 0, "call");
            Close(5.846282209855296, put[1], 1e-13, 0, "put");
            Assert.Equal(0.0, call[0]);
        }
    }
}
