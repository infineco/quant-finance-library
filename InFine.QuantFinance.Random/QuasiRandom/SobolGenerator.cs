using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Random.DotNet;
using System;

namespace InFine.QuantFinance.Random.QuasiRandom
{
    /// <summary>
    /// Host-side sequential Sobol generator: walks the Gray-code sequence of <see cref="Sobol"/> one point at a time,
    /// optionally randomised (<see cref="SobolScrambling"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Index"/> is the raw index n of the next point; point n has coordinate d equal to
    /// <see cref="Sobol.Point"/>(n, d, <see cref="Directions"/>) XOR <see cref="Shifts"/>[d], so a kernel reproduces this
    /// generator bit for bit from the two arrays. Setting <see cref="Index"/> or calling <see cref="Skip"/> is O(1);
    /// the next point is then recomputed by random access, and consecutive points by one XOR per coordinate.
    /// </para>
    /// <para>
    /// First-point convention: by default the generator starts at n = 1, skipping the origin (which is a
    /// degenerate point: every coordinate is 0, mapped to the extreme Gaussian quantile Phi^-1(2^-33) ~ -6.34).
    /// Pass <c>skipZeroPoint: false</c> to start at n = 0. Owen (2020, "On dropping the first Sobol' point")
    /// shows that dropping the origin breaks the net property of blocks of 2^m points, which costs convergence
    /// rate under scrambling; with <see cref="SobolScramblingKind.DigitalShift"/> or
    /// <see cref="SobolScramblingKind.LinearMatrixAndDigitalShift"/> the origin is an ordinary random point, so
    /// prefer <c>skipZeroPoint: false</c> and sample sizes that are powers of two.
    /// </para>
    /// <para>
    /// <see cref="BaseGenerator"/> entry points fill <c>count</c> values point-major (point n: dimensions 0..D-1,
    /// then point n + 1, ...); <c>count</c> must be a multiple of <see cref="Dimensions"/>. Uniforms:
    /// <see cref="UniformTransformEnum.STANDARD"/> returns the classical value x 2^-32 in [0, 1),
    /// <see cref="UniformTransformEnum.UNBIAISED"/> the open value (x + 0.5) 2^-32 (<see cref="Sobol.ToUniformOpen"/>).
    /// Gaussians: only <see cref="GaussianTransformEnum.InverseCumulativeDistributionFunction"/> is supported
    /// (see <see cref="Sobol"/> for why Box-Muller is wrong for quasi-random points).
    /// </para>
    /// </remarks>
    public sealed class SobolGenerator : BaseGenerator
    {
        /// <summary>Total number of points of the 32-bit sequence (indices 0 .. 2^32 - 1).</summary>
        public const long PointCount = 1L << 32;

        private readonly uint[] _state; // unshifted coordinates of point _stateIndex
        private long _stateIndex = -1;  // -1: state must be recomputed by random access
        private long _index;

