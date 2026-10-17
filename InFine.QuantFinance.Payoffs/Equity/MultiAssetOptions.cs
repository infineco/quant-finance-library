using InFine.QuantFinance.Arithmetic.AutoDiff;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Payoffs.Concepts;
using InFine.QuantFinance.Scheduling;
using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Payoffs.Equity
{
    /// <summary>
    /// Hybridizer-free numerics of the multi-asset payoffs, shared by the kernels and the unit tests, with
    /// <see cref="Dual4"/> overloads of the basket and rainbow payoffs for tangent-mode pathwise Greeks.
    /// <para>
    /// Paths follow the multi-asset convention of <c>CorrelatedLogNormalMath</c> (InFine.QuantFinance.Models): asset d at
    /// pillar t is column <c>t * D + d</c>. The per-path helpers take one path as a flat array in that layout. Payoffs are
    /// written on performances <c>P_d(t) = S_d(t) / reference_d</c> (pass the initial fixings as references for a
    /// structured product, ones for a payoff on levels); strikes and barriers are then in performance units.
    /// </para>
    /// </summary>
    public static class MultiAssetMath
    {
        /// <summary>Basket term of one asset: <c>w P</c> (arithmetic) or <c>w ln P</c> (geometric).</summary>
        [Kernel]
        public static double BasketTerm(bool geometric, double weight, double performance)
        {
            return geometric ? weight * Math.Log(performance) : weight * performance;
        }

        /// <summary>Basket from the sum of <see cref="BasketTerm(bool, double, double)"/>: the sum itself, or its exponential (geometric).</summary>
        [Kernel]
        public static double BasketValue(bool geometric, double sum)
        {
            return geometric ? Math.Exp(sum) : sum;
        }

        /// <summary>Running extreme: the larger of the two for a best-of, the smaller for a worst-of.</summary>
        [Kernel]
        public static double Extreme(bool best, double current, double candidate)
        {
            return best ? Math.Max(current, candidate) : Math.Min(current, candidate);
        }

        /// <summary>Vanilla payoff <c>max(sign (x - K), 0)</c>.</summary>
        [Kernel]
        public static double CallPut(int sign, double underlying, double strike)
        {
            double intrinsic = sign * (underlying - strike);
            return intrinsic > 0.0 ? intrinsic : 0.0;
        }

        /// <summary>Down-and-in put payoff <c>max(K - W, 0)</c> when knocked in, 0 otherwise.</summary>
        [Kernel]
        public static double DownAndInPut(bool knockedIn, double strike, double worst)
        {
            return knockedIn ? CallPut(-1, worst, strike) : 0.0;
        }

        /// <summary>Best (or worst) performance <c>S_d(t) / reference_d</c> over the D assets of a path at pillar t.</summary>
        public static double ExtremePerformance(bool best, double[] path, int assetCount, int pillar, double[] reference)
        {
            int column = pillar * assetCount;
            double e = path[column] / reference[0];
            for (int d = 1; d < assetCount; ++d)
                e = Extreme(best, e, path[column + d] / reference[d]);
            return e;
        }

        /// <summary>Smallest worst performance over the monitoring pillars of a path.</summary>
        public static double LowestWorstPerformance(double[] path, int assetCount, int[] monitor, double[] reference)
        {
            double low = double.PositiveInfinity;
            for (int j = 0; j < monitor.Length; ++j)
                low = Math.Min(low, ExtremePerformance(false, path, assetCount, monitor[j], reference));
            return low;
        }

        /// <summary>
        /// Undiscounted basket option payoff <c>max(sign (B - K), 0)</c> of a path, with
        /// <c>B = sum_d w_d P_d(T)</c> (arithmetic) or <c>B = prod_d P_d(T)^{w_d}</c> (geometric).
        /// </summary>
        public static double BasketPathPayoff(double[] path, int assetCount, int maturity, double[] weights, double[] reference,
            int sign, bool geometric, double strike)
        {
            int column = maturity * assetCount;
            double sum = 0.0;
            for (int d = 0; d < assetCount; ++d)
                sum += BasketTerm(geometric, weights[d], path[column + d] / reference[d]);
            return CallPut(sign, BasketValue(geometric, sum), strike);
        }

        /// <summary>Undiscounted best-of / worst-of payoff <c>max(sign (E - K), 0)</c>, E the best or worst performance at T.</summary>
        public static double RainbowPathPayoff(double[] path, int assetCount, int maturity, double[] reference, int sign, bool best, double strike)
        {
            return CallPut(sign, ExtremePerformance(best, path, assetCount, maturity, reference), strike);
        }

        /// <summary>
        /// Undiscounted worst-of down-and-in put: <c>max(K - W(T), 0)</c> if the worst performance was on or below the
        /// barrier at one of the monitoring pillars (<c>{maturity}</c> for a European barrier), 0 otherwise.
        /// </summary>
        public static double WorstOfDownAndInPutPathPayoff(double[] path, int assetCount, int[] monitor, int maturity, double[] reference,
            double strike, double barrier)
        {
            bool knockedIn = LowestWorstPerformance(path, assetCount, monitor, reference) <= barrier;
            return DownAndInPut(knockedIn, strike, ExtremePerformance(false, path, assetCount, maturity, reference));
        }

        /// <summary><see cref="BasketTerm(bool, double, double)"/> on a dual performance (tangent-mode Greeks).</summary>
        [Kernel]
        public static Dual4 BasketTerm(bool geometric, double weight, Dual4 performance)
        {
            return geometric ? weight * Dual4.Log(performance) : weight * performance;
        }

        /// <summary><see cref="BasketValue(bool, double)"/> on a dual sum (tangent-mode Greeks).</summary>
        [Kernel]
        public static Dual4 BasketValue(bool geometric, Dual4 sum)
        {
            return geometric ? Dual4.Exp(sum) : sum;
        }

        /// <summary>
        /// <see cref="Extreme(bool, double, double)"/> on duals: on a tie the current extreme (the asset seen first) is
        /// kept, value and tangents (<see cref="Dual4.Max"/> / <see cref="Dual4.Min"/> convention).
        /// </summary>
        [Kernel]
        public static Dual4 Extreme(bool best, Dual4 current, Dual4 candidate)
        {
            return best ? Dual4.Max(current, candidate) : Dual4.Min(current, candidate);
        }

        /// <summary>
        /// <see cref="CallPut(int, double, double)"/> on a dual underlying; an intrinsic value of exactly 0 is differentiated
        /// as exercised (<see cref="Dual4.Max"/> convention).
        /// </summary>
        [Kernel]
        public static Dual4 CallPut(int sign, Dual4 underlying, double strike)
        {
            return Dual4.Max(sign * (underlying - strike), 0.0);
        }

        /// <summary><see cref="ExtremePerformance(bool, double[], int, int, double[])"/> on a dual path.</summary>
        public static Dual4 ExtremePerformance(bool best, Dual4[] path, int assetCount, int pillar, double[] reference)
        {
            int column = pillar * assetCount;
            Dual4 e = path[column] / reference[0];
            for (int d = 1; d < assetCount; ++d)
                e = Extreme(best, e, path[column + d] / reference[d]);
            return e;
        }

        /// <summary>
        /// <see cref="BasketPathPayoff(double[], int, int, double[], double[], int, bool, double)"/> on a dual path, same
        /// arithmetic.
        /// </summary>
        public static Dual4 BasketPathPayoff(Dual4[] path, int assetCount, int maturity, double[] weights, double[] reference,
            int sign, bool geometric, double strike)
        {
            int column = maturity * assetCount;
            Dual4 sum = 0.0;
            for (int d = 0; d < assetCount; ++d)
                sum += BasketTerm(geometric, weights[d], path[column + d] / reference[d]);
            return CallPut(sign, BasketValue(geometric, sum), strike);
        }

        /// <summary>
        /// <see cref="RainbowPathPayoff(double[], int, int, double[], int, bool, double)"/> on a dual path. The worst-of
        /// (best-of) is Lipschitz and differentiable except where two performances tie, so its pathwise derivative is that
        /// of the asset attaining the extreme.
        /// </summary>
        public static Dual4 RainbowPathPayoff(Dual4[] path, int assetCount, int maturity, double[] reference, int sign, bool best, double strike)
        {
            return CallPut(sign, ExtremePerformance(best, path, assetCount, maturity, reference), strike);
        }

        /// <summary>Host-side check shared by the kernel classes' constructors.</summary>
        internal static void CheckAssets(int assetCount, double[] reference, double[] weights)
        {
            if (assetCount < 1) throw new ArgumentOutOfRangeException(nameof(assetCount), "At least one asset is required.");
            if (reference == null) throw new ArgumentNullException(nameof(reference));
            if (reference.Length != assetCount) throw new ArgumentException($"Expected {assetCount} reference levels.", nameof(reference));
            for (int d = 0; d < assetCount; ++d)
                if (!(reference[d] > 0.0)) throw new ArgumentOutOfRangeException(nameof(reference), "Reference levels must be positive.");
            if (weights != null && weights.Length != assetCount) throw new ArgumentException($"Expected {assetCount} weights.", nameof(weights));
        }

        /// <summary>Host-side check of an ascending list of pillar indices, all at most <paramref name="last"/>.</summary>
        internal static void CheckPillars(int[] pillars, int last, string name)
        {
            if (pillars == null) throw new ArgumentNullException(name);
            if (pillars.Length == 0) throw new ArgumentException("At least one pillar is required.", name);
            for (int j = 0; j < pillars.Length; ++j)
            {
                if (pillars[j] < 0 || pillars[j] > last) throw new ArgumentOutOfRangeException(name, $"Pillar {pillars[j]} is outside [0, {last}].");
                if (j > 0 && pillars[j] <= pillars[j - 1]) throw new ArgumentException("Pillars must be strictly ascending.", name);
            }
        }
    }

    /// <summary>
    /// Basket option on D assets: <c>max(sign (B - K), 0)</c> paid at the maturity pillar, with the weighted arithmetic
    /// basket <c>B = sum_d w_d S_d(T) / reference_d</c> or, with <c>geometric</c>, <c>B = prod_d (S_d(T) / reference_d)^{w_d}</c>.
    /// The geometric basket has a closed form (<c>MultiAssetKernels.GeometricBasketPrice</c>); run it on the same paths
    /// and combine the two premium arrays with <see cref="AsianControlVariate.Estimate(double[], double[], double)"/> for
    /// the control-variate price of the arithmetic basket. Negative weights are allowed (spread and exchange options).
    /// </summary>
    /// <typeparam name="Discounting">Discounting curve</typeparam>
    /// <typeparam name="Spot">Multi-asset spot, asset d at pillar t in column <c>t * D + d</c></typeparam>
    /// <typeparam name="Schedule">Time schedule of the pricing</typeparam>
    /// <typeparam name="Premium">Output premium</typeparam>
    [HybridRegisterTemplate(Specialize = typeof(BasketOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>))]
    [HybridRegisterTemplate(Specialize = typeof(BasketOption<CurveDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>))]
    public class BasketOption<Discounting, Spot, Schedule, Premium> : ISimulatable
        where Discounting : IDiscounting
        where Spot : ISpot
        where Schedule : ISchedule
        where Premium : IPremium
    {
        Spot _underlying;
        Discounting _discounting;
        Schedule _schedule;
        Premium _premium;

        int _assetCount;
        double[] _weights;
        double[] _reference;
        int _maturity;
        double _strike;
        int _sign;
        bool _geometric;

        /// <summary>Creates a basket option.</summary>
        /// <param name="underlying">Multi-asset spot</param>
        /// <param name="discounting">Discounting curve</param>
        /// <param name="schedule">Pricing schedule</param>
        /// <param name="premium">Output premium</param>
        /// <param name="assetCount">Number of assets D</param>
        /// <param name="weights">Basket weights (D entries)</param>
        /// <param name="reference">Reference levels (D entries, positive): ones for levels, initial fixings for performances</param>
        /// <param name="maturity">Maturity (and payment) pillar index</param>
        /// <param name="strike">Strike, in the units of the basket</param>
        /// <param name="sign">+1 for a call, -1 for a put</param>
        /// <param name="geometric">true for the geometric basket</param>
        public BasketOption(Spot underlying, Discounting discounting, Schedule schedule, Premium premium,
            int assetCount, double[] weights, double[] reference, int maturity, double strike, int sign, bool geometric)
        {
            MultiAssetMath.CheckAssets(assetCount, reference, weights ?? throw new ArgumentNullException(nameof(weights)));
            _underlying = underlying;
            _discounting = discounting;
            _schedule = schedule;
            _premium = premium;
            _assetCount = assetCount;
            _weights = (double[])weights.Clone();
            _reference = (double[])reference.Clone();
            _maturity = maturity;
            _strike = strike;
            _sign = sign;
            _geometric = geometric;
        }

        /// <summary>Monte Carlo simulation of the product: _premium receives the discounted payoff of each path.</summary>
        [Kernel]
        public void Simulate(int simFrom, int simTo)
        {
            int column = _maturity * _assetCount;
            for (alignedindex simId = VectorUnit.ID + simFrom; simId < simTo; simId += VectorUnit.Count)
            {
                double sum = 0.0;
                for (int d = 0; d < _assetCount; ++d)
                    sum += MultiAssetMath.BasketTerm(_geometric, _weights[d], _underlying[simId, column + d] / _reference[d]);
                _premium[simId] = MultiAssetMath.CallPut(_sign, MultiAssetMath.BasketValue(_geometric, sum), _strike)
                    * _discounting[simId, _maturity];
            }
        }
    }

    /// <summary>
    /// Best-of / worst-of (rainbow) option on D assets: <c>max(sign (E - K), 0)</c> paid at the maturity pillar, with
    /// <c>E = max_d</c> (best) or <c>min_d</c> (worst) of the performances <c>S_d(T) / reference_d</c>. For two assets
    /// the closed form is Stulz's (<c>MultiAssetKernels.MinMaxPrice</c>).
    /// </summary>
    /// <typeparam name="Discounting">Discounting curve</typeparam>
    /// <typeparam name="Spot">Multi-asset spot, asset d at pillar t in column <c>t * D + d</c></typeparam>
    /// <typeparam name="Schedule">Time schedule of the pricing</typeparam>
    /// <typeparam name="Premium">Output premium</typeparam>
    [HybridRegisterTemplate(Specialize = typeof(RainbowOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>))]
    [HybridRegisterTemplate(Specialize = typeof(RainbowOption<CurveDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>))]
    public class RainbowOption<Discounting, Spot, Schedule, Premium> : ISimulatable
        where Discounting : IDiscounting
        where Spot : ISpot
        where Schedule : ISchedule
        where Premium : IPremium
    {
        Spot _underlying;
        Discounting _discounting;
        Schedule _schedule;
        Premium _premium;

        int _assetCount;
        double[] _reference;
        int _maturity;
        double _strike;
        int _sign;
        bool _best;

        /// <summary>Creates a best-of or worst-of option.</summary>
        /// <param name="underlying">Multi-asset spot</param>
        /// <param name="discounting">Discounting curve</param>
        /// <param name="schedule">Pricing schedule</param>
        /// <param name="premium">Output premium</param>
        /// <param name="assetCount">Number of assets D</param>
        /// <param name="reference">Reference levels (D entries, positive)</param>
        /// <param name="maturity">Maturity (and payment) pillar index</param>
        /// <param name="strike">Strike, in performance units</param>
        /// <param name="sign">+1 for a call, -1 for a put</param>
        /// <param name="best">true for a best-of, false for a worst-of</param>
        public RainbowOption(Spot underlying, Discounting discounting, Schedule schedule, Premium premium,
            int assetCount, double[] reference, int maturity, double strike, int sign, bool best)
        {
            MultiAssetMath.CheckAssets(assetCount, reference, null);
            _underlying = underlying;
            _discounting = discounting;
            _schedule = schedule;
            _premium = premium;
            _assetCount = assetCount;
            _reference = (double[])reference.Clone();
            _maturity = maturity;
            _strike = strike;
            _sign = sign;
            _best = best;
        }

        /// <summary>Monte Carlo simulation of the product: _premium receives the discounted payoff of each path.</summary>
        [Kernel]
        public void Simulate(int simFrom, int simTo)
        {
            int column = _maturity * _assetCount;
            for (alignedindex simId = VectorUnit.ID + simFrom; simId < simTo; simId += VectorUnit.Count)
            {
                double e = _underlying[simId, column] / _reference[0];
                for (int d = 1; d < _assetCount; ++d)
                    e = MultiAssetMath.Extreme(_best, e, _underlying[simId, column + d] / _reference[d]);
                _premium[simId] = MultiAssetMath.CallPut(_sign, e, _strike) * _discounting[simId, _maturity];
            }
        }
    }

    /// <summary>
    /// Worst-of down-and-in put on D assets: <c>max(K - W(T), 0)</c> paid at the maturity pillar if the worst performance
    /// <c>W(t) = min_d S_d(t) / reference_d</c> was on or below the barrier at one of the monitoring pillars, nothing
    /// otherwise. With <c>monitor = { maturity }</c> the barrier is European (observed at maturity only, as in the capital
    /// protection of most autocallables); with every pillar it is a discretely monitored American barrier. This is the
    /// short put embedded in an autocallable (<see cref="Autocallable{Discounting, Spot, Schedule, Premium}"/>).
    /// </summary>
    /// <typeparam name="Discounting">Discounting curve</typeparam>
    /// <typeparam name="Spot">Multi-asset spot, asset d at pillar t in column <c>t * D + d</c></typeparam>
    /// <typeparam name="Schedule">Time schedule of the pricing</typeparam>
    /// <typeparam name="Premium">Output premium</typeparam>
    [HybridRegisterTemplate(Specialize = typeof(WorstOfDownAndInPut<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>))]
    [HybridRegisterTemplate(Specialize = typeof(WorstOfDownAndInPut<CurveDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>))]
    public class WorstOfDownAndInPut<Discounting, Spot, Schedule, Premium> : ISimulatable
        where Discounting : IDiscounting
        where Spot : ISpot
        where Schedule : ISchedule
        where Premium : IPremium
    {
        Spot _underlying;
        Discounting _discounting;
        Schedule _schedule;
        Premium _premium;

        int _assetCount;
        double[] _reference;
        int[] _monitor;
        int _monitorCount;
        int _maturity;
        double _strike;
        double _barrier;

        /// <summary>Creates a worst-of down-and-in put.</summary>
        /// <param name="underlying">Multi-asset spot</param>
        /// <param name="discounting">Discounting curve</param>
        /// <param name="schedule">Pricing schedule</param>
        /// <param name="premium">Output premium</param>
        /// <param name="assetCount">Number of assets D</param>
        /// <param name="reference">Reference levels (D entries, positive)</param>
        /// <param name="monitor">Ascending pillar indices where the barrier is observed, at most <paramref name="maturity"/></param>
        /// <param name="maturity">Maturity (and payment) pillar index</param>
        /// <param name="strike">Put strike, in performance units</param>
        /// <param name="barrier">Knock-in barrier on the worst performance</param>
        public WorstOfDownAndInPut(Spot underlying, Discounting discounting, Schedule schedule, Premium premium,
            int assetCount, double[] reference, int[] monitor, int maturity, double strike, double barrier)
        {
            MultiAssetMath.CheckAssets(assetCount, reference, null);
            MultiAssetMath.CheckPillars(monitor, maturity, nameof(monitor));
            _underlying = underlying;
            _discounting = discounting;
            _schedule = schedule;
            _premium = premium;
            _assetCount = assetCount;
            _reference = (double[])reference.Clone();
            _monitor = (int[])monitor.Clone();
            _monitorCount = monitor.Length;
            _maturity = maturity;
            _strike = strike;
            _barrier = barrier;
        }

        /// <summary>Monte Carlo simulation of the product: _premium receives the discounted payoff of each path.</summary>
        [Kernel]
        public void Simulate(int simFrom, int simTo)
        {
            for (alignedindex simId = VectorUnit.ID + simFrom; simId < simTo; simId += VectorUnit.Count)
            {
                double low = double.PositiveInfinity;
                for (int j = 0; j < _monitorCount; ++j)
                {
                    int column = _monitor[j] * _assetCount;
                    for (int d = 0; d < _assetCount; ++d)
                        low = Math.Min(low, _underlying[simId, column + d] / _reference[d]);
                }
                int last = _maturity * _assetCount;
                double worst = _underlying[simId, last] / _reference[0];
                for (int d = 1; d < _assetCount; ++d)
                    worst = Math.Min(worst, _underlying[simId, last + d] / _reference[d]);
                _premium[simId] = MultiAssetMath.DownAndInPut(low <= _barrier, _strike, worst) * _discounting[simId, _maturity];
            }
        }
    }
}
