using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Random
{
    /// <summary>
    /// Brownian bridge construction of a standard Brownian motion W on an arbitrary increasing time grid
    /// t_0 &lt; t_1 &lt; ... &lt; t_(n-1) (W(origin) = 0), from n independent standard normals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gaussian z_0 sets the terminal value, W(t_(n-1)) = sqrt(t_(n-1) - origin) z_0; then each step j fills the
    /// middle point m (by index) of an interval (l, r) whose end values are already known:
    /// W(t_m) = w_l W(t_l) + w_r W(t_r) + sigma_j z_j, with
    /// w_l = (t_r - t_m) / (t_r - t_l), w_r = (t_m - t_l) / (t_r - t_l),
    /// sigma_j = sqrt((t_m - t_l)(t_r - t_m) / (t_r - t_l)) (the conditional law of W(t_m) given W(t_l), W(t_r)).
    /// The left end of the first intervals is the origin (index -1, W = 0). Intervals are bisected breadth first,
    /// so z_0 carries the whole terminal variance, z_1 the mid-point, z_2 and z_3 the quarter points, and so on:
    /// the first Gaussians, i.e. the first (best equidistributed) Sobol dimensions, explain most of the variance
    /// of the path. This is what makes Sobol + bridge effective for path-dependent payoffs.
    /// </para>
    /// <para>
    /// The construction is precomputed on the host in flat arrays (<see cref="BridgeIndex"/>, <see cref="LeftIndex"/>,
    /// <see cref="RightIndex"/>, <see cref="LeftWeight"/>, <see cref="RightWeight"/>, <see cref="StdDev"/>);
    /// <see cref="BuildPath(int, int[], int[], int[], double[], double[], double[], double[], int, int, double[], int, int)"/>
    /// is the kernel-compatible apply step (no allocation, no exception, control flow independent of the data).
    /// It reads z_j at <c>z[zOffset + j * zStride]</c> and writes W(t_i) at <c>w[wOffset + i * wStride]</c>,
    /// so it works on path-major and simulation-major (coalesced, <c>simId + simCount * timeIndex</c>) layouts alike.
    /// <see cref="PathToStandardIncrements"/> converts the path in place to (W(t_i) - W(t_(i-1))) / sqrt(t_i - t_(i-1)),
    /// i.i.d. standard normals that can feed any model written for per-step normals (for instance the
    /// <c>IBrownian</c> storage consumed by the diffusions, which multiply by sqrt(dt)).
    /// </para>
    /// <para>
    /// Multi-factor paths (F independent Brownian motions on the same grid): the Gaussian vector of length F n is read
    /// step-major, <c>z[j * F + f]</c> (bridge step j, factor f), so the first F Sobol dimensions drive the F
    /// terminal values, the next F the F mid-points, etc.; the output is factor-major, W_f(t_i) at
    /// <c>w[f * n + i]</c> (each factor a contiguous path). See <see cref="BuildPaths"/>. Correlation is applied
    /// afterwards (e.g. Cholesky on the increments): the bridge is the same linear map for every factor and
    /// commutes with mixing the factors, so this is equivalent to bridging correlated Gaussians.
    /// </para>
    /// </remarks>
    public sealed class BrownianBridge
    {
        /// <param name="times">Strictly increasing observation times t_0 .. t_(n-1), all greater than <paramref name="origin"/>.
        /// For a schedule whose first date is the origin (e.g. <c>PrecalculatedSchedule</c>), pass the dates after it.</param>
        /// <param name="origin">Time at which W = 0.</param>
        public BrownianBridge(ReadOnlySpan<double> times, double origin = 0.0)
        {
            int n = times.Length;
            if (n < 1) throw new ArgumentException("At least one time is required.", nameof(times));
            double previous = origin;
            for (int i = 0; i < n; ++i)
            {
                if (!(times[i] > previous))
                    throw new ArgumentException("Times must be finite, strictly increasing and after the origin.", nameof(times));
                previous = times[i];
            }
            if (double.IsInfinity(previous)) throw new ArgumentException("Times must be finite.", nameof(times));

            Size = n;
            Origin = origin;
            Times = times.ToArray();
            BridgeIndex = new int[n];
            LeftIndex = new int[n];
            RightIndex = new int[n];
            LeftWeight = new double[n];
            RightWeight = new double[n];
            StdDev = new double[n];
            InverseSqrtDt = new double[n];
            for (int i = 0; i < n; ++i)
                InverseSqrtDt[i] = 1.0 / Math.Sqrt(Times[i] - (i == 0 ? origin : Times[i - 1]));

            // Step 0: terminal value from the origin.
            BridgeIndex[0] = n - 1;
            LeftIndex[0] = -1;
            RightIndex[0] = -1;
            LeftWeight[0] = 0.0;
            RightWeight[0] = 0.0;
            StdDev[0] = Math.Sqrt(Times[n - 1] - origin);

            // Breadth-first bisection of the open index intervals (l, r); l = -1 stands for the origin.
            int[] queueLeft = new int[2 * n]; // 1 + 2 (n - 1) intervals are enqueued in total
            int[] queueRight = new int[2 * n];
            int head = 0, tail = 0, step = 1;
            queueLeft[tail] = -1;
            queueRight[tail] = n - 1;
            ++tail;
            while (head < tail)
            {
                int l = queueLeft[head], r = queueRight[head];
                ++head;
                if (r - l < 2) continue;
                int m = l + (r - l) / 2;
                double tl = l < 0 ? origin : Times[l];
                double tm = Times[m], tr = Times[r];
                BridgeIndex[step] = m;
                LeftIndex[step] = l;
                RightIndex[step] = r;
                LeftWeight[step] = (tr - tm) / (tr - tl);
                RightWeight[step] = (tm - tl) / (tr - tl);
                StdDev[step] = Math.Sqrt((tm - tl) * (tr - tm) / (tr - tl));
                ++step;
                queueLeft[tail] = l; queueRight[tail] = m; ++tail;
                queueLeft[tail] = m; queueRight[tail] = r; ++tail;
            }
        }

        /// <summary>Number of observation times n (= number of Gaussians per path and factor).</summary>
        public int Size { get; }

        /// <summary>Time at which W = 0.</summary>
        public double Origin { get; }

        /// <summary>Observation times t_0 .. t_(n-1).</summary>
        public double[] Times { get; }

        /// <summary>Step j fills W(t_BridgeIndex[j]).</summary>
        public int[] BridgeIndex { get; }

        /// <summary>Left neighbour of step j (-1 = origin, unused for step 0).</summary>
        public int[] LeftIndex { get; }

        /// <summary>Right neighbour of step j (unused for step 0).</summary>
        public int[] RightIndex { get; }

        /// <summary>Weight of W(t_LeftIndex[j]) in step j.</summary>
        public double[] LeftWeight { get; }

        /// <summary>Weight of W(t_RightIndex[j]) in step j.</summary>
        public double[] RightWeight { get; }

        /// <summary>Conditional standard deviation multiplying z_j in step j.</summary>
        public double[] StdDev { get; }

        /// <summary>1 / sqrt(t_i - t_(i-1)), with t_(-1) = origin.</summary>
        public double[] InverseSqrtDt { get; }

        /// <summary>
        /// Builds W(t_0) .. W(t_(n-1)) from z_0 .. z_(n-1), reading <c>z[zOffset + j * zStride]</c> and writing
        /// <c>w[wOffset + i * wStride]</c>. Kernel-compatible.
        /// </summary>
        [Kernel]
        public static void BuildPath(int size, int[] bridgeIndex, int[] leftIndex, int[] rightIndex,
                                     double[] leftWeight, double[] rightWeight, double[] stdDev,
                                     double[] z, int zOffset, int zStride, double[] w, int wOffset, int wStride)
        {
            w[wOffset + bridgeIndex[0] * wStride] = stdDev[0] * z[zOffset];
            for (int j = 1; j < size; ++j)
            {
                int l = leftIndex[j];
                double wl = l < 0 ? 0.0 : leftWeight[j] * w[wOffset + l * wStride];
                double wr = rightWeight[j] * w[wOffset + rightIndex[j] * wStride];
                w[wOffset + bridgeIndex[j] * wStride] = wl + wr + stdDev[j] * z[zOffset + j * zStride];
            }
        }

        /// <summary>
        /// In place: W(t_i) -&gt; (W(t_i) - W(t_(i-1))) * inverseSqrtDt[i] (W(t_(-1)) = 0), i.e. i.i.d. standard normals.
        /// Kernel-compatible.
        /// </summary>
        [Kernel]
        public static void PathToStandardIncrements(int size, double[] inverseSqrtDt, double[] w, int wOffset, int wStride)
        {
            for (int i = size - 1; i > 0; --i)
            {
                int k = wOffset + i * wStride;
                w[k] = (w[k] - w[k - wStride]) * inverseSqrtDt[i];
            }
            w[wOffset] = w[wOffset] * inverseSqrtDt[0];
        }

        /// <summary>Host convenience: one path, contiguous z (length n) and w (length n).</summary>
        public void BuildPath(double[] z, double[] w)
        {
            CheckLength(z, Size, nameof(z));
            CheckLength(w, Size, nameof(w));
            BuildPath(Size, BridgeIndex, LeftIndex, RightIndex, LeftWeight, RightWeight, StdDev, z, 0, 1, w, 0, 1);
        }

        /// <summary>
        /// Host convenience for <paramref name="factors"/> independent factors: z is step-major (<c>z[j * F + f]</c>,
        /// length F n), w is factor-major (<c>w[f * n + i]</c>, length F n). See the class remarks.
        /// </summary>
        public void BuildPaths(int factors, double[] z, double[] w)
        {
            if (factors < 1) throw new ArgumentOutOfRangeException(nameof(factors));
            CheckLength(z, factors * Size, nameof(z));
            CheckLength(w, factors * Size, nameof(w));
            for (int f = 0; f < factors; ++f)
                BuildPath(Size, BridgeIndex, LeftIndex, RightIndex, LeftWeight, RightWeight, StdDev, z, f, factors, w, f * Size, 1);
        }

        private static void CheckLength(double[] a, int length, string name)
        {
            if (a == null) throw new ArgumentNullException(name);
            if (a.Length < length) throw new ArgumentException($"Expecting at least {length} values.", name);
        }
    }
}
