using System;
using InFine.QuantFinance.Arithmetic.AutoDiff;
using InFine.QuantFinance.Models.Diffusion;
using InFine.QuantFinance.Payoffs.Equity;
using Xunit;

namespace InFine.QuantFinance.Payoffs.Tests
{
    /// <summary>
    /// Adjoint of the Heston full-truncation Euler scheme (<see cref="HestonAdjoint"/>) on Philox draws: against a dual-number
    /// (tangent) implementation of the same scheme with each input seeded in turn, including at the v^+ kink, and against
    /// central bump-and-revalue with common random numbers.
    /// </summary>
    public class HestonAdjointTests
    {
        delegate double PayoffAdjoint(double discountFactor, double[] logPath, double[] logSpotAdjoint);

        sealed class Heston
        {
            public double Spot = 100.0, V0, Kappa, Theta, Xi, Rho, Rate;
            public double[] Dt;
            public int Steps => Dt.Length;

            /// <summary>Fang and Oosterlee (2008) set: 2 kappa theta = 0.126 &lt; xi^2 = 0.331, the variance often truncates.</summary>
            public static Heston FellerViolated(int steps) => new Heston
            {
                V0 = 0.0175, Kappa = 1.5768, Theta = 0.0398, Xi = 0.5751, Rho = -0.5711, Rate = 0.02, Dt = Uniform(steps, 1.0),
            };

            /// <summary>2 kappa theta = 0.16 &gt; xi^2 = 0.09, though the Euler variance still truncates now and then.</summary>
            public static Heston FellerSatisfied(int steps) => new Heston
            {
                V0 = 0.04, Kappa = 2.0, Theta = 0.04, Xi = 0.3, Rho = -0.7, Rate = 0.03, Dt = Uniform(steps, 1.0),
            };

            /// <summary>2 kappa theta = 0.24, 24 times xi^2 = 0.01: the variance stays far from 0 on every path used.</summary>
            public static Heston FarFromZero(int steps) => new Heston
            {
                V0 = 0.04, Kappa = 3.0, Theta = 0.04, Xi = 0.1, Rho = -0.7, Rate = 0.03, Dt = Uniform(steps, 1.0),
            };

            static double[] Uniform(int steps, double maturity)
            {
                var dt = new double[steps];
                for (int k = 0; k < steps; ++k) dt[k] = maturity / steps;
                return dt;
            }

            public double[] Inputs => new[] { Spot, V0, Kappa, Theta, Xi, Rho, Rate };

            public Heston With(int input, double value)
            {
                var x = Inputs;
                x[input] = value;
                return new Heston { Spot = x[0], V0 = x[1], Kappa = x[2], Theta = x[3], Xi = x[4], Rho = x[5], Rate = x[6], Dt = Dt };
            }
        }

        static double AdjointPath(Heston m, double[] z, PayoffAdjoint payoff, double[] gradient, double[] variance)
        {
            int n = m.Steps;
            var logPath = new double[n + 1];
            var adjoint = new double[n + 1];
            Array.Clear(gradient, 0, gradient.Length);
            double df = HestonAdjoint.Forward(m.Spot, m.V0, m.Kappa, m.Theta, m.Xi, m.Rho, m.Rate, m.Dt, n, z, 0, logPath, 0, variance, 0);
            double premium = payoff(df, logPath, adjoint);
            HestonAdjoint.Reverse(m.Spot, m.Kappa, m.Theta, m.Xi, m.Rho, m.Dt, n, z, 0, variance, 0, premium, adjoint, 0, gradient, 0);
            return premium;
        }

