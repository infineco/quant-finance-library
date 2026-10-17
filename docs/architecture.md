# Architecture

This document describes how the library is organised and why. Sections that describe a direction rather than the
current state are marked as such.

## Why the code looks the way it does

The goal is to write a pricer once in C# and run it on the .NET runtime, on a CUDA GPU or on AVX units
through Hybridizer. Kernels on those targets cannot afford virtual calls, allocation or exceptions, which shapes
the design: behaviour is composed through generics, not through interface dispatch.

## Concepts pattern

A *concept* is an interface marked `[HybridTemplateConcept]`, for example `ISpot`, `ILogSpot` (in
`InFine.QuantFinance.Models/Concepts`) and the premium concept in `Payoffs/Concepts`. Algorithms are generic
classes constrained on concepts:

```csharp
public class LongstaffSchwartz<Spot, Discounting, CashFlowSource, Regressor, ParallelExecutor>
    where Spot : ISpot
    where Discounting : IDiscounting
    where CashFlowSource : ICashFlowSource
    where Regressor : IRegressor<...>
    where ParallelExecutor : IParallelExecutor
```

Each concrete combination used on a device is declared with `[HybridRegisterTemplate(Specialize = typeof(...))]`
on the generic class. Hybridizer then emits a fully specialised kernel for that combination, so calls through the
interfaces become direct (inlinable) calls instead of virtual calls. On plain .NET the same generic code runs
as ordinary C#.

Consequence: a combination that is not registered is not available on the device. Adding a model, payoff or
executor means adding the registrations that use it.

## Executors

`IParallelExecutor` (in `InFine.QuantFinance.Scheduling`) abstracts how a loop and a reduction are run:

- `For(from, to, Action<alignedindex>)` runs a functor over a range of simulations.
- `Reduce(alignedstorage_double, from, to, reduce, neutral)` reduces an array.

Implementations present in the repository: `SequentialExecutor`, `SingleBlockParallelExecutor`
and `MultiBlockParallelExecutor`, together with serial, parallel and CUDA single-block reductions. Executors are
passed as a generic argument so that the choice is resolved at specialisation time. Their `For` and `Reduce`
are documented as callable from a single thread only.

## alignedindex and VectorUnit

`alignedindex` is a Hybridizer type for a loop index that may be vectorised: on AVX it stands for a vector of
lanes, on CUDA for a thread. Concept indexers have both an `int` and an `alignedindex` overload, and
`[ReturnTypeInference(... VectorTransitive)]` tells Hybridizer that the result is a vector when the index is.
Loops are written as `for (alignedindex i = VectorUnit.ID + from; i < to; i += VectorUnit.Count)`, and
`VectorUnit.Barrier()` synchronises the units of a block. These types come from
`Hybridizer.Runtime.CUDAImports`, which is why code using them does not load on arm64 hosts.

## Memory layout

Per-path data is stored as a structure of arrays in a flat buffer: for a spot matrix, the element for
simulation `simId` at date `timeIndex` is at

```
index = simId + simCount * timeIndex
```

Consecutive simulations are contiguous, which gives coalesced access on GPU and unit-stride loads on AVX. See
`ArrayDoubleSpot` in `InFine.QuantFinance.Models/Concepts/ILogSpot.cs`, which uses an `alignedstorage_double`
buffer.

## Monte Carlo pipeline

The intended flow of a path-dependent price:

1. Random numbers: generators in `InFine.QuantFinance.Random` (MRG32K3A, Philox, plus MKL and cuRAND wrappers).
2. Brownian increments: `IBrownian` implementations in `Models/Concepts`.
3. Diffusion: `EquityDiffusion` and `BlackScholesMerton` fill the spot matrix over a schedule; a Heston model
   also exists.
4. Payoff: payoff types (for example `EuropeanCallOption`, `VarianceSwap`) read the spot and write a premium
   per simulation.
5. Reduction: an executor reduces per-simulation premiums to a price.

Not all stages are wired end to end for every model and target; check the tests of each project for what is
currently exercised.

## Host market model and compilation to kernels

Users describe markets and products with dates; kernels only see arrays of doubles on one clock. The two sides
are kept apart by project boundaries:

- `InFine.QuantFinance.Market` is the host model: dates, holiday calendars, day counters, schedules, curve
  instruments and bootstrapped curves. Idiomatic C# (exceptions, allocation, LINQ); it does not reference
  Hybridizer, so it loads on any host.
- `InFine.QuantFinance.Numerics` holds the allocation-free numerical kernels both sides share: the interpolation
  kernels and `DiscountCurveKernels`, which evaluate a curve stored as flat arrays. It references nothing.
  Hybridizer transforms these methods because `[Kernel]` code calls them; they need no attribute.
- `InFine.QuantFinance.Models` holds the kernel concepts and references `Numerics`, not `Market`.

Compilation happens on the host, once per pricing:

1. `SimulationGridBuilder` turns a valuation date, a day counter, the product event dates and an optional maximum
   step into a `SimulationGrid`: pillar times from 0, event times exact, gaps split into equal refinement steps, and
   a map from each event date to its pillar index. `new PrecalculatedSchedule(grid.ToTimesArray())` is the kernel
   schedule; `grid.PillarIndex(date)` is the time index a payoff reads.
