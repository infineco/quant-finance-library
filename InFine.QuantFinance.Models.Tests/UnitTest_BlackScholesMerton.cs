using System;
using System.Runtime.InteropServices;
using InFine.QuantFinance.TestSupport;
using Xunit;

namespace InFine.QuantFinance.Models.Tests
{
    static class RandomExtensions
    {
        public static void Shuffle<T>(this System.Random rng, T[] array)
        {
            int n = array.Length;
            while (n > 1)
            {
                int k = rng.Next(n--);
                T temp = array[n];
                array[n] = array[k];
                array[k] = temp;
            }
        }
    }


    public class UnitTest_BlackScholesMerton
    {
        [HybridizerFact]
        public unsafe void TestMethod_BlackScholesMertonDiffusion()
        {
            int datesCount = 2;
            int simulationCount = 1000000;
            double S0 = 1.0;

            double[] dates = new double[datesCount];
            for (int k = 0; k < datesCount; ++k)
            {
                dates[k] = (double)k; // / 365.0;
            }

            Models.Concepts.PrecalculatedSchedule sched = new Concepts.PrecalculatedSchedule(dates);

            Models.Concepts.ConstantVolatility vol = new Concepts.ConstantVolatility();
            Models.Concepts.ConstantRate rate = new Concepts.ConstantRate();

            vol.Volatility = 0.30; // 45%
            rate.YearlyRate = 0.04; // 1% ACT

            // prepare storages
            Models.Concepts.ArrayDoubleSpot logSpot = new Concepts.ArrayDoubleSpot(simulationCount, datesCount);
            Models.Concepts.ArrayBrownian brownian = new Concepts.ArrayBrownian(simulationCount, datesCount);

            // init values
            for (int k = 0; k < simulationCount; ++k)
            {
                logSpot[k, 0] = Math.Log(S0);
            }

            double accumcall = 0.0;
            double accumcount = 0.0;

            for (int redo = 0; redo < 1; ++redo)
            {
                // generate gaussians
                // rng.GenerateGaussians(Random.GaussianTransformEnum.BoxMuller2, 0.0, 1.0, simulationCount * datesCount, brownian.Backing);
                // rng.GenerateGaussians(Random.GaussianTransformEnum.InverseCumulativeDistributionFunction, 0.0, 1.0, simulationCount * datesCount, brownian.Backing);
                // BoxMuller(1000, brownian.Backing);
                CumulNormal(simulationCount * datesCount, brownian.Backing);

                // diffuse
                Diffusion.BlackScholesMerton<Models.Concepts.ArrayDoubleSpot, Models.Concepts.ArrayBrownian,
                        Models.Concepts.PrecalculatedSchedule> diffuser = new Diffusion.BlackScholesMerton<Concepts.ArrayDoubleSpot, Concepts.ArrayBrownian, Concepts.PrecalculatedSchedule>();
                diffuser.Diffusion(
                        0, simulationCount - 1,
                        0, datesCount - 1,
                        vol, rate, logSpot, brownian, sched);

                // compute atm call value
                double[] callValue = new double[simulationCount];
                for (int k = 0; k < simulationCount; ++k)
                    callValue[k] = Math.Max(0.0, Math.Exp(logSpot[k, datesCount - 1]) - S0) * Math.Exp(-rate[k, datesCount - 1] * dates[datesCount - 1]);

                double avg = 0.0;
                for (int k = 0; k < simulationCount; ++k)
                {
                    avg += Math.Exp(logSpot[k, datesCount - 1]);
                }

                avg /= (simulationCount);
                Console.WriteLine("average logspot: " + avg);

                double variance = 0.0;
                for (int k = 0; k < simulationCount; ++k)
                {
                    variance += (Math.Exp(logSpot[k, datesCount - 1]) - avg) * (Math.Exp(logSpot[k, datesCount - 1]) - avg);
                }
                variance /= (simulationCount);
                // https://en.wikipedia.org/wiki/Log-normal_distribution
                double mu = rate.YearlyRate - 0.5 * vol.Volatility * vol.Volatility;

                double expectedMean = Math.Exp(mu + 0.5 * vol.Volatility * vol.Volatility);
                Console.WriteLine("mean logspot: {0}, expected {1}, error {2}", avg, expectedMean, Math.Abs(avg / expectedMean - 1.0));
                double expectedVariance = (Math.Exp(vol.Volatility * vol.Volatility) - 1.0) * Math.Exp(2 * mu + vol.Volatility * vol.Volatility);
                Console.WriteLine("variance logspot: {0}, expected {1}, error {2}", variance, expectedVariance, Math.Abs(variance / expectedVariance - 1.0));

                double call = 0.0;
                for (int k = 0; k < simulationCount; ++k)
                {
                    // discount it !
                    call += Math.Max(0.0, Math.Exp(logSpot[k, datesCount - 1]) - S0) * Math.Exp(-rate[k, datesCount - 1] * dates[datesCount - 1]);
                }
                call /= (double)simulationCount;

                accumcall += call;
                accumcount += 1.0;
            }
            // discount it

            double refcall, refput;
            ClosedForm.BlackScholesMertonPrimitive.CallPut(out refcall, out refput, S0, S0, rate.YearlyRate, vol.Volatility, (dates[datesCount - 1]));

            Console.Out.WriteLine("Closed Form = {0} -- Simulated = {1} -- Error = {2}", refcall, accumcall / accumcount, Math.Abs(refcall * accumcount / accumcall - 1.0));
        }

