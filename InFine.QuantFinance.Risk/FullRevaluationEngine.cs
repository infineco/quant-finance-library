using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Numerics;
using InFine.QuantFinance.Random.QuasiRandom;

namespace InFine.QuantFinance.Risk
{
    /// <summary>How the move of a risk factor over the horizon is applied to its level (see <see cref="FullRevaluationKernels"/>).</summary>
    public enum RiskFactorKind
    {
        /// <summary><c>X = X0 exp(x - v/2)</c>: <c>x</c> is a log-return with variance <c>v</c>; <c>E[X] = X0</c>. For spots.</summary>
        LogNormal = FullRevaluationKernels.LogNormalFactor,

        /// <summary><c>X = X0 (1 + x)</c>: <c>x</c> is a simple return. Linear positions then have exactly normal P&amp;L.</summary>
        SimpleReturn = FullRevaluationKernels.SimpleReturnFactor,

        /// <summary><c>X = X0 + x</c>: <c>x</c> is an absolute change. For implied volatilities and rates.</summary>
        Additive = FullRevaluationKernels.AdditiveFactor,
    }

    /// <summary>A market risk factor: its name, its level today and how its moves are applied.</summary>
    /// <param name="Name">Label (for reports only).</param>
    /// <param name="Level">Level today (spot price, implied volatility as a decimal, continuously compounded rate), finite.</param>
    /// <param name="Kind">How a move is applied; it fixes what the matching row of the covariance describes.</param>
    public sealed record RiskFactor(string Name, double Level, RiskFactorKind Kind)
    {
        /// <summary>
        /// Optional tag ("equity", "vol", "rates", ...) used by <see cref="StressShock"/> to shock a family of factors at
        /// once. Not used by the Monte Carlo engine.
        /// </summary>
        public string? Group { get; init; }
    }

    /// <summary>A position of the portfolio: an instrument and a quantity (negative is short).</summary>
    public abstract record PortfolioPosition
    {
        private protected PortfolioPosition(double quantity)
        {
            Quantity = quantity;
        }

        /// <summary>Number of units held; P&amp;L scales linearly with it.</summary>
        public double Quantity { get; init; }
    }

    /// <summary>A linear position worth the level of a factor per unit (shares, index units, an FX amount).</summary>
    /// <param name="SpotFactor">Index of the factor whose level is the unit price.</param>
    /// <param name="Quantity">Units held.</param>
    public sealed record SpotPosition(int SpotFactor, double Quantity) : PortfolioPosition(Quantity);

    /// <summary>
    /// A European option priced by generalised Black-Scholes-Merton (<see cref="BlackScholesKernels.Price"/>) on the
    /// level of <paramref name="SpotFactor"/>, with the volatility and the rate read from factors. Use a factor with zero
    /// variance for a volatility or rate that should not move.
    /// </summary>
    /// <param name="Type">Call or put.</param>
    /// <param name="SpotFactor">Index of the underlying spot factor.</param>
    /// <param name="VolatilityFactor">Index of the implied-volatility factor (level = lognormal volatility, decimal).</param>
    /// <param name="RateFactor">Index of the rate factor (level = continuously compounded rate).</param>
    /// <param name="Strike">Strike, positive.</param>
    /// <param name="Maturity">Time to expiry in years today, non-negative.</param>
    /// <param name="Quantity">Number of options held (negative is written).</param>
    /// <param name="DividendYield">Continuous dividend yield, constant.</param>
    public sealed record EuropeanOptionPosition(OptionType Type, int SpotFactor, int VolatilityFactor, int RateFactor,
        double Strike, double Maturity, double Quantity, double DividendYield = 0.0) : PortfolioPosition(Quantity);

    /// <summary>Source of the standard normals of the scenarios.</summary>
    public enum ScenarioGenerator
    {
        /// <summary>Philox4x32-10 pseudo-random normals by inversion, counter (seed, scenario, factor).</summary>
        Philox,

        /// <summary>
        /// Sobol points (one dimension per factor) with Matousek linear scrambling and a digital shift seeded by
        /// <see cref="MonteCarloVarOptions.Seed"/> and <see cref="MonteCarloVarOptions.SobolReplication"/>, normals by
        /// inversion. Point 0 is used (it is an ordinary point once scrambled); prefer a power-of-two scenario count.
        /// </summary>
        ScrambledSobol,
    }

