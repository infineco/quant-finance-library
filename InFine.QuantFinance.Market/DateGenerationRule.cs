namespace InFine.QuantFinance.Market
{
    /// <summary>How the unadjusted dates of a schedule are generated between effective and termination date.</summary>
    public enum DateGenerationRule
    {
        /// <summary>
        /// From the termination date (or the next-to-last date) backward in whole tenors; a remainder at the front is the stub.
        /// The market standard for swaps and bonds.
        /// </summary>
        Backward = 0,

        /// <summary>From the effective date (or the first date) forward in whole tenors; a remainder at the back is the stub.</summary>
        Forward,

        /// <summary>No intermediate date: a single period from effective to termination date (zero-coupon).</summary>
        Zero,

        /// <summary>
        /// Forward generation where every intermediate date is moved to the third Wednesday of its month
        /// (IMM-style dates; month-based tenors only). Effective and termination dates are left as given.
        /// </summary>
        ThirdWednesday,
    }
}
