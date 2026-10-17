using System;
using System.Runtime.InteropServices;
using Hybridizer.Runtime.CUDAImports;

namespace InFine.QuantFinance.Random.CURAND
{
    [IntrinsicType("curandStateMRG32k3a_t")]
    [IntrinsicIncludeCUDA("curand_kernel.h")]
    [IntrinsicIncludeOMP("curand_kernel_omp.h")]
    [StructLayout(LayoutKind.Sequential)]
    [HybridizerIgnore("AVX")]
    public unsafe struct curandStateMRG32k3a_t
    {
        const double MRG32k3a_M1 = 4294967087.0;
        const double MRG32k3a_M2 = 4294944443.0;
        const double MRG32k3a_INV_M1 = 1.0 / 4294967087.0;
        const double MRG32k3a_INV_M2 = 1.0 / 4294944443.0;

        const double a12 = 1403580.0;
        const double a13n = 810728.0;
        const double a21 = 527612.0;
        const double a23n = 1370589.0;
        const double norm = 2.328306549295728e-10;

        const double two17 = 131072.0;
        const double two53 = 9007199254740992.0;
        const double fact = 5.9604644775390625e-8;

        public double x1, y1, x2, y2, x3, y3;

        public int boxmuller_flag;
        public int boxmuller_flag_double;
        public float boxmuller_extra;
        public double boxmuller_extra_double;

        public void init(ulong seed)
        {
            if (seed != 0ul)
            {
                // Same behaviour as curand
                uint x1 = ((uint)seed) ^ 0x55555555U;
                uint x2 = (((uint)(seed >> 32)) ^ 0xAAAAAAAAU);

                this.x1 = MultModM(x1, this.x1, 0, MRG32k3a_M1);
                this.x2 = MultModM(x2, this.x2, 0, MRG32k3a_M1);
                this.x3 = MultModM(x1, this.x3, 0, MRG32k3a_M1);
                this.y1 = MultModM(x2, this.y1, 0, MRG32k3a_M2);
                this.y2 = MultModM(x1, this.y2, 0, MRG32k3a_M2);
                this.y3 = MultModM(x2, this.y3, 0, MRG32k3a_M2);
            }
        }

        public void init(ulong seed, ulong subsequence, ulong offset)
        {
            init(seed);
            skipahead_subsequence(subsequence, ref this);
            skipahead(offset, ref this);
        }

        [IntrinsicFunction("curand_init")]
        public static void curand_init(ulong seed, ulong subsequence, ulong offset, out curandStateMRG32k3a_t state)
        {
            state = new curandStateMRG32k3a_t();
            state.x1 = 12345;
            state.x2 = 12345;
            state.x3 = 12345;
            state.y1 = 12345;
            state.y2 = 12345;
            state.y3 = 12345;
            state.init(seed, subsequence, offset);
        }

        [IntrinsicFunction("curand_uniform_double")]
        public double curand_uniform_double()
        {
            double next = NextDouble();
            // UNBIAISED : return next * MRG32k3a_INV_M1 + (MRG32k3a_INV_M1 * 0.5);
            // INTEL BIAIS :
            if (next == 0.0) return 1.0;
            return next * MRG32k3a_INV_M1;
        }

        [IntrinsicFunction("skipahead")]
        public static void skipahead(ulong n, ref curandStateMRG32k3a_t state)
        {
            state.AdvanceState(0L, (long)n);
        }

        [IntrinsicFunction("skipahead_sequence")]
        public static void skipahead_sequence(ulong n, ref curandStateMRG32k3a_t state)
        {
            for (int i = 0; i < 64; ++i)
                if ((1ul << i & n) != 0)
                    state.AdvanceState(i + 127, 0);
        }

        [IntrinsicFunction("skipahead_subsequence")]
        public static void skipahead_subsequence(ulong n, ref curandStateMRG32k3a_t state)
        {
            ulong nn = (n << (64 - 51)) >> (64 - 51);
            for (int i = 0; i < 64; ++i)
                if ((1ul << i & nn) != 0)
                    state.AdvanceState(i + 76, 0);
        }

        #region Unimplemented methods

        [IntrinsicFunction("curand")]
        public uint curand() { throw new NotImplementedException(); }

        [IntrinsicFunction("curand_log_normal")]
        public float curand_log_normal(float mean, float stdev) { throw new NotImplementedException(); }

        [IntrinsicFunction("curand_log_normal2")]
        public float2 curand_log_normal2(float mean, float stdev) { throw new NotImplementedException(); }

        [IntrinsicFunction("curand_log_normal_double")]
        public double curand_log_normal_double(double mean, double stdev) { throw new NotImplementedException(); }

        [IntrinsicFunction("curand_log_normal2_double")]
        public double2 curand_log_normal2_double(double mean, double stdev) { throw new NotImplementedException(); }

        [IntrinsicFunction("curand_normal")]
        public float curand_normal() { throw new NotImplementedException(); }

        [IntrinsicFunction("curand_normal2")]
        public float2 curand_normal2() { throw new NotImplementedException(); }

