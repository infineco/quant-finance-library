using System.Threading;
using ExcelDna.Integration;
using InFine.QuantFinance.Excel.Core;

namespace InFine.QuantFinance.Excel
{
    /// <summary>
    /// The risk worksheet functions. They are the long-running ones, so they run through <see cref="XlAsync"/>: a call
    /// on a large range (and every <c>QF.MCVAR</c>) is computed on the thread pool while the cell shows <c>#N/A</c>,
    /// and a small call is computed inline. They are not marked thread-safe: Excel-DNA's asynchronous machinery is built
    /// on RTD topics, which Excel only supports from the main calculation thread, and the heavy work is off that thread
    /// anyway.
    /// </summary>
    public static class RiskFunctions
    {
        private const string Category = "InFine QuantFinance";

        private static QuantFunctions Qf => QuantFunctions.Default;

        [ExcelFunction(Name = "QF.COV", Category = Category,
            Description = "Covariance matrix of the columns of a range of returns (sample, EWMA or Ledoit-Wolf shrinkage). Large ranges are computed in the background (#N/A while calculating).")]
        public static object Cov(
            [ExcelArgument(Name = "returns", Description = "T x N range of returns, one row per date (oldest first), one column per asset.")] object returns,
            [ExcelArgument(Name = "method", Description = "SAMPLE (default), EWMA, LW_CC (Ledoit-Wolf, constant correlation) or LW_ID (Ledoit-Wolf, identity).")] object method = null,
            [ExcelArgument(Name = "lambda", Description = "EWMA decay factor (default 0.94).")] object lambda = null,
            [ExcelArgument(Name = "annualisation", Description = "Periods per year to multiply by (252 for daily returns); omit for the per-period covariance.")] object annualisation = null,
            [ExcelArgument(Name = "header", Description = "TRUE if the first row of the range holds the asset names (default FALSE).")] object header = null)
        {
            object[] a = { Xl.In(returns), Xl.In(method), Xl.In(lambda), Xl.In(annualisation), Xl.In(header) };
            return XlAsync.Call("QF.COV", a, false, ct => Qf.Cov(a[0], a[1], a[2], a[3], a[4], ct));
        }

        [ExcelFunction(Name = "QF.COV.OBJ", Category = Category,
            Description = "Covariance matrix of a range of returns kept by the add-in; returns its handle, to pass to QF.MCVAR. Large ranges are computed in the background.")]
        public static object CovObj(
            [ExcelArgument(Name = "name", Description = "Name of the matrix; computing it again with other inputs replaces it and changes the handle.")] object name,
            [ExcelArgument(Name = "returns", Description = "T x N range of returns, one row per date (oldest first), one column per asset.")] object returns,
            [ExcelArgument(Name = "method", Description = "SAMPLE (default), EWMA, LW_CC or LW_ID.")] object method = null,
            [ExcelArgument(Name = "lambda", Description = "EWMA decay factor (default 0.94).")] object lambda = null,
            [ExcelArgument(Name = "annualisation", Description = "Periods per year to multiply by; omit for the per-period covariance.")] object annualisation = null,
            [ExcelArgument(Name = "header", Description = "TRUE if the first row of the range holds the asset names; they are then usable in the positions of QF.MCVAR.")] object header = null)
        {
            object[] a = { Xl.In(name), Xl.In(returns), Xl.In(method), Xl.In(lambda), Xl.In(annualisation), Xl.In(header) };
            return XlAsync.Call("QF.COV.OBJ", a, false, ct => Qf.CovObj(a[0], a[1], a[2], a[3], a[4], a[5], ct));
        }

        [ExcelFunction(Name = "QF.VAR", Category = Category,
            Description = "Value-at-Risk (a positive loss) of a P&L sample, or of a portfolio over a range of asset returns. Large ranges are computed in the background.")]
        public static object Var(
            [ExcelArgument(Name = "data", Description = "P&L sample (gain positive), or with positions a T x N range of asset returns.")] object data,
            [ExcelArgument(Name = "alpha", Description = "Confidence level, e.g. 0.99.")] object alpha,
            [ExcelArgument(Name = "method", Description = "HISTORICAL (default), PARAMETRIC (normal) or CORNISHFISHER.")] object method = null,
            [ExcelArgument(Name = "horizon", Description = "Horizon in periods (default 1); square-root-of-time scaling.")] object horizon = null,
            [ExcelArgument(Name = "positions", Description = "Currency exposure per asset (N values): makes data a range of returns.")] object positions = null)
        {
            object[] a = { Xl.In(data), Xl.In(alpha), Xl.In(method), Xl.In(horizon), Xl.In(positions) };
            return XlAsync.Call("QF.VAR", a, false, ct => Qf.Var(a[0], a[1], a[2], a[3], a[4], ct));
        }