        double myLog(double x)
        {
            return Math.Log(2.0) + Math.Log(0.5 * x);
        }

        double average(double[] input)
        {
            double result = 0.0;
            for (int i = 0; i < input.Length; ++i)
            {
                result += input[i];
            }

            return result / (double)input.Length;
        }

        double variance(double[] input, double average)
        {
            double result = 0.0;
            for (int i = 0; i < input.Length; ++i)
            {
                result += (input[i] - average) * (input[i] - average);
            }

            return result / (double)(input.Length);
        }

        unsafe void CumulNormal(int count, double* target)
        {
            double[] result = new double[count];
            double h = 1.0 / (double)(count);
            double x = h / 2.0;
            double squares = 0.0;
            for (int i = 0; i < count; ++i, x += h)
            {
                result[i] = normcdfinv(x);
                squares += result[i] * result[i];
            }

            double invVol = 1.0; // / Math.Sqrt(squares / (double)count);
            new System.Random().Shuffle(result);

            double accum = 0.0;

            for (int i = 0; i < count; ++i)
            {
                target[i] = result[i] * invVol;
                accum += target[i] * target[i];
            }
            Console.Out.WriteLine("NEW VAR = {0}", accum / (double)count);
        }

        double[] CumulNormal(int count)
        {
            double[] result = new double[count];
            double h = 1.0 / (double)(count);
            double x = h / 2.0;
            double squares = 0.0;
            for (int i = 0; i < count; ++i, x += h)
            {
                result[i] = normcdfinv(x);
                squares += result[i] * result[i];
            }
            double invVol = 1.0 / Math.Sqrt(squares / (double)count);

            for (int i = 0; i < count; ++i, x += h)
            {
                result[i] *= invVol;
            }

            return result;
        }

        double[] BoxMuller(int count)
        {
            double[] result = new double[2 * count * count];
            double h = 1.0 / (double)(count);
            double y, x = h / 2.0;
            int index = 0;
            double n = (double)(count);
            double factor = 1.0; // Math.Sqrt(1.0 / (1.0 - Math.Log(2.0) / (2.0 * n) + 1.0 / (24.0 * n * n)));
            for (int i = 0; i < count; ++i, x += h)
            {
                y = h / 2.0;
                for (int j = 0; j < count; ++j, y += h, index += 2)
                {
                    result[index] = Math.Sqrt(-2.0 * Math.Log(x)) * Math.Cos(2.0 * Math.PI * y) * factor;
                    result[index + 1] = Math.Sqrt(-2.0 * Math.Log(x)) * Math.Sin(2.0 * Math.PI * y) * factor;
                }
            }

            return result;
        }