        /// <summary>
        /// Tangent reference: the scheme of <see cref="HestonStep"/> written on <see cref="Dual"/> (same operations, so the
        /// values are bitwise those of the double scheme), one pass per input. <c>Dual.Max(0.0, v)</c> keeps its first
        /// argument on a tie, so at v = 0 the truncated variance is a constant: the subgradient the adjoint uses.
        /// </summary>
        static double TangentPath(Heston m, double[] z, Func<Dual[], Dual> payoff, double[] tangent)
        {
            int n = m.Steps;
            var logPath = new Dual[n + 1];
            double value = double.NaN;
            for (int input = 0; input < HestonAdjoint.GradientLength; ++input)
            {
                double[] x = m.Inputs;
                Dual Seed(int i) => i == input ? Dual.Variable(x[i]) : Dual.Constant(x[i]);
                Dual spot = Seed(HestonAdjoint.SpotIndex), v = Seed(HestonAdjoint.InitialVarianceIndex);
                Dual kappa = Seed(HestonAdjoint.KappaIndex), theta = Seed(HestonAdjoint.ThetaIndex), xi = Seed(HestonAdjoint.XiIndex);
                Dual rho = Seed(HestonAdjoint.CorrelationIndex), rate = Seed(HestonAdjoint.RateIndex);
                Dual logSpot = Dual.Log(spot);
                double maturity = 0.0;
                logPath[0] = logSpot;
                for (int k = 0; k < n; ++k)
                {
                    double h = m.Dt[k], sqrtH = Math.Sqrt(h), zSpot = z[2 * k];
                    Dual zVariance = rho * zSpot + Dual.Sqrt(1.0 - rho * rho) * z[2 * k + 1];
                    Dual vPlus = Dual.Max(0.0, v);
                    Dual volatility = Dual.Sqrt(vPlus);
                    logSpot = logSpot + (rate - 0.5 * vPlus) * h + volatility * sqrtH * zSpot;
                    v = v + kappa * (theta - vPlus) * h + xi * volatility * sqrtH * zVariance;
                    logPath[k + 1] = logSpot;
                    maturity += h;
                }
                Dual premium = Dual.Exp(-rate * maturity) * payoff(logPath);
                tangent[input] = premium.Tangent;
                value = premium.Value;
            }
            return value;
        }

        readonly Xunit.Abstractions.ITestOutputHelper _output;
        double _worstDifference;

        public HestonAdjointTests(Xunit.Abstractions.ITestOutputHelper output) { _output = output; }

        /// <summary>|expected - actual| within relative * max(1, |expected|); the worst ratio is logged.</summary>
        void Close(double expected, double actual, double relative, string what)
        {
            double scale = Math.Max(1.0, Math.Abs(expected));
            double tolerance = relative * scale;
            _worstDifference = Math.Max(_worstDifference, Math.Abs(expected - actual) / scale);
            Assert.True(Math.Abs(expected - actual) <= tolerance,
                $"{what}: expected {expected:R}, got {actual:R} (|diff| {Math.Abs(expected - actual):E3} > {tolerance:E3})");
        }

        [Fact]
        public void HestonAdjointEqualsTangentOnEveryPathForEveryInput()
        {
            const int steps = 24;
            const double strike = 100.0;
            int[] fixings = { 6, 12, 18, 24 };
            var cases = new (string name, PayoffAdjoint adjoint, Func<Dual[], Dual> tangent)[]
            {
                ("European call", (df, x, a) => AdjointGreeks.European(+1, strike, df, x, 0, steps, a, 0),
                    x => Dual.Max(Dual.Exp(x[steps]) - strike, 0.0)),
                ("European put", (df, x, a) => AdjointGreeks.European(-1, strike, df, x, 0, steps, a, 0),
                    x => Dual.Max(-(Dual.Exp(x[steps]) - strike), 0.0)),
                ("Asian call", (df, x, a) => AdjointGreeks.ArithmeticAsian(+1, strike, fixings, fixings.Length, df, x, 0, steps, a, 0),
                    x =>
                    {
                        Dual sum = 0.0;
                        foreach (int k in fixings) sum += Dual.Exp(x[k]);
                        return Dual.Max(sum / fixings.Length - strike, 0.0);
                    }),
            };
            var z = new double[2 * steps];
            var gradient = new double[HestonAdjoint.GradientLength];
            var tangent = new double[HestonAdjoint.GradientLength];
            var variance = new double[steps + 1];
            foreach (Heston m in new[] { Heston.FellerViolated(steps), Heston.FellerSatisfied(steps) })
                foreach (var c in cases)
                {
                    int truncated = 0;
                    for (int p = 0; p < 200; ++p)
                    {
                        McPaths.Normals((uint)p, McPaths.NormalStream, z, z.Length);
                        double premium = AdjointPath(m, z, c.adjoint, gradient, variance);
                        double value = TangentPath(m, z, c.tangent, tangent);
                        Assert.Equal(value, premium);
                        for (int i = 0; i < HestonAdjoint.GradientLength; ++i)
                            Close(tangent[i], gradient[i], 1e-10, $"{c.name} path {p} input {i}");
                        for (int k = 0; k < steps; ++k)
                            if (variance[k] <= 0.0) ++truncated;
                    }
                    _output.WriteLine($"{c.name}, xi {m.Xi}: {truncated} truncated steps out of {200 * steps}");
                    if (m.Kappa * m.Theta * 2.0 < m.Xi * m.Xi)
                        Assert.True(truncated > 100, $"{c.name}: only {truncated} truncated steps"); // the kink is exercised
                }
            _output.WriteLine($"worst scaled difference {_worstDifference:E2}");
        }

