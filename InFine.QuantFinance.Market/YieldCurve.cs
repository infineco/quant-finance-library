using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// Base class of the curves: derives zero, forward and date-based quantities from a discount function and an
    /// instantaneous forward supplied by the subclass, and validates the times once.
    /// </summary>
    public abstract class YieldCurve : IYieldCurve
    {
        /// <summary>Initialises the reference date and the time-measuring day counter.</summary>
        protected YieldCurve(DateOnly referenceDate, IDayCounter dayCounter)
        {
            ArgumentNullException.ThrowIfNull(dayCounter);
            ReferenceDate = referenceDate;
            DayCounter = dayCounter;
        }

        /// <inheritdoc />
        public DateOnly ReferenceDate { get; }

        /// <inheritdoc />
        public IDayCounter DayCounter { get; }

        /// <inheritdoc />
        public double TimeFromReference(DateOnly date) => DayCounter.YearFraction(ReferenceDate, date);

        /// <inheritdoc />
        public double DiscountFactor(DateOnly date) => DiscountFactor(TimeFromReference(date));

        /// <inheritdoc />
        public double DiscountFactor(double t)
        {
            CheckTime(t, nameof(t));
            return DiscountFactorCore(t);
        }

        /// <inheritdoc />
        public double ZeroRate(double t, Compounding compounding = Compounding.Continuous)
        {
            CheckTime(t, nameof(t));
            return t == 0.0
                ? compounding.FromInstantaneous(InstantaneousForwardCore(0.0))
                : compounding.ImpliedRate(DiscountFactorCore(t), t);
        }

        /// <inheritdoc />
        public double ForwardRate(double t1, double t2, Compounding compounding = Compounding.Continuous)
        {
            CheckTime(t1, nameof(t1));
            CheckTime(t2, nameof(t2));
            if (t2 < t1)
                throw new ArgumentOutOfRangeException(nameof(t2), t2, $"The end time must not be before the start time {t1}.");
            return t2 == t1
                ? compounding.FromInstantaneous(InstantaneousForwardCore(t1))
                : compounding.RateFromGrowth(DiscountFactorCore(t1) / DiscountFactorCore(t2), t2 - t1);
        }

        /// <inheritdoc />
        public double InstantaneousForward(double t)
        {
            CheckTime(t, nameof(t));
            return InstantaneousForwardCore(t);
        }

        /// <summary>The discount factor at a validated time <c>t &gt;= 0</c>.</summary>
        protected abstract double DiscountFactorCore(double t);

        /// <summary>The instantaneous forward at a validated time <c>t &gt;= 0</c>.</summary>
        protected abstract double InstantaneousForwardCore(double t);

        private static void CheckTime(double t, string name)
        {
            if (!(t >= 0.0) || double.IsInfinity(t))
                throw new ArgumentOutOfRangeException(name, t, "Curve times must be finite and non-negative (dates on or after the reference date).");
        }
    }
}
