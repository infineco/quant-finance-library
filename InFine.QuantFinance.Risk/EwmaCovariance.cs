using System;
using InFine.QuantFinance.Numerics;

namespace InFine.QuantFinance.Risk
{
    /// <summary>
    /// Exponentially weighted moving-average covariance, the RiskMetrics estimator (J.P. Morgan/Reuters,
    /// RiskMetrics Technical Document, 4th ed., 1996):
    /// <c>S_t = lambda S_{t-1} + (1 - lambda) r_t r_t^T</c>, with the conventional <c>lambda = 0.94</c> for daily data
    /// (0.97 monthly). Streaming: feed one return vector at a time with <see cref="Update"/>, or run a whole history
    /// with <see cref="Estimate"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns are taken as zero-mean, as in RiskMetrics: at daily horizons the mean is negligible next to the
    /// volatility and not estimating it removes noise. Demean first (for example with
    /// <see cref="Covariance.Mean"/>) if you want the centred variant.
    /// </para>
    /// <para>
    /// Initialisation. The recursion needs a starting matrix. Two choices are supported, and both give the same
    /// recursive and batch results when applied alike:
    /// (1) <b>first outer product</b> (default, no seed): <c>S_1 = r_1 r_1^T</c>. It uses the first observation
    /// only, so early estimates are rank one and noisy and the effect of the start decays as <c>lambda^t</c>
    /// (about 0.94^60 = 2.5% after 60 days); (2) <b>seeded</b>: <c>S</c> starts at a supplied matrix, typically the
    /// zero-mean sample covariance of a warm-up window (<c>Estimate(..., seedWindow: k)</c>, or
    /// <see cref="Initialize"/> for a prior from a longer history). Prefer the seeded start whenever a warm-up
    /// window exists: the first <c>~1/(1-lambda)</c> estimates are then well-conditioned.
    /// </para>
    /// </remarks>
    public sealed class EwmaCovariance
    {
        /// <summary>RiskMetrics decay factor for daily data.</summary>
        public const double DefaultLambda = 0.94;

        readonly double[,] _cov;
        bool _initialised;

        /// <summary>Creates an estimator for <paramref name="dimension"/> assets, not yet initialised.</summary>
        /// <param name="dimension">Number of assets, at least 1.</param>
        /// <param name="lambda">Decay factor in (0, 1); closer to 1 means longer memory.</param>
        public EwmaCovariance(int dimension, double lambda = DefaultLambda)
        {
            if (dimension < 1) throw new ArgumentOutOfRangeException(nameof(dimension));
            if (!(lambda > 0.0 && lambda < 1.0)) throw new ArgumentOutOfRangeException(nameof(lambda), "Lambda must be in (0, 1).");
            Dimension = dimension;
            Lambda = lambda;
            _cov = new double[dimension, dimension];
        }

        /// <summary>Number of assets.</summary>
        public int Dimension { get; }

        /// <summary>Decay factor.</summary>
        public double Lambda { get; }

        /// <summary>Number of return vectors consumed by <see cref="Update"/> (a seed is not counted).</summary>
        public long Count { get; private set; }

        /// <summary>True once a seed was supplied or the first return vector was consumed.</summary>
        public bool IsInitialised => _initialised;

        /// <summary>Sets the starting covariance (symmetric, finite, non-negative diagonal); copies it.</summary>
        public void Initialize(double[,] seed)
        {
            ArgumentNullException.ThrowIfNull(seed);
            if (seed.GetLength(0) != Dimension || seed.GetLength(1) != Dimension)
                throw new ArgumentException($"Seed must be {Dimension}x{Dimension}.", nameof(seed));
            if (!Matrix.IsFinite(seed)) throw new ArgumentException("Seed must be finite.", nameof(seed));
            for (int i = 0; i < Dimension; i++)
            {
                if (seed[i, i] < 0.0) throw new ArgumentException("Seed has a negative variance.", nameof(seed));
                for (int j = 0; j < Dimension; j++) _cov[i, j] = 0.5 * (seed[i, j] + seed[j, i]);
            }
            _initialised = true;
        }

        /// <summary>
        /// Consumes one return vector. The very first call on an uninitialised estimator sets
        /// <c>S = r r^T</c>; every other call applies the recursion.
        /// </summary>
        public void Update(double[] returns)
        {
            ArgumentNullException.ThrowIfNull(returns);
            if (returns.Length != Dimension) throw new ArgumentException($"Expected {Dimension} returns.", nameof(returns));
            foreach (double r in returns)
                if (!double.IsFinite(r)) throw new ArgumentException("Returns must be finite.", nameof(returns));
            double keep = _initialised ? Lambda : 0.0;
            double add = _initialised ? 1.0 - Lambda : 1.0;
            for (int i = 0; i < Dimension; i++)
                for (int j = 0; j <= i; j++)
                {
                    double v = keep * _cov[i, j] + add * returns[i] * returns[j];
                    _cov[i, j] = v;
                    _cov[j, i] = v;
                }
            _initialised = true;
            Count++;
        }

        /// <summary>Returns a copy of the current covariance. Throws if nothing was fed yet.</summary>
        public double[,] GetCovariance()
        {
            if (!_initialised) throw new InvalidOperationException("The estimator has no data yet; call Initialize or Update.");
            return (double[,])_cov.Clone();
        }

        /// <summary>
        /// EWMA covariance after running the whole history.
        /// </summary>
        /// <param name="returns">T x N returns, rows = dates oldest first.</param>
        /// <param name="lambda">Decay factor in (0, 1).</param>
        /// <param name="seedWindow">0 starts at the first outer product. A value k in [1, T] seeds with the zero-mean
        /// sample covariance <c>(1/k) sum_{t&lt;=k} r_t r_t^T</c> of the first k rows and recurses over the rest.</param>
        public static double[,] Estimate(double[,] returns, double lambda = DefaultLambda, int seedWindow = 0)
        {
            Covariance.RequireReturns(returns, 1);
            int t = returns.GetLength(0), n = returns.GetLength(1);
            if (seedWindow < 0 || seedWindow > t) throw new ArgumentOutOfRangeException(nameof(seedWindow));
            var ewma = new EwmaCovariance(n, lambda);
            int start = 0;
            if (seedWindow > 0)
            {
                var head = new double[seedWindow, n];
                for (int i = 0; i < seedWindow; i++)
                    for (int j = 0; j < n; j++)
                        head[i, j] = returns[i, j];
                ewma.Initialize(Covariance.Moment(head, seedWindow, n, seedWindow));
                start = seedWindow;
            }
            var row = new double[n];
            for (int i = start; i < t; i++)
            {
                for (int j = 0; j < n; j++) row[j] = returns[i, j];
                ewma.Update(row);
            }
            return ewma.GetCovariance();
        }
    }
}
