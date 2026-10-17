using System;
using System.Collections.Generic;
using InFine.QuantFinance.Numerics;

namespace InFine.QuantFinance.Portfolio
{
    /// <summary>Outcome of a <see cref="MeanVarianceOptimizer"/> request.</summary>
    public enum MeanVarianceStatus
    {
        /// <summary>The portfolio solves the request.</summary>
        Optimal,

        /// <summary>No portfolio satisfies the constraints (or the target return / positive excess return).</summary>
        Infeasible,

        /// <summary>The objective is unbounded on the feasible set (e.g. maximum return without bounds).</summary>
        Unbounded,

        /// <summary>
        /// A risk target outside the attainable range: the closest end of the efficient frontier is returned
        /// (minimum-risk portfolio when the target is too low, maximum-return portfolio when it is too high).
        /// </summary>
        TargetUnattainable,

        /// <summary>The covariance is not positive definite and regularisation is disabled.</summary>
        NotConvex,

        /// <summary>The quadratic-programming iteration limit was hit.</summary>
        MaxIterationsReached,
    }

    /// <summary>A portfolio returned by <see cref="MeanVarianceOptimizer"/> with its risk and return.</summary>
    public sealed record MeanVarianceResult
    {
        /// <summary>Status of the request.</summary>
        public MeanVarianceStatus Status { get; init; }

        /// <summary>Weights (meaningful when <see cref="Status"/> is Optimal or TargetUnattainable).</summary>
        public double[] Weights { get; init; } = Array.Empty<double>();

        /// <summary>Expected return <c>mu^T w</c>.</summary>
        public double ExpectedReturn { get; init; }

        /// <summary>Variance <c>w^T Sigma w</c>.</summary>
        public double Variance { get; init; }

        /// <summary>Volatility <c>sqrt(w^T Sigma w)</c>.</summary>
        public double Volatility => Math.Sqrt(Math.Max(Variance, 0.0));

        /// <summary>Expected active return <c>mu^T (w - b)</c> for benchmark-relative requests, NaN otherwise.</summary>
        public double ActiveReturn { get; init; } = double.NaN;

        /// <summary>Tracking error <c>sqrt((w - b)^T Sigma (w - b))</c> for benchmark-relative requests, NaN otherwise.</summary>
        public double TrackingError { get; init; } = double.NaN;

        /// <summary>
        /// Risk aversion <c>lambda</c> of the mean-variance utility the portfolio maximises (given, or found by the
        /// risk-target search); +infinity for a minimum-risk portfolio, 0 for the maximum-return one, NaN when the
        /// request is not a utility (target return, maximum Sharpe ratio).
        /// </summary>
        public double RiskAversion { get; init; } = double.NaN;

        /// <summary>Total number of active-set changes over all quadratic programmes solved for the request.</summary>
        public int Iterations { get; init; }

        /// <summary>
        /// The last quadratic programme solved, with its multipliers (diagnostics). Its scaling may differ from the
        /// request: utilities are solved as <c>min 1/2 a^T Sigma a - t mu^T a</c> with <c>t = 1 / lambda</c>, the maximum
        /// Sharpe ratio in the homogenised variables.
        /// </summary>
        public QuadraticProgramResult? Solution { get; init; }

        /// <summary>Explanation when the status is not Optimal.</summary>
        public string? Message { get; init; }

        /// <summary>Sharpe ratio <c>(mu^T w - r_f sum_i w_i) / sigma</c>.</summary>
        public double SharpeRatio(double riskFreeRate)
        {
            double sum = 0.0;
            foreach (double w in Weights) sum += w;
            return (ExpectedReturn - riskFreeRate * sum) / Volatility;
        }
    }

    /// <summary>Points of an efficient frontier, from the minimum-variance to the maximum-return portfolio.</summary>
    public sealed record EfficientFrontier
    {
        /// <summary>Optimal, or the status of the end point that could not be computed.</summary>
        public MeanVarianceStatus Status { get; init; }

        /// <summary>Portfolios in increasing order of target return (empty when the status is not Optimal).</summary>
        public IReadOnlyList<MeanVarianceResult> Points { get; init; } = Array.Empty<MeanVarianceResult>();