2. `MarketCompiler.CompileDiscountCurve(curve, grid)` turns an `IYieldCurve` into a `CompiledDiscountCurve`. An
   interpolated curve is copied node for node and evaluated through the same kernels, so the device reproduces the
   host curve exactly; other curves are sampled at the pillars.
3. `CurveDiscounting` and `CurveRate` (in `Models/Concepts`) read the compiled curve: discount factors precomputed
   on the pillars and interpolated at any other time, and as drift the forward over each step,
   `r_k = -ln(P(t_{k+1}) / P(t_k)) / dt_k`, which makes the discounted spot a martingale on the grid.

## Closed-form pricing

`InFine.QuantFinance.ClosedForm` holds the analytic formulas used as references and as risk engines:

- `NormalDistribution`: `Cdf` (Cody erfc, full relative accuracy in the tails), `Pdf` and the AS241 `InverseCdf`.
- `Black76Kernels`: Black (1976) on a forward, the core of everything else. `NormalisedPrice(sign, F, K, v)` with
  `v = sigma sqrt(T)` is the form implied-volatility solvers iterate on; `Price` adds the discount factor and
  `Greeks` returns price, delta, gamma, vega, theta, rho, vanna, volga and dual delta as `out` parameters.
- `BlackScholesKernels`: generalised Black-Scholes-Merton with a dividend yield, expressed as a Black-76 option on
  `F = S exp((r - q) T)`; its Greeks follow from the Black-76 ones by the chain rule, and it adds dividend rho.
- `BlackScholes` and `Black76`: host API taking an `OptionType`, validating arguments and returning an
  `OptionGreeks` struct (conventions for theta, rho and the others are documented on that type).

The kernels take the option sign as an `int` (+1 call, -1 put), allocate nothing and never throw. Degenerate inputs
(`T = 0`, `sigma = 0`) give the intrinsic value of the forward instead of NaN. `PriceAt` / `GreeksAt` evaluate one
element of a batch and `PriceArray` / `GreeksArray` are the `[EntryPoint]` loops over them; the per-element methods
use plain arrays, so they can be tested without the Hybridizer runtime. `BlackScholesMertonPrimitive.CallPut`
remains and delegates to the same kernel.

## Algorithmic differentiation (tangent mode)

`InFine.QuantFinance.Arithmetic` (no dependencies) holds forward-mode dual numbers in `AutoDiff`: `Dual` (value and one
tangent) and `Dual4` (value and four tangents in named fields, so a pass yields four first-order sensitivities). Both are
readonly structs with operators and static functions only (arithmetic mixed with `double`, `Exp`, `Log`, `Sqrt`, `Pow`,
`Abs`, `Max`, `Min`, `Sin`, `Cos`), so they stay in registers in a kernel. `Dual4` uses scalar fields rather than a
`fixed` buffer: a fixed buffer needs `unsafe`, cannot be readonly and is indexed through a pointer, which on GPU sends it to
local memory.

Conventions:

- Branches follow the primal: comparisons compare values and return `bool`, so code keeps the control flow it has on
  doubles and the derivative is that of the branch taken.
- Kinks take a one-sided derivative chosen by argument order: `Max`/`Min` return their first argument on a tie and `Abs`
  differentiates `+x` at 0. `Max(S - K, 0)` at `S = K` therefore has derivative 1 (exercised), `Max(0, S - K)` has 0.
- The chain rule keeps an exact zero on a direction whose tangent is zero, even where the derivative is infinite
  (`Sqrt(0)`), so constants never become NaN.
- First order only: gamma and other second-order Greeks need nested duals or, for Monte Carlo payoffs with kinks,
  likelihood-ratio weights; neither is implemented.

Dual counterparts of pricing code sit next to the double code and repeat its operations in the same order, so the value
part is the double result: `DualNormalDistribution` (`Cdf`, `Pdf`, `Erfc`, primal from `NormalDistribution`) and
`BlackScholesDual` (Black-76 and generalised Black-Scholes) in ClosedForm, which references Arithmetic;
`DualLogNormalMath` (the exact log-normal step, a `Dual4` path from given draws with constant or per-step parameters, and
`DiffusePath`, the dual correlated multi-asset path) in `Models/Diffusion`; the `Dual4` overloads of `AsianMath`,
`MultiAssetMath` (basket and best-/worst-of) and `PathwiseGreeks` (discounted European and Asian premiums of a dual path)
in `Payoffs/Equity`. Seeding spot, volatility, rate and dividend yield on the four directions of a `Dual4` path gives the
pathwise Monte Carlo delta, vega, rho and dividend rho in the pricing pass. The pathwise estimator needs a payoff that is
Lipschitz in the inputs (calls, puts, Asians); digitals and barriers need likelihood-ratio or smoothing instead.

The former generic `Real<T, Ops>` / `Condition<T>` abstraction was removed: `Condition<T>` boxed its result through
`object`, which no kernel target supports, and nothing in the solution used it.

## Adjoint (reverse-mode) Greeks

