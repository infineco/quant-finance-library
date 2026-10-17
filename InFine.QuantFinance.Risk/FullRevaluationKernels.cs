using System;
using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Random.DotNet;
using InFine.QuantFinance.Random.QuasiRandom;

namespace InFine.QuantFinance.Risk
{
    /// <summary>
    /// Kernel layer of the Monte Carlo VaR by full revaluation: for scenario <c>s</c>, draw correlated risk-factor moves,
    /// reprice every instrument of the portfolio in the shocked market and write the P&amp;L. One scenario is one unit of
    /// work, so the scenario loop is what gets parallelised (one CUDA thread, one AVX lane or one host chunk per
    /// scenario) and the instrument loop runs inside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kernel rules.</b> Everything here is allocation-free, exception-free, has no virtual call and takes plain
    /// <c>int</c> / <c>uint</c> / <c>ulong</c> / <c>double</c> scalars and flat arrays (structure of arrays). The
    /// per-scenario cores (<see cref="GenerateScenario"/>, <see cref="InstrumentValue"/>, <see cref="RevalueScenario"/>,
    /// <see cref="SimulateScenario"/>) touch no Hybridizer type, so they also run as plain C# on any host;
    /// <see cref="FullRevaluationEngine"/> calls them directly. Only <see cref="SimulateScenarios"/>, the grid-stride
    /// <c>[EntryPoint]</c>, reads the CUDA thread indices.
    /// </para>
    /// <para>
    /// <b>Determinism.</b> Every random number of scenario <c>s</c> is a pure function of (seed, s, factor index), and
    /// the sums are evaluated in a fixed order, so the P&amp;L of a scenario does not depend on which thread, chunk or
    /// device computed it: any partition of the scenario range gives bit-identical arrays on the same platform.
    /// </para>
    /// <para>
    /// <b>Risk-factor model.</b> <c>F</c> factors with base levels <c>X0_j</c>. A scenario draws <c>z ~ N(0, I_F)</c>, sets
    /// <c>x = L z</c> with <c>L</c> the lower-triangular Cholesky factor (row-major, <c>L[j * F + k]</c>) of the
    /// covariance of the factor moves over the horizon, and maps each factor by its kind:
    /// <list type="bullet">
    /// <item><see cref="LogNormalFactor"/>: <c>X = X0 exp(x - v/2)</c>, with <c>v = (L L^T)_jj</c> (passed as
    /// <c>halfVariance = v/2</c>), so <c>E[X] = X0</c>; <c>x</c> is the log-return. Typical for spots.</item>
    /// <item><see cref="SimpleReturnFactor"/>: <c>X = X0 (1 + x)</c>; <c>x</c> is the simple return. The P&amp;L of a
    /// linear position is then exactly normal (the delta-normal model).</item>
    /// <item><see cref="AdditiveFactor"/>: <c>X = X0 + x</c>; <c>x</c> is an absolute change. Typical for implied
    /// volatilities and rates (a parallel shift of a flat rate).</item>
    /// </list>
    /// A factor whose row of <c>L</c> is zero is not shocked; that is how a constant input (a fixed volatility or rate)
    /// is represented.
    /// </para>
    /// <para>
    /// <b>Random numbers.</b> <see cref="PhiloxNormals"/>: <c>z_j</c> uses words <c>2j</c> (high) and <c>2j + 1</c> (low) of
    /// the Philox4x32-10 sub-stream (seed, path = s, stream), <c>z_j = Phi^-1(u52)</c> with
    /// <see cref="Philox4x32x10.ToUniformOpen52"/>; this is exactly what
    /// <c>new PhiloxGenerator(seed, s, stream).GenerateGaussians(InverseCumulativeDistributionFunction, ...)</c> returns.
    /// <see cref="SobolNormals"/>: <c>z_j = </c><see cref="Sobol.Gaussian"/>(s, j, directions, shifts[j]): coordinate j of
    /// Sobol point s (point 0 included), normally with scrambled direction numbers and digital shifts.
    /// </para>
    /// <para>
    /// <b>Instruments</b> (index <c>i</c>, arrays of length <c>N</c>): <see cref="LinearInstrument"/> is worth the level of
    /// factor <c>spotFactor[i]</c> per unit (a spot position: stock, index, FX amount); <see cref="EuropeanOptionInstrument"/>
    /// is a Black-Scholes-Merton option of sign <c>optionSign[i]</c> on the level of <c>spotFactor[i]</c>, with volatility
    /// and rate read from factors <c>volFactor[i]</c> and <c>rateFactor[i]</c>, constant <c>dividendYield[i]</c>, and
    /// time to expiry <c>max(maturity[i] - timeDecay, 0)</c> in a scenario (the base value uses <c>timeDecay = 0</c>, so
    /// the P&amp;L includes the theta over the horizon when <c>timeDecay</c> is the horizon in years). Degenerate shocked
    /// inputs (a non-positive volatility or spot) price at intrinsic value through <see cref="Black76Kernels"/>, never NaN.
    /// The P&amp;L of scenario s is <c>pnl[s] = sum_i quantity[i] (V_i(s) - baseValue[i])</c>, summed in instrument order.
    /// </para>
    /// <para>
    /// <b>Memory layout.</b> The scenario P&amp;L is <c>pnl[s]</c>. The optional per-instrument P&amp;L (per unit of quantity)
    /// is <c>instrumentPnl[i * scenarioCount + s]</c>: consecutive scenarios are contiguous, as in the library's other
    /// per-path buffers, which coalesces the writes of a CUDA warp. A scenario needs <c>F</c> doubles of scratch for its
    /// factor levels, at <c>scratch[offset .. offset + F)</c>: one row per thread on a device, one per chunk on the host.
    /// </para>
    /// <para>
    /// The class is not static so that a HybRunner can wrap an instance of it to reach the static entry point
    /// (<c>runner.Wrap(new FullRevaluationKernels()).SimulateScenarios(...)</c>, as for the other wrapped kernels of
    /// the library); it has no state.
    /// </para>
    /// </remarks>
    public sealed class FullRevaluationKernels
    {
        /// <summary>Generator code: Philox4x32-10 normals by inversion.</summary>
        public const int PhiloxNormals = 0;

