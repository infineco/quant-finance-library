using System;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Models.Diffusion;
using InFine.QuantFinance.Random.DotNet;
using InFine.QuantFinance.TestSupport;
using Xunit;

namespace InFine.QuantFinance.Models.Tests
{
    public class UnitTest_CholeskyCorrelation
    {
        static readonly double[,] Correlation =
        {
            { 1.0, 0.6, 0.3, -0.2 },
            { 0.6, 1.0, 0.5, 0.1 },
            { 0.3, 0.5, 1.0, 0.4 },
            { -0.2, 0.1, 0.4, 1.0 },
        };

        [Fact]
        public void FactorReproducesThePositiveDefiniteCorrelationAndIsLowerTriangular()
        {
            var f = CholeskyCorrelation.FromCorrelation(Correlation);
            Assert.False(f.Repaired);
            double[,] c = f.Reconstruct();
            for (int i = 0; i < 4; ++i)
                for (int j = 0; j < 4; ++j)
                {
                    Assert.Equal(Correlation[i, j], c[i, j], 14);
                    if (j > i) Assert.Equal(0.0, f.Lower[i * 4 + j]);
                }
        }

        [Fact]
        public void PerfectCorrelationDrivesBothVariablesWithTheSameDraw()
        {
            var f = CholeskyCorrelation.FromCorrelation(new double[,] { { 1.0, 1.0 }, { 1.0, 1.0 } });
            Assert.Equal(new[] { 1.0, 0.0, 1.0, 0.0 }, f.Lower);
            var anti = CholeskyCorrelation.FromCorrelation(new double[,] { { 1.0, -1.0 }, { -1.0, 1.0 } });
            Assert.Equal(new[] { 1.0, 0.0, -1.0, 0.0 }, anti.Lower);
            // a repeated asset inside a larger positive semi-definite matrix
            var three = CholeskyCorrelation.FromCorrelation(new double[,] { { 1.0, 0.5, 0.5 }, { 0.5, 1.0, 1.0 }, { 0.5, 1.0, 1.0 } });
            double[] z = { 0.3, -1.2, 0.8 };
            CholeskyCorrelation.CorrelateInPlace(three.Lower, 3, z, 0);
            Assert.Equal(z[1], z[2], 14); // equal up to the rounding of 0.75 / sqrt(0.75) vs sqrt(0.75)
        }

        [Fact]
        public void IndefiniteMatrixIsRejectedUnlessRepairIsRequested()
        {
            var bad = new double[,] { { 1.0, 0.9, 0.9 }, { 0.9, 1.0, -0.9 }, { 0.9, -0.9, 1.0 } };
            Assert.Throws<ArgumentException>(() => CholeskyCorrelation.FromCorrelation(bad));
            var repaired = CholeskyCorrelation.FromCorrelation(bad, repairIndefinite: true);
            Assert.True(repaired.Repaired);
            Assert.True(repaired.Adjustment > 0.0);
        }

        [Fact]
        public void MalformedMatricesAreRejected()
        {
            Assert.Throws<ArgumentException>(() => CholeskyCorrelation.FromCorrelation(new double[,] { { 1.0, 0.2 }, { 0.3, 1.0 } }));
            Assert.Throws<ArgumentException>(() => CholeskyCorrelation.FromCorrelation(new double[,] { { 1.1, 0.2 }, { 0.2, 1.0 } }));
            Assert.Throws<ArgumentException>(() => CholeskyCorrelation.FromCorrelation(new double[,] { { 1.0, 1.2 }, { 1.2, 1.0 } }));
            Assert.Throws<ArgumentException>(() => CholeskyCorrelation.FromCorrelation(new double[2, 3]));
        }

        [Fact]
        public void InPlaceCorrelationEqualsTheComponentsOfLz()
        {
            var f = CholeskyCorrelation.FromCorrelation(Correlation);
            double[] z = { 9.0, 0.4, -1.1, 2.3, 0.7, 9.0 };
            var expected = new double[4];
            for (int i = 0; i < 4; ++i)
                expected[i] = CholeskyCorrelation.Component(f.Lower, 4, i, z, 1);
            CholeskyCorrelation.CorrelateInPlace(f.Lower, 4, z, 1);
            for (int i = 0; i < 4; ++i)
                Assert.Equal(expected[i], z[1 + i]);
            Assert.Equal(9.0, z[0]);
            Assert.Equal(9.0, z[5]);
        }
    }

