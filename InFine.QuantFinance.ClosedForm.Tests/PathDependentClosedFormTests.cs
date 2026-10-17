using System;
using InFine.QuantFinance.ClosedForm;
using Xunit;

namespace InFine.QuantFinance.ClosedForm.Tests
{
    /// <summary>
    /// Closed forms of the geometric Asian, continuous-barrier and continuous-lookback options against values produced by
    /// reference/path_dependent_reference.py, which integrates the law of the functional numerically (Simpson on the
    /// reflection-principle densities, brute-force covariance sums, tail integrals of the running extrema) and shares
    /// no formula with the C# code. Reference integration error is below 1e-10; tolerances are 1e-8 relative.
    /// </summary>
    public class PathDependentClosedFormTests
    {
        const double S = 100.0, R = 0.05, Q = 0.02, Sigma = 0.25, T = 1.0;
        const double RelTol = 1e-8;

        static void Close(double expected, double actual, string what = "")
        {
            double tol = RelTol * Math.Max(1.0, Math.Abs(expected));
            Assert.True(Math.Abs(expected - actual) <= tol, $"{what}: expected {expected:R}, actual {actual:R}");
        }

        static double[] EquallySpaced(int n, double maturity)
        {
            var t = new double[n];
            for (int i = 0; i < n; ++i) t[i] = maturity * (i + 1) / n;
            return t;
        }

        // ------------------------------------------------------------------ geometric Asian

        [Fact]
        public void GeometricAsianDiscreteMatchesReference()
        {
            double[] eq12 = EquallySpaced(12, T);
            double[] irr = { 0.1, 0.15, 0.4, 0.55, 0.9, 1.0 };
            Close(6.39033026606412e+00, AsianKernels.GeometricPrice(1, S, 100.0, T, eq12, 12, R, Q, Sigma), "eq12 call 100");
            Close(1.23643851528999e+01, AsianKernels.GeometricPrice(1, S, 90.0, T, eq12, 12, R, Q, Sigma), "eq12 call 90");
            Close(5.33071566117804e+00, AsianKernels.GeometricPrice(-1, S, 100.0, T, eq12, 12, R, Q, Sigma), "eq12 put 100");
            Close(1.79247630300667e+00, AsianKernels.GeometricPrice(-1, S, 90.0, T, eq12, 12, R, Q, Sigma), "eq12 put 90");
            Close(3.85089051632171e+00, AsianKernels.GeometricPrice(1, S, 105.0, 1.0, irr, 6, R, Q, Sigma), "irregular call");
            Close(7.69806331049898e+00, AsianKernels.GeometricPrice(-1, S, 105.0, 1.0, irr, 6, R, Q, Sigma), "irregular put");
        }

        [Fact]
        public void KemnaVorstEquallySpacedAgreesWithTheGeneralFormula()
        {
            foreach (int n in new[] { 1, 2, 12, 50, 250 })
                foreach (int sign in new[] { 1, -1 })
                {
                    double general = AsianKernels.GeometricPrice(sign, S, 97.0, 0.8, EquallySpaced(n, 0.8), n, R, Q, Sigma);
                    double kv = AsianKernels.GeometricPriceEquallySpaced(sign, S, 97.0, 0.8, n, R, Q, Sigma);
                    Close(general, kv, $"n={n} sign={sign}");
                }
        }

        [Fact]
        public void SingleFixingAtMaturityIsTheVanillaOption()
        {
            for (int sign = -1; sign <= 1; sign += 2)
                Close(BlackScholesKernels.Price(sign, S, 95.0, T, R, Q, Sigma),
                      AsianKernels.GeometricPriceEquallySpaced(sign, S, 95.0, T, 1, R, Q, Sigma));
        }

