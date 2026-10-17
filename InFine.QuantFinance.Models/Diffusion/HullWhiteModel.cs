using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Scheduling;

namespace InFine.QuantFinance.Models.Diffusion
{
    /// <summary>
    /// Monte Carlo simulation of the one-factor Hull-White short rate fitted to a curve,
    /// <c>dr = (theta(t) - a r) dt + sigma dW</c>, on the grid of a <see cref="CompiledHullWhite"/>.
    /// <para>
    /// The state <c>x = r - phi</c> and the integral <c>I = int_0^t x ds</c> are drawn jointly and exactly
    /// (<see cref="HullWhiteStep"/>) from two independent standard normal arrays; the kernel stores per path and pillar the
    /// state <c>x(t_k)</c> and the log stochastic discount factor <c>ln D(0, t_k) = ln P(0, t_k) - V(t_k) / 2 - I(t_k)</c>.
    /// <see cref="HullWhiteDiscounting"/> and <see cref="HullWhiteRate"/> read them, so that any payoff written against
    /// <see cref="IDiscounting"/> or any diffusion written against <see cref="IRate"/> runs on stochastic rates.
    /// </para>
    /// Usage:
    /// <code>
    /// var model = new CompiledHullWhite(a, sigma, compiledCurve, schedule);
    /// var state = new ArrayDoubleSpot(paths, steps + 1);
    /// var logDiscount = new ArrayDoubleSpot(paths, steps + 1);
    /// // fill z1, z2 (paths x steps) with independent N(0, 1), e.g. Philox streams
    /// new HullWhiteModel&lt;ArrayBrownian&gt;(model, state, logDiscount, z1, z2).Simulate(0, paths);
    /// var discounting = new HullWhiteDiscounting(model, state, logDiscount);
    /// </code>
    /// </summary>
    /// <typeparam name="Brownian">Source of the standard normal draws, <c>[simId, step]</c>.</typeparam>
    [HybridRegisterTemplate(Specialize = typeof(HullWhiteModel<ArrayBrownian>))]
    [HybridRegisterTemplate(Specialize = typeof(EquityDiffusion<ConstantVolatility, HullWhiteRate, ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule>))]
    public class HullWhiteModel<Brownian> : ISimulatable
        where Brownian : IBrownian
    {
        CompiledHullWhite _model;
        ArrayDoubleSpot _state;
        ArrayDoubleSpot _logDiscount;
        Brownian _z1;
        Brownian _z2;

        /// <summary>Constructor.</summary>
        /// <param name="model">The compiled model and grid.</param>
        /// <param name="state">Written: <c>x(t_k)</c>, <c>[simId, 0 .. StepCount]</c>.</param>
        /// <param name="logDiscount">Written: <c>ln D(0, t_k)</c>, <c>[simId, 0 .. StepCount]</c>.</param>
        /// <param name="z1">Standard normals driving the state, <c>[simId, 0 .. StepCount - 1]</c>.</param>
        /// <param name="z2">Standard normals independent from <paramref name="z1"/>, same shape.</param>
        public HullWhiteModel(CompiledHullWhite model, ArrayDoubleSpot state, ArrayDoubleSpot logDiscount, Brownian z1, Brownian z2)
        {
            _model = model;
            _state = state;
            _logDiscount = logDiscount;
            _z1 = z1;
            _z2 = z2;
        }

        /// <summary>Simulates the paths [simFrom, simTo).</summary>
        [Kernel]
        public void Simulate(int simFrom, int simTo)
        {
            int steps = _model.StepCount;
            for (alignedindex simId = VectorUnit.ID + simFrom; simId < simTo; simId += VectorUnit.Count)
            {
                double x = 0.0;
                double integral = 0.0;
                _state[simId, 0] = 0.0;
                _logDiscount[simId, 0] = _model.LogDrift(0);
                for (int k = 0; k < steps; ++k)
                {
                    double z1 = _z1[simId, k];
                    double z2 = _z2[simId, k];
                    integral += _model.IntegralIncrement(k, x, z1, z2);
                    x = _model.NextState(k, x, z1);
                    _state[simId, k + 1] = x;
                    _logDiscount[simId, k + 1] = _model.LogDrift(k + 1) - integral;
                }
            }
        }
    }
}
