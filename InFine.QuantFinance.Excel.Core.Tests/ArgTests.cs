using System;
using Xunit;

namespace InFine.QuantFinance.Excel.Core.Tests;

public class ArgTests
{
    private static XlException Fails(Action action) => Assert.Throws<XlException>(action);

    [Fact]
    public void DoubleAcceptsNumbersAndNumericStrings()
    {
        Assert.Equal(1.5, Arg.Double(1.5, "x"));
        Assert.Equal(3.0, Arg.Double(3, "x"));
        Assert.Equal(2.0, Arg.Double(2L, "x"));
        Assert.Equal(0.25, Arg.Double(0.25f, "x"));
        Assert.Equal(7.0, Arg.Double(7m, "x"));
        Assert.Equal(0.05, Arg.Double("0.05", "x"));
        Assert.Equal(1e-4, Arg.Double(" 1e-4 ", "x"));
        // Invariant culture: a comma is not a decimal separator.
        Assert.Equal(XlError.Value, Fails(() => Arg.Double("0,05", "x")).Error);
    }

    [Fact]
    public void StringsAndNumbersAreNotInterchangeable()
    {
        Assert.Equal(XlError.Value, Fails(() => Arg.Double("abc", "x")).Error);
        Assert.Equal(XlError.Value, Fails(() => Arg.Double(true, "x")).Error);
        Assert.Equal("1.5", Arg.Text(1.5, "x"));
        Assert.Equal("TARGET", Arg.Text("  TARGET ", "x"));
    }

    [Fact]
    public void EmptyAndOmittedArgumentsAreErrorsUnlessOptional()
    {
        foreach (object? missing in new object?[] { null, "", System.Reflection.Missing.Value })
        {
            var e = Fails(() => Arg.Double(missing, "strike"));
            Assert.Equal(XlError.Value, e.Error);
            Assert.Contains("strike", e.Message);
            Assert.Equal(9.0, Arg.Double(missing, "x", 9.0));
            Assert.Null(Arg.OptionalText(missing, "x"));
            Assert.True(Arg.Bool(missing, "x", true));
            Assert.Equal(4, Arg.Int(missing, "x", 4));
        }
        Assert.Equal(0.0, Arg.Double(0.0, "x", 9.0));
    }

    [Fact]
    public void ErrorValuesPropagateWithTheirOwnCode()
    {
        foreach (var error in Enum.GetValues<XlError>())
        {
            Assert.Equal(error, Fails(() => Arg.Double(error, "x")).Error);
            Assert.Equal(error, Fails(() => Arg.Text(error, "x")).Error);
            Assert.Equal(error, Fails(() => Arg.Date(error, "x")).Error);
            Assert.Equal(error, Fails(() => Arg.Doubles(new object?[,] { { 1.0, error } }, "x")).Error);
        }
    }

    [Fact]
    public void IntegersMustBeIntegral()
    {
        Assert.Equal(5, Arg.Int(5.0, "n"));
        Assert.Equal(-3, Arg.Int("-3", "n"));
        Assert.Equal(5, Arg.Int(5.0000000001, "n"));
        Assert.Equal(XlError.Value, Fails(() => Arg.Int(5.5, "n")).Error);
        Assert.Equal(XlError.Value, Fails(() => Arg.Int(1e12, "n")).Error);
    }

    [Fact]
    public void BooleansComeFromBoolsNumbersAndStrings()
    {
        Assert.True(Arg.Bool(true, "b", false));
        Assert.False(Arg.Bool(0.0, "b", true));
        Assert.True(Arg.Bool(2.0, "b", false));
        Assert.True(Arg.Bool("TRUE", "b", false));
        Assert.False(Arg.Bool("false", "b", true));
        Assert.Equal(XlError.Value, Fails(() => Arg.Bool("maybe", "b", false)).Error);
    }

