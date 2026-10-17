using System;

namespace InFine.QuantFinance.Numerics
{
    /// <summary>
    /// Eigen-decomposition <c>A = V diag(values) V^T</c> of a real symmetric matrix. <see cref="Values"/> are in
    /// ascending order and column <c>k</c> of <see cref="Vectors"/> is the unit eigenvector of <c>Values[k]</c>.
    /// </summary>
    public sealed class SymmetricEigenDecomposition
    {
        internal SymmetricEigenDecomposition(double[] values, double[,] vectors, int sweeps, bool converged)
        {
            Values = values;
            Vectors = vectors;
            Sweeps = sweeps;
            Converged = converged;
        }

        /// <summary>Eigenvalues, ascending.</summary>
        public double[] Values { get; }

        /// <summary>Orthonormal eigenvectors stored as columns.</summary>
        public double[,] Vectors { get; }

        /// <summary>Number of Jacobi sweeps performed.</summary>
        public int Sweeps { get; }

        /// <summary>False when the sweep limit was hit before the off-diagonal part vanished to tolerance.</summary>
        public bool Converged { get; }

        /// <summary>Rebuilds <c>V diag(values) V^T</c> with the eigenvalues replaced by <paramref name="values"/>.</summary>
        public double[,] Reconstruct(double[] values)
        {
            ArgumentNullException.ThrowIfNull(values);
            int n = Values.Length;
            if (values.Length != n) throw new ArgumentException("Wrong number of eigenvalues.", nameof(values));
            var result = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j <= i; j++)
                {
                    double sum = 0.0;
                    for (int k = 0; k < n; k++) sum += Vectors[i, k] * values[k] * Vectors[j, k];
                    result[i, j] = sum;
                    result[j, i] = sum;
                }
            return result;
        }
    }

    /// <summary>
    /// Symmetric eigenvalue problem by the cyclic Jacobi method (Rutishauser's stable rotation formulas, as in
    /// Golub and Van Loan, "Matrix Computations", section 8.5, and Numerical Recipes section 11.1). Host-side.
    /// </summary>
    /// <remarks>
    /// Jacobi is chosen for robustness and simplicity, not speed: every eigenvalue and eigenvector is obtained to
    /// high relative accuracy and the code is short enough to audit. One sweep rotates the <c>n(n-1)/2</c> off-diagonal
    /// pairs, each rotation costing O(n), so a sweep is O(n^3) (about <c>3 n^3</c> flops of rotations and the same for
    /// the vectors) and the method converges quadratically in a handful (typically 6 to 10) of sweeps. That is
    /// comfortable for covariance matrices up to a few hundred assets (n = 500 takes seconds); beyond that, a
    /// tridiagonal QL/QR or divide-and-conquer solver is the right tool. Memory: two n x n arrays.
    /// </remarks>
    public static class SymmetricEigen
    {
        /// <summary>
        /// Decomposes the symmetric part <c>(a + a^T) / 2</c> of <paramref name="a"/>; <paramref name="a"/> is not modified.
        /// </summary>
        /// <param name="a">Square matrix with finite entries.</param>
        /// <param name="tolerance">Stop when the off-diagonal Frobenius norm falls below <c>tolerance</c> times the
        /// Frobenius norm of the matrix.</param>
        /// <param name="maxSweeps">Sweep limit.</param>
        public static SymmetricEigenDecomposition Decompose(double[,] a, double tolerance = 1e-15, int maxSweeps = 60)
        {
            Matrix.RequireSquare(a, nameof(a));
            if (!Matrix.IsFinite(a)) throw new ArgumentException("Matrix entries must be finite.", nameof(a));
            if (!(tolerance >= 0.0)) throw new ArgumentOutOfRangeException(nameof(tolerance));
            if (maxSweeps < 1) throw new ArgumentOutOfRangeException(nameof(maxSweeps));

            int n = a.GetLength(0);
            double[,] w = Matrix.Symmetrize(a);
            double[,] v = Matrix.Identity(n);
            double norm = Matrix.FrobeniusNorm(w);

            int sweeps = 0;
            bool converged = n <= 1 || norm == 0.0;
            while (!converged && sweeps < maxSweeps)
            {
                double off = 0.0;
                for (int p = 0; p < n; p++)
                    for (int q = p + 1; q < n; q++)
                        off += w[p, q] * w[p, q];
                if (Math.Sqrt(2.0 * off) <= tolerance * norm)
                {
                    converged = true;
                    break;
                }
                sweeps++;
                for (int p = 0; p < n - 1; p++)
                    for (int q = p + 1; q < n; q++)
                        Rotate(w, v, n, p, q);
            }
            if (!converged)
            {
                double off = 0.0;
                for (int p = 0; p < n; p++)
                    for (int q = p + 1; q < n; q++)
                        off += w[p, q] * w[p, q];
                converged = Math.Sqrt(2.0 * off) <= tolerance * norm;
            }

            // Sort ascending (selection sort on n values, then one column permutation of the vectors).
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (x, y) => w[x, x].CompareTo(w[y, y]));
            var values = new double[n];
            var vectors = new double[n, n];
            for (int k = 0; k < n; k++)
            {
                values[k] = w[order[k], order[k]];
                for (int i = 0; i < n; i++) vectors[i, k] = v[i, order[k]];
            }
            return new SymmetricEigenDecomposition(values, vectors, sweeps, converged);
        }

        static void Rotate(double[,] w, double[,] v, int n, int p, int q)
        {
            double apq = w[p, q];
            if (apq == 0.0) return;
            double app = w[p, p], aqq = w[q, q];
            double theta = (aqq - app) / (2.0 * apq);
            double t = Math.Abs(theta) > 1e150
                ? 0.5 / theta
                : Math.Sign(theta == 0.0 ? 1.0 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
            double c = 1.0 / Math.Sqrt(t * t + 1.0);
            double s = t * c;

            w[p, p] = app - t * apq;
            w[q, q] = aqq + t * apq;
            w[p, q] = 0.0;
            w[q, p] = 0.0;
            for (int k = 0; k < n; k++)
            {
                if (k == p || k == q) continue;
                double akp = w[k, p], akq = w[k, q];
                double np = c * akp - s * akq;
                double nq = s * akp + c * akq;
                w[k, p] = np; w[p, k] = np;
                w[k, q] = nq; w[q, k] = nq;
            }
            for (int k = 0; k < n; k++)
            {
                double vkp = v[k, p], vkq = v[k, q];
                v[k, p] = c * vkp - s * vkq;
                v[k, q] = s * vkp + c * vkq;
            }
        }
    }
}
