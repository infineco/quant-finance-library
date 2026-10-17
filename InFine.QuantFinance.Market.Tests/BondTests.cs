using System;
using System.Linq;
using Xunit;

namespace InFine.QuantFinance.Market.Tests;

/// <summary>
/// Fixed-rate bonds: hand-checkable prices and accruals, the closed forms of a level annuity as independent references,
/// finite differences for the risk measures, and consistency of the yield-based and curve-based prices.
/// </summary>
public class BondTests
{
    private static void Near(double expected, double actual, double tolerance, string? what = null) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"{what}expected {expected:R}, got {actual:R} (tolerance {tolerance})");

    private static FixedRateBond Annual5Y(double coupon = 0.05) =>
        new(D.Of("2025-01-15"), D.Of("2030-01-15"), coupon, Frequency.Annual);

    [Fact]
    public void FivePercentFiveYearBondAtFivePercentIsWorthPar()
    {
        // 5% annual coupon, 5Y, yield 5% on the issue date: P = 5 sum_{k=1..5} 1.05^-k + 100 * 1.05^-5 = 100 exactly.
        var bond = Annual5Y();
        var settle = D.Of("2025-01-15");
        Near(100.0, bond.DirtyPriceFromYield(0.05, settle), 1e-12);
        Near(100.0, bond.CleanPriceFromYield(0.05, settle), 1e-12);
        Assert.Equal(0.0, bond.AccruedInterest(settle));
        Near(0.05, bond.YieldFromCleanPrice(100.0, settle), 1e-14);
        // Act/Act ICMA gives a coupon of exactly rate / frequency per 100, whatever the length of the year.
        Assert.All(bond.Coupons, c => Near(5.0, c.Amount, 1e-13));
        // 15 Jan 2028 is a Saturday: the coupon accrues to the 15th but is paid on Monday 17th (Following).
        Assert.Equal(D.Of("2028-01-17"), bond.Coupons[2].PaymentDate);
        Assert.Equal(D.Of("2028-01-15"), bond.Coupons[2].AccrualEnd);
        Assert.Equal(6, bond.CashFlows.Count);
        Near(100.0, bond.CashFlows[^1].Amount, 0);
        Assert.Equal(bond.FinalPaymentDate, bond.CashFlows[^1].Date);
    }

    [Fact]
    public void DurationAndConvexityOfTheParBondMatchClosedForms()
    {
        // Annual bond at issue, coupon c, yield y, n years. Macaulay duration (Macaulay 1938):
        //   D = (1+y)/y - (1 + y + n (c - y)) / (c ((1+y)^n - 1) + y)  = 4.54595050416... for c = y = 5%, n = 5.
        // Convexity of a par bond: (1/P) sum t (t+1) CF_t (1+y)^-(t+2).
        var bond = Annual5Y();
        var settle = D.Of("2025-01-15");
        const double y = 0.05;
        const int n = 5;
        var macaulay = (1 + y) / y - (1 + y + n * (y - y)) / (y * (Math.Pow(1 + y, n) - 1) + y);
        Near(4.5459505041623665, macaulay, 1e-13);
        Near(macaulay, bond.MacaulayDuration(y, settle), 1e-12);
        Near(macaulay / (1 + y), bond.ModifiedDuration(y, settle), 1e-12);
        var convexity = Enumerable.Range(1, n).Sum(t => t * (t + 1.0) * (t == n ? 105.0 : 5.0) * Math.Pow(1 + y, -(t + 2))) / 100.0;
        Near(convexity, bond.Convexity(y, settle), 1e-11);
        // DV01 = modified duration x price x 1bp.
        Near(macaulay / (1 + y) * 100.0 * 1e-4, bond.Dv01(y, settle), 1e-12);
    }

    [Fact]
    public void SemiannualBondByAnnuityFormula()
    {
        // 4% semiannual, 3Y, yield 5% (2.5% per half-year), n = 6 periods, coupon 2:
        //   P = 2 (1 - 1.025^-6) / 0.025 + 100 * 1.025^-6 = 97.2459373192.  The bond is cheap: the coupon is below the yield.
        var bond = new FixedRateBond(D.Of("2025-03-17"), D.Of("2028-03-17"), 0.04, Frequency.Semiannual);
        var settle = D.Of("2025-03-17");
        Near(97.2459373192101, bond.DirtyPriceFromYield(0.05, settle), 1e-11);
        Near(97.2459373192101, bond.CleanPriceFromYield(0.05, settle), 1e-11);
        Assert.Equal(0.05, bond.YieldFromCleanPrice(97.2459373192101, settle), 12);
    }

    [Fact]
    public void TreasuryNoteAccruedAndPriceActActIcma()
    {
        // 4.25% note, coupons 15 May / 15 Nov, issued 15 Nov 2024, maturing 15 Nov 2034. Settlement 10 Mar 2025.
        // Current period 15 Nov 2024 -> 15 May 2025 has 15+31+31+28+31+30 + ... = 30+31+31+28+31+30 = 181 days
        // (Nov 15->Dec 15: 30, ->Jan 15: 31, ->Feb 15: 31, ->Mar 15: 28, ->Apr 15: 31, ->May 15: 30).
        // Days elapsed 15 Nov -> 10 Mar = 120 - 5 = 115. Accrued per 100 = (4.25 / 2) x 115 / 181 = 244.375 / 181 = 1.350138...
        // To the next coupon: 66 days, so the first exponent is w = 66/181 coupon periods. At a 4.5% street yield (2.25% per
        // period, v = 1/1.0225) the 20 remaining coupons c = 2.125 give
        //   dirty = v^w [ c (1 - v^20) / (1 - v) + 100 v^19 ] = 99.39987837...,  clean = dirty - accrued = 98.04974025...
        var bond = new FixedRateBond(D.Of("2024-11-15"), D.Of("2034-11-15"), 0.0425, Frequency.Semiannual, settlementDays: 1);
        var settle = D.Of("2025-03-10");
        Near(244.375 / 181.0, bond.AccruedInterest(settle), 1e-13);
        Near(1.3501381215469612, bond.AccruedInterest(settle), 1e-13);
        var v = 1 / 1.0225;
        var w = 66.0 / 181.0;
        var dirty = Math.Pow(v, w) * (2.125 * (1 - Math.Pow(v, 20)) / (1 - v) + 100 * Math.Pow(v, 19));
        Near(99.39987837453997, dirty, 1e-12);
        Near(dirty, bond.DirtyPriceFromYield(0.045, settle), 1e-11);
        Near(98.049740252993, bond.CleanPriceFromYield(0.045, settle), 1e-10);
        Assert.Equal(0.045, bond.YieldFromCleanPrice(98.049740252993, settle), 9);
        // Settlement is one business day after trade date.
        Assert.Equal(settle, bond.SettlementDate(D.Of("2025-03-07"))); // Friday -> Monday
    }

    [Fact]
    public void Thirty360CorporateAccruedAndPrice()
    {
        // 6% semiannual 30/360 bond, coupons 15 Feb / 15 Aug, issued 15 Aug 2024, maturing 15 Aug 2029; settlement 20 Apr 2025.
        // 30/360 from 15 Feb to 20 Apr = 2 x 30 + 5 = 65 days of 180: accrued = 3 x 65/180 = 1.08333...; to the next coupon
        // 115/180 of a period. Nine coupons remain; 5% yield: dirty = v^(115/180) [3 (1 - v^9)/(1 - v) + 100 v^8], v = 1/1.025.
        var bond = new FixedRateBond(D.Of("2024-08-15"), D.Of("2029-08-15"), 0.06, Frequency.Semiannual, DayCounter.Thirty360BondBasis);
        var settle = D.Of("2025-04-20");
        Near(3.0 * 65 / 180, bond.AccruedInterest(settle), 1e-13);
        Near(104.91679385429964, bond.DirtyPriceFromYield(0.05, settle), 1e-11);
        Near(103.83346052096631, bond.CleanPriceFromYield(0.05, settle), 1e-11);
        // Regular coupons are exactly 3 per 100 under 30/360.
        Assert.All(bond.Coupons, c => Near(3.0, c.Amount, 1e-13));
    }

    [Fact]
    public void ShortFirstCouponUsesTheNotionalReferencePeriod()
    {
        // Issued 10 Mar 2025, semiannual to 15 May 2027: the first coupon covers 10 Mar -> 15 May 2025 (66 days) of the
        // notional period 15 Nov 2024 -> 15 May 2025 (181 days): coupon = 100 x 5% / 2 x 66/181 = 0.9116022.
        // At a 4% yield, on the issue date: e0 = 66/181 periods (f x 66/(f x 181)), then +1 per regular period; four more coupons.
        var bond = new FixedRateBond(D.Of("2025-03-10"), D.Of("2027-05-15"), 0.05, Frequency.Semiannual);
        Assert.Equal(5, bond.Coupons.Count);
        Near(2.5 * 66 / 181, bond.Coupons[0].Amount, 1e-13);
        Assert.False(bond.Schedule.IsRegular(0));
        Near(102.07572574434242, bond.DirtyPriceFromYield(0.04, D.Of("2025-03-10")), 1e-11);
        Near(0.0, bond.AccruedInterest(D.Of("2025-03-10")), 0);
        // Half way through the stub the accrued interest is the corresponding share of the stub coupon.
        Near(2.5 * 33 / 181, bond.AccruedInterest(D.Of("2025-04-12")), 1e-13);
    }

    [Fact]
    public void ExCouponTradingGivesNegativeAccruedAndDropsTheCoupon()
    {
        // 4% semiannual, 10 Jun / 10 Dec, 7 business days ex-coupon. The coupon of 10 Dec 2025 (Wednesday) goes ex on
        // Monday 1 Dec 2025 (7 business days before: Dec 9, 8, 5, 4, 3, 2, 1).
        var bond = new FixedRateBond(D.Of("2025-06-10"), D.Of("2030-06-10"), 0.04, Frequency.Semiannual, exCouponDays: 7);
        // Fri 28 Nov, cum-coupon: 171 days elapsed of 183 -> accrued 2 x 171/183.
        Near(2.0 * 171 / 183, bond.AccruedInterest(D.Of("2025-11-28")), 1e-13);
        // Tue 2 Dec, ex-coupon: -2 x 8/183 (8 days to the payment).
        var settle = D.Of("2025-12-02");
        Near(-16.0 / 183, bond.AccruedInterest(settle), 1e-13);
        // Dirty price at 4.2% without the coming coupon: nine later coupons, e0 = 8/183 periods.
        Near(99.09758439034653, bond.DirtyPriceFromYield(0.042, settle), 1e-11);
        Near(99.09758439034653 + 16.0 / 183, bond.CleanPriceFromYield(0.042, settle), 1e-11);
        // Cum-coupon, the same bond carries that coupon.
        var cum = new FixedRateBond(D.Of("2025-06-10"), D.Of("2030-06-10"), 0.04, Frequency.Semiannual);
        Near(99.09758439034653 + 2.0 * Math.Pow(1 + 0.021, -8.0 / 183), cum.DirtyPriceFromYield(0.042, settle), 1e-11);
        // On the settlement day itself the coupon belongs to the seller: no accrual deducted, nothing paid.
        Near(0.0, bond.AccruedInterest(D.Of("2025-12-10")), 1e-13);
    }

    public static TheoryData<string, YieldCompounding> Cases()
    {
        var data = new TheoryData<string, YieldCompounding>();
        foreach (var date in new[] { "2025-01-15", "2025-03-03", "2026-07-20", "2029-11-30" })
            foreach (var c in Enum.GetValues<YieldCompounding>())
                data.Add(date, c);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void PriceYieldRoundTrip(string settleText, YieldCompounding compounding)
    {
        var bond = new FixedRateBond(D.Of("2024-09-30"), D.Of("2031-03-31"), 0.0375, Frequency.Semiannual, calendar: TargetCalendar.Instance);
        var settle = D.Of(settleText);
        foreach (var y in new[] { -0.004, 0.0, 0.021, 0.0375, 0.09, 0.2 })
        {
            var price = bond.CleanPriceFromYield(y, settle, compounding);
            Near(y, bond.YieldFromCleanPrice(price, settle, compounding), 1e-12, $"yield {y}: ");
            var back = bond.CleanPriceFromYield(bond.YieldFromCleanPrice(price, settle, compounding), settle, compounding);
            Near(price, back, 1e-12, $"price at yield {y}: ");
        }
    }

    [Theory]
    [InlineData(YieldCompounding.Periodic)]
    [InlineData(YieldCompounding.Annual)]
    [InlineData(YieldCompounding.Continuous)]
    public void RiskMeasuresMatchFiniteDifferencesOfThePriceYieldFunction(YieldCompounding compounding)
    {
        var bond = new FixedRateBond(D.Of("2024-09-30"), D.Of("2031-03-31"), 0.0375, Frequency.Semiannual, calendar: TargetCalendar.Instance);
        var settle = D.Of("2026-07-20");
        const double y = 0.031;
        const double h = 1e-3;
        double P(double rate) => bond.DirtyPriceFromYield(rate, settle, compounding);
        // Fourth-order central stencils (error ~ h^4 P^(5)/30 and h^4 P^(6)/90).
        var d1 = (-P(y + 2 * h) + 8 * P(y + h) - 8 * P(y - h) + P(y - 2 * h)) / (12 * h);
        var d2 = (-P(y + 2 * h) + 16 * P(y + h) - 30 * P(y) + 16 * P(y - h) - P(y - 2 * h)) / (12 * h * h);
        var price = P(y);
        var modified = bond.ModifiedDuration(y, settle, compounding);
        var convexity = bond.Convexity(y, settle, compounding);
        Assert.True(Math.Abs(modified / (-d1 / price) - 1) < 1e-8, $"modified {modified} vs {-d1 / price}");
        Assert.True(Math.Abs(convexity / (d2 / price) - 1) < 1e-8, $"convexity {convexity} vs {d2 / price}");
        Near(modified * price * 1e-4, bond.Dv01(y, settle, compounding), 1e-13);
        var macaulay = bond.MacaulayDuration(y, settle, compounding);
        var adjustment = compounding switch
        {
            YieldCompounding.Periodic => 1 + y / 2,
            YieldCompounding.Annual => 1 + y,
            _ => 1.0,
        };
        Near(macaulay / adjustment, modified, 1e-12);
    }

    [Fact]
    public void FlatCurveWithContinuousRateEqualsContinuousYieldPrice()
    {
        // 30/360 bond, annual 5%, curve time also 30/360 (additive on mid-month dates): a flat 4% continuous curve is
        // exactly the continuous 4% yield, whether settlement is on the reference date or two days later.
        var issue = D.Of("2025-03-15");
        var bond = new FixedRateBond(issue, D.Of("2030-03-15"), 0.05, Frequency.Annual, DayCounter.Thirty360BondBasis,
            paymentConvention: BusinessDayConvention.Unadjusted);
        var curve = new FlatForward(issue, 0.04, DayCounter.Thirty360BondBasis);
        Near(bond.DirtyPriceFromYield(0.04, issue, YieldCompounding.Continuous), bond.DirtyPrice(curve), 1e-12);
        Near(bond.CleanPriceFromYield(0.04, issue, YieldCompounding.Continuous), bond.CleanPrice(curve), 1e-12);
        // An annually compounded 4% curve is the annual 4% yield.
        var annual = new FlatForward(issue, 0.04, DayCounter.Thirty360BondBasis, Compounding.Annual);
        Near(bond.DirtyPriceFromYield(0.04, issue, YieldCompounding.Annual), bond.DirtyPrice(annual), 1e-12);
        // Annual coupons: street yield = annual yield, and the par bond identity holds.
        Near(bond.DirtyPriceFromYield(0.04, issue, YieldCompounding.Annual), bond.DirtyPriceFromYield(0.04, issue), 1e-12);
        // Settlement 2 days after valuation, mid-period: the price is a value at the settlement date.
        var later = new FixedRateBond(issue, D.Of("2030-03-15"), 0.05, Frequency.Annual, DayCounter.Thirty360BondBasis,
            settlementDays: 2, paymentConvention: BusinessDayConvention.Unadjusted);
        var valuation = D.Of("2025-06-12");
        var settle = later.SettlementDate(valuation); // Friday 14 Jun
        var curve2 = new FlatForward(valuation, 0.04, DayCounter.Thirty360BondBasis);
        Assert.Equal(D.Of("2025-06-16"), settle);
        Near(later.DirtyPriceFromYield(0.04, settle, YieldCompounding.Continuous), later.DirtyPrice(curve2), 1e-12);
        // Npv is the currency value at the reference date: dirty price x P(settlement) x notional / 100.
        Near(later.DirtyPrice(curve2) * curve2.DiscountFactor(settle), later.Npv(curve2), 1e-12);
    }

    [Fact]
    public void ActActIcmaOnAnnualAnniversariesIsAdditiveSoAFlatCurveMatchesTheYield()
    {
        // 1 Mar 2025 -> 1 Mar 2027 contains no 29 February: each Act/Act (ISDA) year is exactly 1, as the ICMA fraction is.
        var issue = D.Of("2025-03-01");
        var bond = new FixedRateBond(issue, D.Of("2027-03-01"), 0.06, Frequency.Annual, paymentConvention: BusinessDayConvention.Unadjusted);
        var curve = new FlatForward(issue, 0.035, DayCounter.ActualActualIsda);
        Near(bond.DirtyPriceFromYield(0.035, issue, YieldCompounding.Continuous), bond.DirtyPrice(curve), 1e-12);
        Near(6 * Math.Exp(-0.035) + 106 * Math.Exp(-0.07), bond.DirtyPrice(curve), 1e-12);
    }

    [Fact]
    public void ZSpreadIsZeroWhenTheCurveRepricesTheBondAndRecoversAShift()
    {
        var trade = D.Of("2025-01-15");
        var curve = BuildEurCurve(trade);
        var bond = new FixedRateBond(D.Of("2024-07-15"), D.Of("2032-07-15"), 0.03, Frequency.Annual, calendar: TargetCalendar.Instance,
            settlementDays: 2);
        var model = bond.CleanPrice(curve);
        Near(0.0, bond.ZSpread(model, curve), 1e-12);
        foreach (var spread in new[] { -0.004, 0.0015, 0.03 })
        {
            var shifted = ZeroShiftedCurve.Parallel(curve, spread);
            Near(spread, bond.ZSpread(bond.CleanPrice(shifted), curve), 1e-12, $"spread {spread}: ");
        }
        // Cheaper bond, wider spread; the spread reprices it.
        var z = bond.ZSpread(model - 2.0, curve);
        Assert.True(z > 0);
        Near(model - 2.0, bond.CleanPrice(ZeroShiftedCurve.Parallel(curve, z)), 1e-10);
        // Flat curve: Z-spread over the 3% curve of the price from a 3.4% curve is 40bp.
        var flat3 = new FlatForward(trade, 0.03, DayCounter.Actual365Fixed);
        var flat34 = new FlatForward(trade, 0.034, DayCounter.Actual365Fixed);
        Near(0.004, bond.ZSpread(bond.CleanPrice(flat34), flat3), 1e-12);
    }

    [Fact]
    public void EffectiveDurationOnAFlatCurveIsTheContinuousModifiedDuration()
    {
        var issue = D.Of("2025-03-15");
        var bond = new FixedRateBond(issue, D.Of("2030-03-15"), 0.05, Frequency.Annual, DayCounter.Thirty360BondBasis,
            paymentConvention: BusinessDayConvention.Unadjusted);
        var curve = new FlatForward(issue, 0.04, DayCounter.Thirty360BondBasis);
        var analytic = bond.ModifiedDuration(0.04, issue, YieldCompounding.Continuous);
        var price = bond.DirtyPrice(curve);
        Assert.True(Math.Abs(bond.EffectiveDuration(curve) / analytic - 1) < 1e-6);
        Near(analytic * price * 1e-4, bond.EffectiveDv01(curve), 1e-6 * analytic * price * 1e-4);
        // Key-rate DV01s add up to the parallel one (the triangular weights sum to one).
        var keys = new[] { 1.0, 2.0, 3.0, 5.0, 7.0 };
        var krd = bond.KeyRateDv01s(curve, keys);
        Assert.True(Math.Abs(krd.Sum() / bond.EffectiveDv01(curve) - 1) < 1e-6, $"{krd.Sum()} vs {bond.EffectiveDv01(curve)}");
        // A 5Y bond has nothing beyond 5Y: the 7Y key rate carries no risk, the 5Y one carries the principal and last coupon.
        Near(0.0, krd[4], 1e-12);
        Assert.True(krd[3] > krd[2] && krd[3] > krd[1] && krd[3] > krd[0]);
    }

    [Fact]
    public void NotionalAndRedemptionScaleAmountsNotPrices()
    {
        var plain = Annual5Y();
        var big = new FixedRateBond(D.Of("2025-01-15"), D.Of("2030-01-15"), 0.05, Frequency.Annual, redemption: 102.0, notional: 1_000_000);
        var settle = D.Of("2025-01-15");
        Near(1_000_000 * 0.05, big.Coupons[0].Amount, 1e-7);
        Near(1_020_000, big.CashFlows[^1].Amount, 1e-7);
        // Prices per 100: redemption 102 adds 2 v^5 to the price.
        Near(plain.DirtyPriceFromYield(0.04, settle) + 2 * Math.Pow(1.04, -5), big.DirtyPriceFromYield(0.04, settle), 1e-11);
    }

    [Fact]
    public void RejectsInvalidSettlementAndFrequency()
    {
        var bond = Annual5Y();
        Assert.Throws<ArgumentException>(() => bond.DirtyPriceFromYield(0.05, D.Of("2025-01-14")));
        Assert.Throws<ArgumentException>(() => bond.DirtyPriceFromYield(0.05, D.Of("2030-01-15")));
        Assert.Equal(0.0, bond.AccruedInterest(D.Of("2031-01-01")));
        Assert.Throws<ArgumentException>(() => new FixedRateBond(D.Of("2025-01-15"), D.Of("2030-01-15"), 0.05, Frequency.Once));
        Assert.Throws<ArgumentOutOfRangeException>(() => bond.YieldFromDirtyPrice(-1.0, D.Of("2025-01-15")));
    }

    [Fact]
    public void ZeroCouponBondPricesAndYields()
    {
        // Act/Act ISDA 2025-01-15 -> 2028-01-17? Use dates whose fraction is known: 2025-03-01 -> 2028-03-01 = 3 years minus
        // the leap day effect handled by ISDA (2027->2028 splits at 1 Jan): 306/365 + 1 + 1 + 60/366.
        var maturity = D.Of("2028-03-01"); // Wednesday
        var bond = new ZeroCouponBond(maturity);
        var settle = D.Of("2025-03-01");
        var t = 306.0 / 365 + 2 + 60.0 / 366; // ISDA year fraction: (Mar 1 -> Jan 1 2026 = 306) / 365, two whole years, 60 / 366
        Near(t, DayCounter.ActualActualIsda.YearFraction(settle, maturity), 1e-13);
        Near(100 * Math.Pow(1.03, -t), bond.PriceFromYield(0.03, settle), 1e-12);
        Near(100 * Math.Exp(-0.03 * t), bond.PriceFromYield(0.03, settle, YieldCompounding.Continuous), 1e-12);
        Near(100 * Math.Pow(1 + 0.03 / 2, -2 * t), bond.PriceFromYield(0.03, settle, YieldCompounding.Periodic, Frequency.Semiannual), 1e-12);
        foreach (var c in Enum.GetValues<YieldCompounding>())
            Near(0.03, bond.YieldFromPrice(bond.PriceFromYield(0.03, settle, c, Frequency.Quarterly), settle, c, Frequency.Quarterly), 1e-13);
        // Curve price: R P(T) / P(settle) on a flat curve, with the same day counter.
        var curve = new FlatForward(settle, 0.03, DayCounter.ActualActualIsda);
        Near(bond.PriceFromYield(0.03, settle, YieldCompounding.Continuous), bond.Price(curve), 1e-12);
    }

    [Fact]
    public void FloatingRateNoteTradesAtParOnACouponDate()
    {
        // Issued on the curve date, zero spread, the first fixing equal to the curve forward: the coupons telescope, the
        // note is worth par (exactly 100) and no interest has accrued.
        var trade = D.Of("2025-01-15");
        var curve = BuildEurCurve(trade);
        var calendar = TargetCalendar.Instance;
        var issue = D.Of("2025-01-15");
        var maturity = D.Of("2030-01-15");
        var probe = new FloatingRateNote(issue, maturity, Frequency.Semiannual, 0.0, 0.0, calendar: calendar);
        var first = probe.Coupons(curve)[0];
        // Fixing = the first period's forward (the probe used fixing 0 only for the period starting on the curve date, so
        // recompute the forward by hand: (P(s)/P(e) - 1) / tau).
        var forward = (curve.DiscountFactor(first.AccrualStart) / curve.DiscountFactor(first.AccrualEnd) - 1) / first.YearFraction;
        var note = new FloatingRateNote(issue, maturity, Frequency.Semiannual, 0.0, forward, calendar: calendar);
        Near(100.0, note.DirtyPrice(curve), 1e-12);
        Near(0.0, note.AccruedInterest(curve, issue), 0);
        Near(100.0, note.CleanPrice(curve), 1e-12);
        // A spread s adds s x sum tau P / 100-per-100: price = 100 + 100 s A (A = annuity of the floating periods).
        var spreadNote = new FloatingRateNote(issue, maturity, Frequency.Semiannual, 0.002, forward, calendar: calendar);
        var annuity = spreadNote.Coupons(curve).Sum(c => c.YearFraction * curve.DiscountFactor(c.PaymentDate));
        Near(100.0 + 100.0 * 0.002 * annuity, spreadNote.DirtyPrice(curve), 1e-11);
        Assert.Equal(10, spreadNote.Coupons(curve).Count);
    }

    // A humped single curve bootstrapped from deposits and swaps (not market data).
    internal static InterpolatedDiscountCurve BuildEurCurve(DateOnly trade)
    {
        var builder = new PiecewiseYieldCurveBuilder(trade, DayCounter.Actual365Fixed).WithInterpolation(CurveInterpolation.MonotoneCubicZero);
        foreach (var (tenor, rate) in new[] { ("3M", 0.0275), ("6M", 0.0260) })
            builder.Add(Deposit.FromTenor(trade, Period.Parse(tenor), TargetCalendar.Instance), rate);
        foreach (var (tenor, rate) in SwapQuotes)
            builder.Add(VanillaSwap.FromTenor(trade, Period.Parse(tenor), SwapConventions.EurSixMonth), rate);
        return builder.Build();
    }

    internal static readonly (string Tenor, double Rate)[] SwapQuotes =
    [
        ("1Y", 0.0245), ("2Y", 0.0230), ("3Y", 0.0228), ("5Y", 0.0233), ("7Y", 0.0240), ("10Y", 0.0250), ("15Y", 0.0258), ("30Y", 0.0235),
    ];
}