        /// <summary>Generator code: Sobol normals by inversion.</summary>
        public const int SobolNormals = 1;

        /// <summary>Factor kind: <c>X = X0 exp(x - v/2)</c>.</summary>
        public const int LogNormalFactor = 0;

        /// <summary>Factor kind: <c>X = X0 (1 + x)</c>.</summary>
        public const int SimpleReturnFactor = 1;

        /// <summary>Factor kind: <c>X = X0 + x</c>.</summary>
        public const int AdditiveFactor = 2;

        /// <summary>Instrument type: linear position worth the factor level per unit.</summary>
        public const int LinearInstrument = 0;

        /// <summary>Instrument type: European option priced by Black-Scholes-Merton.</summary>
        public const int EuropeanOptionInstrument = 1;

        /// <summary>Default Philox stream tag of the scenario generator ("MCVR").</summary>
        public const uint DefaultPhiloxStream = 0x4D435652u;

        /// <summary>
        /// Writes the <paramref name="factorCount"/> shocked factor levels of scenario <paramref name="scenario"/> to
        /// <paramref name="levels"/>[<paramref name="offset"/> .. <paramref name="offset"/> + F).
        /// </summary>
        /// <param name="scenario">Scenario index s (the Philox path index, the Sobol point index).</param>
        /// <param name="generator"><see cref="PhiloxNormals"/> or <see cref="SobolNormals"/>.</param>
        /// <param name="seed">Philox key (ignored by Sobol).</param>
        /// <param name="stream">Philox stream tag (ignored by Sobol).</param>
        /// <param name="sobolDirections">Sobol direction numbers, 32 per dimension, at least F dimensions (ignored by Philox; may be empty).</param>
        /// <param name="sobolShifts">Sobol digital shifts, one per dimension (ignored by Philox; may be empty).</param>
        /// <param name="factorCount">Number of factors F.</param>
        /// <param name="choleskyLower">Lower-triangular F x F factor of the horizon covariance, row-major.</param>
        /// <param name="factorKind">Kind of each factor (see the class remarks).</param>
        /// <param name="baseLevel">Base level of each factor.</param>
        /// <param name="halfVariance">Half the horizon variance <c>(L L^T)_jj / 2</c> of each factor (used by log-normal factors).</param>
        /// <param name="levels">Output (and scratch) buffer.</param>
        /// <param name="offset">Start of the F-element row of this scenario in <paramref name="levels"/>.</param>
        [Kernel]
        public static void GenerateScenario(int scenario, int generator, ulong seed, uint stream,
            uint[] sobolDirections, uint[] sobolShifts,
            int factorCount, double[] choleskyLower, int[] factorKind, double[] baseLevel, double[] halfVariance,
            double[] levels, int offset)
        {
            // 1. Independent standard normals z_0 .. z_(F-1).
            if (generator == SobolNormals)
            {
                for (int j = 0; j < factorCount; ++j)
                    levels[offset + j] = Sobol.Gaussian((uint)scenario, j, sobolDirections, sobolShifts[j]);
            }
            else
            {
                for (int j = 0; j < factorCount; j += 2)
                {
                    uint r0, r1, r2, r3;
                    Philox4x32x10.GenerateBlock(seed, (ulong)(j >> 1), (uint)scenario, stream, out r0, out r1, out r2, out r3);
                    levels[offset + j] = NormalDistribution.InverseCdf(Philox4x32x10.ToUniformOpen52(r0, r1));
                    if (j + 1 < factorCount)
                        levels[offset + j + 1] = NormalDistribution.InverseCdf(Philox4x32x10.ToUniformOpen52(r2, r3));
                }
            }

            // 2. Correlate in place, x = L z. Row j only needs z_0 .. z_j, so going from the last row up never reads an
            //    entry that was already overwritten.
            for (int j = factorCount - 1; j >= 0; --j)
            {
                int row = j * factorCount;
                double x = 0.0;
                for (int k = 0; k <= j; ++k)
                    x += choleskyLower[row + k] * levels[offset + k];
                levels[offset + j] = x;
            }

            // 3. Factor moves to levels.
            for (int j = 0; j < factorCount; ++j)
            {
                double x = levels[offset + j];
                int kind = factorKind[j];
                double level;
                if (kind == LogNormalFactor) level = baseLevel[j] * Math.Exp(x - halfVariance[j]);
                else if (kind == SimpleReturnFactor) level = baseLevel[j] * (1.0 + x);
                else level = baseLevel[j] + x;
                levels[offset + j] = level;
            }
        }

