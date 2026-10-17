using System;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Models.Diffusion;

namespace InFine.QuantFinance.Payoffs.Tests
{
    /// <summary>
    /// Correlated multi-asset Black-Scholes paths from Philox draws (<see cref="McPaths.Normals"/>) through the host
    /// reference of the diffusion kernel (<see cref="CorrelatedLogNormalMath.DiffusePath"/>), without any Hybridizer type.
    /// Path p uses the first <c>steps * D</c> normals of the sub-stream (seed, p, <see cref="McPaths.NormalStream"/>),
    /// asset d of step t taking draw <c>t * D + d</c>.
    /// </summary>
    public sealed class MultiAssetPaths
    {
        readonly double[] _log0, _sigma, _dividend, _lower, _dt, _rates, _normals, _logPath;
        readonly int _steps;

        public MultiAssetPaths(double[] spots, double[] sigma, double[] dividend, double[,] correlation, double rate, double[] dates)
        {
            AssetCount = spots.Length;
            _log0 = new double[AssetCount];
            for (int d = 0; d < AssetCount; ++d) _log0[d] = Math.Log(spots[d]);
            _sigma = sigma;
            _dividend = dividend;
            _lower = CholeskyCorrelation.FromCorrelation(correlation).Lower;
            _steps = dates.Length - 1;
            _dt = new double[_steps];
            _rates = new double[_steps];
            for (int t = 0; t < _steps; ++t) { _dt[t] = dates[t + 1] - dates[t]; _rates[t] = rate; }
            _normals = new double[_steps * AssetCount];
            _logPath = new double[(_steps + 1) * AssetCount];
            Dates = dates;
            Discounts = McPaths.Discounts(dates, rate);
        }

        public int AssetCount { get; }
        public double[] Dates { get; }
        public double[] Discounts { get; }

        /// <summary>Spots of path <paramref name="pathIndex"/>, asset d at pillar t in <c>path[t * D + d]</c>.</summary>
        public void Fill(uint pathIndex, double[] path)
        {
            McPaths.Normals(pathIndex, McPaths.NormalStream, _normals, _normals.Length);
            CorrelatedLogNormalMath.DiffusePath(_log0, _sigma, _dividend, _lower, AssetCount, _dt, _rates, _steps, _normals, _logPath);
            for (int k = 0; k < _logPath.Length; ++k) path[k] = Math.Exp(_logPath[k]);
        }

        public double[] NewPath() => new double[(_steps + 1) * AssetCount];

        /// <summary>Asset <paramref name="asset"/> of a multi-asset path as a one-asset path.</summary>
        public double[] Column(double[] path, int asset)
        {
            var single = new double[_steps + 1];
            for (int t = 0; t <= _steps; ++t) single[t] = path[t * AssetCount + asset];
            return single;
        }

        public static double[,] Uniform(int n, double rho)
        {
            var c = new double[n, n];
            for (int i = 0; i < n; ++i)
                for (int j = 0; j < n; ++j)
                    c[i, j] = i == j ? 1.0 : rho;
            return c;
        }

        public static double[] Flatten(double[,] m)
        {
            int n = m.GetLength(0);
            var r = new double[n * n];
            for (int i = 0; i < n; ++i)
                for (int j = 0; j < n; ++j)
                    r[i * n + j] = m[i, j];
            return r;
        }

        public static double[] Repeat(int n, double value)
        {
            var r = new double[n];
            for (int i = 0; i < n; ++i) r[i] = value;
            return r;
        }
    }
}
