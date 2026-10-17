using System;
using System.Globalization;
using InFine.QuantFinance.Risk;
using InFine.QuantFinance.TimeSeries;
using Xunit;

namespace InFine.QuantFinance.TimeSeries.Tests
{
    /// <summary>
    /// Factor regressions, Student-t / F tail probabilities, factor covariance models and PCA, checked against exact fits,
    /// the NIST StRD Longley certified values, closed-form sandwich variances and published t / F critical values.
    /// </summary>
    public class FactorModelTests
    {
        static void Close(double expected, double actual, double tol)
        {
            Assert.True(Math.Abs(expected - actual) <= tol, $"expected {expected:R}, got {actual:R} (tol {tol:E1})");
        }

        static void CloseRel(double expected, double actual, double rel)
        {
            Assert.True(Math.Abs(expected - actual) <= rel * Math.Abs(expected), $"expected {expected:R}, got {actual:R} (rel {rel:E1})");
        }

        static double Normal(System.Random rng)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        static double[,] RandomMatrix(System.Random rng, int rows, int cols, double scale)
        {
            var m = new double[rows, cols];
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++) m[i, j] = scale * Normal(rng);
            return m;
        }

        // ---------- Student-t / F ----------

        [Fact]
        public void IncompleteBetaMatchesClosedForms()
        {
            Close(0.3, StudentT.RegularizedIncompleteBeta(1, 1, 0.3), 1e-15);               // I_x(1,1) = x
            Close(Math.Pow(0.7, 3.5), StudentT.RegularizedIncompleteBeta(3.5, 1, 0.7), 1e-14);  // I_x(a,1) = x^a
            Close(1 - Math.Pow(0.3, 2.5), StudentT.RegularizedIncompleteBeta(1, 2.5, 0.7), 1e-14); // I_x(1,b) = 1-(1-x)^b
            Close(0.5, StudentT.RegularizedIncompleteBeta(7, 7, 0.5), 1e-14);               // symmetry
            Close(0.5 * (1 - 0.5 * 0.5 * 0.5 * 0.0) , StudentT.RegularizedIncompleteBeta(2, 2, 0.5), 1e-14); // 3x^2-2x^3 at 1/2
        }

        [Theory]
        [InlineData(1.0, 12.706204736432095)]
        [InlineData(2.0, 4.302652729911275)]
        [InlineData(3.0, 3.182446305284263)]
        [InlineData(5.0, 2.5705818366147395)]
        [InlineData(10.0, 2.2281388519649385)]
        [InlineData(30.0, 2.0422724563012373)]
        [InlineData(120.0, 1.9799304)]
        public void TwoSidedPValueAtPublishedCriticalValuesIsFivePercent(double df, double critical)
        {
            Close(0.05, StudentT.TwoSidedPValue(critical, df), df == 120.0 ? 1e-7 : 1e-9);
            Close(0.975, StudentT.Cdf(critical, df), df == 120.0 ? 1e-7 : 1e-9);
            Close(0.025, StudentT.Cdf(-critical, df), df == 120.0 ? 1e-7 : 1e-9);
        }

        [Fact]
        public void StudentTCdfClosedFormsForOneAndTwoDegreesOfFreedom()
        {
            foreach (double t in new[] { -3.0, -0.4, 0.0, 0.7, 1.0, 5.0 })
            {
                Close(0.5 + Math.Atan(t) / Math.PI, StudentT.Cdf(t, 1), 1e-14);                       // Cauchy
                Close(0.5 + t / (2.0 * Math.Sqrt(2.0 + t * t)), StudentT.Cdf(t, 2), 1e-14);
            }
            // Far tail keeps relative accuracy: Cauchy P(|T| > t) = 2/pi * atan(1/t).
            CloseRel(2.0 / Math.PI * Math.Atan(1e-8), StudentT.TwoSidedPValue(1e8, 1), 1e-12);
            Close(1.0, StudentT.TwoSidedPValue(0.0, 7), 1e-15);
        }

