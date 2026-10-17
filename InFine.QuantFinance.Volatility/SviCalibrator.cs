using System;
using System.Collections.Generic;
using InFine.QuantFinance.Numerics;

namespace InFine.QuantFinance.Volatility
{
    /// <summary>Settings of <see cref="SviCalibrator.Calibrate"/>.</summary>
    public sealed class SviCalibrationOptions
    {
        /// <summary>Quote weighting. Default <see cref="SmileWeighting.AsGiven"/>.</summary>
        public SmileWeighting Weighting { get; init; } = SmileWeighting.AsGiven;

        /// <summary>
        /// Upper bound on both asymptotic wing slopes <c>b (1 +- rho)</c> of the total variance. Default 2, Lee's
        /// moment bound, which every arbitrage-free slice satisfies.
        /// </summary>
        public double MaxWingSlope { get; init; } = 2.0;

        /// <summary>Lower bound on the SVI <c>sigma</c>, keeping the smile smooth at its vertex. Default 1e-4.</summary>
        public double MinimumSigma { get; init; } = 1e-4;
    }

    /// <summary>Outcome of <see cref="SviCalibrator.Calibrate"/>.</summary>
    public sealed class SviCalibrationResult
    {
        internal SviCalibrationResult(SviRawParameters parameters, double maturity, double forward, double rmse,
            double maxVolError, double cost, LevenbergMarquardtStatus status, int iterations)
        {
            Parameters = parameters; Maturity = maturity; Forward = forward;
            RootMeanSquareTotalVarianceError = rmse; MaxAbsoluteVolatilityError = maxVolError;
            WeightedCost = cost; Status = status; Iterations = iterations;
        }

        /// <summary>The fitted slice.</summary>
        public SviRawParameters Parameters { get; }

        /// <summary>Maturity of the quotes.</summary>
        public double Maturity { get; }

        /// <summary>Forward of the quotes.</summary>
        public double Forward { get; }

        /// <summary>Unweighted root-mean-square error in total variance over all quotes.</summary>
        public double RootMeanSquareTotalVarianceError { get; }

        /// <summary>Largest absolute implied-volatility error over all quotes.</summary>
        public double MaxAbsoluteVolatilityError { get; }

        /// <summary>Half the weighted sum of squared total-variance errors (the minimised objective).</summary>
        public double WeightedCost { get; }

        /// <summary>Why the final Levenberg-Marquardt polish stopped.</summary>
        public LevenbergMarquardtStatus Status { get; }

        /// <summary>True unless the polish hit an iteration or evaluation limit.</summary>
        public bool Converged => Status != LevenbergMarquardtStatus.MaxIterations
                                 && Status != LevenbergMarquardtStatus.MaxFunctionEvaluations;

        /// <summary>Accepted Levenberg-Marquardt steps of the polish that produced the result.</summary>
        public int Iterations { get; }
    }

    /// <summary>
    /// Fits a raw SVI slice to one expiry by weighted least squares in total variance,
    /// <c>min sum_i omega_i (w(k_i) - sigma_i^2 T)^2</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two stages. <b>Start:</b> the quasi-explicit reduction of De Marco and Martini (Zeliade white paper, 2009). With
    /// <c>y = (k - m) / sigma</c>, the slice is linear in <c>(a, d, c) = (a, rho b sigma, b sigma)</c>:
    /// <c>w = a + d y + c sqrt(y^2 + 1)</c>. For fixed <c>(m, sigma)</c> the weighted linear least-squares problem is
    /// solved exactly under the linear constraints <c>|d| &lt;= c</c> (<c>b &gt;= 0</c>, <c>|rho| &lt;= 1</c>) and
    /// <c>c + |d| &lt;= S sigma</c> (wing slopes at most <c>S</c>): the unconstrained solution if feasible, otherwise
    /// the best of the four edges of the feasible diamond (a convex quadratic, so this is the global constrained
    /// minimum). The outer problem in <c>(m, sigma)</c> is scanned on a grid; De Marco-Martini bound the slopes by
    /// 4, we use Lee's sharper 2 by default, and we leave <c>a</c> free (they impose <c>a &gt;= 0</c>, which excludes
    /// valid slices); the minimum-variance constraint is left to the polish.
    /// </para>
    /// <para>
    /// <b>Polish:</b> the three best grid starts are refined by the bounded Levenberg-Marquardt solver of
    /// <c>InFine.QuantFinance.Numerics</c> over all five parameters, in a reparametrisation where every SVI
    /// constraint is a box: the minimum total variance <c>v = a + b sigma sqrt(1 - rho^2) &gt;= 0</c>, the wing slopes
    /// <c>s_L = b (1 - rho)</c> and <c>s_R = b (1 + rho)</c> in <c>(0, S]</c> (so <c>b &gt;= 0</c>, <c>|rho| &lt; 1</c>
    /// and Lee's bound), <c>m</c> free and <c>sigma &gt;= MinimumSigma</c>. Back to raw:
    /// <c>b = (s_L + s_R) / 2</c>, <c>rho = (s_R - s_L) / (s_R + s_L)</c>, <c>a = v - sigma sqrt(s_L s_R)</c>. The
    /// lowest cost wins.
    /// </para>
    /// <para>
    /// Rejected: a direct 5-parameter fit from a heuristic start, which lands in local minima when <c>m</c> or
    /// <c>sigma</c> start far off; the grid of exact inner solves makes the start global in <c>(m, sigma)</c>.
    /// The fit does not impose absence of butterfly arbitrage (no closed-form condition exists for raw SVI); check
    /// the result with <see cref="ArbitrageChecks.CheckButterfly{TSlice}"/>, or use SSVI.
    /// </para>
    /// </remarks>
    public static class SviCalibrator
    {
        const int MGridSize = 25, SigmaGridSize = 20, Starts = 3;
        const double SlopeFloor = 1e-10;

