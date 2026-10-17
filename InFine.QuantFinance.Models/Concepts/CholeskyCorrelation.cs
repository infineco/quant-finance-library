using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Numerics;
using System;

namespace InFine.QuantFinance.Models.Concepts
{
    /// <summary>
    /// Correlates independent standard normal draws with the lower Cholesky factor of a correlation matrix: for a
    /// vector <c>z</c> of D independent N(0, 1) draws, <c>y = L z</c> is Gaussian with covariance <c>L L^T = C</c>, the
    /// target correlation matrix (a Gaussian copula with unit-variance margins).
    /// <para>
    /// Kernel side: the factor is a flat row-major D x D array, <c>L[i * D + j]</c>, zero above the diagonal.
    /// <see cref="Component"/> and <see cref="CorrelateInPlace"/> allocate nothing, never throw and only read the lower
    /// triangle, so they are callable from <c>[Kernel]</c> code; the multi-asset diffusion
    /// (<see cref="Diffusion.CorrelatedLogNormalDiffusion{Rate, LogSpot, Brownian, Schedule}"/>) evaluates the same sums,
    /// in the same order, on its Brownian storage.
    /// </para>
    /// <para>
    /// Host side: <see cref="FromCorrelation"/> validates a correlation matrix and factorises it. A positive definite
    /// matrix gets its plain Cholesky factor; a positive semi-definite one (perfect correlation, an asset repeated, a
    /// rank-deficient factor model) gets the unpivoted semi-definite factor, in which a zero pivot leaves a zero column,
    /// so that, for instance, two perfectly correlated assets are driven by exactly the same draw. An indefinite matrix
    /// is repaired by <see cref="Cholesky.Robust"/> (eigenvalue clipping) only when the caller allows it.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Replaces the former <c>InFine.QuantFinance.Random.GaussianCopula</c> / <c>ICorrelator</c> stub, which went through
    /// an unfinished tensor reduction and had no factor. It lives here rather than in <c>InFine.QuantFinance.Random</c>
    /// because the diffusion kernel of this assembly calls it and <c>Models</c> does not reference <c>Random</c>.
    /// </remarks>
    public sealed class CholeskyCorrelation
    {
        /// <summary>Tolerance on the symmetry, the unit diagonal and the semi-definite reconstruction of the input.</summary>
        public const double Tolerance = 1e-12;

        CholeskyCorrelation(int dimension, double[] lower, bool repaired, double adjustment)
        {
            Dimension = dimension;
            Lower = lower;
            Repaired = repaired;
            Adjustment = adjustment;
        }

        /// <summary>Number of correlated variables D.</summary>
        public int Dimension { get; }

        /// <summary>Row-major lower-triangular factor, <c>L[i * D + j]</c> (zero for j &gt; i). Shared, do not modify.</summary>
        public double[] Lower { get; }

        /// <summary>True when the input was indefinite and was repaired before factorisation.</summary>
        public bool Repaired { get; }

        /// <summary>Frobenius distance between the input and <c>L L^T</c> when repaired, otherwise 0.</summary>
        public double Adjustment { get; }

        /// <summary>The identity factor: D independent variables.</summary>
        public static CholeskyCorrelation Independent(int dimension)
        {
            if (dimension < 1) throw new ArgumentOutOfRangeException(nameof(dimension), "At least one variable is required.");
            var lower = new double[dimension * dimension];
            for (int i = 0; i < dimension; ++i)
                lower[i * dimension + i] = 1.0;
            return new CholeskyCorrelation(dimension, lower, false, 0.0);
        }

