using System;
using System.Collections.Generic;
using System.Diagnostics;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace InFine.QuantFinance.ClosedForm.Tests
{
    /// <summary>
    /// Heston calibration on synthetic surfaces: exact recovery from noise-free quotes (price and vol quotes, both
    /// objectives), stability under small deterministic noise, the Feller penalty and argument checks.
    /// </summary>
    public class HestonCalibratorTests
    {
        readonly ITestOutputHelper _output;

        public HestonCalibratorTests(ITestOutputHelper output) { _output = output; }

        static readonly HestonParameters Truth = new HestonParameters(0.03, 2.0, 0.05, 0.6, -0.65);

        static readonly double[] Maturities = { 0.25, 0.5, 1.0, 2.0, 5.0 };
        static readonly double[] StandardisedStrikes = { -2.0, -1.0, -0.5, 0.0, 0.5, 1.0, 2.0 };

        /// <summary>
        /// Surface of price quotes from <paramref name="p"/>: forward 100 e^{0.02 T}, DF e^{-0.03 T}, strikes
        /// F exp(k 0.2 sqrt(T)), alternating calls and puts; optional volatility noise of the given size.
        /// </summary>
        static List<HestonQuote> Surface(HestonParameters p, double noise = 0.0, int seed = 1)
        {
            var rng = new Random(seed);
            var quotes = new List<HestonQuote>();
            int n = 0;
            foreach (double t in Maturities)
            {
                double forward = 100.0 * Math.Exp(0.02 * t), df = Math.Exp(-0.03 * t);
                foreach (double k in StandardisedStrikes)
                {
                    double strike = forward * Math.Exp(k * 0.2 * Math.Sqrt(t));
                    OptionType type = n++ % 2 == 0 ? OptionType.Call : OptionType.Put;
                    double price = Heston.Price(type, forward, strike, df, t, p);
                    HestonQuote q = HestonQuote.FromPrice(t, forward, df, strike, price, type);
                    if (noise > 0.0) q = q with { Volatility = q.Volatility + noise * (2.0 * rng.NextDouble() - 1.0) };
                    quotes.Add(q);
                }
            }
            return quotes;
        }

        void Report(string label, HestonCalibrationResult r, long ms)
        {
            var p = r.Parameters;
            _output.WriteLine($"{label}: v0 {p.V0:R} kappa {p.Kappa:R} theta {p.Theta:R} xi {p.Xi:R} rho {p.Rho:R}; " +
                $"rmse {r.RootMeanSquareError:E2}, {r.Solver.Status}, {r.Solver.Iterations} it, {r.Solver.FunctionEvaluations} evals, {ms} ms");
        }

        static void AssertClose(HestonParameters expected, HestonParameters actual, double tolerance)
        {
            Assert.Equal(expected.V0, actual.V0, tolerance);
            Assert.Equal(expected.Kappa, actual.Kappa, tolerance);
            Assert.Equal(expected.Theta, actual.Theta, tolerance);
            Assert.Equal(expected.Xi, actual.Xi, tolerance);
            Assert.Equal(expected.Rho, actual.Rho, tolerance);
        }

        [Theory]
        [InlineData(HestonCalibrationObjective.ImpliedVolatility)]
        [InlineData(HestonCalibrationObjective.VegaWeightedPrice)]
        public void NoiseFreeSurface_RecoversParameters(HestonCalibrationObjective objective)
        {
            var quotes = Surface(Truth);
            var sw = Stopwatch.StartNew();
            var result = HestonCalibrator.Calibrate(quotes, new HestonCalibrationOptions { Objective = objective });
            Report(objective.ToString(), result, sw.ElapsedMilliseconds);
            Assert.True(result.Converged);
            AssertClose(Truth, result.Parameters, 1e-6); // the ticket asks 1e-4; about 1e-12 is reached
            Assert.True(result.RootMeanSquareError < 1e-10, $"rmse {result.RootMeanSquareError:E2}");
            Assert.Equal(quotes.Count, result.Residuals.Length);
        }

        [Fact]
        public void NoiseFreeSurface_RecoversFellerViolatingParameters_FromAFarStart()
        {
            // Fang-Oosterlee parameters (Feller violated), started far away
            var truth = new HestonParameters(0.0175, 1.5768, 0.0398, 0.5751, -0.5711);
            var result = HestonCalibrator.Calibrate(Surface(truth), new HestonCalibrationOptions
            {
                InitialGuess = new HestonParameters(0.1, 5.0, 0.1, 1.5, 0.3),
            });
            Report("far start", result, 0);
            AssertClose(truth, result.Parameters, 1e-6);
        }

        [Fact]
        public void NoisySurface_IsStable()
        {
            // +/- 10 bp of vol noise, uniform: the fit stays near the truth and the rmse near the noise (10bp / sqrt 3)
            const double noise = 0.001;
            foreach (int seed in new[] { 1, 2, 3 })
            {
                var result = HestonCalibrator.Calibrate(Surface(Truth, noise, seed));
                Report($"noise seed {seed}", result, 0);
                Assert.True(result.Converged);
                Assert.True(result.RootMeanSquareError < noise, $"rmse {result.RootMeanSquareError:E2}");
                var p = result.Parameters;
                Assert.Equal(Truth.V0, p.V0, 1e-3);
                Assert.Equal(Truth.Theta, p.Theta, 1e-3);
                Assert.Equal(Truth.Rho, p.Rho, 0.02);
                Assert.Equal(Truth.Xi, p.Xi, 0.03);
                Assert.Equal(Truth.Kappa, p.Kappa, 0.2);
            }
        }

        [Fact]
        public void VolatilityQuotes_AndPriceQuotes_AreEquivalent()
        {
            var fromPrices = Surface(Truth);
            var fromVols = new List<HestonQuote>();
            foreach (var q in fromPrices)
                fromVols.Add(new HestonQuote(q.Maturity, q.Forward, q.DiscountFactor, q.Strike, q.Volatility));
            var a = HestonCalibrator.Calibrate(fromPrices);
            var b = HestonCalibrator.Calibrate(fromVols);
            Assert.Equal(a.Parameters, b.Parameters);
        }

        [Fact]
        public void FellerPenalty_PullsTheFitTowardsTheFellerCondition()
        {
            var truth = new HestonParameters(0.0175, 1.5768, 0.0398, 0.5751, -0.5711); // xi^2 - 2 kappa theta = 0.205
            var quotes = Surface(truth);
            var free = HestonCalibrator.Calibrate(quotes);
            var penalised = HestonCalibrator.Calibrate(quotes, new HestonCalibrationOptions { FellerPenalty = true, FellerPenaltyWeight = 10.0 });
            Report("free", free, 0);
            Report("penalised", penalised, 0);
            double Violation(HestonParameters p) => p.Xi * p.Xi - 2.0 * p.Kappa * p.Theta;
            Assert.False(free.Parameters.SatisfiesFeller);
            Assert.True(Violation(penalised.Parameters) < 0.1 * Violation(free.Parameters),
                $"violation {Violation(penalised.Parameters):R} vs {Violation(free.Parameters):R}");
            Assert.True(penalised.RootMeanSquareError > free.RootMeanSquareError);
            Assert.Equal(quotes.Count, penalised.Residuals.Length);
        }

        [Fact]
        public void Bounds_AreRespected()
        {
            var options = new HestonCalibrationOptions { UpperBounds = new HestonParameters(4.0, 50.0, 4.0, 0.4, 0.999) };
            var result = HestonCalibrator.Calibrate(Surface(Truth), options);
            Assert.Equal(0.4, result.Parameters.Xi, 1e-12); // the truth (0.6) is outside: the fit sits on the bound
            Assert.True(result.RootMeanSquareError > 1e-4);
        }

        [Fact]
        public void InvalidInputs_Throw()
        {
            var quotes = Surface(Truth);
            Assert.Throws<ArgumentException>(() => HestonCalibrator.Calibrate(quotes.GetRange(0, 4)));
            Assert.Throws<ArgumentNullException>(() => HestonCalibrator.Calibrate(null));
            var bad = new List<HestonQuote>(quotes) { quotes[0] with { Volatility = -0.1 } };
            Assert.Throws<ArgumentException>(() => HestonCalibrator.Calibrate(bad));
            Assert.Throws<ArgumentException>(() => HestonCalibrator.Calibrate(quotes, new HestonCalibrationOptions
            {
                Solver = new LevenbergMarquardtOptions { LowerBounds = new double[5] },
            }));
            Assert.Throws<ArgumentException>(() => HestonCalibrator.Calibrate(quotes, new HestonCalibrationOptions
            {
                LowerBounds = new HestonParameters(0.1, 1.0, 0.1, 0.1, 0.0),
                UpperBounds = new HestonParameters(0.05, 2.0, 0.2, 0.2, 0.5),
            }));
            // a price below the intrinsic value has no implied volatility
            Assert.Throws<ArgumentOutOfRangeException>(() => HestonQuote.FromPrice(1.0, 100.0, 1.0, 80.0, 10.0, OptionType.Call));
        }
    }
}
