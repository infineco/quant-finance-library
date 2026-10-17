using System;
using InFine.QuantFinance.ClosedForm;

namespace InFine.QuantFinance.Risk
{
    /// <summary>
    /// Parametric (variance-covariance, delta-normal) Value-at-Risk and Expected Shortfall, the Cornish-Fisher
    /// "modified" VaR, and the Euler decomposition of the delta-normal measures. Host-side code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sign convention.</b> P&amp;L is positive for a gain, VaR and ES are positive losses (see <see cref="VarEs"/>).
    /// The portfolio P&amp;L over the horizon is modelled as normal with mean <c>mu</c> and standard deviation
    /// <c>sigma</c>; with <c>z = Phi^-1(alpha)</c> and <c>phi</c> the standard normal density:
    /// <c>VaR = z sigma - mu</c> and <c>ES = sigma phi(z) / (1 - alpha) - mu</c>.
    /// </para>
    /// <para>
    /// <b>Horizon scaling is an assumption.</b> The covariance, means and moments you pass describe <b>one period</b>
    /// (for example one day). A horizon of <c>h</c> periods is obtained by assuming independent, identically
    /// distributed periods: the mean grows with <c>h</c>, the standard deviation with <c>sqrt(h)</c> (variance with
    /// <c>h</c>), skewness shrinks as <c>1/sqrt(h)</c> and excess kurtosis as <c>1/h</c>. Volatility clustering,
    /// autocorrelation, mean reversion and intra-horizon trading all break it; the historical measures do not scale and
    /// should be fed horizon-length returns instead. Positions are fixed over the horizon (delta-normal: P&amp;L is
    /// linear in the risk factors).
    /// </para>
    /// </remarks>
    public static class ParametricRisk
    {
        /// <summary>
        /// VaR and ES of a normally distributed P&amp;L.
        /// </summary>
        /// <param name="sigma">Standard deviation of the one-period P&amp;L, non-negative.</param>
        /// <param name="confidence">Confidence level in (0, 1).</param>
        /// <param name="mean">Expected one-period P&amp;L (gain positive), default 0.</param>
        /// <param name="horizon">Number of periods, positive; scaled by the square-root-of-time assumption (see class remarks).</param>
        public static VarEs Normal(double sigma, double confidence, double mean = 0.0, double horizon = 1.0)
        {
            RiskArgs.Confidence(confidence);
            RequireSigma(sigma);
            RequireMean(mean);
            RequireHorizon(horizon);
            double sh = sigma * Math.Sqrt(horizon), mh = mean * horizon;
            double z = NormalDistribution.InverseCdf(confidence);
            return new VarEs(z * sh - mh, sh * NormalDistribution.Pdf(z) / (1.0 - confidence) - mh);
        }

        /// <summary>
        /// Portfolio standard deviation <c>sqrt(w^T Sigma w)</c> of the one-period P&amp;L for currency positions <c>w</c>
        /// and a one-period return covariance <c>Sigma</c> (symmetric, finite, positive semi-definite up to rounding).
        /// </summary>
        public static double PortfolioVolatility(double[] positions, double[,] covariance)
        {
            RequireInputs(positions, covariance, null);
            return Sigma(positions, covariance, out _);
        }

        /// <summary>
        /// Delta-normal VaR and ES of a portfolio with currency positions <paramref name="positions"/>, return covariance
        /// <paramref name="covariance"/> and optional expected returns <paramref name="mean"/> (all per period).
        /// </summary>
        public static VarEs DeltaNormal(double[] positions, double[,] covariance, double confidence, double[]? mean = null, double horizon = 1.0)
        {
            RiskArgs.Confidence(confidence);
            RequireHorizon(horizon);
            RequireInputs(positions, covariance, mean);
            return Normal(Sigma(positions, covariance, out _), confidence, Dot(positions, mean), horizon);
        }

        // ---- Cornish-Fisher ------------------------------------------------------------------------------------

