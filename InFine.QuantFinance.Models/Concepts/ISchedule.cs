using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Models.Concepts
{
    /// <summary>
    /// Schedure concept.
    /// 
    /// A schedule is a set of dates that represent the simulation dates for a pricing. 
    /// The Monte Carlo simulations will have values at each date in that schedule, a.k.a. pilar dates.
    /// </summary>
    [HybridTemplateConcept]
    public interface ISchedule
    {
        /// <summary>
        /// Number of dates in that schedule
        /// </summary>
        int PilarCount { get; }
        /// <summary>
        /// Returns the square root of the time-span between schedule date at timeIndex, and the next schedule date.
        /// </summary>
        /// <param name="timeIndex">Index at which time-span is to be taken</param>
        /// <returns></returns>
        double getSqrtDT(int timeIndex) ;
        /// <summary>
        /// Returns the time-span between schedule date at timeIndex, and the next schedule date.
        /// </summary>
        /// <param name="timeIndex">Index at which time-span is to be taken</param>
        /// <returns></returns>
        double getDT(int timeIndex) ;
        /// <summary>
        /// Returns the date, in years from origin (zero at index zero) at the given timeIndex.
        /// </summary>
        /// <param name="timeIndex">Index at which date is to be taken</param>
        /// <returns></returns>
        double getT (int timeIndex) ;
    }

    public class BasicSchedule
    {
        double[] _dates ;

        public struct DTCalculator
        {
            double[] _dates ;

            public DTCalculator(double[] dates)
            {
                _dates = dates;
            }
            public double this[int timeIndex] { get { return _dates[timeIndex + 1] - _dates[timeIndex]; } set { } }
        }

        public struct SqrtDTCalculator
        {
            double[] _dates ;
            public SqrtDTCalculator(double[] dates)
            {
                _dates = dates;
            }
            public double this[int timeIndex] { get { return Math.Sqrt(_dates[timeIndex + 1] - _dates[timeIndex]); } set { } }
        }

        public DTCalculator dt ;
        public SqrtDTCalculator sqrtDt ;

        public BasicSchedule(double[] dates)
        {
            _dates = dates;
            dt = new DTCalculator(_dates);
            sqrtDt = new SqrtDTCalculator(_dates);
        }

        // NEED USE HYBRID ARRAYS [Kernel]
        public int PilarCount { get { return _dates.Length; } }

        [Kernel]
        public double getSqrtDT(int timeIndex) { return sqrtDt[timeIndex]; }
        [Kernel]
        public double getDT(int timeIndex) { return dt[timeIndex]; }
        [Kernel]
        public double getT (int timeIndex) { return _dates[timeIndex] ; }
    }

    public class PrecalculatedSchedule : ISchedule
    {
        double[] _dates;
        double[] _dt;
        double[] _sqrtDt;
        int _pilarCount;

        public PrecalculatedSchedule(double[] dates)
        {
            _dates = dates;
            _pilarCount = _dates.Length - 1;
            _dt = new double[_dates.Length - 1];
            _sqrtDt = new double[_dates.Length - 1];

            for (int k = 0; k < _dates.Length - 1; ++k)
            {
                _dt[k] = _dates[k + 1] - _dates[k];
                _sqrtDt[k] = Math.Sqrt(_dt[k]);
            }
        }

        [Kernel]
        public int PilarCount { get { return _pilarCount; } }

        [Kernel]
        public double getSqrtDT(int timeIndex) { return _sqrtDt[timeIndex]; }
        [Kernel]
        public double getDT(int timeIndex) { return _dt[timeIndex]; }
        [Kernel]
        public double getT(int timeIndex) { return _dates[timeIndex]; }
    }
}
