using System;
using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Random.DotNet;

namespace InFine.QuantFinance.Backtesting
{
    /// <summary>
    /// Kernel layer of the backtester: the portfolio accounting steps shared by <see cref="BacktestEngine"/> and the
    /// bootstrap, the stationary block bootstrap step, and the per-path evaluation of a constant-mix strategy on a
    /// resampled history. One bootstrap path is one unit of work (one CUDA thread, one AVX lane or one host chunk element).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kernel rules.</b> Allocation-free, exception-free, no virtual call, plain scalars and flat arrays. The cores
    /// (<see cref="BootstrapRow"/>, <see cref="Rebalance"/>, <see cref="Grow"/>, <see cref="EvaluateConstantMixPath"/>)
    /// touch no Hybridizer type and run as plain C# on any host; only <see cref="SimulateConstantMixPaths"/>, the
    /// grid-stride <c>[EntryPoint]</c>, reads the CUDA thread indices. The host engine calls <see cref="Rebalance"/> and
    /// <see cref="Grow"/> itself, so a constant-mix backtest on a resampled panel and the kernel evaluation of the same
    /// path perform the same floating-point operations in the same order.
    /// </para>
    /// <para>
    /// <b>Stationary bootstrap</b> (Politis and Romano, "The stationary bootstrap", JASA 89, 1994). A resampled history of
    /// <c>T</c> periods is a sequence of source rows <c>I_0 .. I_(T-1)</c>: <c>I_0</c> is uniform on <c>0 .. T-1</c>; then,
    /// with probability <c>p = 1 / L</c>, <c>I_s</c> starts a new block at a uniform row, otherwise it continues the block,
    /// <c>I_s = I_(s-1) + 1</c> wrapped circularly. Block lengths are geometric with mean <c>L</c>; <c>L = 1</c> is the
    /// i.i.d. bootstrap. The draws of step <c>s</c> of path <c>p</c> are the four words of Philox4x32-10 block
    /// (seed, block = s, path = p, stream): words 0-1 give the restart uniform, words 2-3 the start row, both through
    /// <see cref="Philox4x32x10.ToUniformOpen52"/>. Every draw is a pure function of (seed, stream, path, step), so the
    /// results do not depend on threads, chunks or devices: any partition of the path range gives bit-identical outputs.
    /// </para>
    /// <para>
    /// <b>Accounting</b> (money units; a path starts with wealth 1 in cash). At a rebalance with pre-trade value <c>V</c>,
    /// asset <c>i</c> is traded to <c>w_i V</c> when the trade <c>|w_i V - H_i|</c> exceeds <c>tolerance |V|</c>; purchases
    /// are paid from cash, and the cost <c>c_prop sum |trade| + c_fixed (number of trades)</c> is paid from cash too (so the
    /// cash residual absorbs it). Over the period, <c>H_i *= 1 + r_i</c> and <c>cash *= 1 + r_cash</c>; the value is
    /// <c>sum H + cash</c> and the period return <c>V_end / V_start - 1</c> includes the costs. A value at or below zero
    /// wipes the portfolio out: it stays at 0 and later returns are 0.
    /// </para>
    /// <para>
    /// <b>Memory layout.</b> Returns are row-major, <c>returns[row * N + i]</c> (a path reads whole rows at random),
    /// cash returns <c>cashReturns[row]</c> (zeros for no cash). Each path needs N doubles of scratch for its holdings at
    /// <c>scratch[offset .. offset + N)</c>: one row per thread on a device, one per chunk on the host. Outputs are
    /// indexed by path.
    /// </para>
    /// <para>
    /// The class is not static so that a HybRunner can wrap an instance of it to reach the static entry point
    /// (<c>runner.Wrap(new BacktestKernels()).SimulateConstantMixPaths(...)</c>); it has no state.
    /// </para>
    /// </remarks>
    public sealed class BacktestKernels
    {
        /// <summary>Default Philox stream tag of the bootstrap ("BOOT").</summary>
        public const uint DefaultBootstrapStream = 0x424F4F54u;

        /// <summary>
        /// Source row <c>I_s</c> of step <paramref name="step"/> of bootstrap path <paramref name="path"/>, given the previous
        /// row (ignored at step 0).
        /// </summary>
        /// <param name="path">Path index (the Philox path word).</param>
        /// <param name="step">Step s in the resampled history (the Philox block index).</param>
        /// <param name="previousRow">I_(s-1).</param>
        /// <param name="sourceLength">Number of source rows T.</param>
        /// <param name="restartProbability">p = 1 / L in (0, 1].</param>
        /// <param name="seed">Philox key.</param>
        /// <param name="stream">Philox stream tag.</param>
        [Kernel]
        public static int BootstrapRow(int path, int step, int previousRow, int sourceLength, double restartProbability, ulong seed, uint stream)
        {
            uint r0, r1, r2, r3;
            Philox4x32x10.GenerateBlock(seed, (ulong)step, (uint)path, stream, out r0, out r1, out r2, out r3);
            if (step > 0 && Philox4x32x10.ToUniformOpen52(r0, r1) >= restartProbability)
            {
                int next = previousRow + 1;
                return next == sourceLength ? 0 : next;
            }
            int row = (int)(Philox4x32x10.ToUniformOpen52(r2, r3) * sourceLength);
            return row < sourceLength ? row : sourceLength - 1;
        }

