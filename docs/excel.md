# Excel add-in

`InFine.QuantFinance.Excel` exposes library functions as worksheet functions with stable `QF.*` names, so desk users
can price vanillas, back out implied volatilities, bootstrap a curve, fit an SVI smile or compute VaR, Expected Shortfall and Monte Carlo risk from a spreadsheet.
It is an [Excel-DNA](https://excel-dna.net) add-in (Excel-DNA 1.9, .NET 8).

## Requirements

- Windows with **64-bit Excel** (Microsoft 365, 2016 or later).
- The **.NET 8 Desktop Runtime (x64)**. Excel-DNA hosts the .NET runtime inside Excel; if it is missing, the add-in
  fails to load and Excel offers a download link.

## Build

On Windows, from the repository root:

```sh
dotnet build InFine.QuantFinance.Excel/InFine.QuantFinance.Excel.csproj -c Release
```

The single-file add-in to distribute (the managed assemblies are packed inside it) is

```
InFine.QuantFinance.Excel/bin/Release/net8.0-windows/publish/InFine.QuantFinance.Excel-AddIn64-packed.xll
```

(the unpacked `InFine.QuantFinance.Excel-AddIn64.xll` next to the DLLs works too, but only from that folder).

The project also builds on Linux and macOS, so `dotnet build QuantFinance.sln` is the same command everywhere and CI
needs no special case; the `.xll` it produces can only be loaded by Excel on Windows.

## Install

1. Copy the packed `.xll` to a local folder. If it came from a download or a network share, right-click it, open
   *Properties* and tick *Unblock*; otherwise Excel's Protected View / macro policy silently refuses it.
2. In Excel: *File > Options > Add-ins > Manage: Excel Add-ins > Go... > Browse...*, pick the `.xll` and keep it
   ticked (it is then loaded at each start). Double-clicking the file loads it for the current session only.
3. Type `=QF.VERSION()` in a cell. The functions are also listed under the category **InFine QuantFinance** in the
   *Insert Function* dialog, with argument help.

The add-in has no ribbon and no state on disk: the objects it creates (curves, surfaces, covariance matrices) live in memory only. After
opening a saved workbook the cells still show the old handle texts but the objects are gone, so dependent cells give
`#REF!` until you force a full recalculation with **Ctrl+Alt+F9**, which rebuilds each object before its users.

## Conventions

- **Dates** are Excel serial numbers, in and out; format result cells as dates. ISO text (`"2025-01-15"`) is also
  accepted as input. Excel's 1900 leap-year bug is handled: serial 60 (the non-existent 1900-02-29) and serial 0 are
  rejected with `#VALUE!`, serial 61 is 1900-03-01. Times of day are ignored.
- **Rates** are decimals (`0.03` is 3%); **volatilities** are decimals (`0.2` is 20%); **times** are in years.
- **Names are case-insensitive** and ignore spaces and hyphens. Calendars: `TARGET`, `NYSE`, `LONDON`, `WEEKENDS`;
  join several with `+` (`TARGET+NYSE`: business day in all of them). Day counts: `ACT/360`, `ACT/365F` (also
  `ACT/365`), `ACT/365.25`, `30/360` (bond basis), `30E/360`, `30E/360 ISDA`, `ACT/ACT` (ISDA), `ACT/ACT AFB`
  (`ACT/ACT ICMA` needs a reference period and is not available by name). Business-day conventions: `Following`,
  `ModifiedFollowing` (`MF`), `Preceding`, `ModifiedPreceding` (`MP`), `Unadjusted`. Option type: `C`/`Call`/`1`,
  `P`/`Put`/`-1`.
- **Ranges** (strikes, tenors...) can be a row, a column or a block (read row by row). An empty cell inside a range is
  an error, never silently skipped, so that paired ranges cannot misalign.
- **Array results** (`QF.BS.GREEKS`, `QF.OBJ.LIST`, `QF.OBJ.INFO`) spill automatically in Excel 365; in older Excel
  select the target range and confirm with Ctrl+Shift+Enter.

## Errors

A cell can only show an Excel error, so the add-in uses them consistently and keeps the reason:

| Error | Meaning |
| --- | --- |
| `#VALUE!` | An argument has the wrong type, is empty, or names something unknown (calendar, day count, option type...). |
| `#NUM!` | The arguments are well-formed but outside the numerical domain (negative volatility, price below intrinsic value, a curve that cannot be bootstrapped, a non-finite result...). |
| `#REF!` | An object handle is unknown, deleted or stale (see below). |
| other | An error value in an argument propagates unchanged to the result. |

`=QF.LASTERROR()` returns the reason of the most recent failure of any `QF` function, with the function name, for
example `QF.BS.PRICE: S: 'abc' is not a number (#VALUE!)`. It is process-wide: with multi-threaded recalculation it is
the last failure to occur, not necessarily the one of the cell you are inspecting.

## Objects and handles

`QF.CURVE.*` and `QF.SVI.FIT` constructors return a **handle**, a text such as `QF.Curve:EUR6M#3`
(`kind:name#version`), and keep the object in memory. Pass the handle to the functions that use it
(`QF.CURVE.DF`, `QF.SURFACE.VOL`...).

- Creating an object under an existing name **replaces** it and bumps the version, so the handle text changes and every
  cell that takes that handle recalculates. Creating it again with **identical inputs** keeps the same handle, so a
  full recalculation (Ctrl+Alt+F9) does not needlessly invalidate every dependent cell.
- A handle whose version is no longer current is **stale** and gives `#REF!` (it is never silently redirected to the
  new object). Same for a deleted object, or a handle pasted as a value after the workbook was closed: objects do
  not survive a restart, recalculate the creating cell.
- Using a handle of the wrong kind (a surface where a curve is expected) gives `#VALUE!`; `QF.LASTERROR()` says which.
- Give each object a unique name per workbook, and avoid creating two objects with the same name in different cells.
- `QF.OBJ.LIST()` lists live objects, `QF.OBJ.INFO(handle)` describes one (parameters, fit quality, bootstrap
  repricing error...), `QF.OBJ.DELETE(handle)` removes it (TRUE if removed, FALSE if unknown or stale).

## Function reference

Optional arguments are in brackets.

### Vanilla options

| Function | Result |
| --- | --- |
| `QF.BS.PRICE(type, S, K, T, r, q, vol)` | Black-Scholes-Merton price of a European option; `r` and `q` continuously compounded. |
| `QF.BS.GREEKS(type, S, K, T, r, q, vol, [greek])` | `greek` = `Price`, `Delta`, `Gamma`, `Vega`, `Theta`, `Rho`, `DivRho`, `Vanna`, `Volga` or `DualDelta` returns one number. `ALL` (default) returns a 1x10 row in that order; `HEADER` returns 2x10 with the names on top. |
| `QF.BLACK76.PRICE(type, F, K, T, DF, vol)` | Black-76 price on forward `F`, discounted by the factor `DF` to the payment date. |
| `QF.IMPLIEDVOL(price, type, S, K, T, r, q)` | Black-Scholes-Merton implied volatility. |
| `QF.IMPLIEDVOL.BLACK76(price, type, F, K, T, DF)` | Black-76 implied volatility. |

Greeks are the raw partial derivatives of the price: vega is per unit of volatility (divide by 100 for one vol point),
theta is per year with calendar time running forward (negative for a long option; divide by 365 for a day), rho is per
unit of rate. See `OptionGreeks` in `InFine.QuantFinance.ClosedForm` for the full definitions.

### Dates

| Function | Result |
| --- | --- |
| `QF.DATE.ADJUST(date, calendar, [convention])` | The date rolled to a business day (default `Following`). |
| `QF.DATE.ADVANCE(date, period, calendar, [convention], [endOfMonth])` | `period` a number or `5B` moves that many business days (negative goes back). A tenor such as `10D`, `2W`, `3M`, `1Y` is added on the calendar and the result adjusted (default `ModifiedFollowing`); `endOfMonth` TRUE keeps month ends at month end. |
| `QF.YEARFRAC(start, end, daycount)` | Year fraction under a day-count convention (negative if `end` is before `start`). |

### Yield curves

| Function | Result |
| --- | --- |
| `QF.CURVE.FLAT(name, refDate, rate, dayCount, [compounding])` | Handle of a flat-forward curve. `compounding` is `Continuous` (default) or `Annual`. |
| `QF.CURVE.BOOTSTRAP(name, refDate, instrumentTypes, tenors, quotes, [interpolation], [calendar])` | Handle of a discount curve bootstrapped from the quotes. |
| `QF.CURVE.DF(handle, date)` | Discount factor from the reference date to `date`. |
| `QF.CURVE.ZERO(handle, date, [compounding])` | Zero rate to `date` (`Continuous` default, `Annual`, `Simple`). |

`QF.CURVE.BOOTSTRAP` takes three ranges of equal length: instrument types (`DEPOSIT`, `FRA`, `SWAP`), tenors
(`3M`, `5Y`; `3x6` for a FRA starting in 3 months and ending in 6) and quoted rates as decimals. `refDate` is the trade
date; the spot date is two business days later on `calendar` (default `TARGET`). Conventions are the euro ones of
`SwapConventions.EurSixMonth`: deposits and FRAs Actual/360, swaps annual 30/360 fixed against 6M Actual/360
floating, single curve for discounting and forecasting. The curve day count is Actual/365 Fixed. `interpolation`
is `LogLinearDiscount` (default), `LinearZero` or `MonotoneCubicZero`. Pillars are the instrument maturities, one
instrument per pillar. Beyond the last pillar the curve extrapolates with a flat forward.

### Volatility

| Function | Result |
| --- | --- |
| `QF.SVI.FIT(name, T, forward, strikes, vols)` | Handle of a raw-SVI smile fitted to Black implied volatilities at one expiry (at least five points; a fit that does not converge is `#NUM!`). |
| `QF.SURFACE.VOL(handle, T, K)` | Implied volatility from the fitted smile; other `T` scale the total variance linearly in time, as `SviVolatilitySurface` does for a single-slice surface. |

### Risk

| Function | Result |
| --- | --- |
| `QF.COV(returns, [method], [lambda], [annualisation], [header])` | N x N covariance matrix of the columns of a T x N range of returns. |
| `QF.COV.OBJ(name, returns, [method], [lambda], [annualisation], [header])` | The same matrix kept by the add-in, with its asset names; returns a handle (`QF.Cov:name#1`). |
| `QF.VAR(data, alpha, [method], [horizon], [positions])` | Value-at-Risk, a positive loss. |
| `QF.ES(data, alpha, [method], [horizon], [positions])` | Expected Shortfall, a positive loss. |
| `QF.VAR.DECOMP(returns, positions, alpha, [method], [horizon], [header])` | Table: asset, marginal VaR, component VaR, % VaR, marginal ES, component ES, % ES, then a Total row. |
| `QF.MCVAR(cov, spots, vols, positions, alpha, scenarios, [seed], [generator], [rate], [horizon])` | 3 x 3 block: VaR and ES by Monte Carlo full revaluation, with standard errors. |

**Covariance.** `returns` is one row per date (oldest first) and one column per asset. `method` is `SAMPLE` (default,
unbiased), `EWMA` (RiskMetrics recursion with decay `lambda`, default 0.94, zero mean), `LW_CC` or `LW_ID`
(Ledoit-Wolf shrinkage towards the constant-correlation matrix or a scaled identity). `annualisation` multiplies the
matrix by the number of periods per year (252 for daily returns); leave it empty for the per-period matrix.
`header` TRUE reads the first row as asset names. Fewer than two dates, a `lambda` outside (0, 1) or a non-positive
annualisation are `#NUM!`.

**VaR and ES.** `alpha` is the confidence level (0.99). Without `positions`, `data` is a sample of P&L (gain positive, any
shape). With `positions` (one currency exposure per asset), `data` is a T x N range of asset returns and the P&L of
scenario t is the exposures times the returns of row t. Methods: `HISTORICAL` (default; empirical quantile and tail
average), `PARAMETRIC` (normal with zero mean and the sample standard deviation of the P&L, the delta-normal VaR) and
`CORNISHFISHER` (the normal quantile corrected for the sample skewness and excess kurtosis; `#NUM!` when the
expansion is not monotone for that sample, which the library refuses rather than return a meaningless number).
`horizon` (periods, default 1) applies the square-root-of-time rule to every method. `QF.VAR.DECOMP` allocates the risk
to the assets by Euler's theorem (`HISTORICAL` or `PARAMETRIC`); the components add up to the Total row. The
historical component VaR is a kernel estimate and is noisy on short samples; the parametric one is exact.

**Monte Carlo VaR.** `QF.MCVAR` reprices every position in every scenario instead of using a linear approximation, so
options are handled with their curvature.

- `cov` is a `QF.COV.OBJ` handle or a plain N x N range: the per-period covariance of the **log** returns of the N
  assets. Build it from log returns, not from simple ones.
- `spots` and `vols` are the N current spot prices and implied volatilities. Volatilities and the risk-free `rate` (default
  0) are held fixed in the scenarios: only spots move.
- `positions` is a table with one row per position: asset (name from the covariance header, or its 1-based number),
  type (`SPOT`, `CALL`, `PUT`), quantity (negative is short), strike, maturity in years and optionally a dividend yield.
  Strike and maturity are not read for `SPOT`. A first row of titles is skipped.
- `generator` is `PHILOX` (default; pseudo-random scenarios) or `SOBOL` (scrambled Sobol quasi-random points; prefer a
  power of two for `scenarios`). `horizon` is in periods of the covariance (a 10-day VaR from a daily covariance is 10);
  the scenarios use the exact option expiry (no time decay) over that horizon.
- The result has a header row and the rows `VaR` and `ES`, each with a standard error. The error is estimated by
  splitting the scenarios into 20 blocks; it is `n/a` below 2000 scenarios and is only indicative for `SOBOL`.

| | A | B | C |
| --- | --- | --- | --- |
| 1 | `=QF.COV.OBJ("PF", Returns, "EWMA", 0.97, , TRUE)` | handle, e.g. `QF.Cov:PF#1` | |
| 2 | `=QF.MCVAR(A1, Spots, Vols, Book, 0.99, 100000, 42)` | a 3 x 3 block | |
| 3 | `=QF.VAR(Returns, 0.99, "HISTORICAL", 10, Exposures)` | 10-day historical VaR | |

#### Asynchronous execution

The risk functions are the ones that can take seconds, so they are computed off Excel's main thread, through
Excel-DNA's `ExcelAsyncUtil.RunTaskWithCancellation`:

- `QF.MCVAR` is always asynchronous. `QF.COV`, `QF.COV.OBJ`, `QF.VAR`, `QF.ES` and `QF.VAR.DECOMP` run inline when their
  ranges hold fewer than 20 000 cells in total (a wait would cost more than the work) and in the background above that.
- While a background computation runs, the cell shows **`#N/A`** ("calculating") and Excel stays responsive. When it
  finishes, the cell is recalculated with the result, and so are its dependents. A real error (`#VALUE!`, `#NUM!`)
  replaces `#N/A` in the same way, with the reason in `QF.LASTERROR()`. Do not wrap these functions in `IFERROR` or
  `IFNA` unless you mean to hide the calculating state.
- Excel recalculating the cell with the same arguments while the computation runs (for example on a full
  recalculation, or when another precedent changes) joins the computation in flight: the arguments are compared by
  value (a digest of the ranges), not by reference. Once the result has been delivered, a recalculation of the cell
  starts the computation again: the cell shows `#N/A`, then the same digits (the result is deterministic). So
  Ctrl+Alt+F9 recomputes every Monte Carlo VaR of the workbook. Changing an argument starts a new computation and **cancels** the
  previous one; so does deleting the cell or closing the workbook. Cancellation is observed while the ranges are read,
  between the stages of the computation, at every row of an EWMA recursion and every 2048 Monte Carlo scenarios; the
  steps inside a library call (a Ledoit-Wolf estimate, a sort) run to completion.
- These functions are not marked thread-safe (`IsThreadSafe`): Excel-DNA's asynchronous calls are built on RTD topics,
  which Excel supports only from the main calculation thread. The heavy work runs on the .NET thread pool instead, so
  it still uses the cores of the machine. The other functions of the add-in remain thread-safe.

Because a result arrives after the first calculation, a workbook that prints or exports straight after
opening should be given time to finish (the status bar shows Excel calculating while a function is pending, and
`Ctrl+Alt+F9` re-runs everything).

#### Determinism and performance

`QF.MCVAR` is deterministic: for the same inputs and `seed` it returns the same digits on every run on a given machine and runtime, whatever the number of threads (last-bit differences of `exp` and `log` between CPU types are possible across machines). Each scenario is a function of its index only
(counter-based Philox, or scrambled Sobol seeded by `seed`), so the chunks of 2048 scenarios can run in any order on any
core. To get an independent estimate, change the seed; to compare two portfolios, keep it. The scenarios run in
parallel on all cores; cost grows linearly with scenarios times positions. Memory is 8 bytes per scenario (50 million is the limit, `#NUM!` beyond).
The standard error shrinks as one over the square root of the number of scenarios: quadrupling the scenarios halves it.
The covariance estimates are O(T x N^2); only a very long history on hundreds of assets takes noticeable time.

### Objects and diagnostics

| Function | Result |
| --- | --- |
| `QF.OBJ.LIST()` | A header row and one row per live object (handle, kind, name, version, type, creation time, description). Volatile. |
| `QF.OBJ.INFO(handle)` | Two-column table of properties and values. |
| `QF.OBJ.DELETE(handle)` | TRUE if removed, FALSE if the handle was unknown or stale. |
| `QF.VERSION()` | Version of the add-in. |
| `QF.LASTERROR()` | Reason of the last error. Volatile. |

### Example

| | A | B |
| --- | --- | --- |
| 1 | `=QF.CURVE.FLAT("USD", DATE(2025,1,15), 0.04, "ACT/365F")` | handle, e.g. `QF.Curve:USD#1` |
| 2 | `=QF.CURVE.DF(A1, DATE(2026,1,15))` | 0.9608 |
| 3 | `=QF.BS.PRICE("C", 100, 100, 1, 0.05, 0, 0.2)` | 10.4506 |
| 4 | `=QF.IMPLIEDVOL(B3, "C", 100, 100, 1, 0.05, 0)` | 0.2 |
| 5 | `=QF.BS.GREEKS("C", 100, 100, 1, 0.05, 0, 0.2, "HEADER")` | a 2x10 block |
| 6 | `=QF.DATE.ADVANCE(DATE(2025,12,24), "1B", "TARGET")` | 29-Dec-2025 (format as a date) |

## Sample workbooks

[`samples/excel`](../samples/excel) holds five ready-made workbooks that show the add-in on realistic tasks. Download one, load the
`.xll` (see *Install*), open the workbook and, if cells show `#NAME?`, press **Ctrl+Alt+F9**. Without the add-in the formulas
cannot be calculated: the cells show `#NAME?`. Each workbook starts with an **About** sheet (purpose, how to load the add-in, a
live "Add-in status" line and the last `QF.LASTERROR()`), has its inputs on a pale yellow background with blue text, uses named
ranges (`Spot`, `Strike`, `Exposures`, `Returns`...) in its formulas, and keeps results in black on white.

| Workbook | What it shows | Functions |
| --- | --- | --- |
| `01-Pricing-and-Greeks.xlsx` | Inputs (S, K, T, r, q, vol, call or put), Black-Scholes and Black-76 prices, put-call parity check, the ten greeks in one call, an implied-vol round trip, and a 13-strike ladder with charts of price and delta against strike. | `QF.BS.PRICE`, `QF.BS.GREEKS`, `QF.BLACK76.PRICE`, `QF.IMPLIEDVOL`, `QF.IMPLIEDVOL.BLACK76` |
| `02-Implied-Volatility-Smile.xlsx` | A strip of option prices (static, computed from a known SVI smile) turned into implied vols, a raw-SVI fit, the fitted vols read back on the strikes, residuals, and a chart of market against fit. | `QF.IMPLIEDVOL`, `QF.SVI.FIT`, `QF.SURFACE.VOL` |
| `03-Yield-Curve.xlsx` | Euro-style deposit, FRA and swap quotes bootstrapped into a curve; dates from `QF.DATE.ADVANCE`, year fractions, discount factors, zero and forward rates on a 20-point grid, compared with a flat curve; charts of the zero/forward curves and of the discount factors. | `QF.CURVE.BOOTSTRAP`, `QF.CURVE.FLAT`, `QF.CURVE.DF`, `QF.CURVE.ZERO`, `QF.DATE.ADVANCE`, `QF.YEARFRAC` |
| `04-Portfolio-Risk.xlsx` | 250 days of synthetic returns for five assets and a book of exposures: sample, EWMA and Ledoit-Wolf covariances side by side, 99% VaR and ES by historical, parametric and Cornish-Fisher methods, and the parametric and historical VaR decompositions. | `QF.COV`, `QF.VAR`, `QF.ES`, `QF.VAR.DECOMP` |
| `05-Monte-Carlo-VaR.xlsx` | A covariance kept by the add-in (`QF.COV.OBJ`), spots and vols, a book of shares and options, and `QF.MCVAR` three times side by side (PHILOX seed, PHILOX seed + 1, SOBOL), with notes on the `#N/A` while computing and on determinism. | `QF.COV.OBJ`, `QF.MCVAR` |

The market data is illustrative: the returns are a fixed-seed multivariate Student-t with realistic volatilities and correlations,
the smile prices come from a known SVI smile with a small wiggle, the curve quotes are euro levels of January 2025.
Object names are global to the Excel session (`SAMPLE02_SVI`, `SAMPLE03_EUR`, `SAMPLE03_FLAT`, `SAMPLE05_COV`), so do not open two copies of a workbook at once: the second would
replace the objects of the first and its handles would go stale (`#REF!`).

### How the formulas are written

- **Plain function names.** The functions are XLL functions, written in the file as `QF.BS.PRICE(...)`, with no `_xlfn.` prefix
  (that prefix is for functions Excel itself added after 2007) and no `=` in the stored formula text. Only functions of Excel 2007 or earlier appear
  next to them (`SUMPRODUCT`, `STDEV`, `SKEW`, `MMULT`, `IFERROR`...), so that nothing needs a prefix.
- **Blocks are legacy array formulas.** `QF.BS.GREEKS(..., "HEADER")`, `QF.COV`, `QF.VAR.DECOMP` and `QF.MCVAR` return a block.
  The workbooks write each one as a Ctrl+Shift+Enter array formula over exactly the block the function returns (2x10, 5x5, 7x7
  and 3x3), shown as `{=QF.COV(...)}` in Excel. ClosedXML cannot write the cell metadata that makes a formula a dynamic-array one
  (what `Range.Formula2` produces in the smoke test), and a plain formula would be opened by Excel 365 as an implicit-intersection
  formula (`=@QF.COV(...)`) that returns only the first cell. A legacy array formula works in every version of Excel; to change the
  size of a block, select it, edit the formula and confirm with Ctrl+Shift+Enter, or in Excel 365 delete it and type the formula in a single cell.
- **Charts** are scatter charts added to the package by the generator (ClosedXML has no chart API). They carry only the ranges they
  plot, so they stay empty until the add-in has calculated the cells they read.
- The workbooks are written without cached values and are flagged to recalculate fully when opened.

### Regenerating

The workbooks are generated by `tools/InFine.QuantFinance.ExcelSamples` (ClosedXML) and committed, so that they can be downloaded
directly. After changing the generator, regenerate and commit them:

```sh
dotnet run --project tools/InFine.QuantFinance.ExcelSamples
```

The output is byte-for-byte reproducible. `SampleWorkbookTests` (in `InFine.QuantFinance.Excel.Core.Tests`, runs on any OS) checks that
each formula calls a function declared by the shim with a valid number of arguments (the registry is read from the `[ExcelFunction]`
declarations of the shim sources, since the net8.0-windows shim cannot be referenced from a net8.0 test project), that every cell a
formula or a chart reads is filled in, that the package is valid OpenXML, that the inputs of each workbook give sensible results through the Core implementations of the functions
(for the first one, against independent Black-Scholes calls), and that the committed files are what the generator writes.
It does not run Excel: the formulas are not evaluated by a spreadsheet. To check the workbooks in a real Excel, run the smoke test with
`-SampleWorkbooks` (see below).

## Design and testing

The add-in is split in two so that it is testable everywhere:

- `InFine.QuantFinance.Excel.Core` (`net8.0`, no Excel-DNA reference) holds everything behind the functions: argument
  coercion from Excel-like values (`Arg`, `ExcelDates`, `Names`), the thread-safe object cache (`ObjectCache`) and the
  function implementations (`QuantFunctions`), which return plain values, `object[,]` arrays or an `XlError`
  sentinel and never throw. It is covered by `InFine.QuantFinance.Excel.Core.Tests`, which runs on any OS: each
  function is checked against a direct call of the library and against independent references (textbook prices, finite
  differences, round trips, repricing of the bootstrap inputs).
- `InFine.QuantFinance.Excel` (`net8.0-windows`, Excel-DNA) is a shim: one `[ExcelFunction]` wrapper per function with
  its help text, and `Xl`, which converts omitted/empty arguments to `null` and `ExcelError` to and from `XlError`.
  All functions are declared thread-safe, except the risk functions of `RiskFunctions.cs`, which run through `XlAsync`
  (see *Asynchronous execution*): the Core keeps no mutable state outside the locked object cache.

The shim compiles and packs on every OS, but nothing in CI runs Excel. On Windows with 64-bit Excel,
`InFine.QuantFinance.Excel/smoke/Invoke-ExcelSmokeTest.ps1` loads the packed `.xll` into a private, hidden Excel
through COM and checks every `QF.*` function against independent values, omitted optional arguments, handle versioning and `#REF!`, reopening a saved workbook then Ctrl+Alt+F9, multi-threaded recalculation, `QF.LASTERROR`, and the
`#N/A`-then-result, join, restart and cancellation behaviour of `QF.MCVAR` (cancellation is measured on the CPU time of
the Excel process). It prints a table and writes a JSON file; run it after changing the shim:

```sh
powershell -NoProfile -ExecutionPolicy Bypass -File InFine.QuantFinance.Excel/smoke/Invoke-ExcelSmokeTest.ps1
```

With `-SampleWorkbooks` it also opens each workbook of `samples/excel` read-only, recalculates it in full, waits for the asynchronous
cells to finish and fails if any cell shows an error or the About sheet does not report the add-in as loaded; `-SamplesOnly` runs just that
part (`-SamplesDirectory` and `-SampleTimeoutSeconds` adjust the folder and the wait per workbook).

The *Insert Function* dialog cannot be driven through COM; its category and descriptions were checked once by hand
(see [verification-windows-gpu.md](verification-windows-gpu.md)). Function names are a public
contract of existing workbooks; add new ones rather than renaming.

The Core methods that can run long take a `CancellationToken` and throw `OperationCanceledException` when it is
cancelled (the only exception a Core method lets out); `RiskCalls.Key` builds the stable key that identifies an
asynchronous call.

To add a function, implement it in `QuantFunctions` with a test, then add its wrapper in
`QuantFinanceFunctions.cs`.
