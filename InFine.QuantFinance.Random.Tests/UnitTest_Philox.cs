using System;
using InFine.QuantFinance.Random.DotNet;
using Xunit;

namespace InFine.QuantFinance.Random.Tests
{
    public class UnitTest_Philox
    {
        // Known-answer vectors for philox4x32 R=10, from Random123 tests/kat_vectors
        // (https://github.com/DEShawResearch/random123/blob/main/tests/kat_vectors, lines "philox4x32 10 ...").
        // Columns: counter c0..c3, key k0..k1, expected r0..r3.
        [Theory]
        [InlineData(0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u,
                    0x6627e8d5u, 0xe169c58du, 0xbc57ac4cu, 0x9b00dbd8u)]
        [InlineData(0xffffffffu, 0xffffffffu, 0xffffffffu, 0xffffffffu, 0xffffffffu, 0xffffffffu,
                    0x408f276du, 0x41c83b0eu, 0xa20bc7c6u, 0x6d5451fdu)]
        [InlineData(0x243f6a88u, 0x85a308d3u, 0x13198a2eu, 0x03707344u, 0xa4093822u, 0x299f31d0u,
                    0xd16cfe09u, 0x94fdccebu, 0x5001e420u, 0x24126ea1u)]
        public void KnownAnswer_Random123(uint c0, uint c1, uint c2, uint c3, uint k0, uint k1,
                                          uint e0, uint e1, uint e2, uint e3)
        {
            Philox4x32x10.Generate(k0, k1, c0, c1, c2, c3, out uint r0, out uint r1, out uint r2, out uint r3);
            Assert.Equal(e0, r0);
            Assert.Equal(e1, r1);
            Assert.Equal(e2, r2);
            Assert.Equal(e3, r3);
        }

        [Fact]
        public void CounterLayout_MatchesDocumentedWords()
        {
            ulong block = 0x0123456789ABCDEFUL;
            Philox4x32x10.Counter(block, 77u, 5u, out uint c0, out uint c1, out uint c2, out uint c3);
            Assert.Equal(0x89ABCDEFu, c0);
            Assert.Equal(77u, c1);
            Assert.Equal(5u, c2);
            Assert.Equal(0x01234567u, c3);
        }

        [Fact]
        public void Generator_WordsAreTheKernelFunctionOfPosition()
        {
            const ulong seed = 0xDEADBEEF12345678UL;
            var gen = new PhiloxGenerator(seed, pathIndex: 42, stream: 3);
            gen.Skip(4UL * 1000 + 2); // land mid-block
            uint w2 = gen.NextUInt32();
            uint w3 = gen.NextUInt32();
            uint w4 = gen.NextUInt32();

            Philox4x32x10.Generate(0x12345678u, 0xDEADBEEFu, 1000u, 42u, 3u, 0u, out _, out _, out uint r2, out uint r3);
            Philox4x32x10.Generate(0x12345678u, 0xDEADBEEFu, 1001u, 42u, 3u, 0u, out uint n0, out _, out _, out _);
            Assert.Equal(r2, w2);
            Assert.Equal(r3, w3);
            Assert.Equal(n0, w4);
            Assert.Equal(4UL * 1000 + 5, gen.Position);
        }

        [Fact]
        public void DistinctPathsAndStreams_GiveDistinctSubStreams()
        {
            var a = new double[64];
            var b = new double[64];
            var c = new double[64];
            new PhiloxGenerator(7, 0, 0).GenerateUniforms(UniformTransformEnum.UNBIAISED, 0, 1, 64, a);
            new PhiloxGenerator(7, 1, 0).GenerateUniforms(UniformTransformEnum.UNBIAISED, 0, 1, 64, b);
            new PhiloxGenerator(7, 0, 1).GenerateUniforms(UniformTransformEnum.UNBIAISED, 0, 1, 64, c);
            int sameAB = 0, sameAC = 0;
            for (int i = 0; i < 64; ++i)
            {
                if (a[i] == b[i]) ++sameAB;
                if (a[i] == c[i]) ++sameAC;
            }
            Assert.Equal(0, sameAB);
            Assert.Equal(0, sameAC);
        }

        [Theory]
        [InlineData(UniformTransformEnum.UNBIAISED)]
        [InlineData(UniformTransformEnum.STANDARD)]
        public void Uniforms_ChunkInvariant(UniformTransformEnum transform)
        {
            const int n = 1003;
            var whole = new double[n];
            new PhiloxGenerator(2024, 9, 1).GenerateUniforms(transform, -3.0, 5.0, n, whole);

            var gen = new PhiloxGenerator(2024, 9, 1);
            var chunked = new double[n];
            int[] sizes = { 1, 3, 7, 0, 2, 5, 11, 4, 13 };
            int offset = 0, s = 0;
            while (offset < n)
            {
                int size = Math.Min(sizes[s++ % sizes.Length], n - offset);
                var buffer = new double[size];
                gen.GenerateUniforms(transform, -3.0, 5.0, size, buffer);
                Array.Copy(buffer, 0, chunked, offset, size);
                offset += size;
            }
            Assert.Equal(whole, chunked);
        }

        [Fact]
        public void Gaussians_ChunkInvariant_ForEvenChunks()
        {
            const int n = 1000;
            var whole = new double[n];
            new PhiloxGenerator(31337).GenerateGaussians(GaussianTransformEnum.BoxMuller2, 0.5, 2.0, n, whole);

            var gen = new PhiloxGenerator(31337);
            var chunked = new double[n];
            int[] sizes = { 2, 6, 0, 10, 4, 14 };
            int offset = 0, s = 0;
            while (offset < n)
            {
                int size = Math.Min(sizes[s++ % sizes.Length], n - offset);
                var buffer = new double[size];
                gen.GenerateGaussians(GaussianTransformEnum.BoxMuller, 0.5, 2.0, size, buffer);
                Array.Copy(buffer, 0, chunked, offset, size);
                offset += size;
            }
            Assert.Equal(whole, chunked);
        }