        /// <summary>
        /// Trades the holdings <paramref name="holdings"/>[<paramref name="offset"/> + i] to
        /// <paramref name="target"/>[<paramref name="targetOffset"/> + i] times <paramref name="value"/> and returns the new cash.
        /// </summary>
        /// <param name="assetCount">Number of assets N.</param>
        /// <param name="holdings">Money held in each asset (updated in place).</param>
        /// <param name="offset">Start of the holdings row.</param>
        /// <param name="target">Target weights as fractions of <paramref name="value"/>.</param>
        /// <param name="targetOffset">Start of the target row.</param>
        /// <param name="value">Pre-trade portfolio value V.</param>
        /// <param name="cash">Pre-trade cash.</param>
        /// <param name="proportionalCost">Cost per unit of traded notional (10 bps is 0.001).</param>
        /// <param name="fixedCost">Cost per traded asset, in money.</param>
        /// <param name="tradeTolerance">Trades of at most <c>tradeTolerance |V|</c> are skipped.</param>
        /// <param name="notional">Total traded notional <c>sum |trade|</c>.</param>
        /// <param name="cost">Total transaction cost.</param>
        /// <returns>Post-trade cash, after purchases, sales and costs.</returns>
        [Kernel]
        public static double Rebalance(int assetCount, double[] holdings, int offset, double[] target, int targetOffset,
            double value, double cash, double proportionalCost, double fixedCost, double tradeTolerance,
            out double notional, out double cost)
        {
            double threshold = tradeTolerance * Math.Abs(value);
            double traded = 0.0;
            int trades = 0;
            for (int i = 0; i < assetCount; ++i)
            {
                double held = holdings[offset + i];
                double wanted = target[targetOffset + i] * value;
                double trade = wanted - held;
                double size = Math.Abs(trade);
                if (size > threshold)
                {
                    holdings[offset + i] = wanted;
                    cash -= trade;
                    traded += size;
                    trades += 1;
                }
            }
            notional = traded;
            cost = proportionalCost * traded + fixedCost * trades;
            return cash - cost;
        }

        /// <summary>
        /// Grows the holdings over one period, <c>H_i *= 1 + returns[returnOffset + i]</c>, and returns <c>sum H</c> (summed
        /// in asset order).
        /// </summary>
        [Kernel]
        public static double Grow(int assetCount, double[] holdings, int offset, double[] returns, int returnOffset)
        {
            double sum = 0.0;
            for (int i = 0; i < assetCount; ++i)
            {
                double h = holdings[offset + i] * (1.0 + returns[returnOffset + i]);
                holdings[offset + i] = h;
                sum += h;
            }
            return sum;
        }

