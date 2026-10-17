using System;
using InFine.QuantFinance.Arithmetic.AutoDiff;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Models.Diffusion;
using InFine.QuantFinance.Payoffs.Equity;
using Xunit;

namespace InFine.QuantFinance.Payoffs.Tests
{
    /// <summary>
    /// Pathwise Monte Carlo Greeks by tangent-mode AD on Philox GBM paths (<see cref="McPaths"/> draws, so path p is the
    /// same in every run): against central finite differences with common random numbers, and against Black-Scholes.
    /// Directions: 0 spot (delta), 1 volatility (vega), 2 rate (rho), 3 dividend yield.
    /// </summary>
    public class PathwiseGreeksTests
    {
        const double S0 = 100.0, K = 100.0, R = 0.03, Q = 0.01, Sigma = 0.25, T = 1.0;

        /// <summary>Mean and standard error of the value and of the four tangents of the premium over the paths.</summary>
        static void Pathwise(int paths, int steps, Func<Dual4[], Dual4> premium, double[] mean, double[] error)
        {
            var normals = new double[steps];
            var path = new Dual4[steps + 1];
            var samples = new double[5][];
            for (int i = 0; i < 5; ++i) samples[i] = new double[paths];
            Dual4 spot = Dual4.Variable(S0, 0), sigma = Dual4.Variable(Sigma, 1);
            Dual4 rate = Dual4.Variable(R, 2), dividend = Dual4.Variable(Q, 3);
            for (int p = 0; p < paths; ++p)
            {
                McPaths.Normals((uint)p, McPaths.NormalStream, normals, steps);
                DualLogNormalMath.Path(spot, rate, dividend, sigma, T, steps, normals, 0, path);
                Dual4 v = premium(path);
                samples[0][p] = v.Value;
                samples[1][p] = v.D0;
                samples[2][p] = v.D1;
                samples[3][p] = v.D2;
                samples[4][p] = v.D3;
            }
            for (int i = 0; i < 5; ++i) McPaths.MeanAndError(samples[i], out mean[i], out error[i]);
        }

        /// <summary>Plain double Monte Carlo price on the same Philox paths (multiplicative GBM of <see cref="McPaths.Gbm"/>).</summary>
        static double Price(int paths, int steps, double spot, double rate, double dividend, double sigma, Func<double[], double, double> premium)
        {
            var normals = new double[steps];
            var path = new double[steps + 1];
            double sum = 0.0;
            for (int p = 0; p < paths; ++p)
            {
                McPaths.Gbm((uint)p, steps, spot, rate, dividend, sigma, T, path, normals);
                sum += premium(path, rate);
            }
            return sum / paths;
        }

        /// <summary>Central differences with common random numbers along spot, volatility, rate and dividend yield.</summary>
        static double[] Bumped(int paths, int steps, Func<double[], double, double> premium, double hSpot, double hParameter)
        {
            return new[]
            {
                (Price(paths, steps, S0 + hSpot, R, Q, Sigma, premium) - Price(paths, steps, S0 - hSpot, R, Q, Sigma, premium)) / (2 * hSpot),
                (Price(paths, steps, S0, R, Q, Sigma + hParameter, premium) - Price(paths, steps, S0, R, Q, Sigma - hParameter, premium)) / (2 * hParameter),
                (Price(paths, steps, S0, R + hParameter, Q, Sigma, premium) - Price(paths, steps, S0, R - hParameter, Q, Sigma, premium)) / (2 * hParameter),
                (Price(paths, steps, S0, R, Q + hParameter, Sigma, premium) - Price(paths, steps, S0, R, Q - hParameter, Sigma, premium)) / (2 * hParameter),
            };
        }

        static void Close(double expected, double actual, double relative, string what)
        {
            double tolerance = relative * Math.Max(1.0, Math.Abs(expected));
            Assert.True(Math.Abs(expected - actual) <= tolerance,
                $"{what}: expected {expected:R}, got {actual:R} (|diff| {Math.Abs(expected - actual):E3} > {tolerance:E3})");
        }

