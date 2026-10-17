using InFine.QuantFinance.Scheduling;
using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Models.Diffusion
{
    /// <summary>
    /// Variance part of the Heston model:
    /// 
    /// $$
    /// dS_t / S_t = r dt + \sqrt{v_t} dW^S_t, \qquad dv_t = \kappa ( \theta - v_t ) dt + \xi \sqrt{v_t} dW^v_t, \qquad d\langle W^S, W^v \rangle_t = \rho dt
    /// $$
    /// <see href="https://dx.doi.org/10.1093%2Frfs%2F6.2.327"/>
    /// <see href="https://en.wikipedia.org/wiki/Heston_model"/>
    /// 
    /// The variance is diffused with the full-truncation Euler scheme (<see cref="HestonStep.FullTruncationEuler"/>),
    /// driven by $Z_v = \rho Z_S + \sqrt{1 - \rho^2} Z_\perp$ where $Z_S$ is the spot Brownian array and $Z_\perp$ an
    /// independent one; both hold standard normal draws N(0,1).
    /// 
    /// The spot is diffused afterwards by <see cref="EquityDiffusion{Volatility, Rate, LogSpot, Brownian, Schedule}"/>
    /// given the <b>same</b> volatility storage and the <b>same</b> spot Brownian array: its IVolatility view returns
    /// $\sqrt{v_t^+}$, so that diffusion is the log-Euler step <see cref="HestonStep.LogSpotEuler"/>.
    /// Usage:
    /// <code>
    /// volatility[simId, 0] = v0;                       // for every simId, raw variance at t0
    /// heston.Simulate(simFrom, simTo);                  // fills volatility[simId, 1..PilarCount]
    /// equity.Diffusion(simFrom, simTo, 0, schedule.PilarCount, volatility, rate, logSpot, spotBrownian, schedule);
    /// </code>
    /// </summary>
    /// <typeparam name="Volatility">Variance storage: written with raw (possibly negative) variances</typeparam>
    /// <typeparam name="Rate">Risk-free rate (unused by the variance diffusion; kept for template registration)</typeparam>
    /// <typeparam name="Brownian">Brownian source for Stochastic process diffusion</typeparam>
    /// <typeparam name="Schedule">Simulation schedule</typeparam>
    [HybridRegisterTemplate(Specialize = typeof(HestonVolatilityModel<Concepts.ArrayStochasticVolatility, Concepts.ConstantRate, Concepts.ArrayBrownian, Concepts.PrecalculatedSchedule>))]
    [HybridRegisterTemplate(Specialize = typeof(EquityDiffusion<Concepts.ArrayStochasticVolatility, Concepts.ConstantRate, Concepts.ArrayDoubleSpot, Concepts.ArrayBrownian, Concepts.PrecalculatedSchedule>))]
    public class HestonVolatilityModel <Volatility, Rate, Brownian, Schedule> : ISimulatable
        where Volatility : Concepts.IStochasticVolatility
        where Rate : Concepts.IRate
        where Brownian : Concepts.IBrownian
        where Schedule : Concepts.ISchedule
    {
        Volatility _vol;
        
        Brownian _spotBrownian ;

        Brownian _independentBrownian ;

        Schedule _sched;

        // model parameters
        double _xi, _kappa, _theta, _rho;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="vol">The variance storage; column 0 must hold the initial variance $v_0$ of each simulation</param>
        /// <param name="spotBrownian">Standard normal draws $Z_S$ driving the spot (pass the same array to the spot diffusion)</param>
        /// <param name="independentBrownian">Standard normal draws $Z_\perp$, independent from <paramref name="spotBrownian"/></param>
        /// <param name="sched">The scheduler</param>
        /// <param name="xi">$\xi$ volatility of variance</param>
        /// <param name="kappa">$\kappa$ mean-reversion speed</param>
        /// <param name="theta">$\theta$ long-term variance</param>
        /// <param name="rho">$\rho$ spot/variance correlation, in [-1, 1]</param>
        public HestonVolatilityModel(Volatility vol, Brownian spotBrownian, Brownian independentBrownian, Schedule sched, double xi, double kappa, double theta, double rho)
        {
            _vol = vol ;
            _spotBrownian = spotBrownian ;
            _independentBrownian = independentBrownian ;
            _sched = sched ;
            _xi = xi ;
            _kappa = kappa ;
            _theta = theta ;
            _rho = rho ;
        }

        /// <summary>
        /// Perform Heston model diffusion of the variance using brownians, in the provided range of simulations.
        /// </summary>
        /// <param name="simFrom">First simulation (inclusive)</param>
        /// <param name="simTo">Last simulation (exclusive)</param>
        [Kernel]
        public void Simulate(int simFrom, int simTo)
        {
            for (alignedindex simId = VectorUnit.ID + simFrom; simId < simTo; simId += VectorUnit.Count)
            {
                double vt = _vol[simId, 0];
                for (int timeIndex = 0; timeIndex < _sched.PilarCount; ++timeIndex)
                {
                    double zv = HestonStep.CorrelatedDriver(_spotBrownian[simId, timeIndex], _independentBrownian[simId, timeIndex], _rho);
                    vt = HestonStep.FullTruncationEuler(vt, _kappa, _theta, _xi, _sched.getDT(timeIndex), _sched.getSqrtDT(timeIndex), zv);
                    _vol[simId, timeIndex + 1] = vt;
                }
            }
        }
    }
}