    /// <summary>Settings of a Monte Carlo full-revaluation run.</summary>
    public sealed record MonteCarloVarOptions
    {
        /// <summary>Number of scenarios M, at least 1. Default 100 000.</summary>
        public int Scenarios { get; init; } = 100_000;

        /// <summary>64-bit seed: the Philox key, or the seed of the Sobol scrambling.</summary>
        public ulong Seed { get; init; } = 1;

        /// <summary>Scenario generator. Default Philox.</summary>
        public ScenarioGenerator Generator { get; init; } = ScenarioGenerator.Philox;

        /// <summary>Independent randomisation index of the scrambled Sobol set (ignored by Philox).</summary>
        public uint SobolReplication { get; init; }

        /// <summary>
        /// Horizon in periods of the covariance (for example 10 for a 10-day VaR on a daily covariance), positive. The
        /// horizon covariance is <c>Horizon * covariance</c>: independent, identically distributed periods, as in
        /// <see cref="ParametricRisk"/>.
        /// </summary>
        public double Horizon { get; init; } = 1.0;

        /// <summary>
        /// Years by which option expiries are shortened in the scenarios (the horizon in years, to include theta in the
        /// P&amp;L), non-negative. Default 0: an instantaneous shock.
        /// </summary>
        public double TimeDecay { get; init; }

        /// <summary>Run the scenario chunks concurrently (Parallel.For) instead of one after the other. Default true.</summary>
        public bool Parallel { get; init; } = true;

        /// <summary>Scenarios per chunk, at least 1. Results do not depend on it.</summary>
        public int ChunkSize { get; init; } = 1024;

        /// <summary>Maximum number of concurrent chunks when <see cref="Parallel"/>; -1 (default) lets the runtime decide.</summary>
        public int MaxDegreeOfParallelism { get; init; } = -1;

        /// <summary>
        /// Also keep the M x N per-instrument P&amp;L, needed by <see cref="MonteCarloVarResult.Decompose"/>
        /// (8 M N bytes, twice that while the result is assembled).
        /// </summary>
        public bool KeepInstrumentPnl { get; init; }
    }

    /// <summary>
    /// Monte Carlo Value-at-Risk by full revaluation: M correlated market scenarios over a horizon, every position of
    /// the portfolio repriced in every scenario, VaR and ES of the simulated P&amp;L by
    /// <see cref="HistoricalRisk"/>, and their Euler decomposition by position. Host-side API over
    /// <see cref="FullRevaluationKernels"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Inputs.</b> The covariance is the per-period covariance of the factor moves, in the units implied by each
    /// factor's <see cref="RiskFactorKind"/>: log-returns for <see cref="RiskFactorKind.LogNormal"/>, simple returns for
    /// <see cref="RiskFactorKind.SimpleReturn"/>, absolute changes for <see cref="RiskFactorKind.Additive"/>. Any
    /// estimator of the library fits (<see cref="Covariance.Sample(double[,], bool)"/>, <see cref="EwmaCovariance"/>,
    /// <see cref="LedoitWolf"/>) applied to the matching returns / changes. A factor with zero variance (and then zero
    /// covariances) is held constant: that is how a fixed volatility or rate is expressed.
    /// </para>
    /// <para>
    /// <b>Factorisation.</b> The factors with positive variance are factorised by <see cref="Cholesky.Robust"/>, which
    /// repairs a slightly indefinite estimate (reported by <see cref="CovarianceRepaired"/>); the constant factors get
    /// zero rows and columns, so the factor stays lower-triangular in the factor order.
    /// </para>
    /// <para>
    /// <b>Execution.</b> The scenario range is cut in chunks of <see cref="MonteCarloVarOptions.ChunkSize"/> run
    /// sequentially or with <c>Parallel.For</c>, each with its own scratch row; every scenario is a pure function of its
    /// index (counter-based random numbers), so the P&amp;L arrays are bit-identical whatever the chunk size or the
    /// degree of parallelism. The library's <c>IParallelExecutor</c> is not used on the host because its signatures
    /// carry Hybridizer vector types; on a device the same cores run through the
    /// <see cref="FullRevaluationKernels.SimulateScenarios"/> entry point (see <see cref="FullRevaluationBatch"/>).
    /// </para>
    /// <para>
    /// <b>Measures.</b> P&amp;L is <c>sum_i q_i (V_i(scenario) - V_i(today))</c>, gain positive; VaR and ES are positive
    /// losses (see <see cref="VarEs"/>), computed by <see cref="HistoricalRisk.Measures(double[], double, QuantileMethod)"/>
    /// on the simulated sample. Their Monte Carlo error shrinks as <c>1/sqrt(M)</c> (faster with scrambled Sobol on
    /// smooth portfolios).
    /// </para>
    /// </remarks>
    public sealed class FullRevaluationEngine
    {
        readonly RiskFactor[] _factors;
        readonly PortfolioPosition[] _positions;
        readonly double[] _cholesky;      // per-period lower factor, row-major
        readonly int[] _factorKind;
        readonly double[] _baseLevel;
        readonly int[] _instrumentType, _optionSign, _spotFactor, _volFactor, _rateFactor;
        readonly double[] _strike, _maturity, _dividendYield, _quantity, _baseValue;

