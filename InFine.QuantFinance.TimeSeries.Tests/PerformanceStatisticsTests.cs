using System;
using InFine.QuantFinance.Risk;
using InFine.QuantFinance.TimeSeries;
using Xunit;

namespace InFine.QuantFinance.TimeSeries.Tests
{
    /// <summary>
    /// Performance statistics on a tiny series whose expected values were computed independently (Python <c>statistics</c>
    /// module and the textbook formulas) and on constructed price paths with obvious drawdowns.
    /// </summary>
    public class PerformanceStatisticsTests
    {
        static readonly double[] R = { 0.02, -0.01, 0.03, -0.02, 0.01, 0.04, -0.03, 0.005 };

        static void Close(double expected, double actual, double tol = 1e-12) =>
            Assert.True(Math.Abs(expected - actual) <= tol, $"expected {expected:R}, got {actual:R}");

        [Fact]
        public void MeanAndAnnualisedMean()
        {
            Close(0.005625, PerformanceStatistics.Mean(R));
            Close(0.0675, PerformanceStatistics.AnnualisedMean(R, Periodicity.Monthly));
        }

        [Fact]
        public void VolatilityPopulationAndSample()
        {
            Close(0.02441274550019196, PerformanceStatistics.StandardDeviation(R));
            Close(0.022836032383056388, PerformanceStatistics.StandardDeviation(R, MomentConvention.Population));
            Close(0.02441274550019196 * Math.Sqrt(12.0), PerformanceStatistics.Volatility(R, Periodicity.Monthly));
        }

        [Fact]
        public void SkewnessAndExcessKurtosisBothConventions()
        {
            Close(-0.08081551063777745, PerformanceStatistics.Skewness(R, MomentConvention.Population));
            Close(-0.10079465078124934, PerformanceStatistics.Skewness(R, MomentConvention.SampleAdjusted));
            Close(-1.2101939990741915, PerformanceStatistics.ExcessKurtosis(R, MomentConvention.Population));
            Close(-1.141407398055802, PerformanceStatistics.ExcessKurtosis(R, MomentConvention.SampleAdjusted));
        }

        [Fact]
        public void SymmetricSeriesHasZeroSkewness()
        {
            var r = new[] { -0.03, -0.01, 0.0, 0.01, 0.03 };
            Close(0.0, PerformanceStatistics.Skewness(r, MomentConvention.Population));
            Close(0.0, PerformanceStatistics.Skewness(r, MomentConvention.SampleAdjusted));
            // Two-point +/- series: m4 = m2^2, so population excess kurtosis is exactly -2.
            Close(-2.0, PerformanceStatistics.ExcessKurtosis(new[] { 0.1, -0.1, 0.1, -0.1 }, MomentConvention.Population));
        }

        [Fact]
        public void GeometricReturnCompoundsAndAnnualises()
        {
            Close(0.04373723211037284, PerformanceStatistics.TotalReturn(R));
            // 8 monthly returns -> exponent 12 / 8.
            Close(Math.Pow(1.04373723211037284, 12.0 / 8.0) - 1.0, PerformanceStatistics.AnnualisedReturn(R, Periodicity.Monthly));
            Close(0.0663180574712694, PerformanceStatistics.AnnualisedReturn(R, Periodicity.Monthly), 1e-10);
            // Twelve +1% months compound to 1.01^12 - 1 over exactly one year.
            var twelve = new double[12];
            Array.Fill(twelve, 0.01);
            Close(Math.Pow(1.01, 12) - 1.0, PerformanceStatistics.AnnualisedReturn(twelve, Periodicity.Monthly));
            Assert.Equal(-1.0, PerformanceStatistics.AnnualisedReturn(new[] { 0.1, -1.0, 0.2 }, 12));
        }

        [Fact]
        public void SharpeUsesAnnualRiskFreeRateConvertedToPeriodRate()
        {
            Close(0.44342892720609256, PerformanceStatistics.SharpeRatio(R, Periodicity.Monthly, 0.03));
            Close(0.4740454631399773, PerformanceStatistics.SharpeRatio(R, Periodicity.Monthly, 0.03, MomentConvention.Population));
            Close(0.005625 / 0.02441274550019196 * Math.Sqrt(12.0), PerformanceStatistics.SharpeRatio(R, Periodicity.Monthly));
        }

        [Fact]
        public void SortinoUsesTargetDownsideDeviationOverAllObservations()
        {
            // MAR 6% a year = 0.5% a month; shortfalls vs 0.005: -0.015, -0.025, -0.035; the other five returns are at or above target but
            // still count in the denominator n = 8.
            double dd = Math.Sqrt((0.015 * 0.015 + 0.025 * 0.025 + 0.035 * 0.035) / 8.0);
            Close(dd, PerformanceStatistics.DownsideDeviation(R, 0.005));
            Close(0.016105123408406405, dd);
            Close(0.13443321448446638, PerformanceStatistics.SortinoRatio(R, Periodicity.Monthly, 0.06));
        }

