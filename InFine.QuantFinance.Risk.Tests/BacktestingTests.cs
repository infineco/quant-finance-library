using System;
using Xunit;

namespace InFine.QuantFinance.Risk.Tests
{
    /// <summary>Exception counting, Kupiec POF and Christoffersen tests, and the chi-squared helpers.</summary>
    public class BacktestingTests
    {
        // ---- chi-squared helper -----------------------------------------------------------------------------------

        [Fact]
        public void ChiSquaredQuantilesFromTables()
        {
            // Textbook critical values: chi2(1) 95% = 3.841458820694124, 99% = 6.634896601021213; chi2(2) 95% = 5.991464547107979.
            Assert.Equal(0.95, ChiSquared.Cdf1(3.841458820694124), 12);
            Assert.Equal(0.05, ChiSquared.Survival1(3.841458820694124), 12);
            Assert.Equal(0.01, ChiSquared.Survival1(6.634896601021213), 12);
            Assert.Equal(0.05, ChiSquared.Survival2(5.991464547107979), 12);
            Assert.Equal(1.0, ChiSquared.Survival1(0.0));
            Assert.Equal(0.0, ChiSquared.Cdf1(-1.0));
            // Far tail keeps relative accuracy: P(chi2_1 > 100) = erfc(sqrt(50)) = 1.5233...e-23.
            Assert.True(Math.Abs(ChiSquared.Survival1(100.0) / 1.5239706048320992e-23 - 1.0) < 1e-12);
        }

        // ---- exception counting -----------------------------------------------------------------------------------

        [Fact]
        public void ExceptionsAreLossesStrictlyBeyondVar()
        {
            double[] pnl = { 1.0, -2.0, -3.5, 0.5, -3.0, -10.0 };
            double[] var = { 3.0, 3.0, 3.0, 3.0, 3.0, 12.0 };
            Assert.Equal(new[] { false, false, true, false, false, false }, Backtesting.Exceptions(pnl, var)); // -3.0 == VaR is not a breach
            Assert.Equal(1, Backtesting.CountExceptions(pnl, var));
            Assert.Equal(3, Backtesting.CountExceptions(pnl, 2.5));   // losses 3.5, 3.0 and 10 exceed 2.5
        }

        // ---- Kupiec -----------------------------------------------------------------------------------------------

        [Theory]
        // Jorion (Value at Risk, 3rd ed., Table 6.2): 99% VaR, 5% test (critical value 3.84): non-rejection N < 7 for T = 255,
        // 1 < N < 11 for T = 510, 4 < N < 17 for T = 1000. Statistics and p-values below computed independently in Python
        // (direct log-likelihoods; p = erfc(sqrt(LR/2))).
        [InlineData(255, 6, 3.415357526264515, 0.06459242047243673, false)]
        [InlineData(255, 7, 5.316341340978184, 0.021126323789771168, true)]
        [InlineData(510, 1, 4.974722886533934, 0.02572032375457423, true)]
        [InlineData(510, 2, 2.474621200348352, 0.11569731398702103, false)]
        [InlineData(510, 10, 3.7145996225970066, 0.05393855936949492, false)]
        [InlineData(510, 11, 5.179618602373324, 0.022853339786952385, true)]
        [InlineData(1000, 4, 4.70596454128431, 0.030058130684599033, true)]
        [InlineData(1000, 5, 3.0937383140152974, 0.078594055539747, false)]
        [InlineData(1000, 16, 3.0765534575433833, 0.07942867754062208, false)]
        [InlineData(1000, 17, 4.090972554545601, 0.04311282800651028, true)]
        [InlineData(250, 0, 5.025167926750726, 0.024981503053449705, true)]   // zero exceptions: 0 ln 0 = 0, LR = -2 n ln(0.99)
        [InlineData(250, 5, 1.956809788230622, 0.16185491719604275, false)]
        [InlineData(250, 10, 12.955491062356018, 0.0003189845082133834, true)]
        public void KupiecMatchesJorionsNonRejectionRegionsAndIndependentValues(int n, int x, double lr, double p, bool rejects)
        {
            LikelihoodRatioTest t = Backtesting.KupiecPof(n, x, 0.99);
            Assert.Equal(lr, t.Statistic, 11);
            Assert.Equal(p, t.PValue, 12);
            Assert.Equal(1, t.DegreesOfFreedom);
            Assert.Equal(rejects, t.Rejects(0.05));
            Assert.Equal(rejects, lr > 3.841458820694124);
        }