        /// <summary>Expected returns of <see cref="Points"/>.</summary>
        public double[] Returns { get; init; } = Array.Empty<double>();

        /// <summary>Volatilities of <see cref="Points"/>.</summary>
        public double[] Volatilities { get; init; } = Array.Empty<double>();

        /// <summary>Explanation when the status is not Optimal.</summary>
        public string? Message { get; init; }
    }

    /// <summary>
    /// Constrained Markowitz mean-variance optimisation on a covariance matrix (sample, EWMA or Ledoit-Wolf from
    /// <c>InFine.QuantFinance.Risk</c>) and a vector of expected returns, over the feasible set of
    /// <see cref="PortfolioConstraints"/>. Every request is one or a short sequence of quadratic programmes solved
    /// exactly by <see cref="QuadraticProgramSolver"/>. Returns and covariance must share the same horizon and units.
    /// </summary>
    /// <remarks>
    /// A positive semi-definite covariance (more assets than observations) is ridged by the solver, see
    /// <see cref="QuadraticProgramOptions.RegularizationFloor"/>; prefer a shrinkage estimate.
    /// </remarks>
    public sealed class MeanVarianceOptimizer
    {
        // The risk-target and maximum-return searches walk t = 1 / lambda over decades from a natural scale.
        const int MaxDecades = 30;
        const int MaxBisections = 200;

        readonly double[] mu;
        readonly double[,] sigma;
        readonly QuadraticProgramOptions? options;
        readonly int n;
        readonly double[,]? eqMatrix;
        readonly double[]? eqVector;
        readonly double[,]? ineqMatrix;
        readonly double[]? ineqVector;
        readonly double[]? lower, upper;
        readonly double tScale;

        /// <param name="expectedReturns">Expected returns <c>mu</c>, length n.</param>
        /// <param name="covariance">Covariance <c>Sigma</c>, n x n, symmetric positive (semi-)definite.</param>
        /// <param name="constraints">Feasible set; null selects <see cref="PortfolioConstraints.LongOnly"/>.</param>
        /// <param name="options">Quadratic-programming settings; null for the defaults.</param>
        public MeanVarianceOptimizer(double[] expectedReturns, double[,] covariance, PortfolioConstraints? constraints = null, QuadraticProgramOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(expectedReturns);
            ArgumentNullException.ThrowIfNull(covariance);
            n = expectedReturns.Length;
            if (n == 0) throw new ArgumentException("At least one asset is required.", nameof(expectedReturns));
            foreach (double m in expectedReturns)
                if (!double.IsFinite(m)) throw new ArgumentException("Expected returns must be finite.", nameof(expectedReturns));
            if (covariance.GetLength(0) != n || covariance.GetLength(1) != n)
                throw new ArgumentException($"Covariance must be {n}x{n}.", nameof(covariance));
            if (!Matrix.IsFinite(covariance)) throw new ArgumentException("Covariance must be finite.", nameof(covariance));
            double maxAbs = 0.0;
            foreach (double v in covariance) maxAbs = Math.Max(maxAbs, Math.Abs(v));
            if (Matrix.MaxAsymmetry(covariance) > 1e-10 * maxAbs) throw new ArgumentException("Covariance must be symmetric.", nameof(covariance));
            for (int i = 0; i < n; i++)
                if (covariance[i, i] < 0.0) throw new ArgumentException("Covariance has a negative variance.", nameof(covariance));

            mu = (double[])expectedReturns.Clone();
            sigma = Matrix.Symmetrize(covariance);
            Constraints = constraints ?? PortfolioConstraints.LongOnly(n);
            Constraints.Validate(n);
            this.options = options;

            // Assemble the constraint blocks once.
            var eqRows = new List<double[]>();
            var eqRhs = new List<double>();
            var inRows = new List<double[]>();
            var inRhs = new List<double>();
            if (Constraints.Budget is double budget)
            {
                var ones = new double[n];
                Array.Fill(ones, 1.0);
                eqRows.Add(ones);
                eqRhs.Add(budget);
            }
            foreach (LinearConstraint lc in Constraints.Linear)
            {
                if (lc.IsEquality) { eqRows.Add(lc.Coefficients); eqRhs.Add(lc.Lower); continue; }
                if (!double.IsPositiveInfinity(lc.Upper)) { inRows.Add(lc.Coefficients); inRhs.Add(lc.Upper); }
                if (!double.IsNegativeInfinity(lc.Lower)) { inRows.Add(Negate(lc.Coefficients)); inRhs.Add(-lc.Lower); }
            }
            if (eqRows.Count > 0) { eqMatrix = ToMatrix(eqRows, n); eqVector = eqRhs.ToArray(); }
            if (inRows.Count > 0) { ineqMatrix = ToMatrix(inRows, n); ineqVector = inRhs.ToArray(); }
            lower = Constraints.LowerBounds == null ? null : (double[])Constraints.LowerBounds.Clone();
            upper = Constraints.UpperBounds == null ? null : (double[])Constraints.UpperBounds.Clone();

            // Natural scale of t = 1 / lambda: average variance over the largest |expected return|.
            double trace = 0.0, muMax = 0.0;
            for (int i = 0; i < n; i++) { trace += sigma[i, i]; muMax = Math.Max(muMax, Math.Abs(mu[i])); }
            tScale = trace > 0.0 && muMax > 0.0 ? trace / n / muMax : 1.0;
        }

