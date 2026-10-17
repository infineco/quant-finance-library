using System;
using InFine.QuantFinance.Risk;

namespace InFine.QuantFinance.Backtesting
{
    /// <summary>
    /// An aligned panel of simple returns: <c>T</c> periods (rows, oldest first) by <c>N</c> assets (columns), with the
    /// date on which each period ends and, optionally, the simple return of cash over each period.
    /// </summary>
    /// <remarks>
    /// Row <c>t</c> holds <c>r[t, i] = P_i(dates[t]) / P_i(previous date) - 1</c>: the return earned over period <c>t</c>,
    /// known at the close of <c>dates[t]</c>. The cash return <c>cash[t]</c> is the per-period (not annualised) simple
    /// return of the cash account over the same period, e.g. a money-market rate times the accrual fraction; without a
    /// cash series, cash earns nothing. The panel copies its inputs and is immutable.
    /// </remarks>
    public sealed class ReturnPanel
    {
        readonly DateOnly[] _dates;
        readonly double[] _returns; // row-major, r[t * N + i]
        readonly double[] _cash;    // length T, zeros when no cash series was given
        readonly string[] _names;

        /// <param name="dates">End date of each period, strictly increasing, length T &gt;= 1.</param>
        /// <param name="returns">T x N simple returns, finite and greater than -1.</param>
        /// <param name="cashReturns">Optional per-period simple cash returns, length T, finite and greater than -1.</param>
        /// <param name="assetNames">Optional asset names, length N.</param>
        public ReturnPanel(DateOnly[] dates, double[,] returns, double[]? cashReturns = null, string[]? assetNames = null)
        {
            ArgumentNullException.ThrowIfNull(dates);
            ArgumentNullException.ThrowIfNull(returns);
            int t = returns.GetLength(0), n = returns.GetLength(1);
            if (t < 1 || n < 1) throw new ArgumentException("The panel needs at least one period and one asset.", nameof(returns));
            if (dates.Length != t) throw new ArgumentException($"Expected {t} dates, one per return row.", nameof(dates));
            for (int k = 1; k < t; k++)
                if (dates[k] <= dates[k - 1]) throw new ArgumentException("Dates must be strictly increasing.", nameof(dates));
            _returns = new double[t * n];
            for (int k = 0; k < t; k++)
                for (int i = 0; i < n; i++)
                {
                    double r = returns[k, i];
                    if (!double.IsFinite(r) || r <= -1.0) throw new ArgumentException("Returns must be finite and greater than -1.", nameof(returns));
                    _returns[k * n + i] = r;
                }
            _cash = new double[t];
            if (cashReturns != null)
            {
                if (cashReturns.Length != t) throw new ArgumentException($"Expected {t} cash returns.", nameof(cashReturns));
                for (int k = 0; k < t; k++)
                {
                    double r = cashReturns[k];
                    if (!double.IsFinite(r) || r <= -1.0) throw new ArgumentException("Cash returns must be finite and greater than -1.", nameof(cashReturns));
                    _cash[k] = r;
                }
            }
            if (assetNames != null && assetNames.Length != n) throw new ArgumentException($"Expected {n} asset names.", nameof(assetNames));
            _names = new string[n];
            for (int i = 0; i < n; i++) _names[i] = assetNames?[i] ?? $"Asset{i}";
            _dates = (DateOnly[])dates.Clone();
            HasCash = cashReturns != null;
            PeriodCount = t;
            AssetCount = n;
        }

        /// <summary>
        /// Panel of simple returns from a (T+1) x N price matrix; the period dates are <c>dates[1..T]</c>.
        /// </summary>
        /// <param name="dates">Date of each price row, strictly increasing, length T+1.</param>
        /// <param name="prices">Prices, rows oldest first, finite and strictly positive.</param>
        /// <param name="cashReturns">Optional per-period simple cash returns, length T.</param>
        /// <param name="assetNames">Optional asset names.</param>
        public static ReturnPanel FromPrices(DateOnly[] dates, double[,] prices, double[]? cashReturns = null, string[]? assetNames = null)
        {
            ArgumentNullException.ThrowIfNull(dates);
            ArgumentNullException.ThrowIfNull(prices);
            if (dates.Length != prices.GetLength(0)) throw new ArgumentException("Expected one date per price row.", nameof(dates));
            double[,] returns = Returns.Simple(prices);
            var periodDates = new DateOnly[dates.Length - 1];
            Array.Copy(dates, 1, periodDates, 0, periodDates.Length);
            return new ReturnPanel(periodDates, returns, cashReturns, assetNames);
        }

        /// <summary>Number of periods T.</summary>
        public int PeriodCount { get; }

        /// <summary>Number of assets N.</summary>
        public int AssetCount { get; }

        /// <summary>True when a cash return series was supplied.</summary>
        public bool HasCash { get; }

        /// <summary>End date of period <paramref name="period"/>.</summary>
        public DateOnly Date(int period) => _dates[period];

        /// <summary>Copy of the period end dates.</summary>
        public DateOnly[] Dates => (DateOnly[])_dates.Clone();

        /// <summary>Name of asset <paramref name="asset"/>.</summary>
        public string AssetName(int asset) => _names[asset];