        /// <summary>Validates and compiles a portfolio and its risk-factor model.</summary>
        /// <param name="factors">The F risk factors.</param>
        /// <param name="covariance">F x F per-period covariance of the factor moves (symmetric part used; see remarks).</param>
        /// <param name="positions">The N positions, referring to factors by index.</param>
        public FullRevaluationEngine(IReadOnlyList<RiskFactor> factors, double[,] covariance, IReadOnlyList<PortfolioPosition> positions)
        {
            ArgumentNullException.ThrowIfNull(factors);
            ArgumentNullException.ThrowIfNull(covariance);
            ArgumentNullException.ThrowIfNull(positions);
            int f = factors.Count, n = positions.Count;
            if (f < 1) throw new ArgumentException("At least one risk factor is required.", nameof(factors));
            if (n < 1) throw new ArgumentException("At least one position is required.", nameof(positions));
            if (covariance.GetLength(0) != f || covariance.GetLength(1) != f)
                throw new ArgumentException($"Expected a {f} x {f} covariance (one row per factor).", nameof(covariance));
            if (!Matrix.IsFinite(covariance)) throw new ArgumentException("Covariance entries must be finite.", nameof(covariance));

            _factors = new RiskFactor[f];
            _factorKind = new int[f];
            _baseLevel = new double[f];
            for (int j = 0; j < f; j++)
            {
                RiskFactor factor = factors[j] ?? throw new ArgumentException($"Factor {j} is null.", nameof(factors));
                if (!double.IsFinite(factor.Level)) throw new ArgumentException($"Factor {j} has a non-finite level.", nameof(factors));
                if (!Enum.IsDefined(factor.Kind)) throw new ArgumentException($"Factor {j} has an unknown kind.", nameof(factors));
                _factors[j] = factor;
                _factorKind[j] = (int)factor.Kind;
                _baseLevel[j] = factor.Level;
            }

            _cholesky = Factorise(covariance, out bool repaired);
            CovarianceRepaired = repaired;

            _positions = new PortfolioPosition[n];
            _instrumentType = new int[n];
            _optionSign = new int[n];
            _spotFactor = new int[n];
            _volFactor = new int[n];
            _rateFactor = new int[n];
            _strike = new double[n];
            _maturity = new double[n];
            _dividendYield = new double[n];
            _quantity = new double[n];
            for (int i = 0; i < n; i++)
            {
                PortfolioPosition position = positions[i] ?? throw new ArgumentException($"Position {i} is null.", nameof(positions));
                if (!double.IsFinite(position.Quantity)) throw new ArgumentException($"Position {i} has a non-finite quantity.", nameof(positions));
                _positions[i] = position;
                _quantity[i] = position.Quantity;
                switch (position)
                {
                    case SpotPosition spot:
                        _instrumentType[i] = FullRevaluationKernels.LinearInstrument;
                        _spotFactor[i] = RequireFactor(spot.SpotFactor, f, i);
                        break;
                    case EuropeanOptionPosition option:
                        if (option.Type != OptionType.Call && option.Type != OptionType.Put)
                            throw new ArgumentException($"Position {i} has an unknown option type.", nameof(positions));
                        if (!(option.Strike > 0.0) || double.IsInfinity(option.Strike))
                            throw new ArgumentException($"Position {i}: the strike must be positive and finite.", nameof(positions));
                        if (!(option.Maturity >= 0.0) || double.IsInfinity(option.Maturity))
                            throw new ArgumentException($"Position {i}: the maturity must be non-negative and finite.", nameof(positions));
                        if (!double.IsFinite(option.DividendYield))
                            throw new ArgumentException($"Position {i}: the dividend yield must be finite.", nameof(positions));
                        _instrumentType[i] = FullRevaluationKernels.EuropeanOptionInstrument;
                        _optionSign[i] = (int)option.Type;
                        _spotFactor[i] = RequireFactor(option.SpotFactor, f, i);
                        _volFactor[i] = RequireFactor(option.VolatilityFactor, f, i);
                        _rateFactor[i] = RequireFactor(option.RateFactor, f, i);
                        _strike[i] = option.Strike;
                        _maturity[i] = option.Maturity;
                        _dividendYield[i] = option.DividendYield;
                        break;
                    default:
                        throw new ArgumentException($"Position {i} has an unsupported instrument type {position.GetType().Name}.", nameof(positions));
                }
            }

            // Today's values, through the same kernel function as the scenarios (so a zero move is a zero P&L).
            _baseValue = new double[n];
            for (int i = 0; i < n; i++)
                _baseValue[i] = FullRevaluationKernels.InstrumentValue(i, _baseLevel, 0, _instrumentType, _optionSign, _spotFactor,
                    _volFactor, _rateFactor, _strike, _maturity, _dividendYield, 0.0);
        }