        /// <summary>Number of assets.</summary>
        public int AssetCount => n;

        /// <summary>The feasible set.</summary>
        public PortfolioConstraints Constraints { get; }

        /// <summary>Global minimum-variance portfolio over the feasible set.</summary>
        public MeanVarianceResult MinimumVariance()
        {
            QuadraticProgramResult qp = Utility(0.0, null);
            return Result(qp, null, double.PositiveInfinity, qp.Iterations);
        }

        /// <summary>Minimum-variance portfolio with <c>mu^T w = targetReturn</c> (a point of the frontier).</summary>
        public MeanVarianceResult MinimumVarianceForTargetReturn(double targetReturn)
        {
            if (!double.IsFinite(targetReturn)) throw new ArgumentOutOfRangeException(nameof(targetReturn));
            QuadraticProgramResult qp = Solve(sigma, new double[n], mu, targetReturn);
            MeanVarianceResult result = Result(qp, null, double.NaN, qp.Iterations);
            return result.Status == MeanVarianceStatus.Infeasible
                ? result with { Message = $"No feasible portfolio has an expected return of {targetReturn}." }
                : result;
        }

        /// <summary>
        /// Mean-variance utility <c>max mu^T a - lambda / 2 a^T Sigma a</c> with <c>a = w - b</c> the active weights
        /// against <paramref name="benchmark"/> (absolute weights when null).
        /// </summary>
        public MeanVarianceResult MaximumUtility(double riskAversion, double[]? benchmark = null)
        {
            if (!(riskAversion > 0.0) || double.IsInfinity(riskAversion)) throw new ArgumentOutOfRangeException(nameof(riskAversion), "Risk aversion must be positive and finite.");
            CheckBenchmark(benchmark);
            // lambda/2 w^T Sigma w - (mu + lambda Sigma b)^T w, so that the multipliers are those of the stated utility.
            var q = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int k = 0; k < n; k++) q[i, k] = riskAversion * sigma[i, k];
            double[] sb = benchmark == null ? new double[n] : Multiply(sigma, benchmark);
            var c = new double[n];
            for (int i = 0; i < n; i++) c[i] = -(mu[i] + riskAversion * sb[i]);
            QuadraticProgramResult qp = Solve(q, c, null, 0.0);
            return Result(qp, benchmark, riskAversion, qp.Iterations);
        }

