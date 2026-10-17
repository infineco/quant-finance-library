using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.ClosedForm;
using System;

namespace InFine.QuantFinance.Random.QuasiRandom
{
    /// <summary>
    /// 32-bit Sobol low-discrepancy sequence (Sobol 1967; Antonov-Saleev Gray-code ordering;
    /// direction numbers of Joe and Kuo 2008, <see cref="JoeKuoDirectionNumbers"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Direction numbers: for dimension d (0-based) the 32 direction integers
    /// v_1..v_32 (v_k = m_k 2^(32-k) for k &lt;= s, then the Bratley-Fox recurrence) are stored in a flat
    /// array at <c>directions[d * 32 + (k - 1)]</c>; <see cref="CreateDirectionNumbers"/> builds it once on the host,
    /// and it is the only data the kernel-side functions need.
    /// </para>
    /// <para>
    /// Ordering: point n (n = 0, 1, 2, ...) is x_n = XOR of v_k over the bits k of the Gray code
    /// g(n) = n ^ (n &gt;&gt; 1). This is the order produced by Joe and Kuo's sobol.cc
    /// (dimension 1: 0, 0.5, 0.75, 0.25, 0.375, ...; dimension 2: 0, 0.5, 0.25, 0.75, 0.375, ...), and
    /// consecutive points differ by one XOR: x_n = x_(n-1) ^ v_(c+1), c = number of trailing zeros of n
    /// (<see cref="NextPoint"/>). Because <see cref="Point"/> is a pure function of (n, d), every GPU thread or
    /// Monte Carlo path can compute its own point directly; the host <see cref="SobolGenerator"/> walks the
    /// same sequence incrementally.
    /// </para>
    /// <para>
    /// Point 0 is the origin. For every m the first 2^m points (n = 0 .. 2^m - 1) form a (t, m, s)-net:
    /// in particular every one-dimensional projection has exactly one point in each interval
    /// [j 2^-m, (j + 1) 2^-m). At most 2^32 points exist (indices are <c>uint</c>).
    /// </para>
    /// <para>
    /// Gaussians must be obtained by inversion, <see cref="ToGaussian"/> = Phi^-1(<see cref="ToUniformOpen"/>(x)),
    /// never by Box-Muller: Box-Muller couples two coordinates through (sqrt(-2 ln u1), 2 pi u2), which maps the
    /// stratified 2-D structure of the net onto a polar grid, destroys the one-dimensional stratification of each
    /// coordinate and the ordering by importance of dimensions, and is discontinuous in u2. Inversion is monotone
    /// and acts coordinate by coordinate, so each Gaussian keeps the equidistribution of its own Sobol dimension.
    /// </para>
    /// <para>
    /// Everything marked <c>[Kernel]</c> is allocation-free and exception-free.
    /// </para>
    /// </remarks>
    public static class Sobol
    {
        /// <summary>Number of bits of each Sobol coordinate (and of the direction integers).</summary>
        public const int Bits = 32;

        /// <summary>Largest dimension count supported by the embedded direction numbers.</summary>
        public const int MaxDimension = JoeKuoDirectionNumbers.MaxDimension;

        const double TwoPowMinus32 = 2.3283064365386962890625e-10; // 2^-32, exact

        /// <summary>
        /// Builds the flat direction-number table for dimensions 0 .. <paramref name="dimensions"/> - 1:
        /// <c>result[d * 32 + k]</c> = v_(k+1) of dimension d, as a 32-bit integer (the binary fraction v_(k+1) * 2^32).
        /// </summary>
        public static uint[] CreateDirectionNumbers(int dimensions)
        {
            if (dimensions < 1 || dimensions > MaxDimension)
                throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, $"Sobol dimensions must be in [1, {MaxDimension}].");

            uint[] v = new uint[dimensions * Bits];
            // Dimension 0: van der Corput, m_k = 1 for all k.
            for (int k = 0; k < Bits; ++k)
                v[k] = 1u << (Bits - 1 - k);