        [Fact]
        public void GeometricAsianContinuousMatchesReferenceAndIsTheLimitOfTheDiscreteForm()
        {
            Close(5.98019857970033e+00, AsianKernels.GeometricPriceContinuous(1, S, 100.0, T, R, Q, Sigma), "call");
            Close(5.04421146846942e+00, AsianKernels.GeometricPriceContinuous(-1, S, 100.0, T, R, Q, Sigma), "put");
            Close(3.53043627801490e+00, AsianKernels.GeometricPriceContinuous(1, S, 110.0, 2.0, 0.03, 0.0, 0.2), "call T=2");
            Close(1.07247935351391e+01, AsianKernels.GeometricPriceContinuous(-1, S, 110.0, 2.0, 0.03, 0.0, 0.2), "put T=2");
            // discrete -> continuous as the number of fixings grows: error of order 1/n
            double continuous = AsianKernels.GeometricPriceContinuous(1, S, 100.0, T, R, Q, Sigma);
            double previous = double.MaxValue;
            foreach (int n in new[] { 12, 120, 1200, 12000 })
            {
                double error = Math.Abs(AsianKernels.GeometricPriceEquallySpaced(1, S, 100.0, T, n, R, Q, Sigma) - continuous);
                Assert.True(error < previous / 5.0, $"n={n}: error {error} did not shrink from {previous}");
                previous = error;
            }
            Assert.True(previous < 1e-3);
        }

        [Fact]
        public void GeometricAverageStrikeMatchesReference()
        {
            double[] eq12 = EquallySpaced(12, T);
            double[] irr = { 0.1, 0.15, 0.4, 0.55, 0.9, 1.0 };
            Close(6.20628726947433e+00, AsianKernels.GeometricAverageStrikePrice(1, S, T, eq12, 12, R, Q, Sigma), "eq12 call");
            Close(4.36897699375635e+00, AsianKernels.GeometricAverageStrikePrice(-1, S, T, eq12, 12, R, Q, Sigma), "eq12 put");
            Close(6.27607463714622e+00, AsianKernels.GeometricAverageStrikePrice(1, S, T, irr, 6, R, Q, Sigma), "irr call");
            Close(4.28812408486849e+00, AsianKernels.GeometricAverageStrikePrice(-1, S, T, irr, 6, R, Q, Sigma), "irr put");
            Close(6.61791163371023e+00, AsianKernels.GeometricAverageStrikePriceContinuous(1, S, T, R, Q, Sigma), "continuous call");
            Close(4.65697385651986e+00, AsianKernels.GeometricAverageStrikePriceContinuous(-1, S, T, R, Q, Sigma), "continuous put");
        }

        [Fact]
        public void GeometricAverageStrikeCallMinusPutIsForwardMinusDiscountedGeometricForward()
        {
            // E[S_T - G] discounted: parity of the exchange option, independent of the volatility structure.
            double[] t = EquallySpaced(12, T);
            double m, v;
            AsianKernels.GeometricLogMoments(t, 12, S, R, Q, Sigma, out m, out v);
            double expected = Math.Exp(-R * T) * (S * Math.Exp((R - Q) * T) - Math.Exp(m + 0.5 * v));
            double actual = AsianKernels.GeometricAverageStrikePrice(1, S, T, t, 12, R, Q, Sigma)
                          - AsianKernels.GeometricAverageStrikePrice(-1, S, T, t, 12, R, Q, Sigma);
            Close(expected, actual);
        }

        // ------------------------------------------------------------------ barriers

        readonly struct BarrierCase
        {
            public readonly int Sign; public readonly bool Up, In; public readonly double K, H, Rebate, Expected;
            public BarrierCase(int sign, bool up, bool isIn, double k, double h, double rebate, double expected)
            { Sign = sign; Up = up; In = isIn; K = k; H = h; Rebate = rebate; Expected = expected; }
        }