        [ExcelFunction(Name = "QF.ES", Category = Category,
            Description = "Expected Shortfall (a positive loss) of a P&L sample, or of a portfolio over a range of asset returns. Large ranges are computed in the background.")]
        public static object Es(
            [ExcelArgument(Name = "data", Description = "P&L sample (gain positive), or with positions a T x N range of asset returns.")] object data,
            [ExcelArgument(Name = "alpha", Description = "Confidence level, e.g. 0.975.")] object alpha,
            [ExcelArgument(Name = "method", Description = "HISTORICAL (default), PARAMETRIC (normal) or CORNISHFISHER.")] object method = null,
            [ExcelArgument(Name = "horizon", Description = "Horizon in periods (default 1); square-root-of-time scaling.")] object horizon = null,
            [ExcelArgument(Name = "positions", Description = "Currency exposure per asset (N values): makes data a range of returns.")] object positions = null)
        {
            object[] a = { Xl.In(data), Xl.In(alpha), Xl.In(method), Xl.In(horizon), Xl.In(positions) };
            return XlAsync.Call("QF.ES", a, false, ct => Qf.Es(a[0], a[1], a[2], a[3], a[4], ct));
        }

        [ExcelFunction(Name = "QF.VAR.DECOMP", Category = Category,
            Description = "Decomposition of VaR and ES by asset: marginal, component and percentage contributions, with a Total row. Large ranges are computed in the background.")]
        public static object VarDecomp(
            [ExcelArgument(Name = "returns", Description = "T x N range of asset returns.")] object returns,
            [ExcelArgument(Name = "positions", Description = "Currency exposure per asset (N values).")] object positions,
            [ExcelArgument(Name = "alpha", Description = "Confidence level, e.g. 0.99.")] object alpha,
            [ExcelArgument(Name = "method", Description = "HISTORICAL (default) or PARAMETRIC.")] object method = null,
            [ExcelArgument(Name = "horizon", Description = "Horizon in periods (default 1).")] object horizon = null,
            [ExcelArgument(Name = "header", Description = "TRUE if the first row of returns holds the asset names.")] object header = null)
        {
            object[] a = { Xl.In(returns), Xl.In(positions), Xl.In(alpha), Xl.In(method), Xl.In(horizon), Xl.In(header) };
            return XlAsync.Call("QF.VAR.DECOMP", a, false, ct => Qf.VarDecomp(a[0], a[1], a[2], a[3], a[4], a[5], ct));
        }

        [ExcelFunction(Name = "QF.MCVAR", Category = Category,
            Description = "Monte Carlo VaR and ES of a portfolio of shares and European options by full revaluation. Always computed in the background (#N/A while calculating); deterministic for a given seed.")]
        public static object McVar(
            [ExcelArgument(Name = "cov", Description = "QF.COV.OBJ handle, or an N x N range: covariance of the log returns of the N assets.")] object cov,
            [ExcelArgument(Name = "spots", Description = "N current spot prices.")] object spots,
            [ExcelArgument(Name = "vols", Description = "N implied volatilities (0.2 = 20%), held fixed.")] object vols,
            [ExcelArgument(Name = "positions", Description = "Table: asset (name or number), type (SPOT, CALL, PUT), quantity, strike, maturity in years, [dividend yield]. A row of titles on top is skipped.")] object positions,
            [ExcelArgument(Name = "alpha", Description = "Confidence level, e.g. 0.99.")] object alpha,
            [ExcelArgument(Name = "scenarios", Description = "Number of scenarios, e.g. 100000.")] object scenarios,
            [ExcelArgument(Name = "seed", Description = "Seed of the scenarios (default 1): same seed, same result.")] object seed = null,
            [ExcelArgument(Name = "generator", Description = "PHILOX (default) or SOBOL (scrambled quasi-random).")] object generator = null,
            [ExcelArgument(Name = "rate", Description = "Continuously compounded risk-free rate for the options (default 0), held fixed.")] object rate = null,
            [ExcelArgument(Name = "horizon", Description = "Horizon in periods of the covariance (default 1).")] object horizon = null)
        {
            object[] a = { Xl.In(cov), Xl.In(spots), Xl.In(vols), Xl.In(positions), Xl.In(alpha), Xl.In(scenarios),
                           Xl.In(seed), Xl.In(generator), Xl.In(rate), Xl.In(horizon) };
            return XlAsync.Call("QF.MCVAR", a, true, ct => Qf.McVar(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], ct));
        }
    }
}