        [Fact]
        public void FSurvivalAtPublishedCriticalValuesAndTRelation()
        {
            Close(0.05, StudentT.FSurvival(3.325834530413012, 5, 10), 1e-11);
            Close(0.05, StudentT.FSurvival(3.885293835, 2, 12), 1e-9);
            // F(1, d) is the square of t(d).
            Close(StudentT.TwoSidedPValue(1.7, 9), StudentT.FSurvival(1.7 * 1.7, 1, 9), 1e-14);
            Close(1.0, StudentT.FSurvival(0.0, 3, 4), 0.0);
        }

        // ---------- QR / exact fit ----------

        [Fact]
        public void NoiseFreeDataRecoversAlphaAndBetasToMachinePrecision()
        {
            var rng = new System.Random(11);
            int t = 60, k = 4;
            double[,] f = RandomMatrix(rng, t, k, 0.02);
            double alpha = 0.0007;
            double[] beta = { 1.1, -0.4, 0.25, 0.0 };
            var y = new double[t];
            for (int i = 0; i < t; i++)
            {
                y[i] = alpha;
                for (int j = 0; j < k; j++) y[i] += beta[j] * f[i, j];
            }
            FactorRegressionResult r = FactorRegression.Fit(y, f);
            Close(alpha, r.Alpha, 1e-12);
            for (int j = 0; j < k; j++) Close(beta[j], r.Betas[j], 1e-12);
            foreach (double e in r.Residuals) Close(0.0, e, 1e-14);
            Close(1.0, r.RSquared, 1e-12);
            Close(0.0 * 1, r.ResidualVariance, 1e-26);
            Assert.Equal(t - k - 1, r.DegreesOfFreedom);
            Close(0.0007 * 252, r.AnnualisedAlpha(Periodicity.Daily), 1e-10);
        }

        [Fact]
        public void CollinearFactorsAreRejected()
        {
            var rng = new System.Random(3);
            double[,] f = RandomMatrix(rng, 30, 2, 1.0);
            var g = new double[30, 3];
            for (int i = 0; i < 30; i++) { g[i, 0] = f[i, 0]; g[i, 1] = f[i, 1]; g[i, 2] = f[i, 0] + f[i, 1]; }
            Assert.Throws<ArgumentException>(() => FactorRegression.Fit(new double[30], g));
        }

        // ---------- NIST Longley ----------

        // NIST StRD Longley.dat (linear regression, higher difficulty): y, x1..x6.
        static readonly double[,] Longley =
        {
            { 60323, 83.0, 234289, 2356, 1590, 107608, 1947 },
            { 61122, 88.5, 259426, 2325, 1456, 108632, 1948 },
            { 60171, 88.2, 258054, 3682, 1616, 109773, 1949 },
            { 61187, 89.5, 284599, 3351, 1650, 110929, 1950 },
            { 63221, 96.2, 328975, 2099, 3099, 112075, 1951 },
            { 63639, 98.1, 346999, 1932, 3594, 113270, 1952 },
            { 64989, 99.0, 365385, 1870, 3547, 115094, 1953 },
            { 63761, 100.0, 363112, 3578, 3350, 116219, 1954 },
            { 66019, 101.2, 397469, 2904, 3048, 117388, 1955 },
            { 67857, 104.6, 419180, 2822, 2857, 118734, 1956 },
            { 68169, 108.4, 442769, 2936, 2798, 120445, 1957 },
            { 66513, 110.8, 444546, 4681, 2637, 121950, 1958 },
            { 68655, 112.6, 482704, 3813, 2552, 123366, 1959 },
            { 69564, 114.2, 502601, 3931, 2514, 125368, 1960 },
            { 69331, 115.7, 518173, 4806, 2572, 127852, 1961 },
            { 70551, 116.9, 554894, 4007, 2827, 130081, 1962 },
        };

