using System;
using InFine.QuantFinance.Arithmetic.AutoDiff;
using Xunit;

namespace InFine.QuantFinance.Arithmetic.Tests
{
    public class DualTests
    {
        const double Tight = 1e-14;

        static void Close(double expected, double actual, double relative)
        {
            double tolerance = relative * Math.Max(1.0, Math.Abs(expected));
            Assert.True(Math.Abs(expected - actual) <= tolerance,
                $"expected {expected:R}, got {actual:R} (|diff| {Math.Abs(expected - actual):E3} > {tolerance:E3})");
        }

        [Fact]
        public void ConstantsBehaveLikeDoubles()
        {
            Dual a = 15.0, b = 27.0;
            Dual c = a + b;
            Assert.Equal(42.0, c.Value);
            Assert.Equal(0.0, c.Tangent);
            Assert.Equal(0.0, Dual.Constant(3.0).Tangent);
            Assert.Equal(1.0, Dual.Variable(3.0).Tangent);
        }

        [Fact]
        public void ArithmeticFollowsTheDifferentiationRules()
        {
            var a = new Dual(2.5, 0.7);
            var b = new Dual(-1.25, 0.3);

            Dual sum = a + b, diff = a - b, prod = a * b, quot = a / b, neg = -a;
            Close(1.25, sum.Value, Tight); Close(1.0, sum.Tangent, Tight);
            Close(3.75, diff.Value, Tight); Close(0.4, diff.Tangent, Tight);
            Close(-3.125, prod.Value, Tight); Close(0.7 * -1.25 + 2.5 * 0.3, prod.Tangent, Tight);
            Close(-2.0, quot.Value, Tight); Close((0.7 * -1.25 - 2.5 * 0.3) / (1.25 * 1.25), quot.Tangent, Tight);
            Assert.Equal(-2.5, neg.Value); Assert.Equal(-0.7, neg.Tangent);

            // Mixed with constants.
            Assert.Equal(0.7, (a + 4.0).Tangent); Assert.Equal(0.7, (4.0 + a).Tangent);
            Assert.Equal(0.7, (a - 4.0).Tangent); Assert.Equal(-0.7, (4.0 - a).Tangent);
            Close(2.8, (a * 4.0).Tangent, Tight); Close(2.8, (4.0 * a).Tangent, Tight);
            Close(0.175, (a / 4.0).Tangent, Tight);
            Close(-4.0 * 0.7 / (2.5 * 2.5), (4.0 / a).Tangent, Tight);
            Close(1.6, (4.0 / a).Value, Tight);
        }

        [Fact]
        public void ElementaryFunctionsMatchTheirAnalyticDerivatives()
        {
            double x = 0.7, t = 1.3;
            var d = new Dual(x, t);

            Close(Math.Exp(x), Dual.Exp(d).Value, Tight); Close(t * Math.Exp(x), Dual.Exp(d).Tangent, Tight);
            Close(Math.Log(x), Dual.Log(d).Value, Tight); Close(t / x, Dual.Log(d).Tangent, Tight);
            Close(Math.Sqrt(x), Dual.Sqrt(d).Value, Tight); Close(t * 0.5 / Math.Sqrt(x), Dual.Sqrt(d).Tangent, Tight);
            Close(Math.Sin(x), Dual.Sin(d).Value, Tight); Close(t * Math.Cos(x), Dual.Sin(d).Tangent, Tight);
            Close(Math.Cos(x), Dual.Cos(d).Value, Tight); Close(-t * Math.Sin(x), Dual.Cos(d).Tangent, Tight);

            Close(Math.Pow(x, 2.5), Dual.Pow(d, 2.5).Value, Tight);
            Close(t * 2.5 * Math.Pow(x, 1.5), Dual.Pow(d, 2.5).Tangent, Tight);
            Close(Math.Pow(3.0, x), Dual.Pow(3.0, d).Value, Tight);
            Close(t * Math.Log(3.0) * Math.Pow(3.0, x), Dual.Pow(3.0, d).Tangent, Tight);

            // x^y with both moving: d(x^y) = x^y (y' ln x + y x' / x).
            var y = new Dual(1.9, -0.4);
            Dual p = Dual.Pow(d, y);
            Close(Math.Pow(x, 1.9), p.Value, Tight);
            Close(Math.Pow(x, 1.9) * (-0.4 * Math.Log(x) + 1.9 * t / x), p.Tangent, Tight);

            Assert.Equal(t, Dual.Abs(d).Tangent);
            Assert.Equal(t, Dual.Abs(-d).Tangent);   // |-x| = x for x > 0
            Assert.Equal(-t, Dual.Abs(d - 1.0).Tangent); // |x - 1| = 1 - x for x < 1
            Assert.Equal(x, Dual.Abs(-d).Value);
        }