        unsafe void BoxMuller(int count, double* target)
        {
            double[] result = new double[2 * count * count];
            double h = 1.0 / (double)(count);
            double y, x = h / 2.0;
            int index = 0;
            double n = (double)(count);
            double factor = Math.Sqrt(1.0 / (1.0 - Math.Log(2.0) / (2.0 * n) + 1.0 / (24.0 * n * n)));

            factor = 1.0;
            for (int i = 0; i < count; ++i, x += h)
            {
                y = h / 2.0;
                for (int j = 0; j < count; ++j, y += h, index += 2)
                {
                    result[index] = Math.Sqrt(-2.0 * Math.Log(x)) * Math.Cos(2.0 * Math.PI * y) * factor;
                    result[index + 1] = Math.Sqrt(-2.0 * Math.Log(x)) * Math.Sin(2.0 * Math.PI * y) * factor;
                }
            }

            new System.Random().Shuffle(result);
            for (int i = 0; i < 2 * count * count; ++i)
            {
                target[i] = result[i];
            }
            /*
            double squares = 0.0;
            for (int i = 0; i < 2 * count * count; ++i)
            {
                squares += result[i] * result[i];
            }
            Console.Out.WriteLine("factored VAR = {0}", squares / (double)(count*count*2));

            double invVol = 1.0 / Math.Sqrt(squares / (double)(count * count * 2));
            new System.Random().Shuffle(result);

            double accum = 0.0;

            for (int i = 0; i < (count * count * 2); ++i)
            {
                target[i] = result[i] * invVol;
                accum += target[i] * target[i];
            }
            Console.Out.WriteLine("NEW VAR = {0}", accum / (double)(count * count * 2));*/
            //return result;
        }

        void VerifyGaussian(double[] distrib)
        {
            double esperance = 0.0;
            for (int i = 0; i < distrib.Length; ++i)
            {
                esperance += distrib[i];
            }
            esperance /= (double)distrib.Length;
            Console.WriteLine("esperance: " + esperance);

            double sumSquare = 0.0;
            for (int i = 0; i < distrib.Length; ++i)
            {
                sumSquare += (distrib[i] - esperance) * (distrib[i] - esperance);
            }
            sumSquare /= distrib.Length;

            Console.WriteLine("pre-adjust variance: " + (sumSquare - 1.0));

            double vol = 1.0 / Math.Sqrt(sumSquare);

            for (int i = 0; i < distrib.Length; ++i) { distrib[i] *= vol; }

            sumSquare = 0.0;
            for (int i = 0; i < distrib.Length; ++i)
            {
                sumSquare += (distrib[i] - esperance) * (distrib[i] - esperance);
            }
            sumSquare /= distrib.Length;

            Console.WriteLine("post-adjust variance: " + (sumSquare - 1.0));
        }

        // all code below is nvidia's property - do not commit it in our production code
        double normcdfinv(double a)
        {
            return -1.4142135623730951 * erfcinv(a + a);
        }