        /// <summary>Number of risk factors F.</summary>
        public int FactorCount => _factors.Length;

        /// <summary>Number of positions N.</summary>
        public int PositionCount => _positions.Length;

        /// <summary>The risk factors, in index order.</summary>
        public IReadOnlyList<RiskFactor> Factors => _factors;

        /// <summary>The positions, in index order.</summary>
        public IReadOnlyList<PortfolioPosition> Positions => _positions;

        /// <summary>True when the covariance of the moving factors was not numerically positive definite and was repaired.</summary>
        public bool CovarianceRepaired { get; }

        /// <summary>Value per unit of each position today (new array).</summary>
        public double[] BaseValues => (double[])_baseValue.Clone();

        /// <summary>Portfolio value today, <c>sum_i q_i V_i</c>.</summary>
        public double PortfolioValue
        {
            get
            {
                double v = 0.0;
                for (int i = 0; i < _baseValue.Length; i++) v += _quantity[i] * _baseValue[i];
                return v;
            }
        }

        /// <summary>
        /// Deterministic revaluation: the P&amp;L per unit of each position, <c>V_i(levels) - V_i(today)</c>, when the factor
        /// levels are <paramref name="factorLevels"/> and option expiries are shortened by <paramref name="timeDecay"/> years.
        /// Same kernel function as the scenarios of <see cref="Run"/>.
        /// </summary>
        public double[] RevalueUnitPnl(double[] factorLevels, double timeDecay = 0.0)
        {
            ArgumentNullException.ThrowIfNull(factorLevels);
            if (factorLevels.Length != _factors.Length)
                throw new ArgumentException($"Expected {_factors.Length} factor levels.", nameof(factorLevels));
            if (!(timeDecay >= 0.0) || double.IsInfinity(timeDecay))
                throw new ArgumentOutOfRangeException(nameof(timeDecay), "The time decay must be non-negative and finite.");
            int n = _positions.Length;
            var unit = new double[n];
            for (int i = 0; i < n; i++)
                unit[i] = FullRevaluationKernels.InstrumentValue(i, factorLevels, 0, _instrumentType, _optionSign, _spotFactor,
                    _volFactor, _rateFactor, _strike, _maturity, _dividendYield, timeDecay) - _baseValue[i];
            return unit;
        }

