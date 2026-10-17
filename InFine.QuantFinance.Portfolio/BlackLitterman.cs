using System;
using System.Collections.Generic;
using InFine.QuantFinance.Numerics;

namespace InFine.QuantFinance.Portfolio
{
    /// <summary>Which covariance the unconstrained / constrained Black-Litterman weights are computed with.</summary>
    public enum BlackLittermanCovariance
    {
        /// <summary>
        /// The prior covariance <c>Sigma</c>. With no views the weights are exactly the market weights
        /// (<c>(delta Sigma)^-1 Pi = w_mkt</c>), and with views they are <c>w_mkt + (tau/delta) P' (...)</c>, the
        /// He-Litterman "market plus view portfolios" decomposition (Idzorek's weights).
        /// </summary>
        Prior,

        /// <summary>
        /// The posterior predictive covariance <c>Sigma_BL = Sigma + M</c> (He-Litterman 1999, Black-Litterman 1992).
        /// With no views <c>M = tau Sigma</c> and the weights are <c>w_mkt / (1 + tau)</c>: the market portfolio scaled
        /// down by the estimation uncertainty on the equilibrium returns.
        /// </summary>
        Posterior,
    }

    /// <summary>Posterior of the Black-Litterman model for one set of views.</summary>
    public sealed record BlackLittermanPosterior
    {
        /// <summary>Equilibrium returns <c>Pi</c>.</summary>
        public double[] Prior { get; init; } = Array.Empty<double>();

        /// <summary>Posterior mean <c>mu_BL</c> of the expected-return vector.</summary>
        public double[] Returns { get; init; } = Array.Empty<double>();

        /// <summary>
        /// Covariance of the posterior mean <c>M = (tau Sigma - tau Sigma P' (P tau Sigma P' + Omega)^-1 P tau Sigma)</c>,
        /// equal to <c>[(tau Sigma)^-1 + P' Omega^-1 P]^-1</c> when Omega is invertible.
        /// </summary>
        public double[,] MeanCovariance { get; init; } = new double[0, 0];

        /// <summary>Predictive covariance <c>Sigma_BL = Sigma + M</c> (He-Litterman convention).</summary>
        public double[,] Covariance { get; init; } = new double[0, 0];

        /// <summary>View-uncertainty matrix <c>Omega</c> that was used (K x K; a +infinity diagonal entry means the view is ignored).</summary>
        public double[,] Omega { get; init; } = new double[0, 0];
    }

    /// <summary>
    /// Black-Litterman model (Black and Litterman 1992; He and Litterman 1999; Idzorek 2005). Host-side.
    /// </summary>
    /// <remarks>
    /// <para>Notation: <c>Sigma</c> asset covariance, <c>w_mkt</c> market weights, <c>delta</c> risk aversion, <c>tau</c>
    /// prior scaling, <c>Pi = delta Sigma w_mkt</c> equilibrium returns, views <c>P mu = Q + e</c>, <c>e ~ N(0, Omega)</c>.</para>
    /// <para>Posterior mean (textbook): <c>mu_BL = [(tau Sigma)^-1 + P' Omega^-1 P]^-1 [(tau Sigma)^-1 Pi + P' Omega^-1 Q]</c>.
    /// The class computes the algebraically identical, numerically stable form
    /// <c>mu_BL = Pi + tau Sigma P' (P tau Sigma P' + Omega)^-1 (Q - P Pi)</c>, which needs one K x K Cholesky solve,
    /// never inverts the (ill-conditioned) <c>tau Sigma</c>, and tolerates <c>Omega = 0</c> (certain views).
    /// The textbook form is exposed as <see cref="PosteriorTextbook"/> for cross-checking.</para>
    /// <para>Covariance conventions: the posterior of the mean has covariance <c>M</c>; the predictive covariance of
    /// returns is <c>Sigma_BL = Sigma + M</c>. Weights are provided under both (<see cref="BlackLittermanCovariance"/>).</para>
    /// <para>Confidence: (a) <see cref="PosteriorHeLitterman"/> uses <c>Omega = diag(P tau Sigma P')</c>, i.e. each view
    /// is as uncertain as the prior on the view portfolio, giving a 50% blend; (b) user <c>Omega</c> via
    /// <see cref="Posterior"/>; (c) <see cref="PosteriorIdzorek"/> converts percentage confidences into <c>Omega</c>.</para>
    /// <para>Idzorek: with the single view <c>k</c> alone and uncertainty <c>omega_k</c>, the unconstrained weights are
    /// <c>w = w_mkt + (tau / delta) p_k (q_k - p_k Pi) / (a_k + omega_k)</c>, <c>a_k = p_k tau Sigma p_k'</c>. The
    /// full-confidence (<c>omega = 0</c>) tilt is the same vector times <c>1 / a_k</c>, so the tilt achieved at
    /// <c>omega_k</c> is the fraction <c>a_k / (a_k + omega_k)</c> of the 100%-confidence tilt. Idzorek asks for the
    /// fraction to equal the confidence <c>c_k</c>; the one-dimensional root of <c>a_k / (a_k + omega) = c_k</c> is
    /// available in closed form: <c>omega_k = a_k (1 - c_k) / c_k</c>. (Idzorek's paper finds it numerically by
    /// minimising the weight distance; here the weight tilts are collinear so the minimiser is exactly this root.)
    /// Confidence 0 gives <c>omega = +infinity</c>, i.e. the view is dropped.</para>
    /// </remarks>
    public sealed class BlackLitterman
    {
        readonly double[,] sigma;
        readonly double[] marketWeights;
        readonly int n;

