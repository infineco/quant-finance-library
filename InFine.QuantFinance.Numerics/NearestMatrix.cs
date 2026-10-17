using System;

namespace InFine.QuantFinance.Numerics
{
    /// <summary>Outcome of <see cref="NearestMatrix.Correlation"/>.</summary>
    public sealed class NearestCorrelationResult
    {
        internal NearestCorrelationResult(double[,] matrix, int iterations, bool converged, double distance)
        {
            Matrix = matrix;
            Iterations = iterations;
            Converged = converged;
            Distance = distance;
        }

        /// <summary>The correlation matrix: symmetric, unit diagonal, positive semi-definite.</summary>
        public double[,] Matrix { get; }

        /// <summary>Alternating-projection iterations performed.</summary>
        public int Iterations { get; }

        /// <summary>False when the iteration limit was reached before the tolerance.</summary>
        public bool Converged { get; }

        /// <summary>Frobenius distance between the input and <see cref="Matrix"/>.</summary>
        public double Distance { get; }
    }

    /// <summary>
    /// Repair of symmetric matrices that should be positive semi-definite (covariance) or a correlation matrix but
    /// are not, because of asynchronous or missing data, pairwise estimation, manual edits or stress overlays.
    /// Host-side; each iteration costs one eigen-decomposition (see <see cref="SymmetricEigen"/> for the O(n^3)
    /// per-sweep cost, fine up to a few hundred assets).
    /// </summary>
    public static class NearestMatrix
    {
        /// <summary>
        /// Nearest symmetric positive semi-definite matrix in the Frobenius norm (Higham, "Computing a nearest
        /// symmetric positive semidefinite matrix", Linear Algebra and its Applications 103, 1988): symmetrise, then
        /// clip the eigenvalues at <paramref name="eigenvalueFloor"/> (0 gives the exact projection).
        /// </summary>
        /// <param name="a">Square matrix with finite entries.</param>
        /// <param name="eigenvalueFloor">Smallest allowed eigenvalue, at least 0.</param>
        /// <param name="minEigenvalue">Smallest eigenvalue of the symmetric part of the input.</param>
        public static double[,] PositiveSemidefinite(double[,] a, double eigenvalueFloor, out double minEigenvalue)
        {
            if (!(eigenvalueFloor >= 0.0)) throw new ArgumentOutOfRangeException(nameof(eigenvalueFloor));
            SymmetricEigenDecomposition eig = SymmetricEigen.Decompose(a);
            int n = eig.Values.Length;
            minEigenvalue = n == 0 ? double.NaN : eig.Values[0];
            var clipped = new double[n];
            for (int k = 0; k < n; k++) clipped[k] = Math.Max(eig.Values[k], eigenvalueFloor);
            return eig.Reconstruct(clipped);
        }

        /// <summary>
        /// Nearest correlation matrix in the Frobenius norm by the alternating-projections method with Dykstra's
        /// correction (Higham, "Computing the nearest correlation matrix - a problem from finance", IMA Journal of
        /// Numerical Analysis 22, 2002, algorithm 3.3): alternately project on the positive semi-definite cone
        /// (eigenvalue clipping) and on the unit-diagonal matrices, carrying the Dykstra increment of the cone
        /// projection. Converges linearly to the unique solution.
        /// </summary>
        /// <param name="a">Symmetric (its symmetric part is used) matrix with finite entries; the diagonal is replaced by 1.</param>
        /// <param name="tolerance">Stop when the relative change of the iterate in the Frobenius norm is below this.</param>
        /// <param name="maxIterations">Iteration limit.</param>
        /// <param name="eigenvalueFloor">Eigenvalue floor of the cone projection; 0 (default) is the exact problem.
        /// A small positive value (e.g. 1e-8) yields a strictly positive definite result.</param>
        /// <remarks>
        /// The returned matrix is the last unit-diagonal iterate. Its smallest eigenvalue can be a few units of
        /// <c>tolerance</c> negative; if so it is removed by the convex combination <c>(Y + d I) / (1 + d)</c>,
        /// which keeps the unit diagonal and moves the matrix by O(d) only.
        /// </remarks>
        public static NearestCorrelationResult Correlation(double[,] a, double tolerance = 1e-12,
            int maxIterations = 200, double eigenvalueFloor = 0.0)
        {
            Matrix.RequireSquare(a, nameof(a));
            if (!Matrix.IsFinite(a)) throw new ArgumentException("Matrix entries must be finite.", nameof(a));
            if (!(tolerance > 0.0)) throw new ArgumentOutOfRangeException(nameof(tolerance));
            if (maxIterations < 1) throw new ArgumentOutOfRangeException(nameof(maxIterations));
            if (!(eigenvalueFloor >= 0.0 && eigenvalueFloor < 1.0)) throw new ArgumentOutOfRangeException(nameof(eigenvalueFloor));

            int n = a.GetLength(0);
            double[,] input = Matrix.Symmetrize(a);
            double[,] y = (double[,])input.Clone(); // Dykstra must start from the point being projected (Y_0 = A)
            var dS = new double[n, n];
            bool converged = n <= 1;
            int iterations = 0;
            var r = new double[n, n];
            while (!converged && iterations < maxIterations)
            {
                iterations++;
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                        r[i, j] = y[i, j] - dS[i, j];
                double[,] x = PositiveSemidefinite(r, eigenvalueFloor, out _);
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                        dS[i, j] = x[i, j] - r[i, j];
                var yNew = (double[,])x.Clone();
                for (int i = 0; i < n; i++) yNew[i, i] = 1.0;
                converged = Matrix.FrobeniusDistance(yNew, y) <= tolerance * Math.Max(1.0, Matrix.FrobeniusNorm(yNew));
                y = yNew;
            }

            if (n > 0)
            {
                double minEig = SymmetricEigen.Decompose(y).Values[0];
                if (minEig < eigenvalueFloor)
                {
                    double d = (eigenvalueFloor - minEig) / (1.0 - eigenvalueFloor);
                    for (int i = 0; i < n; i++)
                        for (int j = 0; j < n; j++)
                            y[i, j] = (y[i, j] + (i == j ? d : 0.0)) / (1.0 + d);
                }
            }
            return new NearestCorrelationResult(y, iterations, converged, Matrix.FrobeniusDistance(input, y));
        }
    }
}
