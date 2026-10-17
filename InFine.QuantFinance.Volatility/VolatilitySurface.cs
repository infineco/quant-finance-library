using System;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Market;

namespace InFine.QuantFinance.Volatility
{
    /// <summary>
    /// An implied-volatility surface in total variance: <c>w(t, k) = sigma_imp(t, K)^2 t</c> on log-forward moneyness
    /// <c>k = ln(K / F(t))</c>, with Black-76 prices. Times are year fractions from the valuation date.
    /// </summary>
    /// <remarks>
    /// Derived surfaces define <see cref="Slice"/>, the smile at any expiry <c>t &gt;= 0</c> (zero total variance at
    /// <c>t = 0</c>); interpolation and extrapolation in expiry are documented on each of them. Strikes enter only
    /// through <c>k</c>, so a surface moves with the forward (sticky moneyness).
    /// </remarks>
    public abstract class VolatilitySurface
    {
        /// <summary>Base constructor.</summary>
        protected VolatilitySurface(IForwardCurve forwards)
        {
            ArgumentNullException.ThrowIfNull(forwards);
            Forwards = forwards;
        }

        /// <summary>Forwards that define the moneyness.</summary>
        public IForwardCurve Forwards { get; }

        /// <summary>The smile at expiry <paramref name="t"/> (non-negative) as a function of log-forward moneyness.</summary>
        public abstract ITotalVarianceSlice Slice(double t);

        /// <summary>Total implied variance <c>w(t, k)</c>.</summary>
        public double TotalVariance(double t, double k)
        {
            RequireTime(t, allowZero: true);
            return Slice(t).TotalVariance(k);
        }

        /// <summary>Black implied volatility for expiry <paramref name="t"/> (positive) and <paramref name="strike"/> (positive).</summary>
        public double ImpliedVolatility(double t, double strike)
        {
            RequireTime(t, allowZero: false);
            RequireStrike(strike);
            double k = Math.Log(strike / Forwards.Forward(t));
            return Math.Sqrt(Math.Max(Slice(t).TotalVariance(k), 0.0) / t);
        }

        /// <summary>
        /// Black-76 price of a European option expiring at <paramref name="t"/> on the forward <c>F(t)</c>, with the
        /// surface's implied volatility and the given discount factor to the payment date.
        /// </summary>
        public double Price(OptionType type, double t, double strike, double discountFactor)
        {
            RequireTime(t, allowZero: true);
            RequireStrike(strike);
            double forward = Forwards.Forward(t);
            double vol = t > 0.0 ? ImpliedVolatility(t, strike) : 0.0;
            return Black76.Price(type, forward, strike, discountFactor, t, vol);
        }

        /// <summary>
        /// Black-76 price for a dated expiry: the expiry time and the discount factor are those of
        /// <paramref name="discountCurve"/>, so the surface times must be measured on that curve's clock.
        /// </summary>
        public double Price(OptionType type, DateOnly expiry, double strike, IYieldCurve discountCurve)
        {
            ArgumentNullException.ThrowIfNull(discountCurve);
            return Price(type, discountCurve.TimeFromReference(expiry), strike, discountCurve.DiscountFactor(expiry));
        }

        /// <summary>Throws unless <paramref name="t"/> is finite and positive (or zero when allowed).</summary>
        protected static void RequireTime(double t, bool allowZero)
        {
            if (!double.IsFinite(t) || t < 0.0 || (!allowZero && t == 0.0))
                throw new ArgumentOutOfRangeException(nameof(t), t,
                    allowZero ? "The expiry time must be finite and non-negative." : "The expiry time must be finite and positive.");
        }

        static void RequireStrike(double strike)
        {
            if (!(strike > 0.0) || double.IsInfinity(strike))
                throw new ArgumentOutOfRangeException(nameof(strike), strike, "The strike must be finite and positive.");
        }
    }
}