    public class UnitTest_CorrelatedLogNormalDiffusion
    {
        const ulong Seed = 0x0045_BA5C_E7C0_FFEEUL;
        static readonly double[] Sigma = { 0.2, 0.35, 0.25 };
        static readonly double[] Dividend = { 0.0, 0.02, 0.01 };
        static readonly double[] Spot0 = { 100.0, 50.0, 80.0 };
        static readonly double[,] Correlation = { { 1.0, 0.7, -0.3 }, { 0.7, 1.0, 0.1 }, { -0.3, 0.1, 1.0 } };

        static void Normals(uint path, double[] destination, int count)
        {
            for (int block = 0, k = 0; k < count; ++block)
            {
                uint r0, r1, r2, r3;
                Philox4x32x10.GenerateBlock(Seed, (ulong)block, path, 0, out r0, out r1, out r2, out r3);
                double z0, z1, z2, z3;
                Philox4x32x10.BoxMuller(r0, r1, out z0, out z1);
                Philox4x32x10.BoxMuller(r2, r3, out z2, out z3);
                if (k < count) destination[k++] = z0;
                if (k < count) destination[k++] = z1;
                if (k < count) destination[k++] = z2;
                if (k < count) destination[k++] = z3;
            }
        }

        [Fact]
        public void SampleCorrelationMeanAndVarianceOfLogReturnsMatchTheInputs()
        {
            const int paths = 40000, steps = 4, d = 3;
            const double rate = 0.03, horizon = 2.0;
            var f = CholeskyCorrelation.FromCorrelation(Correlation);
            var dt = new double[steps];
            var rates = new double[steps];
            for (int t = 0; t < steps; ++t) { dt[t] = horizon / steps; rates[t] = rate; }
            var log0 = new double[d];
            for (int i = 0; i < d; ++i) log0[i] = Math.Log(Spot0[i]);
            var normals = new double[steps * d];
            var logPath = new double[(steps + 1) * d];

            // per-step log-returns: means, variances, cross moments (all steps pooled), and terminal discounted spots
            int samples = paths * steps;
            var sum = new double[d];
            var cross = new double[d, d];
            var terminal = new double[d];
            var terminalSquares = new double[d];
            for (int p = 0; p < paths; ++p)
            {
                Normals((uint)p, normals, steps * d);
                CorrelatedLogNormalMath.DiffusePath(log0, Sigma, Dividend, f.Lower, d, dt, rates, steps, normals, logPath);
                for (int t = 0; t < steps; ++t)
                    for (int i = 0; i < d; ++i)
                    {
                        double xi = logPath[(t + 1) * d + i] - logPath[t * d + i];
                        sum[i] += xi;
                        for (int j = 0; j < d; ++j)
                            cross[i, j] += xi * (logPath[(t + 1) * d + j] - logPath[t * d + j]);
                    }
                for (int i = 0; i < d; ++i)
                {
                    double x = Math.Exp(logPath[steps * d + i] - (rate - Dividend[i]) * horizon) / Spot0[i];
                    terminal[i] += x;
                    terminalSquares[i] += x * x;
                }
            }
            double h = horizon / steps;
            var mean = new double[d];
            var variance = new double[d];
            for (int i = 0; i < d; ++i)
            {
                mean[i] = sum[i] / samples;
                variance[i] = cross[i, i] / samples - mean[i] * mean[i];
                double expectedMean = (rate - Dividend[i] - 0.5 * Sigma[i] * Sigma[i]) * h;
                double sd = Sigma[i] * Math.Sqrt(h);
                Assert.True(Math.Abs(mean[i] - expectedMean) < 4.0 * sd / Math.Sqrt(samples), $"asset {i}: mean {mean[i]} vs {expectedMean}");
                // relative standard error of a sample variance: sqrt(2 / n)
                Assert.True(Math.Abs(variance[i] / (sd * sd) - 1.0) < 4.0 * Math.Sqrt(2.0 / samples), $"asset {i}: variance {variance[i]} vs {sd * sd}");
                // martingale: E[S_T e^{-(r - q) T} / S_0] = 1
                double m = terminal[i] / paths;
                double se = Math.Sqrt((terminalSquares[i] / paths - m * m) / (paths - 1));
                Assert.True(Math.Abs(m - 1.0) < 4.0 * se, $"asset {i}: forward ratio {m} +- {se}");
            }
            for (int i = 0; i < d; ++i)
                for (int j = 0; j < i; ++j)
                {
                    double c = (cross[i, j] / samples - mean[i] * mean[j]) / Math.Sqrt(variance[i] * variance[j]);
                    double rho = Correlation[i, j];
                    // standard error of a sample correlation: (1 - rho^2) / sqrt(n)
                    Assert.True(Math.Abs(c - rho) < 4.0 * (1.0 - rho * rho) / Math.Sqrt(samples), $"({i}, {j}): sample {c} vs {rho}");
                }
        }

