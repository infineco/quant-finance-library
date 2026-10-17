using System;
using InFine.QuantFinance.Random.DotNet;

namespace InFine.QuantFinance.Payoffs.Tests
{
    /// <summary>
    /// Geometric Brownian motion paths from Philox draws, without any Hybridizer type: path p, stream s of the seed always
    /// gives the same draws whatever the number of paths or the order they are generated in.
    /// </summary>
    public static class McPaths
    {
        public const ulong Seed = 0x5EED_0044_C0FF_EE01UL;
        public const uint NormalStream = 0;
        public const uint UniformStream = 1;

        /// <summary>First <paramref name="count"/> standard normals (Box-Muller pairs) of the sub-stream (seed, path, stream).</summary>
        public static void Normals(uint path, uint stream, double[] destination, int count)
        {
            for (int block = 0, k = 0; k < count; ++block)
            {
                uint r0, r1, r2, r3;
                Philox4x32x10.GenerateBlock(Seed, (ulong)block, path, stream, out r0, out r1, out r2, out r3);
                double z0, z1, z2, z3;
                Philox4x32x10.BoxMuller(r0, r1, out z0, out z1);
                Philox4x32x10.BoxMuller(r2, r3, out z2, out z3);
                if (k < count) destination[k++] = z0;
                if (k < count) destination[k++] = z1;
                if (k < count) destination[k++] = z2;
                if (k < count) destination[k++] = z3;
            }
        }

        /// <summary>First <paramref name="count"/> uniforms in (0, 1) of the sub-stream (seed, path, stream).</summary>
        public static void Uniforms(uint path, uint stream, double[] destination, int count)
        {
            for (int block = 0, k = 0; k < count; ++block)
            {
                uint r0, r1, r2, r3;
                Philox4x32x10.GenerateBlock(Seed, (ulong)block, path, stream, out r0, out r1, out r2, out r3);
                if (k < count) destination[k++] = Philox4x32x10.ToUniformOpen32(r0);
                if (k < count) destination[k++] = Philox4x32x10.ToUniformOpen32(r1);
                if (k < count) destination[k++] = Philox4x32x10.ToUniformOpen32(r2);
                if (k < count) destination[k++] = Philox4x32x10.ToUniformOpen32(r3);
            }
        }

        /// <summary>
        /// Exact GBM path S_0..S_steps on the grid k T / steps (S_{k+1} = S_k exp((r - q - sigma^2/2) dt + sigma sqrt(dt) z)),
        /// written into <paramref name="path"/>; <paramref name="normals"/> is scratch of at least <paramref name="steps"/> entries.
        /// </summary>
        public static void Gbm(uint pathIndex, int steps, double spot, double rate, double dividend, double sigma, double maturity,
            double[] path, double[] normals)
        {
            Normals(pathIndex, NormalStream, normals, steps);
            double dt = maturity / steps;
            double drift = (rate - dividend - 0.5 * sigma * sigma) * dt;
            double vol = sigma * Math.Sqrt(dt);
            path[0] = spot;
            for (int k = 0; k < steps; ++k)
                path[k + 1] = path[k] * Math.Exp(drift + vol * normals[k]);
        }

        /// <summary>Mean and standard error of a sample.</summary>
        public static void MeanAndError(double[] values, out double mean, out double standardError)
        {
            double s = 0.0, ss = 0.0;
            foreach (double v in values) { s += v; ss += v * v; }
            int n = values.Length;
            mean = s / n;
            standardError = Math.Sqrt((ss / n - mean * mean) / (n - 1));
        }

        /// <summary>Pillar times k T / steps, k = 0..steps.</summary>
        public static double[] Dates(int steps, double maturity)
        {
            var d = new double[steps + 1];
            for (int k = 0; k <= steps; ++k) d[k] = maturity * k / steps;
            return d;
        }

        /// <summary>Discount factors exp(-r t_k) on <paramref name="dates"/>.</summary>
        public static double[] Discounts(double[] dates, double rate)
        {
            var d = new double[dates.Length];
            for (int k = 0; k < d.Length; ++k) d[k] = Math.Exp(-rate * dates[k]);
            return d;
        }

        /// <summary>Indices from..to inclusive.</summary>
        public static int[] Range(int from, int to)
        {
            var r = new int[to - from + 1];
            for (int i = 0; i < r.Length; ++i) r[i] = from + i;
            return r;
        }
    }
}