        /// <summary>
        /// Maximum expected return over the feasible set; among several maximisers, the one of minimum variance (the
        /// upper end of the efficient frontier). Unbounded when the feasible set does not bound the return.
        /// </summary>
        /// <remarks>
        /// The linear programme is solved as the limit <c>lambda -&gt; 0</c> of the utility: the solution path is
        /// piecewise affine in <c>t = 1 / lambda</c> and constant on its last piece, which is detected when two
        /// solutions a decade apart share the active set and the weights.
        /// </remarks>
        public MeanVarianceResult MaximumReturn()
        {
            double t = tScale;
            QuadraticProgramResult prev = Utility(t, null);
            int iterations = prev.Iterations;
            if (!prev.IsOptimal) return Result(prev, null, 0.0, iterations);
            for (int k = 0; k < MaxDecades; k++)
            {
                t *= 10.0;
                QuadraticProgramResult next = Utility(t, null);
                iterations += next.Iterations;
                if (!next.IsOptimal) return Result(next, null, 0.0, iterations);
                if (next.ActiveSetKey == prev.ActiveSetKey && MaxAbsDiff(next.X, prev.X) <= 1e-9 * Math.Max(1.0, MaxAbs(next.X)))
                    return Result(next, null, 0.0, iterations);
                prev = next;
            }
            return Result(prev, null, 0.0, iterations) with
            {
                Status = MeanVarianceStatus.Unbounded,
                Message = "The expected return is unbounded on the feasible set.",
            };
        }

        /// <summary>
        /// Maximum expected return with variance at most <paramref name="targetVariance"/> (equal to it on the
        /// efficient frontier), found by a search on the risk aversion of the utility.
        /// </summary>
        public MeanVarianceResult MaximumReturnForTargetVariance(double targetVariance)
        {
            if (!(targetVariance > 0.0) || double.IsInfinity(targetVariance)) throw new ArgumentOutOfRangeException(nameof(targetVariance));
            return RiskTarget(targetVariance, null);
        }

        /// <summary>Portfolio closest to <paramref name="benchmark"/> in tracking error over the feasible set.</summary>
        public MeanVarianceResult MinimumTrackingError(double[] benchmark)
        {
            ArgumentNullException.ThrowIfNull(benchmark);
            CheckBenchmark(benchmark);
            QuadraticProgramResult qp = Utility(0.0, benchmark);
            return Result(qp, benchmark, double.PositiveInfinity, qp.Iterations);
        }

        /// <summary>
        /// Maximum expected active return <c>mu^T (w - b)</c> with tracking error at most
        /// <paramref name="trackingError"/> (equal to it unless the maximum-active-return portfolio is closer), by a
        /// search on the risk aversion <c>lambda</c> of <see cref="MaximumUtility"/>: the tracking error decreases
        /// with <c>lambda</c>, and the solution path is piecewise affine in <c>1 / lambda</c>, so once a bracket lies on
        /// one piece the target is hit by solving a quadratic equation.
        /// </summary>
        public MeanVarianceResult MaximumActiveReturnForTrackingError(double[] benchmark, double trackingError)
        {
            ArgumentNullException.ThrowIfNull(benchmark);
            CheckBenchmark(benchmark);
            if (!(trackingError > 0.0) || double.IsInfinity(trackingError)) throw new ArgumentOutOfRangeException(nameof(trackingError));
            return RiskTarget(trackingError * trackingError, benchmark);
        }