        [Fact]
        public void IdentityCorrelationGivesIndependentSingleAssetStepsBitForBit()
        {
            // with L = I each asset follows the one-dimensional log-Euler step of EquityDiffusion on its own draw
            var f = CholeskyCorrelation.Independent(3);
            double[] dt = { 0.25, 0.5 }, rates = { 0.01, 0.04 };
            double[] normals = { 0.3, -1.0, 2.0, 0.7, 0.1, -0.4 };
            var logPath = new double[9];
            var log0 = new double[] { Math.Log(100.0), Math.Log(50.0), Math.Log(80.0) };
            CorrelatedLogNormalMath.DiffusePath(log0, Sigma, Dividend, f.Lower, 3, dt, rates, 2, normals, logPath);
            for (int i = 0; i < 3; ++i)
            {
                double x = log0[i];
                for (int t = 0; t < 2; ++t)
                {
                    x += (rates[t] - Dividend[i] - 0.5 * Sigma[i] * Sigma[i]) * dt[t] + Sigma[i] * Math.Sqrt(dt[t]) * normals[t * 3 + i];
                    Assert.Equal(x, logPath[(t + 1) * 3 + i], 14);
                }
            }
        }

        [HybridizerFact]
        public void KernelMatchesTheHostReferencePathByPath()
        {
            const int paths = 64, steps = 5, d = 3;
            double[] dates = { 0.0, 0.1, 0.35, 0.5, 1.0, 1.25 };
            var schedule = new PrecalculatedSchedule(dates);
            var rate = new ConstantRate(0.02);
            var f = CholeskyCorrelation.FromCorrelation(Correlation);
            var logSpot = new ArrayDoubleSpot(paths, (steps + 1) * d);
            var brownian = new ArrayBrownian(paths, steps * d);
            var normals = new double[paths][];
            for (int p = 0; p < paths; ++p)
            {
                normals[p] = new double[steps * d];
                Normals((uint)p, normals[p], steps * d);
                for (int k = 0; k < steps * d; ++k) brownian[p, k] = normals[p][k];
                for (int i = 0; i < d; ++i) logSpot[p, i] = Math.Log(Spot0[i]);
            }
            new CorrelatedLogNormalDiffusion<ConstantRate, ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule>(
                Sigma, Dividend, f, rate, logSpot, brownian, schedule).Simulate(0, paths);

            var dt = new double[steps];
            var rates = new double[steps];
            for (int t = 0; t < steps; ++t) { dt[t] = schedule.getDT(t); rates[t] = 0.02; }
            var log0 = new double[d];
            for (int i = 0; i < d; ++i) log0[i] = Math.Log(Spot0[i]);
            var expected = new double[(steps + 1) * d];
            for (int p = 0; p < paths; ++p)
            {
                CorrelatedLogNormalMath.DiffusePath(log0, Sigma, Dividend, f.Lower, d, dt, rates, steps, normals[p], expected);
                for (int k = 0; k < expected.Length; ++k)
                    Assert.Equal(expected[k], logSpot[p, k]);
            }
        }
    }
}
