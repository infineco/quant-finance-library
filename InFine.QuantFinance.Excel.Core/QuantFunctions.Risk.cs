using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Risk;

namespace InFine.QuantFinance.Excel.Core
{
    /// <summary>
    /// Risk functions: covariance estimation, VaR / ES, their decomposition and the Monte Carlo full-revaluation VaR.
    /// </summary>
    /// <remarks>
    /// Every method takes an optional <see cref="CancellationToken"/>, observed while the arguments are read, between the
    /// stages of the computation, once per row of an EWMA recursion and once per chunk of Monte Carlo scenarios;
    /// cancelling throws <see cref="OperationCanceledException"/> (the only exception these methods let out). The shim
    /// passes the token Excel-DNA cancels when the cell is recalculated with other arguments or deleted.
    /// </remarks>
    public sealed partial class QuantFunctions
    {
        /// <summary>Object kind of the covariance matrices held in the cache.</summary>
        public const string CovKind = "QF.Cov";

        /// <summary>The largest number of Monte Carlo scenarios accepted by <see cref="McVar"/> (memory: 8 bytes each).</summary>
        public const int MaxMonteCarloScenarios = 50_000_000;

        private enum CovMethod { Sample, Ewma, LedoitWolfConstantCorrelation, LedoitWolfIdentity }

        private enum VarMethod { Historical, Parametric, CornishFisher }

        // Number of equal blocks of the simulated P&L used for the standard errors, and the smallest block that is
        // large enough for a 99% quantile to mean something.
        private const int ErrorBlocks = 20;
        private const int MinBlockSize = 100;

        // ---- covariance ---------------------------------------------------------------------------------------------

        /// <summary>
        /// QF.COV(returns, [method], [lambda], [annualisation], [header]) - the N x N covariance matrix of the columns of a
        /// T x N range of returns (rows = dates, oldest first). <c>method</c>: SAMPLE (default, unbiased), EWMA (RiskMetrics
        /// recursion, <c>lambda</c> default 0.94, zero mean, started at the first outer product), LW_CC (Ledoit-Wolf
        /// shrinkage to the constant-correlation target) or LW_ID (to a scaled identity). <c>annualisation</c> is the
        /// number of periods per year the covariance is multiplied by (252 for daily returns); omit it to keep the
        /// per-period covariance. <c>header</c> TRUE if the first row holds the asset names.
        /// </summary>
        public object Cov(object? returns, object? method = null, object? lambda = null, object? annualisation = null,
            object? header = null, CancellationToken cancellationToken = default) =>
            Run("QF.COV", () => ToArray(EstimateCovariance(returns, method, lambda, annualisation, header, cancellationToken).Matrix));

        /// <summary>
        /// QF.COV.OBJ(name, returns, [method], [lambda], [annualisation], [header]) - the same estimate kept in the object
        /// cache; returns the handle (<c>QF.Cov:name#version</c>) to pass to <see cref="McVar"/>. Asset names come from the
        /// header row when <c>header</c> is TRUE, otherwise they are Asset1, Asset2... Identical inputs keep the existing
        /// version, so a recalculation does not invalidate the cells that use the handle.
        /// </summary>
        public object CovObj(object? name, object? returns, object? method = null, object? lambda = null,
            object? annualisation = null, object? header = null, CancellationToken cancellationToken = default) =>
            Run("QF.COV.OBJ", () =>
            {
                string objectName = Arg.Text(name, "name");
                var estimate = EstimateCovariance(returns, method, lambda, annualisation, header, cancellationToken, out var description,
                    out var details, out string fingerprint);
                return Cache.Put(CovKind, objectName, estimate, fingerprint, description, details);
            });

        private CovarianceMatrix EstimateCovariance(object? returns, object? method, object? lambda, object? annualisation,
            object? header, CancellationToken ct) =>
            EstimateCovariance(returns, method, lambda, annualisation, header, ct, out _, out _, out _);

