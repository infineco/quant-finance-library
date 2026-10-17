using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Standard bivariate normal distribution function M(a, b, rho) = P(X &lt;= a, Y &lt;= b) for standard normals X, Y
    /// with correlation rho. Kernel-compatible: no allocation, no exceptions, only <c>Math.*</c> and
    /// <see cref="NormalDistribution"/> calls.
    /// </summary>
    /// <remarks>
    /// Algorithm of Drezner and Wesolowsky (1990) as refined by A. Genz, "Numerical computation of rectangular bivariate
    /// and trivariate normal and t probabilities", Statistics and Computing 14 (2004), routine BVND: for |rho| &lt; 0.925 a
    /// Gauss-Legendre quadrature of Plackett's integral over asin(rho); above, a series in 1 - rho^2 plus a quadrature of
    /// the remainder, which stays accurate up to |rho| = 1. Genz uses 6, 12 or 20 points according to |rho|; the 20-point
    /// rule is used throughout here (one code path, at least the same accuracy, about 1e-15 absolute).
    /// rho = +-1 give the degenerate limits Phi(min(a, b)) and max(Phi(a) - Phi(-b), 0).
    /// </remarks>
    public static class BivariateNormalDistribution
    {
        const double TwoPi = 6.283185307179586476925286766559;

        /// <summary>P(X &lt;= a, Y &lt;= b) with corr(X, Y) = <paramref name="rho"/> in [-1, 1].</summary>
        [Kernel]
        public static double Cdf(double a, double b, double rho)
        {
            if (rho >= 1.0)
                return NormalDistribution.Cdf(Math.Min(a, b));
            if (rho <= -1.0)
                return Math.Max(NormalDistribution.Cdf(a) - NormalDistribution.Cdf(-b), 0.0);
            return UpperOrthant(-a, -b, rho);
        }

        /// <summary>Genz BVND: P(X &gt; h, Y &gt; k) for |r| &lt; 1.</summary>
        [Kernel]
        static double UpperOrthant(double h, double k, double r)
        {
            double hk = h * k;
            double bvn = 0.0;
            if (Math.Abs(r) < 0.925)
            {
                double hs = 0.5 * (h * h + k * k);
                double asr = Math.Asin(r);
                for (int i = 0; i < 10; ++i)
                {
                    double x = Node(i), w = Weight(i);
                    double sn = Math.Sin(0.5 * asr * (x + 1.0));
                    bvn += w * Math.Exp((sn * hk - hs) / (1.0 - sn * sn));
                    sn = Math.Sin(0.5 * asr * (1.0 - x));
                    bvn += w * Math.Exp((sn * hk - hs) / (1.0 - sn * sn));
                }
                return bvn * asr / (2.0 * TwoPi) + NormalDistribution.Cdf(-h) * NormalDistribution.Cdf(-k);
            }

            if (r < 0.0)
            {
                k = -k;
                hk = -hk;
            }
            double ass = (1.0 - r) * (1.0 + r);
            double a = Math.Sqrt(ass);
            double bs = (h - k) * (h - k);
            double c = (4.0 - hk) / 8.0;
            double d = (12.0 - hk) / 16.0;
            bvn = a * Math.Exp(-0.5 * (bs / ass + hk)) * (1.0 - c * (bs - ass) * (1.0 - d * bs / 5.0) / 3.0 + c * d * ass * ass / 5.0);
            if (hk > -160.0)
            {
                double b = Math.Sqrt(bs);
                bvn -= Math.Exp(-0.5 * hk) * Math.Sqrt(TwoPi) * NormalDistribution.Cdf(-b / a) * b * (1.0 - c * bs * (1.0 - d * bs / 5.0) / 3.0);
            }
            a *= 0.5;
            for (int i = 0; i < 10; ++i)
            {
                double x = Node(i), w = Weight(i);
                for (int side = -1; side <= 1; side += 2)
                {
                    double xs = a * (side * x + 1.0);
                    xs *= xs;
                    double rs = Math.Sqrt(1.0 - xs);
                    bvn += a * w * (Math.Exp(-bs / (2.0 * xs) - hk / (1.0 + rs)) / rs
                        - Math.Exp(-0.5 * (bs / xs + hk)) * (1.0 + c * xs * (1.0 + d * xs)));
                }
            }
            bvn = -bvn / TwoPi;
            if (r > 0.0)
                return bvn + NormalDistribution.Cdf(-Math.Max(h, k));
            bvn = -bvn;
            if (k > h)
            {
                if (h < 0.0)
                    bvn += NormalDistribution.Cdf(k) - NormalDistribution.Cdf(h);
                else
                    bvn += NormalDistribution.Cdf(-h) - NormalDistribution.Cdf(-k);
            }
            return bvn;
        }

        /// <summary>Negative nodes of the 20-point Gauss-Legendre rule on [-1, 1] (the others are their opposites).</summary>
        [Kernel]
        static double Node(int i)
        {
            switch (i)
            {
                case 0: return -0.9931285991850949;
                case 1: return -0.9639719272779138;
                case 2: return -0.9122344282513259;
                case 3: return -0.8391169718222188;
                case 4: return -0.7463319064601508;
                case 5: return -0.6360536807265150;
                case 6: return -0.5108670019508271;
                case 7: return -0.3737060887154196;
                case 8: return -0.2277858511416451;
                default: return -0.07652652113349733;
            }
        }

        /// <summary>Weights of <see cref="Node"/>.</summary>
        [Kernel]
        static double Weight(int i)
        {
            switch (i)
            {
                case 0: return 0.01761400713915212;
                case 1: return 0.04060142980038694;
                case 2: return 0.06267204833410906;
                case 3: return 0.08327674157670475;
                case 4: return 0.1019301198172404;
                case 5: return 0.1181945319615184;
                case 6: return 0.1316886384491766;
                case 7: return 0.1420961093183821;
                case 8: return 0.1491729864726037;
                default: return 0.1527533871307259;
            }
        }
    }
}
