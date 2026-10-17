using System;
using InFine.QuantFinance.Numerics;
using Xunit;
using static InFine.QuantFinance.Risk.Tests.TestHelpers;

namespace InFine.QuantFinance.Risk.Tests
{
    /// <summary>Ledoit-Wolf shrinkage towards constant correlation and towards the scaled identity.</summary>
    public class LedoitWolfTests
    {
        // ---- independent references ---------------------------------------------------------------------------

        [Fact]
        public void ConstantCorrelationMatchesTheTranscribedMatlabReference()
        {
            // Reference: Reference/ledoit_wolf_reference.py, a line-by-line transcription of the authors' covCor.m.
            ShrinkageEstimate e = LedoitWolf.ConstantCorrelation(ReferenceData.Returns);
            Assert.Equal(ReferenceData.ConstantCorrelationDelta, e.Intensity, 13);
            Close(ReferenceData.ConstantCorrelationCovariance, e.Covariance, 1e-12, "covCor");
            Close(ReferenceData.SampleMle, e.Sample, 1e-13, "sample");
        }

        [Fact]
        public void ScaledIdentityMatchesTheTranscribedMatlabReference()
        {
            // Reference: Reference/ledoit_wolf_reference.py, a line-by-line transcription of the authors' cov1Para.m.
            ShrinkageEstimate e = LedoitWolf.ScaledIdentity(ReferenceData.Returns);
            Assert.Equal(ReferenceData.ScaledIdentityDelta, e.Intensity, 13);
            Close(ReferenceData.ScaledIdentityCovariance, e.Covariance, 1e-12, "cov1Para");
        }

        /// <summary>
        /// scikit-learn's <c>ledoit_wolf_shrinkage</c> (sklearn/covariance/_shrunk_covariance.py) transcribed for a
        /// centred X: a third, differently arranged formulation of the same intensity.
        /// </summary>
        static double SklearnShrinkage(double[,] raw)
        {
            int ns = raw.GetLength(0), nf = raw.GetLength(1);
            double[] mean = Covariance.Mean(raw);
            var x = new double[ns, nf];
            for (int i = 0; i < ns; i++)
                for (int j = 0; j < nf; j++) x[i, j] = raw[i, j] - mean[j];
            double[,] x2 = new double[ns, nf];
            for (int i = 0; i < ns; i++)
                for (int j = 0; j < nf; j++) x2[i, j] = x[i, j] * x[i, j];
            double[,] xtx = Matrix.Multiply(Matrix.Transpose(x), x);
            double[,] x2tx2 = Matrix.Multiply(Matrix.Transpose(x2), x2);
            double empCovTrace = 0.0;
            for (int j = 0; j < nf; j++)
                for (int i = 0; i < ns; i++) empCovTrace += x2[i, j] / ns;
            double mu = empCovTrace / nf;
            double beta = 0.0, delta = 0.0;
            for (int i = 0; i < nf; i++)
                for (int j = 0; j < nf; j++)
                {
                    beta += x2tx2[i, j];
                    delta += xtx[i, j] * xtx[i, j];
                }
            delta /= (double)ns * ns;
            beta = 1.0 / ((double)nf * ns) * (beta / ns - delta);
            delta = delta - 2.0 * mu * empCovTrace + nf * mu * mu;
            delta /= nf;
            beta = Math.Min(beta, delta);
            return beta / delta;
        }

        [Theory]
        [InlineData(12, 4, 1)]
        [InlineData(60, 8, 2)]
        [InlineData(10, 25, 3)] // more assets than dates
        [InlineData(250, 30, 4)]
        public void ScaledIdentityIntensityMatchesScikitLearnFormula(int t, int n, int seed)
        {
            double[,] x = RandomReturns(t, n, seed);
            Assert.Equal(SklearnShrinkage(x), LedoitWolf.ScaledIdentity(x).Intensity, 12);
        }

        [Fact]
        public void ScaledIdentityIntensityMatchesScikitLearnOnTheReferenceData()
        {
            Assert.Equal(ReferenceData.ScaledIdentityDelta, SklearnShrinkage(ReferenceData.Returns), 12);
        }

        // ---- structure and properties --------------------------------------------------------------------------

        [Fact]
        public void ConstantCorrelationTargetKeepsVariancesAndHasOneCorrelation()
        {
            ShrinkageEstimate e = LedoitWolf.ConstantCorrelation(ReferenceData.Returns);
            double[,] f = e.Target, s = e.Sample;
            Assert.Equal(ReferenceData.AverageCorrelation, Covariance.ToCorrelation(f)[0, 1], 13);
            double[,] cf = Covariance.ToCorrelation(f);
            double sum = 0.0;
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(s[i, i], f[i, i]);
                for (int j = 0; j < i; j++)
                {
                    Assert.Equal(cf[0, 1], cf[i, j], 13);
                    sum += Covariance.ToCorrelation(s)[i, j];
                }
            }
            Assert.Equal(ReferenceData.AverageCorrelation, sum / 6.0, 13); // rBar is the mean sample correlation
        }

        [Fact]
        public void ShrunkCovarianceIsTheConvexCombinationOfSampleAndTarget()
        {
            foreach (ShrinkageEstimate e in new[] { LedoitWolf.ConstantCorrelation(ReferenceData.Returns), LedoitWolf.ScaledIdentity(ReferenceData.Returns) })
                for (int i = 0; i < 4; i++)
                    for (int j = 0; j < 4; j++)
                        Assert.Equal(e.Intensity * e.Target[i, j] + (1 - e.Intensity) * e.Sample[i, j], e.Covariance[i, j], 14);
        }

