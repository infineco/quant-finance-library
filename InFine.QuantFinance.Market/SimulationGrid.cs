using System;
using System.Collections.Generic;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// The time grid of a Monte Carlo simulation, compiled from dates by <see cref="SimulationGridBuilder"/>: pillar times
    /// in years from the valuation date (pillar 0 is time 0), strictly increasing, holding every event date of the
    /// products plus refinement pillars that keep each step within a maximum length. This is the seam between the
    /// host model and the kernels: <see cref="ToTimesArray"/> is what a kernel schedule is built from, and
    /// <see cref="PillarIndex"/> tells a product which pillar holds each of its dates. Immutable.
    /// </summary>
    public sealed class SimulationGrid
    {
        private readonly double[] _times;
        private readonly DateOnly?[] _pillarDates;
        private readonly DateOnly[] _eventDates;
        private readonly Dictionary<DateOnly, int> _eventPillars;

        // Takes ownership of the arrays.
        internal SimulationGrid(DateOnly valuationDate, IDayCounter dayCounter, double[] times, DateOnly?[] pillarDates,
            DateOnly[] eventDates, Dictionary<DateOnly, int> eventPillars)
        {
            ValuationDate = valuationDate;
            DayCounter = dayCounter;
            _times = times;
            _pillarDates = pillarDates;
            _eventDates = eventDates;
            _eventPillars = eventPillars;
        }

        /// <summary>The date of time zero.</summary>
        public DateOnly ValuationDate { get; }

        /// <summary>The convention turning dates into simulation times.</summary>
        public IDayCounter DayCounter { get; }

        /// <summary>The pillar times, starting at 0, strictly increasing.</summary>
        public IReadOnlyList<double> Times => _times;

        /// <summary>The number of pillars, i.e. <see cref="StepCount"/> + 1.</summary>
        public int PillarCount => _times.Length;

        /// <summary>The number of simulation steps between consecutive pillars.</summary>
        public int StepCount => _times.Length - 1;

        /// <summary>
        /// The date of each pillar: the valuation date at pillar 0, the (earliest) event date at an event pillar, and
        /// <c>null</c> at a refinement pillar, which falls between dates.
        /// </summary>
        public IReadOnlyList<DateOnly?> PillarDates => _pillarDates;

        /// <summary>The distinct event dates, in increasing order.</summary>
        public IReadOnlyList<DateOnly> EventDates => _eventDates;

        /// <summary>A fresh copy of <see cref="Times"/>, for a kernel schedule that takes ownership of its array.</summary>
        public double[] ToTimesArray() => (double[])_times.Clone();

        /// <summary>
        /// The pillar index of an event date or of the valuation date (0). Event dates with the same year fraction (possible
        /// with 30/360 conventions) share a pillar.
        /// </summary>
        /// <exception cref="KeyNotFoundException"><paramref name="date"/> was not given to the builder.</exception>
        public int PillarIndex(DateOnly date)
        {
            if (TryGetPillarIndex(date, out var index)) return index;
            throw new KeyNotFoundException($"{date:yyyy-MM-dd} is neither the valuation date nor an event date of the grid.");
        }

        /// <summary>The pillar index of an event date or of the valuation date, if it is one.</summary>
        public bool TryGetPillarIndex(DateOnly date, out int index)
        {
            if (date == ValuationDate)
            {
                index = 0;
                return true;
            }
            return _eventPillars.TryGetValue(date, out index);
        }

        /// <summary>The simulation time of <paramref name="date"/>: its year fraction from the valuation date.</summary>
        public double TimeOf(DateOnly date) => DayCounter.YearFraction(ValuationDate, date);
    }
}
