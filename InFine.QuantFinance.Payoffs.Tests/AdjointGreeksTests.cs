using System;
using InFine.QuantFinance.Arithmetic.AutoDiff;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Models.Diffusion;
using InFine.QuantFinance.Payoffs.Equity;
using Xunit;

namespace InFine.QuantFinance.Payoffs.Tests
{
    /// <summary>
    /// Adjoint (reverse-mode) pathwise Greeks of the log-normal models on Philox paths (<see cref="McPaths"/>):
    /// against tangent-mode duals on the same paths (every input seeded in turn), against central bump-and-revalue with
    /// common random numbers, against the <see cref="PathwiseGreeks"/> tangent Greeks of the constant model, and against
    /// closed forms.
    /// </summary>
    public class AdjointGreeksTests
    {
        /// <summary>Discounted payoff of a log path and its direct log-spot adjoints (one of the AdjointGreeks functions).</summary>
        delegate double PayoffAdjoint(double discountFactor, double[] logPath, double[] logSpotAdjoint);

        /// <summary>Single-asset term structure: unequal steps (T = 1), every bucket different.</summary>
        sealed class TermStructure
        {
            public double Spot = 100.0;
            public double[] Dt = { 0.05, 0.10, 0.15, 0.20, 0.22, 0.28 };
            public double[] Sigma = { 0.30, 0.22, 0.25, 0.18, 0.27, 0.21 };
            public double[] Rate = { 0.010, 0.020, 0.025, 0.030, 0.035, 0.040 };
            public double[] Dividend = { 0.000, 0.005, 0.010, 0.015, 0.010, 0.020 };
            public int Steps => Dt.Length;
            public int Inputs => LogNormalAdjoint.GradientLength(Steps);

            public static TermStructure Flat(int steps, double maturity, double sigma, double rate, double dividend)
            {
                var m = new TermStructure
                {
                    Dt = new double[steps], Sigma = new double[steps], Rate = new double[steps], Dividend = new double[steps],
                };
                for (int k = 0; k < steps; ++k)
                {
                    m.Dt[k] = maturity / steps;
                    m.Sigma[k] = sigma;
                    m.Rate[k] = rate;
                    m.Dividend[k] = dividend;
                }
                return m;
            }

            /// <summary>Copy with input i of the gradient layout of <see cref="LogNormalAdjoint"/> moved by h.</summary>
            public TermStructure Bumped(int i, double h)
            {
                var m = new TermStructure
                {
                    Spot = Spot, Dt = Dt, Sigma = (double[])Sigma.Clone(), Rate = (double[])Rate.Clone(), Dividend = (double[])Dividend.Clone(),
                };
                int n = Steps;
                if (i == LogNormalAdjoint.SpotIndex) m.Spot += h;
                else if (i < 1 + n) m.Sigma[i - 1] += h;
                else if (i < 1 + 2 * n) m.Rate[i - 1 - n] += h;
                else m.Dividend[i - 1 - 2 * n] += h;
                return m;
            }
        }

        static double AdjointPath(TermStructure m, double[] z, PayoffAdjoint payoff, double[] gradient)
        {
            int n = m.Steps;
            var logPath = new double[n + 1];
            var adjoint = new double[n + 1];
            Array.Clear(gradient, 0, gradient.Length);
            double df = LogNormalAdjoint.Forward(m.Spot, m.Rate, m.Dividend, m.Sigma, m.Dt, n, z, 0, logPath, 0);
            double premium = payoff(df, logPath, adjoint);
            LogNormalAdjoint.Reverse(m.Spot, m.Sigma, m.Dt, n, z, 0, premium, adjoint, 0, gradient, 0);
            return premium;
        }

        /// <summary>Direction of input <paramref name="input"/> in the pass seeding inputs first..first+count-1, else a constant.</summary>
        static Dual4 Seed(double value, int input, int first, int count)
        {
            int direction = input - first;
            return direction >= 0 && direction < count ? Dual4.Variable(value, direction) : Dual4.Constant(value);
        }

        /// <summary>
        /// Tangent reference: the dual term-structure path of <see cref="DualLogNormalMath"/>, every input of the gradient
        /// layout seeded in turn (four per <see cref="Dual4"/> pass). Returns the premium; tangents go to <paramref name="tangent"/>.
        /// </summary>
        static double TangentPath(TermStructure m, double[] z, Func<Dual4[], Dual4> payoff, double[] tangent)
        {
            int n = m.Steps, inputs = m.Inputs;
            var path = new Dual4[n + 1];
            var sigma = new Dual4[n];
            var rate = new Dual4[n];
            var dividend = new Dual4[n];
            double value = double.NaN;
            for (int first = 0; first < inputs; first += Dual4.Width)
            {
                int count = Math.Min(Dual4.Width, inputs - first);
                Dual4 spot = Seed(m.Spot, LogNormalAdjoint.SpotIndex, first, count);
                for (int k = 0; k < n; ++k)
                {
                    sigma[k] = Seed(m.Sigma[k], LogNormalAdjoint.SigmaIndex(k), first, count);
                    rate[k] = Seed(m.Rate[k], LogNormalAdjoint.RateIndex(n, k), first, count);
                    dividend[k] = Seed(m.Dividend[k], LogNormalAdjoint.DividendIndex(n, k), first, count);
                }
                DualLogNormalMath.Path(spot, rate, dividend, sigma, m.Dt, n, z, 0, path);
                Dual4 integrated = 0.0;
                for (int k = 0; k < n; ++k)
                    integrated += rate[k] * m.Dt[k];
                Dual4 premium = Dual4.Exp(-integrated) * payoff(path);
                for (int j = 0; j < count; ++j)
                    tangent[first + j] = premium.Tangent(j);
                value = premium.Value;
            }
            return value;
        }

