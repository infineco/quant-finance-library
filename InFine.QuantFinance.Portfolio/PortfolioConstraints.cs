using System;
using System.Collections.Generic;

namespace InFine.QuantFinance.Portfolio
{
    /// <summary>
    /// A linear constraint <c>Lower &lt;= a^T w &lt;= Upper</c> on the weights. Use infinities for a one-sided
    /// constraint; <c>Lower == Upper</c> makes it an equality. Group / sector caps, country limits, a beta or
    /// duration target are all of this form.
    /// </summary>
    /// <param name="Coefficients">The vector <c>a</c>, one entry per asset.</param>
    /// <param name="Lower">Lower limit, <see cref="double.NegativeInfinity"/> for none.</param>
    /// <param name="Upper">Upper limit, <see cref="double.PositiveInfinity"/> for none.</param>
    /// <param name="Name">Optional label (for reporting).</param>
    public sealed record LinearConstraint(double[] Coefficients, double Lower, double Upper, string? Name = null)
    {
        /// <summary>Total weight of the <paramref name="members"/> between <paramref name="min"/> and <paramref name="max"/>.</summary>
        public static LinearConstraint Group(int assetCount, IEnumerable<int> members, double min, double max, string? name = null)
        {
            if (assetCount <= 0) throw new ArgumentOutOfRangeException(nameof(assetCount));
            ArgumentNullException.ThrowIfNull(members);
            var a = new double[assetCount];
            foreach (int i in members)
            {
                if (i < 0 || i >= assetCount) throw new ArgumentOutOfRangeException(nameof(members), $"Asset index {i} is out of range.");
                a[i] = 1.0;
            }
            return new LinearConstraint(a, min, max, name);
        }

        /// <summary><c>a^T w &lt;= upper</c>.</summary>
        public static LinearConstraint AtMost(double[] coefficients, double upper, string? name = null) =>
            new(coefficients, double.NegativeInfinity, upper, name);

        /// <summary><c>a^T w &gt;= lower</c>.</summary>
        public static LinearConstraint AtLeast(double[] coefficients, double lower, string? name = null) =>
            new(coefficients, lower, double.PositiveInfinity, name);

        /// <summary><c>a^T w = value</c>.</summary>
        public static LinearConstraint EqualTo(double[] coefficients, double value, string? name = null) =>
            new(coefficients, value, value, name);

        /// <summary>True when the two limits coincide.</summary>
        public bool IsEquality => Lower == Upper;
    }

    /// <summary>
    /// The feasible set of a <see cref="MeanVarianceOptimizer"/>: a budget <c>sum_i w_i = Budget</c>, bounds
    /// <c>l_i &lt;= w_i &lt;= u_i</c> and general <see cref="LinearConstraint"/>s. Weights are fractions of capital
    /// (Budget 1, fully invested). Benchmark-relative limits on active weights <c>w - b</c> are bounds shifted by
    /// the benchmark, see <see cref="ActiveBounds"/>.
    /// </summary>
    public sealed record PortfolioConstraints
    {
        /// <summary>Required sum of the weights; null for no budget constraint. Default 1 (fully invested).</summary>
        public double? Budget { get; init; } = 1.0;

        /// <summary>Lower bounds (length n, <see cref="double.NegativeInfinity"/> for none); null for none at all.</summary>
        public double[]? LowerBounds { get; init; }

        /// <summary>Upper bounds (length n, <see cref="double.PositiveInfinity"/> for none); null for none at all.</summary>
        public double[]? UpperBounds { get; init; }

        /// <summary>General linear constraints (group caps and the like). Default none.</summary>
        public IReadOnlyList<LinearConstraint> Linear { get; init; } = Array.Empty<LinearConstraint>();

        /// <summary>Fully invested, no bound: the textbook unconstrained Markowitz problem (short sales allowed).</summary>
        public static PortfolioConstraints FullyInvested { get; } = new PortfolioConstraints();