        [Theory]
        [InlineData(30, 5, 1)]
        [InlineData(100, 10, 2)]
        [InlineData(500, 6, 3)]
        [InlineData(15, 12, 4)]
        public void IntensityIsInUnitIntervalAndEstimatesAreSymmetricPositiveSemidefinite(int t, int n, int seed)
        {
            double[,] x = RandomReturns(t, n, seed);
            foreach (ShrinkageEstimate e in new[] { LedoitWolf.ConstantCorrelation(x), LedoitWolf.ScaledIdentity(x) })
            {
                Assert.InRange(e.Intensity, 0.0, 1.0);
                Assert.Equal(0.0, Matrix.MaxAsymmetry(e.Covariance));
                Assert.True(MinEigenvalue(e.Covariance) >= -1e-18, $"min eigenvalue {MinEigenvalue(e.Covariance):R}");
            }
        }

        [Fact]
        public void ScaledIdentityIsWellConditionedEvenWhenAssetsOutnumberDates()
        {
            double[,] x = RandomReturns(8, 20, 5); // sample covariance has rank at most 7
            ShrinkageEstimate e = LedoitWolf.ScaledIdentity(x);
            Assert.True(MinEigenvalue(e.Sample) < 1e-12 * 1e-4, "sample covariance is singular");
            Assert.True(e.Intensity > 0.0);
            Assert.True(MinEigenvalue(e.Covariance) > 0.0);
            Assert.True(Cholesky.TryFactor(e.Covariance, out _));
            // Shrinkage towards mu I lifts the smallest eigenvalue by delta * mu and keeps the largest at or below the sample's.
            double[] vs = SymmetricEigen.Decompose(e.Sample).Values, vc = SymmetricEigen.Decompose(e.Covariance).Values;
            Assert.True(vc[19] <= vs[19] + 1e-18);
            Assert.True(vc[0] >= vs[0]);
        }

        [Fact]
        public void IntensityTendsToZeroWithManyObservationsAndHighWhenDatesAreScarce()
        {
            double few = LedoitWolf.ScaledIdentity(RandomReturns(12, 10, 6)).Intensity;
            double many = LedoitWolf.ScaledIdentity(RandomReturns(20000, 10, 6)).Intensity;
            Assert.True(many < 0.02, $"intensity with 20000 dates is {many}");
            Assert.True(few > 10 * many);
            Assert.True(LedoitWolf.ConstantCorrelation(RandomReturns(20000, 10, 6)).Intensity < 0.05);
        }

        [Fact]
        public void EstimatesAreCovariantWithScalingOfTheReturns()
        {
            // x -> c x scales S by c^2 and leaves the (dimensionless) intensity unchanged.
            const double c = 37.5;
            var scaled = new double[12, 4];
            for (int i = 0; i < 12; i++)
                for (int j = 0; j < 4; j++) scaled[i, j] = c * ReferenceData.Returns[i, j];
            foreach (var (a, b) in new[]
            {
                (LedoitWolf.ConstantCorrelation(ReferenceData.Returns), LedoitWolf.ConstantCorrelation(scaled)),
                (LedoitWolf.ScaledIdentity(ReferenceData.Returns), LedoitWolf.ScaledIdentity(scaled)),
            })
            {
                Assert.Equal(a.Intensity, b.Intensity, 12);
                for (int i = 0; i < 4; i++)
                    for (int j = 0; j < 4; j++)
                        Assert.Equal(c * c * a.Covariance[i, j], b.Covariance[i, j], 9);
            }
        }

        [Fact]
        public void ConstantCorrelationIsInvariantToTheMeanOfTheReturns()
        {
            var shifted = new double[12, 4];
            for (int i = 0; i < 12; i++)
                for (int j = 0; j < 4; j++) shifted[i, j] = ReferenceData.Returns[i, j] + 100.0 * (j + 1);
            Assert.Equal(ReferenceData.ConstantCorrelationDelta, LedoitWolf.ConstantCorrelation(shifted).Intensity, 10);
            Assert.Equal(ReferenceData.ScaledIdentityDelta, LedoitWolf.ScaledIdentity(shifted).Intensity, 10);
        }

        [Fact]
        public void DegenerateTargetsReportFullIntensity()
        {
            // Two assets: the constant-correlation target is the sample covariance itself.
            var two = new double[,] { { 1, 2 }, { -1, 0 }, { 2, 1 }, { 0, -3 }, { 3, 1 } };
            ShrinkageEstimate e = LedoitWolf.ConstantCorrelation(two);
            Close(e.Sample, e.Covariance, 1e-14, "N = 2");
            // Orthogonal unit-variance columns: S = I = m I, so the sample is its own target.
            var orth = new double[,] { { 1, 1 }, { 1, -1 }, { -1, 1 }, { -1, -1 } };
            ShrinkageEstimate id = LedoitWolf.ScaledIdentity(orth);
            Assert.Equal(1.0, id.Intensity);
            Close(Matrix.Identity(2), id.Covariance, 1e-15, "I");
        }

        [Fact]
        public void InvalidInputIsRejected()
        {
            Assert.Throws<ArgumentException>(() => LedoitWolf.ConstantCorrelation(new double[5, 1]));
            Assert.Throws<ArgumentException>(() => LedoitWolf.ConstantCorrelation(new double[1, 3]));
            Assert.Throws<ArgumentException>(() => LedoitWolf.ConstantCorrelation(new double[,] { { 1, 0 }, { 2, 0 }, { 3, 0 } })); // zero-variance asset
            Assert.Throws<ArgumentException>(() => LedoitWolf.ScaledIdentity(new double[,] { { 1, double.NaN }, { 2, 0 } }));
            Assert.Throws<ArgumentNullException>(() => LedoitWolf.ScaledIdentity(null!));
        }
    }
}
