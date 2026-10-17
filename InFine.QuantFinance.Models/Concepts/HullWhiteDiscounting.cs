using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Models.Diffusion;
using System;

namespace InFine.QuantFinance.Models.Concepts
{
    /// <summary>
    /// Stochastic discounting of a Hull-White simulation (<see cref="HullWhiteModel{Brownian}"/>): one discount factor per
    /// path, so that a payoff written against <see cref="IDiscounting"/> prices under stochastic rates.
    /// <list type="bullet">
    /// <item>At pillar <c>k</c>: the pathwise <c>D(0, t_k) = exp(-int_0^{t_k} r ds)</c>, exact (no discretisation error).</item>
    /// <item>At any other date <c>t</c>: <c>D(0, t_k) P(t_k, t; x(t_k))</c> with <c>t_k</c> the last pillar at or before
    /// <c>t</c>, i.e. the expectation of <c>D(0, t)</c> given the path up to <c>t_k</c>. For a cash flow known at <c>t_k</c> and
    /// paid at <c>t</c> this gives the exact price; put the payment dates on the grid when the amount depends on later
    /// fixings.</item>
    /// </list>
    /// </summary>
    public class HullWhiteDiscounting : IDiscounting
    {
        CompiledHullWhite _model;
        ArrayDoubleSpot _state;
        ArrayDoubleSpot _logDiscount;

        /// <summary>Reads the paths written by <see cref="HullWhiteModel{Brownian}"/> with the same model and storages.</summary>
        public HullWhiteDiscounting(CompiledHullWhite model, ArrayDoubleSpot state, ArrayDoubleSpot logDiscount)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (logDiscount == null) throw new ArgumentNullException(nameof(logDiscount));
            _model = model;
            _state = state;
            _logDiscount = logDiscount;
        }

        /// <summary>Pathwise discount factor at pillar <paramref name="timeIndex"/>.</summary>
        [Kernel] public double this[int simId, int timeIndex] { get { return Math.Exp(_logDiscount[simId, timeIndex]); } }

        /// <summary>Discount factor to a date in years, conditional on the last pillar at or before it.</summary>
        [Kernel]
        public double this[int simId, double dateInYears]
        {
            get
            {
                int k = _model.PillarAtOrBefore(dateInYears);
                return Math.Exp(_logDiscount[simId, k] + _model.LogZeroBond(k, _state[simId, k], dateInYears));
            }
        }

        /// <summary>Pathwise discount factor at pillar <paramref name="timeIndex"/>.</summary>
        [Kernel] public double this[alignedindex simId, int timeIndex] { get { return Math.Exp(_logDiscount[simId, timeIndex]); } }

        /// <summary>Discount factor to a date in years, conditional on the last pillar at or before it.</summary>
        [Kernel]
        public double this[alignedindex simId, double dateInYears]
        {
            get
            {
                int k = _model.PillarAtOrBefore(dateInYears);
                return Math.Exp(_logDiscount[simId, k] + _model.LogZeroBond(k, _state[simId, k], dateInYears));
            }
        }
    }

    /// <summary>
    /// Stochastic short rate of a Hull-White simulation (<see cref="HullWhiteModel{Brownian}"/>), per path.
    /// <list type="bullet">
    /// <item>On step <c>k</c>: the average short rate <c>(int_{t_k}^{t_{k+1}} r ds) / dt_k = ln(D(0, t_k) / D(0, t_{k+1})) / dt_k</c>,
    /// the stochastic analogue of <see cref="CurveRate"/>: a log-Euler equity diffusion with this drift and Brownians
    /// independent of the rate ones has <c>D(0, t_k) S_k</c> exactly a martingale on the grid.</item>
    /// <item>At a date in years: <c>E[r(t) | x(t_k)] = x(t_k) e^{-a (t - t_k)} + phi(t)</c> with <c>t_k</c> the last pillar at or
    /// before <c>t</c>; on a pillar it is the simulated short rate <c>x(t_k) + phi(t_k)</c>.</item>
    /// </list>
    /// </summary>
    public class HullWhiteRate : IRate
    {
        CompiledHullWhite _model;
        ArrayDoubleSpot _state;
        ArrayDoubleSpot _logDiscount;

        /// <summary>Reads the paths written by <see cref="HullWhiteModel{Brownian}"/> with the same model and storages.</summary>
        public HullWhiteRate(CompiledHullWhite model, ArrayDoubleSpot state, ArrayDoubleSpot logDiscount)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (logDiscount == null) throw new ArgumentNullException(nameof(logDiscount));
            _model = model;
            _state = state;
            _logDiscount = logDiscount;
        }

        /// <summary>Average short rate over the step starting at pillar <paramref name="timeIndex"/>.</summary>
        [Kernel]
        public double this[int simId, int timeIndex]
        {
            get { return (_logDiscount[simId, timeIndex] - _logDiscount[simId, timeIndex + 1]) / _model.StepLength(timeIndex); }
        }

        /// <summary>Expected short rate at a date in years given the last pillar at or before it.</summary>
        [Kernel]
        public double this[int simId, double dateInYears]
        {
            get
            {
                int k = _model.PillarAtOrBefore(dateInYears);
                return _model.ConditionalShortRate(k, _state[simId, k], dateInYears);
            }
        }

        /// <summary>Average short rate over the step starting at pillar <paramref name="timeIndex"/>.</summary>
        [Kernel]
        public double this[alignedindex simId, int timeIndex]
        {
            get { return (_logDiscount[simId, timeIndex] - _logDiscount[simId, timeIndex + 1]) / _model.StepLength(timeIndex); }
        }

        /// <summary>Expected short rate at a date in years given the last pillar at or before it.</summary>
        [Kernel]
        public double this[alignedindex simId, double dateInYears]
        {
            get
            {
                int k = _model.PillarAtOrBefore(dateInYears);
                return _model.ConditionalShortRate(k, _state[simId, k], dateInYears);
            }
        }
    }
}
