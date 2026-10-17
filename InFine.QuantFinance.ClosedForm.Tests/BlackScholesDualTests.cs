using System;
using InFine.QuantFinance.Arithmetic.AutoDiff;
using InFine.QuantFinance.ClosedForm;
using Xunit;

namespace InFine.QuantFinance.ClosedForm.Tests
{
    /// <summary>
    /// Tangent-mode AD of the normal distribution and of Black-Scholes: dual derivatives against the analytic Greeks of
    /// <see cref="BlackScholesKernels"/> / <see cref="Black76Kernels"/> and against finite differences.
    /// </summary>
    public class BlackScholesDualTests
    {
        static void Close(double expected, double actual, double relative, string what)
        {
            double tolerance = relative * Math.Max(1.0, Math.Abs(expected));
            Assert.True(Math.Abs(expected - actual) <= tolerance,
                $"{what}: expected {expected:R}, got {actual:R} (|diff| {Math.Abs(expected - actual):E3})");
        }

        // ---- normal distribution -----------------------------------------------------------------------------

        [Theory]
        [InlineData(-7.5)]
        [InlineData(-1.3)]
        [InlineData(0.0)]
        [InlineData(0.4)]
        [InlineData(2.2)]
        [InlineData(9.0)]
        public void NormalFunctionsCarryTheirAnalyticDerivatives(double x)
        {
            const double t = 0.75;
            var d = new Dual(x, t);
            Dual cdf = DualNormalDistribution.Cdf(d);
            Dual pdf = DualNormalDistribution.Pdf(d);
            Dual erfc = DualNormalDistribution.Erfc(d);

            Assert.Equal(NormalDistribution.Cdf(x), cdf.Value);
            Assert.Equal(t * NormalDistribution.Pdf(x), cdf.Tangent);
            Assert.Equal(NormalDistribution.Pdf(x), pdf.Value);
            Close(-t * x * NormalDistribution.Pdf(x), pdf.Tangent, 1e-14, "pdf'");
            Assert.Equal(NormalDistribution.Erfc(x), erfc.Value);
            Close(-t * 2.0 / Math.Sqrt(Math.PI) * Math.Exp(-x * x), erfc.Tangent, 1e-14, "erfc'");

            // Richardson central differences of the double functions: relative 1e-10 plus the FD rounding floor
            // (~ eps / h, reached where the function saturates at 1 or 2 and its differences round to 0).
            double h = 1e-3;
            Assert.True(Math.Abs(Fd(NormalDistribution.Cdf, x, h) * t - cdf.Tangent) <= 1e-10 * Math.Abs(cdf.Tangent) + 1e-13);
            Assert.True(Math.Abs(Fd(NormalDistribution.Erfc, x, h) * t - erfc.Tangent) <= 1e-10 * Math.Abs(erfc.Tangent) + 1e-13);

            // Dual4: same numbers on every seeded direction, exact zeros elsewhere.
            Dual4 c4 = DualNormalDistribution.Cdf(new Dual4(x, t, 0.0, -t, 0.0));
            Assert.Equal(cdf.Value, c4.Value);
            Assert.Equal(cdf.Tangent, c4.D0);
            Assert.Equal(-cdf.Tangent, c4.D2);
            Assert.Equal(0.0, c4.D1);
            Assert.Equal(pdf.Tangent, DualNormalDistribution.Pdf(new Dual4(x, 0.0, t, 0.0, 0.0)).D1);
            Assert.Equal(erfc.Tangent, DualNormalDistribution.Erfc(new Dual4(x, 0.0, 0.0, 0.0, t)).D3);
        }

        static double Fd(Func<double, double> f, double x, double h)
        {
            double c1 = (f(x + h) - f(x - h)) / (2 * h);
            double c2 = (f(x + 2 * h) - f(x - 2 * h)) / (4 * h);
            return (4 * c1 - c2) / 3;
        }

        // ---- Black-Scholes -----------------------------------------------------------------------------------

