using System;
using System.Numerics;
using InFine.QuantFinance.ClosedForm;
using Xunit;
using Xunit.Abstractions;

namespace InFine.QuantFinance.ClosedForm.Tests
{
    /// <summary>
    /// Semi-analytic Heston by the COS method: published Fang-Oosterlee values, an independent Gil-Pelaez /
    /// Gauss-Legendre oracle (<see cref="HestonReferencePricer"/>) over strikes and maturities, the Black-76 limit of
    /// deterministic variance, put-call parity, convergence in N, characteristic-function identities and the batch
    /// kernel. Everything runs on plain doubles (no Hybridizer runtime needed).
    /// </summary>
    public class HestonTests
    {
        readonly ITestOutputHelper _output;

        public HestonTests(ITestOutputHelper output) { _output = output; }

        /// <summary>
        /// Fang and Oosterlee (2008), section 5.3: S0 = 100, r = q = 0, kappa = 1.5768, theta = 0.0398,
        /// xi = 0.5751, rho = -0.5711, v0 = 0.0175 (Feller violated).
        /// </summary>
        static readonly HestonParameters FangOosterlee = new HestonParameters(0.0175, 1.5768, 0.0398, 0.5751, -0.5711);

        /// <summary>
        /// Published reference ATM calls: T = 1 (Table 4) and T = 10 (Table 5). The T = 1 value is only good to about
        /// 2e-8: the oracle and COS with N = 4096 both give 5.78515543419 (agreeing to 2e-10), as do later papers
        /// that quote 5.785155434; the T = 10 value agrees with both to 3e-10.
        /// </summary>
        const double FangOosterleeCallT1 = 5.785155450;
        const double FangOosterleeCallT10 = 22.318945791474590;

        static double Oracle(OptionType type, double f, double k, double df, double t, HestonParameters p)
        {
            return type == OptionType.Call
                ? HestonReferencePricer.Call(f, k, df, t, p.V0, p.Kappa, p.Theta, p.Xi, p.Rho)
                : HestonReferencePricer.Put(f, k, df, t, p.V0, p.Kappa, p.Theta, p.Xi, p.Rho);
        }

        [Fact]
        public void Oracle_MatchesPublishedFangOosterleeValues()
        {
            var p = FangOosterlee;
            Assert.Equal(FangOosterleeCallT1, Oracle(OptionType.Call, 100.0, 100.0, 1.0, 1.0, p), 3e-8);
            Assert.Equal(FangOosterleeCallT10, Oracle(OptionType.Call, 100.0, 100.0, 1.0, 10.0, p), 1e-9);
        }

        [Fact]
        public void Cos_ReproducesPublishedFangOosterleeValues()
        {
            double t1 = Heston.Price(OptionType.Call, 100.0, 100.0, 1.0, 1.0, FangOosterlee);
            double t10 = Heston.Price(OptionType.Call, 100.0, 100.0, 1.0, 10.0, FangOosterlee);
            _output.WriteLine($"T=1: {t1:R} (err {t1 - FangOosterleeCallT1:E2}); T=10: {t10:R} (err {t10 - FangOosterleeCallT10:E2})");
            Assert.Equal(FangOosterleeCallT1, t1, 1e-7);
            Assert.Equal(FangOosterleeCallT10, t10, 1e-7);
            Assert.Equal(Oracle(OptionType.Call, 100.0, 100.0, 1.0, 1.0, FangOosterlee), t1, 1e-9);
            Assert.Equal(Oracle(OptionType.Call, 100.0, 100.0, 1.0, 10.0, FangOosterlee), t10, 1e-9);
        }

