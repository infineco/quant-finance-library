using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Numerics;

namespace InFine.QuantFinance.Models.Concepts
{
    /// <summary>
    /// Piecewise linear interpolation with flat extrapolation, on increasing abscissae.
    /// Allocation free and exception free so that it can be called from kernels; it only uses plain arrays. Inside the
    /// pillar range it is <see cref="InterpolationKernels.LinearValue"/>, shared with the host curves.
    /// </summary>
    public static class LinearInterpolation
    {
        /// <summary>
        /// Interpolates linearly the points (xs[i], ys[i]) for i &lt; count. Outside [xs[0], xs[count-1]] the value of the
        /// nearest end point is returned (flat extrapolation). Returns 0 when count is not positive.
        /// </summary>
        /// <param name="xs">Increasing abscissae (pillar dates).</param>
        /// <param name="ys">Ordinates (pillar values).</param>
        /// <param name="count">Number of points to use in xs and ys.</param>
        /// <param name="x">Evaluation point.</param>
        [Kernel]
        public static double Interpolate(double[] xs, double[] ys, int count, double x)
        {
            if (count <= 0) { return 0.0; }
            if (x <= xs[0]) { return ys[0]; }
            if (x >= xs[count - 1]) { return ys[count - 1]; }
            return InterpolationKernels.LinearValue(xs, ys, count, x);
        }
    }
}
