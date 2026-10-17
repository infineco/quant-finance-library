using System;
using InFine.QuantFinance.Numerics;
using Xunit;

namespace InFine.QuantFinance.Numerics.Tests
{
    /// <summary>Nearest positive semi-definite matrix (Higham 1988) and nearest correlation matrix (Higham 2002).</summary>
    public class NearestMatrixTests
    {
        [Fact]
        public void NearestPsdClipsNegativeEigenvaluesAndIsAtDistanceOfTheirNorm()
        {
            // Higham 1988: the Frobenius distance to the nearest PSD matrix is sqrt(sum of squared negative eigenvalues).
            double[,] a = LinearAlgebraTests.RandomSymmetric(12, 21);
            double[] values = SymmetricEigen.Decompose(a).Values;
            Assert.True(values[0] < 0.0, "test matrix must be indefinite");
            double expected = 0.0;
            foreach (double v in values) if (v < 0.0) expected += v * v;
            expected = Math.Sqrt(expected);

            double[,] x = NearestMatrix.PositiveSemidefinite(a, 0.0, out double minEig);
            Assert.Equal(values[0], minEig, 14);
            Assert.True(SymmetricEigen.Decompose(x).Values[0] >= -1e-13);
            Assert.Equal(expected, Matrix.FrobeniusDistance(a, x), 12);
            Assert.True(Matrix.MaxAsymmetry(x) <= 1e-15);
        }

        [Fact]
        public void NearestPsdLeavesAPsdMatrixUnchanged()
        {
            double[,] a = LinearAlgebraTests.RandomSpd(8, 3);
            double[,] x = NearestMatrix.PositiveSemidefinite(a, 0.0, out _);
            Assert.True(Matrix.FrobeniusDistance(a, x) <= 1e-13);
        }

        [Fact]
        public void NearestPsdFloorGivesStrictlyPositiveEigenvalues()
        {
            double[,] x = NearestMatrix.PositiveSemidefinite(new double[,] { { 1, 2 }, { 2, 1 } }, 1e-6, out double minEig);
            Assert.Equal(-1.0, minEig, 14);
            Assert.Equal(1e-6, SymmetricEigen.Decompose(x).Values[0], 12);
        }

        [Fact]
        public void HighamNearestCorrelationOfTheFourByFourTridiagonalMatrix()
        {
            // Higham's nearcorr documentation example (four decimals as published).
            var a = new double[,] { { 2, -1, 0, 0 }, { -1, 2, -1, 0 }, { 0, -1, 2, -1 }, { 0, 0, -1, 2 } };
            var r = NearestMatrix.Correlation(a);
            Assert.True(r.Converged);
            var expected = new double[,]
            {
                { 1, -0.8084, 0.1916, 0.1068 },
                { -0.8084, 1, -0.6562, 0.1916 },
                { 0.1916, -0.6562, 1, -0.8084 },
                { 0.1068, 0.1916, -0.8084, 1 },
            };
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    Assert.Equal(expected[i, j], r.Matrix[i, j], 4);
        }

        [Fact]
        public void TheDiagonalOfTheInputDoesNotChangeTheNearestCorrelationMatrix()
        {
            // The diagonal is pinned to 1, so its contribution to the distance is constant: [2 -1 0; -1 2 -1; 0 -1 2]
            // has the same nearest correlation matrix as [1 -1 0; -1 1 -1; 0 -1 1], which is D [1 1 0; 1 1 1; 0 1 1] D
            // with D = diag(1, -1, 1); Higham's published answer for the latter is (0.7607, 0.1573).
            var a = new double[,] { { 2, -1, 0 }, { -1, 2, -1 }, { 0, -1, 2 } };
            var r = NearestMatrix.Correlation(a);
            Assert.True(r.Converged);
            var expected = new double[,] { { 1, -0.7607, 0.1573 }, { -0.7607, 1, -0.7607 }, { 0.1573, -0.7607, 1 } };
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    Assert.Equal(expected[i, j], r.Matrix[i, j], 4);
        }