        /// <summary>
        /// Cornish-Fisher modified VaR and ES (Zangari, "A VaR methodology for portfolios that include options",
        /// RiskMetrics Monitor 1996; Favre and Galeano 2002; ES after Boudt, Peterson and Croux 2008): the normal quantile
        /// is replaced by the fourth-moment expansion
        /// <c>g(u) = u + (u^2 - 1) S/6 + (u^3 - 3u) K/24 - (2u^3 - 5u) S^2/36</c> of the quantile of a distribution with
        /// skewness <c>S</c> and excess kurtosis <c>K</c>, evaluated at <c>u = Phi^-1(1 - alpha)</c>, so that
        /// <c>VaR = -mu - sigma g(u)</c>. The ES integrates the same polynomial over the normal tail in closed form:
        /// <c>ES = -mu - sigma/(1-alpha) integral_{-inf}^{u} g(v) phi(v) dv</c>. With <c>S = K = 0</c> both reduce
        /// exactly to <see cref="Normal"/>.
        /// </summary>
        /// <param name="sigma">One-period P&amp;L standard deviation.</param>
        /// <param name="skewness">Skewness of the one-period P&amp;L (negative: long left tail).</param>
        /// <param name="excessKurtosis">Excess kurtosis of the one-period P&amp;L (0 for normal).</param>
        /// <param name="confidence">Confidence level.</param>
        /// <param name="mean">Expected one-period P&amp;L, default 0.</param>
        /// <param name="horizon">Periods; mean x h, sigma x sqrt(h), skewness / sqrt(h), excess kurtosis / h (iid periods).</param>
        /// <exception cref="ArgumentException">The expansion is not monotone on the tail up to the median (see <see cref="IsCornishFisherValid"/>): the result would be meaningless.</exception>
        /// <remarks>
        /// <b>Limits of validity.</b> The expansion is a local, fourth-order approximation. It orders quantiles correctly
        /// (<c>g</c> increasing) only for moderate moments: at 99% roughly <c>|S| &lt; 1</c> and <c>K</c> up to a few units, and
        /// the admissible region shrinks for extreme confidence levels and when <c>S</c> and <c>K</c> are both large. Outside
        /// it VaR can fall as confidence rises, so this method refuses rather than returning it. Within the region it still
        /// fits the true tail only as well as four moments can describe it, and sample skewness and kurtosis are themselves
        /// noisy; the ES uses the polynomial beyond the quantile, where it is least reliable.
        /// </remarks>
        public static VarEs CornishFisher(double sigma, double skewness, double excessKurtosis, double confidence, double mean = 0.0, double horizon = 1.0)
        {
            RiskArgs.Confidence(confidence);
            RequireSigma(sigma);
            RequireMean(mean);
            RequireHorizon(horizon);
            if (!double.IsFinite(skewness) || !double.IsFinite(excessKurtosis))
                throw new ArgumentException("Skewness and excess kurtosis must be finite.");
            double s = skewness / Math.Sqrt(horizon), k = excessKurtosis / horizon;
            if (!IsCornishFisherValid(s, k, confidence))
                throw new ArgumentException("The Cornish-Fisher expansion is not monotone for these skewness and excess kurtosis at this confidence level.");
            double sh = sigma * Math.Sqrt(horizon), mh = mean * horizon;
            double u = -NormalDistribution.InverseCdf(confidence);
            double phi = NormalDistribution.Pdf(u), cdf = 1.0 - confidence; // Phi(u) = 1 - alpha; avoid recomputation error
            // Truncated normal moments M_k = integral_{-inf}^{u} v^k phi(v) dv (recursion M_k = -u^(k-1) phi + (k-1) M_(k-2)).
            double m0 = cdf, m1 = -phi, m2 = m0 - u * phi, m3 = -u * u * phi + 2.0 * m1;
            double integral = m1 + (m2 - m0) * s / 6.0 + (m3 - 3.0 * m1) * k / 24.0 - (2.0 * m3 - 5.0 * m1) * s * s / 36.0;
            return new VarEs(-mh - sh * Quantile(u, s, k), -mh - sh * integral / (1.0 - confidence));
        }

