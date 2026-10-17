using System;
using Xunit;

namespace InFine.QuantFinance.Portfolio.Tests
{
    public class BlackLittermanTests
    {
        // He and Litterman (1999), "The Intuition Behind Black-Litterman Model Portfolios", Goldman Sachs Asset
        // Management: Tables 1-2 (correlations, volatilities, market-capitalisation weights); tau = 0.05, delta = 2.5.
        // Data cross-checked against the public replication in Hudson & Thames,
        // https://hudsonthames.org/bayesian-portfolio-optimisation-the-black-litterman-model/
        // Order: Australia, Canada, France, Germany, Japan, UK, USA.
        static readonly double[,] Corr =
        {
            { 1.000, 0.488, 0.478, 0.515, 0.439, 0.512, 0.491 },
            { 0.488, 1.000, 0.664, 0.655, 0.310, 0.608, 0.779 },
            { 0.478, 0.664, 1.000, 0.861, 0.355, 0.783, 0.668 },
            { 0.515, 0.655, 0.861, 1.000, 0.354, 0.777, 0.653 },
            { 0.439, 0.310, 0.355, 0.354, 1.000, 0.405, 0.306 },
            { 0.512, 0.608, 0.783, 0.777, 0.405, 1.000, 0.652 },
            { 0.491, 0.779, 0.668, 0.653, 0.306, 0.652, 1.000 },
        };
        static readonly double[] Vol = { 0.160, 0.203, 0.248, 0.271, 0.210, 0.200, 0.187 };
        static readonly double[] Wmkt = { 0.016, 0.022, 0.052, 0.055, 0.116, 0.124, 0.615 };
        const double Tau = 0.05, Delta = 2.5;

        static double[,] Sigma()
        {
            var s = new double[7, 7];
            for (int i = 0; i < 7; i++)
                for (int j = 0; j < 7; j++) s[i, j] = Corr[i, j] * Vol[i] * Vol[j];
            return s;
        }

        static BlackLitterman Model() => new BlackLitterman(Sigma(), Wmkt, Delta, Tau);

        /// <summary>View 1: Germany outperforms the market-weighted rest of Europe (France, UK) by 5%. View 2: Canada outperforms USA by 3%.</summary>
        static (double[,] P, double[] Q) HeLittermanViews()
        {
            var p = new double[2, 7];
            double e = Wmkt[2] + Wmkt[5];
            p[0, 3] = 1.0; p[0, 2] = -Wmkt[2] / e; p[0, 5] = -Wmkt[5] / e;
            p[1, 1] = 1.0; p[1, 6] = -1.0;
            return (p, new[] { 0.05, 0.03 });
        }

        static double[] Mul(double[,] a, double[] x)
        {
            var r = new double[a.GetLength(0)];
            for (int i = 0; i < r.Length; i++)
                for (int j = 0; j < x.Length; j++) r[i] += a[i, j] * x[j];
            return r;
        }