        /// <summary>
        /// Value per unit of instrument <paramref name="instrument"/> when the factor levels are
        /// <paramref name="levels"/>[<paramref name="offset"/> + j], with option expiries shortened by
        /// <paramref name="timeDecay"/> years (clamped at 0).
        /// </summary>
        [Kernel]
        public static double InstrumentValue(int instrument, double[] levels, int offset,
            int[] instrumentType, int[] optionSign, int[] spotFactor, int[] volFactor, int[] rateFactor,
            double[] strike, double[] maturity, double[] dividendYield, double timeDecay)
        {
            double spot = levels[offset + spotFactor[instrument]];
            if (instrumentType[instrument] == LinearInstrument)
                return spot;
            double t = maturity[instrument] - timeDecay;
            if (t < 0.0) t = 0.0;
            return BlackScholesKernels.Price(optionSign[instrument], spot, strike[instrument], t,
                levels[offset + rateFactor[instrument]], dividendYield[instrument], levels[offset + volFactor[instrument]]);
        }

        /// <summary>
        /// Reprices the <paramref name="instrumentCount"/> instruments in the factor levels of scenario
        /// <paramref name="scenario"/> (at <paramref name="levels"/>[<paramref name="offset"/> ..]) and writes
        /// <c>pnl[scenario]</c> and, if <paramref name="storeInstrumentPnl"/> is not 0, the per-unit P&amp;L
        /// <c>instrumentPnl[i * scenarioCount + scenario] = V_i(s) - baseValue[i]</c>.
        /// </summary>
        [Kernel]
        public static void RevalueScenario(int scenario, double[] levels, int offset,
            int instrumentCount, int[] instrumentType, int[] optionSign, int[] spotFactor, int[] volFactor, int[] rateFactor,
            double[] strike, double[] maturity, double[] dividendYield, double[] quantity, double[] baseValue, double timeDecay,
            double[] pnl, int scenarioCount, int storeInstrumentPnl, double[] instrumentPnl)
        {
            double total = 0.0;
            for (int i = 0; i < instrumentCount; ++i)
            {
                double unit = InstrumentValue(i, levels, offset, instrumentType, optionSign, spotFactor, volFactor, rateFactor,
                    strike, maturity, dividendYield, timeDecay) - baseValue[i];
                total += quantity[i] * unit;
                if (storeInstrumentPnl != 0)
                    instrumentPnl[i * scenarioCount + scenario] = unit;
            }
            pnl[scenario] = total;
        }

