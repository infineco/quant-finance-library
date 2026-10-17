using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// An instrument quoted by a rate, usable as a bootstrap input: the curve-implied value of the quote is a function
    /// of discount factors up to <see cref="MaturityDate"/> only.
    /// </summary>
    public interface ICurveInstrument
    {
        /// <summary>The last date whose discount factor the implied rate depends on: the pillar the bootstrapper solves at.</summary>
        DateOnly MaturityDate { get; }

        /// <summary>The quote implied by <paramref name="curve"/> (deposit rate, FRA rate, par swap rate...).</summary>
        double ImpliedRate(IYieldCurve curve);
    }
}