        /// <summary>
        /// True when the Cornish-Fisher quantile function is strictly increasing between the median and the
        /// <c>1 - alpha</c> quantile, i.e. <c>g'(u) = 1 + uS/3 + (u^2 - 1)K/8 - (u^2 S^2)/6 + 5 S^2/36 &gt; 0</c> for
        /// <c>u</c> in <c>[Phi^-1(1 - alpha), 0]</c>. This is the minimum requirement for the modified VaR to be usable.
        /// </summary>
        public static bool IsCornishFisherValid(double skewness, double excessKurtosis, double confidence)
        {
            RiskArgs.Confidence(confidence);
            if (!double.IsFinite(skewness) || !double.IsFinite(excessKurtosis)) return false;
            double s = skewness, k = excessKurtosis;
            double a = k / 8.0 - s * s / 6.0, b = s / 3.0, c = 1.0 - k / 8.0 + 5.0 * s * s / 36.0;
            double u0 = -NormalDistribution.InverseCdf(confidence);
            double min = Math.Min(Deriv(u0), Deriv(0.0));
            if (a > 0.0)
            {
                double vertex = -b / (2.0 * a);
                if (vertex > u0 && vertex < 0.0) min = Math.Min(min, Deriv(vertex));
            }
            return min > 0.0;

            double Deriv(double u) => (a * u + b) * u + c;
        }

        static double Quantile(double u, double s, double k) =>
            u + (u * u - 1.0) * s / 6.0 + (u * u * u - 3.0 * u) * k / 24.0 - (2.0 * u * u * u - 5.0 * u) * s * s / 36.0;

        // ---- decomposition -------------------------------------------------------------------------------------

        /// <summary>
        /// Euler decomposition of the delta-normal VaR and ES, with incremental risk (the portfolio recomputed with each
        /// position set to zero).
        /// </summary>
        /// <param name="positions">Currency position per asset.</param>
        /// <param name="covariance">One-period return covariance.</param>
        /// <param name="confidence">Confidence level.</param>
        /// <param name="mean">Optional one-period expected returns.</param>
        /// <param name="horizon">Periods (square-root-of-time assumption, see class remarks).</param>
        /// <remarks>
        /// With <c>sigma_p = sqrt(w^T Sigma w)</c>, <c>z = Phi^-1(alpha)</c> and <c>k = phi(z)/(1 - alpha)</c>, the marginal
        /// VaR is <c>d VaR / d w_i = z (Sigma w)_i / sigma_p - mu_i</c> and the marginal ES
        /// <c>k (Sigma w)_i / sigma_p - mu_i</c> (covariance and means scaled by the horizon). The component
        /// <c>w_i x marginal_i</c> sums exactly to the total, because both measures are homogeneous of degree one in
        /// <c>w</c>. Percentage contributions are <see cref="RiskDecomposition.PercentVar"/> and
        /// <see cref="RiskDecomposition.PercentEs"/>.
        /// </remarks>
        /// <exception cref="InvalidOperationException">The portfolio has zero volatility: marginal risk is undefined.</exception>
        public static RiskDecomposition Decompose(double[] positions, double[,] covariance, double confidence, double[]? mean = null, double horizon = 1.0)
        {
            RiskArgs.Confidence(confidence);
            RequireHorizon(horizon);
            RequireInputs(positions, covariance, mean);
            int n = positions.Length;
            double sigma1 = Sigma(positions, covariance, out double[] sw);
            if (!(sigma1 > 0.0)) throw new InvalidOperationException("The portfolio has zero volatility; marginal and component risk are undefined.");

            double z = NormalDistribution.InverseCdf(confidence);
            double k = NormalDistribution.Pdf(z) / (1.0 - confidence);
            double sqrtH = Math.Sqrt(horizon);
            var margVar = new double[n];
            var compVar = new double[n];
            var margEs = new double[n];
            var compEs = new double[n];
            for (int i = 0; i < n; i++)
            {
                double dSigma = sqrtH * sw[i] / sigma1; // d sigma_h / d w_i
                double dMean = mean == null ? 0.0 : mean[i] * horizon;
                margVar[i] = z * dSigma - dMean;
                margEs[i] = k * dSigma - dMean;
                compVar[i] = positions[i] * margVar[i];
                compEs[i] = positions[i] * margEs[i];
            }

            VarEs total = DeltaNormal(positions, covariance, confidence, mean, horizon);
            var incVar = new double[n];
            var incEs = new double[n];
            var reduced = (double[])positions.Clone();
            for (int i = 0; i < n; i++)
            {
                double saved = reduced[i];
                reduced[i] = 0.0;
                VarEs without = DeltaNormal(reduced, covariance, confidence, mean, horizon);
                reduced[i] = saved;
                incVar[i] = total.Var - without.Var;
                incEs[i] = total.ExpectedShortfall - without.ExpectedShortfall;
            }
            return new RiskDecomposition(total, margVar, compVar, margEs, compEs, incVar, incEs);
        }

