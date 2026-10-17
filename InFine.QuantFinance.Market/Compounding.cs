using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// How an interest rate <c>r</c> grows one unit of currency over a year fraction <c>t</c>.
    /// </summary>
    public enum Compounding
    {
        /// <summary>Growth <c>exp(r t)</c>.</summary>
        Continuous = 0,

        /// <summary>Growth <c>(1 + r)^t</c>: compounded once a year.</summary>
        Annual,

        /// <summary>Growth <c>1 + r t</c>: no compounding (money-market convention).</summary>
        Simple,
    }

    /// <summary>Conversions between rates and discount factors under a <see cref="Compounding"/>.</summary>
    public static class CompoundingExtensions
    {
        /// <summary>The discount factor <c>1 / growth</c> of <paramref name="rate"/> over the year fraction <paramref name="t"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Unknown compounding.</exception>
        public static double DiscountFactor(this Compounding compounding, double rate, double t) => compounding switch
        {
            Compounding.Continuous => Math.Exp(-rate * t),
            Compounding.Annual => Math.Pow(1.0 + rate, -t),
            Compounding.Simple => 1.0 / (1.0 + rate * t),
            _ => throw Unknown(compounding),
        };

        /// <summary>
        /// The rate whose discount factor over <paramref name="t"/> is <paramref name="discountFactor"/>
        /// (the inverse of <see cref="DiscountFactor"/>).
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="t"/> is not positive, <paramref name="discountFactor"/> is not positive, or unknown compounding.</exception>
        public static double ImpliedRate(this Compounding compounding, double discountFactor, double t)
        {
            CheckPositive(discountFactor, nameof(discountFactor));
            CheckPositive(t, nameof(t));
            return compounding switch
            {
                Compounding.Continuous => -Math.Log(discountFactor) / t,
                Compounding.Annual => Math.Pow(discountFactor, -1.0 / t) - 1.0,
                Compounding.Simple => (1.0 / discountFactor - 1.0) / t,
                _ => throw Unknown(compounding),
            };
        }

        /// <summary>
        /// The rate equivalent to the instantaneous (continuously compounded) rate <paramref name="instantaneousRate"/>
        /// over a vanishing period: the limit of <see cref="ImpliedRate"/> as <c>t</c> tends to zero. It is the
        /// instantaneous rate itself for continuous and simple compounding, and <c>exp(f) - 1</c> for annual compounding.
        /// </summary>
        public static double FromInstantaneous(this Compounding compounding, double instantaneousRate) => compounding switch
        {
            Compounding.Continuous or Compounding.Simple => instantaneousRate,
            Compounding.Annual => Math.Exp(instantaneousRate) - 1.0,
            _ => throw Unknown(compounding),
        };

        // The rate whose growth over t is `growth` (t > 0, growth > 0, checked by callers).
        internal static double RateFromGrowth(this Compounding compounding, double growth, double t) => compounding switch
        {
            Compounding.Continuous => Math.Log(growth) / t,
            Compounding.Annual => Math.Pow(growth, 1.0 / t) - 1.0,
            Compounding.Simple => (growth - 1.0) / t,
            _ => throw Unknown(compounding),
        };

        private static void CheckPositive(double value, string name)
        {
            if (!(value > 0.0) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, value, "Must be finite and positive.");
        }

        private static ArgumentOutOfRangeException Unknown(Compounding compounding) =>
            new(nameof(compounding), compounding, "Unknown compounding.");
    }
}