        /// <summary>
        /// One scenario end to end: <see cref="GenerateScenario"/> into the scratch row at <paramref name="scratchOffset"/>,
        /// then <see cref="RevalueScenario"/>.
        /// </summary>
        [Kernel]
        public static void SimulateScenario(int scenario,
            int generator, ulong seed, uint stream, uint[] sobolDirections, uint[] sobolShifts,
            int factorCount, double[] choleskyLower, int[] factorKind, double[] baseLevel, double[] halfVariance,
            int instrumentCount, int[] instrumentType, int[] optionSign, int[] spotFactor, int[] volFactor, int[] rateFactor,
            double[] strike, double[] maturity, double[] dividendYield, double[] quantity, double[] baseValue, double timeDecay,
            double[] scratch, int scratchOffset,
            double[] pnl, int scenarioCount, int storeInstrumentPnl, double[] instrumentPnl)
        {
            GenerateScenario(scenario, generator, seed, stream, sobolDirections, sobolShifts,
                factorCount, choleskyLower, factorKind, baseLevel, halfVariance, scratch, scratchOffset);
            RevalueScenario(scenario, scratch, scratchOffset, instrumentCount, instrumentType, optionSign, spotFactor,
                volFactor, rateFactor, strike, maturity, dividendYield, quantity, baseValue, timeDecay,
                pnl, scenarioCount, storeInstrumentPnl, instrumentPnl);
        }

        /// <summary>
        /// Device entry point: the scenarios [<paramref name="scenarioFrom"/>, <paramref name="scenarioTo"/>) in a
        /// grid-stride loop, one scenario per thread at a time. Thread <c>t = threadIdx.x + blockDim.x * blockIdx.x</c>
        /// uses the scratch row <c>scratch[t * factorCount ..]</c>, so <paramref name="scratch"/> must hold at least
        /// <c>gridDim.x * blockDim.x * factorCount</c> doubles. Arguments are those of <see cref="SimulateScenario"/>;
        /// <see cref="FullRevaluationBatch"/> builds them.
        /// </summary>
        [EntryPoint("FullRevaluationKernels_SimulateScenarios")]
        public static void SimulateScenarios(int scenarioFrom, int scenarioTo,
            int generator, ulong seed, uint stream, uint[] sobolDirections, uint[] sobolShifts,
            int factorCount, double[] choleskyLower, int[] factorKind, double[] baseLevel, double[] halfVariance,
            int instrumentCount, int[] instrumentType, int[] optionSign, int[] spotFactor, int[] volFactor, int[] rateFactor,
            double[] strike, double[] maturity, double[] dividendYield, double[] quantity, double[] baseValue, double timeDecay,
            double[] scratch,
            double[] pnl, int scenarioCount, int storeInstrumentPnl, double[] instrumentPnl)
        {
            int thread = threadIdx.x + blockDim.x * blockIdx.x;
            int stride = blockDim.x * gridDim.x;
            int scratchOffset = thread * factorCount;
            for (int s = scenarioFrom + thread; s < scenarioTo; s += stride)
            {
                SimulateScenario(s, generator, seed, stream, sobolDirections, sobolShifts,
                    factorCount, choleskyLower, factorKind, baseLevel, halfVariance,
                    instrumentCount, instrumentType, optionSign, spotFactor, volFactor, rateFactor,
                    strike, maturity, dividendYield, quantity, baseValue, timeDecay,
                    scratch, scratchOffset, pnl, scenarioCount, storeInstrumentPnl, instrumentPnl);
            }
        }
    }
}
