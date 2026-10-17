using System;
using System.Collections.Generic;
using InFine.QuantFinance.Portfolio;
using InFine.QuantFinance.Risk;

namespace InFine.QuantFinance.Backtesting
{
    /// <summary>What a strategy knows when it decides the weights of period <see cref="Period"/>.</summary>
    /// <remarks>
    /// <b>Timing convention.</b> The decision for period <c>t</c> is taken at the close of period <c>t - 1</c> (before
    /// any return of period <c>t</c> is known) and earns the returns of row <c>t</c>. <see cref="Window"/> therefore holds
    /// rows <c>0 .. t-1</c> only, and reading row <c>t</c> or later throws. The current weights are those of the
    /// portfolio after the close of period <c>t - 1</c> (drifted since the last rebalance), before any trade.
    /// </remarks>
    public sealed class StrategyContext
    {
        internal StrategyContext(int period, ReturnWindow window, double[] currentWeights, double cashWeight, double portfolioValue, bool isInitialAllocation)
        {
            Period = period;
            Window = window;
            CurrentWeights = Array.AsReadOnly(currentWeights);
            CashWeight = cashWeight;
            PortfolioValue = portfolioValue;
            IsInitialAllocation = isInitialAllocation;
        }

        /// <summary>Index of the period whose weights are being decided; <see cref="Window"/> holds exactly that many periods.</summary>
        public int Period { get; }

        /// <summary>The observable history: periods 0 .. <see cref="Period"/> - 1.</summary>
        public ReturnWindow Window { get; }

        /// <summary>Current (drifted, pre-trade) risky weights as fractions of the portfolio value; all zero before the first allocation.</summary>
        public IReadOnlyList<double> CurrentWeights { get; }

        /// <summary>Current cash weight, <c>1 - sum(CurrentWeights)</c> (negative when borrowing).</summary>
        public double CashWeight { get; }

        /// <summary>Portfolio value before trading.</summary>
        public double PortfolioValue { get; }

        /// <summary>True for the first decision of the backtest, when the portfolio is still all cash.</summary>
        public bool IsInitialAllocation { get; }
    }

    /// <summary>
    /// A rule that maps the observable history to target weights at each rebalance date.
    /// </summary>
    /// <remarks>
    /// Implementations receive only a <see cref="StrategyContext"/>, whose window ends at the last closed period (see the
    /// timing convention there), so they cannot see the future through it. The bootstrap calls one instance from several
    /// threads: implementations must be stateless or thread-safe (the built-in ones are immutable).
    /// </remarks>
    public interface IStrategy
    {
        /// <summary>Number of observed periods needed before the first decision (0 if none); the backtest starts there.</summary>
        int MinimumHistory { get; }

        /// <summary>
        /// Target risky weights (length N, fractions of the portfolio value before trading; <c>1 - sum</c> stays in cash,
        /// negative weights are shorts), or null to keep the current, drifted holdings without trading.
        /// </summary>
        double[]? TargetWeights(StrategyContext context);
    }

    /// <summary>Buys the given weights at the first decision and never trades again; the weights then drift with prices.</summary>
    public sealed class BuyAndHoldStrategy : IStrategy
    {
        readonly double[] _weights;

        /// <param name="weights">Initial risky weights.</param>
        public BuyAndHoldStrategy(double[] weights) => _weights = Strategies.CheckWeights(weights);

        /// <inheritdoc />
        public int MinimumHistory => 0;

        /// <inheritdoc />
        public double[]? TargetWeights(StrategyContext context) => context.IsInitialAllocation ? (double[])_weights.Clone() : null;
    }

    /// <summary>Constant mix: back to the same fixed weights at every rebalance date.</summary>
    public sealed class ConstantMixStrategy : IStrategy
    {
        readonly double[] _weights;

        /// <param name="weights">Target risky weights.</param>
        public ConstantMixStrategy(double[] weights) => _weights = Strategies.CheckWeights(weights);

        /// <summary>Copy of the target weights.</summary>
        public double[] Weights => (double[])_weights.Clone();

