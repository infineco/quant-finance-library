using System;
using InFine.QuantFinance.Numerics;
using Xunit;

namespace InFine.QuantFinance.Numerics.Tests
{
    /// <summary>Symmetric eigen-decomposition and Cholesky factorisations against hand values and exact identities.</summary>
    public class LinearAlgebraTests
    {
        internal static double[,] RandomSymmetric(int n, int seed)
        {
            var rng = new Random(seed);
            var a = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j <= i; j++)
                {
                    double v = rng.NextDouble() * 2.0 - 1.0;
                    a[i, j] = v;
                    a[j, i] = v;
                }
            return a;
        }

        /// <summary>B B^T + shift I with B random n x n: symmetric positive definite.</summary>
        internal static double[,] RandomSpd(int n, int seed, double shift = 1.0)
        {
            var rng = new Random(seed);
            var b = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    b[i, j] = rng.NextDouble() * 2.0 - 1.0;
            double[,] a = Matrix.MultiplyByTranspose(b);
            for (int i = 0; i < n; i++) a[i, i] += shift;
            return a;
        }

        static void Close(double[,] expected, double[,] actual, double abs, string what)
        {
            Assert.Equal(expected.GetLength(0), actual.GetLength(0));
            Assert.Equal(expected.GetLength(1), actual.GetLength(1));
            for (int i = 0; i < expected.GetLength(0); i++)
                for (int j = 0; j < expected.GetLength(1); j++)
                    Assert.True(Math.Abs(expected[i, j] - actual[i, j]) <= abs,
                        $"{what}[{i},{j}]: expected {expected[i, j]:R}, got {actual[i, j]:R}");
        }

        // ---- Matrix helpers ------------------------------------------------------------------------------------

        [Fact]
        public void MultiplyAndTransposeMatchHandValues()
        {
            var a = new double[,] { { 1, 2, 3 }, { 4, 5, 6 } };
            var b = new double[,] { { 7, 8 }, { 9, 10 }, { 11, 12 } };
            Close(new double[,] { { 58, 64 }, { 139, 154 } }, Matrix.Multiply(a, b), 0, "ab");
            Close(new double[,] { { 1, 4 }, { 2, 5 }, { 3, 6 } }, Matrix.Transpose(a), 0, "a^T");
            Close(new double[,] { { 14, 32 }, { 32, 77 } }, Matrix.MultiplyByTranspose(a), 0, "a a^T");
            Assert.Throws<ArgumentException>(() => Matrix.Multiply(a, a));
        }

        [Fact]
        public void FrobeniusNormIsScaledAgainstOverflow()
        {
            Assert.Equal(5.0, Matrix.FrobeniusNorm(new double[,] { { 3, 0 }, { 0, 4 } }), 14);
            double big = Matrix.FrobeniusNorm(new double[,] { { 3e200, 0 }, { 0, 4e200 } });
            Assert.Equal(5e200, big, 5e186);
        }

        // ---- Jacobi eigen-decomposition -----------------------------------------------------------------------

        [Fact]
        public void TwoByTwoEigenvaluesAreOneAndThree()
        {
            var eig = SymmetricEigen.Decompose(new double[,] { { 2, 1 }, { 1, 2 } });
            Assert.True(eig.Converged);
            Assert.Equal(1.0, eig.Values[0], 14);
            Assert.Equal(3.0, eig.Values[1], 14);
            // Eigenvector of 3 is (1, 1)/sqrt 2 up to sign.
            Assert.Equal(1.0 / Math.Sqrt(2.0), Math.Abs(eig.Vectors[0, 1]), 14);
            Assert.Equal(1.0 / Math.Sqrt(2.0), Math.Abs(eig.Vectors[1, 1]), 14);
        }

