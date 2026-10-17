// Ported from MINPACK (lmder, lmpar, qrfac, qrsolv). Original licence, reproduced as required:
//
// Minpack Copyright Notice (1999) University of Chicago.  All rights reserved
//
// Redistribution and use in source and binary forms, with or
// without modification, are permitted provided that the
// following conditions are met:
//
// 1. Redistributions of source code must retain the above
// copyright notice, this list of conditions and the following
// disclaimer.
//
// 2. Redistributions in binary form must reproduce the above
// copyright notice, this list of conditions and the following
// disclaimer in the documentation and/or other materials
// provided with the distribution.
//
// 3. The end-user documentation included with the
// redistribution, if any, must include the following
// acknowledgment:
//
//    "This product includes software developed by the
//    University of Chicago, as Operator of Argonne National
//    Laboratory.
//
// Alternately, this acknowledgment may appear in the software
// itself, if and wherever such third-party acknowledgments
// normally appear.
//
// 4. WARRANTY DISCLAIMER. THE SOFTWARE IS SUPPLIED "AS IS"
// WITHOUT WARRANTY OF ANY KIND. THE COPYRIGHT HOLDER, THE
// UNITED STATES, THE UNITED STATES DEPARTMENT OF ENERGY, AND
// THEIR EMPLOYEES: (1) DISCLAIM ANY WARRANTIES, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO ANY IMPLIED WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, TITLE
// OR NON-INFRINGEMENT, (2) DO NOT ASSUME ANY LEGAL LIABILITY
// OR RESPONSIBILITY FOR THE ACCURACY, COMPLETENESS, OR
// USEFULNESS OF THE SOFTWARE, (3) DO NOT REPRESENT THAT USE OF
// THE SOFTWARE WOULD NOT INFRINGE PRIVATELY OWNED RIGHTS, (4)
// DO NOT WARRANT THAT THE SOFTWARE WILL FUNCTION
// UNINTERRUPTED, THAT IT IS ERROR-FREE OR THAT ANY ERRORS WILL
// BE CORRECTED.
//
// 5. LIMITATION OF LIABILITY. IN NO EVENT WILL THE COPYRIGHT
// HOLDER, THE UNITED STATES, THE UNITED STATES DEPARTMENT OF
// ENERGY, OR THEIR EMPLOYEES: BE LIABLE FOR ANY INDIRECT,
// INCIDENTAL, CONSEQUENTIAL, SPECIAL OR PUNITIVE DAMAGES OF
// ANY KIND OR NATURE, INCLUDING BUT NOT LIMITED TO LOSS OF
// PROFITS OR LOSS OF DATA, FOR ANY REASON WHATSOEVER, WHETHER
// SUCH LIABILITY IS ASSERTED ON THE BASIS OF CONTRACT, TORT
// (INCLUDING NEGLIGENCE OR STRICT LIABILITY), OR OTHERWISE,
// EVEN IF ANY OF SAID PARTIES HAS BEEN WARNED OF THE
// POSSIBILITY OF SUCH LOSS OR DAMAGES.

using System;

