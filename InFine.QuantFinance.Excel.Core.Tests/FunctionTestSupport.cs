using System;
using Xunit;

namespace InFine.QuantFinance.Excel.Core.Tests;

internal static class T
{
    public static double Serial(int year, int month, int day) => ExcelDates.ToSerial(new DateOnly(year, month, day));

    public static DateOnly Date(object serial) => ExcelDates.ToDate(Assert.IsType<double>(serial));

    public static double Num(object result)
    {
        if (result is XlError error) Assert.Fail($"expected a number but got {error}: {ErrorLog.Last}");
        return Assert.IsType<double>(result);
    }

    public static string Text(object result)
    {
        if (result is XlError error) Assert.Fail($"expected a string but got {error}: {ErrorLog.Last}");
        return Assert.IsType<string>(result);
    }

    public static object[,] Array(object result)
    {
        if (result is XlError error) Assert.Fail($"expected an array but got {error}: {ErrorLog.Last}");
        return Assert.IsType<object[,]>(result);
    }

    public static object[,] Column(params object?[] values)
    {
        var result = new object[values.Length, 1];
        for (int i = 0; i < values.Length; i++) result[i, 0] = values[i]!;
        return result;
    }

    public static object[,] Row(params object?[] values)
    {
        var result = new object[1, values.Length];
        for (int i = 0; i < values.Length; i++) result[0, i] = values[i]!;
        return result;
    }
}