        /// <param name="covariance">Asset covariance <c>Sigma</c>, n x n, symmetric positive definite.</param>
        /// <param name="marketWeights">Market-capitalisation weights, length n.</param>
        /// <param name="riskAversion">Market risk aversion <c>delta</c> (&gt; 0).</param>
        /// <param name="tau">Prior scaling <c>tau</c> (&gt; 0); He-Litterman use 0.05.</param>
        public BlackLitterman(double[,] covariance, double[] marketWeights, double riskAversion, double tau)
        {
            ArgumentNullException.ThrowIfNull(covariance);
            ArgumentNullException.ThrowIfNull(marketWeights);
            n = marketWeights.Length;
            if (n == 0) throw new ArgumentException("At least one asset is required.", nameof(marketWeights));
            if (covariance.GetLength(0) != n || covariance.GetLength(1) != n) throw new ArgumentException($"Covariance must be {n}x{n}.", nameof(covariance));
            if (!Matrix.IsFinite(covariance)) throw new ArgumentException("Covariance must be finite.", nameof(covariance));
            foreach (double w in marketWeights)
                if (!double.IsFinite(w)) throw new ArgumentException("Market weights must be finite.", nameof(marketWeights));
            if (!(riskAversion > 0.0) || double.IsInfinity(riskAversion)) throw new ArgumentOutOfRangeException(nameof(riskAversion), "Risk aversion must be positive and finite.");
            if (!(tau > 0.0) || double.IsInfinity(tau)) throw new ArgumentOutOfRangeException(nameof(tau), "tau must be positive and finite.");
            sigma = Matrix.Symmetrize(covariance);
            if (!Cholesky.TryFactor(sigma, out _)) throw new ArgumentException("Covariance must be positive definite.", nameof(covariance));
            this.marketWeights = (double[])marketWeights.Clone();
            RiskAversion = riskAversion;
            Tau = tau;
            double[] pi = Multiply(sigma, marketWeights);
            for (int i = 0; i < n; i++) pi[i] *= riskAversion;
            EquilibriumReturns = pi;
        }

        /// <summary>Number of assets.</summary>
        public int AssetCount => n;

        /// <summary>Risk aversion <c>delta</c>.</summary>
        public double RiskAversion { get; }

        /// <summary>Prior scaling <c>tau</c>.</summary>
        public double Tau { get; }

        /// <summary>Implied equilibrium returns <c>Pi = delta Sigma w_mkt</c>.</summary>
        public double[] EquilibriumReturns { get; }