        /// <inheritdoc />
        public int MinimumHistory => 0;

        /// <inheritdoc />
        public double[]? TargetWeights(StrategyContext context) => (double[])_weights.Clone();
    }

    /// <summary>Which covariance estimator a rolling-window strategy uses.</summary>
    public enum CovarianceMethod
    {
        /// <summary>Unbiased sample covariance (<see cref="Covariance.Sample(double[,], bool)"/>).</summary>
        Sample,

        /// <summary>Ledoit-Wolf shrinkage towards constant correlation (<see cref="LedoitWolf.ConstantCorrelation"/>).</summary>
        LedoitWolf,

        /// <summary>RiskMetrics EWMA (<see cref="EwmaCovariance.Estimate"/>).</summary>
        Ewma,
    }

    /// <summary>A covariance estimator applied to the last <see cref="Lookback"/> observed periods.</summary>
    public sealed class CovarianceEstimator
    {
        CovarianceEstimator(CovarianceMethod method, int lookback, double lambda, int seedWindow)
        {
            Method = method;
            Lookback = lookback;
            Lambda = lambda;
            SeedWindow = seedWindow;
        }

        /// <summary>Sample covariance of the last <paramref name="lookback"/> periods (at least 2).</summary>
        public static CovarianceEstimator Sample(int lookback)
        {
            if (lookback < 2) throw new ArgumentOutOfRangeException(nameof(lookback), "The sample covariance needs at least 2 periods.");
            return new CovarianceEstimator(CovarianceMethod.Sample, lookback, double.NaN, 0);
        }

        /// <summary>Ledoit-Wolf constant-correlation shrinkage on the last <paramref name="lookback"/> periods (at least 2; needs 2 assets or more).</summary>
        public static CovarianceEstimator LedoitWolf(int lookback)
        {
            if (lookback < 2) throw new ArgumentOutOfRangeException(nameof(lookback), "Shrinkage needs at least 2 periods.");
            return new CovarianceEstimator(CovarianceMethod.LedoitWolf, lookback, double.NaN, 0);
        }

        /// <summary>
        /// EWMA covariance run over the last <paramref name="lookback"/> periods, seeded with the zero-mean sample moment of
        /// the first <paramref name="seedWindow"/> of them (0: start at the first outer product).
        /// </summary>
        public static CovarianceEstimator Ewma(int lookback, double lambda = EwmaCovariance.DefaultLambda, int seedWindow = 0)
        {
            if (lookback < 1) throw new ArgumentOutOfRangeException(nameof(lookback));
            if (!(lambda > 0.0 && lambda < 1.0)) throw new ArgumentOutOfRangeException(nameof(lambda), "Lambda must lie in (0, 1).");
            if (seedWindow < 0 || seedWindow > lookback) throw new ArgumentOutOfRangeException(nameof(seedWindow));
            return new CovarianceEstimator(CovarianceMethod.Ewma, lookback, lambda, seedWindow);
        }

        /// <summary>The estimator.</summary>
        public CovarianceMethod Method { get; }

        /// <summary>Number of most recent periods the estimate uses.</summary>
        public int Lookback { get; }

        /// <summary>EWMA decay (NaN for the other methods).</summary>
        public double Lambda { get; }

        /// <summary>EWMA seed window (0 for the other methods).</summary>
        public int SeedWindow { get; }

        /// <summary>Per-period covariance of the last <see cref="Lookback"/> periods of <paramref name="window"/>.</summary>
        public double[,] Estimate(ReturnWindow window)
        {
            ArgumentNullException.ThrowIfNull(window);
            double[,] tail = window.Tail(Lookback);
            return Method switch
            {
                CovarianceMethod.Sample => Covariance.Sample(tail),
                CovarianceMethod.LedoitWolf => Risk.LedoitWolf.ConstantCorrelation(tail).Covariance,
                _ => EwmaCovariance.Estimate(tail, Lambda, SeedWindow),
            };
        }
    }