        [Fact]
        public void AtZeroInitialVarianceTheAdjointTakesTheZeroSubgradientLikeTheTangent()
        {
            // v_0 = 0 sits on the kink of v^+: the first step sees a constant zero variance (no diffusion), and both the
            // adjoint and Dual.Max(0.0, v) differentiate it as such, so every sensitivity stays finite.
            const int steps = 12;
            Heston m = Heston.FellerSatisfied(steps).With(HestonAdjoint.InitialVarianceIndex, 0.0);
            var z = new double[2 * steps];
            var gradient = new double[HestonAdjoint.GradientLength];
            var tangent = new double[HestonAdjoint.GradientLength];
            var variance = new double[steps + 1];
            for (int p = 0; p < 50; ++p)
            {
                McPaths.Normals((uint)p, McPaths.NormalStream, z, z.Length);
                AdjointPath(m, z, (df, x, a) => AdjointGreeks.European(+1, 100.0, df, x, 0, steps, a, 0), gradient, variance);
                TangentPath(m, z, x => Dual.Max(Dual.Exp(x[steps]) - 100.0, 0.0), tangent);
                for (int i = 0; i < HestonAdjoint.GradientLength; ++i)
                {
                    Assert.False(double.IsNaN(gradient[i]) || double.IsInfinity(gradient[i]), $"path {p} input {i}: {gradient[i]}");
                    Close(tangent[i], gradient[i], 1e-10, $"path {p} input {i}");
                }
            }
            _output.WriteLine($"worst scaled difference {_worstDifference:E2}");
        }

        [Fact]
        public void HestonAdjointEqualsCommonRandomNumberBumpsOnASmoothPayoff()
        {
            // Softplus call on S_T with the variance kept far from 0 (checked below): the scheme is then smooth along every
            // path, so central differences with common random numbers converge to the adjoint. Near v = 0 they would not:
            // the third derivative of sqrt(v) blows up like v^(-5/2) and a bump can switch the truncation.
            const int steps = 24, paths = 20000;
            const double strike = 100.0, c = 5.0;
            Heston m = Heston.FarFromZero(steps);
            PayoffAdjoint softplus = (df, x, a) =>
            {
                Array.Clear(a, 0, a.Length);
                double s = Math.Exp(x[steps]);
                a[steps] = df * s / (1.0 + Math.Exp(-(s - strike) / c));
                return df * c * Math.Log(1.0 + Math.Exp((s - strike) / c));
            };
            var normals = new double[paths][];
            var gradient = new double[HestonAdjoint.GradientLength];
            var adjoint = new double[HestonAdjoint.GradientLength];
            var variance = new double[steps + 1];
            double lowest = double.PositiveInfinity;
            for (int p = 0; p < paths; ++p)
            {
                normals[p] = new double[2 * steps];
                McPaths.Normals((uint)p, McPaths.NormalStream, normals[p], 2 * steps);
                AdjointPath(m, normals[p], softplus, gradient, variance);
                for (int i = 0; i < HestonAdjoint.GradientLength; ++i) adjoint[i] += gradient[i] / paths;
                for (int k = 0; k <= steps; ++k) lowest = Math.Min(lowest, variance[k]);
            }
            _output.WriteLine($"lowest variance {lowest:E3}");
            Assert.True(lowest > 5e-3, $"lowest variance {lowest:E3}");

            double[] x0 = m.Inputs;
            var logPath = new double[steps + 1];
            var scratch = new double[steps + 1];
            for (int i = 0; i < HestonAdjoint.GradientLength; ++i)
            {
                double h = i == HestonAdjoint.SpotIndex ? 1e-3 : 1e-6;
                Heston up = m.With(i, x0[i] + h), down = m.With(i, x0[i] - h);
                double sum = 0.0;
                for (int p = 0; p < paths; ++p)
                    sum += Price(up, normals[p], softplus, logPath, scratch) - Price(down, normals[p], softplus, logPath, scratch);
                Close(sum / paths / (2.0 * h), adjoint[i], 1e-7, $"input {i}");
            }
            _output.WriteLine($"worst scaled difference {_worstDifference:E2}");
        }