        // S = 100, T = 1, r = 5%, q = 2%, sigma = 25%; strike/barrier pairs cover K > H and K < H for both barrier sides.
        static readonly BarrierCase[] BarrierCases =
        {
            new BarrierCase(1, false, false, 100.0, 90.0, 0.0, 8.13881054762621e+00),
            new BarrierCase(1, false, false, 100.0, 90.0, 3.0, 1.01354311905631e+01),
            new BarrierCase(1, false, false, 100.0, 95.0, 0.0, 4.92982730871039e+00),
            new BarrierCase(1, false, false, 100.0, 95.0, 3.0, 7.42612826204670e+00),
            new BarrierCase(1, false, false, 110.0, 90.0, 0.0, 5.56261581789509e+00),
            new BarrierCase(1, false, false, 110.0, 90.0, 3.0, 7.55923646083203e+00),
            new BarrierCase(1, false, false, 90.0, 85.0, 0.0, 1.41157320517152e+01),
            new BarrierCase(1, false, false, 90.0, 85.0, 3.0, 1.56377098098740e+01),
            new BarrierCase(1, false, false, 90.0, 95.0, 0.0, 6.46262816518250e+00),
            new BarrierCase(1, false, false, 90.0, 95.0, 3.0, 8.95892911851881e+00),
            new BarrierCase(-1, false, false, 100.0, 90.0, 0.0, 8.68162347451962e-02),
            new BarrierCase(-1, false, false, 100.0, 90.0, 3.0, 2.08343687768213e+00),
            new BarrierCase(-1, false, false, 100.0, 95.0, 0.0, 5.38659719857773e-03),
            new BarrierCase(-1, false, false, 100.0, 95.0, 3.0, 2.50168755053489e+00),
            new BarrierCase(-1, false, false, 110.0, 90.0, 0.0, 6.03539094222968e-01),
            new BarrierCase(-1, false, false, 110.0, 90.0, 3.0, 2.60015973715991e+00),
            new BarrierCase(-1, false, false, 90.0, 85.0, 0.0, 1.75759789574207e-02),
            new BarrierCase(-1, false, false, 90.0, 85.0, 3.0, 1.53955373711614e+00),
            new BarrierCase(-1, false, false, 90.0, 95.0, 0.0, 0.00000000000000e+00),
            new BarrierCase(-1, false, false, 90.0, 95.0, 3.0, 2.49630095333631e+00),
            new BarrierCase(1, false, true, 100.0, 90.0, 0.0, 2.98495138043437e+00),
            new BarrierCase(1, false, true, 100.0, 90.0, 3.0, 3.91282665719738e+00),
            new BarrierCase(1, false, true, 100.0, 95.0, 0.0, 6.19393461935017e+00),
            new BarrierCase(1, false, true, 100.0, 95.0, 3.0, 6.65539085545139e+00),
            new BarrierCase(1, false, true, 110.0, 90.0, 0.0, 1.54948653023835e+00),
            new BarrierCase(1, false, true, 110.0, 90.0, 3.0, 2.47736180700137e+00),
            new BarrierCase(1, false, true, 90.0, 85.0, 0.0, 2.52007807255128e+00),
            new BarrierCase(1, false, true, 90.0, 85.0, 3.0, 3.89749763252902e+00),
            new BarrierCase(1, false, true, 90.0, 95.0, 0.0, 1.01731819590832e+01),
            new BarrierCase(1, false, true, 90.0, 95.0, 3.0, 1.06346381951845e+01),
            new BarrierCase(-1, false, true, 100.0, 90.0, 0.0, 8.14002081270975e+00),
            new BarrierCase(-1, false, true, 100.0, 90.0, 3.0, 9.06789608947277e+00),
            new BarrierCase(-1, false, true, 100.0, 95.0, 0.0, 8.22145045025729e+00),
            new BarrierCase(-1, false, true, 100.0, 95.0, 3.0, 8.68290668635850e+00),
            new BarrierCase(-1, false, true, 110.0, 90.0, 0.0, 1.31239326183120e+01),
            new BarrierCase(-1, false, true, 110.0, 90.0, 3.0, 1.40518078950751e+01),
            new BarrierCase(-1, false, true, 90.0, 85.0, 0.0, 4.20901501969441e+00),
            new BarrierCase(-1, false, true, 90.0, 85.0, 3.0, 5.58643457967215e+00),
            new BarrierCase(-1, false, true, 90.0, 95.0, 0.0, 4.22659099865272e+00),
            new BarrierCase(-1, false, true, 90.0, 95.0, 3.0, 4.68804723475394e+00),
            new BarrierCase(1, true, false, 100.0, 115.0, 0.0, 2.62329689848828e-01),
            new BarrierCase(1, true, false, 100.0, 115.0, 3.0, 1.95614261608086e+00),
            new BarrierCase(1, true, false, 100.0, 108.0, 0.0, 2.72210605392472e-02),
            new BarrierCase(1, true, false, 100.0, 108.0, 3.0, 2.27426669840061e+00),
            new BarrierCase(1, true, false, 110.0, 125.0, 0.0, 2.78930703837646e-01),
            new BarrierCase(1, true, false, 110.0, 125.0, 3.0, 1.36348006314481e+00),
            new BarrierCase(1, true, false, 90.0, 120.0, 0.0, 2.23845482376134e+00),
            new BarrierCase(1, true, false, 90.0, 120.0, 3.0, 3.60162979640729e+00),
            new BarrierCase(1, true, false, 110.0, 108.0, 0.0, 0.00000000000000e+00),
            new BarrierCase(1, true, false, 110.0, 108.0, 3.0, 2.24704563786136e+00),
            new BarrierCase(-1, true, false, 100.0, 115.0, 0.0, 6.80282649270797e+00),
            new BarrierCase(-1, true, false, 100.0, 115.0, 3.0, 8.49663941894000e+00),
            new BarrierCase(-1, true, false, 100.0, 108.0, 0.0, 4.74817911879479e+00),
            new BarrierCase(-1, true, false, 100.0, 108.0, 3.0, 6.99522475665615e+00),
            new BarrierCase(-1, true, false, 110.0, 125.0, 0.0, 1.28620902387391e+01),
            new BarrierCase(-1, true, false, 110.0, 125.0, 3.0, 1.39466395980463e+01),
            new BarrierCase(-1, true, false, 90.0, 120.0, 0.0, 3.99637567532552e+00),
            new BarrierCase(-1, true, false, 90.0, 120.0, 3.0, 5.35955064797146e+00),
            new BarrierCase(-1, true, false, 110.0, 108.0, 0.0, 7.03213406935662e+00),
            new BarrierCase(-1, true, false, 110.0, 108.0, 3.0, 9.27917970721798e+00),
            new BarrierCase(1, true, true, 100.0, 115.0, 0.0, 1.08614322382104e+01),
            new BarrierCase(1, true, true, 100.0, 115.0, 3.0, 1.20756239820030e+01),
            new BarrierCase(1, true, true, 100.0, 108.0, 0.0, 1.10965408675209e+01),
            new BarrierCase(1, true, true, 100.0, 108.0, 3.0, 1.17898936708512e+01),
            new BarrierCase(1, true, true, 110.0, 125.0, 0.0, 6.83317164429381e+00),
            new BarrierCase(1, true, true, 110.0, 125.0, 3.0, 8.62977927773742e+00),
            new BarrierCase(1, true, true, 90.0, 120.0, 0.0, 1.43973553005013e+01),
            new BarrierCase(1, true, true, 90.0, 120.0, 3.0, 1.59265651888561e+01),
            new BarrierCase(1, true, true, 110.0, 108.0, 0.0, 7.11210234813344e+00),
            new BarrierCase(1, true, true, 110.0, 108.0, 3.0, 7.80545515146376e+00),
            new BarrierCase(-1, true, true, 100.0, 115.0, 0.0, 1.42401055474851e+00),
            new BarrierCase(-1, true, true, 100.0, 115.0, 3.0, 2.63820229854113e+00),
            new BarrierCase(-1, true, true, 100.0, 108.0, 0.0, 3.47865792866169e+00),
            new BarrierCase(-1, true, true, 100.0, 108.0, 3.0, 4.17201073199201e+00),
            new BarrierCase(-1, true, true, 110.0, 125.0, 0.0, 8.65381473797501e-01),
            new BarrierCase(-1, true, true, 110.0, 125.0, 3.0, 2.66198910724111e+00),
            new BarrierCase(-1, true, true, 90.0, 120.0, 0.0, 2.30215323327210e-01),
            new BarrierCase(-1, true, true, 90.0, 120.0, 3.0, 1.75942521168200e+00),
            new BarrierCase(-1, true, true, 110.0, 108.0, 0.0, 6.69533764318202e+00),
            new BarrierCase(-1, true, true, 110.0, 108.0, 3.0, 7.38869044651234e+00),
        };