        /// <summary>
        /// Factorises a correlation matrix (symmetric, unit diagonal, entries in [-1, 1]).
        /// </summary>
        /// <param name="correlation">D x D correlation matrix.</param>
        /// <param name="repairIndefinite">
        /// When the matrix is not positive semi-definite: false (default) throws, true factorises the nearest positive
        /// semi-definite matrix with eigenvalues floored (<see cref="Cholesky.Robust"/>), whose diagonal is then close to,
        /// but not exactly, one.
        /// </param>
        /// <exception cref="ArgumentException">The matrix is not a correlation matrix, or is indefinite and not repaired.</exception>
        public static CholeskyCorrelation FromCorrelation(double[,] correlation, bool repairIndefinite = false)
        {
            if (correlation == null) throw new ArgumentNullException(nameof(correlation));
            int n = correlation.GetLength(0);
            if (n < 1 || correlation.GetLength(1) != n)
                throw new ArgumentException("The correlation matrix must be square and non-empty.", nameof(correlation));
            for (int i = 0; i < n; ++i)
            {
                if (Math.Abs(correlation[i, i] - 1.0) > Tolerance)
                    throw new ArgumentException($"Diagonal entry {i} is {correlation[i, i]}, not 1.", nameof(correlation));
                for (int j = 0; j < i; ++j)
                {
                    double c = correlation[i, j];
                    if (double.IsNaN(c) || Math.Abs(c) > 1.0 + Tolerance)
                        throw new ArgumentException($"Entry ({i}, {j}) = {c} is not a correlation.", nameof(correlation));
                    if (Math.Abs(c - correlation[j, i]) > Tolerance)
                        throw new ArgumentException($"The matrix is not symmetric at ({i}, {j}).", nameof(correlation));
                }
            }

            var lower = new double[n * n];
            if (TrySemiDefiniteFactor(correlation, n, lower))
                return new CholeskyCorrelation(n, lower, false, 0.0);
            if (!repairIndefinite)
                throw new ArgumentException("The correlation matrix is not positive semi-definite; pass repairIndefinite: true to use the nearest one.", nameof(correlation));

            RobustCholeskyResult robust = Cholesky.Robust(correlation);
            for (int i = 0; i < n; ++i)
                for (int j = 0; j <= i; ++j)
                    lower[i * n + j] = robust.L[i, j];
            return new CholeskyCorrelation(n, lower, robust.Repaired, robust.Adjustment);
        }

        /// <summary>The correlation matrix <c>L L^T</c> actually used.</summary>
        public double[,] Reconstruct()
        {
            int n = Dimension;
            var c = new double[n, n];
            for (int i = 0; i < n; ++i)
                for (int j = 0; j < n; ++j)
                {
                    double s = 0.0;
                    for (int k = 0; k <= Math.Min(i, j); ++k)
                        s += Lower[i * n + k] * Lower[j * n + k];
                    c[i, j] = s;
                }
            return c;
        }

        /// <summary>
        /// Unpivoted Cholesky that accepts zero pivots: a pivot within <see cref="Tolerance"/> of zero gives a zero column,
        /// provided the remaining entries of that column of the Schur complement vanish too (otherwise the matrix is
        /// indefinite and false is returned).
        /// </summary>
        static bool TrySemiDefiniteFactor(double[,] a, int n, double[] l)
        {
            for (int j = 0; j < n; ++j)
            {
                double d = a[j, j];
                for (int k = 0; k < j; ++k) d -= l[j * n + k] * l[j * n + k];
                if (d < -Tolerance || double.IsNaN(d)) return false;
                if (d <= Tolerance)
                {
                    l[j * n + j] = 0.0;
                    for (int i = j + 1; i < n; ++i)
                    {
                        double s = a[i, j];
                        for (int k = 0; k < j; ++k) s -= l[i * n + k] * l[j * n + k];
                        if (Math.Abs(s) > 1e-8) return false;
                        l[i * n + j] = 0.0;
                    }
                    continue;
                }
                double ljj = Math.Sqrt(d);
                l[j * n + j] = ljj;
                for (int i = j + 1; i < n; ++i)
                {
                    double s = a[i, j];
                    for (int k = 0; k < j; ++k) s -= l[i * n + k] * l[j * n + k];
                    l[i * n + j] = s / ljj;
                }
            }
            return true;
        }

        /// <summary>
        /// Component <paramref name="row"/> of <c>L z</c>: <c>sum_{j &lt;= row} L[row * D + j] z[offset + j]</c>, summed in
        /// increasing j.
        /// </summary>
        /// <param name="lower">Row-major lower factor, D x D.</param>
        /// <param name="dimension">D.</param>
        /// <param name="row">Component index, in [0, D).</param>
        /// <param name="z">Independent standard normals, D of them from <paramref name="offset"/>.</param>
        /// <param name="offset">Index of z_0 in <paramref name="z"/>.</param>
        [Kernel]
        public static double Component(double[] lower, int dimension, int row, double[] z, int offset)
        {
            double s = 0.0;
            int baseIndex = row * dimension;
            for (int j = 0; j <= row; ++j)
                s += lower[baseIndex + j] * z[offset + j];
            return s;
        }

        /// <summary>
        /// Replaces <c>z[offset .. offset + D)</c> by <c>L z</c>. Rows are processed from the last to the first, so each
        /// row only reads entries that are still independent draws; no scratch is needed.
        /// </summary>
        [Kernel]
        public static void CorrelateInPlace(double[] lower, int dimension, double[] z, int offset)
        {
            for (int row = dimension - 1; row >= 0; --row)
                z[offset + row] = Component(lower, dimension, row, z, offset);
        }
    }
}
