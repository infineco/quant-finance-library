using ExcelDna.Integration;
using InFine.QuantFinance.Excel.Core;

namespace InFine.QuantFinance.Excel
{
    /// <summary>
    /// The worksheet functions. Each one only forwards to <see cref="QuantFunctions"/> through <see cref="Xl"/>; the
    /// function names are a stable public contract (do not rename them: workbooks refer to them).
    /// </summary>
    public static class QuantFinanceFunctions
    {
        private const string Category = "InFine QuantFinance";

        private static QuantFunctions Qf => QuantFunctions.Default;

        // ---- Black-Scholes-Merton, Black-76, implied volatility ---------------------------------------------------

        [ExcelFunction(Name = "QF.BS.PRICE", Category = Category, IsThreadSafe = true,
            Description = "Black-Scholes-Merton price of a European option with continuous dividend yield.")]
        public static object BsPrice(
            [ExcelArgument(Name = "type", Description = "\"C\"/\"Call\" or \"P\"/\"Put\" (also 1 / -1).")] object type,
            [ExcelArgument(Name = "S", Description = "Spot price.")] object spot,
            [ExcelArgument(Name = "K", Description = "Strike.")] object strike,
            [ExcelArgument(Name = "T", Description = "Time to expiry in years.")] object maturity,
            [ExcelArgument(Name = "r", Description = "Continuously compounded risk-free rate (0.03 = 3%).")] object rate,
            [ExcelArgument(Name = "q", Description = "Continuous dividend yield (0 if none).")] object dividendYield,
            [ExcelArgument(Name = "vol", Description = "Annualised volatility (0.2 = 20%).")] object vol) =>
            Xl.Out(Qf.BsPrice(Xl.In(type), Xl.In(spot), Xl.In(strike), Xl.In(maturity), Xl.In(rate), Xl.In(dividendYield), Xl.In(vol)));

        [ExcelFunction(Name = "QF.BS.GREEKS", Category = Category, IsThreadSafe = true,
            Description = "Black-Scholes-Merton greeks: one named greek, or a row of all ten (optionally with a header row).")]
        public static object BsGreeks(
            [ExcelArgument(Name = "type", Description = "\"C\"/\"Call\" or \"P\"/\"Put\" (also 1 / -1).")] object type,
            [ExcelArgument(Name = "S", Description = "Spot price.")] object spot,
            [ExcelArgument(Name = "K", Description = "Strike.")] object strike,
            [ExcelArgument(Name = "T", Description = "Time to expiry in years.")] object maturity,
            [ExcelArgument(Name = "r", Description = "Continuously compounded risk-free rate.")] object rate,
            [ExcelArgument(Name = "q", Description = "Continuous dividend yield.")] object dividendYield,
            [ExcelArgument(Name = "vol", Description = "Annualised volatility.")] object vol,
            [ExcelArgument(Name = "greek", Description = "Price, Delta, Gamma, Vega, Theta, Rho, DivRho, Vanna, Volga or DualDelta for one value; \"ALL\" (default) for a row of all ten; \"HEADER\" for the row under a header row. Raw derivatives: vega per unit of vol, theta per year.")] object greek = null) =>
            Xl.Out(Qf.BsGreeks(Xl.In(type), Xl.In(spot), Xl.In(strike), Xl.In(maturity), Xl.In(rate), Xl.In(dividendYield), Xl.In(vol), Xl.In(greek)));

        [ExcelFunction(Name = "QF.BLACK76.PRICE", Category = Category, IsThreadSafe = true,
            Description = "Black-76 price of a European option on a forward, discounted by DF.")]
        public static object Black76Price(
            [ExcelArgument(Name = "type", Description = "\"C\"/\"Call\" or \"P\"/\"Put\" (also 1 / -1).")] object type,
            [ExcelArgument(Name = "F", Description = "Forward price of the underlying for the option expiry.")] object forward,
            [ExcelArgument(Name = "K", Description = "Strike.")] object strike,
            [ExcelArgument(Name = "T", Description = "Time to expiry in years.")] object maturity,
            [ExcelArgument(Name = "DF", Description = "Discount factor to the payment date.")] object discountFactor,
            [ExcelArgument(Name = "vol", Description = "Black volatility.")] object vol) =>
            Xl.Out(Qf.Black76Price(Xl.In(type), Xl.In(forward), Xl.In(strike), Xl.In(maturity), Xl.In(discountFactor), Xl.In(vol)));

