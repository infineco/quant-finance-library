using System;
using System.Text;
using System.Threading;
using InFine.QuantFinance.Scheduling;
using Hybridizer.Runtime.CUDAImports;

namespace InFine.QuantFinance.LongstaffSchwartz
{
    public interface IRegressor<T, Data>
        where T : struct
        where Data : struct
    {
        void Init(int simulationCound);
        void Add(int simId, T x, double y, double count);
        void Solve();
        void SetData(Data data);
        double Evaluate(T observable);
    }

    /// <summary>
    /// Least-squares fit of y on (1, x, x^2) accumulated path by path, then solved by Cramer's rule on the 3x3 normal
    /// equations.
    /// </summary>
    /// <remarks>
    /// The sums are held in scalar fields rather than <c>fixed</c> buffers: Hybridizer 7.5 translates a <c>fixed</c>
    /// statement into pinned locals it never declares (nvrtc: identifier "a1" is undefined), and a fixed buffer is
    /// indexed through a pointer, which sends it to local memory on a GPU. The arithmetic is the same, in the same order,
    /// as with the former buffers, so host results are unchanged to the bit.
    /// </remarks>
    public struct QuadraticUnivariateRegressorStruct : IRegressor<double, QuadraticUnivariateRegressorStruct.RegressorData>
    {
        /// <summary>The five distinct entries of the symmetric matrix t(A) A: sums of x^0 .. x^4.</summary>
        public struct RegressorArray
        {
            public double a0, a1, a2, a3, a4;

            public void Add(RegressorArray b)
            {
                a0 += b.a0;
                a1 += b.a1;
                a2 += b.a2;
                a3 += b.a3;
                a4 += b.a4;
            }

            public override string ToString()
            {
                var sb = new StringBuilder();
                sb.Append(a0).Append(", ").Append(a1).Append(", ").Append(a2).Append(", ").Append(a3).Append(", ").Append(a4).Append(", ");
                return sb.ToString();
            }
        }

        public struct RegressorData
        {
            RegressorArray tAA;
            // t(A) b: sums of y, y x, y x^2.
            double tAb0, tAb1, tAb2;

            [Kernel]
            public void Init(int simulationCount)
            {
                tAA.a0 = 0.0;
                tAA.a1 = 0.0;
                tAA.a2 = 0.0;
                tAA.a3 = 0.0;
                tAA.a4 = 0.0;
                tAb0 = 0.0;
                tAb1 = 0.0;
                tAb2 = 0.0;
            }

            [Kernel]
            public void Add(int simId, double x, double y, double count)
            {
                tAA.a0 += Tools.reduce_add(count);
                tAA.a1 += Tools.reduce_add(x);
                tAA.a2 += Tools.reduce_add(x * x);
                tAA.a3 += Tools.reduce_add(x * x * x);
                tAA.a4 += Tools.reduce_add(x * x * x * x);
                tAb0 += Tools.reduce_add(y);
                tAb1 += Tools.reduce_add(y * x);
                tAb2 += Tools.reduce_add(y * x * x);
            }

            /// <summary>Coefficients (r0, r1, r2) of r0 + r1 x + r2 x^2.</summary>
            [Kernel]
            public void Solve(out double r0, out double r1, out double r2)
            {
                double det =
                    + tAA.a0 * (tAA.a2 * tAA.a4 - tAA.a3 * tAA.a3)
                    - tAA.a1 * (tAA.a1 * tAA.a4 - tAA.a3 * tAA.a2)
                    + tAA.a2 * (tAA.a1 * tAA.a3 - tAA.a2 * tAA.a2);
                det = 1.0 / det;

                // Upper triangle of the symmetric inverse of t(A) A.
                double i0 = det * (tAA.a2 * tAA.a4 - tAA.a3 * tAA.a3);
                double i1 = det * (tAA.a2 * tAA.a3 - tAA.a1 * tAA.a4);
                double i2 = det * (tAA.a1 * tAA.a3 - tAA.a2 * tAA.a2);
                double i3 = det * (tAA.a0 * tAA.a4 - tAA.a2 * tAA.a2);
                double i4 = det * (tAA.a2 * tAA.a1 - tAA.a0 * tAA.a3);
                double i5 = det * (tAA.a0 * tAA.a2 - tAA.a1 * tAA.a1);

                r0 = i0 * tAb0 + i1 * tAb1 + i2 * tAb2;
                r1 = i1 * tAb0 + i3 * tAb1 + i4 * tAb2;
                r2 = i2 * tAb0 + i4 * tAb1 + i5 * tAb2;
            }

            [Kernel]
            public void Add(ref RegressorData b)
            {
                tAA.Add(b.tAA);
                tAb0 += b.tAb0;
                tAb1 += b.tAb1;
                tAb2 += b.tAb2;
            }

            [Kernel, HybridizerIgnore("AVX,OMP,VSX")]
            public void Reduce_cuda()
            {
            }

            public override string ToString()
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("\ttAA:").AppendLine(tAA.ToString());
                sb.AppendFormat("\ttAb: {0}, {1}, {2}", tAb0, tAb1, tAb2);
                return sb.ToString();
            }

            [Kernel]
            [HybridizerIgnore("OMP,AVX,VSX")]
            public RegressorData ReduceWarp()
            {
                tAA.a0 = Tools.reduce_warp_add_double(tAA.a0);
                tAA.a1 = Tools.reduce_warp_add_double(tAA.a1);
                tAA.a2 = Tools.reduce_warp_add_double(tAA.a2);
                tAA.a3 = Tools.reduce_warp_add_double(tAA.a3);
                tAA.a4 = Tools.reduce_warp_add_double(tAA.a4);
                tAb0 = Tools.reduce_warp_add_double(tAb0);
                tAb1 = Tools.reduce_warp_add_double(tAb1);
                tAb2 = Tools.reduce_warp_add_double(tAb2);
                return this;
            }
        }

        public RegressorData _data;
        // Fitted coefficients: res0 + res1 x + res2 x^2.
        public double res0, res1, res2;

        [Kernel]
        public void Init(int simulationCount)
        {
            _data.Init(simulationCount);
        }

        [Kernel]
        public void Add(int simId, double x, double y, double count)
        {
            _data.Add(simId, x, y, count);
        }

        [Kernel]
        public void Solve()
        {
            _data.Solve(out res0, out res1, out res2);
        }

        [Kernel]
        public void SetData(RegressorData data)
        {
            _data = (RegressorData)data;
        }

        [Kernel]
        public double Evaluate(double observable)
        {
            return res0 + res1 * observable + res2 * observable * observable;
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendFormat("\tRes: {0}, {1}, {2}", res0, res1, res2);
            sb.AppendLine();
            sb.AppendLine(_data.ToString());
            return sb.ToString();
        }
    }

    [HybridizerIgnore("OMP,AVX,VSX")]
    public class QuadraticUnivariateRegressorStructDataReductor : InFine.QuantFinance.Scheduling.Reductor<QuadraticUnivariateRegressorStruct.RegressorData>
    {
        [Kernel]
        public QuadraticUnivariateRegressorStruct.RegressorData ReduceWarp(ref QuadraticUnivariateRegressorStruct.RegressorData input)
        {
            return input.ReduceWarp();
        }
    }
}
