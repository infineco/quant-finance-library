using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Arithmetic.AutoDiff;
using System;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Standard normal functions on dual numbers (<see cref="Dual"/>, <see cref="Dual4"/>). The value is
    /// <see cref="NormalDistribution"/>'s (Cody erfc, full accuracy in the tails); the tangent is the analytic derivative
    /// times the input tangent. Kernel-compatible like <see cref="NormalDistribution"/>.
    /// </summary>
    /// <remarks>
    /// These live in ClosedForm rather than next to the dual types because the accurate primal is here; Arithmetic depends
    /// on nothing and ClosedForm references Arithmetic, not the other way round.
    /// </remarks>
    public static class DualNormalDistribution
    {
        const double TwoOverSqrtPi = 1.1283791670955125738961589031215; // 2 / sqrt(pi)

        /// <summary>Phi(x); derivative phi(x).</summary>
        [Kernel]
        public static Dual Cdf(Dual x)
        {
            return x.Chain(NormalDistribution.Cdf(x.Value), NormalDistribution.Pdf(x.Value));
        }

        /// <summary>phi(x); derivative -x phi(x).</summary>
        [Kernel]
        public static Dual Pdf(Dual x)
        {
            double p = NormalDistribution.Pdf(x.Value);
            return x.Chain(p, -x.Value * p);
        }

        /// <summary>erfc(x); derivative -2 exp(-x^2) / sqrt(pi).</summary>
        [Kernel]
        public static Dual Erfc(Dual x)
        {
            return x.Chain(NormalDistribution.Erfc(x.Value), -TwoOverSqrtPi * Math.Exp(-x.Value * x.Value));
        }

        /// <summary>Phi(x); derivative phi(x) on each direction.</summary>
        [Kernel]
        public static Dual4 Cdf(Dual4 x)
        {
            return x.Chain(NormalDistribution.Cdf(x.Value), NormalDistribution.Pdf(x.Value));
        }

        /// <summary>phi(x); derivative -x phi(x) on each direction.</summary>
        [Kernel]
        public static Dual4 Pdf(Dual4 x)
        {
            double p = NormalDistribution.Pdf(x.Value);
            return x.Chain(p, -x.Value * p);
        }

        /// <summary>erfc(x); derivative -2 exp(-x^2) / sqrt(pi) on each direction.</summary>
        [Kernel]
        public static Dual4 Erfc(Dual4 x)
        {
            return x.Chain(NormalDistribution.Erfc(x.Value), -TwoOverSqrtPi * Math.Exp(-x.Value * x.Value));
        }
    }
}
