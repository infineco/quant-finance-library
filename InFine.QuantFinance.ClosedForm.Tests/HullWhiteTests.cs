using System;
using InFine.QuantFinance.ClosedForm;
using Xunit;

namespace InFine.QuantFinance.ClosedForm.Tests
{
    /// <summary>
    /// Analytic Hull-White prices. References: the curve itself (fit at t = 0), model-free parities (put-call on bonds,
    /// payer-receiver on swaptions, cap-floor), the deterministic limit sigma = 0 (intrinsic values on the forward curve), and
    /// an independent numerical integration of the payoffs over the Gaussian state under the T-forward measure
    /// (<c>x(T) ~ N(-sigma^2 B(T)^2 / 2, V_x(T))</c>; composite Simpson on +/- 12 standard deviations, 20000 intervals), which
    /// shares neither the Black formula nor Jamshidian's decomposition with the code. Curve: <c>f(0, t) = 3.5% - 1.5% e^{-0.6 t}</c>.
    /// </summary>
    public class HullWhiteTests
    {
        const double C0 = 0.035, C1 = -0.015, K = 0.6;
        const double A = 0.07, Sigma = 0.012;

        static double Forward(double t) => C0 + C1 * Math.Exp(-K * t);
        static double Discount(double t) => Math.Exp(-(C0 * t + C1 * (1.0 - Math.Exp(-K * t)) / K));

        static HullWhite Model(double a = A, double sigma = Sigma) => new HullWhite(a, sigma, Discount, Forward);

        static void Close(double expected, double actual, double tolerance, string what = "")
        {
            Assert.True(Math.Abs(expected - actual) <= tolerance, $"{what}: expected {expected:R}, actual {actual:R}, diff {actual - expected:E3}");
        }

        /// <summary><c>P(0, T) E^T[g(x(T))]</c> by Simpson's rule on <c>[lo, hi]</c> (in standard deviations from the mean).</summary>
        static double ForwardMeasureExpectation(HullWhite model, double expiry, Func<double, double> payoff, double lo = -12.0, double hi = 12.0)
        {
            double b = HullWhiteKernels.B(model.MeanReversion, expiry);
            double mean = -0.5 * model.Volatility * model.Volatility * b * b;
            double sd = Math.Sqrt(model.StateVariance(expiry));
            const int n = 20000;
            double h = (hi - lo) / n, sum = 0.0;
            for (int i = 0; i <= n; i++)
            {
                double z = lo + i * h;
                double w = i == 0 || i == n ? 1.0 : (i % 2 == 1 ? 4.0 : 2.0);
                sum += w * payoff(mean + sd * z) * Math.Exp(-0.5 * z * z);
            }
            return model.DiscountFactor(expiry) * sum * h / 3.0 / Math.Sqrt(2.0 * Math.PI);
        }

        /// <summary>
        /// <c>P(0, T) E^T[max(g(x(T)), 0)]</c> for a monotone <c>g</c>: the kink is located by bisection and only the side where
        /// <c>g &gt; 0</c> is integrated, so Simpson's rule sees a smooth integrand (error O(h^4), not O(h^2)).
        /// </summary>
        static double ForwardMeasureOption(HullWhite model, double expiry, Func<double, double> g)
        {
            double b = HullWhiteKernels.B(model.MeanReversion, expiry);
            double mean = -0.5 * model.Volatility * model.Volatility * b * b;
            double sd = Math.Sqrt(model.StateVariance(expiry));
            Func<double, double> gz = z => g(mean + sd * z);
            double lo = -12.0, hi = 12.0;
            bool positiveLow = gz(lo) > 0.0;
            if (positiveLow == gz(hi) > 0.0)
                return positiveLow ? ForwardMeasureExpectation(model, expiry, g) : 0.0;
            for (int i = 0; i < 200; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (gz(mid) > 0.0 == positiveLow) lo = mid; else hi = mid;
            }
            double root = 0.5 * (lo + hi);
            Func<double, double> payoff = x => Math.Max(g(x), 0.0);
            return positiveLow
                ? ForwardMeasureExpectation(model, expiry, payoff, -12.0, root)
                : ForwardMeasureExpectation(model, expiry, payoff, root, 12.0);
        }

