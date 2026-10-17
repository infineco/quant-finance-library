using System;

namespace InFine.QuantFinance.Numerics
{
    /// <summary>Pivoted Cholesky factorisation <c>P^T A P = L L^T</c> of a positive semi-definite matrix.</summary>
    public sealed class PivotedCholeskyResult
    {
        internal PivotedCholeskyResult(double[,] l, int[] permutation, int rank)
        {
            L = l;
            Permutation = permutation;
            Rank = rank;
        }

        /// <summary>Lower-triangular factor, n x n; columns at and beyond <see cref="Rank"/> are zero.</summary>
        public double[,] L { get; }

        /// <summary>
        /// Pivot order: <c>(L L^T)[i, j]</c> approximates <c>A[Permutation[i], Permutation[j]]</c>.
        /// </summary>
        public int[] Permutation { get; }

        /// <summary>Numerical rank: the number of pivots above the tolerance.</summary>
        public int Rank { get; }

        /// <summary>Returns <c>L L^T</c> mapped back to the original ordering, i.e. the factorised matrix.</summary>
        public double[,] Reconstruct()
        {
            int n = L.GetLength(0);
            double[,] llt = Matrix.MultiplyByTranspose(L);
            var result = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    result[Permutation[i], Permutation[j]] = llt[i, j];
            return result;
        }
    }

    /// <summary>Outcome of <see cref="Cholesky.Robust"/>.</summary>
    public sealed class RobustCholeskyResult
    {
        internal RobustCholeskyResult(double[,] l, bool repaired, double minEigenvalue, double adjustment)
        {
            L = l;
            Repaired = repaired;
            MinEigenvalue = minEigenvalue;
            Adjustment = adjustment;
        }

        /// <summary>Lower-triangular factor of the (possibly repaired) matrix.</summary>
        public double[,] L { get; }

        /// <summary>True when the input was not numerically positive definite and was repaired first.</summary>
        public bool Repaired { get; }

        /// <summary>Smallest eigenvalue of the input when a repair was needed, otherwise <see cref="double.NaN"/>.</summary>
        public double MinEigenvalue { get; }

        /// <summary>Frobenius distance between the input and <c>L L^T</c> when repaired, otherwise 0.</summary>
        public double Adjustment { get; }
    }

    /// <summary>
    /// Cholesky factorisation <c>A = L L^T</c> of symmetric positive (semi-)definite matrices, with failure
    /// detection, a rank-revealing pivoted variant and a robust variant that repairs an indefinite input. Host-side.
    /// </summary>
    public static class Cholesky
    {
        /// <summary>
        /// Factorises a symmetric positive definite matrix. Only the lower triangle of <paramref name="a"/> is read
        /// (as in LAPACK <c>dpotrf</c>). Returns false, with <paramref name="l"/> partially filled, when a pivot is not
        /// strictly positive or not finite, i.e. when the matrix is not numerically positive definite.
        /// </summary>
        /// <remarks>O(n^3 / 3) flops, one n x n allocation.</remarks>
        public static bool TryFactor(double[,] a, out double[,] l)
        {
            Matrix.RequireSquare(a, nameof(a));
            int n = a.GetLength(0);
            l = new double[n, n];
            for (int j = 0; j < n; j++)
            {
                double d = a[j, j];
                for (int k = 0; k < j; k++) d -= l[j, k] * l[j, k];
                if (!(d > 0.0) || double.IsInfinity(d)) return false;
                double ljj = Math.Sqrt(d);
                l[j, j] = ljj;
                for (int i = j + 1; i < n; i++)
                {
                    double s = a[i, j];
                    for (int k = 0; k < j; k++) s -= l[i, k] * l[j, k];
                    l[i, j] = s / ljj;
                }
            }
            return true;
        }

        /// <summary>Like <see cref="TryFactor"/> but throws <see cref="ArgumentException"/> if the matrix is not positive definite.</summary>
        public static double[,] Factor(double[,] a)
        {
            if (!TryFactor(a, out double[,] l))
                throw new ArgumentException("Matrix is not positive definite; use Cholesky.Robust or PivotedFactor.", nameof(a));
            return l;
        }