        /// <summary>The per-period lower Cholesky factor of the covariance, F x F (new array).</summary>
        public double[,] CholeskyFactor
        {
            get
            {
                int f = _factors.Length;
                var l = new double[f, f];
                for (int j = 0; j < f; j++)
                    for (int k = 0; k < f; k++) l[j, k] = _cholesky[j * f + k];
                return l;
            }
        }

        /// <summary>
        /// Builds the flat kernel arguments and output buffers of a run (horizon-scaled Cholesky factor, generator
        /// tables, instrument arrays), without running it. Use it to launch <see cref="FullRevaluationKernels.SimulateScenarios"/>
        /// on a device, or call <see cref="FullRevaluationBatch.RunOnHost"/>.
        /// </summary>
        public FullRevaluationBatch CreateBatch(MonteCarloVarOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            int f = _factors.Length, n = _positions.Length;
            if (options.Scenarios < 1) throw new ArgumentOutOfRangeException(nameof(options), "At least one scenario is required.");
            if (!(options.Horizon > 0.0) || double.IsInfinity(options.Horizon))
                throw new ArgumentOutOfRangeException(nameof(options), "The horizon must be positive and finite.");
            if (!(options.TimeDecay >= 0.0) || double.IsInfinity(options.TimeDecay))
                throw new ArgumentOutOfRangeException(nameof(options), "The time decay must be non-negative and finite.");
            if (options.KeepInstrumentPnl && (long)options.Scenarios * n > Array.MaxLength)
                throw new ArgumentOutOfRangeException(nameof(options), "Scenarios x positions is too large to keep the per-instrument P&L.");

            double scale = Math.Sqrt(options.Horizon);
            var l = new double[f * f];
            var halfVariance = new double[f];
            for (int j = 0; j < f; j++)
            {
                double v = 0.0;
                for (int k = 0; k <= j; k++)
                {
                    double ljk = scale * _cholesky[j * f + k];
                    l[j * f + k] = ljk;
                    v += ljk * ljk;
                }
                halfVariance[j] = 0.5 * v;
            }

            int generator;
            uint[] directions, shifts;
            switch (options.Generator)
            {
                case ScenarioGenerator.Philox:
                    generator = FullRevaluationKernels.PhiloxNormals;
                    directions = Array.Empty<uint>();
                    shifts = Array.Empty<uint>();
                    break;
                case ScenarioGenerator.ScrambledSobol:
                    if (f > Sobol.MaxDimension)
                        throw new ArgumentOutOfRangeException(nameof(options), $"Sobol supports at most {Sobol.MaxDimension} factors.");
                    generator = FullRevaluationKernels.SobolNormals;
                    directions = SobolScrambling.ScrambleDirectionNumbers(Sobol.CreateDirectionNumbers(f), options.Seed, options.SobolReplication);
                    shifts = SobolScrambling.CreateDigitalShifts(f, options.Seed, options.SobolReplication);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(options), "Unknown scenario generator.");
            }

            return new FullRevaluationBatch(options.Scenarios, generator, options.Seed, FullRevaluationKernels.DefaultPhiloxStream,
                directions, shifts, f, l, (int[])_factorKind.Clone(), (double[])_baseLevel.Clone(), halfVariance,
                n, (int[])_instrumentType.Clone(), (int[])_optionSign.Clone(), (int[])_spotFactor.Clone(), (int[])_volFactor.Clone(),
                (int[])_rateFactor.Clone(), (double[])_strike.Clone(), (double[])_maturity.Clone(), (double[])_dividendYield.Clone(),
                (double[])_quantity.Clone(), (double[])_baseValue.Clone(), options.TimeDecay, options.KeepInstrumentPnl);
        }

        /// <summary>Simulates the scenarios on the host and returns the P&amp;L sample (see the class remarks).</summary>
        public MonteCarloVarResult Run(MonteCarloVarOptions? options = null)
        {
            options ??= new MonteCarloVarOptions();
            if (options.ChunkSize < 1) throw new ArgumentOutOfRangeException(nameof(options), "The chunk size must be at least 1.");
            if (options.MaxDegreeOfParallelism == 0 || options.MaxDegreeOfParallelism < -1)
                throw new ArgumentOutOfRangeException(nameof(options), "MaxDegreeOfParallelism must be -1 or positive.");
            FullRevaluationBatch batch = CreateBatch(options);
            batch.RunOnHost(options.Parallel, options.ChunkSize, options.MaxDegreeOfParallelism);
            return new MonteCarloVarResult(batch, CovarianceRepaired);
        }

