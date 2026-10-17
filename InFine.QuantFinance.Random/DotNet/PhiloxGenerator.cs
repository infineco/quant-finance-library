using InFine.QuantFinance.ClosedForm;
using System;

namespace InFine.QuantFinance.Random.DotNet
{
    /// <summary>
    /// Host-side sequential view of one Philox4x32-10 sub-stream (seed, path index, stream).
    /// </summary>
    /// <remarks>
    /// The generator only keeps a position, counted in 32-bit words: word i of the sub-stream is
    /// lane i % 4 of <see cref="Philox4x32x10.GenerateBlock"/>(Seed, i / 4, PathIndex, Stream).
    /// Every value is therefore a pure function of its position, so generating N values at once gives
    /// exactly the same values as generating them in arbitrary chunks, and <see cref="Skip"/> is O(1).
    /// A kernel reproduces this generator's output by calling the same static functions.
    /// <list type="bullet">
    /// <item><see cref="UniformTransformEnum.UNBIAISED"/>: one word per draw, (x + 0.5) * 2^-32.</item>
    /// <item><see cref="UniformTransformEnum.STANDARD"/>: two words per draw (first word = high bits),
    /// 52-bit resolution, (k + 0.5) * 2^-52.</item>
    /// <item><see cref="GaussianTransformEnum.BoxMuller"/> / <see cref="GaussianTransformEnum.BoxMuller2"/>:
    /// two words per pair of normals (cos, sin); count must be even.</item>
    /// <item><see cref="GaussianTransformEnum.InverseCumulativeDistributionFunction"/>: two words per normal
    /// (first word = high bits), z = <see cref="NormalDistribution.InverseCdf"/>(<see cref="Philox4x32x10.ToUniformOpen52"/>(hi, lo)).
    /// The 52-bit uniform keeps the far tails (|z| up to ~8.2) instead of truncating at ~6.3 with one 32-bit word;
    /// count may be odd.</item>
    /// </list>
    /// All uniforms lie strictly inside (0, 1) before scaling to [leftbound, rightbound].
    /// </remarks>
    public sealed class PhiloxGenerator : BaseGenerator
    {
        private ulong _cachedBlock;
        private bool _cacheValid;
        private uint _w0, _w1, _w2, _w3;

        /// <param name="seed">64-bit Philox key (key0 = low word, key1 = high word).</param>
        /// <param name="pathIndex">Path (simulation) index of the sub-stream, counter word c1.</param>
        /// <param name="stream">User stream index of the sub-stream, counter word c2.</param>
        public PhiloxGenerator(ulong seed, uint pathIndex = 0, uint stream = 0)
        {
            Seed = seed;
            PathIndex = pathIndex;
            Stream = stream;
        }

        /// <summary>64-bit Philox key.</summary>
        public ulong Seed { get; }

        /// <summary>Path (simulation) index of the sub-stream.</summary>
        public uint PathIndex { get; }

        /// <summary>User stream index of the sub-stream.</summary>
        public uint Stream { get; }

        /// <summary>Index of the next 32-bit word to be consumed.</summary>
        public ulong Position { get; set; }

        /// <summary>Advances the position by <paramref name="words"/> 32-bit words.</summary>
        public void Skip(ulong words)
        {
            Position += words;
        }

        /// <summary>Returns the next raw 32-bit word and advances the position by one.</summary>
        public uint NextUInt32()
        {
            ulong position = Position;
            ulong block = position >> 2;
            if (!_cacheValid || block != _cachedBlock)
            {
                Philox4x32x10.GenerateBlock(Seed, block, PathIndex, Stream, out _w0, out _w1, out _w2, out _w3);
                _cachedBlock = block;
                _cacheValid = true;
            }
            Position = position + 1;
            switch ((int)(position & 3))
            {
                case 0: return _w0;
                case 1: return _w1;
                case 2: return _w2;
                default: return _w3;
            }
        }

        public override void GenerateUniforms(UniformTransformEnum transform, double leftbound, double rightbound, int count, double[] target)
        {
            CheckTarget(count, target);
            double width = rightbound - leftbound;
            switch (transform)
            {
                case UniformTransformEnum.UNBIAISED:
                    for (int k = 0; k < count; ++k)
                        target[k] = Philox4x32x10.ToUniformOpen32(NextUInt32()) * width + leftbound;
                    return;
                case UniformTransformEnum.STANDARD:
                    for (int k = 0; k < count; ++k)
                    {
                        uint hi = NextUInt32();
                        uint lo = NextUInt32();
                        target[k] = Philox4x32x10.ToUniformOpen52(hi, lo) * width + leftbound;
                    }
                    return;
            }
            throw new NotSupportedException($"Uniform transform {transform} is not supported by {nameof(PhiloxGenerator)}.");
        }

        public override void GenerateGaussians(GaussianTransformEnum transform, double mean, double stdev, int count, double[] target)
        {
            CheckTarget(count, target);
            switch (transform)
            {
                case GaussianTransformEnum.BoxMuller:
                case GaussianTransformEnum.BoxMuller2:
                    if ((count & 1) != 0) throw new ArgumentException("Expecting even number of samples", nameof(count));
                    for (int k = 0; k < count; k += 2)
                    {
                        uint a = NextUInt32();
                        uint b = NextUInt32();
                        Philox4x32x10.BoxMuller(a, b, out double z0, out double z1);
                        target[k] = z0 * stdev + mean;
                        target[k + 1] = z1 * stdev + mean;
                    }
                    return;
                case GaussianTransformEnum.InverseCumulativeDistributionFunction:
                    for (int k = 0; k < count; ++k)
                    {
                        uint hi = NextUInt32();
                        uint lo = NextUInt32();
                        target[k] = NormalDistribution.InverseCdf(Philox4x32x10.ToUniformOpen52(hi, lo)) * stdev + mean;
                    }
                    return;
            }
            throw new NotSupportedException($"Gaussian transform {transform} is not supported by {nameof(PhiloxGenerator)}.");
        }

        private static void CheckTarget(int count, double[] target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (count < 0 || count > target.Length) throw new ArgumentOutOfRangeException(nameof(count));
        }
    }
}