        /// <summary>Discounted payoff of a path by the double scheme of <see cref="HestonStep"/>.</summary>
        static double Price(Heston m, double[] z, PayoffAdjoint payoff, double[] logPath, double[] scratch)
        {
            int n = m.Steps;
            double logSpot = Math.Log(m.Spot), v = m.V0, maturity = 0.0;
            logPath[0] = logSpot;
            for (int k = 0; k < n; ++k)
            {
                double h = m.Dt[k], sqrtH = Math.Sqrt(h);
                double zVariance = HestonStep.CorrelatedDriver(z[2 * k], z[2 * k + 1], m.Rho);
                logSpot = HestonStep.LogSpotEuler(logSpot, v, m.Rate, h, sqrtH, z[2 * k]);
                v = HestonStep.FullTruncationEuler(v, m.Kappa, m.Theta, m.Xi, h, sqrtH, zVariance);
                logPath[k + 1] = logSpot;
                maturity += h;
            }
            return payoff(Math.Exp(-m.Rate * maturity), logPath, scratch);
        }

        [Fact]
        public void HostDriverMatchesThePerPathAdjointsAndValidatesItsInputs()
        {
            const int steps = 12, paths = 400;
            Heston m = Heston.FellerViolated(steps);
            Action<int, double[]> normals = (p, z) => McPaths.Normals((uint)p, McPaths.NormalStream, z, z.Length);
            GradientEstimate e = AdjointMonteCarlo.Heston(AdjointPayoff.European(+1, 100.0), m.Spot, m.V0, m.Kappa, m.Theta, m.Xi, m.Rho,
                m.Rate, m.Dt, paths, normals);
            var z0 = new double[2 * steps];
            var gradient = new double[HestonAdjoint.GradientLength];
            var sum = new double[HestonAdjoint.GradientLength];
            var variance = new double[steps + 1];
            double price = 0.0;
            for (int p = 0; p < paths; ++p)
            {
                normals(p, z0);
                price += AdjointPath(m, z0, (df, x, a) => AdjointGreeks.European(+1, 100.0, df, x, 0, steps, a, 0), gradient, variance);
                for (int i = 0; i < gradient.Length; ++i) sum[i] += gradient[i];
            }
            Close(price / paths, e.Price, 1e-13, "price");
            for (int i = 0; i < gradient.Length; ++i)
                Close(sum[i] / paths, e.Gradient[i], 1e-12, $"input {i}");
            // Delta of a call between 0 and 1, vega-like sensitivities to v0 and theta positive.
            Assert.InRange(e.Gradient[HestonAdjoint.SpotIndex], 0.0, 1.0);
            Assert.True(e.Gradient[HestonAdjoint.InitialVarianceIndex] > 0.0);
            Assert.True(e.Gradient[HestonAdjoint.ThetaIndex] > 0.0);

            Assert.Throws<ArgumentOutOfRangeException>(() => AdjointMonteCarlo.Heston(AdjointPayoff.European(+1, 100.0), m.Spot, m.V0,
                m.Kappa, m.Theta, m.Xi, 1.0, m.Rate, m.Dt, paths, normals));
            Assert.Throws<ArgumentOutOfRangeException>(() => AdjointMonteCarlo.Heston(AdjointPayoff.European(+1, 100.0), m.Spot, -0.01,
                m.Kappa, m.Theta, m.Xi, m.Rho, m.Rate, m.Dt, paths, normals));
        }
    }
}