        [Fact]
        public void LongleyReproducesNistCertifiedValues()
        {
            int t = Longley.GetLength(0);
            var y = new double[t];
            var x = new double[t, 6];
            for (int i = 0; i < t; i++)
            {
                y[i] = Longley[i, 0];
                for (int j = 0; j < 6; j++) x[i, j] = Longley[i, j + 1];
            }
            FactorRegressionResult r = FactorRegression.Fit(y, x);

            double[] b = { -3482258.63459582, 15.0618722713733, -0.358191792925910E-01, -2.02022980381683,
                           -1.03322686717359, -0.511041056535807E-01, 1829.15146461355 };
            double[] se = { 890420.383607373, 84.9149257747669, 0.334910077722432E-01, 0.488399681651699,
                            0.214274163161675, 0.226073200069370, 455.478499142212 };
            // Householder QR on this cond ~ 1e9 problem gives about 10 correct digits; NIST considers 6+ digits a pass.
            for (int i = 0; i < 7; i++)
            {
                CloseRel(b[i], r.Coefficients[i], 1e-8);
                CloseRel(se[i], r.ClassicalStandardErrors[i], 1e-8);
            }
            CloseRel(304.854073561965, Math.Sqrt(r.ResidualVariance), 1e-9);
            CloseRel(0.995479004577296, r.RSquared, 1e-12);
            CloseRel(330.285339234588, r.FStatistic, 1e-8);
            CloseRel(836424.055505915, r.ResidualVariance * r.DegreesOfFreedom, 1e-9);
            Assert.True(r.FPValue < 1e-8);
            // p-values are tiny for x4..., large for x5 (t = -0.226): sanity of the t tail.
            Close(StudentT.TwoSidedPValue(r.ClassicalTStatistics[5], 9), r.ClassicalPValues[5], 0.0);
            Assert.InRange(r.ClassicalPValues[5], 0.8, 0.85);
        }

        // ---------- Standard errors ----------

        [Fact]
        public void NeweyWestLagZeroIsWhiteNotClassical_AndMatchesClosedFormForSlope()
        {
            var rng = new System.Random(21);
            int t = 400;
            var x = new double[t, 1];
            var y = new double[t];
            for (int i = 0; i < t; i++)
            {
                x[i, 0] = Normal(rng);
                // Strong heteroskedasticity: error scale grows with |x|.
                y[i] = 0.3 + 0.8 * x[i, 0] + (0.2 + 2.0 * Math.Abs(x[i, 0])) * Normal(rng);
            }
            FactorRegressionResult r = FactorRegression.Fit(y, x, hacLag: 0);

            double xbar = 0.0;
            for (int i = 0; i < t; i++) xbar += x[i, 0];
            xbar /= t;
            double sxx = 0.0, white = 0.0;
            for (int i = 0; i < t; i++)
            {
                double c = x[i, 0] - xbar;
                sxx += c * c;
                white += c * c * r.Residuals[i] * r.Residuals[i];
            }
            double whiteSe = Math.Sqrt(white) / sxx;                       // HC0 slope standard error
            double classicalSe = Math.Sqrt(r.ResidualVariance / sxx);
            Close(whiteSe, r.HacStandardErrors[1], 1e-12);
            Close(classicalSe, r.ClassicalStandardErrors[1], 1e-12);
            Assert.True(Math.Abs(whiteSe / classicalSe - 1.0) > 0.1, "data must separate HC0 from classical");
            Assert.Equal(0, r.HacLag);
        }

        [Fact]
        public void NeweyWestWithLagsMatchesClosedFormSlopeVariance()
        {
            var rng = new System.Random(5);
            int t = 300;
            var x = new double[t, 1];
            var y = new double[t];
            double e = 0.0;
            for (int i = 0; i < t; i++)
            {
                x[i, 0] = Normal(rng);
                e = 0.6 * e + Normal(rng);   // AR(1) errors
                y[i] = 0.1 + 1.5 * x[i, 0] + e;
            }
            foreach (int lag in new[] { 1, 4, 10 })
            {
                FactorRegressionResult r = FactorRegression.Fit(y, x, lag);
                double xbar = 0.0;
                for (int i = 0; i < t; i++) xbar += x[i, 0];
                xbar /= t;
                double sxx = 0.0;
                var u = new double[t];
                for (int i = 0; i < t; i++) { double c = x[i, 0] - xbar; sxx += c * c; u[i] = c * r.Residuals[i]; }
                double s = 0.0;
                for (int i = 0; i < t; i++) s += u[i] * u[i];
                for (int l = 1; l <= lag; l++)
                {
                    double g = 0.0;
                    for (int i = l; i < t; i++) g += u[i] * u[i - l];
                    s += 2.0 * (1.0 - l / (lag + 1.0)) * g;
                }
                Close(Math.Sqrt(s) / sxx, r.HacStandardErrors[1], 1e-12);
            }
        }