        /// <summary>
        /// Rank-revealing Cholesky with complete diagonal pivoting (Higham, "Accuracy and Stability of Numerical
        /// Algorithms", algorithm 10.9; LAPACK <c>dpstrf</c>). Works for positive semi-definite matrices, where the
        /// plain factorisation breaks down. Only the lower triangle is read.
        /// </summary>
        /// <param name="a">Symmetric positive semi-definite matrix.</param>
        /// <param name="tolerance">Stop when the largest remaining Schur-complement diagonal falls to
        /// <c>tolerance * max(diag(a))</c> or below; null selects <c>n * epsilon</c>.</param>
        public static PivotedCholeskyResult PivotedFactor(double[,] a, double? tolerance = null)
        {
            Matrix.RequireSquare(a, nameof(a));
            int n = a.GetLength(0);
            double tol = tolerance ?? n * 2.220446049250313e-16;
            var perm = new int[n];
            var d = new double[n];
            double dmax = 0.0;
            for (int i = 0; i < n; i++)
            {
                perm[i] = i;
                d[i] = a[i, i];
                dmax = Math.Max(dmax, d[i]);
            }
            var l = new double[n, n];
            int rank = 0;
            for (int k = 0; k < n; k++)
            {
                int pivot = k;
                for (int j = k + 1; j < n; j++)
                    if (d[j] > d[pivot]) pivot = j;
                if (!(d[pivot] > tol * dmax)) break;
                if (pivot != k)
                {
                    (perm[k], perm[pivot]) = (perm[pivot], perm[k]);
                    (d[k], d[pivot]) = (d[pivot], d[k]);
                    for (int j = 0; j < k; j++) (l[k, j], l[pivot, j]) = (l[pivot, j], l[k, j]);
                }
                double lkk = Math.Sqrt(d[k]);
                l[k, k] = lkk;
                for (int i = k + 1; i < n; i++)
                {
                    int pi = perm[i], pk = perm[k];
                    double s = pi >= pk ? a[pi, pk] : a[pk, pi];
                    for (int j = 0; j < k; j++) s -= l[i, j] * l[k, j];
                    double lik = s / lkk;
                    l[i, k] = lik;
                    d[i] -= lik * lik;
                }
                rank++;
            }
            return new PivotedCholeskyResult(l, perm, rank);
        }

        /// <summary>
        /// Factorises <paramref name="a"/> even when it is slightly indefinite (typical of a covariance or correlation
        /// matrix assembled from asynchronous data, shrunk by hand, or stressed). The plain factorisation is tried
        /// first; if it fails the symmetric part of the matrix is repaired by eigenvalue clipping (the nearest
        /// positive semi-definite matrix in the Frobenius norm with eigenvalues floored at
        /// <c>relativeFloor * max|eigenvalue|</c>, Higham 1988) and factorised instead. If rounding still defeats the
        /// factorisation the floor is multiplied by 10, up to 8 times.
        /// </summary>
        /// <param name="a">Square matrix; its symmetric part is used.</param>
        /// <param name="relativeFloor">Eigenvalue floor relative to the spectral radius, default 1e-10.</param>
        /// <remarks>
        /// A well-conditioned input costs one O(n^3 / 3) factorisation. A repair adds a Jacobi eigen-decomposition
        /// (see <see cref="SymmetricEigen"/>: O(n^3) per sweep, fine for n up to a few hundred).
        /// </remarks>
        public static RobustCholeskyResult Robust(double[,] a, double relativeFloor = 1e-10)
        {
            Matrix.RequireSquare(a, nameof(a));
            if (!Matrix.IsFinite(a)) throw new ArgumentException("Matrix entries must be finite.", nameof(a));
            if (!(relativeFloor >= 0.0)) throw new ArgumentOutOfRangeException(nameof(relativeFloor));
            int n = a.GetLength(0);
            double[,] sym = Matrix.Symmetrize(a);
            if (TryFactor(sym, out double[,] l)) return new RobustCholeskyResult(l, false, double.NaN, 0.0);

            SymmetricEigenDecomposition eig = SymmetricEigen.Decompose(sym);
            double minEig = eig.Values[0];
            double scale = Math.Max(Math.Abs(eig.Values[0]), Math.Abs(eig.Values[n - 1]));
            if (scale == 0.0) return new RobustCholeskyResult(new double[n, n], false, double.NaN, 0.0);

            double floor = Math.Max(relativeFloor, 1e-16) * scale;
            for (int attempt = 0; attempt < 9; attempt++, floor *= 10.0)
            {
                var clipped = new double[n];
                for (int k = 0; k < n; k++) clipped[k] = Math.Max(eig.Values[k], floor);
                double[,] repaired = eig.Reconstruct(clipped);
                if (TryFactor(repaired, out l))
                    return new RobustCholeskyResult(l, true, minEig, Matrix.FrobeniusDistance(sym, repaired));
            }
            throw new InvalidOperationException("Cholesky.Robust could not repair the matrix.");
        }
    }
}
