using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Models.Concepts
{
    /// <summary>
    /// Discounting concept.
    /// </summary>
    [HybridTemplateConcept]
    public interface IDiscounting
    {
        /// <summary>
        /// Calulate discounting for a given simulation index at a given time index in the current schedule.
        /// </summary>
        /// <param name="simId">Simulation index for stochastic rates.</param>
        /// <param name="timeIndex">Date index in the current schedule.</param>
        /// <returns>The discounting.</returns>
        [Kernel] double this[int simId, int timeIndex] { [ReturnTypeInference(Index = 1, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; }
        /// <summary>
        /// Calulate discounting for a given simulation index at a given future date.
        /// </summary>
        /// <param name="simId">Simulation index for stochastic rates.</param>
        /// <param name="dateInYears">Date in years for the cash flow to be discounted.</param>
        /// <returns>The discounting.</returns>
        [Kernel] double this[int simId, double dateInYears] { [ReturnTypeInference(Index = 1, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; }
        /// <summary>
        /// Calulate discounting for a given simulation index at a given time index in the current schedule.
        /// </summary>
        /// <param name="simId">Simulation index for stochastic rates.</param>
        /// <param name="timeIndex">Date index in the current schedule.</param>
        /// <returns>The discounting.</returns>
        [Kernel] double this[alignedindex simId, int timeIndex] { [ReturnTypeInference(Index = 1, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; }
        /// <summary>
        /// Calulate discounting for a given simulation index at a given future date.
        /// </summary>
        /// <param name="simId">Simulation index for stochastic rates.</param>
        /// <param name="dateInYears">Date in years for the cash flow to be discounted.</param>
        /// <returns>The discounting.</returns>
        [Kernel] double this[alignedindex simId, double dateInYears] { [ReturnTypeInference(Index = 1, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; }
    }

    /// <summary>
    /// 
    /// The constant rate discounting is the discounting induced by a non stochastic rate
    /// which does not change in time.
    /// Its value is $$ DF(sim,T) = \exp{-r T} $$ where $r$ is the \verb!yearlyRate! parameter 
    /// provided as input in the constructor.
    /// 
    /// </summary>
    public class ConstantRateDiscounting : IDiscounting
    {
        double _yearlyRate;
        double[] _discounting;

        /// <summary>
        /// Discounting values are precalculated at the schedules pillar.
        /// </summary>
        /// <param name="yearlyRate">the yearly rate, such as $0.04$ for a $ 4 $ percent rate.</param>
        /// <param name="schedule">the schedule on which this discounting may be applied.</param>
        public ConstantRateDiscounting(double yearlyRate, ISchedule schedule)
        {
            _yearlyRate = yearlyRate ;
            // precalculate discountings.
            _discounting = new double[schedule.PilarCount+1];
            for (int k = 0; k < schedule.PilarCount+1; ++k)
            {
                _discounting[k] = Math.Exp(-yearlyRate * schedule.getT(k));
            }
        }

        /// <summary>
        /// Calulate discounting for a given simulation index at a given time index in the current schedule.
        /// </summary>
        /// <param name="simId">Simulation index for stochastic rates.</param>
        /// <param name="timeIndex">Date index in the current schedule.</param>
        /// <returns>The discounting.</returns>
        [Kernel] public double this[int simId, int timeIndex] { get { return _discounting[timeIndex]; } }

        /// <summary>
        /// Calulate discounting for a given simulation index at a given future date.
        /// </summary>
        /// <param name="simId">Simulation index for stochastic rates.</param>
        /// <param name="dateInYears">Date in years for the cash flow to be discounted.</param>
        /// <returns>The discounting.</returns>
        [Kernel] public double this[int simId, double dateInYears] { get { return Math.Exp(-_yearlyRate * dateInYears); } }

        /// <summary>
        /// Calulate discounting for a given simulation index at a given time index in the current schedule.
        /// </summary>
        /// <param name="simId">Simulation index for stochastic rates.</param>
        /// <param name="timeIndex">Date index in the current schedule.</param>
        /// <returns>The discounting.</returns>
        [Kernel] public double this[alignedindex simId, int timeIndex] { get { return _discounting[timeIndex]; } }

        /// <summary>
        /// Calulate discounting for a given simulation index at a given future date.
        /// </summary>
        /// <param name="simId">Simulation index for stochastic rates.</param>
        /// <param name="dateInYears">Date in years for the cash flow to be discounted.</param>
        /// <returns>The discounting.</returns>
        [Kernel] public double this[alignedindex simId, double dateInYears] { get { return Math.Exp(-_yearlyRate * dateInYears); } }
    }

    /// <summary>
    /// 
    /// Linear rate discounting is the discounting induced by a non stochastic rate
    /// which is interpolated linearly between predefined pilar dates.
    /// 
    /// </summary>
    public class LinearRateDiscounting : IDiscounting
    {
        int _pilarCount;
        double[] _pilarDates;
        double[] _pilarYearlyRates;

        // precalculated
        double[] _discounting;

        ISchedule _schedule;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="pilarDates">The pilar dates for linear interpolation of the rate</param>
        /// <param name="pilarYearlyRates">The yearly rate at each pilar date</param>
        /// <param name="schedule">Schedule for that discounting</param>
        public LinearRateDiscounting(double[] pilarDates, double[] pilarYearlyRates, ISchedule schedule)
        {
            _pilarCount = pilarDates.Length;
            _pilarDates = pilarDates;
            _pilarYearlyRates = pilarYearlyRates;

            _schedule = schedule;

            // precalculate discountings.
            _discounting = new double[schedule.PilarCount + 1];
            for (int k = 0; k < schedule.PilarCount + 1; ++k)
            {
                _discounting[k] = Math.Exp(-InterpolateRate(schedule.getT(k)) * schedule.getT(k));
            }
        }

        [Kernel]
        private double InterpolateRate(double date)
        {
            return LinearInterpolation.Interpolate(_pilarDates, _pilarYearlyRates, _pilarCount, date);
        }

        #region IDiscounting

        /// <summary>
        /// Calulate discounting for a given simulation index at a given time index in the current schedule.
        /// </summary>
        /// <param name="simId"></param>
        /// <param name="timeIndex"></param>
        /// <returns></returns>
        [Kernel] public double this[int simId, int timeIndex]
        {
            get { return _discounting[timeIndex]; }
        }

        /// <summary>
        /// Interpolates the discounting (with linear rate interpolation)
        /// </summary>
        /// <param name="simId"></param>
        /// <param name="dateInYears"></param>
        /// <returns></returns>
        [Kernel] public double this[int simId, double dateInYears]
        {
            get { return Math.Exp(- InterpolateRate(dateInYears) * dateInYears); }
        }

        /// <summary>
        /// Calulate discounting for a given simulation index at a given time index in the current schedule.
        /// </summary>
        /// <param name="simId"></param>
        /// <param name="timeIndex"></param>
        /// <returns></returns>
        [Kernel] public double this[alignedindex simId, int timeIndex]
        {
            get { return _discounting[timeIndex]; }
        }

        /// <summary>
        /// Interpolates the discounting (with linear rate interpolation)
        /// </summary>
        /// <param name="simId"></param>
        /// <param name="dateInYears"></param>
        /// <returns></returns>
        [Kernel] public double this[alignedindex simId, double dateInYears]
        {
            get { return Math.Exp(- InterpolateRate(dateInYears) * dateInYears); }
        }

        #endregion
    }
}