        [Fact]
        public void DualPathValuesAreTheDoublePath()
        {
            const int steps = 12;
            var normals = new double[steps];
            var dual = new Dual4[steps + 1];
            var plain = new double[steps + 1];
            McPaths.Normals(7, McPaths.NormalStream, normals, steps);
            DualLogNormalMath.Path(Dual4.Variable(S0, 0), R, Q, Dual4.Variable(Sigma, 1), T, steps, normals, 0, dual);
            McPaths.Gbm(7, steps, S0, R, Q, Sigma, T, plain, normals);

            double logSpot = Math.Log(S0), dt = T / steps;
            for (int k = 0; k <= steps; ++k)
            {
                Close(plain[k], dual[k].Value, 1e-13, "spot vs multiplicative GBM");
                if (k > 0)
                {
                    logSpot = CorrelatedLogNormalMath.LogStep(logSpot, R, Q, Sigma, dt, Math.Sqrt(dt), normals[k - 1]);
                    Assert.Equal(Math.Exp(logSpot), dual[k].Value); // same operations as the double log-step
                }
                // dS_k/dS_0 = S_k / S_0 exactly for GBM.
                Close(dual[k].Value / S0, dual[k].D0, 1e-14, "dS/dS0");
                Assert.Equal(0.0, dual[k].D2);
            }
        }

        [Fact]
        public void SmoothPayoffPathwiseGreeksEqualCommonRandomNumberFiniteDifferences()
        {
            // Softplus call c ln(1 + exp((S_T - K) / c)): smooth, so central differences with common random numbers converge
            // to the pathwise estimate on the same paths as h -> 0 (truncation O(h^2), rounding O(eps / h)).
            const int paths = 20000, steps = 4;
            const double c = 5.0;
            var mean = new double[5];
            var error = new double[5];
            Pathwise(paths, steps, path =>
                Dual4.Exp(-Dual4.Variable(R, 2) * T) * c * Dual4.Log(1.0 + Dual4.Exp((path[steps] - K) / c)), mean, error);

            double[] fd = Bumped(paths, steps, (path, rate) =>
                Math.Exp(-rate * T) * c * Math.Log(1.0 + Math.Exp((path[steps] - K) / c)), 1e-4, 1e-5);

            Close(fd[0], mean[1], 1e-8, "delta");
            Close(fd[1], mean[2], 1e-8, "vega");
            Close(fd[2], mean[3], 1e-8, "rho");
            Close(fd[3], mean[4], 1e-8, "dividend rho");
        }

        [Fact]
        public void EuropeanPathwiseGreeksMatchBlackScholesWithinThreeStandardErrors()
        {
            const int paths = 200000, steps = 1;
            foreach (int sign in new[] { +1, -1 })
            {
                var mean = new double[5];
                var error = new double[5];
                Pathwise(paths, steps, path => PathwiseGreeks.EuropeanPremium(sign, K, Dual4.Variable(R, 2), T, path, steps), mean, error);

                double price, delta, gamma, vega, theta, rho, dividendRho, vanna, volga, dualDelta;
                BlackScholesKernels.Greeks(sign, S0, K, T, R, Q, Sigma, out price, out delta, out gamma, out vega, out theta,
                    out rho, out dividendRho, out vanna, out volga, out dualDelta);

                Within(price, mean[0], error[0], "price");
                Within(delta, mean[1], error[1], "delta");
                Within(vega, mean[2], error[2], "vega");
                Within(rho, mean[3], error[3], "rho");
                Within(dividendRho, mean[4], error[4], "dividend rho");
            }
        }

        static void Within(double expected, double estimate, double standardError, string what)
        {
            Assert.True(standardError > 0.0, what + ": zero standard error");
            Assert.True(Math.Abs(estimate - expected) <= 3.0 * standardError,
                $"{what}: {estimate:R} vs {expected:R}, {Math.Abs(estimate - expected) / standardError:F2} SE");
        }

        [Fact]
        public void EuropeanCallPathwiseDeltaAndVegaEqualBumpedMonteCarloOnTheSamePaths()
        {
            // The call has a kink, but with common random numbers a small bump only changes the branch of the paths that
            // end within ~h of the strike; none does here, so the estimators agree to the finite-difference truncation.
            const int paths = 50000, steps = 1;
            var mean = new double[5];
            var error = new double[5];
            Pathwise(paths, steps, path => PathwiseGreeks.EuropeanPremium(+1, K, Dual4.Variable(R, 2), T, path, steps), mean, error);
            double[] fd = Bumped(paths, steps, (path, rate) => Math.Exp(-rate * T) * Math.Max(path[steps] - K, 0.0), 1e-4, 1e-6);

            Close(fd[0], mean[1], 1e-8, "delta");
            Close(fd[1], mean[2], 1e-8, "vega");
        }