        static void AssertClose(double[] expected, double[] actual, double tol)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++) Assert.True(Math.Abs(expected[i] - actual[i]) <= tol, $"[{i}] {expected[i]} vs {actual[i]}");
        }

        static void AssertClose(double[,] expected, double[,] actual, double tol)
        {
            for (int i = 0; i < expected.GetLength(0); i++)
                for (int j = 0; j < expected.GetLength(1); j++)
                    Assert.True(Math.Abs(expected[i, j] - actual[i, j]) <= tol, $"[{i},{j}] {expected[i, j]} vs {actual[i, j]}");
        }

        [Fact]
        public void EquilibriumReturnsMatchPublishedTable()
        {
            // He-Litterman Table 3 "equilibrium returns", in percent to one decimal: 3.9 6.9 8.4 9.0 4.3 6.8 7.6.
            double[] printed = { 3.9, 6.9, 8.4, 9.0, 4.3, 6.8, 7.6 };
            double[] pi = Model().EquilibriumReturns;
            for (int i = 0; i < 7; i++) Assert.True(Math.Abs(pi[i] * 100.0 - printed[i]) <= 0.05, $"asset {i}: {pi[i] * 100.0} vs {printed[i]}");
        }

        [Fact]
        public void EquilibriumReturnsAreDeltaSigmaWmkt()
        {
            double[,] s = Sigma();
            double[] expected = Mul(s, Wmkt);
            for (int i = 0; i < 7; i++) expected[i] *= Delta;
            AssertClose(expected, Model().EquilibriumReturns, 1e-15);
        }

        [Fact]
        public void NoViewsGivesPriorAndMarketWeights()
        {
            var m = Model();
            var post = m.PosteriorWithoutViews();
            AssertClose(m.EquilibriumReturns, post.Returns, 0.0);
            // With the prior covariance the reverse optimisation is inverted exactly.
            AssertClose(Wmkt, m.UnconstrainedWeights(post, BlackLittermanCovariance.Prior), 1e-12);
            // With Sigma_BL = (1 + tau) Sigma the weights are the market portfolio scaled by 1 / (1 + tau).
            double[] scaled = new double[7];
            for (int i = 0; i < 7; i++) scaled[i] = Wmkt[i] / (1.0 + Tau);
            AssertClose(scaled, m.UnconstrainedWeights(post, BlackLittermanCovariance.Posterior), 1e-12);
            // A view-less call through the general path (K = 0) is the same.
            AssertClose(m.EquilibriumReturns, m.PosteriorHeLitterman(new double[0, 7], Array.Empty<double>()).Returns, 0.0);
        }

        [Fact]
        public void StableFormEqualsTextbookForm()
        {
            var m = Model();
            var (p, q) = HeLittermanViews();
            var omega = new double[2, 2] { { 0.0004, 0.0001 }, { 0.0001, 0.0009 } };
            var stable = m.Posterior(p, q, omega);
            var text = m.PosteriorTextbook(p, q, omega);
            AssertClose(text.Returns, stable.Returns, 1e-12);
            AssertClose(text.MeanCovariance, stable.MeanCovariance, 1e-12);
            AssertClose(text.Covariance, stable.Covariance, 1e-12);
            // Also for the He-Litterman default Omega.
            var hl = m.PosteriorHeLitterman(p, q);
            var hlText = m.PosteriorTextbook(p, q, hl.Omega);
            AssertClose(hlText.Returns, hl.Returns, 1e-12);
        }

        [Fact]
        public void HeLittermanDefaultMovesViewHalfWayToViewReturn()
        {
            // Omega = P tau Sigma P' for a single view: P mu_BL = P Pi + (Q - P Pi) / 2.
            var m = Model();
            var (p, q) = HeLittermanViews();
            var one = new double[1, 7];
            for (int j = 0; j < 7; j++) one[0, j] = p[0, j];
            var post = m.PosteriorHeLitterman(one, new[] { q[0] });
            double ppi = Mul(one, m.EquilibriumReturns)[0];
            double pmu = Mul(one, post.Returns)[0];
            Assert.Equal(ppi + 0.5 * (q[0] - ppi), pmu, 14);
        }

        [Fact]
        public void HeLittermanWeightsAreMarketPlusViewPortfolios()
        {
            // Under the prior covariance: w* = w_mkt + (tau / delta) P' (P tau Sigma P' + Omega)^-1 (Q - P Pi).
            var m = Model();
            var (p, q) = HeLittermanViews();
            var post = m.PosteriorHeLitterman(p, q);
            double[,] ts = Sigma();
            double[,] pt = new double[2, 7];
            for (int a = 0; a < 2; a++)
                for (int i = 0; i < 7; i++)
                    for (int j = 0; j < 7; j++) pt[a, i] += p[a, j] * Tau * ts[j, i];
            var s = new double[2, 2];
            for (int a = 0; a < 2; a++)
                for (int b = 0; b < 2; b++)
                {
                    for (int j = 0; j < 7; j++) s[a, b] += pt[a, j] * p[b, j];
                    s[a, b] += post.Omega[a, b];
                }
            double[] ppi = Mul(p, m.EquilibriumReturns);
            double[] r = { q[0] - ppi[0], q[1] - ppi[1] };
            double det = s[0, 0] * s[1, 1] - s[0, 1] * s[1, 0];
            double l0 = (s[1, 1] * r[0] - s[0, 1] * r[1]) / det, l1 = (-s[1, 0] * r[0] + s[0, 0] * r[1]) / det;
            var expected = new double[7];
            for (int i = 0; i < 7; i++) expected[i] = Wmkt[i] + Tau / Delta * (p[0, i] * l0 + p[1, i] * l1);
            AssertClose(expected, m.UnconstrainedWeights(post, BlackLittermanCovariance.Prior), 1e-12);
            // Qualitative: Germany is overweighted, France and UK underweighted; Canada up, USA down.
            double[] w = m.UnconstrainedWeights(post, BlackLittermanCovariance.Prior);
            Assert.True(w[3] > Wmkt[3] && w[2] < Wmkt[2] && w[5] < Wmkt[5] && w[1] > Wmkt[1] && w[6] < Wmkt[6]);
        }

        [Fact]
        public void PosteriorCovarianceAddsPositiveSemiDefiniteUncertainty()
        {
            var m = Model();
            var (p, q) = HeLittermanViews();
            var post = m.PosteriorHeLitterman(p, q);
            var noViews = m.PosteriorWithoutViews();
            // Views reduce the estimation uncertainty: tau Sigma - M is PSD, and M is PD.
            Assert.True(InFine.QuantFinance.Numerics.Cholesky.TryFactor(post.MeanCovariance, out _));
            var d = new double[7, 7];
            for (int i = 0; i < 7; i++)
                for (int j = 0; j < 7; j++) d[i, j] = noViews.MeanCovariance[i, j] - post.MeanCovariance[i, j];
            for (int i = 0; i < 7; i++) Assert.True(d[i, i] >= 0.0);
            // Sigma_BL = Sigma + M, no views: (1 + tau) Sigma.
            double[,] s = Sigma();
            for (int i = 0; i < 7; i++)
                for (int j = 0; j < 7; j++) Assert.Equal((1.0 + Tau) * s[i, j], noViews.Covariance[i, j], 15);
        }

        [Fact]
        public void IdzorekFullConfidenceSatisfiesViewsExactly()
        {
            var m = Model();
            var (p, q) = HeLittermanViews();
            var post = m.PosteriorIdzorek(p, q, new[] { 1.0, 1.0 });
            AssertClose(q, Mul(p, post.Returns), 1e-13);
            // Certain views leave no uncertainty along the view portfolios.
            AssertClose(new double[] { 0, 0 }, Mul(p, Mul(post.MeanCovariance, new[] { p[0, 0], p[0, 1], p[0, 2], p[0, 3], p[0, 4], p[0, 5], p[0, 6] })), 1e-13);
        }

        [Fact]
        public void IdzorekZeroConfidenceGivesNoTilt()
        {
            var m = Model();
            var (p, q) = HeLittermanViews();
            var post = m.PosteriorIdzorek(p, q, new[] { 0.0, 0.0 });
            AssertClose(m.EquilibriumReturns, post.Returns, 0.0);
            AssertClose(Wmkt, m.UnconstrainedWeights(post, BlackLittermanCovariance.Prior), 1e-12);
            // One zero-confidence view is simply dropped.
            var mixed = m.PosteriorIdzorek(p, q, new[] { 0.0, 1.0 });
            var only2 = new double[1, 7];
            for (int j = 0; j < 7; j++) only2[0, j] = p[1, j];
            var ref2 = m.PosteriorIdzorek(only2, new[] { q[1] }, new[] { 1.0 });
            AssertClose(ref2.Returns, mixed.Returns, 1e-13);
        }

        [Theory]
        [InlineData(0.25)]
        [InlineData(0.5)]
        [InlineData(0.9)]
        public void IdzorekTiltIsTheStatedFractionOfTheFullConfidenceTilt(double c)
        {
            var m = Model();
            var (p, q) = HeLittermanViews();
            var one = new double[1, 7];
            for (int j = 0; j < 7; j++) one[0, j] = p[0, j];
            double[] w100 = m.UnconstrainedWeights(m.PosteriorIdzorek(one, new[] { q[0] }, new[] { 1.0 }), BlackLittermanCovariance.Prior);
            double[] wc = m.UnconstrainedWeights(m.PosteriorIdzorek(one, new[] { q[0] }, new[] { c }), BlackLittermanCovariance.Prior);
            for (int i = 0; i < 7; i++) Assert.Equal(Wmkt[i] + c * (w100[i] - Wmkt[i]), wc[i], 12);
        }

        [Fact]
        public void InfiniteViewUncertaintyRecoversPrior()
        {
            var m = Model();
            var (p, q) = HeLittermanViews();
            var inf = new double[2, 2] { { double.PositiveInfinity, 0 }, { 0, double.PositiveInfinity } };
            AssertClose(m.EquilibriumReturns, m.Posterior(p, q, inf).Returns, 0.0);
            // And as a limit: a huge finite Omega is indistinguishable from the prior.
            var big = new double[2, 2] { { 1e15, 0 }, { 0, 1e15 } };
            AssertClose(m.EquilibriumReturns, m.Posterior(p, q, big).Returns, 1e-12);
            AssertClose(m.EquilibriumReturns, m.PosteriorTextbook(p, q, big).Returns, 1e-9);
        }

        [Fact]
        public void ConstrainedWeightsHookRespectsLongOnly()
        {
            var m = Model();
            var (p, q) = HeLittermanViews();
            var post = m.PosteriorIdzorek(p, q, new[] { 0.8, 0.8 });
            var res = m.ConstrainedWeights(post, PortfolioConstraints.LongOnly(7));
            Assert.Equal(MeanVarianceStatus.Optimal, res.Status);
            Assert.Equal(1.0, System.Linq.Enumerable.Sum(res.Weights), 9);
            foreach (double w in res.Weights) Assert.True(w >= -1e-9);
            // The unconstrained solution is already long-only and fully invested under the prior covariance, so the
            // constrained optimum under it must coincide.
            var prior = m.ConstrainedWeights(post, PortfolioConstraints.FullyInvested, BlackLittermanCovariance.Prior);
            double[] unc = m.UnconstrainedWeights(post, BlackLittermanCovariance.Prior);
            Assert.Equal(MeanVarianceStatus.Optimal, prior.Status);
            AssertClose(unc, prior.Weights, 1e-6);
        }

        [Fact]
        public void ImpliedRiskAversionFromMarketSharpe()
        {
            Assert.Equal(2.5, BlackLitterman.ImpliedRiskAversion(0.08, 0.02, 0.024), 12);
            double[,] s = Sigma();
            double v = 0.0;
            for (int i = 0; i < 7; i++) for (int j = 0; j < 7; j++) v += Wmkt[i] * s[i, j] * Wmkt[j];
            Assert.Equal(0.06 / v, BlackLitterman.ImpliedRiskAversion(0.08, 0.02, s, Wmkt), 12);
        }

        [Fact]
        public void RejectsBadInput()
        {
            var m = Model();
            Assert.Throws<ArgumentOutOfRangeException>(() => new BlackLitterman(Sigma(), Wmkt, 0.0, Tau));
            Assert.Throws<ArgumentException>(() => m.PosteriorIdzorek(new double[1, 7], new[] { 0.01 }, new[] { 0.5, 0.5 }));
            // Two identical certain views are dependent: P tau Sigma P' is singular.
            var p = new double[2, 7];
            p[0, 0] = 1; p[1, 0] = 1;
            Assert.Throws<ArgumentException>(() => m.Posterior(p, new[] { 0.01, 0.02 }, new double[2, 2]));
        }
    }
}
