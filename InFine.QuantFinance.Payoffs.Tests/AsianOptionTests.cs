using System;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Payoffs.Concepts;
using InFine.QuantFinance.Payoffs.Equity;
using InFine.QuantFinance.Scheduling;
using InFine.QuantFinance.TestSupport;
using Xunit;

namespace InFine.QuantFinance.Payoffs.Tests
{
    public class AsianMathTests
    {
        [Fact]
        public void ArithmeticAndGeometricAveragesOfAKnownPath()
        {
            double[] path = { 100, 110, 90, 120, 100 };
            int[] fixings = { 1, 2, 3 };
            // arithmetic (110 + 90 + 120) / 3 = 106.666..., geometric (110 * 90 * 120)^(1/3) = 105.9...
            double arithmetic = AsianMath.PathPayoff(path, fixings, 4, 1, false, false, 100.0);
            Assert.Equal(320.0 / 3.0 - 100.0, arithmetic, 12);
            double geometric = AsianMath.PathPayoff(path, fixings, 4, 1, true, false, 100.0);
            Assert.Equal(Math.Pow(110.0 * 90.0 * 120.0, 1.0 / 3.0) - 100.0, geometric, 12);
            Assert.True(geometric < arithmetic); // AM-GM
        }

        [Fact]
        public void PutAndAverageStrikePayoffs()
        {
            double[] path = { 100, 110, 90, 120, 100 };
            int[] fixings = { 1, 2, 3 };
            Assert.Equal(0.0, AsianMath.PathPayoff(path, fixings, 4, -1, false, false, 100.0), 15);
            Assert.Equal(120.0 / 1.0 - 320.0 / 3.0, AsianMath.PathPayoff(path, fixings, 3, 1, false, true, 0.0), 12); // S_T = path[3]
            Assert.Equal(320.0 / 3.0 - 100.0, AsianMath.PathPayoff(path, fixings, 4, -1, false, true, 0.0), 12);      // put: A - S_T
            Assert.Equal(0.0, AsianMath.PathPayoff(path, fixings, 4, 1, false, true, 0.0), 15);
        }

        [Fact]
        public void FixingsMayBeAnySubsetIncludingPillarZero()
        {
            double[] path = { 100, 101, 102, 103 };
            // average of pillars 0 and 3 is 101.5
            Assert.Equal(1.5, AsianMath.PathPayoff(path, new[] { 0, 3 }, 3, 1, false, false, 100.0), 12);
            // a single fixing at the maturity: average-price call = vanilla call, average-strike call = 0
            Assert.Equal(3.0, AsianMath.PathPayoff(path, new[] { 3 }, 3, 1, false, false, 100.0), 12);
            Assert.Equal(0.0, AsianMath.PathPayoff(path, new[] { 3 }, 3, 1, false, true, 0.0), 12);
        }
    }

    public class AsianControlVariateTests
    {
        [Fact]
        public void BetaIsTheRegressionSlopeAndALinearControlIsEliminated()
        {
            var y = new double[1000];
            var x = new double[1000];
            for (int i = 0; i < y.Length; ++i)
            {
                y[i] = Math.Sin(i * 0.37) + 0.01 * i;
                x[i] = 3.0 + 2.5 * y[i];
            }
            Assert.Equal(2.5, AsianControlVariate.OptimalBeta(x, y), 10);
            // E[Y] given exactly as its sample mean + 0.2: the estimate moves by -beta * (-0.2)
            double meanY = 0.0;
            foreach (double v in y) meanY += v;
            meanY /= y.Length;
            var estimate = AsianControlVariate.Estimate(x, y, meanY + 0.2);
            Assert.Equal(3.0 + 2.5 * (meanY + 0.2), estimate.Value, 10);
            Assert.True(estimate.StandardError < 1e-12);
            Assert.Equal(1.0, estimate.Correlation, 12);
        }

        [Fact]
        public void IndependentControlLeavesTheEstimateUnchangedInExpectation()
        {
            // X = Y + noise orthogonal construction: beta must be near 1 and variance reduction near Var(X)/Var(noise)
            var x = new double[20000];
            var y = new double[20000];
            var z = new double[2];
            for (int i = 0; i < x.Length; ++i)
            {
                McPaths.Normals((uint)i, 7, z, 2);
                y[i] = z[0];
                x[i] = z[0] + 0.1 * z[1];
            }
            var e = AsianControlVariate.Estimate(x, y, 0.0);
            Assert.InRange(e.Beta, 0.97, 1.03);
            Assert.InRange(e.VarianceReduction, 80.0, 125.0); // 1.01 / 0.01 = 101
            Assert.True(Math.Abs(e.Value) < 4 * e.StandardError);
        }
    }

    public class AsianMonteCarloTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _output;
        public AsianMonteCarloTests(Xunit.Abstractions.ITestOutputHelper output) { _output = output; }

        const double S = 100.0, R = 0.05, Q = 0.02, Sigma = 0.25, T = 1.0;
        const int Steps = 12, Paths = 40000;

        /// <summary>Discounted arithmetic and geometric premia of the same paths (monthly fixings 1..12).</summary>
        static void Premia(int sign, bool averageStrike, double strike, out double[] arithmetic, out double[] geometric)
        {
            arithmetic = new double[Paths];
            geometric = new double[Paths];
            var path = new double[Steps + 1];
            var normals = new double[Steps];
            int[] fixings = McPaths.Range(1, Steps);
            double df = Math.Exp(-R * T);
            for (int p = 0; p < Paths; ++p)
            {
                McPaths.Gbm((uint)p, Steps, S, R, Q, Sigma, T, path, normals);
                arithmetic[p] = df * AsianMath.PathPayoff(path, fixings, Steps, sign, false, averageStrike, strike);
                geometric[p] = df * AsianMath.PathPayoff(path, fixings, Steps, sign, true, averageStrike, strike);
            }
        }

