using System;
using System.Linq;
using System.Threading;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Risk;
using Xunit;

namespace InFine.QuantFinance.Excel.Core.Tests;

[Collection("QuantFunctions")]
public class RiskFunctionTests
{
    private const int Assets = 3;
    private const int Dates = 400;

    private readonly ObjectCache _cache = new();
    private readonly QuantFunctions _qf;

    public RiskFunctionTests()
    {
        _qf = new QuantFunctions(_cache);
    }

    // Deterministic correlated daily returns (about 1% volatility) with some fat tails.
    private static readonly double[,] Returns = MakeReturns(Dates, Assets, seed: 12345);

    private static double[,] MakeReturns(int t, int n, int seed)
    {
        var rng = new System.Random(seed);
        var result = new double[t, n];
        for (int i = 0; i < t; i++)
        {
            double common = Gaussian(rng);
            for (int j = 0; j < n; j++)
            {
                double e = Gaussian(rng);
                if (rng.NextDouble() < 0.03) e *= 3.0;
                result[i, j] = 0.01 * (0.2 * common * (j + 1) + e) * (1.0 + 0.2 * j);
            }
        }
        return result;
    }

    private static double Gaussian(System.Random rng) =>
        Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * Math.Cos(2.0 * Math.PI * rng.NextDouble());

