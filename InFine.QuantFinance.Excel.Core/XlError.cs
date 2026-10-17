using System;
using System.Threading;

namespace InFine.QuantFinance.Excel.Core
{
    /// <summary>
    /// An Excel error value, as a plain enum so the core stays free of any Excel-DNA reference. The shim maps it
    /// to <c>ExcelError</c>; the same sentinel is accepted on input so that an error in a precedent cell
    /// propagates through a function the way Excel users expect.
    /// </summary>
    public enum XlError
    {
        /// <summary>#NULL!</summary>
        Null,

        /// <summary>#DIV/0!</summary>
        DivZero,

        /// <summary>#VALUE! - wrong type of argument (or an unknown name, or a malformed value).</summary>
        Value,

        /// <summary>#REF! - an object handle that is unknown, deleted or stale.</summary>
        Ref,

        /// <summary>#NAME?</summary>
        Name,

        /// <summary>#NUM! - the arguments are of the right type but outside the numerical domain.</summary>
        Num,

        /// <summary>#N/A</summary>
        NA,
    }

    /// <summary>
    /// A failure with a known Excel error code and a human-readable message. Thrown by the coercion layer and
    /// the object cache; <see cref="QuantFunctions"/> turns it into an <see cref="XlError"/> result and records
    /// the message for <c>QF.LASTERROR()</c>.
    /// </summary>
    public sealed class XlException : Exception
    {
        public XlException(XlError error, string message) : base(message)
        {
            Error = error;
        }

        public XlError Error { get; }
    }

    /// <summary>
    /// The message of the most recent function failure. An Excel cell can only show #VALUE!, so the reason is kept
    /// here and exposed through <c>QF.LASTERROR()</c>. It is process-wide and therefore advisory: with
    /// multi-threaded recalculation the last failure is not necessarily the one of the cell being inspected.
    /// </summary>
    public static class ErrorLog
    {
        private static string? _last;

        public static string? Last => Volatile.Read(ref _last);

        public static void Record(XlError error, string function, string message) =>
            Volatile.Write(ref _last, $"{function}: {message} ({Describe(error)})");

        public static void Clear() => Volatile.Write(ref _last, null);

        public static string Describe(XlError error) => error switch
        {
            XlError.Null => "#NULL!",
            XlError.DivZero => "#DIV/0!",
            XlError.Value => "#VALUE!",
            XlError.Ref => "#REF!",
            XlError.Name => "#NAME?",
            XlError.Num => "#NUM!",
            _ => "#N/A",
        };
    }
}
