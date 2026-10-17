using System;
using System.Globalization;
using InFine.QuantFinance.Market;

namespace InFine.QuantFinance.Excel.Core
{
    public sealed partial class QuantFunctions
    {
        /// <summary>QF.DATE.ADJUST(date, calendar, [convention]) - rolls a date to a business day (default Following).</summary>
        public object DateAdjust(object? date, object? calendar, object? convention = null) =>
            Run("QF.DATE.ADJUST", () =>
            {
                var d = Arg.Date(date, "date");
                var cal = Names.ToCalendar(calendar, "calendar");
                var bdc = Names.ToConvention(convention, "convention", BusinessDayConvention.Following);
                return ExcelDates.ToSerial(cal.Adjust(d, bdc));
            });

        /// <summary>
        /// QF.DATE.ADVANCE(date, period, calendar, [convention], [endOfMonth]) - a plain integer or "5B" moves that many
        /// business days; a tenor such as "10D", "2W", "3M", "1Y" is added to the date and the result adjusted
        /// (default Modified Following).
        /// </summary>
        public object DateAdvance(object? date, object? period, object? calendar, object? convention = null, object? endOfMonth = null) =>
            Run("QF.DATE.ADVANCE", () =>
            {
                var d = Arg.Date(date, "date");
                var cal = Names.ToCalendar(calendar, "calendar");
                var bdc = Names.ToConvention(convention, "convention", BusinessDayConvention.ModifiedFollowing);
                bool eom = Arg.Bool(endOfMonth, "endOfMonth", false);

                if (period is string text)
                {
                    string trimmed = text.Trim();
                    if (trimmed.Length > 1 && trimmed[^1] is 'B' or 'b'
                        && int.TryParse(trimmed.AsSpan(0, trimmed.Length - 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int businessDays))
                        return ExcelDates.ToSerial(cal.Advance(d, businessDays));
                    if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    {
                        if (!Period.TryParse(trimmed, out var tenor))
                            throw new XlException(XlError.Value,
                                $"period: '{text}' is neither a business-day count (5, -3, 5B) nor a tenor (10D, 2W, 3M, 1Y)");
                        return ExcelDates.ToSerial(tenor.AddTo(d, cal, bdc, eom));
                    }
                }
                return ExcelDates.ToSerial(cal.Advance(d, Arg.Int(period, "period")));
            });

        /// <summary>QF.YEARFRAC(start, end, daycount) - year fraction under a named day-count convention.</summary>
        public object YearFrac(object? start, object? end, object? dayCount) =>
            Run("QF.YEARFRAC", () => Finite(Names.ToDayCounter(dayCount, "daycount")
                .YearFraction(Arg.Date(start, "start"), Arg.Date(end, "end"))));
    }
}
