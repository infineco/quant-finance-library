using System;
using InFine.QuantFinance.Numerics;
using Xunit;

namespace InFine.QuantFinance.Risk.Tests
{
    internal static class TestHelpers
    {
        public static void Close(double[,] expected, double[,] actual, double tolerance, string what)
        {
            Assert.Equal(expected.GetLength(0), actual.GetLength(0));
            Assert.Equal(expected.GetLength(1), actual.GetLength(1));
            for (int i = 0; i < expected.GetLength(0); i++)
                for (int j = 0; j < expected.GetLength(1); j++)
                    Assert.True(Math.Abs(expected[i, j] - actual[i, j]) <= tolerance * Math.Max(1.0, Math.Abs(expected[i, j])),
                        $"{what}[{i},{j}]: expected {expected[i, j]:R}, got {actual[i, j]:R}");
        }

        /// <summary>T x N Gaussian returns with a common factor, deterministic for a seed.</summary>
        public static double[,] RandomReturns(int t, int n, int seed, double scale = 0.01)
        {
            var rng = new System.Random(seed);
            var x = new double[t, n];
            for (int i = 0; i < t; i++)
            {
                double f = Normal(rng);
                for (int j = 0; j < n; j++)
                    x[i, j] = scale * (0.6 * f + (0.5 + 0.1 * j) * Normal(rng));
            }
            return x;
        }

        static double Normal(System.Random rng)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        public static double MinEigenvalue(double[,] a) => SymmetricEigen.Decompose(a).Values[0];

        public static double[] Row(double[,] x, int i)
        {
            var row = new double[x.GetLength(1)];
            for (int j = 0; j < row.Length; j++) row[j] = x[i, j];
            return row;
        }
    }
}