        /// <summary>
        /// Maximum Sharpe ratio (tangency) portfolio <c>max (mu - r_f)^T w / sqrt(w^T Sigma w)</c> over the feasible
        /// set, which must have a positive budget.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The ratio is not concave, but it is invariant to scaling, so with <c>y = kappa w</c> and
        /// <c>kappa = 1^T y / Budget &gt;= 0</c> the problem becomes the convex quadratic programme
        /// <c>min y^T Sigma y</c> subject to <c>(mu - r_f)^T y = 1</c> and the constraints homogenised: a bound
        /// <c>w_i &lt;= u_i</c> becomes <c>y_i - u_i kappa &lt;= 0</c>, a linear constraint <c>a^T w &lt;= U</c> becomes
        /// <c>a^T y - U kappa &lt;= 0</c> (Cornuejols and Tutuncu, "Optimization Methods in Finance", section 8.2).
        /// Then <c>w = y / kappa</c>. This is exact for any linear constraints on the weights, as long as some feasible
        /// portfolio earns more than the risk-free rate (otherwise the status is Infeasible); it does not extend to
        /// non-homogeneous terms such as turnover against current holdings or a tracking-error budget. A solution
        /// with <c>kappa = 0</c> means the ratio is approached only at infinite leverage (Unbounded).
        /// </para>
        /// </remarks>
        public MeanVarianceResult MaximumSharpeRatio(double riskFreeRate)
        {
            if (!double.IsFinite(riskFreeRate)) throw new ArgumentOutOfRangeException(nameof(riskFreeRate));
            if (!(Constraints.Budget is double budget) || !(budget > 0.0))
                throw new InvalidOperationException("The maximum Sharpe ratio needs a positive budget constraint.");

            var excess = new double[n];
            for (int i = 0; i < n; i++) excess[i] = mu[i] - riskFreeRate;
            var eqRows = new List<double[]> { excess };
            var eqRhs = new List<double> { 1.0 };
            var inRows = new List<double[]>();
            double[]? yLower = null;
            // kappa >= 0
            inRows.Add(Fill(n, -1.0));
            for (int i = 0; i < n; i++)
            {
                double l = lower?[i] ?? double.NegativeInfinity, u = upper?[i] ?? double.PositiveInfinity;
                if (l == 0.0)
                {
                    yLower ??= Fill(n, double.NegativeInfinity);
                    yLower[i] = 0.0;
                }
                else if (!double.IsNegativeInfinity(l))
                {
                    double[] row = Fill(n, l / budget); // l kappa - y_i <= 0
                    row[i] -= 1.0;
                    inRows.Add(row);
                }
                if (!double.IsPositiveInfinity(u))
                {
                    double[] row = Fill(n, -u / budget); // y_i - u kappa <= 0
                    row[i] += 1.0;
                    inRows.Add(row);
                }
            }
            foreach (LinearConstraint lc in Constraints.Linear)
            {
                if (lc.IsEquality) { eqRows.Add(Homogenise(lc.Coefficients, lc.Lower, budget, 1.0)); eqRhs.Add(0.0); continue; }
                if (!double.IsPositiveInfinity(lc.Upper)) inRows.Add(Homogenise(lc.Coefficients, lc.Upper, budget, 1.0));
                if (!double.IsNegativeInfinity(lc.Lower)) inRows.Add(Homogenise(lc.Coefficients, lc.Lower, budget, -1.0));
            }
            var program = new QuadraticProgram(sigma, new double[n])
            {
                EqualityMatrix = ToMatrix(eqRows, n),
                EqualityVector = eqRhs.ToArray(),
                InequalityMatrix = ToMatrix(inRows, n),
                InequalityVector = new double[inRows.Count],
                LowerBounds = yLower,
            };
            QuadraticProgramResult qp = QuadraticProgramSolver.Solve(program, options);
            if (qp.Status == QuadraticProgramStatus.Infeasible)
                return new MeanVarianceResult
                {
                    Status = MeanVarianceStatus.Infeasible,
                    Weights = new double[n],
                    Iterations = qp.Iterations,
                    Solution = qp,
                    Message = "No feasible portfolio has an expected return above the risk-free rate (or the constraints are infeasible).",
                };
            if (!qp.IsOptimal) return Result(qp, null, double.NaN, qp.Iterations) with { Weights = new double[n] };

            double kappa = 0.0, norm = 0.0;
            foreach (double y in qp.X) { kappa += y; norm += Math.Abs(y); }
            kappa /= budget;
            if (!(kappa > 1e-12 * norm))
                return new MeanVarianceResult
                {
                    Status = MeanVarianceStatus.Unbounded,
                    Weights = new double[n],
                    Iterations = qp.Iterations,
                    Solution = qp,
                    Message = "The Sharpe ratio is approached only by a zero-cost (infinitely levered) position.",
                };
            var w = new double[n];
            for (int i = 0; i < n; i++) w[i] = qp.X[i] / kappa;
            return Describe(MeanVarianceStatus.Optimal, w, null, double.NaN, qp.Iterations, qp, null);
        }