        [IntrinsicFunction("curand_normal2_double")]
        public double2 curand_normal2_double() { throw new NotImplementedException(); }

        [IntrinsicFunction("curand_normal_double")]
        public double curand_normal_double() { throw new NotImplementedException(); }

        [IntrinsicFunction("curand_poisson")]
        public uint curand_poisson(double lambda) { throw new NotImplementedException(); }

        [IntrinsicFunction("curand_uniform")]
        public float curand_uniform() { throw new NotImplementedException(); }

        #endregion Unimplemented methods

        #region Implementation
        private double NextDouble()
        {
            double k;
            double x, y;

            /* Component 1 */
            x = a12 * x2 - a13n * x1;
            k = Math.Floor(x * MRG32k3a_INV_M1);
            x -= k * MRG32k3a_M1;
            if (x < 0.0)
                x += MRG32k3a_M1;

            x1 = x2;
            x2 = x3;
            x3 = x;

            /* Component 2 */
            y = a21 * y3 - a23n * y1;
            k = Math.Floor(y * MRG32k3a_INV_M2);
            y -= k * MRG32k3a_M2;

            if (y < 0.0)
                y += MRG32k3a_M2;

            y1 = y2;
            y2 = y3;
            y3 = y;

            /* Combination */
            if (x <= y)
                return ((x - y + MRG32k3a_M1));
            else
                return ((x - y));
        }


        /// See L'Ecuyer : http://www.iro.umontreal.ca/~lecuyer/

        /* Compute (a*s + c) % m. m must be < 2^35.  Works also for s, c < 0 */
        static double MultModM(double a, double s, double c, double m)
        {
            double v;
            long a1;
            v = a * s + c;
            if ((v >= two53) || (v <= -two53))
            {
                a1 = (long)Math.Floor(a / two17);
                double aa = a - a1 * two17;
                v = a1 * s;
                a1 = (long)(v / m);
                v -= a1 * m;
                v = v * two17 + aa * s + c;
            }
            a1 = (long)(v / m);
            if ((v -= a1 * m) < 0.0)
                v += m;

            return v;
        }

        internal struct Double3
        {
            public fixed double buffer[3];

            public double this[int i]
            {
                get
                {
                    fixed (double* bp = buffer)
                    {
                        return bp[i];
                    }
                }
                set
                {
                    fixed (double* bp = buffer)
                    {
                        bp[i] = value;
                    }
                }
            }

            public void init()
            {
                fixed (double* bp = buffer)
                {
                    bp[0] = 0;
                    bp[1] = 0;
                    bp[2] = 0;
                }
            }
        }

        internal struct Mat3x3
        {
            public fixed double buffer[9];

            public double this[int i, int j]
            {
                get
                {
                    fixed (double* bp = buffer)
                    {
                        return bp[i * 3 + j];
                    }
                }
                set
                {
                    fixed (double* bp = buffer)
                    {
                        bp[i * 3 + j] = value;
                    }
                }
            }
            public static explicit operator Mat3x3(double[] value)
            {
                Mat3x3 res; // = new Mat3x3();
                for (int i = 0; i < 3; ++i)
                    for (int j = 0; j < 3; ++j)
                        res[i, j] = value[i * 3 + j];
                return res;
            }
        }

        [HybridConstant]
        static readonly double[] A1p0_ar = new double[] {
                  0.0,        1.0,       0.0,
                  0.0,        0.0,       1.0,
            -810728.0,  1403580.0,       0.0
          };
        [HybridConstant]
        static readonly double[] A2p0_ar = new double[] {
                   0.0,        1.0,       0.0 ,
                   0.0,        0.0,       1.0 ,
            -1370589.0,        0.0,  527612.0
          };
        [HybridConstant]
        static readonly double[] InvA1_ar = new double[] {
            184888585.0,   0.0,  1945170933.0 ,
                    1.0,   0.0,           0.0 ,
                    0.0,   1.0,           0.0
          };
        [HybridConstant]
        static readonly double[] InvA2_ar = new double[] {
                 0.0,  360363334.0,  4225571728.0,
                 1.0,          0.0,           0.0,
                 0.0,          1.0,           0.0
          };

        private static Mat3x3 A1p0 { get { return (Mat3x3)A1p0_ar; } }
        private static Mat3x3 A2p0 { get { return (Mat3x3)A2p0_ar; } }
        private static Mat3x3 InvA1 { get { return (Mat3x3)InvA1_ar; } }
        private static Mat3x3 InvA2 { get { return (Mat3x3)InvA2_ar; } }

        /* Returns v = A*s % m.  Assumes that -m < s[i] < m. */
        /* Works even if v = s. */
        static unsafe void MatVecModM(ref Mat3x3 A, ref Double3 s, ref Double3 v, double m)
        {
            int i;
            Double3 x;// = new Double3();
            for (i = 0; i < 3; ++i)
            {
                x[i] = MultModM(A[i, 0], s[0], 0.0, m);
                x[i] = MultModM(A[i, 1], s[1], x[i], m);
                x[i] = MultModM(A[i, 2], s[2], x[i], m);
            }
            for (i = 0; i < 3; ++i)
                v[i] = x[i];
        }

