using System;
using System.Collections.Generic;
using InFine.QuantFinance.Numerics;

namespace InFine.QuantFinance.TimeSeries
{
    /// <summary>
    /// GARCH(1,1) (Bollerslev, 1986) with normal or Student-t innovations: log-likelihood, constrained maximum-likelihood
    /// estimation, forecasts and simulation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Model: <c>r_t = mu + eps_t</c>, <c>eps_t = sigma_t z_t</c>, <c>sigma^2_t = omega + alpha eps^2_{t-1} + beta sigma^2_{t-1}</c>,
    /// <c>z_t</c> i.i.d. with zero mean and unit variance. The first variance <c>sigma^2_1</c> is a choice
    /// (<see cref="VarianceInitialisation"/>); the likelihood of observation <c>t</c> is the density of <c>eps_t</c> given <c>sigma^2_t</c>:
    /// normal <c>-1/2 (ln 2 pi + ln sigma^2_t + eps^2_t / sigma^2_t)</c>; Student-t with <c>nu</c> degrees of freedom rescaled to unit variance
    /// <c>ln G((nu+1)/2) - ln G(nu/2) - 1/2 ln(pi (nu-2)) - 1/2 ln sigma^2_t - (nu+1)/2 ln(1 + eps^2_t / (sigma^2_t (nu-2)))</c>.
    /// </para>
    /// <para>
    /// <b>Estimation.</b> Constraints <c>omega &gt; 0</c>, <c>alpha, beta &gt;= 0</c>, <c>alpha + beta &lt; 1</c> and <c>nu &gt; 2</c> are removed by a smooth
    /// reparametrisation to R^n, so an unconstrained optimiser can be used and can never leave the feasible set:
    /// <c>omega = v exp(u1)</c> (<c>v</c> = sample variance, which also makes the problem scale free), persistence <c>p = alpha + beta = logistic(u2)</c>,
    /// share <c>s = alpha / p = logistic(u3)</c>, <c>nu = 2 + exp(u4)</c>, <c>mu = mean + sd u0</c>. The negative average log-likelihood is minimised by the
    /// BFGS quasi-Newton method with a strong-Wolfe line search (<see cref="Bfgs"/>). BFGS was preferred to Levenberg-Marquardt (a
    /// likelihood is not a sum of squares) and to Nelder-Mead (slow and unreliable beyond three or four parameters, and no gradient
    /// information for the convergence test); it uses central-difference gradients, which are accurate enough here (a few ulps of a
    /// well-scaled objective) and spare an error-prone analytic derivation. Several starting points are tried and the best optimum kept, since the
    /// likelihood is flat along the <c>alpha + beta</c> ridge. Boundary solutions (<c>alpha = 0</c>, <c>beta = 0</c>, <c>alpha + beta -&gt; 1</c>)
    /// are the limits <c>u -&gt; +/-infinity</c>; the optimiser stops near them and <see cref="GarchFit.AtBoundary"/> reports it.
    /// </para>
    /// <para>
    /// <b>Standard errors</b> come from the central-difference Hessian <c>H</c> of the log-likelihood in the natural parameters:
    /// <c>Cov = (-H)^-1</c>. The robust (sandwich) option adds the Bollerslev-Wooldridge (1992) estimator <c>H^-1 B H^-1</c> with
    /// <c>B</c> the outer product of the per-observation scores, which stays valid when the innovations are not normal.
    /// </para>
    /// <para>
    /// <b>Forecasts.</b> <c>E[sigma^2_{T+h}] = v + (alpha + beta)^(h-1) (sigma^2_{T+1} - v)</c> with <c>v = omega / (1 - alpha - beta)</c>, for either innovation law.
    /// </para>
    /// </remarks>
    public static class Garch11
    {
        const double Ln2Pi = 1.8378770664093454836;
        const double BoundaryTolerance = 1e-6;
        const double MaxLogit = 30.0;

        // ---- likelihood ----------------------------------------------------------------------------------------

