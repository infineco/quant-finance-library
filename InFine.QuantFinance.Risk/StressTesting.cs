using System;
using System.Collections.Generic;

namespace InFine.QuantFinance.Risk
{
    /// <summary>How a <see cref="StressShock"/> turns the level <c>X0</c> of a risk factor into its stressed level.</summary>
    public enum StressShockMode
    {
        /// <summary><c>X = X0 (1 + x)</c>: <c>x = -0.20</c> is a 20% fall.</summary>
        Relative,

        /// <summary><c>X = X0 + x</c>: <c>x = 0.05</c> is +5 vol points on a volatility quoted as a decimal, +500bp on a rate.</summary>
        Absolute,

        /// <summary><c>X = X0 exp(x)</c>: <c>x</c> is a log-return.</summary>
        LogReturn,

        /// <summary><c>X = x</c>: the factor is set to a level.</summary>
        Set,
    }

    /// <summary>
    /// A declarative shock: a <see cref="StressShockMode"/> and a value, aimed either at one risk factor by name
    /// (<see cref="Factor"/>) or at every factor of a <see cref="RiskFactor.Group"/> (<see cref="Group"/>); exactly one of
    /// the two is set (use <see cref="ForFactor"/> / <see cref="ForGroup"/>).
    /// </summary>
    /// <remarks>
    /// <b>Precedence.</b> In a <see cref="StressScenario"/>, a shock aimed at a factor by name overrides the group shock
    /// for that factor (the group shock still applies to the other members of the group). At most one shock per factor name
    /// and one per group is allowed in a scenario, so there is never a tie. Factor names must exist (a typo throws); a group
    /// without members is a no-op, so that one named scenario can be applied to portfolios that lack, say, a rate factor.
    /// </remarks>
    public sealed record StressShock(StressShockMode Mode, double Value, string? Factor = null, string? Group = null)
    {
        /// <summary>A shock of the factor called <paramref name="factor"/>.</summary>
        public static StressShock ForFactor(string factor, StressShockMode mode, double value) => new(mode, value, Factor: factor);

        /// <summary>A shock of every factor whose <see cref="RiskFactor.Group"/> is <paramref name="group"/>.</summary>
        public static StressShock ForGroup(string group, StressShockMode mode, double value) => new(mode, value, Group: group);

        /// <summary>Short label, <c>name</c> or <c>group:name</c>, used in attributions.</summary>
        public string Label => Factor ?? ("group:" + Group);

        /// <summary>The stressed level of a factor at <paramref name="level"/>, with the shock applied at fraction <paramref name="scale"/> (1 = fully).</summary>
        internal double Apply(double level, double scale) => Mode switch
        {
            StressShockMode.Relative => level * (1.0 + scale * Value),
            StressShockMode.Absolute => level + scale * Value,
            StressShockMode.LogReturn => level * Math.Exp(scale * Value),
            _ => level + scale * (Value - level),
        };
    }

    /// <summary>A named set of shocks applied together, optionally with the passage of time.</summary>
    /// <param name="Name">Scenario name.</param>
    /// <param name="Description">Free text.</param>
    /// <param name="Shocks">The shocks; see <see cref="StressShock"/> for the precedence rules.</param>
    /// <param name="TimeDecayYears">Years by which option expiries are shortened (the stress horizon), non-negative. Default 0 (instantaneous).</param>
    public sealed record StressScenario(string Name, string Description, IReadOnlyList<StressShock> Shocks, double TimeDecayYears = 0.0);

    /// <summary>A named contribution to a scenario's P&amp;L.</summary>
    public sealed record StressContribution(string Label, double Pnl);

    /// <summary>Outcome of one <see cref="StressScenario"/>.</summary>
    /// <param name="Scenario">The scenario.</param>
    /// <param name="TotalPnl">Portfolio P&amp;L with all the shocks (and the time decay) applied together; gain positive.</param>
    /// <param name="InstrumentPnl">P&amp;L of each position (quantity times per-unit P&amp;L), in position order; sums to <paramref name="TotalPnl"/>.</param>
    /// <param name="ShockedLevels">Stressed level of each factor, in factor order.</param>
    /// <param name="Attribution">One-at-a-time contributions, in the order time decay (if any) then the shocks. See <see cref="StressTestEngine"/>.</param>
    /// <param name="Interaction">Total minus the sum of <paramref name="Attribution"/>: the cross effects (gamma, vega-spot, ...).</param>
    public sealed record StressScenarioResult(StressScenario Scenario, double TotalPnl, double[] InstrumentPnl, double[] ShockedLevels,
        IReadOnlyList<StressContribution> Attribution, double Interaction);

