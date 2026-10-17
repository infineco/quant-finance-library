using System;
using InFine.QuantFinance.Backtesting;

namespace InFine.QuantFinance.Backtesting.Tests
{
    static class TestData
    {
        /// <summary>Business-day-like calendar: consecutive weekdays from 2 January 2023.</summary>
        public static DateOnly[] Weekdays(int count)
        {
            var dates = new DateOnly[count];
            var d = new DateOnly(2023, 1, 2);
            for (int i = 0; i < count; i++)
            {
                while (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday) d = d.AddDays(1);
                dates[i] = d;
                d = d.AddDays(1);
            }
            return dates;
        }

        /// <summary>Correlated Gaussian-ish daily returns (seeded System.Random, Box-Muller), T x N.</summary>
        public static double[,] Returns(int periods, int assets, int seed, double vol = 0.01, double drift = 0.0003, double correlation = 0.3)
        {
            var rng = new System.Random(seed);
            var r = new double[periods, assets];
            for (int t = 0; t < periods; t++)
            {
                double common = Normal(rng);
                for (int i = 0; i < assets; i++)
                {
                    double z = Math.Sqrt(correlation) * common + Math.Sqrt(1.0 - correlation) * Normal(rng);
                    r[t, i] = drift + vol * (1.0 + 0.5 * i) * z;
                }
            }
            return r;
        }

        /// <summary>A single AR(1) return series r_t = mu + phi (r_(t-1) - mu) + e_t.</summary>
        public static double[,] Ar1(int periods, double phi, int seed, double mu = 0.0005, double vol = 0.01)
        {
            var rng = new System.Random(seed);
            var r = new double[periods, 1];
            double prev = mu;
            for (int t = 0; t < periods; t++)
            {
                prev = mu + phi * (prev - mu) + vol * Normal(rng);
                r[t, 0] = prev;
            }
            return r;
        }

        public static ReturnPanel Panel(double[,] returns, double[]? cash = null) =>
            new ReturnPanel(Weekdays(returns.GetLength(0)), returns, cash);

        static double Normal(System.Random rng)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