        /// <summary>Plain double revaluation on the draws z (multiplicative steps, independent of the adjoint code).</summary>
        static double Revalue(TermStructure m, double[] z, Func<double[], double> payoff)
        {
            int n = m.Steps;
            var path = new double[n + 1];
            path[0] = m.Spot;
            double integrated = 0.0;
            for (int k = 0; k < n; ++k)
            {
                double h = m.Dt[k];
                path[k + 1] = path[k] * Math.Exp((m.Rate[k] - m.Dividend[k] - 0.5 * m.Sigma[k] * m.Sigma[k]) * h + m.Sigma[k] * Math.Sqrt(h) * z[k]);
                integrated += m.Rate[k] * h;
            }
            return Math.Exp(-integrated) * payoff(path);
        }

        readonly Xunit.Abstractions.ITestOutputHelper _output;
        double _worstDifference, _worstScore;

        public AdjointGreeksTests(Xunit.Abstractions.ITestOutputHelper output) { _output = output; }

        /// <summary>|expected - actual| within relative * max(1, |expected|); the worst ratio is reported by <see cref="Report"/>.</summary>
        void Close(double expected, double actual, double relative, string what)
        {
            double scale = Math.Max(1.0, Math.Abs(expected));
            double tolerance = relative * scale;
            _worstDifference = Math.Max(_worstDifference, Math.Abs(expected - actual) / scale);
            Assert.True(Math.Abs(expected - actual) <= tolerance,
                $"{what}: expected {expected:R}, got {actual:R} (|diff| {Math.Abs(expected - actual):E3} > {tolerance:E3})");
        }

        void Within(double expected, double estimate, double standardError, string what)
        {
            Assert.True(standardError > 0.0, what + ": zero standard error");
            double score = Math.Abs(estimate - expected) / standardError;
            _worstScore = Math.Max(_worstScore, score);
            Assert.True(score <= 3.0, $"{what}: {estimate:R} vs {expected:R}, {score:F2} SE");
        }

        void Report()
        {
            _output.WriteLine($"worst scaled difference {_worstDifference:E2}, worst |z| {_worstScore:F2}");
        }

        static double Logistic(double u) => 1.0 / (1.0 + Math.Exp(-u));

        static double Softplus(double u, double c) => c * Math.Log(1.0 + Math.Exp(u / c));

        // ---------------------------------------------------------------- single asset, term structure

        [Fact]
        public void TermStructureAdjointEqualsTangentOnEveryPathForEveryInput()
        {
            var m = new TermStructure();
            int n = m.Steps;
            const double strike = 100.0;
            int[] fixings = { 0, 2, 3, 3, 5, 6 }; // pillar 0 and a repeated fixing included
            var cases = new (string name, PayoffAdjoint adjoint, Func<Dual4[], Dual4> tangent)[]
            {
                ("European call", (df, x, a) => AdjointGreeks.European(+1, strike, df, x, 0, n, a, 0),
                    p => Dual4.Max(p[n] - strike, 0.0)),
                ("European put", (df, x, a) => AdjointGreeks.European(-1, strike, df, x, 0, n, a, 0),
                    p => Dual4.Max(-(p[n] - strike), 0.0)),
                ("Asian call", (df, x, a) => AdjointGreeks.ArithmeticAsian(+1, strike, fixings, fixings.Length, df, x, 0, n, a, 0),
                    p => AsianMath.PathPayoff(p, fixings, n, +1, false, false, strike)),
                ("Asian put", (df, x, a) => AdjointGreeks.ArithmeticAsian(-1, strike, fixings, fixings.Length, df, x, 0, n, a, 0),
                    p => AsianMath.PathPayoff(p, fixings, n, -1, false, false, strike)),
            };
            var z = new double[n];
            var gradient = new double[m.Inputs];
            var tangent = new double[m.Inputs];
            foreach (var c in cases)
            {
                int exercised = 0;
                for (int p = 0; p < 200; ++p)
                {
                    McPaths.Normals((uint)p, McPaths.NormalStream, z, n);
                    double premium = AdjointPath(m, z, c.adjoint, gradient);
                    double value = TangentPath(m, z, c.tangent, tangent);
                    Close(value, premium, 1e-13, c.name + " premium");
                    for (int i = 0; i < m.Inputs; ++i)
                        Close(tangent[i], gradient[i], 1e-10, $"{c.name} path {p} input {i}");
                    if (premium > 0.0) ++exercised;
                }
                Assert.InRange(exercised, 40, 160); // both branches of the kink are exercised
            }
            Report();
        }

