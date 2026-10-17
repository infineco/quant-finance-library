# 02 - Yield curve

Bootstraps a EUR-like curve from three deposits (1M, 3M, 6M) and seven swaps (1Y to 15Y) on 2026-03-16, then prices a bond
and a swap on it. Dates are real: TARGET calendar, T+2 spot, modified-following roll, Actual/360 deposits, 30/360 fixed
swap legs (`SwapConventions.EurSixMonth`), Actual/Actual ICMA for the bond.

- `PiecewiseYieldCurveBuilder` solves one discount factor per pillar so that every instrument reprices its quote
  (log-linear interpolation of discount factors, so a single pass suffices). The same curve discounts and projects the
  floating leg: a single-curve setup, kept simple on purpose.
- The curve prints discount factors, continuously compounded and annual zero rates, and 1-year forwards.
- `FixedRateBond`: clean and dirty price off the curve, accrued interest, yield from the clean price, Macaulay and modified
  duration, convexity, DV01, effective duration on the curve, and the Z-spread of a market price.
- `VanillaSwap`: par rate, NPV, PV01 and DV01 of a 5-year payer, and its fixed-leg cash flows. The DV01 is the full
  bump-and-reprice of the curve (it moves the floating leg too); the PV01 is the fixed-leg annuity times one basis point.

The quotes are illustrative, not market data.

## Run

```sh
dotnet run -c Release --project samples/02-YieldCurve
```

## Expected output

```text
Trade date 2026-03-16 (Monday), spot 2026-03-18, calendar TARGET

== Instruments ==
Instrument           Start    Maturity    Quote
Deposit 1M      2026-03-18  2026-04-20   1.980%
Deposit 3M      2026-03-18  2026-06-18   2.050%
Deposit 6M      2026-03-18  2026-09-18   2.120%
Swap 1Y         2026-03-18  2027-03-18   2.210%
Swap 2Y         2026-03-18  2028-03-20   2.380%
Swap 3Y         2026-03-18  2029-03-19   2.510%
Swap 5Y         2026-03-18  2031-03-18   2.690%
Swap 7Y         2026-03-18  2033-03-18   2.810%
Swap 10Y        2026-03-18  2036-03-18   2.940%
Swap 15Y        2026-03-18  2041-03-18   3.050%

Bootstrap: 1 pass(es), max repricing error 1.95E-15 (in rate)

== Curve (log-linear discount factors) ==
      Pillar  t (yrs)         DF  Zero cont.  Zero annual  Fwd 1y cont.
  2026-04-20    0.096   0.998079     2.0057%      2.0259%       2.2319%
  2026-06-18    0.258   0.994679     2.0716%      2.0932%       2.2972%
  2026-09-18    0.510   0.989172     2.1365%      2.1594%       2.3758%
  2027-03-18    1.005   0.978270     2.1850%      2.2090%       2.5145%
  2028-03-20    2.014   0.953781     2.3500%      2.3778%       2.7447%
  2029-03-19    3.011   0.928034     2.4805%      2.5115%       2.9369%
  2031-03-18    5.008   0.875163     2.6625%      2.6983%       3.0903%
  2033-03-18    7.011   0.822641     2.7847%      2.8239%       3.2384%
  2036-03-18   10.014   0.746412     2.9208%      2.9638%       3.2741%
  2041-03-18   15.016   0.633642     3.0385%      3.0851%       3.2741%

== Bond: 2.50% annual Actual/Actual (ICMA), maturity 2033-05-15, settlement 2026-03-18 ==
Priced on the curve:  clean 97.9300  dirty 100.0327  accrued 2.1027
Yield (annual, from clean price) 2.8227%
Macaulay duration 6.4992  modified duration 6.3208  convexity 48.859  DV01 0.0632 per 100
Effective duration on the curve 6.5030  Z-spread at the curve price 0.000 bp
At a market clean price of 97.50: yield 2.8909%, Z-spread 6.6 bp over the curve

== Swap: 5Y payer 2.50% on 10,000,000, 2026-03-18 to 2031-03-18 ==
Par rate 2.6900%  NPV 88,097.53  PV01 (annuity x 1bp) 4,636.71  DV01 4,719.70

Fixed leg     Accrual end  Year frac        Coupon         DF
               2027-03-18   1.000000    250,000.00   0.978270
               2028-03-20   1.005556    251,388.89   0.953781
               2029-03-19   0.997222    249,305.56   0.928034
               2030-03-18   0.997222    249,305.56   0.901247
               2031-03-18   1.000000    250,000.00   0.875163
```