namespace InFine.QuantFinance.Numerics
{
    /// <summary>
    /// Nonlinear least squares, <c>min_p 1/2 sum_i r_i(p)^2</c> subject to optional bounds <c>l &lt;= p &lt;= u</c>, by
    /// the Levenberg-Marquardt method in Moré's trust-region formulation (J. J. Moré, "The Levenberg-Marquardt
    /// algorithm: implementation and theory", Lecture Notes in Mathematics 630, 1978), ported from MINPACK's
    /// <c>lmder</c>, <c>lmpar</c>, <c>qrfac</c> and <c>qrsolv</c> (Moré, Garbow and Hillstrom, Argonne National
    /// Laboratory, 1980). Host-side: it allocates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each iteration factorises the Jacobian <c>J P = Q R</c> (Householder QR with column pivoting) and solves the
    /// damped problem <c>min ||J d + r||</c> subject to <c>||D d|| &lt;= delta</c>; the Levenberg-Marquardt parameter
    /// is the Lagrange multiplier of that constraint, found by Moré's safeguarded Newton iteration, and the radius
    /// <c>delta</c> follows the ratio of actual to predicted reduction. <c>D</c> is the scaling: by default the
    /// running maximum of the Jacobian column norms, which makes the iteration invariant to a diagonal rescaling of
    /// the parameters. The Jacobian is analytic when supplied, otherwise forward or central finite differences
    /// (stepping inwards at a bound, so the residuals are never evaluated outside the box).
    /// </para>
    /// <para>
    /// Bounds are handled by an active set with projection. At each iteration a parameter that sits on a bound with
    /// the gradient <c>J^T r</c> pushing it outwards is held fixed; the trust-region step is computed for the other
    /// (free) parameters only, and the trial point is projected onto the box. The predicted reduction is that of the
    /// projected step, so the trust-region logic stays consistent. With no bound active this is exactly MINPACK.
    /// Convergence to a solution on a bound is as fast as in the interior (the bound simply removes a
    /// variable), and the gradient test is applied to the free parameters, i.e. it measures the projected gradient.
    /// An initial guess outside the box is projected onto it.
    /// </para>
    /// <para>
    /// A trial point whose residuals are not finite counts as a failed step (the trust region shrinks), so models
    /// may return NaN outside their domain. The residuals at the initial guess and every Jacobian must be finite.
    /// </para>
    /// </remarks>
    public static class LevenbergMarquardt
    {
        const double Epsmch = 2.220446049250313e-16;
        const double Dwarf = 2.2250738585072014e-308;

        /// <summary>
        /// Minimises half the sum of squares of <paramref name="residuals"/> from <paramref name="initialGuess"/>.
        /// </summary>
        /// <param name="residuals">Residual function r(p), m components.</param>
        /// <param name="initialGuess">Starting parameters (length n &gt; 0); not modified.</param>
        /// <param name="residualCount">m, at least n.</param>
        /// <param name="jacobian">Analytic Jacobian, or null for finite differences.</param>
        /// <param name="options">Tolerances, limits, scaling, finite differences and bounds; null for the defaults.</param>
        /// <exception cref="ArgumentException">Inconsistent sizes, bounds or options, a non-finite initial guess, or
        /// non-finite residuals at the initial guess.</exception>
        /// <exception cref="InvalidOperationException">The Jacobian has a non-finite entry.</exception>
        public static LevenbergMarquardtResult Minimize(ResidualFunction residuals, double[] initialGuess,
            int residualCount, JacobianFunction? jacobian = null, LevenbergMarquardtOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(residuals);
            ArgumentNullException.ThrowIfNull(initialGuess);
            options ??= new LevenbergMarquardtOptions();
            var problem = new Problem(residuals, jacobian, initialGuess.Length, residualCount, options);
            return problem.Solve(initialGuess);
        }

        sealed class Problem
        {
            readonly ResidualFunction _residuals;
            readonly JacobianFunction? _jacobian;
            readonly LevenbergMarquardtOptions _options;
            readonly int _n, _m;
            readonly double[] _lower, _upper;
            readonly double[] _scratch;    // parameters handed to the user's functions
            readonly double[] _fdUp, _fdDown;
            readonly double _fdStep;
            int _nfev;

