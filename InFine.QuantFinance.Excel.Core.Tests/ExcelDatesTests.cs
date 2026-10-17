using System;
using Xunit;

namespace InFine.QuantFinance.Excel.Core.Tests;

public class ExcelDatesTests
{
    [Theory]
    [InlineData(1, "1900-01-01")]
    [InlineData(2, "1900-01-02")]
    [InlineData(59, "1900-02-28")]
    [InlineData(61, "1900-03-01")]   // the day after the fake 1900-02-29
    [InlineData(62, "1900-03-02")]
    [InlineData(25569, "1970-01-01")]
    [InlineData(36526, "2000-01-01")]
    [InlineData(45292, "2024-01-01")]
    [InlineData(45351, "2024-02-29")]
    [InlineData(2958465, "9999-12-31")]
    public void SerialToDate(double serial, string iso)
    {
        Assert.Equal(DateOnly.ParseExact(iso, "yyyy-MM-dd"), ExcelDates.ToDate(serial));
    }

    [Fact]
    public void Serial60IsExcelsFakeLeapDayAndIsRejected()
    {
        Assert.False(ExcelDates.TryToDate(60, out _, out var reason));
        Assert.Contains("1900-02-29", reason);
        Assert.Equal(XlError.Value, Assert.Throws<XlException>(() => ExcelDates.ToDate(60)).Error);
        // A time of day on the fake day is still the fake day.
        Assert.False(ExcelDates.TryToDate(60.75, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(-1)]
    [InlineData(2958466)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void SerialsOutsideTheCalendarAreRejected(double serial)
    {
        Assert.False(ExcelDates.TryToDate(serial, out _, out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void TimeOfDayIsDropped()
    {
        Assert.Equal(new DateOnly(2024, 1, 1), ExcelDates.ToDate(45292.999));
        Assert.Equal(new DateOnly(1900, 3, 1), ExcelDates.ToDate(61.5));
    }

    [Theory]
    [InlineData("1900-01-01", 1)]
    [InlineData("1900-02-28", 59)]
    [InlineData("1900-03-01", 61)]
    [InlineData("1970-01-01", 25569)]
    [InlineData("2024-01-01", 45292)]
    [InlineData("9999-12-31", 2958465)]
    public void DateToSerial(string iso, double serial)
    {
        Assert.Equal(serial, ExcelDates.ToSerial(DateOnly.ParseExact(iso, "yyyy-MM-dd")));
    }

    [Fact]
    public void DatesBefore1900CannotBeRepresented()
    {
        var e = Assert.Throws<XlException>(() => ExcelDates.ToSerial(new DateOnly(1899, 12, 31)));
        Assert.Equal(XlError.Value, e.Error);
    }

    [Fact]
    public void RoundTripsEverySerialAcrossTheLeapYearBug()
    {
        for (int serial = 1; serial <= 80000; serial++)
        {
            if (serial == 60) continue;
            Assert.Equal(serial, ExcelDates.ToSerial(ExcelDates.ToDate(serial)));
        }
        // Consecutive serials are consecutive days, except across the missing day.
        Assert.Equal(1, ExcelDates.ToDate(61).DayNumber - ExcelDates.ToDate(59).DayNumber);
    }
}