        /// <summary>Lower Cholesky factor (row-major) of the covariance: Robust on the factors with positive variance, zero elsewhere.</summary>
        static double[] Factorise(double[,] covariance, out bool repaired)
        {
            int f = covariance.GetLength(0);
            var active = new List<int>(f);
            for (int j = 0; j < f; j++)
            {
                double d = covariance[j, j];
                if (d < 0.0) throw new ArgumentException($"Negative variance for factor {j}.", nameof(covariance));
                if (d > 0.0) active.Add(j);
                else
                    for (int k = 0; k < f; k++)
                        if (covariance[j, k] != 0.0 || covariance[k, j] != 0.0)
                            throw new ArgumentException($"Factor {j} has zero variance but a non-zero covariance with factor {k}.", nameof(covariance));
            }

            var l = new double[f * f];
            repaired = false;
            int m = active.Count;
            if (m == 0) return l;
            var sub = new double[m, m];
            for (int a = 0; a < m; a++)
                for (int b = 0; b < m; b++) sub[a, b] = covariance[active[a], active[b]];
            RobustCholeskyResult chol = Cholesky.Robust(sub);
            repaired = chol.Repaired;
            for (int a = 0; a < m; a++)
                for (int b = 0; b <= a; b++) l[active[a] * f + active[b]] = chol.L[a, b];
            return l;
        }

