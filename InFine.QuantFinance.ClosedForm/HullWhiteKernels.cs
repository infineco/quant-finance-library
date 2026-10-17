using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Kernel-compatible closed forms of the one-factor Hull-White model fitted to an initial discount curve
    /// <c>P(0, t)</c>. No allocation, no exceptions, only <c>Math.*</c>, <see cref="NormalDistribution"/> and
    /// <see cref="Black76Kernels"/> calls; the host API with argument checks is <see cref="HullWhite"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The short rate <c>dr = (theta(t) - a r) dt + sigma dW</c> is written <c>r(t) = x(t) + phi(t)</c> with
    /// <c>dx = -a x dt + sigma dW</c>, <c>x(0) = 0</c>, and <c>phi(t) = f(0, t) + sigma^2 B(0, t)^2 / 2</c>, where
    /// <c>B(t, T) = (1 - exp(-a (T - t))) / a</c> and <c>f(0, t)</c> is the instantaneous forward of the curve. This fits the
    /// curve exactly without differentiating <c>f</c> (Brigo and Mercurio, "Interest Rate Models - Theory and Practice",
    /// 2nd ed., section 3.3).
    /// </para>
    /// <para>
    /// Zero-coupon bond given the state at <c>t</c>:
    /// <c>ln P(t, T) = ln(P(0, T) / P(0, t)) - B(t, T) x - (B(t, T)^2 V_x(t) + sigma^2 B(t, T) B(0, t)^2) / 2</c>, with
    /// <c>V_x(t) = Var x(t) = sigma^2 (1 - exp(-2 a t)) / (2 a)</c>. Under the risk-neutral measure the bank-account integral
    /// <c>I(t) = int_0^t x ds</c> and <c>x(t)</c> are jointly Gaussian with <c>Cov(x(t), I(t)) = sigma^2 B(0, t)^2 / 2</c>; the
    /// formula above is what makes <c>E[exp(-int_0^t r ds) P(t, T)] = P(0, T)</c>.
    /// </para>
    /// <para>
    /// Options on zero-coupon bonds are Black-76 on the forward bond price <c>P(0, S) / P(0, T)</c> with the total standard
    /// deviation <c>sigma_P = B(T, S) sqrt(V_x(T))</c> (Jamshidian 1989). Caplets and floorlets are puts and calls on bonds;
    /// options on coupon bonds and swaptions follow from Jamshidian's decomposition, the critical state being found by
    /// Brent's method on the monotone bond price.
    /// </para>
    /// <para>
    /// <c>a</c> must be positive; <c>a = 0</c> is accepted as the Ho-Lee limit (B(t, T) = T - t). <c>sigma = 0</c> gives the
    /// deterministic limit (intrinsic values on the forward curve).
    /// </para>
    /// </remarks>
    public static class HullWhiteKernels
    {
        /// <summary><c>exp(x) - 1</c> to a few ulps for small <c>|x|</c> (Kahan's trick); .NET has no <c>Math.Expm1</c>.</summary>
        [Kernel]
        public static double Expm1(double x)
        {
            if (!(Math.Abs(x) < 0.5))
                return Math.Exp(x) - 1.0;
            double e = Math.Exp(x);
            if (e == 1.0) return x;
            return (e - 1.0) * x / Math.Log(e);
        }

        /// <summary><c>B(tau) = (1 - exp(-a tau)) / a</c>, and <c>tau</c> when <c>a = 0</c>.</summary>
        [Kernel]
        public static double B(double a, double tau)
        {
            if (a == 0.0) return tau;
            return -Expm1(-a * tau) / a;
        }

        /// <summary><c>V_x(t) = Var x(t) = sigma^2 (1 - exp(-2 a t)) / (2 a)</c>, and <c>sigma^2 t</c> when <c>a = 0</c>.</summary>
        [Kernel]
        public static double StateVariance(double a, double sigma, double t)
        {
            return sigma * sigma * B(2.0 * a, t);
        }

        /// <summary>
        /// Variance of the integral <c>int_t^{t+dt} x ds</c> given <c>x(t)</c>:
        /// <c>sigma^2 / a^2 (dt - 2 B(dt) + (1 - exp(-2 a dt)) / (2 a)) = sigma^2 dt^3 g(a dt)</c>. For <c>a dt &lt; 1</c>, where the
        /// difference cancels, <c>g(u) = sum_{n &gt;= 3} (-1)^n (2 - 2^(n-1)) u^(n-3) / n! = 1/3 - u/4 + 7 u^2/60 - ...</c> is summed
        /// directly (30 terms, truncation below 1e-24). With <c>x(0) = 0</c> and <c>dt = t</c> it is <c>Var int_0^t x ds</c>.
        /// </summary>
        [Kernel]
        public static double IntegralVariance(double a, double sigma, double dt)
        {
            double u = a * dt;
            double g;
            if (Math.Abs(u) < 1.0)
            {
                g = 0.0;
                double power = 1.0, sign = -1.0, twoPower = 4.0, factorial = 6.0;
                for (int n = 3; n < 33; ++n)
                {
                    g += sign * (2.0 - twoPower) * power / factorial;
                    power *= u;
                    sign = -sign;
                    twoPower *= 2.0;
                    factorial *= n + 1;
                }
            }
            else
            {
                g = (u + 2.0 * Expm1(-u) - 0.5 * Expm1(-2.0 * u)) / (u * u * u);
            }
            return sigma * sigma * dt * dt * dt * g;
        }

        /// <summary>
        /// <c>Cov(x(t + dt), int_t^{t+dt} x ds | x(t)) = sigma^2 B(dt)^2 / 2</c>.
        /// </summary>
        [Kernel]
        public static double StateIntegralCovariance(double a, double sigma, double dt)
        {
            double b = B(a, dt);
            return 0.5 * sigma * sigma * b * b;
        }

        /// <summary>
        /// <c>phi(t) = f(0, t) + sigma^2 B(0, t)^2 / 2</c>: the deterministic shift with <c>r(t) = x(t) + phi(t)</c>.
        /// </summary>
        /// <param name="a">Mean reversion.</param>
        /// <param name="sigma">Short-rate volatility.</param>
        /// <param name="instantaneousForward">The curve's instantaneous forward <c>f(0, t)</c>.</param>
        /// <param name="t">Time in years.</param>
        [Kernel]
        public static double Phi(double a, double sigma, double instantaneousForward, double t)
        {
            double b = B(a, t);
            return instantaneousForward + 0.5 * sigma * sigma * b * b;
        }

        /// <summary>
        /// <c>ln P(t, T)</c> given the state <c>x(t)</c>; see the class remarks. At <c>t = 0</c>, <c>x = 0</c> it is
        /// <c>ln P(0, T)</c> exactly.
        /// </summary>
        /// <param name="a">Mean reversion.</param>
        /// <param name="sigma">Short-rate volatility.</param>
        /// <param name="logDiscountT">ln P(0, t) of the curve.</param>
        /// <param name="logDiscountMaturity">ln P(0, T) of the curve.</param>
        /// <param name="t">Observation time.</param>
        /// <param name="maturity">Bond maturity T &gt;= t.</param>
        /// <param name="x">State x(t) = r(t) - phi(t).</param>
        [Kernel]
        public static double LogZeroBond(double a, double sigma, double logDiscountT, double logDiscountMaturity,
            double t, double maturity, double x)
        {
            double b = B(a, maturity - t);
            double b0 = B(a, t);
            double convexity = b * b * StateVariance(a, sigma, t) + sigma * sigma * b * b0 * b0;
            return logDiscountMaturity - logDiscountT - b * x - 0.5 * convexity;
        }

        /// <summary>
        /// Total standard deviation of <c>ln P(T, S)</c> seen from 0: <c>sigma_P = B(T, S) sqrt(V_x(T))</c>, i.e.
        /// <c>sigma / a (1 - exp(-a (S - T))) sqrt((1 - exp(-2 a T)) / (2 a))</c>.
        /// </summary>
        [Kernel]
        public static double ZeroBondOptionStdDev(double a, double sigma, double expiry, double maturity)
        {
            return B(a, maturity - expiry) * Math.Sqrt(StateVariance(a, sigma, expiry));
        }

        /// <summary>
        /// European option expiring at <c>T</c> on the zero-coupon bond maturing at <c>S &gt;= T</c>:
        /// <c>P(0, T) Black76(P(0, S) / P(0, T), K, sigma_P)</c>.
        /// </summary>
        /// <param name="sign">+1 call, -1 put.</param>
        /// <param name="a">Mean reversion.</param>
        /// <param name="sigma">Short-rate volatility.</param>
        /// <param name="discountExpiry">P(0, T).</param>
        /// <param name="discountMaturity">P(0, S).</param>
        /// <param name="expiry">T.</param>
        /// <param name="maturity">S.</param>
        /// <param name="strike">Strike on the bond price.</param>
        [Kernel]
        public static double ZeroBondOption(int sign, double a, double sigma, double discountExpiry, double discountMaturity,
            double expiry, double maturity, double strike)
        {
            double stdDev = ZeroBondOptionStdDev(a, sigma, expiry, maturity);
            return discountExpiry * Black76Kernels.NormalisedPrice(sign, discountMaturity / discountExpiry, strike, stdDev);
        }

        /// <summary>
        /// Caplet (<paramref name="sign"/> = +1) or floorlet (-1) of unit notional on the simple rate
        /// <c>L = (P(start) / P(end) - 1) / accrual</c> fixed at <c>start</c> and paying <c>accrual (L - K)^+</c> (or
        /// <c>(K - L)^+</c>) at <c>end</c>: <c>(1 + accrual K)</c> bond puts (calls) of strike <c>1 / (1 + accrual K)</c>.
        /// </summary>
        [Kernel]
        public static double Caplet(int sign, double a, double sigma, double discountStart, double discountEnd,
            double start, double end, double accrual, double strike)
        {
            double growth = 1.0 + accrual * strike;
            return growth * ZeroBondOption(-sign, a, sigma, discountStart, discountEnd, start, end, 1.0 / growth);
        }

        /// <summary>
        /// Cash flow <paramref name="i"/> of a fixed leg: <c>rate * coupons[i]</c>, plus <paramref name="redemption"/> on the last
        /// one. A bond with arbitrary cash flows is <c>rate = 1</c>, <c>coupons = cash flows</c>, <c>redemption = 0</c>; the fixed
        /// leg of a swap is <c>coupons = accruals</c>, <c>rate = fixed rate</c>, <c>redemption = 1</c>.
        /// </summary>
        [Kernel]
        public static double CashFlow(int i, int count, double[] coupons, double rate, double redemption)
        {
            double c = rate * coupons[i];
            if (i == count - 1) c += redemption;
            return c;
        }

        /// <summary>
        /// Value at <paramref name="expiry"/> of the cash flows paid at <c>paymentTimes[i] &gt;= expiry</c>, given the state
        /// <paramref name="x"/> at expiry: <c>sum_i c_i P(expiry, T_i; x)</c>.
        /// </summary>
        [Kernel]
        public static double CouponBondValue(double a, double sigma, double expiry, double logDiscountExpiry, int count,
            double[] paymentTimes, double[] discountPayments, double[] coupons, double rate, double redemption, double x)
        {
            double sum = 0.0;
            for (int i = 0; i < count; ++i)
            {
                double logP = LogZeroBond(a, sigma, logDiscountExpiry, Math.Log(discountPayments[i]), expiry, paymentTimes[i], x);
                sum += CashFlow(i, count, coupons, rate, redemption) * Math.Exp(logP);
            }
            return sum;
        }

        /// <summary>
        /// Jamshidian's critical state: the <c>x*</c> at which the cash flows are worth <paramref name="strike"/> at expiry,
        /// <c>sum_i c_i P(expiry, T_i; x*) = K</c>. The bond value is strictly decreasing in <c>x</c> when every cash flow is
        /// positive; the root is bracketed by doubling from [-0.05, 0.05] and refined by Brent's method (Brent 1973, ch. 4) to
        /// an absolute tolerance of 1e-15. Returns NaN when no bracket is found (e.g. no positive cash flow, or
        /// <c>K &lt;= 0</c>).
        /// </summary>
        [Kernel]
        public static double CriticalState(double a, double sigma, double expiry, double logDiscountExpiry, int count,
            double[] paymentTimes, double[] discountPayments, double[] coupons, double rate, double redemption, double strike)
        {
            double lo = -0.05, hi = 0.05;
            double flo = CouponBondValue(a, sigma, expiry, logDiscountExpiry, count, paymentTimes, discountPayments, coupons, rate, redemption, lo) - strike;
            double fhi = CouponBondValue(a, sigma, expiry, logDiscountExpiry, count, paymentTimes, discountPayments, coupons, rate, redemption, hi) - strike;
            int expansions = 0;
            while (!(flo >= 0.0) && expansions < 60)
            {
                hi = lo;
                fhi = flo;
                lo *= 2.0;
                flo = CouponBondValue(a, sigma, expiry, logDiscountExpiry, count, paymentTimes, discountPayments, coupons, rate, redemption, lo) - strike;
                ++expansions;
            }
            while (!(fhi <= 0.0) && expansions < 120)
            {
                lo = hi;
                flo = fhi;
                hi *= 2.0;
                fhi = CouponBondValue(a, sigma, expiry, logDiscountExpiry, count, paymentTimes, discountPayments, coupons, rate, redemption, hi) - strike;
                ++expansions;
            }
            if (!(flo >= 0.0) || !(fhi <= 0.0))
                return double.NaN;
            if (flo == 0.0) return lo;
            if (fhi == 0.0) return hi;

            // Brent's method on [lo, hi] (same algorithm as InFine.QuantFinance.Market.Solvers.Brent).
            const double eps = 2.220446049250313e-16;
            const double tolerance = 1e-15;
            double ba = lo, fa = flo, bb = hi, fb = fhi;
            double c = bb, fc = fb, d = bb - ba, e = d;
            for (int iteration = 0; iteration < 200; ++iteration)
            {
                if ((fb > 0.0 && fc > 0.0) || (fb < 0.0 && fc < 0.0))
                {
                    c = ba;
                    fc = fa;
                    d = bb - ba;
                    e = d;
                }
                if (Math.Abs(fc) < Math.Abs(fb))
                {
                    ba = bb; bb = c; c = ba;
                    fa = fb; fb = fc; fc = fa;
                }
                double tol1 = 2.0 * eps * Math.Abs(bb) + 0.5 * tolerance;
                double xm = 0.5 * (c - bb);
                if (Math.Abs(xm) <= tol1 || fb == 0.0)
                    return bb;
                if (Math.Abs(e) >= tol1 && Math.Abs(fa) > Math.Abs(fb))
                {
                    double s = fb / fa, p, q;
                    if (ba == c)
                    {
                        p = 2.0 * xm * s;
                        q = 1.0 - s;
                    }
                    else
                    {
                        double qq = fa / fc, r = fb / fc;
                        p = s * (2.0 * xm * qq * (qq - r) - (bb - ba) * (r - 1.0));
                        q = (qq - 1.0) * (r - 1.0) * (s - 1.0);
                    }
                    if (p > 0.0) q = -q;
                    else p = -p;
                    double min1 = 3.0 * xm * q - Math.Abs(tol1 * q);
                    double min2 = Math.Abs(e * q);
                    if (2.0 * p < (min1 < min2 ? min1 : min2))
                    {
                        e = d;
                        d = p / q;
                    }
                    else
                    {
                        d = xm;
                        e = d;
                    }
                }
                else
                {
                    d = xm;
                    e = d;
                }
                ba = bb;
                fa = fb;
                if (Math.Abs(d) > tol1) bb += d;
                else bb += xm > 0.0 ? tol1 : -tol1;
                fb = CouponBondValue(a, sigma, expiry, logDiscountExpiry, count, paymentTimes, discountPayments, coupons, rate, redemption, bb) - strike;
            }
            return bb;
        }

        /// <summary>
        /// European option expiring at <paramref name="expiry"/> (sign +1 call, -1 put) of strike <paramref name="strike"/> on
        /// the cash flows <c>c_i</c> paid at <c>paymentTimes[i] &gt;= expiry</c> (see <see cref="CashFlow"/>), by Jamshidian's
        /// decomposition: <c>sum_i c_i ZBO(expiry, T_i, X_i)</c> with <c>X_i = P(expiry, T_i; x*)</c> and <c>x*</c> from
        /// <see cref="CriticalState"/>. Exact in the model. NaN when the critical state does not exist.
        /// </summary>
        [Kernel]
        public static double CouponBondOption(int sign, double a, double sigma, double expiry, double discountExpiry, int count,
            double[] paymentTimes, double[] discountPayments, double[] coupons, double rate, double redemption, double strike)
        {
            double logDiscountExpiry = Math.Log(discountExpiry);
            double xStar = CriticalState(a, sigma, expiry, logDiscountExpiry, count, paymentTimes, discountPayments, coupons, rate, redemption, strike);
            if (double.IsNaN(xStar)) return double.NaN;
            double sum = 0.0;
            for (int i = 0; i < count; ++i)
            {
                double strikeI = Math.Exp(LogZeroBond(a, sigma, logDiscountExpiry, Math.Log(discountPayments[i]), expiry, paymentTimes[i], xStar));
                sum += CashFlow(i, count, coupons, rate, redemption)
                    * ZeroBondOption(sign, a, sigma, discountExpiry, discountPayments[i], expiry, paymentTimes[i], strikeI);
            }
            return sum;
        }

        /// <summary>
        /// European swaption of unit notional on a swap starting at <paramref name="expiry"/>: payer (<paramref name="sign"/>
        /// = +1, the right to pay <paramref name="fixedRate"/>) or receiver (-1). The floating leg is worth
        /// <c>1 - P(expiry, T_n)</c> at expiry, so a payer swaption is a put of strike 1 on the bond paying
        /// <c>fixedRate * accruals[i]</c> at <c>T_i</c> plus 1 at <c>T_n</c>, and a receiver swaption the call.
        /// </summary>
        [Kernel]
        public static double Swaption(int sign, double a, double sigma, double expiry, double discountExpiry, int count,
            double[] paymentTimes, double[] discountPayments, double[] accruals, double fixedRate)
        {
            return CouponBondOption(-sign, a, sigma, expiry, discountExpiry, count, paymentTimes, discountPayments, accruals, fixedRate, 1.0, 1.0);
        }
    }
}