    /// <summary>Inverse-volatility weights (<see cref="RiskBudgeting.InverseVolatility(double[,])"/>) from a rolling covariance.</summary>
    public sealed class InverseVolatilityStrategy : IStrategy
    {
        readonly CovarianceEstimator _estimator;

        /// <param name="estimator">Rolling covariance estimator (only its diagonal matters).</param>
        public InverseVolatilityStrategy(CovarianceEstimator estimator) => _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));

        /// <inheritdoc />
        public int MinimumHistory => _estimator.Lookback;

        /// <inheritdoc />
        public double[]? TargetWeights(StrategyContext context) => RiskBudgeting.InverseVolatility(_estimator.Estimate(context.Window));
    }

    /// <summary>
    /// Equal-risk-contribution (or general risk-budgeting) weights from a rolling covariance, through
    /// <see cref="RiskParityOptimizer.Solve"/>.
    /// </summary>
    public sealed class EqualRiskContributionStrategy : IStrategy
    {
        readonly CovarianceEstimator _estimator;
        readonly double[]? _budgets;
        readonly RiskParityOptions? _options;

        /// <param name="estimator">Rolling covariance estimator.</param>
        /// <param name="budgets">Relative risk budgets summing to one; null for equal budgets.</param>
        /// <param name="options">Optimiser options; null for the defaults.</param>
        public EqualRiskContributionStrategy(CovarianceEstimator estimator, double[]? budgets = null, RiskParityOptions? options = null)
        {
            _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));
            _budgets = budgets == null ? null : (double[])budgets.Clone();
            _options = options;
        }

        /// <inheritdoc />
        public int MinimumHistory => _estimator.Lookback;

        /// <inheritdoc />
        public double[]? TargetWeights(StrategyContext context)
        {
            double[,] cov = _estimator.Estimate(context.Window);
            RiskBudgetingResult result = _budgets == null
                ? RiskParityOptimizer.EqualRiskContribution(cov, _options)
                : RiskParityOptimizer.Solve(cov, _budgets, _options);
            return result.Weights;
        }
    }

    /// <summary>
    /// Minimum-variance weights from a rolling covariance, through <see cref="MeanVarianceOptimizer.MinimumVariance"/>
    /// (long-only and fully invested unless other constraints are given).
    /// </summary>
    public sealed class MinimumVarianceStrategy : IStrategy
    {
        readonly CovarianceEstimator _estimator;
        readonly PortfolioConstraints? _constraints;

        /// <param name="estimator">Rolling covariance estimator.</param>
        /// <param name="constraints">Feasible set; null for long-only, fully invested.</param>
        public MinimumVarianceStrategy(CovarianceEstimator estimator, PortfolioConstraints? constraints = null)
        {
            _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));
            _constraints = constraints;
        }

        /// <inheritdoc />
        public int MinimumHistory => _estimator.Lookback;

        /// <inheritdoc />
        /// <exception cref="InvalidOperationException">The optimiser did not return an optimal portfolio (e.g. infeasible constraints).</exception>
        public double[]? TargetWeights(StrategyContext context)
        {
            double[,] cov = _estimator.Estimate(context.Window);
            var optimizer = new MeanVarianceOptimizer(new double[context.Window.AssetCount], cov, _constraints);
            MeanVarianceResult result = optimizer.MinimumVariance();
            if (result.Status != MeanVarianceStatus.Optimal)
                throw new InvalidOperationException($"Minimum-variance optimisation for period {context.Period} ended with status {result.Status}: {result.Message}");
            return result.Weights;
        }
    }

    static class Strategies
    {
        internal static double[] CheckWeights(double[] weights)
        {
            ArgumentNullException.ThrowIfNull(weights);
            if (weights.Length == 0) throw new ArgumentException("At least one weight is required.", nameof(weights));
            foreach (double w in weights)
                if (!double.IsFinite(w)) throw new ArgumentException("Weights must be finite.", nameof(weights));
            return (double[])weights.Clone();
        }
    }
}
