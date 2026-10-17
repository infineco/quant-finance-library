using System;
using System.Collections.Generic;
using System.Linq;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Market;

namespace InFine.QuantFinance.Excel.Core
{
    /// <summary>
    /// The names a spreadsheet user types (calendars, day counts, conventions...) and the library objects they denote.
    /// Matching is case-insensitive and ignores spaces, underscores, hyphens and parentheses, so
    /// "Act/365 (Fixed)", "ACT/365FIXED" and "act/365f" are the same day count. Numbers are not accepted for names.
    /// </summary>
    public static class Names
    {
        private sealed class Table<T>
        {
            private readonly string _what;
            private readonly Dictionary<string, T> _map = new(StringComparer.Ordinal);
            private readonly List<string> _canonical = new();

            public Table(string what) { _what = what; }

            public Table<T> Add(T value, string canonical, params string[] aliases)
            {
                _canonical.Add(canonical);
                _map[Normalize(canonical)] = value;
                foreach (var alias in aliases) _map[Normalize(alias)] = value;
                return this;
            }

            public bool TryGet(string text, out T value) => _map.TryGetValue(Normalize(text), out value!);

            public T Get(string text, string argument)
            {
                if (TryGet(text, out var value)) return value;
                throw new XlException(XlError.Value,
                    $"{argument}: unknown {_what} '{text}'. Expected one of: {string.Join(", ", _canonical)}");
            }
        }

        private static readonly Table<OptionType> OptionTypes = new Table<OptionType>("option type")
            .Add(OptionType.Call, "CALL", "C")
            .Add(OptionType.Put, "PUT", "P");

        private static readonly Table<IDayCounter> DayCounters = new Table<IDayCounter>("day count")
            .Add(DayCounter.Actual360, "ACT/360", "ACTUAL/360", "A/360")
            .Add(DayCounter.Actual365Fixed, "ACT/365F", "ACT/365", "ACT/365FIXED", "ACTUAL/365", "ACTUAL/365FIXED", "A/365")
            .Add(DayCounter.Actual36525, "ACT/365.25", "ACTUAL/365.25")
            .Add(DayCounter.Thirty360BondBasis, "30/360", "30/360US", "30U/360", "30/360BOND", "30/360BONDBASIS", "BONDBASIS")
            .Add(DayCounter.Thirty360E, "30E/360", "30/360E", "EUROBOND")
            .Add(DayCounter.Thirty360EIsda, "30E/360ISDA", "30/360ISDA")
            .Add(DayCounter.ActualActualIsda, "ACT/ACT", "ACT/ACTISDA", "ACTUAL/ACTUAL", "ACTUAL/ACTUALISDA")
            .Add(DayCounter.ActualActualAfb, "ACT/ACTAFB", "ACTUAL/ACTUALAFB", "ACT/ACTFRENCH");

        private static readonly Table<BusinessDayConvention> Conventions = new Table<BusinessDayConvention>("business-day convention")
            .Add(BusinessDayConvention.Following, "FOLLOWING", "F")
            .Add(BusinessDayConvention.ModifiedFollowing, "MODIFIEDFOLLOWING", "MF", "MODFOLLOWING")
            .Add(BusinessDayConvention.Preceding, "PRECEDING", "P")
            .Add(BusinessDayConvention.ModifiedPreceding, "MODIFIEDPRECEDING", "MP", "MODPRECEDING")
            .Add(BusinessDayConvention.Unadjusted, "UNADJUSTED", "NONE", "U");

        private static readonly Table<Compounding> Compoundings = new Table<Compounding>("compounding")
            .Add(Compounding.Continuous, "CONTINUOUS", "CONT", "C")
            .Add(Compounding.Annual, "ANNUAL", "ANN", "A")
            .Add(Compounding.Simple, "SIMPLE", "S");

        private static readonly Table<CurveInterpolation> Interpolations = new Table<CurveInterpolation>("interpolation")
            .Add(CurveInterpolation.LogLinearDiscount, "LOGLINEARDISCOUNT", "LOGLINEAR", "LOGLINEARDF", "LOGDF")
            .Add(CurveInterpolation.LinearZero, "LINEARZERO", "LINEAR", "ZERO")
            .Add(CurveInterpolation.MonotoneCubicZero, "MONOTONECUBICZERO", "MONOTONECUBIC", "CUBIC");

        private static readonly Table<IHolidayCalendar> Calendars = new Table<IHolidayCalendar>("calendar")
            .Add(TargetCalendar.Instance, "TARGET")
            .Add(NyseCalendar.Instance, "NYSE")
            .Add(LondonCalendar.Instance, "LONDON")
            .Add(WeekendsOnlyCalendar.Instance, "WEEKENDS", "WEEKEND", "WEEKENDSONLY");

        /// <summary>A call/put flag: "C", "Call", "P", "Put", or the numbers 1 and -1.</summary>
        public static OptionType ToOptionType(object? value, string name)
        {
            if (value is object[,] range && range.Length == 1) value = range[range.GetLowerBound(0), range.GetLowerBound(1)];
            if (value is string text && !double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
                return OptionTypes.Get(text.Trim(), name);
            double sign = Arg.Double(value, name);
            if (sign == 1.0) return OptionType.Call;
            if (sign == -1.0) return OptionType.Put;
            throw new XlException(XlError.Value, $"{name}: {sign:R} is not an option type (use C/Call/1 or P/Put/-1)");
        }

        public static IDayCounter ToDayCounter(object? value, string name)
        {
            string text = Arg.Text(value, name);
            if (Normalize(text) is "ACT/ACTICMA" or "ACTUAL/ACTUALICMA")
                throw new XlException(XlError.Value,
                    $"{name}: Act/Act ICMA needs a reference period and frequency, which a day-count name cannot carry");
            return DayCounters.Get(text, name);
        }

        public static BusinessDayConvention ToConvention(object? value, string name, BusinessDayConvention defaultValue) =>
            Arg.IsMissing(value) ? defaultValue : Conventions.Get(Arg.Text(value, name), name);

        public static Compounding ToCompounding(object? value, string name, Compounding defaultValue) =>
            Arg.IsMissing(value) ? defaultValue : Compoundings.Get(Arg.Text(value, name), name);

        public static CurveInterpolation ToInterpolation(object? value, string name, CurveInterpolation defaultValue) =>
            Arg.IsMissing(value) ? defaultValue : Interpolations.Get(Arg.Text(value, name), name);

        /// <summary>A calendar name, or several joined with '+' (a day is a business day when it is one in all of them).</summary>
        public static IHolidayCalendar ToCalendar(object? value, string name)
        {
            string text = Arg.Text(value, name);
            var parts = text.Split(new[] { '+', ',', '&' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) throw new XlException(XlError.Value, $"{name}: empty calendar name");
            if (parts.Length == 1) return Calendars.Get(parts[0], name);
            return new JointCalendar(parts.Select(p => Calendars.Get(p, name)));
        }

        public static IHolidayCalendar ToCalendar(object? value, string name, IHolidayCalendar defaultValue) =>
            Arg.IsMissing(value) ? defaultValue : ToCalendar(value, name);

        internal static string Normalize(string text)
        {
            var chars = text.Where(c => !char.IsWhiteSpace(c) && c is not ('_' or '-' or '(' or ')')).ToArray();
            return new string(chars).ToUpperInvariant();
        }
    }
}
