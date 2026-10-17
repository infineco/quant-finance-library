using System;
using Xunit;

namespace InFine.QuantFinance.Volatility.Tests
{
    static class TestHelpers
    {
        public static void Close(double expected, double actual, double rel, double abs, string what)
        {
            Assert.True(Math.Abs(expected - actual) <= abs + rel * Math.Abs(expected),
                $"{what}: expected {expected:R}, got {actual:R} (diff {actual - expected:E3})");
        }

        /// <summary>Central finite-difference first and second derivatives of f at x with step h.</summary>
        public static (double First, double Second) CentralDifferences(Func<double, double> f, double x, double h)
        {
            double fp = f(x + h), f0 = f(x), fm = f(x - h);
            return ((fp - fm) / (2.0 * h), (fp - 2.0 * f0 + fm) / (h * h));
        }

        /// <summary>
        /// Arbitrage-violating raw SVI slice of Gatheral and Jacquier (2014), Example 3.1 (Axel Vogt, wilmott.com), with
        /// T = 1: (a, b, m, rho, sigma) = (-0.0410, 0.1331, 0.3586, 0.3060, 0.4153).
        /// </summary>
        public static SviRawParameters Vogt => new SviRawParameters(-0.0410, 0.1331, 0.3060, 0.3586, 0.4153);

        /// <summary>Quotes generated from an SVI slice at the given log-moneyness points, with optional additive vol noise.</summary>
        public static SmileQuotes Quotes(SviRawParameters svi, double maturity, double forward, double[] k,
            Random? noise = null, double noiseAmplitude = 0.0)
        {
            var strikes = new double[k.Length];
            var vols = new double[k.Length];
            for (int i = 0; i < k.Length; i++)
            {
                strikes[i] = forward * Math.Exp(k[i]);
                vols[i] = Math.Sqrt(svi.TotalVariance(k[i]) / maturity);
                if (noise != null) vols[i] += noiseAmplitude * (2.0 * noise.NextDouble() - 1.0);
            }
            return new SmileQuotes(maturity, forward, strikes, vols);
        }
    }
}