        [Fact]
        public void AllEightBarrierTypesWithAndWithoutRebateMatchReference()
        {
            Assert.Equal(80, BarrierCases.Length);
            foreach (var c in BarrierCases)
            {
                double price = BarrierKernels.Price(c.Sign, c.Up, c.In, S, c.K, c.H, c.Rebate, T, R, Q, Sigma);
                Close(c.Expected, price, $"sign={c.Sign} up={c.Up} in={c.In} K={c.K} H={c.H} R={c.Rebate}");
            }
        }

        [Fact]
        public void BarrierZeroCarryMatchesReference()
        {
            Close(7.82678199079900e+00, BarrierKernels.Price(1, false, false, 100.0, 100.0, 90.0, 2.0, 0.5, 0.04, 0.04, 0.3), "down-out call");
            Close(1.62569976722208e+00, BarrierKernels.Price(-1, true, true, 100.0, 100.0, 120.0, 2.0, 0.5, 0.04, 0.04, 0.3), "up-in put");
        }

        [Fact]
        public void InPlusOutIsTheVanillaOptionWithoutRebate()
        {
            foreach (int sign in new[] { 1, -1 })
                foreach (bool up in new[] { false, true })
                    foreach (double k in new[] { 85.0, 100.0, 118.0 })
                        foreach (double h in up ? new[] { 105.0, 120.0 } : new[] { 80.0, 95.0 })
                        {
                            double vanilla = BlackScholesKernels.Price(sign, S, k, T, R, Q, Sigma);
                            double inn = BarrierKernels.Price(sign, up, true, S, k, h, 0.0, T, R, Q, Sigma);
                            double @out = BarrierKernels.Price(sign, up, false, S, k, h, 0.0, T, R, Q, Sigma);
                            Assert.True(Math.Abs(inn + @out - vanilla) < 1e-11 * Math.Max(1.0, vanilla),
                                $"sign={sign} up={up} K={k} H={h}: {inn} + {@out} != {vanilla}");
                        }
        }

