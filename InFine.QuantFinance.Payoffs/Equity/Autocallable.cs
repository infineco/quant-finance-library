using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Payoffs.Concepts;
using InFine.QuantFinance.Scheduling;
using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Payoffs.Equity
{
    /// <summary>
    /// Hybridizer-free numerics of the worst-of autocallable (Phoenix / Athena), shared by the kernel and the unit tests.
    /// </summary>
    /// <remarks>
    /// Cash flows per unit notional, observation dates <c>t_0 &lt; ... &lt; t_{n-1}</c> (the last is the maturity), worst
    /// performance <c>W(t) = min_d S_d(t) / reference_d</c>, all paid at the observation date and discounted from it:
    /// <list type="number">
    /// <item>Coupon: if <c>W(t_i) &gt;= couponBarrier_i</c>, pay <c>coupon_i</c> plus, with memory, every coupon missed
    /// since the last one paid; without memory a missed coupon is lost.</item>
    /// <item>Autocall (i &lt; n - 1): if <c>W(t_i) &gt;= autocallBarrier_i</c>, repay the notional and stop.</item>
    /// <item>Maturity: repay <c>1 - max(K - W(t_{n-1}), 0) / K</c> if the capital-protection barrier was knocked in
    /// (<c>W &lt;= knockInBarrier</c> at one of the knock-in monitoring pillars: the maturity only for a European barrier,
    /// every pillar for a discretely monitored one), 1 otherwise. With <c>K = 1</c> the investor receives <c>W(T)</c>.</item>
    /// </list>
    /// An Athena is the special case coupon barrier = autocall barrier with memory; a Phoenix has a lower coupon barrier.
    /// The per-path result is the discounted sum of the cash flows and the index of the date of redemption (early, or
    /// n - 1 at maturity), from which <see cref="AutocallSummary"/> derives the redemption probabilities and expected life.
    /// </remarks>
    public static class AutocallMath
    {
        /// <summary>
        /// Coupon paid at an observation date: <c>coupon + missed</c> when <c>W &gt;= barrier</c>, 0 otherwise
        /// (<paramref name="missed"/> is the memory of unpaid coupons, see <see cref="MissedCoupons"/>).
        /// </summary>
        [Kernel]
        public static double CouponPaid(double worst, double couponBarrier, double coupon, double missed)
        {
            return worst >= couponBarrier ? coupon + missed : 0.0;
        }

        /// <summary>
        /// Memory after an observation date: cleared when the coupon is paid, increased by the coupon when it is missed
        /// with <paramref name="memory"/>, and always 0 without memory.
        /// </summary>
        [Kernel]
        public static double MissedCoupons(double worst, double couponBarrier, double coupon, bool memory, double missed)
        {
            return worst >= couponBarrier ? 0.0 : (memory ? missed + coupon : 0.0);
        }

        /// <summary>
        /// Weight of the paths redeemed early at this date: <paramref name="alive"/> (1 while not yet redeemed, 0 after)
        /// when <c>W &gt;= autocallBarrier</c>, 0 otherwise.
        /// </summary>
        [Kernel]
        public static double CalledWeight(double worst, double autocallBarrier, double alive)
        {
            return worst >= autocallBarrier ? alive : 0.0;
        }

        /// <summary>Redemption at maturity: <c>1 - max(K - W, 0) / K</c> if knocked in, 1 otherwise.</summary>
        [Kernel]
        public static double FinalRedemption(bool knockedIn, double putStrike, double worst)
        {
            return knockedIn ? 1.0 - MultiAssetMath.CallPut(-1, worst, putStrike) / putStrike : 1.0;
        }

        /// <summary>
        /// Discounted cash flows of one path (layout of <see cref="MultiAssetMath"/>) and the index of its redemption date.
        /// </summary>
        /// <param name="path">Spots, asset d at pillar t in <c>path[t * D + d]</c>.</param>
        /// <param name="assetCount">D.</param>
        /// <param name="reference">Reference levels.</param>
        /// <param name="discount">Discount factors at the pillars.</param>
        /// <param name="observations">Observation pillars, ascending; the last one is the maturity.</param>
        /// <param name="autocallBarriers">Autocall barrier per observation (the last one is not used).</param>
        /// <param name="couponBarriers">Coupon barrier per observation.</param>
        /// <param name="coupons">Coupon per observation, per unit notional.</param>
        /// <param name="memory">Memory coupons.</param>
        /// <param name="knockInMonitor">Pillars where the capital-protection barrier is observed.</param>
        /// <param name="knockInBarrier">Capital-protection barrier on the worst performance.</param>
        /// <param name="putStrike">Strike of the embedded put (usually 1).</param>
        /// <param name="exitIndex">Index in <paramref name="observations"/> of the redemption date.</param>
        public static double PathPremium(double[] path, int assetCount, double[] reference, double[] discount,
            int[] observations, double[] autocallBarriers, double[] couponBarriers, double[] coupons, bool memory,
            int[] knockInMonitor, double knockInBarrier, double putStrike, out int exitIndex)
        {
            // Same flag arithmetic as the kernel (no early exit, so that vector lanes stay in step): alive is 1 until
            // the path is redeemed, then 0, and every later cash flow is multiplied by it.
            int last = observations.Length - 1;
            double missed = 0.0, pv = 0.0, alive = 1.0, exit = last;
            for (int i = 0; i < last; ++i)
            {
                int pillar = observations[i];
                double worst = MultiAssetMath.ExtremePerformance(false, path, assetCount, pillar, reference);
                double paid = CouponPaid(worst, couponBarriers[i], coupons[i], missed);
                missed = MissedCoupons(worst, couponBarriers[i], coupons[i], memory, missed);
                double called = CalledWeight(worst, autocallBarriers[i], alive);
                exit = called > 0.0 ? i : exit;
                pv += alive * paid * discount[pillar] + called * discount[pillar];
                alive -= called;
            }
            int maturity = observations[last];
            double finalWorst = MultiAssetMath.ExtremePerformance(false, path, assetCount, maturity, reference);
            double finalCoupon = CouponPaid(finalWorst, couponBarriers[last], coupons[last], missed);
            bool knockedIn = MultiAssetMath.LowestWorstPerformance(path, assetCount, knockInMonitor, reference) <= knockInBarrier;
            pv += alive * (finalCoupon + FinalRedemption(knockedIn, putStrike, finalWorst)) * discount[maturity];
            exitIndex = (int)exit;
            return pv;
        }
    }

    /// <summary>
    /// Worst-of autocallable note (Phoenix / Athena) on D assets, per unit notional; the cash flows are documented on
    /// <see cref="AutocallMath"/>. The kernel writes the discounted cash flows of each path to <c>premium</c> and the
    /// index of its redemption date (0 .. n - 1, as a double) to <c>exitIndex</c>; <see cref="AutocallSummary"/> turns the
    /// two arrays into the price, the redemption probability of each date and the expected life.
    /// </summary>
    /// <typeparam name="Discounting">Discounting curve</typeparam>
    /// <typeparam name="Spot">Multi-asset spot, asset d at pillar t in column <c>t * D + d</c></typeparam>
    /// <typeparam name="Schedule">Time schedule of the pricing</typeparam>
    /// <typeparam name="Premium">Output storage (premium and redemption index)</typeparam>
    [HybridRegisterTemplate(Specialize = typeof(Autocallable<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>))]
    [HybridRegisterTemplate(Specialize = typeof(Autocallable<CurveDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>))]
    public class Autocallable<Discounting, Spot, Schedule, Premium> : ISimulatable
        where Discounting : IDiscounting
        where Spot : ISpot
        where Schedule : ISchedule
        where Premium : IPremium
    {
        Spot _underlying;
        Discounting _discounting;
        Schedule _schedule;
        Premium _premium;
        Premium _exitIndex;

        int _assetCount;
        double[] _reference;
        int[] _observations;
        int _observationCount;
        double[] _autocallBarriers;
        double[] _couponBarriers;
        double[] _coupons;
        bool _memory;
        int[] _knockInMonitor;
        int _knockInCount;
        double _knockInBarrier;
        double _putStrike;

        /// <summary>Creates an autocallable; see <see cref="AutocallMath.PathPremium"/> for the parameters.</summary>
        /// <param name="underlying">Multi-asset spot</param>
        /// <param name="discounting">Discounting curve</param>
        /// <param name="schedule">Pricing schedule</param>
        /// <param name="premium">Output: discounted cash flows of each path</param>
        /// <param name="exitIndex">Output: index of the redemption date of each path</param>
        /// <param name="assetCount">Number of assets D</param>
        /// <param name="reference">Reference levels (initial fixings)</param>
        /// <param name="observations">Observation pillars, strictly ascending; the last one is the maturity</param>
        /// <param name="autocallBarriers">Autocall barrier per observation (the last one is not used)</param>
        /// <param name="couponBarriers">Coupon barrier per observation</param>
        /// <param name="coupons">Coupon per observation</param>
        /// <param name="memory">Memory coupons</param>
        /// <param name="knockInMonitor">Knock-in monitoring pillars, strictly ascending, at most the maturity</param>
        /// <param name="knockInBarrier">Capital-protection barrier</param>
        /// <param name="putStrike">Strike of the embedded put, positive (usually 1)</param>
        public Autocallable(Spot underlying, Discounting discounting, Schedule schedule, Premium premium, Premium exitIndex,
            int assetCount, double[] reference, int[] observations, double[] autocallBarriers, double[] couponBarriers,
            double[] coupons, bool memory, int[] knockInMonitor, double knockInBarrier, double putStrike)
        {
            MultiAssetMath.CheckAssets(assetCount, reference, null);
            MultiAssetMath.CheckPillars(observations, int.MaxValue, nameof(observations));
            int n = observations.Length;
            if (autocallBarriers == null || autocallBarriers.Length != n) throw new ArgumentException($"Expected {n} autocall barriers.", nameof(autocallBarriers));
            if (couponBarriers == null || couponBarriers.Length != n) throw new ArgumentException($"Expected {n} coupon barriers.", nameof(couponBarriers));
            if (coupons == null || coupons.Length != n) throw new ArgumentException($"Expected {n} coupons.", nameof(coupons));
            MultiAssetMath.CheckPillars(knockInMonitor, observations[n - 1], nameof(knockInMonitor));
            if (!(putStrike > 0.0)) throw new ArgumentOutOfRangeException(nameof(putStrike), "The put strike must be positive.");
            _underlying = underlying;
            _discounting = discounting;
            _schedule = schedule;
            _premium = premium;
            _exitIndex = exitIndex;
            _assetCount = assetCount;
            _reference = (double[])reference.Clone();
            _observations = (int[])observations.Clone();
            _observationCount = n;
            _autocallBarriers = (double[])autocallBarriers.Clone();
            _couponBarriers = (double[])couponBarriers.Clone();
            _coupons = (double[])coupons.Clone();
            _memory = memory;
            _knockInMonitor = (int[])knockInMonitor.Clone();
            _knockInCount = knockInMonitor.Length;
            _knockInBarrier = knockInBarrier;
            _putStrike = putStrike;
        }

        /// <summary>Monte Carlo simulation of the product: discounted cash flows and redemption index of each path.</summary>
        [Kernel]
        public void Simulate(int simFrom, int simTo)
        {
            int last = _observationCount - 1;
            for (alignedindex simId = VectorUnit.ID + simFrom; simId < simTo; simId += VectorUnit.Count)
            {
                // no early exit: alive is 1 until redemption, 0 after (see AutocallMath.PathPremium)
                double missed = 0.0, pv = 0.0, alive = 1.0, exit = last;
                for (int i = 0; i < last; ++i)
                {
                    int pillar = _observations[i];
                    double worst = WorstPerformance(simId, pillar);
                    double paid = AutocallMath.CouponPaid(worst, _couponBarriers[i], _coupons[i], missed);
                    missed = AutocallMath.MissedCoupons(worst, _couponBarriers[i], _coupons[i], _memory, missed);
                    double called = AutocallMath.CalledWeight(worst, _autocallBarriers[i], alive);
                    exit = called > 0.0 ? i : exit;
                    double df = _discounting[simId, pillar];
                    pv += alive * paid * df + called * df;
                    alive -= called;
                }
                int maturity = _observations[last];
                double finalWorst = WorstPerformance(simId, maturity);
                double finalCoupon = AutocallMath.CouponPaid(finalWorst, _couponBarriers[last], _coupons[last], missed);
                double low = double.PositiveInfinity;
                for (int j = 0; j < _knockInCount; ++j)
                    low = Math.Min(low, WorstPerformance(simId, _knockInMonitor[j]));
                pv += alive * (finalCoupon + AutocallMath.FinalRedemption(low <= _knockInBarrier, _putStrike, finalWorst))
                    * _discounting[simId, maturity];
                _premium[simId] = pv;
                _exitIndex[simId] = exit;
            }
        }

        [Kernel]
        double WorstPerformance(alignedindex simId, int pillar)
        {
            int column = pillar * _assetCount;
            double worst = _underlying[simId, column] / _reference[0];
            for (int d = 1; d < _assetCount; ++d)
                worst = Math.Min(worst, _underlying[simId, column + d] / _reference[d]);
            return worst;
        }
    }

    /// <summary>
    /// Host summary of an autocallable Monte Carlo run: price, standard error, probability of redemption at each
    /// observation date (early redemption before the last date, redemption at maturity on the last one; they sum to 1)
    /// and expected life <c>sum_i p_i t_i</c>.
    /// </summary>
    public sealed class AutocallSummary
    {
        AutocallSummary(double price, double standardError, double[] probabilities, double expectedLife)
        {
            Price = price;
            StandardError = standardError;
            RedemptionProbabilities = probabilities;
            ExpectedLife = expectedLife;
        }

        /// <summary>Mean of the discounted cash flows, per unit notional.</summary>
        public double Price { get; }

        /// <summary>Monte Carlo standard error of <see cref="Price"/>.</summary>
        public double StandardError { get; }

        /// <summary>Fraction of paths redeemed at each observation date.</summary>
        public double[] RedemptionProbabilities { get; }

        /// <summary>Expected redemption time, in the units of the observation times (years).</summary>
        public double ExpectedLife { get; }

        /// <summary>Builds the summary from the per-path outputs of the kernel or of <see cref="AutocallMath.PathPremium"/>.</summary>
        /// <param name="premiums">Discounted cash flows per path.</param>
        /// <param name="exitIndices">Redemption date index per path (integers stored as doubles).</param>
        /// <param name="observationTimes">Time of each observation date, in years.</param>
        public static AutocallSummary Create(double[] premiums, double[] exitIndices, double[] observationTimes)
        {
            if (premiums == null) throw new ArgumentNullException(nameof(premiums));
            if (exitIndices == null) throw new ArgumentNullException(nameof(exitIndices));
            if (observationTimes == null) throw new ArgumentNullException(nameof(observationTimes));
            int paths = premiums.Length, n = observationTimes.Length;
            if (paths < 2) throw new ArgumentException("At least two paths are required.", nameof(premiums));
            if (exitIndices.Length != paths) throw new ArgumentException("One redemption index per path is required.", nameof(exitIndices));
            var counts = new long[n];
            double sum = 0.0, sumSquares = 0.0;
            for (int p = 0; p < paths; ++p)
            {
                double index = exitIndices[p];
                int i = (int)index;
                if (i != index || i < 0 || i >= n)
                    throw new ArgumentOutOfRangeException(nameof(exitIndices), $"Path {p} has redemption index {index}.");
                counts[i]++;
                sum += premiums[p];
                sumSquares += premiums[p] * premiums[p];
            }
            double mean = sum / paths;
            double variance = Math.Max(sumSquares / paths - mean * mean, 0.0) * paths / (paths - 1);
            var probabilities = new double[n];
            double life = 0.0;
            for (int i = 0; i < n; ++i)
            {
                probabilities[i] = (double)counts[i] / paths;
                life += probabilities[i] * observationTimes[i];
            }
            return new AutocallSummary(mean, Math.Sqrt(variance / paths), probabilities, life);
        }
    }
}