        [Fact]
        public void AtTheKinkTheAdjointTakesTheExercisedBranchLikeTheTangent()
        {
            // Zero volatility and zero carry: the path stays at S_0 (to the rounding of exp(ln S_0)) and the strike is set on
            // the simulated S_n, so every payoff sits exactly on its kink; the draws still enter the vegas.
            var m = TermStructure.Flat(3, 1.0, 0.0, 0.0, 0.0);
            int n = m.Steps;
            double[] z = { 0.3, -1.2, 0.8 };
            var logPath = new double[n + 1];
            LogNormalAdjoint.Forward(m.Spot, m.Rate, m.Dividend, m.Sigma, m.Dt, n, z, 0, logPath, 0);
            double strike = Math.Exp(logPath[n]);
            int[] fixings = { 1, 2, 3 };
            var gradient = new double[m.Inputs];
            var tangent = new double[m.Inputs];
            foreach (int sign in new[] { +1, -1 })
            {
                double premium = AdjointPath(m, z, (df, x, a) => AdjointGreeks.European(sign, strike, df, x, 0, n, a, 0), gradient);
                TangentPath(m, z, p => Dual4.Max(sign * (p[n] - strike), 0.0), tangent);
                Assert.Equal(0.0, premium);
                Close(sign * strike / m.Spot, gradient[LogNormalAdjoint.SpotIndex], 1e-14, "delta at the kink");
                for (int i = 0; i < m.Inputs; ++i)
                    Close(tangent[i], gradient[i], 1e-12, $"sign {sign} input {i}");
            }
            double average = (Math.Exp(logPath[1]) + Math.Exp(logPath[2]) + Math.Exp(logPath[3])) / 3.0;
            AdjointPath(m, z, (df, x, a) => AdjointGreeks.ArithmeticAsian(+1, average, fixings, 3, df, x, 0, n, a, 0), gradient);
            TangentPath(m, z, p => AsianMath.PathPayoff(p, fixings, n, +1, false, false, average), tangent);
            for (int i = 0; i < m.Inputs; ++i)
                Close(tangent[i], gradient[i], 1e-12, $"Asian input {i}");
            Report();
        }

        [Fact]
        public void BucketedGreeksSumToTheParallelTangentGreeksOfTheConstantModel()
        {
            // Constant parameters on a uniform grid: the sums of the buckets are the parallel vega, rho and dividend rho
            // that PathwiseGreeks gets by seeding the constants on Dual4 directions.
            const int steps = 8;
            const double T = 1.0, sigma = 0.25, rate = 0.03, dividend = 0.01, strike = 105.0;
            var m = TermStructure.Flat(steps, T, sigma, rate, dividend);
            int[] fixings = McPaths.Range(1, steps);
            var z = new double[steps];
            var gradient = new double[m.Inputs];
            var path = new Dual4[steps + 1];
            for (int p = 0; p < 100; ++p)
            {
                McPaths.Normals((uint)p, McPaths.NormalStream, z, steps);
                DualLogNormalMath.Path(Dual4.Variable(m.Spot, 0), Dual4.Variable(rate, 2), Dual4.Variable(dividend, 3),
                    Dual4.Variable(sigma, 1), T, steps, z, 0, path);
                foreach (bool asian in new[] { false, true })
                {
                    Dual4 reference = asian
                        ? PathwiseGreeks.AsianPremium(+1, false, false, strike, Dual4.Variable(rate, 2), T, path, fixings, steps)
                        : PathwiseGreeks.EuropeanPremium(+1, strike, Dual4.Variable(rate, 2), T, path, steps);
                    double premium = AdjointPath(m, z, asian
                        ? (df, x, a) => AdjointGreeks.ArithmeticAsian(+1, strike, fixings, steps, df, x, 0, steps, a, 0)
                        : (PayoffAdjoint)((df, x, a) => AdjointGreeks.European(+1, strike, df, x, 0, steps, a, 0)), gradient);
                    double vega = 0.0, rho = 0.0, dividendRho = 0.0;
                    for (int k = 0; k < steps; ++k)
                    {
                        vega += gradient[LogNormalAdjoint.SigmaIndex(k)];
                        rho += gradient[LogNormalAdjoint.RateIndex(steps, k)];
                        dividendRho += gradient[LogNormalAdjoint.DividendIndex(steps, k)];
                    }
                    Close(reference.Value, premium, 1e-13, "premium");
                    Close(reference.D0, gradient[LogNormalAdjoint.SpotIndex], 1e-12, "delta");
                    Close(reference.D1, vega, 1e-12, "sum of the vega buckets");
                    Close(reference.D2, rho, 1e-12, "sum of the rho buckets");
                    Close(reference.D3, dividendRho, 1e-12, "sum of the dividend buckets");
                }
            }
            Report();
        }