        [ExcelFunction(Name = "QF.IMPLIEDVOL", Category = Category, IsThreadSafe = true,
            Description = "Black-Scholes-Merton implied volatility of an option price.")]
        public static object ImpliedVol(
            [ExcelArgument(Name = "price", Description = "Option premium.")] object price,
            [ExcelArgument(Name = "type", Description = "\"C\"/\"Call\" or \"P\"/\"Put\" (also 1 / -1).")] object type,
            [ExcelArgument(Name = "S", Description = "Spot price.")] object spot,
            [ExcelArgument(Name = "K", Description = "Strike.")] object strike,
            [ExcelArgument(Name = "T", Description = "Time to expiry in years.")] object maturity,
            [ExcelArgument(Name = "r", Description = "Continuously compounded risk-free rate.")] object rate,
            [ExcelArgument(Name = "q", Description = "Continuous dividend yield.")] object dividendYield) =>
            Xl.Out(Qf.ImpliedVol(Xl.In(price), Xl.In(type), Xl.In(spot), Xl.In(strike), Xl.In(maturity), Xl.In(rate), Xl.In(dividendYield)));

        [ExcelFunction(Name = "QF.IMPLIEDVOL.BLACK76", Category = Category, IsThreadSafe = true,
            Description = "Black-76 implied volatility of an option price on a forward.")]
        public static object ImpliedVolBlack76(
            [ExcelArgument(Name = "price", Description = "Option premium (discounted).")] object price,
            [ExcelArgument(Name = "type", Description = "\"C\"/\"Call\" or \"P\"/\"Put\" (also 1 / -1).")] object type,
            [ExcelArgument(Name = "F", Description = "Forward price of the underlying for the option expiry.")] object forward,
            [ExcelArgument(Name = "K", Description = "Strike.")] object strike,
            [ExcelArgument(Name = "T", Description = "Time to expiry in years.")] object maturity,
            [ExcelArgument(Name = "DF", Description = "Discount factor to the payment date.")] object discountFactor) =>
            Xl.Out(Qf.ImpliedVolBlack76(Xl.In(price), Xl.In(type), Xl.In(forward), Xl.In(strike), Xl.In(maturity), Xl.In(discountFactor)));

        // ---- Dates --------------------------------------------------------------------------------------------------

        [ExcelFunction(Name = "QF.DATE.ADJUST", Category = Category, IsThreadSafe = true,
            Description = "Rolls a date to a business day of a holiday calendar. Returns a date serial: format the cell as a date.")]
        public static object DateAdjust(
            [ExcelArgument(Name = "date", Description = "Date (Excel serial).")] object date,
            [ExcelArgument(Name = "calendar", Description = "TARGET, NYSE, LONDON or WEEKENDS; join several with + (e.g. TARGET+NYSE).")] object calendar,
            [ExcelArgument(Name = "convention", Description = "Following (default), ModifiedFollowing (MF), Preceding, ModifiedPreceding (MP) or Unadjusted.")] object convention = null) =>
            Xl.Out(Qf.DateAdjust(Xl.In(date), Xl.In(calendar), Xl.In(convention)));

        [ExcelFunction(Name = "QF.DATE.ADVANCE", Category = Category, IsThreadSafe = true,
            Description = "Adds business days or a tenor to a date on a holiday calendar. Returns a date serial.")]
        public static object DateAdvance(
            [ExcelArgument(Name = "date", Description = "Start date (Excel serial).")] object date,
            [ExcelArgument(Name = "period", Description = "A number or \"5B\" for business days (negative goes back); or a tenor: \"10D\", \"2W\", \"3M\", \"1Y\" (calendar-based, then adjusted).")] object period,
            [ExcelArgument(Name = "calendar", Description = "TARGET, NYSE, LONDON or WEEKENDS; join several with +.")] object calendar,
            [ExcelArgument(Name = "convention", Description = "Adjustment of a tenor result: ModifiedFollowing (default), Following, Preceding, ModifiedPreceding or Unadjusted.")] object convention = null,
            [ExcelArgument(Name = "endOfMonth", Description = "TRUE to keep month-end dates at month end when adding months or years (default FALSE).")] object endOfMonth = null) =>
            Xl.Out(Qf.DateAdvance(Xl.In(date), Xl.In(period), Xl.In(calendar), Xl.In(convention), Xl.In(endOfMonth)));

