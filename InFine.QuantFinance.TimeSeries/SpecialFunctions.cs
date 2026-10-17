using System;

namespace InFine.QuantFinance.TimeSeries
{
    internal static class SpecialFunctions
    {
        // Lanczos approximation, g = 7, 9 coefficients (Godfrey); relative error about 1e-15 for x >= 0.5.
        static readonly double[] LanczosCoefficients =
        {
            0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
        };

        /// <summary>ln Gamma(x) for x &gt; 0.</summary>
        public static double LogGamma(double x)
        {
            if (x < 0.5) return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LogGamma(1.0 - x);
            x -= 1.0;
            double a = LanczosCoefficients[0];
            double t = x + 7.5;
            for (int i = 1; i < 9; i++) a += LanczosCoefficients[i] / (x + i);
            return 0.5 * Math.Log(2.0 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
        }
    }

    /// <summary>xoshiro256** (Blackman and Vigna) seeded through splitmix64: a small deterministic generator whose stream does not depend on the .NET version.</summary>
    internal sealed class SeededRandom
    {
        ulong _s0, _s1, _s2, _s3;
        double _spare;
        bool _hasSpare;

        public SeededRandom(ulong seed)
        {
            _s0 = SplitMix(ref seed);
            _s1 = SplitMix(ref seed);
            _s2 = SplitMix(ref seed);
            _s3 = SplitMix(ref seed);
        }

        static ulong SplitMix(ref ulong x)
        {
            ulong z = x += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

        ulong Next()
        {
            ulong result = Rotl(_s1 * 5UL, 7) * 9UL;
            ulong t = _s1 << 17;
            _s2 ^= _s0; _s3 ^= _s1; _s1 ^= _s2; _s0 ^= _s3;
            _s2 ^= t;
            _s3 = Rotl(_s3, 45);
            return result;
        }

        /// <summary>Uniform on the open interval (0, 1).</summary>
        public double NextUniform() => ((Next() >> 11) + 0.5) * (1.0 / 9007199254740992.0);

        /// <summary>Standard normal by the Box-Muller transform.</summary>
        public double NextNormal()
        {
            if (_hasSpare) { _hasSpare = false; return _spare; }
            double u1 = NextUniform(), u2 = NextUniform();
            double radius = Math.Sqrt(-2.0 * Math.Log(u1));
            _spare = radius * Math.Sin(2.0 * Math.PI * u2);
            _hasSpare = true;
            return radius * Math.Cos(2.0 * Math.PI * u2);
        }

        /// <summary>Gamma(shape, 1) for shape &gt;= 1 (Marsaglia and Tsang, 2000).</summary>
        public double NextGamma(double shape)
        {
            double d = shape - 1.0 / 3.0, c = 1.0 / Math.Sqrt(9.0 * d);
            while (true)
            {
                double x, v;
                do { x = NextNormal(); v = 1.0 + c * x; } while (v <= 0.0);
                v = v * v * v;
                double u = NextUniform();
                if (Math.Log(u) < 0.5 * x * x + d - d * v + d * Math.Log(v)) return d * v;
            }
        }
    }
}
