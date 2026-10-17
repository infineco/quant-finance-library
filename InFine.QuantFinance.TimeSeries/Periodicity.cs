namespace InFine.QuantFinance.TimeSeries
{
    /// <summary>
    /// Common numbers of return periods per year, to pass as the <c>periodsPerYear</c> argument of the statistics. The
    /// annualisation factor is always an explicit argument: nothing in this library assumes 252.
    /// </summary>
    public static class Periodicity
    {
        /// <summary>Business-day returns: 252 per year (a convention; use 260 or 365 if your data say so).</summary>
        public const double Daily = 252.0;

        /// <summary>Weekly returns: 52 per year.</summary>
        public const double Weekly = 52.0;

        /// <summary>Monthly returns: 12 per year.</summary>
        public const double Monthly = 12.0;

        /// <summary>Quarterly returns: 4 per year.</summary>
        public const double Quarterly = 4.0;

        /// <summary>Semi-annual returns: 2 per year.</summary>
        public const double SemiAnnual = 2.0;

        /// <summary>Annual returns: 1 per year.</summary>
        public const double Annual = 1.0;
    }
}