    /// <summary>Result of <see cref="StressTestEngine.Run"/>.</summary>
    /// <param name="PortfolioValue">Portfolio value today.</param>
    /// <param name="Results">One result per scenario, in input order.</param>
    public sealed record StressTestReport(double PortfolioValue, IReadOnlyList<StressScenarioResult> Results)
    {
        /// <summary>The scenario with the lowest total P&amp;L (the first one on ties).</summary>
        public StressScenarioResult Worst
        {
            get
            {
                StressScenarioResult worst = Results[0];
                foreach (StressScenarioResult r in Results)
                    if (r.TotalPnl < worst.TotalPnl) worst = r;
                return worst;
            }
        }
    }

    /// <summary>Outcome of <see cref="StressTestEngine.ReverseStress"/>.</summary>
    /// <param name="Reached">True when a scale in (0, maxScale] brings the P&amp;L to the loss threshold.</param>
    /// <param name="Scale">Smallest bracketed scale found (the largest tried when not reached).</param>
    /// <param name="Pnl">P&amp;L at <paramref name="Scale"/>.</param>
    /// <param name="ShockedLevels">Stressed factor levels at <paramref name="Scale"/>.</param>
    public sealed record ReverseStressResult(bool Reached, double Scale, double Pnl, double[] ShockedLevels);

    /// <summary>
    /// Deterministic scenario stress tests: shocks the risk factors and reprices the portfolio by full revaluation, through
    /// the same kernel function as <see cref="FullRevaluationEngine"/> (<see cref="FullRevaluationKernels.InstrumentValue"/>),
    /// so there is a single pricer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>P&amp;L.</b> <c>sum_i q_i (V_i(stressed) - V_i(today))</c>, gain positive; option expiries are shortened by the
    /// scenario's <see cref="StressScenario.TimeDecayYears"/>, so with no shock the P&amp;L is exactly the theta of the book
    /// over that horizon, and with neither shock nor decay it is exactly 0.
    /// </para>
    /// <para>
    /// <b>Attribution.</b> The scenario is repriced once per shock with only that shock applied (one at a time), the time decay
    /// kept in every run. The contribution of a shock is <c>P&amp;L(shock alone + decay) - P&amp;L(decay alone)</c>, the
    /// contribution of the time decay is <c>P&amp;L(decay alone)</c> (shown as "time decay" when positive-length), and the
    /// <see cref="StressScenarioResult.Interaction"/> is the remainder
    /// <c>total - sum(contributions)</c>. Hence contributions plus interaction equal the total P&amp;L by construction (to
    /// floating-point rounding). A shock aimed at a group is one block (all the factors it reaches move together); a group
    /// shock fully overridden by factor shocks contributes 0.
    /// </para>
    /// </remarks>
    public static class StressTestEngine
    {
        /// <summary>Time-decay label in <see cref="StressScenarioResult.Attribution"/>.</summary>
        public const string TimeDecayLabel = "time decay";

        /// <summary>Stresses <paramref name="portfolio"/> with every scenario.</summary>
        public static StressTestReport Run(IReadOnlyList<PortfolioPosition> portfolio, IReadOnlyList<RiskFactor> factors,
            IReadOnlyList<StressScenario> scenarios)
        {
            ArgumentNullException.ThrowIfNull(portfolio);
            ArgumentNullException.ThrowIfNull(factors);
            ArgumentNullException.ThrowIfNull(scenarios);
            if (scenarios.Count < 1) throw new ArgumentException("At least one scenario is required.", nameof(scenarios));
            FullRevaluationEngine engine = Compile(portfolio, factors);

            var results = new StressScenarioResult[scenarios.Count];
            for (int s = 0; s < results.Length; s++)
                results[s] = RunScenario(engine, scenarios[s] ?? throw new ArgumentException($"Scenario {s} is null.", nameof(scenarios)));
            return new StressTestReport(engine.PortfolioValue, results);
        }

