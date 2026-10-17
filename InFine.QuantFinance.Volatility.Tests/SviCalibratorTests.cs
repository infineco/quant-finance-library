using System;
using System.Linq;
using InFine.QuantFinance.ClosedForm;
using Xunit;
using static InFine.QuantFinance.Volatility.Tests.TestHelpers;

namespace InFine.QuantFinance.Volatility.Tests
{
    /// <summary>
    /// SVI slice calibration: exact round trips (parameters recovered), noisy round trips (fit no worse than the
    /// generating parameters), price input and vega weighting.
    /// </summary>
    public class SviCalibratorTests
    {
        static readonly double[] Moneyness = Enumerable.Range(0, 21).Select(i => -0.6 + 0.05 * i).ToArray(); // -0.6 .. 0.4

        public static TheoryData<double, double, double, double, double, double> Slices => new()
        {
            // a, b, rho, m, sigma, T
            { 0.01, 0.08, -0.4, 0.05, 0.2, 0.5 },
            { 0.002, 0.15, -0.7, -0.1, 0.1, 0.25 },
            { 0.04, 0.2, 0.2, 0.1, 0.4, 2.0 },
            { -0.0410, 0.1331, 0.3060, 0.3586, 0.4153, 1.0 }, // Vogt: a < 0 and a negative density, still a valid SVI
        };