        [Fact]
        public void SortinoWithoutShortfallIsInfinite()
        {
            Assert.Equal(double.PositiveInfinity, PerformanceStatistics.SortinoRatio(new[] { 0.01, 0.02, 0.03 }, 12));
        }

        [Fact]
        public void HitRatioCountsStrictlyPositiveReturns()
        {
            Close(0.625, PerformanceStatistics.HitRatio(R));
            Close(0.5, PerformanceStatistics.HitRatio(new[] { 0.0, 0.01, -0.01, 0.02 }));
            Close(0.125, PerformanceStatistics.HitRatio(R, 0.03)); // only 0.04 is strictly above 0.03
        }

        [Fact]
        public void MaxDrawdownOnAConstructedPricePath()
        {
            // 121 is the first peak; the fall to 90.75 is 25%; the later fall 130 -> 104 is only 20%.
            double[] prices = { 100, 110, 121, 99, 90.75, 95, 110, 130, 117, 104 };
            var dd = PerformanceStatistics.MaxDrawdownOfLevels(prices);
            Close(0.25, dd.Depth);
            Assert.Equal(2, dd.PeakIndex);
            Assert.Equal(4, dd.TroughIndex);
            Assert.Equal(7, dd.RecoveryIndex);
            Assert.Equal(2, dd.PeakToTroughLength);
            Assert.Equal(3, dd.RecoveryLength);

            var series = PerformanceStatistics.DrawdownsOfLevels(prices);
            Assert.Equal(prices.Length, series.Length);
            double[] expected = { 0, 0, 0, 1 - 99.0 / 121, 0.25, 1 - 95.0 / 121, 1 - 110.0 / 121, 0, 0.1, 0.2 };
            for (int i = 0; i < expected.Length; i++) Close(expected[i], series[i]);
        }

        [Fact]
        public void MaxDrawdownFromReturnsMatchesThePricePathAndUsesWealthPositions()
        {
            double[] prices = { 100, 110, 121, 99, 90.75, 95, 110, 130, 117, 104 };
            var fromReturns = PerformanceStatistics.MaxDrawdown(Returns.Simple(prices));
            Close(0.25, fromReturns.Depth, 1e-12);
            Assert.Equal(2, fromReturns.PeakIndex);
            Assert.Equal(4, fromReturns.TroughIndex);
            Assert.Equal(7, fromReturns.RecoveryIndex);
            var series = PerformanceStatistics.Drawdowns(Returns.Simple(prices));
            Assert.Equal(prices.Length, series.Length);
            Close(0.0, series[0]);
            Close(0.25, series[4], 1e-12);
        }

        [Fact]
        public void DrawdownWithoutRecoveryAndWithoutFall()
        {
            var open = PerformanceStatistics.MaxDrawdownOfLevels(new double[] { 100, 120, 60, 90 });
            Close(0.5, open.Depth);
            Assert.Equal(1, open.PeakIndex);
            Assert.Equal(2, open.TroughIndex);
            Assert.Equal(-1, open.RecoveryIndex);
            Assert.Equal(-1, open.RecoveryLength);

            var up = PerformanceStatistics.MaxDrawdownOfLevels(new double[] { 100, 101, 105 });
            Assert.Equal(new DrawdownInfo(0.0, 0, 0, 0), up);

            // The initial wealth counts as a peak: a first-period loss is a drawdown from position 0.
            var firstLoss = PerformanceStatistics.MaxDrawdown(new[] { -0.2, 0.1 });
            Close(0.2, firstLoss.Depth);
            Assert.Equal(0, firstLoss.PeakIndex);
            Assert.Equal(1, firstLoss.TroughIndex);
            Assert.Equal(-1, firstLoss.RecoveryIndex);
        }

        [Fact]
        public void CalmarIsAnnualisedReturnOverDepth()
        {
            Close(0.0663180574712694 / 0.030000000000000027, PerformanceStatistics.CalmarRatio(R, Periodicity.Monthly), 1e-9);
            Close(0.030000000000000027, PerformanceStatistics.MaxDrawdown(R).Depth, 1e-15);
            Assert.Equal(6, PerformanceStatistics.MaxDrawdown(R).PeakIndex);
            Assert.Equal(7, PerformanceStatistics.MaxDrawdown(R).TroughIndex);
        }