        private CovarianceMatrix EstimateCovariance(object? returns, object? method, object? lambda, object? annualisation,
            object? header, CancellationToken ct, out string description, out List<KeyValuePair<string, object>> details,
            out string fingerprint)
        {
            var kind = ToCovMethod(method);
            double lam = Arg.Double(lambda, "lambda", EwmaCovariance.DefaultLambda);
            bool hasAnnualisation = !Arg.IsMissing(annualisation);
            double periods = hasAnnualisation ? Arg.Double(annualisation, "annualisation") : 1.0;
            bool hasHeader = Arg.Bool(header, "header", false);
            double[,] data = ReadMatrix(returns, "returns", hasHeader, out string[]? names, ct);
            int t = data.GetLength(0), n = data.GetLength(1);
            ct.ThrowIfCancellationRequested();

            double[,] cov;
            double intensity = double.NaN;
            switch (kind)
            {
                case CovMethod.Sample:
                    cov = Covariance.Sample(data);
                    break;
                case CovMethod.Ewma:
                    var ewma = new EwmaCovariance(n, lam);
                    var row = new double[n];
                    for (int i = 0; i < t; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        for (int j = 0; j < n; j++) row[j] = data[i, j];
                        ewma.Update(row);
                    }
                    cov = ewma.GetCovariance();
                    break;
                case CovMethod.LedoitWolfConstantCorrelation:
                    var cc = LedoitWolf.ConstantCorrelation(data);
                    cov = cc.Covariance;
                    intensity = cc.Intensity;
                    break;
                default:
                    var id = LedoitWolf.ScaledIdentity(data);
                    cov = id.Covariance;
                    intensity = id.Intensity;
                    break;
            }
            ct.ThrowIfCancellationRequested();
            if (hasAnnualisation) cov = Covariance.Annualise(cov, periods);
            RequireFiniteMatrix(cov, "the covariance");

            names ??= DefaultNames(n);
            var result = new CovarianceMatrix(cov, names);
            description = $"{n}x{n} {kind} covariance of {t} observations";
            details = new List<KeyValuePair<string, object>>
            {
                new("Method", kind.ToString()),
                new("Assets", (double)n),
                new("Observations", (double)t),
                new("Annualisation", hasAnnualisation ? periods : "none"),
                new("Asset names", string.Join(", ", names)),
            };
            if (kind == CovMethod.Ewma) details.Add(new("Lambda", lam));
            if (!double.IsNaN(intensity)) details.Add(new("Shrinkage intensity", intensity));
            fingerprint = RiskCalls.Digest("cov", kind.ToString(), kind == CovMethod.Ewma ? lam : 0.0, periods, names, data);
            return result;
        }

        // ---- VaR / ES -----------------------------------------------------------------------------------------------

        /// <summary>
        /// QF.VAR(data, alpha, [method], [horizon], [positions]) - Value-at-Risk at confidence <c>alpha</c> (0.99), as a
        /// positive loss. Without <c>positions</c>, <c>data</c> is a sample of P&amp;L (gain positive; any shape). With
        /// <c>positions</c> (one currency exposure per asset), <c>data</c> is a T x N range of asset returns and the P&amp;L of
        /// scenario t is the sum of the exposures times the returns. <c>method</c>: HISTORICAL (default; empirical quantile),
        /// PARAMETRIC (normal, zero mean, standard deviation of the sample P&amp;L) or CORNISHFISHER (normal quantile corrected
        /// for the sample skewness and excess kurtosis; #NUM! outside its domain of validity). <c>horizon</c> in periods
        /// (default 1) applies the square-root-of-time rule.
        /// </summary>
        public object Var(object? data, object? alpha, object? method = null, object? horizon = null, object? positions = null,
            CancellationToken cancellationToken = default) =>
            Run("QF.VAR", () => Finite(VarEsOf(data, alpha, method, horizon, positions, cancellationToken).Var));

        /// <summary>QF.ES(data, alpha, [method], [horizon], [positions]) - Expected Shortfall; same arguments as <see cref="Var"/>.</summary>
        public object Es(object? data, object? alpha, object? method = null, object? horizon = null, object? positions = null,
            CancellationToken cancellationToken = default) =>
            Run("QF.ES", () => Finite(VarEsOf(data, alpha, method, horizon, positions, cancellationToken).ExpectedShortfall));

