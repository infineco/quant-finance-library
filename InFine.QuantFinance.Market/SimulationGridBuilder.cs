using System;
using System.Collections.Generic;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// Builds the <see cref="SimulationGrid"/> of a simulation from the event dates of the products (observation,
    /// exercise, payment dates...):
    /// <list type="number">
    /// <item>each event date becomes the time <c>dayCounter.YearFraction(valuationDate, date)</c>; dates with the same time
    /// share a pillar, and an event on the valuation date (or at time zero) is pillar 0;</item>
    /// <item>with a maximum step <c>h</c>, every gap <c>[a, b]</c> between consecutive event times (and from time 0) is
    /// split into <c>ceil((b - a) / h)</c> equal steps, so that no step exceeds <c>h</c> and event times stay exact.</item>
    /// </list>
    /// </summary>
    public sealed class SimulationGridBuilder
    {
        // A gap within this relative distance of a multiple of the maximum step is not split one more time
        // (12 steps of 1/12 must not become 13 because of rounding).
        private const double StepCountTolerance = 1e-12;

        private readonly List<DateOnly> _eventDates = new();

        /// <summary>Starts a grid whose time zero is <paramref name="valuationDate"/>, measuring time with <paramref name="dayCounter"/>.</summary>
        public SimulationGridBuilder(DateOnly valuationDate, IDayCounter dayCounter)
        {
            ArgumentNullException.ThrowIfNull(dayCounter);
            ValuationDate = valuationDate;
            DayCounter = dayCounter;
        }

        /// <summary>The date of time zero.</summary>
        public DateOnly ValuationDate { get; }

        /// <summary>The convention turning dates into simulation times.</summary>
        public IDayCounter DayCounter { get; }

        /// <summary>The maximum step length in years, or <c>null</c> for steps from event to event only.</summary>
        public double? MaxTimeStep { get; private set; }

        /// <summary>Limits every step to <paramref name="years"/> by adding refinement pillars between event dates.</summary>
        public SimulationGridBuilder WithMaxTimeStep(double years)
        {
            if (!(years > 0.0) || double.IsInfinity(years))
                throw new ArgumentOutOfRangeException(nameof(years), years, "The maximum time step must be finite and positive.");
            MaxTimeStep = years;
            return this;
        }

        /// <summary>Adds an event date; duplicates are merged.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="date"/> is before the valuation date.</exception>
        public SimulationGridBuilder AddEventDate(DateOnly date)
        {
            if (date < ValuationDate)
                throw new ArgumentOutOfRangeException(nameof(date), date, $"Event dates must not be before the valuation date {ValuationDate:yyyy-MM-dd}.");
            _eventDates.Add(date);
            return this;
        }

        /// <summary>Adds event dates; duplicates are merged.</summary>
        public SimulationGridBuilder AddEventDates(IEnumerable<DateOnly> dates)
        {
            ArgumentNullException.ThrowIfNull(dates);
            foreach (var date in dates) AddEventDate(date);
            return this;
        }

        /// <summary>Builds the grid.</summary>
        /// <exception cref="InvalidOperationException">No event date after time zero, or the day counter gives a non-finite or negative time.</exception>
        public SimulationGrid Build()
        {
            var eventDates = new SortedSet<DateOnly>(_eventDates);
            // Distinct event times with the earliest date at each, and every date's time.
            var timeOfDate = new Dictionary<DateOnly, double>();
            var dateOfTime = new SortedDictionary<double, DateOnly>();
            foreach (var date in eventDates)
            {
                var t = DayCounter.YearFraction(ValuationDate, date);
                if (!double.IsFinite(t) || t < 0.0)
                    throw new InvalidOperationException($"{DayCounter.Name} gives the time {t} to {date:yyyy-MM-dd}: simulation times must be finite and non-negative.");
                timeOfDate[date] = t;
                if (t > 0.0) dateOfTime.TryAdd(t, date);
            }
            if (dateOfTime.Count == 0)
                throw new InvalidOperationException("A simulation grid needs at least one event date after the valuation date.");

            var times = new List<double> { 0.0 };
            var pillarDates = new List<DateOnly?> { ValuationDate };
            var pillarOfTime = new Dictionary<double, int> { [0.0] = 0 };
            foreach (var (eventTime, date) in dateOfTime)
            {
                var start = times[^1];
                var steps = StepCount(eventTime - start);
                for (var j = 1; j < steps; j++)
                {
                    times.Add(start + (eventTime - start) * j / steps);
                    pillarDates.Add(null);
                }
                pillarOfTime[eventTime] = times.Count;
                times.Add(eventTime);
                pillarDates.Add(date);
            }
            for (var k = 1; k < times.Count; k++)
            {
                if (!(times[k] > times[k - 1]))
                    throw new InvalidOperationException($"The maximum time step {MaxTimeStep} is too small to keep pillar times distinct in double precision.");
            }

            var eventPillars = new Dictionary<DateOnly, int>();
            foreach (var (date, t) in timeOfDate) eventPillars[date] = pillarOfTime[t];
            var distinct = new DateOnly[eventDates.Count];
            eventDates.CopyTo(distinct);
            return new SimulationGrid(ValuationDate, DayCounter, times.ToArray(), pillarDates.ToArray(), distinct, eventPillars);
        }

        private int StepCount(double gap)
        {
            if (MaxTimeStep is not double h) return 1;
            var steps = Math.Ceiling(gap / h * (1.0 - StepCountTolerance));
            return steps < 1.0 ? 1 : checked((int)steps);
        }
    }
}
