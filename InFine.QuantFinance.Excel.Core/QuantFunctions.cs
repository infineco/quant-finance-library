using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace InFine.QuantFinance.Excel.Core
{
    /// <summary>
    /// Implementations of the <c>QF.*</c> Excel functions. Every method takes Excel-like <c>object</c> arguments (see
    /// <see cref="Arg"/>) and returns a <c>double</c>, <c>string</c>, <c>bool</c>, an <c>object[,]</c> of those, or
    /// an <see cref="XlError"/>; no method throws except to signal a cancellation requested through a <see cref="System.Threading.CancellationToken"/> (<see cref="OperationCanceledException"/>). Dates are Excel serial numbers on input and output.
    /// </summary>
    public sealed partial class QuantFunctions
    {
        /// <summary>The instance backed by <see cref="ObjectCache.Default"/>, used by the Excel shim.</summary>
        public static QuantFunctions Default { get; } = new(ObjectCache.Default);

        public QuantFunctions(ObjectCache cache)
        {
            ArgumentNullException.ThrowIfNull(cache);
            Cache = cache;
        }

        public ObjectCache Cache { get; }

        /// <summary>QF.VERSION() - version of the add-in's calculation core.</summary>
        public object Version() => Run("QF.VERSION", () =>
        {
            var assembly = typeof(QuantFunctions).Assembly;
            string? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                              ?? assembly.GetName().Version?.ToString();
            int plus = version?.IndexOf('+') ?? -1;
            if (plus > 0) version = version![..plus];
            return (object)("InFine.QuantFinance " + version);
        });

        /// <summary>QF.LASTERROR() - the reason of the most recent failure of any QF function (empty if none).</summary>
        public object LastError() => ErrorLog.Last ?? "";

        private static object Run(string function, Func<object> body)
        {
            try
            {
                return body();
            }
            catch (XlException e)
            {
                ErrorLog.Record(e.Error, function, e.Message);
                return e.Error;
            }
            catch (OperationCanceledException)
            {
                // Cancellation is not a failure of the arguments: let the caller (the async shim) observe it.
                throw;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or ArithmeticException
                                          or FormatException or KeyNotFoundException)
            {
                // The library validates its inputs by exceptions: those are domain errors of the numbers given.
                ErrorLog.Record(XlError.Num, function, e.Message);
                return XlError.Num;
            }
            catch (Exception e)
            {
                ErrorLog.Record(XlError.Value, function, $"unexpected {e.GetType().Name}: {e.Message}");
                return XlError.Value;
            }
        }

        /// <summary>A finite number, or #NUM!: NaN and infinities are never a meaningful cell value.</summary>
        private static object Finite(double value) =>
            double.IsFinite(value)
                ? value
                : throw new XlException(XlError.Num, $"the result is not a finite number ({value.ToString(CultureInfo.InvariantCulture)})");

        private static string Fingerprint(params object[] parts) =>
            string.Join("|", parts.Select(p => p switch
            {
                double d => d.ToString("R", CultureInfo.InvariantCulture),
                IEnumerable<double> ds => string.Join(",", ds.Select(d => d.ToString("R", CultureInfo.InvariantCulture))),
                IEnumerable<string> ss => string.Join(",", ss),
                _ => Convert.ToString(p, CultureInfo.InvariantCulture) ?? "",
            }));

        private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