        [Fact]
        public void Skip_EqualsDiscardingDraws()
        {
            var reference = new double[100];
            new PhiloxGenerator(99, 4, 2).GenerateUniforms(UniformTransformEnum.UNBIAISED, 0, 1, 100, reference);

            var gen = new PhiloxGenerator(99, 4, 2);
            gen.Skip(37);
            var tail = new double[63];
            gen.GenerateUniforms(UniformTransformEnum.UNBIAISED, 0, 1, 63, tail);
            for (int i = 0; i < 63; ++i)
                Assert.Equal(reference[37 + i], tail[i]);

            gen.Position = 10;
            var again = new double[1];
            gen.GenerateUniforms(UniformTransformEnum.UNBIAISED, 0, 1, 1, again);
            Assert.Equal(reference[10], again[0]);
        }

        [Fact]
        public void UniformConversions_StayStrictlyInsideUnitInterval()
        {
            Assert.Equal(Math.Pow(2, -33), Philox4x32x10.ToUniformOpen32(0u));
            Assert.Equal(1.0 - Math.Pow(2, -33), Philox4x32x10.ToUniformOpen32(uint.MaxValue));
            Assert.True(Philox4x32x10.ToUniformOpen32(uint.MaxValue) < 1.0);

            Assert.Equal(Math.Pow(2, -53), Philox4x32x10.ToUniformOpen52(0u, 0u));
            Assert.Equal(1.0 - Math.Pow(2, -53), Philox4x32x10.ToUniformOpen52(uint.MaxValue, uint.MaxValue));
            Assert.True(Philox4x32x10.ToUniformOpen52(uint.MaxValue, uint.MaxValue) < 1.0);
        }

        [Theory]
        [InlineData(UniformTransformEnum.UNBIAISED)]
        [InlineData(UniformTransformEnum.STANDARD)]
        public void Uniforms_MomentsAndOpenInterval(UniformTransformEnum transform)
        {
            const int n = 1_000_000;
            var u = new double[n];
            new PhiloxGenerator(20111112).GenerateUniforms(transform, 0.0, 1.0, n, u);

            double sum = 0.0;
            foreach (double x in u)
            {
                Assert.True(x > 0.0 && x < 1.0, $"uniform {x} outside (0,1)");
                sum += x;
            }
            double mean = sum / n;
            double ss = 0.0;
            foreach (double x in u) ss += (x - mean) * (x - mean);
            double variance = ss / (n - 1);

            // 4 sigma: sd(mean) = sqrt(1/12 / n); sd(var) = sqrt((mu4 - sigma^4) / n), mu4 = 1/80, sigma^4 = 1/144.
            Assert.InRange(mean, 0.5 - 4 * Math.Sqrt(1.0 / 12.0 / n), 0.5 + 4 * Math.Sqrt(1.0 / 12.0 / n));
            double sdVar = Math.Sqrt((1.0 / 80.0 - 1.0 / 144.0) / n);
            Assert.InRange(variance, 1.0 / 12.0 - 4 * sdVar, 1.0 / 12.0 + 4 * sdVar);
        }

        [Fact]
        public void Gaussians_Moments()
        {
            const int n = 1_000_000;
            var z = new double[n];
            new PhiloxGenerator(20111112).GenerateGaussians(GaussianTransformEnum.BoxMuller2, 0.0, 1.0, n, z);

            double sum = 0.0;
            foreach (double x in z)
            {
                Assert.False(double.IsNaN(x) || double.IsInfinity(x));
                sum += x;
            }
            double mean = sum / n;
            double ss = 0.0;
            foreach (double x in z) ss += (x - mean) * (x - mean);
            double variance = ss / (n - 1);

            // 4 sigma: sd(mean) = 1/sqrt(n); sd(var) = sqrt(2/n) for a standard normal.
            Assert.InRange(mean, -4.0 / Math.Sqrt(n), 4.0 / Math.Sqrt(n));
            Assert.InRange(variance, 1.0 - 4 * Math.Sqrt(2.0 / n), 1.0 + 4 * Math.Sqrt(2.0 / n));
        }

        [Fact]
        public void Gaussians_MeanAndStdevAreApplied()
        {
            var unit = new double[8];
            var scaled = new double[8];
            new PhiloxGenerator(5).GenerateGaussians(GaussianTransformEnum.BoxMuller2, 0.0, 1.0, 8, unit);
            new PhiloxGenerator(5).GenerateGaussians(GaussianTransformEnum.BoxMuller2, 3.0, 0.25, 8, scaled);
            for (int i = 0; i < 8; ++i)
                Assert.Equal(3.0 + 0.25 * unit[i], scaled[i], 15);
        }

        [Fact]
        public void UnsupportedTransforms_Throw()
        {
            var gen = new PhiloxGenerator(1);
            var target = new double[4];
            Assert.Throws<NotSupportedException>(() =>
                gen.GenerateGaussians(GaussianTransformEnum.NONE, 0, 1, 4, target));
            Assert.Throws<NotSupportedException>(() =>
                gen.GenerateUniforms(UniformTransformEnum.NONE, 0, 1, 4, target));
            Assert.Throws<ArgumentException>(() =>
                gen.GenerateGaussians(GaussianTransformEnum.BoxMuller2, 0, 1, 3, target));
        }
    }
}
