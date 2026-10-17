namespace InFine.QuantFinance.Market
{
    /// <summary>The unit of a <see cref="Period"/>.</summary>
    public enum PeriodUnit
    {
        /// <summary>Calendar days ("D").</summary>
        Days = 0,

        /// <summary>Weeks of seven calendar days ("W").</summary>
        Weeks,

        /// <summary>Calendar months ("M").</summary>
        Months,

        /// <summary>Years of twelve calendar months ("Y").</summary>
        Years,
    }
}
