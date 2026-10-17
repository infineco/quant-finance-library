namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// What to do when the span between effective and termination date is not a whole number of tenors
    /// (and no explicit first / next-to-last date settles it).
    /// </summary>
    public enum StubPolicy
    {
        /// <summary>The irregular period is shorter than a tenor: an extra short period at the front (backward) or back (forward).</summary>
        Short = 0,

        /// <summary>The irregular period is merged with its neighbour, giving one period longer than a tenor.</summary>
        Long,
    }
}