        [Fact]
        public void ZeroCouponBondAtTimeZeroReproducesTheCurve()
        {
            var model = Model();
            foreach (double t in new[] { 0.0, 0.01, 0.5, 1.0, 3.0, 7.5, 30.0 })
                Close(Discount(t), model.ZeroCouponBond(0.0, t, 0.0), 2e-16 * 4, $"P(0, {t})");
            // phi(0) = f(0, 0): the short rate starts on the curve.
            Close(Forward(0.0), model.Phi(0.0), 1e-18, "phi(0)");
            Close(Discount(4.0), model.ZeroCouponBondFromShortRate(0.0, 4.0, Forward(0.0)), 1e-15, "P(0, 4) from r(0)");
        }

        [Fact]
        public void ConditionalBondIsTheForwardMeasureMartingale()
        {
            // E^T[P(T, S; x)] P(0, T) = P(0, S): checks the convexity terms of A(T, S) against the state distribution.
            var model = Model();
            foreach (var (t, s) in new[] { (0.5, 1.0), (1.0, 5.0), (3.0, 10.0) })
                Close(Discount(s), ForwardMeasureExpectation(model, t, x => model.ZeroCouponBond(t, s, x)), 1e-13, $"E[P({t}, {s})]");
        }

        [Fact]
        public void ThetaIsTheDriftThatKeepsPhi()
        {
            // d phi / dt = theta - a phi.
            var model = Model();
            foreach (double t in new[] { 0.3, 1.0, 4.0 })
            {
                const double h = 1e-4;
                double dphi = (model.Phi(t + h) - model.Phi(t - h)) / (2.0 * h);
                Close(model.Theta(t) - A * model.Phi(t), dphi, 1e-8, $"theta({t})");
            }
        }

        [Fact]
        public void IntegralVarianceMatchesQuadratureAcrossTheSeriesSwitch()
        {
            // sigma^2 int_0^dt B(s)^2 ds by Simpson (independent of both branches).
            foreach (double a in new[] { 0.05, 0.5, 2.0 })
                foreach (double dt in new[] { 1e-6, 1e-3, 0.25, 1.999 / a, 2.001 / a, 1.0 / a - 1e-9, 1.0 / a + 1e-9, 6.0 / a })
                {
                    const int n = 2000;
                    double h = dt / n, sum = 0.0;
                    for (int i = 0; i <= n; i++)
                    {
                        double u = a * i * h;
                        double b = (u < 1e-3 ? u * (1.0 - u / 2.0 * (1.0 - u / 3.0 * (1.0 - u / 4.0))) : 1.0 - Math.Exp(-u)) / a;
                        sum += (i == 0 || i == n ? 1.0 : (i % 2 == 1 ? 4.0 : 2.0)) * b * b;
                    }
                    double expected = Sigma * Sigma * sum * h / 3.0;
                    double actual = HullWhiteKernels.IntegralVariance(a, Sigma, dt);
                    Assert.True(Math.Abs(actual / expected - 1.0) < 1e-11, $"a {a}, dt {dt}: {actual:R} vs {expected:R}");
                }
            Assert.Equal(0.0, HullWhiteKernels.IntegralVariance(A, Sigma, 0.0));
        }

        [Fact]
        public void ZeroBondOptionsMatchQuadratureAndPutCallParity()
        {
            var model = Model();
            foreach (var (t, s) in new[] { (0.5, 1.0), (1.0, 5.0), (3.0, 10.0) })
            {
                double forward = Discount(s) / Discount(t);
                foreach (double strike in new[] { 0.9 * forward, forward, 1.05 * forward })
                {
                    double call = model.ZeroCouponBondOption(OptionType.Call, t, s, strike);
                    double put = model.ZeroCouponBondOption(OptionType.Put, t, s, strike);
                    Close(Discount(s) - strike * Discount(t), call - put, 1e-15, $"parity T {t} S {s} K {strike}");
                    Close(ForwardMeasureOption(model, t, x => model.ZeroCouponBond(t, s, x) - strike), call, 1e-12, "call quadrature");
                    Close(ForwardMeasureOption(model, t, x => strike - model.ZeroCouponBond(t, s, x)), put, 1e-12, "put quadrature");
                }
            }
        }

