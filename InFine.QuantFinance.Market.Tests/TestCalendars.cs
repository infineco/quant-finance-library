using System;
using System.Collections.Generic;

namespace InFine.QuantFinance.Market.Tests;

/// <summary>A calendar closed on an explicit set of weekdays, to build month-end edge cases at will.</summary>
internal sealed class FixedClosuresCalendar : HolidayCalendar
{
    private readonly HashSet<DateOnly> _closed;

    public FixedClosuresCalendar(params DateOnly[] closed) => _closed = new HashSet<DateOnly>(closed);

    public override string Name => "Fixed closures";

    protected override bool IsClosedWeekday(DateOnly date) => _closed.Contains(date);
}

internal static class D
{
    public static DateOnly Of(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd");
}