        /// <param name="dimensions">Number of dimensions, 1 .. <see cref="Sobol.MaxDimension"/>.</param>
        /// <param name="scrambling">Randomisation of the point set.</param>
        /// <param name="seed">Philox seed of the randomisation (ignored when <paramref name="scrambling"/> is None).</param>
        /// <param name="replication">Independent replication index of the randomisation.</param>
        /// <param name="skipZeroPoint">Start at n = 1 (default) instead of n = 0, see remarks.</param>
        public SobolGenerator(int dimensions, SobolScramblingKind scrambling = SobolScramblingKind.None,
                              ulong seed = 0, uint replication = 0, bool skipZeroPoint = true)
        {
            uint[] directions = Sobol.CreateDirectionNumbers(dimensions);
            switch (scrambling)
            {
                case SobolScramblingKind.None:
                    Shifts = new uint[dimensions];
                    break;
                case SobolScramblingKind.DigitalShift:
                    Shifts = SobolScrambling.CreateDigitalShifts(dimensions, seed, replication);
                    break;
                case SobolScramblingKind.LinearMatrixAndDigitalShift:
                    directions = SobolScrambling.ScrambleDirectionNumbers(directions, seed, replication);
                    Shifts = SobolScrambling.CreateDigitalShifts(dimensions, seed, replication);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scrambling));
            }
            Dimensions = dimensions;
            Scrambling = scrambling;
            Directions = directions;
            _state = new uint[dimensions];
            _index = skipZeroPoint ? 1 : 0;
        }

        /// <summary>Number of dimensions of each point.</summary>
        public int Dimensions { get; }

        /// <summary>Randomisation applied to the sequence.</summary>
        public SobolScramblingKind Scrambling { get; }

        /// <summary>Flat direction numbers (scrambled if requested), <c>[d * 32 + k]</c>; pass to kernels, do not modify.</summary>
        public uint[] Directions { get; }

        /// <summary>Digital shift of each dimension (all zero when unscrambled); pass to kernels, do not modify.</summary>
        public uint[] Shifts { get; }

        /// <summary>Raw index n of the next point.</summary>
        public long Index
        {
            get => _index;
            set
            {
                if (value < 0 || value > PointCount) throw new ArgumentOutOfRangeException(nameof(value));
                _index = value;
            }
        }

        /// <summary>Skips <paramref name="points"/> points (O(1)).</summary>
        public void Skip(long points)
        {
            if (points < 0) throw new ArgumentOutOfRangeException(nameof(points));
            Index = _index + points;
        }

        /// <summary>Writes the next point as 32-bit integers (shift applied) and advances by one point.</summary>
        public void NextPoint(Span<uint> point)
        {
            if (point.Length < Dimensions) throw new ArgumentException("Span shorter than the dimension count.", nameof(point));
            Advance();
            for (int d = 0; d < Dimensions; ++d)
                point[d] = _state[d] ^ Shifts[d];
        }

        /// <summary>Writes the next point mapped to (0, 1) by <see cref="Sobol.ToUniformOpen"/>.</summary>
        public void NextUniforms(Span<double> point)
        {
            if (point.Length < Dimensions) throw new ArgumentException("Span shorter than the dimension count.", nameof(point));
            Advance();
            for (int d = 0; d < Dimensions; ++d)
                point[d] = Sobol.ToUniformOpen(_state[d] ^ Shifts[d]);
        }

        /// <summary>Writes the next point mapped to standard normals by inversion (<see cref="Sobol.ToGaussian"/>).</summary>
        public void NextGaussians(Span<double> point)
        {
            if (point.Length < Dimensions) throw new ArgumentException("Span shorter than the dimension count.", nameof(point));
            Advance();
            for (int d = 0; d < Dimensions; ++d)
                point[d] = Sobol.ToGaussian(_state[d] ^ Shifts[d]);
        }

        public override void GenerateUniforms(UniformTransformEnum transform, double leftbound, double rightbound, int count, double[] target)
        {
            CheckTarget(count, target);
            if (transform != UniformTransformEnum.STANDARD && transform != UniformTransformEnum.UNBIAISED)
                throw new NotSupportedException($"Uniform transform {transform} is not supported by {nameof(SobolGenerator)}.");
            double width = rightbound - leftbound;
            for (int start = 0; start < count; start += Dimensions)
            {
                Advance();
                for (int d = 0; d < Dimensions; ++d)
                {
                    uint x = _state[d] ^ Shifts[d];
                    double u = transform == UniformTransformEnum.STANDARD ? Sobol.ToUniform(x) : Sobol.ToUniformOpen(x);
                    target[start + d] = u * width + leftbound;
                }
            }
        }

        public override void GenerateGaussians(GaussianTransformEnum transform, double mean, double stdev, int count, double[] target)
        {
            CheckTarget(count, target);
            if (transform != GaussianTransformEnum.InverseCumulativeDistributionFunction)
                throw new NotSupportedException(
                    $"{nameof(SobolGenerator)} only supports inverse-CDF Gaussians: Box-Muller mixes two Sobol coordinates " +
                    "non-monotonically and destroys their low-discrepancy structure.");
            for (int start = 0; start < count; start += Dimensions)
            {
                Advance();
                for (int d = 0; d < Dimensions; ++d)
                    target[start + d] = NormalDistribution.InverseCdf(Sobol.ToUniformOpen(_state[d] ^ Shifts[d])) * stdev + mean;
            }
        }

        // Brings _state to point _index and advances _index by one.
        private void Advance()
        {
            long n = _index;
            if (n >= PointCount)
                throw new InvalidOperationException("The 32-bit Sobol sequence is exhausted (2^32 points).");
            uint un = (uint)n;
            if (_stateIndex >= 0 && n == _stateIndex + 1)
            {
                for (int d = 0; d < Dimensions; ++d)
                    _state[d] = Sobol.NextPoint(_state[d], un, d, Directions);
            }
            else if (n != _stateIndex)
            {
                for (int d = 0; d < Dimensions; ++d)
                    _state[d] = Sobol.Point(un, d, Directions);
            }
            _stateIndex = n;
            _index = n + 1;
        }

        private void CheckTarget(int count, double[] target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (count < 0 || count > target.Length) throw new ArgumentOutOfRangeException(nameof(count));
            if (count % Dimensions != 0)
                throw new ArgumentException($"Expecting a multiple of the dimension count ({Dimensions}).", nameof(count));
        }
    }
}