        [Fact]
        public void DefaultHacLagFollowsNeweyWestRuleOfThumb()
        {
            Assert.Equal(4, FactorRegression.DefaultHacLag(100));
            Assert.Equal(6, FactorRegression.DefaultHacLag(1000));
            Assert.Equal(2, FactorRegression.DefaultHacLag(10));
            Assert.Equal((int)Math.Floor(4.0 * Math.Pow(2.5, 2.0 / 9.0)), FactorRegression.DefaultHacLag(250));
            var rng = new System.Random(1);
            double[,] f = RandomMatrix(rng, 250, 1, 1.0);
            var y = new double[250];
            for (int i = 0; i < 250; i++) y[i] = f[i, 0] + Normal(rng);
            Assert.Equal(FactorRegression.DefaultHacLag(250), FactorRegression.Fit(y, f).HacLag);
        }

        [Fact]
        public void HomoskedasticDataGivesComparableClassicalAndHacErrorsAndFollowsFormulas()
        {
            var rng = new System.Random(8);
            int t = 5000, k = 3;
            double[,] f = RandomMatrix(rng, t, k, 1.0);
            var y = new double[t];
            for (int i = 0; i < t; i++) y[i] = 0.1 + 0.5 * f[i, 0] - 0.3 * f[i, 1] + 0.2 * f[i, 2] + Normal(rng);
            FactorRegressionResult r = FactorRegression.Fit(y, f);
            for (int j = 0; j < 4; j++) CloseRel(r.ClassicalStandardErrors[j], r.HacStandardErrors[j], 0.1);
            for (int j = 0; j < 4; j++) Close(r.Coefficients[j] / r.ClassicalStandardErrors[j], r.ClassicalTStatistics[j], 1e-9);
            // R2 / adjusted R2 / F consistency.
            double r2 = r.RSquared, df = r.DegreesOfFreedom;
            Close(1.0 - (1.0 - r2) * (t - 1.0) / df, r.AdjustedRSquared, 1e-14);
            Close(r2 / k / ((1.0 - r2) / df), r.FStatistic, 1e-8 * r.FStatistic);
            // True betas are within ~4 standard errors; alpha = 0.1 is about 7 standard errors from 0, so it is rejected at 1e-3 even after sampling noise.
            Assert.True(r.ClassicalPValues[0] < 1e-3);
            Close(0.5, r.Betas[0], 4.0 * r.ClassicalStandardErrors[1]);
        }

        // ---------- Rolling ----------

        [Fact]
        public void RollingRegressionTracksTimeVaryingBeta()
        {
            var rng = new System.Random(4);
            int t = 200, w = 60;
            double[,] f = RandomMatrix(rng, t, 1, 0.01);
            var y = new double[t];
            for (int i = 0; i < t; i++) y[i] = (i < 100 ? 0.5 : 1.5) * f[i, 0];   // noise-free regime switch
            FactorRegressionResult[] roll = FactorRegression.Rolling(y, f, w);
            Assert.Equal(t - w + 1, roll.Length);
            Close(0.5, roll[0].Betas[0], 1e-12);                 // window 0..59
            Close(0.5, roll[40].Betas[0], 1e-12);                // window 40..99
            Close(1.5, roll[100].Betas[0], 1e-12);               // window 100..159
            Close(1.5, roll[t - w].Betas[0], 1e-12);
            // A straddling window matches a direct fit on the same slice.
            var ys = new double[w];
            var fs = new double[w, 1];
            for (int i = 0; i < w; i++) { ys[i] = y[70 + i]; fs[i, 0] = f[70 + i, 0]; }
            Close(FactorRegression.Fit(ys, fs).Betas[0], roll[70].Betas[0], 1e-14);
            Assert.NotEqual(roll[70].Betas[0], roll[40].Betas[0]);
        }

