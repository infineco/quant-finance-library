using System;

namespace InFine.QuantFinance.Volatility
{
    /// <summary>
    /// Raw SVI slice (Gatheral 2004; Gatheral and Jacquier, "Arbitrage-free SVI volatility surfaces", 2014, eq. 3.1):
    /// total implied variance on log-forward moneyness <c>k</c>,
    /// <c>w(k) = a + b (rho (k - m) + sqrt((k - m)^2 + sigma^2))</c>.
    /// </summary>
    /// <remarks>
    /// The parameter domain is that of the paper: <c>b &gt;= 0</c>, <c>|rho| &lt; 1</c>, <c>sigma &gt; 0</c> and
    /// <c>a + b sigma sqrt(1 - rho^2) &gt;= 0</c>, which is the minimum of <c>w</c>, so <c>w &gt;= 0</c> everywhere. The
    /// domain does not exclude arbitrage: a valid slice can still have a negative density (see
    /// <see cref="ArbitrageChecks.CheckButterfly{TSlice}"/>) or wings steeper than Lee's bound
    /// (<see cref="HasAdmissibleWings"/>). The total variance is time-free; the expiry belongs to the surface.
    /// </remarks>
    public readonly struct SviRawParameters : ITotalVarianceSlice, IEquatable<SviRawParameters>
    {
        /// <summary>Creates the slice after checking the parameter domain.</summary>
        /// <exception cref="ArgumentOutOfRangeException">A parameter is not finite or outside the SVI domain.</exception>
        public SviRawParameters(double a, double b, double rho, double m, double sigma)
        {
            if (!double.IsFinite(a)) throw new ArgumentOutOfRangeException(nameof(a), a, "a must be finite.");
            if (!(b >= 0.0) || double.IsInfinity(b)) throw new ArgumentOutOfRangeException(nameof(b), b, "b must be finite and non-negative.");
            if (!(Math.Abs(rho) < 1.0)) throw new ArgumentOutOfRangeException(nameof(rho), rho, "rho must lie in (-1, 1).");
            if (!double.IsFinite(m)) throw new ArgumentOutOfRangeException(nameof(m), m, "m must be finite.");
            if (!(sigma > 0.0) || double.IsInfinity(sigma)) throw new ArgumentOutOfRangeException(nameof(sigma), sigma, "sigma must be finite and positive.");
            double minimum = a + b * sigma * Math.Sqrt(1.0 - rho * rho);
            // Relative slack for parameters that put the minimum exactly at zero (rounding of the square root).
            if (minimum < -1e-14 * (Math.Abs(a) + b * sigma))
                throw new ArgumentOutOfRangeException(nameof(a), a,
                    $"a + b sigma sqrt(1 - rho^2) = {minimum:R} is negative: the total variance would be negative.");
            A = a; B = b; Rho = rho; M = m; Sigma = sigma;
        }

        /// <summary>Overall variance level.</summary>
        public double A { get; }

        /// <summary>Wing steepness, non-negative.</summary>
        public double B { get; }

        /// <summary>Skew (rotation), in (-1, 1).</summary>
        public double Rho { get; }

        /// <summary>Horizontal translation.</summary>
        public double M { get; }

        /// <summary>ATM curvature scale, positive.</summary>
        public double Sigma { get; }

        /// <summary>Minimum total variance, <c>a + b sigma sqrt(1 - rho^2)</c>, reached at
        /// <c>k = m - rho sigma / sqrt(1 - rho^2)</c>.</summary>
        public double MinimumTotalVariance => A + B * Sigma * Math.Sqrt(1.0 - Rho * Rho);

        /// <summary>Asymptotic slope of the left wing, <c>b (1 - rho)</c>.</summary>
        public double LeftWingSlope => B * (1.0 - Rho);

        /// <summary>Asymptotic slope of the right wing, <c>b (1 + rho)</c>.</summary>
        public double RightWingSlope => B * (1.0 + Rho);

        /// <summary>
        /// Lee's moment formula: both asymptotic wing slopes of the total variance are at most 2, i.e.
        /// <c>b (1 + |rho|) &lt;= 2</c>. Necessary for absence of arbitrage; with a strict inequality on the right
        /// wing it also gives the call-price limit <c>d+ -&gt; -infinity</c> that Gatheral-Jacquier's Lemma 2.2 requires.
        /// </summary>
        public bool HasAdmissibleWings => B * (1.0 + Math.Abs(Rho)) <= 2.0;

        /// <inheritdoc />
        public double TotalVariance(double k)
        {
            double x = k - M;
            return A + B * (Rho * x + Math.Sqrt(x * x + Sigma * Sigma));
        }

        /// <inheritdoc />
        public double TotalVarianceDerivative(double k)
        {
            double x = k - M;
            return B * (Rho + x / Math.Sqrt(x * x + Sigma * Sigma));
        }

        /// <inheritdoc />
        public double TotalVarianceSecondDerivative(double k)
        {
            double x = k - M;
            double r2 = x * x + Sigma * Sigma;
            return B * Sigma * Sigma / (r2 * Math.Sqrt(r2));
        }

        /// <inheritdoc />
        public bool Equals(SviRawParameters other) =>
            A.Equals(other.A) && B.Equals(other.B) && Rho.Equals(other.Rho) && M.Equals(other.M) && Sigma.Equals(other.Sigma);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is SviRawParameters other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(A, B, Rho, M, Sigma);

        /// <inheritdoc />
        public override string ToString() => $"SVI(a={A:G6}, b={B:G6}, rho={Rho:G6}, m={M:G6}, sigma={Sigma:G6})";
    }
}