        [ExcelFunction(Name = "QF.YEARFRAC", Category = Category, IsThreadSafe = true,
            Description = "Year fraction between two dates under a day-count convention.")]
        public static object YearFrac(
            [ExcelArgument(Name = "start", Description = "Start date (Excel serial).")] object start,
            [ExcelArgument(Name = "end", Description = "End date (Excel serial).")] object end,
            [ExcelArgument(Name = "daycount", Description = "ACT/360, ACT/365F, ACT/365.25, 30/360, 30E/360, 30E/360 ISDA, ACT/ACT (ISDA) or ACT/ACT AFB.")] object dayCount) =>
            Xl.Out(Qf.YearFrac(Xl.In(start), Xl.In(end), Xl.In(dayCount)));

        // ---- Yield curves -------------------------------------------------------------------------------------------

        [ExcelFunction(Name = "QF.CURVE.FLAT", Category = Category, IsThreadSafe = true,
            Description = "Creates a flat-forward discount curve and returns its handle (text such as QF.Curve:EUR#1).")]
        public static object CurveFlat(
            [ExcelArgument(Name = "name", Description = "Name of the curve; creating it again replaces it and changes the handle.")] object name,
            [ExcelArgument(Name = "refDate", Description = "Reference (valuation) date.")] object refDate,
            [ExcelArgument(Name = "rate", Description = "The flat rate (0.03 = 3%).")] object rate,
            [ExcelArgument(Name = "dayCount", Description = "Day count converting dates to curve time, e.g. ACT/365F.")] object dayCount,
            [ExcelArgument(Name = "compounding", Description = "Continuous (default) or Annual: how the rate is quoted.")] object compounding = null) =>
            Xl.Out(Qf.CurveFlat(Xl.In(name), Xl.In(refDate), Xl.In(rate), Xl.In(dayCount), Xl.In(compounding)));

        [ExcelFunction(Name = "QF.CURVE.BOOTSTRAP", Category = Category, IsThreadSafe = true,
            Description = "Bootstraps a discount curve from deposits, FRAs and swaps (EUR conventions) and returns its handle.")]
        public static object CurveBootstrap(
            [ExcelArgument(Name = "name", Description = "Name of the curve; creating it again replaces it and changes the handle.")] object name,
            [ExcelArgument(Name = "refDate", Description = "Reference (trade) date; spot is two business days later.")] object refDate,
            [ExcelArgument(Name = "instrumentTypes", Description = "Range of DEPOSIT, FRA or SWAP.")] object instrumentTypes,
            [ExcelArgument(Name = "tenors", Description = "Range of tenors: 3M, 1Y... or 3x6 for a FRA (months).")] object tenors,
            [ExcelArgument(Name = "quotes", Description = "Range of quoted rates as decimals (0.025 = 2.5%).")] object quotes,
            [ExcelArgument(Name = "interpolation", Description = "LogLinearDiscount (default), LinearZero or MonotoneCubicZero.")] object interpolation = null,
            [ExcelArgument(Name = "calendar", Description = "Calendar for the spot lag and adjustments (default TARGET).")] object calendar = null) =>
            Xl.Out(Qf.CurveBootstrap(Xl.In(name), Xl.In(refDate), Xl.In(instrumentTypes), Xl.In(tenors), Xl.In(quotes), Xl.In(interpolation), Xl.In(calendar)));

        [ExcelFunction(Name = "QF.CURVE.DF", Category = Category, IsThreadSafe = true,
            Description = "Discount factor from the curve's reference date to a date.")]
        public static object CurveDf(
            [ExcelArgument(Name = "handle", Description = "Curve handle returned by QF.CURVE.FLAT or QF.CURVE.BOOTSTRAP.")] object handle,
            [ExcelArgument(Name = "date", Description = "Date (Excel serial), on or after the reference date.")] object date) =>
            Xl.Out(Qf.CurveDf(Xl.In(handle), Xl.In(date)));

