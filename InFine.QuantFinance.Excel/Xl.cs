using ExcelDna.Integration;
using InFine.QuantFinance.Excel.Core;

namespace InFine.QuantFinance.Excel
{
    /// <summary>
    /// Translation between Excel-DNA's argument/result values and the plain values of the Core: omitted and empty
    /// arguments become <c>null</c>, <see cref="ExcelError"/> becomes <see cref="XlError"/> and back, recursively
    /// inside ranges.
    /// </summary>
    internal static class Xl
    {
        public static object In(object value)
        {
            switch (value)
            {
                case null:
                case ExcelMissing:
                case ExcelEmpty:
                    return null;
                case ExcelError error:
                    return ToCore(error);
                case object[,] range:
                    var converted = new object[range.GetLength(0), range.GetLength(1)];
                    for (int i = 0; i < converted.GetLength(0); i++)
                        for (int j = 0; j < converted.GetLength(1); j++)
                            converted[i, j] = In(range[i + range.GetLowerBound(0), j + range.GetLowerBound(1)]);
                    return converted;
                default:
                    return value;
            }
        }

        public static object Out(object result)
        {
            switch (result)
            {
                case XlError error:
                    return ToExcel(error);
                case object[,] range:
                    var converted = new object[range.GetLength(0), range.GetLength(1)];
                    for (int i = 0; i < converted.GetLength(0); i++)
                        for (int j = 0; j < converted.GetLength(1); j++)
                            converted[i, j] = Out(range[i, j]);
                    return converted;
                default:
                    return result;
            }
        }

        public static XlError ToCore(ExcelError error) => error switch
        {
            ExcelError.ExcelErrorNull => XlError.Null,
            ExcelError.ExcelErrorDiv0 => XlError.DivZero,
            ExcelError.ExcelErrorValue => XlError.Value,
            ExcelError.ExcelErrorRef => XlError.Ref,
            ExcelError.ExcelErrorName => XlError.Name,
            ExcelError.ExcelErrorNum => XlError.Num,
            _ => XlError.NA,
        };

        public static ExcelError ToExcel(XlError error) => error switch
        {
            XlError.Null => ExcelError.ExcelErrorNull,
            XlError.DivZero => ExcelError.ExcelErrorDiv0,
            XlError.Value => ExcelError.ExcelErrorValue,
            XlError.Ref => ExcelError.ExcelErrorRef,
            XlError.Name => ExcelError.ExcelErrorName,
            XlError.Num => ExcelError.ExcelErrorNum,
            _ => ExcelError.ExcelErrorNA,
        };
    }
}
