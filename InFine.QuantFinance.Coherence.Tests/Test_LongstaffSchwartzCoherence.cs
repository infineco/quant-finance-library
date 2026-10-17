using System;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.TestSupport;
using Xunit;

namespace InFine.QuantFinance.Coherence.Tests
{
    /// <summary>
    /// Longstaff-Schwartz American (Bermudan) put on Philox paths: price per executor.
    /// </summary>
    /// <remarks>
    /// What is covered: the managed <c>LongstaffSchwartz&lt;..., TExecutor&gt;.Calculate</c> with the three
    /// executors that exist on the managed runtime - <c>SequentialExecutor</c>, <c>SingleBlockParallelExecutor</c>
    /// and <c>MultiBlockParallelExecutor</c> (<c>Parallel.For</c>) - fed with paths diffused by every backend. The
    /// regression sums are always taken by the serial reduction (<c>GetHelperSerialReduction</c>), so the price is
    /// bit-identical across executors.
    /// <para>
    /// What is not covered (gap): <c>GetHelperParallelReduction</c> (<c>SingleBlockCudaParallelReduction</c>, warp
    /// shuffles and shared memory) and the HybRunner entry points <c>LSEntryPoint.Calculate*</c> only exist on a
    /// CUDA/AVX build of the library; they cannot be launched without the hybridized binaries, and the only
    /// executor-parametrized LS entry points (<c>Calculate_Paper</c>, <c>CalculateSequential</c>) are bound to the
    /// 8-path paper data set by their template registrations. A GPU price would also differ from the managed one
    /// in the last digits (different reduction order), so it needs a tolerance, not equality.
    /// </para>
    /// </remarks>
    public class Test_LongstaffSchwartzCoherence
    {
        private const int Paths = 4003; // prime
        private const int Steps = Market.BlackScholesSteps;
        private const double Strike = 100.0;

        [HybridizerTheory]
        [MemberData(nameof(ExecutorBackends.ManagedNames), MemberType = typeof(ExecutorBackends))]
        public void AmericanPutPrice_IsBitIdenticalAcrossBackends(string backendName)
        {
            ExecutorBackend backend = ExecutorBackends.Get(backendName);
            double reference = Workloads.AmericanPutPrice(ExecutorBackends.Reference, Paths, Steps, Strike);
            double actual = Workloads.AmericanPutPrice(backend, Paths, Steps, Strike);
            Assert.Equal(BitConverter.DoubleToInt64Bits(reference), BitConverter.DoubleToInt64Bits(actual));
        }

        [HybridizerTheory]
        [MemberData(nameof(ExecutorBackends.ManagedNames), MemberType = typeof(ExecutorBackends))]
        public void AmericanPutPrice_AgreesWithAnIndependentBermudanTree(string backendName)
        {
            // Independent reference: Cox-Ross-Rubinstein tree with exercise allowed on the same 12 dates (and not
            // at t = 0, as in the Longstaff-Schwartz loop), 600 steps per interval. The Longstaff-Schwartz estimate
            // is low-biased (suboptimal exercise rule) and noisy: with 4003 paths the standard error of the mean
            // payoff is about 8.5 / sqrt(4003) = 0.13; tolerance 0.6 (4.5 sigma) around the tree, and the price must
            // exceed the European put (closed form), which a Bermudan put dominates.
            ExecutorBackend backend = ExecutorBackends.Get(backendName);
            double price = Workloads.AmericanPutPrice(backend, Paths, Steps, Strike);
            double bermudan = BermudanPutTree(Market.Spot, Strike, Market.Rate, Market.Volatility, Market.Maturity, Steps, 600);

            BlackScholesMertonPrimitive.CallPut(out double call, out double europeanPut, Market.Spot, Strike, Market.Rate, Market.Volatility, Market.Maturity);
            Assert.InRange(price, bermudan - 0.6, bermudan + 0.6);
            Assert.True(bermudan > europeanPut, "the Bermudan reference must dominate the European put");
            Assert.True(price > europeanPut - 0.6, $"LS price {price} below European put {europeanPut}");
        }

        /// <summary>
        /// CRR binomial tree of a put exercisable at the <paramref name="exerciseDates"/> equally spaced dates
        /// 1..exerciseDates (the last being maturity), <paramref name="stepsPerInterval"/> tree steps between two dates.
        /// </summary>
        internal static double BermudanPutTree(double spot, double strike, double rate, double sigma, double maturity, int exerciseDates, int stepsPerInterval)
        {
            int n = exerciseDates * stepsPerInterval;
            double dt = maturity / n;
            double up = Math.Exp(sigma * Math.Sqrt(dt));
            double down = 1.0 / up;
            double probUp = (Math.Exp(rate * dt) - down) / (up - down);
            double discount = Math.Exp(-rate * dt);

            var power = new double[2 * n + 1]; // power[m + n] = up^m
            for (int m = -n; m <= n; ++m)
                power[m + n] = Math.Pow(up, m);

            var value = new double[n + 1];
            for (int i = 0; i <= n; ++i)
                value[i] = Math.Max(strike - spot * power[2 * i - n + n], 0.0); // i up moves out of n

            for (int step = n - 1; step >= 0; --step)
            {
                bool exercisable = step > 0 && step % stepsPerInterval == 0;
                for (int i = 0; i <= step; ++i)
                {
                    double continuation = discount * (probUp * value[i + 1] + (1.0 - probUp) * value[i]);
                    value[i] = exercisable
                        ? Math.Max(continuation, Math.Max(strike - spot * power[2 * i - step + n], 0.0))
                        : continuation;
                }
            }
            return value[0];
        }
    }
}
