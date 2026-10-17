using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Random.DotNet
{
    /// <summary>
    /// Philox4x32-10 counter-based random number generator (Salmon, Moraes, Dror, Shaw,
    /// "Parallel random numbers: as easy as 1, 2, 3", SC'11; reference implementation Random123).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generator is a pure function (key, counter) -> 4 x uint32: there is no state, so the same
    /// inputs give bit-identical words on .NET, AVX and CUDA, and every Monte Carlo path can draw its
    /// own sub-stream by index without sharing or skipping anything. Everything here is
    /// kernel-compatible: no allocation, no exceptions, only 32x32->64 bit integer multiplies.
    /// </para>
    /// <para>
    /// Counter layout used by <see cref="Counter"/> (Random123 word order, c0 is the low word):
    /// <list type="bullet">
    /// <item><c>c0</c> = low 32 bits of the block index (block = drawIndex / 4, 4 words per block)</item>
    /// <item><c>c1</c> = path (simulation) index</item>
    /// <item><c>c2</c> = stream index (user tag: asset, time-step family, scenario...)</item>
    /// <item><c>c3</c> = high 32 bits of the block index</item>
    /// </list>
    /// The map (block, path, stream) -> counter is a bijection onto the 128-bit counter space, so
    /// two distinct (path, stream) sub-streams never overlap, and each sub-stream holds 2^64 blocks
    /// (2^66 words) before it would wrap onto itself.
    /// </para>
    /// <para>
    /// Uniform conversion: <see cref="ToUniformOpen32"/> maps one word to (x + 0.5) * 2^-32, which lies in
    /// [2^-33, 1 - 2^-33], is exact in double and never 0 or 1; this is the default, one word per draw.
    /// <see cref="ToUniformOpen52"/> builds a finer uniform from two words (52 random bits,
    /// (k + 0.5) * 2^-52) for callers who need more resolution near 0 and 1. 53 bits are not used
    /// because (2^53 - 0.5) * 2^-53 rounds to 1.0 in double.
    /// </para>
    /// </remarks>
    public static class Philox4x32x10
    {
        /// <summary>Round multiplier applied to words 0 and 1 (Random123 PHILOX_M4x32_0).</summary>
        public const uint M0 = 0xD2511F53u;
        /// <summary>Round multiplier applied to words 2 and 3 (Random123 PHILOX_M4x32_1).</summary>
        public const uint M1 = 0xCD9E8D57u;
        /// <summary>Weyl key increment for key word 0 (golden ratio, Random123 PHILOX_W32_0).</summary>
        public const uint W0 = 0x9E3779B9u;
        /// <summary>Weyl key increment for key word 1 (sqrt(3) - 1, Random123 PHILOX_W32_1).</summary>
        public const uint W1 = 0xBB67AE85u;

        const double TwoPowMinus32 = 2.3283064365386962890625e-10; // 2^-32, exact
        const double TwoPowMinus52 = 2.220446049250313080847263336181640625e-16; // 2^-52, exact
        const double TwoPi = 6.283185307179586476925286766559;

        /// <summary>
        /// Philox4x32 with 10 rounds: encrypts the 128-bit counter (c0..c3) under the 64-bit key
        /// (key0, key1). Bit-identical to Random123 <c>philox4x32(ctr, key)</c> with R = 10.
        /// </summary>
        [Kernel]
        public static void Generate(uint key0, uint key1, uint c0, uint c1, uint c2, uint c3,
                                    out uint r0, out uint r1, out uint r2, out uint r3)
        {
            uint x0 = c0, x1 = c1, x2 = c2, x3 = c3;
            uint k0 = key0, k1 = key1;

            // 10 rounds; the key is bumped between rounds (9 bumps), as in Random123.
            for (int round = 0; round < 10; ++round)
            {
                if (round > 0)
                {
                    k0 += W0;
                    k1 += W1;
                }
                ulong p0 = (ulong)M0 * x0;
                ulong p1 = (ulong)M1 * x2;
                uint hi0 = (uint)(p0 >> 32), lo0 = (uint)p0;
                uint hi1 = (uint)(p1 >> 32), lo1 = (uint)p1;
                uint y0 = hi1 ^ x1 ^ k0;
                uint y2 = hi0 ^ x3 ^ k1;
                x0 = y0;
                x1 = lo1;
                x2 = y2;
                x3 = lo0;
            }

            r0 = x0;
            r1 = x1;
            r2 = x2;
            r3 = x3;
        }

        /// <summary>
        /// Builds the counter for block <paramref name="block"/> (= drawIndex / 4) of the sub-stream
        /// (<paramref name="pathIndex"/>, <paramref name="stream"/>). See the class remarks for the layout.
        /// </summary>
        [Kernel]
        public static void Counter(ulong block, uint pathIndex, uint stream,
                                   out uint c0, out uint c1, out uint c2, out uint c3)
        {
            c0 = (uint)block;
            c1 = pathIndex;
            c2 = stream;
            c3 = (uint)(block >> 32);
        }

        /// <summary>
        /// Words 4*block .. 4*block+3 of the sub-stream (pathIndex, stream) under the 64-bit seed
        /// (key0 = low word of seed, key1 = high word).
        /// </summary>
        [Kernel]
        public static void GenerateBlock(ulong seed, ulong block, uint pathIndex, uint stream,
                                         out uint r0, out uint r1, out uint r2, out uint r3)
        {
            uint c0, c1, c2, c3;
            Counter(block, pathIndex, stream, out c0, out c1, out c2, out c3);
            Generate((uint)seed, (uint)(seed >> 32), c0, c1, c2, c3, out r0, out r1, out r2, out r3);
        }

        /// <summary>Uniform in the open interval (0, 1) from one word: (x + 0.5) * 2^-32.</summary>
        [Kernel]
        public static double ToUniformOpen32(uint x)
        {
            return ((double)x + 0.5) * TwoPowMinus32;
        }

        /// <summary>
        /// Uniform in the open interval (0, 1) with 52 random bits from two words:
        /// k = (hi &gt;&gt; 12) * 2^32 + lo, u = (k + 0.5) * 2^-52, in [2^-53, 1 - 2^-53].
        /// </summary>
        [Kernel]
        public static double ToUniformOpen52(uint hi, uint lo)
        {
            ulong k = ((ulong)(hi >> 12) << 32) | lo;
            return ((double)k + 0.5) * TwoPowMinus52;
        }

        /// <summary>
        /// Box-Muller transform of two words into two independent standard normals:
        /// r = sqrt(-2 ln u1), z0 = r cos(2 pi u2), z1 = r sin(2 pi u2), with u = <see cref="ToUniformOpen32"/>.
        /// Since u1 &gt;= 2^-33, |z| &lt;= sqrt(66 ln 2) ~ 6.76 (tail mass beyond that is ~1.4e-11).
        /// </summary>
        [Kernel]
        public static void BoxMuller(uint a, uint b, out double z0, out double z1)
        {
            double u1 = ToUniformOpen32(a);
            double u2 = ToUniformOpen32(b);
            double r = Math.Sqrt(-2.0 * Math.Log(u1));
            double theta = TwoPi * u2;
            z0 = r * Math.Cos(theta);
            z1 = r * Math.Sin(theta);
        }
    }
}
