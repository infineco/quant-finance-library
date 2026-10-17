using System;

namespace InFine.QuantFinance.Volatility
{
    /// <summary>
    /// Surface SVI (Gatheral and Jacquier, "Arbitrage-free SVI volatility surfaces", 2014, section 4) with the power-law
    /// curvature function of their eq. (4.5):
    /// <c>w(k, theta) = theta/2 (1 + rho phi(theta) k + sqrt((phi(theta) k + rho)^2 + 1 - rho^2))</c>,
    /// <c>phi(theta) = eta / (theta^gamma (1 + theta)^(1 - gamma))</c>, where <c>theta</c> is the ATM total variance
    /// of the expiry.
    /// </summary>
    /// <remarks>
    /// No-arbitrage results used here (all from the paper):
    /// <list type="bullet">
    /// <item>Theorem 4.1 (calendar, iff): <c>theta_t</c> non-decreasing in <c>t</c> and
    /// <c>0 &lt;= d(theta phi)/d theta &lt;= (1 + sqrt(1 - rho^2)) / rho^2 phi</c> for all <c>theta &gt; 0</c>. For the
    /// power law <c>d(theta phi)/d theta = (1 - gamma) phi / (1 + theta)</c>, so the second condition holds for every
    /// <c>gamma</c> in [0, 1].</item>
    /// <item>Theorem 4.2 (butterfly, sufficient): <c>theta phi (1 + |rho|) &lt; 4</c> and
    /// <c>theta phi^2 (1 + |rho|) &lt;= 4</c> for all <c>theta &gt; 0</c>.</item>
    /// <item>Remark 4.4 / eq. (4.5): with the power law and <c>0 &lt; gamma &lt;= 1/2</c>, the surface is free of static
    /// arbitrage provided <c>eta (1 + |rho|) &lt;= 2</c> (and <c>theta_t</c> non-decreasing). Indeed
    /// <c>theta phi = eta (theta/(1+theta))^(1-gamma) &lt; eta</c> and
    /// <c>theta phi^2 = eta^2 (theta/(1+theta))^(1-2 gamma) / (1+theta) &lt;= eta^2</c>.</item>
    /// </list>
    /// </remarks>
    public readonly struct SsviParameters : IEquatable<SsviParameters>
    {
        /// <summary>Creates the parameters after checking their domain.</summary>
        /// <param name="rho">correlation-like skew parameter, in (-1, 1).</param>
        /// <param name="eta">curvature level, positive.</param>
        /// <param name="gamma">curvature decay exponent, in [0, 1] (see the remarks for the no-arbitrage range).</param>
        public SsviParameters(double rho, double eta, double gamma)
        {
            if (!(Math.Abs(rho) < 1.0)) throw new ArgumentOutOfRangeException(nameof(rho), rho, "rho must lie in (-1, 1).");
            if (!(eta > 0.0) || double.IsInfinity(eta)) throw new ArgumentOutOfRangeException(nameof(eta), eta, "eta must be finite and positive.");
            if (!(gamma >= 0.0 && gamma <= 1.0)) throw new ArgumentOutOfRangeException(nameof(gamma), gamma, "gamma must lie in [0, 1].");
            Rho = rho; Eta = eta; Gamma = gamma;
        }

        /// <summary>Skew parameter.</summary>
        public double Rho { get; }

        /// <summary>Curvature level.</summary>
        public double Eta { get; }

        /// <summary>Curvature decay exponent.</summary>
        public double Gamma { get; }

        /// <summary><c>phi(theta) = eta / (theta^gamma (1 + theta)^(1 - gamma))</c>, for <c>theta &gt; 0</c>.</summary>
        public double Phi(double theta) => Eta / (Math.Pow(theta, Gamma) * Math.Pow(1.0 + theta, 1.0 - Gamma));

        /// <summary>The slice of ATM total variance <paramref name="theta"/> (positive).</summary>
        public SsviSlice Slice(double theta)
        {
            if (!(theta > 0.0) || double.IsInfinity(theta))
                throw new ArgumentOutOfRangeException(nameof(theta), theta, "theta must be finite and positive.");
            return new SsviSlice(Rho, theta, Phi(theta));
        }

        /// <summary>Total variance <c>w(k, theta)</c>; 0 when <c>theta = 0</c>.</summary>
        public double TotalVariance(double k, double theta) => theta > 0.0 ? Slice(theta).TotalVariance(k) : 0.0;

        /// <summary>
        /// The closed-form sufficient condition of eq. (4.5): <c>0 &lt; gamma &lt;= 1/2</c> and
        /// <c>eta (1 + |rho|) &lt;= 2</c>. With a non-decreasing ATM total-variance curve the surface is then free of
        /// static arbitrage for every expiry.
        /// </summary>
        public bool SatisfiesPowerLawNoArbitrageCondition => Gamma > 0.0 && Gamma <= 0.5 && Eta * (1.0 + Math.Abs(Rho)) <= 2.0;

        /// <summary>Theorem 4.2's sufficient butterfly conditions at one <paramref name="theta"/>:
        /// <c>theta phi (1 + |rho|) &lt; 4</c> and <c>theta phi^2 (1 + |rho|) &lt;= 4</c>.</summary>
        public bool SatisfiesButterflyConditions(double theta)
        {
            double phi = Phi(theta), r = 1.0 + Math.Abs(Rho);
            return theta * phi * r < 4.0 && theta * phi * phi * r <= 4.0;
        }

        /// <summary>Theorem 4.1's second calendar condition at one <paramref name="theta"/>:
        /// <c>0 &lt;= d(theta phi)/d theta &lt;= (1 + sqrt(1 - rho^2)) / rho^2 phi</c> (no upper bound when rho = 0).</summary>
        public bool SatisfiesCalendarCondition(double theta)
        {
            double phi = Phi(theta);
            double derivative = (1.0 - Gamma) * phi / (1.0 + theta);
            if (derivative < 0.0) return false;
            return Rho == 0.0 || derivative <= (1.0 + Math.Sqrt(1.0 - Rho * Rho)) / (Rho * Rho) * phi;
        }

        /// <inheritdoc />
        public bool Equals(SsviParameters other) => Rho.Equals(other.Rho) && Eta.Equals(other.Eta) && Gamma.Equals(other.Gamma);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is SsviParameters other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Rho, Eta, Gamma);