        [Fact]
        public void HighamNearestCorrelationOfTheOnesBandMatrix()
        {
            // Higham (2002), section 1 example: A = [1 1 0; 1 1 1; 0 1 1].
            var a = new double[,] { { 1, 1, 0 }, { 1, 1, 1 }, { 0, 1, 1 } };
            var r = NearestMatrix.Correlation(a);
            Assert.True(r.Converged);
            var expected = new double[,] { { 1, 0.7607, 0.1573 }, { 0.7607, 1, 0.7607 }, { 0.1573, 0.7607, 1 } };
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    Assert.Equal(expected[i, j], r.Matrix[i, j], 4);
        }

        /// <summary>Correlation matrix of a random factor model with k factors plus idiosyncratic noise.</summary>
        static double[,] RandomCorrelation(int n, int seed)
        {
            var rng = new Random(seed);
            var b = new double[n, 3];
            for (int i = 0; i < n; i++)
                for (int k = 0; k < 3; k++)
                    b[i, k] = rng.NextDouble() * 2.0 - 1.0;
            double[,] c = Matrix.MultiplyByTranspose(b);
            for (int i = 0; i < n; i++) c[i, i] += 0.5;
            var corr = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    corr[i, j] = c[i, j] / Math.Sqrt(c[i, i] * c[j, j]);
            return corr;
        }

        [Theory]
        [InlineData(6, 1)]
        [InlineData(20, 2)]
        [InlineData(40, 3)]
        public void RepairedPerturbedCorrelationIsAValidCorrelationMatrixAndNoFartherThanTheOriginal(int n, int seed)
        {
            double[,] truth = RandomCorrelation(n, seed);
            var rng = new Random(seed + 100);
            var noisy = (double[,])truth.Clone();
            for (int i = 0; i < n; i++)
                for (int j = 0; j < i; j++)
                {
                    double v = truth[i, j] + 1.0 * (rng.NextDouble() - 0.5);
                    noisy[i, j] = v;
                    noisy[j, i] = v;
                }
            Assert.True(SymmetricEigen.Decompose(noisy).Values[0] < 0.0, "perturbation must break positive semi-definiteness");

            var r = NearestMatrix.Correlation(noisy);
            Assert.True(r.Converged);
            for (int i = 0; i < n; i++) Assert.Equal(1.0, r.Matrix[i, i], 15);
            Assert.True(Matrix.MaxAsymmetry(r.Matrix) <= 1e-15);
            Assert.True(SymmetricEigen.Decompose(r.Matrix).Values[0] >= -1e-14);
            // The nearest correlation matrix is at least as close as the (valid) matrix we perturbed.
            Assert.True(r.Distance <= Matrix.FrobeniusDistance(noisy, truth) + 1e-9);
            Assert.Equal(r.Distance, Matrix.FrobeniusDistance(noisy, r.Matrix), 12);
        }

        [Fact]
        public void ACorrelationMatrixIsItsOwnNearestCorrelationMatrix()
        {
            double[,] c = RandomCorrelation(15, 8);
            var r = NearestMatrix.Correlation(c);
            Assert.True(r.Converged);
            Assert.True(r.Iterations <= 3);
            Assert.True(Matrix.FrobeniusDistance(c, r.Matrix) <= 1e-12);
        }

        [Fact]
        public void EigenvalueFloorYieldsAStrictlyPositiveDefiniteCorrelationMatrix()
        {
            var a = new double[,] { { 1, 1, 0 }, { 1, 1, 1 }, { 0, 1, 1 } };
            var r = NearestMatrix.Correlation(a, eigenvalueFloor: 1e-6);
            Assert.True(SymmetricEigen.Decompose(r.Matrix).Values[0] >= 1e-6 - 1e-12);
            Assert.True(Cholesky.TryFactor(r.Matrix, out _));
            for (int i = 0; i < 3; i++) Assert.Equal(1.0, r.Matrix[i, i], 14);
        }
    }
}