        /// <summary>Fully invested and long only, each weight in <c>[0, maxWeight]</c>.</summary>
        public static PortfolioConstraints LongOnly(int assetCount, double maxWeight = 1.0)
        {
            if (assetCount <= 0) throw new ArgumentOutOfRangeException(nameof(assetCount));
            if (!(maxWeight > 0.0)) throw new ArgumentOutOfRangeException(nameof(maxWeight));
            var lo = new double[assetCount];
            var hi = new double[assetCount];
            Array.Fill(hi, maxWeight);
            return new PortfolioConstraints { LowerBounds = lo, UpperBounds = hi };
        }

        /// <summary>Fully invested with box bounds.</summary>
        public static PortfolioConstraints Box(double[] lower, double[] upper) =>
            new PortfolioConstraints { LowerBounds = (double[])lower.Clone(), UpperBounds = (double[])upper.Clone() };

        /// <summary>
        /// Fully invested, active weights <c>w_i - b_i</c> within <c>[-maxUnderweight, maxOverweight]</c>, and
        /// optionally long only (<c>w_i &gt;= 0</c>), so the bounds are <c>[max(0, b_i - under), b_i + over]</c>.
        /// </summary>
        public static PortfolioConstraints ActiveBounds(double[] benchmark, double maxUnderweight, double maxOverweight, bool longOnly = true)
        {
            ArgumentNullException.ThrowIfNull(benchmark);
            if (!(maxUnderweight >= 0.0) || !(maxOverweight >= 0.0)) throw new ArgumentOutOfRangeException(nameof(maxUnderweight), "Active limits must be non-negative.");
            int n = benchmark.Length;
            var lo = new double[n];
            var hi = new double[n];
            for (int i = 0; i < n; i++)
            {
                lo[i] = benchmark[i] - maxUnderweight;
                if (longOnly) lo[i] = Math.Max(0.0, lo[i]);
                hi[i] = benchmark[i] + maxOverweight;
            }
            return new PortfolioConstraints { LowerBounds = lo, UpperBounds = hi };
        }

        /// <summary>A copy with <paramref name="constraint"/> appended to <see cref="Linear"/>.</summary>
        public PortfolioConstraints With(LinearConstraint constraint)
        {
            ArgumentNullException.ThrowIfNull(constraint);
            var list = new List<LinearConstraint>(Linear) { constraint };
            return this with { Linear = list };
        }

        internal void Validate(int n)
        {
            if (Budget is double b && !double.IsFinite(b)) throw new ArgumentException("Budget must be finite.");
            if (LowerBounds != null && LowerBounds.Length != n) throw new ArgumentException($"LowerBounds must have length {n}.");
            if (UpperBounds != null && UpperBounds.Length != n) throw new ArgumentException($"UpperBounds must have length {n}.");
            if (LowerBounds != null)
                foreach (double l in LowerBounds)
                    if (double.IsNaN(l) || l == double.PositiveInfinity) throw new ArgumentException("Lower bounds must be numbers below +infinity.");
            if (UpperBounds != null)
                foreach (double u in UpperBounds)
                    if (double.IsNaN(u) || u == double.NegativeInfinity) throw new ArgumentException("Upper bounds must be numbers above -infinity.");
            foreach (LinearConstraint lc in Linear)
            {
                if (lc == null || lc.Coefficients == null) throw new ArgumentException("Linear constraints must not be null.");
                if (lc.Coefficients.Length != n) throw new ArgumentException($"Linear constraint '{lc.Name}' must have {n} coefficients.");
                foreach (double a in lc.Coefficients)
                    if (!double.IsFinite(a)) throw new ArgumentException($"Linear constraint '{lc.Name}' has a non-finite coefficient.");
                if (double.IsNaN(lc.Lower) || double.IsNaN(lc.Upper) || lc.Lower == double.PositiveInfinity || lc.Upper == double.NegativeInfinity)
                    throw new ArgumentException($"Linear constraint '{lc.Name}' has invalid limits.");
            }
        }
    }
}