        private static VarEs VarEsOf(object? data, object? alpha, object? method, object? horizon, object? positions, CancellationToken ct)
        {
            double confidence = Arg.Double(alpha, "alpha");
            var kind = ToVarMethod(method);
            double h = Arg.Double(horizon, "horizon", 1.0);
            double[] pnl;
            double[,]? returns = null;
            double[]? exposures = null;
            if (Arg.IsMissing(positions))
            {
                pnl = Arg.Doubles(data, "data");
            }
            else
            {
                exposures = Arg.Doubles(positions, "positions");
                returns = ReadMatrix(data, "data", false, out _, ct);
                RequirePositions(exposures, returns.GetLength(1));
                pnl = HistoricalRisk.PortfolioPnl(returns, exposures);
            }
            ct.ThrowIfCancellationRequested();
            if (!(h > 0.0) || double.IsInfinity(h)) throw new ArgumentOutOfRangeException(nameof(horizon), "The horizon must be positive and finite.");

            switch (kind)
            {
                case VarMethod.Historical:
                    var historical = HistoricalRisk.Measures(pnl, confidence);
                    double scale = Math.Sqrt(h);
                    return new VarEs(historical.Var * scale, historical.ExpectedShortfall * scale);
                case VarMethod.Parametric:
                    return ParametricRisk.Normal(SampleMoments(pnl, out _, out _), confidence, 0.0, h);
                default:
                    double sigma = SampleMoments(pnl, out double skewness, out double excessKurtosis);
                    return ParametricRisk.CornishFisher(sigma, skewness, excessKurtosis, confidence, 0.0, h);
            }
        }

        private static readonly string[] DecompHeaders =
            { "Asset", "Marginal VaR", "Component VaR", "% VaR", "Marginal ES", "Component ES", "% ES" };

        /// <summary>
        /// QF.VAR.DECOMP(returns, positions, alpha, [method], [horizon], [header]) - Euler decomposition of VaR and ES of a
        /// portfolio over its assets: a header row, one row per asset (marginal VaR, component VaR, share of the VaR, then the
        /// same for ES) and a Total row. Marginal risk is per unit of exposure; the components add up to the total. <c>method</c>
        /// is HISTORICAL (default; the historical VaR component is a kernel estimate, noisy on short samples) or PARAMETRIC
        /// (delta-normal, exact). <c>header</c> TRUE if the first row of <c>returns</c> holds the asset names.
        /// </summary>
        public object VarDecomp(object? returns, object? positions, object? alpha, object? method = null, object? horizon = null,
            object? header = null, CancellationToken cancellationToken = default) =>
            Run("QF.VAR.DECOMP", () =>
            {
                double confidence = Arg.Double(alpha, "alpha");
                var kind = ToVarMethod(method, allowCornishFisher: false);
                double h = Arg.Double(horizon, "horizon", 1.0);
                if (!(h > 0.0) || double.IsInfinity(h)) throw new ArgumentOutOfRangeException(nameof(horizon), "The horizon must be positive and finite.");
                bool hasHeader = Arg.Bool(header, "header", false);
                var data = ReadMatrix(returns, "returns", hasHeader, out string[]? names, cancellationToken);
                var exposures = Arg.Doubles(positions, "positions");
                int n = data.GetLength(1);
                RequirePositions(exposures, n);
                names ??= DefaultNames(n);
                cancellationToken.ThrowIfCancellationRequested();

                RiskDecomposition d;
                if (kind == VarMethod.Parametric)
                {
                    d = ParametricRisk.Decompose(exposures, Covariance.Sample(data), confidence, null, h);
                }
                else
                {
                    // P&L, VaR, ES and marginal risk all scale with sqrt(h): scale the returns.
                    double scale = Math.Sqrt(h);
                    if (scale != 1.0)
                        for (int i = 0; i < data.GetLength(0); i++)
                            for (int j = 0; j < n; j++) data[i, j] *= scale;
                    d = HistoricalRisk.Decompose(data, exposures, confidence);
                }
                double[] pctVar = d.PercentVar, pctEs = d.PercentEs;

                var result = new object[n + 2, DecompHeaders.Length];
                for (int j = 0; j < DecompHeaders.Length; j++) result[0, j] = DecompHeaders[j];
                for (int i = 0; i < n; i++)
                {
                    result[i + 1, 0] = names[i];
                    result[i + 1, 1] = Finite(d.MarginalVar[i]);
                    result[i + 1, 2] = Finite(d.ComponentVar[i]);
                    result[i + 1, 3] = Finite(pctVar[i]);
                    result[i + 1, 4] = Finite(d.MarginalEs[i]);
                    result[i + 1, 5] = Finite(d.ComponentEs[i]);
                    result[i + 1, 6] = Finite(pctEs[i]);
                }
                result[n + 1, 0] = "Total";
                result[n + 1, 1] = "";
                result[n + 1, 2] = Finite(d.Total.Var);
                result[n + 1, 3] = 1.0;
                result[n + 1, 4] = "";
                result[n + 1, 5] = Finite(d.Total.ExpectedShortfall);
                result[n + 1, 6] = 1.0;
                return result;
            });

