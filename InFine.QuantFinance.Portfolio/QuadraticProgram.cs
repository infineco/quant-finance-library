using System;

namespace InFine.QuantFinance.Portfolio
{
    /// <summary>
    /// A dense convex quadratic programme
    /// <code>
    ///   minimise   1/2 x^T Q x + c^T x
    ///   subject to A x  = b        (equalities, usually few)
    ///              G x &lt;= h        (general linear inequalities)
    ///              l &lt;= x &lt;= u      (bounds; infinite entries are ignored)
    /// </code>
    /// solved by <see cref="QuadraticProgramSolver"/>. Every optional block may be null. Only the symmetric part of
    /// <see cref="Q"/> matters; it must be positive definite, or positive semi-definite with
    /// <see cref="QuadraticProgramOptions.RegularizationFloor"/> enabled (see there).
    /// </summary>
    /// <param name="Q">Hessian, n x n, symmetric positive (semi-)definite.</param>
    /// <param name="C">Linear term, length n.</param>
    public sealed record QuadraticProgram(double[,] Q, double[] C)
    {
        /// <summary>Equality matrix <c>A</c>, m x n, or null.</summary>
        public double[,]? EqualityMatrix { get; init; }

        /// <summary>Equality right-hand side <c>b</c>, length m, or null.</summary>
        public double[]? EqualityVector { get; init; }

        /// <summary>Inequality matrix <c>G</c> of <c>G x &lt;= h</c>, p x n, or null.</summary>
        public double[,]? InequalityMatrix { get; init; }

        /// <summary>Inequality right-hand side <c>h</c>, length p, or null.</summary>
        public double[]? InequalityVector { get; init; }

        /// <summary>Lower bounds <c>l</c> (length n, <see cref="double.NegativeInfinity"/> for none), or null.</summary>
        public double[]? LowerBounds { get; init; }

        /// <summary>Upper bounds <c>u</c> (length n, <see cref="double.PositiveInfinity"/> for none), or null.</summary>
        public double[]? UpperBounds { get; init; }

        /// <summary>Number of variables n.</summary>
        public int Dimension => C.Length;

        /// <summary>Objective <c>1/2 x^T Q x + c^T x</c> at <paramref name="x"/> (symmetric part of Q).</summary>
        public double Objective(double[] x)
        {
            ArgumentNullException.ThrowIfNull(x);
            int n = C.Length;
            if (x.Length != n) throw new ArgumentException($"Expected {n} variables, got {x.Length}.", nameof(x));
            double quad = 0.0, lin = 0.0;
            for (int i = 0; i < n; i++)
            {
                lin += C[i] * x[i];
                for (int j = 0; j < n; j++) quad += x[i] * Q[i, j] * x[j];
            }
            return 0.5 * quad + lin;
        }
    }

    /// <summary>Settings of <see cref="QuadraticProgramSolver.Solve"/>.</summary>
    public sealed record QuadraticProgramOptions
    {
        /// <summary>
        /// A constraint is violated when its slack is below <c>-FeasibilityTolerance * max(1, |rhs|, sum_j |a_j x_j|)</c>
        /// (relative to the size of the terms it adds up). Default 1e-12.
        /// </summary>
        public double FeasibilityTolerance { get; init; } = 1e-12;

        /// <summary>
        /// <c>Q</c> is treated as numerically positive definite when its Cholesky factorisation succeeds with every
        /// squared pivot above <c>PositiveDefiniteTolerance * max_i Q_ii</c>. Default 1e-12.
        /// </summary>
        public double PositiveDefiniteTolerance { get; init; } = 1e-12;

        /// <summary>
        /// The dual active-set method needs a strictly convex objective. When <c>Q</c> is only semi-definite (a sample
        /// covariance with more assets than observations, a rank-deficient factor model) the solver adds the ridge
        /// <c>delta I</c> with <c>delta = RegularizationFloor * max_i Q_ii</c> (times 10 until the test above passes,
        /// at most 6 times) and reports <c>delta</c> in <see cref="QuadraticProgramResult.Regularization"/>. The
        /// result is then the exact solution of the ridged programme: among the near-ties of the original one it
        /// picks the one of smallest norm. Set to 0 to refuse such input
        /// (<see cref="QuadraticProgramStatus.NotConvex"/>). Default 1e-10. A shrinkage estimator such as
        /// Ledoit-Wolf is a better cure than a ridge for an ill-conditioned covariance.
        /// </summary>
        public double RegularizationFloor { get; init; } = 1e-10;