        [Fact]
        public void RollingStatisticsAreAlignedOnTheLastObservation()
        {
            double[] vol = PerformanceStatistics.RollingVolatility(R, 4, Periodicity.Monthly);
            Assert.Equal(R.Length, vol.Length);
            for (int i = 0; i < 3; i++) Assert.True(double.IsNaN(vol[i]));
            double[] expectedVol = { 0.0824621125123532, 0.07681145747868608, 0.0916515138991168, 0.10954451150103321, 0.09937303457175894 };
            for (int i = 0; i < 5; i++) Close(expectedVol[i], vol[i + 3]);

            double[] sharpe = PerformanceStatistics.RollingSharpe(R, 4, Periodicity.Monthly, 0.03);
            double[] expectedSharpe = { 0.3638034375544993, 0.0, 1.6366341767699426, -0.273861278752583, 0.4528391448839648 };
            for (int i = 0; i < 3; i++) Assert.True(double.IsNaN(sharpe[i]));
            for (int i = 0; i < 5; i++) Close(expectedSharpe[i], sharpe[i + 3], 1e-12);

            double[] mean = PerformanceStatistics.RollingMean(R, 2);
            Assert.True(double.IsNaN(mean[0]));
            Close(0.005, mean[1]);
            Close(0.01, mean[2]);

            // The generic form agrees with the dedicated one.
            double[] generic = PerformanceStatistics.Rolling(R, 4, w => PerformanceStatistics.Volatility(w, Periodicity.Monthly));
            for (int i = 0; i < R.Length; i++) Close(double.IsNaN(vol[i]) ? 0.0 : vol[i], double.IsNaN(generic[i]) ? 0.0 : generic[i]);

            // Window equal to the series length gives one value, equal to the full-sample statistic.
            double[] full = PerformanceStatistics.RollingVolatility(R, R.Length, Periodicity.Monthly);
            Close(PerformanceStatistics.Volatility(R, Periodicity.Monthly), full[R.Length - 1]);
        }

        [Fact]
        public void HistoricalVarEsPassesThroughToTheRiskLibrary()
        {
            var r = new double[100];
            for (int i = 0; i < r.Length; i++) r[i] = -0.05 + 0.001 * i; // -5% .. +4.9%
            var viaTimeSeries = PerformanceStatistics.HistoricalVarEs(r, 0.95);
            var direct = HistoricalRisk.Measures(r, 0.95);
            Assert.Equal(direct, viaTimeSeries);
            // Losses are positive: the 5% worst of 100 uniform points: the 95th smallest loss, i.e. the 6th smallest return -0.045.
            Close(0.045, viaTimeSeries.Var, 1e-12);
            Assert.True(viaTimeSeries.ExpectedShortfall >= viaTimeSeries.Var);
        }

        [Fact]
        public void SummariseBundlesTheIndividualStatistics()
        {
            var s = PerformanceStatistics.Summarise(R, Periodicity.Monthly, 0.03, 0.06);
            Assert.Equal(8, s.Count);
            Close(PerformanceStatistics.AnnualisedReturn(R, 12), s.AnnualisedReturn);
            Close(PerformanceStatistics.SharpeRatio(R, 12, 0.03), s.SharpeRatio);
            Close(PerformanceStatistics.SortinoRatio(R, 12, 0.06), s.SortinoRatio);
            Close(PerformanceStatistics.CalmarRatio(R, 12), s.CalmarRatio);
            Assert.Equal(PerformanceStatistics.MaxDrawdown(R), s.MaxDrawdown);
        }

        [Fact]
        public void InvalidInputIsRejected()
        {
            Assert.Throws<ArgumentNullException>(() => PerformanceStatistics.Mean(null!));
            Assert.Throws<ArgumentException>(() => PerformanceStatistics.Mean(Array.Empty<double>()));
            Assert.Throws<ArgumentException>(() => PerformanceStatistics.Mean(new[] { 0.01, double.NaN }));
            Assert.Throws<ArgumentException>(() => PerformanceStatistics.StandardDeviation(new[] { 0.01 }));
            Assert.Throws<ArgumentException>(() => PerformanceStatistics.Skewness(new[] { 0.01, 0.02 }));
            Assert.Throws<ArgumentException>(() => PerformanceStatistics.ExcessKurtosis(new[] { 0.01, 0.02, 0.03 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => PerformanceStatistics.Volatility(R, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => PerformanceStatistics.RollingVolatility(R, 0, 12));
            Assert.Throws<ArgumentOutOfRangeException>(() => PerformanceStatistics.RollingVolatility(R, R.Length + 1, 12));
            Assert.Throws<ArgumentException>(() => PerformanceStatistics.RollingSharpe(R, 1, 12));
            Assert.Throws<ArgumentException>(() => PerformanceStatistics.MaxDrawdownOfLevels(new[] { 1.0, 0.0 }));
        }

        [Fact]
        public void ConstantSeriesHasZeroVolatilityAndUndefinedHigherMoments()
        {
            var flat = new[] { 0.01, 0.01, 0.01, 0.01 };
            Assert.Equal(0.0, PerformanceStatistics.StandardDeviation(flat));
            Assert.True(double.IsNaN(PerformanceStatistics.Skewness(flat)));
            Assert.True(double.IsNaN(PerformanceStatistics.ExcessKurtosis(flat)));
            Assert.Equal(double.PositiveInfinity, PerformanceStatistics.SharpeRatio(flat, 12));
        }
    }
}