        /// <summary>
        /// Reverse stress: scales the shock direction <paramref name="shocks"/> (all applied together, at fraction <c>k</c> of
        /// their size) until the portfolio loses <paramref name="lossThreshold"/>. The scaling is <c>x -&gt; k x</c> for
        /// Relative, Absolute and LogReturn shocks and a move of the level a fraction <c>k</c> of the way to the target for Set
        /// shocks; <c>k = 1</c> is the shock as given.
        /// </summary>
        /// <param name="portfolio">The positions.</param>
        /// <param name="factors">The risk factors.</param>
        /// <param name="shocks">The direction (same precedence rules as in a <see cref="StressScenario"/>).</param>
        /// <param name="lossThreshold">Positive loss to reach (P&amp;L = -lossThreshold).</param>
        /// <param name="maxScale">Largest scale tried, positive. Default 10.</param>
        /// <param name="timeDecayYears">Time decay held fixed during the search.</param>
        /// <param name="tolerance">Absolute tolerance on the scale (bisection stops below it). Default 1e-12.</param>
        /// <remarks>
        /// The scale range is scanned in 64 equal steps for the first one where the P&amp;L is at or below the threshold; that
        /// bracket is bisected (about 40 evaluations). The result is therefore the first crossing found on the grid, even if
        /// the P&amp;L is not monotone (a long straddle, say); a crossing narrower than a grid step can be missed.
        /// </remarks>
        public static ReverseStressResult ReverseStress(IReadOnlyList<PortfolioPosition> portfolio, IReadOnlyList<RiskFactor> factors,
            IReadOnlyList<StressShock> shocks, double lossThreshold, double maxScale = 10.0, double timeDecayYears = 0.0, double tolerance = 1e-12)
        {
            ArgumentNullException.ThrowIfNull(shocks);
            if (!(lossThreshold > 0.0) || double.IsInfinity(lossThreshold)) throw new ArgumentOutOfRangeException(nameof(lossThreshold), "The loss threshold must be positive and finite.");
            if (!(maxScale > 0.0) || double.IsInfinity(maxScale)) throw new ArgumentOutOfRangeException(nameof(maxScale), "maxScale must be positive and finite.");
            if (!(tolerance > 0.0)) throw new ArgumentOutOfRangeException(nameof(tolerance), "The tolerance must be positive.");
            FullRevaluationEngine engine = Compile(portfolio, factors);
            ResolvedShock[] resolved = Resolve(engine.Factors, shocks);

            double target = -lossThreshold;
            double Pnl(double k) => TotalPnl(engine, Levels(engine.Factors, resolved, k, AllShocks), timeDecayYears, null);

            const int steps = 64;
            double lo = 0.0, hi = double.NaN, pnlHi = 0.0;
            for (int i = 1; i <= steps; i++)
            {
                double k = maxScale * i / steps;
                double p = Pnl(k);
                if (p <= target) { hi = k; pnlHi = p; break; }
                lo = k;
            }
            if (double.IsNaN(hi))
            {
                double[] last = Levels(engine.Factors, resolved, maxScale, AllShocks);
                return new ReverseStressResult(false, maxScale, Pnl(maxScale), last);
            }
            for (int iter = 0; iter < 200 && hi - lo > tolerance; iter++)
            {
                double mid = 0.5 * (lo + hi);
                double p = Pnl(mid);
                if (p <= target) { hi = mid; pnlHi = p; } else lo = mid;
            }
            return new ReverseStressResult(true, hi, pnlHi, Levels(engine.Factors, resolved, hi, AllShocks));
        }

        // ---- internals --------------------------------------------------------------------------------------------

        const int AllShocks = -1, NoShock = -2;

        readonly record struct ResolvedShock(StressShock Shock, int[] Factors);

        static FullRevaluationEngine Compile(IReadOnlyList<PortfolioPosition> portfolio, IReadOnlyList<RiskFactor> factors)
        {
            ArgumentNullException.ThrowIfNull(portfolio);
            ArgumentNullException.ThrowIfNull(factors);
            // Validation and compilation of the portfolio are the engine's; the covariance is unused (no moves are drawn).
            return new FullRevaluationEngine(factors, new double[factors.Count, factors.Count], portfolio);
        }