        public static TheoryData<double, double, double, double, double, double, double> OracleCases()
        {
            // maturity, terms, v0, kappa, theta, xi, rho
            var d = new TheoryData<double, double, double, double, double, double, double>();
            d.Add(1.0, 512, 0.0175, 1.5768, 0.0398, 0.5751, -0.5711);   // Fang-Oosterlee (default N)
            d.Add(10.0, 512, 0.0175, 1.5768, 0.0398, 0.5751, -0.5711);
            d.Add(0.25, 512, 0.0175, 1.5768, 0.0398, 0.5751, -0.5711);  // short expiry: slowly decaying phi
            d.Add(0.5, 256, 0.04, 2.0, 0.04, 0.3, -0.7);                 // Feller satisfied
            d.Add(1.0, 256, 0.03, 3.0, 0.05, 0.4, 0.5);                  // positive correlation
            d.Add(2.0, 8192, 0.09, 0.5, 0.06, 1.0, -0.9);                // strong skew, large vol of vol: needs many terms
            return d;
        }

        [Theory]
        [MemberData(nameof(OracleCases))]
        public void Cos_MatchesOracle_AcrossStrikes(double t, double terms, double v0, double kappa, double theta, double xi, double rho)
        {
            var p = new HestonParameters(v0, kappa, theta, xi, rho);
            const double forward = 100.0, df = 0.97;
            double worst = 0.0;
            foreach (double strike in new[] { 50.0, 70.0, 80.0, 90.0, 100.0, 110.0, 120.0, 140.0, 180.0 })
            {
                foreach (OptionType type in new[] { OptionType.Call, OptionType.Put })
                {
                    double cos = Heston.Price(type, forward, strike, df, t, p, (int)terms);
                    double oracle = Oracle(type, forward, strike, df, t, p);
                    worst = Math.Max(worst, Math.Abs(cos - oracle));
                    Assert.True(Math.Abs(cos - oracle) < 1e-8, $"{type} K={strike}: COS {cos:R}, oracle {oracle:R}");
                }
            }
            _output.WriteLine($"T={t} N={terms}: worst abs error {worst:E2}");
        }

        [Fact]
        public void Cos_ConvergesExponentiallyInTerms()
        {
            // Fang-Oosterlee T = 10: the error falls by orders of magnitude per doubling of N down to the 1e-12 floor
            double reference = Oracle(OptionType.Call, 100.0, 100.0, 1.0, 10.0, FangOosterlee);
            var errors = new double[4];
            int[] terms = { 32, 64, 128, 256 };
            for (int i = 0; i < terms.Length; i++)
            {
                errors[i] = Math.Abs(Heston.Price(OptionType.Call, 100.0, 100.0, 1.0, 10.0, FangOosterlee, terms[i]) - reference);
                _output.WriteLine($"N={terms[i]}: {errors[i]:E2}");
            }
            Assert.True(errors[1] < errors[0] / 3.0);
            Assert.True(errors[2] < errors[1] / 100.0);
            Assert.True(errors[3] < 1e-11);
        }

        [Fact]
        public void ZeroVolOfVol_WithV0EqualTheta_IsBlack76()
        {
            var p = new HestonParameters(0.04, 1.3, 0.04, 0.0, -0.5);
            foreach (double t in new[] { 0.1, 1.0, 5.0 })
            foreach (double strike in new[] { 60.0, 90.0, 100.0, 110.0, 150.0 })
            foreach (OptionType type in new[] { OptionType.Call, OptionType.Put })
            {
                double heston = Heston.Price(type, 100.0, strike, 0.95, t, p);
                double black = Black76.Price(type, 100.0, strike, 0.95, t, 0.2);
                Assert.True(Math.Abs(heston - black) < 1e-10, $"{type} T={t} K={strike}: {heston:R} vs {black:R}");
            }
        }