        // ---- helpers -------------------------------------------------------------------------------------------

        /// <summary><c>sqrt(w^T Sigma w)</c>; also returns <c>Sigma w</c>. A variance that is negative beyond rounding is an error.</summary>
        static double Sigma(double[] w, double[,] cov, out double[] sw)
        {
            int n = w.Length;
            sw = new double[n];
            double variance = 0.0, scale = 0.0;
            for (int i = 0; i < n; i++)
            {
                double s = 0.0;
                for (int j = 0; j < n; j++)
                {
                    s += cov[i, j] * w[j];
                    scale += Math.Abs(cov[i, j] * w[i] * w[j]);
                }
                sw[i] = s;
                variance += w[i] * s;
            }
            if (variance < -1e-12 * scale)
                throw new ArgumentException("The covariance matrix is not positive semi-definite: the portfolio variance is negative.", nameof(cov));
            return Math.Sqrt(Math.Max(variance, 0.0));
        }

        static double Dot(double[] positions, double[]? mean)
        {
            if (mean == null) return 0.0;
            double s = 0.0;
            for (int i = 0; i < positions.Length; i++) s += positions[i] * mean[i];
            return s;
        }

        static void RequireInputs(double[] positions, double[,] covariance, double[]? mean)
        {
            RiskArgs.Finite(positions, nameof(positions));
            ArgumentNullException.ThrowIfNull(covariance);
            int n = positions.Length;
            if (covariance.GetLength(0) != n || covariance.GetLength(1) != n)
                throw new ArgumentException($"Covariance must be {n}x{n}.", nameof(covariance));
            if (!InFine.QuantFinance.Numerics.Matrix.IsFinite(covariance)) throw new ArgumentException("Covariance must be finite.", nameof(covariance));
            double maxAbs = 0.0;
            foreach (double v in covariance) maxAbs = Math.Max(maxAbs, Math.Abs(v));
            if (InFine.QuantFinance.Numerics.Matrix.MaxAsymmetry(covariance) > 1e-10 * Math.Max(maxAbs, 1e-300))
                throw new ArgumentException("Covariance must be symmetric.", nameof(covariance));
            if (mean != null)
            {
                RiskArgs.Finite(mean, nameof(mean));
                if (mean.Length != n) throw new ArgumentException($"Expected {n} expected returns.", nameof(mean));
            }
        }

        static void RequireSigma(double sigma)
        {
            if (!(sigma >= 0.0) || double.IsInfinity(sigma)) throw new ArgumentOutOfRangeException(nameof(sigma), "Sigma must be finite and non-negative.");
        }

        static void RequireMean(double mean)
        {
            if (!double.IsFinite(mean)) throw new ArgumentOutOfRangeException(nameof(mean), "Mean must be finite.");
        }

        static void RequireHorizon(double horizon)
        {
            if (!(horizon > 0.0) || double.IsInfinity(horizon)) throw new ArgumentOutOfRangeException(nameof(horizon), "Horizon must be positive and finite.");
        }
    }
}
