using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Kernel-compatible semi-analytic Heston (1993) pricing: the characteristic function of the log-forward in the
    /// "little Heston trap" form and the COS method of Fang and Oosterlee for European options. No allocation, no
    /// exceptions, only <c>Math.*</c> and <see cref="Cplx"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Model, under the pricing measure with the forward F to the expiry as numeraire-adjusted underlying:
    /// <c>dF/F = sqrt(v) dW1</c>, <c>dv = kappa (theta - v) dt + xi sqrt(v) dW2</c>, <c>d&lt;W1, W2&gt; = rho dt</c>,
    /// <c>v(0) = v0</c>. Rates and dividends enter only through the forward and the discount factor, so the model
    /// prices from (F, DF) exactly as Black-76 does.
    /// </para>
    /// <para>
    /// Characteristic function of <c>X = ln(S_T / F)</c> (Albrecher, Mayer, Schoutens and Tistaert, "The little Heston
    /// trap", Wilmott Magazine, January 2007): with <c>beta = kappa - i rho xi u</c>,
    /// <c>d = sqrt(beta^2 + xi^2 (u^2 + i u))</c> (principal root), <c>g = (beta - d) / (beta + d)</c>:
    /// <c>phi(u) = exp(C + D v0)</c>, <c>D = (beta - d) / xi^2 (1 - e^{-dT}) / (1 - g e^{-dT})</c>,
    /// <c>C = kappa theta / xi^2 [(beta - d) T - 2 ln((1 - g e^{-dT}) / (1 - g))]</c>. This form keeps the complex
    /// logarithm on its principal branch for all u, unlike Heston's original.
    /// Two rewritings make it exact down to xi = 0 (deterministic variance, a Black-76 limit): <c>beta - d</c> is
    /// evaluated as <c>-xi^2 (u^2 + i u) / (beta + d)</c> (no cancellation, and the 1/xi^2 factors cancel
    /// analytically), and <c>ln(1 + z) / xi^2</c>, <c>z = g (1 - e^{-dT}) / (1 - g)</c>, by its series when |z| is small.
    /// </para>
    /// <para>
    /// COS method (F. Fang and C. W. Oosterlee, "A novel pricing method for European options based on Fourier-cosine
    /// series expansions", SIAM J. Sci. Comput. 31(2), 2008): the density of <c>y = ln(S_T / K)</c> is expanded in a
    /// cosine series on a truncation range [a, b] and integrated against the payoff in closed form,
    /// <c>put = DF K sum'_{k&lt;N} Re[phi(u_k) e^{i u_k (x - a)}] V_k</c>, <c>u_k = k pi / (b - a)</c>,
    /// <c>x = ln(F / K)</c>, <c>V_k = 2 / (b - a) (psi_k(a, 0) - chi_k(a, 0))</c>, the first term halved.
    /// Puts are priced by COS and calls by put-call parity <c>call = put + DF (F - K)</c>, as Fang and Oosterlee
    /// recommend: the put payoff is bounded, so its cosine coefficients do not grow with the upper end of the range.
    /// </para>
    /// <para>
    /// Truncation range (Fang and Oosterlee's rule, their eq. 49): <c>x + c1 -/+ L sqrt(c2 + sqrt(c4))</c> with
    /// c1, c2, c4 the cumulants of X and L = <see cref="DefaultTruncationWidth"/>. c1 is exact; c2 comes from the
    /// second-order expansion of the Riccati equations (see <see cref="Cumulants"/>; this closed form was re-derived and
    /// is checked against the characteristic function in the tests, since a transcription of the published Heston
    /// expression did not match it); c4, whose closed form is lengthy, is the fourth central difference of
    /// <c>Re ln phi</c> at step <c>0.05 / sqrt(c2)</c>, accurate to about 7% in strongly fat-tailed cases (far
    /// better otherwise), which is all a truncation range needs. The c4 term matters: Heston log-returns are
    /// fat-tailed when the Feller condition fails, and on the Fang-Oosterlee T = 1 case the range
    /// <c>12 sqrt(c2)</c> alone leaves a 4e-5 truncation error.
    /// </para>
    /// <para>
    /// The error decays exponentially in N down to the truncation floor. With the defaults (L = 12,
    /// N = <see cref="DefaultTerms"/> = 512) it is below 1e-9 absolute (forward 100) on the Fang-Oosterlee
    /// cases from T = 0.25 to T = 10 and below 1e-12 on milder parameter sets; N = 256 gives about 1e-7. Large vol of
    /// vol with strong correlation over long horizons (xi = 1, rho = -0.9, T = 2) has a very peaked, fat-tailed
    /// density and needs several thousand terms for 1e-7: increase N there.
    /// </para>
    /// <para>
    /// Degenerate inputs are not checked here (the host API <see cref="Heston"/> validates): kappa must be positive,
    /// v0, theta and xi non-negative, |rho| &lt;= 1, F, K, T and DF positive and N &gt;= 1. Prices are clamped at 0.
    /// </para>
    /// </remarks>
    // Not static so that a HybRunner can wrap an instance to reach the [EntryPoint] array kernels
    // (runner.Wrap(new HestonKernels()).<EntryPoint>(...), as for FullRevaluationKernels); it has no state.
    public sealed class HestonKernels
    {
        /// <summary>Default number of cosine terms N.</summary>
        public const int DefaultTerms = 512;

        /// <summary>Default truncation-range half width L, in standard deviations of ln(S_T / F).</summary>
        public const double DefaultTruncationWidth = 12.0;

        /// <summary>Below this |z|, ln(1 + z) / xi^2 is evaluated by its series (error below |z|^10 / 10).</summary>
        const double SeriesThreshold = 1e-2;

        /// <summary>
        /// Characteristic function <c>E[exp(i u ln(S_T / F))]</c> of the log-forward at expiry T, for real u.
        /// </summary>
        [Kernel]
        public static void CharacteristicFunction(double u, double maturity,
            double v0, double kappa, double theta, double xi, double rho, out double re, out double im)
        {
            Cplx phi = LogForwardCharacteristicFunction(u, maturity, v0, kappa, theta, xi, rho);
            re = phi.Re;
            im = phi.Im;
        }

        /// <summary>Characteristic function of ln(S_T / F) at real u (see the class remarks).</summary>
        public static Cplx LogForwardCharacteristicFunction(double u, double maturity,
            double v0, double kappa, double theta, double xi, double rho)
        {
            return Cplx.Exp(LogCharacteristicExponent(u, maturity, v0, kappa, theta, xi, rho));
        }

        /// <summary>
        /// <c>ln phi(u) = C + D v0</c>, evaluated without the final exponential, so that its real part, which is
        /// O(u^2) near 0, keeps full relative accuracy (the cumulant c4 is a finite difference of it).
        /// </summary>
        public static Cplx LogCharacteristicExponent(double u, double maturity,
            double v0, double kappa, double theta, double xi, double rho)
        {
            if (u == 0.0) return new Cplx(0.0, 0.0);
            double xi2 = xi * xi;
            Cplx beta = new Cplx(kappa, -rho * xi * u);
            Cplx q = new Cplx(u * u, u);                          // u^2 + i u
            Cplx d = Cplx.Sqrt(beta * beta + xi2 * q);
            Cplx betaPlusD = beta + d;
            Cplx bmdOverXi2 = -(q / betaPlusD);                   // (beta - d) / xi^2
            Cplx gOverXi2 = bmdOverXi2 / betaPlusD;               // g / xi^2
            Cplx g = xi2 * gOverXi2;
            Cplx e = Cplx.Exp(-(d * maturity));
            Cplx oneMinusE = 1.0 - e;
            Cplx dTerm = bmdOverXi2 * oneMinusE / (1.0 - g * e);

            // ln((1 - g e) / (1 - g)) = ln(1 + z) with z = g (1 - e) / (1 - g)
            Cplx zOverXi2 = gOverXi2 * oneMinusE / (1.0 - g);
            Cplx z = xi2 * zOverXi2;
            Cplx logOverXi2;
            if (Cplx.Abs(z) < SeriesThreshold)
            {
                // ln(1 + z) / xi^2 = (z / xi^2) (1 - z/2 + z^2/3 - ... - z^9/10)
                Cplx s = new Cplx(-1.0 / 10.0, 0.0);
                for (int n = 9; n >= 1; --n)
                    s = (n % 2 == 1 ? 1.0 / n : -1.0 / n) + s * z;
                logOverXi2 = zOverXi2 * s;
            }
            else
            {
                logOverXi2 = Cplx.Log(1.0 + z) / xi2;
            }
            Cplx cTerm = kappa * theta * (bmdOverXi2 * maturity - 2.0 * logOverXi2);
            return cTerm + dTerm * v0;
        }

        /// <summary>
        /// Cumulants c1, c2, c4 of ln(S_T / F) used for the truncation range: c1 = -E[int_0^T v dt] / 2 exactly; c2
        /// (the variance) in closed form, with kappa floored at 0.01 / T to avoid the cancellation of its 1 / kappa^3
        /// terms; c4 by a central difference, floored at 0 (see the class remarks).
        /// </summary>
        /// <remarks>
        /// c2: with <c>s = i u</c>, <c>ln phi = A + B v0</c>, <c>A = kappa theta int_0^T B</c> and B solving
        /// <c>B' = (s^2 - s)/2 - (kappa - rho xi s) B + xi^2 B^2 / 2</c>, <c>B(0) = 0</c>. Writing
        /// <c>B = b1 s + b2 s^2 + O(s^3)</c>: <c>b1 = -(1 - E)/(2 kappa)</c>, <c>E = exp(-kappa t)</c>, and
        /// <c>b2' + kappa b2 = 1/2 + rho xi b1 + xi^2 b1^2 / 2 = a0 + a1 E + a2 E^2</c>, solved exactly; then
        /// <c>c2 = 2 (v0 b2(T) + kappa theta int_0^T b2)</c>.
        /// </remarks>
        [Kernel]
        public static void Cumulants(double maturity, double v0, double kappa, double theta, double xi, double rho,
            out double c1, out double c2, out double c4)
        {
            double t = maturity;
            double meanIntegratedVariance = theta * t + (v0 - theta) * OneMinusExpOverX(kappa * t) * t;
            c1 = -0.5 * meanIntegratedVariance;

            double k = Math.Max(kappa, 0.01 / t);
            double e = Math.Exp(-k * t);
            double k2 = k * k;
            double a0 = 0.5 - rho * xi / (2.0 * k) + xi * xi / (8.0 * k2);
            double a1 = rho * xi / (2.0 * k) - xi * xi / (4.0 * k2);
            double a2 = xi * xi / (8.0 * k2);
            double b2 = a0 / k * (1.0 - e) + a1 * t * e + a2 / k * (e - e * e);
            double intB2 = a0 / k * (t - (1.0 - e) / k) + a1 * (1.0 - e - k * t * e) / k2
                + a2 / k * ((1.0 - e) / k - (1.0 - e * e) / (2.0 * k));
            c2 = 2.0 * (v0 * b2 + k * theta * intB2);

            // Re ln phi(u) = -c2 u^2/2 + c4 u^4/24 - ... is even: c4 ~ (2 K(2h) - 8 K(h)) / h^4, error O(h^2 c6)
            double h = 0.05 / Math.Sqrt(Math.Max(c2, 1e-16));
            double k1 = LogCharacteristicExponent(h, maturity, v0, kappa, theta, xi, rho).Re;
            double kTwo = LogCharacteristicExponent(2.0 * h, maturity, v0, kappa, theta, xi, rho).Re;
            double h2 = h * h;
            c4 = Math.Max((2.0 * kTwo - 8.0 * k1) / (h2 * h2), 0.0);
        }

        /// <summary>(1 - exp(-x)) / x, accurate near 0.</summary>
        static double OneMinusExpOverX(double x)
        {
            if (Math.Abs(x) < 1e-5) return 1.0 - 0.5 * x + x * x / 6.0;
            return (1.0 - Math.Exp(-x)) / x;
        }

        /// <summary>
        /// Undiscounted put price divided by the strike, <c>E[(1 - S_T / K)^+]</c>, by the COS method.
        /// </summary>
        [Kernel]
        public static double NormalisedPut(double forward, double strike, double maturity,
            double v0, double kappa, double theta, double xi, double rho, int terms, double truncationWidth)
        {
            double c1, c2, c4;
            Cumulants(maturity, v0, kappa, theta, xi, rho, out c1, out c2, out c4);
            double w = truncationWidth * Math.Sqrt(Math.Max(c2, 1e-16) + Math.Sqrt(c4));
            double x = Math.Log(forward / strike);
            double a = x + c1 - w, b = x + c1 + w;
            if (a >= 0.0) return 0.0;          // S_T > K on the whole range
            double top = Math.Min(b, 0.0);      // put payoff support within [a, b]
            double range = b - a;
            double xMinusA = w - c1;           // x - a
            double eTop = Math.Exp(top), eA = Math.Exp(a);

            double sum = 0.0;
            for (int k = 0; k < terms; ++k)
            {
                double omega = k * Math.PI / range;
                double cosTop = Math.Cos(omega * (top - a)), sinTop = Math.Sin(omega * (top - a));
                // chi_k(a, top) = int_a^top e^y cos(omega (y - a)) dy, psi_k(a, top) = int_a^top cos(omega (y - a)) dy
                double chi = (cosTop * eTop - eA + omega * sinTop * eTop) / (1.0 + omega * omega);
                double psi = k == 0 ? top - a : sinTop / omega;
                double vk = 2.0 / range * (psi - chi);

                Cplx phi = LogForwardCharacteristicFunction(omega, maturity, v0, kappa, theta, xi, rho);
                double angle = omega * xMinusA;
                double re = phi.Re * Math.Cos(angle) - phi.Im * Math.Sin(angle);
                double term = re * vk;
                sum += k == 0 ? 0.5 * term : term;
            }
            return Math.Max(sum, 0.0);
        }

        /// <summary>
        /// Heston price of a European call (sign = +1) or put (sign = -1): the put by COS, the call by put-call parity,
        /// both clamped at 0.
        /// </summary>
        /// <param name="sign">+1 for a call, -1 for a put.</param>
        /// <param name="forward">forward of the underlying for the expiry.</param>
        /// <param name="strike">strike.</param>
        /// <param name="discountFactor">discount factor from the payment date (the expiry) to the valuation date.</param>
        /// <param name="maturity">time to expiry in years.</param>
        /// <param name="v0">initial variance.</param>
        /// <param name="kappa">mean-reversion speed.</param>
        /// <param name="theta">long-run variance.</param>
        /// <param name="xi">volatility of variance.</param>
        /// <param name="rho">correlation between the forward and the variance.</param>
        /// <param name="terms">number of cosine terms N.</param>
        /// <param name="truncationWidth">truncation-range half width L in standard deviations.</param>
        [Kernel]
        public static double Price(int sign, double forward, double strike, double discountFactor, double maturity,
            double v0, double kappa, double theta, double xi, double rho, int terms, double truncationWidth)
        {
            double put = discountFactor * strike
                * NormalisedPut(forward, strike, maturity, v0, kappa, theta, xi, rho, terms, truncationWidth);
            if (sign < 0) return put;
            return Math.Max(put + discountFactor * (forward - strike), 0.0);
        }

        /// <summary>
        /// Evaluates one option of a batch sharing one parameter set: reads element <paramref name="index"/> of the
        /// input arrays and writes the price to <paramref name="price"/>.
        /// </summary>
        [Kernel]
        public static void PriceAt(int index, double[] price, int[] sign, double[] forward, double[] strike,
            double[] discountFactor, double[] maturity,
            double v0, double kappa, double theta, double xi, double rho, int terms, double truncationWidth)
        {
            price[index] = Price(sign[index], forward[index], strike[index], discountFactor[index], maturity[index],
                v0, kappa, theta, xi, rho, terms, truncationWidth);
        }

        /// <summary>
        /// Evaluates a batch of Heston prices under one parameter set, one option per array element.
        /// </summary>
        [EntryPoint("HestonKernels_Price_Array")]
        public static void PriceArray(int count, double[] price, int[] sign, double[] forward, double[] strike,
            double[] discountFactor, double[] maturity,
            double v0, double kappa, double theta, double xi, double rho, int terms, double truncationWidth)
        {
            for (int k = threadIdx.x + blockDim.x * blockIdx.x; k < count; k += blockDim.x * gridDim.x)
            {
                PriceAt(k, price, sign, forward, strike, discountFactor, maturity,
                    v0, kappa, theta, xi, rho, terms, truncationWidth);
            }
        }
    }
}
