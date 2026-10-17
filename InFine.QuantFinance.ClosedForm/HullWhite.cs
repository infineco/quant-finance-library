using System;
using System.Collections.Generic;
using InFine.QuantFinance.Numerics;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>Payer (right to pay the fixed rate) or receiver (right to receive it) swaption.</summary>
    public enum SwaptionType
    {
        /// <summary>Right to enter the swap paying fixed: a put on the fixed-leg bond.</summary>
        Payer = 1,

        /// <summary>Right to enter the swap receiving fixed: a call on the fixed-leg bond.</summary>
        Receiver = -1,
    }

    /// <summary>
    /// One-factor Hull-White model <c>dr = (theta(t) - a r) dt + sigma dW</c> fitted to an initial discount curve: host API over
    /// <see cref="HullWhiteKernels"/> (the formulas and conventions are documented there). Times are year fractions on the
    /// curve's clock; arguments are validated (exceptions), the kernels never throw.
    /// <para>
    /// The curve is given as two functions, <c>P(0, t)</c> and the instantaneous forward <c>f(0, t)</c>, so that any curve fits:
    /// for an <c>InFine.QuantFinance.Market.IYieldCurve</c> pass <c>curve.DiscountFactor</c> and <c>curve.InstantaneousForward</c>;
    /// for a compiled curve use the <see cref="HullWhite(double, double, CompiledDiscountCurve)"/> constructor. Prices only read
    /// <c>P(0, t)</c>; <c>f(0, t)</c> enters <see cref="Phi"/>, i.e. the level of the short rate.
    /// </para>
    /// </summary>
    public sealed class HullWhite
    {
        private readonly Func<double, double> _discountFactor;
        private readonly Func<double, double> _instantaneousForward;

        /// <summary>Creates the model on a curve given by its discount function and instantaneous forward.</summary>
        /// <param name="meanReversion">a, positive.</param>
        /// <param name="volatility">sigma, non-negative (0 is the deterministic limit).</param>
        /// <param name="discountFactor">t -&gt; P(0, t), positive with P(0, 0) = 1.</param>
        /// <param name="instantaneousForward">t -&gt; f(0, t) = -d ln P(0, t) / dt.</param>
        public HullWhite(double meanReversion, double volatility, Func<double, double> discountFactor, Func<double, double> instantaneousForward)
        {
            if (!(meanReversion > 0.0) || double.IsInfinity(meanReversion))
                throw new ArgumentOutOfRangeException(nameof(meanReversion), meanReversion, "The mean reversion must be finite and positive.");
            BlackScholes.RequireNonNegative(volatility, nameof(volatility));
            _discountFactor = discountFactor ?? throw new ArgumentNullException(nameof(discountFactor));
            _instantaneousForward = instantaneousForward ?? throw new ArgumentNullException(nameof(instantaneousForward));
            MeanReversion = meanReversion;
            Volatility = volatility;
        }

        /// <summary>Creates the model on a compiled curve (the same evaluation as on the device).</summary>
        public HullWhite(double meanReversion, double volatility, CompiledDiscountCurve curve)
            : this(meanReversion, volatility, Check(curve).DiscountFactor, curve.InstantaneousForward)
        {
        }

        /// <summary>a.</summary>
        public double MeanReversion { get; }

        /// <summary>sigma.</summary>
        public double Volatility { get; }

        /// <summary>The same curve with other parameters.</summary>
        public HullWhite WithParameters(double meanReversion, double volatility)
            => new HullWhite(meanReversion, volatility, _discountFactor, _instantaneousForward);

        /// <summary>P(0, t) of the initial curve.</summary>
        public double DiscountFactor(double t)
        {
            RequireTime(t, nameof(t));
            var p = _discountFactor(t);
            if (!(p > 0.0) || double.IsInfinity(p))
                throw new InvalidOperationException($"The curve returned the discount factor {p} at t = {t}.");
            return p;
        }

        /// <summary><c>phi(t) = f(0, t) + sigma^2 B(0, t)^2 / 2</c>, so that <c>r(t) = x(t) + phi(t)</c> and <c>E[x(t)] = 0</c>.</summary>
        public double Phi(double t)
        {
            RequireTime(t, nameof(t));
            return HullWhiteKernels.Phi(MeanReversion, Volatility, _instantaneousForward(t), t);
        }

        /// <summary><c>theta(t) = f'(0, t) + a f(0, t) + sigma^2 (1 - exp(-2 a t)) / (2 a)</c>, by a central difference of f.
        /// Informative only: neither the prices nor the simulation use it.</summary>
        public double Theta(double t)
        {
            RequireTime(t, nameof(t));
            double h = 1e-5 * Math.Max(1.0, t);
            double lo = Math.Max(0.0, t - h), hi = t + h;
            double slope = (_instantaneousForward(hi) - _instantaneousForward(lo)) / (hi - lo);
            return slope + MeanReversion * _instantaneousForward(t) + HullWhiteKernels.StateVariance(MeanReversion, Volatility, t);
        }

        /// <summary>Variance of the state <c>x(t)</c> (and of the short rate) seen from 0.</summary>
        public double StateVariance(double t)
        {
            RequireTime(t, nameof(t));
            return HullWhiteKernels.StateVariance(MeanReversion, Volatility, t);
        }

        /// <summary>The zero-coupon bond <c>P(t, T)</c> given the state <c>x(t)</c>; equal to the curve at <c>t = 0, x = 0</c>.</summary>
        public double ZeroCouponBond(double t, double maturity, double state)
        {
            RequireOrderedTimes(t, maturity);
            RequireFinite(state, nameof(state));
            return Math.Exp(HullWhiteKernels.LogZeroBond(MeanReversion, Volatility, Math.Log(DiscountFactor(t)), Math.Log(DiscountFactor(maturity)),
                t, maturity, state));
        }

        /// <summary>The zero-coupon bond <c>P(t, T)</c> given the short rate <c>r(t)</c>, i.e. with <c>x = r - phi(t)</c>.</summary>
        public double ZeroCouponBondFromShortRate(double t, double maturity, double shortRate)
        {
            RequireFinite(shortRate, nameof(shortRate));
            return ZeroCouponBond(t, maturity, shortRate - Phi(t));
        }

        /// <summary>
        /// European option expiring at <paramref name="expiry"/> on the zero-coupon bond maturing at <paramref name="maturity"/>.
        /// </summary>
        public double ZeroCouponBondOption(OptionType type, double expiry, double maturity, double strike)
        {
            RequireType(type);
            RequireOrderedTimes(expiry, maturity);
            BlackScholes.RequireNonNegative(strike, nameof(strike));
            return HullWhiteKernels.ZeroBondOption((int)type, MeanReversion, Volatility, DiscountFactor(expiry), DiscountFactor(maturity),
                expiry, maturity, strike);
        }

        /// <summary>
        /// Caplet on the simple rate over <c>[start, end]</c>, fixed at <paramref name="start"/> and paid at
        /// <paramref name="end"/>: <c>notional accrual (L - K)^+</c>.
        /// </summary>
        public double Caplet(double start, double end, double accrual, double strike, double notional = 1.0)
            => CapletFloorlet(1, start, end, accrual, strike, notional);

        /// <summary>Floorlet: <c>notional accrual (K - L)^+</c> paid at <paramref name="end"/>.</summary>
        public double Floorlet(double start, double end, double accrual, double strike, double notional = 1.0)
            => CapletFloorlet(-1, start, end, accrual, strike, notional);

        /// <summary>Cap: the sum of the caplets of the periods <c>[starts[i], ends[i]]</c> with their accruals.</summary>
        public double Cap(IReadOnlyList<double> starts, IReadOnlyList<double> ends, IReadOnlyList<double> accruals, double strike, double notional = 1.0)
            => CapFloor(1, starts, ends, accruals, strike, notional);

        /// <summary>Floor: the sum of the floorlets.</summary>
        public double Floor(IReadOnlyList<double> starts, IReadOnlyList<double> ends, IReadOnlyList<double> accruals, double strike, double notional = 1.0)
            => CapFloor(-1, starts, ends, accruals, strike, notional);

        /// <summary>
        /// European option expiring at <paramref name="expiry"/> on the cash flows <paramref name="cashFlows"/> paid at
        /// <paramref name="paymentTimes"/> (all after the expiry, all positive), by Jamshidian's decomposition.
        /// </summary>
        public double CouponBondOption(OptionType type, double expiry, IReadOnlyList<double> paymentTimes, IReadOnlyList<double> cashFlows, double strike)
        {
            RequireType(type);
            if (!(strike > 0.0) || double.IsInfinity(strike))
                throw new ArgumentOutOfRangeException(nameof(strike), strike, "The strike must be finite and positive.");
            var (times, discounts) = PaymentDiscounts(expiry, paymentTimes);
            var flows = PositiveArray(cashFlows, times.Length, nameof(cashFlows));
            return Checked(HullWhiteKernels.CouponBondOption((int)type, MeanReversion, Volatility, expiry, DiscountFactor(expiry), times.Length,
                times, discounts, flows, 1.0, 0.0, strike));
        }

        /// <summary>
        /// European swaption of notional <paramref name="notional"/> on the swap starting at <paramref name="expiry"/> whose fixed
        /// leg pays <c>fixedRate * accruals[i]</c> at <c>paymentTimes[i]</c>; the floating leg is worth <c>1 - P(T_0, T_n)</c>
        /// per unit notional at expiry (it is reset at the expiry and paid until the last fixed payment).
        /// </summary>
        public double Swaption(SwaptionType type, double expiry, IReadOnlyList<double> paymentTimes, IReadOnlyList<double> accruals,
            double fixedRate, double notional = 1.0)
        {
            if (type != SwaptionType.Payer && type != SwaptionType.Receiver)
                throw new ArgumentOutOfRangeException(nameof(type), type, "Swaption type must be Payer or Receiver.");
            BlackScholes.RequireNonNegative(fixedRate, nameof(fixedRate));
            BlackScholes.RequireNonNegative(notional, nameof(notional));
            var (times, discounts) = PaymentDiscounts(expiry, paymentTimes);
            var tau = PositiveArray(accruals, times.Length, nameof(accruals));
            return notional * Checked(HullWhiteKernels.Swaption((int)type, MeanReversion, Volatility, expiry, DiscountFactor(expiry), times.Length,
                times, discounts, tau, fixedRate));
        }

        /// <summary>
        /// Jamshidian's critical short rate <c>r* = x* + phi(T_0)</c> of a swaption: the short rate at expiry at which the
        /// underlying swap is worth zero.
        /// </summary>
        public double SwaptionCriticalShortRate(double expiry, IReadOnlyList<double> paymentTimes, IReadOnlyList<double> accruals, double fixedRate)
        {
            BlackScholes.RequireNonNegative(fixedRate, nameof(fixedRate));
            var (times, discounts) = PaymentDiscounts(expiry, paymentTimes);
            var tau = PositiveArray(accruals, times.Length, nameof(accruals));
            var x = Checked(HullWhiteKernels.CriticalState(MeanReversion, Volatility, expiry, Math.Log(DiscountFactor(expiry)), times.Length,
                times, discounts, tau, fixedRate, 1.0, 1.0));
            return x + Phi(expiry);
        }

        /// <summary>The forward swap rate <c>(P(0, T_0) - P(0, T_n)) / sum_i accruals[i] P(0, T_i)</c> on the initial curve.</summary>
        public double ForwardSwapRate(double start, IReadOnlyList<double> paymentTimes, IReadOnlyList<double> accruals)
        {
            var (times, discounts) = PaymentDiscounts(start, paymentTimes);
            var tau = PositiveArray(accruals, times.Length, nameof(accruals));
            double annuity = 0.0;
            for (int i = 0; i < times.Length; i++) annuity += tau[i] * discounts[i];
            return (DiscountFactor(start) - discounts[^1]) / annuity;
        }

        private double CapletFloorlet(int sign, double start, double end, double accrual, double strike, double notional)
        {
            RequireOrderedTimes(start, end);
            if (!(end > start))
                throw new ArgumentOutOfRangeException(nameof(end), end, "The period must have a positive length.");
            if (!(accrual > 0.0) || double.IsInfinity(accrual))
                throw new ArgumentOutOfRangeException(nameof(accrual), accrual, "The accrual must be finite and positive.");
            RequireFinite(strike, nameof(strike));
            if (!(1.0 + accrual * strike > 0.0))
                throw new ArgumentOutOfRangeException(nameof(strike), strike, "1 + accrual * strike must be positive.");
            BlackScholes.RequireNonNegative(notional, nameof(notional));
            return notional * HullWhiteKernels.Caplet(sign, MeanReversion, Volatility, DiscountFactor(start), DiscountFactor(end),
                start, end, accrual, strike);
        }

        private double CapFloor(int sign, IReadOnlyList<double> starts, IReadOnlyList<double> ends, IReadOnlyList<double> accruals,
            double strike, double notional)
        {
            ArgumentNullException.ThrowIfNull(starts);
            ArgumentNullException.ThrowIfNull(ends);
            ArgumentNullException.ThrowIfNull(accruals);
            if (starts.Count != ends.Count || starts.Count != accruals.Count)
                throw new ArgumentException($"{starts.Count} starts, {ends.Count} ends and {accruals.Count} accruals.", nameof(accruals));
            double sum = 0.0;
            for (int i = 0; i < starts.Count; i++)
                sum += CapletFloorlet(sign, starts[i], ends[i], accruals[i], strike, notional);
            return sum;
        }

        private (double[] Times, double[] Discounts) PaymentDiscounts(double expiry, IReadOnlyList<double> paymentTimes)
        {
            RequireTime(expiry, nameof(expiry));
            ArgumentNullException.ThrowIfNull(paymentTimes);
            if (paymentTimes.Count == 0)
                throw new ArgumentException("At least one payment is required.", nameof(paymentTimes));
            var times = new double[paymentTimes.Count];
            var discounts = new double[times.Length];
            for (int i = 0; i < times.Length; i++)
            {
                times[i] = paymentTimes[i];
                if (!double.IsFinite(times[i]) || !(times[i] >= (i == 0 ? expiry : times[i - 1])))
                    throw new ArgumentException($"Payment times must be finite, non-decreasing and not before the expiry (payment {i}: {times[i]}).",
                        nameof(paymentTimes));
                discounts[i] = DiscountFactor(times[i]);
            }
            return (times, discounts);
        }

        private static double[] PositiveArray(IReadOnlyList<double> values, int count, string name)
        {
            ArgumentNullException.ThrowIfNull(values, name);
            if (values.Count != count)
                throw new ArgumentException($"{values.Count} values for {count} payments.", name);
            var result = new double[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = values[i];
                if (!(result[i] > 0.0) || double.IsInfinity(result[i]))
                    throw new ArgumentException($"Values must be finite and positive (index {i}: {result[i]}).", name);
            }
            return result;
        }

        private static double Checked(double value)
        {
            if (double.IsNaN(value))
                throw new InvalidOperationException("Jamshidian's critical rate could not be bracketed.");
            return value;
        }

        private static CompiledDiscountCurve Check(CompiledDiscountCurve curve)
            => curve ?? throw new ArgumentNullException(nameof(curve));

        private static void RequireType(OptionType type)
        {
            if (type != OptionType.Call && type != OptionType.Put)
                throw new ArgumentOutOfRangeException(nameof(type), type, "Option type must be Call or Put.");
        }

        private static void RequireTime(double t, string name)
        {
            if (!(t >= 0.0) || double.IsInfinity(t))
                throw new ArgumentOutOfRangeException(name, t, "Times must be finite and non-negative.");
        }

        private static void RequireOrderedTimes(double t, double maturity)
        {
            RequireTime(t, nameof(t));
            RequireTime(maturity, nameof(maturity));
            if (maturity < t)
                throw new ArgumentOutOfRangeException(nameof(maturity), maturity, $"The maturity must not be before {t}.");
        }

        private static void RequireFinite(double value, string name)
        {
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(name, value, name + " must be finite.");
        }
    }
}