        /// <summary>Maximum number of active-set changes; 0 (default) selects <c>max(1000, 20 (n + constraints))</c>.</summary>
        public int MaxIterations { get; init; }
    }

    /// <summary>Outcome of a quadratic programme.</summary>
    public enum QuadraticProgramStatus
    {
        /// <summary>The KKT conditions hold: <see cref="QuadraticProgramResult.X"/> is the unique minimiser.</summary>
        Optimal,

        /// <summary>The constraints have no common point (detected by the dual method as an unbounded dual).</summary>
        Infeasible,

        /// <summary><c>Q</c> is not positive definite and regularisation is disabled, or <c>Q</c> is zero / indefinite.</summary>
        NotConvex,

        /// <summary>The iteration limit was hit; the returned point is the last iterate (dual feasible, not primal).</summary>
        MaxIterationsReached,
    }

    /// <summary>
    /// Solution of a <see cref="QuadraticProgram"/>. Multipliers follow the Lagrangian
    /// <c>L = 1/2 x^T Q x + c^T x + y^T (A x - b) + z^T (G x - h) - zl^T (x - l) + zu^T (x - u)</c>, so that at the
    /// optimum <c>Q x + c + A^T y + G^T z - zl + zu = 0</c> with <c>z, zl, zu &gt;= 0</c> and complementary slackness.
    /// </summary>
    public sealed record QuadraticProgramResult
    {
        /// <summary>Termination status.</summary>
        public QuadraticProgramStatus Status { get; init; }

        /// <summary>The solution (the last iterate when not <see cref="QuadraticProgramStatus.Optimal"/>).</summary>
        public double[] X { get; init; } = Array.Empty<double>();

        /// <summary>Objective <c>1/2 x^T Q x + c^T x</c> of the input programme (without any ridge).</summary>
        public double Objective { get; init; }

        /// <summary>Multipliers <c>y</c> of the equalities (free sign); 0 for an equality found redundant.</summary>
        public double[] EqualityMultipliers { get; init; } = Array.Empty<double>();

        /// <summary>Multipliers <c>z &gt;= 0</c> of <c>G x &lt;= h</c>.</summary>
        public double[] InequalityMultipliers { get; init; } = Array.Empty<double>();

        /// <summary>Multipliers <c>zl &gt;= 0</c> of the lower bounds.</summary>
        public double[] LowerBoundMultipliers { get; init; } = Array.Empty<double>();

        /// <summary>Multipliers <c>zu &gt;= 0</c> of the upper bounds.</summary>
        public double[] UpperBoundMultipliers { get; init; } = Array.Empty<double>();

        /// <summary>Indices of the inequality rows of <c>G</c> in the final active set.</summary>
        public int[] ActiveInequalities { get; init; } = Array.Empty<int>();

        /// <summary>Indices of the variables at an active lower bound in the final active set.</summary>
        public int[] ActiveLowerBounds { get; init; } = Array.Empty<int>();

        /// <summary>Indices of the variables at an active upper bound in the final active set.</summary>
        public int[] ActiveUpperBounds { get; init; } = Array.Empty<int>();

        /// <summary>Number of active-set changes (constraint additions and deletions).</summary>
        public int Iterations { get; init; }

        /// <summary>Ridge <c>delta</c> added to the diagonal of <c>Q</c>, 0 when none was needed.</summary>
        public double Regularization { get; init; }

        /// <summary>True when <see cref="Status"/> is <see cref="QuadraticProgramStatus.Optimal"/>.</summary>
        public bool IsOptimal => Status == QuadraticProgramStatus.Optimal;

        /// <summary>A canonical key of the final active set (used to detect pieces of a parametric solution path).</summary>
        internal string ActiveSetKey =>
            string.Join(",", ActiveInequalities) + "|" + string.Join(",", ActiveLowerBounds) + "|" + string.Join(",", ActiveUpperBounds);
    }
}