        [Fact]
        public void KupiecIsZeroWhenTheObservedRateEqualsTheExpectedRate()
        {
            LikelihoodRatioTest t = Backtesting.KupiecPof(500, 5, 0.99);
            Assert.Equal(0.0, t.Statistic, 12);
            Assert.Equal(1.0, t.PValue, 12);
            // All observations exceptions: LR = -2 n ln(p) at the other boundary.
            Assert.Equal(-2.0 * 20 * Math.Log(0.05), Backtesting.KupiecPof(20, 20, 0.95).Statistic, 12);
        }

        [Fact]
        public void KupiecFromIndicatorsAndFromPnl()
        {
            var pnl = new double[250];
            for (int i = 0; i < 250; i++) pnl[i] = (i % 25 == 3) ? -5.0 : 1.0;   // 10 breaches of VaR = 4
            Assert.Equal(10, Backtesting.CountExceptions(pnl, 4.0));
            LikelihoodRatioTest a = Backtesting.KupiecPof(Backtesting.Exceptions(pnl, 4.0), 0.99);
            LikelihoodRatioTest b = Backtesting.KupiecPof(250, 10, 0.99);
            Assert.Equal(b.Statistic, a.Statistic, 14);
            Assert.Equal(12.955491062356018, a.Statistic, 11);
        }

        // ---- Christoffersen ---------------------------------------------------------------------------------------

        static bool[] Series(string s)
        {
            var b = new bool[s.Length];
            for (int i = 0; i < b.Length; i++) b[i] = s[i] == '1';
            return b;
        }

        [Fact]
        public void IndependenceAndConditionalCoverageMatchIndependentComputation()
        {
            // 40 days, 7 exceptions; transitions n00 = 27, n01 = 5, n10 = 5, n11 = 2 (counted in Python together with the statistics).
            bool[] ex = Series("0010000110000000100000011000001000000000");
            LikelihoodRatioTest ind = Backtesting.ChristoffersenIndependence(ex);
            Assert.Equal(0.5946664013542033, ind.Statistic, 11);
            Assert.Equal(0.4406203021846504, ind.PValue, 11);
            Assert.Equal(1, ind.DegreesOfFreedom);

            LikelihoodRatioTest pof = Backtesting.KupiecPof(ex, 0.95);
            Assert.Equal(8.227494073781372, pof.Statistic, 11);
            LikelihoodRatioTest cc = Backtesting.ChristoffersenConditionalCoverage(ex, 0.95);
            Assert.Equal(2, cc.DegreesOfFreedom);
            Assert.Equal(8.822160475135576, cc.Statistic, 11);
            Assert.Equal(pof.Statistic + ind.Statistic, cc.Statistic, 14);
            Assert.Equal(0.012142054939105843, cc.PValue, 12);                   // exp(-LR/2)
            Assert.True(cc.Rejects(0.05));
        }

        [Fact]
        public void IndependenceDetectsClusteringButNotTheCount()
        {
            // The same 10 exceptions in 250 days: spread evenly versus in one block.
            var spread = new bool[250];
            var clustered = new bool[250];
            for (int i = 0; i < 10; i++)
            {
                spread[10 + 25 * i] = true;
                clustered[100 + i] = true;
            }
            Assert.Equal(Backtesting.KupiecPof(spread, 0.96).Statistic, Backtesting.KupiecPof(clustered, 0.96).Statistic, 14);
            Assert.False(Backtesting.ChristoffersenIndependence(spread).Rejects(0.05));
            LikelihoodRatioTest c = Backtesting.ChristoffersenIndependence(clustered);
            Assert.True(c.Rejects(0.001), $"p = {c.PValue}");
        }

        [Fact]
        public void IndependenceHandlesDegenerateSeries()
        {
            LikelihoodRatioTest none = Backtesting.ChristoffersenIndependence(new bool[30]);
            Assert.Equal(0.0, none.Statistic, 15);
            Assert.Equal(1.0, none.PValue, 15);
            var all = new bool[30];
            Array.Fill(all, true);
            Assert.Equal(0.0, Backtesting.ChristoffersenIndependence(all).Statistic, 15);
        }

        [Fact]
        public void RejectsBadArguments()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Backtesting.KupiecPof(0, 0, 0.99));
            Assert.Throws<ArgumentOutOfRangeException>(() => Backtesting.KupiecPof(10, 11, 0.99));
            Assert.Throws<ArgumentOutOfRangeException>(() => Backtesting.KupiecPof(10, -1, 0.99));
            Assert.Throws<ArgumentOutOfRangeException>(() => Backtesting.KupiecPof(10, 1, 1.0));
            Assert.Throws<ArgumentException>(() => Backtesting.Exceptions(new[] { 1.0, 2.0 }, new[] { 1.0 }));
            Assert.Throws<ArgumentException>(() => Backtesting.ChristoffersenIndependence(new[] { true }));
        }
    }
}