        /// <summary>
        /// Total log-likelihood of <paramref name="returns"/> at the given parameters (the sum over observations of the log-density of
        /// <c>eps_t</c> given <c>sigma^2_t</c>).
        /// </summary>
        /// <param name="returns">Periodic returns (log returns are the usual input), finite.</param>
        /// <param name="parameters">Parameters; validated (<c>omega &gt; 0</c>, <c>alpha, beta &gt;= 0</c>, <c>alpha + beta &lt; 1</c>, <c>nu &gt; 2</c> for Student-t).</param>
        /// <param name="distribution">Innovation distribution.</param>
        /// <param name="initialisation">Choice of <c>sigma^2_1</c>.</param>
        public static double LogLikelihood(double[] returns, GarchParameters parameters,
            InnovationDistribution distribution = InnovationDistribution.Normal,
            VarianceInitialisation initialisation = VarianceInitialisation.SampleVariance)
        {
            RequireReturns(returns, 2);
            parameters.Validate(distribution);
            return Evaluate(returns, parameters.Mu, parameters.Omega, parameters.Alpha, parameters.Beta, parameters.Nu,
                distribution == InnovationDistribution.StudentT, initialisation, null, null);
        }

        /// <summary>
        /// Conditional variances <c>sigma^2_1 .. sigma^2_{T+1}</c> (length <c>T + 1</c>; the last entry is the one-step-ahead forecast
        /// <c>sigma^2_{T+1}</c>) filtered through <paramref name="returns"/> at the given parameters.
        /// </summary>
        public static double[] ConditionalVariances(double[] returns, GarchParameters parameters,
            VarianceInitialisation initialisation = VarianceInitialisation.SampleVariance)
        {
            RequireReturns(returns, 2);
            parameters.Validate(InnovationDistribution.Normal);
            var variances = new double[returns.Length + 1];
            Evaluate(returns, parameters.Mu, parameters.Omega, parameters.Alpha, parameters.Beta, double.PositiveInfinity, false,
                initialisation, variances, null);
            return variances;
        }

        static double InitialVariance(double[] r, double mu, VarianceInitialisation init)
        {
            int n = r.Length;
            if (init == VarianceInitialisation.Backcast)
            {
                int tau = Math.Min(75, n);
                double sum = 0.0, wsum = 0.0, w = 1.0;
                for (int i = 0; i < tau; i++)
                {
                    double e = r[i] - mu;
                    sum += w * e * e;
                    wsum += w;
                    w *= 0.94;
                }
                return sum / wsum;
            }
            double s = 0.0;
            for (int i = 0; i < n; i++) { double e = r[i] - mu; s += e * e; }
            return s / n;
        }

        // Log-likelihood without validating the parameters (ω > 0, α, β >= 0, ν > 2 assumed); alpha + beta >= 1 is allowed, which the
        // numerical Hessian needs next to the stationarity boundary. Fills the optional variance path (length T + 1) and per-observation terms.
        static double Evaluate(double[] r, double mu, double omega, double alpha, double beta, double nu, bool student,
            VarianceInitialisation init, double[]? variances, double[]? perObservation)
        {
            if (!(omega > 0.0) || !(alpha >= 0.0) || !(beta >= 0.0) || (student && !(nu > 2.0))) return double.NegativeInfinity;
            int n = r.Length;
            double h = InitialVariance(r, mu, init);
            if (!(h > 0.0) || double.IsInfinity(h)) return double.NegativeInfinity;
            double constant = student
                ? SpecialFunctions.LogGamma(0.5 * (nu + 1.0)) - SpecialFunctions.LogGamma(0.5 * nu) - 0.5 * Math.Log(Math.PI * (nu - 2.0))
                : -0.5 * Ln2Pi;
            double halfNuPlus1 = 0.5 * (nu + 1.0), nuMinus2 = nu - 2.0;
            double total = 0.0;
            for (int t = 0; t < n; t++)
            {
                if (variances != null) variances[t] = h;
                double e = r[t] - mu, e2 = e * e;
                double ll = student
                    ? constant - 0.5 * Math.Log(h) - halfNuPlus1 * Math.Log(1.0 + e2 / (h * nuMinus2))
                    : constant - 0.5 * (Math.Log(h) + e2 / h);
                if (perObservation != null) perObservation[t] = ll;
                total += ll;
                h = omega + alpha * e2 + beta * h;
                if (!(h > 0.0) || double.IsInfinity(h)) return double.NegativeInfinity;
            }
            if (variances != null) variances[n] = h;
            return double.IsNaN(total) ? double.NegativeInfinity : total;
        }

        // ---- estimation ----------------------------------------------------------------------------------------

        /// <summary>Which natural parameters are free, and their positions in the unconstrained vector.</summary>
        sealed class Layout
        {
            public bool Mu, Omega, Student;
            public int IMu = -1, IOmega = -1, IP, IS, INu = -1, Count;
            public double MeanStart, MeanScale, VarianceScale;