        static Dual Composite(Dual x)
        {
            return Dual.Exp(Dual.Sin(x)) * Dual.Sqrt(1.0 + x * x) / Dual.Log(2.0 + x) + Dual.Pow(x, 2.5) - 3.0 / x
                + Dual.Pow(x, Dual.Cos(x)) - Dual.Abs(x - 2.0) + Dual.Max(x, 0.5) * Dual.Min(x, 1.5);
        }

        static double Composite(double x)
        {
            return Math.Exp(Math.Sin(x)) * Math.Sqrt(1.0 + x * x) / Math.Log(2.0 + x) + Math.Pow(x, 2.5) - 3.0 / x
                + Math.Pow(x, Math.Cos(x)) - Math.Abs(x - 2.0) + Math.Max(x, 0.5) * Math.Min(x, 1.5);
        }

        [Theory]
        [InlineData(0.3)]
        [InlineData(0.9)]
        [InlineData(1.2)]
        [InlineData(2.7)]
        public void CompositeDerivativeMatchesFiniteDifferences(double x)
        {
            Dual d = Composite(Dual.Variable(x));
            Assert.Equal(Composite(x), d.Value, 14);

            // Richardson-extrapolated central differences: O(h^4) truncation, rounding ~ 1e-16 / h.
            double h = 1e-3;
            double c1 = (Composite(x + h) - Composite(x - h)) / (2 * h);
            double c2 = (Composite(x + 2 * h) - Composite(x - 2 * h)) / (4 * h);
            double fd = (4 * c1 - c2) / 3;
            Close(fd, d.Tangent, 1e-9);
        }

        [Fact]
        public void Dual4CarriesEachDirectionLikeASeparateDualPass()
        {
            double x = 1.1, y = 0.45;

            // Directions: 0 = d/dx, 1 = d/dy, 2 = along (1, 1), 3 = constant.
            var x4 = new Dual4(x, 1.0, 0.0, 1.0, 0.0);
            var y4 = new Dual4(y, 0.0, 1.0, 1.0, 0.0);
            Dual4 f4 = F(x4, y4);

            Dual fx = F(Dual.Variable(x), Dual.Constant(y));
            Dual fy = F(Dual.Constant(x), Dual.Variable(y));
            Dual fxy = F(Dual.Variable(x), Dual.Variable(y));

            Assert.Equal(fx.Value, f4.Value);
            Close(fx.Tangent, f4.D0, 1e-15);
            Close(fy.Tangent, f4.D1, 1e-15);
            Close(fxy.Tangent, f4.D2, 1e-15);
            Assert.Equal(0.0, f4.D3);
            Close(f4.D0 + f4.D1, f4.D2, 1e-14);

            Assert.Equal(f4.D1, f4.Tangent(1));
            Assert.Equal(f4.D2, f4.Direction(2).Tangent);
            Assert.True(double.IsNaN(f4.Tangent(4)));

            Dual4 v = Dual4.Variable(2.0, 3);
            Assert.Equal(new[] { 0.0, 0.0, 0.0, 1.0 }, new[] { v.D0, v.D1, v.D2, v.D3 });
        }

        static Dual F(Dual x, Dual y)
        {
            return Dual.Exp(x * y) / (1.0 + y) - Dual.Log(x) * Dual.Sqrt(y) + Dual.Pow(x, y) + Dual.Sin(x - y) * Dual.Cos(2.0 * y)
                + Dual.Pow(x, 3.0) - Dual.Pow(2.0, y) + 4.0 / x - (y - 1.0) / 3.0;
        }

