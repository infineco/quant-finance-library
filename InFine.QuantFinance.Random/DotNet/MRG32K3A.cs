using System;
using Hybridizer.Runtime.CUDAImports.curand_device;

namespace InFine.QuantFinance.Random.DotNet
{
    public class MRG32K3A : BaseGenerator
    {
        private curandStateMRG32k3a_t state;

        public MRG32K3A(int seed)
        {
            // intel seeding - single parameter
            state.x1 = seed;
            state.x2 = state.x3 = state.y1 = state.y2 = state.y3 = 1;
        }

        public MRG32K3A(int[] seed)
        {
            // intel seeding - multi parameter
            if (seed.Length > 0) state.x1 = seed[0]; else state.x1 = 12345;
            if (seed.Length > 1) state.x2 = seed[1]; else state.x2 = 12345;
            if (seed.Length > 2) state.x3 = seed[2]; else state.x3 = 12345;
            if (seed.Length > 3) state.y1 = seed[3]; else state.y1 = 12345;
            if (seed.Length > 4) state.y2 = seed[4]; else state.y2 = 12345;
            if (seed.Length > 5) state.y3 = seed[5]; else state.y3 = 12345;
        }

        public MRG32K3A(ulong seed)
        {
            state.x1 = 12345;
            state.x2 = 12345;
            state.x3 = 12345;
            state.y1 = 12345;
            state.y2 = 12345;
            state.y3 = 12345;
            state.init(seed);
        }

        public override void GenerateGaussians(GaussianTransformEnum transform, double mean, double stdev, int count, double[] target)
        {
            const double REAL_PI = 3.1415926535897932384626433832795;
            switch (transform)
            {
                case GaussianTransformEnum.BoxMuller2:
                    if ((count & 1) != 0) throw new ArgumentException("Expecting even number of samples");
                    {
                        for (int k = 0; k < count; k += 2)
                        {
                            double u1 = state.curand_uniform_double();
                            double u2 = state.curand_uniform_double();
                            target[k] = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(REAL_PI * 2.0 * u2) * stdev + mean;
                            target[k + 1] = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(REAL_PI * 2.0 * u2) * stdev + mean;
                        }
                    }
                    return;
            }
            throw new NotImplementedException();
        }

        public override void GenerateUniforms(UniformTransformEnum transform, double leftbound, double rightbound, int count, double[] target)
        {
            if (transform != UniformTransformEnum.STANDARD)
                throw new NotImplementedException();
            for (int k = 0; k < count; ++k)
                target[k] = state.curand_uniform_double() *(rightbound - leftbound) + leftbound;
        }

        public void skipahead_sequence(ulong n)
        {
            curandStateMRG32k3a_t.skipahead_sequence(n, ref state);
        }

        public void skipahead_subsequence(ulong n)
        {
            curandStateMRG32k3a_t.skipahead_subsequence(n, ref state);
        }

        public void skipahead(ulong n)
        {
            curandStateMRG32k3a_t.skipahead(n, ref state);
        }
    }
}