            public Problem(ResidualFunction residuals, JacobianFunction? jacobian, int n, int m, LevenbergMarquardtOptions options)
            {
                if (n < 1) throw new ArgumentException("At least one parameter is required.", "initialGuess");
                if (m < n) throw new ArgumentException($"At least as many residuals as parameters are required ({m} < {n}).", "residualCount");
                if (!(options.FunctionTolerance >= 0.0) || !(options.ParameterTolerance >= 0.0) || !(options.GradientTolerance >= 0.0))
                    throw new ArgumentException("Tolerances must be non-negative.", nameof(options));
                if (options.MaxIterations < 1 || options.MaxFunctionEvaluations < 1)
                    throw new ArgumentException("Iteration and evaluation limits must be positive.", nameof(options));
                if (!(options.InitialStepBound > 0.0) || double.IsInfinity(options.InitialStepBound))
                    throw new ArgumentException("The initial step bound must be finite and positive.", nameof(options));
                if (options.ParameterScale != null)
                {
                    if (options.ParameterScale.Length != n) throw new ArgumentException("ParameterScale must have one entry per parameter.", nameof(options));
                    foreach (double d in options.ParameterScale)
                        if (!(d > 0.0) || double.IsInfinity(d)) throw new ArgumentException("ParameterScale entries must be finite and positive.", nameof(options));
                }
                _fdStep = double.IsNaN(options.FiniteDifferenceStep)
                    ? (options.FiniteDifference == FiniteDifferenceScheme.Central ? Math.Cbrt(Epsmch) : Math.Sqrt(Epsmch))
                    : options.FiniteDifferenceStep;
                if (!(_fdStep > 0.0) || !(_fdStep < 1.0)) throw new ArgumentException("The finite-difference step must be in (0, 1).", nameof(options));

                if (options.LowerBounds != null && options.LowerBounds.Length != n) throw new ArgumentException("LowerBounds must have one entry per parameter.", nameof(options));
                if (options.UpperBounds != null && options.UpperBounds.Length != n) throw new ArgumentException("UpperBounds must have one entry per parameter.", nameof(options));
                _lower = new double[n];
                _upper = new double[n];
                for (int j = 0; j < n; j++)
                {
                    _lower[j] = options.LowerBounds == null ? double.NegativeInfinity : options.LowerBounds[j];
                    _upper[j] = options.UpperBounds == null ? double.PositiveInfinity : options.UpperBounds[j];
                    if (double.IsNaN(_lower[j]) || double.IsNaN(_upper[j]) || !(_lower[j] < _upper[j])
                        || _lower[j] == double.PositiveInfinity || _upper[j] == double.NegativeInfinity)
                        throw new ArgumentException($"Invalid bounds [{_lower[j]}, {_upper[j]}] for parameter {j}.", nameof(options));
                }

                _residuals = residuals;
                _jacobian = jacobian;
                _options = options;
                _n = n;
                _m = m;
                _scratch = new double[n];
                _fdUp = new double[m];
                _fdDown = new double[m];
            }