A tangent pass gives four sensitivities, so the cost of all of them grows with the number of inputs. The adjoint gives
every first-order sensitivity of a Monte Carlo price in one backward sweep per path, at a small multiple of the cost of
one valuation whatever the number of inputs (Giles and Glasserman, "Smoking adjoints", Risk, 2006). The adjoints are
written by hand, without a tape, so they stay kernel code: static functions on plain arrays with offsets, no allocation,
no exception, the caller passing the scratch (one range per thread on a device).

Per path: the forward sweep of the model stores what the backward sweep needs and returns the discount factor; a payoff
adjoint returns the discounted payoff `P` and writes `dP/dx` for every log-spot `x` it reads; the backward sweep runs the
steps in reverse, carrying the adjoint of the state, and adds `dP/d(input)` to a gradient accumulator. A kernel lets each
thread accumulate its paths in place and the host divides by the path count.

- Models (`Models/Diffusion`):
  - `LogNormalAdjoint`: one asset with a term structure (volatility, rate and dividend yield per step, unequal steps), so
    the gradient is the delta and the bucketed vegas, rhos and dividend sensitivities: `[0]` spot, `[1 + k]` sigma_k,
    `[1 + n + k]` r_k, `[1 + 2n + k]` q_k. The log-normal step has a unit Jacobian in the state, so the backward sweep reads
    the draws, not the path: `xbar_k = xbar_{k+1} + dP/dx_k`, `dP/dsigma_k += xbar_{k+1} (sqrt(dt_k) z_k - sigma_k dt_k)`,
    `dP/dr_k += (xbar_{k+1} - P) dt_k` (drift and discount factor).
  - `CorrelatedLogNormalAdjoint`: the D-asset diffusion of `CorrelatedLogNormalDiffusion`; the forward sweep stores the
    correlated draws `y = L z`, so the backward sweep is O(D) per step (no Cholesky product). Gradient: D deltas, D vegas,
    D dividend sensitivities, then the rate of each step. The correlation is not differentiated (that needs the adjoint of
    the Cholesky factorisation).
  - `HestonAdjoint`: the full-truncation Euler scheme of `HestonStep`; the Jacobian in `(x, v)` is not the identity, so the
    backward sweep carries both adjoints and reads the stored variances. Gradient: delta, v_0, kappa, theta, xi, rho, rate.
    The kink of `v^+ = max(v, 0)` is differentiated with derivative `1{v > 0}`: at `v = 0` the truncated variance is a
    constant, the convention of duals written `Dual.Max(0.0, v)`, and the only one that stays finite (the other side gives
    `sqrt(v^+)` an infinite derivative).
- Payoffs (`Payoffs/Equity/AdjointGreeks`): European call/put and arithmetic average-price Asian on one asset, arithmetic
  basket and best-/worst-of on D assets, all on log paths in the multi-asset layout and paid at the last pillar.
- Host (`Payoffs/Equity/AdjointMonteCarlo`): `LogNormal`, `CorrelatedLogNormal` and `Heston` take an `AdjointPayoff` handle,
  the model inputs, a path count and a callback that fills the draws of path p (so a bumped run can reuse them), validate
  everything and return a `GradientEstimate`: price and gradient means with their standard errors.

Validity is that of the pathwise estimator: the discounted payoff must be Lipschitz in the inputs. Calls, puts, Asians and
baskets qualify; so do worst-of and best-of options, a minimum or maximum of smooth functions being Lipschitz and
differentiable except where two performances tie, which has probability zero unless two assets are perfectly correlated
with identical parameters. Kinks follow the dual conventions (an intrinsic value of exactly 0 is differentiated as
exercised, a tie goes to the asset seen first), so the adjoint equals the tangent of `Dual4` code on every path, kinks
included. Under Heston the derivative of `sqrt(v^+)` is unbounded near 0, so with the Feller condition violated the samples
of the volatility-parameter sensitivities are heavy-tailed.

Tests (`Payoffs.Tests/AdjointGreeksTests`, `HestonAdjointTests`, Philox paths): adjoint = tangent (each input seeded in turn
on `Dual4` or `Dual` over the same draws) on every path and every input, to 1e-10 (observed 3e-14 for the term structure,
7e-15 for baskets and best-/worst-of, 1e-13 for Heston including about 1000 truncated steps); adjoint = central
bump-and-revalue with common random numbers on smooth (softplus) payoffs to 1e-7 (observed 7e-10 or better); bucketed
vegas, rhos and dividend sensitivities sum to the parallel tangent Greeks of the constant model (1e-14); term-structure
European price, delta and every bucket within 3 standard errors of Black-Scholes with the effective parameters; two-asset
worst-of and exchange Greeks within 3 standard errors of Stulz and Margrabe.

Cost (`benchmarks`, `AdjointGreeksBenchmarks`): arithmetic basket call on D = 20 correlated assets, 50 steps, 1000 paths
per operation, pre-generated draws; valuation is the host reference of the diffusion kernel and the basket payoff, the
adjoint returns the price and all 3 D + 50 = 110 sensitivities, tangent mode seeds four inputs per `Dual4` pass and
recomputes the path for each pass. BenchmarkDotNet 0.15.8 short job, in-process, .NET 8.0.28 Arm64 RyuJIT, Apple M5 Pro:

