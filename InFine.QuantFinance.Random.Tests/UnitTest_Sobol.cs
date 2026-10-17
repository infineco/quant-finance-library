using System;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Random.QuasiRandom;
using Xunit;

namespace InFine.QuantFinance.Random.Tests
{
    public class UnitTest_Sobol
    {
        // Independent reference: a small Python implementation written from Joe & Kuo's sobol.cc
        // (sequential Gray-code generation, x_n = x_(n-1) ^ V[lowest zero bit of n-1]) reading the same
        // new-joe-kuo-6.21201 file. Points n = 0..16 of dimensions 1..5 as 32-bit integers (x * 2^32).
        private static readonly uint[][] ReferenceFirstPoints =
        {
            new uint[] { 0u, 2147483648u, 3221225472u, 1073741824u, 1610612736u, 3758096384u, 2684354560u, 536870912u, 805306368u, 2952790016u, 4026531840u, 1879048192u, 1342177280u, 3489660928u, 2415919104u, 268435456u, 402653184u },
            new uint[] { 0u, 2147483648u, 1073741824u, 3221225472u, 1610612736u, 3758096384u, 536870912u, 2684354560u, 1342177280u, 3489660928u, 268435456u, 2415919104u, 805306368u, 2952790016u, 1879048192u, 4026531840u, 2013265920u },
            new uint[] { 0u, 2147483648u, 1073741824u, 3221225472u, 2684354560u, 536870912u, 3758096384u, 1610612736u, 4026531840u, 1879048192u, 2952790016u, 805306368u, 1342177280u, 3489660928u, 268435456u, 2415919104u, 2013265920u },
            new uint[] { 0u, 2147483648u, 1073741824u, 3221225472u, 3758096384u, 1610612736u, 2684354560u, 536870912u, 1879048192u, 4026531840u, 805306368u, 2952790016u, 2415919104u, 268435456u, 3489660928u, 1342177280u, 2818572288u },
            new uint[] { 0u, 2147483648u, 3221225472u, 1073741824u, 1610612736u, 3758096384u, 2684354560u, 536870912u, 2415919104u, 268435456u, 1342177280u, 3489660928u, 4026531840u, 1879048192u, 805306368u, 2952790016u, 1207959552u },
        };

        [Fact]
        public void FirstPoints_Dims1To5_MatchIndependentReference()
        {
            uint[] v = Sobol.CreateDirectionNumbers(5);
            for (int d = 0; d < 5; ++d)
                for (uint n = 0; n < ReferenceFirstPoints[d].Length; ++n)
                    Assert.Equal(ReferenceFirstPoints[d][n], Sobol.Point(n, d, v));
        }

        [Fact]
        public void FirstPoints_MatchClassicValues()
        {
            // Classic Gray-code Sobol points, origin skipped (also the output of Joe & Kuo's sobol.cc,
            // https://web.maths.unsw.edu.au/~fkuo/sobol/ , for "./sobol 10 3 new-joe-kuo-6.21201").
            double[] dim1 = { 0.5, 0.75, 0.25, 0.375, 0.875, 0.625, 0.125, 0.1875, 0.6875 };
            double[] dim2 = { 0.5, 0.25, 0.75, 0.375, 0.875, 0.125, 0.625, 0.3125, 0.8125 };
            double[] dim3 = { 0.5, 0.25, 0.75, 0.625, 0.125, 0.875, 0.375, 0.9375, 0.4375 };

            var gen = new SobolGenerator(3);
            var u = new double[3 * dim1.Length];
            gen.GenerateUniforms(UniformTransformEnum.STANDARD, 0.0, 1.0, u.Length, u);
            for (int n = 0; n < dim1.Length; ++n)
            {
                Assert.Equal(dim1[n], u[3 * n]);
                Assert.Equal(dim2[n], u[3 * n + 1]);
                Assert.Equal(dim3[n], u[3 * n + 2]);
            }
        }

