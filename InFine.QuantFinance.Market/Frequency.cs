using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// How often a periodic payment occurs. The numeric value of each member is the number of periods in a year
    /// (zero for <see cref="Once"/>), so it can be cast to <see cref="int"/> where the ICMA day-count formula needs it.
    /// </summary>
    public enum Frequency
    {
        /// <summary>A single period from effective to termination date (zero-coupon).</summary>
        Once = 0,

        /// <summary>Every 12 months.</summary>
        Annual = 1,

        /// <summary>Every 6 months.</summary>
        Semiannual = 2,

        /// <summary>Every 4 months.</summary>
        EveryFourthMonth = 3,

        /// <summary>Every 3 months.</summary>
        Quarterly = 4,

        /// <summary>Every 2 months.</summary>
        Bimonthly = 6,

        /// <summary>Every month.</summary>
        Monthly = 12,
    }

    /// <summary>Conversions between <see cref="Frequency"/> and <see cref="Period"/>.</summary>
    public static class FrequencyExtensions
    {
        /// <summary>Number of periods per year; zero for <see cref="Frequency.Once"/>.</summary>
        public static int PeriodsPerYear(this Frequency frequency)
        {
            Validate(frequency);
            return (int)frequency;
        }

        /// <summary>Length of one period in months (12, 6, 4, 3, 2 or 1).</summary>
        /// <exception cref="ArgumentException"><paramref name="frequency"/> is <see cref="Frequency.Once"/>, which has no period length.</exception>
        public static int Months(this Frequency frequency)
        {
            Validate(frequency);
            if (frequency == Frequency.Once)
                throw new ArgumentException("Frequency.Once has no regular period length.", nameof(frequency));
            return 12 / (int)frequency;
        }

        /// <summary>The period of the frequency, in months.</summary>
        /// <exception cref="ArgumentException"><paramref name="frequency"/> is <see cref="Frequency.Once"/>.</exception>
        public static Period ToPeriod(this Frequency frequency) => new(frequency.Months(), PeriodUnit.Months);

        private static void Validate(Frequency frequency)
        {
            if (!Enum.IsDefined(frequency))
                throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Unknown frequency.");
        }
    }
}