        double erfcinv(double a)
        {
            double t;
            DoubleUlongUnion cvt = new DoubleUlongUnion { };

            if (Double.IsNaN(a))
            {
                return a + a;
            }
            if (a <= 0.0)
            {
                cvt.l = 0xfff8000000000000ul;
                t = cvt.d;                        /* INDEFINITE */
                if (a == 0.0)
                {
                    t = (1.0 - a) * Math.Exp(1000.0);  /* Infinity */
                }
            }
            else if (a >= 0.0625)
            {
                t = erfinv(1.0 - a);
            }
            else if (a >= 1e-100)
            {
                /* Based on: J.M. Blair, C.A. Edwards, J.H. Johnson: Rational Chebyshev
                   Approximations for the Inverse of the Error Function. Mathematics of
                   Computation, Vol. 30, No. 136 (Oct. 1976), pp. 827-830. Table 59
                */
                double p, q;
                t = Math.Log(a);
                t = 1.0 / Math.Sqrt(-t);
                p = 2.7834010353747001060e-3;
                p = p * t + 8.6030097526280260580e-1;
                p = p * t + 2.1371214997265515515e+0;
                p = p * t + 3.1598519601132090206e+0;
                p = p * t + 3.5780402569085996758e+0;
                p = p * t + 1.5335297523989890804e+0;
                p = p * t + 3.4839207139657522572e-1;
                p = p * t + 5.3644861147153648366e-2;
                p = p * t + 4.3836709877126095665e-3;
                p = p * t + 1.3858518113496718808e-4;
                p = p * t + 1.1738352509991666680e-6;
                q = t + 2.2859981272422905412e+0;
                q = q * t + 4.3859045256449554654e+0;
                q = q * t + 4.6632960348736635331e+0;
                q = q * t + 3.9846608184671757296e+0;
                q = q * t + 1.6068377709719017609e+0;
                q = q * t + 3.5609087305900265560e-1;
                q = q * t + 5.3963550303200816744e-2;
                q = q * t + 4.3873424022706935023e-3;
                q = q * t + 1.3858762165532246059e-4;
                q = q * t + 1.1738313872397777529e-6;
                t = p / (q * t);
            }
            else
            {
                /* Based on: J.M. Blair, C.A. Edwards, J.H. Johnson: Rational Chebyshev
                   Approximations for the Inverse of the Error Function. Mathematics of
                   Computation, Vol. 30, No. 136 (Oct. 1976), pp. 827-830. Table 82
                */
                double p, q;
                t = Math.Log(a);
                t = 1.0 / Math.Sqrt(-t);
                p = 6.9952990607058154858e-1;
                p = p * t + 1.9507620287580568829e+0;
                p = p * t + 8.2810030904462690216e-1;
                p = p * t + 1.1279046353630280005e-1;
                p = p * t + 6.0537914739162189689e-3;
                p = p * t + 1.3714329569665128933e-4;
                p = p * t + 1.2964481560643197452e-6;
                p = p * t + 4.6156006321345332510e-9;
                p = p * t + 4.5344689563209398450e-12;
                q = t + 1.5771922386662040546e+0;
                q = q * t + 2.1238242087454993542e+0;
                q = q * t + 8.4001814918178042919e-1;
                q = q * t + 1.1311889334355782065e-1;
                q = q * t + 6.0574830550097140404e-3;
                q = q * t + 1.3715891988350205065e-4;
                q = q * t + 1.2964671850944981713e-6;
                q = q * t + 4.6156017600933592558e-9;
                q = q * t + 4.5344687377088206783e-12;
                t = p / (q * t);
            }
            return t;
        }


        // emulation of a C union
        [StructLayout(LayoutKind.Explicit)]
        struct DoubleUlongUnion
        {
            [FieldOffset(0)]
            public double d;
            [FieldOffset(0)]
            public ulong l;
        }


