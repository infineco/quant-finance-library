using System;
using System.Collections.Generic;
using InFine.QuantFinance.Market;

namespace InFine.QuantFinance.Excel.Core
{
    public sealed partial class QuantFunctions
    {
        /// <summary>Object kind of the yield curves held in the cache.</summary>
        public const string CurveKind = "QF.Curve";

        /// <summary>
        /// QF.CURVE.FLAT(name, refDate, rate, dayCount, [compounding]) - a flat-forward curve; the rate is continuous
        /// (default) or annual compounding. Returns the curve handle.
        /// </summary>
        public object CurveFlat(object? name, object? refDate, object? rate, object? dayCount, object? compounding = null) =>
            Run("QF.CURVE.FLAT", () =>
            {
                string curveName = Arg.Text(name, "name");
                var reference = Arg.Date(refDate, "refDate");
                double r = Arg.Double(rate, "rate");
                var counter = Names.ToDayCounter(dayCount, "dayCount");
                var comp = Names.ToCompounding(compounding, "compounding", Compounding.Continuous);

                var curve = new FlatForward(reference, r, counter, comp);
                var details = new List<KeyValuePair<string, object>>
                {
                    new("Reference date", Iso(reference)),
                    new("Day count", counter.Name),
                    new("Rate", r),
                    new("Compounding", comp.ToString()),
                };
                return Cache.Put(CurveKind, curveName, curve,
                    Fingerprint("flat", reference.DayNumber, r, counter.Name, comp),
                    $"flat forward {r.ToString("P4", System.Globalization.CultureInfo.InvariantCulture)} {counter.Name}", details);
            });

        /// <summary>
        /// QF.CURVE.BOOTSTRAP(name, refDate, instrumentTypes, tenors, quotes, [interpolation], [calendar]) - bootstraps a
        /// single discount curve from deposits (DEPOSIT, tenor "3M"), FRAs (FRA, tenor "3x6") and EUR-convention vanilla
        /// swaps (SWAP, tenor "5Y"; annual 30/360 fixed against 6M Actual/360 floating). The trade date is refDate, the spot
        /// lag two business days on [calendar] (default TARGET), quotes are decimal rates (0.025 = 2.5%). The
        /// curve day count is Actual/365 Fixed. Returns the curve handle.
        /// </summary>
        public object CurveBootstrap(object? name, object? refDate, object? instrumentTypes, object? tenors, object? quotes,
            object? interpolation = null, object? calendar = null) =>
            Run("QF.CURVE.BOOTSTRAP", () =>
            {
                string curveName = Arg.Text(name, "name");
                var reference = Arg.Date(refDate, "refDate");
                string[] types = Arg.Texts(instrumentTypes, "instrumentTypes");
                string[] tenorTexts = Arg.Texts(tenors, "tenors");
                double[] rates = Arg.Doubles(quotes, "quotes");
                if (tenorTexts.Length != types.Length || rates.Length != types.Length)
                    throw new XlException(XlError.Value,
                        $"instrumentTypes, tenors and quotes must have the same length ({types.Length}, {tenorTexts.Length}, {rates.Length})");
                var interp = Names.ToInterpolation(interpolation, "interpolation", CurveInterpolation.LogLinearDiscount);
                var cal = Names.ToCalendar(calendar, "calendar", TargetCalendar.Instance);
                var conventions = SwapConventions.EurSixMonth with { Calendar = cal };

                var builder = new PiecewiseYieldCurveBuilder(reference, DayCounter.Actual365Fixed).WithInterpolation(interp);
                for (int i = 0; i < types.Length; i++)
                    builder.Add(Instrument(reference, types[i], tenorTexts[i], cal, conventions, i + 1), rates[i]);
                var result = builder.Bootstrap();

                var curve = result.Curve;
                var details = new List<KeyValuePair<string, object>>
                {
                    new("Reference date", Iso(reference)),
                    new("Day count", DayCounter.Actual365Fixed.Name),
                    new("Interpolation", interp.ToString()),
                    new("Calendar", cal.Name),
                    new("Instruments", (double)types.Length),
                    new("Last pillar", Iso(curve.PillarDates![^1])),
                    new("Passes", (double)result.Passes),
                    new("Max repricing error", result.MaxRepricingError),
                };
                return Cache.Put(CurveKind, curveName, curve,
                    Fingerprint("bootstrap", reference.DayNumber, types, tenorTexts, rates, interp.ToString(), cal.Name),
                    $"bootstrapped from {types.Length} instruments, {interp}", details);
            });

        /// <summary>QF.CURVE.DF(handle, date) - discount factor from the curve's reference date to a date.</summary>
        public object CurveDf(object? handle, object? date) =>
            Run("QF.CURVE.DF", () =>
            {
                var curve = Cache.Get<IYieldCurve>(Arg.Text(handle, "handle"));
                return Finite(curve.DiscountFactor(Arg.Date(date, "date")));
            });

        /// <summary>QF.CURVE.ZERO(handle, date, [compounding]) - zero rate to a date (default continuous).</summary>
        public object CurveZero(object? handle, object? date, object? compounding = null) =>
            Run("QF.CURVE.ZERO", () =>
            {
                var curve = Cache.Get<IYieldCurve>(Arg.Text(handle, "handle"));
                var comp = Names.ToCompounding(compounding, "compounding", Compounding.Continuous);
                return Finite(curve.ZeroRate(Arg.Date(date, "date"), comp));
            });

        private static ICurveInstrument Instrument(DateOnly reference, string type, string tenor, IHolidayCalendar calendar,
            SwapConventions conventions, int position)
        {
            string label = $"instrument {position} ({type} {tenor})";
            switch (Names.Normalize(type))
            {
                case "DEPOSIT" or "DEPO" or "DEP" or "CASH":
                    return Deposit.FromTenor(reference, ParseTenor(tenor, label), calendar);
                case "SWAP" or "IRS":
                    return VanillaSwap.FromTenor(reference, ParseTenor(tenor, label), conventions);
                case "FRA":
                {
                    var parts = tenor.ToLowerInvariant().Split('x');
                    if (parts.Length != 2 || !int.TryParse(parts[0], out int from) || !int.TryParse(parts[1], out int to) || from < 0 || to <= from)
                        throw new XlException(XlError.Value, $"{label}: a FRA tenor must look like 3x6 (months)");
                    return ForwardRateAgreement.FromTenors(reference, new Period(from, PeriodUnit.Months),
                        new Period(to - from, PeriodUnit.Months), calendar);
                }
                default:
                    throw new XlException(XlError.Value, $"{label}: unknown instrument type '{type}'. Expected one of: DEPOSIT, FRA, SWAP");
            }
        }

        private static Period ParseTenor(string tenor, string label) =>
            Period.TryParse(tenor, out var period)
                ? period
                : throw new XlException(XlError.Value, $"{label}: '{tenor}' is not a tenor (expected e.g. 3M, 1Y)");
    }
}