    [Fact]
    public void ScalarParametersAcceptAOneByOneRangeButNotALargerOne()
    {
        Assert.Equal(2.5, Arg.Double(new object?[,] { { 2.5 } }, "x"));
        Assert.Equal("EUR", Arg.Text(new object?[,] { { "EUR" } }, "x"));
        var e = Fails(() => Arg.Double(new object?[,] { { 1.0, 2.0 } }, "x"));
        Assert.Contains("1x2", e.Message);
        Assert.True(Arg.IsMissing(new object?[,] { { null } }));
    }

    [Fact]
    public void RangesAreFlattenedRowMajor()
    {
        var column = new object?[,] { { 1.0 }, { 2.0 }, { 3.0 } };
        var row = new object?[,] { { 1.0, 2.0, 3.0 } };
        var block = new object?[,] { { 1.0, 2.0 }, { 3.0, 4.0 } };
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, Arg.Doubles(column, "x"));
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, Arg.Doubles(row, "x"));
        Assert.Equal(new[] { 1.0, 2.0, 3.0, 4.0 }, Arg.Doubles(block, "x"));
        Assert.Equal(new[] { 4.0 }, Arg.Doubles(4.0, "x"));
        Assert.Equal(new[] { 1.0, 2.0 }, Arg.Doubles(new object?[] { 1.0, "2" }, "x"));
        Assert.Equal(new[] { "3M", "1Y" }, Arg.Texts(new object?[,] { { "3M" }, { "1Y" } }, "x"));
    }

    [Fact]
    public void ARangeWithAnEmptyCellNamesTheOffendingPosition()
    {
        var e = Fails(() => Arg.Doubles(new object?[,] { { 1.0 }, { null }, { 3.0 } }, "strikes"));
        Assert.Contains("strikes[2]", e.Message);
        var text = Fails(() => Arg.Texts(new object?[,] { { "a" }, { 4.0 }, { "" } }, "tenors"));
        Assert.Contains("tenors[3]", text.Message);
        Assert.Equal(XlError.Value, Fails(() => Arg.Doubles(null, "x")).Error);
        Assert.Equal(XlError.Value, Fails(() => Arg.Doubles(new object?[0, 0], "x")).Error);
        Assert.Equal(XlError.Value, Fails(() => Arg.Doubles(new object?[,] { { 1.0 }, { "x" } }, "x")).Error);
    }

    [Fact]
    public void DatesComeFromSerialsIsoStringsAndDotNetDates()
    {
        Assert.Equal(new DateOnly(2024, 1, 1), Arg.Date(45292.0, "d"));
        Assert.Equal(new DateOnly(2024, 1, 1), Arg.Date("45292", "d"));
        Assert.Equal(new DateOnly(2024, 1, 1), Arg.Date("2024-01-01", "d"));
        Assert.Equal(new DateOnly(2024, 1, 1), Arg.Date(new DateOnly(2024, 1, 1), "d"));
        Assert.Equal(new DateOnly(2024, 1, 1), Arg.Date(new DateTime(2024, 1, 1, 13, 0, 0), "d"));
        Assert.Equal(new DateOnly(1900, 3, 1), Arg.Date(61.0, "d"));
        Assert.Equal(new DateOnly(2024, 1, 1), Arg.Date(new object?[,] { { 45292.0 } }, "d"));
    }

    [Fact]
    public void DatesRejectTheFakeLeapDayAndGarbage()
    {
        var fake = Fails(() => Arg.Date(60.0, "d"));
        Assert.Equal(XlError.Value, fake.Error);
        Assert.Contains("d:", fake.Message);
        Assert.Equal(XlError.Value, Fails(() => Arg.Date(0.0, "d")).Error);
        Assert.Equal(XlError.Value, Fails(() => Arg.Date("01/02/2024", "d")).Error);
        Assert.Equal(XlError.Value, Fails(() => Arg.Date("2024-13-01", "d")).Error);
        Assert.Equal(XlError.Value, Fails(() => Arg.Date(null, "d")).Error);
        Assert.Equal(XlError.Value, Fails(() => Arg.Date(true, "d")).Error);
    }
}