        /// <summary>Risk aversion implied by the market's Sharpe ratio: <c>delta = (E[r_m] - r_f) / sigma_m^2</c>.</summary>
        public static double ImpliedRiskAversion(double marketExpectedReturn, double riskFreeRate, double marketVariance)
        {
            if (!(marketVariance > 0.0) || double.IsInfinity(marketVariance)) throw new ArgumentOutOfRangeException(nameof(marketVariance), "Market variance must be positive and finite.");
            return (marketExpectedReturn - riskFreeRate) / marketVariance;
        }

        /// <summary>Risk aversion <c>(E[r_m] - r_f) / (w' Sigma w)</c> for the market weights of <paramref name="covariance"/>.</summary>
        public static double ImpliedRiskAversion(double marketExpectedReturn, double riskFreeRate, double[,] covariance, double[] marketWeights)
        {
            double[] sw = Multiply(covariance, marketWeights);
            double v = 0.0;
            for (int i = 0; i < sw.Length; i++) v += marketWeights[i] * sw[i];
            return ImpliedRiskAversion(marketExpectedReturn, riskFreeRate, v);
        }

        /// <summary>Posterior with no views: <c>mu_BL = Pi</c>, <c>M = tau Sigma</c>.</summary>
        public BlackLittermanPosterior PosteriorWithoutViews() => Posterior(new double[0, n], Array.Empty<double>(), new double[0, 0]);

        /// <summary>He-Litterman default: <c>Omega = diag(P tau Sigma P')</c>.</summary>
        public BlackLittermanPosterior PosteriorHeLitterman(double[,] pick, double[] q)
        {
            CheckViews(pick, q);
            return Posterior(pick, q, Diagonal(ViewVariances(pick)));
        }

        /// <summary>User-specified view uncertainty (stable form; entries on the diagonal may be 0 or +infinity).</summary>
        /// <param name="pick">K x n pick matrix (absolute views have a single 1; relative views a +1 and a -1 or weights summing to 0).</param>
        /// <param name="q">View returns, length K.</param>
        /// <param name="omega">K x K symmetric positive semi-definite uncertainty matrix; a +infinity diagonal entry (with zero off-diagonals) drops the view.</param>
        public BlackLittermanPosterior Posterior(double[,] pick, double[] q, double[,] omega)
        {
            CheckViews(pick, q);
            int k = q.Length;
            if (omega.GetLength(0) != k || omega.GetLength(1) != k) throw new ArgumentException($"Omega must be {k}x{k}.", nameof(omega));
            var active = new List<int>();
            for (int i = 0; i < k; i++)
            {
                if (double.IsPositiveInfinity(omega[i, i]))
                {
                    for (int j = 0; j < k; j++)
                        if (j != i && (omega[i, j] != 0.0 || omega[j, i] != 0.0)) throw new ArgumentException("An infinite view uncertainty requires zero covariances.", nameof(omega));
                    continue;
                }
                active.Add(i);
            }
            int m = active.Count;
            double[,] ts = Scale(sigma, Tau);
            double[] pi = EquilibriumReturns;
            double[] mu = (double[])pi.Clone();
            double[,] meanCov = ts;
            if (m > 0)
            {
                var pa = new double[m, n];
                var qa = new double[m];
                var sMat = new double[m, m];
                for (int a = 0; a < m; a++)
                {
                    qa[a] = q[active[a]];
                    for (int j = 0; j < n; j++) pa[a, j] = pick[active[a], j];
                }
                double[,] pt = Matrix.Multiply(pa, ts);                // m x n  (P tau Sigma)
                double[,] ptp = Matrix.Multiply(pt, Matrix.Transpose(pa));
                for (int a = 0; a < m; a++)
                    for (int b = 0; b < m; b++) sMat[a, b] = ptp[a, b] + omega[active[a], active[b]];
                sMat = Matrix.Symmetrize(sMat);
                if (!Cholesky.TryFactor(sMat, out double[,] l))
                    throw new ArgumentException("P tau Sigma P' + Omega is not positive definite (dependent views with zero uncertainty?).", nameof(omega));
                var resid = new double[m, 1];
                double[] ppi = Multiply(pa, pi);
                for (int a = 0; a < m; a++) resid[a, 0] = qa[a] - ppi[a];
                double[,] y = CholeskySolve(l, resid);                  // S^-1 (Q - P Pi)
                double[,] x = CholeskySolve(l, pt);                     // S^-1 P tau Sigma, m x n
                for (int i = 0; i < n; i++)
                    for (int a = 0; a < m; a++) mu[i] += pt[a, i] * y[a, 0];
                meanCov = new double[n, n];
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                    {
                        double s = ts[i, j];
                        for (int a = 0; a < m; a++) s -= pt[a, i] * x[a, j];
                        meanCov[i, j] = s;
                    }
                meanCov = Matrix.Symmetrize(meanCov);
            }
            return Build(mu, meanCov, omega);
        }