        [Fact]
        public void ZeroBondOptionVolatilityIsTheJamshidianFormula()
        {
            double t = 2.0, s = 7.0;
            double expected = Sigma / A * (1.0 - Math.Exp(-A * (s - t))) * Math.Sqrt((1.0 - Math.Exp(-2.0 * A * t)) / (2.0 * A));
            Close(expected, HullWhiteKernels.ZeroBondOptionStdDev(A, Sigma, t, s), 1e-17);
        }

        [Fact]
        public void CapletsTendToTheirIntrinsicValueOnTheForwardCurveAsSigmaVanishes()
        {
            foreach (double sigma in new[] { 0.0, 1e-9 })
            {
                var model = Model(sigma: sigma);
                double start = 2.0, end = 2.5, tau = 0.5;
                double forward = (Discount(start) / Discount(end) - 1.0) / tau;
                foreach (double strike in new[] { forward - 0.01, forward + 0.01 })
                {
                    double tol = sigma == 0.0 ? 1e-15 : 1e-14;
                    Close(tau * Discount(end) * Math.Max(forward - strike, 0.0), model.Caplet(start, end, tau, strike), tol, $"caplet sigma {sigma} K {strike}");
                    Close(tau * Discount(end) * Math.Max(strike - forward, 0.0), model.Floorlet(start, end, tau, strike), tol, $"floorlet sigma {sigma} K {strike}");
                }
            }
        }

        [Fact]
        public void CapletMatchesQuadratureAndCapFloorParityHolds()
        {
            var model = Model();
            double start = 3.0, end = 3.25, tau = 0.25;
            double strike = 0.03;
            double expected = ForwardMeasureOption(model, start, x =>
            {
                // Paid at end; valued at start with P(start, end).
                double p = model.ZeroCouponBond(start, end, x);
                double libor = (1.0 / p - 1.0) / tau;
                return p * tau * (libor - strike);
            });
            Close(expected, model.Caplet(start, end, tau, strike), 1e-13, "caplet");

            double[] starts = { 1.0, 1.5, 2.0, 2.5 }, ends = { 1.5, 2.0, 2.5, 3.0 }, accruals = { 0.5, 0.5, 0.5, 0.5 };
            double cap = model.Cap(starts, ends, accruals, strike, 1e6);
            double floor = model.Floor(starts, ends, accruals, strike, 1e6);
            double swap = 0.0, sumCaplets = 0.0;
            for (int i = 0; i < starts.Length; i++)
            {
                swap += Discount(starts[i]) - Discount(ends[i]) - strike * accruals[i] * Discount(ends[i]);
                sumCaplets += model.Caplet(starts[i], ends[i], accruals[i], strike, 1e6);
            }
            Close(1e6 * swap, cap - floor, 1e-8, "cap - floor");
            Close(sumCaplets, cap, 1e-9, "cap = sum of caplets");
        }

        [Fact]
        public void CouponBondOptionMatchesQuadrature()
        {
            var model = Model();
            double expiry = 2.0;
            double[] times = { 2.5, 3.0, 3.5, 4.0, 4.5, 5.0 };
            double[] flows = { 2.0, 2.0, 2.0, 2.0, 2.0, 102.0 };
            double forwardValue = 0.0;
            foreach (var (t, c) in Zip(times, flows)) forwardValue += c * Discount(t) / Discount(expiry);
            foreach (double strike in new[] { 0.98 * forwardValue, forwardValue, 1.02 * forwardValue })
                foreach (var type in new[] { OptionType.Call, OptionType.Put })
                {
                    double expected = ForwardMeasureOption(model, expiry, x =>
                    {
                        double value = 0.0;
                        for (int i = 0; i < times.Length; i++) value += flows[i] * model.ZeroCouponBond(expiry, times[i], x);
                        return (int)type * (value - strike);
                    });
                    Close(expected, model.CouponBondOption(type, expiry, times, flows, strike), 1e-10, $"{type} K {strike}");
                }
        }