    private static object[,] Range(double[,] values, string[]? header = null)
    {
        int rows = values.GetLength(0), columns = values.GetLength(1), offset = header == null ? 0 : 1;
        var result = new object[rows + offset, columns];
        for (int j = 0; header != null && j < columns; j++) result[0, j] = header[j];
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < columns; j++) result[i + offset, j] = values[i, j];
        return result;
    }

    private static double[,] Numbers(object result)
    {
        var array = T.Array(result);
        var numbers = new double[array.GetLength(0), array.GetLength(1)];
        for (int i = 0; i < numbers.GetLength(0); i++)
            for (int j = 0; j < numbers.GetLength(1); j++) numbers[i, j] = Assert.IsType<double>(array[i, j]);
        return numbers;
    }

    private static void AssertMatrix(double[,] expected, double[,] actual, double tolerance = 1e-14)
    {
        Assert.Equal(expected.GetLength(0), actual.GetLength(0));
        Assert.Equal(expected.GetLength(1), actual.GetLength(1));
        for (int i = 0; i < expected.GetLength(0); i++)
            for (int j = 0; j < expected.GetLength(1); j++)
                Assert.Equal(expected[i, j], actual[i, j], tolerance);
    }

    private static object[,] Exposures => T.Column(1_000_000.0, -400_000.0, 700_000.0);

    private static double[] ExposureArray => [1_000_000.0, -400_000.0, 700_000.0];

    // ---- covariance ---------------------------------------------------------------------------------------------

    [Fact]
    public void CovDefaultsToTheUnbiasedSampleCovariance()
    {
        AssertMatrix(Covariance.Sample(Returns), Numbers(_qf.Cov(Range(Returns))));
        AssertMatrix(Covariance.Sample(Returns), Numbers(_qf.Cov(Range(Returns), "sample")));
    }

    [Fact]
    public void CovEwmaMatchesTheLibraryRecursion()
    {
        AssertMatrix(EwmaCovariance.Estimate(Returns, 0.94), Numbers(_qf.Cov(Range(Returns), "EWMA")));
        AssertMatrix(EwmaCovariance.Estimate(Returns, 0.9), Numbers(_qf.Cov(Range(Returns), "ewma", 0.9)));
    }

    [Fact]
    public void CovLedoitWolfMatchesTheLibraryShrinkage()
    {
        AssertMatrix(LedoitWolf.ConstantCorrelation(Returns).Covariance, Numbers(_qf.Cov(Range(Returns), "LW_CC")));
        AssertMatrix(LedoitWolf.ScaledIdentity(Returns).Covariance, Numbers(_qf.Cov(Range(Returns), "LW_ID")));
    }

    [Fact]
    public void CovAnnualisationMultipliesByThePeriodsPerYear()
    {
        AssertMatrix(Covariance.Annualise(Covariance.Sample(Returns), 252), Numbers(_qf.Cov(Range(Returns), "SAMPLE", null, 252.0)));
    }

    [Fact]
    public void CovSkipsTheHeaderRowOnRequest()
    {
        var ranged = Range(Returns, ["A", "B", "C"]);
        AssertMatrix(Covariance.Sample(Returns), Numbers(_qf.Cov(ranged, header: true)));
        // Without the option the titles are not numbers.
        Assert.Equal(XlError.Value, _qf.Cov(ranged));
    }

    [Fact]
    public void CovRejectsBadArgumentsAsValueErrors()
    {
        Assert.Equal(XlError.Value, _qf.Cov(Range(Returns), "GARCH"));
        Assert.Contains("unknown covariance method", ErrorLog.Last);
        var withHole = Range(Returns);
        withHole[10, 1] = null!;
        Assert.Equal(XlError.Value, _qf.Cov(withHole));
        Assert.Contains("returns[11,2]", ErrorLog.Last);
        Assert.Equal(XlError.Value, _qf.Cov(Range(Returns), "EWMA", "abc"));
        Assert.Equal(XlError.Ref, _qf.Cov(XlError.Ref));
    }

    [Fact]
    public void CovFailuresOfTheNumbersAreNumErrors()
    {
        Assert.Equal(XlError.Num, _qf.Cov(T.Row(0.01, 0.02, 0.03)));                     // one date: no covariance
        Assert.Equal(XlError.Num, _qf.Cov(Range(Returns), "EWMA", 1.5));                 // lambda outside (0,1)
        Assert.Equal(XlError.Num, _qf.Cov(Range(Returns), "SAMPLE", null, -252.0));      // negative annualisation
    }

    [Fact]
    public void CovObjCachesTheMatrixWithItsAssetNames()
    {
        string handle = T.Text(_qf.CovObj("RISK", Range(Returns, ["Equity", "Bond", "Gold"]), "EWMA", 0.97, 252.0, true));
        Assert.Equal("QF.Cov:RISK#1", handle);
        var stored = _cache.Get<CovarianceMatrix>(handle);
        Assert.Equal(["Equity", "Bond", "Gold"], stored.Names);
        AssertMatrix(Covariance.Annualise(EwmaCovariance.Estimate(Returns, 0.97), 252), stored.Matrix);

        // Identical inputs keep the version; different ones replace the object and make the old handle stale.
        Assert.Equal(handle, T.Text(_qf.CovObj("RISK", Range(Returns, ["Equity", "Bond", "Gold"]), "EWMA", 0.97, 252.0, true)));
        string changed = T.Text(_qf.CovObj("RISK", Range(Returns, ["Equity", "Bond", "Gold"]), "EWMA", 0.96, 252.0, true));
        Assert.Equal("QF.Cov:RISK#2", changed);
        Assert.Equal(XlError.Ref, _qf.McVar(handle, T.Column(100.0, 100.0, 100.0), T.Column(0.2, 0.2, 0.2),
            SpotBook(), 0.99, 1000));

        var info = T.Array(_qf.ObjInfo(changed));
        Assert.Contains(Enumerable.Range(0, info.GetLength(0)), i => Equals(info[i, 0], "Asset names") && Equals(info[i, 1], "Equity, Bond, Gold"));
    }

    [Fact]
    public void CovObjUsesDefaultNamesWithoutAHeader()
    {
        string handle = T.Text(_qf.CovObj("D", Range(Returns)));
        Assert.Equal(["Asset1", "Asset2", "Asset3"], _cache.Get<CovarianceMatrix>(handle).Names);
        Assert.Equal(XlError.Value, _qf.CovObj("", Range(Returns)));
    }

    [Fact]
    public void CovHonoursAnAlreadyCancelledToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => _qf.Cov(Range(Returns), "EWMA", cancellationToken: cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => _qf.CovObj("X", Range(Returns), cancellationToken: cts.Token));
        Assert.Equal(0, _cache.Count);
    }

    // ---- VaR / ES -----------------------------------------------------------------------------------------------

    private static double[] Pnl => HistoricalRisk.PortfolioPnl(Returns, ExposureArray);

    [Fact]
    public void HistoricalVarAndEsMatchTheLibraryOnAPnlSample()
    {
        var expected = HistoricalRisk.Measures(Pnl, 0.99);
        var column = T.Column(Pnl.Cast<object>().ToArray());
        Assert.Equal(expected.Var, T.Num(_qf.Var(column, 0.99)), 12);
        Assert.Equal(expected.ExpectedShortfall, T.Num(_qf.Es(column, 0.99, "HISTORICAL")), 12);
        // A row, or any shape, is the same sample.
        Assert.Equal(expected.Var, T.Num(_qf.Var(T.Row(Pnl.Cast<object>().ToArray()), 0.99)), 12);
    }

    [Fact]
    public void HistoricalVarAndEsWithPositionsMatchTheLibraryOnReturns()
    {
        var expected = HistoricalRisk.Measures(Returns, ExposureArray, 0.975);
        Assert.Equal(expected.Var, T.Num(_qf.Var(Range(Returns), 0.975, null, null, Exposures)), 12);
        Assert.Equal(expected.ExpectedShortfall, T.Num(_qf.Es(Range(Returns), 0.975, "historical", 1.0, Exposures)), 12);
    }

    [Fact]
    public void HorizonScalesWithTheSquareRootOfTime()
    {
        double one = T.Num(_qf.Var(Range(Returns), 0.99, "HISTORICAL", 1.0, Exposures));
        Assert.Equal(one * Math.Sqrt(10.0), T.Num(_qf.Var(Range(Returns), 0.99, "HISTORICAL", 10.0, Exposures)), 9);
        var parametric = ParametricRisk.DeltaNormal(ExposureArray, Covariance.Sample(Returns), 0.99, null, 10.0);
        Assert.Equal(parametric.Var, T.Num(_qf.Var(Range(Returns), 0.99, "PARAMETRIC", 10.0, Exposures)), 8);
        Assert.Equal(parametric.ExpectedShortfall, T.Num(_qf.Es(Range(Returns), 0.99, "PARAMETRIC", 10.0, Exposures)), 8);
    }

    [Fact]
    public void ParametricVarOfAPnlSampleIsTheNormalVarOfItsStandardDeviation()
    {
        double[] pnl = Pnl;
        double mean = pnl.Average();
        double sigma = Math.Sqrt(pnl.Sum(x => (x - mean) * (x - mean)) / (pnl.Length - 1));
        var expected = ParametricRisk.Normal(sigma, 0.99);
        var column = T.Column(pnl.Cast<object>().ToArray());
        Assert.Equal(expected.Var, T.Num(_qf.Var(column, 0.99, "PARAMETRIC")), 8);
        Assert.Equal(expected.ExpectedShortfall, T.Num(_qf.Es(column, 0.99, "normal")), 8);
    }

    [Fact]
    public void CornishFisherUsesTheSampleSkewnessAndKurtosis()
    {
        double[] pnl = Pnl;
        int n = pnl.Length;
        double mean = pnl.Average();
        double m2 = pnl.Sum(x => Math.Pow(x - mean, 2)) / n, m3 = pnl.Sum(x => Math.Pow(x - mean, 3)) / n, m4 = pnl.Sum(x => Math.Pow(x - mean, 4)) / n;
        var expected = ParametricRisk.CornishFisher(Math.Sqrt(m2 * n / (n - 1)), m3 / Math.Pow(m2, 1.5), m4 / (m2 * m2) - 3.0, 0.99);
        var column = T.Column(pnl.Cast<object>().ToArray());
        Assert.Equal(expected.Var, T.Num(_qf.Var(column, 0.99, "CornishFisher")), 8);
        Assert.Equal(expected.ExpectedShortfall, T.Num(_qf.Es(column, 0.99, "CF")), 8);
    }

    [Fact]
    public void VarArgumentErrorsAreValueErrorsAndDomainErrorsAreNumErrors()
    {
        var column = T.Column(Pnl.Cast<object>().ToArray());
        Assert.Equal(XlError.Value, _qf.Var(column, "high"));
        Assert.Equal(XlError.Value, _qf.Var(column, 0.99, "MAGIC"));
        Assert.Equal(XlError.Value, _qf.Var(Range(Returns), 0.99, null, null, T.Column(1.0, 2.0)));   // 2 exposures, 3 assets
        Assert.Contains("must match", ErrorLog.Last);
        Assert.Equal(XlError.Value, _qf.Var(Range(Returns), 0.99, null, null, T.Column(1.0, 2.0, "x")));
        Assert.Equal(XlError.Value, _qf.Var(null, 0.99));
        Assert.Equal(XlError.Num, _qf.Var(column, 1.5));
        Assert.Equal(XlError.Num, _qf.Var(column, 0.99, null, -1.0));
        Assert.Equal(XlError.Num, _qf.Var(T.Column(1.0, 1.0, 1.0, 1.0, 1.0), 0.99, "PARAMETRIC"));    // zero variance
        Assert.Equal(XlError.Num, _qf.Var(T.Column(1.0, 2.0), 0.99, "CF"));                          // too short
        Assert.Equal(XlError.Num, _qf.Es(column, 0.99, "PARAMETRIC", double.PositiveInfinity));
        Assert.Equal(XlError.DivZero, _qf.Var(XlError.DivZero, 0.99));
    }

    [Fact]
    public void VarHonoursAnAlreadyCancelledToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => _qf.Var(Range(Returns), 0.99, null, null, Exposures, cts.Token));
    }

    // ---- decomposition ------------------------------------------------------------------------------------------

    [Fact]
    public void ParametricDecompositionMatchesTheLibraryAndAddsUp()
    {
        var table = T.Array(_qf.VarDecomp(Range(Returns, ["Equity", "Bond", "Gold"]), Exposures, 0.99, "PARAMETRIC", 5.0, true));
        var expected = ParametricRisk.Decompose(ExposureArray, Covariance.Sample(Returns), 0.99, null, 5.0);
        Assert.Equal((Assets + 2, 7), (table.GetLength(0), table.GetLength(1)));
        Assert.Equal(["Asset", "Marginal VaR", "Component VaR", "% VaR", "Marginal ES", "Component ES", "% ES"],
            Enumerable.Range(0, 7).Select(j => (string)table[0, j]));
        string[] names = ["Equity", "Bond", "Gold"];
        for (int i = 0; i < Assets; i++)
        {
            Assert.Equal(names[i], table[i + 1, 0]);
            Assert.Equal(expected.MarginalVar[i], Assert.IsType<double>(table[i + 1, 1]), 10);
            Assert.Equal(expected.ComponentVar[i], Assert.IsType<double>(table[i + 1, 2]), 8);
            Assert.Equal(expected.PercentVar[i], Assert.IsType<double>(table[i + 1, 3]), 12);
            Assert.Equal(expected.MarginalEs[i], Assert.IsType<double>(table[i + 1, 4]), 10);
            Assert.Equal(expected.ComponentEs[i], Assert.IsType<double>(table[i + 1, 5]), 8);
            Assert.Equal(expected.PercentEs[i], Assert.IsType<double>(table[i + 1, 6]), 12);
        }
        Assert.Equal("Total", table[Assets + 1, 0]);
        Assert.Equal(expected.Total.Var, Assert.IsType<double>(table[Assets + 1, 2]), 8);
        double sum = Enumerable.Range(1, Assets).Sum(i => (double)table[i, 2]);
        Assert.Equal(expected.Total.Var, sum, 6);
        // The same decomposition is consistent with QF.VAR.
        Assert.Equal(T.Num(_qf.Var(Range(Returns), 0.99, "PARAMETRIC", 5.0, Exposures)), expected.Total.Var, 6);
    }

    [Fact]
    public void HistoricalDecompositionMatchesTheLibrary()
    {
        var table = T.Array(_qf.VarDecomp(Range(Returns), Exposures, 0.95));
        var expected = HistoricalRisk.Decompose(Returns, ExposureArray, 0.95);
        for (int i = 0; i < Assets; i++)
        {
            Assert.Equal($"Asset{i + 1}", table[i + 1, 0]);
            Assert.Equal(expected.ComponentVar[i], Assert.IsType<double>(table[i + 1, 2]), 8);
            Assert.Equal(expected.ComponentEs[i], Assert.IsType<double>(table[i + 1, 5]), 8);
        }
        Assert.Equal(expected.Total.ExpectedShortfall, Assert.IsType<double>(table[Assets + 1, 5]), 8);
        Assert.Equal(1.0, Enumerable.Range(1, Assets).Sum(i => (double)table[i, 3]), 9);
    }

    [Fact]
    public void HistoricalDecompositionAtAHorizonScalesLikeTheTotal()
    {
        var one = T.Array(_qf.VarDecomp(Range(Returns), Exposures, 0.95));
        var ten = T.Array(_qf.VarDecomp(Range(Returns), Exposures, 0.95, "HISTORICAL", 10.0));
        Assert.Equal((double)one[Assets + 1, 2] * Math.Sqrt(10.0), (double)ten[Assets + 1, 2], 8);
        Assert.Equal((double)one[1, 3], (double)ten[1, 3], 9);
    }

    [Fact]
    public void DecompositionRejectsBadArguments()
    {
        Assert.Equal(XlError.Value, _qf.VarDecomp(Range(Returns), Exposures, 0.99, "CORNISHFISHER"));
        Assert.Equal(XlError.Value, _qf.VarDecomp(Range(Returns), T.Column(1.0, 2.0), 0.99));
        Assert.Equal(XlError.Num, _qf.VarDecomp(Range(Returns), Exposures, 2.0));
        Assert.Equal(XlError.Num, _qf.VarDecomp(Range(Returns), T.Column(0.0, 0.0, 0.0), 0.99, "PARAMETRIC"));   // zero-volatility portfolio
    }

    // ---- Monte Carlo VaR ----------------------------------------------------------------------------------------

    private static object[,] SpotBook() =>
        new object[,] { { 1.0, "SPOT", 1000.0, null!, null!, null! }, { 2.0, "stock", -200.0, null!, null!, null! } };

    private static object[,] MixedBook() => new object[,]
    {
        { "Asset", "Type", "Quantity", "Strike", "Maturity", "Div yield" },
        { "Equity", "SPOT", 500.0, null!, null!, null! },
        { "Equity", "PUT", 800.0, 95.0, 0.5, 0.01 },
        { "Bond", "CALL", -600.0, 105.0, 0.25, null! },
        { 3.0, "CALL", 400.0, 50.0, 1.0, null! },
    };

    private string DailyCov() => T.Text(_qf.CovObj("MC", Range(Returns, ["Equity", "Bond", "Gold"]), header: true));

    private static readonly object[,] Spots = T.Column(100.0, 100.0, 50.0);
    private static readonly object[,] Vols = T.Column(0.2, 0.25, 0.3);

    [Fact]
    public void McVarMatchesADirectEngineRun()
    {
        string handle = DailyCov();
        var result = T.Array(_qf.McVar(handle, Spots, Vols, MixedBook(), 0.99, 40_000, 7.0, "PHILOX", 0.02, 5.0));

        var cov = Covariance.Sample(Returns);
        var factors = new[]
        {
            new RiskFactor("Equity", 100.0, RiskFactorKind.LogNormal), new RiskFactor("Bond", 100.0, RiskFactorKind.LogNormal),
            new RiskFactor("Gold", 50.0, RiskFactorKind.LogNormal),
            new RiskFactor("v1", 0.2, RiskFactorKind.Additive), new RiskFactor("v2", 0.25, RiskFactorKind.Additive),
            new RiskFactor("v3", 0.3, RiskFactorKind.Additive), new RiskFactor("r", 0.02, RiskFactorKind.Additive),
        };
        var full = new double[7, 7];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) full[i, j] = cov[i, j];
        PortfolioPosition[] book =
        [
            new SpotPosition(0, 500.0),
            new EuropeanOptionPosition(OptionType.Put, 0, 3, 6, 95.0, 0.5, 800.0, 0.01),
            new EuropeanOptionPosition(OptionType.Call, 1, 4, 6, 105.0, 0.25, -600.0),
            new EuropeanOptionPosition(OptionType.Call, 2, 5, 6, 50.0, 1.0, 400.0),
        ];
        var run = new FullRevaluationEngine(factors, full, book)
            .Run(new MonteCarloVarOptions { Scenarios = 40_000, Seed = 7, Horizon = 5.0 });
        var expected = run.Measures(0.99);

        Assert.Equal(["", "Value", "Std error"], Enumerable.Range(0, 3).Select(j => (string)result[0, j]));
        Assert.Equal("VaR", result[1, 0]);
        Assert.Equal("ES", result[2, 0]);
        Assert.Equal(expected.Var, (double)result[1, 1], 9);
        Assert.Equal(expected.ExpectedShortfall, (double)result[2, 1], 9);
        Assert.True((double)result[2, 1] >= (double)result[1, 1]);
        Assert.True((double)result[1, 2] > 0.0 && (double)result[2, 2] > 0.0);
        // The error estimate is small against the value it qualifies (a few percent at 40 000 scenarios).
        Assert.True((double)result[1, 2] < 0.1 * (double)result[1, 1]);
    }

    [Fact]
    public void McVarIsDeterministicAcrossRunsAndDegreesOfParallelism()
    {
        string handle = DailyCov();
        object Run(int dop, double seed = 42.0, string generator = "PHILOX") =>
            _qf.McVar(handle, Spots, Vols, MixedBook(), 0.99, 30_001, seed, generator, null, null, default, dop);

        var reference = T.Array(Run(-1));
        foreach (int dop in new[] { 1, 2, 8, -1 })
            AssertSame(reference, T.Array(Run(dop)));

        // A different seed gives a different sample.
        Assert.NotEqual((double)reference[1, 1], (double)T.Array(Run(-1, seed: 43.0))[1, 1]);

        // The same holds for the scrambled Sobol generator.
        var sobol = T.Array(Run(1, generator: "SOBOL"));
        AssertSame(sobol, T.Array(Run(8, generator: "sobol")));
        Assert.NotEqual((double)reference[1, 1], (double)sobol[1, 1]);
    }

    private static void AssertSame(object[,] a, object[,] b)
    {
        for (int i = 0; i < a.GetLength(0); i++)
            for (int j = 0; j < a.GetLength(1); j++) Assert.Equal(a[i, j], b[i, j]);
    }

    [Fact]
    public void McVarOfALinearPortfolioIsCloseToTheDeltaNormalVar()
    {
        // Two linear positions on correlated assets: the P&L is very nearly normal, so the simulated VaR and ES must be
        // within sampling and lognormal-skew error (2%) of the delta-normal ones for exposures q * S.
        var cov = Covariance.Sample(Returns);
        string handle = DailyCov();
        var book = new object[,] { { "Equity", "SPOT", 1000.0, null!, null!, null! }, { "Bond", "SPOT", -300.0, null!, null!, null! } };
        var result = T.Array(_qf.McVar(handle, Spots, Vols, book, 0.99, 400_000, 1.0));
        var sub = new double[2, 2];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++) sub[i, j] = cov[i, j];
        var normal = ParametricRisk.DeltaNormal([1000.0 * 100.0, -300.0 * 100.0], sub, 0.99);
        Assert.Equal(normal.Var, (double)result[1, 1], normal.Var * 0.02);
        Assert.Equal(normal.ExpectedShortfall, (double)result[2, 1], normal.ExpectedShortfall * 0.02);
    }

    [Fact]
    public void McVarAcceptsACovarianceRangeAndStandardErrorsNeedEnoughScenarios()
    {
        var cov = Covariance.Sample(Returns);
        var range = Range(cov);
        var result = T.Array(_qf.McVar(range, Spots, Vols, SpotBook(), 0.95, 1000));
        Assert.Equal("n/a", result[1, 2]);                      // 1000 / 20 blocks < 100 scenarios per block
        Assert.IsType<double>(result[1, 1]);
        var direct = T.Array(_qf.McVar(DailyCov(), Spots, Vols, SpotBook(), 0.95, 1000));
        Assert.Equal((double)direct[1, 1], (double)result[1, 1]);   // names do not change the numbers
    }

    [Fact]
    public void McVarRejectsBadArgumentsAsValueErrors()
    {
        string handle = DailyCov();
        Assert.Equal(XlError.Value, _qf.McVar(handle, T.Column(100.0, 100.0), Vols, SpotBook(), 0.99, 1000));         // spots length
        Assert.Contains("one value per asset", ErrorLog.Last);
        Assert.Equal(XlError.Value, _qf.McVar(handle, T.Column(100.0, -1.0, 50.0), Vols, SpotBook(), 0.99, 1000));
        Assert.Equal(XlError.Value, _qf.McVar(handle, Spots, Vols, SpotBook(), 0.99, 10.5));                         // not an integer
        Assert.Equal(XlError.Value, _qf.McVar(handle, Spots, Vols, SpotBook(), 0.99, 1000, -1.0));                   // seed
        Assert.Equal(XlError.Value, _qf.McVar(handle, Spots, Vols, SpotBook(), 0.99, 1000, null, "MERSENNE"));
        Assert.Equal(XlError.Value, _qf.McVar(Range(Returns), Spots, Vols, SpotBook(), 0.99, 1000));                 // not square
        Assert.Equal(XlError.Value, _qf.McVar(handle, Spots, Vols, new object[,] { { "Nobody", "SPOT", 1.0 } }, 0.99, 1000));
        Assert.Equal(XlError.Value, _qf.McVar(handle, Spots, Vols, new object[,] { { 4.0, "SPOT", 1.0 } }, 0.99, 1000));
        Assert.Equal(XlError.Value, _qf.McVar(handle, Spots, Vols, new object[,] { { 1.0, "SWAP", 1.0 } }, 0.99, 1000));
        Assert.Equal(XlError.Value, _qf.McVar(handle, Spots, Vols, new object[,] { { 1.0, "CALL", 1.0, null!, 1.0 } }, 0.99, 1000));   // no strike
        Assert.Equal(XlError.Value, _qf.McVar(handle, Spots, Vols, 5.0, 0.99, 1000));
        Assert.Equal(XlError.Value, _qf.McVar(handle, Spots, Vols, new object[,] { { "Asset", "Type", "Quantity" } }, 0.99, 1000));   // header only
        Assert.Equal(XlError.Ref, _qf.McVar("QF.Cov:NOPE#1", Spots, Vols, SpotBook(), 0.99, 1000));
    }

    [Fact]
    public void McVarNumericalFailuresAreNumErrors()
    {
        string handle = DailyCov();
        Assert.Equal(XlError.Num, _qf.McVar(handle, Spots, Vols, SpotBook(), 1.2, 1000));
        Assert.Equal(XlError.Num, _qf.McVar(handle, Spots, Vols, SpotBook(), 0.99, 0));
        Assert.Equal(XlError.Num, _qf.McVar(handle, Spots, Vols, SpotBook(), 0.99, QuantFunctions.MaxMonteCarloScenarios + 1));
        Assert.Equal(XlError.Num, _qf.McVar(handle, Spots, Vols, SpotBook(), 0.99, 1000, null, null, null, -2.0));      // horizon
        Assert.Equal(XlError.Num, _qf.McVar(handle, Spots, Vols,
            new object[,] { { 1.0, "CALL", 1.0, -5.0, 1.0 } }, 0.99, 1000));                                             // negative strike
        var negative = Range(new double[,] { { 1.0, 2.0 }, { 2.0, 1.0 } });                                              // not positive semi-definite is repaired; negative variance is not
        negative[0, 0] = -1.0;
        Assert.Equal(XlError.Num, _qf.McVar(negative, T.Column(100.0, 100.0), T.Column(0.2, 0.2), SpotBook(), 0.99, 1000));
    }

    [Fact]
    public void McVarStopsWhenCancelled()
    {
        string handle = DailyCov();
        using var already = new CancellationTokenSource();
        already.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            _qf.McVar(handle, Spots, Vols, MixedBook(), 0.99, 1_000_000, 1.0, null, null, null, already.Token));

        // Cancelled while running: the call must return long before the full 50 M scenarios would finish.
        using var cts = new CancellationTokenSource();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var task = System.Threading.Tasks.Task.Run(() =>
            _qf.McVar(handle, Spots, Vols, MixedBook(), 0.99, QuantFunctions.MaxMonteCarloScenarios, 1.0, null, null, null, cts.Token));
        Thread.Sleep(100);
        cts.Cancel();
        var thrown = Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());
        Assert.NotNull(thrown);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
    }

    // ---- call keys ----------------------------------------------------------------------------------------------

    [Fact]
    public void CallKeysAreStableForEqualArgumentsAndDifferentOtherwise()
    {
        object[,] a = Range(Returns), b = Range(Returns);
        string key = RiskCalls.Key("QF.COV", a, "EWMA", 0.94, null, false);
        Assert.Equal(key, RiskCalls.Key("QF.COV", b, "EWMA", 0.94, null, false));
        Assert.StartsWith("QF.COV|", key);
        Assert.NotEqual(key, RiskCalls.Key("QF.COV", b, "EWMA", 0.95, null, false));
        Assert.NotEqual(key, RiskCalls.Key("QF.VAR", b, "EWMA", 0.94, null, false));
        Assert.NotEqual(key, RiskCalls.Key("QF.COV", b, "EWMA", 0.94, 1.0, false));
        b[3, 1] = 0.5;
        Assert.NotEqual(key, RiskCalls.Key("QF.COV", b, "EWMA", 0.94, null, false));
        // 1x2 and 2x1 holding the same values are different arguments.
        Assert.NotEqual(RiskCalls.Key("f", T.Row(1.0, 2.0)), RiskCalls.Key("f", T.Column(1.0, 2.0)));
        Assert.Equal(Dates * Assets + 1, RiskCalls.CellCount(a, null, 5.0));
    }
}