            public LevenbergMarquardtResult Solve(double[] initialGuess)
            {
                int n = _n, m = _m;
                var x = new double[n];
                for (int j = 0; j < n; j++)
                {
                    if (!double.IsFinite(initialGuess[j])) throw new ArgumentException($"Initial guess {j} is not finite.", nameof(initialGuess));
                    x[j] = Math.Clamp(initialGuess[j], _lower[j], _upper[j]);
                }

                var fvec = new double[m];
                Evaluate(x, fvec);
                foreach (double r in fvec)
                    if (!double.IsFinite(r)) throw new ArgumentException("The residuals at the initial guess are not finite.", nameof(initialGuess));

                var jac = new double[m, n];      // full Jacobian
                var fjac = new double[m, n];     // free columns, then their QR factorisation
                var free = new int[n];           // indices of the free parameters
                var colnorm = new double[n];
                var diag = new double[n];
                var diagFree = new double[n];
                var qtf = new double[n];
                var ipvt = new int[n];
                var wa1 = new double[n];
                var wa2 = new double[n];
                var wa3 = new double[n];
                var step = new double[n];
                var trial = new double[n];
                var wa4 = new double[m];
                bool fixedScale = _options.ParameterScale != null;
                if (fixedScale) Array.Copy(_options.ParameterScale!, diag, n);

                double ftol = _options.FunctionTolerance, xtol = _options.ParameterTolerance, gtol = _options.GradientTolerance;
                double fnorm = Enorm(fvec, m), par = 0.0, delta = 0.0, xnorm = 0.0;
                int iter = 1; // 1 + number of accepted steps, as in MINPACK
                LevenbergMarquardtStatus? status = null;

                while (status == null)
                {
                    Jacobian(x, fvec, jac);

                    // Column norms and active set: a parameter on a bound whose gradient component J^T r pushes it
                    // outwards stays there for this iteration.
                    int nf = 0;
                    for (int j = 0; j < n; j++)
                    {
                        double sumSq = 0.0, g = 0.0;
                        for (int i = 0; i < m; i++)
                        {
                            sumSq += jac[i, j] * jac[i, j];
                            g += jac[i, j] * fvec[i];
                        }
                        colnorm[j] = Math.Sqrt(sumSq);
                        bool active = (x[j] <= _lower[j] && g > 0.0) || (x[j] >= _upper[j] && g < 0.0);
                        if (!active) free[nf++] = j;
                    }

                    if (iter == 1)
                    {
                        if (!fixedScale)
                            for (int j = 0; j < n; j++) diag[j] = colnorm[j] == 0.0 ? 1.0 : colnorm[j];
                        for (int j = 0; j < n; j++) wa3[j] = diag[j] * x[j];
                        xnorm = Enorm(wa3, n);
                        delta = _options.InitialStepBound * xnorm;
                        if (delta == 0.0) delta = _options.InitialStepBound;
                    }
                    if (nf == 0)
                    {
                        // Every parameter is held by a bound: a stationary point of the bounded problem.
                        status = LevenbergMarquardtStatus.GradientTolerance;
                        break;
                    }

                    // QR factorisation of the free columns.
                    for (int k = 0; k < nf; k++)
                        for (int i = 0; i < m; i++) fjac[i, k] = jac[i, free[k]];
                    QrFactor(m, nf, fjac, ipvt, wa1, wa2, wa3);

                    // Q^T fvec; its first nf components in qtf. R's diagonal goes back into fjac.
                    Array.Copy(fvec, wa4, m);
                    for (int j = 0; j < nf; j++)
                    {
                        if (fjac[j, j] != 0.0)
                        {
                            double sum = 0.0;
                            for (int i = j; i < m; i++) sum += fjac[i, j] * wa4[i];
                            double temp = -sum / fjac[j, j];
                            for (int i = j; i < m; i++) wa4[i] += fjac[i, j] * temp;
                        }
                        fjac[j, j] = wa1[j];
                        qtf[j] = wa4[j];
                    }

                    // Norm of the scaled (projected) gradient: largest cosine between fvec and a free column.
                    double gnorm = 0.0;
                    if (fnorm != 0.0)
                    {
                        for (int j = 0; j < nf; j++)
                        {
                            int l = ipvt[j];
                            if (wa2[l] != 0.0)
                            {
                                double sum = 0.0;
                                for (int i = 0; i <= j; i++) sum += fjac[i, j] * (qtf[i] / fnorm);
                                gnorm = Math.Max(gnorm, Math.Abs(sum / wa2[l]));
                            }
                        }
                    }
                    if (gnorm <= gtol) { status = LevenbergMarquardtStatus.GradientTolerance; break; }
                    if (iter - 1 >= _options.MaxIterations) { status = LevenbergMarquardtStatus.MaxIterations; break; }

                    if (!fixedScale)
                        for (int j = 0; j < n; j++) diag[j] = Math.Max(diag[j], colnorm[j]);
                    for (int k = 0; k < nf; k++) diagFree[k] = diag[free[k]];

                    // Inner loop: shrink the trust region until a step is accepted or a test is met.
                    while (true)
                    {
                        par = LmPar(nf, fjac, ipvt, diagFree, qtf, delta, par, wa1, wa2, wa3, wa4);

                        // Trust-region step for the free parameters, projected onto the box.
                        bool projected = false;
                        Array.Copy(x, trial, n);
                        for (int k = 0; k < nf; k++)
                        {
                            int j = free[k];
                            wa1[k] = -wa1[k];
                            wa3[k] = diagFree[k] * wa1[k];
                            double t = Math.Clamp(x[j] + wa1[k], _lower[j], _upper[j]);
                            if (t != x[j] + wa1[k]) projected = true;
                            trial[j] = t;
                            step[k] = t - x[j];
                        }
                        double pnorm = Enorm(wa3, nf);
                        if (iter == 1) delta = Math.Min(delta, pnorm);

                        Evaluate(trial, wa4);
                        double fnorm1 = Enorm(wa4, m); // NaN for a non-finite trial: treated as no reduction

                        double actred = -1.0;
                        if (0.1 * fnorm1 < fnorm) actred = 1.0 - (fnorm1 / fnorm) * (fnorm1 / fnorm);

                        // Predicted reduction and directional derivative of the step actually taken.
                        double prered, dirder;
                        if (!projected)
                        {
                            for (int j = 0; j < nf; j++)
                            {
                                wa3[j] = 0.0;
                                double temp = wa1[ipvt[j]];
                                for (int i = 0; i <= j; i++) wa3[i] += fjac[i, j] * temp;
                            }
                            double temp1 = Enorm(wa3, nf) / fnorm, temp2 = Math.Sqrt(par) * pnorm / fnorm;
                            prered = temp1 * temp1 + temp2 * temp2 / 0.5;
                            dirder = -(temp1 * temp1 + temp2 * temp2);
                        }
                        else
                        {
                            // ||f + J s||^2 = ||qtf + R P^T s||^2 + (rest of Q^T f, unchanged).
                            for (int j = 0; j < nf; j++)
                            {
                                wa3[j] = 0.0;
                                double temp = step[ipvt[j]];
                                for (int i = 0; i <= j; i++) wa3[i] += fjac[i, j] * temp;
                            }
                            double cross = 0.0;
                            for (int j = 0; j < nf; j++) cross += (qtf[j] / fnorm) * (wa3[j] / fnorm);
                            double temp1 = Enorm(wa3, nf) / fnorm;
                            prered = -(2.0 * cross + temp1 * temp1);
                            dirder = cross;
                        }
                        double ratio = prered > 0.0 ? actred / prered : 0.0;

                        // Update the trust-region radius.
                        if (ratio <= 0.25)
                        {
                            double temp = actred >= 0.0 ? 0.5 : 0.5 * dirder / (dirder + 0.5 * actred);
                            if (0.1 * fnorm1 >= fnorm || temp < 0.1 || !(temp <= 0.5) || double.IsNaN(fnorm1)) temp = 0.1;
                            delta = temp * Math.Min(delta, pnorm / 0.1);
                            par /= temp;
                        }
                        else if (par == 0.0 || ratio >= 0.75)
                        {
                            delta = pnorm / 0.5;
                            par *= 0.5;
                        }

                        // Successful iteration: accept the step.
                        if (ratio >= 1e-4)
                        {
                            Array.Copy(trial, x, n);
                            for (int j = 0; j < n; j++) wa2[j] = diag[j] * x[j];
                            Array.Copy(wa4, fvec, m);
                            xnorm = Enorm(wa2, n);
                            fnorm = fnorm1;
                            iter++;
                        }

                        // Convergence tests.
                        bool fConverged = Math.Abs(actred) <= ftol && prered <= ftol && 0.5 * ratio <= 1.0;
                        bool xConverged = delta <= xtol * xnorm;
                        if (fConverged && xConverged) { status = LevenbergMarquardtStatus.FunctionAndParameterTolerance; break; }
                        if (fConverged) { status = LevenbergMarquardtStatus.FunctionTolerance; break; }
                        if (xConverged) { status = LevenbergMarquardtStatus.ParameterTolerance; break; }

                        // Limits and tolerances below machine precision.
                        if (_nfev >= _options.MaxFunctionEvaluations) { status = LevenbergMarquardtStatus.MaxFunctionEvaluations; break; }
                        if ((Math.Abs(actred) <= Epsmch && prered <= Epsmch && 0.5 * ratio <= 1.0)
                            || delta <= Epsmch * xnorm || gnorm <= Epsmch)
                        {
                            status = LevenbergMarquardtStatus.MachinePrecision;
                            break;
                        }
                        // A step was accepted, or the projection may have changed the active set: new Jacobian.
                        if (ratio >= 1e-4) break;
                    }
                }

                var jacobianAtSolution = new double[m, n];
                Jacobian(x, fvec, jacobianAtSolution);
                return new LevenbergMarquardtResult(x, fvec, jacobianAtSolution, status.Value, iter - 1, _nfev);
            }