        [Fact]
        public void ZeroVolOfVol_IsBlack76WithIntegratedDeterministicVariance()
        {
            // xi = 0: v(t) = theta + (v0 - theta) e^{-kappa t}, total variance theta T + (v0 - theta)(1 - e^{-kappa T}) / kappa
            var p = new HestonParameters(0.09, 2.0, 0.03, 0.0, 0.3);
            const double t = 1.5;
            double totalVariance = p.Theta * t + (p.V0 - p.Theta) * (1.0 - Math.Exp(-p.Kappa * t)) / p.Kappa;
            double sigma = Math.Sqrt(totalVariance / t);
            foreach (double strike in new[] { 70.0, 100.0, 130.0 })
            {
                double heston = Heston.Price(OptionType.Put, 100.0, strike, 1.0, t, p);
                double black = Black76.Price(OptionType.Put, 100.0, strike, 1.0, t, sigma);
                Assert.True(Math.Abs(heston - black) < 1e-10, $"K={strike}: {heston:R} vs {black:R}");
            }
        }

        [Fact]
        public void SmallVolOfVol_IsContinuousAtZero()
        {
            // With rho = 0 the price moves by O(xi^2): 1e-5 vol of vol is invisible at 1e-9.
            var zero = new HestonParameters(0.04, 1.5, 0.05, 0.0, 0.0);
            var small = zero with { Xi = 1e-5 };
            var tiny = zero with { Xi = 1e-9 };
            double p0 = Heston.Price(OptionType.Call, 100.0, 105.0, 1.0, 2.0, zero);
            Assert.Equal(p0, Heston.Price(OptionType.Call, 100.0, 105.0, 1.0, 2.0, small), 1e-9);
            Assert.Equal(p0, Heston.Price(OptionType.Call, 100.0, 105.0, 1.0, 2.0, tiny), 1e-12);
        }

        [Fact]
        public void PutCallParity_Holds()
        {
            var p = new HestonParameters(0.05, 1.0, 0.06, 0.8, -0.6);
            foreach (double strike in new[] { 60.0, 100.0, 140.0 })
            {
                double call = Heston.Price(OptionType.Call, 102.0, strike, 0.9, 3.0, p);
                double put = Heston.Price(OptionType.Put, 102.0, strike, 0.9, 3.0, p);
                Assert.Equal(0.9 * (102.0 - strike), call - put, 1e-11);
            }
        }

        [Fact]
        public void Prices_AreHomogeneousInForwardAndStrike()
        {
            var p = FangOosterlee;
            double unit = Heston.Price(OptionType.Put, 1.0, 1.1, 0.8, 1.0, p);
            double scaled = Heston.Price(OptionType.Put, 250.0, 275.0, 0.8, 1.0, p);
            Assert.Equal(250.0 * unit, scaled, 1e-11);
        }

        [Fact]
        public void CharacteristicFunction_SatisfiesIdentitiesAndMatchesOracle()
        {
            var p = FangOosterlee;
            const double t = 1.0;
            Assert.Equal(Complex.One, Heston.CharacteristicFunction(0.0, t, p));
            foreach (double u in new[] { 1e-6, 0.3, 1.0, 5.0, 20.0, 100.0 })
            {
                Complex lib = Heston.CharacteristicFunction(u, t, p);
                Complex oracle = HestonReferencePricer.Phi(u, 0.0, t, p.V0, p.Kappa, p.Theta, p.Xi, p.Rho);
                Assert.True(Complex.Abs(lib - oracle) <= 1e-13 * Math.Max(1.0, Complex.Abs(oracle)) + 1e-300,
                    $"u={u}: {lib} vs {oracle}");
                // phi(-u) = conj(phi(u)) for a real random variable
                Complex minus = Heston.CharacteristicFunction(-u, t, p);
                Assert.True(Complex.Abs(minus - Complex.Conjugate(lib)) < 1e-14, $"u={u}");
                Assert.True(Complex.Abs(lib) <= 1.0 + 1e-15);
            }
            // martingale: E[S_T / F] = phi(-i) = 1 (oracle, complex argument)
            Assert.Equal(1.0, HestonReferencePricer.Phi(-Complex.ImaginaryOne, 0.0, t, p.V0, p.Kappa, p.Theta, p.Xi, p.Rho).Real, 1e-14);
        }