        /// <summary>Applies the precedence rules: the factors each shock finally reaches, in shock order.</summary>
        static ResolvedShock[] Resolve(IReadOnlyList<RiskFactor> factors, IReadOnlyList<StressShock> shocks)
        {
            var byName = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int j = 0; j < factors.Count; j++)
                byName.TryAdd(factors[j].Name, j);

            var specific = new HashSet<int>();
            var groupsSeen = new HashSet<string>(StringComparer.Ordinal);
            var namesSeen = new HashSet<string>(StringComparer.Ordinal);
            for (int s = 0; s < shocks.Count; s++)
            {
                StressShock shock = shocks[s] ?? throw new ArgumentException($"Shock {s} is null.", nameof(shocks));
                if (!double.IsFinite(shock.Value)) throw new ArgumentException($"Shock {s} has a non-finite value.", nameof(shocks));
                if (!Enum.IsDefined(shock.Mode)) throw new ArgumentException($"Shock {s} has an unknown mode.", nameof(shocks));
                if ((shock.Factor is null) == (shock.Group is null))
                    throw new ArgumentException($"Shock {s} must target exactly one of a factor or a group.", nameof(shocks));
                if (shock.Factor is not null)
                {
                    if (!byName.TryGetValue(shock.Factor, out int j))
                        throw new ArgumentException($"Shock {s} targets unknown factor '{shock.Factor}'.", nameof(shocks));
                    if (!namesSeen.Add(shock.Factor))
                        throw new ArgumentException($"Factor '{shock.Factor}' is shocked twice.", nameof(shocks));
                    specific.Add(j);
                }
                else if (!groupsSeen.Add(shock.Group!))
                    throw new ArgumentException($"Group '{shock.Group}' is shocked twice.", nameof(shocks));
            }

            var resolved = new ResolvedShock[shocks.Count];
            for (int s = 0; s < shocks.Count; s++)
            {
                StressShock shock = shocks[s];
                var reached = new List<int>();
                if (shock.Factor is not null) reached.Add(byName[shock.Factor]);
                else
                    for (int j = 0; j < factors.Count; j++)
                        if (factors[j].Group == shock.Group && !specific.Contains(j)) reached.Add(j);
                resolved[s] = new ResolvedShock(shock, reached.ToArray());
            }
            return resolved;
        }

        /// <summary>Stressed levels with the resolved shocks at fraction <paramref name="scale"/>; only shock number <paramref name="only"/> unless it is <see cref="AllShocks"/> (none at all for <see cref="NoShock"/>).</summary>
        static double[] Levels(IReadOnlyList<RiskFactor> factors, ResolvedShock[] resolved, double scale, int only)
        {
            var levels = new double[factors.Count];
            for (int j = 0; j < levels.Length; j++) levels[j] = factors[j].Level;
            for (int s = 0; s < resolved.Length; s++)
            {
                if (only != AllShocks && s != only) continue;
                foreach (int j in resolved[s].Factors)
                    levels[j] = resolved[s].Shock.Apply(factors[j].Level, scale);
            }
            return levels;
        }

        static double TotalPnl(FullRevaluationEngine engine, double[] levels, double timeDecay, double[]? instrumentPnl)
        {
            double[] unit = engine.RevalueUnitPnl(levels, timeDecay);
            double total = 0.0;
            for (int i = 0; i < unit.Length; i++)
            {
                double p = engine.Positions[i].Quantity * unit[i];
                if (instrumentPnl is not null) instrumentPnl[i] = p;
                total += p;
            }
            return total;
        }