        /// <summary>Fits a raw SVI slice to <paramref name="quotes"/>.</summary>
        /// <exception cref="ArgumentException">Fewer than five quotes with a positive weight, or invalid options.</exception>
        public static SviCalibrationResult Calibrate(SmileQuotes quotes, SviCalibrationOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(quotes);
            options ??= new SviCalibrationOptions();
            double maxSlope = options.MaxWingSlope, minSigma = options.MinimumSigma;
            if (!(maxSlope > 0.0) || double.IsInfinity(maxSlope))
                throw new ArgumentException("MaxWingSlope must be finite and positive.", nameof(options));
            if (!(minSigma > 0.0) || double.IsInfinity(minSigma))
                throw new ArgumentException("MinimumSigma must be finite and positive.", nameof(options));

            double[] weights = quotes.EffectiveWeights(options.Weighting);
            int n = quotes.Count, active = 0;
            var k = new double[n];
            var w = new double[n];
            double kMin = double.PositiveInfinity, kMax = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                k[i] = quotes.LogMoneyness(i);
                w[i] = quotes.TotalVariance(i);
                if (weights[i] > 0.0)
                {
                    active++;
                    kMin = Math.Min(kMin, k[i]);
                    kMax = Math.Max(kMax, k[i]);
                }
            }
            if (active < 5)
                throw new ArgumentException($"SVI has five parameters; {active} quotes have a positive weight.", nameof(quotes));

            // ---- start: quasi-explicit inner solves on an (m, sigma) grid ----------------------------------------
            double span = kMax - kMin;
            double sigmaLow = Math.Max(minSigma, 0.005 * span), sigmaHigh = Math.Max(2.0 * span, 2.0 * sigmaLow);
            var best = new List<(double Cost, double[] Start)>();
            var inner = new InnerProblem(k, w, weights, maxSlope);
            for (int im = 0; im < MGridSize; im++)
            {
                double m = kMin - 0.5 * span + 2.0 * span * im / (MGridSize - 1);
                for (int js = 0; js < SigmaGridSize; js++)
                {
                    double sigma = sigmaLow * Math.Pow(sigmaHigh / sigmaLow, js / (double)(SigmaGridSize - 1));
                    double cost = inner.Solve(m, sigma, out double a, out double d, out double c);
                    if (!double.IsFinite(cost)) continue;
                    if (best.Count == Starts && cost >= best[Starts - 1].Cost) continue;
                    double sL = Math.Clamp((c - d) / sigma, SlopeFloor, maxSlope);
                    double sR = Math.Clamp((c + d) / sigma, SlopeFloor, maxSlope);
                    double v = Math.Max(0.0, a + Math.Sqrt(Math.Max(0.0, c * c - d * d)));
                    best.Add((cost, new[] { v, sL, sR, m, sigma }));
                    best.Sort((x, y) => x.Cost.CompareTo(y.Cost));
                    if (best.Count > Starts) best.RemoveAt(Starts);
                }
            }