        static Dual4 F(Dual4 x, Dual4 y)
        {
            return Dual4.Exp(x * y) / (1.0 + y) - Dual4.Log(x) * Dual4.Sqrt(y) + Dual4.Pow(x, y) + Dual4.Sin(x - y) * Dual4.Cos(2.0 * y)
                + Dual4.Pow(x, 3.0) - Dual4.Pow(2.0, y) + 4.0 / x - (y - 1.0) / 3.0;
        }

        [Fact]
        public void ComparisonsLookAtTheValueOnly()
        {
            var a = new Dual(1.0, 5.0);
            var b = new Dual(2.0, -5.0);
            Assert.True(a < b); Assert.True(b > a); Assert.True(a <= b); Assert.False(a >= b);
            Assert.True(a == new Dual(1.0, 123.0)); Assert.False(a != new Dual(1.0, 123.0));
            Assert.True(a == 1.0); Assert.True(2.0 > a); Assert.True(a.Equals(new Dual(1.0, -1.0)));

            var c = new Dual4(1.0, 5.0, 0, 0, 0);
            Assert.True(c < 2.0); Assert.True(c == new Dual4(1.0, 0, 0, 0, 9.0)); Assert.True(0.5 <= c);
        }

        [Fact]
        public void KinksTakeTheOneSidedDerivativeChosenByArgumentOrder()
        {
            // max(S - K, 0) at S = K: Max keeps its first argument on a tie -> derivative of S - K, i.e. 1 (right derivative).
            Dual s = Dual.Variable(100.0);
            Dual call = Dual.Max(s - 100.0, 0.0);
            Assert.Equal(0.0, call.Value);
            Assert.Equal(1.0, call.Tangent);
            // Same kink written max(0, S - K): the constant wins the tie -> left derivative 0.
            Assert.Equal(0.0, Dual.Max(0.0, s - 100.0).Tangent);
            // Put max(K - S, 0) at S = K: first argument again -> d(K - S)/dS = -1 (the exercised branch).
            Assert.Equal(-1.0, Dual.Max(100.0 - s, 0.0).Tangent);
            // Away from the kink the larger argument's derivative is taken.
            Assert.Equal(1.0, Dual.Max(Dual.Variable(101.0) - 100.0, 0.0).Tangent);
            Assert.Equal(0.0, Dual.Max(Dual.Variable(99.0) - 100.0, 0.0).Tangent);

            Assert.Equal(-1.0, Dual.Min(100.0 - s, 0.0).Tangent);
            Assert.Equal(0.0, Dual.Min(0.0, 100.0 - s).Tangent);

            // |x| at 0: derivative of +x.
            Assert.Equal(1.0, Dual.Abs(Dual.Variable(0.0)).Tangent);
            Assert.Equal(-1.0, Dual.Abs(-Dual.Variable(0.0)).Tangent); // -x has tangent -1 and value -0 >= 0

            // Same conventions on every direction of a Dual4.
            Dual4 s4 = new Dual4(100.0, 1.0, 2.0, 0.0, -1.0);
            Dual4 c4 = Dual4.Max(s4 - 100.0, 0.0);
            Assert.Equal(new[] { 1.0, 2.0, 0.0, -1.0 }, new[] { c4.D0, c4.D1, c4.D2, c4.D3 });
            Assert.Equal(0.0, Dual4.Max(0.0, s4 - 100.0).D1);
        }

        [Fact]
        public void ConstantsKeepAZeroTangentWhereTheDerivativeIsInfinite()
        {
            Assert.Equal(0.0, Dual.Sqrt(Dual.Constant(0.0)).Tangent);
            Assert.Equal(0.0, Dual.Pow(Dual.Constant(0.0), 0.5).Tangent);
            Assert.True(double.IsPositiveInfinity(Dual.Sqrt(Dual.Variable(0.0)).Tangent));

            Dual4 z = Dual4.Sqrt(new Dual4(0.0, 1.0, 0.0, 0.0, 0.0));
            Assert.True(double.IsPositiveInfinity(z.D0));
            Assert.Equal(0.0, z.D1);
        }
    }
}