        static StressScenarioResult RunScenario(FullRevaluationEngine engine, StressScenario scenario)
        {
            if (!(scenario.TimeDecayYears >= 0.0) || double.IsInfinity(scenario.TimeDecayYears))
                throw new ArgumentException($"Scenario '{scenario.Name}': the time decay must be non-negative and finite.", nameof(scenario));
            ArgumentNullException.ThrowIfNull(scenario.Shocks);
            ResolvedShock[] resolved = Resolve(engine.Factors, scenario.Shocks);
            double decay = scenario.TimeDecayYears;

            double[] levels = Levels(engine.Factors, resolved, 1.0, AllShocks);
            var instrumentPnl = new double[engine.PositionCount];
            double total = TotalPnl(engine, levels, decay, instrumentPnl);

            var attribution = new List<StressContribution>();
            double sum = 0.0;
            double decayOnly = TotalPnl(engine, Levels(engine.Factors, resolved, 1.0, NoShock), decay, null);
            if (decay > 0.0)
            {
                attribution.Add(new StressContribution(TimeDecayLabel, decayOnly));
                sum += decayOnly;
            }
            for (int s = 0; s < resolved.Length; s++)
            {
                double alone = TotalPnl(engine, Levels(engine.Factors, resolved, 1.0, s), decay, null) - decayOnly;
                attribution.Add(new StressContribution(resolved[s].Shock.Label, alone));
                sum += alone;
            }
            return new StressScenarioResult(scenario, total, instrumentPnl, levels, attribution, total - sum);
        }
    }

    /// <summary>
    /// Illustrative stress scenarios as data. They are generic, round-number shocks in the style of regulatory and
    /// risk-management stress sets; they are NOT calibrated to, and do not reproduce, the moves of any specific historical
    /// date or episode. They shock factors by <see cref="RiskFactor.Group"/>: tag spots <see cref="EquityGroup"/>,
    /// implied volatilities <see cref="VolatilityGroup"/> and rates <see cref="RatesGroup"/> (a group with no member is skipped).
    /// Volatilities are assumed quoted as decimals (0.10 = 10 vol points) and rates as decimals (0.01 = 100bp).
    /// </summary>
    public static class StressScenarioLibrary
    {
        /// <summary>Group tag of equity / index spot factors.</summary>
        public const string EquityGroup = "equity";

        /// <summary>Group tag of implied-volatility factors.</summary>
        public const string VolatilityGroup = "vol";

        /// <summary>Group tag of interest-rate factors.</summary>
        public const string RatesGroup = "rates";

        const string Illustrative = " Illustrative round-number shocks, not calibrated to any historical date.";

        /// <summary>Equities -20%, volatilities +10 points, rates -50bp.</summary>
        public static StressScenario EquityCrash { get; } = new("Equity crash -20% / vol +10pts / rates -50bp",
            "Broad equity sell-off with a volatility spike and a flight to quality." + Illustrative,
            new[]
            {
                StressShock.ForGroup(EquityGroup, StressShockMode.Relative, -0.20),
                StressShock.ForGroup(VolatilityGroup, StressShockMode.Absolute, 0.10),
                StressShock.ForGroup(RatesGroup, StressShockMode.Absolute, -0.005),
            });

        /// <summary>Volatilities +10 points, spots unchanged.</summary>
        public static StressScenario VolSpike { get; } = new("Vol spike +10pts",
            "Implied volatility jump with spots unchanged." + Illustrative,
            new[] { StressShock.ForGroup(VolatilityGroup, StressShockMode.Absolute, 0.10) });

        /// <summary>Rates +100bp.</summary>
        public static StressScenario RatesUp100 { get; } = new("Rates +100bp",
            "Parallel upward shift of the rates." + Illustrative,
            new[] { StressShock.ForGroup(RatesGroup, StressShockMode.Absolute, 0.01) });

        /// <summary>Rates -100bp.</summary>
        public static StressScenario RatesDown100 { get; } = new("Rates -100bp",
            "Parallel downward shift of the rates." + Illustrative,
            new[] { StressShock.ForGroup(RatesGroup, StressShockMode.Absolute, -0.01) });

        /// <summary>Equities +10%, volatilities -5 points.</summary>
        public static StressScenario EquityRally { get; } = new("Equity rally +10% / vol -5pts",
            "Equity rally with compressing volatility." + Illustrative,
            new[]
            {
                StressShock.ForGroup(EquityGroup, StressShockMode.Relative, 0.10),
                StressShock.ForGroup(VolatilityGroup, StressShockMode.Absolute, -0.05),
            });

        /// <summary>All the library scenarios.</summary>
        public static IReadOnlyList<StressScenario> All { get; } = new[] { EquityCrash, VolSpike, RatesUp100, RatesDown100, EquityRally };
    }
}