        [Theory]
        [InlineData(1.0, 0.0175, 1.5768, 0.0398, 0.5751, -0.5711)]
        [InlineData(0.1, 0.04, 2.0, 0.04, 0.3, -0.7)]
        [InlineData(5.0, 0.09, 0.5, 0.06, 1.0, -0.9)]
        [InlineData(2.0, 0.03, 1e-4, 0.05, 0.4, 0.5)]   // kappa T far below the 0.01 floor
        public void Cumulants_MatchDerivativesOfTheLogCharacteristicFunction(double t, double v0, double kappa, double theta, double xi, double rho)
        {
            // ln phi(u) = i c1 u - c2 u^2/2 - i c3 u^3/6 + c4 u^4/24 + ...: central differences of the oracle's log phi
            // at 0, Richardson-extrapolated in h (steps scaled to the standard deviation)
            double sd = Math.Sqrt(theta * t + v0 * t);
            double K(double u) => u == 0.0 ? 0.0 : Complex.Log(HestonReferencePricer.Phi(u, 0.0, t, v0, kappa, theta, xi, rho)).Real;
            double C1(double h) => (Complex.Log(HestonReferencePricer.Phi(h, 0.0, t, v0, kappa, theta, xi, rho))
                - Complex.Log(HestonReferencePricer.Phi(-h, 0.0, t, v0, kappa, theta, xi, rho))).Imaginary / (2.0 * h);
            double C2(double h) => -2.0 * K(h) / (h * h);
            double C4(double h) => (2.0 * K(2.0 * h) - 8.0 * K(h)) / (h * h * h * h);
            double h0 = 0.002 / sd;
            double c1Ref = (4.0 * C1(h0) - C1(2.0 * h0)) / 3.0;
            double c2Ref = (4.0 * C2(h0) - C2(2.0 * h0)) / 3.0;
            double h4 = 0.02 / sd;
            double c4Ref = (4.0 * C4(h4) - C4(2.0 * h4)) / 3.0;

            HestonKernels.Cumulants(t, v0, kappa, theta, xi, rho, out double c1, out double c2, out double c4);
            _output.WriteLine($"c1 {c1:R} vs {c1Ref:R}; c2 {c2:R} vs {c2Ref:R}; c4 {c4:R} vs {c4Ref:R}");
            Assert.Equal(c1Ref, c1, 1e-9 * sd * sd);
            // c2 is exact unless kappa T < 0.01, where kappa is floored and c2 is only needed to a few percent
            double tolerance = kappa * t >= 0.01 ? 1e-8 * c2Ref : 0.02 * c2Ref;
            Assert.True(Math.Abs(c2 - c2Ref) <= tolerance, $"c2 {c2:R} vs {c2Ref:R}");
            // c4 only sets the truncation range, through sqrt(c2 + sqrt(c4)): 10% on c4 (reached only for the very
            // fat-tailed xi = 1, rho = -0.9, T = 5 case, excess kurtosis 58) moves the range by under 2.5%
            Assert.True(Math.Abs(c4 - c4Ref) <= 0.1 * c4Ref, $"c4 {c4:R} vs {c4Ref:R}");
        }

        [Fact]
        public void Cumulants_OfDeterministicVariance_AreGaussian()
        {
            // xi = 0: X ~ N(-w/2, w), w = theta T + (v0 - theta)(1 - e^{-kappa T}) / kappa
            const double t = 1.5, v0 = 0.09, kappa = 2.0, theta = 0.03;
            double w = theta * t + (v0 - theta) * (1.0 - Math.Exp(-kappa * t)) / kappa;
            HestonKernels.Cumulants(t, v0, kappa, theta, 0.0, -0.4, out double c1, out double c2, out double c4);
            Assert.Equal(-0.5 * w, c1, 1e-15);
            Assert.Equal(w, c2, 1e-15);
            Assert.True(c4 <= 1e-10 * w * w, $"c4 {c4:R}");
        }