        [ExcelFunction(Name = "QF.CURVE.ZERO", Category = Category, IsThreadSafe = true,
            Description = "Zero rate from the curve's reference date to a date.")]
        public static object CurveZero(
            [ExcelArgument(Name = "handle", Description = "Curve handle.")] object handle,
            [ExcelArgument(Name = "date", Description = "Date (Excel serial), on or after the reference date.")] object date,
            [ExcelArgument(Name = "compounding", Description = "Continuous (default), Annual or Simple.")] object compounding = null) =>
            Xl.Out(Qf.CurveZero(Xl.In(handle), Xl.In(date), Xl.In(compounding)));

        // ---- Volatility ---------------------------------------------------------------------------------------------

        [ExcelFunction(Name = "QF.SVI.FIT", Category = Category, IsThreadSafe = true,
            Description = "Fits a raw-SVI smile to implied volatilities at one expiry and returns the surface handle.")]
        public static object SviFit(
            [ExcelArgument(Name = "name", Description = "Name of the surface; fitting again replaces it and changes the handle.")] object name,
            [ExcelArgument(Name = "T", Description = "Time to expiry in years.")] object maturity,
            [ExcelArgument(Name = "forward", Description = "Forward price for that expiry.")] object forward,
            [ExcelArgument(Name = "strikes", Description = "Range of strikes (at least five).")] object strikes,
            [ExcelArgument(Name = "vols", Description = "Range of Black implied volatilities, same length as strikes.")] object vols) =>
            Xl.Out(Qf.SviFit(Xl.In(name), Xl.In(maturity), Xl.In(forward), Xl.In(strikes), Xl.In(vols)));

        [ExcelFunction(Name = "QF.SURFACE.VOL", Category = Category, IsThreadSafe = true,
            Description = "Implied volatility read from a fitted surface.")]
        public static object SurfaceVol(
            [ExcelArgument(Name = "handle", Description = "Surface handle returned by QF.SVI.FIT.")] object handle,
            [ExcelArgument(Name = "T", Description = "Time to expiry in years.")] object maturity,
            [ExcelArgument(Name = "K", Description = "Strike.")] object strike) =>
            Xl.Out(Qf.SurfaceVol(Xl.In(handle), Xl.In(maturity), Xl.In(strike)));

        // ---- Object cache and diagnostics -----------------------------------------------------------------------------

        [ExcelFunction(Name = "QF.OBJ.LIST", Category = Category, IsThreadSafe = true, IsVolatile = true,
            Description = "Lists the objects (curves, surfaces) held by the add-in: a header row, then one row per object.")]
        public static object ObjList() => Xl.Out(Qf.ObjList());

        [ExcelFunction(Name = "QF.OBJ.INFO", Category = Category, IsThreadSafe = true,
            Description = "Describes an object: a two-column table of properties and values.")]
        public static object ObjInfo(
            [ExcelArgument(Name = "handle", Description = "Object handle.")] object handle) =>
            Xl.Out(Qf.ObjInfo(Xl.In(handle)));

        [ExcelFunction(Name = "QF.OBJ.DELETE", Category = Category, IsThreadSafe = true,
            Description = "Removes an object from the add-in. TRUE if it was removed, FALSE if the handle was unknown or stale.")]
        public static object ObjDelete(
            [ExcelArgument(Name = "handle", Description = "Object handle.")] object handle) =>
            Xl.Out(Qf.ObjDelete(Xl.In(handle)));

        [ExcelFunction(Name = "QF.VERSION", Category = Category, IsThreadSafe = true,
            Description = "Version of the InFine QuantFinance add-in.")]
        public static object Version() => Xl.Out(Qf.Version());

        [ExcelFunction(Name = "QF.LASTERROR", Category = Category, IsThreadSafe = true, IsVolatile = true,
            Description = "Reason for the most recent error returned by any QF function (Excel only shows #VALUE!, #NUM!, #REF!).")]
        public static object LastError() => Xl.Out(Qf.LastError());
    }
}
