using System;

namespace InFine.QuantFinance.Numerics
{
    /// <summary>
    /// Householder QR factorisation <c>A = Q R</c> of an m x n matrix with m &gt;= n, used to solve linear least-squares
    /// problems without forming the normal equations (which square the condition number). Host-side: validates with
    /// exceptions and allocates.
    /// </summary>
    /// <remarks>
    /// <c>Q</c> is never formed: it is kept as the n Householder reflectors <c>H_k = I - 2 v_k v_k^T / (v_k^T v_k)</c>
    /// (stored below the diagonal of the working copy) and applied to right-hand sides on demand. Cost is
    /// <c>2 m n^2 - 2 n^3 / 3</c> flops. Rank deficiency is detected from the diagonal of <c>R</c>.
    /// </remarks>
    public sealed class QrDecomposition
    {
        readonly double[,] _qr;      // R above the diagonal, Householder vector v_k in column k from row k down
        readonly double[] _beta;     // 2 / (v^T v) for every reflector
        readonly double[] _rDiag;

        /// <summary>Factorises <paramref name="a"/> (not modified).</summary>
        public QrDecomposition(double[,] a)
        {
            ArgumentNullException.ThrowIfNull(a);
            Rows = a.GetLength(0);
            Columns = a.GetLength(1);
            if (Columns == 0 || Rows < Columns) throw new ArgumentException("QR needs rows >= columns >= 1.", nameof(a));
            _qr = (double[,])a.Clone();
            _beta = new double[Columns];
            _rDiag = new double[Columns];
            double maxDiag = 0.0;
            for (int k = 0; k < Columns; k++)
            {
                // Norm with scaling to avoid overflow, then the reflector sending column k to -sign(x0)*||x|| e_k.
                double scale = 0.0;
                for (int i = k; i < Rows; i++) scale = Math.Max(scale, Math.Abs(_qr[i, k]));
                double norm = 0.0;
                if (scale > 0.0)
                {
                    double s = 0.0;
                    for (int i = k; i < Rows; i++) { double t = _qr[i, k] / scale; s += t * t; }
                    norm = scale * Math.Sqrt(s);
                }
                if (norm == 0.0) { _rDiag[k] = 0.0; _beta[k] = 0.0; continue; }
                double alpha = _qr[k, k] >= 0.0 ? -norm : norm;   // R[k,k]
                _qr[k, k] -= alpha;                                // v = x - alpha e_k
                double vv = 0.0;
                for (int i = k; i < Rows; i++) vv += _qr[i, k] * _qr[i, k];
                _beta[k] = 2.0 / vv;
                for (int j = k + 1; j < Columns; j++)
                {
                    double dot = 0.0;
                    for (int i = k; i < Rows; i++) dot += _qr[i, k] * _qr[i, j];
                    dot *= _beta[k];
                    for (int i = k; i < Rows; i++) _qr[i, j] -= dot * _qr[i, k];
                }
                _rDiag[k] = alpha;
                maxDiag = Math.Max(maxDiag, Math.Abs(alpha));
            }
            double tol = Math.Max(Rows, Columns) * 2.220446049250313e-16 * maxDiag;
            IsFullRank = true;
            for (int k = 0; k < Columns; k++) if (Math.Abs(_rDiag[k]) <= tol) IsFullRank = false;
        }

        /// <summary>Number of rows m.</summary>
        public int Rows { get; }

        /// <summary>Number of columns n.</summary>
        public int Columns { get; }

        /// <summary>False when some diagonal entry of R is below <c>max(m, n) * eps * max|R_kk|</c> (numerically rank deficient).</summary>
        public bool IsFullRank { get; }

        /// <summary>The n x n upper-triangular factor R.</summary>
        public double[,] R()
        {
            var r = new double[Columns, Columns];
            for (int i = 0; i < Columns; i++)
            {
                r[i, i] = _rDiag[i];
                for (int j = i + 1; j < Columns; j++) r[i, j] = _qr[i, j];
            }
            return r;
        }

        /// <summary>Computes <c>Q^T b</c> (length m) for a right-hand side of length m.</summary>
        public double[] ApplyQTranspose(double[] b)
        {
            ArgumentNullException.ThrowIfNull(b);
            if (b.Length != Rows) throw new ArgumentException("Right-hand side length must equal the number of rows.", nameof(b));
            var y = (double[])b.Clone();
            for (int k = 0; k < Columns; k++)
            {
                if (_beta[k] == 0.0) continue;
                double dot = 0.0;
                for (int i = k; i < Rows; i++) dot += _qr[i, k] * y[i];
                dot *= _beta[k];
                for (int i = k; i < Rows; i++) y[i] -= dot * _qr[i, k];
            }
            return y;
        }

        /// <summary>Least-squares solution of <c>min ||A x - b||_2</c>; throws when A is rank deficient.</summary>
        public double[] Solve(double[] b)
        {
            if (!IsFullRank) throw new InvalidOperationException("Matrix is numerically rank deficient.");
            double[] y = ApplyQTranspose(b);
            var x = new double[Columns];
            for (int i = Columns - 1; i >= 0; i--)
            {
                double sum = y[i];
                for (int j = i + 1; j < Columns; j++) sum -= _qr[i, j] * x[j];
                x[i] = sum / _rDiag[i];
            }
            return x;
        }

        /// <summary>
        /// <c>(A^T A)^{-1} = R^{-1} R^{-T}</c> (n x n, symmetric), obtained from the triangular factor only; throws when A is
        /// rank deficient.
        /// </summary>
        public double[,] NormalMatrixInverse()
        {
            if (!IsFullRank) throw new InvalidOperationException("Matrix is numerically rank deficient.");
            int n = Columns;
            var rinv = new double[n, n];
            for (int j = 0; j < n; j++)
            {
                rinv[j, j] = 1.0 / _rDiag[j];
                for (int i = j - 1; i >= 0; i--)
                {
                    double sum = 0.0;
                    for (int k = i + 1; k <= j; k++) sum += _qr[i, k] * rinv[k, j];
                    rinv[i, j] = -sum / _rDiag[i];
                }
            }
            var inv = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = i; j < n; j++)
                {
                    double sum = 0.0;
                    for (int k = j; k < n; k++) sum += rinv[i, k] * rinv[j, k];
                    inv[i, j] = sum;
                    inv[j, i] = sum;
                }
            return inv;
        }
    }
}