        /// <summary>
        /// Samples <paramref name="points"/> portfolios of the efficient frontier at target returns equally spaced
        /// between the minimum-variance and the maximum-return portfolios (both included).
        /// </summary>
        public EfficientFrontier EfficientFrontier(int points)
        {
            if (points < 2) throw new ArgumentOutOfRangeException(nameof(points), "At least two points are required.");
            MeanVarianceResult first = MinimumVariance();
            if (first.Status != MeanVarianceStatus.Optimal)
                return new EfficientFrontier { Status = first.Status, Message = first.Message ?? "The minimum-variance portfolio could not be computed." };
            MeanVarianceResult last = MaximumReturn();
            if (last.Status != MeanVarianceStatus.Optimal)
                return new EfficientFrontier { Status = last.Status, Message = last.Message ?? "The maximum-return portfolio could not be computed." };

            double r0 = first.ExpectedReturn, r1 = last.ExpectedReturn;
            var list = new List<MeanVarianceResult>(points);
            if (!(r1 > r0 + 1e-14 * Math.Max(1.0, Math.Abs(r0))))
            {
                list.Add(first); // every feasible portfolio has the same efficient return
            }
            else
            {
                list.Add(first);
                for (int k = 1; k < points - 1; k++)
                {
                    double target = r0 + (r1 - r0) * k / (points - 1);
                    MeanVarianceResult point = MinimumVarianceForTargetReturn(target);
                    if (point.Status != MeanVarianceStatus.Optimal)
                        return new EfficientFrontier { Status = point.Status, Message = point.Message ?? $"The frontier point at return {target} could not be computed." };
                    list.Add(point);
                }
                list.Add(last);
            }
            var returns = new double[list.Count];
            var vols = new double[list.Count];
            for (int k = 0; k < list.Count; k++) { returns[k] = list[k].ExpectedReturn; vols[k] = list[k].Volatility; }
            return new EfficientFrontier { Status = MeanVarianceStatus.Optimal, Points = list, Returns = returns, Volatilities = vols };
        }

        // ---- searches ------------------------------------------------------------------------------------------

        // Maximum (active) return with (active) variance equal to target, walking t = 1 / lambda.
        MeanVarianceResult RiskTarget(double target, double[]? benchmark)
        {
            QuadraticProgramResult lo = Utility(0.0, benchmark);
            int iterations = lo.Iterations;
            if (!lo.IsOptimal) return Result(lo, benchmark, double.PositiveInfinity, iterations);
            double vLo = ActiveVariance(lo.X, benchmark);
            double tol = 1e-12 * target;
            if (vLo >= target - tol)
            {
                MeanVarianceResult minRisk = Result(lo, benchmark, double.PositiveInfinity, iterations);
                return vLo <= target + tol
                    ? minRisk
                    : minRisk with { Status = MeanVarianceStatus.TargetUnattainable, Message = "The minimum-risk portfolio already exceeds the risk target." };
            }

            double tLo = 0.0, t = tScale, tHi = double.NaN;
            QuadraticProgramResult? hi = null;
            for (int k = 0; k <= MaxDecades; k++, t *= 10.0)
            {
                QuadraticProgramResult res = Utility(t, benchmark);
                iterations += res.Iterations;
                if (!res.IsOptimal) return Result(res, benchmark, 1.0 / t, iterations);
                if (ActiveVariance(res.X, benchmark) >= target) { hi = res; tHi = t; break; }
                bool saturated = res.ActiveSetKey == lo.ActiveSetKey && MaxAbsDiff(res.X, lo.X) <= 1e-9 * Math.Max(1.0, MaxAbs(res.X));
                if (saturated && tLo > 0.0)
                    return Result(res, benchmark, 0.0, iterations) with
                    {
                        Status = MeanVarianceStatus.TargetUnattainable,
                        Message = "The maximum-return portfolio does not reach the risk target.",
                    };
                lo = res;
                tLo = t;
            }
            if (hi == null)
                return Result(lo, benchmark, 1.0 / tLo, iterations) with
                {
                    Status = MeanVarianceStatus.TargetUnattainable,
                    Message = "The risk target was not reached by the utility search.",
                };

            QuadraticProgramResult best = hi;
            double tBest = tHi;
            for (int k = 0; k < MaxBisections; k++)
            {
                double tNext = double.NaN;
                if (lo.ActiveSetKey == hi.ActiveSetKey) tNext = InterpolateOnPiece(lo, tLo, hi, tHi, target, benchmark);
                if (double.IsNaN(tNext)) tNext = tLo > 0.0 ? Math.Sqrt(tLo * tHi) : 0.5 * tHi;
                QuadraticProgramResult mid = Utility(tNext, benchmark);
                iterations += mid.Iterations;
                if (!mid.IsOptimal) return Result(mid, benchmark, 1.0 / tNext, iterations);
                double v = ActiveVariance(mid.X, benchmark);
                if (Math.Abs(v - target) <= tol) { best = mid; tBest = tNext; break; }
                if (v < target) { lo = mid; tLo = tNext; } else { hi = mid; tHi = tNext; }
                best = Math.Abs(ActiveVariance(lo.X, benchmark) - target) < Math.Abs(ActiveVariance(hi.X, benchmark) - target) ? lo : hi;
                tBest = ReferenceEquals(best, lo) ? tLo : tHi;
                if (tHi - tLo <= 1e-15 * tHi) break;
            }
            return Result(best, benchmark, 1.0 / tBest, iterations);
        }