        // ---------- Factor covariance model ----------

        [Fact]
        public void FactorModelReconstructsSampleCovarianceWhenResidualsAreUncorrelated()
        {
            var rng = new System.Random(77);
            int t = 40000, k = 2, n = 3;
            double[,] f = RandomMatrix(rng, t, k, 0.01);
            double[,] b = { { 1.0, 0.5 }, { 0.8, -0.3 }, { 0.2, 1.1 } };
            double[] idio = { 0.01, 0.02, 0.015 };
            var r = new double[t, n];
            for (int i = 0; i < t; i++)
                for (int a = 0; a < n; a++)
                {
                    double v = 0.0004 * 0 + idio[a] * Normal(rng);   // independent specific returns
                    for (int j = 0; j < k; j++) v += b[a, j] * f[i, j];
                    r[i, a] = v;
                }
            FactorCovarianceModel model = FactorCovarianceModel.Estimate(r, f);
            double[,] implied = model.AssetCovariance();
            double[,] sample = Covariance.Sample(r);
            // Exact identity: OLS residuals are orthogonal to the factors, so sample cov = B Sf B' + Cov(resid)
            // and the model differs from it only by the off-diagonal residual covariances and the T-1 vs T-K-1 divisor.
            for (int a = 0; a < n; a++)
                for (int c = 0; c < n; c++)
                {
                    double tol = a == c ? 1e-5 * 0.0 + idio[a] * idio[a] * 1e-3 : 4.0 * idio[a] * idio[c] / Math.Sqrt(t);
                    Close(sample[a, c], implied[a, c], tol);
                }
            // Estimated loadings are close to the truth (standard error ~ idio / (0.01 sqrt(T)) ~ 0.005..0.01).
            for (int a = 0; a < n; a++)
                for (int j = 0; j < k; j++) Close(b[a, j], model.Loadings[a, j], 0.04);
            // Symmetric, positive definite.
            Assert.True(Numerics.Cholesky.TryFactor(implied, out _));
            Close(0.0, Numerics.Matrix.MaxAsymmetry(implied), 1e-18);
        }