        [Fact]
        public void TermStructureAdjointEqualsCommonRandomNumberBumpsOnSmoothPayoffs()
        {
            // Softplus payoffs c ln(1 + exp((X - K) / c)) on S_n and on an average: smooth, so central differences with
            // common random numbers converge to the pathwise derivative of the same paths (truncation O(h^2), rounding
            // O(eps / h)). The payoff adjoints are written here; the model adjoint under test is LogNormalAdjoint.
            const int paths = 20000;
            const double strike = 100.0, c = 5.0;
            var m = new TermStructure();
            int n = m.Steps;
            int[] fixings = { 0, 2, 3, 5, 6 };
            var cases = new (string name, PayoffAdjoint adjoint, Func<double[], double> payoff)[]
            {
                ("softplus European", (df, x, a) =>
                    {
                        Array.Clear(a, 0, a.Length);
                        double s = Math.Exp(x[n]);
                        a[n] = df * Logistic((s - strike) / c) * s;
                        return df * Softplus(s - strike, c);
                    },
                    path => Softplus(path[n] - strike, c)),
                ("softplus Asian", (df, x, a) =>
                    {
                        Array.Clear(a, 0, a.Length);
                        double sum = 0.0;
                        foreach (int k in fixings) sum += Math.Exp(x[k]);
                        double average = sum / fixings.Length;
                        double slope = df * Logistic((average - strike) / c) / fixings.Length;
                        foreach (int k in fixings) a[k] += slope * Math.Exp(x[k]);
                        return df * Softplus(average - strike, c);
                    },
                    path =>
                    {
                        double sum = 0.0;
                        foreach (int k in fixings) sum += path[k];
                        return Softplus(sum / fixings.Length - strike, c);
                    }),
            };
            var normals = new double[paths][];
            for (int p = 0; p < paths; ++p)
            {
                normals[p] = new double[n];
                McPaths.Normals((uint)p, McPaths.NormalStream, normals[p], n);
            }
            var gradient = new double[m.Inputs];
            foreach (var c0 in cases)
            {
                var adjoint = new double[m.Inputs];
                for (int p = 0; p < paths; ++p)
                {
                    AdjointPath(m, normals[p], c0.adjoint, gradient);
                    for (int i = 0; i < m.Inputs; ++i) adjoint[i] += gradient[i] / paths;
                }
                for (int i = 0; i < m.Inputs; ++i)
                {
                    double h = i == LogNormalAdjoint.SpotIndex ? 1e-3 : 1e-5;
                    TermStructure up = m.Bumped(i, h), down = m.Bumped(i, -h);
                    double sum = 0.0;
                    for (int p = 0; p < paths; ++p)
                        sum += Revalue(up, normals[p], c0.payoff) - Revalue(down, normals[p], c0.payoff);
                    Close(sum / paths / (2.0 * h), adjoint[i], 1e-7, $"{c0.name} input {i}");
                }
            }
            Report();
        }

        [Fact]
        public void TermStructureEuropeanGreeksMatchBlackScholesWithinThreeStandardErrors()
        {
            // The term-structure model is log-normal with total variance sum sigma_k^2 dt_k and integrated rates, so the
            // Black-Scholes Greeks with effective parameters give every bucket: d sigma_eff / d sigma_k = sigma_k dt_k /
            // (sigma_eff T), d r_eff / d r_k = d q_eff / d q_k = dt_k / T.
            const int paths = 200000;
            const double strike = 105.0;
            var m = new TermStructure();
            int n = m.Steps;
            double T = 0.0, variance = 0.0, integratedRate = 0.0, integratedDividend = 0.0;
            for (int k = 0; k < n; ++k)
            {
                T += m.Dt[k];
                variance += m.Sigma[k] * m.Sigma[k] * m.Dt[k];
                integratedRate += m.Rate[k] * m.Dt[k];
                integratedDividend += m.Dividend[k] * m.Dt[k];
            }
            double sigmaEff = Math.Sqrt(variance / T);
            foreach (int sign in new[] { +1, -1 })
            {
                GradientEstimate e = AdjointMonteCarlo.LogNormal(AdjointPayoff.European(sign, strike), m.Spot, m.Rate, m.Dividend, m.Sigma,
                    m.Dt, paths, (p, z) => McPaths.Normals((uint)p, McPaths.NormalStream, z, n));
                BlackScholesKernels.Greeks(sign, m.Spot, strike, T, integratedRate / T, integratedDividend / T, sigmaEff,
                    out double price, out double delta, out _, out double vega, out _, out double rho, out double dividendRho,
                    out _, out _, out _);

                string option = sign > 0 ? "call" : "put";
                Within(price, e.Price, e.PriceStandardError, option + " price");
                Within(delta, e.Gradient[LogNormalAdjoint.SpotIndex], e.GradientStandardError[LogNormalAdjoint.SpotIndex], option + " delta");
                for (int k = 0; k < n; ++k)
                {
                    int iv = LogNormalAdjoint.SigmaIndex(k), ir = LogNormalAdjoint.RateIndex(n, k), iq = LogNormalAdjoint.DividendIndex(n, k);
                    Within(vega * m.Sigma[k] * m.Dt[k] / (sigmaEff * T), e.Gradient[iv], e.GradientStandardError[iv], $"{option} vega bucket {k}");
                    Within(rho * m.Dt[k] / T, e.Gradient[ir], e.GradientStandardError[ir], $"{option} rho bucket {k}");
                    Within(dividendRho * m.Dt[k] / T, e.Gradient[iq], e.GradientStandardError[iq], $"{option} dividend bucket {k}");
                }
            }
            Report();
        }

