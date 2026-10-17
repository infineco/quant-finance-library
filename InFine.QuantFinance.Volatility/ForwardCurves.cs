using System;
using InFine.QuantFinance.Market;

namespace InFine.QuantFinance.Volatility
{
    /// <summary>
    /// Forward of the underlying as a function of time to expiry: the reference against which a surface's
    /// log-forward moneyness <c>k = ln(K / F(t))</c> is measured.
    /// </summary>
    public interface IForwardCurve
    {
        /// <summary>Forward for expiry <paramref name="t"/> (years, non-negative).</summary>
        double Forward(double t);
    }

    /// <summary>The same forward for every expiry (an index future, or a zero-carry underlying).</summary>
    public sealed class ConstantForwardCurve : IForwardCurve
    {
        /// <summary>Creates the curve.</summary>
        /// <param name="forward">forward, finite and positive.</param>
        public ConstantForwardCurve(double forward)
        {
            if (!(forward > 0.0) || double.IsInfinity(forward))
                throw new ArgumentOutOfRangeException(nameof(forward), forward, "The forward must be finite and positive.");
            Value = forward;
        }

        /// <summary>The forward.</summary>
        public double Value { get; }

        /// <inheritdoc />
        public double Forward(double t) => Value;
    }

    /// <summary>
    /// Cost-of-carry forward <c>F(t) = S Q(t) / P(t)</c>, with <c>P</c> the discount factors of the funding curve and
    /// <c>Q</c> those of an optional dividend-yield (or repo/borrow) curve, <c>Q(t) = exp(-int_0^t q)</c>. Both curves
    /// are read at the same curve time <c>t</c>, so they must share their reference date and time measure (day counter).
    /// Dividends are proportional, which is what makes calendar-spread arbitrage a condition on total variance at
    /// fixed log-forward moneyness (Gatheral-Jacquier Lemma 2.1).
    /// </summary>
    public sealed class SpotForwardCurve : IForwardCurve
    {
        /// <summary>Creates the curve.</summary>
        public SpotForwardCurve(double spot, IYieldCurve discountCurve, IYieldCurve? dividendCurve = null)
        {
            ArgumentNullException.ThrowIfNull(discountCurve);
            if (!(spot > 0.0) || double.IsInfinity(spot))
                throw new ArgumentOutOfRangeException(nameof(spot), spot, "The spot must be finite and positive.");
            if (dividendCurve != null && dividendCurve.ReferenceDate != discountCurve.ReferenceDate)
                throw new ArgumentException("The dividend and discount curves must share their reference date.", nameof(dividendCurve));
            Spot = spot; DiscountCurve = discountCurve; DividendCurve = dividendCurve;
        }

        /// <summary>Spot of the underlying.</summary>
        public double Spot { get; }

        /// <summary>Funding curve.</summary>
        public IYieldCurve DiscountCurve { get; }

        /// <summary>Dividend-yield curve, or null for none.</summary>
        public IYieldCurve? DividendCurve { get; }

        /// <inheritdoc />
        public double Forward(double t)
        {
            double q = DividendCurve?.DiscountFactor(t) ?? 1.0;
            return Spot * q / DiscountCurve.DiscountFactor(t);
        }
    }
}