        /// <summary>
        /// One bootstrap path end to end: resample <paramref name="periodCount"/> rows, run a constant-mix strategy on them
        /// (wealth 1 at the start, decisions at steps <c>0, k, 2k, ...</c>) and write the path statistics.
        /// </summary>
        /// <param name="path">Path index.</param>
        /// <param name="seed">Philox key.</param>
        /// <param name="stream">Philox stream tag.</param>
        /// <param name="restartProbability">1 / mean block length.</param>
        /// <param name="periodCount">Rows T of the source panel (and length of the resampled history).</param>
        /// <param name="assetCount">Assets N.</param>
        /// <param name="returns">Source returns, row-major T x N.</param>
        /// <param name="cashReturns">Source cash returns, length T.</param>
        /// <param name="targetWeights">Constant-mix weights, length N.</param>
        /// <param name="rebalanceEvery">Rebalance interval k (at least 1).</param>
        /// <param name="proportionalCost">Cost per unit of traded notional.</param>
        /// <param name="fixedCost">Cost per traded asset, in units of the initial wealth.</param>
        /// <param name="tradeTolerance">Relative trade tolerance (see <see cref="Rebalance"/>).</param>
        /// <param name="scratch">Holdings scratch.</param>
        /// <param name="scratchOffset">Start of this path's N-element scratch row.</param>
        /// <param name="terminalWealth">Output: final value (initial wealth 1).</param>
        /// <param name="meanReturn">Output: arithmetic mean of the period returns.</param>
        /// <param name="returnVariance">Output: sample variance (divisor T - 1) of the period returns; 0 when T = 1.</param>
        /// <param name="maxDrawdown">Output: largest <c>1 - V_t / max_(s &lt;= t) V_s</c>, with <c>V_0 = 1</c>.</param>
        /// <param name="turnover">Output: sum over rebalances of traded notional over pre-trade value.</param>
        /// <param name="totalCost">Output: sum of the transaction costs.</param>
        [Kernel]
        public static void EvaluateConstantMixPath(int path, ulong seed, uint stream, double restartProbability,
            int periodCount, int assetCount, double[] returns, double[] cashReturns,
            double[] targetWeights, int rebalanceEvery, double proportionalCost, double fixedCost, double tradeTolerance,
            double[] scratch, int scratchOffset,
            double[] terminalWealth, double[] meanReturn, double[] returnVariance, double[] maxDrawdown, double[] turnover, double[] totalCost)
        {
            for (int i = 0; i < assetCount; ++i) scratch[scratchOffset + i] = 0.0;
            double cash = 1.0, value = 1.0, peak = 1.0, drawdown = 0.0;
            double mean = 0.0, m2 = 0.0, turn = 0.0, costs = 0.0;
            bool alive = true;
            int row = 0;
            for (int s = 0; s < periodCount; ++s)
            {
                row = BootstrapRow(path, s, row, periodCount, restartProbability, seed, stream);
                double r = 0.0;
                if (alive)
                {
                    if (s % rebalanceEvery == 0)
                    {
                        double notional, cost;
                        cash = Rebalance(assetCount, scratch, scratchOffset, targetWeights, 0, value, cash,
                            proportionalCost, fixedCost, tradeTolerance, out notional, out cost);
                        turn += notional / value;
                        costs += cost;
                    }
                    double risky = Grow(assetCount, scratch, scratchOffset, returns, row * assetCount);
                    cash *= 1.0 + cashReturns[row];
                    double end = risky + cash;
                    if (end <= 0.0)
                    {
                        end = 0.0;
                        cash = 0.0;
                        for (int i = 0; i < assetCount; ++i) scratch[scratchOffset + i] = 0.0;
                        alive = false;
                    }
                    r = end / value - 1.0;
                    value = end;
                }

                // Welford update of the mean and the sum of squared deviations.
                double delta = r - mean;
                mean += delta / (s + 1);
                m2 += delta * (r - mean);

                if (value > peak) peak = value;
                double dd = 1.0 - value / peak;
                if (dd > drawdown) drawdown = dd;
            }
            terminalWealth[path] = value;
            meanReturn[path] = mean;
            returnVariance[path] = periodCount > 1 ? m2 / (periodCount - 1) : 0.0;
            maxDrawdown[path] = drawdown;
            turnover[path] = turn;
            totalCost[path] = costs;
        }

        /// <summary>
        /// Device entry point: paths [<paramref name="pathFrom"/>, <paramref name="pathTo"/>) in a grid-stride loop, one path
        /// per thread at a time. Thread <c>t = threadIdx.x + blockDim.x * blockIdx.x</c> uses the scratch row
        /// <c>scratch[t * assetCount ..]</c>, so <paramref name="scratch"/> must hold at least
        /// <c>gridDim.x * blockDim.x * assetCount</c> doubles. Other arguments are those of <see cref="EvaluateConstantMixPath"/>.
        /// </summary>
        [EntryPoint("BacktestKernels_SimulateConstantMixPaths")]
        public static void SimulateConstantMixPaths(int pathFrom, int pathTo, ulong seed, uint stream, double restartProbability,
            int periodCount, int assetCount, double[] returns, double[] cashReturns,
            double[] targetWeights, int rebalanceEvery, double proportionalCost, double fixedCost, double tradeTolerance,
            double[] scratch,
            double[] terminalWealth, double[] meanReturn, double[] returnVariance, double[] maxDrawdown, double[] turnover, double[] totalCost)
        {
            int thread = threadIdx.x + blockDim.x * blockIdx.x;
            int stride = blockDim.x * gridDim.x;
            int scratchOffset = thread * assetCount;
            for (int p = pathFrom + thread; p < pathTo; p += stride)
            {
                EvaluateConstantMixPath(p, seed, stream, restartProbability, periodCount, assetCount, returns, cashReturns,
                    targetWeights, rebalanceEvery, proportionalCost, fixedCost, tradeTolerance, scratch, scratchOffset,
                    terminalWealth, meanReturn, returnVariance, maxDrawdown, turnover, totalCost);
            }
        }
    }
}