        // On one piece of the path w(t) is affine, so the variance is a quadratic in t: solve it for the target.
        double InterpolateOnPiece(QuadraticProgramResult lo, double tLo, QuadraticProgramResult hi, double tHi, double target, double[]? benchmark)
        {
            var a0 = new double[n];
            var dw = new double[n];
            for (int i = 0; i < n; i++)
            {
                a0[i] = lo.X[i] - (benchmark?[i] ?? 0.0);
                dw[i] = (hi.X[i] - lo.X[i]) / (tHi - tLo);
            }
            double[] sd = Multiply(sigma, dw);
            double alpha = Dot(dw, sd), beta = Dot(a0, sd), gamma = ActiveVariance(lo.X, benchmark) - target;
            if (!(alpha > 0.0)) return double.NaN;
            double disc = beta * beta - alpha * gamma;
            if (disc < 0.0) return double.NaN;
            // Root of alpha s^2 + 2 beta s + gamma = 0 with s >= 0 (gamma < 0), in the cancellation-free form.
            double s = beta >= 0.0 ? -gamma / (beta + Math.Sqrt(disc)) : (-beta + Math.Sqrt(disc)) / alpha;
            double t = tLo + s;
            return t > tLo && t < tHi ? t : double.NaN;
        }

        // ---- quadratic programmes ------------------------------------------------------------------------------

        // min 1/2 a^T Sigma a - t mu^T a over the feasible set, a = w - b.
        QuadraticProgramResult Utility(double t, double[]? benchmark)
        {
            double[] sb = benchmark == null ? new double[n] : Multiply(sigma, benchmark);
            var c = new double[n];
            for (int i = 0; i < n; i++) c[i] = -(sb[i] + t * mu[i]);
            return Solve(sigma, c, null, 0.0);
        }

        QuadraticProgramResult Solve(double[,] q, double[] c, double[]? extraEquality, double extraRhs)
        {
            double[,]? a = eqMatrix;
            double[]? b = eqVector;
            if (extraEquality != null)
            {
                int m = eqVector?.Length ?? 0;
                a = new double[m + 1, n];
                b = new double[m + 1];
                for (int i = 0; i < m; i++)
                {
                    for (int k = 0; k < n; k++) a[i, k] = eqMatrix![i, k];
                    b[i] = eqVector![i];
                }
                for (int k = 0; k < n; k++) a[m, k] = extraEquality[k];
                b[m] = extraRhs;
            }
            var program = new QuadraticProgram(q, c)
            {
                EqualityMatrix = a,
                EqualityVector = b,
                InequalityMatrix = ineqMatrix,
                InequalityVector = ineqVector,
                LowerBounds = lower,
                UpperBounds = upper,
            };
            return QuadraticProgramSolver.Solve(program, options);
        }