        [Theory]
        [InlineData(5, 0.3)]
        [InlineData(40, 0.6)]
        [InlineData(40, -0.02)]
        public void EquicorrelationMatrixHasTheKnownSpectrum(int n, double rho)
        {
            // 1 + (n-1) rho once, 1 - rho (n-1) times.
            var a = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    a[i, j] = i == j ? 1.0 : rho;
            var eig = SymmetricEigen.Decompose(a);
            Assert.True(eig.Converged);
            double big = 1.0 + (n - 1) * rho, small = 1.0 - rho;
            var expected = new double[n];
            for (int i = 0; i < n; i++) expected[i] = small;
            expected[n - 1] = big;
            Array.Sort(expected);
            for (int i = 0; i < n; i++) Assert.Equal(expected[i], eig.Values[i], 12);
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(3, 2)]
        [InlineData(30, 3)]
        [InlineData(120, 4)]
        public void RandomSymmetricMatrixReconstructsAndVectorsAreOrthonormal(int n, int seed)
        {
            double[,] a = RandomSymmetric(n, seed);
            var eig = SymmetricEigen.Decompose(a);
            Assert.True(eig.Converged);
            for (int k = 1; k < n; k++) Assert.True(eig.Values[k - 1] <= eig.Values[k]);
            Close(a, eig.Reconstruct(eig.Values), 1e-12 * n, "V L V^T");
            double[,] vtv = Matrix.Multiply(Matrix.Transpose(eig.Vectors), eig.Vectors);
            Close(Matrix.Identity(n), vtv, 1e-13 * n, "V^T V");
            // trace and Frobenius norm are spectral invariants
            double trace = 0.0, sumSq = 0.0;
            for (int i = 0; i < n; i++) trace += a[i, i];
            double sum = 0.0;
            foreach (double v in eig.Values) { sum += v; sumSq += v * v; }
            Assert.Equal(trace, sum, 10);
            Assert.Equal(Matrix.FrobeniusNorm(a), Math.Sqrt(sumSq), 10);
        }

        [Fact]
        public void DiagonalAndRepeatedEigenvaluesAreHandled()
        {
            var eig = SymmetricEigen.Decompose(Matrix.Identity(6));
            Assert.Equal(0, eig.Sweeps);
            foreach (double v in eig.Values) Assert.Equal(1.0, v);
            var d = SymmetricEigen.Decompose(new double[,] { { 3, 0, 0 }, { 0, -1, 0 }, { 0, 0, 2 } });
            Assert.Equal(new[] { -1.0, 2.0, 3.0 }, d.Values);
        }

        [Fact]
        public void EigenRejectsBadInput()
        {
            Assert.Throws<ArgumentException>(() => SymmetricEigen.Decompose(new double[2, 3]));
            Assert.Throws<ArgumentException>(() => SymmetricEigen.Decompose(new double[,] { { 1, double.NaN }, { double.NaN, 1 } }));
        }

        // ---- Cholesky ------------------------------------------------------------------------------------------

        [Fact]
        public void CholeskyOfHandExample()
        {
            // [[4,2],[2,3]] = [[2,0],[1,sqrt2]] [[2,1],[0,sqrt2]]
            Assert.True(Cholesky.TryFactor(new double[,] { { 4, 2 }, { 2, 3 } }, out double[,] l));
            Close(new double[,] { { 2, 0 }, { 1, Math.Sqrt(2.0) } }, l, 1e-15, "L");
        }

