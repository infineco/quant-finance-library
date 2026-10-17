using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>A dated amount: <paramref name="Amount"/> in currency units paid on <paramref name="Date"/>.</summary>
    public readonly record struct CashFlow(DateOnly Date, double Amount);

    /// <summary>
    /// One coupon of a fixed or floating leg: the accrual period, the payment date, the accrual year fraction, the rate
    /// applied and the resulting amount <c>Amount = notional x Rate x YearFraction</c>.
    /// </summary>
    /// <param name="AccrualStart">First accrual date (adjusted for swaps and floating-rate notes, unadjusted for bonds).</param>
    /// <param name="AccrualEnd">Last accrual date, on the same basis as <paramref name="AccrualStart"/>.</param>
    /// <param name="PaymentDate">The business-day-adjusted payment date.</param>
    /// <param name="YearFraction">The accrual year fraction under the leg's day-count convention.</param>
    /// <param name="Rate">The coupon rate: the fixed rate, or the simple forward plus spread for a floating coupon.</param>
    /// <param name="Amount">The coupon amount in currency units, for the leg's notional.</param>
    public readonly record struct LegCashFlow(
        DateOnly AccrualStart,
        DateOnly AccrualEnd,
        DateOnly PaymentDate,
        double YearFraction,
        double Rate,
        double Amount);
}