        [Fact]
        public void HostDriverAveragesThePerPathAdjoints()
        {
            const int paths = 500;
            var m = new TermStructure();
            int n = m.Steps;
            int[] fixings = McPaths.Range(1, n);
            GradientEstimate e = AdjointMonteCarlo.LogNormal(AdjointPayoff.ArithmeticAsian(+1, 95.0, fixings), m.Spot, m.Rate, m.Dividend,
                m.Sigma, m.Dt, paths, (p, z) => McPaths.Normals((uint)p, McPaths.NormalStream, z, n));
            var z0 = new double[n];
            var gradient = new double[m.Inputs];
            var sum = new double[m.Inputs];
            double price = 0.0;
            for (int p = 0; p < paths; ++p)
            {
                McPaths.Normals((uint)p, McPaths.NormalStream, z0, n);
                price += AdjointPath(m, z0, (df, x, a) => AdjointGreeks.ArithmeticAsian(+1, 95.0, fixings, n, df, x, 0, n, a, 0), gradient);
                for (int i = 0; i < m.Inputs; ++i) sum[i] += gradient[i];
            }
            Assert.Equal(paths, e.Paths);
            Close(price / paths, e.Price, 1e-13, "price");
            for (int i = 0; i < m.Inputs; ++i)
            {
                Close(sum[i] / paths, e.Gradient[i], 1e-12, $"input {i}");
                Assert.True(e.GradientStandardError[i] > 0.0);
            }
        }

        [Fact]
        public void HostDriverRejectsInconsistentInputs()
        {
            var m = new TermStructure();
            Action<int, double[]> normals = (p, z) => McPaths.Normals((uint)p, McPaths.NormalStream, z, z.Length);
            AdjointPayoff call = AdjointPayoff.European(+1, 100.0);
            Assert.Throws<ArgumentException>(() => AdjointMonteCarlo.LogNormal(call, m.Spot, new double[2], m.Dividend, m.Sigma, m.Dt, 10, normals));
            Assert.Throws<ArgumentOutOfRangeException>(() => AdjointMonteCarlo.LogNormal(call, -1.0, m.Rate, m.Dividend, m.Sigma, m.Dt, 10, normals));
            Assert.Throws<ArgumentOutOfRangeException>(() => AdjointMonteCarlo.LogNormal(call, m.Spot, m.Rate, m.Dividend, m.Sigma, m.Dt, 1, normals));
            Assert.Throws<ArgumentOutOfRangeException>(() => AdjointMonteCarlo.LogNormal(AdjointPayoff.ArithmeticAsian(+1, 100.0, new[] { 7 }),
                m.Spot, m.Rate, m.Dividend, m.Sigma, m.Dt, 10, normals));
            Assert.Throws<ArgumentException>(() => AdjointMonteCarlo.LogNormal(AdjointPayoff.Basket(+1, 1.0, new[] { 0.5, 0.5 }, new[] { 1.0, 1.0 }),
                m.Spot, m.Rate, m.Dividend, m.Sigma, m.Dt, 10, normals));
            Assert.Throws<ArgumentOutOfRangeException>(() => AdjointPayoff.European(0, 100.0));
        }

        // ---------------------------------------------------------------- correlated multi-asset

        /// <summary>Correlated log-normal market of D assets with per-step rates.</summary>
        sealed class Basket
        {
            public double[] Spot, Sigma, Dividend, Rates, Dt, Lower;
            public int Assets => Spot.Length;
            public int Steps => Dt.Length;
            public int Inputs => CorrelatedLogNormalAdjoint.GradientLength(Assets, Steps);

            public static Basket FourAssets() => new Basket
            {
                Spot = new[] { 100.0, 95.0, 110.0, 102.0 },
                Sigma = new[] { 0.25, 0.30, 0.20, 0.35 },
                Dividend = new[] { 0.01, 0.00, 0.02, 0.015 },
                Rates = new[] { 0.02, 0.025, 0.03, 0.028, 0.035 },
                Dt = new[] { 0.1, 0.15, 0.25, 0.2, 0.3 },
                Lower = CholeskyCorrelation.FromCorrelation(new[,]
                {
                    { 1.0, 0.5, 0.3, -0.2 },
                    { 0.5, 1.0, 0.4, 0.1 },
                    { 0.3, 0.4, 1.0, 0.25 },
                    { -0.2, 0.1, 0.25, 1.0 },
                }).Lower,
            };

            /// <summary>Copy with input i of the gradient layout of <see cref="CorrelatedLogNormalAdjoint"/> moved by h.</summary>
            public Basket Bumped(int i, double h)
            {
                var b = new Basket
                {
                    Spot = (double[])Spot.Clone(), Sigma = (double[])Sigma.Clone(), Dividend = (double[])Dividend.Clone(),
                    Rates = (double[])Rates.Clone(), Dt = Dt, Lower = Lower,
                };
                int d = Assets;
                if (i < d) b.Spot[i] += h;
                else if (i < 2 * d) b.Sigma[i - d] += h;
                else if (i < 3 * d) b.Dividend[i - 2 * d] += h;
                else b.Rates[i - 3 * d] += h;
                return b;
            }
        }