        [Fact]
        public void RebateTermsAreAdditiveAndInOutParityHoldsWithRebates()
        {
            // in(R) + out(R) = vanilla + E + F, with E and F linear in the rebate.
            double e0, f0, a, b, c, d;
            BarrierKernels.Terms(1, 1, S, 100.0, 90.0, 4.0, T, R, Q, Sigma, out a, out b, out c, out d, out e0, out f0);
            double inn = BarrierKernels.Price(1, false, true, S, 100.0, 90.0, 4.0, T, R, Q, Sigma);
            double @out = BarrierKernels.Price(1, false, false, S, 100.0, 90.0, 4.0, T, R, Q, Sigma);
            Close(BlackScholesKernels.Price(1, S, 100.0, T, R, Q, Sigma) + e0 + f0, inn + @out);
        }

        [Fact]
        public void BarrierBeyondSpotAndZeroMaturityLimits()
        {
            double vanilla = BlackScholesKernels.Price(1, S, 100.0, T, R, Q, Sigma);
            Close(vanilla, BarrierKernels.Price(1, false, true, S, 100.0, 100.0, 1.0, T, R, Q, Sigma), "in, spot on barrier");
            Close(2.5, BarrierKernels.Price(1, false, false, S, 100.0, 105.0, 2.5, T, R, Q, Sigma), "out, spot below down barrier");
            Close(vanilla, BarrierKernels.Price(1, true, true, S, 100.0, 95.0, 0.0, T, R, Q, Sigma), "up-in above barrier");
            Close(3.0, BarrierKernels.Price(1, true, true, 100.0, 100.0, 120.0, 3.0, 0.0, R, Q, Sigma), "in at expiry: rebate");
            Close(10.0, BarrierKernels.Price(1, true, false, 110.0, 100.0, 120.0, 3.0, 0.0, R, Q, Sigma), "out at expiry: intrinsic");
            Assert.True(double.IsNaN(BarrierKernels.Price(1, true, false, S, 100.0, 120.0, 0.0, T, R, Q, 0.0)));
        }