        MeanVarianceResult Result(QuadraticProgramResult qp, double[]? benchmark, double riskAversion, int iterations)
        {
            MeanVarianceStatus status = qp.Status switch
            {
                QuadraticProgramStatus.Optimal => MeanVarianceStatus.Optimal,
                QuadraticProgramStatus.Infeasible => MeanVarianceStatus.Infeasible,
                QuadraticProgramStatus.NotConvex => MeanVarianceStatus.NotConvex,
                _ => MeanVarianceStatus.MaxIterationsReached,
            };
            string? message = status switch
            {
                MeanVarianceStatus.Infeasible => "The constraints admit no portfolio.",
                MeanVarianceStatus.NotConvex => "The covariance is not positive definite and regularisation is disabled.",
                MeanVarianceStatus.MaxIterationsReached => "The quadratic-programming iteration limit was reached.",
                _ => null,
            };
            return Describe(status, qp.X, benchmark, riskAversion, iterations, qp, message);
        }

        MeanVarianceResult Describe(MeanVarianceStatus status, double[] w, double[]? benchmark, double riskAversion, int iterations, QuadraticProgramResult qp, string? message)
        {
            var weights = (double[])w.Clone();
            double activeReturn = double.NaN, te = double.NaN;
            if (benchmark != null)
            {
                activeReturn = 0.0;
                for (int i = 0; i < n; i++) activeReturn += mu[i] * (weights[i] - benchmark[i]);
                te = Math.Sqrt(Math.Max(ActiveVariance(weights, benchmark), 0.0));
            }
            return new MeanVarianceResult
            {
                Status = status,
                Weights = weights,
                ExpectedReturn = Dot(mu, weights),
                Variance = Dot(weights, Multiply(sigma, weights)),
                ActiveReturn = activeReturn,
                TrackingError = te,
                RiskAversion = riskAversion,
                Iterations = iterations,
                Solution = qp,
                Message = message,
            };
        }

        // ---- helpers -------------------------------------------------------------------------------------------

        void CheckBenchmark(double[]? benchmark)
        {
            if (benchmark == null) return;
            if (benchmark.Length != n) throw new ArgumentException($"The benchmark must have {n} weights.", nameof(benchmark));
            foreach (double b in benchmark)
                if (!double.IsFinite(b)) throw new ArgumentException("Benchmark weights must be finite.", nameof(benchmark));
        }

        double ActiveVariance(double[] w, double[]? benchmark)
        {
            var a = new double[n];
            for (int i = 0; i < n; i++) a[i] = w[i] - (benchmark?[i] ?? 0.0);
            return Dot(a, Multiply(sigma, a));
        }

        // sign * (a - (limit / budget) 1): the homogenised form of a^T w <= limit (sign +1) or a^T w >= limit (sign -1).
        static double[] Homogenise(double[] a, double limit, double budget, double sign)
        {
            var row = new double[a.Length];
            for (int i = 0; i < a.Length; i++) row[i] = sign * (a[i] - limit / budget);
            return row;
        }

        static double[] Fill(int n, double value)
        {
            var v = new double[n];
            Array.Fill(v, value);
            return v;
        }

        static double[] Negate(double[] a)
        {
            var v = new double[a.Length];
            for (int i = 0; i < a.Length; i++) v[i] = -a[i];
            return v;
        }

        static double[,] ToMatrix(List<double[]> rows, int n)
        {
            var a = new double[rows.Count, n];
            for (int i = 0; i < rows.Count; i++)
                for (int k = 0; k < n; k++) a[i, k] = rows[i][k];
            return a;
        }

        static double[] Multiply(double[,] a, double[] x)
        {
            int rows = a.GetLength(0), cols = a.GetLength(1);
            var y = new double[rows];
            for (int i = 0; i < rows; i++)
            {
                double s = 0.0;
                for (int k = 0; k < cols; k++) s += a[i, k] * x[k];
                y[i] = s;
            }
            return y;
        }

        static double Dot(double[] a, double[] b)
        {
            double s = 0.0;
            for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }

        static double MaxAbs(double[] a)
        {
            double m = 0.0;
            foreach (double v in a) m = Math.Max(m, Math.Abs(v));
            return m;
        }

        static double MaxAbsDiff(double[] a, double[] b)
        {
            double m = 0.0;
            for (int i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(a[i] - b[i]));
            return m;
        }
    }
}
