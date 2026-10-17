using System;
using System.Collections.Generic;

namespace InFine.QuantFinance.Backtesting
{
    /// <summary>Calendar period used by <see cref="RebalanceSchedule.Calendar"/>.</summary>
    public enum CalendarFrequency
    {
        /// <summary>Weeks starting on Monday.</summary>
        Weekly,

        /// <summary>Calendar months.</summary>
        Monthly,

        /// <summary>Calendar quarters.</summary>
        Quarterly,

        /// <summary>Calendar years.</summary>
        Annual,
    }

    /// <summary>
    /// When the strategy is consulted. A rebalance "at period t" is a decision taken at the close of period <c>t - 1</c>
    /// for the weights of period <c>t</c> (see <see cref="StrategyContext"/>). The first period of the backtest is always a
    /// decision (the initial allocation from cash), whatever the schedule; between decisions the holdings drift.
    /// </summary>
    public sealed class RebalanceSchedule
    {
        enum Kind { Every, Periods, Calendar }

        readonly Kind _kind;
        readonly int _every;
        readonly int[] _periods;
        readonly CalendarFrequency _frequency;

        RebalanceSchedule(Kind kind, int every, int[] periods, CalendarFrequency frequency)
        {
            _kind = kind;
            _every = every;
            _periods = periods;
            _frequency = frequency;
        }

        /// <summary>A decision at every period.</summary>
        public static RebalanceSchedule EveryPeriod { get; } = Every(1);

        /// <summary>Only the initial allocation; the holdings drift afterwards.</summary>
        public static RebalanceSchedule Never { get; } = new RebalanceSchedule(Kind.Periods, 0, Array.Empty<int>(), default);

        /// <summary>A decision at the start period and every <paramref name="periods"/> periods after it.</summary>
        public static RebalanceSchedule Every(int periods)
        {
            if (periods < 1) throw new ArgumentOutOfRangeException(nameof(periods), "The rebalance interval must be at least 1 period.");
            return new RebalanceSchedule(Kind.Every, periods, Array.Empty<int>(), default);
        }

        /// <summary>Decisions at the given panel period indices (plus the start); indices before the start or past the end are ignored.</summary>
        public static RebalanceSchedule AtPeriods(IEnumerable<int> periods)
        {
            ArgumentNullException.ThrowIfNull(periods);
            return new RebalanceSchedule(Kind.Periods, 0, new List<int>(periods).ToArray(), default);
        }

        /// <summary>
        /// A decision at the close of the last observation of each calendar period: at period <c>t</c> whenever
        /// <c>dates[t]</c> falls in a different week / month / quarter / year than <c>dates[t - 1]</c> (month-end
        /// rebalancing for <see cref="CalendarFrequency.Monthly"/>). Only the calendar of the panel is used, not its data.
        /// </summary>
        public static RebalanceSchedule Calendar(CalendarFrequency frequency) =>
            new RebalanceSchedule(Kind.Calendar, 0, Array.Empty<int>(), frequency);

        /// <summary>Decision flags for every period of a panel of <paramref name="dates"/>, with the backtest starting at <paramref name="start"/>.</summary>
        public bool[] Flags(DateOnly[] dates, int start)
        {
            ArgumentNullException.ThrowIfNull(dates);
            int t = dates.Length;
            if (start < 0 || start >= t) throw new ArgumentOutOfRangeException(nameof(start));
            var flags = new bool[t];
            switch (_kind)
            {
                case Kind.Every:
                    for (int k = start; k < t; k += _every) flags[k] = true;
                    break;
                case Kind.Periods:
                    foreach (int k in _periods)
                        if (k >= start && k < t) flags[k] = true;
                    break;
                default:
                    for (int k = Math.Max(start, 1); k < t; k++)
                        if (Key(dates[k]) != Key(dates[k - 1])) flags[k] = true;
                    break;
            }
            flags[start] = true;
            return flags;
        }

        int Key(DateOnly d) => _frequency switch
        {
            // DayNumber 0 is Monday 1 January 0001, so DayNumber / 7 changes every Monday.
            CalendarFrequency.Weekly => d.DayNumber / 7,
            CalendarFrequency.Monthly => d.Year * 12 + d.Month - 1,
            CalendarFrequency.Quarterly => d.Year * 4 + (d.Month - 1) / 3,
            _ => d.Year,
        };
    }
}