            void Evaluate(double[] p, double[] r)
            {
                Array.Copy(p, _scratch, _n);
                _residuals(_scratch, r);
                _nfev++;
            }

            /// <summary>Analytic or finite-difference Jacobian at p; differences step inwards at a bound.</summary>
            void Jacobian(double[] p, double[] fvec, double[,] jac)
            {
                int n = _n, m = _m;
                if (_jacobian != null)
                {
                    Array.Copy(p, _scratch, n);
                    _jacobian(_scratch, jac);
                }
                else
                {
                    bool central = _options.FiniteDifference == FiniteDifferenceScheme.Central;
                    var x = (double[])p.Clone();
                    for (int j = 0; j < n; j++)
                    {
                        double pj = p[j];
                        double h = _fdStep * Math.Abs(pj);
                        if (h == 0.0) h = _fdStep;
                        bool canUp = pj + h <= _upper[j], canDown = pj - h >= _lower[j];
                        if (central && canUp && canDown)
                        {
                            x[j] = pj + h;
                            double hu = x[j] - pj;
                            Evaluate(x, _fdUp);
                            x[j] = pj - h;
                            double hd = pj - x[j];
                            Evaluate(x, _fdDown);
                            for (int i = 0; i < m; i++) jac[i, j] = (_fdUp[i] - _fdDown[i]) / (hu + hd);
                        }
                        else
                        {
                            x[j] = canUp || !canDown ? pj + h : pj - h;
                            double hs = x[j] - pj;
                            Evaluate(x, _fdUp);
                            for (int i = 0; i < m; i++) jac[i, j] = (_fdUp[i] - fvec[i]) / hs;
                        }
                        x[j] = pj;
                    }
                }
                for (int i = 0; i < m; i++)
                    for (int j = 0; j < n; j++)
                        if (!double.IsFinite(jac[i, j]))
                            throw new InvalidOperationException($"The Jacobian entry ({i}, {j}) is not finite.");
            }
        }

