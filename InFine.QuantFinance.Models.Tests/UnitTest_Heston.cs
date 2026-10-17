using System;
using System.Numerics;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Models.Diffusion;
using InFine.QuantFinance.TestSupport;
using Xunit;

namespace InFine.QuantFinance.Models.Tests
{
    /// <summary>Heston parameter set (risk-neutral, no dividends).</summary>
    struct HestonParameters
    {
        public double S0, R, T, V0, Kappa, Theta, Xi, Rho;

        /// <summary>
        /// Fang &amp; Oosterlee (2008), "A novel pricing method for European options based on Fourier-cosine
        /// series expansions", SIAM J. Sci. Comput. 31(2), section 5.3 (Heston test): the Feller condition is
        /// violated (2 kappa theta = 0.126 &lt; xi^2 = 0.331). Published reference ATM call (K = 100): 5.785155450.
        /// </summary>
        public static HestonParameters FangOosterlee => new HestonParameters
        {
            S0 = 100.0, R = 0.0, T = 1.0, V0 = 0.0175, Kappa = 1.5768, Theta = 0.0398, Xi = 0.5751, Rho = -0.5711,
        };

        public const double FangOosterleeAtmCall = 5.785155450;
    }

    /// <summary>
    /// Test-only semi-analytic Heston pricer: Gil-Pelaez inversion of the characteristic function written in the
    /// "little Heston trap" form of Albrecher, Mayer, Schoutens, Tistaert (2007), which has no branch-cut
    /// discontinuity in the complex logarithm. Integrated with a midpoint rule on a truncated domain.
    /// </summary>
    static class HestonCharacteristicFunctionPricer
    {
        const double UMax = 400.0;
        const int Points = 80000;

        /// <summary>phi(u) = E[exp(i u ln S_T)] for complex u.</summary>
        static Complex Phi(Complex u, HestonParameters p)
        {
            Complex i = Complex.ImaginaryOne;
            double xi2 = p.Xi * p.Xi;
            Complex beta = p.Kappa - p.Rho * p.Xi * i * u;
            Complex d = Complex.Sqrt(beta * beta + xi2 * (i * u + u * u));
            Complex g = (beta - d) / (beta + d);
            Complex edt = Complex.Exp(-d * p.T);
            Complex c = p.Kappa * p.Theta / xi2 * ((beta - d) * p.T - 2.0 * Complex.Log((1.0 - g * edt) / (1.0 - g)));
            Complex dd = (beta - d) / xi2 * (1.0 - edt) / (1.0 - g * edt);
            return Complex.Exp(i * u * (Math.Log(p.S0) + p.R * p.T) + c + dd * p.V0);
        }

        public static double Call(HestonParameters p, double strike)
        {
            Complex i = Complex.ImaginaryOne;
            double k = Math.Log(strike);
            double forward = p.S0 * Math.Exp(p.R * p.T); // phi(-i)
            double h = UMax / Points;
            double i1 = 0.0, i2 = 0.0;
            for (int n = 0; n < Points; ++n)
            {
                double u = (n + 0.5) * h;
                Complex e = Complex.Exp(-i * u * k) / (i * u);
                i1 += (e * Phi(u - i, p) / forward).Real;
                i2 += (e * Phi(u, p)).Real;
            }
            double p1 = 0.5 + i1 * h / Math.PI;
            double p2 = 0.5 + i2 * h / Math.PI;
            return p.S0 * p1 - strike * Math.Exp(-p.R * p.T) * p2;
        }

        public static double Put(HestonParameters p, double strike)
        {
            // put-call parity
            return Call(p, strike) - p.S0 + strike * Math.Exp(-p.R * p.T);
        }
    }

    /// <summary>Seeded Box-Muller standard normal pairs over System.Random.</summary>
    sealed class BoxMuller
    {
        readonly System.Random _rng;

        public BoxMuller(int seed) { _rng = new System.Random(seed); }

        public void NextPair(out double z1, out double z2)
        {
            double u1 = 1.0 - _rng.NextDouble(); // (0, 1]
            double u2 = _rng.NextDouble();
            double r = Math.Sqrt(-2.0 * Math.Log(u1));
            z1 = r * Math.Cos(2.0 * Math.PI * u2);
            z2 = r * Math.Sin(2.0 * Math.PI * u2);
        }
    }