        [Fact]
        public void BarrierPriceIsContinuousInTheStrikeAcrossTheBarrier()
        {
            // the K >= H and K < H branches of the table must meet at K = H
            foreach (bool up in new[] { false, true })
                foreach (bool isIn in new[] { false, true })
                    foreach (int sign in new[] { 1, -1 })
                    {
                        double h = up ? 108.0 : 95.0;
                        double above = BarrierKernels.Price(sign, up, isIn, S, h * (1.0 + 1e-9), h, 1.0, T, R, Q, Sigma);
                        double below = BarrierKernels.Price(sign, up, isIn, S, h * (1.0 - 1e-9), h, 1.0, T, R, Q, Sigma);
                        Assert.True(Math.Abs(above - below) < 1e-6, $"up={up} in={isIn} sign={sign}: {above} vs {below}");
                    }
        }

        // ------------------------------------------------------------------ lookbacks

        [Fact]
        public void FloatingStrikeLookbackMatchesReference()
        {
            Close(1.92980879622695e+01, LookbackKernels.FloatingStrikePrice(1, S, S, T, R, Q, Sigma), "call new");
            Close(2.02823537542924e+01, LookbackKernels.FloatingStrikePrice(1, S, 92.0, T, R, Q, Sigma), "call seasoned");
            Close(1.86754416597949e+01, LookbackKernels.FloatingStrikePrice(1, S, S, 0.75, 0.03, 0.03, 0.3), "call zero carry");
            Close(1.94187931656279e+01, LookbackKernels.FloatingStrikePrice(-1, S, S, T, R, Q, Sigma), "put new");
            Close(2.06073220965082e+01, LookbackKernels.FloatingStrikePrice(-1, S, 109.0, T, R, Q, Sigma), "put seasoned");
            Close(2.19753520853223e+01, LookbackKernels.FloatingStrikePrice(-1, S, S, 0.75, 0.03, 0.03, 0.3), "put zero carry");
        }

        [Fact]
        public void FixedStrikeLookbackMatchesReference()
        {
            Close(2.23157180462320e+01, LookbackKernels.FixedStrikePrice(1, S, 100.0, S, T, R, Q, Sigma), "call ATM");
            Close(1.12343705513450e+01, LookbackKernels.FixedStrikePrice(1, S, 115.0, S, T, R, Q, Sigma), "call OTM");
            Close(2.73124468655268e+01, LookbackKernels.FixedStrikePrice(1, S, 95.0, 104.0, T, R, Q, Sigma), "call ITM seasoned");
            Close(2.19753520853223e+01, LookbackKernels.FixedStrikePrice(1, S, 100.0, S, 0.75, 0.03, 0.03, 0.3), "call zero carry");
            Close(1.64011630816654e+01, LookbackKernels.FixedStrikePrice(-1, S, 100.0, S, T, R, Q, Sigma), "put ATM");
            Close(5.59876849554721e+00, LookbackKernels.FixedStrikePrice(-1, S, 85.0, S, T, R, Q, Sigma), "put OTM");
            Close(2.14015359040556e+01, LookbackKernels.FixedStrikePrice(-1, S, 105.0, 96.0, T, R, Q, Sigma), "put ITM seasoned");
            Close(1.86754416597949e+01, LookbackKernels.FixedStrikePrice(-1, S, 100.0, S, 0.75, 0.03, 0.03, 0.3), "put zero carry");
        }

        [Fact]
        public void LookbackPriceIsContinuousAcrossTheZeroCarrySwitch()
        {
            // |b| < 1e-5 uses the averaged formula. The price is smooth in b (slope about 56 per unit of carry here), so
            // the averaged value at b = 5e-6 must sit on the chord between the direct formula just outside the switch
            // at b = +-1.01e-5; the chord error is a few 1e-9 (curvature), a wrong branch would be off by 1e-5 or more.
            double lower = LookbackKernels.FloatingStrikePrice(1, S, S, T, 0.05, 0.05 + 1.01e-5, Sigma);
            double upper = LookbackKernels.FloatingStrikePrice(1, S, S, T, 0.05, 0.05 - 1.01e-5, Sigma);
            double inside = LookbackKernels.FloatingStrikePrice(1, S, S, T, 0.05, 0.05 - 5e-6, Sigma);
            double chord = lower + (upper - lower) * (5e-6 + 1.01e-5) / (2.02e-5);
            Assert.True(Math.Abs(inside - chord) < 1e-7, $"{inside} vs chord {chord}");
        }
    }
}