            // ---- polish: bounded Levenberg-Marquardt in (v, s_L, s_R, m, sigma) ----------------------------------
            var sqrtWeights = new double[n];
            for (int i = 0; i < n; i++) sqrtWeights[i] = Math.Sqrt(weights[i]);
            ResidualFunction residuals = (x, r) =>
            {
                ToRaw(x, out double a, out double b, out double rho, out double m, out double sigma);
                for (int i = 0; i < n; i++)
                {
                    double y = k[i] - m;
                    r[i] = sqrtWeights[i] * (a + b * (rho * y + Math.Sqrt(y * y + sigma * sigma)) - w[i]);
                }
            };
            var lmOptions = new LevenbergMarquardtOptions
            {
                FiniteDifference = FiniteDifferenceScheme.Central,
                MaxIterations = 500,
                LowerBounds = new[] { 0.0, SlopeFloor, SlopeFloor, double.NegativeInfinity, minSigma },
                UpperBounds = new[] { double.PositiveInfinity, maxSlope, maxSlope, double.PositiveInfinity, double.PositiveInfinity },
            };
            LevenbergMarquardtResult? fit = null;
            foreach (var (_, start) in best)
            {
                var candidate = LevenbergMarquardt.Minimize(residuals, start, n, null, lmOptions);
                if (fit == null || candidate.Cost < fit.Cost) fit = candidate;
            }
            if (fit == null) throw new InvalidOperationException("No finite SVI start was found on the (m, sigma) grid.");

            ToRaw(fit.Parameters, out double fa, out double fb, out double frho, out double fm, out double fsigma);
            var parameters = new SviRawParameters(fa, fb, frho, fm, fsigma);
            double sse = 0.0, maxVolError = 0.0;
            for (int i = 0; i < n; i++)
            {
                double model = parameters.TotalVariance(k[i]);
                sse += (model - w[i]) * (model - w[i]);
                double vol = Math.Sqrt(Math.Max(model, 0.0) / quotes.Maturity);
                maxVolError = Math.Max(maxVolError, Math.Abs(vol - quotes.Volatilities[i]));
            }
            return new SviCalibrationResult(parameters, quotes.Maturity, quotes.Forward, Math.Sqrt(sse / n),
                maxVolError, fit.Cost, fit.Status, fit.Iterations);
        }

        /// <summary>(v, s_L, s_R, m, sigma) to raw (a, b, rho, m, sigma).</summary>
        static void ToRaw(double[] x, out double a, out double b, out double rho, out double m, out double sigma)
        {
            double v = x[0], sL = x[1], sR = x[2];
            m = x[3];
            sigma = x[4];
            b = 0.5 * (sL + sR);
            rho = sL + sR > 0.0 ? (sR - sL) / (sR + sL) : 0.0;
            a = v - sigma * Math.Sqrt(sL * sR);
        }

        /// <summary>
        /// The inner problem of the quasi-explicit method: for fixed (m, sigma), weighted linear least squares in
        /// (a, d, c) on the basis (1, y, sqrt(y^2 + 1)) subject to |d| &lt;= c and c + |d| &lt;= S sigma.
        /// </summary>
        sealed class InnerProblem
        {
            readonly double[] _k, _w, _omega, _y, _z, _u, _t;
            readonly double _maxSlope;

            public InnerProblem(double[] k, double[] w, double[] omega, double maxSlope)
            {
                _k = k; _w = w; _omega = omega; _maxSlope = maxSlope;
                _y = new double[k.Length]; _z = new double[k.Length]; _u = new double[k.Length]; _t = new double[k.Length];
            }