        // ---- Monte Carlo VaR ----------------------------------------------------------------------------------------

        /// <summary>
        /// QF.MCVAR(cov, spots, vols, positions, alpha, scenarios, [seed], [generator], [rate], [horizon]) - Monte Carlo
        /// Value-at-Risk and Expected Shortfall of a portfolio of shares and European options, by full revaluation of every
        /// position in each of <c>scenarios</c> correlated scenarios. Returns a 3 x 3 block: a header row, VaR and ES with
        /// their standard errors.
        /// <list type="bullet">
        /// <item><c>cov</c>: a QF.COV.OBJ handle or an N x N range, the per-period covariance of the <b>log</b> returns of the N assets.</item>
        /// <item><c>spots</c>, <c>vols</c>: N current spot prices and N implied volatilities (decimals; the volatility of an asset is held fixed, as is <c>rate</c>, default 0, the continuously compounded rate used to price options).</item>
        /// <item><c>positions</c>: a table with one row per position and the columns asset (name from the covariance or 1-based number), type (SPOT, CALL or PUT), quantity (negative is short), strike, maturity in years and optionally dividend yield; strike and maturity are not read for SPOT. A first row of titles is skipped.</item>
        /// <item><c>generator</c>: PHILOX (default; pseudo-random, independent scenarios) or SOBOL (scrambled Sobol, quasi-random). <c>seed</c> (default 1) fixes the scenarios: the result is identical from run to run, whatever the number of threads. <c>horizon</c> is in periods of the covariance (default 1).</item>
        /// </list>
        /// The standard errors come from splitting the scenarios in 20 blocks and are shown as "n/a" when blocks would be
        /// smaller than 100 scenarios; they describe the Philox sampling error, and are only indicative for SOBOL.
        /// </summary>
        public object McVar(object? cov, object? spots, object? vols, object? positions, object? alpha, object? scenarios,
            object? seed = null, object? generator = null, object? rate = null, object? horizon = null,
            CancellationToken cancellationToken = default, int maxDegreeOfParallelism = -1) =>
            Run("QF.MCVAR", () =>
            {
                CancellationToken ct = cancellationToken;
                var covariance = ReadCovariance(cov, ct);
                int n = covariance.Dimension;
                double[] spotLevels = Arg.Doubles(spots, "spots");
                double[] volLevels = Arg.Doubles(vols, "vols");
                if (spotLevels.Length != n || volLevels.Length != n)
                    throw new XlException(XlError.Value,
                        $"spots and vols must have one value per asset of the covariance ({n}); got {spotLevels.Length} and {volLevels.Length}");
                for (int i = 0; i < n; i++)
                    if (!(spotLevels[i] > 0.0))
                        throw new XlException(XlError.Value, $"spots[{i + 1}]: a spot price must be positive");
                double confidence = Arg.Double(alpha, "alpha");
                int count = Arg.Int(scenarios, "scenarios");
                if (count < 1 || count > MaxMonteCarloScenarios)
                    throw new XlException(XlError.Num, $"scenarios: {count} is outside [1, {MaxMonteCarloScenarios}]");
                ulong seedValue = ToSeed(seed);
                var gen = ToScenarioGenerator(generator);
                double r = Arg.Double(rate, "rate", 0.0);
                double h = Arg.Double(horizon, "horizon", 1.0);
                var book = ReadPositions(positions, covariance.Names, ct);
                ct.ThrowIfCancellationRequested();

                // Factors: the N spots (log-normal moves, covariance given), the N volatilities and the rate (held fixed:
                // zero variance).
                int f = 2 * n + 1;
                var factors = new RiskFactor[f];
                for (int i = 0; i < n; i++)
                {
                    factors[i] = new RiskFactor(covariance.Names[i], spotLevels[i], RiskFactorKind.LogNormal);
                    factors[n + i] = new RiskFactor("vol " + covariance.Names[i], volLevels[i], RiskFactorKind.Additive);
                }
                factors[2 * n] = new RiskFactor("rate", r, RiskFactorKind.Additive);
                var full = new double[f, f];
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++) full[i, j] = covariance.Matrix[i, j];

                var engine = new FullRevaluationEngine(factors, full, book);
                var options = new MonteCarloVarOptions { Scenarios = count, Seed = seedValue, Generator = gen, Horizon = h };
                double[] pnl = SimulatePnl(engine, options, maxDegreeOfParallelism, ct);

                var total = HistoricalRisk.Measures(pnl, confidence);
                object varError = "n/a", esError = "n/a";
                if (count / ErrorBlocks >= MinBlockSize)
                {
                    int size = count / ErrorBlocks;
                    var vars = new double[ErrorBlocks];
                    var shortfalls = new double[ErrorBlocks];
                    var block = new double[size];
                    for (int b = 0; b < ErrorBlocks; b++)
                    {
                        ct.ThrowIfCancellationRequested();
                        Array.Copy(pnl, (long)b * size, block, 0, size);
                        var m = HistoricalRisk.Measures(block, confidence);
                        vars[b] = m.Var;
                        shortfalls[b] = m.ExpectedShortfall;
                    }
                    varError = Finite(StandardDeviation(vars) / Math.Sqrt(ErrorBlocks));
                    esError = Finite(StandardDeviation(shortfalls) / Math.Sqrt(ErrorBlocks));
                }

                return new object[,]
                {
                    { "", "Value", "Std error" },
                    { "VaR", Finite(total.Var), varError },
                    { "ES", Finite(total.ExpectedShortfall), esError },
                };
            });

