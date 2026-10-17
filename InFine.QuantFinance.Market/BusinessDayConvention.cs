namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// How a date that is not a business day is rolled onto one (ISDA/ISDA 2006 definitions).
    /// </summary>
    public enum BusinessDayConvention
    {
        /// <summary>The date is left as it is, even if it is not a business day.</summary>
        Unadjusted = 0,

        /// <summary>Roll forward to the next business day.</summary>
        Following,

        /// <summary>Roll forward to the next business day, unless that falls in the next month, in which case roll backward.</summary>
        ModifiedFollowing,

        /// <summary>Roll backward to the previous business day.</summary>
        Preceding,

        /// <summary>Roll backward to the previous business day, unless that falls in the previous month, in which case roll forward.</summary>
        ModifiedPreceding,
    }
}