        /* Returns C = A*B % m. Work even if A = C or B = C or A = B = C. */
        static void MatMatModM(ref Mat3x3 A, ref Mat3x3 B, ref Mat3x3 C, double m)
        {
            int i, j;
            Double3 V;// = new Double3();
            Mat3x3 W;// = new Mat3x3();
            for (i = 0; i < 3; ++i)
            {
                for (j = 0; j < 3; ++j)
                    V[j] = B[j, i];
                MatVecModM(ref A, ref V, ref V, m);
                for (j = 0; j < 3; ++j)
                    W[j, i] = V[j];
            }
            for (i = 0; i < 3; ++i)
            {
                for (j = 0; j < 3; ++j)
                    C[i, j] = W[i, j];
            }
        }

        /* Compute matrix B = (A^(2^e) % m);  works even if A = B */
        static void MatTwoPowModM(Mat3x3 A, ref Mat3x3 B, double m, long e)
        {
            int i, j;

            /* initialize: B = A */
            for (i = 0; i < 3; i++)
            {
                for (j = 0; j < 3; ++j)
                    B[i, j] = A[i, j];
            }
            /* Compute B = A^{2^e} */
            for (i = 0; i < e; i++)
                MatMatModM(ref B, ref B, ref B, m);
        }

        /* Compute matrix B = A^n % m ;  works even if A = B */
        static void MatPowModM(Mat3x3 A, ref Mat3x3 B, double m, long n)
        {
            int i, j;
            long nn = n;
            Mat3x3 W;// = new Mat3x3();

            /* initialize: W = A; B = I */
            for (i = 0; i < 3; i++)
            {
                for (j = 0; j < 3; ++j)
                {
                    W[i, j] = A[i, j];
                    B[i, j] = 0.0;
                }
            }
            for (j = 0; j < 3; ++j)
                B[j, j] = 1.0;

            /* Compute B = A^nn % m using the binary decomposition of nn */
            while (nn > 0)
            {
                if (nn % 2 != 0)
                    MatMatModM(ref W, ref B, ref B, m);
                MatMatModM(ref W, ref W, ref W, m);
                nn /= 2;
            }
        }

        private void AdvanceState(long e, long c)
        {
            Mat3x3 B1;// = new Mat3x3();
            Mat3x3 C1;// = new Mat3x3();
            Mat3x3 B2;// = new Mat3x3();
            Mat3x3 C2;// = new Mat3x3();

            if (e > 0)
            {
                var t = A1p0;
                MatTwoPowModM(A1p0, ref B1, MRG32k3a_M1, e);
                MatTwoPowModM(A2p0, ref B2, MRG32k3a_M2, e);
            }
            else if (e < 0)
            {
                MatTwoPowModM(InvA1, ref B1, MRG32k3a_M1, -e);
                MatTwoPowModM(InvA2, ref B2, MRG32k3a_M2, -e);
            }

            if (c >= 0)
            {
                MatPowModM(A1p0, ref C1, MRG32k3a_M1, c);
                MatPowModM(A2p0, ref C2, MRG32k3a_M2, c);
            }
            else
            {
                MatPowModM(InvA1, ref C1, MRG32k3a_M1, -c);
                MatPowModM(InvA2, ref C2, MRG32k3a_M2, -c);
            }

            if (e != 0)
            {
                MatMatModM(ref B1, ref C1, ref C1, MRG32k3a_M1);
                MatMatModM(ref B2, ref C2, ref C2, MRG32k3a_M2);
            }

            Double3 x;// = new Double3();
            Double3 y;// = new Double3();
            x[0] = x1;
            x[1] = x2;
            x[2] = x3;
            y[0] = y1;
            y[1] = y2;
            y[2] = y3;
            MatVecModM(ref C1, ref x, ref x, MRG32k3a_M1);
            MatVecModM(ref C2, ref y, ref y, MRG32k3a_M2);
            x1 = x[0];
            x2 = x[1];
            x3 = x[2];
            y1 = y[0];
            y2 = y[1];
            y3 = y[2];
        }
        #endregion
    }

    [HybridizerIgnore("AVX")]
    public class CurandSamples
    {
        [EntryPoint]
        public void GenerateUniform(ulong seed, ulong subsequenceinit, ulong offsetinit, double[] res, int count, ulong skipSequence, ulong skipSubsequence, ulong skip)
        {
            if (threadIdx.x == 0 && blockIdx.x == 0)
            {
                curandStateMRG32k3a_t state = new curandStateMRG32k3a_t();
                curandStateMRG32k3a_t.curand_init(seed, subsequenceinit, offsetinit, out state);
                curandStateMRG32k3a_t.skipahead_sequence(skipSequence, ref state);
                curandStateMRG32k3a_t.skipahead_subsequence(skipSubsequence, ref state);
                curandStateMRG32k3a_t.skipahead(skip, ref state);
                for (int i = 0; i < count; ++i)
                    res[i] = state.curand_uniform_double();
            }
        }
    }
}
