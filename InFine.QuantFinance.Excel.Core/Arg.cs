using System;
using System.Collections.Generic;
using System.Globalization;

namespace InFine.QuantFinance.Excel.Core
{
    /// <summary>
    /// Coercion of Excel-like argument values (<c>object</c>) to typed values.
    /// </summary>
    /// <remarks>
    /// An argument is one of: <c>null</c> (an empty cell or an omitted argument), <see cref="double"/> (any numeric
    /// type is accepted), <see cref="string"/>, <see cref="bool"/>, <see cref="XlError"/> (an error value, which is
    /// re-thrown as is so it propagates), <see cref="DateTime"/>/<see cref="DateOnly"/> (convenience for callers
    /// that are not Excel), or an <c>object[,]</c> range of those. Failures throw <see cref="XlException"/> with a
    /// message naming the argument.
    /// </remarks>
    public static class Arg
    {
        /// <summary>True for an empty cell, an omitted argument or an empty string.</summary>
        public static bool IsMissing(object? value) =>
            value is null or System.Reflection.Missing or ""
            || (value is object[,] range && range.Length == 1 && IsMissing(range[range.GetLowerBound(0), range.GetLowerBound(1)]));

        public static double Double(object? value, string name)
        {
            value = Scalar(value, name);
            switch (value)
            {
                case null:
                case System.Reflection.Missing:
                    throw Missing(name);
                case double d:
                    return d;
                case string s:
                    if (s.Length == 0) throw Missing(name);
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) return parsed;
                    throw new XlException(XlError.Value, $"{name}: '{s}' is not a number");
                case bool:
                    throw new XlException(XlError.Value, $"{name}: a boolean is not a number");
                case IConvertible convertible when IsNumeric(value):
                    return convertible.ToDouble(CultureInfo.InvariantCulture);
                default:
                    throw new XlException(XlError.Value, $"{name}: cannot read a {value.GetType().Name} as a number");
            }
        }

        public static double Double(object? value, string name, double defaultValue) =>
            IsMissing(value) ? defaultValue : Double(value, name);

        public static int Int(object? value, string name)
        {
            double d = Double(value, name);
            double rounded = Math.Round(d);
            if (Math.Abs(d - rounded) > 1e-9 || Math.Abs(rounded) > int.MaxValue)
                throw new XlException(XlError.Value, $"{name}: {d:R} is not an integer");
            return (int)rounded;
        }

        public static int Int(object? value, string name, int defaultValue) =>
            IsMissing(value) ? defaultValue : Int(value, name);

        public static bool Bool(object? value, string name, bool defaultValue)
        {
            if (IsMissing(value)) return defaultValue;
            value = Scalar(value, name);
            switch (value)
            {
                case bool b:
                    return b;
                case string s when bool.TryParse(s, out bool parsed):
                    return parsed;
                case string s:
                    throw new XlException(XlError.Value, $"{name}: '{s}' is not TRUE or FALSE");
                default:
                    double d = Double(value, name);
                    return d != 0.0;
            }
        }

        public static string Text(object? value, string name)
        {
            value = Scalar(value, name);
            switch (value)
            {
                case null:
                case System.Reflection.Missing:
                    throw Missing(name);
                case string s:
                    if (s.Length == 0) throw Missing(name);
                    return s.Trim();
                case bool b:
                    return b ? "TRUE" : "FALSE";
                case IConvertible convertible when IsNumeric(value):
                    return convertible.ToString(CultureInfo.InvariantCulture);
                default:
                    throw new XlException(XlError.Value, $"{name}: cannot read a {value.GetType().Name} as text");
            }
        }

        public static string? OptionalText(object? value, string name) =>
            IsMissing(value) ? null : Text(value, name);

        /// <summary>
        /// A date: an Excel serial number, an ISO <c>yyyy-MM-dd</c> string, or a <see cref="DateOnly"/>/<see cref="DateTime"/>.
        /// </summary>
        public static DateOnly Date(object? value, string name)
        {
            value = Scalar(value, name);
            switch (value)
            {
                case DateOnly date:
                    return date;
                case DateTime dateTime:
                    return DateOnly.FromDateTime(dateTime);
                case string s when !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _):
                    if (DateOnly.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
                        return iso;
                    throw new XlException(XlError.Value, $"{name}: '{s}' is neither a date serial nor an ISO yyyy-MM-dd date");
                default:
                    double serial = Double(value, name);
                    if (ExcelDates.TryToDate(serial, out var converted, out var reason)) return converted;
                    throw new XlException(XlError.Value, $"{name}: {reason}");
            }
        }

        /// <summary>
        /// A vector of numbers from a scalar, a row, a column or a 2-D range (read in row-major order). Empty cells
        /// are an error: silently dropping one would misalign it with the other ranges of the same call.
        /// </summary>
        public static double[] Doubles(object? value, string name)
        {
            var items = Flatten(value, name);
            var result = new double[items.Count];
            for (int i = 0; i < result.Length; i++)
                result[i] = Double(items[i], items.Count == 1 ? name : $"{name}[{i + 1}]");
            return result;
        }

        /// <summary>A vector of strings; see <see cref="Doubles"/>.</summary>
        public static string[] Texts(object? value, string name)
        {
            var items = Flatten(value, name);
            var result = new string[items.Count];
            for (int i = 0; i < result.Length; i++)
                result[i] = Text(items[i], items.Count == 1 ? name : $"{name}[{i + 1}]");
            return result;
        }

        /// <summary>Re-throws an error value held by <paramref name="value"/> so that it propagates to the result.</summary>
        public static void ThrowIfError(object? value, string name)
        {
            if (value is XlError error)
                throw new XlException(error, $"{name}: the argument is itself an error value ({ErrorLog.Describe(error)})");
        }

        private static List<object?> Flatten(object? value, string name)
        {
            ThrowIfError(value, name);
            var items = new List<object?>();
            if (value is object[,] range)
            {
                foreach (var item in range) items.Add(item);
            }
            else if (value is object?[] vector)
            {
                items.AddRange(vector);
            }
            else
            {
                items.Add(value);
            }
            if (items.Count == 0 || (items.Count == 1 && IsMissing(items[0])))
                throw Missing(name);
            return items;
        }

        // A scalar argument may arrive as a 1x1 range; larger ranges are a user mistake worth naming.
        private static object? Scalar(object? value, string name)
        {
            ThrowIfError(value, name);
            if (value is object[,] range)
            {
                if (range.Length != 1)
                    throw new XlException(XlError.Value,
                        $"{name}: expected a single value but got a {range.GetLength(0)}x{range.GetLength(1)} range");
                value = range[range.GetLowerBound(0), range.GetLowerBound(1)];
                ThrowIfError(value, name);
            }
            return value;
        }

        private static bool IsNumeric(object value) => Type.GetTypeCode(value.GetType()) switch
        {
            TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32
                or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal => true,
            _ => false,
        };

        private static XlException Missing(string name) =>
            new(XlError.Value, $"{name}: a value is required but the cell is empty or the argument was omitted");
    }
}