        static double AdjointPath(Basket b, double[] z, PayoffAdjoint payoff, double[] gradient)
        {
            int d = b.Assets, n = b.Steps;
            var logPath = new double[(n + 1) * d];
            var correlated = new double[n * d];
            var adjoint = new double[(n + 1) * d];
            Array.Clear(gradient, 0, gradient.Length);
            double df = CorrelatedLogNormalAdjoint.Forward(b.Spot, b.Sigma, b.Dividend, b.Lower, d, b.Dt, b.Rates, n, z, 0,
                logPath, 0, correlated, 0);
            double premium = payoff(df, logPath, adjoint);
            CorrelatedLogNormalAdjoint.Reverse(b.Spot, b.Sigma, d, b.Dt, n, correlated, 0, premium, adjoint, 0, gradient, 0);
            return premium;
        }

        /// <summary>
        /// Tangent reference: <see cref="DualLogNormalMath.DiffusePath"/> with every input seeded in turn; the payoff reads
        /// the dual spots at maturity (D entries).
        /// </summary>
        static double TangentPath(Basket b, double[] z, Func<Dual4[], Dual4> payoff, double[] tangent)
        {
            int d = b.Assets, n = b.Steps, inputs = b.Inputs;
            var logSpot0 = new Dual4[d];
            var sigma = new Dual4[d];
            var dividend = new Dual4[d];
            var rates = new Dual4[n];
            var logPath = new Dual4[(n + 1) * d];
            var spotT = new Dual4[d];
            double value = double.NaN;
            for (int first = 0; first < inputs; first += Dual4.Width)
            {
                int count = Math.Min(Dual4.Width, inputs - first);
                for (int a = 0; a < d; ++a)
                {
                    logSpot0[a] = Dual4.Log(Seed(b.Spot[a], CorrelatedLogNormalAdjoint.SpotIndex(a), first, count));
                    sigma[a] = Seed(b.Sigma[a], CorrelatedLogNormalAdjoint.SigmaIndex(d, a), first, count);
                    dividend[a] = Seed(b.Dividend[a], CorrelatedLogNormalAdjoint.DividendIndex(d, a), first, count);
                }
                for (int t = 0; t < n; ++t)
                    rates[t] = Seed(b.Rates[t], CorrelatedLogNormalAdjoint.RateIndex(d, t), first, count);
                DualLogNormalMath.DiffusePath(logSpot0, sigma, dividend, b.Lower, d, b.Dt, rates, n, z, 0, logPath);
                for (int a = 0; a < d; ++a)
                    spotT[a] = Dual4.Exp(logPath[n * d + a]);
                Dual4 integrated = 0.0;
                for (int t = 0; t < n; ++t)
                    integrated += rates[t] * b.Dt[t];
                Dual4 premium = Dual4.Exp(-integrated) * payoff(spotT);
                for (int j = 0; j < count; ++j)
                    tangent[first + j] = premium.Tangent(j);
                value = premium.Value;
            }
            return value;
        }

        /// <summary>Plain double revaluation through the host reference of the diffusion kernel and MultiAssetMath.</summary>
        static double Revalue(Basket b, double[] z, Func<double[], double> payoff)
        {
            int d = b.Assets, n = b.Steps;
            var log0 = new double[d];
            for (int a = 0; a < d; ++a) log0[a] = Math.Log(b.Spot[a]);
            var logPath = new double[(n + 1) * d];
            CorrelatedLogNormalMath.DiffusePath(log0, b.Sigma, b.Dividend, b.Lower, d, b.Dt, b.Rates, n, z, logPath);
            var spotT = new double[d];
            for (int a = 0; a < d; ++a) spotT[a] = Math.Exp(logPath[n * d + a]);
            double integrated = 0.0;
            for (int t = 0; t < n; ++t) integrated += b.Rates[t] * b.Dt[t];
            return Math.Exp(-integrated) * payoff(spotT);
        }