    public class UnitTest_HestonStep
    {
        /// <summary>
        /// Discounted European payoff by Monte Carlo with <see cref="HestonStep"/>: full-truncation Euler variance,
        /// log-Euler spot. Returns the price estimate and its standard error.
        /// </summary>
        static (double price, double stdErr) MonteCarlo(HestonParameters p, double strike, bool isCall, int paths, int steps, int seed)
        {
            var normals = new BoxMuller(seed);
            double dt = p.T / steps, sqrtDt = Math.Sqrt(dt);
            double lnS0 = Math.Log(p.S0), discount = Math.Exp(-p.R * p.T);
            double sum = 0.0, sumSq = 0.0;
            for (int path = 0; path < paths; ++path)
            {
                double lnS = lnS0, v = p.V0;
                for (int t = 0; t < steps; ++t)
                {
                    normals.NextPair(out double zS, out double zPerp);
                    double zV = HestonStep.CorrelatedDriver(zS, zPerp, p.Rho);
                    lnS = HestonStep.LogSpotEuler(lnS, v, p.R, dt, sqrtDt, zS); // uses v_t: update spot first
                    v = HestonStep.FullTruncationEuler(v, p.Kappa, p.Theta, p.Xi, dt, sqrtDt, zV);
                }
                double s = Math.Exp(lnS);
                double payoff = discount * (isCall ? Math.Max(s - strike, 0.0) : Math.Max(strike - s, 0.0));
                sum += payoff;
                sumSq += payoff * payoff;
            }
            double mean = sum / paths;
            double variance = (sumSq / paths - mean * mean) * paths / (paths - 1.0);
            return (mean, Math.Sqrt(variance / paths));
        }

        [Fact]
        public void ReferencePricer_MatchesPublishedFangOosterleeValue()
        {
            double price = HestonCharacteristicFunctionPricer.Call(HestonParameters.FangOosterlee, 100.0);
            Assert.Equal(HestonParameters.FangOosterleeAtmCall, price, 1e-6);
        }

        [Fact]
        public void ReferencePricer_TendsToBlackScholesWhenVarianceIsDeterministic()
        {
            // xi -> 0 with v0 = theta: constant variance 0.04, i.e. Black-Scholes with sigma = 0.2.
            // ATM, r = 0, T = 1: C = S (2 N(0.1) - 1) = 7.965567455405804.
            var p = new HestonParameters { S0 = 100.0, R = 0.0, T = 1.0, V0 = 0.04, Kappa = 2.0, Theta = 0.04, Xi = 1e-3, Rho = 0.0 };
            Assert.Equal(7.965567455405804, HestonCharacteristicFunctionPricer.Call(p, 100.0), 1e-4);
        }

        [Fact]
        public void CorrelatedDriver_HasUnitVarianceAndRequestedCorrelation()
        {
            const int n = 200000;
            const double rho = -0.7;
            var normals = new BoxMuller(7);
            double sxy = 0.0, sxx = 0.0, syy = 0.0;
            for (int k = 0; k < n; ++k)
            {
                normals.NextPair(out double zS, out double zPerp);
                double zV = HestonStep.CorrelatedDriver(zS, zPerp, rho);
                sxy += zS * zV; sxx += zS * zS; syy += zV * zV;
            }
            // standard error of the sample correlation ~ (1 - rho^2) / sqrt(n) = 1.1e-3; of the variance ~ sqrt(2/n) = 3.2e-3
            Assert.Equal(rho, sxy / Math.Sqrt(sxx * syy), 5e-3);
            Assert.Equal(1.0, syy / n, 1.5e-2);
            Assert.Equal(1.0, HestonStep.CorrelatedDriver(0.0, 1.0, 0.0));
            Assert.Equal(-2.0, HestonStep.CorrelatedDriver(2.0, 123.0, -1.0));
        }

        [Fact]
        public void FullTruncation_NegativeVariance_UsesZeroInDriftAndDiffusion()
        {
            const double v = -0.01, kappa = 1.5, theta = 0.04, xi = 2.0, dt = 0.01;
            double expected = v + kappa * theta * dt;
            foreach (double z in new[] { -1e6, -3.0, 0.0, 3.0, 1e6 })
                Assert.Equal(expected, HestonStep.FullTruncationEuler(v, kappa, theta, xi, dt, Math.Sqrt(dt), z), 1e-17);

            Assert.Equal(0.0, HestonStep.Volatility(v));
            // spot step with negative raw variance: pure drift r dt, no diffusion
            Assert.Equal(1.0 + 0.03 * dt, HestonStep.LogSpotEuler(1.0, v, 0.03, dt, Math.Sqrt(dt), 5.0), 1e-15);
        }

        [Fact]
        public void FullTruncation_DiffusionTermScalesWithSqrtDt()
        {
            // one step from v = theta (zero drift): increment = xi * sqrt(v) * sqrt(dt) * z
            const double v = 0.04, xi = 0.5, dt = 1.0 / 365.0;
            double next = HestonStep.FullTruncationEuler(v, 2.0, v, xi, dt, Math.Sqrt(dt), 1.0);
            Assert.Equal(xi * 0.2 * Math.Sqrt(dt), next - v, 1e-15);
        }