            /// <summary>Returns the weighted sum of squares at the constrained optimum.</summary>
            public double Solve(double m, double sigma, out double a, out double d, out double c)
            {
                int n = _k.Length;
                for (int i = 0; i < n; i++)
                {
                    _y[i] = (_k[i] - m) / sigma;
                    _z[i] = Math.Sqrt(_y[i] * _y[i] + 1.0);
                }
                double top = _maxSlope * sigma, half = 0.5 * top;

                if (Unconstrained(out a, out d, out c) && Math.Abs(d) <= c && c + Math.Abs(d) <= top)
                    return Cost(a, d, c);

                // Boundary of the diamond |d| <= c <= top - |d|: four edges, each a line fit with c in an interval.
                double bestCost = double.PositiveInfinity;
                a = d = c = 0.0;
                for (int edge = 0; edge < 4; edge++)
                {
                    // d = dSlope * c + dOffset on the edge; w - dOffset y = a + c (z + dSlope y).
                    double dSlope, dOffset, lo, hi;
                    switch (edge)
                    {
                        case 0: dSlope = 1.0; dOffset = 0.0; lo = 0.0; hi = half; break;   // d = c
                        case 1: dSlope = -1.0; dOffset = 0.0; lo = 0.0; hi = half; break;  // d = -c
                        case 2: dSlope = -1.0; dOffset = top; lo = half; hi = top; break;  // c + d = top
                        default: dSlope = 1.0; dOffset = -top; lo = half; hi = top; break; // c - d = top
                    }
                    for (int i = 0; i < n; i++)
                    {
                        _u[i] = _z[i] + dSlope * _y[i];
                        _t[i] = _w[i] - dOffset * _y[i];
                    }
                    LineFit(lo, hi, out double ea, out double ec);
                    double ed = dSlope * ec + dOffset;
                    double cost = Cost(ea, ed, ec);
                    if (cost < bestCost) { bestCost = cost; a = ea; d = ed; c = ec; }
                }
                return bestCost;
            }

            bool Unconstrained(out double a, out double d, out double c)
            {
                // Weighted normal equations for the basis (1, y, z).
                double s11 = 0, s1y = 0, s1z = 0, syy = 0, syz = 0, szz = 0, b1 = 0, by = 0, bz = 0;
                for (int i = 0; i < _k.Length; i++)
                {
                    double o = _omega[i], y = _y[i], z = _z[i], w = _w[i];
                    s11 += o; s1y += o * y; s1z += o * z; syy += o * y * y; syz += o * y * z; szz += o * z * z;
                    b1 += o * w; by += o * y * w; bz += o * z * w;
                }
                var m = new[,] { { s11, s1y, s1z }, { s1y, syy, syz }, { s1z, syz, szz } };
                var rhs = new[] { b1, by, bz };
                a = d = c = double.NaN;
                if (!Solve3(m, rhs)) return false;
                a = rhs[0]; d = rhs[1]; c = rhs[2];
                return double.IsFinite(a) && double.IsFinite(d) && double.IsFinite(c);
            }

            /// <summary>min sum omega (a + c u - t)^2 with a free and c in [lo, hi] (exact: convex in c once a is profiled out).</summary>
            void LineFit(double lo, double hi, out double a, out double c)
            {
                double so = 0, su = 0, st = 0;
                for (int i = 0; i < _k.Length; i++) { so += _omega[i]; su += _omega[i] * _u[i]; st += _omega[i] * _t[i]; }
                double uMean = su / so, tMean = st / so, cov = 0, var = 0;
                for (int i = 0; i < _k.Length; i++)
                {
                    double du = _u[i] - uMean;
                    cov += _omega[i] * du * (_t[i] - tMean);
                    var += _omega[i] * du * du;
                }
                c = var > 0.0 ? Math.Clamp(cov / var, lo, hi) : lo;
                a = tMean - c * uMean;
            }

            double Cost(double a, double d, double c)
            {
                double sum = 0.0;
                for (int i = 0; i < _k.Length; i++)
                {
                    double e = a + d * _y[i] + c * _z[i] - _w[i];
                    sum += _omega[i] * e * e;
                }
                return sum;
            }

            /// <summary>Gaussian elimination with partial pivoting; false when (numerically) singular.</summary>
            static bool Solve3(double[,] m, double[] rhs)
            {
                double scale = 0.0;
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) scale = Math.Max(scale, Math.Abs(m[i, j]));
                if (!(scale > 0.0)) return false;
                for (int col = 0; col < 3; col++)
                {
                    int pivot = col;
                    for (int row = col + 1; row < 3; row++)
                        if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col])) pivot = row;
                    if (Math.Abs(m[pivot, col]) <= 1e-13 * scale) return false;
                    if (pivot != col)
                    {
                        for (int j = 0; j < 3; j++) (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);
                        (rhs[col], rhs[pivot]) = (rhs[pivot], rhs[col]);
                    }
                    for (int row = col + 1; row < 3; row++)
                    {
                        double f = m[row, col] / m[col, col];
                        for (int j = col; j < 3; j++) m[row, j] -= f * m[col, j];
                        rhs[row] -= f * rhs[col];
                    }
                }
                for (int row = 2; row >= 0; row--)
                {
                    double s = rhs[row];
                    for (int j = row + 1; j < 3; j++) s -= m[row, j] * rhs[j];
                    rhs[row] = s / m[row, row];
                }
                return true;
            }
        }
    }
}