            int[] packed = JoeKuoDirectionNumbers.Packed;
            int pos = 0;
            for (int d = 1; d < dimensions; ++d)
            {
                int s = packed[pos];
                int a = packed[pos + 1];
                int baseIndex = d * Bits;
                // v_k = m_k << (32 - k), k = 1..s (0-based slot k-1)
                for (int k = 1; k <= s && k <= Bits; ++k)
                    v[baseIndex + k - 1] = (uint)packed[pos + 1 + k] << (Bits - k);
                // v_k = v_(k-s) ^ (v_(k-s) >> s) ^ XOR_(j=1..s-1) a_j v_(k-j), with a_j = bit (s-1-j) of a
                for (int k = s + 1; k <= Bits; ++k)
                {
                    uint vk = v[baseIndex + k - s - 1];
                    vk ^= vk >> s;
                    for (int j = 1; j < s; ++j)
                    {
                        if (((a >> (s - 1 - j)) & 1) != 0)
                            vk ^= v[baseIndex + k - j - 1];
                    }
                    v[baseIndex + k - 1] = vk;
                }
                pos += 2 + s;
            }
            return v;
        }

        /// <summary>
        /// Coordinate <paramref name="dimension"/> (0-based) of point <paramref name="index"/>, as a 32-bit integer
        /// (the binary fraction x * 2^32). Random access in O(log2 index); <paramref name="index"/> 0 is the origin.
        /// </summary>
        [Kernel]
        public static uint Point(uint index, int dimension, uint[] directions)
        {
            uint gray = index ^ (index >> 1);
            uint x = 0u;
            int k = dimension * Bits;
            while (gray != 0u)
            {
                if ((gray & 1u) != 0u)
                    x ^= directions[k];
                gray >>= 1;
                ++k;
            }
            return x;
        }

        /// <summary>
        /// Gray-code step: coordinate <paramref name="dimension"/> of point <paramref name="index"/>, given the same
        /// coordinate of point <paramref name="index"/> - 1. <paramref name="index"/> must be at least 1.
        /// </summary>
        [Kernel]
        public static uint NextPoint(uint previous, uint index, int dimension, uint[] directions)
        {
            int c = 0;
            uint n = index;
            while ((n & 1u) == 0u && c < Bits - 1)
            {
                n >>= 1;
                ++c;
            }
            return previous ^ directions[dimension * Bits + c];
        }

        /// <summary>The binary fraction x * 2^-32, in [0, 1): the classical Sobol coordinate (0.5, 0.75, ...).</summary>
        [Kernel]
        public static double ToUniform(uint x)
        {
            return (double)x * TwoPowMinus32;
        }

        /// <summary>
        /// Centre of the 2^-32 cell of x: (x + 0.5) * 2^-32, in [2^-33, 1 - 2^-33], never 0 or 1. The shift by half a
        /// cell keeps every point inside its own dyadic interval, so stratification is unchanged.
        /// </summary>
        [Kernel]
        public static double ToUniformOpen(uint x)
        {
            return ((double)x + 0.5) * TwoPowMinus32;
        }

        /// <summary>
        /// Standard normal by inversion: Phi^-1(<see cref="ToUniformOpen"/>(x)), with
        /// <see cref="NormalDistribution.InverseCdf"/> (AS241). Finite for every x: |z| &lt;= Phi^-1(1 - 2^-33) ~ 6.34.
        /// </summary>
        [Kernel]
        public static double ToGaussian(uint x)
        {
            return NormalDistribution.InverseCdf(ToUniformOpen(x));
        }

        /// <summary>
        /// Coordinate <paramref name="dimension"/> of point <paramref name="index"/>, XOR-ed with a digital shift
        /// (0 for the plain sequence, see <see cref="SobolScrambling"/>), mapped to (0, 1) by <see cref="ToUniformOpen"/>.
        /// </summary>
        [Kernel]
        public static double Uniform(uint index, int dimension, uint[] directions, uint shift)
        {
            return ToUniformOpen(Point(index, dimension, directions) ^ shift);
        }

        /// <summary>
        /// Coordinate <paramref name="dimension"/> of point <paramref name="index"/>, XOR-ed with a digital shift
        /// (0 for the plain sequence), mapped to a standard normal by <see cref="ToGaussian"/>.
        /// </summary>
        [Kernel]
        public static double Gaussian(uint index, int dimension, uint[] directions, uint shift)
        {
            return ToGaussian(Point(index, dimension, directions) ^ shift);
        }
    }
}
