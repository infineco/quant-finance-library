using System;

namespace InFine.QuantFinance.ExcelSamples;

/// <summary>
/// Deterministic synthetic daily log returns for the risk samples: five assets with realistic annual volatilities and
/// correlations, fat tails (multivariate Student t with 5 degrees of freedom) and a volatility regime with two
/// stress episodes, so that historical, parametric and Cornish-Fisher VaR do not coincide. Everything comes from a
/// fixed-seed SplitMix64 stream, so the generator writes the same numbers on every run and every platform
/// (returns are rounded to 6 decimals, which absorbs the last-bit differences of <see cref="Math.Log"/> and friends).
/// </summary>
public static class SyntheticMarket
{
    public const int Seed = 20260101;
    public const int Days = 250;

    public static readonly string[] Assets = { "ESTOXX50", "SPX", "GOLD", "BUND", "EURUSD" };
    public static readonly double[] AnnualVol = { 0.19, 0.17, 0.15, 0.055, 0.07 };
    public static readonly double[] AnnualDrift = { 0.06, 0.07, 0.04, 0.01, 0.0 };

    // Equity indices move together, the bund is a mild hedge, gold and the euro both like a weak dollar.
    private static readonly double[,] Correlation =
    {
        {  1.00,  0.80,  0.05, -0.25,  0.25 },
        {  0.80,  1.00,  0.08, -0.20,  0.20 },
        {  0.05,  0.08,  1.00,  0.20,  0.35 },
        { -0.25, -0.20,  0.20,  1.00,  0.10 },
        {  0.25,  0.20,  0.35,  0.10,  1.00 },
    };

    private const double Nu = 5.0;
    private const double TradingDays = 252.0;

    /// <summary>The first dates of the sample: weekdays from 2 January 2024 (no holiday calendar: the data is synthetic).</summary>
    public static DateTime[] Dates(int days)
    {
        var result = new DateTime[days];
        var d = new DateTime(2024, 1, 2);
        for (int i = 0; i < days; i++)
        {
            while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(1);
            result[i] = d;
            d = d.AddDays(1);
        }
        return result;
    }

    /// <summary>A <c>days x Assets.Length</c> matrix of daily log returns, oldest first.</summary>
    public static double[,] DailyLogReturns(int days = Days)
    {
        int n = Assets.Length;
        var chol = Cholesky(Correlation);
        var rng = new SplitMix64(Seed);
        var result = new double[days, n];
        var z = new double[n];
        for (int t = 0; t < days; t++)
        {
            for (int j = 0; j < n; j++) z[j] = rng.NextGaussian();
            double chi2 = 0.0;
            for (int k = 0; k < (int)Nu; k++) { double g = rng.NextGaussian(); chi2 += g * g; }
            // Common mixing variable: multivariate t, unit variance after the (nu - 2) / nu scaling.
            double tail = Math.Sqrt(Nu / chi2) * Math.Sqrt((Nu - 2.0) / Nu);
            // Two stress episodes (around days 60 and 150) scale every volatility up.
            double regime = 1.0 + 0.5 * Math.Exp(-0.5 * Sq((t - 60) / 8.0)) + 1.2 * Math.Exp(-0.5 * Sq((t - 150) / 10.0));
            for (int i = 0; i < n; i++)
            {
                double x = 0.0;
                for (int j = 0; j <= i; j++) x += chol[i, j] * z[j];
                double r = AnnualDrift[i] / TradingDays + AnnualVol[i] / Math.Sqrt(TradingDays) * regime * tail * x;
                result[t, i] = Math.Round(r, 6);
            }
        }
        return result;
    }

    private static double Sq(double x) => x * x;

    private static double[,] Cholesky(double[,] a)
    {
        int n = a.GetLength(0);
        var l = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double s = a[i, j];
                for (int k = 0; k < j; k++) s -= l[i, k] * l[j, k];
                if (i == j)
                {
                    if (!(s > 0.0)) throw new InvalidOperationException("The correlation matrix is not positive definite.");
                    l[i, i] = Math.Sqrt(s);
                }
                else l[i, j] = s / l[j, j];
            }
        }
        return l;
    }

    private sealed class SplitMix64
    {
        private ulong _state;
        private double _spare;
        private bool _hasSpare;

        public SplitMix64(ulong seed) => _state = seed;

        private ulong Next()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Uniform in (0, 1).</summary>
        private double NextUniform() => ((Next() >> 11) + 0.5) / 9007199254740992.0;

        public double NextGaussian()
        {
            if (_hasSpare) { _hasSpare = false; return _spare; }
            double u1 = NextUniform(), u2 = NextUniform();
            double radius = Math.Sqrt(-2.0 * Math.Log(u1));
            _spare = radius * Math.Sin(2.0 * Math.PI * u2);
            _hasSpare = true;
            return radius * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
