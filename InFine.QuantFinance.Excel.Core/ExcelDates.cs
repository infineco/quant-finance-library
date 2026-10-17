using System;

namespace InFine.QuantFinance.Excel.Core
{
    /// <summary>
    /// Conversion between Excel's 1900 date system and <see cref="DateOnly"/>.
    /// </summary>
    /// <remarks>
    /// Excel inherits Lotus 1-2-3's bug: it treats 1900 as a leap year. Serial 60 is the non-existent 1900-02-29,
    /// serial 59 is 1900-02-28 and serial 61 is 1900-03-01, so serials 1..59 are offset by one day from the
    /// proleptic Gregorian calendar and serials from 61 are exactly <c>1899-12-30 + serial</c>. Serial 0
    /// ("1900-01-00") and serial 60 have no real date and are rejected; so are dates before 1900-01-01.
    /// The fractional part of a serial (time of day) is dropped.
    /// </remarks>
    public static class ExcelDates
    {
        /// <summary>The largest serial Excel accepts (9999-12-31).</summary>
        public const double MaxSerial = 2958465;

        private static readonly int Epoch = new DateOnly(1899, 12, 30).DayNumber;
        private static readonly int EarlyEpoch = new DateOnly(1899, 12, 31).DayNumber;
        private static readonly DateOnly FirstDate = new(1900, 1, 1);
        private static readonly DateOnly LastEarlyDate = new(1900, 2, 28);

        /// <summary>Converts a serial to a date; false (with a reason) when the serial denotes no real date.</summary>
        public static bool TryToDate(double serial, out DateOnly date, out string? reason)
        {
            date = default;
            if (!double.IsFinite(serial))
            {
                reason = "the date serial is not a finite number";
                return false;
            }
            double day = Math.Floor(serial);
            if (day < 1 || day > MaxSerial)
            {
                reason = $"the date serial {serial:R} is outside Excel's range 1..{MaxSerial:R}";
                return false;
            }
            if (day == 60)
            {
                reason = "serial 60 is Excel's non-existent 1900-02-29";
                return false;
            }
            int n = (int)day;
            date = DateOnly.FromDayNumber((n < 60 ? EarlyEpoch : Epoch) + n);
            reason = null;
            return true;
        }

        /// <summary>Converts a serial to a date, throwing an <see cref="XlException"/> (#VALUE!) when it denotes none.</summary>
        public static DateOnly ToDate(double serial) =>
            TryToDate(serial, out var date, out var reason) ? date : throw new XlException(XlError.Value, reason!);

        /// <summary>The Excel serial of a date. Dates before 1900-01-01 cannot be represented.</summary>
        public static double ToSerial(DateOnly date)
        {
            if (date < FirstDate)
                throw new XlException(XlError.Value, $"{date:yyyy-MM-dd} is before 1900-01-01, the first date Excel can represent");
            return date.DayNumber - (date <= LastEarlyDate ? EarlyEpoch : Epoch);
        }
    }
}