        /// <inheritdoc />
        public override string ToString() => $"SSVI(rho={Rho:G6}, eta={Eta:G6}, gamma={Gamma:G6})";
    }

    /// <summary>
    /// One SSVI slice: <c>w(k) = theta/2 (1 + rho phi k + sqrt((phi k + rho)^2 + 1 - rho^2))</c> for fixed
    /// <c>theta</c> and <c>phi = phi(theta)</c>; the natural SVI slice <c>(0, 0, rho, theta, phi)</c>.
    /// </summary>
    public readonly struct SsviSlice : ITotalVarianceSlice
    {
        internal SsviSlice(double rho, double theta, double phi) { Rho = rho; Theta = theta; Phi = phi; }

        /// <summary>Skew parameter.</summary>
        public double Rho { get; }

        /// <summary>ATM total variance.</summary>
        public double Theta { get; }

        /// <summary>Curvature <c>phi(theta)</c>.</summary>
        public double Phi { get; }

        /// <inheritdoc />
        public double TotalVariance(double k)
        {
            double x = Phi * k + Rho;
            return 0.5 * Theta * (1.0 + Rho * Phi * k + Math.Sqrt(x * x + 1.0 - Rho * Rho));
        }

        /// <inheritdoc />
        public double TotalVarianceDerivative(double k)
        {
            double x = Phi * k + Rho;
            return 0.5 * Theta * Phi * (Rho + x / Math.Sqrt(x * x + 1.0 - Rho * Rho));
        }

        /// <inheritdoc />
        public double TotalVarianceSecondDerivative(double k)
        {
            double x = Phi * k + Rho;
            double r2 = x * x + 1.0 - Rho * Rho;
            return 0.5 * Theta * Phi * Phi * (1.0 - Rho * Rho) / (r2 * Math.Sqrt(r2));
        }
    }
}
