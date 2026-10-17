using System;

namespace InFine.QuantFinance.Numerics
{
    /// <summary>
    /// Small dense-matrix helpers on <c>double[,]</c> (row-major in the CLR sense: <c>a[row, column]</c>) used by the
    /// linear-algebra routines of this project and by the portfolio-risk code built on them. Host-side: validation
    /// throws and results are allocated. Only what those callers need is here; this is not a general matrix library.
    /// </summary>
    public static class Matrix
    {
        /// <summary>Returns the n x n identity.</summary>
        public static double[,] Identity(int n)
        {
            if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
            var result = new double[n, n];
            for (int i = 0; i < n; i++) result[i, i] = 1.0;
            return result;
        }

        /// <summary>Returns the transpose of <paramref name="a"/>.</summary>
        public static double[,] Transpose(double[,] a)
        {
            ArgumentNullException.ThrowIfNull(a);
            int rows = a.GetLength(0), cols = a.GetLength(1);
            var result = new double[cols, rows];
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++)
                    result[j, i] = a[i, j];
            return result;
        }

        /// <summary>Returns the product <c>a b</c>; the inner dimensions must agree.</summary>
        public static double[,] Multiply(double[,] a, double[,] b)
        {
            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(b);
            int m = a.GetLength(0), k = a.GetLength(1), n = b.GetLength(1);
            if (b.GetLength(0) != k)
                throw new ArgumentException($"Cannot multiply a {m}x{k} matrix by a {b.GetLength(0)}x{n} matrix.");
            var result = new double[m, n];
            for (int i = 0; i < m; i++)
                for (int p = 0; p < k; p++)
                {
                    double aip = a[i, p];
                    if (aip == 0.0) continue;
                    for (int j = 0; j < n; j++) result[i, j] += aip * b[p, j];
                }
            return result;
        }

        /// <summary>Returns <c>a a^T</c> (symmetric by construction).</summary>
        public static double[,] MultiplyByTranspose(double[,] a)
        {
            ArgumentNullException.ThrowIfNull(a);
            int m = a.GetLength(0), k = a.GetLength(1);
            var result = new double[m, m];
            for (int i = 0; i < m; i++)
                for (int j = 0; j <= i; j++)
                {
                    double sum = 0.0;
                    for (int p = 0; p < k; p++) sum += a[i, p] * a[j, p];
                    result[i, j] = sum;
                    result[j, i] = sum;
                }
            return result;
        }

        /// <summary>Frobenius norm <c>sqrt(sum a_ij^2)</c>, scaled to avoid overflow and underflow.</summary>
        public static double FrobeniusNorm(double[,] a)
        {
            ArgumentNullException.ThrowIfNull(a);
            int rows = a.GetLength(0), cols = a.GetLength(1);
            double scale = 0.0;
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++)
                    scale = Math.Max(scale, Math.Abs(a[i, j]));
            if (scale == 0.0 || double.IsNaN(scale) || double.IsInfinity(scale)) return scale;
            double sum = 0.0;
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++)
                {
                    double x = a[i, j] / scale;
                    sum += x * x;
                }
            return scale * Math.Sqrt(sum);
        }

        /// <summary>Frobenius norm of <c>a - b</c>; the two matrices must have the same shape.</summary>
        public static double FrobeniusDistance(double[,] a, double[,] b)
        {
            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(b);
            int rows = a.GetLength(0), cols = a.GetLength(1);
            if (b.GetLength(0) != rows || b.GetLength(1) != cols)
                throw new ArgumentException("The matrices must have the same shape.");
            var diff = new double[rows, cols];
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++)
                    diff[i, j] = a[i, j] - b[i, j];
            return FrobeniusNorm(diff);
        }

        /// <summary>Largest <c>|a_ij - a_ji|</c> of a square matrix.</summary>
        public static double MaxAsymmetry(double[,] a)
        {
            RequireSquare(a, nameof(a));
            int n = a.GetLength(0);
            double max = 0.0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < i; j++)
                    max = Math.Max(max, Math.Abs(a[i, j] - a[j, i]));
            return max;
        }

        /// <summary>Returns <c>(a + a^T) / 2</c>, the nearest symmetric matrix in the Frobenius norm.</summary>
        public static double[,] Symmetrize(double[,] a)
        {
            RequireSquare(a, nameof(a));
            int n = a.GetLength(0);
            var result = new double[n, n];
            for (int i = 0; i < n; i++)
            {
                result[i, i] = a[i, i];
                for (int j = 0; j < i; j++)
                {
                    double v = 0.5 * (a[i, j] + a[j, i]);
                    result[i, j] = v;
                    result[j, i] = v;
                }
            }
            return result;
        }

        /// <summary>True when every entry is a finite number.</summary>
        public static bool IsFinite(double[,] a)
        {
            ArgumentNullException.ThrowIfNull(a);
            foreach (double x in a)
                if (!double.IsFinite(x)) return false;
            return true;
        }

        internal static void RequireSquare(double[,] a, string name)
        {
            ArgumentNullException.ThrowIfNull(a, name);
            if (a.GetLength(0) != a.GetLength(1))
                throw new ArgumentException($"Matrix must be square, got {a.GetLength(0)}x{a.GetLength(1)}.", name);
        }
    }
}