        [Theory]
        // sum_d (d * x_n,d) mod 2^64 over all 1111 dimensions (d 1-based), from the independent Python reference.
        [InlineData(1000u, 1328192002457600UL)]
        [InlineData(4095u, 1360244265451520UL)]
        [InlineData(123456789u, 1283088032882496UL)]
        [InlineData(4294967295u, 1336996567033696UL)]
        public void WholeTable_ChecksumMatchesIndependentReference(uint index, ulong expected)
        {
            uint[] v = Sobol.CreateDirectionNumbers(Sobol.MaxDimension);
            ulong h = 0;
            for (int d = 0; d < Sobol.MaxDimension; ++d)
                h += (ulong)Sobol.Point(index, d, v) * (ulong)(d + 1);
            Assert.Equal(expected, h);
        }

        [Fact]
        public void HighDimensions_FirstPointsMatchIndependentReference()
        {
            uint[] v = Sobol.CreateDirectionNumbers(Sobol.MaxDimension);
            uint[] dim1111 = { 0u, 2147483648u, 3221225472u, 1073741824u, 3758096384u, 1610612736u, 536870912u, 2684354560u, 1342177280u };
            uint[] dim1024 = { 0u, 2147483648u, 3221225472u, 1073741824u, 3758096384u, 1610612736u, 536870912u, 2684354560u, 268435456u };
            uint[] dim500 = { 0u, 2147483648u, 1073741824u, 3221225472u, 2684354560u, 536870912u, 3758096384u, 1610612736u, 1879048192u };
            for (uint n = 0; n < 9; ++n)
            {
                Assert.Equal(dim1111[n], Sobol.Point(n, 1110, v));
                Assert.Equal(dim1024[n], Sobol.Point(n, 1023, v));
                Assert.Equal(dim500[n], Sobol.Point(n, 499, v));
            }
        }