            public Layout(GarchOptions o, double meanStart, double meanScale, double varianceScale)
            {
                Mu = o.EstimateMean;
                Omega = !o.VarianceTargeting;
                Student = o.Distribution == InnovationDistribution.StudentT;
                int i = 0;
                if (Mu) IMu = i++;
                if (Omega) IOmega = i++;
                IP = i++;
                IS = i++;
                if (Student) INu = i++;
                Count = i;
                MeanStart = meanStart; MeanScale = meanScale; VarianceScale = varianceScale;
            }

            public GarchParameters ToParameters(double[] u)
            {
                double mu = Mu ? MeanStart + MeanScale * u[IMu] : 0.0;
                double p = Logistic(Math.Clamp(u[IP], -MaxLogit, MaxLogit));
                double s = Logistic(Math.Clamp(u[IS], -MaxLogit, MaxLogit));
                double alpha = p * s, beta = p * (1.0 - s);
                double omega = Omega ? VarianceScale * Math.Exp(Math.Clamp(u[IOmega], -700.0, 700.0)) : (1.0 - alpha - beta) * VarianceScale;
                double nu = Student ? 2.0 + Math.Exp(Math.Clamp(u[INu], -10.0, 9.0)) : double.PositiveInfinity;
                return new GarchParameters(mu, omega, alpha, beta, nu);
            }

            public double[] ToUnconstrained(GarchParameters g)
            {
                var u = new double[Count];
                if (Mu) u[IMu] = (g.Mu - MeanStart) / MeanScale;
                double p = Math.Clamp(g.Alpha + g.Beta, 1e-8, 1.0 - 1e-8);
                double s = Math.Clamp(g.Alpha / Math.Max(g.Alpha + g.Beta, 1e-300), 1e-6, 1.0 - 1e-6);
                if (Omega) u[IOmega] = Math.Log(g.Omega / VarianceScale);
                u[IP] = Math.Log(p / (1.0 - p));
                u[IS] = Math.Log(s / (1.0 - s));
                if (Student) u[INu] = Math.Log(Math.Max(g.Nu - 2.0, 1e-4));
                return u;
            }
        }

        static double Logistic(double x) => 1.0 / (1.0 + Math.Exp(-x));