        [Fact]
        public void SwaptionIsTheSumOfZeroBondOptionsAtTheCriticalRate()
        {
            // Jamshidian: payer = sum c_i ZBP(T0, T_i, X_i), X_i = P(T0, T_i; r*), and sum c_i X_i = 1.
            var model = Model();
            double expiry = 1.0, rate = 0.032;
            double[] times = { 2.0, 3.0, 4.0, 5.0 }, accruals = { 1.0, 1.0, 1.0, 1.0 };
            double rStar = model.SwaptionCriticalShortRate(expiry, times, accruals, rate);
            double payer = 0.0, receiver = 0.0, sum = 0.0;
            for (int i = 0; i < times.Length; i++)
            {
                double c = rate * accruals[i] + (i == times.Length - 1 ? 1.0 : 0.0);
                double strike = model.ZeroCouponBondFromShortRate(expiry, times[i], rStar);
                sum += c * strike;
                payer += c * model.ZeroCouponBondOption(OptionType.Put, expiry, times[i], strike);
                receiver += c * model.ZeroCouponBondOption(OptionType.Call, expiry, times[i], strike);
            }
            Close(1.0, sum, 1e-14, "sum c_i X_i");
            Close(payer, model.Swaption(SwaptionType.Payer, expiry, times, accruals, rate), 1e-15, "payer");
            Close(receiver, model.Swaption(SwaptionType.Receiver, expiry, times, accruals, rate), 1e-15, "receiver");

            // Payer - receiver = forward payer swap.
            double annuity = 0.0;
            for (int i = 0; i < times.Length; i++) annuity += accruals[i] * Discount(times[i]);
            Close(Discount(expiry) - Discount(times[^1]) - rate * annuity, payer - receiver, 1e-15, "parity");
            Close((Discount(expiry) - Discount(times[^1])) / annuity, model.ForwardSwapRate(expiry, times, accruals), 1e-16, "forward swap rate");
        }

        [Fact]
        public void SwaptionMatchesQuadratureAndItsDeterministicLimit()
        {
            double expiry = 2.0;
            double[] times = { 2.5, 3.0, 3.5, 4.0, 4.5, 5.0, 5.5, 6.0, 6.5, 7.0 };
            var accruals = new double[times.Length];
            for (int i = 0; i < times.Length; i++) accruals[i] = 0.5;
            var model = Model();
            double atm = model.ForwardSwapRate(expiry, times, accruals);
            foreach (double strike in new[] { atm - 0.01, atm, atm + 0.01 })
            {
                double expected = ForwardMeasureOption(model, expiry, x =>
                {
                    double fixedLeg = 0.0;
                    for (int i = 0; i < times.Length; i++) fixedLeg += strike * accruals[i] * model.ZeroCouponBond(expiry, times[i], x);
                    return 1.0 - model.ZeroCouponBond(expiry, times[^1], x) - fixedLeg;
                });
                Close(expected, model.Swaption(SwaptionType.Payer, expiry, times, accruals, strike), 1e-13, $"payer K {strike}");

                double annuity = 0.0;
                foreach (var t in times) annuity += 0.5 * Discount(t);
                Close(annuity * Math.Max(atm - strike, 0.0), Model(sigma: 0.0).Swaption(SwaptionType.Payer, expiry, times, accruals, strike), 1e-15, "sigma 0");
            }
        }

        [Fact]
        public void CalibrationRecoversTheParametersThatGeneratedThePrices()
        {
            var truth = Model(0.09, 0.0105);
            var targets = new System.Collections.Generic.List<HullWhiteCalibrationTarget>();
            foreach (double expiry in new[] { 1.0, 2.0, 5.0 })
                foreach (int tenor in new[] { 2, 5, 10 })
                {
                    var times = new double[tenor];
                    var accruals = new double[tenor];
                    for (int i = 0; i < tenor; i++) { times[i] = expiry + i + 1; accruals[i] = 1.0; }
                    double strike = truth.ForwardSwapRate(expiry, times, accruals);
                    double price = truth.Swaption(SwaptionType.Payer, expiry, times, accruals, strike);
                    targets.Add(new HullWhiteCalibrationTarget(m => m.Swaption(SwaptionType.Payer, expiry, times, accruals, strike), price));
                }
            var result = HullWhiteCalibrator.Calibrate(Model(0.03, 0.005), targets);
            Assert.True(result.Fit.Converged, result.Fit.Status.ToString());
            Close(0.09, result.Model.MeanReversion, 1e-7, "a");
            Close(0.0105, result.Model.Volatility, 1e-9, "sigma");
            Assert.True(result.RootMeanSquareRelativeError < 1e-10, $"rmse {result.RootMeanSquareRelativeError}");

            // Sigma only, a fixed at the truth.
            var sigmaOnly = HullWhiteCalibrator.Calibrate(Model(0.09, 0.02), targets, calibrateMeanReversion: false);
            Close(0.0105, sigmaOnly.Model.Volatility, 1e-10, "sigma only");
        }