        public static TheoryData<int, double, double, double, double, double, double> Cases()
        {
            return new TheoryData<int, double, double, double, double, double, double>
            {
                // sign, S, K, T, r, q, sigma
                { +1, 100.0, 100.0, 1.0, 0.05, 0.0, 0.20 },
                { -1, 100.0, 100.0, 1.0, 0.05, 0.0, 0.20 },
                { +1, 42.0, 40.0, 0.5, 0.10, 0.0, 0.20 },
                { -1, 60.0, 65.0, 0.25, 0.08, 0.03, 0.30 },
                { +1, 100.0, 150.0, 2.0, 0.01, 0.02, 0.15 },
                { -1, 100.0, 60.0, 3.0, -0.005, 0.01, 0.45 },
                { +1, 5000.0, 4800.0, 0.1, 0.03, 0.015, 0.12 },
            };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void DualBlackScholesReproducesTheAnalyticGreeks(int sign, double s, double k, double t, double r, double q, double sigma)
        {
            double price, delta, gamma, vega, theta, rho, dividendRho, vanna, volga, dualDelta;
            BlackScholesKernels.Greeks(sign, s, k, t, r, q, sigma, out price, out delta, out gamma, out vega, out theta,
                out rho, out dividendRho, out vanna, out volga, out dualDelta);

            Dual bySpot = BlackScholesDual.Price(sign, Dual.Variable(s), k, t, r, q, sigma);
            Dual byStrike = BlackScholesDual.Price(sign, s, Dual.Variable(k), t, r, q, sigma);
            Dual byMaturity = BlackScholesDual.Price(sign, s, k, Dual.Variable(t), r, q, sigma);
            Dual byRate = BlackScholesDual.Price(sign, s, k, t, Dual.Variable(r), q, sigma);
            Dual byDividend = BlackScholesDual.Price(sign, s, k, t, r, Dual.Variable(q), sigma);
            Dual bySigma = BlackScholesDual.Price(sign, s, k, t, r, q, Dual.Variable(sigma));

            // The value is computed by the same operations as the analytic kernel.
            Close(BlackScholesKernels.Price(sign, s, k, t, r, q, sigma), bySpot.Value, 1e-15, "price vs Price");
            Close(price, bySpot.Value, 1e-15, "price vs Greeks");

            const double tol = 1e-12;
            Close(delta, bySpot.Tangent, tol, "delta");
            Close(dualDelta, byStrike.Tangent, tol, "dual delta");
            Close(-theta, byMaturity.Tangent, tol, "dV/dT = -theta");
            Close(rho, byRate.Tangent, tol, "rho");
            Close(dividendRho, byDividend.Tangent, tol, "dividend rho");
            Close(vega, bySigma.Tangent, tol, "vega");

            // Seeding several inputs at once gives the directional derivative.
            Dual both = BlackScholesDual.Price(sign, new Dual(s, 1.0), k, t, r, q, new Dual(sigma, 2.0));
            Close(delta + 2.0 * vega, both.Tangent, tol, "directional");
        }

        [Fact]
        public void DualBlack76ReproducesTheForwardGreeks()
        {
            double f = 103.0, k = 95.0, r = 0.04, t = 1.5, sigma = 0.27;
            double df = Math.Exp(-r * t);
            foreach (int sign in new[] { +1, -1 })
            {
                double price, delta, gamma, vega, theta, rho, vanna, volga, dualDelta;
                Black76Kernels.Greeks(sign, f, k, r, t, sigma, out price, out delta, out gamma, out vega, out theta,
                    out rho, out vanna, out volga, out dualDelta);
                Close(price, BlackScholesDual.Black76Price(sign, f, k, df, t, sigma).Value, 1e-15, "price");
                Close(delta, BlackScholesDual.Black76Price(sign, Dual.Variable(f), k, df, t, sigma).Tangent, 1e-12, "delta");
                Close(dualDelta, BlackScholesDual.Black76Price(sign, f, Dual.Variable(k), df, t, sigma).Tangent, 1e-12, "dual delta");
                Close(vega, BlackScholesDual.Black76Price(sign, f, k, df, t, Dual.Variable(sigma)).Tangent, 1e-12, "vega");
                // Rate through the discount factor only (forward held fixed): rho = -T V.
                Dual dfr = Dual.Exp(-Dual.Variable(r) * t);
                Close(rho, BlackScholesDual.Black76Price(sign, f, k, dfr, t, sigma).Tangent, 1e-12, "rho");
            }
        }

        [Fact]
        public void AtExpiryTheDerivativeIsOneSidedAtTheMoney()
        {
            // T = 0: price = intrinsic. Away from the money the delta is the step of the payoff.
            Assert.Equal(1.0, BlackScholesDual.Price(+1, Dual.Variable(110.0), 100.0, 0.0, 0.05, 0.0, 0.2).Tangent);
            Assert.Equal(0.0, BlackScholesDual.Price(+1, Dual.Variable(90.0), 100.0, 0.0, 0.05, 0.0, 0.2).Tangent);
            Assert.Equal(-1.0, BlackScholesDual.Price(-1, Dual.Variable(90.0), 100.0, 0.0, 0.05, 0.0, 0.2).Tangent);

            // At the money the dual takes the exercised branch (call +1, put -1), the analytic kernel reports the
            // symmetric limit Phi(0) = 1/2: the documented difference between the two.
            Dual call = BlackScholesDual.Price(+1, Dual.Variable(100.0), 100.0, 0.0, 0.05, 0.0, 0.2);
            Dual put = BlackScholesDual.Price(-1, Dual.Variable(100.0), 100.0, 0.0, 0.05, 0.0, 0.2);
            Assert.Equal(0.0, call.Value);
            Assert.Equal(1.0, call.Tangent);
            Assert.Equal(-1.0, put.Tangent);
            double price, delta, gamma, vega, theta, rho, dividendRho, vanna, volga, dualDelta;
            BlackScholesKernels.Greeks(+1, 100.0, 100.0, 0.0, 0.05, 0.0, 0.2, out price, out delta, out gamma, out vega,
                out theta, out rho, out dividendRho, out vanna, out volga, out dualDelta);
            Assert.Equal(0.5, delta);

            // Zero volatility, T > 0: intrinsic value of the forward, discounted; vega stays finite (0 tangent of v).
            Dual zeroVol = BlackScholesDual.Price(+1, Dual.Variable(100.0), 90.0, 1.0, 0.05, 0.0, 0.0);
            Close(Math.Exp(-0.05) * (100.0 * Math.Exp(0.05) - 90.0), zeroVol.Value, 1e-15, "zero vol price");
            Close(1.0, zeroVol.Tangent, 1e-15, "zero vol delta");

            Assert.True(double.IsNaN(BlackScholesDual.Price(+1, double.NaN, 100.0, 1.0, 0.05, 0.0, 0.2).Value));
        }
    }
}