        /// <summary>
        /// Maximum-likelihood fit of a GARCH(1,1) to a return series; see the class remarks for the method.
        /// </summary>
        /// <param name="returns">Periodic returns (at least 20), finite and not all equal.</param>
        /// <param name="options">Settings; defaults (normal innovations, estimated mean, sample-variance start) when null.</param>
        public static GarchFit Fit(double[] returns, GarchOptions? options = null)
        {
            options ??= new GarchOptions();
            RequireReturns(returns, 20);
            int n = returns.Length;
            bool student = options.Distribution == InnovationDistribution.StudentT;

            double mean = 0.0;
            foreach (double r in returns) mean += r;
            mean /= n;
            double m0 = options.EstimateMean ? mean : 0.0, v0 = 0.0;
            foreach (double r in returns) v0 += (r - m0) * (r - m0);
            v0 /= n;
            if (!(v0 > 0.0)) throw new ArgumentException("The return series has zero variance.", nameof(returns));
            double s0 = Math.Sqrt(v0);
            var layout = new Layout(options, m0, s0, v0);

            double Objective(double[] u)
            {
                var g = layout.ToParameters(u);
                double ll = Evaluate(returns, g.Mu, g.Omega, g.Alpha, g.Beta, g.Nu, student, options.Initialisation, null, null);
                return double.IsFinite(ll) ? -ll / n : double.PositiveInfinity;
            }

            var starts = new List<GarchParameters>();
            if (options.StartingValues is { } given)
            {
                given.Validate(options.Distribution == InnovationDistribution.StudentT ? InnovationDistribution.StudentT : InnovationDistribution.Normal);
                starts.Add(given);
            }
            else
            {
                double nu0 = student ? 8.0 : double.PositiveInfinity;
                foreach (var (a, b) in new[] { (0.05, 0.90), (0.10, 0.70), (0.03, 0.96) })
                    starts.Add(new GarchParameters(m0, (1.0 - a - b) * v0, a, b, nu0));
            }

            var bfgsOptions = new BfgsOptions { MaxIterations = options.MaxIterations, GradientTolerance = 1e-7 };
            BfgsResult? best = null;
            foreach (var start in starts)
            {
                var u0 = layout.ToUnconstrained(start);
                if (!double.IsFinite(Objective(u0))) continue;
                var result = Bfgs.Minimize(Objective, u0, bfgsOptions);
                if (best == null || result.Value < best.Value) best = result;
            }
            if (best == null) throw new InvalidOperationException("The log-likelihood is not finite at any starting point.");

            var fitted = layout.ToParameters(best.X);
            var variances = new double[n + 1];
            double logLik = Evaluate(returns, fitted.Mu, fitted.Omega, fitted.Alpha, fitted.Beta, fitted.Nu, student, options.Initialisation, variances, null);
            var conditional = new double[n];
            var standardised = new double[n];
            Array.Copy(variances, conditional, n);
            for (int t = 0; t < n; t++) standardised[t] = (returns[t] - fitted.Mu) / Math.Sqrt(conditional[t]);

            bool atBoundary = fitted.Alpha < BoundaryTolerance || fitted.Beta < BoundaryTolerance || fitted.Persistence > 1.0 - BoundaryTolerance;

            // ---- standard errors in scaled natural coordinates z = [mu / sd, omega / v, alpha, beta, nu] (free ones only) ----
            var names = new List<string>();
            var scale = new List<double>();
            var centre = new List<double>();
            if (layout.Mu) { names.Add("mu"); scale.Add(s0); centre.Add(fitted.Mu); }
            if (layout.Omega) { names.Add("omega"); scale.Add(v0); centre.Add(fitted.Omega); }
            names.Add("alpha"); scale.Add(1.0); centre.Add(fitted.Alpha);
            names.Add("beta"); scale.Add(1.0); centre.Add(fitted.Beta);
            if (student) { names.Add("nu"); scale.Add(1.0); centre.Add(fitted.Nu); }
            int k = names.Count;

            GarchParameters FromNatural(double[] z)
            {
                int i = 0;
                double mu = layout.Mu ? z[i++] * s0 : 0.0;
                double omega = layout.Omega ? z[i++] * v0 : double.NaN;
                double alpha = z[i++], beta = z[i++];
                double nu = student ? z[i] : double.PositiveInfinity;
                if (!layout.Omega) omega = (1.0 - alpha - beta) * v0;
                return new GarchParameters(mu, omega, alpha, beta, nu);
            }

            double LogLikAt(double[] z, double[]? perObservation = null)
            {
                var g = FromNatural(z);
                return Evaluate(returns, g.Mu, g.Omega, g.Alpha, g.Beta, g.Nu, student, options.Initialisation, null, perObservation);
            }

            var z0 = new double[k];
            for (int i = 0; i < k; i++) z0[i] = centre[i] / scale[i];

            double[,]? covariance = null;
            double[,]? robustCovariance = null;
            bool hessianOk = false;
            if (!atBoundary)
            {
                foreach (double step in new[] { 1e-3, 1e-4, 1e-5 })
                {
                    var hessian = FiniteDifferences.Hessian(z => LogLikAt(z), z0, step, 0.01);
                    var negative = new double[k, k];
                    bool finite = true;
                    for (int i = 0; i < k; i++)
                        for (int j = 0; j < k; j++) { negative[i, j] = -hessian[i, j]; finite &= double.IsFinite(hessian[i, j]); }
                    if (!finite || !TryInvert(negative, out var inverse)) continue;
                    hessianOk = true;
                    covariance = Unscale(inverse, scale);
                    if (options.RobustStandardErrors) robustCovariance = Unscale(Sandwich(inverse, negative, z0, LogLikAt, n), scale);
                    break;
                }
            }

            var se = StandardErrors(covariance, layout, v0, student, k);
            GarchParameters? robustSe = robustCovariance != null ? StandardErrors(robustCovariance, layout, v0, student, k) : null;

            return new GarchFit(fitted, se, robustSe, logLik, n, k, options.Distribution, conditional, standardised, variances[n],
                InitialVariance(returns, fitted.Mu, options.Initialisation), names.ToArray(), covariance, best.Converged, best.Iterations,
                atBoundary, hessianOk);
        }