        /// <summary>
        /// Textbook form <c>[(tau Sigma)^-1 + P' Omega^-1 P]^-1 [(tau Sigma)^-1 Pi + P' Omega^-1 Q]</c>; requires a
        /// positive definite finite <paramref name="omega"/>. Provided to cross-check the stable form.
        /// </summary>
        public BlackLittermanPosterior PosteriorTextbook(double[,] pick, double[] q, double[,] omega)
        {
            CheckViews(pick, q);
            int k = q.Length;
            if (omega.GetLength(0) != k || omega.GetLength(1) != k) throw new ArgumentException($"Omega must be {k}x{k}.", nameof(omega));
            double[,] tsInv = Inverse(Scale(sigma, Tau));
            var a = (double[,])tsInv.Clone();
            var rhs = Multiply(tsInv, EquilibriumReturns);
            if (k > 0)
            {
                double[,] oInv = Inverse(omega);
                double[,] pt = Matrix.Transpose(pick);
                double[,] ptoi = Matrix.Multiply(pt, oInv);
                double[,] ptoip = Matrix.Multiply(ptoi, pick);
                double[] ptoiq = Multiply(ptoi, q);
                for (int i = 0; i < n; i++)
                {
                    rhs[i] += ptoiq[i];
                    for (int j = 0; j < n; j++) a[i, j] += ptoip[i, j];
                }
            }
            a = Matrix.Symmetrize(a);
            double[,] m = Inverse(a);
            return Build(Multiply(m, rhs), Matrix.Symmetrize(m), omega);
        }

        /// <summary>
        /// Idzorek (2005) percentage confidences: <c>omega_k = a_k (1 - c_k) / c_k</c> with <c>a_k = p_k tau Sigma p_k'</c>
        /// (see the class remarks); confidence 1 gives <c>omega_k = 0</c> and confidence 0 drops the view.
        /// </summary>
        /// <param name="pick">K x n pick matrix.</param>
        /// <param name="q">View returns, length K.</param>
        /// <param name="confidences">Confidence <c>c_k</c> in [0, 1] of each view.</param>
        public BlackLittermanPosterior PosteriorIdzorek(double[,] pick, double[] q, double[] confidences)
        {
            CheckViews(pick, q);
            if (confidences == null || confidences.Length != q.Length) throw new ArgumentException("One confidence per view is required.", nameof(confidences));
            double[] a = ViewVariances(pick);
            var omega = new double[q.Length, q.Length];
            for (int i = 0; i < q.Length; i++)
            {
                double c = confidences[i];
                if (!(c >= 0.0 && c <= 1.0)) throw new ArgumentOutOfRangeException(nameof(confidences), "Confidences must lie in [0, 1].");
                omega[i, i] = c == 0.0 ? double.PositiveInfinity : a[i] * (1.0 - c) / c;
            }
            return Posterior(pick, q, omega);
        }

        /// <summary>Unconstrained mean-variance weights <c>(delta C)^-1 mu_BL</c> with <c>C</c> chosen by <paramref name="covariance"/>.</summary>
        public double[] UnconstrainedWeights(BlackLittermanPosterior posterior, BlackLittermanCovariance covariance = BlackLittermanCovariance.Posterior)
        {
            ArgumentNullException.ThrowIfNull(posterior);
            double[,] c = CovarianceFor(posterior, covariance);
            double[,] l = Cholesky.Factor(Scale(c, RiskAversion));
            var b = new double[n, 1];
            for (int i = 0; i < n; i++) b[i, 0] = posterior.Returns[i];
            double[,] w = CholeskySolve(l, b);
            var res = new double[n];
            for (int i = 0; i < n; i++) res[i] = w[i, 0];
            return res;
        }