| Method | Sensitivities | Mean | Ratio |
| --- | ---: | ---: | ---: |
| Valuation | 0 | 4.00 ms | 1.00 |
| Adjoint | 110 | 5.46 ms | 1.36 |
| Tangent, 1 `Dual4` pass | 4 | 6.17 ms | 1.54 |
| Tangent, 10 passes (deltas and vegas) | 40 | 61.80 ms | 15.44 |
| Tangent, 28 passes (all inputs) | 110 | 173.06 ms | 43.23 |

The adjoint costs 1.36 valuations for 110 sensitivities; tangent mode costs about 1.5 valuations per four inputs, so it
passes the adjoint from the fifth input on and grows linearly. On these paths the two gradients agree to 7e-14. No method
allocates (measured with `GC.GetAllocatedBytesForCurrentThread`; the 120 and 320 bytes BenchmarkDotNet reports for the two
longest runs are harness overhead).

Not done: second-order Greeks, correlation sensitivities, likelihood-ratio weights for discontinuous payoffs, and the
device kernels themselves (the cores are kernel-compatible but no `[EntryPoint]` wraps them yet).

## Volatility surfaces

`InFine.QuantFinance.Volatility` is host-only (exceptions, allocation) and builds implied-volatility surfaces in total
variance `w(t, k) = sigma^2 t` on log-forward moneyness `k = ln(K / F(t))`, following Gatheral and Jacquier,
"Arbitrage-free SVI volatility surfaces" (2014):

- `SviRawParameters`: a raw SVI slice with its derivatives in `k`; `SsviParameters` / `SsviSlice`: SSVI with the
  power-law `phi`, and the paper's sufficient no-arbitrage conditions (Theorems 4.1, 4.2 and eq. 4.5).
- `ArbitrageChecks`: the butterfly function `g(k)` and implied density, grid checks for butterfly and calendar
  arbitrage on any `ITotalVarianceSlice`.
- `SviCalibrator`: one expiry, quasi-explicit start (exact constrained linear least squares on an `(m, sigma)` grid)
  then a bounded Levenberg-Marquardt polish in a reparametrisation where every SVI constraint, Lee's wing bound
  included, is a box. `SsviCalibrator`: all expiries at once, with a non-decreasing ATM curve and, by default, the
  eq. 4.5 region, so the result is free of static arbitrage by construction. Both use `Numerics.LevenbergMarquardt`.
- `VolatilitySurface`: `Slice(t)`, `TotalVariance(t, k)`, `ImpliedVolatility(t, K)` and Black-76 prices through
  `ClosedForm.Black76`, on an `IForwardCurve` (constant, or spot with `Market` discount and dividend curves).
  `SviVolatilitySurface` interpolates linearly in total variance at fixed `k` between pillars;
  `SsviVolatilitySurface` interpolates the ATM total variance `theta`, so every expiry is an exact SSVI slice.
  Before the first and after the last pillar the SVI surface keeps the implied volatility flat at fixed `k`
  (`w` proportional to `t`) and the SSVI surface keeps the ATM implied volatility flat (`theta` proportional to `t`).

## Portfolio risk: covariance estimation

`InFine.QuantFinance.Risk` is host-side (apart from the full-revaluation kernels below) and works on a T x N matrix of returns (`double[,]`, rows = dates oldest first).
`Returns` builds simple or log returns from prices; `Covariance` has the sample covariance (unbiased or maximum
likelihood), correlation/volatility conversions and annualisation; `EwmaCovariance` is the RiskMetrics recursion
(`lambda = 0.94`, zero-mean, started at the first outer product or seeded from a warm-up window) usable one return
vector at a time or in batch with identical results; `LedoitWolf` shrinks the sample covariance towards constant
correlation (Ledoit and Wolf, "Honey, I shrunk the sample covariance matrix", 2004) or towards a scaled identity
(Ledoit and Wolf, JMVA 2004) and returns the optimal intensity with the sample and target matrices.