        [Fact]
        public void Batch_MatchesScalar()
        {
            var p = FangOosterlee;
            var type = new[] { OptionType.Call, OptionType.Put, OptionType.Call, OptionType.Put };
            var forward = new[] { 100.0, 100.0, 95.0, 120.0 };
            var strike = new[] { 90.0, 100.0, 110.0, 100.0 };
            var df = new[] { 1.0, 0.99, 0.95, 0.9 };
            var t = new[] { 0.5, 1.0, 2.0, 3.0 };
            double[] batch = Heston.Price(type, forward, strike, df, t, p);
            for (int i = 0; i < type.Length; i++)
                Assert.Equal(Heston.Price(type[i], forward[i], strike[i], df[i], t[i], p), batch[i]);
        }

        [Fact]
        public void Kernel_PriceAt_MatchesHostApi()
        {
            var p = FangOosterlee;
            var price = new double[2];
            var sign = new[] { 1, -1 };
            var forward = new[] { 100.0, 100.0 };
            var strike = new[] { 100.0, 80.0 };
            var df = new[] { 1.0, 0.9 };
            var t = new[] { 1.0, 0.75 };
            for (int i = 0; i < 2; i++)
                HestonKernels.PriceAt(i, price, sign, forward, strike, df, t, p.V0, p.Kappa, p.Theta, p.Xi, p.Rho,
                    HestonKernels.DefaultTerms, HestonKernels.DefaultTruncationWidth);
            Assert.Equal(Heston.Price(OptionType.Call, 100.0, 100.0, 1.0, 1.0, p), price[0]);
            Assert.Equal(Heston.Price(OptionType.Put, 100.0, 80.0, 0.9, 0.75, p), price[1]);
        }

        [Fact]
        public void HostApi_RejectsInvalidArguments()
        {
            var p = FangOosterlee;
            Assert.Throws<ArgumentOutOfRangeException>(() => Heston.Price(OptionType.Call, 0.0, 100.0, 1.0, 1.0, p));
            Assert.Throws<ArgumentOutOfRangeException>(() => Heston.Price(OptionType.Call, 100.0, -1.0, 1.0, 1.0, p));
            Assert.Throws<ArgumentOutOfRangeException>(() => Heston.Price(OptionType.Call, 100.0, 100.0, 0.0, 1.0, p));
            Assert.Throws<ArgumentOutOfRangeException>(() => Heston.Price(OptionType.Call, 100.0, 100.0, 1.0, 0.0, p));
            Assert.Throws<ArgumentOutOfRangeException>(() => Heston.Price((OptionType)0, 100.0, 100.0, 1.0, 1.0, p));
            Assert.Throws<ArgumentOutOfRangeException>(() => Heston.Price(OptionType.Call, 100.0, 100.0, 1.0, 1.0, p, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Heston.Price(OptionType.Call, 100.0, 100.0, 1.0, 1.0, p with { Kappa = 0.0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => Heston.Price(OptionType.Call, 100.0, 100.0, 1.0, 1.0, p with { V0 = -0.01 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => Heston.Price(OptionType.Call, 100.0, 100.0, 1.0, 1.0, p with { Rho = -1.5 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => Heston.Price(OptionType.Call, 100.0, 100.0, 1.0, 1.0, p with { Xi = double.NaN }));
            Assert.Throws<ArgumentException>(() => Heston.Price(new[] { OptionType.Call }, new double[2], new double[1], new double[1], new double[1], p));
        }

        [Fact]
        public void Feller_IsReported()
        {
            Assert.False(FangOosterlee.SatisfiesFeller);
            Assert.Equal(2.0 * 1.5768 * 0.0398 / (0.5751 * 0.5751), FangOosterlee.FellerRatio, 1e-15);
            Assert.True(new HestonParameters(0.04, 2.0, 0.04, 0.3, -0.7).SatisfiesFeller);
        }
    }
}
