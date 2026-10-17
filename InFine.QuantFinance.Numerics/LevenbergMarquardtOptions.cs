using System;

namespace InFine.QuantFinance.Numerics
{
    /// <summary>Residual function of a least-squares problem: writes <c>r(p)</c> into <paramref name="residuals"/>.</summary>
    /// <param name="parameters">The parameters <c>p</c> (length n). A scratch array: do not keep or modify it.</param>
    /// <param name="residuals">Output, length m.</param>
    public delegate void ResidualFunction(double[] parameters, double[] residuals);

    /// <summary>Analytic Jacobian of a <see cref="ResidualFunction"/>: <c>jacobian[i, j] = d r_i / d p_j</c>.</summary>
    /// <param name="parameters">The parameters <c>p</c> (length n). A scratch array: do not keep or modify it.</param>
    /// <param name="jacobian">Output, m x n.</param>
    public delegate void JacobianFunction(double[] parameters, double[,] jacobian);

    /// <summary>Finite-difference scheme used when no analytic Jacobian is supplied.</summary>
    public enum FiniteDifferenceScheme
    {
        /// <summary>One extra residual evaluation per parameter; error O(h), default step sqrt(eps).</summary>
        Forward,

        /// <summary>Two extra residual evaluations per parameter; error O(h^2), default step eps^(1/3).</summary>
        Central,
    }

    /// <summary>
    /// Settings of <see cref="LevenbergMarquardt.Minimize"/>. The tolerances follow MINPACK's <c>lmder</c>.
    /// </summary>
    public sealed class LevenbergMarquardtOptions
    {
        /// <summary>
        /// ftol: stop when both the actual and the predicted relative reductions of the sum of squares are at most this.
        /// Near a minimum the sum of squares is quadratic in the parameter error, so the parameters are then only
        /// determined to about sqrt(ftol) in relative terms (more on ill-conditioned problems). Default 1e-15, a few
        /// ulps: full precision, termination mostly by xtol, gtol or machine precision; loosen it for speed.
        /// </summary>
        public double FunctionTolerance { get; init; } = 1e-15;

        /// <summary>
        /// xtol: stop when the relative change of the scaled parameters, the trust-region radius over
        /// <c>||D p||</c>, is at most this. Default 1e-12.
        /// </summary>
        public double ParameterTolerance { get; init; } = 1e-12;

        /// <summary>
        /// gtol: stop when the largest cosine between the residual vector and a Jacobian column is at most this
        /// (first-order stationarity, scale free). Default 1e-12.
        /// </summary>
        public double GradientTolerance { get; init; } = 1e-12;

        /// <summary>
        /// Maximum number of iterations (accepted steps; each one costs a Jacobian and one or more trial residual
        /// evaluations). Default 200.
        /// </summary>
        public int MaxIterations { get; init; } = 200;

        /// <summary>
        /// Maximum number of residual evaluations, those spent on finite differences included. Default 10000.
        /// </summary>
        public int MaxFunctionEvaluations { get; init; } = 10000;

        /// <summary>
        /// Initial trust-region radius as a multiple of <c>||D p0||</c> (MINPACK's <c>factor</c>; the radius is this
        /// factor itself when <c>D p0 = 0</c>). Default 100.
        /// </summary>
        public double InitialStepBound { get; init; } = 100.0;

        /// <summary>
        /// Fixed positive scale <c>D</c> of the parameters (MINPACK mode 2). When null (default) the scale
        /// is adaptive: the largest Jacobian column norm seen so far (mode 1), which makes the method invariant to
        /// a rescaling of the parameters.
        /// </summary>
        public double[]? ParameterScale { get; init; }

        /// <summary>Scheme for the finite-difference Jacobian (ignored with an analytic Jacobian). Default forward.</summary>
        public FiniteDifferenceScheme FiniteDifference { get; init; } = FiniteDifferenceScheme.Forward;