        [Fact]
        public void CorrelatedAdjointEqualsTangentOnEveryPathForEveryInput()
        {
            var b = Basket.FourAssets();
            int d = b.Assets, n = b.Steps;
            double[] weights = { 0.4, 0.3, -0.2, 0.5 };
            double[] reference = (double[])b.Spot.Clone();
            double[] ones = { 1.0, 1.0, 1.0, 1.0 };
            var cases = new (string name, PayoffAdjoint adjoint, Func<Dual4[], Dual4> tangent)[]
            {
                ("basket call", (df, x, a) => AdjointGreeks.Basket(+1, 100.0, weights, ones, d, df, x, 0, n, a, 0),
                    s => MultiAssetMath.BasketPathPayoff(s, d, 0, weights, ones, +1, false, 100.0)),
                ("basket put", (df, x, a) => AdjointGreeks.Basket(-1, 100.0, weights, ones, d, df, x, 0, n, a, 0),
                    s => MultiAssetMath.BasketPathPayoff(s, d, 0, weights, ones, -1, false, 100.0)),
                ("worst-of call", (df, x, a) => AdjointGreeks.Rainbow(+1, false, 0.8, reference, d, df, x, 0, n, a, 0),
                    s => MultiAssetMath.RainbowPathPayoff(s, d, 0, reference, +1, false, 0.8)),
                ("worst-of put", (df, x, a) => AdjointGreeks.Rainbow(-1, false, 0.9, reference, d, df, x, 0, n, a, 0),
                    s => MultiAssetMath.RainbowPathPayoff(s, d, 0, reference, -1, false, 0.9)),
                ("best-of call", (df, x, a) => AdjointGreeks.Rainbow(+1, true, 1.1, reference, d, df, x, 0, n, a, 0),
                    s => MultiAssetMath.RainbowPathPayoff(s, d, 0, reference, +1, true, 1.1)),
            };
            var z = new double[n * d];
            var gradient = new double[b.Inputs];
            var tangent = new double[b.Inputs];
            foreach (var c in cases)
            {
                int exercised = 0;
                for (int p = 0; p < 200; ++p)
                {
                    McPaths.Normals((uint)p, McPaths.NormalStream, z, z.Length);
                    double premium = AdjointPath(b, z, c.adjoint, gradient);
                    double value = TangentPath(b, z, c.tangent, tangent);
                    Close(value, premium, 1e-13, c.name + " premium");
                    for (int i = 0; i < b.Inputs; ++i)
                        Close(tangent[i], gradient[i], 1e-10, $"{c.name} path {p} input {i}");
                    if (premium > 0.0) ++exercised;
                }
                Assert.InRange(exercised, 20, 180);
            }
            Report();
        }

        [Fact]
        public void TiedWorstOfTakesTheFirstAssetLikeTheTangent()
        {
            // Two identical assets with correlation 1: the semi-definite factor drives both with the same draw, so their
            // performances tie exactly on every path. The worst-of is not differentiable there; adjoint and Dual4.Min both
            // assign the whole sensitivity to the first asset.
            var b = new Basket
            {
                Spot = new[] { 100.0, 100.0 }, Sigma = new[] { 0.3, 0.3 }, Dividend = new[] { 0.01, 0.01 },
                Rates = new[] { 0.02, 0.03 }, Dt = new[] { 0.5, 0.5 },
                Lower = CholeskyCorrelation.FromCorrelation(MultiAssetPaths.Uniform(2, 1.0)).Lower,
            };
            double[] reference = { 100.0, 100.0 };
            var z = new double[4];
            var gradient = new double[b.Inputs];
            var tangent = new double[b.Inputs];
            for (int p = 0; p < 50; ++p)
            {
                McPaths.Normals((uint)p, McPaths.NormalStream, z, z.Length);
                double premium = AdjointPath(b, z, (df, x, a) => AdjointGreeks.Rainbow(+1, false, 0.9, reference, 2, df, x, 0, 2, a, 0), gradient);
                TangentPath(b, z, s => MultiAssetMath.RainbowPathPayoff(s, 2, 0, reference, +1, false, 0.9), tangent);
                for (int i = 0; i < b.Inputs; ++i)
                    Close(tangent[i], gradient[i], 1e-12, $"path {p} input {i}");
                Assert.Equal(0.0, gradient[CorrelatedLogNormalAdjoint.SpotIndex(1)]);
                Assert.Equal(0.0, gradient[CorrelatedLogNormalAdjoint.SigmaIndex(2, 1)]);
                if (premium > 0.0) Assert.True(gradient[CorrelatedLogNormalAdjoint.SpotIndex(0)] > 0.0);
            }
            Report();
        }

        [Fact]
        public void CorrelatedAdjointEqualsCommonRandomNumberBumpsOnASmoothBasket()
        {
            // Softplus basket call: smooth, so central differences of the double kernel reference with common random numbers
            // converge to the adjoint of the same paths. The payoff adjoint is written here; CorrelatedLogNormalAdjoint is under test.
            const int paths = 20000;
            const double strike = 100.0, c = 5.0;
            var b = Basket.FourAssets();
            int d = b.Assets, n = b.Steps;
            double[] weights = { 0.4, 0.3, -0.2, 0.5 };
            PayoffAdjoint adjointPayoff = (df, x, a) =>
            {
                Array.Clear(a, 0, a.Length);
                double basket = 0.0;
                for (int k = 0; k < d; ++k) basket += weights[k] * Math.Exp(x[n * d + k]);
                double slope = df * Logistic((basket - strike) / c);
                for (int k = 0; k < d; ++k) a[n * d + k] = slope * weights[k] * Math.Exp(x[n * d + k]);
                return df * Softplus(basket - strike, c);
            };
            Func<double[], double> payoff = s =>
            {
                double basket = 0.0;
                for (int k = 0; k < d; ++k) basket += weights[k] * s[k];
                return Softplus(basket - strike, c);
            };
            var normals = new double[paths][];
            var gradient = new double[b.Inputs];
            var adjoint = new double[b.Inputs];
            for (int p = 0; p < paths; ++p)
            {
                normals[p] = new double[n * d];
                McPaths.Normals((uint)p, McPaths.NormalStream, normals[p], n * d);
                AdjointPath(b, normals[p], adjointPayoff, gradient);
                for (int i = 0; i < b.Inputs; ++i) adjoint[i] += gradient[i] / paths;
            }
            for (int i = 0; i < b.Inputs; ++i)
            {
                double h = i < d ? 1e-3 : 1e-5;
                Basket up = b.Bumped(i, h), down = b.Bumped(i, -h);
                double sum = 0.0;
                for (int p = 0; p < paths; ++p)
                    sum += Revalue(up, normals[p], payoff) - Revalue(down, normals[p], payoff);
                Close(sum / paths / (2.0 * h), adjoint[i], 1e-7, $"input {i}");
            }
            Report();
        }