        /// <summary>Return of asset <paramref name="asset"/> over period <paramref name="period"/>.</summary>
        public double this[int period, int asset]
        {
            get
            {
                if ((uint)period >= (uint)PeriodCount) throw new ArgumentOutOfRangeException(nameof(period));
                if ((uint)asset >= (uint)AssetCount) throw new ArgumentOutOfRangeException(nameof(asset));
                return _returns[period * AssetCount + asset];
            }
        }

        /// <summary>Cash return over period <paramref name="period"/> (0 without a cash series).</summary>
        public double CashReturn(int period) => _cash[period];

        /// <summary>Copy of the T x N return matrix.</summary>
        public double[,] ToMatrix()
        {
            var m = new double[PeriodCount, AssetCount];
            for (int t = 0; t < PeriodCount; t++)
                for (int i = 0; i < AssetCount; i++)
                    m[t, i] = _returns[t * AssetCount + i];
            return m;
        }

        /// <summary>
        /// A new panel on the same dates whose row <c>t</c> is row <c>rows[t]</c> of this one (returns and cash together).
        /// This is how a bootstrap history is materialised; the dates are kept as a calendar, not as data.
        /// </summary>
        public ReturnPanel Resample(int[] rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            if (rows.Length != PeriodCount) throw new ArgumentException($"Expected {PeriodCount} row indices.", nameof(rows));
            var r = new double[PeriodCount, AssetCount];
            var c = new double[PeriodCount];
            for (int t = 0; t < PeriodCount; t++)
            {
                int s = rows[t];
                if ((uint)s >= (uint)PeriodCount) throw new ArgumentOutOfRangeException(nameof(rows));
                for (int i = 0; i < AssetCount; i++) r[t, i] = _returns[s * AssetCount + i];
                c[t] = _cash[s];
            }
            return new ReturnPanel(_dates, r, HasCash ? c : null, _names);
        }

        /// <summary>
        /// The read-only view of the first <paramref name="count"/> periods (rows 0 .. count-1): what is known at the close
        /// of period <c>count - 1</c>, which is all a strategy deciding the weights of period <paramref name="count"/> may see.
        /// </summary>
        public ReturnWindow Window(int count)
        {
            if (count < 0 || count > PeriodCount) throw new ArgumentOutOfRangeException(nameof(count));
            return new ReturnWindow(this, count);
        }

        // Flat buffers for the bootstrap kernels and the engine (not exposed: a strategy must go through a window).
        internal double[] FlatReturns => _returns;
        internal double[] FlatCash => _cash;
    }

    /// <summary>
    /// Read-only view of the first <see cref="Count"/> periods of a <see cref="ReturnPanel"/>. Every accessor checks the
    /// row against <see cref="Count"/> and throws <see cref="ArgumentOutOfRangeException"/> beyond it, so a strategy
    /// handed a window cannot read a return it would not have known at decision time; the underlying panel is not exposed.
    /// </summary>
    public sealed class ReturnWindow
    {
        readonly ReturnPanel _panel;

        internal ReturnWindow(ReturnPanel panel, int count)
        {
            _panel = panel;
            Count = count;
        }

        /// <summary>Number of visible periods (rows 0 .. Count-1).</summary>
        public int Count { get; }

        /// <summary>Number of assets N.</summary>
        public int AssetCount => _panel.AssetCount;

        /// <summary>True when the panel has a cash series.</summary>
        public bool HasCash => _panel.HasCash;

        /// <summary>Return of <paramref name="asset"/> over visible period <paramref name="period"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="period"/> is not in [0, Count): a look-ahead.</exception>
        public double this[int period, int asset]
        {
            get
            {
                Check(period);
                return _panel[period, asset];
            }
        }

        /// <summary>End date of visible period <paramref name="period"/>.</summary>
        public DateOnly Date(int period)
        {
            Check(period);
            return _panel.Date(period);
        }

        /// <summary>Cash return of visible period <paramref name="period"/>.</summary>
        public double CashReturn(int period)
        {
            Check(period);
            return _panel.CashReturn(period);
        }

        /// <summary>Name of asset <paramref name="asset"/>.</summary>
        public string AssetName(int asset) => _panel.AssetName(asset);

        /// <summary>
        /// Copy of the last <paramref name="lookback"/> visible periods as a lookback x N matrix, oldest first (the layout
        /// the Risk covariance estimators take).
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="lookback"/> is not in [1, Count].</exception>
        public double[,] Tail(int lookback)
        {
            if (lookback < 1 || lookback > Count)
                throw new ArgumentOutOfRangeException(nameof(lookback), $"A lookback of {lookback} periods needs that many observed periods; {Count} are visible.");
            int n = AssetCount, start = Count - lookback;
            var m = new double[lookback, n];
            for (int t = 0; t < lookback; t++)
                for (int i = 0; i < n; i++)
                    m[t, i] = _panel[start + t, i];
            return m;
        }

        void Check(int period)
        {
            if ((uint)period >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(period),
                    $"Period {period} is not observable: the window holds periods 0 .. {Count - 1} (no look-ahead).");
        }
    }
}