        static int RequireFactor(int index, int factorCount, int position)
        {
            if (index < 0 || index >= factorCount)
                throw new ArgumentException($"Position {position} refers to factor {index}, outside [0, {factorCount}).", "positions");
            return index;
        }
    }

    /// <summary>
    /// The flat arguments of <see cref="FullRevaluationKernels.SimulateScenarios"/> for one run, and its output buffers.
    /// Property names match the kernel parameters. Built by <see cref="FullRevaluationEngine.CreateBatch"/>.
    /// </summary>
    /// <remarks>
    /// To run on a CUDA device with a hybridized build (not exercised in this repository's CI, which has no GPU):
    /// <code>
    /// HybRunner runner = HybRunner.Cuda(binaryName).SetDistrib(gridDimX, blockDimX);
    /// dynamic kernels = runner.Wrap(new FullRevaluationKernels());
    /// double[] scratch = batch.CreateScratch(gridDimX * blockDimX);
    /// kernels.SimulateScenarios(0, batch.ScenarioCount, batch.Generator, batch.Seed, batch.Stream, batch.SobolDirections,
    ///     batch.SobolShifts, batch.FactorCount, batch.CholeskyLower, batch.FactorKind, batch.BaseLevel, batch.HalfVariance,
    ///     batch.InstrumentCount, batch.InstrumentType, batch.OptionSign, batch.SpotFactor, batch.VolFactor, batch.RateFactor,
    ///     batch.Strike, batch.Maturity, batch.DividendYield, batch.Quantity, batch.BaseValue, batch.TimeDecay,
    ///     scratch, batch.Pnl, batch.ScenarioCount, batch.StoreInstrumentPnl, batch.InstrumentPnl);
    /// </code>
    /// The marshaller copies the arrays to the device and the outputs back. The P&amp;L then agrees with
    /// <see cref="RunOnHost"/> up to the last-bit differences of the device's <c>exp</c> / <c>log</c>.
    /// </remarks>
    public sealed class FullRevaluationBatch
    {
        internal FullRevaluationBatch(int scenarioCount, int generator, ulong seed, uint stream, uint[] sobolDirections, uint[] sobolShifts,
            int factorCount, double[] choleskyLower, int[] factorKind, double[] baseLevel, double[] halfVariance,
            int instrumentCount, int[] instrumentType, int[] optionSign, int[] spotFactor, int[] volFactor, int[] rateFactor,
            double[] strike, double[] maturity, double[] dividendYield, double[] quantity, double[] baseValue, double timeDecay,
            bool keepInstrumentPnl)
        {
            ScenarioCount = scenarioCount;
            Generator = generator;
            Seed = seed;
            Stream = stream;
            SobolDirections = sobolDirections;
            SobolShifts = sobolShifts;
            FactorCount = factorCount;
            CholeskyLower = choleskyLower;
            FactorKind = factorKind;
            BaseLevel = baseLevel;
            HalfVariance = halfVariance;
            InstrumentCount = instrumentCount;
            InstrumentType = instrumentType;
            OptionSign = optionSign;
            SpotFactor = spotFactor;
            VolFactor = volFactor;
            RateFactor = rateFactor;
            Strike = strike;
            Maturity = maturity;
            DividendYield = dividendYield;
            Quantity = quantity;
            BaseValue = baseValue;
            TimeDecay = timeDecay;
            Pnl = new double[scenarioCount];
            StoreInstrumentPnl = keepInstrumentPnl ? 1 : 0;
            InstrumentPnl = keepInstrumentPnl ? new double[(long)instrumentCount * scenarioCount] : Array.Empty<double>();
        }

#pragma warning disable CS1591 // the kernel parameters are documented on FullRevaluationKernels
        public int ScenarioCount { get; }
        public int Generator { get; }
        public ulong Seed { get; }
        public uint Stream { get; }
        public uint[] SobolDirections { get; }
        public uint[] SobolShifts { get; }
        public int FactorCount { get; }
        public double[] CholeskyLower { get; }
        public int[] FactorKind { get; }
        public double[] BaseLevel { get; }
        public double[] HalfVariance { get; }
        public int InstrumentCount { get; }
        public int[] InstrumentType { get; }
        public int[] OptionSign { get; }
        public int[] SpotFactor { get; }
        public int[] VolFactor { get; }
        public int[] RateFactor { get; }
        public double[] Strike { get; }
        public double[] Maturity { get; }
        public double[] DividendYield { get; }
        public double[] Quantity { get; }
        public double[] BaseValue { get; }
        public double TimeDecay { get; }
        public int StoreInstrumentPnl { get; }
#pragma warning restore CS1591

        /// <summary>Output: portfolio P&amp;L per scenario, <c>Pnl[s]</c>.</summary>
        public double[] Pnl { get; }

        /// <summary>Output when <see cref="StoreInstrumentPnl"/> is 1: per-unit P&amp;L at <c>[i * ScenarioCount + s]</c>; empty otherwise.</summary>
        public double[] InstrumentPnl { get; }

        /// <summary>A zeroed scratch buffer for <paramref name="workers"/> concurrent scenarios (one row of <see cref="FactorCount"/> each).</summary>
        public double[] CreateScratch(int workers)
        {
            if (workers < 1) throw new ArgumentOutOfRangeException(nameof(workers));
            return new double[(long)workers * FactorCount];
        }

        /// <summary>
        /// Runs the scenarios [<paramref name="from"/>, <paramref name="to"/>) on the calling thread with the scratch row
        /// at <paramref name="scratchOffset"/> of <paramref name="scratch"/>. Ranges run concurrently must use distinct rows.
        /// </summary>
        public void SimulateRange(int from, int to, double[] scratch, int scratchOffset = 0)
        {
            ArgumentNullException.ThrowIfNull(scratch);
            if (from < 0 || to > ScenarioCount || from > to) throw new ArgumentOutOfRangeException(nameof(from));
            if (scratchOffset < 0 || (long)scratchOffset + FactorCount > scratch.Length) throw new ArgumentOutOfRangeException(nameof(scratchOffset));
            for (int s = from; s < to; s++)
                FullRevaluationKernels.SimulateScenario(s, Generator, Seed, Stream, SobolDirections, SobolShifts,
                    FactorCount, CholeskyLower, FactorKind, BaseLevel, HalfVariance,
                    InstrumentCount, InstrumentType, OptionSign, SpotFactor, VolFactor, RateFactor,
                    Strike, Maturity, DividendYield, Quantity, BaseValue, TimeDecay,
                    scratch, scratchOffset, Pnl, ScenarioCount, StoreInstrumentPnl, InstrumentPnl);
        }

        /// <summary>
        /// Runs every scenario on the host, in chunks of <paramref name="chunkSize"/>, one after the other or with
        /// <c>Parallel.For</c>. The outputs do not depend on the chunking.
        /// </summary>
        public void RunOnHost(bool parallel = true, int chunkSize = 1024, int maxDegreeOfParallelism = -1)
        {
            if (chunkSize < 1) throw new ArgumentOutOfRangeException(nameof(chunkSize));
            int chunks = (int)(((long)ScenarioCount + chunkSize - 1) / chunkSize);
            if (!parallel)
            {
                double[] scratch = CreateScratch(1);
                for (int c = 0; c < chunks; c++) RunChunk(c, chunkSize, scratch);
                return;
            }
            var po = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism };
            System.Threading.Tasks.Parallel.For(0, chunks, po, () => CreateScratch(1),
                (c, _, scratch) =>
                {
                    RunChunk(c, chunkSize, scratch);
                    return scratch;
                },
                _ => { });
        }

        void RunChunk(int chunk, int chunkSize, double[] scratch)
        {
            int from = (int)((long)chunk * chunkSize);
            int to = (int)Math.Min((long)from + chunkSize, ScenarioCount);
            SimulateRange(from, to, scratch);
        }
    }

    /// <summary>The simulated P&amp;L of a <see cref="FullRevaluationEngine"/> run and the risk measures built on it.</summary>
    public sealed class MonteCarloVarResult
    {
        readonly double[] _quantity;

        internal MonteCarloVarResult(FullRevaluationBatch batch, bool covarianceRepaired)
        {
            Pnl = batch.Pnl;
            _quantity = batch.Quantity;
            BaseValues = batch.BaseValue;
            CovarianceRepaired = covarianceRepaired;
            if (batch.StoreInstrumentPnl != 0)
            {
                int m = batch.ScenarioCount, n = batch.InstrumentCount;
                var unit = new double[m, n];
                double[] flat = batch.InstrumentPnl;
                for (int i = 0; i < n; i++)
                {
                    int column = i * m;
                    for (int s = 0; s < m; s++) unit[s, i] = flat[column + s];
                }
                InstrumentPnl = unit;
            }
        }

        /// <summary>Portfolio P&amp;L per scenario (gain positive), length M.</summary>
        public double[] Pnl { get; }

        /// <summary>
        /// P&amp;L per unit of each position, M x N (<c>[scenario, position]</c>), or null unless
        /// <see cref="MonteCarloVarOptions.KeepInstrumentPnl"/> was set. <c>Pnl[s] = sum_i q_i InstrumentPnl[s, i]</c>.
        /// </summary>
        public double[,]? InstrumentPnl { get; }

        /// <summary>Value per unit of each position today.</summary>
        public double[] BaseValues { get; }

        /// <summary>Quantity of each position (new array).</summary>
        public double[] Quantities => (double[])_quantity.Clone();

        /// <summary>True when the covariance had to be repaired before factorisation.</summary>
        public bool CovarianceRepaired { get; }

        /// <summary>VaR and ES (positive losses) of the simulated P&amp;L; see <see cref="HistoricalRisk.Measures(double[], double, QuantileMethod)"/>.</summary>
        public VarEs Measures(double confidence, QuantileMethod method = QuantileMethod.Lower) =>
            HistoricalRisk.Measures(Pnl, confidence, method);

        /// <summary>
        /// Euler decomposition of the simulated VaR and ES by position, from the per-unit P&amp;L matrix and the
        /// quantities, by <see cref="HistoricalRisk.Decompose"/>: component ES is exact (the positions' average P&amp;L
        /// in the portfolio's tail scenarios) and component VaR a kernel estimate rescaled to add up; marginal risk is per
        /// unit of quantity. P&amp;L is linear in the quantities, so the Euler allocation applies to non-linear
        /// instruments as well. Requires <see cref="MonteCarloVarOptions.KeepInstrumentPnl"/>.
        /// </summary>
        public RiskDecomposition Decompose(double confidence, double? bandwidth = null)
        {
            if (InstrumentPnl == null)
                throw new InvalidOperationException("Run with KeepInstrumentPnl = true to decompose the risk by position.");
            return HistoricalRisk.Decompose(InstrumentPnl, _quantity, confidence, bandwidth: bandwidth);
        }
    }
}