        [Fact]
        public void TwoAssetAdjointGreeksMatchStulzAndMargrabeWithinThreeStandardErrors()
        {
            // One step is exact for European payoffs. Closed-form Greeks by central differences of the closed forms
            // (smooth, so accurate to ~1e-9, far below the Monte Carlo error).
            const int paths = 200000;
            const double T = 1.0, rho = 0.4, r = 0.03;
            double[] spot = { 100.0, 90.0 }, sigma = { 0.25, 0.35 }, dividend = { 0.01, 0.02 };
            double[] rates = { r }, dt = { T }, ones = { 1.0, 1.0 };
            CholeskyCorrelation correlation = CholeskyCorrelation.FromCorrelation(MultiAssetPaths.Uniform(2, rho));
            Action<int, double[]> normals = (p, z) => McPaths.Normals((uint)p, McPaths.NormalStream, z, z.Length);

            // Worst-of call min(S1, S2) - 85 against Stulz.
            Func<double[], double> stulz = x => MultiAssetKernels.MinMaxPrice(+1, false, x[0], x[1], 85.0, T, x[6], x[4], x[5], x[2], x[3], rho);
            GradientEstimate worst = AdjointMonteCarlo.CorrelatedLogNormal(AdjointPayoff.Rainbow(+1, false, 85.0, ones),
                spot, sigma, dividend, correlation, rates, dt, paths, normals);
            CheckAgainst(stulz, worst, spot, sigma, dividend, r, false, "worst-of");

            // Exchange option max(S1 - S2, 0) = basket (1, -1) with zero strike, against Margrabe. The rate cancels path by
            // path (the discount factor offsets the drift of both assets), so the adjoint rho is zero to rounding.
            Func<double[], double> margrabe = x => MultiAssetKernels.ExchangePrice(x[0], x[1], T, x[4], x[5], x[2], x[3], rho);
            GradientEstimate exchange = AdjointMonteCarlo.CorrelatedLogNormal(AdjointPayoff.Basket(+1, 0.0, new[] { 1.0, -1.0 }, ones),
                spot, sigma, dividend, correlation, rates, dt, paths, normals);
            CheckAgainst(margrabe, exchange, spot, sigma, dividend, r, true, "exchange");
            Report();
        }

        /// <summary>
        /// Price and the 7 sensitivities (2 spots, 2 vols, 2 dividends, rate) of a closed form within 3 SE. With
        /// <paramref name="rateFree"/> the price does not depend on the rate path by path, and the adjoint rho must be 0 to rounding.
        /// </summary>
        void CheckAgainst(Func<double[], double> closedForm, GradientEstimate e, double[] spot, double[] sigma, double[] dividend,
            double rate, bool rateFree, string what)
        {
            double[] x = { spot[0], spot[1], sigma[0], sigma[1], dividend[0], dividend[1], rate };
            int[] index =
            {
                CorrelatedLogNormalAdjoint.SpotIndex(0), CorrelatedLogNormalAdjoint.SpotIndex(1),
                CorrelatedLogNormalAdjoint.SigmaIndex(2, 0), CorrelatedLogNormalAdjoint.SigmaIndex(2, 1),
                CorrelatedLogNormalAdjoint.DividendIndex(2, 0), CorrelatedLogNormalAdjoint.DividendIndex(2, 1),
                CorrelatedLogNormalAdjoint.RateIndex(2, 0),
            };
            Within(closedForm(x), e.Price, e.PriceStandardError, what + " price");
            for (int i = 0; i < x.Length; ++i)
            {
                double h = i < 2 ? 1e-3 : 1e-5;
                double[] up = (double[])x.Clone(), down = (double[])x.Clone();
                up[i] += h;
                down[i] -= h;
                double greek = (closedForm(up) - closedForm(down)) / (2.0 * h);
                if (rateFree && i == 6)
                    Assert.True(Math.Abs(e.Gradient[index[i]]) < 1e-12 && Math.Abs(greek) < 1e-9, $"{what} rho {e.Gradient[index[i]]:E3}");
                else
                    Within(greek, e.Gradient[index[i]], e.GradientStandardError[index[i]], $"{what} input {i}");
            }
        }
    }
}