The dense linear algebra they need lives in `Numerics` and is host-only too (not for kernels): `Matrix` helpers,
`SymmetricEigen` (cyclic Jacobi, O(n^3) per sweep, intended for n up to a few hundred), `Cholesky` (plain with failure
detection, pivoted rank-revealing, and `Robust`, which repairs an indefinite input first), and `NearestMatrix`
(nearest positive semi-definite matrix by eigenvalue clipping; nearest correlation matrix by Higham's alternating
projections with Dykstra's correction, 2002).

## Portfolio risk: Monte Carlo VaR by full revaluation

`FullRevaluationEngine` (in `InFine.QuantFinance.Risk`) simulates M correlated market scenarios over a horizon and
reprices every position of a portfolio in each of them; VaR and ES are then `HistoricalRisk` measures of the simulated
P&L, and `MonteCarloVarResult.Decompose` is `HistoricalRisk.Decompose` over the M x N per-unit P&L matrix with the
quantities as positions (P&L is linear in the quantities, so the Euler allocation holds for options too).

- Risk-factor model: F factors with a level and a kind. `LogNormal` (spots): `X = X0 exp(x - v/2)`, a martingale;
  `SimpleReturn`: `X = X0 (1 + x)`, which makes a linear book exactly delta-normal; `Additive` (implied volatilities,
  a flat rate): `X = X0 + x`. The moves `x` are normal with covariance `Horizon * covariance`, the per-period covariance
  of the moves in the units their kind implies (sample, EWMA or Ledoit-Wolf estimate). Zero-variance factors are held constant, which
  is how a fixed volatility or rate is expressed; the others are factorised by `Cholesky.Robust`.
- Instruments: linear spot positions and Black-Scholes-Merton European options (vol and rate read from factors,
  constant dividend yield), with expiries optionally shortened by `TimeDecay` years so that the P&L includes theta.
- Kernel layer (`FullRevaluationKernels`): structure of arrays, one unit of work per scenario. `GenerateScenario`
  draws `z_j` from the Philox counter (seed, path = scenario, stream; words 2j and 2j+1, inverse CDF) or from scrambled
  Sobol point `s`, dimension `j`, correlates them in place with the row-major lower Cholesky factor and maps them to
  levels in a per-worker scratch row; `RevalueScenario` loops over the instruments with `BlackScholesKernels.Price` and
  writes `pnl[s]` and optionally `instrumentPnl[i * M + s]`. The cores take plain arrays, so the host runs them
  directly on any machine; `SimulateScenarios` is the grid-stride `[EntryPoint]` (one scratch row per thread).
- Execution: the host cuts the scenario range into chunks run sequentially or with `Parallel.For`. Every scenario is a
  pure function of its index, so the results are bit-identical for any chunking. `FullRevaluationBatch` exposes the
  flat kernel arguments for a device launch (`HybRunner.Cuda(binary).SetDistrib(grid, block).Wrap(new
  FullRevaluationKernels()).SimulateScenarios(...)`); that path is covered by a `GpuFact` test only.

### Scenario stress tests

`StressTestEngine.Run(portfolio, factors, scenarios)` (same assembly) reprices the book deterministically under named
scenarios with the same kernel function as the Monte Carlo engine (`FullRevaluationEngine.RevalueUnitPnl`), so there is
one pricer. A `StressShock` has a mode (`Relative` x(1+s), `Absolute` +s, `LogReturn` x e^s, `Set` =s) and targets one
factor by name or every factor of a `RiskFactor.Group` ("equity", "vol", "rates"...). A factor-specific shock overrides
the group shock for that factor; duplicates and unknown factor names throw, a group without members is a no-op.
`StressScenario.TimeDecayYears` shortens option expiries (no shock + decay = theta; nothing = exactly 0). Attribution is
one-at-a-time: each shock alone (with the decay kept) minus the decay-only P&L, plus a time-decay line, plus an
`Interaction` remainder so that contributions and interaction sum to the total. `StressScenarioLibrary` holds a few
generic, explicitly illustrative scenarios (not calibrated to historical dates). `ReverseStress` scales a shock direction
(64-step scan then bisection) until a loss threshold is hit.

## Portfolio construction: risk parity and risk budgeting

`InFine.QuantFinance.Portfolio` is host-side and builds on `Risk` (covariances) and `Numerics` (Cholesky).
`RiskParityOptimizer.Solve(covariance, budgets)` returns the long-only weights whose relative risk contributions equal
the budgets (`EqualRiskContribution` for `b_i = 1/n`), through the convex formulation of Spinu (2013):
`min 1/2 y'Sigma y - sum b_i ln y_i` over `y > 0`, then `w = y / sum y`. The default is damped Newton (Cholesky of
`Sigma + diag(b/y^2)`, backtracking that keeps `y > 0`): 5 to 15 iterations whatever the size and conditioning.
Cyclical coordinate descent (Griveau-Billion, Richard and Roncalli, 2013) is implemented too, as the fallback when Newton
stalls and as an independent cross-check; it is O(n^2) per sweep but converges linearly and slowly for highly correlated
assets. The weights do not depend on the scale of the covariance. A covariance that is not positive semi-definite is rejected.

`RiskBudgeting` holds the naive baselines (`InverseVolatility`, `InverseVariance`), the contribution report and
volatility targeting. `Contributions` uses the conventions of `ParametricRisk.Decompose`: marginal `(Sigma w)_i / sigma_p`,
absolute `w_i x marginal_i` (sums to `sigma_p`), relative `absolute / sigma_p` (sums to 1; equal to component VaR over total
VaR in the delta-normal decomposition). `VolatilityTargetLeverage` / `ScaleToVolatility` scale weights to a target
volatility (an optional leverage cap), in the same period units as the covariance; financing costs are not modelled.

## Portfolio construction: mean-variance optimisation

`QuadraticProgramSolver` (same assembly) solves dense strictly convex QPs `min 1/2 x'Qx + c'x` subject to `Ax = b`,
`Gx <= h` and bounds, by the Goldfarb-Idnani dual active-set method (1983): start at the unconstrained minimiser, add
the most violated constraint, keep the multipliers dual feasible, update `J = L^-T Q_A` and the triangular `R` with
Givens reflections (O(n^2) per active-set change). It was preferred to ADMM (OSQP) because it ends on an exact KKT point,
needs no phase 1, and detects infeasibility as an unbounded dual step; the final `x` and multipliers are recomputed from
the factorisation of the final active set. The result carries multipliers in the convention
`Qx + c + A'y + G'z - zl + zu = 0`, the active set, the iteration count and a status (`Optimal`, `Infeasible`,
`NotConvex`, `MaxIterationsReached`). A semi-definite `Q` (sample covariance with more assets than dates) gets a ridge
`1e-10 max(diag Q)`, increased tenfold until the Cholesky pivots clear `1e-12 max(diag Q)`, and reported; linear
programmes (`Q = 0`) are out of scope.

`MeanVarianceOptimizer(mu, Sigma, PortfolioConstraints)` builds on it. Constraints: a budget (default fully invested),
bounds (`LongOnly`, `Box`, `ActiveBounds` around a benchmark) and `LinearConstraint`s (group / sector caps, any
`L <= a'w <= U`). Requests: `MinimumVariance`, `MinimumVarianceForTargetReturn`, `MaximumUtility(lambda, benchmark?)`
(`max mu'a - lambda/2 a'Sigma a` on weights or active weights), `MaximumReturn`, `MaximumReturnForTargetVariance`,
`MinimumTrackingError`, `MaximumActiveReturnForTrackingError`, `MaximumSharpeRatio(r_f)` and `EfficientFrontier(k)`
(k target returns equally spaced from the minimum-variance to the maximum-return portfolio). Three techniques recur:

- The utility is solved as `min 1/2 a'Sigma a - t mu'a` with `t = 1/lambda`; its solution path is piecewise affine in
  `t`. The maximum return (a linear programme) is the last, constant piece, found by walking `t` up by decades until two
  solutions share active set and weights; among tied maximisers it is the one of minimum variance.
- Risk targets (variance or tracking error) bracket `t`, bisect until both ends share an active set, then solve the
  quadratic `variance(t) = target` on that affine piece, so the target is met to rounding.
- The maximum Sharpe ratio uses the homogenisation `y = kappa w`: `min y'Sigma y` with `(mu - r_f)'y = 1`, every
  constraint rewritten with `kappa = 1'y / budget` on the right-hand side, `kappa >= 0`, then `w = y / kappa`. It is exact
  for linear constraints when some feasible portfolio beats `r_f` (otherwise `Infeasible`).

Infeasible constraints, unattainable targets and an unbounded return are statuses with a message, not exceptions;
malformed input (dimensions, non-finite or asymmetric data) throws.

## Time series: performance statistics and GARCH

`InFine.QuantFinance.TimeSeries` is host-side and builds on `Risk` (`Returns` for returns from prices, `HistoricalRisk` for
VaR / ES) and `Numerics` (BFGS, finite differences, Cholesky).

`PerformanceStatistics` works on simple periodic returns and takes the annualisation factor explicitly (`Periodicity` holds the
usual constants). Geometric annualised return `prod(1+r)^(k/n) - 1`; volatility `sd sqrt(k)`; Sharpe `mean(r - rf/k) / sd sqrt(k)`;
Sortino with the target downside deviation `sqrt(mean(min(r - mar/k, 0)^2))` over all observations; drawdown `1 - W_t / max W_s`
as a non-negative fraction, with peak, trough and recovery positions (`MaxDrawdown` on returns, `MaxDrawdownOfLevels` on prices);
Calmar, hit ratio, rolling mean / volatility / Sharpe (NaN until the window is full, aligned on the last observation), and a
`HistoricalVarEs` passthrough. Variance, skewness and excess kurtosis come in two conventions, `Population` and `SampleAdjusted`
(divisor `n-1`, Fisher-Pearson `G1`, `G2`: the Excel / pandas values, the default).

`Garch11` fits `sigma^2_t = omega + alpha eps^2_{t-1} + beta sigma^2_{t-1}` by maximum likelihood with normal or unit-variance
Student-t innovations, a constant mean (estimated or zero), optional variance targeting and a choice of `sigma^2_1` (sample
variance or exponentially weighted backcast). The constraints `omega > 0`, `alpha, beta >= 0`, `alpha + beta < 1`, `nu > 2` are removed
by a reparametrisation (`omega = v e^u`, persistence and share through logistic maps, `nu = 2 + e^u`), so the optimiser works in R^n
and cannot leave the feasible set; boundary solutions are limits `u -> +/-infinity` and are reported by `AtBoundary`. The optimiser is
`Numerics.Bfgs`, a BFGS quasi-Newton method with a strong-Wolfe line search and central-difference gradients, started from three
(alpha, beta) points; it was chosen over Levenberg-Marquardt (a likelihood is not a sum of squares) and Nelder-Mead (slow beyond a few
parameters, no gradient test). Standard errors invert the central-difference Hessian of the log-likelihood (`Numerics.FiniteDifferences`,
steps relative to each parameter); the Bollerslev-Wooldridge sandwich is available. Forecasts use `v + (alpha+beta)^(h-1)(sigma^2_{T+1} - v)`
and the annualised volatility term structure averages them in closed form; `Simulate` draws seeded paths from its own xoshiro256**
generator so that results do not depend on the .NET version. The log-likelihood is checked against a stand-alone Python implementation
(`TimeSeries.Tests/reference/garch_loglik.py`).

## Backtesting

`InFine.QuantFinance.Backtesting` is host-side apart from `BacktestKernels` and builds on `Portfolio`, `Risk`, `TimeSeries` and
`Random` (Philox).

- Data: `ReturnPanel` holds T x N simple returns with the end date of each period (`DateOnly`) and an optional per-period cash
  return; `FromPrices` builds it from (T+1) x N prices. It is immutable and copies its inputs.
- No look-ahead: the decision for period `t` is taken at the close of period `t - 1` and earns row `t`. An `IStrategy` receives a
  `StrategyContext` whose `ReturnWindow` holds rows `0 .. t-1` only; every accessor (`[t, i]`, `Date`, `CashReturn`, `Tail`)
  checks the row and throws `ArgumentOutOfRangeException` beyond it, and the panel itself is not reachable from the window.
  A strategy returns target weights or null (hold). Built-ins: buy-and-hold, constant mix, inverse volatility, equal risk
  contribution / risk budgets (`RiskParityOptimizer`) and minimum variance (`MeanVarianceOptimizer`, long-only by default), the
  last three on a rolling `CovarianceEstimator` (sample, Ledoit-Wolf constant correlation or EWMA over the last `Lookback` rows).
- Engine: `RebalanceSchedule` (every k periods from the start, explicit period indices, or calendar: after the last observation
  of each week / month / quarter / year). Holdings are kept in money: a target is traded against the pre-trade value, purchases,
  sales and costs (`bps x notional + fixed x trades`, trades under a relative tolerance skipped) go through cash, so the cash
  residual absorbs the costs; holdings drift between rebalances, cash earns the cash return (negative cash borrows at it), an
  optional cap scales targets down to a gross exposure. The result has values, net returns, post-trade weights, cash weight,
  gross and net exposure, turnover (traded notional / value) and costs per period; statistics are `PerformanceStatistics` on the
  net returns.
- Bootstrap: stationary block bootstrap (Politis and Romano, 1994) with mean block length L (`L = 1` is the i.i.d. bootstrap);
  row `I_s` of path `p` uses Philox block (seed, s, p, stream), so each path is a pure function of its index and results are
  bit-identical for any chunking. `StationaryBootstrap.RunConstantMix` evaluates constant mix inside the kernel core
  (`BacktestKernels.EvaluateConstantMixPath`: flat arrays, one holdings scratch row per worker, Welford moments and drawdown
  on the fly; `SimulateConstantMixPaths` is the grid-stride `[EntryPoint]`); `StationaryBootstrap.Run` takes any strategy and runs
  the engine on each materialised history, host-only because the optimiser-based strategies allocate and throw. The engine and
  the kernel share `BacktestKernels.Rebalance` / `Grow`, so the two runners agree exactly on wealth, drawdown and turnover.

## Short-rate model: one-factor Hull-White

`dr = (theta(t) - a r) dt + sigma dW`, fitted exactly to an initial curve through the decomposition `r = x + phi`:
`dx = -a x dt + sigma dW`, `x(0) = 0`, `phi(t) = f(0, t) + sigma^2 B(0, t)^2 / 2` with `B(t, T) = (1 - e^{-a (T - t)}) / a`. Neither
the prices nor the simulation differentiate the forward curve (`theta` is only reported).

- Analytic (`ClosedForm`): `HullWhiteKernels` holds the kernel-compatible cores (allocation-free, NaN instead of exceptions):
  `ln P(t, T | x)`, zero-coupon bond options as Black-76 on the forward bond with `sigma_P = B(T, S) sqrt(sigma^2 (1 - e^{-2aT}) / (2a))`,
  caplets/floorlets as bond puts/calls, and coupon-bond options and swaptions by Jamshidian's decomposition, the critical state
  found by a bracketed Brent iteration on the monotone bond price. `HullWhite` is the validating host API on a curve given as
  `P(0, t)` and `f(0, t)` functions (an `IYieldCurve`'s methods, or a `CompiledDiscountCurve`), with caps/floors on period
  arrays; `HullWhiteCalibrator` fits `(a, sigma)` or `sigma` alone to swaption or cap prices (Black-quoted targets available)
  with `Numerics.LevenbergMarquardt` on relative price errors.
- Monte Carlo (`Models`): `CompiledHullWhite` reduces the model on a grid (starting at 0) to flat arrays. `(x(t_{k+1}),
  int_{t_k}^{t_{k+1}} x ds)` given `x(t_k)` is Gaussian with known means, variances and covariance `sigma^2 B(dt)^2 / 2`, so each step
  is drawn exactly from two independent normals through the 2x2 Cholesky factor (`HullWhiteStep`). The discount factor
  `D(0, t_k) = P(0, t_k) exp(-V(t_k)/2 - I(t_k))` has `E[D] = P(0, t_k)` on any grid, a single step included. `HullWhiteModel<Brownian>`
  is the kernel writing `x` and `ln D` per path and pillar; `HullWhiteDiscounting : IDiscounting` and `HullWhiteRate : IRate` read
  them, so existing payoffs and diffusions run on stochastic rates. On a step, `HullWhiteRate` is the average short rate
  `ln(D_k / D_{k+1}) / dt` (the stochastic analogue of `CurveRate`); off the grid both readers condition on the last pillar
  (`D(0, t_k) P(t_k, t | x_k)`, `x_k e^{-a (t - t_k)} + phi(t)`).

## Multi-asset diffusion, baskets and autocallables

Correlated assets are simulated by correlating independent normals with a Cholesky factor, then taking the usual exact log-normal step per asset.

- Correlation (`Models/Concepts`): `CholeskyCorrelation.FromCorrelation` (host) validates a correlation matrix (symmetry, unit diagonal,
  to `Tolerance = 1e-12`) and returns the row-major lower factor `L[i * D + j]`. A positive definite matrix gets the plain Cholesky
  factor; a singular but valid one (perfect correlation, a repeated asset, a rank-deficient factor model) gets the unpivoted
  semi-definite factor, where a zero pivot leaves a zero column, so two perfectly correlated assets are driven by the same draw.
  An indefinite matrix throws unless `repairIndefinite: true`, which goes through `Cholesky.Robust` (eigenvalue clipping) and reports
  `Repaired` and the Frobenius `Adjustment`. `Independent(D)` is the identity factor. The kernel side is `Component` and
  `CorrelateInPlace`: allocation-free, no exceptions, lower triangle only. The former `GaussianCopula` / `ICorrelator` in `Random`
  were removed (they had no factor); `CholeskyCorrelation` lives in `Models` because the diffusion calls it and `Models` does not
  reference `Random`.
- Diffusion (`Models/Diffusion`): `CorrelatedLogNormalDiffusion<Rate, LogSpot, Brownian, Schedule>` has per-asset constant volatility and
  dividend yield and a drift rate read from `Rate`. Layout: asset `d` at pillar `t` is column `t * D + d` (time-major, asset-minor),
  so with the structure-of-arrays rule consecutive simulations stay contiguous for every (pillar, asset); the spot is an ordinary
  `ILogSpot` with `(steps + 1) * D` columns and the Brownian an `IBrownian` with `steps * D` columns. Each step draws
  `y = L z` and applies the exact step `ln S + (r - q - sigma^2/2) dt + sigma sqrt(dt) y` (`CorrelatedLogNormalMath.LogStep`;
  `DiffusePath` is the Hybridizer-free host reference with the same operations in the same order). Registered for `ConstantRate` and
  `CurveRate` on `ArrayDoubleSpot`, `ArrayBrownian`, `PrecalculatedSchedule`.
- Payoffs (`Payoffs/Equity`): `MultiAssetOptions.cs` has `BasketOption` (weighted arithmetic basket of performances `S_d(T)/reference_d`, or
  geometric; negative weights allow spread options), `RainbowOption` (best-of / worst-of call or put) and `WorstOfDownAndInPut`
  (pays `max(K - W(T), 0)` if the worst performance was at or below the barrier at a monitoring pillar; maturity only gives a European
  barrier). `Autocallable` is a worst-of note with Phoenix and Athena features: coupon barrier per date (Athena is coupon barrier =
  autocall barrier with memory, Phoenix has a lower coupon barrier), optional memory of missed coupons, autocall barrier per date,
  and a knock-in capital-protection barrier monitored at chosen pillars. To keep vector lanes in step the kernel has no early exit: an
  `alive` flag (1 until redeemed) weights each cash flow instead of a `break` (`AutocallMath`, Hybridizer-free and shared with the
  tests). Each path returns the discounted sum and the index of its redemption date; the host `AutocallSummary` derives price,
  standard error, redemption probability per date and expected life.
- References (`ClosedForm`): `MultiAssetKernels` has Margrabe's exchange option, Stulz's min/max options on two assets (puts by parity
  from the exchange option) and the geometric basket option (`GeometricBasketLogMoments`, `GeometricBasketPrice`), the control variate
  of the arithmetic basket. `BivariateNormalDistribution.Cdf` is the Drezner-Wesolowsky/Genz bivariate normal used by Stulz.

## Longstaff-Schwartz

`InFine.QuantFinance.LongstaffSchwartz` prices American options by least-squares Monte Carlo. Over simulated
paths it works backwards in time: it regresses the discounted continuation cash flows on a function of the spot
(a quadratic univariate regressor is provided) over the in-the-money paths, and exercises where the immediate
payoff exceeds the fitted continuation value. The sums needed for the regression are accumulated through the
executor's reductions, so the algorithm is the same on every executor. American call and put cash-flow sources
are included. The 2016 benchmark material is under `research/`.

## Excel add-in

`InFine.QuantFinance.Excel` is an Excel-DNA add-in exposing closed-form pricing, implied volatility, dates, curve
bootstrapping and SVI fitting as `QF.*` worksheet functions. All logic lives in the cross-platform
`InFine.QuantFinance.Excel.Core` (argument coercion, a versioned object cache whose handles change when an object is
replaced, the function implementations), which is unit-tested on any OS; the `net8.0-windows` shim only declares the
functions and maps error sentinels to Excel errors. See [excel.md](excel.md).

## Open-core target split (direction, not current state)

The intended direction is an open-core model:

- Open: the host API (concepts, models, payoffs, pricers) and a pure .NET backend (sequential and
  multi-threaded executors).
- Commercial: the Hybridizer backends (CUDA and AVX generation and their tuned executors).

Today the repository is a single set of projects that mixes both, and the library is MIT-licensed (see [LICENSE](../LICENSE)). The
split above is a goal for the restructuring, not something already implemented.