        /// <summary>
        /// Relative finite-difference step: the parameter <c>p_j</c> is bumped by <c>step |p_j|</c>, or by
        /// <c>step</c> when <c>p_j = 0</c>, towards the inside of the box at a bound. NaN (default) selects sqrt(eps)
        /// for forward and eps^(1/3) for central differences.
        /// </summary>
        public double FiniteDifferenceStep { get; init; } = double.NaN;

        /// <summary>
        /// Lower bounds of the parameters (use <see cref="double.NegativeInfinity"/> for none), or null for none.
        /// See <see cref="LevenbergMarquardt"/> for how bounds are enforced.
        /// </summary>
        public double[]? LowerBounds { get; init; }

        /// <summary>Upper bounds of the parameters (use <see cref="double.PositiveInfinity"/> for none), or null for none.</summary>
        public double[]? UpperBounds { get; init; }
    }

    /// <summary>Why <see cref="LevenbergMarquardt.Minimize"/> stopped.</summary>
    public enum LevenbergMarquardtStatus
    {
        /// <summary>Actual and predicted relative reductions of the sum of squares are at most ftol (MINPACK info 1).</summary>
        FunctionTolerance,

        /// <summary>Relative change of the scaled parameters is at most xtol (info 2).</summary>
        ParameterTolerance,

        /// <summary>Both of the above (info 3).</summary>
        FunctionAndParameterTolerance,

        /// <summary>The residual vector is orthogonal to the Jacobian columns to within gtol (info 4).</summary>
        GradientTolerance,

        /// <summary>
        /// No further reduction is possible in double precision: a tolerance was set below what the arithmetic can
        /// resolve (info 6, 7 or 8). The point is as good as the method can make it.
        /// </summary>
        MachinePrecision,

        /// <summary>The iteration limit was reached before convergence.</summary>
        MaxIterations,

        /// <summary>The residual-evaluation limit was reached before convergence (info 5).</summary>
        MaxFunctionEvaluations,
    }

    /// <summary>Outcome of <see cref="LevenbergMarquardt.Minimize"/>.</summary>
    public sealed class LevenbergMarquardtResult
    {
        internal LevenbergMarquardtResult(double[] parameters, double[] residuals, double[,] jacobian,
            LevenbergMarquardtStatus status, int iterations, int functionEvaluations)
        {
            Parameters = parameters;
            Residuals = residuals;
            Jacobian = jacobian;
            Status = status;
            Iterations = iterations;
            FunctionEvaluations = functionEvaluations;
            double sum = 0.0;
            foreach (double r in residuals) sum += r * r;
            Cost = 0.5 * sum;
        }

        /// <summary>The parameters found (within the bounds).</summary>
        public double[] Parameters { get; }

        /// <summary>Residuals at <see cref="Parameters"/>.</summary>
        public double[] Residuals { get; }

        /// <summary>Half the sum of squared residuals at <see cref="Parameters"/>.</summary>
        public double Cost { get; }

        /// <summary>
        /// Jacobian <c>d r_i / d p_j</c> at <see cref="Parameters"/>: analytic if supplied, else by the configured
        /// finite differences (one-sided inwards at a bound).
        /// With s^2 = 2 Cost / (m - n) the covariance estimate is <c>s^2 (J^T J)^-1</c>, meaningful for parameters
        /// not held at a bound.
        /// </summary>
        public double[,] Jacobian { get; }

        /// <summary>Why the minimisation stopped.</summary>
        public LevenbergMarquardtStatus Status { get; }

        /// <summary>True unless an iteration or evaluation limit was hit.</summary>
        public bool Converged => Status != LevenbergMarquardtStatus.MaxIterations
            && Status != LevenbergMarquardtStatus.MaxFunctionEvaluations;

        /// <summary>Number of iterations: accepted steps.</summary>
        public int Iterations { get; }

        /// <summary>Number of residual evaluations, finite differences included.</summary>
        public int FunctionEvaluations { get; }
    }
}
