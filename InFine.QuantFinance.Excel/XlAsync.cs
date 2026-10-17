using System;
using System.Threading;
using System.Threading.Tasks;
using ExcelDna.Integration;
using InFine.QuantFinance.Excel.Core;

namespace InFine.QuantFinance.Excel
{
    /// <summary>
    /// Runs a long Core computation off Excel's main thread. <see cref="ExcelAsyncUtil.RunTaskWithCancellation{TResult}"/>
    /// starts the work on first call and returns <c>#N/A</c> while it runs; when it ends Excel-DNA posts the result
    /// back and recalculates the cell. A call is identified by the function name and a digest of the arguments
    /// (<see cref="RiskCalls.Key"/>), so recalculating the cell with the same arguments joins the call in flight instead of
    /// starting another, and changing the arguments (or deleting the cell) disposes the subscription, which cancels the
    /// token handed to the work.
    /// </summary>
    internal static class XlAsync
    {
        /// <summary>Calls with fewer cells than this run inline: the round trip through #N/A would cost more than the work.</summary>
        public const long InlineCellLimit = 20_000;

        public static object Call(string function, object[] arguments, bool alwaysAsync, Func<CancellationToken, object> work)
        {
            if (!alwaysAsync && RiskCalls.CellCount(arguments) < InlineCellLimit)
                return Xl.Out(work(CancellationToken.None));

            // Work on the thread pool; Task.Run observes the token before starting, and the Core observes it while running.
            return ExcelAsyncUtil.RunTaskWithCancellation(function, RiskCalls.Key(function, arguments),
                (CancellationToken token) => Task.Run(() => Xl.Out(work(token)), token));
        }
    }
}