        [Theory]
        [MemberData(nameof(Slices))]
        public void ExactQuotesGiveBackTheParameters(double a, double b, double rho, double m, double sigma, double t)
        {
            var truth = new SviRawParameters(a, b, rho, m, sigma);
            var k = rho > 0.25 ? Moneyness.Select(x => x + 0.4).ToArray() : Moneyness; // Vogt's smile sits right of the money
            var result = SviCalibrator.Calibrate(Quotes(truth, t, 100.0, k));

            // Exact data: the cost reaches rounding level. The parameters are identifiable from 21 points around the
            // vertex, so they come back to ~1e-7 (conditioning of the 5x5 normal matrix ~1e6 times 1e-15 residuals^0.5).
            Assert.True(result.Converged, result.Status.ToString());
            Assert.True(result.RootMeanSquareTotalVarianceError < 1e-12, $"rmse {result.RootMeanSquareTotalVarianceError:E3}");
            Assert.True(result.MaxAbsoluteVolatilityError < 1e-10, $"vol error {result.MaxAbsoluteVolatilityError:E3}");
            var p = result.Parameters;
            Close(a, p.A, 0, 1e-7, "a");
            Close(b, p.B, 0, 1e-7, "b");
            Close(rho, p.Rho, 0, 1e-6, "rho");
            Close(m, p.M, 0, 1e-6, "m");
            Close(sigma, p.Sigma, 0, 1e-6, "sigma");
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void NoisyQuotesFitAtLeastAsWellAsTheTruth(int seed)
        {
            // Quotes = truth +- 20 bp of vol (uniform, deterministic seed). The least-squares optimum can only beat the
            // generating parameters, so the fitted cost must not exceed theirs (relative slack 1e-9 for convergence).
            // The fitted smile must stay within the noise band of the true one over the quoted range.
            var truth = new SviRawParameters(0.01, 0.08, -0.4, 0.05, 0.2);
            const double t = 0.5, noise = 0.002;
            var quotes = Quotes(truth, t, 100.0, Moneyness, new Random(seed), noise);
            var result = SviCalibrator.Calibrate(quotes);
            Assert.True(result.Converged, result.Status.ToString());

            double truthCost = 0.0;
            for (int i = 0; i < quotes.Count; i++)
            {
                double e = truth.TotalVariance(quotes.LogMoneyness(i)) - quotes.TotalVariance(i);
                truthCost += 0.5 * e * e;
            }
            Assert.True(result.WeightedCost <= truthCost * (1 + 1e-9), $"fit {result.WeightedCost:E6} vs truth {truthCost:E6}");
            Assert.True(result.MaxAbsoluteVolatilityError < 2 * noise, $"max vol error {result.MaxAbsoluteVolatilityError:E3}");
            foreach (double k in Moneyness)
            {
                double fitted = Math.Sqrt(result.Parameters.TotalVariance(k) / t), exact = Math.Sqrt(truth.TotalVariance(k) / t);
                Close(exact, fitted, 0, noise, $"vol at k={k}");
            }
        }

        [Fact]
        public void PricesAndVegaWeightsRoundTrip()
        {
            // Quotes given as OTM Black-76 prices, converted with Let's Be Rational; vega weights change the
            // objective but not its exact minimiser.
            var truth = new SviRawParameters(0.002, 0.15, -0.7, -0.1, 0.1);
            const double t = 0.25, forward = 50.0, df = 0.99;
            var strikes = Moneyness.Select(k => forward * Math.Exp(k)).ToArray();
            var types = strikes.Select(s => s < forward ? OptionType.Put : OptionType.Call).ToArray();
            var prices = strikes.Select((s, i) =>
                Black76.Price(types[i], forward, s, df, t, Math.Sqrt(truth.TotalVariance(Math.Log(s / forward)) / t))).ToArray();
            var quotes = SmileQuotes.FromPrices(t, forward, df, strikes, prices, types);
            var result = SviCalibrator.Calibrate(quotes, new SviCalibrationOptions { Weighting = SmileWeighting.Vega });

            var weights = quotes.EffectiveWeights(SmileWeighting.Vega);
            Close(1.0, weights.Max(), 0, 1e-15, "max vega weight");
            Assert.True(weights.Min() < 0.5, "wings are down-weighted");
            Assert.True(result.MaxAbsoluteVolatilityError < 1e-9, $"vol error {result.MaxAbsoluteVolatilityError:E3}");
            Close(truth.Rho, result.Parameters.Rho, 0, 1e-6, "rho");
            Close(truth.M, result.Parameters.M, 0, 1e-6, "m");
        }

        [Fact]
        public void TheFitRespectsTheConstraints()
        {
            // Quotes from a slice whose right wing is steeper than Lee's bound (slope 2.4): the fit must stay
            // admissible, with both wing slopes at most 2 and a non-negative minimum variance.
            var steep = new SviRawParameters(0.01, 1.6, 0.5, 0.0, 0.1);
            var quotes = Quotes(steep, 1.0, 100.0, Moneyness.Select(x => x + 0.3).ToArray());
            var p = SviCalibrator.Calibrate(quotes).Parameters;
            Assert.True(p.RightWingSlope <= 2.0 + 1e-12 && p.LeftWingSlope <= 2.0 + 1e-12, p.ToString());
            Assert.True(p.MinimumTotalVariance >= -1e-15, p.ToString());
            Assert.True(Math.Abs(p.Rho) < 1);
        }

        [Fact]
        public void InputsAreValidated()
        {
            var four = Quotes(new SviRawParameters(0.01, 0.08, -0.4, 0.05, 0.2), 0.5, 100, new[] { -0.2, -0.1, 0.0, 0.1 });
            Assert.Throws<ArgumentException>(() => SviCalibrator.Calibrate(four));
            Assert.Throws<ArgumentException>(() => new SmileQuotes(1, 100, new[] { 90.0, 90.0 }, new[] { 0.2, 0.2 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SmileQuotes(1, 100, new[] { 90.0 }, new[] { -0.2 }));
            Assert.Throws<ArgumentException>(() => new SmileQuotes(1, 100, new[] { 90.0 }, new[] { 0.2 }, new[] { 0.0 }));
            // A call priced below intrinsic has no implied volatility.
            Assert.Throws<ArgumentException>(() => SmileQuotes.FromPrices(1, 100, 1, new[] { 80.0 }, new[] { 10.0 }, new[] { OptionType.Call }));
        }
    }
}