        double erfinv(double a)
        {
            double p, q, t, fa;
            DoubleUlongUnion cvt = new DoubleUlongUnion { };

            fa = Math.Abs(a);
            if (fa >= 1.0)
            {
                cvt.l = 0xfff8000000000000ul;
                t = cvt.d;                    /* INDEFINITE */
                if (fa == 1.0)
                {
                    t = a * Math.Exp(1000.0);        /* Infinity */
                }
            }
            else if (fa >= 0.9375)
            {
                /* Based on: J.M. Blair, C.A. Edwards, J.H. Johnson: Rational Chebyshev
                   Approximations for the Inverse of the Error Function. Mathematics of
                   Computation, Vol. 30, No. 136 (Oct. 1976), pp. 827-830. Table 59
                 */
                t = log1p(-fa);
                t = 1.0 / Math.Sqrt(-t);
                p = 2.7834010353747001060e-3;
                p = p * t + 8.6030097526280260580e-1;
                p = p * t + 2.1371214997265515515e+0;
                p = p * t + 3.1598519601132090206e+0;
                p = p * t + 3.5780402569085996758e+0;
                p = p * t + 1.5335297523989890804e+0;
                p = p * t + 3.4839207139657522572e-1;
                p = p * t + 5.3644861147153648366e-2;
                p = p * t + 4.3836709877126095665e-3;
                p = p * t + 1.3858518113496718808e-4;
                p = p * t + 1.1738352509991666680e-6;
                q = t + 2.2859981272422905412e+0;
                q = q * t + 4.3859045256449554654e+0;
                q = q * t + 4.6632960348736635331e+0;
                q = q * t + 3.9846608184671757296e+0;
                q = q * t + 1.6068377709719017609e+0;
                q = q * t + 3.5609087305900265560e-1;
                q = q * t + 5.3963550303200816744e-2;
                q = q * t + 4.3873424022706935023e-3;
                q = q * t + 1.3858762165532246059e-4;
                q = q * t + 1.1738313872397777529e-6;
                t = p / (q * t);
                if (a < 0.0) t = -t;
            }
            else if (fa >= 0.75)
            {
                /* Based on: J.M. Blair, C.A. Edwards, J.H. Johnson: Rational Chebyshev
                   Approximations for the Inverse of the Error Function. Mathematics of
                   Computation, Vol. 30, No. 136 (Oct. 1976), pp. 827-830. Table 39
                */
                t = a * a - .87890625;
                p = .21489185007307062000e+0;
                p = p * t - .64200071507209448655e+1;
                p = p * t + .29631331505876308123e+2;
                p = p * t - .47644367129787181803e+2;
                p = p * t + .34810057749357500873e+2;
                p = p * t - .12954198980646771502e+2;
                p = p * t + .25349389220714893917e+1;
                p = p * t - .24758242362823355486e+0;
                p = p * t + .94897362808681080020e-2;
                q = t - .12831383833953226499e+2;
                q = q * t + .41409991778428888716e+2;
                q = q * t - .53715373448862143349e+2;
                q = q * t + .33880176779595142685e+2;
                q = q * t - .11315360624238054876e+2;
                q = q * t + .20369295047216351160e+1;
                q = q * t - .18611650627372178511e+0;
                q = q * t + .67544512778850945940e-2;
                p = p / q;
                t = a * p;
            }
            else
            {
                /* Based on: J.M. Blair, C.A. Edwards, J.H. Johnson: Rational Chebyshev
                   Approximations for the Inverse of the Error Function. Mathematics of
                   Computation, Vol. 30, No. 136 (Oct. 1976), pp. 827-830. Table 18
                */
                t = a * a - .5625;
                p = -.23886240104308755900e+2;
                p = p * t + .45560204272689128170e+3;
                p = p * t - .22977467176607144887e+4;
                p = p * t + .46631433533434331287e+4;
                p = p * t - .43799652308386926161e+4;
                p = p * t + .19007153590528134753e+4;
                p = p * t - .30786872642313695280e+3;
                q = t - .83288327901936570000e+2;
                q = q * t + .92741319160935318800e+3;
                q = q * t - .35088976383877264098e+4;
                q = q * t + .59039348134843665626e+4;
                q = q * t - .48481635430048872102e+4;
                q = q * t + .18997769186453057810e+4;
                q = q * t - .28386514725366621129e+3;
                p = p / q;
                t = a * p;
            }
            return t;
        }


        /*  
         * The following is based on: David Goldberg, "What every computer scientist 
         * should know about floating-point arithmetic", ACM Computing Surveys, Volume 
         * 23, Issue 1, March 1991.
         */
        double log1p(double a)
        {
            double u, m;

            u = 1.0 + a;
            if (u == 1.0)
            {
                /* a very close to zero */
                u = a;
            }
            else
            {
                m = u - 1.0;
                u = Math.Log(u);
                if (a < 1.0)
                {
                    /* a somewhat close to zero */
                    u = a * u;
                    u = u / m;
                }
            }
            return u;
        }
    }
}
