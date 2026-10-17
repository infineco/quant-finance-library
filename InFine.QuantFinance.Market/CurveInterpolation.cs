using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>The curve quantity an <see cref="InterpolatedDiscountCurve"/> interpolates in time.</summary>
    public enum InterpolatedQuantity
    {
        /// <summary>
        /// The continuously compounded zero rate <c>z(t) = -ln P(0, t) / t</c>, with nodes at the pillars only
        /// (<c>z</c> is undefined at <c>t = 0</c>); before the first pillar <c>z</c> is held at its first value.
        /// </summary>
        ZeroRate = 0,

        /// <summary>
        /// The log discount factor <c>ln P(0, t)</c>, with an extra node <c>(0, 0)</c>. Linear interpolation of this quantity
        /// is the classic "log-linear on discount factors", whose instantaneous forwards are piecewise constant.
        /// </summary>
        LogDiscountFactor,
    }

    /// <summary>
    /// How an <see cref="InterpolatedDiscountCurve"/> fills the gaps between pillars: an <see cref="IInterpolator"/>
    /// applied to an <see cref="InterpolatedQuantity"/>. Any pair is allowed; the three usual choices are provided.
    /// </summary>
    public sealed class CurveInterpolation
    {
        /// <summary>Linear on continuously compounded zero rates.</summary>
        public static readonly CurveInterpolation LinearZero = new(InterpolatedQuantity.ZeroRate, LinearInterpolator.Instance);

        /// <summary>Linear on log discount factors: piecewise-constant instantaneous forwards (raw interpolation).</summary>
        public static readonly CurveInterpolation LogLinearDiscount = new(InterpolatedQuantity.LogDiscountFactor, LinearInterpolator.Instance);

        /// <summary>
        /// Monotonicity-preserving cubic Hermite on zero rates (Bessel slopes, Hyman 1983 filter, see
        /// <see cref="MonotoneCubicInterpolator"/>): continuous instantaneous forwards and no overshoot of the zero curve
        /// between pillars. Not local, so bootstrapping it needs global iteration. This is not the Hagan-West
        /// "monotone convex" scheme, which interpolates discrete forwards instead.
        /// </summary>
        public static readonly CurveInterpolation MonotoneCubicZero = new(InterpolatedQuantity.ZeroRate, MonotoneCubicInterpolator.Instance);

        /// <summary>Pairs an interpolator with the quantity it interpolates.</summary>
        public CurveInterpolation(InterpolatedQuantity quantity, IInterpolator interpolator)
        {
            if (!Enum.IsDefined(quantity))
                throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Unknown interpolated quantity.");
            ArgumentNullException.ThrowIfNull(interpolator);
            Quantity = quantity;
            Interpolator = interpolator;
        }

        /// <summary>The interpolated quantity.</summary>
        public InterpolatedQuantity Quantity { get; }

        /// <summary>The interpolation scheme.</summary>
        public IInterpolator Interpolator { get; }

        /// <summary>True when the curve between two pillars depends only on those two pillars (see <see cref="IInterpolator.IsLocal"/>).</summary>
        public bool IsLocal => Interpolator.IsLocal;

        /// <inheritdoc />
        public override string ToString() => $"{Interpolator.Name} on {Quantity}";
    }
}
