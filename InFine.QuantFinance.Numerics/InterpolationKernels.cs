using System;

namespace InFine.QuantFinance.Numerics
{
    /// <summary>
    /// Allocation-free one-dimensional interpolation kernels on plain arrays, shared by the host curves and the device
    /// kernels: no allocation, no exceptions, no LINQ, only <see cref="Math"/>. Callers validate once, up front,
    /// that the <c>n</c> nodes <c>x[0..n-1]</c> are finite and strictly increasing, that <c>n &gt;= 1</c> and
    /// that the arrays hold at least <c>n</c> elements (they may be longer, so buffers can be reused).
    /// <para>
    /// Every kernel evaluates on the segment <c>[x[i], x[i+1]]</c> found by <see cref="FindSegment"/>; outside
    /// <c>[x[0], x[n-1]]</c> the polynomial of the end segment is continued. Callers wanting another extrapolation
    /// (the curves use flat forwards) must not call the kernels there. With a single node the interpolant is the
    /// constant <c>y[0]</c>.
    /// </para>
    /// </summary>
    public static class InterpolationKernels
    {
        /// <summary>
        /// The index <c>i</c> in <c>[0, n-2]</c> of the segment <c>[x[i], x[i+1])</c> containing <paramref name="value"/>,
        /// clamped to the first segment below <c>x[0]</c> and to the last one from <c>x[n-2]</c> on (so <c>x[n-1]</c> belongs
        /// to the last segment). Zero when <c>n &lt; 2</c>. Binary search, O(log n).
        /// </summary>
        public static int FindSegment(double[] x, int n, double value)
        {
            if (n < 3 || value <= x[0]) return 0;
            if (value >= x[n - 2]) return n - 2;
            // Invariant: x[lo] < value < x[hi] at entry, then x[lo] <= value < x[hi].
            int lo = 0, hi = n - 2;
            while (hi - lo > 1)
            {
                var mid = (lo + hi) >> 1;
                if (x[mid] <= value) lo = mid;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>Piecewise-linear interpolation; exact (bit for bit) at the nodes.</summary>
        public static double LinearValue(double[] x, double[] y, int n, double value)
        {
            if (n < 2) return y[0];
            var i = FindSegment(x, n, value);
            var w = (value - x[i]) / (x[i + 1] - x[i]);
            return (1.0 - w) * y[i] + w * y[i + 1];
        }

        /// <summary>Derivative of <see cref="LinearValue"/>: the secant slope of the segment (right segment at an interior node).</summary>
        public static double LinearDerivative(double[] x, double[] y, int n, double value)
        {
            if (n < 2) return 0.0;
            var i = FindSegment(x, n, value);
            return (y[i + 1] - y[i]) / (x[i + 1] - x[i]);
        }

        /// <summary>
        /// Bessel (parabolic) node slopes: at an interior node the derivative of the parabola through it and its two
        /// neighbours, <c>d_i = (h_i S_{i-1} + h_{i-1} S_i) / (h_{i-1} + h_i)</c> with <c>h</c> the node spacings and
        /// <c>S</c> the secants; at each end the derivative of the parabola through the three end nodes. Second-order
        /// accurate and local (each slope depends on three nodes); exact for quadratic data. With two nodes both slopes
        /// are the secant; with one node the slope is zero. Writes <c>slopes[0..n-1]</c>.
        /// </summary>
        public static void BesselSlopes(double[] x, double[] y, int n, double[] slopes)
        {
            if (n < 2)
            {
                slopes[0] = 0.0;
                return;
            }
            if (n == 2)
            {
                var s = (y[1] - y[0]) / (x[1] - x[0]);
                slopes[0] = s;
                slopes[1] = s;
                return;
            }
            for (var i = 1; i < n - 1; i++)
            {
                var hl = x[i] - x[i - 1];
                var hr = x[i + 1] - x[i];
                var sl = (y[i] - y[i - 1]) / hl;
                var sr = (y[i + 1] - y[i]) / hr;
                slopes[i] = (hr * sl + hl * sr) / (hl + hr);
            }
            var h0 = x[1] - x[0];
            var h1 = x[2] - x[1];
            var s0 = (y[1] - y[0]) / h0;
            var s1 = (y[2] - y[1]) / h1;
            slopes[0] = ((2.0 * h0 + h1) * s0 - h0 * s1) / (h0 + h1);
            var hn = x[n - 1] - x[n - 2];
            var hm = x[n - 2] - x[n - 3];
            var sn = (y[n - 1] - y[n - 2]) / hn;
            var sm = (y[n - 2] - y[n - 3]) / hm;
            slopes[n - 1] = ((2.0 * hn + hm) * sn - hn * sm) / (hn + hm);
        }

        /// <summary>
        /// The Hyman (1983) monotonicity filter, applied in place to node slopes of a cubic Hermite interpolant. With
        /// <c>S_{i-1}</c>, <c>S_i</c> the secants on each side of node <c>i</c>:
        /// <list type="bullet">
        /// <item>interior node where both secants have the same sign <c>s</c>:
        /// <c>d_i = s * min(max(0, s d_i), 3 min(|S_{i-1}|, |S_i|))</c>;</item>
        /// <item>interior node at a local extremum or a flat side of the data (<c>S_{i-1} S_i &lt;= 0</c>): <c>d_i = 0</c>;</item>
        /// <item>end node: <c>d</c> is set to zero if its sign differs from the end secant's (or that secant is zero), and
        /// capped at three times the end secant in magnitude.</item>
        /// </list>
        /// Afterwards, on every segment the ratios <c>d_i / S_i</c> and <c>d_{i+1} / S_i</c> lie in <c>[0, 3]</c>, the
        /// Fritsch-Carlson (1980) sufficient condition for a monotone cubic: the interpolant is monotone on each segment
        /// (so never leaves the range of its two end values) and monotone overall on monotone data. Slopes that already
        /// satisfy the condition are left unchanged, which keeps the accuracy of the underlying slopes on smooth data.
        /// </summary>
        public static void HymanFilter(double[] x, double[] y, int n, double[] slopes)
        {
            if (n < 2)
            {
                slopes[0] = 0.0;
                return;
            }
            for (var i = 0; i < n; i++)
            {
                double secant, bound;
                if (i == 0 || i == n - 1)
                {
                    var k = i == 0 ? 0 : n - 2;
                    secant = (y[k + 1] - y[k]) / (x[k + 1] - x[k]);
                    bound = 3.0 * Math.Abs(secant);
                }
                else
                {
                    var sl = (y[i] - y[i - 1]) / (x[i] - x[i - 1]);
                    secant = (y[i + 1] - y[i]) / (x[i + 1] - x[i]);
                    if (!SameSign(sl, secant))
                    {
                        slopes[i] = 0.0;
                        continue;
                    }
                    bound = 3.0 * Math.Min(Math.Abs(sl), Math.Abs(secant));
                }
                if (!SameSign(slopes[i], secant))
                    slopes[i] = 0.0;
                else if (Math.Abs(slopes[i]) > bound)
                    slopes[i] = secant > 0.0 ? bound : -bound;
            }
        }

        /// <summary>
        /// Slopes of the monotonicity-preserving cubic used by the library: <see cref="BesselSlopes"/> followed by
        /// <see cref="HymanFilter"/>. Writes <c>slopes[0..n-1]</c>.
        /// </summary>
        public static void MonotoneCubicSlopes(double[] x, double[] y, int n, double[] slopes)
        {
            BesselSlopes(x, y, n, slopes);
            HymanFilter(x, y, n, slopes);
        }

        /// <summary>
        /// The cubic Hermite interpolant with node values <paramref name="y"/> and node slopes <paramref name="slopes"/>:
        /// on <c>[x_i, x_{i+1}]</c>, with <c>h = x_{i+1} - x_i</c> and <c>s = (value - x_i) / h</c>,
        /// <c>p = (2s^3 - 3s^2 + 1) y_i + (s^3 - 2s^2 + s) h d_i + (-2s^3 + 3s^2) y_{i+1} + (s^3 - s^2) h d_{i+1}</c>.
        /// Exact at the nodes; continuously differentiable.
        /// </summary>
        public static double HermiteValue(double[] x, double[] y, double[] slopes, int n, double value)
        {
            if (n < 2) return y[0];
            var i = FindSegment(x, n, value);
            var h = x[i + 1] - x[i];
            var s = (value - x[i]) / h;
            var s2 = s * s;
            var s3 = s2 * s;
            return (2.0 * s3 - 3.0 * s2 + 1.0) * y[i]
                + (s3 - 2.0 * s2 + s) * h * slopes[i]
                + (3.0 * s2 - 2.0 * s3) * y[i + 1]
                + (s3 - s2) * h * slopes[i + 1];
        }

        /// <summary>Derivative of <see cref="HermiteValue"/> with respect to <paramref name="value"/>.</summary>
        public static double HermiteDerivative(double[] x, double[] y, double[] slopes, int n, double value)
        {
            if (n < 2) return 0.0;
            var i = FindSegment(x, n, value);
            var h = x[i + 1] - x[i];
            var s = (value - x[i]) / h;
            var s2 = s * s;
            return (6.0 * (s2 - s) * (y[i] - y[i + 1])) / h
                + (3.0 * s2 - 4.0 * s + 1.0) * slopes[i]
                + (3.0 * s2 - 2.0 * s) * slopes[i + 1];
        }

        private static bool SameSign(double a, double b) => (a > 0.0 && b > 0.0) || (a < 0.0 && b < 0.0);
    }
}