        // ---- MINPACK building blocks (0-based ports) --------------------------------------------------------------

        /// <summary>Euclidean norm of the first <paramref name="n"/> entries, scaled against overflow and underflow.</summary>
        internal static double Enorm(double[] v, int n)
        {
            double scale = 0.0;
            for (int i = 0; i < n; i++)
            {
                double a = Math.Abs(v[i]);
                if (double.IsNaN(a)) return double.NaN;
                if (a > scale) scale = a;
            }
            if (scale == 0.0 || double.IsInfinity(scale)) return scale;
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                double t = v[i] / scale;
                sum += t * t;
            }
            return scale * Math.Sqrt(sum);
        }

        static double ColumnNorm(double[,] a, int col, int from, int to)
        {
            double scale = 0.0;
            for (int i = from; i < to; i++) scale = Math.Max(scale, Math.Abs(a[i, col]));
            if (scale == 0.0) return 0.0;
            double sum = 0.0;
            for (int i = from; i < to; i++)
            {
                double t = a[i, col] / scale;
                sum += t * t;
            }
            return scale * Math.Sqrt(sum);
        }

        /// <summary>
        /// MINPACK <c>qrfac</c> with pivoting: Householder QR of the m x n matrix <paramref name="a"/> with column
        /// pivoting. On exit the strict upper triangle of the first n rows holds R (diagonal in <paramref name="rdiag"/>),
        /// the lower trapezoid the Householder vectors; <paramref name="acnorm"/> holds the original column norms.
        /// </summary>
        static void QrFactor(int m, int n, double[,] a, int[] ipvt, double[] rdiag, double[] acnorm, double[] wa)
        {
            for (int j = 0; j < n; j++)
            {
                acnorm[j] = ColumnNorm(a, j, 0, m);
                rdiag[j] = acnorm[j];
                wa[j] = rdiag[j];
                ipvt[j] = j;
            }
            int minmn = Math.Min(m, n);
            for (int j = 0; j < minmn; j++)
            {
                // Bring the column of largest remaining norm into the pivot position.
                int kmax = j;
                for (int k = j; k < n; k++) if (rdiag[k] > rdiag[kmax]) kmax = k;
                if (kmax != j)
                {
                    for (int i = 0; i < m; i++)
                    {
                        double t = a[i, j];
                        a[i, j] = a[i, kmax];
                        a[i, kmax] = t;
                    }
                    rdiag[kmax] = rdiag[j];
                    wa[kmax] = wa[j];
                    (ipvt[j], ipvt[kmax]) = (ipvt[kmax], ipvt[j]);
                }

                // Householder transformation reducing column j to a multiple of the j-th unit vector.
                double ajnorm = ColumnNorm(a, j, j, m);
                if (ajnorm != 0.0)
                {
                    if (a[j, j] < 0.0) ajnorm = -ajnorm;
                    for (int i = j; i < m; i++) a[i, j] /= ajnorm;
                    a[j, j] += 1.0;
                    for (int k = j + 1; k < n; k++)
                    {
                        double sum = 0.0;
                        for (int i = j; i < m; i++) sum += a[i, j] * a[i, k];
                        double temp = sum / a[j, j];
                        for (int i = j; i < m; i++) a[i, k] -= temp * a[i, j];
                        if (rdiag[k] != 0.0)
                        {
                            temp = a[j, k] / rdiag[k];
                            rdiag[k] *= Math.Sqrt(Math.Max(0.0, 1.0 - temp * temp));
                            double ratio = rdiag[k] / wa[k];
                            if (0.05 * ratio * ratio <= Epsmch)
                            {
                                rdiag[k] = ColumnNorm(a, k, j + 1, m);
                                wa[k] = rdiag[k];
                            }
                        }
                    }
                }
                rdiag[j] = -ajnorm;
            }
        }