        [Fact]
        public void DirectionNumbers_AreValid()
        {
            Assert.Equal(1111, Sobol.MaxDimension);
            uint[] v = Sobol.CreateDirectionNumbers(Sobol.MaxDimension);
            for (int d = 0; d < Sobol.MaxDimension; ++d)
                for (int k = 0; k < Sobol.Bits; ++k)
                {
                    // v_(k+1) = m_(k+1) 2^(31-k) with m odd: the lowest set bit is bit 31 - k.
                    uint vk = v[d * Sobol.Bits + k];
                    Assert.Equal(1u << (31 - k), vk & (uint)-(int)vk);
                }
            Assert.Throws<ArgumentOutOfRangeException>(() => Sobol.CreateDirectionNumbers(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Sobol.CreateDirectionNumbers(Sobol.MaxDimension + 1));
        }

        [Fact]
        public void RandomAccess_EqualsSequential()
        {
            const int dims = 40;
            const int count = 5000;
            uint[] v = Sobol.CreateDirectionNumbers(dims);
            var gen = new SobolGenerator(dims, skipZeroPoint: false);
            var point = new uint[dims];
            for (uint n = 0; n < count; ++n)
            {
                gen.NextPoint(point);
                for (int d = 0; d < dims; ++d)
                    Assert.Equal(Sobol.Point(n, d, v), point[d]);
            }

            // Gray-code step across all dimensions of the table.
            uint[] all = Sobol.CreateDirectionNumbers(Sobol.MaxDimension);
            for (int d = 0; d < Sobol.MaxDimension; d += 7)
            {
                uint x = 0u;
                for (uint n = 1; n < 600; ++n)
                {
                    x = Sobol.NextPoint(x, n, d, all);
                    Assert.Equal(Sobol.Point(n, d, all), x);
                }
            }
        }

        [Fact]
        public void Skip_AndIndex_GiveRandomAccess()
        {
            const int dims = 7;
            uint[] v = Sobol.CreateDirectionNumbers(dims);
            var gen = new SobolGenerator(dims);
            Assert.Equal(1L, gen.Index); // origin skipped by default
            var point = new uint[dims];

            gen.Skip(1_000_000);
            gen.NextPoint(point);
            for (int d = 0; d < dims; ++d) Assert.Equal(Sobol.Point(1_000_001u, d, v), point[d]);
            gen.NextPoint(point);
            for (int d = 0; d < dims; ++d) Assert.Equal(Sobol.Point(1_000_002u, d, v), point[d]);

            gen.Index = 12345;
            gen.NextPoint(point);
            for (int d = 0; d < dims; ++d) Assert.Equal(Sobol.Point(12345u, d, v), point[d]);

            gen.Index = SobolGenerator.PointCount - 1;
            gen.NextPoint(point);
            for (int d = 0; d < dims; ++d) Assert.Equal(Sobol.Point(uint.MaxValue, d, v), point[d]);
            Assert.Throws<InvalidOperationException>(() => gen.NextPoint(point));
        }

        [Fact]
        public void ChunkedGeneration_EqualsOneShot()
        {
            const int dims = 5;
            var a = new double[dims * 100];
            new SobolGenerator(dims).GenerateGaussians(GaussianTransformEnum.InverseCumulativeDistributionFunction, 0.0, 1.0, a.Length, a);

            var gen = new SobolGenerator(dims);
            var chunk = new double[dims * 10];
            for (int c = 0; c < 10; ++c)
            {
                gen.GenerateGaussians(GaussianTransformEnum.InverseCumulativeDistributionFunction, 0.0, 1.0, chunk.Length, chunk);
                for (int i = 0; i < chunk.Length; ++i)
                    Assert.Equal(a[c * chunk.Length + i], chunk[i]);
            }
        }

        [Theory]
        [InlineData(SobolScramblingKind.None)]
        [InlineData(SobolScramblingKind.DigitalShift)]
        [InlineData(SobolScramblingKind.LinearMatrixAndDigitalShift)]
        public void FirstPowerOfTwoPoints_AreStratifiedInEveryDimension(SobolScramblingKind scrambling)
        {
            // Every 1-D projection of points 0 .. 2^k - 1 has exactly one point per interval [j 2^-k, (j+1) 2^-k):
            // the top k bits of the coordinates are a permutation of 0 .. 2^k - 1.
            const int maxK = 12;
            int[] dims = { 0, 1, 2, 3, 4, 10, 31, 63, 100, 255, 511, 777, 1023, 1110 };
            var gen = new SobolGenerator(Sobol.MaxDimension, scrambling, seed: 0xC0FFEEUL, replication: 3, skipZeroPoint: false);
            var points = new uint[1 << maxK][];
            for (int n = 0; n < points.Length; ++n)
            {
                points[n] = new uint[Sobol.MaxDimension];
                gen.NextPoint(points[n]);
            }
            foreach (int d in dims)
            {
                for (int k = 0; k <= maxK; ++k)
                {
                    var seen = new bool[1 << k];
                    for (int n = 0; n < (1 << k); ++n)
                    {
                        int cell = k == 0 ? 0 : (int)(points[n][d] >> (32 - k));
                        Assert.False(seen[cell], $"dimension {d}, k = {k}: two points in cell {cell}");
                        seen[cell] = true;
                    }
                }
            }
        }

        [Fact]
        public void Gaussians_AreInverseCdfOfOpenUniforms()
        {
            const int dims = 3;
            var gen = new SobolGenerator(dims, skipZeroPoint: false);
            var z = new double[dims * 64];
            gen.GenerateGaussians(GaussianTransformEnum.InverseCumulativeDistributionFunction, 1.5, 2.0, z.Length, z);
            uint[] v = Sobol.CreateDirectionNumbers(dims);
            for (uint n = 0; n < 64; ++n)
                for (int d = 0; d < dims; ++d)
                {
                    double u = (Sobol.Point(n, d, v) + 0.5) / 4294967296.0;
                    Assert.Equal(1.5 + 2.0 * NormalDistribution.InverseCdf(u), z[n * dims + d]);
                    Assert.Equal(NormalDistribution.InverseCdf(u), Sobol.Gaussian(n, d, v, 0u));
                }
            // The origin maps to a finite extreme quantile.
            Assert.Equal(NormalDistribution.InverseCdf(Math.Pow(2, -33)), Sobol.ToGaussian(0u));
            Assert.True(Sobol.ToGaussian(0u) > -6.4 && Sobol.ToGaussian(uint.MaxValue) < 6.4);
        }

        [Fact]
        public void UnsupportedTransformsAndBadCounts_Throw()
        {
            var gen = new SobolGenerator(4);
            var target = new double[8];
            Assert.Throws<NotSupportedException>(() => gen.GenerateGaussians(GaussianTransformEnum.BoxMuller, 0, 1, 8, target));
            Assert.Throws<NotSupportedException>(() => gen.GenerateGaussians(GaussianTransformEnum.BoxMuller2, 0, 1, 8, target));
            Assert.Throws<NotSupportedException>(() => gen.GenerateUniforms(UniformTransformEnum.NONE, 0, 1, 8, target));
            Assert.Throws<ArgumentException>(() => gen.GenerateUniforms(UniformTransformEnum.STANDARD, 0, 1, 6, target));
        }

        [Fact]
        public void Scrambling_IsReproducibleAndReplicationsDiffer()
        {
            const int dims = 16;
            var a = new SobolGenerator(dims, SobolScramblingKind.LinearMatrixAndDigitalShift, seed: 42, replication: 0);
            var b = new SobolGenerator(dims, SobolScramblingKind.LinearMatrixAndDigitalShift, seed: 42, replication: 0);
            var c = new SobolGenerator(dims, SobolScramblingKind.LinearMatrixAndDigitalShift, seed: 42, replication: 1);
            Assert.Equal(a.Directions, b.Directions);
            Assert.Equal(a.Shifts, b.Shifts);
            Assert.NotEqual(a.Shifts, c.Shifts);
            Assert.NotEqual(a.Directions, c.Directions);

            // The shift words are the documented Philox words.
            uint[] shifts = SobolScrambling.CreateDigitalShifts(dims, 42, 1);
            var philox = new InFine.QuantFinance.Random.DotNet.PhiloxGenerator(42, 1, SobolScrambling.ShiftStream);
            for (int d = 0; d < dims; ++d) Assert.Equal(philox.NextUInt32(), shifts[d]);

            // Kernel view: point n of the scrambled generator = Point(n, d, Directions) ^ Shifts[d].
            var point = new uint[dims];
            for (uint n = 1; n < 100; ++n)
            {
                c.NextPoint(point);
                for (int d = 0; d < dims; ++d)
                    Assert.Equal(Sobol.Point(n, d, c.Directions) ^ c.Shifts[d], point[d]);
            }

            // The top bit of every direction integer is kept by the unit lower-triangular matrix.
            uint[] plain = Sobol.CreateDirectionNumbers(dims);
            for (int i = 0; i < plain.Length; ++i)
                Assert.Equal(plain[i] >> 31, c.Directions[i] >> 31);
        }

        [Theory]
        [InlineData(SobolScramblingKind.DigitalShift)]
        [InlineData(SobolScramblingKind.LinearMatrixAndDigitalShift)]
        public void ScrambledPoints_AreUniformAcrossReplications(SobolScramblingKind scrambling)
        {
            // Each randomised point is uniform on [0,1): the mean over R replications of a fixed coordinate is
            // 1/2 within 4 sigma, sigma = sqrt(1/12 / R).
            const int replications = 4096;
            const int dims = 8;
            var sums = new double[dims];
            var u = new double[dims];
            for (uint r = 0; r < replications; ++r)
            {
                var gen = new SobolGenerator(dims, scrambling, seed: 7, replication: r);
                gen.Index = 5;
                gen.NextUniforms(u);
                for (int d = 0; d < dims; ++d) sums[d] += u[d];
            }
            double tol = 4.0 * Math.Sqrt(1.0 / 12.0 / replications);
            for (int d = 0; d < dims; ++d)
                Assert.InRange(sums[d] / replications, 0.5 - tol, 0.5 + tol);
        }
    }
}