        // Bollerslev-Wooldridge: V = A^-1 B A^-1, A = -Hessian, B = sum of outer products of per-observation scores.
        static double[,] Sandwich(double[,] aInverse, double[,] a, double[] z0, Func<double[], double[]?, double> logLik, int n)
        {
            int k = z0.Length;
            var scores = new double[n, k];
            var plus = new double[n];
            var minus = new double[n];
            var w = (double[])z0.Clone();
            for (int i = 0; i < k; i++)
            {
                double h = 1e-4 * Math.Max(Math.Abs(z0[i]), 0.01);
                w[i] = z0[i] + h; logLik(w, plus);
                w[i] = z0[i] - h; logLik(w, minus);
                w[i] = z0[i];
                for (int t = 0; t < n; t++) scores[t, i] = (plus[t] - minus[t]) / (2.0 * h);
            }
            var b = new double[k, k];
            for (int t = 0; t < n; t++)
                for (int i = 0; i < k; i++)
                    for (int j = 0; j < k; j++) b[i, j] += scores[t, i] * scores[t, j];
            var tmp = Matrix.Multiply(aInverse, b);
            return Matrix.Symmetrize(Matrix.Multiply(tmp, aInverse));
        }

        static double[,] Unscale(double[,] cov, List<double> scale)
        {
            int k = scale.Count;
            var result = new double[k, k];
            for (int i = 0; i < k; i++)
                for (int j = 0; j < k; j++) result[i, j] = cov[i, j] * scale[i] * scale[j];
            return result;
        }

