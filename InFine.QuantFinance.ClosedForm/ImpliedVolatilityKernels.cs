// Port of the reference implementation of "Let's Be Rational" (www.jaeckel.org/LetsBeRational.7z, version of
// 2024-02-15). The original notice, which the licence requires to be preserved, follows.
//
// ======================================================================================
// Copyright © 2013-2024 Peter Jäckel.
//
// Permission to use, copy, modify, and distribute this software is freely granted,
// provided that this notice is preserved.
//
// WARRANTY DISCLAIMER
// The Software is provided "as is" without warranty of any kind, either express or implied,
// including without limitation any implied warranties of condition, uninterrupted use,
// merchantability, fitness for a particular purpose, or non-infringement.
// ======================================================================================

using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Kernel-compatible Black implied volatility by Peter Jäckel's "Let's Be Rational" (Wilmott, January 2015,
    /// pp. 40-53), ported from his reference implementation: no allocation, no exceptions, a fixed number of
    /// iterations, only <c>Math.*</c> and <see cref="NormalDistribution"/> calls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The price is normalised to <c>beta = B / sqrt(F K)</c>, a function of <c>x = ln(F/K)</c> and of the total
    /// standard deviation <c>s = sigma sqrt(T)</c> only. An in-the-money option is reduced to its out-of-the-money
    /// counterpart by subtracting the intrinsic value (put-call parity), so the solver always works on
    /// <c>theta x &lt;= 0</c>, where <c>0 &lt; beta &lt; exp(theta x / 2)</c>. The range of beta is split in four
    /// branches at <c>b_l &lt; b_c &lt; b_u</c>; on each one a rational cubic interpolation of a transformed
    /// objective gives an initial guess, and exactly two Householder(3) steps (Householder(4) beyond
    /// <c>|x| &gt; 190</c> in the lowest and <c>|x| &gt; 580</c> in the highest branch) on an objective chosen to be
    /// nearly linear in that branch (<c>1/ln(b)</c> at the bottom, <c>b</c> in the middle, <c>ln(b_max - b)</c> at
    /// the top) reach the attainable precision. The normalised Black function itself is evaluated with an
    /// asymptotic expansion, a small-<c>s</c> expansion, or Cody's <c>erfc</c>/<c>erfcx</c>, so that it keeps full
    /// relative accuracy in the far wings where the two-term Black formula cancels.
    /// </para>
    /// <para>
    /// Attainable accuracy: if the price carries a relative error <c>eps</c>, the implied <c>s</c> can only be known
    /// to <c>(1 + |b / (s db/ds)|) eps</c>. This is <c>~eps</c> for out-of-the-money options and for small prices,
    /// and degrades for deep in-the-money options, whose time value is the price minus the intrinsic value:
    /// when the time value is below about <c>1e-15</c> times the intrinsic value nothing is left to invert.
    /// Prices below about <c>1e-300</c> (normalised) lose relative precision to subnormal arithmetic.
    /// </para>
    /// <para>
    /// Deviations from the reference code: the inverse normal CDF is <see cref="NormalDistribution.InverseCdf"/>
    /// (Wichura's AS241, which Jäckel reports equally accurate to his own), the exactly-at-the-money case uses
    /// <see cref="NormalDistribution.InverseCdfCentred"/> for <c>2 Phi^-1(1/2 + beta/2)</c>, cube roots are taken
    /// with <c>Math.Pow</c>, the static threshold table of the asymptotic expansion is an explicit comparison
    /// chain, and out-of-range prices give NaN / +Infinity instead of <c>-DBL_MAX</c> / <c>DBL_MAX</c>.
    /// </para>
    /// <para>
    /// Return conventions (all kernels): NaN for a NaN or non-positive forward, strike, discount factor or maturity,
    /// a negative or NaN price, or a price below the intrinsic value; 0 for a price exactly equal to the intrinsic
    /// value; +Infinity for a price at or above the upper bound (the discounted forward for a call, the discounted
    /// strike for a put).
    /// </para>
    /// </remarks>
    // Not static so that a HybRunner can wrap an instance to reach the [EntryPoint] array kernels
    // (runner.Wrap(new ImpliedVolatilityKernels()).<EntryPoint>(...), as for FullRevaluationKernels); it has no state.
    public sealed class ImpliedVolatilityKernels
    {
        const double DblEpsilon = 2.220446049250313e-16;
        const double SqrtDblMax = 1.3407807929942596e154;
        const double Eta = -13.0;                       // region boundary in h = theta x / s
        const double Tau = 0.21022410381342863;         // 2 DBL_EPSILON^(1/16)
        const double SqrtTwo = 1.4142135623730950488;
        const double InvSqrtTwo = 0.70710678118654752440;
        const double SqrtTwoPi = 2.5066282746310005024;
        const double InvSqrtTwoPi = 0.39894228040143267794;
        const double HalfLnTwoPi = 0.91893853320467274178;
        const double TwoPi = 6.283185307179586476925;
        const double SqrtPiOverTwo = 1.253314137315500251208;
        const double SqrtThree = 1.732050807568877293527;
        const double SqrtOneOverThree = 0.577350269189625764509;
        const double TwoPiOverSqrtTwentySeven = 1.209199576156145233729; // 2 pi / sqrt(27)
        const double SqrtThreeOverCbrtTwoPi = 0.938643487427383566075;   // sqrt(3) / cbrt(2 pi)
        const double PiOverSix = 0.523598775598298873077;
        const double MinimumRationalCubicControl = -(1.0 - 1.4901161193847656e-08); // -(1 - sqrt(DBL_EPSILON))
        const double MaximumRationalCubicControl = 2.0 / (DblEpsilon * DblEpsilon);
        const double DblMin = 2.2250738585072014e-308;
        const int MaximumIterations = 2;

        // ---- public entry points -----------------------------------------------------------------------------

        /// <summary>
        /// Black-76 implied volatility from a discounted option price.
        /// </summary>
        /// <param name="sign">+1 for a call, -1 for a put.</param>
        /// <param name="price">discounted option price.</param>
        /// <param name="forward">forward of the underlying for the expiry, positive.</param>
        /// <param name="strike">strike, positive.</param>
        /// <param name="discountFactor">discount factor from the payment date to the valuation date, positive.</param>
        /// <param name="maturity">time to expiry in years, positive.</param>
        /// <returns>sigma; NaN, 0 or +Infinity as described on the class.</returns>
        [Kernel]
        public static double Black76(int sign, double price, double forward, double strike, double discountFactor,
            double maturity)
        {
            if (!(maturity > 0.0 && maturity < double.PositiveInfinity)) return double.NaN;
            return StandardDeviation(sign, price, forward, strike, discountFactor) / Math.Sqrt(maturity);
        }

        /// <summary>
        /// Generalised Black-Scholes-Merton implied volatility (continuous dividend yield): the Black-76 implied
        /// volatility on the forward <c>S exp((r - q) T)</c> discounted at <c>exp(-r T)</c>.
        /// </summary>
        [Kernel]
        public static double BlackScholes(int sign, double price, double spot, double strike, double maturity,
            double rate, double dividendYield)
        {
            return Black76(sign, price, spot * Math.Exp((rate - dividendYield) * maturity), strike,
                Math.Exp(-rate * maturity), maturity);
        }

        /// <summary>
        /// Implied total standard deviation <c>s = sigma sqrt(T)</c> from a discounted Black price.
        /// </summary>
        [Kernel]
        public static double StandardDeviation(int sign, double price, double forward, double strike,
            double discountFactor)
        {
            if (!(forward > 0.0 && forward < double.PositiveInfinity
                && strike > 0.0 && strike < double.PositiveInfinity
                && discountFactor > 0.0 && discountFactor < double.PositiveInfinity
                && price >= 0.0 && (sign == 1 || sign == -1)))
                return double.NaN;
            double undiscounted = price / discountFactor;
            if (undiscounted >= (sign < 0 ? strike : forward)) return double.PositiveInfinity;
            double intrinsic = sign < 0 ? strike - forward : forward - strike;
            double timeValue = intrinsic > 0.0 ? undiscounted - intrinsic : undiscounted;
            return LetsBeRational(timeValue / (Math.Sqrt(forward) * Math.Sqrt(strike)), -Math.Abs(Math.Log(forward / strike)));
        }

        /// <summary>
        /// Implied total standard deviation from a normalised price <c>beta = B / sqrt(F K)</c> (undiscounted price
        /// over the geometric mean of forward and strike), with <c>x = ln(F/K)</c>.
        /// </summary>
        [Kernel]
        public static double NormalisedStandardDeviation(int sign, double beta, double x)
        {
            if (double.IsNaN(x) || double.IsInfinity(x) || !(sign == 1 || sign == -1)) return double.NaN;
            return LetsBeRational(beta - NormalisedIntrinsic(sign * x), -Math.Abs(x));
        }

        /// <summary>
        /// Normalised Black function <c>beta(x, s) = B / sqrt(F K)</c> with <c>x = ln(F/K)</c> and
        /// <c>s = sigma sqrt(T)</c>, accurate to a few ulps in relative terms everywhere, including the far wings.
        /// <c>s &lt;= 0</c> gives the normalised intrinsic value.
        /// </summary>
        [Kernel]
        public static double NormalisedBlack(int sign, double x, double s)
        {
            if (x == 0.0) return s > 0.0 ? NormalDistribution.Erf((0.5 * InvSqrtTwo) * s) : (double.IsNaN(s) ? s : 0.0);
            double thetaX = sign < 0 ? -x : x;
            if (double.IsNaN(s)) return s;
            return NormalisedIntrinsic(thetaX) + (s > 0.0 ? NormalisedBlackOtm(-Math.Abs(x), s) : 0.0);
        }

        /// <summary>
        /// Undiscounted Black price <c>sqrt(F K) beta(ln(F/K), s)</c> computed with <see cref="NormalisedBlack"/>: the
        /// in-the-money part is the intrinsic value plus an out-of-the-money time value, so the wings keep their
        /// relative accuracy. Used as the accurate pricer that the implied volatility inverts.
        /// </summary>
        [Kernel]
        public static double UndiscountedBlack(int sign, double forward, double strike, double stdDev)
        {
            if (forward == strike) return stdDev > 0.0 ? forward * NormalDistribution.Erf((0.5 * InvSqrtTwo) * stdDev) : (double.IsNaN(stdDev) ? stdDev : 0.0);
            double intrinsic = Math.Max(sign < 0 ? strike - forward : forward - strike, 0.0);
            if (double.IsNaN(stdDev)) return stdDev;
            return intrinsic + (stdDev > 0.0
                ? (Math.Sqrt(forward) * Math.Sqrt(strike)) * NormalisedBlackOtm(-Math.Abs(Math.Log(forward / strike)), stdDev)
                : 0.0);
        }

        /// <summary>
        /// Evaluates one option of a batch: writes the Black-76 implied volatility of element
        /// <paramref name="index"/> to <paramref name="volatility"/>.
        /// </summary>
        [Kernel]
        public static void Black76At(int index, double[] volatility, int[] sign, double[] price, double[] forward,
            double[] strike, double[] discountFactor, double[] maturity)
        {
            volatility[index] = Black76(sign[index], price[index], forward[index], strike[index],
                discountFactor[index], maturity[index]);
        }

        /// <summary>
        /// Evaluates a batch of Black-76 implied volatilities, one option per array element.
        /// </summary>
        [EntryPoint("ImpliedVolatilityKernels_Black76_Array")]
        public static void Black76Array(int count, double[] volatility, int[] sign, double[] price, double[] forward,
            double[] strike, double[] discountFactor, double[] maturity)
        {
            for (int k = threadIdx.x + blockDim.x * blockIdx.x; k < count; k += blockDim.x * gridDim.x)
            {
                Black76At(k, volatility, sign, price, forward, strike, discountFactor, maturity);
            }
        }

        // ---- the solver ----------------------------------------------------------------------------------------

        /// <summary>
        /// Implied s for an out-of-the-money normalised price beta with thetaX = theta x &lt;= 0 (reference function
        /// <c>lets_be_rational</c>).
        /// </summary>
        static double LetsBeRational(double beta, double thetaX)
        {
            if (!(beta >= 0.0)) return double.NaN; // below the intrinsic value, or NaN
            if (beta == 0.0) return 0.0;
            double bMax = Math.Exp(0.5 * thetaX);
            if (beta >= bMax) return double.PositiveInfinity;
            // Exactly at the money: b(0, s) = erf(s / sqrt(8)) = 2 Phi(s/2) - 1.
            if (thetaX == 0.0) return 2.0 * NormalDistribution.InverseCdfCentred(0.5 * beta);

            int iterations = 0;
            double f = -double.MaxValue, s = -double.MaxValue, ds = -double.MaxValue;
            // s_c = sqrt(2|x|) is the inflexion point of b(s); b_c = b(x, s_c) = exp(-|x|/2) (1 - erfcx(sqrt|x|)) / 2.
            double sqrtAx = Math.Sqrt(-thetaX), sC = SqrtTwo * sqrtAx, ome = OneMinusErfcx(sqrtAx), bC = 0.5 * bMax * ome;
            if (beta < bC)
            {
                // Lower half, s < s_c. s_l = s_c - b_c / b'(s_c).
                double sL = sC - SqrtPiOverTwo * ome;
                double bL = BlOverBMax(sC) * bMax;
                if (beta < bL)
                {
                    // Lowest branch: interpolate the lower map f(b), then iterate on g(s) = 1/ln(b(s)) - 1/ln(beta).
                    double fL, dfL, d2fL;
                    LowerMap(thetaX, sL, out fL, out dfL, out d2fL);
                    double rLL = ConvexControlToFitSecondDerivativeAtRight(0.0, bL, 0.0, fL, 1.0, dfL, d2fL, true);
                    f = RationalCubic(beta, 0.0, bL, 0.0, fL, 1.0, dfL, rLL);
                    if (!(f > 0.0))
                    {
                        // Roundoff for extreme |x|: quadratic through f(0) = 0, f'(0) = 1 and f(b_l).
                        double tq = beta / bL;
                        f = (fL * tq + bL * (1.0 - tq)) * tq;
                    }
                    s = InverseLowerMap(thetaX, f);
                    double lnBeta = Math.Log(beta);
                    for (; iterations < MaximumIterations && Math.Abs(ds) > DblEpsilon * s; ++iterations)
                    {
                        double bx, lnVega;
                        ScaledNormalisedBlackAndLnVega(thetaX, s, out bx, out lnVega);
                        double lnB = Math.Log(bx) + lnVega, bpob = 1.0 / bx;
                        double h = thetaX / s, x2OverS3 = h * h / s, bH2 = x2OverS3 - s / 4.0;
                        double nu = (lnBeta - lnB) * lnB / lnBeta / bpob, lambda = 1.0 / lnB, otl = 1.0 + 2.0 * lambda;
                        double h2 = bH2 - bpob * otl, c = 3.0 * (x2OverS3 / s);
                        double bH3 = bH2 * bH2 - c - 0.25, sqBpob = bpob * bpob, bppob = bH2 * bpob, mu = 6.0 * lambda * (1.0 + lambda);
                        double h3 = bH3 + sqBpob * (2.0 + mu) - bppob * 3.0 * otl;
                        if (thetaX < -190.0)
                        {
                            double h4 = (bH2 * (bH3 - 0.5) - (bH2 - 2.0 / s) * 2.0 * c)
                                - bpob * (sqBpob * (6.0 + lambda * (22.0 + lambda * (36.0 + lambda * 24.0))) - bppob * (12.0 + 6.0 * mu))
                                - bppob * bH2 * 3.0 * otl - bH3 * bpob * 4.0 * otl;
                            ds = nu * Householder4Factor(nu, h2, h3, h4);
                        }
                        else
                        {
                            ds = nu * Householder3Factor(nu, h2, h3);
                        }
                        s += ds;
                    }
                    return s;
                }
                // Lower middle branch, s_l <= s < s_c: rational cubic interpolation of s(b).
                double invVC = SqrtTwoPi / bMax;
                double invVL = InverseNormalisedVega(thetaX, sL);
                double rLM = ConvexControlToFitSecondDerivativeAtRight(bL, bC, sL, sC, invVL, invVC, 0.0, false);
                s = RationalCubic(beta, bL, bC, sL, sC, invVL, invVC, rLM);
            }
            else
            {
                // Upper half, s >= s_c. s_u = s_c + (b_max - b_c) / b'(s_c).
                double sU = sC + SqrtPiOverTwo * (2.0 - ome);
                double bU = BuOverBMax(sC) * bMax;
                if (beta <= bU)
                {
                    // Upper middle branch, s_c <= s <= s_u.
                    double invVC = SqrtTwoPi / bMax;
                    double invVU = InverseNormalisedVega(thetaX, sU);
                    double rUM = ConvexControlToFitSecondDerivativeAtLeft(bC, bU, sC, sU, invVC, invVU, 0.0, false);
                    s = RationalCubic(beta, bC, bU, sC, sU, invVC, invVU, rUM);
                }
                else
                {
                    // Highest branch: interpolate the upper map, then iterate on g(s) = ln(b_max - beta) - ln(b_max - b(s)).
                    double fU, dfU, d2fU;
                    UpperMap(thetaX, sU, out fU, out dfU, out d2fU);
                    if (d2fU > -SqrtDblMax && d2fU < SqrtDblMax)
                    {
                        double rUU = ConvexControlToFitSecondDerivativeAtLeft(bU, bMax, fU, 0.0, dfU, -0.5, d2fU, true);
                        f = RationalCubic(beta, bU, bMax, fU, 0.0, dfU, -0.5, rUU);
                    }
                    if (f <= 0.0)
                    {
                        // Quadratic through f(b_u), f(b_max) = 0 and f'(b_max) = -1/2.
                        double hq = bMax - bU, tq = (beta - bU) / hq;
                        f = (fU * (1.0 - tq) + 0.5 * hq * tq) * (1.0 - tq);
                    }
                    s = InverseUpperMap(f);
                    if (beta > 0.5 * bMax)
                    {
                        double betaBar = bMax - beta;
                        for (; iterations < MaximumIterations && Math.Abs(ds) > DblEpsilon * s; ++iterations)
                        {
                            // b_max - b(s) evaluated without cancellation: sqrt(pi/2) (erfcx((t+h)/sqrt2) + erfcx((t-h)/sqrt2)) b'(s).
                            double h = thetaX / s, t = s / 2.0;
                            double gp = (2.0 / SqrtTwoPi) / (NormalDistribution.Erfcx((t + h) * InvSqrtTwo) + NormalDistribution.Erfcx((t - h) * InvSqrtTwo));
                            double bBar = NormalisedVega(thetaX, s) / gp;
                            double g = Math.Log(betaBar / bBar), x2OverS3 = (h * h) / s, bH2 = x2OverS3 - s / 4.0;
                            double c = 3.0 * (x2OverS3 / s), bH3 = bH2 * bH2 - c - 0.25;
                            double nu = -g / gp, h2 = bH2 + gp, h3 = bH3 + gp * (2.0 * gp + 3.0 * bH2);
                            if (thetaX < -580.0)
                            {
                                double h4 = (bH2 * (bH3 - 0.5) - (bH2 - 2.0 / s) * 2.0 * c)
                                    + gp * (6.0 * gp * (gp + 2.0 * bH2) + 3.0 * bH2 * bH2 + 4.0 * bH3);
                                ds = nu * Householder4Factor(nu, h2, h3, h4);
                            }
                            else
                            {
                                ds = nu * Householder3Factor(nu, h2, h3);
                            }
                            s += ds;
                        }
                        return s;
                    }
                }
            }
            // Middle branches: iterate on g(s) = b(s) - beta.
            for (; iterations < MaximumIterations && Math.Abs(ds) > DblEpsilon * s; ++iterations)
            {
                double b = NormalisedBlackOtm(thetaX, s), invBp = InverseNormalisedVega(thetaX, s);
                double nu = (beta - b) * invBp, h = thetaX / s, x2OverS3 = (h * h) / s;
                double h2 = x2OverS3 - s * 0.25, h3 = h2 * h2 - 3.0 * (x2OverS3 / s) - 0.25;
                ds = nu * Householder3Factor(nu, h2, h3);
                s += ds;
            }
            return s;
        }

        static double Householder3Factor(double nu, double h2, double h3)
        {
            return (1.0 + 0.5 * h2 * nu) / (1.0 + nu * (h2 + h3 * nu * (1.0 / 6.0)));
        }

        static double Householder4Factor(double nu, double h2, double h3, double h4)
        {
            return (1.0 + nu * (h2 + nu * h3 * (1.0 / 6.0)))
                / (1.0 + nu * (1.5 * h2 + nu * (h2 * h2 * 0.25 + h3 * (1.0 / 3.0) + nu * h4 * (1.0 / 24.0))));
        }

        // ---- normalised Black function ------------------------------------------------------------------------

        /// <summary>2 sinh(thetaX / 2) for thetaX &gt; 0, else 0: the normalised intrinsic value.</summary>
        static double NormalisedIntrinsic(double thetaX)
        {
            return thetaX <= 0.0 ? 0.0 : 2.0 * Math.Sinh(0.5 * thetaX);
        }

        // Region I (asymptotic expansion): h < eta and t < (tau + 1/2) + (|h| - |eta|), with h = theta x / s, t = s / 2.
        static bool IsRegionI(double thetaX, double s)
        {
            return thetaX < s * Eta && s * (0.5 * s - (Tau + 0.5 + Eta)) + thetaX < 0.0;
        }

        // Region II (small-t expansion): t < tau + (1/2 / |eta|) |h|.
        static bool IsRegionII(double thetaX, double s)
        {
            return s * (s - 2.0 * Tau) - thetaX / Eta < 0.0;
        }

        /// <summary>b(x, s) for thetaX &lt; 0 and s &gt; 0.</summary>
        static double NormalisedBlackOtm(double thetaX, double s)
        {
            if (IsRegionI(thetaX, s))
                return AsymptoticExpansionOfScaledNormalisedBlack(thetaX / s, 0.5 * s) * NormalisedVega(thetaX, s);
            if (IsRegionII(thetaX, s))
                return SmallTExpansionOfScaledNormalisedBlack(thetaX / s, 0.5 * s) * NormalisedVega(thetaX, s);
            return NormalisedBlackWithOptimalUseOfCodysFunctions(thetaX, s);
        }

        /// <summary>b / b'(s) and ln b'(s), for thetaX &lt; 0 and s &gt; 0 (no underflow of b for tiny prices).</summary>
        static void ScaledNormalisedBlackAndLnVega(double thetaX, double s, out double bx, out double lnVega)
        {
            lnVega = LnNormalisedVega(thetaX, s);
            if (IsRegionI(thetaX, s))
                bx = AsymptoticExpansionOfScaledNormalisedBlack(thetaX / s, 0.5 * s);
            else if (IsRegionII(thetaX, s))
                bx = SmallTExpansionOfScaledNormalisedBlack(thetaX / s, 0.5 * s);
            else
                bx = NormalisedBlackWithOptimalUseOfCodysFunctions(thetaX, s) * Math.Exp(-lnVega);
        }

        /// <summary>db/ds = exp(-(h^2 + t^2) / 2) / sqrt(2 pi).</summary>
        static double NormalisedVega(double x, double s)
        {
            double h = x / s, t = 0.5 * s;
            return InvSqrtTwoPi * Math.Exp(-0.5 * (h * h + t * t));
        }

        static double InverseNormalisedVega(double x, double s)
        {
            double h = x / s, t = 0.5 * s;
            return SqrtTwoPi * Math.Exp(0.5 * (h * h + t * t));
        }

        static double LnNormalisedVega(double x, double s)
        {
            double h = x / s, t = 0.5 * s;
            return -HalfLnTwoPi - 0.5 * (h * h + t * t);
        }

        /// <summary>
        /// Asymptotic expansion (Abramowitz and Stegun 26.2.12) of the scaled normalised Black function
        /// <c>b / b' = Y(h+t) - Y(h-t)</c>, <c>Y(z) = Phi(z)/phi(z)</c>, in <c>q = (h/r)^2</c> with <c>r = (h+t)(h-t)</c>,
        /// <c>e = (t/h)^2</c>; the number of terms grows towards the boundary of region I.
        /// </summary>
        static double AsymptoticExpansionOfScaledNormalisedBlack(double h, double t)
        {
            double th = t / h, e = th * th, r = (h + t) * (h - t), hr = h / r, q = hr * hr;
            // Number of thresholds (12.347, 12.958, 13.729, 14.718, 16.016, 17.769, 20.221, 23.816, 29.419, 38.93,
            // 57.171, 99.347) not above v; the reference code's switch with fall-through adds terms A16 down to A5.
            double v = -h - t + Tau + 0.5;
            int k = v < 12.347 ? 0 : v < 12.958 ? 1 : v < 13.729 ? 2 : v < 14.718 ? 3 : v < 16.016 ? 4 : v < 17.769 ? 5
                  : v < 20.221 ? 6 : v < 23.816 ? 7 : v < 29.419 ? 8 : v < 38.93 ? 9 : v < 57.171 ? 10 : v < 99.347 ? 11 : 12;
            double omega = 0.0;
            if (k <= 0) omega = q * (A16(e) + omega);
            if (k <= 1) omega = q * (A15(e) + omega);
            if (k <= 2) omega = q * (A14(e) + omega);
            if (k <= 3) omega = q * (A13(e) + omega);
            if (k <= 4) omega = q * (A12(e) + omega);
            if (k <= 5) omega = q * (A11(e) + omega);
            if (k <= 6) omega = q * (A10(e) + omega);
            if (k <= 7) omega = q * (A9(e) + omega);
            if (k <= 8) omega = q * (A8(e) + omega);
            if (k <= 9) omega = q * (A7(e) + omega);
            if (k <= 10) omega = q * (A6(e) + omega);
            if (k <= 11) omega = q * (A5(e) + omega);
            omega = A0(e) + q * (A1(e) + q * (A2(e) + q * (A3(e) + q * (A4(e) + omega))));
            return (t / r) * omega;
        }

        static double A0(double e) { return 2; }
        static double A1(double e) { return -6-2*e; }
        static double A2(double e) { return 30+e*(60+6*e); }
        static double A3(double e) { return -2.1E2+e*(-1.05E3+e*(-6.3E2-30*e)); }
        static double A4(double e) { return 1.89E3+e*(1.764E4+e*(2.646E4+e*(7.56E3+2.1E2*e))); }
        static double A5(double e) { return -2.079E4+e*(-3.1185E5+e*(-8.7318E5+e*(-6.237E5+e*(-1.0395E5-1.89E3*e)))); }
        static double A6(double e) { return 2.7027E5+e*(5.94594E6+e*(2.675673E7+e*(3.567564E7+e*(1.486485E7+e*(1.62162E6+2.079E4*e))))); }
        static double A7(double e) { return -4.05405E6+e*(-1.2297285E8+e*(-8.1162081E8+e*(-1.73918745E9+e*(-1.35270135E9+e*(-3.6891855E8+e*(-2.837835E7-2.7027E5*e)))))); }
        static double A8(double e) { return 6.891885E7+e*(2.756754E9+e*(2.50864614E10+e*(7.88431644E10+e*(9.85539555E10+e*(5.01729228E10+e*(9.648639E9+e*(5.513508E8+4.05405E6*e))))))); }
        static double A9(double e) { return -1.30945815E9+e*(-6.678236565E10+e*(-8.013883878E11+e*(-3.4726830138E12+e*(-6.3665855253E12+e*(-5.2090245207E12+e*(-1.8699062382E12+e*(-2.671294626E11+e*(-1.178512335E10-6.891885E7*e)))))))); }
        static double A10(double e) { return 2.749862115E10+e*(1.7415793395E12+e*(2.664616389435E13+e*(1.52263793682E14+e*(3.848890340295E14+e*(4.618668408354E14+e*(2.664616389435E14+e*(7.10564370516E13+e*(7.83710702775E12+e*(2.749862115E11+1.30945815E9*e))))))))); }
        static double A11(double e) { return -6.3246828645E11+e*(-4.870005805665E13+e*(-9.2530110307635E14+e*(-6.74147946527055E15+e*(-2.24715982175685E16+e*(-3.71802806872497E16+e*(-3.14602375045959E16+e*(-1.34829589305411E16+e*(-2.77590330922905E15+e*(-2.4350029028325E14+e*(-6.95715115095E12-2.749862115E10*e)))))))))); }
        static double A12(double e) { return 1.581170716125E13+e*(1.454677058835E15+e*(3.36030400590885E16+e*(3.04027505296515E17+e*(1.29211689751018875E18+e*(2.81916414002223E18+e*(3.289024830025935E18+e*(2.067387036016302E18+e*(6.8406188691715875E17+e*(1.12010133530295E17+e*(8.0007238235925E15+e*(1.89740485935E14+6.3246828645E11*e))))))))))); }
        static double A13(double e) { return -4.2691609335375E14+e*(-4.624924344665625E16+e*(-1.2764791191277125E18+e*(-1.40412703104048375E19+e*(-7.41067044160255312E19+e*(-2.06151377739125569E20+e*(-3.17155965752500875E20+e*(-2.74868503652167425E20+e*(-1.33392067948845956E20+e*(-3.51031757760120938E19+e*(-4.6804234368016125E18+e*(-2.774954606799375E17+e*(-5.54990921359875E15-1.581170716125E13*e)))))))))))); }
        static double A14(double e) { return 1.238056670725875E16+e*(1.5599514051146025E18+e*(5.06984206662245812E19+e*(6.66322100184665925E20+e*(4.27556680951827302E21+e*(1.47701398874267613E22+e*(2.89721974714909549E22+e*(3.31110828245610914E22+e*(2.2155209831140142E22+e*(8.55113361903654604E21+e*(1.83238577550783129E21+e*(2.02793682664898325E20+e*(1.01396841332449162E19+e*(1.733279339016225E17+4.2691609335375E14*e))))))))))))); }
        static double A15(double e) { return -3.8379756792502125E17+e*(-5.56506473491280812E19+e*(-2.10359446979704147E21+e*(-3.25556286992399275E22+e*(-2.49593153360839444E23+e*(-1.04829124411552567E24+e*(-2.55352995361474201E24+e*(-3.72085793241005264E24+e*(-3.28310994036181115E24+e*(-1.74715207352587611E24+e*(-5.49104937393846778E23+e*(-9.76668860977197826E22+e*(-9.11557603578717971E21+e*(-3.89554531443896569E20+e*(-5.75696351887531875E18-1.238056670725875E16*e)))))))))))))); }
        static double A16(double e) { return 1.26653197415257012E19+e*(2.09399953059891594E21+e*(9.10889795810528434E22+e*(1.63960163245895118E24+e*(1.48019591819210871E25+e*(7.42789224401858187E25+e*(2.19979885688242617E26+e*(3.98058840769200926E26+e*(4.47816195865351041E26+e*(3.1425697955463231E26+e*(1.36178024473674001E26+e*(3.55247020366106089E25+e*(5.32870530549159134E24+e*(4.25081904711579936E23+e*(1.57049964794918696E22+e*(2.0264511586441122E20+3.8379756792502125E17*e))))))))))))))); }

        static double YPrimeTailExpansionRationalFunctionPart(double w)
        {
            return w * (-2.9999999999994663866 + w * (-1.7556263323542206288E2 + w * (-3.4735035445495633334E3 + w * (-2.7805745693864308643E4 + w * (-8.3836021460741980839E4 - 6.6818249032616849037E4 * w))))) / (1 + w * (6.3520877744831739102E1 + w * (1.4404389037604337538E3 + w * (1.4562545638507033944E4 + w * (6.6886794165651675684E4 + w * (1.2569970380923908488E5 + 6.9286518679803751694E4 * w))))));
        }

        /// <summary>Y'(h) = 1 + h Y(h) for h &lt;= 0, without subtractive cancellation.</summary>
        static double YPrime(double h)
        {
            if (h < -4.0)
            {
                // Minimax rational function of order (5,6) in w = 1/h^2, relative accuracy 9.8e-17 on h in [-inf, -4].
                double w = 1.0 / (h * h);
                return w * (1.0 + YPrimeTailExpansionRationalFunctionPart(w));
            }
            if (h <= -0.46875)
                // Minimax rational function of order (7,7), relative accuracy 1.6e-16 on h in [-4, -0.46875].
                return (1.0000000000594317229 - h * (6.1911449879694112749E-1 - h * (2.2180844736576013957E-1 - h * (4.5650900351352987865E-2 - h * (5.545521007735379052E-3 - h * (3.0717392274913902347E-4 - h * (4.2766597835908713583E-8 + 8.4592436406580605619E-10 * h))))))) / (1 - h * (1.8724286369589162071 - h * (1.5685497236077651429 - h * (7.6576489836589035112E-1 - h * (2.3677701403094640361E-1 - h * (4.6762548903194957675E-2 - h * (5.5290453576936595892E-3 - 3.0822020417927147113E-4 * h)))))));
            return 1.0 + h * SqrtPiOverTwo * NormalDistribution.Erfcx(-InvSqrtTwo * h);
        }

        /// <summary>
        /// Small-t expansion of <c>b / b' = Y(h+t) - Y(h-t)</c> to twelfth order in t, accurate to DBL_EPSILON for
        /// h &lt;= 0 and t &lt; tau.
        /// </summary>
        static double SmallTExpansionOfScaledNormalisedBlack(double h, double t)
        {
            double a = YPrime(h), h2 = h * h, t2 = t * t;
            return t * (B0(a, h2) + t2 * (B1(a, h2) + t2 * (B2(a, h2) + t2 * (B3(a, h2) + t2 * (B4(a, h2)
                + t2 * (B5(a, h2) + B6(a, h2) * t2))))));
        }

        static double B0(double a, double h2) { return 2*a; }
        static double B1(double a, double h2) { return (-1+a*(3+h2))/3.0; }
        static double B2(double a, double h2) { return (-7-h2+a*(15+h2*(10+h2)))/60.0; }
        static double B3(double a, double h2) { return (-57+(-18-h2)*h2+a*(105+h2*(105+h2*(21+h2))))/2520.0; }
        static double B4(double a, double h2) { return (-561+h2*(-285+(-33-h2)*h2)+a*(945+h2*(1260+h2*(378+h2*(36+h2)))))/181440.0; }
        static double B5(double a, double h2) { return (-6555+h2*(-4680+h2*(-840+(-52-h2)*h2))+a*(10395+h2*(17325+h2*(6930+h2*(990+h2*(55+h2))))))/19958400.0; }
        static double B6(double a, double h2) { return (-89055+h2*(-82845+h2*(-20370+h2*(-1926+(-75-h2)*h2)))+a*(135135+h2*(270270+h2*(135135+h2*(25740+h2*(2145+h2*(78+h2)))))))/3113510400.0; }

        /// <summary>
        /// b(x, s) = 1/2 exp(-(h^2+t^2)/2) [erfcx(q1) - erfcx(q2)] with q1,2 = -(h -+ t)/sqrt(2), each term switched to
        /// <c>erfc</c> where Cody's <c>erfcx</c> would itself call <c>exp</c>, to minimise the number of exponentials.
        /// </summary>
        static double NormalisedBlackWithOptimalUseOfCodysFunctions(double thetaX, double s)
        {
            const double codysThreshold = 0.46875;
            double h = thetaX / s, t = 0.5 * s, q1 = -InvSqrtTwo * (h + t), q2 = -InvSqrtTwo * (h - t);
            double twoB;
            if (q1 < codysThreshold)
            {
                if (q2 < codysThreshold)
                    twoB = Math.Exp(0.5 * thetaX) * NormalDistribution.Erfc(q1) - Math.Exp(-0.5 * thetaX) * NormalDistribution.Erfc(q2);
                else
                    twoB = Math.Exp(0.5 * thetaX) * NormalDistribution.Erfc(q1) - Math.Exp(-0.5 * (h * h + t * t)) * NormalDistribution.Erfcx(q2);
            }
            else
            {
                if (q2 < codysThreshold)
                    twoB = Math.Exp(-0.5 * (h * h + t * t)) * NormalDistribution.Erfcx(q1) - Math.Exp(-0.5 * thetaX) * NormalDistribution.Erfc(q2);
                else
                    twoB = Math.Exp(-0.5 * (h * h + t * t)) * (NormalDistribution.Erfcx(q1) - NormalDistribution.Erfcx(q2));
            }
            return Math.Max(0.5 * twoB, 0.0);
        }

        /// <summary>1 - erfcx(x), with a minimax rational function near 0 against cancellation.</summary>
        static double OneMinusErfcx(double x)
        {
            if (x < -1.0 / 5.0 || x > 1.0 / 3.0)
                return 1.0 - NormalDistribution.Erfcx(x);
            // Order (4,5) minimax rational function, relative accuracy 2.5e-17 on [-1/5, 1/3].
            return x * (1.128379167095512573896 - x * (1.0000000000000002 + x * (1.1514967181784756 + x * (5.7689001208873741E-1 + x * (1.4069188744609651E-1 + 1.4069285713634565E-2 * x)))) / (1 + x * (1.9037494962421563 + x * (1.5089908593742723 + x * (6.2486081658640257E-1 + x * (1.358008134514386E-1 + 1.2463320728346347E-2 * x))))));
        }

        // ---- branch boundaries b_l / b_max and b_u / b_max as functions of s_c = sqrt(2|x|) ---------------------

        static double BlOverBMax(double sc)
        {
            if (sc < 2.6267851073127395)
            {
                if (sc < 0.7099295739719539)
                {
                    double g = (8.0741072372882856924E-2 + sc * (9.8078911786358897272E-2 + sc * (3.9760631445677058375E-2 + sc * (5.9716928459589189876E-3 + sc * (-6.4036399341479799981E-6 + 4.5425102093616062245E-7 * sc))))) / (1 + sc * (1.8594977672287664353 + sc * (1.3658801475711790419 + sc * (4.6132707108655653215E-1 + 6.1254597049831720643E-2 * sc))));
                    return (sc * sc) * (0.07560996640296361767172 + sc * (sc * g - 0.09672719281339436290858));
                }
                return (1.9795737927598581235E-9 + sc * (-2.7081288564685588037E-8 + sc * (7.5610142272549044609E-2 + sc * (6.917130174466834016E-2 + sc * (2.9537058950963019803E-2 + sc * (6.5849252702302307774E-3 + 6.9711400639834715731E-4 * sc)))))) / (1 + sc * (2.1941448525586579756 + sc * (2.1297103549995181357 + sc * (1.1571483187179784072 + sc * (3.7831622253060456794E-1 + sc * (7.1714862448829349869E-2 + 6.6361975827861200167E-3 * sc))))));
            }
            if (sc < 7.348469228349534)
                return (-9.3325115354837883291E-5 + sc * (5.3118033972794648837E-4 + sc * (7.4114855448345002595E-2 + sc * (7.4039658186822817454E-2 + sc * (3.9225177407687604785E-2 + sc * (1.0022913378254090083E-2 + 1.7012579407246055469E-3 * sc)))))) / (1 + sc * (2.2217238132228132256 + sc * (2.3441816707087403282 + sc * (1.3912323646271141826 + sc * (5.3231258443501838354E-1 + sc * (1.1744005919716101572E-1 + 1.6195405895930935811E-2 * sc))))));
            return (1.4500072297240603183E-3 + sc * (-1.5116692485011195757E-3 + sc * (7.1682178310936334831E-2 + sc * (3.921610857820463493E-2 + sc * (2.9342405658628443931E-2 + sc * (5.1832526171631521426E-3 + 1.6930208078421474854E-3 * sc)))))) / (1 + sc * (1.6176313502305414664 + sc * (1.6823159175281531664 + sc * (8.4878307567372222113E-1 + sc * (3.7543742137375791321E-1 + sc * (7.126137099644302999E-2 + 1.6116992546788676159E-2 * sc))))));
        }

        static double BuOverBMax(double sc)
        {
            if (sc < 1.7888543819998317)
            {
                if (sc < 0.7745966692414833)
                {
                    double g = (-6.063099881233561706E-2 + sc * (-8.1011946637120604985E-2 + sc * (-4.2505564862438753828E-2 + sc * (-8.9880000946868691788E-3 + sc * (-7.5603072110443268356E-6 + 4.3879556621540147458E-7 * sc))))) / (1 + sc * (1.8400371530721828756 + sc * (1.5709283443886143691 + sc * (6.8913245453611400484E-1 + 1.4703173061720980923E-1 * sc))));
                    return 0.7899085945560627246288 + (sc * sc) * (0.0614616805805147403487 + sc * g);
                }
                return (7.8990944435755287611E-1 + sc * (-1.2655410534988972886 + sc * (-2.8803040699221003256 + sc * (-2.6936198689113258727 + sc * (-1.1213067281643205754 + sc * (-2.1277793801691629892E-1 + 5.1486445905299802703E-6 * sc)))))) / (1 + sc * (-1.6021222722060444448 + sc * (-3.7242680976480704555 + sc * (-3.2083117718907365085 + sc * (-1.2922333835930958583 - 2.3762328334050001161E-1 * sc)))));
            }
            if (sc < 6.164414002968976)
                return (7.8990640048967596475E-1 + sc * (1.5993699253596663678 + sc * (1.6481729039140370242 + sc * (9.8227188109869200166E-1 + sc * (3.6313557966186936883E-1 + sc * (7.8277036261179606301E-2 + 9.3404307364538726214E-3 * sc)))))) / (1 + sc * (2.0247407005640401446 + sc * (2.0087454279103740489 + sc * (1.1627561803056961973 + sc * (4.2004672123723823581E-1 + sc * (8.9130862793887234546E-2 + 1.0436767768858021717E-2 * sc))))));
            return (7.91133825948419359E-1 + sc * (1.24653733210880042 + sc * (1.32747426980537386 + sc * (6.95009705717846778E-1 + sc * (3.05965944268228457E-1 + sc * (6.02200363391352887E-2 + 1.29050244454344842E-2 * sc)))))) / (1 + sc * (1.58117486714634672 + sc * (1.60144713247629644 + sc * (8.30040185836882436E-1 + sc * (3.53071863813401531E-1 + sc * (6.95901684131758475E-2 + 1.44197580643890011E-2 * sc))))));
        }

        // ---- lower and upper maps (initial guesses in the outer branches) ---------------------------------------

        /// <summary>
        /// f(b) = 2 pi / sqrt(27) |x| Phi(-|x| / (sqrt(3) s))^3 at s, and its first two derivatives with respect to b
        /// (equations 4.5, 4.16 and 4.17 of the paper).
        /// </summary>
        static void LowerMap(double x, double s, out double f, out double fp, out double fpp)
        {
            double ax = Math.Abs(x), z = SqrtOneOverThree * ax / s, y = z * z, s2 = s * s;
            double phiCdf = 0.5 * NormalDistribution.Erfc(InvSqrtTwo * z); // Phi(-z)
            double phiPdf = NormalDistribution.Pdf(z);
            fpp = PiOverSix * y / (s2 * s) * phiCdf * (8.0 * SqrtThree * s * ax + (3.0 * s2 * (s2 - 8.0) - 8.0 * x * x) * phiCdf / phiPdf)
                * Math.Exp(2.0 * y + 0.25 * s2);
            double phi2 = phiCdf * phiCdf;
            fp = TwoPi * y * phi2 * Math.Exp(y + 0.125 * s * s);
            f = TwoPiOverSqrtTwentySeven * ax * (phi2 * phiCdf);
        }

        /// <summary>Inverse of the lower map, equation 4.38. The two cube roots are deliberately not merged (underflow).</summary>
        static double InverseLowerMap(double x, double f)
        {
            return Math.Abs(x / (SqrtThree * NormalDistribution.InverseCdf(SqrtThreeOverCbrtTwoPi * Cbrt(f) / Cbrt(Math.Abs(x)))));
        }

        /// <summary>f(b) = Phi(-s/2) at s, and its first two derivatives with respect to b.</summary>
        static void UpperMap(double x, double s, out double f, out double fp, out double fpp)
        {
            f = 0.5 * NormalDistribution.Erfc((0.5 * InvSqrtTwo) * s);
            double xs = x / s, w = xs * xs;
            fp = -0.5 * Math.Exp(0.5 * w);
            fpp = SqrtPiOverTwo * Math.Exp(w + 0.125 * s * s) * w / s;
        }

        static double InverseUpperMap(double f)
        {
            return -2.0 * NormalDistribution.InverseCdf(f);
        }

        static double Cbrt(double x)
        {
            return x > 0.0 ? Math.Pow(x, 1.0 / 3.0) : 0.0;
        }

        // ---- rational cubic interpolation (P. Jäckel, after Delbourgo and Gregory) -------------------------------

        static double RationalCubic(double x, double xL, double xR, double yL, double yR, double dL, double dR, double r)
        {
            double h = xR - xL;
            if (Math.Abs(h) <= 0.0) return 0.5 * (yL + yR);
            double t = (x - xL) / h;
            if (!(r >= MaximumRationalCubicControl))
            {
                double omt = 1.0 - t, t2 = t * t, omt2 = omt * omt;
                return (yR * t2 * t + (r * yR - h * dR) * t2 * omt + (r * yL + h * dL) * t * omt2 + yL * omt2 * omt)
                    / (1.0 + (r - 3.0) * t * omt);
            }
            return yR * t + yL * (1.0 - t);
        }

        static double ControlToFitSecondDerivativeAtLeft(double xL, double xR, double yL, double yR, double dL, double dR,
            double secondDerivativeL)
        {
            double h = xR - xL, numerator = 0.5 * h * secondDerivativeL + (dR - dL);
            double denominator = (yR - yL) / h - dL;
            if (Math.Abs(denominator) < DblMin)
                return numerator > 0.0 ? MaximumRationalCubicControl : MinimumRationalCubicControl;
            return numerator / denominator;
        }

        static double ControlToFitSecondDerivativeAtRight(double xL, double xR, double yL, double yR, double dL, double dR,
            double secondDerivativeR)
        {
            double h = xR - xL, numerator = 0.5 * h * secondDerivativeR + (dR - dL);
            double denominator = dR - (yR - yL) / h;
            if (Math.Abs(denominator) < DblMin)
                return numerator > 0.0 ? MaximumRationalCubicControl : MinimumRationalCubicControl;
            return numerator / denominator;
        }

        static double MinimumControl(double dL, double dR, double s, bool preferShapePreservationOverSmoothness)
        {
            bool monotonic = dL * s >= 0.0 && dR * s >= 0.0, convex = dL <= s && s <= dR, concave = dL >= s && s >= dR;
            if (!monotonic && !convex && !concave)
                return MinimumRationalCubicControl;
            double dRmdL = dR - dL, dRms = dR - s, smdL = s - dL;
            double r1 = -double.MaxValue, r2 = r1;
            if (monotonic)
            {
                if (!(Math.Abs(s) < DblMin))
                    r1 = (dR + dL) / s;
                else if (preferShapePreservationOverSmoothness)
                    r1 = MaximumRationalCubicControl;
            }
            if (convex || concave)
            {
                if (!(Math.Abs(smdL) < DblMin || Math.Abs(dRms) < DblMin))
                    r2 = Math.Max(Math.Abs(dRmdL / dRms), Math.Abs(dRmdL / smdL));
                else if (preferShapePreservationOverSmoothness)
                    r2 = MaximumRationalCubicControl;
            }
            else if (monotonic && preferShapePreservationOverSmoothness)
            {
                r2 = MaximumRationalCubicControl;
            }
            return Math.Max(MinimumRationalCubicControl, Math.Max(r1, r2));
        }

        static double ConvexControlToFitSecondDerivativeAtLeft(double xL, double xR, double yL, double yR, double dL,
            double dR, double secondDerivativeL, bool preferShapePreservationOverSmoothness)
        {
            double r = ControlToFitSecondDerivativeAtLeft(xL, xR, yL, yR, dL, dR, secondDerivativeL);
            double rMin = MinimumControl(dL, dR, (yR - yL) / (xR - xL), preferShapePreservationOverSmoothness);
            return Math.Max(r, rMin);
        }

        static double ConvexControlToFitSecondDerivativeAtRight(double xL, double xR, double yL, double yR, double dL,
            double dR, double secondDerivativeR, bool preferShapePreservationOverSmoothness)
        {
            double r = ControlToFitSecondDerivativeAtRight(xL, xR, yL, yR, dL, dR, secondDerivativeR);
            double rMin = MinimumControl(dL, dR, (yR - yL) / (xR - xL), preferShapePreservationOverSmoothness);
            return Math.Max(r, rMin);
        }
    }
}
