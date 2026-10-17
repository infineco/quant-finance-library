using System;
using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Numerics;
using InFine.QuantFinance.TestSupport;
using Xunit;

namespace InFine.QuantFinance.Models.Tests
{
    /// <summary>
    /// <see cref="CurveDiscounting"/> and <see cref="CurveRate"/> read a host-compiled curve. References: on a flat curve,
    /// the existing <see cref="ConstantRateDiscounting"/> / <see cref="ConstantRate"/> (closed form exp(-r t), a few ulps);
    /// on a shaped curve, the host evaluation of the same <see cref="CompiledDiscountCurve"/>, which must match exactly
    /// since both run <see cref="DiscountCurveKernels"/> on the same arrays.
    /// </summary>
    public class UnitTest_CurveDiscounting
    {
        static readonly double[] ScheduleDates = { 0.0, 0.1, 0.25, 0.5, 1.0, 1.5, 2.0, 3.5, 5.0 };
        static readonly double[] Probes = { 0.0, 1e-4, 0.05, 0.3, 0.75, 1.0, 2.2, 4.0, 6.5, 12.0 };

        static CompiledDiscountCurve FlatCurve(double r)
        {
            return new CompiledDiscountCurve(DiscountCurveKernels.LinearLogDiscount, new[] { 0.0, 1.0 }, new[] { 0.0, -r }, default, -r, r);
        }

        static CompiledDiscountCurve ShapedCurve()
        {
            // Humped zero curve under the monotone cubic, with a flat forward of 2.4% beyond 4Y.
            double[] t = { 0.25, 0.5, 1.0, 2.0, 4.0 };
            double[] z = { 0.029, 0.027, 0.0245, 0.023, 0.0232 };
            var slopes = new double[t.Length];
            InterpolationKernels.MonotoneCubicSlopes(t, z, t.Length, slopes);
            return new CompiledDiscountCurve(DiscountCurveKernels.HermiteZeroRate, t, z, slopes, -z[4] * t[4], 0.024);
        }

        [HybridizerFact]
        public void FlatCurveMatchesTheConstantRateConcepts()
        {
            const double r = 0.0315;
            var schedule = new PrecalculatedSchedule(ScheduleDates);
            var discounting = new CurveDiscounting(FlatCurve(r), schedule);
            var reference = new ConstantRateDiscounting(r, schedule);
            var rate = new CurveRate(FlatCurve(r), schedule);
            alignedindex sim = 0;
            for (int k = 0; k <= schedule.PilarCount; ++k)
                Assert.Equal(reference[0, k], discounting[0, k], 1e-15);
            for (int k = 0; k < schedule.PilarCount; ++k)
            {
                Assert.Equal(r, rate[0, k], 1e-13);
                Assert.Equal(r, rate[sim, k], 1e-13);
            }
            foreach (double t in Probes)
            {
                Assert.Equal(reference[0, t], discounting[0, t], 1e-15);
                Assert.Equal(reference[sim, t], discounting[sim, t], 1e-15);
                Assert.Equal(r, rate[0, t], 1e-13);
            }
        }

        [HybridizerFact]
        public void ShapedCurveIsReadExactlyAsOnTheHost()
        {
            var curve = ShapedCurve();
            var schedule = new PrecalculatedSchedule(ScheduleDates);
            var discounting = new CurveDiscounting(curve, schedule);
            var rate = new CurveRate(curve, schedule);
            double[] stepForwards = curve.StepForwards(ScheduleDates);
            alignedindex sim = 7;
            for (int k = 0; k <= schedule.PilarCount; ++k)
            {
                Assert.Equal(curve.DiscountFactor(ScheduleDates[k]), discounting[3, k]);
                Assert.Equal(curve.DiscountFactor(ScheduleDates[k]), discounting[sim, k]);
            }
            for (int k = 0; k < schedule.PilarCount; ++k)
            {
                Assert.Equal(stepForwards[k], rate[3, k]);
                Assert.Equal(stepForwards[k], rate[sim, k]);
                // Martingale step: exp(r_k dt_k) = P(t_k) / P(t_{k+1}).
                Assert.Equal(discounting[0, k] / discounting[0, k + 1], Math.Exp(rate[0, k] * schedule.getDT(k)), 1e-14);
            }
            foreach (double t in Probes)
            {
                Assert.Equal(curve.DiscountFactor(t), discounting[3, t]);
                Assert.Equal(curve.DiscountFactor(t), discounting[sim, t]);
                Assert.Equal(curve.InstantaneousForward(t), rate[3, t]);
                Assert.Equal(curve.InstantaneousForward(t), rate[sim, t]);
            }
            Assert.Equal(0.024, rate[0, 12.0]);
        }

        [HybridizerFact]
        public void RejectsMissingArguments()
        {
            var schedule = new PrecalculatedSchedule(ScheduleDates);
            Assert.Throws<ArgumentNullException>(() => new CurveDiscounting(null, schedule));
            Assert.Throws<ArgumentNullException>(() => new CurveRate(ShapedCurve(), null));
        }
    }
}
