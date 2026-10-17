using System;

namespace InFine.QuantFinance.Numerics
{
    /// <summary>
    /// Evaluation of a discount curve stored as flat arrays, under the kernel rules (no allocation, no exceptions,
    /// only <see cref="Math"/>), so that the host and the device read the same curve with the same arithmetic.
    /// <para>
    /// The curve is <c>n</c> nodes <c>(times[i], values[i])</c> of an interpolated quantity, node slopes for the cubic
    /// schemes, and a flat-forward extrapolation beyond the last node. The <c>mode</c> combines the quantity and the
    /// scheme:
    /// </para>
    /// <list type="bullet">
    /// <item>bit 0 clear: <c>values</c> are continuously compounded zero rates <c>z(t) = -ln P(0, t) / t</c> at times
    /// <c>t &gt; 0</c>; before the first node <c>z</c> is held at <c>values[0]</c> (flat forward from zero);</item>
    /// <item>bit 0 set: <c>values</c> are log discount factors <c>ln P(0, t)</c>, normally with a first node <c>(0, 0)</c>;</item>
    /// <item>bit 1 clear: linear interpolation (<see cref="InterpolationKernels.LinearValue"/>); <c>slopes</c> is not read;</item>
    /// <item>bit 1 set: cubic Hermite interpolation with node slopes <c>slopes</c> (<see cref="InterpolationKernels.HermiteValue"/>).</item>
    /// </list>
    /// Beyond the last node time <c>T</c>: <c>ln P(0, t) = lastLogDiscount - lastForward (t - T)</c>.
    /// <para>
    /// These are the conventions of the market model's interpolated discount curve, so a compiled curve reproduces it
    /// bit for bit. Callers validate once, up front, the requirements of <see cref="InterpolationKernels"/>.
    /// </para>
    /// </summary>
    public static class DiscountCurveKernels
    {
        /// <summary>Linear on zero rates.</summary>
        public const int LinearZeroRate = 0;

        /// <summary>Linear on log discount factors: piecewise-constant instantaneous forwards.</summary>
        public const int LinearLogDiscount = 1;

        /// <summary>Cubic Hermite on zero rates.</summary>
        public const int HermiteZeroRate = 2;

        /// <summary>Cubic Hermite on log discount factors.</summary>
        public const int HermiteLogDiscount = 3;

        /// <summary>The log discount factor <c>ln P(0, t)</c> at <paramref name="t"/> &gt;= 0.</summary>
        public static double LogDiscountFactor(int mode, double[] times, double[] values, double[] slopes, int n,
            double lastLogDiscount, double lastForward, double t)
        {
            double last = times[n - 1];
            if (t > last)
                return lastLogDiscount - lastForward * (t - last);
            if ((mode & 1) != 0)
                return Interpolate(mode, times, values, slopes, n, t);
            double z = t <= times[0] ? values[0] : Interpolate(mode, times, values, slopes, n, t);
            return -z * t;
        }

        /// <summary>
        /// The instantaneous forward <c>f(t) = -d ln P(0, t) / dt</c> at <paramref name="t"/> &gt;= 0: the right derivative
        /// at an interior node, the left one at the last node, <paramref name="lastForward"/> beyond it.
        /// </summary>
        public static double InstantaneousForward(int mode, double[] times, double[] values, double[] slopes, int n,
            double lastForward, double t)
        {
            if (t > times[n - 1])
                return lastForward;
            if ((mode & 1) != 0)
                return -Derivative(mode, times, values, slopes, n, t);
            // -ln P = z(t) t, so f = z + t z'.
            if (t < times[0])
                return values[0];
            return Interpolate(mode, times, values, slopes, n, t) + t * Derivative(mode, times, values, slopes, n, t);
        }

        private static double Interpolate(int mode, double[] times, double[] values, double[] slopes, int n, double t)
        {
            return (mode & 2) == 0
                ? InterpolationKernels.LinearValue(times, values, n, t)
                : InterpolationKernels.HermiteValue(times, values, slopes, n, t);
        }

        private static double Derivative(int mode, double[] times, double[] values, double[] slopes, int n, double t)
        {
            return (mode & 2) == 0
                ? InterpolationKernels.LinearDerivative(times, values, n, t)
                : InterpolationKernels.HermiteDerivative(times, values, slopes, n, t);
        }
    }
}