        /// <summary>
        /// Runs the scenarios in chunks on the thread pool (<c>Parallel.For</c>), checking the token before each chunk. Every
        /// scenario is a pure function of its index, so the P&amp;L does not depend on the chunking or on the threads.
        /// </summary>
        private static double[] SimulatePnl(FullRevaluationEngine engine, MonteCarloVarOptions options, int maxDegreeOfParallelism,
            CancellationToken ct)
        {
            var batch = engine.CreateBatch(options);
            const int chunkSize = 2048;
            int chunks = (int)(((long)batch.ScenarioCount + chunkSize - 1) / chunkSize);
            var parallel = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = maxDegreeOfParallelism };
            Parallel.For(0, chunks, parallel, () => batch.CreateScratch(1),
                (chunk, _, scratch) =>
                {
                    ct.ThrowIfCancellationRequested();
                    int from = chunk * chunkSize;
                    batch.SimulateRange(from, Math.Min(from + chunkSize, batch.ScenarioCount), scratch);
                    return scratch;
                },
                _ => { });
            ct.ThrowIfCancellationRequested();
            return batch.Pnl;
        }

        private CovarianceMatrix ReadCovariance(object? cov, CancellationToken ct)
        {
            if (cov is object[,] { Length: 1 } single) cov = single[single.GetLowerBound(0), single.GetLowerBound(1)];
            if (cov is string handle) return Cache.Get<CovarianceMatrix>(Arg.Text(handle, "cov"));
            var matrix = ReadMatrix(cov, "cov", false, out _, ct);
            int n = matrix.GetLength(0);
            if (matrix.GetLength(1) != n)
                throw new XlException(XlError.Value, $"cov: expected a square matrix or a QF.COV.OBJ handle, got a {n}x{matrix.GetLength(1)} range");
            return new CovarianceMatrix(matrix, DefaultNames(n));
        }

        private static List<PortfolioPosition> ReadPositions(object? positions, string[] names, CancellationToken ct)
        {
            Arg.ThrowIfError(positions, "positions");
            if (positions is not object[,] table)
                throw new XlException(XlError.Value, "positions: expected a table with columns asset, type, quantity, strike, maturity, [dividend yield]");
            int rows = table.GetLength(0), columns = table.GetLength(1);
            if (columns < 3)
                throw new XlException(XlError.Value, $"positions: at least 3 columns are needed (asset, type, quantity), got {columns}");
            int r0 = table.GetLowerBound(0), c0 = table.GetLowerBound(1);
            object? Cell(int i, int j) => j < columns ? table[r0 + i, c0 + j] : null;

            int first = Cell(0, 2) is string quantityText && !double.TryParse(quantityText, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? 1 : 0;
            var result = new List<PortfolioPosition>();
            for (int i = first; i < rows; i++)
            {
                ct.ThrowIfCancellationRequested();
                string at(string what) => $"positions[row {i + 1}, {what}]";
                int asset = ToAssetIndex(Cell(i, 0), names, at("asset"));
                double quantity = Arg.Double(Cell(i, 2), at("quantity"));
                string type = Names.Normalize(Arg.Text(Cell(i, 1), at("type")));
                if (type is "SPOT" or "STOCK" or "SHARE" or "EQUITY" or "LINEAR")
                {
                    result.Add(new SpotPosition(asset, quantity));
                    continue;
                }
                var optionType = Names.ToOptionType(Cell(i, 1), at("type"));
                result.Add(new EuropeanOptionPosition(optionType, asset, names.Length + asset, 2 * names.Length,
                    Arg.Double(Cell(i, 3), at("strike")), Arg.Double(Cell(i, 4), at("maturity")), quantity,
                    Arg.Double(Cell(i, 5), at("dividend yield"), 0.0)));
            }
            if (result.Count == 0) throw new XlException(XlError.Value, "positions: the table has no position");
            return result;
        }

        private static int ToAssetIndex(object? value, string[] names, string name)
        {
            if (value is object[,] { Length: 1 } single) value = single[single.GetLowerBound(0), single.GetLowerBound(1)];
            if (value is string text && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                for (int i = 0; i < names.Length; i++)
                    if (string.Equals(names[i], text.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
                throw new XlException(XlError.Value, $"{name}: unknown asset '{text}'. Expected one of: {string.Join(", ", names)}");
            }
            int number = Arg.Int(value, name);
            if (number < 1 || number > names.Length)
                throw new XlException(XlError.Value, $"{name}: asset number {number} is outside [1, {names.Length}]");
            return number - 1;
        }

        // ---- argument helpers ---------------------------------------------------------------------------------------

        private static CovMethod ToCovMethod(object? value)
        {
            if (Arg.IsMissing(value)) return CovMethod.Sample;
            return Names.Normalize(Arg.Text(value, "method")) switch
            {
                "SAMPLE" or "HISTORICAL" or "EQUAL" => CovMethod.Sample,
                "EWMA" or "RISKMETRICS" => CovMethod.Ewma,
                "LWCC" or "LW" or "LEDOITWOLF" or "CC" => CovMethod.LedoitWolfConstantCorrelation,
                "LWID" or "LWIDENTITY" or "IDENTITY" => CovMethod.LedoitWolfIdentity,
                _ => throw new XlException(XlError.Value, $"method: unknown covariance method '{value}'. Expected one of: SAMPLE, EWMA, LW_CC, LW_ID"),
            };
        }

        private static VarMethod ToVarMethod(object? value, bool allowCornishFisher = true)
        {
            if (Arg.IsMissing(value)) return VarMethod.Historical;
            var method = Names.Normalize(Arg.Text(value, "method")) switch
            {
                "HISTORICAL" or "HIST" => VarMethod.Historical,
                "PARAMETRIC" or "NORMAL" or "DELTANORMAL" or "GAUSSIAN" => VarMethod.Parametric,
                "CORNISHFISHER" or "CF" => VarMethod.CornishFisher,
                _ => throw new XlException(XlError.Value, $"method: unknown VaR method '{value}'. Expected one of: HISTORICAL, PARAMETRIC, CORNISHFISHER"),
            };
            if (method == VarMethod.CornishFisher && !allowCornishFisher)
                throw new XlException(XlError.Value, "method: CORNISHFISHER is not available for the decomposition (use HISTORICAL or PARAMETRIC)");
            return method;
        }

        private static ScenarioGenerator ToScenarioGenerator(object? value)
        {
            if (Arg.IsMissing(value)) return ScenarioGenerator.Philox;
            return Names.Normalize(Arg.Text(value, "generator")) switch
            {
                "PHILOX" or "PSEUDO" or "RANDOM" => ScenarioGenerator.Philox,
                "SOBOL" or "SCRAMBLEDSOBOL" or "QMC" => ScenarioGenerator.ScrambledSobol,
                _ => throw new XlException(XlError.Value, $"generator: unknown generator '{value}'. Expected PHILOX or SOBOL"),
            };
        }

        private static ulong ToSeed(object? value)
        {
            if (Arg.IsMissing(value)) return 1UL;
            double d = Arg.Double(value, "seed");
            if (d < 0.0 || d != Math.Floor(d) || d > 9007199254740992.0)
                throw new XlException(XlError.Value, $"seed: {d:R} must be a non-negative integer (at most 2^53)");
            return (ulong)d;
        }

        private static void RequirePositions(double[] exposures, int assets)
        {
            if (exposures.Length != assets)
                throw new XlException(XlError.Value,
                    $"positions: {exposures.Length} exposures for {assets} asset columns of returns; they must match");
        }

        private static string[] DefaultNames(int n)
        {
            var names = new string[n];
            for (int i = 0; i < n; i++) names[i] = "Asset" + (i + 1).ToString(CultureInfo.InvariantCulture);
            return names;
        }

        /// <summary>
        /// A T x N block of numbers (a scalar or a vector is one column / one row); with <paramref name="header"/> the first
        /// row is read as asset names. Empty cells are errors, as for every range of the add-in.
        /// </summary>
        private static double[,] ReadMatrix(object? value, string name, bool header, out string[]? names, CancellationToken ct)
        {
            Arg.ThrowIfError(value, name);
            object[,] range;
            if (value is object[,] given)
            {
                range = given;
            }
            else
            {
                range = new object[1, 1];
                range[0, 0] = value!;
            }
            int rows = range.GetLength(0), columns = range.GetLength(1);
            int r0 = range.GetLowerBound(0), c0 = range.GetLowerBound(1);
            names = null;
            int first = 0;
            if (header)
            {
                names = new string[columns];
                for (int j = 0; j < columns; j++) names[j] = Arg.Text(range[r0, c0 + j], $"{name} header[{j + 1}]");
                first = 1;
            }
            if (rows - first < 1) throw new XlException(XlError.Value, $"{name}: the range holds no data row");
            var result = new double[rows - first, columns];
            for (int i = first; i < rows; i++)
            {
                ct.ThrowIfCancellationRequested();
                for (int j = 0; j < columns; j++)
                    result[i - first, j] = Arg.Double(range[r0 + i, c0 + j], $"{name}[{i - first + 1},{j + 1}]");
            }
            return result;
        }

        private static object[,] ToArray(double[,] matrix)
        {
            int rows = matrix.GetLength(0), columns = matrix.GetLength(1);
            var result = new object[rows, columns];
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < columns; j++) result[i, j] = matrix[i, j];
            return result;
        }

        private static void RequireFiniteMatrix(double[,] matrix, string what)
        {
            foreach (double d in matrix)
                if (!double.IsFinite(d)) throw new XlException(XlError.Num, $"{what} is not finite");
        }

        /// <summary>Sample standard deviation (n - 1) and the population skewness and excess kurtosis of a series.</summary>
        private static double SampleMoments(double[] values, out double skewness, out double excessKurtosis)
        {
            int n = values.Length;
            if (n < 4) throw new ArgumentException("At least four observations are needed to estimate a standard deviation, skewness and kurtosis.");
            double mean = 0.0;
            foreach (double v in values) mean += v;
            mean /= n;
            double m2 = 0.0, m3 = 0.0, m4 = 0.0;
            foreach (double v in values)
            {
                double d = v - mean, d2 = d * d;
                m2 += d2;
                m3 += d2 * d;
                m4 += d2 * d2;
            }
            m2 /= n;
            m3 /= n;
            m4 /= n;
            if (!(m2 > 0.0)) throw new ArgumentException("The P&L sample has zero variance.");
            skewness = m3 / Math.Pow(m2, 1.5);
            excessKurtosis = m4 / (m2 * m2) - 3.0;
            return Math.Sqrt(m2 * n / (n - 1));
        }

        private static double StandardDeviation(double[] values)
        {
            double mean = 0.0;
            foreach (double v in values) mean += v;
            mean /= values.Length;
            double sum = 0.0;
            foreach (double v in values) sum += (v - mean) * (v - mean);
            return Math.Sqrt(sum / (values.Length - 1));
        }
    }
}
