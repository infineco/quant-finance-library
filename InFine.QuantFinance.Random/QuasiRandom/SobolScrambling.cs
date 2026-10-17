using InFine.QuantFinance.Random.DotNet;
using System;
using System.Numerics;

namespace InFine.QuantFinance.Random.QuasiRandom
{
    /// <summary>Randomisation applied to a Sobol sequence.</summary>
    public enum SobolScramblingKind
    {
        /// <summary>Plain (deterministic) Sobol sequence.</summary>
        None,

        /// <summary>Random digital shift: x' = x XOR e_d, one uniform 32-bit word e_d per dimension.</summary>
        DigitalShift,

        /// <summary>
        /// Matousek's random linear matrix scrambling followed by a random digital shift:
        /// x' = L_d x XOR e_d, with L_d a random lower-triangular binary matrix with unit diagonal.
        /// </summary>
        LinearMatrixAndDigitalShift,
    }

    /// <summary>
    /// Randomised quasi-Monte Carlo for <see cref="Sobol"/>: random digital shifts and Matousek's linear
    /// matrix scrambling, seeded through Philox4x32-10 so that replication r of seed s is reproducible on any device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each replication is an independent randomisation of the same point set, uniformly distributed on [0, 1)^d
    /// point by point, so averaging an estimator over R independent replications gives an unbiased estimate and
    /// the sample standard deviation across replications gives an honest error estimate (standard error / sqrt(R)).
    /// </para>
    /// <para>
    /// Digital shift: x' = x XOR e_d. It preserves the (t, m, s)-net structure of every block of 2^m points.
    /// Linear matrix scrambling (Matousek 1998, "On the L2-discrepancy for anchored boxes"): bit j of the result
    /// (j = 1 most significant) is bit j of x plus a random GF(2) combination of the more significant bits 1..j-1.
    /// It is the affine special case of Owen's nested scrambling with the same mean-square L2 discrepancy, also
    /// preserves the net structure, and costs nothing at run time: since L_d is linear over GF(2) it is applied once
    /// to the direction numbers (L_d (v_1 XOR v_2) = L_d v_1 XOR L_d v_2), and the kernels then run unchanged on the
    /// scrambled table with the shift. Full nested (Owen) scrambling is not implemented: it cannot be folded into
    /// the direction numbers and needs a per-point hash.
    /// </para>
    /// <para>
    /// Random words: for replication r and dimension d, the shift is word d of the Philox sub-stream
    /// (seed, path index = r, stream = <see cref="ShiftStream"/>); the 32 matrix rows of dimension d are words
    /// 32 d .. 32 d + 31 of the sub-stream (seed, r, <see cref="MatrixStream"/>).
    /// </para>
    /// </remarks>
    public static class SobolScrambling
    {
        /// <summary>Philox stream tag used for digital shifts ("SobS").</summary>
        public const uint ShiftStream = 0x536F6253u;

        /// <summary>Philox stream tag used for linear-matrix scrambling ("SobM").</summary>
        public const uint MatrixStream = 0x536F624Du;

        /// <summary>Digital shifts e_0 .. e_(dimensions-1) of replication <paramref name="replication"/>.</summary>
        public static uint[] CreateDigitalShifts(int dimensions, ulong seed, uint replication)
        {
            if (dimensions < 1) throw new ArgumentOutOfRangeException(nameof(dimensions));
            var philox = new PhiloxGenerator(seed, replication, ShiftStream);
            uint[] shifts = new uint[dimensions];
            for (int d = 0; d < dimensions; ++d)
                shifts[d] = philox.NextUInt32();
            return shifts;
        }

        /// <summary>
        /// Returns a copy of <paramref name="directions"/> (as built by <see cref="Sobol.CreateDirectionNumbers"/>)
        /// with Matousek's random lower-triangular matrix L_d of replication <paramref name="replication"/>
        /// applied to every direction integer of dimension d.
        /// </summary>
        public static uint[] ScrambleDirectionNumbers(uint[] directions, ulong seed, uint replication)
        {
            if (directions == null) throw new ArgumentNullException(nameof(directions));
            if (directions.Length == 0 || directions.Length % Sobol.Bits != 0)
                throw new ArgumentException("Expecting 32 direction integers per dimension.", nameof(directions));

            int dimensions = directions.Length / Sobol.Bits;
            var philox = new PhiloxGenerator(seed, replication, MatrixStream);
            uint[] rows = new uint[Sobol.Bits];
            uint[] result = new uint[directions.Length];
            for (int d = 0; d < dimensions; ++d)
            {
                // Row for output bit position p (31 = most significant): unit diagonal at p, random bits strictly
                // above p (more significant input bits), zeros below.
                for (int p = 0; p < Sobol.Bits; ++p)
                {
                    uint above = p == Sobol.Bits - 1 ? 0u : ~0u << (p + 1);
                    rows[p] = (philox.NextUInt32() & above) | (1u << p);
                }
                for (int k = 0; k < Sobol.Bits; ++k)
                {
                    uint v = directions[d * Sobol.Bits + k];
                    uint scrambled = 0u;
                    for (int p = 0; p < Sobol.Bits; ++p)
                        scrambled |= (uint)(BitOperations.PopCount(rows[p] & v) & 1) << p;
                    result[d * Sobol.Bits + k] = scrambled;
                }
            }
            return result;
        }
    }
}