        [Fact]
        public void BlackQuotedTargetsCarryTheBlackPrice()
        {
            var model = Model();
            double expiry = 1.0, vol = 0.2;
            double[] times = { 2.0, 3.0 }, accruals = { 1.0, 1.0 };
            double forward = model.ForwardSwapRate(expiry, times, accruals);
            double annuity = Discount(2.0) + Discount(3.0);
            var target = HullWhiteCalibrationTarget.Swaption(model, SwaptionType.Payer, expiry, times, accruals, forward, vol);
            // ATM Black: F (2 Phi(v / 2) - 1).
            Close(annuity * forward * (2.0 * NormalDistribution.Cdf(0.5 * vol) - 1.0), target.MarketPrice, 1e-16, "ATM swaption");

            var cap = HullWhiteCalibrationTarget.Cap(model, new[] { 1.0 }, new[] { 1.5 }, new[] { 0.5 }, 0.03, vol);
            double f = (Discount(1.0) / Discount(1.5) - 1.0) / 0.5;
            Close(0.5 * Discount(1.5) * Black76Kernels.NormalisedPrice(1, f, 0.03, vol), cap.MarketPrice, 1e-17, "caplet");

            // A flat 1% normal-ish level calibrates sigma alone to a set of caps quoted at one Black vol to a few percent.
            var caps = new System.Collections.Generic.List<HullWhiteCalibrationTarget>();
            foreach (int n in new[] { 4, 8, 12 })
            {
                var s = new double[n]; var e = new double[n]; var tau = new double[n];
                for (int i = 0; i < n; i++) { s[i] = 0.5 * (i + 1); e[i] = s[i] + 0.5; tau[i] = 0.5; }
                caps.Add(HullWhiteCalibrationTarget.Cap(model, s, e, tau, 0.03, 0.3));
            }
            var fit = HullWhiteCalibrator.Calibrate(model, caps, calibrateMeanReversion: false);
            Assert.True(fit.Model.Volatility > 0.003 && fit.Model.Volatility < 0.03, $"sigma {fit.Model.Volatility}");
            Assert.True(fit.RootMeanSquareRelativeError < 0.1, $"rmse {fit.RootMeanSquareRelativeError}");
        }

        [Fact]
        public void HostApiRejectsInvalidArguments()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new HullWhite(0.0, Sigma, Discount, Forward));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HullWhite(A, -0.01, Discount, Forward));
            Assert.Throws<ArgumentNullException>(() => new HullWhite(A, Sigma, null, Forward));
            var model = Model();
            Assert.Throws<ArgumentOutOfRangeException>(() => model.ZeroCouponBondOption(OptionType.Call, 2.0, 1.0, 0.9));
            Assert.Throws<ArgumentException>(() => model.Swaption(SwaptionType.Payer, 2.0, new[] { 1.5, 3.0 }, new[] { 1.0, 1.0 }, 0.03));
            Assert.Throws<ArgumentException>(() => model.Swaption(SwaptionType.Payer, 1.0, new[] { 2.0 }, new[] { 1.0, 1.0 }, 0.03));
            Assert.Throws<ArgumentOutOfRangeException>(() => model.Caplet(1.0, 1.0, 0.5, 0.03));
        }

        static System.Collections.Generic.IEnumerable<(double, double)> Zip(double[] a, double[] b)
        {
            for (int i = 0; i < a.Length; i++) yield return (a[i], b[i]);
        }
    }
}