        static GarchParameters StandardErrors(double[,]? cov, Layout layout, double v0, bool student, int k)
        {
            if (cov == null) return new GarchParameters(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
            int i = 0;
            double mu = double.NaN;
            if (layout.Mu) { mu = Math.Sqrt(cov[i, i]); i++; }
            double omega = double.NaN;
            if (layout.Omega) { omega = Math.Sqrt(cov[i, i]); i++; }
            int ia = i, ib = i + 1;
            double alpha = Math.Sqrt(cov[ia, ia]), beta = Math.Sqrt(cov[ib, ib]);
            double nu = student ? Math.Sqrt(cov[ib + 1, ib + 1]) : double.NaN;
            // Variance targeting: omega = (1 - alpha - beta) v, delta method with v held fixed.
            if (!layout.Omega) omega = v0 * Math.Sqrt(Math.Max(cov[ia, ia] + cov[ib, ib] + 2.0 * cov[ia, ib], 0.0));
            return new GarchParameters(mu, omega, alpha, beta, nu);
        }

        static bool TryInvert(double[,] a, out double[,] inverse)
        {
            int n = a.GetLength(0);
            inverse = new double[n, n];
            var sym = Matrix.Symmetrize(a);
            if (!Cholesky.TryFactor(sym, out var l)) return false;
            // Solve (L L') x = e_j for every column.
            var y = new double[n];
            for (int c = 0; c < n; c++)
            {
                for (int i = 0; i < n; i++)
                {
                    double s = i == c ? 1.0 : 0.0;
                    for (int j = 0; j < i; j++) s -= l[i, j] * y[j];
                    y[i] = s / l[i, i];
                }
                for (int i = n - 1; i >= 0; i--)
                {
                    double s = y[i];
                    for (int j = i + 1; j < n; j++) s -= l[j, i] * inverse[j, c];
                    inverse[i, c] = s / l[i, i];
                }
            }
            inverse = Matrix.Symmetrize(inverse);
            return true;
        }

        // ---- forecasts -----------------------------------------------------------------------------------------

        /// <summary>
        /// Conditional variance forecasts <c>E[sigma^2_{T+h}]</c> for <c>h = 1 .. horizon</c> (entry <c>h - 1</c>):
        /// <c>v + (alpha + beta)^(h-1) (sigma^2_{T+1} - v)</c> with <c>v = omega / (1 - alpha - beta)</c>. They converge geometrically to <c>v</c>.
        /// </summary>
        /// <param name="parameters">Fitted or assumed parameters.</param>
        /// <param name="nextVariance"><c>sigma^2_{T+1} = omega + alpha eps^2_T + beta sigma^2_T</c>, see <see cref="GarchFit.NextVariance"/>.</param>
        /// <param name="horizon">Number of steps, at least 1.</param>
        public static double[] ForecastVariance(GarchParameters parameters, double nextVariance, int horizon)
        {
            parameters.Validate(InnovationDistribution.Normal);
            RequireForecast(nextVariance, horizon);
            double v = parameters.UnconditionalVariance, p = parameters.Persistence;
            var result = new double[horizon];
            double decay = 1.0;
            for (int h = 0; h < horizon; h++)
            {
                result[h] = v + decay * (nextVariance - v);
                decay *= p;
            }
            return result;
        }

        /// <summary>
        /// Term structure of annualised volatility: entry <c>H - 1</c> is <c>sqrt(periodsPerYear / H sum_{h=1..H} E[sigma^2_{T+h}])</c>, the
        /// annualised volatility expected over the next <c>H</c> periods (so entry 0 is the one-step volatility). The sum has the closed form
        /// <c>H v + (sigma^2_{T+1} - v)(1 - p^H) / (1 - p)</c>. It tends to <c>sqrt(periodsPerYear v)</c> for long horizons.
        /// </summary>
        public static double[] AnnualisedVolatilityTermStructure(GarchParameters parameters, double nextVariance, int maxHorizon, double periodsPerYear)
        {
            parameters.Validate(InnovationDistribution.Normal);
            RequireForecast(nextVariance, maxHorizon);
            if (!(periodsPerYear > 0.0) || double.IsInfinity(periodsPerYear)) throw new ArgumentOutOfRangeException(nameof(periodsPerYear));
            double v = parameters.UnconditionalVariance, p = parameters.Persistence;
            var result = new double[maxHorizon];
            double decay = 1.0, cumulativeExcess = 0.0; // sum_{h=1..H} p^(h-1)
            for (int h = 1; h <= maxHorizon; h++)
            {
                cumulativeExcess += decay;
                decay *= p;
                double total = h * v + (nextVariance - v) * cumulativeExcess;
                result[h - 1] = Math.Sqrt(periodsPerYear * Math.Max(total, 0.0) / h);
            }
            return result;
        }

        // ---- simulation ----------------------------------------------------------------------------------------

        /// <summary>
        /// Simulates a GARCH(1,1) path. Deterministic for a given seed (own xoshiro256** generator, Box-Muller normals, Marsaglia-Tsang gamma
        /// for the Student-t mixing variable <c>z = n sqrt((nu - 2) / chi2_nu)</c>), independent of the .NET version.
        /// </summary>
        /// <param name="parameters">Model parameters (validated; Student-t needs <c>nu &gt; 2</c>).</param>
        /// <param name="count">Number of returns to produce.</param>
        /// <param name="seed">Seed of the generator.</param>
        /// <param name="distribution">Innovation distribution.</param>
        /// <param name="burnIn">Extra leading steps, discarded, so that the path is close to stationary; the recursion starts at the unconditional variance. Default 1000.</param>
        public static GarchSimulation Simulate(GarchParameters parameters, int count, ulong seed,
            InnovationDistribution distribution = InnovationDistribution.Normal, int burnIn = 1000)
        {
            parameters.Validate(distribution);
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            if (burnIn < 0) throw new ArgumentOutOfRangeException(nameof(burnIn));
            var rng = new SeededRandom(seed);
            var returns = new double[count];
            var variances = new double[count];
            double h = parameters.UnconditionalVariance;
            bool student = distribution == InnovationDistribution.StudentT;
            for (int t = -burnIn; t < count; t++)
            {
                double z = rng.NextNormal();
                if (student) z *= Math.Sqrt((parameters.Nu - 2.0) / (2.0 * rng.NextGamma(0.5 * parameters.Nu)));
                double eps = Math.Sqrt(h) * z;
                if (t >= 0) { returns[t] = parameters.Mu + eps; variances[t] = h; }
                h = parameters.Omega + parameters.Alpha * eps * eps + parameters.Beta * h;
            }
            return new GarchSimulation(returns, variances);
        }

        // ---- validation ----------------------------------------------------------------------------------------

        static void RequireReturns(double[] returns, int minimum)
        {
            ArgumentNullException.ThrowIfNull(returns);
            if (returns.Length < minimum) throw new ArgumentException($"At least {minimum} returns are needed.", nameof(returns));
            foreach (double r in returns) if (!double.IsFinite(r)) throw new ArgumentException("Returns must be finite.", nameof(returns));
        }

        static void RequireForecast(double nextVariance, int horizon)
        {
            if (!(nextVariance > 0.0) || double.IsInfinity(nextVariance)) throw new ArgumentOutOfRangeException(nameof(nextVariance), "The next-step variance must be positive and finite.");
            if (horizon < 1) throw new ArgumentOutOfRangeException(nameof(horizon), "The horizon must be at least 1.");
        }
    }
}
