using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// A curve with a constant instantaneous forward rate <c>r</c>: <c>P(0, t) = exp(-r t)</c>, so every continuously
    /// compounded zero and forward rate equals <c>r</c>.
    /// </summary>
    public sealed class FlatForward : YieldCurve
    {
        /// <summary>
        /// Creates the curve from a rate quoted with continuous or annual compounding (an annual rate <c>R</c> is the
        /// continuous rate <c>ln(1 + R)</c>).
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Simple compounding: <c>1 / (1 + R t)</c> has no constant forward rate, so it cannot describe a flat-forward curve.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">The rate is not finite, or an annual rate is not above -100%.</exception>
        public FlatForward(DateOnly referenceDate, double rate, IDayCounter dayCounter, Compounding compounding = Compounding.Continuous)
            : base(referenceDate, dayCounter)
        {
            if (!double.IsFinite(rate))
                throw new ArgumentOutOfRangeException(nameof(rate), rate, "The rate must be finite.");
            Rate = compounding switch
            {
                Compounding.Continuous => rate,
                Compounding.Annual when rate > -1.0 => Math.Log(1.0 + rate),
                Compounding.Annual => throw new ArgumentOutOfRangeException(nameof(rate), rate, "An annual rate must be above -100%."),
                Compounding.Simple => throw new ArgumentException(
                    "A simple rate does not define a flat-forward curve; quote a continuous or annual rate.", nameof(compounding)),
                _ => throw new ArgumentOutOfRangeException(nameof(compounding), compounding, "Unknown compounding."),
            };
        }

        /// <summary>The continuously compounded rate, equal to every instantaneous forward of the curve.</summary>
        public double Rate { get; }

        /// <inheritdoc />
        protected override double DiscountFactorCore(double t) => Math.Exp(-Rate * t);

        /// <inheritdoc />
        protected override double InstantaneousForwardCore(double t) => Rate;
    }
}
