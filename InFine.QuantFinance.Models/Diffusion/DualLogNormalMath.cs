using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Arithmetic.AutoDiff;
using InFine.QuantFinance.Models.Concepts;
using System;

namespace InFine.QuantFinance.Models.Diffusion
{
    /// <summary>
    /// The exact log-normal step of <see cref="EquityDiffusion{Volatility, Rate, LogSpot, Brownian, Schedule}"/> and
    /// <see cref="CorrelatedLogNormalMath.LogStep"/> on <see cref="Dual4"/> numbers, for pathwise Greeks: seeding the
    /// initial spot, the volatility, the rate or the dividend yield on a tangent direction makes every simulated spot carry
    /// its derivative along that input for the same Gaussian draws. Hybridizer-free (plain arrays), kernel-compatible.
    /// </summary>
    /// <remarks>
    /// Same operations in the same order as the double step, so the value part of a dual path is the double path.
    /// The draws are inputs: whatever produced them (Philox, MRG32k3a, Sobol) is untouched, which is what common random
    /// numbers between a pathwise run and a bumped run need.
    /// One pass gives four sensitivities, so the cost of all of them grows with the number of inputs; the adjoints of
    /// <see cref="LogNormalAdjoint"/> and <see cref="CorrelatedLogNormalAdjoint"/> give every sensitivity of the same paths
    /// in one backward sweep, and these tangent paths are their reference.
    /// </remarks>
    public static class DualLogNormalMath
    {
        /// <summary>
        /// <c>ln S + (r - q - sigma^2 / 2) dt + sigma sqrt(dt) z</c>, dual counterpart of <see cref="CorrelatedLogNormalMath.LogStep"/>.
        /// </summary>
        [Kernel]
        public static Dual4 LogStep(Dual4 logSpot, Dual4 rate, Dual4 dividendYield, Dual4 sigma, double dt, double sqrtDt, double normal)
        {
            return logSpot + (rate - dividendYield - 0.5 * sigma * sigma) * dt + sigma * sqrtDt * normal;
        }

        /// <summary>
        /// Spot path S_0..S_steps on the uniform grid k T / steps, written to <paramref name="path"/> (steps + 1 entries),
        /// from the draws <paramref name="normals"/>[<paramref name="offset"/> + k], k = 0..steps - 1.
        /// </summary>
        [Kernel]
        public static void Path(Dual4 spot, Dual4 rate, Dual4 dividendYield, Dual4 sigma, double maturity, int steps,
            double[] normals, int offset, Dual4[] path)
        {
            double dt = maturity / steps;
            double sqrtDt = Math.Sqrt(dt);
            Dual4 logSpot = Dual4.Log(spot);
            path[0] = spot;
            for (int k = 0; k < steps; ++k)
            {
                logSpot = LogStep(logSpot, rate, dividendYield, sigma, dt, sqrtDt, normals[offset + k]);
                path[k + 1] = Dual4.Exp(logSpot);
            }
        }

        /// <summary>
        /// Spot path S_0..S_steps with a term structure: rate, dividend yield and volatility of step k and step lengths
        /// <paramref name="dt"/>[k] (the model of <see cref="LogNormalAdjoint"/>), written to <paramref name="path"/>
        /// (steps + 1 entries) from the draws <paramref name="normals"/>[<paramref name="offset"/> + k]. Seeding one bucket
        /// on a direction gives its tangent: the forward-mode counterpart of the bucketed Greeks of the adjoint.
        /// </summary>
        [Kernel]
        public static void Path(Dual4 spot, Dual4[] rate, Dual4[] dividendYield, Dual4[] sigma, double[] dt, int steps,
            double[] normals, int offset, Dual4[] path)
        {
            Dual4 logSpot = Dual4.Log(spot);
            path[0] = spot;
            for (int k = 0; k < steps; ++k)
            {
                logSpot = LogStep(logSpot, rate[k], dividendYield[k], sigma[k], dt[k], Math.Sqrt(dt[k]), normals[offset + k]);
                path[k + 1] = Dual4.Exp(logSpot);
            }
        }

        /// <summary>
        /// Dual counterpart of <see cref="CorrelatedLogNormalMath.DiffusePath"/> (same operations, same order): the
        /// correlated multi-asset log path, asset d at pillar t in <paramref name="logPath"/>[t * D + d], from the
        /// independent draws <paramref name="normals"/>[<paramref name="offset"/> + t * D + d]. The correlation is a
        /// constant, so <c>y = L z</c> is formed in doubles; seed <c>ln S_d(0)</c> (for instance
        /// <c>Dual4.Log(Dual4.Variable(S, i))</c>), a volatility, a dividend yield or a step rate to get its tangent.
        /// </summary>
        [Kernel]
        public static void DiffusePath(Dual4[] logSpot0, Dual4[] sigma, Dual4[] dividendYield, double[] lower, int assetCount,
            double[] dt, Dual4[] stepRates, int steps, double[] normals, int offset, Dual4[] logPath)
        {
            for (int d = 0; d < assetCount; ++d)
                logPath[d] = logSpot0[d];
            for (int t = 0; t < steps; ++t)
            {
                double sqrtDt = Math.Sqrt(dt[t]);
                int from = t * assetCount, to = (t + 1) * assetCount;
                for (int i = 0; i < assetCount; ++i)
                {
                    double y = CholeskyCorrelation.Component(lower, assetCount, i, normals, offset + from);
                    logPath[to + i] = LogStep(logPath[from + i], stepRates[t], dividendYield[i], sigma[i], dt[t], sqrtDt, y);
                }
            }
        }
    }
}