        [Fact]
        public void DiscreteGeometricAsianMatchesKemnaVorstWithinThreeStandardErrors()
        {
            foreach (int sign in new[] { 1, -1 })
                foreach (double strike in new[] { 90.0, 100.0, 110.0 })
                {
                    double[] arithmetic, geometric;
                    Premia(sign, false, strike, out arithmetic, out geometric);
                    double mean, se;
                    McPaths.MeanAndError(geometric, out mean, out se);
                    double exact = AsianKernels.GeometricPriceEquallySpaced(sign, S, strike, T, Steps, R, Q, Sigma);
                    Assert.True(Math.Abs(mean - exact) < 3 * se, $"sign={sign} K={strike}: MC {mean} +- {se}, Kemna-Vorst {exact}");
                }
        }

        [Fact]
        public void DiscreteGeometricAverageStrikeMatchesTheExchangeOptionFormula()
        {
            double[] fixingTimes = McPaths.Dates(Steps, T);
            var times = new double[Steps];
            Array.Copy(fixingTimes, 1, times, 0, Steps);
            foreach (int sign in new[] { 1, -1 })
            {
                double[] arithmetic, geometric;
                Premia(sign, true, 0.0, out arithmetic, out geometric);
                double mean, se;
                McPaths.MeanAndError(geometric, out mean, out se);
                double exact = AsianKernels.GeometricAverageStrikePrice(sign, S, T, times, Steps, R, Q, Sigma);
                Assert.True(Math.Abs(mean - exact) < 3 * se, $"sign={sign}: MC {mean} +- {se}, exact {exact}");
            }
        }

        [Fact]
        public void GeometricControlVariateCutsTheVarianceOfTheAtTheMoneyArithmeticAsianByMoreThanTenfold()
        {
            double[] arithmetic, geometric;
            Premia(1, false, 100.0, out arithmetic, out geometric);
            double control = AsianKernels.GeometricPriceEquallySpaced(1, S, 100.0, T, Steps, R, Q, Sigma);
            var e = AsianControlVariate.Estimate(arithmetic, geometric, control);
            _output.WriteLine($"ATM arithmetic Asian: plain {e.PlainValue:F5} +- {e.PlainStandardError:F5}, control variate {e.Value:F5} +- {e.StandardError:F5}, beta {e.Beta:F4}, rho {e.Correlation:F5}, variance reduction {e.VarianceReduction:F1}x");
            Assert.True(e.VarianceReduction > 10.0, $"variance reduction {e.VarianceReduction}");
            Assert.True(e.StandardError * 3.0 < e.PlainStandardError, "standard error should fall by more than 3x");
            // both estimators are unbiased: they agree within the plain standard error
            Assert.True(Math.Abs(e.Value - e.PlainValue) < 3 * e.PlainStandardError, $"{e.Value} vs {e.PlainValue} +- {e.PlainStandardError}");
            Assert.InRange(e.Beta, 0.9, 1.1);
            // arithmetic average >= geometric average pathwise, hence in price
            Assert.True(e.Value > control);
        }

        [Fact]
        public void ControlVariateEstimatorIsStableAcrossPutAndMoneyness()
        {
            foreach (double strike in new[] { 85.0, 100.0, 115.0 })
            {
                double[] arithmetic, geometric;
                Premia(-1, false, strike, out arithmetic, out geometric);
                double control = AsianKernels.GeometricPriceEquallySpaced(-1, S, strike, T, Steps, R, Q, Sigma);
                var e = AsianControlVariate.Estimate(arithmetic, geometric, control);
                Assert.True(e.VarianceReduction > 10.0, $"K={strike}: variance reduction {e.VarianceReduction}");
                Assert.True(Math.Abs(e.Value - e.PlainValue) < 3 * e.PlainStandardError);
            }
        }
    }

    public class AsianOptionKernelTests
    {
        [HybridizerFact]
        public void KernelMatchesThePerPathHelpers()
        {
            double[] spots = { 100, 104, 97, 108, 111, 103, 99 };
            int n = spots.Length - 1;
            var dates = McPaths.Dates(n, 1.0);
            var schedule = new PrecalculatedSchedule(dates);
            var discounting = new ConstantRateDiscounting(0.03, schedule);
            var log = new ArrayDoubleSpot(1, spots.Length);
            var spot = new LogBasedSpot<ArrayDoubleSpot>(log);
            for (int i = 0; i < spots.Length; ++i) spot[0, i] = spots[i];
            int[] fixings = { 2, 3, 5, 6 };
            foreach (bool geometric in new[] { false, true })
                foreach (bool averageStrike in new[] { false, true })
                    foreach (int sign in new[] { 1, -1 })
                    {
                        var premium = new ArrayPremium(1);
                        new AsianOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                            spot, discounting, schedule, premium, fixings, n, 102.0, sign, geometric, averageStrike).Simulate(0, 1);
                        double expected = AsianMath.PathPayoff(spots, fixings, n, sign, geometric, averageStrike, 102.0) * Math.Exp(-0.03);
                        Assert.Equal(expected, premium[0], 10);
                    }
        }
    }
}