        [Fact]
        public void FullTruncation_ExtremeParameters_NeverProduceNaN()
        {
            // Feller badly violated: 2 kappa theta = 0.04 << xi^2 = 9
            var p = new HestonParameters { S0 = 100.0, R = 0.05, T = 5.0, V0 = 0.04, Kappa = 0.5, Theta = 0.04, Xi = 3.0, Rho = -0.9 };
            const int paths = 2000, steps = 500;
            double dt = p.T / steps, sqrtDt = Math.Sqrt(dt);
            var normals = new BoxMuller(11);
            int negatives = 0;
            for (int path = 0; path < paths; ++path)
            {
                double lnS = Math.Log(p.S0), v = p.V0;
                for (int t = 0; t < steps; ++t)
                {
                    normals.NextPair(out double zS, out double zPerp);
                    double zV = HestonStep.CorrelatedDriver(zS, zPerp, p.Rho);
                    lnS = HestonStep.LogSpotEuler(lnS, v, p.R, dt, sqrtDt, zS);
                    v = HestonStep.FullTruncationEuler(v, p.Kappa, p.Theta, p.Xi, dt, sqrtDt, zV);
                    double sigma = HestonStep.Volatility(v);
                    Assert.True(double.IsFinite(v), "variance");
                    Assert.True(double.IsFinite(lnS), "log-spot");
                    Assert.True(double.IsFinite(sigma) && sigma >= 0.0, "volatility");
                    if (v < 0.0) ++negatives;
                }
            }
            // the scenario must actually exercise the truncation
            Assert.True(negatives > 0);
        }

        [Fact]
        public void ZeroVolOfVol_ReducesToDeterministicMeanReversion()
        {
            const double kappa = 2.0, theta = 0.04, v0 = 0.09, T = 1.0;
            double exact = theta + (v0 - theta) * Math.Exp(-kappa * T);

            double Error(int steps)
            {
                double dt = T / steps, v = v0;
                var normals = new BoxMuller(3);
                for (int t = 0; t < steps; ++t)
                {
                    normals.NextPair(out double z, out _); // must have no effect with xi = 0
                    v = HestonStep.FullTruncationEuler(v, kappa, theta, 0.0, dt, Math.Sqrt(dt), z);
                }
                return v - exact;
            }

            // Euler on a linear ODE: v_n - theta = (v0 - theta)(1 - kappa dt)^n, error ~ (v0 - theta) e^{-kappa T} kappa^2 T dt / 2
            double e1000 = Error(1000), e2000 = Error(2000);
            Assert.True(Math.Abs(e1000) < 2e-5, $"error {e1000}");
            // first-order convergence
            Assert.InRange(e1000 / e2000, 1.9, 2.1);
        }

        [Fact]
        public void MonteCarlo_EuropeanCall_MatchesCharacteristicFunctionPrice()
        {
            var p = HestonParameters.FangOosterlee;
            var (price, stdErr) = MonteCarlo(p, 100.0, isCall: true, paths: 100000, steps: 100, seed: 1);
            double reference = HestonParameters.FangOosterleeAtmCall;
            Assert.True(Math.Abs(price - reference) < 3.0 * stdErr, $"MC {price} +/- {stdErr}, reference {reference}");
        }

        [Fact]
        public void NegativeCorrelation_RaisesOutOfTheMoneyPuts()
        {
            var negative = new HestonParameters { S0 = 100.0, R = 0.02, T = 1.0, V0 = 0.04, Kappa = 2.0, Theta = 0.04, Xi = 0.5, Rho = -0.7 };
            var symmetric = negative; symmetric.Rho = 0.0;
            const double strike = 80.0;

            double cfNegative = HestonCharacteristicFunctionPricer.Put(negative, strike);
            double cfSymmetric = HestonCharacteristicFunctionPricer.Put(symmetric, strike);
            Assert.True(cfNegative > cfSymmetric, $"CF: {cfNegative} vs {cfSymmetric}");

            // same seed (common random numbers): only the correlation differs
            var (mcNegative, seNegative) = MonteCarlo(negative, strike, isCall: false, paths: 50000, steps: 100, seed: 5);
            var (mcSymmetric, _) = MonteCarlo(symmetric, strike, isCall: false, paths: 50000, steps: 100, seed: 5);
            Assert.True(Math.Abs(mcNegative - cfNegative) < 3.0 * seNegative, $"MC {mcNegative} +/- {seNegative}, reference {cfNegative}");
            Assert.True(mcNegative > mcSymmetric, $"MC: {mcNegative} vs {mcSymmetric}");
        }
    }
}