        [Fact]
        public void AsianPathwiseDeltaAndVegaEqualBumpedMonteCarloOnTheSamePaths()
        {
            const int paths = 50000, steps = 12;
            int[] fixings = McPaths.Range(1, steps);
            foreach (bool geometric in new[] { false, true })
            {
                var mean = new double[5];
                var error = new double[5];
                Pathwise(paths, steps, path => PathwiseGreeks.AsianPremium(+1, geometric, false, K, Dual4.Variable(R, 2), T,
                    path, fixings, steps), mean, error);
                double[] fd = Bumped(paths, steps, (path, rate) =>
                    Math.Exp(-rate * T) * AsianMath.PathPayoff(path, fixings, steps, +1, geometric, false, K), 1e-4, 1e-6);

                Close(fd[0], mean[1], 1e-8, "Asian delta");
                Close(fd[1], mean[2], 1e-8, "Asian vega");
                Assert.True(mean[1] > 0.0 && mean[1] < 1.0);
                Assert.True(mean[2] > 0.0);
            }

            // Geometric average-price Asian: the pathwise delta also matches the closed form within 3 SE.
            var g = new double[5];
            var ge = new double[5];
            Pathwise(paths, steps, path => PathwiseGreeks.AsianPremium(+1, true, false, K, Dual4.Variable(R, 2), T,
                path, fixings, steps), g, ge);
            double h = 1e-4;
            double[] times = McPaths.Dates(steps, T);
            double up = GeometricAsian(S0 + h, times), down = GeometricAsian(S0 - h, times);
            Within((up - down) / (2 * h), g[1], ge[1], "geometric Asian delta vs closed form");
        }

        /// <summary>Discounted geometric average-price call on fixings 1..n, from the log-normal moments of the average.</summary>
        static double GeometricAsian(double spot, double[] times)
        {
            int n = times.Length - 1;
            double mu = 0.0, variance = 0.0;
            for (int i = 1; i <= n; ++i)
            {
                mu += Math.Log(spot) + (R - Q - 0.5 * Sigma * Sigma) * times[i];
                for (int j = 1; j <= n; ++j) variance += Sigma * Sigma * Math.Min(times[i], times[j]);
            }
            mu /= n;
            variance /= (double)n * n;
            double forward = Math.Exp(mu + 0.5 * variance);
            return Black76Kernels.Price(+1, forward, K, Math.Exp(-R * T), 1.0, Math.Sqrt(variance));
        }

        [Fact]
        public void AtTheKinkThePathwiseDerivativeIsThatOfTheExercisedBranch()
        {
            // Zero volatility and zero carry: the path stays at S_0 (to the rounding of exp(ln S_0)); the strike is set to the
            // simulated S_T so that the payoff sits exactly on its kink.
            const int steps = 3;
            var normals = new double[] { 0.3, -1.2, 0.8 };
            var path = new Dual4[steps + 1];
            DualLogNormalMath.Path(Dual4.Variable(S0, 0), 0.0, 0.0, Dual4.Variable(0.0, 1), T, steps, normals, 0, path);
            double strike = path[steps].Value;
            Close(S0, strike, 1e-14, "flat path");

            Dual4 call = PathwiseGreeks.EuropeanPremium(+1, strike, 0.0, T, path, steps);
            Dual4 put = PathwiseGreeks.EuropeanPremium(-1, strike, 0.0, T, path, steps);
            Assert.Equal(0.0, call.Value);
            Assert.Equal(0.0, put.Value);
            // max(x, 0) at x = 0 keeps x: call delta +S_T/S_0 (right derivative in S), put delta -S_T/S_0 (left derivative).
            Close(strike / S0, call.D0, 1e-14, "call delta at the kink");
            Close(-strike / S0, put.D0, 1e-14, "put delta at the kink");
            // Vega at sigma = 0 along this path: dS_T/dsigma = S_T (sqrt(dt) sum z - sigma T) = S_T sqrt(dt) sum z.
            double dS = strike * Math.Sqrt(T / steps) * (0.3 - 1.2 + 0.8);
            Close(dS, call.D1, 1e-13, "call vega at the kink");
            Close(-dS, put.D1, 1e-13, "put vega at the kink");

            // Asian average-price call with the strike on the average: same convention.
            int[] fixings = McPaths.Range(1, steps);
            double average = AsianMath.PathPayoff(path, fixings, steps, +1, false, false, 0.0).Value;
            Dual4 asian = PathwiseGreeks.AsianPremium(+1, false, false, average, 0.0, T, path, fixings, steps);
            Assert.Equal(0.0, asian.Value);
            Close(average / S0, asian.D0, 1e-14, "Asian delta at the kink");

            // The pathwise estimator is unaffected in practice: S_T = K has probability 0 when sigma > 0.
        }
    }
}