        [Fact]
        public void RiskDecompositionAddsUpAndMatchesFullCovariance()
        {
            double[,] b = { { 1.0, 0.5 }, { 0.8, -0.3 }, { 0.2, 1.1 } };
            double[,] sf = { { 0.04, 0.006 }, { 0.006, 0.01 } };
            double[] d = { 0.02, 0.03, 0.015 };
            var model = new FactorCovarianceModel(b, sf, d);
            double[] w = { 0.5, 0.3, 0.2 };
            FactorRiskDecomposition rd = model.Decompose(w);
            double[,] cov = model.AssetCovariance();
            double full = 0.0;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++) full += w[i] * cov[i, j] * w[j];
            Close(full, rd.TotalVariance, 1e-14);
            double sumContrib = 0.0;
            foreach (double c in rd.FactorContributions) sumContrib += c;
            Close(rd.FactorVariance, sumContrib, 1e-15);
            Close(0.5 * 0.02 * 0.5 + 0.3 * 0.03 * 0.3 + 0.2 * 0.015 * 0.2, rd.SpecificVariance, 1e-16);
            Close(1.0, rd.FactorShare + rd.SpecificShare, 1e-15);
            Close(Math.Sqrt(full), rd.TotalVolatility, 1e-14);
            Close(0.5 * 1.0 + 0.3 * 0.8 + 0.2 * 0.2, rd.FactorExposures[0], 1e-15);
        }

        [Fact]
        public void AnnualisationScalesCovarianceAndRiskLinearly()
        {
            double[,] b = { { 1.0, 0.5 }, { 0.8, -0.3 } };
            double[,] sf = { { 0.04, 0.006 }, { 0.006, 0.01 } };
            var model = new FactorCovarianceModel(b, sf, new[] { 0.02, 0.03 });
            FactorCovarianceModel yearly = model.Annualise(Periodicity.Monthly);
            double[,] c1 = model.AssetCovariance(), c12 = yearly.AssetCovariance();
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++) Close(12.0 * c1[i, j], c12[i, j], 1e-14);
            double[,] viaRisk = Covariance.Annualise(c1, Periodicity.Monthly);
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++) Close(viaRisk[i, j], c12[i, j], 1e-14);
            var w = new[] { 0.6, 0.4 };
            Close(Math.Sqrt(12.0) * model.Decompose(w).TotalVolatility, yearly.Decompose(w).TotalVolatility, 1e-14);
        }

        // ---------- PCA ----------

        [Fact]
        public void PcaExplainedVarianceSumsToOneAndIsDescending()
        {
            var rng = new System.Random(31);
            int t = 500, n = 6;
            double[,] common = RandomMatrix(rng, t, 1, 0.02);
            var r = new double[t, n];
            for (int i = 0; i < t; i++)
                for (int a = 0; a < n; a++) r[i, a] = (0.5 + 0.2 * a) * common[i, 0] + 0.01 * Normal(rng);
            StatisticalFactorResult pca = StatisticalFactors.FromReturns(r, 2);
            double sum = 0.0;
            foreach (double v in pca.ExplainedVarianceRatio) sum += v;
            Close(1.0, sum, 1e-14);
            for (int i = 1; i < n; i++) Assert.True(pca.Eigenvalues[i - 1] >= pca.Eigenvalues[i]);
            Assert.True(pca.ExplainedVarianceRatio[0] > 0.75);   // one dominant common factor
            Close(pca.ExplainedVarianceRatio[0] + pca.ExplainedVarianceRatio[1], pca.RetainedVarianceRatio, 1e-15);
            // Eigenvalues sum to the trace of the sample covariance.
            double[,] cov = Covariance.Sample(r);
            double tr = 0.0, ev = 0.0;
            for (int a = 0; a < n; a++) { tr += cov[a, a]; ev += pca.Eigenvalues[a]; }
            Close(tr, ev, 1e-14);
            // Loadings orthonormal, sign convention: largest |entry| positive; first PC loads on all assets with one sign.
            for (int c = 0; c < 2; c++)
            {
                double norm = 0.0, biggest = 0.0;
                for (int a = 0; a < n; a++) { norm += pca.Loadings[a, c] * pca.Loadings[a, c]; if (Math.Abs(pca.Loadings[a, c]) > Math.Abs(biggest)) biggest = pca.Loadings[a, c]; }
                Close(1.0, norm, 1e-13);
                Assert.True(biggest > 0.0);
            }
            double dot = 0.0;
            for (int a = 0; a < n; a++) dot += pca.Loadings[a, 0] * pca.Loadings[a, 1];
            Close(0.0, dot, 1e-12);
            // Scores of PC1 have variance equal to the first eigenvalue.
            double[,] scores = pca.Scores(r);
            double m = 0.0, sv = 0.0;
            for (int i = 0; i < t; i++) m += scores[i, 0];
            m /= t;
            for (int i = 0; i < t; i++) sv += (scores[i, 0] - m) * (scores[i, 0] - m);
            Close(pca.Eigenvalues[0], sv / (t - 1), 1e-14);
        }

        [Fact]
        public void PcaOfKnownCovarianceGivesKnownEigenvalues()
        {
            // Equicorrelation matrix with rho = 0.6, n = 4: eigenvalues 1 + 3 rho = 2.8 and 1 - rho = 0.4 (x3).
            var c = new double[4, 4];
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++) c[i, j] = i == j ? 1.0 : 0.6;
            StatisticalFactorResult pca = StatisticalFactors.FromCovariance(c, 1);
            Close(2.8, pca.Eigenvalues[0], 1e-13);
            Close(0.4, pca.Eigenvalues[3], 1e-13);
            Close(2.8 / 4.0, pca.ExplainedVarianceRatio[0], 1e-13);
            for (int a = 0; a < 4; a++) Close(0.5, pca.Loadings[a, 0], 1e-13);
        }
    }
}
