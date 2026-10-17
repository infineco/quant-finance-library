namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// How a bond yield compounds. With <c>e</c> the time to a cash flow in coupon periods (so <c>t = e / f</c> years,
    /// <c>f</c> the coupon frequency), the discount factor of a yield <c>y</c> is:
    /// </summary>
    public enum YieldCompounding
    {
        /// <summary>
        /// Street (ICMA / US Treasury) convention, compounding at the coupon frequency: <c>(1 + y / f)^(-e)</c>.
        /// </summary>
        Periodic = 0,

        /// <summary>Annual compounding: <c>(1 + y)^(-t)</c>.</summary>
        Annual,

        /// <summary>Continuous compounding: <c>exp(-y t)</c>.</summary>
        Continuous,
    }
}
