using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Payoffs.Concepts;
using InFine.QuantFinance.Scheduling;
using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Payoffs.Equity
{
    /// <summary>
    /// Hybridizer-free numerics of the variance swap, shared by the kernel and the unit tests.
    /// </summary>
    public static class VarianceSwapMath
    {
        /// <summary>Default annualisation factor: trading days per year.</summary>
        public const double DefaultAnnualisationFactor = 252.0;

        /// <summary>
        /// Squared log-return ln(next/previous)^2 of one schedule step.
        /// </summary>
        [Kernel]
        public static double SquaredLogReturn(double previous, double next)
        {
            double r = Math.Log(next / previous);
            return r * r;
        }

        /// <summary>
        /// Turns the sum of squared log-returns over <paramref name="stepCount"/> steps into the annualised
        /// realised variance (AF / n) * sum (zero-mean convention: no mean is subtracted), optionally capped.
        /// </summary>
        /// <param name="sumSquaredReturns">Sum of ln(S_{i+1}/S_i)^2</param>
        /// <param name="stepCount">Number of steps n (&gt; 0)</param>
        /// <param name="annualisationFactor">AF</param>
        /// <param name="strike">Variance strike K_var, used only to scale the cap</param>
        /// <param name="cap">Cap multiple of the strike volatility; &lt;= 0 means no cap</param>
        [Kernel]
        public static double RealisedVariance(double sumSquaredReturns, int stepCount, double annualisationFactor, double strike, double cap)
        {
            double variance = annualisationFactor * sumSquaredReturns / stepCount;
            if (cap > 0.0)
            {
                double maxVariance = cap * cap * strike;
                if (variance > maxVariance)
                    variance = maxVariance;
            }
            return variance;
        }

        /// <summary>
        /// Realised variance of a spot path S_0..S_n (first <paramref name="stepCount"/>+1 entries).
        /// </summary>
        public static double RealisedVariance(double[] path, int stepCount, double annualisationFactor, double strike, double cap)
        {
            double sum = 0.0;
            for (int i = 0; i < stepCount; ++i)
                sum += SquaredLogReturn(path[i], path[i + 1]);
            return RealisedVariance(sum, stepCount, annualisationFactor, strike, cap);
        }

        /// <summary>Undiscounted payoff per unit of variance notional.</summary>
        [Kernel]
        public static double Payoff(double realisedVariance, double strike)
        {
            return realisedVariance - strike;
        }
    }

    /// <summary>
    /// Variance swap on an equity underlying. At maturity the product pays, per unit of variance notional,
    /// the realised variance minus the variance strike.
    ///
    /// Realised variance over the schedule pillars 0..maturity (n = maturity steps):
    /// $$ \sigma^2_{real} = \frac{AF}{n} \sum_{i=0}^{n-1} \ln\left(\frac{S_{i+1}}{S_i}\right)^2 $$
    /// "Zero-mean" convention: the mean of the log-returns is not subtracted (market standard for variance swaps).
    /// The annualisation factor AF is 252 by default; the schedule is expected to be daily, so n/AF is the
    /// year fraction. The simulated paths of the underlying are expected to be strictly positive.
    ///
    /// Payoff per path = (min(sigma^2_real, cap^2 * strike) - strike) * discount(maturity).
    /// </summary>
    /// <typeparam name="Discounting">Discounting curve to be applied</typeparam>
    /// <typeparam name="Spot">Spot curve of the underlying</typeparam>
    /// <typeparam name="Schedule">Time schedule of the pricing</typeparam>
    /// <typeparam name="Premium">Output premium</typeparam>
    [HybridRegisterTemplate(Specialize=typeof(VarianceSwap<ConstantRateDiscounting,LogBasedSpot<ArrayDoubleSpot>,PrecalculatedSchedule,ArrayPremium>))]
    public class VarianceSwap<Discounting, Spot, Schedule, Premium> : ISimulatable
        where Discounting : IDiscounting
        where Spot : ISpot
        where Schedule : ISchedule
        where Premium : IPremium
    {
        Spot _underlying;
        Discounting _discounting;
        Schedule _schedule;

        Premium _premium;

        int _maturity;
        double _strike;
        double _cap;
        double _annualisationFactor;

        /// <summary>
        /// Creates a new variance swap with the default annualisation factor of 252.
        /// </summary>
        /// <param name="underlying">Product underlying</param>
        /// <param name="discounting">Discounting curve</param>
        /// <param name="schedule">Pricing schedule - needs to be daily until maturity of the variance swap</param>
        /// <param name="premium">Output premium</param>
        /// <param name="maturity">Maturity as a schedule pillar index (number of daily steps)</param>
        /// <param name="strike">Variance strike K_var (squared volatility, e.g. 0.04 for a 20% vol strike)</param>
        /// <param name="cap">Cap on the realised variance, as a multiple of the strike volatility: realised
        /// variance is capped at cap^2 * strike (market convention: 2.5). A value &lt;= 0 means no cap.</param>
        public VarianceSwap(Spot underlying, Discounting discounting, Schedule schedule, Premium premium, int maturity, double strike, double cap)
            : this(underlying, discounting, schedule, premium, maturity, strike, cap, VarianceSwapMath.DefaultAnnualisationFactor)
        {
        }

        /// <summary>
        /// Creates a new variance swap.
        /// </summary>
        /// <param name="underlying">Product underlying</param>
        /// <param name="discounting">Discounting curve</param>
        /// <param name="schedule">Pricing schedule - needs to be daily until maturity of the variance swap</param>
        /// <param name="premium">Output premium</param>
        /// <param name="maturity">Maturity as a schedule pillar index (number of daily steps)</param>
        /// <param name="strike">Variance strike K_var (squared volatility)</param>
        /// <param name="cap">Cap multiple of the strike volatility (cap^2 * strike on variance); &lt;= 0 means no cap</param>
        /// <param name="annualisationFactor">Number of observations per year (252 for daily)</param>
        public VarianceSwap(Spot underlying, Discounting discounting, Schedule schedule, Premium premium, int maturity, double strike, double cap, double annualisationFactor)
        {
            _underlying = underlying;
            _discounting = discounting;
            _schedule = schedule;
            _premium = premium;
            _maturity = maturity;
            _strike = strike;
            _cap = cap;
            _annualisationFactor = annualisationFactor;
        }

        /// <summary>
        /// Monte Carlo simulation of the product: _premium receives the discounted payoff for each simulation path.
        /// </summary>
        /// <param name="simFrom">lower inclusive bound of simulation</param>
        /// <param name="simTo">upper exclusive bound of simulation</param>
        [Kernel]
        public void Simulate(int simFrom, int simTo)
        {
            for (alignedindex simId = VectorUnit.ID + simFrom; simId < simTo; simId += VectorUnit.Count)
            {
                double sum = 0.0;
                double previous = _underlying[simId, 0];
                for (int i = 1; i <= _maturity; ++i)
                {
                    double next = _underlying[simId, i];
                    sum += VarianceSwapMath.SquaredLogReturn(previous, next);
                    previous = next;
                }
                double realised = VarianceSwapMath.RealisedVariance(sum, _maturity, _annualisationFactor, _strike, _cap);
                _premium[simId] = VarianceSwapMath.Payoff(realised, _strike) * _discounting[simId, _maturity];
            }
        }
    }
}