        /// <summary>
        /// Constrained weights: the <see cref="MeanVarianceOptimizer"/> maximum-utility portfolio for <c>mu_BL</c> and the chosen
        /// covariance, at this model's risk aversion.
        /// </summary>
        public MeanVarianceResult ConstrainedWeights(BlackLittermanPosterior posterior, PortfolioConstraints? constraints = null,
            BlackLittermanCovariance covariance = BlackLittermanCovariance.Posterior, QuadraticProgramOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(posterior);
            return new MeanVarianceOptimizer(posterior.Returns, CovarianceFor(posterior, covariance), constraints, options).MaximumUtility(RiskAversion);
        }

        double[,] CovarianceFor(BlackLittermanPosterior posterior, BlackLittermanCovariance covariance) =>
            covariance == BlackLittermanCovariance.Prior ? sigma : posterior.Covariance;

        BlackLittermanPosterior Build(double[] mu, double[,] meanCov, double[,] omega)
        {
            var cov = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) cov[i, j] = sigma[i, j] + meanCov[i, j];
            return new BlackLittermanPosterior
            {
                Prior = (double[])EquilibriumReturns.Clone(),
                Returns = mu,
                MeanCovariance = meanCov,
                Covariance = cov,
                Omega = (double[,])omega.Clone(),
            };
        }

        void CheckViews(double[,] pick, double[] q)
        {
            ArgumentNullException.ThrowIfNull(pick);
            ArgumentNullException.ThrowIfNull(q);
            if (pick.GetLength(1) != n || pick.GetLength(0) != q.Length) throw new ArgumentException($"Pick matrix must be {q.Length}x{n}.", nameof(pick));
            if (!Matrix.IsFinite(pick)) throw new ArgumentException("Pick matrix must be finite.", nameof(pick));
            foreach (double v in q)
                if (!double.IsFinite(v)) throw new ArgumentException("View returns must be finite.", nameof(q));
        }

        /// <summary>Diagonal of <c>P tau Sigma P'</c>.</summary>
        double[] ViewVariances(double[,] pick)
        {
            int k = pick.GetLength(0);
            var a = new double[k];
            for (int r = 0; r < k; r++)
            {
                double s = 0.0;
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++) s += pick[r, i] * sigma[i, j] * pick[r, j];
                a[r] = Tau * s;
            }
            return a;
        }

        static double[,] Diagonal(double[] d)
        {
            var m = new double[d.Length, d.Length];
            for (int i = 0; i < d.Length; i++) m[i, i] = d[i];
            return m;
        }

        static double[,] Scale(double[,] a, double s)
        {
            int r = a.GetLength(0), c = a.GetLength(1);
            var res = new double[r, c];
            for (int i = 0; i < r; i++)
                for (int j = 0; j < c; j++) res[i, j] = s * a[i, j];
            return res;
        }

        static double[] Multiply(double[,] a, double[] x)
        {
            int r = a.GetLength(0), c = a.GetLength(1);
            var res = new double[r];
            for (int i = 0; i < r; i++)
            {
                double s = 0.0;
                for (int j = 0; j < c; j++) s += a[i, j] * x[j];
                res[i] = s;
            }
            return res;
        }

        /// <summary>Solves <c>L L' X = B</c>.</summary>
        static double[,] CholeskySolve(double[,] l, double[,] b)
        {
            int m = l.GetLength(0), c = b.GetLength(1);
            var x = new double[m, c];
            for (int col = 0; col < c; col++)
            {
                for (int i = 0; i < m; i++)
                {
                    double s = b[i, col];
                    for (int j = 0; j < i; j++) s -= l[i, j] * x[j, col];
                    x[i, col] = s / l[i, i];
                }
                for (int i = m - 1; i >= 0; i--)
                {
                    double s = x[i, col];
                    for (int j = i + 1; j < m; j++) s -= l[j, i] * x[j, col];
                    x[i, col] = s / l[i, i];
                }
            }
            return x;
        }

        static double[,] Inverse(double[,] a)
        {
            double[,] l = Cholesky.Factor(Matrix.Symmetrize(a));
            return CholeskySolve(l, Matrix.Identity(a.GetLength(0)));
        }
    }
}