        /// <summary>
        /// MINPACK <c>lmpar</c>: the Levenberg-Marquardt parameter <c>par</c> such that the solution x of
        /// <c>min ||[R; sqrt(par) D P] P^T x - [Q^T f; 0]||</c> satisfies <c>| ||D x|| - delta | &lt;= 0.1 delta</c>
        /// (or par = 0 with <c>||D x|| &lt;= 1.1 delta</c>). Returns par; x receives the step (to be negated).
        /// </summary>
        static double LmPar(int n, double[,] r, int[] ipvt, double[] diag, double[] qtb, double delta, double par,
            double[] x, double[] sdiag, double[] wa1, double[] wa2)
        {
            // Gauss-Newton direction; least-squares solution if R is singular.
            int nsing = n;
            for (int j = 0; j < n; j++)
            {
                wa1[j] = qtb[j];
                if (r[j, j] == 0.0 && nsing == n) nsing = j;
                if (nsing < n) wa1[j] = 0.0;
            }
            for (int k = 0; k < nsing; k++)
            {
                int j = nsing - 1 - k;
                wa1[j] /= r[j, j];
                double temp = wa1[j];
                for (int i = 0; i < j; i++) wa1[i] -= r[i, j] * temp;
            }
            for (int j = 0; j < n; j++) x[ipvt[j]] = wa1[j];

            int iter = 0;
            for (int j = 0; j < n; j++) wa2[j] = diag[j] * x[j];
            double dxnorm = Enorm(wa2, n);
            double fp = dxnorm - delta;
            if (fp <= 0.1 * delta) return 0.0;

            // Lower bound parl (zero if R is singular).
            double parl = 0.0;
            if (nsing >= n)
            {
                for (int j = 0; j < n; j++)
                {
                    int l = ipvt[j];
                    wa1[j] = diag[l] * (wa2[l] / dxnorm);
                }
                for (int j = 0; j < n; j++)
                {
                    double sum = 0.0;
                    for (int i = 0; i < j; i++) sum += r[i, j] * wa1[i];
                    wa1[j] = (wa1[j] - sum) / r[j, j];
                }
                double temp = Enorm(wa1, n);
                parl = ((fp / delta) / temp) / temp;
            }

            // Upper bound paru.
            for (int j = 0; j < n; j++)
            {
                double sum = 0.0;
                for (int i = 0; i <= j; i++) sum += r[i, j] * qtb[i];
                wa1[j] = sum / diag[ipvt[j]];
            }
            double gnorm = Enorm(wa1, n);
            double paru = gnorm / delta;
            if (paru == 0.0) paru = Dwarf / Math.Min(delta, 0.1);

            par = Math.Max(par, parl);
            par = Math.Min(par, paru);
            if (par == 0.0) par = gnorm / dxnorm;

            while (true)
            {
                iter++;
                if (par == 0.0) par = Math.Max(Dwarf, 0.001 * paru);
                double sqrtPar = Math.Sqrt(par);
                for (int j = 0; j < n; j++) wa1[j] = sqrtPar * diag[j];
                QrSolve(n, r, ipvt, wa1, qtb, x, sdiag, wa2);
                for (int j = 0; j < n; j++) wa2[j] = diag[j] * x[j];
                dxnorm = Enorm(wa2, n);
                double temp = fp;
                fp = dxnorm - delta;

                if (Math.Abs(fp) <= 0.1 * delta || (parl == 0.0 && fp <= temp && temp < 0.0) || iter == 10)
                    return par;

                // Newton correction.
                for (int j = 0; j < n; j++)
                {
                    int l = ipvt[j];
                    wa1[j] = diag[l] * (wa2[l] / dxnorm);
                }
                for (int j = 0; j < n; j++)
                {
                    wa1[j] /= sdiag[j];
                    double t = wa1[j];
                    for (int i = j + 1; i < n; i++) wa1[i] -= r[i, j] * t;
                }
                double tn = Enorm(wa1, n);
                double parc = ((fp / delta) / tn) / tn;

                if (fp > 0.0) parl = Math.Max(parl, par);
                if (fp < 0.0) paru = Math.Min(paru, par);
                par = Math.Max(parl, par + parc);
            }
        }