        [Theory]
        [InlineData(4, 1)]
        [InlineData(50, 2)]
        [InlineData(200, 3)]
        public void CholeskyReconstructsRandomSpd(int n, int seed)
        {
            double[,] a = RandomSpd(n, seed);
            double[,] l = Cholesky.Factor(a);
            Close(a, Matrix.MultiplyByTranspose(l), 1e-12 * n, "L L^T");
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++) Assert.Equal(0.0, l[i, j]);
        }

        [Fact]
        public void CholeskyDetectsIndefiniteAndSingularMatrices()
        {
            Assert.False(Cholesky.TryFactor(new double[,] { { 1, 2 }, { 2, 1 } }, out _));
            Assert.False(Cholesky.TryFactor(new double[,] { { 1, 1 }, { 1, 1 } }, out _)); // rank one: zero pivot
            Assert.False(Cholesky.TryFactor(new double[,] { { 0 } }, out _));
            Assert.False(Cholesky.TryFactor(new double[,] { { 1, 0 }, { 0, double.NaN } }, out _));
            Assert.Throws<ArgumentException>(() => Cholesky.Factor(new double[,] { { 1, 2 }, { 2, 1 } }));
        }

        [Fact]
        public void PivotedCholeskyRevealsTheRankOfASemidefiniteMatrix()
        {
            // A = B B^T with B 4 x 2: rank 2.
            var b = new double[,] { { 1, 0.5 }, { 2, -1 }, { 0, 3 }, { -1, 1 } };
            double[,] a = Matrix.MultiplyByTranspose(b);
            var r = Cholesky.PivotedFactor(a);
            Assert.Equal(2, r.Rank);
            Close(a, r.Reconstruct(), 1e-13, "P L L^T P^T");
            for (int i = 0; i < 4; i++)
                for (int j = 2; j < 4; j++) Assert.Equal(0.0, r.L[i, j]);
        }

        [Fact]
        public void PivotedCholeskyOfPositiveDefiniteMatrixHasFullRank()
        {
            double[,] a = RandomSpd(25, 9);
            var r = Cholesky.PivotedFactor(a);
            Assert.Equal(25, r.Rank);
            Close(a, r.Reconstruct(), 1e-12, "P L L^T P^T");
            // diagonal of L is non-increasing under complete pivoting
            for (int k = 1; k < 25; k++) Assert.True(r.L[k, k] <= r.L[k - 1, k - 1] + 1e-12);
        }

        [Fact]
        public void RobustCholeskyLeavesAPositiveDefiniteMatrixAlone()
        {
            double[,] a = RandomSpd(10, 5);
            var r = Cholesky.Robust(a);
            Assert.False(r.Repaired);
            Assert.True(double.IsNaN(r.MinEigenvalue));
            Assert.Equal(0.0, r.Adjustment);
            Close(Cholesky.Factor(a), r.L, 0, "L");
        }

        [Fact]
        public void RobustCholeskyRepairsAnIndefiniteCorrelationMatrix()
        {
            // Pairwise correlations that cannot coexist (a = b = 0.9 but b,c = -0.9 ... c,a = 0.9): indefinite.
            var a = new double[,] { { 1, 0.9, 0.9 }, { 0.9, 1, -0.9 }, { 0.9, -0.9, 1 } };
            Assert.False(Cholesky.TryFactor(a, out _));
            var r = Cholesky.Robust(a);
            Assert.True(r.Repaired);
            Assert.True(r.MinEigenvalue < -0.5);
            double[,] repaired = Matrix.MultiplyByTranspose(r.L);
            Assert.True(SymmetricEigen.Decompose(repaired).Values[0] >= 0.0);
            Assert.Equal(r.Adjustment, Matrix.FrobeniusDistance(a, repaired), 12);
            // Nearest PSD distance is sqrt(sum of squared negative eigenvalues); the floor adds a negligible amount.
            double nearest = Math.Sqrt(SumSquaredNegatives(SymmetricEigen.Decompose(a).Values));
            Assert.Equal(nearest, r.Adjustment, 6);
        }

        static double SumSquaredNegatives(double[] values)
        {
            double s = 0.0;
            foreach (double v in values) if (v < 0.0) s += v * v;
            return s;
        }

        [Fact]
        public void RobustCholeskyHandlesASingularMatrix()
        {
            var a = new double[,] { { 1, 1 }, { 1, 1 } };
            var r = Cholesky.Robust(a);
            Assert.True(r.Repaired);
            Close(a, Matrix.MultiplyByTranspose(r.L), 1e-9, "L L^T ~ A");
        }
    }
}