        /// <summary>
        /// MINPACK <c>qrsolv</c>: solves <c>min ||[A; D] x - [b; 0]||</c> given the pivoted QR factorisation of A
        /// (R in the upper triangle of <paramref name="r"/>, Q^T b in <paramref name="qtb"/>), by Givens rotations
        /// that eliminate D. The strict lower triangle of r receives the transformed triangle S, its diagonal
        /// <paramref name="sdiag"/>.
        /// </summary>
        static void QrSolve(int n, double[,] r, int[] ipvt, double[] diag, double[] qtb, double[] x, double[] sdiag, double[] wa)
        {
            for (int j = 0; j < n; j++)
            {
                for (int i = j; i < n; i++) r[i, j] = r[j, i];
                x[j] = r[j, j];
                wa[j] = qtb[j];
            }

            for (int j = 0; j < n; j++)
            {
                int l = ipvt[j];
                if (diag[l] != 0.0)
                {
                    for (int k = j; k < n; k++) sdiag[k] = 0.0;
                    sdiag[j] = diag[l];
                    double qtbpj = 0.0;
                    for (int k = j; k < n; k++)
                    {
                        if (sdiag[k] == 0.0) continue;
                        double sin, cos;
                        if (Math.Abs(r[k, k]) < Math.Abs(sdiag[k]))
                        {
                            double cotan = r[k, k] / sdiag[k];
                            sin = 0.5 / Math.Sqrt(0.25 + 0.25 * cotan * cotan);
                            cos = sin * cotan;
                        }
                        else
                        {
                            double tan = sdiag[k] / r[k, k];
                            cos = 0.5 / Math.Sqrt(0.25 + 0.25 * tan * tan);
                            sin = cos * tan;
                        }
                        r[k, k] = cos * r[k, k] + sin * sdiag[k];
                        double temp = cos * wa[k] + sin * qtbpj;
                        qtbpj = -sin * wa[k] + cos * qtbpj;
                        wa[k] = temp;
                        for (int i = k + 1; i < n; i++)
                        {
                            temp = cos * r[i, k] + sin * sdiag[i];
                            sdiag[i] = -sin * r[i, k] + cos * sdiag[i];
                            r[i, k] = temp;
                        }
                    }
                }
                sdiag[j] = r[j, j];
                r[j, j] = x[j];
            }

            // Triangular solve; least-squares solution if singular.
            int nsing = n;
            for (int j = 0; j < n; j++)
            {
                if (sdiag[j] == 0.0 && nsing == n) nsing = j;
                if (nsing < n) wa[j] = 0.0;
            }
            for (int k = 0; k < nsing; k++)
            {
                int j = nsing - 1 - k;
                double sum = 0.0;
                for (int i = j + 1; i < nsing; i++) sum += r[i, j] * wa[i];
                wa[j] = (wa[j] - sum) / sdiag[j];
            }
            for (int j = 0; j < n; j++) x[ipvt[j]] = wa[j];
        }
    }
}
